using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Overlay;

public sealed record ForgeItemLine(ForgeItem Item, string Name, string Detail, Brush Brush);

public sealed record ForgeTile(string Label, string Average, string Note);

public sealed record ForgeStepLine(string Step, string Chance, string Pity, string Attempts, string Materials, string Gold, string Detail);

/// <summary>Enchant/amplify cost calculator: pick an item, a level range, see average and unlucky costs.</summary>
public partial class ForgePage : UserControl
{
    private const int MaxResults = 300;

    private readonly GearForge _forge = BundledGameData.Forge;
    private ForgeItem? _item;
    private bool _updating;

    public ForgePage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (ItemList.ItemsSource is null)
            {
                Search(string.Empty);
            }
        };
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

        _item = line.Item;
        ItemName.Text = _item.Name;
        ItemName.Foreground = line.Brush;
        ItemMeta.Text = line.Detail;
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
        FromBox.SelectedIndex = 0;
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
