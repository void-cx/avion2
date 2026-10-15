using Avion2.Analysis;

namespace Avion2.Tests;

public class HudTests
{
    private const int Width = 1280, Height = 720;

    private static byte[] Frame() => new byte[Width * Height * 4];

    private static void Fill(byte[] frame, int x, int y, int w, int h, byte r, byte g, byte b)
    {
        for (int yy = y; yy < y + h; yy++)
        {
            for (int xx = x; xx < x + w; xx++)
            {
                int i = (yy * Width + xx) * 4;
                frame[i] = b;
                frame[i + 1] = g;
                frame[i + 2] = r;
                frame[i + 3] = 255;
            }
        }
    }

    [Fact]
    public void FindsTheHpBarAcrossItsNumbers()
    {
        // Like the game at 1280×720: HP bar left of the level emblem, white HP text over it, MP bar (blue) to the right
        var frame = Frame();
        Fill(frame, 486, 649, 134, 7, 214, 20, 26);
        Fill(frame, 568, 649, 26, 7, 240, 240, 240);
        Fill(frame, 660, 649, 134, 7, 30, 140, 230);
        // Red noise elsewhere: quest text near the top, a small red icon at the bottom
        Fill(frame, 900, 150, 200, 3, 220, 30, 30);
        Fill(frame, 100, 690, 20, 20, 220, 30, 30);

        var box = HudDetector.FindHpBar(frame, Width, Height, Width * 4);

        Assert.NotNull(box);
        var (x, y, w, h) = box!.ToPixels(Width, Height);
        Assert.Equal(486, x, 1);
        Assert.Equal(649, y, 1);
        Assert.Equal(134, w, 1);
        Assert.Equal(7, h, 1);
    }

    [Fact]
    public void NoHpBarWithoutOne()
    {
        var frame = Frame();
        Fill(frame, 0, 600, Width, 120, 40, 90, 40); // grass

        Assert.Null(HudDetector.FindHpBar(frame, Width, Height, Width * 4));
    }

    [Fact]
    public void BigHitsFlashOnceSmallOnesNever()
    {
        var monitor = new BigHitMonitor();
        Assert.False(monitor.Update(10_000, 10_000, 0));
        Assert.False(monitor.Update(9_500, 10_000, 100));  // 5 %
        Assert.False(monitor.Update(9_000, 10_000, 200));  // 10 % within the second
        Assert.True(monitor.Update(8_400, 10_000, 300));   // 16 %
        Assert.False(monitor.Update(8_300, 10_000, 400));  // the same drop
        Assert.False(monitor.Update(8_300, 10_000, 3_000));

        // Losing HP slowly never flashes
        for (int i = 1; i <= 40; i++)
        {
            Assert.False(monitor.Update((uint)(8_300 - i * 100), 10_000, 3_000 + i * 500));
        }

        // Unknown HP or death resets
        Assert.False(monitor.Update(0, 10_000, 30_000));
        Assert.False(monitor.Update(10_000, 0, 30_100));
    }

    [Fact]
    public void LayoutsAreKeptPerResolution()
    {
        var layout = new HudLayout { Width = 2560, Height = 1440 };
        layout.Boxes[HudAnchors.SelfHp] = HudBox.FromPixels(972, 1298, 267, 14, 2560, 1440);

        var (x, y, w, h) = layout.Box(HudAnchors.SelfHp).ToPixels(1280, 720);
        Assert.Equal(486, x, 1);
        Assert.Equal(649, y, 1);
        Assert.Equal(133.5, w, 1);
        Assert.Equal(7, h, 1);
        Assert.Equal("2560x1440", HudLayout.Key(2560, 1440));
        Assert.Equal(HudAnchors.Default(HudAnchors.Target), layout.Box(HudAnchors.Target)); // not placed yet
    }
}
