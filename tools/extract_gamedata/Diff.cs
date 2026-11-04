using System.Text.Json.Nodes;

namespace ExtractGameData;

/// <summary>A file to write under assets/gamedata (path relative to it, "/" separated).</summary>
public sealed record AssetFile(string Path, string Content);

/// <summary>What changed per asset compared with the files in assets/gamedata, printed after a build.</summary>
public sealed class Report
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines => _lines;

    public void Add(string asset, string line) => _lines.Add($"{asset}: {line}");

    /// <summary>
    /// Compares two id → value maps: how many there were and are, ids added and removed, values changed (with a few
    /// examples of each).
    /// </summary>
    public void Compare<T>(string asset, IReadOnlyDictionary<string, T>? before, IReadOnlyDictionary<string, T> after,
        Func<T, string> show, IEqualityComparer<T>? comparer = null, int examples = 5)
    {
        comparer ??= EqualityComparer<T>.Default;
        if (before is null)
        {
            Add(asset, $"new file, {after.Count} entries");
            return;
        }

        var added = after.Keys.Where(k => !before.ContainsKey(k)).ToList();
        var removed = before.Keys.Where(k => !after.ContainsKey(k)).ToList();
        var changed = after.Keys.Where(k => before.TryGetValue(k, out var b) && !comparer.Equals(b, after[k])).ToList();
        int same = after.Count - added.Count - changed.Count;
        Add(asset, $"{before.Count} → {after.Count} entries; {same} unchanged, {changed.Count} changed, {added.Count} added, {removed.Count} removed");
        if (changed.Count > 0)
        {
            Add(asset, "  changed: " + string.Join("; ", changed.Take(examples).Select(k => $"{k} {show(before[k])} → {show(after[k])}")));
        }

        if (added.Count > 0)
        {
            Add(asset, "  added: " + string.Join("; ", added.Take(examples).Select(k => $"{k} {show(after[k])}")));
        }

        if (removed.Count > 0)
        {
            Add(asset, "  removed: " + string.Join("; ", removed.Take(examples).Select(k => $"{k} {show(before[k])}")));
        }
    }

    /// <summary>An object's properties as a key → JSON text map (for comparing whole entries).</summary>
    public static Dictionary<string, string>? Entries(JsonNode? node) =>
        node is JsonObject o ? o.ToDictionary(p => p.Key, p => p.Value is null ? "null" : Json.Compact(p.Value), StringComparer.Ordinal) : null;
}
