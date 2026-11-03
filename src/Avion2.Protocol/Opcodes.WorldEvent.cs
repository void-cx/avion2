namespace Avion2.Protocol;

public static partial class Opcodes
{
    // World events (43 and 60 families). Names after the client's message list (A2Core's usmap catalogue).

    /// <summary><c>01 43</c> AirspaceBattle_NT: the next Airspace Battle (start and end time), sent at world entry.</summary>
    public const ushort AirspaceBattle = 0x0143;

    /// <summary><c>00 60</c> FieldEventInfo_NT: the field events open right now (Shugo Festival, Dimensional Invasion), at zone entry.</summary>
    public const ushort FieldEventList = 0x0060;

    /// <summary><c>01 60</c> FieldEventChanged_NT: one field event opened, changed phase or closed.</summary>
    public const ushort FieldEventChanged = 0x0160;

    // Leaving an instance and talking to NPCs (40 and E2 families)

    /// <summary><c>05 40</c> MapExit_RS: the reply to using an instance's exit, in the same instant as the map load out.</summary>
    public const ushort MapExit = 0x0540;

    /// <summary><c>07 40</c> NpcConversation_RS: talked to a teleporter or instance NPC (comes with the map load it leads to).</summary>
    public const ushort NpcConversation = 0x0740;

    /// <summary><c>41 E2</c> StartNpcInteraction_RS: a conversation or station window opened.</summary>
    public const ushort NpcInteractionStart = 0x41E2;

    /// <summary><c>43 E2</c> FinishNpcInteraction_RS: the conversation or window was closed.</summary>
    public const ushort NpcInteractionFinish = 0x43E2;

    /// <summary><c>46 E2</c> CancelNpcInteraction_NT: the server ended the conversation (walked away, interrupted).</summary>
    public const ushort NpcInteractionCancel = 0x46E2;
}
