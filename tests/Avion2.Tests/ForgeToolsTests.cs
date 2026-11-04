using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Tests;

/// <summary>The Gear Forge's tools: the stat calculator (bundled rates, conversion, prefill) and the crafting additions.</summary>
public class ForgeToolsTests
{
    // ---- Stat calculator ----

    [Fact]
    public void BundledRatesHaveTheSixteenInputs()
    {
        var rates = StatRates.Bundled;
        Assert.Equal(16, rates.Inputs.Count);
        Assert.Equal(["STR", "DEX", "INT", "CON", "AGI", "WIS"], rates.Inputs.Where(i => !i.IsDeity).Select(i => i.Key));
        Assert.Equal(["Justice", "Destruction", "Freedom", "Death", "Illusion", "Wisdom", "Life", "Destiny", "Time", "Space"],
            rates.Inputs.Where(i => i.IsDeity).Select(i => i.Key));
        Assert.All(rates.Inputs, i => Assert.NotEmpty(i.Gives));

        // The game's names, as the official site's stat list words them
        Assert.Equal(["Might", "Dexterity", "Intelligence", "Constitution", "Precision", "Willpower"],
            rates.Inputs.Where(i => !i.IsDeity).Select(i => i.Name));
        var wisdom = rates.Input("Wisdom")!;
        Assert.Equal(("Wisdom", "Lumiel"), (wisdom.ShortName, wisdom.Lord));
        Assert.Null(rates.Input("STR")!.Lord);
        Assert.Equal([("MPUseIncrease", "MP Cost", -0.1), ("HardHit", "Double Chance", 0.1)], wisdom.Gives.Select(g => (g.Stat, g.Name, g.PerPoint)));
        Assert.Equal(["CombatSpeed", "HardHitResist"], rates.Input("Time")!.Gives.Select(g => g.Stat));
        Assert.Null(rates.Input("Light")); // no conversion in the game data
        Assert.NotNull(rates.Built);
    }

    /// <summary>Two inputs that both raise Attack, one that lowers MP cost.</summary>
    private static StatRates Table() => StatRates.Parse("""
        {"built":"2026-09-19","unit":"percent per point","inputs":[
        {"key":"STR","name":"Might","group":"primary","gives":[{"stat":"DamageRatio","name":"Attack increase","per":0.1}]},
        {"key":"Destruction","name":"Destruction [Zikel]","group":"deity","gives":[{"stat":"DamageRatio","name":"Attack increase","per":0.1},{"stat":"PerfectResist","name":"Perfect Resist","per":0.1}]},
        {"key":"Wisdom","name":"Wisdom [Lumiel]","group":"deity","gives":[{"stat":"MPUseIncrease","name":"MP Cost","per":-0.1},{"stat":"HardHit","name":"Double Chance","per":0.1}]}
        ]}
        """);

    [Fact]
    public void ConversionAddsUpEveryInput()
    {
        var rates = Table();
        var derived = rates.Convert(new Dictionary<string, int> { ["STR"] = 25, ["Destruction"] = 4, ["Wisdom"] = 178 });
        Assert.Equal([("DamageRatio", 2.9), ("PerfectResist", 0.4), ("MPUseIncrease", -17.8), ("HardHit", 17.8)],
            derived.Select(d => (d.Stat, d.Value)));
        Assert.Empty(rates.Convert(new Dictionary<string, int>()));
        Assert.Equal(["Double Chance +17.8%", "MP Cost −17.8%"],
            rates.Gives("Wisdom", 178).Select(g => $"{g.Name} {StatRates.Percent(g.Value)}").Reverse());
        Assert.Empty(rates.Gives("Wisdom", 0));
        Assert.Empty(rates.Gives("Light", 10));

        // Linear: what a change gives is the change times the rate
        Assert.Equal("+20 Wisdom → MP Cost −2%, Double Chance +2%", rates.ChangeLine("Wisdom", 178, 198));
        Assert.Equal("−5 Might → Attack increase −0.5%", rates.ChangeLine("STR", 25, 20));
        Assert.Null(rates.ChangeLine("STR", 25, 25));
        Assert.Equal(("+0.1%", "0%", "−2%"), (StatRates.Percent(0.1), StatRates.Percent(0), StatRates.Percent(-2.0000001)));
    }

    [Fact]
    public void ComparisonShowsTheDifferenceToTheCharacter()
    {
        var rates = Table();
        var mine = new Dictionary<string, int> { ["STR"] = 25, ["Wisdom"] = 178 };
        var entered = new Dictionary<string, int> { ["STR"] = 25, ["Destruction"] = 10, ["Wisdom"] = 198 };
        var lines = rates.Compare(entered, mine).ToDictionary(l => l.Stat);
        Assert.Equal((3.5, 2.5, 1.0), (lines["DamageRatio"].Value, lines["DamageRatio"].Baseline, lines["DamageRatio"].Difference));
        Assert.Equal(1.0, lines["PerfectResist"].Difference); // new: only the entered values give it
        Assert.Equal(-2.0, lines["MPUseIncrease"].Difference);
        Assert.Equal(2.0, lines["HardHit"].Difference);

        // A stat only the character's values give stays in, at 0
        var less = rates.Compare(new Dictionary<string, int> { ["STR"] = 25 }, mine).ToDictionary(l => l.Stat);
        Assert.Equal((0.0, 17.8, -17.8), (less["HardHit"].Value, less["HardHit"].Baseline, less["HardHit"].Difference));
    }

    [Fact]
    public void PrefillTakesTheOfficialSitesStatList()
    {
        // As ArmoryClient reads it: the type is the game's key, item level and Light have no conversion
        var stats = new List<ArmoryStat>
        {
            new("STR", "Might", 15, ["Attack increase +1.5%"]),
            new("WIS", "Willpower", 11, ["Status Effect Resist +1.1%"]),
            new("Justice", "Justice [Nezekan]", 39, ["Defense increase +3.9%", "Perfect Chance +3.9%"]),
            new("Wisdom", "Wisdom [Lumiel]", 2, ["MP Cost -0.2%", "Double Chance +0.2%"]),
            new("Light", "Light [Ariel]", 7, []),
            new("", "Time [Siel]", 39, ["Combat Speed +3.9%", "Double Resist +3.9%"]), // by name when the type is missing
        };
        var rates = StatRates.Bundled;
        var values = rates.FromArmory(stats);
        Assert.Equal(new Dictionary<string, int> { ["STR"] = 15, ["WIS"] = 11, ["Justice"] = 39, ["Wisdom"] = 2, ["Time"] = 39 },
            values.ToDictionary(v => v.Key, v => v.Value));

        // The conversion matches what the site shows for the same values
        var derived = rates.Convert(values).ToDictionary(d => d.Name, d => StatRates.Percent(d.Value));
        Assert.Equal("+1.5%", derived["Attack increase"]);
        Assert.Equal("+3.9%", derived["Defense increase"]);
        Assert.Equal("+3.9%", derived["Perfect Chance"]);
        Assert.Equal("−0.2%", derived["MP Cost"]);
        Assert.Equal("+3.9%", derived["Double Resist"]);
    }

    // ---- Crafting ----

    private const uint Ring = 310360017;
    private const uint Decoration = 610730016;
    private const uint Gem = 610660025;
    private const uint Odyle = 610610013;
    private const uint Splendent = 310350010;

    /// <summary>A ring (25 % combo) from a decoration (made of gemstones) and odyle; a Dark-side potion with a same-item combo.</summary>
    private static RecipeBook Book() => RecipeBook.Parse($$"""
        {
          "items": {
            "{{Ring}}": { "n": "Sapphire Ring", "g": "Rare", "b": 0 },
            "{{Decoration}}": { "n": "Sapphire Decoration", "g": "Common", "b": 0 },
            "{{Gem}}": { "n": "Sapphire Gemstone", "g": "Common", "b": 1 },
            "{{Odyle}}": { "n": "Odyle", "g": "Common", "b": 1 },
            "{{Splendent}}": { "n": "Splendent Sapphire Ring", "g": "Epic", "b": 0 },
            "900": { "n": "Healing Potion", "g": "Common", "b": 0 }
          },
          "recipes": [
            { "id": 1, "p": {{Decoration}}, "y": 1, "prof": "Jewelcrafting", "race": "Light", "cat": "Materials", "m": [[{{Gem}}, 2]] },
            { "id": 2, "p": {{Ring}}, "y": 1, "prof": "Jewelcrafting", "race": "Light", "cat": "Accessories",
              "m": [[{{Decoration}}, 1], [{{Odyle}}, 1]], "combo": [{{Splendent}}, 25] },
            { "id": 3, "p": 900, "y": 1, "prof": "Alchemy", "race": "Dark", "cat": "Consumables", "m": [[{{Odyle}}, 1]], "combo": [900, 30] }
          ]
        }
        """);

    [Fact]
    public void ComboLinesNameTheBetterItem()
    {
        var book = Book();
        Assert.Equal("25 % combo: Splendent Sapphire Ring", book.ComboLine(book.ById(2)!));
        Assert.Equal("30 % combo: two at once", book.ComboLine(book.ById(3)!));
        Assert.Null(book.ComboLine(book.ById(1)!));

        // The bundled recipes: most gear has one
        var recipes = BundledGameData.Recipes;
        var sword = recipes.Find(CraftRace.Light, "true dragon lord greatsword").First(p => p.Name == "True Dragon Lord Greatsword");
        Assert.Equal("25 % combo: Splendent True Dragon Lord Greatsword", recipes.ComboLine(recipes.Best(sword.ItemId, CraftRace.Light)!));
    }

    [Fact]
    public void BrowseByProfessionAndItemType()
    {
        var book = Book();
        Assert.Equal(["Jewelcrafting"], book.Professions(CraftRace.Light));
        Assert.Equal(["Accessories", "Materials"], book.Categories(CraftRace.Light));
        Assert.Equal(["Sapphire Decoration", "Sapphire Ring"], book.Find(CraftRace.Light, null).Select(p => p.Name));
        Assert.Equal(["Sapphire Ring"], book.Find(CraftRace.Light, "", "jewelcrafting", "Accessories").Select(p => p.Name));
        Assert.Equal(["Sapphire Decoration"], book.Find(CraftRace.Light, "sapphire common").Select(p => p.Name)); // grade too
        Assert.Empty(book.Find(CraftRace.Light, null, "Alchemy"));
        Assert.Equal(["Healing Potion"], book.Find(CraftRace.Dark, null, "Alchemy", "Consumables").Select(p => p.Name));

        var bundled = BundledGameData.Recipes;
        Assert.Equal(["Alchemy", "Blacksmithing", "Cooking", "Jewelcrafting", "Tailoring"], bundled.Professions(CraftRace.Light));
        Assert.Contains("Weapons", bundled.Categories(CraftRace.Light));
        Assert.All(bundled.Find(CraftRace.Light, null, "Cooking"), p => Assert.Equal("Cooking", p.Profession));
    }

    [Fact]
    public void BoughtMaterialsAreNotBrokenDown()
    {
        var book = Book();
        var crafted = CraftPlanner.Plan(book, Ring, 3, CraftRace.Light, _ => 0)!;
        Assert.Equal(6ul, crafted.Crafts); // 3 rings, 3 decorations
        Assert.Equal(new Dictionary<uint, ulong> { [Odyle] = 3, [Gem] = 6 }, crafted.Materials.ToDictionary(m => m.ItemId, m => m.Missing));
        var decoration = crafted.Root.Children.Single(c => c.ItemId == Decoration);
        Assert.True(decoration.Craftable);
        Assert.False(decoration.Bought);
        Assert.False(crafted.Root.Craftable); // the item itself has no toggle

        // Decorations bought instead: they count as a base material, the gemstones drop out
        var have = new Dictionary<uint, ulong> { [Decoration] = 1 };
        var bought = CraftPlanner.Plan(book, Ring, 3, CraftRace.Light, id => have.GetValueOrDefault(id), new HashSet<uint> { Decoration })!;
        Assert.Equal(3ul, bought.Crafts);
        var node = bought.Root.Children.Single(c => c.ItemId == Decoration);
        Assert.True(node.Bought);
        Assert.True(node.Craftable); // the toggle stays, to undo it
        Assert.Empty(node.Children);
        Assert.Equal((3ul, 1ul, 2ul), (node.Need, node.FromBag, node.Missing));
        var materials = bought.Materials.ToDictionary(m => m.ItemId);
        Assert.Equal((3ul, 1ul, 2ul), (materials[Decoration].Need, materials[Decoration].FromBag, materials[Decoration].Missing));
        Assert.DoesNotContain(Gem, materials.Keys);

        // The item planned is never "bought"; base materials can't be
        Assert.Equal(6ul, CraftPlanner.Plan(book, Ring, 3, CraftRace.Light, _ => 0, new HashSet<uint> { Ring, Odyle })!.Crafts);
    }
}
