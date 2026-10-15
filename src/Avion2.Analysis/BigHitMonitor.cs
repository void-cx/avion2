namespace Avion2.Analysis;

/// <summary>
/// Notices when you lose a big share of your HP at once: at least <see cref="Share"/> of your max HP within
/// <see cref="WindowMs"/> (one heavy hit or a burst). Drives the HUD's red screen-edge flash.
/// </summary>
public sealed class BigHitMonitor
{
    private readonly Queue<(long Ms, uint Hp)> _samples = new();
    private long _lastFlashMs = long.MinValue / 2;

    /// <summary>Fraction of max HP, e.g. 0.15 for 15 %.</summary>
    public double Share { get; set; } = 0.15;

    public long WindowMs { get; set; } = 1_000;

    public long CooldownMs { get; set; } = 1_500;

    /// <summary>Returns true when a flash should start now.</summary>
    public bool Update(uint hp, uint maxHp, long nowMs)
    {
        if (maxHp == 0 || hp == 0)
        {
            _samples.Clear(); // unknown, or dead: start over
            return false;
        }

        while (_samples.Count > 0 && nowMs - _samples.Peek().Ms > WindowMs)
        {
            _samples.Dequeue();
        }

        _samples.Enqueue((nowMs, hp));
        uint highest = _samples.Max(s => s.Hp);
        if (highest <= hp || (highest - hp) / (double)maxHp < Share || nowMs - _lastFlashMs < CooldownMs)
        {
            return false;
        }

        _lastFlashMs = nowMs;
        _samples.Clear(); // the same drop flashes once
        _samples.Enqueue((nowMs, hp));
        return true;
    }
}
