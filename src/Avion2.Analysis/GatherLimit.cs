using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>
/// The game blocks gathering for a while when you abort too often ("Essence Extraction has been restricted due to
/// repetitive behavior"): abnormal 10020 on us for 60 s. Seen once, after the fourth abort within about three minutes
/// (the third had not triggered it). Counts our recent aborts, for a warning, and tracks the block.
/// </summary>
public sealed class GatherLimit
{
    public const uint BlockAbnormal = 10020;

    /// <summary>Aborts this recent count; the real window is unknown, so it errs on the long side.</summary>
    public const long AbortWindowMs = 10 * 60_000;

    /// <summary>With this many recent aborts, the next one may get gathering blocked.</summary>
    public const int WarnAt = 3;

    private readonly Queue<long> _aborts = new();
    private long _blockedUntil;
    private uint? _instance;

    public void Aborted(long nowMs) => _aborts.Enqueue(nowMs);

    /// <summary>An abnormal on us: the block starts (and the count starts over).</summary>
    public void Apply(BuffEvent buff)
    {
        if (buff.AbnormalId != BlockAbnormal || buff.DurationMs is not { } length)
        {
            return;
        }

        _blockedUntil = buff.TimestampMs + length;
        _instance = buff.Instance;
        _aborts.Clear();
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
