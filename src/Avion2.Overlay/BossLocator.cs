using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <summary>Where a boss stands on the bundled maps, and a crop of the map around it (Timers detail pane).</summary>
public static class BossLocator
{
    private static readonly Lazy<Dictionary<string, (string Key, MapMarker Marker, int Size)>> Markers = new(() =>
    {
        var found = new Dictionary<string, (string, MapMarker, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in BundledGameData.WorldMapKeys)
        {
            if (BundledGameData.WorldMap(key) is not { } map)
            {
                continue;
            }

            foreach (var marker in map.Markers.Where(m => m.Rank is "named" or "elite" || m.Subtype.Contains("Boss", StringComparison.OrdinalIgnoreCase)))
            {
                found.TryAdd(marker.Name, (key, marker, map.Size));
            }
        }

        return found;
    });

    private static readonly Dictionary<string, BitmapSource?> Images = new();

    public static (string Key, MapMarker Marker)? Find(string name) =>
        Markers.Value.TryGetValue(name, out var hit) ? (hit.Key, hit.Marker) : null;

    /// <summary>
    /// A <paramref name="width"/> × <paramref name="height"/> crop of the map around the boss, and where the boss is in it
    /// (centred unless near the map's edge); null if the boss is not on a map.
    /// </summary>
    public static (ImageSource Image, Point At)? Crop(string name, int width, int height)
    {
        if (!Markers.Value.TryGetValue(name, out var hit) || Image(hit.Key) is not { } image)
        {
            return null;
        }

        double scale = image.PixelWidth / (double)hit.Size;
        int w = Math.Min(width, image.PixelWidth), h = Math.Min(height, image.PixelHeight);
        int x = Math.Clamp((int)(hit.Marker.X * scale) - w / 2, 0, image.PixelWidth - w);
        int y = Math.Clamp((int)(hit.Marker.Y * scale) - h / 2, 0, image.PixelHeight - h);
        var crop = new CroppedBitmap(image, new Int32Rect(x, y, w, h));
        crop.Freeze();
        return (crop, new Point(hit.Marker.X * scale - x, hit.Marker.Y * scale - y));
    }

    private static BitmapSource? Image(string key)
    {
        if (Images.TryGetValue(key, out var cached))
        {
            return cached;
        }

        BitmapSource? image = null;
        using (var stream = BundledGameData.WorldMapImage(key))
        {
            if (stream is not null)
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 2048; // about the in-game map's detail; a quarter of the memory
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
        }

        Images[key] = image;
        return image;
    }
}
