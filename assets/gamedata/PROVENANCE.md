# Built-in game data

Everything in this folder is compiled into AVION2. At runtime the app makes **no** network requests for data.
The names are NCSoft game content (AION2). Each table's source is listed below. To refresh a table after a
game patch, update the files here and rebuild.

| Path | Content | Source | License of the source |
|---|---|---|---|
| `skills/<lang>.json` | Skill id → name (10 languages) | [taengu/A2Tools-DPS-Meter](https://github.com/taengu/A2Tools-DPS-Meter) `src/data/i18n/skills`, commit `c9cf6551e8d8c16ed2f547d8aada0d9f016becf9` (2026-10-13) | GPL-3.0 (`licenses/A2Tools-LICENSE-GPL-3.0.txt`) |
| `npcs/<lang>.json` | NPC code → name, boss / dummy flags | same repo and commit, `src/data/i18n/npcs` | GPL-3.0 |
| `field_bosses.json` | Field boss spawn code (map × 100 + n) → NPC code | `FIELD_BOSS_CODES` in NOIA2, commit `00fa90340cd6e33710b7d9bca7f884a15278204a` (2026-10-08) | GPL-3.0 (`licenses/NOIA2-LICENSE-GPL-3.0.txt`) |
| `maps/<lang>.json` | Map id → zone name (en, de, ko; other languages fall back to English) | Written for AVION2. The id↔zone pairs are confirmed by two independent sources: the game's `Map.json` rows (as quoted in arkive-games/arkive test fixtures) and the field boss catalog of Waffle-ens/waffle_meter (MIT). The boss lists match our decoded tables. German and Korean names come from MetaBot.GG's localized map pages. | Facts only; no files copied |
| `gear_forge.json` | Item → enchant/amplify ladder (chance, pity per fail, Kinah and materials per attempt), English item names | Datamined game data as published by [aion2hub.com](https://aion2hub.com/tools/gear-forge) (Gear Forge page and its search index), fetched 2026-10-14 (`tools/build_forge.py`) | Game data |
| `world/<map>.json`, `world/<map>.jpg` | Zone maps: picture (tiles stitched, half resolution), markers (teleports, hidden cubes, monoliths, gathering, monsters with level/rank/drops, NPCs), regions | Datamined game data and map tiles as published by [aion2hub.com/maps](https://aion2hub.com/maps), fetched 2026-10-14 (`tools/build_maps.py`; marker icons, the game's own, via `tools/build_map_icons.py`). The world→map transform of Verteron was fitted from 19 field boss positions in our own captures; other maps calibrate at runtime from the field boss table | Game data |
| `dungeons.json` | Dungeons and instances: name, category (sealed dungeon, party dungeon, raid, …), level or item level, party size, region, bosses and other monsters (NPC codes where shown) | Game data as published by [MetaBot.GG](https://metabot.gg/en/aion-2/dungeons) (dungeon list and pages), fetched 2026-10-16 (`tools/build_dungeons.py`) | Game data |
| `items.json` | Item id → English name (loot of dungeon runs) | Game data as published in [aion2hub.com](https://aion2hub.com)'s public search index, fetched 2026-10-16 (`tools/build_items.py`) | Game data |
| `classes/<class>.png` | Official class emblems (256×256) | NCSoft's public armory CDN, `assets.playnccdn.com/static-aion2/characters/img/class/class_icon_<class>.png` | NCSoft artwork |

## Overrides

Files named `skills.json`, `npcs.json`, `field_bosses.json` or `maps.json` in `%AppData%\AVION2\data` (or the
folder given with `--data`) replace single built-in tables. PNGs in `%AppData%\AVION2\classes` replace single
emblems. `avion2 import-data` (CLI) can refill the override folder from A2Tools. This step is optional and manual.
