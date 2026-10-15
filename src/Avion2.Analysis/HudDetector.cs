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
    /// The target's HP bar: a red bar in the upper right quarter (under the minimap), wider than tall.
    /// Only present while something is targeted.
    /// </summary>
    public static HudBox? FindTargetBar(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        var bars = Bars(pixels, width, height, stride, width * 55 / 100, width, 0, height * 35 / 100, Math.Max(30, width / 30));
        var best = bars
            .Where(b => b.H >= 4 && b.H <= height / 50 && b.W >= b.H * 8 && b.W <= width / 6)
            .OrderByDescending(b => b.W)
            .FirstOrDefault();
        return best.W == 0 ? null : HudBox.FromPixels(best.X, best.Y, best.W, best.H, width, height);
    }

    /// <summary>
    /// The party frames: two or more thin red HP bars on the left, aligned at the same x, with the same width, one
    /// below the other at an even spacing. Returns the first (top) bar and the row pitch as a fraction of the height.
    /// </summary>
    public static (HudBox First, double Pitch, int Rows)? FindPartyBars(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        var bars = Bars(pixels, width, height, stride, 0, width / 4, 0, height * 70 / 100, Math.Max(30, width / 30))
            .Where(b => b.H >= 3 && b.H <= height / 90 && b.W >= b.H * 10 && b.W <= width / 6)
            .ToList();

        // Longest run of equally spaced, aligned bars
        (int Start, int Count, int Pitch) best = default;
        for (int i = 0; i < bars.Count; i++)
        {
            for (int j = i + 1; j < bars.Count; j++)
            {
                int pitch = bars[j].Y - bars[i].Y;
                if (!Aligned(bars[i], bars[j]) || pitch < bars[i].H * 3)
                {
                    continue;
                }

                int count = 2;
                var last = bars[j];
                foreach (var next in bars.Skip(j + 1))
                {
                    if (Aligned(bars[i], next) && Math.Abs(next.Y - last.Y - pitch) <= 2)
                    {
                        count++;
                        last = next;
                    }
                }

                if (count > best.Count)
                {
                    best = (i, count, pitch);
                }
            }
        }

        if (best.Count < 2)
        {
            return null;
        }

        var first = bars[best.Start];
        return (HudBox.FromPixels(first.X, first.Y, first.W, first.H, width, height), best.Pitch / (double)height, best.Count);

        static bool Aligned((int X, int Y, int W, int H) a, (int X, int Y, int W, int H) b) =>
            Math.Abs(a.X - b.X) <= 4 && Math.Abs(a.W - b.W) <= Math.Max(6, a.W / 12);
    }

    /// <summary>Red horizontal bars (rows with a long red run, stacked) inside a region, top to bottom.</summary>
    private static List<(int X, int Y, int W, int H)> Bars(ReadOnlySpan<byte> pixels, int width, int height, int stride,
        int left, int right, int top, int bottom, int minWidth)
    {
        const int maxGap = 40;
        var rows = new List<(int Y, int X0, int X1)>();
        for (int y = top; y < bottom; y++)
        {
            var row = pixels.Slice(y * stride, width * 4);
            int bestStart = -1, bestEnd = -1, start = -1, last = -1;
            for (int x = left; x < right; x++)
            {
                if (!IsHpRed(row[x * 4 + 2], row[x * 4 + 1], row[x * 4]))
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

    private static bool IsHpRed(byte r, byte g, byte b) => r > 150 && g < 80 && b < 80 && r - Math.Max(g, b) > 110;
}
