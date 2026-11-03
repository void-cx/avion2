using System.Buffers.Binary;
using Avion2.Analysis;
using Avion2.Protocol;
using Avion2.Protocol.Parsers;

namespace Avion2.Tests;

public class LootTests
{
    private const uint Me = 100;
    private const uint MyCharacter = 123456;
    private const uint OtherCharacter = 654321;
    private const uint Chest = 17279;

    /// <summary>A <c>1E 56</c> record (63 bytes) as the game lays it out; ids are made up.</summary>
    private static byte[] DropRecord(uint source, long uid, uint item, uint count, uint owner)
    {
        var record = new byte[63];
        BinaryPrimitives.WriteUInt32LittleEndian(record, source);
        BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(4), 1000f); // the source's position
        BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(16), 1100f); // the item's own spot
        BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(20), -200f);
        BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(24), 30f);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(28), uid);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(36), item);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(40), count);
        record[44] = 2; // owner type: a player
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(45), owner);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(51), 1321);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(53), 1_761_908_994_543);
        record[61] = 1;
        record[62] = 3;
        return record;
    }

    private static byte[] Batch(params byte[][] records) => [(byte)records.Length, .. records.SelectMany(r => r), 1];

    private static byte[] TakeRecord(uint source, long uid, uint taker)
    {
        var record = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(record, source);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(4), uid);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(12), taker);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(18), taker == 0 ? (ushort)0 : (ushort)1321);
        return record;
    }

    private static byte[] Pick(long uid, ushort result = 0)
    {
        var body = new byte[11];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(2), uid);
        return body;
    }

    private delegate bool Parser(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit);

    private static List<GameEvent> Parse(Parser parser, byte[] body, long ts)
    {
        var events = new List<GameEvent>();
        Assert.True(parser(body, ts, events.Add));
        return events;
    }

    [Fact]
    public void PickAnswerCarriesTheDropUid()
    {
        // 21 56 as recorded: result 0, uid, auto-loot 0
        var picked = Assert.IsType<LootPickedEvent>(Assert.Single(Parse(LootParsers.ParseItemLooting, Convert.FromHexString("0000c45c0000b0595c0000"), 5)));
        Assert.Equal((ushort)0, picked.Result);
        Assert.Equal(0x005C59B0_00005CC4L, picked.DropUid);
        Assert.False(picked.AutoLoot);
        Assert.False(LootParsers.ParseItemLooting(Convert.FromHexString("0000c45c0000b0595c00"), 5, _ => { }));
    }

    [Fact]
    public void DropAndTakeReadOneUid()
    {
        // The uid's low half repeats between drops (read earlier as a "source" u32): only the whole i64 is unique
        var events = Parse(InventoryParsers.ParseLootDrop, Batch(DropRecord(Chest, 0x002D9332_00005E19L, 533460001, 180, MyCharacter)), 1_000);
        var drop = Assert.IsType<LootDropEvent>(Assert.Single(events));
        Assert.Equal((Chest, 0x002D9332_00005E19L, 533460001u, 180u, MyCharacter, (ushort)1321),
            (drop.SourceId, drop.Uid, drop.ItemId, drop.Count, drop.OwnerCharacterId, drop.OwnerServerId));
        Assert.Equal(new Vec3(1100f, -200f, 30f), drop.Position);

        var take = Assert.IsType<LootTakenEvent>(Assert.Single(Parse(InventoryParsers.ParseLootTaken, Batch(TakeRecord(Chest, 0x002D9332_00005E19L, MyCharacter)), 2_000)));
        Assert.Equal((Chest, 0x002D9332_00005E19L, MyCharacter), (take.SourceId, take.Uid, take.TakerCharacterId));
    }

    [Fact]
    public void ChestDropsShareTheirSourceButEachPickGetsItsOwnItem()
    {
        // One chest drops three items (same source id); we take them in another order. Joining by the source gave the
        // last drop's item to the first take and lost the other two
        var loot = new LootTracker();
        var picks = new List<LootPickup>();
        loot.PickedUp += picks.Add;
        loot.Apply(new SelfIdentityEvent(0, Me, "TestDaeva", 1321, GameClass.Chanter, 45) { CharacterId = MyCharacter }, "TestDaeva");
        Parse(InventoryParsers.ParseLootDrop, Batch(
            DropRecord(Chest, 0x2D932C_00005E19L, 531900009, 1000, MyCharacter),
            DropRecord(Chest, 0x2D932D_00005E19L, 533460001, 180, MyCharacter),
            DropRecord(Chest, 0x2D932E_00005E19L, 630840002, 10, MyCharacter)), 1_000).ForEach(e => loot.Apply(e, "TestDaeva"));
        foreach (long uid in new[] { 0x2D932E_00005E19L, 0x2D932C_00005E19L, 0x2D932D_00005E19L })
        {
            Parse(LootParsers.ParseItemLooting, Pick(uid), 1_400).ForEach(e => loot.Apply(e, "TestDaeva"));
        }

        Parse(InventoryParsers.ParseLootTaken, Batch(
            TakeRecord(Chest, 0x2D932E_00005E19L, MyCharacter),
            TakeRecord(Chest, 0x2D932C_00005E19L, MyCharacter),
            TakeRecord(Chest, 0x2D932D_00005E19L, MyCharacter)), 1_400).ForEach(e => loot.Apply(e, "TestDaeva"));

        Assert.Equal([(630840002u, 10u), (531900009u, 1000u), (533460001u, 180u)], picks.Select(p => (p.ItemId, p.Count)));
        Assert.All(picks, p => Assert.Equal(Chest, p.SourceId));
    }

    [Fact]
    public void PickAnswerMakesTheTakeOursBeforeOurIdIsKnown()
    {
        var loot = new LootTracker();
        var picks = new List<LootPickup>();
        loot.PickedUp += picks.Add;
        loot.Apply(new LootDropEvent(1_000, 900, 11, 182400001, 1, MyCharacter, 1321, default), null);
        loot.Apply(new LootDropEvent(1_000, 901, 12, 182400002, 2, OtherCharacter, 1321, default), null);
        loot.Apply(new LootDropEvent(1_000, 902, 13, 182400003, 3, MyCharacter, 1321, default), null);

        loot.Apply(new LootTakenEvent(1_500, 901, 12, OtherCharacter), null); // someone else's: no answer of ours
        loot.Apply(new LootPickedEvent(2_000, 0, 11, false), null);
        loot.Apply(new LootTakenEvent(2_000, 900, 11, MyCharacter), null);
        Assert.Equal(MyCharacter, loot.SelfCharacterId); // learned from the take we answered

        loot.Apply(new LootTakenEvent(3_000, 902, 13, MyCharacter), null); // no answer, but our id is known now
        Assert.Equal([182400001u, 182400003u], picks.Select(p => p.ItemId));
    }

    [Fact]
    public void TakeBeforeItsAnswerStillCounts()
    {
        var loot = new LootTracker();
        var picks = new List<LootPickup>();
        loot.PickedUp += picks.Add;
        loot.Apply(new LootDropEvent(1_000, 900, 21, 182400001, 1, MyCharacter, 1321, default), null);
        loot.Apply(new LootDropEvent(1_000, 900, 22, 182400002, 1, OtherCharacter, 1321, default), null);
        loot.Apply(new LootDropEvent(1_000, 900, 23, 182400003, 1, MyCharacter, 1321, default), null);
        loot.Apply(new LootTakenEvent(2_000, 900, 22, OtherCharacter), null);
        loot.Apply(new LootTakenEvent(2_000, 900, 23, MyCharacter), null);
        loot.Apply(new LootTakenEvent(2_000, 900, 21, MyCharacter), null);
        Assert.Empty(picks); // whose these were is not known yet

        loot.Apply(new LootPickedEvent(2_000, 0, 21, false), null);
        // The answer names us: our other take waiting counts too, the other player's does not
        Assert.Equal([182400001u, 182400003u], picks.Select(p => p.ItemId));
        Assert.Equal(MyCharacter, loot.SelfCharacterId);
    }

    [Fact]
    public void RefusedPickIsNotOurs()
    {
        var loot = new LootTracker();
        var picks = new List<LootPickup>();
        loot.PickedUp += picks.Add;
        loot.Apply(new LootDropEvent(1_000, 900, 31, 182400001, 1, OtherCharacter, 1321, default), null);
        loot.Apply(new LootPickedEvent(1_200, 1, 31, false), null);
        loot.Apply(new LootTakenEvent(1_300, 900, 31, OtherCharacter), null);
        Assert.Empty(picks);
        Assert.Null(loot.SelfCharacterId);
    }

    [Fact]
    public void PickWithoutOurIdCountsForTheRun()
    {
        // A capture that started after our identity record: the pick-up answer alone makes the soul ours
        var session = new GameSession();
        DungeonRun? run = null;
        session.Runs.RunEnded += r => run = r;
        session.Apply(new MapLoadEvent(500, 152003, 37));
        session.Apply(new LootDropEvent(1_000, 900, 41, 534660001, 1, MyCharacter, 1321, default));
        session.Apply(new LootPickedEvent(2_000, 0, 41, false));
        session.Apply(new LootTakenEvent(2_000, 900, 41, MyCharacter));
        session.Runs.Finish(70_000);
        Assert.Equal(new RunItem(534660001, 1), Assert.Single(run!.Loot));
    }

    [Fact]
    public void LegionHallRecord()
    {
        // 45 8A as recorded: result 0, hall 1, the rent's end (2026-11-08); and the form without a hall
        var hall = Assert.IsType<LegionHallInfoEvent>(Assert.Single(Parse(LootParsers.ParseLegionHallInfo,
            Convert.FromHexString("00000100000067d930bca1010000"), 5)));
        Assert.Equal(((ushort)0, 1u, 0x01A1BC30D967L), (hall.Result, hall.HallId, hall.RentEndsMs));
        Assert.Equal(new DateTime(2026, 11, 8), DateTimeOffset.FromUnixTimeMilliseconds(hall.RentEndsMs).UtcDateTime.Date);

        var none = Assert.IsType<LegionHallInfoEvent>(Assert.Single(Parse(LootParsers.ParseLegionHallInfo, new byte[14], 5)));
        Assert.Equal((0u, 0L), (none.HallId, none.RentEndsMs));
        Assert.False(LootParsers.ParseLegionHallInfo(new byte[13], 5, _ => { }));
    }

    [Fact]
    public void HallRentGoesToTheNamedCharacterOnly()
    {
        long ends = new DateTimeOffset(2026, 11, 8, 17, 24, 40, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var session = new GameSession();
        session.Apply(new LegionHallInfoEvent(100, 0, 1, ends)); // before our name (as at login): whose is not known
        Assert.Null(session.Legion.HallRentEndsMs);

        session.Apply(new SelfIdentityEvent(200, Me, "TestDaeva", 1321, GameClass.Chanter, 45));
        session.Apply(new LegionHallInfoEvent(300, 0, 1, ends));
        Assert.Equal(ends, session.Legion.HallRentEndsMs);
        Assert.Equal("TestDaeva", session.Legion.Character);

        // Another character: the old rent is forgotten, the new one says there is no hall
        session.Apply(new SelfIdentityEvent(400, Me + 1, "OtherDaeva", 1321, GameClass.Cleric, 30));
        Assert.Null(session.Legion.HallRentEndsMs);
        session.Apply(new LegionHallInfoEvent(500, 0, 0, 0));
        Assert.Equal(0L, session.Legion.HallRentEndsMs);
        Assert.Null(session.Legion.HallRentText(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void HallRentText()
    {
        var now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.FromHours(1));
        var legion = new LegionState { Name = "Test Legion" };
        Assert.Null(legion.HallRentText(now)); // no record seen: nothing shown

        legion.HallRentEndsMs = now.AddDays(5).AddHours(6).ToUnixTimeMilliseconds();
        Assert.Equal("Legion Hall rent ends in 5 days (Sun, 08 Nov)", legion.HallRentText(now));
        legion.HallRentEndsMs = now.AddHours(30).ToUnixTimeMilliseconds();
        Assert.Equal("Legion Hall rent ends in 1 day (Wed, 04 Nov)", legion.HallRentText(now));
        legion.HallRentEndsMs = now.AddHours(5).ToUnixTimeMilliseconds();
        Assert.Equal("Legion Hall rent ends in 5 h (Tue, 03 Nov)", legion.HallRentText(now));
        legion.HallRentEndsMs = now.AddDays(-1).ToUnixTimeMilliseconds();
        Assert.Equal("Legion Hall rent ended Mon, 02 Nov", legion.HallRentText(now));
    }

    [Fact]
    public void SavedHallRentIsKeptUntilTheGameSendsItAgain()
    {
        var book = new LegionBook();
        Assert.True(book.Take(new LegionState { Character = "TestDaeva", Name = "Test Legion", HallRentEndsMs = 1_762_622_680_423 }));
        Assert.True(book.Take(new LegionState { Character = "TestDaeva", Name = "Test Legion" })); // this session: no record yet
        Assert.Equal(1_762_622_680_423, book.For("TestDaeva")!.HallRentEndsMs);
        Assert.True(book.Take(new LegionState { Character = "TestDaeva", HallRentEndsMs = 0 })); // the hall is gone
        Assert.Equal(0L, book.For("TestDaeva")!.HallRentEndsMs);
        Assert.Equal("Test Legion", book.For("TestDaeva")!.Name);

        string json = System.Text.Json.JsonSerializer.Serialize(book);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<LegionBook>(json)!;
        Assert.Equal(0L, loaded.For("TestDaeva")!.HallRentEndsMs);
    }

    [Fact]
    public void InstanceEntryCounters()
    {
        // 08 61 / 09 61 as recorded at an instance entry (0 in all 113 / 95 records)
        Assert.Equal((byte)0, Assert.IsType<DungeonClearKillNpcEvent>(Assert.Single(Parse(LootParsers.ParseDungeonClearKillNpc, [0], 5))).Value);
        Assert.Equal(0u, Assert.IsType<DungeonDeathLimitEvent>(Assert.Single(Parse(LootParsers.ParseDungeonDeathLimit, new byte[4], 5))).Value);
        Assert.Equal(3u, Assert.IsType<DungeonDeathLimitEvent>(Assert.Single(Parse(LootParsers.ParseDungeonDeathLimit, [3, 0, 0, 0], 5))).Value);
        Assert.False(LootParsers.ParseDungeonDeathLimit(new byte[5], 5, _ => { }));
        Assert.False(LootParsers.ParseDungeonClearKillNpc([], 5, _ => { }));

        var events = new List<GameEvent>();
        Assert.True(PacketParser.Parse([0x09, 0x61, 0, 0, 0, 0], 5, events.Add));
        Assert.IsType<DungeonDeathLimitEvent>(Assert.Single(events));
    }

    [Fact]
    public void DespawnActIsTheThirdField()
    {
        // 42 36: entity, reason (0 in all 17,427 records), despawn act 3 (died in combat)
        var events = new List<GameEvent>();
        Assert.True(RecordParsers.ParseDeath([0xE5, 0x2B, 0x00, 0x03], 5, events.Add));
        var death = Assert.IsType<DeathEvent>(Assert.Single(events));
        Assert.Equal((5605u, DeathKind.Combat), (death.EntityId, death.Kind));
    }
}
