using Avion2.Engine;
using Avion2.Protocol;

namespace Avion2.Tests;

public class PortraitTests : IDisposable
{
    private const string Picture = "https://profileimg.plaync.com/game_profile_images/aion2global/images?gameServerKey=1&charKey=abc";
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1];
    private static readonly byte[] OtherJpeg = [0xFF, 0xD8, 0xFF, 0xE1, 0, 0x10, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 2];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "avion2-portraits-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private int _lookups;

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private PortraitStore Store() => new(_dir, () => _now);

    private Func<string, ushort, Task<(bool, string?)>> Site(bool answers = true, string? url = Picture) => (_, _) =>
    {
        _lookups++;
        return Task.FromResult((answers, answers ? url : null));
    };

    [Fact]
    public async Task APortraitIsSavedAndKeptAcrossRuns()
    {
        var store = Store();
        Assert.True(await store.RefreshAsync("Hero", 1321, Site(), _ => Task.FromResult(Jpeg)));

        var again = Store(); // the next run of the app
        Assert.Equal(Picture, again.Url("Hero", 1321));
        Assert.Equal(Jpeg, File.ReadAllBytes(again.PicturePath("Hero", 1321)!));
        Assert.DoesNotContain("Hero", Path.GetFileName(again.PicturePath("Hero", 1321)!)); // no names in file names
        Assert.Null(again.Url("Hero", 1001)); // per server
        Assert.Null(again.PicturePath("Other", 1321));
    }

    [Fact]
    public async Task AFreshPortraitIsNotAskedForAgainUntilItIsAWeekOld()
    {
        var store = Store();
        await store.RefreshAsync("Hero", 1321, Site(), _ => Task.FromResult(Jpeg));
        _now += TimeSpan.FromDays(6);
        Assert.False(store.NeedsRefresh("Hero", 1321));
        Assert.False(await store.RefreshAsync("Hero", 1321, Site(), _ => Task.FromResult(OtherJpeg)));
        Assert.Equal(1, _lookups);

        _now += TimeSpan.FromDays(1);
        string old = store.PicturePath("Hero", 1321)!;
        Assert.True(await store.RefreshAsync("Hero", 1321, Site(), _ => Task.FromResult(OtherJpeg)));
        Assert.Equal(OtherJpeg, File.ReadAllBytes(store.PicturePath("Hero", 1321)!));
        Assert.False(File.Exists(old)); // the old picture is replaced, not piled up
    }

    [Fact]
    public async Task ASiteThatDoesNotAnswerKeepsTheSavedPictureAndWaitsBeforeTheNextTry()
    {
        var store = Store();
        await store.RefreshAsync("Hero", 1321, Site(), _ => Task.FromResult(Jpeg));
        _now += TimeSpan.FromDays(8);

        Assert.False(await store.RefreshAsync("Hero", 1321, Site(answers: false), _ => Task.FromResult(OtherJpeg)));
        Assert.Equal(Jpeg, File.ReadAllBytes(store.PicturePath("Hero", 1321)!));
        Assert.False(store.NeedsRefresh("Hero", 1321));
        _now += PortraitStore.RetryAfter;
        Assert.True(store.NeedsRefresh("Hero", 1321));

        // a download that fails or isn't a picture counts the same
        Assert.False(await store.RefreshAsync("Hero", 1321, Site(), _ => throw new HttpRequestException("down")));
        _now += PortraitStore.RetryAfter;
        Assert.False(await store.RefreshAsync("Hero", 1321, Site(), _ => Task.FromResult("<html>"u8.ToArray())));
        Assert.Equal(Jpeg, File.ReadAllBytes(store.PicturePath("Hero", 1321)!));
    }

    [Fact]
    public async Task ACharacterWithoutAPictureHasNone()
    {
        var store = Store();
        await store.RefreshAsync("Hero", 1321, Site(url: null), _ => throw new InvalidOperationException("not asked"));

        Assert.Null(store.PicturePath("Hero", 1321));
        Assert.Null(store.Url("Hero", 1321));
        Assert.False(store.NeedsRefresh("Hero", 1321)); // asked: not again before the week is over
    }

    [Fact]
    public void OnlyJpegsAndPngsCount()
    {
        Assert.True(PortraitStore.IsPicture(Jpeg));
        Assert.True(PortraitStore.IsPicture([0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0]));
        Assert.False(PortraitStore.IsPicture("<!doctype html>"u8.ToArray()));
        Assert.False(PortraitStore.IsPicture([0xFF, 0xD8]));
    }

    [Fact]
    public void DiscordShowsThePortraitOnlyWhenAllowedAndNeverInStreamerMode()
    {
        var input = new PresenceInput { Capturing = true, Map = "Sanctum", SelfClass = GameClass.Chanter, SelfPictureUrl = Picture };

        Assert.Equal(DiscordPresence.LargeImageKey, DiscordPresence.Build(input, new PresenceOptions())!.LargeImage);
        Assert.Equal(Picture, DiscordPresence.Build(input, new PresenceOptions(ShowPicture: true))!.LargeImage);
        Assert.Equal(DiscordPresence.LargeImageKey,
            DiscordPresence.Build(input, new PresenceOptions(ShowPicture: true, StreamerMode: true))!.LargeImage);
        Assert.Equal(DiscordPresence.LargeImageKey,
            DiscordPresence.Build(input with { SelfPictureUrl = null }, new PresenceOptions(ShowPicture: true))!.LargeImage);
        Assert.Equal("chanter", DiscordPresence.Build(input, new PresenceOptions(ShowPicture: true))!.SmallImage);
    }

    [Theory]
    [InlineData(Picture, true)]
    [InlineData("http://profileimg.plaync.com/x.jpg", false)] // not https
    [InlineData("https://example.com/x.jpg", false)] // not the official site
    [InlineData("https://plaync.com.example.com/x.jpg", false)]
    [InlineData("avion2", false)]
    [InlineData(null, false)]
    public void OnlyTheOfficialSitesPicturesGoToDiscord(string? url, bool passed) =>
        Assert.Equal(passed, DiscordPresence.IsPictureUrl(url));
}
