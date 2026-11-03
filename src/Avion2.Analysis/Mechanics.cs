using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>A drop of an entity's stagger gauge, credited to the hit that caused it.</summary>
/// <param name="ActorId">Raw actor of the hit (a summon resolves to its owner when a view is built).</param>
/// <param name="SkillId">Base skill id of the hit (as in <see cref="DamageEvent.SkillId"/>).</param>
/// <param name="Broke">This drop emptied the gauge.</param>
public sealed record StaggerCredit(long TimestampMs, uint TargetId, uint ActorId, uint SkillId, uint Amount, bool Broke);

/// <summary>
/// Credits stagger gauge drops (<c>05 E0</c>) to players. A hit that takes stagger is followed by a value-less
/// <c>04 38</c> entry of the same actor and skill, which marks it; each drop goes to the oldest marked hit not yet
/// credited since the gauge's last update, else to the last hit since then (checked against captures: 97–98 % of
/// the drops then match the skill's usual amount). Not thread-safe.
/// </summary>
public sealed class StaggerTracker
{
    private sealed class Pending(DamageEvent hit)
    {
        public DamageEvent Hit { get; } = hit;
        public bool Marked { get; set; }
        public bool Taken { get; set; }
    }

    private sealed class Gauge
    {
        public uint? Left;
        public List<Pending> Batch { get; } = new();
        public bool Updated;
    }

    private readonly Dictionary<uint, Gauge> _gauges = new();

    /// <summary>Feeds an event; returns the credit when it was a gauge drop or break with a hit to credit.</summary>
    public StaggerCredit? Apply(GameEvent e)
    {
        switch (e)
        {
            case DamageEvent hit when (hit.Flags & HitFlags.OverTime) == 0:
                // Any direct record on the entity after an update starts the next batch; misses take nothing
                if (_gauges.TryGetValue(hit.TargetId, out var gauge))
                {
                    var hits = BatchOf(gauge);
                    if (hit.Amount > 0 && IsCandidate(hit.ActorId, hit.TargetId, hit.RawSkillId))
                    {
                        hits.Add(new Pending(hit));
                    }
                }

                return null;
            case ValuelessHitEvent mark:
                if (_gauges.TryGetValue(mark.TargetId, out var marked))
                {
                    var batch = BatchOf(marked);
                    if (!IsCandidate(mark.ActorId, mark.TargetId, mark.RawSkillId))
                    {
                        return null;
                    }

                    for (int i = batch.Count - 1; i >= 0; i--)
                    {
                        var h = batch[i];
                        if (h.Hit.ActorId == mark.ActorId && h.Hit.RawSkillId == mark.RawSkillId && !h.Marked && !h.Taken)
                        {
                            h.Marked = true;
                            break;
                        }
                    }
                }

                return null;
            case StaggerGaugeEvent update:
                return ApplyUpdate(update);
            case StaggerBrokenEvent broken:
                return ApplyBreak(broken);
            case DeathEvent death:
                _gauges.Remove(death.EntityId);
                return null;
            case ZoneChangeEvent:
                _gauges.Clear();
                return null;
            default:
                return null;
        }
    }

    /// <summary>Player skills on someone else (the raw id range the rule was checked with).</summary>
    private static bool IsCandidate(uint actor, uint target, uint rawSkill) =>
        actor != target && rawSkill is >= 10_000_000 and <= 29_999_999;

    /// <summary>The hits since the last update: the first hit after an update starts a new batch.</summary>
    private static List<Pending> BatchOf(Gauge gauge)
    {
        if (gauge.Updated)
        {
            gauge.Batch.Clear();
            gauge.Updated = false;
        }

        return gauge.Batch;
    }

    private StaggerCredit? ApplyUpdate(StaggerGaugeEvent update)
    {
        if (!_gauges.TryGetValue(update.EntityId, out var gauge))
        {
            // First sight: what it was before is not known, so this drop is not credited.
            _gauges[update.EntityId] = new Gauge { Left = update.Left, Updated = true };
            return null;
        }

        uint? before = gauge.Left;
        gauge.Left = update.Left;
        StaggerCredit? credit = null;
        if (before is { } was && was > update.Left)
        {
            credit = Credit(gauge, update.EntityId, was - update.Left, update.TimestampMs, broke: false);
        }

        gauge.Updated = true;
        return credit;
    }

    private StaggerCredit? ApplyBreak(StaggerBrokenEvent broken)
    {
        if (!_gauges.TryGetValue(broken.EntityId, out var gauge))
        {
            _gauges[broken.EntityId] = new Gauge { Left = null, Updated = true };
            return null;
        }

        uint amount = gauge.Left ?? 0;
        gauge.Left = null; // refills to the maximum about 5 s later; that update is not a drop
        var credit = Credit(gauge, broken.EntityId, amount, broken.TimestampMs, broke: true);
        gauge.Updated = true;
        return credit;
    }

    private static StaggerCredit? Credit(Gauge gauge, uint entity, uint amount, long ts, bool broke)
    {
        if (gauge.Updated)
        {
            return null; // no hit since the last update
        }

        var batch = gauge.Batch;
        var pick = batch.FirstOrDefault(h => h.Marked && !h.Taken) ?? batch.LastOrDefault(h => !h.Taken);
        if (pick is null)
        {
            return null;
        }

        pick.Taken = true;
        return new StaggerCredit(ts, entity, pick.Hit.ActorId, pick.Hit.SkillId, amount, broke);
    }
}

/// <summary>An NPC's cast with a cast time, as known now.</summary>
/// <param name="EndMs">When the skill goes off (start + cast time).</param>
/// <param name="StoppedMs">When it was stopped early, if it was.</param>
/// <param name="Interrupted">Stopped early while the caster lives on.</param>
public sealed record NpcCast(uint CasterId, uint SkillId, long StartMs, long EndMs, long? StoppedMs = null, bool Interrupted = false)
{
    public int CastMs => (int)(EndMs - StartMs);
}

/// <summary>Running and recently stopped NPC casts (<c>09 38</c> / <c>0C 38</c>), for the live boss cast bar.</summary>
public sealed class NpcCastTracker
{
    /// <summary>How long an interrupt stays visible.</summary>
    public const long InterruptShownMs = 3_000;

    private readonly Dictionary<uint, NpcCast> _casts = new();

    public void Apply(GameEvent e)
    {
        switch (e)
        {
            case NpcCastEvent start:
                _casts[start.CasterId] = new NpcCast(start.CasterId, start.SkillId, start.TimestampMs, start.TimestampMs + start.CastMs);
                break;
            case NpcCastStopEvent stop when _casts.TryGetValue(stop.CasterId, out var cast) && cast.SkillId == stop.SkillId && cast.StoppedMs is null:
                _casts[stop.CasterId] = cast with { StoppedMs = stop.TimestampMs, Interrupted = !stop.CasterDied };
                break;
            case SkillCastEvent went when _casts.TryGetValue(went.ActorId, out var open) && open.SkillId == went.SkillId && open.StoppedMs is null:
                _casts.Remove(went.ActorId); // the skill went off
                break;
            case DeathEvent death:
                _casts.Remove(death.EntityId);
                break;
            case ZoneChangeEvent:
                _casts.Clear();
                break;
        }
    }

    /// <summary>
    /// The cast to show for <paramref name="caster"/> at <paramref name="nowMs"/>: one still running, or one
    /// interrupted less than <see cref="InterruptShownMs"/> ago. Null otherwise.
    /// </summary>
    public NpcCast? Of(uint caster, long nowMs)
    {
        if (!_casts.TryGetValue(caster, out var cast))
        {
            return null;
        }

        if (cast.StoppedMs is { } stopped)
        {
            return cast.Interrupted && nowMs - stopped <= InterruptShownMs ? cast : null;
        }

        // A skill that went off out of sight (no 02 38) still ends: allow a little lateness
        return nowMs <= cast.EndMs + 500 ? cast : null;
    }
}

/// <summary>Dodges from skill casts (<c>02 38</c>): CC000100 Dodge and CC000101 its sprinting form.</summary>
public static class DodgeSkills
{
    /// <summary>1 a dodge, 2 a dodge while sprinting (or out of combat), 0 not a dodge.</summary>
    public static int Kind(uint rawSkill) => rawSkill is >= 11_000_000 and <= 29_999_999
        ? (rawSkill % 1_000_000) switch { 100 => 1, 101 => 2, _ => 0 }
        : 0;
}
