using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Avion2.Engine;

/// <summary>
/// What the OBS page shows, as chosen in Settings; <see cref="Url"/> turns it into the address to paste into OBS
/// (only the options that differ from the page's defaults).
/// </summary>
public sealed class ObsPage
{
    /// <summary>"meter", "taken", "character", "character,meter" or "character,taken".</summary>
    public string View { get; set; } = "meter";

    public int Rows { get; set; } = 8;

    public double Scale { get; set; } = 1;

    /// <summary>Fight title, timer and party DPS above the rows.</summary>
    public bool Header { get; set; } = true;

    /// <summary>48.2K instead of 48,213.</summary>
    public bool Compact { get; set; }

    /// <summary>Today's fights and this session's kinah and runs under the character card.</summary>
    public bool Session { get; set; }

    /// <summary>The character card on one line.</summary>
    public bool Line { get; set; }

    /// <summary>"aion2" or "void"; null = the app's theme.</summary>
    public string? Theme { get; set; }

    [JsonIgnore]
    public bool ShowsMeter => View.Contains("meter", StringComparison.Ordinal) || View.Contains("taken", StringComparison.Ordinal);

    [JsonIgnore]
    public bool ShowsCharacter => View.Contains("character", StringComparison.Ordinal);

    public string Url(int port)
    {
        var query = new List<string>();
        if (View != "meter")
        {
            query.Add($"view={View}");
        }

        if (ShowsMeter)
        {
            if (Rows != 8)
            {
                query.Add(FormattableString.Invariant($"rows={Rows}"));
            }

            if (!Header)
            {
                query.Add("header=0");
            }

            if (Compact)
            {
                query.Add("compact=1");
            }
        }

        if (ShowsCharacter)
        {
            if (Line)
            {
                query.Add("style=line");
            }

            if (Session)
            {
                query.Add("session=1");
            }
        }

        if (Math.Abs(Scale - 1) > 0.001)
        {
            query.Add("scale=" + Scale.ToString("0.##", CultureInfo.InvariantCulture));
        }

        if (Theme is { Length: > 0 } theme)
        {
            query.Add($"theme={theme}");
        }

        var url = new StringBuilder($"http://localhost:{port}/overlay");
        if (query.Count > 0)
        {
            url.Append('?').AppendJoin('&', query);
        }

        return url.ToString();
    }

    /// <summary>About the Browser Source size the page needs (OBS crops what doesn't fit, so it errs larger).</summary>
    public (int Width, int Height) SuggestedSize()
    {
        double height = 10;
        if (ShowsCharacter)
        {
            height += Line ? 32 : 112;
            height += Session ? 84 : 0;
        }

        if (ShowsMeter)
        {
            height += (Header ? 78 : 0) + Rows * 29 + 8; // the header includes room for the boss cast row
        }

        return (RoundUp(320 * Scale), RoundUp(height * Scale));
    }

    private static int RoundUp(double value) => (int)Math.Ceiling(value / 10) * 10;
}
