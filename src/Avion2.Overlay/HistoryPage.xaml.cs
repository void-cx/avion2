using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <summary>Saved fights on the left, the selected fight's full breakdown on the right.</summary>
public partial class HistoryPage : UserControl
{
    private readonly AppState _state;

    public HistoryPage(AppState state)
    {
        _state = state;
        InitializeComponent();
        Grid.ItemsSource = state.Fights;
        state.Fights.CollectionChanged += OnFightsChanged;
        UpdateHint();
    }

    public void Select(HistoryRow row)
    {
        Grid.SelectedItem = row;
        Grid.ScrollIntoView(row);
    }

    private void OnFightsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateHint();

    private void UpdateHint() =>
        Hint.Text = _state.Fights.Count == 0
            ? "No fights saved yet. Fights are saved when they end."
            : $"{_state.Fights.Count} fights · saved locally only · they include other players' names, so ask before sharing";

    private HistoryRow? Selected => Grid.SelectedItem as HistoryRow;

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is { } row)
        {
            Detail.Visibility = Visibility.Visible;
            Empty.Visibility = Visibility.Collapsed;
            Detail.ShowSaved(row.Entry, trace: _state.LoadTrace(row.Entry));
        }
        else
        {
            Detail.Visibility = Visibility.Collapsed;
            Empty.Visibility = Visibility.Visible;
            Detail.ShowSaved(null);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            return;
        }

        try
        {
            Clipboard.SetText(HistoryStore.Summarize(row.Entry.Snapshot));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // clipboard busy; user can retry
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var rows = Grid.SelectedItems.OfType<HistoryRow>().ToList();
        if (rows.Count == 0)
        {
            return;
        }

        string what = rows.Count == 1 ? $"the saved fight \"{rows[0].Title}\" ({rows[0].When})" : $"{rows.Count} saved fights";
        if (MessageBox.Show(Window.GetWindow(this)!, $"Delete {what} and its replay?", "AVION2",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            foreach (var row in rows)
            {
                _state.DeleteFight(row);
            }
        }
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_state.History.Directory);
        Process.Start(new ProcessStartInfo("explorer.exe", _state.History.Directory) { UseShellExecute = true });
    }
}
