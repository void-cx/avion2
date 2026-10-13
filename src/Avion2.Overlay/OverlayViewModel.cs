using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Avion2.Analysis;
using Avion2.Engine;
using Avion2.Protocol;

namespace Avion2.Overlay;

public enum OverlayTab
{
    Damage,
    Taken,
}

public sealed record RowViewModel(
    uint ActorId,
    int Rank,
    string Name,
    string Dps,
    string Total,
    string Share,
    string Detail,
    double Ratio,
    Brush BarBrush,
    bool IsSelf);

/// <summary>
/// Bindable state for the compact overlay (bars only) plus the user settings that the Settings window edits.
/// </summary>
public sealed class OverlayViewModel : Bindable
{
    private static readonly Dictionary<GameClass, Color> ClassColors = new()
    {
        [GameClass.Gladiator] = Color.FromRgb(0xC8, 0x7B, 0x3E),
        [GameClass.Templar] = Color.FromRgb(0xD4, 0xB2, 0x4A),
        [GameClass.Ranger] = Color.FromRgb(0x6B, 0xBF, 0x59),
        [GameClass.Assassin] = Color.FromRgb(0x9B, 0x59, 0xB6),
        [GameClass.Elementalist] = Color.FromRgb(0x3F, 0xA7, 0xD6),
        [GameClass.Sorcerer] = Color.FromRgb(0x5C, 0x6B, 0xC0),
        [GameClass.Cleric] = Color.FromRgb(0xDD, 0xDD, 0xDD),
        [GameClass.Chanter] = Color.FromRgb(0x4D, 0xB6, 0xAC),
        [GameClass.Fighter] = Color.FromRgb(0xE5, 0x73, 0x73),
        [GameClass.Unknown] = Color.FromRgb(0x80, 0x88, 0x90),
    };

    private static readonly Dictionary<GameClass, Brush> BarBrushes = ClassColors.ToDictionary(
        kv => kv.Key,
        kv => (Brush)Freeze(new SolidColorBrush(Color.FromArgb(0x99, kv.Value.R, kv.Value.G, kv.Value.B))));

    private string _title = "AVION2";
    private string _duration = "--:--";
    private string _totalDps = string.Empty;
    private string _status = "Starting…";
    private string _footer = string.Empty;
    private string _selfHp = string.Empty;
    private bool _hasHp;
    private double _hpRatio;
    private string _hpText = string.Empty;
    private bool _allTargets;
    private bool _locked;
    private bool _lowHpAlert = true;
    private double _alertVolume = 0.6;
    private double _lowHpThreshold = 0.3;
    private string? _lowHpSound;
    private bool _autoStart;
    private bool _alerting;
    private OverlayTab _tab;
    private IReadOnlyList<RowViewModel> _rows = [];
    private double _backgroundOpacity = 0.78;

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Duration { get => _duration; private set => Set(ref _duration, value); }
    public string TotalDps { get => _totalDps; private set => Set(ref _totalDps, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Footer { get => _footer; private set => Set(ref _footer, value); }
    public string SelfHp { get => _selfHp; private set => Set(ref _selfHp, value); }
    public bool HasHp { get => _hasHp; private set => Set(ref _hasHp, value); }
    public double HpRatio { get => _hpRatio; private set => Set(ref _hpRatio, value); }
    public string HpText { get => _hpText; private set => Set(ref _hpText, value); }
    public IReadOnlyList<RowViewModel> Rows { get => _rows; private set => Set(ref _rows, value); }
    public bool HasRows => _rows.Count > 0;

    /// <summary>Briefly true after a low-HP alert; drives the red border.</summary>
    public bool Alerting { get => _alerting; set => Set(ref _alerting, value); }

    public OverlayTab Tab
    {
        get => _tab;
        set
        {
            if (Set(ref _tab, value))
            {
                OnPropertyChanged(nameof(IsDamageTab));
                OnPropertyChanged(nameof(IsTakenTab));
            }
        }
    }

    public bool IsDamageTab => _tab == OverlayTab.Damage;
    public bool IsTakenTab => _tab == OverlayTab.Taken;

    public bool AllTargets
    {
        get => _allTargets;
        set
        {
            if (Set(ref _allTargets, value))
            {
                OnPropertyChanged(nameof(ScopeGlyph));
                OnPropertyChanged(nameof(ScopeTooltip));
            }
        }
    }

    public bool Locked
    {
        get => _locked;
        set
        {
            if (Set(ref _locked, value))
            {
                OnPropertyChanged(nameof(LockGlyph));
            }
        }
    }

    public bool LowHpAlert
    {
        get => _lowHpAlert;
        set
        {
            if (Set(ref _lowHpAlert, value))
            {
                OnPropertyChanged(nameof(AlertGlyph));
                OnPropertyChanged(nameof(AlertTooltip));
            }
        }
    }

    /// <summary>0..1.</summary>
    public double AlertVolume
    {
        get => _alertVolume;
        set
        {
            if (Set(ref _alertVolume, Math.Round(Math.Clamp(value, 0, 1), 2)))
            {
                OnPropertyChanged(nameof(AlertTooltip));
            }
        }
    }

    /// <summary>Fraction of max HP below which the alert fires.</summary>
    public double LowHpThreshold
    {
        get => _lowHpThreshold;
        set => Set(ref _lowHpThreshold, Math.Round(Math.Clamp(value, 0.05, 0.9), 2));
    }

    /// <summary>Custom .wav/.mp3; null = built-in chime.</summary>
    public string? LowHpSound { get => _lowHpSound; set => Set(ref _lowHpSound, value); }

    public bool AutoStart { get => _autoStart; set => Set(ref _autoStart, value); }

    public double BackgroundOpacity
    {
        get => _backgroundOpacity;
        set => Set(ref _backgroundOpacity, Math.Round(Math.Clamp(value, 0.2, 1.0), 2));
    }

    // Segoe Fluent Icons
    public string ScopeGlyph => _allTargets ? "" : "";
    public string ScopeTooltip => _allTargets ? "Showing all targets nearby (click: only my targets)" : "Showing my targets (click: all targets)";
    public string LockGlyph => _locked ? "" : "";
    public string AlertGlyph => _lowHpAlert ? "" : "";
    public string AlertTooltip => Invariant($"Low-HP sound {(_lowHpAlert ? "on" : "off")} · volume {_alertVolume * 100:0}%\nClick: {(_lowHpAlert ? "mute" : "enable")} · Scroll: volume");

    public MeterScope Scope => _allTargets ? MeterScope.AllTargets : MeterScope.MyTargets;

    public void Update(MeterSnapshot? snapshot, EngineStatus status)
    {
        Status = status.Locked ? (status.InCombat ? "LIVE" : "IDLE") : "SEARCHING";
        string self = status.SelfName is { } name
            ? $"{name} · {status.SelfClass}" + (status.SelfNameIsGuess ? " (last session)" : string.Empty)
            : status.SelfClass != GameClass.Unknown ? $"You · {status.SelfClass} (teleport once to show your name)"
            : "Waiting for your character…";
        Footer = status.Locked ? self : status.Message;
        SelfHp = status.SelfHp > 0 && status.SelfMaxHp > 0
            ? Invariant($"HP {Math.Min(1.0, (double)status.SelfHp / status.SelfMaxHp) * 100:0}%")
            : string.Empty;

        UpdateHeader(snapshot);
        if (snapshot is null)
        {
            Rows = [];
        }
        else if (_tab == OverlayTab.Taken)
        {
            UpdateTaken(snapshot);
        }
        else
        {
            UpdateDamage(snapshot);
        }

        OnPropertyChanged(nameof(HasRows));
    }

    private void UpdateHeader(MeterSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            Title = "Waiting for combat…";
            Duration = "--:--";
            TotalDps = string.Empty;
            HasHp = false;
            return;
        }

        Title = snapshot.Title;
        Duration = HistoryStore.Duration(snapshot.DurationSeconds);
        TotalDps = _tab == OverlayTab.Taken
            ? $"{Format(snapshot.Incoming.Sum(i => i.Total) / snapshot.DurationSeconds)}/s taken"
            : $"{Format(snapshot.TotalDps)}/s";

        if (snapshot.TargetMaxHp is { } max && snapshot.TargetHp is { } hp && max > 0)
        {
            HasHp = true;
            HpRatio = Math.Clamp((double)hp / max, 0, 1);
            HpText = Invariant($"{HpRatio * 100:0.0}%");
        }
        else
        {
            HasHp = false;
        }
    }

    private void UpdateDamage(MeterSnapshot snapshot)
    {
        double top = snapshot.Rows.Count > 0 ? Math.Max(snapshot.Rows[0].Total, 1) : 1;
        int rank = 0;
        Rows = snapshot.Rows.Select(row => new RowViewModel(
            row.ActorId, ++rank, row.Name, Format(row.Dps), Format(row.Total), Invariant($"{row.Share * 100:0.0}%"),
            Invariant($"{Format(row.Total)} · crit {row.CritRate * 100:0}% · back {row.BackRate * 100:0}% · {row.Hits} hits\nClick for details"),
            row.Total / top, BarBrushes[row.Class], row.IsSelf)).ToList();
    }

    private void UpdateTaken(MeterSnapshot snapshot)
    {
        var incoming = snapshot.Incoming;
        double top = incoming.Count > 0 ? Math.Max(incoming[0].Total, 1) : 1;
        int rank = 0;
        Rows = incoming.Select(row => new RowViewModel(
            row.VictimId, ++rank, row.Name, Format(row.Dtps), Format(row.Total), Invariant($"{row.Share * 100:0.0}%"),
            Invariant($"{Format(row.Total)} taken · {row.Hits} hits · {row.Crits} crits\nClick for details"),
            row.Total / top, BarBrushes[row.Class], row.IsSelf)).ToList();
    }

    public static string Format(double value) => HistoryStore.Compact(value);

    private static string Invariant(FormattableString s) => FormattableString.Invariant(s);

    private static Freezable Freeze(Freezable f)
    {
        f.Freeze();
        return f;
    }
}
