using Avion2.Analysis;
using Avion2.Engine;
using Avion2.Protocol;

namespace Avion2.Tests;

/// <summary>The timetable after the AION2 patch of 2026-11-04 (Nov 3 PST / Nov 4 CET), from the official patch notes.</summary>
public class Patch1104Tests
{
    private static readonly TimeSpan Cet = TimeSpan.FromHours(1);

    private static ScheduledEvent Event(ServerRegion region, string key) => GameSchedule.Events(region).First(e => e.Key == key);

    private static DateTimeOffset Next(ServerRegion region, string key, DateTimeOffset now) =>
        GameSchedule.Next(Event(region, key), now, region);

    [Fact]
    public void ArtifactSiegeOnlyOnThursdayAndSaturday()
    {
        var siege = Event(ServerRegion.Europe, GameSchedule.ArtifactSiegeKey);
        Assert.Equal("Artifact Siege", siege.Name);
        Assert.Equal("Thu, Sat · 21:00", GameSchedule.Describe(siege));

        // From Monday morning the next one is Thursday 21:00 server time (14:00 CET), no longer Monday
        var monday = new DateTimeOffset(2026, 11, 9, 8, 0, 0, Cet);
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 14, 0, 0, Cet), Next(ServerRegion.Europe, GameSchedule.ArtifactSiegeKey, monday));

        // After Thursday's comes Saturday's, then Thursday again
        var thursday = new DateTimeOffset(2026, 11, 12, 15, 0, 0, Cet);
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 14, 0, 0, Cet), Next(ServerRegion.Europe, GameSchedule.ArtifactSiegeKey, thursday));
        var saturday = new DateTimeOffset(2026, 11, 14, 15, 0, 0, Cet);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 14, 0, 0, Cet), Next(ServerRegion.Europe, GameSchedule.ArtifactSiegeKey, saturday));
        Assert.Equal("Thu, Sat · 21:00", GameSchedule.Describe(Event(ServerRegion.NorthAmerica, GameSchedule.ArtifactSiegeKey)));
    }

    [Fact]
    public void ArtifactBossesOnlyOnThursdayAndSaturday()
    {
        Assert.Equal("Thu, Sat · 21:30", GameSchedule.Describe(Event(ServerRegion.Europe, "wb-executors")));
        Assert.Equal("Thu, Sat · 21:30", GameSchedule.Describe(Event(ServerRegion.NorthAmerica, "wb-executors")));
        var monday = new DateTimeOffset(2026, 11, 9, 8, 0, 0, Cet);
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 14, 30, 0, Cet), Next(ServerRegion.Europe, "wb-executors", monday));

        // Korea and Taiwan keep their own timetable (the notes are for the Global servers)
        Assert.Equal("Wed, Sat · 22:30", GameSchedule.Describe(Event(ServerRegion.Korea, "wb-executors")));
        Assert.DoesNotContain(GameSchedule.Events(ServerRegion.Korea), e => e.Key == GameSchedule.ArtifactSiegeKey);
    }

    [Fact]
    public void SpiritKingTwiceADay()
    {
        var argo = Event(ServerRegion.Europe, "wb-spirit-king");
        Assert.Equal("Argo, the Spirit King", argo.Name);
        Assert.Equal(ScheduleCategory.WorldBoss, argo.Category);
        Assert.Equal("10:30, 22:30", GameSchedule.Describe(argo));

        // Europe in winter (server time GMT+8): 22:30 server is 15:30 CET, 10:30 server is 03:30 CET
        var noon = new DateTimeOffset(2026, 11, 4, 12, 0, 0, Cet);
        var evening = Next(ServerRegion.Europe, "wb-spirit-king", noon);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 15, 30, 0, Cet), evening);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 3, 30, 0, Cet), Next(ServerRegion.Europe, "wb-spirit-king", evening));

        // North America uses GMT+9: 10:30 server is 01:30 UTC
        var utc = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 1, 30, 0, TimeSpan.Zero), Next(ServerRegion.NorthAmerica, "wb-spirit-king", utc));

        Assert.DoesNotContain(GameSchedule.Events(ServerRegion.Korea), e => e.Key == "wb-spirit-king");
        Assert.DoesNotContain(GameSchedule.Events(ServerRegion.Taiwan), e => e.Key == "wb-spirit-king");
    }

    [Fact]
    public void GartuaAndLagtaRespawnAtTheTimeTheGameSends()
    {
        // The board keeps no interval of its own: the game's table (01 91) carries the respawn time, so the 11 h
        // respawn shows as sent, and a newer table replaces a saved one that still had a 12 h respawn.
        var data = BundledGameData.Load("en");
        Assert.Equal("Eternal Gartua", data.FieldBossName(101021));
        Assert.Equal("High Commander Lagta", data.FieldBossName(101024));

        var killed = new DateTimeOffset(2026, 11, 4, 18, 0, 0, Cet);
        long killedMs = killed.ToUnixTimeMilliseconds();
        string path = Path.Combine(Path.GetTempPath(), $"avion2-fb-{Guid.NewGuid():N}.json");
        try
        {
            var old = new FieldBossBoard();
            old.Apply(new FieldBossTableEvent(killedMs, 1011, [new FieldBossEntry(101021, false, killed.AddHours(12).ToUnixTimeMilliseconds(), null, null, null)]));
            old.Save(path);

            var board = FieldBossBoard.Load(path);
            Assert.True(board.Apply(new FieldBossTableEvent(killedMs + 1000, 1011,
            [
                new FieldBossEntry(101021, false, killed.AddHours(11).ToUnixTimeMilliseconds(), null, null, null),
                new FieldBossEntry(101024, false, killed.AddHours(11).ToUnixTimeMilliseconds(), null, null, null),
            ])));

            var rows = board.Rows(data, killed.AddHours(1));
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal(FieldBossStatus.Respawning, r.Status));
            Assert.All(rows, r => Assert.Equal(killed.AddHours(11), r.At));

            // Once the 11 h have passed (and no newer table came), the boss is probably up, not still respawning
            Assert.All(board.Rows(data, killed.AddHours(11.5)), r => Assert.Equal(FieldBossStatus.Due, r.Status));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("Draupnir/explore", 14)]
    [InlineData("BakronIsland/explore", 21)]
    [InlineData("HornCave/explore", 28)]
    public void ExplorationGuaranteedRewardsMatchConquest(string key, int points)
    {
        // The patch matches these Exploration modes' guaranteed reward to Conquest: more points, no claim limit
        var cube = BundledGameData.Cubes.ByKey(key)!;
        Assert.Equal(points, cube.Ceiling!.After);
        Assert.Equal(0, cube.Ceiling.Claims);
        Assert.Equal(points, BundledGameData.Cubes.ByKey(key.Replace("explore", "conquest"))!.Ceiling!.After);
        Assert.Equal(1, BundledGameData.Cubes.ByKey("KraoCave/explore")!.Ceiling!.After); // the others stay
    }

    [Fact]
    public void LudraHasNormalAndHardCubes()
    {
        var normal = BundledGameData.Cubes.ByNpc(2301014)!; // Eternal Ludra
        var hard = BundledGameData.Cubes.ByNpc(2300290)!;   // Eternal Ludra (Hard)
        Assert.Equal("Abyssal Forge: Ludra · Normal", normal.Title);
        Assert.Equal("Abyssal Forge: Ludra · Hard", hard.Title);
        Assert.Equal("abyssal-forge-ludra", hard.Slug);
        Assert.Equal(2, normal.Ceiling!.After);
        Assert.Equal(2, hard.Ceiling!.After);
        Assert.Null(BundledGameData.Cubes.ByKey("ReforgedAbyss/conquest")); // the pre-patch key is gone

        // A map saved with the old key learns the new one at the next clear (or from the saved runs)
        var book = new CeilingBook();
        book.Learn(600082, "ReforgedAbyss/conquest");
        Assert.True(book.Learn(600082, normal.Key));
        Assert.Equal("ReforgedAbyss/normal", book.CubeOf(600082));
    }
}
