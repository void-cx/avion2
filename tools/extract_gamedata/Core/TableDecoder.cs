using System.Text.Json.Nodes;

namespace ExtractGameData.Core;

/// <summary>A decoded client table: its rows as JSON objects (field name → value, nested structs as objects).</summary>
public sealed record DecodedTable(string Name, int HeaderColumns, IReadOnlyList<JsonObject> Rows);

/// <summary>
/// Decodes a decrypted client table with the row layout from the schema, strictly: the version must be 13, the
/// header's column count must match the schema, every bool must be 0 or 1 and every byte must be consumed.
/// </summary>
/// <remarks>
/// Body: <c>i32 version</c>, a masked string of comma-separated column hashes (one per leaf column),
/// <c>i32 rowCount</c>, then the rows back to back. A row is its fields in schema order with no tags: numbers raw
/// little-endian, bool one byte, name and enum a masked string, text a plain string, a struct its fields inline, an
/// array <c>i32 n</c> and its elements, a set <c>i32 removed</c> (that many elements) and an array, a map
/// <c>i32 removed</c>, <c>i32 n</c> and n key/value pairs. An array or set of structs (outside of a map) starts with a
/// struct tag, which is skipped: two strings, 8 bytes, a string, a 16-byte GUID and a flag byte (16 more bytes when
/// set).
/// </remarks>
public sealed class TableDecoder(TableSchema schema)
{
    public const int Version = 13;

    public DecodedTable Decode(string table, byte[] body)
    {
        if (!schema.Tables.TryGetValue(table, out var def))
        {
            throw new GameDataFormatException($"table {table} is not in the schema");
        }

        var r = new ByteReader(body);
        int version = r.Int32();
        if (version != Version)
        {
            throw new GameDataFormatException($"{table}: version {version}, expected {Version}");
        }

        string hashes = r.String(masked: true);
        int headerColumns = hashes.Length == 0 ? 0 : hashes.Split(',').Length;
        int expected = schema.LeafColumns(table);
        if (headerColumns != expected)
        {
            throw new GameDataFormatException(
                $"{table}: the file has {headerColumns} columns, the schema {expected}; the layout changed (run `usmap` with a newer type map)");
        }

        int count = r.Int32();
        if (count < 0 || count > r.Remaining)
        {
            throw new GameDataFormatException($"{table}: bad row count {count}");
        }

        var rows = new List<JsonObject>(count);
        for (int i = 0; i < count; i++)
        {
            int at = r.Position;
            try
            {
                var row = Struct(r, def.Row);
                if (def.VariantField is { } field && def.Variants is { } variants &&
                    row[field]?.GetValue<string>() is { } key && variants.TryGetValue(key, out var variant))
                {
                    var part = Struct(r, variant);
                    foreach (string name in part.Select(p => p.Key).ToList())
                    {
                        var value = part[name];
                        part.Remove(name);
                        row[row.ContainsKey(name) ? $"{variant}.{name}" : name] = value;
                    }
                }

                rows.Add(row);
            }
            catch (GameDataFormatException e)
            {
                throw new GameDataFormatException($"{table} row {i} (at byte {at}): {e.Message}");
            }
        }

        if (r.Remaining != 0)
        {
            throw new GameDataFormatException($"{table}: {r.Remaining} bytes left after {count} rows");
        }

        return new DecodedTable(table, headerColumns, rows);
    }

    private JsonObject Struct(ByteReader r, string name)
    {
        var row = new JsonObject();
        foreach (var field in schema.Struct(name))
        {
            int at = r.Position;
            try
            {
                row[field.Name] = Value(r, field.Type, inMap: false);
            }
            catch (GameDataFormatException e)
            {
                throw new GameDataFormatException($"{name}.{field.Name} (at byte {at}): {e.Message}");
            }
        }

        return row;
    }

    // Integers come out as long and floats as double, so readers need one accessor per kind.
    private JsonNode? Value(ByteReader r, FieldType type, bool inMap) => type.Kind switch
    {
        FieldKind.Bool => r.Bool(),
        FieldKind.Byte => (long) r.Byte(),
        FieldKind.Int8 => (long) (sbyte) r.Byte(),
        FieldKind.Int16 => (long) r.Int16(),
        FieldKind.UInt16 => (long) r.UInt16(),
        FieldKind.Int => (long) r.Int32(),
        FieldKind.UInt32 => (long) r.UInt32(),
        FieldKind.Int64 => r.Int64(),
        FieldKind.UInt64 => r.UInt64(),
        FieldKind.Float => (double) r.Single(),
        FieldKind.Double => r.Double(),
        FieldKind.Name or FieldKind.Enum => r.String(masked: true),
        FieldKind.Str => r.String(masked: false),
        FieldKind.Struct => Struct(r, type.Name!),
        FieldKind.Array => Array(r, type.Inner!, inMap),
        FieldKind.Set => Set(r, type.Inner!),
        FieldKind.Map => Map(r, type),
        _ => throw new GameDataFormatException($"unsupported type {type}"),
    };

    private JsonArray Array(ByteReader r, FieldType inner, bool inMap)
    {
        int count = Count(r);
        if (!inMap && inner.Kind == FieldKind.Struct)
        {
            SkipStructTag(r);
        }

        var array = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            array.Add(Value(r, inner, inMap));
        }

        return array;
    }

    private JsonArray Set(ByteReader r, FieldType inner)
    {
        int removed = Count(r);
        for (int i = 0; i < removed; i++)
        {
            Value(r, inner, inMap: false);
        }

        return Array(r, inner, inMap: false);
    }

    private JsonArray Map(ByteReader r, FieldType type)
    {
        int removed = Count(r);
        for (int i = 0; i < removed; i++)
        {
            Value(r, type.Inner!, inMap: false);
        }

        int count = Count(r);
        var pairs = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            pairs.Add(new JsonArray(Value(r, type.Inner!, inMap: true), Value(r, type.Value!, inMap: true)));
        }

        return pairs;
    }

    private static int Count(ByteReader r)
    {
        int at = r.Position;
        int count = r.Int32();
        return count < 0 || count > r.Remaining ? throw new GameDataFormatException($"bad element count {count} at {at}") : count;
    }

    private static void SkipStructTag(ByteReader r)
    {
        r.String(masked: true); // property name
        r.String(masked: true); // property type
        r.Take(8); // size and index
        r.String(masked: true); // struct name
        r.Take(16); // struct GUID
        if (r.Byte() != 0)
        {
            r.Take(16); // property GUID
        }
    }
}
