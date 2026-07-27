# Announcer Recording Script

A read script for recording a full replacement announcer voice pack for VortexArena
(the Godot port of Xonotic). It covers **every announcer cue that ships in Base
Xonotic's `default` pack**, plus a second half of **new / variation lines** for a
richer, on-theme announcer.

## How the announcer works (so recordings drop in cleanly)

- Voice pack lives at `sound/announcer/<pack>/<cue>.ogg` (a few Base originals are
  `.wav`). Base ships exactly one pack, `default`; VortexArena reads the same set via
  the data junction. To make a new pack, record the filenames in the **File** column
  and drop them into `sound/announcer/<yourpack>/`.
- Cues are **UI voice** — non-positional, played on `CH_INFO` at `VOL_BASEVOICE`,
  atten none. Record everything at a consistent level/proximity; the game does not
  spatialize them.
- **Antispam:** the same file will not replay within `cl_announcer_antispam` (2s
  default), so back-to-back identical cues are deduped by the engine — you don't need
  to leave headroom for that.
- **Timing classes** (Base `ANNCE_*TIME`): countdown numbers, BEGIN, TIMEOUT and the
  vote cues are `INSTANT` (fire immediately, must be tight/short). Achievements,
  killstreaks, remaining-time and PREPARE are `DEFTIME` (~2s budget, a little more room
  to breathe).
- **Recording specs (recommended):** mono, 44.1 or 48 kHz, normalized to ~ -3 dBFS
  peak, trim leading/trailing silence tight (especially the INSTANT cues), export OGG
  Vorbis ~q6. Keep a consistent mic distance, tone and "arena PA / combat AI" character
  across the whole pack.

Delivery reference for the whole pack: **cold, clipped, authoritative arena-AI /
tournament-PA** — think a fighting-game or Quake/UT announcer, not a warm narrator.

---

# PART 1 — Existing lines (Base `default` pack, 1:1 to record)

These are the canonical cues. **File** = the filename to produce. **Read** = suggested
words (Base's default pack is largely wordless-to-literal; where the traditional Xonotic
wording is well established it's given, otherwise treat Read as the recommended line).
**Default** = whether the cue is on out of the box (`ALWAYS` = always on,
`GENTLE-off` = on unless "gentle mode" hides violent/taunt cues, `NEVER` = ships off).

## 1A. Countdown numbers  (INSTANT — keep each a single crisp beat)

Reused for the match/round start countdown, and by instagib/clientkill timers.

| File | Read | Notes |
|---|---|---|
| `1.ogg` | "One" | |
| `2.ogg` | "Two" | |
| `3.ogg` | "Three" | |
| `4.ogg` | "Four" | |
| `5.ogg` | "Five" | |
| `6.ogg` | "Six" | Defined but game-start/round-start counts >5 default off; still record for other timers. |
| `7.ogg` | "Seven" | |
| `8.ogg` | "Eight" | |
| `9.ogg` | "Nine" | |
| `10.ogg` | "Ten" | |

> Match-start counts down from 5→1 by default; round-start from 3→1. All ten numbers
> are still worth recording — instagib ammo timers and clientkill use the full set.

## 1B. Match / round flow  (mixed timing)

| File | Read | Timing | Default | Trigger |
|---|---|---|---|---|
| `prepareforbattle.ogg` | "Prepare for battle!" | DEFTIME | ALWAYS | Fires once when the pre-match countdown is armed (≥5s of countdown remain). |
| `begin.ogg` | "Begin!" | INSTANT | ALWAYS | The instant the countdown hits zero and play starts. |
| `1minuteremains.ogg` | "One minute remains." | DEFTIME | ALWAYS | Map time-limit crossing 1:00 left (gated by `cl_announcer_maptime`). |
| `5minutesremain.ogg` | "Five minutes remain." | DEFTIME | ALWAYS | Map time-limit crossing 5:00 left. |
| `timeoutcalled.ogg` | "Timeout!" | INSTANT | ALWAYS | A timeout is called. |

## 1C. Vote cues  (Base ships these as `.wav`)

| File | Read | Default | Trigger |
|---|---|---|---|
| `votecall.wav` | "Vote now!" | ALWAYS | A vote/callvote is started. |
| `voteaccept.wav` | "Vote passed." | ALWAYS | The vote succeeds. |
| `votefail.wav` | "Vote failed." | ALWAYS | The vote is rejected / times out. |

## 1D. Killstreaks  (DEFTIME, GENTLE-off — escalate intensity with the number)

Plays when a player reaches the streak without dying. Base files are literally the
counts; you can read them as counts or as escalating spree titles (see Part 2 for a
titled variation set).

| File | Read (literal) | Trigger |
|---|---|---|
| `03kills.ogg` | "Three kills!" | 3-kill streak. |
| `05kills.ogg` | "Five kills!" | 5-kill streak. |
| `10kills.ogg` | "Ten kills!" | 10-kill streak. |
| `15kills.ogg` | "Fifteen kills!" | 15-kill streak. |
| `20kills.ogg` | "Twenty kills!" | 20-kill streak. |
| `25kills.ogg` | "Twenty-five kills!" | 25-kill streak. |
| `30kills.ogg` | "Thirty kills!" | 30-kill streak. |

## 1E. Skill / achievement taunts  (DEFTIME)

| File | Read | Default | Trigger (approx.) |
|---|---|---|---|
| `headshot.ogg` | "Headshot!" | ALWAYS | Rifle/Vortex headshot kill. |
| `airshot.ogg` | "Airshot!" | GENTLE-off | Hit/kill an airborne enemy with a projectile. |
| `impressive.ogg` | "Impressive!" | GENTLE-off | Hard precision shot (e.g. hitting multiple enemies / a difficult Vortex hit). |
| `amazing.ogg` | "Amazing!" | GENTLE-off | Exceptional precision feat. |
| `awesome.ogg` | "Awesome!" | GENTLE-off | Multi-kill with a single shot / splash. |
| `botlike.ogg` | "Botlike!" | GENTLE-off | Inhuman, machine-precise play. |
| `yoda.ogg` | "Yoda!" | GENTLE-off | Trick / high-ground / Crylink-style novelty kill. |
| `electrobitch.ogg` | "Electro bitch!" | ALWAYS | Killed by an Electro combo (mocking taunt). |

## 1F. Instagib flavor  (DEFTIME, GENTLE-off)

| File | Read | Trigger |
|---|---|---|
| `terminated.ogg` | "Terminated!" | Instagib kill. |
| `narrowly.ogg` | "Narrowly!" | Barely-survived / near-miss instagib moment. |
| `lastsecond.ogg` | "Last second!" | A clutch final-instant instagib kill. |

## 1G. Defined but NOT shipped as audio

Record these to *complete* the table beyond stock Base:

| File | Read | Default | Trigger |
|---|---|---|---|
| `multifrag.ogg` | "Multi-frag!" | NEVER (off) | Rapid multi-kill window. No audio ships in Base; the notification exists. Record it to enable a working multifrag cue. |

## 1H. Orphaned legacy files (present on disk, unreferenced)

These `.ogg` files exist in Base's `default` folder but **no current notification plays
them** (Nexuiz/older-Xonotic leftovers). Re-record only if you also wire up team-lead
announcements in the port; otherwise skip.

| File | Read | Historical use |
|---|---|---|
| `leadgained.ogg` | "Lead gained." | You took the lead. |
| `leadlost.ogg` | "Lead lost." | You lost the lead. |
| `leadtied.ogg` | "Lead tied." | Scores tied. |
| `redteamtakeslead.ogg` | "Red team takes the lead." | Team lead change. |
| `blueteamtakeslead.ogg` | "Blue team takes the lead." | Team lead change. |

---

# PART 2 — New & variation lines (on-theme, for a richer pack)

Optional content. Two uses: (a) **variation takes** of existing cues so the game can
randomize and the announcer feels less repetitive, and (b) **brand-new cues** for events
that currently have no voice. New cues need code wiring in `AnnouncerController` /
`HudNotifications` / the notification registry before they'll fire — flagged below.

> Naming suggestion for variations: keep the base cue name and add `_a`, `_b`… (e.g.
> `headshot_a.ogg`), then have the player pick a random variant. Nothing in Base does
> this today, so it's a port-side feature.

## 2A. Variation takes for existing cues

Record 2–3 alternates each for the cues players hear most, so they can rotate:

**Begin / prepare**
- "Prepare for battle!" → "Get ready!" / "Stand by…" / "Weapons hot."
- "Begin!" → "Fight!" / "Go, go, go!" / "Engage!"

**Headshot**
- "Headshot!" → "Right between the eyes!" / "Clean headshot!" / "Skull cracker!"

**Multi-kill / spree escalation** (titled alternative to the literal counts in 1D)
- 3 kills → "Killing spree!"
- 5 kills → "Rampage!"
- 10 kills → "Massacre!" / "Unstoppable!"
- 15 kills → "Dominating!"
- 20 kills → "Godlike!"
- 25 kills → "Wicked sick!"
- 30 kills → "Legendary!"

**Achievement taunts (alternates)**
- Airshot → "Out of the air!" / "Sky shot!"
- Impressive → "Nailed it!" / "Precision kill!"
- Awesome → "Devastating!" / "Total wipeout!"
- Terminated → "Vaporized!" / "Deleted!" / "Disintegrated!"

**Vote / timeout**
- Vote now → "A vote has begun." / "Cast your vote."
- Vote passed → "Vote accepted." / "Motion carried."
- Vote failed → "Vote denied." / "Rejected."
- Timeout → "Match paused." / "Time!"

## 2B. New match-flow cues (need light wiring)

| Suggested file | Read | Fires when |
|---|---|---|
| `overtime.ogg` | "Overtime!" | Match goes to overtime / sudden death. |
| `suddendeath.ogg` | "Sudden death!" | First-frag-wins tiebreak begins. |
| `matchpoint.ogg` | "Match point!" | A team/player is one score from winning. |
| `flawless.ogg` | "Flawless victory!" | Round/match won without dying. |
| `10minutesremain.ogg` | "Ten minutes remain." | Add a 10-min warning tier alongside 5/1. |
| `30secondsremain.ogg` | "Thirty seconds remaining!" | Final-30s warning. |
| `matchdraw.ogg` | "It's a draw." | Match ends tied. |
| `victory.ogg` | "Victory!" | You / your team won. |
| `defeat.ogg` | "Defeat." | You / your team lost. |
| `2minutesremain.ogg` | "Two minutes remain." | Optional mid-tier time warning. |

## 2C. Objective / team cues (for CTF, Domination, etc. — need wiring)

| Suggested file | Read |
|---|---|
| `flagtaken.ogg` | "The flag has been taken!" |
| `flagdropped.ogg` | "Flag dropped!" |
| `flagreturned.ogg` | "Flag returned." |
| `flagcaptured.ogg` | "Flag captured!" |
| `yourbasetaken.ogg` | "Your base is under attack!" |
| `teamlead_generic.ogg` | "Your team takes the lead." |
| `teamtrail_generic.ogg` | "Your team is falling behind." |
| `objectivesecured.ogg` | "Objective secured." |
| `pointcaptured.ogg` | "Control point captured." |

## 2D. Pickup / power cues (arena-shooter staples — need wiring)

| Suggested file | Read |
|---|---|
| `strengthpickup.ogg` | "Strength!" |
| `shieldpickup.ogg` | "Shield!" |
| `speedpickup.ogg` | "Speed!" |
| `megahealth.ogg` | "Mega health!" |
| `powerupexpired.ogg` | "Power-up expired." |
| `firstblood.ogg` | "First blood!" |
| `revenge.ogg` | "Revenge!" |
| `denied.ogg` | "Denied!" |
| `humiliation.ogg` | "Humiliation!" |
| `doublekill.ogg` | "Double kill!" |
| `triplekill.ogg` | "Triple kill!" |
| `comboking.ogg` | "Combo king!" |

## 2E. VortexArena-branded flavor (optional identity cues)

| Suggested file | Read |
|---|---|
| `welcometothearena.ogg` | "Welcome to the arena." |
| `entervortex.ogg` | "Enter the Vortex." |
| `arenaonline.ogg` | "Arena online." |
| `combatants_ready.ogg` | "Combatants ready." |

---

## Quick production checklist

- [ ] Record every **Part 1** file (that's the drop-in-compatible pack).
- [ ] Match the cold arena-AI delivery across all takes.
- [ ] Trim INSTANT cues (numbers, begin, timeout, votes) *tight* — no leading air.
- [ ] Normalize to a consistent peak; export mono OGG (or `.wav` for the three vote cues
      if staying byte-compatible with Base filenames).
- [ ] Drop into `sound/announcer/<packname>/`, matching filenames exactly.
- [ ] Part 2 variation/new cues are optional and (for new cues) need code wiring before
      they play.
