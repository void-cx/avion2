using System.Text.Json;
using Avion2.Analysis;
using Avion2.Engine;
using Avion2.Protocol;
using static Avion2.Tests.TestPackets;

namespace Avion2.Tests;

/// <summary>Pet levels and soul sources, the craft log and the crafting calculator (hand-built records and recipes).</summary>
public class CraftingTests
{
    private const uint Me = 1000;
    private const uint Station = 21219;
    private const uint Refining = 610530001;
    private const uint RefiningBound = 630530001;
    private const uint Odyle = 610610013;
    private const uint Decoration = 610730016;
    private const uint Ring = 310360017;

    // ---- Pets ----

    [Fact]
    public void PetLevelUpIsRead()
    {
        // Pet 1216 reached level 2 (creature type 5, board level 3, board XP 2750)
        var up = Assert.IsType<PetLevelUpEvent>(Assert.Single(Parse("1490 0000 c0040000 02000000 05 03000000 be0a000000000000")));
        Assert.Equal((1216u, 2u, (byte)5), (up.PetId, up.Level, up.CreatureType));

        Assert.Empty(Parse("1490 0100 c0040000 02000000 05 03000000 be0a000000000000")); // a refusal
        Assert.Empty(Parse("1490 0000 c0040000 02000000 05 03000000")); // always 23 bytes
    }

    [Fact]
    public void RosterCarriesThePetLevels()
    {
        // Mount, 2 pets owned (1003 at level 1, 1216 at level 2), souls of 2 kinds (1003: 3, 1099: 4), then crystals
        var roster = Assert.IsType<PetRosterEvent>(Assert.Single(Parse(
            "0090 b4040000 02 eb030000eb03000001000000 c0040000c004000002000000 02 eb03000003000000 4b04000004000000 01 06 64000000")));
        Assert.Equal([1003u, 1216u], roster.Owned);
        Assert.Equal([1u, 2u], roster.Levels!);

        var progress = new CharacterProgress { Character = "TestDaeva" };
        progress.Apply(roster);
        Assert.Equal(2u, progress.PetLevels[1216]);
        Assert.Equal(25u, PetSoulTable.Needed(progress, 1003)); // owned at level 1: 25 to level 2
        Assert.Null(PetSoulTable.Needed(progress, 1216)); // level 3's cost is not known
        Assert.Equal(5u, PetSoulTable.Needed(progress, 1099)); // not owned: 5 to the pet

        progress.Apply(new PetLevelUpEvent(5_000, 1003, 2, 4));
        progress.Apply(new PetAcquiredEvent(6_000, Me, 1099, 1, 3));
        Assert.Equal((2u, 1u), (progress.PetLevels[1003], progress.PetLevels[1099]));

        // Levels go with the pets into the saved progress
        var saved = JsonSerializer.Deserialize<CharacterProgress>(JsonSerializer.Serialize(progress))!;
        var fresh = new CharacterProgress { Character = "TestDaeva" };
        fresh.FillFrom(saved);
        Assert.Equal(2u, fresh.PetLevels[1003]);
    }

    [Fact]
    public void PetRowsShowTheNextStepAndTheSources()
    {
        var progress = new CharacterProgress
        {
            Character = "TestDaeva",
            Pets = [1003, 1216],
            Souls = { [1003] = 23, [1216] = 71, [1099] = 4, [1100] = 0 },
            PetLevels = { [1003] = 1, [1216] = 2 },
        };
        var sources = new SoulSourceBook();
        sources.Add(new SoulDrop(1, 534_660_002, 1, 2100601));
        sources.Add(new SoulDrop(2, 534_660_002, 2, 2100601));
        sources.Add(new SoulDrop(3, 534_660_002, 1, 2100613));
        sources.Add(new SoulDrop(4, 534_660_002, 1, null)); // dropped from something not in view: not counted
        uint? Item(uint pet) => pet == 1003 ? 534_660_002 : null;
        string? Npc(uint code) => code switch { 2100601 => "Canyon Vine Spirit", 2100613 => "Canyon Lake Skyray", _ => null };

        var rows = PetSoulTable.Rows(progress, sources, Item, pet => $"Pet {pet}", Npc);
        Assert.Equal([1003u, 1099u, 1216u], rows.Select(r => r.PetId)); // closest to its next step first; none without souls
        Assert.Equal("23 / 25 to level 2", rows[0].Progress);
        Assert.Equal("Level 1", rows[0].State);
        Assert.Equal("Canyon Vine Spirit ×3 · Canyon Lake Skyray ×1", rows[0].Sources);
        Assert.Equal(("4 / 5 to the pet", "Not owned"), (rows[1].Progress, rows[1].State));
        Assert.Equal(("71 souls", "Level 2"), (rows[2].Progress, rows[2].State));

        Assert.Equal("Soul of Pet 1099: 4/5", PetSoulTable.Notice(progress, 1099, pet => $"Pet {pet}"));
        Assert.Equal("Soul of Pet 1003: 23/25 to level 2", PetSoulTable.Notice(progress, 1003, pet => $"Pet {pet}"));
        Assert.Equal("Soul of Pet 1216: 71", PetSoulTable.Notice(progress, 1216, pet => $"Pet {pet}"));
        Assert.Null(PetSoulTable.Notice(new CharacterProgress(), 1003, pet => $"Pet {pet}")); // before the roster
    }

    [Fact]
    public void SoulDropsAreJoinedToTheMonsterAndCountedOnce()
    {
        var session = new GameSession();
        session.Apply(new SelfIdentityEvent(0, Me, "TestDaeva", 1321, GameClass.Chanter, 45) { CharacterId = 77 });
        session.Apply(new NpcSpawnEvent(100, 500, 0x04, 2100600, 1_000, null));
        session.Apply(new NpcSpawnEvent(100, 501, 0x04, 2100601, 1_000, null));
        session.Apply(new LootDropEvent(1_000, 500, 11, 534_660_149, 1, 77, 1321, default));
        session.Apply(new LootDropEvent(1_000, 501, 12, 534_660_002, 2, 77, 1321, default));
        session.Apply(new LootDropEvent(1_000, 501, 13, 182_400_001, 5, 77, 1321, default)); // not a soul
        foreach (long uid in new long[] { 11, 12, 13 })
        {
            session.Apply(new LootPickedEvent(2_000, 0, uid, false));
            session.Apply(new LootTakenEvent(2_000, uid == 11 ? 500u : 501u, uid, 77));
        }

        Assert.Equal([(534_660_149u, (uint?)2100600u), (534_660_002u, 2100601u)],
            session.Progress.SoulDrops.Select(d => (d.ItemId, d.NpcCode)));

        // Two kinds at once: the counts picked up of each item tell them apart
        session.Apply(new PetSoulsEvent(2_050, [new PetSouls(1216, 1), new PetSouls(1003, 2)], Gained: true));
        Assert.Equal(534_660_149u, session.Progress.SoulItems[1216]);
        Assert.Equal(534_660_002u, session.Progress.SoulItems[1003]);

        var book = new ProgressBook();
        book.Take(session.Progress.Copy());
        book.Take(session.Progress.Copy()); // taken again (every progress change): not counted twice
        Assert.Equal([new SoulSource(2100601, 2)], book.SoulSources.For(534_660_002));

        // Saved and loaded with the progress
        var loaded = JsonSerializer.Deserialize<ProgressBook>(JsonSerializer.Serialize(book))!;
        Assert.Equal([new SoulSource(2100600, 1)], loaded.SoulSources.For(534_660_149));
    }

    [Fact]
    public void SoulLearnerNeedsDistinctCounts()
    {
        var learner = new SoulItemLearner();
        learner.Picked(new LootPickup(1_000, 534_660_149, 1, 500));
        learner.Picked(new LootPickup(1_000, 534_660_002, 1, 501));
        Assert.Empty(learner.ApplyAll(new PetSoulsEvent(1_050, [new PetSouls(1216, 1), new PetSouls(1003, 1)], Gained: true)));

        learner.Picked(new LootPickup(2_000, 534_660_149, 1, 500));
        Assert.Equal((1216u, 534_660_149u), learner.Apply(new PetSoulsEvent(2_050, [new PetSouls(1216, 1)], Gained: true)));
    }

    // ---- Craft log ----

    private static GameSession Crafter(bool named = true, bool list = true)
    {
        var session = new GameSession();
        if (list)
        {
            session.Apply(new InventoryListEvent(0,
            [
                new InventoryItem(Decoration, 2, InventoryItem.Cube, 10),
                new InventoryItem(RefiningBound, 5, InventoryItem.Cube, 11),
                new InventoryItem(Odyle, 3, InventoryItem.Cube, 12),
            ]));
        }

        if (named)
        {
            session.Apply(new SelfIdentityEvent(0, Me, "TestDaeva", 1321, GameClass.Chanter, 45));
        }

        return session;
    }

    /// <summary>A Sapphire Ring craft as the game sends it: the materials leave in the start's instant, the ring comes with the end.</summary>
    private static void CraftRing(GameSession session, long at, byte result, ulong decorationsLeft, ulong refiningLeft, ulong odyleLeft)
    {
        session.Apply(new InventoryItemEvent(at, new InventoryItem(Decoration, decorationsLeft, InventoryItem.Cube, 10)));
        session.Apply(new InventoryItemEvent(at, new InventoryItem(RefiningBound, refiningLeft, InventoryItem.Cube, 11)));
        session.Apply(new InventoryItemEvent(at, new InventoryItem(Odyle, odyleLeft, InventoryItem.Cube, 12)));
        session.Apply(new CraftStartEvent(at, Station, 213036001, result != 2, false, 6));
        session.Apply(new CraftEndEvent(at + 15_000, Station, result));
        if (result != 2)
        {
            session.Apply(new InventoryItemEvent(at + 15_000, new InventoryItem(Ring, 1, InventoryItem.Cube, 20)));
        }

        session.Apply(new ServerClockEvent(at + 15_100, 0)); // the next moment hands the craft on
    }

    [Fact]
    public void CraftLogsWhatWasUsedAndMade()
    {
        var session = Crafter();
        var crafts = new List<(CraftEntry Craft, string Who)>();
        session.Crafts.Crafted += (craft, who) => crafts.Add((craft, who));

        CraftRing(session, 10_000, 0, 1, 3, 2);
        var (ring, who) = Assert.Single(crafts);
        Assert.Equal("TestDaeva", who);
        Assert.Equal(213036001u, ring.RecipeId);
        Assert.Equal([new RunItem(Decoration, 1), new RunItem(RefiningBound, 2), new RunItem(Odyle, 1)], ring.Used);
        Assert.Equal([new RunItem(Ring, 1)], ring.Made);
        Assert.Equal(1ul, session.Inventory.SlotCount(new InventoryItem(Ring, 0, InventoryItem.Cube, 20))); // the bag took the ring too

        // A failed craft still uses its materials and makes nothing
        CraftRing(session, 40_000, 2, 0, 1, 1);
        Assert.True(crafts[1].Craft.Failed);
        Assert.Empty(crafts[1].Craft.Made);
        Assert.Equal(3, crafts[1].Craft.Used.Count);
    }

    [Fact]
    public void CraftsBeforeOurNameWaitForIt()
    {
        var session = Crafter(named: false, list: false);
        var crafts = new List<(CraftEntry Craft, string Who)>();
        session.Crafts.Crafted += (craft, who) => crafts.Add((craft, who));

        CraftRing(session, 10_000, 1, 1, 3, 2);
        Assert.Empty(crafts);

        session.Apply(new SelfIdentityEvent(30_000, Me, "TestDaeva", 1321, GameClass.Chanter, 45));
        session.Apply(new ServerClockEvent(30_050, 0));
        var (craft, who) = Assert.Single(crafts);
        Assert.Equal("TestDaeva", who);
        Assert.True(craft.Combo);
        Assert.Empty(craft.Used); // the bag was not known: nothing measured rather than a guess
    }

    [Fact]
    public void AbortedCraftIsNotLogged()
    {
        var session = Crafter();
        var crafts = new List<CraftEntry>();
        session.Crafts.Crafted += (craft, _) => crafts.Add(craft);
        session.Apply(new CraftStartEvent(1_000, Station, 213036001, true, false, 6));
        session.Apply(new InteractionInterruptedEvent(3_000, Station, Me, 1));
        session.Apply(new CraftEndEvent(4_000, Station, 0)); // no craft running any more
        session.Apply(new ServerClockEvent(5_000, 0));
        Assert.Empty(crafts);
    }

    [Fact]
    public void CraftBookKeepsTheNewestPerCharacter()
    {
        var book = new CraftBook();
        for (int i = 0; i < CraftBook.Kept + 5; i++)
        {
            book.Add("TestDaeva", new CraftEntry { StartMs = i, RecipeId = 1 });
        }

        book.Add("OtherDaeva", new CraftEntry { StartMs = 7, RecipeId = 2 });
        var loaded = JsonSerializer.Deserialize<CraftBook>(JsonSerializer.Serialize(book))!;
        Assert.Equal(CraftBook.Kept, loaded.For("TestDaeva").Count);
        Assert.Equal(CraftBook.Kept + 4, loaded.For("TestDaeva")[0].StartMs);
        Assert.Single(loaded.For("OtherDaeva"));
        Assert.Empty(loaded.For(null));
    }

    [Fact]
    public void CraftLineNamesWhatWasMade()
    {
        var book = Book();
        var craft = new CraftEntry
        {
            StartMs = 1_793_621_590_600, RecipeId = 2, Result = 1,
            Used = [new RunItem(Decoration, 1), new RunItem(Odyle, 1)], Made = [new RunItem(Ring, 2)],
        };
        var line = CraftText.Line(craft, book, _ => null, TimeZoneInfo.Utc);
        Assert.Equal(("Nov 2, 12:13", "Sapphire Ring ×2 (combo)", "Sapphire Decoration 1 · Odyle 1"), (line.When, line.Made, line.Used));

        var failed = CraftText.Line(new CraftEntry { StartMs = 0, RecipeId = 2, Result = 2 }, book, _ => null, TimeZoneInfo.Utc);
        Assert.Equal("Sapphire Ring (failed)", failed.Made);
        Assert.True(failed.Failed);
    }

    // ---- Calculator ----

    /// <summary>A ring from a decoration (made of gemstones), refining stones and odyle; the gemstones in two batch sizes.</summary>
    private static RecipeBook Book() => RecipeBook.Parse($$"""
        {
          "items": {
            "{{Ring}}": { "n": "Sapphire Ring", "g": "Rare", "b": 0 },
            "{{Decoration}}": { "n": "Sapphire Decoration", "g": "Common", "b": 0 },
            "610660025": { "n": "Sapphire Gemstone", "g": "Common", "b": 1 },
            "630542001": { "n": "Abrasive (Bound)", "g": "Common", "b": 1 },
            "{{Refining}}": { "n": "Refining Stone", "g": "Common", "b": 1 },
            "{{Odyle}}": { "n": "Odyle", "g": "Common", "b": 1 },
            "310350010": { "n": "Splendent Sapphire Ring", "g": "Epic", "b": 0 }
          },
          "bound": { "{{Refining}}": [{{RefiningBound}}] },
          "recipes": [
            { "id": 1, "p": {{Decoration}}, "y": 1, "prof": "Jewelcrafting", "race": "Light", "cat": "Materials", "fee": 0,
              "m": [[610660025, 2], [630542001, 1]] },
            { "id": 3, "p": {{Decoration}}, "y": 10, "prof": "Jewelcrafting", "race": "Light", "cat": "Materials", "fee": 0,
              "m": [[610660025, 20], [630542001, 10]] },
            { "id": 2, "p": {{Ring}}, "y": 1, "prof": "Jewelcrafting", "race": "Light", "cat": "Accessories", "fee": 0,
              "m": [[{{Decoration}}, 1], [{{Refining}}, 2], [{{Odyle}}, 1]], "combo": [310350010, 25] },
            { "id": 4, "p": 999, "y": 1, "prof": "Jewelcrafting", "race": "Dark", "cat": "Accessories", "fee": 0, "m": [[{{Odyle}}, 1]] }
          ]
        }
        """);

    [Fact]
    public void PlanGoesDownToTheBaseMaterials()
    {
        var book = Book();
        Assert.Equal(1u, book.Best(Decoration, CraftRace.Light)!.Id); // the smallest batch
        Assert.Equal(["Sapphire Ring"], book.Products(CraftRace.Light).Where(p => p.Category == "Accessories").Select(p => p.Name));

        // In the bag: 1 decoration, 3 bound refining stones (they count for Refining Stone), 2 gemstones
        var bag = new Dictionary<uint, ulong> { [Decoration] = 1, [RefiningBound] = 3, [610660025] = 2 };
        var inventory = new InventoryState();
        inventory.Apply(new InventoryListEvent(0, bag.Select((b, i) => new InventoryItem(b.Key, b.Value, InventoryItem.Cube, (byte)i)).ToList()));
        var plan = CraftPlanner.Plan(book, Ring, 3, CraftRace.Light, CraftPlanner.Bag(inventory, book))!;

        Assert.Equal(3ul, plan.Root.Crafts);
        var decoration = plan.Root.Children.Single(c => c.ItemId == Decoration);
        Assert.Equal((3ul, 1ul, 2ul), (decoration.Need, decoration.FromBag, decoration.Crafts)); // 1 from the bag, 2 crafted
        Assert.Equal(5ul, plan.Crafts);
        Assert.Equal(0ul, plan.KinahFee);

        var materials = plan.Materials.ToDictionary(m => m.ItemId);
        Assert.Equal((4ul, 2ul, 2ul), (materials[610660025].Need, materials[610660025].FromBag, materials[610660025].Missing));
        Assert.Equal((6ul, 3ul, 3ul), (materials[Refining].Need, materials[Refining].FromBag, materials[Refining].Missing));
        Assert.Equal((3ul, 0ul, 3ul), (materials[Odyle].Need, materials[Odyle].FromBag, materials[Odyle].Missing));
        Assert.Equal(2ul, materials[630542001].Missing);
        Assert.False(plan.HasEverything);
        Assert.Equal(Ring, plan.Flat.First().ItemId);

        Assert.Null(CraftPlanner.Plan(book, Odyle, 1, CraftRace.Light, _ => 0)); // nothing makes it
        Assert.Null(CraftPlanner.Plan(book, Ring, 0, CraftRace.Light, _ => 0));
    }

    [Fact]
    public void RacesFromServerFactions()
    {
        Assert.Equal(CraftRace.Light, RecipeBook.RaceOf("Elyos"));
        Assert.Equal(CraftRace.Dark, RecipeBook.RaceOf("Asmodian"));
        Assert.Null(RecipeBook.RaceOf("?"));
    }

    [Fact]
    public void BundledRecipesMatchOurCrafts()
    {
        var book = BundledGameData.Recipes;
        Assert.True(book.Recipes.Count > 500);
        Assert.All(book.Recipes, r => Assert.Equal(0ul, r.Fee)); // no recipe charges Kinah

        // Recipes our captures crafted, with the materials that left the bag
        var salad = book.ById(514086002)!;
        Assert.Equal("Fruit Salad", book.Name(salad.Product));
        Assert.Equal(["Fresh Fruit 1", "Verteron Pepper (Bound) 1"], salad.Materials.Select(m => $"{book.Name(m.ItemId)} {m.Count}"));
        var ring = book.ById(213036001)!;
        Assert.Equal([(Decoration, 1u), (Refining, 2u), (Odyle, 1u)], ring.Materials.Select(m => (m.ItemId, m.Count)));
        Assert.Contains(RefiningBound, book.WithVariants(Refining)); // used in its place
        Assert.Equal(3u, book.ById(315016007)!.Yield); // Yggdrasil Paper: three per craft
        foreach (uint id in new uint[] { 213025001, 213026001, 215016003, 215016005, 311096001, 315016001, 315016006, 514086001 })
        {
            Assert.NotNull(book.ById(id));
        }

        Assert.NotNull(CraftPlanner.Plan(book, book.ById(311096001)!.Product, 1, CraftRace.Light, _ => 0));
    }
}
