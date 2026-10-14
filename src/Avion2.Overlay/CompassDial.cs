using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Avion2.Overlay;

/// <summary>
/// "Precision finding" dial: an ornamented ring with a glowing arc toward the target, the distance in the middle,
/// and a disc that fills as you get close (a check mark when you are there). Colours come from the theme.
/// </summary>
public sealed class CompassDial : FrameworkElement
{
    /// <summary>Within this distance the disc starts to grow.</summary>
    public const double NearMeters = 30;

    /// <summary>Within this distance you are "there".</summary>
    public const double HereMeters = 8;

    private double _shownAngle;
    private double _shownFill;
    private bool _animating;

    /// <summary>Direction to go in degrees, clockwise; 0 = up (ahead, or map north when <see cref="NorthUp"/>).</summary>
    public double Angle { get; private set; }

    /// <summary>Metres to the target; null = unknown (no arc, a dash in the middle).</summary>
    public double? Meters { get; private set; }

    /// <summary>The arc is relative to map north (no recent facing): an "N" marks the top.</summary>
    public bool NorthUp { get; private set; }

    /// <summary>The target is a cube the game showed us (exact position): green instead of the theme accent.</summary>
    public bool Exact { get; private set; }

    /// <summary>Green for an exact target.</summary>
    public static readonly Color ExactColor = Color.FromRgb(0x5B, 0xD4, 0x8A);

    /// <summary>Inside an approximate target's area: no arrow or countdown, a search ring instead.</summary>
    public bool Searching { get; private set; }

    public void Update(double angle, double? meters, bool northUp, bool exact = false, bool searching = false)
    {
        Angle = angle;
        Meters = meters;
        NorthUp = northUp;
        Exact = exact;
        Searching = searching && !exact;
        if (!_animating)
        {
            _animating = true;
            CompositionTarget.Rendering += Animate;
        }
    }

    /// <summary>Skips the easing (render shots).</summary>
    internal void Jump()
    {
        _shownAngle = Angle;
        _shownFill = TargetFill;
        InvalidateVisual();
    }

    // Only an exact target fills the disc as you close in; an approximate one cannot know how close you are
    private double TargetFill => !Exact || Meters is not { } m ? 0 : m <= HereMeters ? 1 : m >= NearMeters ? 0 : 1 - (m - HereMeters) / (NearMeters - HereMeters);

    /// <summary>Eases the arc toward its angle (the short way round) and the disc toward its size.</summary>
    private void Animate(object? sender, EventArgs e)
    {
        double delta = ((Angle - _shownAngle) % 360 + 540) % 360 - 180;
        double fillDelta = TargetFill - _shownFill;
        _shownAngle += delta * 0.18;
        _shownFill += fillDelta * 0.15;
        if (Math.Abs(delta) < 0.2 && Math.Abs(fillDelta) < 0.002)
        {
            _shownAngle = Angle;
            _shownFill = TargetFill;
            CompositionTarget.Rendering -= Animate;
            _animating = false;
        }

        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(180, 180);

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double ring = size / 2 - 10;

        var accent = Exact ? new SolidColorBrush(ExactColor) : Theme("HighlightBrush", Colors.Goldenrod);
        var title = Theme("TitleBrush", Colors.White);
        var muted = Theme("MutedBrush", Colors.Gray);
        var hairline = Theme("HairlineBrush", Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        var accentColor = (accent as SolidColorBrush)?.Color ?? Colors.Goldenrod;
        // Only an exact target can say "you are there"; a hub marker can be 70 m off
        bool here = Exact && Meters is { } m0 && m0 <= HereMeters;

        // Halo and ring
        var halo = new RadialGradientBrush(Color.FromArgb(0x30, accentColor.R, accentColor.G, accentColor.B), Colors.Transparent)
        {
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        halo.Freeze();
        dc.DrawEllipse(halo, null, c, ring + 10, ring + 10);
        dc.DrawEllipse(null, new Pen(hairline, 1.5), c, ring, ring);
        dc.DrawEllipse(null, new Pen(hairline, 0.8), c, ring - 9, ring - 9);

        // Ticks every 15°, longer every 45°, diamonds at the quarters
        for (int deg = 0; deg < 360; deg += 15)
        {
            double a = deg * Math.PI / 180;
            double inner = deg % 45 == 0 ? ring - 7 : ring - 4;
            var p1 = new Point(c.X + Math.Sin(a) * inner, c.Y - Math.Cos(a) * inner);
            var p2 = new Point(c.X + Math.Sin(a) * ring, c.Y - Math.Cos(a) * ring);
            dc.DrawLine(new Pen(deg % 45 == 0 ? muted : hairline, 1), p1, p2);
            if (deg % 90 == 0 && !(NorthUp && deg == 0))
            {
                var at = new Point(c.X + Math.Sin(a) * (ring + 0.5), c.Y - Math.Cos(a) * (ring + 0.5));
                dc.DrawGeometry(muted, null, Diamond(at, 3.2));
            }
        }

        if (NorthUp)
        {
            var n = Text("N", 10, title, FontWeights.SemiBold);
            dc.DrawText(n, new Point(c.X - n.Width / 2, c.Y - ring - n.Height / 2));
        }

        // Proximity disc
        if (_shownFill > 0.01)
        {
            double r = (ring - 14) * (0.35 + 0.65 * _shownFill);
            var disc = new SolidColorBrush(Color.FromArgb((byte)(0x50 + 0xAF * _shownFill), accentColor.R, accentColor.G, accentColor.B));
            disc.Freeze();
            dc.DrawEllipse(disc, null, c, r, r);
        }

        // Searching an approximate area: a dashed ring, no direction
        if (Searching)
        {
            var dashed = new Pen(accent, 2.5) { DashStyle = new DashStyle([2, 3], 0), DashCap = PenLineCap.Round };
            dc.DrawEllipse(null, dashed, c, ring - 22, ring - 22);
            var faint = new SolidColorBrush(Color.FromArgb(0x26, accentColor.R, accentColor.G, accentColor.B));
            faint.Freeze();
            dc.DrawEllipse(faint, null, c, ring - 22, ring - 22);
            var word = Text("Search", size * 0.13, title, FontWeights.SemiBold);
            dc.DrawText(word, new Point(c.X - word.Width / 2, c.Y - word.Height * 0.85));
            var hint = Text("this area", size * 0.075, muted, FontWeights.Normal);
            dc.DrawText(hint, new Point(c.X - hint.Width / 2, c.Y + word.Height * 0.15));
            return;
        }

        // Direction arc with a glow
        if (Meters is not null && !here)
        {
            double span = 34;
            DrawArc(dc, c, ring, _shownAngle - span, _shownAngle + span, new Pen(new SolidColorBrush(Color.FromArgb(0x50, accentColor.R, accentColor.G, accentColor.B)), 9)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            });
            DrawArc(dc, c, ring, _shownAngle - span, _shownAngle + span, new Pen(accent, 3.5)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            });

            // A small pointer just inside the ring
            double a = _shownAngle * Math.PI / 180;
            var tip = new Point(c.X + Math.Sin(a) * (ring - 12), c.Y - Math.Cos(a) * (ring - 12));
            dc.DrawGeometry(accent, null, Diamond(tip, 4));
        }

        // Centre: distance, or a check mark when there
        if (here)
        {
            var check = new StreamGeometry();
            using (var g = check.Open())
            {
                g.BeginFigure(new Point(c.X - size * 0.13, c.Y + size * 0.0), false, false);
                g.LineTo(new Point(c.X - size * 0.035, c.Y + size * 0.1), true, true);
                g.LineTo(new Point(c.X + size * 0.15, c.Y - size * 0.1), true, true);
            }

            check.Freeze();
            var background = Theme("NoticeBackground", Colors.Black);
            dc.DrawGeometry(null, new Pen(background, 9) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, check);
        }
        else
        {
            string approx = Exact ? string.Empty : "~";
            string text = Meters is not { } m ? "—" : m >= 1000 ? $"{approx}{m / 1000:0.0} km" : $"{approx}{m:0} m";
            var distance = Text(text, size * 0.17, _shownFill > 0.6 ? Theme("NoticeBackground", Colors.Black) : title, FontWeights.SemiBold);
            dc.DrawText(distance, new Point(c.X - distance.Width / 2, c.Y - distance.Height / 2));
        }
    }

    private static void DrawArc(DrawingContext dc, Point c, double r, double fromDeg, double toDeg, Pen pen)
    {
        Point At(double deg) => new(c.X + Math.Sin(deg * Math.PI / 180) * r, c.Y - Math.Cos(deg * Math.PI / 180) * r);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(At(fromDeg), false, false);
            ctx.ArcTo(At(toDeg), new Size(r, r), 0, toDeg - fromDeg > 180, SweepDirection.Clockwise, true, false);
        }

        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }

    private static Geometry Diamond(Point at, double r)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(at.X, at.Y - r), true, true);
            ctx.LineTo(new Point(at.X + r, at.Y), true, false);
            ctx.LineTo(new Point(at.X, at.Y + r), true, false);
            ctx.LineTo(new Point(at.X - r, at.Y), true, false);
        }

        g.Freeze();
        return g;
    }

    private Brush Theme(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private FormattedText Text(string text, double size, Brush brush, FontWeight weight)
    {
        var family = TryFindResource("DisplayFont") as FontFamily ?? new FontFamily("Segoe UI");
        return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }
}
