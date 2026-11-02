using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <summary>
/// Characters as the official AION2 site shows them: ours on the left (every character AVION2 has seen us play),
/// anyone else by name and server. Looked up only when opened, kept for 10 minutes.
/// </summary>
public partial class CharactersPage : UserControl
{
    private readonly AppState _state;
    private CharacterSheet? _sheet;
    private string _tab = "equipment";
    private int _board;

    /// <summary>The sheet shows one of ours beside this character instead of the tabs.</summary>
    private bool _comparing;

    /// <summary>The character being loaded or shown; a late answer for another one is dropped.</summary>
    private (string Name, ushort Server)? _current;

    public CharactersPage(AppState state)
    {
        _state = state;
        InitializeComponent();
        ServerBox.ItemsSource = BundledGameData.Servers
            .Where(s => ArmoryClient.RegionCode(s.Key) is not null)
            .OrderBy(s => s.Value.Region).ThenBy(s => s.Value.Name)
            .Select(s => new ServerChoice(s.Key, $"{s.Value.Name} · {s.Value.Region}"))
            .ToList();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                LoadMine();
            }
        };
        ShowMessage("Pick a character on the left, or look one up by name.");
        StreamerMode.Changed += () =>
        {
            foreach (var list in new[] { Mine, Results })
            {
                foreach (var row in list.ItemsSource as IEnumerable<CharacterRow> ?? [])
                {
                    row.Restate();
                }
            }

            if (_sheet is { } sheet)
            {
                DataContext = null;
                DataContext = sheet;
                ShowTab();
            }
        };
    }

    private sealed record ServerChoice(ushort Id, string Label);

    /// <summary>Shows a character (from the meter's right-click, or the lists).</summary>
    public void Show(string name, ushort serverId) => _ = LoadAsync(name, serverId, refresh: false);

    private void LoadMine()
    {
        var characters = new CharacterStore().Characters;
        var selected = (Mine.SelectedItem as CharacterRow)?.Name;
        var rows = characters.Select(c => new CharacterRow(c.Name, c.ServerId, c.Class, Detail(c.Class.ToString(), c.ServerId, c.Name))).ToList();
        Mine.SelectionChanged -= List_SelectionChanged;
        Mine.ItemsSource = rows;
        Mine.SelectedItem = rows.FirstOrDefault(r => r.Name == selected);
        Mine.SelectionChanged += List_SelectionChanged;
        NoneYet.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (ServerBox.SelectedItem is null && characters.Count > 0)
        {
            ServerBox.SelectedItem = ((IEnumerable<ServerChoice>)ServerBox.ItemsSource).FirstOrDefault(s => s.Id == characters[0].ServerId);
        }

        if (_current is null && rows.Count > 0)
        {
            Mine.SelectedIndex = 0; // the character played last
        }

        _ = FillSummariesAsync(rows);
    }

    /// <summary>Our characters' combat power and item level in the list: one lookup each, cached.</summary>
    private async Task FillSummariesAsync(IReadOnlyList<CharacterRow> rows)
    {
        foreach (var row in rows)
        {
            if ((await _state.Armory.LoadAsync(row.Name, row.ServerId)).Profile is { } profile)
            {
                row.Detail = $"{new CharacterSheet(profile).Summary} · {ServerName(row.ServerId)}";
            }
        }
    }

    /// <summary>"Chanter · Gauss", or the cached combat power and item level.</summary>
    private string Detail(string className, ushort serverId, string name) =>
        _state.Armory.Cached(name, serverId) is { } profile
            ? $"{new CharacterSheet(profile).Summary} · {ServerName(serverId)}"
            : $"{className} · {ServerName(serverId)}";

    private static string ServerName(ushort id) => BundledGameData.Server(id)?.Name ?? id.ToString(CultureInfo.InvariantCulture);

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: CharacterRow row } list)
        {
            (list == Mine ? Results : Mine).SelectedItem = null;
            _ = LoadAsync(row.Name, row.ServerId, refresh: false);
        }
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = SearchAsync();
        }
    }

    private void Search_Click(object sender, RoutedEventArgs e) => _ = SearchAsync();

    private async Task SearchAsync()
    {
        string name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            return;
        }

        if (ServerBox.SelectedItem is not ServerChoice server)
        {
            ShowMessage("Pick the player's server first.");
            return;
        }

        ResultsPanel.Visibility = Visibility.Visible;
        ResultsLabel.Text = "SEARCHING…";
        IReadOnlyList<ArmoryHit> hits;
        try
        {
            hits = await _state.Armory.SearchAsync(name, server.Id);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            ResultsLabel.Text = "THE OFFICIAL SITE DID NOT ANSWER";
            Results.ItemsSource = null;
            return;
        }

        ResultsLabel.Text = hits.Count == 0 ? $"NO ONE NAMED {StreamerMode.Name(name).ToUpperInvariant()} ON {server.Label.Split(' ')[0].ToUpperInvariant()}" : "RESULTS";
        var rows = hits.Select(h => new CharacterRow(h.Name, h.ServerId, Protocol.GameClass.Unknown,
            $"Level {h.Level} · {(h.ServerName.Length > 0 ? h.ServerName : ServerName(h.ServerId))}")).ToList();
        Results.ItemsSource = rows;
        if (rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) is { } exact)
        {
            Results.SelectedItem = exact;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_sheet is { } sheet)
        {
            _ = LoadAsync(sheet.Profile.Name, sheet.Profile.ServerId, refresh: true);
        }
    }

    private async Task LoadAsync(string name, ushort serverId, bool refresh)
    {
        _current = (name, serverId);
        if (_sheet is null || !string.Equals(_sheet.Name, name, StringComparison.OrdinalIgnoreCase) || _sheet.Profile.ServerId != serverId)
        {
            ShowMessage($"Loading {StreamerMode.Name(name)}…");
        }
        else
        {
            Updated.Text = "Updating…";
        }

        var lookup = await _state.Armory.LoadAsync(name, serverId, refresh);
        if (_current != (name, serverId))
        {
            return;
        }

        if (lookup.Profile is not { } profile)
        {
            ShowMessage(lookup.Problem?.Replace(name, StreamerMode.Name(name), StringComparison.Ordinal) ?? $"No character named {StreamerMode.Name(name)}.");
            return;
        }

        bool same = _sheet?.Profile.CharacterId == profile.CharacterId;
        _sheet = new CharacterSheet(profile);
        DataContext = _sheet;
        Message.Visibility = Visibility.Collapsed;
        Sheet.Visibility = Visibility.Visible;
        UpdateAge();
        if (!same)
        {
            _board = profile.Daevanion.FirstOrDefault()?.Id ?? 0;
        }

        FillCompareWith(profile);
        ShowTab();
        if (Mine.ItemsSource is IEnumerable<CharacterRow> mine && mine.FirstOrDefault(r => r.Name == profile.Name && r.ServerId == profile.ServerId) is { } row)
        {
            row.Detail = $"{_sheet.Summary} · {ServerName(row.ServerId)}";
        }
    }

    /// <summary>Our characters to compare with: all but the one shown, the one played last first.</summary>
    private void FillCompareWith(ArmoryProfile shown)
    {
        var picked = CompareWith.SelectedItem as CharacterRow;
        var rows = (Mine.ItemsSource as IEnumerable<CharacterRow> ?? [])
            .Where(r => !(string.Equals(r.Name, shown.Name, StringComparison.OrdinalIgnoreCase) && r.ServerId == shown.ServerId))
            .ToList();
        CompareWith.SelectionChanged -= CompareWith_SelectionChanged;
        CompareWith.ItemsSource = rows;
        CompareWith.SelectedItem = rows.FirstOrDefault(r => r.Name == picked?.Name && r.ServerId == picked?.ServerId) ?? rows.FirstOrDefault();
        CompareWith.SelectionChanged += CompareWith_SelectionChanged;
        CompareButton.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (rows.Count == 0)
        {
            _comparing = false;
        }
    }

    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        _comparing = !_comparing;
        ShowTab();
    }

    private void CompareWith_SelectionChanged(object sender, SelectionChangedEventArgs e) => _ = CompareAsync();

    private async Task CompareAsync()
    {
        if (_sheet is not { } sheet || CompareWith.SelectedItem is not CharacterRow row)
        {
            return;
        }

        CompareTheirs.Text = sheet.Shown;
        CompareMessage.Text = $"Loading {row.Shown}…";
        CompareSections.ItemsSource = null;
        var lookup = await _state.Armory.LoadAsync(row.Name, row.ServerId);
        if (_sheet != sheet || CompareWith.SelectedItem != row || !_comparing)
        {
            return;
        }

        if (lookup.Profile is not { } mine)
        {
            CompareMessage.Text = lookup.Problem?.Replace(row.Name, row.Shown, StringComparison.Ordinal) ?? $"No character named {row.Shown}.";
            return;
        }

        CompareMessage.Text = string.Empty;
        CompareSections.ItemsSource = CharacterSheet.Compare(CharacterComparison.Build(mine, sheet.Profile, BundledGameData.GearStats));
        CompareScroll.ScrollToTop();
    }

    private void UpdateAge()
    {
        if (_sheet is null)
        {
            return;
        }

        double minutes = (DateTimeOffset.UtcNow - _sheet.Profile.FetchedAt).TotalMinutes;
        Updated.Text = minutes < 1 ? "Updated just now" : $"Updated {minutes:0} min ago";
    }

    private void ShowMessage(string text)
    {
        Message.Text = text;
        Message.Visibility = Visibility.Visible;
        Sheet.Visibility = Visibility.Collapsed;
    }

    /// <summary>Opens a tab of the sheet (for the developer screenshots).</summary>
    internal void SelectTab(string tab)
    {
        foreach (var button in Tabs.Children.OfType<RadioButton>())
        {
            button.IsChecked = (string)button.Tag == tab;
        }
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tab })
        {
            _tab = tab;
            if (IsLoaded)
            {
                ShowTab();
            }
        }
    }

    private void ShowTab()
    {
        if (_sheet is not { } sheet)
        {
            return;
        }

        CompareButton.Content = _comparing ? "Back to the sheet" : "Compare";
        Tabs.Visibility = _comparing ? Visibility.Collapsed : Visibility.Visible;
        CompareTab.Visibility = _comparing ? Visibility.Visible : Visibility.Collapsed;
        CardsTab.Visibility = !_comparing && _tab is "equipment" or "skills" or "cosmetics" ? Visibility.Visible : Visibility.Collapsed;
        StatsTab.Visibility = !_comparing && _tab == "stats" ? Visibility.Visible : Visibility.Collapsed;
        DaevanionTab.Visibility = !_comparing && _tab == "daevanion" ? Visibility.Visible : Visibility.Collapsed;
        if (_comparing)
        {
            _ = CompareAsync();
            return;
        }

        Groups.ItemsSource = _tab switch
        {
            "skills" => sheet.Skills,
            "cosmetics" => sheet.Cosmetics,
            _ => sheet.Equipment,
        };
        CardsTab.ScrollToTop();
        if (_tab == "daevanion")
        {
            Boards.ItemsSource = sheet.Boards(_board);
            _ = ShowBoardAsync(sheet, _board);
        }
    }

    private void Board_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: int id } && _sheet is { } sheet && id != _board)
        {
            _board = id;
            _ = ShowBoardAsync(sheet, id);
        }
    }

    /// <summary>Draws a board as the game lays it out: 15 × 15 cells, open nodes lit, closed ones dim.</summary>
    private async Task ShowBoardAsync(CharacterSheet sheet, int boardId)
    {
        BoardGrid.Children.Clear();
        Effects.Children.Clear();
        EffectsTitle.Text = string.Empty;
        BoardMessage.Text = "Loading the board…";
        var board = await _state.Armory.LoadBoardAsync(sheet.Profile, boardId);
        if (_sheet != sheet || _board != boardId)
        {
            return;
        }

        if (board is null)
        {
            BoardMessage.Text = "The official site did not answer.";
            return;
        }

        BoardMessage.Text = board.Nodes.Count == 0 ? "This board is not open yet." : string.Empty;
        var cells = board.Nodes.ToDictionary(n => (n.Row, n.Column));
        for (int row = 1; row <= 15; row++)
        {
            for (int column = 1; column <= 15; column++)
            {
                BoardGrid.Children.Add(cells.TryGetValue((row, column), out var node) ? Node(node, sheet) : new Border());
            }
        }

        int open = board.SkillEffects.Count + board.StatEffects.Count;
        EffectsTitle.Text = $"Active effects ({open})";
        AddEffects("SKILL UPGRADES", board.SkillEffects, ItemGrades.Brush("Legend"));
        AddEffects("STAT UPGRADES", board.StatEffects, (Brush)FindResource("SecondaryTextBrush"));
    }

    /// <summary>A node: its skill's icon for skill upgrades ("Skill Level Up - Onslaught"), else a square in its grade color.</summary>
    private static UIElement Node(ArmoryNode node, CharacterSheet sheet)
    {
        var color = node.Type == "Start" ? (Brush)Application.Current.FindResource("AccentBrush") : ItemGrades.Brush(node.Grade);
        string skillName = node.Name.Split(" - ", 2) is [_, var name] ? name : node.Name;
        var icon = node.Type == "SkillLevel"
            ? CharacterSheet.WebImage(sheet.Profile.Skills.FirstOrDefault(s => s.Name == skillName)?.Icon, 60)
            : null;
        return new Border
        {
            Margin = new Thickness(3),
            CornerRadius = new CornerRadius(4),
            Background = icon is null ? color : Brushes.Transparent,
            BorderBrush = icon is null ? Brushes.Transparent : color,
            BorderThickness = new Thickness(icon is null ? 0 : 1.5),
            Child = icon is null ? null : new Image { Source = icon, Stretch = Stretch.UniformToFill },
            Opacity = node.Open ? 1 : 0.25,
            ToolTip = $"{(node.Name.Length > 0 ? node.Name : node.Type)}{(node.Open ? string.Empty : " (not open)")}\n{string.Join("\n", node.Effects)}".TrimEnd(),
        };
    }

    private void AddEffects(string label, IReadOnlyList<string> effects, Brush brush)
    {
        if (effects.Count == 0)
        {
            return;
        }

        Effects.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("SectionLabel"), Margin = new Thickness(0, 6, 0, 6) });
        foreach (string effect in effects)
        {
            Effects.Children.Add(new TextBlock { Text = effect, Foreground = brush, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap });
        }
    }
}
