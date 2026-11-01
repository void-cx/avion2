"""
Builds assets/gamedata/servers.json: every AION2 server (id as in the game's player records, name, short name, faction),
from NC's public server list (aion2.plaync.com) for all regions, fetched once at build time.

    python tools/build_servers.py            # writes assets/gamedata/servers.json
"""
import json
import os
import sys
import time
import urllib.request

API = "https://aion2.plaync.com/en-us/api/gameinfo/servers?lang=en-US&region={}"
# NA East, NA West, Europe, Latin America, Asia (an unknown region answers with NA East)
REGIONS = {"nae": "NA East", "naw": "NA West", "eu": "Europe", "la": "Latin America", "as": "Asia"}
AGENT = "Mozilla/5.0 (AVION2 data build; https://github.com/void-cx/avion2)"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def main() -> int:
    servers = {}
    for code, region in REGIONS.items():
        request = urllib.request.Request(API.format(code), headers={"User-Agent": AGENT})
        with urllib.request.urlopen(request, timeout=30) as response:
            listed = json.loads(response.read().decode("utf-8"))["serverList"]
        for s in listed:
            servers[s["serverId"]] = {
                "name": s["serverName"],
                "short": s.get("serverShortName"),
                "faction": {1: "Elyos", 2: "Asmodian"}.get(s.get("raceId"), "?"),
                "region": region,
            }
        print(f"{region}: {len(listed)} servers", file=sys.stderr)
        time.sleep(0.5)

    out = os.path.join(ROOT, "assets", "gamedata", "servers.json")
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        json.dump({"source": API.format("<region>"), "fetched": time.strftime("%Y-%m-%d"),
                   "servers": {str(k): servers[k] for k in sorted(servers)}}, f, ensure_ascii=False, indent=1)
    print(f"{len(servers)} servers → {out}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
