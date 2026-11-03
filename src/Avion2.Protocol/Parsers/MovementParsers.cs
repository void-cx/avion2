using System.Numerics;

namespace Avion2.Protocol.Parsers;

/// <summary>
/// Movement offsets (10 Hz deltas), the keyframes of the fall, glide, flight, roll and swim streams, and the rarer
/// records that carry our own position, height or facing. Every layout has an exact length rule (bytes after the
/// entity varint), checked before anything is read; see docs/PROTOCOL.md.
/// </summary>
public static class MovementParsers
{
    /// <summary>Decodes the opcode if it is one of this area's; false otherwise.</summary>
    public static bool TryParse(ushort opcode, ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit) => opcode switch
    {
        Opcodes.MoveOffset or Opcodes.FallOffset or Opcodes.GlideOffset or Opcodes.FlyOffset or Opcodes.RollOffset or
            Opcodes.SwimOffset => ParseOffset(opcode, body, ts, emit),
        Opcodes.FallStart or Opcodes.FallSnapshot or Opcodes.GlideStart or Opcodes.GlideSnapshot or Opcodes.FlyStart or
            Opcodes.FlySnapshot or Opcodes.RollStart or Opcodes.SwimStart or Opcodes.SwimSnapshot or
            Opcodes.PositionNote1 or Opcodes.PositionNote2 or Opcodes.PositionNote3 => ParseKeyframe(opcode, body, ts, emit),
        Opcodes.GroundHeight => ParseGroundHeight(body, ts, emit),
        Opcodes.Rotate => ParseRotate(body, ts, emit),
        _ => false,
    };

    /// <summary>
    /// The offset records: flags u8, then int8 cm deltas for the axes whose flag bit is set, a heading u16 and, for
    /// the air and swim streams, a velocity (3 × f32, cm/s):
    /// <list type="bullet">
    /// <item><c>1D 37</c> (on foot): f, [S if f&amp;1], dx f&amp;2, dy f&amp;4, dz f&amp;8, hd, hd2, [S if not f&amp;1]; 6 + bits(f&amp;0x0e).</item>
    /// <item><c>20 37</c> (fall): f, dx f&amp;1, dy f&amp;2, dz always, hd, vel, [01 if f&amp;8]; 16 + bits(f&amp;0x0b).</item>
    /// <item><c>25 37</c> (glide): f, dx f&amp;1, dy f&amp;2, dz f&amp;4, hd, vel, [byte if f&amp;8]; 15 + bits(f&amp;0x0f).</item>
    /// <item><c>2A 37</c> (flight): f, [S if f&amp;1], dx f&amp;2, dy f&amp;4, dz f&amp;8, vel, hd, [S if f&amp;0x10 and not f&amp;1]; 15 + bits(f&amp;0x0e) + (f&amp;0x11 ≠ 0).</item>
    /// <item><c>38 37</c> (swim): as <c>2A 37</c> with hd before vel.</item>
    /// <item><c>32 37</c> (dodge roll): f, dx f&amp;1, dy f&amp;2, dz f&amp;4, u16 (0), hd, [byte if f&amp;8]; 5 + bits(f&amp;0x0f).</item>
    /// </list>
    /// </summary>
    public static bool ParseOffset(ushort opcode, ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint id) || id == 0 || r.Remaining < 1)
        {
            return false;
        }

        int f = r.Rest[0];
        int Bits(int mask) => BitOperations.PopCount((uint)(f & mask));
        // Flag bits of x, y, z (0 = always present), the length rule, and whether a state byte precedes the deltas
        var (bx, by, bz, length, lead) = opcode switch
        {
            Opcodes.MoveOffset => (2, 4, 8, 6 + Bits(0x0e), f & 1),
            Opcodes.FallOffset => (1, 2, 0, 16 + Bits(0x0b), 0),
            Opcodes.GlideOffset => (1, 2, 4, 15 + Bits(0x0f), 0),
            Opcodes.FlyOffset or Opcodes.SwimOffset => (2, 4, 8, 15 + Bits(0x0e) + ((f & 0x11) != 0 ? 1 : 0), f & 1),
            _ => (1, 2, 4, 5 + Bits(0x0f), 0), // RollOffset
        };

        if (r.Remaining != length || !r.TrySkip(1 + lead) ||
            !TryDelta(ref r, f, bx, out sbyte dx) || !TryDelta(ref r, f, by, out sbyte dy) || !TryDelta(ref r, f, bz, out sbyte dz) ||
            (opcode == Opcodes.FlyOffset && !r.TrySkip(12)) || // the flight stream has its velocity before the heading
            (opcode == Opcodes.RollOffset && !r.TrySkip(2)) || // the roll's first u16 was 0 in all records seen
            !r.TryU16(out ushort heading))
        {
            return false;
        }

        emit(new MoveOffsetEvent(ts, id, dx, dy, dz, Degrees(heading)));
        return true;
    }

    /// <summary>An int8 delta when its flag bit is set (bit 0: always present); 0 otherwise.</summary>
    private static bool TryDelta(ref SpanReader r, int flags, int bit, out sbyte delta)
    {
        delta = 0;
        if (bit != 0 && (flags & bit) == 0)
        {
            return true;
        }

        if (!r.TryU8(out byte value))
        {
            return false;
        }

        delta = (sbyte)value;
        return true;
    }

    /// <summary>
    /// Keyframes (absolute position, ≈ cm; f = flags u8, S = a state byte, hd = heading u16, vel = 3 × f32 cm/s):
    /// <list type="bullet">
    /// <item><c>1E 37</c> (jump or fall starts): f, [A if f&amp;1], pos, hd, vel, [B if f&amp;0x14]; 27 + (f&amp;1) + (f&amp;0x14 ≠ 0). Also sent for us.</item>
    /// <item><c>1F 37</c> (falling): f, pos, hd, vel, [01 if f&amp;2]; 27 + (f&gt;&gt;1&amp;1).</item>
    /// <item><c>23 37</c> (glide starts): f, pos, hd, vel, …; 27 to 29. <c>24 37</c> (gliding): f, pos, hd, vel, [byte if f&amp;1]; 27 + (f&amp;1).</item>
    /// <item><c>28 37</c> (flight starts): f, [S if f&amp;1], pos, vel, hd, [S if not f&amp;1]; 28 + (f&gt;&gt;1&amp;1). <c>29 37</c> (flying): the same, 28.</item>
    /// <item><c>30 37</c> (dodge roll starts): no flags, pos, hd, hd2, byte; 17.</item>
    /// <item><c>36 37</c> / <c>37 37</c> (swimming): f, [S if f&amp;1], pos, hd, vel, …; 28 or 29.</item>
    /// <item><c>3E</c> / <c>3F</c> / <c>40 37</c> (rare, mostly ours): f, byte, pos, u16; 16.</item>
    /// </list>
    /// </summary>
    public static bool ParseKeyframe(ushort opcode, ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint id) || id == 0 || r.Remaining < 1)
        {
            return false;
        }

        int n = r.Remaining;
        int f = r.Rest[0];
        // Bytes before the position, whether the heading comes after a velocity, and whether the length fits
        var (skip, velocityFirst, fits) = opcode switch
        {
            Opcodes.FallStart => (1 + (f & 1), false, n == 27 + (f & 1) + ((f & 0x14) != 0 ? 1 : 0)),
            Opcodes.FallSnapshot => (1, false, n == 27 + (f >> 1 & 1)),
            Opcodes.GlideStart => (1, false, n is >= 27 and <= 29),
            Opcodes.GlideSnapshot => (1, false, n == 27 + (f & 1)),
            Opcodes.FlyStart => (1 + (f & 1), true, n == 28 + (f >> 1 & 1)),
            Opcodes.FlySnapshot => (1 + (f & 1), true, n == 28),
            Opcodes.RollStart => (0, false, n == 17),
            Opcodes.SwimStart or Opcodes.SwimSnapshot => (1 + (f & 1), false, n is 28 or 29),
            _ => (2, false, n == 16), // PositionNote1..3
        };

        if (!fits || !r.TrySkip(skip) || !r.TryF32(out float x) || !r.TryF32(out float y) || !r.TryF32(out float z) ||
            (velocityFirst && !r.TrySkip(12)) || !r.TryU16(out ushort heading))
        {
            return false;
        }

        var position = new Vec3(x, y, z);
        if (!position.IsWorldPoint)
        {
            return false;
        }

        bool note = opcode is Opcodes.PositionNote1 or Opcodes.PositionNote2 or Opcodes.PositionNote3;
        var source = opcode switch
        {
            Opcodes.RollStart => PositionSource.Dash,
            Opcodes.SwimStart or Opcodes.SwimSnapshot => PositionSource.Swim,
            _ when note => PositionSource.Steer,
            _ => PositionSource.Air,
        };

        // The notes end in a u16 that is not a heading (01 00 in all records seen)
        emit(new PositionEvent(ts, id, position, note ? null : Degrees(heading), source));
        return true;
    }

    /// <summary><c>46 37</c>: entity varint, z f32 (where it last stood on the ground); 4.</summary>
    public static bool ParseGroundHeight(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint id) || id == 0 || r.Remaining != 4 || !r.TryF32(out float z) ||
            !float.IsFinite(z) || Math.Abs(z) > 1_000_000)
        {
            return false;
        }

        emit(new GroundHeightEvent(ts, id, z));
        return true;
    }

    /// <summary><c>4E 36</c>: entity varint, facing f32 (degrees, where it turns to), from f32 (degrees), reason u8; 9.</summary>
    public static bool ParseRotate(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint id) || id == 0 || r.Remaining != 9 || !r.TryF32(out float facing) || !r.TryF32(out float from) ||
            !r.TryU8(out byte reason) || !float.IsFinite(facing) || !float.IsFinite(from) || Math.Abs(facing) > 720 || Math.Abs(from) > 720)
        {
            return false;
        }

        emit(new RotateEvent(ts, id, Normalize(facing), Normalize(from), reason));
        return true;
    }

    /// <summary>A u16 heading (65536 = 360°) in degrees.</summary>
    private static float Degrees(ushort heading) => heading * 360f / 65536f;

    private static float Normalize(float degrees) => ((degrees % 360) + 360) % 360;
}
