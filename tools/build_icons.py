"""
Builds the skill and item icons AVION2 ships: assets/gamedata/skillicons/ and assets/gamedata/itemicons/, each with
an index.json (id → icon file) and the icons as small PNGs. The pictures are the game's own, downloaded once from
NCSoft's public CDN (assets.playnccdn.com/static-aion2-gamedata/resources/<name>.png).

Which icon goes with which id:
- skills: the official AION2 site's character data (aion2.plaync.com/api/character/equipment). Its skill list holds a
  class's whole skill set (learned or not) with id, name and icon, so a few public characters per class are enough.
  The Fighter is not on the Global servers yet, so its characters come from the Korean site (same API). The site
  lists the skill bar's skills only; chain steps and the like come from SkeeveAN/Aion-DPS-Meter's skill table (MIT;
  Backend/src/data/aion2/skill_icons.json, base skill id → icon name). The meter's skill ids are CC SS NNNN: the low
  four digits are the specialisations, so every variant shares the base id's icon (SkillIds.ToBase). Base ids
  neither lists get the icon of the class's site skill with the same English name (assets/gamedata/skills/en.json).
- items: the item → icon table of SkeeveAN/Aion-DPS-Meter (MIT; Backend/src/data/aion2/item_icons.json and
  Client/assets/aion2/craft/item_icons.json), the gear those characters wear (official site), and for the items
  AVION2 shows (Gear Forge, reward cubes, equipment, wings, currencies, materials, consumables) that neither has,
  aion2hub.com's item pages (their og:image is the CDN icon; the most wanted HUB_LIMIT items), then aion2hub's own
  copy of the item's icon (also used where the CDN lacks a file). Skins, titles and decorations are left out.
  Identical pictures are kept once.

Fetching is polite: plain GETs, User-Agent "AVION2", no cookies, a pause between requests, and every answer is cached
in <cache dir>, so a second run asks nothing. Needs Pillow (a venv outside the repo). The skill set is 64 px, the
item set 48 px, both quantized PNGs.

    python -I tools/build_icons.py <cache dir> <SkeeveAN repo dir>
"""
import hashlib
import io
import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GAMEDATA = os.path.join(ROOT, "assets", "gamedata")
AGENT = "AVION2"
CDN = "https://assets.playnccdn.com/static-aion2-gamedata/resources/{}.png"
SEARCH = ("https://api-search.plaync.com/aion2global/search/v2/character"
          "?keyword={}&pcId={}&page=1&size=20&localeInfo=en-US&region={}&serverId={}")
# the Fighter is not on the Global servers yet: the Korean site's characters (same API, English names with lang=en)
SEARCH_KR = "https://api-search.plaync.com/aion2/search/v2/character?keyword={}&pcId={}&page=1&size=20&serverId={}"
EQUIPMENT = "https://aion2.plaync.com/api/character/equipment?lang=en&characterId={}&serverId={}"
HUB_ITEM = "https://aion2hub.com/database/items/{}"
# the icon aion2hub shows for an item (96 px WebP): for icons the CDN does not have, and items past HUB_LIMIT
HUB_ICON = "https://aion2hub.com/api/icon/items/{}"
HUB_INDEX = "https://aion2hub.com/search-index"

# class prefix of the skill ids (GameClasses.FromSkillId) and the roster codes the search answers with (pcId)
CLASSES = {
    11: ("Gladiator", range(5, 9)), 12: ("Templar", range(9, 13)), 13: ("Assassin", range(17, 21)),
    14: ("Ranger", range(13, 17)), 15: ("Sorcerer", range(25, 29)), 16: ("Elementalist", range(21, 25)),
    17: ("Cleric", range(29, 33)), 18: ("Chanter", range(33, 37)), 19: ("Fighter", range(45, 49)),
}
# a few servers and short name parts; the search finds characters of a class whose name contains them
SERVERS = [(1301, "eu"), (1321, "eu"), (1101, "nae")]
SERVERS_KR = [1001, 1002]
KEYWORDS = ["a", "e", "ka", "el"]
PER_CLASS = 3
SKILL_PX = 64
ITEM_PX = 48
HUB_LIMIT = 800  # aion2hub item pages at most (~100 KB each); the other wanted items by their icon (~4 KB)
# item kinds (aion2hub search index, grade first) worth an icon when the other sources have none
WANTED_KINDS = {
    "Longsword", "Greatsword", "Dagger", "Bow", "Spellbook", "Orb", "Mace", "Staff", "Fist", "Guard", "Polearm",
    "Top", "Legs", "Pauldrons", "Gloves", "Shoes", "Helm", "Cloak", "Earrings", "Ring", "Necklace", "Bracelet",
    "Amulet", "Belt", "Brooch", "Rune", "Wing", "Currency", "Magic Stone", "Godstone", "Crafting Material",
    "Arcana Card", "Chalice", "Parchment", "Compass", "Bell", "Mirror", "Scales", "Pendant",
    # what drops and gets used in runs
    "Usable", "Reward Box", "Ticket", "Food & Drink", "Scroll", "Misc", "Contents Sweep Ticket", "Monolith Token",
    "Seal", "Key", "Hourglass", "Dice", "Lantern",
}
# left out on purpose (about 4,500 items, the bulk of a full set): skins, titles and housing decorations


def cached(cache, name, url, binary=False, pause=0.4):
    """The answer for url, from <cache>/<name> when it was fetched before; None when the server has none."""
    path = os.path.join(cache, name)
    if os.path.exists(path):
        if os.path.getsize(path) == 0:
            return None
        with open(path, "rb") as f:
            data = f.read()
        return data if binary else data.decode("utf-8")
    request = urllib.request.Request(url, headers={"User-Agent": AGENT})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            data = response.read()
    except urllib.error.HTTPError as e:
        if e.code not in (404, 410):
            raise
        data = b""
    time.sleep(pause)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)
    if not data:
        return None
    return data if binary else data.decode("utf-8")


def safe(text):
    return re.sub(r"[^A-Za-z0-9_.-]", "_", text)


def icon_name(url):
    """'https://…/resources/ICON_CH_SKILL_001.png' → 'ICON_CH_SKILL_001'."""
    return os.path.splitext(urllib.parse.urlparse(url).path.rsplit("/", 1)[-1])[0] if url else None


def find_characters(cache):
    """Up to PER_CLASS high-level public characters per class: (characterId, serverId, region)."""
    found = {prefix: [] for prefix in CLASSES}
    for prefix, (cls, codes) in CLASSES.items():
        pc = ",".join(str(c) for c in codes)
        searches = [(f"search/{cls}_{s}_{k}.json", SEARCH.format(k, pc, region, s), region)
                    for s, region in SERVERS for k in KEYWORDS]
        searches += [(f"search/{cls}_kr{s}_{k}.json", SEARCH_KR.format(k, pc, s), None) for s in SERVERS_KR for k in KEYWORDS]
        for name, url, region in searches:
            if len(found[prefix]) >= PER_CLASS:
                break
            for hit in json.loads(cached(cache, name, url) or "{}").get("list", []):
                if hit.get("level", 0) >= 40 and hit.get("pcId") in codes and len(found[prefix]) < PER_CLASS:
                    found[prefix].append((urllib.parse.unquote(hit["characterId"]), hit["serverId"], region))
    return found


def read_characters(cache, found):
    """Per class the site's skills (id → (name, icon)); all characters' worn items (id → icon)."""
    skills = {prefix: {} for prefix in CLASSES}
    items = {}
    for prefix, characters in found.items():
        for character_id, server, region in characters:
            name = f"equipment/{safe(character_id)}.json"
            url = EQUIPMENT.format(urllib.parse.quote(character_id, safe=""), server) + (f"&region={region}" if region else "")
            text = cached(cache, name, url)
            if not text:
                continue
            data = json.loads(text)
            for s in (data.get("skill") or {}).get("skillList") or []:
                if s.get("icon"):
                    skills[prefix][s["id"]] = (s.get("name", ""), icon_name(s["icon"]))
            equipment = data.get("equipment") or {}
            worn = (equipment.get("equipmentList") or []) + (equipment.get("skinList") or [])
            petwing = data.get("petwing") or {}
            worn += [petwing[k] for k in ("wing", "wingSkin", "pet") if isinstance(petwing.get(k), dict)]
            for i in worn:
                if i.get("id") and i.get("icon"):
                    items[i["id"]] = icon_name(i["icon"])
    return skills, items


def is_player_skill(skill_id):
    return 11_000_000 <= skill_id <= 19_999_999 and skill_id // 10_000 % 100 != 0


def map_skills(site, extra):
    """The meter's base skill ids → icon name, and per class the coverage (bases, with icon, names without).
    The site's skills first, then the other table's (extra), then the site skill of the same name."""
    names = json.load(open(os.path.join(GAMEDATA, "skills", "en.json"), encoding="utf-8"))
    bases = {}
    for key, name in names.items():
        skill_id = int(key)
        if is_player_skill(skill_id):
            bases.setdefault(skill_id - skill_id % 10_000, name)
    table, coverage = {}, {}
    for skill_id, icon in extra.items():
        if skill_id // 1_000_000 in CLASSES:
            table[skill_id] = icon
    for prefix, (cls, _) in CLASSES.items():
        by_name = {}
        for skill_id, (name, icon) in site[prefix].items():
            table[skill_id] = icon
            by_name.setdefault(name.casefold(), icon)
        mine = {b: n for b, n in bases.items() if b // 1_000_000 == prefix}
        without = []
        for base, name in sorted(mine.items()):
            if base in table:
                continue
            icon = by_name.get(names.get(str(base), name).casefold())
            if icon:
                table[base] = icon
            else:
                without.append(f"{base} {name}")
        coverage[cls] = (len(mine), len(mine) - len(without), without)
    return table, coverage


def hub_kinds(cache):
    index = json.loads(cached(cache, "hub/search-index.json", HUB_INDEX))
    kinds = {}
    for entry in index:
        if entry.get("t") == "item":
            item_id = entry.get("u", "").rsplit("/", 1)[-1]
            if item_id.isdigit():
                kinds[int(item_id)] = entry.get("s", "")
    return kinds


def wanted_items(kinds):
    """Item ids AVION2 shows, most wanted first: Gear Forge materials, reward cube items and Gear Forge items, then
    wings, currencies and stones, then the other wanted kinds."""
    first = set()
    forge = json.load(open(os.path.join(GAMEDATA, "gear_forge.json"), encoding="utf-8"))
    for table in ("enchant", "amplify"):
        for ladder in forge[table].values():
            for step in ladder["steps"]:
                first.update(m["id"] for m in step["materials"] if m.get("id"))
    cubes = json.load(open(os.path.join(GAMEDATA, "cube_rewards.json"), encoding="utf-8"))
    for cube in cubes["cubes"]:
        first.update((cube.get("ceiling") or {}).get("items") or [])
    first.update(i["id"] for i in forge["items"])
    soon = {"Wing", "Currency", "Magic Stone", "Godstone"}

    def rank(item_id):
        kind = kinds.get(item_id, "").split(" ", 1)[-1]
        return (0 if item_id in first else 1 if kind in soon else 2, item_id)

    wanted = set(first)
    wanted.update(i for i, kind in kinds.items() if kind.split(" ", 1)[-1] in WANTED_KINDS)
    return sorted(wanted, key=rank)


def same_name(items, names, kinds):
    """Items without an icon whose name and kind match items that all show one icon (bound and unbound copies)."""
    icons = {}
    for item_id, icon in items.items():
        key = (names.get(str(item_id)), kinds.get(item_id))
        if key[0]:
            icons.setdefault(key, set()).add(icon)
    found = {}
    for key, value in names.items():
        item_id = int(key)
        match = icons.get((value, kinds.get(item_id)))
        if item_id not in items and match and len(match) == 1:
            found[item_id] = next(iter(match))
    return found


def hub_icons(cache, ids):
    """Item id → icon name from aion2hub's item pages (their og:image), at most HUB_LIMIT pages."""
    icons = {}
    for item_id in ids[:HUB_LIMIT]:
        html = cached(cache, f"hub/items/{item_id}.html", HUB_ITEM.format(item_id), pause=0.6)
        match = re.search(r'property="og:image" content="([^"]+/static-aion2-gamedata/resources/[^"]+)"', html or "")
        if match:
            icons[item_id] = icon_name(match.group(1))
    return icons


def shrink(raw, px):
    """The icon as a px × px PNG (transparent edges kept), as small as Pillow makes it."""
    image = Image.open(io.BytesIO(raw)).convert("RGBA")
    image.thumbnail((px, px), Image.LANCZOS)
    if image.size != (px, px):
        canvas = Image.new("RGBA", (px, px))
        canvas.paste(image, ((px - image.width) // 2, (px - image.height) // 2))
        image = canvas
    out = io.BytesIO()
    image.quantize(256, method=Image.Quantize.FASTOCTREE).save(out, "PNG", optimize=True)
    return out.getvalue()


def hub_icon(cache, item_id):
    """aion2hub's picture of an item (WebP), or None."""
    return cached(cache, f"hubicon/{item_id}.webp", HUB_ICON.format(item_id), binary=True, pause=0.3)


def write_set(cache, folder, table, px, source, fallback=None):
    """Downloads each icon once, writes <folder>/<name>.png (identical pictures once) and index.json. Names starting
    with "item_", and icons the CDN does not have, come from fallback(id) when given."""
    out_dir = os.path.join(GAMEDATA, folder)
    os.makedirs(out_dir, exist_ok=True)
    for name in os.listdir(out_dir):
        os.remove(os.path.join(out_dir, name))
    files, by_hash, index, missing = {}, {}, {}, set()
    for key, icon in sorted(table.items()):
        if icon not in files and icon not in missing:
            raw = None
            if not icon.startswith("item_"):
                raw = cached(cache, f"cdn/{safe(icon)}.png", CDN.format(urllib.parse.quote(icon)), binary=True, pause=0.15)
            if not raw and fallback:
                raw = fallback(key)
            if not raw:
                missing.add(icon)
                continue
            try:
                png = shrink(raw, px)
            except OSError:  # not a picture
                missing.add(icon)
                continue
            digest = hashlib.sha1(png).hexdigest()
            if digest not in by_hash:
                by_hash[digest] = icon
                with open(os.path.join(out_dir, icon + ".png"), "wb") as f:
                    f.write(png)
            files[icon] = by_hash[digest]
        if icon in files:
            index[str(key)] = files[icon]
    with open(os.path.join(out_dir, "index.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump({"source": source, "fetched": time.strftime("%Y-%m-%d"), "size": px,
                   "icons": dict(sorted(index.items(), key=lambda kv: int(kv[0])))}, f, separators=(",", ":"))
    size = sum(os.path.getsize(os.path.join(out_dir, n)) for n in os.listdir(out_dir))
    print(f"{folder}: {len(index)} ids, {len(by_hash)} pictures, {size / 1024 / 1024:.2f} MB"
          f"{f', {len(missing)} icons not found' if missing else ''}", file=sys.stderr)
    return index


def main() -> int:
    cache, skeeve = sys.argv[1], sys.argv[2]

    found = find_characters(cache)
    site_skills, worn = read_characters(cache, found)
    for prefix, (cls, _) in CLASSES.items():
        print(f"{cls}: {len(found[prefix])} characters, {len(site_skills[prefix])} skills on the site", file=sys.stderr)
    data = os.path.join(skeeve, "Backend", "src", "data", "aion2")
    extra = {int(k): v for k, v in json.load(open(os.path.join(data, "skill_icons.json"), encoding="utf-8")).items()}
    skills, coverage = map_skills(site_skills, extra)
    on_site = sum(len(v) for v in site_skills.values())
    print(f"skills: {on_site} on the site, {len([k for k in skills if k in extra and not any(k in v for v in site_skills.values())])}"
          f" more from SkeeveAN", file=sys.stderr)
    write_set(cache, "skillicons", skills, SKILL_PX,
              "aion2.plaync.com character skill lists, SkeeveAN/Aion-DPS-Meter skill_icons.json (MIT); "
              "icons from assets.playnccdn.com/static-aion2-gamedata")
    for cls, (total, with_icon, without) in coverage.items():
        print(f"  {cls}: {with_icon}/{total} skill ids with an icon; without: {', '.join(without) or '-'}", file=sys.stderr)

    items = {}
    for path in (os.path.join(data, "item_icons.json"), os.path.join(skeeve, "Client", "assets", "aion2", "craft", "item_icons.json")):
        items.update({int(k): v for k, v in json.load(open(path, encoding="utf-8")).items()})
    from_skeeve = len(items)
    items.update(worn)  # the official site wins where both know an item
    kinds = hub_kinds(cache)
    names = json.load(open(os.path.join(GAMEDATA, "items.json"), encoding="utf-8"))["items"]
    copies = same_name(items, names, kinds)
    items.update(copies)
    todo = [i for i in wanted_items(kinds) if i not in items]
    from_hub = hub_icons(cache, todo)
    items.update(from_hub)
    items.update(same_name(items, names, kinds))
    rest = [i for i in todo if i not in items]
    items.update({i: f"item_{i}" for i in rest})
    print(f"items: {from_skeeve} from SkeeveAN, {len(worn)} worn on the site, {len(copies)} by name and kind, "
          f"{len(from_hub)} of {len(todo)} from aion2hub's item pages, {len(rest)} by aion2hub's item icon", file=sys.stderr)
    write_set(cache, "itemicons", items, ITEM_PX,
              "SkeeveAN/Aion-DPS-Meter item_icons.json (MIT), aion2.plaync.com, aion2hub.com item pages and icons; "
              "icons from assets.playnccdn.com/static-aion2-gamedata", lambda i: hub_icon(cache, i))
    return 0


if __name__ == "__main__":
    sys.exit(main())
