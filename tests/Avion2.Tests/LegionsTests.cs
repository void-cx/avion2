using System.Text.Json;
using Avion2.Analysis;
using Avion2.Engine;
using Avion2.Protocol;

namespace Avion2.Tests;

/// <summary>The legions seen: the store (merge, per server, stale members), the ranking, the spotter and the page's names.</summary>
public class LegionsTests
{
    private const ushort Server = 1101;
    private const ushort OtherServer = 2001;
    private const long Day = 24 * 60 * 60 * 1000L;
    private const long Now = 1_790_000_000_000;

    private static LegionSighting Seen(string name, string? legion, uint? power = null, uint? itemLevel = null, long at = Now,
        ushort? server = Server, GameClass cls = GameClass.Unknown, PowerSource source = PowerSource.Spawn) =>
        new(name, server, legion, cls, itemLevel, power, at, source);

    [Fact]
    public void SightingsMergeIntoTheirLegion()
    {
        var book = new LegionsSeenBook();
        Assert.True(book.Take(Seen("Alpha", "Knights", power: 50_000, cls: GameClass.Chanter, at: Now - 1000)));
        Assert.True(book.Take(Seen("Alpha", "Knights", itemLevel: 1_400, at: Now)));
        Assert.True(book.Take(Seen("Bravo", "Knights", power: 40_000)));

        var knights = Assert.Single(book.On(Server));
        Assert.Equal("Knights", knights.Name);
        var alpha = knights.Members.Single(m => m.Name == "Alpha");
        // class and combat power kept from before; the item level added; last seen moves on
        Assert.Equal((GameClass.Chanter, 50_000u, 1_400u, Now), (alpha.Class, alpha.CombatPower, alpha.ItemLevel, alpha.LastSeenMs));
        Assert.Equal(2, knights.Members.Count);
        Assert.False(book.Take(Seen("", "Knights")));
    }

    [Fact]
    public void LegionsAndPlayersAreKeptPerServer()
    {
        var book = new LegionsSeenBook();
        book.Take(Seen("Alpha", "Knights", power: 50_000));
        book.Take(Seen("Alpha", "Knights", power: 30_000, server: OtherServer));
        book.Take(Seen("Charlie", "Ravens", server: OtherServer));

        Assert.Equal([OtherServer, Server], book.Servers); // most legions first
        Assert.Equal(50_000u, book.Find(Server, "Knights")!.Members.Single().CombatPower);
        Assert.Equal(30_000u, book.Find(OtherServer, "Knights")!.Members.Single().CombatPower);
        Assert.Null(book.Find(Server, "Ravens"));
    }

    [Fact]
    public void APlayerSeenInAnotherLegionMovesThere()
    {
        var book = new LegionsSeenBook();
        book.Take(Seen("Alpha", "Knights", at: Now - 5000));
        book.Take(Seen("Alpha", "Ravens", at: Now));

        Assert.Null(book.Find(Server, "Knights")); // left without members
        Assert.Equal("Alpha", book.Find(Server, "Ravens")!.Members.Single().Name);

        // a late, older sighting does not move them back
        Assert.False(book.Take(Seen("Alpha", "Knights", at: Now - 4000)));
        Assert.Null(book.Find(Server, "Knights"));
    }

    [Fact]
    public void SightingsWithoutLegionOnlyUpdateAPlayerKnownByName()
    {
        var book = new LegionsSeenBook();
        book.Take(Seen("Alpha", "Knights", power: 50_000, at: Now - 1000));
        book.Take(Seen("Delta", "Knights", at: Now - 1000));
        book.Take(Seen("Delta", "Ravens", server: OtherServer, at: Now - 1000));

        // the group finder: name, class, item level, combat power, no server or legion
        Assert.True(book.Take(Seen("Alpha", null, power: 52_000, itemLevel: 1_410, server: null, cls: GameClass.Cleric, source: PowerSource.Game)));
        var alpha = book.Find(Server, "Knights")!.Members.Single(m => m.Name == "Alpha");
        Assert.Equal((52_000u, 1_410u, GameClass.Cleric, PowerSource.Game), (alpha.CombatPower, alpha.ItemLevel, alpha.Class, alpha.Power));

        Assert.False(book.Take(Seen("Nobody", null, power: 1, server: null))); // never placed anywhere
        Assert.False(book.Take(Seen("Delta", null, power: 9, server: null))); // on two servers: which one?
        Assert.True(book.Take(Seen("Delta", null, power: 9, server: OtherServer))); // the server settles it
        Assert.Equal(9u, book.Find(OtherServer, "Ravens")!.Members.Single().CombatPower);
        Assert.Single(book.On(Server));
    }

    [Fact]
    public void ASpawnsCombatPowerDoesNotReplaceASurerOneOfTheSameDay()
    {
        var book = new LegionsSeenBook();
        book.Take(Seen("Alpha", "Knights", power: 50_000, at: Now - Day * 2, source: PowerSource.Site));
        book.Take(Seen("Alpha", "Knights", power: 51_000, at: Now - Day * 2 + 1000, source: PowerSource.Game));
        book.Take(Seen("Alpha", "Knights", power: 49_000, at: Now - Day * 2 + 2000));
        var alpha = book.Find(Server, "Knights")!.Members.Single();
        Assert.Equal((51_000u, PowerSource.Game), (alpha.CombatPower, alpha.Power));
        Assert.Equal(Now - Day * 2 + 2000, alpha.LastSeenMs);

        book.Take(Seen("Alpha", "Knights", power: 53_000, at: Now)); // more than a day later
        Assert.Equal((53_000u, PowerSource.Spawn), (alpha.CombatPower, alpha.Power));
    }

    [Fact]
    public void StaleMembersDropOutOfTheBookAndTheRanking()
    {
        var book = new LegionsSeenBook();
        book.Take(Seen("Alpha", "Knights", power: 50_000, at: Now - Day * 31));
        book.Take(Seen("Bravo", "Knights", power: 10_000, at: Now - Day * 2));
        book.Take(Seen("Charlie", "Ravens", power: 90_000, at: Now - Day * 40));

        var ranked = LegionRanking.Rank(book.On(Server), LegionOrder.CombatPower, Now);
        var knights = Assert.Single(ranked); // Ravens has nobody left in the last 30 days
        Assert.Equal((1, 10_000L), (knights.Members, knights.CombatPower));

        Assert.Equal(2, book.Prune(Now));
        Assert.Equal("Bravo", Assert.Single(book.On(Server)).Members.Single().Name);
    }

    [Fact]
    public void RankingByCombatPowerAndByMembers()
    {
        var book = new LegionsSeenBook();
        // Knights: 3 members, 2 of them with combat power (60k); Ravens: 2 members, 90k; Wolves: 3 members, 65k
        book.Take(Seen("A1", "Knights", power: 30_000));
        book.Take(Seen("A2", "Knights", power: 30_000));
        book.Take(Seen("A3", "Knights"));
        book.Take(Seen("B1", "Ravens", power: 50_000));
        book.Take(Seen("B2", "Ravens", power: 40_000, source: PowerSource.Site));
        book.Take(Seen("C1", "Wolves", power: 20_000, at: Now - 5000));
        book.Take(Seen("C2", "Wolves", power: 20_000, at: Now - 5000));
        book.Take(Seen("C3", "Wolves", power: 25_000, at: Now - 5000));

        var byPower = LegionRanking.Rank(book.On(Server), LegionOrder.CombatPower, Now);
        Assert.Equal(["Ravens", "Wolves", "Knights"], byPower.Select(s => s.Legion.Name));
        Assert.Equal([1, 2, 3], byPower.Select(s => s.Rank));
        Assert.Equal((2, 90_000L, 45_000L, 1), (byPower[0].Members, byPower[0].CombatPower, byPower[0].AverageCombatPower, byPower[0].FromSite));
        Assert.Equal((2, 3, 30_000L), (byPower[2].MembersWithPower, byPower[2].Members, byPower[2].AverageCombatPower));

        // 3 members each: the tie goes to more combat power
        var byMembers = LegionRanking.Rank(book.On(Server), LegionOrder.Members, Now);
        Assert.Equal(["Wolves", "Knights", "Ravens"], byMembers.Select(s => s.Legion.Name));

        // equal in everything: the one seen last first
        book.Take(Seen("D1", "Hawks", power: 90_000, at: Now - 9000));
        book.Take(Seen("D2", "Hawks", at: Now - 9000));
        Assert.Equal(["Ravens", "Hawks"], LegionRanking.Rank(book.On(Server), LegionOrder.CombatPower, Now).Take(2).Select(s => s.Legion.Name));
        Assert.Empty(LegionRanking.Rank([], LegionOrder.Members, Now));
    }

    [Fact]
    public void TheBoardScalesBarsToTheLeaderAndMarksOurLegion()
    {
        var book = new LegionsSeenBook();
        book.Take(Seen("A1", "Knights", power: 40_000));
        book.Take(Seen("B1", "Ravens", power: 80_000));

        var rows = LegionBoard.Rows(book, Server, LegionOrder.CombatPower, "Knights", new NameMask(), Now);
        Assert.Equal(["Ravens", "Knights"], rows.Select(r => r.Name));
        Assert.Equal([1.0, 0.5], rows.Select(r => r.Share));
        Assert.Equal([false, true], rows.Select(r => r.Own));
        Assert.Equal("Knights", rows[1].Key);

        var members = LegionBoard.Rows(book, Server, LegionOrder.Members, null, new NameMask(), Now);
        Assert.Equal([1.0, 1.0], members.Select(r => r.Share));
        Assert.Empty(LegionBoard.Rows(book, OtherServer, LegionOrder.Members, null, new NameMask(), Now));
    }

    [Fact]
    public void StreamerModeShowsStandInsForLegionsAndPlayers()
    {
        var book = new LegionsSeenBook();
        book.Take(Seen("Hero", "Knights", power: 40_000, source: PowerSource.Game));
        book.Take(Seen("Someone", "Knights", power: 30_000));
        book.Take(Seen("Other", "Ravens", power: 80_000));
        book.Take(Seen("Third", "Wolves", power: 10_000));
        var mask = new NameMask { On = true, Self = "Hero" };

        var rows = LegionBoard.Rows(book, Server, LegionOrder.CombatPower, "Knights", mask, Now);
        Assert.Equal(["Legion A", "Your legion", "Legion B"], rows.Select(r => r.Name));
        Assert.Equal(["Ravens", "Knights", "Wolves"], rows.Select(r => r.Key)); // kept for finding them, never shown
        Assert.Equal("Legion A", LegionBoard.Rows(book, Server, LegionOrder.Members, "Knights", mask, Now).Single(r => r.Key == "Ravens").Name);

        var members = LegionBoard.Members(book, Server, "Knights", mask, Now);
        Assert.Equal(["You", "Player 2"], members.Select(m => m.Name));
        Assert.Equal([true, false], members.Select(m => m.Self));

        mask.On = false;
        Assert.Equal(["Ravens", "Knights", "Wolves"], LegionBoard.Rows(book, Server, LegionOrder.CombatPower, "Knights", mask, Now).Select(r => r.Name));
        Assert.Equal(["Hero", "Someone"], LegionBoard.Members(book, Server, "Knights", mask, Now).Select(m => m.Name));
    }

    [Fact]
    public void MembersListStrongestFirstAndSaysWhatCameFromTheSite()
    {
        var book = new LegionsSeenBook();
        book.Take(Seen("Weak", "Knights", power: 10_000, cls: GameClass.Ranger));
        book.Take(Seen("Unknown", "Knights", itemLevel: 1_300));
        book.Take(Seen("Strong", "Knights", power: 70_000, itemLevel: 1_500, source: PowerSource.Site));
        book.Take(Seen("Gone", "Knights", power: 99_000, at: Now - Day * 31));

        var members = LegionBoard.Members(book, Server, "Knights", new NameMask(), Now);
        Assert.Equal(["Strong", "Weak", "Unknown"], members.Select(m => m.Name));
        Assert.True(members[0].FromSite);
        Assert.False(members[1].FromSite);
        Assert.Null(members[2].CombatPower);
        Assert.Empty(LegionBoard.Members(book, Server, "Nobody", new NameMask(), Now));
    }

    [Fact]
    public void TheBookSurvivesSavingAndLoading()
    {
        string path = Path.Combine(Path.GetTempPath(), $"avion2-legions-{Guid.NewGuid():N}.json");
        try
        {
            var book = new LegionsSeenBook();
            book.Take(Seen("Alpha", "Knights", power: 50_000, itemLevel: 1_400, cls: GameClass.Sorcerer, source: PowerSource.Site));
            book.Take(Seen("Old", "Knights", at: Now - Day * 45));
            Assert.True(LegionBoard.Save(book, path, Now));
            Assert.Contains("\"Sorcerer\"", File.ReadAllText(path));

            var loaded = LegionBoard.Load(path, Now);
            var alpha = Assert.Single(loaded.Find(Server, "Knights")!.Members); // the stale one was dropped
            Assert.Equal(("Alpha", GameClass.Sorcerer, 50_000u, 1_400u, PowerSource.Site, Now),
                (alpha.Name, alpha.Class, alpha.CombatPower, alpha.ItemLevel, alpha.Power, alpha.LastSeenMs));

            // moving players keeps working after a load
            loaded.Take(Seen("Alpha", "Ravens", at: Now + 1));
            Assert.Null(loaded.Find(Server, "Knights"));

            Assert.Empty(LegionBoard.Load(path + ".missing", Now).Servers);
            File.WriteAllText(path, "{ not json");
            Assert.Empty(LegionBoard.Load(path, Now).Servers);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AFileWithAPlayerInTwoLegionsKeepsTheLaterOne()
    {
        string json = """
            {"version":1,"legions":[
              {"server":1101,"name":"Knights","members":[{"name":"Alpha","seenMs":100},{"name":"Bravo","seenMs":100}]},
              {"server":1101,"name":"Ravens","members":[{"name":"Alpha","seenMs":200}]},
              {"server":1101,"name":"Wolves","members":[{"name":"Bravo","seenMs":50},{"name":"","seenMs":300}]}
            ]}
            """;
        var book = JsonSerializer.Deserialize<LegionsSeenBook>(json)!;
        Assert.Equal(["Bravo"], book.Find(Server, "Knights")!.Members.Select(m => m.Name));
        Assert.Equal(["Alpha"], book.Find(Server, "Ravens")!.Members.Select(m => m.Name));
        Assert.Null(book.Find(Server, "Wolves"));
    }

    [Fact]
    public void ACharacterLookedUpOnTheSiteIsASightingFromTheSite()
    {
        var profile = new ArmoryProfile("id", "Alpha", "Chanter", 45, 61_234, 1_455, "Elyos", "Knights", Server, "Siel", null,
            [], [], [], 0, [], [], null, null, null, [], DateTimeOffset.FromUnixTimeMilliseconds(Now));
        var seen = LegionBoard.FromProfile(profile, GameClass.Chanter);
        Assert.Equal(new LegionSighting("Alpha", Server, "Knights", GameClass.Chanter, 1_455, 61_234, Now, PowerSource.Site), seen);
        Assert.Null(LegionBoard.FromProfile(profile with { Legion = null }, GameClass.Chanter));
    }

    [Fact]
    public void TheSpotterNotesPlayersInRangeWithTheirLegion()
    {
        var session = new GameSession();
        session.Apply(new PlayerSpawnEvent(Now, 500, "Alpha", null, GameClass.Templar, CombatPower: 45_000));
        Assert.Equal(0, session.LegionsSeen.Pending); // no server yet: the legion tag names it
        session.Apply(new LegionTagEvent(Now + 10, 500, Server, "Knights"));
        session.Apply(new EquipmentChangedEvent(Now + 20, 500, 1_420, 1));
        session.Apply(new LegionTagEvent(Now + 30, 500, Server, "Knights")); // nothing new: not noted again

        var seen = session.LegionsSeen.Drain();
        Assert.Equal(2, seen.Count);
        Assert.Equal(new LegionSighting("Alpha", Server, "Knights", GameClass.Templar, null, 45_000, Now + 10, PowerSource.Spawn), seen[0]);
        Assert.Equal(1_420u, seen[1].ItemLevel);
        Assert.Equal(0, session.LegionsSeen.Pending);

        // ten minutes later the same player is noted again (last seen moves on)
        session.Apply(new LegionTagEvent(Now + 11 * 60_000, 500, Server, "Knights"));
        Assert.Equal(1, session.LegionsSeen.Pending);
    }

    [Fact]
    public void TheSpotterNotesUsTheGroupFinderAndTheParty()
    {
        var session = new GameSession();
        session.Apply(new SelfIdentityEvent(Now, 100, "Hero", Server, GameClass.Cleric, 45, 60_000));
        session.Apply(new LegionTagEvent(Now + 1, 100, Server, "Knights"));
        session.Apply(new SelfItemLevelEvent(Now + 2, 1_480, 1_480));
        var us = session.LegionsSeen.Drain().Last();
        Assert.Equal(new LegionSighting("Hero", Server, "Knights", GameClass.Cleric, 1_480, 60_000, Now + 2, PowerSource.Game), us);

        // a finder slot names a player without legion or server
        var slot = new FinderSlot(1, 77, "Bravo", GameClass.Ranger, 45, 1_390, 41_000, FinderRole.Damage, true, true, false);
        session.Apply(new FinderSlotEvent(Now + 3, slot, Joined: true));
        Assert.Equal(new LegionSighting("Bravo", null, null, GameClass.Ranger, 1_390, 41_000, Now + 3, PowerSource.Game),
            Assert.Single(session.LegionsSeen.Drain()));

        // a party member's status carries their legion
        session.Apply(new PartyRosterEvent(Now + 4,
            [new PartyMember(0, 1, "Hero", GameClass.Cleric), new PartyMember(1, 88, "Charlie", GameClass.Gladiator)], LeaderId: 1));
        session.Apply(new PartyMemberStatusEvent(Now + 5, 88, GameClass.Gladiator,
            new PartyMemberStatus(1010, 0, 1, new Vec3(1000, 1000, 100), 45, 1_450, 55_000, "Ravens")));
        Assert.Equal(new LegionSighting("Charlie", null, "Ravens", GameClass.Gladiator, 1_450, 55_000, Now + 5, PowerSource.Game),
            Assert.Single(session.LegionsSeen.Drain()));
    }

    [Fact]
    public void APlayerSpawnNamesTheServerAndLegionOfPlayersInALegion()
    {
        // id 300, mask, name "Alpha", class 13 (Ranger), filler, the legion block: u16, 00 00 00 00, server 1321,
        // "Knights", 01 00 02 00 00 00
        var events = TestPackets.Parse("4536 ac02 00000000 01 05 416c706861 0d000000 aabbccdd 0601 00000000 2905 07 4b6e6967687473 0100 02000000");
        Assert.IsType<PlayerSpawnEvent>(events[0]);
        Assert.Equal(new LegionTagEvent(1000, 300, 1321, "Knights"), events[1]);
        Assert.Equal(2, events.Count);

        // no legion: no block
        Assert.IsType<PlayerSpawnEvent>(Assert.Single(TestPackets.Parse("4536 ac02 00000000 01 05 416c706861 0d000000 aabbccdd 01000200")));
        // look-alikes: a server out of range, a text not followed by 01 00 / 02 00
        Assert.Single(TestPackets.Parse("4536 ac02 00000000 01 05 416c706861 0d000000 0601 00000000 2900 07 4b6e6967687473 0100"));
        Assert.Single(TestPackets.Parse("4536 ac02 00000000 01 05 416c706861 0d000000 0601 00000000 2905 07 4b6e6967687473 0300"));
        Assert.Single(TestPackets.Parse("4536 ac02 00000000 01 05 416c706861 0d000000 0601 00000000 2905 07 4b6e6967687473"));
    }

    [Fact]
    public void ASpawnWithALegionIsNotedAtOnce()
    {
        var session = new GameSession();
        foreach (var e in TestPackets.Parse("4536 ac02 00000000 01 05 416c706861 0d000000 aabbccdd 0601 00000000 2905 07 4b6e6967687473 0100 02000000"))
        {
            session.Apply(e);
        }

        var seen = Assert.Single(session.LegionsSeen.Drain());
        Assert.Equal(("Alpha", (ushort?)1321, "Knights", GameClass.Ranger), (seen.Name, seen.ServerId, seen.Legion, seen.Class));
    }

    [Fact]
    public void StreamerStandInsForLegionsStayTheSame()
    {
        var mask = new NameMask { On = true };
        Assert.Equal("Legion A", mask.LegionInList("Knights"));
        Assert.Equal("Legion B", mask.LegionInList("Ravens"));
        Assert.Equal("Legion A", mask.LegionInList("Knights"));
        Assert.Equal("Your legion", mask.LegionInList("Wolves", own: true));
        for (int i = 0; i < 30; i++)
        {
            mask.LegionInList($"L{i}");
        }

        Assert.Equal("Legion AE", mask.LegionInList("L28"));
        mask.On = false;
        Assert.Equal("Knights", mask.LegionInList("Knights", own: true));
    }
}
