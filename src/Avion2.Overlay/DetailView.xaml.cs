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
                Refresh();
            }
        };
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
            _vm.Update(_live(), _inCombat?.Invoke() == true ? "● Live" : "Live · last fight");
            return;
        }

        if (_saved is { } saved)
        {
            var started = DateTimeOffset.FromUnixTimeMilliseconds(saved.Snapshot.StartedAtMs).ToLocalTime();
            _vm.Update(saved.Snapshot, $"Saved fight · {started.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)}");
            return;
        }

        _vm.Update(null, string.Empty);
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
        PlayButton.Content = playing ? "" : ""; // pause / play glyphs
        System.Windows.Media.CompositionTarget.Rendering -= OnFrame;
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
            double speed = SpeedBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
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
