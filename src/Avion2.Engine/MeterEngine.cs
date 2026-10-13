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

    /// <summary>Directory with skills.json / npcs.json; null = auto-detect.</summary>
    public string? DataDirectory { get; init; }

    public TrackerOptions Tracker { get; init; } = new();

    /// <summary>Directory for saved fights; null = %LocalAppData%\AVION2\history. Set <see cref="SaveHistory"/> to false to disable.</summary>
    public string? HistoryDirectory { get; init; }

    public bool SaveHistory { get; init; } = true;

    /// <summary>Where our own characters are remembered; null = %AppData%\AVION2\characters.json.</summary>
    public string? CharacterStorePath { get; init; }

    /// <summary>Play a recorded .pcap in real time instead of capturing live (UI development, demos).</summary>
    public string? ReplayPath { get; init; }

    public double ReplaySpeed { get; init; } = 1.0;
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
    uint SelfMaxHp);

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
        var (gameData, dataDirectory) = FindGameData(_options.DataDirectory);
        DataDirectory = dataDirectory;
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
            _history.Insert(0, entry);
            FightSaved?.Invoke(entry);
        };
        _session.Registry.SetKnownCharacters(characters.Characters);
        _session.Registry.CharacterIdentified += character =>
        {
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
        _pipeline = new GameCapturePipeline(e =>
        {
            _session.Apply(e);
            _log?.Write(e);
        });
        _pipeline.StatusChanged += message => _message = message;
    }

    public bool HasGameData => _session.Registry.Data.Skills.Count > 0;

    /// <summary>Where the name tables were loaded from; null if none were found.</summary>
    public string? DataDirectory { get; private set; }

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

    /// <summary>Loads the name tables from <paramref name="directory"/> (e.g. after an import) without restarting.</summary>
    public void ReloadGameData(string directory)
    {
        var data = GameData.Load(directory);
        lock (_sync)
        {
            _session.Registry.SetData(data);
            DataDirectory = directory;
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
                registry.SelfMaxHp);
        }
    }

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

    public static GameData LoadGameData(string? directory) => FindGameData(directory).Data;

    /// <summary>Finds the name tables: the given directory, else ./data, %AppData%\AVION2\data, or a parent's data/.</summary>
    public static (GameData Data, string? Directory) FindGameData(string? directory)
    {
        var candidates = new List<string>();
        if (directory is not null)
        {
            candidates.Add(directory);
        }
        else
        {
            candidates.Add(Path.Combine(Environment.CurrentDirectory, "data"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AVION2", "data"));

            // Development builds run from bin/<config>/<tfm>/: also look in parent folders (the repo's data/).
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                candidates.Add(Path.Combine(dir.FullName, "data"));
            }
        }

        foreach (string candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "skills.json")) || File.Exists(Path.Combine(candidate, "npcs.json")))
            {
                return (GameData.Load(candidate), candidate);
            }
        }

        return (GameData.Empty, null);
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
                    _pipeline.Process(frame);
                    _recorder?.Write(frame, _pipeline.Detector.Locked);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
