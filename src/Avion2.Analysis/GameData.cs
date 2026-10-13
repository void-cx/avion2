using System.Text.Json;

namespace Avion2.Analysis;

public sealed record NpcData(string Name, bool IsBoss, bool IsDummy = false);

/// <summary>
/// Optional name tables, loaded from <c>data/skills.json</c> (<c>{"11020000": "Name"}</c>) and
/// <c>data/npcs.json</c> (<c>{"2400032": {"name": "...", "isBoss": true}}</c>).
/// These are NCSoft game data and are not shipped with the repo.
/// </summary>
public sealed class GameData
{
    public static readonly GameData Empty = new(new Dictionary<uint, string>(), new Dictionary<uint, NpcData>());

    public GameData(IReadOnlyDictionary<uint, string> skills, IReadOnlyDictionary<uint, NpcData> npcs)
    {
        Skills = skills;
        Npcs = npcs;
    }

    public IReadOnlyDictionary<uint, string> Skills { get; }

    public IReadOnlyDictionary<uint, NpcData> Npcs { get; }

    public string SkillName(uint skillId)
    {
        if (Skills.TryGetValue(skillId, out var name))
        {
            return name;
        }

        return skillId == 0 ? "?" : $"Skill {skillId}";
    }

    public static GameData Load(string directory)
    {
        var skills = new Dictionary<uint, string>();
        var npcs = new Dictionary<uint, NpcData>();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        string skillsPath = Path.Combine(directory, "skills.json");
        if (File.Exists(skillsPath))
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(skillsPath), options) ?? new();
            foreach (var (key, value) in raw)
            {
                if (uint.TryParse(key, out uint id))
                {
                    skills[id] = value;
                }
            }
        }

        string npcsPath = Path.Combine(directory, "npcs.json");
        if (File.Exists(npcsPath))
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, NpcData>>(File.ReadAllText(npcsPath), options) ?? new();
            foreach (var (key, value) in raw)
            {
                if (uint.TryParse(key, out uint id))
                {
                    npcs[id] = value;
                }
            }
        }

        return new GameData(skills, npcs);
    }
}
