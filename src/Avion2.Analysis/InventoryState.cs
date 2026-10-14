using System.Text.Json.Serialization;
using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>Item ids with a meaning of their own.</summary>
public static class ItemIds
{
    public const uint Kinah = 930_100_031;
    public const uint KinahBound = 930_100_001;
    public const uint EnhanceStone = 930_100_023;
    public const uint AbyssPoints = 930_100_003;
}

/// <summary>
/// Our character's inventory: the full list sent when entering the world (<c>11 56</c>), kept current by slot
/// updates (<c>1B 56</c>). Saved between sessions so the Gear Forge knows it without a running meter.
/// </summary>
public sealed class InventoryState
{
    [JsonInclude]
    [JsonPropertyName("items")]
    private Dictionary<string, InventoryItem> _slots = new();

    /// <summary>The character the inventory belongs to (as far as known when it was saved).</summary>
    [JsonPropertyName("character")]
    public string? Character { get; set; }

    /// <summary>When the last full list arrived (Unix ms); null = never.</summary>
    [JsonInclude]
    [JsonPropertyName("listMs")]
    public long? ListMs { get; private set; }

    [JsonInclude]
    [JsonPropertyName("updatedMs")]
    public long UpdatedMs { get; private set; }

    [JsonIgnore]
    public bool IsKnown => ListMs is not null;

    [JsonIgnore]
    public IEnumerable<InventoryItem> Items => _slots.Values;

    /// <summary>Equipped gear (with enchant levels).</summary>
    [JsonIgnore]
    public IEnumerable<InventoryItem> Equipped => _slots.Values.Where(i => i.Container == InventoryItem.Equipped).OrderBy(i => i.Slot);

    /// <summary>Kinah in the cube, bound and tradable (what the game's currency bar shows).</summary>
    [JsonIgnore]
    public ulong Kinah => Count(ItemIds.Kinah) + Count(ItemIds.KinahBound);

    /// <summary>How many of an item are in the cube (the bag).</summary>
    public ulong Count(uint itemId) => Count(itemId, InventoryItem.Cube);

    /// <summary>How many of an item are in the warehouse.</summary>
    public ulong InWarehouse(uint itemId) => Count(itemId, InventoryItem.Warehouse);

    private ulong Count(uint itemId, byte container) =>
        (ulong)_slots.Values.Where(i => i.ItemId == itemId && i.Container == container).Sum(i => (decimal)i.Count);

    /// <summary>Applies inventory events; returns true when something changed.</summary>
    public bool Apply(GameEvent e)
    {
        switch (e)
        {
            case InventoryListEvent list:
                _slots = list.Items.ToDictionary(Key);
                ListMs = UpdatedMs = list.TimestampMs;
                return true;
            case InventoryItemEvent { Item: var item } update:
                if (item.Count == 0)
                {
                    _slots.Remove(Key(item));
                }
                else
                {
                    // Updates carry no levels: keep the ones from the list
                    _slots[Key(item)] = _slots.TryGetValue(Key(item), out var old) && old.ItemId == item.ItemId
                        ? item with { Enchant = item.Enchant ?? old.Enchant }
                        : item;
                }

                UpdatedMs = update.TimestampMs;
                return true;
            default:
                return false;
        }
    }

    public InventoryState Copy() => new()
    {
        _slots = new Dictionary<string, InventoryItem>(_slots),
        Character = Character,
        ListMs = ListMs,
        UpdatedMs = UpdatedMs,
    };

    private static string Key(InventoryItem item) => $"{item.Container:X2}:{item.Slot}";
}
