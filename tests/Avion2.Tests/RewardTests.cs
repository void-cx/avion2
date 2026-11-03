using System.Text.Json;
using Avion2.Analysis;
using Avion2.Engine;
using Avion2.Protocol;

namespace Avion2.Tests;

public class RewardTests
{
    // 12 57 at a world entry after the third Vakron Sky Island (Conquest, map 600072) clear of 2026-11-02
    private const string CeilingInfoHex = "1257" + "05" + "c1270900" + "0200" + "c2270900" + "0001" + "d5270900" + "0101" +
        "df270900" + "0001" + "08280900" + "0600";

    // 13 57, half a second after the Vakron Sky Island clear: its two mid bosses
    private const string RewardStartHex = "1357" + "02" + "8a1b2300" + "8b1b2300";

    // 15 57 of a Krao Cave Exploration cube opened double (2026-11-03): result 0, 2 shares, 9 items
    private const string CubeRewardHex = "1557" + "0000" + "02" + "09" +
        "37337037" + "d0070000" + "21337037" + "a0860100" + "29dc9606" + "01000000" + "b688890c" + "01000000" +
        "1602880c" + "01000000" + "c8a2860c" + "01000000" + "d6489b06" + "01000000" + "c2da9925" + "08000000" +
        "d1f26324" + "01000000";

    // 17 57: the Krao Cave Exploration guaranteed reward claimed (Red Agate Breastplate), 1 point left, claimed once
    private const string CeilingRewardHex = "1757" + "0002" + "c2270900" + "767b860c" + "01000000" + "01" + "01";

    private static List<GameEvent> Parse(string hex)
    {
        var events = new List<GameEvent>();
        Assert.True(PacketParser.Parse(Convert.FromHexString(hex), 1_000, events.Add));
        return events;
    }

    private static T Single<T>(string hex) where T : GameEvent => Assert.IsType<T>(Assert.Single(Parse(hex)));

    private static void Rejected(string hex)
    {
        var events = new List<GameEvent>();
        Assert.False(PacketParser.Parse(Convert.FromHexString(hex), 0, events.Add));
        Assert.Empty(events);
    }

    [Fact]
    public void CeilingInfoListsEveryExpedition()
    {
        var info = Single<CubeCeilingInfoEvent>(CeilingInfoHex);
        Assert.Equal(
            [new(600001, 2, 0), new(600002, 0, 1), new(600021, 1, 1), new(600031, 0, 1), new CubeCeiling(600072, 6, 0)],
            info.Dungeons);
    }

    [Fact]
    public void RewardStartNamesTheMidBosses()
    {
        Assert.Equal([2300810u, 2300811u], Single<CubeRewardStartEvent>(RewardStartHex).NpcCodes);
        Assert.Equal(2310104u, Single<MiddleBossRewardEvent>("2e57d83f2300").NpcCode);
    }

    [Fact]
    public void CubeRewardListsItemsWithCounts()
    {
        var reward = Single<CubeRewardEvent>(CubeRewardHex);
        Assert.Equal(0, reward.Result);
        Assert.Equal(2, reward.Shares);
        Assert.Equal(9, reward.Items.Count);
        Assert.Equal(new RewardItem(ItemIds.EnhanceStone, 2000), reward.Items[0]);
        Assert.Equal(new RewardItem(ItemIds.KinahBound, 100_000), reward.Items[1]);
        Assert.Equal(new RewardItem(630840002, 8), reward.Items[7]);
    }

    [Fact]
    public void CeilingRewardCarriesWhatIsLeft()
    {
        var claim = Single<CubeCeilingRewardEvent>(CeilingRewardHex);
        Assert.Equal(600002u, claim.DungeonId);
        Assert.Equal(new RewardItem(210140022, 1), claim.Item);
        Assert.Equal(1u, claim.PointsLeft);
        Assert.Equal(1u, claim.Claims);
    }

    [Fact]
    public void GroupRequestsAndPointCounters()
    {
        var progressed = Single<GroupRequestProgressedEvent>("02e0" + "d4de000000000000" + "df170000");
        Assert.Equal(57044ul, progressed.Key);
        Assert.Equal(6111u, progressed.EntityId);
        Assert.Equal(57044ul, Single<GroupRequestCompleteEvent>("04e0" + "d4de000000000000").Key);

        var daevanion = Single<DaevanionPointsEvent>("2ee2" + "05" + "013900" + "020000" + "030000" + "040000" + "050000");
        Assert.False(daevanion.FromItems);
        Assert.Equal(new PointCount(1, 57), daevanion.Points[0]);
        Assert.Equal(5, daevanion.Points.Count);
        Assert.True(Single<DaevanionPointsEvent>("2fe2" + "05" + "010000" + "020000" + "030000" + "040000" + "050000").FromItems);

        Assert.Equal([new PointCount(1, 182)], Single<SkillPointsEvent>("0551" + "01" + "01b600").Points);
    }

    [Fact]
    public void TruncatedOrPaddedRecordsAreRejected()
    {
        Rejected(CeilingInfoHex[..^2]);
        Rejected(CeilingInfoHex + "00");
        Rejected(CubeRewardHex[..^2]);
        Rejected(CeilingRewardHex + "00");
        Rejected("2e57d83f23");
        Rejected("0551" + "01" + "01b6");
    }

    private static CubeTable Cubes => BundledGameData.Cubes;

    [Fact]
    public void BundledCubeTableHasTheGuaranteedRewards()
    {
        var krao = Cubes.ByNpc(2310104); // a Krao Cave Exploration mid boss
        Assert.NotNull(krao);
        Assert.Equal("KraoCave/explore", krao.Key);
        Assert.Equal("Krao Cave · Exploration", krao.Title);
        Assert.Equal(20, krao.Odyle);
        Assert.Equal(1, krao.Ceiling!.After);
        Assert.Equal(1, krao.Ceiling.Claims);
        Assert.Contains(210140022u, krao.Ceiling.Items);

        var vakron = Cubes.ByNpc(2300810);
        Assert.Equal("BakronIsland/conquest", vakron!.Key);
        Assert.Equal("vakron-sky-island", vakron.Slug);
        Assert.Equal(21, vakron.Ceiling!.After);
        Assert.Same(vakron, Cubes.ByKey("BakronIsland/conquest"));
        Assert.Null(Cubes.ByNpc(1));
    }

    [Fact]
    public void CountersWaitForTheCharacterTheyBelongTo()
    {
        var tracker = new RewardTracker { Cubes = Cubes };
        int changes = 0;
        tracker.Changed += () => changes++;
        tracker.Apply(Single<CubeCeilingInfoEvent>(CeilingInfoHex));
        Assert.Null(tracker.Ceilings.Character); // sent before the game names us
        Assert.Equal(0, changes);

        tracker.Apply(new SelfIdentityEvent(2_000, 7, "Alt", 1, GameClass.Ranger, 30));
        Assert.Equal("Alt", tracker.Ceilings.Character);
        Assert.Equal(5, tracker.Ceilings.Dungeons.Count);
        Assert.Equal(1, changes);

        // the next identity without a new list changes nothing
        tracker.Apply(new SelfIdentityEvent(3_000, 7, "Alt", 1, GameClass.Ranger, 30));
        Assert.Equal(1, changes);

        tracker.Apply(Single<CubeCeilingRewardEvent>(CeilingRewardHex));
        Assert.Equal(new CeilingEntry(600002, 1, 1), tracker.Ceilings.Dungeons[600002]);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void TheCubesBossesTellWhichCubeAMapHas()
    {
        var tracker = new RewardTracker { Cubes = Cubes };
        tracker.Apply(Single<CubeRewardStartEvent>(RewardStartHex)); // no map yet: nothing learned
        Assert.Empty(tracker.LearnedMaps);

        tracker.Apply(new MapLoadEvent(1_000, 600072, 79));
        tracker.Apply(Single<CubeRewardStartEvent>(RewardStartHex));
        Assert.Equal("BakronIsland/conquest", tracker.LearnedMaps[600072]);

        tracker.Apply(new MapLoadEvent(2_000, 1010));
        tracker.Apply(Single<MiddleBossRewardEvent>("2e57d83f2300")); // open world: not an expedition map
        Assert.Single(tracker.LearnedMaps);
    }

    [Fact]
    public void ProgressSaysHowFarTheGuaranteedRewardIs()
    {
        var book = new CeilingBook();
        Assert.True(book.Take(new CeilingState
        {
            Character = "Alt",
            Dungeons = new() { [600002] = new(600002, 2, 0), [600072] = new(600072, 6, 0), [600021] = new(600021, 1, 1) },
        }));
        book.Learn(600002, "KraoCave/explore");
        book.Learn(600072, "BakronIsland/conquest");
        var list = book.Progress("Alt", Cubes);
        Assert.Equal([600002u, 600021u, 600072u], list.Select(p => p.DungeonId));

        var krao = list[0];
        Assert.True(krao.Ready);
        Assert.Equal("2 / 1", krao.Value);
        Assert.Equal("Ready to claim", krao.Status);

        var unknown = list[1];
        Assert.Null(unknown.Cube);
        Assert.Null(unknown.Needed);
        Assert.False(unknown.Ready);
        Assert.Equal("1 point", unknown.Value);
        Assert.Equal("Expedition 600021", unknown.Title);

        var vakron = list[2];
        Assert.False(vakron.Ready);
        Assert.Equal(15, vakron.ToGo);
        Assert.Equal("6 / 21", vakron.Value);
        Assert.Equal("15 to go", vakron.Status);

        // claimed as often as the table allows: no more to go
        var claimed = new CeilingProgress(600002, Cubes.ByKey("KraoCave/explore"), 1, 1);
        Assert.True(claimed.AllClaimed);
        Assert.False(claimed.Ready);
        Assert.Null(claimed.ToGo);
        Assert.Equal("Claimed", claimed.Value);
        Assert.Equal("Claimed 1 of 1", claimed.Status);

        Assert.Empty(book.Progress("Main", Cubes));
        Assert.False(book.Take(new CeilingState()));
    }

    [Fact]
    public void BookLearnsMapsFromSavedRunsAndRoundTrips()
    {
        var book = new CeilingBook();
        var runs = new[]
        {
            new DungeonRun { Maps = [600002], Bosses = [new RunBoss("Boss", 2310171, 60_000)] },
            new DungeonRun { Maps = [310042], Bosses = [new RunBoss("Boss", 2310171, 60_000)] }, // not an expedition
            new DungeonRun { Maps = [600072], Bosses = [new RunBoss("Unknown", 1, 60_000)] }, // no cube boss
        };
        Assert.Equal(1, book.Learn(runs, Cubes));
        Assert.Equal(0, book.Learn(runs, Cubes)); // known already
        Assert.Equal("KraoCave/explore", book.CubeOf(600002));
        Assert.Null(book.CubeOf(600072));

        book.Take(new CeilingState { Character = "Alt", Dungeons = new() { [600002] = new(600002, 2, 0) }, UpdatedMs = 5 });
        var back = JsonSerializer.Deserialize<CeilingBook>(JsonSerializer.Serialize(book))!;
        Assert.Equal("KraoCave/explore", back.CubeOf(600002));
        Assert.Equal(new CeilingEntry(600002, 2, 0), back.For("Alt")!.Dungeons[600002]);
        Assert.Equal(5, back.For("Alt")!.UpdatedMs);
    }

    [Fact]
    public void ARunKeepsWhatItsCubeGave()
    {
        var session = new GameSession();
        session.Apply(new MapLoadEvent(10_000, 600002, 79));
        session.Apply(new CubeRewardEvent(20_000, 0, 2, [new RewardItem(ItemIds.Kinah, 600_000), new RewardItem(630840002, 8)]));
        session.Apply(new CubeRewardEvent(21_000, 5, 1, [new RewardItem(630840002, 3)])); // a failed reply adds nothing
        var run = session.Runs.Current!;
        Assert.Equal(2, run.Cube!.Shares);
        Assert.Equal([new RunItem(ItemIds.Kinah, 600_000), new RunItem(630840002, 8)], run.Cube.Items);

        // a second opening in the same run adds up; the cube survives the saved copy
        session.Apply(new CubeRewardEvent(22_000, 0, 1, [new RewardItem(630840002, 3)]));
        var copy = session.Runs.Current!.Copy();
        Assert.Equal(3, copy.Cube!.Shares);
        Assert.Equal(11, copy.Cube.Items.Single(i => i.ItemId == 630840002).Count);
    }
}
