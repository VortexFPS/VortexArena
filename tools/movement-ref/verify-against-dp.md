# Verifying the movement golden-trace corpus against the live Darkplaces engine

`movement_ref.c` is an *independent analytic reference* transcribed from the preprocessed Xonotic
QuakeC (`.tmp/server.txt`). Its `stock()` cvar table (movement_ref.c:95-110) and engine-gameplayfix
defines (movement_ref.c:112-117) were hand-copied from `physicsX.cfg` / `xonotic-server.cfg`. A
transcription is only trustworthy if it actually matches what the shipping engine executes — so this
doc records a **live cross-check**: boot the real `darkplaces-sdl.exe`, let it `exec` the full Xonotic
config chain, dump every movement cvar, and diff against `stock()`.

This is a one-time/occasional validation, not part of CI (the committed JSON fixtures are the CI guard).
Re-run it whenever `movement_ref.c`'s `stock()` table or the upstream `physicsX.cfg` changes.

> Scope note (Wave A2, T30): the high-value, fully-achievable deliverable was
> `TraceResult.DpHitTextureName` (see `XonoticGodot.Engine/Collision/*` + `tests/.../BspCollisionTests.cs`).
> For the golden-trace loop, the faithful increment is (1) this live-engine **config/cvar** cross-check
> (done below — it found two benign discrepancies and proved them harmless) plus (2) a precise written
> plan for a DP-**captured** smoke scenario. A full DP-capture pipeline is **not** committed: see
> "Why no DP-captured fixture yet" — the blocker is deterministic per-tick input injection, not the
> engine or the toolchain.

---

## 1. The exact launch (native Windows engine, headless, config dump)

The reference engine is `Base/darkplaces/darkplaces-sdl.exe` (PE32+ x86-64, "Xonotic Windows64
dedicated"). Run it dedicated (no window), with `-xonotic` (so the gamedir resolves to `data` and the
Xonotic progs/configs mount) and `-basedir` pointing at `Base/` (so the loose `.cfg` files inside
`data/xonotic-data.pk3dir/` are found — **without `-basedir` the engine prints `couldn't exec
quake.rc` and you get bare DP defaults, not Xonotic's**). `-condebug` mirrors the console to
`<userdir>/data/qconsole.log`.

Put this `dumpcvars.cfg` in the gamedir (`Base/data/xonotic-data.pk3dir/`), then run + remove it:

```cfg
echo ===T30CVARDUMP-BEGIN===
sv_maxspeed
sv_accelerate
sv_friction
sv_friction_slick
sv_stopspeed
sv_slickaccelerate
sv_friction_on_land
sv_maxairspeed
sv_airaccelerate
sv_airaccel_qw
sv_airstrafeaccel_qw
sv_airaccel_qw_stretchfactor
sv_airspeedlimit_nonqw
sv_airaccel_sideways_friction
sv_maxairstrafespeed
sv_airstrafeaccelerate
sv_airstopaccelerate
sv_airstopaccelerate_full
sv_aircontrol
sv_aircontrol_flags
sv_aircontrol_power
sv_aircontrol_penalty
sv_jumpvelocity
sv_jumpvelocity_crouch
sv_jumpspeedcap_min
sv_jumpspeedcap_max
sv_jumpspeedcap_max_disable_on_ramps
sv_track_canjump
sv_doublejump
sv_jumpstep
sv_gravity
sv_stepheight
sv_gameplayfix_stepdown
sv_gameplayfix_stepdown_maxspeed
sv_wallfriction
sv_gameplayfix_gravityunaffectedbyticrate
sv_gameplayfix_nogravityonground
sv_gameplayfix_stepmultipletimes
sv_gameplayfix_downtracesupportsongroundflag
sv_gameplayfix_q2airaccelerate
echo ===T30CVARDUMP-END===
quit
```

> A bare cvar name on its own line prints `name is "value" ["default"] <description>` — DP's `echo`
> does NOT expand `$cvar`, so use the bare-name form, not `echo sv_x $sv_x`.

Launch (Git Bash on Windows):

```bash
USERDIR=/tmp/t30-xon-userdir
mkdir -p "$USERDIR"
cp dumpcvars.cfg /c/Users/Bryan/Projects/Xonotic/Base/data/xonotic-data.pk3dir/
cd /c/Users/Bryan/Projects/Xonotic/Base/darkplaces
timeout 50 ./darkplaces-sdl.exe -dedicated -xonotic \
  -basedir /c/Users/Bryan/Projects/Xonotic/Base \
  -userdir "$USERDIR" -condebug +exec dumpcvars.cfg >/dev/null 2>&1
sed -n '/T30CVARDUMP-BEGIN/,/T30CVARDUMP-END/p' "$USERDIR/data/qconsole.log" | sed 's/\^[0-9]//g'
rm -f /c/Users/Bryan/Projects/Xonotic/Base/data/xonotic-data.pk3dir/dumpcvars.cfg   # don't leave it in the ref checkout
```

The dedicated server then tries to spawn the default map and exits ("empty maplist" / a benign
shutdown segfault) — that is *after* the full `quake.rc → default.cfg → xonotic-common.cfg →
xonotic-server.cfg → physicsX.cfg → …` exec chain has already run and our cvars are dumped, so it does
not affect the capture.

---

## 2. Result: `stock()` matches the live engine (38/40 exact; 2 benign)

Captured 2026-06-07 against `darkplaces-sdl.exe` "Xonotic Windows64 dedicated d93f9c42 Jun 5 2026".
All 40 movement cvars below were dumped from the live, fully-config'd engine. **38 match `stock()`
exactly.** The two that differ are proven harmless for the corpus:

| cvar | live engine | movement_ref.c `stock()` | verdict |
|---|---|---|---|
| sv_maxspeed | 360 | maxspeed=360 | ✅ |
| sv_accelerate | 15 | accelerate=15 | ✅ |
| sv_friction | 6 | friction=6 | ✅ |
| sv_friction_slick | 0.5 | friction_slick=0.5 | ✅ |
| sv_stopspeed | 100 | stopspeed=100 | ✅ |
| sv_slickaccelerate | 15 | slickaccelerate=15 | ✅ |
| sv_friction_on_land | 0 | friction_on_land=0 | ✅ |
| sv_maxairspeed | 360 | maxairspeed=360 | ✅ |
| sv_airaccelerate | 2 | airaccelerate=2 | ✅ |
| sv_airaccel_qw | -0.8 | airaccel_qw=-0.8 | ✅ |
| sv_airstrafeaccel_qw | -0.95 | airstrafeaccel_qw=-0.95 | ✅ |
| sv_airaccel_qw_stretchfactor | 2 | =2 | ✅ |
| sv_airspeedlimit_nonqw | 900 | =900 | ✅ |
| sv_airaccel_sideways_friction | 0 | =0 | ✅ |
| sv_maxairstrafespeed | 100 | =100 | ✅ |
| sv_airstrafeaccelerate | 18 | =18 | ✅ |
| sv_airstopaccelerate | 3 | =3 | ✅ |
| sv_airstopaccelerate_full | 0 | =0 | ✅ |
| sv_aircontrol | 100 | =100 | ✅ |
| sv_aircontrol_flags | 0 | =0 | ✅ |
| sv_aircontrol_power | 2 | =2 | ✅ |
| sv_aircontrol_penalty | 0 | =0 | ✅ |
| sv_jumpvelocity | 260 | =260 | ✅ |
| sv_jumpvelocity_crouch | 0 | =0 | ✅ |
| sv_jumpspeedcap_min | nan | NAN | ✅ |
| sv_jumpspeedcap_max | nan | NAN | ✅ |
| sv_jumpspeedcap_max_disable_on_ramps | 1 | =1 | ✅ |
| sv_track_canjump | 0 | =0 | ✅ |
| **sv_doublejump** | **0** | **=0** | ✅ (T51 changes the doublejump branch — default-neutral, so the corpus stays byte-identical) |
| sv_jumpstep | 1 | =1 | ✅ |
| sv_gravity | 800 | =800 | ✅ |
| sv_stepheight | 31 | =31 | ✅ |
| sv_gameplayfix_stepdown | 2 | stepdown=2 | ✅ |
| sv_gameplayfix_stepdown_maxspeed | 400 | stepdown_maxspeed=400 | ✅ |
| **sv_wallfriction** | **1** | **wallfriction=1** | ✅ corrected (was 0) — wall friction is a no-op in stock QC, see §2.1 |
| sv_gameplayfix_gravityunaffectedbyticrate | 1 | GF_…=1 | ✅ |
| sv_gameplayfix_nogravityonground | 1 | GF_…=1 | ✅ |
| sv_gameplayfix_stepmultipletimes | 1 | GF_…=1 | ✅ |
| sv_gameplayfix_downtracesupportsongroundflag | 1 | GF_DOWNTRACEONGROUND=1 | ✅ |
| **sv_gameplayfix_q2airaccelerate** | **1** | **GF_Q2AIRACCELERATE=0** | ❌ verdict below was WRONG — fixed 2026-07-05, see the §2.2 CORRECTION |

### 2.1 `sv_wallfriction`: live 1 — wall friction is a no-op in stock QC (RESOLVED 2026-06-07)

`sv_wallfriction` is **not** set by any Xonotic config (`grep -rn sv_wallfriction *.cfg` → none) and the
QC autocvar has **no initializer** (`common/stats.qh:422 float autocvar_sv_wallfriction;`, unlike
`autocvar_sv_gameplayfix_q2airaccelerate = 1`), so it inherits Darkplaces' engine cvar default of **1**.
The live `1` is therefore correct and authoritative.

The reason the corpus is unaffected is **stronger than "no scenario presses a wall"**: stock Xonotic
applies **no wall friction at all**, because the QC function body is *commented out*. The gate
(`walk.qc:146 if ((clip & 2) && PHYS_WALLFRICTION(this)) _Movetype_WallFriction(...)`) *does* pass on a
wall-press (the stat is 1), but `_Movetype_WallFriction` (`movetypes.qc:102`) has its entire body inside
`/* … */` — a deliberate no-op. (Players use QC physics: `use_engine_physics` is a debug-only global,
default false.) So wall friction never affects the trajectory in stock play regardless of the cvar.

**The fix (this commit):** match stock faithfully instead of documenting it wrong.
- `MovementParameters.Defaults.WallFriction` → **1** (the true cvar), with a corrected comment
  (MovementParameters.cs:71, :197). The old `0` + "OFF in stock Xonotic" was doubly wrong: the cvar is 1,
  and "off" is achieved by the commented-out body, not by the cvar.
- `PlayerPhysics.WallFriction` (the QC-physics port) was a *live* implementation gated on
  `mp.WallFriction != 0`. With the corrected default of 1 that gate is now live, so leaving the body active
  would make the port apply wall friction that stock does **not** (a fidelity regression that the corpus,
  with no wall-press scenario, would not catch — and `MoveVarsBlock` replicates `sv_wallfriction` to the
  client, so it would hit real netplay prediction too). It is now a **no-op mirroring the commented-out QC
  body** (the math is preserved in a comment for a server that un-comments the QC). The sibling
  `MoveTypePhysics.WallFriction` (engine-physics port, monster `MOVETYPE_WALK`) was already a documented
  no-op; only its "DP default 0" comment was corrected to "default 1".
- `movement_ref.c` `stock()` → `wallfriction=1` (the field is **unread**; the analytic walk path never had
  a wall-friction term and still doesn't — the line-510 comment now states the real reason: the QC body is
  commented out, not "cvar 0").

Net result: the corpus is byte-identical **by construction** (a no-op cannot change a trajectory), not by
the accident that no scenario presses a wall — verified `dotnet test … --filter MovementParity` → 10/10
pass. Both the test path and real netplay now match stock Xonotic exactly.

### 2.2 `sv_gameplayfix_q2airaccelerate`: live 1, ref 0 — benign (proven)

This was the surprising one and is worth the detail, because it *looks* like it should change air
acceleration but does not in stock Xonotic.

- The QC autocvar default is **1** (`common/stats.qh:396 float autocvar_sv_gameplayfix_q2airaccelerate
  = 1;`) and `xonotic-server.cfg:562` also sets `1`, so the live value is genuinely 1.
- `PM_Accelerate` (player.qc:288) does `if (GAMEPLAYFIX_Q2AIRACCELERATE) wishspeed0 = wishspeed;` —
  i.e. when the fix is ON, it forces `wishspeed0` to equal the (clamped) `wishspeed`, skipping the Q1
  "bug" where `step = accel*dt*wishspeed0` used a *larger* unclamped wishspeed0.
- **But all three implementations capture `wishspeed0` AFTER the `vel_max` clamp, so `wishspeed0`
  already equals `wishspeed` before `PM_Accelerate` is ever called** — making the fix a no-op in stock
  Xonotic (where `com_phys_vel_max == sv_maxairspeed == 360`):
  - QC driver `ecs/systems/physics.qc`: `:307 wishspeed = min(wishspeed, com_phys_vel_max);` then
    `:313 wishspeed0 = wishspeed;` then `:315 wishspeed = min(wishspeed, maxairspd);` →
    `:365 PM_Accelerate(..., wishspeed, wishspeed0, ...)` with `wishspeed0 == wishspeed`.
  - movement_ref.c: `:590 wishspeed = fminf(wishspeed, vel_max);` then `:594 wishspeed0 = wishspeed;`
    then `:596 wishspeed = fminf(wishspeed, maxairspd);` → same.
  - Port PlayerPhysics.cs: `:436 wishspeed = Min(wishspeed, maxairspd);` then `:439 wishspeed0 =
    wishspeed;` → same; the air call (`:484`) omits the q2 flag (defaults false) which is *correct*
    precisely because `wishspeed0` is already pre-clamped.

  So movement_ref.c's `GF_Q2AIRACCELERATE 0` is the wrong *value* but yields the identical *result* as
  the live `1`. The fix would only diverge if `com_phys_vel_max > sv_maxairspeed` (a non-stock per-frame
  speed-cap), which no corpus scenario uses. Recommend bumping the define to `1` in a future corpus
  regen for documentation accuracy (it will not change any golden JSON).

  > **CORRECTION (2026-07-05): the "benign" verdict above is WRONG.** It proved `wishspeed0 == wishspeed`
  > at the *call site* — but between that capture and the flag's application inside `PM_Accelerate`
  > (player.qc:288-289), the driver reduces `wishspeed` further: the STRAFE `GeomLerp` clamp
  > (ecs/systems/physics.qc:345-348, pure strafe → `sv_maxairstrafespeed` = **100**) and the ducked
  > halving (:316-317). With the fix ON (live Base), `wishspeed0` becomes that clamped value, so the air
  > accel step is `accel·dt·100` for pure strafe; with the define at 0 the step ran from the unclamped
  > 360 — **~3× live Base's mid-air strafe-redirect budget** (measured 637.6 vs 242.7 deg/s sustained at
  > 400 u/s — StrafeParityProbeTests). Straight-line air movement has `wishspeed == wishspeed0`, so every
  > forward-only scenario — all this section examined — was genuinely unaffected, which is how the wrong
  > verdict survived. Fixed: `GF_Q2AIRACCELERATE 1` + the port's `MovementParameters.GameplayFixQ2AirAccelerate`
  > (default ON, wire-replicated); `strafe_jump_air.json` was the one fixture that changed on regen,
  > exactly as this analysis predicts.

**Bottom line:** the analytic corpus is faithful to the live engine for every scenario it covers; the
two mismatches are documented and proven harmless. The corpus remains the primary ULP-tight movement
guard.

---

## 3. Why no DP-*captured* fixture yet (the honest blocker)

A working engine + QC toolchain both exist:
- `darkplaces-sdl.exe` boots the real gamedir (above).
- `Base/gmqcc/gmqcc` is a Linux ELF (GMQCC 0.3.6) that runs under WSL:
  `MSYS_NO_PATHCONV=1 wsl --exec /mnt/c/.../Base/gmqcc/gmqcc --version` → `GMQCC 0.3.6`.

What is **missing** is a way to feed the engine a *deterministic, scripted, per-tick usercmd stream*
(the exact `ang/move/jump/crouch/dt` the corpus encodes) and read back post-physics
`origin/velocity/onground/waterlevel`. DP movement is driven by the client `usercmd`
(`PHYS_CS(this).movement`, `v_angle`, buttons); there is **no stock console command** that injects a
scripted usercmd into the server physics tick. So a DP capture needs *new tooling*, and the analytic
corpus already gives ULP-tight parity without it. (Also: DP's collision uses `collision_impactnudge`
+ double precision in spots, so a DP-captured trace would differ from the analytic golden by small
amounts even when the port is correct — a captured fixture must use a *looser, network-aware*
tolerance than the analytic `PosTol 0.20 / VelTol 0.40`, and only validates trajectory shape.)

Direct compilation of the preprocessed `.tmp/server.txt` is also not a shortcut: it carries `cpp`
line-markers (`# 12 "file.qc"`) that gmqcc's parser rejects (`error: expected 'pragma' keyword after
'#'`). A real rebuild must go through the Makefile path (`make sv` → `qcc.sh` → `cc -xc -E` then
gmqcc), which needs a working WSL `cc` and the full source tree.

---

## 4. Precise plan to add ONE DP-captured smoke fixture (B.3a — for a follow-up)

A minimal, faithful capture of `free_fall` (and/or `ground_accel_forward`) via a tiny QC dumper:

**Step 1 — add a server-side dumper to the QC.** In a new file `qcsrc/server/_movedump.qc`
(included from `server/progs.inc`), add an entity that, each `SV_PlayerPhysics`/`PlayerPreThink`
frame, (a) overwrites the test entity's `.movement`, `.v_angle`, and jump/crouch buttons from a baked
table indexed by frame number, (b) lets the normal physics run, then (c) appends one JSON tick line to
a file via `fputs`:

```c
// per frame, AFTER physics, for the test bot `self`:
fputs(fh, sprintf("{\"in\":{\"ang\":[%g,%g,%g],\"move\":[%g,%g,%g],\"jump\":%d,\"crouch\":%d,\"dt\":%g},"
                  "\"out\":{\"origin\":[%g,%g,%g],\"velocity\":[%g,%g,%g],\"onground\":%d,\"waterlevel\":%d}}\n",
    ang.x,ang.y,ang.z, mv.x,mv.y,mv.z, jmp, crh, dt,
    self.origin.x,self.origin.y,self.origin.z,
    self.velocity.x,self.velocity.y,self.velocity.z,
    !!(self.flags & FL_ONGROUND), self.waterlevel));
```

(Match the schema `MovementParityTests.LoadGolden` already parses, MovementParityTests.cs:126-162:
`world[].{contents,planes}`, `hull.{mins,maxs}`, `start.{origin,velocity,vangle,flags}`,
`ticks[].{in,out}`. For a stock map the `world` array can be omitted/approximated since the analytic
collision is the parity guard; the DP fixture is a *cross-check* on the physics maths only.)

**Step 2 — fixed dt + determinism.** Launch dedicated with `+set sys_ticrate 0.03125`
(= 1/32 s, the corpus dt) and `+set sv_fixedframeratesingleplayer 1` so each physics frame is exactly
one tick; `host_framerate 0.03125` forces the integration dt. Spawn the bot with
`+set bot_number 1` (or a `sv_cmd addbot`) on a flat stock map (e.g. a known DM map for `free_fall`:
drop the bot from a height with zero input). Drive a fixed number of frames, then `quit`.

**Step 3 — build + run.**
```bash
# rebuild progs.dat with the dumper (WSL):
MSYS_NO_PATHCONV=1 wsl --exec bash -lc \
  'cd /mnt/c/Users/Bryan/Projects/Xonotic/Base/data/xonotic-data.pk3dir/qcsrc \
   && QCC=../../../../gmqcc/gmqcc make sv'
# then run dedicated with the launch flags above, capturing the dumper's JSON file.
```

**Step 4 — commit + assert.** Save as `tests/XonoticGodot.Tests/golden/dp_free_fall.json` and add a
`MovementParityTests` theory entry that replays it through `PlayerPhysics` with a **looser**
DP-network/impactnudge-aware tolerance (e.g. PosTol ~1.0 qu, VelTol ~2.0 qu/s — document why: DP's
double-precision collision + `collision_impactnudge` 0.03125 vs the analytic `TRACE_DIST_EPSILON`
1/32). Keep the analytic corpus as the tight guard; the DP fixture is an additive sanity cross-check,
not a replacement.

Risks: the gmqcc rebuild of the *full* server QC is the biggest lift (exact `cc -E` flags +
watermark); bot spawn/teleport determinism; and demo/network quantization if you go the demo route
(`B.3b`) instead — demo-derived origins are quantized and only good for trajectory shape, not
ULP parity.
