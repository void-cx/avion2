namespace Avion2.Protocol.Parsers;

/// <summary>World events and schedules: the Airspace Battle, field events, leaving a dungeon, NPC conversations. See docs/PROTOCOL.md.</summary>
public static class WorldEventParsers
{
    /// <summary>A field event record's kind byte: 3 = open (three times follow), 0 = closed (one far-future time follows).</summary>
    private const byte FieldEventOpen = 3;
    private const byte FieldEventClosed = 0;

    /// <summary>Bytes after an airspace's id and phase in <c>01 43</c> (zero in every record seen).</summary>
    private const int AirspaceTail = 14;

    /// <summary>Decodes the opcode if it is one of this area's; false otherwise.</summary>
    public static bool TryParse(ushort opcode, ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit) => opcode switch
    {
        Opcodes.AirspaceBattle => ParseAirspaceBattle(body, ts, emit),
        Opcodes.FieldEventList => ParseFieldEventList(body, ts, emit),
        Opcodes.FieldEventChanged => ParseFieldEventChanged(body, ts, emit),
        Opcodes.MapExit => ParseMapExit(body, ts, emit),
        Opcodes.NpcConversation => ParseNpcConversation(body, ts, emit),
        Opcodes.NpcInteractionStart => ParseNpcInteractionStart(body, ts, emit),
        Opcodes.NpcInteractionFinish => ParseNpcInteractionFinish(body, ts, emit),
        Opcodes.NpcInteractionCancel => ParseNpcInteractionCancel(body, ts, emit),
        _ => false,
    };

    /// <summary>
    /// <c>01 43</c>: <c>u8 1</c>, <c>u8 has extra time</c>, <c>u8</c> (1), phase <c>u32</c>, start <c>i64</c>, end
    /// <c>i64</c>, [extra <c>i64</c>], <c>varint n</c>, n × (<c>u8 1</c>, airspace <c>u8</c>, phase <c>u32</c>, 14 bytes).
    /// The single byte <c>00</c> is the form without a battle.
    /// </summary>
    public static bool ParseAirspaceBattle(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        if (body.Length == 1 && body[0] == 0)
        {
            emit(new AirspaceBattleEvent(ts, false, 0, 0, 0, null, []));
            return true;
        }

        var r = new SpanReader(body);
        if (!r.TryU8(out byte announced) || announced != 1 || !r.TryU8(out byte hasExtra) || hasExtra > 1 || !r.TryU8(out _) ||
            !r.TryU32(out uint phase) || !r.TryI64(out long start) || !r.TryI64(out long end) || end < start || start <= 0)
        {
            return false;
        }

        long? extra = null;
        if (hasExtra == 1)
        {
            if (!r.TryI64(out long third))
            {
                return false;
            }

            extra = third;
        }

        if (!r.TryVarint(out uint count) || count > 64)
        {
            return false;
        }

        var airspaces = new List<AirspaceState>((int)count);
        for (int i = 0; i < count; i++)
        {
            if (!r.TryU8(out byte marker) || marker != 1 || !r.TryU8(out byte id) || !r.TryU32(out uint airspacePhase) || !r.TrySkip(AirspaceTail))
            {
                return false;
            }

            airspaces.Add(new AirspaceState(id, airspacePhase));
        }

        if (r.Remaining != 0)
        {
            return false;
        }

        emit(new AirspaceBattleEvent(ts, true, phase, start, end, extra, airspaces));
        return true;
    }

    /// <summary><c>00 60</c>: <c>varint n</c>, then n field event records as in <c>01 60</c>.</summary>
    public static bool ParseFieldEventList(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint count) || count > 256)
        {
            return false;
        }

        var events = new List<FieldEventState>((int)count);
        for (int i = 0; i < count; i++)
        {
            if (ReadFieldEvent(ref r) is not { } state)
            {
                return false;
            }

            events.Add(state);
        }

        if (r.Remaining != 0)
        {
            return false;
        }

        emit(new FieldEventListEvent(ts, events));
        return true;
    }

    /// <summary><c>01 60</c>: one field event record.</summary>
    public static bool ParseFieldEventChanged(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (ReadFieldEvent(ref r) is not { } state || r.Remaining != 0)
        {
            return false;
        }

        emit(new FieldEventChangedEvent(ts, state));
        return true;
    }

    /// <summary>
    /// A field event record: kind <c>u8</c>, event <c>varint</c>, phase <c>u8</c>; kind 3 then has the event's start,
    /// the phase's start and the phase's end (<c>i64</c> ms each), kind 0 (closed, phase 0) one <c>i64</c> far in the
    /// future (2100-01-01), which is dropped.
    /// </summary>
    private static FieldEventState? ReadFieldEvent(ref SpanReader r)
    {
        if (!r.TryU8(out byte kind) || !r.TryVarint(out uint id) || id == 0 || !r.TryU8(out byte phase))
        {
            return null;
        }

        if (kind == FieldEventClosed)
        {
            return phase == 0 && r.TryI64(out _) ? new FieldEventState(id, 0, 0, 0, 0) : null;
        }

        if (kind != FieldEventOpen || phase == 0 ||
            !r.TryI64(out long start) || !r.TryI64(out long phaseStart) || !r.TryI64(out long phaseEnd) ||
            start <= 0 || phaseStart < start || phaseEnd < phaseStart)
        {
            return null;
        }

        return new FieldEventState(id, phase, start, phaseStart, phaseEnd);
    }

    /// <summary><c>05 40</c>: result <c>u16</c>.</summary>
    public static bool ParseMapExit(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryU16(out ushort result) || r.Remaining != 0)
        {
            return false;
        }

        emit(new MapExitEvent(ts, result));
        return true;
    }

    /// <summary><c>07 40</c>: <c>u8 0</c>, result <c>u16</c>, NPC entity <c>u32</c>.</summary>
    public static bool ParseNpcConversation(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryU8(out byte lead) || lead != 0 || !r.TryU16(out ushort result) || !r.TryU32(out uint npc) || r.Remaining != 0)
        {
            return false;
        }

        emit(new NpcConversationEvent(ts, result, npc));
        return true;
    }

    /// <summary><c>41 E2</c>: result <c>u16</c>, NPC entity <c>u32</c>, expiry <c>i64</c> (server time + 300 s).</summary>
    public static bool ParseNpcInteractionStart(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryU16(out ushort result) || !r.TryU32(out uint npc) || !r.TryI64(out long expires) || r.Remaining != 0)
        {
            return false;
        }

        emit(new NpcInteractionStartEvent(ts, result, npc, expires));
        return true;
    }

    /// <summary><c>43 E2</c>: result <c>u16</c>, NPC entity <c>u32</c>.</summary>
    public static bool ParseNpcInteractionFinish(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryU16(out ushort result) || !r.TryU32(out uint npc) || r.Remaining != 0)
        {
            return false;
        }

        emit(new NpcInteractionEndEvent(ts, result, npc, Cancelled: false));
        return true;
    }

    /// <summary><c>46 E2</c>: NPC entity <c>u32</c>.</summary>
    public static bool ParseNpcInteractionCancel(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryU32(out uint npc) || r.Remaining != 0)
        {
            return false;
        }

        emit(new NpcInteractionEndEvent(ts, 0, npc, Cancelled: true));
        return true;
    }
}
