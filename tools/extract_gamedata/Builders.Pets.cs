using System.Text.Json.Nodes;
using ExtractGameData.Core;

namespace ExtractGameData;

/// <summary>
/// pets.json from VehicleList, VehicleLevel and Item: every pet kind with its name in the 8 languages, the souls the
/// pet takes and its soul item ("Soul: Sparkie"); plus the souls each pet level takes to the next.
/// </summary>
public static class PetsBuilder
{
    public static IEnumerable<AssetFile> Build(GameTables tables, ItemIndex items, ToolConfig config, Report report)
    {
        // Souls from level n to n + 1, in level order; the last level takes none (it is the highest)
        var levels = new JsonArray();
        foreach (var row in tables.Rows("VehicleLevel").OrderBy(r => r.Long("Level")))
        {
            if (row.Long("NeedSoulCount") is > 0 and long souls)
            {
                levels.Add(souls);
            }
        }

        if (levels.Count == 0)
        {
            throw new GameDataFormatException("VehicleLevel: no level takes souls");
        }

        var pets = new JsonObject();
        foreach (var row in tables.Rows("VehicleList").OrderBy(r => r.Id()))
        {
            string key = row.Key("Desc");
            var names = new JsonObject();
            foreach (var (lang, locale) in GameTables.Languages)
            {
                if (tables.Text(locale, key) is { } name)
                {
                    names[lang] = name;
                }
            }

            if (!names.ContainsKey("en"))
            {
                throw new GameDataFormatException($"pet {row.Id()}: no English name for {key}");
            }

            var pet = new JsonObject { ["name"] = names, ["souls"] = row.Long("NeedSoul") };
            // Shop and event pets have no soul item
            if (row.Str("SoulItemName") is var soul and not "None")
            {
                pet["soul"] = items.Id(soul);
            }

            pets[row.Id().ToString()] = pet;
        }

        var file = new JsonObject
        {
            ["source"] = "AION2 client tables VehicleList, VehicleLevel, Item and the texts (tools/extract_gamedata)",
            ["built"] = tables.ClientDate.ToString("yyyy-MM-dd"),
            ["levels"] = levels,
            ["pets"] = pets,
        };

        var old = Json.ReadAsset(Path.Combine(config.AssetsDir, "pets.json"));
        report.Compare("pets.json", Report.Entries(old?["pets"]), Report.Entries(pets)!, s => s.Length > 120 ? s[..120] + "…" : s, examples: 3);
        report.Add("pets.json", $"souls per level {Json.Compact(levels)}" + (old?["levels"] is { } was && Json.Compact(was) != Json.Compact(levels) ? $" (was {Json.Compact(was)})" : ""));
        yield return new AssetFile("pets.json", Json.Indented(file) + "\n");
    }
}
