namespace Avion2.Protocol;

public static partial class Opcodes
{
    /// <summary>
    /// <c>50 36</c>: another player's profile, sent when we open it in the game (name, class, level, item level,
    /// legion, gear, combat power). Only the answer is readable; the request is encrypted.
    /// </summary>
    public const ushort CharacterProfile = 0x5036;
}
