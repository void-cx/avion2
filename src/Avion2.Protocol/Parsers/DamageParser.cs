namespace Avion2.Protocol.Parsers;

/// <summary>
/// <c>04 38</c> direct damage. Layout (after opcode), verified against live captures (2026-10-13):
/// <code>
/// target varint, switch varint, flag varint, actor varint, skill u32, uid u8, hitType varint,
/// [switch&amp;0x02: plotter u8, restoreHp varint, angle u8],
/// [switch&amp;0x01: u32 ?],
/// effect u32        skill effect code (skill×100 + n)
/// seq u32           hit index within the cast (1, 2, 3 …)
/// [00 pad] scalar varint
/// [layout ≠ 0: value varint]
/// [switch&amp;0x20: count varint, count × varint]   additional hits, already included in value
/// [switch&amp;0x40: u8, u32 linked effect code]
/// seq u8, 00        terminator, repeats the low byte of seq
/// </code>
/// Layout (switch &amp; 0x0F) 0 carries no value: aura/effect ticks such as mantras.
/// </summary>
public static class DamageParser
{
    public const long MaxDamage = 100_000_000;
    private const int MaxExtraHits = 25;
    private const int MinRecordBytes = 14;

    public static int Parse(ReadOnlySpan<byte> body, long timestampMs, Action<GameEvent> emit)
    {
        int records = 0;
        var reader = new SpanReader(body);
        while (reader.Remaining >= MinRecordBytes)
        {
            int start = reader.Position;
            if (!TryParseRecord(ref reader, timestampMs, emit))
            {
                reader.Position = start;
                break;
            }

            records++;

            // A repeated opcode may introduce the next record.
            if (reader.Remaining >= 2 && reader.Rest[0] == 0x04 && reader.Rest[1] == 0x38)
            {
                reader.Position += 2;
            }
        }

        return records;
    }

    private static bool TryParseRecord(ref SpanReader r, long ts, Action<GameEvent> emit)
    {
        if (!r.TryVarint(out uint target) || !r.TryVarint(out uint sw) || !r.TryVarint(out _) ||
            !r.TryVarint(out uint actor) || !r.TryU32(out uint rawSkill) || !r.TryU8(out _) ||
            !r.TryVarint(out uint hitTypeValue))
        {
            return false;
        }

        if (target == 0 || actor == 0 || hitTypeValue > 0xFF)
        {
            return false;
        }

        uint skill = SkillIds.FromWire(rawSkill);
        if (skill is 0 or > 299_999_999)
        {
            return false;
        }

        var hitType = (HitType)hitTypeValue;
        var flags = hitType == HitType.Critical ? HitFlags.Critical : HitFlags.None;
        if ((sw & 0x02) != 0)
        {
            if (!r.TryU8(out byte plotter) || !r.TryVarint(out _) || !r.TryU8(out byte angle))
            {
                return false;
            }

            flags |= DecodePlotter(plotter);
            flags |= angle switch
            {
                0x01 => HitFlags.Back,
                0x02 => HitFlags.Front,
                _ => HitFlags.None,
            };
        }

        if ((sw & 0x01) != 0 && !r.TrySkip(4))
        {
            return false;
        }

        if (!r.TryU32(out _) || !r.TryU32(out uint seq) || seq == 0 || seq > 0xFFFF)
        {
            return false;
        }

        if (!r.TryVarint(out uint scalar))
        {
            return false;
        }

        if (scalar == 0 && !r.TryVarint(out scalar))
        {
            return false; // zero pad before the scalar
        }

        int layout = (int)(sw & 0x0F);
        uint value = 0;
        if (layout != 0 && !r.TryVarint(out value))
        {
            return false;
        }

        int hitCount = 1;
        long extraDamage = 0;
        if ((sw & 0x20) != 0)
        {
            if (!r.TryVarint(out uint extra) || extra > MaxExtraHits)
            {
                return false;
            }

            for (int i = 0; i < extra; i++)
            {
                if (!r.TryVarint(out uint extraHit))
                {
                    return false;
                }

                extraDamage += extraHit;
            }

            hitCount += (int)extra;
            if (extra > 0)
            {
                flags |= HitFlags.MultiHit;
            }
        }

        if ((sw & 0x40) != 0)
        {
            if (!r.TrySkip(5))
            {
                return false;
            }

            flags |= HitFlags.LinkedEffect;
        }

        // Terminator repeats the hit index. Older captures sometimes end without it.
        var rest = r.Rest;
        if (rest.Length >= 2 && rest[0] == (byte)seq && rest[1] == 0x00)
        {
            r.Position += 2;
        }
        else if (!rest.IsEmpty)
        {
            return false;
        }

        if (value > MaxDamage)
        {
            return false;
        }

        if (SkillIds.IsSpiritToOwnerLink(skill))
        {
            emit(new SummonLinkEvent(ts, SummonId: actor, OwnerId: target));
            return true;
        }

        if (SkillIds.IsOwnerToSpiritLink(skill))
        {
            emit(new SummonLinkEvent(ts, SummonId: target, OwnerId: actor));
            return true;
        }

        if (layout == 0 && hitType is not (HitType.Miss or HitType.Resist))
        {
            return true; // effect tick without a damage value
        }

        emit(new DamageEvent(ts, actor, target, SkillIds.ToBase(skill), rawSkill, value, hitType, flags, hitCount, scalar, extraDamage));
        return true;
    }

    public static HitFlags DecodePlotter(byte plotter)
    {
        var flags = HitFlags.None;
        if ((plotter & 0x01) != 0) flags |= HitFlags.ShieldBlock;
        if ((plotter & 0x02) != 0) flags |= HitFlags.Parry;
        if ((plotter & 0x04) != 0) flags |= HitFlags.Perfect;
        if ((plotter & 0x08) != 0) flags |= HitFlags.Double;
        if ((plotter & 0x10) != 0) flags |= HitFlags.IronWall;
        if ((plotter & 0x20) != 0) flags |= HitFlags.Regeneration;
        if ((plotter & 0x40) != 0) flags |= HitFlags.PerfectBlock;
        return flags;
    }
}
