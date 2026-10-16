using System.Text.Json;
using System.Text.Json.Serialization;
using Avion2.Analysis;

namespace Avion2.Engine;

/// <summary>
/// Dungeon runs in %LocalAppData%\AVION2\runs.json, with what was learned on the way: which dungeon a map id is
/// (from the bosses killed there) and names the user gave places. Local only. Thread-safe.
/// </summary>
public sealed class RunStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _sync = new();
    private readonly string _path;
    private readonly Data _data;

    public RunStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVION2", "runs.json");
        _data = Load(_path);
    }

    /// <summary>Saved runs, newest first.</summary>
    public IReadOnlyList<DungeonRun> Runs
    {
        get
        {
            lock (_sync)
            {
                return _data.Runs.OrderByDescending(r => r.StartMs).ToList();
            }
        }
    }

    /// <summary>The dungeon a map was found to be (null = not known yet).</summary>
    public string? DungeonFor(uint mapId)
    {
        lock (_sync)
        {
            return _data.Maps.GetValueOrDefault(mapId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>The user's name for a map, if they gave one.</summary>
    public string? NameFor(uint mapId)
    {
        lock (_sync)
        {
            return _data.Names.GetValueOrDefault(mapId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Saves a finished run and learns its first map's dungeon. Short visits without a fight (a look into a
    /// stronghold, a loading screen) are not kept. Returns false when the run was dropped.
    /// </summary>
    public bool Add(DungeonRun run)
    {
        if (run.Fights == 0 && run.Bosses.Count == 0 && run.DurationMs < 60_000)
        {
            return false;
        }

        lock (_sync)
        {
            string key = Key(run.FirstMap);
            if (run.DungeonSlug is { } slug)
            {
                _data.Maps[key] = slug;
            }

            run.CustomName ??= _data.Names.GetValueOrDefault(key);
            _data.Runs.RemoveAll(r => r.Id == run.Id);
            _data.Runs.Add(run);
            Save();
        }

        return true;
    }

    /// <summary>Names a map (null clears it); every run there shows the name.</summary>
    public void Rename(uint mapId, string? name)
    {
        lock (_sync)
        {
            string key = Key(mapId);
            if (string.IsNullOrWhiteSpace(name))
            {
                _data.Names.Remove(key);
            }
            else
            {
                _data.Names[key] = name.Trim();
            }

            foreach (var run in _data.Runs.Where(r => r.FirstMap == mapId))
            {
                run.CustomName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            }

            Save();
        }
    }

    public void Delete(IEnumerable<string> ids)
    {
        var gone = ids.ToHashSet();
        lock (_sync)
        {
            if (_data.Runs.RemoveAll(r => gone.Contains(r.Id)) > 0)
            {
                Save();
            }
        }
    }

    private static string Key(uint mapId) => mapId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_data, Options));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not critical: the run stays in memory for this session
        }
    }

    private static Data Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(path), Options) ?? new();
                foreach (var run in data.Runs)
                {
                    run.Kind = Content.Classify(run.FirstMap, run.ContentCode); // runs saved before a map range was known
                }

                return data;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // start fresh
        }

        return new();
    }

    private sealed class Data
    {
        [JsonPropertyName("runs")]
        public List<DungeonRun> Runs { get; set; } = [];

        /// <summary>Map id → dungeon slug (learned from boss kills).</summary>
        [JsonPropertyName("maps")]
        public Dictionary<string, string> Maps { get; set; } = new();

        /// <summary>Map id → the user's name for it.</summary>
        [JsonPropertyName("names")]
        public Dictionary<string, string> Names { get; set; } = new();
    }
}
