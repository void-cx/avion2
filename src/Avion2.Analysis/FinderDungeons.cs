using System.Text.Json;

namespace Avion2.Analysis;

/// <summary>
/// Names the dungeon of a group-finder room (<c>02 97</c> carries only its id, 600001 …): the NPC table gives the bosses
/// with that <c>dungeonId</c> and their tier ("Conquest [Normal]", "Exploration" …), and the dungeon table names the
/// dungeon those bosses belong to ("Krao Cave (Conquest [Normal])").
/// </summary>
public static class FinderDungeons
{
    /// <summary>Dungeon id → name with tier, for every id whose bosses the dungeon table knows.</summary>
    /// <param name="npcsJson">An NPC table (<c>{"2300104": {"name": …, "tier": …, "dungeonId": 600001}}</c>), English
    /// like the dungeon table.</param>
    public static IReadOnlyDictionary<uint, string> Build(string? npcsJson, DungeonCatalog catalog)
    {
        var names = new Dictionary<uint, string>();
        if (string.IsNullOrWhiteSpace(npcsJson))
        {
            return names;
        }

        var bosses = new Dictionary<uint, (List<(uint? Npc, string Name)> Bosses, string? Tier)>();
        using (var doc = JsonDocument.Parse(npcsJson))
        {
            foreach (var npc in doc.RootElement.EnumerateObject())
            {
                if (!uint.TryParse(npc.Name, out uint code) || npc.Value.ValueKind != JsonValueKind.Object ||
                    !npc.Value.TryGetProperty("dungeonId", out var id) || !id.TryGetUInt32(out uint dungeon) ||
                    !npc.Value.TryGetProperty("name", out var name) || name.GetString() is not { Length: > 0 } text)
                {
                    continue;
                }

                if (!bosses.TryGetValue(dungeon, out var entry))
                {
                    entry = ([], null);
                }

                entry.Bosses.Add((code, text));
                if (entry.Tier is null && npc.Value.TryGetProperty("tier", out var tier) && tier.ValueKind == JsonValueKind.String)
                {
                    entry.Tier = tier.GetString();
                }

                bosses[dungeon] = entry;
            }
        }

        foreach (var (dungeon, (list, tier)) in bosses)
        {
            if (catalog.Match(list) is { } match)
            {
                names[dungeon] = string.IsNullOrWhiteSpace(tier) ? match.Name : $"{match.Name} ({tier})";
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
