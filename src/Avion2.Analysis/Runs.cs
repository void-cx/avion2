using System.Text.Json;
using System.Text.Json.Serialization;
using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>What kind of place a map is.</summary>
public enum ContentKind
{
    OpenWorld,
    SealedDungeon,
    Expedition,
    Stronghold,

    /// <summary>Any other instance (the game's content code is kept on the run).</summary>
    Instance,
}

public static class Content
{
    /// <summary>
    /// Map id ranges first (3100xx and 400001 sealed dungeons, 6000xx expeditions, 15xxxx strongholds (152xxx–154xxx seen)), then the
    /// content code of the map load (30 sealed, 79 expedition, 37/38 stronghold, 0 open world).
    /// </summary>
    public static ContentKind Classify(uint mapId, byte contentCode = 0)
    {
        if (mapId is >= 310_000 and < 320_000 or 400_001)
        {
            return ContentKind.SealedDungeon;
        }

        if (mapId is >= 600_000 and < 700_000)
        {
            return ContentKind.Expedition;
        }

        if (mapId is >= 150_000 and < 160_000)
        {
            return ContentKind.Stronghold;
        }

        if (mapId < 100_000)
        {
            return ContentKind.OpenWorld;
        }

        return contentCode switch
        {
            30 => ContentKind.SealedDungeon,
            79 => ContentKind.Expedition,
            37 or 38 => ContentKind.Stronghold,
            _ => ContentKind.Instance,
        };
    }

    public static string Label(ContentKind kind) => kind switch
    {
        ContentKind.OpenWorld => "Open world",
        ContentKind.SealedDungeon => "Sealed Dungeon",
        ContentKind.Expedition => "Expedition",
        ContentKind.Stronghold => "Stronghold",
        _ => "Instance",
    };
}

public sealed record DungeonBoss(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("npc")] uint? Npc);

/// <summary>One dungeon of the built-in table (assets/gamedata/dungeons.json).</summary>
public sealed record DungeonInfo(
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("level")] int? Level,
    [property: JsonPropertyName("itemLevel")] int? ItemLevel,
    [property: JsonPropertyName("party")] int? Party,
    [property: JsonPropertyName("region")] string? Region,
    [property: JsonPropertyName("bosses")] IReadOnlyList<DungeonBoss> Bosses,
    [property: JsonPropertyName("monsters")] IReadOnlyList<DungeonBoss>? Monsters = null);

/// <summary>The dungeon table: finds the dungeon a run was in by the bosses killed there.</summary>
public sealed class DungeonCatalog(IReadOnlyList<DungeonInfo> dungeons)
{
    public static readonly DungeonCatalog Empty = new([]);

    public IReadOnlyList<DungeonInfo> Dungeons { get; } = dungeons;

    private HashSet<uint>? _bossCodes;
    private HashSet<string>? _bossNames;

    public DungeonInfo? Find(string slug) => Dungeons.FirstOrDefault(d => d.Slug == slug);

    /// <summary>The NPC is a boss of some dungeon in the table (by code, else by English name).</summary>
    public bool IsBoss(uint? npc, string? englishName)
    {
        _bossCodes ??= Dungeons.SelectMany(d => d.Bosses).Where(b => b.Npc is not null).Select(b => b.Npc!.Value).ToHashSet();
        _bossNames ??= Dungeons.SelectMany(d => d.Bosses).Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return npc is { } code && _bossCodes.Contains(code) || englishName is { Length: > 0 } name && _bossNames.Contains(name);
    }

    /// <summary>
    /// The dungeon whose boss list has the most of these bosses (NPC code, else the English name). Bosses can
    /// appear in several dungeons (e.g. a sealed dungeon and its harder version): the earlier boss in a list wins ties.
    /// Without a boss, the other monsters killed decide (each kind counts a little; a lone match is not enough).
    /// </summary>
    public DungeonInfo? Match(IEnumerable<(uint? Npc, string Name)> bosses, IEnumerable<string>? monsters = null)
    {
        var killed = bosses.ToList();
        if (killed.Count == 0)
        {
            return monsters is null ? null : MatchMonsters(monsters.ToHashSet(StringComparer.OrdinalIgnoreCase));
        }

        DungeonInfo? best = null;
        double bestScore = 0;
        foreach (var dungeon in Dungeons)
        {
            double score = 0;
            foreach (var (npc, name) in killed)
            {
                int at = -1;
                for (int i = 0; i < dungeon.Bosses.Count; i++)
                {
                    var boss = dungeon.Bosses[i];
                    if (npc is { } code && boss.Npc == code ||
                        string.Equals(boss.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        at = i;
                        break;
                    }
                }

                if (at >= 0)
                {
                    score += 1 + 0.5 / (at + 1);
                }
            }

            if (score > bestScore)
            {
                (best, bestScore) = (dungeon, score);
            }
        }

        return best;
    }

    private Dictionary<string, int>? _spread;

    /// <summary>In how many dungeons a monster kind lives.</summary>
    private int Spread(string monster)
    {
        _spread ??= Dungeons.SelectMany(d => (d.Monsters ?? []).Select(m => m.Name).Distinct())
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        return Math.Max(1, _spread.GetValueOrDefault(monster));
    }

    private DungeonInfo? MatchMonsters(HashSet<string> killed)
    {
        DungeonInfo? best = null;
        double bestScore = 0;
        bool tie = false;
        foreach (var dungeon in Dungeons)
        {
            if (dungeon.Monsters is not { Count: > 0 } list)
            {
                continue;
            }

            // A monster kind counts by how telling it is: one found in a single dungeon decides alone, one shared by
            // three dungeons a third; together they must reach one telling kind
            double score = list.Where(m => killed.Contains(m.Name)).Sum(m => 1.0 / Spread(m.Name));
            score = score < 0.99 ? 0 : score + score / list.Count;
            if (score > bestScore)
            {
                (best, bestScore, tie) = (dungeon, score, false);
            }
            else if (score > 0 && score == bestScore)
            {
                tie = true;
            }
        }

        return tie ? null : best;
    }

    public static DungeonCatalog Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        using var doc = JsonDocument.Parse(json);
        var list = doc.RootElement.TryGetProperty("dungeons", out var items)
            ? items.Deserialize<List<DungeonInfo>>() ?? []
            : [];
        return new DungeonCatalog(list);
    }
}

/// <summary>A boss killed during a run.</summary>
public sealed record RunBoss(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("npc")] uint? Npc,
    [property: JsonPropertyName("atMs")] long AtMs,
    [property: JsonPropertyName("fightMs")] long? FightMs = null);

/// <summary>An item count that changed during a run (the bag only).</summary>
public sealed record RunItem(
    [property: JsonPropertyName("item")] uint ItemId,
    [property: JsonPropertyName("count")] long Count);

/// <summary>A party member seen fighting during a run.</summary>
public sealed record RunMember(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("class")] GameClass Class);

/// <summary>One dungeon / expedition / instance visit, from entering to leaving.</summary>
public sealed class DungeonRun
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("startMs")]
    public long StartMs { get; set; }

    [JsonPropertyName("endMs")]
    public long EndMs { get; set; }

    [JsonPropertyName("kind")]
    public ContentKind Kind { get; set; }

    /// <summary>The content code of the first map load (kept for kinds we do not know yet).</summary>
    [JsonPropertyName("code")]
    public byte ContentCode { get; set; }

    /// <summary>Every map of the run in order (a run can lead through several instance maps).</summary>
    [JsonPropertyName("maps")]
    public List<uint> Maps { get; set; } = [];

    /// <summary>The dungeon from the built-in table (matched by bosses or remembered for the map); null = unknown.</summary>
    [JsonPropertyName("dungeon")]
    public string? DungeonSlug { get; set; }

    /// <summary>A name the user gave this place (overrides the table's).</summary>
    [JsonPropertyName("name")]
    public string? CustomName { get; set; }

    [JsonPropertyName("character")]
    public string? Character { get; set; }

    [JsonPropertyName("characterClass")]
    public GameClass Class { get; set; }

    [JsonPropertyName("bosses")]
    public List<RunBoss> Bosses { get; set; } = [];

    /// <summary>When we died, ms after the start.</summary>
    [JsonPropertyName("deaths")]
    public List<long> Deaths { get; set; } = [];

    [JsonPropertyName("party")]
    public List<RunMember> Party { get; set; } = [];

    [JsonPropertyName("fights")]
    public int Fights { get; set; }

    /// <summary>Our damage over all fights of the run.</summary>
    [JsonPropertyName("damage")]
    public long Damage { get; set; }

    /// <summary>Kinah gained (negative = spent) while inside; null when the inventory was unknown.</summary>
    [JsonPropertyName("kinah")]
    public long? Kinah { get; set; }

    [JsonPropertyName("loot")]
    public List<RunItem> Loot { get; set; } = [];

    /// <summary>False when the run ended without leaving (capture stopped, game closed).</summary>
    [JsonPropertyName("left")]
    public bool Left { get; set; }

    [JsonIgnore]
    public long DurationMs => Math.Max(0, EndMs - StartMs);

    /// <summary>A deep copy (runs change on the capture thread while the UI shows them).</summary>
    public DungeonRun Copy() => JsonSerializer.Deserialize<DungeonRun>(JsonSerializer.Serialize(this))!;

    [JsonIgnore]
    public uint FirstMap => Maps.Count > 0 ? Maps[0] : 0;

    /// <summary>The run's place: the user's name, the table's dungeon, else the map's name or "Map 310042".</summary>
    /// <remarks>An unknown place with a boss kill is named after its last boss ("Stronghold · Wanted Criminal Gargos").</remarks>
    public string Title(DungeonCatalog catalog, GameData data) =>
        CustomName ?? (DungeonSlug is { } slug ? catalog.Find(slug)?.Name : null) ??
        (data.Maps.TryGetValue(FirstMap, out var map) ? map
            : Bosses.Count > 0 ? $"{Content.Label(Kind)} · {Bosses[^1].Name}"
            : $"{Content.Label(Kind)} {FirstMap}");
}

/// <summary>
/// Follows map loads into runs: an instance map starts one, further instance maps continue it, an open-world map ends
/// it. Boss kills (with the fight time), our deaths, party members, fights, damage and the bag's changes are collected
/// on the way. Not thread-safe.
/// </summary>
public sealed class RunTracker
{
    /// <summary>A gap without events after which an instance map starts a new run instead of continuing.</summary>
    private const long ContinueWithinMs = 20 * 60_000;

    private readonly EntityRegistry _registry;
    private readonly InventoryState _inventory;
    private Dictionary<uint, long>? _bagAtStart;
    private ulong? _kinahAtStart;
    private long _lastMs;

    /// <summary>The party as the game last sent it (ourselves included).</summary>
    private IReadOnlyList<PartyMember> _roster = [];

    /// <summary>English names of the monsters killed in the current run (to name runs without a boss).</summary>
    private readonly HashSet<string> _killed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Players seen in the current run: the roster can outlive a party, so only members seen inside are kept.</summary>
    private readonly HashSet<string> _seen = new();

    /// <summary>Players who dealt damage in the run's fights: the best evidence of who was in the party.</summary>
    private readonly HashSet<string> _fought = new();

    /// <summary>The fight that ended last: a boss kill closes its fight before the run hears of the kill.</summary>
    private (long Start, long Last)? _lastFight;

    /// <summary>When we loaded an open-world map; the run ends at the zone change that follows (the last fight closes there).</summary>
    private long? _leftAt;

    public RunTracker(EntityRegistry registry, CombatTracker tracker, InventoryState inventory)
    {
        _registry = registry;
        _inventory = inventory;
        tracker.EncounterEnded += OnEncounterEnded;
    }

    /// <summary>Dungeon table for matching; settable so the engine can load it after the session exists.</summary>
    public DungeonCatalog Catalog { get; set; } = DungeonCatalog.Empty;

    /// <summary>NPC code → English name, for matching against the (English) dungeon table in other languages.</summary>
    public Func<uint, string?>? EnglishName { get; set; }

    /// <summary>Map id → dungeon slug learned from earlier runs (a map whose bosses we never killed is still named).</summary>
    public Func<uint, string?>? KnownMap { get; set; }

    /// <summary>The run in progress, if we are inside an instance.</summary>
    public DungeonRun? Current { get; private set; }

    public event Action<DungeonRun>? RunStarted;

    /// <summary>A boss died, we died, or a fight ended in the current run.</summary>
    public event Action<DungeonRun>? RunChanged;

    public event Action<DungeonRun>? RunEnded;

    public void Apply(GameEvent e)
    {
        _lastMs = Math.Max(_lastMs, e.TimestampMs);
        if (_leftAt is { } left && Current is { } leaving && (e is ZoneChangeEvent || e.TimestampMs - left > 15_000))
        {
            _leftAt = null;
            End(leaving, left, left: true);
        }

        switch (e)
        {
            case MapLoadEvent map:
                OnMap(map);
                break;
            case DeathEvent { Kind: DeathKind.Combat } death when Current is { } run:
                OnDeath(run, death);
                break;
            case PlayerSpawnEvent player when Current is not null:
                _seen.Add(player.Name);
                break;
            case PartyRosterEvent roster:
                _roster = roster.Members;
                if (Current is { } inside && AddParty(inside))
                {
                    RunChanged?.Invoke(inside);
                }

                break;
        }
    }

    /// <summary>Ends the current run without leaving (capture stopped).</summary>
    public void Finish(long nowMs)
    {
        if (Current is { } run)
        {
            bool left = _leftAt is not null;
            End(run, _leftAt ?? Math.Max(nowMs, run.StartMs), left);
            _leftAt = null;
        }
    }

    private void OnMap(MapLoadEvent map)
    {
        var kind = Content.Classify(map.MapId, map.ContentCode);
        if (kind == ContentKind.OpenWorld)
        {
            if (Current is not null)
            {
                _leftAt ??= map.TimestampMs;
            }

            return;
        }

        _leftAt = null; // straight into another instance map: the run goes on
        if (Current is { } current)
        {
            // Expeditions are one map each: another expedition is a new run (the exit was missed, e.g. a capture gap)
            bool another = kind == ContentKind.Expedition && current.Kind == ContentKind.Expedition && !current.Maps.Contains(map.MapId);
            if (!another && map.TimestampMs - current.EndMs <= ContinueWithinMs)
            {
                if (current.Maps[^1] != map.MapId)
                {
                    current.Maps.Add(map.MapId);
                    current.DungeonSlug ??= KnownMap?.Invoke(map.MapId);
                    RunChanged?.Invoke(current);
                }

                current.EndMs = map.TimestampMs;
                return;
            }

            End(current, current.EndMs, left: false);
        }

        Current = new DungeonRun
        {
            Id = $"{map.TimestampMs}-{map.MapId}",
            StartMs = map.TimestampMs,
            EndMs = map.TimestampMs,
            Kind = kind,
            ContentCode = map.ContentCode,
            Maps = [map.MapId],
            DungeonSlug = KnownMap?.Invoke(map.MapId),
            Character = _registry.SelfName ?? _inventory.Character,
            Class = _registry.SelfClass,
        };
        AddParty(Current);
        _bagAtStart = _inventory.IsKnown ? Bag() : null;
        _kinahAtStart = _inventory.IsKnown ? _inventory.Kinah : null;
        RunStarted?.Invoke(Current);
    }

    private void OnDeath(DungeonRun run, DeathEvent death)
    {
        run.EndMs = Math.Max(run.EndMs, death.TimestampMs);
        if (death.EntityId == _registry.SelfId)
        {
            run.Deaths.Add(death.TimestampMs - run.StartMs);
            RunChanged?.Invoke(run);
            return;
        }

        if (_registry.Find(death.EntityId) is { Kind: EntityKind.Npc, NpcCode: { } killed } &&
            (EnglishName?.Invoke(killed) ?? _registry.Data.Npcs.GetValueOrDefault(killed)?.Name) is { } killedName)
        {
            _killed.Add(killedName);
        }

        if (_registry.Find(death.EntityId) is not { Kind: EntityKind.Npc } boss ||
            !boss.IsBoss && !Catalog.IsBoss(boss.NpcCode, boss.NpcCode is { } npc ? EnglishName?.Invoke(npc) : null))
        {
            return;
        }

        string name = boss.Name ?? (boss.NpcCode is { } code ? _registry.Data.Npcs.GetValueOrDefault(code)?.Name : null) ?? "Boss";
        long? fight = _lastFight is { } f && death.TimestampMs >= f.Start && death.TimestampMs <= f.Last + 5_000
            ? death.TimestampMs - f.Start
            : null;
        // Copies of one boss (phases, clones) die several times: the split is the last kill
        run.Bosses.RemoveAll(b => b.Npc is { } npc ? npc == boss.NpcCode : b.Name == name);
        run.Bosses.Add(new RunBoss(name, boss.NpcCode, death.TimestampMs - run.StartMs, fight));
        Rematch(run);
        RunChanged?.Invoke(run);
    }

    private void OnEncounterEnded(Encounter encounter)
    {
        if (Current is not { } run || encounter.LastMs < run.StartMs)
        {
            return;
        }

        run.EndMs = Math.Max(run.EndMs, encounter.LastMs);
        run.Fights++;
        _lastFight = (encounter.StartMs, encounter.LastMs);
        if (encounter.Final is not { } final)
        {
            return;
        }

        run.Damage += final.Self?.Total ?? 0;
        foreach (var row in final.Rows)
        {
            _fought.Add(row.Name);
        }

        // The boss fight's length goes with the boss killed in it
        for (int i = run.Bosses.Count - 1; i >= 0; i--)
        {
            var boss = run.Bosses[i];
            long at = run.StartMs + boss.AtMs;
            if (boss.FightMs is null && at >= encounter.StartMs && at <= encounter.LastMs + 5_000)
            {
                run.Bosses[i] = boss with { FightMs = Math.Max(0, at - encounter.StartMs) };
            }
        }

        RunChanged?.Invoke(run);
    }

    /// <summary>Everyone in the party besides us joins the run's party (members who leave stay listed).</summary>
    private bool AddParty(DungeonRun run)
    {
        string? self = _registry.SelfName ?? run.Character;
        bool added = false;
        foreach (var member in _roster)
        {
            if (member.Name != self && run.Party.All(m => m.Name != member.Name))
            {
                run.Party.Add(new RunMember(member.Name, member.Class));
                added = true;
            }
        }

        return added;
    }

    private void Rematch(DungeonRun run)
    {
        var bosses = run.Bosses.Select(b => (b.Npc, b.Npc is { } code ? EnglishName?.Invoke(code) ?? b.Name : b.Name));
        if (Catalog.Match(bosses, _killed) is { } dungeon)
        {
            run.DungeonSlug = dungeon.Slug;
        }
    }

    private void End(DungeonRun run, long endMs, bool left)
    {
        run.EndMs = Math.Max(run.EndMs, endMs);
        run.Left = left;
        run.Character ??= _registry.SelfName ?? _inventory.Character;
        if (run.Class == GameClass.Unknown)
        {
            run.Class = _registry.SelfClass;
        }

        if (run.Bosses.Count == 0 && run.DungeonSlug is null)
        {
            Rematch(run);
        }

        if (_kinahAtStart is { } before && _inventory.IsKnown)
        {
            run.Kinah = (long)_inventory.Kinah - (long)before;
        }

        if (_bagAtStart is { } start && _inventory.IsKnown)
        {
            foreach (var (item, count) in Bag())
            {
                long delta = count - start.GetValueOrDefault(item);
                if (delta > 0 && item is not ItemIds.Kinah and not ItemIds.KinahBound)
                {
                    run.Loot.Add(new RunItem(item, delta));
                }
            }
        }

        // Party members fought with us; without fights, being seen inside is enough
        run.Party.RemoveAll(m => run.Fights > 0 ? !_fought.Contains(m.Name) : !_seen.Contains(m.Name));
        Current = null;
        _killed.Clear();
        _seen.Clear();
        _fought.Clear();
        _bagAtStart = null;
        _kinahAtStart = null;
        RunEnded?.Invoke(run);
    }

    private Dictionary<uint, long> Bag() => _inventory.Items
        .Where(i => i.Container == InventoryItem.Cube)
        .GroupBy(i => i.ItemId)
        .ToDictionary(g => g.Key, g => g.Sum(i => (long)i.Count));
}
