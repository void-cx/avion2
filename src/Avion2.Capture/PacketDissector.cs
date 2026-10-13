using System.Buffers.Binary;
using System.Net;

namespace Avion2.Capture;

public readonly record struct NetEndpoint(UInt128 Address, bool IsV6, ushort Port)
{
    public override string ToString()
    {
        if (!IsV6)
        {
            uint v4 = (uint)Address;
            return $"{v4 >> 24}.{(v4 >> 16) & 0xFF}.{(v4 >> 8) & 0xFF}.{v4 & 0xFF}:{Port}";
        }

        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(bytes, Address);
        return $"[{new IPAddress(bytes)}]:{Port}";
    }
}

/// <summary>Directional TCP flow (source → destination).</summary>
public readonly record struct FlowKey(NetEndpoint Source, NetEndpoint Destination)
{
    public override string ToString() => $"{Source} -> {Destination}";
}

[Flags]
public enum TcpFlags : byte
{
    None = 0,
    Fin = 0x01,
    Syn = 0x02,
    Rst = 0x04,
    Psh = 0x08,
    Ack = 0x10,
}

public readonly record struct TcpSegmentInfo(FlowKey Flow, uint Sequence, TcpFlags Flags);

/// <summary>
/// Minimal link → IPv4/IPv6 → TCP dissector. Hand-rolled (instead of PacketDotNet) for speed and to
/// truncate payloads at the IP total length: some NICs pad short frames with non-zero bytes.
/// </summary>
public static class PacketDissector
{
    // libpcap DLT values
    public const int DltNull = 0;
    public const int DltEthernet = 1;
    public const int DltRaw12 = 12;
    public const int DltRaw14 = 14;
    public const int DltRaw = 101;
    public const int DltLoop = 108;
    public const int DltLinuxSll = 113;
    public const int DltIpv4 = 228;
    public const int DltIpv6 = 229;
    public const int DltLinuxSll2 = 276;

    public static bool IsLoopbackLink(int dlt) => dlt is DltNull or DltLoop;

    public static bool TryParse(int dlt, ReadOnlySpan<byte> frame, out TcpSegmentInfo segment, out ReadOnlySpan<byte> payload)
    {
        segment = default;
        payload = default;
        if (!TryGetNetworkLayer(dlt, frame, out var ip))
        {
            return false;
        }

        if (ip.IsEmpty)
        {
            return false;
        }

        int version = ip[0] >> 4;
        return version switch
        {
            4 => TryParseIpv4(ip, out segment, out payload),
            6 => TryParseIpv6(ip, out segment, out payload),
            _ => false,
        };
    }

    private static bool TryGetNetworkLayer(int dlt, ReadOnlySpan<byte> frame, out ReadOnlySpan<byte> ip)
    {
        ip = default;
        switch (dlt)
        {
            case DltEthernet:
            {
                if (frame.Length < 14)
                {
                    return false;
                }

                int offset = 12;
                ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(frame[offset..]);
                for (int tags = 0; tags < 2 && etherType is 0x8100 or 0x88A8; tags++)
                {
                    offset += 4;
                    if (frame.Length < offset + 2)
                    {
                        return false;
                    }

                    etherType = BinaryPrimitives.ReadUInt16BigEndian(frame[offset..]);
                }

                if (etherType is not (0x0800 or 0x86DD))
                {
                    return false;
                }

                ip = frame[(offset + 2)..];
                return true;
            }

            case DltNull or DltLoop:
                if (frame.Length < 4)
                {
                    return false;
                }

                ip = frame[4..];
                return true;

            case DltRaw12 or DltRaw14 or DltRaw or DltIpv4 or DltIpv6:
                ip = frame;
                return true;

            case DltLinuxSll:
                if (frame.Length < 16)
                {
                    return false;
                }

                ip = frame[16..];
                return true;

            case DltLinuxSll2:
                if (frame.Length < 20)
                {
                    return false;
                }

                ip = frame[20..];
                return true;

            default:
                return false;
        }
    }

    private static bool TryParseIpv4(ReadOnlySpan<byte> ip, out TcpSegmentInfo segment, out ReadOnlySpan<byte> payload)
    {
        segment = default;
        payload = default;
        if (ip.Length < 20)
        {
            return false;
        }

        int headerLength = (ip[0] & 0x0F) * 4;
        int totalLength = BinaryPrimitives.ReadUInt16BigEndian(ip[2..]);
        ushort fragment = BinaryPrimitives.ReadUInt16BigEndian(ip[6..]);
        if (headerLength < 20 || ip[9] != 6 || (fragment & 0x3FFF) != 0)
        {
            return false; // not TCP, or fragmented
        }

        // Total length 0 happens with TSO; otherwise cut the link-layer padding.
        if (totalLength >= headerLength && totalLength <= ip.Length)
        {
            ip = ip[..totalLength];
        }

        if (ip.Length < headerLength)
        {
            return false;
        }

        UInt128 source = BinaryPrimitives.ReadUInt32BigEndian(ip[12..]);
        UInt128 destination = BinaryPrimitives.ReadUInt32BigEndian(ip[16..]);
        return TryParseTcp(ip[headerLength..], source, destination, isV6: false, out segment, out payload);
    }

    private static bool TryParseIpv6(ReadOnlySpan<byte> ip, out TcpSegmentInfo segment, out ReadOnlySpan<byte> payload)
    {
        segment = default;
        payload = default;
        if (ip.Length < 40 || ip[6] != 6)
        {
            return false; // extension headers are not supported
        }

        int payloadLength = BinaryPrimitives.ReadUInt16BigEndian(ip[4..]);
        var body = ip[40..];
        if (payloadLength > 0 && payloadLength <= body.Length)
        {
            body = body[..payloadLength];
        }

        UInt128 source = BinaryPrimitives.ReadUInt128BigEndian(ip[8..]);
        UInt128 destination = BinaryPrimitives.ReadUInt128BigEndian(ip[24..]);
        return TryParseTcp(body, source, destination, isV6: true, out segment, out payload);
    }

    private static bool TryParseTcp(
        ReadOnlySpan<byte> tcp, UInt128 source, UInt128 destination, bool isV6,
        out TcpSegmentInfo segment, out ReadOnlySpan<byte> payload)
    {
        segment = default;
        payload = default;
        if (tcp.Length < 20)
        {
            return false;
        }

        int dataOffset = (tcp[12] >> 4) * 4;
        if (dataOffset < 20 || dataOffset > tcp.Length)
        {
            return false;
        }

        ushort sourcePort = BinaryPrimitives.ReadUInt16BigEndian(tcp);
        ushort destinationPort = BinaryPrimitives.ReadUInt16BigEndian(tcp[2..]);
        uint sequence = BinaryPrimitives.ReadUInt32BigEndian(tcp[4..]);
        var flags = (TcpFlags)(tcp[13] & 0x1F);

        var flow = new FlowKey(new NetEndpoint(source, isV6, sourcePort), new NetEndpoint(destination, isV6, destinationPort));
        segment = new TcpSegmentInfo(flow, sequence, flags);
        payload = tcp[dataOffset..];
        return true;
    }
}
