using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Avion2.Analysis;
using Avion2.Protocol;

namespace Avion2.Overlay;

/// <summary>Where the replay's camera looks.</summary>
public enum ReplayCamera
{
    /// <summary>The whole fight (wheel zooms, drag pans).</summary>
    Fit,

    /// <summary>Follows you.</summary>
    You,

    /// <summary>Follows the main target.</summary>
    Boss,
}

/// <summary>
/// Top-down replay of a fight trace at <see cref="Time"/>. Players are their class emblem in a ring of their class
/// colour with a facing arrow and an HP bar; the boss is a large marker with its HP ring, name and the skill it is
/// casting; monsters are small dots. Hits draw attack lines, crit bursts and damage numbers (crits gold, back attacks
/// cyan), heals draw green lines and numbers, buffs show as pips over a unit, deaths leave a skull. Our own position
/// is drawn dashed where it is estimated (the server only tells us where we are now and then). The camera fits the
/// fight or follows you or the boss, smoothly; wheel zooms, drag pans, double-click resets.
/// </summary>
public sealed class ReplayView : FrameworkElement
{
    private const int TrailMs = 4_000;
    private const int HitFlashMs = 450;
    private const int BurstMs = 350;
    private const int NumberMs = 900;
    private const int CastLabelMs = 1_600;

    private double _zoom = 1, _zoomShown = 1;
    private Vector _pan; // world units, Fit mode
    private Point? _camera; // world point shown at the centre (smoothed)
    private Point? _dragFrom;
    private Point _pressedAt;
    private Dictionary<uint, TraceEntity> _byId = new();
    private Dictionary<uint, uint> _owner = new();
    private Rect _bounds = Rect.Empty;

    /// <summary>Where each entity was drawn in the last frame, for clicks.</summary>
    private readonly List<(uint Id, Point P, double Radius)> _dots = new();

    /// <summary>The entity whose info card is shown (picked by clicking its dot).</summary>
    private uint? _inspected;

    public ReplayView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public static readonly DependencyProperty TraceProperty = DependencyProperty.Register(
        nameof(Trace), typeof(FightTrace), typeof(ReplayView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) =>
        {
            var view = (ReplayView)d;
            view.Index();
            // A refreshed live trace keeps the view; another fight resets it.
            if (e.OldValue is not FightTrace old || e.NewValue is not FightTrace now || old.StartMs != now.StartMs)
            {
                view._inspected = null;
                view.ResetView();
            }
        }));

    public FightTrace? Trace
    {
        get => (FightTrace?)GetValue(TraceProperty);
        set => SetValue(TraceProperty, value);
    }

    public static readonly DependencyProperty TimeProperty = DependencyProperty.Register(
        nameof(Time), typeof(double), typeof(ReplayView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Milliseconds since the trace start.</summary>
    public double Time
    {
        get => (double)GetValue(TimeProperty);
        set => SetValue(TimeProperty, value);
    }

    public static readonly DependencyProperty SelectedActorProperty = DependencyProperty.Register(
        nameof(SelectedActor), typeof(uint?), typeof(ReplayView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public uint? SelectedActor
    {
        get => (uint?)GetValue(SelectedActorProperty);
        set => SetValue(SelectedActorProperty, value);
    }

    public static readonly DependencyProperty CameraProperty = DependencyProperty.Register(
        nameof(Camera), typeof(ReplayCamera), typeof(ReplayView),
        new FrameworkPropertyMetadata(ReplayCamera.Fit, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) =>
            {
                var view = (ReplayView)d;
                // Following starts a little closer than the whole fight
                view._zoom = (ReplayCamera)e.NewValue == ReplayCamera.Fit ? 1 : Math.Max(view._zoom, 2.2);
                view._pan = default;
            }));

    public ReplayCamera Camera
    {
        get => (ReplayCamera)GetValue(CameraProperty);
        set => SetValue(CameraProperty, value);
    }

    public GameData Data { get; set; } = GameData.Empty;

    /// <summary>Shows the info card of an entity (null closes it).</summary>
    public void Inspect(uint? id)
    {
        _inspected = id;
        InvalidateVisual();
    }

    public void ResetView()
    {
        _zoom = _zoomShown = 1;
        _pan = default;
        _camera = null;
        InvalidateVisual();
    }

    /// <summary>Zooms in or out around the centre (buttons).</summary>
    public void ZoomBy(double factor)
    {
        _zoom = Math.Clamp(_zoom * factor, 0.3, 12);
        InvalidateVisual();
    }

    private void Index()
    {
        var trace = Trace;
        _byId = trace?.Entities.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First()) ?? new();
        _owner = _byId.ToDictionary(p => p.Key, p => p.Value.OwnerId ?? p.Key);
        _bounds = trace is null ? Rect.Empty : Bounds(trace);
    }

    private uint Owner(uint id) => _owner.GetValueOrDefault(id, id);

    /// <summary>
    /// World bounds of where the fight happened: positions during it (each entity's position at a few instants),
    /// trimmed of the outer 5 % so one far-off fix doesn't shrink everything. Summons follow their owners.
    /// </summary>
    private static Rect Bounds(FightTrace trace)
    {
        var xs = new List<float>();
        var ys = new List<float>();
        foreach (var e in trace.Entities.Where(e => e.Kind != TraceEntityKind.Summon))
        {
            for (int t = 0; t <= trace.DurationMs; t += Math.Max(500, trace.DurationMs / 40))
            {
                if (trace.PositionAt(e.Id, t) is { } p)
                {
                    xs.Add(p.X);
                    ys.Add(p.Y);
                }
            }
        }

        if (xs.Count == 0)
        {
            return Rect.Empty;
        }

        xs.Sort();
        ys.Sort();
        int lo = xs.Count / 20, hi = xs.Count - 1 - xs.Count / 20;
        double minX = xs[lo], maxX = xs[hi], minY = ys[lo], maxY = ys[hi];
        // At least 16 m across, so a fight around one spot doesn't zoom into noise.
        double size = Math.Max(Math.Max(maxX - minX, maxY - minY), 1_600) * 1.4;
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        return new Rect(cx - size / 2, cy - size / 2, size, size);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        dc.DrawRectangle((Brush)FindResource("StageBrush"), null, new Rect(size));
        var trace = Trace;
        var muted = (Brush)FindResource("MutedBrush");
        var ink = (Brush)FindResource("TextBrush");
        var accent = ((SolidColorBrush)FindResource("AccentBrush")).Color;
        var bodyFont = (FontFamily)FindResource("BodyFont");
        var semibold = new Typeface(bodyFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var bold = new Typeface(bodyFont, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Text(string s, double em, Brush brush, bool heavy = false) =>
            new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, heavy ? bold : semibold, em, brush, dpi);

        if (trace is null)
        {
            dc.DrawText(Text("No replay for this fight (recorded before replays existed).", 12, muted), new Point(12, 12));
            return;
        }

        if (_bounds.IsEmpty)
        {
            dc.DrawText(Text("No positions were seen during this fight.", 12, muted), new Point(12, 12));
            return;
        }

        int t = (int)Time;

        // Camera: what to centre on, smoothly
        double baseScale = Math.Min(size.Width, size.Height) / _bounds.Width;
        var fitCenter = new Point(_bounds.X + _bounds.Width / 2 + _pan.X, _bounds.Y + _bounds.Height / 2 + _pan.Y);
        uint? follow = Camera switch
        {
            ReplayCamera.You => trace.SelfId,
            ReplayCamera.Boss => trace.PrimaryTargetId,
            _ => null,
        };
        var want = follow is { } f && trace.PositionAt(f, t) is { } fp ? new Point(fp.X, fp.Y) : fitCenter;
        var cam = _camera ?? want;
        cam = new Point(cam.X + (want.X - cam.X) * 0.18, cam.Y + (want.Y - cam.Y) * 0.18);
        _zoomShown += (_zoom - _zoomShown) * 0.25;
        double scale = baseScale * _zoomShown;
        bool settling = (cam - want).Length * scale > 0.5 || Math.Abs(_zoom - _zoomShown) > 0.002;
        _camera = settling ? cam : want;
        if (settling)
        {
            Dispatcher.BeginInvoke(InvalidateVisual, DispatcherPriority.Render);
        }

        var center = _camera.Value;
        Point S(double x, double y) => new(size.Width / 2 + (x - center.X) * scale, size.Height / 2 - (y - center.Y) * scale);

        DrawGrid(dc, size, scale, center, muted, Text);

        // Where everyone is
        var positions = new Dictionary<uint, (Point P, bool Estimated)>();
        foreach (var e in trace.Entities)
        {
            if (e.Kind is TraceEntityKind.Monster or TraceEntityKind.Summon && trace.IsDeadAt(e.Id, t))
            {
                continue;
            }

            uint anchor = e.Kind == TraceEntityKind.Summon && e.Track.Count == 0 && e.OwnerId is { } o ? o : e.Id;
            if (trace.PositionAt(anchor, t) is { } p)
            {
                positions[e.Id] = (S(p.X, p.Y), p.Estimated);
            }
        }

        // Deaths so far: a skull where it happened
        foreach (var death in trace.Deaths.Where(d => d.T <= t))
        {
            if (!_byId.TryGetValue(death.EntityId, out var dead) || dead.Kind is TraceEntityKind.Summon ||
                trace.PositionAt(death.EntityId, death.T) is not { } where)
            {
                continue;
            }

            bool important = dead.Kind is TraceEntityKind.Self or TraceEntityKind.Player or TraceEntityKind.Boss;
            DrawSkull(dc, S(where.X, where.Y), important ? 7 : 4, important ? Color.FromArgb(220, 0xF2, 0xF4, 0xF8) : Color.FromArgb(90, 0x9A, 0x9F, 0xAE));
        }

        // Trails (players and bosses)
        foreach (var e in trace.Entities.Where(e => e.Kind is not (TraceEntityKind.Summon or TraceEntityKind.Monster)))
        {
            var trail = e.Track.Where(p => p.T <= t && p.T >= t - TrailMs).Select(p => S(p.X, p.Y)).ToList();
            if (positions.TryGetValue(e.Id, out var now))
            {
                trail.Add(now.P);
            }

            var color = ColorOf(e, accent);
            for (int i = 1; i < trail.Count; i++)
            {
                dc.DrawLine(new Pen(WithAlpha(color, (byte)(20 + 110 * i / trail.Count)), 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                    trail[i - 1], trail[i]);
            }
        }

        // Attack lines and crit bursts
        foreach (var hit in trace.Hits.Where(h => h.T <= t && h.T > t - HitFlashMs))
        {
            uint from = positions.ContainsKey(hit.ActorId) ? hit.ActorId : Owner(hit.ActorId);
            if (!positions.TryGetValue(from, out var a) || !positions.TryGetValue(hit.TargetId, out var b))
            {
                continue;
            }

            double fade = 1 - (t - hit.T) / (double)HitFlashMs;
            var color = _byId.TryGetValue(Owner(hit.ActorId), out var actor) ? ColorOf(actor, accent) : Colors.White;
            dc.DrawLine(new Pen(WithAlpha(color, (byte)(200 * fade)), hit.Critical ? 2.2 : 1.1), a.P, b.P);
            if (hit.Critical && t - hit.T < BurstMs)
            {
                DrawBurst(dc, b.P, (t - hit.T) / (double)BurstMs);
            }
        }

        // Heals: green lines
        foreach (var heal in trace.Heals.Where(h => h.T <= t && h.T > t - HitFlashMs))
        {
            uint from = positions.ContainsKey(heal.ActorId) ? heal.ActorId : Owner(heal.ActorId);
            if (positions.TryGetValue(from, out var a) && positions.TryGetValue(heal.TargetId, out var b) && from != heal.TargetId)
            {
                double fade = 1 - (t - heal.T) / (double)HitFlashMs;
                dc.DrawLine(new Pen(WithAlpha(Color.FromRgb(0x5B, 0xD4, 0x8A), (byte)(190 * fade)), 1.6) { DashStyle = new DashStyle([3, 2], 0) }, a.P, b.P);
            }
        }

        // Units, back to front
        _dots.Clear();
        foreach (var e in trace.Entities.OrderBy(e => e.Kind switch
                 {
                     TraceEntityKind.Monster => 0, TraceEntityKind.Summon => 1, TraceEntityKind.Boss => 2, TraceEntityKind.Player => 3, _ => 4,
                 }))
        {
            if (!positions.TryGetValue(e.Id, out var at))
            {
                continue;
            }

            switch (e.Kind)
            {
                case TraceEntityKind.Monster:
                    DrawMonster(dc, trace, e, at.P, at.Estimated, t);
                    break;
                case TraceEntityKind.Summon:
                    var ownerColor = _byId.TryGetValue(Owner(e.Id), out var owner) ? ColorOf(owner, accent) : Colors.White;
                    dc.DrawEllipse(WithAlpha(ownerColor, 170), null, at.P, 3.5, 3.5);
                    _dots.Add((e.Id, at.P, 3.5));
                    break;
                case TraceEntityKind.Boss:
                    DrawBoss(dc, trace, e, at.P, t, Text);
                    break;
                default:
                    DrawPlayer(dc, trace, e, at.P, at.Estimated, t, accent, Text, ink);
                    break;
            }
        }

        DrawNumbers(dc, trace, positions, t, Text);

        // Server freeze banner
        if (trace.LagAt(t) is { } lag)
        {
            var banner = Text($"⚠ Server froze here for {lag.Length / 1000.0:0.0} s: positions jump when it catches up", 12,
                new SolidColorBrush(Color.FromRgb(0xFC, 0xD3, 0x4D)));
            var box = new Rect(10, 8, banner.Width + 16, banner.Height + 8);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xCC, 0x3A, 0x2A, 0x05)), new Pen(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)), 1), box, 4, 4);
            dc.DrawText(banner, new Point(box.X + 8, box.Y + 4));
        }

        if (_inspected is { } inspected && _byId.TryGetValue(inspected, out var picked))
        {
            DrawInfoCard(dc, trace, picked, t, size, Text, muted, ink);
        }
    }

    private void DrawPlayer(DrawingContext dc, FightTrace trace, TraceEntity e, Point at, bool estimated, int t, Color accent,
        Func<string, double, Brush, bool, FormattedText> text, Brush ink)
    {
        bool self = e.Kind == TraceEntityKind.Self;
        bool dead = trace.IsDeadAt(e.Id, t);
        double r = self ? 13 : 12;
        var color = ColorOf(e, accent);
        byte alpha = dead ? (byte)90 : estimated ? (byte)150 : (byte)255;

        if (e.Id == _inspected || e.Id == SelectedActor)
        {
            dc.DrawEllipse(null, new Pen(WithAlpha(accent, 220), 2), at, r + 6, r + 6);
        }

        // Facing arrow outside the ring
        if (!dead && trace.FacingAt(e.Id, t) is { } facing)
        {
            DrawArrow(dc, at, r, facing, Color.FromArgb(alpha, color.R, color.G, color.B));
        }

        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(alpha * 0.9), 0x0B, 0x13, 0x22)), null, at, r, r);
        var ring = estimated
            ? new Pen(WithAlpha(color, 210), 2) { DashStyle = DashStyles.Dash }
            : new Pen(WithAlpha(color, alpha), self ? 3 : 2.5);
        dc.DrawEllipse(null, ring, at, r, r);
        if (ClassIcons.For(e.Class) is { } icon)
        {
            dc.PushOpacity(alpha / 255.0);
            dc.DrawImage(icon, new Rect(at.X - r * 0.72, at.Y - r * 0.72, r * 1.44, r * 1.44));
            dc.Pop();
        }

        if (dead)
        {
            DrawSkull(dc, at, 7, Color.FromArgb(230, 0xF2, 0xF4, 0xF8));
        }

        _dots.Add((e.Id, at, r));
        DrawBuffPips(dc, trace, e.Id, new Point(at.X, at.Y - r - 6), t);

        // HP bar and name below
        double y = at.Y + r + 4;
        if (e.MaxHp > 0 && trace.HpAt(e.Id, t) is { } hp)
        {
            DrawBar(dc, new Rect(at.X - 16, y, 32, 3.5), hp / (double)e.MaxHp, Color.FromRgb(0x4A, 0xDE, 0x80));
            y += 5;
        }

        var name = text(self ? "You" : e.Name, 11, self ? new SolidColorBrush(accent) : ink, false);
        dc.DrawText(name, new Point(at.X - name.Width / 2, y));
    }

    private void DrawBoss(DrawingContext dc, FightTrace trace, TraceEntity e, Point at, int t, Func<string, double, Brush, bool, FormattedText> text)
    {
        const double r = 19;
        var red = Color.FromRgb(0xE5, 0x48, 0x4D);
        if (trace.FacingAt(e.Id, t) is { } facing)
        {
            DrawArrow(dc, at, r + 6, facing, Color.FromRgb(0xF0, 0x8A, 0x6C));
        }

        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x2A, 0x10, 0x14)), new Pen(new SolidColorBrush(red), 2.5), at, r, r);
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(70, 0xE5, 0x48, 0x4D)), 5), at, r + 5, r + 5);
        if (e.MaxHp > 0 && trace.HpAt(e.Id, t) is { } hp)
        {
            DrawArc(dc, at, r + 5, Math.Clamp(hp / (double)e.MaxHp, 0, 1), new Pen(new SolidColorBrush(Color.FromRgb(0xF0, 0x58, 0x4F)), 5));
            var pct = text($"{Math.Clamp(hp / (double)e.MaxHp, 0, 1):P0}", 11, Brushes.White, true);
            dc.DrawText(pct, new Point(at.X - pct.Width / 2, at.Y - pct.Height / 2));
        }

        if (e.Id == _inspected)
        {
            dc.DrawEllipse(null, new Pen((Brush)FindResource("AccentBrush"), 2), at, r + 10, r + 10);
        }

        _dots.Add((e.Id, at, r));
        DrawBuffPips(dc, trace, e.Id, new Point(at.X, at.Y + r + 10), t);

        // Name, and the skill it is casting above it
        var name = text(e.Name, 12, Brushes.White, true);
        double top = at.Y - r - 12 - name.Height;
        dc.DrawText(name, new Point(at.X - name.Width / 2, top));
        if (trace.Casts.LastOrDefault(c => c.ActorId == e.Id && c.T <= t && c.T > t - CastLabelMs) is { } cast &&
            Data.SkillName(cast.SkillId) is var skill && Named(skill))
        {
            double age = (t - cast.T) / (double)CastLabelMs;
            var label = text(skill, 13, Brushes.White, true);
            var box = new Rect(at.X - label.Width / 2 - 10, top - label.Height - 10, label.Width + 20, label.Height + 6);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(235, 0x2A, 0x0C, 0x0E)), new Pen(new SolidColorBrush(red), 1.2), box, 4, 4);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, 0xE5, 0x48, 0x4D)), null, new Rect(box.X + 1, box.Bottom - 3, (box.Width - 2) * (1 - age), 2));
            dc.DrawText(label, new Point(box.X + 10, box.Y + 3));
        }
    }

    private void DrawMonster(DrawingContext dc, FightTrace trace, TraceEntity e, Point at, bool estimated, int t)
    {
        const double r = 5;
        var fill = new SolidColorBrush(Color.FromArgb(estimated ? (byte)90 : (byte)200, 0x9A, 0x9F, 0xAE));
        dc.DrawEllipse(fill, new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 1), at, r, r);
        if (e.Id == trace.PrimaryTargetId || e.Id == _inspected)
        {
            dc.DrawEllipse(null, new Pen((Brush)FindResource("AccentBrush"), 1.5), at, r + 3, r + 3);
        }

        if (e.MaxHp > 0 && trace.HpAt(e.Id, t) is { } hp && hp < e.MaxHp)
        {
            DrawArc(dc, at, r + 2, Math.Clamp(hp / (double)e.MaxHp, 0, 1), new Pen(new SolidColorBrush(Color.FromArgb(200, 0xF0, 0x58, 0x4F)), 1.5));
        }

        _dots.Add((e.Id, at, r));
    }

    /// <summary>Up to six pips over a unit: buffs blue, debuffs (from monsters) red.</summary>
    private void DrawBuffPips(DrawingContext dc, FightTrace trace, uint id, Point at, int t)
    {
        var buffs = trace.BuffsAt(id, t).GroupBy(b => b.AbnormalId).Select(g => g.First()).Take(6).ToList();
        if (buffs.Count == 0)
        {
            return;
        }

        double x = at.X - (buffs.Count * 6 - 2) / 2.0;
        foreach (var buff in buffs)
        {
            bool hostile = _byId.TryGetValue(Owner(buff.CasterId), out var caster) && caster.Kind is TraceEntityKind.Monster or TraceEntityKind.Boss;
            var color = hostile ? Color.FromRgb(0xF0, 0x58, 0x4F) : Color.FromRgb(0x7F, 0xD8, 0xFF);
            dc.DrawRoundedRectangle(new SolidColorBrush(color), null, new Rect(x, at.Y - 2, 4, 4), 1, 1);
            x += 6;
        }
    }

    /// <summary>Damage numbers rising from the target (crits gold and larger, back attacks cyan), heals green.</summary>
    private void DrawNumbers(DrawingContext dc, FightTrace trace, Dictionary<uint, (Point P, bool Estimated)> positions, int t,
        Func<string, double, Brush, bool, FormattedText> text)
    {
        int shown = 0;
        uint focus = SelectedActor ?? trace.SelfId ?? 0;
        foreach (var hit in trace.Hits.Where(h => h.T <= t && h.T > t - NumberMs && h.Amount > 0 && (Owner(h.ActorId) == focus || h.TargetId == focus))
                     .OrderByDescending(h => h.T))
        {
            if (shown++ > 24 || !positions.TryGetValue(hit.TargetId, out var at))
            {
                continue;
            }

            double age = (t - hit.T) / (double)NumberMs;
            bool incoming = hit.TargetId == focus;
            var color = incoming ? Color.FromRgb(0xFF, 0x8A, 0x80)
                : hit.Critical ? Color.FromRgb(0xFF, 0xD1, 0x5C)
                : hit.Back ? Color.FromRgb(0x7F, 0xE0, 0xFF)
                : Colors.White;
            var label = text((incoming ? "−" : string.Empty) + Compact(hit.Amount), hit.Critical ? 15 : 11, new SolidColorBrush(WithAlpha(color, (byte)(255 * (1 - age))).Color), hit.Critical);
            double jitter = (hit.T * 37 % 40) - 20;
            dc.DrawText(label, new Point(at.P.X + jitter - label.Width / 2, at.P.Y - 22 - age * 36));
        }

        foreach (var heal in trace.Heals.Where(h => h.T <= t && h.T > t - NumberMs && (Owner(h.ActorId) == focus || h.TargetId == focus)).Take(8))
        {
            if (!positions.TryGetValue(heal.TargetId, out var at))
            {
                continue;
            }

            double age = (t - heal.T) / (double)NumberMs;
            var label = text("+" + Compact(heal.Amount), 11, new SolidColorBrush(WithAlpha(Color.FromRgb(0x5B, 0xD4, 0x8A), (byte)(255 * (1 - age))).Color), false);
            dc.DrawText(label, new Point(at.P.X + 14, at.P.Y - 6 - age * 30));
        }
    }

    /// <summary>Details of the clicked entity at the current time, in the lower left corner.</summary>
    private void DrawInfoCard(DrawingContext dc, FightTrace trace, TraceEntity e, int t, Size size,
        Func<string, double, Brush, bool, FormattedText> text, Brush muted, Brush ink)
    {
        static string K(long v) => v >= 10_000 ? (v / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K" : v.ToString("N0", CultureInfo.InvariantCulture);
        string legion = e.Legion is { Length: > 0 } l ? $" ‹{l}›" : string.Empty;
        string kind = e.Kind switch
        {
            TraceEntityKind.Self => $"You · {e.Class}{legion}",
            TraceEntityKind.Player => (e.Class == GameClass.Unknown ? "Player" : $"Player · {e.Class}") + legion,
            TraceEntityKind.Boss => "Boss",
            TraceEntityKind.Summon => $"Summon of {(e.OwnerId is { } o && _byId.TryGetValue(o, out var owner) ? owner.Name : "?")}",
            _ => "Monster",
        };

        var lines = new List<(string Text, Brush Brush)> { (kind, muted) };
        if (trace.IsDeadAt(e.Id, t))
        {
            lines.Add(("Dead", new SolidColorBrush(Color.FromRgb(0xF0, 0x58, 0x4F))));
        }
        else if (e.MaxHp > 0 && trace.HpAt(e.Id, t) is { } hp)
        {
            lines.Add(($"HP {Math.Clamp(hp / (double)e.MaxHp, 0, 1):P0} ({hp:N0} / {e.MaxHp:N0})", ink));
        }

        lines.Add((trace.FixAgeMs(e.Id, t) switch
        {
            null => "No position fix: drawn where its owner is",
            <= 3_000 and var age => $"Position: real fix {age / 1000.0:0.0} s away",
            var age => $"Position: estimated (closest fix {age / 1000.0:0.0} s away)",
        }, muted));

        if (trace.LastCast(e.Id, t) is { } cast && Named(Data.SkillName(cast.SkillId)))
        {
            lines.Add(($"Last skill: {Data.SkillName(cast.SkillId)} ({(t - cast.T) / 1000.0:0.0} s ago)", ink));
        }

        bool counts(uint actor) => actor == e.Id || (e.Kind is TraceEntityKind.Self or TraceEntityKind.Player && Owner(actor) == e.Id);
        long dealt = trace.Hits.Where(h => h.T <= t && counts(h.ActorId)).Sum(h => h.Amount);
        long taken = trace.Hits.Where(h => h.T <= t && h.TargetId == e.Id).Sum(h => h.Amount);
        long healed = trace.Heals.Where(h => h.T <= t && h.TargetId == e.Id).Sum(h => h.Amount);
        if (dealt > 0 || taken > 0 || healed > 0)
        {
            lines.Add(($"So far: dealt {K(dealt)} · taken {K(taken)}" + (healed > 0 ? $" · healed {K(healed)}" : string.Empty), ink));
        }

        var buffs = trace.BuffsAt(e.Id, t).Select(b => FightTrace.BuffName(b, Data)).Where(Named).Distinct().Take(5).ToList();
        if (buffs.Count > 0)
        {
            lines.Add(("Effects: " + string.Join(", ", buffs), muted));
        }

        var title = text(e.Kind == TraceEntityKind.Self ? $"{e.Name} (you)" : e.Name, 13, ink, true);
        var formatted = lines.Select(x => text(x.Text, 11, x.Brush, false)).ToList();
        foreach (var f in formatted)
        {
            f.MaxTextWidth = 360;
        }

        double width = Math.Max(title.Width, formatted.Max(f => f.Width)) + 20;
        double height = title.Height + formatted.Sum(f => f.Height + 2) + 14;
        var box = new Rect(10, size.Height - height - 32, width, height);
        dc.DrawRoundedRectangle((Brush)FindResource("FloatingPanelBrush"), new Pen((Brush)FindResource("AccentBrush"), 1), box, 6, 6);
        double y = box.Y + 7;
        dc.DrawText(title, new Point(box.X + 10, y));
        y += title.Height + 2;
        foreach (var f in formatted)
        {
            dc.DrawText(f, new Point(box.X + 10, y));
            y += f.Height + 2;
        }
    }

    private static void DrawGrid(DrawingContext dc, Size size, double scale, Point center, Brush muted, Func<string, double, Brush, bool, FormattedText> text)
    {
        // Grid every 5/10/25 m (1 m = 100 units) anchored to the world, and a scale bar.
        double meters = scale * 100 >= 12 ? 5 : scale * 100 >= 5 ? 10 : 25;
        double step = meters * 100 * scale;
        double offsetX = (size.Width / 2 - center.X * scale) % step;
        double offsetY = (size.Height / 2 + center.Y * scale) % step;
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(16, 255, 255, 255)), 1);
        for (double x = (offsetX + step) % step; x < size.Width; x += step)
        {
            dc.DrawLine(pen, new Point(x, 0), new Point(x, size.Height));
        }

        for (double y = (offsetY + step) % step; y < size.Height; y += step)
        {
            dc.DrawLine(pen, new Point(0, y), new Point(size.Width, y));
        }

        var bar = new Pen(muted, 2);
        dc.DrawLine(bar, new Point(12, size.Height - 14), new Point(12 + step, size.Height - 14));
        dc.DrawText(text($"{meters:0} m", 10, muted, false), new Point(16 + step, size.Height - 22));
    }

    /// <summary>A small triangle just outside a ring, pointing where the unit faces (world degrees, y up).</summary>
    private static void DrawArrow(DrawingContext dc, Point at, double r, float degrees, Color color)
    {
        double a = degrees * Math.PI / 180;
        var dir = new Vector(Math.Cos(a), -Math.Sin(a));
        var side = new Vector(-dir.Y, dir.X);
        var tip = at + dir * (r + 8);
        var baseCenter = at + dir * (r + 2);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(tip, true, true);
            g.LineTo(baseCenter + side * 4.5, true, false);
            g.LineTo(baseCenter - side * 4.5, true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(new SolidColorBrush(color), null, geometry);
    }

    /// <summary>A short star of rays for a critical hit, growing and fading.</summary>
    private static void DrawBurst(DrawingContext dc, Point at, double progress)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(230 * (1 - progress)), 0xFF, 0xD1, 0x5C)), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        double inner = 6 + progress * 8, outer = 12 + progress * 14;
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4 + 0.3;
            var dir = new Vector(Math.Cos(a), Math.Sin(a));
            dc.DrawLine(pen, at + dir * inner, at + dir * outer);
        }
    }

    private static void DrawSkull(DrawingContext dc, Point at, double r, Color color)
    {
        var brush = new SolidColorBrush(color);
        dc.DrawEllipse(brush, null, new Point(at.X, at.Y - r * 0.15), r, r * 0.85);
        dc.DrawRectangle(brush, null, new Rect(at.X - r * 0.55, at.Y + r * 0.45, r * 1.1, r * 0.5));
        var eye = new SolidColorBrush(Color.FromRgb(0x0B, 0x13, 0x22));
        dc.DrawEllipse(eye, null, new Point(at.X - r * 0.38, at.Y - r * 0.1), r * 0.24, r * 0.28);
        dc.DrawEllipse(eye, null, new Point(at.X + r * 0.38, at.Y - r * 0.1), r * 0.24, r * 0.28);
    }

    private static void DrawBar(DrawingContext dc, Rect box, double ratio, Color color)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, 0x10, 0x16, 0x1E)), null, box);
        dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(box.X, box.Y, box.Width * Math.Clamp(ratio, 0, 1), box.Height));
    }

    private static void DrawArc(DrawingContext dc, Point center, double radius, double ratio, Pen pen)
    {
        if (ratio <= 0)
        {
            return;
        }

        if (ratio >= 0.999)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        double angle = ratio * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(start, false, false);
            g.ArcTo(end, new Size(radius, radius), 0, ratio > 0.5, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static Color ColorOf(TraceEntity e, Color accent) => e.Kind switch
    {
        TraceEntityKind.Boss => Color.FromRgb(0xE5, 0x48, 0x4D),
        TraceEntityKind.Monster => Color.FromRgb(0x9A, 0x9F, 0xAE),
        TraceEntityKind.Self => accent,
        _ => e.Class == GameClass.Unknown ? Color.FromRgb(0xC9, 0xD7, 0xE4) : OverlayViewModel.ClassColor(e.Class),
    };

    private static SolidColorBrush WithAlpha(Color color, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>A real name, not a placeholder ("Skill 123", "Effect 123", "?").</summary>
    private static bool Named(string name) => name.Trim('?', ' ').Length > 0 &&
        !name.StartsWith("Skill ", StringComparison.Ordinal) && !name.StartsWith("Effect ", StringComparison.Ordinal);

    private static string Compact(long value) => value >= 10_000
        ? (value / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K"
        : value.ToString("N0", CultureInfo.InvariantCulture);

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.25 : 1 / 1.25), 0.3, 12);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _pressedAt = new Point(double.NaN, double.NaN); // the release of a double-click is not a click
            Camera = ReplayCamera.Fit;
            ResetView();
            return;
        }

        _dragFrom = e.GetPosition(this);
        _pressedAt = _dragFrom.Value;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragFrom is not { } from || e.LeftButton != MouseButtonState.Pressed || _bounds.IsEmpty)
        {
            return;
        }

        var at = e.GetPosition(this);
        if ((at - _pressedAt).Length < 4)
        {
            return;
        }

        // Dragging takes the camera over: keep the current view, then pan
        if (Camera != ReplayCamera.Fit && _camera is { } cam)
        {
            Camera = ReplayCamera.Fit;
            _zoom = _zoomShown;
            _pan = new Vector(cam.X - (_bounds.X + _bounds.Width / 2), cam.Y - (_bounds.Y + _bounds.Height / 2));
        }

        double scale = Math.Min(RenderSize.Width, RenderSize.Height) / _bounds.Width * _zoomShown;
        var delta = at - from;
        _pan += new Vector(-delta.X / scale, delta.Y / scale);
        _camera = null; // jump, no smoothing while dragging
        _dragFrom = at;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragFrom = null;
        ReleaseMouseCapture();

        // A click (not a drag): show the info card of the unit under the cursor, or close it.
        var at = e.GetPosition(this);
        if ((at - _pressedAt).Length < 4)
        {
            var hit = _dots
                .Where(d => (d.P - at).Length <= d.Radius + 5)
                .OrderBy(d => (d.P - at).Length)
                .Select(d => (uint?)d.Id)
                .FirstOrDefault();
            _inspected = hit == _inspected ? null : hit;
            InvalidateVisual();
        }
    }
}
