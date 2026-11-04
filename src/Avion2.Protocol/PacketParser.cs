using Avion2.Protocol.Parsers;

namespace Avion2.Protocol;

public enum ParseScope
{
    /// <summary>Everything we know how to decode.</summary>
    All,

    /// <summary>Only identity/spawn records. Used for bundles embedded mid-packet, which would otherwise double-count combat.</summary>
    IdentityOnly,
}

public static class PacketParser
{
    /// <summary>Decodes one packet payload (opcode + body). Returns true when the opcode was recognised and parsed.</summary>
    public static bool Parse(ReadOnlySpan<byte> payload, long timestampMs, Action<GameEvent> emit, ParseScope scope = ParseScope.All)
    {
        if (payload.Length < 2)
        {
            return false;
        }

        ushort opcode = Opcodes.Read(payload);
        var body = payload[2..];

        switch (opcode)
        {
            case Opcodes.SelfIdentity:
                return RecordParsers.ParseSelfIdentity(body, timestampMs, emit);
            case Opcodes.PlayerSpawn:
                return RecordParsers.ParsePlayerSpawn(body, timestampMs, emit);
            case Opcodes.NpcSpawn:
                return RecordParsers.ParseNpcSpawn(body, timestampMs, emit);
            case Opcodes.Kill:
                return WorldParsers.ParseKill(body, timestampMs, emit);
            case Opcodes.LegionTag:
                return WorldParsers.ParseLegionTag(body, timestampMs, emit);
            case Opcodes.LegionDetails:
                return WorldParsers.ParseLegionDetails(body, timestampMs, emit);
            case Opcodes.LegionRoster:
                return WorldParsers.ParseLegionRoster(body, timestampMs, emit);
        }

        if (scope == ParseScope.IdentityOnly)
        {
            return false;
        }

        return opcode switch
        {
            Opcodes.Damage => DamageParser.Parse(body, timestampMs, emit) > 0,
            Opcodes.DamageOverTime => RecordParsers.ParseDamageOverTime(body, timestampMs, emit),
            Opcodes.Death => RecordParsers.ParseDeath(body, timestampMs, emit),
            Opcodes.MapLoad => RecordParsers.ParseMapLoad(body, timestampMs, emit),
            Opcodes.PartyRoster => RecordParsers.ParsePartyRoster(body, timestampMs, emit),
            Opcodes.PartyInfo => RecordParsers.ParsePartyInfo(body, timestampMs, emit),
            Opcodes.SelfCombatPower => RecordParsers.ParseSelfCombatPower(body, timestampMs, emit),
            Opcodes.PartyAddMember or Opcodes.PartyReconnectMember => RecordParsers.ParsePartyMemberJoined(body, timestampMs, emit),
            Opcodes.InstanceStage => RecordParsers.ParseInstanceStage(body, timestampMs, emit),
            Opcodes.GatherStart => RecordParsers.ParseGatherStart(body, timestampMs, emit),
            Opcodes.GatherEnd => RecordParsers.ParseGatherEnd(body, timestampMs, emit),
            Opcodes.CraftStart => RecordParsers.ParseCraftStart(body, timestampMs, emit),
            Opcodes.CraftEnd => RecordParsers.ParseCraftEnd(body, timestampMs, emit),
            Opcodes.ChainOpen => RecordParsers.ParseChainOpen(body, timestampMs, emit),
            Opcodes.ChainClose => RecordParsers.ParseChainClose(body, timestampMs, emit),
            Opcodes.TargetSelected => RecordParsers.ParseTargetSelected(body, timestampMs, emit),
            Opcodes.TargetOfTarget => RecordParsers.ParseTargetOfTarget(body, timestampMs, emit),
            Opcodes.InteractionInterrupted => RecordParsers.ParseInteractionInterrupted(body, timestampMs, emit),
            Opcodes.MapEventStart => RecordParsers.ParseMapEventStart(body, timestampMs, emit),
            Opcodes.MapEventProgress => RecordParsers.ParseMapEventProgress(body, timestampMs, emit),
            Opcodes.Position => RecordParsers.ParsePosition(body, timestampMs, emit),
            Opcodes.HpUpdate => RecordParsers.ParseHpUpdate(body, timestampMs, emit),
            Opcodes.RemainingHp => RecordParsers.ParseRemainingHp(body, timestampMs, emit),
            Opcodes.OwnStats => RecordParsers.ParseOwnStats(body, timestampMs, emit),
            Opcodes.CombatToggle => RecordParsers.ParseCombatState(body, timestampMs, emit),
            Opcodes.FieldBossTable => RecordParsers.ParseFieldBossTable(body, timestampMs, emit),
            Opcodes.SkillCast => WorldParsers.ParseSkillCast(body, timestampMs, emit),
            Opcodes.InventoryList => InventoryParsers.ParseInventoryList(body, timestampMs, emit),
            Opcodes.WorldObject => WorldParsers.ParseWorldObject(body, timestampMs, emit),
            Opcodes.InteractionDone => WorldParsers.ParseInteractionDone(body, timestampMs, emit),
            Opcodes.InventoryItem => InventoryParsers.ParseInventoryItem(body, timestampMs, emit),
            Opcodes.LootDrop => InventoryParsers.ParseLootDrop(body, timestampMs, emit),
            Opcodes.LootTaken => InventoryParsers.ParseLootTaken(body, timestampMs, emit),
            Opcodes.ServerClock => WorldParsers.ParseServerClock(body, timestampMs, emit),
            Opcodes.BuffAdded => WorldParsers.ParseBuff(body, timestampMs, emit, added: true),
            Opcodes.BuffChanged => WorldParsers.ParseBuff(body, timestampMs, emit, added: false),
            Opcodes.BuffRemoved => WorldParsers.ParseBuffRemoved(body, timestampMs, emit),
            Opcodes.Move => WorldParsers.ParseMove(body, timestampMs, emit),
            Opcodes.MoveRelative or Opcodes.MoveSteer or Opcodes.Dash or Opcodes.Relocate =>
                WorldParsers.ParsePositionAt(body, timestampMs, emit, opcode),
            // Each area's own records (see its file): false when the opcode is not one of them
            _ => MechanicsParsers.TryParse(opcode, body, timestampMs, emit) ||
                 MovementParsers.TryParse(opcode, body, timestampMs, emit) ||
                 PartyParsers.TryParse(opcode, body, timestampMs, emit) ||
                 ProgressParsers.TryParse(opcode, body, timestampMs, emit) ||
                 WorldEventParsers.TryParse(opcode, body, timestampMs, emit) ||
                 LootParsers.TryParse(opcode, body, timestampMs, emit) ||
                 RewardsParsers.TryParse(opcode, body, timestampMs, emit) ||
                 FinderParsers.TryParse(opcode, body, timestampMs, emit) ||
                 PetParsers.TryParse(opcode, body, timestampMs, emit) ||
                 LegionsParsers.TryParse(opcode, body, timestampMs, emit),
        };
    }
}
