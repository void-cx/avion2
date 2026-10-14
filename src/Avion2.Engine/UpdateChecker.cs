using System.Reflection;
using System.Text.Json;

namespace Avion2.Engine;

/// <param name="ZipUrl">The portable build (<c>AVION2-v1.2.3-win-x64.zip</c>).</param>
/// <param name="ChecksumsUrl"><c>SHA256SUMS.txt</c> of the release, used by the updater to verify the zip.</param>
public sealed record ReleaseInfo(Version Version, string Tag, string PageUrl, string? Notes, string ZipUrl, string? ChecksumsUrl, long ZipSize);

/// <summary>
/// Looks for a newer release of AVION2 on GitHub (void-cx/avion2). One small request at start; nothing is
/// downloaded until the user chooses to update (then AVION2-Updater.exe does the work). Dev builds never update.
/// </summary>
public static class UpdateChecker
{
    public const string Repository = "void-cx/avion2";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";
    private const string LatestApi = "https://api.github.com/repos/" + Repository + "/releases/latest";

    /// <summary>This build's version ("1.2.3", or "0.0.0-dev" for local builds).</summary>
    public static string CurrentVersion
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(UpdateChecker).Assembly;
            string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return (informational ?? assembly.GetName().Version?.ToString() ?? "0.0.0-dev").Split('+')[0];
        }
    }

    /// <summary>Local and CI-less builds (versions with a pre-release suffix) don't look for updates.</summary>
    public static bool IsDevBuild(string version) => version.Contains('-', StringComparison.Ordinal) || Parse(version) is null;

    /// <summary>"v1.2.3" / "1.2.3" → 1.2.3; pre-releases ("1.3.0-beta") and junk → null.</summary>
    public static Version? Parse(string text)
    {
        string trimmed = text.Trim().TrimStart('v', 'V').Split('+')[0];
        if (trimmed.Contains('-', StringComparison.Ordinal) || !Version.TryParse(trimmed, out var version))
        {
            return null;
        }

        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }

    public static bool IsNewer(ReleaseInfo release, string current) =>
        !IsDevBuild(current) && Parse(current) is { } mine && release.Version > mine;

    /// <summary>Reads a GitHub "release" JSON object; null for drafts, pre-releases or releases without the portable zip.</summary>
    public static ReleaseInfo? FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() ||
            root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
        {
            return null;
        }

        string tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (Parse(tag) is not { } version)
        {
            return null;
        }

        string? zip = null, sums = null;
        long size = 0;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? string.Empty;
            string url = asset.GetProperty("browser_download_url").GetString() ?? string.Empty;
            if (name.StartsWith("AVION2-", StringComparison.OrdinalIgnoreCase) && name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
            {
                zip = url;
                size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            }
            else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
            {
                sums = url;
            }
        }

        if (zip is null)
        {
            return null;
        }

        string page = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? ReleasesPage : ReleasesPage;
        string? notes = root.TryGetProperty("body", out var body) ? body.GetString() : null;
        return new ReleaseInfo(version, tag, page, notes, zip, sums, size);
    }

    /// <summary>The latest release, or null (none, offline, rate-limited: never an error for the user).</summary>
    public static async Task<ReleaseInfo?> LatestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"AVION2/{CurrentVersion}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.GetAsync(LatestApi, cancellationToken);
            return response.IsSuccessStatusCode ? FromJson(await response.Content.ReadAsStringAsync(cancellationToken)) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }
}
