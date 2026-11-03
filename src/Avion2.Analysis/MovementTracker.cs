using Avion2.Protocol;

namespace Avion2.Analysis;

/// <summary>
/// Adds up the server's 10 Hz movement offsets into positions, one track per entity: a keyframe (any movement
/// record with an absolute position, or a spawn) sets the track, each offset moves it by its whole-cm deltas from
/// the entity's previous record. That turns the ~1 Hz keyframes of moving players and monsters into ~10 Hz
/// positions. Not thread-safe (driven by <see cref="GameSession"/>).
/// </summary>
public sealed class MovementTracker
{
    /// <summary>
    /// An offset this long after the entity's previous record is not added to it: the track may have missed records
    /// (a gap in the capture) and waits for the next keyframe instead.
    /// </summary>
    public const long MaxGapMs = 5_000;

    /// <summary>Tracks without a record for this long are dropped (entities out of range).</summary>
    private const long ForgetMs = 60_000;

    private readonly Dictionary<uint, (Vec3 Position, long Ms)> _tracks = new();
    private long _lastPruneMs;

    /// <summary>Entities with a track.</summary>
    public int Count => _tracks.Count;

    /// <summary>Where the entity's track is now, and when its last record came.</summary>
    public bool TryGet(uint id, out Vec3 position, out long ms)
    {
        bool found = _tracks.TryGetValue(id, out var track);
        (position, ms) = track;
        return found;
    }

    /// <summary>The position an offset moved its entity to; null for other events and offsets without a track.</summary>
    public PositionEvent? Apply(GameEvent e)
    {
        PositionEvent? moved = null;
        switch (e)
        {
            case MoveOffsetEvent offset:
                if (_tracks.TryGetValue(offset.EntityId, out var track) && offset.TimestampMs - track.Ms is >= 0 and <= MaxGapMs)
                {
                    var at = new Vec3(track.Position.X + offset.Dx, track.Position.Y + offset.Dy, track.Position.Z + offset.Dz);
                    _tracks[offset.EntityId] = (at, offset.TimestampMs);
                    moved = new PositionEvent(offset.TimestampMs, offset.EntityId, at, offset.Heading, PositionSource.Offset);
                }
                else
                {
                    _tracks.Remove(offset.EntityId);
                }

                break;
            // Buff positions are where the buff landed (within ~0.6 m), not the server's movement track
            case PositionEvent { Source: not (PositionSource.Buff or PositionSource.Offset), Position.IsWorldPoint: true } key:
                _tracks[key.EntityId] = (key.Position, key.TimestampMs);
                break;
            case DeathEvent { Kind: DeathKind.LeftWorld } gone:
                _tracks.Remove(gone.EntityId);
                break;
            case ZoneChangeEvent:
                _tracks.Clear();
                break;
        }

        if (e.TimestampMs - _lastPruneMs > ForgetMs)
        {
            _lastPruneMs = e.TimestampMs;
            foreach (var (id, (_, ms)) in _tracks)
            {
                if (e.TimestampMs - ms > ForgetMs)
                {
                    _tracks.Remove(id);
                }
            }
        }

        return moved;
    }
}
