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
    public string Ended => S.End switch
    {
        EncounterEnd.BossKilled => "Boss killed",
        EncounterEnd.ZoneChange => "Zone change",
        EncounterEnd.Reset => "Reset",
        _ => "Idle",
    };
}
