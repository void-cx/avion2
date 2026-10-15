using Avion2.Analysis;
using Avion2.Protocol;

namespace Avion2.Tests;

public class TraceTests
{
    private const uint Me = 100;
    private const uint Ally = 101;
    private const uint Spirit = 500;
    private const uint Boss = 900;

    private static DamageEvent Hit(long ts, uint actor, long amount) =>
        new(ts, actor, Boss, 16040000, 16040000, amount, HitType.Normal, HitFlags.None, 1, 0);

    /// <summary>A short fight: we and an ally hit a boss that walks east, with a shield buff on us.</summary>
    private static (GameSession Session, FightTrace Trace) Fight()
    {
        var session = new GameSession(new GameData(
            new Dictionary<uint, string> { [16040000] = "Combustion", [11020000] = "Shield of Light" },
            new Dictionary<uint, NpcData> { [2100753] = new("Big Boss", IsBoss: true) }));
        session.Apply(new SelfIdentityEvent(0, Me, "TestDaeva", 1304, GameClass.Elementalist, 30));
        session.Apply(new PlayerSpawnEvent(0, Ally, "Friend", 1304, GameClass.Templar));
        session.Apply(new NpcSpawnEvent(0, Boss, 0x0C, 2100753, 100_000, null, new Vec3(10_000, 5_000, 2_000)));
        session.Apply(new PositionEvent(0, Boss, new Vec3(10_000, 5_000, 2_000), null, PositionSource.Spawn));
        session.Apply(new SummonLinkEvent(0, Spirit, Me));
        session.Apply(new LegionTagEvent(0, Ally, 1304, "Legion"));

        session.Apply(new PositionEvent(1_000, Me, new Vec3(9_000, 5_000, 2_000), null, PositionSource.Buff));
        session.Apply(new BuffEvent(1_000, Me, 7, 110200001, 11020000, Me, 4_000, Added: true, null));
        session.Apply(new SkillCastEvent(1_000, Me, 16040000, 16040001, Boss, null));
        session.Apply(Hit(1_100, Me, 5_000));
        session.Apply(new PositionEvent(2_000, Boss, new Vec3(11_000, 5_000, 2_000), 90, PositionSource.Move));
        session.Apply(Hit(2_500, Spirit, 1_000));
        session.Apply(Hit(3_000, Ally, 2_000));
        session.Apply(new HpUpdateEvent(3_000, Boss, 92_000, 100_000));
        session.Apply(new BuffRemovedEvent(3_000, Me, [7]));
        session.Apply(Hit(5_000, Me, 5_000));
        session.Tracker.Tick(60_000); // idle → the encounter ends

        var encounter = Assert.Single(session.Tracker.History);
        return (session, session.Trace(encounter));
    }

    [Fact]
    public void RecordsFacingHealsAndBackAttacks()
    {
        var session = new GameSession(new GameData(
            new Dictionary<uint, string> { [16040000] = "Combustion", [17010000] = "Healing Light", [99000000] = "Wave of Fury" },
            new Dictionary<uint, NpcData> { [2100753] = new("Big Boss", IsBoss: true) }));
        session.Apply(new SelfIdentityEvent(0, Me, "TestDaeva", 1304, GameClass.Elementalist, 30));
        session.Apply(new PlayerSpawnEvent(0, Ally, "Friend", 1304, GameClass.Cleric));
        session.Apply(new NpcSpawnEvent(0, Boss, 0x0C, 2100753, 100_000, null, new Vec3(10_000, 5_000, 2_000)));
        session.Apply(new SkillCastEvent(1_000, Me, 16040000, 16040001, Boss, null, Facing: 90));
        session.Apply(new DamageEvent(1_100, Me, Boss, 16040000, 16040001, 5_000, HitType.Critical, HitFlags.Critical | HitFlags.Back, 1, 0));
        session.Apply(new SkillCastEvent(1_500, Boss, 99000000, 99000000, Me, null, Facing: 180));
        session.Apply(new DamageEvent(1_600, Boss, Me, 99000000, 99000000, 3_000, HitType.Normal, HitFlags.None, 1, 0));
        session.Apply(new SkillCastEvent(1_900, Boss, 99000000, 99000000, Me, null)); // the same skill again: one moment
        session.Apply(new HealEvent(2_000, Ally, Me, 17010000, 2_500, false));
        session.Apply(new DamageEvent(3_000, Me, Boss, 16040000, 16040001, 5_000, HitType.Normal, HitFlags.None, 1, 0));
        session.Tracker.Tick(60_000);
        var trace = session.Trace(Assert.Single(session.Tracker.History));

        Assert.Equal(90f, trace.Casts.First(c => c.ActorId == Me).Facing);
        Assert.Equal(90f, trace.FacingAt(Me, trace.Casts.First(c => c.ActorId == Me).T + 500));
        var first = trace.Hits.First(h => h.ActorId == Me);
        Assert.True(first.Critical);
        Assert.True(first.Back);
        var heal = Assert.Single(trace.Heals);
        Assert.Equal((Ally, Me, 2_500L), (heal.ActorId, heal.TargetId, heal.Amount));

        var moments = trace.Moments(session.Registry.Data);
        var skill = Assert.Single(moments, m => m.Kind == MomentKind.BossSkill);
        Assert.Equal("Big Boss: Wave of Fury", skill.Text);
    }

    [Fact]
    public void CollectsParticipantsWithKindsOwnersAndLegions()
    {
        var (_, trace) = Fight();
        Assert.Equal(TraceEntityKind.Self, trace.Entity(Me)!.Kind);
        Assert.Equal(TraceEntityKind.Boss, trace.Entity(Boss)!.Kind);
        Assert.Equal("Big Boss", trace.Entity(Boss)!.Name);
        Assert.Equal(TraceEntityKind.Summon, trace.Entity(Spirit)!.Kind);
        Assert.Equal(Me, trace.OwnerOf(Spirit));
        Assert.Equal("Legion", trace.Entity(Ally)!.Legion);
        Assert.Equal(Boss, trace.PrimaryTargetId);
        Assert.Equal(4, trace.Hits.Count);
        Assert.Single(trace.Casts);
    }

    [Fact]
    public void InterpolatesPositionsAndFlagsStaleOnes()
    {
        var (_, trace) = Fight();
        int Start(long ms) => (int)(ms - trace.StartMs);

        // Boss walks 10_000 → 11_000 between t=0 and t=2s: halfway at 1s.
        var boss = trace.PositionAt(Boss, Start(1_000))!.Value;
        Assert.Equal(10_500, boss.X, 1);
        Assert.False(boss.Estimated);

        // Our only fix is at 1s: 4 s later it is an estimate.
        var me = trace.PositionAt(Me, Start(5_000))!.Value;
        Assert.Equal(9_000, me.X, 1);
        Assert.True(me.Estimated);
    }

    [Fact]
    public void MeasuresBuffUptimeAndNamesItFromItsSkill()
    {
        var (session, trace) = Fight();
        var shield = Assert.Single(trace.Uptime(Me, session.Registry.Data));
        Assert.Equal("Shield of Light", shield.Name);
        Assert.Equal(1, shield.Applications);
        // Up from 1 s to 3 s (removed early): 2 s of the trace.
        Assert.Equal(2_000.0 / trace.DurationMs, shield.Uptime, 3);
        Assert.Equal("self", shield.Caster);
    }

    [Fact]
    public void RemembersBossHp() => Assert.Equal(92_000u, Fight().Trace.HpAt(Boss, int.MaxValue));

    [Fact]
    public void LegionStateMergesRosterPages()
    {
        var legion = new LegionState();
        legion.Apply(new LegionDetailsEvent(1, "Legion", "Leader", ["Hello"]));
        legion.Apply(new LegionRosterEvent(2, [new LegionMember("A", 40, 100), new LegionMember("B", 41, 300)]));
        legion.Apply(new LegionRosterEvent(3, [new LegionMember("A", 42, 500)]));
        Assert.Equal(["A", "B"], legion.Members.Select(m => m.Name)); // newest login first
        Assert.Equal(42, legion.Members[0].Level);
    }
}

public class SummonOwnerTests
{
    private const uint Ranger = 200;
    private const uint Trap = 600;
    private const uint Boss = 900;
    private const uint Add = 901;

    private static GameSession Create()
    {
        var session = new GameSession(new GameData(
            new Dictionary<uint, string> { [14170000] = "Explosion Trap" },
            new Dictionary<uint, NpcData>
            {
                [2900001] = new("Explosion Trap", false),
                [2100753] = new("Big Boss", IsBoss: true),
                [2100754] = new("Add", false),
            }));
        session.Apply(new SelfIdentityEvent(0, 100, "TestDaeva", 1304, GameClass.Chanter, 30));
        session.Apply(new PlayerSpawnEvent(0, Ranger, "Hunter", 1304, GameClass.Ranger));
        session.Apply(new NpcSpawnEvent(0, Boss, 0x0C, 2100753, 100_000, null));
        session.Apply(new NpcSpawnEvent(0, Add, 0x0C, 2100754, 10_000, null));
        return session;
    }

    private static DamageEvent Hit(long ts, uint actor, uint target, long amount) =>
        new(ts, actor, target, 14170000, 14170000, amount, HitType.Normal, HitFlags.None, 1, 0);

    [Fact]
    public void TrapSpawnedRightAfterItsSkillIsCreditedToTheCaster()
    {
        var session = Create();
        session.Apply(new SkillCastEvent(1_000, Ranger, 14170000, 14170007, Ranger, null));
        session.Apply(new NpcSpawnEvent(1_400, Trap, 0x5F, 2900001, 1, null)); // no owner record
        session.Apply(Hit(2_000, Trap, Boss, 4_000));
        session.Apply(Hit(2_100, Ranger, Boss, 1_000));

        var row = Assert.Single(session.Snapshot(MeterScope.AllTargets)!.Rows);
        Assert.Equal("Hunter", row.Name);
        Assert.Equal(5_000, row.Total);
    }

    [Fact]
    public void MonstersAreNeverDamageRows()
    {
        var session = Create();
        session.Apply(Hit(1_000, Ranger, Add, 3_000));
        session.Apply(Hit(1_200, Boss, Add, 800)); // the boss hitting its own add

        var row = Assert.Single(session.Snapshot(MeterScope.AllTargets)!.Rows);
        Assert.Equal("Hunter", row.Name);
    }
}

public class OwnershipTests
{
    private static GameSession Create()
    {
        var session = new GameSession(new GameData(new Dictionary<uint, string>(), new Dictionary<uint, NpcData>
        {
            [2100456] = new("Red Cap Fungen", false),
            [2920110] = new("Fire Spirit", false),
        }));
        session.Apply(new SelfIdentityEvent(0, 100, "TestDaeva", 1304, GameClass.Chanter, 30));
        session.Apply(new PlayerSpawnEvent(0, 200, "Friend", 1304, GameClass.Elementalist));
        return session;
    }

    [Fact]
    public void ClaimedMonstersStayMonsters()
    {
        // Seen in a dungeon: 04 8D claims monsters for the player who hit them first. Their damage (e.g. a shield
        // absorbing it, reported as a tick with the shield's skill) must not become that player's damage.
        var session = Create();
        session.Apply(new NpcSpawnEvent(0, 700, 0x04, 2100456, 5_000, null));
        session.Apply(new OwnershipEvent(10, 700, 200));
        Assert.Equal(700u, session.Registry.ResolveOwner(700));
    }

    [Fact]
    public void ClaimedSummonsBelongToTheirOwnerEvenIfTheClaimCameFirst()
    {
        var session = Create();
        session.Apply(new OwnershipEvent(10, 701, 200));
        session.Apply(new NpcSpawnEvent(20, 701, 0x5F, 2920110, 1, null));
        Assert.Equal(200u, session.Registry.ResolveOwner(701));
    }
}

public class LagTests
{
    [Fact]
    public void ParsesTheServerClock()
    {
        var tick = Assert.IsType<ServerClockEvent>(Assert.Single(TestPackets.Parse("0036 5ce93a34a1010000")));
        Assert.Equal(1_791_877_638_492, tick.ServerMs);
    }

    [Fact]
    public void FindsServerFreezesAndDelaysInAFight()
    {
        var session = new GameSession();
        session.Apply(new SelfIdentityEvent(0, 100, "TestDaeva", 1304, GameClass.Chanter, 30));
        session.Apply(new NpcSpawnEvent(0, 900, 0x0C, null, 100_000, null));
        long server = 1_791_877_600_000;
        // Ticks every 50 ms arriving 20 ms late; a 7 s freeze at 3 s, after which the held ticks arrive at once.
        for (long t = 0; t <= 10_000; t += 50)
        {
            long arrival = t is > 3_000 and < 10_000 ? Math.Max(t, 10_000) + 20 : t + 20;
            if (t is > 3_000 and < 10_000 && t != 3_050)
            {
                continue; // collapse the burst to one late tick for simplicity
            }

            session.Apply(new ServerClockEvent(arrival, server + t));
        }

        session.Apply(new DamageEvent(1_000, 100, 900, 16040000, 16040000, 500, HitType.Normal, HitFlags.None, 1, 0));
        session.Apply(new DamageEvent(10_500, 100, 900, 16040000, 16040000, 500, HitType.Normal, HitFlags.None, 1, 0));
        session.Tracker.Tick(60_000);
        var trace = session.Trace(Assert.Single(session.Tracker.History));

        var lag = Assert.Single(trace.Lags);
        Assert.InRange(lag.Length, 6_900, 7_100);
        Assert.Equal(0, trace.DelayMs); // typical delay is the best case
        Assert.NotNull(trace.LagAt(lag.Start + 1_000));
        Assert.Contains("1 server freeze", trace.NetSummary);
    }
}

public class ReplayStateTests
{
    private static FightTrace Trace() => new()
    {
        DurationMs = 10_000,
        Entities =
        [
            new TraceEntity(1, "Mob", TraceEntityKind.Monster, GameClass.Unknown, null, null, 1000,
                [new TracePoint(0, 0, 0, 0, PositionSource.Spawn), new TracePoint(8_000, 500, 0, 0, PositionSource.Move)]),
        ],
        Deaths = [new TraceDeath(4_000, 1)],
    };

    [Fact]
    public void MonstersAreDeadAfterTheirDeathUntilSeenAgain()
    {
        var trace = Trace();
        Assert.False(trace.IsDeadAt(1, 3_999));
        Assert.True(trace.IsDeadAt(1, 5_000));
        Assert.False(trace.IsDeadAt(1, 8_500)); // the id moved again: alive (reused or respawned)
    }

    [Fact]
    public void FixAgeIsTheDistanceToTheClosestFix()
    {
        var trace = Trace();
        Assert.Equal(1_000, trace.FixAgeMs(1, 7_000));
        Assert.Equal(3_000, trace.FixAgeMs(1, 3_000));
        Assert.Null(trace.FixAgeMs(2, 0));
    }
}
