using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExtractGameData.Core;

namespace ExtractGameData;

/// <summary>
/// npcs/&lt;lang&gt;.json from NpcData and the texts: every monster and summon with an English name. A name missing in a
/// language falls back to English.
/// </summary>
/// <remarks>
/// <c>isBoss</c>: the game's hero and legend monsters (<c>NpcSubType</c>), and the training scarecrows, so that a
/// session on a scarecrow is a boss fight. <c>isDummy</c> (only written when true): training targets, by their
/// English name (punching bags, scarecrows, balance and level scaling tests).
/// </remarks>
public static partial class NpcsBuilder
{
    public static IEnumerable<AssetFile> Build(GameTables tables, ToolConfig config, Report report)
    {
        var npcs = tables.Rows("NpcData")
            .Where(r => r.EnumValue("NpcType") is "Monster" or "Summon")
            .Select(r => (Row: r, Id: r.Id(), English: tables.Text("en-US", r.Key("Desc"))))
            .Where(n => n.English is not null)
            .OrderBy(n => n.Id.ToString(), StringComparer.Ordinal)
            .ToList();

        foreach (var (lang, locale) in GameTables.Languages)
        {
            var file = new JsonObject();
            foreach (var (row, id, english) in npcs)
            {
                bool dummy = Dummy().IsMatch(english!);
                bool boss = row.EnumValue("NpcSubType") is "HeroMonster" or "LegendMonster" || dummy && Scarecrow().IsMatch(row.Str("Name"));
                var entry = new JsonObject { ["name"] = tables.Text(locale, row.Key("Desc")) ?? english, ["isBoss"] = boss };
                if (dummy)
                {
                    entry["isDummy"] = true;
                }

                file[id.ToString()] = entry;
            }

            var old = Json.ReadAsset(Path.Combine(config.AssetsDir, "npcs", lang + ".json")) as JsonObject;
            if (lang == "en")
            {
                report.Compare("npcs/en.json names", Field(old, "name"), Field(file, "name")!, s => s);
                report.Compare("npcs/en.json isBoss", Field(old, "isBoss"), Field(file, "isBoss")!, s => s, examples: 10);
                report.Compare("npcs/en.json isDummy", Field(old, "isDummy"), Field(file, "isDummy")!, s => s);
            }
            else
            {
                report.Compare($"npcs/{lang}.json names", Field(old, "name"), Field(file, "name")!, s => s, examples: 2);
            }

            yield return new AssetFile($"npcs/{lang}.json", Json.Indented(file));
        }
    }

    private static Dictionary<string, string>? Field(JsonObject? file, string field) =>
        file?.ToDictionary(p => p.Key, p => p.Value?[field]?.ToString() ?? "false", StringComparer.Ordinal);

    [GeneratedRegex(@"Punching Bag|Scarecrow|^Balance Test|Level Scaling Test|^Test Monster|-Degree Arc")]
    private static partial Regex Dummy();

    [GeneratedRegex("TraDummy|Scarecrow")]
    private static partial Regex Scarecrow();
}

/// <summary>
/// skills/&lt;lang&gt;.json: skill id → name. The texts name most skills by id
/// (<c>SkillString_&lt;group&gt;_&lt;id&gt;_skill_name</c>); the rest take the name of their Skill row's SkillString.
/// A name missing in a language falls back to English.
/// </summary>
public static partial class SkillsBuilder
{
    public static IEnumerable<AssetFile> Build(GameTables tables, ToolConfig config, Report report)
    {
        var strings = tables.Rows("SkillString").ToDictionary(r => r.Str("Name"), r => r.Key("SkillName"), StringComparer.Ordinal);
        var rows = tables.Rows("Skill").ToDictionary(r => r.Id(), r => r.Str("SkillString_Key"));

        Dictionary<uint, string> Names(string locale)
        {
            var names = new Dictionary<uint, string>();
            foreach (var (key, _) in tables.Texts(locale))
            {
                var m = ById().Match(key);
                if (m.Success && uint.TryParse(m.Groups[1].Value, out uint id) && tables.RawText(locale, key) is { } text)
                {
                    names.TryAdd(id, text);
                }
            }

            foreach (var (id, stringKey) in rows)
            {
                if (!names.ContainsKey(id) && strings.TryGetValue(stringKey, out var textKey) && tables.RawText(locale, textKey) is { } text)
                {
                    names[id] = text;
                }
            }

            return names;
        }

        var english = Names("en-US");
        var ids = english.Keys.Select(i => i.ToString()).Order(StringComparer.Ordinal).ToList();
        foreach (var (lang, locale) in GameTables.Languages)
        {
            var names = lang == "en" ? english : Names(locale);
            var file = new JsonObject();
            foreach (string id in ids)
            {
                file[id] = names.GetValueOrDefault(uint.Parse(id)) ?? english[uint.Parse(id)];
            }

            var old = Report.Entries(Json.ReadAsset(Path.Combine(config.AssetsDir, "skills", lang + ".json")));
            report.Compare($"skills/{lang}.json", old, Report.Entries(file)!, s => s, examples: lang == "en" ? 8 : 2);
            yield return new AssetFile($"skills/{lang}.json", Json.Indented(file));
        }
    }

    [GeneratedRegex(@"^SkillString_\w+?_(\d+)_skill_name$")]
    private static partial Regex ById();
}

/// <summary>maps/&lt;lang&gt;.json from Map: map id → name (<c>Desc.Key</c>), English where a language has none.</summary>
public static class MapsBuilder
{
    public static IEnumerable<AssetFile> Build(GameTables tables, ToolConfig config, Report report)
    {
        var maps = tables.Rows("Map")
            .Select(r => (Id: r.Id(), Key: r.Key("Desc"), English: tables.Text("en-US", r.Key("Desc"))))
            .Where(m => m.English is not null)
            .OrderBy(m => m.Id)
            .ToList();
        foreach (var (lang, locale) in GameTables.Languages)
        {
            var file = new JsonObject();
            foreach (var (id, key, english) in maps)
            {
                file[id.ToString()] = tables.Text(locale, key) ?? english;
            }

            var old = Report.Entries(Json.ReadAsset(Path.Combine(config.AssetsDir, "maps", lang + ".json")));
            report.Compare($"maps/{lang}.json", old, Report.Entries(file)!, s => s, examples: 3);
            yield return new AssetFile($"maps/{lang}.json", Json.Indented(file) + "\n");
        }
    }
}

/// <summary>
/// dungeon_names.json from Dungeon and PartyDungeonLevel: dungeon id (the id a group-finder room carries) → the English
/// name the game shows, with the difficulty for party dungeons and raids ("Krao Cave (Exploration)") or the stage of a
/// Transcendence dungeon ("Submerged Life Temple (Stage 2)"). The NPC tables have no dungeon column, so the group
/// finder names rooms from this file.
/// </summary>
public static class DungeonNamesBuilder
{
    public static IEnumerable<AssetFile> Build(GameTables tables, ToolConfig config, Report report)
    {
        var stages = tables.Rows("PartyDungeonLevel").ToDictionary(r => r.Str("DungeonName"), r => r.Long("DungeonLevel"), StringComparer.Ordinal);
        string stage = tables.Text("en-US", "UI_PARTYDUNGEON_PARTYCHALLENGE_GRADE_INFO_TEXT") is { } s && s.Contains("{0}", StringComparison.Ordinal)
            ? s
            : throw new GameDataFormatException("no English text for a Transcendence stage");

        var names = new JsonObject();
        foreach (var row in tables.Rows("Dungeon").OrderBy(r => r.Id()))
        {
            if (tables.Text("en-US", row.Key("Title")) is not { } title)
            {
                continue;
            }

            string? suffix = null;
            string type = row.EnumValue("DungeonType");
            if (type is "Party" or "Raid")
            {
                // Party dungeons say Exploration / Conquest / Conquest [Hard], raids Normal / Hard
                string difficulty = row.EnumValue("DungeonDifficulty").ToUpperInvariant();
                suffix = stages.TryGetValue(row.Str("Name"), out long level) ? stage.Replace("{0}", level.ToString(), StringComparison.Ordinal)
                    : difficulty == "NONE" ? null
                    : (type == "Raid"
                          ? tables.Text("en-US", "UI_PARTYDUNGEON_RAID_DIFFICULTY_" + difficulty) ?? tables.Text("en-US", "UI_RAID_HALLOFFAME_DIFFICULTY_" + difficulty)
                          : tables.Text("en-US", "STR_DUNGEONDIFFICULTY_" + difficulty))
                      ?? throw new GameDataFormatException($"dungeon {row.Id()}: no English text for difficulty {difficulty}");
            }

            names[row.Id().ToString()] = suffix is null ? title : $"{title} ({suffix})";
        }

        var file = new JsonObject
        {
            ["source"] = "AION2 client tables Dungeon, PartyDungeonLevel and the English texts (tools/extract_gamedata)",
            ["built"] = tables.ClientDate.ToString("yyyy-MM-dd"),
            ["dungeons"] = names,
        };

        var old = Json.ReadAsset(Path.Combine(config.AssetsDir, "dungeon_names.json"));
        report.Compare("dungeon_names.json", Report.Entries(old?["dungeons"]), Report.Entries(names)!, s => s, examples: 8);
        yield return new AssetFile("dungeon_names.json", Json.Indented(file) + "\n");
    }
}

/// <summary>items.json from Item: item id → English name, every named item.</summary>
public static class ItemsBuilder
{
    public static IEnumerable<AssetFile> Build(GameTables tables, ItemIndex items, ToolConfig config, Report report)
    {
        var names = new JsonObject();
        foreach (var row in items.Rows.OrderBy(r => r.Id()))
        {
            if (items.Name(row) is { } name)
            {
                names[row.Id().ToString()] = name;
            }
        }

        var file = new JsonObject
        {
            ["source"] = "AION2 client tables Item and the English texts (tools/extract_gamedata)",
            ["built"] = tables.ClientDate.ToString("yyyy-MM-dd"),
            ["items"] = names,
        };
        var old = Json.ReadAsset(Path.Combine(config.AssetsDir, "items.json"));
        report.Compare("items.json", Report.Entries(old?["items"]), Report.Entries(names)!, s => s, examples: 8);
        yield return new AssetFile("items.json", Json.Compact(file));
    }
}

/// <summary>
/// field_bosses.json from WorldMapFieldNamed: the field boss code the server's field boss tables use (the row id) →
/// the boss's NPC code (its <c>FieldNamedName</c> in NpcData).
/// </summary>
public static class FieldBossesBuilder
{
    public static IEnumerable<AssetFile> Build(GameTables tables, ToolConfig config, Report report)
    {
        var npcByKey = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var npc in tables.Rows("NpcData"))
        {
            npcByKey.TryAdd(npc.Str("Name"), npc.Id());
        }

        var file = new JsonObject();
        foreach (var row in tables.Rows("WorldMapFieldNamed").OrderBy(r => r.Id()))
        {
            string key = row.Str("FieldNamedName");
            file[row.Id().ToString()] = npcByKey.TryGetValue(key, out uint npc) ? npc : throw new GameDataFormatException($"field boss {row.Id()}: NPC {key} is not in NpcData");
        }

        var old = Report.Entries(Json.ReadAsset(Path.Combine(config.AssetsDir, "field_bosses.json")));
        report.Compare("field_bosses.json", old, Report.Entries(file)!, s => s, examples: 10);
        yield return new AssetFile("field_bosses.json", Json.Indented(file));
    }
}
