using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Avion2.Analysis;
using Avion2.Protocol;

namespace Avion2.Overlay;

/// <summary>
/// A zone map: the picture, markers by layer, region names, and live positions from the meter. Drag to pan,
/// wheel to zoom, click a marker or dot for details, right-click a collectible to mark it found.
/// Coordinates: "map px" are the full-size picture's pixels (what markers use).
/// </summary>
public sealed class MapView : FrameworkElement
{
    // First: the static brushes below are built through it
    private static readonly Dictionary<Color, Brush> BrushCache = new();

    private WorldMap? _map;
    private BitmapSource? _image;
    private double _zoom = 0.1;          // screen px per map px
    private Point _center;               // map px at the middle of the view
    private Point? _dragFrom;
    private Point _dragCenter;
    private bool _dragged;
    private object? _selected;           // MapMarker or LiveEntity

    public MapView()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Hand;
    }

    /// <summary>Layers shown: marker groups (location, collection, gathering, monster, npc, creature) plus "live".</summary>
    public HashSet<string> Layers { get; } = ["location", "collection", "creature", "live"];

    /// <summary>Ids of collectibles marked as found.</summary>
    public HashSet<string> Found { get; set; } = [];

    /// <summary>World → map px for live positions; null = not calibrated (live dots hidden).</summary>
    public MapTransform? Transform { get; set; }

    public IReadOnlyList<LiveEntity> Live { get; private set; } = [];

    /// <summary>Subtypes switched off inside a shown layer (e.g. one kind of ore).</summary>
    public HashSet<string> HiddenSubtypes { get; set; } = [];

    /// <summary>Monster ranks shown (normal, elite, named).</summary>
    public HashSet<string> MonsterRanks { get; set; } = ["normal", "elite", "named"];

    /// <summary>Monster level range shown (0 = no limit).</summary>
    public int MinLevel { get; set; }
    public int MaxLevel { get; set; }

    /// <summary>Whether a marker passes the layers and filters.</summary>
    public bool Shows(MapMarker marker)
    {
        if (!Layers.Contains(LayerOf(marker)) || HiddenSubtypes.Contains(marker.Subtype))
        {
            return false;
        }

        if (_map?.GroupOf(marker.Subtype) != "monster")
        {
            return true;
        }

        int level = marker.Level ?? 0;
        return MonsterRanks.Contains(marker.Rank ?? "normal") &&
               (MinLevel <= 0 || level >= MinLevel) && (MaxLevel <= 0 || level <= MaxLevel);
    }

    /// <summary>Decodes a marker icon by file name (from the map data); null = draw shapes.</summary>
    public Func<string, ImageSource?>? IconSource { get; set; }

    /// <summary>Hidden cubes the game showed us on this map (exact positions; drawn when calibrated).</summary>
    public IReadOnlyList<CubeSighting> ExactCubes { get; set; } = [];

    /// <summary>The compass's approximate target: a search circle (centre and radius in map px), or null.</summary>
    public (Point Center, double Radius)? SearchArea { get; set; }

    /// <summary>Skipped cubes: marker id → until when (UTC). Shown dimmed with a clock while active.</summary>
    public IReadOnlyDictionary<string, DateTime> Skipped { get; set; } = new Dictionary<string, DateTime>();

    /// <summary>Raised when a skipped cube was right-clicked (to offer it again).</summary>
    public event Action<MapMarker>? UnskipRequested;

    private bool IsSkipped(string? id, out TimeSpan left)
    {
        left = TimeSpan.Zero;
        if (id is null || !Skipped.TryGetValue(id, out var until) || until <= DateTime.UtcNow)
        {
            return false;
        }

        left = until - DateTime.UtcNow;
        return true;
    }

    /// <summary>Raised when a collectible was marked found or not found (right-click).</summary>
    public event Action<MapMarker, bool>? FoundChanged;

    public WorldMap? Map => _map;

    /// <summary>The layer a marker belongs to: its group, except monolith materials (hundreds of them) apart from cubes.</summary>
    public string LayerOf(MapMarker marker) => marker.Subtype == "monolithMaterial" ? "monolith" : _map?.GroupOf(marker.Subtype) ?? "other";

    public void Show(WorldMap map, BitmapSource? image)
    {
        _map = map;
        _image = image;
        _selected = null;
        FitToView();
    }

    public void SetLive(IReadOnlyList<LiveEntity> live)
    {
        Live = live;
        if (_selected is LiveEntity selected)
        {
            _selected = live.FirstOrDefault(e => e.Id == selected.Id);
        }

        InvalidateVisual();
    }

    public void FitToView()
    {
        if (_map is null || ActualWidth <= 0)
        {
            return;
        }

        _zoom = Math.Min(ActualWidth, ActualHeight) / _map.Size;
        _center = new Point(_map.Size / 2.0, _map.Size / 2.0);
        InvalidateVisual();
    }

    /// <summary>Centers on a map point, zooming in to at least <paramref name="minZoom"/>.</summary>
    public void CenterOn(double x, double y, double minZoom = 0.35)
    {
        _center = new Point(x, y);
        _zoom = Math.Max(_zoom, minZoom);
        InvalidateVisual();
    }

    /// <summary>Shows the info card of any drawn object (render shots).</summary>
    internal void SelectObject(object item)
    {
        _selected = item;
        InvalidateVisual();
    }

    public void Select(MapMarker marker)
    {
        _selected = marker;
        CenterOn(marker.X, marker.Y);
    }

    /// <summary>Our own position in map px, if known and calibrated.</summary>
    public Point? SelfPoint => Live.FirstOrDefault(e => e.Kind == LiveKind.Self) is { } self && Transform is { } t
        ? ToPoint(t.ToImage(self.Position))
        : null;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.PreviousSize.Width == 0)
        {
            FitToView();
        }
    }

    private Point ToScreen(double x, double y) =>
        new((x - _center.X) * _zoom + ActualWidth / 2, (y - _center.Y) * _zoom + ActualHeight / 2);

    private Point ToMap(Point screen) =>
        new((screen.X - ActualWidth / 2) / _zoom + _center.X, (screen.Y - ActualHeight / 2) / _zoom + _center.Y);

    private static Point ToPoint((double X, double Y) p) => new(p.X, p.Y);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Background, null, new Rect(RenderSize));
        if (_map is null)
        {
            return;
        }

        var topLeft = ToScreen(0, 0);
        if (_image is not null)
        {
            dc.DrawImage(_image, new Rect(topLeft, new Size(_map.Size * _zoom, _map.Size * _zoom)));
        }

        var view = new Rect(ToMap(new Point(-20, -20)), ToMap(new Point(ActualWidth + 20, ActualHeight + 20)));
        DrawRegions(dc);
        DrawMarkers(dc, view);
        if (SearchArea is { } area)
        {
            double r = area.Radius * _zoom;
            var center = ToScreen(area.Center.X, area.Center.Y);
            dc.DrawEllipse(SearchFill, SearchPen, center, Math.Max(r, 6), Math.Max(r, 6));
        }

        DrawExactCubes(dc);
        DrawLive(dc);
        DrawSelection(dc);
    }

    private static readonly Brush Background = Frozen(Color.FromRgb(0x0B, 0x0F, 0x16));

    private void DrawRegions(DrawingContext dc)
    {
        if (_zoom < 0.18)
        {
            return;
        }

        foreach (var region in _map!.Regions)
        {
            var ring = region.Borders.OrderByDescending(r => r.Count).FirstOrDefault();
            if (ring is null || ring.Count == 0)
            {
                continue;
            }

            var center = ToScreen(ring.Average(p => p[0]), ring.Average(p => p[1]));
            var text = Text(Spaced(region.Name), 12.5, RegionBrush, FontWeights.SemiBold);
            DrawOutlined(dc, text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2), RegionBrush, 3.2);
        }
    }

    private void DrawMarkers(DrawingContext dc, Rect view)
    {
        bool labels = _zoom >= 0.45;
        foreach (var marker in _map!.Markers)
        {
            if (!view.Contains(marker.X, marker.Y))
            {
                continue;
            }

            string group = _map.GroupOf(marker.Subtype);
            if (!Shows(marker) || _zoom < MinZoom(group, marker))
            {
                continue;
            }

            var at = ToScreen(marker.X, marker.Y);
            var style = StyleOf(group, marker);
            bool found = marker.Id is { } id && Found.Contains(id);
            bool skipped = !found && IsSkipped(marker.Id, out _);
            double r = style.Radius * (_zoom < 0.2 ? 0.8 : 1);
            dc.PushOpacity(found ? 0.35 : skipped ? 0.5 : 1);
            if (_zoom >= IconZoom(group, marker) &&
                (_map.IconOf(marker.Subtype) is { } iconName ? IconSource?.Invoke(iconName) : Badge(group)) is { } icon)
            {
                // The game's own marker icon
                r = IconSize(group, marker) / 2 * (_zoom < 0.2 ? 0.75 : 1);
                dc.DrawImage(icon, new Rect(at.X - r, at.Y - r, r * 2, r * 2));
            }
            else switch (style.Shape)
            {
                case Shape.Diamond:
                    dc.DrawGeometry(style.Fill, OutlinePen, Diamond(at, r + 1));
                    break;
                case Shape.Square:
                    dc.DrawRectangle(style.Fill, OutlinePen, new Rect(at.X - r, at.Y - r, r * 2, r * 2));
                    break;
                default:
                    dc.DrawEllipse(style.Fill, OutlinePen, at, r, r);
                    break;
            }

            dc.Pop();
            if (skipped && _zoom >= 0.15)
            {
                var clock = Text("\u23F1", 10, LocationBrush);
                dc.DrawText(clock, new Point(at.X + r + 1, at.Y - clock.Height - 1));
            }

            if (labels && style.Label)
            {
                var text = Text(marker.Name, 11, LabelBrush, FontWeights.SemiBold);
                DrawOutlined(dc, text, new Point(at.X + r + 3, at.Y - text.Height / 2), LabelBrush);
            }
        }
    }

    /// <summary>Cube spots the game showed us: green while there, dimmed with ✓ (we opened it) or ✗ (taken).</summary>
    private void DrawExactCubes(DrawingContext dc)
    {
        if (Transform is not { } t || !Layers.Contains("collection"))
        {
            return;
        }

        foreach (var cube in ExactCubes)
        {
            var (x, y) = t.ToImage(cube.Position);
            var at = ToScreen(x, y);
            var state = cube.State;
            bool skipped = state == CubeState.Available && IsSkipped(cube.Id, out _);
            if (state == CubeState.Available && !cube.IsReliable(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            {
                // Seen a while ago: only a hint (cubes may come back elsewhere)
                dc.PushOpacity(skipped ? 0.4 : 0.8);
                dc.DrawRectangle(null, ExactRingPen, new Rect(at.X - 5, at.Y - 5, 10, 10));
                dc.Pop();
                continue;
            }

            dc.PushOpacity(state != CubeState.Available ? 0.45 : skipped ? 0.55 : 1);
            dc.DrawRectangle(state == CubeState.Taken ? TakenCubeBrush : ExactCubeBrush, OutlinePen, new Rect(at.X - 5, at.Y - 5, 10, 10));
            if (state == CubeState.Available)
            {
                dc.DrawEllipse(null, ExactRingPen, at, 9, 9);
            }

            dc.Pop();
            if (state != CubeState.Available)
            {
                var mark = Text(state == CubeState.Opened ? "✓" : "✗", 11, SelfFill, FontWeights.Bold);
                dc.DrawText(mark, new Point(at.X - mark.Width / 2, at.Y - mark.Height / 2));
            }
        }
    }

    private void DrawLive(DrawingContext dc)
    {
        if (Transform is not { } transform || !Layers.Contains("live"))
        {
            return;
        }

        foreach (var e in Live.OrderBy(e => e.Kind == LiveKind.Self))
        {
            var at = ToScreen(transform.ToImage(e.Position).X, transform.ToImage(e.Position).Y);
            if (at.X < -30 || at.Y < -30 || at.X > ActualWidth + 30 || at.Y > ActualHeight + 30)
            {
                continue;
            }

            if (e.Facing is { } facing && e.Kind is LiveKind.Self or LiveKind.Player)
            {
                DrawFacing(dc, at, facing, e.Kind == LiveKind.Self ? 18 : 11, e.FacingAgeMs);
            }

            switch (e.Kind)
            {
                case LiveKind.Self:
                    dc.DrawEllipse(null, SelfRing, at, 11, 11);
                    dc.DrawEllipse(SelfFill, OutlinePen, at, 5.5, 5.5);
                    var you = Text("You", 11.5, SelfFill, FontWeights.SemiBold);
                    DrawOutlined(dc, you, new Point(at.X - you.Width / 2, at.Y - 28), SelfFill);
                    break;
                case LiveKind.Player:
                    var brush = Frozen(e.Class == GameClass.Unknown ? Color.FromRgb(0xC9, 0xD7, 0xE4) : OverlayViewModel.ClassColor(e.Class));
                    dc.DrawEllipse(brush, OutlinePen, at, 4.5, 4.5);
                    if (_zoom >= 0.3)
                    {
                        var name = Text(e.Name, 10.5, brush, FontWeights.SemiBold);
                        DrawOutlined(dc, name, new Point(at.X + 7, at.Y - name.Height / 2), brush, 2.6);
                    }

                    break;
                default:
                    bool boss = e.Kind == LiveKind.Boss;
                    dc.PushOpacity(e.AgeMs > 10_000 ? 0.45 : 1);
                    dc.DrawEllipse(null, boss ? BossPen : MonsterPen, at, boss ? 7 : 3.5, boss ? 7 : 3.5);
                    dc.Pop();
                    break;
            }
        }
    }

    /// <summary>A pointer in the facing direction (0° = +X = right, growing toward +Y = down); fades when old.</summary>
    private static void DrawFacing(DrawingContext dc, Point at, float degrees, double length, long ageMs)
    {
        double a = degrees * Math.PI / 180, dx = Math.Cos(a), dy = Math.Sin(a);
        var tip = new Point(at.X + dx * length, at.Y + dy * length);
        var left = new Point(at.X + dx * length * 0.45 - dy * length * 0.35, at.Y + dy * length * 0.45 + dx * length * 0.35);
        var right = new Point(at.X + dx * length * 0.45 + dy * length * 0.35, at.Y + dy * length * 0.45 - dx * length * 0.35);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(tip, true, true);
            c.LineTo(left, true, false);
            c.LineTo(right, true, false);
        }

        g.Freeze();
        dc.PushOpacity(ageMs > 30_000 ? 0.35 : 0.9);
        dc.DrawGeometry(FacingBrush, OutlinePen, g);
        dc.Pop();
    }

    private void DrawSelection(DrawingContext dc)
    {
        if (_selected is null)
        {
            return;
        }

        string title;
        var lines = new List<string>();
        Point at;
        switch (_selected)
        {
            case MapMarker m:
                at = ToScreen(m.X, m.Y);
                title = m.Name;
                lines.Add(string.Join(" · ", new[]
                {
                    _map!.LabelOf(m.Subtype),
                    m.Level is { } lv ? $"Lv {lv}" : null,
                    m.Rank is { } rank and not "normal" ? Capitalize(rank) : null,
                }.OfType<string>()));
                if (_map.RegionAt(m.X, m.Y) is { } region)
                {
                    lines.Add(Spaced(region));
                }

                if (m.SpawnKey is { } key && _map.Drops.TryGetValue(key, out var drops) && drops.Count > 0)
                {
                    lines.Add("Drops: " + string.Join(", ", drops.Take(6)) + (drops.Count > 6 ? $" +{drops.Count - 6}" : string.Empty));
                }

                if (m.Subtype == "hiddenCube")
                {
                    lines.Add($"Reported cube area: cubes turned up 30–160 m from these spots");
                    lines.Add("Walk or fly around; spots the game shows you turn green");
                    if (m.Id is { } areaId && IsSkipped(areaId, out var areaLeft))
                    {
                        lines.Add($"Skipped · back in {Left(areaLeft)} (right-click to offer it again)");
                    }
                }
                else if (m.Id is { } id)
                {
                    lines.Add(Found.Contains(id) ? "Found ✓ (right-click to undo)"
                        : IsSkipped(id, out var left) ? (left > TimeSpan.FromDays(3650) ? "Skipped (right-click to offer it again)"
                            : $"Skipped · back in {(left.TotalHours >= 1 ? $"{(int)left.TotalHours} h {left.Minutes} min" : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min")} (right-click to offer it again)")
                        : "Right-click to mark as found");
                }

                break;
            case CubeSighting cube when Transform is { } ct:
                var cp = ct.ToImage(cube.Position);
                at = ToScreen(cp.X, cp.Y);
                title = "Hidden Cube";
                lines.Add(cube.State switch
                {
                    CubeState.Opened => $"Opened by you {When(cube.OpenedMs)}",
                    CubeState.Taken => $"Taken{(cube.TakenBy is { } who ? $" by {who}" : string.Empty)} {When(cube.TakenMs)}",
                    _ when !cube.IsReliable(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) => $"Last seen here {When(cube.SeenMs)} · may be elsewhere now",
                    _ => $"There · seen {When(cube.SeenMs)}",
                });
                if (cube.Visits >= 2)
                {
                    lines.Add($"Seen here on {cube.Visits} visits: a fixed spot");
                }
                lines.Add($"Exact spot · height {cube.Z / 100:0} m" + (Live.FirstOrDefault(l => l.Kind == LiveKind.Self) is { } me
                    ? $" ({(cube.Z - me.Position.Z) / 100:+0;-0;0} m from you)" : string.Empty));
                if (IsSkipped(cube.Id, out var cubeLeft))
                {
                    lines.Add($"Skipped · back in {Left(cubeLeft)}");
                }

                if (_map!.RegionAt(cp.X, cp.Y) is { } cubeRegion)
                {
                    lines.Add(Spaced(cubeRegion));
                }

                break;
            case LiveEntity e when Transform is { } t:
                var p = t.ToImage(e.Position);
                at = ToScreen(p.X, p.Y);
                title = e.Kind == LiveKind.Self ? "You" : e.Name;
                lines.Add(e.Kind switch
                {
                    LiveKind.Self or LiveKind.Player => e.Class == GameClass.Unknown ? "Player" : e.Class.ToString(),
                    LiveKind.Boss => "Boss",
                    _ => "Monster",
                });
                lines.Add(e.AgeMs < 2_000 ? "Position: now" : $"Position: {e.AgeMs / 1000} s ago");
                if (e.Facing is not null)
                {
                    lines.Add(e.FacingAgeMs < 2_000 ? "Facing: now" : $"Facing: {e.FacingAgeMs / 1000} s ago");
                }
                if (_map!.RegionAt(p.X, p.Y) is { } where)
                {
                    lines.Add(Spaced(where));
                }

                break;
            default:
                return;
        }

        dc.DrawEllipse(null, SelectPen, at, 10, 10);
        var heading = Text(title, 13, TitleBrush, FontWeights.SemiBold);
        var body = lines.Select(l => Text(l, 11, BodyBrush)).ToList();
        double width = Math.Min(340, Math.Max(heading.Width, body.Select(b => b.Width).DefaultIfEmpty(0).Max()) + 20);
        foreach (var text in body)
        {
            text.MaxTextWidth = width - 20;
        }

        double height = 14 + heading.Height + body.Sum(b => b.Height + 2);
        var card = new Rect(Math.Min(at.X + 16, ActualWidth - width - 8), Math.Max(8, at.Y - height / 2), width, height);
        dc.DrawRoundedRectangle(CardBrush, CardPen, card, 6, 6);
        double y = card.Top + 7;
        dc.DrawText(heading, new Point(card.Left + 10, y));
        y += heading.Height + 2;
        foreach (var text in body)
        {
            dc.DrawText(text, new Point(card.Left + 10, y));
            y += text.Height + 2;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_map is null)
        {
            return;
        }

        var mouse = e.GetPosition(this);
        var before = ToMap(mouse);
        double min = Math.Min(ActualWidth, ActualHeight) / _map.Size * 0.8;
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.25 : 0.8), min, 2.0);
        var after = ToMap(mouse);
        _center = new Point(_center.X + before.X - after.X, _center.Y + before.Y - after.Y);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        _dragFrom = e.GetPosition(this);
        _dragCenter = _center;
        _dragged = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragFrom is not { } from || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var now = e.GetPosition(this);
        if (!_dragged && (now - from).Length < 4)
        {
            return;
        }

        _dragged = true;
        _center = new Point(_dragCenter.X - (now.X - from.X) / _zoom, _dragCenter.Y - (now.Y - from.Y) / _zoom);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (_dragFrom is not null && !_dragged)
        {
            _selected = HitTest(e.GetPosition(this));
            InvalidateVisual();
        }

        _dragFrom = null;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (HitTest(e.GetPosition(this)) is MapMarker { Id: { } skippedId } skippedMarker && !Found.Contains(skippedId) && IsSkipped(skippedId, out _))
        {
            _selected = skippedMarker;
            UnskipRequested?.Invoke(skippedMarker);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (HitTest(e.GetPosition(this)) is MapMarker { Id: { } id } marker && marker.Subtype != "hiddenCube")
        {
            bool found = Found.Add(id) || !Found.Remove(id);
            _selected = marker;
            FoundChanged?.Invoke(marker, found);
            InvalidateVisual();
            e.Handled = true;
        }
    }

    /// <summary>The nearest live dot or visible marker within a few pixels of a screen point.</summary>
    private object? HitTest(Point screen)
    {
        if (_map is null)
        {
            return null;
        }

        const double reach = 9;
        if (Transform is { } t && Layers.Contains("live"))
        {
            var dot = Live
                .Select(e => (Entity: e, Distance: (ToScreen(t.ToImage(e.Position).X, t.ToImage(e.Position).Y) - screen).Length))
                .Where(x => x.Distance <= reach)
                .OrderBy(x => x.Distance)
                .FirstOrDefault();
            if (dot.Entity is not null)
            {
                return dot.Entity;
            }
        }

        if (Transform is { } ct && Layers.Contains("collection"))
        {
            var cube = ExactCubes
                .Select(c => (Cube: c, Distance: (ToScreen(ct.ToImage(c.Position).X, ct.ToImage(c.Position).Y) - screen).Length))
                .Where(x => x.Distance <= reach)
                .OrderBy(x => x.Distance)
                .FirstOrDefault();
            if (cube.Cube is not null)
            {
                return cube.Cube;
            }
        }

        return _map.Markers
            .Where(Shows)
            .Select(m => (Marker: m, Distance: (ToScreen(m.X, m.Y) - screen).Length))
            .Where(x => x.Distance <= reach)
            .OrderBy(x => x.Distance)
            .Select(x => x.Marker)
            .FirstOrDefault();
    }

    private enum Shape
    {
        Dot,
        Diamond,
        Square,
    }

    private sealed record MarkerStyle(Brush Fill, double Radius, Shape Shape, bool Label);

    private static MarkerStyle StyleOf(string group, MapMarker marker) => group switch
    {
        "location" => marker.Subtype == "teleport"
            ? new MarkerStyle(TeleportBrush, 5, Shape.Diamond, true)
            : new MarkerStyle(LocationBrush, 5, Shape.Diamond, true),
        "collection" => marker.Subtype == "hiddenCube"
            ? new MarkerStyle(CubeAreaBrush, 4, Shape.Dot, false)
            : new MarkerStyle(MonolithBrush, 3, Shape.Dot, false),
        "gathering" => new MarkerStyle(GatherBrush, 3, Shape.Dot, false),
        "npc" => new MarkerStyle(NpcBrush, 3, Shape.Dot, false),
        "creature" => new MarkerStyle(BossBrush, 7, Shape.Diamond, true),
        "monster" => marker.Rank switch
        {
            "named" => new MarkerStyle(BossBrush, 6, Shape.Dot, true),
            "elite" => new MarkerStyle(EliteBrush, 4.5, Shape.Dot, false),
            _ => new MarkerStyle(MonsterBrush, 2.5, Shape.Dot, false),
        },
        _ => new MarkerStyle(LocationBrush, 3, Shape.Dot, false),
    };

    private static readonly Dictionary<string, ImageSource?> Badges = new();

    /// <summary>
    /// Our own icon where the game has none: a coloured disc with a dark rim and a white symbol from the Windows icon
    /// font (vector, sharp at any zoom).
    /// </summary>
    public static ImageSource? Badge(string group)
    {
        if (Badges.TryGetValue(group, out var cached))
        {
            return cached;
        }

        (Color fill, string glyph)? look = group switch
        {
            "npc" => (Color.FromRgb(0xE8, 0xB9, 0x3A), "\uE77B"),     // person
            "location" => (Color.FromRgb(0x4F, 0xB8, 0xD8), "\uE707"), // map pin
            "gathering" => (Color.FromRgb(0x5C, 0xB8, 0x5C), "\uE8D4"),
            "collection" => (Color.FromRgb(0xB0, 0x5C, 0xD8), "\uE7B8"),
            _ => null,
        };
        if (look is not { } badge)
        {
            Badges[group] = null;
            return null;
        }

        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            var fill = new SolidColorBrush(badge.fill);
            dc.DrawEllipse(fill, new Pen(new SolidColorBrush(Color.FromArgb(0xE6, 0x12, 0x14, 0x1A)), 2.2), new Point(16, 16), 14, 14);
            var glyph = new FormattedText(badge.glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                15, Brushes.White, 1.0);
            dc.DrawText(glyph, new Point(16 - glyph.Width / 2, 16 - glyph.Height / 2));
        }

        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        Badges[group] = image;
        return image;
    }

    /// <summary>Ordinary monsters and NPCs only appear once zoomed in a bit (thousands of them would hide the map).</summary>
    private static double MinZoom(string group, MapMarker marker) =>
        group is "monster" && marker.Rank != "named" || group is "npc" ? 0.18 : 0;

    /// <summary>
    /// From which zoom a kind gets its icon (below: a small dot), so thousands of monsters and gathering spots do not
    /// cover the map when zoomed out. Places, world bosses, named monsters and cubes always show their icon.
    /// </summary>
    private static double IconZoom(string group, MapMarker marker) => group switch
    {
        "location" or "creature" => 0,
        "collection" => marker.Subtype == "hiddenCube" ? 0 : 0.35,
        "gathering" => 0.35,
        "monster" => marker.Rank == "named" ? 0.2 : 0.6,
        _ => 0.6,
    };

    /// <summary>Icon size (px at normal zoom) by kind: places and bosses large, monsters and gathering small.</summary>
    private static double IconSize(string group, MapMarker marker) => group switch
    {
        "location" => 24,
        "creature" => 30,
        "collection" => marker.Subtype == "hiddenCube" ? 20 : 15,
        "gathering" => 17,
        "npc" => 15,
        "monster" => marker.Rank switch { "named" => 22, "elite" => 15, _ => 11 },
        _ => 14,
    };

    /// <summary>Light text with a dark outline: readable on parchment and on dark terrain alike.</summary>
    private static void DrawOutlined(DrawingContext dc, FormattedText text, Point at, Brush fill, double outline = 3)
    {
        var geometry = text.BuildGeometry(at);
        dc.DrawGeometry(null, new Pen(HaloBrush, outline) { LineJoin = PenLineJoin.Round }, geometry);
        dc.DrawGeometry(fill, null, geometry);
    }

    private static Geometry Diamond(Point at, double r)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(at.X, at.Y - r), true, true);
            c.LineTo(new Point(at.X + r, at.Y), true, false);
            c.LineTo(new Point(at.X, at.Y + r), true, false);
            c.LineTo(new Point(at.X - r, at.Y), true, false);
        }

        g.Freeze();
        return g;
    }

    private static string When(long? ms)
    {
        if (ms is not { } value)
        {
            return string.Empty;
        }

        var ago = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(value);
        return ago.TotalMinutes < 1 ? "just now"
            : ago.TotalHours < 1 ? $"{(int)ago.TotalMinutes} min ago"
            : ago.TotalDays < 1 ? $"{(int)ago.TotalHours} h ago"
            : $"{(int)ago.TotalDays} d ago";
    }

    private static string Left(TimeSpan left) =>
        left > TimeSpan.FromDays(3650) ? "when you un-skip it"
        : left.TotalHours >= 1 ? $"{(int)left.TotalHours} h {left.Minutes} min"
        : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min";

    /// <summary>"EllunRiverMarsh" → "Ellun River Marsh" (region keys come without spaces).</summary>
    private static string Spaced(string name) => WorldMap.Readable(name);

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private FormattedText Text(string text, double size, Brush brush, FontWeight? weight = null) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static Brush Frozen(Color color)
    {
        lock (BrushCache)
        {
            if (!BrushCache.TryGetValue(color, out var brush))
            {
                brush = new SolidColorBrush(color);
                brush.Freeze();
                BrushCache[color] = brush;
            }

            return brush;
        }
    }

    private static Pen FrozenPen(Color color, double width)
    {
        var pen = new Pen(Frozen(color), width);
        pen.Freeze();
        return pen;
    }

    private static readonly Brush TeleportBrush = Frozen(Color.FromRgb(0x4F, 0xD8, 0xF0));
    private static readonly Brush LocationBrush = Frozen(Color.FromRgb(0xF2, 0xE6, 0xC4));
    private static readonly Brush MonolithBrush = Frozen(Color.FromRgb(0x8F, 0xB8, 0xFF));
    private static readonly Brush GatherBrush = Frozen(Color.FromRgb(0x6C, 0xD4, 0x6C));
    private static readonly Brush NpcBrush = Frozen(Color.FromRgb(0xF5, 0xD0, 0x4A));
    private static readonly Brush MonsterBrush = Frozen(Color.FromRgb(0xE0, 0x5A, 0x4F));
    private static readonly Brush EliteBrush = Frozen(Color.FromRgb(0xF0, 0x8A, 0x3C));
    private static readonly Brush BossBrush = Frozen(Color.FromRgb(0xFF, 0x6A, 0x2A));
    private static readonly Brush SelfFill = Frozen(Colors.White);
    private static readonly Brush ExactCubeBrush = Frozen(CompassDial.ExactColor);
    private static readonly Brush TakenCubeBrush = Frozen(Color.FromRgb(0x9A, 0xA3, 0xAE));
    private static readonly Brush CubeAreaBrush = Frozen(Color.FromArgb(0x90, 0xE0, 0x4F, 0xD8));
    private static readonly Brush SearchFill = Frozen(Color.FromArgb(0x30, 0xE6, 0xC2, 0x5A));
    private static readonly Pen SearchPen = FrozenPen(Color.FromArgb(0xC0, 0xE6, 0xC2, 0x5A), 1.5);
    private static readonly Pen ExactRingPen = FrozenPen(Color.FromArgb(0xC0, 0x5B, 0xD4, 0x8A), 1.5);
    private static readonly Brush FacingBrush = Frozen(Color.FromRgb(0x4F, 0xD8, 0xF0));
    private static readonly Brush LabelBrush = Frozen(Color.FromRgb(0xF4, 0xF1, 0xEA));
    private static readonly Brush RegionBrush = Frozen(Color.FromRgb(0xF2, 0xE2, 0xB4));
    private static readonly Brush HaloBrush = Frozen(Color.FromArgb(0xE0, 0x0E, 0x10, 0x16));
    private static readonly Brush TitleBrush = Frozen(Color.FromRgb(0xF4, 0xF1, 0xEA));
    private static readonly Brush BodyBrush = Frozen(Color.FromRgb(0xB8, 0xC2, 0xD0));
    private static readonly Brush CardBrush = Frozen(Color.FromArgb(0xEE, 0x10, 0x16, 0x22));
    private static readonly Pen CardPen = FrozenPen(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF), 1);
    private static readonly Pen OutlinePen = FrozenPen(Color.FromArgb(0xB0, 0x10, 0x10, 0x10), 1);
    private static readonly Pen SelfRing = FrozenPen(Color.FromRgb(0x4F, 0xD8, 0xF0), 2);
    private static readonly Pen MonsterPen = FrozenPen(Color.FromRgb(0xE0, 0x3A, 0x30), 2);
    private static readonly Pen BossPen = FrozenPen(Color.FromRgb(0xFF, 0x6A, 0x2A), 2.5);
    private static readonly Pen SelectPen = FrozenPen(Colors.White, 2);
}
