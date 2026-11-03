"""Builds assets/gamedata/cube_rewards.json: the reward cube of each expedition and mode (the client's
RewardOdEnergyCube table): its bosses (NPC codes), the Odyle one share of the cube costs, and the guaranteed reward
(how many points it takes, how often it can be claimed, the items to pick from).

The table comes from SkeeveAN/Aion-DPS-Meter (MIT), which publishes it as Backend/src/data/aion2/boss_loot.json and
the bosses' NPC codes and English names as Backend/src/data/aion2/bosses.json. Clone that repository first.

usage: python -I build_cube_rewards.py <SkeeveAN repo dir> <assets/gamedata/dungeons.json> <out json>
"""
import json
import os
import re
import sys

repo, dungeons_path, out = sys.argv[1], sys.argv[2], sys.argv[3]
data = os.path.join(repo, "Backend", "src", "data", "aion2")
loot = json.load(open(os.path.join(data, "boss_loot.json"), encoding="utf-8"))
bosses = json.load(open(os.path.join(data, "bosses.json"), encoding="utf-8"))["items"]
dungeons = json.load(open(dungeons_path, encoding="utf-8"))["dungeons"]

boss_name = {}
for boss in bosses:
    for npc in boss["npcIds"]:
        boss_name[npc] = boss["name"]["en"]

# our dungeon table by boss name (English), to name a cube after the dungeon AVION2 already knows
slug_by_boss = {}
for dungeon in dungeons:
    for boss in dungeon["bosses"]:
        slug_by_boss.setdefault(boss["name"].lower(), dungeon)


def split_camel(key):
    return re.sub(r"(?<=[a-z])(?=[A-Z])", " ", key)


def mode_name(mode):
    names = {k: v["en"] for k, v in loot["modes"].items()}
    if mode in names:
        return names[mode]
    match = re.fullmatch(r"grade(\d+)", mode)
    return f"Grade {match.group(1)}" if match else mode


cubes = {}
for entry in loot["bosses"].values():
    for row in entry["loot"]:
        key = (row["dungeon"], row["mode"])
        cube = cubes.setdefault(key, {"dungeon": row["dungeon"], "mode": row["mode"], "odyle": row["odyleCost"],
                                      "ceiling": row["ceiling"], "final": None, "npcs": set()})
        cube["npcs"].add(row["npcId"])
        if row["role"] == "final":
            cube["final"] = row["npcId"]

result = []
for (dungeon_key, mode), cube in sorted(cubes.items()):
    named = None
    for npc in [cube["final"], *sorted(cube["npcs"])]:
        if npc is not None and boss_name.get(npc, "").lower() in slug_by_boss:
            named = slug_by_boss[boss_name[npc].lower()]
            break
    if named is None:
        named = next((d for d in dungeons if d["name"].lower() == split_camel(dungeon_key).lower()), None)
    ceiling = cube["ceiling"]
    result.append({
        "dungeon": dungeon_key,
        "slug": named["slug"] if named else None,
        "name": named["name"] if named else split_camel(dungeon_key),
        "mode": mode,
        "modeName": mode_name(mode),
        "odyle": cube["odyle"],
        "final": cube["final"],
        "npcs": sorted(cube["npcs"]),
        "ceiling": {"after": ceiling["after"], "claims": ceiling["rewards"], "items": [i["itemId"] for i in ceiling["items"]]}
        if ceiling else None,
    })

json.dump({"source": "https://github.com/SkeeveAN/Aion-DPS-Meter (Backend/src/data/aion2/boss_loot.json, bosses.json)",
           "table": loot["_meta"]["source"], "cubes": result},
          open(out, "w", encoding="utf-8", newline="\n"), ensure_ascii=False, indent=1)
print(f"{len(result)} cubes")
