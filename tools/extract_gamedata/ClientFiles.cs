using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using ExtractGameData.Core;

namespace ExtractGameData;

/// <summary>
/// The installed client's files, read-only: mounts <c>Content/Paks</c> with CUE4Parse (<c>GAME_Aion2</c>) and hands out
/// the tables and texts without their <c>.dat</c> layer. The game process is never touched.
/// </summary>
public sealed class ClientFiles : IDisposable
{
    public const string TableFolder = "AION2/Content/Data/Table/";
    public const string TextFolder = "AION2/Content/L10N/Text/";
    public const string ManifestPath = "Aion2/Content/StartUpData/Table/key_manifest.dat";

    private readonly DefaultFileProvider _provider;
    private readonly DatDecryptor _dat;

    private ClientFiles(DefaultFileProvider provider, DatDecryptor dat, DateTime newestPak)
    {
        _provider = provider;
        _dat = dat;
        ClientDate = DateOnly.FromDateTime(newestPak);
    }

    /// <summary>The date of the newest pak file: the client's patch date.</summary>
    public DateOnly ClientDate { get; }

    public int FileCount => _provider.Files.Count;

    public static ClientFiles Mount(ToolConfig config)
    {
        string natives = config.Require("AION2_NATIVES_DIR");
        OodleHelper.Initialize(Path.Combine(natives, OodleHelper.OodleFileName));
        ZlibHelper.Initialize(Path.Combine(natives, ZlibHelper.DllName));

        string paks = Path.Combine(config.Require("AION2_GAME_DIR"), "Content", "Paks");
        if (!Directory.Exists(paks))
        {
            throw new InvalidOperationException($"{paks} does not exist (AION2_GAME_DIR is the folder with Content/Paks)");
        }

        var provider = new DefaultFileProvider(paks, SearchOption.AllDirectories, new VersionContainer(EGame.GAME_Aion2), StringComparer.OrdinalIgnoreCase);
        provider.Initialize();
        provider.SubmitKey(new FGuid(), new FAesKey(config.PakKey()));
        provider.Mount();

        var dat = DatDecryptor.FromConstantsFile(config.Require("AION2_DAT_CONSTANTS"));
        var newest = Directory.EnumerateFiles(paks, "*.pak", SearchOption.AllDirectories).Max(File.GetLastWriteTime);
        var files = new ClientFiles(provider, dat, newest);
        dat.LoadManifest(files.Read(ManifestPath));
        return files;
    }

    /// <summary>A file as stored in the paks (the highest-priority copy when a patch pak has it too).</summary>
    public byte[] Read(string path) =>
        _provider.Files.TryGetValue(path, out var file)
            ? file.Read()
            : throw new GameDataFormatException($"{path} is not in the client");

    /// <summary>A data table's body (<c>Data/Table/&lt;name&gt;.dat</c>, decrypted).</summary>
    public byte[] Table(string name) => _dat.Table(Read(TableFolder + name + ".dat"), name);

    /// <summary>The texts of one locale ("en-US"), key → text.</summary>
    public IReadOnlyDictionary<string, string> Texts(string locale) =>
        L10nReader.Read(_dat.Text(Read(TextFolder + locale + "/L10NString.dat"), locale), out _);

    public void Dispose() => _provider.Dispose();
}
