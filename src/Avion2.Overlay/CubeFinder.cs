using System.Windows;
using System.Windows.Threading;
using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <summary>
/// Find My Cube, running with the capture (not with the Map page): picks the cube or area to lead to, keeps the
/// compass on the nearest cube the game is rendering, handles skips, and shows the compass by itself when the
/// player chose so (<see cref="OverlaySettings.CompassAutoShow"/>). Off unless Find My Cube is switched on.
/// Only a cube the game sent us now (a packet this session, on this map, still near) is exact — the compass turns
/// green for nothing else; spots from the cube log are "last seen here" search areas.
/// </summary>
public sealed class CubeFinder
{
    private readonly AppState _state;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private CompassWindow? _compass;
    private uint? _mapId;

    /// <summary>The map shown on the Map page when "Find a cube" was clicked off that map (no live position yet).</summary>
    private WorldMap? _shownMap;

    /// <summary>What the player closed the compass on: it does not open by itself again for the same thing.</summary>
    private string? _dismissed;

    private bool _closingOurselves;

    /// <summary>Cubes the game sent this session on this map and still near us: the only exact targets.</summary>
    private readonly HashSet<string> _live = new();

    public CubeFinder(AppState state)
    {
        _state = state;
        _timer.Tick += (_, _) => Tick();
        state.CubeUpdated += OnCube;
        state.CubeFinderChanged += () =>
        {
            if (!state.Stored.CubeFinder)
            {
                Close();
            }
        };
        _timer.Start();
    }

    /// <summary>Raised when the compass points somewhere else (or closes): the Map page follows it.</summary>
    public event Action? TargetChanged;

    public CubeTarget? Target => _compass?.Target;

    public WorldMap? TargetMap => _compass is null ? null : LeadMap;

    private WorldMap? LeadMap => CurrentMap ?? _shownMap;

    internal CompassWindow? Compass => _compass;

    public bool CompassVisible => _compass?.IsVisible == true;

    /// <summary>The map we are on (one of the bundled ones), or null.</summary>
    public WorldMap? CurrentMap =>
        _mapId is { } id ? BundledGameData.WorldMapKeys.Select(BundledGameData.WorldMap).FirstOrDefault(m => m?.MapId == id) : null;

    /// <summary>The built-in transform, else one fitted from the field boss table the game sent for this map.</summary>
    public MapTransform? TransformFor(WorldMap map)
    {
        if (map.Transform is { } known)
        {
            return known;
        }

        var table = _state.FieldBosses.Maps.FirstOrDefault(m => m.MapId == map.MapId);
        return table is null ? null : map.Calibrate(table.Entries, code => _state.Data.FieldBossName(code));
    }

    /// <summary>Our position in map pixels, when known and the map is calibrated.</summary>
    private Point? SelfPoint(WorldMap map)
    {
        if (_state.Engine is not { } engine || TransformFor(map) is not { } t)
        {
            return null;
        }

        var (mapId, live) = engine.LiveMap();
        return mapId == map.MapId && live.FirstOrDefault(e => e.Kind == LiveKind.Self) is { } self
            ? new Point(t.ToImage(self.Position).X, t.ToImage(self.Position).Y)
            : null;
    }

    public bool IsSkipped(string id) =>
        _state.Stored.SkippedCubes.TryGetValue(id, out var until) && until > DateTime.UtcNow;

    /// <summary>aion2hub's cube markers on a map: areas where cubes were reported, not positions.</summary>
    public static IEnumerable<MapMarker> Areas(WorldMap map) => map.Markers.Where(m => m.Subtype == "hiddenCube" && m.Id is not null);

    private double? MetersTo(WorldMap map, CubeSighting cube)
    {
        if (TransformFor(map) is not { } t || SelfPoint(map) is not { } me)
        {
            return null;
        }

        var (x, y) = t.ToImage(cube.Position);
        return Math.Sqrt((x - me.X) * (x - me.X) + (y - me.Y) * (y - me.Y)) / t.Scale / 100;
    }

    /// <summary>The nearest cube the game is rendering for us now: sent this session, there, not skipped, within range.</summary>
    private (CubeSighting Cube, double Meters)? NearestRendered(WorldMap map) =>
        _state.CubeLog.OnMap(map.MapId)
            .Where(c => _live.Contains(c.Id) && c.State == CubeState.Available && !IsSkipped(c.Id))
            .Select(c => (Cube: c, Meters: MetersTo(map, c)))
            .Where(c => c.Meters is <= CubeLog.RenderMeters)
            .OrderBy(c => c.Meters)
            .Select(c => ((CubeSighting, double)?)(c.Cube, c.Meters!.Value))
            .FirstOrDefault();

    /// <summary>
    /// What to lead to next: a cube the game is showing us now (exact), else the nearest search area — hub markers
    /// and spots where a cube was seen before (approximate, never exact); skipped ones are left out while others remain.
    /// </summary>
    private CubeTarget? NextTarget(WorldMap map)
    {
        if (TransformFor(map) is not { } t)
        {
            return null;
        }

        if (NearestRendered(map) is { } rendered)
        {
            return CubeTarget.For(rendered.Cube);
        }

        var from = SelfPoint(map) ?? new Point(map.Size / 2.0, map.Size / 2.0);
        double Distance(double x, double y) => Math.Sqrt((x - from.X) * (x - from.X) + (y - from.Y) * (y - from.Y));
        var spots = _state.CubeLog.OnMap(map.MapId)
            .Where(c => c.State == CubeState.Available && !IsSkipped(c.Id))
            .Select(c => (Cube: c, Distance: Distance(t.ToImage(c.Position).X, t.ToImage(c.Position).Y)))
            .ToList();

        // Areas to search: hub markers, and spots where a cube was seen before (tighter: a real sighting)
        var area = Areas(map)
            .Where(a => !IsSkipped(a.Id!))
            .Select(a => (Target: CubeTarget.For(a), Distance: Distance(a.X, a.Y)))
            .Concat(spots.Select(c => (Target: CubeTarget.LastSeen(c.Cube, t), c.Distance)))
            .OrderBy(a => a.Distance - a.Target.RadiusMeters * 100 * t.Scale)
            .FirstOrDefault();

        if (area.Target is not null)
        {
            return area.Target;
        }

        // Everything is skipped: the area whose skip ends first
        return Areas(map).OrderBy(a => _state.Stored.SkippedCubes.GetValueOrDefault(a.Id!)).Select(CubeTarget.For).FirstOrDefault();
    }

    /// <summary>
    /// Leads to the best cube or area on the map we are on (else <paramref name="shown"/>, the map on screen) and
    /// shows the compass ("Find a cube").
    /// </summary>
    public bool FindNext(WorldMap? shown = null)
    {
        _dismissed = null;
        _shownMap = shown;
        return LeadToNext();
    }

    private bool LeadToNext()
    {
        if (!_state.Stored.CubeFinder || LeadMap is not { } map || NextTarget(map) is not { } next)
        {
            Close();
            return false;
        }

        Lead(map, next);
        return true;
    }

    private void Lead(WorldMap map, CubeTarget target)
    {
        if (_compass is null)
        {
            _compass = new CompassWindow(_state);
            _compass.SkipClicked += skipped =>
            {
                Skip(skipped);
                LeadToNext();
            };
            _compass.Closed += (_, _) =>
            {
                // Closed by the player: don't pop up again for the same cube or area
                if (!_closingOurselves)
                {
                    _dismissed = _compass?.Target?.Key ?? _dismissed;
                }

                _compass = null;
                TargetChanged?.Invoke();
            };
        }

        bool changed = _compass.Target?.Key != target.Key;
        _compass.Point(map, target, TransformFor);
        if (changed)
        {
            TargetChanged?.Invoke();
        }
    }

    public void Close()
    {
        if (_compass is null)
        {
            return;
        }

        _closingOurselves = true;
        _compass.Close();
        _closingOurselves = false;
    }

    /// <summary>Hides a cube or area from the compass for the chosen time (saved, so it survives a restart).</summary>
    public void Skip(CubeTarget target)
    {
        var skips = _state.Stored.SkippedCubes;
        foreach (var expired in skips.Where(s => s.Value <= DateTime.UtcNow).Select(s => s.Key).ToList())
        {
            skips.Remove(expired);
        }

        int minutes = _state.Stored.CubeSkipMinutes;
        skips[target.Key] = minutes <= 0 ? DateTime.MaxValue : DateTime.UtcNow.AddMinutes(minutes);
        _state.SaveSettings();
    }

    /// <summary>
    /// Twice a second while capturing: follow the map we are on, keep the compass on the nearest rendered cube, and
    /// show it by itself when chosen — "nearby" when the game renders a cube, "map" whenever there is something to
    /// find on this map. Closing it by hand holds until something new comes up.
    /// </summary>
    private void Tick()
    {
        if (!_state.Stored.CubeFinder || _state.Engine is not { } engine)
        {
            return;
        }

        var (mapId, _) = engine.LiveMap();
        if (mapId != _mapId)
        {
            _mapId = mapId;
            _dismissed = null;
            _live.Clear(); // another map: nothing is rendered until the game sends it
            if (_compass is not null)
            {
                LeadToNext(); // a new map: lead to something here
            }
        }

        if (CurrentMap is not { } map)
        {
            return;
        }

        // Cubes we walked away from are no longer rendered (the game sends them again when we come back)
        _live.RemoveWhere(id => _state.CubeLog.Find(id) is not { State: CubeState.Available } cube ||
                                MetersTo(map, cube) is > CubeLog.RenderMeters * 1.3);
        if (_compass?.Target?.Exact is { } exact && !_live.Contains(exact.Id))
        {
            LeadToNext(); // no longer rendered: back to searching
        }

        FollowRenderedCube(map);
        if (_compass is null && _state.Stored.CompassAutoShow == "nearby" &&
            NearestRendered(map) is { } nearest && nearest.Cube.Id != _dismissed)
        {
            Lead(map, CubeTarget.For(nearest.Cube));
        }
    }

    /// <summary>
    /// The nearest cube on screen always wins: whatever the compass leads to (an area, an old hint, a cube further
    /// away), it switches to a rendered cube — and between two rendered ones only when the new one is 10 m closer.
    /// </summary>
    private void FollowRenderedCube(WorldMap map)
    {
        if (_compass?.Target is not { } target || NearestRendered(map) is not { } nearest)
        {
            return;
        }

        if (target.Exact is { } leading)
        {
            if (leading.Id == nearest.Cube.Id)
            {
                return;
            }

            var current = _state.CubeLog.Find(leading.Id) ?? leading;
            if (current.State == CubeState.Available && MetersTo(map, current) is { } now && now <= CubeLog.RenderMeters && now - nearest.Meters < 10)
            {
                return; // already on a rendered cube about as close
            }
        }

        Lead(map, CubeTarget.For(nearest.Cube));
    }

    /// <summary>
    /// A cube packet: a cube the game sends now is rendered near us (the only exact kind); lift a skip on one that is
    /// back, hand over from one that was just opened or taken.
    /// </summary>
    private void OnCube(CubeSighting cube)
    {
        var known = _state.CubeLog.Find(cube.Id) ?? cube;
        if (known.State == CubeState.Available)
        {
            _live.Add(cube.Id);
        }
        else
        {
            _live.Remove(cube.Id);
        }

        if (cube.State == CubeState.Available && _state.Stored.SkippedCubes.Remove(cube.Id))
        {
            _state.SaveSettings();
        }

        if (_compass?.Target is not { } target || LeadMap is not { } map || cube.MapId != map.MapId)
        {
            return;
        }

        var latest = _state.CubeLog.Find(cube.Id) ?? cube;
        if (target.Exact?.Id == latest.Id && latest.State == CubeState.Opened)
        {
            Close(); // we picked it up: done (in "nearby" mode the next cube the game sends opens it again)
        }
        else if (target.Exact?.Id == latest.Id && latest.State == CubeState.Taken)
        {
            LeadToNext(); // someone else was faster: on to the next
        }
        else
        {
            FollowRenderedCube(map);
        }
    }
}
