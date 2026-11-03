using Avion2.Analysis;
using Avion2.Protocol;

namespace Avion2.Tests;

public class WorldEventTests
{
    // 01 43 as sent at every world entry from 2026-10-31 on: the battle on Wed 2026-11-04 17:00–17:10 CET
    private const string AirspaceHex = "0143" + "010001" + "00000000" + "00e449a7a1010000" + "c00b53a7a1010000" + "05" +
        "0101000000000000000000000000000000000000" + "0102000000000000000000000000000000000000" +
        "0103000000000000000000000000000000000000" + "0104000000000000000000000000000000000000" +
        "0105000000000000000000000000000000000000";

    private static readonly TimeSpan Cet = TimeSpan.FromHours(1);
    private static readonly DateTimeOffset BattleStart = new(2026, 11, 4, 17, 0, 0, Cet);

    // Dimensional Invasion variant 120101 on Mon 2026-11-02 19:30 CET: the lead-in, the event, closed
    private const string InvasionLeadIn = "0160" + "03" + "a5aa07" + "01" + "0185869da1010000" + "0185869da1010000" + "2144899da1010000";
    private const string InvasionRunning = "0160" + "03" + "a5aa07" + "02" + "0185869da1010000" + "7846899da1010000" + "386e929da1010000";
    private const string InvasionClosed = "0160" + "00" + "a5aa07" + "00" + "8075d52abb030000";

    private static List<GameEvent> Parse(string hex)
    {
        var events = new List<GameEvent>();
        Assert.True(PacketParser.Parse(Convert.FromHexString(hex), 1_000, events.Add));
        return events;
    }

    private static T Single<T>(string hex) where T : GameEvent => Assert.IsType<T>(Assert.Single(Parse(hex)));

    private static long Ms(DateTimeOffset at) => at.ToUnixTimeMilliseconds();

    [Fact]
    public void AirspaceBattleAnnouncement()
    {
        var battle = Single<AirspaceBattleEvent>(AirspaceHex);
        Assert.True(battle.Announced);
        Assert.Equal(0u, battle.Phase);
        Assert.Equal(Ms(BattleStart), battle.StartMs);
        Assert.Equal(Ms(BattleStart.AddMinutes(10)), battle.EndMs);
        Assert.Null(battle.ExtraMs);
        Assert.Equal([1, 2, 3, 4, 5], battle.Airspaces.Select(a => (int)a.AirspaceId));
        Assert.All(battle.Airspaces, a => Assert.Equal(0u, a.Phase));
    }

    [Fact]
    public void AirspaceBattleShortFormHasNoTimes()
    {
        var battle = Single<AirspaceBattleEvent>("014300");
        Assert.False(battle.Announced);
        Assert.Empty(battle.Airspaces);
    }

    [Fact]
    public void AirspaceBattleRejectsTruncatedRecords()
    {
        var events = new List<GameEvent>();
        Assert.False(PacketParser.Parse(Convert.FromHexString(AirspaceHex[..^2]), 0, events.Add));
        Assert.Empty(events);
    }

    [Fact]
    public void FieldEventPhases()
    {
        var leadIn = Single<FieldEventChangedEvent>(InvasionLeadIn).State;
        Assert.Equal(120101u, leadIn.EventId);
        Assert.Equal(1, leadIn.Phase);
        Assert.Equal(leadIn.StartMs, leadIn.PhaseStartMs);
        Assert.Equal(TimeSpan.FromMinutes(3), TimeSpan.FromMilliseconds(leadIn.PhaseEndMs - leadIn.PhaseStartMs));
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 19, 30, 1, 217, Cet), DateTimeOffset.FromUnixTimeMilliseconds(leadIn.StartMs));

        var running = Single<FieldEventChangedEvent>(InvasionRunning).State;
        Assert.Equal(2, running.Phase);
        Assert.Equal(leadIn.StartMs, running.StartMs);
        Assert.Equal(TimeSpan.FromMinutes(10), TimeSpan.FromMilliseconds(running.PhaseEndMs - running.PhaseStartMs));

        var closed = Single<FieldEventChangedEvent>(InvasionClosed).State;
        Assert.True(closed.Closed);
        Assert.Equal(120101u, closed.EventId);
        Assert.Equal(0, closed.PhaseEndMs);
    }

    [Fact]
    public void FieldEventListAtZoneEntry()
    {
        // 00 60 at 18:37:44 on 2026-11-02: one event open (120101 in phase 2, 18:33:02–18:43:02)
        var list = Single<FieldEventListEvent>("0060" + "01" + "03a5aa0702d8984f9da1010000525a529da101000012825b9da1010000");
        var state = Assert.Single(list.Events);
        Assert.Equal(120101u, state.EventId);
        Assert.Equal(2, state.Phase);

        Assert.Empty(Single<FieldEventListEvent>("006000").Events);
    }

    [Fact]
    public void LeavingAndNpcConversations()
    {
        Assert.Equal(0, Single<MapExitEvent>("05400000").Result);

        var talk = Single<NpcConversationEvent>("0740" + "00" + "3113" + "147a0000");
        Assert.Equal(4913, talk.Result);
        Assert.Equal(0x7a14u, talk.NpcId);

        var start = Single<NpcInteractionStartEvent>("41E2" + "0000" + "32470000" + "c06fcc91a1010000");
        Assert.Equal(0, start.Result);
        Assert.Equal(0x4732u, start.NpcId);
        Assert.True(start.ExpiresMs > 0);

        var failed = Single<NpcInteractionStartEvent>("41E2" + "083e" + "00000000" + "0000000000000000");
        Assert.Equal(15880, failed.Result);
        Assert.Equal(0u, failed.NpcId);

        var finish = Single<NpcInteractionEndEvent>("43E2" + "0000" + "32470000");
        Assert.False(finish.Cancelled);
        Assert.Equal(0x4732u, finish.NpcId);

        var cancel = Single<NpcInteractionEndEvent>("46E2" + "ed840000");
        Assert.True(cancel.Cancelled);
        Assert.Equal(0x84edu, cancel.NpcId);
    }

    [Fact]
    public void EuropeFallsBackToTheKnownAirspaceSlot()
    {
        // Without an announcement: Wed 17:00 Central European time, also across the clock change
        var events = GameSchedule.Events(ServerRegion.Europe);
        var slot = events.First(e => e.Key == GameSchedule.AirspaceKey);
        Assert.Equal("Thu · 00:00", GameSchedule.Describe(slot));
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 17, 0, 0, TimeSpan.FromHours(2)),
            GameSchedule.Next(slot, new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.FromHours(2)), ServerRegion.Europe));
        Assert.Equal(BattleStart, GameSchedule.Next(slot, new DateTimeOffset(2026, 11, 3, 12, 0, 0, Cet), ServerRegion.Europe));

        // Other regions: unknown until the game announces it
        var na = GameSchedule.Events(ServerRegion.NorthAmerica);
        Assert.DoesNotContain(na, e => e.Key == GameSchedule.AirspaceKey);
        Assert.Contains(na, e => e.Key == "abyss"); // and the global PvP events stay as they were
        Assert.DoesNotContain(na, e => e.Key == "abyss-rift-zone");
    }

    [Fact]
    public void AnnouncedAirspaceBattleDrivesTheTimer()
    {
        var board = new WorldEventBoard();
        Assert.True(board.Apply(Single<AirspaceBattleEvent>(AirspaceHex)));
        Assert.False(board.Apply(Single<AirspaceBattleEvent>(AirspaceHex))); // the same again: nothing to save
        Assert.False(board.Apply(Single<AirspaceBattleEvent>("014300"))); // the short form keeps the announcement

        // Before: the announced time (and a single entry, replacing the fixed slot)
        var before = new DateTimeOffset(2026, 11, 3, 20, 0, 0, Cet);
        var upcoming = board.Adjust(GameSchedule.Upcoming(ServerRegion.Europe, before), ServerRegion.Europe, before);
        var airspace = Assert.Single(upcoming, o => o.Event.Key == GameSchedule.AirspaceKey);
        Assert.Equal(BattleStart, airspace.StartsAt);
        Assert.StartsWith("Announced by the game", airspace.Source);
        Assert.Empty(board.Live(ServerRegion.Europe, before));

        // During: on now until 17:10; the next one a week later
        var during = BattleStart.AddMinutes(4);
        var live = Assert.Single(board.Live(ServerRegion.Europe, during));
        Assert.Equal(GameSchedule.AirspaceBattle, live.Name);
        Assert.Equal(BattleStart.AddMinutes(10), live.EndsAt);
        var next = board.Adjust(GameSchedule.Upcoming(ServerRegion.Europe, during), ServerRegion.Europe, during)
            .Single(o => o.Event.Key == GameSchedule.AirspaceKey);
        Assert.Equal(BattleStart.AddDays(7), next.StartsAt);

        // A region without a fixed slot gets the announced one too
        var na = board.Adjust(GameSchedule.Upcoming(ServerRegion.NorthAmerica, before), ServerRegion.NorthAmerica, before);
        Assert.Contains(na, o => o.Event.Key == GameSchedule.AirspaceKey && o.StartsAt == BattleStart);
    }

    [Fact]
    public void AnnouncedAirspaceBattleIsSaved()
    {
        string path = Path.Combine(Path.GetTempPath(), $"avion2-world-{Guid.NewGuid():N}.json");
        try
        {
            var board = new WorldEventBoard();
            board.Apply(Single<AirspaceBattleEvent>(AirspaceHex));
            board.Save(path);
            Assert.Equal(board.Airspace, WorldEventBoard.Load(path).Airspace);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FieldEventsOnNowFollowTheGamesPhases()
    {
        var board = new WorldEventBoard();
        board.Apply(Single<FieldEventChangedEvent>(InvasionLeadIn));
        var leadIn = Assert.Single(board.Live(ServerRegion.Europe, new DateTimeOffset(2026, 11, 2, 19, 31, 0, Cet)));
        Assert.Equal(GameSchedule.DimensionalInvasion, leadIn.Name);
        Assert.Equal(1, leadIn.Phase);
        Assert.Null(leadIn.EndsAt); // the event's end comes with phase 2
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 19, 33, 1, 217, Cet), leadIn.PhaseEndsAt);

        board.Apply(Single<FieldEventChangedEvent>(InvasionRunning));
        var running = Assert.Single(board.Live(ServerRegion.Europe, new DateTimeOffset(2026, 11, 2, 19, 35, 0, Cet)));
        Assert.Equal(2, running.Phase);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 19, 43, 1, 816, Cet), running.EndsAt);
        Assert.Equal([120101u], running.Ids);

        board.Apply(Single<FieldEventChangedEvent>(InvasionClosed));
        Assert.Empty(board.Live(ServerRegion.Europe, new DateTimeOffset(2026, 11, 2, 19, 36, 0, Cet)));
    }

    [Theory]
    [InlineData(true, 0L, true)] // the exit reply in the same instant as the map load out
    [InlineData(true, -3_500L, true)] // or a few seconds before it
    [InlineData(false, 0L, false)] // a teleport, the automatic exit or a relogin
    [InlineData(true, -60_000L, false)] // an exit long before belongs to something else
    public void RunsTellTheExitFromOtherWaysOut(bool exit, long exitOffsetMs, bool usedExit)
    {
        var session = new GameSession(GameData.Empty);
        var ended = new List<DungeonRun>();
        session.Runs.RunEnded += ended.Add;
        session.Apply(new MapLoadEvent(10_000, 310042, 30));
        if (exit)
        {
            session.Apply(new MapExitEvent(200_000 + exitOffsetMs, 0));
        }

        session.Apply(new MapLoadEvent(200_000, 1010, 0));
        session.Apply(new ZoneChangeEvent(201_000));

        var run = Assert.Single(ended);
        Assert.True(run.Left);
        Assert.Equal(usedExit, run.UsedExit);
        Assert.Equal(usedExit, run.Copy().UsedExit);
    }

    [Fact]
    public void RunEndedByStoppingTheCaptureHasNoWayOut()
    {
        var session = new GameSession(GameData.Empty);
        var ended = new List<DungeonRun>();
        session.Runs.RunEnded += ended.Add;
        session.Apply(new MapLoadEvent(10_000, 310042, 30));
        session.Apply(new MapExitEvent(50_000, 0));
        session.Runs.Finish(60_000);
        Assert.Null(Assert.Single(ended).UsedExit);
    }

    [Fact]
    public void ShugoFestivalMinigamesShowAsOneEvent()
    {
        var at = new DateTimeOffset(2026, 11, 3, 19, 0, 1, Cet);
        long start = Ms(at);
        long phase2 = start + 180_000;
        var board = new WorldEventBoard();
        board.Apply(new FieldEventListEvent(0, [
            new FieldEventState(110101, 2, start, phase2, phase2 + 175_000),
            new FieldEventState(110301, 2, start, phase2, phase2 + 312_000),
            new FieldEventState(120101, 0, 0, 0, 0), // a closed entry is ignored
        ]));
        var shugo = Assert.Single(board.Live(ServerRegion.Europe, at.AddMinutes(4)));
        Assert.Equal(GameSchedule.ShugoFestival, shugo.Name);
        Assert.Equal(ScheduleCategory.FieldEvent, shugo.Category);
        Assert.Equal(at, shugo.StartedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(phase2 + 312_000), shugo.EndsAt); // the longest minigame
        Assert.Equal([110101u, 110301u], shugo.Ids);

        // A new list replaces the old one: an empty list means nothing is open
        board.Apply(new FieldEventListEvent(0, []));
        Assert.Empty(board.Live(ServerRegion.Europe, at.AddMinutes(4)));
    }
}
