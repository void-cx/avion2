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
    public void BuffRemovedListsInstances()
    {
        var removed = Assert.IsType<BuffRemovedEvent>(Assert.Single(Parse("2C38 e173 01 00 c802 07")));
        Assert.Equal(14817u, removed.EntityId);
        Assert.Equal([328u], removed.Instances);
    }

    [Fact]
    public void OwnershipLinksSpawnedEntitiesToTheirOwnerAndLegion()
    {
        var events = Parse("048D ab8303 b01d1301 b152 2905 " + Text("TestDaeva") + Text("Legion") + "010000000000000100");
        var claim = Assert.IsType<OwnershipEvent>(events[0]);
        Assert.Equal(49579u, claim.EntityId);
        Assert.Equal(10545u, claim.OwnerId);
        var tag = Assert.IsType<LegionTagEvent>(events[1]);
        Assert.Equal(("Legion", (ushort)1321), (tag.Legion, tag.ServerId));
    }

    [Fact]
    public void ShortOwnershipFormIsIgnored() => Assert.Empty(Parse("048D 97aa02 0000000000000000000000 7e06 00000000"));

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
