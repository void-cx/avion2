using System.Text;
using Avion2.Protocol;
using static Avion2.Tests.TestPackets;

namespace Avion2.Tests;

/// <summary>Movement, casts, buffs, ownership and legion records (vectors from live captures; names replaced).</summary>
public class WorldTests
{
    [Fact]
    public void ParsesAbsoluteMoveWithHeading()
    {
        var move = Assert.IsType<PositionEvent>(Assert.Single(Parse("1C37 d2ff01 04 e98f6847 dcdb9646 000efa46 0040 0040 01")));
        Assert.Equal(32722u, move.EntityId);
        Assert.Equal(59535.9f, move.Position.X, 0.1f);
        Assert.Equal(19309.9f, move.Position.Y, 0.1f);
        Assert.Equal(90f, move.Heading!.Value, 0.01f);
        Assert.Equal(PositionSource.Move, move.Source);
    }

    [Fact]
    public void ParsesMoveMode5WithItsExtraByte()
    {
        var move = Assert.IsType<PositionEvent>(Assert.Single(Parse("1C37 8d18 05 03 19d55f47 d5db7446 01e5f946 3349 3349")));
        Assert.Equal(57301.1f, move.Position.X, 0.1f);
        Assert.True(move.Position.IsPlausible);
    }

    [Theory]
    [InlineData("1C37 d2ff01 01 03 e98f6847 dcdb9646 000efa46 0040 0040")] // flags bit 0: a state byte before the position
    [InlineData("1B37 d2ff01 01 03 e98f6847 dcdb9646 000efa46 0040 0040")]
    [InlineData("1B37 d2ff01 04 e98f6847 dcdb9646 000efa46 0040 0040 01")] // otherwise it comes at the end
    [InlineData("1A37 d2ff01 01 00 03 e98f6847 dcdb9646 000efa46 0040")] // a stop: flags, a byte, [state], position
    [InlineData("1A37 d2ff01 00 00 e98f6847 dcdb9646 000efa46 0040")]
    public void MoveFlagBit0AddsAByteBeforeThePosition(string hex)
    {
        var move = Assert.IsType<PositionEvent>(Assert.Single(Parse(hex)));
        Assert.Equal(59535.9f, move.Position.X, 0.1f);
        Assert.Equal(19309.9f, move.Position.Y, 0.1f);
    }

    [Fact]
    public void ParsesSkillCastWithTargetPosition()
    {
        var cast = Assert.IsType<SkillCastEvent>(Assert.Single(
            Parse("0238 b15200 a0f61201 07 02 f19c02 74faeb41 568ce7c7 9e8bd4c7 00dcbf46 cc530100")));
        Assert.Equal(10545u, cast.ActorId);
        Assert.Equal(18_020_000u, cast.SkillId);
        Assert.Equal(36465u, cast.TargetId);
        Assert.Equal(-118552f, cast.TargetPosition!.Value.X, 1f);
        Assert.Equal(-108823f, cast.TargetPosition!.Value.Y, 1f);
    }

    [Fact]
    public void ParsesSelfCastWithoutPosition()
    {
        var cast = Assert.IsType<SkillCastEvent>(Assert.Single(Parse("0238 e17300 e59fd500 ea 02 e173 101c1cbe 54dec742 c31168c0 00000000 904e01f403")));
        Assert.Equal(14817u, cast.ActorId);
        Assert.Equal(14817u, cast.TargetId);
        Assert.Null(cast.TargetPosition); // the floats here are not world coordinates
    }

    [Fact]
    public void DodgeKeepsItsIdAndName()
    {
        // The same record is a dodge (14000101): basic actions (CC 00 NNNN) keep their id, not 14000000
        var cast = Assert.IsType<SkillCastEvent>(Assert.Single(Parse("0238 e17300 e59fd500 ea 02 e173 101c1cbe 54dec742 c31168c0 00000000 904e01f403")));
        Assert.Equal(14_000_101u, cast.SkillId);

        var data = Avion2.Analysis.GameData.Parse(
            """{"11000000": "Equip Gladiator Weapon", "11000100": "Dodge", "14000100": "Dodge", "18000100": "Dodge", "18000101": "Basic Attack"}""", null);
        Assert.Equal("Dodge", data.SkillName(cast.SkillId));
        Assert.Equal("Dodge", data.SkillName(11_000_101));
        Assert.Equal("Dodge", data.SkillName(18_000_101)); // the sprint dodge, mislabelled in the tables
        Assert.Equal("Equip Gladiator Weapon", data.SkillName(11_000_000));
    }

    [Fact]
    public void BuffAddedCarriesDurationCasterAndAPositionFix()
    {
        var events = Parse("2A38 e173 01 11 c802 c8000000 2c01000000000000 1cfc3a34a1010000 e173 01 00 ce305a47 2a4da646 00baf546");
        var buff = Assert.IsType<BuffEvent>(events[0]);
        Assert.Equal(14817u, buff.EntityId);
        Assert.Equal(328u, buff.Instance);
        Assert.Equal(200u, buff.AbnormalId);
        Assert.Equal(300, buff.DurationMs);
        Assert.Equal(14817u, buff.CasterId);
        var fix = Assert.IsType<PositionEvent>(events[1]);
        Assert.Equal(PositionSource.Buff, fix.Source);
        Assert.Equal(55856.8f, fix.Position.X, 0.1f);
    }

    [Fact]
    public void OnlySelfCastBuffsPlaceTheEntity()
    {
        // Refresh (2B 38) of a mantra the Chanter keeps on himself: a position fix, once a second while it ticks
        var refresh = Parse("2B38 e725132d0bffd20a600900000000000029f61b3aa1010000e72501811915010297ef924766f7884700a8da46");
        Assert.IsType<BuffEvent>(refresh[0]);
        Assert.Equal(PositionSource.Buff, Assert.IsType<PositionEvent>(refresh[1]).Source);

        // The same buff from another caster carries the caster's spot: no fix for the target
        var fromOther = Parse("2A38 e173 01 11 c802 c8000000 2c01000000000000 1cfc3a34a1010000 9921 01 00 ce305a47 2a4da646 00baf546");
        Assert.IsType<BuffEvent>(Assert.Single(fromOther));
    }

    [Fact]
    public void BuffRemovedListsInstances()
    {
        var removed = Assert.IsType<BuffRemovedEvent>(Assert.Single(Parse("2C38 e173 01 00 c802 07")));
        Assert.Equal(14817u, removed.EntityId);
        Assert.Equal([328u], removed.Instances);
    }

    [Fact]
    public void KillNamesVictimKillerAndTheKillersLegion()
    {
        // victim, skill, killer, server, name, legion, emblem, NPC killer (none), u32, reason (1 a hit), u8
        var events = Parse("048D ab8303 b01d1301 b152 2905 " + Text("TestDaeva") + Text("Legion") + "0100 00 00000000 01 00");
        var kill = Assert.IsType<KillEvent>(events[0]);
        Assert.Equal((49579u, 10545u, 18_030_000u, (byte)1), (kill.VictimId, kill.KillerId, kill.SkillId, kill.Reason));
        Assert.Null(kill.KillerNpcCode);
        var tag = Assert.IsType<LegionTagEvent>(events[1]);
        Assert.Equal(("Legion", (ushort)1321), (tag.Legion, tag.ServerId));
    }

    [Theory]
    [InlineData("048D 97aa02 082a0401 84a002 0000 00 00 0000 beea8e01 00000000 01 02", 36_868u, 17_050_120u, 2_340_158u)] // killed by an NPC (die type 2: a player)
    [InlineData("048D fbac01 f4eed500 00 0000 00 00 0000 00 00000000 01 02", 0u, 14_020_340u, null)] // killer not said (party, big fight)
    public void KillWithoutAPlayerKiller(string hex, uint killer, uint skill, uint? npc)
    {
        var kill = Assert.IsType<KillEvent>(Assert.Single(Parse(hex)));
        Assert.Equal(killer, kill.KillerId);
        Assert.Equal(skill, kill.SkillId);
        Assert.Equal(npc, kill.KillerNpcCode);
    }

    [Fact]
    public void ParsesLegionTags()
    {
        var tag = Assert.IsType<LegionTagEvent>(Assert.Single(Parse("338A b152 060100000000 2905 " + Text("Legion") + "78010001000000")));
        Assert.Equal(10545u, tag.EntityId);
        Assert.Equal("Legion", tag.Legion);

        var other = Assert.IsType<LegionTagEvent>(Assert.Single(Parse("338A cf5558 0000000000 2905 " + Text("Others") + "010002000000")));
        Assert.Equal("Others", other.Legion);
    }

    [Fact]
    public void ParsesLegionDetailsAndTexts()
    {
        const string intro = "[EN] A relaxed legion - join us!";
        const string notice = "Welcome!\r\nDungeons every evening.";
        var details = Assert.IsType<LegionDetailsEvent>(Assert.Single(Parse(
            "038A 0000 " + Text("Legion") + Text("Leader") + "0a8001 0000000000000000 e803000000000000 0100 " +
            Text(intro) + "8501" + Text(notice))));
        Assert.Equal("Legion", details.Name);
        Assert.Equal("Leader", details.Leader);
        Assert.Equal([intro, notice], details.Texts);
    }

    [Fact]
    public void ParsesLegionRosterWithLevelsAndLastLogin()
    {
        var roster = Assert.IsType<LegionRosterEvent>(Assert.Single(Parse(
            "058A 00000a009fc90b0000000000 3c2900000000 " +
            "2905 22000000 " + Text("Alpha") + "2d 00000002 80 6dcbdb31a1010000 0000005e070000 " +
            "2905 19000000 " + Text("Bravo") + "2d 00000000 d5c04b32a1010000 00000000")));
        Assert.Equal(["Alpha", "Bravo"], roster.Members.Select(m => m.Name));
        Assert.Equal(45, roster.Members[0].Level);
        Assert.Equal(1_791_837_850_477, roster.Members[0].LastOnlineMs);
    }

    [Fact]
    public void NpcSpawnCarriesItsPosition()
    {
        var events = Parse("4136 c397020d2000c5b72c000002218e5947d407384600fcf9464e80da416c1301f74df74d64000000640000000000");
        var spawn = Assert.IsType<NpcSpawnEvent>(events[0]);
        Assert.Equal(55694.1f, spawn.Position!.Value.X, 0.1f);
        Assert.IsType<PositionEvent>(events[1]);
    }

    [Theory]
    [InlineData(-8e-28f, 41_000f, 2_100f, false)] // a spawn read at the wrong offset
    [InlineData(41_000f, 3e-30f, 2_100f, false)]
    [InlineData(0f, 41_000f, 2_100f, true)]
    [InlineData(41_000f, -52_300f, 2_100f, true)]
    public void TinyCoordinatesAreNotPositions(float x, float y, float z, bool plausible) =>
        Assert.Equal(plausible, new Vec3(x, y, z).IsPlausible);

    /// <summary>u8 length + UTF-8, as hex.</summary>
    private static string Text(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        return $"{bytes.Length:x2}{Convert.ToHexString(bytes)} ";
    }
}

public class DecoderResilienceTests
{
    [Fact]
    public void APacketThatThrowsIsSkippedAndDecodingContinues()
    {
        var events = new List<GameEvent>();
        var errors = new List<string>();
        var previous = GameStreamDecoder.PacketError;
        GameStreamDecoder.PacketError = (ex, payload, _) => errors.Add($"{Opcodes.Format(Opcodes.Read(payload))} {ex.Message}");
        try
        {
            bool first = true;
            var decoder = new GameStreamDecoder(e =>
            {
                if (first)
                {
                    first = false;
                    throw new InvalidOperationException("boom"); // e.g. a handler bug on an unusual record
                }

                events.Add(e);
            });

            byte[] move = TestPackets.Hex("1C37 d2ff01 04 e98f6847 dcdb9646 000efa46 0040 0040 01");
            decoder.Feed([.. TestPackets.Frame(move), .. TestPackets.Frame(move)], 1000);

            Assert.Single(events);
            Assert.Equal(1, decoder.Stats.Errors);
            Assert.Equal(["1C 37 boom"], errors);
        }
        finally
        {
            GameStreamDecoder.PacketError = previous;
        }
    }
}

public class BodylessPacketTests
{
    [Theory]
    [InlineData("1993")]
    [InlineData("3F8D")]
    public void OpcodeOnlyPacketsDecodeWithoutErrors(string hex)
    {
        var decoder = new GameStreamDecoder(_ => { });
        byte[] move = TestPackets.Hex("1C37 d2ff01 04 e98f6847 dcdb9646 000efa46 0040 0040 01");
        // Mid-stream, as in the dungeon: a known packet, the bodyless one, another known packet.
        decoder.Feed([.. TestPackets.Frame(move), .. TestPackets.Frame(TestPackets.Hex(hex)), .. TestPackets.Frame(move)], 1000);
        Assert.Equal(0, decoder.Stats.Errors);
        Assert.Equal(3, decoder.Stats.Packets);
    }
}
