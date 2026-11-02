using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>An item's item level (before enchanting), slot and enchant effect group.</summary>
public sealed record GearStatsItem(
    [property: JsonPropertyName("itemLevel")] int ItemLevel,
    [property: JsonPropertyName("slot")] string? Slot,
    [property: JsonPropertyName("effect")] string? Effect);

/// <summary>What a stat changes by ("Defense +14", "PvE damage +0.5%").</summary>
public sealed record StatGain(string Stat, double Value)
{
    /// <summary>Stats the game stores ×100 and shows as percent.</summary>
    private static readonly HashSet<string> Percent = ["DefensePierce", "PvEDecreaseDamage", "PvEAmplifyDamage", "CombatSpeed", "AdditionalHitRate"];

    public string Name => Stat switch
    {
        "ArmorDefense" => "Defense",
        "HPMax" => "HP",
        "WeaponFixingDamage" or "WeaponDamage" => "Attack",
        "WeaponMinDamage" => "Min attack",
        "DefensePierce" => "Defense penetration",
        "SealStoneAddDamage" => "Seal stone damage",
        "PvEDecreaseDamage" => "PvE damage taken −",
        "PvEAmplifyDamage" => "PvE damage",
        "CombatSpeed" => "Combat speed",
        "AdditionalHitRate" => "Accuracy",
        _ => Stat,
    };

    public override string ToString() => Percent.Contains(Stat)
        ? $"{Name} +{(Value / 100).ToString("0.##", CultureInfo.InvariantCulture)}%"
        : $"{Name} +{Value.ToString("N0", CultureInfo.InvariantCulture)}";
}

/// <summary>
/// Item levels and the stats every enchant level adds, datamined from the game (assets/gamedata/gear_stats.json).
/// An item's item level is its base level plus its enchant level (each enchant step adds 1).
/// </summary>
public sealed class GearStats
{
    public static GearStats Empty { get; } = new(new Dictionary<string, GearStatsItem>(), new Dictionary<string, Dictionary<string, Dictionary<string, double>>>());

    private readonly IReadOnlyDictionary<string, GearStatsItem> _items;
    private readonly IReadOnlyDictionary<string, Dictionary<string, Dictionary<string, double>>> _effects;

    private GearStats(IReadOnlyDictionary<string, GearStatsItem> items, IReadOnlyDictionary<string, Dictionary<string, Dictionary<string, double>>> effects)
    {
        _items = items;
        _effects = effects;
    }

    public static GearStats Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        var file = JsonSerializer.Deserialize<StatsFile>(json) ?? throw new JsonException("empty gear stats table");
        return new GearStats(file.Items ?? new(), file.Effects ?? new());
    }

    public GearStatsItem? Item(long id) => _items.GetValueOrDefault(id.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// What going from <paramref name="from"/> to <paramref name="to"/> adds, per stat. The table holds the totals
    /// at each level (+0 adds nothing); weapons list min and max attack as one "Attack".
    /// </summary>
    public IReadOnlyList<StatGain> Gain(string effect, int from, int to)
    {
        if (!_effects.TryGetValue(effect, out var levels))
        {
            return [];
        }

        Dictionary<string, double> At(int level) =>
            levels.TryGetValue(level.ToString(CultureInfo.InvariantCulture), out var stats) ? stats : new Dictionary<string, double>();

        var before = At(from);
        var after = At(to);
        var gains = after.Keys.Union(before.Keys)
            .Select(stat => new StatGain(stat, after.GetValueOrDefault(stat) - before.GetValueOrDefault(stat)))
            .Where(g => g.Value > 0)
            .ToList();
        if (gains.Any(g => g.Stat == "WeaponDamage") && gains.FirstOrDefault(g => g.Stat == "WeaponMinDamage") is { } min &&
            min.Value == gains.First(g => g.Stat == "WeaponDamage").Value)
        {
            gains.Remove(min);
        }

        return gains;
    }

    private sealed class StatsFile
    {
        [JsonPropertyName("items")]
        public Dictionary<string, GearStatsItem>? Items { get; set; }

        [JsonPropertyName("effects")]
        public Dictionary<string, Dictionary<string, Dictionary<string, double>>>? Effects { get; set; }
    }
}

/// <summary>How much of a material something takes on average.</summary>
public sealed record ExpectedMaterial(string Name, double Count, uint? Id = null);

/// <summary>The next enchant step of one equipped item: what it costs on average and what it gives.</summary>
/// <param name="Kinah">Expected Kinah (attempts × Kinah per attempt).</param>
/// <param name="Materials">Expected materials by name.</param>
/// <param name="Chance">Success chance of the first attempt, percent.</param>
public sealed record GearUpgrade(
    InventoryItem Slot,
    ForgeItem Item,
    int From,
    int ItemLevel,
    double Kinah,
    IReadOnlyList<ExpectedMaterial> Materials,
    IReadOnlyList<StatGain> Stats,
    double Chance)
{
    /// <summary>Expected Kinah per item level gained (what the list is sorted by).</summary>
    public double KinahPerItemLevel => Kinah / Math.Max(1, ItemLevel);
}

/// <summary>Which enchant step on your equipped gear gives the most item level for the Kinah.</summary>
public static class GearUpgrades
{
    /// <summary>The next enchant step of every equipped item that can still go up, cheapest item level first.</summary>
    public static IReadOnlyList<GearUpgrade> Next(IEnumerable<InventoryItem> equipped, GearForge forge, GearStats stats)
    {
        var upgrades = new List<GearUpgrade>();
        foreach (var slot in equipped)
        {
            if (forge.Find(slot.ItemId) is not { } item || forge.Ladder(item, ForgeMode.Enchant) is not { } ladder)
            {
                continue;
            }

            int from = slot.Enchant ?? 0;
            if (ladder.Steps.FirstOrDefault(s => s.Level == from) is not { } step)
            {
                continue; // at the top of the ladder
            }

            double attempts = step.ExpectedAttempts;
            var materials = step.Materials
                .GroupBy(m => m.Name)
                .Select(g => new ExpectedMaterial(g.Key, attempts * g.Sum(m => m.Count), g.First().Id))
                .ToList();
            var gains = stats.Item(slot.ItemId)?.Effect is { } effect ? stats.Gain(effect, from, from + 1) : [];
            upgrades.Add(new GearUpgrade(slot, item, from, 1, attempts * step.Gold, materials, gains, step.Chance));
        }

        return upgrades.OrderBy(u => u.KinahPerItemLevel).ThenBy(u => u.Item.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Every remaining step that can't fail (100% chance) on the equipped gear, together: item level gained and
    /// expected Kinah and materials.
    /// </summary>
    public static (int ItemLevel, double Kinah, IReadOnlyList<ExpectedMaterial> Materials) SafeSteps(IEnumerable<InventoryItem> equipped, GearForge forge)
    {
        int levels = 0;
        double kinah = 0;
        var materials = new Dictionary<string, double>();
        foreach (var slot in equipped)
        {
            if (forge.Find(slot.ItemId) is not { } item || forge.Ladder(item, ForgeMode.Enchant) is not { } ladder)
            {
                continue;
            }

            foreach (var step in ladder.Steps.Where(s => s.Level >= (slot.Enchant ?? 0)).OrderBy(s => s.Level).TakeWhile(s => s.Chance >= 100))
            {
                levels++;
                kinah += step.Gold;
                foreach (var material in step.Materials)
                {
                    materials[material.Name] = materials.GetValueOrDefault(material.Name) + material.Count;
                }
            }
        }

        return (levels, kinah, materials.Select(m => new ExpectedMaterial(m.Key, m.Value)).ToList());
    }
}
