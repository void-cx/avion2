using System.IO.Compression;
using System.Security.Cryptography;

namespace Avion2.Engine;

/// <summary>
/// The first half of an update, done inside AVION2 so it can show progress: download the release zip from GitHub,
/// check it against the release's SHA256SUMS.txt and unpack it. AVION2-Updater.exe then only swaps the files.
/// </summary>
public static class UpdateDownload
{
    public enum Stage
    {
        Downloading,
        Verifying,
        Unpacking,
    }

    public sealed record Progress(Stage Stage, long Done, long? Total);

    /// <summary>Downloads, verifies and unpacks a release; returns the folder that holds the new AVION2.exe.</summary>
    /// <remarks>Everything lands in a fresh temp folder; the updater deletes it when it is done.</remarks>
    public static async Task<string> RunAsync(ReleaseInfo release, IProgress<Progress>? progress = null, CancellationToken cancel = default)
    {
        string work = Path.Combine(Path.GetTempPath(), $"avion2-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            string zip = Path.Combine(work, "release.zip");
            await DownloadAsync(release.ZipUrl, zip, progress, cancel);
            if (release.ChecksumsUrl is { } sumsUrl)
            {
                progress?.Report(new Progress(Stage.Verifying, 0, null));
                string sums = Path.Combine(work, "SHA256SUMS.txt");
                await DownloadAsync(sumsUrl, sums, null, cancel);
                Verify(zip, Path.GetFileName(new Uri(release.ZipUrl).LocalPath), sums);
            }

            progress?.Report(new Progress(Stage.Unpacking, 0, null));
            string staged = Path.Combine(work, "staged");
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, staged), cancel);
            File.Delete(zip);
            string? exe = Directory.EnumerateFiles(staged, "AVION2.exe", SearchOption.AllDirectories).FirstOrDefault();
            return exe is null
                ? throw new InvalidOperationException("The release does not contain AVION2.exe.")
                : Path.GetDirectoryName(exe)!;
        }
        catch
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // temp folder; Windows cleans it eventually
            }

            throw;
        }
    }

    private static async Task DownloadAsync(string url, string target, IProgress<Progress>? progress, CancellationToken cancel)
    {
        var uri = new Uri(url);
        if (uri.Scheme != Uri.UriSchemeHttps || !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                                                  uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Refusing to download from {uri.Host}: updates only come from GitHub.");
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AVION2");
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancel);
        await using var file = File.Create(target);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancel)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), cancel);
            done += read;
            progress?.Report(new Progress(Stage.Downloading, done, total));
        }
    }

    /// <summary>Checks the zip against its line in SHA256SUMS.txt ("&lt;hex&gt;  &lt;file name&gt;").</summary>
    public static void Verify(string zipPath, string zipName, string sumsPath)
    {
        string? expected = File.ReadLines(sumsPath)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2 && parts[^1].TrimStart('*').Equals(zipName, StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[0])
            .FirstOrDefault() ?? throw new InvalidOperationException($"{zipName} is not listed in SHA256SUMS.txt.");
        using var stream = File.OpenRead(zipPath);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The download is damaged (checksum mismatch).");
        }
    }
}
