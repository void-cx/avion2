using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>Registry + tracker, applied in the right order. Not thread-safe.</summary>
public sealed class GameSession
{
    public GameSession(GameData? data = null, TrackerOptions? options = null)
    {
        Registry = new EntityRegistry(data);
        Tracker = new CombatTracker(Registry, options);
    }

    public EntityRegistry Registry { get; }

    public CombatTracker Tracker { get; }

    public void Apply(GameEvent e)
    {
        if (e is ZoneChangeEvent)
        {
            // Close (and freeze) the encounter before entity ids are forgotten.
            Tracker.Apply(e);
            Registry.Apply(e);
            return;
        }

        Registry.Apply(e);
        Tracker.Apply(e);
    }

    /// <summary>The running encounter, or the last finished one.</summary>
    public MeterSnapshot? Snapshot(MeterScope scope = MeterScope.MyTargets, long? nowMs = null)
    {
        if (Tracker.Current is { } current)
        {
            return MeterView.Build(current, Registry, scope, nowMs);
        }

        return Tracker.History.LastOrDefault()?.Final;
    }
}
