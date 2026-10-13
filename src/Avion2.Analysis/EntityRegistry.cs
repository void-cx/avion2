using Avion2.Protocol;

namespace Avion2.Analysis;

public enum EntityKind
{
    Unknown,
    Player,
    Npc,
    Summon,
}

public sealed class EntityInfo(uint id)
{
    public uint Id { get; } = id;
    public EntityKind Kind { get; set; }
    public string? Name { get; set; }
    public ushort? ServerId { get; set; }
    public GameClass Class { get; set; }
    public uint? NpcCode { get; set; }
    public uint MaxHp { get; set; }
    public uint Hp { get; set; }
    public uint? OwnerId { get; set; }
    public string? OwnerName { get; set; }
    public bool IsBoss { get; set; }
    public bool IsDummy { get; set; }
}

/// <summary>
/// Tracks what we know about entity ids. Ids are session-scoped and reissued on zone change,
/// so the table is cleared then (our own name is kept to re-identify ourselves).
/// </summary>
public sealed class EntityRegistry
{
    private readonly Dictionary<uint, EntityInfo> _entities = new();
    private GameData _data;

    public EntityRegistry(GameData? data = null)
    {
        _data = data ?? GameData.Empty;
    }

    private readonly List<KnownCharacter> _knownCharacters = new();
    private string? _confirmedName;
    private GameClass _selfClass;

    public uint? SelfId { get; private set; }

    /// <summary>Our name: confirmed by an identity record, else guessed from characters seen in earlier sessions.</summary>
    public string? SelfName => _confirmedName ?? GuessCharacter()?.Name;

    /// <summary>True while <see cref="SelfName"/> comes from an earlier session rather than this one.</summary>
    public bool SelfNameIsGuess => _confirmedName is null && SelfName is not null;

    /// <summary>From the identity record, else inferred from our skills, else the guessed character's class.</summary>
    public GameClass SelfClass =>
        _selfClass != GameClass.Unknown ? _selfClass
        : InferredSelfClass != GameClass.Unknown ? InferredSelfClass
        : _confirmedName is null ? GuessCharacter()?.Class ?? GameClass.Unknown
        : GameClass.Unknown;

    /// <summary>Raised when an identity record confirms who we are (for persisting across sessions).</summary>
    public event Action<KnownCharacter>? CharacterIdentified;

    private uint _observedMaxHp;

    /// <summary>Our current HP (0 = unknown).</summary>
    public uint SelfHp { get; private set; }

    /// <summary>Highest HP seen for our character, including earlier sessions (0 = unknown).</summary>
    public uint SelfMaxHp => Math.Max(_observedMaxHp, CurrentCharacter()?.MaxHp ?? 0);

    /// <summary>The known character we currently are (confirmed or guessed).</summary>
    public KnownCharacter? CurrentCharacter() =>
        _confirmedName is { } name ? _knownCharacters.FirstOrDefault(c => c.Name == name) : GuessCharacter();

    private GameClass InferredSelfClass =>
        SelfId is { } id && _entities.TryGetValue(id, out var info) ? info.Class : GameClass.Unknown;
    public uint? MapId { get; private set; }

    public IReadOnlyCollection<EntityInfo> All => _entities.Values;

    /// <summary>Characters confirmed in earlier sessions, used to name us before an identity record arrives.</summary>
    public void SetKnownCharacters(IEnumerable<KnownCharacter> characters)
    {
        _knownCharacters.Clear();
        _knownCharacters.AddRange(characters.OrderByDescending(c => c.LastSeen));
    }

    /// <summary>Most recent known character, restricted to our class once our skills reveal it (alts).</summary>
    private KnownCharacter? GuessCharacter()
    {
        if (_knownCharacters.Count == 0)
        {
            return null;
        }

        var cls = InferredSelfClass;
        return cls == GameClass.Unknown ? _knownCharacters[0] : _knownCharacters.FirstOrDefault(c => c.Class == cls);
    }

    public EntityInfo GetOrAdd(uint id)
    {
        if (!_entities.TryGetValue(id, out var info))
        {
            info = new EntityInfo(id);
            _entities[id] = info;
        }

        return info;
    }

    public EntityInfo? Find(uint id) => _entities.GetValueOrDefault(id);

    public void Apply(GameEvent e)
    {
        switch (e)
        {
            case SelfIdentityEvent self:
            {
                SelfId = self.EntityId;
                if (_confirmedName is not null && _confirmedName != self.Name)
                {
                    _observedMaxHp = 0; // switched character
                    SelfHp = 0;
                }

                _confirmedName = self.Name;
                if (self.Class != GameClass.Unknown)
                {
                    _selfClass = self.Class;
                }

                CharacterIdentified?.Invoke(new KnownCharacter(self.Name, self.Class, self.ServerId, DateTimeOffset.UtcNow, SelfMaxHp));

                var info = GetOrAdd(self.EntityId);
                info.Kind = EntityKind.Player;
                info.Name = self.Name;
                info.ServerId = self.ServerId;
                if (self.Class != GameClass.Unknown)
                {
                    info.Class = self.Class;
                }

                break;
            }

            case SelfHintEvent hint:
            {
                SelfId = hint.EntityId;
                var info = GetOrAdd(hint.EntityId);
                info.Kind = EntityKind.Player;
                info.Name ??= _confirmedName;
                break;
            }

            case PlayerSpawnEvent player:
            {
                var info = GetOrAdd(player.EntityId);
                info.Kind = EntityKind.Player;
                info.Name = player.Name;
                info.ServerId = player.ServerId ?? info.ServerId;
                if (SelfId is null && player.Name == _confirmedName)
                {
                    SelfId = player.EntityId;
                }

                break;
            }

            case NpcSpawnEvent npc:
                ApplyNpcSpawn(npc);
                break;

            case SummonLinkEvent link when link.SummonId != link.OwnerId:
            {
                var info = GetOrAdd(link.SummonId);
                info.Kind = EntityKind.Summon;
                info.OwnerId = link.OwnerId;
                break;
            }

            case HpUpdateEvent hp:
            {
                if (hp.EntityId == SelfId)
                {
                    SelfHp = hp.Hp;
                    _observedMaxHp = Math.Max(_observedMaxHp, Math.Max(hp.Hp, hp.MaxHp));
                }

                var info = GetOrAdd(hp.EntityId);
                info.Hp = hp.Hp;
                info.MaxHp = Math.Max(info.MaxHp, Math.Max(hp.MaxHp, hp.Hp));
                break;
            }

            case DamageEvent damage:
                InferFromDamage(damage);
                break;

            case MapLoadEvent map:
                MapId = map.MapId;
                break;

            case ZoneChangeEvent:
                _entities.Clear();
                SelfId = null;
                break;
        }
    }

    /// <summary>Follows summon → owner links (spirits can own effect entities) up to a few hops.</summary>
    public uint ResolveOwner(uint id)
    {
        uint current = id;
        for (int hop = 0; hop < 4; hop++)
        {
            if (_entities.TryGetValue(current, out var info) && info.OwnerId is { } owner && owner != current)
            {
                current = owner;
                continue;
            }

            if (_entities.TryGetValue(current, out info) && info.OwnerName is { } ownerName &&
                FindPlayerByName(ownerName) is { } byName)
            {
                info.OwnerId = byName.Id;
                current = byName.Id;
                continue;
            }

            break;
        }

        return current;
    }

    public string DisplayName(uint id)
    {
        if (_entities.TryGetValue(id, out var info))
        {
            if (info.Name is { Length: > 0 } name)
            {
                return name;
            }

            if (info.NpcCode is { } code && _data.Npcs.TryGetValue(code, out var npc))
            {
                return npc.Name;
            }

            if (info.NpcCode is { } unknownCode)
            {
                return $"NPC {unknownCode}";
            }
        }

        if (id == SelfId)
        {
            return SelfName ?? "You";
        }

        return info?.Kind == EntityKind.Npc ? "Unknown target" : $"#{id}";
    }

    /// <summary>Like <see cref="DisplayName"/>, but never a bare id: mobs that spawned before capture started are unnamed.</summary>
    public string TargetName(uint id, string fallback = "Unknown target")
    {
        string name = DisplayName(id);
        return name.StartsWith('#') || name == "Unknown target" ? fallback : name;
    }

    public bool IsNpc(uint id) => _entities.TryGetValue(id, out var info) && info.Kind == EntityKind.Npc;

    public bool IsBoss(uint id) => _entities.TryGetValue(id, out var info) && info.IsBoss;

    public bool IsDummy(uint id) => _entities.TryGetValue(id, out var info) && info.IsDummy;

    public GameData Data => _data;

    /// <summary>Swaps the name tables (e.g. after an import); NPC flags are re-read for known entities.</summary>
    public void SetData(GameData data)
    {
        _data = data;
        foreach (var info in _entities.Values)
        {
            if (info.NpcCode is { } code && data.Npcs.TryGetValue(code, out var npc))
            {
                info.IsBoss = npc.IsBoss;
                info.IsDummy = npc.IsDummy;
            }
        }
    }

    private void ApplyNpcSpawn(NpcSpawnEvent npc)
    {
        var info = GetOrAdd(npc.EntityId);
        switch (npc.Kind)
        {
            case 0x0C or 0x0D:
                info.Kind = EntityKind.Npc;
                break;
            case 0x5F or 0x1C or 0x1D or 0x1F or 0x5D:
                info.Kind = EntityKind.Summon;
                info.OwnerName = npc.InlineName ?? info.OwnerName;
                break;
            default:
                if (info.Kind == EntityKind.Unknown)
                {
                    info.Kind = EntityKind.Npc;
                }

                break;
        }

        if (npc.NpcCode is { } code)
        {
            info.NpcCode = code;
            if (_data.Npcs.TryGetValue(code, out var npcData))
            {
                info.IsBoss = npcData.IsBoss;
                info.IsDummy = npcData.IsDummy;
            }
        }

        if (npc.MaxHp is { } maxHp)
        {
            info.MaxHp = Math.Max(info.MaxHp, maxHp);
        }
    }

    private void InferFromDamage(DamageEvent damage)
    {
        if ((damage.Flags & HitFlags.OverTime) != 0)
        {
            return;
        }

        var cls = GameClasses.FromSkillId(damage.SkillId);
        if (cls == GameClass.Unknown)
        {
            return;
        }

        var info = GetOrAdd(damage.ActorId);
        if (info.Kind is EntityKind.Unknown)
        {
            info.Kind = EntityKind.Player;
        }

        if (info.Kind == EntityKind.Player && info.Class == GameClass.Unknown)
        {
            info.Class = cls;
        }
    }

    private EntityInfo? FindPlayerByName(string name)
    {
        foreach (var info in _entities.Values)
        {
            if (info.Kind == EntityKind.Player && info.Name == name)
            {
                return info;
            }
        }

        return null;
    }
}
