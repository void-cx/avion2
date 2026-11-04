using System.Buffers.Binary;
using System.Text;

namespace ExtractGameData.Core;

/// <summary>A table or text file that does not have the expected layout. Nothing is written when one is thrown.</summary>
public sealed class GameDataFormatException(string message) : Exception(message);

/// <summary>
/// Reads the little-endian values of a decrypted client table or text file, with bounds checks on every read so a
/// wrong row layout fails at once instead of producing garbage.
/// </summary>
public sealed class ByteReader(byte[] data)
{
    /// <summary>
    /// UTF-16 strings in table rows are stored XOR-ed with this 8-byte pattern (repeating). UTF-8 strings and the
    /// strings of the text files are stored as they are.
    /// </summary>
    public static ReadOnlySpan<byte> Utf16Mask => [0x25, 0x00, 0xa8, 0x00, 0x7e, 0x00, 0x91, 0x00];

    public byte[] Data { get; } = data;

    public int Position { get; set; }

    public int Remaining => Data.Length - Position;

    public ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new GameDataFormatException($"read of {count} bytes at {Position} runs past the end ({Data.Length})");
        }

        var span = Data.AsSpan(Position, count);
        Position += count;
        return span;
    }

    public byte Byte() => Take(1)[0];

    public short Int16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));

    public ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public long Int64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    public ulong UInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    public float Single() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    public double Double() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    /// <summary>A bool byte; anything but 0 or 1 means the layout is off.</summary>
    public bool Bool()
    {
        byte b = Byte();
        return b switch
        {
            0 => false,
            1 => true,
            _ => throw new GameDataFormatException($"bool byte {b} at {Position - 1}"),
        };
    }

    /// <summary>
    /// A length-prefixed string: <c>i32 n</c>, then n UTF-8 bytes including the closing NUL, or for n &lt; 0, -n UTF-16
    /// code units including the closing NUL. <paramref name="masked"/> undoes the XOR pattern of table strings.
    /// </summary>
    public string String(bool masked)
    {
        int at = Position;
        int length = Int32();
        if (length == 0)
        {
            return string.Empty;
        }

        if (length > 0)
        {
            if (length > Remaining)
            {
                throw new GameDataFormatException($"string length {length} at {at} runs past the end");
            }

            var bytes = Take(length);
            if (bytes[^1] != 0)
            {
                throw new GameDataFormatException($"string at {at} is not NUL-terminated");
            }

            return Encoding.UTF8.GetString(bytes[..^1]);
        }

        if (length == int.MinValue || -length > Remaining / 2)
        {
            throw new GameDataFormatException($"string length {length} at {at} runs past the end");
        }

        var units = Take(-length * 2).ToArray();
        bool terminated = units[^1] == 0 && units[^2] == 0; // the closing NUL is stored as it is
        if (masked)
        {
            Unmask(units);
        }

        if (!terminated && (units[^1] != 0 || units[^2] != 0))
        {
            throw new GameDataFormatException($"string at {at} is not NUL-terminated");
        }

        return Encoding.Unicode.GetString(units, 0, units.Length - 2);
    }

    /// <summary>Undoes (or applies: XOR is its own inverse) the UTF-16 string pattern in place.</summary>
    public static void Unmask(Span<byte> utf16)
    {
        var mask = Utf16Mask;
        for (int i = 0; i < utf16.Length; i++)
        {
            utf16[i] ^= mask[i & 7];
        }
    }
}
