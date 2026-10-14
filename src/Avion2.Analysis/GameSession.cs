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

    /// <summary>Recent positions, casts, hits and buffs, for fight traces (timeline, buff uptime, replay).</summary>
    public TraceRecorder Recorder { get; } = new();

    /// <summary>Our own legion, as far as the game has told us this session.</summary>
    public LegionState Legion { get; } = new();

    /// <summary>The trace of an encounter (finished or running).</summary>
    public FightTrace Trace(Encounter encounter) =>
        Recorder.Build(encounter, Registry, encounter.Final?.Title ?? MeterView.Build(encounter, Registry).Title);

    public EntityRegistry Registry { get; }

    public CombatTracker Tracker { get; }

    public void Apply(GameEvent e)
    {
        if (e is ZoneChangeEvent)
        {
            // Close (and freeze) the encounter before entity ids are forgotten.
            Tracker.Apply(e);
            Registry.Apply(e);
            Recorder.Apply(e);
            return;
        }

        Registry.Apply(e);
        Tracker.Apply(e);
        Recorder.Apply(e);
        Legion.Apply(e);
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
