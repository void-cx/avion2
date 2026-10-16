using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avion2.Analysis;

namespace Avion2.Engine;

/// <summary>A finished encounter on disk.</summary>
public sealed record HistoryEntry(string Path, MeterSnapshot Snapshot);

/// <summary>
/// Finished encounters as JSON files in %LocalAppData%\AVION2\history (one file per fight, newest kept).
/// They contain other players' names and damage: local only, never uploaded.
/// </summary>
public sealed class HistoryStore
{
    public const int MaxFiles = 300;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public HistoryStore(string? directory = null)
    {
        Directory = directory ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVION2", "history");
    }

    public string Directory { get; }

    public HistoryEntry? Save(MeterSnapshot snapshot)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var started = DateTimeOffset.FromUnixTimeMilliseconds(snapshot.StartedAtMs).ToLocalTime();
            string name = $"{started:yyyyMMdd-HHmmss}-{Sanitize(snapshot.Title)}.json";
            string path = System.IO.Path.Combine(Directory, name);
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot, Options));
            Prune();
            return new HistoryEntry(path, snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The trace file of a saved fight (timeline, buffs, positions): gzipped JSON next to the snapshot.</summary>
    public static string TracePath(string snapshotPath) => System.IO.Path.ChangeExtension(snapshotPath, ".trace");

    public void SaveTrace(HistoryEntry entry, FightTrace trace)
    {
        if (entry.Path.Length == 0)
        {
            return;
        }

        try
        {
            using var file = File.Create(TracePath(entry.Path));
            using var zip = new GZipStream(file, CompressionLevel.Optimal);
            JsonSerializer.Serialize(zip, trace, TraceOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the fight itself is saved; it just has no replay
        }
    }

    /// <summary>The trace of a saved fight; null for fights saved before traces existed.</summary>
    public FightTrace? LoadTrace(HistoryEntry entry)
    {
        string path = entry.Path.Length == 0 ? string.Empty : TracePath(entry.Path);
        if (path.Length == 0 || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var file = File.OpenRead(path);
            using var zip = new GZipStream(file, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<FightTrace>(zip, TraceOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions TraceOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public bool Delete(HistoryEntry entry)
    {
        try
        {
            if (entry.Path.Length > 0 && File.Exists(entry.Path))
            {
                File.Delete(entry.Path);
                if (File.Exists(TracePath(entry.Path)))
                {
                    File.Delete(TracePath(entry.Path));
                }

                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // locked; leave it
        }

        return false;
    }

    /// <summary>A fight's boss or target without the "(+N)" extra-targets suffix, for grouping the same boss.</summary>
    public static string BossOf(string title) => System.Text.RegularExpressions.Regex.Replace(title, @"\s*\(\+\d+\)$", string.Empty);

    /// <summary>
    /// Our best fight per boss: the highest own DPS among fights of at least <paramref name="minSeconds"/> where we
    /// dealt damage (generic "Combat" fights left out), best first.
    /// </summary>
    public static List<HistoryEntry> BestPerBoss(IEnumerable<HistoryEntry> fights, double minSeconds = 20) => fights
        .Where(f => f.Snapshot.Self is { Dps: > 0 } && f.Snapshot.DurationSeconds >= minSeconds && BossOf(f.Snapshot.Title) != "Combat")
        .GroupBy(f => BossOf(f.Snapshot.Title))
        .Select(g => g.MaxBy(f => f.Snapshot.Self!.Dps)!)
        .OrderByDescending(f => f.Snapshot.Self!.Dps)
        .ToList();

    /// <summary>Saved fights on disk (all of them, not only the ones loaded) and the bytes they and their replays take.</summary>
    public (int Fights, long Bytes) Usage()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return (0, 0);
        }

        var files = new DirectoryInfo(Directory).GetFiles("*.*").Where(f => f.Extension is ".json" or ".trace").ToList();
        return (files.Count(f => f.Extension == ".json"), files.Sum(f => f.Length));
    }

    /// <summary>Deletes every saved fight (and its replay) last written before <paramref name="cutoff"/>; returns how many.</summary>
    public int DeleteOlderThan(DateTime cutoff)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return 0;
        }

        int deleted = 0;
        foreach (var file in new DirectoryInfo(Directory).GetFiles("*.json").Where(f => f.LastWriteTimeUtc < cutoff.ToUniversalTime()))
        {
            try
            {
                file.Delete();
                if (File.Exists(TracePath(file.FullName)))
                {
                    File.Delete(TracePath(file.FullName));
                }

                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // locked; next time
            }
        }

        return deleted;
    }

    /// <summary>Most recent encounters first.</summary>
    public List<HistoryEntry> Load(int max = 50)
    {
        var entries = new List<HistoryEntry>();
        if (!System.IO.Directory.Exists(Directory))
        {
            return entries;
        }

        foreach (string path in System.IO.Directory.GetFiles(Directory, "*.json").OrderByDescending(p => p, StringComparer.Ordinal).Take(max))
        {
            try
            {
                if (JsonSerializer.Deserialize<MeterSnapshot>(File.ReadAllText(path), Options) is { Rows: not null } snapshot) // other JSON in the folder is not a fight
                {
                    entries.Add(new HistoryEntry(path, snapshot));
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException)
            {
                // skip unreadable files (older formats)
            }
        }

        return entries;
    }

    /// <summary>Plain-text summary, e.g. for pasting into a chat.</summary>
    public static string Summarize(MeterSnapshot s, int maxRows = 10)
    {
        var sb = new StringBuilder();
        var started = DateTimeOffset.FromUnixTimeMilliseconds(s.StartedAtMs).ToLocalTime();
        sb.Append(CultureInfo.InvariantCulture, $"AVION2 — {s.Title} · {Duration(s.DurationSeconds)} · {Compact(s.TotalDps)}/s");
        sb.AppendLine(CultureInfo.InvariantCulture, $" · {started:yyyy-MM-dd HH:mm}");
        int rank = 0;
        foreach (var row in s.Rows.Take(maxRows))
        {
            rank++;
            string cls = row.Class == Protocol.GameClass.Unknown ? string.Empty : $" ({row.Class})";
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{rank}. {row.Name}{cls}  {Compact(row.Dps)}/s  {Compact(row.Total)}  {row.Share * 100:0.0}%  crit {row.CritRate * 100:0}%");
        }

        return sb.ToString().TrimEnd();
    }

    public static string Compact(double value) => value switch
    {
        >= 1_000_000_000 => (value / 1_000_000_000).ToString("0.00", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (value / 1_000_000).ToString("0.00", CultureInfo.InvariantCulture) + "M",
        >= 10_000 => (value / 1_000).ToString("0.0", CultureInfo.InvariantCulture) + "K",
        _ => value.ToString("0", CultureInfo.InvariantCulture),
    };

    public static string Duration(double seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);

    private void Prune()
    {
        var files = System.IO.Directory.GetFiles(Directory, "*.json").OrderByDescending(p => p, StringComparer.Ordinal).Skip(MaxFiles);
        foreach (string old in files)
        {
            File.Delete(old);
            if (File.Exists(TracePath(old)))
            {
                File.Delete(TracePath(old));
            }
        }
    }

    private static string Sanitize(string title)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        string clean = new(title.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray());
        return clean.Length > 40 ? clean[..40] : clean;
    }
}
