namespace Avion2.Engine;

/// <summary>Session recordings (.pcap, several MB each) in %LocalAppData%\AVION2\captures.</summary>
public static class Recordings
{
    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVION2", "captures");

    /// <summary>Every recording's path (except <paramref name="inUse"/>, the one being written).</summary>
    public static IReadOnlyList<string> Paths(string? inUse = null) => Files()
        .Where(f => inUse is null || !string.Equals(Path.GetFullPath(inUse), f.FullName, StringComparison.OrdinalIgnoreCase))
        .Select(f => f.FullName)
        .ToList();

    /// <summary>The pictures of the screen recorded with <paramref name="recording"/> (Developer setting).</summary>
    public static string FramesDirectory(string recording) => Path.ChangeExtension(recording, ".frames");

    /// <summary>How many recordings there are and their size, screen pictures included.</summary>
    public static (int Files, long Bytes) Usage()
    {
        var files = Files();
        long frames = Frames().Sum(d => d.EnumerateFiles().Sum(f => f.Length));
        return (files.Count, files.Sum(f => f.Length) + frames);
    }

    /// <summary>
    /// Deletes recordings last written before <paramref name="cutoff"/> (all of them when null), never
    /// <paramref name="inUse"/> (the one being recorded). Returns how many were deleted.
    /// </summary>
    public static int Delete(DateTime? cutoff = null, string? inUse = null)
    {
        int deleted = 0;
        foreach (var file in Files())
        {
            if (cutoff is { } before && file.LastWriteTimeUtc >= before.ToUniversalTime() ||
                inUse is not null && string.Equals(Path.GetFullPath(inUse), file.FullName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                file.Delete();
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // in use; next time
            }
        }

        string? inUseFrames = inUse is null ? null : Path.GetFullPath(FramesDirectory(inUse));
        foreach (var folder in Frames())
        {
            if (cutoff is { } before && folder.LastWriteTimeUtc >= before.ToUniversalTime() ||
                string.Equals(inUseFrames, folder.FullName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                folder.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // in use; next time
            }
        }

        return deleted;
    }

    private static List<FileInfo> Files() =>
        System.IO.Directory.Exists(Directory) ? new DirectoryInfo(Directory).GetFiles("*.pcap").ToList() : [];

    private static List<DirectoryInfo> Frames() =>
        System.IO.Directory.Exists(Directory) ? new DirectoryInfo(Directory).GetDirectories("*.frames").ToList() : [];
}
