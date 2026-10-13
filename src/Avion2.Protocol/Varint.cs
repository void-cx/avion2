namespace Avion2.Protocol;

/// <summary>
/// Unsigned LEB128 varints as used throughout the AION2 wire format
/// (7 bits per byte, low group first, 0x80 = continue, at most 5 bytes / 32 bits).
/// </summary>
public static class Varint
{
    public const int MaxBytes = 5;

    public static bool TryRead(ReadOnlySpan<byte> data, out uint value, out int length)
    {
        value = 0;
        length = 0;
        int shift = 0;
        for (int i = 0; i < data.Length && i < MaxBytes; i++)
        {
            byte b = data[i];
            // The fifth byte may only contribute the top 4 bits and must terminate.
            if (i == MaxBytes - 1 && b > 0x0F)
            {
                break;
            }

            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                length = i + 1;
                return true;
            }

            shift += 7;
        }

        value = 0;
        return false;
    }

    public static int SizeOf(uint value)
    {
        int size = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            size++;
        }

        return size;
    }

    public static int Write(Span<byte> destination, uint value)
    {
        int i = 0;
        while (value >= 0x80)
        {
            destination[i++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[i++] = (byte)value;
        return i;
    }
}
