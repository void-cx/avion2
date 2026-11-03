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
[JsonDerivedType(typeof(FieldBossTableEvent), "fieldBosses")]
[JsonDerivedType(typeof(SkillCastEvent), "cast")]
[JsonDerivedType(typeof(PositionEvent), "position")]
[JsonDerivedType(typeof(BuffEvent), "buff")]
[JsonDerivedType(typeof(BuffRemovedEvent), "buffRemoved")]
[JsonDerivedType(typeof(LegionTagEvent), "legionTag")]
[JsonDerivedType(typeof(KillEvent), "kill")]
[JsonDerivedType(typeof(SelfCombatPowerEvent), "selfCombatPower")]
[JsonDerivedType(typeof(ServerClockEvent), "clock")]
[JsonDerivedType(typeof(LegionDetailsEvent), "legion")]
[JsonDerivedType(typeof(LegionRosterEvent), "legionRoster")]
[JsonDerivedType(typeof(InventoryListEvent), "inventory")]
[JsonDerivedType(typeof(WorldObjectEvent), "worldObject")]
[JsonDerivedType(typeof(InteractionDoneEvent), "interactionDone")]
[JsonDerivedType(typeof(InventoryItemEvent), "inventoryItem")]
[JsonDerivedType(typeof(MoveOffsetEvent), "moveOffset")]
[JsonDerivedType(typeof(RotateEvent), "rotate")]
[JsonDerivedType(typeof(GroundHeightEvent), "groundHeight")]
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

/// <param name="CombatPower">Our combat power as shown on the armory page, if found in the record.</param>
public sealed record SelfIdentityEvent(
    long TimestampMs,
    uint EntityId,
    string Name,
    ushort ServerId,
    GameClass Class,
    uint Level,
    uint? CombatPower = null) : GameEvent(TimestampMs)
{
    /// <summary>Our character id (as in the party roster and loot records); null when it was not found.</summary>
    public uint? CharacterId { get; init; }
}

/// <param name="CombatPower">Unverified: read with the prior-art rule (u32 after the <c>F4 CB 1F</c> marker).</param>
public sealed record PlayerSpawnEvent(
    long TimestampMs,
    uint EntityId,
    string Name,
    ushort? ServerId,
    GameClass Class = GameClass.Unknown,
    uint? CombatPower = null,
    Vec3? Position = null) : GameEvent(TimestampMs);

/// <summary>A world position (≈ cm; z is height).</summary>
public readonly record struct Vec3(float X, float Y, float Z)
{
    /// <summary>Plausible world coordinates (filters out the floats that turn up when scanning a record).</summary>
    public bool IsPlausible =>
        float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z) &&
        Math.Abs(X) < 1_000_000 && Math.Abs(Y) < 1_000_000 && Z is > -50_000 and < 200_000 &&
        (Math.Abs(X) > 100 || Math.Abs(Y) > 100) && Math.Abs(Z) > 10;

    public float DistanceTo(Vec3 other)
    {
        float dx = X - other.X, dy = Y - other.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}

/// <param name="Kind">Low byte of the spawn mask: 0x0C/0x0D NPC, 0x5F summon, 0x1C effect, 0x1D/0x1F/0x5D spirit/ground spell.</param>
/// <param name="InlineName">For summons this is the owner's name.</param>
public sealed record NpcSpawnEvent(
    long TimestampMs,
    uint EntityId,
    byte Kind,
    uint? NpcCode,
    uint? MaxHp,
    string? InlineName,
    Vec3? Position = null) : GameEvent(TimestampMs);

public enum DeathKind : byte
{
    Unknown = 0,
    LoadedDead = 1,
    Combat = 3,
    LeftWorld = 7,
}

/// <param name="AtKill">Timed at the kill (from the death record <c>04 8D</c>), not at the despawn a few seconds later.</param>
public sealed record DeathEvent(long TimestampMs, uint EntityId, DeathKind Kind, bool AtKill = false) : GameEvent(TimestampMs);

/// <summary>
/// <c>1E 56</c>: an item dropped on the ground (from a corpse or chest) for <paramref name="OwnerCharacterId"/> to take.
/// </summary>
/// <param name="DropId">Identifies the drop until it is taken (<see cref="LootTakenEvent"/>).</param>
/// <param name="SourceId">Most likely the entity it dropped from (not confirmed).</param>
public sealed record LootDropEvent(long TimestampMs, uint DropId, uint SourceId, uint ItemId, uint Count, uint OwnerCharacterId,
    ushort OwnerServerId, Vec3 Position) : GameEvent(TimestampMs);

/// <summary><c>1F 56</c>: a drop left the ground; taken by <paramref name="TakerCharacterId"/>, or 0 when it just vanished.</summary>
public sealed record LootTakenEvent(long TimestampMs, uint DropId, uint TakerCharacterId) : GameEvent(TimestampMs);

/// <summary>
/// <c>00 61</c>: the stage of the instance we are in. 2 = running (from entering), 3 = cleared (to = the automatic
/// exit, 10 s later), 4 = leaving (to = 10 min later, likely the time until it can be entered again).
/// </summary>
public sealed record InstanceStageEvent(long TimestampMs, uint MapId, byte Stage, long FromMs, long ToMs) : GameEvent(TimestampMs);

/// <summary><c>2E 92</c>: a map event started (e.g. a stronghold's objectives, "World_L1_GarrisonTerritory_R3_MapEvent").</summary>
public sealed record MapEventStartEvent(long TimestampMs, uint EventId, string Name) : GameEvent(TimestampMs);

/// <summary>
/// <c>2D 92</c>: progress of a map event: the objective <paramref name="Step"/> (1, 2, …) and whether the event is
/// <paramref name="Done"/> (all objectives met).
/// </summary>
public sealed record MapEventProgressEvent(long TimestampMs, uint EventId, byte Step, bool Done) : GameEvent(TimestampMs);

/// <summary>
/// <c>31 8D</c>: we started gathering from a node. The server decides the outcome up front: <paramref name="Success"/>
/// (the bars on screen only play it out) and whether the item is the better grade (<paramref name="Fine"/>).
/// </summary>
/// <param name="Attempt">Gatherings on this node so far, this one included (1, 2, 3).</param>
/// <param name="Steps">How many steps the success / fail race on screen has (more steps, longer gathering).</param>
public sealed record GatherStartEvent(long TimestampMs, uint NodeId, uint Attempt, bool Success, bool Fine, int Steps)
    : GameEvent(TimestampMs);

/// <summary>
/// <c>39 36</c>: using an object was interrupted before it finished (e.g. an aborted gathering): the object, who used
/// it, and which attempt on it.
/// </summary>
public sealed record InteractionInterruptedEvent(long TimestampMs, uint ObjectId, uint ActorId, uint Attempt) : GameEvent(TimestampMs);

/// <summary><c>33 8D</c>: the gathering ended: <paramref name="Result"/> 0 success, 1 success with the better grade, 2 failed.</summary>
public sealed record GatherEndEvent(long TimestampMs, uint NodeId, byte Result, uint ItemId, uint Count) : GameEvent(TimestampMs);

/// <summary>
/// <c>6F 8D</c>: we started crafting a recipe at a station. As with gathering, the server decides the outcome up front
/// (<paramref name="Success"/>), including whether a combo follows the first race (<paramref name="Combo"/>: the
/// better recipe, two items; a failed combo yields nothing).
/// </summary>
/// <param name="Steps">How many steps the first success / fail race on screen has.</param>
public sealed record CraftStartEvent(long TimestampMs, uint StationId, uint RecipeId, bool Success, bool Combo, int Steps)
    : GameEvent(TimestampMs);

/// <summary><c>71 8D</c>: the craft ended: <paramref name="Result"/> 0 success, 1 combo success (two items), 2 failed.</summary>
public sealed record CraftEndEvent(long TimestampMs, uint StationId, byte Result) : GameEvent(TimestampMs);

/// <summary>
/// <c>18 38</c> (<paramref name="Open"/>): after <paramref name="After"/>, the chain skill <paramref name="Skill"/> can
/// be used for 3 s. <c>19 38</c>: it can't any more (used or expired); <paramref name="After"/> is 0 then.
/// </summary>
public sealed record ChainStepEvent(long TimestampMs, uint After, uint Skill, bool Open) : GameEvent(TimestampMs);

/// <summary><c>34 38</c>: we selected <paramref name="TargetId"/>; 0 = nothing selected.</summary>
public sealed record TargetSelectedEvent(long TimestampMs, uint TargetId) : GameEvent(TimestampMs);

/// <summary><c>35 38</c>: whom our selected target <paramref name="EntityId"/> targets; 0 = nobody.</summary>
public sealed record TargetOfTargetEvent(long TimestampMs, uint EntityId, uint TargetId) : GameEvent(TimestampMs);

/// <summary>A member of our party or group-finder room; <paramref name="CharacterId"/> is the character, not the entity id.</summary>
/// <param name="Online">Logged in (from the party record); null when the record does not say (the finder room).</param>
/// <param name="EntityId">The member's entity id in our zone, 0 when elsewhere or not known.</param>
/// <param name="Hp">HP and max HP from the party record; 0/0 when the member is not in our zone (and in the finder room).</param>
/// <param name="Alive">False when the party record says the member is dead.</param>
/// <param name="Status">Map, position, levels and combat power from the party record; null for the finder room.</param>
public sealed record PartyMember(byte Slot, uint CharacterId, string Name, GameClass Class, bool? Online = null, uint EntityId = 0,
    uint Hp = 0, uint MaxHp = 0, bool Alive = true, PartyMemberStatus? Status = null);

/// <summary>
/// Our party (<c>00 92</c>, after every change) or our group-finder room (<c>02 97</c>, every few seconds while we sit
/// in one); includes ourselves.
/// </summary>
/// <param name="LeaderId">The leader's character id: set for the party (<c>00 92</c>), null for the finder room.</param>
public sealed record PartyRosterEvent(long TimestampMs, IReadOnlyList<PartyMember> Members, uint? LeaderId = null) : GameEvent(TimestampMs);

/// <summary><c>0D 92</c> / <c>22 92</c>: a member joined the party, or came back with a new entity id.</summary>
public sealed record PartyMemberJoinedEvent(long TimestampMs, PartyMember Member) : GameEvent(TimestampMs);

/// <param name="ContentCode">The map's content type as the game sends it: 0 open world, 30 sealed dungeon, 79 expedition,
/// 37/38 stronghold, 72/80 other instances, 12 a reload inside an instance (0 when the record is too short).</param>
public sealed record MapLoadEvent(long TimestampMs, uint MapId, byte ContentCode = 0) : GameEvent(TimestampMs);

/// <summary>Self teleport / zone change. Entity ids are reissued afterwards.</summary>
public sealed record ZoneChangeEvent(long TimestampMs) : GameEvent(TimestampMs);

/// <param name="MaxHp">0 when the record carries only the current HP.</param>
/// <param name="Alive">From a party member's HP record (<c>1B 92</c>): false while they are dead; null for other records.</param>
public sealed record HpUpdateEvent(long TimestampMs, uint EntityId, uint Hp, uint MaxHp, bool? Alive = null) : GameEvent(TimestampMs);

/// <summary>
/// <c>00 8D</c> resource 6: an entity's flight time in 1/10 000 s (570 000 = 57 s when full). Every 100 ms while it
/// flies, every second while it refills.
/// </summary>
public sealed record FlightTimeEvent(long TimestampMs, uint EntityId, uint Value) : GameEvent(TimestampMs);

/// <summary>Our own entity id, from records only the local player receives (own stats, about once per second).
/// Carries no name; <see cref="SelfIdentityEvent"/> (sent on teleport/zone) does.</summary>
public sealed record SelfHintEvent(long TimestampMs, uint EntityId) : GameEvent(TimestampMs);

/// <summary><c>21 8D</c>: an entity entered or left combat. For us this matches the game's Combat Analysis clock
/// (on ~0.2 s before the first hit, off 3–5 s after the last).</summary>
public sealed record CombatStateEvent(long TimestampMs, uint EntityId, bool InCombat) : GameEvent(TimestampMs);

/// <summary>One field boss spawn point from <c>01 91</c>.</summary>
/// <param name="SpawnCode">Map × 100 + index, e.g. 101005.</param>
/// <param name="Alive">Alive: <paramref name="TimestampMs"/> is when it spawned. Dead: when it respawns.</param>
public sealed record FieldBossEntry(uint SpawnCode, bool Alive, long TimestampMs, float? X, float? Y, float? Z);

/// <summary><c>01 91</c>: the field boss table of the current map (sent about once a second).</summary>
public sealed record FieldBossTableEvent(long TimestampMs, uint MapId, IReadOnlyList<FieldBossEntry> Entries) : GameEvent(TimestampMs);

/// <summary>
/// <c>00 36</c>: the server's clock, 20 times a second. <see cref="GameEvent.TimestampMs"/> is when it arrived, so
/// arrival − server time shows the delivery delay, and a gap between ticks a freeze.
/// </summary>
public sealed record ServerClockEvent(long TimestampMs, long ServerMs) : GameEvent(TimestampMs);

/// <summary><c>02 38</c>: a skill cast (any entity in range, including buffs and heals that deal no damage).</summary>
/// <param name="SkillId">Base skill id (specialisation digits stripped).</param>
/// <param name="TargetId">The cast's target (the caster itself for self buffs).</param>
/// <param name="TargetPosition">Where the target stood (kind 2 records only).</param>
public sealed record SkillCastEvent(
    long TimestampMs,
    uint ActorId,
    uint SkillId,
    uint RawSkillId,
    uint? TargetId,
    Vec3? TargetPosition,
    float? Facing = null) : GameEvent(TimestampMs);

public enum PositionSource : byte
{
    /// <summary><c>1C 37</c>: the server's absolute fix (about once a second per moving entity).</summary>
    Move,
    /// <summary><c>1A</c>/<c>1B 37</c>: movement with a position (also <c>3E</c>/<c>3F</c>/<c>40 37</c>, mostly ours).</summary>
    Steer,
    /// <summary><c>2F</c>/<c>3C 37</c>: dash, leap or relocation (also sent for us); <c>30 37</c>: a dodge roll starts.</summary>
    Dash,
    /// <summary>Spawn records.</summary>
    Spawn,
    /// <summary>Where a buff landed on the entity (<c>2A 38</c>); our own position comes mostly from these.</summary>
    Buff,
    /// <summary>Jump, fall, glide or flight keyframes (<c>1E</c>, <c>1F</c>, <c>23</c>, <c>24</c>, <c>28</c>, <c>29 37</c>).</summary>
    Air,
    /// <summary>Swim keyframes (<c>36</c>/<c>37 37</c>).</summary>
    Swim,
    /// <summary>Added up from 10 Hz movement offsets since the last keyframe (Analysis, not a record of its own).</summary>
    Offset,
}

/// <summary>
/// Where an entity is. The server does not stream our own movement; we get fixes from buffs, stops, falls, dashes and
/// spawns.
/// </summary>
/// <param name="Heading">Degrees (0..360), if the record has one.</param>
public sealed record PositionEvent(long TimestampMs, uint EntityId, Vec3 Position, float? Heading, PositionSource Source) : GameEvent(TimestampMs);

/// <summary><c>2A 38</c> / <c>2B 38</c>: an abnormal (buff or debuff) was applied or refreshed on an entity.</summary>
/// <param name="Instance">Per-entity instance id, used by <see cref="BuffRemovedEvent"/>.</param>
/// <param name="AbnormalId">Effect id; <paramref name="SkillId"/> names it when present.</param>
/// <param name="DurationMs">Total length; null = permanent.</param>
public sealed record BuffEvent(
    long TimestampMs,
    uint EntityId,
    uint Instance,
    uint AbnormalId,
    uint? SkillId,
    uint CasterId,
    long? DurationMs,
    bool Added,
    Vec3? Position) : GameEvent(TimestampMs);

/// <summary><c>2C 38</c>: abnormals ended on an entity.</summary>
public sealed record BuffRemovedEvent(long TimestampMs, uint EntityId, IReadOnlyList<uint> Instances) : GameEvent(TimestampMs);

/// <summary><c>56 36</c>: our combat power changed (and the highest it has been).</summary>
public sealed record SelfCombatPowerEvent(long TimestampMs, uint CombatPower, uint Best) : GameEvent(TimestampMs);

/// <summary>
/// <c>04 8D</c>: <paramref name="VictimId"/> died. The despawn (<c>42 36</c>) follows a few seconds later.
/// </summary>
/// <param name="SkillId">The killing skill, 0 for damage over time or no cause.</param>
/// <param name="KillerId">The player who killed it, 0 when not said (an NPC killer, or a kill by a party member in a
/// big fight).</param>
/// <param name="KillerNpcCode">The NPC that killed it, if one did.</param>
/// <param name="Reason">1 a hit, 3 damage over time, 0 no cause.</param>
public sealed record KillEvent(long TimestampMs, uint VictimId, uint SkillId, uint KillerId, uint? KillerNpcCode, byte Reason)
    : GameEvent(TimestampMs);

/// <summary><c>33 8A</c> (and the owner part of <c>04 8D</c>): an entity's legion.</summary>
public sealed record LegionTagEvent(long TimestampMs, uint EntityId, ushort ServerId, string Legion) : GameEvent(TimestampMs);

/// <summary><c>03 8A</c>: our own legion: name, leader and its texts (introduction, notice, …).</summary>
public sealed record LegionDetailsEvent(long TimestampMs, string Name, string? Leader, IReadOnlyList<string> Texts) : GameEvent(TimestampMs);

/// <param name="LastOnlineMs">Unix ms of the member's last login, if present.</param>
public sealed record LegionMember(string Name, int? Level, long? LastOnlineMs);

/// <summary><c>05 8A</c>: a page of our legion's member list.</summary>
public sealed record LegionRosterEvent(long TimestampMs, IReadOnlyList<LegionMember> Members) : GameEvent(TimestampMs);

/// <summary>One inventory slot of our character.</summary>
/// <param name="Container">01 cube (bag), 0B equipped, 0F warehouse; 04 seen for quest items.</param>
/// <param name="Enchant">Enchant level of gear (+0…+15), null for stackables.</param>
/// <param name="ItemLevel">Worn gear's own item level as the game counts it (base, enchant and any bonus); null when not sent.</param>
public sealed record InventoryItem(uint ItemId, ulong Count, byte Container, byte Slot, byte? Enchant = null, uint? ItemLevel = null)
{
    public const byte Cube = 0x01;
    public const byte Equipped = 0x0B;
    public const byte Warehouse = 0x0F;
}

/// <summary><c>11 56</c>: our whole inventory (sent when entering the world).</summary>
public sealed record InventoryListEvent(long TimestampMs, IReadOnlyList<InventoryItem> Items) : GameEvent(TimestampMs);

/// <summary><c>1B 56</c>: one slot changed; <see cref="InventoryItem.Count"/> is the new total.</summary>
public sealed record InventoryItemEvent(long TimestampMs, InventoryItem Item) : GameEvent(TimestampMs);

/// <summary><c>34 36</c>: an interactable world object came into range (hidden cube, gather node, …) with its exact position.</summary>
/// <param name="Code">Object type; 1200039 was a hidden cube both times one was seen.</param>
public sealed record WorldObjectEvent(long TimestampMs, uint EntityId, byte Kind, uint Code, Vec3 Position) : GameEvent(TimestampMs);

/// <summary><c>3A 36</c>: an interaction finished — <paramref name="ItemId"/> is the item it used up (a Hidden Cube key when opening a cube).</summary>
public sealed record InteractionDoneEvent(long TimestampMs, uint ObjectId, uint ActorId, uint? ItemId) : GameEvent(TimestampMs);
