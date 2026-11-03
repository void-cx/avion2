namespace Avion2.Protocol;

/// <summary>
/// <c>21 56</c>: the game's answer to our own pick-up: the drop it was about (<see cref="LootDropEvent.Uid"/>, also in
/// the take record <c>1F 56</c> that follows in the same instant). Only sent to the one who picked it up.
/// </summary>
/// <param name="Result">0 = picked up (every record seen); anything else is a refusal.</param>
/// <param name="AutoLoot">The pick-up was the game's auto-loot (never set in our captures).</param>
public sealed record LootPickedEvent(long TimestampMs, ushort Result, long DropUid, bool AutoLoot) : GameEvent(TimestampMs);

/// <summary>
/// <c>45 8A</c>: our legion's Legion Hall: which hall, and when its rent ends (Unix ms). Hall 0 with no time: the
/// legion has no hall (or we are in none).
/// </summary>
public sealed record LegionHallInfoEvent(long TimestampMs, ushort Result, uint HallId, long RentEndsMs) : GameEvent(TimestampMs);

/// <summary><c>08 61</c> DungeonClearKillNpc_NT at an instance entry: one byte, 0 in every record seen (its meaning is not known).</summary>
public sealed record DungeonClearKillNpcEvent(long TimestampMs, byte Value) : GameEvent(TimestampMs);

/// <summary>
/// <c>09 61</c> DungeonDeathLimit_NT at an instance entry: deaths counted against the instance's limit, or the limit
/// itself; 0 in every record seen, so which of the two is not known.
/// </summary>
public sealed record DungeonDeathLimitEvent(long TimestampMs, uint Value) : GameEvent(TimestampMs);
