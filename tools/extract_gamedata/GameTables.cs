using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExtractGameData.Core;

namespace ExtractGameData;

/// <summary>The client's tables and texts, decoded on first use.</summary>
public sealed class GameTables(ClientFiles files, TableSchema schema)
{
    /// <summary>AVION2's language codes and the client's locale for each (the Global/EU client has these 8).</summary>
    public static readonly (string Lang, string Locale)[] Languages =
        [("en", "en-US"), ("de", "de-DE"), ("fr", "fr-FR"), ("es", "es-ES"), ("pt", "pt-BR"), ("ru", "ru-RU"), ("ja", "ja-JP"), ("ko", "ko-KR")];

    private readonly TableDecoder _decoder = new(schema);
    private readonly Dictionary<string, DecodedTable> _tables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _texts = new(StringComparer.Ordinal);

    public DateOnly ClientDate => files.ClientDate;

    /// <summary>The tables decoded so far (name, header columns, rows), for the summary.</summary>
    public IEnumerable<DecodedTable> Decoded => _tables.Values.OrderBy(t => t.Name, StringComparer.Ordinal);

    public IReadOnlyList<JsonObject> Rows(string table)
    {
        if (!_tables.TryGetValue(table, out var decoded))
        {
            _tables[table] = decoded = _decoder.Decode(table, files.Table(table));
        }

        return decoded.Rows;
    }

    public IReadOnlyDictionary<string, string> Texts(string locale)
    {
        if (!_texts.TryGetValue(locale, out var texts))
        {
            _texts[locale] = texts = files.Texts(locale);
        }

        return texts;
    }

    /// <summary>The text of a string key ("STR_Map_World_L_A" → "Verteron"), or null when there is none.</summary>
    public string? Text(string locale, string? key) =>
        key is null or "" or "None" ? null : Clean(Texts(locale).GetValueOrDefault("String_" + key + "_body"));

    /// <summary>A text by its full key, or null when missing, empty or the placeholder "None".</summary>
    public string? RawText(string locale, string key) => Clean(Texts(locale).GetValueOrDefault(key));

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) || text == "None" ? null : text;
}

/// <summary>Accessors for decoded rows and the JSON styles of the asset files.</summary>
public static class Json
{
    private static readonly JsonSerializerOptions CompactOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static string Str(this JsonObject row, string field) =>
        row[field]?.GetValue<string>() ?? throw new GameDataFormatException($"no text field {field}");

    public static long Long(this JsonObject row, string field) =>
        row[field] is JsonValue v && v.TryGetValue<long>(out long n) ? n : throw new GameDataFormatException($"no number field {field}");

    public static bool Bool(this JsonObject row, string field) =>
        row[field] is JsonValue v && v.TryGetValue<bool>(out bool b) ? b : throw new GameDataFormatException($"no bool field {field}");

    public static JsonObject Obj(this JsonObject row, string field) =>
        row[field] as JsonObject ?? throw new GameDataFormatException($"no struct field {field}");

    public static IEnumerable<JsonObject> Objects(this JsonObject row, string field) =>
        (row[field] as JsonArray ?? throw new GameDataFormatException($"no array field {field}")).Select(n => (JsonObject) n!);

    public static IEnumerable<string> Strings(this JsonObject row, string field) =>
        (row[field] as JsonArray ?? throw new GameDataFormatException($"no array field {field}")).Select(n => n!.GetValue<string>());

    /// <summary>The <c>ID.Value</c> of a row.</summary>
    public static uint Id(this JsonObject row) => (uint) row.Obj("ID").Long("Value");

    /// <summary>A text key field (<c>Desc.Key</c>).</summary>
    public static string Key(this JsonObject row, string field) => row.Obj(field).Str("Key");

    /// <summary>"ERace::Light" → "Light".</summary>
    public static string EnumValue(this JsonObject row, string field)
    {
        string value = row.Str(field);
        int at = value.IndexOf("::", StringComparison.Ordinal);
        return at < 0 ? value : value[(at + 2)..];
    }

    /// <summary>Compact JSON, as Python's <c>json.dump(separators=(",", ":"), ensure_ascii=False)</c> writes it.</summary>
    public static string Compact(JsonNode node) => node.ToJsonString(CompactOptions);

    /// <summary>Two-space indented JSON with \n line ends, as Python's <c>json.dump(indent=2)</c> writes it.</summary>
    public static string Indented(JsonNode node) => node.ToJsonString(IndentedOptions).Replace("\r\n", "\n");

    public static JsonNode? ReadAsset(string path) => File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
}
