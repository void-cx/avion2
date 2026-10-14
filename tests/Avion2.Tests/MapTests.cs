using Avion2.Analysis;
using Avion2.Engine;
using Avion2.Protocol;

namespace Avion2.Tests;

public class MapTests
{
    private static readonly GameData Data = BundledGameData.Load("en");

    // Live field bosses from a Verteron (map 1010) field boss table, 2026-10-13
    private static readonly FieldBossEntry[] Verteron =
    [
        new(101001, true, 0, -57517, -85173, 10150),
        new(101002, true, 0, -27290, -80782, 271),
        new(101003, true, 0, -94204, -43535, 12046),
        new(101004, true, 0, -18107, -10451, 21518),
        new(101008, true, 0, -57863, 82629, 13298),
        new(101010, true, 0, 18723, 95025, 34078),
        new(101013, true, 0, 124957, 11064, 29304),
        new(101015, true, 0, 117274, -96444, 10773),
        new(101020, true, 0, 159231, -130981, 8231),
        new(101005, false, 0, null, null, null),
    ];

    [Fact]
    public void AllMapsAreBuiltIn()
    {
        foreach (var key in BundledGameData.WorldMapKeys)
        {
            var map = BundledGameData.WorldMap(key);
            Assert.NotNull(map);
            using var image = BundledGameData.WorldMapImage(key);
            Assert.NotNull(image);
        }

        var verteron = BundledGameData.WorldMap("World_L_A")!;
        Assert.Equal(1010u, verteron.MapId);
        Assert.Equal(102, verteron.Markers.Count(m => m.Subtype == "hiddenCube"));
        Assert.All(verteron.Markers.Where(m => m.Subtype == "hiddenCube"), m => Assert.NotNull(m.Id));
    }

    [Fact]
    public void FieldBossTableCalibratesTheMap()
    {
        var verteron = BundledGameData.WorldMap("World_L_A")!;
        var fit = verteron.Calibrate(Verteron, Data.FieldBossName);

        Assert.NotNull(fit);
        Assert.Equal(verteron.Transform!.Scale, fit.Scale, 4);
        var (x, y) = fit.ToImage(0, 0);
        Assert.InRange(x, verteron.Transform.X0 - 3, verteron.Transform.X0 + 3);
        Assert.InRange(y, verteron.Transform.Y0 - 3, verteron.Transform.Y0 + 3);
    }

    [Fact]
    public void FitRejectsAWrongMatch()
    {
        var t = new MapTransform(0.01, 4000, 4000);
        var pairs = new List<((double, double), (double, double))>
        {
            ((0, 0), (4000, 4000)),
            ((10000, 0), (4100, 4000)),
            ((0, 10000), (4000, 4100)),
        };
        Assert.NotNull(MapTransform.Fit(pairs));
        pairs.Add(((20000, 20000), (100, 100)));
        Assert.Null(MapTransform.Fit(pairs));
        Assert.Equal(0, t.Error((0, 0), (4000, 4000)), 6);
    }

    [Fact]
    public void RegionLookup()
    {
        var verteron = BundledGameData.WorldMap("World_L_A")!;
        var marker = verteron.Markers.First(m => m.Name == "Confused Ursus");
        Assert.Equal("Altamia Canyon", verteron.RegionAt(marker.X, marker.Y));
    }

    [Fact]
    public void LivePositionsKeepLatestFixAndDropDeadMonsters()
    {
        var session = new GameSession(Data);
        session.Apply(new MapLoadEvent(0, 1010));
        session.Apply(new NpcSpawnEvent(1_000, 50, 0x0C, null, 1000, null, new Vec3(100, 200, 300)));
        session.Apply(new PlayerSpawnEvent(1_000, 60, "Someone", 1, GameClass.Cleric, null, new Vec3(500, 600, 700)));
        session.Apply(new PositionEvent(2_000, 50, new Vec3(150, 250, 300), null, PositionSource.Move));

        var live = session.Positions.Snapshot(session.Registry);
        Assert.Equal(1010u, session.Positions.MapId);
        Assert.Equal(150, live.Single(e => e.Id == 50).Position.X);
        Assert.Equal(LiveKind.Player, live.Single(e => e.Id == 60).Kind);

        session.Apply(new DeathEvent(3_000, 50, DeathKind.Combat));
        Assert.DoesNotContain(session.Positions.Snapshot(session.Registry), e => e.Id == 50);

        session.Apply(new ZoneChangeEvent(4_000));
        Assert.Empty(session.Positions.Snapshot(session.Registry));
    }

    [Fact]
    public void FacingFromCastsAndTravel()
    {
        var session = new GameSession(Data);
        session.Apply(new PlayerSpawnEvent(0, 60, "Someone", 1, GameClass.Cleric, null, new Vec3(5_000, 5_000, 300)));

        // Moving +Y → 90°
        session.Apply(new PositionEvent(1_000, 60, new Vec3(5_000, 6_000, 300), null, PositionSource.Move));
        Assert.Equal(90, session.Positions.Snapshot(session.Registry).Single().Facing!.Value, 1);

        // A cast says 10°: exact, newer than the last fix
        session.Apply(new SkillCastEvent(1_500, 60, 11000000, 11000001, null, null, 10f));
        Assert.Equal(10, session.Positions.Snapshot(session.Registry).Single().Facing!.Value, 1);

        // A move spanning that cast keeps the (exact) cast facing
        session.Apply(new PositionEvent(2_500, 60, new Vec3(4_000, 6_000, 300), null, PositionSource.Move));
        Assert.Equal(10, session.Positions.Snapshot(session.Registry).Single().Facing!.Value, 1);

        // The next move after it gives the direction of travel again (-Y → 270°)
        session.Apply(new PositionEvent(4_000, 60, new Vec3(4_000, 5_000, 300), null, PositionSource.Move));
        var e = session.Positions.Snapshot(session.Registry).Single();
        Assert.Equal(270, e.Facing!.Value, 1);
        Assert.Equal(0, e.FacingAgeMs);
    }

    [Theory]
    [InlineData("Forestof Exiles", "Forest of Exiles")]
    [InlineData("Gardenofthe Illusion God", "Garden of the Illusion God")]
    [InlineData("EllunRiverMarsh", "Ellun River Marsh")]
    [InlineData("Western Cantas Valley", "Western Cantas Valley")]
    [InlineData("Dranactus", "Dranactus")]
    [InlineData("Theobomos Laboratory", "Theobomos Laboratory")]
    public void RegionNamesReadNaturally(string raw, string shown) => Assert.Equal(shown, WorldMap.Readable(raw));
}
