using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>An expedition's guaranteed reward: the points it takes, how often it can be claimed, the items to pick from.</summary>
/// <param name="After">Points it takes (one point per cube share opened).</param>
/// <param name="Claims">How often it can be claimed; 0 = the table sets no number.</param>
public sealed record CubeCeilingRule(
    [property: JsonPropertyName("after")] int After,
    [property: JsonPropertyName("claims")] int Claims,
    [property: JsonPropertyName("items")] IReadOnlyList<uint> Items);

/// <summary>The reward cube of one expedition and mode (assets/gamedata/cube_rewards.json).</summary>
/// <param name="Dungeon">The game's key for the expedition ("KraoCave").</param>
/// <param name="Slug">The dungeon in the built-in dungeon table, if it is there.</param>
/// <param name="Odyle">Odyle one share costs.</param>
/// <param name="Final">The final boss (NPC code).</param>
/// <param name="Npcs">The final and mid bosses (NPC codes; each mode has its own).</param>
public sealed record CubeInfo(
    [property: JsonPropertyName("dungeon")] string Dungeon,
    [property: JsonPropertyName("slug")] string? Slug,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("modeName")] string ModeName,
    [property: JsonPropertyName("odyle")] int Odyle,
    [property: JsonPropertyName("final")] uint? Final,
    [property: JsonPropertyName("npcs")] IReadOnlyList<uint> Npcs,
    [property: JsonPropertyName("ceiling")] CubeCeilingRule? Ceiling)
{
    /// <summary>"KraoCave/explore": the cube's key in saved data.</summary>
    [JsonIgnore]
    public string Key => $"{Dungeon}/{Mode}";

    /// <summary>"Krao Cave · Exploration".</summary>
    [JsonIgnore]
    public string Title => $"{Name} · {ModeName}";
}

/// <summary>The reward cubes of all expeditions, found by a boss's NPC code or by key.</summary>
public sealed class CubeTable(IReadOnlyList<CubeInfo> cubes)
{
    public static readonly CubeTable Empty = new([]);

    public IReadOnlyList<CubeInfo> Cubes { get; } = cubes;

    private readonly Dictionary<uint, CubeInfo> _byNpc = cubes
        .SelectMany(c => c.Npcs.Select(npc => (npc, c)))
        .GroupBy(p => p.npc)
        .ToDictionary(g => g.Key, g => g.First().c);

    private readonly Dictionary<string, CubeInfo> _byKey = cubes
        .GroupBy(c => c.Key)
        .ToDictionary(g => g.Key, g => g.First());

    /// <summary>The cube a boss belongs to, or null.</summary>
    public CubeInfo? ByNpc(uint npc) => _byNpc.GetValueOrDefault(npc);

    public CubeInfo? ByKey(string? key) => key is not null ? _byKey.GetValueOrDefault(key) : null;

    public static CubeTable Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("cubes", out var list)
            ? new CubeTable(list.Deserialize<List<CubeInfo>>() ?? [])
            : Empty;
    }
}

/// <summary>One expedition's guaranteed-reward counter as last sent by the game.</summary>
public sealed record CeilingEntry(
    [property: JsonPropertyName("dungeon")] uint DungeonId,
    [property: JsonPropertyName("points")] uint Points,
    [property: JsonPropertyName("claims")] uint Claims);

/// <summary>A character's guaranteed-reward counters, by expedition (map id).</summary>
public sealed class CeilingState
{
    [JsonPropertyName("character")]
    public string? Character { get; set; }

    [JsonPropertyName("dungeons")]
    public Dictionary<uint, CeilingEntry> Dungeons { get; set; } = new();

    /// <summary>When the game last sent them (Unix ms).</summary>
    [JsonPropertyName("updatedMs")]
    public long UpdatedMs { get; set; }

    public CeilingState Copy() => new() { Character = Character, Dungeons = new(Dungeons), UpdatedMs = UpdatedMs };
}

/// <summary>
/// The guaranteed-reward counters of each of our characters, by name, and which reward cube each expedition map has
/// (learned from the bosses killed there). Saved between sessions.
/// </summary>
public sealed class CeilingBook
{
    [JsonInclude]
    [JsonPropertyName("characters")]
    private Dictionary<string, CeilingState> _byName = new();

    /// <summary>Expedition map id → cube key ("KraoCave/explore").</summary>
    [JsonInclude]
    [JsonPropertyName("maps")]
    private Dictionary<uint, string> _maps = new();

    public CeilingState? For(string? name) => name is not null && _byName.TryGetValue(name, out var state) ? state : null;

    /// <summary>Takes a character's counters (a copy); false when they belong to no one yet.</summary>
    public bool Take(CeilingState state)
    {
        if (state.Character is not { } name)
        {
            return false;
        }

        _byName[name] = state.Copy();
        return true;
    }

    /// <summary>The cube key learned for an expedition map, or null.</summary>
    public string? CubeOf(uint dungeonId) => _maps.GetValueOrDefault(dungeonId);

    /// <summary>Remembers the cube of a map; true when that was new.</summary>
    public bool Learn(uint dungeonId, string cubeKey)
    {
        if (_maps.GetValueOrDefault(dungeonId) == cubeKey)
        {
            return false;
        }

        _maps[dungeonId] = cubeKey;
        return true;
    }

    /// <summary>
    /// Learns the cubes of the expedition maps of saved runs from their bosses (each mode has its own NPC codes).
    /// Returns how many maps were new.
    /// </summary>
    public int Learn(IEnumerable<DungeonRun> runs, CubeTable cubes)
    {
        int learned = 0;
        foreach (var run in runs)
        {
            var maps = run.Maps.Where(m => Content.Classify(m) == ContentKind.Expedition).Distinct().ToList();
            if (maps.Count != 1)
            {
                continue;
            }

            var cube = run.Bosses.Select(b => b.Npc is { } npc ? cubes.ByNpc(npc) : null).FirstOrDefault(c => c is not null);
            if (cube is not null && Learn(maps[0], cube.Key))
            {
                learned++;
            }
        }

        return learned;
    }

    /// <summary>A character's counters with their cubes (the expeditions in the game's order).</summary>
    public IReadOnlyList<CeilingProgress> Progress(string? name, CubeTable cubes) =>
        For(name)?.Dungeons.Values
            .OrderBy(e => e.DungeonId)
            .Select(e => new CeilingProgress(e.DungeonId, cubes.ByKey(CubeOf(e.DungeonId)), e.Points, e.Claims))
            .ToList() ?? [];
}

/// <summary>How far one expedition's guaranteed reward is.</summary>
/// <param name="Cube">The expedition's reward cube; null while AVION2 has not seen its bosses.</param>
public sealed record CeilingProgress(uint DungeonId, CubeInfo? Cube, uint Points, uint Claims)
{
    /// <summary>Points the guaranteed reward takes; null when unknown.</summary>
    public int? Needed => Cube?.Ceiling is { After: > 0 } rule ? rule.After : null;

    /// <summary>It was claimed as often as the table allows.</summary>
    public bool AllClaimed => Cube?.Ceiling is { Claims: > 0 } rule && Claims >= rule.Claims;

    /// <summary>Enough points to claim it now.</summary>
    public bool Ready => Needed is { } needed && Points >= needed && !AllClaimed;

    /// <summary>Points still missing (0 when ready); null when unknown or all claimed.</summary>
    public int? ToGo => Needed is { } needed && !AllClaimed ? Math.Max(0, needed - (int)Points) : null;

    /// <summary>"Krao Cave · Exploration", else "Expedition 600021".</summary>
    public string Title => Cube?.Title ?? $"Expedition {DungeonId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>"6 / 21", "2 points" when the goal is unknown.</summary>
    public string Value => AllClaimed ? "Claimed"
        : Needed is { } needed ? $"{Points} / {needed}"
        : Points == 1 ? "1 point" : $"{Points} points";

    /// <summary>"Ready to claim", "15 to go", "Claimed 1 of 1", or what is not known.</summary>
    public string Status => AllClaimed ? $"Claimed {Claims} of {Cube!.Ceiling!.Claims}"
        : Ready ? "Ready to claim"
        : ToGo is { } left ? $"{left} to go"
        : Cube is null ? "Goal unknown until AVION2 sees a clear here"
        : "No guaranteed reward here";
}

/// <summary>What a run's reward cube gave (<c>15 57</c>): the shares opened and the items, Kinah included.</summary>
public sealed record RunCube(
    [property: JsonPropertyName("shares")] int Shares,
    [property: JsonPropertyName("items")] List<RunItem> Items)
{
    /// <summary>Adds a cube opening to what the run had (a second opening in one run adds up).</summary>
    public static RunCube Merge(RunCube? before, CubeRewardEvent reward)
    {
        var items = (before?.Items ?? []).ToDictionary(i => i.ItemId, i => i.Count);
        foreach (var item in reward.Items)
        {
            items[item.ItemId] = items.GetValueOrDefault(item.ItemId) + item.Count;
        }

        return new RunCube((before?.Shares ?? 0) + reward.Shares, items.Select(p => new RunItem(p.Key, p.Value)).ToList());
    }
}

/// <summary>
/// Follows our guaranteed-reward counters through a session. The game sends them at every world entry, just before
/// it names our character, so they wait for that name (after a character switch the old one is still around); a
/// claim updates the character in game. The reward cube's mid bosses tell which cube the current map has. Not
/// thread-safe.
/// </summary>
public sealed class RewardTracker
{
    private IReadOnlyList<CubeCeiling>? _pending;
    private long _pendingMs;
    private uint? _map;

    /// <summary>The reward cube table; settable so the engine can load it after the session exists.</summary>
    public CubeTable Cubes { get; set; } = CubeTable.Empty;

    /// <summary>The counters of the character in game (empty until the game sent them and named us).</summary>
    public CeilingState Ceilings { get; private set; } = new();

    /// <summary>Expedition map → cube key learned this session.</summary>
    public Dictionary<uint, string> LearnedMaps { get; } = new();

    /// <summary>The counters changed (or a map's cube was learned).</summary>
    public event Action? Changed;

    public void Apply(GameEvent e)
    {
        switch (e)
        {
            case CubeCeilingInfoEvent info:
                _pending = info.Dungeons;
                _pendingMs = info.TimestampMs;
                break;
            case SelfIdentityEvent { Name.Length: > 0 } self when _pending is { } list:
                Ceilings = new CeilingState
                {
                    Character = self.Name,
                    Dungeons = list.ToDictionary(c => c.DungeonId, c => new CeilingEntry(c.DungeonId, c.Points, c.Claims)),
                    UpdatedMs = _pendingMs,
                };
                _pending = null;
                Changed?.Invoke();
                break;
            case CubeCeilingRewardEvent claim when Ceilings.Character is not null:
                Ceilings.Dungeons[claim.DungeonId] = new CeilingEntry(claim.DungeonId, claim.PointsLeft, claim.Claims);
                Ceilings.UpdatedMs = claim.TimestampMs;
                Changed?.Invoke();
                break;
            case MapLoadEvent map:
                _map = map.MapId;
                break;
            case CubeRewardStartEvent start:
                Learn(start.NpcCodes);
                break;
            case MiddleBossRewardEvent boss:
                Learn([boss.NpcCode]);
                break;
        }
    }

    private void Learn(IEnumerable<uint> npcs)
    {
        if (_map is not { } map || Content.Classify(map) != ContentKind.Expedition ||
            npcs.Select(Cubes.ByNpc).FirstOrDefault(c => c is not null) is not { } cube ||
            LearnedMaps.GetValueOrDefault(map) == cube.Key)
        {
            return;
        }

        LearnedMaps[map] = cube.Key;
        Changed?.Invoke();
    }
}
