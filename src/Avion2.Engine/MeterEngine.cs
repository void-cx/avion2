using Avion2.Analysis;
using Avion2.Capture;
using Avion2.Protocol;

namespace Avion2.Engine;

public sealed class EngineOptions
{
    /// <summary>Capture device indices; null = all devices with an address plus loopback.</summary>
    public IReadOnlyCollection<int>? Devices { get; init; }

    public string? RecordPath { get; init; }

    public string? EventLogPath { get; init; }

    /// <summary>Directory whose skills.json / npcs.json / field_bosses.json / maps.json override the built-in tables; null = %AppData%\AVION2\data if it has any.</summary>
    public string? DataDirectory { get; init; }

    /// <summary>Language of the built-in name tables (see <see cref="BundledGameData.Languages"/>).</summary>
    public string DataLanguage { get; init; } = "en";

    public TrackerOptions Tracker { get; init; } = new();

    /// <summary>Directory for saved fights; null = %LocalAppData%\AVION2\history. Set <see cref="SaveHistory"/> to false to disable.</summary>
    public string? HistoryDirectory { get; init; }

    public bool SaveHistory { get; init; } = true;

    /// <summary>Where our own characters are remembered; null = %AppData%\AVION2\characters.json.</summary>
    public string? CharacterStorePath { get; init; }

    /// <summary>Play a recorded .pcap in real time instead of capturing live (UI development, demos).</summary>
    public string? ReplayPath { get; init; }

    public double ReplaySpeed { get; init; } = 1.0;

    /// <summary>Developer aid: no real player or legion names anywhere (shareable screenshots, see <see cref="NameAnonymizer"/>).</summary>
    public bool AnonymizeNames { get; init; }
}

/// <summary>What a UI needs besides the meter rows.</summary>
public sealed record EngineStatus(
    string Message,
    bool Locked,
    string? SelfName,
    bool SelfNameIsGuess,
    GameClass SelfClass,
    long Frames,
    long GamePackets,
    long ParsedPackets,
    long Dropped,
    long Recorded,
    bool InCombat,
    uint SelfHp,
    uint SelfMaxHp,
    long ServerSilenceMs = 0);

/// <summary>
/// Live capture → pipeline → <see cref="GameSession"/>, on a background consumer.
/// All public members are thread-safe; UIs poll <see cref="Snapshot"/> and <see cref="Status"/>.
/// </summary>
public sealed class MeterEngine : IDisposable
{
    private readonly object _sync = new();
    private readonly EngineOptions _options;
    private readonly GameSession _session;
    private readonly GameCapturePipeline _pipeline;
    private readonly LiveCapture _capture = new();
    private readonly EventLog? _log;
    private readonly PcapRecorder? _recorder;
    private readonly CancellationTokenSource _cts = new();
    private Task? _consumer;
    private readonly CharacterStore _characters;
    private readonly HistoryStore _historyStore;
    private readonly List<HistoryEntry> _history;
    private string _message = "Searching for the AION2 connection…";

    public MeterEngine(EngineOptions? options = null)
    {
        _options = options ?? new EngineOptions();
        ErrorLog.Install();
        var (gameData, dataSource) = FindGameData(_options.DataDirectory, _options.DataLanguage);
        DataSource = dataSource;
        _session = new GameSession(gameData, _options.Tracker);
        var characters = _characters = new CharacterStore(_options.CharacterStorePath);
        _historyStore = new HistoryStore(_options.HistoryDirectory);
        _history = _options.SaveHistory ? _historyStore.Load() : new List<HistoryEntry>();
        _session.Tracker.EncounterEnded += encounter =>
        {
            if (encounter.Final is not { } final)
            {
                return;
            }

            var entry = (_options.SaveHistory ? _historyStore.Save(final) : null) ?? new HistoryEntry(string.Empty, final);
            var trace = _session.Trace(encounter);
            _traces.AddOrUpdate(entry, trace);
            if (_options.SaveHistory)
            {
                _historyStore.SaveTrace(entry, trace);
            }

            _history.Insert(0, entry);
            FightSaved?.Invoke(entry);
        };
        _session.Cubes.Changed += cube => CubeSeen?.Invoke(cube);
        var anonymizer = _options.AnonymizeNames ? new NameAnonymizer() : null;
        if (anonymizer is null)
        {
            _session.Registry.SetKnownCharacters(characters.Characters);
        }

        _session.Registry.CharacterIdentified += character =>
        {
            if (anonymizer is not null)
            {
                return; // never remember "You" as a character
            }

            // Identity records repeat; only touch the file when something changed.
            var latest = characters.Characters.FirstOrDefault();
            if (latest is null || latest.Name != character.Name || latest.Class != character.Class ||
                character.LastSeen - latest.LastSeen > TimeSpan.FromMinutes(10))
            {
                characters.Remember(character);
            }
        };
        _log = _options.EventLogPath is { } logPath ? new EventLog(logPath) : null;
        _recorder = _options.RecordPath is { } recordPath ? new PcapRecorder(recordPath) : null;
        _pipeline = new GameCapturePipeline(raw =>
        {
            if ((anonymizer is null ? raw : anonymizer.Rewrite(raw)) is not { } e)
            {
                return;
            }

            // A replay at another speed than 1× compresses arrival times against the server clock: its delays and
            // freezes would be fiction, so such replays carry no connection data.
            if (e is ServerClockEvent && _options.ReplayPath is not null && _options.ReplaySpeed != 1.0)
            {
                return;
            }

            _session.Apply(e);
            if (e is ServerClockEvent)
            {
                _lastServerTick = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            else
            {
                _log?.Write(e); // 20 clock ticks a second would swamp the event log
            }

            if (e is FieldBossTableEvent table)
            {
                FieldBossTable?.Invoke(table);
            }
            else if (e is LegionDetailsEvent or LegionRosterEvent)
            {
                LegionChanged?.Invoke();
            }
            else if (e is InventoryListEvent or InventoryItemEvent)
            {
                InventoryChanged?.Invoke();
            }
            else if (e is SkillCastEvent cast && Cast is { } onCast)
            {
                onCast(cast, _session.Registry.Find(cast.ActorId));
            }
        });
        _pipeline.StatusChanged += message => _message = message;
    }

    public bool HasGameData => _session.Registry.Data.Skills.Count > 0;

    /// <summary>Where the name tables in use came from.</summary>
    public GameDataSource DataSource { get; private set; }

    public (int Skills, int Npcs) GameDataCounts
    {
        get
        {
            lock (_sync)
            {
                return (_session.Registry.Data.Skills.Count, _session.Registry.Data.Npcs.Count);
            }
        }
    }

    /// <summary>Switches the name tables (other language or override directory) without restarting.</summary>
    public void ReloadGameData(string language, string? directory = null)
    {
        var (data, source) = FindGameData(directory, language);
        lock (_sync)
        {
            _session.Registry.SetData(data);
            DataSource = source;
        }
    }

    /// <summary>Opens the capture devices and starts decoding. Returns per-device errors.</summary>
    public IReadOnlyList<string> Start()
    {
        if (_options.ReplayPath is { } replayPath)
        {
            _consumer = Task.Run(() => ReplayAsync(replayPath));
            return [];
        }

        var errors = _capture.Start(_options.Devices);
        if (_capture.OpenDevices.Count == 0)
        {
            _message = "No capture device could be opened. Is Npcap installed? Try running as administrator.";
            return errors;
        }

        _consumer = Task.Run(ConsumeAsync);
        return errors;
    }

    public string HistoryDirectory => _historyStore.Directory;

    /// <summary>Raised on the capture thread for every field boss table (about once a second on field maps).</summary>
    public event Action<FieldBossTableEvent>? FieldBossTable;

    /// <summary>Raised on the capture thread when an encounter ends (and was saved, if saving is on).</summary>
    public event Action<HistoryEntry>? FightSaved;

    public MeterSnapshot? Snapshot(MeterScope scope = MeterScope.MyTargets)
    {
        lock (_sync)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _session.Tracker.Tick(now);
            return _session.Snapshot(scope, now);
        }
    }

    public EngineStatus Status()
    {
        lock (_sync)
        {
            var registry = _session.Registry;
            var stats = _pipeline.Decoder.Stats;
            return new EngineStatus(
                _message,
                _pipeline.Detector.Locked is not null,
                registry.SelfName,
                registry.SelfNameIsGuess,
                registry.SelfClass,
                _pipeline.Stats.Frames,
                stats.Packets,
                stats.ParsedPackets,
                _capture.Dropped,
                _recorder?.Written ?? 0,
                _session.Tracker.Current is not null,
                registry.SelfHp,
                registry.SelfMaxHp,
                _pipeline.Detector.Locked is not null && _lastServerTick != 0
                    ? (long)System.Diagnostics.Stopwatch.GetElapsedTime(_lastServerTick).TotalMilliseconds
                    : 0);
        }
    }

    /// <summary>When the last server clock tick arrived (Stopwatch ticks); 0 before the first.</summary>
    private long _lastServerTick;

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<HistoryEntry, FightTrace> _traces = new();

    /// <summary>The trace (timeline, buffs, positions) of a saved fight; null if it has none.</summary>
    public FightTrace? Trace(HistoryEntry entry)
    {
        lock (_sync)
        {
            if (_traces.TryGetValue(entry, out var cached))
            {
                return cached;
            }
        }

        return _historyStore.LoadTrace(entry);
    }

    /// <summary>The trace of the running encounter, or of the last finished one.</summary>
    public FightTrace? LiveTrace()
    {
        lock (_sync)
        {
            var encounter = _session.Tracker.Current ?? _session.Tracker.History.LastOrDefault();
            return encounter is null ? null : _session.Trace(encounter);
        }
    }

    /// <summary>The current map and everyone's latest position on it, for the live map.</summary>
    public (uint? MapId, IReadOnlyList<LiveEntity> Entities) LiveMap()
    {
        lock (_sync)
        {
            return (_session.Positions.MapId ?? _session.Registry.MapId, _session.Positions.Snapshot(_session.Registry));
        }
    }

    /// <summary>Our legion as seen this session (a copy).</summary>
    public LegionState? Legion()
    {
        lock (_sync)
        {
            var legion = _session.Legion;
            return legion.IsKnown || legion.Members.Count > 0
                ? new LegionState { Name = legion.Name, Leader = legion.Leader, Texts = legion.Texts.ToList(), Members = legion.Members, UpdatedMs = legion.UpdatedMs }
                : null;
        }
    }

    /// <summary>Raised on the capture thread when legion details or members arrive.</summary>
    public event Action? LegionChanged;

    /// <summary>Raised on the capture thread when a hidden cube came into range or was opened.</summary>
    public event Action<CubeSighting>? CubeSeen;

    /// <summary>Cube object codes learned this session (to keep them between sessions).</summary>
    public IReadOnlyCollection<uint> CubeCodes()
    {
        lock (_sync)
        {
            return _session.Cubes.CubeCodes.ToList();
        }
    }

    /// <summary>Codes known from earlier sessions.</summary>
    public void TeachCubeCodes(IEnumerable<uint> codes)
    {
        lock (_sync)
        {
            _session.Cubes.CubeCodes.UnionWith(codes);
        }
    }

    /// <summary>Raised on the capture thread when our inventory changed (<see cref="Inventory"/> has it).</summary>
    public event Action? InventoryChanged;

    /// <summary>Our inventory as seen this session (a copy).</summary>
    public InventoryState Inventory()
    {
        lock (_sync)
        {
            return _session.Inventory.Copy();
        }
    }

    /// <summary>Raised on the capture thread for every skill cast (boss cast warnings).</summary>
    public event Action<SkillCastEvent, EntityInfo?>? Cast;

    /// <summary>Finished encounters of this session, oldest first.</summary>
    public IReadOnlyList<MeterSnapshot> History()
    {
        lock (_sync)
        {
            return _session.Tracker.History.Select(e => e.Final).OfType<MeterSnapshot>().ToList();
        }
    }

    /// <summary>Saved encounters including earlier sessions, newest first.</summary>
    public IReadOnlyList<HistoryEntry> SavedHistory()
    {
        lock (_sync)
        {
            return _history.ToList();
        }
    }

    public int SavedHistoryCount
    {
        get
        {
            lock (_sync)
            {
                return _history.Count;
            }
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _session.Tracker.Reset();
        }
    }

    /// <summary>Stops capturing and closes the running encounter so it lands in <see cref="History"/>.</summary>
    public void Stop()
    {
        _cts.Cancel();
        _capture.Dispose();
        try
        {
            _consumer?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // cancelled
        }

        Reset();
    }

    public void Dispose()
    {
        Stop();
        RememberMaxHp();
        _log?.Dispose();
        _recorder?.Dispose();
        _cts.Dispose();
    }

    /// <summary>Max HP is only ever observed; keep the best value per character for the next session.</summary>
    private void RememberMaxHp()
    {
        lock (_sync)
        {
            var registry = _session.Registry;
            if (registry.CurrentCharacter() is { } character && registry.SelfMaxHp > character.MaxHp)
            {
                _characters.Remember(character with { MaxHp = registry.SelfMaxHp, LastSeen = DateTimeOffset.UtcNow });
            }
        }
    }

    public static GameData LoadGameData(string? directory, string language = "en") => FindGameData(directory, language).Data;

    /// <summary>
    /// The built-in name tables, with single files overridden from <paramref name="directory"/> (or from
    /// %AppData%\AVION2\data when that has tables in it).
    /// </summary>
    public static (GameData Data, GameDataSource Source) FindGameData(string? directory, string language = "en")
    {
        if (directory is null)
        {
            string user = BundledGameData.UserDataDirectory;
            string[] tables = ["skills.json", "npcs.json", "field_bosses.json", "maps.json"];
            if (tables.Any(t => File.Exists(Path.Combine(user, t))))
            {
                directory = user;
            }
        }

        if (!BundledGameData.Languages.Contains(language))
        {
            language = "en";
        }

        return (BundledGameData.Load(language, directory), new GameDataSource(language, directory));
    }

    /// <summary>Feeds a recording with timestamps shifted to "now", paced by the original gaps.</summary>
    private async Task ReplayAsync(string path)
    {
        try
        {
            long? firstTs = null;
            long startNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var frame in PcapFile.Read(path))
            {
                firstTs ??= frame.TimestampMs;
                long offset = (long)((frame.TimestampMs - firstTs.Value) / _options.ReplaySpeed);
                long wait = startNow + offset - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (wait > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(wait), _cts.Token);
                }

                lock (_sync)
                {
                    _pipeline.Process(frame with { TimestampMs = startNow + offset });
                }
            }

            _message = $"Replay finished: {Path.GetFileName(path)}";
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _message = $"Replay failed: {ex.Message}";
        }
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var frame in _capture.Frames.ReadAllAsync(_cts.Token))
            {
                lock (_sync)
                {
                    // Never let one frame end the capture: before this guard, an exception here stopped the
                    // meter silently (e.g. on entering a dungeon).
                    try
                    {
                        _pipeline.Process(frame);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        ErrorLog.Write("frame", ex, frame.Data);
                    }

                    try
                    {
                        _recorder?.Write(frame, _pipeline.Detector.Locked);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        ErrorLog.Write("recorder", ex, null);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
