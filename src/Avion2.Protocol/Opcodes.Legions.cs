namespace Avion2.Protocol;

public static partial class Opcodes
{
    // Our legion changing, and the legion lists (8A family). Names after the client's message list (A2Core's usmap
    // catalogue: message number = 0x3875 + the opcode's first byte).

    /// <summary><c>01 8A</c> GuildCreate_RS: the answer to creating a legion (result, then the new legion).</summary>
    public const ushort LegionCreate = 0x018A;

    /// <summary><c>07 8A</c> GuildRecommendedList_RS: the legion list the legion window shows (up to 100, most members first).</summary>
    public const ushort LegionRecommendedList = 0x078A;

    /// <summary><c>09 8A</c> GuildSearch_RS: the legions a name search found, in the layout of <c>07 8A</c>.</summary>
    public const ushort LegionSearch = 0x098A;

    /// <summary><c>0B 8A</c> GuildLeave_RS: the answer to leaving our legion.</summary>
    public const ushort LegionLeave = 0x0B8A;

    /// <summary><c>0C 8A</c> GuildLeave_NT: a member is out of our legion (name, reason).</summary>
    public const ushort LegionMemberLeft = 0x0C8A;

    /// <summary><c>35 8A</c> GuildDisband_RS: the answer to disbanding our legion.</summary>
    public const ushort LegionDisband = 0x358A;

    /// <summary><c>39 8A</c> GuildMemberConnected_NT: a member of our legion came online (us too, at login).</summary>
    public const ushort LegionMemberOnline = 0x398A;
}
