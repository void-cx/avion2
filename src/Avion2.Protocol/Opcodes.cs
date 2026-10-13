namespace Avion2.Protocol;

/// <summary>
/// Known opcodes, written in wire order: <c>0x0438</c> is the byte sequence <c>04 38</c>.
/// See docs/PROTOCOL.md. Values reflect the post-June-2026 client.
/// </summary>
public static class Opcodes
{
    public const ushort Bundle = 0xFFFF;

    public const ushort Damage = 0x0438;
    public const ushort DamageOverTime = 0x0538;
    public const ushort BuffAdded = 0x2A38;
    public const ushort BuffChanged = 0x2B38;
    public const ushort BuffRemoved = 0x2C38;

    public const ushort Ping = 0x0336;
    public const ushort MapLoad = 0x2136;
    public const ushort Position = 0x2336;
    public const ushort SelfIdentity = 0x3336;
    public const ushort NpcSpawn = 0x4136;
    public const ushort Death = 0x4236;
    public const ushort PlayerSpawn = 0x4536;
    public const ushort AllStats = 0x4936;
    public const ushort OwnStats = 0x4A36;

    public const ushort RemainingHp = 0x008D;
    public const ushort CombatToggle = 0x218D;
    public const ushort Ownership = 0x048D;
    public const ushort HpUpdate = 0x1B92;
    public const ushort PartyRoster = 0x0297;

    public static ushort Read(ReadOnlySpan<byte> payload) => (ushort)((payload[0] << 8) | payload[1]);

    public static string Format(ushort opcode) => $"{opcode >> 8:X2} {opcode & 0xFF:X2}";
}
