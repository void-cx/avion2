# Roadmap

## Done (milestone 1)
- Npcap capture, automatic game-connection detection, TCP reassembly, framing/LZ4, resync
- Damage, DoT/HoT, identity, spawns, deaths, HP decoding (damage verified 1:1 against Combat Analysis:
  180,002 vs. 180.00K on a Training Scarecrow)
- Console meter, pcap record/replay, hex dump for reverse engineering, name tables via `import-data`

## Done (milestone 2)
- WPF overlay: click-through lock, global hotkeys, my/all targets, skill breakdown, target HP bar,
  saved window settings, replay mode for UI work

## Done (milestone 3)
- Fight clock from our combat on/off (`21 8D`), matching Combat Analysis
- Incoming damage: per player, by mob and skill; crits taken, parries, blocks, shield hits and absorbs
- Low-HP sound alert and red flash; HP shown in the footer; max HP learned per character
- Fight history saved to disk, browsable in the overlay, copy-as-text summary

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
