using Avion2.Engine;

namespace Avion2.Tests;

public class CharacterComparisonTests
{
    private static ArmoryItem Cloak(int enchant) => new(210740031, "Pilgrim Cloak", "Legend", enchant, 0, 19, "Cape", null);
    private static ArmoryItem Staff(int enchant) => new(110830047, "Spiritforged Staff", "Unique", enchant, 0, 1, "MainHand", null);

    private static ArmoryProfile Profile(string name, string cls, IReadOnlyList<ArmoryItem> gear, int might, int nezekan, int onslaught) => new(
        name, name, cls, 45, 60_000, 1_400, "Elyos", null, 1321, "Gauss", null,
        [new ArmoryStat("STR", "Might", might, [])],
        [new ArmoryBoard(81, "Nezekan", nezekan, 88)],
        [], 0, gear, [], null, null, null,
        [new ArmorySkill(18010000, "Onslaught", "Active", onslaught, true, null)],
        DateTimeOffset.UnixEpoch);

    [Fact]
    public void GearIsComparedBySlotWithItemLevels()
    {
        var mine = Profile("Me", "Chanter", [Staff(10), Cloak(5)], 25, 60, 14);
        var theirs = Profile("Them", "Chanter", [Cloak(8)], 30, 70, 16);

        var c = CharacterComparison.Build(mine, theirs, BundledGameData.GearStats);

        var cloak = c.Gear.Single(g => g.Slot == 19);
        Assert.Equal((45, 48, 3), (cloak.MineLevel, cloak.TheirsLevel, cloak.Difference));
        // a slot only one of them fills has no difference
        var staff = c.Gear.Single(g => g.Slot == 1);
        Assert.Null(staff.Theirs);
        Assert.Null(staff.Difference);

        Assert.Equal(5, c.Stats.Single(s => s.Name == "Might").Difference);
        Assert.Equal((60, 70, 88), (c.Boards[0].Mine, c.Boards[0].Theirs, c.Boards[0].Total));
        Assert.Equal(2, c.Skills.Single().Difference);
    }

    [Fact]
    public void SkillsOnlyWithinOneClass()
    {
        var c = CharacterComparison.Build(
            Profile("Me", "Chanter", [], 25, 60, 14), Profile("Them", "Sorcerer", [], 25, 60, 14), BundledGameData.GearStats);

        Assert.False(c.SameClass);
        Assert.Empty(c.Skills);
    }
}
