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

    [Theory]
    // start records as recorded (header and the first race): cooking station 18103, recipe 514086002
    [InlineData("0005b78d017254a41e01000203008f0000008f00000002000101000001010000", true, false)]
    [InlineData("0005b78d017254a41e0d000704003e0000003e00000003008800000088000000", true, true)]
    [InlineData("0005b78d017254a41e0500050500a8000000a8000000", false, true)]
    public void TheCraftOutcomeIsInTheStartRecord(string hex, bool success, bool combo)
    {
        var events = new List<GameEvent>();
        Assert.True(RecordParsers.ParseCraftStart(Convert.FromHexString(hex), 0, events.Add));
        var start = Assert.IsType<CraftStartEvent>(Assert.Single(events));
        Assert.Equal((18103u, 514086002u, success, combo), (start.StationId, start.RecipeId, start.Success, start.Combo));
    }

    [Fact]
    public void TheCraftEndRecordHasTheResult()
    {
        // a failed combo at alchemy station 34277
        var events = new List<GameEvent>();
        Assert.True(RecordParsers.ParseCraftEnd(Convert.FromHexString("0205e58b0200"), 0, events.Add));
        var end = Assert.IsType<CraftEndEvent>(Assert.Single(events));
        Assert.Equal((34277u, (byte)2), (end.StationId, end.Result));
    }

    [Fact]
    public void AnAbortedGatheringIsAnInterruptedUse()
    {
        // 39 36 as recorded: node 41082, 06 19, us (9710), attempt 3
        var events = new List<GameEvent>();
        Assert.True(RecordParsers.ParseInteractionInterrupted(Convert.FromHexString("fac0020619ee4b03000000"), 0, events.Add));
        var stop = Assert.IsType<InteractionInterruptedEvent>(Assert.Single(events));
        Assert.Equal((41082u, 9710u, 3u), (stop.ObjectId, stop.ActorId, stop.Attempt));
    }
}
