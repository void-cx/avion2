using System.Buffers.Binary;

namespace Avion2.Protocol.Parsers;

/// <summary>
/// Inventory records (our own character). Each item record contains
/// <c>item u32, count u64, container u8, slot u8</c>, then 11 zero bytes; for gear the enchant level follows in one of
/// the next two bytes (staff +10, belt and rune +2 in the first; Bakarma Boots +5 in the second — why is not known).
/// Containers: 01 cube (the bag, currencies included), 0B equipped, 0F warehouse (gear and stacks the cube does not show).
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
    /// <c>1B 56</c>: one inventory slot changed — u8, u8, u8, u32, u32, then the item record at offset 11
    /// (count is the new total).
    /// </summary>
    public static bool ParseInventoryItem(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        const int at = 11;
        if (body.Length < at + 14 || !TryRecord(body, at, out var item, requireTail: false))
        {
            return false;
        }

        emit(new InventoryItemEvent(ts, item));
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
        if (id is < 100_000_000 or > 999_999_999 || count > 1_000_000_000_000 || container is not (0x01 or 0x04 or 0x0B or 0x0F))
        {
            return false;
        }

        byte? enchant = null;
        if (requireTail)
        {
            // The 11 bytes after the slot are (almost) all zero in every record seen
            int zeros = 0;
            for (int k = 0; k < RecordTail; k++)
            {
                zeros += body[i + 14 + k] == 0 ? 1 : 0;
            }

            if (zeros < RecordTail - 2)
            {
                return false;
            }

            int levels = i + 14 + RecordTail + 1;
            if (container is 0x0B or 0x0F && levels + 1 < body.Length && body[levels] <= 30 && body[levels + 1] <= 30)
            {
                enchant = body[levels] != 0 ? body[levels] : body[levels + 1];
            }
        }

        item = new InventoryItem(id, count, container, slot, enchant);
        return true;
    }

    /// <summary>
    /// <c>1E 56</c>: count u8, then per drop (63 bytes): drop id u32, position x/y/z f32, a second position, source
    /// entity u32, drop serial u32, item id u32, count u32, 02, owner character u32, 00 00, owner server u16, time u64,
    /// 2 bytes. One trailing byte.
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
            if (!r.TryU32(out uint dropId) || !r.TryF32(out float x) || !r.TryF32(out float y) || !r.TryF32(out float z))
            {
                return false;
            }

            r.Position += 12; // the second position
            if (!r.TryU32(out uint source) || !r.TryU32(out _) || !r.TryU32(out uint itemId) || itemId == 0 ||
                !r.TryU32(out uint count) || !r.TryU8(out _) || !r.TryU32(out uint owner) || !r.TryU16(out _) ||
                !r.TryU16(out ushort server))
            {
                return false;
            }

            drops.Add(new LootDropEvent(ts, dropId, source, itemId, count, owner, server, new Vec3(x, y, z)));
        }

        drops.ForEach(emit);
        return true;
    }

    /// <summary>
    /// <c>1F 56</c>: count u8, then per drop (20 bytes): drop id u32, source entity u32, drop serial u32, taker character
    /// u32 (0 = it vanished), 00 00, server u16. One trailing byte.
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
            r.TryU32(out uint dropId);
            r.Position += 8;
            r.TryU32(out uint taker);
            emit(new LootTakenEvent(ts, dropId, taker));
        }

        return true;
    }
}
