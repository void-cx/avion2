using Avion2.Protocol;
using Avion2.Protocol.Parsers;

namespace Avion2.Tests;

public class GatherTests
{
    [Theory]
    // start records as recorded (header and the first step): node 38571, attempt, outcome flags, grade
    [InlineData("0000abad0201000000010000010000000805016400000001690000000000000000", 1u, true, false)]
    [InlineData("0000abad0203000000070000020000000804012003000001250300003200000000", 3u, true, true)]
    [InlineData("0000abad0201000000000000010000000604012003000001250300003200000000", 1u, false, false)]
    [InlineData("0000abad0201000000060000020000000604012003000001250300003200000000", 1u, false, true)]
    public void TheOutcomeIsInTheStartRecord(string hex, uint attempt, bool success, bool fine)
    {
        var events = new List<GameEvent>();
        Assert.True(RecordParsers.ParseGatherStart(Convert.FromHexString(hex), 0, events.Add));
        var start = Assert.IsType<GatherStartEvent>(Assert.Single(events));
        Assert.Equal((38571u, attempt, success, fine), (start.NodeId, start.Attempt, start.Success, start.Fine));
    }

    [Fact]
    public void TheEndRecordNamesTheItem()
    {
        var events = new List<GameEvent>();
        Assert.True(RecordParsers.ParseGatherEnd(Convert.FromHexString("013aabad02beee652401000000000000000000000000000000"), 0, events.Add));
        var end = Assert.IsType<GatherEndEvent>(Assert.Single(events));
        Assert.Equal((38571u, (byte)1, 610660030u, 1u), (end.NodeId, end.Result, end.ItemId, end.Count));
    }
}
