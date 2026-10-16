using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Avion2.Analysis;
using Avion2.Engine;
using Avion2.Protocol;

namespace Avion2.Overlay;

/// <summary>A saved dungeon run as shown in lists.</summary>
public sealed record RunRow(DungeonRun Run, GameData Data)
{
    public DungeonInfo? Dungeon => Run.DungeonSlug is { } slug ? BundledGameData.Dungeons.Find(slug) : null;

    public string Title => Run.Title(BundledGameData.Dungeons, Data);

    /// <summary>"Sealed Dungeon", "Party Dungeon", "Expedition", …</summary>
    public string Category => Dungeon?.Category ?? Content.Label(Run.Kind);

    /// <summary>Sidebar view: sealed, expedition, stronghold or other.</summary>
    public string Group => Run.Kind switch
    {
        ContentKind.SealedDungeon => "sealed",
        ContentKind.Expedition => "expedition",
        ContentKind.Stronghold => "stronghold",
        _ when Dungeon?.Category is "Party Dungeon" or "Raid" => "expedition",
        _ => "other",
    };

    /// <summary>Runs in the same place are compared (best time, attempts).</summary>
    public string PlaceKey => Run.DungeonSlug ?? $"map:{Run.FirstMap}";

    private DateTime Started => DateTimeOffset.FromUnixTimeMilliseconds(Run.StartMs).ToLocalTime().DateTime;

    /// <summary>List group: "Today", "Yesterday" or the date.</summary>
    public string Day => (DateTime.Today - Started.Date).Days switch
    {
        0 => "Today",
        1 => "Yesterday",
        _ => Started.ToString("ddd, dd MMM yyyy", CultureInfo.InvariantCulture),
    };

    public string Time => Started.ToString("HH:mm", CultureInfo.InvariantCulture);

    public string ShortDate => Started.ToString("dd MMM", CultureInfo.InvariantCulture);

    public string When => Started.ToString("dd MMM · HH:mm", CultureInfo.InvariantCulture);

    public string Duration => Clock(Run.DurationMs);

    public string Bosses => Run.Bosses.Count == 0 ? "–" : Run.Bosses.Count.ToString(CultureInfo.InvariantCulture);

    public string Deaths => Run.Deaths.Count == 0 ? "–" : Run.Deaths.Count.ToString(CultureInfo.InvariantCulture);

    /// <summary>Muted text after the title: the category, or the map id while the place is unknown.</summary>
    public string Extra => Run.DungeonSlug is null && Run.CustomName is null ? $"{Category} · map {Run.FirstMap}" : Category;

    public bool IsWithin(TimeSpan age) => DateTime.Now - Started <= age;

    /// <summary>A run that counts for best times: left normally with at least one boss down.</summary>
    public bool Counts => Run.Left && Run.Bosses.Count > 0;

    /// <summary>"4:03", or "1:02:03" past an hour.</summary>
    public static string Clock(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>The fastest run per place, among runs with the most bosses killed there (full clears beat partial ones).</summary>
    public static List<RunRow> BestPerPlace(IEnumerable<RunRow> runs) => runs
        .Where(r => r.Counts)
        .GroupBy(r => r.PlaceKey)
        .Select(g =>
        {
            int most = g.Max(r => r.Run.Bosses.Count);
            return g.Where(r => r.Run.Bosses.Count == most).MinBy(r => r.Run.DurationMs)!;
        })
        .OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
        .ToList();
}

/// <summary>A boss kill or a death in the detail pane's timeline.</summary>
public sealed record RunMoment(string At, string Text, string Detail, Brush Accent);

public sealed record RunMemberRow(string Name, string ClassName, ImageSource? Icon);

public sealed record RunLootRow(string Name, string Count);

/// <summary>The selected run in the detail pane (or the live one).</summary>
public sealed class RunSummary(RunRow row, IReadOnlyList<RunRow> all, bool live = false)
{
    private readonly List<RunRow> _same = all.Where(r => r.PlaceKey == row.PlaceKey && r.Run.Id != row.Run.Id).ToList();

    public RunRow Row => row;
    public string Title => row.Title;
    public string Category => row.Category;
    public string When => row.When;
    public bool Live => live;

    public string Where
    {
        get
        {
            var d = row.Dungeon;
            var parts = new List<string>();
            if (d?.Region is { Length: > 0 } region)
            {
                parts.Add(region);
            }

            if (d?.Level is { } level)
            {
                parts.Add($"Lv. {level}");
            }

            if (d?.ItemLevel is { } itemLevel)
            {
                parts.Add($"Item level {itemLevel}+");
            }

            return parts.Count > 0 ? string.Join(" · ", parts) : $"Map {row.Run.FirstMap}";
        }
    }

    public Visibility WhereVisibility => Visibility.Visible;

    public string Ending => live ? "In progress" : row.Run.Left ? "Left the instance" : "Capture stopped inside";

    public string PartyText => row.Run.Party.Count == 0 ? "Solo" : $"Party of {row.Run.Party.Count + 1}";

    public string Duration => row.Duration;

    public string BossCount
    {
        get
        {
            int listed = row.Dungeon?.Bosses.Count ?? 0;
            return listed >= row.Run.Bosses.Count && listed > 0 ? $"{row.Run.Bosses.Count} / {listed}" : row.Run.Bosses.Count.ToString(CultureInfo.InvariantCulture);
        }
    }

    public string DeathCount => row.Run.Deaths.Count.ToString(CultureInfo.InvariantCulture);

    /// <summary>The best other run here (to compare with); null when this is the first or only one.</summary>
    private RunRow? Best => RunRow.BestPerPlace(_same).FirstOrDefault();

    public string BestTime => Best is { } best ? best.Duration : "–";

    /// <summary>"−0:12 vs best" / "new best" / "first run here".</summary>
    public string BestNote
    {
        get
        {
            if (Best is not { } best)
            {
                return _same.Count == 0 ? "First run here" : "No full run to compare";
            }

            if (!row.Counts || live)
            {
                return $"{best.ShortDate}";
            }

            long delta = row.Run.DurationMs - best.Run.DurationMs;
            return delta < 0 ? $"New best by {RunRow.Clock(-delta)}" : $"+{RunRow.Clock(delta)} vs best";
        }
    }

    public string Attempts => (_same.Count + 1).ToString(CultureInfo.InvariantCulture);

    public string Fights => row.Run.Fights.ToString(CultureInfo.InvariantCulture);

    public string Damage => row.Run.Damage.ToString("N0", CultureInfo.InvariantCulture);

    public string Kinah => row.Run.Kinah is { } k ? k.ToString("+#,0;−#,0;0", CultureInfo.InvariantCulture) : "–";

    public IReadOnlyList<RunMoment> Moments
    {
        get
        {
            var gold = Brush("WarnBrush");
            var red = Brush("HpBarBrush");
            var list = row.Run.Bosses
                .Select(b => (b.AtMs, new RunMoment(RunRow.Clock(b.AtMs), b.Name,
                    b.FightMs is { } ms ? $"fight {RunRow.Clock(ms)}" : "", gold)))
                .Concat(row.Run.Deaths.Select(d => (d, new RunMoment(RunRow.Clock(d), "You died", "", red))))
                .OrderBy(m => m.Item1)
                .Select(m => m.Item2)
                .ToList();
            return list;
        }
    }

    public Visibility MomentsVisibility => row.Run.Bosses.Count + row.Run.Deaths.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Bosses of the table not killed in this run.</summary>
    public string Missing
    {
        get
        {
            if (row.Dungeon is not { Bosses.Count: > 0 } d)
            {
                return string.Empty;
            }

            var killed = row.Run.Bosses.Select(b => b.Npc is { } npc ? BundledGameData.EnglishNpcName(npc) ?? b.Name : b.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = d.Bosses.Where(b => !killed.Contains(b.Name) && (b.Npc is null || row.Run.Bosses.All(k => k.Npc != b.Npc)))
                .Select(b => b.Name).ToList();
            return missing.Count == 0 ? string.Empty : "Not killed: " + string.Join(", ", missing);
        }
    }

    public Visibility MissingVisibility => Missing.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public IReadOnlyList<RunMemberRow> Party => row.Run.Party
        .Select(m => new RunMemberRow(m.Name, m.Class == GameClass.Unknown ? "" : m.Class.ToString(), ClassIcons.For(m.Class)))
        .ToList();

    public Visibility PartyVisibility => row.Run.Party.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public IReadOnlyList<RunLootRow> Loot => row.Run.Loot
        .OrderByDescending(i => i.Count)
        .Select(i => new RunLootRow(ItemName(i.ItemId), $"× {i.Count.ToString("N0", CultureInfo.InvariantCulture)}"))
        .ToList();

    public Visibility LootVisibility => row.Run.Loot.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private static string ItemName(uint id) => id switch
    {
        ItemIds.EnhanceStone => "Enhancement Stone",
        ItemIds.AbyssPoints => "Abyss Points",
        _ => BundledGameData.Forge.Find(id)?.Name ?? $"Item {id}",
    };

    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
