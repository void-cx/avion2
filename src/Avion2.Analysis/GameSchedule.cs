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
    FieldBoss,
    WorldBoss,
}

/// <summary>A recurring in-game event. Times are server-local (see <see cref="GameSchedule.ServerOffset"/>).</summary>
/// <param name="Hours">Server hours it starts at (for hourly events: every hour).</param>
/// <param name="Days">Server weekdays it runs on; empty = every day.</param>
/// <param name="Zone">Where it happens (world bosses), or null.</param>
public sealed record ScheduledEvent(
    string Key,
    string Name,
    ScheduleCategory Category,
    int[] Hours,
    int Minute,
    DayOfWeek[] Days,
    string? Zone = null)
{
    public static int[] EveryHour { get; } = Enumerable.Range(0, 24).ToArray();
}

public sealed record Occurrence(ScheduledEvent Event, DateTimeOffset StartsAt);

/// <summary>
/// The fixed AION2 event timetable per server region. Source: aion2hub.com's rift timer and world boss
/// schedule (Global data from the 2026-09-19 client; Korea/Taiwan from client v110, 2026-09-09).
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

        events.AddRange(WorldBosses(global));
        return events;
    }

    private const string LowerReshanta = "Chaotic Lower Reshanta";
    private const string MiddleReshanta = "Chaotic Middle Reshanta";

    /// <summary>
    /// Reshanta world bosses. Watcher Kaira respawns on an interval (every 3 h from 01:00 server time on Global,
    /// every 4 h on Korea/Taiwan); the others spawn on fixed weekdays. Middle Reshanta is Korea/Taiwan only.
    /// </summary>
    private static IEnumerable<ScheduledEvent> WorldBosses(bool global)
    {
        static int[] Every(int hours) => Enumerable.Range(0, 24 / hours).Select(i => 1 + i * hours).ToArray();
        DayOfWeek[] executorDays = global ? [DayOfWeek.Monday, DayOfWeek.Thursday, DayOfWeek.Saturday] : [DayOfWeek.Wednesday, DayOfWeek.Saturday];
        DayOfWeek[] siegeDays = [DayOfWeek.Sunday, DayOfWeek.Friday];
        int executorHour = global ? 21 : 22;
        int siegeHour = global ? 21 : 22;

        yield return new("wb-watcher-kaira", "Watcher Kaira", ScheduleCategory.WorldBoss, Every(global ? 3 : 4), 0, Daily, LowerReshanta);
        yield return new("wb-executors", "Executors Argo, Kaira & Tamasa", ScheduleCategory.WorldBoss, [executorHour], 30, executorDays, LowerReshanta);
        yield return new("wb-siege", "Abyss Siege Boss", ScheduleCategory.WorldBoss, [siegeHour], 0, siegeDays, LowerReshanta);
        if (!global)
        {
            yield return new("wb-nahma", "Enraged Guardian Lord Nahma", ScheduleCategory.WorldBoss, [22], 0, siegeDays, MiddleReshanta);
            yield return new("wb-middle", "Dramos, Marakha & Ducal", ScheduleCategory.WorldBoss, [22], 30, executorDays, MiddleReshanta);
        }
    }

    /// <summary>"Every 3 h from 01:00", "Mon, Thu, Sat · 21:30" — the rule behind an event, in server time.</summary>
    public static string Describe(ScheduledEvent ev)
    {
        if (ev.Hours.Length == 24)
        {
            return $"Every hour at :{ev.Minute:00}";
        }

        string time(int hour) => $"{hour:00}:{ev.Minute:00}";
        string hours = ev.Hours.Length > 2 && ev.Days.Length == 0 && ev.Hours.Zip(ev.Hours.Skip(1), (a, b) => b - a).Distinct().Count() == 1
            ? $"Every {ev.Hours[1] - ev.Hours[0]} h from {time(ev.Hours[0])}"
            : string.Join(", ", ev.Hours.Select(time));
        return ev.Days.Length == 0 ? hours : $"{string.Join(", ", ev.Days.Select(d => d.ToString()[..3]))} · {hours}";
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
