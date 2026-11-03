namespace Avion2.Protocol;

/// <summary>
/// <c>05 E0</c>: the stagger gauge of <paramref name="EntityId"/>: <paramref name="Left"/> of <paramref name="Max"/>
/// points. Each hit that staggers takes a few points; at 0 the gauge breaks (<see cref="StaggerBrokenEvent"/>) and
/// refills to the maximum about 5 s later.
/// </summary>
/// <param name="Status">2 normal; 3 seen while the gauge holds still for a while (meaning not known).</param>
public sealed record StaggerGaugeEvent(long TimestampMs, uint EntityId, uint Max, uint Left, byte Status) : GameEvent(TimestampMs);

/// <summary><c>05 E0</c> short form: the stagger gauge of <paramref name="EntityId"/> broke (reached 0).</summary>
public sealed record StaggerBrokenEvent(long TimestampMs, uint EntityId) : GameEvent(TimestampMs);

/// <summary>
/// A <c>04 38</c> entry without a damage value (layout 0). One follows each hit that takes stagger, with the same
/// actor, target and skill; effect ticks such as mantras send them too.
/// </summary>
/// <param name="RawSkillId">Skill id as sent on the wire (as <see cref="DamageEvent.RawSkillId"/>).</param>
public sealed record ValuelessHitEvent(long TimestampMs, uint ActorId, uint TargetId, uint RawSkillId) : GameEvent(TimestampMs);

/// <summary>
/// <c>09 38</c>: an NPC started a skill with a cast time. The skill itself (<c>02 38</c>) follows after
/// <paramref name="CastMs"/>, unless the cast stops early (<see cref="NpcCastStopEvent"/>). Only NPCs send these.
/// </summary>
/// <param name="TargetId">The cast's target; null for none.</param>
/// <param name="Facing">Where the caster faces, degrees (0..360).</param>
/// <param name="Position">Where the cast is aimed (the target's position).</param>
public sealed record NpcCastEvent(
    long TimestampMs,
    uint CasterId,
    uint SkillId,
    uint? TargetId,
    float? Facing,
    Vec3? Position,
    uint CastMs) : GameEvent(TimestampMs);

/// <summary>
/// <c>0C 38</c>: an NPC's cast with a cast time ended before its skill went off: <paramref name="Result"/> 0 means
/// the caster died, other codes that it was interrupted and lives on.
/// </summary>
/// <param name="CooldownMs">The skill's cooldown from now, 0 when none was set.</param>
public sealed record NpcCastStopEvent(long TimestampMs, uint CasterId, ushort Result, uint SkillId, uint CooldownMs) : GameEvent(TimestampMs)
{
    /// <summary>The caster died during the cast (the other results: interrupted).</summary>
    public bool CasterDied => Result == 0;
}

/// <summary>
/// <c>48 38</c>: where an area of an NPC skill will land (one record per area of the cast), and in how many ms.
/// Sent with the skill's <c>02 38</c>; <paramref name="CastSeq"/> is that cast's sequence byte.
/// </summary>
/// <param name="Index">Number of this area within the cast (1, 2, …).</param>
/// <param name="Facing">Direction of the area, degrees (often 0).</param>
/// <param name="DelayMs">Time until it lands.</param>
public sealed record TelegraphEvent(
    long TimestampMs,
    uint CasterId,
    uint SkillId,
    byte CastSeq,
    ushort Index,
    Vec3 Position,
    float Facing,
    ushort DelayMs) : GameEvent(TimestampMs);
