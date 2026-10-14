using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Overlay;

public sealed record MapChoice(string Key, string Name);

public sealed record SkipChoice(int Minutes, string Label);

public sealed record FindLine(MapMarker Marker, string Text)
{
    public override string ToString() => Text;
}

/// <summary>Zone maps with markers (teleports, hidden cubes, gathering, monsters…) and live positions from the meter.</summary>
public partial class MapPage : UserControl
{
    private static readonly (string Group, string Label)[] LayerChoices =
    [
        ("location", "Teleports & places"),
        ("collection", "Hidden cubes"),
        ("monolith", "Monolith materials"),
        ("gathering", "Gathering"),
        ("creature", "World bosses"),
        ("monster", "Monsters"),
        ("npc", "NPCs"),
        ("live", "Live: you, players, monsters"),
    ];

    private readonly AppState _state;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Dictionary<string, BitmapSource?> _images = new();
    private CompassWindow? _compass;
    private bool _loading;

    public MapPage(AppState state)
    {
        _state = state;
        InitializeComponent();

        var choices = BundledGameData.WorldMapKeys
            .Select(k => BundledGameData.WorldMap(k) is { } map ? new MapChoice(k, map.Name) : null)
            .OfType<MapChoice>()
            .ToList();
        MapBox.ItemsSource = choices;

        if (state.Stored.MapLayers is { Count: > 0 } layers)
        {
            View.Layers.Clear();
            View.Layers.UnionWith(layers);
        }

        foreach (var (group, label) in LayerChoices)
        {
            var box = new CheckBox { Content = label, IsChecked = View.Layers.Contains(group), Margin = new Thickness(0, 0, 0, 2), Tag = group };
            box.Click += Layer_Click;
            LayerList.Children.Add(box);
        }

        View.Found = new HashSet<string>(state.Stored.FoundCollectibles);
        View.FoundChanged += (_, _) =>
        {
            state.Stored.FoundCollectibles = View.Found.ToList();
            state.SaveSettings();
        };
        View.Skipped = state.Stored.SkippedCubes;
        View.UnskipRequested += marker =>
        {
            state.Stored.SkippedCubes.Remove(marker.Id!);
            state.SaveSettings();
        };
        var skips = new List<SkipChoice> { new(15, "15 min"), new(30, "30 min"), new(60, "1 hour"), new(24 * 60, "1 day"), new(0, "until I un-skip it") };
        SkipBox.ItemsSource = skips;
        SkipBox.SelectedItem = skips.FirstOrDefault(s => s.Minutes == state.Stored.CubeSkipMinutes) ?? skips[1];
        state.CubeUpdated += OnCube;

        FollowBox.IsChecked = state.Stored.MapFollow;
        _loading = true;
        MapBox.SelectedItem = choices.FirstOrDefault(c => c.Key == state.Stored.MapKey) ?? choices.First();
        _loading = false;

        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) =>
        {
            ShowMap(((MapChoice)MapBox.SelectedItem).Key);
            Tick();
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void MapBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MapBox.SelectedItem is not MapChoice choice)
        {
            return;
        }

        ShowMap(choice.Key);
        _state.Stored.MapKey = choice.Key;
        _state.SaveSettings();
    }

    private void ShowMap(string key)
    {
        if (BundledGameData.WorldMap(key) is not { } map || View.Map == map)
        {
            return;
        }

        View.Show(map, Image(key));
        View.Transform = TransformFor(map);
        View.ExactCubes = _state.CubeLog.OnMap(map.MapId).ToList();
        RefreshCubes();
        Find(FindBox.Text);
    }

    private BitmapSource? Image(string key)
    {
        if (!_images.TryGetValue(key, out var image))
        {
            using var stream = BundledGameData.WorldMapImage(key);
            if (stream is not null)
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }

            // Keep only the current picture in memory (an 8×8 map is 4096² pixels)
            _images.Clear();
            _images[key] = image;
        }

        return image;
    }

    /// <summary>The built-in transform, else one fitted from the field boss table the game sent for this map.</summary>
    private MapTransform? TransformFor(WorldMap map)
    {
        if (map.Transform is { } known)
        {
            return known;
        }

        var table = _state.FieldBosses.Maps.FirstOrDefault(m => m.MapId == map.MapId);
        return table is null ? null : map.Calibrate(table.Entries, code => _state.Data.FieldBossName(code));
    }

    private void Tick()
    {
        if (View.Map is not { } map)
        {
            return;
        }

        if (_state.Engine is not { } engine)
        {
            View.SetLive([]);
            LiveStatus.Text = "Start the meter to see yourself and others on the map.";
            CenterMe.IsEnabled = false;
            return;
        }

        var (mapId, live) = engine.LiveMap();
        if (FollowBox.IsChecked == true && mapId is { } id && map.MapId != id &&
            BundledGameData.WorldMapKeys.Select(BundledGameData.WorldMap).FirstOrDefault(m => m?.MapId == id) is { } current)
        {
            MapBox.SelectedItem = ((IEnumerable<MapChoice>)MapBox.ItemsSource).First(c => c.Key == current.Key);
            map = current;
        }

        View.Transform ??= TransformFor(map);
        bool here = mapId == map.MapId;
        View.SetLive(here ? live : []);
        CenterMe.IsEnabled = here && View.SelfPoint is not null;

        int players = live.Count(e => e.Kind == LiveKind.Player);
        LiveStatus.Text = !here
            ? mapId is null ? "Waiting for the game to load a map…" : "You are on another map."
            : View.Transform is null
                ? "Live positions appear once the game sends this map's field boss table (stay a moment on the map)."
                : View.SelfPoint is null
                    ? $"{players} players nearby. Your own dot appears after a teleport, buff or skill (the game doesn't send your movement)."
                    : $"{players} players nearby. Your dot updates on teleports, buffs and skills.";
    }

    private void Layer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: string group } box)
        {
            if (box.IsChecked == true)
            {
                View.Layers.Add(group);
            }
            else
            {
                View.Layers.Remove(group);
            }

            _state.Stored.MapLayers = View.Layers.ToList();
            _state.SaveSettings();
            View.InvalidateVisual();
        }
    }

    private void FollowBox_Click(object sender, RoutedEventArgs e)
    {
        _state.Stored.MapFollow = FollowBox.IsChecked == true;
        _state.SaveSettings();
    }

    /// <summary>
    /// The game showed us a cube, or someone opened one: redraw, lift a skip on a cube that is back, and keep the
    /// compass on something worth finding — a cube that shows up while searching an area takes over (precision
    /// finding), and one that was just opened or taken hands over to the next.
    /// </summary>
    private void OnCube(CubeSighting cube)
    {
        if (cube.State == CubeState.Available && _state.Stored.SkippedCubes.Remove(cube.Id))
        {
            _state.SaveSettings();
        }

        if (View.Map is { } map)
        {
            View.ExactCubes = _state.CubeLog.OnMap(map.MapId).ToList();
        }

        RefreshCubes();
        if (_compass?.Target is not { } target || View.Map is not { } current || cube.MapId != current.MapId)
        {
            UpdateSearchArea();
            return;
        }

        var latest = _state.CubeLog.Find(cube.Id) ?? cube;
        if (target.Exact?.Id == latest.Id && latest.State != CubeState.Available)
        {
            GoToNextCube(); // opened by us or taken by someone else
        }
        else if (target.Area is not null && latest.State == CubeState.Available && Near(latest, target.Area))
        {
            Lead(current, CubeTarget.For(latest)); // found it: precision finding from here on
        }

        UpdateSearchArea();
    }

    /// <summary>A cube close enough to a search area (or to us) to be the one we are looking for.</summary>
    private bool Near(CubeSighting cube, MapMarker area)
    {
        if (View.Transform is not { } t)
        {
            return false;
        }

        var (x, y) = t.ToImage(cube.Position);
        double reach = CubeLog.SearchMeters * 1.25 * 100 * t.Scale;
        bool nearArea = Math.Sqrt((x - area.X) * (x - area.X) + (y - area.Y) * (y - area.Y)) <= reach;
        bool nearUs = View.SelfPoint is { } me && Math.Sqrt((x - me.X) * (x - me.X) + (y - me.Y) * (y - me.Y)) <= reach;
        return nearArea || nearUs;
    }

    /// <summary>aion2hub's cube markers on this map: areas where cubes were reported, not positions.</summary>
    private IEnumerable<MapMarker> Areas => View.Map?.Markers.Where(m => m.Subtype == "hiddenCube" && m.Id is not null) ?? [];

    private void RefreshCubes()
    {
        if (View.Map is not { } map)
        {
            return;
        }

        var spots = _state.CubeLog.OnMap(map.MapId).ToList();
        int opened = spots.Count(c => c.OpenedMs is not null);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int open = spots.Count(c => c.IsReliable(now));
        CubeProgress.Text = spots.Count == 0 ? "None seen here yet" : $"{opened} opened · {open} there now";
        CubeHint.Text = spots.Count == 0
            ? "Cubes the game shows you are kept here with their exact spot. Until then the hub's markers are search areas."
            : $"{spots.Count} spots seen on {map.Name}; {_state.CubeLog.OpenedCount()} opened on all maps. Hub markers are search areas.";
        NextCube.IsEnabled = spots.Count > 0 || Areas.Any();
    }

    private bool IsSkipped(string id) =>
        _state.Stored.SkippedCubes.TryGetValue(id, out var until) && until > DateTime.UtcNow;

    /// <summary>
    /// What to lead to next: the nearest cube the game showed us that is still there, unless a hub area is clearly
    /// closer (by more than its uncertainty); skipped ones are left out while others remain.
    /// </summary>
    private CubeTarget? NextTarget()
    {
        if (View.Map is not { } map || View.Transform is not { } t)
        {
            return null;
        }

        var from = View.SelfPoint ?? new Point(map.Size / 2.0, map.Size / 2.0);
        double Distance(double x, double y) => Math.Sqrt((x - from.X) * (x - from.X) + (y - from.Y) * (y - from.Y));

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var spots = _state.CubeLog.OnMap(map.MapId)
            .Where(c => c.State == CubeState.Available && !IsSkipped(c.Id))
            .Select(c => (Cube: c, Distance: Distance(t.ToImage(c.Position).X, t.ToImage(c.Position).Y)))
            .ToList();
        var exact = spots.Where(c => c.Cube.IsReliable(now)).OrderBy(c => c.Distance).FirstOrDefault();

        // Areas to search: hub markers, and spots where a cube was seen a while ago (tighter: a real sighting)
        var areas = Areas
            .Where(a => !IsSkipped(a.Id!))
            .Select(a => (Target: CubeTarget.For(a), Distance: Distance(a.X, a.Y)))
            .Concat(spots.Where(c => !c.Cube.IsReliable(now)).Select(c => (Target: CubeTarget.LastSeen(c.Cube, t), c.Distance)))
            .OrderBy(a => a.Distance - a.Target.RadiusMeters * 100 * t.Scale)
            .ToList();
        var area = areas.FirstOrDefault();

        if (exact.Cube is not null && (area.Target is null || exact.Distance <= area.Distance + area.Target.RadiusMeters * 100 * t.Scale))
        {
            return CubeTarget.For(exact.Cube);
        }

        if (area.Target is not null)
        {
            return area.Target;
        }

        // Everything is skipped: the area whose skip ends first
        return Areas.OrderBy(a => _state.Stored.SkippedCubes.GetValueOrDefault(a.Id!)).Select(CubeTarget.For).FirstOrDefault();
    }

    private void SkipBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SkipBox.SelectedItem is SkipChoice choice && choice.Minutes != _state.Stored.CubeSkipMinutes)
        {
            _state.Stored.CubeSkipMinutes = choice.Minutes;
            _state.SaveSettings();
        }
    }

    /// <summary>Hides a cube or area from the compass for the chosen time (saved, so it survives a restart).</summary>
    private void Skip(CubeTarget target)
    {
        var skips = _state.Stored.SkippedCubes;
        foreach (var expired in skips.Where(s => s.Value <= DateTime.UtcNow).Select(s => s.Key).ToList())
        {
            skips.Remove(expired);
        }

        int minutes = _state.Stored.CubeSkipMinutes;
        skips[target.Key] = minutes <= 0 ? DateTime.MaxValue : DateTime.UtcNow.AddMinutes(minutes);
        _state.SaveSettings();
        View.InvalidateVisual();
    }

    /// <summary>Leads to the best cube or area and opens the compass overlay.</summary>
    private void NextCube_Click(object sender, RoutedEventArgs e) => GoToNextCube();

    private void GoToNextCube()
    {
        if (NextTarget() is not { } next || View.Map is not { } map)
        {
            _compass?.Close();
            return;
        }

        Lead(map, next);
    }

    private void Lead(WorldMap map, CubeTarget target)
    {
        View.Layers.Add("collection");
        SyncLayerBoxes();
        if (target.Area is { } area)
        {
            View.Select(area);
        }
        else if (target.Exact is { } cube && View.Transform is { } t)
        {
            var (x, y) = t.ToImage(cube.Position);
            View.CenterOn(x, y);
            View.SelectObject(cube);
        }

        if (_compass is null)
        {
            _compass = new CompassWindow(_state);
            _compass.SkipClicked += skipped =>
            {
                Skip(skipped);
                GoToNextCube();
            };
            _compass.Closed += (_, _) =>
            {
                _compass = null;
                UpdateSearchArea();
            };
        }

        _compass.Point(map, target, TransformFor);
        UpdateSearchArea();
    }

    /// <summary>A search circle around the compass target while it is only a hub area.</summary>
    private void UpdateSearchArea()
    {
        View.SearchArea = _compass?.Target is { Area: { } area } target && View.Transform is { } t
            ? (new Point(area.X, area.Y), target.RadiusMeters * 100 * t.Scale)
            : null;
        View.InvalidateVisual();
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FindHint.Visibility = FindBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Find(FindBox.Text);
    }

    private void Find(string query)
    {
        if (View.Map is not { } map || query.Trim().Length < 2)
        {
            FindList.Visibility = Visibility.Collapsed;
            return;
        }

        var lines = map.Markers
            .Where(m => m.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .GroupBy(m => (m.Name, m.Subtype))
            .Select(g => new FindLine(g.First(), g.Count() > 1 ? $"{g.Key.Name} ({g.Count()})" : g.Key.Name))
            .Take(40)
            .ToList();
        FindList.ItemsSource = lines;
        FindList.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FindList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FindList.SelectedItem is FindLine line && View.Map is { } map)
        {
            View.Layers.Add(View.LayerOf(line.Marker));
            SyncLayerBoxes();
            View.Select(line.Marker);
        }
    }

    private void SyncLayerBoxes()
    {
        foreach (CheckBox box in LayerList.Children)
        {
            box.IsChecked = View.Layers.Contains((string)box.Tag);
        }
    }

    private void CenterMe_Click(object sender, RoutedEventArgs e)
    {
        if (View.SelfPoint is { } me)
        {
            View.CenterOn(me.X, me.Y, 0.5);
        }
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => View.FitToView();

    /// <summary>For render shots: the first exact cube, zoomed in with its card.</summary>
    internal bool PreviewExactCube()
    {
        Preview("World_L_A", string.Empty);
        if (View.ExactCubes.FirstOrDefault() is not { } cube || View.Transform is not { } t)
        {
            return false;
        }

        var (x, y) = t.ToImage(cube.Position);
        View.CenterOn(x, y, 0.8);
        View.SelectObject(cube);
        return true;
    }

    /// <summary>For render shots: the compass on the next cube or area.</summary>
    internal CompassWindow? OpenCompass()
    {
        GoToNextCube();
        return _compass;
    }

    /// <summary>For render shots: pick a map and a marker.</summary>
    internal void Preview(string key, string find)
    {
        MapBox.SelectedItem = ((IEnumerable<MapChoice>)MapBox.ItemsSource).First(c => c.Key == key);
        if (View.Map?.Markers.FirstOrDefault(m => m.Name == find) is { } marker)
        {
            View.Select(marker);
        }
    }
}
