using System.Globalization;
using System.Text;
using Avion2.Analysis;

namespace Avion2.Cli;

internal static class ConsoleMeter
{
    public static string Format(long value) => Format((double)value);

    public static string Format(double value) => value switch
    {
        >= 1_000_000_000 => (value / 1_000_000_000).ToString("0.00", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (value / 1_000_000).ToString("0.00", CultureInfo.InvariantCulture) + "M",
        >= 10_000 => (value / 1_000).ToString("0.0", CultureInfo.InvariantCulture) + "K",
        _ => value.ToString("0", CultureInfo.InvariantCulture),
    };

    public static string Percent(double ratio) => (ratio * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    public static string Duration(double seconds) => TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");

    public static string Render(MeterSnapshot? snapshot, bool live, int maxRows = 12, int maxSkills = 8)
    {
        var sb = new StringBuilder();
        if (snapshot is null)
        {
            sb.AppendLine("Waiting for combat…");
            return sb.ToString();
        }

        string hp = snapshot.TargetMaxHp is { } max && snapshot.TargetHp is { } cur
            ? $"   HP {Percent((double)cur / max)}"
            : string.Empty;
        sb.AppendLine($"{(live ? "▶" : "■")} #{snapshot.EncounterId} {snapshot.Title}   {Duration(snapshot.DurationSeconds)}   " +
                      $"Total {Format(snapshot.TotalDamage)}  ({Format(snapshot.TotalDps)}/s){hp}");
        sb.AppendLine();
        sb.AppendLine($"{"#",2}  {"Name",-18} {"Class",-12} {"DPS",8} {"Damage",9} {"Share",6} {"Crit",6} {"Back",6} {"Hits",6}");

        int rank = 0;
        foreach (var row in snapshot.Rows.Take(maxRows))
        {
            rank++;
            string name = Truncate(row.Name + (row.IsSelf ? " *" : string.Empty), 18);
            string cls = row.Class == Protocol.GameClass.Unknown ? "-" : row.Class.ToString();
            sb.AppendLine($"{rank,2}  {name,-18} {cls,-12} {Format(row.Dps),8} {Format(row.Total),9} {Percent(row.Share),6} " +
                          $"{Percent(row.CritRate),6} {Percent(row.BackRate),6} {row.Hits,6}");
        }

        var focus = snapshot.Rows.FirstOrDefault(r => r.IsSelf) ?? snapshot.Rows.FirstOrDefault();
        if (focus is not null && focus.Skills.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Skills — {focus.Name}");
            sb.AppendLine($"    {"Skill",-28} {"Damage",9} {"Share",6} {"Hits",5} {"Crit",6} {"Max",8}");
            foreach (var skill in focus.Skills.Take(maxSkills))
            {
                string name = Truncate(skill.Name + (skill.IsOverTime ? " (DoT)" : string.Empty), 28);
                sb.AppendLine($"    {name,-28} {Format(skill.Total),9} {Percent(skill.Share),6} {skill.Hits,5} " +
                              $"{(skill.IsOverTime ? "-" : Percent(skill.CritRate)),6} {Format(skill.MaxHit),8}");
            }
        }

        return sb.ToString();
    }

    private static string Truncate(string value, int width) => value.Length <= width ? value : value[..(width - 1)] + "…";
}
