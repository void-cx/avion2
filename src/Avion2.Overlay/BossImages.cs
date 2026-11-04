using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <summary>
/// Boss portraits as images (<see cref="BossPortraits"/>: built in from assets/gamedata/bosses; a PNG named after
/// the NPC code or the world boss row in %AppData%\AVION2\bosses overrides one). Null when a boss has none.
/// </summary>
public static class BossImages
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string Directory => BossPortraits.UserDirectory;

    /// <summary>An NPC's portrait.</summary>
    public static ImageSource? ForNpc(uint npcCode) => Load(BossPortraits.Bundled.Locate(npcCode));

    /// <summary>A field boss by its spawn code (the respawn table's id).</summary>
    public static ImageSource? ForSpawn(uint spawnCode, GameData data) => Load(BossPortraits.Bundled.LocateSpawn(spawnCode, data));

    /// <summary>A Timers row or notice by its event key ("wb-…" or "fieldboss:&lt;spawn code&gt;").</summary>
    public static ImageSource? ForTimer(string key, GameData data) => Load(BossPortraits.Bundled.LocateTimer(key, data));

    /// <summary>A boss marker on the bundled maps, by name.</summary>
    public static ImageSource? ForMarker(string name) => Load(BossPortraits.Bundled.LocateName(name));

    /// <summary>Every boss of a world boss row (the Executors: three); the override for the row replaces them all.</summary>
    public static IReadOnlyList<ImageSource> ForWorldBoss(string key) =>
        BossPortraits.Bundled.LocateWorldBoss(key).Select(Load).OfType<ImageSource>().ToList();

    private static ImageSource? Load(string? location)
    {
        if (location is null)
        {
            return null;
        }

        lock (Cache)
        {
            if (Cache.TryGetValue(location, out var cached))
            {
                return cached;
            }

            ImageSource? image = null;
            try
            {
                using var stream = BossPortraits.Bundled.Open(location);
                if (stream is not null)
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad; // read now: the stream closes and no file stays locked
                    bitmap.DecodePixelWidth = 128;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    image = bitmap;
                }
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or ArgumentException)
            {
                image = null; // a broken override file: no picture
            }

            Cache[location] = image;
            return image;
        }
    }
}
