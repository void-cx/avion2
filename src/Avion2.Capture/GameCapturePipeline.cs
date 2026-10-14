using Avion2.Protocol;

namespace Avion2.Capture;

/// <summary>A captured link-layer frame. <see cref="LinkType"/> is the libpcap DLT value.</summary>
public readonly record struct RawFrame(int DeviceIndex, int LinkType, bool IsLoopbackDevice, long TimestampMs, byte[] Data);

public sealed class PipelineStats
{
    public long Frames { get; internal set; }
    public long TcpSegments { get; internal set; }
    public long GameSegments { get; internal set; }
    public long Locks { get; internal set; }
}

/// <summary>
/// Frames → TCP → game-connection detection → reassembly → <see cref="GameStreamDecoder"/> → events.
/// Not thread-safe: call <see cref="Process"/> from a single consumer thread.
/// </summary>
public sealed class GameCapturePipeline
{
    private readonly Action<GameEvent> _emit;
    private readonly PacketObserver? _observer;
    private TcpReassembler? _reassembler;
    private long _currentTimestamp;

    /// <summary>
    /// Segments of connections that are not locked yet. The game sends a lot right when a connection opens
    /// (our identity, the map, the whole inventory) — before the detector has seen enough to lock. On lock the
    /// flow's buffered segments are decoded first, so nothing from the start of the connection is lost.
    /// </summary>
    private readonly Dictionary<(int Device, FlowKey Flow), Queue<PendingSegment>> _pending = new();
    private readonly Dictionary<(int Device, FlowKey Flow), long> _pendingBytes = new();
    private long _lastPruneMs;

    /// <summary>How far back, and how much per connection, is kept until a lock.</summary>
    public const long PendingWindowMs = 10_000;
    public const long PendingBytesPerFlow = 4 * 1024 * 1024;

    private readonly record struct PendingSegment(uint Sequence, TcpFlags Flags, byte[] Data, long TimestampMs);

    public GameCapturePipeline(Action<GameEvent> emit, PacketObserver? observer = null)
    {
        _emit = emit;
        _observer = observer;
        Decoder = new GameStreamDecoder(emit, observer);
        Detector.LockChanged += OnLockChanged;
    }

    public GameConnectionDetector Detector { get; } = new();

    public GameStreamDecoder Decoder { get; private set; }

    public PipelineStats Stats { get; } = new();

    public TcpReassembler? Reassembler => _reassembler;

    /// <summary>Raised on lock / unlock with a human-readable message.</summary>
    public event Action<string>? StatusChanged;

    public void Process(in RawFrame frame)
    {
        Stats.Frames++;
        if (!PacketDissector.TryParse(frame.LinkType, frame.Data, out var segment, out var payload))
        {
            return;
        }

        Stats.TcpSegments++;
        _currentTimestamp = frame.TimestampMs;
        bool game = Detector.Observe(frame.DeviceIndex, frame.IsLoopbackDevice, segment, payload, frame.TimestampMs);

        // The segment that completes a lock is reported as not-yet-game; it belongs right after the replayed ones
        if (!game && Detector.Locked is { } locked && locked.DeviceIndex == frame.DeviceIndex && locked.Flow == segment.Flow)
        {
            game = true;
        }

        if (!game)
        {
            Buffer(frame.DeviceIndex, segment, payload, frame.TimestampMs);
            return;
        }

        Stats.GameSegments++;
        _reassembler?.Push(segment.Sequence, segment.Flags, payload, frame.TimestampMs);
    }

    private void OnLockChanged(LockedFlow? locked)
    {
        // A new connection means a new byte stream; never splice two streams together.
        Decoder = new GameStreamDecoder(_emit, _observer);
        _reassembler = locked is null ? null : new TcpReassembler(data => Decoder.Feed(data, _currentTimestamp), () => Decoder.Resync());

        if (locked is null)
        {
            StatusChanged?.Invoke("Game connection lost; searching…");
            return;
        }

        Stats.Locks++;
        ReplayPending(locked);
        string via = locked.IsLoopback ? " (loopback relay)" : string.Empty;
        StatusChanged?.Invoke($"Locked game stream {locked.Flow}{via} on device #{locked.DeviceIndex}");
    }

    private void Buffer(int device, in TcpSegmentInfo segment, ReadOnlySpan<byte> payload, long timestampMs)
    {
        if (payload.IsEmpty && (segment.Flags & TcpFlags.Syn) == 0)
        {
            return;
        }

        var key = (device, segment.Flow);
        if (!_pending.TryGetValue(key, out var queue))
        {
            _pending[key] = queue = new Queue<PendingSegment>();
        }

        queue.Enqueue(new PendingSegment(segment.Sequence, segment.Flags, payload.ToArray(), timestampMs));
        long bytes = _pendingBytes.GetValueOrDefault(key) + payload.Length;
        while (queue.Count > 0 && (bytes > PendingBytesPerFlow || timestampMs - queue.Peek().TimestampMs > PendingWindowMs))
        {
            bytes -= queue.Dequeue().Data.Length;
        }

        _pendingBytes[key] = bytes;

        // Forget connections that went quiet
        if (timestampMs - _lastPruneMs > PendingWindowMs)
        {
            _lastPruneMs = timestampMs;
            foreach (var (k, q) in _pending.ToList())
            {
                if (q.Count == 0 || timestampMs - q.Last().TimestampMs > PendingWindowMs)
                {
                    _pending.Remove(k);
                    _pendingBytes.Remove(k);
                }
            }
        }
    }

    /// <summary>Decodes what the newly locked connection sent before the lock.</summary>
    private void ReplayPending(LockedFlow locked)
    {
        var key = (locked.DeviceIndex, locked.Flow);
        if (_pending.Remove(key, out var queue) && _reassembler is { } reassembler)
        {
            long now = _currentTimestamp;
            foreach (var segment in queue)
            {
                _currentTimestamp = segment.TimestampMs;
                reassembler.Push(segment.Sequence, segment.Flags, segment.Data, segment.TimestampMs);
            }

            _currentTimestamp = now;
        }

        _pending.Clear();
        _pendingBytes.Clear();
    }
}
