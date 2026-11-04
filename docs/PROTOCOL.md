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
- So does crossing through a **Spacetime Rift** into the other faction's land: Verteron and the rift's Altgard were on
  two different servers, and each crossing (there and back, twice) opened a new connection.
- Before a switch the old connection sends `16 36` with the next server's address as text (`str8`, e.g.
  `193.202.112.54`) and its port (`u16`, 13328), followed by bytes not decoded here; then both sides close the old
  connection (FIN). The new connection's first record is `11 36` (278 bytes, the key exchange, see
  [Entering the world](#entering-the-world)). It shows the `0E 00 36`
  signature about a dozen times only some 2.7 s and 30 KB in, so a reader has to keep what arrives before it decides
  (identity, bag and map load are in there).

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
`11 36` (the client → server direction is encrypted from here). `11 36` is `Handshake_RS` in the client's message
list: `u16 0`, `u32 60000`, `varint 256`, a 256-byte key block, 8 zero bytes and `i32 −7` (SkeeveAN, 57 of 57). AVION2
only uses it as the sign of a new connection and does not read the key block. `15 36` (~4 KB) then describes the chosen character
(name, class, level, legion, position, appearance, character id) and `20 36` enters the world with it.

### `5A E2` — Anti-cheat data
Status: Partly verified (envelope) · AVION2 ignores it and does not decode its content

Once per login (4 in a 4 h session, 216–218 bytes): `result u16` (0), `length varint` (212), then that many bytes of
opaque data.

- **Name.** The client's message list calls it `NCGuardData_RS`, the server's answer to an `NCGuardData_RQ` from the
  client, which is encrypted and unreadable. NCGuard is NCSoft's anti-cheat. The opcode is placed by catalogue order,
  so the name is likely rather than certain.
- **Use.** One tool that analysed the client says the client passes the data to the NCGuard SDK, and only when the
  result is 0.
- **Content.** SkeeveAN and glass-cannon read it as a FlatBuffers table: two u32 values, a 128-byte vector and a
  32-byte vector. It is different at every login, and 168 of its bytes look random (about 7.9 bits per byte). That
  points to key material or a challenge for the anti-cheat, not game state.

It tells a meter nothing, and its content may be security-relevant, so AVION2 neither decodes nor stores it. Do not
publish samples of it.

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

Records with layout 0 carry no value. One follows **each hit that takes stagger**, with the same actor, target and
skill (as do effect ticks such as mantras, and a dodge's effect on the dodger itself). AVION2 uses them to credit
stagger (see [`05 E0`](#05-e0--stagger-gauge)).

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
| flying | −1 000, −1 500 or −2 500 per 100 ms; each `44 37` (`our id`, `00 00`; `CharFlyStart_RS`, the answer to a change of flight mode) switches between −1 500 and −2 500 |
| gliding | −750 per 100 ms, −1 000 for 2 s at a time between two `43 37` (`our id`, `00 00`), and −10 000 at once every ~4 s |
| on the ground | +4 000 per second until full |

At 0 the character lands.

### `1B 92` — Party member HP
Status: Verified (6,900 of 6,900 records read exactly) · `PartyUpdateMemberHPMP_NT`

Sent for every other member of the party, also when they are out of view; never for the player.

| Field | Type | Meaning |
|---|---|---|
| entity | `varint` | the member's entity id (as in [`00 92`](#00-92--party)) |
| hp, hp max | `varint`, `varint` | equal to the member's `00 8D` HP in 2,319 of 2,347 cases |
| mp, mp max, ap, ap max, dp, dp max | 6 × `u32` | |
| alive | `u8` | 0 = dead |

### `21 8D` — Combat state
Status: Verified

| Field | Type | Meaning |
|---|---|---|
| entity | `varint` | |
| — | `00` | |
| state | `u8` | 1 entered combat, 0 left combat |

For the player these bracket the game's own Combat Analysis clock: on just before the first hit, off a few seconds
after the last.

## Boss mechanics

Sources (facts only, no code taken): A2Tools-DPS-Meter (GPL-3.0) and PeakMeter (GPL-3.0) for the stagger attribution
rule; glass-cannon (MIT) for the `09 38` flags and the `48 38` split; A2Core (AGPL-3.0) for the names. Counts are from
four captures (a solo session, a party raid, dungeon1, airraid1).

### `05 E0` — Stagger gauge
Status: Verified (802 of 802, 4,876 of 4,876, 1,264 of 1,264 records; none in airraid1) · `UpdateGroggyInfo_NT`

| Field | Type | When | Meaning |
|---|---|---|---|
| entity | `varint` | | the NPC |
| flags | `u8` | | bit 0 max present, bit 1 left present: `03` for the gauge, `00` for a break |
| state | `u8` | | `01` gauge, `03` broken |
| max | `u32` | `flags & 1` | gauge size (100, 150, 240, 750, 1200 seen) |
| left | `u32` | `flags & 2` | points left |
| status | `u8` | | `02` normal; `03` while the gauge holds still for ~13 s (5 of 14 gauges), meaning unknown |

Two forms in practice: `entity 03 01 max left status` and `entity 00 03 02`, the gauge **broke**. An update with left
= 0 never comes: the break replaces it. The gauge refills to max 5.1–5.2 s after a break. The first record for an
entity is often not full (the gauge was seen mid-fight), so AVION2 does not credit that first difference.

**Who took the points.** A hit that takes stagger is followed by a value-less `04 38` entry of the same actor and
skill (the mark). On each update the drop (previous left − new left) goes to the oldest marked hit since the previous
update not yet credited; without a marked hit, to the last hit since then. A break credits what was left the same way;
that player broke the gauge. Only player skills on someone else count; damage over time does not. Checked: the
credited drops equal the actor's usual amount for that skill in 96.7 % (728 drops), 97.9 % (1,143) and 84.3 % (4,148;
the rest are the same skill at ~1.5×, probably a stagger buff). Typical amounts: Tempest Shot 1, Judgment Thunder 2,
Pummel 3, Punishing Strike 5, Condemnation 7.

### `09 38` — NPC cast with a cast time
Status: Verified (1,095 of 1,095, 344 of 344, 824 of 824, 108 of 108) · `SkillCastingStart_NT` · NPCs only

| Field | Type | When | Meaning |
|---|---|---|---|
| caster | `varint` | | |
| flags | `u8` | | `00`, `02`, `06` |
| skill | `u32` | | 7-digit NPC skill |
| — | `01` | | always |
| kind | `u8` | | always `02` |
| target | `varint` | | 0 = none |
| facing | `f32` | | degrees, may be negative |
| position | `pos` | | where the cast is aimed (the target's position) |
| cast time | `varint` | | ms, 500–3000 seen |
| — | `u8` | | `01` (`02`/`03` rarely) |
| — | `varint` | `flags & 2` | 10000 (glass-cannon: attack speed) |
| targets | `u8` n, n × (`entity varint`, `pos`) | `flags & 4` | the entities the cast is aimed at |

The cast's `02 38` (same caster and skill) follows after 1.00 × the cast time in 962 of 1,095, 774 of 824 and 332 of
344, unless `0C 38` stops it.

### `0C 38` — Cast stopped early
Status: Verified (96 of 96, 7 of 7, 18 of 18, 22 of 22) · `SkillCastingStop_NT`

| Field | Type | Meaning |
|---|---|---|
| caster | `varint` | |
| result | `u16` | `0` the caster died; `0x1808` / `0x1813` interrupted, the caster lives on |
| skill | `u32` | the skill of the open `09 38` |
| cooldown | `varint` | ms; 0, or 3000–30000 after some interrupts |

Every `0C 38` stops an open `09 38` of the same caster and skill, and no `02 38` of it follows. Result 0 comes ~50 ms
after the NPC's last hit. Interrupt codes share the `0x18xx` space of `01 38` refusals; debuffs land on the NPC just
before most interrupts (likely crowd control).

### `48 38` — Telegraph
Status: Verified (layout: 311 of 312, 103 of 103, 1,340 of 1,340, 138 of 138) · Likely (delay) ·
`SkillRangeNoticeFilterLocation_NT`

Where an NPC skill will land.

| Field | Type | Meaning |
|---|---|---|
| caster | `varint` | NPC |
| skill | `u32` | NPC skill |
| cast seq | `u8` | the `seq` of the cast's `02 38` (same tick in 312 of 312) |
| — | `u32` | always 1 |
| index | `u16` | area number within the cast (1 … n) |
| position | `pos` | the area's centre |
| facing | `f32` | degrees, often 0 |
| delay | `u16` | ms until it lands (median 1700–1950, 1000–4200 seen; 6 hits matched at delay + 0–50 ms) |

The air raid's telegraphs lie at z ≈ −4. The area's size and shape are not in the record.

### Dodges
From `02 38`: skill CC000100 is the class's Dodge, CC000101 the dodge done while sprinting or out of combat (A2Core:
CombatDodge / NormalDodge). Both come for other players in range too, and never carry damage. `2E 37` (other players'
dodge moves) misses some of them.

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

The player's combat power is in [`56 36`](#56-36--own-combat-power), not here: an earlier reading of a CP pair before
`05 01 91 00` matched only 1 of 179 records.

### `56 36` — Own combat power
Status: Verified (404 of 404 records; equal to the game's value in 152 of 152) · `CharCombatPowerUpdate_NT` · `55 36`
before June 2026

| Field | Type | Meaning |
|---|---|---|
| combat power | `u64` | now |
| best | `u64` | the highest it has been |

Sent whenever the player's combat power changes (gear, Daevanion nodes, buffs), three times in a row on entering the
world, and on a character switch just before the new character's `33 36`.

### `45 36` — Player spawn
Status: Verified (class) · Partly verified (combat power) · `44 36` before June 2026

| Field | Type | When | Meaning |
|---|---|---|---|
| id | `varint` | | |
| mask1 | `u32` | | |
| mask2 | `u8` | | |
| name | `str8` | `mask2 & 1` | |
| class | `u32` | | [roster class code](#roster-class-codes) |
| position | `pos` | | after the class code |
| legion block | | players in a legion | `u16`, `00 00 00 00`, server `u16`, legion `str8`, `01 00`/`02 00` ([Legions](#legions)) |

The combat power sits after the last `F4 CB 1F` marker: from 11 bytes on, the first `u32` in 1…10,000,000 that is
followed by a zero `u32`. It equals the finder room's CP for the same player in 105 of 115 spawns. A faction byte
follows the class code (1 Light, 2 Dark; Aion2Flow).

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

### `42 36` — Despawn
Status: Verified · `NpcDisappear_NT`

| Field | Type | Meaning |
|---|---|---|
| entity | `varint` | |
| reason | `varint` | 0 in all 17,427 records |
| despawn act | `varint` | 1 already dead when loaded (14,427), 3 died in combat (2,090), 7 left the area (910) |

The names follow the client's field list (key, reason, despawn act). No record had bytes after the third field.

Despawn act 3 is the **body being removed**, a median 4.1 s after the killing blow (493 deaths), not the death itself; use
[`04 8D`](#04-8d--death) for that. Players never get one when they die.

### `04 8D` — Death
Status: Verified (1,501 records: the victim's HP was 0 just before in all of them) · `CharDie_NT`

| Field | Type | Meaning |
|---|---|---|
| victim | `varint` | who died |
| skill | `u32` | the killing skill; 0 for a kill by damage over time or without a cause |
| killer | `varint` | 0 when not said |
| killer server | `u16` | 0 unless a player killed |
| killer name | `str8` | empty unless a player killed |
| killer legion | `str8` | |
| emblem | `u16` | above 0 exactly when the legion is set |
| killer NPC | `varint` | the killer's NPC code when an NPC killed, else 0 |
| die act | `u32` | 0, or an id on scripted deaths |
| reason | `u8` | 1 a hit, 3 damage over time, 0 no cause |
| die type | `u8` | 0, or 2 (the victim is a player, or the kill went to a group) |

- The killer is the last hitter in 752 of 795 kills that name one. Monsters take no damage afterwards (0 of 1,438);
  players can be resurrected.
- **Four shapes**:
  - a player killer while not in a group, named (719);
  - no killer, skill set, die type 2, when a player in a party or alliance killed (273). This is the 23-byte form
    `<victim> <skill> 00 … 01 02`; the killer is withheld;
  - an NPC killer (101, including the player's own deaths);
  - all zeros, for expiring or scripted entities (408).
- It can arrive just before the killing hit in the same packet (254 of 1,501).
- AVION2 used to read it as "ownership" of summons, as some other tools still do.

### Legions
Status: Verified

| Opcode | Record | Fields |
|---|---|---|
| `33 8A` | legion tag of a player in range (`GuildDescUpdate_NT`) | `entity varint`, legion id `u64`, `legion str8`, … A player in no legion: id 0 and an empty name (9 zero bytes after the entity) |
| `03 8A` | legion details | `00 00`, `legion str8`, `leader str8`, counters, texts (`u8` or `u16` length: introduction, notice …) |
| `05 8A` | legion roster | per member: `server u16`, `u32`, `name str8`, `level u8`, …, last login `time` |
| `45 36` (part) | a player's server and legion, in the player spawn | after the position: legion id `u64`, `legion str8`, then `01 00` or `02 00` |
| `01 8A` | the answer to creating a legion (`GuildCreate_RS`) | `result u16`, legion id `u64`, `legion str8`, level `u8`, experience `u64`, join rule, password, emblem, introduction, the grades' rights. A refusal (2070 seen) has the rest zeroed |
| `0B 8A` | the answer to leaving our legion (`GuildLeave_RS`) | `result u16`, reputation penalty `u64`; 10 bytes |
| `35 8A` | the answer to disbanding our legion (`GuildDisband_RS`) | `result u16`; 2 bytes |
| `0C 8A` | a member is out of our legion (`GuildLeave_NT`) | `name str8`, reason `u8` (2 when we disbanded it, naming us), reputation penalty `u64` |
| `39 8A` | a member of our legion came online (`GuildMemberConnected_NT`) | `name str8`; us at login and right after creating a legion |
| `07 8A` | the legion window's list (`GuildRecommendedList_RS`) | `result u16`, `varint n` × legion (below); 100 legions, most members first |
| `09 8A` | a legion name search (`GuildSearch_RS`) | as `07 8A` |

The player spawn (`45 36`) carries the server and legion of players in a legion (players without one have no such
block), 92 to 118 bytes after the name, e.g. `06 01 00 00 00 00 29 05 07 …legion… 01 00 02 00`. AVION2 takes the first
such block after the name as a legion tag. In 45 captures, 892 of 2,145 spawns had it; for the 138 players that also
had a `33 8A` tag, server and legion agreed in 138 of 138. The spawn covers far more players than `33 8A` alone (145
against 38 of 373 players in the largest capture).

A legion id is a `u64` with the server in its top 16 bits and the legion's number in the low 32 (`1a 03 00 00 00 00
29 05`: legion 794 on server 1321); the tag, the spawn block, the lists and the create answer all carry it before the
legion's name, which is why a server number seemed to stand in front of every legion name. The message names are the
client's (A2Core's usmap catalogue: message number = 0x3875 + the opcode's first byte, so `45 8A` is 0x38BA).

A legion on a list (`07 8A`, `09 8A`): legion id `u64`, `name str8`, `leader str8`, level `u8`, experience `u64`,
members `u8`, maximum `varint` (128), emblem `varint`, introduction `str8` (line breaks allowed), join rule `u8` (0, 2
and 3 seen), then 9 or 10 more bytes, all 0 in our records (join requested, request time and race in the client's
layout; their width did not settle, so AVION2 finds the next legion by its id and name).

Leaving, creating and disbanding a legion, in one capture: leaving answers `0B 8A` (result 0) and our own tag turns
empty in the same instant; a refused name answers `01 8A` with result 2070; a created legion answers `01 8A`, then our
tag carries the new legion and `39 8A` names us; disbanding answers `35 8A`, then `0C 8A` names us with reason 2 and
our tag turns empty again. `03 8A` and `05 8A` came only at login and when the legion window opened, not after these.
AVION2 follows our legion through all of them, and through our own tag at map loads, so the Home card and the Legions
page show the legion we are in now. `1A 8A` (`GuildRecvJoinRequests_RS`, 3 zero bytes after creating) and `AF 8A`
(`AgitDecoList_NT`, at login) are not read.

The official site has no legion pages, legion API or legion ranking for Global servers (checked November 2026): its
ranking API answers an empty list there. The Legions page counts the legions AVION2 saw (players around you, your
party, characters you looked up), and takes a legion's real member count, leader and level from the game's legion
list whenever you open it; still not an official ranking.

## Positions

Status: Verified

World units are about **1 cm**; z points up. Headings in `u16` are `× 360 / 65536` degrees.

Each record starts with `entity varint`. In the layouts below `f` is a flags byte, `S` a state byte, `vel` 3 × `f32`
(cm/s), `hd` a `u16` heading (0 = +x, turning toward +y) and `d` an `i8` in cm. Every record has an exact length rule;
AVION2 rejects records that break it. Names are the client's (A2Core's message list); layouts agree with SkeeveAN
(MIT), glass-cannon (MIT) and Waffle (MIT).

**Keyframes** (a full position):

| Opcode | Record | Fields after the entity | Rate |
|---|---|---|---|
| `1B 37` | move start | `f`, [`S` if `f & 1`], `pos`, `hd` × 2, [`S` if not `f & 1`] | |
| `1C 37` | move snapshot (`CharMoveUpdateSnapshot_NT`) | as `1B 37` | ~1 / s per moving entity |
| `1A 37` | move stop | `f`, `u8`, [`u8` if `f & 1`], `pos`, `hd`, [9 bytes if `f & 0x0C`] | |
| `1E 37` | fall or jump start (`CharGravityStart_NT`) | `f`, [`u8` if `f & 1`], `pos`, `hd`, `vel`, [`u8` if `f & 0x14`] | |
| `1F 37` | falling (`CharGravityUpdateSnapshot_NT`) | `f`, `pos`, `hd`, `vel`, [`01` if `f & 2`] | |
| `23 37`, `24 37` | glide start, gliding (`CharGlideStart_NT`, `CharGlideUpdateSnapshot_NT`) | `f`, `pos`, `hd`, `vel`, … (27–29 bytes) | |
| `28 37`, `29 37` | flight start, flying (`CharFlyStart_NT`, `CharFlyUpdateSnapshot_NT`) | `f`, [`S` if `f & 1`], `pos`, `vel`, `hd`, [`S` if not `f & 1`] | |
| `30 37` | dodge roll start (`CharRollingStart_NT`) | `pos`, `hd` × 2, `u8` (no flags) | other players only |
| `36 37`, `37 37` | swimming (likely; no names) | `f`, [`S` if `f & 1`], `pos`, `hd`, `vel`, … | |
| `2F 37` | forward-attack move | `f`, `pos`, direction, `f32` facing, [`01` if `f & 2`] | follows `2E 37` |
| `3C 37` | relocation | `f`, `pos`, `f32` yaw, … | also sent for the player |
| `3E`, `3F`, `40 37` | the player's position | `f`, `u8`, `pos`, `u16` | the player only (5 of 5), rare |

**Offsets** (10 per second per moving entity, no position):

| Opcode | Record | Fields after the entity | Parsed |
|---|---|---|---|
| `1D 37` | on foot (`CharMoveUpdateOffset_NT`) | `f`, [`S` if `f & 1`], `dx` if `f & 2`, `dy` if `f & 4`, `dz` if `f & 8`, `hd` × 2, [`S` if not `f & 1`] | 97,882 of 97,882 |
| `20 37` | falling (`CharGravityUpdateOffset_NT`) | `f`, `dx` if `f & 1`, `dy` if `f & 2`, `dz`, `hd`, `vel`, [`01` if `f & 8`] | 2,662 of 2,662 |
| `25 37` | gliding (`CharGlideUpdateOffset_NT`) | `f`, `dx` if `f & 1`, `dy` if `f & 2`, `dz` if `f & 4`, `hd`, `vel`, [`u8` if `f & 8`] | 1,446 of 1,446 |
| `2A 37` | flying (`CharFlyUpdateOffset_NT`) | `f`, [`S` if `f & 1`], `dx` if `f & 2`, `dy` if `f & 4`, `dz` if `f & 8`, `vel`, `hd`, [`S` if `f & 0x10` and not `f & 1`] | 31,949 of 31,949 |
| `32 37` | dodge roll (`CharRollingUpdateOffset_NT`) | `f`, `dx` if `f & 1`, `dy` if `f & 2`, `dz` if `f & 4`, `u16` (always 0), `hd`, [`u8` if `f & 8`] | 173 of 173 |
| `38 37` | swimming (likely; no name) | as `2A 37`, with `hd` before `vel` | 468 of 468 |

`2D 37` (a parkour offset, 70 records) is not used.

**Applying offsets.** An offset moves the entity by its deltas (a missing axis is 0) from its previous movement
record, keyframe or offset. A keyframe replaces the position and already includes its own tick, so the integrated
track is one tick short when the keyframe comes. Keep one track per entity: entities switch streams (a `1A 37` stop
often ends a `1C`/`1D 37` run). AVION2 starts tracks from keyframes and spawns, not from buff positions, and drops an
offset when the entity had no record for 5 s. Summed this way, on-foot tracks land a median 20–26 cm (p90 76–112 cm)
from the next keyframe, against 158 cm (p90 600 cm) when the last keyframe is held; flight, falls, glides, rolls and
swimming land 13–112 cm off.

Some instances have their floor at z ≈ 0, so a height near 0 is a real position in these records. A position read
at the wrong offset shows up as a tiny float (like 8e-28) instead; AVION2 rejects those.

**The player's own movement is not streamed**: it travels in the encrypted direction, and none of the offset records
or `1B`/`1C 37` snapshots ever carry the player (0 in 559 minutes). The player's position comes from:
- `1A 37`: stops after some skills or crowd control;
- `1E 37`: a fall starting when flight or a glide ends (~0.1 per minute);
- `3C 37`: zone-in and dash skills;
- `3E`–`40 37`;
- [`46 37`](#46-37--own-height): the height;
- self-cast buffs (`2A`/`2B 38`, well under a metre off, ~9 per minute), spawn, identity and cast records, which stay
  the main source.

### `46 37` — Own height
Status: Verified · `LastOnGroundPosZ_NT`

`z f32`. The player's in 333 of 337 records. It is within 36 cm (median) of a self-buff height less than 3 s away. As
a guess of the player's height until the next fix it beats the last fix's height: median 136 vs 241 cm (n 243).

### `4E 36` — Turn
Status: Verified (291 of 291) · `CharRotate_NT`

| Field | Type | Meaning |
|---|---|---|
| facing | `f32` | degrees, where the entity turns to (as cast facing: 0 = +x, toward +y) |
| from | `f32` | degrees |
| reason | `u8` | 0 for the player in 140 of 141; meaning unknown |

Comes for everyone, the player included. Other players' facing equals that of their next cast (median 0°, p90 15°).

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
| 30 | sealed dungeon; also the other faction's land reached through a Spacetime Rift (map 1110 Altgard) |
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
| `38 36` | the player starts using an object | `object varint`, `actor varint`, … |
| `37 36` | the server answers the player's request to use an object (`InteractEnvObj_RS`) | `u8`, `result u16` (0 = OK), `object varint`, 8 bytes; no actor |
| `3A 36` | use finished | `object varint`, `actor varint`, `u8`, …, `item u32` used up (last 4 bytes, 0 = none) |
| `6D 8D` | use finished, state (`InteractionEnvObjFinishState_NT`) | `object varint`, … ; with `3A 36`, or with `39 36` when gathering is aborted |
| `3B 36` | the object can be used again (`InteractEnvObjAvailableState_NT`) | `object varint`; 0–1.5 s after a `3A 36` on it (947 of 961) |
| `05 8D` | the object was used up or destroyed (`EnvObjDie_NT`) | `object varint`; always followed by `35 36` |
| `35 36` | the object left view (`EnvObjDespawn_NT`) | `object varint`, `state u8`, `reason u8`, `act`; state 4, reason 3 = destroyed (after `05 8D`, 513 of 513) |

A **Spacetime Rift** portal is code `1100007` (kind 4). After the position its record carries `f32` facing, `01 01 01`,
the time the rift opened (`time`; 13:00:00 UTC on a Europe server, 1 November 2026) and a `u64` that went from 0 to 31
while the rift stayed open (players who went through, probably). Using it is `38 36` / `3A 36` like any object; the
server switch follows (see [Transport](#transport)).

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
Status: Verified · 46 crafts at cooking, alchemy and handicraft (jewelcrafting) stations, 4 of them combos

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

The materials taken match the recipe in the game data (43 of 46 crafts; the other 3 could not be compared, as the
bag was not known yet when they started). A **bound** material ("Refining Stone (Bound)", item 630530001 or 66xxxxxxx)
is taken in place of the tradable one the recipe names, and the game picks either. No Kinah leaves the bag for a
craft, and no recipe in the game data has a fee.

**Everyone's crafts** at the stations around are broadcast, ours included (without the outcome in advance):

| Opcode | Fields | Meaning |
|---|---|---|
| `72 8D` | `actor varint`, `station varint`, `u8`, craft type `u8` | a player started a craft |
| `74 8D` | `actor varint`, result `u8` (as in `71 8D`), `u8`, `station varint`, `u8`, craft type `u8` | a player's craft ended |

Opening a station's window sends `41 E2` and closing it `43 E2`, as for any NPC (see
[NPC interaction](#npc-interaction)).

### Shops
Status: Verified · two purchases from NPC shops

| Opcode | Fields | Meaning |
|---|---|---|
| `01 8C` | `00 00`, `npc varint`, `00`, `time` | a shop opened (also `41 E2` with the NPC) |
| `03 8C` | `00 06`, npc `u32`, lines `u8`, then per line: goods `u32` (the shop's own id, not the item's), count `u32` | we bought |

The bag update (`1B 56`) with the items and the Kina paid arrives in the same instant.

### NPC interaction
Status: Verified (222 / 217 / 3 records) · names from A2Core

| Opcode | Fields | Meaning |
|---|---|---|
| `41 E2` | `result u16`, `npc u32` (entity), expiry `time` | a conversation or station window opened (`StartNpcInteraction_RS`); expiry = server time + 300 s |
| `43 E2` | `result u16`, `npc u32` | the player closed it (`FinishNpcInteraction_RS`) |
| `46 E2` | `npc u32` | the server ended it, e.g. the player walked away (`CancelNpcInteraction_NT`) |
| `07 40` | `u8 0`, `result u16`, `npc u32` | an NPC conversation's answer (`NpcConversation_RS`, likely); 13 of 14 with a `21 36` map load (teleporters, instance NPCs) |

Failures carry result 15880 or 15881 and entity 0. The entity is the one spawned by `41 36`.

### `01 43` — Airspace Battle
Status: Verified (172 of 172 full records) · `AirspaceBattle_NT`, the answer to the bodyless `00 43` at world entry

| Field | Type | Meaning |
|---|---|---|
| announced | `u8` | 1; the one-byte form `00` carries nothing else and does not cancel an announcement |
| has extra | `u8` | 0 in every record; 1 would add a third time after `end` (SkeeveAN's layout, not seen) |
| — | `u8` | 1 |
| phase | `u32` | 0 before the battle |
| start | `i64` ms | |
| end | `i64` ms | start + 10 min |
| [extra] | `i64` ms | when has extra = 1 |
| n | `varint` | 5 |
| per airspace | `u8 1`, airspace `u8` (1–5), phase `u32`, 14 bytes | all zero so far |

Every capture announces Wednesday 17:00–17:10 local (October 14, CEST, and November 4, CET): Thursday 00:00 Europe
server time both times. `02 43` (AirspaceBattleUpdate_NT) and `03 43` (AirspaceBattleProgressResult_NT, with the owning
legion per A2Core) have not been seen.

### `00 60` / `01 60` — Field events
Status: Verified (31 of 31, 351 of 351) · `FieldEventInfo_NT` / `FieldEventChanged_NT` · layout as nebula-overlay's

`00 60` at every zone entry: `varint n`, then n records, exactly the events open at that moment. `01 60`: one record
at each change.

| Field | Type | Meaning |
|---|---|---|
| kind | `u8` | 3 open, 0 closed |
| event | `varint` | below |
| phase | `u8` | 1 = the 3-minute lead-in, 2 = the event; 0 when closed |
| start | `i64` ms | kind 3: when the event opened |
| phase start, phase end | `i64` ms, `i64` ms | kind 3 |
| — | `i64` ms | kind 0 only, instead of the three times: 2100-01-01, "never" |

| Ids | Event | When |
|---|---|---|
| 110101 … 110901 | **Shugo Festival**: nine minigames at once | every hour at :00:01; phase 2 from :03:01; each closes at its own time (:05:56 to :08:14) |
| 120101, 120201, 120301 | **Dimensional Invasion**: one of the three each hour | every hour at :30:01; phase 2 from :33:01 to :43:01 |

The records arrive 0–2 s before the phase starts. nebula-overlay (GPL-3.0) and glass-cannon (MIT) name the :30 slot
Dimensional Invasion, rolling Beritra Air Raid, Accursed Sword or Incursion of the Spirits; 120101 is likely Beritra
Air Raid (seen in an air raid capture), the other two are not yet matched.

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
| leave | `00 61` stage 4, `05 40` when the exit is used, `47 36`, `21 36` back to the open world, `2F 92` |

**`05 40` map exit** (`MapExit_RS`): `result u16` (0 in 25 of 25). The answer to using an instance's exit: in the same
millisecond as the `21 36` back to the open world in 23 of 24, 3.5 s before it once. Leaving any other way sends none:
the automatic exit after some clears (`00 61` stage 3, then the map load 100 ms later), relogins and character
switches.

Matchmaking for group content arrives just before entering: `00 92` / `0D 92` member records (see
[Party](#party)), `26 92` and `27 92`.

### Reward cube
Status: Verified (layouts) · Likely (points rule) · 5 cubes opened, 1 guaranteed reward claimed

An expedition's final boss leaves a reward cube. It holds the final boss's share and one for every mid boss killed.
Opening it costs Odyle (`0B 61` ticket 60000001). The table sets the price per share (20, 30 or 40). Opening it
double (two shares) gives two rolls. Every share opened adds a point to the expedition's guaranteed-reward counter.
With enough points you pick one item from a fixed list. The mode has its own counter (Exploration, Conquest,
Conquest - Hard).

| Opcode | Record | Layout |
|---|---|---|
| `2E 57` | a mid boss died (`PartyDungeonRewardMiddleBossKill_NT`) | `npc u32` |
| `13 57` | the cube can be opened (`OdEnergyCubeRewardStart_NT`) | `n varint`, n × `npc u32`: the mid bosses whose shares are in it |
| `15 57` | what the cube gave (`OdEnergyCubeReward_RS`) | `result u16`, `shares u8`, `n varint`, n × (`item u32`, `count u32`) |
| `12 57` | the guaranteed-reward counters (`OdEnergyCubeCeilingInfo_NT`) | `n varint`, n × (`dungeon u32`, `points varint`, `claims varint`) |
| `17 57` | a guaranteed reward claimed (`OdEnergyCubeCeilingReward_RS`) | `u16` (`00 02`), `dungeon u32`, `item u32`, `count u32`, `points left varint`, `claims varint` |
| `2D 57` | `SpecialDistributionList_NT` | `00` (an empty list) in 27 of 27, at every world entry |

- **`2E 57`** came 10 times, each a mid boss. Its codes are those in the next `13 57` in 5 of 5 clears.
- **`13 57`** came 0.4–0.5 s after the clear (`01 61`) in 5 of 6 clears. The sixth (Krao Cave Exploration, 11-03
  19:08) had none, and no cube was opened there.
- **`15 57`** follows 10–50 s later, when the cube is opened: 24–25 items for Vakron Sky Island, 9 for Krao Cave
  Exploration. Kinah (`930100031` or bound `930100001`) and Enhance Stones come first. The items also reach the bag
  through `1B 56`. The record parses fully in 5 of 5, with result 0 and `shares` 2 every time. 50 ms before it,
  `0C 61` takes shares × the table's price in Odyle in 5 of 5 (Krao Cave Conquest and Vakron Sky Island 2 × 40,
  Krao Cave Exploration 2 × 20).
- **`12 57`** comes at every world entry together with the inventory list (`11 56`). It arrives 0.2 s before
  `2D 57` and the map load, and before the game names our character (`33 36`, 4–7 s later). After a character switch
  it is the new character's list. 22 of 22 parse fully. An expedition is listed from the first time its cube was
  opened.
  - The dungeon field is the expedition's map id. Each mode has its own: Krao Cave Exploration is 600002 (the
    alt's clear) and Vakron Sky Island Conquest is 600072.
  - Vakron Sky Island's points went 2 → 4 → 6 over three double openings (3 of 3). Krao Cave's went to 2 after one
    double opening.
- **`17 57`** was seen once, 30 minutes after a Krao Cave Exploration clear in the open world. The item (`210140022`)
  is in the cube's guaranteed list. It reached the bag (`1B 56`) in the same millisecond. Points went 2 → 1 and
  claims 0 → 1. That fits the table: the reward takes 1 point (`CeilingMaxCount`) and can be claimed once
  (`CeilingRewardCount`).
  - The leading `u16` is `00 02` although the claim worked. A2Core lists a result there, so its meaning is open.
- **The points rule** (one point per share, the reward takes `CeilingMaxCount` points) is likely, not confirmed:
  it rests on one claim. A `CeilingRewardCount` of 0 (all Conquest modes) may mean no limit; no capture shows it.
- The cube's table is the client's `RewardOdEnergyCube`: bosses, price per share, `CeilingMaxCount`,
  `CeilingRewardCount`, `CeilingItems`. AVION2 ships it as `assets/gamedata/cube_rewards.json`, from SkeeveAN's
  `boss_loot.json` (MIT).
- **Monster cubes** are a separate family with a selection step, seen twice at the end of map 500032 with an
  Invasion key used (`0C 61` ticket 8). They are listed under "Named, not decoded". `0A 57` carries character names.

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
| `10` | cooking ingredients and other crafting stacks |

### Inventory
Status: Verified

| Opcode | Record | Fields |
|---|---|---|
| `11 56` | the whole inventory, on entering the world | item records of different lengths (gear carries more); scan for them |
| `1B 56` | slots changed | `n u8`, then n changes: two flag bytes, `u32` (top byte 0), `00`, `u16`, `00`, the item record (count = new total, 0 = emptied). Records differ in length (gear carries stats, some items an expiry time), so after the first (at offset 11) the next is found by that shape. Rewards change up to 11 slots at once |
| `1C 56` | equipment changed (`ItemEquipChange_NT`), also for players in range | the entity, its item level, and the changed pieces (item id with the old and the new level); an enchant is one cause of it |
| `23 56` | an enchant attempt's result | ends with the new level and the item id; the `1B 56` record already carries the new level |

### Ground loot` section (under `## Items`)

### Ground loot
Status: Verified · `1E 56` and `1F 56` come in batches: a `u8` count, the records, one trailing byte

Drops, takes and our own pick-ups are joined by the drop's **uid**. The first `u32` is the entity the item dropped
from, shared by all drops of one chest, so it is no key.

**`1E 56` dropped**, 63 bytes each:

| Field | Type | Meaning |
|---|---|---|
| source | `u32` | the entity it dropped from (a monster or a chest) |
| position | `pos` | the source's position |
| spot | `pos` | where the item lies, near the source |
| uid | `i64` | the drop's own id: global and rising (read earlier as a source and a serial `u32`) |
| item | `u32` | |
| count | `u32` | |
| owner type | `u8` | `02` a player (1,411 of 1,498); `01` (74) and `05` (13) also seen, meaning not known |
| owner | `u32` | character id with the right to take it |
| — | `00 00` | |
| owner server | `u16` | |
| time | `time` | |
| — | 2 bytes | |

**`1F 56` taken**, 20 bytes each:

| Field | Type | Meaning |
|---|---|---|
| source | `u32` | as in `1E 56` |
| uid | `i64` | as in `1E 56` |
| taker | `u32` | character id, 0 = it vanished (174 of 1,457) |
| — | `00 00` | |
| server | `u16` | |

**`21 56` our pick-up** (`ItemLooting_RS`), 11 bytes:

| Field | Type | Meaning |
|---|---|---|
| result | `u16` | 0 = picked up |
| uid | `i64` | the drop, as in `1E 56` |
| auto-loot | `u8` | 0 |

All 425 records had result 0 and auto-loot 0, and each matched a `1F 56` taken by us in the same instant and an
earlier `1E 56`; none of our 425 takes came without one. It makes a take ours even before the game has named our
character (37 of the 425).

The source is the dropping entity: one chest's 9 drops shared it and its position (each item had its own spot), 588
of 1,498 drops came from an NPC in view, 517 within 15 s of that entity's death. A new drop reused the source of a
drop still on the ground 349 times, so joining by it went wrong: it found 233 of our 425 pick-ups, 23 of them with
another drop's item. By the uid all 425 match.

Items that go to a separate collection (souls, `5346600xx`) are picked up like any drop but never appear in the cube;
the take record is the only trace of them.

### Other item records
Status: Partly verified

| Opcode | Record |
|---|---|
| `15 57` | what an expedition's reward cube gave: `result u16`, `shares u8`, (item, count) pairs (`OdEnergyCubeReward_RS`); see [Reward cube](#reward-cube) |
| `13 56` | an item was used from the bag (`ItemUse_RS`): `result u16`, stack `uid i64`, `item u32` (87 records: serums, wings, statues, tickets) |
| `16 56` | an item was used, by the game itself or from a quick slot (`ItemUse_NT`): stack `uid i64`, `item u32` (189 records: potions and scrolls, the scrolls auto-used about every 5 minutes) |
| `15 56` | an item was used by its item id (`ItemUseByItemID_RS`) |
| `17 56`, `19 56` | an item with a cast time started and finished (`ItemUseStarted_NT` / `ItemUseCompleted_NT`), e.g. a Hall Return Scroll (2.05 s) or Daeva Wings (5.05 s) |
| `83 56` | the achievement list at every zone load, ~40 KB unpacked (`AchievementList_NT`): `n varint`, then n × (`id u32`, `counter u32`, `time`, `level u8`) |
| `84 56` | one achievement updated (`AchievementCountUpdate_NT`): `n varint`, then the same entry fields as `83 56` |

Earlier readings of `13 56` and `17`/`19 56` as legion member status and legion membership were wrong: the Legion Hall
link was the Hall Return Scroll.

## Progress

Status: Verified unless noted · names from A2Core (AGPL-3.0, names only); layouts from our captures; a2kit (MIT),
Daevalog (AGPL-3.0), Scarlet/A2Tools (GPL-3.0), Ottitsch (GPL-3.0) and glass-cannon (MIT) agree where they cover a
record. All of these describe the player's own character.

### `1D 56` — Item level
`ItemEquipLevelChange_NT` · 8 bytes: item level now `u32`, highest so far `u32`. At each world entry and after every
equipment, enchant or Daevanion board change, in the same instant as [`56 36`](#56-36--own-combat-power).

**The item level is the worn gear's item levels added up plus the Daevanion board points** (the type-1 value of
`2E E2`: `u8 n`, then n × (`u8 type`, `u16 points`)). 145 of 157 records equal that sum exactly; the rest are moments
in between (`1D 56` comes ~50 ms before the slot update). Each Daevanion node moves it by exactly 1.

The gear's item levels are in the worn-gear inventory records (container `0B`): a `u32` item level 14 bytes after the
enchant level. It can be above the item data's base level plus the enchant (a +5 piece of base 38 counts 48, a +10
piece of base 45 counts 67), so adding up the base levels undercounts enchanted gear. AVION2 reads this value.

`1C 56` (`ItemEquipChange_NT`) carries the same total for any player in range: `entity varint`, item level `u32`,
`varint n` changed slots, each with the equip slot `u8` and the items before and after (not decoded). For the player
it equals `1D 56` in 57 of 57 records.

### Daily and weekly counts

| Opcode | Record | Fields |
|---|---|---|
| `A5 FF`, `A6 FF` | Kinah gained today, at login / at each gain (`InitKinaLimitStatus` / `KinaLimitStatus`) | gained `u64`, server time `u64` ms; the daily limit is 1,000,000. The reset time is not confirmed by a capture |
| `AB FF` | Abyss Points this week (`AbyssPointUpdate`; `A9 FF` before October 29) | gained `u64`, weekly cap `u64` (1,000,000, now 2,500,000) |

### `0B 61` / `0C 61` — Tickets
`ContentsTickets_NT` (27 of 27) / `ContentsTicketUpdate_NT` (183 of 183)

`0B 61`: `varint n`, then n tickets. `0C 61`: flags `u8` (bit 0: a `u32` charge value at the end), one ticket, reason
`u8` (3 used, 1 recharged by an item, 2 refreshed over time).

| Field | Type | When |
|---|---|---|
| mask | `u8` | |
| id | `u32` | |
| count | `varint` | `mask & 4`; left out = 0 |
| charged | `varint` | `mask & 8`, on top of the count (Odyle, Nightmare) |
| time | `u64` | `mask & 1` (Abyss passages) |

Ids: 60000001 Odyle Energy (840 regular plus charged; −80 per reward), 4 Nightmare (−1 per clear), 80000001
Ascension (at entry), 1 Shugo keys and 8 Invasion keys (likely), 10000001 and up Abyss passages, 10 and 12 refreshed
about every 10 minutes. The names come from what changes them, not from game data.

### `07 8D` — Experience
`ExpUpdate_NT` · 17 bytes, only while levelling: experience within the level `i64`, gained `i64`, reason `u8` (1 a
kill: the player's kill within 1 s in 102 of 113; 2 a quest: a turn-in within 2 s in 42 of 42). It starts over at each
level up (`08 8D`).

### Pets
Pets and their souls belong to the **account**: two characters received the same roster.

| Opcode | Record | Fields |
|---|---|---|
| `00 90` | the pet roster at login (`RideInitData_NT`) | default mount `u32`, `varint n` × (pet `u32`, pet `u32`, level `u32`) owned, `varint n` × (pet `u32`, souls `u32`), `varint n` × (crystal type `u8`, amount `u32`), … |
| `0D 90`, `0E 90` | souls gained / spent (`RideSoulPointIncrease` / `Decrease`) | `varint n` × (pet `u32`, souls `u32`); 1–3 per kill, 5 spent per new pet, 25 for level 2 |
| `09 90` | a new pet (`CharRideAcquire_NT`) | `entity varint`, pet `u32`, level `u32`, creature type `u8`, board level `u32`, board XP `u64` (+500 per pet of a type); with the `0E 90` spending 5 souls, and `08 90` (`_RS`, `entity varint`, result `u16`) in the same ms |
| `14 90` | a pet levelled up | result `u16` (0), pet `u32`, level `u32`, creature type `u8`, board level `u32`, board XP `u64`; 23 bytes. Seen 3 times, each to level 2, with an `0E 90` spending 25 souls of that kind |
| `11 90` | soul crystals gained (`RideSoulCrystalIncrease_NT`) | `varint n` × (type `u8`, amount `u32`), reason `u8`; not read |

A soul arrives as a bag item (ids 534660000–534669999) about a second before the `0D 90` that counts it; our pickup
of it (`21 56`) comes about 50 ms before. The pet id to item id offset is not constant; AVION2 takes the pairs and
the pets' names from the client's pet table (VehicleList, built into the app). For a kind a newer patch adds, it
learns the pair: from a gain of one kind with one soul item just before it, or from a gain of several kinds when the
counts picked up of each item tell them apart. The soul's drop (`1E 56`) names the monster it fell from, so AVION2
counts which monsters drop each soul.

Souls of a kind keep coming after the pet is owned (71 of one kind at level 2 in our captures); they pay for its
levels. Level 2 costs 25 souls (5 level-ups, in `14 90` and in roster diffs between logins), level 3 costs 75 and
is the highest (the client's VehicleLevel table). The roster's level per owned pet changes accordingly (63 pets at level 1, 5 at level 2, 1 at level 3
in one account).

## Quests
Status: Seen once · two kill-ten quests done and turned in

| Opcode | Fields | Meaning |
|---|---|---|
| `10 8D` | `quest u32`, `state u8`, `01`, `count u32`, `00 00` | progress, one record per step (1 … 10); at the last step a second record follows with state 3 instead of 2: the objective is done |
| `0F 8D` | `00 00`, `quest u32`, `00` | the quest was turned in |
| `2C 8D` | `kind u8`, `result`, `id u32`, `npc u32` (an entity id), objective index | a quest step done by talking to an NPC (`QuestStepNpcConversation_RS`) |

Quest ids seen: 167 and 168. A turn-in comes with the reward in the bag (`1B 56`), several `84 56` records and pass
progress (`24 E3` id 10044: +150 per quest; the battle pass, see [Daeva Pass](#daeva-pass)). A `10 8D` of another
shape follows each turn-in
(`09 00 00 00 02 01`, then varints, one of them +10 per quest), probably a counter of quests done.

## Daeva Pass
Status: Partly verified · `24 E3` at every world entry, two rewards claimed (`23 E3`)

Pass progress is `24 E3` (`BattlePassInfoList_NT`, partly decoded): `varint n` seasons, each with its id `u32` (10044 =
the first Light season, September 30 to December 16; 10047 the next), pass XP this season `u32`, pass XP this week `u32`
(it starts over at the weekly reset), a `time`, an `i64`, the pass ids, the claimed (pass, `level u8`) pairs, then about
22 bytes not decoded. Pass XP comes from each successful gather (+30; an earlier note said per craft), every 10 kills
(+60) and quest turn-ins (+150 or +200); `84 56` id 810433000 (BattlePassExp) counts it as well. The pass level and
the weekly cap are not in the record (the claimed reward level is not the level reached).

**`23 E3` reward claimed:** `00 00`, `pass u32` (10044, 10047), `u32` (810433000, 810439000; 810433000 also names the
pass points in `84 56`), `level u16` (44, 13). The reward arrives in the bag (`1B 56`) in the same instant: Kina from
the first, a Nightmare Dungeon Challenge Ticket and 20 Small Odyle Energy from the second.

## Party

Party records (`92` family) describe the real party; group-finder records (`97` family) describe the finder room,
which exists before the party does and survives several runs. Two rules apply to all of them:

- **Bools are bits of one byte shared across the whole message**, low bit first: a new byte appears where a bool is
  needed and the previous byte is used up, so it can fall inside any record.
- **Optional fields** depend on a mask byte at the start of a struct.

Strings in these records are a `varint` length and UTF-8. A character is an `i64`: the low 48 bits are the character
number (the `u32` character id elsewhere), the high 16 bits its creation server. Every member record carries a
36-character account id string; AVION2 skips it and never keeps it.

### `00 92` — Party
Status: Verified (22 of 23 records read exactly; the 23rd, from an October build, carries one more 121-byte block) ·
`PartyInfo_NT` · sent after every change

| Field | Type | When | Meaning |
|---|---|---|---|
| mask | `u8` | | bit 0: 9 more bytes after the rules |
| party | `i64` | | stable while the party lasts |
| leader | `i64` | | character |
| size | `u32` | | 5 |
| rules | 4 × `u8` | | `02 01 00 02` |
| — | 9 bytes | `mask & 1` | |
| n | `varint` | | members, **the player included** |
| members | n × member | | below |
| reason | `u8` | | 0 created, 1 joined, 3 a member left, 5 reconnected (after entering the instance) |

**Member** (also the whole body of `0D 92` and `22 92`):

| Field | Type | When | Meaning |
|---|---|---|---|
| mask | `u8` | | `01` legion, `02` emblem, `04` rebirth skills |
| slot | `u8` | | 1–5 |
| entity | `u32` | | the member's entity id in the player's zone; 0 when elsewhere |
| home server, current server | `u16`, `u16` | | the current one is 4xxx inside an instance |
| account | string | | skipped |
| character | `i64` | | |
| name | string | | |
| class | `u32` | | [roster class code](#roster-class-codes) |
| hp, hp max | `varint`, `varint` | | 0 when not in the player's zone |
| mp, mp max, ap, ap max, dp, dp max | 6 × `u32` | | |
| map | `i64` | | low `u32` the map id, high `u32` the instance |
| subzone | `u32` | | |
| position | `pos` | | |
| alive | bit | | |
| legion | string | `mask & 1` | |
| emblem | `u32` | `mask & 2` | |
| level, conqueror level, item level | 3 × `u32` | | |
| can loot, logged in | bit, bit | | |
| rebirth skills | `varint` n, n × (`u8` mask, `skill u32`, `level u8`, [`u32` if mask bit 0]) | `mask & 4` | |
| combat power | `i64` | | |
| latency | `u8` | | 0–5 |

| Opcode | Record | Fields |
|---|---|---|
| `0D 92` | a member joined (`PartyAddMember_NT`) | one member (27 of 27 exact) |
| `22 92` | a member came back with a new entity id after loading into the instance (`PartyReconnectMember_NT`) | one member |
| `21 92` | a member dropped off the world server (`PartyDisconnectMember_NT`): when leaving, and when zoning into the instance at the start | `character i64`; only the next `00 92` tells whether they left |
| `1C 92` | a member's status (`PartyUpdateMemberStatus_NT`), every few seconds, also on other maps; never for the player (5,874 of 5,874) | `character i64`, `class u32`, map `u32` (0 while loading), instance `u32`, subzone `u32`, `pos`, level, conqueror level, item level (3 × `u32`), legion string, emblem `u32`, can loot `u8`, combat power `i64`, latency `u8` |
| `11 92` | new leader (`PartyNewLeader_NT`) | `character i64`; the next `00 92` names the same leader |
| `1F 92` | party disbanded | `reason u8` (8 = the dungeon expired) |
| `1F 93` | the player left the party (likely, 7 records) | party `i64` (the `00 92` key), `reason u8`: 3 left (no record of that party follows), 10 replaced by a new party (its `00 92` follows in the same millisecond). No disband is sent when the player leaves |
| `0E 92` | a member's buff added (`PartyAbnormalAdd_NT`), also out of range and for the player | `entity varint`, `n u8`, n × `abnormal u32` |
| `1A 92` | a member's buff removed (`PartyAbnormalRemove_NT`) | `entity varint`, `abnormal u32` |
| `20 92` | the party member a boss targets (`PartySetTargetByBoss_NT`) | `entity varint` (the member's or the player's entity id), or `00` when cleared (the boss died or dropped its target); every non-zero entity was a member of the party record of that time or the player (201 of 201), and it equals the target the game shows for the player's selected boss (`35 38`) in 161 of 164 checks |
| `16 92` | party focus target (likely) | `u32 0`, `id u32`, `u32 0` |

A party started from the finder begins at `18 97`: `00 92` with only the player (reason 0), then one `0D 92` per other
member, all in the same millisecond. A party formed by invitation has no finder room at all.

### `02 97` — Group-finder room
Status: Verified (348 of 348 read exactly; layout as Daevalog's) · sent every ~10 s while the player sits in a room
(during a run it can pause for minutes), and after every change

| Field | Type | Meaning |
|---|---|---|
| room | `u32` | the room number; a room re-listed after a clear gets a new one |
| title | string | the recruiting text (free text; may hold control characters) |
| size | `u8` | 5 |
| dungeon | `u32` | 600001, 600002, 600072 …: the id of the client's Dungeon table (600002 Krao Cave, Exploration; 600072 Vakron Sky Island, Conquest) |
| stage | `u8` | |
| faction | `u8` | |
| leader | `i64` | character |
| public | bit | |
| join type, state | `u8`, `u8` | state 0 recruiting, 2 in the dungeon, 3 cleared |
| n, slots | `varint`, n × slot | empty slots included (mask 0) |
| affixes | `varint` n, n × `u32` | |
| reason | `u8` | 1 created by the player, 2 the player joined, 4 a member left, 5 a member removed, 7 a member updated, 9 refresh, 10 state changed |

**Slot** (also the whole body of `0B 97`, 28 of 28, and `1F 97`, 12 of 12):

| Field | Type | When | Meaning |
|---|---|---|---|
| mask | `u8` | | `02` ready, `04` home server, `08` current server, `10` inside the instance (revive items); 0 = empty slot |
| slot | `u8` | | |
| character | `i64` | | |
| name | string | | |
| class, level, item level | 3 × `u32` | | [roster class code](#roster-class-codes) |
| ready | bit | `mask & 2` | 1 in all 1,268 slots that carry it: the field is there only while the member is ready |
| online | bit | | clears while the member is between servers (zoning into the instance) |
| home, current server | `u16` | `mask & 4`, `& 8` | |
| role | `u8` | | the role the slot asks for, also on empty slots: 1 tank, 2 damage, 3 healer, 4 any |
| combat power | `i64` | | |
| tickets | `u8` | | |
| revive items | `i64` | `mask & 0x10` | |
| mentoring, latency | `u8`, `u8` | | |

The first slot byte was read earlier as "nearby / elsewhere": it is this mask, so its bit `02` means ready. The leader
readies by starting: the leader's slot has no ready field until the members are inside. In all 7 starts seen, every
slot was taken and every member but the leader ready before the start (0.7 to 9.6 s before it).

| Opcode | Record | Fields |
|---|---|---|
| `01 97` | room list (`DungeonRoomList_RS`) | `result u16`, `dungeon u32`, the player's own room (all zero when none), `varint n`, n rooms |
| `0B 97` | a member joined the room | one slot |
| `0D 97` | the player sent a room invitation | `result u16`, `server u16`, name, `entity varint`, `class u32`, expiry `time` (~20 s ahead), `u32 0`; a `0B 97` from the same player follows 1–3 s later (10 of 10) |
| `07 97` | a join request to the player's room (only the leader gets it) | `room u32`, `character i64`, `class u32`, `level u32`, `item level u32`, name, `server u16` (not the character's own server in 3 of 11), `u32 0`, `combat power i64`, `u8 1`, expiry `time` (~20.6 s ahead; the same player asking again comes just after it). 11 of 11 exact |
| `09 97` | the answer to accepting or refusing a request | `result u16` |
| `13 97` | the answer to joining a room | `result u16`, `u32` |
| `04 97` | the answer to creating a room | 6 × `00` |
| `15 97` | the player readied | `u16 0`, `ready u8` (2 of 2) |
| `16 97` | a member readied or unreadied | `character i64`, `ready u8` (25 of 25: 23 ready, 2 not); the next room record agrees |
| `18 97` | the instance starts | `00 00`; sent only to the leader (5 of 5 in rooms the player led, none in the 2 starts the player joined as a member, where the room's state turning 0 → 2 is the start) |
| `19 97` | leader changed (likely) | character, `01` |
| `1B 97` | a room is gone (likely) | `room u32`: the room the player just left or that a re-listed room replaces (7 of 7) |
| `1D 97` | the player left the room | `u16 0` (4 of 4) |
| `1E 97` | a member disconnected, as `21 92` | character |
| `1F 97` | a member's slot updated | one slot |
| `25 97` | a join request was cancelled | `character i64` (1 record: the requester of a `07 97` 11 s earlier) |
| `29 97` | the player removed a member | `u16 0` |

### `96` — Force (raid of up to 20)
Status: Unconfirmed (no `96` record in our captures; from Waffle's schema)

`02 96` force info (force, leader, vice-leaders, size, parties of members), `0A 96` member added, `1B 96` reconnected,
`1D 96` party added, `1E 96` member moved, `22 96` member removed, `2B 96` member HP (as `1B 92`), `2C 96` member
status (as `1C 92`, in another order), `1A 96` disbanded, `2E 96` left. Inside a force, `00 92` holds only the
player's own party.

## Other records

Status: Partly verified · layouts read from our captures, names from the client's message list

| Opcode | Record | Fields and evidence |
|---|---|---|
| `03 38` | a projectile launched at a target | `caster varint`, `00`, `target varint`, …, the target's `pos`, flight time `varint` ms; its seq matches the `02 38` cast, and the hit (`04 38`) lands flight + 50 ms later (95–99.7 %). Ranged skills only |
| `0E 38` | an NPC hit along a short line | `caster`, `02`, `target`, `u32`, counter, two positions (at the target, ~1 m back toward the caster); the `04 38` follows within 50 ms |
| `32 38` | another player's target (`LockOnTarget_NT`) | `player varint`, `00`, `target varint`; only for other players; their next cast within 2 s has the same target in 96–97 % |
| `13 38` | one skill's level (`SkillLevelUpdate_NT`) | 21 at each map load (reason 10), one when a skill point is spent (reason 3, `05 51` follows) |
| `01 38` | the player's skill accepted or refused | flags, `result u16`, `skill u32`; refusal codes `0x1802`, `0x1813`, `0x181B`, `0x181D`, `0x181E`, `0x184D` |
| `2E 37` | another entity's skill-driven move (`CharForwardAttackMoveStart_NT`) | `f`, `skill i32`, state, `pos`, direction, facing, target, …; mostly Dodge (`CC000100`) and the sprinting dodge (`CC000101`); never the player; the `02 38` of the same skill follows (> 99 %) |
| `30 37` | rolling start (`CharRollingStart_NT`) | no skill; `32 37` follows |
| `21 37`, `22 37` | another player's sprint on / off (`CharMoveSprintOn_NT` / `Off_NT`) | entity only; `41 37` / `42 37` are the player's own (`_RS`) |
| `46 36` | shields on an entity (`CharBarrierInfo_NT`) | `entity varint`, `n`, n × (abnormal instance, remaining shield); an empty one for the player after every `33 36` |
| `47 36` | a player left view (`CharDisappear_NT`) | `entity varint`, `reason` (1 out of range), teleport effect id |
| `4B 36` | speed modifiers (`MoveSpeedUpdate_NT`) | `entity varint`, `n u8`, n × (`stat u16`, `value i32`), as `4A 36`; never for the player; stats 205, 208, 214. A boss reached 205 = 10000 300 s into a fight (rage), seen once |
| `02 8D`, `03 8D` | glide / sprint cooldown | `entity varint`, `u32` ms (2000 / 0) |
| `07 8D` | experience (`ExpUpdate_NT`) | see [Experience](#07-8d--experience) |
| `15 8D` | scripted texts and cameras (`EventFuncAction_NT`) | text keys; type 19 captions, 43 NPC speech bubbles, 9 cutscenes, 15 notes, 45 camera |
| `52 8D` | a monster's first aggro (`NpcFirstAggro_NT`) | `npc varint`; combat starts within 0.5 s (371 of 376) but no field or dungeon boss had one, so it is no boss-pull marker |
| `07 95`, `08 95` | an NPC emote starts / ends (`SocialActionStart_NT` / `End_NT`) | `entity varint`, `action u32` (from the NPC's spawn in 250 of 287), `01` / `02` |
| `01 95` | the player's emotes (`PcSocialActionInfo_NT`) | 36 emote ids at login |
| `0C 91` | world-map markers (`WorldMapAbyssRankerInfo_RS`) | `result u16`, `map u32`, `n`, n × (`01`, `pos`, `time`); after each `01 91` while the world map is open, mostly empty |
| `01 40`, `02 40` | map environment state and setting (`MapEnvState_NT` / `MapEnvSetting_NT`) | `map u32`, state, `time`, …; `02 40` 51 ms after every zone change (87 of 87). `01 40` also comes mid-dungeon (state 1, then 3), so it is no zone signal |
| `45 8A` | our legion's Legion Hall (`GuildAgitInfo_RS`) | `result u16`, `hall u32`, rent end `time`; 14 bytes. At most map loads, not only Legion Hall teleports (146 records). Hall 1 with an end in 93, hall 0 and time 0 for a character whose legion has none (54). The end was 2026-11-08 16:24:40 UTC in the October captures and exactly one hour later in November (renewed or a clock-change effect, not known). Not sent with the character's name: AVION2 takes it only while the game has named us |
| `08 61` | `DungeonClearKillNpc_NT` | one byte, 0 in all 113 records; at instance entries and map loads |
| `09 61` | `DungeonDeathLimit_NT` | a `u32`, 0 in all 95 records, within 1 s of a `00 61` stage; whether it counts deaths or is the limit is not known until a non-zero value is seen |
| `02 90`, `05 90` | the player mounted / dismounted (`CharGetOnRide_RS` / `CharGetOffRide_RS`) | `entity varint`, a fixed `u16` (`2D 3B` / `38 3B`), ride id 1204, … |
| `03 90`, `06 90` | another player mounted / a dismount, forced or by another player | `entity varint`, … ; `06 90` reason 1, 2, 3 |
| `2E 92`, `2F 92` | joined / left a map event (`MapEventJoin_NT` / `Leave_NT`) | `event u32` |
| `00 44`–`03 44` | the vote after a dungeon clear (likely) | party members with their names; AVION2 does not read it |

### Group requests, Daevanion and skill points
Status: Partly verified · names from A2Core

| Opcode | Record | Layout | Seen |
|---|---|---|---|
| `02 E0` | `GroupRequestProgressed_NT` | `key u64`, `entity u32` (ours) | 19 |
| `04 E0` | `GroupRequestComplete_NT` | `key u64` | 19, in the same millisecond as each `02 E0` |
| `2E E2` | `DaevanionPointUpdate_NT` (used points) | `n u8` (5), n × (`type u8`, `points u16`) | 97 |
| `2F E2` | `UpdateItemDaevanionPoint_NT` (bonus points from items) | as `2E E2` | 6 |
| `05 51` | `SkillPointUpdate_NT` | `n u8` (1), n × (`type u8`, `points u16`) | 91 |

- **`02 E0` and `04 E0`:** the keys rise, and each shares its id with a `35 E3` that names a scripted sequence
  (`…_Seq_1021`). A2Core's catalogue also has `GroupRequestPeerComplete_RQ/RS`. Likely a scene the whole group
  waits on.
- **`2E E2`:** only type 1 moved. It rose one step at a time while an alt opened Daevanion nodes (`28 E2`, `03 E5`)
  on 11-03. A2Core names the list "used points", which fits: it counts points spent, not points gained.
- **`05 51`:** type 1 counts 174–186 and type 2 counts 1–4 in our captures. A2Core writes a varint where we see a
  `u16`. What the two types are is open.

AVION2 decodes these but does not show them: what each type means is not known.

### Named, not decoded

Seen in normal play. The names come from the client's message list, placed by its order (likely, not certain).

| Opcode | Name | Seen |
|---|---|---|
| `01 51`, `00 51` | AddSkillList, MySkillList | the login skill list |
| `00 57`, `02 57`, `07 57`, `0A 57`, `0B 57`, `0D 57`, `0F 57` | MonsterCubeReward, …Select_RS, …SelectComplete_RS, …Result_RS, …FinishSelect_NT, …AutoSelect_RS, …List_RS | a monster cube with a pick among five, twice at the end of map 500032 (Invasion key); `0F 57` is `00 00 00` (an empty list) at every map load (33 of 37), else the open cube (its `u64` uid also in `00 57`); `0A 57` holds character names |
| `00 39`, `07 39` | SpecializedSkillLoad, SpecializedSkillUpdate | specialisation slots |
| `26 E2`, `28 E2`, `29 E2`, `2B E2`, `2D E2`, `03 E5` | Daevanion board info, open node, close node, reset board, open route | node ids 709xxx, board 71 |
| `8C E2`, `8D E2`, `93 E2`, `94 E2`, `96 E2`, `48 E2` | presets (hotbars, gear, skills, titles, decorations), ItemEquip | `48 E2` 50 ms after each `8D E2` |
| `15 E2`, `74 E2`, `00 E2`, `23 E2` | LoadPost, FriendList, MessageCardInitData, WorldChatFreeChance | login |
| `57 E2`, `58 E2`, `24 E2`, `5C E2` | ContentsUnlockList / Update, ContentsControl, ContentsFeatureOnOffListByAdmin | login |
| `56 E2`, `62 E2`, `61 E2`, `63 E2`, `51 E2`, `37 E2` | PeriodCollectionList, TrustLimitInfo, TradeServerInfo, BMShopTutorialInfo, BossChallengeList | login |
| `30 93`, `31 93`, `19 93` | BookmarkedContentList, PartyDungeonChallenge, SetWorldMarker? | |
| `0A 40`, `00 40` | ArenaMatchingInfo, CharPhaseUpdate | |
| `02 43`, `03 43` | AirspaceBattleUpdate, AirspaceBattleProgressResult | not seen yet |
| `2D 37` | a parkour offset | 70 records |
| `01 8C`, `07 8C` | NpcShopOpen, CoinShopOpen | |
| `73 E2`, `25 E2` | RankingPointUpdate, UpdateCharAscension | |
| `26 37`, `33`–`35 37`, `45 37`, `48`/`49`/`4B`/`4C 37` | movement records | rare |

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

The November 2026 pass (deaths, party, movement offsets, combat power, progress records and the names above) checked
every claim against 51 captures from October 13 to November 3 and drew on:

| Source | License | Used for |
|---|---|---|
| [glass-cannon](https://github.com/xinjalin/glass-cannon) | MIT | layouts and counts across the protocol |
| [Aion-DPS-Meter](https://github.com/SkeeveAN/Aion-DPS-Meter) (SkeeveAN) | MIT | movement, achievements, key exchange, session records; the expedition reward cube table (`cube_rewards.json`) |
| [waffle_meter](https://github.com/Waffle-ens/waffle_meter) | MIT | party and force layouts, combat power |
| [a2kit](https://github.com/nuriland/a2kit) | MIT | item level, death record |
| [A2Core](https://github.com/Addiboii/A2Core) | AGPL-3.0 | the client's message names (a server emulator; names only) |
| [Daevalog](https://github.com/Seralth/Daevalog) | AGPL-3.0 | the finder room layout |
| [Aion2Flow](https://github.com/cloris-chan/Aion2Flow) | GPL-3.0 | map state, party HP, faction byte |
| [nebula-overlay](https://github.com/DLL-P/nebula-overlay) | GPL-3.0 | field event layout |
| [aion2-cube-radar](https://github.com/dr89mh/aion2-cube-radar) | GPL-3.0 | object and movement records |
| Wingbeat (Klauensprung) | no license file | the death record's field order (read, not used) |
| aion2-dps-meter (RonaldoBueno16), Aion2Meter (a2meter), aion2fundps (tonyHan123) | MIT / none | party and stagger leads |

Some names come from tools that read the game client's own data (A2Core, Waffle); they are used here as names only.
