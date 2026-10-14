using System.Globalization;
using System.Windows;
using Avion2.Engine;

namespace Avion2.Overlay;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string[] args = e.Args;
        var state = new AppState(ParseArgs(args));
        ThemeManager.Apply(Get(args, "theme") ?? state.Stored.Theme); // --theme previews without saving
        var home = new HomeWindow(state);
        MainWindow = home;

        var tab = Get(args, "tab") is { } t && Enum.TryParse<OverlayTab>(t, ignoreCase: true, out var parsed) ? parsed : OverlayTab.Damage;
        AppState.CleanUpAfterUpdate();
        if (Get(args, "render-shots") is { } shots)
        {
            double delay = double.TryParse(Get(args, "shot-delay"), CultureInfo.InvariantCulture, out double d) ? d : 9;
            DevShots.Run(state, home, shots, delay);
            return;
        }

        home.Show();
        if (state.Stored.CheckForUpdates && !state.IsReplay)
        {
            _ = state.CheckForUpdatesAsync();
        }

        if (state.IsReplay || args.Contains("--start") || state.Settings.AutoStart)
        {
            if (state.StartMeter(tab) is { } error)
            {
                MessageBox.Show(home, error, "AVION2", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private static string? Get(string[] args, string name)
    {
        int i = Array.IndexOf(args, "--" + name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>
    /// Same options as <c>avion2 live</c>: --device 7,9 --record x.pcap --log x.jsonl --data dir;
    /// plus --start, --replay x.pcap [--speed 2] [--history-dir dir] and --tab taken.
    /// </summary>
    private static EngineOptions ParseArgs(string[] args) => new()
    {
        Devices = Get(args, "device")?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList(),
        RecordPath = Get(args, "record"),
        EventLogPath = Get(args, "log"),
        DataDirectory = Get(args, "data"),
        ReplayPath = Get(args, "replay"),
        HistoryDirectory = Get(args, "history-dir"),
        // Replays are for UI work: only save their fights when a history directory is given explicitly.
        SaveHistory = Get(args, "replay") is null || Get(args, "history-dir") is not null,
        ReplaySpeed = double.TryParse(Get(args, "speed"), CultureInfo.InvariantCulture, out double speed) && speed > 0 ? speed : 1.0,
    };
}
