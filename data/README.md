# Override data (optional)

The app ships all tables built in (see [assets/gamedata](../assets/gamedata) and its PROVENANCE.md). Files in an
override folder (`%AppData%\AVION2\data`, or `--data <dir>`) replace single built-in tables. Missing files keep
the built-in version. This folder (`data/`) is gitignored and is only used when passed with `--data data`.

## skills.json

```json
{ "16040000": "Skill name", "17010000": "Another skill" }
```

Keys are **base** skill ids, i.e. the wire id with its last four digits zeroed (`raw - raw % 10000`).
Those four digits encode the skill's specialisation choices. DoT ids are `wire / 100`.

## npcs.json

```json
{ "2400032": { "name": "Training Dummy", "isBoss": false } }
```

Keys are NPC codes (the u24 from `41 36` spawn records).

## field_bosses.json

```json
{ "101005": 2100077 }
```

Field boss spawn code (map x 100 + n, from `01 91`) mapped to the NPC code.

## maps.json

```json
{ "1010": "Verteron" }
```

## Refreshing from A2Tools

`avion2 import-data [--lang de] [--source <local A2Tools clone>] [--data <dir>]` writes the A2Tools tables into an
override folder (default `%AppData%\AVION2\data`). This is manual and optional. To update what ships, replace the
files under `assets/gamedata` instead and rebuild.
