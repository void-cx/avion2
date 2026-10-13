using Avion2.Protocol;
using static Avion2.Tests.TestPackets;

namespace Avion2.Tests;

public class VarintTests
{
    [Theory]
    [InlineData("00", 0u, 1)]
    [InlineData("7f", 127u, 1)]
    [InlineData("8001", 128u, 2)]
    [InlineData("b1ea01", 30001u, 3)]
    [InlineData("ffffffff0f", uint.MaxValue, 5)]
    public void Reads(string hex, uint expected, int length)
    {
        Assert.True(Varint.TryRead(Hex(hex), out uint value, out int read));
        Assert.Equal(expected, value);
        Assert.Equal(length, read);
    }

    [Theory]
    [InlineData("80")]          // truncated
    [InlineData("ffffffff1f")]  // > 32 bits
    [InlineData("ffffffffff01")]
    public void RejectsInvalid(string hex) => Assert.False(Varint.TryRead(Hex(hex), out _, out _));

    [Theory]
    [InlineData(0u)]
    [InlineData(300u)]
    [InlineData(16384u)]
    [InlineData(uint.MaxValue)]
    public void RoundTrips(uint value)
    {
        var buffer = new byte[5];
        int written = Varint.Write(buffer, value);
        Assert.Equal(Varint.SizeOf(value), written);
        Assert.True(Varint.TryRead(buffer, out uint read, out int length));
        Assert.Equal(value, read);
        Assert.Equal(written, length);
    }
}

public class FramingTests
{
    // Frame size = L - 4 + varint width (1-byte varint → L-3, 2-byte → L-2).
    [Fact]
    public void FrameSizeAccountsForVarintWidth()
    {
        Assert.True(Framing.TryGetFrameSize(Hex("13"), out int small, out int smallHeader));
        Assert.Equal(16, small);
        Assert.Equal(1, smallHeader);

        Assert.True(Framing.TryGetFrameSize(Hex("c801"), out int large, out int largeHeader)); // L = 200
        Assert.Equal(198, large);
        Assert.Equal(2, largeHeader);
    }

    [Fact]
    public void DecodesRealDotFrame()
    {
        // Example from prior art: heal on 5041 by 5041, skill 190000131/100, 7351 HP.
        var events = Decode(Hex("13 0538 b127 01 b127 b601 032c530b b739"));
        var heal = Assert.IsType<HealEvent>(Assert.Single(events));
        Assert.Equal(5041u, heal.ActorId);
        Assert.Equal(5041u, heal.TargetId);
        Assert.Equal(1900001u, heal.SkillId);
        Assert.Equal(7351, heal.Amount);
    }

    [Fact]
    public void HandlesFramesSplitAcrossChunksAndZeroPadding()
    {
        byte[] frame = Frame("4236 ccc801 00 03");
        byte[] stream = [0x00, 0x00, .. frame, 0x00, .. frame];
        var events = Decode(stream[..4], stream[4..9], stream[9..]);
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(DeathKind.Combat, Assert.IsType<DeathEvent>(e).Kind));
    }

    [Fact]
    public void ResyncsAfterInvalidLengths()
    {
        byte[] garbage = Hex("01 02 03 02 01");
        byte[] stream = [.. garbage, .. Frame("4236 ccc801 00 07")];
        var death = Assert.IsType<DeathEvent>(Assert.Single(Decode(stream)));
        Assert.Equal(25676u, death.EntityId);
        Assert.Equal(DeathKind.LeftWorld, death.Kind);
    }

    [Fact]
    public void RecoversAfterPlausibleBogusLength()
    {
        // "ff 01" reads as a 253-byte frame; in resync mode its opcode family is implausible, so it is skipped.
        byte[] frame = Frame("4236 ccc801 00 03");
        var stream = new List<byte> { 0xFF, 0x01 };
        for (int i = 0; i < 60; i++)
        {
            stream.AddRange(frame);
        }

        var events = Decode([.. stream]);
        Assert.Equal(60, events.Count);
    }

    [Fact]
    public void SkipsStrayTlsRecords()
    {
        byte[] tls = [0x17, 0x03, 0x03, 0x00, 0x04, 0xAA, 0xBB, 0xCC, 0xDD];
        var events = Decode([.. tls, .. Frame("4236 ccc801 00 03")]);
        Assert.Single(events);
    }

    [Fact]
    public void DecodesNestedLz4Bundles()
    {
        byte[] death = Frame("4236 ccc801 00 03");
        byte[] map = Frame("2136 01000000 d5270900");
        byte[] inner = Bundle(death, map);
        byte[] outer = Bundle(inner, death);

        var events = Decode(outer);
        Assert.Equal(3, events.Count);
        Assert.Equal(600021u, events.OfType<MapLoadEvent>().Single().MapId);
    }

    [Fact]
    public void WaitsForLargeBundleFragments()
    {
        byte[] payload = new byte[20_000];
        Random.Shared.NextBytes(payload);
        byte[] bundle = Bundle(Frame([0x99, 0x99, .. payload]), Frame("4236 ccc801 00 03"));
        Assert.True(bundle.Length > Framing.MaxFragmentWaitBytes);

        var events = Decode(bundle[..5000], bundle[5000..12000], bundle[12000..]);
        Assert.IsType<DeathEvent>(Assert.Single(events));
    }

    [Fact]
    public void FindsIdentityInEmbeddedBundle()
    {
        byte[] self = Frame("3336 ed74 5e91c128 37 06 4e6169636861 1805 1e000000 01 1c000000");
        byte[] bundle = Bundle(self);
        // An unrelated packet that happens to carry a bundle frame mid-payload.
        byte[] host = Frame([0x77, 0x36, 0x01, 0x02, .. bundle, 0x03]);
        var identity = Assert.IsType<SelfIdentityEvent>(Assert.Single(Decode(host)));
        Assert.Equal("Naicha", identity.Name);
    }
}

public class DamageParserTests
{
    // Vectors below are from a live capture (2026-10-13, Chanter vs. open-world mobs) unless noted.

    [Fact]
    public void ParsesLiveMultiHitRecord()
    {
        // Onslaught on mob 49579, layout 4 + 0x20: 802 damage incl. one extra hit, seq 1.
        var hit = Assert.IsType<DamageEvent>(Assert.Single(
            Parse("0438 ab8303 24 00 b152 90cf1201 10 02 4b14596b 01000000 c253 a206 01 11 01 00")));
        Assert.Equal(49579u, hit.TargetId);
        Assert.Equal(10545u, hit.ActorId);
        Assert.Equal(18010000u, hit.SkillId);
        Assert.Equal(802, hit.Amount);
        Assert.Equal(2, hit.HitCount);
        Assert.Equal(10690u, hit.PowerScalar);
        Assert.True(hit.Flags.HasFlag(HitFlags.MultiHit));
    }

    [Fact]
    public void ParsesLaterHitsOfACombo()
    {
        // The second u32 is the hit index within the cast and is repeated as the terminator.
        var hit = Assert.IsType<DamageEvent>(Assert.Single(
            Parse("0438 a0ea03 26 00 b152 c0441301 16 02 00 00 02 15db866b 02000000 c253 a00f 01 2b 02 00")));
        Assert.Equal(18040000u, hit.SkillId);
        Assert.Equal(1952, hit.Amount);
        Assert.True(hit.Flags.HasFlag(HitFlags.Front));

        var layout4 = Assert.IsType<DamageEvent>(Assert.Single(
            Parse("0438 ab8303 04 00 b152 a0f61201 13 02 9556686b 02000000 c253 da02 02 00")));
        Assert.Equal(346, layout4.Amount);
    }

    [Fact]
    public void ParsesMonsterCritWithLinkedEffect()
    {
        // Mob 61796 crits us; switch 0x40 carries a linked effect code (our Protection Circle).
        var crit = Assert.IsType<DamageEvent>(Assert.Single(
            Parse("0438 b152 06 00 9cf001 aaa71200 01 03 00 00 02 737e4907 01000000 904e 9f04 01 00")));
        Assert.Equal(HitType.Critical, crit.HitType);
        Assert.Equal(10545u, crit.TargetId);

        var linked = Assert.IsType<DamageEvent>(Assert.Single(
            Parse("0438 b152 46 00 e4e203 72a81200 01 02 00 00 02 93cc4907 01000000 904e de03 01 13b7a36f 01 00")));
        Assert.Equal(478, linked.Amount);
        Assert.Equal(61796u, linked.ActorId);
    }

    [Fact]
    public void ParryAndPerfectFlags()
    {
        var parry = Assert.IsType<DamageEvent>(Assert.Single(
            Parse("0438 ab8303 06 00 b152 a0f61201 13 02 02 00 02 9556686b 01000000 c253 8207 01 00")));
        Assert.True(parry.Flags.HasFlag(HitFlags.Parry));

        // Prior-art vector (A2Tools): Perfect, value 127.
        var perfect = Assert.IsType<DamageEvent>(Assert.Single(
            Parse("0438 cac204 06 00 9e38 9147ff00 e1 02 04 00 00 aff4b763 01000000 a458 7f 01 00")));
        Assert.Equal(127, perfect.Amount);
        Assert.True(perfect.Flags.HasFlag(HitFlags.Perfect));
    }

    [Fact]
    public void EffectTicksWithoutValueEmitNothing()
    {
        // Undefeated Mantra aura tick on ourselves: layout 0, no value.
        var events = new List<GameEvent>();
        Assert.True(PacketParser.Parse(Hex("0438 b152 00 00 b152 c58e1501 06 02 ffc46b6c 01000000 c253 01 00"), 0, events.Add));
        Assert.Empty(events);
    }

    [Fact]
    public void ParsesZeroPadBeforeScalar()
    {
        var hit = Assert.IsType<DamageEvent>(Assert.Single(
            Parse("0438 b1ea01 06 00 f30a 40c0f400 7a 02 00 00 00 0b199b5f 01000000 00 ac52 a40d 01 00")));
        Assert.Equal(1700, hit.Amount);
        Assert.Equal(10540u, hit.PowerScalar);
    }

    [Fact]
    public void ParsesChainedRecords()
    {
        const string record = "b1ea01 06 00 f30a 40c0f400 7a 02 00 00 00 0b199b5f 01000000 ac52 a40d 01 00";
        Assert.Equal(2, Parse($"0438 {record} {record}").Count);
    }

    [Fact]
    public void SpiritLinkIsNotDamage()
    {
        // skill 16990000 = 0x01033F30
        var link = Assert.IsType<SummonLinkEvent>(Assert.Single(
            Parse("0438 b1ea01 04 00 f30a 303f0301 7a 02 0b199b5f 01000000 ac52 a40d 01 00")));
        Assert.Equal(1395u, link.SummonId);
        Assert.Equal(30001u, link.OwnerId);
    }

    [Fact]
    public void RejectsMismatchedTerminator() =>
        Assert.Empty(Parse("0438 ab8303 04 00 b152 a0f61201 13 02 9556686b 02000000 c253 da02 07 00"));

    [Fact]
    public void RejectsGarbage() => Assert.Empty(Parse("0438 01 02 03 04 05 06 07 08 09 0a 0b 0c 0d 0e 0f"));
}

public class RecordParserTests
{
    [Fact]
    public void ParsesSelfIdentity()
    {
        var self = Assert.IsType<SelfIdentityEvent>(Assert.Single(
            Parse("3336 ed74 5e91c128 37 06 4e6169636861 1805 1e000000 01 1c000000")));
        Assert.Equal(14957u, self.EntityId);
        Assert.Equal("Naicha", self.Name);
        Assert.Equal((ushort)1304, self.ServerId);
        Assert.Equal(GameClass.Cleric, self.Class);
        Assert.Equal(28u, self.Level);
    }

    [Fact]
    public void ParsesBurnDot()
    {
        // 812a = 5377, 0a = damage tick, bdd101 = actor, 9002 = ?, skill 120001211/100 = 1200012
        var dot = Assert.IsType<DamageEvent>(Assert.Single(Parse("0538 812a 0a bdd101 9002 bb122707 33")));
        Assert.Equal(1200012u, dot.SkillId);
        Assert.Equal(51, dot.Amount);
        Assert.True(dot.Flags.HasFlag(HitFlags.OverTime));
    }

    [Fact]
    public void ParsesMapLoadAndZoneChange()
    {
        Assert.Equal(600021u, Assert.IsType<MapLoadEvent>(Assert.Single(Parse("2136 01000000 d5270900"))).MapId);
        Assert.IsType<ZoneChangeEvent>(Assert.Single(Parse("2336 00 01 02")));
    }

    [Fact]
    public void ParsesLiveNpcSpawn()
    {
        var npc = Assert.IsType<NpcSpawnEvent>(Assert.Single(Parse(
            "4136 a0ea03042000110e20000002ba672f47912cd047000ab8460e5b9d43cbdf0194a70794a707640000006400000000000000000000000000000000000000640000006400000001000000000000000000000000000000000000000600010021000000acb6a39c00")));
        Assert.Equal(62752u, npc.EntityId);
        Assert.Equal(2100753u, npc.NpcCode); // Highland Odyle Sparkie
        Assert.Equal(119700u, npc.MaxHp);
    }

    [Fact]
    public void ParsesRemainingHp()
    {
        var hp = Assert.IsType<HpUpdateEvent>(Assert.Single(Parse("008D ab8303 020100 77c50100 00000000")));
        Assert.Equal(49579u, hp.EntityId);
        Assert.Equal(116087u, hp.Hp);
    }

    [Fact]
    public void ParsesNpcSpawnWithCodeAndHp()
    {
        // id 300, kind 0x0D, no name, code 2400032 (0x249F20) before 00 40 02, then 01 cur max
        var npc = Assert.IsType<NpcSpawnEvent>(Assert.Single(
            Parse("4136 ac02 0d000000 00 aa bb 209f24 004002 ff 01 c0843d c0843d")));
        Assert.Equal(300u, npc.EntityId);
        Assert.Equal(0x0D, npc.Kind);
        Assert.Equal(2400032u, npc.NpcCode);
        Assert.Equal(1_000_000u, npc.MaxHp);
    }
}

public class SelfDetectionTests
{
    [Fact]
    public void OwnStatsIdentifiesUsWithoutTeleport()
    {
        var hint = Assert.IsType<SelfHintEvent>(Assert.Single(TestPackets.Parse("4A36 b152 000000000000000000")));
        Assert.Equal(10545u, hint.EntityId);
    }
}

public class CombatPowerTests
{
    [Fact]
    public void SelfIdentityCarriesCombatPower()
    {
        // ... level u32, then (elsewhere in the record) cp, 0, best, 0, 05 01 91 00
        var self = Assert.IsType<SelfIdentityEvent>(Assert.Single(TestPackets.Parse(
            "3336 ed74 5e91c128 37 06 4e6169636861 1805 1e000000 01 1c000000 0000 05920000 00000000 e4930000 00000000 05019100")));
        Assert.Equal(37381u, self.CombatPower);
    }

    [Fact]
    public void PlayerSpawnCarriesClassAndCombatPower()
    {
        // id 300, mask, name "Test", class 25 (Sorcerer), filler, F4 CB 1F, 8 bytes, cp 50,000, 0
        var player = Assert.IsType<PlayerSpawnEvent>(Assert.Single(TestPackets.Parse(
            "4536 ac02 00000000 01 04 54657374 19000000 0101 aabbcc f4cb1f 0000000000000000 50c30000 00000000")));
        Assert.Equal("Test", player.Name);
        Assert.Equal(GameClass.Sorcerer, player.Class);
        Assert.Equal(50000u, player.CombatPower);
    }
}
