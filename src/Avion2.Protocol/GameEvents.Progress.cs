namespace Avion2.Protocol;

/// <summary>
/// <c>1D 56</c>: our item level as the game shows it, and the highest it has been. Sent at login and after every
/// equipment, enchant or Daevanion change. It is the item levels of the gear worn plus the Daevanion board points.
/// </summary>
public sealed record SelfItemLevelEvent(long TimestampMs, uint ItemLevel, uint Best) : GameEvent(TimestampMs);

/// <summary>
/// <c>1C 56</c>: a player's equipment changed (equip, unequip, enchant): ours or a player nearby, with the item level
/// after the change.
/// </summary>
/// <param name="Slots">How many slots changed.</param>
public sealed record EquipmentChangedEvent(long TimestampMs, uint EntityId, uint ItemLevel, int Slots) : GameEvent(TimestampMs);

/// <summary>
/// One of our items was used: <c>16 56</c> (potions and scrolls, also when the game uses them by itself) or
/// <c>13 56</c> (the answer to using an item from the bag, <paramref name="FromBag"/>).
/// </summary>
/// <param name="ItemUid">The item stack (the same one each time).</param>
public sealed record ItemUsedEvent(long TimestampMs, long ItemUid, uint ItemId, bool FromBag) : GameEvent(TimestampMs);

/// <summary><c>A5 FF</c> (<paramref name="Initial"/>, at login) / <c>A6 FF</c>: Kinah gained today, toward the daily limit.</summary>
/// <param name="ServerMs">The server's clock when it was sent.</param>
public sealed record KinahLimitEvent(long TimestampMs, ulong Gained, long ServerMs, bool Initial) : GameEvent(TimestampMs);

/// <summary><c>AB FF</c> (<c>A9 FF</c> before the 2026-10-29 client): Abyss Points gained this week and the weekly cap.</summary>
public sealed record AbyssPointLimitEvent(long TimestampMs, ulong Points, ulong Cap) : GameEvent(TimestampMs);

/// <summary>
/// One entry ticket or key count of <c>0B 61</c> / <c>0C 61</c>. A record leaves out a count that is 0.
/// </summary>
/// <param name="Count">How many are left.</param>
/// <param name="Extra">A second count: charged on top of the regular ones (Odyle, Nightmare).</param>
/// <param name="Time">A time or duration (ms) instead of a count, for some entries; null = not sent.</param>
public sealed record ContentTicket(uint Id, uint Count, uint Extra = 0, long? Time = null);

/// <summary><c>0B 61</c>: every ticket and key count (at login).</summary>
public sealed record ContentTicketsEvent(long TimestampMs, IReadOnlyList<ContentTicket> Tickets) : GameEvent(TimestampMs);

/// <summary>
/// <c>0C 61</c>: one ticket count changed. <paramref name="Reason"/>: 1 recharged by an item, 2 refreshed over time,
/// 3 used (entering or a reward taken).
/// </summary>
public sealed record ContentTicketChangedEvent(long TimestampMs, ContentTicket Ticket, byte Reason) : GameEvent(TimestampMs)
{
    public const byte ItemRecharge = 1;
    public const byte Refresh = 2;
    public const byte Used = 3;
}

/// <summary>One Daeva Pass season of <c>24 E3</c>.</summary>
/// <param name="TotalExp">Pass experience this season.</param>
/// <param name="WeekExp">Pass experience this week (it starts over at the weekly reset).</param>
public sealed record DaevaPassSeason(uint SeasonId, uint TotalExp, uint WeekExp);

/// <summary><c>24 E3</c>: the Daeva Pass seasons, at login and after each pass experience gain.</summary>
public sealed record DaevaPassEvent(long TimestampMs, IReadOnlyList<DaevaPassSeason> Seasons) : GameEvent(TimestampMs);

/// <summary>
/// <c>07 8D</c>: we gained experience. <paramref name="Total"/> is the experience within the current level (it starts
/// over at a level up). <paramref name="Reason"/>: 1 a kill, 2 a quest.
/// </summary>
public sealed record ExperienceEvent(long TimestampMs, long Total, long Gained, byte Reason) : GameEvent(TimestampMs);

/// <summary>Souls of one pet kind (a pet id); 5 of a kind not owned yet bring the pet.</summary>
public sealed record PetSouls(uint PetId, uint Souls);

/// <summary><c>00 90</c>: the pets we own and the souls collected per kind (at login).</summary>
public sealed record PetRosterEvent(long TimestampMs, IReadOnlyList<uint> Owned, IReadOnlyList<PetSouls> Souls) : GameEvent(TimestampMs);

/// <summary><c>0D 90</c> (<paramref name="Gained"/>) / <c>0E 90</c>: souls gained from kills, or spent on a pet.</summary>
public sealed record PetSoulsEvent(long TimestampMs, IReadOnlyList<PetSouls> Changes, bool Gained) : GameEvent(TimestampMs);

/// <summary><c>09 90</c>: we acquired a pet (5 souls of its kind are spent in the same instant).</summary>
/// <param name="CreatureType">The pet's creature type; each pet of a type adds to that type's board.</param>
public sealed record PetAcquiredEvent(long TimestampMs, uint EntityId, uint PetId, uint Level, byte CreatureType) : GameEvent(TimestampMs);
