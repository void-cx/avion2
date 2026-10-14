using System.Text.Json;
using System.Text.Json.Nodes;

namespace Avion2.Engine;

public sealed record ImportResult(int Skills, int Npcs, int Bosses, int Dummies, string Directory, int FieldBosses = 0);

/// <summary>
/// Imports skill and NPC name tables from taengu/A2Tools-DPS-Meter (extracted from the game client).
/// The files are written to a local data directory and never committed: the repo is GPL-3.0 and the
/// strings are NCSoft content.
/// </summary>
public static class GameDataImporter
{
    public const string Repository = "https://github.com/taengu/A2Tools-DPS-Meter";
    private const string RawBase = "https://raw.githubusercontent.com/taengu/A2Tools-DPS-Meter/main/src/data/i18n";

    public static string[] Languages => BundledGameData.Languages;

    /// <param name="source">Local A2Tools clone (or its i18n folder); null downloads from GitHub.</param>
    public static async Task<ImportResult> ImportAsync(string language, string targetDirectory, string? source = null, CancellationToken cancellationToken = default)
    {
        if (!Languages.Contains(language))
        {
            throw new ArgumentException($"Unknown language '{language}'. Available: {string.Join(", ", Languages)}", nameof(language));
        }

        string skillsJson;
        string npcsJson;
        string origin;
        if (source is not null)
        {
            string i18n = FindI18nDirectory(source)
                ?? throw new DirectoryNotFoundException($"No skills/ and npcs/ folders found under {source}");
            skillsJson = await File.ReadAllTextAsync(Path.Combine(i18n, "skills", $"{language}.json"), cancellationToken);
            npcsJson = await File.ReadAllTextAsync(Path.Combine(i18n, "npcs", $"{language}.json"), cancellationToken);
            origin = Path.GetFullPath(i18n);
        }
        else
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("avion2-data-import");
            skillsJson = await http.GetStringAsync($"{RawBase}/skills/{language}.json", cancellationToken);
            npcsJson = await http.GetStringAsync($"{RawBase}/npcs/{language}.json", cancellationToken);
            origin = $"{RawBase}/{{skills,npcs}}/{language}.json";
        }

        // Validate before writing so a bad download never replaces working tables.
        var skills = JsonNode.Parse(skillsJson)?.AsObject() ?? throw new InvalidDataException("skills: not a JSON object");
        var npcs = JsonNode.Parse(npcsJson)?.AsObject() ?? throw new InvalidDataException("npcs: not a JSON object");
        if (skills.Count == 0 || npcs.Count == 0 || skills.Any(kv => !uint.TryParse(kv.Key, out _)))
        {
            throw new InvalidDataException("Unexpected table format; the upstream layout may have changed.");
        }

        int bosses = npcs.Count(kv => kv.Value?["isBoss"]?.GetValue<bool>() == true);
        int dummies = npcs.Count(kv => kv.Value?["isDummy"]?.GetValue<bool>() == true);

        Directory.CreateDirectory(targetDirectory);
        int fieldBosses = await ImportFieldBossesAsync(targetDirectory, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(targetDirectory, "skills.json"), skillsJson, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(targetDirectory, "npcs.json"), npcsJson, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(targetDirectory, "source.json"), JsonSerializer.Serialize(new
        {
            origin,
            repository = Repository,
            license = "GPL-3.0 (repository); names are NCSoft game content. Local use only, do not commit.",
            language,
            importedAt = DateTimeOffset.UtcNow,
        }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

        return new ImportResult(skills.Count, npcs.Count, bosses, dummies, Path.GetFullPath(targetDirectory), fieldBosses);
    }

    private const string FieldBossSource =
        "https://raw.githubusercontent.com/ZDYoung0519/NOIA2/main/src-tauri/src/dps_meter/capture/parser/field_boss_timer.rs";

    /// <summary>
    /// The field boss spawn table (spawn code → NPC code) published in NOIA2's source (GPL-3.0). It is parsed
    /// into data/field_bosses.json locally and never committed. Failure only means unnamed field bosses.
    /// </summary>
    public static async Task<int> ImportFieldBossesAsync(string targetDirectory, CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("avion2-data-import");
            string source = await http.GetStringAsync(FieldBossSource, cancellationToken);
            int start = source.IndexOf("FIELD_BOSS_CODES", StringComparison.Ordinal);
            int end = start < 0 ? -1 : source.IndexOf("];", start, StringComparison.Ordinal);
            if (start < 0 || end < 0)
            {
                return 0;
            }

            var table = new SortedDictionary<string, uint>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         source[start..end], @"\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)"))
            {
                table[m.Groups[2].Value] = uint.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            }

            if (table.Count == 0)
            {
                return 0;
            }

            await File.WriteAllTextAsync(Path.Combine(targetDirectory, "field_bosses.json"),
                JsonSerializer.Serialize(table, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            return table.Count;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return 0;
        }
    }

    /// <summary>Language of the installed tables, from source.json; null if none.</summary>
    public static string? InstalledLanguage(string directory)
    {
        try
        {
            string path = Path.Combine(directory, "source.json");
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?["language"]?.GetValue<string>() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? FindI18nDirectory(string root)
    {
        foreach (string candidate in new[] { root, Path.Combine(root, "i18n"), Path.Combine(root, "src", "data", "i18n") })
        {
            if (Directory.Exists(Path.Combine(candidate, "skills")) && Directory.Exists(Path.Combine(candidate, "npcs")))
            {
                return candidate;
            }
        }

        return null;
    }
}
