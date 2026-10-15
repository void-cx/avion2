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

    private static bool IsHpRed(byte r, byte g, byte b) => r > 150 && g < 80 && b < 80 && r - Math.Max(g, b) > 110;
}
