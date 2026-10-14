using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>Our legion: details from <c>03 8A</c>, members from <c>05 8A</c> pages (merged by name).</summary>
public sealed class LegionState
{
    private readonly Dictionary<string, LegionMember> _members = new(StringComparer.Ordinal);

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

    public bool IsKnown => Name is not null;

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
}
