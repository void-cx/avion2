using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <summary>
/// One player's full breakdown of one encounter: follows a live source, or shows a saved fight.
/// Used by the live detail window and the History page.
/// </summary>
public partial class DetailView : UserControl
{
    private readonly DetailViewModel _vm = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private Func<MeterSnapshot?>? _live;
    private Func<bool>? _inCombat;
    private Func<FightTrace?>? _liveTrace;
    private DateTime _traceRefreshed;
    private HistoryEntry? _saved;
    private TimeSpan _lastFrame;
    private DateTime _paneUpdated;
    private Dictionary<uint, uint> _owners = new();

    /// <summary>Name tables for skills and buffs (set by the app when they load).</summary>
    public static GameData Names { get; set; } = GameData.Empty;

    public DetailView()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.PlayerChanged += Refresh;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DetailViewModel.TabIndex))
            {
                SyncTabLinks();
                Refresh();
            }
            else if (e.PropertyName == nameof(DetailViewModel.Trace))
            {
                _owners = _vm.Trace?.Entities.ToDictionary(x => x.Id, x => x.OwnerId ?? x.Id) ?? new();
                UpdatePlayhead(force: true);
            }
        };
        System.ComponentModel.DependencyPropertyDescriptor
            .FromProperty(ScrubberView.ValueProperty, typeof(ScrubberView))
            .AddValueChanged(ReplaySlider, (_, _) => UpdatePlayhead(force: PlayButton.IsChecked != true));
        SyncTabLinks();
        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Space && _vm.TabIndex == 4)
            {
                PlayButton.IsChecked = PlayButton.IsChecked != true;
                e.Handled = true;
            }
        };
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            SortByTotal();
            if (_live is not null)
            {
                _timer.Start();
            }
        };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            PlayButton.IsChecked = false;
        };
    }

    /// <summary>Raised when the pin toggle changes (only shown when <see cref="CanPin"/>).</summary>
    public event Action<bool>? PinChanged;

    public bool CanPin
    {
        set => PinButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public DetailViewModel ViewModel => _vm;

    /// <summary>Follows a live encounter.</summary>
    public void ShowLive(Func<MeterSnapshot?> snapshot, Func<bool> inCombat, uint? actorId = null, int tab = 0, Func<FightTrace?>? trace = null)
    {
        _live = snapshot;
        _inCombat = inCombat;
        _liveTrace = trace;
        _traceRefreshed = default;
        _saved = null;
        Focus(actorId, tab);
        if (IsLoaded)
        {
            _timer.Start();
        }
    }

    /// <summary>Shows a saved fight (null clears the view).</summary>
    public void ShowSaved(HistoryEntry? entry, uint? actorId = null, int tab = -1, FightTrace? trace = null)
    {
        _timer.Stop();
        _live = null;
        _liveTrace = null;
        _saved = entry;
        _vm.Trace = trace;
        ReplaySlider.Value = 0;
        Replay.ResetView();
        Focus(actorId, tab < 0 ? _vm.TabIndex : tab);
    }

    private void Focus(uint? actorId, int tab)
    {
        _vm.SelectedActorId = actorId;
        _vm.TabIndex = tab;
        _vm.Skills.Clear();
        _vm.Sources.Clear();
        Refresh();
        SortByTotal();
    }

    private void Refresh()
    {
        _vm.Data = Names;
        Timeline.Data = Names;
        Replay.Data = Names;

        // The live trace is rebuilt at most once a second, and only while a trace tab is open.
        if (_liveTrace is not null && _vm.TabIndex >= 2 && DateTime.UtcNow - _traceRefreshed > TimeSpan.FromSeconds(1))
        {
            bool atEnd = ReplaySlider.Value >= ReplaySlider.Maximum - 1;
            _vm.Trace = _liveTrace();
            _traceRefreshed = DateTime.UtcNow;
            if (atEnd && PlayButton.IsChecked != true)
            {
                ReplaySlider.Value = ReplaySlider.Maximum;
            }
        }

        if (_live is not null)
        {
            var snapshot = _live();
            _vm.Update(snapshot, _inCombat?.Invoke() == true ? "● Live" : "Live · last fight");
            _vm.Subtitle = Subtitle(snapshot);
            return;
        }

        if (_saved is { } saved)
        {
            _vm.Update(saved.Snapshot, "Saved fight");
            _vm.Subtitle = Subtitle(saved.Snapshot);
            return;
        }

        _vm.Update(null, string.Empty);
        _vm.Subtitle = string.Empty;
    }

    /// <summary>"14 Oct · 23:11 · 1:20 · 2 players · Verteron".</summary>
    private string Subtitle(MeterSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return string.Empty;
        }

        var started = DateTimeOffset.FromUnixTimeMilliseconds(snapshot.StartedAtMs).ToLocalTime();
        var parts = new List<string>
        {
            started.ToString("dd MMM · HH:mm", CultureInfo.InvariantCulture),
            HistoryStore.Duration(snapshot.DurationSeconds),
            snapshot.Rows.Count <= 1 ? "solo" : $"{snapshot.Rows.Count} players",
        };
        if (_vm.Trace?.MapId is { } map && Names.Maps.ContainsKey(map))
        {
            parts.Add(Names.MapName(map));
        }

        return string.Join(" · ", parts);
    }

    private void TabLink_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
        {
            _vm.TabIndex = int.Parse(tag, CultureInfo.InvariantCulture);
        }
    }

    private void SyncTabLinks()
    {
        foreach (var link in TabLinks.Children.OfType<RadioButton>())
        {
            if ((string)link.Tag == _vm.TabIndex.ToString(CultureInfo.InvariantCulture))
            {
                link.IsChecked = true;
            }
        }
    }

    private static string Format(double ms) => TimeSpan.FromMilliseconds(Math.Max(0, ms)).ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// The replay pane at the playhead: the main target's HP, every player's damage so far and the last hits.
    /// While playing it updates a few times a second (the view itself runs at full frame rate).
    /// </summary>
    private void UpdatePlayhead(bool force)
    {
        var trace = _vm.Trace;
        double at = ReplaySlider.Value;
        Clock.Text = Format(at);
        Length.Text = trace is null ? string.Empty : $"of {Format(trace.DurationMs)}";
        if (!force && DateTime.UtcNow - _paneUpdated < TimeSpan.FromMilliseconds(150))
        {
            return;
        }

        _paneUpdated = DateTime.UtcNow;
        if (trace is null)
        {
            _vm.PlayheadLabel = string.Empty;
            _vm.BossHpText = string.Empty;
            _vm.BossHp = 0;
            _vm.PlayheadRows = [];
            _vm.RecentHits = [];
            return;
        }

        int t = (int)at;
        _vm.PlayheadLabel = $"AT {Format(t)}";
        if (trace.PrimaryTargetId is { } target && trace.Entity(target) is { MaxHp: > 0 } boss && trace.HpAt(target, t) is { } hp)
        {
            double ratio = Math.Clamp(hp / (double)boss.MaxHp, 0, 1);
            _vm.BossHp = ratio;
            _vm.BossHpText = $"{boss.Name} {ratio.ToString("P0", CultureInfo.InvariantCulture)}";
        }
        else
        {
            _vm.BossHp = 0;
            _vm.BossHpText = string.Empty;
        }

        var accent = ((System.Windows.Media.SolidColorBrush)FindResource("AccentBrush")).Color;
        var players = trace.Entities.Where(e => e.Kind is TraceEntityKind.Self or TraceEntityKind.Player).ToDictionary(e => e.Id);
        uint Owner(uint id) => _owners.GetValueOrDefault(id, id);
        var damage = new Dictionary<uint, long>();
        var recent = new List<TraceHit>();
        foreach (var hit in trace.Hits)
        {
            if (hit.T > t)
            {
                continue;
            }

            uint owner = Owner(hit.ActorId);
            if (!players.ContainsKey(owner))
            {
                continue;
            }

            damage[owner] = damage.GetValueOrDefault(owner) + hit.Amount;
            recent.Add(hit);
        }

        long total = Math.Max(1, damage.Values.Sum());
        double seconds = Math.Max(t, 1000) / 1000.0;
        System.Windows.Media.Brush Brush(System.Windows.Media.Color c, byte alpha = 255)
        {
            var b = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        _vm.PlayheadRows = damage.OrderByDescending(d => d.Value).Take(6).Select(d =>
        {
            var e = players[d.Key];
            var color = ScrubberView.LaneColor(e, accent);
            double share = d.Value / (double)total;
            string who = e.Class == Avion2.Protocol.GameClass.Unknown ? string.Empty : $"{e.Class} · ";
            return new PlayheadRow(e.Kind == TraceEntityKind.Self ? "You" : e.Name, who + share.ToString("P0", CultureInfo.InvariantCulture),
                (d.Value / seconds).ToString("N0", CultureInfo.InvariantCulture), share, Brush(color), Brush(color, 0x38), e.Kind == TraceEntityKind.Self);
        }).ToList();

        var critBrush = Brush(System.Windows.Media.Color.FromRgb(0xFF, 0xD1, 0x5C));
        var plain = (System.Windows.Media.Brush)FindResource("TextBrush");
        _vm.RecentHits = recent.OrderByDescending(h => h.T).Take(7).Select(h =>
        {
            var e = players[Owner(h.ActorId)];
            string skill = Names.SkillName(h.SkillId);
            string text = e.Kind == TraceEntityKind.Self ? skill : $"{e.Name} · {skill}";
            return new HitLine(TimeSpan.FromMilliseconds(Math.Max(0, h.T)).ToString(@"m\:ss\.f", CultureInfo.InvariantCulture), text,
                h.Amount.ToString("N0", CultureInfo.InvariantCulture), Brush(ScrubberView.LaneColor(e, accent)), h.Critical ? critBrush : plain);
        }).ToList();
    }

    /// <summary>Default order: biggest total first (headers can re-sort).</summary>
    private void SortByTotal()
    {
        foreach (var source in new object[] { _vm.Skills, _vm.Sources })
        {
            var view = CollectionViewSource.GetDefaultView(source);
            if (view.SortDescriptions.Count == 0)
            {
                view.SortDescriptions.Add(new SortDescription("Total", ListSortDirection.Descending));
            }

            if (view is ICollectionViewLiveShaping { CanChangeLiveSorting: true } live && !live.LiveSortingProperties.Contains("Total"))
            {
                live.LiveSortingProperties.Add("Total");
                live.IsLiveSorting = true;
            }
        }
    }

    private void Pin_Changed(object sender, RoutedEventArgs e) => PinChanged?.Invoke(PinButton.IsChecked == true);

    private void Play_Changed(object sender, RoutedEventArgs e)
    {
        bool playing = PlayButton.IsChecked == true;
        System.Windows.Media.CompositionTarget.Rendering -= OnFrame;
        if (!playing)
        {
            UpdatePlayhead(force: true);
        }

        if (playing)
        {
            if (ReplaySlider.Value >= ReplaySlider.Maximum - 1)
            {
                ReplaySlider.Value = 0;
            }

            _lastFrame = TimeSpan.Zero;
            System.Windows.Media.CompositionTarget.Rendering += OnFrame;
            Replay.Focus();
        }
    }

    /// <summary>Advances the replay with the screen's frame clock.</summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((System.Windows.Media.RenderingEventArgs)e).RenderingTime;
        if (_lastFrame != TimeSpan.Zero)
        {
            double speed = Speeds.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true) is { Tag: string tag } &&
                           double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) ? s : 1;
            ReplaySlider.Value = Math.Min(ReplaySlider.Maximum, ReplaySlider.Value + (now - _lastFrame).TotalMilliseconds * speed);
            if (ReplaySlider.Value >= ReplaySlider.Maximum)
            {
                PlayButton.IsChecked = false;
            }
        }

        _lastFrame = now;
    }
}
