using System.Text.Json;
using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>The side a recipe belongs to: the game keeps separate recipes (and gear) for Elyos and Asmodians.</summary>
public enum CraftRace
{
    Light,
    Dark,
}

/// <summary>An item a recipe makes or uses.</summary>
/// <param name="IsBase">A base material: gathered, dropped or bought, never crafted.</param>
public sealed record CraftItem(uint Id, string Name, string? Grade, bool IsBase);

/// <summary>One material of a recipe, per craft.</summary>
public sealed record RecipeMaterial(uint ItemId, uint Count);

/// <summary>
/// A crafting recipe (its id is the one <c>6F 8D</c> names). One craft makes <paramref name="Yield"/> of the product;
/// a combo (<paramref name="ComboPercent"/> % of the crafts) makes the better <paramref name="ComboProduct"/> instead.
/// </summary>
/// <param name="Fee">Kinah per craft (0 in every recipe of the game data, and no Kinah left the bag in any of our crafts).</param>
public sealed record Recipe(uint Id, uint Product, uint Yield, string Profession, CraftRace Race, string Category, ulong Fee,
    IReadOnlyList<RecipeMaterial> Materials, uint? ComboProduct, int ComboPercent);

/// <summary>A craftable item as the calculator lists it.</summary>
public sealed record CraftProduct(uint ItemId, string Name, string? Grade, string Profession, string Category);

/// <summary>
/// The game's crafting recipes (assets/gamedata/recipes.json, see PROVENANCE.md): what each makes and uses, and the
/// bound variants of the materials, which the game takes in their place ("Odyle (Bound)" for "Odyle").
/// </summary>
public sealed class RecipeBook
{
    private readonly Dictionary<uint, CraftItem> _items = new();
    private readonly Dictionary<uint, Recipe> _byId = new();
    private readonly Dictionary<uint, List<Recipe>> _byProduct = new();
    private readonly Dictionary<uint, IReadOnlyList<uint>> _bound = new();

    public IReadOnlyCollection<Recipe> Recipes => _byId.Values;

    public static RecipeBook Parse(string? json)
    {
        var book = new RecipeBook();
        if (json is null)
        {
            return book;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        foreach (var item in root.GetProperty("items").EnumerateObject())
        {
            if (uint.TryParse(item.Name, out uint id))
            {
                var v = item.Value;
                book._items[id] = new CraftItem(id, v.GetProperty("n").GetString() ?? $"Item {id}",
                    v.TryGetProperty("g", out var g) ? g.GetString() : null, v.TryGetProperty("b", out var b) && b.GetInt32() == 1);
            }
        }

        if (root.TryGetProperty("bound", out var bound))
        {
            foreach (var entry in bound.EnumerateObject())
            {
                if (uint.TryParse(entry.Name, out uint id))
                {
                    book._bound[id] = entry.Value.EnumerateArray().Select(v => v.GetUInt32()).ToList();
                }
            }
        }

        foreach (var r in root.GetProperty("recipes").EnumerateArray())
        {
            uint? combo = null;
            int pct = 0;
            if (r.TryGetProperty("combo", out var c) && c.ValueKind == JsonValueKind.Array)
            {
                combo = c[0].GetUInt32();
                pct = c[1].GetInt32();
            }

            var recipe = new Recipe(r.GetProperty("id").GetUInt32(), r.GetProperty("p").GetUInt32(), Math.Max(1, r.GetProperty("y").GetUInt32()),
                r.GetProperty("prof").GetString() ?? "?", r.GetProperty("race").GetString() == "Dark" ? CraftRace.Dark : CraftRace.Light,
                r.GetProperty("cat").GetString() ?? "Materials", r.TryGetProperty("fee", out var fee) ? fee.GetUInt64() : 0,
                r.GetProperty("m").EnumerateArray().Select(m => new RecipeMaterial(m[0].GetUInt32(), m[1].GetUInt32())).ToList(), combo, pct);
            book._byId[recipe.Id] = recipe;
            if (!book._byProduct.TryGetValue(recipe.Product, out var list))
            {
                book._byProduct[recipe.Product] = list = [];
            }

            list.Add(recipe);
        }

        return book;
    }

    public CraftItem? Item(uint id) => _items.GetValueOrDefault(id);

    /// <summary>The item's name from the recipe data, else from <paramref name="fallback"/>, else "Item 123".</summary>
    public string Name(uint id, Func<uint, string?>? fallback = null) => Item(id)?.Name ?? fallback?.Invoke(id) ?? $"Item {id}";

    public Recipe? ById(uint recipeId) => _byId.GetValueOrDefault(recipeId);

    /// <summary>The item and its bound variants, which a recipe takes alike.</summary>
    public IEnumerable<uint> WithVariants(uint id) => _bound.TryGetValue(id, out var variants) ? variants.Prepend(id) : [id];

    /// <summary>The recipe to plan an item with: one of the side asked for if there is one, the smallest batch first.</summary>
    public Recipe? Best(uint product, CraftRace race) =>
        _byProduct.TryGetValue(product, out var recipes)
            ? recipes.OrderBy(r => r.Race == race ? 0 : 1).ThenBy(r => r.Yield).ThenBy(r => r.Id).First()
            : null;

    /// <summary>Everything one side can craft, by name.</summary>
    public IReadOnlyList<CraftProduct> Products(CraftRace race) => _byId.Values.Where(r => r.Race == race)
        .GroupBy(r => r.Product)
        .Select(g => new CraftProduct(g.Key, Name(g.Key), Item(g.Key)?.Grade, g.First().Profession, g.First().Category))
        .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.ItemId)
        .ToList();

    /// <summary>The professions one side crafts with, by name.</summary>
    public IReadOnlyList<string> Professions(CraftRace race) =>
        _byId.Values.Where(r => r.Race == race).Select(r => r.Profession).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The item types (categories) one side crafts, by name.</summary>
    public IReadOnlyList<string> Categories(CraftRace race) =>
        _byId.Values.Where(r => r.Race == race).Select(r => r.Category).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// What one side can craft, narrowed to a profession and an item type (null = all) and to the words of
    /// <paramref name="query"/> (each in the name, profession, type or grade).
    /// </summary>
    public IReadOnlyList<CraftProduct> Find(CraftRace race, string? query, string? profession = null, string? category = null)
    {
        var words = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Products(race)
            .Where(p => profession is null || string.Equals(p.Profession, profession, StringComparison.OrdinalIgnoreCase))
            .Where(p => category is null || string.Equals(p.Category, category, StringComparison.OrdinalIgnoreCase))
            .Where(p => words.All(w => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                                       p.Profession.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                                       p.Category.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                                       (p.Grade?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)))
            .ToList();
    }

    /// <summary>
    /// A recipe's combo in words: "25 % combo: Splendent Truth Orb", "30 % combo: two at once" (the same item);
    /// null when it has none.
    /// </summary>
    public string? ComboLine(Recipe recipe) =>
        recipe.ComboProduct is { } combo && recipe.ComboPercent > 0
            ? $"{recipe.ComboPercent} % combo: {(combo == recipe.Product ? "two at once" : Name(combo))}"
            : null;

    /// <summary>The side of a server faction ("Elyos" → Light, "Asmodian" → Dark); null when unknown.</summary>
    public static CraftRace? RaceOf(string? faction) => faction switch
    {
        "Elyos" => CraftRace.Light,
        "Asmodian" or "Asmodians" => CraftRace.Dark,
        _ => null,
    };
}

/// <summary>One item of a crafting plan: how many it takes, how many the bag covers, and how it is made.</summary>
/// <param name="FromBag">Of <paramref name="Need"/>, how many the bag covers (each item counted once across the plan).</param>
/// <param name="Missing">What neither the bag nor a recipe covers (a base material to gather or buy).</param>
/// <param name="Crafts">How often its recipe runs for the rest (0 when the bag covers it or it has no recipe).</param>
/// <param name="Bought">A craftable material the player buys or gathers instead: counted as a base material.</param>
public sealed record CraftNode(uint ItemId, string Name, ulong Need, ulong FromBag, ulong Missing, ulong Crafts, Recipe? Recipe, int Depth,
    IReadOnlyList<CraftNode> Children, bool Bought = false)
{
    /// <summary>A material with a recipe of its own (the "buy or gather instead" toggle applies to it).</summary>
    public bool Craftable => Depth > 0 && (Recipe is not null || Bought);
}

/// <summary>A base material of a plan, added up over the whole tree.</summary>
public sealed record CraftMaterialLine(uint ItemId, string Name, ulong Need, ulong FromBag, ulong Missing);

/// <summary>What making an item takes: the tree down to the base materials, their totals, and the Kinah fee.</summary>
public sealed record CraftPlan(CraftNode Root, IReadOnlyList<CraftMaterialLine> Materials, ulong KinahFee, ulong Crafts)
{
    /// <summary>The tree in order, each item after the one it goes into.</summary>
    public IEnumerable<CraftNode> Flat => Walk(Root);

    public bool HasEverything => Materials.All(m => m.Missing == 0);

    private static IEnumerable<CraftNode> Walk(CraftNode node) => node.Children.SelectMany(Walk).Prepend(node);
}

/// <summary>Plans a craft from the recipes and the bag.</summary>
public static class CraftPlanner
{
    private const int MaxDepth = 10;

    /// <summary>
    /// Plans <paramref name="quantity"/> of <paramref name="product"/>: each material is taken from the bag first
    /// (intermediates too), the rest crafted from its own recipe, down to the base materials; null when nothing makes
    /// the product. Combos are not counted on (they are a chance).
    /// </summary>
    /// <param name="have">How many of an item (its bound variants included) the bag holds.</param>
    /// <param name="buy">Craftable materials not to break down: bought or gathered, they count as base materials.</param>
    public static CraftPlan? Plan(RecipeBook book, uint product, ulong quantity, CraftRace race, Func<uint, ulong> have,
        IReadOnlySet<uint>? buy = null)
    {
        if (quantity == 0 || book.Best(product, race) is not { } top)
        {
            return null;
        }

        var pool = new Dictionary<uint, ulong>();
        ulong fee = 0, crafts = 0;

        ulong Take(uint item, ulong need)
        {
            if (!pool.TryGetValue(item, out ulong left))
            {
                left = have(item);
            }

            ulong taken = Math.Min(left, need);
            pool[item] = left - taken;
            return taken;
        }

        CraftNode Make(uint item, ulong need, ulong fromBag, int depth, ImmutableStack path)
        {
            ulong rest = need - fromBag;
            var recipe = book.Item(item)?.IsBase == true || depth >= MaxDepth || path.Contains(item) ? null : book.Best(item, race);
            if (depth > 0 && recipe is not null && buy?.Contains(item) == true)
            {
                return new CraftNode(item, book.Name(item), need, fromBag, rest, 0, null, depth, [], Bought: true);
            }

            if (rest == 0 || recipe is null)
            {
                return new CraftNode(item, book.Name(item), need, fromBag, rest, 0, recipe, depth, []);
            }

            ulong runs = (rest + recipe.Yield - 1) / recipe.Yield;
            fee += recipe.Fee * runs;
            crafts += runs;
            var inner = path.Push(item);
            var children = recipe.Materials
                .Select(m => (m.ItemId, Need: m.Count * runs))
                .Select(m => Make(m.ItemId, m.Need, Take(m.ItemId, m.Need), depth + 1, inner))
                .ToList();
            return new CraftNode(item, book.Name(item), need, fromBag, 0, runs, recipe, depth, children);
        }

        var root = Make(top.Product, quantity, 0, 0, ImmutableStack.Empty);
        var materials = new CraftPlan(root, [], fee, crafts).Flat
            .Where(n => n.Recipe is null && n.Depth > 0)
            .GroupBy(n => n.ItemId)
            .Select(g => new CraftMaterialLine(g.Key, g.First().Name, Sum(g, n => n.Need), Sum(g, n => n.FromBag), Sum(g, n => n.Missing)))
            .OrderByDescending(m => m.Missing > 0).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new CraftPlan(root, materials, fee, crafts);
    }

    /// <summary>How many of an item, its bound variants included, the inventory holds outside the worn gear (0 when unknown).</summary>
    public static Func<uint, ulong> Bag(InventoryState? inventory, RecipeBook book)
    {
        if (inventory is not { IsKnown: true })
        {
            return _ => 0;
        }

        var counts = inventory.Items.Where(i => i.Container != InventoryItem.Equipped)
            .GroupBy(i => i.ItemId).ToDictionary(g => g.Key, g => g.Aggregate(0UL, (sum, i) => sum + i.Count));
        return item => book.WithVariants(item).Aggregate(0UL, (sum, id) => sum + counts.GetValueOrDefault(id));
    }

    private static ulong Sum(IEnumerable<CraftNode> nodes, Func<CraftNode, ulong> value) => nodes.Aggregate(0UL, (sum, n) => sum + value(n));

    /// <summary>The items above the one being planned, to stop a recipe that would use its own product.</summary>
    private sealed class ImmutableStack
    {
        public static readonly ImmutableStack Empty = new(null, 0);

        private readonly ImmutableStack? _parent;
        private readonly uint _item;

        private ImmutableStack(ImmutableStack? parent, uint item) => (_parent, _item) = (parent, item);

        public ImmutableStack Push(uint item) => new(this, item);

        public bool Contains(uint item)
        {
            for (var s = this; s is { _parent: not null }; s = s._parent)
            {
                if (s._item == item)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
