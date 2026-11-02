using System.Text.Json;
using System.Text.Json.Serialization;
using Avion2.Analysis;
using Avion2.Protocol;

namespace Avion2.Engine;

/// <summary>
/// Names on the OBS overlay. Streamer mode wins (it hides everything, with the app's own stand-ins); otherwise
/// other players and our own name and legion are hidden on their own settings.
/// </summary>
public sealed class ObsNames(NameMask app)
{
    private readonly NameMask _own = new() { On = true };

    public bool HideOthers { get; set; } = true;

    public bool HideSelf { get; set; }

    public string Name(string name, bool self = false)
    {
        if (app.On)
        {
            return app.Name(name, self);
        }

        self = self || name == app.Self;
        if (self ? !HideSelf : !HideOthers)
        {
            return name;
        }

        _own.Self = app.Self;
        return _own.Name(name, self);
    }

    /// <summary>Our legion.</summary>
    public string? Legion(string? legion) =>
        app.On ? app.Legion(legion) : HideSelf && !string.IsNullOrEmpty(legion) ? "Legion" : legion;
}

/// <summary>Our character for the OBS character card (null fields aren't known yet).</summary>
public sealed record ObsCharacter(string Name, GameClass Class, string? Server, string? Legion, uint? CombatPower, string? Map, double? Hp);

/// <summary>This session so far, for the character card.</summary>
public sealed record ObsSession(int FightsToday, double? BestDpsToday, long? Kinah, int Runs);

/// <summary>Builds the JSON the OBS page polls (<c>/state</c>).</summary>
public static class ObsFeed
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <param name="running">The meter is capturing (false: the page says AVION2 isn't).</param>
    /// <param name="theme">The app's theme id, the page's default look.</param>
    public static string Build(bool running, string theme, MeterSnapshot? fight, ObsCharacter? me, ObsSession? session, ObsNames names)
    {
        object? Fight() => fight is null ? null : new
        {
            title = fight.Title,
            seconds = Math.Round(fight.DurationSeconds, 1),
            ended = fight.End != EncounterEnd.Ongoing,
            partyDps = Math.Round(fight.TotalDps),
            partyTaken = fight.DurationSeconds > 0 ? Math.Round(fight.Incoming.Sum(i => i.Total) / fight.DurationSeconds) : 0,
            hp = fight.TargetMaxHp is { } max && fight.TargetHp is { } hp && max > 0 ? Math.Round(Math.Clamp((double)hp / max, 0, 1), 4) : (double?)null,
            meter = fight.Rows.Select(r => new
            {
                name = names.Name(r.Name, r.IsSelf),
                cls = Class(r.Class),
                self = r.IsSelf,
                value = Math.Round(r.Dps),
                total = r.Total,
                share = Math.Round(r.Share, 4),
            }),
            taken = fight.Incoming.Select(r => new
            {
                name = names.Name(r.Name, r.IsSelf),
                cls = Class(r.Class),
                self = r.IsSelf,
                value = Math.Round(r.Dtps),
                total = r.Total,
                share = Math.Round(r.Share, 4),
            }),
        };

        var state = new
        {
            running,
            theme,
            fight = Fight(),
            me = me is null ? null : new
            {
                name = names.Name(me.Name, self: true),
                cls = Class(me.Class),
                server = me.Server,
                legion = names.Legion(me.Legion),
                cp = me.CombatPower,
                map = me.Map,
                hp = me.Hp is { } ratio ? Math.Round(ratio, 4) : (double?)null,
            },
            session = session is null ? null : new
            {
                fightsToday = session.FightsToday,
                bestDpsToday = session.BestDpsToday is { } best ? Math.Round(best) : (double?)null,
                kinah = session.Kinah,
                runs = session.Runs,
            },
        };
        return JsonSerializer.Serialize(state, Options);
    }

    /// <summary>"gladiator", "unknown", … (the page's colours and tags).</summary>
    private static string Class(GameClass cls) => cls.ToString().ToLowerInvariant();
}
