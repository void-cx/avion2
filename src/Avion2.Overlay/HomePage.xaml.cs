using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Avion2.Engine;
using Avion2.Protocol;

namespace Avion2.Overlay;

public sealed record DeviceOption(int? Index, string Label);

public sealed record LegionLine(string Name, string Seen);

public sealed record OverlayModeOption(string Id, string Label);

/// <summary>Start/stop the meter, see what's going on, jump to recent fights.</summary>
public partial class HomePage : UserControl
{
    /// <summary>Height of the banner (the key-art video plays behind it).</summary>
    public const double BannerHeight = 330;

    public static GridLength BannerRow { get; } = new(BannerHeight);

    private DetailWindow? _fightWindow;

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
        OverlayModeBox.ItemsSource = OverlayModes;
        OverlayModeBox.SelectedItem = OverlayModes.FirstOrDefault(m => m.Id == state.Stored.OverlayAutoShow) ?? OverlayModes[1];
        RecordBox.Click += (_, _) => state.Stored.RecordSessions = RecordBox.IsChecked == true;

        state.MeterStateChanged += Refresh;
        state.LegionUpdated += RefreshLegion;
        state.UpdateAvailableChanged += RefreshUpdate;
        state.Fights.CollectionChanged += (_, _) => RefreshRecent();
        state.InventoryUpdated += RefreshCharacter;
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            RefreshLegion();
            RefreshUpdate();
            Refresh();
            RefreshRecent();
            RefreshCharacter();
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>Next rift, next world boss and the next field boss respawn (or event when none is known).</summary>
    private void RefreshComingUp()
    {
        var now = DateTimeOffset.Now;
        var upcoming = _state.Timers.Upcoming(now);
        var lines = new List<(DateTimeOffset At, EventLine Line)>();

        void Add(string category, string name, DateTimeOffset at) =>
            lines.Add((at, new EventLine(category, name, $"{at.ToLocalTime():HH:mm}", "in " + TimersPage.Countdown(at - now), string.Empty)));


        if (_state.FieldBosses.Rows(_state.Data, now).FirstOrDefault(r => r.Status == Avion2.Analysis.FieldBossStatus.Respawning) is { } next)
        {
            Add("Field boss", next.Name, next.At);
        }

        var rift = upcoming.First(o => o.Event.Category == Avion2.Analysis.ScheduleCategory.Rift);
        Add("Spacetime Rift", "Spacetime Rift", rift.StartsAt);
        if (upcoming.FirstOrDefault(o => o.Event.Category == Avion2.Analysis.ScheduleCategory.WorldBoss) is { } boss)
        {
            Add("World boss", boss.Event.Name, boss.StartsAt);
        }

        if (upcoming.FirstOrDefault(o => o.Event.Category == Avion2.Analysis.ScheduleCategory.BossRaid) is { } raid)
        {
            Add("Boss raid", raid.Event.Name, raid.StartsAt);
        }

        ComingUp.ItemsSource = lines.OrderBy(l => l.At).Take(4).Select(l => l.Line).ToList();
    }

    private void Refresh()
    {
        RefreshComingUp();
        NpcapValue.Text = AppState.NpcapInstalled ? "Installed" : "Missing: install from npcap.com";
        NamesValue.Text = _state.SkillNames == 0
            ? "Not installed (Settings)"
            : $"{_state.SkillNames:N0} skills · {_state.NpcNames:N0} NPCs";

        bool running = _state.IsRunning;
        StartStop.Content = running ? "Stop capture" : "Start capture";
        OverlayToggle.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        OverlayToggle.Content = _state.OverlayVisible ? "Hide overlay" : "Show overlay";
        DeviceBox.IsEnabled = RecordBox.IsEnabled = !running; // the overlay choice stays editable while capturing

        if (_state.Engine is not { } engine)
        {
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "IdleBrush");
            StatusTitle.Text = "Capture stopped";
            StatusDetail.Text = "Start the capture, then play: AVION2 finds the game connection by itself.";
            LiveLine.Visibility = Visibility.Collapsed;
            return;
        }

        var status = engine.Status();
        if (!status.Locked)
        {
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "WarnBrush");
            StatusTitle.Text = "Capturing · searching for AION2";
            StatusDetail.Text = status.Message;
        }
        else
        {
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "LiveBrush");
            StatusTitle.Text = "Capturing · connected";
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
        Recent.ItemsSource = _state.Fights.Take(6).ToList();
        var last = _state.Fights.FirstOrDefault();
        NoFights.Visibility = last is null ? Visibility.Visible : Visibility.Collapsed;
        LastStats.Visibility = last is null ? Visibility.Collapsed : Visibility.Visible;
        LastReplay.IsEnabled = last is not null;
        LastTitle.Text = last is null ? "No fights yet" : last.Title;
        LastWhen.Text = last is null ? string.Empty : $"{last.Day} · {last.Time}";
        LastDps.Text = last?.YourDpsFull ?? string.Empty;
        LastDuration.Text = last?.Duration ?? string.Empty;
    }

    /// <summary>Your characters, Kinah and Enhance Stones (from the last login the meter saw).</summary>
    private void RefreshCharacter()
    {
        var characters = new CharacterStore().Characters;
        CharactersValue.Text = characters.Count == 0
            ? "None yet: teleport once in game"
            : string.Join("\n", characters.Take(3).Select(c => $"{c.Name} · {c.Class}"));
        if (_state.Inventory is { IsKnown: true } inventory)
        {
            Wallet.Visibility = Visibility.Visible;
            KinahValue.Text = inventory.Kinah.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
            StonesValue.Text = inventory.Count(Avion2.Analysis.ItemIds.EnhanceStone).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
            InventoryAge.Text = inventory.Character is { } who ? who : string.Empty;
        }
        else
        {
            Wallet.Visibility = Visibility.Collapsed;
            InventoryAge.Text = string.Empty;
        }
    }

    private void LastReplay_Click(object sender, RoutedEventArgs e)
    {
        if (_state.Fights.FirstOrDefault() is not { } last)
        {
            return;
        }

        var trace = _state.LoadTrace(last.Entry);
        if (_fightWindow is not { IsLoaded: true })
        {
            _fightWindow = new DetailWindow { Owner = Window.GetWindow(this) };
        }

        _fightWindow.OpenSaved(last.Entry, trace, trace is null ? 0 : DetailViewModel.ReplayTab);
    }

    private void Forge_Click(object sender, RoutedEventArgs e) => _state.RequestPage("forge");

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

    private void OverlayToggle_Click(object sender, RoutedEventArgs e) => _state.SetOverlayVisible(!_state.OverlayVisible);

    private static readonly OverlayModeOption[] OverlayModes =
    [
        new("start", "Show when the capture starts"),
        new("connection", "Show when the game is connected"),
        new("fight", "Show during fights"),
        new("manual", "Only when I show it"),
    ];

    private void OverlayMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OverlayModeBox.SelectedItem is OverlayModeOption mode)
        {
            _state.Stored.OverlayAutoShow = mode.Id;
            _state.SaveSettings();
        }
    }

    private void Timers_Click(object sender, RoutedEventArgs e) => _state.RequestPage("timers");

    private void Recent_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HistoryRow row })
        {
            _openHistory(row);
        }
    }

    private bool _updateDismissed;

    private void RefreshUpdate()
    {
        if (_state.AvailableUpdate is not { } release || _updateDismissed)
        {
            UpdateBanner.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateBanner.Visibility = Visibility.Visible;
        UpdateTitle.Text = $"AVION2 {release.Tag} is available";
        string size = release.ZipSize > 0 ? $" · {release.ZipSize / 1_048_576.0:0} MB download" : string.Empty;
        UpdateDetail.Text = $"You have v{Avion2.Engine.UpdateChecker.CurrentVersion}{size}. Updating closes AVION2, installs the release and starts it again; your settings and fights stay.";
    }

    private void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_state.StartUpdate() is { } error)
        {
            MessageBox.Show(Window.GetWindow(this), error, "AVION2 update", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateLater_Click(object sender, RoutedEventArgs e)
    {
        _updateDismissed = true;
        RefreshUpdate();
    }

    private void ReleaseNotes_Click(object sender, RoutedEventArgs e)
    {
        string url = _state.AvailableUpdate?.PageUrl ?? Avion2.Engine.UpdateChecker.ReleasesPage;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>Our legion: name, leader, introduction and who logged in last (saved between sessions).</summary>
    private void RefreshLegion()
    {
        if (_state.Legion is not { IsKnown: true } legion)
        {
            LegionCard.Visibility = Visibility.Collapsed;
            return;
        }

        var now = DateTimeOffset.Now;
        LegionCard.Visibility = Visibility.Visible;
        LegionName.Text = legion.Name;
        var members = legion.Members;
        LegionMeta.Text = string.Join(" · ", new[]
        {
            legion.Leader is { } leader ? $"Leader {leader}" : null,
            members.Count > 0 ? $"{members.Count} members seen" : null,
        }.OfType<string>());
        LegionIntro.Text = legion.Texts.FirstOrDefault() ?? string.Empty;
        LegionIntro.Visibility = LegionIntro.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        LegionMembers.ItemsSource = members.Take(15).Select(m => new LegionLine(m.Name, Seen(m.LastOnlineMs, now))).ToList();
    }

    private static string Seen(long? lastOnlineMs, DateTimeOffset now)
    {
        if (lastOnlineMs is not { } ms)
        {
            return string.Empty;
        }

        var ago = now - DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return ago.TotalMinutes < 60 ? $"{Math.Max(1, (int)ago.TotalMinutes)} min ago"
            : ago.TotalHours < 24 ? $"{(int)ago.TotalHours} h ago"
            : $"{(int)ago.TotalDays} d ago";
    }
}
