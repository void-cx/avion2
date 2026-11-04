using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <summary>One attribute of the stat calculator: the points entered, what they give and what the change gives.</summary>
public sealed class StatInputRow(StatInput input) : INotifyPropertyChanged
{
    private string _value = "0";
    private string _effect = string.Empty;
    private string? _change;

    public StatInput Input { get; } = input;

    public string Name => Input.ShortName;

    public string Lord => Input.Lord is { } lord ? $"[{lord}]" : string.Empty;

    public string Tip => $"{Input.Name}: " + string.Join(", ", Input.Gives.Select(g => $"{g.Name} {StatRates.Percent(g.PerPoint)} per point"));

    public string Value
    {
        get => _value;
        set
        {
            if (_value != value)
            {
                _value = value;
                Changed(nameof(Value));
            }
        }
    }

    /// <summary>The points entered; 0 when the box holds no number.</summary>
    public int Points => int.TryParse(_value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int points) ? points : 0;

    /// <summary>"Double Chance +17.8% · MP Cost −17.8%".</summary>
    public string Effect
    {
        get => _effect;
        set
        {
            _effect = value;
            Changed(nameof(Effect));
        }
    }

    /// <summary>"+20 Wisdom → Double Chance +2%, MP Cost −2%" against the character's own value; null when the same.</summary>
    public string? Change
    {
        get => _change;
        set
        {
            _change = value;
            Changed(nameof(Change));
            Changed(nameof(ChangeVisibility));
        }
    }

    public Visibility ChangeVisibility => _change is null ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A line of the results: a group heading ("Offense") or a derived stat with its total and the difference.</summary>
public sealed record StatResultRow(string Name, Brush NameBrush, double NameSize, string Value, string Difference, Brush DifferenceBrush, Thickness Line);

/// <summary>
/// The Forge's stat calculator: the six attributes and the ten deity stats in, the derived stats they convert into
/// out (linear, from the game's per-point table). "Fill in from" takes the values of the character shown from the
/// official site's lookup, so each change shows what it gives against the character's own.
/// </summary>
public partial class StatCalculatorPanel : UserControl
{
    private readonly StatRates _rates = StatRates.Bundled;
    private readonly List<StatInputRow> _rows;
    private AppState? _state;
    private string? _baselineName;
    private IReadOnlyDictionary<string, int>? _baseline;
    private bool _filling;

    /// <summary>The derived stats by kind, in this order; what isn't listed goes under "Other".</summary>
    private static readonly (string Group, string[] Stats)[] Groups =
    [
        ("Offense", ["DamageRatio", "AccuracyRatio", "CriticalRatio", "HardHit", "Perfect", "IgnoreIronWall", "IgnoreRestoration"]),
        ("Defense", ["DefenseRatio", "EvasionRatio", "BlockRatio", "CriticalResistRatio", "IronWall", "HardHitResist", "PerfectResist", "Restoration"]),
        ("HP and MP", ["MaxHPRatio", "MaxMPRatio", "MPUseIncrease"]),
        ("Speed and cooldowns", ["CombatSpeed", "MoveSpeed", "CoolTimeIncrease"]),
        ("Status effects", ["AbnormalAccuracy", "AbnormalResistance"]),
    ];

    public StatCalculatorPanel()
    {
        InitializeComponent();
        _rows = _rates.Inputs.Select(i => new StatInputRow(i)).ToList();
        foreach (var row in _rows)
        {
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(StatInputRow.Value) && !_filling)
                {
                    Recalculate();
                }
            };
        }

        PrimaryList.ItemsSource = _rows.Where(r => !r.Input.IsDeity).ToList();
        DeityList.ItemsSource = _rows.Where(r => r.Input.IsDeity).ToList();
        SourceNote.Text = $"Per-point rates from the game data ({_rates.Built ?? "date unknown"}), the same the official site's character pages show.";
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                RefreshFill();
            }
        };
        Recalculate();
    }

    /// <summary>Follows the character shown, for "Fill in from".</summary>
    public void Attach(AppState state)
    {
        _state = state;
        state.ViewedCharacterChanged += RefreshFill;
        StreamerMode.Changed += () =>
        {
            RefreshFill();
            Recalculate();
        };
    }

    /// <summary>The character shown and its server, when the official site can look it up.</summary>
    private (string Name, ushort Server)? Viewed =>
        _state?.ViewedCharacter is { } name &&
        _state.OurCharacters.FirstOrDefault(c => c.Name == name) is { ServerId: not 0 } known &&
        ArmoryClient.RegionCode(known.ServerId) is not null
            ? (name, known.ServerId)
            : null;

    /// <summary>The "Fill in from" button and the line under it.</summary>
    private void RefreshFill()
    {
        if (_state is null || !IsVisible)
        {
            return;
        }

        if (Viewed is not { } viewed)
        {
            FillButton.Content = "Fill in from your character";
            FillButton.IsEnabled = false;
            FillNote.Text = _state.ViewedCharacter is null
                ? "Enter the values from the character window, or play once with the meter running: then AVION2 can take them from the official site."
                : "The official site has no characters on this server: enter the values from the character window.";
            return;
        }

        FillButton.IsEnabled = true;
        FillButton.Content = $"Fill in from {StreamerMode.Name(viewed.Name)}";
        FillNote.Text = _state.Armory.Cached(viewed.Name, viewed.Server) is { } cached
            ? $"Takes the values the official site showed at {cached.FetchedAt.ToLocalTime():HH:mm}, then shows what each change gives."
            : "Asks the official AION2 site for the character's values, then shows what each change gives.";
    }

    private async void Fill_Click(object sender, RoutedEventArgs e)
    {
        if (_state is null || Viewed is not { } viewed)
        {
            return;
        }

        FillButton.IsEnabled = false;
        FillNote.Text = "Asking the official AION2 site…";
        var lookup = await _state.Armory.LoadAsync(viewed.Name, viewed.Server);
        FillButton.IsEnabled = true;
        if (lookup.Profile is not { } profile)
        {
            FillNote.Text = lookup.Problem ?? "The official AION2 site did not answer. Try again in a moment.";
            return;
        }

        Fill(profile.Name, _rates.FromArmory(profile.Stats));
        FillNote.Text = $"The values of {StreamerMode.Name(profile.Name)} on the official site at {profile.FetchedAt.ToLocalTime():HH:mm}. " +
                        "Change any of them to see what it gives.";
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _baseline = null;
        _baselineName = null;
        Fill(null, new Dictionary<string, int>());
        RefreshFill();
    }

    /// <summary>Enters <paramref name="values"/>; with a name they are that character's own, which changes are shown against.</summary>
    internal void Fill(string? character, IReadOnlyDictionary<string, int> values)
    {
        _filling = true;
        foreach (var row in _rows)
        {
            row.Value = values.GetValueOrDefault(row.Input.Key).ToString(CultureInfo.InvariantCulture);
        }

        _filling = false;
        if (character is not null)
        {
            _baselineName = character;
            _baseline = values;
        }

        Recalculate();
    }

    /// <summary>
    /// Render shots: the character shown's values when the official site's answer is cached, else example values,
    /// then 20 more Wisdom and 10 more Death, so the changes show.
    /// </summary>
    internal void Demo()
    {
        RefreshFill();
        if (Viewed is { } viewed && _state?.Armory.Cached(viewed.Name, viewed.Server) is { } profile &&
            _rates.FromArmory(profile.Stats) is { Count: > 0 } own)
        {
            Fill(profile.Name, own);
        }
        else
        {
            Fill("Example", new Dictionary<string, int>
            {
                ["STR"] = 180, ["DEX"] = 70, ["INT"] = 40, ["CON"] = 60, ["AGI"] = 200, ["WIS"] = 90,
                ["Justice"] = 40, ["Destruction"] = 35, ["Freedom"] = 30, ["Death"] = 60, ["Illusion"] = 25,
                ["Wisdom"] = 120, ["Life"] = 30, ["Destiny"] = 20, ["Time"] = 45, ["Space"] = 15,
            });
        }

        Set("Wisdom", Values["Wisdom"] + 20);
        Set("Death", Values["Death"] + 10);
    }

    /// <summary>Sets one value as if typed (render shots).</summary>
    internal void Set(string key, int points)
    {
        if (_rows.FirstOrDefault(r => r.Input.Key == key) is { } row)
        {
            row.Value = points.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The values entered, by the game's key.</summary>
    internal IReadOnlyDictionary<string, int> Values => _rows.ToDictionary(r => r.Input.Key, r => r.Points);

    private void Recalculate()
    {
        var values = Values;
        foreach (var row in _rows)
        {
            var gives = _rates.Gives(row.Input.Key, row.Points);
            row.Effect = gives.Count > 0 ? string.Join(" · ", gives.Select(g => $"{g.Name} {StatRates.Percent(g.Value)}")) : string.Empty;
            row.Change = _baseline is null ? null : _rates.ChangeLine(row.Input.Key, _baseline.GetValueOrDefault(row.Input.Key), row.Points);
        }

        string? who = _baselineName is { } name ? StreamerMode.Name(name) : null;
        VersusHeader.Text = who is null ? string.Empty : $"vs. {who}";
        var lines = _baseline is null
            ? _rates.Convert(values).Select(d => new DerivedChange(d.Stat, d.Name, d.Value, d.Value)).ToList()
            : _rates.Compare(values, _baseline);
        ResultHeader.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        bool unchanged = _baseline is { } own && _rows.All(r => own.GetValueOrDefault(r.Input.Key) == r.Points);
        ResultIntro.Text = lines.Count == 0
            ? "Enter the values from the character window (or fill them in from your character) to see what they convert into."
            : who is null ? "The derived stats the values convert into."
            : unchanged ? $"What the values of {who} convert into. Change a value to see what it gives."
            : $"What the values entered convert into, and the difference to what {who}'s own give.";
        ResultList.ItemsSource = Rows(lines, _baseline is not null);
    }

    /// <summary>The lines under group headings; the difference column only against a character.</summary>
    private static List<StatResultRow> Rows(IReadOnlyList<DerivedChange> lines, bool versus)
    {
        var title = Resource("ChipTextBrush");
        var text = Resource("SecondaryTextBrush");
        var muted = Resource("MutedBrush");
        var up = Resource("LiveBrush");
        var down = Resource("WarnBrush");
        var rows = new List<StatResultRow>();
        var grouped = lines.GroupBy(l => Groups.FirstOrDefault(g => g.Stats.Contains(l.Stat)).Group ?? "Other")
            .OrderBy(g => Array.FindIndex(Groups, x => x.Group == g.Key) is var i and >= 0 ? i : Groups.Length);
        foreach (var group in grouped)
        {
            rows.Add(new StatResultRow(group.Key.ToUpperInvariant(), title, 11, string.Empty, string.Empty, muted,
                new Thickness(0, rows.Count == 0 ? 0 : 1, 0, 0)));
            foreach (var line in group.OrderBy(l => Order(l.Stat)))
            {
                double difference = line.Difference;
                // Lower is better for MP cost and cooldowns: colour by what the change means, not by its sign
                bool better = Lowers(line.Stat) ? difference < 0 : difference > 0;
                rows.Add(new StatResultRow(line.Name, text, 13, StatRates.Percent(line.Value),
                    !versus ? string.Empty : difference == 0 ? "—" : StatRates.Percent(difference),
                    difference == 0 ? muted : better ? up : down, new Thickness(0)));
            }
        }

        return rows;
    }

    private static int Order(string stat) =>
        Groups.Select(g => Array.IndexOf(g.Stats, stat)).FirstOrDefault(i => i >= 0, int.MaxValue);

    /// <summary>Stats where less is better (the table gives them negative rates).</summary>
    private static bool Lowers(string stat) => stat is "MPUseIncrease" or "CoolTimeIncrease";

    private static Brush Resource(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}
