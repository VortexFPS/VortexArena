# Superbots — what they are, and where Vortex Arena stands

*Audited 2026-07-27 against Base `origin/master` (`a76649320`, 2026-07-13) and port `main` (`a969c8a`).*

## Short answer

There is no "superbots patch" to go and find. **Superbots are a stock, shipped Xonotic feature**, added
for 0.8.6 and therefore already inside our pinned Base (`v0.8.6-1779-g863cd3e84`). They are not a
branch, an MR, or a third-party mod — they are a single `#define` in the bot code:

```c
// qcsrc/server/bot/default/bot.qh:23
#define SUPERBOT (skill > 100)
```

Set the `skill` cvar above 100 and every bot on the server switches into a mode with no aim error, no
simulated latency, a 200 Hz think rate, projectile dodging, random combat strafing, and low-health
target prioritisation. The press coverage around the 0.8.6 release described them as bots that "have
no aim limitations, actively dodge projectiles, strafe randomly while in combat and prioritize low
health targets" — that is an accurate summary of exactly these nine gated code sites.

So the real question is not "should we port superbots" but **"is our bot AI faithful at skill > 100"**.
It mostly is. Two gaps.

## Try it right now

```bash
dotnet build XonoticGodot.csproj -c Debug
```

Then launch with the skill cvar pushed past the threshold:

```bash
./XonoticGodot --host afterslime --bots 4 --cvar skill 101
```

`skill` is registered in [Cvars.cs:329](src/XonoticGodot.Server/Cvars.cs:329) as
`bot skill 0..10 (>100 = superbot)`, default 8, and [BotPopulation.cs:157](src/XonoticGodot.Server/Bot/BotPopulation.cs:157)
pushes live cvar changes onto already-spawned bots — so `skill 101` in the console mid-match works too,
no reconnect needed.

## Site-by-site parity

Base has nine `SUPERBOT`-gated sites. Seven are ported faithfully, one is moot, one is missing.

| # | Base site | What superbots do | Port | Status |
|---|---|---|---|---|
| 1 | `bot.qh:23` | `skill > 100` gate | [BotAim.cs:43](src/XonoticGodot.Server/Bot/BotAim.cs:43) `SuperbotSkill = 100f` | ✅ |
| 2 | `aim.qc:167` | Exact aim — no smoothing, no error; `bot_firetimer = time + 0.001` | [BotAim.cs:168](src/XonoticGodot.Server/Bot/BotAim.cs:168) | ✅ |
| 3 | `bot.qc:72` | Think every `0.005s` instead of the skill-scaled interval | [BotBrain.cs:409](src/XonoticGodot.Server/Bot/BotBrain.cs:409) | ✅ |
| 4 | `bot.qc:96` | `CS(this).ping = 0` — no simulated latency | — | ⚠️ **moot** (see below) |
| 5 | `bot.qc:293` | Skip the `READSKILL` clamps | — | ✅ (comments only upstream; no live code) |
| 6 | `havocbot.qc:1281` | **Random combat strafing** while `AI_STATUS_ATTACKING` | [BotBrain.cs:604](src/XonoticGodot.Server/Bot/BotBrain.cs:604) | ✅ *(ported 2026-07-27)* |
| 7 | `havocbot.qc:1370` | Re-pick target every `0.1s`, not every `bot_ai_enemydetectioninterval` (2s) | [BotBrain.cs:1264](src/XonoticGodot.Server/Bot/BotBrain.cs:1264) | ✅ |
| 8 | `havocbot.qc:1409` | Target rating `bound(50, hp+armor, 250) * dist` — prefer the weak kill | [BotBrain.cs:1268](src/XonoticGodot.Server/Bot/BotBrain.cs:1268) | ✅ |
| 9 | `havocbot.qc:1777` | `havocbot_dodge` enabled (disabled at all other skills as "too expensive") | [BotBrain.cs:905](src/XonoticGodot.Server/Bot/BotBrain.cs:905) | ✅ |

The adjacent `skill < 10` gate on keyboard-movement emulation (`havocbot.qc:1310`) is also present, at
[BotNavigation.cs:413](src/XonoticGodot.Server/Bot/BotNavigation.cs:413) — so our superbots correctly skip
key quantisation and move fully analog.

The dodge plumbing is wired end to end, not just the gate: `bot_dodge` flags are set on in-flight
projectiles across [Blaster.cs:168](src/XonoticGodot.Common/Gameplay/Weapons/Blaster.cs:168),
[Mortar.cs:214](src/XonoticGodot.Common/Gameplay/Weapons/Mortar.cs:214),
[Porto.cs:192](src/XonoticGodot.Common/Gameplay/Weapons/Porto.cs:192) and
[PhaserTurret.cs:148](src/XonoticGodot.Common/Gameplay/Turrets/PhaserTurret.cs:148), and consumed by
`BotBrain.HavocbotDodge`.

> **Resolved 2026-07-27.** Gap 1 is now ported and Gap 2 is now recorded as a deliberate divergence.
> The two sections below are kept as written (they are the analysis that led to each decision); each
> carries a resolution note at the end.

## Gap 1 — random combat strafing (real, and it is the visible one) — **PORTED**

`havocbot.qc:1281-1306`: when a superbot is `AI_STATUS_ATTACKING` and not currently dodging, every
`0.35s` it rolls a new random horizontal move vector (`crandom() * maxspeed` on X and Y), holds it for
`0.3s`, and has a 15% chance to roll "no direction" instead so the bot still drifts toward its goal.
Vertical is deliberately excluded.

We do not have this. What we have instead, at
[BotBrain.cs:862 `CombatMovement`](src/XonoticGodot.Server/Bot/BotBrain.cs:862), is a **different design
that runs at every skill level**: a strafe-sign flip on a skill-scaled clock (`0.4 + rand * (1.2 - skill*0.1)`),
plus a health-advantage term that biases the bot toward closing or retreating, blended 75/25 with the
navigation move.

That is not a bug — it is a deliberate divergence that gives *all* our bots combat movement where stock
gives it only to superbots. But it means our superbots are **more predictable in combat than stock
superbots**, because our strafe flips on a regular cadence with a fixed 0.8 magnitude while stock rolls a
fresh random 2D vector. Against a human, the stock behaviour is meaningfully harder to lead.

Fix, if we want stock parity at skill > 100: add the random-direction branch inside `CombatMovement`,
gated on `Skill > BotAim.SuperbotSkill && aistatus == Attacking && dodge == zero`, and let it override
the strafe term for its 0.3s window. Small — call it S. Leave the sub-100 behaviour alone.

### Resolution — ported 2026-07-27

Landed as step **4c-ii** in `BotBrain.ThinkProduce`, not inside `CombatMovement`: QC applies the jitter
*after* the dodge fold and overrides it, and its `!dodge` gate reads the **post-checkdanger-veto** dodge
value, so the override has to sit where that value is final.

One subtlety worth recording. QC's gate is `this.aistatus == AI_STATUS_ATTACKING` — **equality, not a bit
test** — so the jitter only runs when "enemies in sight" is the bot's *only* active status: not
bunnyhopping, not danger-ahead, not escaping a `trigger_hurt`, not out of a jumppad or water, not
jetpacking, not stuck. The port has no `.aistatus` bitfield (tracked as `bot-ai.state.aistatus`; the
statuses live as ad-hoc per-behaviour state), so the equality is approximated by *has an enemy and none of
the statuses we do model is active*. The bunnyhop exclusion is the one that matters — jittering would
scrub the speed a bunnyhopping approach just built.

Also: the QC comment above the block says "75% chance" while the code rolls 85%. The **code** is what was
ported.

Pinned by `BotLiveLoopTests.SuperbotCombatJitter_VariesTheHorizontalWishMove`, which fails without the
change (1421 in-combat frames, zero sub-run-speed wish-moves), plus
`NonSuperbot_CombatMoveStaysPinnedToRunSpeed` as the no-leak control at skill 10.

## Gap 2 — bot latency simulation is absent entirely — **INTENDED DIVERGENCE**

Base `bot.qc:96-106` gives every non-superbot a simulated ping:

```c
CS(this).ping = bound(0, 0.07 - bound(0, (skill + this.bot_pingskill) * 0.005, 0.05) + random() * 0.01, 0.65);
```

and superbots get `ping = 0`. That value is not cosmetic — it feeds antilag, so a low-skill bot is
rewound further and effectively shoots "later" than a high-skill one.

We model none of it. There is no bot ping assignment anywhere in `src/`, and
[AntiCheat.cs:191](src/XonoticGodot.Server/AntiCheat.cs:191) documents ping as "defaults to 0 = a
local/bot client". So the superbot `ping = 0` line is already our behaviour by accident — **but so is
skill 0's**, which is the actual divergence. Every bot in Vortex Arena currently plays with a superbot's
latency.

This is the broader of the two gaps and it is *not* superbot-specific: it makes low-skill bots harder
than stock across the whole skill range. Whether to close it is a design call — simulated bot latency is
one of those stock behaviours that is arguably a wart — but right now it is an accidental divergence
rather than a chosen one, which is the part worth fixing. If we do adopt it, it lands on the antilag
path, so read the ENet throttle and net-input-trace notes first and measure rather than assume.

### Resolution — recorded as an intended divergence, 2026-07-27

Decided **not** to simulate bot latency. Recorded on `bot-ai.aim.latency` in
`planning/parity/registry/bot-ai.yaml` with `intended_divergence: true` and a full rationale, and
cross-referenced from `bot-ai.think.throttle`, which owns the ping half.

The reasoning, short version: Base makes a low-skill bot worse by pretending it is on a bad connection
rather than by making it play worse, and it does so through the antilag path. That conflates two axes, and
it puts bot difficulty inside netcode machinery this port keeps reserved for real clients — our
antilag/rewind story is a reimplementation, and synthetic rewind traffic in it is not something we want.
Skill stays expressed purely through aim error, turn rate, reaction interval and decision cadence, all of
which *are* ported faithfully.

**Accepted consequence:** bots below SUPERBOT lead and react slightly sharper than stock at the same skill
number, so our `skill` values are not numerically interchangeable with upstream's. Revisit only if bot
difficulty tuning turns out to be unable to reach a stock-like low-skill feel without the latency term.

## Recommendation

1. **Nothing to port from upstream.** Superbots predate our pin; this is parity work, not upstream-watch
   work. No ledger row.
2. ~~Close Gap 1~~ — **done** (2026-07-27), see the resolution above.
3. ~~Decide Gap 2~~ — **done** (2026-07-27): recorded as an intended divergence, not implemented.
4. ~~Add a parity registry row for the superbot unit~~ — unnecessary: the existing `bot-ai` unit already
   carried both gaps (`bot-ai.combat.dodge`, `bot-ai.aim.latency`, `bot-ai.think.throttle`), so all three
   rows were updated rather than duplicated into a new unit. The nine sites are cited in the table above,
   which is the durable record.

**Not playtested.** Both tests are code-level. What a playtest would tell us that they cannot: whether a
jittering superbot actually *feels* harder to lead, and whether the 0.3s hold reads as erratic movement or
as stutter. Run `--cvar skill 101` against 2-3 bots and watch.
