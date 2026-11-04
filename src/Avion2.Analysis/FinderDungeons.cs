using System.Text.Json;

namespace Avion2.Analysis;

/// <summary>
/// Names the dungeon of a group-finder room (<c>02 97</c> carries only its id, 600001 …) from the dungeon name table
/// (<c>dungeon_names.json</c>, the game's own names with the difficulty or stage: "Krao Cave (Exploration)").
/// </summary>
public static class FinderDungeons
{
    /// <summary>Dungeon id → name, from <c>{"dungeons": {"600002": "Krao Cave (Exploration)"}}</c>.</summary>
    public static IReadOnlyDictionary<uint, string> Parse(string? json)
    {
        var names = new Dictionary<uint, string>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return names;
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("dungeons", out var dungeons) ||
            dungeons.ValueKind != JsonValueKind.Object)
        {
            return names;
        }

        foreach (var dungeon in dungeons.EnumerateObject())
        {
            if (uint.TryParse(dungeon.Name, out uint id) && dungeon.Value.ValueKind == JsonValueKind.String &&
                dungeon.Value.GetString() is { Length: > 0 } name)
            {
                names[id] = name;
            }
        }

        return names;
    }

    /// <summary>The name, or "Dungeon 600021" when the tables do not know it.</summary>
    public static string Name(IReadOnlyDictionary<uint, string> names, uint dungeonId) =>
        names.TryGetValue(dungeonId, out var name) ? name : $"Dungeon {dungeonId}";

    public static string Label(Protocol.FinderRoomState state) => state switch
    {
        Protocol.FinderRoomState.Recruiting => "Recruiting",
        Protocol.FinderRoomState.InDungeon => "In the dungeon",
        Protocol.FinderRoomState.Cleared => "Cleared",
        _ => $"State {(byte)state}",
    };

    public static string Label(Protocol.FinderRole role) => role switch
    {
        Protocol.FinderRole.Tank => "Tank",
        Protocol.FinderRole.Damage => "Damage",
        Protocol.FinderRole.Healer => "Healer",
        Protocol.FinderRole.Any => "Any role",
        _ => string.Empty,
    };
}
