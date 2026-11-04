using System.Windows;
using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <summary>The legions seen (the Legions page): taken from the meter and from character lookups, saved now and then.</summary>
public sealed partial class AppState
{
    private LegionsSeenBook? _legionsSeen;
    private DateTime _legionsSeenSaved;
    private bool _legionsSeenDirty;

    /// <summary>Every legion seen, per server, with the members seen in it (saved in legions_seen.json).</summary>
    public LegionsSeenBook LegionsSeen => _legionsSeen ??= LegionBoard.Load();

    /// <summary>Raised on the UI thread when legions or members were seen.</summary>
    public event Action? LegionsSeenUpdated;

    /// <summary>
    /// The server and legion of the character the pages show, for marking it on the Legions page; null parts when not
    /// known.
    /// </summary>
    public (ushort? Server, string? Legion) OwnLegion()
    {
        string? name = ViewedCharacter;
        ushort? server = OurCharacters.FirstOrDefault(c => c.Name == name)?.ServerId;
        string? legion = Legion?.Name;
        if (!ViewsOther && Engine?.Status() is { SelfName: { } self } status && self == name)
        {
            server = status.SelfServer ?? server;
            legion = status.SelfLegion ?? legion;
        }

        if (legion is null && server is { } on && name is not null)
        {
            // our character as the book saw it (tagged in range, or looked up on the site)
            legion = LegionsSeen.On(on).FirstOrDefault(l => l.Members.Any(m => m.Name == name))?.Name;
        }

        return (server, legion);
    }

    /// <summary>Takes the meter's sightings (and its next ones).</summary>
    private void AttachLegionsSeen(MeterEngine engine) =>
        engine.LegionsSighted += () => Application.Current.Dispatcher.BeginInvoke(() =>
            TakeSightings(engine.DrainLegionSightings(), engine.DrainLegionListings(), engine.DrainLegionRosters()));

    /// <summary>A character looked up on the official site: their legion, item level and combat power as the site shows them.</summary>
    private void OnArmoryProfile(ArmoryProfile profile)
    {
        if (LegionBoard.FromProfile(profile, CharacterSheet.ParseClass(profile.ClassName)) is { } seen)
        {
            Application.Current.Dispatcher.BeginInvoke(() => TakeSightings([seen]));
        }
    }

    private void TakeSightings(IReadOnlyList<LegionSighting> sightings,
        IReadOnlyList<(Avion2.Protocol.LegionListing Listing, long AtMs)>? listings = null,
        IReadOnlyList<LegionRosterSighting>? rosters = null)
    {
        bool changed = false;
        foreach (var roster in rosters ?? [])
        {
            changed |= LegionsSeen.TakeRoster(roster);
        }

        foreach (var seen in sightings)
        {
            changed |= LegionsSeen.Take(seen);
        }

        foreach (var (listing, at) in listings ?? [])
        {
            changed |= LegionsSeen.TakeListing(listing, at);
        }

        if (!changed)
        {
            return;
        }

        LegionsSeenUpdated?.Invoke();

        // Busy towns bring many players a minute: write at most every minute (SaveSettings writes the rest on exit)
        _legionsSeenDirty = true;
        if (DateTime.UtcNow - _legionsSeenSaved > TimeSpan.FromMinutes(1))
        {
            SaveLegionsSeen();
        }
    }

    private void SaveLegionsSeen()
    {
        if (!KeepsGameState || !_legionsSeenDirty || _legionsSeen is null)
        {
            return;
        }

        _legionsSeenSaved = DateTime.UtcNow;
        _legionsSeenDirty = !LegionBoard.Save(_legionsSeen);
    }
}
