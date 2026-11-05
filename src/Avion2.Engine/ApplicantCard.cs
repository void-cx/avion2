using Avion2.Analysis;

namespace Avion2.Engine;

/// <summary>Our group-finder room's members as an applicant is measured against them (us included, open slots not).</summary>
/// <param name="ItemLevels">Each seated member's item level that is known, lowest first.</param>
public sealed record RoomStanding(int Members, uint? AverageItemLevel, uint? AverageCombatPower, IReadOnlyList<uint> ItemLevels)
{
    public uint? MinItemLevel => ItemLevels.Count > 0 ? ItemLevels[0] : null;
    public uint? MaxItemLevel => ItemLevels.Count > 0 ? ItemLevels[^1] : null;
}

/// <summary>An applicant's gear as the official site shows it, summed up for the applicant card.</summary>
/// <param name="Items">Weapons, armor and accessories, in slot order.</param>
/// <param name="Grades">How many items of each grade, the best grade first.</param>
/// <param name="AverageEnchant">The enchant level over those items; null when there are none.</param>
public sealed record GearSummary(
    IReadOnlyList<ArmoryItem> Items,
    IReadOnlyList<(string Grade, int Count)> Grades,
    double? AverageEnchant,
    int DaevanionOpen,
    int DaevanionTotal,
    int Titles);

/// <summary>Runs we did with someone before: how many, and the latest.</summary>
public sealed record RunsTogether(int Count, DungeonRun? Last);

/// <summary>
/// What the applicant card shows when someone asks to join the group-finder room we lead: how they compare to the
/// room, their gear in short, and whether we ran with them before.
/// </summary>
public static class ApplicantCard
{
    /// <summary>The best grade first; the official site calls Heroic "Legend".</summary>
    private static readonly string[] GradeOrder = ["Epic", "Unique", "Legend", "Heroic", "Special", "Rare", "Common"];

    /// <summary>The room's seated members (us included) to measure applicants against.</summary>
    public static RoomStanding Room(FinderRoomView room)
    {
        var seated = room.Slots.Where(s => !s.IsOpen).ToList();
        var levels = seated.Select(s => s.ItemLevel).Where(v => v > 0).Order().ToList();
        var powers = seated.Select(s => s.CombatPower).Where(v => v > 0).ToList();
        return new RoomStanding(
            seated.Count,
            levels.Count > 0 ? (uint)Math.Round(levels.Average(v => (double)v)) : null,
            powers.Count > 0 ? (uint)Math.Round(powers.Average(v => (double)v)) : null,
            levels);
    }

    /// <summary>How far <paramref name="value"/> is above (+) or below (−) the average, in whole percent; null when either is unknown.</summary>
    public static int? PercentVs(uint value, uint? average) =>
        value > 0 && average is > 0 and var avg ? (int)Math.Round(100.0 * ((double)value - avg) / avg) : null;

    /// <summary>Where <paramref name="value"/> sits between <paramref name="min"/> and <paramref name="max"/>, 0 to 1 (0.5 when they are equal).</summary>
    public static double Position(uint value, uint min, uint max) =>
        max <= min ? 0.5 : Math.Clamp(((double)value - min) / (max - min), 0, 1);

    /// <summary>Weapons (slots 1–2), armor (3–8, 19) and accessories (10–17, 22): what makes item level.</summary>
    public static bool IsGear(ArmoryItem item) => item.Slot is >= 1 and <= 8 or >= 10 and <= 17 or 19 or 22;

    public static GearSummary Gear(ArmoryProfile profile)
    {
        var items = profile.Gear.Where(IsGear).OrderBy(g => g.Slot).ToList();
        var grades = items
            .GroupBy(g => g.Grade)
            .Select(g => (Grade: g.Key, Count: g.Count()))
            .OrderBy(g => Array.IndexOf(GradeOrder, g.Grade) is var i and >= 0 ? i : GradeOrder.Length)
            .ThenBy(g => g.Grade, StringComparer.Ordinal)
            .ToList();
        return new GearSummary(
            items,
            grades,
            items.Count > 0 ? items.Average(g => g.Enchant) : null,
            profile.Daevanion.Sum(b => b.Open),
            profile.Daevanion.Sum(b => b.Total),
            profile.TitlesOwned);
    }

    /// <summary>The grade as the game names it ("Legend" on the site is the game's Heroic).</summary>
    public static string GradeName(string grade) => grade == "Legend" ? "Heroic" : grade;

    /// <summary>"2 Unique · 7 Epic · 2 Heroic".</summary>
    public static string GradeLine(GearSummary gear) =>
        string.Join(" · ", gear.Grades.Select(g => $"{g.Count} {GradeName(g.Grade)}"));

    /// <summary>Our runs with a party member of that name (any of our characters), the latest last.</summary>
    public static RunsTogether SeenWith(IEnumerable<DungeonRun> runs, string name)
    {
        var together = runs
            .Where(r => r.Party.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return new RunsTogether(together.Count, together.MaxBy(r => r.StartMs));
    }

    /// <summary>"just now", "5 minutes ago", "3 hours ago", "2 days ago".</summary>
    public static string Ago(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalMinutes: < 2 } => "a minute ago",
        { TotalHours: < 1 } => $"{(int)span.TotalMinutes} minutes ago",
        { TotalHours: < 2 } => "an hour ago",
        { TotalDays: < 1 } => $"{(int)span.TotalHours} hours ago",
        { TotalDays: < 2 } => "yesterday",
        _ => $"{(int)span.TotalDays} days ago",
    };
}
