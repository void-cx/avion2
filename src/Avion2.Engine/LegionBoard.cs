using System.Globalization;
using System.Text.Json;
using Avion2.Analysis;
using Avion2.Protocol;

namespace Avion2.Engine;

/// <summary>A legion on the Legions page, its name as shown (streamer mode: a stand-in).</summary>
/// <param name="Key">The legion's real name, to find it again; never shown.</param>
/// <param name="Share">The ranked value as a share of the leader's (0…1), for the bar.</param>
public sealed record LegionBoardRow(
    int Rank,
    string Key,
    string Name,
    int Members,
    int MembersWithPower,
    long CombatPower,
    long? AverageCombatPower,
    int FromSite,
    double Share,
    bool Own,
    long LastSeenMs,
    int Seen = 0,
    int? Listed = null,
    string? Leader = null,
    long? RosterMs = null);

/// <summary>A member of a legion on the Legions page, the name as shown.</summary>
/// <param name="FromSite">The combat power came from the official site (a character lookup).</param>
public sealed record LegionBoardMember(
    string Name,
    GameClass Class,
    uint? ItemLevel,
    uint? CombatPower,
    bool FromSite,
    bool Self,
    long LastSeenMs);

/// <summary>
/// The Legions page's data: the legions seen on a server, ranked, with names through streamer mode's stand-ins; and
/// where the legions seen are kept (%LocalAppData%\AVION2\legions_seen.json).
/// </summary>
public static class LegionBoard
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVION2", "legions_seen.json");

    /// <summary>The legions seen on a server, ranked; <paramref name="ownLegion"/> is marked (and named "Your legion" in streamer mode).</summary>
    public static IReadOnlyList<LegionBoardRow> Rows(LegionsSeenBook book, ushort serverId, LegionOrder order, string? ownLegion,
        NameMask names, long nowMs)
    {
        var standings = LegionRanking.Rank(book.On(serverId), order, nowMs);
        long top = standings.Count > 0 ? Math.Max(1, LegionRanking.Score(standings[0], order)) : 1;
        return standings.Select(s =>
        {
            bool own = ownLegion is not null && s.Legion.Name == ownLegion;
            string? leader = s.Listed is not null && s.Legion.Listed?.Leader is { Length: > 0 } l ? names.Name(l) : null;
            return new LegionBoardRow(s.Rank, s.Legion.Name, names.LegionInList(s.Legion.Name, own), s.Members, s.MembersWithPower,
                s.CombatPower, s.AverageCombatPower, s.FromSite, Math.Clamp((double)LegionRanking.Score(s, order) / top, 0, 1), own,
                s.LastSeenMs, s.Seen, s.Listed, leader, s.Legion.RosterMs);
        }).ToList();
    }

    /// <summary>A legion's members seen within <see cref="LegionsSeenBook.StaleAfter"/>, strongest first (unknown combat power last).</summary>
    public static IReadOnlyList<LegionBoardMember> Members(LegionsSeenBook book, ushort serverId, string legion, NameMask names, long nowMs)
    {
        if (book.Find(serverId, legion) is not { } seen)
        {
            return [];
        }

        long cutoff = nowMs - (long)LegionsSeenBook.StaleAfter.TotalMilliseconds;
        return seen.Members
            .Where(m => m.LastSeenMs >= cutoff)
            .OrderByDescending(m => m.CombatPower ?? 0)
            .ThenByDescending(m => m.ItemLevel ?? 0)
            .ThenBy(m => m.Name, StringComparer.CurrentCulture)
            .Select(m => new LegionBoardMember(names.Name(m.Name), m.Class, m.ItemLevel, m.CombatPower, m.Power == PowerSource.Site,
                names.Self is { } self && m.Name == self, m.LastSeenMs))
            .ToList();
    }

    /// <summary>The members' average item level, of those it is known of; null when none.</summary>
    public static long? AverageItemLevel(IEnumerable<LegionBoardMember> members)
    {
        var known = members.Where(m => m.ItemLevel is > 0).Select(m => (long)m.ItemLevel!.Value).ToList();
        return known.Count > 0 ? (long)Math.Round(known.Average()) : null;
    }

    /// <summary>"25 Sorcerer · 18 Gladiator · …", the most first; members of a class not known left out.</summary>
    public static string ClassMix(IEnumerable<LegionBoardMember> members) =>
        string.Join(" · ", members.Where(m => m.Class != GameClass.Unknown).GroupBy(m => m.Class)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}"));

    /// <summary>A character looked up on the official site, as a sighting (null when the site names no legion).</summary>
    public static LegionSighting? FromProfile(ArmoryProfile profile, GameClass cls) =>
        profile.Legion is { Length: > 0 } legion && profile.Name.Length > 0
            ? new LegionSighting(profile.Name, profile.ServerId, legion, cls, profile.ItemLevel is > 0 ? (uint)profile.ItemLevel : null,
                profile.CombatPower > 0 ? (uint)profile.CombatPower : null, profile.FetchedAt.ToUnixTimeMilliseconds(), PowerSource.Site)
            : null;

    /// <summary>"just now", "12 min ago", "3 h ago", "5 days ago".</summary>
    public static string Ago(long thenMs, long nowMs)
    {
        var ago = TimeSpan.FromMilliseconds(Math.Max(0, nowMs - thenMs));
        return ago.TotalMinutes < 1 ? "just now"
            : ago.TotalHours < 1 ? $"{(int)ago.TotalMinutes} min ago"
            : ago.TotalDays < 1 ? $"{(int)ago.TotalHours} h ago"
            : (int)ago.TotalDays == 1 ? "1 day ago"
            : $"{(int)ago.TotalDays} days ago";
    }

    /// <summary>"1,234,567".</summary>
    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The saved legions (stale members dropped); empty when there is no file or it can't be read.</summary>
    public static LegionsSeenBook Load(string? path = null, long? nowMs = null)
    {
        path ??= DefaultPath;
        try
        {
            var book = File.Exists(path) ? JsonSerializer.Deserialize<LegionsSeenBook>(File.ReadAllText(path)) ?? new() : new();
            book.Prune(nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return book;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new();
        }
    }

    /// <summary>Saves the legions (stale members dropped first); false when the file can't be written.</summary>
    public static bool Save(LegionsSeenBook book, string? path = null, long? nowMs = null)
    {
        path ??= DefaultPath;
        book.Prune(nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(book));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
