namespace Avion2.Analysis;

/// <summary>A HUD element's box as fractions of the game's client area (0..1), so it survives window moves.</summary>
public sealed record HudBox(double X, double Y, double W, double H)
{
    public static HudBox FromPixels(double x, double y, double w, double h, int width, int height) =>
        new(Math.Clamp(x / width, 0, 1), Math.Clamp(y / height, 0, 1), Math.Clamp(w / width, 0, 1), Math.Clamp(h / height, 0, 1));

    public (double X, double Y, double W, double H) ToPixels(double width, double height) => (X * width, Y * height, W * width, H * height);
}

/// <summary>The HUD elements AVION2 draws effects on.</summary>
public static class HudAnchors
{
    /// <summary>Your own HP bar (low-HP blink).</summary>
    public const string SelfHp = "selfHp";

    /// <summary>The target / boss frame (boss skill warnings under it).</summary>
    public const string Target = "target";

    /// <summary>The party frames.</summary>
    public const string Party = "party";

    /// <summary>The elements the calibration asks for (party frames: once an effect uses them).</summary>
    public static IReadOnlyList<string> All { get; } = [SelfHp, Target];

    public static string Label(string id) => id switch
    {
        SelfHp => "Your HP bar",
        Target => "Target frame",
        Party => "Party frames",
        _ => id,
    };

    /// <summary>A first guess before calibration (the game's default layout); the player moves them onto the real HUD.</summary>
    public static HudBox Default(string id) => id switch
    {
        SelfHp => new HudBox(0.395, 0.855, 0.21, 0.02),
        Target => new HudBox(0.38, 0.05, 0.24, 0.045),
        Party => new HudBox(0.01, 0.30, 0.13, 0.26),
        _ => new HudBox(0.45, 0.45, 0.1, 0.05),
    };
}

/// <summary>
/// Where the HUD elements are, calibrated for one game resolution (the game scales its HUD with it, so a layout
/// only applies to the size it was made at).
/// </summary>
public sealed class HudLayout
{
    public int Width { get; set; }

    public int Height { get; set; }

    public Dictionary<string, HudBox> Boxes { get; set; } = new();

    public DateTime CalibratedUtc { get; set; }

    public static string Key(int width, int height) => $"{width}x{height}";

    public HudBox Box(string id) => Boxes.TryGetValue(id, out var box) ? box : HudAnchors.Default(id);
}
