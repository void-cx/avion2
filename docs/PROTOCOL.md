# AION2 Network Protocol

A reference for the AION2 game traffic as of the post-June-2026 client: how packets are framed and compressed, and
the layout of every packet type that has been decoded. It covers what a passive reader can see: the server → client
direction, read from the network without touching the game.

Each packet section starts with a status line:

- **Verified**: checked field by field against live traffic and the game's own UI.
- **Partly verified**: the fields that matter were checked; others are inferred.
- **Unconfirmed**: taken from earlier public research or a single observation.

Working notes, measurements and the history behind each result are kept in
[PROTOCOL-NOTES.md](https://github.com/void-cx/avion2/blob/main/docs/PROTOCOL-NOTES.md).

---

## Conventions

| Notation | Meaning |
|---|---|
| `u8` `u16` `u32` `u64` | unsigned integers, **little-endian** |
| `i64` | signed 64-bit integer, little-endian |
| `f32` | IEEE 754 single, little-endian |
| `varint` | unsigned LEB128, at most 5 bytes (32 bits) |
| `str8` | `u8` length, then that many bytes of UTF-8 |
| `pos` | three `f32` (x, y, z): a world position, see [Positions](#positions) |
| `time` | `u64` milliseconds since the Unix epoch |

Opcodes are two bytes, written **in wire order** (`04 38`) as they appear in a hex dump. Field tables start right
after the opcode and list the fields in order; a **When** column marks fields that are only present sometimes.

## Transport

- The game uses TCP. **Server → client data is plaintext**; client → server data is encrypted, so the client's own
  actions (movement, chat, skill input) are only visible through the server's answers.
- The same server port may also carry a TLS connection. A connection whose first segment is a TLS record header
  (type `14`…`17`, version `03 00`…`03 04`, length 1…16640) is not game traffic.
- Instances (dungeons, strongholds, arenas) are served by **separate servers**: entering one moves the game stream to
  a new TCP connection and leaving moves it back. A reader has to follow the stream across connections.

### Finding the game connection
Status: Verified

The server sends a small periodic record whose frame ends in `0E 00 36` (`06 00 36` before June 2026) about 19 times a
second, even when nothing happens. A connection that shows this signature a dozen times within three seconds is the
game stream; the server is the side sending it. Addresses and ports differ between regions, so this is more reliable
than a fixed list.

### Capturing reliably

- Cut each payload at the **IP total length**, not the frame length: some network cards pad short frames with non-zero
  bytes. When the total length is 0 (segmentation offload), use the end of the frame.
- Reassemble TCP by sequence number: handle wrap-around, retransmissions and partial overlaps, and give up on a gap
  after a bound instead of waiting forever.
- Latency tools that relay the game through `localhost` make the stream visible only on the loopback device.

## Framing

```
00                                    zero padding: skip one byte
17 03 03  <u16 BE length>  …          a stray TLS record: skip 5 + length bytes
<varint L>  <opcode: 2 bytes>  body   a packet
<varint L>  FF FF  <u32 size>  data   a compressed bundle
```

| Rule | Detail |
|---|---|
| Frame size | `L − 4 + (bytes used by the varint)`: a 1-byte varint gives `L − 3`, a 2-byte one `L − 2` |
| Legacy flag | older clients put a byte in `F0`…`FE` right after the length; skip it |
| Bad length | ≤ 0, or a frame over 65,535 bytes: advance one byte |
| Incomplete frame | wait for more data only up to 16,384 bytes (65,535 when it looks like a bundle), else advance one byte |
| Bodyless packets | some packets are the opcode only; never assume a body |

### Bundles
Status: Verified

| Field | Type | Meaning |
|---|---|---|
| marker | `FF FF` | |
| size | `u32` | decompressed size, 1…1,000,000 |
| data | bytes | a **raw LZ4 block** (not the LZ4 frame format) |

The decompressed bytes are more frames in the same format, zero padding included; bundles nest up to four deep. Inside
a bundle, stop at the first impossible length rather than resynchronising.

Bundles also appear **embedded in the middle of another packet**, preceded by a 1–3 byte varint; identity and roster
records often arrive this way. Damage, spawn, HP and identity records are therefore best found by scanning packets,
not only at frame starts.

## Lobby

Status: Partly verified · plaintext server → client, like the world connection

Before the world connection, the client talks to a **lobby server** on a separate connection for a few seconds:
account, server list, character list and the chosen server, ending with a redirect to the world server. The lobby
closes as the world connection opens. Its packets are the `xx 39` family; the framing is the same as below.

| Opcode | Record | Content |
|---|---|---|
| `01 39` | handshake | an encrypted key block |
| `06 39` | account | account identifiers (GUID strings) |
| `09 39` | server list | see below |
| `0B 39` | characters | the account's characters on the servers: name, level, legion, … |
| `0D 39` | server chosen | `00 00 00`, `server u16` |
| `0F 39` | redirect | `00 00`, `server u16`, `host str8`, `port u16` of the world server |

**`09 39` server list**: `00 00`, `count u8`, then per server:

| Field | Type | Meaning |
|---|---|---|
| id | `u16` | the server id used in player records (e.g. 1321) |
| faction | `u8` | 1 Light, 2 Dark |
| id | `u16` | again |
| — | `00 00` | |
| name | `str8` | the internal name, e.g. `LIVE_Light_021` |
| status | 6 bytes | load or status (not decoded) |

Observed: ids 1301–1323 are `LIVE_Light_001…023`, ids 2301–2323 `LIVE_Dark_001…023`.

### Entering the world
Status: Partly verified

The world connection starts with a key exchange: the client sends its public key (`10 36`), the server answers with
`11 36` (the client → server direction is encrypted from here). `15 36` (~4 KB) then describes the chosen character
(name, class, level, legion, position, appearance, character id) and `20 36` enters the world with it.

## Combat

### `04 38` — Direct damage
Status: Verified · one packet can hold several records back to back, without separators

| Field | Type | When | Meaning |
|---|---|---|---|
| target | `varint` | | entity hit |
| switch | `varint` | | layout = `switch & 0x0F` (0 = no value: an aura or effect tick); flag bits below |
| flag | `varint` | | 0 in all observations |
| actor | `varint` | | entity hitting |
| skill | `u32` | | 8-digit player skill or 7-digit NPC skill |
| uid | `u8` | | |
| hitType | `varint` | | 2 normal, 3 critical (1 miss, 6 resist per earlier research) |
| plotter | `u8` | `switch & 0x02` | hit modifiers, see below |
| restoreHP | `varint` | `switch & 0x02` | |
| angle | `u8` | `switch & 0x02` | `0x01` back, `0x02` front |
| extra | `u32` | `switch & 0x01` | unknown |
| effect | `u32` | | skill effect code = skill × 100 + n |
| seq | `u32` | | hit index within the cast: 1, 2, 3 … |
| scalar | `varint` | after a `00` | the attacker's damage multiplier in 1/100 % (10000 = 100 %) |
| value | `varint` | layout ≠ 0 | total damage, additional hits included |
| hits | `varint` + n × `varint` | `switch & 0x20` | additional hits, already counted in value |
| linked | `u8`, `u32` | `switch & 0x40` | a linked effect code (e.g. damage absorbed by a shield) |
| end | `u8`, `00` | | the low byte of seq, then `00` |

| Plotter bit | Meaning | Plotter bit | Meaning |
|---|---|---|---|
| `0x01` | shield block | `0x10` | iron wall |
| `0x02` | parry (about half damage) | `0x20` | regeneration |
| `0x04` | perfect | `0x40` | perfect block |
| `0x08` | double | | |

Skill ids 3,000,000…3,099,999 map to `id × 10 + 1` (Theostones). Skill ids 16990000…16999999 and 16770000…16779999
are **links between a spirit and its owner**, not damage.

### `05 38` — Damage and healing over time
Status: Unconfirmed

| Field | Type | Meaning |
|---|---|---|
| target | `varint` | |
| effectType | `u8` | `02`/`0A` damage, `01`/`09` heal, `0B` heal over time, `00` status, `08` buff |
| actor | `varint` | |
| — | `varint` | unknown |
| skillRaw | `u32` | the skill is `skillRaw / 100` |
| amount | `varint` | |

### `02 38` — Skill cast
Status: Verified · sent for every entity in range, including buffs and heals and the player's own casts

| Field | Type | When | Meaning |
|---|---|---|---|
| actor | `varint` | | |
| — | `00` | | |
| skill | `u32` | | |
| sequence | `u8` | | |
| kind | `u8` | | 2 = aimed at the ground |
| target | `varint` | | |
| facing | `f32` | | degrees; 0 along +X, turning toward +Y (`atan2(dy, dx)`) |
| target position | `pos` | kind 2 | |

**`06 38`** follows every `02 38` for the same actor and skill, almost always in the same bundle (rarely up to 2.5 s
later): `actor varint`, `skill u32`, `counter u16` rising by one with each of that actor's casts.

### Own skills and target
Status: Verified on one session (Chanter at a training dummy, 13 skills)

Sent only for the player. Skill ids here are `u32` as on the skill bar (`18010000`).

| Opcode | Fields | Meaning |
|---|---|---|
| `01 38` | `00 00 00`, `skill u32` | a skill was used and its cooldown starts. Every skill the player uses, including Dodge and Dash; not for auras and passive procs. The cooldown length is not sent: the client has it, and no record marks the cooldown ending |
| `18 38` | `skill u32`, `01`, `next u32` | `next` can be used now: a chain step after `skill` (Onslaught → Resonance Crush → Bolt Crush) |
| `19 38` | `01`, `next u32` | the chain step `next` is gone: used, or expired after 3 s |
| `3B 38` `3D 38` | `00 00` | around each targeted skill: `3B 38` with its `02 38`, `3D 38` about 50 ms later |
| `34 38` | `00 00 00`, `target varint` | the player selected a target; `00` = none |
| `35 38` | `entity varint`, `00`, `target varint` | the selected target's own target ("target of target"); `00` = none. Sent on selecting and when it changes, e.g. a monster turning to the player once hit |
| `31 38` | `00 00`, `target varint`, `00 00` | the target the player attacks; `00` when combat ends |

### Buffs and debuffs
Status: Verified

**`2A 38` added** (`2B 38` changed has the same fields from abnormal on, without the leading `01`):

| Field | Type | When | Meaning |
|---|---|---|---|
| entity | `varint` | | who has it |
| — | `01` | | |
| flags | `u8` | | |
| instance | `varint` | | identifies this application, used by `2C 38` |
| abnormal | `u32` | | the buff or debuff |
| length | `i64` | | ms, −1 = permanent |
| end | `i64` | | server-epoch ms, 4102412400000 = never |
| caster | `varint` | | |
| level | `u8` | | |
| skill | `u32` | `flags & 2` | |
| reapplied | `u8` | | 2 = reapplied |
| position | `pos` | | where the caster was; for a self-cast buff, the entity's own position |

**`2C 38` removed**: `entity varint`, `n u8`, then n times:

| Field | Type | When | Meaning |
|---|---|---|---|
| kind | `u8` | | |
| instance | `varint` | | as in `2A 38` |
| reason | `u8` | | 1 = expired |
| remover | `varint` | `kind & 7 = 7` | |
| skill | `u32` | `kind & 7 = 7` | |
| effect | `u32` | `kind & 7 = 7` | |

### `00 8D` — Resources
Status: Verified

| Field | Type | Meaning |
|---|---|---|
| entity | `varint` | |
| kind | `u8` | |
| — | `u8` | |
| resource | `u8` | 0 HP, 1 MP, 6 flight time (below), 3 / 4 other gauges |
| value | `u32` | current value; the maximum is not sent (learn it from the highest value seen or from spawns) |

**Flight time** (resource 6) counts in 1/10 000 s; full is 570 000 (57 s). Sent every 100 ms while flying, every
second while it refills.

| While | Change |
|---|---|
| taking off | −2 500 once |
| flying | −1 000, −1 500 or −2 500 per 100 ms; each `44 37` (`our id`, `00 00`) switches between −1 500 and −2 500 (probably a speed boost) |
| gliding | −750 per 100 ms, −1 000 for 2 s at a time between two `43 37` (`our id`, `00 00`), and −10 000 at once every ~4 s |
| on the ground | +4 000 per second until full |

At 0 the character lands.

### `1B 92` — HP
Status: Unconfirmed

| Field | Type |
|---|---|
| actor | `varint` |
| hp | `varint` |
| hpMax | `varint` |

### `21 8D` — Combat state
Status: Verified

| Field | Type | Meaning |
|---|---|---|
| entity | `varint` | |
| — | `00` | |
| state | `u8` | 1 entered combat, 0 left combat |

For the player these bracket the game's own Combat Analysis clock: on just before the first hit, off a few seconds
after the last.

## Entities

### Entity and character ids

| Id | Scope | Used in |
|---|---|---|
| entity id | **per zone**: reissued on every zone change | almost every record (`varint`) |
| character id | stable per character | party roster, loot, own identity (`u32`, followed by `00 00` and the server `u16`) |

Entity ids above 1,000,000 in spawn records map to `(id & 0x3FFF) | 0x4000`.

### `33 36` — Own character
Status: Verified · sent on every map load and periodically, often inside embedded bundles

| Field | Type | When | Meaning |
|---|---|---|---|
| id | `varint` | | the player's entity id |
| mask1 | `u32` | | |
| mask2 | `u8` | | bit 0 = name present |
| name | `str8` | | |
| server | `u16` | | 1000…2999 |
| class | `u32` | | [roster class code](#roster-class-codes) |
| — | `u8` | | |
| level | `u32` | | |
| … | | | further in the record, located by pattern: |
| character id | `u32`, `00 00`, `u16` server | | right after the character's position |
| combat power | `u32`, `0 u32`, `u32`, `0 u32`, `05 01 91 00` | | found by the trailing bytes |

### `45 36` — Player spawn
Status: Verified (class) · Unconfirmed (combat power) · `44 36` before June 2026

| Field | Type | When | Meaning |
|---|---|---|---|
| id | `varint` | | |
| mask1 | `u32` | | |
| mask2 | `u8` | | |
| name | `str8` | `mask2 & 1` | |
| class | `u32` | | [roster class code](#roster-class-codes) |
| position | `pos` | | after the class code |

Earlier research places the combat power after the last `F4 CB 1F` marker: from 11 bytes on, the first `u32` in
1…10,000,000 that is followed by a zero `u32`.

### `41 36` — NPC, monster and summon spawn
Status: Verified · `40 36` before June 2026

| Field | Type | Meaning |
|---|---|---|
| id | `varint` | |
| kind | `u8` | see below |
| — | `20 00` | |
| npcCode | `u24` | the `u24` right before `00 40 02` or `00 00 02` (within the first 60 bytes) |
| — | `00 00 02` or `00 40 02` | |
| position | `pos` | |
| … | | |
| HP | `01`, `varint` hp, `varint` hpMax | within 64 bytes of the code |

| Kind | Entity | Kind | Entity |
|---|---|---|---|
| `04`, `05` | monsters | `1C` | effect entities |
| `0C`, `0D` | NPCs | `1D`, `1F`, `5D` | spirits, ground spells |
| `14` | boss helpers (deal damage, no owner) | `5F` | summons and pets |

### `42 36` — Death and despawn
Status: Verified

| Field | Type | Meaning |
|---|---|---|
| entity | `varint` | |
| — | `varint` | 0 |
| how | `varint` | 1 already dead when loaded, 3 died in combat, 7 left the area |

### `04 8D` — Ownership
Status: Verified · links summons, spirits and effects to the player who made them (monsters are claimed this way too)

| Field | Type | Meaning |
|---|---|---|
| spawned | `varint` | the summon, spirit or effect |
| skill | `u32` | the skill that created it |
| owner | `varint` | |
| server | `u16` | |
| owner name | `str8` | |
| legion | `str8` | |

A 23-byte short form carries no owner.

### Legions
Status: Verified

| Opcode | Record | Fields |
|---|---|---|
| `33 8A` | legion tag of a player in range | `entity varint`, 5–6 bytes, `server u16`, `legion str8`, … |
| `03 8A` | legion details | `00 00`, `legion str8`, `leader str8`, counters, texts (`u8` or `u16` length: introduction, notice …) |
| `05 8A` | legion roster | per member: `server u16`, `u32`, `name str8`, `level u8`, …, last login `time` |

## Positions

Status: Verified

World units are about **1 cm**; z points up. Headings in `u16` are `× 360 / 65536` degrees.

| Opcode | Record | Fields | Rate |
|---|---|---|---|
| `1C 37` | move | `entity varint`, `mode u8` (5 = one more byte follows), `pos`, `heading u16` × 2, `01` | ~1 / s per moving entity |
| `1D 37` | steer | `entity varint`, `flags u8`, `i8` deltas …, `heading u16` × 2 | ~10 / s, no absolute position |
| `1A 37` | position | `entity varint`, 2 bytes, `pos` | |
| `1B 37` | position | `entity varint`, [`05`] `03`, `pos` | |
| `2F 37` | dash, leap | `entity varint`, `u8`, `pos` | |
| `3C 37` | relocation | `entity varint`, `u8`, `pos` | also sent for the player |

**The server does not echo the player's own movement**: it travels in the encrypted direction. The player's position
can only be followed through records about it: self-cast buffs (`2A`/`2B 38`, well under a metre off), relocations
(`3C 37`), spawn and identity records, and cast records.

## World

### `21 36` — Map load
Status: Verified

| Field | Type | Meaning |
|---|---|---|
| count | `u32` | |
| map | `u32` | see [Map ids](#map-ids) |
| — | `u64` | |
| position | `pos` | where the player appears |
| facing | `f32` | |
| … | | |
| content | `u8` | at offset 43, see below |

| Content code | Place |
|---|---|
| 0 | open world |
| 12 | reload inside the current instance |
| 30 | sealed dungeon |
| 37, 38, 40, 65 | stronghold |
| 72, 80 | other instances (e.g. ascension instances) |
| 79 | expedition |

The content code also arrives just before the map load in `47 36` (`self varint`, `02`, `content u8`).

### `23 36` — Zone change
Status: Verified

A body starting with `00` (entity 0) is the player's own teleport or zone change. Entity ids are reissued from here on.

### Time
Status: Verified

| Opcode | Record | Fields |
|---|---|---|
| `00 36` | server clock, ~20 / s | `u64` ms |
| `03 36` | every 10 s | `00 00`, `u64` server uptime ms, `time` |

### `01 91` — Field bosses of the current map
Status: Verified · repeats about once a second on maps with field bosses

`00 00`, `map u32`, `count u8`, then count times:

| Field | Type | When | Meaning |
|---|---|---|---|
| state | `u8` | | 1 alive, 0 dead |
| spawn code | `varint` | | `map × 100 + n` |
| position | `pos` | state 1 | |
| — | 0–1 byte | | |
| time | `time` | | alive: when it spawned · dead: the announced respawn |

### World objects
Status: Verified · gathering nodes, portals, chests, hidden cubes

| Opcode | Record | Fields |
|---|---|---|
| `34 36` | object in range | `entity varint`, `kind u8`, `u8`, `code u32`, `pos`, … |
| `38 36`, `37 36` | the player starts using an object | `object varint`, `actor varint`, … |
| `3A 36` | use finished | `object varint`, `actor varint`, `u8`, …, `item u32` used up (last 4 bytes, 0 = none) |
| `35 36` | object state changed | `object varint`, state bytes (e.g. after opening) |

Hidden cubes are objects with code `1200039`; opening one uses up item `630900003` (Key: Hidden Cube) and the loot
drops at the cube's position.

### Gathering
Status: Verified · 28 gatherings on ore, plant and gem nodes

The server decides a gathering's **outcome when it starts**; the success and fail bars on screen only play it out.

**`31 8D` started:**

| Field | Type | Meaning |
|---|---|---|
| — | `00 00` | |
| node | `varint` | the gathering node (object) |
| attempt | `u32` | gatherings on this node so far, this one included (1, 2, 3) |
| outcome | `u8` | bit 0 = success; bits 1–2 = the better grade: 0 fail, 1 success, 6 fail (fine node), 7 fine success |
| — | `00 00` | |
| grade | `u8` | 1 normal, 2 fine (the item the panel shows) |
| — | `00 00 00` | |
| steps | `u8` | how many steps the bar race on screen has; the steps follow (27 bytes each: kind `u8`, `01`, timing values) |

**`33 8D` ended:**

| Field | Type | Meaning |
|---|---|---|
| result | `u8` | 0 success, 1 success with the better grade, 2 failed |
| — | `u8` | |
| node | `varint` | |
| item | `u32` | the item gathered (or that would have been) |
| count | `u32` | 0 when it failed |

`3A 36` (use finished) follows on the node, and `1B 56` updates the bag.

**Aborted:** no end record comes. Instead `39 36` (use interrupted: `object varint`, `u8`, `u8` (`06 19`),
`actor varint`, `attempt u32`) arrives, with `3E 36` (`00 00`, `object varint`) and `6D 8D` (`object varint`).

**Blocked for aborting too often:** the **fourth abort within 5 minutes** gets gathering blocked; this held for all
nine aborts in one session (three blocks, none on a third abort). Aborting a gathering that would succeed counts too,
and a block does not reset the count: one more abort 104 s after a block blocked again. The window lies between
4 min 34 s (an abort that long ago still counted) and 5 min 5 s (one that long ago did not).

The server puts abnormal **10020** on the player with `2A 38`, caster the player: 60 000 ms the first two times,
120 000 ms when blocked again 104 s after the last block. The game then says "Essence Extraction has been restricted
due to repetitive behavior." While it lasts, every try to gather gets a refused `31 8D` of 19 bytes: `1C 38` instead
of `00 00`, then the node and zeros, and no bar race. `2C 38` removes the abnormal (reason 1, expired) when it runs
out.

### Crafting
Status: Verified · 27 crafts at cooking and alchemy stations, 4 of them combos

Like gathering, the server decides a craft's **outcome when it starts**, including whether a **combo** follows: after the
first race succeeds, a second race for the better recipe (two items) runs. A failed combo yields nothing.

**`6F 8D` started:**

| Field | Type | Meaning |
|---|---|---|
| — | `00`, `u8` | |
| station | `varint` | the crafting station (object) |
| recipe | `u32` | |
| outcome | `u8` | bit 0 = the first race succeeds; bit 2 = a combo follows; bit 3 = the combo succeeds (`01` success, `05` combo that fails, `0D` combo that succeeds) |
| — | `00` | |
| races | | per race: steps `u8`, the steps (10 bytes each: kind `u8`, `00`, two equal `u32`), `00`; a second race when a combo follows |

**`71 8D` ended:**

| Field | Type | Meaning |
|---|---|---|
| result | `u8` | 0 success, 1 combo success (two items), 2 failed |
| — | `u8` | |
| station | `varint` | |
| — | `u8` | |

The materials leave the bag (`1B 56`) when the craft starts, the crafted item arrives with the end record. `7C 8D` and
`7D 8D` follow with the craft's proficiency (craft type `u8`: 3 handicraft, 4 alchemy, 5 cooking, then points).

**Everyone's crafts** at the stations around are broadcast, ours included (without the outcome in advance):

| Opcode | Fields | Meaning |
|---|---|---|
| `72 8D` | `actor varint`, `station varint`, `u8`, craft type `u8` | a player started a craft |
| `74 8D` | `actor varint`, result `u8` (as in `71 8D`), `u8`, `station varint`, `u8`, craft type `u8` | a player's craft ended |

Opening a station's window sends `41 E2` (`00 00`, station `u32`, …) and closing it `43 E2` (`00 00`, station `u32`).

### Shops
Status: Verified · two purchases from NPC shops

| Opcode | Fields | Meaning |
|---|---|---|
| `01 8C` | `00 00`, `npc varint`, `00`, `time` | a shop opened (also `41 E2` with the NPC) |
| `03 8C` | `00 06`, npc `u32`, lines `u8`, then per line: goods `u32` (the shop's own id, not the item's), count `u32` | we bought |

The bag update (`1B 56`) with the items and the Kina paid arrives in the same instant.

The **marketplace** (other players' listings) does not use the game connection: opening it starts a few short HTTPS
connections, so its listings and prices are encrypted web traffic.

## Instances

### `00 61` — Instance stage
Status: Verified

| Field | Type | Meaning |
|---|---|---|
| map | `u32` | |
| stage | `u8` | see below |
| from | `time` | |
| to | `time` | 0 = open-ended |

| Stage | Meaning |
|---|---|
| 2 | running; `from` = when it was entered |
| 3 | **cleared**; `to` = `from` + 10 s, the automatic exit |
| 4 | leaving; `to` = `from` + 10 min (likely the time until it can be entered again) |

### Map events and objectives
Status: Verified · instances with objectives (strongholds) run a map event

| Opcode | Record | Fields |
|---|---|---|
| `2E 92` | event started | `event id u32`, `name str8` (e.g. `World_L1_GarrisonTerritory_R3_MapEvent`), …, `time` |
| `2D 92` | progress | `event id u32`, `state u8` (2 running, 3 done), `01`, `step u8` (1, 2 …), `n u8`, n counters, `08`, … |
| `2F 92` | event ended | `event id u32` |

A step begins when the one before it is done, so step changes mark each objective's completion; state 3 closes the
last one. Open-world events use the same records with a single step whose counter rises.

### Entering and leaving

| Step | Records |
|---|---|
| use the entry portal | `38 36` / `3A 36` on the portal object |
| announce the place | `47 36` with the content code |
| load it | `21 36`, then `00 61` stage 2 and `2E 92` |
| clear | `2D 92` state 3, `00 61` stage 3 |
| leave | `00 61` stage 4, bodyless `05 40`, `47 36`, `21 36` back to the open world, `2F 92` |

Matchmaking for group content arrives just before entering: `00 92` / `0D 92` member records (an id string, server,
name, legion), `26 92`, `27 92` and `1F 93`.

## Items

Inventory records describe the player's own character only.

### Item record
Status: Verified

| Field | Type | Meaning |
|---|---|---|
| item | `u32` | |
| count | `u64` | |
| container | `u8` | see below |
| slot | `u8` | |
| — | 11 × `00` | |
| enchant | `u8` | gear only, in one of the next two bytes |

| Container | Holds |
|---|---|
| `01` | the cube (bag), currencies included |
| `04` | quest items |
| `0B` | equipped gear |
| `0F` | warehouse |

### Inventory
Status: Verified

| Opcode | Record | Fields |
|---|---|---|
| `11 56` | the whole inventory, on entering the world | item records of different lengths (gear carries more); scan for them |
| `1B 56` | one slot changed | `01`, `u8`, `00`, `u32`, `u32`, then the item record at offset 11 (count = new total, 0 = emptied) |

### Ground loot
Status: Verified · both come in batches: a `u8` count, the records, one trailing byte

**`1E 56` dropped**, 63 bytes each:

| Field | Type | Meaning |
|---|---|---|
| drop id | `u32` | identifies the drop until it is taken |
| position | `pos` | where it lies |
| — | `pos` | a second spot nearby |
| source | `u32` | likely the corpse it dropped from |
| serial | `u32` | global, rising |
| item | `u32` | |
| count | `u32` | |
| — | `02` | |
| owner | `u32` | character id with the right to take it |
| — | `00 00` | |
| owner server | `u16` | |
| time | `time` | |
| — | 2 bytes | |

**`1F 56` taken**, 20 bytes each:

| Field | Type | Meaning |
|---|---|---|
| drop id | `u32` | as in `1E 56` |
| source | `u32` | |
| serial | `u32` | |
| taker | `u32` | character id, 0 = it vanished |
| — | `00 00` | |
| server | `u16` | |

Items that go to a separate collection (souls, `5346600xx`) are picked up like any drop but never appear in the cube;
the take record is the only trace of them.

### Other item records
Status: Partly verified

| Opcode | Record |
|---|---|
| `15 57` | rewards of a cleared instance as (item, count) pairs |
| `83 56` | ~40 KB at login with item ids (not decoded) |
| `84 56` | `01`, `u64`, `u64` time, `u8` per item: a cooldown or acquisition time (not decoded) |

## Party

### `02 97` — Party roster
Status: Partly verified · sent after every change (join, leave, zone); it can outlive a party

A header (party id, recruiting text), then per member:

| Field | Type | Meaning |
|---|---|---|
| flag | `u8` | `0C`/`1C` nearby, `0E`/`1E` elsewhere or offline |
| slot | `u8` | |
| character | `u32` | character id |
| — | `00 00`, 2 bytes | |
| name | `str8` | |
| class | `u32` | [roster class code](#roster-class-codes) |
| level | `u32` | |

`01 97` is the group finder board (listings with names and recruitment texts; not decoded). `0B 97` and `0D 97`
announce a player joining (not decoded).

## Not decoded yet

Seen in normal play and described by shape only.

| Opcode | Size | Shape | Probably |
|---|---|---|---|
| `03 38` | ~37&nbsp;B | `caster varint`, `00`, `target varint`, `u32`, counter, …, `pos` | an effect placed at a position |
| `09 38` | ~33&nbsp;B | `caster`, `u32`, `01 02`, `target`, `pos` | like `03 38` |
| `0E 38` | ~47&nbsp;B | `caster`, `02`, `target`, `u32`, counter, two positions | something moving between two points |
| `35 36` `37 36` `6D 8D` `46 37` | small | `entity varint` with the player's id | targeting and object interaction |
| `05 E0` | 16&nbsp;B | `entity`, `03 01`, max `u32`, current `u32` falling by 2 | a gauge (not flight time: absent while flying) |
| `07 95` `08 95` | 14&nbsp;B | `entity varint`, `u32` (fixed per entity, e.g. 1252, 1264; or 10000 / 20000), `01` / `02`; `08 95` then `07 95` 150 ms later, every few seconds, for NPCs standing around | an idle animation, or a timer in ms |
| `0C 91` | ~30&nbsp;B | `map u32`, `01 01`, `pos`, `time` | something placed on the map with a time |
| `15 8D` | ~55&nbsp;B | dialogue keys (`Talk_L1_Garrison_…`), entity at the end | NPC dialogue |
| `24 E3` | 64&nbsp;B | `01`, id `u32` (10044, 10047), two points `u32`, `time`, … ; both points +30 after each successful craft, none after a failed one | progress of a pass or event |
| `13 56` | 16&nbsp;B | `04 03`, `id varint`, `00 00`, `varint`, `00`, `02`, `varint` | a legion member's status: one each time the chat says a legion member logged in, and one for the player entering the Legion Hall |
| `17 56` `19 56` | 16&nbsp;B | `entity varint` (the player), then the same fields as `13 56` | the player's own legion membership, when the legion window opens and its Hall tab is used |
| `45 8A` `09 61` `08 61` | 3–16&nbsp;B | `45 8A` a fixed 16-byte record; `09 61` `00 00 00 00`; `08 61` `00` | always together, at each teleport into or out of the Legion Hall |
| `0C 61` | 10&nbsp;B | `00 04`, counter `u32` (10, then 12), `0E 02` | progress in the Legion Training Grounds |
| `01 43` | 50–130&nbsp;B | ids around 10044–10047 with start and end times | event or season schedule |

## Identifiers

### Skills

Player skills have 8 digits, `CC SS NNNN`. The base skill is `raw − raw % 10000`; the low digits encode
specialisation choices. NPC skills have 7 digits.

| Prefix | Class | Prefix | Class |
|---|---|---|---|
| 11 | Gladiator | 16 | Elementalist |
| 12 | Templar | 17 | Cleric |
| 13 | Assassin | 18 | Chanter |
| 14 | Ranger | 19 | Fighter |
| 15 | Sorcerer | | |

### Roster class codes

Used in identity, spawn and roster records; four codes per class.

| Codes | Class | Codes | Class |
|---|---|---|---|
| 5–8 | Gladiator | 25–28 | Sorcerer |
| 9–12 | Templar | 29–32 | Cleric |
| 13–16 | Ranger | 33–36 | Chanter |
| 17–20 | Assassin | 45–48 | Fighter |
| 21–24 | Elementalist | | |

### Items

| Item | Id |
|---|---|
| Kina (Bound) | `930100001` |
| Kina | `930100031` |
| Enhance Stone | `930100023` |
| Abyss Points | `930100003` |
| Key: Hidden Cube | `630900003` |

The game's currency bar shows the sum of both Kina items in the cube.

### Map ids

| Range | Content |
|---|---|
| below 100000 | open world (e.g. 1010 Verteron, 1110 Altgard, 20 / 22 / 23 Reshanta) |
| 152xxx, 153xxx, 154xxx | strongholds; map events `GarrisonTerritory_R1…R5`, `H1…H5`, `D1…D5` |
| 200xxx, 210xxx | an ascension instance chain |
| 3100xx, 400001 | sealed dungeons |
| 6000xx | expeditions |
| 800xxx, 900xxx | other instances |

## Prior work

This reference builds on public research by other passive meters (facts only, no code taken):
[A2Tools-DPS-Meter](https://github.com/taengu/A2Tools-DPS-Meter) (GPL-3.0),
[Aion2-Dps-Meter](https://github.com/TK-open-public/Aion2-Dps-Meter) (MIT; before the June 2026 changes) and
[NOIA2](https://github.com/ZDYoung0519/NOIA2) (GPL-3.0). Also worth reading: nuriland/a2kit (a Go decoder) and
lunixxx37/aion2-protocol-research.
