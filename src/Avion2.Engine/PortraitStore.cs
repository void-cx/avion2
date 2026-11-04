using System.Text.Json;
using System.Text.Json.Serialization;

namespace Avion2.Engine;

/// <summary>A character's portrait as the official AION2 site shows it, kept on this PC.</summary>
/// <param name="Url">The site's address of the picture (Discord shows it from there).</param>
/// <param name="File">The saved copy's file name in the store's folder; null when the character has no picture.</param>
/// <param name="FetchedMs">When it was looked up (Unix ms).</param>
public sealed record StoredPortrait(
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("file")] string? File,
    [property: JsonPropertyName("fetchedMs")] long FetchedMs);

/// <summary>
/// The portraits of our characters, in %LocalAppData%\AVION2\portraits: the picture the official AION2 site shows for
/// each (a JPEG, looked up with the character lookup) with an index of where it came from. A portrait is looked up
/// again after a week; when the site didn't answer, after <see cref="RetryAfter"/>. Nothing is asked for while one is
/// fresh, so the pages and the Discord status work from the saved copy.
/// </summary>
public sealed class PortraitStore
{
    /// <summary>A portrait older than this is looked up again (the site lets it be cached for 10 days).</summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(7);

    /// <summary>After the site didn't answer, the next try waits this long.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(30);

    /// <summary>Pictures larger than this are not saved.</summary>
    private const int MaxBytes = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, StoredPortrait> _index;
    private readonly Dictionary<string, DateTimeOffset> _failed = new();
    private readonly HashSet<string> _loading = new();
    private readonly object _sync = new();

    public PortraitStore(string? directory = null, Func<DateTimeOffset>? now = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVION2", "portraits");
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _index = Load(Path.Combine(_directory, "index.json"));
    }

    /// <summary>Raised (on a worker thread) when a character's portrait was saved or changed.</summary>
    public event Action<string, ushort>? Changed;

    /// <summary>The saved portrait of a character; null when it was never looked up.</summary>
    public StoredPortrait? Get(string name, ushort serverId)
    {
        lock (_sync)
        {
            return _index.GetValueOrDefault(Key(name, serverId));
        }
    }

    /// <summary>The saved picture's full path; null when there is none (yet).</summary>
    public string? PicturePath(string name, ushort serverId) =>
        Get(name, serverId) is { File: { } file } && System.IO.File.Exists(Path.Combine(_directory, file))
            ? Path.Combine(_directory, file)
            : null;

    /// <summary>The site's address of the picture, for the Discord status; null when there is none.</summary>
    public string? Url(string name, ushort serverId) => Get(name, serverId) is { File: not null, Url: { } url } ? url : null;

    /// <summary>Whether <see cref="RefreshAsync"/> would ask the site now (no picture yet, or an old one).</summary>
    public bool NeedsRefresh(string name, ushort serverId)
    {
        string key = Key(name, serverId);
        lock (_sync)
        {
            if (_loading.Contains(key) || (_failed.TryGetValue(key, out var failed) && _now() - failed < RetryAfter))
            {
                return false;
            }

            return !_index.TryGetValue(key, out var stored)
                || _now() - DateTimeOffset.FromUnixTimeMilliseconds(stored.FetchedMs) >= RefreshAfter;
        }
    }

    /// <summary>
    /// Looks the character up on the official site and saves its picture, unless the saved one is fresh. Returns
    /// whether the portrait changed. Never throws: a site that doesn't answer leaves the saved picture as it is.
    /// </summary>
    /// <param name="lookUp">The character lookup: the picture's address, null when the character has none; throws
    /// or returns <c>(false, null)</c> when the site didn't answer.</param>
    /// <param name="download">Fetches the picture's bytes.</param>
    public async Task<bool> RefreshAsync(string name, ushort serverId,
        Func<string, ushort, Task<(bool Answered, string? Url)>> lookUp, Func<string, Task<byte[]>> download)
    {
        string key = Key(name, serverId);
        if (!NeedsRefresh(name, serverId))
        {
            return false;
        }

        lock (_sync)
        {
            if (!_loading.Add(key))
            {
                return false;
            }
        }

        try
        {
            var (answered, url) = await lookUp(name, serverId).ConfigureAwait(false);
            if (!answered)
            {
                Fail(key);
                return false;
            }

            string? file = null;
            if (url is not null)
            {
                byte[] bytes = await download(url).ConfigureAwait(false);
                if (!IsPicture(bytes))
                {
                    Fail(key);
                    return false;
                }

                file = FileName(name, serverId, bytes);
                Directory.CreateDirectory(_directory);
                await File.WriteAllBytesAsync(Path.Combine(_directory, file), bytes).ConfigureAwait(false);
            }

            StoredPortrait? old;
            lock (_sync)
            {
                old = _index.GetValueOrDefault(key);
                _index[key] = new StoredPortrait(url, file, _now().ToUnixTimeMilliseconds());
                _failed.Remove(key);
                Save();
            }

            if (old?.File is { } oldFile && oldFile != file)
            {
                TryDelete(Path.Combine(_directory, oldFile));
            }

            bool changed = old?.File != file || old?.Url != url;
            if (changed)
            {
                Changed?.Invoke(name, serverId);
            }

            return changed;
        }
        catch (Exception)
        {
            Fail(key);
            return false;
        }
        finally
        {
            lock (_sync)
            {
                _loading.Remove(key);
            }
        }
    }

    /// <summary>A JPEG or PNG of a sensible size (what WPF and Discord both show).</summary>
    public static bool IsPicture(byte[] bytes) =>
        bytes.Length is > 8 and <= MaxBytes
        && ((bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            || (bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G'));

    private static string Key(string name, ushort serverId) => $"{serverId}/{name}";

    /// <summary>"1321-3f2a….jpg": the server and a hash of the name (no names in file names), new per picture.</summary>
    private static string FileName(string name, ushort serverId, byte[] bytes)
    {
        string nameHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(name)))[..12].ToLowerInvariant();
        string content = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..8].ToLowerInvariant();
        string extension = bytes[0] == 0x89 ? ".png" : ".jpg";
        return $"{serverId}-{nameHash}-{content}{extension}";
    }

    private void Fail(string key)
    {
        lock (_sync)
        {
            _failed[key] = _now();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, "index.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(_index, Options));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (IOException)
        {
            // kept in memory; saved with the next portrait
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static Dictionary<string, StoredPortrait> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, StoredPortrait>>(File.ReadAllText(path), Options) ?? new()
                : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
