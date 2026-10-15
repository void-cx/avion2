using System.Windows;
using System.Windows.Media;

namespace Avion2.Overlay;

/// <summary>The meter's state in one line ("Meter running · Verteron"), shown at the foot of the sidebars.</summary>
public sealed class ShellStatus : Bindable
{
    private string _text = "Meter stopped";
    private Brush _dot = Brushes.Gray;

    public static ShellStatus Current { get; } = new();

    public string Text { get => _text; private set => Set(ref _text, value); }

    /// <summary>The status dot (theme brush: idle, searching or live).</summary>
    public Brush Dot { get => _dot; private set => Set(ref _dot, value); }

    public string Version { get; } = $"Version {Avion2.Engine.UpdateChecker.CurrentVersion}";

    /// <param name="dotKey">"IdleBrush", "WarnBrush" or "LiveBrush".</param>
    public void Update(string text, string dotKey)
    {
        Text = text;
        if (Application.Current?.TryFindResource(dotKey) is Brush brush)
        {
            Dot = brush;
        }
    }
}
