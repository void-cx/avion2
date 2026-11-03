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
        Loot.PickedUp += Runs.OnPickup;
    }

    /// <summary>Items on the ground and the ones we pick up.</summary>
    public LootTracker Loot { get; } = new();

    /// <summary>Dungeon / expedition / instance runs, from entering to leaving.</summary>
    public RunTracker Runs { get; }

    /// <summary>Recent positions, casts, hits and buffs, for fight traces (timeline, buff uptime, replay).</summary>
    public TraceRecorder Recorder { get; } = new();

    /// <summary>Our inventory (from entering the world, kept current by slot updates).</summary>
    public InventoryState Inventory { get; } = new();

    /// <summary>The entity the game last named as us, and that name.</summary>
    private (uint Id, string Name)? _named;

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
        switch (e)
        {
            // The death record (04 8D) is the death: the despawn (42 36) comes ~3.4 s later and never for players
            case KillEvent kill:
                _killed[kill.VictimId] = kill.TimestampMs;
                ApplyOne(e);
                ApplyOne(new DeathEvent(kill.TimestampMs, kill.VictimId, DeathKind.Combat, AtKill: true));
                return;
            case DeathEvent { Kind: DeathKind.Combat } death
                when _killed.Remove(death.EntityId, out long at) && death.TimestampMs - at is >= 0 and <= KilledToDespawnMs:
                return; // already applied at the kill
            case ZoneChangeEvent:
                _killed.Clear();
                break;
        }

        ApplyOne(e);
    }

    /// <summary>How long after its death record an entity's despawn still belongs to that death.</summary>
    private const long KilledToDespawnMs = 15_000;

    /// <summary>Entities whose death record came, until their despawn does.</summary>
    private readonly Dictionary<uint, long> _killed = new();

    private void ApplyOne(GameEvent e)
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
        Loot.Apply(e, Registry.SelfName);
        Recorder.Apply(e);
        Legion.Apply(e);
        Positions.Apply(e);
        Cubes.Apply(e, Registry.SelfId, Positions.MapId ?? Registry.MapId, Registry.DisplayName);
        // The list comes at login a few seconds before our name, and after a character switch the old name is still
        // around: a new list keeps the name only while we are the entity the game last named, else waits for it (as do
        // slot updates seen before any list, so they land on the right character's saved inventory)
        if (e is SelfIdentityEvent { Name.Length: > 0 } named)
        {
            if (Legion.Character is { } of && of != named.Name)
            {
                Legion.Clear(); // another character: its legion comes next, the old one must not mix in
            }

            _named = (named.EntityId, named.Name);
            if (Inventory.Character is null)
            {
                Inventory.Character = named.Name;
            }
        }

        if (e is LegionDetailsEvent or LegionRosterEvent)
        {
            Legion.Character = _named?.Name ?? Registry.SelfName;
        }

        if (Inventory.Apply(e) && e is InventoryListEvent)
        {
            Inventory.Character = _named is { } n && n.Id == Registry.SelfId ? n.Name : null;
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
