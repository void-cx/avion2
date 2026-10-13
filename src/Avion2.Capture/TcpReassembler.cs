namespace Avion2.Capture;

public delegate void StreamDataHandler(ReadOnlySpan<byte> data);

/// <summary>
/// Reorders one direction of a TCP connection into a contiguous byte stream.
/// Handles retransmits, partial overlaps and sequence wraparound. Gaps that never fill
/// (capture drops) are skipped after <see cref="MaxHeldBytes"/> or <see cref="MaxGapWaitMs"/>.
/// </summary>
public sealed class TcpReassembler
{
    public const int MaxHeldBytes = 512 * 1024;
    public const long MaxGapWaitMs = 1500;

    private readonly Dictionary<uint, byte[]> _pending = new();
    private readonly StreamDataHandler _output;
    private readonly Action? _onGap;
    private uint _next;
    private bool _initialized;
    private int _heldBytes;
    private long _gapSinceMs;

    /// <param name="onGap">Called before data following a skipped gap is emitted.</param>
    public TcpReassembler(StreamDataHandler output, Action? onGap = null)
    {
        _output = output;
        _onGap = onGap;
    }

    public long Retransmits { get; private set; }

    public long GapSkips { get; private set; }

    public long SkippedBytes { get; private set; }

    public void Push(uint sequence, TcpFlags flags, ReadOnlySpan<byte> data, long timestampMs)
    {
        if ((flags & TcpFlags.Syn) != 0)
        {
            Reset();
            _next = sequence + 1;
            _initialized = true;
            return;
        }

        if (data.IsEmpty)
        {
            return;
        }

        if (!_initialized)
        {
            _next = sequence;
            _initialized = true;
        }

        int distance = (int)(sequence - _next);
        if (distance <= 0)
        {
            int overlap = -distance;
            if (overlap >= data.Length)
            {
                Retransmits++;
                return;
            }

            Emit(data[overlap..]);
            Drain();
            _gapSinceMs = timestampMs; // any remaining pending data starts a fresh gap
        }
        else
        {
            if (_pending.Count == 0)
            {
                _gapSinceMs = timestampMs;
            }

            if (!_pending.TryGetValue(sequence, out var existing) || existing.Length < data.Length)
            {
                _heldBytes += data.Length - (existing?.Length ?? 0);
                _pending[sequence] = data.ToArray();
            }

            if (_heldBytes > MaxHeldBytes || timestampMs - _gapSinceMs > MaxGapWaitMs)
            {
                SkipGap();
                _gapSinceMs = timestampMs;
            }
        }
    }

    public void Reset()
    {
        _pending.Clear();
        _heldBytes = 0;
        _initialized = false;
    }

    private void Emit(ReadOnlySpan<byte> data)
    {
        _output(data);
        _next += (uint)data.Length;
    }

    private void Drain()
    {
        bool progressed = true;
        while (progressed && _pending.Count > 0)
        {
            progressed = false;
            foreach (var (sequence, chunk) in _pending)
            {
                int distance = (int)(sequence - _next);
                if (distance > 0)
                {
                    continue;
                }

                _pending.Remove(sequence);
                _heldBytes -= chunk.Length;
                int overlap = -distance;
                if (overlap < chunk.Length)
                {
                    Emit(chunk.AsSpan(overlap));
                }

                progressed = true;
                break;
            }
        }
    }

    private void SkipGap()
    {
        uint nearest = 0;
        int best = int.MaxValue;
        foreach (uint sequence in _pending.Keys)
        {
            int distance = (int)(sequence - _next);
            if (distance > 0 && distance < best)
            {
                best = distance;
                nearest = sequence;
            }
        }

        if (best == int.MaxValue)
        {
            return;
        }

        GapSkips++;
        SkippedBytes += best;
        _next = nearest;
        _onGap?.Invoke();
        Drain();
    }
}
