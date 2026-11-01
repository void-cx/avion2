using Avion2.Analysis;
using Avion2.Protocol;
using Avion2.Protocol.Parsers;

namespace Avion2.Tests;

public class SkillStateTests
{
    [Fact]
    public void ChainRecordsOpenAndCloseTheNextSkill()
    {
        // As recorded: Onslaught (18010000) opens Resonance Crush (18020000), which later closes
        var events = new List<GameEvent>();
        Assert.True(RecordParsers.ParseChainOpen(Convert.FromHexString("90cf120101a0f61201"), 0, events.Add));
        Assert.True(RecordParsers.ParseChainClose(Convert.FromHexString("01a0f61201"), 0, events.Add));
        Assert.Equal(new ChainStepEvent(0, 18_010_000, 18_020_000, true), events[0]);
        Assert.Equal(new ChainStepEvent(0, 0, 18_020_000, false), events[1]);
    }

    [Fact]
    public void TargetOfTargetIsTheEntityAndWhomItTargets()
    {
        // The training dummy (39364) targeting nobody, then us (2041)
        var events = new List<GameEvent>();
        Assert.True(RecordParsers.ParseTargetOfTarget(Convert.FromHexString("c4b3020000"), 0, events.Add));
        Assert.True(RecordParsers.ParseTargetOfTarget(Convert.FromHexString("c4b30200f90f"), 0, events.Add));
        Assert.Equal(new TargetOfTargetEvent(0, 39_364, 0), events[0]);
        Assert.Equal(new TargetOfTargetEvent(0, 39_364, 2_041), events[1]);
    }

    [Fact]
    public void SelectedTargetFollowsThreeZeroBytes()
    {
        var events = new List<GameEvent>();
        Assert.True(RecordParsers.ParseTargetSelected(Convert.FromHexString("000000c4b302"), 0, events.Add));
        Assert.True(RecordParsers.ParseTargetSelected(Convert.FromHexString("00000000"), 0, events.Add)); // cleared
        Assert.Equal([new TargetSelectedEvent(0, 39_364), new TargetSelectedEvent(0, 0)], events);
    }

    [Fact]
    public void ChainStepsRunOutAfterThreeSeconds()
    {
        var chains = new ChainSteps();
        chains.Apply(new ChainStepEvent(1_000, 18_010_000, 18_020_000, true));
        chains.Apply(new ChainStepEvent(1_500, 18_040_000, 18_050_000, true));

        var open = chains.Open(2_000, id => $"Skill {id}");
        Assert.Equal([18_020_000u, 18_050_000u], open.Select(s => s.SkillId));
        Assert.Equal(2_000, open[0].LeftMs);

        chains.Apply(new ChainStepEvent(2_200, 0, 18_050_000, false)); // used
        Assert.Equal(18_020_000u, Assert.Single(chains.Open(2_500, id => "")).SkillId);
        Assert.Empty(chains.Open(4_000, id => "")); // closing record lost: gone after 3 s anyway
    }
}
