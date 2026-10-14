using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Avion2.Analysis;

namespace Avion2.Overlay;

/// <summary>
/// What the compass leads to: a cube the game showed us recently (<see cref="Exact"/>, precision finding), or an
/// area to search (<see cref="Area"/>): an aion2hub cube marker (their spots are 30–160 m off) or a spot where a cube
/// was seen a while ago.
/// </summary>
public sealed record CubeTarget(string Key, CubeSighting? Exact, MapMarker? Area, double RadiusMeters = CubeLog.SearchMeters)
{
    public static CubeTarget For(CubeSighting cube) => new(cube.Id, cube, null);

    public static CubeTarget For(MapMarker area) => new(area.Id!, null, area);

    /// <summary>A spot seen a while ago, as an area to search (cubes may come back elsewhere).</summary>
    public static CubeTarget LastSeen(CubeSighting cube, MapTransform transform)
    {
        var (x, y) = transform.ToImage(cube.Position);
        return new(cube.Id, null, new MapMarker("Last seen here", "hiddenCube", x, y, Id: cube.Id), CubeLog.LastSeenMeters);
    }
}

/// <summary>
/// A small always-on-top card that leads to a hidden cube, like a phone's "find my": an area to search while only
/// the hub's rough spot is known, then precision finding (green, distance, height) once the game shows the cube.
/// Up is where you face (from your skills or direction of travel); without a recent facing, map north.
/// </summary>
public partial class CompassWindow : Window
{
    /// <summary>A facing older than this is not trusted for "turn left/right".</summary>
    private const long FacingFreshMs = 30_000;

    private readonly AppState _state;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private WorldMap? _map;
    private CubeTarget? _target;
    private Func<WorldMap, MapTransform?> _transform = _ => null;

    public CompassWindow(AppState state)
    {
        _state = state;
        InitializeComponent();
        ThemeManager.Attach(this);
        Left = state.Stored.CompassLeft ?? SystemParameters.WorkArea.Right - 330;
        Top = state.Stored.CompassTop ?? SystemParameters.WorkArea.Top + 220;
        _timer.Tick += (_, _) => Refresh();
        Closed += (_, _) =>
        {
            _timer.Stop();
            state.Stored.CompassLeft = Left;
            state.Stored.CompassTop = Top;
            state.SaveSettings();
        };
    }

    /// <summary>Skip this one for a while: the owner points the compass at the next one.</summary>
    public event Action<CubeTarget>? SkipClicked;

    public CubeTarget? Target => _target;

    /// <summary>The latest state of the exact cube being led to (from the cube log), or null for an area.</summary>
    private CubeSighting? Exact => _target?.Exact is { } cube ? _state.CubeLog.Find(cube.Id) ?? cube : null;

    public void Point(WorldMap map, CubeTarget target, Func<WorldMap, MapTransform?> transform)
    {
        _map = map;
        _target = target;
        _transform = transform;
        if (!IsVisible)
        {
            Show();
        }

        _timer.Start();
        Refresh();
    }

    private void Refresh()
    {
        if (_map is null || _target is null || _transform(_map) is not { } transform)
        {
            return;
        }

        var exact = Exact;
        var (tx, ty) = exact is not null ? transform.ToImage(exact.Position) : (_target.Area!.X, _target.Area.Y);
        string region = _map.RegionAt(tx, ty) is { } r ? $"{r} · " : string.Empty;
        Kind.Text = exact is null ? (_target.Area?.Name == "Last seen here" ? "Hidden cube · last seen here" : "Hidden cube · search area")
            : exact.State switch
            {
                CubeState.Opened => "Hidden cube · opened",
                CubeState.Taken => $"Hidden cube · taken{(exact.TakenBy is { } who ? $" by {who}" : string.Empty)}",
                _ => "Hidden cube · spotted",
            };

        LiveEntity? self = null;
        uint? mapId = null;
        if (_state.Engine is { } engine)
        {
            (mapId, var live) = engine.LiveMap();
            self = live.FirstOrDefault(e => e.Kind == LiveKind.Self);
        }

        if (self is null || mapId != _map.MapId)
        {
            Dial.Update(0, null, northUp: true, exact is not null);
            Turn.Text = "Finding you…";
            Detail.Text = region + (_state.Engine is null ? "Start the meter to get directions."
                : mapId != _map.MapId ? $"Go to {_map.Name}."
                : "Waiting for your position (teleport, buff or skill).");
            return;
        }

        var (sx, sy) = transform.ToImage(self.Position);
        double dx = tx - sx, dy = ty - sy;
        double meters = Math.Sqrt(dx * dx + dy * dy) / transform.Scale / 100;
        bool there = exact is not null && meters <= CompassDial.HereMeters;
        bool searching = exact is null && meters <= _target.RadiusMeters;

        // Angles: 0° = +X (right on the map), growing toward +Y (down) — the game's facing convention
        double bearing = Math.Atan2(dy, dx) * 180 / Math.PI;
        if (self.Facing is { } facing && self.FacingAgeMs <= FacingFreshMs)
        {
            double relative = Normalize(bearing - facing);
            Dial.Update(relative, meters, northUp: false, exact is not null, searching);
            Turn.Text = there ? "You're here"
                : searching ? "Walk or fly around here"
                : Math.Abs(relative) <= 12 ? "Straight ahead"
                : Math.Abs(relative) >= 160 ? "Behind you"
                : relative > 0 ? $"Turn right {relative:0}°" : $"Turn left {-relative:0}°";
        }
        else
        {
            Dial.Update(Normalize(bearing + 90), meters, northUp: true, exact is not null, searching);
            Turn.Text = there ? "You're here" : searching ? "Walk or fly around here" : Cardinal(bearing);
        }

        string age = self.AgeMs < 3_000 ? "position now" : $"position {self.AgeMs / 1000} s old";
        string height = string.Empty;
        if (exact is not null)
        {
            double up = (exact.Z - self.Position.Z) / 100;
            height = Math.Abs(up) < 2 ? "same height · " : up > 0 ? $"↑ {up:0} m above you · " : $"↓ {-up:0} m below you · ";
        }

        Detail.Text = searching
            ? $"Somewhere within ~{_target.RadiusMeters:0} m · turns green once the game shows it"
            : exact is null ? $"{region}{(_target.Area?.Name == "Last seen here" ? "a cube was here earlier" : "approximate area")}" : height + region + age;
    }

    /// <summary>Render shots: a fixed state without the live update.</summary>
    internal void Demo(double angle, double meters, bool northUp, string turn, string detail, bool exact = false, bool searching = false)
    {
        _timer.Stop();
        Kind.Text = exact ? "Hidden cube · spotted" : "Hidden cube · search area";
        Dial.Update(angle, meters, northUp, exact, searching);
        Dial.Jump();
        Turn.Text = turn;
        Detail.Text = detail;
    }

    private static double Normalize(double degrees) => ((degrees % 360) + 540) % 360 - 180;

    /// <summary>Compass direction on the map (north = up).</summary>
    private static string Cardinal(double bearing)
    {
        string[] names = ["east", "south-east", "south", "south-west", "west", "north-west", "north", "north-east"];
        int index = (int)Math.Round(((bearing % 360) + 360) % 360 / 45) % 8;
        return $"Head {names[index]}";
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (_target is { } target)
        {
            SkipClicked?.Invoke(target);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
