namespace Avion2.Protocol.Parsers;

/// <summary>Parsers for the smaller, fixed-shape records. Offsets start after the 2-byte opcode.</summary>
public static class RecordParsers
{
    private static ReadOnlySpan<byte> NpcCodeMarkerA => [0x00, 0x40, 0x02];
    private static ReadOnlySpan<byte> NpcCodeMarkerB => [0x00, 0x00, 0x02];

    /// <summary><c>05 38</c>: target varint, effect u8, actor varint, ? varint, skill×100 u32, amount varint.</summary>
    public static bool ParseDamageOverTime(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint target) || !r.TryU8(out byte effect) || !r.TryVarint(out uint actor) ||
            !r.TryVarint(out _) || !r.TryU32(out uint rawSkill) || !r.TryVarint(out uint amount))
        {
            return false;
        }

        if (target == 0 || actor == 0 || amount > DamageParser.MaxDamage)
        {
            return false;
        }

        uint skill = rawSkill / 100;
        switch (effect)
        {
            case 0x02 or 0x0A:
                emit(new DamageEvent(ts, actor, target, SkillIds.ToBase(skill), rawSkill, amount, HitType.Normal, HitFlags.OverTime, 1, 0));
                return true;
            case 0x01 or 0x09 or 0x0B:
                emit(new HealEvent(ts, actor, target, skill, amount, OverTime: effect == 0x0B));
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// <c>33 36</c>: id varint, mask1 u32, mask2 u8 (bit0 = name present), len u8, name,
    /// server u16, class u32, u8, level u32.
    /// </summary>
    public static bool ParseSelfIdentity(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint id) || !r.TryU32(out _) || !r.TryU8(out byte mask2) || (mask2 & 0x01) == 0)
        {
            return false;
        }

        if (!r.TryU8(out byte nameLength) || nameLength is 0 or > 36 || !r.TryUtf8(nameLength, out string name))
        {
            return false;
        }

        if (!r.TryU16(out ushort server) || server is < 1000 or > 2999)
        {
            return false;
        }

        r.TryU32(out uint classCode);
        r.TrySkip(1);
        r.TryU32(out uint level);
        emit(new SelfIdentityEvent(ts, id, name, server, GameClasses.FromRosterCode(classCode), level));
        return true;
    }

    /// <summary><c>45 36</c>: id varint, mask1 u32, mask2 u8, [len u8, name], [server u16].</summary>
    public static bool ParsePlayerSpawn(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint id) || id == 0 || !r.TryU32(out _) || !r.TryU8(out byte mask2) || (mask2 & 0x01) == 0)
        {
            return false;
        }

        if (!r.TryU8(out byte nameLength) || nameLength is 0 or > 36 || !r.TryUtf8(nameLength, out string name))
        {
            return false;
        }

        ushort? server = r.TryU16(out ushort s) && s is >= 1000 and <= 2999 ? s : null;
        emit(new PlayerSpawnEvent(ts, id, name, server));
        return true;
    }

    /// <summary>
    /// <c>41 36</c>: id varint, mask u32 (low byte = kind), gate u8 (bit0 = inline name), [len u8, name], ...
    /// NPC code is the u24 before <c>00 40 02</c> / <c>00 00 02</c>; HP follows as <c>01 cur max</c>.
    /// </summary>
    public static bool ParseNpcSpawn(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint id) || id == 0 || !r.TryU32(out uint mask))
        {
            return false;
        }

        if (id > 1_000_000)
        {
            id = (id & 0x3FFF) | 0x4000;
        }

        int afterId = r.Position - 4;
        string? inlineName = null;
        int scanFrom = afterId;
        if (r.TryU8(out byte gate) && (gate & 0x01) != 0 &&
            r.TryU8(out byte nameLength) && nameLength is > 0 and <= 36 && r.TryUtf8(nameLength, out string name))
        {
            inlineName = name;
            scanFrom = r.Position;
        }

        // Live captures: id, 04 20 00, code u24, 00 00 02 — the code overlaps the "mask", so scan from the id.
        uint? npcCode = null;
        uint? maxHp = null;
        var window = body[scanFrom..Math.Min(body.Length, scanFrom + 60)];
        int marker = window.IndexOf(NpcCodeMarkerA);
        if (marker < 3)
        {
            marker = window.IndexOf(NpcCodeMarkerB);
        }

        if (marker >= 3)
        {
            npcCode = (uint)(window[marker - 3] | window[marker - 2] << 8 | window[marker - 1] << 16);
            maxHp = FindMaxHp(body, scanFrom + marker + 3);
        }

        emit(new NpcSpawnEvent(ts, id, (byte)(mask & 0xFF), npcCode, maxHp, inlineName));
        return true;
    }

    private static uint? FindMaxHp(ReadOnlySpan<byte> body, int from)
    {
        int end = Math.Min(body.Length, from + 64);
        for (int i = from; i < end; i++)
        {
            if (body[i] != 0x01)
            {
                continue;
            }

            var r = new SpanReader(body, i + 1);
            if (r.TryVarint(out uint current) && r.TryVarint(out uint max) && max >= 100 && current <= max)
            {
                return max;
            }
        }

        return null;
    }

    /// <summary><c>42 36</c>: entity varint, varint, kind varint (1 loaded dead, 3 combat death, 7 left world).</summary>
    public static bool ParseDeath(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint id) || id == 0 || !r.TryVarint(out _) || !r.TryVarint(out uint kind))
        {
            return false;
        }

        var deathKind = kind switch
        {
            1 => DeathKind.LoadedDead,
            3 => DeathKind.Combat,
            7 => DeathKind.LeftWorld,
            _ => DeathKind.Unknown,
        };
        emit(new DeathEvent(ts, id, deathKind));
        return true;
    }

    /// <summary><c>21 36</c>: count u32, mapId u32.</summary>
    public static bool ParseMapLoad(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryU32(out _) || !r.TryU32(out uint mapId) || mapId == 0)
        {
            return false;
        }

        emit(new MapLoadEvent(ts, mapId));
        return true;
    }

    /// <summary><c>23 36</c> with entity 0 is a self teleport / zone change.</summary>
    public static bool ParsePosition(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        if (body.Length > 0 && body[0] == 0x00)
        {
            emit(new ZoneChangeEvent(ts));
            return true;
        }

        return false;
    }

    /// <summary>
    /// <c>00 8D</c> resource update: entity varint, kind u8, ? u8, resource u8, value u32, [tail].
    /// Resource 0 is HP (mobs and us); 1 looks like MP, 3/4/6 are other gauges. Max HP is not included.
    /// </summary>
    public static bool ParseRemainingHp(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint id) || id == 0 || !r.TrySkip(2) || !r.TryU8(out byte resource) || resource != 0 ||
            !r.TryU32(out uint hp) || hp > 1_000_000_000)
        {
            return false;
        }

        emit(new HpUpdateEvent(ts, id, hp, MaxHp: 0));
        return true;
    }

    /// <summary><c>21 8D</c>: entity varint, varint (0), state varint (1 = entered combat, 0 = left).</summary>
    public static bool ParseCombatState(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint id) || id == 0 || !r.TryVarint(out _) || !r.TryVarint(out uint state) || state > 1)
        {
            return false;
        }

        emit(new CombatStateEvent(ts, id, state == 1));
        return true;
    }

    /// <summary><c>4A 36</c> own stats: entity varint, ... Only ever sent about the local player (verified on 3 captures).</summary>
    public static bool ParseOwnStats(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint id) || id == 0)
        {
            return false;
        }

        emit(new SelfHintEvent(ts, id));
        return true;
    }

    /// <summary><c>1B 92</c>: actor varint, hp varint, maxHp varint.</summary>
    public static bool ParseHpUpdate(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint id) || id is < 100 or > 9_999_999 || !r.TryVarint(out uint hp) ||
            !r.TryVarint(out uint max) || max == 0 || max > 50_000_000 || hp > max)
        {
            return false;
        }

        emit(new HpUpdateEvent(ts, id, hp, max));
        return true;
    }
}
