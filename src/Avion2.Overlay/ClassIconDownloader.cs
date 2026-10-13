using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using Avion2.Protocol;

namespace Avion2.Overlay;

/// <summary>
/// Downloads the official class icons from NCSoft's public site into %AppData%\AVION2\classes on first start
/// (or on demand from Settings). They are game artwork: stored per user, never bundled or committed.
/// Every image is decoded and re-encoded as PNG (WPF cannot show WebP directly).
/// </summary>
public static class ClassIconDownloader
{
    private const string Cdn = "https://assets.playnccdn.com/static-aion2/characters/img/class/class_icon_";

    /// <summary>
    /// The class emblems of the official AION2 armory (aion2.plaync.com/…/characters): 256×256 transparent PNGs
    /// on NCSoft's CDN, named by the lowercase class (Elementalist = Spiritmaster).
    /// </summary>
    public static readonly IReadOnlyDictionary<GameClass, string> Sources = new Dictionary<GameClass, string>
    {
        [GameClass.Gladiator] = Cdn + "gladiator.png",
        [GameClass.Templar] = Cdn + "templar.png",
        [GameClass.Ranger] = Cdn + "ranger.png",
        [GameClass.Assassin] = Cdn + "assassin.png",
        [GameClass.Elementalist] = Cdn + "elementalist.png",
        [GameClass.Sorcerer] = Cdn + "sorcerer.png",
        [GameClass.Cleric] = Cdn + "cleric.png",
        [GameClass.Chanter] = Cdn + "chanter.png",
        [GameClass.Fighter] = Cdn + "fighter.png",
    };

    public static bool AllPresent =>
        Sources.Keys.All(cls => File.Exists(Path.Combine(ClassIcons.Directory, cls.ToString().ToLowerInvariant() + ".png")));

    /// <summary>Downloads every icon that is missing (or all with <paramref name="overwrite"/>). Returns how many were saved.</summary>
    public static async Task<int> DownloadAsync(bool overwrite = false, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ClassIcons.Directory);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 AVION2");

        int saved = 0;
        foreach (var (cls, url) in Sources)
        {
            string target = Path.Combine(ClassIcons.Directory, cls.ToString().ToLowerInvariant() + ".png");
            if (!overwrite && File.Exists(target))
            {
                continue;
            }

            try
            {
                byte[] bytes = await http.GetByteArrayAsync(url, cancellationToken);
                await File.WriteAllBytesAsync(target, ToPng(bytes), cancellationToken);
                saved++;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or NotSupportedException or TaskCanceledException or InvalidOperationException)
            {
                // one missing icon just keeps the drawn emblem
            }
        }

        ClassIcons.Reload();
        return saved;
    }

    private static byte[] ToPng(byte[] image)
    {
        using var input = new MemoryStream(image);
        var frame = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
