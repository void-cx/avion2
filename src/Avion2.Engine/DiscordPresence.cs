using System.Globalization;
using Avion2.Protocol;

namespace Avion2.Engine;

/// <summary>
/// What our Discord profile shows (Discord Rich Presence): two lines of text, when the activity started, and the
/// keys of pictures uploaded as Rich Presence art to the AVION2 application in the Discord developer portal.
/// </summary>
public sealed record DiscordActivity(
    string? Details,
    string? State,
    long? StartMs,
    string LargeImage,
    string? LargeText,
    string? SmallImage = null,
    string? SmallText = null);

/// <summary>The dungeon run in progress, as far as the presence needs it.</summary>
/// <param name="BossesTotal">Bosses the built-in dungeon table lists for it; null when unknown.</param>
public sealed record PresenceRun(string Title, string Category, long StartMs, int BossesKilled, int? BossesTotal);

/// <summary>The boss we fight right now.</summary>
/// <param name="HpFraction">Its HP, 0..1; null when unknown.</param>
public sealed record PresenceBoss(string Name, double? HpFraction, long StartMs);

/// <summary>Gathering or crafting.</summary>
public enum PresenceTask
{
    None,
    Gathering,
    Crafting,
}

/// <summary>What AVION2 knows right now, gathered by the app for <see cref="DiscordPresence.Build"/>.</summary>
public sealed record PresenceInput
{
    /// <summary>The meter runs; without it the presence is cleared.</summary>
    public bool Capturing { get; init; }

    /// <summary>The map's name; null when the map isn't known (or has no name in the tables).</summary>
    public string? Map { get; init; }

    /// <summary>When we arrived on the map.</summary>
    public long? MapSinceMs { get; init; }

    public PresenceRun? Run { get; init; }

    public PresenceBoss? Boss { get; init; }

    /// <summary>A fight (not with a boss) is running, since when.</summary>
    public long? CombatSinceMs { get; init; }

    public PresenceTask Task { get; init; }

    public long? TaskSinceMs { get; init; }

    /// <summary>Members of our party, us included; 0 or 1 when not in one.</summary>
    public int PartySize { get; init; }

    /// <summary>Our character's name, when known for sure.</summary>
    public string? SelfName { get; init; }

    public GameClass SelfClass { get; init; }

    /// <summary>Our character's portrait on the official AION2 site (from a character lookup); null until looked up.</summary>
    public string? SelfPictureUrl { get; init; }
}

/// <summary>The privacy settings that apply to the presence.</summary>
/// <param name="ShowName">The user allowed our character's name (Settings, off by default).</param>
/// <param name="StreamerMode">Streamer mode is on: our name and picture never show, whatever the other options say.</param>
/// <param name="ShowPicture">The user allowed our character's portrait as the large picture (off by default).</param>
public sealed record PresenceOptions(bool ShowName = false, bool StreamerMode = false, bool ShowPicture = false);

/// <summary>
/// Turns what AVION2 knows into the text of our Discord status. Other players' names never appear (a party shows as
/// "Party of 5"); our own name only when the user allowed it and streamer mode is off.
/// </summary>
/// <remarks>
/// Rich Presence art of the AVION2 application (Discord developer portal, Rich Presence → Art Assets), by key:
/// <c>avion2</c> (the large picture, unless the user chose their character's portrait) and one small picture per class, named like the class in lower
/// case: <c>gladiator</c>, <c>templar</c>, <c>ranger</c>, <c>assassin</c>, <c>elementalist</c>, <c>sorcerer</c>,
/// <c>cleric</c>, <c>chanter</c>, <c>fighter</c>.
/// </remarks>
public static class DiscordPresence
{
    /// <summary>AVION2's Discord application id (public, not a secret).</summary>
    public const string ApplicationId = "1567454515794542642";

    /// <summary>The large picture's key.</summary>
    public const string LargeImageKey = "avion2";

    /// <summary>Discord refuses texts shorter than 2 or longer than 128 characters.</summary>
    private const int MaxText = 128;

    /// <summary>Discord takes an image address instead of an art key up to this length.</summary>
    private const int MaxImage = 256;

    /// <summary>
    /// A portrait address we pass on to Discord: only the official site's own (https, a plaync.com host), as the
    /// character lookup gives it.
    /// </summary>
    public static bool IsPictureUrl(string? url) =>
        url is { Length: <= MaxImage } && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host == "plaync.com" || uri.Host.EndsWith(".plaync.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>An application id is a Discord snowflake: a number of 17 to 20 digits.</summary>
    public static bool IsApplicationId(string? id) =>
        id is { Length: >= 17 and <= 20 } && id.All(char.IsAsciiDigit);

    /// <summary>The small picture's key for a class ("templar"); null when the class is unknown.</summary>
    public static string? ClassImageKey(GameClass cls) =>
        cls == GameClass.Unknown ? null : cls.ToString().ToLowerInvariant();

    /// <summary>The activity to show; null (nothing shown) while the meter isn't running.</summary>
    public static DiscordActivity? Build(PresenceInput input, PresenceOptions options)
    {
        if (!input.Capturing)
        {
            return null;
        }

        // our own name, last on the second line, only when allowed
        string? name = options.ShowName && !options.StreamerMode ? input.SelfName : null;
        string? party = input.PartySize >= 2 ? string.Create(CultureInfo.InvariantCulture, $"Party of {input.PartySize}") : null;
        string? details;
        string? state;
        long? start;
        if (input.Boss is { } boss)
        {
            details = $"Fighting {boss.Name}";
            string? hp = boss.HpFraction is { } f ? string.Create(CultureInfo.InvariantCulture, $"{Math.Clamp(f, 0, 1) * 100:0}% HP") : null;
            state = Join(hp, input.Run?.Title ?? input.Map, party, name);
            start = input.Run?.StartMs ?? boss.StartMs; // inside a run its clock keeps running through the fights
        }
        else if (input.Run is { } run)
        {
            details = run.Title;
            state = Join(Bosses(run) ?? run.Category, party, name);
            start = run.StartMs;
        }
        else if (input.Task != PresenceTask.None)
        {
            details = input.Task == PresenceTask.Gathering ? "Gathering" : "Crafting";
            state = Join(input.Map, party, name);
            start = input.TaskSinceMs;
        }
        else if (input.CombatSinceMs is { } combat)
        {
            details = "In combat";
            state = Join(input.Map, party, name);
            start = combat;
        }
        else
        {
            details = input.Map ?? "Idle";
            state = Join(input.Map is null ? null : "Idle", party, name);
            start = input.Map is null ? null : input.MapSinceMs;
        }

        string? classKey = ClassImageKey(input.SelfClass);
        string large = options.ShowPicture && !options.StreamerMode && IsPictureUrl(input.SelfPictureUrl)
            ? input.SelfPictureUrl!
            : LargeImageKey;
        return new DiscordActivity(
            Clip(details),
            Clip(state),
            start is > 0 ? start : null,
            large,
            "AVION2",
            classKey,
            classKey is null ? null : input.SelfClass.ToString());
    }

    /// <summary>"2 / 4 bosses" when the dungeon's bosses are known, else "2 bosses"; null before the first.</summary>
    private static string? Bosses(PresenceRun run)
    {
        if (run.BossesTotal is { } total && total > 0 && total >= run.BossesKilled)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{run.BossesKilled} / {total} bosses");
        }

        return run.BossesKilled switch
        {
            0 => null,
            1 => "1 boss",
            _ => string.Create(CultureInfo.InvariantCulture, $"{run.BossesKilled} bosses"),
        };
    }

    private static string? Join(params string?[] parts)
    {
        var shown = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return shown.Count == 0 ? null : string.Join(" · ", shown);
    }

    /// <summary>Fits a text into Discord's limits; null for one too short to send.</summary>
    private static string? Clip(string? text)
    {
        text = text?.Trim();
        if (text is null || text.Length < 2)
        {
            return null;
        }

        return text.Length <= MaxText ? text : text[..(MaxText - 1)] + "…";
    }
}

/// <summary>
/// Sends the presence at most once per interval (15 s by default) and only when it changed: a change in between
/// waits for a later check. Discord itself allows 5 updates per 20 s.
/// </summary>
public sealed class PresenceThrottle(long intervalMs = 15_000)
{
    private DiscordActivity? _last;
    private bool _sent;
    private long _sentAtMs;

    /// <summary>True when <paramref name="activity"/> should be sent now (it then counts as sent).</summary>
    public bool ShouldSend(DiscordActivity? activity, long nowMs)
    {
        if (_sent && (Equals(activity, _last) || nowMs - _sentAtMs < intervalMs))
        {
            return false;
        }

        _last = activity;
        _sent = true;
        _sentAtMs = nowMs;
        return true;
    }

    /// <summary>Forgets what was sent, so the next check sends at once (a new connection, changed privacy settings).</summary>
    public void Reset()
    {
        _sent = false;
        _last = null;
    }
}
