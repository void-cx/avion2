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
        if (!Detector.Observe(frame.DeviceIndex, frame.IsLoopbackDevice, segment, payload, frame.TimestampMs))
        {
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
        string via = locked.IsLoopback ? " (loopback relay)" : string.Empty;
        StatusChanged?.Invoke($"Locked game stream {locked.Flow}{via} on device #{locked.DeviceIndex}");
    }
}
