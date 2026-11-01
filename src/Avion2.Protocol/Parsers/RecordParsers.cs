namespace Avion2.Protocol.Parsers;

/// <summary>Parsers for the smaller, fixed-shape records. Offsets start after the 2-byte opcode.</summary>
public static class RecordParsers
{
    private static ReadOnlySpan<byte> NpcCodeMarkerA => [0x00, 0x40, 0x02];
    private static ReadOnlySpan<byte> SelfCombatPowerTrailer => [0x05, 0x01, 0x91, 0x00];
    private static ReadOnlySpan<byte> PlayerCombatPowerMarker => [0xF4, 0xCB, 0x1F];
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
        emit(new SelfIdentityEvent(ts, id, name, server, GameClasses.FromRosterCode(classCode), level, FindSelfCombatPower(body))
        {
            CharacterId = FindCharacterId(body, server),
        });
        return true;
    }

    /// <summary>
    /// The character id sits after our position, right before the server id: <c>id u32, 00 00, server u16</c>
    /// (e.g. <c>32 30 01 00 00 00 29 05</c>). The first such spot with a plausible id is taken.
    /// </summary>
    public static uint? FindCharacterId(ReadOnlySpan<byte> body, ushort server)
    {
        for (int i = 4; i + 4 <= body.Length; i++)
        {
            if (body[i] == 0 && body[i + 1] == 0 && body[i + 2] == (byte)server && body[i + 3] == (byte)(server >> 8))
            {
                uint id = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(body[(i - 4)..]);
                if (id is >= 1_000 and < 100_000_000)
                {
                    return id;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Our combat power: <c>cp u32, 0 u32, best? u32, 0 u32, 05 01 91 00</c> (verified against the armory:
    /// 37,381 → 37.38K). Its offset varies with the record's contents, so the trailer is searched.
    /// </summary>
    private static uint? FindSelfCombatPower(ReadOnlySpan<byte> body)
    {
        int at = body.IndexOf(SelfCombatPowerTrailer);
        while (at >= 16)
        {
            var r = new SpanReader(body, at - 16);
            if (r.TryU32(out uint cp) && r.TryU32(out uint zero1) && r.TryU32(out _) && r.TryU32(out uint zero2) &&
                zero1 == 0 && zero2 == 0 && cp is > 0 and < 10_000_000)
            {
                return cp;
            }

            int next = body[(at + 1)..].IndexOf(SelfCombatPowerTrailer);
            at = next < 0 ? -1 : at + 1 + next;
        }

        return null;
    }

    /// <summary>Other players: after the last <c>F4 CB 1F</c>, from +11, a u32 in 1..10M followed by a u32 0 (prior art; unverified).</summary>
    private static uint? FindPlayerCombatPower(ReadOnlySpan<byte> body)
    {
        int marker = body.LastIndexOf(PlayerCombatPowerMarker);
        if (marker < 0)
        {
            return null;
        }

        for (int i = marker + 11; i + 8 <= body.Length && i < marker + 80; i++)
        {
            var r = new SpanReader(body, i);
            if (r.TryU32(out uint value) && r.TryU32(out uint zero) && value is >= 1 and <= 10_000_000 && zero == 0)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary><c>45 36</c>: id varint, mask1 u32, mask2 u8, [len u8, name], class u32 (roster code), ...</summary>
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

        // Live captures: the class code follows the name directly (e.g. 19 00 00 00 = 25 = Sorcerer).
        var cls = r.TryU32(out uint classCode) ? GameClasses.FromRosterCode(classCode) : GameClass.Unknown;
        var position = WorldParsers.FindPosition(body, r.Position, 32);
        emit(new PlayerSpawnEvent(ts, id, name, null, cls, FindPlayerCombatPower(body), position));
        if (position is { } p)
        {
            emit(new PositionEvent(ts, id, p, null, PositionSource.Spawn));
        }
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

        // Position: 9 bytes after the id for plain NPCs (checked against their movement tracks); after the name otherwise.
        var position = inlineName is null ? WorldParsers.FindPosition(body, afterId + 9, 1) : null;
        position ??= WorldParsers.FindPosition(body, inlineName is null ? afterId + 9 : scanFrom, 40);
        emit(new NpcSpawnEvent(ts, id, (byte)(mask & 0xFF), npcCode, maxHp, inlineName, position));
        if (position is { } p)
        {
            emit(new PositionEvent(ts, id, p, null, PositionSource.Spawn));
        }

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

    /// <summary>
    /// <c>21 36</c>: count u32, mapId u32, u64, spawn x/y/z f32, facing f32, then 11 bytes and the content type u8
    /// (offset 43; 0 open world, 30 sealed dungeon, 79 expedition, 37/38 stronghold).
    /// </summary>
    public static bool ParseMapLoad(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryU32(out _) || !r.TryU32(out uint mapId) || mapId == 0)
        {
            return false;
        }

        emit(new MapLoadEvent(ts, mapId, body.Length > 43 ? body[43] : (byte)0));
        return true;
    }

    /// <summary>
    /// <c>02 97</c>: a header (party id, recruiting text), then one entry per slot: flag u8 (<c>0C</c>/<c>1C</c> here,
    /// <c>0E</c>/<c>1E</c> elsewhere / offline), slot u8, character id u32, <c>00 00</c>, 2 bytes, name length u8, name,
    /// class u32 (roster code), level u32, ... Empty slots are zeros, so entries are found by their shape.
    /// </summary>
    public static bool ParsePartyRoster(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var members = new List<PartyMember>();
        for (int i = 0; i + 14 < body.Length; i++)
        {
            byte flag = body[i];
            if (flag is not (0x0C or 0x0E or 0x1C or 0x1E) || body[i + 1] is 0 or > 8 || body[i + 6] != 0 || body[i + 7] != 0)
            {
                continue;
            }

            var r = new SpanReader(body, i + 2);
            if (!r.TryU32(out uint characterId) || characterId == 0)
            {
                continue;
            }

            r.Position += 4;
            if (!r.TryU8(out byte length) || length is 0 or > 36 || !r.TryUtf8(length, out string name) ||
                !r.TryU32(out uint classCode) || GameClasses.FromRosterCode(classCode) is var cls && cls == GameClass.Unknown)
            {
                continue;
            }

            if (members.All(m => m.CharacterId != characterId))
            {
                members.Add(new PartyMember(body[i + 1], characterId, name, cls, (flag & 0x02) == 0));
            }

            i = r.Position - 1;
        }

        if (members.Count == 0)
        {
            return false;
        }

        emit(new PartyRosterEvent(ts, members));
        return true;
    }

    /// <summary>
    /// <c>31 8D</c>: 00 00, node varint, attempt u32, outcome u8 (bit 0 success, bits 1–2 the better grade: 0 fail,
    /// 1 success, 6 fail on a fine node, 7 fine success), 00 00, grade u8 (1 normal, 2 fine), 00 00 00, steps u8, then
    /// the steps of the race on screen (27 bytes each).
    /// </summary>
    public static bool ParseGatherStart(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body, 2);
        if (body.Length < 2 || body[0] != 0 || body[1] != 0 || !r.TryVarint(out uint node) || node == 0 ||
            !r.TryU32(out uint attempt) || !r.TryU8(out byte outcome) || !r.TryU16(out _) || !r.TryU8(out byte grade) ||
            r.Remaining < 4)
        {
            return false;
        }

        r.Position += 3;
        r.TryU8(out byte steps);
        emit(new GatherStartEvent(ts, node, attempt, (outcome & 1) != 0, (outcome & 6) != 0 || grade >= 2, steps));
        return true;
    }

    /// <summary><c>39 36</c>: object varint, u8, u8 (06 19 when a gathering was aborted), actor varint, attempt u32.</summary>
    public static bool ParseInteractionInterrupted(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint obj) || obj == 0 || !r.TryU8(out _) || !r.TryU8(out _) || !r.TryVarint(out uint actor) ||
            !r.TryU32(out uint attempt))
        {
            return false;
        }

        emit(new InteractionInterruptedEvent(ts, obj, actor, attempt));
        return true;
    }

    /// <summary><c>33 8D</c>: result u8 (0 success, 1 fine success, 2 failed), u8, node varint, item u32, count u32, …</summary>
    public static bool ParseGatherEnd(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryU8(out byte result) || result > 2 || !r.TryU8(out _) || !r.TryVarint(out uint node) ||
            !r.TryU32(out uint item) || !r.TryU32(out uint count))
        {
            return false;
        }

        emit(new GatherEndEvent(ts, node, result, item, count));
        return true;
    }

    /// <summary>
    /// <c>6F 8D</c>: 00, u8, station varint, recipe u32, outcome u8 (bit 0 the first race succeeds, bit 2 a combo race
    /// follows, bit 3 the combo succeeds: 01 success, 05 a combo that fails, 0D a combo that succeeds), 00, then each
    /// race: steps u8, the steps (10 bytes each: kind u8, 00, two equal u32), 00.
    /// </summary>
    public static bool ParseCraftStart(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body, 2);
        if (body.Length < 2 || body[0] != 0 || !r.TryVarint(out uint station) || station == 0 || !r.TryU32(out uint recipe) ||
            !r.TryU8(out byte outcome) || !r.TryU8(out _) || !r.TryU8(out byte steps))
        {
            return false;
        }

        bool combo = (outcome & 4) != 0;
        emit(new CraftStartEvent(ts, station, recipe, combo ? (outcome & 8) != 0 : (outcome & 1) != 0, combo, steps));
        return true;
    }

    /// <summary><c>71 8D</c>: result u8 (0 success, 1 combo success, 2 failed), u8, station varint, u8.</summary>
    public static bool ParseCraftEnd(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryU8(out byte result) || result > 2 || !r.TryU8(out _) || !r.TryVarint(out uint station) || station == 0)
        {
            return false;
        }

        emit(new CraftEndEvent(ts, station, result));
        return true;
    }

    /// <summary><c>18 38</c>: skill u32, <c>01</c>, the chain skill it opens u32.</summary>
    public static bool ParseChainOpen(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryU32(out uint after) || !r.TryU8(out _) || !r.TryU32(out uint skill) || after == 0 || skill == 0)
        {
            return false;
        }

        emit(new ChainStepEvent(ts, after, skill, Open: true));
        return true;
    }

    /// <summary><c>19 38</c>: <c>01</c>, the chain skill that closed u32.</summary>
    public static bool ParseChainClose(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryU8(out _) || !r.TryU32(out uint skill) || skill == 0)
        {
            return false;
        }

        emit(new ChainStepEvent(ts, 0, skill, Open: false));
        return true;
    }

    /// <summary><c>34 38</c>: <c>00 00 00</c>, the selected target varint (0 = none).</summary>
    public static bool ParseTargetSelected(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        if (body.Length < 4 || body[0] != 0 || body[1] != 0 || body[2] != 0)
        {
            return false;
        }

        var r = new SpanReader(body[3..]);
        if (!r.TryVarint(out uint target))
        {
            return false;
        }

        emit(new TargetSelectedEvent(ts, target));
        return true;
    }

    /// <summary><c>35 38</c>: entity varint, <c>00</c>, its target varint (0 = none).</summary>
    public static bool ParseTargetOfTarget(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint entity) || entity == 0 || !r.TryU8(out _) || !r.TryVarint(out uint target))
        {
            return false;
        }

        emit(new TargetOfTargetEvent(ts, entity, target));
        return true;
    }

    /// <summary><c>00 61</c>: map u32, stage u8, from u64 (Unix ms), to u64 (Unix ms, 0 = open).</summary>
    public static bool ParseInstanceStage(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryU32(out uint map) || map == 0 || !r.TryU8(out byte stage) || !r.TryI64(out long from) || !r.TryI64(out long to))
        {
            return false;
        }

        emit(new InstanceStageEvent(ts, map, stage, from, to));
        return true;
    }

    /// <summary><c>2E 92</c>: event id u32, name length u8, name (ASCII), …</summary>
    public static bool ParseMapEventStart(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryU32(out uint id) || !r.TryU8(out byte length) || length is 0 or > 120 || !r.TryUtf8(length, out string name))
        {
            return false;
        }

        emit(new MapEventStartEvent(ts, id, name));
        return true;
    }

    /// <summary><c>2D 92</c>: event id u32, state u8 (2 running, 3 done), 01, step u8, counter count u8, counters …</summary>
    public static bool ParseMapEventProgress(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryU32(out uint id) || !r.TryU8(out byte state) || state is not (2 or 3) || !r.TryU8(out _) ||
            !r.TryU8(out byte step) || step == 0)
        {
            return false;
        }

        emit(new MapEventProgressEvent(ts, id, step, state == 3));
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
    /// Resource 0 is HP (mobs and us), 6 flight time; 1 looks like MP, 3/4 are other gauges. Max HP is not included.
    /// </summary>
    public static bool ParseRemainingHp(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint id) || id == 0 || !r.TrySkip(2) || !r.TryU8(out byte resource) || !r.TryU32(out uint value))
        {
            return false;
        }

        switch (resource)
        {
            case 0 when value <= 1_000_000_000:
                emit(new HpUpdateEvent(ts, id, value, MaxHp: 0));
                return true;
            case 6:
                emit(new FlightTimeEvent(ts, id, value));
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// <c>01 91</c> field boss table: <c>00 00, map u32, count u8</c>, then per boss
    /// <c>state u8, spawn code varint, [state 1: x f32, y f32, z f32], [0–1 extra byte], timestamp u64 ms</c>.
    /// Alive bosses carry their spawn time, dead ones their respawn time (verified on live captures).
    /// </summary>
    public static bool ParseFieldBossTable(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryU16(out ushort zero) || zero != 0 || !r.TryU32(out uint mapId) || !r.TryU8(out byte count) || count == 0)
        {
            return false;
        }

        var entries = new List<FieldBossEntry>(count);
        for (int i = 0; i < count; i++)
        {
            if (!r.TryU8(out byte state) || state > 1 || !r.TryVarint(out uint code))
            {
                break;
            }

            float? x = null, y = null, z = null;
            if (state == 1)
            {
                if (r.Remaining < 12)
                {
                    break;
                }

                x = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(r.Rest);
                y = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(r.Rest[4..]);
                z = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(r.Rest[8..]);
                r.Position += 12;
            }

            // Some entries carry one extra byte before the timestamp; accept the first plausible epoch-ms value.
            long? when = null;
            for (int skip = 0; skip <= 2 && when is null; skip++)
            {
                var probe = new SpanReader(r.Data, r.Position + skip);
                if (probe.TryI64(out long value) && value is > 1_600_000_000_000 and < 2_500_000_000_000)
                {
                    when = value;
                    r.Position += skip + 8;
                }
            }

            if (when is null)
            {
                break;
            }

            entries.Add(new FieldBossEntry(code, state == 1, when.Value, x, y, z));
        }

        if (entries.Count == 0)
        {
            return false;
        }

        emit(new FieldBossTableEvent(ts, mapId, entries));
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
