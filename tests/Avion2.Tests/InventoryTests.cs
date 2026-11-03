using Avion2.Analysis;
using Avion2.Engine;
using Avion2.Protocol;

namespace Avion2.Tests;

public class InventoryTests
{
    // Records from a real 11 56 (entering the world): 15,895 Enhance Stones, Spiritforged Staff +10, Bakarma Boots +5
    private const string Stone = "d53804000001000037337037173e000000000000010b00000000000000000000000000";
    private const string Staff = "b6ca040000000000df219b0601000000000000000b010000000000000000000000000a00" + "00000000000000000000";
    private const string Boots = "26c504000000000096f58d0c01000000000000000b080000000000000000000000000005" + "00000000000000000000";

    private static List<GameEvent> Parse(string hex)
    {
        var events = new List<GameEvent>();
        Assert.True(PacketParser.Parse(Convert.FromHexString(hex), 1_000, events.Add));
        return events;
    }

    [Fact]
    public void InventoryListHasCountsAndLevels()
    {
        var list = Assert.IsType<InventoryListEvent>(Assert.Single(Parse("1156" + "10026100" + Stone + Staff + Boots)));

        var stone = list.Items.Single(i => i.ItemId == ItemIds.EnhanceStone);
        Assert.Equal(15_895ul, stone.Count);
        Assert.Equal(0x01, stone.Container);

        var staff = list.Items.Single(i => i.ItemId == 110830047);
        Assert.Equal(InventoryItem.Equipped, staff.Container);
        Assert.Equal((byte)1, staff.Slot);
        Assert.Equal((byte)10, staff.Enchant);

        // The boots carry their level one byte later
        var boots = list.Items.Single(i => i.ItemId == 210630038);
        Assert.Equal((byte)5, boots.Enchant);
    }

    [Fact]
    public void SlotUpdateChangesTheCount()
    {
        // 1B 56: Kina in slot 17 goes to 97,105
        var update = Assert.IsType<InventoryItemEvent>(Assert.Single(Parse("1B56" + "010100d0bb0d0000f301003f337037517b0100000000000111000000000000000000000000000b0000")));
        Assert.Equal(ItemIds.Kinah, update.Item.ItemId);
        Assert.Equal(97_105ul, update.Item.Count);
        Assert.Equal((byte)17, update.Item.Slot);

        var inventory = new InventoryState();
        inventory.Apply(Assert.Single(Parse("1156" + "10026100" + Stone + Staff + Boots)));
        inventory.Apply(update);
        inventory.Apply(new InventoryItemEvent(2_000, new InventoryItem(ItemIds.KinahBound, 3_359_246, 0x01, 1)));
        Assert.Equal(3_359_246ul + 97_105ul, inventory.Kinah);
        Assert.Equal(15_895ul, inventory.Count(ItemIds.EnhanceStone));

        // A count of 0 empties the slot; levels survive updates
        inventory.Apply(new InventoryItemEvent(3_000, new InventoryItem(ItemIds.EnhanceStone, 0, 0x01, 0x0b)));
        Assert.Equal(0ul, inventory.Count(ItemIds.EnhanceStone));
        inventory.Apply(new InventoryItemEvent(4_000, new InventoryItem(110830047, 1, InventoryItem.Equipped, 1)));
        Assert.Equal((byte)10, inventory.Equipped.Single(i => i.Slot == 1).Enchant);
    }

    [Fact]
    public void EnchantingShowsInTheSlotUpdate()
    {
        // 1B 56 after enchanting the Pilgrim Cloak (slot 19) from +5 to +6: the record carries the new level
        const string Cloak = "3fa38f0c" + "0100000000000000" + "0b13" + "000000000000000000000000" + "06" + "0000000000000000000000000033000000";
        var update = Assert.IsType<InventoryItemEvent>(Assert.Single(Parse("1B56" + "01" + "0a00010103e70800000a" + Cloak)));
        Assert.Equal(210740031u, update.Item.ItemId);
        Assert.Equal((byte)6, update.Item.Enchant);

        var inventory = new InventoryState();
        inventory.Apply(new InventoryListEvent(1_000, [new InventoryItem(210740031, 1, InventoryItem.Equipped, 19, 5)]));
        inventory.Apply(update);
        Assert.Equal((byte)6, inventory.Equipped.Single().Enchant);
    }

    [Fact]
    public void SlotUpdateCarriesEveryChangedSlot()
    {
        // 1B 56 from a Daeva Pass reward: a Nightmare Dungeon Challenge Ticket (with an expiry time) and 20 Small Odyle Energy
        var events = Parse("1B56" + "020000cb160b000075000042da051e0100000000000000013e00000002008099a67da20100004800000100595c050000cd010040da051e1400000000000000012f000000000088dd7da2010000480000");
        Assert.Equal([(503700034u, 1ul, (byte)62), (503700032u, 20ul, (byte)47)],
            events.Cast<InventoryItemEvent>().Select(e => (e.Item.ItemId, e.Item.Count, e.Item.Slot)));

        // Gear in between carries stats, so records are found by their shape: earrings, then Kina
        const string Earrings = "0001ae580a00002d0100" + "1ee37d1201000000000000000138" + "0000000000000000000000000000000000000000000000000000210000000003000000000000000000000000000000000000000000000000000000000000030000000000000000000000000000000000000000180000";
        const string Kina = "010038ab0a0000bf0000" + "2133703760037000000000000101" + "00000000000000000000000000480000";
        var two = Parse("1B56" + "02" + Earrings + Kina).Cast<InventoryItemEvent>().ToList();
        Assert.Equal([310240030u, ItemIds.KinahBound], two.Select(e => e.Item.ItemId));
        Assert.Equal(7_340_896ul, two[1].Item.Count);
    }

    [Fact]
    public void ListWithARepeatedSlotStillApplies()
    {
        // Real lists can name one slot twice (container 10, slot 0); the list must not be dropped
        var inventory = new InventoryState();
        Assert.True(inventory.Apply(new InventoryListEvent(1_000,
        [
            new InventoryItem(210740031, 1, InventoryItem.Equipped, 19, 6),
            new InventoryItem(500100001, 3, 0x10, 0),
            new InventoryItem(500100002, 5, 0x10, 0),
        ])));

        Assert.True(inventory.IsKnown);
        Assert.Equal((byte)6, inventory.Equipped.Single().Enchant);
    }

    [Fact]
    public void ListIsNamedAfterTheCharacterItBelongsTo()
    {
        var session = new GameSession();
        session.Apply(new SelfIdentityEvent(1_000, 100, "Doxylamin", 1321, GameClass.Chanter, 45));
        session.Apply(new InventoryListEvent(2_000, [new InventoryItem(110830047, 1, InventoryItem.Equipped, 1, 10)]));
        Assert.Equal("Doxylamin", session.Inventory.Character);

        // Switching characters: the game points at the new entity, sends its list, and names it seconds later
        session.Apply(new SelfHintEvent(2_500, 200));
        session.Apply(new InventoryListEvent(3_000, [new InventoryItem(110760001, 1, InventoryItem.Equipped, 1, 0)]));
        Assert.Null(session.Inventory.Character);
        session.Apply(new SelfIdentityEvent(4_000, 200, "Chiyu", 1321, GameClass.Cleric, 7));
        Assert.Equal("Chiyu", session.Inventory.Character);
    }

    [Fact]
    public void EachCharacterKeepsItsOwnInventory()
    {
        static InventoryState List(string? who, long ms, uint item, byte enchant)
        {
            var inventory = new InventoryState { Character = who };
            inventory.Apply(new InventoryListEvent(ms, [new InventoryItem(item, 1, InventoryItem.Equipped, 1, enchant)]));
            return inventory;
        }

        var book = new InventoryBook();
        book.Take(List("Doxylamin", 1_000, 110830047, 10));

        // Chiyu logs in: shown at once, kept once the game names it; Doxylamin's stays as it was
        var unnamed = List(null, 2_000, 110760001, 0);
        Assert.Same(unnamed, book.Take(unnamed));
        Assert.Null(book.For("Chiyu"));
        book.Take(List("Chiyu", 2_000, 110760001, 0));
        Assert.Equal((byte)10, book.For("Doxylamin")!.Equipped.Single().Enchant);
        Assert.Equal("Chiyu", book.Latest!.Character);

        // Slot updates seen before a list wait for our name, then go on top of that character's saved inventory
        var updates = new InventoryState();
        updates.Apply(new InventoryItemEvent(3_000, new InventoryItem(ItemIds.KinahBound, 500, InventoryItem.Cube, 1)));
        Assert.Null(book.Take(updates));
        updates.Character = "Doxylamin";
        var merged = book.Take(updates)!;
        Assert.Equal(500ul, merged.Kinah);
        Assert.Equal((byte)10, merged.Equipped.Single().Enchant);
        Assert.Equal(0ul, book.For("Chiyu")!.Kinah);

        var back = System.Text.Json.JsonSerializer.Deserialize<InventoryBook>(System.Text.Json.JsonSerializer.Serialize(book))!;
        Assert.Equal(["Doxylamin", "Chiyu"], back.All.Select(i => i.Character));
    }

    [Fact]
    public void InventorySurvivesSaving()
    {
        var inventory = new InventoryState { Character = "Doxylamin" };
        inventory.Apply(Assert.Single(Parse("1156" + "10026100" + Stone + Staff)));
        var json = System.Text.Json.JsonSerializer.Serialize(inventory);
        var back = System.Text.Json.JsonSerializer.Deserialize<InventoryState>(json)!;

        Assert.True(back.IsKnown);
        Assert.Equal("Doxylamin", back.Character);
        Assert.Equal(15_895ul, back.Count(ItemIds.EnhanceStone));
        Assert.Equal((byte)10, back.Equipped.Single().Enchant);
    }

    [Fact]
    public void ReachFromWhatYouHave()
    {
        var forge = BundledGameData.Forge;
        var staff = forge.Find(110830047)!;
        var ladder = forge.Ladder(staff, ForgeMode.Enchant)!;
        ulong Have(uint id) => id == ItemIds.EnhanceStone ? 15_895ul : 0;

        // From +10 the next step needs ~1.49 × its stones; 15,895 stones and plenty of Kinah go a step or more, not to +15
        int reach = GearForge.Reach(ladder, 10, 50_000_000, Have);
        Assert.InRange(reach, 11, 14);
        Assert.Equal(10, GearForge.Reach(ladder, 10, 0, Have)); // no Kinah, no progress
    }
}
