using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Overlay;

public sealed record MapChoice(string Key, string Name);

public sealed record SkipChoice(int Minutes, string Label);

/// <param name="MapKey">The zone the marker is on (search covers every map).</param>
public sealed record FindLine(MapMarker Marker, string MapKey, string Text, string Zone)
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

        View.HiddenSubtypes = new HashSet<string>(state.Stored.MapHiddenSubtypes);
        if (state.Stored.MapMonsterRanks is { Count: > 0 } ranks)
        {
            View.MonsterRanks = new HashSet<string>(ranks);
        }

        View.MinLevel = state.Stored.MapMinLevel;
        View.MaxLevel = state.Stored.MapMaxLevel;

        View.IconSource = LoadIcon;
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
        var skips = new List<SkipChoice> { new(15, "Skip: 15 min"), new(30, "Skip: 30 min"), new(60, "Skip: 1 hour"), new(24 * 60, "Skip: 1 day"), new(0, "Skip: until un-skipped") };
        SkipBox.ItemsSource = skips;
        SkipBox.SelectedItem = skips.FirstOrDefault(s => s.Minutes == state.Stored.CubeSkipMinutes) ?? skips[1];
        state.CubeUpdated += OnCube;
        state.CubeFinderChanged += ApplyCubeFinder;
        state.Cubes.TargetChanged += OnCompassTarget;

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
        BuildLayerPanel(map);
        ApplyCubeFinder();
        View.Transform = _state.Cubes.TransformFor(map);
        RefreshCubes();
        Find(FindBox.Text);
    }

    private readonly Dictionary<string, BitmapSource?> _icons = new();

    /// <summary>A built-in marker icon, decoded once.</summary>
    private BitmapSource? LoadIcon(string name)
    {
        if (!_icons.TryGetValue(name, out var icon))
        {
            using var stream = BundledGameData.MapIcon(name);
            if (stream is not null)
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                icon = bitmap;
            }

            _icons[name] = icon;
        }

        return icon;
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

        View.Transform ??= _state.Cubes.TransformFor(map);
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

    private readonly Dictionary<string, CheckBox> _groupBoxes = new();
    private readonly HashSet<string> _expanded = new();

    /// <summary>Smaller than the default checkbox (the panel lists many of them).</summary>
    private static CheckBox Compact(CheckBox box)
    {
        box.MinHeight = 0;
        box.Height = 26;
        box.FontSize = 12;
        box.Padding = new Thickness(4, 0, 0, 0);
        box.LayoutTransform = new System.Windows.Media.ScaleTransform(0.9, 0.9);
        return box;
    }

    /// <summary>
    /// One collapsible section per marker group, with a checkbox for the group, one per kind (with its icon and
    /// count), and for monsters rank and level filters. Rebuilt per map (maps have different kinds).
    /// </summary>
    private void BuildLayerPanel(WorldMap map)
    {
        LayerList.Children.Clear();
        _groupBoxes.Clear();
        var counts = map.Markers.GroupBy(m => m.Subtype).ToDictionary(g => g.Key, g => g.Count());

        foreach (var (group, label) in LayerChoices)
        {
            var kinds = map.Categories
                .Where(c => group == "monolith" ? c.Key == "monolithMaterial"
                    : group == "collection" ? c.Key == "hiddenCube"
                    : c.Group == group && c.Key != "monolithMaterial")
                .Where(c => counts.ContainsKey(c.Key))
                .ToList();
            int total = kinds.Sum(k => counts[k.Key]);
            if (group != "live" && total == 0)
            {
                continue;
            }

            var groupBox = Compact(new CheckBox
            {
                Content = group == "live" ? label : $"{label} ({total:N0})",
                IsChecked = View.Layers.Contains(group),
                Tag = group,
                VerticalAlignment = VerticalAlignment.Center,
            });
            groupBox.Click += Layer_Click;
            _groupBoxes[group] = groupBox;

            var details = new StackPanel { Margin = new Thickness(22, 0, 0, 4) };
            if (kinds.Count > 1)
            {
                foreach (var kind in kinds)
                {
                    details.Children.Add(KindBox(map, kind, counts[kind.Key]));
                }
            }

            if (group == "monster")
            {
                details.Children.Add(MonsterFilters(map));
            }

            if (details.Children.Count == 0)
            {
                LayerList.Children.Add(groupBox);
                continue;
            }

            // A compact row: the group's checkbox, and a small chevron that folds its kinds and filters open
            bool open = _expanded.Contains(group);
            details.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            var chevron = new Button
            {
                Style = (Style)FindResource("HeaderButton"),
                Content = open ? "\uE70E" : "\uE70D",
                ToolTip = "Kinds and filters",
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            chevron.Click += (_, _) =>
            {
                bool show = details.Visibility != Visibility.Visible;
                details.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                chevron.Content = show ? "\uE70E" : "\uE70D";
                if (show)
                {
                    _expanded.Add(group);
                }
                else
                {
                    _expanded.Remove(group);
                }
            };

            var header = new Grid();
            header.Children.Add(groupBox);
            header.Children.Add(chevron);
            LayerList.Children.Add(header);
            LayerList.Children.Add(details);
        }
    }

    /// <summary>A checkbox for one kind of marker (e.g. Orichalcum Ore) with its map icon.</summary>
    private CheckBox KindBox(WorldMap map, MapCategory kind, int count)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = kind.Icon is { } name ? LoadIcon(name) : MapView.Badge(kind.Group);
        if (icon is not null)
        {
            row.Children.Add(new System.Windows.Controls.Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) });
        }

        row.Children.Add(new TextBlock { Text = $"{kind.Label} ({count:N0})", VerticalAlignment = VerticalAlignment.Center });
        var box = Compact(new CheckBox { Content = row, IsChecked = !View.HiddenSubtypes.Contains(kind.Key) });
        box.Click += (_, _) =>
        {
            if (box.IsChecked == true)
            {
                View.HiddenSubtypes.Remove(kind.Key);
            }
            else
            {
                View.HiddenSubtypes.Add(kind.Key);
            }

            _state.Stored.MapHiddenSubtypes = View.HiddenSubtypes.ToList();
            _state.SaveSettings();
            View.InvalidateVisual();
        };
        return box;
    }

    /// <summary>Rank toggles and a level range for monsters.</summary>
    private UIElement MonsterFilters(WorldMap map)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var ranks = new WrapPanel();
        foreach (var (rank, label) in new[] { ("normal", "Normal"), ("elite", "Elite"), ("named", "Named") })
        {
            var box = Compact(new CheckBox { Content = label, IsChecked = View.MonsterRanks.Contains(rank), Margin = new Thickness(0, 0, 8, 0) });
            box.Click += (_, _) =>
            {
                if (box.IsChecked == true)
                {
                    View.MonsterRanks.Add(rank);
                }
                else
                {
                    View.MonsterRanks.Remove(rank);
                }

                _state.Stored.MapMonsterRanks = View.MonsterRanks.ToList();
                _state.SaveSettings();
                View.InvalidateVisual();
            };
            ranks.Children.Add(box);
        }

        panel.Children.Add(ranks);

        var levels = map.Markers.Where(m => m.Level is not null).Select(m => m.Level!.Value).ToList();
        if (levels.Count == 0)
        {
            return panel;
        }

        int lowest = levels.Min(), highest = levels.Max();
        var range = new TextBlock { Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"), Margin = new Thickness(0, 6, 0, 0) };
        var min = new Slider { Minimum = lowest, Maximum = highest, IsSnapToTickEnabled = true, TickFrequency = 1 };
        var max = new Slider { Minimum = lowest, Maximum = highest, IsSnapToTickEnabled = true, TickFrequency = 1 };
        min.Value = View.MinLevel > 0 ? Math.Clamp(View.MinLevel, lowest, highest) : lowest;
        max.Value = View.MaxLevel > 0 ? Math.Clamp(View.MaxLevel, lowest, highest) : highest;

        void Apply()
        {
            if (min.Value > max.Value)
            {
                max.Value = min.Value;
            }

            range.Text = $"Level {min.Value:0} – {max.Value:0}";
            View.MinLevel = min.Value <= lowest ? 0 : (int)min.Value;
            View.MaxLevel = max.Value >= highest ? 0 : (int)max.Value;
            _state.Stored.MapMinLevel = View.MinLevel;
            _state.Stored.MapMaxLevel = View.MaxLevel;
            View.InvalidateVisual();
        }

        min.ValueChanged += (_, _) => Apply();
        max.ValueChanged += (_, _) => Apply();
        min.LostMouseCapture += (_, _) => _state.SaveSettings();
        max.LostMouseCapture += (_, _) => _state.SaveSettings();
        Apply();
        panel.Children.Add(range);
        panel.Children.Add(min);
        panel.Children.Add(max);
        return panel;
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

    /// <summary>The game showed us a cube, or someone opened one: redraw (the cube finder moves the compass).</summary>
    private void OnCube(CubeSighting cube)
    {
        if (View.Map is { } map && _state.Stored.CubeFinder)
        {
            View.ExactCubes = _state.CubeLog.OnMap(map.MapId).ToList();
        }

        RefreshCubes();
        UpdateSearchArea();
    }

    /// <summary>The compass points somewhere new: show it here when it is on this map.</summary>
    private void OnCompassTarget()
    {
        if (_state.Cubes.Target is { } target && _state.Cubes.TargetMap is { } map && View.Map?.MapId == map.MapId)
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
        }

        UpdateSearchArea();
    }

    /// <summary>aion2hub's cube markers on this map: areas where cubes were reported, not positions.</summary>
    private IEnumerable<MapMarker> Areas => View.Map is { } map ? CubeFinder.Areas(map) : [];

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
            ? "Cubes the game shows you are remembered with their exact spot."
            : $"{spots.Count} spots seen here · {_state.CubeLog.OpenedCount()} opened on all maps";
        NextCube.IsEnabled = spots.Count > 0 || Areas.Any();
        RefreshCubeStats();
    }

    /// <summary>Find My Cube is opt-in: without it, no cube card, compass or exact cube positions.</summary>
    private void ApplyCubeFinder()
    {
        bool on = _state.Stored.CubeFinder;
        CubeCard.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        View.ExactCubes = on && View.Map is { } map ? _state.CubeLog.OnMap(map.MapId).ToList() : [];

        View.InvalidateVisual();
    }

    private void CubeStats_Click(object sender, RoutedEventArgs e)
    {
        CubeStatsPanel.Visibility = CubeStatsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        RefreshCubeStats();
    }

    /// <summary>A small table: per map, spots seen, opened by us, and opened by someone else first.</summary>
    private void RefreshCubeStats()
    {
        if (CubeStatsPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        var grid = CubeStatsPanel;
        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Clear();
        foreach (var width in new[] { new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        }

        var stats = _state.CubeLog.Stats();
        var rows = new List<string[]> { new[] { string.Empty, "Seen", "Opened", "Others" } };
        rows.AddRange(stats.Select(s => new[] { MapName(s.MapId), $"{s.Spots}", $"{s.Opened}", $"{s.Taken}" }));
        if (stats.Count > 1)
        {
            rows.Add(new[] { "All maps", $"{stats.Sum(s => s.Spots)}", $"{stats.Sum(s => s.Opened)}", $"{stats.Sum(s => s.Taken)}" });
        }

        if (stats.Count == 0)
        {
            rows.Add(new[] { "No cubes seen yet", string.Empty, string.Empty, string.Empty });
        }

        for (int r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int col = 0; col < 4; col++)
            {
                bool header = r == 0, total = r == rows.Count - 1 && stats.Count > 1;
                var cell = new TextBlock
                {
                    Text = rows[r][col],
                    FontSize = 11,
                    Margin = new Thickness(col == 0 ? 0 : 10, 1, 0, 1),
                    TextAlignment = col == 0 ? TextAlignment.Left : TextAlignment.Right,
                    FontWeight = total ? FontWeights.SemiBold : FontWeights.Normal,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                cell.SetResourceReference(TextBlock.ForegroundProperty, header ? "MutedBrush" : "TitleBrush");
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, col);
                grid.Children.Add(cell);
            }
        }
    }

    private static string MapName(uint mapId) =>
        BundledGameData.WorldMapKeys.Select(BundledGameData.WorldMap).FirstOrDefault(m => m?.MapId == mapId)?.Name ?? $"Map {mapId}";

    private void SkipBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SkipBox.SelectedItem is SkipChoice choice && choice.Minutes != _state.Stored.CubeSkipMinutes)
        {
            _state.Stored.CubeSkipMinutes = choice.Minutes;
            _state.SaveSettings();
        }
    }

    /// <summary>Leads to the best cube or area and opens the compass overlay.</summary>
    private void NextCube_Click(object sender, RoutedEventArgs e) => _state.Cubes.FindNext(View.Map);

    /// <summary>A search circle around the compass target while it is only a hub area.</summary>
    private void UpdateSearchArea()
    {
        View.SearchArea = _state.Cubes.Target is { Area: { } area } target && _state.Cubes.TargetMap?.MapId == View.Map?.MapId && View.Transform is { } t
            ? (new Point(area.X, area.Y), target.RadiusMeters * 100 * t.Scale)
            : null;
        View.InvalidateVisual();
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Find(FindBox.Text);
    }

    private void Find(string query)
    {
        if (View.Map is not { } map || query.Trim().Length < 2)
        {
            FindList.Visibility = Visibility.Collapsed;
            return;
        }

        // Every zone, the one shown first: "where is that monster?" without knowing the map
        string text = query.Trim();
        var lines = BundledGameData.WorldMapKeys
            .Select(BundledGameData.WorldMap)
            .OfType<WorldMap>()
            .OrderBy(m => m.MapId == map.MapId ? 0 : 1)
            .SelectMany(m => m.Markers
                .Where(x => x.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => (x.Name, x.Subtype))
                .Select(g => new FindLine(g.First(), m.Key,
                    g.Count() > 1 ? $"{g.Key.Name} ({g.Count()})" : g.Key.Name,
                    m.MapId == map.MapId ? string.Empty : m.Name)))
            .Take(60)
            .ToList();
        FindList.ItemsSource = lines;
        FindList.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FindList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FindList.SelectedItem is FindLine line)
        {
            Reveal(line.MapKey, line.Marker);
        }
    }

    /// <summary>Switches to the marker's map, makes sure no filter hides it, and selects it (also used by Timers).</summary>
    public void Reveal(string mapKey, MapMarker marker)
    {
        var line = new FindLine(marker, mapKey, marker.Name, string.Empty);
        if (View.Map?.Key != line.MapKey)
        {
            MapBox.SelectedItem = ((IEnumerable<MapChoice>)MapBox.ItemsSource).First(c => c.Key == line.MapKey);
        }

        // Make sure the marker is not filtered out
        View.Layers.Add(View.LayerOf(line.Marker));
        View.HiddenSubtypes.Remove(line.Marker.Subtype);
        if (View.Map?.GroupOf(line.Marker.Subtype) == "monster")
        {
            View.MonsterRanks.Add(line.Marker.Rank ?? "normal");
            View.MinLevel = View.MaxLevel = 0;
        }

        if (View.Map is { } shown)
        {
            BuildLayerPanel(shown);
        }

        SyncLayerBoxes();
        View.Select(line.Marker);
    }

    private void SyncLayerBoxes()
    {
        foreach (var (group, box) in _groupBoxes)
        {
            box.IsChecked = View.Layers.Contains(group);
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

    /// <summary>For render shots: a search across all maps, with the cube statistics open.</summary>
    internal void PreviewSearch(string query)
    {
        FindBox.Text = query;
        CubeStatsPanel.Visibility = Visibility.Visible;
        RefreshCubeStats();
    }

    /// <summary>For render shots: zoomed in close around a marker.</summary>
    internal void PreviewClose(string find, double zoom)
    {
        Preview("World_L_A", find);
        if (View.Map?.Markers.FirstOrDefault(m => m.Name == find) is { } marker)
        {
            View.CenterOn(marker.X, marker.Y, zoom);
        }
    }

    /// <summary>For render shots: the compass on the next cube or area.</summary>
    internal CompassWindow? OpenCompass()
    {
        _state.Cubes.FindNext(View.Map);
        return _state.Cubes.Compass;
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
