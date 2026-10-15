using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Avion2.Analysis;

namespace Avion2.Overlay;

public sealed record EventLine(string Category, string Name, string At, string In, string Rule);

/// <summary>A row of the Timers list; updated in place every refresh so selection and scrolling stay.</summary>
public sealed class TimerItem(string key, string kind) : Bindable
{
    private string _name = string.Empty;
    private string _where = string.Empty;
    private string _status = string.Empty;
    private Brush _statusBrush = Brushes.White;
    private Brush _statusBack = Brushes.Transparent;
    private string _in = string.Empty;

    public string Key { get; } = key;

    /// <summary>"field", "world" or "event".</summary>
    public string Kind { get; } = kind;

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Where { get => _where; set => Set(ref _where, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }
    public Brush StatusBack { get => _statusBack; set => Set(ref _statusBack, value); }
    public string In { get => _in; set => Set(ref _in, value); }

    public string Category { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }
    public string Rule { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
}

/// <summary>Spacetime Rifts, events, world bosses and the live field boss table: views on the left, the list, the selected timer.</summary>
public partial class TimersPage : UserControl
{
    private readonly AppState _state;
    private readonly Action<string, MapMarker> _showOnMap;
    private readonly Action<HistoryRow> _openFight;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly ObservableCollection<TimerItem> _items = new();
    private string _view = "next";
    private HistoryRow? _best;

    public TimersPage(AppState state, Action<string, MapMarker> showOnMap, Action<HistoryRow> openFight)
    {
        _state = state;
        _showOnMap = showOnMap;
        _openFight = openFight;
        InitializeComponent();
        DataContext = state.Settings;
        TimerList.ItemsSource = _items;
        RegionBox.ItemsSource = Enum.GetValues<ServerRegion>();
        RegionBox.SelectionChanged += (_, _) => Refresh();
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            Refresh();
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>Render shots: select the first field boss on a map (or the first timer).</summary>
    internal bool SelectFirst()
    {
        TimerList.SelectedItem = _items.FirstOrDefault(i => BossLocator.Find(i.Name) is not null) ?? _items.FirstOrDefault();
        return TimerList.SelectedItem is not null;
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

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized && _state is not null)
        {
            Refresh();
        }
    }

    private void Notifications_Click(object sender, RoutedEventArgs e) => _state.RequestPage("settings");

    private void Refresh()
    {
        var now = DateTimeOffset.Now;
        var region = _state.Settings.Region;
        var offset = GameSchedule.ServerOffset(region);
        RegionNote.Text = $"GMT+{offset.TotalHours:0} · schedules in server time, countdowns in yours";
        ServerTime.Text = $"Server time {now.ToOffset(offset):HH:mm}";

        // Rift card
        var rifts = _state.Timers.NextRifts(now, 5);
        RiftIn.Text = "in " + Countdown(rifts[0] - now);
        RiftAt.Text = $"Opens at {rifts[0].ToLocalTime():HH:mm}";
        var interval = rifts.Count > 1 ? rifts[1] - rifts[0] : TimeSpan.FromHours(3);
        RiftBar.Value = Math.Clamp(1 - (rifts[0] - now) / interval, 0, 1);
        RiftList.Text = "Then " + string.Join(" · ", rifts.Skip(1).Select(r => r.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)));

        // World boss card
        var upcoming = _state.Timers.Upcoming(now);
        if (upcoming.FirstOrDefault(o => o.Event.Category == ScheduleCategory.WorldBoss) is { } world)
        {
            WorldIn.Text = "in " + Countdown(world.StartsAt - now);
            WorldAt.Text = $"{world.Event.Name} · {world.StartsAt.ToLocalTime():HH:mm}";
            WorldBar.Value = Math.Clamp(1 - (world.StartsAt - now).TotalHours / 24, 0, 1);
            WorldZone.Text = world.Event.Zone ?? string.Empty;
        }

        var items = Lines(now, upcoming);
        Ui.SetCount(FieldView, items.Count(i => i.Kind == "field").ToString(CultureInfo.InvariantCulture));
        Ui.SetCount(WorldView, items.Count(i => i.Kind == "world").ToString(CultureInfo.InvariantCulture));
        Ui.SetCount(EventView, items.Count(i => i.Kind == "event").ToString(CultureInfo.InvariantCulture));

        string query = Search.Text.Trim();
        var shown = items
            .Where(i => _view == "next" || i.Kind == _view)
            .Where(i => query.Length == 0 || i.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || i.Where.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.At < now ? 1 : 0) // coming up first, then what is up already
            .ThenBy(i => i.At < now ? -i.At.ToUnixTimeMilliseconds() : i.At.ToUnixTimeMilliseconds())
            .Take(_view == "next" ? 30 : 200)
            .ToList();
        Sync(shown);

        ListTitle.Text = _view switch
        {
            "field" => "Field bosses · from the game's respawn table",
            "world" => region is ServerRegion.Korea or ServerRegion.Taiwan ? "World bosses · Korea/Taiwan schedule" : "World bosses · global schedule",
            "event" => "Events and Spacetime Rifts",
            _ => "Up next",
        };
        NoTimers.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoTimers.Text = query.Length > 0 ? "Nothing matches the search."
            : _view == "field" ? "No field boss data yet. Visit a field map with the meter running: the game sends the respawn table of the map you are on."
            : "Nothing scheduled.";

        if (TimerList.SelectedItem is TimerItem selected)
        {
            DetailCountdown.Text = selected.In;
        }
    }

    /// <summary>Every timer as a row: field bosses (live), world bosses, events and the next rift.</summary>
    private List<TimerItem> Lines(DateTimeOffset now, IReadOnlyList<Occurrence> upcoming)
    {
        var lines = new List<TimerItem>();
        TimerItem Item(string key, string kind) => _items.FirstOrDefault(i => i.Key == key) ?? new TimerItem(key, kind);

        foreach (var r in _state.FieldBosses.Rows(_state.Data, now))
        {
            var item = Item($"field:{r.MapId}:{r.Name}", "field");
            item.Name = r.Name;
            item.Where = _state.Data.MapName(r.MapId);
            item.Category = "Field boss";
            item.At = r.At;
            item.Rule = string.Empty;
            item.Source = "Respawn table sent by the game";
            switch (r.Status)
            {
                case FieldBossStatus.Respawning:
                    Tone(item, r.At - now < TimeSpan.FromMinutes(15) ? "soon" : "later", "Respawning");
                    item.In = "in " + Countdown(r.At - now);
                    break;
                case FieldBossStatus.Due:
                    Tone(item, "due", "Probably up");
                    item.In = $"due since {r.At.ToLocalTime():HH:mm}";
                    break;
                default:
                    Tone(item, "up", "Up");
                    item.In = $"since {r.At.ToLocalTime():HH:mm}";
                    break;
            }

            lines.Add(item);
        }

        foreach (var o in upcoming.Where(o => o.Event.Category != ScheduleCategory.Rift))
        {
            bool worldBoss = o.Event.Category == ScheduleCategory.WorldBoss;
            var item = Item($"{o.Event.Category}:{o.Event.Name}", worldBoss ? "world" : "event");
            item.Name = o.Event.Name;
            item.Where = o.Event.Zone ?? AppState.CategoryLabel(o.Event.Category);
            item.Category = worldBoss ? "World boss" : AppState.CategoryLabel(o.Event.Category);
            item.At = o.StartsAt;
            item.Rule = GameSchedule.Describe(o.Event) + " server time";
            item.Source = "Fixed server schedule";
            Tone(item, o.StartsAt - now < TimeSpan.FromMinutes(15) ? "soon" : "later", worldBoss ? "Scheduled" : item.Category);
            item.In = "in " + Countdown(o.StartsAt - now);
            lines.Add(item);
        }

        var rift = _state.Timers.NextRifts(now, 1)[0];
        var next = Item("rift", "event");
        next.Name = "Spacetime Rift";
        next.Where = $"{_state.Settings.Region} server";
        next.Category = "Spacetime Rift";
        next.At = rift;
        next.Rule = "Every few hours, server time";
        next.Source = "Fixed server schedule";
        Tone(next, rift - now < TimeSpan.FromMinutes(15) ? "soon" : "later", "Spacetime Rift");
        next.In = "in " + Countdown(rift - now);
        lines.Add(next);
        return lines;
    }

    private void Tone(TimerItem item, string tone, string status)
    {
        item.Status = status;
        (item.StatusBrush, item.StatusBack) = tone switch
        {
            "up" => (Theme("LiveBrush"), Theme("PillBrush")),
            "due" => (Theme("WarnBrush"), Theme("PillBrush")),
            "soon" => (Theme("AccentTextBrush"), Theme("AccentPillBrush")),
            _ => (Theme("PillTextBrush"), Theme("PillBrush")),
        };
    }

    /// <summary>Shows <paramref name="shown"/> in order, reusing rows (selection and scroll position stay).</summary>
    private void Sync(List<TimerItem> shown)
    {
        if (_items.Select(i => i.Key).SequenceEqual(shown.Select(i => i.Key)))
        {
            return;
        }

        string? selected = (TimerList.SelectedItem as TimerItem)?.Key;
        _items.Clear();
        foreach (var item in shown)
        {
            _items.Add(item);
        }

        TimerList.SelectedItem = _items.FirstOrDefault(i => i.Key == selected);
    }

    private void TimerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TimerList.SelectedItem is not TimerItem item)
        {
            if (e.RemovedItems.Count > 0 && _items.Count > 0)
            {
                return; // the list is being rebuilt; the selection comes back
            }

            Pane.Visibility = Visibility.Collapsed;
            Empty.Visibility = Visibility.Visible;
            return;
        }

        Pane.Visibility = Visibility.Visible;
        Empty.Visibility = Visibility.Collapsed;
        DetailName.Text = item.Name;
        DetailCategory.Text = item.Category;
        DetailWhere.Text = item.Where;
        DetailWhereBox.Visibility = item.Where.Length > 0 && item.Where != item.Category ? Visibility.Visible : Visibility.Collapsed;
        DetailCountdown.Text = item.In;

        // The spawn area on the map, when the boss is a map marker
        var located = BossLocator.Find(item.Name);
        ShowOnMap.IsEnabled = located is not null;
        if (BossLocator.Crop(item.Name, 384, 170) is { } crop)
        {
            MapCrop.Source = crop.Image;
            Canvas.SetLeft(Pin, crop.At.X - 13);
            Canvas.SetTop(Pin, crop.At.Y - 13);
            PinLayer.Visibility = Visibility.Visible;
            NoMap.Visibility = Visibility.Collapsed;
        }
        else
        {
            MapCrop.Source = null;
            PinLayer.Visibility = Visibility.Collapsed;
            NoMap.Visibility = Visibility.Visible;
            NoMapText.Text = item.Kind == "field" ? "Not on the bundled maps" : item.Category;
        }

        _best = HistoryRow.BestPerBoss(_state.Fights).FirstOrDefault(r => string.Equals(r.Boss, item.Name, StringComparison.OrdinalIgnoreCase));
        OpenBest.Visibility = _best is null ? Visibility.Collapsed : Visibility.Visible;

        var facts = new List<(string, string)>
        {
            ("When", item.At.ToLocalTime().ToString(item.At - DateTimeOffset.Now > TimeSpan.FromHours(20) ? "ddd HH:mm" : "HH:mm", CultureInfo.InvariantCulture) + " your time"),
        };
        if (item.Rule.Length > 0)
        {
            facts.Add(("Schedule", item.Rule));
        }

        facts.Add(("Source", item.Source));
        if (_best is { } best)
        {
            facts.Add(("Your best", $"{best.YourDpsFull} DPS · {best.Day}"));
        }

        SetFacts(facts);
    }

    private void SetFacts(List<(string Label, string Value)> facts)
    {
        Facts.Children.Clear();
        Facts.RowDefinitions.Clear();
        for (int i = 0; i < facts.Count; i++)
        {
            Facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = facts[i].Label, Margin = new Thickness(0, 0, 0, 10) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            var value = new TextBlock { Text = facts[i].Value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            value.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            Facts.Children.Add(label);
            Facts.Children.Add(value);
        }
    }

    private void ShowOnMap_Click(object sender, RoutedEventArgs e)
    {
        if (TimerList.SelectedItem is TimerItem item && BossLocator.Find(item.Name) is { } located)
        {
            _showOnMap(located.Key, located.Marker);
        }
    }

    private void OpenBest_Click(object sender, RoutedEventArgs e)
    {
        if (_best is { } best)
        {
            _openFight(best);
        }
    }

    internal static string Countdown(TimeSpan span) => span.TotalHours >= 24
        ? $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes:00}m"
        : $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";

    /// <summary>A brush of the current theme (looked up on every refresh, so theme switches apply).</summary>
    private Brush Theme(string key) => (Brush)FindResource(key);
}
