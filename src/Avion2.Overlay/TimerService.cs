using System.Windows.Threading;
using Avion2.Analysis;

namespace Avion2.Overlay;

/// <summary>
/// Ticks the event timetable once a second and raises <see cref="Due"/> once per occurrence when it is
/// within the notification lead time (and its category is enabled). UI thread only.
/// </summary>
public sealed class TimerService
{
    private readonly OverlayViewModel _settings;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly HashSet<(string Key, DateTimeOffset StartsAt)> _notified = new();

    public TimerService(OverlayViewModel settings)
    {
        _settings = settings;
        _timer.Tick += (_, _) => Tick(DateTimeOffset.Now);
        _timer.Start();
    }

    public event Action<Occurrence>? Due;

    public IReadOnlyList<Occurrence> Upcoming(DateTimeOffset now) => GameSchedule.Upcoming(_settings.Region, now);

    public IReadOnlyList<DateTimeOffset> NextRifts(DateTimeOffset now, int count) => GameSchedule.NextRifts(_settings.Region, now, count);

    internal void Tick(DateTimeOffset now)
    {
        var lead = TimeSpan.FromMinutes(_settings.NotifyLeadMinutes);
        foreach (var occurrence in Upcoming(now))
        {
            var remaining = occurrence.StartsAt - now;
            if (remaining > lead || remaining <= TimeSpan.Zero || !_settings.Notifies(occurrence.Event.Category))
            {
                continue;
            }

            if (_notified.Add((occurrence.Event.Key, occurrence.StartsAt)))
            {
                Due?.Invoke(occurrence);
            }
        }

        if (_notified.Count > 500)
        {
            _notified.RemoveWhere(n => n.StartsAt < now);
        }
    }
}
