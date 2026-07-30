# bones_was_here's "netcode / client-side weapons" branch — search result

*Searched 2026-07-27 against a freshly fetched `xonotic-data.pk3dir` (308 refs) and `darkplaces` (156 refs),
plus the GitLab MR and issue APIs.*

## Short answer

**It does not exist — not under that description, not right now.** There is no branch or merge request by
bones_was_here (or anyone else) implementing client-side weapons or client-side weapon prediction in either
upstream repo.

What almost certainly produced the rumour is a **pair** of real things that sit next to each other:

1. **Upstream issue [#1506 "Client side weapon switching"](https://gitlab.com/xonotic/xonotic-data.pk3dir/-/issues/1506)** —
   open, unassigned, no implementation. The ask: *"the client waits for a server round trip to switch
   weapons"*, so move weapon selection client-side to help high-latency players. It is a wanted feature with
   nobody building it.
2. **bones' actual open netcode-adjacent branch, [`bones_was_here/ilikephysics4` (!1579)](https://gitlab.com/xonotic/xonotic-data.pk3dir/-/merge_requests/1579)** —
   very much real, 21 commits, and heavily about **prediction**. That is the branch people are talking about
   when they say "bones' new netcode branch". It is movement physics, not weapons.

## How I searched (so this doesn't need redoing)

The `data` clone had silently narrowed to a master-only fetch refspec, which is why the branch stream looked
empty at first — fixed, and the harvester now detects that condition (see `planning/upstream-watch/README.md` §1).
After re-fetching all 308 refs:

- **Branch names**, all refs, both repos: `superbot|weapon|csqc|net|predict|client|lag|ping|antilag` — nothing.
- **Commit messages**, `--all`, since 2025-01-01: `clientside|client.side|csqc|predict|netcode` — 30 hits, all
  accounted for (bones' `pm: …` prediction commits on ilikephysics4, otta8634's CSQC effects/modicons work,
  Mario's CSQC projectile-registry prep). No weapons-prediction work.
- **Every MR bones has ever opened** on `xonotic-data.pk3dir` (~100, all states, via `author_id=4278526`) —
  listed and read by title. Nothing matching.
- **His GitLab activity feed**, last ~60 events (covers 2026-07-10 → 2026-07-26) — his live branches are
  `ilikephysics4`, `mapinfo_fixes`, `pipeline_erbium`, `q3_mapents`, `robust_damage`, `most_available`, `ucrt`.
- **Issue search** for `clientside`, `client side weapon`, `prediction` — found #1506 (above) and #2852
  "Migrate SVQC effects to CSQC", neither with an implementation branch.
- He has **no public personal fork** (`bones_was_here/xonotic-data.pk3dir` → 404), so fork-hosted work would
  still have surfaced through the MR API, and did not.

The one caveat I can't rule out: work that exists only on his own machine or a private server, never pushed
to GitLab. If the rumour came from a Matrix/IRC conversation rather than a repo link, that is the likely
explanation, and there is nothing to read yet.

## What *is* real, ranked by relevance to us

### 1. `ilikephysics4` — !1579, open, 21 commits — **tracked as [UW-0130](upstream-watch/LEDGER.yaml)**

"New player movement physics: complete CPMA support, known bugs fixed." Rebased and grown since we last
triaged it (was UW-0051, tip `582a3690` → `91d517e6`, old tip no longer reachable). Nine commits are new:
CPMA double jumps **with prediction**, Q3/CPMA step-up, **predicted Q3 skimming**, Q3 crouching with
customisable speed, a water-physics rewrite, a rewritten unsticking pass, the Q3 acceleration penalty while
holding +jump/+crouch, legacy step-up removal, and a pipeline hash bump. Resolves 15 upstream issues. bones
states the commits interlock and must be tested together.

This is the single highest-impact open upstream item for us, and it is where the "netcode" framing is
earned — the prediction commits are the reason. See UW-0130 for the full recommendation; the short version
is **don't port it piecemeal**, because our movement layer carries three hard-won divergences
(GAMEPLAYFIX_Q2AIRACCELERATE strafe parity, the WalkMove downtrace jump-grant fix, fixed-timestep bunnyhop)
that a partial port would quietly undo.

### 2. His merged 2025 netcode series — **already inside our pin, so this is parity work, not upstream-watch**

All four of these are ancestors of our parity pin `v0.8.6-1779-g863cd3e84` (2026-06-03), i.e. they are in the
Base we already measure against. They will never appear in an upstream-watch worklist, which is exactly why
they are worth naming here:

| Merged | MR | What |
|---|---|---|
| 2025-11-25 `a188c36da` | !1569 `cl_nettimesyncboundmode` | Change the client time-sync bound mode default to a smoother one |
| 2025-12-15 `d28e42a76` | !1566 `csqcplayer_hz` | Dynamically increase the minimum update rate for CSQC player entities |
| 2025-12-26 `5471a4c07` | !1577 `cl_physics` | Client-selectable physics updates |
| 2024-08-05 `b16c4a803` | !1248 `ticrate` | Support only perfect ticrates (32/64/128 Hz), raise the default to 64 Hz |

Where we stand on these:

- **`cl_nettimesyncboundmode` — modelled.** Our client time-sync law is built against Base's mode 5 and
  documented in [NetGame.cs:362-367](game/net/NetGame.cs:362) and
  [ClientSettings.cs:162-170](game/menu/framework/ClientSettings.cs:162), with an A/B switch between Base's
  exact stepped law and our rate-based variant, from the 2026-07-11 frametime parity audit. **This is the
  one to re-read in light of the rubberband postmortem** — that investigation found Godot's
  `MainTimerSync::advance_checked` was the dominant wobble term and left `cl_smoothdt` as the top remaining
  residual, and bones changing the upstream default in the same problem space is a useful second opinion.
- **`csqcplayer_hz` — not present by name.** Dynamically raising the CSQC player-entity update rate is
  exactly the class of fix that matters for "inaccurate display of remote, fast moving players" (upstream
  #1761). Worth an explicit look at what our snapshot cadence does for fast-moving remote players.
- **`ticrate` 32/64/128 Hz — we have a `TicRate` concept** threaded through the gameplay layer, but not the
  perfect-ticrate constraint or the 64 Hz default as a modelled rule. Note our `physics_ticks_per_second` is
  10 on the Godot side, which is a *different* clock and the one implicated in the wobble postmortem — don't
  conflate them.

### 3. Client-side weapon switching (#1506) — the feature itself

Nobody upstream is building it. If we want it, we would be first, and we are arguably better placed than
upstream: we already predict weapon fire client-side (`cl_predictfire`, per-weapon ready clock — see the
client-fire-prediction-model notes), which is the harder half. Predicting the *switch* on top of an existing
predicted-fire model is a smaller step for us than it is for QuakeC.

That is a genuine differentiation opportunity rather than a port. Not a ledger row — there is nothing
upstream to track — but worth a `WISHLIST.md` entry citing issue #1506.

## Recommendation

1. Treat **UW-0130 (`ilikephysics4`)** as "bones' netcode branch" — it is the thing that exists, it is open,
   and it is the biggest upstream item on our board. Decide it as its own feature-build with a playtest gate.
2. Re-read the **`cl_nettimesyncboundmode` and `csqcplayer_hz`** changes as *parity* items against our
   frametime work, not as ports. They are inside the pin and therefore invisible to upstream-watch — a
   structural blind spot worth remembering: **upstream-watch only sees past the pin, so pre-pin upstream work
   we never ported stays invisible unless parity catches it.**
3. File client-side weapon switching to `WISHLIST.md` as our own feature, citing upstream #1506.
4. If the rumour came from a specific conversation or link, send it over — a private/unpushed branch is the
   only thing this search could not have found.
