using System.Windows;
using System.Windows.Controls;
using Avion2.Analysis;

namespace Avion2.Overlay;

/// <summary>
/// The pet kinds under Home's progress card, opened from a link: souls toward the next pet or level, the level, and the
/// monsters that dropped each kind's soul for us.
/// </summary>
public partial class PetSoulsView : UserControl
{
    private int _count;

    public PetSoulsView()
    {
        InitializeComponent();
        Label();
    }

    /// <summary>Shows the rows; collapses itself when there are none.</summary>
    public void Show(IReadOnlyList<PetSoulRow> rows)
    {
        _count = rows.Count;
        Rows.ItemsSource = rows;
        Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        Label();
    }

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        Details.Visibility = Toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Label();
    }

    private void Label() => Toggle.Content = Toggle.IsChecked == true
        ? "Hide pet souls"
        : _count == 1 ? "Show 1 pet kind with souls" : $"Show {_count} pet kinds with souls";
}
