using System.Buffers.Binary;
using System.Text;

namespace Avion2.Protocol.Parsers;

/// <summary>
/// The group finder (<c>97</c> records: the room we sit in, joins, ready checks, join requests) and the boss's target
/// in the party (<c>20 92</c>). See docs/PROTOCOL.md. Strings are a varint length and UTF-8; bools are bits of one byte
/// shared across the whole message, low bit first.
/// </summary>
public static class FinderParsers
{
    /// <summary>Decodes the opcode if it is one of this area's; false otherwise.</summary>
    public static bool TryParse(ushort opcode, ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit) => opcode switch
    {
        Opcodes.FinderMemberJoined => ParseSlotEvent(body, ts, emit, joined: true),
        Opcodes.FinderSlotUpdated => ParseSlotEvent(body, ts, emit, joined: false),
        Opcodes.FinderJoinRequest => ParseJoinRequest(body, ts, emit),
        Opcodes.FinderJoinRequestCancelled => ParseCharacter(body, emit, id => new FinderJoinRequestCancelledEvent(ts, id)),
        Opcodes.FinderMemberReady => ParseMemberReady(body, ts, emit),
        Opcodes.FinderSelfReady => body.Length == 3 && body[0] == 0 && body[1] == 0 && Emit(emit, new FinderReadyEvent(ts, null, body[2] != 0)),
        Opcodes.FinderStart => IsOk(body) && Emit(emit, new FinderStartEvent(ts)),
        Opcodes.FinderLeft => IsOk(body) && Emit(emit, new FinderLeftEvent(ts)),
        Opcodes.FinderRoomClosed => body.Length == 4 && Emit(emit, new FinderRoomClosedEvent(ts, BinaryPrimitives.ReadUInt32LittleEndian(body))),
        Opcodes.PartyBossTarget => ParseBossTarget(body, ts, emit),
        _ => false,
    };

    /// <summary>
    /// <c>02 97</c>: room u32, title, size u8, dungeon u32, stage u8, faction u8, leader i64, public (bit), join type u8,
    /// state u8, slot count varint, slots (see <see cref="TrySlot"/>), affix count varint, affixes u32 × n, reason u8.
    /// </summary>
    public static bool TryParseRoom(ReadOnlySpan<byte> body, long ts, out FinderRoomEvent room)
    {
        room = null!;
        var r = new SpanReader(body);
        var bits = new Bits();
        if (!r.TryU32(out uint number) || number == 0 || !TryString(ref r, out string title) || !r.TryU8(out byte size) ||
            !r.TryU32(out uint dungeon) || !r.TrySkip(2) || !r.TryI64(out long leader) || !bits.TryRead(ref r, out _) ||
            !r.TryU8(out _) || !r.TryU8(out byte state) || !r.TryVarint(out uint count) || count > 20)
        {
            return false;
        }

        var slots = new List<FinderSlot>((int)count);
        for (int i = 0; i < count; i++)
        {
            if (!TrySlot(ref r, ref bits, out var slot))
            {
                return false;
            }

            slots.Add(slot);
        }

        if (!r.TryVarint(out uint affixes) || affixes > 16 || !r.TrySkip((int)affixes * 4) || !r.TryU8(out byte reason) ||
            r.Remaining != 0)
        {
            return false;
        }

        room = new FinderRoomEvent(ts, number, title, size, dungeon, (uint)leader, (FinderRoomState)state, slots, reason);
        return true;
    }

    /// <summary><c>0B 97</c> (a member joined) and <c>1F 97</c> (a member's slot changed): one slot.</summary>
    public static bool ParseSlotEvent(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit, bool joined)
    {
        var r = new SpanReader(body);
        var bits = new Bits();
        if (!TrySlot(ref r, ref bits, out var slot) || slot.IsOpen || r.Remaining != 0)
        {
            return false;
        }

        emit(new FinderSlotEvent(ts, slot, joined));
        return true;
    }

    /// <summary>
    /// <c>07 97</c>: room u32, character i64, class u32 (roster code), level u32, item level u32, name, server u16,
    /// u32 (0), combat power i64, u8 (1), expiry i64 (epoch ms).
    /// </summary>
    public static bool ParseJoinRequest(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryU32(out uint room) || !r.TryI64(out long character) || character == 0 || !r.TryU32(out uint classCode) ||
            !r.TryU32(out uint level) || !r.TryU32(out uint itemLevel) || !TryString(ref r, out string name) || name.Length == 0 ||
            !r.TryU16(out ushort server) || !r.TrySkip(4) || !r.TryI64(out long power) || !r.TrySkip(1) ||
            !r.TryI64(out long expires) || r.Remaining != 0)
        {
            return false;
        }

        emit(new FinderJoinRequestEvent(ts, room, (uint)character, GameClasses.FromRosterCode(classCode), level, itemLevel, name,
            server, Clamp(power), expires));
        return true;
    }

    /// <summary><c>16 97</c>: character i64, ready u8.</summary>
    public static bool ParseMemberReady(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (body.Length != 9 || !r.TryI64(out long character) || character == 0 || !r.TryU8(out byte ready))
        {
            return false;
        }

        emit(new FinderReadyEvent(ts, (uint)character, ready != 0));
        return true;
    }

    /// <summary><c>20 92</c>: entity varint (0 = cleared).</summary>
    public static bool ParseBossTarget(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint entity) || r.Remaining != 0)
        {
            return false;
        }

        emit(new PartyBossTargetEvent(ts, entity));
        return true;
    }

    /// <summary>
    /// A room slot: mask u8 (<c>02</c> ready, <c>04</c> home server, <c>08</c> current server, <c>10</c> revive items;
    /// 0 = open), slot u8, character i64, name, class u32, level u32, item level u32, [ready bit], online bit,
    /// [home server u16], [current server u16], role u8, combat power i64, tickets u8, [revive items i64], mentoring u8,
    /// latency u8.
    /// </summary>
    private static bool TrySlot(ref SpanReader r, ref Bits bits, out FinderSlot slot)
    {
        slot = null!;
        bool ready = false;
        if (!r.TryU8(out byte mask) || (mask & ~0x1E) != 0 || !r.TryU8(out byte number) || !r.TryI64(out long character) ||
            !TryString(ref r, out string name) || !r.TryU32(out uint classCode) || !r.TryU32(out uint level) ||
            !r.TryU32(out uint itemLevel) || ((mask & 0x02) != 0 && !bits.TryRead(ref r, out ready)) ||
            !bits.TryRead(ref r, out bool online) || !r.TrySkip(((mask & 0x04) != 0 ? 2 : 0) + ((mask & 0x08) != 0 ? 2 : 0)) ||
            !r.TryU8(out byte role) || !r.TryI64(out long power) || !r.TrySkip(1 + ((mask & 0x10) != 0 ? 8 : 0) + 2))
        {
            return false;
        }

        bool open = mask == 0 || character == 0;
        slot = new FinderSlot(number, open ? 0 : (uint)character, open ? string.Empty : name,
            open ? GameClass.Unknown : GameClasses.FromRosterCode(classCode), open ? 0 : level, open ? 0 : itemLevel,
            open ? 0 : Clamp(power), (FinderRole)role, !open && ready, online, (mask & 0x10) != 0);
        return true;
    }

    /// <summary>A varint length and UTF-8; empty allowed, control characters become spaces (the title is free text).</summary>
    private static bool TryString(ref SpanReader r, out string value)
    {
        value = string.Empty;
        if (!r.TryVarint(out uint length) || length > 256 || r.Remaining < length)
        {
            return false;
        }

        if (length == 0)
        {
            return true;
        }

        var bytes = r.Rest[..(int)length];
        r.Position += (int)length;
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            value = string.Create(text.Length, text, (span, source) =>
            {
                for (int i = 0; i < source.Length; i++)
                {
                    span[i] = char.IsControl(source[i]) ? ' ' : source[i];
                }
            });
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
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

    private static bool IsOk(ReadOnlySpan<byte> body) => body.Length == 2 && body[0] == 0 && body[1] == 0;

    private static bool Emit(Action<GameEvent> emit, GameEvent e)
    {
        emit(e);
        return true;
    }

    private static uint Clamp(long value) => (uint)Math.Clamp(value, 0, uint.MaxValue);

    /// <summary>Bools packed into a byte shared by the whole message: a new byte is read when the last one is used up.</summary>
    private struct Bits
    {
        private byte _value;
        private int _used;

        public Bits()
        {
            _used = 8;
        }

        public bool TryRead(ref SpanReader r, out bool value)
        {
            if (_used >= 8)
            {
                if (!r.TryU8(out _value))
                {
                    value = false;
                    return false;
                }

                _used = 0;
            }

            value = (_value >> _used++ & 1) != 0;
            return true;
        }
    }
}
