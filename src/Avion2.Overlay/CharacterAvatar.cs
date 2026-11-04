using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Avion2.Overlay;

/// <summary>
/// One of our characters as a picture: its portrait from the official site in a circle when one is saved, else its
/// class emblem.
/// </summary>
public sealed class CharacterAvatar : Grid
{
    public static readonly DependencyProperty PortraitProperty = DependencyProperty.Register(
        nameof(Portrait), typeof(ImageSource), typeof(CharacterAvatar), new PropertyMetadata(null, (d, _) => ((CharacterAvatar)d).Update()));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(ImageSource), typeof(CharacterAvatar), new PropertyMetadata(null, (d, _) => ((CharacterAvatar)d).Update()));

    private readonly Image _icon = new() { Margin = new Thickness(2) };
    private readonly Ellipse _round = new() { Visibility = Visibility.Collapsed };

    public CharacterAvatar()
    {
        RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(_round, BitmapScalingMode.HighQuality);
        Children.Add(_icon);
        Children.Add(_round);
    }

    /// <summary>The saved portrait; null shows <see cref="Icon"/> instead.</summary>
    public ImageSource? Portrait
    {
        get => (ImageSource?)GetValue(PortraitProperty);
        set => SetValue(PortraitProperty, value);
    }

    /// <summary>The class emblem.</summary>
    public ImageSource? Icon
    {
        get => (ImageSource?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    private void Update()
    {
        _icon.Source = Icon;
        _icon.Visibility = Portrait is null ? Visibility.Visible : Visibility.Collapsed;
        _round.Fill = Portrait is { } portrait ? new ImageBrush(portrait) { Stretch = Stretch.UniformToFill } : null;
        _round.Visibility = Portrait is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
