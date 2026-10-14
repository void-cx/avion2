using Avion2.Analysis;

namespace Avion2.Tests;

public class ScheduleTests
{
    // Tue 2026-10-13 12:24:33 Berlin (CEST, UTC+2), the moment the user's aion2hub page was captured.
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 24, 33, TimeSpan.FromHours(2));

    private static ScheduledEvent Event(ServerRegion region, string key) => GameSchedule.Events(region).First(e => e.Key == key);

    [Fact]
    public void EuropeRiftMatchesAion2HubCountdown()
    {
        // aion2hub: "Next Spacetime Rift … Opens at 14:00 your time" (in 1h 35m 27s).
        var next = GameSchedule.Next(Event(ServerRegion.Europe, "rift"), Now, ServerRegion.Europe);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 14, 0, 0, TimeSpan.FromHours(2)), next.ToOffset(TimeSpan.FromHours(2)));
        Assert.Equal(TimeSpan.FromSeconds(1 * 3600 + 35 * 60 + 27), next - Now);
    }

    [Fact]
    public void RiftsEveryThreeHours()
    {
        var rifts = GameSchedule.NextRifts(ServerRegion.Europe, Now, 6).Select(r => r.ToOffset(TimeSpan.FromHours(2)).Hour);
        Assert.Equal([14, 17, 20, 23, 2, 5], rifts);
    }

    [Fact]
    public void HourlyEvents()
    {
        // Beritra Air Raid on the half hour ("in 05m 27s"), field events on the hour ("in 35m 27s").
        Assert.Equal(TimeSpan.FromSeconds(5 * 60 + 27), GameSchedule.Next(Event(ServerRegion.Europe, "beritra"), Now, ServerRegion.Europe) - Now);
        Assert.Equal(TimeSpan.FromSeconds(35 * 60 + 27), GameSchedule.Next(Event(ServerRegion.Europe, "field"), Now, ServerRegion.Europe) - Now);
    }

    [Fact]
    public void AbyssEventOnThursday()
    {
        // aion2hub: Abyss Event Mon/Thu/Sat, "in 49h 35m", next 14:00 local.
        var next = GameSchedule.Next(Event(ServerRegion.Europe, "abyss"), Now, ServerRegion.Europe);
        Assert.Equal(DayOfWeek.Thursday, next.ToOffset(TimeSpan.FromHours(2)).DayOfWeek);
        Assert.Equal(TimeSpan.FromHours(49) + TimeSpan.FromSeconds(35 * 60 + 27), next - Now);
    }

    [Fact]
    public void TaiwanIsAnHourAfterKorea()
    {
        var korea = GameSchedule.Next(Event(ServerRegion.Korea, "rift"), Now, ServerRegion.Korea);
        var taiwan = GameSchedule.Next(Event(ServerRegion.Taiwan, "rift"), Now, ServerRegion.Taiwan);
        Assert.Equal(TimeSpan.FromHours(1), taiwan - korea);
    }

    [Fact]
    public void GlobalWorldBossesMatchAion2Hub()
    {
        // aion2hub world boss page, Wed 2026-10-14 11:53:02 Berlin, Europe server: Watcher Kaira at 12:00,
        // the Executors Thu 14:30, the Abyss Siege Boss Fri 14:00 (local time).
        var now = new DateTimeOffset(2026, 10, 14, 11, 53, 2, TimeSpan.FromHours(2));
        var berlin = TimeSpan.FromHours(2);
        DateTimeOffset Next(string key) => GameSchedule.Next(Event(ServerRegion.Europe, key), now, ServerRegion.Europe).ToOffset(berlin);

        Assert.Equal(new DateTimeOffset(2026, 10, 14, 12, 0, 0, berlin), Next("wb-watcher-kaira"));
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 14, 30, 0, berlin), Next("wb-executors"));
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 14, 0, 0, berlin), Next("wb-siege"));
        Assert.DoesNotContain(GameSchedule.Events(ServerRegion.Europe), e => e.Key == "wb-nahma");
    }

    [Fact]
    public void KoreaWorldBossesUseTheirOwnSchedule()
    {
        var kaira = Event(ServerRegion.Korea, "wb-watcher-kaira");
        Assert.Equal([1, 5, 9, 13, 17, 21], kaira.Hours);
        Assert.Equal("Every 4 h from 01:00", GameSchedule.Describe(kaira));
        Assert.Equal("Wed, Sat · 22:30", GameSchedule.Describe(Event(ServerRegion.Korea, "wb-executors")));
        Assert.Contains(GameSchedule.Events(ServerRegion.Korea), e => e.Key == "wb-nahma" && e.Zone == "Chaotic Middle Reshanta");
    }
}
