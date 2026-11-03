namespace Avion2.Protocol;

public static partial class Opcodes
{
    public const ushort FinderJoinRequest = 0x0797;
    public const ushort FinderMemberJoined = 0x0B97;
    public const ushort FinderSelfReady = 0x1597;
    public const ushort FinderMemberReady = 0x1697;
    public const ushort FinderStart = 0x1897;
    public const ushort FinderRoomClosed = 0x1B97;
    public const ushort FinderLeft = 0x1D97;
    public const ushort FinderSlotUpdated = 0x1F97;
    public const ushort FinderJoinRequestCancelled = 0x2597;
    public const ushort PartyBossTarget = 0x2092;
}
