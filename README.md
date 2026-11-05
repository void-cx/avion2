# AVION2

An all-in-one hub for **AION2** on your PC: damage meter and fight analyzer, dungeon runs, timers, a zone map,
characters, gear and progress. AVION2 reads the game's network traffic, so it can also run on another PC than the game.

It is passive: it never touches the game, its files or its memory. Your data stays on your PC; AVION2 only asks the
official AION2 site for the characters you open and for your own characters' portraits (saved, renewed weekly).

**Website and download:** https://avion2.app

## Features

- **Live overlay** on top of the game: damage per player (DPS, share, crit, back attacks and more), damage taken,
  boss HP, fight timer.
- **Fight history** with the full breakdown of every fight: skills, hits, crits, damage taken by source, stagger
  (whose hits filled the boss's gauge and who broke it), dodges, and your best DPS per boss.
- **Dungeon runs**: every sealed dungeon, expedition, stronghold and other instance from entering to leaving, with
  boss times, deaths, party, Kinah, loot and the potions and scrolls you used, whether you left through the exit,
  what an expedition's reward cube gave, your guaranteed-reward points per expedition and your best time per dungeon.
- **Timeline** of every skill cast per player with the boss's wind-ups and interrupts, **buff uptime**, and a
  **replay** of the fight: everyone moving smoothly, their facing, HP, hits, heals and deaths, where boss skills will
  land with a countdown, the key moments to jump to and a camera that follows you or the boss.
- **Timers and notices** for Spacetime Rifts, Dimensional Invasions, the Shugo Festival, the Airspace Battle and other
  PvP events, resets, Reshanta world bosses and field boss respawns; events that are on show the game's own times.
  Bosses show the game's own pictures on the Timers page, in notices and on the map.
- **Map** of every zone with teleports, gathering spots, monsters (with drops) and NPCs; you and the players around
  you live on it. Optional, off by default: a compass that finds hidden cubes (it uses
  cube positions the game sends before you can see them, which may count as an unfair advantage). Your party shows on
  it too, also out of view, and a click on a member shows their map.
- **Characters**: combat power, item level, gear, skills, Daevanion boards and stats of your characters and of
  anyone you look up (or right-click in the meter); a profile you open in the game opens there too. Compare puts one
  of yours beside them, piece by piece. It asks the official AION2 site, only when you open a character (or fill the
  stat calculator in from one).
- **Gear Forge**, the calculators:
  - **Enchant and amplify:** average and unlucky cost (materials, Kinah, attempts) for any item, and the best next
    upgrades for your gear: the most item level for your Kinah.
  - **Crafting:** every material an item takes down to the gathered ones, what your bag already holds and what is
    missing, combo chances, and materials you'd rather buy or gather; below it, a log of your crafts.
  - **Stats:** what your attributes and deity stats convert into (Attack, Double Chance, MP Cost, …), filled in from
    your character if you like, and what a change would give.
- **Progress** on Home: your item level as the game counts it (gear plus Daevanion boards), Kinah today, Abyss Points
  this week, tickets and keys, Daeva Pass XP, XP per hour while levelling, and your pet souls: per pet kind the souls
  toward the next pet or level and which monsters dropped them, with a short notice for each soul and each new pet.
- **All your characters**: AVION2 keeps each character's gear, Kinah, legion, fights and runs apart, and follows the
  one you play, with its portrait from the official site. The character at the bottom of the left rail shows another
  one's without changing anything.
- **Party HP** on the overlay: the other members' HP, also out of view, with a mark when someone is dead and on the
  member a boss is targeting.
- **Group finder** on Home: the room you sit in with every slot's role, class, item level, combat power and ready
  check, and the join requests while you lead; notices when someone asks to join, when the room is full and ready
  and when the dungeon starts. When someone asks to join your room, a small card shows their item level and combat
  power against your room, their gear from the official site and your runs with them.
- **Boss casts** under the boss HP bar on the overlay: the skill being wound up with the time left, and "Interrupted".
- **Low-HP alert** with its own sound.
- **HUD effects** (beta, off by default): after a one-off calibration picture of the game, AVION2 draws on the
  game's own HUD: your HP bar pulses when low, the screen edges flash on big hits (with what hit you), boss HP and
  time to kill under the boss bar, boss skills under it, a mark while the boss targets you, your DPS above your
  HP bar, the chain skill you can use next with its time left, and the party's DPS under the group list.
- **Server lag warning** when the game server freezes, also marked in each fight.
- **Legions**: tags on players, a legion card with your members and when your Legion Hall rent ends (it follows you
  when you leave, create or disband a legion), and a Legions page that compares the legions seen on your server by
  combat power or members, with each legion's real size and leader once you open the game's legion list, and every
  member of your own legion with class, item level and combat power (what AVION2 saw, not an official ranking).
- **Skill and item icons**, the game's own, built in: in fight details, the History page, the HUD, the Characters
  page, the Gear Forge and a run's loot.
- Two themes: **AION2** and **VOID**. Skill and NPC names in 8 languages.
- **Streamer mode**: player, character and legion names show as "You", "Player 2", … everywhere in the app.
- **OBS overlay**: the meter (with the boss's HP and casts) and your character card as an OBS Browser Source (`http://localhost:47421/overlay`), turned on in Settings. Only your own PC can open it.
- **Discord status** (off by default): your map, a dungeon run with its time and bosses, the boss you fight with its
  HP, or gathering, in your Discord profile, with your character's portrait if you like. Other players' names never
  show; your own only if you allow it (never in streamer mode). AVION2 only talks to the Discord app on the PC where
  AVION2 runs.

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

AVION2 is a fan-made tool and is not affiliated with or endorsed by NCSoft. AION2 and its names, class emblems and skill
and item icons belong to NCSoft. The VOID mark belongs to Enter The Void. Skill, NPC, item and map names come from the game
client's own tables; fonts are under the SIL Open Font License. Details: [PROVENANCE.md](assets/gamedata/PROVENANCE.md).

Building from source, the command-line tool and how it works: [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).
