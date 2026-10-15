using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Avion2.Overlay;

/// <summary>
/// The AION2 window: where its client area is on screen (in pixels), whether it is in front, and a one-off picture
/// of it for HUD calibration. Read-only: AVION2 never sends anything to the game window.
/// </summary>
internal static class GameWindow
{
    private static readonly string[] Names = ["AION2", "AION2-Win64-Shipping"];
    private static nint _cached;
    private static DateTime _found;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sx, int sy, uint rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint dc);

    private const uint SrcCopy = 0x00CC0020;

    /// <summary>The game's main window, or 0 when it is not running (looked up at most every 2 s).</summary>
    public static nint Handle
    {
        get
        {
            if (_cached != 0 && IsWindow(_cached))
            {
                return _cached;
            }

            if (DateTime.UtcNow - _found < TimeSpan.FromSeconds(2))
            {
                return 0;
            }

            _found = DateTime.UtcNow;
            _cached = 0;
            foreach (string name in Names)
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    if (_cached == 0 && process.MainWindowHandle != 0)
                    {
                        _cached = process.MainWindowHandle;
                    }

                    process.Dispose();
                }
            }

            return _cached;
        }
    }

    /// <summary>The client area in screen pixels; null when the game is not running or minimized.</summary>
    public static Int32Rect? ClientArea
    {
        get
        {
            nint hwnd = Handle;
            if (hwnd == 0 || IsIconic(hwnd) || !GetClientRect(hwnd, out var rect) || rect.Right <= 0 || rect.Bottom <= 0)
            {
                return null;
            }

            var origin = new NativePoint();
            ClientToScreen(hwnd, ref origin);
            return new Int32Rect(origin.X, origin.Y, rect.Right, rect.Bottom);
        }
    }

    /// <summary>The game is the active window (or one of AVION2's own windows is: clicking the overlay counts).</summary>
    public static bool IsInFront
    {
        get
        {
            nint front = GetForegroundWindow();
            if (front == 0)
            {
                return false;
            }

            if (front == Handle)
            {
                return true;
            }

            GetWindowThreadProcessId(front, out uint pid);
            return pid == Environment.ProcessId;
        }
    }

    public static bool IsForeground => Handle is var hwnd && hwnd != 0 && GetForegroundWindow() == hwnd;

    /// <summary>
    /// One picture of the game's client area, copied from the screen (the game must be in front and not covered).
    /// Used once for calibration; nothing is saved.
    /// </summary>
    public static BitmapSource? Capture()
    {
        if (ClientArea is not { } area)
        {
            return null;
        }

        nint screen = GetDC(0);
        nint memory = CreateCompatibleDC(screen);
        nint bitmap = CreateCompatibleBitmap(screen, area.Width, area.Height);
        nint old = SelectObject(memory, bitmap);
        try
        {
            if (!BitBlt(memory, 0, 0, area.Width, area.Height, screen, area.X, area.Y, SrcCopy))
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }
}
