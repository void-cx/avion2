namespace Avion2.Analysis;

/// <summary>
/// Finds HUD elements in one picture of the game (32-bit BGRA pixels). Currently your HP bar: the long, saturated
/// red horizontal bar in the lower part of the screen, near the middle (the HP numbers on it are bridged over).
/// Calibrate at full HP: a partly empty bar is found shorter.
/// </summary>
public static class HudDetector
{
    /// <param name="pixels">BGRA, <paramref name="stride"/> bytes per row.</param>
    public static HudBox? FindHpBar(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        const int maxGap = 40; // HP text drawn over the bar
        int minWidth = Math.Max(40, width / 20);
        int top = height * 55 / 100;

        // Longest red segment per row (gaps of up to maxGap bridged)
        var rows = new List<(int Y, int X0, int X1)>();
        for (int y = top; y < height; y++)
        {
            var row = pixels.Slice(y * stride, width * 4);
            int bestStart = -1, bestEnd = -1, start = -1, last = -1;
            for (int x = 0; x < width; x++)
            {
                byte b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                if (!IsHpRed(r, g, b))
                {
                    continue;
                }

                if (start < 0 || x - last > maxGap)
                {
                    start = x;
                }

                last = x;
                if (last - start > bestEnd - bestStart)
                {
                    (bestStart, bestEnd) = (start, last);
                }
            }

            if (bestStart >= 0 && bestEnd - bestStart + 1 >= minWidth)
            {
                rows.Add((y, bestStart, bestEnd));
            }
        }

        // Stack rows that overlap horizontally into bars; keep bar-shaped ones near the middle
        HudBox? best = null;
        double bestScore = double.MaxValue;
        int i = 0;
        while (i < rows.Count)
        {
            int j = i;
            int x0 = rows[i].X0, x1 = rows[i].X1;
            while (j + 1 < rows.Count && rows[j + 1].Y == rows[j].Y + 1 && rows[j + 1].X0 < x1 && rows[j + 1].X1 > x0)
            {
                j++;
                x0 = Math.Min(x0, rows[j].X0);
                x1 = Math.Max(x1, rows[j].X1);
            }

            int barHeight = rows[j].Y - rows[i].Y + 1;
            int barWidth = x1 - x0 + 1;
            double center = (x0 + x1) / 2.0 / width;
            if (barHeight >= 4 && barHeight <= height / 30 && barWidth >= barHeight * 6 && center > 0.25 && center < 0.75)
            {
                // Prefer wide bars close to the horizontal middle
                double score = Math.Abs(center - 0.5) * 4 - barWidth / (double)width;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = HudBox.FromPixels(x0, rows[i].Y, barWidth, barHeight, width, height);
                }
            }

            i = j + 1;
        }

        return best;
    }

    /// <summary>
    /// The boss frame at the top centre: its HP bar sits in an ornate frame whose gold rims draw several long,
    /// light-gold lines across the middle of the screen. The rims are found (not the fill, which shrinks with the
    /// boss's HP); the box covers the whole frame. Only present during a boss fight.
    /// </summary>
    public static HudBox? FindTargetBar(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        var rows = new List<(int Y, int X0, int X1)>();
        for (int y = height * 2 / 100; y < height * 15 / 100; y++)
        {
            var row = pixels.Slice(y * stride, width * 4);
            int bestStart = -1, bestEnd = -1, start = -1, last = -1;
            for (int x = width / 4; x < width * 3 / 4; x++)
            {
                byte b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                if (!(r > 140 && g > 130 && b < 175 && r - b > 30 && g - b > 20))
                {
                    continue;
                }

                if (start < 0 || x - last > 40)
                {
                    start = x;
                }

                last = x;
                if (last - start > bestEnd - bestStart)
                {
                    (bestStart, bestEnd) = (start, last);
                }
            }

            if (bestStart >= 0 && bestEnd - bestStart + 1 >= width / 10 && Math.Abs((bestStart + bestEnd) / 2.0 - width / 2.0) < width * 0.08)
            {
                rows.Add((y, bestStart, bestEnd));
            }
        }

        // The rims of one frame: rows close together (a few px apart); the biggest group wins
        List<(int Y, int X0, int X1)>? best = null;
        var group = new List<(int Y, int X0, int X1)>();
        foreach (var row in rows.Append<(int Y, int X0, int X1)>((int.MaxValue, 0, 0)))
        {
            if (group.Count > 0 && row.Y - group[^1].Y > height / 120)
            {
                if (best is null || group.Count > best.Count)
                {
                    best = group;
                }

                group = new();
            }

            group.Add(row);
        }

        if (best is not { Count: >= 3 })
        {
            return null;
        }

        int x0 = Median(best.Select(r => r.X0)), x1 = Median(best.Select(r => r.X1));
        return HudBox.FromPixels(x0, best[0].Y, x1 - x0 + 1, best[^1].Y - best[0].Y + 1, width, height);

        static int Median(IEnumerable<int> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            return sorted[sorted.Count / 2];
        }
    }

    /// <summary>
    /// The party frames: thin red HP bars on the left, starting at the same x, one below the other at an even
    /// spacing. Rows can be missing from the picture (a tinted bar, an empty one); the spacing is then a whole
    /// multiple. The width is the widest bar (a damaged member's bar is shorter). Returns the top bar, the row pitch
    /// as a fraction of the height and the number of rows from the top bar to the bottom one.
    /// </summary>
    public static (HudBox First, double Pitch, int Rows)? FindPartyBars(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        var bars = Bars(pixels, width, height, stride, 0, width / 4, 0, height * 70 / 100, Math.Max(20, width / 50), dim: true, maxGap: 4) // no text on these bars; the buff icons beside them stay apart
            .Where(b => b.H >= 3 && b.H <= height / 90 && b.W >= b.H * 4 && b.W <= width / 6)
            .ToList();

        (List<(int X, int Y, int W, int H)> Rows, double Pitch) best = (new(), 0);
        foreach (var a in bars)
        {
            foreach (var b in bars.Where(b => b.Y > a.Y && Math.Abs(b.X - a.X) <= 4))
            {
                for (int k = 1; k <= 4; k++)
                {
                    double pitch = (b.Y - a.Y) / (double)k;
                    if (pitch < a.H * 3 || pitch > height / 12.0)
                    {
                        continue;
                    }

                    double tolerance = Math.Max(3, pitch * 0.06);
                    var rows = bars
                        .Where(c => c.Y >= a.Y && Math.Abs(c.X - a.X) <= 4)
                        .Where(c =>
                        {
                            double steps = (c.Y - a.Y) / pitch;
                            return Math.Abs(steps - Math.Round(steps)) * pitch <= tolerance && Math.Round(steps) <= 8;
                        })
                        .ToList();
                    if (rows.Count > best.Rows.Count || (rows.Count == best.Rows.Count && rows.Count > 0 && pitch > best.Pitch)) // a tie: the larger spacing (half of it fits too)
                    {
                        best = (rows, pitch);
                    }
                }
            }
        }

        if (best.Rows.Count < 2)
        {
            return null;
        }

        var top = best.Rows[0];
        var bottom = best.Rows[^1];
        int count = (int)Math.Round((bottom.Y - top.Y) / best.Pitch) + 1;
        double pitchExact = count > 1 ? (bottom.Y - top.Y) / (double)(count - 1) : best.Pitch;
        int widest = best.Rows.Max(r => r.W);
        return (HudBox.FromPixels(top.X, top.Y, widest, top.H, width, height), pitchExact / height, count);
    }

    /// <summary>Red horizontal bars (rows with a long red run, stacked) inside a region, top to bottom.</summary>
    private static List<(int X, int Y, int W, int H)> Bars(ReadOnlySpan<byte> pixels, int width, int height, int stride,
        int left, int right, int top, int bottom, int minWidth, bool dim = false, int maxGap = 40)
    {
        var rows = new List<(int Y, int X0, int X1)>();
        for (int y = top; y < bottom; y++)
        {
            var row = pixels.Slice(y * stride, width * 4);
            int bestStart = -1, bestEnd = -1, start = -1, last = -1;
            for (int x = left; x < right; x++)
            {
                byte r = row[x * 4 + 2], g = row[x * 4 + 1], b = row[x * 4];
                if (!(IsHpRed(r, g, b) || (dim && IsDimRed(r, g, b))))
                {
                    continue;
                }

                if (start < 0 || x - last > maxGap)
                {
                    start = x;
                }

                last = x;
                if (last - start > bestEnd - bestStart)
                {
                    (bestStart, bestEnd) = (start, last);
                }
            }

            if (bestStart >= 0 && bestEnd - bestStart + 1 >= minWidth)
            {
                rows.Add((y, bestStart, bestEnd));
            }
        }

        var bars = new List<(int X, int Y, int W, int H)>();
        int i = 0;
        while (i < rows.Count)
        {
            int j = i;
            int x0 = rows[i].X0, x1 = rows[i].X1;
            while (j + 1 < rows.Count && rows[j + 1].Y == rows[j].Y + 1 && rows[j + 1].X0 < x1 && rows[j + 1].X1 > x0)
            {
                j++;
                x0 = Math.Min(x0, rows[j].X0);
                x1 = Math.Max(x1, rows[j].X1);
            }

            bars.Add((x0, rows[i].Y, x1 - x0 + 1, rows[j].Y - rows[i].Y + 1));
            i = j + 1;
        }

        return bars;
    }

    /// <summary>A greyed-out (offline or far away) member's bar.</summary>
    private static bool IsDimRed(byte r, byte g, byte b) => r > 115 && g < 70 && b < 80 && r - Math.Max(g, b) > 75;

    private static bool IsHpRed(byte r, byte g, byte b) => r > 150 && g < 80 && b < 80 && r - Math.Max(g, b) > 110;
}
