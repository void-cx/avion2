"""Builds assets/gamedata/gear_stats.json from aion2hub's build planner (item level and stats per enchant level).

Download https://aion2hub.com/tools/build-calculator and every /_next/static/chunks/*.js it loads into one
folder first; the planner data sits in two of those chunks as JSON.parse('...') modules.

usage: python -I build_gear_stats.py <chunks dir> <gear_forge.json> <out json>
"""
import json
import os
import re
import sys

chunks_dir, forge_path, out = sys.argv[1], sys.argv[2], sys.argv[3]

MODULE = re.compile(r"(\d+),\(\w,(\w),\w\)=>\{\2\.exports=JSON\.parse\('")


def modules(text):
    """Module id -> parsed JSON for every JSON.parse('...') module in a chunk."""
    found = {}
    for match in MODULE.finditer(text):
        i = match.end()
        start = i
        while True:
            if text[i] == "\\":
                i += 2
                continue
            if text[i] == "'":
                break
            i += 1
        raw = text[start:i]
        # a JS single-quoted string: undo its escapes, then parse the JSON inside
        found[int(match.group(1))] = json.loads(raw.encode("utf-8").decode("unicode_escape").encode("latin-1").decode("utf-8"))
    return found


texts = {}
for name in os.listdir(chunks_dir):
    if name.endswith(".js"):
        texts[name] = open(os.path.join(chunks_dir, name), encoding="utf-8").read()

bundle = next(t for t in texts.values() if 'id:"planner-bundle/global"' in t)
wiring = re.search(r'id:"planner-bundle/global"[^}]*\}', bundle).group(0)
imports = dict(re.findall(r"(\w)=\w\.i\((\d+)\)", bundle))


def module_id(field):
    letter = re.search(field + r":(\w)\.default", wiring).group(1)
    return int(imports[letter])


everything = {}
for text in texts.values():
    if "JSON.parse('" in text:
        everything.update(modules(text))

items = everything[module_id("items")]
enchant_effects = everything[module_id("enchantEffects")]
forge = json.load(open(forge_path, encoding="utf-8"))

out_items = {}
used_effects = set()
for entry in forge["items"]:
    item = items.get(str(entry["id"]))
    if item is None:
        continue
    effect = item.get("enchantEffectGroup")
    out_items[str(entry["id"])] = {"itemLevel": item["itemLevel"], "slot": item.get("slot"), "effect": effect}
    if effect:
        used_effects.add(effect)

# cumulative stats per level: {"group": {"1": {"stat": value}, ...}}
effects = {}
for group in sorted(used_effects):
    levels = enchant_effects.get(group)
    if not levels:
        continue
    effects[group] = {lv: {s["stat"]: s["value"] for s in stats} for lv, stats in sorted(levels.items(), key=lambda kv: int(kv[0]))}

data = {"items": out_items, "effects": effects}
with open(out, "w", encoding="utf-8", newline="\n") as f:
    json.dump(data, f, ensure_ascii=False, separators=(",", ":"))
print(f"items {len(out_items)} of {len(forge['items'])}, effect groups {len(effects)} of {len(used_effects)}")
