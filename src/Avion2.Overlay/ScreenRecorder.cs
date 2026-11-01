using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <summary>
/// Developer: pictures of the game window beside a session recording (<c>session-….frames\&lt;unix ms&gt;.jpg</c>, four
/// a second), so the traffic can be matched with what was on screen. Named by the same clock as the packets. Only the
/// game's area of the screen, and only while the game (or AVION2) is in front: nothing else on the desktop is recorded.
/// </summary>
internal sealed class ScreenRecorder : IDisposable
{
    private const int PerSecond = 4;
    private const int MaxWidth = 1920;

    private readonly string _directory;
    private readonly ManualResetEventSlim _stop = new();
    private readonly Thread _thread;

    public ScreenRecorder(string recording)
    {
        _directory = Recordings.FramesDirectory(recording);
        // Its own thread: the encoders are WPF objects, which tie themselves to the thread that makes them
        _thread = new Thread(Loop) { IsBackground = true, Name = "AVION2 screen recorder", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    private void Loop()
    {
        var interval = TimeSpan.FromSeconds(1.0 / PerSecond);
        var clock = Stopwatch.StartNew();
        var next = TimeSpan.Zero;
        while (!_stop.IsSet)
        {
            if (GameWindow.IsInFront)
            {
                long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                try
                {
                    if (GameWindow.Capture() is { } picture)
                    {
                        Save(picture, ms);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                               System.Runtime.InteropServices.COMException or NotSupportedException)
                {
                    // window resized or closed mid-copy, disk full, folder gone: skip this frame
                }
            }

            // Steady four a second; a slow frame skips ahead instead of piling up
            next += interval;
            if (next < clock.Elapsed)
            {
                next = clock.Elapsed;
            }

            _stop.Wait(next - clock.Elapsed);
        }
    }

    private void Save(BitmapSource picture, long ms)
    {
        BitmapSource frame = picture.PixelWidth > MaxWidth
            ? new TransformedBitmap(picture, new ScaleTransform((double)MaxWidth / picture.PixelWidth, (double)MaxWidth / picture.PixelWidth))
            : picture;
        var encoder = new JpegBitmapEncoder { QualityLevel = 75 };
        encoder.Frames.Add(BitmapFrame.Create(frame));
        Directory.CreateDirectory(_directory);
        using var file = File.Create(Path.Combine(_directory, $"{ms}.jpg"));
        encoder.Save(file);
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
