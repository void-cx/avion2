using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Overlay;

public sealed record ForgeItemLine(ForgeItem Item, string Name, string Detail, Brush Brush);

public sealed record ForgeTile(string Label, string Average, string Note);

/// <param name="ItemLevel">The piece's item level as the game counts it (from the inventory); null when not sent.</param>
public sealed record MyGearLine(ForgeItem Item, string Name, string Level, Brush Brush, int Enchant, byte Slot = 0, uint? ItemLevel = null);

public sealed record UpgradeLine(GearUpgrade Upgrade, string Name, Brush Brush, string Gain, string Step, string Kinah, string Chance, string Materials, string Afford, string Detail);

public sealed record ForgeStepLine(string Step, string Chance, string Pity, string Attempts, string Materials, string Gold, string Detail);

/// <summary>Enchant/amplify cost calculator: pick an item, a level range, see average and unlucky costs.</summary>
public partial class ForgePage : UserControl
{
    private const int MaxResults = 300;

    private readonly GearForge _forge = BundledGameData.Forge;
    private readonly GearStats _stats = BundledGameData.GearStats;
    private readonly AppState _state;
    private ForgeItem? _item;
    private MyGearLine? _mine;
    private bool _updating;

    public ForgePage(AppState state)
    {
        _state = state;
        InitializeComponent();
        state.ViewedCharacterChanged += Refresh;
        state.InventoryUpdated += Refresh;
        state.ProgressUpdated += RefreshMyGear;
        void Refresh()
        {
            RefreshMyGear();
            RefreshHave();
            if (Results.Visibility == Visibility.Visible)
            {
                Calculate();
            }
        }

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
        var lines = (_state.ViewedInventory?.Equipped ?? [])
            .Select(i => (Slot: i, Item: _forge.Find(i.ItemId)))
            .Where(x => x.Item is not null)
            .Select(x => new MyGearLine(x.Item!, x.Item!.Name,
                $"+{x.Slot.Enchant ?? 0}" + (x.Slot.ItemLevel is { } level ? $" · {level}" : string.Empty),
                GradeBrush(x.Item!.Kind), x.Slot.Enchant ?? 0, x.Slot.Slot, x.Slot.ItemLevel))
            .ToList();
        MyGear.ItemsSource = lines;
        MyGearPanel.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var inventory = _state.ViewedInventory;
        string title = inventory?.Character is { } who ? $"Your gear ({StreamerMode.Name(who)})" : "Your gear";
        // The game's item level: the gear's plus the Daevanion board points
        MyGearTitle.ToolTip = null;
        if (_state.Progress is { ItemLevel: { } total, Character: { } owner } && owner == inventory?.Character)
        {
            title += $" · item level {total}";
            if (inventory.GearItemLevel is { } gear && gear <= total)
            {
                MyGearTitle.ToolTip = $"Gear {gear} + Daevanion {total - gear}";
            }
        }

        MyGearTitle.Text = title;
        RefreshUpgrades();
    }

    /// <summary>The next enchant step of each equipped item, cheapest item level first, and what can't fail in total.</summary>
    private void RefreshUpgrades()
    {
        var inventory = _state.ViewedInventory;
        var equipped = inventory?.Equipped.ToList() ?? [];
        var upgrades = GearUpgrades.Next(equipped, _forge, _stats);
        UpgradesCard.Visibility = equipped.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (equipped.Count == 0)
        {
            return;
        }

        var (safeLevels, safeKinah, safeMaterials) = GearUpgrades.SafeSteps(equipped, _forge);
        UpgradesIntro.Text = safeLevels > 0
            ? $"Every step that can't fail on your gear: +{safeLevels} item level for {Number(safeKinah)} Kinah and " +
              string.Join(", ", safeMaterials.Select(m => Amount(m.Count, m.Name))) + "."
            : upgrades.Count > 0 ? "Your gear is past the levels that can't fail; from here every step can fail and gets dearer."
            : "Your gear is fully enchanted.";

        _upgrades = upgrades.Select(u => new UpgradeLine(
            u,
            u.Item.Name,
            GradeBrush(u.Item.Kind),
            string.Join(" · ", u.Stats.Select(g => g.ToString()).Prepend($"+{u.ItemLevel} item level")),
            $"+{u.From} → +{u.From + 1}",
            $"{Number(u.Kinah)} Kinah",
            u.Chance >= 100 ? "can't fail" : $"{u.Chance:0.#}% first try",
            string.Join(", ", u.Materials.Select(m => $"{Number(m.Count)} {Short(m.Name)}")),
            inventory is { IsKnown: true } ? CanAfford(u, inventory) ? "you have enough" : "not enough yet" : string.Empty,
            $"{u.Item.Kind} · average Kinah and materials, failed attempts included")).ToList();
        ShowUpgrades();
    }

    private const int TopUpgrades = 5;

    private List<UpgradeLine> _upgrades = [];
    private bool _allUpgrades;

    private void ShowUpgrades()
    {
        UpgradeList.ItemsSource = _allUpgrades ? _upgrades : _upgrades.Take(TopUpgrades).ToList();
        UpgradesMore.Visibility = _upgrades.Count > TopUpgrades ? Visibility.Visible : Visibility.Collapsed;
        UpgradesMore.Content = _allUpgrades ? "Show the best 5" : $"Show all {_upgrades.Count}";
    }

    private void UpgradesMore_Click(object sender, RoutedEventArgs e)
    {
        _allUpgrades = !_allUpgrades;
        ShowUpgrades();
    }

    private static bool CanAfford(GearUpgrade upgrade, InventoryState inventory) =>
        inventory.Kinah >= upgrade.Kinah && upgrade.Materials.All(m => m.Id is not { } id || inventory.Count(id) >= m.Count);

    /// <summary>"223,685 Enhance Stones", "8 × Noble Belt Enhance Scroll (Bound)".</summary>
    private static string Amount(double count, string material) =>
        material == "Enhance Stone" ? $"{Number(count)} Enhance Stones" : $"{Number(count)} × {material}";

    /// <summary>"Enhance Stone" → "stones" for the narrow column.</summary>
    private static string Short(string material) => material == "Enhance Stone" ? "stones" : material;

    private void UpgradeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UpgradeList.SelectedItem is not UpgradeLine line)
        {
            return;
        }

        MyGear.SelectedItem = (MyGear.ItemsSource as IEnumerable<MyGearLine>)?.FirstOrDefault(l => l.Slot == line.Upgrade.Slot.Slot);
    }

    private void RefreshHave()
    {
        if (_state.ViewedInventory is not { IsKnown: true } inventory)
        {
            HaveLine.Visibility = Visibility.Collapsed;
            return;
        }

        var seen = DateTimeOffset.FromUnixTimeMilliseconds(inventory.UpdatedMs).ToLocalTime();
        string who = inventory.Character is { } name ? $", {StreamerMode.Name(name)}" : string.Empty;
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
        Dispatcher.BeginInvoke(() => PlanCard.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);

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
        if (Mode == ForgeMode.Enchant && _stats.Item(_item.Id) is { } info)
        {
            var gains = info.Effect is { } effect ? _stats.Gain(effect, from, to) : [];
            // Your piece can count more than the table's base plus enchant (the game sends its item level): start from that
            int bonus = _mine is { ItemLevel: { } mine } && mine >= info.ItemLevel + _mine.Enchant ? (int)mine - info.ItemLevel - _mine.Enchant : 0;
            Summary.Text = $"{verb} {_item.Name} from +{from} to +{to} adds {to - from} item level ({info.ItemLevel + bonus + from} → {info.ItemLevel + bonus + to})" +
                           string.Concat(gains.Select(g => $", {g}")) + ". On average it takes:";
        }

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
        if (_state.ViewedInventory is { IsKnown: true } inventory)
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

    private static Brush GradeBrush(string kind) => ItemGrades.Brush(kind.Split(' ')[0]);
}
