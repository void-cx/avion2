using System.Text.Json;
using Avion2.Analysis;
using Avion2.Protocol;
using static Avion2.Tests.TestPackets;

namespace Avion2.Tests;

/// <summary>Item level, daily and weekly caps, tickets, the Daeva Pass, experience, pets and items used (hand-built records).</summary>
public class ProgressTests
{
    private const uint Me = 1000;
    private const string Season = "3f270000 743f0100 725b0000"; // season 10047: 81,780 XP, 23,410 this week

    [Fact]
    public void ItemLevelNowAndHighest()
    {
        var level = Assert.IsType<SelfItemLevelEvent>(Assert.Single(Parse("1D56 8b050000 8f050000")));
        Assert.Equal(1419u, level.ItemLevel);
        Assert.Equal(1423u, level.Best);

        Assert.Empty(Parse("1D56 8b050000")); // always 8 bytes
    }

    [Fact]
    public void EquipmentChangeNamesThePlayerAndTheirItemLevel()
    {
        // Entity 1000, item level 388, one slot (the slot and the items before and after follow)
        var change = Assert.IsType<EquipmentChangedEvent>(Assert.Single(Parse("1C56 e807 84010000 01 07 3fa38f0c 05")));
        Assert.Equal((Me, 388u, 1), (change.EntityId, change.ItemLevel, change.Slots));

        var registry = new EntityRegistry();
        registry.Apply(change);
        registry.Apply(Assert.Single(Parse("1D56 8b050000 8f050000")));
        Assert.Equal(388u, registry.Find(Me)!.ItemLevel);
        Assert.Equal(1419u, registry.SelfItemLevel);
    }

    [Fact]
    public void ItemUsedByTheGameOrFromTheBag()
    {
        var notice = Assert.IsType<ItemUsedEvent>(Assert.Single(Parse("1656 3930000000000000 adeca709")));
        Assert.Equal((12345L, 162000045u, false), (notice.ItemUid, notice.ItemId, notice.FromBag));

        // 13 56 starts with a result
        var used = Assert.IsType<ItemUsedEvent>(Assert.Single(Parse("1356 0403 3930000000000000 adeca709")));
        Assert.Equal((12345L, 162000045u, true), (used.ItemUid, used.ItemId, used.FromBag));
    }

    [Fact]
    public void KinahAndAbyssPointsTowardTheirCaps()
    {
        var login = Assert.IsType<KinahLimitEvent>(Assert.Single(Parse("A5FF 4b10000000000000 00e91c4a9a010000")));
        Assert.Equal((4171ul, true), (login.Gained, login.Initial));
        Assert.False(Assert.IsType<KinahLimitEvent>(Assert.Single(Parse("A6FF 4b10000000000000 00e91c4a9a010000"))).Initial);

        var abyss = Assert.IsType<AbyssPointLimitEvent>(Assert.Single(Parse("ABFF 9200000000000000 a025260000000000")));
        Assert.Equal((146ul, 2_500_000ul), (abyss.Points, abyss.Cap));
        Assert.IsType<AbyssPointLimitEvent>(Assert.Single(Parse("A9FF 9200000000000000 a025260000000000"))); // older clients
    }

    [Fact]
    public void TicketListWithEveryKindOfEntry()
    {
        // Odyle (both counts), Nightmare (one count), Ascension (none left: mask 0), an Abyss passage (a time)
        var list = Assert.IsType<ContentTicketsEvent>(Assert.Single(Parse(
            "0B61 04 0c01879303c8069204 040400000008 0001b4c404 018196980080ee360000000000")));
        Assert.Equal(
            [new ContentTicket(60_000_001, 840, 530), new ContentTicket(4, 8), new ContentTicket(80_000_001, 0), new ContentTicket(10_000_001, 0, 0, 3_600_000)],
            list.Tickets);
    }

    [Fact]
    public void TicketUsed()
    {
        // Flags 01: a u32 follows the reason
        var changed = Assert.IsType<ContentTicketChangedEvent>(Assert.Single(Parse("0C61 01 040400000007 03 50000000")));
        Assert.Equal(new ContentTicket(4, 7), changed.Ticket);
        Assert.Equal(ContentTicketChangedEvent.Used, changed.Reason);

        Assert.Empty(Parse("0C61 00 020400000007 03")); // an unknown mask bit
    }

    [Fact]
    public void DaevaPassSeasons()
    {
        var pass = Assert.IsType<DaevaPassEvent>(Assert.Single(Parse(
            "24E3 02 " + Season + " 0000 40270000 f4010000 f4010000 00e91c4a9a010000 00")));
        Assert.Equal([new DaevaPassSeason(10047, 81_780, 23_410), new DaevaPassSeason(10048, 500, 500)], pass.Seasons);
    }

    [Fact]
    public void ExperienceGained()
    {
        var exp = Assert.IsType<ExperienceEvent>(Assert.Single(Parse("078D 8813000000000000 fa00000000000000 01")));
        Assert.Equal((5000L, 250L, (byte)1), (exp.Total, exp.Gained, exp.Reason));
    }

    [Fact]
    public void PetRosterSoulsAndANewPet()
    {
        // Mount, two pets owned (pet, pet, level), souls of two kinds, then the rest (not read)
        var roster = Assert.IsType<PetRosterEvent>(Assert.Single(Parse(
            "0090 b4040000 02 e9030000e903000001000000 ea030000ea03000003000000 02 0a04000004000000 e90300000c000000 00")));
        Assert.Equal([1001u, 1002u], roster.Owned);
        Assert.Equal([new PetSouls(1034, 4), new PetSouls(1001, 12)], roster.Souls);

        var gained = Assert.IsType<PetSoulsEvent>(Assert.Single(Parse("0D90 01 0a04000001000000")));
        Assert.True(gained.Gained);
        var spent = Assert.IsType<PetSoulsEvent>(Assert.Single(Parse("0E90 01 0a04000005000000")));
        Assert.False(spent.Gained);
        var acquired = Assert.IsType<PetAcquiredEvent>(Assert.Single(Parse("0990 e807 0a040000 01000000 02 00000000 0000000000000000")));
        Assert.Equal((Me, 1034u, 1u, (byte)2), (acquired.EntityId, acquired.PetId, acquired.Level, acquired.CreatureType));

        var progress = new CharacterProgress();
        foreach (var e in new GameEvent[] { roster, gained, spent, acquired })
        {
            Assert.True(progress.Apply(e));
        }

        Assert.Equal(0u, progress.Souls[1034]);
        Assert.Contains(1034u, progress.Pets!);
        Assert.Empty(progress.NextPets); // owned pets keep collecting souls, but bring nothing
    }

    [Fact]
    public void WornGearCarriesItsItemLevel()
    {
        // The enchant in the first of its two bytes (+10), in the second (+5), and +0: the item level follows 14 bytes later
        const string Staff = "b6ca040000000000" + "df219b06" + "0100000000000000" + "0b01" + "000000000000000000000000" + "0a00" + "000000000000000000000000" + "43000000" + "0000";
        const string Boots = "26c5040000000000" + "96f58d0c" + "0100000000000000" + "0b08" + "00000000000000000000000000" + "05" + "00000000000000000000000000" + "30000000" + "0000";
        const string Cloak = "0000000000000000" + "3fa38f0c" + "0100000000000000" + "0b13" + "000000000000000000000000" + "0000" + "000000000000000000000000" + "26000000" + "0000";
        var list = Assert.IsType<InventoryListEvent>(Assert.Single(Parse("1156 10026100" + Staff + Boots + Cloak)));
        Assert.Equal([(10, 67u), (5, 48u), (0, 38u)], list.Items.Select(i => ((int)i.Enchant!, i.ItemLevel!.Value)));

        var inventory = new InventoryState();
        inventory.Apply(list);
        Assert.Equal(67 + 48 + 38, inventory.GearItemLevel);

        // A slot update without the item level keeps it while the enchant stays; a new enchant level forgets it
        inventory.Apply(new InventoryItemEvent(2_000, new InventoryItem(210630038, 1, InventoryItem.Equipped, 8, 5)));
        Assert.Equal(48u, inventory.Equipped.Single(i => i.Slot == 8).ItemLevel);
        inventory.Apply(new InventoryItemEvent(3_000, new InventoryItem(210630038, 1, InventoryItem.Equipped, 8, 6)));
        Assert.Null(inventory.Equipped.Single(i => i.Slot == 8).ItemLevel);
        Assert.Null(inventory.GearItemLevel);
    }

    private static GameSession Login(GameSession session, long ts, uint entity, string name)
    {
        session.Apply(new ZoneChangeEvent(ts));
        session.Apply(new SelfHintEvent(ts + 1, entity));
        session.Apply(new KinahLimitEvent(ts + 2, 4_000, 0, Initial: true));
        session.Apply(new ContentTicketsEvent(ts + 2, [new ContentTicket(4, 8)]));
        session.Apply(new SelfItemLevelEvent(ts + 3, 1419, 1419));
        session.Apply(new SelfIdentityEvent(ts + 10_000, entity, name, 1304, GameClass.Chanter, 45));
        return session;
    }

    [Fact]
    public void ProgressFollowsTheCharacterPlayed()
    {
        var session = Login(new GameSession(), 1_000, Me, "TestDaeva");
        session.Apply(new ExperienceEvent(20_000, 500, 500, 1));
        Assert.Equal("TestDaeva", session.Progress.Character);
        Assert.Equal(4_000ul, session.Progress.KinahToday);

        // The same character logging in again: the session's experience goes on
        Login(session, 30_000, 2000, "TestDaeva");
        Assert.Equal(500, session.Progress.SessionExp);

        // Another character at the character screen: its records arrive before its name and start over
        session.Apply(new ZoneChangeEvent(60_000));
        session.Apply(new SelfHintEvent(60_001, 3000));
        session.Apply(new KinahLimitEvent(60_002, 77, 0, Initial: true));
        Assert.Null(session.Progress.Character);
        Assert.Null(session.Progress.ItemLevel);
        session.Apply(new SelfIdentityEvent(70_000, 3000, "AltDaeva", 1304, GameClass.Cleric, 10));
        Assert.Equal(("AltDaeva", 77ul, 0L), (session.Progress.Character, session.Progress.KinahToday!.Value, session.Progress.SessionExp));
    }

    [Fact]
    public void BookKeepsEachCharacterAndFillsInWhatWasNotSentAgain()
    {
        var book = new ProgressBook();
        var first = new CharacterProgress { Character = "TestDaeva" };
        first.Apply(new AbyssPointLimitEvent(1_000, 146, 2_500_000));
        first.Apply(new SelfItemLevelEvent(1_000, 1419, 1419));
        book.Take(first);

        // A later session sent only the item level: the Abyss Points stay from before
        var later = new CharacterProgress { Character = "TestDaeva" };
        later.Apply(new SelfItemLevelEvent(5_000, 1437, 1437));
        var merged = book.Take(later)!;
        Assert.Equal((1437u, 146ul), (merged.ItemLevel!.Value, merged.AbyssPoints!.Value));
        Assert.Null(book.Take(new CharacterProgress())); // no name yet: nowhere to put it

        var back = JsonSerializer.Deserialize<ProgressBook>(JsonSerializer.Serialize(book))!;
        Assert.Equal(1437u, back.For("TestDaeva")!.ItemLevel);
        Assert.Null(back.For("AltDaeva"));
    }

    [Fact]
    public void CountsShowOnlyWhileTheirDayOrWeekLasts()
    {
        // Wednesday 2026-11-04 is a weekly reset; Europe resets at 16:00 server time
        var region = ServerRegion.Europe;
        var now = DateTimeOffset.Parse("2026-11-06T12:00:00Z");
        var day = ProgressResets.LastDaily(now, region);
        var week = ProgressResets.LastWeekly(now, region);
        Assert.Equal(TimeSpan.FromDays(1), ProgressResets.LastDaily(day.AddDays(1).AddMinutes(1), region) - day);
        Assert.Equal(DayOfWeek.Wednesday, week.ToOffset(GameSchedule.ServerOffset(region, week)).DayOfWeek);

        var progress = new CharacterProgress { Character = "TestDaeva" };
        progress.Apply(new KinahLimitEvent(day.AddMinutes(-1).ToUnixTimeMilliseconds(), 900_000, 0, true)); // yesterday
        progress.Apply(new AbyssPointLimitEvent(week.AddHours(1).ToUnixTimeMilliseconds(), 146, 2_500_000));
        progress.Apply(new ContentTicketsEvent(day.AddHours(1).ToUnixTimeMilliseconds(), [new ContentTicket(TicketIds.Odyle, 840, 530), new ContentTicket(TicketIds.Nightmare, 8)]));

        var stats = ProgressSummary.Stats(progress, now, region);
        Assert.DoesNotContain(stats, s => s.Label == "Kinah today");
        Assert.Contains(new ProgressStat("Abyss Points this week", "146", " / 2,500,000", "Starts over at the weekly reset"), stats);
        Assert.Contains(stats, s => s is { Label: "Odyle", Value: "840", Of: " +530" });
        Assert.Contains(stats, s => s is { Label: "Nightmare", Value: "8", Of: "" });
    }

    [Fact]
    public void ItemLevelSplitsIntoGearAndDaevanion()
    {
        var progress = new CharacterProgress();
        progress.Apply(new SelfItemLevelEvent(1_000, 388, 388));
        var level = Assert.Single(ProgressSummary.Stats(progress, DateTimeOffset.UnixEpoch.AddSeconds(1), ServerRegion.Europe, gearItemLevel: 309));
        Assert.Equal(("Item level", "388", "Gear 309 + Daevanion 79"), (level.Label, level.Value, level.Tip));
    }

    [Fact]
    public void ExperiencePerHourCountsLevelUps()
    {
        var progress = new CharacterProgress();
        progress.Apply(new ExperienceEvent(0, 9_000, 1_000, 1));
        progress.Apply(new ExperienceEvent(30 * 60_000, 500, 2_000, 1)); // a level up: the experience starts over
        Assert.Equal(1, progress.SessionLevelUps);
        Assert.Null(progress.ExpPerHour(60_000)); // too soon to tell
        Assert.Equal(4_500, progress.ExpPerHour(40 * 60_000)!.Value, 3);
        // Long after the last gain the rate is the one it had while it lasted
        Assert.Equal(6_000, progress.ExpPerHour(5 * 60 * 60_000)!.Value, 3);
    }

    [Fact]
    public void PetNamesAreLearnedFromTheSoulInTheBag()
    {
        var learner = new SoulItemLearner();
        Assert.Null(learner.Apply(new InventoryItemEvent(1_000, new InventoryItem(534_660_034, 1, InventoryItem.Cube, 40))));
        Assert.Equal((1034u, 534_660_034u), learner.Apply(new PetSoulsEvent(2_000, [new PetSouls(1034, 1)], Gained: true)));

        // Too long after, or two kinds at once: nothing learned
        learner.Apply(new InventoryItemEvent(3_000, new InventoryItem(534_660_117, 1, InventoryItem.Cube, 41)));
        Assert.Null(learner.Apply(new PetSoulsEvent(9_000, [new PetSouls(1219, 1)], Gained: true)));
        learner.Apply(new InventoryItemEvent(10_000, new InventoryItem(534_660_117, 1, InventoryItem.Cube, 41)));
        learner.Apply(new InventoryItemEvent(10_100, new InventoryItem(534_660_006, 1, InventoryItem.Cube, 42)));
        Assert.Null(learner.Apply(new PetSoulsEvent(11_000, [new PetSouls(1219, 1)], Gained: true)));

        var progress = new CharacterProgress { Character = "TestDaeva", Pets = [1001], Souls = { [1034] = 3, [1219] = 1 } };
        progress.SoulItems[1034] = 534_660_034;
        Assert.Equal("Pet souls: Zelophi 3/5 · Pet 1219 1/5 · 1 pet owned",
            ProgressSummary.Pets(progress, pet => progress.SoulItems.ContainsKey(pet) ? "Zelophi" : $"Pet {pet}"));
    }

    [Fact]
    public void RunCountsPotionsAndScrollsUsed()
    {
        var session = new GameSession();
        session.Apply(new SelfIdentityEvent(0, Me, "TestDaeva", 1304, GameClass.Chanter, 45));
        var ended = new List<DungeonRun>();
        session.Runs.RunEnded += ended.Add;
        session.Apply(new MapLoadEvent(10_000, 310042, 30));
        session.Apply(new ItemUsedEvent(20_000, 7, 162000045, FromBag: false));
        session.Apply(new ItemUsedEvent(20_500, 7, 162000045, FromBag: true)); // the same use, answered
        session.Apply(new ItemUsedEvent(320_000, 7, 162000045, FromBag: false));
        session.Apply(new ItemUsedEvent(330_000, 9, 162000046, FromBag: false));
        session.Apply(new MapLoadEvent(400_000, 1010, 0));
        session.Apply(new ZoneChangeEvent(401_000));

        var run = Assert.Single(ended);
        Assert.Equal([new RunItem(162000045, 2), new RunItem(162000046, 1)], run.Used);
    }

    [Fact]
    public void PetCountsWaitForTheRoster()
    {
        // A capture that began after the login: souls and a new pet arrive without the roster
        var progress = new CharacterProgress { Character = "TestDaeva" };
        progress.Apply(new PetSoulsEvent(1_000, [new PetSouls(1034, 3)], Gained: true));
        progress.Apply(new PetAcquiredEvent(2_000, 1, 1219, 1, 2));

        Assert.Null(progress.Pets);
        Assert.Empty(progress.Souls);
        Assert.Null(ProgressSummary.Pets(progress, id => $"Pet {id}"));

        progress.Apply(new PetRosterEvent(3_000, [1001], [new PetSouls(1034, 2)]));
        progress.Apply(new PetSoulsEvent(4_000, [new PetSouls(1034, 1)], Gained: true));
        progress.Apply(new PetAcquiredEvent(5_000, 1, 1219, 1, 2));

        Assert.Equal([1001u, 1219u], progress.Pets!.Order());
        Assert.Equal(3u, progress.Souls[1034]);
        Assert.Equal("Pet souls: Pet 1034 3/5 · 2 pets owned", ProgressSummary.Pets(progress, id => $"Pet {id}"));
    }
}
