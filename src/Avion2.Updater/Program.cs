using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Avion2.Updater;

/// <summary>
/// AVION2-Updater.exe --dir &lt;install folder&gt; --zip &lt;url&gt; [--sums &lt;url&gt;] [--version v1.2.3] [--pid &lt;AVION2 pid&gt;]
/// <list type="number">
/// <item>waits for AVION2 to close,</item>
/// <item>downloads the release zip and checks it against the release's SHA256SUMS.txt,</item>
/// <item>moves every file it replaces to *.old (renaming works even for running exes, including this one),</item>
/// <item>copies the new files in; on any error all *.old files are put back,</item>
/// <item>starts AVION2 again (which deletes the *.old files on its next start).</item>
/// </list>
/// Only talks to github.com (and its download host); nothing else on the PC is touched.
/// </summary>
public static class Program
{
    private const string MainExe = "AVION2.exe";

    public static async Task<int> Main(string[] args)
    {
        Console.Title = "AVION2 Updater";
        string? dir = Arg(args, "dir");
        string? zipUrl = Arg(args, "zip");
        if (dir is null || zipUrl is null || !File.Exists(Path.Combine(dir, MainExe)))
        {
            Console.WriteLine("AVION2-Updater is started by AVION2 when you choose \"Update now\".");
            Console.WriteLine("Usage: AVION2-Updater --dir <AVION2 folder> --zip <release zip url> [--sums <SHA256SUMS url>] [--version v1.2.3] [--pid <pid>]");
            return 2;
        }

        string version = Arg(args, "version") ?? "the new version";
        Console.WriteLine($"AVION2 Updater — updating to {version}");
        string work = Path.Combine(Path.GetTempPath(), $"avion2-update-{Guid.NewGuid():N}");
        try
        {
            if (Arg(args, "pid") is { } pidText && int.TryParse(pidText, out int pid))
            {
                Step("Waiting for AVION2 to close");
                WaitForExit(pid);
            }

            Directory.CreateDirectory(work);
            string zipPath = Path.Combine(work, "release.zip");
            await DownloadAsync(zipUrl, zipPath);

            if (Arg(args, "sums") is { } sumsUrl)
            {
                Step("Verifying the download");
                string sumsPath = Path.Combine(work, "SHA256SUMS.txt");
                await DownloadAsync(sumsUrl, sumsPath, quiet: true);
                Verify(zipPath, Path.GetFileName(new Uri(zipUrl).LocalPath), sumsPath);
            }

            Step("Unpacking");
            string staged = Path.Combine(work, "staged");
            ZipFile.ExtractToDirectory(zipPath, staged);
            string root = FindRoot(staged);

            Step("Replacing files");
            Install(root, dir);

            Step("Starting AVION2");
            Process.Start(new ProcessStartInfo(Path.Combine(dir, MainExe)) { WorkingDirectory = dir, UseShellExecute = true });
            Console.WriteLine("Done.");
            await Task.Delay(1500);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"Update failed: {ex.Message}");
            Console.WriteLine("AVION2 was left as it was. You can download the release manually from");
            Console.WriteLine("https://github.com/void-cx/avion2/releases");
            Console.WriteLine();
            Console.WriteLine("Press Enter to close.");
            Console.ReadLine();
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, "--" + name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static void Step(string text) => Console.WriteLine($"  • {text}…");

    private static void WaitForExit(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                throw new InvalidOperationException("AVION2 is still running. Close it and try again.");
            }
        }
        catch (ArgumentException)
        {
            // already gone
        }
    }

    private static async Task DownloadAsync(string url, string target, bool quiet = false)
    {
        var uri = new Uri(url);
        if (uri.Scheme != Uri.UriSchemeHttps || !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                                                  uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Refusing to download from {uri.Host}: updates only come from GitHub.");
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AVION2-Updater");
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var file = File.Create(target);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        long lastShown = 0;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read));
            done += read;
            if (!quiet && done - lastShown > 2_000_000)
            {
                lastShown = done;
                Console.Write(total is > 0
                    ? $"\r  • Downloading… {done / 1_048_576.0:0.0} / {total.Value / 1_048_576.0:0.0} MB   "
                    : $"\r  • Downloading… {done / 1_048_576.0:0.0} MB   ");
            }
        }

        if (!quiet)
        {
            Console.WriteLine($"\r  • Downloaded {done / 1_048_576.0:0.0} MB                    ");
        }
    }

    /// <summary>Checks the zip against its line in SHA256SUMS.txt ("&lt;hex&gt;  &lt;file name&gt;").</summary>
    internal static void Verify(string zipPath, string zipName, string sumsPath)
    {
        string? expected = File.ReadLines(sumsPath)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2 && parts[^1].TrimStart('*').Equals(zipName, StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[0])
            .FirstOrDefault();
        if (expected is null)
        {
            throw new InvalidOperationException($"{zipName} is not listed in SHA256SUMS.txt.");
        }

        using var stream = File.OpenRead(zipPath);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The download is damaged (checksum mismatch).");
        }
    }

    /// <summary>The folder inside the zip that holds AVION2.exe (zips may wrap everything in one folder).</summary>
    internal static string FindRoot(string staged)
    {
        string? exe = Directory.EnumerateFiles(staged, MainExe, SearchOption.AllDirectories).FirstOrDefault();
        return exe is null
            ? throw new InvalidOperationException("The release does not contain AVION2.exe.")
            : Path.GetDirectoryName(exe)!;
    }

    /// <summary>Moves replaced files to *.old, copies the new ones in, and restores everything if a step fails.</summary>
    internal static void Install(string from, string to)
    {
        var moved = new List<(string Target, string Old)>();
        var added = new List<string>();
        try
        {
            foreach (string source in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(to, Path.GetRelativePath(from, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    string old = target + ".old";
                    File.Delete(old);
                    File.Move(target, old);
                    moved.Add((target, old));
                }
                else
                {
                    added.Add(target);
                }

                File.Copy(source, target);
            }
        }
        catch
        {
            foreach (string file in added)
            {
                TryDelete(file);
            }

            foreach (var (target, old) in moved)
            {
                TryDelete(target);
                File.Move(old, target);
            }

            throw;
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
