namespace Avion2.Protocol;

public enum GameClass : byte
{
    Unknown = 0,
    Gladiator,
    Templar,
    Ranger,
    Assassin,
    Elementalist,
    Sorcerer,
    Cleric,
    Chanter,
    Fighter,
}

public static class GameClasses
{
    /// <summary>Class code used in identity and party-roster records (four codes per class).</summary>
    public static GameClass FromRosterCode(uint code) => code switch
    {
        >= 5 and <= 8 => GameClass.Gladiator,
        >= 9 and <= 12 => GameClass.Templar,
        >= 13 and <= 16 => GameClass.Ranger,
        >= 17 and <= 20 => GameClass.Assassin,
        >= 21 and <= 24 => GameClass.Elementalist,
        >= 25 and <= 28 => GameClass.Sorcerer,
        >= 29 and <= 32 => GameClass.Cleric,
        >= 33 and <= 36 => GameClass.Chanter,
        >= 45 and <= 48 => GameClass.Fighter,
        _ => GameClass.Unknown,
    };

    /// <summary>Player skills are 8 digits <c>CC SS NNNN</c>; CC is the class prefix.</summary>
    public static GameClass FromSkillId(uint skillId)
    {
        if (!SkillIds.IsPlayerSkill(skillId))
        {
            return GameClass.Unknown;
        }

        return (skillId / 1_000_000) switch
        {
            11 => GameClass.Gladiator,
            12 => GameClass.Templar,
            13 => GameClass.Assassin,
            14 => GameClass.Ranger,
            15 => GameClass.Sorcerer,
            16 => GameClass.Elementalist,
            17 => GameClass.Cleric,
            18 => GameClass.Chanter,
            19 => GameClass.Fighter,
            _ => GameClass.Unknown,
        };
    }
}

public static class SkillIds
{
    /// <summary>8-digit class skill with a non-zero SS group.</summary>
    public static bool IsPlayerSkill(uint id) => id is >= 11_000_000 and <= 19_999_999 && (id / 10_000) % 100 != 0;

    /// <summary>7-digit ids on direct damage records are monster skills.</summary>
    public static bool IsNpcSkill(uint id) => id is >= 1_000_000 and <= 9_999_999;

    public static bool IsSpiritToOwnerLink(uint id) => id is >= 16_990_000 and <= 16_999_999;

    public static bool IsOwnerToSpiritLink(uint id) => id is >= 16_770_000 and <= 16_779_999;

    /// <summary>Theostone item procs are sent as 3,0xx,xxx and are re-keyed to <c>id*10+1</c>.</summary>
    public static uint FromWire(uint raw) => raw is >= 3_000_000 and <= 3_099_999 ? raw * 10 + 1 : raw;

    /// <summary>
    /// Specialisation slots chosen for a player skill: the thousands, hundreds and tens digits of the wire id
    /// each name a slot (1–5). Returned as a bit mask, bit n = slot n.
    /// </summary>
    public static int SpecSlots(uint rawId)
    {
        if (!IsPlayerSkill(rawId))
        {
            return 0;
        }

        int mask = 0;
        uint digits = rawId % 10_000 / 10;
        for (int i = 0; i < 3; i++)
        {
            uint slot = digits % 10;
            if (slot is >= 1 and <= 5)
            {
                mask |= 1 << (int)slot;
            }

            digits /= 10;
        }

        return mask;
    }

    /// <summary>Strips the specialisation digits (low four) from player skill ids.</summary>
    public static uint ToBase(uint id) => IsPlayerSkill(id) ? id - id % 10_000 : id;
}
