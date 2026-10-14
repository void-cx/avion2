# AION2 Network Protocol — Working Notes

Status: compiled 2026-10-13 from public research. Sections marked ✅ were verified against our own
capture (EU, Chanter lvl 45, map 1010). Treat everything else as unconfirmed.

Sources (facts only — do not copy code; A2/NOIA2 are GPL-3.0, TK is MIT):
- **A2** — taengu/A2Tools-DPS-Meter (Rust, GPL-3.0, updated 2026-10-13). Most current reference.
- **TK** — TK-open-public/Aion2-Dps-Meter (Kotlin, MIT). Common ancestor; ended 2026-06-19, stale since the June patch.
- **NOIA2** — ZDYoung0519/NOIA2 (Rust, GPL-3.0, legacy 4.0 branch).
- Also worth checking: nuriland/a2kit (Go decoder), lunixxx37/aion2-protocol-research.

---

## 1. Transport

- TCP. Only **server → client** is parsed; it is plaintext. Client → server is encrypted.
- KR server port seen: `13328`. Don't hard-code — detect (below).
- The same server port also carries a **TLS** connection; exclude any connection whose first segment is a TLS record header
  (type `0x14..0x17`, `03`, minor `00..04`, BE len `1..16640`).

### Game-connection detection ✅ (locked EU server 193.202.112.231:13328 in ~1 s)
- Record terminator signature `0E 00 36` (pre-June 2026: `06 00 36`) appears ~19×/s even when idle.
- Lock a flow when ≥12 signature hits occur within 3 s. Candidate port = segment's `src_port` (server side).
- Prefer loopback devices (ping reducers like ExitLag relay through localhost): give loopback a ~2.5 s head start.
- Reset lock after 120 s with nothing parsed.

### Capture pitfalls
- Truncate payload to the **IP total length**, not frame length — some NICs pad short frames with non-zero bytes
  (caused 77% damage loss in A2). If total length is 0 (TSO), use the frame end.
- Link types: Ethernet (VLAN tags 0x8100/0x88A8), Null/loopback (4-byte `02 00 00 00`), raw IP (DLT 12/14/101/228),
  Linux cooked v1/v2.
- Do proper TCP seq reassembly (handle wraparound, retransmits, partial overlap, gap-skip after a bound).

## 2. Framing

```
00                                     zero padding → skip 1 byte
17 03 03 <u16 BE len> ...              stray TLS record → skip 5+len
<varint L> <opcode u8 u8> <body>       packet; total frame bytes = L - 4 + varintLen
<varint L> FF FF <u32 LE size> <lz4>   compressed bundle
```

- Varint = LEB128 unsigned, max 5 bytes / 32 bits.
- **Frame size = `L - 4 + len(varint)`** (1-byte varint → L-3, 2-byte → L-2). Using a fixed L-3 loses ~3% of packets.
- Opcode written in wire order, e.g. `04 38`.
- Legacy (TK): byte `0xF0..0xFE` right after the length was an "extraFlag" to skip. A2 no longer sees it; tolerate it.
- Resync: bad/≤0 length → advance 1 byte; frame > 65535 → advance 1; incomplete frame: wait only if ≤16384
  (≤65535 if it looks like a bundle header), else advance 1.

### Bundles (compression)
- `FF FF` + `u32 LE decompressed_size` (1..1,000,000) + **raw LZ4 block** (not frame format).
- Decompressed content = more varint-framed packets (zero padding possible). Nesting up to depth 4.
- Bundles also appear **embedded mid-packet** (varint 1–3 bytes before `FF FF`); these carry identity/roster records.
- Inside bundles: stop at the first unparseable length rather than resyncing.
- Some records (damage, spawn, HP, identity) are found by **scanning** packets, not only at frame starts.

## 3. Opcodes (post-June 2026)

Offsets start after the 2-byte opcode. `varint` = LEB128.

### `04 38` — Direct damage ✅ verified 2026-10-13 (live capture, 249/251 records)
```
target   varint
switch   varint        layout = switch & 0x0F (0 = no value: aura/effect tick, e.g. mantras)
                       0x02 plotter block, 0x01 extra u32, 0x20 additional hits, 0x40 linked effect
flag     varint        (always 0 so far)
actor    varint
skill    u32 LE        e.g. 18010000 Onslaught; 7-digit = NPC skill
uid      u8
hitType  varint        2 Normal, 3 Critical (1 Miss, 6 Resist per prior art)
[switch & 0x02]  plotter u8, restoreHP varint, angle u8 (0x01 back, 0x02 front)
[switch & 0x01]  u32 ?
effect   u32 LE        skill effect code = skill×100 + n (e.g. 1801000011)
seq      u32 LE        hit index within the cast: 1, 2, 3 …
[00 pad] scalar varint attacker multiplier in 1/100 % (10000 mobs, ~10690 our lvl-45 Chanter)
[layout ≠ 0] value varint   total damage, includes additional hits
[switch & 0x20]  count varint + count × varint (additional hits, already in value)
[switch & 0x40]  u8 + u32 linked effect code (e.g. damage absorbed by Protection Circle)
seq u8, 00             terminator repeats low byte of seq
```
- **Correction to prior art:** the `01 00 00 00` "anchor" is the hit index, not a constant, and the
  "layout 4 extra varint" doesn't exist. Records chain without separators. The terminator is `seq 00`.
- Plotter bits (from prior art, Parry and Perfect seen live): `0x01` ShieldBlock, `0x02` Parry (about half damage),
  `0x04` Perfect, `0x08` Double, `0x10` IronWall, `0x20` Regeneration, `0x40` PerfectBlock.
- Skill filters: `3,000,000..3,099,999` → `id*10+1` (Theostone). Skills 16990000–16999999 / 16770000–16779999
  are spirit↔owner **link records**, not damage.

### `05 38` — DoT / HoT
```
target varint, effectType u8, actor varint, unknown varint, skillRaw u32 LE, amount varint
```
- effectType (exact match): `0x02`/`0x0A` damage, `0x01`/`0x09` heal, `0x0B` HoT, `0x00` status, `0x08` buff.
- skill = `skillRaw / 100`. Count damage only for known DoT skill ids (need an allowlist).

### `33 36` — Own character
```
id varint, mask1 u32, mask2 u8 (bit0 = name present), nameLen u8, utf8 name,
server u16 LE (1000-2999), class u32, u8, level u32
```
Sent on map load and periodically (often inside embedded bundles). Teleport once to trigger it.

### `45 36` — Other player spawn (pre-June `44 36`) ✅ class verified
`id, mask1 u32, mask2, [len, name]`, then **class u32** (roster code; `19 00 00 00` = 25 = Sorcerer). All 52 spawns in a
town capture decoded a class, and every class matched the players' own skills.
Combat power (unverified): after the last `F4 CB 1F`, from +11, the first u32 in 1..10M followed by a u32 0 (prior art);
values 10K–85K.

### Own combat power ✅
In the `33 36` self record: `cp u32, 0 u32, best? u32, 0 u32, 05 01 91 00` (searched by the trailer, offset varies).
37,381 matches the armory's 37.38K; the following u32 (37,860) is probably the best value.

### `41 36` — Mob / summon spawn (pre-June `40 36`) ✅ code + HP verified
```
id varint, kind u8, 20 00, npcCode u24 LE, 00 00 02 | 00 40 02, x f32, y f32, z f32, ..., 01 curHP varint maxHP varint, ...
```
Live: `a0ea03 04 2000 110e20 000002 …` → entity 62752, kind 4, NPC 2100753 (Highland Odyle Sparkie), HP 119700.
Kinds seen live: 4, 5 (mobs), 0x0C (NPC). Prior art says the u32 after the id is a mask, but the code overlaps it.
- Kind = mask low byte: `0x0C/0x0D` NPC, `0x5F` summon/pet, `0x1C` effect entity, `0x1F/0x1D/0x5D` spirits/ground spells.
- NPC code: scan ≤60 bytes for `00 40 02` or `00 00 02`; 3 bytes before = u24 LE code.
  Then `01 <curHP varint> <maxHP varint>` within 64 bytes.
- Summon owner: parent_key → owner record; fallback anchor `80 75 D5 2A BB 03 00 00` + caster varint.

### `42 36` — Death / despawn (pre-June `41 36`)
`entity varint, varint (0), flag varint` — 1 loaded dead, 3 combat death, 7 left world.

### `04 8D` — Ownership / loot
`summon_or_mob varint, 4 bytes, owner varint, server u16, len u8 name, len u8 server name`

### HP
- `00 8D` ✅ resource update: `entity varint, kind u8, ? u8, resource u8, value u32 [tail]`. Resource 0 = HP (mobs and us),
  1 = MP (max 2,599 matched the HUD), 3/4/6 = other gauges. Max HP is not sent: learned from the highest HP seen
  (9,016 matched the HUD).
- `21 8D` ✅ combat state: `entity varint, 00, 01|00` (entered/left combat). For us it brackets the in-game Combat
  Analysis clock (on ~0.2 s before the first hit, off 3–5 s after the last).
- `01 91` ✅ field-boss table of the current map (repeats about once a second on field maps):
  `00 00, map u32, count u8`, then per boss `state u8, spawn code varint (map × 100 + n, e.g. 101005),
  [state 1: x f32, y f32, z f32], [0–1 extra byte], timestamp u64 ms`.
  State 1 = alive (the timestamp is when it spawned); state 0 = dead (the timestamp is the announced respawn).
  Verified: map 1010 had 24 entries (20 up, 4 dead with respawns 2–6 h ahead). Spawn code → NPC code comes from
  NOIA2's published table (86 bosses on maps 1010/1110/1011/1111/20/22), built in as assets/gamedata/field_bosses.json.
  Maps: 1010 Verteron, 1110 Altgard, 1011 Eltnen, 1111 Morheim, 20/22 Chaotic Lower/Middle Reshanta (assets/gamedata/maps).
- `1B 92`: `actor varint, hp varint, hpMax varint`.
- Scan: `8D <id varint> 02 01 00 <u32 LE curHP> 00 00 00 00` (NPC; `01 01 01` for local player).

### Movement and positions ✅
World units are about 1 cm (a melee fix lands 1.2 m from the dummy it hits; running is ~490 units/s), z is up.
Positions were checked by comparing each record type against the same entity's `1C 37` track (median distance).
```
1C 37 move:      entity varint, mode u8 (4; 5 = one more byte follows), x f32, y f32, z f32,
                 heading u16 (×360/65536), heading u16, 01            ~1/s per moving entity
1D 37 steer:     entity varint, flags u8, i8 deltas …, heading u16 ×2  10 Hz velocity/steering, no position
1A 37:           entity varint, 2 bytes, x, y, z                      (73 units from the 1C track)
1B 37:           entity varint, [05] 03, x, y, z                       (106)
2F 37 / 3C 37:   entity varint, u8, x, y, z     dash / leap / relocation; 3C is also sent for us (237)
41 36 NPC spawn: position 9 bytes after the id (88); 45 36 player spawn: after the class code
```
**The server does not echo our own movement** (the client sends it, encrypted). Our position comes from
`2A 38` buff applications (they carry where the entity is), `3C 37`, and spawn/identity records: about one fix
every 1–3 s in combat. `2B 38` refreshes may repeat an older spot and are not used as fixes.

### Skill casts ✅
```
02 38: actor varint, 00, skill u32 (raw), sequence u8, kind u8, target varint, facing f32,
       [kind 2: target x f32, y f32, z f32], …
```
Every entity in range, including buffs and heals that deal no damage (269/278 records name a known skill).
`06 38` (actor, skill u32, …) follows each cast; its meaning (cooldown/resolution) is open.

### Ownership and legions ✅
```
04 8D long:  spawned varint, skill u32, owner varint, server u16, owner name (u8 len), legion (u8 len), …
             links effects/spirits/summons to their owner; a 23-byte short form carries no owner
33 8A:       entity varint, ~5–6 bytes, server u16, legion (u8 len), …   legion tag of nearby players
03 8A:       00 00, legion (u8 len), leader (u8 len), counters, texts (u8/u16 len: introduction, notice, …)
05 8A:       member entries: server u16, u32, name (u8 len), level u8, …, last login u64 ms
```

### Dungeons ✅
- Instances run on **their own server**: entering a dungeon moves the game stream to a new TCP connection
  (e.g. 193.202.112.231 → 87.232.75.237, both port 13328) and back on leaving. The detector follows both switches.
- Matchmaking arrives just before: `00 92` / `0D 92` member records (GUID string, server, name, legion), `26`/`27 92`, `1F 93`.
- **Bodyless packets** (opcode only, e.g. `19 93`, `3F 8D`) occur around the switch; scanning them for embedded
  bundles used to throw and stopped the meter.
- A Ranger's **Explosion Trap** spawns as a summon (`41 36` kind 0x5F) without owner name, ~0.4 s after its owner
  casts the skill of the same name (6/6): credited to that caster.
- Bosses spawn helper entities (kinds 0x14/0x1C, no owner) that deal damage; they are not player rows.

### Party and group finder (not decoded yet)
- `01 97`: group finder board (listings with names and recruitment texts, ~6 KB pages).
- `02 97`: our party; `0B`/`0D 97`: a player joined.

### Other
- `00 36`: server clock, u64 ms, 20 Hz.
- `0C 91`: per-map table next to `01 91` (empty in all captures so far).

### Buffs (abnormals)
```
2A 38 added:   entity varint, 01, flags u8, instance varint, abnormal u32, length i64 ms (-1 never),
               end i64 server-epoch ms (4102412400000 = never), caster varint, level u8,
               [flags&2: skill u32], u8 (2 = reapplied), x f32, y f32, z f32
2B 38 changed: entity varint, flags u8, instance ... (same from abnormal on)
2C 38 removed: entity varint, n u8, n × (kind u8, instance varint, reason u8,
               [kind&7==7: by varint, skill u32, effect u32])   reason 1 = expired
```

### Stats, map, party
- `4A 36` own stats / `49 36` all stats: list of `(stat u16, value i32)`.
- `21 36` map load: `u32 count, u32 mapId`. Dungeon ids 600000–699999.
- `23 36 00…` zone change (entity 0 = self teleport) → encounter reset.
- `02 97` party roster (members with name, class, level, gear score, combat power).
- `21 8D` boss battle toggle (TK era): `id varint, varint, toggle varint` (1 start / 0 end).
- Ping: `03 36 00 00 <i64 LE>` client timestamp echo.

## 4. IDs & classes

- Entity ids are session-scoped; **reissued on zone change**.
- Ids > 1,000,000 in spawn records map to `(id & 0x3FFF) | 0x4000`.
- Player skill id: 8 digits `CC SS NNNN`. Base skill = `raw - raw % 10000`; low digits encode specialisation choices.
- Class prefix CC: 11 Gladiator, 12 Templar, 13 Assassin, 14 Ranger, 15 Sorcerer, 16 Elementalist,
  17 Cleric, 18 Chanter, 19 Fighter.
- Roster/job class codes (4 per class): 5–8 Gladiator, 9–12 Templar, 13–16 Ranger, 17–20 Assassin,
  21–24 Elementalist, 25–28 Sorcerer, 29–32 Cleric, 33–36 Chanter, 45–48 Fighter.

## 5. Encounter rules (prior art)

- DPS = damage / max(lastHit − firstHit, 1 s).
- New fight on a target after 30 s idle; reset on zone change (with lull/debounce).
- First hit on a known boss clears trash segment.
- Merge summon damage into owner.
- Sanity check: on boss kill, total damage ≈ 97–107% of boss max HP.
- Validate against the in-game Combat Analysis window (official personal meter since 2026-04-01).

## 6. Data (skill / NPC names)

Needs skill-id → name, NPC code → name/isBoss, DoT allowlist. Prior art extracts these from the game client.
Plan: write our own extractor rather than copying GPL'd JSON.

## 7. Policy notes

- No known bans for passive meters; NCSoft hasn't ruled on them explicitly.
- NCSoft (May 2026) threatened action against **publishing other players' damage without consent**.
  → Keep data local by default; any upload must be opt-in and self-only unless others consent.
