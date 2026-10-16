using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Avion2.Updater;

/// <summary>
/// AVION2-Updater.exe --dir &lt;install folder&gt; --staged &lt;unpacked release&gt; [--version v1.2.3] [--pid &lt;AVION2 pid&gt;]
/// (AVION2 has downloaded, checked and unpacked the release in its update window), or the older
/// --zip &lt;url&gt; [--sums &lt;url&gt;] form (AVION2 up to v0.4.0 hands over before downloading):
/// <list type="number">
/// <item>waits for AVION2 to close,</item>
/// <item>with --zip: downloads the release zip and checks it against the release's SHA256SUMS.txt,</item>
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
        string? dir = Arg(args, "dir");
        string? zipUrl = Arg(args, "zip");
        string? stagedArg = Arg(args, "staged");
        if (dir is null || zipUrl is null && stagedArg is null || !File.Exists(Path.Combine(dir, MainExe)))
        {
            Message("AVION2-Updater is started by AVION2 when you choose \"Update now\" on its home page.", warning: false);
            return 2;
        }

        // --staged: AVION2 unpacked the release into <temp>\avion2-update-…\staged; that temp folder goes when done
        string work = stagedArg is not null && Path.GetDirectoryName(Path.GetFullPath(stagedArg)) is { } parent &&
                      Path.GetFileName(parent).StartsWith("avion2-update-", StringComparison.Ordinal)
            ? parent
            : Path.Combine(Path.GetTempPath(), $"avion2-update-{Guid.NewGuid():N}");
        try
        {
            if (Arg(args, "pid") is { } pidText && int.TryParse(pidText, out int pid))
            {
                WaitForExit(pid);
            }

            string root;
            if (stagedArg is not null)
            {
                root = FindRoot(stagedArg);
            }
            else
            {
                Directory.CreateDirectory(work);
                string zipPath = Path.Combine(work, "release.zip");
                await DownloadAsync(zipUrl!, zipPath);
                if (Arg(args, "sums") is { } sumsUrl)
                {
                    string sumsPath = Path.Combine(work, "SHA256SUMS.txt");
                    await DownloadAsync(sumsUrl, sumsPath);
                    Verify(zipPath, Path.GetFileName(new Uri(zipUrl!).LocalPath), sumsPath);
                }

                string staged = Path.Combine(work, "staged");
                ZipFile.ExtractToDirectory(zipPath, staged);
                root = FindRoot(staged);
            }

            Install(root, dir);
            Start(dir);
            return 0;
        }
        catch (Exception ex)
        {
            Message($"The update to {Arg(args, "version") ?? "the new version"} failed: {ex.Message}\n\n" +
                    "AVION2 was left as it was. You can download the release from https://github.com/void-cx/avion2/releases",
                    warning: true);
            Start(dir);
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

    private static void Start(string dir)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(dir, MainExe)) { WorkingDirectory = dir, UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // the user starts it
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    /// <summary>A plain Windows message box (the updater has no window of its own).</summary>
    private static void Message(string text, bool warning) =>
        MessageBoxW(IntPtr.Zero, text, "AVION2 update", warning ? 0x30u /* MB_ICONWARNING */ : 0x40u /* MB_ICONINFORMATION */);

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, "--" + name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

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

    private static async Task DownloadAsync(string url, string target)
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
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var file = File.Create(target);
        await source.CopyToAsync(file);
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
