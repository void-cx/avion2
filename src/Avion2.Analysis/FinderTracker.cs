using System.Globalization;
using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>A slot of our group-finder room as the UI shows it; an open slot has no character.</summary>
/// <param name="Ready">Readied (the leader readies by starting, so the leader's slot is ready only once inside).</param>
/// <param name="Online">False while the member is between servers.</param>
public sealed record FinderSlotView(
    byte Slot,
    uint CharacterId,
    string Name,
    GameClass Class,
    uint Level,
    uint ItemLevel,
    uint CombatPower,
    FinderRole Role,
    bool Ready,
    bool Online,
    bool IsLeader,
    bool IsSelf)
{
    public bool IsOpen => CharacterId == 0;
}

/// <summary>A player asking to join our room (we lead it), until they join, withdraw or the request runs out.</summary>
/// <param name="ExpiresAtMs">When the request runs out (epoch ms).</param>
public sealed record FinderJoinRequestView(uint CharacterId, string Name, GameClass Class, uint Level, uint ItemLevel,
    uint CombatPower, ushort Server, long ExpiresAtMs);

/// <summary>Our group-finder room: what the game showed in its last room record, kept current by the slot records.</summary>
/// <param name="Requests">Open join requests (only the leader gets them).</param>
public sealed record FinderRoomView(
    uint RoomNumber,
    string Title,
    uint DungeonId,
    FinderRoomState State,
    byte Size,
    IReadOnlyList<FinderSlotView> Slots,
    IReadOnlyList<FinderJoinRequestView> Requests,
    bool SelfIsLeader)
{
    /// <summary>Members seated, us included.</summary>
    public int Filled => Slots.Count(s => !s.IsOpen);

    /// <summary>Every slot is taken and everyone but the leader has readied: the leader can start.</summary>
    public bool FullAndReady => Slots.Count > 0 && Slots.All(s => !s.IsOpen && (s.Ready || s.IsLeader));
}

public enum FinderAlertKind
{
    /// <summary>Someone asks to join our room (<see cref="FinderAlert.Request"/>).</summary>
    JoinRequest,

    /// <summary>Every slot is taken and everyone but the leader is ready.</summary>
    FullAndReady,

    /// <summary>The room's dungeon is starting.</summary>
    Starting,
}

/// <summary>Something worth a notice while the game is in the background.</summary>
public sealed record FinderAlert(FinderAlertKind Kind, uint RoomNumber, FinderJoinRequestView? Request = null);

/// <summary>
/// The group-finder room we sit in (<c>02 97</c>, with joins <c>0B 97</c>, slot updates <c>1F 97</c>, ready checks
/// <c>15 97</c> / <c>16 97</c>, the start <c>18 97</c>, leaving <c>1D 97</c> / <c>1B 97</c>) and the join requests to it
/// (<c>07 97</c>, <c>25 97</c>). <see cref="Alert"/> is raised when someone asks to join, when the room is full and
/// ready, and when its dungeon starts. Not thread-safe (driven by <see cref="GameSession"/>).
/// </summary>
public sealed class FinderTracker
{
    /// <summary>A second request from the same player this soon after the first gets no second notice.</summary>
    public const long RequestNoticeGapMs = 30_000;

    private uint _room;
    private string _title = string.Empty;
    private uint _dungeon;
    private FinderRoomState _state;
    private byte _size;
    private uint _leader;
    private readonly List<FinderSlot> _slots = new();
    private readonly List<FinderJoinRequestEvent> _requests = new();
    private readonly Dictionary<uint, long> _noticed = new();
    private string? _selfName;

    /// <summary>
    /// Our character, once a record tells: our identity record, the room created by us (reason 1: we lead it), or a join
    /// request (only the leader gets those). Our name alone also works, but it comes only on teleports.
    /// </summary>
    private uint _selfId;
    private bool _fullNoticed;
    private bool _startNoticed;
    private long _nowMs;

    /// <summary>Raised inside <see cref="Apply"/> (the capture thread in the app).</summary>
    public event Action<FinderAlert>? Alert;

    /// <summary>True while we sit in a room.</summary>
    public bool InRoom => _room != 0;

    public void Apply(GameEvent e, EntityRegistry registry)
    {
        _nowMs = Math.Max(_nowMs, e.TimestampMs);
        switch (e)
        {
            case FinderRoomEvent room:
                if (room.RoomNumber != _room)
                {
                    _requests.Clear();
                    _fullNoticed = false;
                    // A room first seen already running is not a start we saw
                    _startNoticed = room.State != FinderRoomState.Recruiting;
                }

                if (room.Reason == 1)
                {
                    _selfId = room.LeaderId;
                }

                _room = room.RoomNumber;
                _title = room.Title;
                _dungeon = room.DungeonId;
                _size = room.Size;
                _leader = room.LeaderId;
                _slots.Clear();
                _slots.AddRange(room.Slots.OrderBy(s => s.Slot));
                SetState(room.State);
                break;
            case FinderSlotEvent { Slot: var slot } when InRoom:
                int at = _slots.FindIndex(s => s.Slot == slot.Slot);
                if (at >= 0)
                {
                    _slots[at] = slot;
                }
                else
                {
                    _slots.Add(slot);
                    _slots.Sort((a, b) => a.Slot.CompareTo(b.Slot));
                }

                _requests.RemoveAll(r => r.CharacterId == slot.CharacterId);
                break;
            case FinderReadyEvent ready when InRoom:
                int who = ready.CharacterId is { } id
                    ? _slots.FindIndex(s => s.CharacterId == id)
                    : _slots.FindIndex(s => IsSelf(s, registry));
                if (who >= 0)
                {
                    _slots[who] = _slots[who] with { Ready = ready.Ready };
                }

                break;
            case FinderStartEvent when InRoom:
                Started();
                break;
            case FinderLeftEvent:
            case FinderRoomClosedEvent closed when closed.RoomNumber == _room:
                Clear();
                break;
            case FinderJoinRequestEvent request:
                if (InRoom && request.RoomNumber == _room)
                {
                    _selfId = _leader;
                }

                _requests.RemoveAll(r => r.CharacterId == request.CharacterId);
                _requests.Add(request);
                if (!_noticed.TryGetValue(request.CharacterId, out long last) || request.TimestampMs - last >= RequestNoticeGapMs)
                {
                    _noticed[request.CharacterId] = request.TimestampMs;
                    Alert?.Invoke(new FinderAlert(FinderAlertKind.JoinRequest, request.RoomNumber, View(request)));
                }

                break;
            case FinderJoinRequestCancelledEvent cancelled:
                _requests.RemoveAll(r => r.CharacterId == cancelled.CharacterId);
                break;
            case SelfIdentityEvent { Name.Length: > 0 } self:
                if (_selfName is not null && _selfName != self.Name)
                {
                    Clear(); // another character
                    _selfId = 0;
                }

                _selfName = self.Name;
                if (self.CharacterId is { } character)
                {
                    _selfId = character;
                }

                break;
        }

        _requests.RemoveAll(r => r.ExpiresAtMs > 0 && r.ExpiresAtMs < _nowMs);
        if (InRoom && e is FinderRoomEvent or FinderSlotEvent or FinderReadyEvent)
        {
            CheckFull(registry);
        }
    }

    /// <summary>Our room, null when we are not in one.</summary>
    public FinderRoomView? Snapshot(EntityRegistry registry)
    {
        if (!InRoom)
        {
            return null;
        }

        var slots = _slots.Select(s => new FinderSlotView(s.Slot, s.CharacterId, s.Name, s.Class, s.Level, s.ItemLevel,
            s.CombatPower, s.Role, s.Ready, s.Online, !s.IsOpen && s.CharacterId == _leader, IsSelf(s, registry))).ToList();
        return new FinderRoomView(_room, _title, _dungeon, _state, _size, slots,
            _requests.Where(r => r.ExpiresAtMs == 0 || r.ExpiresAtMs >= _nowMs).Select(View).ToList(),
            slots.Any(s => s.IsSelf && s.IsLeader));
    }

    private void SetState(FinderRoomState state)
    {
        var was = _state;
        _state = state;
        if (state == FinderRoomState.Recruiting)
        {
            _startNoticed = false;
        }
        else if (was == FinderRoomState.Recruiting && state == FinderRoomState.InDungeon)
        {
            Started(); // members other than the leader get no 18 97
        }
    }

    private void Started()
    {
        if (_startNoticed)
        {
            return;
        }

        _startNoticed = true;
        _fullNoticed = true; // too late for "everyone ready"
        Alert?.Invoke(new FinderAlert(FinderAlertKind.Starting, _room));
    }

    private void CheckFull(EntityRegistry registry)
    {
        bool full = _state == FinderRoomState.Recruiting && Snapshot(registry) is { FullAndReady: true };
        if (full && !_fullNoticed && !_startNoticed)
        {
            _fullNoticed = true;
            Alert?.Invoke(new FinderAlert(FinderAlertKind.FullAndReady, _room));
        }
        else if (!full && _state == FinderRoomState.Recruiting)
        {
            _fullNoticed = false; // someone unreadied or left: notice again when it is ready again
        }
    }

    private bool IsSelf(FinderSlot slot, EntityRegistry registry) =>
        !slot.IsOpen && (_selfId != 0 && slot.CharacterId == _selfId || (registry.SelfName ?? _selfName) is { } me && slot.Name == me);

    private static FinderJoinRequestView View(FinderJoinRequestEvent r) =>
        new(r.CharacterId, r.Name, r.Class, r.Level, r.ItemLevel, r.CombatPower, r.Server, r.ExpiresAtMs);

    private void Clear()
    {
        _room = 0;
        _title = string.Empty;
        _dungeon = 0;
        _state = FinderRoomState.Recruiting;
        _leader = 0;
        _slots.Clear();
        _requests.Clear();
        _fullNoticed = _startNoticed = false;
    }
}

/// <summary>
/// The party member (or us) a boss targets, from <c>20 92</c>. Matches the boss's target as the game shows it (our
/// selected boss's target, <c>35 38</c>) in 161 of 164 checks. Cleared by <c>20 92</c> 0 and on map changes.
/// </summary>
public sealed class BossTargetTracker
{
    /// <summary>The entity id, null when no boss targets anyone in the party.</summary>
    public uint? EntityId { get; private set; }

    public void Apply(GameEvent e)
    {
        switch (e)
        {
            case PartyBossTargetEvent target:
                EntityId = target.EntityId == 0 ? null : target.EntityId;
                break;
            case ZoneChangeEvent or MapLoadEvent or PartyLeftEvent or PartyDisbandedEvent:
                EntityId = null; // entity ids are reissued
                break;
        }
    }
}

/// <summary>The text of a group-finder notice.</summary>
public static class FinderNoticeText
{
    /// <param name="name">Shows a player's name (streamer mode hides it).</param>
    public static string Text(FinderAlert alert, Func<string, string> name) => alert.Kind switch
    {
        FinderAlertKind.JoinRequest when alert.Request is { } r => string.Create(CultureInfo.InvariantCulture,
            $"Join request: {(r.Class == GameClass.Unknown ? "Player" : r.Class.ToString())} Lv {r.Level}, iLv {r.ItemLevel}, CP {r.CombatPower:N0} · {name(r.Name)}"),
        FinderAlertKind.FullAndReady => "Room full, everyone ready",
        FinderAlertKind.Starting => "The dungeon is starting",
        _ => "Group finder",
    };
}
