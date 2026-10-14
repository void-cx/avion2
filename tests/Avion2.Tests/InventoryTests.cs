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
