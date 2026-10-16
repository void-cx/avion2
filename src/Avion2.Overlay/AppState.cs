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
        // --history-dir (replays for UI work) keeps the runs beside those fights, away from the real ones
        RunLog = new RunStore(baseOptions.HistoryDirectory is { } dir
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dir)) ?? dir, "runs.json")
            : null);
        if (baseOptions.ReplayPath is null)
        {
            CleanUpStorage();
        }

        foreach (var entry in History.Load(300))
        {
            Fights.Add(new HistoryRow(entry));
        }

        RefreshGameDataInfo();
        foreach (var run in RunLog.Runs)
        {
            Runs.Add(new RunRow(run, Data));
        }

        FieldBosses = Avion2.Analysis.FieldBossBoard.Load(FieldBossFile);
        Timers = new TimerService(Settings, FieldBossOccurrences);
        Timers.Due += OnEventDue;
        Cubes = new CubeFinder(this);
    }

    /// <summary>Find My Cube: the compass and what it leads to (runs with the capture, not with the Map page).</summary>
    public CubeFinder Cubes { get; }

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
        Avion2.Analysis.ScheduleCategory.WorldBoss => "World boss",
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

    /// <summary>Saved dungeon runs and what was learned about maps.</summary>
    public RunStore RunLog { get; }

    /// <summary>Saved dungeon runs, newest first.</summary>
    public ObservableCollection<RunRow> Runs { get; } = new();

    /// <summary>The run in progress (inside a dungeon, expedition or other instance), or null.</summary>
    public Avion2.Analysis.DungeonRun? LiveRun { get; private set; }

    /// <summary>The live run started, changed or ended.</summary>
    public event Action? LiveRunChanged;

    /// <summary>Names the place of a run (every run on that map); empty clears the name.</summary>
    public void RenameRuns(RunRow row, string? name)
    {
        RunLog.Rename(row.Run.FirstMap, name);
        for (int i = 0; i < Runs.Count; i++)
        {
            if (Runs[i].Run.FirstMap == row.Run.FirstMap)
            {
                Runs[i].Run.CustomName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
                Runs[i] = new RunRow(Runs[i].Run, Data);
            }
        }
    }

    public void DeleteRuns(IReadOnlyCollection<RunRow> rows)
    {
        RunLog.Delete(rows.Select(r => r.Run.Id));
        foreach (var row in rows)
        {
            Runs.Remove(row);
        }
    }

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
            record = Path.Combine(Recordings.Directory, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.pcap");
        }

        CurrentRecording = record;
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
            AnonymizeNames = _baseOptions.AnonymizeNames,
            Runs = _baseOptions.SaveHistory ? RunLog : null,
        });
        engine.RunChanged += run => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            LiveRun = run;
            LiveRunChanged?.Invoke();
        });
        engine.RunEnded += (run, saved) => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            LiveRun = null;
            if (saved)
            {
                Runs.Insert(0, new RunRow(run, Data));
            }

            LiveRunChanged?.Invoke();
        });
        engine.FightSaved += entry => Application.Current.Dispatcher.BeginInvoke(() => Fights.Insert(0, new HistoryRow(entry)));
        engine.LegionChanged += () =>
        {
            var legion = engine.Legion();
            Application.Current.Dispatcher.BeginInvoke(() => UpdateLegion(legion));
        };
        engine.TeachCubeCodes(CubeLog.Codes);
        engine.CubeSeen += cube => Application.Current.Dispatcher.BeginInvoke(() => OnCube(cube, engine));
        engine.InventoryChanged += () =>
        {
            var inventory = engine.Inventory();
            Application.Current.Dispatcher.BeginInvoke(() => UpdateInventory(inventory));
        };
        engine.HitTaken += hit =>
        {
            if (Hud is not null)
            {
                Application.Current.Dispatcher.BeginInvoke(() => Hud?.AddHit(hit));
            }
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
        Overlay.Run();
        if (Stored.OverlayAutoShow == "start")
        {
            Overlay.Show();
        }

        UpdateHud();
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
        overlay?.Stop(); // disposes the engine
        UpdateHud();
        MeterStateChanged?.Invoke();
    }

    /// <summary>The overlay is on screen (the meter may run with it hidden).</summary>
    public bool OverlayVisible => Overlay?.IsVisible == true;

    /// <summary>Shows or hides the DPS overlay; the capture keeps running either way.</summary>
    public void SetOverlayVisible(bool visible)
    {
        if (Overlay is { } overlay)
        {
            if (visible)
            {
                overlay.Show();
                TopmostKeeper.Raise(overlay);
            }
            else
            {
                overlay.Hide();
            }
        }

        MeterStateChanged?.Invoke();
    }

    /// <summary>Called by the overlay when it closes (only when the meter is stopped).</summary>
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

    /// <summary>The .pcap being written by the running meter (never deleted), or null.</summary>
    public string? CurrentRecording { get; private set; }

    /// <summary>The automatic cleanup at start: fights and recordings older than the kept number of days.</summary>
    private void CleanUpStorage()
    {
        if (Stored.KeepFightsDays > 0)
        {
            History.DeleteOlderThan(DateTime.Now.AddDays(-Stored.KeepFightsDays));
        }

        if (Stored.KeepRecordingsDays > 0)
        {
            Recordings.Delete(DateTime.Now.AddDays(-Stored.KeepRecordingsDays));
        }
    }

    /// <summary>Deletes saved fights (with replays) older than <paramref name="days"/> days; returns how many.</summary>
    public int DeleteFightsOlderThan(int days)
    {
        int deleted = History.DeleteOlderThan(DateTime.Now.AddDays(-days));
        foreach (var gone in Fights.Where(f => f.Entry.Path.Length > 0 && !File.Exists(f.Entry.Path)).ToList())
        {
            Fights.Remove(gone);
        }

        return deleted;
    }

    /// <summary>Deletes all recordings except the one being written; returns how many.</summary>
    public int DeleteRecordings() => Recordings.Delete(inUse: Engine is not null ? CurrentRecording : null);

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

    private static string CubeFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVION2", "cubes.json");

    /// <summary>Every hidden cube the game showed us (exact positions), and the ones we opened.</summary>
    public Avion2.Analysis.CubeLog CubeLog { get; } = LoadCubes();

    /// <summary>The HUD effects window while the meter runs and HUD effects are on.</summary>
    public HudWindow? Hud { get; private set; }

    /// <summary>Raised after a HUD calibration was saved or HUD effects were switched on or off.</summary>
    public event Action? HudChanged;

    public void SetHudEffects(bool enabled)
    {
        Stored.HudEffects = enabled;
        SaveSettings();
        UpdateHud();
        HudChanged?.Invoke();
    }

    /// <summary>Opens or closes the HUD effects window to match the settings and the meter.</summary>
    private void UpdateHud()
    {
        bool wanted = Stored.HudEffects && Engine is not null;
        if (wanted && Hud is null)
        {
            var engine = Engine!;
            Hud = new HudWindow(this, () => Engine == engine ? engine.Status() : null,
                () => Engine == engine ? engine.Snapshot(Settings.Scope) : null);
            Hud.Show();
        }
        else if (!wanted && Hud is not null)
        {
            Hud.Close();
            Hud = null;
        }
    }

    public void SaveHudLayout(Avion2.Analysis.HudLayout layout)
    {
        Stored.HudLayouts[Avion2.Analysis.HudLayout.Key(layout.Width, layout.Height)] = layout;
        SaveSettings();
        Hud?.Relayout();
        HudChanged?.Invoke();
    }

    /// <summary>
    /// Takes one picture of the game for HUD calibration: waits (up to 20 s) until AION2 is in front, hides AVION2's
    /// own overlays for a moment, copies the game's area of the screen and opens the calibration window.
    /// </summary>
    public async Task CalibrateHudAsync(Window owner)
    {
        if (GameWindow.Handle == 0)
        {
            MessageBox.Show(owner, "AION2 is not running. Start the game, log in, then calibrate.", "HUD calibration",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(owner,
                "Click OK, then switch to AION2. As soon as the game is in front, AVION2 takes one picture of it to find your HUD " +
                "(you have 20 seconds). The picture is only used for the calibration and is not saved.\n\n" +
                "Tip: do it at full HP and with nothing covering the bottom of the screen.",
                "HUD calibration", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
        {
            return;
        }

        var until = DateTime.UtcNow.AddSeconds(20);
        while (!GameWindow.IsForeground && DateTime.UtcNow < until)
        {
            await Task.Delay(200);
        }

        if (!GameWindow.IsForeground)
        {
            MessageBox.Show(owner, "AION2 was not brought to the front within 20 seconds. Try again.", "HUD calibration",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Our own windows must not be in the picture
        if (Hud is { } hud)
        {
            hud.Suspended = true;
        }

        var hidden = new List<Window>();
        foreach (Window window in Application.Current.Windows)
        {
            if (window.IsVisible && window != owner && window.Topmost)
            {
                window.Hide();
                hidden.Add(window);
            }
        }

        await Task.Delay(700); // let the game redraw and the window animations finish
        var picture = GameWindow.Capture();
        foreach (var window in hidden)
        {
            if (window != Hud)
            {
                window.Show();
            }
        }

        if (Hud is { } shown)
        {
            shown.Suspended = false;
        }

        if (picture is null)
        {
            MessageBox.Show(owner, "The game's picture could not be taken. Is AION2 in borderless or windowed mode?", "HUD calibration",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var calibration = new HudCalibrationWindow(this, picture);
        calibration.Show();
        calibration.Activate();
    }

    /// <summary>Raised when "Find My Cube" was switched on or off.</summary>
    public event Action? CubeFinderChanged;

    public void SetCubeFinder(bool enabled)
    {
        Stored.CubeFinder = enabled;
        SaveSettings();
        CubeFinderChanged?.Invoke();
    }

    /// <summary>Raised on the UI thread when a cube was seen or opened (and found markers may have changed).</summary>
    public event Action<Avion2.Analysis.CubeSighting>? CubeUpdated;

    private static Avion2.Analysis.CubeLog LoadCubes()
    {
        try
        {
            var log = File.Exists(CubeFile)
                ? System.Text.Json.JsonSerializer.Deserialize<Avion2.Analysis.CubeLog>(File.ReadAllText(CubeFile)) ?? new()
                : new();

            // Entries from before spot ids existed
            log.Cubes = log.Cubes
                .Select(c => c.Id is null ? c with { Id = Avion2.Analysis.CubeSighting.SpotId(c.MapId, c.Position) } : c)
                .ToList();
            return log;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    /// <summary>A cube came into range, or was opened by us or someone else: keep the spot and what happened there.</summary>
    private void OnCube(Avion2.Analysis.CubeSighting cube, MeterEngine engine)
    {
        if (!Stored.CubeFinder)
        {
            return; // opted out: no cube positions are kept
        }

        var stored = CubeLog.Merge(cube);
        CubeLog.Codes = engine.CubeCodes().ToList();

        if (PersistSettings)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CubeFile)!);
                File.WriteAllText(CubeFile, System.Text.Json.JsonSerializer.Serialize(CubeLog));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // kept in memory; the next cube saves again
            }
        }

        CubeUpdated?.Invoke(stored);
    }

    private static string InventoryFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVION2", "inventory.json");

    /// <summary>Our inventory: live while the meter runs, otherwise as last seen (saved between sessions).</summary>
    public Avion2.Analysis.InventoryState? Inventory { get; private set; } = LoadInventory();

    /// <summary>Raised on the UI thread when <see cref="Inventory"/> changed.</summary>
    public event Action? InventoryUpdated;

    private static Avion2.Analysis.InventoryState? LoadInventory()
    {
        try
        {
            return File.Exists(InventoryFile)
                ? System.Text.Json.JsonSerializer.Deserialize<Avion2.Analysis.InventoryState>(File.ReadAllText(InventoryFile))
                : null;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private DateTime _inventorySaved;
    private bool _inventoryDirty;

    private void UpdateInventory(Avion2.Analysis.InventoryState inventory)
    {
        // Slot updates before this session's full list only make sense on top of the saved one
        if (!inventory.IsKnown)
        {
            if (Inventory is null)
            {
                return;
            }

            var merged = Inventory.Copy();
            foreach (var item in inventory.Items)
            {
                merged.Apply(new Avion2.Protocol.InventoryItemEvent(inventory.UpdatedMs, item));
            }

            inventory = merged;
        }

        Inventory = inventory;
        InventoryUpdated?.Invoke();

        // Loot changes counts every few seconds: write at most every 10 s (SaveSettings writes the rest on exit)
        _inventoryDirty = true;
        if (DateTime.UtcNow - _inventorySaved > TimeSpan.FromSeconds(10))
        {
            SaveInventory();
        }
    }

    private void SaveInventory()
    {
        if (!PersistSettings || !_inventoryDirty || Inventory is null)
        {
            return;
        }

        _inventorySaved = DateTime.UtcNow;
        _inventoryDirty = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(InventoryFile)!);
            File.WriteAllText(InventoryFile, System.Text.Json.JsonSerializer.Serialize(Inventory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _inventoryDirty = true; // next change tries again
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
        if (Hud?.ShowCast(name, skill) == true)
        {
            return; // shown under the game's target frame
        }

        Notices.Notify($"{name}: {skill}", "Boss skill");
    }

    /// <summary>A newer release than this build, once found (null: none, not checked, or a dev build).</summary>
    public ReleaseInfo? AvailableUpdate { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="AvailableUpdate"/> changed.</summary>
    public event Action? UpdateAvailableChanged;

    /// <summary>Asks GitHub for the latest release. Returns a short status for "Check now".</summary>
    public async Task<string> CheckForUpdatesAsync()
    {
        string current = UpdateChecker.CurrentVersion;
        if (UpdateChecker.IsDevBuild(current))
        {
            return $"This is a development build ({current}); it does not update itself.";
        }

        var latest = await UpdateChecker.LatestAsync();
        if (latest is null)
        {
            return "Could not reach GitHub (or no release yet).";
        }

        if (!UpdateChecker.IsNewer(latest, current))
        {
            return $"Up to date (v{current}).";
        }

        AvailableUpdate = latest;
        UpdateAvailableChanged?.Invoke();
        return $"{latest.Tag} is available.";
    }

    /// <summary>
    /// Hands over to AVION2-Updater.exe (next to this exe) and closes AVION2: the updater waits for us to exit,
    /// installs the release and restarts AVION2. Returns an error message, or null when the update started.
    /// </summary>
    public string? StartUpdate()
    {
        if (AvailableUpdate is not { } release)
        {
            return "No update available.";
        }

        string dir = AppContext.BaseDirectory;
        string updater = Path.Combine(dir, "AVION2-Updater.exe");
        if (!File.Exists(updater))
        {
            return "AVION2-Updater.exe is missing next to AVION2.exe. Download the release from GitHub instead.";
        }

        SaveSettings();
        StopMeter();
        var start = new System.Diagnostics.ProcessStartInfo(updater) { UseShellExecute = false };
        foreach (string arg in new[] { "--dir", dir.TrimEnd(Path.DirectorySeparatorChar), "--zip", release.ZipUrl, "--version", release.Tag,
                     "--pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) })
        {
            start.ArgumentList.Add(arg);
        }

        if (release.ChecksumsUrl is { } sums)
        {
            start.ArgumentList.Add("--sums");
            start.ArgumentList.Add(sums);
        }

        System.Diagnostics.Process.Start(start);
        Application.Current.Shutdown();
        return null;
    }

    /// <summary>Removes the *.old files an update leaves behind (they were still in use while it ran).</summary>
    public static void CleanUpAfterUpdate()
    {
        try
        {
            foreach (string old in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.old", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(old);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // still locked; next start
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
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
        Settings.NotifyWorldBoss = s.NotifyWorldBoss;
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

        SaveInventory();
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
        s.NotifyWorldBoss = Settings.NotifyWorldBoss;
        s.NotifyBossCasts = Settings.NotifyBossCasts;
        s.NotifySound = Settings.NotifySound;
        s.NoticeSound = Settings.NoticeSound;
        s.NoticeVolume = Settings.NoticeVolume;
        s.Save();
    }
}
