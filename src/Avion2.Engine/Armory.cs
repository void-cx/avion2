using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Avion2.Engine;

/// <summary>A search hit: enough to load the character.</summary>
public sealed record ArmoryHit(string CharacterId, string Name, ushort ServerId, string ServerName, int Level);

/// <param name="Slot">The game's slot number (1 main hand … 24 second rune).</param>
public sealed record ArmoryItem(uint Id, string Name, string Grade, int Enchant, int Exceed, int Slot, string SlotName, string? Icon);

/// <param name="Effects">What the points give, as the game words it ("Attack increase +2.5%").</param>
public sealed record ArmoryStat(string Type, string Name, int Value, IReadOnlyList<string> Effects);

public sealed record ArmoryBoard(int Id, string Name, int Open, int Total);

/// <summary>A node on a Daevanion board; the board is a 15 × 15 grid counted from 1.</summary>
/// <param name="Type">Start, Stat or SkillLevel.</param>
public sealed record ArmoryNode(int Row, int Column, string Name, string Type, string Grade, bool Open, IReadOnlyList<string> Effects);

/// <param name="SkillEffects">What the open nodes add up to: skill levels ("Onslaught +1")…</param>
/// <param name="StatEffects">…and stats ("HP +800").</param>
public sealed record ArmoryBoardDetail(IReadOnlyList<ArmoryNode> Nodes, IReadOnlyList<string> SkillEffects, IReadOnlyList<string> StatEffects);

/// <param name="Category">The slot the title is worn in: Attack, Defense or Etc.</param>
public sealed record ArmoryTitle(uint Id, string Name, string Grade, string Category, IReadOnlyList<string> Effects);

/// <param name="Category">Active, Passive or Dp (the DP skills).</param>
public sealed record ArmorySkill(uint Id, string Name, string Category, int Level, bool Equipped, string? Icon);

public sealed record ArmoryPet(string Name, int Level, string? Icon);

/// <summary>A character as the official AION2 site shows it.</summary>
/// <param name="Legion">The site calls it <c>regionName</c>; it holds the legion.</param>
public sealed record ArmoryProfile(
    string CharacterId,
    string Name,
    string ClassName,
    int Level,
    int CombatPower,
    int? ItemLevel,
    string Race,
    string? Legion,
    ushort ServerId,
    string ServerName,
    string? ImageUrl,
    IReadOnlyList<ArmoryStat> Stats,
    IReadOnlyList<ArmoryBoard> Daevanion,
    IReadOnlyList<ArmoryTitle> Titles,
    int TitlesOwned,
    IReadOnlyList<ArmoryItem> Gear,
    IReadOnlyList<ArmoryItem> Skins,
    ArmoryItem? Wings,
    ArmoryItem? WingSkin,
    ArmoryPet? Pet,
    IReadOnlyList<ArmorySkill> Skills,
    DateTimeOffset FetchedAt);

/// <summary>A lookup's outcome: the character, or why there is none.</summary>
public sealed record ArmoryLookup(ArmoryProfile? Profile, string? Problem);

/// <summary>
/// Reads characters from the official AION2 site (aion2.plaync.com), the public data behind its character
/// pages: no login, nothing about the user is sent but the name looked up. Global servers only. Only on demand,
/// and every answer is kept for 10 minutes, so opening a page twice asks once.
/// </summary>
public sealed partial class ArmoryClient
{
    private const string SearchUrl = "https://api-search.plaync.com/aion2global/search/v2/character";
    private const string ApiUrl = "https://aion2.plaync.com/api/character";
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<(string, ushort), ArmoryProfile> _cache = new();

    public ArmoryClient(HttpMessageHandler? handler = null, Func<DateTimeOffset>? now = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(10);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"AVION2/{UpdateChecker.CurrentVersion}");
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The site's region code for a Global server ("eu"), or null for servers it doesn't cover.</summary>
    public static string? RegionCode(ushort serverId) => BundledGameData.Server(serverId)?.Region switch
    {
        "NA East" => "nae",
        "NA West" => "naw",
        "Europe" => "eu",
        "Latin America" => "la",
        "Asia" => "as",
        _ => null,
    };

    /// <summary>The cached profile, if it was loaded in the last 10 minutes.</summary>
    public ArmoryProfile? Cached(string name, ushort serverId) =>
        _cache.TryGetValue(Key(name, serverId), out var profile) && _now() - profile.FetchedAt < CacheFor ? profile : null;

    /// <summary>Characters on a server whose name contains <paramref name="keyword"/>.</summary>
    public async Task<IReadOnlyList<ArmoryHit>> SearchAsync(string keyword, ushort serverId, CancellationToken cancellationToken = default)
    {
        if (RegionCode(serverId) is not { } region || keyword.Trim().Length == 0)
        {
            return [];
        }

        string url = $"{SearchUrl}?keyword={Uri.EscapeDataString(keyword.Trim())}&page=1&size=20&localeInfo=en-US&region={region}&serverId={serverId}";
        return ParseSearch(await GetAsync(url, cancellationToken));
    }

    /// <summary>Loads a character by name and server; from the cache unless <paramref name="refresh"/>.</summary>
    public async Task<ArmoryLookup> LoadAsync(string name, ushort serverId, bool refresh = false, CancellationToken cancellationToken = default)
    {
        string server = BundledGameData.Server(serverId)?.Name ?? serverId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (RegionCode(serverId) is not { } region)
        {
            return new(null, $"The official site has no characters for {server}.");
        }

        if (!refresh && Cached(name, serverId) is { } cached)
        {
            return new(cached, null);
        }

        try
        {
            var hit = (await SearchAsync(name, serverId, cancellationToken))
                .FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase) && h.ServerId == serverId);
            if (hit is null)
            {
                return new(null, $"No character named {name} on {server}.");
            }

            string query = $"?lang=en&characterId={Uri.EscapeDataString(hit.CharacterId)}&serverId={serverId}&region={region}";
            var info = GetAsync($"{ApiUrl}/info{query}", cancellationToken);
            var equipment = GetAsync($"{ApiUrl}/equipment{query}", cancellationToken);
            var profile = ParseProfile(hit.CharacterId, await info, await equipment, _now());
            _cache[Key(profile.Name, serverId)] = profile;
            return new(profile, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new(null, "The official AION2 site did not answer. Try again in a moment.");
        }
    }

    /// <summary>One Daevanion board of a loaded character, node by node; null when the site does not answer.</summary>
    public async Task<ArmoryBoardDetail?> LoadBoardAsync(ArmoryProfile profile, int boardId, CancellationToken cancellationToken = default)
    {
        if (RegionCode(profile.ServerId) is not { } region)
        {
            return null;
        }

        var key = (profile.CharacterId, boardId);
        if (_boards.TryGetValue(key, out var cached) && _now() - cached.At < CacheFor)
        {
            return cached.Board;
        }

        try
        {
            string url = $"{ApiUrl}/daevanion/detail?lang=en&characterId={Uri.EscapeDataString(profile.CharacterId)}&serverId={profile.ServerId}&boardId={boardId}&region={region}";
            var board = ParseBoard(await GetAsync(url, cancellationToken));
            _boards[key] = (board, _now());
            return board;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    private readonly ConcurrentDictionary<(string, int), (ArmoryBoardDetail Board, DateTimeOffset At)> _boards = new();

    public static ArmoryBoardDetail ParseBoard(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var nodes = Array(root, "nodeList").Where(n => Str(n, "type") is { } type && type != "None").Select(n => new ArmoryNode(
            Int(n, "row"), Int(n, "col"), Str(n, "name") ?? string.Empty, Str(n, "type") ?? string.Empty, Str(n, "grade") ?? string.Empty,
            Int(n, "open") == 1, Array(n, "effectList").Select(e => Str(e, "desc") ?? string.Empty).ToList())).ToList();
        return new ArmoryBoardDetail(nodes,
            Array(root, "openSkillEffectList").Select(e => Str(e, "desc") ?? string.Empty).ToList(),
            Array(root, "openStatEffectList").Select(e => Str(e, "desc") ?? string.Empty).ToList());
    }

    private static (string, ushort) Key(string name, ushort serverId) => (name.ToLowerInvariant(), serverId);

    private async Task<string> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new KeyNotFoundException(url);
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>Reads the site's search answer; names come with the match in <c>&lt;strong&gt;</c>.</summary>
    public static IReadOnlyList<ArmoryHit> ParseSearch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return list.EnumerateArray().Select(h => new ArmoryHit(
            Uri.UnescapeDataString(Str(h, "characterId") ?? string.Empty),
            Tags().Replace(Str(h, "name") ?? string.Empty, string.Empty),
            (ushort)Int(h, "serverId"),
            Str(h, "serverName") ?? string.Empty,
            Int(h, "level"))).Where(h => h.CharacterId.Length > 0).ToList();
    }

    /// <summary>Reads the site's <c>info</c> and <c>equipment</c> answers for one character.</summary>
    public static ArmoryProfile ParseProfile(string characterId, string infoJson, string equipmentJson, DateTimeOffset fetchedAt)
    {
        using var info = JsonDocument.Parse(infoJson);
        using var equipment = JsonDocument.Parse(equipmentJson);
        var root = info.RootElement;
        var profile = root.GetProperty("profile");

        var stats = Array(root, "stat", "statList").Select(s => new ArmoryStat(
            Str(s, "type") ?? string.Empty, Str(s, "name") ?? string.Empty, Int(s, "value"),
            s.TryGetProperty("statSecondList", out var effects) && effects.ValueKind == JsonValueKind.Array
                ? effects.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList()
                : [])).ToList();
        int? itemLevel = stats.FirstOrDefault(s => s.Type == "ItemLevel")?.Value;

        var titles = Array(root, "title", "titleList").Select(t => new ArmoryTitle(
            (uint)Int(t, "id"), Str(t, "name") ?? string.Empty, Str(t, "grade") ?? string.Empty, Str(t, "equipCategory") ?? string.Empty,
            Array(t, "equipStatList").Select(d => Str(d, "desc") ?? string.Empty).ToList())).ToList();

        var boards = Array(root, "daevanion", "boardList")
            .Select(b => new ArmoryBoard(Int(b, "id"), Str(b, "name") ?? string.Empty, Int(b, "openNodeCount"), Int(b, "totalNodeCount"))).ToList();

        var gearRoot = equipment.RootElement;
        var gear = Array(gearRoot, "equipment", "equipmentList").Select(Item).OrderBy(i => SlotOrder(i.Slot)).ToList();
        var skins = Array(gearRoot, "equipment", "skinList").Select(Item).OrderBy(i => i.Slot).ToList();
        JsonElement? Part(string name) =>
            gearRoot.TryGetProperty("petwing", out var petwing) && petwing.ValueKind == JsonValueKind.Object &&
            petwing.TryGetProperty(name, out var part) && part.ValueKind == JsonValueKind.Object ? part : null;
        var pet = Part("pet") is { } p ? new ArmoryPet(Str(p, "name") ?? string.Empty, Int(p, "level"), NullIfEmpty(Str(p, "icon"))) : null;
        var skills = Array(gearRoot, "skill", "skillList").Where(s => Int(s, "acquired") == 1).Select(s => new ArmorySkill(
            (uint)Int(s, "id"), Str(s, "name") ?? string.Empty, Str(s, "category") ?? string.Empty, Int(s, "skillLevel"), Int(s, "equip") == 1,
            NullIfEmpty(Str(s, "icon")))).ToList();

        return new ArmoryProfile(
            characterId,
            Str(profile, "characterName") ?? string.Empty,
            Str(profile, "className") ?? string.Empty,
            Int(profile, "characterLevel"),
            Int(profile, "combatPower"),
            itemLevel,
            Str(profile, "raceName") ?? string.Empty,
            NullIfEmpty(Str(profile, "regionName")),
            (ushort)Int(profile, "serverId"),
            Str(profile, "serverName") ?? string.Empty,
            NullIfEmpty(Str(profile, "profileImage")),
            stats.Where(s => s.Type != "ItemLevel").ToList(),
            boards,
            titles,
            root.TryGetProperty("title", out var title) ? Int(title, "ownedCount") : 0,
            gear,
            skins,
            Part("wing") is { } wing ? Item(wing) : null,
            Part("wingSkin") is { } wingSkin ? Item(wingSkin) : null,
            pet,
            skills,
            fetchedAt);
    }

    /// <summary>Weapons, armor top to bottom, then accessories, as the game's character window lists them.</summary>
    private static int SlotOrder(int slot) => slot switch
    {
        <= 8 => slot, // weapons, helmet, shoulders, torso, pants, gloves, boots
        19 => 9,      // cape
        17 => 10,     // belt
        _ => 10 + slot,
    };

    private static ArmoryItem Item(JsonElement i) => new(
        (uint)Int(i, "id"), Str(i, "name") ?? string.Empty, Str(i, "grade") ?? string.Empty, Int(i, "enchantLevel"), Int(i, "exceedLevel"),
        Int(i, "slotPos"), Str(i, "slotPosName") ?? string.Empty, NullIfEmpty(Str(i, "icon")));

    private static IEnumerable<JsonElement> Array(JsonElement element, params string[] path)
    {
        foreach (string name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
            {
                return [];
            }
        }

        return element.ValueKind == JsonValueKind.Array ? element.EnumerateArray().ToList() : [];
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : 0;

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}
