using Avion2.Engine;

namespace Avion2.Tests;

public class BossPortraitTests
{
    private static readonly byte[] PngMagic = [0x89, (byte)'P', (byte)'N', (byte)'G'];

    private static BossPortraits Bundled(string? overrideDirectory = null) =>
        BossPortraits.Parse(BundledGameData.Read("bosses/bosses.json"), BundledGameData.BossPortrait, overrideDirectory);

    private static byte[] Head(Stream? stream)
    {
        Assert.NotNull(stream);
        using (stream)
        {
            var head = new byte[4];
            Assert.Equal(4, stream.Read(head, 0, 4));
            return head;
        }
    }

    [Fact]
    public void FieldBossesShowTheirModelsPortrait()
    {
        var portraits = Bundled();
        var data = BundledGameData.Load("en");

        // Neikel of the East (Verteron, spawn code 101001)
        Assert.Equal("Neikel of the East", data.Npcs[2100003].Name);
        Assert.Equal("bosses/Keneir_01_V03.png", portraits.Locate(2100003));
        Assert.Equal(portraits.Locate(2100003), portraits.LocateSpawn(101001, data));
        Assert.Equal(portraits.Locate(2100003), portraits.LocateTimer("fieldboss:101001", data));
        Assert.Equal(PngMagic, Head(portraits.Open(portraits.Locate(2100003)!)));

        // The same model, the same picture: Watcher Kaira and Executor Kaira
        Assert.Equal(portraits.PortraitOf(2600089), portraits.PortraitOf(2600098));
        Assert.Equal(portraits.Locate(2100003), portraits.LocateName("Neikel of the East"));
    }

    [Fact]
    public void BossesWithoutASureMatchHaveNoPicture()
    {
        var portraits = Bundled();
        var data = BundledGameData.Load("en");

        Assert.Null(portraits.Locate(2100079));                         // Bodyguard Teegant: no portrait on the site
        Assert.Null(portraits.Locate(2600068));                         // Argo, the Spirit King
        Assert.Null(portraits.Locate(42));                              // not a boss
        Assert.Null(portraits.LocateSpawn(999999, data));               // unknown spawn code
        Assert.Null(portraits.LocateTimer("fieldboss:not-a-code", data));
        Assert.Null(portraits.LocateTimer("rift", data));               // events have no portrait
        Assert.Null(portraits.LocateTimer(Avion2.Analysis.GameSchedule.InvasionKey, data));
        Assert.Null(portraits.LocateName("Eternal Gartua"));            // the map knows "Immortal Gartua"; no guessing
        Assert.Empty(portraits.LocateWorldBoss("rift"));
    }

    [Fact]
    public void EveryWorldBossRowAndEveryPortraitIsBundled()
    {
        var portraits = Bundled();
        var data = BundledGameData.Load("en");
        var rows = Avion2.Analysis.GameSchedule.Events(Avion2.Analysis.ServerRegion.Korea)
            .Where(e => e.Category == Avion2.Analysis.ScheduleCategory.WorldBoss).ToList();
        Assert.Equal(5, rows.Count);
        foreach (var row in rows)
        {
            Assert.NotNull(portraits.LocateTimer(row.Key, data));
        }

        Assert.Equal(3, portraits.LocateWorldBoss("wb-executors").Count);
        Assert.Equal(3, portraits.LocateWorldBoss("wb-middle").Count);
        Assert.Single(portraits.LocateWorldBoss("wb-watcher-kaira"));

        // Every NPC with a portrait in the table opens a PNG built into the app
        Assert.True(portraits.NpcCount >= 50);
        foreach (uint npc in data.FieldBosses.Values.Distinct())
        {
            if (portraits.Locate(npc) is { } location)
            {
                Assert.Equal(PngMagic, Head(portraits.Open(location)));
            }
        }
    }

    [Fact]
    public void TheOverrideFolderReplacesAndAddsPictures()
    {
        string dir = Path.Combine(Path.GetTempPath(), "avion2-boss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] png = [.. PngMagic, 1, 2, 3];
            File.WriteAllBytes(Path.Combine(dir, "2100079.png"), png);    // a boss without a built-in picture
            File.WriteAllBytes(Path.Combine(dir, "2100003.png"), png);    // replaces a built-in one
            File.WriteAllBytes(Path.Combine(dir, "wb-executors.png"), png);
            File.WriteAllBytes(Path.Combine(dir, "rift.png"), png);       // not a boss row: ignored
            var portraits = Bundled(dir);
            var data = BundledGameData.Load("en");

            Assert.Equal(Path.Combine(dir, "2100079.png"), portraits.Locate(2100079));
            Assert.Equal(Path.Combine(dir, "2100003.png"), portraits.LocateTimer("fieldboss:101001", data));
            Assert.Equal(png, ReadAll(portraits.Open(portraits.Locate(2100079)!)));
            Assert.Equal([Path.Combine(dir, "wb-executors.png")], portraits.LocateWorldBoss("wb-executors"));
            Assert.Equal(Path.Combine(dir, "wb-executors.png"), portraits.LocateTimer("wb-executors", data));
            Assert.Null(portraits.LocateTimer("rift", data));
            Assert.Equal("bosses/Kashapa_01.png", portraits.LocateWorldBoss("wb-middle")[0]);   // no override: built in

            // A file that went away cannot be opened: no picture rather than an error
            Assert.Null(portraits.Open(Path.Combine(dir, "missing.png")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AMissingOrBrokenTableKnowsNoPortraits()
    {
        foreach (string? json in new[] { null, "{", "[]", """{"npcs":{"x":"A","5":""},"events":{"wb-a":["B"]}}""" })
        {
            var portraits = BossPortraits.Parse(json, _ => null, null);
            Assert.Null(portraits.Locate(5));
            Assert.Null(portraits.LocateName("Anything"));
        }

        var table = BossPortraits.Parse("""{"npcs":{"7":"Seven"},"events":{"wb-a":["A","B"]},"names":{"Boss Seven":"Seven"}}""", _ => null, null);
        Assert.Equal("bosses/Seven.png", table.Locate(7));
        Assert.Equal("bosses/Seven.png", table.LocateName("boss seven"));
        Assert.Equal(["bosses/A.png", "bosses/B.png"], table.LocateWorldBoss("wb-a"));
        Assert.Null(table.Open("bosses/Seven.png")); // listed, but not bundled
    }

    private static byte[] ReadAll(Stream? stream)
    {
        Assert.NotNull(stream);
        using (stream)
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
    }
}
