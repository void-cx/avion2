using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>
/// The game blocks gathering for a while when you abort too often ("Essence Extraction has been restricted due to
/// repetitive behavior"): abnormal 10020 on us, 60 s or more (120 s when blocked again soon after). Recorded: three
/// blocks, each on the fourth abort within 5 minutes and none on a third; aborting a gathering that would succeed
/// counts too, and a block does not reset the count. Counts our recent aborts, for a warning, and tracks the block.
/// </summary>
public sealed class GatherLimit
{
    public const uint BlockAbnormal = 10020;

    /// <summary>Aborts this recent count. Recorded: an abort 4 min 34 s back still counted, one 5 min 5 s back did not.</summary>
    public const long AbortWindowMs = 5 * 60_000;

    /// <summary>With this many recent aborts, the next one gets gathering blocked.</summary>
    public const int WarnAt = 3;

    private readonly Queue<long> _aborts = new();
    private long _blockedUntil;
    private uint? _instance;

    public void Aborted(long nowMs) => _aborts.Enqueue(nowMs);

    /// <summary>An abnormal on us: the block starts.</summary>
    public void Apply(BuffEvent buff)
    {
        if (buff.AbnormalId != BlockAbnormal || buff.DurationMs is not { } length)
        {
            return;
        }

        _blockedUntil = buff.TimestampMs + length;
        _instance = buff.Instance;
    }

    public void Apply(BuffRemovedEvent removed)
    {
        if (_instance is { } instance && removed.Instances.Contains(instance))
        {
            _blockedUntil = 0;
            _instance = null;
        }
    }

    /// <summary>How long gathering stays blocked; null when it isn't.</summary>
    public long? BlockedMs(long nowMs) => _blockedUntil > nowMs ? _blockedUntil - nowMs : null;

    public int RecentAborts(long nowMs)
    {
        while (_aborts.Count > 0 && nowMs - _aborts.Peek() > AbortWindowMs)
        {
            _aborts.Dequeue();
        }

        return _aborts.Count;
    }
}
