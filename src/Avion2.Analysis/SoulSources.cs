using System.Text.Json.Serialization;
using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>A soul we picked up and the monster it dropped from.</summary>
/// <param name="NpcCode">The dropping monster's NPC code; null when it was not in view (its spawn was not seen).</param>
public sealed record SoulDrop(long TimestampMs, uint ItemId, uint Count, uint? NpcCode);

/// <summary>A monster that dropped a kind of soul for us, and how many souls it gave.</summary>
public sealed record SoulSource(uint NpcCode, uint Souls);

/// <summary>
/// Which monsters dropped which souls for us, learned from our own pickups (the drop's source entity, <c>1E 56</c>,
/// named by its spawn). Kept for all our characters together, like the pets (they belong to the account). Saved with
/// the progress. Takes a session's drops more than once without counting them twice.
/// </summary>
public sealed class SoulSourceBook
{
    /// <summary>Soul item → NPC code → souls.</summary>
    [JsonInclude]
    [JsonPropertyName("items")]
    private Dictionary<uint, Dictionary<uint, uint>> _byItem = new();

    /// <summary>How many drops of each live session were counted already.</summary>
    private readonly Dictionary<Guid, int> _counted = new();

    /// <summary>Counts one soul drop (drops of unknown monsters are left out).</summary>
    public void Add(SoulDrop drop)
    {
        if (drop.NpcCode is not { } npc || drop.Count == 0)
        {
            return;
        }

        if (!_byItem.TryGetValue(drop.ItemId, out var sources))
        {
            _byItem[drop.ItemId] = sources = new Dictionary<uint, uint>();
        }

        sources[npc] = sources.GetValueOrDefault(npc) + drop.Count;
    }

    /// <summary>Counts the drops of a live session not counted yet; true when there were any.</summary>
    public bool Take(Guid session, IReadOnlyList<SoulDrop> drops)
    {
        int counted = _counted.GetValueOrDefault(session);
        if (drops.Count <= counted)
        {
            return false;
        }

        for (int i = counted; i < drops.Count; i++)
        {
            Add(drops[i]);
        }

        _counted[session] = drops.Count;
        return true;
    }

    /// <summary>The monsters that dropped this soul item, most souls first.</summary>
    public IReadOnlyList<SoulSource> For(uint soulItem) => _byItem.TryGetValue(soulItem, out var sources)
        ? sources.Select(s => new SoulSource(s.Key, s.Value)).OrderByDescending(s => s.Souls).ThenBy(s => s.NpcCode).ToList()
        : [];
}

/// <summary>One pet kind on the pet list: its souls, what they lead to, and where they came from.</summary>
/// <param name="Needed">Souls the next step takes (5 for the pet, 25 for level 2); null when not known.</param>
/// <param name="Level">The pet's level, when owned and known.</param>
public sealed record PetSoulRow(uint PetId, string Name, uint Souls, uint? Needed, bool Owned, uint? Level, string Sources)
{
    /// <summary>"3 / 5 to the pet", "23 / 25 to level 2", or "71 souls".</summary>
    public string Progress => Needed is { } needed
        ? $"{Math.Min(Souls, needed)} / {needed} {(Owned ? $"to level {Level + 1}" : "to the pet")}"
        : Souls == 1 ? "1 soul" : $"{Souls} souls";

    /// <summary>"Level 2", "Owned" (level not known), or "Not owned".</summary>
    public string State => !Owned ? "Not owned" : Level is { } level ? $"Level {level}" : "Owned";

    /// <summary>How far to the next step, 0 to 1 (0 without one).</summary>
    public double Fraction => Needed is { } needed and > 0 ? Math.Min(1.0, (double)Souls / needed) : 0;

    public bool HasSources => Sources.Length > 0;
}

/// <summary>The pet list and the line shown when a soul comes in.</summary>
public static class PetSoulTable
{
    /// <summary>
    /// Souls of a kind that bring its pet level 2: the souls go on counting once the pet is owned, and 25 of them were
    /// spent at each of the five level 2s in our captures (<c>14 90</c> with <c>0E 90</c>). What level 3 takes is not known.
    /// </summary>
    public const uint SoulsForLevel2 = 25;

    /// <summary>Souls the next step of a pet kind takes: the pet itself, or level 2; null when not known.</summary>
    public static uint? Needed(CharacterProgress progress, uint pet)
    {
        if (progress.Pets is not { } owned)
        {
            return null;
        }

        if (!owned.Contains(pet))
        {
            return CharacterProgress.SoulsPerPet;
        }

        return progress.PetLevels.TryGetValue(pet, out uint level) && level == 1 ? SoulsForLevel2 : null;
    }

    /// <summary>
    /// Every pet kind with souls, closest to its next step first, then the most souls; with the monsters that dropped
    /// its soul ("Corrupted Spirit ×42 · Canyon Vine Spirit ×3"). Empty before the game sent the pets.
    /// </summary>
    /// <param name="soulItem">The soul item of a pet kind, when known (it joins the drops to the kind).</param>
    /// <param name="npcName">An NPC's name by code, or null.</param>
    public static IReadOnlyList<PetSoulRow> Rows(CharacterProgress progress, SoulSourceBook sources, Func<uint, uint?> soulItem,
        Func<uint, string> petName, Func<uint, string?> npcName, int sourcesShown = 3)
    {
        if (progress.Pets is not { } owned)
        {
            return [];
        }

        var rows = new List<PetSoulRow>();
        foreach (var (pet, souls) in progress.Souls)
        {
            if (souls == 0)
            {
                continue;
            }

            var from = soulItem(pet) is { } item ? sources.For(item) : [];
            var named = from.Select(s => (Name: npcName(s.NpcCode), s.Souls)).Where(s => s.Name is not null).ToList();
            string text = string.Join(" · ", named.Take(sourcesShown).Select(s => $"{s.Name} ×{s.Souls}"));
            if (named.Count > sourcesShown)
            {
                text += $" · {named.Count - sourcesShown} more";
            }

            uint? level = progress.PetLevels.TryGetValue(pet, out uint l) ? l : null;
            rows.Add(new PetSoulRow(pet, petName(pet), souls, Needed(progress, pet), owned.Contains(pet), owned.Contains(pet) ? level : null, text));
        }

        return rows.OrderByDescending(r => r.Needed is not null).ThenByDescending(r => r.Fraction).ThenByDescending(r => r.Souls)
            .ThenBy(r => r.PetId).ToList();
    }

    /// <summary>"Soul of Zelophi: 3/5", "Soul of Klaw: 23/25 to level 2" or "Soul of Klaw: 71", after a gain; null before the roster.</summary>
    public static string? Notice(CharacterProgress progress, uint pet, Func<uint, string> petName)
    {
        if (progress.Pets is not { } owned)
        {
            return null;
        }

        uint souls = progress.Souls.GetValueOrDefault(pet);
        string count = Needed(progress, pet) is { } needed
            ? $"{Math.Min(souls, needed)}/{needed}{(owned.Contains(pet) ? " to level 2" : string.Empty)}"
            : $"{souls}";
        return $"Soul of {petName(pet)}: {count}";
    }
}
