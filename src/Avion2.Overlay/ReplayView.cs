using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Avion2.Analysis;
using Avion2.Protocol;

namespace Avion2.Overlay;

/// <summary>
/// Top-down replay of a fight trace at <see cref="Time"/>: entities at their (interpolated) positions with short
/// trails, attack lines and damage numbers, skill names over the caster, the boss with an HP ring. Our own
/// position is drawn dashed where it is estimated (the server only tells us where we are now and then).
/// Wheel zooms, drag pans, double-click resets the view.
/// </summary>
public sealed class ReplayView : FrameworkElement
{
    private const int TrailMs = 6_000;
    private const int HitFlashMs = 450;
    private const int NumberMs = 900;
    private const int CastLabelMs = 1_400;

    private double _zoom = 1;
    private Vector _pan;
    private Point? _dragFrom;
    private Point _pressedAt;

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

    public GameData Data { get; set; } = GameData.Empty;

    /// <summary>Shows the info card of an entity (null closes it).</summary>
    public void Inspect(uint? id)
    {
        _inspected = id;
        InvalidateVisual();
    }

    public void ResetView()
    {
        _zoom = 1;
        _pan = default;
        InvalidateVisual();
    }

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
        var background = (Brush)FindResource("GridBackgroundBrush");
        dc.DrawRoundedRectangle(background, new Pen((Brush)FindResource("GridBorderBrush"), 1), new Rect(size), 6, 6);
        var trace = Trace;
        var muted = (Brush)FindResource("MutedBrush");
        var textBrush = (Brush)FindResource("TextBrush");
        var typeface = new Typeface((FontFamily)FindResource("BodyFont"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Text(string s, double em, Brush brush) =>
            new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, em, brush, dpi);

        if (trace is null)
        {
            dc.DrawText(Text("No replay for this fight (recorded before replays existed).", 12, muted), new Point(12, 12));
            return;
        }

        var world = Bounds(trace);
        if (world.IsEmpty)
        {
            dc.DrawText(Text("No positions were seen during this fight.", 12, muted), new Point(12, 12));
            return;
        }

        // World → screen: square, centred, y up (north up), with zoom and pan.
        double scale = Math.Min(size.Width, size.Height) / world.Width * _zoom;
        var center = new Point(world.X + world.Width / 2, world.Y + world.Height / 2);
        Point S(double x, double y) => new(size.Width / 2 + (x - center.X) * scale + _pan.X, size.Height / 2 - (y - center.Y) * scale + _pan.Y);

        DrawGrid(dc, size, scale, muted, Text);

        int t = (int)Time;
        var positions = new Dictionary<uint, (Point P, bool Estimated)>();
        foreach (var e in trace.Entities)
        {
            // Dead monsters and summons are gone from the map (they come back if seen moving again).
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

        // Trails (players and bosses).
        foreach (var e in trace.Entities.Where(e => e.Kind is not (TraceEntityKind.Summon or TraceEntityKind.Monster)))
        {
            var trail = e.Track.Where(p => p.T <= t && p.T >= t - TrailMs).Select(p => S(p.X, p.Y)).ToList();
            if (positions.TryGetValue(e.Id, out var now))
            {
                trail.Add(now.P);
            }

            for (int i = 1; i < trail.Count; i++)
            {
                var pen = new Pen(WithAlpha(ColorOf(e), (byte)(40 + 140 * i / trail.Count)), 2);
                dc.DrawLine(pen, trail[i - 1], trail[i]);
            }
        }

        // Attack lines: actor (or owner) → target for recent hits.
        foreach (var hit in trace.Hits.Where(h => h.T <= t && h.T > t - HitFlashMs))
        {
            uint from = positions.ContainsKey(hit.ActorId) ? hit.ActorId : trace.OwnerOf(hit.ActorId);
            if (!positions.TryGetValue(from, out var a) || !positions.TryGetValue(hit.TargetId, out var b))
            {
                continue;
            }

            var actor = trace.Entity(trace.OwnerOf(hit.ActorId));
            byte alpha = (byte)(220 * (1 - (t - hit.T) / (double)HitFlashMs));
            dc.DrawLine(new Pen(WithAlpha(actor is null ? Colors.White : ColorOf(actor), alpha), hit.Critical ? 2.5 : 1.2), a.P, b.P);
        }

        // Entities.
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

            var color = ColorOf(e);
            double radius = e.Kind switch
            {
                TraceEntityKind.Boss => 11,
                TraceEntityKind.Monster => 5,
                TraceEntityKind.Summon => 3.5,
                _ => 7,
            };
            bool player = e.Kind is TraceEntityKind.Self or TraceEntityKind.Player;
            // No recent fix: players get a dashed ring (an estimate still helps), others are just drawn fainter.
            var fill = new SolidColorBrush(WithAlpha(color, at.Estimated ? (byte)(player ? 90 : 110) : (byte)235).Color);
            var outline = new Pen(e.Id == SelectedActor || e.Kind == TraceEntityKind.Self ? Brushes.White : new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), e.Kind == TraceEntityKind.Self ? 2 : 1);
            if (at.Estimated && player)
            {
                outline = new Pen(Brushes.White, 1.5) { DashStyle = DashStyles.Dash };
            }

            dc.DrawEllipse(fill, outline, at.P, radius, radius);
            if (e.Id == _inspected)
            {
                dc.DrawEllipse(null, new Pen((Brush)FindResource("AccentBrush"), 2), at.P, radius + 4, radius + 4);
            }

            _dots.Add((e.Id, at.P, radius));

            if (e.Kind == TraceEntityKind.Boss && e.MaxHp > 0 && trace.HpAt(e.Id, t) is { } hp)
            {
                double ratio = Math.Clamp(hp / (double)e.MaxHp, 0, 1);
                DrawArc(dc, at.P, radius + 5, ratio, new Pen(new SolidColorBrush(Color.FromRgb(0xF0, 0x58, 0x4F)), 3));
            }

            // Names for players and the main target; other monsters stay anonymous dots (hover-free clarity).
            if (e.Kind is TraceEntityKind.Self or TraceEntityKind.Player || e.Id == trace.PrimaryTargetId)
            {
                string name = e.Kind == TraceEntityKind.Self ? $"{e.Name} (you)" : e.Name;
                var label = Text(name, e.Kind == TraceEntityKind.Boss ? 12 : 11, e.Kind == TraceEntityKind.Boss ? Brushes.White : textBrush);
                dc.DrawText(label, new Point(at.P.X - label.Width / 2, at.P.Y + radius + 3));
            }
        }

        // Skill names over the caster; in crowded fights (raids) only for us, the selected player and bosses.
        bool crowded = trace.Entities.Count(e => e.Kind is TraceEntityKind.Player or TraceEntityKind.Self) > 8;
        var shown = new HashSet<uint>();
        foreach (var cast in trace.Casts.Where(c => c.T <= t && c.T > t - CastLabelMs).OrderByDescending(c => c.T))
        {
            uint owner = trace.OwnerOf(cast.ActorId);
            var entity = trace.Entity(owner);
            if (entity is null || entity.Kind is TraceEntityKind.Monster || !positions.TryGetValue(owner, out var at) ||
                (crowded && entity.Kind == TraceEntityKind.Player && owner != SelectedActor) || !shown.Add(owner))
            {
                continue;
            }

            string skill = Data.SkillName(cast.SkillId);
            if (skill.StartsWith("Skill ", StringComparison.Ordinal))
            {
                continue; // unnamed (most monster skills)
            }

            byte alpha = (byte)(255 * (1 - (t - cast.T) / (double)CastLabelMs * 0.6));
            var brush = new SolidColorBrush(entity.Kind == TraceEntityKind.Boss ? Color.FromArgb(alpha, 0xFF, 0x8A, 0x80) : Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));
            var label = Text(skill, entity.Kind == TraceEntityKind.Boss ? 13 : 11, brush);
            var box = new Rect(at.P.X - label.Width / 2 - 4, at.P.Y - 32, label.Width + 8, label.Height + 2);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb((byte)(alpha * 0.6), 0, 0, 0)), null, box, 4, 4);
            dc.DrawText(label, new Point(box.X + 4, box.Y + 1));
        }

        // Damage numbers rising from the target.
        int shownNumbers = 0;
        uint focus = SelectedActor ?? trace.SelfId ?? 0;
        foreach (var hit in trace.Hits.Where(h => h.T <= t && h.T > t - NumberMs && h.Amount > 0 && trace.OwnerOf(h.ActorId) == focus)
                     .OrderByDescending(h => h.T))
        {
            if (shownNumbers++ > 24 || !positions.TryGetValue(hit.TargetId, out var at))
            {
                continue;
            }

            double age = (t - hit.T) / (double)NumberMs;
            bool mine = trace.OwnerOf(hit.ActorId) == trace.SelfId;
            var color = hit.Critical ? Color.FromRgb(0xFF, 0xD1, 0x5C) : mine ? Colors.White : Color.FromRgb(0xB8, 0xC0, 0xD0);
            var label = Text(Compact(hit.Amount), hit.Critical ? 13 : 11, new SolidColorBrush(WithAlpha(color, (byte)(255 * (1 - age))).Color));
            double jitter = (hit.T * 37 % 40) - 20;
            dc.DrawText(label, new Point(at.P.X + jitter - label.Width / 2, at.P.Y - 18 - age * 34));
        }

        // Server freeze banner.
        if (trace.LagAt(t) is { } lag)
        {
            var banner = Text($"⚠ Server froze here for {lag.Length / 1000.0:0.0} s: positions jump when it catches up", 12,
                new SolidColorBrush(Color.FromRgb(0xFC, 0xD3, 0x4D)));
            var box = new Rect(10, 8, banner.Width + 16, banner.Height + 8);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xCC, 0x3A, 0x2A, 0x05)), new Pen(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)), 1), box, 4, 4);
            dc.DrawText(banner, new Point(box.X + 8, box.Y + 4));
        }

        if (_inspected is { } inspected && trace.Entity(inspected) is { } picked)
        {
            DrawInfoCard(dc, trace, picked, t, size, Text, muted, textBrush);
        }

        // Clock.
        var clock = Text($"{TimeSpan.FromMilliseconds(Math.Max(0, t)):m\\:ss\\.f} / {TimeSpan.FromMilliseconds(trace.DurationMs):m\\:ss}", 12, muted);
        dc.DrawText(clock, new Point(size.Width - clock.Width - 10, 8));
    }

    /// <summary>Details of the clicked entity at the current time, in the lower left corner.</summary>
    private void DrawInfoCard(DrawingContext dc, FightTrace trace, TraceEntity e, int t, Size size,
        Func<string, double, Brush, FormattedText> text, Brush muted, Brush ink)
    {
        static string K(long v) => v >= 10_000 ? (v / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K" : v.ToString("N0", CultureInfo.InvariantCulture);
        string legion = e.Legion is { Length: > 0 } l ? $" ‹{l}›" : string.Empty;
        string kind = e.Kind switch
        {
            TraceEntityKind.Self => $"You · {e.Class}{legion}",
            TraceEntityKind.Player => (e.Class == GameClass.Unknown ? "Player" : $"Player · {e.Class}") + legion,
            TraceEntityKind.Boss => "Boss",
            TraceEntityKind.Summon => $"Summon of {(e.OwnerId is { } o ? trace.Entity(o)?.Name ?? $"#{o}" : "?")}",
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

        if (trace.LastCast(e.Id, t) is { } cast)
        {
            lines.Add(($"Last skill: {Data.SkillName(cast.SkillId)} ({(t - cast.T) / 1000.0:0.0} s ago)", ink));
        }

        bool counts(uint actor) => actor == e.Id || (e.Kind is TraceEntityKind.Self or TraceEntityKind.Player && trace.OwnerOf(actor) == e.Id);
        long dealt = trace.Hits.Where(h => h.T <= t && counts(h.ActorId)).Sum(h => h.Amount);
        long taken = trace.Hits.Where(h => h.T <= t && h.TargetId == e.Id).Sum(h => h.Amount);
        if (dealt > 0 || taken > 0)
        {
            lines.Add(($"So far: dealt {K(dealt)} · taken {K(taken)}", ink));
        }

        var title = text(e.Kind == TraceEntityKind.Self ? $"{e.Name} (you)" : e.Name, 13, ink);
        var formatted = lines.Select(x => text(x.Text, 11, x.Brush)).ToList();
        double width = Math.Max(title.Width, formatted.Max(f => f.Width)) + 20;
        double height = title.Height + formatted.Sum(f => f.Height + 2) + 14;
        var box = new Rect(10, size.Height - height - 32, width, height);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xE6, 0x10, 0x12, 0x24)), new Pen((Brush)FindResource("AccentBrush"), 1), box, 6, 6);
        double y = box.Y + 7;
        dc.DrawText(title, new Point(box.X + 10, y));
        y += title.Height + 2;
        foreach (var f in formatted)
        {
            dc.DrawText(f, new Point(box.X + 10, y));
            y += f.Height + 2;
        }
    }

    private static void DrawGrid(DrawingContext dc, Size size, double scale, Brush muted, Func<string, double, Brush, FormattedText> text)
    {
        // Grid every 5/10/25 m (1 m = 100 units), and a scale bar.
        double meters = scale * 100 >= 12 ? 5 : scale * 100 >= 5 ? 10 : 25;
        double step = meters * 100 * scale;
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), 1);
        for (double x = size.Width / 2 % step; x < size.Width; x += step)
        {
            dc.DrawLine(pen, new Point(x, 0), new Point(x, size.Height));
        }

        for (double y = size.Height / 2 % step; y < size.Height; y += step)
        {
            dc.DrawLine(pen, new Point(0, y), new Point(size.Width, y));
        }

        var bar = new Pen(muted, 2);
        dc.DrawLine(bar, new Point(12, size.Height - 14), new Point(12 + step, size.Height - 14));
        dc.DrawText(text($"{meters:0} m", 10, muted), new Point(16 + step, size.Height - 22));
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

    private static Color ColorOf(TraceEntity e) => e.Kind switch
    {
        TraceEntityKind.Boss => Color.FromRgb(0xE5, 0x48, 0x4D),
        TraceEntityKind.Monster => Color.FromRgb(0x9A, 0x9F, 0xAE),
        _ => e.Class == GameClass.Unknown ? Color.FromRgb(0xC9, 0xD7, 0xE4) : OverlayViewModel.ClassColor(e.Class),
    };

    private static SolidColorBrush WithAlpha(Color color, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static string Compact(long value) => value >= 10_000
        ? (value / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K"
        : value.ToString("N0", CultureInfo.InvariantCulture);

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var at = e.GetPosition(this);
        double factor = e.Delta > 0 ? 1.2 : 1 / 1.2;
        double newZoom = Math.Clamp(_zoom * factor, 0.3, 12);
        factor = newZoom / _zoom;
        // Zoom around the cursor.
        var fromCenter = at - new Point(RenderSize.Width / 2, RenderSize.Height / 2);
        _pan = fromCenter - (fromCenter - _pan) * factor;
        _zoom = newZoom;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _pressedAt = new Point(double.NaN, double.NaN); // the release of a double-click is not a click
            ResetView();
            return;
        }

        _dragFrom = e.GetPosition(this);
        _pressedAt = _dragFrom.Value;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragFrom is { } from && e.LeftButton == MouseButtonState.Pressed)
        {
            var at = e.GetPosition(this);
            _pan += at - from;
            _dragFrom = at;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragFrom = null;
        ReleaseMouseCapture();

        // A click (not a drag): show the info card of the dot under the cursor, or close it.
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
