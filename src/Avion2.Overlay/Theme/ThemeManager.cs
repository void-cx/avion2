using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Avion2.Overlay;

/// <summary>The Fluent accent ramp a theme retints the standard controls with (sliders, check boxes, selections).</summary>
/// <param name="Fill">Accent fills (check boxes, selections, slider thumbs).</param>
/// <param name="Text">Accent-coloured text.</param>
/// <param name="OnAccent">Text and glyphs on accent fills; null keeps Fluent's default.</param>
public sealed record AccentRamp(Color Base, Color Light1, Color Light2, Color Light3, Color Dark1, Color Dark2, Color Dark3, Color Fill, Color Text, Color? OnAccent = null);

/// <param name="Id">Stored in the settings ("aion2", "void").</param>
/// <param name="Dictionary">Resource dictionary with every theme key (see Theme/README.md).</param>
/// <param name="Icon">Window icon.</param>
/// <param name="VideoBackdrop">Whether the home page may play the key-art video behind the drawn backdrop.</param>
public sealed record ThemeDefinition(string Id, string Name, string Dictionary, string Icon, AccentRamp Accent, bool VideoBackdrop)
{
    public override string ToString() => Name;
}

/// <summary>
/// Swaps the app's theme at runtime. Views reference theme keys as DynamicResource, so replacing the merged
/// dictionary restyles every open window; Fluent's accent brushes are retinted per window.
/// </summary>
public static class ThemeManager
{
    private static Color Rgb(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static readonly ThemeDefinition Aion2 = new("aion2", "AION2", "Theme/Aion2Theme.xaml", "Assets/avion2.ico",
        new AccentRamp(Rgb(0x3E8CF0), Rgb(0x5EA6F5), Rgb(0x7FC4FA), Rgb(0x9ADFFF), Rgb(0x2F6FE0), Rgb(0x2456B0), Rgb(0x1A3E80),
            Fill: Rgb(0x7FC4FA), Text: Rgb(0x9ADFFF)),
        VideoBackdrop: true);

    public static readonly ThemeDefinition Void = new("void", "VOID", "Theme/VoidTheme.xaml", "Assets/void-mark.png",
        // Dark-mode Fluent fills with Light2 and writes accent text in Light3: violet fills with white text (as the
        // manual's primary buttons), violet-text #D27BFF for links and accents.
        new AccentRamp(Rgb(0xB303F8), Rgb(0x9302CC), Rgb(0xB303F8), Rgb(0xD27BFF), Rgb(0x9302CC), Rgb(0x7302A0), Rgb(0x530274),
            Fill: Rgb(0xB303F8), Text: Rgb(0xD27BFF), OnAccent: Colors.White),
        VideoBackdrop: false);

    public static IReadOnlyList<ThemeDefinition> Themes { get; } = [Aion2, Void];

    private static readonly List<WeakReference<Window>> Windows = new();
    private static readonly Dictionary<SolidColorBrush, Color> Defaults = new(ReferenceEqualityComparer.Instance);
    private static ResourceDictionary? _active;

    public static ThemeDefinition Current { get; private set; } = Aion2;

    /// <summary>Raised after a theme switch (UI thread).</summary>
    public static event Action? Changed;

    public static ThemeDefinition Find(string? id) =>
        Themes.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Aion2;

    /// <summary>Activates a theme for the whole app (unknown ids fall back to AION2).</summary>
    public static void Apply(string? id)
    {
        var theme = Find(id);
        var app = Application.Current;
        var dictionary = new ResourceDictionary { Source = new Uri($"pack://application:,,,/AVION2;component/{theme.Dictionary}") };
        var merged = app.Resources.MergedDictionaries;
        int index = _active is null ? -1 : merged.IndexOf(_active);
        if (index >= 0)
        {
            merged[index] = dictionary;
        }
        else
        {
            merged.Insert(0, dictionary);
        }

        _active = dictionary;
        bool changed = theme != Current;
        Current = theme;

        foreach (var window in OpenWindows())
        {
            if (changed && window.ThemeMode == ThemeMode.Dark)
            {
                // Fluent freezes some accent brushes when a window loads its dictionary; reloading it (by toggling
                // the theme mode) rebuilds them, so the retint below reaches all of them.
                window.ThemeMode = ThemeMode.Light;
                window.ThemeMode = ThemeMode.Dark;
            }

            Style(window);
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Applies the current theme's icon and accent to a window, now and whenever the theme changes.</summary>
    public static void Attach(Window window)
    {
        Windows.RemoveAll(w => !w.TryGetTarget(out _));
        Windows.Add(new WeakReference<Window>(window));
        Style(window);
        window.SourceInitialized += (_, _) => Style(window);
        window.Loaded += (_, _) => Style(window);
    }

    private static IEnumerable<Window> OpenWindows()
    {
        foreach (var reference in Windows.ToList())
        {
            if (reference.TryGetTarget(out var window))
            {
                yield return window;
            }
        }
    }

    private static void Style(Window window)
    {
        try
        {
            window.Icon = BitmapFrame.Create(new Uri($"pack://application:,,,/AVION2;component/{Current.Icon}"));
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException)
        {
            // keep the previous icon
        }

        Retint(window, Current.Accent);
    }

    /// <summary>
    /// <c>ThemeMode</c> merges a Fluent dictionary into each window whose accent brushes are resolved from the
    /// Windows accent colour but stay mutable: recolouring those brush instances updates every control using them.
    /// </summary>
    private static void Retint(Window window, AccentRamp accent)
    {
        var r = window.Resources;
        r[SystemColors.AccentColorKey] = accent.Base;
        r[SystemColors.AccentColorLight1Key] = accent.Light1;
        r[SystemColors.AccentColorLight2Key] = accent.Light2;
        r[SystemColors.AccentColorLight3Key] = accent.Light3;
        r[SystemColors.AccentColorDark1Key] = accent.Dark1;
        r[SystemColors.AccentColorDark2Key] = accent.Dark2;
        r[SystemColors.AccentColorDark3Key] = accent.Dark3;
        r[SystemColors.AccentColorBrushKey] = Frozen(accent.Base);
        r[SystemColors.AccentColorLight1BrushKey] = Frozen(accent.Light1);
        r[SystemColors.AccentColorLight2BrushKey] = Frozen(accent.Light2);
        r[SystemColors.AccentColorLight3BrushKey] = Frozen(accent.Light3);
        r[SystemColors.AccentColorDark1BrushKey] = Frozen(accent.Dark1);
        r[SystemColors.AccentColorDark2BrushKey] = Frozen(accent.Dark2);
        r[SystemColors.AccentColorDark3BrushKey] = Frozen(accent.Dark3);

        foreach (var dictionary in r.MergedDictionaries)
        {
            if (dictionary.Source?.OriginalString.Contains("PresentationFramework.Fluent", StringComparison.OrdinalIgnoreCase) == true)
            {
                RetintDictionary(dictionary, accent);
            }
        }
    }

    private static void RetintDictionary(ResourceDictionary dictionary, AccentRamp accent)
    {
        foreach (object key in dictionary.Keys)
        {
            if (key is not string name || !name.Contains("Accent", StringComparison.Ordinal) ||
                dictionary[name] is not SolidColorBrush { IsFrozen: false } brush)
            {
                continue;
            }

            if (name.StartsWith("TextOnAccent", StringComparison.Ordinal))
            {
                // Remember Fluent's own colour, so switching back to a theme without OnAccent restores it.
                Defaults.TryAdd(brush, brush.Color);
                var on = accent.OnAccent ?? Defaults[brush];
                brush.Color = Color.FromArgb(brush.Color.A, on.R, on.G, on.B);
                continue;
            }

            if (!IsChromatic(brush.Color))
            {
                continue;
            }

            var target = name.Contains("Text", StringComparison.Ordinal) ? accent.Text : accent.Fill;
            brush.Color = Color.FromArgb(brush.Color.A, target.R, target.G, target.B);
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            RetintDictionary(merged, accent);
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
