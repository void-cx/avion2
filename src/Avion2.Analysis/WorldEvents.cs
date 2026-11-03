using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>The Airspace Battle the game announced last (<c>01 43</c>). Times are Unix ms.</summary>
/// <param name="SeenMs">When the announcement arrived.</param>
public sealed record AirspaceBattle(
    [property: JsonPropertyName("startMs")] long StartMs,
    [property: JsonPropertyName("endMs")] long EndMs,
    [property: JsonPropertyName("seenMs")] long SeenMs);

/// <summary>An event on right now, with the times the game sent for it.</summary>
/// <param name="Phase">The field event's phase (1 = the 3-minute lead-in, 2 = the event); 0 for the Airspace Battle.</param>
/// <param name="PhaseEndsAt">When the current phase ends.</param>
/// <param name="EndsAt">When the event ends; null while that is not known yet (a field event in its lead-in).</param>
/// <param name="Ids">The game's ids of the field events in this group (the nine Shugo Festival minigames, …).</param>
public sealed record LiveEvent(
    string Key,
    string Name,
    ScheduleCategory Category,
    DateTimeOffset StartedAt,
    int Phase,
    DateTimeOffset PhaseEndsAt,
    DateTimeOffset? EndsAt,
    IReadOnlyList<uint> Ids);

/// <summary>
/// What the game says about its scheduled events: the next Airspace Battle (<c>01 43</c>, at world entry) and the
/// field events open right now (<c>00 60</c> at zone entry, <c>01 60</c> at each phase change). The Airspace Battle is
/// saved, so its time is known after a restart; it is taken to repeat weekly at the same server time. Not thread-safe.
/// </summary>
public sealed class WorldEventBoard
{
    private static readonly TimeSpan DefaultAirspaceLength = TimeSpan.FromMinutes(10);

    private readonly Dictionary<uint, FieldEventState> _fieldEvents = new();

    public AirspaceBattle? Airspace { get; private set; }

    /// <summary>Applies a world event record; returns true when the saved part (the Airspace Battle) changed.</summary>
    public bool Apply(GameEvent e)
    {
        switch (e)
        {
            case AirspaceBattleEvent { Announced: true } battle:
                if (Airspace is { } known && known.StartMs == battle.StartMs && known.EndMs == battle.EndMs)
                {
                    return false;
                }

                Airspace = new AirspaceBattle(battle.StartMs, battle.EndMs, battle.TimestampMs);
                return true;
            case FieldEventListEvent list:
                // The complete list of what is open: anything missing has closed
                _fieldEvents.Clear();
                foreach (var state in list.Events.Where(s => !s.Closed))
                {
                    _fieldEvents[state.EventId] = state;
                }

                return false;
            case FieldEventChangedEvent { State: var changed }:
                if (changed.Closed)
                {
                    _fieldEvents.Remove(changed.EventId);
                }
                else
                {
                    _fieldEvents[changed.EventId] = changed;
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// The timetable with the announced Airspace Battle in place of the fixed slot (or added, for regions without one).
    /// Unchanged while no announcement was seen.
    /// </summary>
    public IReadOnlyList<Occurrence> Adjust(IReadOnlyList<Occurrence> timetable, ServerRegion region, DateTimeOffset now)
    {
        if (Airspace is not { } battle)
        {
            return timetable;
        }

        var ev = AirspaceEvent(battle, region);
        var start = FromMs(battle.StartMs);
        string seen = FromMs(battle.SeenMs).ToLocalTime().ToString("ddd dd MMM", CultureInfo.InvariantCulture);
        var next = start > now
            ? new Occurrence(ev, start, $"Announced by the game ({seen})")
            : new Occurrence(ev, GameSchedule.Next(ev, now, region), $"Announced by the game ({seen}), repeating weekly");
        return timetable.Where(o => o.Event.Key != GameSchedule.AirspaceKey).Append(next).OrderBy(o => o.StartsAt).ToList();
    }

    /// <summary>Events on right now: field events the game reported open, and the Airspace Battle in its slot.</summary>
    public IReadOnlyList<LiveEvent> Live(ServerRegion region, DateTimeOffset now)
    {
        var live = new List<LiveEvent>();
        if (Airspace is { } battle && CurrentAirspace(battle, region, now) is { } on)
        {
            live.Add(on);
        }

        long nowMs = now.ToUnixTimeMilliseconds();
        foreach (var group in _fieldEvents.Values.Where(s => s.PhaseEndMs > nowMs).GroupBy(s => Family(s.EventId)))
        {
            var states = group.ToList();
            int phase = states.Max(s => s.Phase);
            var current = states.Where(s => s.Phase == phase).ToList();
            var phaseEnd = FromMs(current.Max(s => s.PhaseEndMs));
            var (key, name, category) = Describe(group.Key, states[0].EventId);
            live.Add(new LiveEvent(key, name, category, FromMs(states.Min(s => s.StartMs)), phase, phaseEnd,
                phase >= 2 ? phaseEnd : null, states.Select(s => s.EventId).Order().ToList()));
        }

        return live.OrderBy(l => l.StartedAt).ToList();
    }

    /// <summary>The Airspace Battle when one is under way now (the announced one, or its weekly repeat).</summary>
    private static LiveEvent? CurrentAirspace(AirspaceBattle battle, ServerRegion region, DateTimeOffset now)
    {
        var length = battle.EndMs > battle.StartMs ? TimeSpan.FromMilliseconds(battle.EndMs - battle.StartMs) : DefaultAirspaceLength;
        var start = FromMs(battle.StartMs);
        var since = now - length;
        var at = start > since ? start : GameSchedule.Next(AirspaceEvent(battle, region), since, region);
        return at <= now && now < at + length
            ? new LiveEvent(GameSchedule.AirspaceKey, GameSchedule.AirspaceBattle, ScheduleCategory.Pvp, at, 0, at + length, at + length, [])
            : null;
    }

    /// <summary>A weekly event at the announced battle's server weekday and time.</summary>
    private static ScheduledEvent AirspaceEvent(AirspaceBattle battle, ServerRegion region)
    {
        var start = FromMs(battle.StartMs);
        var wall = start.ToOffset(GameSchedule.ServerOffset(region, start));
        return new ScheduledEvent(GameSchedule.AirspaceKey, GameSchedule.AirspaceBattle, ScheduleCategory.Pvp, [wall.Hour], wall.Minute, [wall.DayOfWeek]);
    }

    /// <summary>The event family: 11 = Shugo Festival (1101xx–1109xx), 12 = Dimensional Invasion (1201xx–1203xx).</summary>
    private static uint Family(uint eventId) => eventId / 10_000;

    private static (string Key, string Name, ScheduleCategory Category) Describe(uint family, uint anyId) => family switch
    {
        11 => (GameSchedule.ShugoKey, GameSchedule.ShugoFestival, ScheduleCategory.FieldEvent),
        12 => (GameSchedule.InvasionKey, GameSchedule.DimensionalInvasion, ScheduleCategory.BossRaid),
        _ => ($"fieldevent:{family}", $"Field event {anyId}", ScheduleCategory.FieldEvent),
    };

    private static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    private sealed record Saved([property: JsonPropertyName("airspace")] AirspaceBattle? Airspace);

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new Saved(Airspace)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not critical
        }
    }

    public static WorldEventBoard Load(string path)
    {
        var board = new WorldEventBoard();
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)) is { Airspace: { } airspace })
            {
                board.Airspace = airspace;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // start empty
        }

        return board;
    }
}
