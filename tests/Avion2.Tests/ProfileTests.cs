using Avion2.Engine;
using Avion2.Protocol;
using static Avion2.Tests.TestPackets;

namespace Avion2.Tests;

/// <summary>Another player's profile opened in the game (<c>50 36</c>; hand-built records, names made up).</summary>
public class ProfileTests
{
    // result, 07, "Bravo", Chanter (34), faction 1, 02, level 45, 0, item level 872, server 1321 twice, 39, title
    private const string Head = "5036 0000 07 05427261766f 22000000 01 02 2d000000 00000000 68030000 2905 2905 39 d4ffa700";

    // legion id, legion name, rank, then some gear, the combat power after 04 06 0E 10 14, and the trailer
    private const string Tail = "0100 02 00000000 1ce38e0c 0000 0406 0e10 14 4c98000000000000 02 01000000 06000000 03000000 01000000";

    [Fact]
    public void AProfileOpenedInTheGameIsRead()
    {
        var withLegion = Assert.IsType<CharacterProfileEvent>(Assert.Single(Parse(Head + " a200000000002905 074b6e6967687473 " + Tail)));
        Assert.Equal(("Bravo", (ushort)1321, GameClass.Chanter, 45u, 872u), (withLegion.Name, withLegion.ServerId, withLegion.Class, withLegion.Level, withLegion.ItemLevel));
        Assert.Equal(("Knights", 38_988u), (withLegion.Legion, withLegion.CombatPower!.Value));

        var alone = Assert.IsType<CharacterProfileEvent>(Assert.Single(Parse(Head + " 0000000000000000 00 " + Tail)));
        Assert.Null(alone.Legion);

        var noPower = Assert.IsType<CharacterProfileEvent>(Assert.Single(Parse(Head + " 0000000000000000 00 0100 02")));
        Assert.Null(noPower.CombatPower);
    }

    [Fact]
    public void BrokenProfilesAreRejected()
    {
        Assert.Empty(Parse(Head.Replace("5036 0000", "5036 0100") + " 0000000000000000 00 " + Tail)); // a refusal
        Assert.Empty(Parse(Head.Replace("22000000", "63000000") + " 0000000000000000 00 " + Tail)); // no such class
        Assert.Empty(Parse(Head.Replace("2905 2905", "0900 0900") + " 0000000000000000 00 " + Tail)); // no such server
        Assert.Empty(Parse(Head)); // cut short
    }

    [Fact]
    public void AnonymizedSessionsDropTheProfile()
    {
        var profile = new CharacterProfileEvent(0, "Bravo", 1321, GameClass.Chanter, 45, 872, null, null);
        Assert.Null(new NameAnonymizer().Rewrite(profile));
    }
}
