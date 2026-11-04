using System.Text.Json.Serialization;
using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>One craft of ours: the recipe, how it ended, and what it took from and put into the bag.</summary>
public sealed class CraftEntry
{
    [JsonPropertyName("startMs")]
    public long StartMs { get; set; }

    [JsonPropertyName("endMs")]
    public long? EndMs { get; set; }

    /// <summary>The recipe (<c>6F 8D</c>).</summary>
    [JsonPropertyName("recipe")]
    public uint RecipeId { get; set; }

    /// <summary>0 success, 1 combo success (the better item), 2 failed (<c>71 8D</c>).</summary>
    [JsonPropertyName("result")]
    public byte? Result { get; set; }

    /// <summary>What left the bag when the craft started (bag updates in the same instant).</summary>
    [JsonPropertyName("used")]
    public List<RunItem> Used { get; set; } = [];

    /// <summary>What arrived in the bag when it ended.</summary>
    [JsonPropertyName("made")]
    public List<RunItem> Made { get; set; } = [];

    [JsonIgnore]
    public bool Failed => Result == 2;

    [JsonIgnore]
    public bool Combo => Result == 1;
}

/// <summary>
/// Our crafts as they happen: a craft's materials leave the bag (<c>1B 56</c>) in the same instant as its start
/// (<c>6F 8D</c>), and the item made arrives with its end (<c>71 8D</c>). Each craft is handed on once the instant of
/// its end has passed; crafts that end before the game has named us wait for the name. Bag changes are only counted
/// while the inventory is known (else a craft is logged without them). Not thread-safe.
/// </summary>
public sealed class CraftLog
{
    /// <summary>How far apart a record and its bag updates may be (they share the millisecond in our captures).</summary>
    private const long SameInstantMs = 50;

    /// <summary>Bag changes of the last moment, for a craft start that comes after its materials left.</summary>
    private readonly List<(long At, uint Item, long Delta)> _changes = new();

    private CraftEntry? _running;
    private uint _station;
    private CraftEntry? _ended;

    /// <summary>Crafts that ended before the game named us.</summary>
    private readonly List<CraftEntry> _unnamed = new();

    /// <summary>How many crafts wait for our name at most.</summary>
    private const int MaxUnnamed = 50;

    /// <summary>A craft finished: the entry and the character it belongs to.</summary>
    public event Action<CraftEntry, string>? Crafted;

    /// <summary>
    /// Applies an event; call it before <paramref name="inventory"/> takes the event, so a slot update is measured
    /// against the count before it. <paramref name="character"/> is who we are (null while the game has not named us:
    /// a craft ended then waits until it is).
    /// </summary>
    public void Apply(GameEvent e, InventoryState inventory, string? character)
    {
        if (_ended is { EndMs: { } endedAt } done && e.TimestampMs > endedAt + SameInstantMs)
        {
            _ended = null;
            if (_unnamed.Count < MaxUnnamed)
            {
                _unnamed.Add(done);
            }
        }

        if (character is not null && _unnamed.Count > 0)
        {
            foreach (var craft in _unnamed)
            {
                Crafted?.Invoke(craft, character);
            }

            _unnamed.Clear();
        }

        switch (e)
        {
            case InventoryItemEvent { Item: var item } update when inventory.IsKnown && inventory.SlotCount(item) is { } before:
                long delta = (long)item.Count - (long)before;
                if (delta == 0)
                {
                    break;
                }

                if (_ended is { } ending && delta > 0)
                {
                    Add(ending.Made, item.ItemId, delta);
                }
                else if (_running is { } running && delta < 0 && update.TimestampMs - running.StartMs <= SameInstantMs)
                {
                    Add(running.Used, item.ItemId, -delta);
                }
                else
                {
                    _changes.RemoveAll(c => update.TimestampMs - c.At > SameInstantMs);
                    _changes.Add((update.TimestampMs, item.ItemId, delta));
                }

                break;
            case CraftStartEvent start:
                _running = new CraftEntry { StartMs = start.TimestampMs, RecipeId = start.RecipeId };
                _station = start.StationId;
                foreach (var (at, id, d) in _changes)
                {
                    if (d < 0 && start.TimestampMs - at <= SameInstantMs)
                    {
                        Add(_running.Used, id, -d);
                    }
                }

                _changes.Clear();
                break;
            case CraftEndEvent end when _running is { } craft && end.StationId == _station:
                craft.EndMs = end.TimestampMs;
                craft.Result = end.Result;
                _ended = craft;
                _running = null;
                break;
            case InteractionInterruptedEvent stop when _running is not null && stop.ObjectId == _station:
            case ZoneChangeEvent:
                _running = null; // aborted: no end record, and whether the materials came back is not known
                break;
        }
    }

    private static void Add(List<RunItem> items, uint item, long count)
    {
        int i = items.FindIndex(x => x.ItemId == item);
        if (i >= 0)
        {
            items[i] = items[i] with { Count = items[i].Count + count };
        }
        else
        {
            items.Add(new RunItem(item, count));
        }
    }
}

/// <summary>The crafts of each of our characters, newest first. Saved between sessions (crafts.json).</summary>
public sealed class CraftBook
{
    /// <summary>Crafts kept per character.</summary>
    public const int Kept = 300;

    [JsonInclude]
    [JsonPropertyName("characters")]
    private Dictionary<string, List<CraftEntry>> _byName = new();

    public void Add(string character, CraftEntry craft)
    {
        if (!_byName.TryGetValue(character, out var list))
        {
            _byName[character] = list = [];
        }

        list.Insert(0, craft);
        if (list.Count > Kept)
        {
            list.RemoveRange(Kept, list.Count - Kept);
        }
    }

    /// <summary>A character's crafts, newest first.</summary>
    public IReadOnlyList<CraftEntry> For(string? character) =>
        character is not null && _byName.TryGetValue(character, out var list) ? list : [];
}

/// <summary>A craft as the craft log shows it.</summary>
public sealed record CraftLine(string When, string Made, string Used, bool Failed);

/// <summary>Words for the craft log.</summary>
public static class CraftText
{
    /// <summary>"Fruit Salad ×2 (combo)", "Truth Orb (failed)" and "Fresh Fruit 1 · Verteron Pepper (Bound) 1".</summary>
    public static CraftLine Line(CraftEntry craft, RecipeBook book, Func<uint, string?> itemName, TimeZoneInfo? zone = null)
    {
        var at = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(craft.StartMs), zone ?? TimeZoneInfo.Local);
        string when = at.ToString("MMM d, HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        string Name(uint id) => book.Name(id, itemName);
        string made;
        if (craft.Made.Count > 0)
        {
            made = string.Join(", ", craft.Made.Select(m => m.Count == 1 ? Name(m.ItemId) : $"{Name(m.ItemId)} ×{m.Count}"));
        }
        else
        {
            made = book.ById(craft.RecipeId) is { } recipe ? Name(recipe.Product) : $"Recipe {craft.RecipeId}";
        }

        made += craft.Result switch
        {
            1 => " (combo)",
            2 => " (failed)",
            null => " (no end seen)",
            _ => string.Empty,
        };
        string used = craft.Used.Count == 0 ? "Materials not seen (the bag wasn't known yet)"
            : string.Join(" · ", craft.Used.Select(u => $"{Name(u.ItemId)} {u.Count}"));
        return new CraftLine(when, made, used, craft.Failed);
    }
}
