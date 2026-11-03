namespace Avion2.Protocol;

/// <summary>Boss and fight mechanics: casts with a cast time, telegraphs, the stagger gauge.</summary>
public static partial class Opcodes
{
    /// <summary>An NPC started a skill with a cast time (the boss's wind-up).</summary>
    public const ushort NpcCastStart = 0x0938;

    /// <summary>A cast with a cast time ended early: the caster died, or it was interrupted.</summary>
    public const ushort NpcCastStop = 0x0C38;

    /// <summary>Where an NPC skill's area will land, and when.</summary>
    public const ushort SkillTelegraph = 0x4838;

    /// <summary>An entity's stagger gauge: its maximum and what is left, or that it broke.</summary>
    public const ushort StaggerGauge = 0x05E0;
}
