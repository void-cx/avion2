using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Avion2.Protocol;

namespace Avion2.Overlay;

/// <summary>The app's main window: start/stop the meter, browse past fights, change settings.</summary>
public partial class HomeWindow : Window
{
    private readonly AppState _state;
    private readonly HomePage _home;
    private readonly TimersPage _timers;
    private readonly ForgePage _forge;
    private readonly CharactersPage _characters;
    private readonly LegionsPage _legions;
    private MapPage? _map;
    private readonly HistoryPage _history;
    private readonly RunsPage _runs;
    private readonly SettingsPage _settings;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private string _page = "home";
    private bool _gameConnected;
    private string? _playing;

    public HomeWindow(AppState state)
    {
        _state = state;
        InitializeComponent();
        ThemeManager.Attach(this);
        ThemeManager.Changed += UpdateBackdrop;
        _home = new HomePage(state, row => ShowPage("history", row));
        _timers = new TimersPage(state, ShowOnMap, row => ShowPage("history", row));
        _forge = new ForgePage(state);
        _characters = new CharactersPage(state);
        _legions = new LegionsPage(state);
        _history = new HistoryPage(state);
        _runs = new RunsPage(state, run =>
        {
            ShowPage("history");
            _history.ShowRange(run.Run.StartMs, run.Run.EndMs, $"{run.Title} ({run.When})");
        });
        _settings = new SettingsPage(state);

        state.ShowPageRequested += page => ShowPage(page);
        state.ShowCharacterRequested += (name, server) =>
        {
            ShowPage("characters");
            _characters.Show(name, server);
        };
        state.ProfileOpened += (name, server) =>
        {
            // the game has the focus: switch the page, but leave the window where it is
            ShowPage("characters", front: false);
            _characters.Show(name, server);
        };
        state.BackdropChanged += () =>
        {
            _playing = null; // force a reload of the (possibly new) file
            UpdateBackdrop();
        };
        StateChanged += (_, _) => UpdateBackdrop();
        Closing += (_, _) =>
        {
            state.StopMeter();
            state.SaveSettings();
        };
        _timer.Tick += (_, _) => UpdateNavStatus();
        state.ViewedCharacterChanged += UpdateCharacterPick;
        StreamerMode.Changed += UpdateCharacterPick;
        state.PortraitsChanged += UpdateCharacterPick;
        Loaded += (_, _) =>
        {
            UpdateCharacterPick();
            UpdateNavStatus();
            UpdateBackdrop();
            _timer.Start();
        };

        NavLinks.First().IsChecked = true;
    }

    /// <summary>The rail's character: whose data the pages show (accented while it is not the one in game).</summary>
    private void UpdateCharacterPick()
    {
        var characters = _state.OurCharacters;
        CharacterPick.Visibility = characters.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_state.ViewedCharacter is not { } name)
        {
            return;
        }

        CharacterPick.ApplyTemplate();

        var known = characters.FirstOrDefault(c => c.Name == name);
        var portrait = known is null ? null : Portrait(known);
        if (CharacterPick.Template.FindName("PickIcon", CharacterPick) is Image icon)
        {
            icon.Source = ClassIcons.For(known?.Class ?? GameClass.Unknown);
            icon.Visibility = portrait is null ? Visibility.Visible : Visibility.Collapsed;
        }

        if (CharacterPick.Template.FindName("PickPortrait", CharacterPick) is Ellipse round)
        {
            round.Fill = portrait is null ? null : new ImageBrush(portrait) { Stretch = Stretch.UniformToFill };
            round.Visibility = portrait is null ? Visibility.Collapsed : Visibility.Visible;
        }

        if (CharacterPick.Template.FindName("PickName", CharacterPick) is TextBlock label)
        {
            label.Text = StreamerMode.Name(name, self: !_state.ViewsOther);
        }

        CharacterPick.Tag = _state.ViewsOther ? "other" : null;
        CharacterPick.ToolTip = _state.ViewsOther
            ? $"Showing {StreamerMode.Name(name)}'s gear, fights, runs and legion; {StreamerMode.Name(_state.PlayingCharacter ?? "?", self: true)} is in game. Click to switch."
            : $"Showing the gear, fights, runs and legion of {StreamerMode.Name(name, self: true)}. Click to see another character's.";
    }

    private void CharacterPick_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = CharacterPick, Placement = System.Windows.Controls.Primitives.PlacementMode.Right };
        menu.Items.Add(new MenuItem { Header = "Show the data of", IsEnabled = false });
        foreach (var character in _state.OurCharacters)
        {
            bool playing = character.Name == _state.PlayingCharacter;
            var item = new MenuItem
            {
                Header = StreamerMode.Name(character.Name, self: playing) + (playing ? "  ·  in game" : string.Empty),
                IsCheckable = true,
                IsChecked = character.Name == _state.ViewedCharacter,
                Icon = Portrait(character) is { } face
                    ? new Ellipse { Width = 20, Height = 20, Fill = new ImageBrush(face) { Stretch = Stretch.UniformToFill } }
                    : new Image { Source = ClassIcons.For(character.Class), Width = 18, Height = 18 },
            };
            string name = character.Name;
            item.Click += (_, _) => _state.View(name);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    /// <summary>A character's portrait from the official site as saved on this PC; null in streamer mode.</summary>
    private ImageSource? Portrait(Avion2.Analysis.KnownCharacter character) => _state.PortraitOf(character.Name, character.ServerId);

    /// <summary>The rail's page buttons (Settings sits at the bottom of the rail).</summary>
    private IEnumerable<RadioButton> NavLinks => Rail.Children.OfType<RadioButton>()
        .Concat(NavStack.Children.OfType<RadioButton>())
        .OrderBy(b => (string)b.Tag == "settings" ? 1 : 0);

    /// <summary>
    /// Fits the page buttons into the rail: full height (64) when there is room, lower (down to 48, which still shows
    /// the icon and the label) in a small window; below that the buttons scroll.
    /// </summary>
    private void NavScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var buttons = NavStack.Children.OfType<RadioButton>().ToList();
        if (buttons.Count == 0 || !e.HeightChanged)
        {
            return;
        }

        double height = Math.Clamp(Math.Floor(NavScroll.ActualHeight / buttons.Count), 48, 64);
        foreach (var button in buttons)
        {
            button.Height = height;
        }
    }

    /// <summary>Shows a page and brings the window to the front (used by the overlay's buttons), unless <paramref name="front"/> is false.</summary>
    /// <param name="front">Show, restore and activate the window; false only switches the page.</param>
    public void ShowPage(string page, HistoryRow? select = null, bool front = true)
    {
        foreach (var link in NavLinks)
        {
            if ((string)link.Tag == page)
            {
                link.IsChecked = true;
            }
        }

        if (select is not null)
        {
            _history.Select(select);
        }

        if (!front)
        {
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    /// <summary>Opens the map at a marker (Timers: "Show on map").</summary>
    private void ShowOnMap(string mapKey, Avion2.Analysis.MapMarker marker)
    {
        ShowPage("map");
        _map?.Reveal(mapKey, marker);
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } button)
        {
            return;
        }

        button.BringIntoView(); // in a small window the page's button may be scrolled away
        Page.Content = tag switch
        {
            "timers" => _timers,
            "forge" => _forge,
            "characters" => _characters,
            "legions" => _legions,
            "map" => _map ??= new MapPage(_state), // created on first visit: it loads the map pictures
            "history" => _history,
            "runs" => _runs,
            "settings" => _settings,
            _ => _home,
        };

        // The key art is the home page's banner; other pages are plain.
        Backdrop.Visibility = tag == "home" ? Visibility.Visible : Visibility.Collapsed;
        _page = tag;
        UpdateBackdrop();
    }

    /// <summary>
    /// The video only plays when it is actually worth the GPU/CPU: on the home page, window visible, and the
    /// game neither running on this PC nor connected. Otherwise it is unloaded (decoder freed) and the drawn
    /// backdrop shows instead.
    /// </summary>
    private void UpdateBackdrop()
    {
        string? path = _state.BackgroundVideo;
        bool wanted = ThemeManager.Current.VideoBackdrop && path is not null && File.Exists(path) && !_gameConnected &&
                      _page == "home" && WindowState != WindowState.Minimized && IsVisible;

        if (!wanted)
        {
            if (_playing is not null)
            {
                BackdropVideo.Stop();
                BackdropVideo.Close();
                BackdropVideo.Source = null;
                BackdropVideo.Visibility = Visibility.Collapsed;
                _playing = null;
            }

            return;
        }

        if (_playing == path)
        {
            return;
        }

        BackdropVideo.Visibility = Visibility.Visible;
        BackdropVideo.Source = new Uri(path!, UriKind.Absolute);
        BackdropVideo.Play();
        _playing = path;
    }

    private void BackdropVideo_MediaEnded(object sender, RoutedEventArgs e)
    {
        BackdropVideo.Position = TimeSpan.Zero;
        BackdropVideo.Play();
    }

    private void BackdropVideo_MediaFailed(object? sender, ExceptionRoutedEventArgs e) =>
        BackdropVideo.Visibility = Visibility.Collapsed;

    private void UpdateNavStatus()
    {
        // The game running here is enough (no need to wait for the meter to find its connection); the lock still
        // counts for a game on another PC whose traffic we see.
        bool connected = _state.Engine?.Status().Locked == true || Avion2.Engine.GameProcess.IsRunning;
        if (connected != _gameConnected)
        {
            _gameConnected = connected;
            UpdateBackdrop();
        }

        if (_state.Engine is not { } engine)
        {
            SetStatus("Capture stopped", "IdleBrush");
            return;
        }

        var status = engine.Status();
        if (!status.Locked)
        {
            SetStatus("Searching for AION2…", "WarnBrush");
            return;
        }

        StreamerMode.Names.Self = status.SelfName ?? StreamerMode.Names.Self;
        string who = status.SelfName is { } self ? StreamerMode.Name(self, self: true)
            : status.SelfClass != GameClass.Unknown ? status.SelfClass.ToString() : "connected";
        if (status.SelfName is not null && status.SelfServer is { } server && Avion2.Engine.BundledGameData.Server(server) is { } info)
        {
            who = $"{who} · {info.Name}";
        }
        string where = engine.LiveMap() is { MapId: { } map } ? _state.Data.MapName(map) : string.Empty;
        SetStatus(status.InCombat ? $"{who} · in combat" : where.Length > 0 ? $"{who} · {where}" : who, "LiveBrush");
    }

    private void SetStatus(string text, string dotKey)
    {
        NavDot.SetResourceReference(Shape.FillProperty, dotKey);
        ShellStatus.Current.Update(text, dotKey);
    }

    internal UserControl CurrentPage => (UserControl)Page.Content;
}
