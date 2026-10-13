using Avion2.Protocol;
using K4os.Compression.LZ4;

namespace Avion2.Tests;

internal static class TestPackets
{
    public static byte[] Hex(string hex) => Convert.FromHexString(new string(hex.Where(Uri.IsHexDigit).ToArray()));

    /// <summary>Builds a framed packet: varint(L = payload + 4) + payload.</summary>
    public static byte[] Frame(byte[] payload)
    {
        Span<byte> header = stackalloc byte[Varint.MaxBytes];
        int headerSize = Varint.Write(header, Framing.LengthValueFor(payload.Length));
        return [.. header[..headerSize], .. payload];
    }

    public static byte[] Frame(string payloadHex) => Frame(Hex(payloadHex));

    /// <summary>Builds a bundle frame containing the given (already framed) packets.</summary>
    public static byte[] Bundle(params byte[][] frames)
    {
        byte[] content = frames.SelectMany(f => f).ToArray();
        var compressed = new byte[LZ4Codec.MaximumOutputSize(content.Length)];
        int length = LZ4Codec.Encode(content, compressed);
        byte[] size = BitConverter.GetBytes(content.Length);
        return Frame([0xFF, 0xFF, .. size, .. compressed[..length]]);
    }

    public static List<GameEvent> Decode(params byte[][] streamChunks)
    {
        var events = new List<GameEvent>();
        var decoder = new GameStreamDecoder(events.Add);
        foreach (var chunk in streamChunks)
        {
            decoder.Feed(chunk, 1000);
        }

        return events;
    }

    public static List<GameEvent> Parse(string payloadHex)
    {
        var events = new List<GameEvent>();
        PacketParser.Parse(Hex(payloadHex), 1000, events.Add);
        return events;
    }
}
