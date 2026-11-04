using System.Text;
using Avion2.Analysis;
using Avion2.Engine;
using Avion2.Protocol;

namespace Avion2.Tests;

/// <summary>
/// Our legion's whole member list (<c>05 8A</c>), on records built after the ones in our captures (names made up):
/// read, kept as the legion's members and taken by the Legions page.
/// </summary>
public class LegionRosterTests
{
    private const ushort Server = 1321;
    private const long Logout = 1_793_000_000_000;

    private static string Hex<T>(T value) where T : struct => value switch
    {
        byte b => b.ToString("x2"),
        uint u => Convert.ToHexString(BitConverter.GetBytes(u)),
        long l => Convert.ToHexString(BitConverter.GetBytes(l)),
        _ => throw new ArgumentException(nameof(value)),
    };

    private static string Text(string text) => Convert.ToHexString([(byte)Encoding.UTF8.GetByteCount(text), .. Encoding.UTF8.GetBytes(text)]);

    /// <summary>
    /// One member: flags, account, character (number, 16 bits of 0, server), class code, name, level, grade, the
    /// message when given, the byte the game sends now and then, logout, zone, item level, combat power.
    /// </summary>
    private static string Member(string name, uint code, uint level, byte grade, uint itemLevel, long power, string? message = null,
        byte? extra = null, uint character = 4_660) =>
        Hex((byte)(message is null ? 0 : 2)) + Hex(77_000L) + Hex(character) + "0000" + Convert.ToHexString(BitConverter.GetBytes(Server)) +
        Hex(code) + Text(name) + Hex(level) + Hex(grade) + (message is null ? "" : Text(message)) + (extra is { } x ? Hex(x) : "") +
        Hex(Logout) + Hex(0u) + Hex(itemLevel) + Hex(power);

    private static string Roster(params string[] members) => "058A" + "0000" + members.Length.ToString("x2") + string.Concat(members);

    private static LegionRosterEvent Parse(string hex)
    {
        var events = new List<GameEvent>();
        Assert.True(PacketParser.Parse(Convert.FromHexString(hex), 1_000, events.Add));
        return Assert.IsType<LegionRosterEvent>(Assert.Single(events));
    }

    [Fact]
    public void TheWholeListIsRead()
    {
        var roster = Parse(Roster(
            Member("Alpha", 34, 45, 0, 2710, 154_860, extra: 0x40),
            Member("Bravo", 14, 45, 2, 2165, 113_568, message: "Hello there"),
            Member("Charlie", 30, 29, 1, 388, 20_348, message: "Hi", extra: 0x01, character: 9_999)));

        Assert.True(roster.Complete);
        Assert.Equal(
            [
                new LegionMember("Alpha", 45, Logout, GameClass.Chanter, 2710, 154_860, 0, Server),
                new LegionMember("Bravo", 45, Logout, GameClass.Ranger, 2165, 113_568, 2, Server),
                new LegionMember("Charlie", 29, Logout, GameClass.Cleric, 388, 20_348, 1, Server),
            ],
            roster.Members);
    }

    [Fact]
    public void ARecordThatDoesNotWalkIsSearchedForNames()
    {
        // one byte too many at the end: not walked, the names still found
        var roster = Parse(Roster(Member("Alpha", 34, 45, 0, 2710, 154_860)) + "00");
        Assert.False(roster.Complete);
        Assert.Equal("Alpha", Assert.Single(roster.Members).Name);
    }

    [Fact]
    public void TheListReplacesTheMembersAndReachesTheLegionsPage()
    {
        var session = new GameSession();
        session.Apply(new SelfIdentityEvent(0, 1000, "Alpha", Server, GameClass.Chanter, 45));
        session.Apply(new LegionDetailsEvent(100, "Knights", "Alpha", []));
        session.Apply(new LegionRosterEvent(150, [new LegionMember("Gone", 45, 1)]));
        session.Apply(new LegionRosterEvent(200,
        [
            new LegionMember("Alpha", 45, 1, GameClass.Chanter, 2710, 154_860, 0, Server),
            new LegionMember("Bravo", 45, 1, GameClass.Ranger, 2165, 113_568, 2, Server),
        ], Complete: true));

        Assert.Equal(["Alpha", "Bravo"], session.Legion.Members.Select(m => m.Name).Order());
        Assert.Equal(200, session.Legion.RosterMs);

        var roster = Assert.Single(session.LegionsSeen.DrainRosters());
        Assert.Equal((Server, "Knights", 2), (roster.ServerId, roster.Legion, roster.Members.Count));
        Assert.All(roster.Members, m => Assert.Equal(PowerSource.Game, m.Power));
    }

    [Fact]
    public void TheBookKeepsOnlyTheMembersOnTheList()
    {
        var book = new LegionsSeenBook();
        book.Take(new LegionSighting("Gone", Server, "Knights", GameClass.Templar, 1500, 70_000, 1_000));
        book.Take(new LegionSighting("Later", Server, "Knights", GameClass.Cleric, 1600, 80_000, 9_000));
        book.TakeListing(new LegionListing(Server, 871, "Knights", "Alpha", 3, 40, 128, "", 0), 1_500);

        Assert.True(book.TakeRoster(new LegionRosterSighting(Server, "Knights",
        [
            new LegionSighting("Alpha", null, null, GameClass.Chanter, 2710, 154_860, 0),
            new LegionSighting("Bravo", null, null, GameClass.Ranger, 2165, 113_568, 0),
        ], 5_000)));

        var legion = book.Find(Server, "Knights")!;
        // "Gone" was not on the list; "Later" was seen after it, so stays
        Assert.Equal(["Alpha", "Bravo", "Later"], legion.Members.Select(m => m.Name).Order());
        Assert.Equal(PowerSource.Game, legion.Members.Single(m => m.Name == "Alpha").Power);

        // the list is newer than the legion list's 40: the members count
        var standing = Assert.Single(LegionRanking.Rank(book.On(Server), LegionOrder.Members, 10_000));
        Assert.Equal((3, (int?)null), (standing.Members, standing.Listed));

        // an older list changes nothing
        Assert.False(book.TakeRoster(new LegionRosterSighting(Server, "Knights",
            [new LegionSighting("Old", null, null, GameClass.Gladiator, 1, 1, 0)], 4_000)));
    }

    [Fact]
    public void TheDetailsCarryTheMemberCountAndMaximum()
    {
        var events = new List<GameEvent>();
        Assert.True(PacketParser.Parse(Convert.FromHexString("038A" + "0000" + Text("Knights") + Text("Alpha") + "80" + "8001" + "00" + new string('0', 40)),
            1_000, events.Add));
        var details = Assert.IsType<LegionDetailsEvent>(Assert.Single(events));
        Assert.Equal(("Knights", "Alpha", 128, 128), (details.Name, details.Leader, details.Members, details.MaxMembers));
    }

    [Fact]
    public void TheExportSumsUpEachLegion()
    {
        var knights = new LegionState
        {
            Character = "Alpha", Name = "Knights", MaxMembers = 128, RosterMs = 5_000,
            Members =
            [
                new LegionMember("Alpha", 45, 1, GameClass.Chanter, 2000, 150_000, 0, Server),
                new LegionMember("Bravo", 45, 1, GameClass.Templar, 1000, 50_000, 1, Server),
                new LegionMember("Charlie", 30, 1, GameClass.Elementalist, 1500, 100_000, 2, Server),
            ],
        };
        var unlisted = new LegionState { Character = "Delta", Name = "Wolves", Members = [new LegionMember("Delta", 45, 1)] };
        var older = new LegionState { Character = "Echo", Name = "Knights", RosterMs = 1_000, Members = [new LegionMember("Echo", 45, 1)] };

        var (json, count) = LegionExport.Build([knights, unlisted, older], DateTimeOffset.UnixEpoch, _ => "Test Server");
        Assert.Equal(1, count);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var legion = Assert.Single(doc.RootElement.GetProperty("legions").EnumerateArray());
        Assert.Equal("Knights", legion.GetProperty("name").GetString());
        Assert.Equal("Test Server", legion.GetProperty("server").GetString());
        Assert.Equal("Alpha", legion.GetProperty("leader").GetString());
        Assert.Equal(["Bravo"], legion.GetProperty("officers").EnumerateArray().Select(o => o.GetString()));
        var size = legion.GetProperty("size");
        Assert.Equal((3, 128, 125, 2), (size.GetProperty("members").GetInt32(), size.GetProperty("max").GetInt32(),
            size.GetProperty("free").GetInt32(), size.GetProperty("level45").GetInt32()));
        var itemLevel = legion.GetProperty("itemLevel");
        Assert.Equal((1000, 2000, 1500, 1500), (itemLevel.GetProperty("min").GetInt32(), itemLevel.GetProperty("max").GetInt32(),
            itemLevel.GetProperty("average").GetInt32(), itemLevel.GetProperty("median").GetInt32()));
        Assert.Equal(300_000, legion.GetProperty("combatPower").GetProperty("total").GetInt64());
        Assert.Equal(["Tank", "Support", "DPS"], legion.GetProperty("roles").EnumerateObject().Select(r => r.Name));
        Assert.Equal("DPS", legion.GetProperty("classes").GetProperty("Spiritmaster").GetProperty("role").GetString());
        Assert.Equal(["Alpha", "Bravo", "Charlie"], legion.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("name").GetString()));
    }

    [Fact]
    public void ASavedLegionTakesTheNewListAsIs()
    {
        var book = new LegionBook();
        book.Take(new LegionState { Character = "Alpha", Name = "Knights", Members = [new LegionMember("Gone", 45, 1)] });
        book.Take(new LegionState { Character = "Alpha", Name = "Knights", RosterMs = 5_000, Members = [new LegionMember("Alpha", 45, 1)] });
        Assert.Equal(["Alpha"], book.For("Alpha")!.Members.Select(m => m.Name));

        // a session without the list keeps the list's members
        book.Take(new LegionState { Character = "Alpha", Name = "Knights" });
        Assert.Equal(["Alpha"], book.For("Alpha")!.Members.Select(m => m.Name));
        Assert.Equal(5_000, book.For("Alpha")!.RosterMs);
    }
}
