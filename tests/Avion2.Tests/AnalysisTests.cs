using Avion2.Analysis;
using Avion2.Protocol;

namespace Avion2.Tests;

public class GameDataTests
{
    [Fact]
    public void LoadsA2ToolsTableFormat()
    {
        string dir = Directory.CreateTempSubdirectory("avion2-data").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "skills.json"), """{ "16040000": "Combustion", "1200012": "Burn" }""");
            File.WriteAllText(Path.Combine(dir, "npcs.json"), """
                { "2400032": { "name": "Training Scarecrow", "isBoss": true, "isDummy": true, "category": "x", "tier": 1, "dungeonId": 0 } }
                """);

            var data = GameData.Load(dir);
            Assert.Equal("Combustion", data.SkillName(16040000));
            Assert.Equal("Skill 99", data.SkillName(99));
            Assert.Equal(new NpcData("Training Scarecrow", IsBoss: true, IsDummy: true), data.Npcs[2400032]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class CombatTrackerTests
{
    private const uint Me = 100;
    private const uint Ally = 101;
    private const uint Stranger = 102;
    private const uint Spirit = 500;
    private const uint Boss = 900;
    private const uint OtherDummy = 901;

    private static DamageEvent Hit(long ts, uint actor, long amount, HitFlags flags = HitFlags.None, uint target = Boss, uint skill = 16040000) =>
        new(ts, actor, target, skill, skill, amount, flags.HasFlag(HitFlags.Critical) ? HitType.Critical : HitType.Normal, flags, 1, 15000);

    private static GameSession Create()
    {
        var session = new GameSession(new GameData(
            new Dictionary<uint, string>(),
            new Dictionary<uint, NpcData>
            {
                [2400032] = new("Training Scarecrow", IsBoss: true, IsDummy: true),
                [2100753] = new("Big Boss", IsBoss: true),
            }));
        session.Apply(new SelfIdentityEvent(0, Me, "Naicha", 1304, GameClass.Elementalist, 30));
        session.Apply(new PlayerSpawnEvent(0, Ally, "Friend", 1304));
        session.Apply(new PlayerSpawnEvent(0, Stranger, "Stranger", 1304));
        session.Apply(new NpcSpawnEvent(0, Boss, 0x04, 2100753, 1_000_000, null));
        session.Apply(new NpcSpawnEvent(0, OtherDummy, 0x04, 2400032, 1_000_000, null));
        return session;
    }

    [Fact]
    public void ComputesDpsSharesAndRates()
    {
        var session = Create();
        session.Apply(new SummonLinkEvent(0, Spirit, Me));
        session.Apply(Hit(1_000, Me, 1000, HitFlags.Critical | HitFlags.Back));
        session.Apply(Hit(2_000, Me, 1000));
        session.Apply(Hit(3_000, Spirit, 1000)); // credited to owner
        session.Apply(Hit(11_000, Ally, 1000));

        var snapshot = session.Snapshot()!;
        Assert.Equal("Big Boss", snapshot.Title);
        Assert.Equal(10, snapshot.DurationSeconds);
        var me = snapshot.Rows[0];
        Assert.True(me.IsSelf);
        Assert.Equal(3000, me.Total);
        Assert.Equal(300, me.Dps);
        Assert.Equal(0.75, me.Share);
        Assert.Equal(1.0 / 3, me.CritRate, 3);
        Assert.Equal(GameClass.Elementalist, me.Class);
    }

    [Fact]
    public void SummonLinkArrivingAfterHitsStillMerges()
    {
        var session = Create();
        session.Apply(Hit(1_000, Me, 1000));
        session.Apply(Hit(1_500, Spirit, 500));
        session.Apply(new SummonLinkEvent(2_000, Spirit, Me));

        var row = Assert.Single(session.Snapshot()!.Rows);
        Assert.Equal(1500, row.Total);
    }

    [Fact]
    public void MyTargetsScopeIgnoresStrangersOnOtherTargets()
    {
        var session = Create();
        session.Apply(Hit(1_000, Me, 1000));
        session.Apply(Hit(1_200, Ally, 400));                        // same target: counts
        session.Apply(Hit(1_500, Stranger, 9999, target: OtherDummy)); // someone else's dummy

        var mine = session.Snapshot()!;
        Assert.Equal(1400, mine.TotalDamage);
        Assert.DoesNotContain(mine.Rows, r => r.Name == "Stranger");

        var all = session.Snapshot(MeterScope.AllTargets)!;
        Assert.Contains(all.Rows, r => r.Name == "Stranger");
    }

    [Fact]
    public void SplitsEncountersOnIdle()
    {
        var session = Create();
        session.Apply(Hit(1_000, Me, 10));
        session.Apply(Hit(1_000 + 16_000, Me, 10));
        Assert.Single(session.Tracker.History);
        Assert.Equal(EncounterEnd.Idle, session.Tracker.History[0].End);
    }

    [Fact]
    public void BossDeathEndsEncounterButDummyDoesNot()
    {
        var session = Create();
        session.Apply(Hit(1_000, Me, 10, target: OtherDummy));
        session.Apply(new DeathEvent(1_500, OtherDummy, DeathKind.Combat));
        Assert.NotNull(session.Tracker.Current);

        session.Apply(Hit(2_000, Me, 10));
        session.Apply(new DeathEvent(2_500, Boss, DeathKind.Combat));
        Assert.Null(session.Tracker.Current);
        var encounter = session.Tracker.History.Single();
        Assert.Equal(EncounterEnd.BossKilled, encounter.End);
        Assert.True(encounter.Targets[Boss].Killed);
    }

    [Fact]
    public void MonsterDamageIsDamageTakenNotDps()
    {
        var session = Create();
        session.Apply(Hit(1_000, Me, 100));
        session.Apply(Hit(1_500, Boss, 5000, target: Me, skill: 2_100_010));
        var me = Assert.Single(session.Snapshot()!.Rows);
        Assert.Equal(100, me.Total);
        Assert.Equal(5000, me.DamageTaken);
        Assert.Equal(EntityKind.Npc, session.Registry.Find(Boss)!.Kind);
    }

    [Fact]
    public void ZoneChangeFreezesNamesAndForgetsIds()
    {
        var session = Create();
        session.Apply(new SummonLinkEvent(0, Spirit, Me));
        session.Apply(Hit(1_000, Me, 100));
        session.Apply(Hit(1_100, Spirit, 50));
        session.Apply(new ZoneChangeEvent(2_000));

        Assert.Null(session.Tracker.Current);
        Assert.Null(session.Registry.SelfId);
        Assert.Equal("Naicha", session.Registry.SelfName);

        var final = session.Tracker.History.Single().Final!;
        Assert.Equal("Big Boss", final.Title);
        var me = Assert.Single(final.Rows);
        Assert.Equal("Naicha", me.Name);
        Assert.Equal(150, me.Total);
    }
}

public class KnownCharacterTests
{
    private static EntityRegistry Create()
    {
        var registry = new EntityRegistry();
        registry.SetKnownCharacters(
        [
            new KnownCharacter("TestDaeva", GameClass.Chanter, 1321, DateTimeOffset.UtcNow.AddDays(-1)),
            new KnownCharacter("AltGlad", GameClass.Gladiator, 1321, DateTimeOffset.UtcNow),
        ]);
        return registry;
    }

    [Fact]
    public void GuessesMostRecentCharacterUntilClassIsKnown()
    {
        var registry = Create();
        registry.Apply(new SelfHintEvent(0, 10545));
        Assert.Equal("AltGlad", registry.SelfName);
        Assert.True(registry.SelfNameIsGuess);

        // A Chanter skill reveals which character we are.
        registry.Apply(new DamageEvent(1, 10545, 900, 18010000, 18010000, 100, HitType.Normal, HitFlags.None, 1, 0));
        Assert.Equal("TestDaeva", registry.SelfName);
        Assert.Equal(GameClass.Chanter, registry.SelfClass);
    }

    [Fact]
    public void NoGuessForUnconfirmedClass()
    {
        var registry = Create();
        registry.Apply(new SelfHintEvent(0, 10545));
        registry.Apply(new DamageEvent(1, 10545, 900, 17010000, 17010000, 100, HitType.Normal, HitFlags.None, 1, 0));
        Assert.Null(registry.SelfName); // a Cleric we have never confirmed
        Assert.Equal(GameClass.Cleric, registry.SelfClass);
    }

    [Fact]
    public void IdentityRecordWinsAndIsReported()
    {
        var registry = Create();
        KnownCharacter? reported = null;
        registry.CharacterIdentified += c => reported = c;
        registry.Apply(new SelfIdentityEvent(0, 10545, "NewChar", 1321, GameClass.Ranger, 10));
        Assert.Equal("NewChar", registry.SelfName);
        Assert.False(registry.SelfNameIsGuess);
        Assert.Equal("NewChar", reported?.Name);
    }

    [Fact]
    public void PlaceholderNpcNamesAreNeverShown()
    {
        var data = new GameData(new Dictionary<uint, string>(), new Dictionary<uint, NpcData>
        {
            [2090012] = new("???", false),
            [2000002] = new("Draconute Ranger", false),
        });
        var registry = new EntityRegistry(data);
        registry.Apply(new NpcSpawnEvent(0, 100, 0x5F, 2090012, null, null));
        registry.Apply(new NpcSpawnEvent(0, 101, 0x0C, 2090012, null, null));
        registry.Apply(new NpcSpawnEvent(0, 102, 0x0C, 2000002, null, null));

        Assert.Equal("Summon 2090012", registry.DisplayName(100));
        Assert.Equal("NPC 2090012", registry.DisplayName(101));
        Assert.Equal("Draconute Ranger", registry.DisplayName(102));
    }
}
