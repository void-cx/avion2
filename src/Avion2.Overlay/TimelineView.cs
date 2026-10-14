using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Avion2.Analysis;

namespace Avion2.Overlay;

/// <summary>
/// The fight as lanes: one per player (and the boss), a marker per skill cast, the primary target's HP behind
/// them. Hover a marker for the skill and time. Drawn directly (thousands of casts stay cheap).
/// </summary>
public sealed class TimelineView : FrameworkElement
{
    private const double LaneHeight = 26;
    private const double LabelWidth = 150;
    private const double AxisHeight = 22;

    private readonly ToolTip _tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Relative };
    private List<(Rect Box, string Text)> _hitBoxes = new();
    private List<TraceEntity> _lanes = new();

    public TimelineView()
    {
        ToolTip = _tip;
        ToolTipService.SetInitialShowDelay(this, 0);
        ClipToBounds = true;
    }

    public static readonly DependencyProperty TraceProperty = DependencyProperty.Register(
        nameof(Trace), typeof(FightTrace), typeof(TimelineView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public FightTrace? Trace
    {
        get => (FightTrace?)GetValue(TraceProperty);
        set => SetValue(TraceProperty, value);
    }

    public static readonly DependencyProperty SelectedActorProperty = DependencyProperty.Register(
        nameof(SelectedActor), typeof(uint?), typeof(TimelineView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Highlighted lane (the player picked in the details header).</summary>
    public uint? SelectedActor
    {
        get => (uint?)GetValue(SelectedActorProperty);
        set => SetValue(SelectedActorProperty, value);
    }

    /// <summary>Name tables for skill names.</summary>
    public GameData Data { get; set; } = GameData.Empty;

    protected override Size MeasureOverride(Size availableSize)
    {
        _lanes = Lanes(Trace);
        double width = double.IsInfinity(availableSize.Width) ? 900 : availableSize.Width;
        return new Size(width, AxisHeight + Math.Max(1, _lanes.Count) * LaneHeight + 8);
    }

    /// <summary>Us first, then players by casts, then bosses; summons fold into their owner's lane.</summary>
    private static List<TraceEntity> Lanes(FightTrace? trace)
    {
        if (trace is null)
        {
            return new();
        }

        var casts = trace.Casts.GroupBy(c => trace.OwnerOf(c.ActorId)).ToDictionary(g => g.Key, g => g.Count());
        return trace.Entities
            .Where(e => e.Kind is TraceEntityKind.Self or TraceEntityKind.Player or TraceEntityKind.Boss)
            .Where(e => casts.ContainsKey(e.Id) || e.Kind == TraceEntityKind.Self)
            .OrderBy(e => e.Kind switch { TraceEntityKind.Self => 0, TraceEntityKind.Player => 1, _ => 2 })
            .ThenByDescending(e => casts.GetValueOrDefault(e.Id))
            .Take(12)
            .ToList();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var trace = Trace;
        var text = (Brush)FindResource("TextBrush");
        var muted = (Brush)FindResource("MutedBrush");
        var line = (Brush)FindResource("HairlineBrush");
        var typeface = new Typeface((FontFamily)FindResource("BodyFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        _hitBoxes = new();

        if (trace is null || _lanes.Count == 0)
        {
            dc.DrawText(Text("No timeline for this fight (recorded before timelines existed, or no casts seen).", 12, muted), new Point(8, 8));
            return;
        }

        double plotLeft = LabelWidth;
        double plotWidth = Math.Max(50, RenderSize.Width - plotLeft - 10);
        double X(int t) => plotLeft + Math.Clamp(t / (double)Math.Max(trace.DurationMs, 1), 0, 1) * plotWidth;
        double plotBottom = AxisHeight + _lanes.Count * LaneHeight;

        // Axis: a tick every 5/10/30 s depending on length.
        int step = trace.DurationMs > 240_000 ? 30_000 : trace.DurationMs > 60_000 ? 10_000 : 5_000;
        var tickPen = new Pen(line, 1);
        for (int t = 0; t <= trace.DurationMs; t += step)
        {
            double x = X(t);
            dc.DrawLine(tickPen, new Point(x, AxisHeight - 4), new Point(x, plotBottom));
            dc.DrawText(Text(TimeSpan.FromMilliseconds(t).ToString(@"m\:ss", CultureInfo.InvariantCulture), 10, muted), new Point(x + 2, 2));
        }

        // Primary target HP as a filled area behind the lanes.
        if (trace.PrimaryTargetId is { } target && trace.Entity(target) is { MaxHp: > 0 } boss)
        {
            var samples = trace.Hp.Where(h => h.EntityId == target).ToList();
            if (samples.Count > 1)
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(new Point(X(samples[0].T), plotBottom), true, true);
                    foreach (var s in samples)
                    {
                        double y = plotBottom - Math.Clamp(s.Hp / (double)boss.MaxHp, 0, 1) * (plotBottom - AxisHeight);
                        g.LineTo(new Point(X(s.T), y), true, false);
                    }

                    g.LineTo(new Point(X(samples[^1].T), plotBottom), true, false);
                }

                geometry.Freeze();
                var fill = new SolidColorBrush(Color.FromArgb(0x22, 0xE5, 0x48, 0x4D));
                dc.DrawGeometry(fill, null, geometry);
                dc.DrawText(Text($"{boss.Name} HP", 10, muted), new Point(plotLeft + plotWidth - 120, AxisHeight + 2));
            }
        }

        // Server freezes: an amber band across all lanes.
        var lagBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xF5, 0x9E, 0x0B));
        var lagText = new SolidColorBrush(Color.FromRgb(0xFC, 0xD3, 0x4D));
        foreach (var lag in trace.Lags)
        {
            double x0 = X(lag.Start), x1 = Math.Max(X(lag.Start + lag.Length), x0 + 2);
            dc.DrawRectangle(lagBrush, null, new Rect(x0, AxisHeight, x1 - x0, plotBottom - AxisHeight));
            dc.DrawText(Text($"server froze {lag.Length / 1000.0:0.0} s", 10, lagText), new Point(x0 + 3, plotBottom - 14));
            _hitBoxes.Add((new Rect(x0, AxisHeight, x1 - x0, plotBottom - AxisHeight),
                $"Server froze for {lag.Length / 1000.0:0.0} s: no game data, then everything arrived at once"));
        }

        for (int lane = 0; lane < _lanes.Count; lane++)
        {
            var entity = _lanes[lane];
            double top = AxisHeight + lane * LaneHeight;
            if (entity.Id == SelectedActor)
            {
                dc.DrawRectangle((Brush)FindResource("HoverBrush"), null, new Rect(0, top, RenderSize.Width, LaneHeight));
            }

            dc.DrawLine(tickPen, new Point(0, top + LaneHeight), new Point(RenderSize.Width, top + LaneHeight));
            var nameBrush = entity.Kind == TraceEntityKind.Boss ? new SolidColorBrush(Color.FromRgb(0xF0, 0x58, 0x4F)) : text;
            var label = Text(entity.Kind == TraceEntityKind.Self ? $"{entity.Name} (you)" : entity.Name, 12, nameBrush);
            label.MaxTextWidth = LabelWidth - 12;
            label.Trimming = TextTrimming.CharacterEllipsis;
            label.MaxLineCount = 1;
            dc.DrawText(label, new Point(6, top + (LaneHeight - label.Height) / 2));

            foreach (var cast in trace.Casts)
            {
                if (trace.OwnerOf(cast.ActorId) != entity.Id)
                {
                    continue;
                }

                double x = X(cast.T);
                var brush = SkillBrush(cast.SkillId);
                var box = new Rect(x - 2, top + 5, 4, LaneHeight - 10);
                dc.DrawRoundedRectangle(brush, null, box, 1.5, 1.5);
                string skill = Data.SkillName(cast.SkillId);
                _hitBoxes.Add((new Rect(x - 4, top, 8, LaneHeight),
                    $"{TimeSpan.FromMilliseconds(Math.Max(cast.T, 0)):m\\:ss\\.f} · {skill}" +
                    (cast.ActorId != entity.Id ? $" ({trace.Entity(cast.ActorId)?.Name})" : string.Empty)));
            }
        }

        FormattedText Text(string s, double size, Brush brush) =>
            new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush, dpi);
    }

    /// <summary>A stable colour per skill (hue from the id), so repeats of one skill line up visually.</summary>
    public static Brush SkillBrush(uint skillId)
    {
        double hue = (skillId / 10_000 * 47 % 360);
        var brush = new SolidColorBrush(FromHsl(hue, 0.65, 0.62));
        brush.Freeze();
        return brush;
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var at = e.GetPosition(this);
        var hit = _hitBoxes.LastOrDefault(h => h.Box.Contains(at));
        if (hit.Text is null)
        {
            _tip.IsOpen = false;
            return;
        }

        _tip.Content = hit.Text;
        _tip.PlacementTarget = this;
        _tip.HorizontalOffset = at.X + 12;
        _tip.VerticalOffset = at.Y + 12;
        _tip.IsOpen = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e) => _tip.IsOpen = false;
}
