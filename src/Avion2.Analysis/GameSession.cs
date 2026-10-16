using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>Registry + tracker, applied in the right order. Not thread-safe.</summary>
public sealed class GameSession
{
    public GameSession(GameData? data = null, TrackerOptions? options = null)
    {
        Registry = new EntityRegistry(data);
        Tracker = new CombatTracker(Registry, options);
        Runs = new RunTracker(Registry, Tracker, Inventory);
    }

    /// <summary>Dungeon / expedition / instance runs, from entering to leaving.</summary>
    public RunTracker Runs { get; }

    /// <summary>Recent positions, casts, hits and buffs, for fight traces (timeline, buff uptime, replay).</summary>
    public TraceRecorder Recorder { get; } = new();

    /// <summary>Our inventory (from entering the world, kept current by slot updates).</summary>
    public InventoryState Inventory { get; } = new();

    /// <summary>Hidden cubes in range and the ones we open.</summary>
    public CubeTracker Cubes { get; } = new();

    /// <summary>Latest position per entity, for the live map.</summary>
    public LivePositions Positions { get; } = new();

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
            Runs.Apply(e);
            Registry.Apply(e);
            Recorder.Apply(e);
            Positions.Apply(e);
            Cubes.Apply(e, Registry.SelfId, Positions.MapId ?? Registry.MapId, Registry.DisplayName);
            return;
        }

        Registry.Apply(e);
        Tracker.Apply(e);
        Runs.Apply(e);
        Recorder.Apply(e);
        Legion.Apply(e);
        Positions.Apply(e);
        Cubes.Apply(e, Registry.SelfId, Positions.MapId ?? Registry.MapId, Registry.DisplayName);
        if (Inventory.Apply(e))
        {
            Inventory.Character = Registry.SelfId is { } self ? Registry.Find(self)?.Name ?? Inventory.Character : Inventory.Character;
        }
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
