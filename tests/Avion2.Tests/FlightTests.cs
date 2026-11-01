using Avion2.Analysis;
using Avion2.Protocol;
using Avion2.Protocol.Parsers;

namespace Avion2.Tests;

public class FlightTests
{
    [Fact]
    public void ResourceSixIsFlightTime()
    {
        // 00 8D as recorded: us (2041), 01 01, resource 6, 567 500 right after taking off
        var events = new List<GameEvent>();
        Assert.True(RecordParsers.ParseRemainingHp(Convert.FromHexString("f90f010106cca80800"), 0, events.Add));
        var flight = Assert.IsType<FlightTimeEvent>(Assert.Single(events));
        Assert.Equal((2041u, 567_500u), (flight.EntityId, flight.Value));
    }

    [Fact]
    public void ResourceZeroIsStillHp()
    {
        var events = new List<GameEvent>();
        Assert.True(RecordParsers.ParseRemainingHp(Convert.FromHexString("f90f010100763e0000"), 0, events.Add));
        Assert.Equal(15_990u, Assert.IsType<HpUpdateEvent>(Assert.Single(events)).Hp);
    }

    [Fact]
    public void TimeLeftFollowsTheDrain()
    {
        var gauge = new FlightGauge();
        uint value = 300_000;
        long ms = 0;
        for (int i = 0; i < 20; i++, ms += 100)
        {
            gauge.Update(value, ms);
            value -= 1_500; // flying: 15 000 a second
        }

        // 271 500 left after the last update, at 15 per ms
        Assert.InRange(gauge.LeftMs(ms - 100)!.Value, 18_000, 18_200);

        for (int i = 0; i < 50; i++, ms += 100)
        {
            gauge.Update(value, ms);
            value -= 750; // gliding: half the drain
        }

        // 233 250 left, at 7.5 per ms once the window holds only gliding
        Assert.InRange(gauge.LeftMs(ms - 100)!.Value, 30_500, 31_500);
    }

    [Fact]
    public void NothingRightAfterTakingOffOrOnTheGround()
    {
        var gauge = new FlightGauge();
        gauge.Update(567_500, 0);
        gauge.Update(566_500, 100);
        Assert.Null(gauge.LeftMs(100)); // too little to tell the rate

        uint value = 566_500;
        for (long ms = 200; ms <= 1_200; ms += 100)
        {
            gauge.Update(value -= 1_000, ms);
        }

        Assert.NotNull(gauge.LeftMs(1_200));
        Assert.Null(gauge.LeftMs(2_000)); // no update for a while: landed

        gauge.Update(4_000, 2_100); // refilling
        gauge.Update(8_000, 3_100);
        Assert.Null(gauge.LeftMs(3_100));
    }
}
