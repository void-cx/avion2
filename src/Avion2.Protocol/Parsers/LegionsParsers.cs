using System.Text;

namespace Avion2.Protocol.Parsers;

/// <summary>
/// Legion records: our legion changing (created, left, disbanded, a member out or online) and the legion lists; plus
/// the legion parts of records that are about something else. See docs/PROTOCOL.md.
/// </summary>
/// <remarks>
/// A legion's id is a <c>u64</c> with its server in the top 16 bits and its number in the low 32 (<c>1a 03 00 00 00 00
/// 29 05</c>: legion 794 on server 1321); the legion tag, the spawn's legion block, the lists and the create answer all
/// carry it that way, followed by the name.
/// </remarks>
public static class LegionsParsers
{
    /// <summary>Decodes the opcode if it is one of this area's; false otherwise.</summary>
    public static bool TryParse(ushort opcode, ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit) => opcode switch
    {
        Opcodes.LegionCreate => ParseCreate(body, ts, emit),
        Opcodes.LegionRecommendedList => ParseList(body, ts, search: false, emit),
        Opcodes.LegionSearch => ParseList(body, ts, search: true, emit),
        Opcodes.LegionLeave => ParseResult(body, ts, emit, 10, r => new LegionLeftEvent(ts, r)),
        Opcodes.LegionDisband => ParseResult(body, ts, emit, 2, r => new LegionDisbandedEvent(ts, r)),
        Opcodes.LegionMemberLeft => ParseMemberLeft(body, ts, emit),
        Opcodes.LegionMemberOnline => ParseMemberOnline(body, ts, emit),
        _ => false,
    };

    private const int MaxName = 36;

    /// <summary>
    /// <c>05 8A</c> (GuildMemberInfo_RS), our whole legion in one record: result u16, member count (varint), then per
    /// member flags u8 (bit 1: a shared message follows the grade), account u64, character u64 (server in the top 16
    /// bits), class code u32, name (u8 len), level u32, grade u8 (0 leader, 1 officer, 2 member), the message (u8 len)
    /// when flagged, a byte the game does not always send, last logout u64 ms, zone u32, item level u32 and combat
    /// power u64. The byte sent or not is told by where the next member starts (or the record ends). A record that does
    /// not walk that way is searched for names instead.
    /// </summary>
    public static bool ParseRoster(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        if (WalkRoster(body) is { } members)
        {
            emit(new LegionRosterEvent(ts, members, Complete: true));
            return true;
        }

        return WorldParsers.ParseLegionRoster(body, ts, emit);
    }

    private const int MemberTail = 24; // logout, zone, item level, combat power

    private static List<LegionMember>? WalkRoster(ReadOnlySpan<byte> body)
    {
        var r = new SpanReader(body);
        if (!r.TryU16(out ushort result) || result != 0 || !r.TryVarint(out uint count) || count is 0 or > 1_000)
        {
            return null;
        }

        var members = new List<LegionMember>((int)count);
        int at = r.Position;
        for (uint k = 0; k < count; k++)
        {
            if (!TryMemberHead(body, at, out byte flags, out ushort server, out uint code, out string name, out at))
            {
                return null;
            }

            r = new SpanReader(body, at);
            if (!r.TryU32(out uint level) || !r.TryU8(out byte grade) ||
                ((flags & 2) != 0 && (!r.TryU8(out byte message) || !r.TrySkip(message))))
            {
                return null;
            }

            int tail = -1;
            for (int extra = 0; extra <= 1 && tail < 0; extra++)
            {
                int t = r.Position + extra, end = t + MemberTail;
                if (end > body.Length)
                {
                    break;
                }

                long logout = BitConverter.ToInt64(body.Slice(t, 8));
                bool fits = k == count - 1 ? end == body.Length : TryMemberHead(body, end, out _, out _, out _, out _, out _);
                if (fits && logout is 0 or (> 1_600_000_000_000 and < 2_200_000_000_000))
                {
                    tail = t;
                }
            }

            if (tail < 0)
            {
                return null;
            }

            long lastLogout = BitConverter.ToInt64(body.Slice(tail, 8));
            uint itemLevel = BitConverter.ToUInt32(body.Slice(tail + 12, 4));
            long power = BitConverter.ToInt64(body.Slice(tail + 16, 8));
            members.Add(new LegionMember(name, level is >= 1 and <= 99 ? (int)level : null, lastLogout > 0 ? lastLogout : null,
                GameClasses.FromRosterCode(code), itemLevel is > 0 and < 100_000 ? itemLevel : null,
                power is > 0 and <= uint.MaxValue ? (uint)power : null, grade, server));
            at = tail + MemberTail;
        }

        return at == body.Length ? members : null;
    }

    /// <summary>A member's start: flags, account, character (its server 1000–2999), class code 1–48 and a name.</summary>
    private static bool TryMemberHead(ReadOnlySpan<byte> body, int at, out byte flags, out ushort server, out uint code, out string name,
        out int next)
    {
        server = 0;
        code = 0;
        name = string.Empty;
        next = at;
        var r = new SpanReader(body, at);
        if (!r.TryU8(out flags) || flags > 0x0F || !r.TrySkip(8) || !r.TryI64(out long character) || !r.TryU32(out code) ||
            code is 0 or > 48 || !r.TryU8(out byte length) || length is 0 or > MaxName || !r.TryUtf8(length, out name))
        {
            return false;
        }

        server = (ushort)((ulong)character >> 48);
        if (server is < 1000 or > 2999)
        {
            return false;
        }

        next = r.Position;
        return true;
    }

    /// <summary>
    /// A legion id: the server (top 16 bits, 1000–2999) and the number (low 32, the 16 between them 0). False for
    /// anything else.
    /// </summary>
    public static bool TryLegionId(ref SpanReader r, out ushort server, out uint legion)
    {
        server = 0;
        legion = 0;
        if (!r.TryI64(out long id))
        {
            return false;
        }

        ulong value = (ulong)id;
        server = (ushort)(value >> 48);
        legion = (uint)value;
        return server is >= 1000 and <= 2999 && ((value >> 32) & 0xFFFF) == 0 && legion != 0;
    }

    /// <summary>A text (u8 length, UTF-8) that may hold line breaks, as legion introductions do; empty allowed.</summary>
    private static bool TryText(ref SpanReader r, out string text)
    {
        text = string.Empty;
        if (!r.TryU8(out byte length) || r.Remaining < length)
        {
            return false;
        }

        var bytes = r.Rest[..length];
        r.Position += length;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        return !text.Any(c => c < 0x20 && c is not ('\r' or '\n' or '\t'));
    }

    private static bool TryName(ref SpanReader r, out string name, bool allowEmpty = false)
    {
        name = string.Empty;
        return r.TryU8(out byte length) && length <= MaxName && (length == 0 ? allowEmpty : r.TryUtf8(length, out name));
    }

    /// <summary><c>u16</c> result, then <paramref name="length"/> − 2 bytes not read (<c>0B 8A</c>: a reputation penalty <c>u64</c>).</summary>
    private static bool ParseResult(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit, int length, Func<ushort, GameEvent> make)
    {
        var r = new SpanReader(body);
        if (body.Length != length || !r.TryU16(out ushort result))
        {
            return false;
        }

        emit(make(result));
        return true;
    }

    /// <summary>
    /// <c>01 8A</c>: <c>u16</c> result, legion id <c>u64</c>, name (u8 len), level <c>u8</c>, experience <c>u64</c>, join
    /// rule, join password, emblem, introduction and the grades' rights (not read). A failure (result ≠ 0) has the rest
    /// zeroed.
    /// </summary>
    public static bool ParseCreate(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryU16(out ushort result))
        {
            return false;
        }

        if (result != 0)
        {
            emit(new LegionCreatedEvent(ts, result, 0, 0, string.Empty));
            return true;
        }

        if (!TryLegionId(ref r, out ushort server, out uint legion) || !TryName(ref r, out string name))
        {
            return false;
        }

        emit(new LegionCreatedEvent(ts, result, legion, server, name));
        return true;
    }

    /// <summary><c>0C 8A</c>: name (u8 len), reason <c>u8</c>, reputation penalty <c>u64</c>.</summary>
    public static bool ParseMemberLeft(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!TryName(ref r, out string name) || !r.TryU8(out byte reason) || r.Remaining != 8)
        {
            return false;
        }

        emit(new LegionMemberLeftEvent(ts, name, reason));
        return true;
    }

    /// <summary><c>39 8A</c>: name (u8 len), nothing else.</summary>
    public static bool ParseMemberOnline(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!TryName(ref r, out string name) || r.Remaining != 0)
        {
            return false;
        }

        emit(new LegionMemberOnlineEvent(ts, name));
        return true;
    }

    /// <summary>
    /// <c>07 8A</c> / <c>09 8A</c>: <c>u16</c> result, count (varint), then per legion: id <c>u64</c>, name, leader (u8
    /// len each), level <c>u8</c>, experience <c>u64</c>, members <c>u8</c>, maximum (varint, 128), emblem (varint),
    /// introduction (u8 len, line breaks allowed), join rule <c>u8</c>, then 9–10 bytes (join requested, request time,
    /// race in the client's layout; all 0 in our records, and their width did not settle), so the next legion is found
    /// by its id and name.
    /// </summary>
    public static bool ParseList(ReadOnlySpan<byte> body, long ts, bool search, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryU16(out ushort result) || result != 0 || !r.TryVarint(out uint count) || count > 500)
        {
            return false;
        }

        var legions = new List<LegionListing>((int)count);
        for (int i = 0; i < count; i++)
        {
            if (i > 0 && !SkipToNext(ref r))
            {
                return false;
            }

            if (!TryLegionId(ref r, out ushort server, out uint id) || !TryName(ref r, out string name) ||
                !TryName(ref r, out string leader, allowEmpty: true) || !r.TryU8(out byte level) || !r.TrySkip(8) ||
                !r.TryU8(out byte members) || !r.TryVarint(out uint max) || !r.TryVarint(out _) ||
                !TryText(ref r, out string intro) || !r.TryU8(out byte rule) || members > max || max is 0 or > 1000)
            {
                return false;
            }

            legions.Add(new LegionListing(server, id, name, leader, level, members, (int)max, intro, rule));
        }

        emit(new LegionListEvent(ts, search, legions));
        return true;
    }

    /// <summary>Moves past an entry's last 9–10 bytes to where the next legion's id and name read.</summary>
    private static bool SkipToNext(ref SpanReader r)
    {
        for (int skip = 9; skip <= 11; skip++)
        {
            var probe = new SpanReader(r.Data, r.Position);
            if (probe.TrySkip(skip) && TryLegionId(ref probe, out _, out _) && TryName(ref probe, out _))
            {
                r.Position += skip;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The legion block of a player spawn (<c>45 36</c>), present only for players in a legion: after the position,
    /// <c>u16</c>, <c>00 00 00 00</c>, server <c>u16</c>, legion (u8 len, UTF-8), then <c>01 00</c> or <c>02 00</c>
    /// (e.g. <c>06 01 00 00 00 00 29 05 07 …legion… 01 00 02 00</c>). The first such spot after the name is taken.
    /// Checked against the legion tag (<c>33 8A</c>) of the same player: equal in 148 of 148 spawns that had one.
    /// </summary>
    /// <param name="from">Where to start looking (after the name).</param>
    public static (ushort Server, string Legion)? FindSpawnLegion(ReadOnlySpan<byte> body, int from)
    {
        for (int at = Math.Max(0, from); at + 9 <= body.Length; at++)
        {
            if (body[at] != 0 || body[at + 1] != 0 || body[at + 2] != 0 || body[at + 3] != 0)
            {
                continue;
            }

            var r = new SpanReader(body, at + 4);
            if (!r.TryU16(out ushort server) || server is < 1000 or > 2999 || !r.TryU8(out byte length) || length is 0 or > 36 ||
                !r.TryUtf8(length, out string legion) || !r.TryU8(out byte next) || next is not (1 or 2) ||
                !r.TryU8(out byte zero) || zero != 0)
            {
                continue;
            }

            return (server, legion);
        }

        return null;
    }
}
