using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Avion2.Overlay;

/// <summary>
/// The floating capture-marker button, on top of the game while a session is recorded: a click marks the moment and
/// opens the note box (the same as the marker key, for when the game doesn't let the key through); dragging moves it, and the place is kept. It never takes the keyboard from the game on its own, and
/// it is left out of the marker pictures.
/// </summary>
internal sealed class MarkerButton : Window
{
    private const double DragThreshold = 4;
    private const uint WdaExcludeFromCapture = 0x11;

    private readonly AppState _state;
    private readonly TextBlock _count;
    private Point _grab;
    private Point _downScreen;
    private bool _down;
    private bool _dragged;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    public MarkerButton(AppState state)
    {
        _state = state;
        Title = "Capture marker";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = Height = 44;
        Cursor = Cursors.Hand;
        ToolTip = "Capture marker: click to mark what you just did · drag to move";

        var circle = new Ellipse { StrokeThickness = 2 };
        circle.SetResourceReference(Shape.FillProperty, "FloatingPanelBrush");
        circle.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        var dot = new Ellipse { Width = 14, Height = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Shape.FillProperty, "AccentBrush");
        _count = new TextBlock
        {
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 1, 0),
        };
        _count.SetResourceReference(TextBlock.ForegroundProperty, "TitleBrush");
        Content = new Grid { Children = { circle, dot, _count } };

        ThemeManager.Attach(this);
        TopmostKeeper.Keep(this);
        Place();
        SourceInitialized += (_, _) =>
        {
            nint hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.SetExStyle(hwnd, NativeMethods.WsExNoActivate, 0); // the game keeps the keyboard
            SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture); // not in the marker pictures (Windows 10 2004+)
        };
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        LostMouseCapture += (_, _) => _down = false;
        Refresh();
    }

    /// <summary>Shows the number of markers saved so far.</summary>
    public void Refresh() => _count.Text = _state.Markers is { Count: > 0 } markers ? markers.Count.ToString() : string.Empty;

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _down = true;
        _dragged = false;
        _grab = e.GetPosition(this);
        _downScreen = PointToScreen(_grab);
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_down)
        {
            return;
        }

        var position = e.GetPosition(this);
        var screen = PointToScreen(position);
        if (!_dragged && (Math.Abs(screen.X - _downScreen.X) > DragThreshold || Math.Abs(screen.Y - _downScreen.Y) > DragThreshold))
        {
            _dragged = true;
        }

        if (_dragged)
        {
            // The window follows the mouse: the grab point stays under the cursor
            Left += position.X - _grab.X;
            Top += position.Y - _grab.Y;
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_down)
        {
            return;
        }

        _down = false;
        ReleaseMouseCapture();
        e.Handled = true;
        if (_dragged)
        {
            _state.Stored.MarkerButtonLeft = Left;
            _state.Stored.MarkerButtonTop = Top;
            _state.SaveSettings();
        }
        else
        {
            _state.Mark();
        }
    }

    /// <summary>Where it was left; at first the game's top right corner (or the main screen's), clear of the HUD.</summary>
    private void Place()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (_state.Stored.MarkerButtonLeft is { } left && _state.Stored.MarkerButtonTop is { } top && OnScreen(left, top))
        {
            Left = left;
            Top = top;
            return;
        }

        double right = SystemParameters.PrimaryScreenWidth, upper = 0;
        if (GameWindow.ClientArea is { } area)
        {
            double scale = VisualTreeHelper.GetDpi(Application.Current.MainWindow ?? this).DpiScaleX;
            right = (area.X + area.Width) / scale;
            upper = area.Y / scale;
        }

        Left = right - Width - 220;
        Top = upper + 90;
    }

    private bool OnScreen(double left, double top) =>
        left + Width > SystemParameters.VirtualScreenLeft && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
        top + Height > SystemParameters.VirtualScreenTop && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
}
