using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>A character of ours confirmed by an identity record.</summary>
/// <param name="MaxHp">Highest HP observed for this character (max HP is not sent explicitly).</param>
public sealed record KnownCharacter(string Name, GameClass Class, ushort ServerId, DateTimeOffset LastSeen, uint MaxHp = 0);
