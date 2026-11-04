using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avion2.Analysis;
using Avion2.Protocol;

namespace Avion2.Engine;

/// <summary>
/// Settings → Developer → Export legions: our characters' legions as JSON, each with its whole member list as the game
/// last sent it (<c>05 8A</c>) and summed up: size, roles, classes, item level and combat power.
/// </summary>
public static class LegionExport
{
    /// <summary>The role a class plays in a group, as legions usually count them.</summary>
    public static string Role(GameClass cls) => cls switch
    {
        GameClass.Templar => "Tank",
        GameClass.Cleric => "Healer",
        GameClass.Chanter => "Support",
        GameClass.Unknown => "Unknown",
        _ => "DPS",
    };

    /// <summary>The class as the game names it on Global servers.</summary>
    public static string ClassName(GameClass cls) => cls switch
    {
        GameClass.Elementalist => "Spiritmaster",
        GameClass.Fighter => "Brawler",
        _ => cls.ToString(),
    };

    private static string Grade(byte? grade) => grade switch
    {
        0 => "Leader",
        1 => "Officer",
        2 => "Member",
        null => "Unknown",
        _ => $"Grade {grade}",
    };

    private static readonly string[] Roles = ["Tank", "Healer", "Support", "DPS", "Unknown"];

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // names and texts readable as they are
    };

    /// <summary>
    /// The legions with a member list, the newest list of each (two of our characters in one legion: the newer one's),
    /// as indented JSON. Returns the JSON and how many legions it holds.
    /// </summary>
    public static (string Json, int Legions) Build(IEnumerable<LegionState> legions, DateTimeOffset now, Func<ushort, string?>? serverName = null)
    {
        var picked = legions
            .Where(l => l is { IsKnown: true, NoLegion: false, RosterMs: not null } && l.Members.Count > 0)
            .GroupBy(l => l.Name!)
            .Select(g => g.MaxBy(l => l.RosterMs)!)
            .OrderByDescending(l => l.Members.Count)
            .ThenBy(l => l.Name, StringComparer.Ordinal)
            .ToList();
        var export = new Dictionary<string, object?>
        {
            ["exported"] = now.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            ["source"] = "AVION2: the legion member list the game sends to a legion's members at login and when the legion window opens",
            ["notes"] = new[]
            {
                "itemLevel is the equipped item level the game shows (often called gear score); combatPower is the combat power.",
                "Roles by class: Templar = Tank, Cleric = Healer, Chanter = Support, every other class = DPS.",
                "p25/p75: a quarter of the members are below p25, a quarter above p75.",
                "lastLogout is when the member last logged out (local time), also for members online now.",
            },
            ["legions"] = picked.Select(l => Legion(l, serverName)).ToList(),
        };
        return (JsonSerializer.Serialize(export, Options), picked.Count);
    }

    private static Dictionary<string, object?> Legion(LegionState legion, Func<ushort, string?>? serverName)
    {
        var members = legion.Members;
        ushort? server = members.Where(m => m.ServerId is not null).GroupBy(m => m.ServerId!.Value)
            .OrderByDescending(g => g.Count()).Select(g => (ushort?)g.Key).FirstOrDefault();
        return new Dictionary<string, object?>
        {
            ["name"] = legion.Name,
            ["server"] = server is { } id ? serverName?.Invoke(id) ?? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            ["serverId"] = server,
            ["asOf"] = Time(legion.RosterMs),
            ["character"] = legion.Character,
            ["leader"] = members.FirstOrDefault(m => m.Grade == 0)?.Name ?? legion.Leader,
            ["officers"] = members.Where(m => m.Grade == 1).Select(m => m.Name).Order(StringComparer.CurrentCulture).ToList(),
            ["size"] = new Dictionary<string, object?>
            {
                ["members"] = members.Count,
                ["max"] = legion.MaxMembers,
                ["free"] = legion.MaxMembers is { } max ? Math.Max(0, max - members.Count) : null,
                ["full"] = legion.MaxMembers is { } most ? members.Count >= most : null,
                ["level45"] = members.Count(m => m.Level >= 45),
            },
            ["itemLevel"] = Spread(members.Select(m => m.ItemLevel)),
            ["combatPower"] = Spread(members.Select(m => m.CombatPower), total: true),
            ["roles"] = Roles
                .Select(r => (Role: r, Members: members.Where(m => Role(m.Class) == r).ToList()))
                .Where(x => x.Members.Count > 0)
                .ToDictionary(x => x.Role, x => Group(x.Members, members.Count)),
            ["classes"] = members.GroupBy(m => m.Class)
                .OrderByDescending(g => g.Count()).ThenBy(g => ClassName(g.Key), StringComparer.Ordinal)
                .ToDictionary(g => ClassName(g.Key), g =>
                {
                    var group = Group(g.ToList(), members.Count);
                    group["role"] = Role(g.Key);
                    return group;
                }),
            ["introduction"] = legion.Texts.FirstOrDefault(),
            ["members"] = members
                .OrderBy(m => m.Grade ?? byte.MaxValue)
                .ThenByDescending(m => m.CombatPower ?? 0)
                .ThenBy(m => m.Name, StringComparer.CurrentCulture)
                .Select(m => new Dictionary<string, object?>
                {
                    ["name"] = m.Name,
                    ["class"] = ClassName(m.Class),
                    ["role"] = Role(m.Class),
                    ["level"] = m.Level,
                    ["grade"] = Grade(m.Grade),
                    ["itemLevel"] = m.ItemLevel,
                    ["combatPower"] = m.CombatPower,
                    ["lastLogout"] = Time(m.LastOnlineMs),
                })
                .ToList(),
        };
    }

    private static Dictionary<string, object?> Group(IReadOnlyList<LegionMember> group, int all) => new()
    {
        ["count"] = group.Count,
        ["sharePercent"] = Math.Round(100.0 * group.Count / all, 1),
        ["itemLevel"] = Spread(group.Select(m => m.ItemLevel)),
        ["averageCombatPower"] = Spread(group.Select(m => m.CombatPower))?["average"],
    };

    /// <summary>Min, max, average, median and quartiles of the values known; null when none is.</summary>
    private static Dictionary<string, object?>? Spread(IEnumerable<uint?> values, bool total = false)
    {
        var known = values.Where(v => v is > 0).Select(v => (double)v!.Value).Order().ToList();
        if (known.Count == 0)
        {
            return null;
        }

        var spread = new Dictionary<string, object?>();
        if (total)
        {
            spread["total"] = (long)known.Sum();
        }

        spread["known"] = known.Count;
        spread["min"] = (long)known[0];
        spread["max"] = (long)known[^1];
        spread["average"] = (long)Math.Round(known.Average());
        spread["median"] = (long)Math.Round(Quantile(known, 0.5));
        spread["p25"] = (long)Math.Round(Quantile(known, 0.25));
        spread["p75"] = (long)Math.Round(Quantile(known, 0.75));
        return spread;
    }

    /// <summary>The q-quantile of sorted values, interpolated between the two nearest.</summary>
    private static double Quantile(IReadOnlyList<double> sorted, double q)
    {
        double at = (sorted.Count - 1) * q;
        int low = (int)Math.Floor(at);
        int high = Math.Min(low + 1, sorted.Count - 1);
        return sorted[low] + (sorted[high] - sorted[low]) * (at - low);
    }

    private static string? Time(long? ms) =>
        ms is { } at and > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz") : null;
}
