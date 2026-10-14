using System.Buffers.Binary;
using System.Text;

namespace Avion2.Protocol;

/// <summary>Forward-only reader over a packet body. All multi-byte fixed integers are little-endian.</summary>
public ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> _data;

    public SpanReader(ReadOnlySpan<byte> data, int position = 0)
    {
        _data = data;
        Position = position;
    }

    public int Position { get; set; }

    public readonly int Remaining => _data.Length - Position;

    public readonly ReadOnlySpan<byte> Rest => _data[Position..];

    public readonly ReadOnlySpan<byte> Data => _data;

    public bool TryVarint(out uint value)
    {
        if (Position < _data.Length && Varint.TryRead(_data[Position..], out value, out int length))
        {
            Position += length;
            return true;
        }

        value = 0;
        return false;
    }

    public bool TryU8(out byte value)
    {
        if (Remaining >= 1)
        {
            value = _data[Position++];
            return true;
        }

        value = 0;
        return false;
    }

    public bool TryU16(out ushort value)
    {
        if (Remaining >= 2)
        {
            value = BinaryPrimitives.ReadUInt16LittleEndian(_data[Position..]);
            Position += 2;
            return true;
        }

        value = 0;
        return false;
    }

    public bool TryU32(out uint value)
    {
        if (Remaining >= 4)
        {
            value = BinaryPrimitives.ReadUInt32LittleEndian(_data[Position..]);
            Position += 4;
            return true;
        }

        value = 0;
        return false;
    }

    public bool TryI64(out long value)
    {
        if (Remaining >= 8)
        {
            value = BinaryPrimitives.ReadInt64LittleEndian(_data[Position..]);
            Position += 8;
            return true;
        }

        value = 0;
        return false;
    }

    public bool TryF32(out float value)
    {
        value = 0;
        if (Remaining < 4)
        {
            return false;
        }

        value = BinaryPrimitives.ReadSingleLittleEndian(_data[Position..]);
        Position += 4;
        return true;
    }

    public bool TrySkip(int count)
    {
        if (count >= 0 && Remaining >= count)
        {
            Position += count;
            return true;
        }

        return false;
    }

    /// <summary>Reads <paramref name="length"/> bytes as UTF-8, rejecting control characters.</summary>
    public bool TryUtf8(int length, out string value)
    {
        value = string.Empty;
        if (length <= 0 || Remaining < length)
        {
            return false;
        }

        var bytes = _data.Slice(Position, length);
        foreach (byte b in bytes)
        {
            if (b < 0x20 || b == 0x7F)
            {
                return false;
            }
        }

        try
        {
            value = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        Position += length;
        return true;
    }
}
