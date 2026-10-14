"""Builds assets/gamedata/world/<key>.json + <key>.jpg from aion2hub map data and tiles.

usage: venv python -I build_maps.py <mapjson dir> <tiles dir> <out dir>

- Tiles are named <key>_<col>_<row>.webp (1024 px). The image is stitched and saved at half resolution.
- aion2hub marker y runs bottom-up; here every coordinate is in image pixels (top-down) of the full-size map
  (cols * 1024), so markers and regions line up with the picture.
"""
import glob
import json
import os
import sys

from PIL import Image

mapdir, tiledir, out = sys.argv[1:4]
os.makedirs(out, exist_ok=True)

MAPS = {
    "World_L_Starter": (1000, "Poeta"),
    "World_L_A": (1010, "Verteron"),
    "World_L_B": (1011, "Eltnen"),
    "World_D_Starter": (1100, "Ishalgen"),
    "World_D_A": (1110, "Altgard"),
    "World_D_B": (1111, "Morheim"),
    "Abyss_Reshanta_A": (20, "Chaotic Lower Reshanta"),
    "Abyss_Reshanta_B": (22, "Chaotic Middle Reshanta"),
}

# world (cm) -> image px, fitted from 19 Verteron field bosses (0.3 px mean error); other maps calibrate at runtime
TRANSFORMS = {
    "World_L_A": {"scale": 0.009604, "x0": 4094.7, "y0": 4032.0},
}

hub = {}
for f in glob.glob(os.path.join(mapdir, "*.json")):
    d = json.load(open(f, encoding="utf-8"))
    hub[d["map"]] = d

index = []
for key, (map_id, name) in MAPS.items():
    d = hub.get(key)
    n = d["cols"] if d else {"World_L_Starter": 4, "World_D_Starter": 4}[key]
    size = n * 1024

    img = Image.new("RGB", (size // 2, size // 2))
    for col in range(n):
        for row in range(n):
            tile = Image.open(os.path.join(tiledir, key, f"{key}_{col:02d}_{row:02d}.webp")).convert("RGB").resize((512, 512), Image.LANCZOS)
            img.paste(tile, (col * 512, row * 512))
    img.save(os.path.join(out, f"{key}.jpg"), quality=82, optimize=True, progressive=False)

    categories, markers, regions, drops = [], [], [], {}
    if d:
        for c in d["categories"]:
            for s in c["subtypes"]:
                categories.append({"group": c["name"], "groupLabel": c["label"], "key": s["key"], "label": s["label"]})
        for m in d["markers"]:
            marker = {"n": m["n"], "s": m["s"], "x": m["x"], "y": size - m["y"]}
            if m["c"] == "collection":
                marker["id"] = m["id"]  # so found cubes / materials can be checked off
            for k in ("lv", "rank", "k"):
                if m.get(k) is not None:
                    marker[k] = m[k]
            markers.append(marker)
        for r in d.get("regions", []):
            regions.append({"name": r["name"], "borders": [[[x, size - y] for x, y in ring] for ring in r["borders"]]})
        used = {m.get("k") for m in markers}
        drops = {k: v for k, v in d.get("drops", {}).items() if k in used}

    data = {
        "key": key, "mapId": map_id, "name": name, "faction": d["faction"] if d else None, "size": size,
        "transform": TRANSFORMS.get(key), "categories": categories, "markers": markers, "regions": regions, "drops": drops,
    }
    with open(os.path.join(out, f"{key}.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, ensure_ascii=False, separators=(",", ":"))
    index.append(key)
    print(key, map_id, len(markers), "markers", len(regions), "regions",
          os.path.getsize(os.path.join(out, f"{key}.jpg")) // 1024, "KB jpg",
          os.path.getsize(os.path.join(out, f"{key}.json")) // 1024, "KB json")
