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
    public const ushort SkillCast = 0x0238;

    // Movement (37 family). Positions are world units (≈ cm), z up.
    public const ushort MoveRelative = 0x1A37;
    public const ushort MoveSteer = 0x1B37;
    public const ushort Move = 0x1C37;
    public const ushort MoveVelocity = 0x1D37;
    public const ushort Dash = 0x2F37;
    public const ushort Relocate = 0x3C37;

    public const ushort Ping = 0x0336;

    /// <summary>Server clock, u64 ms, sent 20 times a second.</summary>
    public const ushort ServerClock = 0x0036;
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
    public const ushort FieldBossTable = 0x0191;

    // World objects (hidden cubes, gather nodes, doors…)
    public const ushort WorldObject = 0x3436;
    public const ushort InteractionDone = 0x3A36;

    // Inventory (56 family, our own character)
    public const ushort InventoryList = 0x1156;
    public const ushort InventoryItem = 0x1B56;

    // Legion (8A family)
    public const ushort LegionDetails = 0x038A;
    public const ushort LegionRoster = 0x058A;
    public const ushort LegionTag = 0x338A;

    public static ushort Read(ReadOnlySpan<byte> payload) => (ushort)((payload[0] << 8) | payload[1]);

    public static string Format(ushort opcode) => $"{opcode >> 8:X2} {opcode & 0xFF:X2}";
}
