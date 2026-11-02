using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <summary>
/// Streamer mode (Settings): player, character and legion names show as stand-ins everywhere in the app, for
/// streams and screenshots. Fights, runs and the legion are still saved with the real names.
/// </summary>
public static class StreamerMode
{
    public static NameMask Names { get; } = new();

    public static bool On => Names.On;

    /// <summary>Raised when streamer mode is switched; views that don't refresh by themselves redraw.</summary>
    public static event Action? Changed;

    public static void Set(bool on)
    {
        if (Names.On != on)
        {
            Names.On = on;
            Changed?.Invoke();
        }
    }

    public static string Name(string name, bool self = false) => Names.Name(name, self);

    public static string? Legion(string? legion) => Names.Legion(legion);

    /// <summary>Players only: bosses, monsters and summons keep their names.</summary>
    public static string Name(TraceEntity e) =>
        e.Kind is TraceEntityKind.Self or TraceEntityKind.Player ? Name(e.Name, e.Kind == TraceEntityKind.Self) : e.Name;

    /// <summary>"Name (you)" for our character, just "You" in streamer mode.</summary>
    public static string Titled(TraceEntity e) =>
        e.Kind == TraceEntityKind.Self && !On ? $"{e.Name} (you)" : Name(e);

    public static string Name(LiveEntity e) =>
        e.Kind is LiveKind.Self or LiveKind.Player ? Name(e.Name, e.Kind == LiveKind.Self) : e.Name;
}
