# AVION2

A gameplay analyzer for **AION2**. It passively reads the game's network traffic with
[Npcap](https://npcap.com/) and decodes combat into a DPS meter, skill breakdowns and encounter logs.

- **Passive only.** It never touches the game process or memory and never injects or sends anything.
  It sees the same packets your network card does.
- **Local first.** Captures and logs stay on your machine. See [Privacy](#privacy).

> Status: **milestone 2**: capture → decoder → console meter and overlay. Damage is verified 1:1
> against the in-game Combat Analysis. The protocol notes come from public
> research and **still have to be checked against real captures** (see [Validating](#validating-against-the-game)).

## Requirements

- Windows 10/11
- [Npcap](https://npcap.com/). WinPcap-compatible mode is optional. Turn on "Support loopback traffic"
  if you use a ping reducer (ExitLag, GearUP, …).
- .NET 10 SDK
- Admin rights may be needed, depending on how Npcap was installed.

## Usage

### App

```bash
dotnet run --project src/Avion2.Overlay -c Release
```

The **home window** is the app's main window, styled after AION2's own site and armory:
- The key-art video plays behind the home page. Copy a video to `%AppData%\AVION2\background.mp4`, or pick one under Settings → Appearance. It stays local: NCSoft artwork is never committed.
- Glowing serif display type, aether-blue and violet pill buttons, and star-topped cards.
- Charcoal "armory" panels on the inner pages.

- **Home**:
  - **Start / Stop meter**, plus the capture device (automatic by default) and the option to record the session as a `.pcap`.
  - Status cards: Npcap, name tables, and your known characters (with their learned max HP).
  - Your most recent fights.
- **History**: every saved fight, with the selected fight's full breakdown next to it. Copy summary, delete, open folder.
- **Settings**:
  - Auto-start, overlay opacity and targets, lock.
  - Low-HP alert: threshold, volume, custom sound, test.
  - Skill/NPC names: download or update, with a language choice.
  - History folder.

While the meter runs, a compact **overlay** sits on top of the game (windowed or borderless mode):

| Action | How |
|---|---|
| Move / resize | Drag the header / the grip in the bottom-right corner |
| Opacity | Ctrl + mouse wheel over the header |
| Damage / Taken | Tabs under the header |
| Details | Click a player: the live **detail window** (stat tiles, skill table with spec, crit/back/double/perfect/parry/multi rates, avg/min/max; damage taken by mob and skill). 📌 keeps it on top |
| History / Settings | Clock / gear icon: opens the home window on that page |
| My targets / all targets | Person icon |
| Reset the encounter | ⟲ or **Ctrl+Shift+R** |
| Lock (click-through) | Lock icon; unlock with **Ctrl+Shift+L** |
| Hide / show | **Ctrl+Shift+O** |
| Low-HP alert | Sound + red border below the threshold. Speaker icon: click to mute, scroll for volume |
| Stop the meter | ✕ (the running fight is saved) |

Options: `--start` (start the meter right away), `--record captures/x.pcap`, `--log captures/x.jsonl`,
`--device 7`, `--data dir`. For UI work: `--replay captures/x.pcap [--speed 2] [--history-dir dir]`,
and `--render-shots dir` renders every window off-screen to PNGs and exits.

Settings live in `%AppData%\AVION2\overlay.json`, characters in `characters.json` next to it, and saved fights in
`%LocalAppData%\AVION2\history` (newest 300). The fight timer follows the game's own combat state, like Combat
Analysis. Max HP is never sent by the game, so it is learned from the highest HP seen.

### Console

```bash
dotnet run --project src/Avion2.Cli -- live
```

| Command | Purpose |
|---|---|
| `avion2 devices` | List capture devices |
| `avion2 live [--device 7,9] [--record captures/run.pcap] [--log captures/run.jsonl]` | Live console meter. Keys: `R` reset, `Q` quit |
| `avion2 replay <file.pcap> [--log events.jsonl]` | Decode a recording and print every encounter |
| `avion2 dump <file.pcap> [--opcode 0438] [--limit 200] [--summary]` | Hex-dump decoded packets and show an opcode histogram (for reverse engineering) |

Common options: `--data <dir>` (skill/NPC name tables) and `--idle <seconds>` (gap that ends an encounter, default 15).

To find your own character, the game must send its identity record. **Teleport or change zone once**
after starting the meter.

## Architecture

```
src/
  Avion2.Protocol   pure decoding, no I/O
    Varint, SpanReader             LEB128 varints, little-endian fields
    Framing                        length-prefixed frames, resync, LZ4 bundles (nested)
    GameStreamDecoder              byte stream → frames → bundles → parsers → GameEvents
    Parsers/                       04 38 damage, 05 38 DoT/HoT, 33 36 self, 45 36 players,
                                   41 36 NPC/summon spawn, 42 36 death, 21 36 map, 23 36 zone, 1B 92 HP
  Avion2.Capture    Npcap capture and transport
    PacketDissector                Ethernet/loopback/raw → IPv4/IPv6 → TCP (trims NIC padding)
    TcpReassembler                 seq ordering, retransmits, overlaps, wraparound, gap skip
    GameConnectionDetector         finds the game stream by its record signature (no hard-coded IPs)
    GameCapturePipeline            ties it together
    LiveCapture / PcapFile / PcapRecorder
  Avion2.Analysis   game semantics
    EntityRegistry                 names, classes, NPC codes, summon → owner
    CombatTracker                  encounters, per-actor/per-skill/per-target stats
    MeterView                      UI-agnostic snapshot rows (shared by the console and a future overlay)
  Avion2.Engine     live capture loop shared by the console and the overlay (also replays recordings)
  Avion2.Overlay    WPF app: home window (Home / History / Settings), compact click-through overlay,
                    live detail window (Fluent dark theme)
  Avion2.Cli        console meter, replay, dump, import-data
tests/Avion2.Tests  protocol vectors, framing/resync, reassembly, detection, pcap round-trip, DPS math
docs/PROTOCOL.md    wire-format notes (start here for protocol work)
```

### Design notes

- **Detection.** Server→client game traffic contains `0E 00 36` many times per second even when idle.
  A flow locks after 12 hits in 3 s. Loopback flows (from ping reducers) win over the physical NIC.
  Connections that open with a TLS record are excluded.
- **Resync.** The stream is joined mid-way and may have capture gaps. Until the decoder has locked
  onto frame boundaries, it accepts a frame only if its opcode is in a known family and the next length
  prefix is also plausible. Without this, a bogus length can swallow real frames.
- **Damage records** are located by the `01 00 00 00` + power-scalar anchor rather than fixed offsets,
  because some records carry unexplained extra bytes.
- **DPS** = an actor's damage ÷ encounter duration (first to last player hit, minimum 1 s). Summon damage
  is credited to the owner. Monster hits on players count as *damage taken*.

## Validating against the game

The game has a personal damage meter, *Combat Analysis*, since the 2026-04-01 patch. Use it as ground truth:

1. `avion2 live --record captures/dummy.pcap --log captures/dummy.jsonl`
2. Teleport once, then hit a training dummy or a boss for a minute.
3. Compare your total and per-skill damage with Combat Analysis.
4. If they differ, run `avion2 dump captures/dummy.pcap --opcode 0438` and look at the raw records.
   Add the failing hex as a test vector in `tests/Avion2.Tests/ProtocolTests.cs`.

On a boss kill, total party damage should land at roughly 97–107% of the boss's max HP.

## Game data

Skill and NPC names are NCSoft game data and are **not** committed. Fetch them into the gitignored
`data/` folder:

```bash
dotnet run --project src/Avion2.Cli -- import-data --lang en
```

This pulls the tables that [A2Tools](https://github.com/taengu/A2Tools-DPS-Meter) extracts from the
game client: 9k+ skills and 9.7k NPCs with boss and dummy flags, in 10 languages. Re-run it after
game patches. Without the tables, the meter shows IDs. Format: [data/README.md](data/README.md).

## Privacy

NCSoft has warned against publishing other players' damage records without consent. AVION2 therefore:

- keeps everything local; there are no uploads;
- ignores `captures/`, `*.pcap` and `*.jsonl` in git, because recordings contain other players' names.

Any future sharing feature must be opt-in and limited to your own character unless others consent.

## Prior art and licensing

The protocol notes were compiled from public research by these projects:
- [TK-open-public/Aion2-Dps-Meter](https://github.com/TK-open-public/Aion2-Dps-Meter) (MIT)
- [taengu/A2Tools-DPS-Meter](https://github.com/taengu/A2Tools-DPS-Meter) (GPL-3.0)
- [ZDYoung0519/NOIA2](https://github.com/ZDYoung0519/NOIA2) (GPL-3.0)

No code was copied from the GPL projects; only protocol facts are used.

Display font: [Cinzel](https://github.com/NDISCOVER/Cinzel) by the Cinzel Project Authors, bundled under the
SIL Open Font License 1.1 (`src/Avion2.Overlay/Fonts/Cinzel-OFL.txt`). The app icon is generated by
`tools/make-icon.ps1`.
