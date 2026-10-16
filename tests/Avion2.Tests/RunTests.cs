using Avion2.Analysis;
using Avion2.Protocol;
using Avion2.Protocol.Parsers;

namespace Avion2.Tests;

public class RunTests
{
    private const uint Me = 100;
    private const uint Boss = 900;
    private const uint Mob = 901;

    private static readonly DungeonCatalog Catalog = new([
        new DungeonInfo("deep-and-dark-pit", "Deep and Dark Pit", "Sealed Dungeon", 30, null, null, "Verteron",
            [new DungeonBoss("Seyron of the Lake", 2700988)], [new DungeonBoss("Pit Crawler", null)]),
        new DungeonInfo("cave-of-the-desperate", "Cave of the Desperate", "Sealed Dungeon", 30, null, null, "Verteron",
            [], [new DungeonBoss("Desperate Knight's Soul", null), new DungeonBoss("Desperate Ranger's Soul", null)]),
    ]);

    private static GameSession Session()
    {
        var session = new GameSession(new GameData(new Dictionary<uint, string> { [16040000] = "Combustion" },
            new Dictionary<uint, NpcData>
            {
                [2700988] = new("Seyron of the Lake", IsBoss: false), // not flagged: the dungeon table knows it
                [2700989] = new("Desperate Knight's Soul", IsBoss: false),
                [2701587] = new("Desperate Ranger's Soul", IsBoss: false),
            }));
        session.Runs.Catalog = Catalog;
        session.Apply(new SelfIdentityEvent(0, Me, "TestDaeva", 1304, GameClass.Chanter, 30));
        return session;
    }

    private static void Kill(GameSession session, long ts, uint id, uint npc)
    {
        session.Apply(new NpcSpawnEvent(ts - 10_000, id, 0x0C, npc, 100_000, null, new Vec3(0, 0, 0)));
        session.Apply(new DamageEvent(ts - 9_000, Me, id, 16040000, 16040000, 60_000, HitType.Normal, HitFlags.None, 1, 0));
        session.Apply(new DamageEvent(ts - 1_000, Me, id, 16040000, 16040000, 40_000, HitType.Normal, HitFlags.None, 1, 0));
        session.Apply(new DeathEvent(ts, id, DeathKind.Combat));
    }

    [Theory]
    [InlineData(310042u, 30, ContentKind.SealedDungeon)]
    [InlineData(400001u, 30, ContentKind.SealedDungeon)]
    [InlineData(600001u, 79, ContentKind.Expedition)]
    [InlineData(152003u, 37, ContentKind.Stronghold)]
    [InlineData(200001u, 80, ContentKind.Instance)]
    [InlineData(1010u, 0, ContentKind.OpenWorld)]
    public void ClassifiesMaps(uint map, byte code, ContentKind kind) => Assert.Equal(kind, Content.Classify(map, code));

    [Fact]
    public void RunFromEnteringToLeavingWithBossSplit()
    {
        var session = Session();
        var ended = new List<DungeonRun>();
        session.Runs.RunEnded += ended.Add;

        session.Apply(new MapLoadEvent(10_000, 310042, 30));
        Assert.NotNull(session.Runs.Current);
        Kill(session, 70_000, Boss, 2700988);
        session.Tracker.Tick(100_000); // the boss fight ends
        session.Apply(new MapLoadEvent(110_000, 1010, 0));
        Assert.Empty(ended); // ends at the zone change that follows
        session.Apply(new ZoneChangeEvent(112_000));

        var run = Assert.Single(ended);
        Assert.Null(session.Runs.Current);
        Assert.True(run.Left);
        Assert.Equal(ContentKind.SealedDungeon, run.Kind);
        Assert.Equal("deep-and-dark-pit", run.DungeonSlug);
        Assert.Equal(100_000, run.DurationMs);
        var boss = Assert.Single(run.Bosses);
        Assert.Equal("Seyron of the Lake", boss.Name);
        Assert.Equal(60_000, boss.AtMs);
        Assert.Equal(9_000, boss.FightMs);
        Assert.Equal(1, run.Fights);
        Assert.Equal(100_000, run.Damage);
    }

    [Fact]
    public void RunWithoutBossIsNamedByItsMonsters()
    {
        var session = Session();
        DungeonRun? run = null;
        session.Runs.RunEnded += r => run = r;
        session.Apply(new MapLoadEvent(0, 310010, 30));
        Kill(session, 20_000, Mob, 2700989);
        Kill(session, 40_000, Mob + 1, 2701587);
        session.Runs.Finish(50_000);

        Assert.NotNull(run);
        Assert.False(run.Left);
        Assert.Equal("cave-of-the-desperate", run.DungeonSlug);
        Assert.Empty(run.Bosses);
    }

    [Fact]
    public void InstanceChainIsOneRunAndPartyComesFromTheRoster()
    {
        var session = Session();
        session.Apply(new PartyRosterEvent(0, [
            new PartyMember(1, 1, "TestDaeva", GameClass.Chanter, true),
            new PartyMember(2, 2, "Friend", GameClass.Cleric, true),
        ]));
        session.Apply(new MapLoadEvent(1_000, 200001, 80));
        session.Apply(new MapLoadEvent(60_000, 210014, 72));
        session.Apply(new MapLoadEvent(120_000, 210015, 72));
        var run = session.Runs.Current;
        Assert.NotNull(run);
        Assert.Equal([200001u, 210014u, 210015u], run.Maps);
        Assert.Equal("Friend", Assert.Single(run.Party).Name);
    }

    [Fact]
    public void AnotherExpeditionStartsANewRunAndStalePartyIsDropped()
    {
        var session = Session();
        var ended = new List<DungeonRun>();
        session.Runs.RunEnded += ended.Add;
        session.Apply(new PartyRosterEvent(0, [new PartyMember(2, 2, "Gone", GameClass.Cleric, true)]));
        session.Apply(new MapLoadEvent(1_000, 600001, 79));
        session.Apply(new MapLoadEvent(300_000, 600031, 79)); // the exit in between was missed
        var first = Assert.Single(ended);
        Assert.Equal([600001u], first.Maps);
        Assert.Empty(first.Party); // never seen inside: an old party
        Assert.Equal(600031u, session.Runs.Current?.FirstMap);
    }

    [Fact]
    public void ParsesPartyRoster()
    {
        // header, then two entries (flag, slot, character id, 00 00, 2 bytes, name, class code, level) and an empty slot
        byte[] body = Convert.FromHexString(
            "05e9080000" +
            "0c01e65c03000000080903" + Convert.ToHexString("Ann"u8) + "24000000" + "2d000000" +
            "0e027367000000000f0903" + Convert.ToHexString("Bob"u8) + "08000000" + "2d000000" +
            "0003000000000000000000000000");
        var events = new List<GameEvent>();
        Assert.True(RecordParsers.ParsePartyRoster(body, 5, events.Add));
        var roster = Assert.IsType<PartyRosterEvent>(Assert.Single(events));
        Assert.Collection(roster.Members,
            m => Assert.Equal(("Ann", GameClass.Chanter, true), (m.Name, m.Class, m.Online)),
            m => Assert.Equal(("Bob", GameClass.Gladiator, false), (m.Name, m.Class, m.Online)));
    }
}
