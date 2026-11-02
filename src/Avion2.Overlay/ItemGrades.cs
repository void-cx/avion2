using System.Windows;
using System.Windows.Media;

namespace Avion2.Overlay;

/// <summary>Item grade colors as in the game (Common → Epic).</summary>
public static class ItemGrades
{
    private static readonly Dictionary<Color, Brush> Brushes = new();

    /// <param name="grade">"Unique", "Heroic", … The official site calls Heroic "Legend".</param>
    public static Brush Brush(string grade) => grade switch
    {
        "Epic" => Frozen(Color.FromRgb(0xE0, 0x8A, 0x3C)),
        "Unique" => Frozen(Color.FromRgb(0xE6, 0xC2, 0x5A)),
        "Heroic" or "Legend" => Frozen(Color.FromRgb(0x5A, 0x9B, 0xE6)),
        "Special" => Frozen(Color.FromRgb(0xB4, 0x8C, 0xE6)),
        "Rare" => Frozen(Color.FromRgb(0x6C, 0xC4, 0x6C)),
        "Common" => Resource("SecondaryTextBrush"),
        _ => Resource("TitleBrush"),
    };

    private static Brush Resource(string key) => Application.Current.TryFindResource(key) as Brush ?? System.Windows.Media.Brushes.Gray;

    private static Brush Frozen(Color color)
    {
        if (!Brushes.TryGetValue(color, out var brush))
        {
            brush = new SolidColorBrush(color);
            brush.Freeze();
            Brushes[color] = brush;
        }

        return brush;
    }
}
