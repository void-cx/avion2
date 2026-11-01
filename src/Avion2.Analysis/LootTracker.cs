using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>An item we picked up from the ground.</summary>
public sealed record LootPickup(long TimestampMs, uint ItemId, uint Count, uint SourceId);

/// <summary>
/// Drops on the ground (<c>1E 56</c>) and who took them (<c>1F 56</c>): our own pickups, including items that never
/// show in the bag (souls). Our character id comes from the identity record (or the party roster). Not thread-safe.
/// </summary>
public sealed class LootTracker
{
    private const int MaxOpen = 2_000;
    private readonly Dictionary<uint, LootDropEvent> _open = new();

    /// <summary>Our character id, once known.</summary>
    public uint? SelfCharacterId { get; private set; }

    /// <summary>We picked something up.</summary>
    public event Action<LootPickup>? PickedUp;

    public void Apply(GameEvent e, string? selfName)
    {
        switch (e)
        {
            case SelfIdentityEvent { CharacterId: { } id }:
                SelfCharacterId = id;
                break;
            case PartyRosterEvent roster when SelfCharacterId is null && selfName is not null &&
                                              roster.Members.FirstOrDefault(m => m.Name == selfName) is { } me:
                SelfCharacterId = me.CharacterId;
                break;
            case LootDropEvent drop:
                if (_open.Count >= MaxOpen)
                {
                    _open.Clear(); // drops nobody took for a long time (left behind, or the take was missed)
                }

                _open[drop.DropId] = drop;
                break;
            case LootTakenEvent taken when _open.Remove(taken.DropId, out var dropped):
                if (taken.TakerCharacterId != 0 && taken.TakerCharacterId == SelfCharacterId)
                {
                    PickedUp?.Invoke(new LootPickup(taken.TimestampMs, dropped.ItemId, dropped.Count, dropped.SourceId));
                }

                break;
        }
    }
}
