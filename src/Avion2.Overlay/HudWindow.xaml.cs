using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <summary>
/// Effects on the game's own HUD, placed with the calibrated <see cref="HudLayout"/>: your HP bar pulses red while
/// your HP is low, the screen edges flash red when you lose a big share of HP at once, your DPS shows above your
/// HP bar during a fight, and boss skills appear right under the target frame. A transparent, click-through window
/// that follows the game's client area and only shows while the game is in front.
/// </summary>
public partial class HudWindow : Window
{
    private readonly AppState _state;
    private readonly Func<EngineStatus?> _status;
    private readonly Func<MeterSnapshot?> _snapshot;
    private readonly BigHitMonitor _bigHits = new();
    private DateTime _dpsUpdated;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _castTimer = new() { Interval = TimeSpan.FromSeconds(2.8) };
    private Int32Rect _placed;
    private bool _pulsing;
    private bool _demo;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    private const uint NoActivate = 0x0010;
    private static readonly nint HwndTopmost = new(-1);

    public HudWindow(AppState state, Func<EngineStatus?> status, Func<MeterSnapshot?> snapshot)
    {
        _state = state;
        _status = status;
        _snapshot = snapshot;
        InitializeComponent();
        Left = -20000;
        Width = 10;
        Height = 10;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.SetExStyle(hwnd, NativeMethods.WsExTransparent | NativeMethods.WsExLayered | NativeMethods.WsExNoActivate | 0x00000080 /* tool window */, NativeMethods.WsExAppWindow);
        };
        _timer.Tick += (_, _) => Tick();
        _castTimer.Tick += (_, _) =>
        {
            _castTimer.Stop();
            Fade(CastBanner, 0, 400);
        };
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _castTimer.Stop();
        };
    }

    /// <summary>The layout for the game's current size (null: not calibrated for it).</summary>
    private HudLayout? Layout(Int32Rect area) =>
        _state.Stored.HudLayouts.GetValueOrDefault(HudLayout.Key(area.Width, area.Height));

    /// <summary>Kept hidden (while the calibration picture is taken).</summary>
    public bool Suspended { get; set; }

    private void Tick()
    {
        var area = GameWindow.ClientArea;
        bool show = !Suspended && area is not null && (_demo || GameWindow.IsInFront) && Layout(area.Value) is not null;
        if (!show)
        {
            if (IsVisible)
            {
                Hide();
            }

            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        if (area!.Value != _placed)
        {
            Place(area.Value);
        }

        var s = _status();
        UpdateLowHp(s);
        UpdateBigHit(s);
        UpdateDps(s);
    }

    /// <summary>Covers the game's client area exactly (pixels), above the game.</summary>
    private void Place(Int32Rect area)
    {
        _placed = area;
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, HwndTopmost, area.X, area.Y, area.Width, area.Height, NoActivate);
        var dpi = VisualTreeHelper.GetDpi(this);
        Stage.Width = area.Width / dpi.DpiScaleX;
        Stage.Height = area.Height / dpi.DpiScaleY;
        EdgeFlash.Width = Stage.Width;
        EdgeFlash.Height = Stage.Height;
        Arrange(Layout(area)!);
    }

    private void Arrange(HudLayout layout)
    {
        double w = Stage.Width, h = Stage.Height;
        var hp = layout.Box(HudAnchors.SelfHp).ToPixels(w, h);
        const double pad = 4;
        Canvas.SetLeft(HpGlow, hp.X - pad);
        Canvas.SetTop(HpGlow, hp.Y - pad);
        HpGlow.Width = hp.W + pad * 2;
        HpGlow.Height = hp.H + pad * 2;

        DpsLine.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(DpsLine, hp.X);
        Canvas.SetTop(DpsLine, hp.Y - DpsLine.DesiredSize.Height - 6);

        var target = layout.Box(HudAnchors.Target).ToPixels(w, h);
        CastBanner.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(CastBanner, target.X + target.W / 2 - CastBanner.DesiredSize.Width / 2);
        Canvas.SetTop(CastBanner, target.Y + target.H + 8);
    }

    private void UpdateLowHp(EngineStatus? status)
    {
        bool low = _demo;
        if (!low && _state.Stored.HudHpBlink && status is { SelfMaxHp: > 0 } s && s.SelfHp > 0)
        {
            low = s.SelfHp / (double)s.SelfMaxHp < _state.Settings.LowHpThreshold;
        }

        if (low == _pulsing)
        {
            return;
        }

        _pulsing = low;
        if (low)
        {
            HpGlow.Visibility = Visibility.Visible;
            HpGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(450))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase(),
            });
        }
        else
        {
            HpGlow.BeginAnimation(OpacityProperty, null);
            HpGlow.Opacity = 0;
            HpGlow.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateBigHit(EngineStatus? status)
    {
        if (status is null || !_bigHits.Update(status.SelfHp, status.SelfMaxHp, Environment.TickCount64) || !_state.Stored.HudDamageFlash)
        {
            return;
        }

        Flash();
    }

    private void Flash()
    {
        EdgeFlash.Visibility = Visibility.Visible;
        var flash = new DoubleAnimationUsingKeyFrames();
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
        flash.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700)), new QuadraticEase()));
        EdgeFlash.BeginAnimation(OpacityProperty, flash);
    }

    /// <summary>"6,279 DPS · 13 % crit · 1:20" above your HP bar while you are fighting (refreshed twice a second).</summary>
    private void UpdateDps(EngineStatus? status)
    {
        bool fighting = _demo || (_state.Stored.HudDpsLine && status is { InCombat: true });
        if (!fighting)
        {
            DpsLine.Visibility = Visibility.Collapsed;
            return;
        }

        if (DateTime.UtcNow - _dpsUpdated < TimeSpan.FromMilliseconds(500) && DpsLine.Visibility == Visibility.Visible)
        {
            return;
        }

        _dpsUpdated = DateTime.UtcNow;
        if (_demo)
        {
            DpsText.Text = "6,279 DPS · 13 % crit · 1:20";
        }
        else if (_snapshot() is { Self: { } me } snapshot)
        {
            DpsText.Text = $"{me.Dps:N0} DPS · {me.CritRate:P0} crit · {HistoryStore.Duration(snapshot.DurationSeconds)}";
        }
        else
        {
            DpsLine.Visibility = Visibility.Collapsed;
            return;
        }

        bool first = DpsLine.Visibility != Visibility.Visible;
        DpsLine.Visibility = Visibility.Visible;
        if (first && GameWindow.ClientArea is { } area && Layout(area) is { } layout)
        {
            Arrange(layout);
        }
    }

    /// <summary>A boss skill under the target frame for a few seconds. Returns false when not shown (not calibrated, game not in front).</summary>
    public bool ShowCast(string boss, string skill)
    {
        if (!_state.Stored.HudBossCasts || !IsVisible || GameWindow.ClientArea is not { } area || Layout(area) is not { } layout)
        {
            return false;
        }

        CastBoss.Text = boss;
        CastSkill.Text = skill;
        CastBanner.Visibility = Visibility.Visible;
        Arrange(layout);
        Fade(CastBanner, 1, 120);
        _castTimer.Stop();
        _castTimer.Start();
        return true;
    }

    /// <summary>Calibration preview: shows every effect for a few seconds.</summary>
    public void Demo()
    {
        _demo = true;
        _placed = default;
        Tick();
        ShowCast("Boss", "Skill name");
        Flash();
        var stop = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        stop.Tick += (_, _) =>
        {
            stop.Stop();
            _demo = false;
        };
        stop.Start();
    }

    /// <summary>Re-reads the layout (after calibration).</summary>
    public void Relayout() => _placed = default;

    private static void Fade(UIElement element, double to, int ms) =>
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)));
}
