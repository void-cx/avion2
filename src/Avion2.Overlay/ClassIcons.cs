using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Avion2.Protocol;

namespace Avion2.Overlay;

/// <summary>
/// Optional class icons from %AppData%\AVION2\classes\&lt;class&gt;.png (e.g. gladiator.png). Game artwork stays
/// on the user's PC; without a file the overlay draws its own emblem.
/// </summary>
public static class ClassIcons
{
    private static readonly Dictionary<GameClass, ImageSource?> Cache = new();

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AVION2", "classes");

    /// <summary>Forget cached icons (after a download), so the next lookup reads the folder again.</summary>
    public static void Reload() => Cache.Clear();

    public static ImageSource? For(GameClass cls)
    {
        if (Cache.TryGetValue(cls, out var cached))
        {
            return cached;
        }

        ImageSource? image = null;
        string path = Path.Combine(Directory, cls.ToString().ToLowerInvariant() + ".png");
        if (cls != GameClass.Unknown && File.Exists(path))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
                bitmap.DecodePixelWidth = 64;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException or InvalidOperationException)
            {
                image = null;
            }
        }

        Cache[cls] = image;
        return image;
    }
}
