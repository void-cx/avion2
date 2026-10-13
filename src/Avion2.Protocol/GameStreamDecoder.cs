namespace Avion2.Protocol;

/// <summary>Observes every decoded (decompressed) packet payload; for reverse-engineering and dumps.</summary>
public delegate void PacketObserver(ReadOnlySpan<byte> payload, int bundleDepth, long timestampMs);

public sealed class DecoderStats
{
    public long BytesIn { get; internal set; }
    public long BytesSkipped;
    public long Packets { get; internal set; }
    public long Bundles { get; internal set; }
    public long BadBundles { get; internal set; }
    public long EmbeddedBundles { get; internal set; }
    public long ParsedPackets { get; internal set; }
    public long BufferOverflows { get; internal set; }
    public long Resyncs { get; internal set; }
    public Dictionary<ushort, long> OpcodeCounts { get; } = new();
}

/// <summary>
/// Turns one reassembled server→client TCP byte stream into <see cref="GameEvent"/>s:
/// framing → bundle decompression (nested) → record parsers.
/// Not thread-safe; feed from a single thread.
/// </summary>
public sealed class GameStreamDecoder
{
    private const int MaxBufferedBytes = 2 * 1024 * 1024;

    private readonly Action<GameEvent> _emit;
    private readonly PacketObserver? _observer;
    private readonly FrameHandler _outerHandler;
    private byte[] _buffer = new byte[64 * 1024];
    private int _count;
    private long _now;
    private bool _synced;

    public GameStreamDecoder(Action<GameEvent> emit, PacketObserver? observer = null)
    {
        _emit = emit;
        _observer = observer;
        _outerHandler = payload => HandleFrame(payload, depth: 0, ParseScope.All);
    }

    public DecoderStats Stats { get; } = new();

    /// <summary>Drops buffered bytes and re-enters resync mode (e.g. after a TCP gap).</summary>
    public void Resync()
    {
        _count = 0;
        _synced = false;
        Stats.Resyncs++;
    }

    public void Feed(ReadOnlySpan<byte> data, long timestampMs)
    {
        _now = timestampMs;
        Stats.BytesIn += data.Length;

        if (_count + data.Length > MaxBufferedBytes)
        {
            // Lost sync for far too long; start over rather than grow without bound.
            Stats.BufferOverflows++;
            _count = 0;
            _synced = false;
        }

        EnsureCapacity(_count + data.Length);
        data.CopyTo(_buffer.AsSpan(_count));
        _count += data.Length;

        int consumed = Framing.Walk(_buffer.AsSpan(0, _count), _outerHandler, ref _synced, ref Stats.BytesSkipped);
        if (consumed > 0)
        {
            Buffer.BlockCopy(_buffer, consumed, _buffer, 0, _count - consumed);
            _count -= consumed;
        }
    }

    private void HandleFrame(ReadOnlySpan<byte> payload, int depth, ParseScope scope)
    {
        if (payload.Length < 2)
        {
            return;
        }

        if (payload[0] == 0xFF && payload[1] == 0xFF)
        {
            HandleBundle(payload, depth, scope);
            return;
        }

        Stats.Packets++;
        ushort opcode = Opcodes.Read(payload);
        Stats.OpcodeCounts[opcode] = Stats.OpcodeCounts.GetValueOrDefault(opcode) + 1;
        _observer?.Invoke(payload, depth, _now);

        if (PacketParser.Parse(payload, _now, _emit, scope))
        {
            Stats.ParsedPackets++;
        }

        ScanEmbeddedBundles(payload, depth);
    }

    private void HandleBundle(ReadOnlySpan<byte> payload, int depth, ParseScope scope)
    {
        if (depth >= Framing.MaxBundleDepth)
        {
            Stats.BadBundles++;
            return;
        }

        byte[]? content = Framing.DecompressBundle(payload);
        if (content is null)
        {
            Stats.BadBundles++;
            return;
        }

        Stats.Bundles++;
        Framing.WalkInner(content, inner => HandleFrame(inner, depth + 1, scope));
    }

    /// <summary>
    /// Some packets carry a complete bundle frame mid-payload (periodic self/roster records).
    /// Those are decoded for identity only, to avoid double-counting combat.
    /// </summary>
    private void ScanEmbeddedBundles(ReadOnlySpan<byte> payload, int depth)
    {
        if (depth + 1 >= Framing.MaxBundleDepth)
        {
            return;
        }

        int searchFrom = 3;
        while (true)
        {
            int idx = payload[searchFrom..].IndexOf((byte)0xFF);
            if (idx < 0)
            {
                return;
            }

            int marker = searchFrom + idx;
            searchFrom = marker + 1;
            if (marker + Framing.BundleHeaderBytes > payload.Length || payload[marker + 1] != 0xFF)
            {
                continue;
            }

            for (int headerSize = 1; headerSize <= 3; headerSize++)
            {
                int frameStart = marker - headerSize;
                if (frameStart < 2)
                {
                    break;
                }

                var frame = payload[frameStart..];
                if (!Framing.TryGetFrameSize(frame, out int frameSize, out int actualHeader) ||
                    actualHeader != headerSize || frameSize > frame.Length ||
                    !Framing.TryReadBundleHeader(frame[headerSize..frameSize], out _))
                {
                    continue;
                }

                Stats.EmbeddedBundles++;
                HandleBundle(frame[headerSize..frameSize], depth + 1, ParseScope.IdentityOnly);
                searchFrom = frameStart + frameSize;
                break;
            }

            if (searchFrom >= payload.Length)
            {
                return;
            }
        }
    }

    private void EnsureCapacity(int required)
    {
        if (_buffer.Length >= required)
        {
            return;
        }

        Array.Resize(ref _buffer, Math.Max(required, _buffer.Length * 2));
    }
}
