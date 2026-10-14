using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Avion2.Overlay;

/// <summary>
/// Developer aid: <c>--replay x.pcap --render-shots dir</c> renders every window off-screen to PNGs and exits,
/// so the UI can be checked without windows popping up over a running game.
/// </summary>
internal static class DevShots
{
    public static void Run(AppState state, HomeWindow home, string directory, double delaySeconds = 9)
    {
        Directory.CreateDirectory(directory);
        state.PersistSettings = false;
        state.Stored.CubeFinder = true; // show every feature in the shots (settings are not saved in this run)
        home.WindowStartupLocation = WindowStartupLocation.Manual;
        home.Left = -20000;
        home.Show();
        state.StartMeter();
        if (state.Overlay is { } overlay)
        {
            overlay.Left = -20000;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delaySeconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (state.Overlay is { } running)
            {
                Save(running, Path.Combine(directory, "overlay.png"));
                _ = state.NoticeSound(); // builds the notification sound (writes notice.wav) without playing it
                state.Notices.Notify("Spacetime Rift in 5 min (14:00)", "Spacetime Rift");
                state.Notices.Notify("Beritra Air Raid in 5 min (13:30)", "Boss raid");
                state.Notices.Left = -20000;
                Pump(TimeSpan.FromMilliseconds(900));
                Save(state.Notices, Path.Combine(directory, "notices.png"));
                state.Notices.SetPositioning(true);
                state.Notices.Left = -20000;
                Pump(TimeSpan.FromMilliseconds(400));
                Save(state.Notices, Path.Combine(directory, "notices-move.png"));
                running.OpenDetails(null, 0);
                if (running.DetailView is { } detail)
                {
                    detail.WindowStartupLocation = WindowStartupLocation.Manual;
                    detail.Left = -20000;
                    Pump(TimeSpan.FromMilliseconds(1200));
                    Save(detail, Path.Combine(directory, "detail-live.png"));
                    foreach (var (tab, name) in new[] { (2, "timeline"), (3, "buffs"), (4, "replay") })
                    {
                        detail.View.ViewModel.TabIndex = tab;
                        Pump(TimeSpan.FromMilliseconds(1300));
                        if (tab == 4)
                        {
                            detail.View.ReplaySlider.Value = detail.View.ReplaySlider.Maximum * 0.6;
                            detail.View.Replay.Inspect(detail.View.ViewModel.Trace?.SelfId);
                            Pump(TimeSpan.FromMilliseconds(400));
                        }

                        Save(detail, Path.Combine(directory, $"detail-{name}.png"));
                    }
                }
            }

            Shoot(home, "home", directory, "home.png");
            if (home.CurrentPage.Content is System.Windows.Controls.ScrollViewer scroller)
            {
                scroller.ScrollToVerticalOffset(560);
                Pump(TimeSpan.FromMilliseconds(500));
                Save(home, Path.Combine(directory, "home-lower.png"));
                scroller.ScrollToTop();
            }
            if (state.Fights.FirstOrDefault() is { } fight)
            {
                home.ShowPage("history", fight);
            }

            Shoot(home, "timers", directory, "timers.png");
            Shoot(home, "map", directory, "map.png");
            if (home.CurrentPage is MapPage mapPage)
            {
                mapPage.Preview("World_L_A", "Confused Ursus");
                Shoot(home, "map", directory, "map-zoom.png");
                mapPage.PreviewClose("Dawn Legion Base", 1.0);
                Shoot(home, "map", directory, "map-close.png");
                mapPage.PreviewSearch("kaira");
                Shoot(home, "map", directory, "map-search.png");
                mapPage.Preview("World_D_A", "Teleport");
                Shoot(home, "map", directory, "map-altgard.png");
                if (mapPage.PreviewExactCube())
                {
                    Shoot(home, "map", directory, "map-exact.png");
                }

                mapPage.Preview("World_L_A", "Confused Ursus");
                if (mapPage.OpenCompass() is { } compass)
                {
                    compass.Left = -20000;
                    Shoot(home, "map", directory, "map-cube.png");
                    Save(compass, Path.Combine(directory, "compass.png"));
                    foreach (var (name, angle, meters, north, turn, exact, searching, detail) in new[]
                    {
                        ("far", 38.0, 240.0, false, "Turn right 38°", false, false, "Dawn Legion Base · position now"),
                        ("search", 0.0, 45.0, false, "Walk or fly around here", false, true, "The cube is somewhere within ~80 m. The compass turns green once the game shows it."),
                        ("near", -6.0, 17.0, false, "Straight ahead", true, false, "↑ 4 m above you · Dawn Legion Base"),
                        ("here", 0.0, 4.0, false, "You're here", true, false, "same height · Dawn Legion Base"),
                    })
                    {
                        compass.Demo(angle, meters, north, turn, detail, exact, searching);
                        Pump(TimeSpan.FromMilliseconds(300));
                        Save(compass, Path.Combine(directory, $"compass-{name}.png"));
                    }

                    compass.Close();
                }
            }

            Shoot(home, "forge", directory, "forge.png");
            if (home.CurrentPage is ForgePage forge)
            {
                forge.Pick("ludra blade");
                Shoot(home, "forge", directory, "forge-plan.png");
                forge.PickMine("Spiritforged Staff");
                Shoot(home, "forge", directory, "forge-mine.png");
            }

            Shoot(home, "history", directory, "history.png");
            if (home.CurrentPage is HistoryPage historyPage)
            {
                historyPage.ShowBest();
                Shoot(home, "history", directory, "history-best.png");
            }

            Shoot(home, "settings", directory, "settings.png");
            if (home.CurrentPage.Content is System.Windows.Controls.ScrollViewer settingsScroller)
            {
                settingsScroller.ScrollToVerticalOffset(700);
                Pump(TimeSpan.FromMilliseconds(500));
                Save(home, Path.Combine(directory, "settings-lower.png"));
                settingsScroller.ScrollToTop();
            }

            // Switch the theme while everything is open: every window must restyle without a restart.
            var other = ThemeManager.Themes.First(t => t != ThemeManager.Current);
            ThemeManager.Apply(other.Id);
            Shoot(home, "home", directory, $"switched-{other.Id}-home.png");
            Shoot(home, "settings", directory, $"switched-{other.Id}-settings.png");
            if (state.Overlay is { } switched)
            {
                Save(switched, Path.Combine(directory, $"switched-{other.Id}-overlay.png"));
            }

            Application.Current.Shutdown();
        };
        timer.Start();
    }

    private static void Shoot(HomeWindow home, string page, string directory, string name)
    {
        home.ShowPage(page);
        home.Left = -20000;
        Pump(TimeSpan.FromMilliseconds(1200));
        Save(home, Path.Combine(directory, name));
    }

    /// <summary>
    /// Developer aid: <c>--replay-shot dir</c> renders the replay of the longest saved fight. Use it on a
    /// history recorded with <c>--anonymize</c> (no real names) for screenshots that can be shared.
    /// </summary>
    public static void AnonymousReplay(AppState state, string directory)
    {
        Directory.CreateDirectory(directory);
        state.PersistSettings = false;
        var best = state.Fights
            .Select(row => (Row: row, Trace: state.History.LoadTrace(row.Entry)))
            .Where(x => x.Trace is not null)
            .OrderByDescending(x => x.Row.Entry.Snapshot.DurationSeconds)
            .FirstOrDefault();
        if (best.Trace is not { } anonymous)
        {
            Application.Current.Shutdown();
            return;
        }

        var view = new DetailView();
        var window = new Window
        {
            Content = view,
            Width = 1180,
            Height = 700,
            Left = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("BackdropBrush"),
        };
        ThemeManager.Attach(window);
        window.Show();
        view.ShowSaved(best.Row.Entry, anonymous.SelfId, 4, anonymous);
        Pump(TimeSpan.FromMilliseconds(1500));
        view.ReplaySlider.Value = view.ReplaySlider.Maximum * 0.55;
        view.Replay.Inspect(anonymous.SelfId);
        Pump(TimeSpan.FromMilliseconds(800));
        Save(window, Path.Combine(directory, "replay.png"));
        File.WriteAllText(Path.Combine(directory, "replay.txt"), best.Row.Entry.Snapshot.Title);
        Application.Current.Shutdown();
    }

    private static void Save(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        int width = (int)Math.Ceiling(root.ActualWidth);
        int height = (int)Math.Ceiling(root.ActualHeight);
        if (width == 0 || height == 0)
        {
            return;
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Lets layout, bindings and timers run for a while.</summary>
    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var stop = new DispatcherTimer { Interval = duration };
        stop.Tick += (_, _) =>
        {
            stop.Stop();
            frame.Continue = false;
        };
        stop.Start();
        Dispatcher.PushFrame(frame);
    }
}
