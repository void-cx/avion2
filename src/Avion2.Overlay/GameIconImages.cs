using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <summary>Skill icons, built in (assets/gamedata/skillicons), with the override folder %AppData%\AVION2\skillicons.</summary>
public static class SkillIcons
{
    private static readonly IconImages Images = new(id => GameIcons.SkillIcon(id));

    public static string Directory => GameIcons.SkillDirectory;

    /// <summary>The skill's (or its base skill's) icon, or null when there is none.</summary>
    public static ImageSource? For(uint skillId) => Images.For(skillId);
}

/// <summary>Item icons, built in (assets/gamedata/itemicons), with the override folder %AppData%\AVION2\itemicons.</summary>
public static class ItemIcons
{
    private static readonly IconImages Images = new(id => GameIcons.ItemIcon(id));

    public static string Directory => GameIcons.ItemDirectory;

    /// <summary>The item's icon, or null when there is none.</summary>
    public static ImageSource? For(uint itemId) => Images.For(itemId);
}

/// <summary>Decodes icons once per id and keeps them (frozen, so any thread may draw them).</summary>
internal sealed class IconImages(Func<uint, byte[]?> load)
{
    private readonly Dictionary<uint, ImageSource?> _cache = new();

    public ImageSource? For(uint id)
    {
        lock (_cache)
        {
            if (!_cache.TryGetValue(id, out var image))
            {
                _cache[id] = image = load(id) is { } png ? Decode(png) : null;
            }

            return image;
        }
    }

    private static ImageSource? Decode(byte[] png)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(png);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or FormatException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
