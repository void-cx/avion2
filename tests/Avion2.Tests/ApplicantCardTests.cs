using Avion2.Analysis;
using Avion2.Engine;
using Avion2.Protocol;

namespace Avion2.Tests;

/// <summary>The applicant card's sums: the room an applicant is measured against, their gear, our runs with them (names made up).</summary>
public class ApplicantCardTests
{
    private static FinderSlotView Seat(byte slot, string name, uint itemLevel, uint power, bool self = false) =>
        new(slot, 100u + slot, name, GameClass.Templar, 45, itemLevel, power, default, false, true, self, self);

    private static FinderSlotView Open(byte slot) => new(slot, 0, "", GameClass.Unknown, 0, 0, 0, default, false, false, false, false);

    private static FinderRoomView Room(params FinderSlotView[] slots) => new(1, "Room", 0, default, (byte)slots.Length, slots, [], true);

    [Fact]
    public void TheRoomIsItsSeatedMembers()
    {
        var room = ApplicantCard.Room(Room(Seat(0, "Alpha", 2000, 100_000, self: true), Seat(1, "Bravo", 1600, 80_000), Seat(2, "Charlie", 0, 0), Open(3)));

        Assert.Equal(3, room.Members);
        Assert.Equal(1800u, room.AverageItemLevel); // unknown item levels don't count
        Assert.Equal(90_000u, room.AverageCombatPower);
        Assert.Equal([1600u, 2000u], room.ItemLevels);
        Assert.Equal((1600u, 2000u), (room.MinItemLevel!.Value, room.MaxItemLevel!.Value));
    }

    [Fact]
    public void TheApplicantIsComparedInPercent()
    {
        Assert.Equal(10, ApplicantCard.PercentVs(1980, 1800));
        Assert.Equal(-25, ApplicantCard.PercentVs(1350, 1800));
        Assert.Equal(0, ApplicantCard.PercentVs(1801, 1800));
        Assert.Null(ApplicantCard.PercentVs(1800, null));
        Assert.Null(ApplicantCard.PercentVs(0, 1800));

        Assert.Equal(0.5, ApplicantCard.Position(1800, 1600, 2000));
        Assert.Equal(0.5, ApplicantCard.Position(1800, 1800, 1800));
        Assert.Equal(1, ApplicantCard.Position(2400, 1600, 2000));
    }

    [Fact]
    public void TheGearIsSummedUp()
    {
        static ArmoryItem Item(int slot, string grade, int enchant) => new(1, "Item", grade, enchant, 0, slot, "Slot", null);
        var profile = new ArmoryProfile("x", "Bravo", "Cleric", 45, 100_000, 2000, "Elyos", null, 1321, "Test", null, [],
            [new ArmoryBoard(1, "One", 120, 200), new ArmoryBoard(2, "Two", 106, 180)], [], 174,
            [Item(1, "Unique", 9), Item(3, "Epic", 8), Item(12, "Legend", 4), Item(10, "Epic", 7), Item(23, "Epic", 15), Item(30, "Rare", 0)],
            [], null, null, null, [], DateTimeOffset.UnixEpoch);

        var gear = ApplicantCard.Gear(profile);
        Assert.Equal([1, 3, 10, 12], gear.Items.Select(i => i.Slot)); // no runes or other slots, in slot order
        Assert.Equal("2 Epic · 1 Unique · 1 Heroic", ApplicantCard.GradeLine(gear));
        Assert.Equal(7.0, gear.AverageEnchant);
        Assert.Equal((226, 380, 174), (gear.DaevanionOpen, gear.DaevanionTotal, gear.Titles));
    }

    [Fact]
    public void OurRunsWithTheApplicantAreCounted()
    {
        static DungeonRun Run(long start, params string[] party) =>
            new() { StartMs = start, Party = party.Select(p => new RunMember(p, GameClass.Cleric)).ToList() };

        var seen = ApplicantCard.SeenWith([Run(1_000, "Bravo", "Charlie"), Run(5_000, "bravo"), Run(9_000, "Charlie")], "Bravo");
        Assert.Equal(2, seen.Count);
        Assert.Equal(5_000, seen.Last!.StartMs);

        Assert.Equal(new RunsTogether(0, null), ApplicantCard.SeenWith([Run(1_000, "Charlie")], "Bravo"));
        Assert.Equal("2 days ago", ApplicantCard.Ago(TimeSpan.FromHours(50)));
        Assert.Equal("5 minutes ago", ApplicantCard.Ago(TimeSpan.FromMinutes(5.5)));
    }
}
