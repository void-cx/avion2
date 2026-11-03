using System.Buffers.Binary;

namespace Avion2.Protocol.Parsers;

/// <summary>
/// Inventory records (our own character). Each item record contains
/// <c>item u32, count u64, container u8, slot u8</c>, then 11 zero bytes; for gear the enchant level follows in one of
/// the next two bytes (staff +10, belt and rune +2 in the first; Bakarma Boots +5 in the second — why is not known).
/// Containers: 01 cube (the bag, currencies included), 0B equipped, 0F warehouse (gear and stacks the cube does not show),
/// 10 cooking ingredients and other crafting stacks (seen in crafting rewards).
/// </summary>
public static class InventoryParsers
{
    private const int RecordTail = 11;

    /// <summary>
    /// <c>11 56</c>: the whole inventory, sent when entering the world. Records differ in length (gear carries
    /// manastones, stats…), so the body is scanned for the record shape instead of walked.
    /// </summary>
    public static bool ParseInventoryList(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var items = new List<InventoryItem>();
        for (int i = 0; i + 4 + 8 + 2 + RecordTail <= body.Length;)
        {
            if (TryRecord(body, i, out var item))
            {
                items.Add(item);
                i += 4 + 8 + 2 + RecordTail;
            }
            else
            {
                i++;
            }
        }

        if (items.Count == 0)
        {
            return false;
        }

        emit(new InventoryListEvent(ts, items));
        return true;
    }

    /// <summary>
    /// <c>1B 56</c>: inventory slots changed (counts are the new totals). The first byte is how many; each change is
    /// 10 bytes (two flag bytes, a u32 whose top byte is 0, <c>00</c>, u16, <c>00</c>) and then the item record, the
    /// first at offset 11. Records differ in length (gear carries stats, some items an expiry time), so the following
    /// ones are found by that shape. Quest and pass rewards, crafting and loot often change several slots at once.
    /// </summary>
    public static bool ParseInventoryItem(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        const int at = 11;
        if (body.Length < at + 14 || !TryRecord(body, at, out var first, requireTail: false))
        {
            return false;
        }

        emit(new InventoryItemEvent(ts, first));
        int found = 1;
        for (int i = at + 14; found < body[0] && i + 14 <= body.Length;)
        {
            if (body[i - 1] == 0 && body[i - 4] == 0 && body[i - 5] == 0 && TryRecord(body, i, out var item, requireTail: false))
            {
                emit(new InventoryItemEvent(ts, item));
                found++;
                i += 14;
            }
            else
            {
                i++;
            }
        }

        return true;
    }

    private static bool TryRecord(ReadOnlySpan<byte> body, int i, out InventoryItem item, bool requireTail = true)
    {
        item = default!;
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(body[i..]);
        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(body[(i + 4)..]);
        byte container = body[i + 12];
        byte slot = body[i + 13];

        // Item ids are nine digits (1xxxxxxxx … 9xxxxxxxx); counts stay far below 10^12
        if (id is < 100_000_000 or > 999_999_999 || count > 1_000_000_000_000 || container is not (0x01 or 0x04 or 0x0B or 0x0F or 0x10))
        {
            return false;
        }

        // The 11 bytes after the slot are (almost) all zero in every record seen; gear's level follows (slot updates
        // carry it too, so an enchant shows at once)
        int zeros = 0;
        bool tail = i + 14 + RecordTail <= body.Length;
        for (int k = 0; tail && k < RecordTail; k++)
        {
            zeros += body[i + 14 + k] == 0 ? 1 : 0;
        }

        tail &= zeros >= RecordTail - 2;
        if (requireTail && !tail)
        {
            return false;
        }

        byte? enchant = null;
        uint? itemLevel = null;
        int levels = i + 14 + RecordTail + 1;
        if (tail && container is 0x0B or 0x0F && levels + 1 < body.Length && body[levels] <= 30 && body[levels + 1] <= 30)
        {
            enchant = body[levels] != 0 ? body[levels] : body[levels + 1];
            if (container == 0x0B)
            {
                itemLevel = ItemLevel(body, levels, enchant.Value);
            }
        }

        item = new InventoryItem(id, count, container, slot, enchant, itemLevel);
        return true;
    }

    /// <summary>
    /// Worn gear carries its own item level: a <c>u32</c> 14 bytes after the enchant level. The enchant sits in the
    /// first or the second of its two bytes, and everything after it moves along with it; at +0 the item level
    /// itself shows which (its low byte is never 0: item levels are below 256).
    /// </summary>
    private static uint? ItemLevel(ReadOnlySpan<byte> body, int levels, byte enchant)
    {
        int shift = body[levels] != 0 ? 0 : body[levels + 1] != 0 ? 1
            : levels + 14 < body.Length && body[levels + 14] != 0 ? 0 : 1;
        int at = levels + shift + 14;
        if (at + 4 > body.Length)
        {
            return null;
        }

        uint level = BinaryPrimitives.ReadUInt32LittleEndian(body[at..]);
        return level is > 0 and < 1_000 && level >= enchant ? level : null;
    }

    /// <summary>
    /// <c>1E 56</c>: count u8, then per drop (63 bytes): source entity u32, the source's position x/y/z f32, the item's
    /// own position, drop uid i64, item id u32, count u32, owner type u8, owner character u32, 00 00, owner server u16,
    /// time u64, 2 bytes. One trailing byte.
    /// </summary>
    public static bool ParseLootDrop(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        const int Size = 63;
        if (body.Length < 1 || body[0] is 0 or > 32 || body.Length < 1 + body[0] * Size)
        {
            return false;
        }

        var drops = new List<GameEvent>();
        for (int n = 0; n < body[0]; n++)
        {
            var r = new SpanReader(body, 1 + n * Size);
            r.TryU32(out uint source);
            r.Position += 12; // the source's position
            if (!r.TryF32(out float x) || !r.TryF32(out float y) || !r.TryF32(out float z) ||
                !r.TryI64(out long uid) || !r.TryU32(out uint itemId) || itemId == 0 ||
                !r.TryU32(out uint count) || !r.TryU8(out _) || !r.TryU32(out uint owner) || !r.TryU16(out _) ||
                !r.TryU16(out ushort server))
            {
                return false;
            }

            drops.Add(new LootDropEvent(ts, source, uid, itemId, count, owner, server, new Vec3(x, y, z)));
        }

        drops.ForEach(emit);
        return true;
    }

    /// <summary>
    /// <c>1F 56</c>: count u8, then per drop (20 bytes): source entity u32, drop uid i64, taker character u32 (0 = it
    /// vanished), 00 00, server u16. One trailing byte.
    /// </summary>
    public static bool ParseLootTaken(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        const int Size = 20;
        if (body.Length < 1 || body[0] is 0 or > 64 || body.Length < 1 + body[0] * Size)
        {
            return false;
        }

        for (int n = 0; n < body[0]; n++)
        {
            var r = new SpanReader(body, 1 + n * Size);
            r.TryU32(out uint source);
            r.TryI64(out long uid);
            r.TryU32(out uint taker);
            emit(new LootTakenEvent(ts, source, uid, taker));
        }

        return true;
    }
}
