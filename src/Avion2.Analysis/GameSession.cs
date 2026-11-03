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

    /// <summary>NPC casts with a cast time, for the boss cast bar.</summary>
    public NpcCastTracker Casts { get; } = new();

    /// <summary>Our own legion, as far as the game has told us this session.</summary>
    public LegionState Legion { get; } = new();

    /// <summary>
    /// Our character's progress (item level, daily and weekly counts, tickets, Daeva Pass, pet souls) as far as the
    /// game has told us since the character entered the world.
    /// </summary>
    public CharacterProgress Progress { get; private set; } = new();

    private readonly SoulItemLearner _soulItems = new();

    /// <summary>Who is in our party, with their HP and where they are.</summary>
    public PartyTracker Party { get; } = new();

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
        // Offsets become positions (10 Hz) for the replay and the live map; nothing else uses positions
        if (Movement.Apply(e) is { } moved)
        {
            Recorder.Apply(moved);
            Positions.Apply(moved);
        }
    }

    /// <summary>Adds up the 10 Hz movement offsets of other players and monsters into positions.</summary>
    public MovementTracker Movement { get; } = new();

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
            Casts.Apply(e);
            Cubes.Apply(e, Registry.SelfId, Positions.MapId ?? Registry.MapId, Registry.DisplayName);
            return;
        }

        Registry.Apply(e);
        Tracker.Apply(e);
        Runs.Apply(e);
        Loot.Apply(e, Registry.SelfName);
        Recorder.Apply(e);
        Legion.Apply(e);
        Party.Apply(e, Registry);
        Positions.Apply(e);
        Casts.Apply(e);
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

        // The Legion Hall record comes at most map loads, also before our name at login: only taken while we are the
        // entity the game last named (the next map load sends it again)
        if (e is LegionHallInfoEvent hall && _named is { } self && self.Id == Registry.SelfId &&
            (Legion.Character is null || Legion.Character == self.Name))
        {
            Legion.Character = self.Name;
            Legion.ApplyHall(hall);
        }

        if (Inventory.Apply(e) && e is InventoryListEvent)
        {
            Inventory.Character = _named is { } n && n.Id == Registry.SelfId ? n.Name : null;
        }

        ApplyProgress(e);
    }

    /// <summary>
    /// Keeps <see cref="Progress"/> on the right character: the records sent on entering the world start over when
    /// they come for a character other than the one the game last named (a switch at the character screen), and the
    /// next identity names them.
    /// </summary>
    private void ApplyProgress(GameEvent e)
    {
        if (e is SelfIdentityEvent { Name.Length: > 0 } named && Progress.Character != named.Name)
        {
            if (Progress.Character is not null)
            {
                StartProgress();
            }

            Progress.Character = named.Name;
            if (_progressBefore is { } before && before.Character == named.Name)
            {
                Progress.ContinueSession(before); // the same character logged in again
            }

            _progressBefore = null;
        }

        if (e is KinahLimitEvent { Initial: true } or ContentTicketsEvent or PetRosterEvent or InventoryListEvent
            && Progress.Character is not null && !(_named is { } n && n.Id == Registry.SelfId))
        {
            StartProgress();
        }

        if (_soulItems.Apply(e) is { } learned)
        {
            Progress.SoulItems[learned.Pet] = learned.Item;
        }

        Progress.Apply(e);
    }

    /// <summary>The progress before the last start over, until the game names whose the new one is.</summary>
    private CharacterProgress? _progressBefore;

    private void StartProgress()
    {
        _progressBefore = Progress.Character is not null ? Progress : _progressBefore;
        Progress = new CharacterProgress { SoulItems = new Dictionary<uint, uint>(Progress.SoulItems) };
    }

    /// <summary>The running encounter, or the last finished one.</summary>
    public MeterSnapshot? Snapshot(MeterScope scope = MeterScope.MyTargets, long? nowMs = null)
    {
        if (Tracker.Current is { } current)
        {
            var snapshot = MeterView.Build(current, Registry, scope, nowMs);
            if (snapshot.TargetId is { } target && Casts.Of(target, nowMs ?? current.LastMs) is { } cast)
            {
                string skill = Registry.Data.SkillName(cast.SkillId);
                bool named = !skill.StartsWith("Skill ", StringComparison.Ordinal) && skill.Trim('?', ' ').Length > 0;
                snapshot = snapshot with
                {
                    BossCast = new BossCastInfo(named ? skill : null, cast.StartMs, cast.EndMs, cast.StoppedMs, cast.Interrupted),
                };
            }

            return snapshot;
        }

        return Tracker.History.LastOrDefault()?.Final;
    }
}
