using Avion2.Analysis;

namespace Avion2.Engine;

/// <summary>One gear slot of two characters: what each wears there and its item level (base + enchant).</summary>
public sealed record GearPair(int Slot, string SlotName, ArmoryItem? Mine, ArmoryItem? Theirs, int? MineLevel, int? TheirsLevel)
{
    /// <summary>Their item level minus ours; null when either is unknown.</summary>
    public int? Difference => MineLevel is { } mine && TheirsLevel is { } theirs ? theirs - mine : null;
}

/// <summary>A number both characters have (a stat, a board's open nodes, a skill's level).</summary>
public sealed record ValuePair(string Name, int Mine, int Theirs, int Total = 0)
{
    public int Difference => Theirs - Mine;
}

/// <summary>
/// Two characters from the official site side by side: ours and someone else's. Gear by slot (with item levels from
/// the datamined table), stats, Daevanion boards and, for the same class, skill levels.
/// </summary>
public sealed record CharacterComparison(
    ArmoryProfile Mine,
    ArmoryProfile Theirs,
    IReadOnlyList<GearPair> Gear,
    IReadOnlyList<ValuePair> Stats,
    IReadOnlyList<ValuePair> Boards,
    IReadOnlyList<ValuePair> Skills)
{
    public bool SameClass => string.Equals(Mine.ClassName, Theirs.ClassName, StringComparison.OrdinalIgnoreCase);

    public static CharacterComparison Build(ArmoryProfile mine, ArmoryProfile theirs, GearStats stats)
    {
        int? Level(ArmoryItem? item) => item is not null && stats.Item(item.Id) is { } info ? info.ItemLevel + item.Enchant : null;

        // Slots in the order the site lists them (ours first, then any only they fill)
        var gear = mine.Gear.Select(g => g.Slot).Concat(theirs.Gear.Select(g => g.Slot)).Distinct()
            .Select(slot =>
            {
                var a = mine.Gear.FirstOrDefault(g => g.Slot == slot);
                var b = theirs.Gear.FirstOrDefault(g => g.Slot == slot);
                return new GearPair(slot, (a ?? b)!.SlotName, a, b, Level(a), Level(b));
            })
            .ToList();

        var statList = mine.Stats.Select(s => (s.Type, s.Name)).Concat(theirs.Stats.Select(s => (s.Type, s.Name))).DistinctBy(s => s.Type)
            .Select(s => new ValuePair(s.Name,
                mine.Stats.FirstOrDefault(m => m.Type == s.Type)?.Value ?? 0,
                theirs.Stats.FirstOrDefault(t => t.Type == s.Type)?.Value ?? 0))
            .ToList();

        var boards = mine.Daevanion.Select(b => (b.Id, b.Name)).Concat(theirs.Daevanion.Select(b => (b.Id, b.Name))).DistinctBy(b => b.Id)
            .Select(b =>
            {
                var a = mine.Daevanion.FirstOrDefault(x => x.Id == b.Id);
                var t = theirs.Daevanion.FirstOrDefault(x => x.Id == b.Id);
                return new ValuePair(b.Name, a?.Open ?? 0, t?.Open ?? 0, Math.Max(a?.Total ?? 0, t?.Total ?? 0));
            })
            .ToList();

        // Skill levels only mean something within one class; the biggest differences first
        var skills = !string.Equals(mine.ClassName, theirs.ClassName, StringComparison.OrdinalIgnoreCase)
            ? []
            : mine.Skills.Select(s => (s.Id, s.Name)).Concat(theirs.Skills.Select(s => (s.Id, s.Name))).DistinctBy(s => s.Id)
                .Select(s => new ValuePair(s.Name,
                    mine.Skills.FirstOrDefault(m => m.Id == s.Id)?.Level ?? 0,
                    theirs.Skills.FirstOrDefault(t => t.Id == s.Id)?.Level ?? 0))
                .OrderByDescending(s => Math.Abs(s.Difference)).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

        return new CharacterComparison(mine, theirs, gear, statList, boards, skills);
    }
}
