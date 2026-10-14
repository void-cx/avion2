using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Avion2.Analysis;

namespace Avion2.Overlay;

public sealed record EventLine(string Category, string Name, string At, string In, string Rule);

public sealed record WorldBossLine(string Name, string Zone, string Rule, string At, string In);

public sealed record FieldBossLine(string Name, string When, Brush Dot, Brush WhenBrush, string Detail);

/// <summary>Spacetime Rifts and events, the Reshanta world boss schedule and the live field boss table.</summary>
public partial class TimersPage : UserControl
{
    private readonly AppState _state;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public TimersPage(AppState state)
    {
        _state = state;
        InitializeComponent();
        DataContext = state.Settings;
        RegionBox.ItemsSource = Enum.GetValues<ServerRegion>();
        RegionBox.SelectionChanged += (_, _) => Refresh();
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            Refresh();
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        var now = DateTimeOffset.Now;
        var region = _state.Settings.Region;
        var offset = GameSchedule.ServerOffset(region);
        RegionChip.Text = $"{region} server · GMT+{offset.TotalHours:0}";

        var rifts = _state.Timers.NextRifts(now, 5);
        RiftCountdown.Text = Countdown(rifts[0] - now);
        RiftAt.Text = $"Opens at {rifts[0].ToLocalTime():HH:mm} your time";
        RiftList.Text = "Then " + string.Join(" · ", rifts.Skip(1).Select(r => r.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)));

        var upcoming = _state.Timers.Upcoming(now);
        EventList.ItemsSource = upcoming
            .Where(o => o.Event.Category is not (ScheduleCategory.Rift or ScheduleCategory.WorldBoss))
            .Select(o => new EventLine(AppState.CategoryLabel(o.Event.Category), o.Event.Name, At(o.StartsAt, now), "in " + Countdown(o.StartsAt - now),
                GameSchedule.Describe(o.Event) + " server time"))
            .ToList();

        var bosses = upcoming.Where(o => o.Event.Category == ScheduleCategory.WorldBoss).ToList();
        WorldBossList.ItemsSource = bosses
            .Select(o => new WorldBossLine(o.Event.Name, o.Event.Zone ?? string.Empty, GameSchedule.Describe(o.Event), At(o.StartsAt, now),
                "in " + Countdown(o.StartsAt - now)))
            .ToList();
        WorldBossNote.Text = region is ServerRegion.Korea or ServerRegion.Taiwan
            ? "Korea/Taiwan schedule · times in server time, countdown in yours"
            : "Global schedule · times in server time, countdown in yours";

        RefreshFieldBosses(now);
    }

    private void RefreshFieldBosses(DateTimeOffset now)
    {
        var rows = _state.FieldBosses.Rows(_state.Data, now);
        NoFieldBosses.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var newest = _state.FieldBosses.Maps.Select(m => m.ReceivedAtMs).DefaultIfEmpty(0).Max();
        FieldBossSeen.Text = newest == 0 ? string.Empty
            : $"Updated {DateTimeOffset.FromUnixTimeMilliseconds(newest).ToLocalTime():HH:mm} · {string.Join(", ", _state.FieldBosses.Maps.Select(m => _state.Data.MapName(m.MapId)))}";

        FieldBossList.ItemsSource = rows.Select(r => r.Status switch
        {
            FieldBossStatus.Respawning => new FieldBossLine(r.Name,
                $"{r.At.ToLocalTime():HH:mm} · in {Countdown(r.At - now)}", Theme("IdleBrush"), Theme("HighlightBrush"),
                $"Respawns at {r.At.ToLocalTime():HH:mm} ({_state.Data.MapName(r.MapId)})"),
            FieldBossStatus.Due => new FieldBossLine(r.Name,
                $"due since {r.At.ToLocalTime():HH:mm}", Theme("WarnBrush"), Theme("WarnBrush"),
                $"Respawn was due at {r.At.ToLocalTime():HH:mm}; probably up ({_state.Data.MapName(r.MapId)})"),
            _ => new FieldBossLine(r.Name, $"up since {r.At.ToLocalTime():HH:mm}", Theme("LiveBrush"), Theme("MutedBrush"),
                $"Spawned at {r.At.ToLocalTime():HH:mm} ({_state.Data.MapName(r.MapId)})"),
        }).ToList();
    }

    private static string At(DateTimeOffset at, DateTimeOffset now) =>
        at.ToLocalTime().ToString(at - now > TimeSpan.FromHours(20) ? "ddd HH:mm" : "HH:mm", CultureInfo.InvariantCulture);

    internal static string Countdown(TimeSpan span) => span.TotalHours >= 24
        ? $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes:00}m"
        : $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";

    /// <summary>A brush of the current theme (looked up on every refresh, so theme switches apply).</summary>
    private Brush Theme(string key) => (Brush)FindResource(key);
}
