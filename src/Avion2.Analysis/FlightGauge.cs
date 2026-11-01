namespace Avion2.Analysis;

/// <summary>
/// Our flight time (<c>00 8D</c> resource 6, in 1/10 000 s, 570 000 when full): how long until we land at the rate it
/// is draining. While flying it updates every 100 ms (gliding drains slower than flying, a boost faster); on the
/// ground it refills once a second.
/// </summary>
public sealed class FlightGauge
{
    /// <summary>
    /// The rate is averaged over this long, so gliding's big drops (10 000 every ~4 s) don't make it jump; checked on a
    /// recorded flight, it then stays within about a second of when we landed.
    /// </summary>
    private const long WindowMs = 4_500;

    /// <summary>Nothing shown before this much flying: the take-off cost (2 500 at once) would skew the rate.</summary>
    private const long MinSpanMs = 1_000;

    /// <summary>No drain for this long: we landed (or the updates stopped).</summary>
    private const long StaleMs = 500;

    private readonly Queue<(long Ms, uint Value)> _recent = new();

    public void Update(uint value, long nowMs)
    {
        if (_recent.Count > 0 && value > _recent.Last().Value)
        {
            _recent.Clear(); // refilling: on the ground
        }

        _recent.Enqueue((nowMs, value));
        while (_recent.Count > 2 && nowMs - _recent.Peek().Ms > WindowMs)
        {
            _recent.Dequeue();
        }
    }

    /// <summary>Milliseconds of flight left at the current rate; null when we are not flying.</summary>
    public long? LeftMs(long nowMs)
    {
        if (_recent.Count < 2)
        {
            return null;
        }

        var (firstMs, first) = _recent.Peek();
        var (lastMs, last) = _recent.Last();
        if (last == 0 || nowMs - lastMs > StaleMs || lastMs - firstMs < MinSpanMs || first <= last)
        {
            return null;
        }

        double perMs = (double)(first - last) / (lastMs - firstMs);
        return Math.Max(0, (long)(last / perMs) - (nowMs - lastMs));
    }
}
