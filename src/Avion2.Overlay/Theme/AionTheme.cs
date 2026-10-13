using System.Windows;
using System.Windows.Media;

namespace Avion2.Overlay;

/// <summary>
/// Retints the Fluent dark theme with AION2's aether blue instead of the Windows accent colour.
/// <para>
/// <c>ThemeMode</c> merges a Fluent dictionary into each window whose accent brushes are already resolved
/// to the Windows accent, but are mutable: recolouring those brush instances updates every control that
/// uses them (sliders, check boxes, selections, focus lines).
/// </para>
/// </summary>
public static class AionTheme
{
    private static readonly Color Fill = Color.FromRgb(0x7F, 0xC4, 0xFA);      // aether blue (Light2)
    private static readonly Color Text = Color.FromRgb(0x9A, 0xDF, 0xFF);      // lighter, for accent text (Light3)

    public static void Apply(Window window)
    {
        Retint(window);
        window.SourceInitialized += (_, _) => Retint(window);
        window.Loaded += (_, _) => Retint(window);
    }

    private static void Retint(Window window)
    {
        // Fluent's SystemAccentColor* entries are dynamic references to these system keys.
        var r = window.Resources;
        r[SystemColors.AccentColorKey] = Color.FromRgb(0x3E, 0x8C, 0xF0);
        r[SystemColors.AccentColorLight1Key] = Color.FromRgb(0x5E, 0xA6, 0xF5);
        r[SystemColors.AccentColorLight2Key] = Fill;
        r[SystemColors.AccentColorLight3Key] = Text;
        r[SystemColors.AccentColorDark1Key] = Color.FromRgb(0x2F, 0x6F, 0xE0);
        r[SystemColors.AccentColorDark2Key] = Color.FromRgb(0x24, 0x56, 0xB0);
        r[SystemColors.AccentColorDark3Key] = Color.FromRgb(0x1A, 0x3E, 0x80);
        r[SystemColors.AccentColorBrushKey] = Frozen(Color.FromRgb(0x3E, 0x8C, 0xF0));
        r[SystemColors.AccentColorLight1BrushKey] = Frozen(Color.FromRgb(0x5E, 0xA6, 0xF5));
        r[SystemColors.AccentColorLight2BrushKey] = Frozen(Fill);
        r[SystemColors.AccentColorLight3BrushKey] = Frozen(Text);
        r[SystemColors.AccentColorDark1BrushKey] = Frozen(Color.FromRgb(0x2F, 0x6F, 0xE0));
        r[SystemColors.AccentColorDark2BrushKey] = Frozen(Color.FromRgb(0x24, 0x56, 0xB0));
        r[SystemColors.AccentColorDark3BrushKey] = Frozen(Color.FromRgb(0x1A, 0x3E, 0x80));

        foreach (var dictionary in window.Resources.MergedDictionaries)
        {
            if (dictionary.Source?.OriginalString.Contains("PresentationFramework.Fluent", StringComparison.OrdinalIgnoreCase) == true)
            {
                RetintDictionary(dictionary);
            }
        }
    }

    private static void RetintDictionary(ResourceDictionary dictionary)
    {
        foreach (object key in dictionary.Keys)
        {
            if (key is not string name || !name.Contains("Accent", StringComparison.Ordinal) ||
                dictionary[name] is not SolidColorBrush { IsFrozen: false } brush || !IsChromatic(brush.Color))
            {
                continue;
            }

            var target = name.Contains("Text", StringComparison.Ordinal) ? Text : Fill;
            brush.Color = Color.FromArgb(brush.Color.A, target.R, target.G, target.B);
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            RetintDictionary(merged);
        }
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Accent-derived colours are saturated; greys/white/black (disabled states, strokes) are left alone.</summary>
    private static bool IsChromatic(Color c)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B));
        int min = Math.Min(c.R, Math.Min(c.G, c.B));
        return max - min > 40;
    }
}
