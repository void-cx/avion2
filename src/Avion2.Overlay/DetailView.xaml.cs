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
    private HistoryEntry? _saved;

    public DetailView()
    {
        InitializeComponent();
        DataContext = _vm;
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            SortByTotal();
            if (_live is not null)
            {
                _timer.Start();
            }
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>Raised when the pin toggle changes (only shown when <see cref="CanPin"/>).</summary>
    public event Action<bool>? PinChanged;

    public bool CanPin
    {
        set => PinButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public DetailViewModel ViewModel => _vm;

    /// <summary>Follows a live encounter.</summary>
    public void ShowLive(Func<MeterSnapshot?> snapshot, Func<bool> inCombat, uint? actorId = null, int tab = 0)
    {
        _live = snapshot;
        _inCombat = inCombat;
        _saved = null;
        Focus(actorId, tab);
        if (IsLoaded)
        {
            _timer.Start();
        }
    }

    /// <summary>Shows a saved fight (null clears the view).</summary>
    public void ShowSaved(HistoryEntry? entry, uint? actorId = null, int tab = -1)
    {
        _timer.Stop();
        _live = null;
        _saved = entry;
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
}
