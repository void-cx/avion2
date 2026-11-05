namespace Avion2.Protocol;

/// <summary><c>50 36</c>: we opened <paramref name="Name"/>'s profile in the game.</summary>
/// <param name="Legion">Their legion's name; null without a legion.</param>
/// <param name="CombatPower">Their combat power; null when the record does not have it where expected.</param>
public sealed record CharacterProfileEvent(
    long TimestampMs,
    string Name,
    ushort ServerId,
    GameClass Class,
    uint Level,
    uint ItemLevel,
    string? Legion,
    uint? CombatPower) : GameEvent(TimestampMs);
