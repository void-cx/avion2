using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>An item we picked up from the ground.</summary>
/// <param name="SourceId">The entity it dropped from (a monster or a chest).</param>
public sealed record LootPickup(long TimestampMs, uint ItemId, uint Count, uint SourceId);

/// <summary>
/// Drops on the ground (<c>1E 56</c>), who took them (<c>1F 56</c>) and our own pick-up answers (<c>21 56</c>), joined
/// by the drop's uid: our own pickups, including items that never show in the bag (souls). A pick-up answer makes a
/// take ours even while our character id is not known; otherwise our character id comes from the identity record (or
/// the party roster), and from the first take we answered. Not thread-safe.
/// </summary>
public sealed class LootTracker
{
    private const int MaxOpen = 2_000;

    /// <summary>How many takes of unknown takers wait for a pick-up answer (it normally comes first).</summary>
    private const int MaxWaiting = 64;

    /// <summary>Drops on the ground, by uid.</summary>
    private readonly Dictionary<long, LootDropEvent> _open = new();

    /// <summary>Drops we picked up (<c>21 56</c>) whose take has not come yet.</summary>
    private readonly HashSet<long> _picked = new();

    /// <summary>Takes by someone we can't tell from us yet (our id not known), in case our pick-up answer comes after.</summary>
    private readonly Dictionary<long, (LootDropEvent Drop, LootTakenEvent Take)> _waiting = new();

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

                _open[drop.Uid] = drop;
                break;
            case LootPickedEvent { Result: 0 } picked:
                if (_waiting.Remove(picked.DropUid, out var early))
                {
                    Ours(early.Drop, early.Take);
                }
                else
                {
                    if (_picked.Count >= MaxOpen)
                    {
                        _picked.Clear();
                    }

                    _picked.Add(picked.DropUid);
                }

                break;
            case LootTakenEvent taken:
                bool answered = _picked.Remove(taken.Uid);
                if (!_open.Remove(taken.Uid, out var dropped))
                {
                    break; // dropped before we were watching: the item is not known
                }

                if (answered || (taken.TakerCharacterId != 0 && taken.TakerCharacterId == SelfCharacterId))
                {
                    Ours(dropped, taken);
                }
                else if (taken.TakerCharacterId != 0 && SelfCharacterId is null)
                {
                    if (_waiting.Count >= MaxWaiting)
                    {
                        _waiting.Clear();
                    }

                    _waiting[taken.Uid] = (dropped, taken);
                }

                break;
        }
    }

    private void Ours(LootDropEvent drop, LootTakenEvent take)
    {
        PickedUp?.Invoke(new LootPickup(take.TimestampMs, drop.ItemId, drop.Count, drop.SourceId));
        if (SelfCharacterId is not null || take.TakerCharacterId == 0)
        {
            return;
        }

        // Now we know who we are: of the takes still waiting, ours count, the others were someone else's
        SelfCharacterId = take.TakerCharacterId;
        var waiting = _waiting.Values.Where(w => w.Take.TakerCharacterId == SelfCharacterId).OrderBy(w => w.Take.TimestampMs).ToList();
        _waiting.Clear();
        foreach (var (earlierDrop, earlierTake) in waiting)
        {
            PickedUp?.Invoke(new LootPickup(earlierTake.TimestampMs, earlierDrop.ItemId, earlierDrop.Count, earlierDrop.SourceId));
        }
    }
}
