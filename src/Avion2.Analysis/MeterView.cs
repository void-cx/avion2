using Avion2.Protocol;

namespace Avion2.Analysis;

public sealed record SkillRow(uint SkillId, string Name, long Total, double Share, int Hits, double CritRate, long MaxHit, bool IsOverTime)
{
    public double BackRate { get; init; }
    public double DoubleRate { get; init; }
    public double PerfectRate { get; init; }
    public double ParryRate { get; init; }
    public double MultiHitRate { get; init; }
    public long MultiHitDamage { get; init; }
    public long MinHit { get; init; }
    public double AverageHit => Hits == 0 ? 0 : (double)Total / Hits;

    /// <summary>Bit n = specialisation slot n (1–5).</summary>
    public int SpecSlots { get; init; }
}

public sealed record MeterRow(
    uint ActorId,
    string Name,
    GameClass Class,
    bool IsSelf,
    long Total,
    double Dps,
    double Share,
    double CritRate,
    double BackRate,
    int Hits,
    int Perfects,
    int Doubles,
    long Healing,
    long DamageTaken,
    IReadOnlyList<SkillRow> Skills)
{
    public int Parried { get; init; }
    public int MultiHits { get; init; }
    public int FrontHits { get; init; }
    public uint? CombatPower { get; init; }

    public double FrontRate => Hits == 0 ? 0 : (double)FrontHits / Hits;

    public double DoubleRate => Hits == 0 ? 0 : (double)Doubles / Hits;
    public double PerfectRate => Hits == 0 ? 0 : (double)Perfects / Hits;
    public double ParryRate => Hits == 0 ? 0 : (double)Parried / Hits;
    public double MultiHitRate => Hits == 0 ? 0 : (double)MultiHits / Hits;
}

public sealed record IncomingSourceRow(string Attacker, string Skill, long Total, double Share, int Hits, int Crits, long MaxHit);

public sealed record IncomingRow(
    uint VictimId,
    string Name,
    GameClass Class,
    bool IsSelf,
    long Total,
    double Dtps,
    double Share,
    int Hits,
    int Crits,
    int Parried,
    int Blocked,
    int PerfectBlocks,
    int Shielded,
    int Misses,
    IReadOnlyList<IncomingSourceRow> Sources,
    long Absorbed = 0);

/// <param name="StartedAtMs">Unix ms of the encounter start.</param>
/// <param name="Incoming">Damage taken from monsters, per player.</param>
public sealed record MeterSnapshot(
    int EncounterId,
    string Title,
    double DurationSeconds,
    long TotalDamage,
    double TotalDps,
    uint? TargetHp,
    uint? TargetMaxHp,
    IReadOnlyList<MeterRow> Rows,
    long StartedAtMs = 0,
    EncounterEnd End = EncounterEnd.Ongoing,
    IReadOnlyList<IncomingRow>? Incoming = null)
{
    public IReadOnlyList<IncomingRow> Incoming { get; init; } = Incoming ?? [];

    public MeterRow? Self => Rows.FirstOrDefault(r => r.IsSelf);
}

public enum MeterScope
{
    /// <summary>Only targets we damaged ourselves (like the game's per-target metric). Falls back to all.</summary>
    MyTargets,

    /// <summary>Every target anyone damaged during the encounter.</summary>
    AllTargets,
}

/// <summary>Turns tracker state into display rows (sorted by damage). UI-agnostic.</summary>
public static class MeterView
{
    /// <param name="nowMs">For a running encounter: lets the clock keep ticking while we are in combat.</param>
    public static MeterSnapshot Build(Encounter encounter, EntityRegistry registry, MeterScope scope = MeterScope.MyTargets, long? nowMs = null)
    {
        var data = registry.Data;
        uint? self = registry.SelfId ?? encounter.SelfId;

        var targets = encounter.Targets.Values.ToList();
        if (scope == MeterScope.MyTargets && self is { } me)
        {
            var mine = targets.Where(t => t.Actors.Keys.Any(a => registry.ResolveOwner(a) == me)).ToList();
            if (mine.Count > 0)
            {
                targets = mine;
            }
        }

        // Merge per-target, per-raw-actor stats into rows keyed by the resolved owner.
        var merged = new Dictionary<uint, ActorStats>();
        foreach (var target in targets)
        {
            foreach (var (actorId, stats) in target.Actors)
            {
                uint owner = registry.ResolveOwner(actorId);
                if (!merged.TryGetValue(owner, out var row))
                {
                    row = new ActorStats(owner);
                    merged[owner] = row;
                }

                row.Merge(stats);
            }
        }

        var support = new Dictionary<uint, SupportStats>();
        foreach (var (actorId, stats) in encounter.Support)
        {
            uint owner = registry.ResolveOwner(actorId);
            if (!support.TryGetValue(owner, out var row))
            {
                row = new SupportStats();
                support[owner] = row;
            }

            row.Healing += stats.Healing;
            row.DamageTaken += stats.DamageTaken;
        }

        long first = targets.Count == 0 ? encounter.StartMs : targets.Min(t => t.FirstMs);
        long last = targets.Count == 0 ? encounter.LastMs : targets.Max(t => t.LastMs);

        // Match the game's Combat Analysis clock: our combat-on to combat-off, while it brackets the hits.
        if (encounter.CombatStartMs is { } combatStart && combatStart <= first && first - combatStart <= 10_000)
        {
            first = combatStart;
        }

        if (encounter.CombatEndMs is { } combatEnd && combatEnd >= last && combatEnd - last <= 10_000)
        {
            last = combatEnd;
        }
        else if (encounter.SelfInCombat && nowMs is { } now && now > last)
        {
            last = Math.Min(now, last + 10_000);
        }

        double seconds = Math.Max(last - first, 1000) / 1000.0;
        long total = targets.Sum(t => t.Total);

        var primary = targets
            .OrderByDescending(t => registry.IsBoss(t.TargetId))
            .ThenByDescending(t => t.Total)
            .FirstOrDefault();
        var targetInfo = primary is null ? null : registry.Find(primary.TargetId);
        string title = primary is null ? "Combat" : registry.TargetName(primary.TargetId);
        if (targets.Count > 1)
        {
            title += $" (+{targets.Count - 1})";
        }

        var rows = new List<MeterRow>();
        foreach (var actor in merged.Values)
        {
            var help = support.GetValueOrDefault(actor.ActorId);
            if (actor.Total <= 0)
            {
                continue;
            }

            var info = registry.Find(actor.ActorId);
            bool isSelf = actor.ActorId == self;
            if (!isSelf && info?.Kind is EntityKind.Npc)
            {
                continue;
            }

            var skills = actor.Skills.Values
                .OrderByDescending(s => s.Total)
                .Select(s => new SkillRow(
                    s.SkillId,
                    data.SkillName(s.SkillId),
                    s.Total,
                    actor.Total == 0 ? 0 : (double)s.Total / actor.Total,
                    s.Hits,
                    s.Hits == 0 || s.IsOverTime ? 0 : (double)s.Crits / s.Hits,
                    s.MaxHit,
                    s.IsOverTime)
                {
                    BackRate = Rate(s.BackHits, s.Hits),
                    DoubleRate = Rate(s.Doubles, s.Hits),
                    PerfectRate = Rate(s.Perfects, s.Hits),
                    ParryRate = Rate(s.Parried, s.Hits),
                    MultiHitRate = Rate(s.MultiHits, s.Hits),
                    MultiHitDamage = s.MultiHitDamage,
                    MinHit = s.MinHit,
                    SpecSlots = s.SpecSlots,
                })
                .ToList();

            rows.Add(new MeterRow(
                actor.ActorId,
                isSelf ? registry.SelfName ?? registry.DisplayName(actor.ActorId) : registry.DisplayName(actor.ActorId),
                info?.Class is { } cls and not GameClass.Unknown ? cls : (isSelf ? registry.SelfClass : GameClass.Unknown),
                isSelf,
                actor.Total,
                actor.Total / seconds,
                total == 0 ? 0 : (double)actor.Total / total,
                actor.CritRate,
                actor.BackRate,
                actor.Hits,
                actor.Perfects,
                actor.Doubles,
                help?.Healing ?? 0,
                help?.DamageTaken ?? 0,
                skills)
            {
                Parried = actor.Parried,
                MultiHits = actor.MultiHits,
                FrontHits = actor.FrontHits,
                CombatPower = info?.CombatPower,
            });
        }

        rows.Sort((a, b) => b.Total.CompareTo(a.Total));
        return new MeterSnapshot(
            encounter.Id,
            title,
            seconds,
            total,
            total / seconds,
            targetInfo is { MaxHp: > 0 } ? targetInfo.Hp : null,
            targetInfo is { MaxHp: > 0 } ? targetInfo.MaxHp : null,
            rows,
            encounter.StartMs,
            encounter.End,
            BuildIncoming(encounter, registry, self, seconds));
    }

    private static double Rate(int count, int total) => total == 0 ? 0 : (double)count / total;

    private static List<IncomingRow> BuildIncoming(Encounter encounter, EntityRegistry registry, uint? self, double seconds)
    {
        var data = registry.Data;
        long total = encounter.TotalTaken;
        var rows = new List<IncomingRow>();
        foreach (var stats in encounter.Incoming.Values)
        {
            if (stats.Total <= 0 && stats.Misses == 0 && stats.Absorbed == 0)
            {
                continue;
            }

            // Same monster type and skill merge into one line ("Highland Odyle Sparkie — Bite").
            var sources = stats.Sources.Values
                .GroupBy(s => (Attacker: registry.TargetName(s.AttackerId, "Unknown monster"), s.SkillId))
                .Select(g => new IncomingSourceRow(
                    g.Key.Attacker,
                    data.SkillName(g.Key.SkillId),
                    g.Sum(s => s.Total),
                    stats.Total == 0 ? 0 : (double)g.Sum(s => s.Total) / stats.Total,
                    g.Sum(s => s.Hits),
                    g.Sum(s => s.Crits),
                    g.Max(s => s.MaxHit)))
                .OrderByDescending(s => s.Total)
                .ToList();

            bool isSelf = stats.VictimId == self;
            var info = registry.Find(stats.VictimId);
            rows.Add(new IncomingRow(
                stats.VictimId,
                isSelf ? registry.SelfName ?? registry.DisplayName(stats.VictimId) : registry.DisplayName(stats.VictimId),
                info?.Class is { } cls and not GameClass.Unknown ? cls : (isSelf ? registry.SelfClass : GameClass.Unknown),
                isSelf,
                stats.Total,
                stats.Total / seconds,
                total == 0 ? 0 : (double)stats.Total / total,
                stats.Hits,
                stats.Crits,
                stats.Parried,
                stats.Blocked,
                stats.PerfectBlocks,
                stats.Shielded,
                stats.Misses,
                sources,
                stats.Absorbed));
        }

        rows.Sort((a, b) => b.Total.CompareTo(a.Total));
        return rows;
    }
}
