using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>A chain skill we can use right now, and for how much longer.</summary>
public sealed record ChainStep(uint SkillId, string Name, long LeftMs);

/// <summary>
/// Our open chain steps (<c>18 38</c> / <c>19 38</c>): after e.g. Onslaught, Resonance Crush can be used for 3 s. The
/// server closes a step when it is used or runs out; steps are also dropped here once their 3 s are over, in case the
/// closing record is lost.
/// </summary>
public sealed class ChainSteps
{
    /// <summary>How long a step stays open; recorded closings by expiry came 3.0–3.05 s after the opening.</summary>
    public const long WindowMs = 3_000;

    private readonly Dictionary<uint, long> _opened = new();

    public void Apply(ChainStepEvent e)
    {
        if (e.Open)
        {
            _opened[e.Skill] = e.TimestampMs;
        }
        else
        {
            _opened.Remove(e.Skill);
        }
    }

    public void Clear() => _opened.Clear();

    /// <summary>The open steps, the first to run out first.</summary>
    public IReadOnlyList<ChainStep> Open(long nowMs, Func<uint, string> name)
    {
        foreach (var (skill, _) in _opened.Where(o => nowMs - o.Value >= WindowMs).ToList())
        {
            _opened.Remove(skill);
        }

        return _opened.OrderBy(o => o.Value)
            .Select(o => new ChainStep(o.Key, name(o.Key), WindowMs - (nowMs - o.Value)))
            .ToList();
    }
}
