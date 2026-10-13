namespace Avion2.Analysis;

public enum ServerRegion
{
    Europe,
    NorthAmerica,
    SouthAmerica,
    Japan,
    Korea,
    Taiwan,
}

public enum ScheduleCategory
{
    Rift,
    BossRaid,
    FieldEvent,
    Pvp,
    Reset,
}

/// <summary>A recurring in-game event. Times are server-local (see <see cref="GameSchedule.ServerOffset"/>).</summary>
/// <param name="Hours">Server hours it starts at (for hourly events: every hour).</param>
/// <param name="Days">Server weekdays it runs on; empty = every day.</param>
public sealed record ScheduledEvent(
    string Key,
    string Name,
    ScheduleCategory Category,
    int[] Hours,
    int Minute,
    DayOfWeek[] Days)
{
    public static int[] EveryHour { get; } = Enumerable.Range(0, 24).ToArray();
}

public sealed record Occurrence(ScheduledEvent Event, DateTimeOffset StartsAt);

/// <summary>
/// The fixed AION2 event timetable per server region. Source: aion2hub.com's rift timer
/// (Global data from the 2026-09-19 client; Korea/Taiwan extracted from their clients).
/// Spacetime Rifts are a timetable, not packets: Global servers open them every 3 hours from 00:00
/// server time (GMT+9), Korea/Taiwan from 02:00.
/// </summary>
public static class GameSchedule
{
    private static readonly int[] GlobalRiftHours = [0, 3, 6, 9, 12, 15, 18, 21];
    private static readonly int[] KoreaTaiwanRiftHours = [2, 5, 8, 11, 14, 17, 20, 23];
    private static readonly DayOfWeek[] Daily = [];

    public static TimeSpan ServerOffset(ServerRegion region) =>
        region == ServerRegion.Taiwan ? TimeSpan.FromHours(8) : TimeSpan.FromHours(9);

    public static IReadOnlyList<ScheduledEvent> Events(ServerRegion region)
    {
        bool global = region is not (ServerRegion.Korea or ServerRegion.Taiwan);
        var events = new List<ScheduledEvent>
        {
            new("rift", "Spacetime Rift", ScheduleCategory.Rift, global ? GlobalRiftHours : KoreaTaiwanRiftHours, 0, Daily),
            new("beritra", "Beritra Air Raid", ScheduleCategory.BossRaid, ScheduledEvent.EveryHour, 30, Daily),
            new("field", "Field events (Shugo defense, minigames)", ScheduleCategory.FieldEvent, ScheduledEvent.EveryHour, 0, Daily),
            new("daily-reset", "Daily reset", ScheduleCategory.Reset, [global ? 16 : 5], 0, Daily),
            new("weekly-reset", "Weekly reset", ScheduleCategory.Reset, [global ? 16 : 5], 0, [DayOfWeek.Wednesday]),
        };

        if (global)
        {
            events.Add(new("abyss", "Abyss Event", ScheduleCategory.Pvp, [21], 0, [DayOfWeek.Monday, DayOfWeek.Thursday, DayOfWeek.Saturday]));
        }
        else
        {
            events.Add(new("abyss-rift-zone", "Abyss Rift Zone", ScheduleCategory.Pvp, [22], 0, [DayOfWeek.Tuesday, DayOfWeek.Thursday]));
            events.Add(new("rift-domination", "Spacetime Rift Domination", ScheduleCategory.Pvp, [20, 23], 0,
                [DayOfWeek.Monday, DayOfWeek.Thursday, DayOfWeek.Saturday]));
        }

        return events;
    }

    /// <summary>The next start of <paramref name="ev"/> strictly after <paramref name="now"/>.</summary>
    public static DateTimeOffset Next(ScheduledEvent ev, DateTimeOffset now, ServerRegion region)
    {
        var offset = ServerOffset(region);
        var server = now.ToOffset(offset);
        var day = new DateTimeOffset(server.Year, server.Month, server.Day, 0, 0, 0, offset);
        for (int d = 0; d < 8; d++, day = day.AddDays(1))
        {
            if (ev.Days.Length > 0 && !ev.Days.Contains(day.DayOfWeek))
            {
                continue;
            }

            foreach (int hour in ev.Hours.Order())
            {
                var start = day.AddHours(hour).AddMinutes(ev.Minute);
                if (start > now)
                {
                    return start;
                }
            }
        }

        throw new InvalidOperationException($"No occurrence of {ev.Key} within a week.");
    }

    /// <summary>Next occurrence of every event, soonest first.</summary>
    public static IReadOnlyList<Occurrence> Upcoming(ServerRegion region, DateTimeOffset now) =>
        Events(region).Select(e => new Occurrence(e, Next(e, now, region))).OrderBy(o => o.StartsAt).ToList();

    /// <summary>The next few rift openings.</summary>
    public static IReadOnlyList<DateTimeOffset> NextRifts(ServerRegion region, DateTimeOffset now, int count)
    {
        var rift = Events(region).First(e => e.Key == "rift");
        var list = new List<DateTimeOffset>();
        var cursor = now;
        for (int i = 0; i < count; i++)
        {
            cursor = Next(rift, cursor, region);
            list.Add(cursor);
        }

        return list;
    }
}
