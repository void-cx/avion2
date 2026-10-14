using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Avion2.Engine;
using Avion2.Protocol;

namespace Avion2.Overlay;

public sealed record DeviceOption(int? Index, string Label);

public sealed record EventLine(string Category, string Name, string At, string In);

public sealed record LegionLine(string Name, string Seen);

public sealed record FieldBossLine(string Name, string When, Brush Dot, Brush WhenBrush, string Detail);

/// <summary>Start/stop the meter, see what's going on, jump to recent fights.</summary>
public partial class HomePage : UserControl
{

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
        state.LegionUpdated += RefreshLegion;
        state.UpdateAvailableChanged += RefreshUpdate;
        state.Fights.CollectionChanged += (_, _) => RefreshRecent();
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            RefreshLegion();
            RefreshUpdate();
            Refresh();
            RefreshRecent();
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void RefreshTimers()
    {
        var now = DateTimeOffset.Now;
        var region = _state.Settings.Region;
        var offset = Avion2.Analysis.GameSchedule.ServerOffset(region);
        RegionChip.Text = $"{region} server · GMT+{offset.TotalHours:0}";

        var rifts = _state.Timers.NextRifts(now, 5);
        RiftCountdown.Text = Countdown(rifts[0] - now);
        RiftAt.Text = $"Opens at {rifts[0].ToLocalTime():HH:mm} your time";
        RiftList.Text = "Then " + string.Join(" · ", rifts.Skip(1).Select(r => r.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)));

        RefreshFieldBosses(now);
        EventList.ItemsSource = _state.Timers.Upcoming(now)
            .Where(o => o.Event.Category != Avion2.Analysis.ScheduleCategory.Rift)
            .Select(o => new EventLine(
                AppState.CategoryLabel(o.Event.Category),
                o.Event.Name,
                o.StartsAt.ToLocalTime().ToString(o.StartsAt - now > TimeSpan.FromHours(20) ? "ddd HH:mm" : "HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                "in " + Countdown(o.StartsAt - now)))
            .ToList();
    }


    private void RefreshFieldBosses(DateTimeOffset now)
    {
        var rows = _state.FieldBosses.Rows(_state.Data, now);
        NoFieldBosses.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var newest = _state.FieldBosses.Maps.Select(m => m.ReceivedAtMs).DefaultIfEmpty(0).Max();
        FieldBossSeen.Text = newest == 0 ? string.Empty
            : $"Updated {DateTimeOffset.FromUnixTimeMilliseconds(newest).ToLocalTime():HH:mm} · {string.Join(", ", _state.FieldBosses.Maps.Select(m => _state.Data.MapName(m.MapId)))}";

        FieldBossList.ItemsSource = rows.Take(16).Select(r => r.Status switch
        {
            Avion2.Analysis.FieldBossStatus.Respawning => new FieldBossLine(r.Name,
                $"{r.At.ToLocalTime():HH:mm} · in {Countdown(r.At - now)}", Theme("IdleBrush"), Theme("HighlightBrush"),
                $"Respawns at {r.At.ToLocalTime():HH:mm} ({_state.Data.MapName(r.MapId)})"),
            Avion2.Analysis.FieldBossStatus.Due => new FieldBossLine(r.Name,
                $"due since {r.At.ToLocalTime():HH:mm}", Theme("WarnBrush"), Theme("WarnBrush"),
                $"Respawn was due at {r.At.ToLocalTime():HH:mm}; probably up ({_state.Data.MapName(r.MapId)})"),
            _ => new FieldBossLine(r.Name, $"up since {r.At.ToLocalTime():HH:mm}", Theme("LiveBrush"), Theme("MutedBrush"),
                $"Spawned at {r.At.ToLocalTime():HH:mm} ({_state.Data.MapName(r.MapId)})"),
        }).ToList();
    }

    private static string Countdown(TimeSpan span) => span.TotalHours >= 24
        ? $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes:00}m"
        : $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";

    private void Refresh()
    {
        RefreshTimers();
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
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "IdleBrush");
            StatusTitle.Text = "Meter stopped";
            StatusDetail.Text = "Start the meter, then play: the overlay appears and finds the game connection by itself.";
            LiveLine.Visibility = Visibility.Collapsed;
            return;
        }

        var status = engine.Status();
        if (!status.Locked)
        {
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "WarnBrush");
            StatusTitle.Text = "Meter running · searching for AION2";
            StatusDetail.Text = status.Message;
        }
        else
        {
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "LiveBrush");
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

    /// <summary>A brush of the current theme (looked up on every refresh, so theme switches apply).</summary>
    private Brush Theme(string key) => (Brush)FindResource(key);
}
