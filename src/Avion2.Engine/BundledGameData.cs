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
    public static readonly string[] Languages = ["en", "de", "fr", "es", "pt", "ru", "ja", "ko"];

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

    /// <summary>A map marker icon (PNG) by file name, or null.</summary>
    public static Stream? MapIcon(string name) =>
        typeof(BundledGameData).Assembly.GetManifestResourceStream($"gamedata/world/icons/{name}");

    /// <summary>A boss portrait (PNG) by its path, "bosses/&lt;name&gt;.png" (see <see cref="BossPortraits"/>), or null.</summary>
    public static Stream? BossPortrait(string path) =>
        typeof(BundledGameData).Assembly.GetManifestResourceStream("gamedata/" + path);

    /// <summary>The map picture (JPEG, half the marker resolution).</summary>
    public static Stream? WorldMapImage(string key) =>
        typeof(BundledGameData).Assembly.GetManifestResourceStream($"gamedata/world/{key}.jpg");

    private static readonly Lazy<DungeonCatalog> DungeonTable = new(() => DungeonCatalog.Parse(Read("dungeons.json")));

    private static readonly Lazy<GameData> EnglishNpcTable = new(() => GameData.Parse(null, Read("npcs/en.json")));

    private static readonly Lazy<Dictionary<uint, string>> ItemTable = new(() =>
    {
        var items = new Dictionary<uint, string>();
        if (Read("items.json") is { } json)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateObject())
            {
                if (uint.TryParse(item.Name, out uint id) && item.Value.GetString() is { } name)
                {
                    items[id] = name;
                }
            }
        }

        return items;
    });

    /// <summary>A game server: its name ("Gauss"), short name ("GAU"), faction and region.</summary>
    public sealed record ServerInfo(string Name, string? Short, string Faction, string Region);

    private static readonly Lazy<Dictionary<ushort, ServerInfo>> ServerTable = new(() =>
    {
        var servers = new Dictionary<ushort, ServerInfo>();
        if (Read("servers.json") is { } json)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var server in doc.RootElement.GetProperty("servers").EnumerateObject())
            {
                if (ushort.TryParse(server.Name, out ushort id))
                {
                    var v = server.Value;
                    servers[id] = new ServerInfo(v.GetProperty("name").GetString() ?? $"Server {id}",
                        v.TryGetProperty("short", out var s) ? s.GetString() : null,
                        v.TryGetProperty("faction", out var f) ? f.GetString() ?? "?" : "?",
                        v.TryGetProperty("region", out var r) ? r.GetString() ?? "?" : "?");
                }
            }
        }

        return servers;
    });

    /// <summary>The server with this id (as in player records, e.g. 1321 = Gauss), or null when unknown.</summary>
    public static ServerInfo? Server(ushort id) => ServerTable.Value.GetValueOrDefault(id);

    /// <summary>Every known server by id.</summary>
    public static IReadOnlyDictionary<ushort, ServerInfo> Servers => ServerTable.Value;

    /// <summary>A pet kind: its name per language code, the souls the pet takes and its soul item (none for shop pets).</summary>
    public sealed record PetInfo(IReadOnlyDictionary<string, string> Names, uint Souls, uint? SoulItem);

    /// <summary>The pets file: every pet kind by id, and the souls each pet level takes to the next.</summary>
    public sealed record PetTable(IReadOnlyDictionary<uint, PetInfo> Pets, IReadOnlyList<uint> SoulsPerLevel);

    private static readonly Lazy<PetTable> PetData = new(() => ParsePets(Read("pets.json")));

    public static PetTable ParsePets(string? json)
    {
        var pets = new Dictionary<uint, PetInfo>();
        var levels = new List<uint>();
        if (json is null)
        {
            return new PetTable(pets, levels);
        }

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        foreach (var level in doc.RootElement.GetProperty("levels").EnumerateArray())
        {
            levels.Add(level.GetUInt32());
        }

        foreach (var pet in doc.RootElement.GetProperty("pets").EnumerateObject())
        {
            if (!uint.TryParse(pet.Name, out uint id))
            {
                continue;
            }

            var names = pet.Value.GetProperty("name").EnumerateObject()
                .Where(n => n.Value.GetString() is { Length: > 0 })
                .ToDictionary(n => n.Name, n => n.Value.GetString()!, StringComparer.Ordinal);
            pets[id] = new PetInfo(names, pet.Value.GetProperty("souls").GetUInt32(),
                pet.Value.TryGetProperty("soul", out var soul) ? soul.GetUInt32() : null);
        }

        return new PetTable(pets, levels);
    }

    /// <summary>Every pet kind from the client's tables, with the souls per level.</summary>
    public static PetTable Pets => PetData.Value;

    /// <summary>A pet kind's name in a language ("de"), English when that language lacks it; null for an unknown kind.</summary>
    public static string? PetName(uint pet, string language) =>
        Pets.Pets.TryGetValue(pet, out var info) ? info.Names.GetValueOrDefault(language) ?? info.Names.GetValueOrDefault("en") : null;

    /// <summary>The soul item of a pet kind ("Soul: Sparkie"), or null.</summary>
    public static uint? PetSoulItem(uint pet) => Pets.Pets.GetValueOrDefault(pet)?.SoulItem;

    /// <summary>An item's English name ("Enhance Stone"), or null when the table does not know it.</summary>
    public static string? ItemName(uint itemId) => ItemTable.Value.GetValueOrDefault(itemId);

    /// <summary>Dungeons and instances with their bosses (English names), parsed on first use.</summary>
    public static DungeonCatalog Dungeons => DungeonTable.Value;

    /// <summary>An NPC's English name (the dungeon table is English).</summary>
    public static string? EnglishNpcName(uint code) => EnglishNpcTable.Value.Npcs.GetValueOrDefault(code)?.Name;

    private static readonly Lazy<CubeTable> CubeRewardTable = new(() => CubeTable.Parse(Read("cube_rewards.json")));

    /// <summary>The expeditions' reward cubes (bosses, Odyle per share, guaranteed reward), parsed on first use.</summary>
    public static CubeTable Cubes => CubeRewardTable.Value;

    private static readonly Lazy<GearForge> ForgeTable = new(() => GearForge.Parse(Read("gear_forge.json")));

    /// <summary>Enchant/amplify ladders per item (English names), parsed on first use.</summary>
    public static GearForge Forge => ForgeTable.Value;

    private static readonly Lazy<GearStats> GearStatsTable = new(() => GearStats.Parse(Read("gear_stats.json")));

    /// <summary>Item levels and the stats each enchant level adds, parsed on first use.</summary>
    public static GearStats GearStats => GearStatsTable.Value;

    private static readonly Lazy<RecipeBook> RecipeTable = new(() => RecipeBook.Parse(Read("recipes.json")));

    /// <summary>The crafting recipes (materials, yields), parsed on first use.</summary>
    public static RecipeBook Recipes => RecipeTable.Value;

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
