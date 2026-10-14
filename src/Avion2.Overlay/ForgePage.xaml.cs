using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Overlay;

public sealed record ForgeItemLine(ForgeItem Item, string Name, string Detail, Brush Brush);

public sealed record ForgeTile(string Label, string Average, string Note);

public sealed record MyGearLine(ForgeItem Item, string Name, string Level, Brush Brush, int Enchant);

public sealed record ForgeStepLine(string Step, string Chance, string Pity, string Attempts, string Materials, string Gold, string Detail);

/// <summary>Enchant/amplify cost calculator: pick an item, a level range, see average and unlucky costs.</summary>
public partial class ForgePage : UserControl
{
    private const int MaxResults = 300;

    private readonly GearForge _forge = BundledGameData.Forge;
    private readonly AppState _state;
    private ForgeItem? _item;
    private MyGearLine? _mine;
    private bool _updating;

    public ForgePage(AppState state)
    {
        _state = state;
        InitializeComponent();
        state.InventoryUpdated += () =>
        {
            RefreshMyGear();
            RefreshHave();
            if (Results.Visibility == Visibility.Visible)
            {
                Calculate();
            }
        };
        Loaded += (_, _) =>
        {
            if (ItemList.ItemsSource is null)
            {
                Search(string.Empty);
            }

            RefreshMyGear();
            RefreshHave();
        };
    }

    /// <summary>Equipped gear the Forge knows, at its current levels (from the inventory the game sent).</summary>
    private void RefreshMyGear()
    {
        var lines = (_state.Inventory?.Equipped ?? [])
            .Select(i => (Slot: i, Item: _forge.Find(i.ItemId)))
            .Where(x => x.Item is not null)
            .Select(x => new MyGearLine(x.Item!, x.Item!.Name, $"+{x.Slot.Enchant ?? 0}", GradeBrush(x.Item!.Kind), x.Slot.Enchant ?? 0))
            .ToList();
        MyGear.ItemsSource = lines;
        MyGearPanel.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MyGearTitle.Text = _state.Inventory?.Character is { } who ? $"Your gear ({who})" : "Your gear";
    }

    private void RefreshHave()
    {
        if (_state.Inventory is not { IsKnown: true } inventory)
        {
            HaveLine.Visibility = Visibility.Collapsed;
            return;
        }

        var seen = DateTimeOffset.FromUnixTimeMilliseconds(inventory.UpdatedMs).ToLocalTime();
        string who = inventory.Character is { } name ? $", {name}" : string.Empty;
        ulong stored = inventory.InWarehouse(ItemIds.EnhanceStone);
        string warehouse = stored > 0 ? $" (+{stored:N0} in the warehouse)" : string.Empty;
        HaveLine.Text = $"In your cube: {inventory.Count(ItemIds.EnhanceStone):N0} Enhance Stones{warehouse} and {inventory.Kinah:N0} Kinah" +
                        $" · as of {seen:dd.MM. HH:mm}{who}";
        HaveLine.Visibility = Visibility.Visible;
    }

    private void MyGear_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MyGear.SelectedItem is not MyGearLine line)
        {
            return;
        }

        ItemList.SelectedItem = null;
        _mine = line;
        Select(line.Item, line.Brush);
    }

    /// <summary>Selects one of your equipped items (render shots).</summary>
    internal void PickMine(string name)
    {
        RefreshMyGear();
        MyGear.SelectedItem = (MyGear.ItemsSource as IEnumerable<MyGearLine>)?.FirstOrDefault(l => l.Name == name);
    }

    /// <summary>Searches and selects the first match (render shots).</summary>
    internal void Pick(string query)
    {
        SearchBox.Text = query;
        ItemList.SelectedIndex = 0;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Search(SearchBox.Text);
    }

    private void Search(string query)
    {
        var matches = _forge.Search(query).ToList();
        ItemList.ItemsSource = matches.Take(MaxResults).Select(i => new ForgeItemLine(i, i.Name, Detail(i), GradeBrush(i.Kind))).ToList();
        ResultCount.Text = matches.Count > MaxResults ? $"{MaxResults} of {matches.Count:N0} items, type to narrow down" : matches.Count == 1 ? "1 item" : $"{matches.Count:N0} items";
    }

    private string Detail(ForgeItem item)
    {
        var parts = new List<string> { item.Kind };
        if (_forge.Ladder(item, ForgeMode.Enchant) is { } enchant)
        {
            parts.Add($"enchant +{enchant.MaxLevel}");
        }

        if (_forge.Ladder(item, ForgeMode.Amplify) is { } amplify)
        {
            parts.Add($"amplify +{amplify.MaxLevel}");
        }

        return string.Join(" · ", parts);
    }

    private void ItemList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemList.SelectedItem is not ForgeItemLine line)
        {
            return;
        }

        MyGear.SelectedItem = null;
        _mine = null;
        Select(line.Item, line.Brush);
    }

    private void Select(ForgeItem item, Brush brush)
    {
        _item = item;
        ItemName.Text = _item.Name;
        ItemName.Foreground = brush;
        ItemMeta.Text = Detail(item) + (_mine is { } mine ? $" · yours is +{mine.Enchant}" : string.Empty);
        Controls.IsEnabled = true;

        _updating = true;
        EnchantMode.IsEnabled = _forge.Ladder(_item, ForgeMode.Enchant) is not null;
        AmplifyMode.IsEnabled = _forge.Ladder(_item, ForgeMode.Amplify) is not null;
        if (!EnchantMode.IsEnabled)
        {
            AmplifyMode.IsChecked = true;
        }
        else if (!AmplifyMode.IsEnabled)
        {
            EnchantMode.IsChecked = true;
        }

        _updating = false;
        FillLevels();
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_updating && IsLoaded)
        {
            FillLevels();
        }
    }

    private ForgeMode Mode => AmplifyMode.IsChecked == true ? ForgeMode.Amplify : ForgeMode.Enchant;

    /// <summary>Level pickers for the current item and mode: from +0, to the ladder's maximum.</summary>
    private void FillLevels()
    {
        if (_item is null || _forge.Ladder(_item, Mode) is not { } ladder)
        {
            Results.Visibility = Visibility.Collapsed;
            return;
        }

        _updating = true;
        var levels = Enumerable.Range(0, ladder.MaxLevel + 1).Select(l => $"+{l}").ToList();
        FromBox.ItemsSource = levels;
        ToBox.ItemsSource = levels;
        int current = _mine is null || Mode == ForgeMode.Amplify ? 0 : _mine.Enchant;
        FromBox.SelectedIndex = Math.Min(current, Math.Max(0, ladder.MaxLevel - 1));
        ToBox.SelectedIndex = ladder.MaxLevel;
        _updating = false;
        Calculate();
    }

    private void Level_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating)
        {
            Calculate();
        }
    }

    private void Calculate()
    {
        if (_item is null || _forge.Ladder(_item, Mode) is not { } ladder)
        {
            return;
        }

        int from = Math.Max(0, FromBox.SelectedIndex);
        int to = Math.Max(0, ToBox.SelectedIndex);
        if (to <= from)
        {
            Results.Visibility = Visibility.Visible;
            Summary.Text = "Pick a target level above the current one.";
            Tiles.ItemsSource = null;
            StepList.ItemsSource = null;
            ReachLine.Visibility = Visibility.Collapsed;
            return;
        }

        var plan = GearForge.Plan(ladder, from, to);
        string verb = Mode == ForgeMode.Enchant ? "Enchanting" : "Amplifying";
        Summary.Text = $"{verb} {_item.Name} from +{from} to +{to} takes on average:";

        var tiles = plan.Materials
            .Select(m => new ForgeTile(m.Name, Number(m.Average), $"unlucky: {Number(m.P90)}"))
            .Append(new ForgeTile("Kinah", Number(plan.Gold.Average), $"unlucky: {Number(plan.Gold.P90)}"))
            .Append(new ForgeTile("Attempts", plan.Attempts.Average.ToString("0.0"), $"unlucky: {plan.Attempts.P90:0}"))
            .ToList();
        Tiles.ItemsSource = tiles;

        StepList.ItemsSource = plan.Steps.Select(s => new ForgeStepLine(
            $"+{s.Step.Level} → +{s.Step.Level + 1}",
            $"{s.Step.Chance:0.#}%",
            s.Step.Pity > 0 ? $"+{s.Step.Pity:0.#}%" : "—",
            s.ExpectedAttempts.ToString("0.00"),
            Number(s.ExpectedMaterials),
            Number(s.ExpectedAttempts * s.Step.Gold),
            "Per attempt: " + string.Join(", ", s.Step.Materials.Select(m => $"{m.Count:N0} {m.Name}").Append($"{s.Step.Gold:N0} Kinah")))).ToList();
        Results.Visibility = Visibility.Visible;

        // What the inventory covers (on average)
        if (_state.Inventory is { IsKnown: true } inventory)
        {
            int reach = GearForge.Reach(ladder, from, inventory.Kinah, inventory.Count);
            ReachLine.Text = reach >= to ? $"You have enough for +{to} on average."
                : reach == from ? $"What you have is not enough for +{from + 1} on average."
                : $"What you have covers +{reach} on average.";
            ReachLine.Visibility = Visibility.Visible;
        }
        else
        {
            ReachLine.Visibility = Visibility.Collapsed;
        }
    }

    private static string Number(double value) => value >= 100 ? Math.Round(value).ToString("N0") : value.ToString("0.#");

    /// <summary>Item grade colors as in the game (Common → Epic).</summary>
    private Brush GradeBrush(string kind)
    {
        string grade = kind.Split(' ')[0];
        return grade switch
        {
            "Epic" => Frozen(Color.FromRgb(0xE0, 0x8A, 0x3C)),
            "Unique" => Frozen(Color.FromRgb(0xE6, 0xC2, 0x5A)),
            "Heroic" => Frozen(Color.FromRgb(0x5A, 0x9B, 0xE6)),
            "Rare" => Frozen(Color.FromRgb(0x6C, 0xC4, 0x6C)),
            "Common" => (Brush)FindResource("SecondaryTextBrush"),
            _ => (Brush)FindResource("TitleBrush"),
        };
    }

    private static readonly Dictionary<Color, Brush> Brushes = new();

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
