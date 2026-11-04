namespace Avion2.Protocol;

/// <summary>
/// <c>01 8A</c>: the answer to creating a legion. <paramref name="Result"/> 0 is success (then the legion follows);
/// anything else failed (2070 seen, with the rest empty).
/// </summary>
/// <param name="LegionId">The legion's number on its server.</param>
public sealed record LegionCreatedEvent(long TimestampMs, ushort Result, uint LegionId, ushort ServerId, string Name) : GameEvent(TimestampMs);

/// <summary><c>0B 8A</c>: the answer to leaving our legion; <paramref name="Result"/> 0 is success.</summary>
public sealed record LegionLeftEvent(long TimestampMs, ushort Result) : GameEvent(TimestampMs);

/// <summary><c>35 8A</c>: the answer to disbanding our legion; <paramref name="Result"/> 0 is success.</summary>
public sealed record LegionDisbandedEvent(long TimestampMs, ushort Result) : GameEvent(TimestampMs);

/// <summary>
/// <c>0C 8A</c>: a member is out of our legion. <paramref name="Reason"/> is the game's EGuildUpdateReason (2 when we
/// disbanded it, naming us).
/// </summary>
public sealed record LegionMemberLeftEvent(long TimestampMs, string Name, byte Reason) : GameEvent(TimestampMs);

/// <summary><c>39 8A</c>: a member of our legion came online (us at login, and when we created one).</summary>
public sealed record LegionMemberOnlineEvent(long TimestampMs, string Name) : GameEvent(TimestampMs);

/// <summary>A legion on a legion list (<c>07 8A</c>, <c>09 8A</c>).</summary>
/// <param name="LegionId">The legion's number on its server.</param>
/// <param name="JoinRule">How players join (the game's EGuildJoinRule; 0, 2 and 3 seen).</param>
public sealed record LegionListing(
    ushort ServerId,
    uint LegionId,
    string Name,
    string Leader,
    byte Level,
    int Members,
    int MaxMembers,
    string Intro,
    byte JoinRule);

/// <summary>
/// <c>07 8A</c> (the legion window's list) or <c>09 8A</c> (a name search): legions with their leader, level, member
/// count and introduction, as the game has them.
/// </summary>
public sealed record LegionListEvent(long TimestampMs, bool Search, IReadOnlyList<LegionListing> Legions) : GameEvent(TimestampMs);
