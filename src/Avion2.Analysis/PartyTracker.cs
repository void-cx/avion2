using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>A member of our party as the UI shows it.</summary>
/// <param name="EntityId">The member's entity id where we are, 0 when they are elsewhere or not known.</param>
/// <param name="Hp">HP and max HP; 0/0 while not known (the member is in another zone, or no record came yet).</param>
/// <param name="Online">False after the member dropped off the server, until the party says otherwise.</param>
/// <param name="Status">Map, position, levels and combat power from the latest party record, if any.</param>
/// <param name="StatusAgeMs">Since <paramref name="Status"/> came.</param>
public sealed record PartyMemberView(
    uint CharacterId,
    uint EntityId,
    string Name,
    GameClass Class,
    bool IsSelf,
    bool IsLeader,
    uint Hp,
    uint MaxHp,
    bool Alive,
    bool Online,
    PartyMemberStatus? Status,
    long StatusAgeMs)
{
    /// <summary>HP as a fraction of max HP, null while not known.</summary>
    public double? HpRatio => MaxHp > 0 ? Math.Clamp((double)Hp / MaxHp, 0, 1) : null;
}

/// <summary>
/// Who is in our party right now, from the party records (<c>00 92</c> list, <c>0D 92</c> join, <c>22 92</c>
/// reconnect, <c>21 92</c> disconnect, <c>11 92</c> leader, <c>1F 92</c> disband, <c>1F 93</c> we left), with each
/// member's HP (<c>1B 92</c>, also out of view), deaths and map position (<c>1C 92</c>, also on other maps). The
/// group-finder room (<c>02 97</c>) is not the party and is ignored. Not thread-safe (driven by <see cref="GameSession"/>).
/// </summary>
public sealed class PartyTracker
{
    private sealed class Member
    {
        public required uint CharacterId { get; init; }
        public string Name { get; set; } = string.Empty;
        public GameClass Class { get; set; }
        public byte Slot { get; set; }
        public uint EntityId { get; set; }
        public uint Hp { get; set; }
        public uint MaxHp { get; set; }
        public bool Alive { get; set; } = true;
        public bool Online { get; set; } = true;
        public PartyMemberStatus? Status { get; set; }
        public long StatusMs { get; set; }
    }

    /// <summary>In slot order.</summary>
    private readonly List<Member> _members = new();
    private uint? _leader;
    private string? _selfName;
    private long _nowMs;

    /// <summary>True while the party has at least one member besides us.</summary>
    public bool HasParty => _members.Count > 1 || _members.Count == 1 && _members[0].Name != _selfName;

    public void Apply(GameEvent e, EntityRegistry registry)
    {
        _nowMs = Math.Max(_nowMs, e.TimestampMs);
        switch (e)
        {
            case PartyRosterEvent { LeaderId: { } leader } roster:
                // The whole party (the finder room's roster has no leader and is not the party)
                var kept = new List<Member>(roster.Members.Count);
                foreach (var record in roster.Members)
                {
                    var member = Find(record.CharacterId) ?? new Member { CharacterId = record.CharacterId };
                    Fill(member, record, roster.TimestampMs);
                    kept.Add(member);
                }

                _members.Clear();
                _members.AddRange(kept.OrderBy(m => m.Slot));
                _leader = leader;
                break;
            case PartyMemberJoinedEvent joined:
            {
                var member = Find(joined.Member.CharacterId);
                if (member is null)
                {
                    member = new Member { CharacterId = joined.Member.CharacterId };
                    _members.Add(member);
                }

                Fill(member, joined.Member, joined.TimestampMs);
                _members.Sort((a, b) => a.Slot.CompareTo(b.Slot));
                break;
            }
            case PartyMemberDisconnectedEvent gone when Find(gone.CharacterId) is { } member:
                // Leaving or zoning into an instance: a party record or reconnect follows with the new state
                member.Online = false;
                member.EntityId = 0;
                member.Hp = member.MaxHp = 0;
                break;
            case PartyLeaderEvent leader:
                _leader = leader.CharacterId;
                break;
            case PartyDisbandedEvent or PartyLeftEvent:
                Clear();
                break;
            case SelfIdentityEvent { Name.Length: > 0 } self:
                if (_selfName is not null && _selfName != self.Name)
                {
                    Clear(); // another character: its party (if any) comes next
                }

                _selfName = self.Name;
                break;
            case HpUpdateEvent hp when hp.EntityId != 0 && FindEntity(hp.EntityId) is { } member:
                if (hp.MaxHp > 0)
                {
                    member.MaxHp = hp.MaxHp;
                    member.Hp = Math.Min(hp.Hp, hp.MaxHp);
                }
                else if (member.MaxHp > 0)
                {
                    member.Hp = Math.Min(hp.Hp, member.MaxHp);
                }

                member.Alive = hp.Alive ?? hp.Hp > 0;
                break;
            case DeathEvent { Kind: DeathKind.Combat or DeathKind.LoadedDead } death when FindEntity(death.EntityId) is { } member &&
                                                                                           IsThem(member, death.EntityId, registry):
                member.Alive = false;
                member.Hp = 0;
                break;
            case PlayerSpawnEvent spawn when _members.FirstOrDefault(m => m.Name == spawn.Name) is { } member:
                // A member who came into view with an entity id the party records did not give us (yet)
                member.EntityId = spawn.EntityId;
                if (spawn.Class != GameClass.Unknown)
                {
                    member.Class = spawn.Class;
                }

                break;
            case PartyMemberStatusEvent status when Find(status.CharacterId) is { } member:
                member.Status = status.Status;
                member.StatusMs = status.TimestampMs;
                if (status.Class != GameClass.Unknown)
                {
                    member.Class = status.Class;
                }

                break;
        }
    }

    /// <summary>Our party in slot order, us included; empty when we are not in one.</summary>
    public IReadOnlyList<PartyMemberView> Snapshot(EntityRegistry registry)
    {
        if (!HasParty)
        {
            return [];
        }

        return _members.Select(m => new PartyMemberView(
            m.CharacterId,
            m.EntityId,
            m.Name,
            m.Class,
            IsSelf(m, registry),
            m.CharacterId == _leader,
            m.Hp,
            m.MaxHp,
            m.Alive,
            m.Online,
            m.Status,
            m.Status is null ? 0 : Math.Max(0, _nowMs - m.StatusMs))).ToList();
    }

    /// <summary>
    /// <paramref name="live"/> (everyone in view on <paramref name="mapId"/>) with our party members marked, plus the
    /// members on that map who are out of view, at the position their party record gave.
    /// </summary>
    public IReadOnlyList<LiveEntity> OnMap(uint? mapId, IReadOnlyList<LiveEntity> live, EntityRegistry registry)
    {
        var others = Snapshot(registry).Where(m => !m.IsSelf).ToList();
        if (others.Count == 0)
        {
            return live;
        }

        var byEntity = new Dictionary<uint, PartyMemberView>();
        foreach (var member in others.Where(m => m.EntityId != 0))
        {
            byEntity[member.EntityId] = member;
        }

        var result = new List<LiveEntity>(live.Count + others.Count);
        var shown = new HashSet<uint>();
        foreach (var e in live)
        {
            if (e.Kind != LiveKind.Player || !byEntity.TryGetValue(e.Id, out var member) || !Here(member, mapId))
            {
                result.Add(e);
                continue;
            }

            shown.Add(member.CharacterId);
            var marked = e with { Party = true, Dead = !member.Alive };
            // The party record can be fresher than the last movement we saw
            if (OnThisMap(member, mapId) is { } at && member.StatusAgeMs < e.AgeMs)
            {
                marked = marked with { Position = at, AgeMs = member.StatusAgeMs };
            }

            result.Add(marked);
        }

        if (mapId is { } map)
        {
            result.AddRange(Markers(others.Where(m => !shown.Contains(m.CharacterId)), map));
        }

        return result;
    }

    /// <summary>
    /// The members on <paramref name="mapId"/> at the position their last party record gave (also when we are on another
    /// map), for the map.
    /// </summary>
    public static IEnumerable<LiveEntity> Markers(IEnumerable<PartyMemberView> members, uint mapId) =>
        members
            .Where(m => !m.IsSelf && OnThisMap(m, mapId) is not null)
            .Select(m => new LiveEntity(m.EntityId != 0 ? m.EntityId : OutOfViewId(m.CharacterId), m.Name, LiveKind.Player, m.Class,
                m.Status!.Position, m.StatusAgeMs, !m.Alive, Party: true));

    /// <summary>An id for a member we know no entity id of; the high bit keeps it apart from entity ids.</summary>
    public static uint OutOfViewId(uint characterId) => characterId | 0x8000_0000;

    /// <summary>
    /// Not known to be elsewhere: entity ids are reissued when we change maps, so a member whose last record puts them
    /// on another map is not the entity with their old id here.
    /// </summary>
    private static bool Here(PartyMemberView member, uint? mapId) =>
        member.Status is not { MapId: not 0 } status || status.MapId == mapId;

    /// <summary>The entity is the member, as far as the registry knows (ids are reissued after map changes).</summary>
    private static bool IsThem(Member member, uint entityId, EntityRegistry registry) =>
        registry.Find(entityId) is not { Name: { Length: > 0 } name } || name == member.Name;

    private static Vec3? OnThisMap(PartyMemberView member, uint? mapId) =>
        member.Status is { } status && mapId is { } map && status.MapId == map && status.Position.IsWorldPoint ? status.Position : null;

    private bool IsSelf(Member m, EntityRegistry registry) =>
        m.EntityId != 0 && m.EntityId == registry.SelfId || (registry.SelfName ?? _selfName) is { } me && m.Name == me;

    private Member? Find(uint characterId) => _members.FirstOrDefault(m => m.CharacterId == characterId);

    private Member? FindEntity(uint entityId) => _members.FirstOrDefault(m => m.EntityId == entityId);

    private static void Fill(Member member, PartyMember record, long ms)
    {
        member.Name = record.Name;
        member.Slot = record.Slot;
        member.EntityId = record.EntityId;
        member.Online = record.Online ?? true;
        member.Alive = record.Alive;
        if (record.Class != GameClass.Unknown)
        {
            member.Class = record.Class;
        }

        // 0/0 while the member is in another zone: unknown, not dead
        member.Hp = record.MaxHp > 0 ? Math.Min(record.Hp, record.MaxHp) : 0;
        member.MaxHp = record.MaxHp;
        if (record.Status is { } status)
        {
            member.Status = status;
            member.StatusMs = ms;
        }
    }

    private void Clear()
    {
        _members.Clear();
        _leader = null;
    }
}
