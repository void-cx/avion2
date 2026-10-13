using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avion2.Analysis;
using Avion2.Engine;
using Avion2.Protocol;

namespace Avion2.Overlay;

/// <summary>Base for small bindable objects.</summary>
public abstract class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record PlayerOption(uint ActorId, string Label);

public sealed record StatTile(string Label, string Value, string Tone = "");

/// <summary>One row of the skill table; updated in place so sorting and scrolling survive refreshes.</summary>
public sealed class SkillLine(uint skillId) : Bindable
{
    private string _name = string.Empty;
    private IReadOnlyList<bool> _spec = [];
    private int _hits;
    private double _crit;
    private double _back;
    private double _double;
    private double _perfect;
    private double _parry;
    private double _multi;
    private long _multiDamage;
    private double _average;
    private long _min;
    private long _max;
    private long _total;
    private double _share;

    public uint SkillId { get; } = skillId;
    public string Name { get => _name; set => Set(ref _name, value); }
    public IReadOnlyList<bool> Spec { get => _spec; set => Set(ref _spec, value); }
    public int Hits { get => _hits; set => Set(ref _hits, value); }
    public double Crit { get => _crit; set => Set(ref _crit, value); }
    public double Back { get => _back; set => Set(ref _back, value); }
    public double Double { get => _double; set => Set(ref _double, value); }
    public double Perfect { get => _perfect; set => Set(ref _perfect, value); }
    public double Parry { get => _parry; set => Set(ref _parry, value); }
    public double Multi { get => _multi; set => Set(ref _multi, value); }
    public long MultiDamage { get => _multiDamage; set => Set(ref _multiDamage, value); }
    public double Average { get => _average; set => Set(ref _average, value); }
    public long Min { get => _min; set => Set(ref _min, value); }
    public long Max { get => _max; set => Set(ref _max, value); }
    public long Total { get => _total; set => Set(ref _total, value); }
    public double Share { get => _share; set => Set(ref _share, value); }
}

/// <summary>One row of the damage-taken table.</summary>
public sealed class SourceLine(string key) : Bindable
{
    private string _attacker = string.Empty;
    private string _skill = string.Empty;
    private int _hits;
    private int _crits;
    private double _average;
    private long _max;
    private long _total;
    private double _share;

    public string Key { get; } = key;
    public string Attacker { get => _attacker; set => Set(ref _attacker, value); }
    public string Skill { get => _skill; set => Set(ref _skill, value); }
    public int Hits { get => _hits; set => Set(ref _hits, value); }
    public int Crits { get => _crits; set => Set(ref _crits, value); }
    public double Average { get => _average; set => Set(ref _average, value); }
    public long Max { get => _max; set => Set(ref _max, value); }
    public long Total { get => _total; set => Set(ref _total, value); }
    public double Share { get => _share; set => Set(ref _share, value); }
}

/// <summary>
/// The detail window: one player's full breakdown of one encounter, live or from history.
/// </summary>
public sealed class DetailViewModel : Bindable
{
    private string _target = string.Empty;
    private string _source = string.Empty;
    private IReadOnlyList<PlayerOption> _players = [];
    private PlayerOption? _selectedPlayer;
    private IReadOnlyList<StatTile> _tiles = [];
    private IReadOnlyList<StatTile> _takenTiles = [];
    private int _tabIndex;
    private bool _hasPlayer;

    public string Target { get => _target; private set => Set(ref _target, value); }

    /// <summary>"Live" or the time of a saved fight.</summary>
    public string Source { get => _source; private set => Set(ref _source, value); }

    public IReadOnlyList<PlayerOption> Players { get => _players; private set => Set(ref _players, value); }

    public PlayerOption? SelectedPlayer
    {
        get => _selectedPlayer;
        set
        {
            if (Set(ref _selectedPlayer, value) && value is not null)
            {
                SelectedActorId = value.ActorId;
                Skills.Clear();
                Sources.Clear();
            }
        }
    }

    public uint? SelectedActorId { get; set; }

    public bool HasPlayer { get => _hasPlayer; private set => Set(ref _hasPlayer, value); }

    /// <summary>0 = damage dealt, 1 = damage taken.</summary>
    public int TabIndex { get => _tabIndex; set => Set(ref _tabIndex, value); }

    public IReadOnlyList<StatTile> Tiles { get => _tiles; private set => Set(ref _tiles, value); }
    public IReadOnlyList<StatTile> TakenTiles { get => _takenTiles; private set => Set(ref _takenTiles, value); }

    public ObservableCollection<SkillLine> Skills { get; } = new();
    public ObservableCollection<SourceLine> Sources { get; } = new();

    public void Update(MeterSnapshot? snapshot, string source)
    {
        Source = source;
        if (snapshot is null)
        {
            Target = "No encounter yet";
            Players = [];
            HasPlayer = false;
            Tiles = [];
            TakenTiles = [];
            Skills.Clear();
            Sources.Clear();
            return;
        }

        Target = snapshot.Title;

        // Everyone who dealt or took damage can be inspected.
        var options = snapshot.Rows.Select(r => new PlayerOption(r.ActorId, Label(r.Name, r.Class, r.IsSelf)))
            .Concat(snapshot.Incoming.Where(i => snapshot.Rows.All(r => r.ActorId != i.VictimId))
                .Select(i => new PlayerOption(i.VictimId, Label(i.Name, i.Class, i.IsSelf))))
            .ToList();
        if (!options.SequenceEqual(_players))
        {
            Players = options;
        }

        uint? wanted = SelectedActorId
            ?? snapshot.Self?.ActorId
            ?? snapshot.Incoming.FirstOrDefault(i => i.IsSelf)?.VictimId
            ?? snapshot.Rows.FirstOrDefault()?.ActorId;
        var selected = options.FirstOrDefault(o => o.ActorId == wanted) ?? options.FirstOrDefault();
        if (_selectedPlayer?.ActorId != selected?.ActorId || !ReferenceEquals(_selectedPlayer, selected))
        {
            _selectedPlayer = selected;
            OnPropertyChanged(nameof(SelectedPlayer));
        }

        SelectedActorId = selected?.ActorId;
        HasPlayer = selected is not null;

        UpdateDealt(snapshot, snapshot.Rows.FirstOrDefault(r => r.ActorId == SelectedActorId));
        UpdateTaken(snapshot, snapshot.Incoming.FirstOrDefault(i => i.VictimId == SelectedActorId));
    }

    private void UpdateDealt(MeterSnapshot snapshot, MeterRow? row)
    {
        if (row is null)
        {
            Tiles = [new StatTile("Damage", "0"), new StatTile("Duration", HistoryStore.Duration(snapshot.DurationSeconds))];
            Skills.Clear();
            return;
        }

        Tiles =
        [
            new StatTile("Total damage", row.Total.ToString("N0", CultureInfo.InvariantCulture)),
            new StatTile("DPS", row.Dps.ToString("N0", CultureInfo.InvariantCulture), "accent"),
            new StatTile("Duration", HistoryStore.Duration(snapshot.DurationSeconds)),
            new StatTile("Hits", row.Hits.ToString(CultureInfo.InvariantCulture)),
            new StatTile("Share", Percent(row.Share)),
            new StatTile("Critical", Percent(row.CritRate), "crit"),
            new StatTile("Back attack", Percent(row.BackRate), "back"),
            new StatTile("Double", Percent(row.DoubleRate), "double"),
            new StatTile("Perfect", Percent(row.PerfectRate), "perfect"),
            new StatTile("Parried", Percent(row.ParryRate)),
            new StatTile("Multi-hit", Percent(row.MultiHitRate)),
        ];

        var seen = new HashSet<uint>();
        foreach (var skill in row.Skills)
        {
            seen.Add(skill.SkillId);
            var line = Skills.FirstOrDefault(l => l.SkillId == skill.SkillId);
            if (line is null)
            {
                line = new SkillLine(skill.SkillId);
                Skills.Add(line);
            }

            line.Name = skill.IsOverTime ? skill.Name + " (DoT)" : skill.Name;
            line.Spec = SpecDots(skill.SpecSlots);
            line.Hits = skill.Hits;
            line.Crit = skill.CritRate;
            line.Back = skill.BackRate;
            line.Double = skill.DoubleRate;
            line.Perfect = skill.PerfectRate;
            line.Parry = skill.ParryRate;
            line.Multi = skill.MultiHitRate;
            line.MultiDamage = skill.MultiHitDamage;
            line.Average = skill.AverageHit;
            line.Min = skill.MinHit;
            line.Max = skill.MaxHit;
            line.Total = skill.Total;
            line.Share = skill.Share;
        }

        foreach (var stale in Skills.Where(l => !seen.Contains(l.SkillId)).ToList())
        {
            Skills.Remove(stale);
        }
    }

    private void UpdateTaken(MeterSnapshot snapshot, IncomingRow? row)
    {
        if (row is null)
        {
            TakenTiles = [new StatTile("Damage taken", "0")];
            Sources.Clear();
            return;
        }

        TakenTiles =
        [
            new StatTile("Damage taken", row.Total.ToString("N0", CultureInfo.InvariantCulture), "hp"),
            new StatTile("Taken / s", row.Dtps.ToString("N0", CultureInfo.InvariantCulture)),
            new StatTile("Hits taken", row.Hits.ToString(CultureInfo.InvariantCulture)),
            new StatTile("Crits taken", row.Crits.ToString(CultureInfo.InvariantCulture), "crit"),
            new StatTile("Parried", row.Parried.ToString(CultureInfo.InvariantCulture)),
            new StatTile("Blocked", (row.Blocked + row.PerfectBlocks).ToString(CultureInfo.InvariantCulture)),
            new StatTile("Shield hits", row.Shielded.ToString(CultureInfo.InvariantCulture)),
            new StatTile("Absorbed", row.Absorbed.ToString("N0", CultureInfo.InvariantCulture), "perfect"),
            new StatTile("Missed", row.Misses.ToString(CultureInfo.InvariantCulture)),
        ];

        var seen = new HashSet<string>();
        foreach (var source in row.Sources)
        {
            string key = source.Attacker + "\u0001" + source.Skill;
            seen.Add(key);
            var line = Sources.FirstOrDefault(l => l.Key == key);
            if (line is null)
            {
                line = new SourceLine(key);
                Sources.Add(line);
            }

            line.Attacker = source.Attacker;
            line.Skill = source.Skill;
            line.Hits = source.Hits;
            line.Crits = source.Crits;
            line.Average = source.Hits == 0 ? 0 : (double)source.Total / source.Hits;
            line.Max = source.MaxHit;
            line.Total = source.Total;
            line.Share = source.Share;
        }

        foreach (var stale in Sources.Where(l => !seen.Contains(l.Key)).ToList())
        {
            Sources.Remove(stale);
        }
    }

    private static IReadOnlyList<bool> SpecDots(int slots) =>
        Enumerable.Range(1, 5).Select(slot => (slots & (1 << slot)) != 0).ToList();

    private static string Label(string name, GameClass cls, bool isSelf) =>
        $"{name}{(cls == GameClass.Unknown ? string.Empty : $" · {cls}")}{(isSelf ? " (you)" : string.Empty)}";

    private static string Percent(double ratio) => (ratio * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
}
