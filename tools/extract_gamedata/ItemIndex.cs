using System.Text.Json.Nodes;
using ExtractGameData.Core;

namespace ExtractGameData;

/// <summary>The client's Item table by internal name and id, with English names and grades.</summary>
public sealed class ItemIndex
{
    private readonly GameTables _tables;
    private readonly Dictionary<string, JsonObject> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, JsonObject> _byId = [];

    public ItemIndex(GameTables tables)
    {
        _tables = tables;
        foreach (var row in tables.Rows("Item"))
        {
            _byKey[row.Str("Name")] = row;
            _byId[row.Id()] = row;
        }
    }

    public IEnumerable<JsonObject> Rows => _byId.Values;

    public JsonObject? ById(uint id) => _byId.GetValueOrDefault(id);

    /// <summary>The id of an item by its internal name ("Material_Gather_Od_A_u_01_f"); fails when unknown.</summary>
    public uint Id(string key) =>
        _byKey.TryGetValue(key, out var row) ? row.Id() : throw new GameDataFormatException($"item {key} is not in the Item table");

    public string? Name(JsonObject row, string locale = "en-US") => _tables.Text(locale, row.Key("Desc"));

    public string? Name(uint id, string locale = "en-US") => ById(id) is { } row ? Name(row, locale) : null;

    /// <summary>The grade as the game shows it ("EItemGrade::Legend" → "Epic").</summary>
    public string? Grade(JsonObject row) => GradeName(_tables, row.EnumValue("ItemGrade"));

    public static string? GradeName(GameTables tables, string grade) => tables.Text("en-US", "Tooltip_Grade_" + grade);
}
