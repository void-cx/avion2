using System.Text.Json;
using System.Text.Json.Serialization;
using Avion2.Analysis;

namespace Avion2.Engine;

/// <summary>Our own characters, in %AppData%\AVION2\characters.json, so the meter can name us without a teleport.</summary>
public sealed class CharacterStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly List<KnownCharacter> _characters;

    public CharacterStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AVION2", "characters.json");
        _characters = Load(_path);
    }

    public IReadOnlyList<KnownCharacter> Characters => _characters;

    public void Remember(KnownCharacter character)
    {
        _characters.RemoveAll(c => c.Name == character.Name && c.ServerId == character.ServerId);
        _characters.Insert(0, character);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_characters, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not critical
        }
    }

    private static List<KnownCharacter> Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<List<KnownCharacter>>(File.ReadAllText(path), Options) ?? new();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // start fresh
        }

        return new();
    }
}
