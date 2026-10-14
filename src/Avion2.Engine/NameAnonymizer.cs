using Avion2.Protocol;

namespace Avion2.Engine;

/// <summary>
/// Developer aid for shareable screenshots: rewrites decoded events so no real player or legion name reaches the
/// session — our character becomes "You", others "Player 2", "Player 3", … (stable per name); legion tags read
/// "Legion"; the legion card and roster are dropped. Returns null for events to drop.
/// </summary>
public sealed class NameAnonymizer
{
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private string? _self;

    public GameEvent? Rewrite(GameEvent e) => e switch
    {
        SelfIdentityEvent self => self with { Name = Self(self.Name) },
        PlayerSpawnEvent player => player with { Name = Alias(player.Name) },
        NpcSpawnEvent { InlineName: { } owner } npc => npc with { InlineName = Alias(owner) },
        LegionTagEvent tag => tag with { Legion = "Legion" },
        LegionDetailsEvent or LegionRosterEvent => null,
        _ => e,
    };

    private string Self(string name)
    {
        _self = name;
        _aliases[name] = "You";
        return "You";
    }

    private string Alias(string name)
    {
        if (name == _self)
        {
            return "You";
        }

        if (!_aliases.TryGetValue(name, out var alias))
        {
            _aliases[name] = alias = $"Player {_aliases.Count(a => a.Value != "You") + 2}";
        }

        return alias;
    }
}
