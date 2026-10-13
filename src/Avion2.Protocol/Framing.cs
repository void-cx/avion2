using System.Buffers.Binary;
using K4os.Compression.LZ4;

namespace Avion2.Protocol;

/// <summary>Receives one framed packet payload (opcode + body, length prefix stripped).</summary>
public delegate void FrameHandler(ReadOnlySpan<byte> payload);

/// <summary>
/// Server→client framing:
/// <code>
/// 00                                     zero padding, skip 1 byte
/// 17 03 03 &lt;u16 BE len&gt; ...              stray TLS record, skip 5+len
/// &lt;varint L&gt; &lt;opcode&gt; &lt;body&gt;             packet, total frame bytes = L - 4 + varintLen
/// &lt;varint L&gt; FF FF &lt;u32 LE size&gt; &lt;lz4&gt;   compressed bundle of further frames
/// </code>
/// </summary>
public static class Framing
{
    public const int MaxPacketBytes = 65535;
    public const int MaxFragmentWaitBytes = 16384;
    public const int MaxBundleDecompressedBytes = 1_000_000;
    public const int MaxBundleDepth = 4;
    public const int BundleHeaderBytes = 6;

    /// <summary>
    /// Reads the length prefix. Fails when the varint is invalid or the declared frame is smaller than its own header.
    /// </summary>
    public static bool TryGetFrameSize(ReadOnlySpan<byte> data, out int frameSize, out int headerSize)
    {
        frameSize = 0;
        headerSize = 0;
        if (!Varint.TryRead(data, out uint length, out headerSize))
        {
            return false;
        }

        long size = (long)length - 4 + headerSize;
        if (size <= headerSize || size > int.MaxValue)
        {
            return false;
        }

        frameSize = (int)size;
        return true;
    }

    /// <summary>Encodes the length prefix for a payload: L = payload length + 4.</summary>
    public static uint LengthValueFor(int payloadLength) => (uint)(payloadLength + 4);

    public static bool LooksLikeTlsRecord(ReadOnlySpan<byte> data, out int recordSize)
    {
        recordSize = 0;
        if (data.Length < 5 || data[0] < 0x14 || data[0] > 0x17 || data[1] != 0x03 || data[2] > 0x04)
        {
            return false;
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(data[3..]);
        if (length < 1 || length > 16640)
        {
            return false;
        }

        recordSize = 5 + length;
        return true;
    }

    public static bool TryReadBundleHeader(ReadOnlySpan<byte> payload, out int decompressedSize)
    {
        decompressedSize = 0;
        if (payload.Length < BundleHeaderBytes || payload[0] != 0xFF || payload[1] != 0xFF)
        {
            return false;
        }

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(payload[2..]);
        if (size == 0 || size > MaxBundleDecompressedBytes)
        {
            return false;
        }

        decompressedSize = (int)size;
        return true;
    }

    /// <summary>Decompresses a bundle payload (<c>FF FF size lz4-block</c>). Returns null when malformed.</summary>
    public static byte[]? DecompressBundle(ReadOnlySpan<byte> payload)
    {
        if (!TryReadBundleHeader(payload, out int size))
        {
            return null;
        }

        var output = new byte[size];
        int decoded = LZ4Codec.Decode(payload[BundleHeaderBytes..], output);
        if (decoded <= 0)
        {
            return null;
        }

        return decoded == size ? output : output.AsSpan(0, decoded).ToArray();
    }

    /// <summary>
    /// Walks a reassembled TCP stream. Returns the number of bytes consumed; the rest is an incomplete
    /// frame to retry once more data arrives.
    /// <para>
    /// While <paramref name="synced"/> is false (stream joined mid-way, or after garbage/gaps) a candidate
    /// frame is only accepted when it looks real: a known opcode family or bundle header, followed by
    /// another plausible length prefix. Otherwise a bogus length such as <c>ff 01</c> would swallow real
    /// frames and keep the walker misaligned.
    /// </para>
    /// </summary>
    public static int Walk(ReadOnlySpan<byte> buffer, FrameHandler handler, ref bool synced, ref long skippedBytes)
    {
        int pos = 0;
        while (pos < buffer.Length)
        {
            var rest = buffer[pos..];
            if (rest[0] == 0x00)
            {
                pos++;
                continue;
            }

            if (rest[0] is >= 0x14 and <= 0x17 && rest.Length >= 2 && rest[1] == 0x03)
            {
                if (rest.Length < 5)
                {
                    return pos;
                }

                if (LooksLikeTlsRecord(rest, out int tlsSize))
                {
                    if (rest.Length < tlsSize)
                    {
                        return pos;
                    }

                    pos += tlsSize;
                    skippedBytes += tlsSize;
                    continue;
                }
            }

            if (!TryGetFrameSize(rest, out int frameSize, out int headerSize))
            {
                if (rest.Length < Varint.MaxBytes)
                {
                    return pos;
                }

                Skip(ref pos, ref synced, ref skippedBytes);
                continue;
            }

            if (frameSize > MaxPacketBytes)
            {
                Skip(ref pos, ref synced, ref skippedBytes);
                continue;
            }

            var payload = rest[headerSize..Math.Min(frameSize, rest.Length)];
            if (!synced && payload.Length < 2)
            {
                return pos; // need the opcode to judge plausibility
            }

            bool plausible = synced || LooksLikePacketStart(payload);
            if (frameSize > rest.Length)
            {
                if (!plausible)
                {
                    Skip(ref pos, ref synced, ref skippedBytes);
                    continue;
                }

                if (frameSize <= MaxFragmentWaitBytes)
                {
                    return pos;
                }

                // Large frames are only worth waiting for when they are bundles (zone loads reach ~17 KB).
                if (payload.Length < BundleHeaderBytes || TryReadBundleHeader(payload, out _))
                {
                    return pos;
                }

                Skip(ref pos, ref synced, ref skippedBytes);
                continue;
            }

            if (!synced)
            {
                var next = rest[frameSize..];
                bool nextOk = next.IsEmpty || next[0] == 0x00 ||
                              (TryGetFrameSize(next, out int nextSize, out _) && nextSize <= MaxPacketBytes) ||
                              (next.Length < Varint.MaxBytes && next[^1] >= 0x80);
                if (!plausible || !nextOk)
                {
                    Skip(ref pos, ref synced, ref skippedBytes);
                    continue;
                }

                synced = true;
            }

            handler(payload);
            pos += frameSize;
        }

        return pos;
    }

    /// <summary>
    /// Opcodes observed so far share a small set of second bytes ("families").
    /// Only used to judge candidates while resynchronising.
    /// </summary>
    public static bool LooksLikePacketStart(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
        {
            return false;
        }

        if (payload[0] == 0xFF && payload[1] == 0xFF)
        {
            return payload.Length < BundleHeaderBytes || TryReadBundleHeader(payload, out _);
        }

        return payload[1] is 0x36 or 0x37 or 0x38 or 0x40 or 0x43 or 0x56 or 0x8A or 0x8D
            or 0x90 or 0x91 or 0x92 or 0x97 or 0xE3 or 0xFF;
    }

    private static void Skip(ref int pos, ref bool synced, ref long skippedBytes)
    {
        pos++;
        skippedBytes++;
        synced = false;
    }

    /// <summary>Walks decompressed bundle content. Stops at the first unparseable length instead of resyncing.</summary>
    public static void WalkInner(ReadOnlySpan<byte> buffer, FrameHandler handler)
    {
        int pos = 0;
        while (pos < buffer.Length)
        {
            var rest = buffer[pos..];
            if (rest[0] == 0x00)
            {
                pos++;
                continue;
            }

            if (!TryGetFrameSize(rest, out int frameSize, out int headerSize) || frameSize > rest.Length)
            {
                return;
            }

            handler(rest[headerSize..frameSize]);
            pos += frameSize;
        }
    }
}
