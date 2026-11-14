using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Avion2.Overlay;

/// <summary>
/// The small note box of a capture marker: on top of the game, takes the keyboard only while it is open. Enter saves
/// the note, Esc saves the marker without one; after 30 s without typing it saves what is there. The window that had
/// the keyboard before (the game) gets it back when the box closes.
/// </summary>
internal sealed class MarkerNoteWindow : Window
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    private readonly TextBox _text;
    private readonly DispatcherTimer _idle = new() { Interval = IdleTimeout };
    private readonly nint _previous;
    private bool _done;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hwnd);

    public MarkerNoteWindow(int number, bool hasPicture)
    {
        _previous = GetForegroundWindow();
        Title = $"Marker {number}";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = 460;
        ShowActivated = true;
        SetResourceReference(BackgroundProperty, "FloatingPanelBrush");
        SetResourceReference(BorderBrushProperty, "FloatingBorderBrush");
        BorderThickness = new Thickness(1);

        var caption = new TextBlock
        {
            Text = $"Marker {number}{(hasPicture ? " · picture saved" : " · no picture")} — what did you just do?",
            Margin = new Thickness(0, 0, 0, 6),
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        _text = new TextBox { FontSize = 14, Padding = new Thickness(4, 3, 4, 3) };
        var hint = new TextBlock { Text = "Enter saves · Esc saves without a note", FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Content = new StackPanel { Margin = new Thickness(12, 10, 12, 10), Children = { caption, _text, hint } };

        ThemeManager.Attach(this);
        PlaceOverGame();
        _text.PreviewKeyDown += OnKey;
        _text.TextChanged += (_, _) => RestartIdle();
        _idle.Tick += (_, _) => Commit();
        Loaded += (_, _) =>
        {
            Activate();
            _text.Focus();
            Keyboard.Focus(_text);
            RestartIdle();
        };
        Deactivated += (_, _) => Commit(); // clicked back into the game: keep what was typed
    }

    /// <summary>Raised once with the note ("" when there is none) when the box closes.</summary>
    public event Action<string>? Done;

    /// <summary>Saves what is typed and closes (another marker key press, the capture stopping).</summary>
    public void Commit() => Finish(_text.Text.Trim());

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Commit();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Finish(string.Empty);
        }
    }

    private void Finish(string note)
    {
        if (_done)
        {
            return;
        }

        _done = true;
        _idle.Stop();
        Done?.Invoke(note);
        Close();
        if (_previous != 0 && IsWindow(_previous))
        {
            SetForegroundWindow(_previous); // the game gets the keyboard back
        }
    }

    private void RestartIdle()
    {
        _idle.Stop();
        _idle.Start();
    }

    /// <summary>Top centre of the game's area (in its upper third, clear of the chat), else of the main screen.</summary>
    private void PlaceOverGame()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        double scale = 1;
        if (PresentationSource.FromVisual(Application.Current.MainWindow ?? this) is HwndSource source && source.CompositionTarget is { } target)
        {
            scale = target.TransformToDevice.M11;
        }

        if (GameWindow.ClientArea is { } area)
        {
            Left = area.X / scale + (area.Width / scale - Width) / 2;
            Top = area.Y / scale + area.Height / scale * 0.18;
        }
        else
        {
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = SystemParameters.PrimaryScreenHeight * 0.18;
        }
    }
}
