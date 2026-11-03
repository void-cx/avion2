using Avion2.Analysis;
using Avion2.Protocol;
using static Avion2.Tests.TestPackets;

namespace Avion2.Tests;

/// <summary>Movement offsets and keyframes, their integration into tracks, and our own facing and height (vectors from captures).</summary>
public class MovementTests
{
    [Theory]
    // 1D 37 on foot: f 26 (dx, dy; state byte at the end), heading eba8
    [InlineData("1D37 84b101 26 0e f9 a8eb a8eb 01", 22660u, 14, -7, 0, 331.4f)]
    [InlineData("1D37 84b101 2e 0e f9 f9 a5eb a5eb 01", 22660u, 14, -7, -7, 331.4f)]
    // 20 37 falling: dz always, f 0b (dx, dy, trailing 01)
    [InlineData("2037 ec23 0b fc 0a f2 cd4f 589416c2 9949b942 cea481c3 01", 4588u, -4, 10, -14, 112.2f)]
    // 25 37 gliding: f 0f (dx, dy, dz, trailing byte)
    [InlineData("2537 a67a 0f 01 7c a5 803f 31ba6a41 21fd9544 27d94ec4 01", 15654u, 1, 124, -91, 89.3f)]
    // 2A 37 flying: f 1f (state byte first, dx, dy, dz), velocity before the heading
    [InlineData("2A37 8bcf02 1f 03 17 ce 07 89366b43 2289fcc3 68b69a42 c2d1", 42891u, 23, -50, 7, 295.0f)]
    // 38 37 swimming: f 16 (dx, dy, state byte at the end), heading before the velocity
    [InlineData("3837 a3cc01 16 f7 fa 3399 928cc3c2 67248bc2 00000000 01", 26147u, -9, -6, 0, 215.4f)]
    // 32 37 dodge roll: f 0f (dx, dy, dz, trailing byte), a zero u16, then the heading
    [InlineData("3237 a11d 0f 2e 4a f3 0000 7829 01", 3745u, 46, 74, -13, 58.3f)]
    public void ParsesMovementOffsets(string hex, uint entity, int dx, int dy, int dz, float heading)
    {
        var offset = Assert.IsType<MoveOffsetEvent>(Assert.Single(Parse(hex)));
        Assert.Equal(entity, offset.EntityId);
        Assert.Equal((dx, dy, dz), ((int)offset.Dx, (int)offset.Dy, (int)offset.Dz));
        Assert.Equal(heading, offset.Heading, 0.1f);
    }

    [Theory]
    [InlineData("1D37 84b101 26 0e f9 a8eb a8eb 01 00")] // one byte too many for its flags
    [InlineData("1D37 84b101 2e 0e f9 a8eb a8eb 01")] // dz flagged but missing
    [InlineData("2A37 8bcf02 1f 03 17 ce 07 89366b43 2289fcc3 68b69a42")]
    public void RejectsOffsetsThatBreakTheLengthRule(string hex) => Assert.Empty(Parse(hex));

    [Theory]
    // 1E 37 jump (f 14: no byte before the position, one at the end) and fall start (f 11: a byte before it)
    [InlineData("1E37 a11d 14 ae258ec7 24ef96c7 18a81246 7829 ac37d743 d0562e44 00007a44 03", -72779.4f, -77278.3f, 58.3f, PositionSource.Air)]
    [InlineData("1E37 ec23 11 02 de5fc5c7 309f2ec7 eb6f3446 cd4f 589416c2 9949b942 00000000 01", -101055.7f, -44703.2f, 112.2f, PositionSource.Air)]
    // 1F 37 falling (f 02: trailing 01), 24 37 gliding (f 01: trailing byte)
    [InlineData("1F37 8740 02 aa171546 4aeda0c7 b5fb8444 e9a8 5c9d20c4 b8687cc4 68da9fc3 01", 9541.9f, -82394.6f, 237.5f, PositionSource.Air)]
    [InlineData("2437 a11d 01 d3ac8cc7 9a7b94c7 baa11146 7829 588bcb44 133e3445 b8ae10c4 01", -72025.7f, -76023.2f, 58.3f, PositionSource.Air)]
    // 28 37 flight start (f 05: state byte first; f 04: at the end), 29 37 flying; velocity before the heading
    [InlineData("2837 8bcf02 05 03 fb5bc8c7 5c7c40c7 ae521c46 0a3d6b43 8087fcc3 9fb99a42 c2d1", -102584.0f, -49276.4f, 295.0f, PositionSource.Air)]
    [InlineData("2837 ad8901 04 8063c6c7 009544c7 00d41e46 9580ef43 538d57c4 0ec72443 a9d4 01", -101575f, -50325f, 299.1f, PositionSource.Air)]
    [InlineData("2937 8bcf02 03 03 d001c8c7 99ff41c7 ee3f1d46 b1985744 867ee7c4 7bd48d43 c2d1", -102403.6f, -49663.6f, 295.0f, PositionSource.Air)]
    // 30 37 dodge roll start: no flags
    [InlineData("3037 a11d dc088dc7 b32195c7 5b351046 7829 7829 01", -72209.7f, -76355.4f, 58.3f, PositionSource.Dash)]
    // 36 37 / 37 37 swimming
    [InlineData("3637 a3cc01 06 f08e41c7 03cea1c6 d8c19145 3399 928cc3c2 67248bc2 00000000 01 01", -49550.9f, -20711.0f, 215.4f, PositionSource.Swim)]
    [InlineData("3637 a87a 07 03 35af3ec7 300ca8c6 09764e45 fa26 17866643 4172a343 00000000 01", -48815.2f, -21510.1f, 54.8f, PositionSource.Swim)]
    [InlineData("3737 a3cc01 00 b6f041c7 2659a2c6 d8c19145 3399 928cc3c2 67248bc2 00000000 01", -49648.7f, -20780.6f, 215.4f, PositionSource.Swim)]
    public void ParsesKeyframes(string hex, float x, float y, float heading, PositionSource source)
    {
        var key = Assert.IsType<PositionEvent>(Assert.Single(Parse(hex)));
        Assert.Equal(x, key.Position.X, 0.1f);
        Assert.Equal(y, key.Position.Y, 0.1f);
        Assert.Equal(heading, key.Heading!.Value, 0.1f);
        Assert.Equal(source, key.Source);
    }

    [Theory]
    [InlineData("3F37 df2f 00 00 25e122c7 88af91c7 689a2e46 0100")]
    [InlineData("4037 df2f 08 00 4ddc18c6 191a52c6 8c5a9746 0100")]
    public void ParsesTheRarePositionRecordsAtTwoBytes(string hex)
    {
        var fix = Assert.IsType<PositionEvent>(Assert.Single(Parse(hex)));
        Assert.Equal(6111u, fix.EntityId);
        Assert.True(fix.Position.IsPlausible);
        Assert.Null(fix.Heading);
        Assert.Equal(PositionSource.Steer, fix.Source);
    }

    [Fact]
    public void ParsesGroundHeightAndRotate()
    {
        var ground = Assert.IsType<GroundHeightEvent>(Assert.Single(Parse("4637 df2f e91a4546")));
        Assert.Equal(6111u, ground.EntityId);
        Assert.Equal(12614.7f, ground.Z, 0.1f);

        var turn = Assert.IsType<RotateEvent>(Assert.Single(Parse("4E36 df2f b43bac43 33300743 00")));
        Assert.Equal(6111u, turn.EntityId);
        Assert.Equal(344.5f, turn.Facing, 0.1f);
        Assert.Equal(135.2f, turn.From, 0.1f);
        Assert.Equal(0, turn.Reason);
    }

    private static PositionEvent Key(long ms, uint id, float x, float y, float z = 1_000, PositionSource source = PositionSource.Move) =>
        new(ms, id, new Vec3(x, y, z), 0, source);

    private static MoveOffsetEvent Offset(long ms, uint id, sbyte dx, sbyte dy, sbyte dz = 0) => new(ms, id, dx, dy, dz, 45);

    [Fact]
    public void TrackerAddsOffsetsFromTheLastKeyframe()
    {
        var tracker = new MovementTracker();
        Assert.Null(tracker.Apply(Offset(0, 7, 10, 10))); // no keyframe yet: nothing to add to

        tracker.Apply(Key(100, 7, 5_000, 6_000));
        var first = tracker.Apply(Offset(200, 7, 30, -20, 5))!;
        Assert.Equal(new Vec3(5_030, 5_980, 1_005), first.Position);
        Assert.Equal(PositionSource.Offset, first.Source);
        Assert.Equal(45, first.Heading);
        Assert.Equal(new Vec3(5_060, 5_960, 1_010), tracker.Apply(Offset(300, 7, 30, -20, 5))!.Position);

        // A keyframe replaces the track; offsets continue from it
        tracker.Apply(Key(400, 7, 5_100, 5_950, source: PositionSource.Air));
        Assert.Equal(new Vec3(5_090, 5_950, 1_000), tracker.Apply(Offset(500, 7, -10, 0))!.Position);
    }

    [Fact]
    public void TrackerIgnoresBuffPositionsAndStaleTracks()
    {
        var tracker = new MovementTracker();
        tracker.Apply(Key(0, 7, 5_000, 6_000));
        // Where a buff landed is not the server's movement track
        tracker.Apply(new PositionEvent(50, 7, new Vec3(5_040, 6_000, 1_000), null, PositionSource.Buff));
        Assert.Equal(5_010, tracker.Apply(Offset(100, 7, 10, 0))!.Position.X);

        // After a long silence the offsets wait for the next keyframe
        Assert.Null(tracker.Apply(Offset(100 + MovementTracker.MaxGapMs + 1, 7, 10, 0)));
        Assert.Null(tracker.Apply(Offset(200 + MovementTracker.MaxGapMs, 7, 10, 0)));

        tracker.Apply(Key(10_000, 8, 1_000, 1_000));
        tracker.Apply(new ZoneChangeEvent(10_100));
        Assert.Null(tracker.Apply(Offset(10_200, 8, 10, 0)));
    }

    [Fact]
    public void OffsetsReachTheLiveMapAndTheReplayAtTenHertz()
    {
        var session = new GameSession(new GameData(
            new Dictionary<uint, string> { [16040000] = "Combustion" }, new Dictionary<uint, NpcData>()));
        session.Apply(new SelfIdentityEvent(0, 100, "TestDaeva", 1304, GameClass.Elementalist, 30));
        session.Apply(new NpcSpawnEvent(0, 900, 0x0C, 2100753, 100_000, null, new Vec3(10_000, 5_000, 2_000)));
        session.Apply(new PositionEvent(0, 900, new Vec3(10_000, 5_000, 2_000), null, PositionSource.Spawn));
        session.Apply(new DamageEvent(100, 100, 900, 16040000, 16040000, 1_000, HitType.Normal, HitFlags.None, 1, 0));
        // The monster walks +x at 50 cm per tick for 2 s; a second record inside a tick replaces the first
        for (int tick = 1; tick <= 20; tick++)
        {
            session.Apply(Offset(tick * 100, 900, 50, 0));
        }

        session.Apply(new PositionEvent(2_050, 900, new Vec3(11_050, 5_000, 2_000), null, PositionSource.Buff));
        Assert.Equal(11_050, session.Positions.Snapshot(session.Registry).Single(e => e.Id == 900).Position.X);

        session.Apply(new DamageEvent(2_100, 100, 900, 16040000, 16040000, 1_000, HitType.Normal, HitFlags.None, 1, 0));
        session.Tracker.Tick(60_000);
        var trace = session.Trace(Assert.Single(session.Tracker.History));
        var track = trace.Entity(900)!.Track;
        Assert.Equal(21, track.Count); // the spawn and one point per 100 ms tick
        Assert.Equal(PositionSource.Offset, track[10].Source);
        Assert.Equal(PositionSource.Buff, track[^1].Source);
        // Halfway between two ticks
        Assert.Equal(10_525, trace.PositionAt(900, (int)(1_050 - trace.StartMs))!.Value.X, 1);
    }

    [Fact]
    public void OurFacingAndHeightComeFromTurnsAndTheGround()
    {
        var session = new GameSession(new GameData(
            new Dictionary<uint, string> { [16040000] = "Combustion" }, new Dictionary<uint, NpcData>()));
        session.Apply(new SelfIdentityEvent(0, 100, "TestDaeva", 1304, GameClass.Elementalist, 30));
        session.Apply(new NpcSpawnEvent(0, 900, 0x0C, 2100753, 100_000, null, new Vec3(10_000, 5_000, 2_000)));
        session.Apply(new PositionEvent(500, 100, new Vec3(9_000, 5_000, 2_000), null, PositionSource.Buff));
        session.Apply(new RotateEvent(1_000, 100, 200, 90, 0));

        var me = session.Positions.Snapshot(session.Registry).Single(e => e.Kind == LiveKind.Self);
        Assert.Equal(200, me.Facing!.Value, 1);

        // Where we last stood on the ground: a new height, the fix (and its age) otherwise unchanged
        session.Apply(new GroundHeightEvent(1_500, 100, 2_400));
        me = session.Positions.Snapshot(session.Registry).Single(e => e.Kind == LiveKind.Self);
        Assert.Equal((9_000f, 2_400f), (me.Position.X, me.Position.Z));
        Assert.Equal(1_000, me.AgeMs);

        session.Apply(new DamageEvent(1_200, 100, 900, 16040000, 16040000, 1_000, HitType.Normal, HitFlags.None, 1, 0));
        session.Tracker.Tick(60_000);
        var trace = session.Trace(Assert.Single(session.Tracker.History));
        var turn = Assert.Single(trace.Turns);
        Assert.Equal(200, trace.FacingAt(100, turn.T + 500));
        Assert.Equal(200, trace.FacingAt(100, turn.T + 3_000)); // not moving: the turn still says where we face
        Assert.Null(trace.FacingAt(100, turn.T + 5_000));
    }

    [Fact]
    public void LiveFacingFollowsTravelAtTenHertz()
    {
        var session = new GameSession(GameData.Empty);
        session.Apply(new PlayerSpawnEvent(0, 60, "Someone", 1, GameClass.Cleric, null, new Vec3(5_000, 5_000, 300)));
        session.Apply(new PositionEvent(0, 60, new Vec3(5_000, 5_000, 300), null, PositionSource.Spawn));
        // 40 cm per tick toward -y: no single step is long enough, the stretch since the spawn is
        for (int tick = 1; tick <= 10; tick++)
        {
            session.Apply(Offset(tick * 100, 60, 0, -40));
        }

        Assert.Equal(270, session.Positions.Snapshot(session.Registry).Single().Facing!.Value, 1);
    }
}
