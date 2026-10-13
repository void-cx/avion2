using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Avion2.Protocol;

namespace Avion2.Overlay;

/// <summary>The app's main window: start/stop the meter, browse past fights, change settings.</summary>
public partial class HomeWindow : Window
{
    private static readonly Brush Idle = Frozen(0x6B, 0x72, 0x80);
    private static readonly Brush Searching = Frozen(0xF5, 0x9E, 0x0B);
    private static readonly Brush Connected = Frozen(0x4A, 0xDE, 0x80);

    private readonly AppState _state;
    private readonly HomePage _home;
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
        AionTheme.Apply(this);
        _home = new HomePage(state, row => ShowPage("history", row));
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

        ((RadioButton)NavLinks.Children[0]).IsChecked = true;
    }

    /// <summary>Shows a page and brings the window to the front (used by the overlay's buttons).</summary>
    public void ShowPage(string page, HistoryRow? select = null)
    {
        foreach (RadioButton link in NavLinks.Children)
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

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag })
        {
            return;
        }

        Page.Content = tag switch
        {
            "history" => _history,
            "settings" => _settings,
            _ => _home,
        };

        // The key art belongs to the home page; inner pages get an armory-style dark.
        Dim.BeginAnimation(OpacityProperty, new DoubleAnimation(tag == "home" ? 0 : 0.86, TimeSpan.FromMilliseconds(250)));
        _page = tag;
        UpdateBackdrop();
    }

    /// <summary>
    /// The video only plays when it is actually worth the GPU/CPU: on the home page, window visible, and the
    /// game not connected yet. Otherwise it is unloaded (decoder freed) and the drawn backdrop shows instead.
    /// </summary>
    private void UpdateBackdrop()
    {
        string? path = _state.BackgroundVideo;
        bool wanted = path is not null && File.Exists(path) && !_gameConnected &&
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
        bool connected = _state.Engine?.Status().Locked == true;
        if (connected != _gameConnected)
        {
            _gameConnected = connected;
            UpdateBackdrop();
        }

        if (_state.Engine is not { } engine)
        {
            NavDot.Fill = Idle;
            NavStatus.Text = "Meter stopped";
            return;
        }

        var status = engine.Status();
        if (!status.Locked)
        {
            NavDot.Fill = Searching;
            NavStatus.Text = "Searching for AION2…";
            return;
        }

        NavDot.Fill = Connected;
        string who = status.SelfName ?? (status.SelfClass != GameClass.Unknown ? status.SelfClass.ToString() : "connected");
        NavStatus.Text = status.InCombat ? $"{who} · in combat" : who;
    }

    internal UserControl CurrentPage => (UserControl)Page.Content;

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
