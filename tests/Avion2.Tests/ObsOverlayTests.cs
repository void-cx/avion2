using System.Net;
using System.Text.Json;
using Avion2.Analysis;
using Avion2.Engine;
using Avion2.Protocol;

namespace Avion2.Tests;

public class ObsOverlayTests
{
    private static ObsServer Server() => new(0, new Dictionary<string, ObsFile>
    {
        ["/overlay"] = new("text/html; charset=utf-8", "<!doctype html>page"u8.ToArray()),
    });

    [Fact]
    public async Task ServesThePageAndTheLatestState()
    {
        using var server = Server();
        server.Start();
        server.Publish("""{"running":true}""");
        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{server.Port}") };

        var page = await http.GetAsync("/overlay?view=meter");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Equal("<!doctype html>page", await page.Content.ReadAsStringAsync());

        Assert.Equal("""{"running":true}""", await http.GetStringAsync("/state"));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/nothing")).StatusCode);
    }

    [Fact]
    public void RefusesOtherHostsAndMethods()
    {
        using var server = Server();

        Assert.Equal(200, server.Answer("GET /state HTTP/1.1\r\nHost: 127.0.0.1:47421").Status);
        Assert.Equal(200, server.Answer("GET / HTTP/1.1\r\nHost: localhost").Status);
        Assert.Equal(403, server.Answer("GET /state HTTP/1.1\r\nHost: evil.example:47421").Status);
        Assert.Equal(403, server.Answer("GET /state HTTP/1.1").Status);
        Assert.Equal(405, server.Answer("POST /state HTTP/1.1\r\nHost: localhost").Status);
        Assert.Equal(400, server.Answer("nonsense").Status);
    }

    [Fact]
    public void HidesOthersButNotUsByDefault()
    {
        var app = new NameMask { Self = "Hero" };
        var names = new ObsNames(app);

        Assert.Equal("Hero", names.Name("Hero", self: true));
        Assert.Equal("Player 2", names.Name("Someone"));
        Assert.Equal("Knights", names.Legion("Knights"));

        names.HideSelf = true;
        names.HideOthers = false;
        Assert.Equal("You", names.Name("Hero"));
        Assert.Equal("Someone", names.Name("Someone"));
        Assert.Equal("Legion", names.Legion("Knights"));
    }

    [Fact]
    public void StreamerModeHidesEverything()
    {
        var app = new NameMask { On = true, Self = "Hero" };
        var names = new ObsNames(app) { HideOthers = false, HideSelf = false };

        Assert.Equal("You", names.Name("Hero", self: true));
        Assert.Equal("Player 2", names.Name("Someone"));
        Assert.Equal("Legion", names.Legion("Knights"));
    }

    [Fact]
    public void FeedCarriesTheFightAndTheCharacter()
    {
        var rows = new[]
        {
            Row(1, "Hero", GameClass.Gladiator, self: true, total: 6000, share: 0.6),
            Row(2, "Someone", GameClass.Cleric, self: false, total: 4000, share: 0.4),
        };
        var fight = new MeterSnapshot(1, "Tahabata", 10, 10_000, 1000, 500, 1000, rows);
        var me = new ObsCharacter("Hero", GameClass.Gladiator, "Gauss", "Knights", 64_358, "Verteron", 0.82);
        var names = new ObsNames(new NameMask { Self = "Hero" });

        using var json = JsonDocument.Parse(ObsFeed.Build(true, "void", fight, me, new ObsSession(3, 1200, -500, 1), names));
        var root = json.RootElement;

        Assert.True(root.GetProperty("running").GetBoolean());
        Assert.Equal("void", root.GetProperty("theme").GetString());
        var f = root.GetProperty("fight");
        Assert.Equal("Tahabata", f.GetProperty("title").GetString());
        Assert.Equal(0.5, f.GetProperty("hp").GetDouble());
        var meter = f.GetProperty("meter");
        Assert.Equal("Hero", meter[0].GetProperty("name").GetString());
        Assert.Equal("gladiator", meter[0].GetProperty("cls").GetString());
        Assert.Equal("Player 2", meter[1].GetProperty("name").GetString());
        Assert.Equal(400, meter[1].GetProperty("value").GetDouble());
        Assert.Equal(64_358, root.GetProperty("me").GetProperty("cp").GetInt32());
        Assert.Equal("Knights", root.GetProperty("me").GetProperty("legion").GetString());
        Assert.Equal(-500, root.GetProperty("session").GetProperty("kinah").GetInt64());

        using var idle = JsonDocument.Parse(ObsFeed.Build(false, "aion2", null, null, null, names));
        Assert.False(idle.RootElement.TryGetProperty("fight", out _));
        Assert.False(idle.RootElement.TryGetProperty("me", out _));
    }

    [Theory]
    [InlineData(false, "Earthquake", 0.4, 1.2)]
    [InlineData(true, "Earthquake", 0.25, null)]
    [InlineData(false, null, 0.4, 1.2)]
    public void FeedCarriesTheBossCast(bool interrupted, string? skill, double progress, double? left)
    {
        var cast = new BossCastInfo(skill, StartMs: 10_000, EndMs: 12_000, StoppedMs: interrupted ? 10_500 : null, interrupted);
        var fight = new MeterSnapshot(1, "Tahabata", 10, 10_000, 1000, 500, 1000, []) { BossCast = cast };

        using var json = JsonDocument.Parse(ObsFeed.Build(true, "aion2", fight, null, null, new ObsNames(new NameMask()), nowMs: 10_800));
        var c = json.RootElement.GetProperty("fight").GetProperty("cast");

        Assert.Equal(interrupted, c.GetProperty("interrupted").GetBoolean());
        Assert.Equal(progress, c.GetProperty("progress").GetDouble(), 3);
        Assert.Equal(skill is not null, c.TryGetProperty("skill", out _));
        Assert.Equal(left, c.TryGetProperty("left", out var l) ? l.GetDouble() : null);
    }

    [Fact]
    public void FeedHasNoCastWithoutABoss()
    {
        var fight = new MeterSnapshot(1, "Mob", 10, 10_000, 1000, null, null, [])
        {
            BossCast = new BossCastInfo("Earthquake", 10_000, 12_000, null, false),
        };

        using var json = JsonDocument.Parse(ObsFeed.Build(true, "aion2", fight, null, null, new ObsNames(new NameMask()), nowMs: 10_800));
        Assert.False(json.RootElement.GetProperty("fight").TryGetProperty("cast", out _));
    }

    [Fact]
    public void AddressCarriesOnlyWhatDiffersFromTheDefaults()
    {
        Assert.Equal("http://localhost:47421/overlay", new ObsPage().Url(47421));

        var page = new ObsPage { View = "character,meter", Rows = 5, Header = false, Session = true, Scale = 1.25, Theme = "void" };
        Assert.Equal("http://localhost:5000/overlay?view=character,meter&rows=5&header=0&session=1&scale=1.25&theme=void", page.Url(5000));

        // options of a part that isn't shown stay out
        var card = new ObsPage { View = "character", Rows = 3, Compact = true, Line = true };
        Assert.Equal("http://localhost:47421/overlay?view=character&style=line", card.Url(47421));
    }

    [Fact]
    public void SuggestedSizeGrowsWithWhatIsShown()
    {
        var meter = new ObsPage().SuggestedSize();
        var both = new ObsPage { View = "character,meter", Session = true }.SuggestedSize();
        var bigger = new ObsPage { Scale = 2 }.SuggestedSize();

        Assert.Equal(320, meter.Width);
        Assert.True(both.Height > meter.Height);
        Assert.Equal(640, bigger.Width);
        Assert.True(bigger.Height >= meter.Height * 2);
    }

    [Fact]
    public void PageOptionsSurviveTheSettingsFile()
    {
        var json = JsonSerializer.Serialize(new ObsPage { View = "taken", Rows = 4 });
        var back = JsonSerializer.Deserialize<ObsPage>(json)!;

        Assert.DoesNotContain("ShowsMeter", json);
        Assert.Equal("taken", back.View);
        Assert.Equal(4, back.Rows);
    }

    private static MeterRow Row(uint id, string name, GameClass cls, bool self, long total, double share) =>
        new(id, name, cls, self, total, total / 10.0, share, 0, 0, 10, 0, 0, 0, 0, []);
}
