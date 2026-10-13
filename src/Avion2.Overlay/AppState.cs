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
    }

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

    public string? DataDirectory { get; private set; }

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
            HistoryDirectory = _baseOptions.HistoryDirectory,
            SaveHistory = _baseOptions.SaveHistory,
            ReplayPath = _baseOptions.ReplayPath,
            ReplaySpeed = _baseOptions.ReplaySpeed,
        });
        engine.FightSaved += entry => Application.Current.Dispatcher.BeginInvoke(() => Fights.Insert(0, new HistoryRow(entry)));

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

    /// <summary>Loads freshly imported name tables (into the running meter too).</summary>
    public void ReloadGameData(string directory)
    {
        Engine?.ReloadGameData(directory);
        _dataOverride = directory;
        RefreshGameDataInfo();
    }

    private string? _dataOverride;

    private void RefreshGameDataInfo()
    {
        var (data, dir) = MeterEngine.FindGameData(_dataOverride ?? _baseOptions.DataDirectory);
        DataDirectory = dir;
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
        s.Save();
    }
}
