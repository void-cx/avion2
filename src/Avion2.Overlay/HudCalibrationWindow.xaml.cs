using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Avion2.Analysis;

namespace Avion2.Overlay;

/// <summary>
/// One picture of the game with a box per HUD element: found automatically where possible, moved and resized by
/// the player, saved as fractions of the game's client area for this resolution.
/// </summary>
public partial class HudCalibrationWindow : Window
{
    private static readonly Dictionary<string, Color> Colors = new()
    {
        [HudAnchors.SelfHp] = Color.FromRgb(0xFF, 0x5A, 0x5A),
        [HudAnchors.Target] = Color.FromRgb(0xF3, 0xC7, 0x6E),
        [HudAnchors.Party] = Color.FromRgb(0x7F, 0xD8, 0xFF),
    };

    private readonly AppState _state;
    private readonly BitmapSource _picture;
    private readonly Dictionary<string, Border> _boxes = new();
    private readonly HashSet<string> _found = new();

    public HudCalibrationWindow(AppState state, BitmapSource picture)
    {
        _state = state;
        _picture = picture;
        InitializeComponent();
        ThemeManager.Attach(this);
        Shot.Source = picture;
        Picture.Width = picture.PixelWidth;
        Picture.Height = picture.PixelHeight;

        var saved = state.Stored.HudLayouts.GetValueOrDefault(HudLayout.Key(picture.PixelWidth, picture.PixelHeight));
        foreach (string id in HudAnchors.All)
        {
            AddMarker(id, saved?.Box(id) ?? HudAnchors.Default(id));
        }

        if (saved is null)
        {
            Detect();
        }

        UpdateSummary(saved is not null);
    }

    private double Px => Math.Max(2, _picture.PixelWidth / 640.0);

    private void AddMarker(string id, HudBox box)
    {
        var color = Colors[id];
        var (x, y, w, h) = box.ToPixels(_picture.PixelWidth, _picture.PixelHeight);
        var label = new TextBlock
        {
            Text = HudAnchors.Label(id),
            Foreground = Brushes.White,
            FontSize = Px * 7,
            FontWeight = FontWeights.SemiBold,
            Background = new SolidColorBrush(Color.FromArgb(0xD0, color.R, color.G, color.B)),
            Padding = new Thickness(Px * 2, Px * 0.5, Px * 2, Px * 0.5),
            IsHitTestVisible = false,
        };
        var move = new Thumb { Cursor = System.Windows.Input.Cursors.SizeAll, Template = Transparent(), ToolTip = HudAnchors.Label(id) };
        var resize = new Thumb
        {
            Width = Px * 7,
            Height = Px * 7,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -Px * 3.5, -Px * 3.5),
            Cursor = System.Windows.Input.Cursors.SizeNWSE,
            Background = new SolidColorBrush(color),
        };
        var marker = new Border
        {
            Width = w,
            Height = h,
            BorderThickness = new Thickness(Px),
            BorderBrush = new SolidColorBrush(color),
            Background = new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B)),
            Child = new Grid { Children = { move, resize } },
            Tag = label,
        };
        Canvas.SetLeft(marker, x);
        Canvas.SetTop(marker, y);
        move.DragDelta += (_, e) =>
        {
            Canvas.SetLeft(marker, Math.Clamp(Canvas.GetLeft(marker) + e.HorizontalChange, 0, _picture.PixelWidth - marker.Width));
            Canvas.SetTop(marker, Math.Clamp(Canvas.GetTop(marker) + e.VerticalChange, 0, _picture.PixelHeight - marker.Height));
            PlaceLabel(marker);
        };
        resize.DragDelta += (_, e) =>
        {
            marker.Width = Math.Max(Px * 6, marker.Width + e.HorizontalChange);
            marker.Height = Math.Max(Px * 3, marker.Height + e.VerticalChange);
        };
        Markers.Children.Add(marker);
        Markers.Children.Add(label);
        _boxes[id] = marker;
        PlaceLabel(marker);
    }

    private void PlaceLabel(Border marker)
    {
        var label = (TextBlock)marker.Tag;
        Canvas.SetLeft(label, Canvas.GetLeft(marker));
        Canvas.SetTop(label, Math.Max(0, Canvas.GetTop(marker) - Px * 11));
    }

    private static ControlTemplate Transparent()
    {
        var template = new ControlTemplate(typeof(Thumb));
        var fill = new FrameworkElementFactory(typeof(Border));
        fill.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        template.VisualTree = fill;
        return template;
    }

    /// <summary>Looks for the HUD elements in the picture and moves the boxes there.</summary>
    private void Detect()
    {
        _found.Clear();
        var bgra = new FormatConvertedBitmap(_picture, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        if (HudDetector.FindHpBar(pixels, bgra.PixelWidth, bgra.PixelHeight, stride) is { } hp)
        {
            var (x, y, w, h) = hp.ToPixels(_picture.PixelWidth, _picture.PixelHeight);
            var marker = _boxes[HudAnchors.SelfHp];
            Canvas.SetLeft(marker, x);
            Canvas.SetTop(marker, y);
            marker.Width = w;
            marker.Height = h;
            PlaceLabel(marker);
            _found.Add(HudAnchors.SelfHp);
        }
    }

    private void UpdateSummary(bool saved)
    {
        string hp = _found.Contains(HudAnchors.SelfHp) ? "Your HP bar was found automatically."
            : saved ? "These are your saved positions."
            : "Your HP bar was not found (calibrate at full HP); drag the red box onto it.";
        Summary.Text = $"{_picture.PixelWidth} × {_picture.PixelHeight} · {hp} Put the gold box on the target frame (where boss skills appear below).";
    }

    private void Detect_Click(object sender, RoutedEventArgs e)
    {
        Detect();
        UpdateSummary(false);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        int w = _picture.PixelWidth, h = _picture.PixelHeight;
        var layout = new HudLayout { Width = w, Height = h, CalibratedUtc = DateTime.UtcNow };
        foreach (var (id, marker) in _boxes)
        {
            layout.Boxes[id] = HudBox.FromPixels(Canvas.GetLeft(marker), Canvas.GetTop(marker), marker.Width, marker.Height, w, h);
        }

        _state.SaveHudLayout(layout);
        Close();
    }
}
