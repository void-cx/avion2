using Avion2.Analysis;
using Avion2.Protocol;

namespace Avion2.Tests;

/// <summary>Data kept per character: a switch must not mix one character's legion into another's.</summary>
public class CharacterDataTests
{
    private static SelfIdentityEvent Self(long ms, uint id, string name) => new(ms, id, name, 1321, GameClass.Chanter, 45);

    [Fact]
    public void LegionBelongsToTheCharacterThatGotIt()
    {
        var session = new GameSession();
        session.Apply(Self(1_000, 100, "Doxylamin"));
        session.Apply(new LegionDetailsEvent(1_100, "VOIDcx", "Lutti", []));
        session.Apply(new LegionRosterEvent(1_200, [new LegionMember("Lutti", 45, 1_000)]));
        Assert.Equal(("Doxylamin", "VOIDcx"), (session.Legion.Character, session.Legion.Name));

        // Switching to a character in another legion: nothing of the old one stays
        session.Apply(Self(2_000, 200, "Chiyu"));
        Assert.False(session.Legion.IsKnown);
        session.Apply(new LegionDetailsEvent(2_100, "Other", null, []));
        Assert.Equal(("Chiyu", "Other"), (session.Legion.Character, session.Legion.Name));
        Assert.Empty(session.Legion.Members);
    }

    [Fact]
    public void EachCharacterKeepsItsOwnLegion()
    {
        static LegionState Legion(string? character, string name, params string[] members) =>
            new() { Character = character, Name = name, Members = members.Select(m => new LegionMember(m, 45, null)).ToList() };

        var book = new LegionBook();
        Assert.True(book.Take(Legion("Doxylamin", "VOIDcx", "Lutti", "Doxylamin")));
        Assert.True(book.Take(Legion("Chiyu", "Other", "Chiyu")));
        Assert.False(book.Take(Legion(null, "Nobody's")));

        // A member page later in the session adds to what was known
        Assert.True(book.Take(new LegionState { Character = "Doxylamin", Members = [new LegionMember("Mira", 40, null)] }));
        Assert.Equal("VOIDcx", book.For("Doxylamin")!.Name);
        Assert.Equal(["Doxylamin", "Lutti", "Mira"], book.For("Doxylamin")!.Members.Select(m => m.Name).Order());
        Assert.Equal("Other", book.For("Chiyu")!.Name);

        var back = System.Text.Json.JsonSerializer.Deserialize<LegionBook>(System.Text.Json.JsonSerializer.Serialize(book))!;
        Assert.Equal(3, back.For("Doxylamin")!.Members.Count);
    }
}

/// <summary>Our combat power comes from <c>56 36</c>, also when it arrives just before the identity of a new character.</summary>
public class SelfCombatPowerTests
{
    [Fact]
    public void ParsesCurrentAndBest()
    {
        var power = Assert.IsType<SelfCombatPowerEvent>(Assert.Single(TestPackets.Parse("5636 f417000000000000 331a000000000000")));
        Assert.Equal((6132u, 6707u), (power.CombatPower, power.Best));
    }

    [Fact]
    public void FollowsOurCharacterAcrossZonesAndSwitches()
    {
        var session = new GameSession();
        session.Apply(new SelfCombatPowerEvent(900, 6_100, 6_100)); // at login, before we know who we are
        session.Apply(new SelfIdentityEvent(1_000, 100, "Doxylamin", 1321, GameClass.Chanter, 45));
        Assert.Equal(6_100u, session.Registry.Find(100)!.CombatPower);

        session.Apply(new SelfCombatPowerEvent(5_000, 6_200, 6_200));
        Assert.Equal(6_200u, session.Registry.Find(100)!.CombatPower);

        // A teleport much later: the same character keeps its power under its new entity id
        session.Apply(new SelfIdentityEvent(600_000, 101, "Doxylamin", 1321, GameClass.Chanter, 45));
        Assert.Equal(6_200u, session.Registry.Find(101)!.CombatPower);

        // Another character later on: the old power is not theirs
        session.Apply(new SelfIdentityEvent(900_000, 200, "Chiyu", 1321, GameClass.Cleric, 30));
        Assert.Null(session.Registry.Find(200)!.CombatPower);
    }
}
