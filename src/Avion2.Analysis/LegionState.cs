using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>
/// Our legion: details from <c>03 8A</c>, members from <c>05 8A</c> (the whole list, each member with class, item level
/// and combat power; merged by name when it could only be searched for names); followed when we create,
/// leave or disband one (<c>01 8A</c>, <c>0B 8A</c>, <c>35 8A</c>) and by our own legion tag (<c>33 8A</c>).
/// </summary>
public sealed class LegionState
{
    private readonly Dictionary<string, LegionMember> _members = new(StringComparer.Ordinal);

    /// <summary>The character the legion is of (ours, as the game named us when it sent the legion).</summary>
    public string? Character { get; set; }

    public string? Name { get; set; }

    public string? Leader { get; set; }

    public List<string> Texts { get; set; } = new();

    /// <summary>Members, most recently online first.</summary>
    public List<LegionMember> Members
    {
        get => _members.Values.OrderByDescending(m => m.LastOnlineMs ?? 0).ThenBy(m => m.Name, StringComparer.CurrentCulture).ToList();
        set
        {
            _members.Clear();
            foreach (var member in value)
            {
                _members[member.Name] = member;
            }
        }
    }

    public long UpdatedMs { get; set; }

    /// <summary>The most members the legion can have (from <c>03 8A</c>), when known.</summary>
    public int? MaxMembers { get; set; }

    /// <summary>When the game last sent the whole member list (Unix ms); null when it did not since the legion was known.</summary>
    public long? RosterMs { get; set; }

    /// <summary>
    /// When the rent of the legion's Legion Hall ends (Unix ms, from <c>45 8A</c>); 0 when the game said there is no hall;
    /// null while no such record was seen.
    /// </summary>
    public long? HallRentEndsMs { get; set; }

    public bool IsKnown => Name is not null;

    /// <summary>The character is in no legion (it left, or its legion was disbanded), as of <see cref="UpdatedMs"/>.</summary>
    public bool NoLegion { get; set; }

    /// <summary>Counts every change, so a caller can tell whether an event changed the legion.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int Revision { get; private set; }

    /// <summary>
    /// "Legion Hall rent ends in N days (date)" for the card, or null when no rent is known. A rent that ran out (as
    /// last seen) says so.
    /// </summary>
    public string? HallRentText(DateTimeOffset now)
    {
        if (HallRentEndsMs is not ({ } ms and > 0))
        {
            return null;
        }

        var ends = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToOffset(now.Offset);
        string date = ends.ToString("ddd, dd MMM", System.Globalization.CultureInfo.InvariantCulture);
        var left = ends - now;
        if (left <= TimeSpan.Zero)
        {
            return $"Legion Hall rent ended {date}";
        }

        string inText = left.TotalDays >= 2 ? $"{(int)left.TotalDays} days"
            : left.TotalDays >= 1 ? "1 day"
            : left.TotalHours >= 1 ? $"{(int)left.TotalHours} h"
            : "less than an hour";
        return $"Legion Hall rent ends in {inText} ({date})";
    }

    /// <summary>Forgets the legion (we switched to another character).</summary>
    public void Clear()
    {
        Character = Name = Leader = null;
        Texts = new();
        _members.Clear();
        UpdatedMs = 0;
        RosterMs = null;
        MaxMembers = null;
        HallRentEndsMs = null;
        NoLegion = false;
        Revision++;
    }

    private void Touch()
    {
        Revision++;
        Changed?.Invoke();
    }

    /// <summary>Raised when something changed (capture thread).</summary>
    public event Action? Changed;

    public void Apply(GameEvent e)
    {
        switch (e)
        {
            case LegionDetailsEvent details:
                if (Name is { } before && before != details.Name)
                {
                    Forget(); // another legion's details: the old members must not mix in
                }

                Name = details.Name;
                Leader = details.Leader;
                Texts = details.Texts.ToList();
                MaxMembers = details.MaxMembers ?? MaxMembers;
                NoLegion = false;
                UpdatedMs = details.TimestampMs;
                Touch();
                break;
            case LegionRosterEvent roster:
                if (roster.Complete)
                {
                    _members.Clear(); // the whole list: who is not on it left
                    RosterMs = roster.TimestampMs;
                }

                foreach (var member in roster.Members)
                {
                    _members[member.Name] = member;
                }

                UpdatedMs = roster.TimestampMs;
                Touch();
                break;
        }
    }

    /// <summary>
    /// Follows our own legion changing: created (<c>01 8A</c>), left (<c>0B 8A</c>), disbanded (<c>35 8A</c>), us out
    /// of it (<c>0C 8A</c> naming us), and our own legion tag (<c>33 8A</c>), which the game sends after each of them
    /// and at map loads. A member out of the legion (<c>0C 8A</c>) leaves the member list; one coming online
    /// (<c>39 8A</c>) is marked online now.
    /// </summary>
    /// <param name="selfId">Our entity, when known.</param>
    /// <param name="self">Our character's name, when known; without it nothing is taken.</param>
    public void ApplyOwn(GameEvent e, uint? selfId, string? self)
    {
        if (self is null)
        {
            return;
        }

        switch (e)
        {
            case LegionTagEvent tag when tag.EntityId == selfId:
                if (tag.Legion.Length == 0)
                {
                    if (!NoLegion || IsKnown)
                    {
                        Leave(self, tag.TimestampMs);
                    }
                }
                else if (tag.Legion != Name || NoLegion)
                {
                    Join(self, tag.Legion, null, tag.TimestampMs);
                }

                break;
            case LegionCreatedEvent { Result: 0, Name.Length: > 0 } created:
                Join(self, created.Name, self, created.TimestampMs);
                _members[self] = new LegionMember(self, null, created.TimestampMs);
                break;
            case LegionLeftEvent { Result: 0 } or LegionDisbandedEvent { Result: 0 }:
                Leave(self, e.TimestampMs);
                break;
            case LegionMemberLeftEvent left when left.Name == self:
                Leave(self, e.TimestampMs);
                break;
            case LegionMemberLeftEvent left when _members.Remove(left.Name):
                UpdatedMs = left.TimestampMs;
                Touch();
                break;
            case LegionMemberOnlineEvent online when IsKnown && _members.TryGetValue(online.Name, out var member):
                _members[online.Name] = member with { LastOnlineMs = online.TimestampMs };
                Touch();
                break;
        }
    }

    /// <summary>We are in <paramref name="legion"/> now (a new one: what we knew of another goes).</summary>
    private void Join(string self, string legion, string? leader, long atMs)
    {
        if (Name != legion)
        {
            Forget();
        }

        Character = self;
        Name = legion;
        Leader = leader ?? Leader;
        NoLegion = false;
        UpdatedMs = atMs;
        Touch();
    }

    /// <summary>We are in no legion now.</summary>
    private void Leave(string self, long atMs)
    {
        Forget();
        Character = self;
        Name = null;
        NoLegion = true;
        UpdatedMs = atMs;
        Touch();
    }

    private void Forget()
    {
        Leader = null;
        Texts = new();
        _members.Clear();
        RosterMs = null;
        MaxMembers = null;
        HallRentEndsMs = null;
    }

    /// <summary>
    /// The Legion Hall record (<c>45 8A</c>). It does not name the character, so the caller applies it only while it is
    /// sure whose it is.
    /// </summary>
    public void ApplyHall(LegionHallInfoEvent hall)
    {
        if (hall.Result != 0)
        {
            return;
        }

        HallRentEndsMs = hall.HallId != 0 && hall.RentEndsMs > 0 ? hall.RentEndsMs : 0;
        Touch();
    }
}
