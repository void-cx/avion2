namespace Avion2.Protocol;

/// <summary>One airspace of the Airspace Battle as <c>01 43</c> lists it (five in our captures, all in phase 0).</summary>
public sealed record AirspaceState(byte AirspaceId, uint Phase);

/// <summary>
/// <c>01 43</c>: the Airspace Battle the game announces (Wednesday 17:00–17:10 Central European time in our captures).
/// Times are Unix ms. <paramref name="Announced"/> is false for the one-byte form, which carries no times.
/// </summary>
/// <param name="Phase">The battle's phase (0 in every record seen, before the battle).</param>
/// <param name="ExtraMs">A third time some records may carry (never seen; null when absent).</param>
public sealed record AirspaceBattleEvent(
    long TimestampMs,
    bool Announced,
    uint Phase,
    long StartMs,
    long EndMs,
    long? ExtraMs,
    IReadOnlyList<AirspaceState> Airspaces) : GameEvent(TimestampMs);

/// <summary>
/// A field event's state: ids 110101–110901 are the nine Shugo Festival minigames (every hour at :00), 120101,
/// 120201 and 120301 the Dimensional Invasion variants (every hour at :30). Times are Unix ms.
/// </summary>
/// <param name="Phase">1 = the 3-minute lead-in, 2 = the event itself, 0 = closed (then all times are 0).</param>
/// <param name="StartMs">When the event opened (the start of phase 1).</param>
public sealed record FieldEventState(uint EventId, byte Phase, long StartMs, long PhaseStartMs, long PhaseEndMs)
{
    public bool Closed => Phase == 0;
}

/// <summary><c>00 60</c>: the field events open right now (empty outside them), sent at zone entry.</summary>
public sealed record FieldEventListEvent(long TimestampMs, IReadOnlyList<FieldEventState> Events) : GameEvent(TimestampMs);

/// <summary><c>01 60</c>: a field event opened, moved to its next phase, or closed.</summary>
public sealed record FieldEventChangedEvent(long TimestampMs, FieldEventState State) : GameEvent(TimestampMs);

/// <summary><c>05 40</c>: we used the instance's exit (result 0); the map load out comes in the same instant.</summary>
public sealed record MapExitEvent(long TimestampMs, ushort Result) : GameEvent(TimestampMs);

/// <summary><c>07 40</c>: we talked to a teleporter or instance NPC (result 0 = it went through).</summary>
public sealed record NpcConversationEvent(long TimestampMs, ushort Result, uint NpcId) : GameEvent(TimestampMs);

/// <summary>
/// <c>41 E2</c>: a conversation with an NPC (or a crafting station's window) opened. <paramref name="NpcId"/> is the
/// NPC's entity; failures carry result 15880 or 15881 and entity 0.
/// </summary>
/// <param name="ExpiresMs">Server time (Unix ms) when the game closes the conversation by itself: 5 minutes later.</param>
public sealed record NpcInteractionStartEvent(long TimestampMs, ushort Result, uint NpcId, long ExpiresMs) : GameEvent(TimestampMs);

/// <summary>
/// <c>43 E2</c> (we closed it) or <c>46 E2</c> (<paramref name="Cancelled"/>: the server ended it): the conversation
/// with <paramref name="NpcId"/> is over.
/// </summary>
public sealed record NpcInteractionEndEvent(long TimestampMs, ushort Result, uint NpcId, bool Cancelled) : GameEvent(TimestampMs);
