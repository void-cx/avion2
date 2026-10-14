using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>A position sample; <paramref name="T"/> is ms since the trace start, coordinates in world units (≈ cm).</summary>
public sealed record TracePoint(int T, int X, int Y, int Z, PositionSource Source);

public enum TraceEntityKind
{
    Self,
    Player,
    Boss,
    Monster,
    Summon,
}

/// <param name="OwnerId">For summons/effects: the player they belong to.</param>
public sealed record TraceEntity(
    uint Id,
    string Name,
    TraceEntityKind Kind,
    GameClass Class,
    string? Legion,
    uint? OwnerId,
    uint MaxHp,
    List<TracePoint> Track);

public sealed record TraceCast(int T, uint ActorId, uint SkillId, uint? TargetId);

public sealed record TraceHit(int T, uint ActorId, uint TargetId, uint SkillId, long Amount, bool Critical);

/// <summary>An abnormal on an entity from <paramref name="Start"/> to <paramref name="End"/> (ms, clipped to the trace).</summary>
public sealed record TraceBuff(uint EntityId, uint AbnormalId, uint? SkillId, uint CasterId, int Start, int End);

public sealed record TraceHp(int T, uint EntityId, uint Hp);

/// <summary>The game server went silent from <paramref name="Start"/> for <paramref name="Length"/> ms (then caught up).</summary>
public sealed record TraceLag(int Start, int Length);

/// <summary>An entity died (or left the world) at <paramref name="T"/>.</summary>
public sealed record TraceDeath(int T, uint EntityId);

public sealed record BuffUptime(string Name, uint AbnormalId, int Applications, double Uptime, string Caster);

/// <summary>
/// Everything that happened around one fight, for the timeline, buff uptime and the replay.
/// Built by <see cref="TraceRecorder"/> when an encounter ends; saved next to the fight's snapshot.
/// </summary>
public sealed class FightTrace
{
    public long StartMs { get; init; }

    public int DurationMs { get; init; }

    public string Title { get; init; } = string.Empty;

    public uint? SelfId { get; init; }

    public uint? PrimaryTargetId { get; init; }

    public uint? MapId { get; init; }

    public List<TraceEntity> Entities { get; init; } = new();

    public List<TraceCast> Casts { get; init; } = new();

    public List<TraceHit> Hits { get; init; } = new();

    public List<TraceBuff> Buffs { get; init; } = new();

    public List<TraceHp> Hp { get; init; } = new();

    /// <summary>Deaths during the fight (monsters disappear from the replay at these).</summary>
    public List<TraceDeath> Deaths { get; init; } = new();

    /// <summary>Dead at <paramref name="t"/>: died before it, and not seen moving since (ids can come back).</summary>
    public bool IsDeadAt(uint id, int t)
    {
        int died = int.MinValue;
        foreach (var death in Deaths)
        {
            if (death.EntityId == id && death.T <= t)
            {
                died = Math.Max(died, death.T);
            }
        }

        return died != int.MinValue && Entity(id)?.Track.Any(p => p.T > died && p.T <= t) != true;
    }

    /// <summary>Ms between <paramref name="t"/> and the closest real position fix of the entity; null without fixes.</summary>
    public int? FixAgeMs(uint id, int t)
    {
        var track = Entity(id)?.Track;
        return track is { Count: > 0 } ? track.Min(p => Math.Abs(p.T - t)) : null;
    }

    /// <summary>The entity's last cast at or before <paramref name="t"/>.</summary>
    public TraceCast? LastCast(uint id, int t) => Casts.LastOrDefault(c => c.ActorId == id && c.T <= t);

    /// <summary>Server freezes (no game data for more than a second) during the fight.</summary>
    public List<TraceLag> Lags { get; init; } = new();

    /// <summary>Typical (median) and 95th-percentile delivery delay of the server clock, in ms above the best case.</summary>
    public int? DelayMs { get; init; }

    public int? DelayP95Ms { get; init; }

    /// <summary>"Connection 17 ms · p95 30 ms · 1 server freeze (7.0 s)", or null without clock data.</summary>
    public string? NetSummary
    {
        get
        {
            if (DelayMs is not { } typical)
            {
                return null;
            }

            string freezes = Lags.Count switch
            {
                0 => "no server freezes",
                1 => $"1 server freeze ({Lags[0].Length / 1000.0:0.0} s)",
                _ => $"{Lags.Count} server freezes (worst {Lags.Max(l => l.Length) / 1000.0:0.0} s)",
            };
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Connection: +{typical} ms typical, +{DelayP95Ms} ms p95 · {freezes}");
        }
    }

    /// <summary>The freeze covering <paramref name="t"/>, if any.</summary>
    public TraceLag? LagAt(int t) => Lags.FirstOrDefault(l => t >= l.Start && t < l.Start + l.Length);

    public TraceEntity? Entity(uint id) => Entities.FirstOrDefault(e => e.Id == id);

    /// <summary>The player an entity counts for (summons → owner).</summary>
    public uint OwnerOf(uint id) => Entity(id)?.OwnerId ?? id;

    /// <summary>
    /// Where an entity was at <paramref name="t"/>, interpolated between fixes. <c>Estimated</c> is true when the
    /// nearest fix is more than 3 s away (typical for ourselves: the server does not echo our movement).
    /// </summary>
    public (float X, float Y, bool Estimated)? PositionAt(uint id, int t)
    {
        var track = Entity(id)?.Track;
        if (track is not { Count: > 0 })
        {
            return null;
        }

        int index = track.FindLastIndex(p => p.T <= t);
        if (index < 0)
        {
            var first = track[0];
            return (first.X, first.Y, first.T - t > 3000);
        }

        var a = track[index];
        if (index + 1 >= track.Count)
        {
            return (a.X, a.Y, t - a.T > 3000);
        }

        var b = track[index + 1];
        float f = b.T == a.T ? 0 : (t - a.T) / (float)(b.T - a.T);
        bool estimated = Math.Min(t - a.T, b.T - t) > 3000;
        return (a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f, estimated);
    }

    /// <summary>The last known HP of an entity at <paramref name="t"/>.</summary>
    public uint? HpAt(uint id, int t)
    {
        uint? hp = null;
        foreach (var sample in Hp)
        {
            if (sample.T > t)
            {
                break;
            }

            if (sample.EntityId == id)
            {
                hp = sample.Hp;
            }
        }

        return hp;
    }

    /// <summary>Buffs and debuffs on <paramref name="entityId"/>, by share of the fight they were up.</summary>
    public List<BuffUptime> Uptime(uint entityId, GameData data)
    {
        int length = Math.Max(DurationMs, 1);
        return Buffs
            .Where(b => b.EntityId == entityId)
            .GroupBy(b => BuffName(b, data))
            .Select(g =>
            {
                // Merge overlapping instances of the same abnormal before measuring.
                long covered = 0;
                int end = int.MinValue;
                foreach (var b in g.OrderBy(b => b.Start))
                {
                    int start = Math.Max(Math.Max(b.Start, 0), end);
                    int stop = Math.Min(b.End, DurationMs);
                    if (stop > start)
                    {
                        covered += stop - start;
                    }

                    end = Math.Max(end, stop);
                }

                string caster = string.Join(", ", g.Select(b => b.CasterId).Distinct()
                    .Select(c => c == entityId ? "self" : Entity(OwnerOf(c))?.Name ?? "?").Distinct().Take(3));
                return new BuffUptime(g.Key, g.First().AbnormalId, g.Count(), Math.Min(1.0, covered / (double)length), caster);
            })
            .Where(u => u.Uptime > 0)
            .OrderByDescending(u => u.Uptime)
            .ToList();
    }

    public static string BuffName(TraceBuff buff, GameData data)
    {
        if (buff.SkillId is { } skill && data.Skills.Count > 0)
        {
            string name = data.SkillName(skill - skill % 10_000);
            if (!name.StartsWith("Skill ", StringComparison.Ordinal))
            {
                return name;
            }
        }

        return AbnormalName(buff.AbnormalId, data);
    }

    /// <summary>
    /// Abnormal ids are skill ids with one or two extra digits (e.g. 141700071 → 14170007 → 14170000). Small ids
    /// (basic actions such as 200 Flight) only match exactly: their neighbours are unrelated.
    /// </summary>
    public static string AbnormalName(uint abnormal, GameData data)
    {
        if (abnormal < 1_000_000)
        {
            return data.Skills.TryGetValue(abnormal, out var basic) ? basic : $"Effect {abnormal}";
        }

        foreach (uint candidate in new[] { abnormal, abnormal / 10, abnormal / 100 })
        {
            if (data.Skills.TryGetValue(candidate, out var exact))
            {
                return exact;
            }

            if (candidate >= 10_000 && data.Skills.TryGetValue(candidate - candidate % 10_000, out var name))
            {
                return name;
            }
        }

        return $"Effect {abnormal}";
    }
}
