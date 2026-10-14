using Avion2.Analysis;
using Avion2.Engine;
using Avion2.Protocol;
using static Avion2.Tests.TestPackets;

namespace Avion2.Tests;

public class CombatClockTests
{
    private const uint Me = 10545;
    private const uint Dummy = 900;

    private static GameSession Create()
    {
        var session = new GameSession();
        session.Apply(new SelfHintEvent(0, Me));
        session.Apply(new NpcSpawnEvent(0, Dummy, 0x04, 2400032, 1_000_000, null));
        return session;
    }

    private static DamageEvent Hit(long ts, long amount) =>
        new(ts, Me, Dummy, 18010000, 18010000, amount, HitType.Normal, HitFlags.None, 1, 0);

    [Fact]
    public void ParsesCombatToggle()
    {
        var on = Assert.IsType<CombatStateEvent>(Assert.Single(Parse("218D b152 00 01")));
        Assert.Equal(10545u, on.EntityId);
        Assert.True(on.InCombat);
        Assert.False(Assert.IsType<CombatStateEvent>(Assert.Single(Parse("218D b152 00 00"))).InCombat);
    }

    [Fact]
    public void DurationRunsFromCombatOnToCombatOff()
    {
        // Mirrors the dummy test: combat on 0.15 s before the first hit, off ~3.5 s after the last.
        var session = Create();
        session.Apply(new CombatStateEvent(9_850, Me, true));
        session.Apply(Hit(10_000, 60_000));
        session.Apply(Hit(20_000, 0));
        session.Apply(Hit(30_000, 60_000));
        session.Apply(Hit(36_400, 60_000));
        session.Apply(new CombatStateEvent(40_250, Me, false));

        var snapshot = session.Snapshot()!;
        Assert.Equal(30.4, snapshot.DurationSeconds, 1);
        Assert.Equal(180_000 / 30.4, snapshot.Rows[0].Dps, 0);
    }

    [Fact]
    public void ClockTicksWhileInCombat()
    {
        var session = Create();
        session.Apply(new CombatStateEvent(9_900, Me, true));
        session.Apply(Hit(10_000, 1000));
        Assert.Equal(5.1, session.Snapshot(nowMs: 15_000)!.DurationSeconds, 1);
    }

    [Fact]
    public void WithoutCombatStateFallsBackToHits()
    {
        var session = Create();
        session.Apply(Hit(10_000, 1000));
        session.Apply(Hit(14_000, 1000));
        Assert.Equal(4, session.Snapshot(nowMs: 20_000)!.DurationSeconds, 1);
    }
}

public class IncomingDamageTests
{
    private const uint Me = 10545;
    private const uint Mob = 61796;

    [Fact]
    public void BreaksDownDamageTakenWithMitigation()
    {
        var session = new GameSession(new GameData(
            new Dictionary<uint, string> { [1222770] = "Bite" },
            new Dictionary<uint, NpcData> { [2100753] = new("Highland Odyle Sparkie", false) }));
        session.Apply(new SelfIdentityEvent(0, Me, "TestDaeva", 1321, GameClass.Chanter, 45));
        session.Apply(new NpcSpawnEvent(0, Mob, 0x04, 2100753, 119_700, null));

        // Being hit first starts the encounter.
        session.Apply(Parse("0438 b152 46 00 e4e203 72a81200 01 02 00 00 02 93cc4907 01000000 904e de03 01 13b7a36f 01 00")[0]);
        session.Apply(new DamageEvent(1_000, Mob, Me, 1222770, 1222770, 600, HitType.Critical, HitFlags.Critical, 1, 10000));
        session.Apply(new DamageEvent(2_000, Mob, Me, 1222770, 1222770, 100, HitType.Normal, HitFlags.Parry, 1, 10000));

        var snapshot = session.Snapshot()!;
        var me = Assert.Single(snapshot.Incoming);
        Assert.True(me.IsSelf);
        Assert.Equal(478 + 600 + 100, me.Total);
        Assert.Equal(1, me.Crits);
        Assert.Equal(1, me.Parried);
        Assert.Equal(1, me.Shielded);
        var source = Assert.Single(me.Sources);
        Assert.Equal("Highland Odyle Sparkie", source.Attacker);
        Assert.Equal("Bite", source.Skill);
        Assert.Equal(3, source.Hits);
    }

    [Fact]
    public void ShieldAbsorptionIsNotDamageTaken()
    {
        var session = new GameSession();
        session.Apply(new SelfHintEvent(0, Me));
        session.Apply(new NpcSpawnEvent(0, Mob, 0x04, 2100753, 119_700, null));

        // Live pair: heal 297 on us + a "tick" from the mob carrying our Protection Circle (18730002).
        foreach (var e in Parse("0538 b152 09 b152 61 13b7a36f a902 12cc1d01").Concat(Parse("0538 b152 0a e4e203 61 13b7a36f a902 12cc1d01")))
        {
            session.Apply(e);
        }

        session.Apply(new DamageEvent(1_000, Mob, Me, 1222770, 1222770, 600, HitType.Normal, HitFlags.None, 1, 10000));

        var me = Assert.Single(session.Snapshot()!.Incoming);
        Assert.Equal(600, me.Total);
        Assert.Equal(297, me.Absorbed);
    }
}

public class OwnHpTests
{
    [Fact]
    public void OnlyResourceZeroIsHp()
    {
        Assert.IsType<HpUpdateEvent>(Assert.Single(Parse("008D b152 020100 e1210000 00000000")));
        Assert.Empty(Parse("008D b152 010101 b0090000"));      // MP-like gauge
        Assert.Empty(Parse("008D b152 010103 f0cb0100"));      // other gauge (127600)
    }

    [Fact]
    public void TracksOwnHpAndRemembersObservedMax()
    {
        var registry = new EntityRegistry();
        registry.SetKnownCharacters([new KnownCharacter("TestDaeva", GameClass.Chanter, 1321, DateTimeOffset.UtcNow, MaxHp: 9016)]);
        registry.Apply(new SelfHintEvent(0, 10545));
        registry.Apply(new HpUpdateEvent(1, 10545, 4000, 0));
        Assert.Equal(4000u, registry.SelfHp);
        Assert.Equal(9016u, registry.SelfMaxHp);   // from the earlier session

        registry.Apply(new HpUpdateEvent(2, 10545, 9500, 0));
        Assert.Equal(9500u, registry.SelfMaxHp);   // a buff or level-up raised it
    }

    [Fact]
    public void LowHpAlertFiresOnceUntilRecovered()
    {
        var monitor = new LowHpMonitor { Threshold = 0.3, CooldownMs = 1000 };
        Assert.False(monitor.Update(9000, 9000, 0));
        Assert.True(monitor.Update(2500, 9000, 100));    // 27.8 %
        Assert.False(monitor.Update(2000, 9000, 5000));  // still low: no repeat
        Assert.False(monitor.Update(2900, 9000, 6000));  // 32 %: not yet re-armed (needs 35 %)
        Assert.False(monitor.Update(3300, 9000, 7000));  // 36.7 %: re-armed
        Assert.True(monitor.Update(2000, 9000, 8000));
        Assert.False(monitor.Update(0, 9000, 20000));    // dead: no alert
    }
}

public class HistoryStoreTests
{
    [Fact]
    public void SavesLoadsAndSummarizes()
    {
        string dir = Directory.CreateTempSubdirectory("avion2-history").FullName;
        try
        {
            var store = new HistoryStore(dir);
            var snapshot = new MeterSnapshot(
                1, "Training Scarecrow", 30.4, 180_002, 5921, null, null,
                [new MeterRow(10545, "TestDaeva", GameClass.Chanter, true, 180_002, 5921, 1, 0.02, 0.19, 104, 0, 0, 0, 0, [])],
                StartedAtMs: 1_791_877_743_597,
                End: EncounterEnd.Idle);

            var saved = store.Save(snapshot)!;
            Assert.True(File.Exists(saved.Path));

            var loaded = Assert.Single(store.Load());
            Assert.Equal("Training Scarecrow", loaded.Snapshot.Title);
            Assert.Equal(GameClass.Chanter, loaded.Snapshot.Rows[0].Class);
            Assert.Empty(loaded.Snapshot.Incoming);

            string summary = HistoryStore.Summarize(loaded.Snapshot);
            Assert.Contains("Training Scarecrow · 00:30 · 5921/s", summary);
            Assert.Contains("1. TestDaeva (Chanter)  5921/s  180.0K  100.0%", summary);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DeletesFightsOlderThanACutoffWithTheirReplays()
    {
        string dir = Directory.CreateTempSubdirectory("avion2-history").FullName;
        try
        {
            var store = new HistoryStore(dir);
            MeterSnapshot Fight(string title, long startMs) => new(
                1, title, 30, 1000, 33, null, null,
                [new MeterRow(1, "TestDaeva", GameClass.Chanter, true, 1000, 33, 1, 0, 0, 1, 0, 0, 0, 0, [])],
                StartedAtMs: startMs, End: EncounterEnd.Idle);

            var old = store.Save(Fight("Old fight", 1_000_000))!;
            var recent = store.Save(Fight("Recent fight", 2_000_000))!;
            File.WriteAllText(HistoryStore.TracePath(old.Path), "{}");
            File.SetLastWriteTime(old.Path, DateTime.Now.AddDays(-40));

            Assert.Equal(2, store.Usage().Fights);
            Assert.Equal(1, store.DeleteOlderThan(DateTime.Now.AddDays(-30)));
            Assert.False(File.Exists(old.Path));
            Assert.False(File.Exists(HistoryStore.TracePath(old.Path)));
            Assert.True(File.Exists(recent.Path));
            Assert.Equal("Recent fight", Assert.Single(store.Load()).Snapshot.Title);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BestFightPerBossIgnoresTheTargetSuffix()
    {
        static HistoryEntry Fight(string title, double seconds, double myDps) => new(title + seconds, new MeterSnapshot(
            1, title, seconds, 1000, myDps, null, null,
            [new MeterRow(1, "TestDaeva", GameClass.Chanter, true, (long)(myDps * seconds), myDps, 1, 0, 0, 1, 0, 0, 0, 0, [])],
            StartedAtMs: 1, End: EncounterEnd.BossKilled));

        Assert.Equal("Air Raiders Captain Tetrata", HistoryStore.BossOf("Air Raiders Captain Tetrata (+25)"));
        var best = HistoryStore.BestPerBoss([
            Fight("Tetrata (+25)", 120, 3_000),
            Fight("Tetrata (+3)", 90, 4_500),   // same boss, better
            Fight("Tetrata", 5, 9_000),         // too short to count
            Fight("Combat", 60, 8_000),         // not a boss
            Fight("Kaira", 60, 2_000),
        ]);

        Assert.Equal(["Tetrata (+3)", "Kaira"], best.Select(b => b.Snapshot.Title));
    }
}
