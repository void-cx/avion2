using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExtractGameData;

/// <summary>
/// Settings from environment variables, else from the untracked <c>tools/extract_gamedata/local.json</c>
/// (<c>{"AION2_GAME_DIR": "...", ...}</c>). Keys and constants are only ever read from local files or the
/// environment, never from the repository.
/// </summary>
public sealed partial class ToolConfig
{
    private readonly Dictionary<string, string> _local = new(StringComparer.Ordinal);

    private ToolConfig(string repoRoot)
    {
        RepoRoot = repoRoot;
        string local = Path.Combine(ToolDir, "local.json");
        if (File.Exists(local))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(local));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                {
                    _local[p.Name] = p.Value.GetString()!;
                }
            }
        }
    }

    /// <summary>The AVION2 checkout (the folder with Avion2.sln), found from the current folder or the tool's.</summary>
    public string RepoRoot { get; }

    public string ToolDir => Path.Combine(RepoRoot, "tools", "extract_gamedata");

    public string AssetsDir => Path.Combine(RepoRoot, "assets", "gamedata");

    public string SchemaPath => Get("AION2_SCHEMA") ?? Path.Combine(ToolDir, "schema.json");

    /// <summary>Scratch folder for staged output and dumps (<c>%LOCALAPPDATA%\AVION2-datamine</c>).</summary>
    public string WorkDir => Get("AVION2_DATAMINE_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVION2-datamine");

    public static ToolConfig Load()
    {
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Avion2.sln")))
                {
                    return new ToolConfig(dir.FullName);
                }
            }
        }

        throw new InvalidOperationException("run this from inside the AVION2 checkout (no Avion2.sln found)");
    }

    public string? Get(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : _local.GetValueOrDefault(name) is { Length: > 0 } v ? v : null;

    public string Require(string name) =>
        Get(name) ?? throw new InvalidOperationException($"{name} is not set (environment or tools/extract_gamedata/local.json)");

    /// <summary>The pak AES key as "0x…": <c>AION2_PAK_KEY</c>, or the first 256-bit hex value in <c>AION2_PAK_KEY_FILE</c>.</summary>
    public string PakKey()
    {
        string text = Get("AION2_PAK_KEY") ?? File.ReadAllText(Require("AION2_PAK_KEY_FILE"));
        var match = HexKey().Match(text);
        return match.Success ? "0x" + match.Groups[1].Value : throw new InvalidOperationException("no 256-bit pak key found");
    }

    [GeneratedRegex("(?:0x)?([0-9A-Fa-f]{64})")]
    private static partial Regex HexKey();
}
