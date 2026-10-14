"""Builds assets/gamedata/gear_forge.json from aion2hub's Gear Forge page + search index.

usage: python -I build_forge.py <hub dir> <out json>
"""
import json
import os
import re
import sys

hub, out = sys.argv[1], sys.argv[2]
html = open(os.path.join(hub, "forge.html"), encoding="utf-8").read()

# Next.js flight data: self.__next_f.push([1,"<json string>"])
chunks = re.findall(r'self\.__next_f\.push\(\[1,("(?:[^"\\]|\\.)*")\]\)', html)
flight = "".join(json.loads(c) for c in chunks)

start = flight.index('{"idKey"')
depth, i, in_str, esc = 0, start, False, False
while True:
    c = flight[i]
    if in_str:
        if esc:
            esc = False
        elif c == "\\":
            esc = True
        elif c == '"':
            in_str = False
    elif c == '"':
        in_str = True
    elif c in "{[":
        depth += 1
    elif c in "}]":
        depth -= 1
        if depth == 0:
            break
    i += 1
props = json.loads(flight[start:i + 1])

index = json.load(open(os.path.join(hub, "search-index.json"), encoding="utf-8"))
names = {}
ids_by_name = {}
for entry in index:
    if entry.get("t") == "item":
        item_id = entry["u"].rsplit("/", 1)[-1]
        names[item_id] = (entry["n"], entry.get("s", ""))
        ids_by_name.setdefault(entry["n"], int(item_id))


def ladder(raw):
    return {
        "grade": raw["grade"],
        "steps": [
            {
                "level": s["lv"],
                "chance": s["s"],
                "pity": s["p"],
                "gold": s["g"],
                "materials": [{"name": m["n"], "count": m["c"], "id": ids_by_name.get(m["n"])} for m in s["mats"]],
            }
            for s in raw["steps"]
        ],
    }


items = []
ids = sorted(set(props["idKey"]) | set(props["exceedIdKey"]), key=int)
missing = 0
for item_id in ids:
    if item_id not in names:
        missing += 1
        continue
    name, kind = names[item_id]
    items.append({
        "id": int(item_id),
        "name": name,
        "kind": kind,
        "enchant": props["idKey"].get(item_id),
        "amplify": props["exceedIdKey"].get(item_id),
    })

data = {
    "items": items,
    "enchant": {k: ladder(v) for k, v in props["ladders"].items()},
    "amplify": {k: ladder(v) for k, v in props["exceedLadders"].items()},
}
with open(out, "w", encoding="utf-8", newline="\n") as f:
    json.dump(data, f, ensure_ascii=False, separators=(",", ":"))
print(f"items {len(items)} (no name: {missing}), enchant ladders {len(data['enchant'])}, amplify ladders {len(data['amplify'])}")
