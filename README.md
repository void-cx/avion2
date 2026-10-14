# AVION2

A damage meter and fight analyzer for **AION2**. AVION2 reads the game's network traffic on your PC and shows who
did how much damage, live and after every fight.

It is passive: it never touches the game, its files or its memory, and sends nothing anywhere.

## Features

- **Live overlay** on top of the game: damage per player (DPS, share, crit, back attacks and more), damage taken,
  boss HP, fight timer.
- **Fight history** with the full breakdown of every fight: skills, hits, crits, damage taken by source.
- **Timeline** of every skill cast per player, **buff uptime**, and a **replay** of the fight on a map.
- **Timers and notices** for Spacetime Rifts, Beritra air raids, PvP events, resets, Reshanta world bosses and
  field boss respawns.
- **Gear Forge**: average and unlucky cost (materials, Kinah, attempts) to enchant or amplify any item.
- **Low-HP alert** with its own sound.
- **Server lag warning** when the game server freezes, also marked in each fight.
- **Legion** tags on players and a legion card with your members.
- Two themes: **AION2** and **VOID**. Skill and NPC names in 10 languages.

## Install

1. Install [Npcap](https://npcap.com/) once. If you use a ping reducer (ExitLag, GearUP, …), enable
   "Support loopback traffic" during setup.
2. Download `AVION2-vX.Y.Z-win-x64.zip` from [Releases](https://github.com/void-cx/avion2/releases) and unzip it
   anywhere. There is no installer.
3. Run `AVION2.exe` and click **Start Meter**, then play. The overlay appears and finds the game by itself.

Requires Windows 10 or 11. Run AION2 in windowed or borderless mode so the overlay can sit on top.
After starting the meter, **teleport or change zone once** so AVION2 learns which character is you.

## Using the overlay

| Action | How |
|---|---|
| Move / resize | Drag the header / the corner grip |
| Opacity | Ctrl + mouse wheel over the header |
| Fight details | Click a player |
| Reset the fight | ⟲ or **Ctrl+Shift+R** |
| Click-through (lock) | Lock icon; unlock with **Ctrl+Shift+L** |
| Hide / show | **Ctrl+Shift+O** |
| My targets / all targets | Person icon |
| Low-HP alert volume | Scroll over the speaker icon; click to mute |

In the **Replay**, click a dot to see who or what it is. Your own position is estimated between updates (the game
doesn't send it continuously), so you are drawn dashed then.

## Updates

AVION2 checks for a new release when it starts (Settings → General). When there is one, the Home page offers
**Update now**: AVION2 closes, `AVION2-Updater.exe` installs the new version and starts AVION2 again. Your settings
and fights stay.

## Privacy

Everything stays on your PC: fights, settings and recordings. Nothing is uploaded. Fights and recordings contain
other players' names, so ask before sharing them.

## License

AVION2 is free software under the [GNU General Public License v3.0](LICENSE). Copyright © 2026 Maurice Schmitz and
the Enter The Void community.

AVION2 is a fan-made tool and is not affiliated with or endorsed by NCSoft. AION2 and its names and class emblems
belong to NCSoft. The VOID mark belongs to Enter The Void. Skill and NPC names come from
[A2Tools](https://github.com/taengu/A2Tools-DPS-Meter) and [NOIA2](https://github.com/ZDYoung0519/NOIA2) (GPL-3.0);
fonts are under the SIL Open Font License. Details: [PROVENANCE.md](assets/gamedata/PROVENANCE.md).

Building from source, the command-line tool and how it works: [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).
