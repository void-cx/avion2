using System.Text.Json.Nodes;
using ExtractGameData.Core;

namespace ExtractGameData;

/// <summary>
/// gear_forge.json from Item (EnchantGroup, ExceedEnchantGroup), Enchant and ExceedEnchant: every named equipment item
/// with a ladder, and the ladders they use. Chances are in 1/10000 in the tables (10000 = 100 %).
/// </summary>
public static class GearForgeBuilder
{
    private static readonly string[] GradeTokens = ["Common", "Rare", "Legend", "Unique", "Epic", "Mythic", "Special"];

    public static IEnumerable<AssetFile> Build(GameTables tables, ItemIndex items, ToolConfig config, Report report)
    {
        var enchant = tables.Rows("Enchant").GroupBy(r => r.Str("Group")).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var amplify = tables.Rows("ExceedEnchant").GroupBy(r => r.Str("Group")).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var categoryNames = tables.Texts("en-US")
            .Where(t => t.Key.StartsWith("String_STR_ITEM_TOOLTIP_", StringComparison.Ordinal) && t.Key.EndsWith("_body", StringComparison.Ordinal))
            .GroupBy(t => t.Key["String_STR_ITEM_TOOLTIP_".Length..^"_body".Length], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);

        var list = new JsonArray();
        var usedEnchant = new SortedSet<string>(StringComparer.Ordinal);
        var usedAmplify = new SortedSet<string>(StringComparer.Ordinal);
        var gradeOfGroup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in items.Rows.Where(r => r.EnumValue("ItemType") == "Equip").OrderBy(r => r.Id()))
        {
            string? e = row["EnchantGroup"]?.GetValue<string>() is { } eg && enchant.ContainsKey(eg) ? eg : null;
            string? a = row["ExceedEnchantGroup"]?.GetValue<string>() is { } ag && amplify.ContainsKey(ag) ? ag : null;
            if (e is null && a is null || items.Name(row) is not { } name)
            {
                continue;
            }

            string grade = items.Grade(row) ?? throw new GameDataFormatException($"item {row.Id()}: no grade name");
            string category = row.EnumValue("EquipCategory");
            string kind = $"{grade} {(categoryNames.TryGetValue(category, out var c) ? c : category)}";
            list.Add(new JsonObject { ["id"] = row.Id(), ["name"] = name, ["kind"] = kind, ["enchant"] = e, ["amplify"] = a });
            foreach (string g in new[] { e, a }.OfType<string>())
            {
                gradeOfGroup.TryAdd(g, grade);
            }

            if (e is not null)
            {
                usedEnchant.Add(e);
            }

            if (a is not null)
            {
                usedAmplify.Add(a);
            }
        }

        JsonObject Ladders(SortedSet<string> used, Dictionary<string, List<JsonObject>> groups)
        {
            var result = new JsonObject();
            foreach (string group in used)
            {
                string? token = group.Split('_').FirstOrDefault(t => GradeTokens.Contains(t, StringComparer.Ordinal));
                string grade = token is not null ? ItemIndex.GradeName(tables, token) ?? token : gradeOfGroup[group];
                var steps = new JsonArray();
                // the last row of a ladder is the top level: no chance, no cost, nothing to attempt
                foreach (var s in groups[group].OrderBy(s => s.Long("CurrentLevel"))
                             .Where(s => s.Long("SuccessProb") > 0 || s.Long("CostGold") > 0 || s.Objects("CostItems").Any()))
                {
                    steps.Add(new JsonObject
                    {
                        ["level"] = s.Long("CurrentLevel"),
                        ["chance"] = Percent(s.Long("SuccessProb")),
                        ["pity"] = Percent(s.Long("FailCorrectionProb")),
                        ["gold"] = s.Long("CostGold"),
                        ["materials"] = new JsonArray(s.Objects("CostItems").Select(m =>
                        {
                            uint id = items.Id(m.Str("Item"));
                            return (JsonNode) new JsonObject { ["name"] = items.Name(id), ["count"] = m.Long("Count"), ["id"] = id };
                        }).ToArray()),
                    });
                }

                result[group] = new JsonObject { ["grade"] = grade, ["steps"] = steps };
            }

            return result;
        }

        var file = new JsonObject { ["items"] = list, ["enchant"] = Ladders(usedEnchant, enchant), ["amplify"] = Ladders(usedAmplify, amplify) };

        var old = Json.ReadAsset(Path.Combine(config.AssetsDir, "gear_forge.json"));
        static Dictionary<string, string>? ItemsById(JsonNode? n) => n is JsonArray a
            ? a.GroupBy(i => i!["id"]!.ToString()).ToDictionary(g => g.Key, g => Json.Compact(g.First()!), StringComparer.Ordinal)
            : null;
        report.Compare("gear_forge.json items", ItemsById(old?["items"]), ItemsById(list)!, s => s, examples: 3);
        report.Compare("gear_forge.json enchant ladders", Report.Entries(old?["enchant"]), Report.Entries(file["enchant"])!, s => s.Length > 120 ? s[..120] + "…" : s, examples: 3);
        report.Compare("gear_forge.json amplify ladders", Report.Entries(old?["amplify"]), Report.Entries(file["amplify"])!, s => s.Length > 120 ? s[..120] + "…" : s, examples: 3);
        yield return new AssetFile("gear_forge.json", Json.Compact(file));
    }

    // 1/10000 → percent; whole percents as integers like the current file
    private static JsonNode Percent(long value) => value % 100 == 0 ? JsonValue.Create(value / 100)! : JsonValue.Create(value / 100.0)!;
}

/// <summary>
/// Checks cube_rewards.json (built from SkeeveAN's published copy of RewardOdEnergyCube) against the client's own
/// RewardOdEnergyCube table: Odyle per share, bosses, guaranteed reward. It writes nothing; the file keeps its
/// dungeon and mode names, which the client table does not have.
/// </summary>
public static class CubeRewardsCheck
{
    public static void Run(GameTables tables, ItemIndex items, ToolConfig config, Report report)
    {
        var npcByKey = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var npc in tables.Rows("NpcData"))
        {
            npcByKey.TryAdd(npc.Str("Name"), npc.Id());
        }

        uint Npc(string key) => npcByKey.TryGetValue(key, out uint id) ? id : throw new GameDataFormatException($"NPC {key} is not in NpcData");
        var cubes = tables.Rows("RewardOdEnergyCube");
        var byFinal = cubes.GroupBy(c => Npc(c.Str("FinalBossName"))).ToDictionary(g => g.Key, g => g.ToList());

        var old = Json.ReadAsset(Path.Combine(config.AssetsDir, "cube_rewards.json"))?["cubes"] as JsonArray ?? [];
        int same = 0;
        var problems = new List<string>();
        var matched = new HashSet<uint>();
        foreach (var cube in old.OfType<JsonObject>())
        {
            string label = $"{cube["dungeon"]} {cube["mode"]}";
            // Unfinished cubes reuse another dungeon's boss; then the row named after the dungeon is the one
            var rows = cube["final"]?.GetValue<uint>() is { } f && byFinal.TryGetValue(f, out var r) ? r : [];
            if (rows.Count > 1)
            {
                rows = rows.Where(c => c.Str("Name").StartsWith($"reward_{cube["dungeon"]}_", StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (rows.Count != 1)
            {
                problems.Add($"{label}: no single client row with final boss {cube["final"]}");
                continue;
            }

            uint final = cube["final"]!.GetValue<uint>();
            var row = rows[0];
            matched.Add(row.Id());
            var diffs = new List<string>();
            if (cube["odyle"]?.GetValue<long>() != row.Long("ConsumeTicketCount"))
            {
                diffs.Add($"odyle {cube["odyle"]} vs {row.Long("ConsumeTicketCount")}");
            }

            var npcs = row.Objects("RewardMiddleBossList").Select(b => Npc(b.Str("BossName"))).Append(final).Order().ToList();
            var oldNpcs = (cube["npcs"] as JsonArray)?.Select(n => n!.GetValue<uint>()).Order().ToList() ?? [];
            if (!npcs.SequenceEqual(oldNpcs))
            {
                diffs.Add($"npcs [{string.Join(",", oldNpcs)}] vs [{string.Join(",", npcs)}]");
            }

            var ceilingItems = row.Strings("CeilingItems").Select(items.Id).ToList();
            if (cube["ceiling"] is JsonObject ceiling)
            {
                if (ceiling["after"]?.GetValue<long>() != row.Long("CeilingMaxCount") || ceiling["claims"]?.GetValue<long>() != row.Long("CeilingRewardCount"))
                {
                    diffs.Add($"ceiling {ceiling["after"]}/{ceiling["claims"]} vs {row.Long("CeilingMaxCount")}/{row.Long("CeilingRewardCount")}");
                }

                var oldItems = (ceiling["items"] as JsonArray)?.Select(n => n!.GetValue<uint>()).ToList() ?? [];
                if (!oldItems.Order().SequenceEqual(ceilingItems.Order()))
                {
                    diffs.Add($"ceiling items [{string.Join(",", oldItems)}] vs [{string.Join(",", ceilingItems)}]");
                }
            }
            else if (ceilingItems.Count > 0)
            {
                diffs.Add($"no ceiling vs {row.Long("CeilingMaxCount")}/{row.Long("CeilingRewardCount")} with {ceilingItems.Count} items");
            }

            if (diffs.Count == 0)
            {
                same++;
            }
            else
            {
                problems.Add($"{label}: {string.Join("; ", diffs)}");
            }
        }

        report.Add("cube_rewards.json", $"kept (not rebuilt): {same} of {old.Count} cubes match the client's RewardOdEnergyCube ({cubes.Count} rows, " +
                                        $"{cubes.Count(c => !matched.Contains(c.Id()))} not in the file)");
        foreach (string p in problems)
        {
            report.Add("cube_rewards.json", "  " + p);
        }
    }
}
