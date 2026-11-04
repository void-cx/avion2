using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Avion2.Engine;
using Avion2.Protocol;

namespace Avion2.Tests;

public class DiscordTests
{
    private const long Now = 1_760_000_000_000;

    [Fact]
    public void FramesAreLittleEndianOpLengthAndUtf8Json()
    {
        var frame = DiscordFrames.Encode(DiscordOp.Frame, """{"a":"é"}""");

        Assert.Equal(new byte[] { 1, 0, 0, 0, 10, 0, 0, 0 }, frame[..8]); // "é" is two bytes
        Assert.Equal("""{"a":"é"}""", Encoding.UTF8.GetString(frame, 8, frame.Length - 8));
        Assert.Equal((DiscordOp.Frame, 10), DiscordFrames.ReadHeader(frame));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, DiscordFrames.Encode(DiscordOp.Handshake, "{}")[..4]);
    }

    [Fact]
    public void RefusesHeadersThatAreNotDiscords()
    {
        Assert.Throws<InvalidDataException>(() => DiscordFrames.ReadHeader(new byte[] { 9, 0, 0, 0, 2, 0, 0, 0 }));
        Assert.Throws<InvalidDataException>(() => DiscordFrames.ReadHeader(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0x7F }));
    }

    [Fact]
    public void HandshakeAndSetActivityPayloads()
    {
        using var handshake = JsonDocument.Parse(DiscordFrames.Handshake("1567454515794542642"));
        Assert.Equal(1, handshake.RootElement.GetProperty("v").GetInt32());
        Assert.Equal("1567454515794542642", handshake.RootElement.GetProperty("client_id").GetString());

        var activity = new DiscordActivity("Fighting Boss", "40% HP", Now, "avion2", "AVION2", "chanter", "Chanter");
        using var set = JsonDocument.Parse(DiscordFrames.SetActivity(4242, activity, "n1"));
        var root = set.RootElement;
        Assert.Equal("SET_ACTIVITY", root.GetProperty("cmd").GetString());
        Assert.Equal("n1", root.GetProperty("nonce").GetString());
        Assert.Equal(4242, root.GetProperty("args").GetProperty("pid").GetInt32());
        var a = root.GetProperty("args").GetProperty("activity");
        Assert.Equal("Fighting Boss", a.GetProperty("details").GetString());
        Assert.Equal("40% HP", a.GetProperty("state").GetString());
        Assert.Equal(Now, a.GetProperty("timestamps").GetProperty("start").GetInt64());
        Assert.Equal("avion2", a.GetProperty("assets").GetProperty("large_image").GetString());
        Assert.Equal("AVION2", a.GetProperty("assets").GetProperty("large_text").GetString());
        Assert.Equal("chanter", a.GetProperty("assets").GetProperty("small_image").GetString());
        Assert.Equal("Chanter", a.GetProperty("assets").GetProperty("small_text").GetString());

        using var clear = JsonDocument.Parse(DiscordFrames.SetActivity(4242, null, "n2"));
        Assert.Equal(JsonValueKind.Null, clear.RootElement.GetProperty("args").GetProperty("activity").ValueKind);
    }

    [Fact]
    public void ApplicationIdIsASnowflake()
    {
        Assert.True(DiscordPresence.IsApplicationId(DiscordPresence.ApplicationId));
        Assert.False(DiscordPresence.IsApplicationId(""));
        Assert.False(DiscordPresence.IsApplicationId("12345"));
        Assert.False(DiscordPresence.IsApplicationId("15674545157945426x2"));
    }

    private static readonly PresenceInput Base = new() { Capturing = true, SelfClass = GameClass.Templar };

    [Fact]
    public void NothingWhileTheMeterIsStopped() =>
        Assert.Null(DiscordPresence.Build(new PresenceInput { Map = "Sanctum" }, new PresenceOptions()));

    [Fact]
    public void IdleOnAMap()
    {
        var a = DiscordPresence.Build(Base with { Map = "Sanctum", MapSinceMs = Now }, new PresenceOptions())!;
        Assert.Equal("Sanctum", a.Details);
        Assert.Equal("Idle", a.State);
        Assert.Equal(Now, a.StartMs);
        Assert.Equal("avion2", a.LargeImage);
        Assert.Equal("AVION2", a.LargeText);
        Assert.Equal("templar", a.SmallImage);
        Assert.Equal("Templar", a.SmallText);

        var unknown = DiscordPresence.Build(new PresenceInput { Capturing = true }, new PresenceOptions())!;
        Assert.Equal("Idle", unknown.Details);
        Assert.Null(unknown.State);
        Assert.Null(unknown.StartMs);
        Assert.Null(unknown.SmallImage); // no class, no small picture
        Assert.Null(unknown.SmallText);
    }

    [Fact]
    public void DungeonRunWithItsBossesAndParty()
    {
        var run = new PresenceRun("Fire Temple", "Sealed Dungeon", Now - 60_000, 2, 4);
        var a = DiscordPresence.Build(Base with { Map = "Fire Temple", Run = run, PartySize = 5 }, new PresenceOptions())!;
        Assert.Equal("Fire Temple", a.Details);
        Assert.Equal("2 / 4 bosses · Party of 5", a.State);
        Assert.Equal(Now - 60_000, a.StartMs);

        Assert.Equal("Sealed Dungeon", DiscordPresence.Build(Base with { Run = run with { BossesKilled = 0, BossesTotal = null } }, new())!.State);
        Assert.Equal("1 boss", DiscordPresence.Build(Base with { Run = run with { BossesKilled = 1, BossesTotal = null } }, new())!.State);
        Assert.Equal("3 bosses", DiscordPresence.Build(Base with { Run = run with { BossesKilled = 3, BossesTotal = null } }, new())!.State);
    }

    [Fact]
    public void BossFightWithHpKeepsTheRunClock()
    {
        var boss = new PresenceBoss("Flame Lord", 0.426, Now - 5_000);
        var run = new PresenceRun("Fire Temple", "Sealed Dungeon", Now - 60_000, 1, 4);
        var a = DiscordPresence.Build(Base with { Boss = boss, Run = run, PartySize = 3 }, new PresenceOptions())!;
        Assert.Equal("Fighting Flame Lord", a.Details);
        Assert.Equal("43% HP · Fire Temple · Party of 3", a.State);
        Assert.Equal(Now - 60_000, a.StartMs);

        var field = DiscordPresence.Build(Base with { Boss = boss with { HpFraction = null }, Map = "Eltnen" }, new PresenceOptions())!;
        Assert.Equal("Eltnen", field.State);
        Assert.Equal(Now - 5_000, field.StartMs);
    }

    [Fact]
    public void GatheringCraftingAndCombat()
    {
        var gather = DiscordPresence.Build(Base with { Map = "Eltnen", Task = PresenceTask.Gathering, TaskSinceMs = Now, CombatSinceMs = Now }, new())!;
        Assert.Equal("Gathering", gather.Details);
        Assert.Equal("Eltnen", gather.State);
        Assert.Equal(Now, gather.StartMs);

        Assert.Equal("Crafting", DiscordPresence.Build(Base with { Task = PresenceTask.Crafting }, new())!.Details);

        var combat = DiscordPresence.Build(Base with { Map = "Eltnen", CombatSinceMs = Now - 3_000, PartySize = 1 }, new())!;
        Assert.Equal("In combat", combat.Details);
        Assert.Equal("Eltnen", combat.State); // alone: no "Party of 1"
        Assert.Equal(Now - 3_000, combat.StartMs);
    }

    [Fact]
    public void OurNameOnlyWhenAllowedAndNeverInStreamerMode()
    {
        var input = Base with { Map = "Sanctum", SelfName = "Hero", PartySize = 2 };
        string all(DiscordActivity a) => string.Join("|", a.Details, a.State, a.LargeText, a.SmallText);

        Assert.DoesNotContain("Hero", all(DiscordPresence.Build(input, new PresenceOptions())!));
        Assert.DoesNotContain("Hero", all(DiscordPresence.Build(input, new PresenceOptions(ShowName: true, StreamerMode: true))!));
        Assert.Equal("Idle · Party of 2 · Hero", DiscordPresence.Build(input, new PresenceOptions(ShowName: true))!.State);
    }

    [Fact]
    public void TextsFitDiscordsLimits()
    {
        var a = DiscordPresence.Build(Base with { Map = new string('x', 300) }, new())!;
        Assert.Equal(128, a.Details!.Length);
        Assert.EndsWith("…", a.Details);
        Assert.Equal("Idle", DiscordPresence.Build(Base with { Map = "X" }, new())!.State); // a 1-letter details is dropped
        Assert.Null(DiscordPresence.Build(Base with { Map = "X" }, new())!.Details);
    }

    [Fact]
    public void ThrottleSendsChangesAtMostEvery15Seconds()
    {
        var throttle = new PresenceThrottle();
        var a = new DiscordActivity("A", "s", null, "avion2", "AVION2");
        var b = a with { Details = "B" };

        Assert.True(throttle.ShouldSend(a, Now));
        Assert.False(throttle.ShouldSend(a, Now + 60_000)); // unchanged: not again
        Assert.True(throttle.ShouldSend(b, Now + 60_000));

        var t = new PresenceThrottle();
        Assert.True(t.ShouldSend(a, Now));
        Assert.False(t.ShouldSend(b, Now + 14_999)); // changed, too soon
        Assert.True(t.ShouldSend(b, Now + 15_000));
        Assert.False(t.ShouldSend(b, Now + 40_000));
        Assert.True(t.ShouldSend(null, Now + 40_000)); // clearing is a change too
        t.Reset();
        Assert.True(t.ShouldSend(null, Now + 40_001)); // after a reset at once
    }

    [Fact]
    public async Task ClientHandshakesSendsTheActivityAndClearsOnStop()
    {
        var discord = new FakeDiscord();
        using var client = new DiscordClient(_ => Task.FromResult<Stream?>(discord), processId: 77, retry: TimeSpan.FromMinutes(1));
        client.SetActivity(new DiscordActivity("Sanctum", "Idle", Now, "avion2", "AVION2"));
        client.Start("1567454515794542642");

        var handshake = await discord.NextAsync();
        Assert.Equal(DiscordOp.Handshake, handshake.Op);
        Assert.Equal("1567454515794542642", handshake.Json.RootElement.GetProperty("client_id").GetString());

        discord.Reply(DiscordOp.Frame, """{"cmd":"DISPATCH","evt":"READY","data":{"v":1}}""");
        var set = await discord.NextAsync();
        Assert.Equal(DiscordOp.Frame, set.Op);
        Assert.Equal(77, set.Json.RootElement.GetProperty("args").GetProperty("pid").GetInt32());
        Assert.Equal("Sanctum", set.Json.RootElement.GetProperty("args").GetProperty("activity").GetProperty("details").GetString());
        await WaitFor(() => client.State == DiscordState.Connected);

        discord.Reply(DiscordOp.Ping, """{"x":1}""");
        var pong = await discord.NextAsync();
        Assert.Equal(DiscordOp.Pong, pong.Op);

        client.SetActivity(new DiscordActivity("Gathering", null, Now, "avion2", "AVION2"));
        var update = await discord.NextAsync();
        Assert.Equal("Gathering", update.Json.RootElement.GetProperty("args").GetProperty("activity").GetProperty("details").GetString());

        client.Stop(TimeSpan.FromSeconds(5));
        var cleared = await discord.NextAsync();
        Assert.Equal(JsonValueKind.Null, cleared.Json.RootElement.GetProperty("args").GetProperty("activity").ValueKind);
        Assert.Equal(DiscordState.Off, client.State);
    }

    [Fact]
    public async Task ClientReportsARefusedIdAndAMissingDiscord()
    {
        var discord = new FakeDiscord();
        using var refused = new DiscordClient(_ => Task.FromResult<Stream?>(discord), retry: TimeSpan.FromMinutes(1));
        refused.Start("1567454515794542642");
        await discord.NextAsync();
        discord.Reply(DiscordOp.Close, """{"code":4000,"message":"Invalid Client ID"}""");
        await WaitFor(() => refused.State == DiscordState.Rejected);

        int tries = 0;
        using var missing = new DiscordClient(_ => { tries++; return Task.FromResult<Stream?>(null); }, retry: TimeSpan.FromMilliseconds(20));
        missing.Start("1567454515794542642");
        await WaitFor(() => missing.State == DiscordState.NotRunning && tries >= 2); // keeps looking, quietly
        missing.Stop();
        Assert.Equal(DiscordState.Off, missing.State);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            await Task.Delay(10);
        }
    }

    /// <summary>Discord's side of the pipe, in memory: records our frames and plays back its answers.</summary>
    private sealed class FakeDiscord : Stream
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<(DiscordOp, JsonDocument)> _written = Channel.CreateUnbounded<(DiscordOp, JsonDocument)>();
        private byte[] _pending = [];
        private int _offset;

        public void Reply(DiscordOp op, string json) => _incoming.Writer.TryWrite(DiscordFrames.Encode(op, json));

        public async Task<(DiscordOp Op, JsonDocument Json)> NextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await _written.Reader.ReadAsync(timeout.Token);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset >= _pending.Length)
            {
                _pending = await _incoming.Reader.ReadAsync(cancellationToken);
                _offset = 0;
            }

            int n = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsMemory(_offset, n).CopyTo(buffer);
            _offset += n;
            return n;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var (op, length) = DiscordFrames.ReadHeader(buffer.Span);
            _written.Writer.TryWrite((op, JsonDocument.Parse(buffer.Slice(DiscordFrames.HeaderSize, length))));
            return ValueTask.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
