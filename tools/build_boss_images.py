"""Builds assets/gamedata/bosses/: portraits of the world bosses and field bosses, and bosses.json saying which
boss shows which portrait.

The portraits are the game's own (one per boss model, shared by bosses that use the same model), as aion2hub.com
publishes them: /world-bosses/<model>.webp (256 px) on its world boss page and /maps-assets/mobs/MOB_<model>.webp
(128 px) as the "por" of named monsters in its map data. Neither carries an NPC code, so a boss is matched by its
English name (our npcs/en.json) against the names those pages give, on the boss's own map where the field boss
table tells it (spawn code = map id x 100 + n). A boss gets a portrait only when every page that names it agrees on
one; bosses whose name has no portrait, or more than one, get none.

usage: venv python -I build_boss_images.py <download dir> <repo>

Downloads (cached in <download dir>, re-run to reuse): the world boss page, the map page, its script chunks and the
portraits. Output: <repo>/assets/gamedata/bosses/<model>.png (at most 128 px, RGBA) and bosses.json.
"""
import hashlib
import io
import json
import os
import re
import sys
import time
import urllib.request

from PIL import Image

SITE = "https://aion2hub.com"
SIZE = 128

# Timers page world boss rows (GameSchedule.cs keys) -> the bosses they stand for, as the world boss page names them
WORLD_BOSS_EVENTS = {
    "wb-watcher-kaira": ["Watcher Kaira"],
    "wb-executors": ["Executor Argo", "Executor Kaira", "Executor Tamasa"],
    "wb-siege": ["Abyss Siege Boss"],
    "wb-nahma": ["Enraged Guardian Lord Nahma"],
    "wb-middle": ["Executioner Dramos", "Ravager Marakha", "Turncoat Ducal"],
}


def fetch(downloads, path):
    """The bytes at SITE + path, from the cache when downloaded before."""
    local = os.path.join(downloads, re.sub(r"[^A-Za-z0-9_.\-]", "_", path.strip("/")) or "index")
    if not os.path.exists(local):
        req = urllib.request.Request(SITE + path, headers={"User-Agent": "AVION2"})
        with urllib.request.urlopen(req, timeout=30) as r:
            data = r.read()
        with open(local, "wb") as f:
            f.write(data)
        time.sleep(0.5)  # polite
    with open(local, "rb") as f:
        return f.read()


def js_unescape(s):
    """Decodes the body of a single-quoted JavaScript string literal."""
    out, i = [], 0
    while i < len(s):
        c = s[i]
        if c != "\\":
            out.append(c)
            i += 1
            continue
        n = s[i + 1]
        if n == "u":
            out.append(chr(int(s[i + 2:i + 6], 16)))
            i += 6
        elif n == "x":
            out.append(chr(int(s[i + 2:i + 4], 16)))
            i += 4
        else:
            out.append({"n": "\n", "t": "\t", "r": "\r", "b": "\b", "f": "\f", "v": "\v", "0": "\0"}.get(n, n))
            i += 2
    return "".join(out)


def map_markers(chunk):
    """(map key, markers) from a map data chunk: JSON.parse('...') for the big maps, an object literal for small ones."""
    m = re.search(r"JSON\.parse\('((?:[^'\\]|\\.)*)'\)", chunk)
    if m and '"markers"' in m.group(1):
        d = json.loads(js_unescape(m.group(1)))
        return d["map"], d["markers"]
    m = re.search(r'\{map:"([A-Za-z_]+)"', chunk)
    if not m:
        return None, []
    markers = []
    for obj in re.finditer(r'\{id:"[^"]*",n:"((?:[^"\\]|\\.)*)"((?:,[a-z]+:(?:"(?:[^"\\]|\\.)*"|-?[0-9.]+|!0|!1))*)\}', chunk):
        marker = {"n": json.loads('"' + obj.group(1) + '"')}
        for key, value in re.findall(r',([a-z]+):"((?:[^"\\]|\\.)*)"', obj.group(2)):
            marker[key] = json.loads('"' + value + '"')
        markers.append(marker)
    return m.group(1), markers


def model_of(path):
    """'mobs/MOB_BeritraD_01.webp' or '/world-bosses/BeritraD_01.webp' -> 'BeritraD_01'."""
    name = os.path.splitext(os.path.basename(path))[0]
    return name[4:] if name.startswith("MOB_") else name


def main():
    downloads, repo = sys.argv[1:3]
    os.makedirs(downloads, exist_ok=True)
    gamedata = os.path.join(repo, "assets", "gamedata")
    out_dir = os.path.join(gamedata, "bosses")
    os.makedirs(out_dir, exist_ok=True)

    # 1. World boss page: <img src="/world-bosses/<model>.webp" alt="<boss>"> (and MOB_ portraits of their adds)
    page = fetch(downloads, "/tools/world-bosses").decode("utf-8")
    world_page = {}  # boss name -> set of (model, url path)
    for src, alt in re.findall(r'<img src="(/(?:world-bosses|maps-assets/mobs)/[^"]+\.webp)" alt="([^"]+)"', page):
        name = alt.replace("&amp;", "&").replace("&#x27;", "'").replace("&#39;", "'")
        world_page.setdefault(name, set()).add((model_of(src), src))

    # 2. Map data: the map page's chunks import one chunk per map; named monsters there carry "por"
    maps_page = fetch(downloads, "/maps").decode("utf-8")
    chunks = set(re.findall(r'/_next/static/chunks/[A-Za-z0-9_\-]+\.js', maps_page))
    lazy = set()
    for c in chunks:
        lazy.update("/_next/" + p for p in re.findall(r'static/chunks/[A-Za-z0-9_\-]+\.js', fetch(downloads, c).decode("utf-8")))
    on_map = {}  # map key -> boss name -> set of (model, url path)
    for c in sorted(chunks | lazy):
        key, markers = map_markers(fetch(downloads, c).decode("utf-8"))
        for marker in markers:
            if key and marker.get("por") and marker.get("n"):
                por = marker["por"]
                on_map.setdefault(key, {}).setdefault(marker["n"], set()).add((model_of(por), "/maps-assets/" + por))
    print("maps with portraits:", {k: len(v) for k, v in sorted(on_map.items())})

    # 3. Our bosses: field bosses (spawn code -> NPC code) and the world boss rows of the Timers page
    npcs = json.load(open(os.path.join(gamedata, "npcs", "en.json"), encoding="utf-8"))
    field = json.load(open(os.path.join(gamedata, "field_bosses.json"), encoding="utf-8"))
    map_key = {}  # map id -> world key
    for f in os.listdir(os.path.join(gamedata, "world")):
        if f.endswith(".json"):
            d = json.load(open(os.path.join(gamedata, "world", f), encoding="utf-8"))
            map_key[d["mapId"]] = d["key"]

    def candidates(name, key):
        found = set(world_page.get(name, set()))
        for k, names in on_map.items():
            if key is None or k == key:
                found |= names.get(name, set())
        return found

    picked = {}  # model -> url path (the largest source)
    npc_models, unmatched = {}, []
    for spawn, npc in sorted(field.items(), key=lambda kv: int(kv[0])):
        code = int(spawn)
        name = npcs.get(str(npc), {}).get("name")
        key = map_key.get(code // 100) if code < 1_000_000 else None  # 7-digit codes are NPC codes: map unknown
        found = candidates(name, key) if name else set()
        models = {m for m, _ in found}
        if len(models) != 1:
            unmatched.append(f"{spawn} {npc} {name!r} on {key or 'any map'}: {sorted(models) or 'no portrait'}")
            continue
        model = models.pop()
        npc_models[str(npc)] = model
        for m, src in found:
            if src.startswith("/world-bosses/") or model not in picked:
                picked[model] = src

    # Map marker names that show a portrait on the Map page (only the bosses matched here; a name that two
    # bosses with different portraits share shows none)
    by_name = {}
    for npc, model in npc_models.items():
        by_name.setdefault(npcs[npc]["name"], set()).add(model)

    event_models = {}
    for event, bosses in WORLD_BOSS_EVENTS.items():
        models = []
        for name in bosses:
            found = candidates(name, None)
            if len({m for m, _ in found}) != 1:
                unmatched.append(f"{event} {name!r}: {sorted({m for m, _ in found}) or 'no portrait'}")
                continue
            model = next(iter(found))[0]
            models.append(model)
            by_name.setdefault(name, set()).add(model)
            for _, src in found:
                if src.startswith("/world-bosses/") or model not in picked:
                    picked[model] = src
        if models:
            event_models[event] = models
    names = {name: models.pop() for name, models in by_name.items() if len(models) == 1}

    # 4. Portraits: download, shrink to SIZE, save as PNG; models with the very same picture share one file
    for f in os.listdir(out_dir):
        if f.endswith(".png"):
            os.remove(os.path.join(out_dir, f))
    total, file_of, by_bytes = 0, {}, {}
    for model, src in sorted(picked.items()):
        img = Image.open(io.BytesIO(fetch(downloads, src))).convert("RGBA")
        img.thumbnail((SIZE, SIZE), Image.LANCZOS)
        buffer = io.BytesIO()
        img.save(buffer, format="PNG", optimize=True)
        data = buffer.getvalue()
        file_of[model] = by_bytes.setdefault(hashlib.sha256(data).hexdigest(), model)
        if file_of[model] == model:
            with open(os.path.join(out_dir, model + ".png"), "wb") as f:
                f.write(data)
            total += len(data)

    index = {
        "source": "aion2hub.com (world boss page and map data), game artwork",
        "npcs": {npc: file_of[m] for npc, m in sorted(npc_models.items(), key=lambda kv: int(kv[0]))},
        "events": {event: [file_of[m] for m in models] for event, models in event_models.items()},
        "names": {name: file_of[m] for name, m in sorted(names.items())},
    }
    with open(os.path.join(out_dir, "bosses.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(index, f, ensure_ascii=False, indent=1)
        f.write("\n")

    print(f"field bosses: {len(npc_models)} of {len(set(field.values()))} NPC codes matched "
          f"({len(field)} spawn codes); world boss rows: {len(event_models)} of {len(WORLD_BOSS_EVENTS)}")
    print(f"portraits: {len(set(file_of.values()))} files for {len(picked)} models, {total // 1024} KB")
    print("no portrait:")
    for line in unmatched:
        print("  " + line)


if __name__ == "__main__":
    main()
