using System.Text.Json.Serialization;
using Avion2.Protocol;

namespace Avion2.Analysis;

public enum CubeState
{
    /// <summary>Seen in game since it was last opened: it is there.</summary>
    Available,

    /// <summary>We opened it (and have not seen it again since).</summary>
    Opened,

    /// <summary>Someone else opened it (and we have not seen it again since).</summary>
    Taken,
}

/// <summary>A hidden cube spot the game showed us, with what last happened there.</summary>
/// <param name="Id">Stable id of the spot (map and position).</param>
/// <param name="SeenMs">Last time the game showed the cube there (Unix ms).</param>
/// <param name="OpenedMs">Last time we opened it.</param>
/// <param name="TakenMs">Last time someone else opened it (<paramref name="TakenBy"/>).</param>
/// <param name="Visits">Separate visits (10+ minutes apart) on which the game showed a cube at this spot.</param>
public sealed record CubeSighting(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("map")] uint MapId,
    [property: JsonPropertyName("x")] float X,
    [property: JsonPropertyName("y")] float Y,
    [property: JsonPropertyName("z")] float Z,
    [property: JsonPropertyName("code")] uint Code,
    [property: JsonPropertyName("seen")] long SeenMs,
    [property: JsonPropertyName("openedMs")] long? OpenedMs = null,
    [property: JsonPropertyName("takenMs")] long? TakenMs = null,
    [property: JsonPropertyName("takenBy")] string? TakenBy = null,
    [property: JsonPropertyName("visits")] int Visits = 1)
{
    /// <summary>A sighting this recent is trusted as the cube's position (it may respawn elsewhere later).</summary>
    public const long FreshMs = 20 * 60_000;

    /// <summary>
    /// Whether to lead straight to this spot: the cube is there and was seen recently, or it was seen here on
    /// two separate visits (a fixed spawn point). Otherwise it is only a hint where to search.
    /// </summary>
    public bool IsReliable(long nowMs) => State == CubeState.Available && (nowMs - SeenMs <= FreshMs || Visits >= 2);

    [JsonIgnore]
    public Vec3 Position => new(X, Y, Z);

    /// <summary>What happened last: seen (there), opened by us, or taken by someone else.</summary>
    [JsonIgnore]
    public CubeState State =>
        OpenedMs is { } opened && opened >= SeenMs && opened >= (TakenMs ?? 0) ? CubeState.Opened
        : TakenMs is { } taken && taken >= SeenMs ? CubeState.Taken
        : CubeState.Available;

    public static string SpotId(uint mapId, Vec3 at) => $"{mapId}:{Math.Round(at.X / 100)}:{Math.Round(at.Y / 100)}";
}

/// <summary>
/// Hidden cubes from the stream: cube objects in range (<c>34 36</c>) and cubes opened with a Hidden Cube key
/// (<c>3A 36</c>) — by us or by someone else. Opening one teaches the tracker its object code, so cubes of that kind
/// are recognised on sight. Not thread-safe.
/// </summary>
public sealed class CubeTracker
{
    /// <summary>Key: Hidden Cube — opening a cube uses one up.</summary>
    public const uint KeyItem = 630_900_003;

    private readonly Dictionary<uint, WorldObjectEvent> _objects = new();

    /// <summary>Object codes known to be hidden cubes (1200039 in all 5 openings seen; more are learned).</summary>
    public HashSet<uint> CubeCodes { get; } = [1_200_039];

    /// <summary>A cube came into range, or was opened by us or someone else (raised from <see cref="Apply"/>).</summary>
    public event Action<CubeSighting>? Changed;

    /// <param name="nameOf">Display name of an entity (who took a cube).</param>
    public void Apply(GameEvent e, uint? selfId, uint? mapId, Func<uint, string>? nameOf = null)
    {
        switch (e)
        {
            case ZoneChangeEvent:
                _objects.Clear();
                break;
            case WorldObjectEvent obj:
                _objects[obj.EntityId] = obj;
                if (CubeCodes.Contains(obj.Code) && mapId is { } seenOn)
                {
                    Changed?.Invoke(Spot(seenOn, obj, obj.TimestampMs));
                }

                break;
            case InteractionDoneEvent { ItemId: KeyItem } done:
                if (_objects.TryGetValue(done.ObjectId, out var cube) && mapId is { } openedOn)
                {
                    CubeCodes.Add(cube.Code);
                    bool ours = selfId is null || done.ActorId == selfId;
                    var spot = Spot(openedOn, cube, cube.TimestampMs);
                    Changed?.Invoke(ours
                        ? spot with { OpenedMs = done.TimestampMs }
                        : spot with { TakenMs = done.TimestampMs, TakenBy = nameOf?.Invoke(done.ActorId) });
                }

                break;
        }
    }

    private static CubeSighting Spot(uint mapId, WorldObjectEvent obj, long seenMs) =>
        new(CubeSighting.SpotId(mapId, obj.Position), mapId, obj.Position.X, obj.Position.Y, obj.Position.Z, obj.Code, seenMs);
}

/// <summary>Every cube spot seen, opened or taken, kept between sessions (one entry per spot).</summary>
public sealed class CubeLog
{
    /// <summary>Sightings closer than this (cm) are the same spot.</summary>
    public const float SameCubeCm = 300;

    /// <summary>Sightings of a spot this far apart count as separate visits.</summary>
    public const long VisitGapMs = 10 * 60_000;

    /// <summary>Search radius around a spot seen a while ago (m): unknown whether cubes come back at the same place.</summary>
    public const double LastSeenMeters = 60;

    /// <summary>
    /// How far real cubes were from the nearest aion2hub cube marker (m): 12 of them, 33–157 m, in no common
    /// direction. Hub markers are therefore search areas of this radius, not positions.
    /// </summary>
    public const double SearchMeters = 160;

    [JsonPropertyName("cubes")]
    public List<CubeSighting> Cubes { get; set; } = [];

    [JsonPropertyName("codes")]
    public List<uint> Codes { get; set; } = [];

    /// <summary>Adds a spot or updates what happened there last. Returns the stored entry.</summary>
    public CubeSighting Merge(CubeSighting sighting)
    {
        int index = Cubes.FindIndex(c => c.MapId == sighting.MapId && c.Position.DistanceTo(sighting.Position) < SameCubeCm);
        if (index < 0)
        {
            Cubes.Add(sighting);
            return sighting;
        }

        var old = Cubes[index];
        bool newVisit = sighting.OpenedMs is null && sighting.TakenMs is null && sighting.SeenMs - old.SeenMs >= VisitGapMs;
        var merged = old with
        {
            Visits = old.Visits + (newVisit ? 1 : 0),
            Z = sighting.Z,
            SeenMs = Math.Max(old.SeenMs, sighting.SeenMs),
            OpenedMs = Max(old.OpenedMs, sighting.OpenedMs),
            TakenMs = Max(old.TakenMs, sighting.TakenMs),
            TakenBy = sighting.TakenMs is { } t && t >= (old.TakenMs ?? 0) ? sighting.TakenBy : old.TakenBy,
        };
        Cubes[index] = merged;
        return merged;
    }

    public CubeSighting? Find(string id) => Cubes.FirstOrDefault(c => c.Id == id);

    public IEnumerable<CubeSighting> OnMap(uint mapId) => Cubes.Where(c => c.MapId == mapId);

    /// <summary>Cube spots we opened (all maps, or one).</summary>
    public int OpenedCount(uint? mapId = null) => Cubes.Count(c => c.OpenedMs is not null && (mapId is null || c.MapId == mapId));

    private static long? Max(long? a, long? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}
