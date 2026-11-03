using System.Text.Json.Serialization;
using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>Content ticket ids with a known meaning (<c>0B 61</c> / <c>0C 61</c>).</summary>
public static class TicketIds
{
    public const uint ShugoKeys = 1;
    public const uint Nightmare = 4;
    public const uint InvasionKeys = 8;
    public const uint Odyle = 60_000_001;
    public const uint Ascension = 80_000_001;

    /// <summary>The tickets shown on Home, in order, with their names.</summary>
    public static IReadOnlyList<(uint Id, string Name)> Shown { get; } =
    [
        (Odyle, "Odyle"),
        (Nightmare, "Nightmare"),
        (Ascension, "Ascension"),
        (ShugoKeys, "Shugo keys"),
        (InvasionKeys, "Invasion keys"),
    ];
}

/// <summary>
/// What the game tells a character about its progress: item level, Kinah gained today, Abyss Points this week, entry
/// tickets, the Daeva Pass and pet souls. Each part keeps when it arrived, so a day's or a week's count is only shown
/// while that day or week lasts. Experience gained is kept for this session only.
/// </summary>
public sealed class CharacterProgress
{
    /// <summary>Kinah a character can gain per day.</summary>
    public const ulong DailyKinahLimit = 1_000_000;

    /// <summary>Souls of one kind that bring a pet.</summary>
    public const int SoulsPerPet = 5;

    [JsonPropertyName("character")]
    public string? Character { get; set; }

    /// <summary>The game's item level (<c>1D 56</c>).</summary>
    [JsonPropertyName("itemLevel")]
    public uint? ItemLevel { get; set; }

    [JsonPropertyName("bestItemLevel")]
    public uint? BestItemLevel { get; set; }

    [JsonPropertyName("itemLevelMs")]
    public long ItemLevelMs { get; set; }

    /// <summary>Kinah gained on the day of <see cref="KinahMs"/>.</summary>
    [JsonPropertyName("kinahToday")]
    public ulong? KinahToday { get; set; }

    [JsonPropertyName("kinahMs")]
    public long KinahMs { get; set; }

    /// <summary>Abyss Points gained in the week of <see cref="AbyssMs"/>, and the weekly cap.</summary>
    [JsonPropertyName("abyssPoints")]
    public ulong? AbyssPoints { get; set; }

    [JsonPropertyName("abyssCap")]
    public ulong? AbyssCap { get; set; }

    [JsonPropertyName("abyssMs")]
    public long AbyssMs { get; set; }

    /// <summary>Entry tickets and keys by id, as of <see cref="TicketsMs"/>.</summary>
    [JsonPropertyName("tickets")]
    public Dictionary<uint, ContentTicket> Tickets { get; set; } = new();

    [JsonPropertyName("ticketsMs")]
    public long TicketsMs { get; set; }

    /// <summary>The Daeva Pass seasons (experience this season and in the week of <see cref="PassMs"/>).</summary>
    [JsonPropertyName("pass")]
    public List<DaevaPassSeason> Pass { get; set; } = [];

    [JsonPropertyName("passMs")]
    public long PassMs { get; set; }

    /// <summary>Pets owned; null until the roster came. The pets and their souls belong to the account: every character gets the same.</summary>
    [JsonPropertyName("pets")]
    public HashSet<uint>? Pets { get; set; }

    /// <summary>Souls collected per pet kind.</summary>
    [JsonPropertyName("souls")]
    public Dictionary<uint, uint> Souls { get; set; } = new();

    [JsonPropertyName("petsMs")]
    public long PetsMs { get; set; }

    /// <summary>Pet kind → the soul item it drops (learned from the bag: names the pet).</summary>
    [JsonPropertyName("soulItems")]
    public Dictionary<uint, uint> SoulItems { get; set; } = new();

    [JsonPropertyName("updatedMs")]
    public long UpdatedMs { get; set; }

    /// <summary>Experience gained this session (from the first gain on); 0 = none yet.</summary>
    [JsonIgnore]
    public long SessionExp { get; set; }

    /// <summary>When this session's first and last experience arrived (Unix ms).</summary>
    [JsonIgnore]
    public long SessionExpFromMs { get; set; }

    [JsonIgnore]
    public long SessionExpLastMs { get; set; }

    /// <summary>Level ups this session (the experience within the level started over).</summary>
    [JsonIgnore]
    public int SessionLevelUps { get; set; }

    [JsonIgnore]
    private long? _expTotal;

    /// <summary>Pets not owned yet, most souls first (only kinds with souls).</summary>
    [JsonIgnore]
    public IEnumerable<PetSouls> NextPets => Pets is { } owned
        ? Souls.Where(s => s.Value > 0 && !owned.Contains(s.Key)).OrderByDescending(s => s.Value).ThenBy(s => s.Key).Select(s => new PetSouls(s.Key, s.Value))
        : [];

    /// <summary>
    /// Experience per hour this session, once it has run for a few minutes; null before (or without experience,
    /// e.g. at the level cap).
    /// </summary>
    public double? ExpPerHour(long nowMs)
    {
        // After a long pause (or for a character played earlier) the rate is the one it had while it lasted
        long end = nowMs - SessionExpLastMs > 15 * 60_000 ? SessionExpLastMs : nowMs;
        long span = end - SessionExpFromMs;
        return SessionExp > 0 && span >= 5 * 60_000 ? SessionExp * 3_600_000.0 / span : null;
    }

    /// <summary>Applies a progress record; true when something changed.</summary>
    public bool Apply(GameEvent e)
    {
        switch (e)
        {
            case SelfItemLevelEvent level:
                ItemLevel = level.ItemLevel;
                BestItemLevel = level.Best;
                ItemLevelMs = level.TimestampMs;
                break;
            case KinahLimitEvent kinah:
                KinahToday = kinah.Gained;
                KinahMs = kinah.TimestampMs;
                break;
            case AbyssPointLimitEvent abyss:
                AbyssPoints = abyss.Points;
                AbyssCap = abyss.Cap;
                AbyssMs = abyss.TimestampMs;
                break;
            case ContentTicketsEvent list:
                Tickets = list.Tickets.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.Last());
                TicketsMs = list.TimestampMs;
                break;
            case ContentTicketChangedEvent changed:
                Tickets[changed.Ticket.Id] = changed.Ticket;
                TicketsMs = changed.TimestampMs;
                break;
            case DaevaPassEvent pass:
                // Updates name one season each; the others stay as they were
                var seasons = Pass.ToDictionary(s => s.SeasonId);
                foreach (var season in pass.Seasons)
                {
                    seasons[season.SeasonId] = season;
                }

                Pass = seasons.Values.OrderBy(s => s.SeasonId).ToList();
                PassMs = pass.TimestampMs;
                break;
            case ExperienceEvent exp:
                if (_expTotal is { } before && exp.Total < before)
                {
                    SessionLevelUps++;
                }

                _expTotal = exp.Total;
                if (SessionExp == 0)
                {
                    SessionExpFromMs = exp.TimestampMs;
                }

                SessionExp += exp.Gained;
                SessionExpLastMs = exp.TimestampMs;
                break;
            case PetRosterEvent roster:
                Pets = roster.Owned.ToHashSet();
                Souls = roster.Souls.GroupBy(s => s.PetId).ToDictionary(g => g.Key, g => g.Last().Souls);
                PetsMs = roster.TimestampMs;
                break;
            // Souls and new pets only count on top of the roster: without it (the capture began after the login) the
            // counts would leave out what the account already had.
            case PetSoulsEvent when Pets is null:
            case PetAcquiredEvent when Pets is null:
                return false;
            case PetSoulsEvent souls:
                foreach (var change in souls.Changes)
                {
                    uint now = Souls.GetValueOrDefault(change.PetId);
                    Souls[change.PetId] = souls.Gained ? now + change.Souls : now - Math.Min(now, change.Souls);
                }

                PetsMs = souls.TimestampMs;
                break;
            case PetAcquiredEvent acquired:
                Pets.Add(acquired.PetId);
                PetsMs = acquired.TimestampMs;
                break;
            default:
                return false;
        }

        UpdatedMs = Math.Max(UpdatedMs, e.TimestampMs);
        return true;
    }

    /// <summary>
    /// Fills in what this session has not sent yet from an older record of the same character (each part as a
    /// whole: whichever arrived later wins).
    /// </summary>
    public void FillFrom(CharacterProgress old)
    {
        if (old.ItemLevelMs > ItemLevelMs)
        {
            (ItemLevel, BestItemLevel, ItemLevelMs) = (old.ItemLevel, old.BestItemLevel, old.ItemLevelMs);
        }

        if (old.KinahMs > KinahMs)
        {
            (KinahToday, KinahMs) = (old.KinahToday, old.KinahMs);
        }

        if (old.AbyssMs > AbyssMs)
        {
            (AbyssPoints, AbyssCap, AbyssMs) = (old.AbyssPoints, old.AbyssCap, old.AbyssMs);
        }

        if (old.TicketsMs > TicketsMs)
        {
            (Tickets, TicketsMs) = (new Dictionary<uint, ContentTicket>(old.Tickets), old.TicketsMs);
        }

        if (old.PassMs > PassMs)
        {
            (Pass, PassMs) = (old.Pass.ToList(), old.PassMs);
        }

        if (old.PetsMs > PetsMs)
        {
            (Pets, Souls, PetsMs) = (old.Pets?.ToHashSet(), new Dictionary<uint, uint>(old.Souls), old.PetsMs);
        }

        foreach (var (pet, item) in old.SoulItems)
        {
            SoulItems.TryAdd(pet, item);
        }

        UpdatedMs = Math.Max(UpdatedMs, old.UpdatedMs);
    }

    /// <summary>
    /// Continues this session's experience from an earlier start of the same character (logging in again sends
    /// everything anew, but the session goes on).
    /// </summary>
    public void ContinueSession(CharacterProgress earlier)
    {
        if (earlier.SessionExp == 0)
        {
            return;
        }

        SessionExpFromMs = SessionExp == 0 ? earlier.SessionExpFromMs : Math.Min(SessionExpFromMs, earlier.SessionExpFromMs);
        SessionExpLastMs = Math.Max(SessionExpLastMs, earlier.SessionExpLastMs);
        SessionExp += earlier.SessionExp;
        SessionLevelUps += earlier.SessionLevelUps;
        _expTotal ??= earlier._expTotal;
    }

    public CharacterProgress Copy()
    {
        var copy = new CharacterProgress { Character = Character };
        copy.FillFrom(this);
        copy.SessionExp = SessionExp;
        copy.SessionExpFromMs = SessionExpFromMs;
        copy.SessionExpLastMs = SessionExpLastMs;
        copy.SessionLevelUps = SessionLevelUps;
        copy._expTotal = _expTotal;
        return copy;
    }
}

/// <summary>When the game's daily and weekly counts last started over (from the reset times in <see cref="GameSchedule"/>).</summary>
public static class ProgressResets
{
    /// <summary>The start of the current game day.</summary>
    public static DateTimeOffset LastDaily(DateTimeOffset now, ServerRegion region) => Last("daily-reset", now, region, TimeSpan.FromDays(1));

    /// <summary>The start of the current game week.</summary>
    public static DateTimeOffset LastWeekly(DateTimeOffset now, ServerRegion region) => Last("weekly-reset", now, region, TimeSpan.FromDays(7));

    /// <summary>Whether something received at <paramref name="atMs"/> (Unix ms) is from the current day or week.</summary>
    public static bool Since(long atMs, DateTimeOffset reset) => atMs > 0 && atMs >= reset.ToUnixTimeMilliseconds();

    private static DateTimeOffset Last(string key, DateTimeOffset now, ServerRegion region, TimeSpan period) =>
        GameSchedule.Next(GameSchedule.Events(region).First(e => e.Key == key), now, region) - period;
}

/// <summary>
/// The progress of each of our characters, by name: the one played now stays current, the others as they were last
/// seen. Saved between sessions (progress.json), so Home shows a character's tickets and caps without a running meter.
/// </summary>
public sealed class ProgressBook
{
    [JsonInclude]
    [JsonPropertyName("characters")]
    private Dictionary<string, CharacterProgress> _byName = new();

    public CharacterProgress? For(string? name) => name is not null && _byName.TryGetValue(name, out var progress) ? progress : null;

    /// <summary>
    /// Takes the meter's progress for its character: what this session did not send again is kept from before.
    /// Returns the merged progress, or null when it can't be placed yet (the game has not named us).
    /// </summary>
    public CharacterProgress? Take(CharacterProgress live)
    {
        if (live.Character is not { } name)
        {
            return null;
        }

        var merged = live.Copy();
        if (For(name) is { } old)
        {
            merged.FillFrom(old);
        }

        _byName[name] = merged;
        return merged;
    }

    /// <summary>The newest pet roster of any of our characters (pets belong to the account); null when none came yet.</summary>
    public CharacterProgress? LatestPets() =>
        _byName.Values.Where(p => p.Pets is not null).MaxBy(p => p.PetsMs);

    /// <summary>The soul item of a pet kind, learned on any of our characters; null when never seen.</summary>
    public uint? SoulItem(uint pet)
    {
        foreach (var progress in _byName.Values)
        {
            if (progress.SoulItems.TryGetValue(pet, out uint item))
            {
                return item;
            }
        }

        return null;
    }
}

/// <summary>
/// Learns which soul item belongs to a pet kind: the soul lands in the bag (<c>1B 56</c>) about a second before the
/// game counts it for the pet (<c>0D 90</c>). Only a soul gain of a single kind with a single soul item in the bag
/// shortly before is taken.
/// </summary>
public sealed class SoulItemLearner
{
    /// <summary>Soul items ("Soul: Zelophi") have ids in this range.</summary>
    public static bool IsSoulItem(uint item) => item is >= 534_660_000 and < 534_670_000;

    private const long WindowMs = 3_000;

    private readonly Queue<(long At, uint Item)> _recent = new();

    /// <summary>Returns a learned (pet, soul item) pair, if this event settles one.</summary>
    public (uint Pet, uint Item)? Apply(GameEvent e)
    {
        switch (e)
        {
            case InventoryItemEvent { Item: { Count: > 0 } item } added when IsSoulItem(item.ItemId):
                _recent.Enqueue((added.TimestampMs, item.ItemId));
                return null;
            case PetSoulsEvent { Gained: true, Changes.Count: 1 } souls:
                while (_recent.Count > 0 && souls.TimestampMs - _recent.Peek().At > WindowMs)
                {
                    _recent.Dequeue();
                }

                var items = _recent.Select(r => r.Item).Distinct().ToList();
                _recent.Clear();
                return items.Count == 1 ? (souls.Changes[0].PetId, items[0]) : null;
            default:
                return null;
        }
    }
}

/// <summary>One figure of the progress block on Home: "Kinah today 412,300 / 1,000,000".</summary>
/// <param name="Of">Muted text after the value (" / 1,000,000"); empty for none.</param>
public sealed record ProgressStat(string Label, string Value, string Of = "", string? Tip = null)
{
    public bool HasTip => Tip is not null;
}

/// <summary>The figures of a character's progress worth showing now: only what the game sent, today's and this week's while they last.</summary>
public static class ProgressSummary
{
    private static string N(ulong value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    /// <param name="gearItemLevel">The worn gear's item levels added up (<see cref="InventoryState.GearItemLevel"/>), to split the item level.</param>
    public static IReadOnlyList<ProgressStat> Stats(CharacterProgress progress, DateTimeOffset now, ServerRegion region, int? gearItemLevel = null)
    {
        var stats = new List<ProgressStat>();
        var day = ProgressResets.LastDaily(now, region);
        var week = ProgressResets.LastWeekly(now, region);

        if (progress.ItemLevel is { } level)
        {
            string? tip = gearItemLevel is { } gear && gear <= level ? $"Gear {gear} + Daevanion {level - gear}" : null;
            if (progress.BestItemLevel is { } best && best > level)
            {
                tip = (tip is null ? string.Empty : tip + "\n") + $"Highest: {best}";
            }

            stats.Add(new ProgressStat("Item level", N(level), Tip: tip));
        }

        if (progress.KinahToday is { } kinah && ProgressResets.Since(progress.KinahMs, day))
        {
            stats.Add(new ProgressStat("Kinah today", N(kinah), $" / {N(CharacterProgress.DailyKinahLimit)}",
                "Kinah gained today toward the daily limit; starts over at the daily reset"));
        }

        if (progress.AbyssPoints is { } abyss && ProgressResets.Since(progress.AbyssMs, week))
        {
            stats.Add(new ProgressStat("Abyss Points this week", N(abyss), progress.AbyssCap is { } cap ? $" / {N(cap)}" : string.Empty,
                "Starts over at the weekly reset"));
        }

        if (ProgressResets.Since(progress.TicketsMs, day))
        {
            foreach (var (id, name) in TicketIds.Shown)
            {
                if (progress.Tickets.TryGetValue(id, out var ticket))
                {
                    stats.Add(ticket.Extra > 0
                        ? new ProgressStat(name, N(ticket.Count), $" +{N(ticket.Extra)}", $"{N(ticket.Count)} regular, {N(ticket.Extra)} charged")
                        : new ProgressStat(name, N(ticket.Count)));
                }
            }
        }

        if (progress.Pass.OrderByDescending(s => s.SeasonId).FirstOrDefault() is { } season)
        {
            string tip = $"Season {season.SeasonId}: {N(season.TotalExp)} XP";
            stats.Add(ProgressResets.Since(progress.PassMs, week)
                ? new ProgressStat("Daeva Pass", N(season.WeekExp), " XP this week", tip)
                : new ProgressStat("Daeva Pass", N(season.TotalExp), " XP this season"));
        }

        if (progress.ExpPerHour(now.ToUnixTimeMilliseconds()) is { } perHour)
        {
            string ups = progress.SessionLevelUps switch { 0 => string.Empty, 1 => ", 1 level up", var n => $", {n} level ups" };
            stats.Add(new ProgressStat("XP / hour", N((ulong)perHour), Tip: $"{N((ulong)progress.SessionExp)} XP this session{ups}"));
        }

        return stats;
    }

    /// <summary>"Pet souls: Zelophi 4/5 · Klaw 3/5 · 12 pets owned", or null before the game sent the pets.</summary>
    public static string? Pets(CharacterProgress progress, Func<uint, string> petName, int kinds = 5)
    {
        if (progress.Pets is not { } owned)
        {
            return null;
        }

        var next = progress.NextPets.ToList();
        var parts = next.Take(kinds).Select(p => $"{petName(p.PetId)} {Math.Min(p.Souls, CharacterProgress.SoulsPerPet)}/{CharacterProgress.SoulsPerPet}").ToList();
        if (next.Count > kinds)
        {
            parts.Add($"{next.Count - kinds} more");
        }

        string pets = owned.Count == 1 ? "1 pet owned" : $"{owned.Count} pets owned";
        return parts.Count == 0 ? pets : $"Pet souls: {string.Join(" · ", parts)} · {pets}";
    }
}
