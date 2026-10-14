using Avion2.Analysis;
using Avion2.Protocol;
using static Avion2.Tests.TestPackets;

namespace Avion2.Tests;

public class FieldBossTests
{
    // Entries taken from a live map-1010 table: an alive boss, an alive boss with the odd extra byte
    // before its timestamp, and a dead one (no coordinates, future respawn time).
    private const string Table =
        "0191 0000 f2030000 03 " +
        "01 8d9506 0012d3c6 00be9b46 00529046 ff f2b0ee32a1010000 " +
        "01 919506 647864c7 d0175a47 00442146 ff f2b0ee32a1010000 " +
        "00 9d9506 00000040a1010000";

    [Fact]
    public void ParsesAliveAndDeadEntries()
    {
        var table = Assert.IsType<FieldBossTableEvent>(Assert.Single(Parse(Table)));
        Assert.Equal(1010u, table.MapId);
        Assert.Equal(3, table.Entries.Count);

        var first = table.Entries[0];
        Assert.Equal(101005u, first.SpawnCode);   // varint 8d 95 06 = map 1010 × 100 + 5
        Assert.True(first.Alive);
        Assert.Equal(1_791_855_866_098, first.TimestampMs);
        Assert.NotNull(first.X);

        Assert.Equal(101009u, table.Entries[1].SpawnCode);
        Assert.Equal(1_791_855_866_098, table.Entries[1].TimestampMs);

        var dead = table.Entries[2];
        Assert.Equal(101021u, dead.SpawnCode);
        Assert.False(dead.Alive);
        Assert.Null(dead.X);
    }

    [Fact]
    public void BoardShowsNamesStatusesAndOrder()
    {
        var data = new GameData(
            new Dictionary<uint, string>(),
            new Dictionary<uint, NpcData> { [2100077] = new("Ritualist Garshim", true) },
            new Dictionary<uint, uint> { [101005] = 2100077 });
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_791_877_091_000);
        long future = now.AddMinutes(30).ToUnixTimeMilliseconds();
        long past = now.AddMinutes(-30).ToUnixTimeMilliseconds();

        var board = new FieldBossBoard();
        Assert.True(board.Apply(new FieldBossTableEvent(0, 1010,
        [
            new FieldBossEntry(101005, true, past, 1, 2, 3),
            new FieldBossEntry(101007, false, future, null, null, null),
            new FieldBossEntry(101008, false, past, null, null, null),
        ])));

        var rows = board.Rows(data, now);
        Assert.Equal([FieldBossStatus.Respawning, FieldBossStatus.Due, FieldBossStatus.Alive], rows.Select(r => r.Status));
        Assert.Equal("Ritualist Garshim", rows[2].Name);
        Assert.Equal("Field boss 101007", rows[0].Name);

        // The same table again is not a change (no needless saves once a second).
        Assert.False(board.Apply(new FieldBossTableEvent(1000, 1010,
        [
            new FieldBossEntry(101005, true, past, 1, 2, 3),
            new FieldBossEntry(101007, false, future, null, null, null),
            new FieldBossEntry(101008, false, past, null, null, null),
        ])));
    }

    [Fact]
    public void BoardSurvivesSaveAndLoad()
    {
        string path = Path.Combine(Path.GetTempPath(), $"avion2-fb-{Guid.NewGuid():N}.json");
        try
        {
            var board = new FieldBossBoard();
            board.Apply(new FieldBossTableEvent(5, 1110, [new FieldBossEntry(111003, false, 1_791_900_000_000, null, null, null)]));
            board.Save(path);

            var loaded = FieldBossBoard.Load(path);
            var map = Assert.Single(loaded.Maps);
            Assert.Equal(1110u, map.MapId);
            Assert.Equal(111003u, Assert.Single(map.Entries).SpawnCode);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
