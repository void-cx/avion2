# Game data

These files are optional and are not committed: they contain NCSoft game data, and `data/*.json` is gitignored.

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

## Source

`avion2 import-data [--lang de] [--source <local A2Tools clone>]` fetches the tables that
[taengu/A2Tools-DPS-Meter](https://github.com/taengu/A2Tools-DPS-Meter) extracts from the game client,
and records where they came from in `source.json`. That repo is GPL-3.0, so the files stay local and
are never committed or bundled. If we ever ship binaries with names built in, we need our own client
extractor first.
