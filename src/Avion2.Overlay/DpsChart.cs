using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Avion2.Analysis;

namespace Avion2.Overlay;

/// <summary>
/// DPS over the fight per player (a rolling 5 s window), you in the accent colour with a soft fill, up to three
/// others in their class colours. Drawn from the fight's trace; header of the History detail pane.
/// </summary>
public sealed class DpsChart : FrameworkElement
{
    private const int WindowMs = 5_000;
    private const int MaxOthers = 3;

    public static readonly DependencyProperty TraceProperty = DependencyProperty.Register(
        nameof(Trace), typeof(FightTrace), typeof(DpsChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public FightTrace? Trace
    {
        get => (FightTrace?)GetValue(TraceProperty);
        set => SetValue(TraceProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(size));
        var muted = (Brush)FindResource("MutedBrush");
        var typeface = new Typeface((FontFamily)FindResource("BodyFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Text(string s, Brush brush) => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, brush, dpi);

        var trace = Trace;
        if (trace is null || trace.DurationMs <= 0 || trace.Hits.Count == 0)
        {
            var none = Text("No timeline recorded for this fight", muted);
            dc.DrawText(none, new Point((size.Width - none.Width) / 2, (size.Height - none.Height) / 2));
            return;
        }

        // Damage per player (summons count for their owner).
        var players = trace.Entities.Where(e => e.Kind is TraceEntityKind.Self or TraceEntityKind.Player).ToDictionary(e => e.Id);
        var byPlayer = trace.Hits
            .Select(h => (Owner: trace.OwnerOf(h.ActorId), h.T, h.Amount))
            .Where(h => players.ContainsKey(h.Owner))
            .GroupBy(h => h.Owner)
            .ToDictionary(g => g.Key, g => g.OrderBy(h => h.T).ToList());
        if (byPlayer.Count == 0)
        {
            return;
        }

        uint? self = trace.SelfId is { } s && byPlayer.ContainsKey(s) ? s : null;
        var others = byPlayer.Keys.Where(k => k != self).OrderByDescending(k => byPlayer[k].Sum(h => h.Amount)).Take(MaxOthers).ToList();

        int steps = Math.Clamp((int)(size.Width / 4), 20, 200);
        var series = new Dictionary<uint, double[]>();
        foreach (uint id in others.Concat(self is { } me ? new[] { me } : Array.Empty<uint>()))
        {
            var hits = byPlayer[id];
            var values = new double[steps + 1];
            for (int i = 0; i <= steps; i++)
            {
                int t = (int)(trace.DurationMs * (double)i / steps);
                int from = Math.Max(0, t - WindowMs);
                long sum = 0;
                foreach (var h in hits)
                {
                    if (h.T > t)
                    {
                        break;
                    }

                    if (h.T > from)
                    {
                        sum += h.Amount;
                    }
                }

                values[i] = sum / (Math.Max(t - from, 1000) / 1000.0);
            }

            series[id] = values;
        }

        double max = Math.Max(1, series.Values.SelectMany(v => v).Max());
        double top = 30, bottom = size.Height - 6;
        Point P(int i, double v) => new(size.Width * i / steps, bottom - v / max * (bottom - top));

        foreach (uint id in others)
        {
            var color = players[id].Class == Avion2.Protocol.GameClass.Unknown ? Color.FromRgb(0x7F, 0xD8, 0xFF) : OverlayViewModel.ClassColor(players[id].Class);
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0xB0, color.R, color.G, color.B)), 1.2), Line(series[id], false));
        }

        if (self is { } you)
        {
            var accent = (SolidColorBrush)FindResource("AccentBrush");
            var fill = new SolidColorBrush(Color.FromArgb(0x26, accent.Color.R, accent.Color.G, accent.Color.B));
            dc.DrawGeometry(fill, null, Line(series[you], true));
            dc.DrawGeometry(null, new Pen(accent, 1.8), Line(series[you], false));
        }

        // Legend
        double x = 16;
        foreach (uint id in (self is { } y ? new[] { y } : Array.Empty<uint>()).Concat(others))
        {
            bool mine = id == self;
            var brush = mine ? (Brush)FindResource("AccentBrush")
                : new SolidColorBrush(players[id].Class == Avion2.Protocol.GameClass.Unknown ? Color.FromRgb(0x7F, 0xD8, 0xFF) : OverlayViewModel.ClassColor(players[id].Class));
            dc.DrawRectangle(brush, null, new Rect(x, 15, 10, 2));
            var label = Text(mine ? "You" : players[id].Name, (Brush)FindResource("SecondaryTextBrush"));
            label.MaxTextWidth = 110;
            label.MaxLineCount = 1;
            label.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(label, new Point(x + 14, 9));
            x += 14 + label.Width + 14;
            if (x > size.Width - 120)
            {
                break;
            }
        }

        var caption = Text("DPS over time", muted);
        dc.DrawText(caption, new Point(size.Width - caption.Width - 16, 9));

        StreamGeometry Line(double[] values, bool closed)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(closed ? new Point(0, bottom) : P(0, values[0]), closed, closed);
                for (int i = closed ? 0 : 1; i <= steps; i++)
                {
                    g.LineTo(P(i, values[i]), true, true);
                }

                if (closed)
                {
                    g.LineTo(new Point(size.Width, bottom), true, false);
                }
            }

            geometry.Freeze();
            return geometry;
        }
    }
}
