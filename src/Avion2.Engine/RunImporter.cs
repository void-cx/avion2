using Avion2.Analysis;
using Avion2.Capture;

namespace Avion2.Engine;

/// <summary>Finds dungeon runs in saved recordings (.pcap) and adds them to the saved runs.</summary>
public static class RunImporter
{
    public sealed record Result(int Files, int Found, int Added);

    /// <summary>
    /// Reads every recording (oldest first) and saves the runs found in them. A run that is already saved (same start
    /// and map) is replaced, not doubled. Each file is one capture: a run does not go on into the next file.
    /// </summary>
    public static Result Import(IEnumerable<string> files, GameData data, RunStore store,
        Action<int, int>? progress = null, CancellationToken cancel = default)
    {
        var paths = files.OrderBy(f => File.GetLastWriteTimeUtc(f)).ToList();
        var known = store.Runs.Select(r => r.Id).ToHashSet();
        var session = new GameSession(data);
        session.Runs.Catalog = BundledGameData.Dungeons;
        session.Runs.EnglishName = BundledGameData.EnglishNpcName;
        session.Runs.KnownMap = store.DungeonFor;
        int found = 0, added = 0;
        session.Runs.RunEnded += run =>
        {
            if (store.Add(run))
            {
                found++;
                if (known.Add(run.Id))
                {
                    added++;
                }
            }
        };

        var pipeline = new GameCapturePipeline(session.Apply);
        for (int i = 0; i < paths.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Invoke(i, paths.Count);
            long lastTs = 0;
            try
            {
                foreach (var frame in PcapFile.Read(paths[i]))
                {
                    pipeline.Process(frame);
                    session.Tracker.Tick(frame.TimestampMs);
                    lastTs = frame.TimestampMs;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException)
            {
                // a cut-off recording (the app was closed while writing): keep what was read
            }

            session.Tracker.Tick(lastTs + 60_000);
            session.Runs.Finish(lastTs);
        }

        progress?.Invoke(paths.Count, paths.Count);
        return new Result(paths.Count, found, added);
    }
}
