using System.Text;
using Avion2.Analysis;
using Avion2.Protocol;

namespace Avion2.Tests;

/// <summary>
/// Our legion changing (created, left, disbanded) and the game's legion lists, on records built after the ones in
/// our captures (names made up).
/// </summary>
public class LegionChangesTests
{
    private const uint Me = 1000;
    private const string MeVarint = "e807";
    private const ushort Server = 1321;

    private static List<GameEvent> Parse(string hex)
    {
        var events = new List<GameEvent>();
        Assert.True(PacketParser.Parse(Convert.FromHexString(hex), 1_000, events.Add));
        return events;
    }

    private static string Text(string text) => Convert.ToHexString([(byte)Encoding.UTF8.GetByteCount(text), .. Encoding.UTF8.GetBytes(text)]);

    /// <summary>A legion id: its number, 16 bits of 0, the server.</summary>
    private static string Id(uint legion, ushort server = Server) =>
        Convert.ToHexString(BitConverter.GetBytes(legion)) + "0000" + Convert.ToHexString(BitConverter.GetBytes(server));

    private static string Zeros(int count) => new('0', count * 2);

    /// <summary>One legion of a list: …, level 1, no experience, members, max 128, emblem 0, introduction, join rule, then the tail.</summary>
    private static string Listed(uint id, string name, string leader, int members, string intro, byte rule, int tail) =>
        Id(id) + Text(name) + Text(leader) + "01" + Zeros(8) + members.ToString("x2") + "8001" + "00" + Text(intro) + rule.ToString("x2") + Zeros(tail);

    [Fact]
    public void CreateLeaveAndDisbandAnswersDecode()
    {
        var created = Assert.IsType<LegionCreatedEvent>(Assert.Single(Parse(
            "018A" + "0000" + Id(871) + Text("Ravens") + "01" + Zeros(8) + "02" + "00" + "01" + "00" + "03" + "00fe1f0000" + "0198060000" + "0200020000")));
        Assert.Equal((0, 871u, Server, "Ravens"), (created.Result, created.LegionId, created.ServerId, created.Name));

        var refused = Assert.IsType<LegionCreatedEvent>(Assert.Single(Parse("018A" + "1608" + Zeros(24))));
        Assert.Equal((2070, ""), (refused.Result, refused.Name));

        Assert.Equal(0, Assert.IsType<LegionLeftEvent>(Assert.Single(Parse("0B8A" + Zeros(10)))).Result);
        Assert.Equal(0, Assert.IsType<LegionDisbandedEvent>(Assert.Single(Parse("358A" + "0000"))).Result);

        var left = Assert.IsType<LegionMemberLeftEvent>(Assert.Single(Parse("0C8A" + Text("TestDaeva") + "02" + Zeros(8))));
        Assert.Equal(("TestDaeva", 2), (left.Name, left.Reason));
        Assert.Equal("TestDaeva", Assert.IsType<LegionMemberOnlineEvent>(Assert.Single(Parse("398A" + Text("TestDaeva")))).Name);
    }

    [Fact]
    public void ATagWithoutLegionSaysInNoLegion()
    {
        var none = Assert.IsType<LegionTagEvent>(Assert.Single(Parse("338A" + MeVarint + Zeros(9) + "0002" + Zeros(18))));
        Assert.Equal((Me, (ushort)0, ""), (none.EntityId, none.ServerId, none.Legion));

        var tagged = Assert.IsType<LegionTagEvent>(Assert.Single(Parse("338A" + MeVarint + Id(794) + Text("Knights") + "01000200" + Zeros(14))));
        Assert.Equal((Server, "Knights"), (tagged.ServerId, tagged.Legion));
    }

    [Fact]
    public void LegionListsDecodeWhateverTheEntryTail()
    {
        // The entries' last bytes are 10 or 11 long in our records (rule included): the next legion is found by its id and name
        var list = Assert.IsType<LegionListEvent>(Assert.Single(Parse("078A" + "0000" + "03" +
            Listed(13, "Knights", "Alpha", 127, "Welcome!\r\nPvE and PvP", 0, 10) +
            Listed(12, "Ravens", "Bravo", 122, "", 2, 9) +
            Listed(781, "Wolves", "", 2, "Шлем", 3, 9))));
        Assert.False(list.Search);
        Assert.Equal(["Knights", "Ravens", "Wolves"], list.Legions.Select(l => l.Name));
        var knights = list.Legions[0];
        Assert.Equal((Server, 13u, "Alpha", (byte)1, 127, 128, "Welcome!\r\nPvE and PvP", (byte)0),
            (knights.ServerId, knights.LegionId, knights.Leader, knights.Level, knights.Members, knights.MaxMembers, knights.Intro, knights.JoinRule));
        Assert.Equal(("", "Шлем", (byte)3), (list.Legions[2].Leader, list.Legions[2].Intro, list.Legions[2].JoinRule));

        var search = Assert.IsType<LegionListEvent>(Assert.Single(Parse("098A" + "0000" + "01" + Listed(262, "Knights", "Alpha", 128, "", 0, 9))));
        Assert.True(search.Search);
        Assert.Equal(128, search.Legions.Single().Members);
    }

    [Fact]
    public void OurLegionFollowsLeavingCreatingAndDisbanding()
    {
        var session = new GameSession();
        session.Apply(new SelfIdentityEvent(0, Me, "TestDaeva", Server, GameClass.Chanter, 45));
        session.Apply(new LegionTagEvent(100, Me, Server, "Knights"));
        session.Apply(new LegionDetailsEvent(200, "Knights", "Alpha", ["Hello"]));
        session.Apply(new LegionRosterEvent(300, [new LegionMember("Alpha", 45, 1), new LegionMember("TestDaeva", 45, 2)]));
        var legion = session.Legion;
        Assert.Equal(("Knights", "Alpha", 2), (legion.Name, legion.Leader, legion.Members.Count));

        // Left: no legion, nothing of the old one kept; the empty tag after it changes nothing
        session.Apply(new LegionLeftEvent(1_000, 0));
        Assert.Equal((true, (string?)null, 0, (string?)null), (legion.NoLegion, legion.Name, legion.Members.Count, legion.Leader));
        int revision = legion.Revision;
        session.Apply(new LegionTagEvent(1_001, Me, 0, ""));
        Assert.Equal(revision, legion.Revision);

        // A failed attempt, then the legion we created: we lead it and are its only member
        session.Apply(new LegionCreatedEvent(2_000, 2070, 0, 0, ""));
        Assert.True(legion.NoLegion);
        session.Apply(new LegionCreatedEvent(2_100, 0, 871, Server, "Ravens"));
        Assert.Equal(("Ravens", "TestDaeva", false), (legion.Name, legion.Leader, legion.NoLegion));
        Assert.Equal(["TestDaeva"], legion.Members.Select(m => m.Name));

        // Disbanded (the answer, then the notice naming us)
        session.Apply(new LegionDisbandedEvent(3_000, 0));
        session.Apply(new LegionMemberLeftEvent(3_001, "TestDaeva", 2));
        Assert.Equal((true, (string?)null), (legion.NoLegion, legion.Name));

        // A tag alone puts us in a legion too (joined while AVION2 was not looking)
        session.Apply(new LegionTagEvent(4_000, Me, Server, "Wolves"));
        Assert.Equal(("Wolves", false), (legion.Name, legion.NoLegion));
    }

    [Fact]
    public void MembersLeaveAndComeOnline()
    {
        var session = new GameSession();
        session.Apply(new SelfIdentityEvent(0, Me, "TestDaeva", Server, GameClass.Chanter, 45));
        session.Apply(new LegionDetailsEvent(100, "Knights", "Alpha", []));
        session.Apply(new LegionRosterEvent(200, [new LegionMember("Alpha", 45, 1), new LegionMember("Bravo", 40, 1)]));

        session.Apply(new LegionMemberOnlineEvent(5_000, "Bravo"));
        Assert.Equal(5_000, session.Legion.Members.Single(m => m.Name == "Bravo").LastOnlineMs);
        session.Apply(new LegionMemberLeftEvent(6_000, "Bravo", 1));
        Assert.Equal(["Alpha"], session.Legion.Members.Select(m => m.Name));
        Assert.Equal("Knights", session.Legion.Name);
    }

    [Fact]
    public void TheSavedLegionDoesNotComeBackAfterLeaving()
    {
        var book = new LegionBook();
        book.Take(new LegionState { Character = "TestDaeva", Name = "Knights", Leader = "Alpha", Texts = ["Hello"] });
        book.Take(new LegionState { Character = "TestDaeva", NoLegion = true });
        Assert.False(book.For("TestDaeva")!.IsKnown);

        book.Take(new LegionState { Character = "TestDaeva", Name = "Ravens" });
        Assert.Equal(("Ravens", (string?)null), (book.For("TestDaeva")!.Name, book.For("TestDaeva")!.Leader));
        Assert.Empty(book.For("TestDaeva")!.Texts); // not the old legion's
    }

    [Fact]
    public void LegionListsGiveTheLegionsPageRealSizes()
    {
        const long now = 1_790_000_000_000;
        var book = new LegionsSeenBook();
        book.Take(new LegionSighting("Alpha", Server, "Knights", GameClass.Chanter, null, 50_000, now));
        Assert.True(book.TakeListing(new LegionListing(Server, 13, "Knights", "Alpha", 1, 127, 128, "", 0), now));
        Assert.True(book.TakeListing(new LegionListing(Server, 12, "Ravens", "Bravo", 1, 40, 128, "", 2), now));

        var ranked = LegionRanking.Rank(book.On(Server), LegionOrder.Members, now);
        Assert.Equal(["Knights", "Ravens"], ranked.Select(r => r.Legion.Name));
        Assert.Equal((127, 1, 127), (ranked[0].Members, ranked[0].Seen, ranked[0].Listed));
        Assert.Equal((40, 0), (ranked[1].Members, ranked[1].Seen)); // listed, no member seen
        Assert.Equal("Bravo", book.Find(Server, "Ravens")!.Listed!.Leader);

        // A listing goes stale like a member (Alpha, the one member seen, goes too); legions with nothing left go with them
        Assert.Equal(1, book.Prune(now + (long)LegionsSeenBook.StaleAfter.TotalMilliseconds + 1));
        Assert.Empty(book.On(Server));
    }

    [Fact]
    public void APlayerTaggedWithoutLegionLeavesTheirs()
    {
        const long now = 1_790_000_000_000;
        var book = new LegionsSeenBook();
        book.Take(new LegionSighting("Alpha", Server, "Knights", GameClass.Chanter, null, null, now));
        book.Take(new LegionSighting("Bravo", Server, "Knights", GameClass.Chanter, null, null, now));

        Assert.False(book.Take(new LegionSighting("Alpha", Server, "", GameClass.Chanter, null, null, now - 1))); // older than the sighting
        Assert.True(book.Take(new LegionSighting("Alpha", Server, "", GameClass.Chanter, null, null, now + 1)));
        Assert.Equal(["Bravo"], book.Find(Server, "Knights")!.Members.Select(m => m.Name));
        Assert.False(book.Take(new LegionSighting("Charlie", Server, "", GameClass.Chanter, null, null, now))); // never placed
    }
}
