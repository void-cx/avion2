using Avion2.Analysis;
using Avion2.Engine;
using Avion2.Protocol;
using static Avion2.Tests.TestPackets;

namespace Avion2.Tests;

/// <summary>
/// The group finder (<c>97</c> records: room, joins, ready checks, join requests), its notices, the dungeon names, and
/// the boss's target in the party (<c>20 92</c>). The room vectors are capture records with the names, title, room and
/// character numbers replaced.
/// </summary>
public class FinderTests
{
    /// <summary>
    /// <c>02 97</c> after the start (reason 10, state 2): leader Bravo (slot 1, no ready field), then Charlie, Delta,
    /// Foxtrot and Echo, all ready. The bit byte <c>ff</c> after the leader holds "public" and the first seven slot bools.
    /// </summary>
    private const string RoomStarted =
        "0297 56341200 15 4b72616f2072756e2c20616c6c2077656c636f6d65 05 c2270900 0003 1111000000000709 ff 01 02 05" +
        " 0c 01 1111000000000709 05 427261766f 24000000 18000000 a6010000 0709 0709 04 6e53000000000000 00 01 03" +
        " 0e 02 2212000000001e05 07 436861726c6965 1d000000 16000000 e3000000 1e05 1e05 04 0530000000000000 00 01 02" +
        " 0e 03 3313000000002905 05 44656c7461 1e000000 16000000 2c010000 2905 2905 04 b93d000000000000 00 01 01" +
        " 0e 04 5515000000002505 07 466f7874726f74 1a000000 19000000 db010000 2505 2505 04 0b69000000000000 00 01 01" +
        " 0e 05 4414000000001b05 04 4563686f 12000000 2d000000 68060000 03 1b05 1b05 04 521e010000000000 00 01 01" +
        " 00 0a";

    /// <summary><c>02 97</c> as we joined (reason 2, recruiting): three members (Charlie ready), two open slots for any role.</summary>
    private const string RoomJoined =
        "0297 56341200 15 4b72616f2072756e2c20616c6c2077656c636f6d65 05 c2270900 0003 1111000000000709 1f 01 00 05" +
        " 0c 01 1111000000000709 05 427261766f 24000000 18000000 a6010000 0709 0709 04 6e53000000000000 00 01 03" +
        " 0e 02 2212000000001e05 07 436861726c6965 1d000000 16000000 e3000000 1e05 1e05 04 0530000000000000 00 01 01" +
        " 0c 03 3313000000002905 05 44656c7461 1e000000 16000000 2c010000 2905 2905 04 b93d000000000000 00 01 01" +
        " 00 04 0000000000000000 00 00000000 00000000 00000000 04 0000000000000000 00 00 00" +
        " 00 05 0000000000000000 00 00000000 00000000 00000000 04 0000000000000000 00 00 00" +
        " 00 02";

    [Fact]
    public void RoomRecordReadsEverySlot()
    {
        var events = Parse(RoomStarted);
        var room = Assert.IsType<FinderRoomEvent>(events[0]);
        Assert.Equal((0x123456u, "Krao run, all welcome", (byte)5, 600002u, 0x1111u, FinderRoomState.InDungeon, (byte)10),
            (room.RoomNumber, room.Title, room.Size, room.DungeonId, room.LeaderId, room.State, room.Reason));
        Assert.Equal(["Bravo", "Charlie", "Delta", "Foxtrot", "Echo"], room.Slots.Select(s => s.Name));
        Assert.Equal([GameClass.Chanter, GameClass.Cleric, GameClass.Cleric, GameClass.Sorcerer, GameClass.Assassin],
            room.Slots.Select(s => s.Class));
        Assert.Equal([false, true, true, true, true], room.Slots.Select(s => s.Ready));
        Assert.All(room.Slots, s => Assert.True(s.Online));
        var echo = room.Slots[4];
        Assert.Equal((0x1444u, 45u, 1640u, 73298u, FinderRole.Any, false), (echo.CharacterId, echo.Level, echo.ItemLevel,
            echo.CombatPower, echo.Role, echo.Inside));

        // The members also come as the finder roster (runs and loot owners use it), without the leader's party id
        var roster = Assert.IsType<PartyRosterEvent>(events[1]);
        Assert.Null(roster.LeaderId);
        Assert.Equal([0x1111u, 0x1222u, 0x1333u, 0x1555u, 0x1444u], roster.Members.Select(m => m.CharacterId));

        Assert.Empty(Parse(RoomStarted + " 00").OfType<FinderRoomEvent>()); // a byte too many: not this layout
    }

    [Fact]
    public void OpenSlotsKeepTheRoleTheRoomAsksFor()
    {
        var room = Assert.IsType<FinderRoomEvent>(Parse(RoomJoined)[0]);
        Assert.Equal((FinderRoomState.Recruiting, (byte)2), (room.State, room.Reason));
        Assert.Equal([false, false, false, true, true], room.Slots.Select(s => s.IsOpen));
        Assert.Equal([false, true, false, false, false], room.Slots.Select(s => s.Ready));
        Assert.All(room.Slots.Where(s => s.IsOpen), s => Assert.Equal((FinderRole.Any, string.Empty, 0u), (s.Role, s.Name, s.CombatPower)));
        Assert.Equal(3, Assert.IsType<PartyRosterEvent>(Parse(RoomJoined)[1]).Members.Count);
    }

    [Fact]
    public void JoinedAndUpdatedSlots()
    {
        // 0B 97: Echo joined slot 5 (not ready yet)
        var joined = Assert.IsType<FinderSlotEvent>(Assert.Single(Parse(
            "0B97 0c 05 4414000000001b05 04 4563686f 12000000 2d000000 68060000 01 1b05 1b05 04 521e010000000000 00 01 01")));
        Assert.True(joined.Joined);
        Assert.Equal(("Echo", GameClass.Assassin, 45u, 1640u, 73298u, false), (joined.Slot.Name, joined.Slot.Class, joined.Slot.Level,
            joined.Slot.ItemLevel, joined.Slot.CombatPower, joined.Slot.Ready));

        // 1F 97: Delta inside the instance (mask 1e: ready, both servers, revive items 14)
        var updated = Assert.IsType<FinderSlotEvent>(Assert.Single(Parse(
            "1F97 1e 03 3313000000002905 05 44656c7461 1e000000 16000000 2c010000 03 2905 d210 04 b93d000000000000 00 0e00000000000000 01 00")));
        Assert.False(updated.Joined);
        Assert.Equal((3, "Delta", true, true, true), (updated.Slot.Slot, updated.Slot.Name, updated.Slot.Ready, updated.Slot.Online,
            updated.Slot.Inside));

        Assert.Empty(Parse("0B97 00 05 0000000000000000 00 00000000 00000000 00000000 00 04 0000000000000000 00 00 00")); // open
    }

    [Fact]
    public void JoinRequestAndTheSmallRecords()
    {
        // 07 97: room, character, class 31 (Cleric), level 45, item level 1512, name, server 2318, 0, CP 81957, 1, expiry
        var request = Assert.IsType<FinderJoinRequestEvent>(Assert.Single(Parse(
            "0797 56341200 0010000000000e09 1f000000 2d000000 e8050000 05 416c706861 0e09 00000000 2540010000000000 01 969bac9da1010000")));
        Assert.Equal(new FinderJoinRequestEvent(1000, 0x123456, 0x1000, GameClass.Cleric, 45, 1512, "Alpha", 2318, 81957, 0x01A1_9DAC_9B96),
            request);

        Assert.Equal(new FinderJoinRequestCancelledEvent(1000, 0x1000), Assert.Single(Parse("2597 0010000000000e09")));
        Assert.Equal(new FinderReadyEvent(1000, 0x1222, true), Assert.Single(Parse("1697 2212000000001e05 01")));
        Assert.Equal(new FinderReadyEvent(1000, 0x1222, false), Assert.Single(Parse("1697 2212000000001e05 00")));
        Assert.Equal(new FinderReadyEvent(1000, null, true), Assert.Single(Parse("1597 0000 01")));
        Assert.Equal(new FinderStartEvent(1000), Assert.Single(Parse("1897 0000")));
        Assert.Equal(new FinderLeftEvent(1000), Assert.Single(Parse("1D97 0000")));
        Assert.Equal(new FinderRoomClosedEvent(1000, 0x123456), Assert.Single(Parse("1B97 56341200")));
        Assert.Empty(Parse("1897 0d3c")); // an error result is not a start
    }

    [Fact]
    public void BossTargetRecordAndTracker()
    {
        // 20 92: entity 3336 (varint 88 1a); a single 00 clears it
        Assert.Equal(new PartyBossTargetEvent(1000, 3336), Assert.Single(Parse("2092 881a")));
        Assert.Equal(new PartyBossTargetEvent(1000, 0), Assert.Single(Parse("2092 00")));

        var tracker = new BossTargetTracker();
        tracker.Apply(new PartyBossTargetEvent(0, 3336));
        Assert.Equal(3336u, tracker.EntityId);
        tracker.Apply(new PartyBossTargetEvent(1, 0));
        Assert.Null(tracker.EntityId);
        tracker.Apply(new PartyBossTargetEvent(2, 12033));
        tracker.Apply(new ZoneChangeEvent(3)); // entity ids are reissued
        Assert.Null(tracker.EntityId);

        var session = new GameSession();
        session.Apply(new PartyBossTargetEvent(4, 9601));
        Assert.Equal(9601u, session.BossTarget.EntityId);
    }

    [Fact]
    public void MemberRoomFullReadyAndStarting()
    {
        var (tracker, registry, alerts) = Tracker();
        tracker.Apply(new SelfIdentityEvent(0, 1, "Delta", 0, GameClass.Cleric, 22), registry);
        tracker.Apply(Parse(RoomJoined)[0], registry);
        var room = tracker.Snapshot(registry)!;
        Assert.Equal((3, false, "Delta"), (room.Filled, room.SelfIsLeader, room.Slots.Single(s => s.IsSelf).Name));
        Assert.True(room.Slots[0].IsLeader);

        tracker.Apply(new FinderSlotEvent(10, Slot(4, 0x5555, "Foxtrot"), Joined: true), registry);
        tracker.Apply(new FinderSlotEvent(20, Slot(5, 0x1444, "Echo"), Joined: true), registry);
        tracker.Apply(new FinderReadyEvent(30, null, true), registry); // us
        tracker.Apply(new FinderReadyEvent(40, 0x5555, true), registry);
        Assert.Empty(alerts);
        tracker.Apply(new FinderReadyEvent(50, 0x1444, true), registry);
        Assert.Equal(FinderAlertKind.FullAndReady, Assert.Single(alerts).Kind);
        Assert.True(tracker.Snapshot(registry)!.FullAndReady);

        // Someone unreadies and readies again: a second notice; repeats of the same state: none
        tracker.Apply(new FinderReadyEvent(60, 0x1444, false), registry);
        tracker.Apply(new FinderReadyEvent(70, 0x1444, true), registry);
        tracker.Apply(new FinderReadyEvent(80, 0x1444, true), registry);
        Assert.Equal(2, alerts.Count(a => a.Kind == FinderAlertKind.FullAndReady));

        // Members get no 18 97: the room turning to "in the dungeon" is the start, once
        tracker.Apply(Parse(RoomStarted)[0], registry);
        tracker.Apply(Parse(RoomStarted)[0], registry);
        Assert.Equal(1, alerts.Count(a => a.Kind == FinderAlertKind.Starting));
        Assert.Equal(FinderRoomState.InDungeon, tracker.Snapshot(registry)!.State);

        tracker.Apply(new FinderLeftEvent(100), registry);
        Assert.Null(tracker.Snapshot(registry));
    }

    [Fact]
    public void LeaderGetsJoinRequestsUntilTheyEnd()
    {
        var (tracker, registry, alerts) = Tracker();
        // Reason 1: we created the room, so its leader is us (no name needed)
        var created = (FinderRoomEvent)Parse(RoomJoined)[0] with { Reason = 1, TimestampMs = 0 };
        tracker.Apply(created, registry);
        Assert.True(tracker.Snapshot(registry)!.SelfIsLeader);

        var request = new FinderJoinRequestEvent(1_000, 0x123456, 0x7777, GameClass.Cleric, 45, 1512, "Golf", 2318, 81957, 21_000);
        tracker.Apply(request, registry);
        var alert = Assert.Single(alerts);
        Assert.Equal((FinderAlertKind.JoinRequest, "Golf", 81957u), (alert.Kind, alert.Request!.Name, alert.Request.CombatPower));
        Assert.Equal("Golf", Assert.Single(tracker.Snapshot(registry)!.Requests).Name);

        // Asked again soon: still one request, no second notice; withdrawn: gone
        tracker.Apply(request with { TimestampMs = 5_000, ExpiresAtMs = 25_000 }, registry);
        Assert.Single(alerts);
        Assert.Single(tracker.Snapshot(registry)!.Requests);
        tracker.Apply(new FinderJoinRequestCancelledEvent(6_000, 0x7777), registry);
        Assert.Empty(tracker.Snapshot(registry)!.Requests);

        // Accepted: the join removes it; another one runs out
        tracker.Apply(request with { TimestampMs = 40_000, ExpiresAtMs = 60_000 }, registry);
        Assert.Equal(2, alerts.Count);
        tracker.Apply(new FinderSlotEvent(41_000, Slot(4, 0x7777, "Golf"), Joined: true), registry);
        Assert.Empty(tracker.Snapshot(registry)!.Requests);
        tracker.Apply(request with { CharacterId = 0x8888, Name = "Hotel", TimestampMs = 42_000, ExpiresAtMs = 62_000 }, registry);
        tracker.Apply(new FinderReadyEvent(63_000, 0x2222, true), registry);
        Assert.Empty(tracker.Snapshot(registry)!.Requests);

        // The leader's start (18 97), then the room record saying the same: one notice
        tracker.Apply(new FinderStartEvent(70_000), registry);
        tracker.Apply(Parse(RoomStarted)[0], registry);
        Assert.Equal(1, alerts.Count(a => a.Kind == FinderAlertKind.Starting));
    }

    [Fact]
    public void RoomsComeAndGo()
    {
        var (tracker, registry, alerts) = Tracker();
        // First seen already inside: no start notice
        tracker.Apply(Parse(RoomStarted)[0], registry);
        Assert.Empty(alerts);
        Assert.Equal(5, tracker.Snapshot(registry)!.Filled);

        tracker.Apply(new FinderRoomClosedEvent(1, 0x999999), registry); // another room's number
        Assert.NotNull(tracker.Snapshot(registry));
        tracker.Apply(new FinderRoomClosedEvent(2, 0x123456), registry);
        Assert.Null(tracker.Snapshot(registry));

        // Slot records without a room are ignored; another character drops the room
        tracker.Apply(new FinderSlotEvent(3, Slot(4, 0x5555, "Foxtrot"), Joined: true), registry);
        Assert.Null(tracker.Snapshot(registry));
        tracker.Apply(new SelfIdentityEvent(4, 1, "Delta", 0, GameClass.Cleric, 22), registry);
        tracker.Apply(Parse(RoomJoined)[0], registry);
        tracker.Apply(new SelfIdentityEvent(5, 1, "Kilo", 0, GameClass.Gladiator, 45), registry);
        Assert.Null(tracker.Snapshot(registry));
    }

    [Fact]
    public void NoticeTexts()
    {
        var request = new FinderJoinRequestView(0x7777, "Golf", GameClass.Cleric, 45, 1512, 81957, 2318, 0);
        Assert.Equal("Join request: Cleric Lv 45, iLv 1512, CP 81,957 · Player 2",
            FinderNoticeText.Text(new FinderAlert(FinderAlertKind.JoinRequest, 1, request), _ => "Player 2"));
        Assert.Equal("Room full, everyone ready", FinderNoticeText.Text(new FinderAlert(FinderAlertKind.FullAndReady, 1), n => n));
        Assert.Equal("The dungeon is starting", FinderNoticeText.Text(new FinderAlert(FinderAlertKind.Starting, 1), n => n));
    }

    [Fact]
    public void DungeonNamesComeFromTheBossesOfThatId()
    {
        const string npcs = """
            {
              "2300104": { "name": "Enhanced Harcon", "isBoss": true, "tier": "Conquest [Normal]", "dungeonId": 600001 },
              "2300108": { "name": "Neglected Gadioton", "isBoss": true, "tier": "Conquest [Normal]", "dungeonId": 600001 },
              "2300999": { "name": "Nobody Knows", "isBoss": true, "dungeonId": 600021 },
              "2300105": { "name": "Sunken Jailor", "isBoss": false }
            }
            """;
        var catalog = new DungeonCatalog([
            new DungeonInfo("krao-cave", "Krao Cave", "Party Dungeon", null, null, 5, null,
                [new DungeonBoss("Enhanced Harcon", null), new DungeonBoss("Neglected Gadioton", null)]),
        ]);
        var names = FinderDungeons.Build(npcs, catalog);
        Assert.Equal("Krao Cave (Conquest [Normal])", FinderDungeons.Name(names, 600001));
        Assert.Equal("Dungeon 600021", FinderDungeons.Name(names, 600021)); // its bosses are in no dungeon of the table

        // The built-in tables
        Assert.Equal("Krao Cave (Exploration)", FinderData.DungeonName(600002));
        Assert.Equal("Vakron Sky Island (Conquest [Normal])", FinderData.DungeonName(600072));
    }

    [Fact]
    public void AnonymizerHidesNamesAndTheTitle()
    {
        var anonymizer = new NameAnonymizer();
        var room = Assert.IsType<FinderRoomEvent>(anonymizer.Rewrite(Parse(RoomJoined)[0]));
        Assert.Equal("Group finder room", room.Title);
        Assert.DoesNotContain(room.Slots, s => s.Name is "Bravo" or "Charlie" or "Delta");
        Assert.Equal(string.Empty, room.Slots[4].Name);
        var request = Assert.IsType<FinderJoinRequestEvent>(anonymizer.Rewrite(
            new FinderJoinRequestEvent(0, 1, 0x7777, GameClass.Cleric, 45, 1512, "Golf", 2318, 81957, 0)));
        Assert.NotEqual("Golf", request.Name);
    }

    private static (FinderTracker Tracker, EntityRegistry Registry, List<FinderAlert> Alerts) Tracker()
    {
        var tracker = new FinderTracker();
        var alerts = new List<FinderAlert>();
        tracker.Alert += alerts.Add;
        return (tracker, new EntityRegistry(), alerts);
    }

    private static FinderSlot Slot(byte slot, uint character, string name, bool ready = false) =>
        new(slot, character, name, GameClass.Sorcerer, 25, 475, 26891, FinderRole.Any, ready, Online: true, Inside: false);
}
