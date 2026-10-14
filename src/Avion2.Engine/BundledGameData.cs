using Avion2.Analysis;

namespace Avion2.Engine;

/// <summary>Where the name tables in use came from.</summary>
/// <param name="Directory">The override directory, or null when everything is built in.</param>
public sealed record GameDataSource(string Language, string? Directory)
{
    public override string ToString() => Directory is null ? $"built in ({Language})" : $"{Directory} (missing files built in, {Language})";
}

/// <summary>
/// The name tables compiled into the app (assets/gamedata, see PROVENANCE.md there). Nothing is downloaded:
/// a directory with skills.json / npcs.json / field_bosses.json / maps.json overrides single tables.
/// </summary>
public static class BundledGameData
{
    public static readonly string[] Languages = ["en", "de", "fr", "es", "pt", "ru", "ja", "ko", "zh-Hans", "zh-Hant"];

    /// <summary>Per-user override directory (%AppData%\AVION2\data), used only when it has tables in it.</summary>
    public static string UserDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AVION2", "data");

    /// <summary>A built-in file such as "skills/en.json", or null.</summary>
    public static string? Read(string name)
    {
        using var stream = typeof(BundledGameData).Assembly.GetManifestResourceStream("gamedata/" + name);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Zone maps in the order they are offered (key = aion2hub / game map asset name).</summary>
    public static readonly string[] WorldMapKeys =
        ["World_L_A", "World_L_B", "World_L_Starter", "World_D_A", "World_D_B", "World_D_Starter", "Abyss_Reshanta_A", "Abyss_Reshanta_B"];

    private static readonly Dictionary<string, WorldMap> LoadedMaps = new();

    /// <summary>A zone map's markers and regions (parsed on first use), or null.</summary>
    public static WorldMap? WorldMap(string key)
    {
        lock (LoadedMaps)
        {
            if (!LoadedMaps.TryGetValue(key, out var map) && Read($"world/{key}.json") is { } json)
            {
                LoadedMaps[key] = map = Avion2.Analysis.WorldMap.Parse(json);
            }

            return map;
        }
    }

    /// <summary>The map picture (JPEG, half the marker resolution).</summary>
    public static Stream? WorldMapImage(string key) =>
        typeof(BundledGameData).Assembly.GetManifestResourceStream($"gamedata/world/{key}.jpg");

    private static readonly Lazy<GearForge> ForgeTable = new(() => GearForge.Parse(Read("gear_forge.json")));

    /// <summary>Enchant/amplify ladders per item (English names), parsed on first use.</summary>
    public static GearForge Forge => ForgeTable.Value;

    /// <summary>
    /// The tables for <paramref name="language"/>: each file from <paramref name="overrideDirectory"/> when it
    /// exists there, otherwise built in (English when a table has no translation).
    /// </summary>
    public static GameData Load(string language, string? overrideDirectory = null)
    {
        if (!Languages.Contains(language))
        {
            language = "en";
        }

        string? Pick(string file, string? bundled) =>
            (overrideDirectory is null ? null : GameData.ReadIfExists(Path.Combine(overrideDirectory, file))) ?? bundled;

        return GameData.Parse(
            Pick("skills.json", Read($"skills/{language}.json") ?? Read("skills/en.json")),
            Pick("npcs.json", Read($"npcs/{language}.json") ?? Read("npcs/en.json")),
            Pick("field_bosses.json", Read("field_bosses.json")),
            Pick("maps.json", Read($"maps/{language}.json") ?? Read("maps/en.json")));
    }
}
