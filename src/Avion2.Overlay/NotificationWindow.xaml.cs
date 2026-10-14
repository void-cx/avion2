using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Avion2.Overlay;

/// <summary>One notice card; fades in, stays a while, fades out.</summary>
public sealed class Notice(string text, string chip) : Bindable
{
    private double _opacity;

    public string Text { get; } = text;
    public string Chip { get; } = chip;
    public double Opacity { get => _opacity; set => Set(ref _opacity, value); }
    public DateTime ShownAt { get; } = DateTime.UtcNow;
}

/// <summary>
/// Free-floating notification area, independent of the DPS overlay: transparent, always on top and
/// click-through, so it can sit anywhere over the game. "Move notification area" in Settings shows a
/// frame that can be dragged.
/// </summary>
public partial class NotificationWindow : Window
{
    private const int MaxNotices = 4;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(400);

    private readonly AppState _state;
    private readonly ObservableCollection<Notice> _notices = new();
    private readonly DispatcherTimer _animate = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private nint _hwnd;
    private bool _positioning;

    public NotificationWindow(AppState state)
    {
        _state = state;
        InitializeComponent();
        ThemeManager.Attach(this);
        Notices.ItemsSource = _notices;
        PlaceWindow();
        _animate.Tick += (_, _) => Animate();
        Closed += (_, _) => SavePosition();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyClickThrough();
    }

    /// <summary>Shows a notice card (newest on top).</summary>
    public void Notify(string text, string chip)
    {
        _notices.Insert(0, new Notice(text, chip));
        while (_notices.Count > MaxNotices)
        {
            _notices.RemoveAt(_notices.Count - 1);
        }

        if (!IsVisible)
        {
            Show();
        }

        _animate.Start();
    }

    /// <summary>Shows the draggable frame; "Done" (or calling again with false) locks it in place.</summary>
    public void SetPositioning(bool positioning)
    {
        _positioning = positioning;
        if (positioning)
        {
            _notices.Clear(); // keep the frame's instructions readable
        }

        Placeholder.Visibility = positioning ? Visibility.Visible : Visibility.Collapsed;
        if (!IsVisible)
        {
            Show();
        }

        ApplyClickThrough();
        if (!positioning)
        {
            SavePosition();
        }
    }

    private void Animate()
    {
        var now = DateTime.UtcNow;
        foreach (var notice in _notices.ToList())
        {
            var age = now - notice.ShownAt;
            if (age >= Lifetime)
            {
                _notices.Remove(notice);
                continue;
            }

            double fadeIn = Math.Min(1, age / Fade);
            double fadeOut = Math.Min(1, (Lifetime - age) / Fade);
            notice.Opacity = Math.Min(fadeIn, fadeOut);
        }

        if (_notices.Count == 0)
        {
            _animate.Stop();
        }
    }

    /// <summary>Click-through unless the frame is being positioned. Never activates, hidden from Alt+Tab.</summary>
    private void ApplyClickThrough()
    {
        if (_hwnd == 0)
        {
            return;
        }

        const int toolWindow = 0x00000080;
        NativeMethods.SetExStyle(
            _hwnd,
            NativeMethods.WsExNoActivate | toolWindow | (_positioning ? 0 : NativeMethods.WsExTransparent | NativeMethods.WsExLayered),
            _positioning ? NativeMethods.WsExTransparent : 0);
    }

    private void Placeholder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Button)
        {
            DragMove();
        }
    }

    private void Done_Click(object sender, RoutedEventArgs e) => SetPositioning(false);

    private void PlaceWindow()
    {
        var s = _state.Stored;
        var area = SystemParameters.WorkArea;
        bool valid = s.NoticeLeft is { } left && s.NoticeTop is { } top &&
                     left >= SystemParameters.VirtualScreenLeft - 50 &&
                     top >= SystemParameters.VirtualScreenTop - 50 &&
                     left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50 &&
                     top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50;

        // Default: top centre of the primary screen, below where most games put their own banners.
        Left = valid ? s.NoticeLeft!.Value : area.Left + (area.Width - Width) / 2;
        Top = valid ? s.NoticeTop!.Value : area.Top + area.Height * 0.12;
    }

    private void SavePosition()
    {
        if (!_state.PersistSettings)
        {
            return;
        }

        _state.Stored.NoticeLeft = Left;
        _state.Stored.NoticeTop = Top;
        _state.SaveSettings();
    }
}
