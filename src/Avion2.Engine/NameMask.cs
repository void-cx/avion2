namespace Avion2.Engine;

/// <summary>
/// Streamer mode's stand-ins for real names: our character is "You", every other player "Player 2", "Player 3", …
/// (the same name always gets the same number), every legion "Legion". Only what is shown changes, never what is
/// saved.
/// </summary>
public sealed class NameMask
{
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);

    public bool On { get; set; }

    /// <summary>Our character's name; it shows as "You".</summary>
    public string? Self { get; set; }

    public string Name(string name, bool self = false)
    {
        if (!On || name.Length == 0)
        {
            return name;
        }

        if (self || name == Self)
        {
            return "You";
        }

        if (!_aliases.TryGetValue(name, out var alias))
        {
            _aliases[name] = alias = $"Player {_aliases.Count + 2}";
        }

        return alias;
    }

    public string? Legion(string? legion) => On && !string.IsNullOrEmpty(legion) ? "Legion" : legion;

    private readonly Dictionary<string, string> _legions = new(StringComparer.Ordinal);

    /// <summary>
    /// For lists of many legions (the Legions page): our legion is "Your legion", every other one "Legion A", "Legion B",
    /// … (the same legion always gets the same letter).
    /// </summary>
    public string LegionInList(string legion, bool own = false)
    {
        if (!On || legion.Length == 0)
        {
            return legion;
        }

        if (own)
        {
            return "Your legion";
        }

        if (!_legions.TryGetValue(legion, out var alias))
        {
            _legions[legion] = alias = $"Legion {Letters(_legions.Count)}";
        }

        return alias;
    }

    /// <summary>A, B, … Z, AA, AB, …</summary>
    private static string Letters(int index) => index < 26 ? ((char)('A' + index)).ToString() : Letters(index / 26 - 1) + (char)('A' + index % 26);
}
