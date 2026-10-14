using System.Text.Json;

namespace Avion2.Analysis;

public sealed record NpcData(string Name, bool IsBoss, bool IsDummy = false);

/// <summary>
/// Name tables: skills (<c>{"11020000": "Name"}</c>), NPCs (<c>{"2400032": {"name": "...", "isBoss": true}}</c>),
/// field boss spawn codes (<c>{"101005": 2100077}</c>) and maps (<c>{"1010": "Name"}</c>).
/// The app ships them built in (see assets/gamedata); a data directory can override single files.
/// </summary>
public sealed class GameData
{
    public static readonly GameData Empty = new(new Dictionary<uint, string>(), new Dictionary<uint, NpcData>());

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public GameData(IReadOnlyDictionary<uint, string> skills, IReadOnlyDictionary<uint, NpcData> npcs,
        IReadOnlyDictionary<uint, uint>? fieldBosses = null, IReadOnlyDictionary<uint, string>? maps = null)
    {
        Skills = skills;
        Npcs = npcs;
        FieldBosses = fieldBosses ?? new Dictionary<uint, uint>();
        Maps = maps ?? new Dictionary<uint, string>();
    }

    /// <summary>Field boss spawn code (map × 100 + n) → NPC code.</summary>
    public IReadOnlyDictionary<uint, uint> FieldBosses { get; }

    /// <summary>Map id (as in <c>21 36</c> and the field boss tables) → zone name.</summary>
    public IReadOnlyDictionary<uint, string> Maps { get; }

    public IReadOnlyDictionary<uint, string> Skills { get; }

    public IReadOnlyDictionary<uint, NpcData> Npcs { get; }

    /// <summary>"Ritualist Garshim", or "Field boss 101005" while the tables are missing.</summary>
    public string FieldBossName(uint spawnCode) =>
        FieldBosses.TryGetValue(spawnCode, out uint npc) && Npcs.TryGetValue(npc, out var data)
            ? data.Name
            : $"Field boss {spawnCode}";

    /// <summary>The zone name, or "Map 1010" when unknown.</summary>
    public string MapName(uint mapId) => Maps.TryGetValue(mapId, out var name) ? name : $"Map {mapId}";

    public string SkillName(uint skillId)
    {
        if (Skills.TryGetValue(skillId, out var name))
        {
            return name;
        }

        return skillId == 0 ? "?" : $"Skill {skillId}";
    }

    /// <summary>Builds the tables from JSON text; a null text leaves that table empty.</summary>
    public static GameData Parse(string? skillsJson, string? npcsJson, string? fieldBossesJson = null, string? mapsJson = null) =>
        new(ById<string>(skillsJson), ById<NpcData>(npcsJson), ById<uint>(fieldBossesJson), ById<string>(mapsJson));

    /// <summary>Loads skills.json, npcs.json, field_bosses.json and maps.json from a directory (missing files stay empty).</summary>
    public static GameData Load(string directory) => Parse(
        ReadIfExists(Path.Combine(directory, "skills.json")),
        ReadIfExists(Path.Combine(directory, "npcs.json")),
        ReadIfExists(Path.Combine(directory, "field_bosses.json")),
        ReadIfExists(Path.Combine(directory, "maps.json")));

    public static string? ReadIfExists(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    private static Dictionary<uint, T> ById<T>(string? json)
    {
        var result = new Dictionary<uint, T>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, T>>(json, Options) ?? new())
        {
            if (uint.TryParse(key, out uint id) && value is not null)
            {
                result[id] = value;
            }
        }

        return result;
    }
}
