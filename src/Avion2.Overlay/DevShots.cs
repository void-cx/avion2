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
                }
            }

            Shoot(home, "home", directory, "home.png");
            if (state.Fights.FirstOrDefault() is { } fight)
            {
                home.ShowPage("history", fight);
            }

            Shoot(home, "history", directory, "history.png");
            Shoot(home, "settings", directory, "settings.png");
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
