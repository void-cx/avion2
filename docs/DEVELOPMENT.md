# Development

## Build and run

Requires the .NET 10 SDK and [Npcap](https://npcap.com/).

```powershell
dotnet run --project src/Avion2.Overlay -c Release   # the app
dotnet test tests/Avion2.Tests                       # tests (CI skips the two that need Npcap: --filter "Category!=Npcap")
tools/publish.ps1                                    # artifacts/AVION2: AVION2.exe + AVION2-Updater.exe + licenses
tools/publish.ps1 -Version 0.2.0 -Zip                # + the release zip and SHA256SUMS.txt
```

App options: `--start` (start the meter right away), `--record x.pcap`, `--log x.jsonl`, `--device 7`, `--data dir`,
`--theme void`. For UI work: `--replay x.pcap [--speed 2] [--from minutes] [--history-dir dir]` (`--from` feeds the first minutes at once, with their real timing); `--render-shots dir` renders every
window off-screen to PNGs and exits; `--obs` serves the OBS overlay for that run without saving the setting.
For shareable screenshots add `--anonymize` (players become "You", "Player 2", …; no legions), then
`--history-dir <that run's history> --replay-shot dir` renders the longest saved fight's replay. The website lives
in `site/` and deploys to GitHub Pages on every push that changes it.

## CI and releases

- `.github/workflows/ci.yml` builds and tests every push to `main` and every pull request, and attaches the
  portable folder.
- `.github/workflows/release.yml` runs on a version tag: it tests, builds, zips and creates the GitHub release with
  `AVION2-v<version>-win-x64.zip` and `SHA256SUMS.txt`.

```bash
git tag v0.2.0
git push origin v0.2.0
```

Local builds are version `0.0.0-dev` and never update themselves. Released builds ask the GitHub API for the latest
release; `AVION2-Updater.exe` downloads the zip (GitHub only), checks it against `SHA256SUMS.txt`, moves replaced
files to `*.old`, copies the new ones in (restoring everything if a step fails) and restarts AVION2, which deletes the
`*.old` files on its next start.

## Command-line tool

```bash
dotnet run --project src/Avion2.Cli -- live
```

| Command | Purpose |
|---|---|
| `avion2 devices` | List capture devices |
| `avion2 live [--device 7,9] [--record run.pcap] [--log run.jsonl]` | Console meter. Keys: `R` reset, `Q` quit |
| `avion2 replay <file.pcap> [--trace] [--log events.jsonl]` | Decode a recording, print every fight (`--trace`: timeline/buff/position/connection summary) |
| `avion2 dump <file.pcap> [--opcode 0438] [--events] [--unparsed] [--summary]` | Hex-dump decoded packets, opcode histogram |

Common options: `--data <dir>` (override tables), `--lang <code>`, `--idle <seconds>` (gap that ends a fight, default 15).

## Architecture

```
src/
  Avion2.Protocol   decoding only, no I/O: framing (varints, LZ4 bundles, resync), parsers → GameEvents
  Avion2.Capture    Npcap capture, TCP reassembly, game-connection detection, pcap read/record
  Avion2.Analysis   entities, encounters and stats, fight traces (timeline, buffs, positions, lag), timers
  Avion2.Engine     capture loop shared by app and CLI, history, built-in game data, update check
  Avion2.Overlay    WPF app: home, timers, map + compass, gear forge, history, overlay, details, themes
  Avion2.Updater    AVION2-Updater.exe
  Avion2.Cli        console meter and tools
tests/Avion2.Tests  protocol vectors, reassembly, detection, analysis, traces, updater
docs/PROTOCOL.md    the wire format, as published on the website (start here for protocol work)
docs/PROTOCOL-NOTES.md  working notes: measurements, verification history, open questions
```

- **Detection.** Server→client game traffic contains `0E 00 36` many times per second. A connection locks after
  12 hits in 3 s; loopback flows (ping reducers) win over the network card. Dungeons and raids run on their own
  servers: the stream moves to a new connection and back, and the detector follows.
- **Resync.** The stream is joined mid-way. Until frame boundaries are known, a frame is accepted only if its
  opcode belongs to a known family and the next length is plausible.
- **Robustness.** Each packet is decoded inside its own error boundary; failures go to
  `%LocalAppData%\AVION2\errors.log` with the bytes.
- **DPS** = damage ÷ fight duration (the game's own combat clock). Summons count for their owner; only players are rows.

## Validating against the game

AION2's *Combat Analysis* is the ground truth:

1. Start the meter with **Record session** on, teleport once, hit a training dummy for a minute.
2. Compare your total and per-skill damage with Combat Analysis.
3. If they differ: `avion2 dump <recording> --opcode 0438` and add the failing hex as a test vector.

On a boss kill, total party damage should be roughly 97–107% of the boss's max HP.

## Game data

Skill/NPC/item/zone/dungeon names (en, de, fr, es, pt, ru, ja, ko), field bosses, crafting recipes, stat rates, enchant
ladders, zone maps and class emblems are compiled in from [assets/gamedata](../assets/gamedata) (sources in its
PROVENANCE.md). `skills.json`, `npcs.json`, `field_bosses.json` or `maps.json` in `%AppData%\AVION2\data` replace
single tables ([format](../data/README.md)).

### Refreshing game data after a patch

The name tables, `recipes.json`, `stat_rates.json`, `gear_forge.json`, `items.json`, `field_bosses.json`,
`dungeon_names.json` and `pets.json` are built from the installed client's own tables by
[tools/extract_gamedata](../tools/extract_gamedata/README.md) (a dev-only tool, not part of the app):

1. Update the game, then run `dotnet run --project tools/extract_gamedata -- build`. It decodes the tables, prints
   per file what changed (counts, ids added and removed, examples) and stages the files in its work folder.
2. If a table's header no longer matches `schema.json`, the build stops and writes nothing. Refresh the layouts from
   a current `.usmap` with `dotnet run --project tools/extract_gamedata -- usmap <file>` and build again.
3. When the changes look right, `build --write` copies the files into `assets/gamedata`. Run the tests and update the
   client date in PROVENANCE.md.

It also checks `cube_rewards.json` against the client's RewardOdEnergyCube table without rewriting it. Zone maps come
from aion2hub's pages through `tools/build_maps.py` (Pillow; usage in the script's header); boss portraits from the
same site through `tools/build_boss_images.py` (Pillow), which matches bosses by English name and prints which ones
got no picture; gear stats through `tools/build_gear_stats.py`; the expedition reward cubes from SkeeveAN's tables
through `tools/build_cube_rewards.py`.
Skill and item icons (`assets/gamedata/skillicons`, `itemicons`) come from `tools/build_icons.py` (also Pillow); PNGs
in `%AppData%\AVION2\skillicons` or `itemicons`, named after the id or the icon, replace single ones.

## Prior art

Protocol facts were compiled from public research by
[TK-open-public/Aion2-Dps-Meter](https://github.com/TK-open-public/Aion2-Dps-Meter) (MIT),
[taengu/A2Tools-DPS-Meter](https://github.com/taengu/A2Tools-DPS-Meter) and
[ZDYoung0519/NOIA2](https://github.com/ZDYoung0519/NOIA2) (GPL-3.0). No code was copied.
