using System.Text;
using System.Text.Json.Nodes;
using Avion2.Analysis;
using Avion2.Engine;
using ExtractGameData.Core;

namespace Avion2.Tests;

/// <summary>
/// The game data built from the client tables (tools/extract_gamedata), and the extractor's decoder on hand-built
/// bytes (no game files needed).
/// </summary>
public class ClientGameDataTests
{
    [Fact]
    public void RecipesHaveTheDragonGearWithItsCombo()
    {
        var book = BundledGameData.Recipes;
        var light = Assert.IsType<Recipe>(book.ById(111013001));
        Assert.Equal("True Dragon Lord Longsword", book.Name(light.Product));
        Assert.Equal(CraftRace.Light, light.Race);
        Assert.Equal(25, light.ComboPercent);
        Assert.Equal("Splendent True Dragon Lord Longsword", book.Name(light.ComboProduct!.Value));
        Assert.NotEmpty(light.Materials);

        var dark = Assert.IsType<Recipe>(book.ById(121013001));
        Assert.Equal("Star Dragon Lord Longsword", book.Name(dark.Product));
        Assert.Equal(CraftRace.Dark, dark.Race);
        Assert.Equal(25, dark.ComboPercent);

        // The guaranteed recipe makes the combo product outright
        var guaranteed = Assert.IsType<Recipe>(book.ById(111013011));
        Assert.Equal("Splendent True Dragon Lord Longsword", book.Name(guaranteed.Product));
        Assert.Null(guaranteed.ComboProduct);

        Assert.Equal(550, book.Recipes.Count);
    }

    [Fact]
    public void StatRatesAreTheTenthOfAPercentPerPoint()
    {
        var rates = StatRates.Bundled;
        Assert.Equal(16, rates.Inputs.Count);
        Assert.Equal(6, rates.Inputs.Count(i => !i.IsDeity));
        Assert.All(rates.Inputs, i => Assert.All(i.Gives, g => Assert.Equal(0.1, Math.Abs(g.PerPoint), 6)));
        Assert.NotNull(rates.Input("STR"));
        Assert.NotNull(rates.Input("Justice"));
    }

    [Fact]
    public void NamesComeInEightLanguages()
    {
        foreach (string language in new[] { "en", "de", "fr", "es", "pt", "ru", "ja", "ko" })
        {
            Assert.NotNull(BundledGameData.Read($"skills/{language}.json"));
            Assert.NotNull(BundledGameData.Read($"npcs/{language}.json"));
            Assert.NotNull(BundledGameData.Read($"maps/{language}.json"));
        }

        Assert.Equal("Verteron", BundledGameData.Load("de").MapName(1010));
        Assert.Equal("Krao Cave", BundledGameData.Load("en").MapName(600002));
        Assert.NotEqual("Verteron", BundledGameData.Load("ja").MapName(1010));
    }

    [Fact]
    public void TableStringsUndoTheMaskAndKeepTheirTerminator()
    {
        var bytes = new Body().Masked("Ore_01").Bytes;
        Assert.Equal(-7, BitConverter.ToInt32(bytes, 0));
        Assert.Equal((byte) ('O' ^ 0x25), bytes[4]); // the mask runs 25 00 a8 00 7e 00 91 00
        Assert.Equal((byte) ('r' ^ 0xa8), bytes[6]);
        Assert.Equal(new byte[] { 0, 0 }, bytes[^2..]); // the closing NUL is stored as it is
        Assert.Equal("Ore_01", new ByteReader(bytes).String(masked: true));

        var span = Encoding.Unicode.GetBytes("Odyle");
        var copy = span.ToArray();
        ByteReader.Unmask(copy);
        Assert.NotEqual(span, copy);
        ByteReader.Unmask(copy);
        Assert.Equal(span, copy);

        Assert.Equal("Grüße", new ByteReader(new Body().Utf8("Grüße").Bytes).String(masked: false));
        Assert.Equal(string.Empty, new ByteReader(new Body().Int(0).Bytes).String(masked: true));
        var unterminated = new Body().Int(3).Raw((byte) 'a', (byte) 'b', (byte) 'c').Bytes;
        Assert.Throws<GameDataFormatException>(() => new ByteReader(unterminated).String(masked: false));
        Assert.Throws<GameDataFormatException>(() => new ByteReader(new Body().Int(-50).Bytes).String(masked: true));
    }

    [Fact]
    public void TextFilesAreKeyTextPairs()
    {
        var body = new Body().Int(1).Utf8("AION2").Int(2)
            .Utf8("String_STR_Map_Krao_Cave_body").Utf8("Krao Cave")
            .Utf8("String_STR_Map_Krao_Cave_name").Utf16("Höhle")
            .Int(0);
        var texts = L10nReader.Read(body.Bytes, out string ns);
        Assert.Equal("AION2", ns);
        Assert.Equal("Krao Cave", texts["String_STR_Map_Krao_Cave_body"]);
        Assert.Equal("Höhle", texts["String_STR_Map_Krao_Cave_name"]);

        var twice = new Body().Int(1).Utf8("AION2").Int(2).Utf8("k").Utf8("a").Utf8("k").Utf8("b").Int(0);
        Assert.Throws<GameDataFormatException>(() => L10nReader.Read(twice.Bytes, out _));
        var trailing = new Body().Int(1).Utf8("AION2").Int(0).Int(0).Raw(9);
        Assert.Throws<GameDataFormatException>(() => L10nReader.Read(trailing.Bytes, out _));
        Assert.Throws<GameDataFormatException>(() => L10nReader.Read(new Body().Int(2).Bytes, out _));
    }

    private const string Schema = """
        {
          "source": "test",
          "tables": { "Test": { "row": "TestRow" } },
          "structs": {
            "TableId": [ "Value: Int" ],
            "Part": [ "Item: Name", "Count: Int" ],
            "TestRow": [
              "ID: Struct<TableId>", "Name: Name", "Race: Enum<ERace>", "Shown: Bool", "Rate: Float", "Desc: Str",
              "Parts: Array<Struct<Part>>", "Stats: Map<Enum<EStat>,Int>"
            ]
          }
        }
        """;

    // A row as the client stores it: fields in order, no tags; the array of structs starts with a struct tag
    private static Body Row(Body body, int id, bool shown = true, byte? boolByte = null)
    {
        body.Int(id).Masked("Sword").Masked("ERace::Light").Raw(boolByte ?? (byte) (shown ? 1 : 0)).Float(0.25f).Utf8("Grüße");
        body.Int(2).Utf8("Parts").Utf8("StructProperty").Raw(0, 0, 0, 0, 0, 0, 0, 0).Utf8("Part").Raw(new byte[16]).Raw(0);
        body.Masked("Ore").Int(3).Masked("Gem").Int(1);
        body.Int(0).Int(1).Masked("EStat::STR").Int(5);
        return body;
    }

    private static Body Table(int columns, int rows, int version = 13)
    {
        var body = new Body().Int(version).Masked(string.Join(",", Enumerable.Range(0, columns).Select(i => $"c{i:x8}"))).Int(rows);
        for (int i = 0; i < rows; i++)
        {
            Row(body, 100 + i, shown: i == 0);
        }

        return body;
    }

    [Fact]
    public void RowsDecodeFieldByField()
    {
        var schema = TableSchema.Parse(Schema);
        Assert.Equal(10, schema.LeafColumns("Test")); // ID 1, five plain fields, Parts 2, the map's key and value

        var table = new TableDecoder(schema).Decode("Test", Table(10, 2).Bytes);
        Assert.Equal(10, table.HeaderColumns);
        Assert.Equal(2, table.Rows.Count);
        var row = table.Rows[0];
        Assert.Equal(100L, row["ID"]!["Value"]!.GetValue<long>());
        Assert.Equal("Sword", row["Name"]!.GetValue<string>());
        Assert.Equal("ERace::Light", row["Race"]!.GetValue<string>());
        Assert.True(row["Shown"]!.GetValue<bool>());
        Assert.False(table.Rows[1]["Shown"]!.GetValue<bool>());
        Assert.Equal(0.25, row["Rate"]!.GetValue<double>());
        Assert.Equal("Grüße", row["Desc"]!.GetValue<string>());
        var parts = row["Parts"]!.AsArray();
        Assert.Equal(new[] { "Ore", "Gem" }, parts.Select(p => p!["Item"]!.GetValue<string>()));
        Assert.Equal(new[] { 3L, 1L }, parts.Select(p => p!["Count"]!.GetValue<long>()));
        var stat = Assert.Single(row["Stats"]!.AsArray())!.AsArray();
        Assert.Equal(("EStat::STR", 5L), (stat[0]!.GetValue<string>(), stat[1]!.GetValue<long>()));
        Assert.Equal(101L, table.Rows[1]["ID"]!["Value"]!.GetValue<long>());
    }

    [Fact]
    public void DecodingFailsOnAnyMismatch()
    {
        var decoder = new TableDecoder(TableSchema.Parse(Schema));
        // the header has a column more than the schema: the layout changed
        var wide = Assert.Throws<GameDataFormatException>(() => decoder.Decode("Test", Table(11, 1).Bytes));
        Assert.Contains("usmap", wide.Message);
        Assert.Throws<GameDataFormatException>(() => decoder.Decode("Test", Table(10, 1, version: 12).Bytes));
        Assert.Throws<GameDataFormatException>(() => decoder.Decode("Test", Table(10, 1).Raw(0).Bytes)); // a byte left
        Assert.Throws<GameDataFormatException>(() => decoder.Decode("Other", Table(10, 1).Bytes));

        var badBool = Row(new Body().Int(13).Masked(string.Join(",", Enumerable.Range(0, 10))).Int(1), 1, boolByte: 2);
        var e = Assert.Throws<GameDataFormatException>(() => decoder.Decode("Test", badBool.Bytes));
        Assert.Contains("TestRow.Shown", e.Message);

        var truncated = Table(10, 1).Bytes[..^4];
        Assert.Throws<GameDataFormatException>(() => decoder.Decode("Test", truncated));
    }

    [Fact]
    public void FieldTypesParseAndPrintTheSame()
    {
        foreach (string text in new[] { "Int", "Name", "Enum<ERace>", "Struct<TableId>", "Array<Struct<ComponentItem>>", "Set<Name>", "Map<Enum<EStat>,Int>" })
        {
            Assert.Equal(text, FieldType.Parse(text).ToString());
        }

        var map = FieldType.Parse("Map<Enum<EStat>,Array<Int>>");
        Assert.Equal((FieldKind.Map, FieldKind.Enum, "EStat", FieldKind.Array), (map.Kind, map.Inner!.Kind, map.Inner.Name, map.Value!.Kind));
        foreach (string bad in new[] { "int", "Array", "Int<X>", "Map<Int>", "Struct<X", "Float32" })
        {
            Assert.Throws<GameDataFormatException>(() => FieldType.Parse(bad));
        }
    }

    [Fact]
    public void TheCommittedSchemaRoundTripsAndCountsTheItemColumns()
    {
        string path = Path.Combine(RepoRoot(), "tools", "extract_gamedata", "schema.json");
        string json = File.ReadAllText(path).Replace("\r\n", "\n");
        var schema = TableSchema.Parse(json);
        Assert.Equal(json, schema.ToJson());
        Assert.Equal(125, schema.LeafColumns("Item")); // the client's Item header (row, the four item parts, its stat maps)
        Assert.Contains("CraftRecipe", schema.Tables.Keys);
        Assert.Contains("Dungeon", schema.Tables.Keys);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Avion2.sln")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Avion2.sln not found above the test binaries");
    }

    /// <summary>Builds a little-endian body the way the client writes it.</summary>
    private sealed class Body
    {
        private readonly List<byte> _bytes = [];

        public byte[] Bytes => [.. _bytes];

        public Body Int(int value)
        {
            _bytes.AddRange(BitConverter.GetBytes(value));
            return this;
        }

        public Body Float(float value)
        {
            _bytes.AddRange(BitConverter.GetBytes(value));
            return this;
        }

        public Body Raw(params byte[] bytes)
        {
            _bytes.AddRange(bytes);
            return this;
        }

        public Body Utf8(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            return Int(bytes.Length + 1).Raw(bytes).Raw(0);
        }

        public Body Utf16(string text) => Int(-(text.Length + 1)).Raw(Encoding.Unicode.GetBytes(text)).Raw(0, 0);

        /// <summary>A table string: UTF-16 with the mask over the characters, the NUL as it is.</summary>
        public Body Masked(string text)
        {
            var units = Encoding.Unicode.GetBytes(text);
            ByteReader.Unmask(units);
            return Int(-(text.Length + 1)).Raw(units).Raw(0, 0);
        }
    }
}
