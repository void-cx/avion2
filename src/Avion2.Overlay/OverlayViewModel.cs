using System.Collections.ObjectModel;
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

/// <summary>
/// One player card in the overlay. Updated in place every tick (no list rebuilds), and its bar glides
/// toward the target length instead of jumping.
/// </summary>
public sealed class PlayerCard(uint actorId) : Bindable
{
    private int _rank;
    private string _name = string.Empty;
    private string _emblem = "?";
    private Brush _classBrush = Brushes.Gray;
    private ImageSource? _icon;
    private Brush _barBrush = Brushes.Gray;
    private bool _isSelf;
    private string _combatPower = string.Empty;
    private string _value = string.Empty;
    private string _share = string.Empty;
    private string _stats = string.Empty;
    private string _detail = string.Empty;
    private double _ratio;

    public uint ActorId { get; } = actorId;
    public int Rank { get => _rank; set => Set(ref _rank, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Emblem { get => _emblem; set => Set(ref _emblem, value); }
    public Brush ClassBrush { get => _classBrush; set => Set(ref _classBrush, value); }

    /// <summary>Optional class icon supplied by the user; null = drawn emblem.</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (Set(ref _icon, value))
            {
                OnPropertyChanged(nameof(HasIcon));
            }
        }
    }

    public bool HasIcon => _icon is not null;
    public Brush BarBrush { get => _barBrush; set => Set(ref _barBrush, value); }
    public bool IsSelf { get => _isSelf; set => Set(ref _isSelf, value); }
    public string CombatPower { get => _combatPower; set => Set(ref _combatPower, value); }
    public bool HasCombatPower => _combatPower.Length > 0;

    private string _legion = string.Empty;

    /// <summary>"‹Legion›", or empty.</summary>
    public string Legion { get => _legion; set => Set(ref _legion, value); }
    public string Value { get => _value; set => Set(ref _value, value); }
    public string Share { get => _share; set => Set(ref _share, value); }
    public string Stats { get => _stats; set => Set(ref _stats, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    /// <summary>Displayed bar length (0..1), eased toward <see cref="TargetRatio"/>.</summary>
    public double Ratio { get => _ratio; private set => Set(ref _ratio, value); }

    public double TargetRatio { get; set; }

    public void Ease()
    {
        double next = _ratio + (TargetRatio - _ratio) * 0.45;
        Ratio = Math.Abs(TargetRatio - next) < 0.002 ? TargetRatio : next;
    }

    public void NotifyCombatPower() => OnPropertyChanged(nameof(HasCombatPower));
}

/// <summary>
/// Bindable state for the overlay plus the user settings that the Settings page edits.
/// </summary>
public sealed class OverlayViewModel : Bindable
{
    /// <summary>The colour a class is drawn in (cards, bars, replay).</summary>
    public static Color ClassColor(GameClass cls) => ClassColors.GetValueOrDefault(cls, ClassColors[GameClass.Unknown]);

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

    private static readonly Dictionary<GameClass, string> Emblems = new()
    {
        [GameClass.Gladiator] = "GL",
        [GameClass.Templar] = "TE",
        [GameClass.Ranger] = "RA",
        [GameClass.Assassin] = "AS",
        [GameClass.Elementalist] = "EL",
        [GameClass.Sorcerer] = "SO",
        [GameClass.Cleric] = "CL",
        [GameClass.Chanter] = "CH",
        [GameClass.Fighter] = "FI",
        [GameClass.Unknown] = "?",
    };

    private static readonly Dictionary<GameClass, Brush> SolidBrushes = ClassColors.ToDictionary(
        kv => kv.Key, kv => (Brush)Freeze(new SolidColorBrush(kv.Value)));

    private static readonly Dictionary<GameClass, Brush> BarBrushes = ClassColors.ToDictionary(
        kv => kv.Key,
        kv => (Brush)Freeze(new SolidColorBrush(Color.FromArgb(0x99, kv.Value.R, kv.Value.G, kv.Value.B))));

    private string _title = "AVION2";
    private string _duration = "--:--";
    private string _partyValue = string.Empty;
    private string _partyLabel = "Party DPS";
    private string _status = "Starting…";
    private string _footer = string.Empty;
    private string _selfHp = string.Empty;
    private bool _hasHp;
    private double _hpRatio;
    private string _hpText = string.Empty;
    private string _hpPercent = string.Empty;
    private bool _allTargets;
    private bool _locked;
    private bool _lowHpAlert = true;
    private double _alertVolume = 0.6;
    private double _lowHpThreshold = 0.3;
    private string? _lowHpSound;
    private string? _noticeSound;
    private double _noticeVolume = 0.5;
    private bool _autoStart;
    private bool _compactRows;
    private ServerRegion _region = ServerRegion.Europe;
    private int _notifyLeadMinutes = 5;
    private bool _notifyRift = true;
    private bool _notifyBossRaid = true;
    private bool _notifyFieldEvents;
    private bool _notifyPvp = true;
    private bool _notifyReset;
    private bool _notifyFieldBoss = true;
    private bool _notifyWorldBoss = true;
    private bool _notifySound = true;

    private bool _alerting;
    private OverlayTab _tab;
    private double _backgroundOpacity = 0.78;

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Duration { get => _duration; private set => Set(ref _duration, value); }
    public string PartyValue { get => _partyValue; private set => Set(ref _partyValue, value); }
    public string PartyLabel { get => _partyLabel; private set => Set(ref _partyLabel, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Footer { get => _footer; private set => Set(ref _footer, value); }
    public string SelfHp { get => _selfHp; private set => Set(ref _selfHp, value); }
    public bool HasHp { get => _hasHp; private set => Set(ref _hasHp, value); }
    public double HpRatio { get => _hpRatio; private set => Set(ref _hpRatio, value); }
    public string HpText { get => _hpText; private set => Set(ref _hpText, value); }
    public string HpPercent { get => _hpPercent; private set => Set(ref _hpPercent, value); }

    /// <summary>Player cards, updated in place and kept in rank order.</summary>
    public ObservableCollection<PlayerCard> Rows { get; } = new();

    /// <summary>Briefly true after a low-HP alert; drives the red border.</summary>
    public bool Alerting { get => _alerting; set => Set(ref _alerting, value); }

    public OverlayTab Tab
    {
        get => _tab;
        set
        {
            if (Set(ref _tab, value))
            {
                Rows.Clear();
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

    /// <summary>Single-line rows instead of the two-line player cards.</summary>
    public bool CompactRows
    {
        get => _compactRows;
        set
        {
            if (Set(ref _compactRows, value))
            {
                OnPropertyChanged(nameof(DetailedRows));
            }
        }
    }

    public bool DetailedRows => !_compactRows;

    public ServerRegion Region { get => _region; set => Set(ref _region, value); }
    public int NotifyLeadMinutes { get => _notifyLeadMinutes; set => Set(ref _notifyLeadMinutes, Math.Clamp(value, 1, 60)); }
    public bool NotifyRift { get => _notifyRift; set => Set(ref _notifyRift, value); }
    public bool NotifyBossRaid { get => _notifyBossRaid; set => Set(ref _notifyBossRaid, value); }
    public bool NotifyFieldEvents { get => _notifyFieldEvents; set => Set(ref _notifyFieldEvents, value); }
    public bool NotifyPvp { get => _notifyPvp; set => Set(ref _notifyPvp, value); }
    public bool NotifyReset { get => _notifyReset; set => Set(ref _notifyReset, value); }
    public bool NotifyFieldBoss { get => _notifyFieldBoss; set => Set(ref _notifyFieldBoss, value); }
    public bool NotifyWorldBoss { get => _notifyWorldBoss; set => Set(ref _notifyWorldBoss, value); }

    private bool _notifyBossCasts = true;

    public bool NotifyBossCasts { get => _notifyBossCasts; set => Set(ref _notifyBossCasts, value); }
    public bool NotifySound { get => _notifySound; set => Set(ref _notifySound, value); }

    /// <summary>Custom .wav/.mp3 for notifications; null = built-in bell.</summary>
    public string? NoticeSound { get => _noticeSound; set => Set(ref _noticeSound, value); }

    /// <summary>0..1.</summary>
    public double NoticeVolume { get => _noticeVolume; set => Set(ref _noticeVolume, Math.Round(Math.Clamp(value, 0, 1), 2)); }

    public bool Notifies(ScheduleCategory category) => category switch
    {
        ScheduleCategory.Rift => _notifyRift,
        ScheduleCategory.BossRaid => _notifyBossRaid,
        ScheduleCategory.FieldEvent => _notifyFieldEvents,
        ScheduleCategory.Pvp => _notifyPvp,
        ScheduleCategory.Reset => _notifyReset,
        ScheduleCategory.FieldBoss => _notifyFieldBoss,
        ScheduleCategory.WorldBoss => _notifyWorldBoss,
        _ => false,
    };


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

    private string _lagText = string.Empty;
    private long _lagWorstMs;
    private long _lagShownUntil;

    /// <summary>"Server lag 3.2 s" while the game server is silent; "Server froze 7.0 s" for a few seconds after.</summary>
    public string LagText { get => _lagText; private set { if (Set(ref _lagText, value)) OnPropertyChanged(nameof(HasLag)); } }

    public bool HasLag => _lagText.Length > 0;

    private void UpdateLag(long silenceMs)
    {
        long now = Environment.TickCount64;
        if (silenceMs > 1_000)
        {
            _lagWorstMs = Math.Max(_lagWorstMs, silenceMs);
            _lagShownUntil = now + 5_000;
            LagText = Invariant($"⚠ Server lag {silenceMs / 1000.0:0.0} s");
        }
        else if (_lagWorstMs > 0 && now < _lagShownUntil)
        {
            LagText = Invariant($"⚠ Server froze {_lagWorstMs / 1000.0:0.0} s");
        }
        else
        {
            _lagWorstMs = 0;
            LagText = string.Empty;
        }
    }

    public void Update(MeterSnapshot? snapshot, EngineStatus status)
    {
        Status = status.Locked ? (status.InCombat ? "LIVE" : "IDLE") : "SEARCHING";
        UpdateLag(status.ServerSilenceMs);
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
            Rows.Clear();
            return;
        }

        if (_tab == OverlayTab.Taken)
        {
            UpdateTaken(snapshot);
        }
        else
        {
            UpdateDamage(snapshot);
        }
    }

    private void UpdateHeader(MeterSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            Title = "Waiting for combat…";
            Duration = "--:--";
            PartyValue = string.Empty;
            HasHp = false;
            return;
        }

        Title = snapshot.Title;
        Duration = HistoryStore.Duration(snapshot.DurationSeconds);
        if (_tab == OverlayTab.Taken)
        {
            PartyLabel = "Party taken/s";
            PartyValue = Big(snapshot.Incoming.Sum(i => i.Total) / snapshot.DurationSeconds);
        }
        else
        {
            PartyLabel = "Party DPS";
            PartyValue = Big(snapshot.TotalDps);
        }

        if (snapshot.TargetMaxHp is { } max && snapshot.TargetHp is { } hp && max > 0)
        {
            HasHp = true;
            HpRatio = Math.Clamp((double)hp / max, 0, 1);
            HpText = Invariant($"{hp:N0} / {max:N0}");
            HpPercent = Invariant($"{HpRatio * 100:0.0} %");
        }
        else
        {
            HasHp = false;
        }
    }

    private void UpdateDamage(MeterSnapshot snapshot)
    {
        double top = snapshot.Rows.Count > 0 ? Math.Max(snapshot.Rows[0].Total, 1) : 1;
        Sync(snapshot.Rows.Select(r => r.ActorId), (card, i) =>
        {
            var row = snapshot.Rows[i];
            Fill(card, row.Name, row.Class, row.IsSelf, row.CombatPower);
            card.Legion = row.Legion is { Length: > 0 } legion ? $"‹{legion}›" : string.Empty;
            card.Value = Big(row.Dps);
            card.Share = Invariant($"{row.Share * 100:0.0}%");
            card.Stats = Invariant($"Dbl {row.DoubleRate * 100:0}%  Perf {row.PerfectRate * 100:0}%  Crit {row.CritRate * 100:0}%  Front {row.FrontRate * 100:0}%  Back {row.BackRate * 100:0}%");
            card.Detail = Invariant($"{row.Total:N0} damage · {row.Hits} hits\nClick for details");
            card.TargetRatio = row.Total / top;
        });
    }

    private void UpdateTaken(MeterSnapshot snapshot)
    {
        var incoming = snapshot.Incoming;
        double top = incoming.Count > 0 ? Math.Max(incoming[0].Total, 1) : 1;
        Sync(incoming.Select(r => r.VictimId), (card, i) =>
        {
            var row = incoming[i];
            Fill(card, row.Name, row.Class, row.IsSelf, null);
            card.Value = Big(row.Dtps);
            card.Share = Invariant($"{row.Share * 100:0.0}%");
            card.Stats = Invariant($"Hits {row.Hits}  Crits {row.Crits}  Parried {row.Parried}  Blocked {row.Blocked + row.PerfectBlocks}  Absorbed {Format(row.Absorbed)}");
            card.Detail = Invariant($"{row.Total:N0} damage taken\nClick for details");
            card.TargetRatio = row.Total / top;
        });
    }

    /// <summary>Keeps <see cref="Rows"/> in the given order, reusing cards by id; eases every bar.</summary>
    private void Sync(IEnumerable<uint> orderedIds, Action<PlayerCard, int> update)
    {
        var ids = orderedIds.ToList();
        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(Rows[i].ActorId))
            {
                Rows.RemoveAt(i);
            }
        }

        for (int i = 0; i < ids.Count; i++)
        {
            int existing = -1;
            for (int j = i; j < Rows.Count; j++)
            {
                if (Rows[j].ActorId == ids[i])
                {
                    existing = j;
                    break;
                }
            }

            PlayerCard card;
            if (existing < 0)
            {
                card = new PlayerCard(ids[i]);
                Rows.Insert(i, card);
            }
            else
            {
                card = Rows[existing];
                if (existing != i)
                {
                    Rows.Move(existing, i);
                }
            }

            card.Rank = i + 1;
            update(card, i);
            card.Ease();
        }
    }

    private static void Fill(PlayerCard card, string name, GameClass cls, bool isSelf, uint? combatPower)
    {
        card.Name = name;
        card.Emblem = Emblems[cls];
        card.Icon = ClassIcons.For(cls);
        card.ClassBrush = SolidBrushes[cls];
        card.BarBrush = BarBrushes[cls];
        card.IsSelf = isSelf;
        string cp = combatPower is { } value ? Format(value) : string.Empty;
        if (cp != card.CombatPower)
        {
            card.CombatPower = cp;
            card.NotifyCombatPower();
        }
    }

    /// <summary>Full numbers up to a million (like the game), compact above.</summary>
    private static string Big(double value) =>
        value < 1_000_000 ? value.ToString("N0", CultureInfo.InvariantCulture) : Format(value);

    public static string Format(double value) => HistoryStore.Compact(value);

    private static string Invariant(FormattableString s) => FormattableString.Invariant(s);

    private static Freezable Freeze(Freezable f)
    {
        f.Freeze();
        return f;
    }
}
