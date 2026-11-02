using Avion2.Analysis;
using Avion2.Engine;
using Avion2.Protocol;

namespace Avion2.Tests;

public class GearUpgradeTests
{
    private const uint PilgrimCloak = 210_740_031;
    private const uint PilgrimNecklace = 310_140_031;
    private const uint LudrasBlade = 110_120_003;

    private static InventoryItem Worn(uint id, byte slot, byte enchant) => new(id, 1, InventoryItem.Equipped, slot, enchant);

    [Fact]
    public void BundledTableKnowsItemLevelsAndStats()
    {
        var stats = BundledGameData.GearStats;
        var cloak = stats.Item(PilgrimCloak)!;

        Assert.Equal(40, cloak.ItemLevel);
        Assert.Equal("Cape", cloak.Slot);
        Assert.Equal(["Defense +14", "HP +5"], stats.Gain(cloak.Effect!, 4, 5).Select(g => g.ToString()).Order());
        // a weapon's min and max attack rise together: one "Attack"
        Assert.Equal(["Attack +42"], stats.Gain(stats.Item(LudrasBlade)!.Effect!, 0, 5).Select(g => g.ToString()));
    }

    [Fact]
    public void CheapestItemLevelComesFirst()
    {
        var gear = new[] { Worn(LudrasBlade, 1, 10), Worn(PilgrimCloak, 19, 4), Worn(PilgrimNecklace, 10, 4) };

        var next = GearUpgrades.Next(gear, BundledGameData.Forge, BundledGameData.GearStats);

        Assert.Equal(3, next.Count);
        var cloak = next[0];
        Assert.Equal(PilgrimCloak, cloak.Slot.ItemId);
        Assert.Equal(4, cloak.From);
        Assert.Equal(1, cloak.ItemLevel);
        Assert.Equal(28_000, cloak.Kinah);
        Assert.Equal(1_120, cloak.Materials.Single().Count);
        Assert.Equal(100, cloak.Chance);
        // +10 → +11 on Unique gear can fail, so it costs more than one attempt
        var blade = next[^1];
        Assert.Equal(LudrasBlade, blade.Slot.ItemId);
        Assert.True(blade.Kinah > 360_000);
        Assert.True(blade.Chance < 100);
    }

    [Fact]
    public void MaxedGearHasNoNextStep()
    {
        var next = GearUpgrades.Next([Worn(PilgrimCloak, 19, 10)], BundledGameData.Forge, BundledGameData.GearStats);

        Assert.Empty(next);
    }

    [Fact]
    public void SafeStepsAddUpEveryStepThatCannotFail()
    {
        var (levels, kinah, materials) = GearUpgrades.SafeSteps([Worn(PilgrimCloak, 19, 8), Worn(LudrasBlade, 1, 9)], BundledGameData.Forge);

        // cloak +8 → +10 (2 steps), blade +9 → +10 (1 step; +10 → +11 can fail)
        Assert.Equal(3, levels);
        Assert.True(kinah > 202_000);
        Assert.Contains(materials, m => m.Name == "Enhance Stone");
    }
}
