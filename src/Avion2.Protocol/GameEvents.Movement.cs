namespace Avion2.Protocol;

/// <summary>
/// A 10 Hz movement offset (<c>1D</c>, <c>20</c>, <c>25</c>, <c>2A</c>, <c>32</c>, <c>38 37</c>): how far the entity
/// moved in one 100 ms server tick, in whole cm, from its previous movement record (keyframe or offset). The record
/// has no absolute position; <c>MovementTracker</c> (Analysis) adds the offsets up from the last keyframe.
/// </summary>
/// <param name="Heading">Degrees (0..360, 0 = +x turning toward +y).</param>
public sealed record MoveOffsetEvent(long TimestampMs, uint EntityId, sbyte Dx, sbyte Dy, sbyte Dz, float Heading) : GameEvent(TimestampMs);

/// <summary><c>4E 36</c>: an entity turns (the server also sends it for us, whose movement it does not echo otherwise).</summary>
/// <param name="Facing">Where it turns to, degrees (0..360, 0 = +x turning toward +y).</param>
/// <param name="From">The direction it faced before, degrees.</param>
/// <param name="Reason">0..3, meaning unknown.</param>
public sealed record RotateEvent(long TimestampMs, uint EntityId, float Facing, float From, byte Reason) : GameEvent(TimestampMs);

/// <summary><c>46 37</c>: the height (z, ≈ cm) where the entity (in practice we) last stood on the ground.</summary>
public sealed record GroundHeightEvent(long TimestampMs, uint EntityId, float Z) : GameEvent(TimestampMs);
