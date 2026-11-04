using System.Text.Json;
using Avion2.Protocol;

namespace Avion2.Engine;

/// <summary>
/// An id → icon table (skill or item ids to the game's icon names, e.g. 18010000 → ICON_CH_SKILL_001), as
/// tools/build_icons.py writes it into assets/gamedata/skillicons/index.json and itemicons/index.json.
/// </summary>
public sealed class IconTable
{
    public static readonly IconTable Empty = new(new Dictionary<uint, string>());

    private readonly IReadOnlyDictionary<uint, string> _icons;

    public IconTable(IReadOnlyDictionary<uint, string> icons) => _icons = icons;

    public int Count => _icons.Count;

    /// <summary>The icon name listed for exactly this id, or null.</summary>
    public string? Exact(uint id) => _icons.GetValueOrDefault(id);

    /// <summary>
    /// A skill's icon: its own, or its base skill's. The meter sees CC SS NNNN ids whose low four digits are the
    /// specialisations; every variant shares the base skill's icon. Null for skills without one (monster skills,
    /// basic actions, item procs).
    /// </summary>
    public string? Skill(uint skillId) => Exact(skillId) ?? (SkillIds.ToBase(skillId) is var b && b != skillId ? Exact(b) : null);

    /// <summary>Reads <c>{"icons": {"18010000": "ICON_CH_SKILL_001"}}</c>; null or broken text gives an empty table.</summary>
    public static IconTable Parse(string? json)
    {
        var icons = new Dictionary<uint, string>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return new(icons);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("icons", out var list) && list.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in list.EnumerateObject())
                {
                    if (uint.TryParse(entry.Name, out uint id) && entry.Value.ValueKind == JsonValueKind.String &&
                        entry.Value.GetString() is { Length: > 0 } name)
                    {
                        icons[id] = name;
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return new(icons);
    }
}

/// <summary>
/// Skill and item icons: the game's own pictures (NCSoft's public CDN, see assets/gamedata/PROVENANCE.md), built into
/// the app as small PNGs. A PNG in %AppData%\AVION2\skillicons or %AppData%\AVION2\itemicons replaces a built-in
/// one: named after the id (<c>18010000.png</c>; for skills also the base skill's id) or after the icon
/// (<c>ICON_CH_SKILL_001.png</c>). Nothing is downloaded at runtime.
/// </summary>
public static class GameIcons
{
    private static readonly string AppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AVION2");

    public static string SkillDirectory => Path.Combine(AppData, "skillicons");

    public static string ItemDirectory => Path.Combine(AppData, "itemicons");

    private static readonly Lazy<IconTable> SkillTable = new(() => IconTable.Parse(BundledGameData.Read("skillicons/index.json")));

    private static readonly Lazy<IconTable> ItemTable = new(() => IconTable.Parse(BundledGameData.Read("itemicons/index.json")));

    public static IconTable Skills => SkillTable.Value;

    public static IconTable Items => ItemTable.Value;

    /// <summary>A skill's icon as PNG bytes (the override folder first), or null when there is none.</summary>
    public static byte[]? SkillIcon(uint skillId, string? overrideDirectory = null) =>
        Load("skillicons", Skills.Skill(skillId), overrideDirectory ?? SkillDirectory, skillId, SkillIds.ToBase(skillId));

    /// <summary>An item's icon as PNG bytes (the override folder first), or null when there is none.</summary>
    public static byte[]? ItemIcon(uint itemId, string? overrideDirectory = null) =>
        Load("itemicons", Items.Exact(itemId), overrideDirectory ?? ItemDirectory, itemId, itemId);

    /// <summary>
    /// The file in the override folder named after the id, the base id or the icon; else the built-in icon.
    /// </summary>
    public static byte[]? Load(string set, string? icon, string overrideDirectory, uint id, uint baseId)
    {
        var names = new List<string> { id + ".png" };
        if (baseId != id)
        {
            names.Add(baseId + ".png");
        }

        if (icon is not null)
        {
            names.Add(icon + ".png");
        }

        foreach (string name in names)
        {
            try
            {
                string path = Path.Combine(overrideDirectory, name);
                if (File.Exists(path))
                {
                    return File.ReadAllBytes(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }

        return icon is null ? null : Bundled(set, icon);
    }

    /// <summary>A built-in icon by set and name ("skillicons", "ICON_CH_SKILL_001"), or null.</summary>
    public static byte[]? Bundled(string set, string icon)
    {
        using var stream = typeof(GameIcons).Assembly.GetManifestResourceStream($"gamedata/{set}/{icon}.png");
        if (stream is null)
        {
            return null;
        }

        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
