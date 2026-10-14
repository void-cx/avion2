using System.Text.Json;
using System.Text.Json.Serialization;

namespace Avion2.Analysis;

public enum ForgeMode
{
    /// <summary>Enchant (+0 → +15 on Unique gear).</summary>
    Enchant,

    /// <summary>Amplified enhance (exceed), after the enchant ladder is maxed.</summary>
    Amplify,
}

public sealed record ForgeMaterial(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("count")] long Count);

/// <summary>One upgrade step: from <see cref="Level"/> to the next.</summary>
/// <param name="Chance">Base success chance in percent.</param>
/// <param name="Pity">Percent added to the chance after each failed attempt at this step (reset on success).</param>
/// <param name="Gold">Kinah per attempt.</param>
/// <param name="Materials">Materials per attempt.</param>
public sealed record ForgeStep(
    [property: JsonPropertyName("level")] int Level,
    [property: JsonPropertyName("chance")] double Chance,
    [property: JsonPropertyName("pity")] double Pity,
    [property: JsonPropertyName("gold")] long Gold,
    [property: JsonPropertyName("materials")] IReadOnlyList<ForgeMaterial> Materials)
{
    /// <summary>Success chance of the <paramref name="attempt"/>-th try (1-based), pity included.</summary>
    public double ChanceAt(int attempt) => Math.Min(100, Chance + Pity * (attempt - 1)) / 100;

    /// <summary>Expected attempts until success (exact).</summary>
    public double ExpectedAttempts
    {
        get
        {
            if (Chance <= 0 && Pity <= 0)
            {
                return double.PositiveInfinity;
            }

            if (Pity <= 0)
            {
                return 100 / Chance;
            }

            // E = Σ P(attempt k is needed) = Σ Π_{j<k} (1 - p_j)
            double expected = 0, reach = 1;
            for (int k = 1; reach > 1e-12; k++)
            {
                expected += reach;
                reach *= 1 - ChanceAt(k);
            }

            return expected;
        }
    }
}

public sealed record ForgeLadder(
    [property: JsonPropertyName("grade")] string Grade,
    [property: JsonPropertyName("steps")] IReadOnlyList<ForgeStep> Steps)
{
    public int MaxLevel => Steps.Count == 0 ? 0 : Steps.Max(s => s.Level) + 1;
}

public sealed record ForgeItem(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("enchant")] string? Enchant,
    [property: JsonPropertyName("amplify")] string? Amplify);

/// <summary>Average and unlucky (90th percentile) cost of one material, Kinah or attempts.</summary>
public sealed record ForgeCost(string Name, double Average, double P90);

public sealed record ForgeStepResult(ForgeStep Step, double ExpectedAttempts, double ExpectedMaterials);

/// <summary>Cost of going from one level to another.</summary>
public sealed record ForgePlan(
    ForgeCost Attempts,
    ForgeCost Gold,
    IReadOnlyList<ForgeCost> Materials,
    IReadOnlyList<ForgeStepResult> Steps);

/// <summary>
/// Enchant and amplify ladders per item, datamined from the game (assets/gamedata/gear_forge.json). Each attempt
/// costs the step's Kinah and materials; a failure keeps the level and raises the next attempt's chance by the
/// step's pity until it succeeds.
/// </summary>
public sealed class GearForge
{
    public static GearForge Empty { get; } = new([], new Dictionary<string, ForgeLadder>(), new Dictionary<string, ForgeLadder>());

    private readonly Dictionary<string, ForgeLadder> _enchant;
    private readonly Dictionary<string, ForgeLadder> _amplify;

    public GearForge(IReadOnlyList<ForgeItem> items, IReadOnlyDictionary<string, ForgeLadder> enchant, IReadOnlyDictionary<string, ForgeLadder> amplify)
    {
        _enchant = new Dictionary<string, ForgeLadder>(enchant);
        _amplify = new Dictionary<string, ForgeLadder>(amplify);

        // The game has several ids per item (e.g. per faction); one entry per name + ladder is enough here.
        Items = items
            .Where(i => i.Enchant is not null && _enchant.ContainsKey(i.Enchant) || i.Amplify is not null && _amplify.ContainsKey(i.Amplify))
            .DistinctBy(i => (i.Name, i.Kind, i.Enchant, i.Amplify))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<ForgeItem> Items { get; }

    public static GearForge Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        var file = JsonSerializer.Deserialize<ForgeFile>(json) ?? throw new JsonException("empty gear forge table");
        return new GearForge(file.Items ?? [], file.Enchant ?? new(), file.Amplify ?? new());
    }

    public ForgeLadder? Ladder(ForgeItem item, ForgeMode mode) => mode switch
    {
        ForgeMode.Enchant when item.Enchant is { } key => _enchant.GetValueOrDefault(key),
        ForgeMode.Amplify when item.Amplify is { } key => _amplify.GetValueOrDefault(key),
        _ => null,
    };

    /// <summary>Items whose name contains every word of <paramref name="query"/> (case-insensitive).</summary>
    public IEnumerable<ForgeItem> Search(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Items.Where(i => words.All(w => i.Name.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                                               i.Kind.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Cost from <paramref name="from"/> to <paramref name="to"/>. Averages are exact; the 90th percentiles come
    /// from <paramref name="runs"/> simulated runs with a fixed seed, so the same input always shows the same numbers.
    /// </summary>
    public static ForgePlan Plan(ForgeLadder ladder, int from, int to, int runs = 20_000)
    {
        var steps = ladder.Steps.Where(s => s.Level >= from && s.Level < to).OrderBy(s => s.Level).ToList();
        var materialNames = steps.SelectMany(s => s.Materials.Select(m => m.Name)).Distinct().ToList();

        var perStep = steps.Select(s =>
        {
            double attempts = s.ExpectedAttempts;
            return new ForgeStepResult(s, attempts, attempts * s.Materials.Sum(m => m.Count));
        }).ToList();

        // Simulation for the unlucky case
        var random = new Random(1);
        var attemptRuns = new double[runs];
        var goldRuns = new double[runs];
        var materialRuns = materialNames.ToDictionary(n => n, _ => new double[runs]);
        for (int run = 0; run < runs; run++)
        {
            foreach (var step in steps)
            {
                int attempt = 1;
                while (random.NextDouble() >= step.ChanceAt(attempt))
                {
                    attempt++;
                    if (attempt > 10_000)
                    {
                        break; // 0% and no pity: never succeeds
                    }
                }

                attemptRuns[run] += attempt;
                goldRuns[run] += (double)attempt * step.Gold;
                foreach (var material in step.Materials)
                {
                    materialRuns[material.Name][run] += (double)attempt * material.Count;
                }
            }
        }

        double avgAttempts = perStep.Sum(s => s.ExpectedAttempts);
        double avgGold = perStep.Sum(s => s.ExpectedAttempts * s.Step.Gold);
        var materials = materialNames.Select(name => new ForgeCost(
            name,
            perStep.Sum(s => s.ExpectedAttempts * s.Step.Materials.Where(m => m.Name == name).Sum(m => m.Count)),
            Percentile(materialRuns[name], 0.9))).ToList();

        return new ForgePlan(
            new ForgeCost("Attempts", avgAttempts, Percentile(attemptRuns, 0.9)),
            new ForgeCost("Kinah", avgGold, Percentile(goldRuns, 0.9)),
            materials,
            perStep);
    }

    private static double Percentile(double[] values, double p)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    private sealed class ForgeFile
    {
        [JsonPropertyName("items")]
        public List<ForgeItem>? Items { get; set; }

        [JsonPropertyName("enchant")]
        public Dictionary<string, ForgeLadder>? Enchant { get; set; }

        [JsonPropertyName("amplify")]
        public Dictionary<string, ForgeLadder>? Amplify { get; set; }
    }
}
