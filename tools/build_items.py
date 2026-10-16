"""
Builds assets/gamedata/items.json: item id → English name (and grade/kind as the site shows it), from aion2hub's
public search index (game data as they publish it), fetched once at build time. AVION2 names the loot of a run with it.

    python tools/build_items.py            # writes assets/gamedata/items.json
"""
import json
import os
import sys
import time
import urllib.request

INDEX = "https://aion2hub.com/search-index"
AGENT = "Mozilla/5.0 (AVION2 data build; https://github.com/void-cx/avion2)"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def main() -> int:
    request = urllib.request.Request(INDEX, headers={"User-Agent": AGENT})
    with urllib.request.urlopen(request, timeout=60) as response:
        index = json.loads(response.read().decode("utf-8"))

    items = {}
    for entry in index:
        if entry.get("t") != "item":
            continue
        item_id = entry.get("u", "").rsplit("/", 1)[-1]
        if item_id.isdigit() and entry.get("n"):
            items[item_id] = entry["n"]

    out = os.path.join(ROOT, "assets", "gamedata", "items.json")
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        json.dump({"source": INDEX, "fetched": time.strftime("%Y-%m-%d"),
                   "items": dict(sorted(items.items(), key=lambda kv: int(kv[0])))}, f, ensure_ascii=False, separators=(",", ":"))
    print(f"{len(items)} items → {out}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
