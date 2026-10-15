using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>
/// Keeps the last minutes of world events (positions, casts, hits, buffs, HP) and cuts a <see cref="FightTrace"/>
/// out of them for an encounter. Not thread-safe (driven by <see cref="GameSession"/>).
/// </summary>
public sealed class TraceRecorder
{
    private const long KeepMs = 20 * 60_000;
    private const long LeadInMs = 2_000;
    private const long TailMs = 1_500;
    private const int MinPointSpacingMs = 200;

    private readonly List<PositionEvent> _positions = new();
    private readonly List<SkillCastEvent> _casts = new();
    private readonly List<DamageEvent> _hits = new();
    private readonly List<HealEvent> _heals = new();
    private readonly List<GameEvent> _buffs = new();
    private readonly List<HpUpdateEvent> _hp = new();
    private readonly List<ServerClockEvent> _clock = new();
    private readonly List<DeathEvent> _deaths = new();

    /// <summary>A gap this long between server ticks (normally 50 ms) counts as a freeze.</summary>
    public const int FreezeMs = 1_000;
    private long _lastPruneMs;

    public void Apply(GameEvent e)
    {
        switch (e)
        {
            case PositionEvent position:
                _positions.Add(position);
                break;
            case SkillCastEvent cast:
                _casts.Add(cast);
                break;
            case DamageEvent damage:
                _hits.Add(damage);
                break;
            case HealEvent heal:
                _heals.Add(heal);
                break;
            case BuffEvent or BuffRemovedEvent:
                _buffs.Add(e);
                break;
            case HpUpdateEvent hp:
                _hp.Add(hp);
                break;
            case ServerClockEvent clock:
                _clock.Add(clock);
                break;
            case DeathEvent death:
                _deaths.Add(death);
                break;
            case ZoneChangeEvent:
                // Entity ids are reissued: older events would point at the wrong entities.
                Clear();
                return;
        }

        if (e.TimestampMs - _lastPruneMs > 10_000)
        {
            Prune(e.TimestampMs - KeepMs);
            _lastPruneMs = e.TimestampMs;
        }
    }

    public void Clear()
    {
        _positions.Clear();
        _casts.Clear();
        _hits.Clear();
        _heals.Clear();
        _buffs.Clear();
        _hp.Clear();
        _clock.Clear();
        _deaths.Clear();
    }

    /// <summary>The trace of <paramref name="encounter"/> (finished or running) with names as the registry knows them now.</summary>
    public FightTrace Build(Encounter encounter, EntityRegistry registry, string title)
    {
        long start = (encounter.CombatStartMs is { } combat ? Math.Min(combat, encounter.StartMs) : encounter.StartMs) - LeadInMs;
        long end = Math.Max(encounter.CombatEndMs ?? encounter.LastMs, encounter.LastMs) + TailMs;
        uint? self = encounter.SelfId ?? registry.SelfId;

        var ids = Participants(encounter, self);
        // Summons and effects of participants, and whoever buffed or healed them.
        foreach (var hit in InWindow(_hits, start, end))
        {
            if (ids.Contains(registry.ResolveOwner(hit.ActorId)))
            {
                ids.Add(hit.ActorId);
            }
        }

        foreach (var cast in InWindow(_casts, start, end))
        {
            if (cast.TargetId is { } target && ids.Contains(target) && registry.Find(cast.ActorId)?.Kind == EntityKind.Player)
            {
                ids.Add(cast.ActorId);
            }
        }

        int T(long ms) => (int)(ms - start);
        var delays = Delays(start, end);

        var entities = ids.Select(id => Describe(id, registry, self, InTrack(id, start, end, T))).ToList();
        var primary = encounter.Targets.Values.OrderByDescending(t => t.Total).FirstOrDefault()?.TargetId;

        return new FightTrace
        {
            StartMs = start,
            DurationMs = (int)(end - start),
            Title = title,
            SelfId = self,
            PrimaryTargetId = primary,
            MapId = registry.MapId,
            Entities = entities,
            Casts = InWindow(_casts, start, end)
                .Where(c => ids.Contains(c.ActorId))
                .Select(c => new TraceCast(T(c.TimestampMs), c.ActorId, c.SkillId, c.TargetId, c.Facing))
                .ToList(),
            Hits = InWindow(_hits, start, end)
                .Where(h => ids.Contains(h.ActorId) || ids.Contains(h.TargetId))
                .Select(h => new TraceHit(T(h.TimestampMs), h.ActorId, h.TargetId, h.SkillId, h.Amount, (h.Flags & HitFlags.Critical) != 0,
                    (h.Flags & HitFlags.Back) != 0))
                .ToList(),
            Heals = InWindow(_heals, start, end)
                .Where(h => h.Amount > 0 && (ids.Contains(h.TargetId) || ids.Contains(registry.ResolveOwner(h.ActorId))))
                .Select(h => new TraceHeal(T(h.TimestampMs), h.ActorId, h.TargetId, h.SkillId, h.Amount))
                .ToList(),
            Buffs = BuffIntervals(ids, start, end, T),
            Hp = InWindow(_hp, start, end)
                .Where(h => ids.Contains(h.EntityId))
                .Select(h => new TraceHp(T(h.TimestampMs), h.EntityId, h.Hp))
                .ToList(),
            Lags = Freezes(start, end, T),
            Deaths = InWindow(_deaths, start, end)
                .Where(d => ids.Contains(d.EntityId))
                .Select(d => new TraceDeath(T(d.TimestampMs), d.EntityId))
                .ToList(),
            DelayMs = delays.Count > 0 ? delays[delays.Count / 2] : null,
            DelayP95Ms = delays.Count > 0 ? delays[(int)(delays.Count * 0.95)] : null,
        };
    }

    /// <summary>
    /// Delivery delays (ms above the best case seen in the last minutes, so clock offsets cancel out), sorted.
    /// </summary>
    private List<int> Delays(long start, long end)
    {
        if (_clock.Count == 0)
        {
            return new();
        }

        long best = _clock.Min(c => c.TimestampMs - c.ServerMs);
        return InWindow(_clock, start, end).Select(c => (int)(c.TimestampMs - c.ServerMs - best)).Order().ToList();
    }

    /// <summary>Gaps of more than <see cref="FreezeMs"/> between consecutive server ticks.</summary>
    private List<TraceLag> Freezes(long start, long end, Func<long, int> t)
    {
        var lags = new List<TraceLag>();
        ServerClockEvent? previous = null;
        foreach (var tick in _clock)
        {
            if (previous is not null && tick.TimestampMs >= start && previous.TimestampMs <= end &&
                tick.TimestampMs - previous.TimestampMs > FreezeMs)
            {
                lags.Add(new TraceLag(Math.Max(t(previous.TimestampMs), 0), (int)(tick.TimestampMs - previous.TimestampMs)));
            }

            previous = tick;
        }

        return lags;
    }

    private static HashSet<uint> Participants(Encounter encounter, uint? self)
    {
        var ids = new HashSet<uint>();
        foreach (var target in encounter.Targets.Values)
        {
            ids.Add(target.TargetId);
            ids.UnionWith(target.Actors.Keys);
        }

        foreach (var incoming in encounter.Incoming.Values)
        {
            ids.Add(incoming.VictimId);
            ids.UnionWith(incoming.Sources.Keys.Select(k => k.Attacker));
        }

        ids.UnionWith(encounter.Support.Keys);
        if (self is { } me)
        {
            ids.Add(me);
        }

        ids.Remove(0);
        return ids;
    }

    private TraceEntity Describe(uint id, EntityRegistry registry, uint? self, List<TracePoint> track)
    {
        var info = registry.Find(id);
        uint owner = registry.ResolveOwner(id);
        var kind = id == self ? TraceEntityKind.Self
            : owner != id ? TraceEntityKind.Summon
            : info?.IsBoss == true ? TraceEntityKind.Boss
            : info?.Kind == EntityKind.Player ? TraceEntityKind.Player
            : TraceEntityKind.Monster;
        string name = id == self ? registry.SelfName ?? registry.DisplayName(id) : registry.DisplayName(id);
        var cls = info?.Class is { } c and not GameClass.Unknown ? c : id == self ? registry.SelfClass : GameClass.Unknown;
        uint maxHp = id == self ? registry.SelfMaxHp : info?.MaxHp ?? 0;
        return new TraceEntity(id, name, kind, cls, info?.Legion, owner != id ? owner : null, maxHp, track);
    }

    /// <summary>Fixes inside the window (thinned to one per 200 ms) plus the last one before it, so everyone has a start.</summary>
    private List<TracePoint> InTrack(uint id, long start, long end, Func<long, int> t)
    {
        var points = new List<TracePoint>();
        PositionEvent? before = null;
        foreach (var p in _positions)
        {
            if (p.EntityId != id)
            {
                continue;
            }

            if (p.TimestampMs < start)
            {
                before = p;
                continue;
            }

            if (p.TimestampMs > end)
            {
                break;
            }

            if (points.Count > 0 && t(p.TimestampMs) - points[^1].T < MinPointSpacingMs)
            {
                points[^1] = Point(p, t);
                continue;
            }

            points.Add(Point(p, t));
        }

        if (before is not null)
        {
            points.Insert(0, Point(before, t) with { T = Math.Max(t(before.TimestampMs), -60_000) });
        }

        return points;
    }

    private static TracePoint Point(PositionEvent p, Func<long, int> t) =>
        new(t(p.TimestampMs), (int)p.Position.X, (int)p.Position.Y, (int)p.Position.Z, p.Source);

    /// <summary>Buff intervals overlapping the window: added → removed (or its duration, or the window end).</summary>
    private List<TraceBuff> BuffIntervals(HashSet<uint> ids, long start, long end, Func<long, int> t)
    {
        var open = new Dictionary<(uint Entity, uint Instance), (BuffEvent Buff, long From)>();
        var result = new List<TraceBuff>();

        void Close((uint, uint) key, long at)
        {
            var (buff, from) = open[key];
            open.Remove(key);
            long stop = buff.DurationMs is { } d ? Math.Min(at, from + d) : at;
            if (stop >= start && from <= end)
            {
                result.Add(new TraceBuff(buff.EntityId, buff.AbnormalId, buff.SkillId, buff.CasterId,
                    Math.Max(t(from), 0), Math.Min(t(stop), t(end))));
            }
        }

        foreach (var e in _buffs)
        {
            if (e.TimestampMs > end)
            {
                break;
            }

            switch (e)
            {
                case BuffEvent buff when ids.Contains(buff.EntityId):
                    var key = (buff.EntityId, buff.Instance);
                    if (buff.Added || !open.ContainsKey(key))
                    {
                        if (open.ContainsKey(key))
                        {
                            Close(key, buff.TimestampMs);
                        }

                        open[key] = (buff, buff.TimestampMs);
                    }
                    else
                    {
                        // Refreshed: the running interval continues with the new duration from now.
                        var (_, from) = open[key];
                        open[key] = (buff with { DurationMs = buff.DurationMs is { } d ? buff.TimestampMs - from + d : null }, from);
                    }

                    break;
                case BuffRemovedEvent removed when ids.Contains(removed.EntityId):
                    foreach (uint instance in removed.Instances)
                    {
                        if (open.ContainsKey((removed.EntityId, instance)))
                        {
                            Close((removed.EntityId, instance), removed.TimestampMs);
                        }
                    }

                    break;
            }
        }

        foreach (var key in open.Keys.ToList())
        {
            Close(key, end);
        }

        return result;
    }

    private static IEnumerable<T> InWindow<T>(List<T> events, long start, long end) where T : GameEvent =>
        events.Where(e => e.TimestampMs >= start && e.TimestampMs <= end);

    private void Prune(long before)
    {
        _positions.RemoveAll(e => e.TimestampMs < before);
        _casts.RemoveAll(e => e.TimestampMs < before);
        _hits.RemoveAll(e => e.TimestampMs < before);
        _heals.RemoveAll(e => e.TimestampMs < before);
        _buffs.RemoveAll(e => e.TimestampMs < before);
        _hp.RemoveAll(e => e.TimestampMs < before);
        _clock.RemoveAll(e => e.TimestampMs < before);
        _deaths.RemoveAll(e => e.TimestampMs < before);
    }
}
