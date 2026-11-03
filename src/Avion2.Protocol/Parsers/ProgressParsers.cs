namespace Avion2.Protocol.Parsers;

/// <summary>Character progress: item level, experience, currencies with daily or weekly caps, tickets, the pass, pets, items used. See docs/PROTOCOL.md.</summary>
public static class ProgressParsers
{
    /// <summary>Decodes the opcode if it is one of this area's; false otherwise.</summary>
    public static bool TryParse(ushort opcode, ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit) => opcode switch
    {
        Opcodes.SelfItemLevel => ParseSelfItemLevel(body, ts, emit),
        Opcodes.EquipmentChange => ParseEquipmentChange(body, ts, emit),
        Opcodes.ItemUseNotice => ParseItemUsed(body, ts, emit, fromBag: false),
        Opcodes.ItemUseResult => ParseItemUsed(body, ts, emit, fromBag: true),
        Opcodes.KinahLimitInit => ParseKinahLimit(body, ts, emit, initial: true),
        Opcodes.KinahLimitUpdate => ParseKinahLimit(body, ts, emit, initial: false),
        Opcodes.AbyssPointLimit or Opcodes.AbyssPointLimitOld => ParseAbyssPoints(body, ts, emit),
        Opcodes.ContentTicketList => ParseTicketList(body, ts, emit),
        Opcodes.ContentTicketUpdate => ParseTicketUpdate(body, ts, emit),
        Opcodes.DaevaPassInfo => ParseDaevaPass(body, ts, emit),
        Opcodes.ExperienceUpdate => ParseExperience(body, ts, emit),
        Opcodes.PetRoster => ParsePetRoster(body, ts, emit),
        Opcodes.PetSoulsGained => ParsePetSouls(body, ts, emit, gained: true),
        Opcodes.PetSoulsSpent => ParsePetSouls(body, ts, emit, gained: false),
        Opcodes.PetAcquired => ParsePetAcquired(body, ts, emit),
        _ => false,
    };

    /// <summary>Item levels above this are not real (the highest seen is about 1,500).</summary>
    private const uint MaxItemLevel = 10_000;

    /// <summary><c>1D 56</c>: <c>u32</c> item level now, <c>u32</c> highest item level (8 bytes).</summary>
    public static bool ParseSelfItemLevel(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (body.Length != 8 || !r.TryU32(out uint now) || !r.TryU32(out uint best) || now > MaxItemLevel || best > MaxItemLevel)
        {
            return false;
        }

        emit(new SelfItemLevelEvent(ts, now, best));
        return true;
    }

    /// <summary>
    /// <c>1C 56</c>: <c>entity varint</c>, <c>u32</c> item level after the change, <c>varint</c> number of slots,
    /// then per slot the slot and the item before and after (not decoded here).
    /// </summary>
    public static bool ParseEquipmentChange(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint entity) || entity == 0 || !r.TryU32(out uint level) || level > MaxItemLevel ||
            !r.TryVarint(out uint slots) || slots is 0 or > 32 || r.Remaining == 0)
        {
            return false;
        }

        emit(new EquipmentChangedEvent(ts, entity, level, (int)slots));
        return true;
    }

    /// <summary>
    /// <c>16 56</c>: <c>i64</c> item uid, <c>u32</c> item id. <c>13 56</c>: <c>u16</c> result, then the same two
    /// fields. Every result seen is <c>04 03</c> but one; a use is counted either way (the item was used up).
    /// </summary>
    public static bool ParseItemUsed(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit, bool fromBag)
    {
        var r = new SpanReader(body);
        if (fromBag && !r.TrySkip(2))
        {
            return false;
        }

        if (!r.TryI64(out long uid) || !r.TryU32(out uint item) || item is < 100_000_000 or > 999_999_999)
        {
            return false;
        }

        emit(new ItemUsedEvent(ts, uid, item, fromBag));
        return true;
    }

    /// <summary><c>A5 FF</c> / <c>A6 FF</c>: <c>u64</c> Kinah gained today, <c>u64</c> server time (16 bytes).</summary>
    public static bool ParseKinahLimit(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit, bool initial)
    {
        var r = new SpanReader(body);
        if (body.Length != 16 || !r.TryI64(out long gained) || !r.TryI64(out long serverMs) || gained < 0)
        {
            return false;
        }

        emit(new KinahLimitEvent(ts, (ulong)gained, serverMs, initial));
        return true;
    }

    /// <summary>
    /// <c>AB FF</c> (<c>A9 FF</c> on the clients before 2026-10-29): <c>u64</c> Abyss Points gained this week,
    /// <c>u64</c> the weekly cap (16 bytes). The cap was 1,000,000, later 2,500,000.
    /// </summary>
    public static bool ParseAbyssPoints(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (body.Length != 16 || !r.TryI64(out long points) || !r.TryI64(out long cap) || points < 0 || cap is < 10_000 or > 1_000_000_000)
        {
            return false;
        }

        emit(new AbyssPointLimitEvent(ts, (ulong)points, (ulong)cap));
        return true;
    }

    /// <summary><c>0B 61</c>: <c>varint</c> count, then that many tickets (see <see cref="TryTicket"/>).</summary>
    public static bool ParseTicketList(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint count) || count > 1_000)
        {
            return false;
        }

        var tickets = new List<ContentTicket>((int)count);
        for (int i = 0; i < count; i++)
        {
            if (!TryTicket(ref r, out var ticket))
            {
                return false;
            }

            tickets.Add(ticket);
        }

        emit(new ContentTicketsEvent(ts, tickets));
        return true;
    }

    /// <summary><c>0C 61</c>: <c>u8</c> flags (bit 0: a <c>u32</c> follows at the end), one ticket, <c>u8</c> reason.</summary>
    public static bool ParseTicketUpdate(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryU8(out byte flags) || !TryTicket(ref r, out var ticket) || !r.TryU8(out byte reason) ||
            (flags & 1) != 0 && !r.TryU32(out _))
        {
            return false;
        }

        emit(new ContentTicketChangedEvent(ts, ticket, reason));
        return true;
    }

    /// <summary>
    /// A ticket: <c>u8</c> mask, <c>u32</c> id, then what the mask says: bit 2 a <c>varint</c> count, bit 3 a
    /// second <c>varint</c> count, bit 0 a <c>u64</c> time. A count left out is 0 (the last Ascension ticket used
    /// comes as mask 0).
    /// </summary>
    private static bool TryTicket(ref SpanReader r, out ContentTicket ticket)
    {
        ticket = default!;
        if (!r.TryU8(out byte mask) || (mask & ~0x0D) != 0 || !r.TryU32(out uint id))
        {
            return false;
        }

        uint count = 0, extra = 0;
        long? time = null;
        if ((mask & 0x04) != 0)
        {
            if (!r.TryVarint(out uint value))
            {
                return false;
            }

            count = value;
        }

        if ((mask & 0x08) != 0)
        {
            if (!r.TryVarint(out uint value))
            {
                return false;
            }

            extra = value;
        }

        if ((mask & 0x01) != 0)
        {
            if (!r.TryI64(out long value))
            {
                return false;
            }

            time = value;
        }

        ticket = new ContentTicket(id, count, extra, time);
        return true;
    }

    /// <summary>
    /// <c>24 E3</c>: <c>varint</c> seasons, each <c>u32</c> season, <c>u32</c> experience this season, <c>u32</c>
    /// experience this week, then times and the passes' reward levels (not decoded: the seasons are read from the
    /// start, the rest of each one is found by the next season id).
    /// </summary>
    public static bool ParseDaevaPass(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint count) || count is 0 or > 8 || !r.TryU32(out uint season) || season is < 10_000 or > 99_999 ||
            !r.TryU32(out uint total) || !r.TryU32(out uint week) || week > total)
        {
            return false;
        }

        var seasons = new List<DaevaPassSeason> { new(season, total, week) };
        // Further seasons: the same shape, found after the first one's lists
        for (int i = r.Position; seasons.Count < count && i + 12 <= body.Length; i++)
        {
            var next = new SpanReader(body, i);
            if (next.TryU32(out uint id) && id is >= 10_000 and <= 99_999 && id != season && next.TryU32(out uint t) && next.TryU32(out uint w) &&
                w <= t && next.TryI64(out long time) && time is > 1_600_000_000_000 and < 4_000_000_000_000)
            {
                seasons.Add(new DaevaPassSeason(id, t, w));
                i += 11;
            }
        }

        emit(new DaevaPassEvent(ts, seasons));
        return true;
    }

    /// <summary><c>07 8D</c>: <c>i64</c> experience within the level, <c>i64</c> gained, <c>u8</c> reason (17 bytes).</summary>
    public static bool ParseExperience(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (body.Length != 17 || !r.TryI64(out long total) || !r.TryI64(out long gained) || !r.TryU8(out byte reason) || total < 0 || gained < 0)
        {
            return false;
        }

        emit(new ExperienceEvent(ts, total, gained, reason));
        return true;
    }

    /// <summary>
    /// <c>00 90</c>: <c>u32</c> default mount, <c>varint</c> n × (<c>u32</c> pet, <c>u32</c> pet again, <c>u32</c>
    /// level) owned, <c>varint</c> n × (<c>u32</c> pet, <c>u32</c> souls), then soul crystals and boards (not read).
    /// </summary>
    public static bool ParsePetRoster(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TrySkip(4) || !r.TryVarint(out uint owned) || owned > 2_000)
        {
            return false;
        }

        var pets = new List<uint>((int)owned);
        for (int i = 0; i < owned; i++)
        {
            if (!r.TryU32(out uint pet) || !r.TryU32(out uint again) || again != pet || !r.TryU32(out _))
            {
                return false;
            }

            pets.Add(pet);
        }

        if (!TrySouls(ref r, out var souls))
        {
            return false;
        }

        emit(new PetRosterEvent(ts, pets, souls));
        return true;
    }

    /// <summary><c>0D 90</c> / <c>0E 90</c>: <c>varint</c> n × (<c>u32</c> pet, <c>u32</c> souls).</summary>
    public static bool ParsePetSouls(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit, bool gained)
    {
        var r = new SpanReader(body);
        if (!TrySouls(ref r, out var souls) || souls.Count == 0 || r.Remaining != 0)
        {
            return false;
        }

        emit(new PetSoulsEvent(ts, souls, gained));
        return true;
    }

    private static bool TrySouls(ref SpanReader r, out List<PetSouls> souls)
    {
        souls = [];
        if (!r.TryVarint(out uint count) || count > 2_000)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (!r.TryU32(out uint pet) || !r.TryU32(out uint value) || value > 100_000)
            {
                return false;
            }

            souls.Add(new PetSouls(pet, value));
        }

        return true;
    }

    /// <summary>
    /// <c>09 90</c>: <c>entity varint</c> (ours), <c>u32</c> pet, <c>u32</c> pet level, <c>u8</c> creature type,
    /// <c>u32</c> board level, <c>u64</c> board experience.
    /// </summary>
    public static bool ParsePetAcquired(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryVarint(out uint entity) || !r.TryU32(out uint pet) || !r.TryU32(out uint level) || !r.TryU8(out byte type) ||
            !r.TryU32(out _) || !r.TryI64(out _))
        {
            return false;
        }

        emit(new PetAcquiredEvent(ts, entity, pet, level, type));
        return true;
    }
}
