using System.Text;
using Avion2.Analysis;
using Avion2.Engine;
using Avion2.Protocol;
using static Avion2.Tests.TestPackets;

namespace Avion2.Tests;

/// <summary>Party state (HP, deaths, leader, leaving), member positions for the map, and the small party records.</summary>
public class PartyFeatureTests
{
    private const uint Map = 1010;

    [Fact]
    public void MemberStatusGivesMapPositionLevelsAndCombatPower()
    {
        // 1C 92: character, class 29 (Cleric), map 1010 in instance 0, subzone 2088, position, level 45, conqueror 0,
        // item level 475, legion "Legion", emblem, can loot, combat power 26891, latency
        string hex = $"1C92 {Le(200, 8)} {Le(29, 4)} {Le(Map, 4)} {Le(0, 4)} {Le(2088, 4)} {F(-23034)} {F(43353)} {F(14654)} " +
                     $"{Le(45, 4)} {Le(0, 4)} {Le(475, 4)} {Str("Legion")} {Le(7, 4)} 01 {Le(26891, 8)} 01";

        var status = Assert.IsType<PartyMemberStatusEvent>(Assert.Single(Parse(hex)));
        Assert.Equal((200u, GameClass.Cleric), (status.CharacterId, status.Class));
        Assert.Equal(new PartyMemberStatus(Map, 0, 2088, new Vec3(-23034, 43353, 14654), 45, 475, 26891, "Legion"), status.Status);

        // Without a legion the name is an empty string
        string bare = $"1C92 {Le(200, 8)} {Le(29, 4)} {Le(Map, 4)} {Le(0, 4)} {Le(2088, 4)} {F(-23034)} {F(43353)} {F(14654)} " +
                      $"{Le(45, 4)} {Le(0, 4)} {Le(475, 4)} 00 {Le(0, 4)} 01 {Le(26891, 8)} 01";
        Assert.Null(Assert.IsType<PartyMemberStatusEvent>(Assert.Single(Parse(bare))).Status.Legion);
        Assert.Empty(Parse(bare + " 00")); // a byte too many: not this record
    }

    [Fact]
    public void MemberHpSaysWhetherTheyAreAlive()
    {
        // 1B 92: entity 12033 (varint 81 5e), hp 0 of 6089, mp/ap/dp and maxima, live 0
        var hp = Assert.IsType<HpUpdateEvent>(Assert.Single(Parse("1B92 815e 00 c92f 6f090000 040a0000 00000000 00000000 00000000 f0490200 00")));
        Assert.Equal((12033u, 0u, 6089u, (bool?)false), (hp.EntityId, hp.Hp, hp.MaxHp, hp.Alive));

        var alive = Assert.IsType<HpUpdateEvent>(Assert.Single(Parse("1B92 815e c92f c92f 6f090000 040a0000 00000000 00000000 00000000 f0490200 01")));
        Assert.True(alive.Alive);
    }

    [Fact]
    public void SmallPartyRecords()
    {
        Assert.Equal(new PartyMemberDisconnectedEvent(1000, 200), Assert.Single(Parse($"2192 {Le(200, 8)}")));
        Assert.Equal(new PartyLeaderEvent(1000, 300), Assert.Single(Parse($"1192 {Le(300, 8)}")));
        Assert.Equal(new PartyDisbandedEvent(1000, 8), Assert.Single(Parse("1F92 08")));
        Assert.Equal(new PartyLeftEvent(1000, 0x1234_5678, 3), Assert.Single(Parse($"1F93 {Le(0x1234_5678, 8)} 03")));

        var added = Assert.IsType<PartyAbnormalEvent>(Assert.Single(Parse("0E92 815e 01 eb92d70a")));
        Assert.Equal((12033u, true), (added.EntityId, added.Added));
        Assert.Equal([0x0AD792EBu], added.AbnormalIds);
        var removed = Assert.IsType<PartyAbnormalEvent>(Assert.Single(Parse("1A92 815e eb92d70a")));
        Assert.Equal((12033u, false, 0x0AD792EBu), (removed.EntityId, removed.Added, removed.AbnormalIds[0]));
        Assert.Empty(Parse("0E92 815e 02 eb92d70a")); // two announced, one there
    }

    [Fact]
    public void PartyListCarriesHpPositionAndLeader()
    {
        string hex = "0092 00 0100000000000000 " + Le(200, 8) + " 05000000 02010002 02 " +
                     Member(1, 1000, 100, "TestDaeva", 33, hp: 900, maxHp: 1000, x: -23034, y: 43353, bits: "1f") +
                     Member(2, 0, 200, "Friend", 29, hp: 0, maxHp: 0, x: 500, y: 600, bits: null) + "01";

        var roster = Assert.IsType<PartyRosterEvent>(Assert.Single(Parse(hex)));
        Assert.Equal(200u, roster.LeaderId);
        var me = roster.Members[0];
        Assert.Equal((900u, 1000u, true), (me.Hp, me.MaxHp, me.Alive));
        Assert.Equal((Map, new Vec3(-23034, 43353, 1500), 45u, 3000u, 250_000u),
            (me.Status!.MapId, me.Status.Position, me.Status.Level, me.Status.ItemLevel, me.Status.CombatPower));
        Assert.Equal((0u, 0u), (roster.Members[1].Hp, roster.Members[1].MaxHp));
    }

    [Fact]
    public void TracksMembersHpDeathsAndLeader()
    {
        var session = Party();
        var party = session.Party.Snapshot(session.Registry);
        Assert.Equal(["TestDaeva", "Friend", "Faraway"], party.Select(m => m.Name));
        Assert.Equal([true, false, false], party.Select(m => m.IsSelf));
        Assert.True(party[1].IsLeader);
        Assert.Null(party[2].HpRatio); // in another zone: HP not known

        session.Apply(new HpUpdateEvent(1_000, 2000, 300, 1200, Alive: true));
        Assert.Equal(0.25, session.Party.Snapshot(session.Registry)[1].HpRatio);

        session.Apply(new KillEvent(2_000, 2000, 0, 0, 900, 1));
        Assert.False(session.Party.Snapshot(session.Registry)[1].Alive);

        // Revived: the HP record says alive again
        session.Apply(new HpUpdateEvent(3_000, 2000, 600, 1200, Alive: true));
        Assert.True(session.Party.Snapshot(session.Registry)[1].Alive);

        session.Apply(new PartyLeaderEvent(4_000, 100));
        Assert.Equal([true, false, false], session.Party.Snapshot(session.Registry).Select(m => m.IsLeader));
    }

    [Fact]
    public void DisconnectReconnectAndLeaving()
    {
        var session = Party();
        session.Apply(new PartyMemberDisconnectedEvent(1_000, 200));
        var friend = session.Party.Snapshot(session.Registry)[1];
        Assert.Equal((false, 0u), (friend.Online, friend.EntityId));

        // Back with the entity id they have inside the instance
        session.Apply(new PartyMemberJoinedEvent(2_000, new PartyMember(2, 200, "Friend", GameClass.Cleric, true, 7000, 1200, 1200)));
        friend = session.Party.Snapshot(session.Registry)[1];
        Assert.Equal((true, 7000u, 1.0), (friend.Online, friend.EntityId, friend.HpRatio));

        // The finder room is not the party
        session.Apply(new PartyRosterEvent(3_000, [new PartyMember(1, 100, "TestDaeva", GameClass.Chanter)]));
        Assert.Equal(3, session.Party.Snapshot(session.Registry).Count);

        session.Apply(new PartyLeftEvent(4_000, 1, 3));
        Assert.Empty(session.Party.Snapshot(session.Registry));
        Assert.False(session.Party.HasParty);
    }

    [Fact]
    public void PartyOfJustUsIsNoParty()
    {
        var session = new GameSession();
        session.Apply(new SelfIdentityEvent(0, 1000, "TestDaeva", 1321, GameClass.Chanter, 45));
        session.Apply(new PartyRosterEvent(0, [Record(1, 1000, 100, "TestDaeva", GameClass.Chanter)], 100));
        Assert.Empty(session.Party.Snapshot(session.Registry));

        session.Apply(new PartyDisbandedEvent(1, 8));
        Assert.False(session.Party.HasParty);
    }

    [Fact]
    public void MembersOnTheMapAlsoOutOfView()
    {
        var session = Party();
        session.Apply(new MapLoadEvent(0, Map));
        session.Apply(new PlayerSpawnEvent(500, 2000, "Friend", 1321, GameClass.Cleric, Position: new Vec3(1000, 2000, 300)));
        session.Apply(new PlayerSpawnEvent(500, 4000, "Stranger", 1321, GameClass.Ranger, Position: new Vec3(1500, 2500, 300)));
        // A member out of view on our map, known only from their status record
        session.Apply(new PartyMemberStatusEvent(600, 300, GameClass.Ranger,
            new PartyMemberStatus(Map, 0, 1, new Vec3(-5000, 9000, 300), 45, 3000, 1)));

        var (_, live) = Live(session);
        Assert.Equal(["Friend", "Faraway"], live.Where(e => e.Party).Select(e => e.Name));
        Assert.False(live.Single(e => e.Name == "Stranger").Party);
        var faraway = live.Single(e => e.Name == "Faraway");
        Assert.Equal((PartyTracker.OutOfViewId(300), new Vec3(-5000, 9000, 300)), (faraway.Id, faraway.Position));

        // On another map: no marker on ours, and the player now holding the old entity id is not taken for them
        session.Apply(new PartyMemberStatusEvent(700, 200, GameClass.Cleric,
            new PartyMemberStatus(310042, 1, 1, new Vec3(1, 2000, 300), 45, 3000, 1)));
        (_, live) = Live(session);
        Assert.Equal(["Faraway"], live.Where(e => e.Party).Select(e => e.Name));
        Assert.Equal(["Friend"], PartyTracker.Markers(session.Party.Snapshot(session.Registry), 310042).Select(e => e.Name));
    }

    [Fact]
    public void AnonymizedPartyHasNoRealNames()
    {
        var anonymizer = new NameAnonymizer();
        anonymizer.Rewrite(new SelfIdentityEvent(0, 1000, "TestDaeva", 1321, GameClass.Chanter, 45));
        var status = new PartyMemberStatus(Map, 0, 0, default, 45, 3000, 1, "RealLegion");
        var roster = Assert.IsType<PartyRosterEvent>(anonymizer.Rewrite(new PartyRosterEvent(0,
            [Record(1, 1000, 100, "TestDaeva", GameClass.Chanter) with { Status = status }, Record(2, 2000, 200, "Friend", GameClass.Cleric)], 100)));
        Assert.Equal(["You", "Player 2"], roster.Members.Select(m => m.Name));
        Assert.Equal("Legion", roster.Members[0].Status!.Legion);

        var moved = Assert.IsType<PartyMemberStatusEvent>(anonymizer.Rewrite(new PartyMemberStatusEvent(0, 200, GameClass.Cleric, status)));
        Assert.Equal("Legion", moved.Status.Legion);
    }

    /// <summary>Us, a leader in our zone (entity 2000) and a member in another zone, on map 1010.</summary>
    private static GameSession Party()
    {
        var session = new GameSession();
        session.Apply(new SelfIdentityEvent(0, 1000, "TestDaeva", 1321, GameClass.Chanter, 45));
        session.Apply(new PartyRosterEvent(0, [
            Record(1, 1000, 100, "TestDaeva", GameClass.Chanter, 1000, 1000),
            Record(2, 2000, 200, "Friend", GameClass.Cleric, 1200, 1200),
            Record(3, 0, 300, "Faraway", GameClass.Ranger),
        ], LeaderId: 200));
        return session;
    }

    private static (uint? Map, IReadOnlyList<LiveEntity> Live) Live(GameSession session) =>
        (session.Positions.MapId, session.Party.OnMap(session.Positions.MapId, session.Positions.Snapshot(session.Registry), session.Registry));

    private static PartyMember Record(byte slot, uint entity, uint character, string name, GameClass cls, uint hp = 0, uint max = 0) =>
        new(slot, character, name, cls, true, entity, hp, max);

    /// <summary>A party member record (mask 0); <paramref name="bits"/> is the shared bool byte when this member starts one.</summary>
    private static string Member(byte slot, uint entity, long character, string name, uint classCode, uint hp, uint maxHp,
        float x, float y, string? bits) =>
        $"00 {slot:x2} {Le(entity, 4)} 2905 2905 {Str("account")} {Le(character, 8)} {Str(name)} {Le(classCode, 4)} " +
        $"{Varint(hp)} {Varint(maxHp)} {Zeros(24)} {Le(Map, 8)} {Zeros(4)} {F(x)} {F(y)} {F(1500)} {bits} " +
        $"{Le(45, 4)} {Zeros(4)} {Le(3000, 4)} {Le(250_000, 8)} 00 ";

    private static string Varint(uint value)
    {
        var bytes = new List<byte>();
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            bytes.Add(value != 0 ? (byte)(b | 0x80) : b);
        }
        while (value != 0);

        return Convert.ToHexString(bytes.ToArray());
    }

    private static string F(float value) => Convert.ToHexString(BitConverter.GetBytes(value));

    private static string Le(long value, int bytes) => Convert.ToHexString(BitConverter.GetBytes(value)[..bytes]);

    private static string Zeros(int bytes) => new('0', bytes * 2);

    /// <summary>varint length + UTF-8, as hex.</summary>
    private static string Str(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        return $"{bytes.Length:x2}{Convert.ToHexString(bytes)}";
    }
}
