namespace Avion2.Protocol.Parsers;

/// <summary>Our own loot pick-ups, the Legion Hall rent and the instance entry counters. See docs/PROTOCOL.md.</summary>
public static class LootParsers
{
    /// <summary>Decodes the opcode if it is one of this area's; false otherwise.</summary>
    public static bool TryParse(ushort opcode, ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit) => opcode switch
    {
        Opcodes.ItemLooting => ParseItemLooting(body, ts, emit),
        Opcodes.LegionHallInfo => ParseLegionHallInfo(body, ts, emit),
        Opcodes.DungeonClearKillNpc => ParseDungeonClearKillNpc(body, ts, emit),
        Opcodes.DungeonDeathLimit => ParseDungeonDeathLimit(body, ts, emit),
        _ => false,
    };

    /// <summary><c>21 56</c>: result <c>u16</c>, drop uid <c>i64</c>, auto-loot <c>u8</c>. 11 bytes.</summary>
    public static bool ParseItemLooting(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (body.Length != 11 || !r.TryU16(out ushort result) || !r.TryI64(out long uid) || !r.TryU8(out byte auto) || auto > 1)
        {
            return false;
        }

        emit(new LootPickedEvent(ts, result, uid, auto == 1));
        return true;
    }

    /// <summary><c>45 8A</c>: result <c>u16</c>, hall <c>u32</c>, rent end <c>i64</c> (Unix ms). 14 bytes.</summary>
    public static bool ParseLegionHallInfo(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (body.Length != 14 || !r.TryU16(out ushort result) || !r.TryU32(out uint hall) || !r.TryI64(out long ends) || ends < 0)
        {
            return false;
        }

        emit(new LegionHallInfoEvent(ts, result, hall, ends));
        return true;
    }

    /// <summary><c>08 61</c>: one byte.</summary>
    public static bool ParseDungeonClearKillNpc(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        if (body.Length != 1)
        {
            return false;
        }

        emit(new DungeonClearKillNpcEvent(ts, body[0]));
        return true;
    }

    /// <summary><c>09 61</c>: one <c>u32</c>.</summary>
    public static bool ParseDungeonDeathLimit(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (body.Length != 4 || !r.TryU32(out uint value))
        {
            return false;
        }

        emit(new DungeonDeathLimitEvent(ts, value));
        return true;
    }
}
