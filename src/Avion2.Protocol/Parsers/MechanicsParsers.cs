namespace Avion2.Protocol.Parsers;

/// <summary>Boss and fight mechanics: casts with a cast time, telegraphs, the stagger gauge. See docs/PROTOCOL.md.</summary>
public static class MechanicsParsers
{
    /// <summary>Decodes the opcode if it is one of this area's; false otherwise.</summary>
    public static bool TryParse(ushort opcode, ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit) => opcode switch
    {
        Opcodes.StaggerGauge => ParseStaggerGauge(body, ts, emit),
        Opcodes.NpcCastStart => ParseNpcCastStart(body, ts, emit),
        Opcodes.NpcCastStop => ParseNpcCastStop(body, ts, emit),
        Opcodes.SkillTelegraph => ParseTelegraph(body, ts, emit),
        _ => false,
    };

    /// <summary>
    /// <c>05 E0</c>: entity varint, flags u8, state u8, [flags&amp;1: max u32], [flags&amp;2: left u32], u8.
    /// Seen as <c>03 01 max left status</c> (the gauge) and <c>00 03 02</c> (broken).
    /// </summary>
    public static bool ParseStaggerGauge(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint entity) || entity == 0 || !r.TryU8(out byte flags) || !r.TryU8(out byte state))
        {
            return false;
        }

        uint max = 0, left = 0;
        if (((flags & 1) != 0 && !r.TryU32(out max)) || ((flags & 2) != 0 && !r.TryU32(out left)) || !r.TryU8(out byte status))
        {
            return false;
        }

        if (flags == 3 && state == 1 && max > 0 && left <= max)
        {
            emit(new StaggerGaugeEvent(ts, entity, max, left, status));
            return true;
        }

        if (flags == 0 && state == 3)
        {
            emit(new StaggerBrokenEvent(ts, entity));
            return true;
        }

        return false;
    }

    /// <summary>
    /// <c>09 38</c>: caster varint, flags u8, skill u32, u8 (1), kind u8 (2), target varint, facing f32,
    /// [kind 2: position 3×f32], cast time varint ms, u8, [flags&amp;2: varint (10000)],
    /// [flags&amp;4: count u8, count × (entity varint, position 3×f32)].
    /// </summary>
    public static bool ParseNpcCastStart(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint caster) || caster == 0 || !r.TryU8(out _) || !r.TryU32(out uint skill) || skill == 0 ||
            !r.TryU8(out _) || !r.TryU8(out byte kind) || !r.TryVarint(out uint target) || !r.TryF32(out float facing))
        {
            return false;
        }

        Vec3? at = null;
        if (kind == 2)
        {
            if (!TryVec3(ref r, out var position))
            {
                return false;
            }

            at = IsWorldPoint(position) ? position : null;
        }

        if (!r.TryVarint(out uint castMs) || castMs is 0 or > 120_000)
        {
            return false;
        }

        float? heading = float.IsFinite(facing) && Math.Abs(facing) <= 720 ? ((facing % 360) + 360) % 360 : null;
        emit(new NpcCastEvent(ts, caster, skill, target == 0 ? null : target, heading, at, castMs));
        return true;
    }

    /// <summary><c>0C 38</c>: caster varint, result u16, skill u32, cooldown varint ms.</summary>
    public static bool ParseNpcCastStop(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint caster) || caster == 0 || !r.TryU16(out ushort result) || !r.TryU32(out uint skill) || skill == 0 ||
            !r.TryVarint(out uint cooldown))
        {
            return false;
        }

        emit(new NpcCastStopEvent(ts, caster, result, skill, cooldown));
        return true;
    }

    /// <summary>
    /// <c>48 38</c>: caster varint, skill u32, cast seq u8, u32 (1), index u16, position 3×f32, facing f32 (degrees),
    /// delay u16 ms.
    /// </summary>
    public static bool ParseTelegraph(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint caster) || caster == 0 || !r.TryU32(out uint skill) || skill == 0 || !r.TryU8(out byte seq) ||
            !r.TryU32(out _) || !r.TryU16(out ushort index) || !TryVec3(ref r, out var position) || !r.TryF32(out float facing) ||
            !r.TryU16(out ushort delay))
        {
            return false;
        }

        if (!IsWorldPoint(position) || !float.IsFinite(facing))
        {
            return false;
        }

        emit(new TelegraphEvent(ts, caster, skill, seq, index, position, ((facing % 360) + 360) % 360, delay));
        return true;
    }

    /// <summary>A world position; a height near 0 is fine (the air raid's telegraphs lie at z ≈ -4).</summary>
    private static bool IsWorldPoint(Vec3 p) => p.IsWorldPoint;

    private static bool TryVec3(ref SpanReader r, out Vec3 position)
    {
        position = default;
        if (!r.TryF32(out float x) || !r.TryF32(out float y) || !r.TryF32(out float z))
        {
            return false;
        }

        position = new Vec3(x, y, z);
        return true;
    }
}
