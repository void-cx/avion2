using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <param name="Share">Relative to the biggest skill (bar length).</param>
public sealed record SkillBar(string Name, string Total, double Share, System.Windows.Media.ImageSource? Icon = null);

/// <summary>The selected fight in the History detail pane.</summary>
public sealed record FightSummary(HistoryRow Row, MeterRow? Player)
{
    public string Boss => Row.Boss;
    public string ExtraText => Row.Extra.Length > 0 ? $"{Row.Extra} more targets" : string.Empty;
    public Visibility ExtraVisibility => Row.Extra.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string PartyText => Row.Players <= 1 ? "Solo" : $"{Row.Players} players";
    public string WhenText => DateTimeOffset.FromUnixTimeMilliseconds(Row.StartedAtMs).ToLocalTime().ToString("dd MMM · HH:mm", CultureInfo.InvariantCulture);
    public string Ended => Row.Ended;
    public double Share => Player?.Share ?? 0;
    public string ShareText => Player is null ? "–" : Player.Share.ToString("P0", CultureInfo.InvariantCulture);
    public string Dps => Player?.Dps.ToString("N0", CultureInfo.InvariantCulture) ?? "–";
    public string Duration => Row.Duration;
    public string Crit => Player?.CritRate.ToString("P0", CultureInfo.InvariantCulture) ?? "–";
    public string Hits => Player?.Hits.ToString("N0", CultureInfo.InvariantCulture) ?? "–";
    public string ShareLabel => Player is { IsSelf: false } p ? $"{StreamerMode.Name(p.Name)}'s share of the damage" : "Your share of the damage";
    public string DpsLabel => Player is { IsSelf: false } ? "Top DPS" : "Your DPS";
    public string SkillsLabel => Player is { IsSelf: false } p ? $"TOP SKILLS · {StreamerMode.Name(p.Name).ToUpperInvariant()}" : "TOP SKILLS";

    /// <summary>"Stagger 412 · broke the gauge 2× · 14 dodges (5 sprinting)"; stagger only for fights with a gauge.</summary>
    public string MechanicsText
    {
        get
        {
            if (Player is not { } p)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            if (Row.Entry.Snapshot.Rows.Any(r => r.Stagger > 0) || Row.Entry.Snapshot.Breaks.Count > 0)
            {
                parts.Add($"Stagger {p.Stagger.ToString("N0", CultureInfo.InvariantCulture)}");
                if (p.StaggerBreaks > 0)
                {
                    parts.Add($"broke the gauge {p.StaggerBreaks}×");
                }
            }

            parts.Add($"{DetailViewModel.Dodges(p.Dodges, p.SprintDodges)} dodges");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Who broke the stagger gauge in this fight; empty when nobody did.</summary>
    public string BreaksText => DetailViewModel.Breaks(Row.Entry.Snapshot.Breaks);

    public Visibility BreaksVisibility => BreaksText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public IReadOnlyList<SkillBar> Skills
    {
        get
        {
            var top = Player?.Skills.OrderByDescending(s => s.Total).Take(6).ToList() ?? [];
            long max = Math.Max(1, top.FirstOrDefault()?.Total ?? 1);
            return top.Select(s => new SkillBar(s.Name, s.Total.ToString("N0", CultureInfo.InvariantCulture), s.Total / (double)max, SkillIcons.For(s.SkillId))).ToList();
        }
    }
}

/// <summary>Saved fights: views and search on the left, the list in the middle, the selected fight on the right.</summary>
public partial class HistoryPage : UserControl
{
    private readonly AppState _state;
    private readonly ListCollectionView _all;
    private int _periodDays;

    /// <summary>Only the fights of one dungeon run (from the Runs page); cleared by any other filter change.</summary>
    private (long From, long To, string Label)? _range;
    private DetailWindow? _fightWindow;

    public HistoryPage(AppState state)
    {
        _state = state;
        InitializeComponent();
        _all = new ListCollectionView(state.Fights) { Filter = Matches };
        _all.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HistoryRow.Day)));
        FightList.ItemsSource = _all;
        state.Fights.CollectionChanged += OnFightsChanged;
        state.ViewedCharacterChanged += Refresh;
        Refresh();
    }

    /// <summary>Render shots: the best-per-target view.</summary>
    internal void ShowBest() => BestMode.IsChecked = true;

    public void Select(HistoryRow row)
    {
        AllMode.IsChecked = true;
        Search.Text = string.Empty;
        ((RadioButton)Periods.Children[^1]).IsChecked = true;
        FightList.SelectedItem = row;
        FightList.ScrollIntoView(row);
    }

    private bool Best => BestMode.IsChecked == true;

    /// <summary>The fights of the character the pages show.</summary>
    private IEnumerable<HistoryRow> Mine => _state.ViewedFights;

    /// <summary>Shows only the fights started between two times (a dungeon run's fights).</summary>
    public void ShowRange(long fromMs, long toMs, string label)
    {
        AllMode.IsChecked = true;
        Search.Text = string.Empty;
        ((RadioButton)Periods.Children[^1]).IsChecked = true;
        _range = (fromMs, toMs, label);
        Refresh();
        FightList.SelectedIndex = FightList.Items.Count > 0 ? 0 : -1;
    }

    private bool Matches(object item)
    {
        if (item is not HistoryRow row)
        {
            return false;
        }

        if (!_state.Shows(row.Entry.Snapshot.Self?.Name))
        {
            return false;
        }

        if (_range is { } range && (row.StartedAtMs < range.From || row.StartedAtMs > range.To))
        {
            return false;
        }

        string query = Search?.Text.Trim() ?? string.Empty;
        return (query.Length == 0 || row.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
               (_periodDays == 0 || (_periodDays == 1 ? row.Day == "Today" : row.IsWithin(TimeSpan.FromDays(_periodDays))));
    }

    private void OnFightsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (Best)
        {
            FightList.ItemsSource = HistoryRow.BestPerBoss(Mine).Where(Matches).ToList();
        }
        else
        {
            if (!ReferenceEquals(FightList.ItemsSource, _all))
            {
                FightList.ItemsSource = _all;
            }

            _all.Refresh();
        }

        FightList.Tag = Best ? "best" : null;
        int shown = FightList.Items.Count;
        int bests = HistoryRow.BestPerBoss(Mine).Count;
        Ui.SetCount(AllMode, Mine.Count().ToString("N0", CultureInfo.InvariantCulture));
        Ui.SetCount(BestMode, bests.ToString("N0", CultureInfo.InvariantCulture));
        Shown.Text = Best ? $"{shown} targets" : $"{shown} fights";

        Hint.Text = _range is { } range ? $"The fights of {range.Label}. Change the view or the period to see all fights."
            : Best ? "Your best DPS per boss or named target, from fights of 20 s or more."
            : "Saved on this PC only. Fights include other players' names, so ask before sharing.";
        NoFights.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoFights.Text = _state.Fights.Count == 0 ? "No fights saved yet. Fights are saved when they end."
            : !Mine.Any() ? $"No fights of {StreamerMode.Name(_state.ViewedCharacter ?? string.Empty)} yet. Pick another character in the rail on the left."
            : Best && bests == 0 ? "No records yet: they come from fights of 20 s or more where you dealt damage."
            : "Nothing matches the search or the period.";
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        _range = null;
        if (IsInitialized && _state is not null)
        {
            Refresh();
        }
    }

    private void Period_Checked(object sender, RoutedEventArgs e)
    {
        _range = null;
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
        if (Search.Text.Length > 0)
        {
            _range = null;
        }

        if (IsInitialized && _state is not null)
        {
            Refresh();
        }
    }

    private HistoryRow? Selected => FightList.SelectedItem as HistoryRow;

    private void FightList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is not { } row)
        {
            Pane.Visibility = Visibility.Collapsed;
            Empty.Visibility = Visibility.Visible;
            Chart.Trace = null;
            return;
        }

        var snapshot = row.Entry.Snapshot;
        var player = snapshot.Self ?? snapshot.Rows.OrderByDescending(r => r.Total).FirstOrDefault();
        Pane.DataContext = new FightSummary(row, player);
        Pane.Visibility = Visibility.Visible;
        Empty.Visibility = Visibility.Collapsed;
        var trace = _state.LoadTrace(row.Entry);
        Chart.Trace = trace;
        ReplayButton.IsEnabled = trace is not null;
        ReplayButton.ToolTip = trace is null ? "No replay for this fight (recorded before replays existed)" : null;
    }

    private void FightList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            Delete_Click(sender, e);
            e.Handled = true;
        }
    }

    /// <summary>The fight window (replay, damage by skill, damage taken, buffs, timeline); one, reused.</summary>
    private void OpenFight(int tab)
    {
        if (Selected is not { } row)
        {
            return;
        }

        if (_fightWindow is not { IsLoaded: true })
        {
            _fightWindow = new DetailWindow { Owner = Window.GetWindow(this) };
        }

        _fightWindow.OpenSaved(row.Entry, _state.LoadTrace(row.Entry), tab);
    }

    /// <summary>Render shots: the selected fight's window.</summary>
    internal DetailWindow? OpenFightForShots(int tab)
    {
        OpenFight(tab);
        return _fightWindow;
    }

    private void Replay_Click(object sender, RoutedEventArgs e) => OpenFight(DetailViewModel.ReplayTab);

    private void Details_Click(object sender, RoutedEventArgs e) => OpenFight(0);

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            return;
        }

        try
        {
            Clipboard.SetText(HistoryStore.Summarize(row.Entry.Snapshot, name: StreamerMode.Name));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // clipboard busy; user can retry
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var rows = FightList.SelectedItems.OfType<HistoryRow>().ToList();
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

    private void Storage_Click(object sender, RoutedEventArgs e) => _state.RequestPage("settings");
}
