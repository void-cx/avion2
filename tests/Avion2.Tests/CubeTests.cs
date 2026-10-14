using Avion2.Analysis;
using Avion2.Protocol;

namespace Avion2.Tests;

public class CubeTests
{
    // A hidden cube in Verteron coming into range, and us opening it with a key (2026-10-14)
    private const string CubeSpawn = "3436" + "e6c5020400a74f120000bd2bc7c0d3024800cc7b460000000000000000010101a01f243aa101000000000000000000002c4d4502070000";
    private const string Opened = "3A36" + "e6c502e7250100000023c59a25";
    private const uint Self = 4839;

    private static GameEvent One(string hex)
    {
        var events = new List<GameEvent>();
        Assert.True(PacketParser.Parse(Convert.FromHexString(hex), 1_000, events.Add));
        return Assert.Single(events);
    }

    [Fact]
    public void WorldObjectAndInteractionDecode()
    {
        var cube = Assert.IsType<WorldObjectEvent>(One(CubeSpawn));
        Assert.Equal(41702u, cube.EntityId);
        Assert.Equal(1_200_039u, cube.Code);
        Assert.Equal(-43965, cube.Position.X, 1);
        Assert.Equal(133967, cube.Position.Y, 1);
        Assert.Equal(16115, cube.Position.Z, 1);

        var done = Assert.IsType<InteractionDoneEvent>(One(Opened));
        Assert.Equal(41702u, done.ObjectId);
        Assert.Equal(Self, done.ActorId);
        Assert.Equal(CubeTracker.KeyItem, done.ItemId);

        // An interaction that used no item
        Assert.Null(Assert.IsType<InteractionDoneEvent>(One("3A36" + "e4d501de1e0000000000000000")).ItemId);
    }

    [Fact]
    public void CubesAreSeenOpenedOrTaken()
    {
        var tracker = new CubeTracker();
        var seen = new List<CubeSighting>();
        tracker.Changed += seen.Add;

        tracker.Apply(One(CubeSpawn), Self, 1010);
        Assert.Equal(CubeState.Available, Assert.Single(seen).State);

        tracker.Apply(One(Opened), Self, 1010);
        Assert.Equal(CubeState.Opened, seen[^1].State);

        // Another cube, opened by someone else
        tracker.Apply(new WorldObjectEvent(5_000, 50_000, 4, 1_200_039, new Vec3(-35281, 10251, 20993)), Self, 1010);
        tracker.Apply(new InteractionDoneEvent(6_000, 50_000, 7648, CubeTracker.KeyItem), Self, 1010, id => id == 7648 ? "OtherPlayer" : "?");
        Assert.Equal(CubeState.Taken, seen[^1].State);
        Assert.Equal("OtherPlayer", seen[^1].TakenBy);
    }

    [Fact]
    public void OpeningTeachesAnUnknownCubeCode()
    {
        var tracker = new CubeTracker();
        tracker.CubeCodes.Clear();
        var seen = new List<CubeSighting>();
        tracker.Changed += seen.Add;

        tracker.Apply(One(CubeSpawn), Self, 1010);
        Assert.Empty(seen); // not known as a cube yet

        tracker.Apply(One(Opened), Self, 1010);
        Assert.Single(seen);
        Assert.Contains(1_200_039u, tracker.CubeCodes);
    }

    [Fact]
    public void LogKeepsOneEntryPerSpotAndItsLatestState()
    {
        var log = new CubeLog();
        var at = new Vec3(-43965, 133967, 16115);
        string id = CubeSighting.SpotId(1010, at);
        log.Merge(new CubeSighting(id, 1010, at.X, at.Y, at.Z, 1_200_039, 1_000));
        log.Merge(new CubeSighting(id, 1010, at.X + 50, at.Y, at.Z, 1_200_039, 1_000, OpenedMs: 2_000));
        Assert.Single(log.Cubes);
        Assert.Equal(CubeState.Opened, log.Find(id)!.State);
        Assert.Equal(1, log.OpenedCount(1010));

        // Seen again later: it respawned and is there again
        log.Merge(new CubeSighting(id, 1010, at.X, at.Y, at.Z, 1_200_039, 9_000));
        Assert.Equal(CubeState.Available, log.Find(id)!.State);

        // Taken by someone after that
        log.Merge(new CubeSighting(id, 1010, at.X, at.Y, at.Z, 1_200_039, 9_000, TakenMs: 9_500, TakenBy: "AnotherPlayer"));
        Assert.Equal(CubeState.Taken, log.Find(id)!.State);
        Assert.Equal("AnotherPlayer", log.Find(id)!.TakenBy);

        // Another map, same coordinates: another spot
        log.Merge(new CubeSighting(CubeSighting.SpotId(1110, at), 1110, at.X, at.Y, at.Z, 1_200_039, 1_000));
        Assert.Equal(2, log.Cubes.Count);
    }

    [Fact]
    public void OnlyRecentOrRepeatedSpotsAreExact()
    {
        var at = new Vec3(-43965, 133967, 16115);
        string id = CubeSighting.SpotId(1010, at);
        var log = new CubeLog();
        log.Merge(new CubeSighting(id, 1010, at.X, at.Y, at.Z, 1_200_039, 0));
        var spot = log.Find(id)!;

        Assert.True(spot.IsReliable(CubeSighting.FreshMs)); // just seen
        Assert.False(spot.IsReliable(CubeSighting.FreshMs + 1)); // a while ago: only a hint

        // Seen again a few minutes later: same visit
        log.Merge(new CubeSighting(id, 1010, at.X, at.Y, at.Z, 1_200_039, 5 * 60_000));
        Assert.Equal(1, log.Find(id)!.Visits);

        // Back an hour later and it is there again: a fixed spot, trusted from now on
        log.Merge(new CubeSighting(id, 1010, at.X, at.Y, at.Z, 1_200_039, 65 * 60_000));
        Assert.Equal(2, log.Find(id)!.Visits);
        Assert.True(log.Find(id)!.IsReliable(10 * 60 * 60_000));
    }
}
