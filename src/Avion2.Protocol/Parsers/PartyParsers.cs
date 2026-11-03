namespace Avion2.Protocol.Parsers;

/// <summary>Party member status and party-wide records beyond the roster. See docs/PROTOCOL.md.</summary>
public static class PartyParsers
{
    /// <summary>Decodes the opcode if it is one of this area's; false otherwise.</summary>
    public static bool TryParse(ushort opcode, ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit) => opcode switch
    {
        Opcodes.PartyUpdateMemberStatus => ParseMemberStatus(body, ts, emit),
        Opcodes.PartyDisconnectMember => ParseCharacter(body, emit, id => new PartyMemberDisconnectedEvent(ts, id)),
        Opcodes.PartyNewLeader => ParseCharacter(body, emit, id => new PartyLeaderEvent(ts, id)),
        Opcodes.PartyDisband => body.Length == 1 && Emit(emit, new PartyDisbandedEvent(ts, body[0])),
        Opcodes.PartyLeft => ParseLeft(body, ts, emit),
        Opcodes.PartyAbnormalAdd => ParseAbnormalAdd(body, ts, emit),
        Opcodes.PartyAbnormalRemove => ParseAbnormalRemove(body, ts, emit),
        _ => false,
    };

    /// <summary>
    /// <c>1C 92</c>: character i64, class u32 (roster code), map key i64 (map id, instance), subzone u32, position
    /// 3 × f32, level, conqueror level, item level (u32), legion name (varint length, UTF-8; 0 = none), emblem u32,
    /// can loot u8, combat power i64, latency u8.
    /// </summary>
    public static bool ParseMemberStatus(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryI64(out long character) || character == 0 || !r.TryU32(out uint classCode) ||
            !r.TryU32(out uint map) || !r.TryU32(out uint instance) || !r.TryU32(out uint subzone) ||
            !r.TryF32(out float x) || !r.TryF32(out float y) || !r.TryF32(out float z) ||
            !r.TryU32(out uint level) || !r.TrySkip(4) || !r.TryU32(out uint itemLevel) ||
            !r.TryVarint(out uint legionLength) || legionLength > 64)
        {
            return false;
        }

        string? legion = null;
        if (legionLength > 0 && !r.TryUtf8((int)legionLength, out legion))
        {
            return false;
        }

        if (!r.TrySkip(4 + 1) || !r.TryI64(out long power) || !r.TrySkip(1) || r.Remaining != 0)
        {
            return false;
        }

        var status = new PartyMemberStatus(map, instance, subzone, new Vec3(x, y, z), level, itemLevel, Clamp(power), legion);
        emit(new PartyMemberStatusEvent(ts, (uint)character, GameClasses.FromRosterCode(classCode), status));
        return true;
    }

    /// <summary><c>1F 93</c>: party key i64, reason u8.</summary>
    public static bool ParseLeft(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (body.Length != 9 || !r.TryI64(out long key) || key == 0 || !r.TryU8(out byte reason))
        {
            return false;
        }

        emit(new PartyLeftEvent(ts, key, reason));
        return true;
    }

    /// <summary><c>0E 92</c>: entity varint, count u8, abnormal u32 × count.</summary>
    public static bool ParseAbnormalAdd(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint entity) || entity == 0 || !r.TryU8(out byte count) || r.Remaining != count * 4)
        {
            return false;
        }

        var ids = new uint[count];
        for (int i = 0; i < count; i++)
        {
            r.TryU32(out ids[i]);
        }

        emit(new PartyAbnormalEvent(ts, entity, ids, Added: true));
        return true;
    }

    /// <summary><c>1A 92</c>: entity varint, abnormal u32.</summary>
    public static bool ParseAbnormalRemove(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint entity) || entity == 0 || !r.TryU32(out uint abnormal) || r.Remaining != 0)
        {
            return false;
        }

        emit(new PartyAbnormalEvent(ts, entity, [abnormal], Added: false));
        return true;
    }

    /// <summary>A body that is just a character id (i64).</summary>
    private static bool ParseCharacter(ReadOnlySpan<byte> body, Action<GameEvent> emit, Func<uint, GameEvent> make)
    {
        var r = new SpanReader(body);
        if (body.Length != 8 || !r.TryI64(out long character) || character == 0)
        {
            return false;
        }

        emit(make((uint)character));
        return true;
    }

    private static bool Emit(Action<GameEvent> emit, GameEvent e)
    {
        emit(e);
        return true;
    }

    private static uint Clamp(long value) => (uint)Math.Clamp(value, 0, uint.MaxValue);
}
