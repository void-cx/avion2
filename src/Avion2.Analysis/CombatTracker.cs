using Avion2.Protocol;

namespace Avion2.Analysis;

public sealed class SkillStats(uint skillId)
{
    public uint SkillId { get; } = skillId;
    public long Total { get; internal set; }
    public int Hits { get; internal set; }
    public int Crits { get; internal set; }
    public int BackHits { get; internal set; }
    public int Perfects { get; internal set; }
    public int Doubles { get; internal set; }
    public int MultiHits { get; internal set; }
    public int Parried { get; internal set; }
    public long MultiHitDamage { get; internal set; }
    public long MaxHit { get; internal set; }
    public long MinHit { get; internal set; }
    public bool IsOverTime { get; internal set; }

    /// <summary>Union of specialisation slots seen (bit n = slot n), see <see cref="SkillIds.SpecSlots"/>.</summary>
    public int SpecSlots { get; internal set; }

    internal void Merge(SkillStats other)
    {
        MinHit = MinHit == 0 ? other.MinHit : other.MinHit == 0 ? MinHit : Math.Min(MinHit, other.MinHit);
        Parried += other.Parried;
        MultiHitDamage += other.MultiHitDamage;
        SpecSlots |= other.SpecSlots;
        Total += other.Total;
        Hits += other.Hits;
        Crits += other.Crits;
        BackHits += other.BackHits;
        Perfects += other.Perfects;
        Doubles += other.Doubles;
        MultiHits += other.MultiHits;
        MaxHit = Math.Max(MaxHit, other.MaxHit);
        IsOverTime |= other.IsOverTime;
    }
}

/// <summary>Damage dealt by one actor (raw entity id, before summon → owner resolution).</summary>
public sealed class ActorStats(uint actorId)
{
    public uint ActorId { get; } = actorId;
    public long Total { get; internal set; }
    public long OverTimeTotal { get; internal set; }
    public int Hits { get; internal set; }
    public int Crits { get; internal set; }
    public int BackHits { get; internal set; }
    public int Perfects { get; internal set; }
    public int Doubles { get; internal set; }
    public int Misses { get; internal set; }
    public int Parried { get; internal set; }
    public int MultiHits { get; internal set; }
    public long FirstMs { get; internal set; }
    public long LastMs { get; internal set; }
    public Dictionary<uint, SkillStats> Skills { get; } = new();

    /// <summary>Direct hits only; DoT ticks cannot crit or land from behind.</summary>
    public int DirectHits { get; internal set; }

    public double CritRate => DirectHits == 0 ? 0 : (double)Crits / DirectHits;

    public double BackRate => DirectHits == 0 ? 0 : (double)BackHits / DirectHits;

    internal void Merge(ActorStats other)
    {
        Total += other.Total;
        OverTimeTotal += other.OverTimeTotal;
        Hits += other.Hits;
        Crits += other.Crits;
        BackHits += other.BackHits;
        Perfects += other.Perfects;
        Doubles += other.Doubles;
        Misses += other.Misses;
        Parried += other.Parried;
        MultiHits += other.MultiHits;
        DirectHits += other.DirectHits;
        FirstMs = FirstMs == 0 ? other.FirstMs : Math.Min(FirstMs, other.FirstMs);
        LastMs = Math.Max(LastMs, other.LastMs);
        foreach (var (id, skill) in other.Skills)
        {
            if (!Skills.TryGetValue(id, out var mine))
            {
                mine = new SkillStats(id);
                Skills[id] = mine;
            }

            mine.Merge(skill);
        }
    }
}

public sealed class TargetStats(uint targetId)
{
    public uint TargetId { get; } = targetId;
    public long Total { get; internal set; }
    public long FirstMs { get; internal set; }
    public long LastMs { get; internal set; }
    public bool Killed { get; internal set; }
    public Dictionary<uint, ActorStats> Actors { get; } = new();
}

/// <summary>Healing done and damage taken, per raw actor id.</summary>
public sealed class SupportStats
{
    public long Healing { get; internal set; }
    public long DamageTaken { get; internal set; }
}

/// <summary>Damage one player took from one attacker with one skill.</summary>
public sealed class IncomingSource(uint attackerId, uint skillId)
{
    public uint AttackerId { get; } = attackerId;
    public uint SkillId { get; } = skillId;
    public long Total { get; internal set; }
    public int Hits { get; internal set; }
    public int Crits { get; internal set; }
    public long MaxHit { get; internal set; }
}

/// <summary>Damage taken by one player (raw entity id), with mitigation counters.</summary>
public sealed class IncomingStats(uint victimId)
{
    public uint VictimId { get; } = victimId;
    public long Total { get; internal set; }
    public int Hits { get; internal set; }
    public int Crits { get; internal set; }
    public int Parried { get; internal set; }
    public int Blocked { get; internal set; }
    public int PerfectBlocks { get; internal set; }
    public int Shielded { get; internal set; }
    public int Misses { get; internal set; }

    /// <summary>Damage soaked by our own shields (reported as an over-time tick carrying our shield skill).</summary>
    public long Absorbed { get; internal set; }

    public Dictionary<(uint Attacker, uint Skill), IncomingSource> Sources { get; } = new();
}

public enum EncounterEnd
{
    Ongoing,
    Idle,
    ZoneChange,
    BossKilled,
    Reset,
}

public sealed class Encounter(int id, long startMs)
{
    public int Id { get; } = id;
    public long StartMs { get; } = startMs;
    public long LastMs { get; internal set; } = startMs;
    public EncounterEnd End { get; internal set; }
    public Dictionary<uint, TargetStats> Targets { get; } = new();
    public Dictionary<uint, SupportStats> Support { get; } = new();
    public Dictionary<uint, IncomingStats> Incoming { get; } = new();

    /// <summary>When we entered combat (<c>21 8D</c>), if that happened just before the first hit.</summary>
    public long? CombatStartMs { get; internal set; }

    /// <summary>When we left combat; the game's Combat Analysis stops its clock here.</summary>
    public long? CombatEndMs { get; internal set; }

    /// <summary>True while we are in combat during this (still running) encounter.</summary>
    public bool SelfInCombat { get; internal set; }

    /// <summary>Our own entity id while this encounter ran (ids are reissued on zone change).</summary>
    public uint? SelfId { get; internal set; }

    /// <summary>Snapshot taken when the encounter closed, with names and owners still resolvable.</summary>
    public MeterSnapshot? Final { get; internal set; }

    public long TotalDamage => Targets.Values.Sum(t => t.Total);

    public long TotalTaken => Incoming.Values.Sum(i => i.Total);

    public double DurationSeconds => Math.Max(LastMs - StartMs, 1000) / 1000.0;
}

public sealed class TrackerOptions
{
    /// <summary>Gap without player damage that closes an encounter.</summary>
    public long EncounterIdleMs { get; init; } = 15_000;

    public int MaxHistory { get; init; } = 100;
}

/// <summary>
/// Aggregates damage/heal events into encounters, per target and per raw actor. Summon → owner
/// merging happens when a view is built, so links that arrive after the first hits still apply.
/// Not thread-safe.
/// </summary>
public sealed class CombatTracker
{
    private readonly EntityRegistry _registry;
    private readonly TrackerOptions _options;
    private readonly List<Encounter> _history = new();
    private int _nextId = 1;
    private bool _selfInCombat;
    private long _selfCombatSince;

    public CombatTracker(EntityRegistry registry, TrackerOptions? options = null)
    {
        _registry = registry;
        _options = options ?? new TrackerOptions();
    }

    public Encounter? Current { get; private set; }

    public IReadOnlyList<Encounter> History => _history;

    public event Action<Encounter>? EncounterEnded;

    public void Apply(GameEvent e)
    {
        switch (e)
        {
            case DamageEvent damage:
                ApplyDamage(damage);
                break;
            case HealEvent heal:
                ApplyHeal(heal);
                break;
            case DeathEvent { Kind: DeathKind.Combat } death:
                ApplyDeath(death);
                break;
            case CombatStateEvent combat when combat.EntityId == _registry.SelfId:
                ApplySelfCombat(combat);
                break;
            case ZoneChangeEvent:
                Close(EncounterEnd.ZoneChange);
                break;
        }
    }

    public void Reset() => Close(EncounterEnd.Reset);

    /// <summary>Closes the current encounter if it has been idle at <paramref name="nowMs"/>.</summary>
    public void Tick(long nowMs)
    {
        if (Current is { } current && nowMs - current.LastMs > _options.EncounterIdleMs)
        {
            Close(EncounterEnd.Idle);
        }
    }

    private void ApplyDamage(DamageEvent damage)
    {
        uint actor = damage.ActorId;
        uint target = damage.TargetId;
        if (_registry.ResolveOwner(actor) == _registry.ResolveOwner(target))
        {
            return; // self-applied (e.g. HP cost) records are not damage
        }

        bool isOverTime = (damage.Flags & HitFlags.OverTime) != 0;
        bool fromMonster = _registry.IsNpc(actor) || (!isOverTime && SkillIds.IsNpcSkill(damage.RawSkillId));
        if (fromMonster)
        {
            ApplyIncoming(damage, isOverTime);
            return;
        }

        var encounter = EnsureEncounter(damage.TimestampMs);
        encounter.LastMs = Math.Max(encounter.LastMs, damage.TimestampMs);
        encounter.SelfId = _registry.SelfId ?? encounter.SelfId;

        if (!encounter.Targets.TryGetValue(target, out var targetStats))
        {
            targetStats = new TargetStats(target) { FirstMs = damage.TimestampMs };
            encounter.Targets[target] = targetStats;
        }

        targetStats.LastMs = damage.TimestampMs;
        if (!targetStats.Actors.TryGetValue(actor, out var stats))
        {
            stats = new ActorStats(actor) { FirstMs = damage.TimestampMs };
            targetStats.Actors[actor] = stats;
        }

        stats.LastMs = damage.TimestampMs;
        if (damage.Amount == 0)
        {
            if (damage.HitType is HitType.Miss or HitType.Resist)
            {
                stats.Misses++;
            }

            return;
        }

        targetStats.Total += damage.Amount;
        stats.Total += damage.Amount;
        stats.Hits++;

        if (!stats.Skills.TryGetValue(damage.SkillId, out var skill))
        {
            skill = new SkillStats(damage.SkillId) { IsOverTime = isOverTime };
            stats.Skills[damage.SkillId] = skill;
        }

        skill.Total += damage.Amount;
        skill.Hits++;
        skill.MaxHit = Math.Max(skill.MaxHit, damage.Amount);
        skill.MinHit = skill.MinHit == 0 ? damage.Amount : Math.Min(skill.MinHit, damage.Amount);
        skill.SpecSlots |= SkillIds.SpecSlots(damage.RawSkillId);

        if (isOverTime)
        {
            stats.OverTimeTotal += damage.Amount;
            return;
        }

        stats.DirectHits++;
        var flags = damage.Flags;
        if ((flags & HitFlags.Critical) != 0) { stats.Crits++; skill.Crits++; }
        if ((flags & HitFlags.Back) != 0) { stats.BackHits++; skill.BackHits++; }
        if ((flags & HitFlags.Perfect) != 0) { stats.Perfects++; skill.Perfects++; }
        if ((flags & HitFlags.Double) != 0) { stats.Doubles++; skill.Doubles++; }
        if ((flags & HitFlags.Parry) != 0) { stats.Parried++; skill.Parried++; }
        if ((flags & HitFlags.MultiHit) != 0) { stats.MultiHits++; skill.MultiHits++; skill.MultiHitDamage += damage.ExtraHitDamage; }
    }

    /// <summary>Monster damage on a player. Starts an encounter: being hit first is still combat.</summary>
    private void ApplyIncoming(DamageEvent damage, bool isOverTime)
    {
        var victim = _registry.Find(_registry.ResolveOwner(damage.TargetId));
        if (victim?.Kind != EntityKind.Player && damage.TargetId != _registry.SelfId)
        {
            return; // monsters hitting summons/other monsters
        }

        var encounter = EnsureEncounter(damage.TimestampMs);
        encounter.LastMs = Math.Max(encounter.LastMs, damage.TimestampMs);
        encounter.SelfId = _registry.SelfId ?? encounter.SelfId;
        Support(encounter, damage.TargetId).DamageTaken += damage.Amount;

        if (!encounter.Incoming.TryGetValue(damage.TargetId, out var stats))
        {
            stats = new IncomingStats(damage.TargetId);
            encounter.Incoming[damage.TargetId] = stats;
        }

        // A monster "tick" carrying one of our class skills (e.g. Protection Circle) is our shield absorbing
        // the hit; it is paired with an equal heal on us. Not damage taken.
        if (isOverTime && SkillIds.IsPlayerSkill(damage.SkillId))
        {
            stats.Absorbed += damage.Amount;
            return;
        }

        var flags = damage.Flags;
        if ((flags & HitFlags.Parry) != 0) stats.Parried++;
        if ((flags & HitFlags.ShieldBlock) != 0) stats.Blocked++;
        if ((flags & HitFlags.PerfectBlock) != 0) stats.PerfectBlocks++;
        if ((flags & HitFlags.LinkedEffect) != 0) stats.Shielded++;
        if (damage.Amount == 0)
        {
            stats.Misses++;
            return;
        }

        stats.Total += damage.Amount;
        stats.Hits++;
        if ((flags & HitFlags.Critical) != 0 && !isOverTime)
        {
            stats.Crits++;
        }

        var key = (damage.ActorId, damage.SkillId);
        if (!stats.Sources.TryGetValue(key, out var source))
        {
            source = new IncomingSource(damage.ActorId, damage.SkillId);
            stats.Sources[key] = source;
        }

        source.Total += damage.Amount;
        source.Hits++;
        source.MaxHit = Math.Max(source.MaxHit, damage.Amount);
        if ((flags & HitFlags.Critical) != 0)
        {
            source.Crits++;
        }
    }

    private void ApplySelfCombat(CombatStateEvent combat)
    {
        _selfInCombat = combat.InCombat;
        if (combat.InCombat)
        {
            _selfCombatSince = combat.TimestampMs;
            if (Current is { } current)
            {
                current.SelfInCombat = true;
            }

            return;
        }

        if (Current is { } running)
        {
            running.SelfInCombat = false;
            running.CombatEndMs = combat.TimestampMs;
        }
    }

    private void ApplyHeal(HealEvent heal)
    {
        if (Current is not { } encounter || heal.TimestampMs - encounter.LastMs > _options.EncounterIdleMs)
        {
            return;
        }

        Support(encounter, heal.ActorId).Healing += heal.Amount;
    }

    private void ApplyDeath(DeathEvent death)
    {
        if (Current is not { } encounter || !encounter.Targets.TryGetValue(death.EntityId, out var target))
        {
            return;
        }

        target.Killed = true;
        if (_registry.IsBoss(death.EntityId) && !_registry.IsDummy(death.EntityId))
        {
            Close(EncounterEnd.BossKilled);
        }
    }

    private Encounter EnsureEncounter(long timestampMs)
    {
        if (Current is { } current && timestampMs - current.LastMs <= _options.EncounterIdleMs)
        {
            return current;
        }

        Close(EncounterEnd.Idle);
        Current = new Encounter(_nextId++, timestampMs)
        {
            SelfInCombat = _selfInCombat,
            CombatStartMs = _selfInCombat && timestampMs - _selfCombatSince is >= 0 and <= 10_000 ? _selfCombatSince : null,
        };
        return Current;
    }

    private void Close(EncounterEnd reason)
    {
        if (Current is not { } current)
        {
            return;
        }

        current.End = reason;
        current.SelfInCombat = false;
        Current = null;
        if (current.TotalDamage <= 0 && current.TotalTaken <= 0)
        {
            return;
        }

        // Freeze while names and summon links are still known.
        current.Final = MeterView.Build(current, _registry);
        _history.Add(current);
        if (_history.Count > _options.MaxHistory)
        {
            _history.RemoveAt(0);
        }

        EncounterEnded?.Invoke(current);
    }

    private static SupportStats Support(Encounter encounter, uint id)
    {
        if (!encounter.Support.TryGetValue(id, out var stats))
        {
            stats = new SupportStats();
            encounter.Support[id] = stats;
        }

        return stats;
    }
}
