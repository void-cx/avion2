namespace Avion2.Protocol;

public static partial class Opcodes
{
    // Expedition rewards (57 family, the run of 2E 57 PartyDungeonRewardMiddleBossKill). Names after the client's
    // message list (A2Core's usmap catalogue); the run's offset is confirmed by 2E 57 (glass-cannon) and the layouts.

    /// <summary><c>12 57</c> OdEnergyCubeCeilingInfo_NT: the guaranteed-reward points of every expedition, at each world entry.</summary>
    public const ushort CubeCeilingInfo = 0x1257;

    /// <summary><c>13 57</c> OdEnergyCubeRewardStart_NT: the reward cube is ready (half a second after the clear), with the mid bosses killed.</summary>
    public const ushort CubeRewardStart = 0x1357;

    /// <summary><c>15 57</c> OdEnergyCubeReward_RS: what opening the reward cube gave.</summary>
    public const ushort CubeReward = 0x1557;

    /// <summary><c>17 57</c> OdEnergyCubeCeilingReward_RS: a guaranteed reward was claimed.</summary>
    public const ushort CubeCeilingReward = 0x1757;

    /// <summary><c>2E 57</c> PartyDungeonRewardMiddleBossKill_NT: a mid boss died; its share goes into the reward cube.</summary>
    public const ushort MiddleBossReward = 0x2E57;

    // Group requests, Daevanion and skill points (E0, E2 and 51 families)

    /// <summary><c>02 E0</c> GroupRequestProgressed_NT: one character finished a request the whole group waits on (a scripted scene).</summary>
    public const ushort GroupRequestProgressed = 0x02E0;

    /// <summary><c>04 E0</c> GroupRequestComplete_NT: everyone finished it.</summary>
    public const ushort GroupRequestComplete = 0x04E0;

    /// <summary><c>2E E2</c> DaevanionPointUpdate_NT: the Daevanion points used, per point type.</summary>
    public const ushort DaevanionPointUpdate = 0x2EE2;

    /// <summary><c>2F E2</c> UpdateItemDaevanionPoint_NT: bonus Daevanion points from items, per point type.</summary>
    public const ushort ItemDaevanionPointUpdate = 0x2FE2;

    /// <summary><c>05 51</c> SkillPointUpdate_NT: a skill point counter changed.</summary>
    public const ushort SkillPointUpdate = 0x0551;
}
