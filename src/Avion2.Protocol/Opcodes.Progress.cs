namespace Avion2.Protocol;

/// <summary>Character progress: item level, items used, currencies with caps, tickets, the Daeva Pass, experience, pets.</summary>
public static partial class Opcodes
{
    /// <summary><c>1D 56</c> ItemEquipLevelChange: our item level now and the highest it has been.</summary>
    public const ushort SelfItemLevel = 0x1D56;

    /// <summary><c>1C 56</c> ItemEquipChange: a player's equipment changed (ours or a player nearby), with the new item level.</summary>
    public const ushort EquipmentChange = 0x1C56;

    /// <summary><c>13 56</c> ItemUse_RS: we used an item from the bag.</summary>
    public const ushort ItemUseResult = 0x1356;

    /// <summary><c>16 56</c> ItemUse_NT: an item of ours was used (potions and scrolls, also by auto-use).</summary>
    public const ushort ItemUseNotice = 0x1656;

    /// <summary><c>A5 FF</c> / <c>A6 FF</c>: Kinah gained today toward the daily limit (at login / at each gain).</summary>
    public const ushort KinahLimitInit = 0xA5FF;
    public const ushort KinahLimitUpdate = 0xA6FF;

    /// <summary><c>AB FF</c> (from the 2026-10-29 client; <c>A9 FF</c> before): Abyss Points this week and the weekly cap.</summary>
    public const ushort AbyssPointLimit = 0xABFF;
    public const ushort AbyssPointLimitOld = 0xA9FF;

    /// <summary><c>0B 61</c> / <c>0C 61</c>: entry tickets of instances and keys (the list at login / one change).</summary>
    public const ushort ContentTicketList = 0x0B61;
    public const ushort ContentTicketUpdate = 0x0C61;

    /// <summary><c>24 E3</c> BattlePassInfoList: Daeva Pass experience per season.</summary>
    public const ushort DaevaPassInfo = 0x24E3;

    /// <summary><c>07 8D</c> ExpUpdate: our experience within the level, what was gained and why.</summary>
    public const ushort ExperienceUpdate = 0x078D;

    /// <summary><c>00 90</c> RideInitData: pets owned and souls collected (at login).</summary>
    public const ushort PetRoster = 0x0090;

    /// <summary><c>0D 90</c> / <c>0E 90</c>: pet souls gained (from kills) / spent (5 when a pet is acquired).</summary>
    public const ushort PetSoulsGained = 0x0D90;
    public const ushort PetSoulsSpent = 0x0E90;

    /// <summary><c>09 90</c> CharRideAcquire_NT: we acquired a pet.</summary>
    public const ushort PetAcquired = 0x0990;
}
