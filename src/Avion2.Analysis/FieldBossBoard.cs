using System.Text.Json;
using Avion2.Protocol;

namespace Avion2.Analysis;

public enum FieldBossStatus
{
    /// <summary>Dead; <see cref="FieldBossRow.At"/> is the announced respawn time.</summary>
    Respawning,

    /// <summary>The respawn time has passed but no newer table arrived yet (probably up).</summary>
    Due,

    /// <summary>Up; <see cref="FieldBossRow.At"/> is when it spawned.</summary>
    Alive,
}

public sealed record FieldBossRow(uint SpawnCode, uint MapId, string Name, FieldBossStatus Status, DateTimeOffset At, DateTimeOffset SeenAt);

/// <summary>
/// The latest field boss table per map (<c>01 91</c>, sent for the map you are on). Respawn times are absolute,
/// so the board is saved and stays useful after a restart or when the meter is stopped.
/// </summary>
public sealed class FieldBossBoard
{
    private readonly Dictionary<uint, MapTable> _maps = new();

    public sealed record MapTable(uint MapId, long ReceivedAtMs, IReadOnlyList<FieldBossEntry> Entries);

    public IReadOnlyCollection<MapTable> Maps => _maps.Values;

    /// <summary>Applies a table; returns true if anything changed (callers can then save).</summary>
    public bool Apply(FieldBossTableEvent table)
    {
        bool changed = !_maps.TryGetValue(table.MapId, out var old) || !Same(old.Entries, table.Entries);
        _maps[table.MapId] = new MapTable(table.MapId, table.TimestampMs, table.Entries);
        return changed;
    }

    public IReadOnlyList<FieldBossRow> Rows(GameData data, DateTimeOffset now)
    {
        var rows = new List<FieldBossRow>();
        foreach (var map in _maps.Values)
        {
            var seen = DateTimeOffset.FromUnixTimeMilliseconds(map.ReceivedAtMs);
            foreach (var entry in map.Entries)
            {
                var at = DateTimeOffset.FromUnixTimeMilliseconds(entry.TimestampMs);
                var status = entry.Alive ? FieldBossStatus.Alive : at > now ? FieldBossStatus.Respawning : FieldBossStatus.Due;
                rows.Add(new FieldBossRow(entry.SpawnCode, map.MapId, data.FieldBossName(entry.SpawnCode), status, at, seen));
            }
        }

        // Soonest respawn first, then the ones due, then the ones that are up (newest first).
        return rows
            .OrderBy(r => r.Status)
            .ThenBy(r => r.Status == FieldBossStatus.Alive ? -r.At.ToUnixTimeMilliseconds() : r.At.ToUnixTimeMilliseconds())
            .ToList();
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_maps.Values.ToList()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not critical
        }
    }

    public static FieldBossBoard Load(string path)
    {
        var board = new FieldBossBoard();
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<List<MapTable>>(File.ReadAllText(path)) is { } maps)
            {
                foreach (var map in maps)
                {
                    board._maps[map.MapId] = map;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // start empty
        }

        return board;
    }

    private static bool Same(IReadOnlyList<FieldBossEntry> a, IReadOnlyList<FieldBossEntry> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.SpawnCode == p.Second.SpawnCode &&
                                                 p.First.Alive == p.Second.Alive &&
                                                 p.First.TimestampMs == p.Second.TimestampMs);
}
