using System.Windows;
using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <summary>
/// The fight window: replay, damage by skill, damage taken, buffs and timeline. Follows the running meter (opened
/// by clicking a player in the overlay) or shows a saved fight (opened from History).
/// </summary>
public partial class DetailWindow : Window
{
    private readonly MeterEngine? _engine;
    private readonly Func<MeterScope>? _scope;

    /// <summary>For saved fights (<see cref="OpenSaved"/>).</summary>
    public DetailWindow()
    {
        InitializeComponent();
        ThemeManager.Attach(this);
    }

    /// <summary>For the running meter (<see cref="Open"/>), with a pin to keep it above the game.</summary>
    public DetailWindow(MeterEngine engine, Func<MeterScope> scope)
        : this()
    {
        _engine = engine;
        _scope = scope;
        View.CanPin = true;
        View.PinChanged += pinned => Topmost = pinned;
    }

    public void Open(uint? actorId, int tabIndex)
    {
        if (_engine is { } engine && _scope is { } scope)
        {
            View.ShowLive(() => engine.Snapshot(scope()), () => engine.Status().InCombat, actorId, tabIndex, engine.LiveTrace);
        }

        Present();
    }

    public void OpenSaved(HistoryEntry entry, FightTrace? trace, int tabIndex)
    {
        Title = $"AVION2 · {entry.Snapshot.Title}";
        View.ShowSaved(entry, entry.Snapshot.Self?.ActorId, tabIndex, trace);
        Present();
    }

    private void Present()
    {
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
