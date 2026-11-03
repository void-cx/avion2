namespace Avion2.Protocol;

/// <summary>Where a party member is and what they have, from <c>1C 92</c> and the party records (<c>00 92</c>, <c>0D 92</c>).</summary>
/// <param name="MapId">The map (the low half of the map key: 1010, 600002 …).</param>
/// <param name="Instance">The instance number (the high half of the map key), 0 in the open world.</param>
/// <param name="Subzone">The game's subzone number on that map.</param>
/// <param name="Position">World position, as in movement records.</param>
/// <param name="Legion">The member's legion name, null when they have none.</param>
public sealed record PartyMemberStatus(uint MapId, uint Instance, uint Subzone, Vec3 Position, uint Level, uint ItemLevel,
    uint CombatPower, string? Legion = null);

/// <summary>
/// <c>1C 92</c>: a party member's map, position, level, item level and combat power, every few seconds and also when they
/// are out of view or on another map. Never sent for us.
/// </summary>
public sealed record PartyMemberStatusEvent(long TimestampMs, uint CharacterId, GameClass Class, PartyMemberStatus Status)
    : GameEvent(TimestampMs);

/// <summary>
/// <c>21 92</c>: a member dropped off the world server: when they leave (a party record without them follows) and when they
/// load into an instance (they come back with <c>22 92</c>).
/// </summary>
public sealed record PartyMemberDisconnectedEvent(long TimestampMs, uint CharacterId) : GameEvent(TimestampMs);

/// <summary><c>11 92</c>: the party has a new leader.</summary>
public sealed record PartyLeaderEvent(long TimestampMs, uint CharacterId) : GameEvent(TimestampMs);

/// <summary><c>1F 92</c>: the party was disbanded (reason 8: the dungeon expired).</summary>
public sealed record PartyDisbandedEvent(long TimestampMs, byte Reason) : GameEvent(TimestampMs);

/// <summary>
/// <c>1F 93</c>: we are out of the party with that key (reason 3: we left; 10: a new party replaces it, its <c>00 92</c>
/// follows at once).
/// </summary>
public sealed record PartyLeftEvent(long TimestampMs, long PartyKey, byte Reason) : GameEvent(TimestampMs);

/// <summary>
/// <c>0E 92</c> (added) / <c>1A 92</c> (removed): abnormals on a party member or us, by entity id, also out of view. It
/// carries only the abnormal ids (no instance, caster or duration); in view, <c>2A 38</c> / <c>2C 38</c> say the same.
/// </summary>
public sealed record PartyAbnormalEvent(long TimestampMs, uint EntityId, IReadOnlyList<uint> AbnormalIds, bool Added)
    : GameEvent(TimestampMs);
