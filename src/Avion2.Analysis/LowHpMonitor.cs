namespace Avion2.Analysis;

/// <summary>
/// Decides when to warn about low HP: fires once when HP drops below <see cref="Threshold"/>, then re-arms
/// only after HP recovers above the threshold plus a margin, with a cooldown against flapping.
/// </summary>
public sealed class LowHpMonitor
{
    private bool _armed = true;
    private long _lastAlertMs = long.MinValue / 2;

    /// <summary>Fraction of max HP, e.g. 0.3 for 30%.</summary>
    public double Threshold { get; set; } = 0.3;

    public double RearmMargin { get; set; } = 0.05;

    public long CooldownMs { get; set; } = 8_000;

    /// <summary>Returns true when an alert should fire now.</summary>
    public bool Update(uint hp, uint maxHp, long nowMs)
    {
        if (maxHp == 0 || hp == 0)
        {
            return false; // unknown, or dead
        }

        double ratio = (double)hp / maxHp;
        if (ratio >= Threshold + RearmMargin)
        {
            _armed = true;
            return false;
        }

        if (!_armed || ratio >= Threshold || nowMs - _lastAlertMs < CooldownMs)
        {
            return false;
        }

        _armed = false;
        _lastAlertMs = nowMs;
        return true;
    }
}
