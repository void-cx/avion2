using Avion2.Engine;

namespace Avion2.Tests;

public class BundledDataTests
{
    [Fact]
    public void ShipsNamesFieldBossesAndMaps()
    {
        var data = BundledGameData.Load("en");
        Assert.True(data.Skills.Count > 1000);
        Assert.True(data.Npcs.Count > 1000);
        Assert.Equal("Basic Attack", data.SkillName(10000));
        Assert.Equal(86, data.FieldBosses.Count);
        Assert.DoesNotContain("Field boss", data.FieldBossName(101005));
        Assert.Equal("Verteron", data.MapName(1010));
        Assert.Equal("Altgard", data.MapName(1110));
        Assert.Equal("Map 4242", data.MapName(4242));
    }

    [Fact]
    public void EveryLanguageLoadsAndMissingMapsFallBackToEnglish()
    {
        foreach (string language in BundledGameData.Languages)
        {
            var data = BundledGameData.Load(language);
            Assert.True(data.Skills.Count > 1000, language);
            Assert.True(data.Npcs.Count > 1000, language);
            Assert.NotEqual("Map 1010", data.MapName(1010));
        }

        Assert.Equal("Verteron", BundledGameData.Load("fr").MapName(1010));
        Assert.Equal("베르테론", BundledGameData.Load("ko").MapName(1010));
        Assert.Equal("Verteron", BundledGameData.Load("xx").MapName(1010));
    }

    [Fact]
    public void OverrideDirectoryReplacesSingleTables()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"avion2-data-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "maps.json"), """{ "1010": "Custom" }""");
            var (data, source) = MeterEngine.FindGameData(dir, "en");
            Assert.Equal("Custom", data.MapName(1010));
            Assert.Equal("Basic Attack", data.SkillName(10000)); // the rest stays built in
            Assert.Equal(dir, source.Directory);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
