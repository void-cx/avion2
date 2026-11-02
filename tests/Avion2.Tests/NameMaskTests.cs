using Avion2.Engine;

namespace Avion2.Tests;

public class NameMaskTests
{
    [Fact]
    public void OffShowsTheRealNames()
    {
        var mask = new NameMask { Self = "Hero" };

        Assert.Equal("Hero", mask.Name("Hero"));
        Assert.Equal("Someone", mask.Name("Someone"));
        Assert.Equal("Knights", mask.Legion("Knights"));
    }

    [Fact]
    public void OnShowsStandInsThatStayTheSame()
    {
        var mask = new NameMask { On = true, Self = "Hero" };

        Assert.Equal("You", mask.Name("Hero"));
        Assert.Equal("You", mask.Name("Alt", self: true));
        Assert.Equal("Player 2", mask.Name("Someone"));
        Assert.Equal("Player 3", mask.Name("Other"));
        Assert.Equal("Player 2", mask.Name("Someone"));
        Assert.Equal("Legion", mask.Legion("Knights"));
        Assert.Null(mask.Legion(null));
        Assert.Equal(string.Empty, mask.Name(string.Empty));
    }
}
