using System.Globalization;
using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <summary>A saved fight as shown in lists.</summary>
public sealed record HistoryRow(HistoryEntry Entry)
{
    private MeterSnapshot S => Entry.Snapshot;

    public long StartedAtMs => S.StartedAtMs;
    public string When => DateTimeOffset.FromUnixTimeMilliseconds(S.StartedAtMs).ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
    public string Title => S.Title;
    public double Seconds => S.DurationSeconds;
    public string Duration => HistoryStore.Duration(S.DurationSeconds);
    public double TotalDpsValue => S.TotalDps;
    public string TotalDps => HistoryStore.Compact(S.TotalDps);
    public double YourDpsValue => S.Self?.Dps ?? 0;
    public string YourDps => S.Self is { } me ? HistoryStore.Compact(me.Dps) : "–";
    public int Players => S.Rows.Count;

    private DateTime Started => DateTimeOffset.FromUnixTimeMilliseconds(S.StartedAtMs).ToLocalTime().DateTime;

    /// <summary>List group: "Today", "Yesterday" or the date.</summary>
    public string Day => (DateTime.Today - Started.Date).Days switch
    {
        0 => "Today",
        1 => "Yesterday",
        _ => Started.ToString("ddd, dd MMM yyyy", CultureInfo.InvariantCulture),
    };

    public string Time => Started.ToString("HH:mm", CultureInfo.InvariantCulture);

    public string ShortDate => Started.ToString("dd MMM", CultureInfo.InvariantCulture);

    /// <summary>The boss or target without the "(+N)" suffix.</summary>
    public string Boss => HistoryStore.BossOf(S.Title);

    /// <summary>"+14" when the fight had more targets than the main one.</summary>
    public string Extra => S.Title.Length > Boss.Length ? S.Title[Boss.Length..].Trim().Trim('(', ')') : string.Empty;

    public string Party => Players <= 1 ? "Solo" : Players.ToString(CultureInfo.InvariantCulture);

    /// <summary>Own DPS in full ("6,279"), for lists and the detail pane.</summary>
    public string YourDpsFull => S.Self is { } me ? me.Dps.ToString("N0", CultureInfo.InvariantCulture) : "–";

    public bool IsWithin(TimeSpan age) => DateTime.Now - Started <= age;

    /// <summary>Our best fight per boss (see <see cref="HistoryStore.BestPerBoss"/>), as rows.</summary>
    public static List<HistoryRow> BestPerBoss(IEnumerable<HistoryRow> fights)
    {
        var rows = fights.ToDictionary(f => f.Entry);
        return HistoryStore.BestPerBoss(rows.Keys).Select(e => rows[e]).ToList();
    }
    public string Ended => S.End switch
    {
        EncounterEnd.BossKilled => "Boss killed",
        EncounterEnd.ZoneChange => "Zone change",
        EncounterEnd.Reset => "Reset",
        _ => "Idle",
    };
}
