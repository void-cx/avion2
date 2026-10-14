using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Avion2.Analysis;
using Avion2.Capture;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <summary>
/// Everything that outlives a meter session: user settings, saved fights, name tables, and the running
/// meter (engine + overlay) if any. Lives on the UI thread.
/// </summary>
public sealed class AppState
{
    private readonly EngineOptions _baseOptions;

    public AppState(EngineOptions baseOptions)
    {
        _baseOptions = baseOptions;
        Stored = OverlaySettings.Load();
        Settings = new OverlayViewModel();
        LoadSettings();
        History = new HistoryStore(baseOptions.HistoryDirectory);
        foreach (var entry in History.Load(300))
        {
            Fights.Add(new HistoryRow(entry));
        }

        RefreshGameDataInfo();
        FieldBosses = Avion2.Analysis.FieldBossBoard.Load(FieldBossFile);
        Timers = new TimerService(Settings, FieldBossOccurrences);
        Timers.Due += OnEventDue;
    }

    private static string FieldBossFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVION2", "field_bosses.json");

    /// <summary>Latest field boss table per map; saved, so respawn times survive restarts.</summary>
    public Avion2.Analysis.FieldBossBoard FieldBosses { get; }

    /// <summary>Name tables (skills, NPCs, field bosses) for views that work without a running meter.</summary>
    public Avion2.Analysis.GameData Data { get; private set; } = Avion2.Analysis.GameData.Empty;

    /// <summary>Upcoming respawns as timetable occurrences, so they share the notification path.</summary>
    private IEnumerable<Avion2.Analysis.Occurrence> FieldBossOccurrences(DateTimeOffset now) =>
        FieldBosses.Rows(Data, now)
            .Where(r => r.Status == Avion2.Analysis.FieldBossStatus.Respawning)
            .Select(r => new Avion2.Analysis.Occurrence(
                new Avion2.Analysis.ScheduledEvent($"fieldboss:{r.SpawnCode}", $"{r.Name} respawns",
                    Avion2.Analysis.ScheduleCategory.FieldBoss, [], 0, []),
                r.At));

    /// <summary>Rift/event timetable and its notifications.</summary>
    public TimerService Timers { get; }

    /// <summary>Overlay banner while the meter runs, otherwise a small toast in the corner.</summary>
    private void OnEventDue(Avion2.Analysis.Occurrence occurrence)
    {
        var minutes = Math.Max(1, (int)Math.Round((occurrence.StartsAt - DateTimeOffset.Now).TotalMinutes));
        string when = occurrence.StartsAt.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        string text = $"{occurrence.Event.Name} in {minutes} min ({when})";

        if (Settings.NotifySound)
        {
            NoticeSound().Play();
        }

        Notices.Notify(text, CategoryLabel(occurrence.Event.Category));
    }

    private NotificationWindow? _notices;

    /// <summary>The free-floating notification area (created on first use, lives as long as the app).</summary>
    public NotificationWindow Notices => _notices ??= new NotificationWindow(this);

    private AlertSound? _notifySound;

    /// <summary>The notification sound, rebuilt when its file changed (separate from the low-HP alert).</summary>
    public AlertSound NoticeSound()
    {
        string? wanted = string.IsNullOrWhiteSpace(Settings.NoticeSound) || !File.Exists(Settings.NoticeSound)
            ? null
            : Path.GetFullPath(Settings.NoticeSound);
        if (_notifySound is null || _notifySound.CustomPath != wanted)
        {
            _notifySound?.Dispose();
            _notifySound = new AlertSound(Settings.NoticeSound, Settings.NoticeVolume, SoundKind.Notice);
        }

        _notifySound.Volume = Settings.NoticeVolume;
        return _notifySound;
    }

    public static string CategoryLabel(Avion2.Analysis.ScheduleCategory category) => category switch
    {
        Avion2.Analysis.ScheduleCategory.Rift => "Spacetime Rift",
        Avion2.Analysis.ScheduleCategory.BossRaid => "Boss raid",
        Avion2.Analysis.ScheduleCategory.FieldEvent => "Field event",
        Avion2.Analysis.ScheduleCategory.Pvp => "PvP",
        Avion2.Analysis.ScheduleCategory.FieldBoss => "Field boss",
        _ => "Reset",
    };

    /// <summary>Persisted settings (window placement etc.).</summary>
    public OverlaySettings Stored { get; }

    /// <summary>Bindable settings shared by the overlay and the Settings page.</summary>
    public OverlayViewModel Settings { get; }

    public HistoryStore History { get; }

    /// <summary>Saved fights, newest first.</summary>
    public ObservableCollection<HistoryRow> Fights { get; } = new();

    public MeterEngine? Engine { get; private set; }

    public MainWindow? Overlay { get; private set; }

    public bool IsRunning => Engine is not null;

    public bool IsReplay => _baseOptions.ReplayPath is not null;

    /// <summary>Where the name tables in use came from (built in, or an override folder).</summary>
    public GameDataSource? DataSource { get; private set; }

    public int SkillNames { get; private set; }

    public int NpcNames { get; private set; }

    public static string DefaultBackgroundVideo =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AVION2", "background.mp4");

    /// <summary>The home-screen video to play, or null for the drawn backdrop.</summary>
    public string? BackgroundVideo => Stored.BackgroundVideo switch
    {
        null => File.Exists(DefaultBackgroundVideo) ? DefaultBackgroundVideo : null,
        "" => null,
        var path => path,
    };

    /// <summary>null = default location, "" = no video, else a file.</summary>
    public void SetBackgroundVideo(string? path)
    {
        Stored.BackgroundVideo = path;
        SaveSettings();
        BackdropChanged?.Invoke();
    }

    public event Action? BackdropChanged;

    /// <summary>Raised when the meter starts or stops.</summary>
    public event Action? MeterStateChanged;

    /// <summary>Asks the home window to show a page ("home", "history", "settings").</summary>
    public event Action<string>? ShowPageRequested;

    public static bool NpcapInstalled =>
        File.Exists(Path.Combine(Environment.SystemDirectory, "Npcap", "wpcap.dll")) ||
        File.Exists(Path.Combine(Environment.SystemDirectory, "wpcap.dll"));

    public IReadOnlyList<CaptureDeviceInfo> Devices()
    {
        try
        {
            return CaptureDevices.List();
        }
        catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException or BadImageFormatException)
        {
            return [];
        }
    }

    public void RequestPage(string page) => ShowPageRequested?.Invoke(page);

    /// <summary>Starts capturing and shows the overlay. Returns an error message, or null.</summary>
    public string? StartMeter(OverlayTab startTab = OverlayTab.Damage)
    {
        if (Engine is not null)
        {
            return null;
        }

        if (!NpcapInstalled && !IsReplay)
        {
            return "Npcap is not installed. Get it from https://npcap.com/ and start the meter again.";
        }

        string? record = _baseOptions.RecordPath;
        if (record is null && Stored.RecordSessions)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVION2", "captures");
            record = Path.Combine(dir, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.pcap");
        }

        var engine = new MeterEngine(new EngineOptions
        {
            Devices = _baseOptions.Devices ?? (Stored.Device is { } device ? [device] : null),
            RecordPath = record,
            EventLogPath = _baseOptions.EventLogPath,
            DataDirectory = _baseOptions.DataDirectory,
            DataLanguage = Stored.DataLanguage,
            HistoryDirectory = _baseOptions.HistoryDirectory,
            SaveHistory = _baseOptions.SaveHistory,
            ReplayPath = _baseOptions.ReplayPath,
            ReplaySpeed = _baseOptions.ReplaySpeed,
        });
        engine.FightSaved += entry => Application.Current.Dispatcher.BeginInvoke(() => Fights.Insert(0, new HistoryRow(entry)));
        engine.LegionChanged += () =>
        {
            var legion = engine.Legion();
            Application.Current.Dispatcher.BeginInvoke(() => UpdateLegion(legion));
        };
        engine.Cast += (cast, info) =>
        {
            if (info is { IsBoss: true, IsDummy: false } && Settings.NotifyBossCasts)
            {
                Application.Current.Dispatcher.BeginInvoke(() => OnBossCast(cast, info));
            }
        };
        engine.FieldBossTable += table => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (FieldBosses.Apply(table) && PersistSettings)
            {
                FieldBosses.Save(FieldBossFile);
            }
        });

        Engine = engine;
        Overlay = new MainWindow(this, engine) { StartTab = startTab };
        Overlay.Show();
        MeterStateChanged?.Invoke();
        return null;
    }

    /// <summary>Stops capturing and closes the overlay (the running fight is saved).</summary>
    public void StopMeter()
    {
        if (Engine is null)
        {
            return;
        }

        var overlay = Overlay;
        Overlay = null;
        Engine = null;
        overlay?.Close(); // disposes the engine
        MeterStateChanged?.Invoke();
    }

    /// <summary>Called by the overlay when it closes on its own (e.g. Alt+F4).</summary>
    internal void OnOverlayClosed(MainWindow overlay)
    {
        if (Overlay != overlay)
        {
            return;
        }

        Overlay = null;
        Engine = null;
        MeterStateChanged?.Invoke();
    }

    public void DeleteFight(HistoryRow row)
    {
        History.Delete(row.Entry);
        Fights.Remove(row);
    }

    private static string LegionFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVION2", "legion.json");

    /// <summary>Our legion (name, leader, texts, members with last login); saved, so Home shows it between sessions.</summary>
    public Avion2.Analysis.LegionState? Legion { get; private set; } = LoadLegion();

    /// <summary>Raised on the UI thread when <see cref="Legion"/> changed.</summary>
    public event Action? LegionUpdated;

    private static Avion2.Analysis.LegionState? LoadLegion()
    {
        try
        {
            return File.Exists(LegionFile)
                ? System.Text.Json.JsonSerializer.Deserialize<Avion2.Analysis.LegionState>(File.ReadAllText(LegionFile))
                : null;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void UpdateLegion(Avion2.Analysis.LegionState? legion)
    {
        if (legion is null)
        {
            return;
        }

        // Keep what we knew if this session only sent part of it (e.g. a member page but no details).
        if (Legion is { } old)
        {
            legion.Name ??= old.Name;
            legion.Leader ??= old.Leader;
            if (legion.Texts.Count == 0)
            {
                legion.Texts = old.Texts;
            }

            var members = old.Members.ToDictionary(m => m.Name);
            foreach (var member in legion.Members)
            {
                members[member.Name] = member;
            }

            legion.Members = members.Values.ToList();
        }

        Legion = legion;
        LegionUpdated?.Invoke();
        if (PersistSettings)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LegionFile)!);
                File.WriteAllText(LegionFile, System.Text.Json.JsonSerializer.Serialize(legion));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // shown again next session once the game resends it
            }
        }
    }

    private readonly Dictionary<(uint Boss, uint Skill), DateTime> _bossCasts = new();

    /// <summary>A boss started a named skill: a notice (no sound), at most once per skill per 10 s.</summary>
    private void OnBossCast(Avion2.Protocol.SkillCastEvent cast, Avion2.Analysis.EntityInfo boss)
    {
        string skill = Data.SkillName(cast.SkillId);
        if (skill.StartsWith("Skill ", StringComparison.Ordinal) || skill.Contains("Basic Attack", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var key = (cast.ActorId, cast.SkillId);
        if (_bossCasts.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(10))
        {
            return;
        }

        _bossCasts[key] = DateTime.UtcNow;
        string name = boss.NpcCode is { } code && Data.Npcs.TryGetValue(code, out var npc) ? npc.Name : "Boss";
        Notices.Notify($"{name}: {skill}", "Boss skill");
    }

    /// <summary>The trace (timeline, buffs, replay) of a saved fight, from memory if this session saved it.</summary>
    public Avion2.Analysis.FightTrace? LoadTrace(HistoryEntry entry) => Engine?.Trace(entry) ?? History.LoadTrace(entry);

    /// <summary>Switches the app theme for every open window and remembers it.</summary>
    public void SetTheme(string id)
    {
        Stored.Theme = ThemeManager.Find(id).Id;
        SaveSettings();
        ThemeManager.Apply(Stored.Theme);
        BackdropChanged?.Invoke(); // the VOID theme has no video backdrop
    }

    /// <summary>Switches the language of the built-in names (in the running meter too).</summary>
    public void SetDataLanguage(string language)
    {
        Stored.DataLanguage = language;
        SaveSettings();
        Engine?.ReloadGameData(language, _baseOptions.DataDirectory);
        RefreshGameDataInfo();
    }

    private void RefreshGameDataInfo()
    {
        var (data, source) = MeterEngine.FindGameData(_baseOptions.DataDirectory, Stored.DataLanguage);
        DataSource = source;
        Data = data;
        DetailView.Names = data;
        SkillNames = data.Skills.Count;
        NpcNames = data.Npcs.Count;
    }

    private void LoadSettings()
    {
        var s = Stored;
        Settings.BackgroundOpacity = s.BackgroundOpacity;
        Settings.AllTargets = s.AllTargets;
        Settings.Locked = s.Locked;
        Settings.LowHpAlert = s.LowHpAlert;
        Settings.AlertVolume = s.LowHpVolume;
        Settings.LowHpThreshold = s.LowHpThreshold;
        Settings.LowHpSound = string.IsNullOrWhiteSpace(s.LowHpSound) ? null : s.LowHpSound;
        Settings.AutoStart = s.AutoStart;
        Settings.CompactRows = s.CompactRows;
        Settings.Region = s.Region;
        Settings.NotifyLeadMinutes = s.NotifyLeadMinutes;
        Settings.NotifyRift = s.NotifyRift;
        Settings.NotifyBossRaid = s.NotifyBossRaid;
        Settings.NotifyFieldEvents = s.NotifyFieldEvents;
        Settings.NotifyPvp = s.NotifyPvp;
        Settings.NotifyReset = s.NotifyReset;
        Settings.NotifyFieldBoss = s.NotifyFieldBoss;
        Settings.NotifyBossCasts = s.NotifyBossCasts;
        Settings.NotifySound = s.NotifySound;
        Settings.NoticeSound = string.IsNullOrWhiteSpace(s.NoticeSound) ? null : s.NoticeSound;
        Settings.NoticeVolume = s.NoticeVolume;
    }

    /// <summary>False for developer render runs, so they never overwrite the user's settings.</summary>
    public bool PersistSettings { get; set; } = true;

    public void SaveSettings()
    {
        if (!PersistSettings)
        {
            return;
        }

        var s = Stored;
        s.BackgroundOpacity = Settings.BackgroundOpacity;
        s.AllTargets = Settings.AllTargets;
        s.Locked = Settings.Locked;
        s.LowHpAlert = Settings.LowHpAlert;
        s.LowHpVolume = Settings.AlertVolume;
        s.LowHpThreshold = Settings.LowHpThreshold;
        s.LowHpSound = Settings.LowHpSound;
        s.AutoStart = Settings.AutoStart;
        s.CompactRows = Settings.CompactRows;
        s.Region = Settings.Region;
        s.NotifyLeadMinutes = Settings.NotifyLeadMinutes;
        s.NotifyRift = Settings.NotifyRift;
        s.NotifyBossRaid = Settings.NotifyBossRaid;
        s.NotifyFieldEvents = Settings.NotifyFieldEvents;
        s.NotifyPvp = Settings.NotifyPvp;
        s.NotifyReset = Settings.NotifyReset;
        s.NotifyFieldBoss = Settings.NotifyFieldBoss;
        s.NotifyBossCasts = Settings.NotifyBossCasts;
        s.NotifySound = Settings.NotifySound;
        s.NoticeSound = Settings.NoticeSound;
        s.NoticeVolume = Settings.NoticeVolume;
        s.Save();
    }
}
