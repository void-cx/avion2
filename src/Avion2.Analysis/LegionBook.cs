using System.Text.Json.Serialization;

namespace Avion2.Analysis;

/// <summary>The legion of each of our characters, by name (saved between sessions, so Home shows it).</summary>
public sealed class LegionBook
{
    [JsonInclude]
    [JsonPropertyName("characters")]
    private Dictionary<string, LegionState> _byName = new();

    public LegionState? For(string? name) => name is not null && _byName.TryGetValue(name, out var legion) ? legion : null;

    /// <summary>
    /// Takes the meter's legion for its character. What the game did not send again this session (details, members
    /// not on the pages it sent) is kept from before. Returns false when the legion can't be placed (no character).
    /// </summary>
    public bool Take(LegionState legion)
    {
        if (legion.Character is not { } name)
        {
            return false;
        }

        if (For(name) is { } old)
        {
            legion.Name ??= old.Name;
            legion.Leader ??= old.Leader;
            legion.HallRentEndsMs ??= old.HallRentEndsMs;
            if (legion.Texts.Count == 0)
            {
                legion.Texts = old.Texts;
            }

            var members = old.Members.ToDictionary(m => m.Name);
            foreach (var member in legion.Members)
            {
                members[member.Name] = member;
            }

            legion.Members = members.Values.ToList();
        }

        _byName[name] = legion;
        return true;
    }
}
