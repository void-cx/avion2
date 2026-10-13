using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <summary>
/// The compact, always-on-top meter shown while the meter runs. History and settings live in the
/// home window; clicking a player opens the live detail window.
/// </summary>
public partial class MainWindow : Window
{
    private const int HotkeyLock = 1;
    private const int HotkeyReset = 2;
    private const int HotkeyToggle = 3;

    private readonly AppState _state;
    private readonly MeterEngine _engine;
    private readonly OverlayViewModel _vm;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _alertTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly LowHpMonitor _lowHp = new();
    private AlertSound _alertSound;
    private DetailWindow? _detail;
    private nint _hwnd;
    private string? _startupError;

    public MainWindow(AppState state, MeterEngine engine)
    {
        _state = state;
        _engine = engine;
        _vm = state.Settings;
        InitializeComponent();
        DataContext = _vm;
        PlaceWindow();
        _alertSound = new AlertSound(_vm.LowHpSound, _vm.AlertVolume);
        _lowHp.Threshold = _vm.LowHpThreshold;
        _vm.PropertyChanged += OnSettingChanged;
        _timer.Tick += (_, _) => Refresh();
        _alertTimer.Tick += (_, _) =>
        {
            _alertTimer.Stop();
            _vm.Alerting = false;
        };
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>Initial tab (<c>--tab taken</c>).</summary>
    public OverlayTab StartTab { get; set; }

    internal DetailWindow? DetailView => _detail;



    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        // Never steal focus from the game; keep a taskbar entry so the overlay can be found and closed.
        NativeMethods.SetExStyle(_hwnd, NativeMethods.WsExNoActivate | NativeMethods.WsExAppWindow, 0);
        ApplyLock();

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        uint mods = NativeMethods.ModControl | NativeMethods.ModShift | NativeMethods.ModNoRepeat;
        NativeMethods.RegisterHotKey(_hwnd, HotkeyLock, mods, (uint)KeyInterop.VirtualKeyFromKey(Key.L));
        NativeMethods.RegisterHotKey(_hwnd, HotkeyReset, mods, (uint)KeyInterop.VirtualKeyFromKey(Key.R));
        NativeMethods.RegisterHotKey(_hwnd, HotkeyToggle, mods, (uint)KeyInterop.VirtualKeyFromKey(Key.O));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm.Tab = StartTab;
        var errors = _engine.Start();
        if (errors.Count > 0 && _engine.Status().Message.StartsWith("No capture", StringComparison.Ordinal))
        {
            _startupError = string.Join("; ", errors);
        }

        Refresh();
        _timer.Start();
    }

    private void Refresh()
    {
        var status = _engine.Status();
        if (_startupError is not null)
        {
            status = status with { Message = $"Capture failed: {_startupError}" };
        }

        _vm.Update(_engine.Snapshot(_vm.Scope), status);
        CheckLowHp(status);
    }

    private void CheckLowHp(EngineStatus status)
    {
        if (!_lowHp.Update(status.SelfHp, status.SelfMaxHp, Environment.TickCount64) || !_vm.LowHpAlert)
        {
            return;
        }

        _alertSound.Play();
        _vm.Alerting = true;
        _alertTimer.Stop();
        _alertTimer.Start();
    }

    /// <summary>Settings edited in the home window (or via the header) take effect immediately.</summary>
    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(OverlayViewModel.Locked):
                ApplyLock();
                break;
            case nameof(OverlayViewModel.AlertVolume):
                _alertSound.Volume = _vm.AlertVolume;
                break;
            case nameof(OverlayViewModel.LowHpThreshold):
                _lowHp.Threshold = _vm.LowHpThreshold;
                break;
            case nameof(OverlayViewModel.LowHpSound):
                _alertSound.Dispose();
                _alertSound = new AlertSound(_vm.LowHpSound, _vm.AlertVolume);
                break;
            case nameof(OverlayViewModel.AllTargets):
                Refresh();
                break;
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != NativeMethods.WmHotKey)
        {
            return 0;
        }

        switch ((int)wParam)
        {
            case HotkeyLock:
                _vm.Locked = !_vm.Locked;
                break;
            case HotkeyReset:
                _engine.Reset();
                Refresh();
                break;
            case HotkeyToggle:
                Visibility = Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
                break;
        }

        handled = true;
        return 0;
    }

    /// <summary>Locked = click-through: mouse input goes to the game underneath.</summary>
    private void ApplyLock()
    {
        bool locked = _vm.Locked;
        if (_hwnd != 0)
        {
            NativeMethods.SetExStyle(
                _hwnd,
                locked ? NativeMethods.WsExTransparent | NativeMethods.WsExLayered : 0,
                locked ? 0 : NativeMethods.WsExTransparent);
        }

        ResizeMode = locked ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Header_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _vm.BackgroundOpacity += e.Delta > 0 ? 0.05 : -0.05;
            e.Handled = true;
        }
    }

    private void Scope_Click(object sender, RoutedEventArgs e) => _vm.AllTargets = !_vm.AllTargets;

    private void Alert_Click(object sender, RoutedEventArgs e)
    {
        _vm.LowHpAlert = !_vm.LowHpAlert;
        if (_vm.LowHpAlert)
        {
            _alertSound.Play(); // preview
        }
    }

    /// <summary>Scroll over the speaker icon: volume in 10% steps, with a preview.</summary>
    private void Alert_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _vm.AlertVolume += e.Delta > 0 ? 0.1 : -0.1;
        _vm.LowHpAlert = _vm.AlertVolume > 0;
        _alertSound.Play();
        e.Handled = true;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _engine.Reset();
        Refresh();
    }

    private void History_Click(object sender, RoutedEventArgs e) => _state.RequestPage("history");

    private void Settings_Click(object sender, RoutedEventArgs e) => _state.RequestPage("settings");

    private void Lock_Click(object sender, RoutedEventArgs e) => _vm.Locked = true;

    /// <summary>✕ stops the meter (the running fight is saved) and brings the home window back.</summary>
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _state.StopMeter();
        _state.RequestPage("home");
    }

    private void TabDamage_Click(object sender, RoutedEventArgs e) => SwitchTab(OverlayTab.Damage);

    private void TabTaken_Click(object sender, RoutedEventArgs e) => SwitchTab(OverlayTab.Taken);

    private void SwitchTab(OverlayTab tab)
    {
        _vm.Tab = tab;
        Refresh();
    }

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: uint id })
        {
            OpenDetails(id, _vm.Tab == OverlayTab.Taken ? 1 : 0);
        }
    }

    internal void OpenDetails(uint? actorId, int tab)
    {
        if (_detail is null)
        {
            _detail = new DetailWindow(_engine, () => _vm.Scope);
            _detail.Closed += (_, _) => _detail = null;
        }

        _detail.Open(actorId, tab);
    }

    private void PlaceWindow()
    {
        var s = _state.Stored;
        bool onScreen =
            s.Left >= SystemParameters.VirtualScreenLeft - 50 &&
            s.Top >= SystemParameters.VirtualScreenTop - 50 &&
            s.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50 &&
            s.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50;

        Left = onScreen ? s.Left : 80;
        Top = onScreen ? s.Top : 160;
        Width = Math.Max(s.Width, MinWidth);
        Height = Math.Max(s.Height, MinHeight);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _vm.PropertyChanged -= OnSettingChanged;
        _detail?.Close();

        if (_state.PersistSettings)
        {
            _state.Stored.Left = Left;
            _state.Stored.Top = Top;
            _state.Stored.Width = Width;
            _state.Stored.Height = Height;
            _state.SaveSettings();
        }

        if (_hwnd != 0)
        {
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyLock);
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyReset);
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyToggle);
        }

        _alertSound.Dispose();
        _engine.Dispose();
        _state.OnOverlayClosed(this);
    }
}
