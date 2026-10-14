using System.Buffers.Binary;
using Avion2.Capture;
using Avion2.Protocol;
using static Avion2.Tests.TestPackets;

namespace Avion2.Tests;

internal static class FrameBuilder
{
    public static readonly uint ServerIp = 0xCE7F9C0A; // 206.127.156.10
    public static readonly uint ClientIp = 0xC0A80002; // 192.168.0.2

    /// <summary>Ethernet + IPv4 + TCP frame, optionally with non-zero link padding after the IP packet.</summary>
    public static byte[] Ethernet(uint src, ushort srcPort, uint dst, ushort dstPort, uint seq, byte[] payload,
        TcpFlags flags = TcpFlags.Ack | TcpFlags.Psh, int padding = 0)
    {
        int ipLength = 20 + 20 + payload.Length;
        var frame = new byte[14 + ipLength + padding];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x0800);
        var ip = frame.AsSpan(14);
        ip[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(ip[2..], (ushort)ipLength);
        ip[8] = 64;
        ip[9] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(ip[12..], src);
        BinaryPrimitives.WriteUInt32BigEndian(ip[16..], dst);
        var tcp = ip[20..];
        BinaryPrimitives.WriteUInt16BigEndian(tcp, srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[2..], dstPort);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[4..], seq);
        tcp[12] = 0x50;
        tcp[13] = (byte)flags;
        payload.CopyTo(tcp[20..]);
        for (int i = 0; i < padding; i++)
        {
            frame[14 + ipLength + i] = 0xAB;
        }

        return frame;
    }

    public static RawFrame Server(uint seq, byte[] payload, long ts, int device = 0, bool loopback = false) =>
        new(device, PacketDissector.DltEthernet, loopback, ts, Ethernet(ServerIp, 13328, ClientIp, 50000, seq, payload));
}

public class DissectorTests
{
    [Fact]
    public void TruncatesLinkPaddingAtIpTotalLength()
    {
        byte[] frame = FrameBuilder.Ethernet(FrameBuilder.ServerIp, 13328, FrameBuilder.ClientIp, 50000, 42, [1, 2, 3], padding: 9);
        Assert.True(PacketDissector.TryParse(PacketDissector.DltEthernet, frame, out var segment, out var payload));
        Assert.Equal([1, 2, 3], payload.ToArray());
        Assert.Equal(42u, segment.Sequence);
        Assert.Equal(13328, segment.Flow.Source.Port);
        Assert.Equal("206.127.156.10:13328", segment.Flow.Source.ToString());
    }

    [Fact]
    public void ParsesLoopbackNullHeader()
    {
        byte[] ethernet = FrameBuilder.Ethernet(0x7F000001, 1000, 0x7F000001, 2000, 7, [9]);
        byte[] loopback = [0x02, 0x00, 0x00, 0x00, .. ethernet[14..]];
        Assert.True(PacketDissector.TryParse(PacketDissector.DltNull, loopback, out var segment, out var payload));
        Assert.Equal(2000, segment.Flow.Destination.Port);
        Assert.Equal([9], payload.ToArray());
    }
}

public class ReassemblerTests
{
    private static (TcpReassembler Reassembler, List<byte> Output) Create()
    {
        var output = new List<byte>();
        return (new TcpReassembler(data => output.AddRange(data.ToArray())), output);
    }

    [Fact]
    public void ReordersAndDeduplicates()
    {
        var (r, output) = Create();
        r.Push(100, TcpFlags.Ack, [1, 2], 0);
        r.Push(104, TcpFlags.Ack, [5, 6], 0);   // future
        r.Push(100, TcpFlags.Ack, [1, 2], 0);   // retransmit
        r.Push(101, TcpFlags.Ack, [2, 3, 4], 0); // overlaps by one
        Assert.Equal([1, 2, 3, 4, 5, 6], output);
        Assert.Equal(1, r.Retransmits);
    }

    [Fact]
    public void HandlesSequenceWraparound()
    {
        var (r, output) = Create();
        r.Push(uint.MaxValue - 1, TcpFlags.Ack, [1, 2], 0);
        r.Push(1, TcpFlags.Ack, [4], 0);
        r.Push(0, TcpFlags.Ack, [3], 0);
        Assert.Equal([1, 2, 3, 4], output);
    }

    [Fact]
    public void SkipsGapThatNeverFills()
    {
        var (r, output) = Create();
        r.Push(0, TcpFlags.Ack, [1], 0);
        r.Push(10, TcpFlags.Ack, [2], 100);
        r.Push(11, TcpFlags.Ack, [3], 100 + TcpReassembler.MaxGapWaitMs + 1);
        Assert.Equal([1, 2, 3], output);
        Assert.Equal(1, r.GapSkips);
    }
}

public class PipelineTests
{
    private static readonly byte[] Idle = Frame("0e36 0e0036 0e0036");

    [Fact]
    public void LocksOnSignatureAndDecodes()
    {
        var events = new List<GameEvent>();
        var pipeline = new GameCapturePipeline(events.Add);
        uint seq = 1000;
        long ts = 0;

        // Physical flows need the loopback grace period before locking.
        for (int i = 0; i < 40; i++, ts += 100)
        {
            pipeline.Process(FrameBuilder.Server(seq, Idle, ts));
            seq += (uint)Idle.Length;
        }

        Assert.NotNull(pipeline.Detector.Locked);
        Assert.Equal(13328, pipeline.Detector.Locked!.Flow.Source.Port);

        byte[] death = Frame("4236 ccc801 00 03");
        pipeline.Process(FrameBuilder.Server(seq, death, ts));
        Assert.IsType<DeathEvent>(Assert.Single(events));
    }

    [Fact]
    public void DecodesWhatTheConnectionSentBeforeTheLock()
    {
        // The game sends identity, map and inventory right after connecting, before enough signature hits to lock
        var events = new List<GameEvent>();
        var pipeline = new GameCapturePipeline(events.Add);
        uint seq = 1000;
        long ts = 0;

        byte[] early = Frame("4236 ccc801 00 03");
        pipeline.Process(FrameBuilder.Server(seq, early, ts));
        seq += (uint)early.Length;
        Assert.Null(pipeline.Detector.Locked);

        for (int i = 0; i < 40; i++, ts += 100)
        {
            pipeline.Process(FrameBuilder.Server(seq, Idle, ts));
            seq += (uint)Idle.Length;
        }

        Assert.NotNull(pipeline.Detector.Locked);
        Assert.IsType<DeathEvent>(Assert.Single(events));
    }

    [Fact]
    [Trait("Category", "Npcap")] // needs wpcap.dll (not on CI runners)
    public void RecordsAndReplaysPcapEndToEnd()
    {
        string path = Path.Combine(Path.GetTempPath(), $"avion2-test-{Guid.NewGuid():N}.pcap");
        try
        {
            var live = new GameCapturePipeline(_ => { });
            using (var recorder = new PcapRecorder(path))
            {
                uint seq = 1;
                long ts = 1_700_000_000_000;
                byte[] self = Frame("3336 ed74 5e91c128 37 06 4e6169636861 1805 1e000000 01 1c000000");
                byte[] hit = Frame("0438 b1ea01 36 00 ed74 40c0f400 7a 03 80 00 01 0b199b5f 01000000 ac52 a40d 02 18 18");
                foreach (byte[] payload in Enumerable.Repeat(Idle, 30).Append(self).Append(Bundle(hit, hit)))
                {
                    var frame = FrameBuilder.Server(seq, payload, ts);
                    live.Process(frame);
                    recorder.Write(frame, live.Detector.Locked);
                    seq += (uint)payload.Length;
                    ts += 100;
                }
            }

            var events = new List<GameEvent>();
            var replay = new GameCapturePipeline(events.Add);
            foreach (var frame in PcapFile.Read(path))
            {
                replay.Process(frame);
            }

            Assert.Equal("Naicha", events.OfType<SelfIdentityEvent>().Single().Name);
            var hits = events.OfType<DamageEvent>().ToList();
            Assert.Equal(2, hits.Count);
            Assert.All(hits, h => Assert.Equal(14957u, h.ActorId));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IgnoresTlsConnections()
    {
        var pipeline = new GameCapturePipeline(_ => { });
        byte[] tls = [0x17, 0x03, 0x03, 0x00, 0x20, .. Idle, .. new byte[32 - Idle.Length]];
        for (int i = 0; i < 40; i++)
        {
            pipeline.Process(FrameBuilder.Server((uint)(i * tls.Length), tls, i * 100));
        }

        Assert.Null(pipeline.Detector.Locked);
    }

    [Fact]
    public void LoopbackLocksImmediately()
    {
        var pipeline = new GameCapturePipeline(_ => { });
        for (int i = 0; i < GameConnectionDetector.SignatureLockThreshold; i++)
        {
            pipeline.Process(FrameBuilder.Server((uint)(i * Idle.Length), Idle, i * 10, device: 1, loopback: true));
        }

        Assert.True(pipeline.Detector.Locked?.IsLoopback);
    }
}

public class RecorderTests
{
    [Fact]
    [Trait("Category", "Npcap")] // needs wpcap.dll (not on CI runners)
    public void RecordsOnlyTheGameConnection()
    {
        string path = Path.Combine(Path.GetTempPath(), $"avion2-rec-{Guid.NewGuid():N}.pcap");
        try
        {
            PacketDissector.TryParse(PacketDissector.DltEthernet, FrameBuilder.Server(1, [1], 0).Data, out var game, out _);
            var locked = new LockedFlow(0, false, game.Flow, 0);
            using (var recorder = new PcapRecorder(path))
            {
                // Before the lock (buffered), the game and a browser; after it, both directions of the game and the browser again.
                recorder.Write(FrameBuilder.Server(1, [1, 2, 3], 1_000), null);
                recorder.Write(Browser(1_100), null);
                recorder.Write(FrameBuilder.Server(4, [4], 2_000), locked);
                recorder.Write(new RawFrame(0, PacketDissector.DltEthernet, false, 2_100,
                    FrameBuilder.Ethernet(FrameBuilder.ClientIp, 50000, FrameBuilder.ServerIp, 13328, 9, [7])), locked);
                recorder.Write(Browser(2_200), locked);
                Assert.Equal(3, recorder.Written);
            }

            var ports = PcapFile.Read(path)
                .Select(f => PacketDissector.TryParse(f.LinkType, f.Data, out var s, out _) ? s.Flow.Source.Port : 0)
                .ToList();
            Assert.Equal([13328, 13328, 50000], ports);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static RawFrame Browser(long ts) =>
        new(0, PacketDissector.DltEthernet, false, ts, FrameBuilder.Ethernet(0x68108A01, 443, FrameBuilder.ClientIp, 51515, 1, [0x17, 0x03, 0x03]));
}
