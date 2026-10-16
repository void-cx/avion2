"""
Builds assets/gamedata/dungeons.json: AION2 dungeons and instances (name, category, party size, level, bosses) from
MetaBot.GG's public dungeon pages (game data as they publish it), fetched once at build time. AVION2 matches a run
to a dungeon by the bosses killed in it (boss names → our NPC table), and learns the map id from there.

    python tools/build_dungeons.py            # writes assets/gamedata/dungeons.json
"""
import html
import json
import os
import re
import sys
import time
import urllib.request

BASE = "https://metabot.gg"
LIST = BASE + "/en/aion-2/dungeons"
AGENT = "Mozilla/5.0 (AVION2 data build; https://github.com/void-cx/avion2)"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def fetch(url: str) -> str:
    request = urllib.request.Request(url, headers={"User-Agent": AGENT})
    with urllib.request.urlopen(request, timeout=30) as response:
        return response.read().decode("utf-8")


def text(fragment: str) -> str:
    return html.unescape(re.sub(r"<[^>]+>", "", fragment)).strip()


CATEGORIES = ["Party Dungeon", "Raid", "Ascension Trial", "Growth Dungeon", "Daily Dungeon", "Sealed Dungeon", "Arena",
              "Legion Content", "Ascension Instance", "Boss Challenge"]


def number(value: str):
    digits = value.replace(",", "").strip()
    return int(digits) if digits.isdigit() else None


def rows(page: str):
    """Every dungeon of the per-category tables: name, category, level / item level, party size, region, bosses."""
    headings = [(m.start(), text(m.group(1))) for m in re.finditer(r"<h2[^>]*>(.*?)</h2>", page, re.S)]
    for i, (at, title) in enumerate(headings):
        if title not in CATEGORIES:
            continue
        section = page[at:headings[i + 1][0] if i + 1 < len(headings) else len(page)]
        head = re.search(r"<thead>(.*?)</thead>", section, re.S)
        columns = [text(c) for c in re.findall(r"<th[^>]*>(.*?)</th>", head.group(1), re.S)] if head else []
        for row in re.findall(r"<tr><th scope=\"row\">(.*?)</tr>", section, re.S):
            link = re.search(r'<a href="/en/aion-2/dungeons/([^"#]+)">(.*?)</a>', row)
            if not link:
                continue
            cells = dict(zip(columns[1:], re.findall(r"<td[^>]*>(.*?)</td>", row, re.S)))
            bosses = []
            for boss in re.findall(r'<a class="B32I9a_nameLink"[^>]*>(.*?)</a>', cells.get("Bosses", ""), re.S):
                npc = re.search(r"npcs%2F(\d+)\.webp", boss)
                name = re.search(r'class="B32I9a_nameText">(.*?)</span>', boss, re.S)
                bosses.append({"name": text(name.group(1)) if name else "", "npc": int(npc.group(1)) if npc else None})
            more = re.search(r"<span>\+<!-- -->(\d+)</span>", cells.get("Bosses", ""))
            yield {
                "slug": link.group(1),
                "name": text(link.group(2)),
                "category": title,
                "level": number(text(cells.get("Level", ""))),
                "itemLevel": number(text(cells.get("Min. item level", ""))),
                "party": number(text(cells.get("Party size", ""))),
                "region": text(cells.get("Region", "")) or None,
                "bosses": bosses,
                "more": int(more.group(1)) if more else 0,
            }


def detail(page: str):
    """A dungeon page's "Bosses and monsters" section: (bosses, monsters), with NPC ids where the page shows them."""
    section = re.search(r"<h2[^>]*>Bosses and monsters</h2>(.*?)(?:<h2|$)", page, re.S)
    found = {"bosses": [], "monsters": []}
    for kind, link in re.findall(r'<a[^>]*href="/en/aion-2/(bosses|monsters)/[^"]+"[^>]*>(.*?)</a>', section.group(1) if section else "", re.S):
        name = re.search(r'nameText">(.*?)</span>', link, re.S)
        npc = re.search(r"npcs%2F(\d+)\.webp", link)
        group = found[kind]
        if name and text(name.group(1)) not in [f["name"] for f in group]:
            group.append({"name": text(name.group(1)), "npc": int(npc.group(1)) if npc else None})
    return found["bosses"], found["monsters"]


def main() -> int:
    page = fetch(LIST)
    dungeons = list(rows(page))
    print(f"{len(dungeons)} dungeons on the list", file=sys.stderr)
    for n, d in enumerate(dungeons):
        # every dungeon's page: the full boss list (the list's order first: main bosses) and the other monsters,
        # so runs without a boss kill can still be matched by what was killed
        bosses, monsters = detail(fetch(f"{BASE}/en/aion-2/dungeons/{d['slug']}"))
        known = [b["name"] for b in d["bosses"]]
        d["bosses"] += [b for b in bosses if b["name"] not in known]
        d["monsters"] = [m for m in monsters if m["name"] not in known]
        del d["more"]
        print(f"{n + 1}/{len(dungeons)} {d['name']}: {len(d['bosses'])} bosses, {len(d['monsters'])} monsters", file=sys.stderr)
        time.sleep(0.5)  # be gentle

    out = os.path.join(ROOT, "assets", "gamedata", "dungeons.json")
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        json.dump({"source": LIST, "fetched": time.strftime("%Y-%m-%d"), "dungeons": dungeons}, f, ensure_ascii=False, indent=1)
    print(f"wrote {out}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
