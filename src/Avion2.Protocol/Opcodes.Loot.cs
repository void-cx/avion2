namespace Avion2.Protocol;

public static partial class Opcodes
{
    // Picking loot up (56 family), the Legion Hall (8A) and instance counters (61). Names after the client's message
    // list (A2Core's usmap catalogue).

    /// <summary><c>21 56</c> ItemLooting_RS: the answer to our own pick-up of a ground drop (result, the drop's uid, auto-loot).</summary>
    public const ushort ItemLooting = 0x2156;

    /// <summary><c>45 8A</c> GuildAgitInfo_RS: our legion's Legion Hall and when its rent ends, at most map loads.</summary>
    public const ushort LegionHallInfo = 0x458A;

    /// <summary><c>08 61</c> DungeonClearKillNpc_NT: one byte at every instance entry (0 in every record seen).</summary>
    public const ushort DungeonClearKillNpc = 0x0861;

    /// <summary><c>09 61</c> DungeonDeathLimit_NT: a <c>u32</c> at every instance entry (0 in every record seen).</summary>
    public const ushort DungeonDeathLimit = 0x0961;
}
