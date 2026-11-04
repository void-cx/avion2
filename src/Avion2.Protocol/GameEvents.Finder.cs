namespace Avion2.Protocol;

/// <summary>Where a group-finder room is: the game sends 0, 2 and 3; other values are kept as they come.</summary>
public enum FinderRoomState : byte
{
    Recruiting = 0,
    InDungeon = 2,
    Cleared = 3,
}

/// <summary>The role a room slot asks for (open slots too).</summary>
public enum FinderRole : byte
{
    None = 0,
    Tank = 1,
    Damage = 2,
    Healer = 3,
    Any = 4,
}

/// <summary>
/// One slot of a group-finder room (<c>02 97</c>, <c>0B 97</c>, <c>1F 97</c>). An open slot has character 0 and no name;
/// its <paramref name="Role"/> is the role the room asks for there.
/// </summary>
/// <param name="CharacterId">The character (the low 32 bits of the record's character number), 0 for an open slot.</param>
/// <param name="Ready">The slot carries the ready field (mask <c>02</c>; its value was 1 in every record seen).</param>
/// <param name="Online">Clears while the member is between servers (zoning into the instance).</param>
/// <param name="Inside">The slot carries the revive-item count (mask <c>10</c>): the member is inside the instance.</param>
public sealed record FinderSlot(byte Slot, uint CharacterId, string Name, GameClass Class, uint Level, uint ItemLevel,
    uint CombatPower, FinderRole Role, bool Ready, bool Online, bool Inside)
{
    public bool IsOpen => CharacterId == 0;
}

/// <summary>
/// <c>02 97</c>: the group-finder room we sit in (not the party), sent while we are in one: on joining, on every change
/// and every few seconds. Every record seen has us in a slot.
/// </summary>
/// <param name="RoomNumber">The room's number (a re-listed room gets a new one).</param>
/// <param name="Title">The recruiting text the leader wrote.</param>
/// <param name="Size">Slots in the room (5).</param>
/// <param name="DungeonId">The dungeon (600001, 600002, 600072 …; the id of the client's Dungeon table).</param>
/// <param name="LeaderId">The leader's character.</param>
/// <param name="Reason">Why it was sent: 1 we created it, 2 we joined, 4 a member left, 5 a member was removed,
/// 7 a member's slot changed, 9 refresh, 10 the state changed.</param>
public sealed record FinderRoomEvent(long TimestampMs, uint RoomNumber, string Title, byte Size, uint DungeonId, uint LeaderId,
    FinderRoomState State, IReadOnlyList<FinderSlot> Slots, byte Reason) : GameEvent(TimestampMs);

/// <summary><c>0B 97</c> (<paramref name="Joined"/>): a member joined our room; <c>1F 97</c>: a member's slot changed.</summary>
public sealed record FinderSlotEvent(long TimestampMs, FinderSlot Slot, bool Joined) : GameEvent(TimestampMs);

/// <summary><c>16 97</c>: a member readied or unreadied; <c>15 97</c>: we did (<paramref name="CharacterId"/> null).</summary>
public sealed record FinderReadyEvent(long TimestampMs, uint? CharacterId, bool Ready) : GameEvent(TimestampMs);

/// <summary><c>18 97</c>: the room's instance starts (the party records follow in the same millisecond).</summary>
public sealed record FinderStartEvent(long TimestampMs) : GameEvent(TimestampMs);

/// <summary><c>1D 97</c>: we left the room.</summary>
public sealed record FinderLeftEvent(long TimestampMs) : GameEvent(TimestampMs);

/// <summary><c>1B 97</c> (likely): the room with that number is gone (a re-listed room replaced it, or we left).</summary>
public sealed record FinderRoomClosedEvent(long TimestampMs, uint RoomNumber) : GameEvent(TimestampMs);

/// <summary><c>07 97</c>: a player asks to join our room (only the leader gets it).</summary>
/// <param name="Server">The server named after the name (in 3 of 11 records not the one in the character number).</param>
/// <param name="ExpiresAtMs">When the request runs out (epoch ms; about 20 s after it came).</param>
public sealed record FinderJoinRequestEvent(long TimestampMs, uint RoomNumber, uint CharacterId, GameClass Class, uint Level,
    uint ItemLevel, string Name, ushort Server, uint CombatPower, long ExpiresAtMs) : GameEvent(TimestampMs);

/// <summary><c>25 97</c>: the player withdrew their request to join.</summary>
public sealed record FinderJoinRequestCancelledEvent(long TimestampMs, uint CharacterId) : GameEvent(TimestampMs);

/// <summary>
/// <c>20 92</c> (<c>PartySetTargetByBoss_NT</c>): the party member (or us) a boss targets, by entity id; 0 when cleared
/// (the boss died or dropped its target).
/// </summary>
public sealed record PartyBossTargetEvent(long TimestampMs, uint EntityId) : GameEvent(TimestampMs);
