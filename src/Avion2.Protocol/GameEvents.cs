using System.Text.Json.Serialization;

namespace Avion2.Protocol;

/// <summary>A decoded, semantic event. Timestamps are capture time in Unix milliseconds.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DamageEvent), "damage")]
[JsonDerivedType(typeof(HealEvent), "heal")]
[JsonDerivedType(typeof(SummonLinkEvent), "summonLink")]
[JsonDerivedType(typeof(SelfIdentityEvent), "self")]
[JsonDerivedType(typeof(PlayerSpawnEvent), "playerSpawn")]
[JsonDerivedType(typeof(NpcSpawnEvent), "npcSpawn")]
[JsonDerivedType(typeof(DeathEvent), "death")]
[JsonDerivedType(typeof(MapLoadEvent), "mapLoad")]
[JsonDerivedType(typeof(ZoneChangeEvent), "zoneChange")]
[JsonDerivedType(typeof(HpUpdateEvent), "hp")]
[JsonDerivedType(typeof(SelfHintEvent), "selfHint")]
[JsonDerivedType(typeof(CombatStateEvent), "combat")]
public abstract record GameEvent(long TimestampMs);

public enum HitType : byte
{
    Unknown = 0,
    Miss = 1,
    Normal = 2,
    Critical = 3,
    Resist = 6,
}

[Flags]
public enum HitFlags : ushort
{
    None = 0,
    Critical = 1 << 0,
    Back = 1 << 1,
    Front = 1 << 2,
    ShieldBlock = 1 << 3,
    Parry = 1 << 4,
    Perfect = 1 << 5,
    Double = 1 << 6,
    IronWall = 1 << 7,
    Regeneration = 1 << 8,
    PerfectBlock = 1 << 9,
    MultiHit = 1 << 10,
    OverTime = 1 << 11,

    /// <summary>The hit referenced another effect (switch 0x40), e.g. a shield such as Protection Circle absorbing it.</summary>
    LinkedEffect = 1 << 12,
}

/// <param name="SkillId">Base skill id (specialisation digits stripped).</param>
/// <param name="RawSkillId">Skill id as sent on the wire.</param>
/// <param name="HitCount">1 plus any additional hits folded into <paramref name="Amount"/>.</param>
/// <param name="PowerScalar">Attacker damage multiplier in 1/100 %, 0 if absent.</param>
/// <param name="ExtraHitDamage">Damage of the additional hits, already included in <paramref name="Amount"/>.</param>
public sealed record DamageEvent(
    long TimestampMs,
    uint ActorId,
    uint TargetId,
    uint SkillId,
    uint RawSkillId,
    long Amount,
    HitType HitType,
    HitFlags Flags,
    int HitCount,
    uint PowerScalar,
    long ExtraHitDamage = 0) : GameEvent(TimestampMs);

public sealed record HealEvent(
    long TimestampMs,
    uint ActorId,
    uint TargetId,
    uint SkillId,
    long Amount,
    bool OverTime) : GameEvent(TimestampMs);

public sealed record SummonLinkEvent(long TimestampMs, uint SummonId, uint OwnerId) : GameEvent(TimestampMs);

public sealed record SelfIdentityEvent(
    long TimestampMs,
    uint EntityId,
    string Name,
    ushort ServerId,
    GameClass Class,
    uint Level) : GameEvent(TimestampMs);

public sealed record PlayerSpawnEvent(long TimestampMs, uint EntityId, string Name, ushort? ServerId) : GameEvent(TimestampMs);

/// <param name="Kind">Low byte of the spawn mask: 0x0C/0x0D NPC, 0x5F summon, 0x1C effect, 0x1D/0x1F/0x5D spirit/ground spell.</param>
/// <param name="InlineName">For summons this is the owner's name.</param>
public sealed record NpcSpawnEvent(
    long TimestampMs,
    uint EntityId,
    byte Kind,
    uint? NpcCode,
    uint? MaxHp,
    string? InlineName) : GameEvent(TimestampMs);

public enum DeathKind : byte
{
    Unknown = 0,
    LoadedDead = 1,
    Combat = 3,
    LeftWorld = 7,
}

public sealed record DeathEvent(long TimestampMs, uint EntityId, DeathKind Kind) : GameEvent(TimestampMs);

public sealed record MapLoadEvent(long TimestampMs, uint MapId) : GameEvent(TimestampMs);

/// <summary>Self teleport / zone change. Entity ids are reissued afterwards.</summary>
public sealed record ZoneChangeEvent(long TimestampMs) : GameEvent(TimestampMs);

/// <param name="MaxHp">0 when the record carries only the current HP.</param>
public sealed record HpUpdateEvent(long TimestampMs, uint EntityId, uint Hp, uint MaxHp) : GameEvent(TimestampMs);

/// <summary>Our own entity id, from records only the local player receives (own stats, about once per second).
/// Carries no name; <see cref="SelfIdentityEvent"/> (sent on teleport/zone) does.</summary>
public sealed record SelfHintEvent(long TimestampMs, uint EntityId) : GameEvent(TimestampMs);

/// <summary><c>21 8D</c>: an entity entered or left combat. For us this matches the game's Combat Analysis clock
/// (on ~0.2 s before the first hit, off 3–5 s after the last).</summary>
public sealed record CombatStateEvent(long TimestampMs, uint EntityId, bool InCombat) : GameEvent(TimestampMs);
