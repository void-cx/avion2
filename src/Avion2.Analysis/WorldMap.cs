using System.Text.Json;
using System.Text.Json.Serialization;
using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>World position (game cm) → map image pixel: <c>px = Scale·x + X0</c>, <c>py = Scale·y + Y0</c>.</summary>
public sealed record MapTransform(
    [property: JsonPropertyName("scale")] double Scale,
    [property: JsonPropertyName("x0")] double X0,
    [property: JsonPropertyName("y0")] double Y0)
{
    public (double X, double Y) ToImage(float x, float y) => (Scale * x + X0, Scale * y + Y0);

    public (double X, double Y) ToImage(Vec3 p) => ToImage(p.X, p.Y);

    /// <summary>
    /// Least-squares fit of a shared scale and two offsets from (world, image) pairs. Null with fewer than three
    /// pairs or when a pair is off by more than <paramref name="maxErrorPx"/> (a wrong name match).
    /// </summary>
    public static MapTransform? Fit(IReadOnlyList<((double X, double Y) World, (double X, double Y) Image)> pairs, double maxErrorPx = 25)
    {
        if (pairs.Count < 3)
        {
            return null;
        }

        // Unknowns s, x0, y0; rows (wx, 1, 0) → ix and (wy, 0, 1) → iy
        double sww = 0, swx = 0, swy = 0, n = pairs.Count, six = 0, siy = 0, swi = 0;
        foreach (var (w, i) in pairs)
        {
            sww += w.X * w.X + w.Y * w.Y;
            swx += w.X;
            swy += w.Y;
            swi += w.X * i.X + w.Y * i.Y;
            six += i.X;
            siy += i.Y;
        }

        // Normal equations: [sww swx swy; swx n 0; swy 0 n] · [s x0 y0] = [swi six siy]
        double det = sww * n * n - swx * swx * n - swy * swy * n;
        if (Math.Abs(det) < 1e-9)
        {
            return null;
        }

        double s = (swi * n - swx * six - swy * siy) * n / det;
        var fit = new MapTransform(s, (six - s * swx) / n, (siy - s * swy) / n);
        return pairs.All(p => fit.Error(p.World, p.Image) <= maxErrorPx) ? fit : null;
    }

    public double Error((double X, double Y) world, (double X, double Y) image)
    {
        var (x, y) = (Scale * world.X + X0, Scale * world.Y + Y0);
        return Math.Sqrt((x - image.X) * (x - image.X) + (y - image.Y) * (y - image.Y));
    }
}

/// <param name="Icon">The game's marker icon for this category (a PNG in the built-in map data), or null.</param>
public sealed record MapCategory(
    [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("groupLabel")] string GroupLabel,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("icon")] string? Icon = null);

/// <summary>A point of interest in image pixels of the full-size map.</summary>
/// <param name="Subtype">Category key: teleport, hiddenCube, gatheringOre, creatureFeral, npc, namedBoss…</param>
/// <param name="Id">Set for collectibles (to check them off).</param>
/// <param name="SpawnKey">Monster spawn key; <see cref="WorldMap.Drops"/> lists what it drops.</param>
public sealed record MapMarker(
    [property: JsonPropertyName("n")] string Name,
    [property: JsonPropertyName("s")] string Subtype,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("lv")] int? Level = null,
    [property: JsonPropertyName("rank")] string? Rank = null,
    [property: JsonPropertyName("k")] string? SpawnKey = null,
    [property: JsonPropertyName("id")] string? Id = null);

public sealed record MapRegion(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("borders")] IReadOnlyList<IReadOnlyList<double[]>> Borders);

/// <summary>
/// One zone map: picture size, markers, regions and monster drops (assets/gamedata/world, datamined data as
/// published by aion2hub.com). <see cref="Transform"/> is known for maps calibrated at build time; others are
/// calibrated from the field boss table the game sends (<see cref="Calibrate"/>).
/// </summary>
public sealed class WorldMap
{
    [JsonPropertyName("key")] public string Key { get; init; } = string.Empty;
    [JsonPropertyName("mapId")] public uint MapId { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("faction")] public string? Faction { get; init; }

    /// <summary>Full-size picture width and height in pixels (marker coordinates use this).</summary>
    [JsonPropertyName("size")] public int Size { get; init; }

    [JsonPropertyName("transform")] public MapTransform? Transform { get; init; }
    [JsonPropertyName("categories")] public IReadOnlyList<MapCategory> Categories { get; init; } = [];
    [JsonPropertyName("markers")] public IReadOnlyList<MapMarker> Markers { get; init; } = [];
    [JsonPropertyName("regions")] public IReadOnlyList<MapRegion> Regions { get; init; } = [];
    [JsonPropertyName("drops")] public IReadOnlyDictionary<string, IReadOnlyList<string>> Drops { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    public static WorldMap Parse(string json) => JsonSerializer.Deserialize<WorldMap>(json) ?? throw new JsonException("empty map");

    /// <summary>
    /// A region or place name as people write it: "EllunRiverMarsh" → "Ellun River Marsh", and the data's
    /// "Forestof Exiles" / "Gardenofthe Illusion God" → "Forest of Exiles" / "Garden of the Illusion God".
    /// </summary>
    public static string Readable(string name)
    {
        if (!name.Contains(' '))
        {
            name = string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]) ? " " + c : c.ToString()));
        }

        // "of" / "the" / "and" glued to the end of a word
        return System.Text.RegularExpressions.Regex.Replace(name, @"(?<=[a-z]{3})(of|the|and)(?=\s|[A-Z]|the\b)", " $1");
    }

    /// <summary>Group of a marker subtype (location, collection, gathering, monster, npc, creature).</summary>
    public string GroupOf(string subtype) => Categories.FirstOrDefault(c => c.Key == subtype)?.Group ?? "other";

    public string? IconOf(string subtype) => Categories.FirstOrDefault(c => c.Key == subtype)?.Icon;

    public string LabelOf(string subtype) => Categories.FirstOrDefault(c => c.Key == subtype)?.Label ?? subtype;

    /// <summary>The region a picture point lies in (point-in-polygon), or null.</summary>
    public string? RegionAt(double x, double y) =>
        Regions.FirstOrDefault(r => r.Borders.Any(ring => Inside(ring, x, y)))?.Name;

    /// <summary>
    /// A transform from the field bosses the game reported for this map (live ones carry positions), matched by
    /// name to this map's monster markers that occur once. Null when too few match.
    /// </summary>
    public MapTransform? Calibrate(IEnumerable<FieldBossEntry> entries, Func<uint, string> fieldBossName)
    {
        var unique = Markers
            .Where(m => GroupOf(m.Subtype) is "monster" or "creature")
            .GroupBy(m => m.Name)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single());

        var pairs = entries
            .Where(e => e is { X: { } x, Y: { } y } && Math.Abs(x) + Math.Abs(y) > 100)
            .Select(e => (Entry: e, Name: fieldBossName(e.SpawnCode)))
            .Where(x => unique.ContainsKey(x.Name))
            .Select(x => (World: ((double)x.Entry.X!.Value, (double)x.Entry.Y!.Value), Image: (unique[x.Name].X, unique[x.Name].Y)))
            .ToList();
        return MapTransform.Fit(pairs);
    }

    private static bool Inside(IReadOnlyList<double[]> ring, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            double xi = ring[i][0], yi = ring[i][1], xj = ring[j][0], yj = ring[j][1];
            if (yi > y != yj > y && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}

public enum LiveKind
{
    Self,
    Player,
    Monster,
    Boss,
}

/// <summary>An entity's latest known position.</summary>
/// <param name="AgeMs">Since the last position fix.</param>
/// <param name="Facing">Degrees, 0 = +X turning toward +Y (map: right, then down); null when unknown.</param>
/// <param name="FacingAgeMs">Since the facing was seen.</param>
public sealed record LiveEntity(uint Id, string Name, LiveKind Kind, GameClass Class, Vec3 Position, long AgeMs, bool Dead,
    float? Facing = null, long FacingAgeMs = 0);

/// <summary>
/// Latest position per entity for the live map (moves, spawns, buff positions). The game does not echo our own
/// movement, so our position updates only on teleports, buffs and skills. Not thread-safe.
/// </summary>
public sealed class LivePositions
{
    private readonly Dictionary<uint, (Vec3 Position, long Ms)> _fixes = new();
    private readonly Dictionary<uint, (float Degrees, long Ms)> _facing = new();

    /// <summary>Two fixes at least this far apart (cm) and this close in time give a direction of travel.</summary>
    private const float MinTravelCm = 300;
    private const long MaxTravelGapMs = 8_000;
    private readonly HashSet<uint> _dead = new();
    private long _nowMs;

    public uint? MapId { get; private set; }

    public void Apply(GameEvent e)
    {
        _nowMs = Math.Max(_nowMs, e.TimestampMs);
        switch (e)
        {
            case MapLoadEvent map:
                MapId = map.MapId;
                break;
            case FieldBossTableEvent table:
                // Sent about once a second for the map we are on (also when the recording began after the map load)
                MapId = table.MapId;
                break;
            case ZoneChangeEvent:
                _fixes.Clear();
                _facing.Clear();
                _dead.Clear();
                break;
            case SkillCastEvent { Facing: { } facing } cast:
                // Exact: the direction the actor faced when using the skill
                _facing[cast.ActorId] = (facing, cast.TimestampMs);
                break;
            case PositionEvent { Position.IsPlausible: true } p:
                Fix(p.EntityId, p.Position, p.TimestampMs);
                break;
            case NpcSpawnEvent { Position: { IsPlausible: true } at } spawn:
                Fix(spawn.EntityId, at, spawn.TimestampMs);
                break;
            case PlayerSpawnEvent { Position: { IsPlausible: true } at } spawn:
                Fix(spawn.EntityId, at, spawn.TimestampMs);
                break;
            case DeathEvent death:
                _dead.Add(death.EntityId);
                break;
        }
    }

    private void Fix(uint id, Vec3 position, long ms)
    {
        // Direction of travel from the previous fix, unless a skill told us the facing since then
        if (_fixes.TryGetValue(id, out var before) && ms - before.Ms is > 0 and <= MaxTravelGapMs &&
            before.Position.DistanceTo(position) >= MinTravelCm &&
            (!_facing.TryGetValue(id, out var known) || known.Ms <= before.Ms))
        {
            float degrees = MathF.Atan2(position.Y - before.Position.Y, position.X - before.Position.X) * 180 / MathF.PI;
            _facing[id] = ((degrees + 360) % 360, ms);
        }

        _fixes[id] = (position, ms);
        _dead.Remove(id);
    }

    /// <summary>Entities with a fix younger than <paramref name="maxAgeMs"/> (players and our own position always).</summary>
    public IReadOnlyList<LiveEntity> Snapshot(EntityRegistry registry, long maxAgeMs = 60_000)
    {
        var list = new List<LiveEntity>();
        foreach (var (id, (position, ms)) in _fixes)
        {
            var info = registry.Find(id);
            bool self = id == registry.SelfId;
            var kind = self ? LiveKind.Self
                : info?.Kind == EntityKind.Player ? LiveKind.Player
                : info?.IsBoss == true ? LiveKind.Boss
                : info?.Kind is EntityKind.Npc ? LiveKind.Monster
                : (LiveKind?)null;
            long age = _nowMs - ms;
            if (kind is null || kind is LiveKind.Monster or LiveKind.Boss && (age > maxAgeMs || _dead.Contains(id)))
            {
                continue;
            }

            bool faced = _facing.TryGetValue(id, out var facing);
            list.Add(new LiveEntity(id, registry.DisplayName(id), kind.Value, info?.Class ?? GameClass.Unknown, position, age, _dead.Contains(id),
                faced ? facing.Degrees : null, faced ? _nowMs - facing.Ms : 0));
        }

        return list;
    }
}
