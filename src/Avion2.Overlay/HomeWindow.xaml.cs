using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private MapPage? _map;
    private readonly HistoryPage _history;
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
        _history = new HistoryPage(state);
        _settings = new SettingsPage(state);

        state.ShowPageRequested += page => ShowPage(page);
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
        Loaded += (_, _) =>
        {
            UpdateNavStatus();
            UpdateBackdrop();
            _timer.Start();
        };

        NavLinks.First().IsChecked = true;
    }

    /// <summary>The rail's page buttons (Settings sits at the bottom of the rail).</summary>
    private IEnumerable<RadioButton> NavLinks => Rail.Children.OfType<RadioButton>()
        .Concat(Rail.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<RadioButton>()))
        .OrderBy(b => (string)b.Tag == "settings" ? 1 : 0);

    /// <summary>Shows a page and brings the window to the front (used by the overlay's buttons).</summary>
    public void ShowPage(string page, HistoryRow? select = null)
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
        if (sender is not RadioButton { Tag: string tag })
        {
            return;
        }

        Page.Content = tag switch
        {
            "timers" => _timers,
            "forge" => _forge,
            "map" => _map ??= new MapPage(_state), // created on first visit: it loads the map pictures
            "history" => _history,
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
            SetStatus("Meter stopped", "IdleBrush");
            return;
        }

        var status = engine.Status();
        if (!status.Locked)
        {
            SetStatus("Searching for AION2…", "WarnBrush");
            return;
        }

        string who = status.SelfName ?? (status.SelfClass != GameClass.Unknown ? status.SelfClass.ToString() : "connected");
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
