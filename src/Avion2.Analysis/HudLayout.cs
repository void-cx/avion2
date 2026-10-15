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

    /// <summary>The boss frame at the top centre (boss skill warnings centred under it).</summary>
    public const string Target = "target";

    /// <summary>The party frames.</summary>
    public const string Party = "party";

    public static IReadOnlyList<string> All { get; } = [SelfHp, Target, Party];

    public static string Label(string id) => id switch
    {
        SelfHp => "Your HP bar",
        Target => "Boss HP bar",
        Party => "Party HP bars",
        _ => id,
    };

    /// <summary>A first guess before calibration (the game's default layout); the player moves them onto the real HUD.</summary>
    public static HudBox Default(string id) => id switch
    {
        SelfHp => new HudBox(0.38, 0.901, 0.104, 0.01),
        Target => new HudBox(0.408, 0.043, 0.182, 0.019),
        Party => new HudBox(0.024, 0.138, 0.081, 0.15),
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

    /// <summary>How many party rows the party box spans (from the top bar to the bottom bar); 0 when unknown.</summary>
    public int PartyRows { get; set; }

    /// <summary>
    /// The party rows' HP bars: the party box split into <see cref="PartyRows"/> evenly spaced bars as tall as
    /// <paramref name="barHeight"/> (a fraction of the screen height).
    /// </summary>
    public IReadOnlyList<HudBox> PartyBars(double barHeight)
    {
        if (PartyRows < 1 || !Boxes.TryGetValue(HudAnchors.Party, out var box))
        {
            return [];
        }

        double pitch = PartyRows == 1 ? 0 : (box.H - barHeight) / (PartyRows - 1);
        return Enumerable.Range(0, PartyRows).Select(i => box with { Y = box.Y + i * pitch, H = barHeight }).ToList();
    }

    public static string Key(int width, int height) => $"{width}x{height}";

    public HudBox Box(string id) => Boxes.TryGetValue(id, out var box) ? box : HudAnchors.Default(id);
}
