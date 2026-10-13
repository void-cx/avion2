using Avion2.Protocol;

namespace Avion2.Capture;

/// <summary>The server→client flow we decode, and the capture device it was seen on.</summary>
public sealed record LockedFlow(int DeviceIndex, bool IsLoopback, FlowKey Flow, long LockedAtMs);

/// <summary>
/// Finds the game connection without hard-coded IPs/ports. Server→client game traffic contains the
/// record terminator <c>0E 00 36</c> (legacy <c>06 00 36</c>) many times per second even when idle;
/// a flow is locked once it shows enough of them in a sliding window.
/// Ping reducers (ExitLag etc.) relay over loopback, so loopback flows get a head start.
/// </summary>
public sealed class GameConnectionDetector
{
    public const int SignatureLockThreshold = 12;
    public const long SignatureWindowMs = 3000;
    public const long LoopbackGraceMs = 2500;
    public const long StaleLockMs = 120_000;
    public const long SwitchAfterSilenceMs = 5000;

    private static ReadOnlySpan<byte> Signature => [0x0E, 0x00, 0x36];
    private static ReadOnlySpan<byte> LegacySignature => [0x06, 0x00, 0x36];

    private readonly Dictionary<(int Device, FlowKey Flow), Candidate> _candidates = new();
    private readonly HashSet<FlowKey> _seenFlows = new();
    private readonly HashSet<FlowKey> _tlsFlows = new();
    private long _lockedLastSignatureMs;
    private long _lockedLastSegmentMs;

    public LockedFlow? Locked { get; private set; }

    public event Action<LockedFlow?>? LockChanged;

    /// <summary>Feeds one TCP segment. Returns true if it belongs to the locked server→client flow.</summary>
    public bool Observe(int deviceIndex, bool isLoopback, in TcpSegmentInfo segment, ReadOnlySpan<byte> payload, long timestampMs)
    {
        var flow = segment.Flow;
        bool isLockedFlow = Locked is { } locked && locked.DeviceIndex == deviceIndex && locked.Flow == flow;

        if (isLockedFlow && (segment.Flags & (TcpFlags.Fin | TcpFlags.Rst)) != 0)
        {
            SetLock(null);
            return false;
        }

        if (Locked is not null && timestampMs - _lockedLastSegmentMs > StaleLockMs)
        {
            SetLock(null);
            isLockedFlow = false;
        }

        if (payload.IsEmpty)
        {
            return isLockedFlow;
        }

        // A connection whose first payload is a TLS record is not the game stream (the server also speaks TLS).
        if (_seenFlows.Add(flow) && Framing.LooksLikeTlsRecord(payload, out _))
        {
            _tlsFlows.Add(flow);
        }

        if (_seenFlows.Count > 4096)
        {
            _seenFlows.Clear();
        }

        bool hasSignature = !_tlsFlows.Contains(flow) &&
                            (payload.IndexOf(Signature) >= 0 || payload.IndexOf(LegacySignature) >= 0);

        if (isLockedFlow)
        {
            _lockedLastSegmentMs = timestampMs;
            if (hasSignature)
            {
                _lockedLastSignatureMs = timestampMs;
            }

            return true;
        }

        if (hasSignature)
        {
            ConsiderCandidate(deviceIndex, isLoopback, flow, timestampMs);
        }

        return false;
    }

    public void Reset()
    {
        _candidates.Clear();
        SetLock(null);
    }

    private void ConsiderCandidate(int deviceIndex, bool isLoopback, FlowKey flow, long timestampMs)
    {
        var key = (deviceIndex, flow);
        if (!_candidates.TryGetValue(key, out var candidate))
        {
            candidate = new Candidate(timestampMs);
            _candidates[key] = candidate;
        }

        candidate.Hits.Enqueue(timestampMs);
        while (candidate.Hits.Count > 0 && timestampMs - candidate.Hits.Peek() > SignatureWindowMs)
        {
            candidate.Hits.Dequeue();
        }

        if (candidate.Hits.Count < SignatureLockThreshold)
        {
            return;
        }

        if (!isLoopback && timestampMs - candidate.FirstSeenMs < LoopbackGraceMs)
        {
            return;
        }

        if (Locked is { } current)
        {
            bool upgradeToLoopback = isLoopback && !current.IsLoopback;
            bool currentWentQuiet = timestampMs - _lockedLastSignatureMs > SwitchAfterSilenceMs;
            if (!upgradeToLoopback && !currentWentQuiet)
            {
                return;
            }
        }

        _lockedLastSignatureMs = timestampMs;
        _lockedLastSegmentMs = timestampMs;
        SetLock(new LockedFlow(deviceIndex, isLoopback, flow, timestampMs));

        if (_candidates.Count > 256)
        {
            _candidates.Clear();
        }
    }

    private void SetLock(LockedFlow? flow)
    {
        if (Locked == flow)
        {
            return;
        }

        Locked = flow;
        LockChanged?.Invoke(flow);
    }

    private sealed class Candidate(long firstSeenMs)
    {
        public long FirstSeenMs { get; } = firstSeenMs;

        public Queue<long> Hits { get; } = new();
    }
}
