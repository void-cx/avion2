"""Map marker icons: download from aion2hub, convert to PNG, reference them from each map's categories.

usage: venv python -I map_icons.py <hub mapjson dir> <download dir> <repo>
"""
import glob
import json
import os
import sys
import urllib.request

from PIL import Image

mapdir, downloads, repo = sys.argv[1:4]
world = os.path.join(repo, "assets", "gamedata", "world")
icons_out = os.path.join(world, "icons")
os.makedirs(downloads, exist_ok=True)
os.makedirs(icons_out, exist_ok=True)

# subtype -> icon path, per map key
per_map = {}
for f in glob.glob(os.path.join(mapdir, "*.json")):
    d = json.load(open(f, encoding="utf-8"))
    per_map[d["map"]] = {s["key"]: s["icon"] for c in d["categories"] for s in c["subtypes"] if s.get("icon")}

paths = sorted({p for m in per_map.values() for p in m.values()})
names = {}
for path in paths:
    name = os.path.splitext(os.path.basename(path))[0]
    names[path] = name + ".png"
    raw = os.path.join(downloads, os.path.basename(path))
    if not os.path.exists(raw):
        req = urllib.request.Request("https://aion2hub.com/maps-assets/" + path, headers={"User-Agent": "Mozilla/5.0"})
        with urllib.request.urlopen(req, timeout=30) as r, open(raw, "wb") as out:
            out.write(r.read())
    img = Image.open(raw).convert("RGBA")
    img.thumbnail((48, 48), Image.LANCZOS)
    img.save(os.path.join(icons_out, names[path]), optimize=True)
print("icons:", len(paths))

for f in glob.glob(os.path.join(world, "*.json")):
    d = json.load(open(f, encoding="utf-8"))
    icons = per_map.get(d["key"], {})
    for c in d["categories"]:
        if c["key"] in icons:
            c["icon"] = names[icons[c["key"]]]
    with open(f, "w", encoding="utf-8", newline="\n") as out:
        json.dump(d, out, ensure_ascii=False, separators=(",", ":"))
    print(d["key"], sum(1 for c in d["categories"] if "icon" in c), "categories with an icon")
