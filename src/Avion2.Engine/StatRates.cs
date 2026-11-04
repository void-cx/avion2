using System.Globalization;
using System.Text.Json;

namespace Avion2.Engine;

/// <summary>One stat an attribute raises: <paramref name="PerPoint"/> percent per point (negative where it lowers it).</summary>
/// <param name="Stat">The game's key ("HardHit").</param>
/// <param name="Name">The game's English name ("Double Chance"), as the official site words it.</param>
public sealed record StatRate(string Stat, string Name, double PerPoint);

/// <summary>An attribute or deity stat the calculator takes, and what each point of it gives.</summary>
/// <param name="Key">The game's key, the same the official site's stat list uses as its type ("STR", "Wisdom").</param>
/// <param name="Name">"Might", "Wisdom [Lumiel]".</param>
/// <param name="IsDeity">One of the ten deity stats of the outer ring (else one of the six attributes).</param>
public sealed record StatInput(string Key, string Name, bool IsDeity, IReadOnlyList<StatRate> Gives)
{
    /// <summary>"Wisdom" of "Wisdom [Lumiel]".</summary>
    public string ShortName => Name.IndexOf(" [", StringComparison.Ordinal) is var i and > 0 ? Name[..i] : Name;

    /// <summary>"Lumiel" of "Wisdom [Lumiel]"; null for the attributes.</summary>
    public string? Lord => Name.IndexOf(" [", StringComparison.Ordinal) is var i and > 0 ? Name[(i + 2)..].TrimEnd(']') : null;
}

/// <summary>A derived stat, in percent, added up over every attribute that gives it.</summary>
public sealed record DerivedStat(string Stat, string Name, double Value);

/// <summary>A derived stat for the values entered beside the same for a character's own values.</summary>
public sealed record DerivedChange(string Stat, string Name, double Value, double Baseline)
{
    public double Difference => Math.Round(Value - Baseline, 4);
}

/// <summary>
/// How the six attributes and the ten deity stats convert into derived stats (assets/gamedata/stat_rates.json, see
/// PROVENANCE.md): linear, a fixed percentage per point. Only the conversions: the character sheet in game also adds
/// flat stats from class, level, gear, manastones, skills and buffs.
/// </summary>
public sealed class StatRates
{
    private static readonly Lazy<StatRates> BundledTable = new(() => Parse(BundledGameData.Read("stat_rates.json")));

    private readonly Dictionary<string, StatInput> _byKey;

    private StatRates(IReadOnlyList<StatInput> inputs, string? built)
    {
        Inputs = inputs;
        Built = built;
        _byKey = inputs.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The bundled table.</summary>
    public static StatRates Bundled => BundledTable.Value;

    /// <summary>The attributes first, then the deity stats, in the order the game lists them.</summary>
    public IReadOnlyList<StatInput> Inputs { get; }

    /// <summary>When the source built its extract of the game data ("2026-09-19"); null when not given.</summary>
    public string? Built { get; }

    public StatInput? Input(string key) => _byKey.GetValueOrDefault(key);

    public static StatRates Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new StatRates([], null);
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var inputs = root.GetProperty("inputs").EnumerateArray().Select(i => new StatInput(
            i.GetProperty("key").GetString() ?? string.Empty,
            i.GetProperty("name").GetString() ?? string.Empty,
            i.TryGetProperty("group", out var group) && group.GetString() == "deity",
            i.GetProperty("gives").EnumerateArray().Select(g => new StatRate(
                g.GetProperty("stat").GetString() ?? string.Empty,
                g.GetProperty("name").GetString() ?? string.Empty,
                g.GetProperty("per").GetDouble())).ToList())).ToList();
        return new StatRates(inputs, root.TryGetProperty("built", out var built) ? built.GetString() : null);
    }

    /// <summary>What one input's points give ("Double Chance +17.8%"), in the table's order.</summary>
    public IReadOnlyList<DerivedStat> Gives(string key, int points) =>
        Input(key) is { } input && points != 0
            ? input.Gives.Select(r => new DerivedStat(r.Stat, r.Name, Round(points * r.PerPoint))).ToList()
            : [];

    /// <summary>
    /// Every derived stat the values give: Σ value × rate per point, over all inputs (Might and Destruction both
    /// raise Attack). In the order the stats first appear in the table; stats that come to 0 are left out.
    /// </summary>
    public IReadOnlyList<DerivedStat> Convert(IReadOnlyDictionary<string, int> values)
    {
        var totals = new Dictionary<string, (string Name, double Value, int Order)>();
        foreach (var input in Inputs)
        {
            foreach (var rate in input.Gives)
            {
                int points = values.GetValueOrDefault(input.Key);
                var (_, value, order) = totals.GetValueOrDefault(rate.Stat, (rate.Name, 0.0, totals.Count));
                totals[rate.Stat] = (rate.Name, value + points * rate.PerPoint, order);
            }
        }

        return totals.Where(t => Round(t.Value.Value) != 0).OrderBy(t => t.Value.Order)
            .Select(t => new DerivedStat(t.Key, t.Value.Name, Round(t.Value.Value))).ToList();
    }

    /// <summary>
    /// The derived stats for <paramref name="values"/> beside those for <paramref name="baseline"/> (a character's own
    /// values): every stat either side gives, in the table's order.
    /// </summary>
    public IReadOnlyList<DerivedChange> Compare(IReadOnlyDictionary<string, int> values, IReadOnlyDictionary<string, int> baseline)
    {
        var now = Convert(values).ToDictionary(d => d.Stat);
        var before = Convert(baseline).ToDictionary(d => d.Stat);
        return Inputs.SelectMany(i => i.Gives).DistinctBy(r => r.Stat)
            .Where(r => now.ContainsKey(r.Stat) || before.ContainsKey(r.Stat))
            .Select(r => new DerivedChange(r.Stat, r.Name, now.GetValueOrDefault(r.Stat)?.Value ?? 0, before.GetValueOrDefault(r.Stat)?.Value ?? 0))
            .ToList();
    }

    /// <summary>
    /// A character's values from the official site's stat list, by the type it gives each stat ("STR", "Wisdom"), else
    /// by name; stats the table doesn't convert (item level, Light, Darkness) are left out.
    /// </summary>
    public IReadOnlyDictionary<string, int> FromArmory(IEnumerable<ArmoryStat> stats)
    {
        var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var stat in stats)
        {
            var input = Input(stat.Type) ?? Inputs.FirstOrDefault(i => string.Equals(i.Name, stat.Name, StringComparison.OrdinalIgnoreCase));
            if (input is not null)
            {
                values[input.Key] = stat.Value;
            }
        }

        return values;
    }

    /// <summary>
    /// What a change of one input gives: "+20 Wisdom → Double Chance +2%, MP Cost −2%"; null when the value is the same
    /// or the input gives nothing.
    /// </summary>
    public string? ChangeLine(string key, int from, int to)
    {
        if (Input(key) is not { } input || from == to || input.Gives.Count == 0)
        {
            return null;
        }

        int delta = to - from;
        string sign = delta > 0 ? "+" : "−";
        return $"{sign}{Math.Abs(delta).ToString(CultureInfo.InvariantCulture)} {input.ShortName} → " +
               string.Join(", ", Gives(key, delta).Select(d => $"{d.Name} {Percent(d.Value)}"));
    }

    /// <summary>"+17.8%", "−2%", "0%".</summary>
    public static string Percent(double value)
    {
        value = Round(value);
        string number = Math.Abs(value).ToString("0.##", CultureInfo.InvariantCulture);
        return value > 0 ? $"+{number}%" : value < 0 ? $"−{number}%" : "0%";
    }

    private static double Round(double value) => Math.Round(value, 4);
}
