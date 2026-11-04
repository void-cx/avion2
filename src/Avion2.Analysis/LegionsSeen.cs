using System.Text.Json.Serialization;
using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>Where a combat power came from, the surest last.</summary>
public enum PowerSource
{
    /// <summary>A player spawn record (<c>45 36</c>), read by pattern; matched the finder's value in 105 of 115 spawns.</summary>
    Spawn = 0,

    /// <summary>A record that carries it as a field: our own (<c>56 36</c>), the party's (<c>1C 92</c>), the group finder's.</summary>
    Game = 1,

    /// <summary>The official AION2 site, from a character lookup.</summary>
    Site = 2,
}

/// <summary>
/// A player AVION2 saw, with what it knew of them at that moment. A sighting without a server or a legion only
/// updates a member already known by that name (the group finder names players but not their legion).
/// </summary>
/// <param name="Legion">The legion; empty when the game said the player is in no legion (a legion tag without one), null when not known.</param>
/// <param name="AtMs">When (Unix ms).</param>
/// <param name="Power">Where <paramref name="CombatPower"/> came from.</param>
public sealed record LegionSighting(
    string Name,
    ushort? ServerId,
    string? Legion,
    GameClass Class,
    uint? ItemLevel,
    uint? CombatPower,
    long AtMs,
    PowerSource Power = PowerSource.Spawn);

/// <summary>A member of a legion as last seen.</summary>
public sealed class SeenMember
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("class")]
    [JsonConverter(typeof(JsonStringEnumConverter<GameClass>))]
    public GameClass Class { get; set; }

    [JsonPropertyName("itemLevel")]
    public uint? ItemLevel { get; set; }

    [JsonPropertyName("combatPower")]
    public uint? CombatPower { get; set; }

    [JsonPropertyName("power")]
    [JsonConverter(typeof(JsonStringEnumConverter<PowerSource>))]
    public PowerSource Power { get; set; }

    /// <summary>When <see cref="CombatPower"/> was seen (Unix ms).</summary>
    [JsonPropertyName("powerMs")]
    public long PowerMs { get; set; }

    /// <summary>When the player was last seen (Unix ms).</summary>
    [JsonPropertyName("seenMs")]
    public long LastSeenMs { get; set; }
}

/// <summary>A legion on one server, with the members seen in it.</summary>
public sealed class SeenLegion
{
    [JsonPropertyName("server")]
    public ushort ServerId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("members")]
    public List<SeenMember> Members { get; set; } = new();

    /// <summary>The legion as the game's legion list last showed it (<c>07 8A</c> / <c>09 8A</c>); null when never listed.</summary>
    [JsonPropertyName("listed")]
    public ListedLegion? Listed { get; set; }

    [JsonIgnore]
    public long LastSeenMs => Math.Max(Members.Count == 0 ? 0 : Members.Max(m => m.LastSeenMs), Listed?.AtMs ?? 0);

    /// <summary>Neither members seen nor a listing: nothing left to show.</summary>
    [JsonIgnore]
    public bool IsEmpty => Members.Count == 0 && Listed is null;
}

/// <summary>A legion as the game's legion list showed it: its real member count, leader and level.</summary>
public sealed class ListedLegion
{
    [JsonPropertyName("id")]
    public uint Id { get; set; }

    [JsonPropertyName("leader")]
    public string Leader { get; set; } = string.Empty;

    [JsonPropertyName("level")]
    public int Level { get; set; }

    [JsonPropertyName("members")]
    public int Members { get; set; }

    [JsonPropertyName("max")]
    public int MaxMembers { get; set; }

    /// <summary>When the list showed it (Unix ms).</summary>
    [JsonPropertyName("atMs")]
    public long AtMs { get; set; }
}

/// <summary>
/// Every legion AVION2 has seen, per server, with the members seen in it (name, class, last item level and combat
/// power, last seen). Built from what the game shows of the players around us, our party and group-finder rooms, and
/// characters looked up on the official site. It counts what was seen, never a legion's real size. Saved between
/// sessions (legions_seen.json). Not thread-safe.
/// </summary>
public sealed class LegionsSeenBook
{
    /// <summary>A member not seen for this long no longer counts and is dropped.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    /// <summary>
    /// A combat power read from a spawn does not replace one the game or the site sent as a field, while that one is
    /// younger than this: it is then likely still right.
    /// </summary>
    private const long SurerPowerKeptMs = 24 * 60 * 60 * 1000L;

    private readonly Dictionary<(ushort, string), SeenLegion> _legions = new();

    /// <summary>The legion a player is in, by server and name, for moving players between legions.</summary>
    private readonly Dictionary<(ushort, string), SeenLegion> _memberOf = new();

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("legions")]
    public List<SeenLegion> Legions
    {
        get => _legions.Values.OrderBy(l => l.ServerId).ThenBy(l => l.Name, StringComparer.Ordinal).ToList();
        set
        {
            _legions.Clear();
            _memberOf.Clear();
            foreach (var legion in value.Where(l => l.Name.Length > 0))
            {
                var key = (legion.ServerId, legion.Name);
                if (_legions.TryGetValue(key, out var same))
                {
                    same.Members.AddRange(legion.Members);
                }
                else
                {
                    _legions[key] = legion;
                }
            }

            // A member saved twice (in two legions, or twice in one) stays where they were seen last
            var latest = _legions.Values
                .SelectMany(l => l.Members.Where(m => m.Name.Length > 0).Select(m => (Legion: l, Member: m)))
                .GroupBy(x => (x.Legion.ServerId, x.Member.Name))
                .ToDictionary(g => g.Key, g => g.MaxBy(x => x.Member.LastSeenMs));
            foreach (var legion in _legions.Values)
            {
                legion.Members = legion.Members.Where(m => latest.TryGetValue((legion.ServerId, m.Name), out var keep) && keep.Member == m).ToList();
            }

            foreach (var (at, keep) in latest)
            {
                _memberOf[at] = keep.Legion;
            }

            foreach (var (key, legion) in _legions.Where(l => l.Value.IsEmpty).ToList())
            {
                _legions.Remove(key);
            }
        }
    }

    /// <summary>The servers with at least one legion, most legions first.</summary>
    [JsonIgnore]
    public IReadOnlyList<ushort> Servers =>
        _legions.Values.GroupBy(l => l.ServerId).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).ToList();

    /// <summary>The legions seen on a server.</summary>
    public IReadOnlyList<SeenLegion> On(ushort serverId) => _legions.Values.Where(l => l.ServerId == serverId).ToList();

    public SeenLegion? Find(ushort serverId, string legion) => _legions.GetValueOrDefault((serverId, legion));

    /// <summary>
    /// Takes a sighting. With a server and a legion the player is placed in it (and leaves the legion they were in
    /// before); otherwise only a member already known by that name is updated, when exactly one matches. Returns true
    /// when something changed.
    /// </summary>
    public bool Take(LegionSighting seen)
    {
        if (seen.Name.Length == 0)
        {
            return false;
        }

        if (seen.ServerId is { } from && seen.Legion is { Length: 0 })
        {
            // In no legion now: out of the one they were placed in, unless that was seen later
            if (!_memberOf.TryGetValue((from, seen.Name), out var was) ||
                was.Members.FirstOrDefault(m => m.Name == seen.Name) is not { } left || left.LastSeenMs > seen.AtMs)
            {
                return false;
            }

            was.Members.Remove(left);
            _memberOf.Remove((from, seen.Name));
            if (was.IsEmpty)
            {
                _legions.Remove((was.ServerId, was.Name));
            }

            return true;
        }

        if (seen.ServerId is { } server && seen.Legion is { Length: > 0 } name)
        {
            var key = (server, name);
            var at = (server, seen.Name);
            if (_memberOf.TryGetValue(at, out var before) && before.Name != name &&
                before.Members.FirstOrDefault(m => m.Name == seen.Name) is { } placed && placed.LastSeenMs > seen.AtMs)
            {
                return false; // an older sighting than the one that placed them where they are
            }

            if (!_legions.TryGetValue(key, out var legion))
            {
                _legions[key] = legion = new SeenLegion { ServerId = server, Name = name };
            }

            if (before is not null && before != legion)
            {
                before.Members.RemoveAll(m => m.Name == seen.Name);
                if (before.IsEmpty)
                {
                    _legions.Remove((before.ServerId, before.Name));
                }
            }

            _memberOf[at] = legion;
            var member = legion.Members.FirstOrDefault(m => m.Name == seen.Name);
            if (member is null)
            {
                legion.Members.Add(member = new SeenMember { Name = seen.Name });
            }

            Update(member, seen);
            return true;
        }

        var matches = _memberOf
            .Where(m => m.Key.Item2 == seen.Name && (seen.ServerId is not { } only || m.Key.Item1 == only) &&
                        (seen.Legion is not { Length: > 0 } legionName || m.Value.Name == legionName))
            .Take(2)
            .ToList();
        if (matches.Count != 1 || matches[0].Value.Members.FirstOrDefault(m => m.Name == seen.Name) is not { } known)
        {
            return false;
        }

        Update(known, seen);
        return true;
    }

    private static void Update(SeenMember member, LegionSighting seen)
    {
        bool newer = seen.AtMs >= member.LastSeenMs;
        if (seen.Class != GameClass.Unknown && (newer || member.Class == GameClass.Unknown))
        {
            member.Class = seen.Class;
        }

        if (seen.ItemLevel is > 0 && (newer || member.ItemLevel is null))
        {
            member.ItemLevel = seen.ItemLevel;
        }

        if (seen.CombatPower is > 0 && seen.AtMs >= member.PowerMs &&
            (member.CombatPower is null || seen.Power != PowerSource.Spawn || member.Power == PowerSource.Spawn ||
             seen.AtMs - member.PowerMs > SurerPowerKeptMs))
        {
            member.CombatPower = seen.CombatPower;
            member.Power = seen.Power;
            member.PowerMs = seen.AtMs;
        }

        member.LastSeenMs = Math.Max(member.LastSeenMs, seen.AtMs);
    }

    /// <summary>
    /// Takes a legion from the game's legion list (its member count, leader and level); the legion shows on the page
    /// even with no member seen. Returns true when something changed.
    /// </summary>
    public bool TakeListing(LegionListing listing, long atMs)
    {
        if (listing.Name.Length == 0 || listing.ServerId == 0)
        {
            return false;
        }

        var key = (listing.ServerId, listing.Name);
        if (!_legions.TryGetValue(key, out var legion))
        {
            _legions[key] = legion = new SeenLegion { ServerId = listing.ServerId, Name = listing.Name };
        }

        if (legion.Listed is { } old && old.AtMs > atMs)
        {
            return false;
        }

        legion.Listed = new ListedLegion
        {
            Id = listing.LegionId, Leader = listing.Leader, Level = listing.Level, Members = listing.Members,
            MaxMembers = listing.MaxMembers, AtMs = atMs,
        };
        return true;
    }

    /// <summary>
    /// Drops the members not seen and the listings not renewed within <see cref="StaleAfter"/>, and legions left with
    /// neither. Returns how many members went.
    /// </summary>
    public int Prune(long nowMs)
    {
        long cutoff = nowMs - (long)StaleAfter.TotalMilliseconds;
        int dropped = 0;
        foreach (var (key, legion) in _legions.ToList())
        {
            foreach (var stale in legion.Members.Where(m => m.LastSeenMs < cutoff).ToList())
            {
                legion.Members.Remove(stale);
                _memberOf.Remove((legion.ServerId, stale.Name));
                dropped++;
            }

            if (legion.Listed is { } listed && listed.AtMs < cutoff)
            {
                legion.Listed = null;
            }

            if (legion.IsEmpty)
            {
                _legions.Remove(key);
            }
        }

        return dropped;
    }
}

/// <summary>How the Legions page orders legions.</summary>
public enum LegionOrder
{
    /// <summary>The combat power of the members seen, added up.</summary>
    CombatPower,

    /// <summary>The members: the game's legion list's count when it showed the legion, else the members seen.</summary>
    Members,
}

/// <summary>A legion's place on a server.</summary>
/// <param name="Members">Members seen within <see cref="LegionsSeenBook.StaleAfter"/>.</param>
/// <param name="MembersWithPower">Of those, the ones whose combat power is known.</param>
/// <param name="CombatPower">The known combat power of those members, added up.</param>
/// <param name="Members">The legion's members: <paramref name="Listed"/> when the legion list showed it, else <paramref name="Seen"/>.</param>
/// <param name="FromSite">Members whose combat power came from the official site.</param>
/// <param name="Seen">Members seen within <see cref="LegionsSeenBook.StaleAfter"/>.</param>
/// <param name="Listed">The member count of the game's legion list, when it showed the legion within that time.</param>
public sealed record LegionStanding(
    int Rank,
    SeenLegion Legion,
    int Members,
    int MembersWithPower,
    long CombatPower,
    int FromSite,
    long LastSeenMs,
    int Seen = 0,
    int? Listed = null)
{
    /// <summary>The members' average combat power, null when none is known.</summary>
    public long? AverageCombatPower => MembersWithPower > 0 ? CombatPower / MembersWithPower : null;
}

/// <summary>
/// Ranks the legions seen on a server. By combat power: the known combat power of the members seen, added up, as
/// community ranking sites add up a legion's members (here only those seen, so it says as much about how often we met
/// them as about the legion); ties go to more members seen. By members: the members seen; ties go to more combat power.
/// Members not seen within <see cref="LegionsSeenBook.StaleAfter"/> do not count. Equal legions share no rank: the
/// one seen last goes first, then by name.
/// </summary>
public static class LegionRanking
{
    public static IReadOnlyList<LegionStanding> Rank(IEnumerable<SeenLegion> legions, LegionOrder order, long nowMs)
    {
        long cutoff = nowMs - (long)LegionsSeenBook.StaleAfter.TotalMilliseconds;
        var standings = legions
            .Select(l =>
            {
                var members = l.Members.Where(m => m.LastSeenMs >= cutoff).ToList();
                var powered = members.Where(m => m.CombatPower is > 0).ToList();
                int? listed = l.Listed is { } list && list.AtMs >= cutoff ? list.Members : null;
                long last = Math.Max(members.Count == 0 ? 0 : members.Max(m => m.LastSeenMs), listed is null ? 0 : l.Listed!.AtMs);
                return new LegionStanding(0, l, listed ?? members.Count, powered.Count, powered.Sum(m => (long)m.CombatPower!.Value),
                    powered.Count(m => m.Power == PowerSource.Site), last, members.Count, listed);
            })
            .Where(s => s.Members > 0);

        var ordered = order == LegionOrder.Members
            ? standings.OrderByDescending(s => s.Members).ThenByDescending(s => s.CombatPower)
            : standings.OrderByDescending(s => s.CombatPower).ThenByDescending(s => s.Members);
        return ordered
            .ThenByDescending(s => s.LastSeenMs)
            .ThenBy(s => s.Legion.Name, StringComparer.Ordinal)
            .Select((s, i) => s with { Rank = i + 1 })
            .ToList();
    }

    /// <summary>The value a legion is ranked by.</summary>
    public static long Score(LegionStanding standing, LegionOrder order) =>
        order == LegionOrder.Members ? standing.Members : standing.CombatPower;
}

/// <summary>
/// Notes the players the game shows us with their legion: players in range (legion tag <c>33 8A</c>, spawn, item level
/// from <c>1C 56</c>), us (<c>56 36</c>, <c>1D 56</c>), our party (<c>1C 92</c>) and the group-finder room and its
/// join requests. A player is noted again only when something about them changed, or ten minutes later.
/// Driven by <see cref="GameSession"/>; not thread-safe.
/// </summary>
public sealed class LegionSpotter
{
    private const long RenoteMs = 10 * 60 * 1000;

    private readonly Dictionary<(ushort?, string), (LegionSighting Seen, long At)> _last = new();
    private readonly List<LegionSighting> _pending = new();

    /// <summary>Sightings and listings not taken yet.</summary>
    public int Pending => _pending.Count + _listings.Count;

    /// <summary>The sightings since the last call.</summary>
    public IReadOnlyList<LegionSighting> Drain()
    {
        var taken = _pending.ToList();
        _pending.Clear();
        return taken;
    }

    private readonly List<(LegionListing Listing, long AtMs)> _listings = new();

    /// <summary>The legion lists' legions since the last call, with when they were listed.</summary>
    public IReadOnlyList<(LegionListing Listing, long AtMs)> DrainListings()
    {
        var taken = _listings.ToList();
        _listings.Clear();
        return taken;
    }

    public void Apply(GameEvent e, EntityRegistry registry, PartyTracker party, LegionState ownLegion)
    {
        switch (e)
        {
            case LegionTagEvent tag:
                Entity(registry, tag.EntityId, e.TimestampMs, ownLegion, noLegion: tag.Legion.Length == 0);
                break;
            case LegionListEvent list:
                _listings.AddRange(list.Legions.Select(l => (l, e.TimestampMs)));
                if (_listings.Count > 5_000)
                {
                    _listings.RemoveRange(0, _listings.Count - 5_000);
                }

                break;
            case PlayerSpawnEvent spawn:
                Entity(registry, spawn.EntityId, e.TimestampMs, ownLegion);
                break;
            case EquipmentChangedEvent equipment:
                Entity(registry, equipment.EntityId, e.TimestampMs, ownLegion);
                break;
            case SelfCombatPowerEvent or SelfItemLevelEvent or SelfIdentityEvent when registry.SelfId is { } self:
                Entity(registry, self, e.TimestampMs, ownLegion);
                break;
            case PartyMemberStatusEvent status when party.Snapshot(registry).FirstOrDefault(m => m.CharacterId == status.CharacterId) is
                { IsSelf: false, Name.Length: > 0 } member:
                // Out of view the member's server is not known: the book then updates them only if their name is
                Note(new LegionSighting(member.Name, member.EntityId != 0 ? registry.Find(member.EntityId)?.ServerId : null,
                    status.Status.Legion is { Length: > 0 } legion ? legion : null, status.Class, Positive(status.Status.ItemLevel),
                    Positive(status.Status.CombatPower), e.TimestampMs, PowerSource.Game));
                break;
            case FinderRoomEvent room:
                foreach (var slot in room.Slots)
                {
                    Slot(slot, e.TimestampMs);
                }

                break;
            case FinderSlotEvent slot:
                Slot(slot.Slot, e.TimestampMs);
                break;
            case FinderJoinRequestEvent request when request.Name.Length > 0:
                // (its server is not always the player's own)
                Note(new LegionSighting(request.Name, null, null, request.Class, Positive(request.ItemLevel), Positive(request.CombatPower),
                    e.TimestampMs, PowerSource.Game));
                break;
        }
    }

    private void Slot(FinderSlot slot, long atMs)
    {
        if (!slot.IsOpen && slot.Name.Length > 0)
        {
            Note(new LegionSighting(slot.Name, null, null, slot.Class, Positive(slot.ItemLevel), Positive(slot.CombatPower), atMs,
                PowerSource.Game));
        }
    }

    private void Entity(EntityRegistry registry, uint id, long atMs, LegionState ownLegion, bool noLegion = false)
    {
        if (registry.Find(id) is not { Kind: EntityKind.Player, Name: { Length: > 0 } name, ServerId: { } server } info)
        {
            return;
        }

        bool self = id == registry.SelfId;
        string? legion = info.Legion is { Length: > 0 } tagged ? tagged
            : noLegion ? string.Empty
            : self && ownLegion.Name is { Length: > 0 } own && ownLegion.Character == name ? own
            : null;
        uint? itemLevel = self ? registry.SelfItemLevel ?? info.ItemLevel : info.ItemLevel;
        Note(new LegionSighting(name, server, legion, info.Class, Positive(itemLevel), Positive(info.CombatPower), atMs,
            self ? PowerSource.Game : PowerSource.Spawn));
    }

    private void Note(LegionSighting seen)
    {
        var key = (seen.ServerId, seen.Name);
        if (_last.TryGetValue(key, out var last) && last.Seen with { AtMs = 0 } == seen with { AtMs = 0 } &&
            seen.AtMs - last.At < RenoteMs)
        {
            return;
        }

        _last[key] = (seen, seen.AtMs);
        _pending.Add(seen);
        if (_pending.Count > 5_000)
        {
            _pending.RemoveRange(0, _pending.Count - 5_000); // nobody takes them (no page, no store)
        }
    }

    private static uint? Positive(uint? value) => value is > 0 ? value : null;
}
