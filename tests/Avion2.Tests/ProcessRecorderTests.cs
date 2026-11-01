using Avion2.Capture;

namespace Avion2.Tests;

public class ProcessRecorderTests
{
    [Fact]
    public void ReadsTheConnectionTableOfAProcess()
    {
        // MIB_TCPTABLE_OWNER_PID with two rows: 192.168.1.5:54000 → 193.0.2.7:13328 (pid 42) and one of pid 7
        byte[] table = Convert.FromHexString(
            "02000000" +
            "05000000" + "c0a80105" + "d2f00000" + "c1000207" + "34100000" + "2a000000" +
            "05000000" + "c0a80105" + "d2f10000" + "c1000207" + "01bb0000" + "07000000");
        var flows = new HashSet<FlowKey>();
        ProcessConnections.Parse(table, v6: false, new HashSet<int> { 42 }, flows);
        Assert.Equal(2, flows.Count);
        Assert.Contains(flows, f => f.ToString() == "192.168.1.5:54000 -> 193.0.2.7:13328");
        Assert.Contains(flows, f => f.ToString() == "193.0.2.7:13328 -> 192.168.1.5:54000");
    }

    [Theory]
    [InlineData("0b0d390000002905", true)]                 // 0D 39 (server chosen): L = 11 → an 8-byte frame
    [InlineData("0b0d3900000029050b0d390000002905", true)] // the segment may hold more frames
    [InlineData("0b0d39000000290517fe", false)] // a lobby-like start, then bytes that are no frame
    [InlineData("0a000036e2294196a10100", false)] // 00 36 (world clock), not the lobby
    [InlineData("160303004a", false)]             // TLS
    public void RecognisesLobbyPackets(string hex, bool lobby) =>
        Assert.Equal(lobby, ProcessRecorder.IsLobby(Convert.FromHexString(hex)));

    [Fact]
    public void DoesNotRecordAnotherProgramsConnectionThatLooksLikeTheLobbyMidStream()
    {
        string path = Path.Combine(Path.GetTempPath(), $"avion2-proc-{Guid.NewGuid():N}.pcap");
        try
        {
            using (var recorder = new ProcessRecorder(path, ["avion2-no-such-process"]))
            {
                // An HTTPS download: the handshake, then encrypted data, one segment of which reads as a lobby packet
                recorder.Write(Download(1, [0x16, 0x03, 0x03, 0x00, 0x4a], 1_000), null);
                recorder.Write(Download(6, Convert.FromHexString("0b0d390000002905"), 1_100), null);
                recorder.Write(Download(14, [0x17, 0x03, 0x03], 1_200), null);
                Assert.Equal(0, recorder.Written);
            }

            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    [Trait("Category", "Npcap")] // needs wpcap.dll (not on CI runners)
    public void RecordsALobbyOfAnotherProcessFromItsFirstPacket()
    {
        string path = Path.Combine(Path.GetTempPath(), $"avion2-proc-{Guid.NewGuid():N}.pcap");
        try
        {
            using (var recorder = new ProcessRecorder(path, ["avion2-no-such-process"]))
            {
                recorder.Write(Lobby(0, [], 1_000, TcpFlags.Syn | TcpFlags.Ack), null); // held until the lobby is known
                recorder.Write(Lobby(1, Convert.FromHexString("0b0d390000002905"), 1_050), null);
                recorder.Write(Lobby(9, [0x17, 0x03, 0x03], 1_100), null); // later data of the lobby: recorded too
                Assert.Equal(3, recorder.Written);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static RawFrame Download(uint seq, byte[] payload, long ts) =>
        new(0, PacketDissector.DltEthernet, false, ts, FrameBuilder.Ethernet(0xB9C76F85, 443, FrameBuilder.ClientIp, 16243, seq, payload));

    private static RawFrame Lobby(uint seq, byte[] payload, long ts, TcpFlags flags = TcpFlags.Ack | TcpFlags.Psh) =>
        new(0, PacketDissector.DltEthernet, false, ts, FrameBuilder.Ethernet(FrameBuilder.ServerIp, 13700, FrameBuilder.ClientIp, 54795, seq, payload, flags));
}
