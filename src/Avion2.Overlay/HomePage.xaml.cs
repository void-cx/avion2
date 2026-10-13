using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Avion2.Engine;
using Avion2.Protocol;

namespace Avion2.Overlay;

public sealed record DeviceOption(int? Index, string Label);

/// <summary>Start/stop the meter, see what's going on, jump to recent fights.</summary>
public partial class HomePage : UserControl
{
    private static readonly Brush Idle = Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));
    private static readonly Brush Searching = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
    private static readonly Brush Connected = Freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)));

    private readonly AppState _state;
    private readonly Action<HistoryRow?> _openHistory;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public HomePage(AppState state, Action<HistoryRow?> openHistory)
    {
        _state = state;
        _openHistory = openHistory;
        InitializeComponent();

        var devices = new List<DeviceOption> { new(null, "Automatic (all adapters, detects the game)") };
        devices.AddRange(state.Devices()
            .Where(d => d.IsLoopback || d.Addresses.Count > 0)
            .Select(d => new DeviceOption(d.Index, $"#{d.Index} {d.Description}{(d.IsLoopback ? " (loopback)" : string.Empty)}")));
        DeviceBox.ItemsSource = devices;
        DeviceBox.SelectedItem = devices.FirstOrDefault(d => d.Index == state.Stored.Device) ?? devices[0];
        DeviceBox.SelectionChanged += (_, _) => state.Stored.Device = (DeviceBox.SelectedItem as DeviceOption)?.Index;
        RecordBox.IsChecked = state.Stored.RecordSessions;
        RecordBox.Click += (_, _) => state.Stored.RecordSessions = RecordBox.IsChecked == true;

        state.MeterStateChanged += Refresh;
        state.Fights.CollectionChanged += (_, _) => RefreshRecent();
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            Refresh();
            RefreshRecent();
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        NpcapValue.Text = AppState.NpcapInstalled ? "Installed ✓" : "Missing — install from npcap.com";
        NamesValue.Text = _state.SkillNames == 0
            ? "Not installed (Settings → download)"
            : $"{_state.SkillNames:N0} skills · {_state.NpcNames:N0} NPCs";
        var characters = new CharacterStore().Characters;
        CharactersValue.Text = characters.Count == 0
            ? "None yet — teleport once in game"
            : string.Join("\n", characters.Take(3).Select(c =>
                $"{c.Name} · {c.Class}{(c.MaxHp > 0 ? $" · {c.MaxHp:N0} HP" : string.Empty)}"));

        bool running = _state.IsRunning;
        StartStop.Content = running ? "Stop Meter" : "Start Meter";
        Options.IsEnabled = !running;

        if (_state.Engine is not { } engine)
        {
            StatusDot.Fill = Idle;
            StatusTitle.Text = "Meter stopped";
            StatusDetail.Text = "Start the meter, then play: the overlay appears and finds the game connection by itself.";
            LiveLine.Visibility = Visibility.Collapsed;
            return;
        }

        var status = engine.Status();
        if (!status.Locked)
        {
            StatusDot.Fill = Searching;
            StatusTitle.Text = "Meter running · searching for AION2";
            StatusDetail.Text = status.Message;
        }
        else
        {
            StatusDot.Fill = Connected;
            StatusTitle.Text = "Meter running · connected";
            string who = status.SelfName is { } name
                ? $"{name} · {status.SelfClass}"
                : status.SelfClass != GameClass.Unknown ? $"You · {status.SelfClass}" : "waiting for your character";
            StatusDetail.Text = $"{who} · {status.GamePackets:N0} game packets" +
                                (status.Recorded > 0 ? $" · recording ({status.Recorded:N0} frames)" : string.Empty);
        }

        var snapshot = engine.Snapshot(_state.Settings.Scope);
        if (snapshot is null)
        {
            LiveLine.Visibility = Visibility.Collapsed;
            return;
        }

        LiveLine.Visibility = Visibility.Visible;
        string yours = snapshot.Self is { } me ? $" · you {HistoryStore.Compact(me.Dps)}/s" : string.Empty;
        LiveLine.Text = $"{(status.InCombat ? "Fighting" : "Last fight")}: {snapshot.Title} · {HistoryStore.Duration(snapshot.DurationSeconds)}" +
                        $" · party {HistoryStore.Compact(snapshot.TotalDps)}/s{yours}";
    }

    private void RefreshRecent()
    {
        var recent = _state.Fights.Take(6).ToList();
        Recent.ItemsSource = recent;
        NoFights.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_state.IsRunning)
        {
            _state.StopMeter();
            return;
        }

        _state.SaveSettings();
        if (_state.StartMeter() is { } error)
        {
            MessageBox.Show(Window.GetWindow(this)!, error, "AVION2", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AllFights_Click(object sender, RoutedEventArgs e) => _openHistory(null);

    private void Recent_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HistoryRow row })
        {
            _openHistory(row);
        }
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
