using Avion2.Analysis;
using Avion2.Protocol;

namespace Avion2.Tests;

public class GatherLimitTests
{
    [Fact]
    public void TheBlockLastsAsLongAsTheDebuff()
    {
        // As recorded: abnormal 10020 on us for 60 s, removed by instance 11528 when it ran out
        var limit = new GatherLimit();
        limit.Apply(new BuffEvent(1_000, 2041, 11528, GatherLimit.BlockAbnormal, null, 2041, 60_000, true, null));
        Assert.Equal(45_000, limit.BlockedMs(16_000));

        limit.Apply(new BuffRemovedEvent(61_000, 2041, [11528]));
        Assert.Null(limit.BlockedMs(61_000));
    }

    [Fact]
    public void OtherDebuffsDoNotBlock()
    {
        var limit = new GatherLimit();
        limit.Apply(new BuffEvent(1_000, 2041, 1, 10021, null, 2041, 60_000, true, null));
        Assert.Null(limit.BlockedMs(2_000));
    }

    [Fact]
    public void RecentAbortsCountUntilTheBlock()
    {
        var limit = new GatherLimit();
        limit.Aborted(0);
        limit.Aborted(48_000);
        limit.Aborted(156_000);
        Assert.Equal(GatherLimit.WarnAt, limit.RecentAborts(160_000)); // the next one may block

        Assert.Equal(2, limit.RecentAborts(GatherLimit.AbortWindowMs + 1_000)); // the first is old enough to forget

        limit.Apply(new BuffEvent(182_000, 2041, 1, GatherLimit.BlockAbnormal, null, 2041, 60_000, true, null));
        Assert.Equal(0, limit.RecentAborts(182_000)); // counting starts over after a block
    }
}
