using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Avion2.Analysis;
using Avion2.Engine;
using Avion2.Protocol;

namespace Avion2.Overlay;

/// <summary>
/// Discord Rich Presence (Settings, General; off by default): what we are doing in game shows in our Discord
/// profile. Talks only to the Discord app on this PC (where AVION2 runs, which need not be the game's PC). Checks
/// every 5 s and sends at most every 15 s, only when the text changed.
/// </summary>
public sealed partial class DiscordFeed
{
    /// <summary>"Gathering" stays up this long after the last gathering ended (walking to the next node).</summary>
    private const long TaskLingerMs = 90_000;

    private readonly AppState _state;
    private readonly DiscordClient _client = new();
    private readonly PresenceThrottle _throttle = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };

    private MeterEngine? _engine;
    private Action<GameEvent>? _gatherHandler;
    private uint? _mapId;
    private long _mapSinceMs;
    private PresenceTask _task;
    private long _taskSinceMs, _taskLastMs;

    public DiscordFeed(AppState state)
    {
        _state = state;
        _timer.Tick += (_, _) => Publish();
        _client.StateChanged += _ => Application.Current?.Dispatcher.BeginInvoke(() => StateChanged?.Invoke());
        StreamerMode.Changed += () => Publish(force: true); // our name goes at once, not after the 15 s
        state.PortraitsChanged += () => Publish(force: true);
        if (Application.Current is { } app)
        {
            app.Exit += (_, _) => _client.Stop(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>Raised on the UI thread when the connection changes (for the status line in Settings).</summary>
    public event Action? StateChanged;

    /// <summary>The status line in Settings; empty while the feature is off.</summary>
    public string Status => _client.State switch
    {
        DiscordState.Connected => "Connected to Discord",
        DiscordState.NotRunning => "Discord is not running on this PC",
        DiscordState.Connecting => "Looking for Discord…",
        DiscordState.Rejected => "Discord did not accept AVION2's application id",
        _ => string.Empty,
    };

    public bool Connected => _client.State == DiscordState.Connected;

    /// <summary>Starts or stops to match the settings; a changed privacy setting is sent at once.</summary>
    public void Apply()
    {
        if (!_state.Stored.DiscordPresence || !DiscordPresence.IsApplicationId(DiscordPresence.ApplicationId))
        {
            _timer.Stop();
            _client.Stop(); // clears the presence first
            _throttle.Reset();
            return;
        }

        _client.Start(DiscordPresence.ApplicationId);
        _timer.Start();
        Publish(force: true);
    }

    private void Publish(bool force = false)
    {
        if (!_timer.IsEnabled)
        {
            return;
        }

        if (force)
        {
            _throttle.Reset();
        }

        var options = new PresenceOptions(_state.Stored.DiscordShowName, StreamerMode.On, _state.Stored.DiscordShowPicture);
        var activity = DiscordPresence.Build(Input(), options);
        if (_throttle.ShouldSend(activity, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
        {
            _client.SetActivity(activity);
        }
    }

    /// <summary>What the meter knows right now; nothing while it is stopped (or replays a recording).</summary>
    private PresenceInput Input()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SyncEngine();
        if (_engine is not { } engine || _state.IsReplay)
        {
            return new PresenceInput();
        }

        var status = engine.Status();
        var data = _state.Data;
        uint? mapId = engine.LiveMap().MapId;
        if (mapId != _mapId)
        {
            _mapId = mapId;
            _mapSinceMs = now;
        }

        PresenceRun? run = null;
        if (_state.LiveRun is { } live)
        {
            var row = new RunRow(live, data);
            int listed = row.Dungeon?.Bosses.Count ?? 0;
            run = new PresenceRun(row.Title, row.Category, live.StartMs, live.Bosses.Count,
                listed > 0 && listed >= live.Bosses.Count ? listed : null);
        }

        PresenceBoss? boss = null;
        long? combat = null;
        if (status.InCombat && engine.Snapshot(_state.Settings.Scope) is { End: EncounterEnd.Ongoing } fight)
        {
            if (fight.TargetIsBoss)
            {
                double? hp = fight is { TargetHp: { } h, TargetMaxHp: { } max } && max > 0 ? (double)h / max : null;
                boss = new PresenceBoss(MoreTargets().Replace(fight.Title, string.Empty), hp, fight.StartedAtMs);
            }
            else
            {
                combat = fight.StartedAtMs > 0 ? fight.StartedAtMs : now;
            }
        }

        bool busy = _task != PresenceTask.None && now - _taskLastMs < TaskLingerMs;
        return new PresenceInput
        {
            Capturing = true,
            Map = mapId is { } id && data.Maps.TryGetValue(id, out var name) ? name : null,
            MapSinceMs = mapId is null ? null : _mapSinceMs,
            Run = run,
            Boss = boss,
            CombatSinceMs = combat,
            Task = busy ? _task : PresenceTask.None,
            TaskSinceMs = busy ? _taskSinceMs : null,
            PartySize = engine.Party().Count,
            SelfName = status.SelfNameIsGuess ? null : status.SelfName,
            SelfClass = status.SelfClass,
            SelfPictureUrl = status.SelfNameIsGuess ? null : Picture(status.SelfName, status.SelfServer),
        };
    }

    /// <summary>Our portrait's address on the official site (saved with the portrait), when the user chose it.</summary>
    private string? Picture(string? name, ushort? server) =>
        _state.Stored.DiscordShowPicture && !StreamerMode.On && !string.IsNullOrEmpty(name) && server is { } id
            ? _state.Portraits.Url(name, id)
            : null;

    /// <summary>Follows the meter session: its gathering and crafting events mark what we do.</summary>
    private void SyncEngine()
    {
        if (_state.Engine == _engine)
        {
            return;
        }

        if (_engine is not null && _gatherHandler is not null)
        {
            _engine.Gathering -= _gatherHandler;
        }

        _engine = _state.Engine;
        _mapId = null;
        _task = PresenceTask.None;
        if (_engine is not null)
        {
            _gatherHandler = e => Application.Current?.Dispatcher.BeginInvoke(() => OnGathering(e));
            _engine.Gathering += _gatherHandler;
        }
    }

    private void OnGathering(GameEvent e)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var task = e switch
        {
            GatherStartEvent => PresenceTask.Gathering,
            CraftStartEvent => PresenceTask.Crafting,
            _ => PresenceTask.None,
        };
        if (task != PresenceTask.None && (task != _task || now - _taskLastMs >= TaskLingerMs))
        {
            _task = task;
            _taskSinceMs = now;
        }

        _taskLastMs = now;
    }

    /// <summary>The " (+2)" the meter adds for further targets.</summary>
    [GeneratedRegex(@"\s*\(\+\d+\)$")]
    private static partial Regex MoreTargets();
}
