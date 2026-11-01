using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <summary>
/// Effects on the game's own HUD, placed with the calibrated <see cref="HudLayout"/>: your HP bar pulses red while
/// your HP is low, the screen edges flash red when you lose a big share of HP at once, your DPS shows above your
/// HP bar during a fight, and boss skills appear right under the target frame. A transparent, click-through window
/// that follows the game's client area: on top of everything while the game is in front, otherwise just above the
/// game window (a game on a second screen keeps its HUD; a window over the game covers the HUD too).
/// </summary>
public partial class HudWindow : Window
{
    private readonly AppState _state;
    private readonly Func<EngineStatus?> _status;
    private readonly Func<MeterSnapshot?> _snapshot;
    private readonly BigHitMonitor _bigHits = new();
    private DateTime _dpsUpdated;
    private DateTime _partyUpdated;
    private DateTime _bossUpdated;
    private readonly Queue<(long Ms, SelfHit Hit)> _hits = new();
    private long _lastCombatMs = long.MinValue / 2;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _castTimer = new() { Interval = TimeSpan.FromSeconds(2.8) };
    private Int32Rect _placed;
    private bool _pulsing;
    private bool _demo;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint command);

    private const uint NoSize = 0x0001;
    private const uint NoMove = 0x0002;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;
    private const uint WindowAbove = 3; // GW_HWNDPREV

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
        bool show = !Suspended && area is not null && Layout(area.Value) is not null;
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

        Stack();
        var s = _status();
        UpdateLowHp(s);
        UpdateBigHit(s);
        UpdateDps(s);
        UpdateFlight(s);
        UpdateChains(s);
        UpdateOnYou(s);
        UpdateParty(s);
        UpdateBoss(s);
    }

    /// <summary>Covers the game's client area exactly (pixels), above the game.</summary>
    private void Place(Int32Rect area)
    {
        _placed = area;
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, 0, area.X, area.Y, area.Width, area.Height, NoZOrder | NoActivate);
        var dpi = VisualTreeHelper.GetDpi(this);
        Stage.Width = area.Width / dpi.DpiScaleX;
        Stage.Height = area.Height / dpi.DpiScaleY;
        EdgeFlash.Width = Stage.Width;
        EdgeFlash.Height = Stage.Height;
        Arrange(Layout(area)!);
    }

    /// <summary>
    /// On top of everything while the game (or AVION2) is in front. Otherwise right above the game window, so the HUD
    /// stays on a game that is still on screen (e.g. while another screen has the focus), and whatever covers the
    /// game covers it too.
    /// </summary>
    private void Stack()
    {
        if (_demo || GameWindow.IsInFront)
        {
            if (!Topmost)
            {
                Topmost = true;
            }

            return;
        }

        if (Topmost)
        {
            Topmost = false;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        nint game = GameWindow.Handle;
        nint above = game == 0 ? 0 : GetWindow(game, WindowAbove);
        if (above != 0 && above != hwnd)
        {
            SetWindowPos(hwnd, above, 0, 0, 0, 0, NoMove | NoSize | NoActivate);
        }
    }

    private void Arrange(HudLayout layout)
    {
        double w = Stage.Width, h = Stage.Height;
        var hp = layout.Box(HudAnchors.SelfHp).ToPixels(w, h);
        var target = layout.Box(HudAnchors.Target).ToPixels(w, h);
        const double pad = 4;
        Canvas.SetLeft(HpGlow, hp.X - pad);
        Canvas.SetTop(HpGlow, hp.Y - pad);
        HpGlow.Width = hp.W + pad * 2;
        HpGlow.Height = hp.H + pad * 2;

        // Right-aligned to the HP bar's end (next to the level emblem): the DPS line, what hit you above it
        DpsLine.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double right = hp.X + hp.W;
        double dpsTop = hp.Y - h * 0.01 - DpsLine.DesiredSize.Height;
        Canvas.SetLeft(DpsLine, right - DpsLine.DesiredSize.Width);
        Canvas.SetTop(DpsLine, dpsTop);

        HitInfo.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(HitInfo, right - HitInfo.DesiredSize.Width);
        Canvas.SetTop(HitInfo, dpsTop - HitInfo.DesiredSize.Height - 2);

        // Centred below the boss's status pills and buff icons; the skill banner goes under it
        BossInfo.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(BossInfo, target.X + target.W / 2 - BossInfo.DesiredSize.Width / 2);
        Canvas.SetTop(BossInfo, target.Y + target.H + h * 0.058);

        // Bottom centre, just above the game's gathering panel (its title sits at ~76 % of the height) or crafting panel
        GatherBanner.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(GatherBanner, w / 2 - GatherBanner.DesiredSize.Width / 2);
        Canvas.SetTop(GatherBanner, h * _gatherBottom - GatherBanner.DesiredSize.Height);

        // Just right of the boss's round emblem, level with its centre (it hangs a little below the middle of the bar)
        double mark = Math.Max(12, h * 0.018);
        OnYouMark.Width = OnYouMark.Height = mark;
        OnYouDot.Width = OnYouDot.Height = mark * 0.3;
        Canvas.SetLeft(OnYouMark, target.X + target.W / 2 + h * 0.019);
        Canvas.SetTop(OnYouMark, target.Y + target.H / 2 + h * 0.011 - mark / 2);

        CastBanner.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // Centred under the boss bar, kept on screen when the bar is near an edge
        double bannerWidth = CastBanner.DesiredSize.Width;
        Canvas.SetLeft(CastBanner, Math.Clamp(target.X + target.W / 2 - bannerWidth / 2, 8, Math.Max(8, w - bannerWidth - 8)));
        // Below the boss's status pills and buff icons under the bar
        Canvas.SetTop(CastBanner, target.Y + target.H + h * 0.058 + (BossInfo.Visibility == Visibility.Visible ? BossInfo.DesiredSize.Height + 6 : 0));
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
        ShowWhatHitYou();
    }

    /// <summary>A hit we took (from the engine): kept for a second and a half, for "what hit you".</summary>
    public void AddHit(SelfHit hit)
    {
        long now = Environment.TickCount64;
        _hits.Enqueue((now, hit));
        while (_hits.Count > 0 && now - _hits.Peek().Ms > 1_500)
        {
            _hits.Dequeue();
        }
    }

    /// <summary>The biggest recent hit next to your HP bar, with the edge flash: "Wave of Fury −3,214 · Ultimate Berk".</summary>
    private void ShowWhatHitYou()
    {
        long now = Environment.TickCount64;
        var hits = _hits.Where(h => now - h.Ms <= 1_500).Select(h => h.Hit).ToList();
        if (_demo)
        {
            hits = [new SelfHit("Boss", "Skill name", 3214, true)];
        }

        if (hits.Count == 0)
        {
            return;
        }

        // Several hits of one skill in the burst add up
        var worst = hits.GroupBy(h => (h.Attacker, h.Skill))
            .Select(g => (g.Key.Attacker, g.Key.Skill, Amount: g.Sum(h => h.Amount), Count: g.Count()))
            .MaxBy(g => g.Amount);
        string times = worst.Count > 1 ? $" ×{worst.Count}" : string.Empty;
        HitInfo.Text = $"{worst.Skill}{times}  −{worst.Amount:N0}  ·  {worst.Attacker}";
        HitInfo.Visibility = Visibility.Visible;
        if (GameWindow.ClientArea is { } area && (_preview ?? Layout(area)) is { } layout)
        {
            Arrange(layout);
        }

        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2500))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3200))));
        HitInfo.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// Under the boss bar: exact HP, what is left, the time to kill at the party's damage so far and the fight clock,
    /// e.g. "5,268,856 / 6,800,000 · 77 % · ~1:12 left · 4:19". Only while fighting a boss.
    /// </summary>
    private void UpdateBoss(EngineStatus? status)
    {
        if (!_demo && (!_state.Stored.HudBossInfo || status is not { InCombat: true }))
        {
            BossInfo.Visibility = Visibility.Collapsed;
            return;
        }

        if (DateTime.UtcNow - _bossUpdated < TimeSpan.FromMilliseconds(500) && BossInfo.Visibility == Visibility.Visible)
        {
            return;
        }

        _bossUpdated = DateTime.UtcNow;
        string? text = null;
        if (_demo)
        {
            text = "5,268,856 / 6,800,000  ·  77 %  ·  ~1:12 left  ·  4:19";
        }
        else if (_snapshot() is { TargetIsBoss: true, TargetHp: { } hp, TargetMaxHp: > 0 } s)
        {
            double share = hp / (double)s.TargetMaxHp!.Value;
            string left = s.TotalDps > 0 && hp > 0 ? $"  ·  ~{HistoryStore.Duration(hp / s.TotalDps)} left" : string.Empty;
            text = $"{hp:N0} / {s.TargetMaxHp:N0}  ·  {share:P0}{left}  ·  {HistoryStore.Duration(s.DurationSeconds)}";
        }

        if (text is null)
        {
            BossInfo.Visibility = Visibility.Collapsed;
            return;
        }

        bool first = BossInfo.Visibility != Visibility.Visible || BossInfo.Text.Length != text.Length;
        BossInfo.Text = text;
        BossInfo.Visibility = Visibility.Visible;
        if (first && (_preview ?? (GameWindow.ClientArea is { } area ? Layout(area) : null)) is { } layout)
        {
            Arrange(layout);
        }
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
    /// <summary>
    /// While flying: seconds until we land at the current rate, white, orange under 10 s, red under 5 s. Just left of
    /// the game's flight arc, which sits left of the character (the camera keeps it in the centre).
    /// </summary>
    private void UpdateFlight(EngineStatus? status)
    {
        long? left = _demo ? 23_000 : _state.Stored.HudFlightTime ? status?.FlightLeftMs : null;
        if (left is not { } ms)
        {
            FlightInfo.Visibility = Visibility.Collapsed;
            return;
        }

        double seconds = ms / 1000.0;
        FlightText.Text = seconds < 10 ? $"{seconds:0.0} s" : $"{seconds:0} s";
        FlightText.Foreground = new SolidColorBrush(seconds < 5 ? Color.FromRgb(0xFF, 0x6B, 0x6B)
            : seconds < 10 ? Color.FromRgb(0xFF, 0xB0, 0x4A) : Colors.White);
        FlightInfo.Visibility = Visibility.Visible;
        FlightInfo.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(FlightInfo, Stage.Width * 0.425 - FlightInfo.DesiredSize.Width);
        Canvas.SetTop(FlightInfo, Stage.Height * 0.57 - FlightInfo.DesiredSize.Height / 2);
    }

    /// <summary>
    /// The chain skills we can use right now, e.g. "Resonance Crush  2.1 s" with a bar running out over the 3 s,
    /// centred above the buff icons over the skill bar. Several open at once stack, the first to run out on top.
    /// </summary>
    private void UpdateChains(EngineStatus? status)
    {
        var steps = _demo ? [new ChainStep(18020000, "Resonance Crush", 2_100)]
            : _state.Stored.HudChainSteps ? status?.Chains ?? [] : [];
        var layout = _preview ?? (GameWindow.ClientArea is { } area ? Layout(area) : null);
        if (steps.Count == 0 || layout is null)
        {
            ChainList.Visibility = Visibility.Collapsed;
            return;
        }

        double w = Stage.Width, h = Stage.Height;
        var hp = layout.Box(HudAnchors.SelfHp).ToPixels(w, h);
        double font = Math.Max(12, h * 0.017);
        double width = font * 13;
        ChainList.Children.Clear();
        foreach (var step in steps)
        {
            double left = Math.Clamp(step.LeftMs / (double)ChainSteps.WindowMs, 0, 1);
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, font * 0.35) };
            var text = new DockPanel { Width = width };
            var seconds = new TextBlock { Text = $"{step.LeftMs / 1000.0:0.0} s", Foreground = Brushes.White, FontSize = font * 0.85, VerticalAlignment = VerticalAlignment.Bottom };
            DockPanel.SetDock(seconds, Dock.Right);
            text.Children.Add(seconds);
            text.Children.Add(new TextBlock
            {
                Text = step.Name,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0xC7, 0x6E)),
                FontSize = font,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            row.Children.Add(text);

            // Runs out over the 3 s: gold, red in the last second
            var track = new Grid { Width = width, Height = Math.Max(3, h * 0.004), Margin = new Thickness(0, 2, 0, 0) };
            track.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x16, 0x1E)) });
            track.Children.Add(new Border
            {
                Background = new SolidColorBrush(step.LeftMs < 1_000 ? Color.FromRgb(0xFF, 0x6B, 0x6B) : Color.FromRgb(0xF0, 0xB4, 0x48)),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = width * left,
            });
            row.Children.Add(track);
            ChainList.Children.Add(row);
        }

        ChainList.Visibility = Visibility.Visible;
        ChainList.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(ChainList, w / 2 - ChainList.DesiredSize.Width / 2);
        // The buff icons sit about 9 % of the height above the HP bar
        Canvas.SetTop(ChainList, hp.Y - h * 0.095 - ChainList.DesiredSize.Height);
    }

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

    /// <summary>
    /// The party's DPS right under the game's group list, in its style: class emblem, name, a thin bar (share of
    /// the damage) at the game's row width and spacing, best first. Shows during a fight and for 30 s after it.
    /// </summary>
    private void UpdateParty(EngineStatus? status)
    {
        if (status is { InCombat: true })
        {
            _lastCombatMs = Environment.TickCount64;
        }

        var layout = _preview ?? (GameWindow.ClientArea is { } area ? Layout(area) : null);
        bool wanted = (_demo || (_state.Stored.HudPartyMeter && Environment.TickCount64 - _lastCombatMs < 30_000)) &&
                      layout is { PartyRows: > 0 } && layout.Boxes.ContainsKey(HudAnchors.Party);
        if (!wanted)
        {
            PartyMeter.Visibility = Visibility.Collapsed;
            return;
        }

        if (DateTime.UtcNow - _partyUpdated < TimeSpan.FromMilliseconds(500) && PartyMeter.Visibility == Visibility.Visible)
        {
            return;
        }

        _partyUpdated = DateTime.UtcNow;
        var rows = _demo
            ? [("You", Avion2.Protocol.GameClass.Chanter, 6279.0, 0.43, true), ("Party member", Avion2.Protocol.GameClass.Cleric, 4120.0, 0.28, false),
               ("Party member", Avion2.Protocol.GameClass.Gladiator, 2980.0, 0.2, false), ("Party member", Avion2.Protocol.GameClass.Ranger, 1310.0, 0.09, false)]
            : (_snapshot()?.Rows ?? []).Where(r => r.Total > 0).Take(8).Select(r => (r.IsSelf ? r.Name : r.Name, r.Class, r.Dps, r.Share, r.IsSelf)).ToList();
        if (rows.Count == 0)
        {
            PartyMeter.Visibility = Visibility.Collapsed;
            return;
        }

        // Geometry from the calibrated group list
        double w = Stage.Width, h = Stage.Height;
        var box = layout!.Box(HudAnchors.Party).ToPixels(w, h);
        double barHeight = Math.Max(3, h * 0.0042);
        double pitch = layout.PartyRows > 1 ? (box.H - barHeight) / (layout.PartyRows - 1) : h * 0.037;
        double font = Math.Max(10, pitch * 0.29);
        double emblem = pitch * 0.62;
        double top = box.Y + box.H + pitch * 0.45;
        double max = rows.Max(r => r.Item3);

        PartyMeter.Children.Clear();
        PartyMeter.Width = box.W + emblem + pitch * 0.2;
        Canvas.SetLeft(PartyMeter, box.X - emblem - pitch * 0.2);
        Canvas.SetTop(PartyMeter, top);
        PartyMeter.Children.Add(new TextBlock
        {
            Text = _demo ? "DPS · 1:20" : $"DPS · {HistoryStore.Duration(_snapshot()?.DurationSeconds ?? 0)}",
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xC2, 0x7A)),
            FontSize = font * 0.9,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(emblem + pitch * 0.2, 0, 0, pitch * 0.12),
        });

        foreach (var (name, cls, dps, share, self) in rows)
        {
            var row = new Grid { Height = pitch };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(emblem + pitch * 0.2) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            if (ClassIcons.For(cls) is { } icon)
            {
                row.Children.Add(new Image { Source = icon, Width = emblem, Height = emblem, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center });
            }

            var text = new DockPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, pitch * 0.06, 0, 0) };
            var value = new TextBlock { Text = $"{dps:N0}  {share:P0}", Foreground = Brushes.White, FontSize = font };
            DockPanel.SetDock(value, Dock.Right);
            text.Children.Add(value);
            text.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = font,
                Foreground = new SolidColorBrush(self ? Color.FromRgb(0xF3, 0xC7, 0x6E) : Color.FromRgb(0x7F, 0xD8, 0xFF)),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            // Bar where the game draws its HP bar: dark track, gold fill for your share
            var track = new Grid { Height = barHeight, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, pitch * 0.36, 0, 0) };
            track.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x16, 0x1E)) });
            track.Children.Add(new Border
            {
                Background = new SolidColorBrush(self ? Color.FromRgb(0xF0, 0xB4, 0x48) : Color.FromRgb(0xE0, 0x8A, 0x3A)),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = box.W * Math.Clamp(dps / Math.Max(max, 1), 0, 1),
            });
            Grid.SetColumn(track, 1);
            row.Children.Add(track);
            PartyMeter.Children.Add(row);
        }

        PartyMeter.Visibility = Visibility.Visible;
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

    /// <summary>
    /// A small red crosshair beside the boss's emblem while the boss we have selected targets us; it pulses for a
    /// moment when the boss turns to us.
    /// </summary>
    private void UpdateOnYou(EngineStatus? status)
    {
        bool onYou = _demo || (_state.Stored.HudBossOnYou && status is { BossOnYou: true });
        if (onYou == (OnYouMark.Visibility == Visibility.Visible))
        {
            return;
        }

        if (!onYou)
        {
            OnYouMark.BeginAnimation(OpacityProperty, null);
            OnYouMark.Visibility = Visibility.Collapsed;
            return;
        }

        OnYouMark.Visibility = Visibility.Visible;
        OnYouMark.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(250))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(3),
            FillBehavior = FillBehavior.Stop,
        });
    }

    private readonly DispatcherTimer _gatherTimer = new() { Interval = TimeSpan.FromSeconds(15) };

    /// <summary>Where the outcome banner ends, as a share of the height: just above the game's progress panel.</summary>
    private double _gatherBottom = 0.728;

    /// <summary>
    /// The outcome of the gathering that just started (the server decides it up front): "Will succeed" in green
    /// ("· Fine" when the item is the better grade), "Will fail" in red. Hidden when it ends (or after 15 s).
    /// </summary>
    public void ShowGather(Avion2.Protocol.GatherStartEvent start) =>
        ShowOutcome(!start.Success ? "✖  Will fail" : start.Fine ? "✔  Will succeed · Fine" : "✔  Will succeed",
            start.Success, start.Fine, 0.728, TimeSpan.FromSeconds(15));

    /// <summary>
    /// The same for a craft. Its panel sits lower (the title at ~84 % of the height), and a combo (the better recipe,
    /// two items) runs a second race, so it can take twice as long; a failed combo yields nothing.
    /// </summary>
    public void ShowCraft(Avion2.Protocol.CraftStartEvent start) =>
        ShowOutcome(!start.Success ? start.Combo ? "✖  Combo will fail" : "✖  Will fail" : start.Combo ? "✔  Will succeed · Combo ×2" : "✔  Will succeed",
            start.Success, start.Combo, 0.81, TimeSpan.FromSeconds(60));

    private void ShowOutcome(string text, bool success, bool better, double bottom, TimeSpan timeout)
    {
        if (!_state.Stored.HudGatherOutcome || GameWindow.ClientArea is not { } area)
        {
            return;
        }

        var color = !success ? Color.FromRgb(0xFF, 0x6B, 0x6B) : better ? Color.FromRgb(0xF0, 0xC8, 0x5A) : Color.FromRgb(0x5B, 0xD4, 0x8A);
        _gatherBottom = bottom;
        GatherText.Text = text;
        GatherText.Foreground = new SolidColorBrush(color);
        GatherBanner.BorderBrush = new SolidColorBrush(Color.FromArgb(0xB0, color.R, color.G, color.B));
        GatherBanner.Visibility = Visibility.Visible;
        if (Layout(area) is { } layout)
        {
            Arrange(layout);
        }

        _gatherTimer.Stop();
        _gatherTimer.Interval = timeout;
        _gatherTimer.Tick -= GatherTimeout;
        _gatherTimer.Tick += GatherTimeout;
        _gatherTimer.Start();
    }

    public void HideGather()
    {
        _gatherTimer.Stop();
        GatherBanner.Visibility = Visibility.Collapsed;
    }

    private void GatherTimeout(object? sender, EventArgs e) => HideGather();

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

    private HudLayout? _preview;

    /// <summary>
    /// Developer aid: every effect drawn over <paramref name="picture"/> with <paramref name="layout"/> (no game needed),
    /// returned as an image.
    /// </summary>
    internal BitmapSource PreviewOn(BitmapSource picture, HudLayout layout)
    {
        _timer.Stop();
        _preview = layout;
        _demo = true;
        Stage.Width = picture.PixelWidth;
        Stage.Height = picture.PixelHeight;
        EdgeFlash.Width = Stage.Width;
        EdgeFlash.Height = Stage.Height;
        CastBoss.Text = "Boss";
        CastSkill.Text = "Skill name";
        CastBanner.Visibility = Visibility.Visible;
        CastBanner.Opacity = 1;
        _gatherBottom = 0.728;
        GatherText.Text = "✔  Will succeed · Fine";
        GatherText.Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xC8, 0x5A));
        GatherBanner.BorderBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0xF0, 0xC8, 0x5A));
        GatherBanner.Visibility = Visibility.Visible;
        HpGlow.Visibility = Visibility.Visible;
        HpGlow.Opacity = 1;
        OnYouMark.Visibility = Visibility.Visible;
        UpdateDps(null);
        UpdateChains(null);
        UpdateParty(null);
        UpdateBoss(null);
        ShowWhatHitYou();
        HitInfo.BeginAnimation(OpacityProperty, null);
        HitInfo.Opacity = 1;
        DpsLine.Visibility = Visibility.Visible;
        Arrange(layout);
        Stage.Measure(new Size(Stage.Width, Stage.Height));
        Stage.Arrange(new Rect(0, 0, Stage.Width, Stage.Height));
        Stage.UpdateLayout();

        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
        {
            dc.DrawImage(picture, new Rect(0, 0, picture.PixelWidth, picture.PixelHeight));
        }

        // The stage at its real coordinates over the picture (a VisualBrush would stretch it to its content)
        var bitmap = new RenderTargetBitmap(picture.PixelWidth, picture.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(background);
        bitmap.Render(Stage);
        return bitmap;
    }

    /// <summary>Re-reads the layout (after calibration).</summary>
    public void Relayout() => _placed = default;

    private static void Fade(UIElement element, double to, int ms) =>
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)));
}
