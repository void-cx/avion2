using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Avion2.Analysis;

namespace Avion2.Overlay;

/// <summary>Dungeon runs: views by content on the left, the runs in the middle, the selected run on the right.</summary>
public partial class RunsPage : UserControl
{
    private readonly AppState _state;
    private readonly Action<RunRow> _showFights;
    private readonly ListCollectionView _all;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _view = "all";
    private int _periodDays;

    /// <summary>The live run is shown in the pane (instead of a saved one).</summary>
    private bool _showingLive;

    public RunsPage(AppState state, Action<RunRow> showFights)
    {
        _state = state;
        _showFights = showFights;
        InitializeComponent();
        _all = new ListCollectionView(state.Runs) { Filter = Matches };
        _all.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RunRow.Day)));
        RunList.ItemsSource = _all;
        state.Runs.CollectionChanged += OnRunsChanged;
        state.LiveRunChanged += UpdateLive;
        _clock.Tick += (_, _) => UpdateLive();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _clock.Start();
                UpdateLive();
            }
            else
            {
                _clock.Stop();
            }
        };
        Refresh();
    }

    private bool Best => _view == "best";

    private bool Matches(object item)
    {
        if (item is not RunRow row)
        {
            return false;
        }

        string query = Search?.Text.Trim() ?? string.Empty;
        return (_view is "all" or "best" || row.Group == _view) &&
               (query.Length == 0 || row.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.Run.Bosses.Any(b => b.Name.Contains(query, StringComparison.OrdinalIgnoreCase))) &&
               (_periodDays == 0 || (_periodDays == 1 ? row.Day == "Today" : row.IsWithin(TimeSpan.FromDays(_periodDays))));
    }

    private void OnRunsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (Best)
        {
            RunList.ItemsSource = RunRow.BestPerPlace(_state.Runs).Where(Matches).ToList();
        }
        else
        {
            if (!ReferenceEquals(RunList.ItemsSource, _all))
            {
                RunList.ItemsSource = _all;
            }

            _all.Refresh();
        }

        RunList.Tag = Best ? "best" : null;
        foreach (var view in Views.Children.OfType<RadioButton>())
        {
            int count = (string)view.Tag switch
            {
                "all" => _state.Runs.Count,
                "best" => RunRow.BestPerPlace(_state.Runs).Count,
                var group => _state.Runs.Count(r => r.Group == group),
            };
            Ui.SetCount(view, count.ToString("N0", CultureInfo.InvariantCulture));
        }

        int shown = RunList.Items.Count;
        Shown.Text = Best ? $"{shown} dungeons" : shown == 1 ? "1 run" : $"{shown} runs";
        Hint.Text = Best
            ? "Your fastest run per dungeon, among the runs with the most bosses killed there."
            : "From entering an instance to leaving it. Saved on this PC only.";
        NoRuns.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoRuns.Text = _state.Runs.Count == 0
            ? "No runs yet. Start the capture and enter a dungeon, expedition or other instance: the run is saved when you leave."
            : Best && RunRow.BestPerPlace(_state.Runs).Count == 0
                ? "No best times yet: they come from runs where you killed a boss and left normally."
                : "Nothing matches the view, the search or the period.";
    }

    private void View_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string view })
        {
            _view = view;
        }

        if (IsInitialized && _state is not null)
        {
            Refresh();
        }
    }

    private void Period_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string days })
        {
            _periodDays = int.Parse(days, CultureInfo.InvariantCulture);
        }

        if (IsInitialized && _state is not null)
        {
            Refresh();
        }
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized && _state is not null)
        {
            Refresh();
        }
    }

    private RunRow? Selected => RunList.SelectedItem as RunRow;

    private void RunList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is { } row)
        {
            _showingLive = false;
            ShowInPane(new RunSummary(row, _state.Runs));
        }
        else if (!_showingLive)
        {
            ShowInPane(null);
        }
    }

    private void ShowInPane(RunSummary? summary)
    {
        RenameBox.Visibility = Visibility.Collapsed;
        Pane.DataContext = summary;
        Pane.Visibility = summary is null ? Visibility.Collapsed : Visibility.Visible;
        Empty.Visibility = summary is null ? Visibility.Visible : Visibility.Collapsed;
        Actions.Visibility = summary is { Live: false } ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The live card (and the pane while it shows the live run), every second and on every change.</summary>
    private void UpdateLive()
    {
        if (_state.LiveRun is not { } run)
        {
            LiveCard.Visibility = Visibility.Collapsed;
            if (_showingLive)
            {
                _showingLive = false;
                ShowInPane(null);
            }

            return;
        }

        // The clock runs on while nothing happens inside
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var shown = run.Copy();
        shown.EndMs = Math.Max(shown.EndMs, now);
        var row = new RunRow(shown, _state.Data);
        LiveCard.Visibility = Visibility.Visible;
        LiveTitle.Text = row.Title;
        LiveClock.Text = row.Duration;
        string bosses = run.Bosses.Count == 1 ? "1 boss" : $"{run.Bosses.Count} bosses";
        string deaths = run.Deaths.Count == 1 ? "1 death" : $"{run.Deaths.Count} deaths";
        var best = RunRow.BestPerPlace(_state.Runs.Where(r => r.PlaceKey == row.PlaceKey)).FirstOrDefault();
        LiveDetail.Text = $"{row.Category} · {bosses} · {deaths}" + (best is null ? string.Empty : $" · best {best.Duration}");
        if (_showingLive)
        {
            ShowInPane(new RunSummary(row, _state.Runs, live: true));
        }
    }

    private void LiveCard_Click(object sender, MouseButtonEventArgs e)
    {
        RunList.SelectedItem = null;
        _showingLive = true;
        UpdateLive();
    }

    private void RunList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            Delete_Click(sender, e);
            e.Handled = true;
        }
    }

    private void Fights_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row)
        {
            _showFights(row);
        }
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            return;
        }

        RenameBox.Text = row.Run.CustomName ?? string.Empty;
        RenameBox.Visibility = Visibility.Visible;
        RenameBox.Focus();
        RenameBox.SelectAll();
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Selected is { } row)
        {
            string id = row.Run.Id;
            _state.RenameRuns(row, RenameBox.Text);
            RenameBox.Visibility = Visibility.Collapsed;
            RunList.SelectedItem = _state.Runs.FirstOrDefault(r => r.Run.Id == id);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            RenameBox.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e) => RenameBox.Visibility = Visibility.Collapsed;

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            return;
        }

        var text = new StringBuilder($"{row.Title} ({row.Category}) in {row.Duration}");
        if (row.Run.Deaths.Count > 0)
        {
            text.Append($", {row.Run.Deaths.Count} deaths");
        }

        foreach (var boss in row.Run.Bosses)
        {
            text.Append($" | {RunRow.Clock(boss.AtMs)} {boss.Name}");
        }

        try
        {
            Clipboard.SetText(text.ToString());
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // clipboard busy; user can retry
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var rows = RunList.SelectedItems.OfType<RunRow>().ToList();
        if (rows.Count == 0)
        {
            return;
        }

        string what = rows.Count == 1 ? $"the run \"{rows[0].Title}\" ({rows[0].When})" : $"{rows.Count} runs";
        if (MessageBox.Show(Window.GetWindow(this)!, $"Delete {what}? The fights stay in the Fight History.", "AVION2",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _state.DeleteRuns(rows);
        }
    }

    /// <summary>Render shots: select the first run.</summary>
    internal void SelectFirst()
    {
        if (RunList.Items.Count > 0)
        {
            RunList.SelectedIndex = 0;
        }
    }
}
