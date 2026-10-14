using System.Text;

namespace Avion2.Protocol.Parsers;

/// <summary>
/// Movement, skill casts, buffs, ownership and legion records. Layouts were inferred from captures and checked
/// against each other (e.g. a position from one record type against the same entity's <c>1C 37</c> track); see
/// docs/PROTOCOL.md.
/// </summary>
public static class WorldParsers
{
    private const long NeverEnds = 4_102_412_400_000;

    /// <summary><c>00 36</c>: server time, u64 Unix ms.</summary>
    public static bool ParseServerClock(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryI64(out long serverMs) || serverMs is < 1_600_000_000_000 or > 2_200_000_000_000)
        {
            return false;
        }

        emit(new ServerClockEvent(ts, serverMs));
        return true;
    }

    /// <summary>
    /// <c>1C 37</c>: entity varint, mode u8 (4; 5 is followed by one more byte), x f32, y f32, z f32,
    /// heading u16, heading u16, 01.
    /// </summary>
    public static bool ParseMove(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint id) || !r.TryU8(out byte mode) || (mode == 5 && !r.TrySkip(1)) ||
            !TryVec3(ref r, out var position) || !r.TryU16(out ushort heading))
        {
            return false;
        }

        emit(new PositionEvent(ts, id, position, heading * 360f / 65536f, PositionSource.Move));
        return true;
    }

    /// <summary>
    /// Records with a position at a fixed offset after the entity: <c>1A 37</c> / <c>1B 37</c> (+2; <c>1B</c> with
    /// a 1-byte header when its first byte is not 05), <c>2F 37</c> and <c>3C 37</c> (+1).
    /// </summary>
    public static bool ParsePositionAt(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit, ushort opcode)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint id) || r.Remaining < 13)
        {
            return false;
        }

        int skip = opcode switch
        {
            Opcodes.MoveRelative => 2,
            Opcodes.MoveSteer => r.Rest[0] == 0x05 ? 2 : 1,
            _ => 1,
        };

        if (!r.TrySkip(skip) || !TryVec3(ref r, out var position))
        {
            return false;
        }

        var source = opcode is Opcodes.Dash or Opcodes.Relocate ? PositionSource.Dash : PositionSource.Steer;
        emit(new PositionEvent(ts, id, position, null, source));
        return true;
    }

    /// <summary>
    /// <c>02 38</c>: actor varint, 00, skill u32, sequence u8, kind u8, target varint, facing f32,
    /// [kind 2: target x f32, y f32, z f32], …
    /// </summary>
    public static bool ParseSkillCast(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint actor) || actor == 0 || !r.TryU8(out _) || !r.TryU32(out uint raw) || raw < 1000)
        {
            return false;
        }

        uint? target = null;
        Vec3? at = null;
        float? facing = null;
        if (r.TryU8(out _) && r.TryU8(out byte kind) && r.TryVarint(out uint t))
        {
            target = t == 0 ? null : t;
            if (r.TryF32(out float f) && float.IsFinite(f) && Math.Abs(f) <= 720)
            {
                facing = ((f % 360) + 360) % 360;
            }

            if (kind == 2 && TryVec3(ref r, out var position))
            {
                at = position;
            }
        }

        emit(new SkillCastEvent(ts, actor, raw - raw % 10_000, raw, target, at, facing));
        return true;
    }

    /// <summary><c>34 36</c>: entity varint, kind u8, u8, code u32, x f32, y f32, z f32, ….</summary>
    public static bool ParseWorldObject(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint entity) || !r.TryU8(out byte kind) || !r.TryU8(out _) || !r.TryU32(out uint code) ||
            !TryVec3(ref r, out var position) || !position.IsPlausible)
        {
            return false;
        }

        emit(new WorldObjectEvent(ts, entity, kind, code, position));
        return true;
    }

    /// <summary><c>3A 36</c>: object varint, actor varint, u8, …, item u32 (the last four bytes; 0 = none).</summary>
    public static bool ParseInteractionDone(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint obj) || !r.TryVarint(out uint actor) || body.Length - r.Position < 4)
        {
            return false;
        }

        uint item = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(body[^4..]);
        emit(new InteractionDoneEvent(ts, obj, actor, item is >= 100_000_000 and <= 999_999_999 ? item : null));
        return true;
    }

    /// <summary>
    /// <c>2A 38</c> added: entity varint, 01, flags u8, …; <c>2B 38</c> changed: entity varint, flags u8, …; then
    /// instance varint, abnormal u32, length i64 ms (-1 never), end i64, caster varint, level u8,
    /// [flags&amp;2: skill u32], u8, x f32, y f32, z f32.
    /// </summary>
    public static bool ParseBuff(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit, bool added)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint entity) || (added && !r.TryU8(out _)) || !r.TryU8(out byte flags) ||
            !r.TryVarint(out uint instance) || !r.TryU32(out uint abnormal) || !r.TryI64(out long length) ||
            !r.TryI64(out long end) || !r.TryVarint(out uint caster) || !r.TryU8(out _))
        {
            return false;
        }

        uint? skill = null;
        if ((flags & 0x02) != 0)
        {
            if (!r.TryU32(out uint s))
            {
                return false;
            }

            skill = s;
        }

        Vec3? position = r.TryU8(out _) && TryVec3(ref r, out var p) ? p : null;
        long? duration = length <= 0 || end >= NeverEnds ? null : length;
        emit(new BuffEvent(ts, entity, instance, abnormal, skill, caster, duration, added, position));
        if (caster == entity && position is { } fix)
        {
            // Only self-cast buffs carry where the entity is (within ~0.6 m of the server's move fixes, added or
            // refreshed). With another caster the position is 5–25 m off — the caster's spot. Ticking self buffs
            // (e.g. Chanter mantras, once a second) are what tells us our own position while we move.
            emit(new PositionEvent(ts, entity, fix, null, PositionSource.Buff));
        }

        return true;
    }

    /// <summary><c>2C 38</c>: entity varint, n u8, n × (kind u8, instance varint, reason u8, [kind&amp;7 == 7: by varint, skill u32, effect u32]).</summary>
    public static bool ParseBuffRemoved(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint entity) || !r.TryU8(out byte count) || count == 0)
        {
            return false;
        }

        var instances = new List<uint>(count);
        for (int i = 0; i < count; i++)
        {
            if (!r.TryU8(out byte kind) || !r.TryVarint(out uint instance) || !r.TryU8(out _))
            {
                break;
            }

            instances.Add(instance);
            if ((kind & 7) == 7 && !(r.TryVarint(out _) && r.TrySkip(8)))
            {
                break;
            }
        }

        if (instances.Count == 0)
        {
            return false;
        }

        emit(new BuffRemovedEvent(ts, entity, instances));
        return true;
    }

    /// <summary>
    /// <c>04 8D</c> (long form): entity varint, u32, owner varint, server u16, owner name (u8 len), legion (u8 len).
    /// Claims an entity for a player: their summons and effects, but monsters too (see <see cref="OwnershipEvent"/>).
    /// </summary>
    public static bool ParseOwnership(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint spawned) || !r.TryU32(out _) || !r.TryVarint(out uint owner) || owner == 0 ||
            !r.TryU16(out ushort server) || server is < 1000 or > 2999 ||
            !r.TryU8(out byte nameLength) || nameLength is 0 or > 36 || !r.TryUtf8(nameLength, out _))
        {
            return false;
        }

        if (spawned != owner)
        {
            emit(new OwnershipEvent(ts, spawned, owner));
        }

        if (r.TryU8(out byte legionLength) && legionLength is > 0 and <= 36 && r.TryUtf8(legionLength, out string legion))
        {
            emit(new LegionTagEvent(ts, owner, server, legion));
        }

        return true;
    }

    /// <summary><c>33 8A</c>: entity varint, a few bytes, server u16, legion (u8 len), …</summary>
    public static bool ParseLegionTag(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint entity) || entity == 0)
        {
            return false;
        }

        for (int at = r.Position; at < Math.Min(body.Length - 3, r.Position + 12); at++)
        {
            var s = new SpanReader(body, at);
            if (s.TryU16(out ushort server) && server is >= 1000 and <= 2999 &&
                s.TryU8(out byte length) && length is > 0 and <= 36 && s.TryUtf8(length, out string legion))
            {
                emit(new LegionTagEvent(ts, entity, server, legion));
                return true;
            }
        }

        return false;
    }

    /// <summary><c>03 8A</c>: 00 00, name (u8 len), leader (u8 len), counters, then the legion's texts.</summary>
    public static bool ParseLegionDetails(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryU16(out _) || !r.TryU8(out byte nameLength) || nameLength is 0 or > 36 || !r.TryUtf8(nameLength, out string name))
        {
            return false;
        }

        string? leader = r.TryU8(out byte leaderLength) && leaderLength is > 0 and <= 36 && r.TryUtf8(leaderLength, out string l) ? l : null;
        emit(new LegionDetailsEvent(ts, name, leader, FindTexts(body[r.Position..])));
        return true;
    }

    /// <summary>
    /// <c>05 8A</c>: member entries, each with server u16, u32, name (u8 len), level u8, …, last login u64 ms.
    /// Entries are found by that pattern rather than walked, as their tails vary.
    /// </summary>
    public static bool ParseLegionRoster(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var members = new List<LegionMember>();
        for (int at = 0; at + 8 < body.Length; at++)
        {
            var r = new SpanReader(body, at);
            if (!r.TryU16(out ushort server) || server is < 1000 or > 2999 || !r.TrySkip(4) ||
                !r.TryU8(out byte length) || length is < 2 or > 36 || !r.TryUtf8(length, out string name))
            {
                continue;
            }

            int? level = r.TryU8(out byte lv) && lv is >= 1 and <= 99 ? lv : null;
            long? lastOnline = null;
            for (int i = r.Position; i + 8 <= Math.Min(body.Length, r.Position + 24); i++)
            {
                long value = BitConverter.ToInt64(body.Slice(i, 8));
                if (value is > 1_600_000_000_000 and < 2_200_000_000_000)
                {
                    lastOnline = value;
                    break;
                }
            }

            members.Add(new LegionMember(name, level, lastOnline));
            at = r.Position - 1;
        }

        if (members.Count == 0)
        {
            return false;
        }

        emit(new LegionRosterEvent(ts, members));
        return true;
    }

    private static bool TryVec3(ref SpanReader r, out Vec3 position)
    {
        position = default;
        if (!r.TryF32(out float x) || !r.TryF32(out float y) || !r.TryF32(out float z))
        {
            return false;
        }

        position = new Vec3(x, y, z);
        return position.IsPlausible;
    }

    /// <summary>
    /// Finds a position (x, y, z f32) by scanning a record; used for spawns, whose layout before it varies with the
    /// record's contents.
    /// </summary>
    public static Vec3? FindPosition(ReadOnlySpan<byte> body, int from, int window)
    {
        for (int at = Math.Max(0, from); at + 12 <= body.Length && at < from + window; at++)
        {
            var r = new SpanReader(body, at);
            if (TryVec3(ref r, out var position))
            {
                return position;
            }
        }

        return null;
    }

    /// <summary>Length-prefixed texts (u8 or u16 length) of at least 3 printable characters; newlines allowed.</summary>
    private static List<string> FindTexts(ReadOnlySpan<byte> rest)
    {
        var texts = new List<string>();
        var utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
        int at = 0;
        while (at < rest.Length)
        {
            if (TryText(rest, at, 1, utf8, out string? text, out int next) || TryText(rest, at, 2, utf8, out text, out next))
            {
                texts.Add(text!);
                at = next;
                continue;
            }

            at++;
        }

        return texts;
    }

    private static bool TryText(ReadOnlySpan<byte> rest, int at, int prefix, UTF8Encoding utf8, out string? text, out int next)
    {
        text = null;
        next = at;
        if (at + prefix > rest.Length)
        {
            return false;
        }

        int length = prefix == 1 ? rest[at] : rest[at] | rest[at + 1] << 8;
        if (length < 3 || at + prefix + length > rest.Length)
        {
            return false;
        }

        var bytes = rest.Slice(at + prefix, length);
        foreach (byte b in bytes)
        {
            if ((b < 0x20 && b is not (0x0A or 0x0D or 0x09)) || b == 0x7F)
            {
                return false;
            }
        }

        try
        {
            text = utf8.GetString(bytes).Trim();
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        // Real texts contain letters; this filters runs of digits/punctuation that happen to decode.
        if (text.Count(char.IsLetter) < 3)
        {
            return false;
        }

        next = at + prefix + length;
        return true;
    }
}
