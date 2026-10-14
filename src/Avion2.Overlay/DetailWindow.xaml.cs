using System.Windows;
using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <summary>Live details for the running meter (opened by clicking a player in the overlay).</summary>
public partial class DetailWindow : Window
{
    private readonly MeterEngine _engine;
    private readonly Func<MeterScope> _scope;

    public DetailWindow(MeterEngine engine, Func<MeterScope> scope)
    {
        _engine = engine;
        _scope = scope;
        InitializeComponent();
        ThemeManager.Attach(this);
        View.CanPin = true;
        View.PinChanged += pinned => Topmost = pinned;
    }

    public void Open(uint? actorId, int tabIndex)
    {
        View.ShowLive(() => _engine.Snapshot(_scope()), () => _engine.Status().InCombat, actorId, tabIndex, _engine.LiveTrace);
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
}
