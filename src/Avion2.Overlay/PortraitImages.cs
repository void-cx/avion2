using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Avion2.Overlay;

/// <summary>Saved character portraits (see <see cref="Avion2.Engine.PortraitStore"/>) as images, loaded once per file.</summary>
public static class PortraitImages
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The picture at <paramref name="path"/>, decoded small; null for no path or an unreadable file.</summary>
    public static ImageSource? Load(string? path, int width = 96)
    {
        if (path is null)
        {
            return null;
        }

        if (!Cache.TryGetValue(path, out var image))
        {
            try
            {
                var bitmap = new BitmapImage();
                using (var stream = File.OpenRead(path))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad; // read now: the file stays free to be replaced
                    bitmap.DecodePixelWidth = width;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                }

                bitmap.Freeze();
                image = bitmap;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
            {
                image = null;
            }

            Cache[path] = image;
        }

        return image;
    }
}
