// Developer tool: rebuilds AVION2's bundled game data (assets/gamedata) from an installed AION2 client, read-only.
// See README.md. The app itself never reads game files or keys.
using System.Text;
using ExtractGameData;
using ExtractGameData.Core;

Console.OutputEncoding = Encoding.UTF8;
string command = args.Length > 0 ? args[0] : "help";
try
{
    var config = ToolConfig.Load();
    switch (command)
    {
        case "build":
            return Build(config, write: args.Contains("--write"));
        case "usmap" or "--usmap" when args.Length >= 2:
        {
            var current = File.Exists(config.SchemaPath) ? TableSchema.Parse(File.ReadAllText(config.SchemaPath)) : null;
            var schema = UsmapImport.Build(args[1], current, args.Skip(2));
            File.WriteAllText(config.SchemaPath, schema.ToJson(), new UTF8Encoding(false));
            Console.WriteLine($"{config.SchemaPath}: {schema.Tables.Count} tables, {schema.Structs.Count} structs");
            return 0;
        }
        case "dump" when args.Length >= 2:
        {
            var schema = TableSchema.Parse(File.ReadAllText(config.SchemaPath));
            using var files = ClientFiles.Mount(config);
            var tables = new GameTables(files, schema);
            string dir = Directory.CreateDirectory(Path.Combine(config.WorkDir, "json")).FullName;
            foreach (string table in args.Skip(1))
            {
                var rows = tables.Rows(table);
                string path = Path.Combine(dir, table + ".json");
                File.WriteAllText(path, Json.Indented(new System.Text.Json.Nodes.JsonArray(rows.Select(r => r.DeepClone()).ToArray())));
                Console.WriteLine($"{table}: {rows.Count} rows → {path}");
            }

            return 0;
        }
        default:
            Console.WriteLine("""
                extract_gamedata build [--write]   rebuild assets/gamedata from the client; prints what changed.
                                                   Without --write it only stages the files (see the work folder).
                extract_gamedata usmap <file> [Table[=RowStruct] ...]
                                                   rewrite schema.json from a type map (after a layout change)
                extract_gamedata dump <Table> ...  decode tables to JSON in the work folder, to look at them
                """);
            return command == "help" ? 0 : 1;
    }
}
catch (Exception e) when (e is GameDataFormatException or InvalidOperationException or IOException)
{
    Console.Error.WriteLine($"failed: {e.Message}");
    Console.Error.WriteLine("nothing was written to assets/gamedata");
    return 2;
}

static int Build(ToolConfig config, bool write)
{
    var schema = TableSchema.Parse(File.ReadAllText(config.SchemaPath));
    using var files = ClientFiles.Mount(config);
    Console.WriteLine($"client of {files.ClientDate:yyyy-MM-dd}: {files.FileCount:N0} files mounted");

    var tables = new GameTables(files, schema);
    var items = new ItemIndex(tables);
    var report = new Report();
    var output = new List<AssetFile>();
    output.AddRange(RecipesBuilder.Build(tables, items, config, report));
    output.AddRange(StatRatesBuilder.Build(tables, config, report));
    output.AddRange(GearForgeBuilder.Build(tables, items, config, report));
    CubeRewardsCheck.Run(tables, items, config, report);
    output.AddRange(ItemsBuilder.Build(tables, items, config, report));
    output.AddRange(NpcsBuilder.Build(tables, config, report));
    output.AddRange(SkillsBuilder.Build(tables, config, report));
    output.AddRange(MapsBuilder.Build(tables, config, report));
    output.AddRange(DungeonNamesBuilder.Build(tables, config, report));
    output.AddRange(FieldBossesBuilder.Build(tables, config, report));

    Console.WriteLine();
    Console.WriteLine("tables (every byte consumed, header columns = schema):");
    foreach (var t in tables.Decoded)
    {
        Console.WriteLine($"  {t.Name,-28} {t.Rows.Count,7:N0} rows, {t.HeaderColumns} columns");
    }

    Console.WriteLine();
    foreach (string line in report.Lines)
    {
        Console.WriteLine(line);
    }

    // Everything built: stage it, then (with --write) replace the assets in one go.
    string stage = Path.Combine(config.WorkDir, "staged");
    if (Directory.Exists(stage))
    {
        Directory.Delete(stage, recursive: true);
    }

    foreach (var file in output)
    {
        string path = Path.Combine(stage, file.Path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, file.Content, new UTF8Encoding(false));
    }

    File.WriteAllLines(Path.Combine(stage, "changes.txt"), report.Lines, new UTF8Encoding(false));
    Console.WriteLine();
    if (!write)
    {
        Console.WriteLine($"staged {output.Count} files in {stage}; run with --write to update assets/gamedata");
        return 0;
    }

    foreach (var file in output)
    {
        string path = Path.Combine(config.AssetsDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, file.Content, new UTF8Encoding(false));
    }

    Console.WriteLine($"wrote {output.Count} files to {config.AssetsDir}");
    return 0;
}
