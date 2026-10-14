using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Avion2.Protocol;

namespace Avion2.Overlay;

/// <summary>
/// The official class emblems, built into the app (Assets/classes, from assets/gamedata/classes). A PNG with the
/// same name in %AppData%\AVION2\classes (e.g. gladiator.png) overrides the built-in one.
/// </summary>
public static class ClassIcons
{
    private static readonly Dictionary<GameClass, ImageSource?> Cache = new();

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AVION2", "classes");

    public static ImageSource? For(GameClass cls)
    {
        if (Cache.TryGetValue(cls, out var cached))
        {
            return cached;
        }

        ImageSource? image = null;
        if (cls != GameClass.Unknown)
        {
            string file = cls.ToString().ToLowerInvariant() + ".png";
            string custom = Path.Combine(Directory, file);
            image = (File.Exists(custom) ? Decode(new Uri(custom, UriKind.Absolute)) : null)
                ?? Decode(new Uri($"pack://application:,,,/Assets/classes/{file}", UriKind.Absolute));
        }

        Cache[cls] = image;
        return image;
    }

    private static ImageSource? Decode(Uri uri)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad; // don't keep a file locked
            bitmap.DecodePixelWidth = 64;
            bitmap.UriSource = uri;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException or InvalidOperationException)
        {
            return null;
        }
    }
}
