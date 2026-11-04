using System.Text.Json;
using Avion2.Analysis;

namespace Avion2.Engine;

/// <summary>
/// Which picture a boss shows: the game's own portraits (assets/gamedata/bosses, one per boss model, see
/// tools/build_boss_images.py), looked up by NPC code, by a Timers row (a world boss row or a field boss respawn) or
/// by a map marker's name. A PNG in the override folder named after the NPC code (2100003.png) or the world boss row
/// (wb-executors.png) replaces the built-in picture, or gives one to a boss that has none. A boss without a picture
/// gets null, never a stand-in.
/// </summary>
public sealed class BossPortraits
{
    private const string FieldBossPrefix = "fieldboss:";
    private const string WorldBossPrefix = "wb-";

    private readonly IReadOnlyDictionary<uint, string> _npcs;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _events;
    private readonly IReadOnlyDictionary<string, string> _names;
    private readonly Func<string, Stream?> _bundled;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> _overrides = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="npcs">NPC code → portrait (file name without .png).</param>
    /// <param name="events">World boss row (GameSchedule key) → the portraits of its bosses, in order.</param>
    /// <param name="names">English map marker name → portrait.</param>
    /// <param name="bundled">Opens a built-in portrait by name, or null.</param>
    /// <param name="overrideDirectory">Folder with replacement PNGs, or null.</param>
    public BossPortraits(IReadOnlyDictionary<uint, string> npcs, IReadOnlyDictionary<string, IReadOnlyList<string>> events,
        IReadOnlyDictionary<string, string> names, Func<string, Stream?> bundled, string? overrideDirectory)
    {
        _npcs = npcs;
        _events = events;
        _names = names;
        _bundled = bundled;
        OverrideDirectory = overrideDirectory;
    }

    /// <summary>Per-user folder with replacement pictures (%AppData%\AVION2\bosses).</summary>
    public static string UserDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AVION2", "bosses");

    private static readonly Lazy<BossPortraits> BuiltIn = new(() =>
        Parse(BundledGameData.Read("bosses/bosses.json"), BundledGameData.BossPortrait, UserDirectory));

    /// <summary>The built-in table with the user's override folder.</summary>
    public static BossPortraits Bundled => BuiltIn.Value;

    public string? OverrideDirectory { get; }

    /// <summary>How many NPC codes have a built-in portrait.</summary>
    public int NpcCount => _npcs.Count;

    /// <summary>Reads bosses.json (see tools/build_boss_images.py); a missing or broken table knows no portraits.</summary>
    public static BossPortraits Parse(string? json, Func<string, Stream?> bundled, string? overrideDirectory)
    {
        var npcs = new Dictionary<uint, string>();
        var events = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (json is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("npcs", out var n))
                {
                    foreach (var p in n.EnumerateObject())
                    {
                        if (uint.TryParse(p.Name, out uint code) && p.Value.GetString() is { Length: > 0 } file)
                        {
                            npcs[code] = file;
                        }
                    }
                }

                if (root.TryGetProperty("events", out var e))
                {
                    foreach (var p in e.EnumerateObject())
                    {
                        events[p.Name] = p.Value.EnumerateArray().Select(v => v.GetString()).OfType<string>().ToList();
                    }
                }

                if (root.TryGetProperty("names", out var m))
                {
                    foreach (var p in m.EnumerateObject())
                    {
                        if (p.Value.GetString() is { Length: > 0 } file)
                        {
                            names[p.Name] = file;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // no portraits
            }
        }

        return new BossPortraits(npcs, events, names, bundled, overrideDirectory);
    }

    /// <summary>The built-in portrait name of an NPC, or null.</summary>
    public string? PortraitOf(uint npcCode) => _npcs.GetValueOrDefault(npcCode);

    /// <summary>The built-in portraits of a world boss row ("wb-executors": three), or none.</summary>
    public IReadOnlyList<string> PortraitsOf(string eventKey) => _events.GetValueOrDefault(eventKey) ?? [];

    /// <summary>Where an NPC's picture is: an override file path, a built-in "bosses/&lt;name&gt;.png", or null.</summary>
    public string? Locate(uint npcCode) =>
        Override(npcCode.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? BuiltInPath(PortraitOf(npcCode));

    /// <summary>
    /// The picture of a Timers row or notice: "wb-…" (a world boss row, its first boss) or "fieldboss:&lt;spawn code&gt;"
    /// (a field boss respawn, through the field boss table of <paramref name="data"/>); null for anything else (rifts,
    /// events).
    /// </summary>
    public string? LocateTimer(string key, GameData data)
    {
        if (key.StartsWith(FieldBossPrefix, StringComparison.Ordinal))
        {
            return uint.TryParse(key.AsSpan(FieldBossPrefix.Length), out uint spawn) ? LocateSpawn(spawn, data) : null;
        }

        return key.StartsWith(WorldBossPrefix, StringComparison.Ordinal) ? Override(key) ?? BuiltInPath(PortraitsOf(key).FirstOrDefault()) : null;
    }

    /// <summary>Every picture of a world boss row, one per boss; an override for the row replaces them all.</summary>
    public IReadOnlyList<string> LocateWorldBoss(string key) =>
        !key.StartsWith(WorldBossPrefix, StringComparison.Ordinal) ? []
        : Override(key) is { } custom ? [custom]
        : PortraitsOf(key).Select(p => BuiltInPath(p)!).ToList();

    /// <summary>The picture of a field boss by its spawn code (the respawn table's id).</summary>
    public string? LocateSpawn(uint spawnCode, GameData data) =>
        data.FieldBosses.TryGetValue(spawnCode, out uint npc) ? Locate(npc) : null;

    /// <summary>The picture of a boss marker on the bundled maps, by its (English) name; built in only.</summary>
    public string? LocateName(string markerName) => BuiltInPath(_names.GetValueOrDefault(markerName));

    /// <summary>Opens a location from <see cref="Locate"/> and friends; null when it cannot be read.</summary>
    public Stream? Open(string location)
    {
        try
        {
            return Path.IsPathRooted(location) ? File.OpenRead(location) : _bundled(location);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? BuiltInPath(string? portrait) => portrait is null ? null : $"bosses/{portrait}.png";

    /// <summary>The override file for a name, looked up once (the Timers page asks twice a second; new files count after a restart).</summary>
    private string? Override(string name)
    {
        if (OverrideDirectory is null || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        return _overrides.GetOrAdd(name, n =>
        {
            string path = Path.Combine(OverrideDirectory, n + ".png");
            return File.Exists(path) ? path : null;
        });
    }
}
