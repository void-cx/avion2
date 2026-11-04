namespace Avion2.Protocol;

/// <summary><c>14 90</c>: one of our pets reached <paramref name="Level"/> (25 souls of its kind spent for level 2).</summary>
/// <param name="CreatureType">The pet's creature type, as in <see cref="PetAcquiredEvent"/>.</param>
public sealed record PetLevelUpEvent(long TimestampMs, uint PetId, uint Level, byte CreatureType) : GameEvent(TimestampMs);
