using System.Text;
using Avion2.Analysis;
using Avion2.Protocol;
using static Avion2.Tests.TestPackets;

namespace Avion2.Tests;

/// <summary>The party (<c>00 92</c>, <c>0D 92</c>, <c>22 92</c>), as opposed to the group-finder room (<c>02 97</c>).</summary>
public class PartyTests
{
    /// <summary>A party member record; <paramref name="bits"/> is the shared bool byte when this member starts one.</summary>
    private static string Member(byte mask, byte slot, uint entity, long character, string name, uint classCode,
        string? bits, string? legion = null, string rebirth = "") =>
        $"{mask:x2} {slot:x2} {Le(entity, 4)} 2905 2905 {Str("account")} {Le(character, 8)} {Str(name)} {Le(classCode, 4)} " +
        $"e807 e807 {Zeros(24)} {Le(1010, 8)} {Zeros(4)} {Zeros(12)} {bits} {(legion is null ? "" : Str(legion))} " +
        $"{Le(45, 4)} {Zeros(4)} {Le(3000, 4)} {rebirth} {Le(250_000, 8)} 00 ";

    [Fact]
    public void PartyListsEveryMemberWithTheBoolsSharedAcrossTheMessage()
    {
        // One bool byte (1f) serves both members: alive, can loot, logged in for the first; alive, can loot and
        // not logged in for the second, who carries no byte of its own
        string hex = "0092 00 0100000000000000 6400000000000000 05000000 02010002 02 " +
                     Member(0x00, 1, 1000, 100, "TestDaeva", 33, "1f") +
                     Member(0x01, 2, 0, 200, "Friend", 29, null, "Legion") + "01";

        var roster = Assert.IsType<PartyRosterEvent>(Assert.Single(Parse(hex)));
        Assert.Equal(["TestDaeva", "Friend"], roster.Members.Select(m => m.Name));
        Assert.Equal([GameClass.Chanter, GameClass.Cleric], roster.Members.Select(m => m.Class));
        Assert.Equal([100u, 200u], roster.Members.Select(m => m.CharacterId));
        Assert.Equal([1000u, 0u], roster.Members.Select(m => m.EntityId));
        Assert.Equal([true, false], roster.Members.Select(m => m.Online));
    }

    [Fact]
    public void MemberJoiningWithRebirthSkills()
    {
        // Mask 04: one rebirth skill with a cooldown (its own mask bit 0)
        string rebirth = "01 01 f0f60301 02 10270000";
        var joined = Assert.IsType<PartyMemberJoinedEvent>(Assert.Single(Parse("0D92 " + Member(0x04, 3, 0, 300, "Healer", 30, "07", rebirth: rebirth))));
        Assert.Equal(("Healer", GameClass.Cleric, (byte)3), (joined.Member.Name, joined.Member.Class, joined.Member.Slot));
    }

    [Fact]
    public void InvitedPartyJoinsTheRunWithoutAFinderRoom()
    {
        var session = new GameSession();
        session.Apply(new SelfIdentityEvent(0, 1000, "TestDaeva", 1321, GameClass.Chanter, 45));
        session.Apply(new PartyRosterEvent(0, [new PartyMember(1, 100, "TestDaeva", GameClass.Chanter, true, 1000)]));
        session.Apply(new PartyMemberJoinedEvent(500, new PartyMember(2, 200, "Friend", GameClass.Cleric, true)));
        session.Apply(new MapLoadEvent(1_000, 310042, 30));

        var run = Assert.IsType<DungeonRun>(session.Runs.Current);
        Assert.Equal(["Friend"], run.Party.Select(m => m.Name));
    }

    private static string Le(long value, int bytes) => Convert.ToHexString(BitConverter.GetBytes(value)[..bytes]);

    private static string Zeros(int bytes) => new('0', bytes * 2);

    /// <summary>varint length + UTF-8, as hex.</summary>
    private static string Str(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        return $"{bytes.Length:x2}{Convert.ToHexString(bytes)}";
    }
}
