using Avion2.Analysis;
using Avion2.Engine;
using Avion2.Protocol;
using static Avion2.Tests.TestPackets;

namespace Avion2.Tests;

/// <summary>Stagger gauge (<c>05 E0</c>), NPC casts (<c>09 38</c> / <c>0C 38</c>), telegraphs (<c>48 38</c>) and dodges.</summary>
public class MechanicsTests
{
    private const uint Me = 100;
    private const uint Ally = 101;
    private const uint Boss = 900;
    private const uint Thunder = 11_020_000;   // a skill whose hits take stagger
    private const uint Strike = 11_030_000;    // another one
    private const uint Quake = 1_230_790;      // the boss's skill (NPC skills have 7 digits)

    private static string Var(uint value)
    {
        var bytes = new List<byte>();
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            bytes.Add(value != 0 ? (byte)(b | 0x80) : b);
        }
        while (value != 0);
        return Convert.ToHexString(bytes.ToArray());
    }

    private static string U32(uint value) => Convert.ToHexString(BitConverter.GetBytes(value));

    private static string U16(ushort value) => Convert.ToHexString(BitConverter.GetBytes(value));

    private static string F32(float value) => Convert.ToHexString(BitConverter.GetBytes(value));

    private static string Pos(float x, float y, float z) => F32(x) + F32(y) + F32(z);

    [Fact]
    public void ParsesTheStaggerGaugeAndItsBreak()
    {
        var gauge = Assert.IsType<StaggerGaugeEvent>(Assert.Single(Parse($"05E0 {Var(Boss)} 03 01 {U32(100)} {U32(94)} 02")));
        Assert.Equal((Boss, 100u, 94u, (byte)2), (gauge.EntityId, gauge.Max, gauge.Left, gauge.Status));

        var broken = Assert.IsType<StaggerBrokenEvent>(Assert.Single(Parse($"05E0 {Var(Boss)} 00 03 02")));
        Assert.Equal(Boss, broken.EntityId);

        Assert.Empty(Parse($"05E0 {Var(Boss)} 03 01 {U32(100)}")); // cut short
    }

    [Fact]
    public void ParsesAnNpcCastWithACastTime()
    {
        string hex = $"0938 {Var(Boss)} 00 {U32(Quake)} 01 02 {Var(Me)} {F32(172.5f)} {Pos(-96873f, -46081.5f, 11331.8f)} {Var(900)} 01";
        var cast = Assert.IsType<NpcCastEvent>(Assert.Single(Parse(hex)));
        Assert.Equal((Boss, Quake, (uint?)Me, 900u), (cast.CasterId, cast.SkillId, cast.TargetId, cast.CastMs));
        Assert.Equal(172.5f, cast.Facing);
        Assert.Equal(-96873f, cast.Position!.Value.X);
    }

    [Fact]
    public void ParsesAnNpcCastWithAttackSpeedAndTargetList()
    {
        // flags 06: attack speed (10000), then two targets with positions
        string targets = $"02 {Var(Me)} {Pos(-38485f, -23455f, 55518f)} {Var(Ally)} {Pos(-38780f, -23781f, 55515f)}";
        string hex = $"0938 {Var(Boss)} 06 {U32(Quake)} 01 02 {Var(Me)} {F32(-110f)} {Pos(-38500f, -23400f, 55500f)} {Var(2000)} 01 {Var(10000)} {targets}";
        var cast = Assert.IsType<NpcCastEvent>(Assert.Single(Parse(hex)));
        Assert.Equal(2000u, cast.CastMs);
        Assert.Equal(250f, cast.Facing); // normalised to 0..360
    }

    [Fact]
    public void ParsesACastStoppedEarly()
    {
        var died = Assert.IsType<NpcCastStopEvent>(Assert.Single(Parse($"0C38 {Var(Boss)} 0000 {U32(Quake)} 00")));
        Assert.True(died.CasterDied);

        var interrupted = Assert.IsType<NpcCastStopEvent>(Assert.Single(Parse($"0C38 {Var(Boss)} 0818 {U32(Quake)} {Var(30000)}")));
        Assert.False(interrupted.CasterDied);
        Assert.Equal((ushort)0x1808, interrupted.Result);
        Assert.Equal(30000u, interrupted.CooldownMs);
    }

    [Fact]
    public void ParsesATelegraph()
    {
        string hex = $"4838 {Var(Boss)} {U32(Quake)} 01 {U32(1)} {U16(2)} {Pos(12360f, -3055f, 760f)} {F32(-116.6f)} {U16(4200)}";
        var telegraph = Assert.IsType<TelegraphEvent>(Assert.Single(Parse(hex)));
        Assert.Equal((Boss, Quake, (byte)1, (ushort)2, (ushort)4200), (telegraph.CasterId, telegraph.SkillId, telegraph.CastSeq, telegraph.Index, telegraph.DelayMs));
        Assert.Equal(12360f, telegraph.Position.X);
        Assert.Equal(243.4f, telegraph.Facing, 2);
    }

    private static DamageEvent Hit(long ts, uint actor, uint skill, long amount = 1000, uint target = Boss) =>
        new(ts, actor, target, skill, skill, amount, HitType.Normal, HitFlags.None, 1, 10000);

    private static ValuelessHitEvent Mark(long ts, uint actor, uint skill, uint target = Boss) => new(ts, actor, target, skill);

    private static StaggerGaugeEvent Gauge(long ts, uint left, uint max = 100) => new(ts, Boss, max, left, 2);

    [Fact]
    public void CreditsEachDropToTheMarkedHit()
    {
        var tracker = new StaggerTracker();
        Assert.Null(tracker.Apply(Gauge(0, 100)));

        // Ally hits last, but our hit is the one marked as taking stagger
        tracker.Apply(Hit(100, Me, Thunder));
        tracker.Apply(Mark(100, Me, Thunder));
        tracker.Apply(Hit(120, Ally, Strike));
        var credit = tracker.Apply(Gauge(150, 98));
        Assert.Equal((Me, Thunder, 2u, false), (credit!.ActorId, credit.SkillId, credit.Amount, credit.Broke));

        // No mark: the last hit since the update
        tracker.Apply(Hit(200, Me, Thunder));
        tracker.Apply(Hit(220, Ally, Strike));
        credit = tracker.Apply(Gauge(250, 93));
        Assert.Equal((Ally, 5u), (credit!.ActorId, credit.Amount));

        // An update without a new hit credits nobody
        Assert.Null(tracker.Apply(Gauge(300, 90)));
    }

    [Fact]
    public void TheFirstSightOfAGaugeIsNotADrop()
    {
        var tracker = new StaggerTracker();
        tracker.Apply(Hit(100, Me, Thunder));
        Assert.Null(tracker.Apply(Gauge(150, 60))); // what it was before is not known
    }

    [Fact]
    public void TheBreakTakesWhatWasLeftAndTheRefillIsNoDrop()
    {
        var tracker = new StaggerTracker();
        tracker.Apply(Gauge(0, 3));
        tracker.Apply(Hit(100, Ally, Strike));
        tracker.Apply(Mark(100, Ally, Strike));
        var credit = tracker.Apply(new StaggerBrokenEvent(150, Boss));
        Assert.Equal((Ally, 3u, true), (credit!.ActorId, credit.Amount, credit.Broke));

        tracker.Apply(Hit(5_000, Me, Thunder));
        Assert.Null(tracker.Apply(Gauge(5_200, 100))); // back to full
    }

    [Fact]
    public void MarksFromOtherSkillsOrOnOneselfDoNotCount()
    {
        var tracker = new StaggerTracker();
        tracker.Apply(Gauge(0, 100));
        tracker.Apply(Hit(100, Me, Thunder));
        tracker.Apply(Hit(110, Ally, Strike));
        tracker.Apply(Mark(110, Me, Strike));       // our mark for a skill we did not hit with
        tracker.Apply(Mark(110, Ally, Strike, Ally)); // a self effect
        var credit = tracker.Apply(Gauge(150, 98));
        Assert.Equal(Ally, credit!.ActorId); // fell back to the last hit
    }

    private static GameSession Session()
    {
        var session = new GameSession(new GameData(
            new Dictionary<uint, string> { [Thunder] = "Judgment Thunder", [Strike] = "Strike", [Quake] = "Quake", [17_000_100] = "Dodge" },
            new Dictionary<uint, NpcData> { [2100753] = new("Big Boss", IsBoss: true) }));
        session.Apply(new SelfIdentityEvent(0, Me, "Naicha", 1304, GameClass.Cleric, 30));
        session.Apply(new PlayerSpawnEvent(0, Ally, "Friend", 1304));
        session.Apply(new NpcSpawnEvent(0, Boss, 0x04, 2100753, 1_000_000, null));
        return session;
    }

    [Fact]
    public void FightShowsStaggerPerPlayerAndSkillWhoBrokeItAndDodges()
    {
        var session = Session();
        session.Apply(Gauge(500, 10));
        session.Apply(Hit(1_000, Me, Thunder));
        session.Apply(Mark(1_000, Me, Thunder));
        session.Apply(Gauge(1_050, 4));
        session.Apply(Hit(2_000, Ally, Strike));
        session.Apply(Mark(2_000, Ally, Strike));
        session.Apply(new StaggerBrokenEvent(2_050, Boss));
        session.Apply(new SkillCastEvent(2_500, Me, 17_000_100, 17_000_100, null, null));
        session.Apply(new SkillCastEvent(2_600, Me, 17_000_101, 17_000_101, null, null));
        session.Apply(new SkillCastEvent(2_700, Ally, 17_000_100, 17_000_100, null, null));
        session.Apply(new SkillCastEvent(2_800, Boss, 17_000_100, 17_000_100, null, null)); // not a player

        var snapshot = session.Snapshot()!;
        var me = snapshot.Rows.Single(r => r.IsSelf);
        var ally = snapshot.Rows.Single(r => !r.IsSelf);
        Assert.Equal((6L, 0, 1, 1), (me.Stagger, me.StaggerBreaks, me.Dodges, me.SprintDodges));
        Assert.Equal((4L, 1, 1, 0), (ally.Stagger, ally.StaggerBreaks, ally.Dodges, ally.SprintDodges));
        Assert.Equal(6, me.Skills.Single(s => s.SkillId == Thunder).Stagger);

        var broke = Assert.Single(snapshot.Breaks);
        Assert.Equal(("Friend", "Strike", "Big Boss", false), (broke.Player, broke.Skill, broke.Target, broke.IsSelf));
        Assert.Equal(1_050, broke.AtMs);
    }

    [Fact]
    public void SavedFightsKeepStaggerAndDodgesButNotTheLiveCast()
    {
        var session = Session();
        session.Apply(Gauge(500, 10));
        session.Apply(Hit(1_000, Me, Thunder));
        session.Apply(Mark(1_000, Me, Thunder));
        session.Apply(new StaggerBrokenEvent(1_050, Boss));
        session.Apply(new SkillCastEvent(1_100, Me, 17_000_100, 17_000_100, null, null));
        session.Apply(new NpcCastEvent(1_200, Boss, Quake, Me, null, null, 3_000));
        var live = session.Snapshot(nowMs: 1_500)!;
        Assert.NotNull(live.BossCast);

        string dir = Path.Combine(Path.GetTempPath(), "avion2-mechanics-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new HistoryStore(dir);
            Assert.NotNull(store.Save(live));
            var saved = Assert.Single(store.Load()).Snapshot;
            var me = Assert.Single(saved.Rows);
            Assert.Equal((10L, 1, 1), (me.Stagger, me.StaggerBreaks, me.Dodges));
            Assert.Equal(10, me.Skills.Single().Stagger);
            Assert.Equal("Naicha", Assert.Single(saved.Breaks).Player);
            Assert.Null(saved.BossCast);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LiveSnapshotCarriesTheBossCastUntilItGoesOff()
    {
        var session = Session();
        session.Apply(Hit(1_000, Me, Thunder));
        session.Apply(new NpcCastEvent(2_000, Boss, Quake, Me, null, null, 1_500));

        var cast = session.Snapshot(nowMs: 2_500)!.BossCast;
        Assert.Equal(("Quake", 2_000L, 3_500L, false), (cast!.Skill, cast.StartMs, cast.EndMs, cast.Interrupted));

        session.Apply(new SkillCastEvent(3_500, Boss, Quake, Quake, Me, null));
        Assert.Null(session.Snapshot(nowMs: 3_600)!.BossCast);
    }

    [Fact]
    public void InterruptedCastShowsForAWhileButADeathDoesNot()
    {
        var session = Session();
        session.Apply(Hit(1_000, Me, Thunder));
        session.Apply(new NpcCastEvent(2_000, Boss, Quake, Me, null, null, 1_500));
        session.Apply(new NpcCastStopEvent(2_400, Boss, 0x1808, Quake, 0));

        var cast = session.Snapshot(nowMs: 2_500)!.BossCast;
        Assert.True(cast!.Interrupted);
        Assert.Equal(2_400L, cast.StoppedMs);
        Assert.Null(session.Snapshot(nowMs: 2_400 + NpcCastTracker.InterruptShownMs + 1)!.BossCast);

        session.Apply(new NpcCastEvent(6_000, Boss, Quake, Me, null, null, 1_500));
        session.Apply(new NpcCastStopEvent(6_300, Boss, 0, Quake, 0));
        Assert.Null(session.Snapshot(nowMs: 6_400)!.BossCast);
    }

    [Fact]
    public void TraceHasWindupsWithTheirEndAndTelegraphs()
    {
        var session = Session();
        session.Apply(Hit(1_000, Me, Thunder));
        session.Apply(new NpcCastEvent(2_000, Boss, Quake, Me, null, null, 1_500));
        session.Apply(new NpcCastStopEvent(2_400, Boss, 0x1808, Quake, 0));
        session.Apply(new NpcCastEvent(4_000, Boss, Quake, Me, null, null, 1_000));
        session.Apply(new TelegraphEvent(5_000, Boss, Quake, 3, 1, new Vec3(12360, -3055, 760), 0, 1_700));
        session.Apply(Hit(5_500, Me, Thunder));

        var trace = session.Trace(session.Tracker.Current!);
        Assert.Equal(2, trace.Windups.Count);
        var interrupted = trace.Windups[0];
        Assert.Equal((1_500, true), (interrupted.CastMs, interrupted.Interrupted));
        Assert.Equal(interrupted.T + 400, interrupted.StopT);
        Assert.Null(trace.Windups[1].StopT);

        var telegraph = Assert.Single(trace.Telegraphs);
        Assert.Equal((12360, -3055, 1_700), (telegraph.X, telegraph.Y, telegraph.DelayMs));
        Assert.Contains(trace.Moments(session.Registry.Data), m => m.Text == "Big Boss: Quake interrupted");
    }

    [Fact]
    public void DodgeSkillsAreTheClassDodgeAndItsSprintingForm()
    {
        Assert.Equal(1, DodgeSkills.Kind(17_000_100));
        Assert.Equal(2, DodgeSkills.Kind(18_000_101));
        Assert.Equal(0, DodgeSkills.Kind(17_000_001)); // basic attack
        Assert.Equal(0, DodgeSkills.Kind(1_000_100));  // an NPC skill
    }
}
