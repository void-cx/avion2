using System.Text.Json.Serialization;
using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>
/// The inventory of each of our characters, by name: the one played now stays current, the others as they were last
/// seen. Saved between sessions, so the Gear Forge knows every character's gear whichever one the game starts with.
/// </summary>
public sealed class InventoryBook
{
    [JsonInclude]
    [JsonPropertyName("characters")]
    private Dictionary<string, InventoryState> _byName = new();

    /// <summary>Every character's inventory, the one seen last first.</summary>
    [JsonIgnore]
    public IReadOnlyList<InventoryState> All => _byName.Values.OrderByDescending(i => i.UpdatedMs).ToList();

    /// <summary>The character played last.</summary>
    [JsonIgnore]
    public InventoryState? Latest => All.FirstOrDefault();

    public InventoryState? For(string? name) => name is not null && _byName.TryGetValue(name, out var inventory) ? inventory : null;

    /// <summary>
    /// Takes the meter's inventory and returns the one to show, or null when it can't be placed yet. A full list
    /// replaces its character's inventory (one without a name yet is shown but kept only once the game names it);
    /// slot updates before this session's list go on top of that character's saved inventory, once we know who we are.
    /// </summary>
    public InventoryState? Take(InventoryState live)
    {
        if (live.IsKnown)
        {
            if (live.Character is { } name)
            {
                _byName[name] = live;
            }

            return live;
        }

        if (For(live.Character) is not { } saved)
        {
            return null;
        }

        var merged = saved.Copy();
        foreach (var item in live.Items)
        {
            merged.Apply(new InventoryItemEvent(live.UpdatedMs, item));
        }

        _byName[saved.Character ?? live.Character!] = merged;
        return merged;
    }

    /// <summary>An inventory saved by an older AVION2 (one file, one character).</summary>
    public void Add(InventoryState inventory)
    {
        if (inventory.Character is { } name && inventory.IsKnown && For(name) is null)
        {
            _byName[name] = inventory;
        }
    }
}
