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
public sealed partial class AppState
{
    private readonly EngineOptions _baseOptions;

    public AppState(EngineOptions baseOptions)
    {
        _baseOptions = baseOptions;
        Stored = OverlaySettings.Load();
        Inventory = Inventories.Latest;
        foreach (var known in new CharacterStore().Characters)
        {
            _ourNames.Add(known.Name);
        }

        _playing = new CharacterStore().Characters.FirstOrDefault()?.Name ?? Inventory?.Character;
        // the last character we played is "You" until the meter sees which one it is today
        StreamerMode.Names.Self = new CharacterStore().Characters.FirstOrDefault()?.Name;
        StreamerMode.Set(Stored.HideNames);
        Portraits.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() => PortraitsChanged?.Invoke());
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
        WorldEvents = Avion2.Analysis.WorldEventBoard.Load(WorldEventFile);
        Timers = new TimerService(Settings, FieldBossOccurrences, WorldEvents);
        Timers.Due += OnEventDue;
        Cubes = new CubeFinder(this);
        Ceilings = new RewardCeilings(this, () => KeepsGameState);
        Crafts = new CraftJournal(this, () => KeepsGameState);
        Obs = new ObsOverlay(this);
        Obs.Apply();
        Discord = new DiscordFeed(this);
        Discord.Apply();
        Armory.Loaded += OnArmoryProfile;
        foreach (var known in OurCharacters)
        {
            RefreshPortrait(known);
        }
    }

    /// <summary>Discord Rich Presence (Settings, General): our status in the Discord app on this PC.</summary>
    public DiscordFeed Discord { get; }

    /// <summary>The OBS overlay (Settings, General): a page on localhost for an OBS Browser Source.</summary>
    public ObsOverlay Obs { get; }

    /// <summary>Find My Cube: the compass and what it leads to (runs with the capture, not with the Map page).</summary>
    public CubeFinder Cubes { get; }

    private static string FieldBossFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVION2", "field_bosses.json");

    private static string WorldEventFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVION2", "world_events.json");

    /// <summary>The announced Airspace Battle (saved) and the field events open now, for the Timers page.</summary>
    public Avion2.Analysis.WorldEventBoard WorldEvents { get; }

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

        Notices.Notify(text, CategoryLabel(occurrence.Event.Category), BossImages.ForTimer(occurrence.Event.Key, Data));
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

    /// <summary>The guaranteed rewards of the expedition reward cubes, per character.</summary>
    public RewardCeilings Ceilings { get; }

    /// <summary>Our crafts per character, and the pet-soul notices.</summary>
    public CraftJournal Crafts { get; }

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

    /// <summary>
    /// Finds the runs in the saved recordings (Record session) and saves them; the Runs page then shows them.
    /// Runs already saved are replaced, not doubled. Reports (file, files) while it reads.
    /// </summary>
    public async Task<RunImporter.Result> ImportRunsAsync(Action<int, int> progress)
    {
        var files = Recordings.Paths(Engine is not null ? CurrentRecording : null);
        var data = Data;
        var dispatcher = Application.Current.Dispatcher;
        var result = await Task.Run(() => RunImporter.Import(files, data, RunLog,
            (done, total) => dispatcher.BeginInvoke(() => progress(done, total))));
        Runs.Clear();
        foreach (var run in RunLog.Runs)
        {
            Runs.Add(new RunRow(run, Data));
        }

        return result;
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

    /// <summary>Characters from the official AION2 site, looked up on demand.</summary>
    public ArmoryClient Armory { get; } = new();

    /// <summary>Our characters' portraits from the official AION2 site, kept on this PC.</summary>
    public PortraitStore Portraits { get; } = new();

    /// <summary>Raised on the UI thread when one of our characters' portraits was saved or changed.</summary>
    public event Action? PortraitsChanged;

    /// <summary>One of our characters' saved portrait as an image; null when there is none, and in streamer mode.</summary>
    public System.Windows.Media.ImageSource? PortraitOf(string name, ushort serverId) =>
        StreamerMode.On ? null : PortraitImages.Load(Portraits.PicturePath(name, serverId));

    private static readonly Lazy<System.Net.Http.HttpClient> PortraitHttp = new(() =>
    {
        var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AVION2");
        return http;
    });

    /// <summary>
    /// Fetches the portrait of a character we play from the official site, in the background, when there is none on
    /// this PC or it is a week old. Not for replays or anonymized runs.
    /// </summary>
    private void RefreshPortrait(Avion2.Analysis.KnownCharacter character)
    {
        if (IsReplay || _baseOptions.AnonymizeNames || character.ServerId == 0 || !Portraits.NeedsRefresh(character.Name, character.ServerId))
        {
            return;
        }

        _ = Portraits.RefreshAsync(character.Name, character.ServerId,
            async (name, server) =>
            {
                var lookup = await Armory.LoadAsync(name, server).ConfigureAwait(false);
                return (lookup.Profile is not null, lookup.Profile?.ImageUrl);
            },
            url => PortraitHttp.Value.GetByteArrayAsync(url));
    }

    /// <summary>Raised to open a character on the Characters page (name, server id).</summary>
    public event Action<string, ushort>? ShowCharacterRequested;

    public void ShowCharacter(string name, ushort serverId) => ShowCharacterRequested?.Invoke(name, serverId);

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
            RecordGameProcess = Stored.RecordAllGameConnections,
            EventLogPath = _baseOptions.EventLogPath,
            DataDirectory = _baseOptions.DataDirectory,
            DataLanguage = Stored.DataLanguage,
            HistoryDirectory = _baseOptions.HistoryDirectory,
            SaveHistory = _baseOptions.SaveHistory,
            ReplayPath = _baseOptions.ReplayPath,
            ReplaySpeed = _baseOptions.ReplaySpeed,
            ReplayFromMs = _baseOptions.ReplayFromMs,
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
        engine.CharacterIdentified += character => Application.Current.Dispatcher.BeginInvoke(() => Identified(character));
        engine.LegionChanged += () =>
        {
            var legion = engine.Legion();
            Application.Current.Dispatcher.BeginInvoke(() => UpdateLegion(legion));
        };
        Ceilings.Attach(engine);
        Crafts.Attach(engine);
        AttachLegionsSeen(engine);
        engine.TeachCubeCodes(CubeLog.Codes);
        engine.CubeSeen += cube => Application.Current.Dispatcher.BeginInvoke(() => OnCube(cube, engine));
        engine.InventoryChanged += () =>
        {
            var inventory = engine.Inventory();
            Application.Current.Dispatcher.BeginInvoke(() => UpdateInventory(inventory));
        };
        engine.ProgressChanged += () =>
        {
            var progress = engine.Progress();
            Application.Current.Dispatcher.BeginInvoke(() => UpdateProgress(progress));
        };
        engine.PetAcquired += pet => Application.Current.Dispatcher.BeginInvoke(() =>
            Notices.Notify($"New pet: {PetName(pet.PetId)}", "Pet acquired"));
        engine.HitTaken += hit =>
        {
            if (Hud is not null)
            {
                Application.Current.Dispatcher.BeginInvoke(() => Hud?.AddHit(hit));
            }
        };
        engine.Gathering += e => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (e is Avion2.Protocol.GatherStartEvent start)
            {
                Hud?.ShowGather(start);
            }
            else if (e is Avion2.Protocol.CraftStartEvent craft)
            {
                Hud?.ShowCraft(craft);
            }
            else
            {
                Hud?.HideGather();
            }
        });
        engine.Cast += (cast, info) =>
        {
            if (info is { IsBoss: true, IsDummy: false } && Settings.NotifyBossCasts)
            {
                Application.Current.Dispatcher.BeginInvoke(() => OnBossCast(cast, info));
            }
        };
        engine.FinderAlert += alert => Application.Current.Dispatcher.BeginInvoke(() => OnFinderAlert(alert));
        engine.FieldBossTable += table => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (FieldBosses.Apply(table) && KeepsGameState)
            {
                FieldBosses.Save(FieldBossFile);
            }
        });
        engine.WorldEvent += e => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (WorldEvents.Apply(e) && KeepsGameState)
            {
                WorldEvents.Save(WorldEventFile);
            }
        });

        Engine = engine;
        _screen = record is not null && Stored.RecordScreen && !IsReplay ? new ScreenRecorder(record) : null;
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
        StopScreen();
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
        StopScreen();
        MeterStateChanged?.Invoke();
    }

    /// <summary>Pictures of the game window while recording (Developer setting), or null.</summary>
    private ScreenRecorder? _screen;

    private void StopScreen()
    {
        _screen?.Dispose();
        _screen = null;
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
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVION2", "legions.json");

    /// <summary>Each character's legion (name, leader, texts, members with last login); saved, so Home shows it between sessions.</summary>
    public Avion2.Analysis.LegionBook Legions { get; } = LoadLegions();

    /// <summary>The legion of the character the pages show.</summary>
    public Avion2.Analysis.LegionState? Legion => Legions.For(ViewedCharacter);

    /// <summary>Raised on the UI thread when a legion changed.</summary>
    public event Action? LegionUpdated;

    private static Avion2.Analysis.LegionBook LoadLegions()
    {
        // (legion.json of AVION2 before 0.4.15 did not say whose legion it was: the game sends it again on the next teleport)
        try
        {
            return File.Exists(LegionFile)
                ? System.Text.Json.JsonSerializer.Deserialize<Avion2.Analysis.LegionBook>(File.ReadAllText(LegionFile)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    private void UpdateLegion(Avion2.Analysis.LegionState? legion)
    {
        if (legion is null || !Legions.Take(legion))
        {
            return;
        }

        LegionUpdated?.Invoke();
        if (KeepsGameState)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LegionFile)!);
                File.WriteAllText(LegionFile, System.Text.Json.JsonSerializer.Serialize(Legions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // shown again next session once the game resends it
            }
        }
    }

    private string? _playing;
    private string? _viewing;
    private readonly HashSet<string> _ourNames = new(StringComparer.Ordinal);

    /// <summary>The character in game, or the one played last while the meter has not seen one.</summary>
    public string? PlayingCharacter => _playing;

    /// <summary>
    /// The character the pages show (gear, fights, runs, legion): the one in game unless another was picked. Only
    /// what is shown changes; the meter keeps saving everything for the character actually played.
    /// </summary>
    public string? ViewedCharacter => _viewing ?? _playing;

    /// <summary>True while the pages show another character than the one in game.</summary>
    public bool ViewsOther => _viewing is not null;

    /// <summary>Raised on the UI thread when <see cref="ViewedCharacter"/> changed (picked, or another one in game).</summary>
    public event Action? ViewedCharacterChanged;

    /// <summary>Our characters, played last first.</summary>
    public IReadOnlyList<Avion2.Analysis.KnownCharacter> OurCharacters => new CharacterStore().Characters;

    /// <summary>Shows another character's data; null or the one in game follows the game again.</summary>
    public void View(string? name)
    {
        string? viewing = name is null || name == _playing ? null : name;
        if (viewing == _viewing)
        {
            return;
        }

        _viewing = viewing;
        ViewedCharacterChanged?.Invoke();
    }

    /// <summary>The inventory of the character the pages show.</summary>
    public Avion2.Analysis.InventoryState? ViewedInventory => _viewing is { } name ? Inventories.For(name) : Inventory;

    /// <summary>
    /// Whether something of a character (a fight's "you", a run) belongs on the pages: the viewed character's, and
    /// anything whose character is unknown or not one of ours (older saves).
    /// </summary>
    public bool Shows(string? character) =>
        character is null || ViewedCharacter is null || character == ViewedCharacter || !_ourNames.Contains(character);

    /// <summary>The fights of the character the pages show (newest first).</summary>
    public IEnumerable<HistoryRow> ViewedFights => Fights.Where(f => Shows(f.Entry.Snapshot.Self?.Name));

    private void Identified(Avion2.Analysis.KnownCharacter character)
    {
        _ourNames.Add(character.Name);
        RefreshPortrait(character);
        if (character.Name == _playing)
        {
            return;
        }

        _playing = character.Name;
        if (_viewing == _playing)
        {
            _viewing = null;
        }

        ViewedCharacterChanged?.Invoke();
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

        // The game is in front and Windows does not let us take the focus from it: show the editor above the game
        // (topmost) so it is not hidden behind it; a click gives it the focus.
        var calibration = new HudCalibrationWindow(this, picture) { Topmost = true };
        calibration.Show();
        TopmostKeeper.Raise(calibration);
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

        if (KeepsGameState)
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

    private static string InventoryDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVION2");

    private static string InventoryFile => Path.Combine(InventoryDirectory, "inventories.json");

    /// <summary>Every character's inventory as last seen (saved between sessions).</summary>
    public Avion2.Analysis.InventoryBook Inventories { get; } = LoadInventories();

    /// <summary>The inventory of the character played now (live while the meter runs), else of the one played last.</summary>
    public Avion2.Analysis.InventoryState? Inventory { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="Inventory"/> changed.</summary>
    public event Action? InventoryUpdated;

    private static Avion2.Analysis.InventoryBook LoadInventories()
    {
        try
        {
            if (File.Exists(InventoryFile))
            {
                return System.Text.Json.JsonSerializer.Deserialize<Avion2.Analysis.InventoryBook>(File.ReadAllText(InventoryFile)) ?? new();
            }

            // AVION2 before 0.4.15 kept one inventory (the character played last) in inventory.json
            var book = new Avion2.Analysis.InventoryBook();
            string old = Path.Combine(InventoryDirectory, "inventory.json");
            if (File.Exists(old) && System.Text.Json.JsonSerializer.Deserialize<Avion2.Analysis.InventoryState>(File.ReadAllText(old)) is { } single)
            {
                book.Add(single);
            }

            return book;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    private DateTime _inventorySaved;
    private bool _inventoryDirty;

    private void UpdateInventory(Avion2.Analysis.InventoryState inventory)
    {
        // Each character keeps its own; slot updates wait until we know whose inventory they change
        if (Inventories.Take(inventory) is not { } shown)
        {
            return;
        }

        Inventory = shown;
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
        if (!KeepsGameState || !_inventoryDirty)
        {
            return;
        }

        _inventorySaved = DateTime.UtcNow;
        _inventoryDirty = false;
        try
        {
            Directory.CreateDirectory(InventoryDirectory);
            File.WriteAllText(InventoryFile, System.Text.Json.JsonSerializer.Serialize(Inventories));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _inventoryDirty = true; // next change tries again
        }
    }

    private static string ProgressFile => Path.Combine(InventoryDirectory, "progress.json");

    /// <summary>Each character's progress (item level, Kinah and Abyss Points caps, tickets, Daeva Pass, pet souls); saved.</summary>
    public Avion2.Analysis.ProgressBook Progresses { get; } = LoadProgress();

    /// <summary>
    /// The progress of the character the pages show: a picked character's, else the one the meter is following (named
    /// by the game in this session; the last one played before that).
    /// </summary>
    public Avion2.Analysis.CharacterProgress? Progress =>
        _viewing is { } name ? Progresses.For(name) : Progresses.For(_progressLive ?? _playing);

    /// <summary>The character the meter's progress belongs to, once the game named it.</summary>
    private string? _progressLive;

    /// <summary>Raised on the UI thread when a character's progress changed.</summary>
    public event Action? ProgressUpdated;

    /// <summary>A pet's name, from the soul item it drops ("Soul: Zelophi (Bound)"), once AVION2 has seen one land in the bag.</summary>
    public string PetName(uint pet)
    {
        string? item = Progresses.SoulItem(pet) is { } soul ? BundledGameData.ItemName(soul) : null;
        if (item is null)
        {
            return $"Pet {pet}";
        }

        int colon = item.IndexOf(':');
        string name = colon >= 0 ? item[(colon + 1)..].Trim() : item;
        return name.EndsWith(" (Bound)", StringComparison.Ordinal) ? name[..^" (Bound)".Length] : name;
    }

    /// <summary>An NPC's name in the app's language (English when that table lacks it), or null.</summary>
    public string? NpcName(uint code) =>
        Data.Npcs.TryGetValue(code, out var npc) && !string.IsNullOrWhiteSpace(npc.Name) ? npc.Name : BundledGameData.EnglishNpcName(code);

    private static Avion2.Analysis.ProgressBook LoadProgress()
    {
        try
        {
            return File.Exists(ProgressFile)
                ? System.Text.Json.JsonSerializer.Deserialize<Avion2.Analysis.ProgressBook>(File.ReadAllText(ProgressFile)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    private DateTime _progressSaved;
    private bool _progressDirty;

    private void UpdateProgress(Avion2.Analysis.CharacterProgress progress)
    {
        // Waits for our name: what came before it stays with the meter and is placed then
        if (Progresses.Take(progress) is not { } merged)
        {
            return;
        }

        _progressLive = merged.Character;
        ProgressUpdated?.Invoke();
        _progressDirty = true;
        if (DateTime.UtcNow - _progressSaved > TimeSpan.FromSeconds(10))
        {
            SaveProgress();
        }
    }

    private void SaveProgress()
    {
        if (!KeepsGameState || !_progressDirty)
        {
            return;
        }

        _progressSaved = DateTime.UtcNow;
        _progressDirty = false;
        try
        {
            Directory.CreateDirectory(InventoryDirectory);
            File.WriteAllText(ProgressFile, System.Text.Json.JsonSerializer.Serialize(Progresses));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _progressDirty = true; // next change tries again
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

        Notices.Notify($"{name}: {skill}", "Boss skill", boss.NpcCode is { } npcCode ? BossImages.ForNpc(npcCode) : null);
    }

    /// <summary>Group finder: a join request, the room full and ready, the dungeon starting (each with its Settings switch).</summary>
    private void OnFinderAlert(Avion2.Analysis.FinderAlert alert)
    {
        if (FinderNotices.Text(alert, Settings) is not { } text)
        {
            return;
        }

        if (Settings.NotifySound)
        {
            NoticeSound().Play();
        }

        Notices.Notify(text, "Group finder");
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
    /// "Update now": opens the update window, which downloads and checks the release with a progress bar and then
    /// hands over to AVION2-Updater.exe. Returns an error message, or null when the update window opened.
    /// </summary>
    public string? StartUpdate(Window? owner = null)
    {
        if (AvailableUpdate is not { } release)
        {
            return "No update available.";
        }

        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "AVION2-Updater.exe")))
        {
            return "AVION2-Updater.exe is missing next to AVION2.exe. Download the release from GitHub instead.";
        }

        new UpdateWindow(this, release) { Owner = owner }.Show();
        return null;
    }

    /// <summary>
    /// Starts AVION2-Updater.exe on a downloaded and unpacked release and closes AVION2: the updater waits for us to
    /// exit, swaps the files and starts AVION2 again. Returns an error message, or null when AVION2 is closing.
    /// </summary>
    public string? HandOverUpdate(string staged, ReleaseInfo release)
    {
        string dir = AppContext.BaseDirectory;
        string updater = Path.Combine(dir, "AVION2-Updater.exe");
        if (!File.Exists(updater))
        {
            return "AVION2-Updater.exe is missing next to AVION2.exe. Download the release from GitHub instead.";
        }

        SaveSettings();
        StopMeter();
        var start = new System.Diagnostics.ProcessStartInfo(updater) { UseShellExecute = false };
        foreach (string arg in new[] { "--dir", dir.TrimEnd(Path.DirectorySeparatorChar), "--staged", staged, "--version", release.Tag,
                     "--pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) })
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            System.Diagnostics.Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return $"AVION2-Updater.exe could not be started: {ex.Message}";
        }

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
        Settings.PartyFrames = s.PartyFrames;
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
        Settings.NotifyPetSouls = s.NotifyPetSouls;
        Settings.NotifyJoinRequests = s.NotifyJoinRequests;
        Settings.NotifyRoomReady = s.NotifyRoomReady;
        Settings.NotifyDungeonStart = s.NotifyDungeonStart;
        Settings.NotifySound = s.NotifySound;
        Settings.NoticeSound = string.IsNullOrWhiteSpace(s.NoticeSound) ? null : s.NoticeSound;
        Settings.NoticeVolume = s.NoticeVolume;
    }

    /// <summary>False for developer render runs, so they never overwrite the user's settings.</summary>
    public bool PersistSettings { get; set; } = true;

    /// <summary>
    /// What the game told us (inventory, legion, field bosses, cubes) is saved only from the live game: a replay's
    /// is old and would overwrite the current one.
    /// </summary>
    private bool KeepsGameState => PersistSettings && !IsReplay;

    public void SaveSettings()
    {
        if (!PersistSettings)
        {
            return;
        }

        SaveInventory();
        SaveProgress();
        SaveLegionsSeen();
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
        s.PartyFrames = Settings.PartyFrames;
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
        s.NotifyPetSouls = Settings.NotifyPetSouls;
        s.NotifyJoinRequests = Settings.NotifyJoinRequests;
        s.NotifyRoomReady = Settings.NotifyRoomReady;
        s.NotifyDungeonStart = Settings.NotifyDungeonStart;
        s.NotifySound = Settings.NotifySound;
        s.NoticeSound = Settings.NoticeSound;
        s.NoticeVolume = Settings.NoticeVolume;
        s.Save();
    }
}
