namespace Avion2.Protocol;

/// <summary>
/// One expedition's guaranteed-reward counter as <c>12 57</c> lists it. Opening a share of the expedition's reward
/// cube adds one point (two when the cube is opened double); claiming the guaranteed reward uses the points it takes.
/// </summary>
/// <param name="DungeonId">The expedition's map id (one per mode: Krao Cave Exploration is 600002).</param>
/// <param name="Points">Points collected and not used yet.</param>
/// <param name="Claims">How often the guaranteed reward was claimed here.</param>
public sealed record CubeCeiling(uint DungeonId, uint Points, uint Claims);

/// <summary>
/// <c>12 57</c>: the guaranteed-reward counters of our character, sent at every world entry (with the inventory). An
/// expedition is listed once its cube was opened there.
/// </summary>
public sealed record CubeCeilingInfoEvent(long TimestampMs, IReadOnlyList<CubeCeiling> Dungeons) : GameEvent(TimestampMs);

/// <summary><c>13 57</c>: the reward cube can be opened (half a second after the clear); the mid bosses whose shares are in it.</summary>
public sealed record CubeRewardStartEvent(long TimestampMs, IReadOnlyList<uint> NpcCodes) : GameEvent(TimestampMs);

/// <summary>An item and its count in a reward.</summary>
public sealed record RewardItem(uint ItemId, uint Count);

/// <summary>
/// <c>15 57</c>: what opening the reward cube gave us (Kinah and Enhance Stones included). The items also arrive in
/// the bag through the usual slot updates.
/// </summary>
/// <param name="Shares">How many shares were opened (2 = the cube opened double, for twice the Odyle).</param>
public sealed record CubeRewardEvent(long TimestampMs, ushort Result, byte Shares, IReadOnlyList<RewardItem> Items) : GameEvent(TimestampMs);

/// <summary><c>17 57</c>: a guaranteed reward was claimed (picked from the expedition's list).</summary>
/// <param name="Head">The first two bytes (<c>00 02</c> on the one claim seen, which succeeded; their meaning is open).</param>
/// <param name="PointsLeft">The points left after the claim.</param>
/// <param name="Claims">How often the guaranteed reward was claimed here, this claim included.</param>
public sealed record CubeCeilingRewardEvent(long TimestampMs, ushort Head, uint DungeonId, RewardItem Item, uint PointsLeft, uint Claims)
    : GameEvent(TimestampMs);

/// <summary><c>2E 57</c>: a mid boss of the expedition died (its share is added to the reward cube).</summary>
public sealed record MiddleBossRewardEvent(long TimestampMs, uint NpcCode) : GameEvent(TimestampMs);

/// <summary><c>02 E0</c>: <paramref name="EntityId"/> finished group request <paramref name="Key"/> (a scripted scene of the instance).</summary>
public sealed record GroupRequestProgressedEvent(long TimestampMs, ulong Key, uint EntityId) : GameEvent(TimestampMs);

/// <summary><c>04 E0</c>: group request <paramref name="Key"/> is complete.</summary>
public sealed record GroupRequestCompleteEvent(long TimestampMs, ulong Key) : GameEvent(TimestampMs);

/// <summary>A point counter of one type.</summary>
public sealed record PointCount(byte Type, ushort Points);

/// <summary>
/// <c>2E E2</c> (used Daevanion points) or <c>2F E2</c> (bonus points from items, <paramref name="FromItems"/>): five
/// point types; only type 1 has moved in our captures (it rises as Daevanion nodes are opened).
/// </summary>
public sealed record DaevanionPointsEvent(long TimestampMs, bool FromItems, IReadOnlyList<PointCount> Points) : GameEvent(TimestampMs);

/// <summary><c>05 51</c>: a skill point counter (types 1 and 2 seen) and its new value.</summary>
public sealed record SkillPointsEvent(long TimestampMs, IReadOnlyList<PointCount> Points) : GameEvent(TimestampMs);
