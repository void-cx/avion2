using System.Text.Json.Nodes;
using ExtractGameData.Core;

namespace ExtractGameData;

/// <summary>
/// recipes.json from CraftRecipe (+ CraftComponentReplacement, Item and the English texts): every recipe the game
/// lists (<c>bShowRecipe</c>). The combo recipes (<c>LearnType Combo</c>, no materials of their own) are what a combo
/// makes instead and appear as the listed recipe's combo product.
/// </summary>
public static class RecipesBuilder
{
    // ECraftSubCategory → the item types the Crafting panel groups by
    private static readonly Dictionary<string, string> Categories = new(StringComparer.Ordinal)
    {
        ["Weapon"] = "Weapons", ["Armor"] = "Armor", ["Accessory"] = "Accessories", ["Usable"] = "Consumables", ["Misc"] = "Materials",
    };

    public static IEnumerable<AssetFile> Build(GameTables tables, ItemIndex items, ToolConfig config, Report report)
    {
        var all = tables.Rows("CraftRecipe");
        var byName = all.ToDictionary(r => r.Str("Name"), StringComparer.Ordinal);
        // Stand-ins per side: "Replace_Craft_L_Gather_Od_A_c_01" lists what may replace Material_Gather_Od_A_c_01_f.
        // (The "Replace_<recipe>_componentNN" rows belong to one recipe's component and are not used here.)
        var standIns = new Dictionary<string, List<uint>>(StringComparer.Ordinal);
        foreach (var row in tables.Rows("CraftComponentReplacement"))
        {
            string name = row.Str("Name");
            if (name.StartsWith("Replace_Craft_", StringComparison.Ordinal) && name.Length > 16)
            {
                string material = name[16..]; // after "Replace_Craft_L_"
                var list = standIns.TryGetValue(material, out var l) ? l : standIns[material] = [];
                list.AddRange(row.Objects("ReplacementItemList").Select(i => items.Id(i.Str("Item"))));
            }
        }

        var byEnglishName = items.Rows.Where(r => items.Name(r) is not null).GroupBy(r => items.Name(r)!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Id()).ToList(), StringComparer.Ordinal);

        var recipes = new JsonArray();
        var used = new SortedSet<uint>();
        var products = new HashSet<uint>();
        foreach (var r in all.Where(r => r.Bool("bShowRecipe")))
        {
            uint id = r.Id();
            string race = r.EnumValue("QualificationRace");
            if (race is not ("Light" or "Dark"))
            {
                throw new GameDataFormatException($"recipe {id}: side {race}");
            }

            string sub = r.EnumValue("SubCategory");
            uint product = items.Id(r.Str("Product"));
            var materials = new JsonArray();
            foreach (var c in r.Objects("ComponentList"))
            {
                uint material = items.Id(c.Str("Item"));
                materials.Add(new JsonArray(material, c.Long("Count")));
                used.Add(material);
            }

            var entry = new JsonObject
            {
                ["id"] = id,
                ["p"] = product,
                ["y"] = r.Long("ProductCount"),
                ["prof"] = r.EnumValue("Category"),
                ["race"] = race,
                ["cat"] = Categories.TryGetValue(sub, out var cat) ? cat : throw new GameDataFormatException($"recipe {id}: sub-category {sub}"),
                ["fee"] = r.Long("CraftingFee"),
                ["m"] = materials,
            };

            if (r.Str("ComboRecipe") is not "None" and var comboName)
            {
                // ComboProbability is in 1/10000 (2500 = 25 %)
                long chance = r.Long("ComboProbability");
                if (chance <= 0 || chance % 100 != 0)
                {
                    throw new GameDataFormatException($"recipe {id}: combo chance {chance} is not a whole percent");
                }

                var combo = byName.TryGetValue(comboName, out var c) ? c : throw new GameDataFormatException($"recipe {id}: no combo recipe {comboName}");
                uint comboProduct = items.Id(combo.Str("Product"));
                entry["combo"] = new JsonArray(comboProduct, chance / 100);
                used.Add(comboProduct);
            }

            recipes.Add(entry);
            used.Add(product);
        }

        // made by some recipe (a listed one, a hidden one or a combo): not a base material
        foreach (var r in all)
        {
            products.Add(items.Id(r.Str("Product")));
        }

        var itemTable = new JsonObject();
        foreach (uint id in used)
        {
            var row = items.ById(id)!;
            itemTable[id.ToString()] = new JsonObject
            {
                ["n"] = items.Name(row) ?? throw new GameDataFormatException($"item {id} has no English name"),
                ["g"] = items.Grade(row),
                ["b"] = products.Contains(id) ? 0 : 1,
            };
        }

        // A material's bound variants, which recipes take alike: the side stand-ins of CraftComponentReplacement and the
        // items named "<material> (Bound)"
        var boundTable = new JsonObject();
        foreach (uint id in used)
        {
            string key = items.ById(id)!.Str("Name");
            var variants = new SortedSet<uint>();
            string bare = key.StartsWith("Material_", StringComparison.Ordinal) ? key["Material_".Length..] : key;
            bare = bare.EndsWith("_f", StringComparison.Ordinal) ? bare[..^2] : bare;
            variants.UnionWith(standIns.GetValueOrDefault(bare) ?? []);
            if (items.Name(id) is { } name)
            {
                variants.UnionWith(byEnglishName.GetValueOrDefault(name + " (Bound)") ?? []);
            }

            variants.Remove(id);
            if (variants.Count == 0)
            {
                continue;
            }

            boundTable[id.ToString()] = new JsonArray(variants.Select(v => (JsonNode) v).ToArray());
            foreach (uint v in variants)
            {
                if (!itemTable.ContainsKey(v.ToString()) && items.ById(v) is { } row)
                {
                    itemTable[v.ToString()] = new JsonObject { ["n"] = items.Name(row), ["g"] = items.Grade(row), ["b"] = 1 };
                }
            }
        }

        var file = new JsonObject
        {
            ["source"] = "AION2 client tables CraftRecipe, CraftComponentReplacement, Item and the English texts (tools/extract_gamedata)",
            ["built"] = tables.ClientDate.ToString("yyyy-MM-dd"),
            ["items"] = itemTable,
            ["bound"] = boundTable,
            ["recipes"] = recipes,
        };

        var old = Json.ReadAsset(Path.Combine(config.AssetsDir, "recipes.json"));
        report.Compare("recipes.json recipes", ById(old?["recipes"]), ById(recipes)!, Summary);
        report.Compare("recipes.json items", Report.Entries(old?["items"]), Report.Entries(itemTable)!, s => s);
        report.Compare("recipes.json bound", Report.Entries(old?["bound"]), Report.Entries(boundTable)!, s => s);
        int dragon = recipes.Count(r => items.Name((uint) r!["p"]!) is { } n && (n.Contains("True Dragon") || n.Contains("Star Dragon"))) +
                     recipes.Count(r => r!["combo"] is JsonArray c && items.Name((uint) c[0]!) is { } n && (n.Contains("True Dragon") || n.Contains("Star Dragon")));
        report.Add("recipes.json", $"{recipes.Count} recipes ({all.Count} in CraftRecipe; {all.Count(r => r.EnumValue("LearnType") == "Combo")} are combo outcomes), " +
                                   $"{itemTable.Count} items, {boundTable.Count} materials with stand-ins; True/Star Dragon products and combo products: {dragon}");
        yield return new AssetFile("recipes.json", Json.Compact(file));
    }

    private static Dictionary<string, string>? ById(JsonNode? recipes) =>
        recipes is JsonArray a ? a.ToDictionary(r => r!["id"]!.ToString(), r => Json.Compact(r!), StringComparer.Ordinal) : null;

    private static string Summary(string json) => json.Length > 160 ? json[..160] + "…" : json;
}

/// <summary>
/// stat_rates.json from PcStatSecond: per attribute and deity stat, what one point gives (StatValue / 100 = percent),
/// with the game's English stat names (<c>StatName_&lt;stat&gt;</c>). Fails unless every row is the per-point value
/// times the points.
/// </summary>
public static class StatRatesBuilder
{
    private static readonly (string Key, string Column, string Group)[] Inputs =
    [
        ("STR", "StrStatList", "primary"), ("DEX", "DexStatList", "primary"), ("INT", "IntStatList", "primary"),
        ("CON", "ConStatList", "primary"), ("AGI", "AgiStatList", "primary"), ("WIS", "WisStatList", "primary"),
        ("Justice", "JusticeStatList", "deity"), ("Destruction", "DestructionStatList", "deity"), ("Freedom", "FreedomStatList", "deity"),
        ("Death", "DeathStatList", "deity"), ("Illusion", "IllusionStatList", "deity"), ("Wisdom", "WisdomStatList", "deity"),
        ("Life", "LifeStatList", "deity"), ("Destiny", "DestinyStatList", "deity"), ("Time", "TimeStatList", "deity"), ("Space", "SpaceStatList", "deity"),
    ];

    public static IEnumerable<AssetFile> Build(GameTables tables, ToolConfig config, Report report)
    {
        var rows = tables.Rows("PcStatSecond");
        var first = rows.Single(r => r.Long("Stat") == 1);
        string Name(string stat) => tables.Text("en-US", "StatName_" + stat) ?? throw new GameDataFormatException($"no English name for stat {stat}");

        var lines = new List<string>();
        foreach (var (key, column, group) in Inputs)
        {
            var perPoint = first.Objects(column).Select(e => (Stat: e.EnumValue("StatType"), Value: e.Long("StatValue"))).ToList();
            foreach (var row in rows)
            {
                long points = row.Long("Stat");
                var values = row.Objects(column).Select(e => (Stat: e.EnumValue("StatType"), Value: e.Long("StatValue"))).ToList();
                if (!values.SequenceEqual(perPoint.Select(p => (p.Stat, p.Value * points))))
                {
                    throw new GameDataFormatException($"PcStatSecond {column} at {points} points is not linear");
                }
            }

            var gives = new JsonArray(perPoint.Select(p => (JsonNode) new JsonObject
            {
                ["stat"] = p.Stat, ["name"] = Name(p.Stat), ["per"] = Math.Round(p.Value / 100.0, 6),
            }).ToArray());
            lines.Add(Json.Compact(new JsonObject { ["key"] = key, ["name"] = Name(key), ["group"] = group, ["gives"] = gives }));
        }

        string built = tables.ClientDate.ToString("yyyy-MM-dd");
        string content = $"{{\"built\":\"{built}\",\"unit\":\"percent per point\",\"inputs\":[\n{string.Join(",\n", lines)}\n]}}\n";

        var old = Json.ReadAsset(Path.Combine(config.AssetsDir, "stat_rates.json"));
        var oldInputs = (old?["inputs"] as JsonArray)?.ToDictionary(i => (string) i!["key"]!, i => Json.Compact(i!), StringComparer.Ordinal);
        var newInputs = JsonNode.Parse(content)!["inputs"]!.AsArray().ToDictionary(i => (string) i!["key"]!, i => Json.Compact(i!), StringComparer.Ordinal);
        report.Compare("stat_rates.json", oldInputs, newInputs, s => s);
        report.Add("stat_rates.json", $"{rows.Count} PcStatSecond rows, linear in every column");
        yield return new AssetFile("stat_rates.json", content);
    }
}
