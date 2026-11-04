using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExtractGameData.Core;

/// <summary>The kinds of field a row can hold, as the client's type map names them.</summary>
public enum FieldKind
{
    Bool,
    Byte,
    Int8,
    Int16,
    UInt16,
    Int,
    UInt32,
    Int64,
    UInt64,
    Float,
    Double,
    /// <summary>A name: a masked table string.</summary>
    Name,
    /// <summary>An enum value ("ERace::Light"): a masked table string.</summary>
    Enum,
    /// <summary>A text field: a plain (unmasked) string.</summary>
    Str,
    Struct,
    Array,
    Set,
    Map,
}

/// <summary>
/// A field type: <c>Int</c>, <c>Name</c>, <c>Enum&lt;ERace&gt;</c>, <c>Struct&lt;TableId&gt;</c>,
/// <c>Array&lt;Struct&lt;ComponentItem&gt;&gt;</c>, <c>Map&lt;Enum&lt;EStat&gt;,Int&gt;</c>.
/// </summary>
public sealed record FieldType(FieldKind Kind, string? Name = null, FieldType? Inner = null, FieldType? Value = null)
{
    public static FieldType Parse(string text)
    {
        int at = 0;
        var type = Parse(text, ref at);
        if (at != text.Length)
        {
            throw new GameDataFormatException($"bad type '{text}'");
        }

        return type;
    }

    private static FieldType Parse(string text, ref int at)
    {
        int start = at;
        while (at < text.Length && char.IsLetterOrDigit(text[at]))
        {
            at++;
        }

        string head = text[start..at];
        if (!Enum.TryParse<FieldKind>(head, out var kind) || kind.ToString() != head)
        {
            throw new GameDataFormatException($"unknown type '{head}' in '{text}'");
        }

        if (at >= text.Length || text[at] != '<')
        {
            return kind is FieldKind.Struct or FieldKind.Array or FieldKind.Set or FieldKind.Map or FieldKind.Enum
                ? throw new GameDataFormatException($"'{head}' needs a parameter in '{text}'")
                : new FieldType(kind);
        }

        at++; // '<'
        FieldType result;
        if (kind is FieldKind.Struct or FieldKind.Enum)
        {
            int nameStart = at;
            while (at < text.Length && text[at] != '>')
            {
                at++;
            }

            result = new FieldType(kind, text[nameStart..at]);
        }
        else if (kind is FieldKind.Array or FieldKind.Set)
        {
            result = new FieldType(kind, Inner: Parse(text, ref at));
        }
        else if (kind == FieldKind.Map)
        {
            var key = Parse(text, ref at);
            if (at >= text.Length || text[at] != ',')
            {
                throw new GameDataFormatException($"map without value type in '{text}'");
            }

            at++;
            result = new FieldType(kind, Inner: key, Value: Parse(text, ref at));
        }
        else
        {
            throw new GameDataFormatException($"'{head}' takes no parameter in '{text}'");
        }

        if (at >= text.Length || text[at] != '>')
        {
            throw new GameDataFormatException($"unclosed '<' in '{text}'");
        }

        at++;
        return result;
    }

    public override string ToString() => Kind switch
    {
        FieldKind.Struct or FieldKind.Enum => $"{Kind}<{Name}>",
        FieldKind.Array or FieldKind.Set => $"{Kind}<{Inner}>",
        FieldKind.Map => $"Map<{Inner},{Value}>",
        _ => Kind.ToString(),
    };
}

/// <summary>One field of a row struct, in the order the row stores it.</summary>
public sealed record FieldDef(string Name, FieldType Type);

/// <summary>
/// A table: its row struct, and for tables whose rows continue with a second struct chosen by one of their fields
/// (<c>Item</c>: the equipment, usable, misc or currency part by <c>ItemType</c>), that field and the structs.
/// </summary>
public sealed record TableDef(string Row, string? VariantField = null, IReadOnlyDictionary<string, string>? Variants = null);

/// <summary>
/// Row layouts of the client tables AVION2 builds data from (tools/extract_gamedata/schema.json). The client stores
/// rows without field names or tags, so every field must be known, in order; the file's header lists one column
/// hash per leaf column, which checks the count.
/// </summary>
public sealed class TableSchema
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public TableSchema(IReadOnlyDictionary<string, TableDef> tables, IReadOnlyDictionary<string, IReadOnlyList<FieldDef>> structs, string? source = null)
    {
        Tables = tables;
        Structs = structs;
        Source = source;
    }

    /// <summary>Where the layouts came from (the type map they were bootstrapped from).</summary>
    public string? Source { get; }

    public IReadOnlyDictionary<string, TableDef> Tables { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<FieldDef>> Structs { get; }

    public IReadOnlyList<FieldDef> Struct(string name) =>
        Structs.TryGetValue(name, out var fields) ? fields : throw new GameDataFormatException($"struct {name} is not in the schema");

    /// <summary>
    /// Leaf columns as the table header counts them: nested structs and arrays or sets of structs count their own
    /// leaves, a map directly on a row struct counts its key's and its value's, everything else counts one; a table's
    /// row variants add theirs.
    /// </summary>
    public int LeafColumns(string table)
    {
        var def = Tables[table];
        int count = LeafColumnsOf(def.Row);
        if (def.Variants is { } variants)
        {
            count += variants.Values.Distinct().Sum(LeafColumnsOf);
        }

        return count;
    }

    private int LeafColumnsOf(string structName)
    {
        return Struct(structName).Sum(field => LeafColumnsOf(field.Type, top: true));
    }

    private int LeafColumnsOf(FieldType type, bool top) => type.Kind switch
    {
        FieldKind.Struct => LeafColumnsOf(type.Name!),
        FieldKind.Array or FieldKind.Set when type.Inner!.Kind == FieldKind.Struct => LeafColumnsOf(type.Inner.Name!),
        FieldKind.Map when top => LeafColumnsOf(type.Inner!, top: false) + LeafColumnsOf(type.Value!, top: false),
        _ => 1,
    };

    public static TableSchema Parse(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new GameDataFormatException("empty schema");
        var tables = new Dictionary<string, TableDef>(StringComparer.Ordinal);
        foreach (var (name, node) in root["tables"]?.AsObject() ?? [])
        {
            var t = node!.AsObject();
            tables[name] = new TableDef(
                (string?) t["row"] ?? name + "TableItem",
                (string?) t["variantField"],
                t["variants"]?.AsObject().ToDictionary(v => v.Key, v => (string) v.Value!, StringComparer.Ordinal));
        }

        var structs = new Dictionary<string, IReadOnlyList<FieldDef>>(StringComparer.Ordinal);
        foreach (var (name, node) in root["structs"]?.AsObject() ?? [])
        {
            structs[name] = node!.AsArray().Select(f =>
            {
                string line = (string) f!;
                int colon = line.IndexOf(": ", StringComparison.Ordinal);
                if (colon <= 0)
                {
                    throw new GameDataFormatException($"bad field '{line}' in struct {name}");
                }

                return new FieldDef(line[..colon], FieldType.Parse(line[(colon + 2)..]));
            }).ToList();
        }

        return new TableSchema(tables, structs, (string?) root["source"]);
    }

    /// <summary>The schema as JSON: one line per field ("Name: Type"), tables and structs sorted.</summary>
    public string ToJson()
    {
        var tables = new JsonObject();
        foreach (var (name, def) in Tables.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            var t = new JsonObject { ["row"] = def.Row };
            if (def.VariantField is { } field && def.Variants is { } variants)
            {
                t["variantField"] = field;
                t["variants"] = new JsonObject(variants.OrderBy(v => v.Key, StringComparer.Ordinal)
                    .Select(v => KeyValuePair.Create(v.Key, (JsonNode?) v.Value)));
            }

            tables[name] = t;
        }

        var structs = new JsonObject();
        foreach (var (name, fields) in Structs.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            structs[name] = new JsonArray(fields.Select(f => (JsonNode) $"{f.Name}: {f.Type}").ToArray());
        }

        var root = new JsonObject { ["source"] = Source, ["tables"] = tables, ["structs"] = structs };
        return root.ToJsonString(Indented).Replace("\r\n", "\n") + "\n";
    }
}
