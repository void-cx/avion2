using System.Windows;
using System.Windows.Media;

namespace Avion2.Overlay;

/// <summary>Attached properties the shell styles (Theme/Shell.xaml) read: an icon, a count and a placeholder.</summary>
public static class Ui
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(Geometry), typeof(Ui), new FrameworkPropertyMetadata(null));

    /// <summary>A stroke icon on a 24 × 24 grid (see the Icon* geometries in Theme/Shell.xaml).</summary>
    public static Geometry? GetIcon(DependencyObject d) => (Geometry?)d.GetValue(IconProperty);

    public static void SetIcon(DependencyObject d, Geometry? value) => d.SetValue(IconProperty, value);

    public static readonly DependencyProperty CountProperty = DependencyProperty.RegisterAttached(
        "Count", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));

    /// <summary>A number shown at the right of a sidebar item.</summary>
    public static string? GetCount(DependencyObject d) => (string?)d.GetValue(CountProperty);

    public static void SetCount(DependencyObject d, string? value) => d.SetValue(CountProperty, value);

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));

    /// <summary>Hint text of an empty search box.</summary>
    public static string? GetPlaceholder(DependencyObject d) => (string?)d.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject d, string? value) => d.SetValue(PlaceholderProperty, value);
}
