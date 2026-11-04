using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Overlay;

public sealed record CraftProductLine(CraftProduct Product, string Name, string Detail, Brush Brush);

public sealed record CraftMaterialRow(string Name, Brush Brush, string Need, string Have, string Missing, Brush MissingBrush);

/// <param name="BuyVisibility">The "buy / gather" toggle, for materials with a recipe of their own.</param>
/// <param name="Combo">"25 % combo: Splendent …" for an intermediate whose recipe has one.</param>
public sealed record CraftStepRow(string Need, string Name, Brush Brush, string How, Thickness Indent, uint ItemId = 0, bool Bought = false,
    Visibility BuyVisibility = Visibility.Collapsed, string? Combo = null)
{
    public Visibility ComboVisibility => Combo is null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// The Forge's crafting tab: pick a craftable item, see every material down to the gathered ones, what the bag of the
/// character shown holds and what is missing; below, that character's crafts as the meter saw them.
/// </summary>
public partial class CraftingPanel : UserControl
{
    private const int MaxResults = 300;
    private const string AllProfessions = "All professions";
    private const string AllCategories = "All item types";

    private readonly RecipeBook _book = BundledGameData.Recipes;
    private AppState? _state;
    private CraftProduct? _product;
    private string? _sideFor;
    private bool _filling;

    public CraftingPanel()
    {
        InitializeComponent();
        FillBrowse();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Refresh();
            }
        };
    }

    /// <summary>Follows the character shown and their bag and crafts.</summary>
    public void Attach(AppState state)
    {
        _state = state;
        state.ViewedCharacterChanged += Refresh;
        state.InventoryUpdated += Refresh;
        state.Crafts.Updated += Refresh;
    }

    private CraftRace Side => DarkSide.IsChecked == true ? CraftRace.Dark : CraftRace.Light;

    private void Refresh()
    {
        if (!IsVisible || _state is null)
        {
            return;
        }

        // The side of the character shown, from its server, once per character (the toggles stay free to change)
        string? who = _state.ViewedCharacter;
        if (who != _sideFor)
        {
            _sideFor = who;
            var known = new CharacterStore().Characters.FirstOrDefault(c => c.Name == who);
            if (known is not null && RecipeBook.RaceOf(BundledGameData.Server(known.ServerId)?.Faction) is { } race)
            {
                (race == CraftRace.Dark ? DarkSide : LightSide).IsChecked = true;
            }
        }

        if (ProductList.ItemsSource is null)
        {
            Search(SearchBox.Text);
        }

        Calculate();
        ShowLog();
    }

    private void Side_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        // The same item on the other side (the gear has its own ids per side; materials are shared)
        string? name = _product?.Name;
        FillBrowse();
        Search(SearchBox.Text);
        if (name is not null && (ProductList.ItemsSource as IEnumerable<CraftProductLine>)?.FirstOrDefault(l => l.Name == name) is { } same)
        {
            ProductList.SelectedItem = same;
        }
        else
        {
            Calculate();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Search(SearchBox.Text);
    }

    /// <summary>The profession and item type pickers, "All …" first; keeps what was picked.</summary>
    private void FillBrowse()
    {
        _filling = true;
        string? profession = Profession, category = Category;
        ProfessionBox.ItemsSource = _book.Professions(Side).Prepend(AllProfessions).ToList();
        CategoryBox.ItemsSource = _book.Categories(Side).Prepend(AllCategories).ToList();
        ProfessionBox.SelectedItem = profession ?? AllProfessions;
        CategoryBox.SelectedItem = category ?? AllCategories;
        ProfessionBox.SelectedIndex = Math.Max(0, ProfessionBox.SelectedIndex);
        CategoryBox.SelectedIndex = Math.Max(0, CategoryBox.SelectedIndex);
        _filling = false;
    }

    private string? Profession => ProfessionBox.SelectedItem is string p && p != AllProfessions ? p : null;

    private string? Category => CategoryBox.SelectedItem is string c && c != AllCategories ? c : null;

    private void Browse_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && IsLoaded)
        {
            Search(SearchBox.Text);
        }
    }

    /// <summary>Shows one profession and item type (render shots); null for all.</summary>
    internal void Browse(string? profession, string? category)
    {
        ProfessionBox.SelectedItem = profession ?? AllProfessions;
        CategoryBox.SelectedItem = category ?? AllCategories;
        Search(SearchBox.Text);
    }

    private void Search(string query)
    {
        var matches = _book.Find(Side, query, Profession, Category);
        ProductList.ItemsSource = matches.Take(MaxResults)
            .Select(p => new CraftProductLine(p, p.Name, Detail(p), ItemGrades.Brush(p.Grade ?? string.Empty)))
            .ToList();
        ResultCount.Text = matches.Count > MaxResults ? $"{MaxResults} of {matches.Count:N0} items, type to narrow down"
            : matches.Count == 1 ? "1 item" : $"{matches.Count:N0} items";
    }

    private void ProductList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProductList.SelectedItem is not CraftProductLine line)
        {
            return;
        }

        _product = line.Product;
        ProductName.Text = line.Name;
        ProductName.Foreground = line.Brush;
        Controls.IsEnabled = true;
        Calculate();
    }

    private void QuantityBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            Calculate();
        }
    }

    private void Calculate()
    {
        if (_product is not { } product || _state is null || _book.Best(product.ItemId, Side) is not { } recipe)
        {
            Results.Visibility = Visibility.Collapsed;
            return;
        }

        ProductMeta.Text = Meta(recipe);
        ComboText.Text = _book.ComboLine(recipe) ?? string.Empty;
        ComboText.Visibility = ComboText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var inventory = _state.ViewedInventory;
        BagLine.Text = inventory is { IsKnown: true, Character: { } owner }
            ? $"Bag and warehouse of {StreamerMode.Name(owner)}, as last seen"
            : "Your bag isn't known yet: enter the world once with the meter running.";

        if (!ulong.TryParse(QuantityBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong quantity) || quantity is 0 or > 100_000)
        {
            QuantityNote.Text = "Enter how many to make";
            Results.Visibility = Visibility.Collapsed;
            return;
        }

        if (CraftPlanner.Plan(_book, product.ItemId, quantity, Side, CraftPlanner.Bag(inventory, _book), Bought) is not { } plan)
        {
            Results.Visibility = Visibility.Collapsed;
            return;
        }

        QuantityNote.Text = plan.Root.Crafts == quantity ? string.Empty
            : plan.Root.Crafts == 1 ? $"1 craft of {recipe.Yield}" : $"{N(plan.Root.Crafts)} crafts of {recipe.Yield}";
        var missing = plan.Materials.Where(m => m.Missing > 0).ToList();
        string crafts = plan.Crafts == 1 ? "1 craft in all" : $"{N(plan.Crafts)} crafts in all";
        Summary.Text = inventory is not { IsKnown: true } ? crafts
            : missing.Count == 0 ? $"Your bag holds everything: {crafts}"
            : missing.Count == 1 ? $"1 material missing · {crafts}"
            : $"{missing.Count} materials missing · {crafts}";
        KinahLine.Text = plan.KinahFee == 0
            ? "No Kinah fee: no recipe charges one in the game data, and no Kinah left the bag in any craft we recorded."
            : $"Kinah fee: {N(plan.KinahFee)}";

        var muted = Brush("MutedBrush");
        var warn = Brush("WarnBrush", "HighlightBrush");
        MaterialList.ItemsSource = plan.Materials.Select(m => new CraftMaterialRow(m.Name, ItemBrush(m.ItemId), N(m.Need), N(m.FromBag),
            m.Missing == 0 ? "—" : N(m.Missing), m.Missing == 0 ? muted : warn)).ToList();
        StepList.ItemsSource = plan.Flat.Select(n => new CraftStepRow($"{N(n.Need)}×", n.Name, ItemBrush(n.ItemId), How(n),
            new Thickness(n.Depth * 18, 0, 0, 0), n.ItemId, n.Bought, n.Craftable ? Visibility.Visible : Visibility.Collapsed,
            n is { Depth: > 0, Crafts: > 0, Recipe: { } own } ? _book.ComboLine(own) : null)).ToList();
        Results.Visibility = Visibility.Visible;
    }

    /// <summary>The materials bought or gathered instead of crafted (remembered per item).</summary>
    private HashSet<uint> Bought => _state?.Stored.CraftBuyInstead.ToHashSet() ?? [];

    /// <summary>The "buy / gather" toggle of a material in the steps: stop or go back to breaking it down.</summary>
    private void Buy_Click(object sender, RoutedEventArgs e)
    {
        if (_state is null || sender is not CheckBox { Tag: uint item } box)
        {
            return;
        }

        var list = _state.Stored.CraftBuyInstead;
        list.Remove(item);
        if (box.IsChecked == true)
        {
            list.Add(item);
        }

        _state.SaveSettings();
        Calculate();
    }

    /// <summary>"Alchemy · Materials · 3 per craft" (the combo has its own line).</summary>
    private static string Meta(Recipe recipe)
    {
        var parts = new List<string> { recipe.Profession, recipe.Category };
        if (recipe.Yield > 1)
        {
            parts.Add($"{recipe.Yield} per craft");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>"Alchemy · Materials", with " · 25 % combo" when its recipe has one.</summary>
    private string Detail(CraftProduct product) =>
        $"{product.Profession} · {product.Category}" +
        (_book.Best(product.ItemId, Side) is { ComboPercent: > 0, ComboProduct: not null } recipe ? $" · {recipe.ComboPercent} % combo" : string.Empty);

    private static string How(CraftNode node) => node switch
    {
        { Bought: true, Missing: 0 } => "bought / gathered: from the bag",
        { Bought: true, FromBag: > 0 } => $"bought / gathered: {N(node.FromBag)} from the bag, {N(node.Missing)} to get",
        { Bought: true } => $"bought / gathered: {N(node.Missing)} to get",
        { Crafts: > 0, FromBag: > 0 } => $"{N(node.FromBag)} from the bag, craft {N(node.Crafts)}×",
        { Crafts: > 0 } => $"craft {N(node.Crafts)}×",
        { Depth: 0 } => string.Empty,
        { Missing: 0 } => "from the bag",
        { FromBag: > 0 } => $"{N(node.FromBag)} from the bag, {N(node.Missing)} missing",
        _ => $"{N(node.Missing)} missing",
    };

    private void ShowLog()
    {
        if (_state is null)
        {
            return;
        }

        var crafts = _state.Crafts.Viewed;
        LogCard.Visibility = crafts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        LogWho.Text = _state.ViewedCharacter is { } who ? StreamerMode.Name(who) : string.Empty;
        LogList.ItemsSource = crafts.Take(30).Select(c => CraftText.Line(c, _book, BundledGameData.ItemName)).ToList();
    }

    private Brush ItemBrush(uint item) => ItemGrades.Brush(_book.Item(item)?.Grade ?? string.Empty);

    private static Brush Brush(string key, string? fallback = null) =>
        Application.Current.TryFindResource(key) as Brush ??
        (fallback is null ? null : Application.Current.TryFindResource(fallback) as Brush) ?? Brushes.Gray;

    private static string N(ulong value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
