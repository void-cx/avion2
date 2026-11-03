using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>Our legion: details from <c>03 8A</c>, members from <c>05 8A</c> pages (merged by name).</summary>
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

    /// <summary>
    /// When the rent of the legion's Legion Hall ends (Unix ms, from <c>45 8A</c>); 0 when the game said there is no hall;
    /// null while no such record was seen.
    /// </summary>
    public long? HallRentEndsMs { get; set; }

    public bool IsKnown => Name is not null;

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
        HallRentEndsMs = null;
    }

    /// <summary>Raised when something changed (capture thread).</summary>
    public event Action? Changed;

    public void Apply(GameEvent e)
    {
        switch (e)
        {
            case LegionDetailsEvent details:
                Name = details.Name;
                Leader = details.Leader;
                Texts = details.Texts.ToList();
                UpdatedMs = details.TimestampMs;
                Changed?.Invoke();
                break;
            case LegionRosterEvent roster:
                foreach (var member in roster.Members)
                {
                    _members[member.Name] = member;
                }

                UpdatedMs = roster.TimestampMs;
                Changed?.Invoke();
                break;
        }
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
        Changed?.Invoke();
    }
}
