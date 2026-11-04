using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap;
using ExtractGameData.Core;

namespace ExtractGameData;

/// <summary>
/// Bootstraps the schema from a type map (<c>.usmap</c>, not part of the repository): takes the row struct of every
/// table the schema lists (plus any named on the command line, "Table" or "Table=RowStruct") and every struct they
/// use, and writes them as schema.json. Run it when a patch changes a layout and the header check fails.
/// </summary>
public static class UsmapImport
{
    public static TableSchema Build(string usmapPath, TableSchema? current, IEnumerable<string> extraTables)
    {
        var mappings = new FileUsmapTypeMappingsProvider(usmapPath).MappingsForGame
            ?? throw new InvalidOperationException($"{usmapPath} has no type mappings");

        var tables = new Dictionary<string, TableDef>(current?.Tables ?? new Dictionary<string, TableDef>(), StringComparer.Ordinal);
        foreach (string arg in extraTables)
        {
            var parts = arg.Split('=', 2);
            tables[parts[0]] = new TableDef(parts.Length == 2 ? parts[1] : parts[0] + "TableItem");
        }

        var structs = new Dictionary<string, IReadOnlyList<FieldDef>>(StringComparer.Ordinal);
        void Add(string name)
        {
            if (structs.ContainsKey(name))
            {
                return;
            }

            if (!mappings.Types.TryGetValue(name, out var s))
            {
                throw new InvalidOperationException($"struct {name} is not in {Path.GetFileName(usmapPath)}");
            }

            var fields = new List<FieldDef>();
            structs[name] = fields;
            int count = s.CountProperties(true);
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                if (!s.TryGetValue(i, out var info))
                {
                    throw new InvalidOperationException($"{name}: no property #{i}");
                }

                string field = info.ArraySize > 1 ? $"{info.Name}[{info.Index}]" : info.Name;
                if (!names.Add(field))
                {
                    field = $"{field}#{i}";
                    names.Add(field);
                }

                var type = Convert(info.MappingType);
                fields.Add(new FieldDef(field, type));
                foreach (string used in StructsIn(type))
                {
                    Add(used);
                }
            }
        }

        foreach (var def in tables.Values)
        {
            Add(def.Row);
            foreach (string variant in def.Variants?.Values ?? [])
            {
                Add(variant);
            }
        }

        return new TableSchema(tables, structs, $"{Path.GetFileName(usmapPath)} (row layouts of the tables below only)");
    }

    private static IEnumerable<string> StructsIn(FieldType type)
    {
        if (type.Kind == FieldKind.Struct)
        {
            yield return type.Name!;
        }

        foreach (var inner in new[] { type.Inner, type.Value })
        {
            if (inner is not null)
            {
                foreach (string s in StructsIn(inner))
                {
                    yield return s;
                }
            }
        }
    }

    private static FieldType Convert(PropertyType t) => t.Type switch
    {
        "BoolProperty" => new FieldType(FieldKind.Bool),
        "ByteProperty" when t.EnumName is { } e && !e.Equals("None", StringComparison.OrdinalIgnoreCase) => new FieldType(FieldKind.Enum, e),
        "ByteProperty" => new FieldType(FieldKind.Byte),
        "Int8Property" => new FieldType(FieldKind.Int8),
        "Int16Property" => new FieldType(FieldKind.Int16),
        "UInt16Property" => new FieldType(FieldKind.UInt16),
        "IntProperty" => new FieldType(FieldKind.Int),
        "UInt32Property" => new FieldType(FieldKind.UInt32),
        "Int64Property" => new FieldType(FieldKind.Int64),
        "UInt64Property" => new FieldType(FieldKind.UInt64),
        "FloatProperty" => new FieldType(FieldKind.Float),
        "DoubleProperty" => new FieldType(FieldKind.Double),
        "NameProperty" => new FieldType(FieldKind.Name),
        "EnumProperty" => new FieldType(FieldKind.Enum, t.EnumName ?? "?"),
        "StrProperty" => new FieldType(FieldKind.Str),
        "StructProperty" => new FieldType(FieldKind.Struct, t.StructType ?? throw new InvalidOperationException("struct without a name")),
        "ArrayProperty" => new FieldType(FieldKind.Array, Inner: Convert(t.InnerType!)),
        "SetProperty" => new FieldType(FieldKind.Set, Inner: Convert(t.InnerType!)),
        "MapProperty" => new FieldType(FieldKind.Map, Inner: Convert(t.InnerType!), Value: Convert(t.ValueType!)),
        _ => throw new InvalidOperationException($"unsupported property type {t.Type}"),
    };
}
