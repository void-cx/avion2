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
}
