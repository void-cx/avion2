using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Avion2.Analysis;
using Avion2.Engine;
using Avion2.Protocol;

namespace Avion2.Overlay;

/// <summary>A legion on the page: a podium card or a row of the list.</summary>
public sealed class LegionRowView(LegionBoardRow row, LegionOrder order, long nowMs, bool podium) : INotifyPropertyChanged
{
    public LegionBoardRow Row { get; } = row;
    public int Rank => Row.Rank;
    public string Name => Row.Name;
    public bool Own => Row.Own;
    public double Share => Row.Share;
    public string MembersText => LegionBoard.Number(Row.Members);
    public string Power => Row.MembersWithPower > 0 ? LegionBoard.Number(Row.CombatPower) : "–";
    public string LastSeen => $"Seen {LegionBoard.Ago(Row.LastSeenMs, nowMs)}";

    /// <summary>The first place stands higher than the second and third.</summary>
    public Thickness PodiumMargin => podium && Row.Rank != 1 ? new Thickness(6, 22, 6, 0) : new Thickness(6, 0, 6, 0);

    public string Tip
    {
        get
        {
            string power = Row.MembersWithPower == Row.Seen
                ? "combat power of all of them known"
                : $"combat power known for {Row.MembersWithPower}";
            string ranked = order == LegionOrder.Members ? "ranked by members" : "ranked by their combat power, added up";
            string members = Row.Listed is { } listed
                ? $"{listed} members on the game's legion list, {Row.Seen} of them seen in the last 30 days"
                : $"{Row.Seen} members seen in the last 30 days";
            return $"{members} ({power}); {ranked}";
        }
    }

    private bool _selected;

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected != value)
            {
                _selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A member of the selected legion.</summary>
public sealed record LegionMemberView(LegionBoardMember Member, long NowMs)
{
    public string Name => Member.Name;
    public bool Self => Member.Self;
    public ImageSource? Icon => ClassIcons.For(Member.Class);
    public string ItemLevel => Member.ItemLevel is { } level ? LegionBoard.Number(level) : "–";
    public string Power => Member.CombatPower is { } power ? LegionBoard.Number(power) : "–";

    /// <summary>A small mark before a combat power from the official site.</summary>
    public string SiteMark => Member.FromSite && Member.CombatPower is not null ? "◆ " : string.Empty;

    public string Seen => LegionBoard.Ago(Member.LastSeenMs, NowMs);

    public string Tip =>
        (Member.Class == GameClass.Unknown ? "Class not seen" : Member.Class.ToString()) +
        (Member.FromSite && Member.CombatPower is not null
            ? " · combat power and item level from the official AION2 site (a character lookup)"
            : " · as the game showed them");
}

/// <summary>
/// Legions compared: the legions AVION2 has seen on a server, ranked by the combat power of the members seen or by
/// the members seen, the top three on a podium; the selected legion's members on the right.
/// </summary>
public partial class LegionsPage : UserControl
{
    private readonly AppState _state;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(3) };
    private ushort? _server;
    private string? _selected;
    private LegionOrder _order = LegionOrder.CombatPower;
    private bool _dirty = true;
    private List<LegionRowView> _rows = new();

    public LegionsPage(AppState state)
    {
        _state = state;
        InitializeComponent();
        state.LegionsSeenUpdated += () => _dirty = true;
        state.LegionUpdated += () => _dirty = true;
        state.ViewedCharacterChanged += () =>
        {
            _server = null; // the viewed character's server
            RefreshIfShown();
        };
        StreamerMode.Changed += RefreshIfShown;
        _refresh.Tick += (_, _) =>
        {
            if (_dirty)
            {
                Refresh();
            }
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Refresh();
                _refresh.Start();
            }
            else
            {
                _refresh.Stop();
            }
        };
    }

    /// <summary>Developer shots: selects the first legion.</summary>
    public void SelectFirst()
    {
        if (_rows.FirstOrDefault() is { } first)
        {
            Select(first.Row.Key);
        }
    }

    /// <summary>Developer shots: switches the order.</summary>
    public void ShowOrder(LegionOrder order)
    {
        (order == LegionOrder.Members ? ByMembers : ByPower).IsChecked = true;
    }

    private void RefreshIfShown()
    {
        if (IsVisible)
        {
            Refresh();
        }
        else
        {
            _dirty = true;
        }
    }

    private static string ServerName(ushort id) => BundledGameData.Server(id)?.Name ?? id.ToString(CultureInfo.InvariantCulture);

    private void Refresh()
    {
        _dirty = false;
        var book = _state.LegionsSeen;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (ownServer, ownLegion) = _state.OwnLegion();
        var servers = book.Servers.ToList();
        if (ownServer is { } mine && !servers.Contains(mine))
        {
            servers.Insert(0, mine);
        }

        _server ??= ownServer ?? (servers.Count > 0 ? servers[0] : null);
        if (_server is { } current && !servers.Contains(current))
        {
            servers.Add(current);
        }

        FillServers(book, servers, ownServer);

        if (_server is not { } server)
        {
            ShowNothing("No legions seen yet. Start the meter and play: AVION2 notes the legion of every player the game shows you, " +
                        "and of the characters you look up.");
            return;
        }

        var rows = LegionBoard.Rows(book, server, _order, server == ownServer ? ownLegion : null, StreamerMode.Names, now);
        int members = rows.Sum(r => r.Members);
        Hint.Text = $"The legions AVION2 has seen on {ServerName(server)}: the members seen in game (or looked up) in the last 30 days, " +
                    "and their combat power as the game showed it; a legion's size from the game's legion list when you opened it. " +
                    "Not the official ranking.";
        Shown.Text = rows.Count == 0 ? string.Empty : $"{rows.Count} legions · {LegionBoard.Number(members)} members";
        if (rows.Count == 0)
        {
            ShowNothing($"No legions seen on {ServerName(server)} yet.");
            return;
        }

        Nothing.Visibility = Visibility.Collapsed;
        Scroller.Visibility = Visibility.Visible;
        var top = rows.Take(3).Select(r => new LegionRowView(r, _order, now, podium: true)).ToList();
        var rest = rows.Skip(3).Select(r => new LegionRowView(r, _order, now, podium: false)).ToList();
        _rows = top.Concat(rest).ToList();
        // second, first, third
        Podium.ItemsSource = top.Count switch
        {
            3 => new[] { top[1], top[0], top[2] },
            2 => new[] { top[1], top[0] },
            _ => top,
        };
        Ranked.ItemsSource = rest;
        ListHeader.Visibility = rest.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        int fromSite = rows.Sum(r => r.FromSite);
        Footnote.Text = "Members: the legion's size on the game's legion list (in game: Legion, then the list or a search), else the " +
                        "members seen: the players around you whose legion the game showed, your party, and the characters you looked up " +
                        "(the group finder adds combat power and item level to players already known). " +
                        "Members not seen and listings not renewed for 30 days drop out. A legion's combat power adds up the combat power known of its members seen, " +
                        "so a legion you meet often ranks higher than one you seldom see." +
                        (fromSite > 0 ? $" ◆ marks the combat power of {fromSite} members, taken from the official AION2 site." : string.Empty);

        if (_selected is { } key && _rows.All(r => r.Row.Key != key))
        {
            _selected = null;
        }

        _selected ??= rows.FirstOrDefault(r => r.Own)?.Key;
        ShowSelected(book, server, now);
    }

    private void FillServers(LegionsSeenBook book, IReadOnlyList<ushort> servers, ushort? ownServer)
    {
        ServerList.Children.Clear();
        foreach (ushort id in servers)
        {
            var button = new RadioButton
            {
                Style = (Style)FindResource("SideItem"),
                GroupName = "LegionServer",
                Content = ServerName(id),
                Tag = id,
                IsChecked = id == _server,
                ToolTip = id == ownServer ? "Your server" : BundledGameData.Server(id)?.Region,
            };
            Ui.SetIcon(button, (Geometry)FindResource(id == ownServer ? "IconCharacter" : "IconLegion"));
            Ui.SetCount(button, book.On(id).Count.ToString(CultureInfo.InvariantCulture));
            button.Checked += Server_Checked;
            ServerList.Children.Add(button);
        }

        NoServers.Visibility = servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowNothing(string text)
    {
        Nothing.Text = text;
        Nothing.Visibility = Visibility.Visible;
        Scroller.Visibility = Visibility.Collapsed;
        Podium.ItemsSource = null;
        Ranked.ItemsSource = null;
        _rows = new();
        _selected = null;
        Pane.Visibility = Visibility.Collapsed;
        Empty.Visibility = Visibility.Visible;
    }

    private void Select(string key)
    {
        _selected = key;
        if (_server is { } server)
        {
            ShowSelected(_state.LegionsSeen, server, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    private void ShowSelected(LegionsSeenBook book, ushort server, long now)
    {
        foreach (var row in _rows)
        {
            row.Selected = row.Row.Key == _selected;
        }

        if (_rows.FirstOrDefault(r => r.Selected)?.Row is not { } legion)
        {
            Pane.Visibility = Visibility.Collapsed;
            Empty.Visibility = Visibility.Visible;
            return;
        }

        Empty.Visibility = Visibility.Collapsed;
        Pane.Visibility = Visibility.Visible;
        PaneName.Text = legion.Name;
        PaneRank.Text = $"#{legion.Rank} by {(_order == LegionOrder.Members ? "members" : "combat power")}";
        PaneOwn.Visibility = legion.Own ? Visibility.Visible : Visibility.Collapsed;
        PaneServer.Text = ServerName(server);
        PaneMembers.Text = legion.Listed is { } listed
            ? $"{LegionBoard.Number(listed)} ({LegionBoard.Number(legion.Seen)} seen)"
            : LegionBoard.Number(legion.Members);
        PaneLeader.Text = legion.Leader is { } leader ? $"Leader: {leader}" : string.Empty;
        PaneLeader.Visibility = legion.Leader is null ? Visibility.Collapsed : Visibility.Visible;
        PanePower.Text = legion.MembersWithPower > 0 ? LegionBoard.Number(legion.CombatPower) : "–";
        PaneAverage.Text = legion.AverageCombatPower is { } average ? LegionBoard.Number(average) : "–";
        PaneSeen.Text = LegionBoard.Ago(legion.LastSeenMs, now);
        string known = legion.Seen == 0 ? "No member seen yet: the size is the game's legion list's."
            : legion.MembersWithPower == legion.Seen
            ? "The combat power of every member seen is known."
            : $"The combat power of {legion.MembersWithPower} of the {legion.Seen} members seen is known.";
        PaneNote.Text = legion.FromSite > 0 ? $"{known} ◆ {legion.FromSite} from the official AION2 site." : known;
        MemberList.ItemsSource = LegionBoard.Members(book, server, legion.Key, StreamerMode.Names, now)
            .Select(m => new LegionMemberView(m, now)).ToList();
    }

    private void Server_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: ushort id } && id != _server)
        {
            _server = id;
            _selected = null;
            Dispatcher.BeginInvoke(Refresh); // not while the list of servers is being built
        }
    }

    private void Order_Checked(object sender, RoutedEventArgs e)
    {
        if (Shown is null)
        {
            return; // the first one checked while the page is built
        }

        _order = sender == ByMembers ? LegionOrder.Members : LegionOrder.CombatPower;
        Refresh();
    }

    private void Legion_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LegionRowView row })
        {
            Select(row.Row.Key);
        }
    }
}
