using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Avion2.Overlay;

/// <summary>
/// Keeps overlay windows above the game. <see cref="Window.Topmost"/> is set once; a game that keeps activating
/// itself (borderless fullscreen) can still end up on top. This puts the window back at the top of the topmost
/// band every couple of seconds while it is visible, without activating it (the game keeps the keyboard).
/// An exclusive-fullscreen game cannot be drawn over at all — use borderless/windowed mode.
/// </summary>
internal static class TopmostKeeper
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint NoSize = 0x0001, NoMove = 0x0002, NoActivate = 0x0010, NoOwnerZOrder = 0x0200;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>Re-asserts topmost every <paramref name="every"/> while the window is shown.</summary>
    public static void Keep(Window window, TimeSpan? every = null)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = every ?? TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) => Raise(window);
        window.IsVisibleChanged += (_, _) =>
        {
            if (window.IsVisible)
            {
                Raise(window);
                timer.Start();
            }
            else
            {
                timer.Stop();
            }
        };
        window.Closed += (_, _) => timer.Stop();
    }

    /// <summary>Puts the window on top now (no focus change).</summary>
    public static void Raise(Window window)
    {
        if (!window.IsVisible || new WindowInteropHelper(window).Handle is var hwnd && hwnd == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, NoMove | NoSize | NoActivate | NoOwnerZOrder);
    }
}
