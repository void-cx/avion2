# Roadmap

## Done (milestone 1)
- Npcap capture, automatic game-connection detection, TCP reassembly, framing/LZ4, resync
- Damage, DoT/HoT, identity, spawns, deaths, HP decoding (damage verified 1:1 against Combat Analysis:
  180,002 vs. 180.00K on a Training Scarecrow)
- Console meter, pcap record/replay, hex dump for reverse engineering, built-in name tables

## Done (milestone 2)
- WPF overlay: click-through lock, global hotkeys, my/all targets, skill breakdown, target HP bar,
  saved window settings, replay mode for UI work

## Done (milestone 3)
- Fight clock from our combat on/off (`21 8D`), matching Combat Analysis
- Incoming damage: per player, by mob and skill; crits taken, parries, blocks, shield hits and absorbs
- Low-HP sound alert and red flash; HP shown in the footer; max HP learned per character
- Fight history saved to disk, browsable in the overlay, copy-as-text summary

## Done (milestone 4)
- Home window (Home / History / Settings) in AION2 style, Cinzel, app icon, single-exe publish
- Player cards in the overlay (class emblem, combat power, Dbl/Perf/Crit/Front/Back), boss HP numbers,
  timer box, party DPS, 10 Hz updates with eased bars; compact rows option
- Rift and event timetable with notifications in a free-floating, click-through notification area, per region
- Field boss board: who is up and when the dead ones respawn (from the game's own `01 91` table), saved across
  restarts, with respawn notifications
- Official class emblems (from the AION2 armory CDN), built in
- All game data built in (names in 10 languages, field bosses, zone names, emblems); no runtime downloads
- Theme engine with live switching: AION2 and VOID (Enter The Void design manual)
- Separate built-in sounds and volumes for low HP (warning beeps) and notifications (bell)
- Movement, skill casts, buffs, ownership and legion records decoded (docs/PROTOCOL.md)
- Fight traces saved with every fight: **Timeline** (every cast per player), **Buffs** (uptime), **Replay**
  (top-down positions, attack lines, damage numbers, skill names; play/scrub/zoom)
- Legion tags on player cards, legion card on Home (introduction, members by last login)
- Boss skill cast notices; summons/effects linked to their owner via `04 8D`
- Server lag: live overlay warning, freeze bands in Timeline/Replay, connection summary per fight
- CI (build + test on every push) and tagged releases; self-update via AVION2-Updater.exe from GitHub releases

## Next
- **Per-target view** (the game's "Metric: <target>"): pick one target instead of "my targets".

## Ideas
- **Buff uptime and rotation timeline** from `2A/2B/2C 38` abnormal records. For example, whether a
  mantra was up during a burst.
- **Boss HP bar and enrage timer.** Boss max HP comes from the spawn record; `00 8D` gives live HP.
- **Healing meter.** HoT/heal records are already decoded.
- **Compare runs** (e.g. this dummy session vs. last week's).
- **Exact max HP** if it turns up in a packet (not in `49 36`/`33 36` so far).
- **Party roster** from `02 97`: class, gear score and combat power of party members.
- **DoT accuracy.** Gate damage DoTs with a DoT skill allowlist, as prior art does.
