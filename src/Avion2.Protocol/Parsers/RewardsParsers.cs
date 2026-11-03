namespace Avion2.Protocol.Parsers;

/// <summary>
/// Expedition rewards (the reward cube and its guaranteed reward), group requests, Daevanion and skill points. See
/// docs/PROTOCOL.md.
/// </summary>
public static class RewardsParsers
{
    /// <summary>More entries than any list could hold: the record is something else.</summary>
    private const int MaxEntries = 256;

    /// <summary>Decodes the opcode if it is one of this area's; false otherwise.</summary>
    public static bool TryParse(ushort opcode, ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit) => opcode switch
    {
        Opcodes.CubeCeilingInfo => ParseCeilingInfo(body, ts, emit),
        Opcodes.CubeRewardStart => ParseRewardStart(body, ts, emit),
        Opcodes.CubeReward => ParseCubeReward(body, ts, emit),
        Opcodes.CubeCeilingReward => ParseCeilingReward(body, ts, emit),
        Opcodes.MiddleBossReward => ParseMiddleBoss(body, ts, emit),
        Opcodes.GroupRequestProgressed => ParseGroupProgressed(body, ts, emit),
        Opcodes.GroupRequestComplete => ParseGroupComplete(body, ts, emit),
        Opcodes.DaevanionPointUpdate => ParsePoints(body, ts, emit, daevanion: true, fromItems: false),
        Opcodes.ItemDaevanionPointUpdate => ParsePoints(body, ts, emit, daevanion: true, fromItems: true),
        Opcodes.SkillPointUpdate => ParsePoints(body, ts, emit, daevanion: false, fromItems: false),
        _ => false,
    };

    /// <summary><c>12 57</c>: <c>varint n</c>, n × (dungeon <c>u32</c>, points <c>varint</c>, claims <c>varint</c>).</summary>
    public static bool ParseCeilingInfo(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint count) || count > MaxEntries)
        {
            return false;
        }

        var list = new List<CubeCeiling>((int)count);
        for (int i = 0; i < count; i++)
        {
            if (!r.TryU32(out uint dungeon) || !r.TryVarint(out uint points) || !r.TryVarint(out uint claims) || dungeon == 0)
            {
                return false;
            }

            list.Add(new CubeCeiling(dungeon, points, claims));
        }

        if (r.Remaining != 0)
        {
            return false;
        }

        emit(new CubeCeilingInfoEvent(ts, list));
        return true;
    }

    /// <summary><c>13 57</c>: <c>varint n</c>, n × NPC code <c>u32</c>.</summary>
    public static bool ParseRewardStart(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint count) || count > MaxEntries)
        {
            return false;
        }

        var npcs = new List<uint>((int)count);
        for (int i = 0; i < count; i++)
        {
            if (!r.TryU32(out uint npc))
            {
                return false;
            }

            npcs.Add(npc);
        }

        if (r.Remaining != 0)
        {
            return false;
        }

        emit(new CubeRewardStartEvent(ts, npcs));
        return true;
    }

    /// <summary><c>15 57</c>: result <c>u16</c>, shares <c>u8</c>, <c>varint n</c>, n × (item <c>u32</c>, count <c>u32</c>).</summary>
    public static bool ParseCubeReward(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryU16(out ushort result) || !r.TryU8(out byte shares) || !r.TryVarint(out uint count) || count > MaxEntries)
        {
            return false;
        }

        var items = new List<RewardItem>((int)count);
        for (int i = 0; i < count; i++)
        {
            if (!r.TryU32(out uint item) || !r.TryU32(out uint amount))
            {
                return false;
            }

            items.Add(new RewardItem(item, amount));
        }

        if (r.Remaining != 0)
        {
            return false;
        }

        emit(new CubeRewardEvent(ts, result, shares, items));
        return true;
    }

    /// <summary>
    /// <c>17 57</c>: <c>u16</c> (<c>00 02</c>), dungeon <c>u32</c>, item <c>u32</c>, count <c>u32</c>, points left
    /// <c>varint</c>, claims <c>varint</c>.
    /// </summary>
    public static bool ParseCeilingReward(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryU16(out ushort head) || !r.TryU32(out uint dungeon) || !r.TryU32(out uint item) || !r.TryU32(out uint count) ||
            !r.TryVarint(out uint left) || !r.TryVarint(out uint claims) || r.Remaining != 0 || dungeon == 0)
        {
            return false;
        }

        emit(new CubeCeilingRewardEvent(ts, head, dungeon, new RewardItem(item, count), left, claims));
        return true;
    }

    /// <summary><c>2E 57</c>: NPC code <c>u32</c>.</summary>
    public static bool ParseMiddleBoss(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryU32(out uint npc) || r.Remaining != 0)
        {
            return false;
        }

        emit(new MiddleBossRewardEvent(ts, npc));
        return true;
    }

    /// <summary><c>02 E0</c>: key <c>u64</c>, entity <c>u32</c>.</summary>
    public static bool ParseGroupProgressed(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryI64(out long key) || !r.TryU32(out uint entity) || r.Remaining != 0)
        {
            return false;
        }

        emit(new GroupRequestProgressedEvent(ts, (ulong)key, entity));
        return true;
    }

    /// <summary><c>04 E0</c>: key <c>u64</c>.</summary>
    public static bool ParseGroupComplete(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryI64(out long key) || r.Remaining != 0)
        {
            return false;
        }

        emit(new GroupRequestCompleteEvent(ts, (ulong)key));
        return true;
    }

    /// <summary><c>2E E2</c>, <c>2F E2</c>, <c>05 51</c>: <c>u8 n</c>, n × (type <c>u8</c>, points <c>u16</c>).</summary>
    public static bool ParsePoints(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit, bool daevanion, bool fromItems)
    {
        var r = new SpanReader(body);
        if (!r.TryU8(out byte count) || count is 0 or > 16)
        {
            return false;
        }

        var points = new List<PointCount>(count);
        for (int i = 0; i < count; i++)
        {
            if (!r.TryU8(out byte type) || !r.TryU16(out ushort value))
            {
                return false;
            }

            points.Add(new PointCount(type, value));
        }

        if (r.Remaining != 0)
        {
            return false;
        }

        emit(daevanion ? new DaevanionPointsEvent(ts, fromItems, points) : new SkillPointsEvent(ts, points));
        return true;
    }
}
