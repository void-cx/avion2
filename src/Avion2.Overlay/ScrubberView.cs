using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Avion2.Analysis;

namespace Avion2.Overlay;

/// <summary>
/// The replay's scrubber: key moments on top (boss skills, deaths, big hits, freezes; hover for what, click to jump
/// just before), one lane of cast marks per player (you first), the main target's HP behind them, server freezes as
/// amber bands and the playhead. Click or drag to jump; casts after the playhead are faint.
/// </summary>
public sealed class ScrubberView : FrameworkElement
{
    private const double LabelWidth = 96;
    private const double AxisHeight = 16;
    private const double MomentHeight = 16;
    private readonly System.Windows.Controls.ToolTip _tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Relative };
    private const int MaxLanes = 5;
    private List<TraceEntity> _lanes = new();

    /// <summary>Cast times per lane (owner resolved once, not per frame).</summary>
    private List<int[]> _casts = new();

    public ScrubberView()
    {
        ClipToBounds = true;
        Cursor = Cursors.Hand;
        ToolTip = _tip;
        System.Windows.Controls.ToolTipService.SetInitialShowDelay(this, 0);
    }

    public static readonly DependencyProperty MomentsProperty = DependencyProperty.Register(
        nameof(Moments), typeof(IReadOnlyList<TraceMoment>), typeof(ScrubberView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Key moments shown above the lanes.</summary>
    public IReadOnlyList<TraceMoment>? Moments
    {
        get => (IReadOnlyList<TraceMoment>?)GetValue(MomentsProperty);
        set => SetValue(MomentsProperty, value);
    }

    public static Color MomentColor(MomentKind kind) => kind switch
    {
        MomentKind.BossSkill => Color.FromRgb(0xE5, 0x48, 0x4D),
        MomentKind.Death => Color.FromRgb(0xF2, 0xF4, 0xF8),
        MomentKind.BigHit => Color.FromRgb(0xF5, 0x9E, 0x0B),
        _ => Color.FromRgb(0xFC, 0xD3, 0x4D),
    };

    private TraceMoment? MomentAt(Point at)
    {
        if (Moments is not { Count: > 0 } moments || at.Y > AxisHeight + MomentHeight + 2)
        {
            return null;
        }

        var near = moments.MinBy(m => Math.Abs(X(m.T) - at.X));
        return near is not null && Math.Abs(X(near.T) - at.X) <= 6 ? near : null;
    }

    public static readonly DependencyProperty TraceProperty = DependencyProperty.Register(
        nameof(Trace), typeof(FightTrace), typeof(ScrubberView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((ScrubberView)d).Prepare()));

    public FightTrace? Trace
    {
        get => (FightTrace?)GetValue(TraceProperty);
        set => SetValue(TraceProperty, value);
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ScrubberView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>The playhead, ms since the trace start.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(ScrubberView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public static readonly DependencyProperty SelectedActorProperty = DependencyProperty.Register(
        nameof(SelectedActor), typeof(uint?), typeof(ScrubberView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public uint? SelectedActor
    {
        get => (uint?)GetValue(SelectedActorProperty);
        set => SetValue(SelectedActorProperty, value);
    }

    /// <summary>Raised when the user moves the playhead (click or drag).</summary>
    public event Action? Scrubbed;

    private void Prepare()
    {
        var trace = Trace;
        _lanes = Lanes(trace);
        if (trace is null)
        {
            _casts = new();
            return;
        }

        var owners = trace.Entities.ToDictionary(e => e.Id, e => e.OwnerId ?? e.Id);
        var byOwner = trace.Casts.GroupBy(c => owners.GetValueOrDefault(c.ActorId, c.ActorId)).ToDictionary(g => g.Key, g => g.Select(c => c.T).ToArray());
        _casts = _lanes.Select(l => byOwner.GetValueOrDefault(l.Id) ?? []).ToList();
    }

    /// <summary>You first, then the players with the most damage; summons count for their owner.</summary>
    private static List<TraceEntity> Lanes(FightTrace? trace)
    {
        if (trace is null)
        {
            return new();
        }

        var damage = trace.Hits.GroupBy(h => trace.OwnerOf(h.ActorId)).ToDictionary(g => g.Key, g => g.Sum(h => h.Amount));
        return trace.Entities
            .Where(e => e.Kind is TraceEntityKind.Self or TraceEntityKind.Player)
            .Where(e => damage.ContainsKey(e.Id) || e.Kind == TraceEntityKind.Self)
            .OrderBy(e => e.Kind == TraceEntityKind.Self ? 0 : 1)
            .ThenByDescending(e => damage.GetValueOrDefault(e.Id))
            .Take(MaxLanes)
            .ToList();
    }

    /// <summary>The lane colour of a player: the accent for you, the class colour for others.</summary>
    public static Color LaneColor(TraceEntity e, Color accent) => e.Kind == TraceEntityKind.Self ? accent
        : e.Class == Avion2.Protocol.GameClass.Unknown ? Color.FromRgb(0x7F, 0xD8, 0xFF) : OverlayViewModel.ClassColor(e.Class);

    private double PlotWidth => Math.Max(40, RenderSize.Width - LabelWidth - 8);

    private double X(double t) => LabelWidth + Math.Clamp(t / Math.Max(Maximum, 1), 0, 1) * PlotWidth;

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(size));
        var trace = Trace;
        var muted = (Brush)FindResource("SectionLabelBrush");
        var line = (Brush)FindResource("LineBrush");
        var accent = ((SolidColorBrush)FindResource("AccentBrush")).Color;
        var typeface = new Typeface((FontFamily)FindResource("BodyFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Text(string s, double em, Brush brush) => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, em, brush, dpi);

        if (trace is null || Maximum <= 0)
        {
            dc.DrawText(Text("No replay for this fight.", 12, muted), new Point(LabelWidth, AxisHeight + 6));
            return;
        }

        // Key moments
        if (Moments is { Count: > 0 } moments)
        {
            double my = AxisHeight + MomentHeight / 2 + 1;
            foreach (var moment in moments)
            {
                double x = X(moment.T);
                var brush = new SolidColorBrush(MomentColor(moment.Kind));
                switch (moment.Kind)
                {
                    case MomentKind.BossSkill:
                        dc.DrawGeometry(brush, null, Diamond(new Point(x, my), 4.5));
                        break;
                    case MomentKind.Death:
                        dc.DrawEllipse(brush, null, new Point(x, my - 1), 4, 3.6);
                        dc.DrawRectangle(brush, null, new Rect(x - 2.4, my + 1.6, 4.8, 2.6));
                        break;
                    case MomentKind.BigHit:
                        dc.DrawGeometry(brush, null, Triangle(new Point(x, my), 5));
                        break;
                    default:
                        dc.DrawRectangle(brush, null, new Rect(x - 1, my - 5, 2, 10));
                        break;
                }
            }
        }

        double laneTop = AxisHeight + MomentHeight + 6;
        int lanes = Math.Max(1, _lanes.Count);
        double laneHeight = Math.Clamp((size.Height - laneTop - 4) / lanes, 14, 30);
        double plotBottom = laneTop + lanes * laneHeight;

        // Axis: 5 labels across
        for (int i = 0; i <= 4; i++)
        {
            double t = Maximum * i / 4;
            var label = Text(TimeSpan.FromMilliseconds(t).ToString(@"m\:ss", CultureInfo.InvariantCulture), 10, muted);
            double x = X(t) - (i == 4 ? label.Width : i == 0 ? 0 : label.Width / 2);
            dc.DrawText(label, new Point(x, 0));
        }

        // Main target HP behind the lanes
        if (trace.PrimaryTargetId is { } target && trace.Entity(target) is { MaxHp: > 0 } boss)
        {
            var samples = trace.Hp.Where(h => h.EntityId == target).ToList();
            if (samples.Count > 1)
            {
                var area = new StreamGeometry();
                var curve = new StreamGeometry();
                using (var a = area.Open())
                using (var c = curve.Open())
                {
                    a.BeginFigure(new Point(X(samples[0].T), plotBottom), true, true);
                    for (int i = 0; i < samples.Count; i++)
                    {
                        var p = new Point(X(samples[i].T), plotBottom - Math.Clamp(samples[i].Hp / (double)boss.MaxHp, 0, 1) * (plotBottom - laneTop));
                        a.LineTo(p, true, false);
                        if (i == 0)
                        {
                            c.BeginFigure(p, false, false);
                        }
                        else
                        {
                            c.LineTo(p, true, false);
                        }
                    }

                    a.LineTo(new Point(X(samples[^1].T), plotBottom), true, false);
                }

                area.Freeze();
                curve.Freeze();
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x16, 0xE5, 0x48, 0x4D)), null, area);
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xE5, 0x48, 0x4D)), 1.2), curve);
            }
        }

        // Server freezes
        var lagBrush = new SolidColorBrush(Color.FromArgb(0x38, 0xF5, 0x9E, 0x0B));
        foreach (var lag in trace.Lags)
        {
            double x0 = X(lag.Start);
            dc.DrawRectangle(lagBrush, null, new Rect(x0, laneTop, Math.Max(X(lag.Start + lag.Length) - x0, 2), plotBottom - laneTop));
        }

        // Lanes with cast marks
        var laneBack = new SolidColorBrush(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF));
        double now = Value;
        for (int lane = 0; lane < _lanes.Count; lane++)
        {
            var e = _lanes[lane];
            double top = laneTop + lane * laneHeight;
            dc.DrawRoundedRectangle(laneBack, null, new Rect(LabelWidth, top + 2, PlotWidth, laneHeight - 4), 3, 3);
            var color = LaneColor(e, accent);
            var name = Text(e.Kind == TraceEntityKind.Self ? "You" : e.Name, 12,
                new SolidColorBrush(e.Id == SelectedActor || e.Kind == TraceEntityKind.Self ? color : Color.FromArgb(0xD0, color.R, color.G, color.B)));
            name.MaxTextWidth = LabelWidth - 10;
            name.MaxLineCount = 1;
            name.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(name, new Point(0, top + (laneHeight - name.Height) / 2));

            var past = new SolidColorBrush(color);
            var future = new SolidColorBrush(Color.FromArgb(0x55, color.R, color.G, color.B));
            past.Freeze();
            future.Freeze();
            double markHeight = laneHeight - 10;
            foreach (int t in _casts[lane])
            {
                dc.DrawRectangle(t <= now ? past : future, null, new Rect(X(t) - 1, top + 5, 2.4, markHeight));
            }
        }

        // Playhead
        double head = X(now);
        dc.DrawRectangle((Brush)FindResource("TitleBrush"), null, new Rect(head - 1, AxisHeight, 2, plotBottom - AxisHeight + 2));
        dc.DrawEllipse((Brush)FindResource("TitleBrush"), null, new Point(head, AxisHeight + 1), 5, 5);
        dc.DrawLine(new Pen(line, 1), new Point(LabelWidth, plotBottom + 1), new Point(LabelWidth + PlotWidth, plotBottom + 1));
    }

    private static Geometry Diamond(Point c, double r)
    {
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(new Point(c.X, c.Y - r), true, true);
            s.LineTo(new Point(c.X + r, c.Y), true, false);
            s.LineTo(new Point(c.X, c.Y + r), true, false);
            s.LineTo(new Point(c.X - r, c.Y), true, false);
        }

        g.Freeze();
        return g;
    }

    private static Geometry Triangle(Point c, double r)
    {
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(new Point(c.X, c.Y + r), true, true);
            s.LineTo(new Point(c.X + r, c.Y - r * 0.7), true, false);
            s.LineTo(new Point(c.X - r, c.Y - r * 0.7), true, false);
        }

        g.Freeze();
        return g;
    }

    private void Seek(Point at)
    {
        // On a key moment: jump a second and a half before it, to see it coming
        if (MomentAt(at) is { } moment)
        {
            Value = Math.Clamp(moment.T - 1_500, 0, Maximum);
            Scrubbed?.Invoke();
            return;
        }

        double t = (at.X - LabelWidth) / PlotWidth * Maximum;
        Value = Math.Clamp(t, 0, Maximum);
        Scrubbed?.Invoke();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        Seek(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var at = e.GetPosition(this);
        if (IsMouseCaptured)
        {
            if (MomentAt(at) is null)
            {
                Seek(at);
            }

            return;
        }

        if (MomentAt(at) is { } moment)
        {
            _tip.Content = $"{TimeSpan.FromMilliseconds(Math.Max(0, moment.T)):m\\:ss} · {moment.Text}";
            _tip.PlacementTarget = this;
            _tip.HorizontalOffset = at.X + 12;
            _tip.VerticalOffset = at.Y + 12;
            _tip.IsOpen = true;
        }
        else
        {
            _tip.IsOpen = false;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e) => _tip.IsOpen = false;

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();
}
