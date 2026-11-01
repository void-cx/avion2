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
    public void RecentAbortsCountForFiveMinutes()
    {
        var limit = new GatherLimit();
        limit.Aborted(0);
        limit.Aborted(48_000);
        limit.Aborted(156_000);
        Assert.Equal(GatherLimit.WarnAt, limit.RecentAborts(160_000)); // the next one blocks

        Assert.Equal(2, limit.RecentAborts(GatherLimit.AbortWindowMs + 1_000)); // the first is over 5 min old

        // A block does not start the count over: recorded, the next abort 104 s after one blocked again
        var again = new GatherLimit();
        foreach (long ms in new long[] { 0, 106_000, 170_000, 274_000 })
        {
            again.Aborted(ms);
        }

        again.Apply(new BuffEvent(276_000, 2041, 1, GatherLimit.BlockAbnormal, null, 2041, 60_000, true, null));
        Assert.Equal(GatherLimit.WarnAt, again.RecentAborts(378_000));
    }
}
