using System.Numerics;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Physics;

/// <summary>
/// The shared, deterministic player-movement simulation — the C# port of Xonotic's per-tick player
/// physics. The same instance runs on the client (prediction) and the server (authority), so it depends
/// only on the engine-services facade (<see cref="Api"/>) and is Godot-free.
///
/// Sources ported (Base/data/xonotic-data.pk3dir/qcsrc):
///   * <c>ecs/systems/physics.qc</c> — <c>sys_phys_update</c> (the full PM_Main branch selection:
///     waterjump / PM_Physics-hook / noclip-fly-IsFlying / swimming / ladder / jetpack / ground / air) and
///     <c>sys_phys_simulate</c> (PM_friction + PM_walk + PM_air + PM_swim + PM_ladder, folded into one
///     function in the ECS rewrite).
///   * <c>ecs/systems/sv_physics.qc</c> — <c>sys_phys_spectator_control</c> (the spectator free-flight
///     speed ladder + SPECTATORSPEED seed) driving the <c>!IS_PLAYER</c> maxspeed_mod branch (physics.qc:58-62).
///   * <c>common/physics/player.qc</c> — <c>PlayerJump</c> (jump + frozen/typing/water guards + debounce +
///     landing friction), <c>CheckPlayerJump</c> (jetpack activation + water-jump), <c>PM_jetpack</c>
///     (fuel + thrust), <c>PM_check_slick</c> (Q3SURFACEFLAG_SLICK), <c>PM_check_frozen</c>,
///     <c>PM_ClientMovement_UpdateStatus</c> (crouch hull resize), and the accel math in <see cref="PMAccelerate"/>.
///   * <c>common/physics/movetypes/movetypes.qc</c> — <c>_Movetype_FlyMove</c> (SV_FlyMove) /
///     <c>_Movetype_ClipVelocity</c>.
///   * <c>common/physics/movetypes/walk.qc</c> — <c>_Movetype_Physics_Walk</c> (SV_WalkMove: the onground
///     re-detection, the up/forward/down re-step with wall friction, and the step-down).
///
/// All movement branches are now ported (water/swimming, ladders, jetpack, crouch, conveyors, slick,
/// noclip/fly, the jump guards). Determinism: fixed dt = <c>input.FrameTime</c>; no wall-clock;
/// single-precision throughout, matching the QC. The gravity half-step
/// (<c>GAMEPLAYFIX_GRAVITYUNAFFECTEDBYTICRATE</c>) is ON, as in Xonotic.
/// </summary>
public sealed class PlayerPhysics : IPlayerPhysics
{
    // --- engine gameplayfix constants matching the stock Xonotic config ---------------------------
    private const bool GravityUnaffectedByTicrate = true;
    private const bool StepMultipleTimes = true;
    private const bool DownTraceOnGround = true;
    // sv_gameplayfix_nogravityonground — default TRUE in Xonotic (verified in the compiled server config /
    // .tmp/server.txt). When on-ground, _Movetype_FlyMove applies NO gravity (neither half-step), so a
    // grounded player keeps velocity.z == 0 instead of accumulating a spurious downward velocity. Pinned by
    // the forward_jump_arc / bunnyhop golden traces (see MovementParityTests).
    private const bool NoGravityOnGround = true;

    private const int MaxClipPlanes = 5;        // QC MAX_CLIP_PLANES
    private const float OnGroundNormalZ = 0.7f; // floor threshold

    // --- water-level codes (QC WATERLEVEL_*, movetypes.qh) ---
    private const int WaterLevelNone = 0;
    private const int WaterLevelWetFeet = 1;
    private const int WaterLevelSwimming = 2;
    private const int WaterLevelSubmerged = 3;

    // --- engine content/surface bitmasks (Base/darkplaces/bspfile.h SUPERCONTENTS_*, Q3SURFACEFLAG_*).
    //     Mirrored here as literals: XonoticGodot.Common must not reference XonoticGodot.Engine, and the trace
    //     reports these as raw ints on TraceResult.DpHitContents / DpHitQ3SurfaceFlags. ---
    private const int SuperContentsWater = 0x00000010;
    private const int SuperContentsSlime = 0x00000020;
    private const int SuperContentsLava  = 0x00000040;
    private const int SuperContentsLiquidsMask = SuperContentsWater | SuperContentsSlime | SuperContentsLava;
    private const int Q3SurfaceFlagSlick = 0x0002;
    private const int Q3SurfaceFlagMetalSteps = 0x1000;
    private const int Q3SurfaceFlagNoSteps = 0x2000;

    // --- jetpack tunables (g_jetpack_*; defaults = stock balance-xonotic.cfg). Now read live as cvars in
    //     PMJetpack (a retuning server takes effect); these are the fallbacks when the cvar is unset. ---
    private const float JetpackAccelSide = 1200f;     // g_jetpack_acceleration_side 1200
    private const float JetpackAccelUp   = 600f;      // g_jetpack_acceleration_up 600
    private const float JetpackAntiGravity = 0.8f;    // g_jetpack_antigravity 0.8
    // balance-xonotic.cfg:248 sets g_jetpack_maxspeed_side 1200 (the 1500 was the nexuiz25 balance — a stale
    // baked value); the stock Xonotic fallback is 1200.
    private const float JetpackMaxSpeedSide = 1200f;  // g_jetpack_maxspeed_side 1200
    private const float JetpackMaxSpeedUp   = 600f;   // g_jetpack_maxspeed_up 600
    private const float JetpackFuel = 8f;             // g_jetpack_fuel 8
    private const float JetpackReverseThrust = 0f;    // g_jetpack_reverse_thrust 0
    private const float PauseFuelRegen = 2f;

    // --- movement sounds ---
    // QC nextstep = time + 0.3 + random()*0.1 (player.qc:674 / :695): a 0.3s base interval with up to 0.1s of
    // jitter so footsteps don't sound metronomic. The jitter is audio-only and gated to the server tick
    // (#ifdef SVQC), so it must NOT advance the shared deterministic Prandom stream (which the predicting
    // client also consumes for spread/effects) — a server-only draw from that stream would desync the seed.
    // Use a dedicated, server-local RNG for the cosmetic cadence jitter instead.
    private const float FootstepIntervalBase = 0.3f;
    private const float FootstepIntervalJitter = 0.1f;
    private const float FootstepSpeedThreshold = 0.6f; // fraction of maxspeed before footsteps play
    private static readonly System.Random FootstepJitterRng = new();

    // --- crouch hull (sv_player_crouch_mins/maxs) ---
    private static readonly Vector3 CrouchMins = new(-16f, -16f, -24f);
    private static readonly Vector3 CrouchMaxs = new(16f, 16f, 25f);

    // --- view offset, standing vs crouched (sv_player_viewoffset '0 0 35' / sv_player_crouch_viewoffset '0 0 20').
    //     QC PM_ClientMovement_UpdateStatus sets this.view_ofs to STAT(PL_VIEW_OFS) / STAT(PL_CROUCH_VIEW_OFS) on the
    //     stand/duck transition; the eye drops 35 -> 20 while crouched. The render camera reads the live ViewOfs.Z. ---
    /// <summary>The standing eye offset (QC <c>STAT(PL_VIEW_OFS)</c>, <c>sv_player_viewoffset '0 0 35'</c>) — the
    /// canonical value the spawn path must seed onto a (re)spawned player's <see cref="Entity.ViewOfs"/> (QC
    /// PutPlayerInServer sets <c>view_ofs = PL_VIEW_OFS</c>). Without it the server player's eye sits at the origin
    /// (ViewOfs 0) until the first crouch-leave edge — firing the shot ~35u too LOW (the "shot low until I crouch
    /// once" bug). Public so <see cref="XonoticGodot.Common.Gameplay.Player.SpawnSystem"/> seeds the same value.</summary>
    public static readonly Vector3 StandViewOfs = new(0f, 0f, 35f);
    private static readonly Vector3 CrouchViewOfs = new(0f, 0f, 20f);

    // --- spectator free-flight speed ladder (ecs/systems/sv_physics.qc:3-5) ---
    // sv_spectator_speed_multiplier has NO inline default in QC (autocvar float → 0 when the cvar is unset;
    // xonotic-server.cfg sets it to 1.5). We read it RAW (no C# fallback) like QC — substituting 1.5 here
    // would bake a cfg value into the engine. _min/_max DO have inline defaults (1 / 5) — safe fallbacks.
    private const float SpectatorSpeedMultiplierMinDefault = 1f; // autocvar_sv_spectator_speed_multiplier_min
    private const float SpectatorSpeedMultiplierMaxDefault = 5f; // autocvar_sv_spectator_speed_multiplier_max

    // --- specialcommand cheat-code (QC SpecialCommand / PM_check_specialcommand, common/physics/player.qc) ---
    // The secret movement-input sequence: each server tick the button mask decodes to one character; matching the
    // next character of this string advances the per-player pointer (Entity.SpecialCommandPos), a mismatch resets
    // it, and completing it runs SpecialCommand -> give-all. (QC `string specialcommand = "xwxwxsxsxaxdxaxdx1x "`.)
    private const string SpecialCommandSequence = "xwxwxsxsxaxdxaxdx1x ";

    /// <summary>
    /// QC <c>SpecialCommand</c> (common/physics/player.qc:582): runs <c>CheatImpulse(CHIMPULSE_GIVE_ALL)</c> when
    /// cheats are allowed (the cvar/maycheat gate lives inside the server impl). The cheat system is server-side
    /// (<c>XonoticGodot.Server.Cheats</c>), which the shared physics code can't reference, so the server installs
    /// this seam at boot (GameWorld). Null on the client-prediction path (and in headless unit tests), where the
    /// sequence still decodes/advances but the give-all is a no-op — exactly like Base, whose SpecialCommand body
    /// is <c>#ifdef SVQC</c>.
    /// </summary>
    public static System.Action<Player>? SpecialCommandGiveAll;

    /// <summary>
    /// QC <c>player_blocked</c> (server/clientkill.qc:228, common/physics/player.qc:395,728) — dom roundbased
    /// variant only: during the pre-round grace window (warmup / countdown / end-delay) the dom
    /// <c>reset_map_players</c> hook sets <c>it.player_blocked = 1</c> and <c>Domination_RoundStart</c> clears
    /// it, zeroing the player's movement input and blocking jump while the round has not yet started.
    /// The active round handler lives in the server <c>GameWorld</c>, out of reach of this headless Common
    /// driver, so the host wires it via this static seam (matching the
    /// <see cref="XonoticGodot.Common.Gameplay.WeaponFireDriver.RoundFireForbidden"/> pattern).
    /// Null when no mode has player_blocked semantics (default). Wire:
    /// <c>GameWorld.ActivateGameType</c> Domination-roundbased arm sets it; the ActivateGameType preamble
    /// clears it before any arm runs.
    /// </summary>
    public static System.Func<Entity, bool>? RoundMoveForbidden { get; set; }

    public void Move(Entity player, IMovementInput input)
    {
        float dt = input.FrameTime;
        if (dt <= 0f)
            return;

        // ----- dead players don't move (QC PlayerThink bails on IS_DEAD before PM_Main) -----
        // The authoritative server already gates this (GameWorld.OnClientMove runs DeadPlayerThink and returns
        // for a dead player), but the CLIENT-PREDICTION carrier (EntityMovementStep) and the single-process demo
        // path (PlayerController) call Move directly — without this gate a dead local player keeps sliding under
        // WASD (the corpse "drives"). DeadPlayerThink runs no corpse physics, so the server holds the body fixed
        // at the death spot; freezing the predicted/demo body here keeps client and server in lockstep. The dead
        // state is mirrored onto the prediction carrier each frame (NetGame) so this gate actually fires.
        if (player.DeadState != DeadFlag.No)
        {
            // Zero the corpse's velocity: the server freezes the body at the death spot, but it still networks the
            // velocity it died WITH — over a void that's a large downward free-fall speed. Left on the carrier it
            // feeds the render eye-extrapolation (NetGame.UpdateCamera) + stair feedforward, bobbing the death-cam
            // down then snapping back every input tic (the "view shakes over a void" report). A frozen body has no
            // velocity, so clear it here (prediction/demo only — the server never calls Move while dead).
            player.Velocity = Vector3.Zero;
            return;
        }

        // ----- specialcommand cheat-code (QC sys_phys_override -> PM_check_specialcommand) -----
        // Decode the button mask into the "xwxwxsxsxaxdxaxdx1x " sequence; completing it gives-all (cheat-gated
        // inside the server seam). QC advances the pointer under #ifdef SVQC and runs this in sys_phys_override,
        // BEFORE the branch chain. Server-authority only: on the predicted client the seam is null and the
        // decode would advance a pointer the server already owns, so skip it on the predicted leg entirely.
        // (Base returns true from sys_phys_override on completion to skip that tick's physics; the port instead
        // just fires the give-all and lets physics run normally — avoids a 1-tick server/client prediction skew
        // for a cheat-only side effect.)
        if (!input.Predicted && player is Player cheatChecker)
            CheckSpecialCommand(cheatChecker, input);

        // Per-player parameter resolution (T54): QC reads per-player STATs filled by Physics_UpdateStats —
        // preset-resolved when g_physics_clientselect is on. Resolve() consults the server's PresetProvider
        // (authoritative leg) or the client's replicated PredictionOverride (predicted leg); both default null
        // → the ambient FromCvars() read, byte-identical to the pre-T54 path.
        MovementParameters mp = MovementParameters.Resolve(player, input.Predicted);

        // Make sure the hull matches the player (QC keeps PL_MIN/PL_MAX on the entity via setsize()).
        if (player.Mins == Vector3.Zero && player.Maxs == Vector3.Zero)
        {
            player.Mins = mp.PlayerMins;
            player.Maxs = mp.PlayerMaxs;
        }

        // ----- crouch hull resize + view (QC PM_ClientMovement_UpdateStatus) -----
        // Runs in PreThink in QC, just before physics; doing it here keeps the headless sim deterministic.
        UpdateCrouch(player, input, mp);

        // ----- recover from being embedded in solid (DP SV_NudgeOutOfSolid) -----
        // If a tick begins with the hull inside solid — a tight landing on curved patch-collision geometry, a
        // telefrag overlap, a spawn-in-solid, a mover closing on the player — the move can't make progress and
        // the server holds position while the client keeps predicting forward, which reads as a stuck/rubberband
        // ("server stopped caring", then a reconcile snap). Nudge to the nearest free spot first. Deterministic
        // and run in the shared client+server sim, so it can't desync. Gated by sv_gameplayfix_nudgeoutofsolid
        // (default ON like DP; set it to 0 to disable).
        if (Api.Cvars.GetString("sv_gameplayfix_nudgeoutofsolid") != "0")
            NudgeOutOfSolid(player);

        // ----- per-frame water detection (QC _Movetype_CheckWater) -----
        CheckWater(player);

        // ----- frozen movement clamp (QC PM_check_frozen, common/physics/player.qc:649) -----
        Vector3 move = input.MoveValues;
        bool frozen = IsFrozen(player);
        if (frozen)
        {
            // QC: a frozen player normally can't steer (movement = '0 0 0'). EXCEPTION (dodging-frozen sub-case):
            // when PHYS_DODGING_FROZEN (autocvar_sv_dodging_frozen) is set, bind movement to a very slow speed —
            // bound(-2, .movement, 2) — rather than zeroing it, so the dodging mutator can still read .movement
            // for its directional calculation (a frozen Freeze-Tag player can dodge). The input bridge below writes
            // the RAW MoveValues onto the entity for the dodge detector; this clamps the player's ACTUAL motion.
            if (Api.Services is not null && Api.Cvars.GetFloat("sv_dodging_frozen") != 0f)
                move = new Vector3(
                    QMath.Bound(-2f, move.X, 2f),
                    QMath.Bound(-2f, move.Y, 2f),
                    QMath.Bound(-2f, move.Z, 2f));
            else
                move = Vector3.Zero;
        }

        // ----- typing / chat guard (QC PM_check_blocked) -----
        if (input.Typing)
            move = Vector3.Zero;

        // ----- round-blocked movement clamp (QC player_blocked: common/physics/player.qc:728-732) -----
        // Dom roundbased: during the pre-round window reset_map_players sets player_blocked=1 and
        // Domination_RoundStart clears it; QC zeroes .movement and sets disableclientprediction=1.
        // The port has no DP networking stat for disableclientprediction, but the movement zero is
        // the observable effect reproduced here via the host-wired RoundMoveForbidden predicate.
        if (RoundMoveForbidden?.Invoke(player) == true)
            move = Vector3.Zero;

        // ----- conveyors: fix velocity into the conveyor frame (QC sys_phys_update) -----
        bool onConveyor = player.ConveyorEntity is not null;
        if (onConveyor)
            player.Velocity -= player.ConveyorMoveDir;

        // ----- per-player movement-stat reset (QC Physics_UpdateStats:47 seeds STAT(MOVEVARS_HIGHSPEED) =
        //       autocvar_g_movement_highspeed each frame, then mutators multiply it via the hook below — the
        //       maxspd_mod fold at player.qc:50). Seed from the resolved g_movement_highspeed (unset → 1, the
        //       xonotic-server.cfg:586 stock value, so the golden traces are unchanged) instead of a literal 1 —
        //       a server-set g_movement_highspeed ≠ 1 now actually scales movement, identically on both sides
        //       because the cvar rides the replicated MoveVarsBlock (T54). Reset BEFORE the hook so every
        //       PlayerPhysics handler can use a pure multiplicative write to player.SpeedMultiplier (powerup
        //       speed, entrap-nade slow, buffs speed/disability) and the value stays frame-local. -----
        player.SpeedMultiplier = mp.HighSpeed;

        // ----- trigger_swamp slowdown (QC player.qc:50: maxspd_mod = PHYS_HIGHSPEED *
        //       ((swampslug.active == ACTIVE_ACTIVE) ? swampslug.swamp_slowdown : 1)). SwampThink stamps the
        //       toucher's SwampSlug = the active swamp trigger, which carries the swamp_slowdown multiplier.
        //       Fold it into the same maxspd_mod chain as HighSpeed (seed BEFORE the PlayerPhysics hook so a
        //       powerup/buff handler still multiplies on top), so a player standing in an active swamp actually
        //       moves slower. No-op when the player has no swamp slug or its swamp is inactive. -----
        if (player.SwampSlug is { } swampSlug && swampSlug.Active == MapMover.ActiveActive)
            player.SpeedMultiplier *= swampSlug.SwampSlowdown;

        // ----- INPUT BRIDGE: copy the usercmd intent onto the entity (QC PHYS_CS(this).movement / buttons / v_angle).
        //       The movement-driven mutators read these off the entity each frame: the dodging double-tap detector
        //       (DodgingMutator.CheckPressedKeys reads MovementForward/Right + PressedKeys + ButtonCrouch), the
        //       multijump dodging-redirect, and the bugrigs throttle/steer. QC populates them from the usercmd before
        //       the PlayerPhysics hook runs; the headless sim has no usercmd edict, so bridge them here, just before
        //       the hook fires. Write the RAW input.MoveValues (not the frozen/typing-clamped `move` local) so a
        //       frozen-dodge (sv_dodging_frozen) — which Base detects off the raw PHYS_CS movement — can still fire. -----
        player.MovementForward = input.MoveValues.X;
        player.MovementRight = input.MoveValues.Y;
        player.ButtonCrouch = input.ButtonCrouch;
        player.ButtonChat = input.Typing; // QC PHYS_INPUT_BUTTON_CHAT — campcheck typecheck gate reads this
        player.ViewAngles = input.ViewAngles;

        // ----- PlayerPhysics mutator hook (QC ecs/systems/physics.qc:56 MUTATOR_CALLHOOK(PlayerPhysics, this, dt))
        //       Fired after PM_check_frozen/PM_check_blocked + the conveyor velocity-fix and BEFORE the movement
        //       branch selection, so a mutator that drives a per-frame movement state machine off the input
        //       (dodging double-tap detection, multijump/walljump bookkeeping, buffs swapper) runs at exactly
        //       the QC point. Args: (player, ticrate=dt). -----
        var pp = new MutatorHooks.PlayerPhysicsArgs(player, dt);
        MutatorHooks.PlayerPhysics.Call(ref pp);

        // ----- A PlayerPhysics handler may REWRITE the wish-move intent (QC mutates CS(player).movement, which the
        //       subsequent physics reads): the CTS gametype force-quantizes analog input to keyboard cardinals/45°
        //       diagonals for record fairness (sv_cts.qc PlayerPhysics). Fold any such rewrite of the entity's
        //       MovementForward/MovementRight back into the local `move` the branch chain below consumes. This is a
        //       strict no-op in every mode that doesn't rewrite them: the input bridge above seeds them from
        //       input.MoveValues, so the X/Y only differ here if a handler changed them. We preserve the existing
        //       frozen/typing zeroing of `move` — a frozen player keeps move==0 because the X/Y guards compare
        //       against the seed, not the raw input — by only adopting the X/Y when move's X/Y is itself non-frozen
        //       (i.e. equals the seed input). -----
        if (player.MovementForward != input.MoveValues.X && move.X == input.MoveValues.X)
            move.X = player.MovementForward;
        if (player.MovementRight != input.MoveValues.Y && move.Y == input.MoveValues.Y)
            move.Y = player.MovementRight;

        // ----- CLIENT-PREDICTION speed parity (QC replicates STAT(MOVEVARS_HIGHSPEED) per-player) -----
        // The prediction carrier has none of the powerup/buff/nade status effects the PlayerPhysics hook above
        // reads, so on the predicted leg the hook left SpeedMultiplier at its HighSpeed seed (line 181) — the
        // carrier would predict BASE speed while a Speed powerup / speed buff / entrap slow is active, and every
        // tick would reconcile-pop. Adopt the server-replicated resolved multiplier (set on the carrier by NetGame
        // from the owner snapshot) so prediction scales identically to authority. A value <= 0 means "not yet
        // replicated" → leave the seed untouched. SVQC is unaffected (input.Predicted is false there).
        if (input.Predicted && player.SpeedMultiplierPredicted > 0f)
            player.SpeedMultiplier = player.SpeedMultiplierPredicted;

        // ----- honour the per-player top-speed multiplier set by the PlayerPhysics handlers (QC
        //       Physics_UpdateStats: STAT(MOVEVARS_HIGHSPEED) drives g_movement_highspeed). The Speed powerup
        //       (×1.5), the entrap nade (×0.5) and the speed/disability buffs all ride player.SpeedMultiplier;
        //       ApplyHighSpeed no-ops when it's 1. This is the single integration point that makes those inert
        //       writes actually affect movement. -----
        if (player.SpeedMultiplier != 1f)
            mp.ApplyHighSpeed(player.SpeedMultiplier);

        // ----- spectator free-flight speed control (QC ecs/systems/physics.qc:58-62) -----
        // QC: `float maxspeed_mod = 1; ...; if (!IS_PLAYER(this)) { sys_phys_spectator_control(this);
        //      maxspeed_mod = STAT(SPECTATORSPEED, this); } sys_phys_fixspeed(this, maxspeed_mod);`
        // !IS_PLAYER (classname != STR_PLAYER) maps to Player.IsObserver (ADR-0007): a spectator/observer has
        // MOVETYPE_NOCLIP/FLY (set at TRANSMUTE) so it naturally takes the fly branch; spectator-control just
        // scales its speed. A DEAD player keeps IS_PLAYER true (and takes the dead early-return upstream), so it
        // does NOT enter here — only a real observer does. sys_phys_fixspeed's stuffcmd of cl_forwardspeed is
        // partly cosmetic (wishmove is rescaled client-side via WishMoveScaling), EXCEPT its maxspeed_mod factor,
        // which we apply to the wishmove in the maxspeedMod block below (else the fly-branch wishspeed cap pins a
        // spectator at base speed regardless of SPECTATORSPEED).
        float maxspeedMod = 1f;
        if (player is Player { IsObserver: true } spectator)
        {
            SpectatorControl(spectator, input);
            maxspeedMod = spectator.SpectatorSpeed;
        }

        // QC fly branch scales BOTH the top speed AND the accel rate by maxspeed_mod (physics.qc:116-117:
        // com_phys_vel_max = PHYS_MAXSPEED * maxspeed_mod; com_phys_acc_rate = PHYS_ACCELERATE * maxspeed_mod).
        // An observer always takes the fly branch (FlyBranch uses mp.MaxSpeed + mp.Accelerate), so apply the
        // multiplier to both. ApplyHighSpeed scales MaxSpeed (+ the air-strafe/nonqw speeds) but NOT Accelerate,
        // so scale Accelerate explicitly to match the fly branch's acc_rate. Only the observer sets maxspeedMod
        // != 1 (a live player never reaches here), so this can't perturb a normal player's accel. No-op at 1.
        if (maxspeedMod != 1f)
        {
            mp.ApplyHighSpeed(maxspeedMod);
            mp.Accelerate *= maxspeedMod;
            // QC sys_phys_fixspeed raises cl_forwardspeed/back/side/up to max(maxspeed,maxairspeed)*maxspeed_mod so
            // the client's wishmove can reach the scaled top speed. The fly branch caps wishspeed at |wishvel| (the
            // input magnitude), so without scaling the wishmove too, a spectator would stay at BASE fly speed despite
            // the ×mod vel_max. Scale the local wishmove here. Only observers reach maxspeedMod != 1.
            move *= maxspeedMod;
        }

        // ----- slick detection from the surface under the player (QC PM_check_slick) -----
        CheckSlick(player);

        // ----- build wishdir basis (2D yaw-only for ground/air; full angles for water/ladder/jetpack) ---
        Vector3 viewAngles = input.ViewAngles;

        // ----- jump / jetpack activation (QC CheckPlayerJump -> PlayerJump) -----
        // Must run before friction/accel, as in PM_Main. Returns whether the jetpack thrust is active.
        // QC physics.qc:86-96 calls CheckPlayerJump ONLY inside `if (IS_PLAYER(this))`, so a spectator/observer
        // (the !IS_PLAYER case) NEVER runs it. Gate on the same Player.IsObserver test used for the spectator
        // maxspeed_mod branch above: an observer reports no-jetpack (false), matching QC where CheckPlayerJump —
        // and thus the jetpack activation it owns — is simply never reached for !IS_PLAYER. (A dead player keeps
        // IS_PLAYER true and would still run it in QC, so do NOT widen this guard to IsDead.)
        bool jetpackActive = (player is Player { IsObserver: true })
            ? false
            : CheckPlayerJump(player, input, mp, move);

        // ----- determine onground -----
        bool onground = DetermineOnGround(player, mp);

        // ===== branch selection (QC sys_phys_update) =====
        if ((player.Flags & EntFlags.WaterJump) != 0)
        {
            // climbing out of water: hold the horizontal push, finish when clear/timeout.
            Vector3 v = player.Velocity;
            v.X = player.MoveDir.X;
            v.Y = player.MoveDir.Y;
            player.Velocity = v;
            if (Now() > player.TeleportTime || player.WaterLevel == WaterLevelNone)
            {
                player.Flags &= ~EntFlags.WaterJump;
                player.TeleportTime = 0f;
            }
            WalkMove(player, mp, dt, applyGravity: true, viewAngles);
        }
        else if (MutatorHooks.PMPhysics.Count > 0 && CallPmPhysics(player, maxspeedMod, dt))
        {
            // PM_Physics mutator hook (QC physics.qc:108 `else if (MUTATOR_CALLHOOK(PM_Physics, this,
            // maxspeed_mod, dt)) { /* handled */ }`). A true return means a mutator (bugrigs, vehicle drive
            // physics, ...) FULLY REPLACED the move — the whole branch chain below is skipped (QC's empty
            // body), only the post-update bookkeeping runs. Gated on Count>0 so the default path is
            // allocation-free and the golden movement-parity traces are unaffected (Call returns false when
            // no handler is registered).
        }
        else if (player.MoveType is MoveType.Noclip or MoveType.Fly or MoveType.FlyWorldOnly
                 || (MutatorHooks.IsFlying.Count > 0 && CallIsFlying(player)))
        {
            // free flight: PM_Accelerate with air friction, no ground. The IsFlying hook (QC physics.qc:113,
            // the last `||` term) FORCES this branch even on a walking player (e.g. a mutator that grants
            // temporary flight). Count>0 gate keeps it inert by default.
            FlyBranch(player, mp, dt, viewAngles, move);
            Integrate(player, mp, dt, viewAngles, applyGravity: false, noCollide: player.MoveType == MoveType.Noclip);
        }
        else if (player.WaterLevel >= WaterLevelSwimming)
        {
            SwimMove(player, mp, dt, input, viewAngles, move);
            player.JumpPadCount = 0;
            Integrate(player, mp, dt, viewAngles, applyGravity: false, noCollide: false);
        }
        else if (player.LadderEntity is not null)
        {
            // The ladder branch adds +gravity to its velocity to cancel the gravity the WalkMove
            // integrator then applies (QC com_phys_gravity), netting gravity-free climbing.
            LadderMove(player, mp, dt, viewAngles, move);
            WalkMove(player, mp, dt, applyGravity: true, viewAngles);
        }
        else if (jetpackActive)
        {
            PMJetpack(player, input, mp, dt, viewAngles, move, maxspeedMod);
            Integrate(player, mp, dt, viewAngles, applyGravity: false, noCollide: false);
        }
        else if (onground && (!player.OnSlick || !SlickApplyGravity))
        {
            GroundBranch(player, mp, dt, viewAngles, move);
            WalkMove(player, mp, dt, applyGravity: true, viewAngles);
        }
        else
        {
            AirBranch(player, mp, dt, viewAngles, move);
            WalkMove(player, mp, dt, applyGravity: true, viewAngles);
        }

        // ----- end-of-tick bookkeeping (QC sys_phys_postupdate) -----
        // Base runs PM_check_hitground/PM_Footsteps at physics.qc:90-91 BEFORE sys_phys_postupdate sets
        // `this.lastground = time` at physics.qc:188, so the footstep `time >= lastground + 0.2` gate reads the
        // PRIOR-tick lastground (it lags one tick and genuinely suppresses the first grounded frame after a
        // >0.2s fall). The port runs UpdateMovementSounds after the postupdate update, so capture the prior
        // value here and feed it to Footsteps — reproducing Base's one-tick-lagged gate instead of reading `now`.
        float priorLastGroundTime = player.LastGroundTime;
        if (player.OnGround)
            player.LastGroundTime = Now();
        // conveyors: restore velocity out of the conveyor frame
        if (onConveyor)
            player.Velocity += player.ConveyorMoveDir;
        UpdateMovementSounds(player, mp, input.Predicted, priorLastGroundTime);
        CheckPunch(player, dt, input.Predicted);
        player.LastFlags = player.Flags;

        // QC physics.qc:24 saves PHYS_CS(this).movement_old = .movement at the TOP of the next sys_phys_update;
        // the port has no separate pre-pass, so snapshot the forward wishmove here (read next tick by the
        // viewloc ladder sub-case). Inert on stock maps (ViewLoc null).
        player.MovementForwardOld = player.MovementForward;

        // QC sys_phys_postupdate: `this.lastclassname = this.classname;` (physics.qc:194). The port tracks the
        // spectator half of this — whether the entity was an observer this tick — so the next tick's
        // spectator-speed ladder can gate its STEP on "was a spectator last tick" (QC lastclassname != STR_PLAYER).
        player.WasSpectatorLastTick = player is Player { IsObserver: true };

        // Install reference (the lead wires this in GameInit):
        //   Movement.System = new PlayerPhysics();
    }

    // sv_slick_applygravity default off: on slick ground the player still uses the ground branch.
    private const bool SlickApplyGravity = false;

    // ===============================================================================================
    //  PM_Physics / IsFlying mutator-hook call-sites (QC ecs/systems/physics.qc:108 / :113)
    // ===============================================================================================

    /// <summary>
    /// Fire EV_PM_Physics (physics.qc:108). Returns true when a handler fully replaced the move (QC's
    /// CALLHOOK "any returned true" — HookChain.Call ORs the handler returns). Slots: player, maxspeed_mod, dt.
    /// </summary>
    private static bool CallPmPhysics(Entity player, float maxspeedMod, float dt)
    {
        var a = new MutatorHooks.PMPhysicsArgs(player, maxspeedMod, dt);
        return MutatorHooks.PMPhysics.Call(ref a);
    }

    /// <summary>Fire EV_IsFlying (physics.qc:113, the last <c>||</c> term). True forces the fly branch.</summary>
    private static bool CallIsFlying(Entity player)
    {
        var a = new MutatorHooks.IsFlyingArgs(player);
        return MutatorHooks.IsFlying.Call(ref a);
    }

    // ===============================================================================================
    //  Spectator free-flight speed control — sys_phys_spectator_control (QC ecs/systems/sv_physics.qc:67-103)
    // ===============================================================================================

    /// <summary>
    /// QC <c>sys_phys_spectator_control</c>: seed the per-spectator <see cref="Entity.SpectatorSpeed"/> to
    /// <c>autocvar_sv_spectator_speed_multiplier</c> on first use, then — only when an impulse in the speed-step
    /// ranges (1-19 / 200-209 / 220-229) is present — adjust it along the ladder and CLEAR the impulse. The
    /// ladder STEP only happens when the entity was already a spectator on the previous tick (QC
    /// <c>this.lastclassname != STR_PLAYER</c>); on the first tick after un-spawning the impulse is merely
    /// consumed (no step). Faithful to sv_physics.qc line-for-line:
    ///  - 10 / 15 / 18 / 200-209 → bound(min, +0.5, max)
    ///  - 11                     → reset to the multiplier
    ///  - 12 / 16 / 19 / 220-229 → bound(min, -0.5, max)
    ///  - 1-9                    → 1 + 0.5 * (impulse - 1)
    /// </summary>
    private static void SpectatorControl(Player spec, IMovementInput input)
    {
        // autocvar_sv_spectator_speed_multiplier: read RAW (no inline QC default → 0 when unset; the 1.5 in
        // xonotic-server.cfg is a cfg value, not an engine default — do not substitute it here).
        float maxspeedMod = Api.Cvars.GetFloat("sv_spectator_speed_multiplier");
        float min = SpectatorCvar("sv_spectator_speed_multiplier_min", SpectatorSpeedMultiplierMinDefault);
        float max = SpectatorCvar("sv_spectator_speed_multiplier_max", SpectatorSpeedMultiplierMaxDefault);

        // QC: if (!STAT(SPECTATORSPEED, this)) STAT(SPECTATORSPEED, this) = maxspeed_mod;
        if (spec.SpectatorSpeed == 0f)
            spec.SpectatorSpeed = maxspeedMod;

        int impulse = input.Impulse;
        if ((impulse >= 1 && impulse <= 19)
            || (impulse >= 200 && impulse <= 209)
            || (impulse >= 220 && impulse <= 229))
        {
            // step only when the previous tick was already a spectator (QC lastclassname != STR_PLAYER)
            if (spec.WasSpectatorLastTick)
            {
                if (impulse == 10 || impulse == 15 || impulse == 18 || (impulse >= 200 && impulse <= 209))
                    spec.SpectatorSpeed = QMath.Bound(min, spec.SpectatorSpeed + 0.5f, max);
                else if (impulse == 11)
                    spec.SpectatorSpeed = maxspeedMod;
                else if (impulse == 12 || impulse == 16 || impulse == 19 || (impulse >= 220 && impulse <= 229))
                    spec.SpectatorSpeed = QMath.Bound(min, spec.SpectatorSpeed - 0.5f, max);
                else if (impulse >= 1 && impulse <= 9)
                    spec.SpectatorSpeed = 1f + 0.5f * (impulse - 1);
            }
            // otherwise just clear (QC CS(this).impulse = 0). The net layer also zeroes the cached command's
            // impulse after dispatch (ServerNet.ProvideInput), so there's no further per-tick re-read to clear.
        }
    }

    // sv_spectator_speed_multiplier_min/_max: non-zero inline QC defaults (1 / 5), so treat a 0 read (cvar
    // unset) as "use default" — the same idiom MovementParameters.Cvar uses for non-zero-default magnitudes.
    private static float SpectatorCvar(string name, float fallback)
    {
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }

    // g_jetpack_* tunables: non-zero stock balance-xonotic.cfg defaults, so a 0 read (cvar unset) → fallback
    // (same idiom as MovementParameters.Cvar). reverse_thrust's stock value is a genuine 0 and is read raw.
    private static float JetpackCvar(string name, float fallback)
    {
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }

    // ===============================================================================================
    //  specialcommand cheat-code — PM_check_specialcommand / SpecialCommand (QC common/physics/player.qc)
    // ===============================================================================================

    /// <summary>
    /// QC <c>PM_check_specialcommand</c> (player.qc:592): decode this tick's button mask to a single character
    /// (<c>PHYS_INPUT_BUTTON_MASK</c>) and walk it through the secret <c>"xwxwxsxsxaxdxaxdx1x "</c> sequence —
    /// advancing <see cref="Entity.SpecialCommandPos"/> on a match, resetting on a mismatch, and firing
    /// <see cref="SpecialCommandGiveAll"/> (QC <c>SpecialCommand</c> → give-all) when the sequence completes.
    /// Server-authority only (QC advances the pointer under <c>#ifdef SVQC</c>); called from <see cref="Move"/>
    /// on the non-predicted tick. The button decode mirrors <c>player.qh:191</c> exactly:
    ///   0 → "x", ATCK → "1", ATCK2 → " ", BACKWARD(move.x&lt;0) → "s", FORWARD(move.x&gt;0) → "w",
    ///   LEFT(move.y&lt;0) → "a", RIGHT(move.y&gt;0) → "d", anything else → "?".
    /// </summary>
    private static void CheckSpecialCommand(Player player, IMovementInput input)
    {
        Vector3 move = input.MoveValues;

        // QC PHYS_INPUT_BUTTON_MASK then the switch in PM_check_specialcommand. Only the buttons the decode
        // distinguishes need to be tested; any combination not matching a single case maps to '?' (a reset).
        char c;
        bool atck = input.ButtonAttack1;
        bool jump = input.ButtonJump;
        bool atck2 = input.ButtonAttack2;
        bool crouch = input.ButtonCrouch;
        bool hook = input.ButtonHook;
        bool use = input.ButtonUse;
        bool back = move.X < 0f, fwd = move.X > 0f, left = move.Y < 0f, right = move.Y > 0f;

        // Count how many of the mask's bits are set; the QC switch matches only single-bit (or zero) masks.
        int bits = (atck ? 1 : 0) + (jump ? 1 : 0) + (atck2 ? 1 : 0) + (crouch ? 1 : 0) + (hook ? 1 : 0)
                 + (use ? 1 : 0) + (back ? 1 : 0) + (fwd ? 1 : 0) + (left ? 1 : 0) + (right ? 1 : 0);
        if (bits == 0) c = 'x';
        else if (bits == 1 && atck) c = '1';
        else if (bits == 1 && atck2) c = ' ';
        else if (bits == 1 && back) c = 's';
        else if (bits == 1 && fwd) c = 'w';
        else if (bits == 1 && left) c = 'a';
        else if (bits == 1 && right) c = 'd';
        else c = '?'; // BIT(1) jump / BIT(3) zoom / BIT(4) crouch / BIT(5) hook / BIT(6) use / multi-bit → default

        int pos = player.SpecialCommandPos;
        if (pos < SpecialCommandSequence.Length && c == SpecialCommandSequence[pos])
        {
            ++pos;
            if (pos >= SpecialCommandSequence.Length)
            {
                player.SpecialCommandPos = 0;
                SpecialCommand(player);
                return;
            }
            player.SpecialCommandPos = pos;
        }
        else if (pos > 0 && c != SpecialCommandSequence[pos - 1])
        {
            player.SpecialCommandPos = 0;
        }
    }

    /// <summary>QC <c>SpecialCommand</c> (player.qc:582): give-all when cheats are allowed (the cvar/maycheat
    /// gate lives inside the installed server seam). No-op when no seam is installed (client/headless).</summary>
    private static void SpecialCommand(Player player) => SpecialCommandGiveAll?.Invoke(player);

    // ===============================================================================================
    //  Ground branch — friction + simple Quake accelerate (QC sys_phys_simulate, com_phys_ground)
    // ===============================================================================================
    private static void GroundBranch(Entity player, in MovementParameters mp, float dt, Vector3 viewAngles, Vector3 move)
    {
        (Vector3 wishdir, float wishspeed) = WishDir2D(viewAngles, move);
        wishspeed = MathF.Min(wishspeed, mp.MaxSpeed);
        if (player.IsDucked)
            wishspeed *= 0.5f;
        GroundMove(player, mp, dt, wishdir, wishspeed);
    }

    private static void GroundMove(Entity player, in MovementParameters mp, float dt, Vector3 wishdir, float wishspeed)
    {
        // --- friction (the PHYS_FRICTION_REPLICA_DT geometric form ported verbatim) ---
        float f2 = Vec2LenSq(player.Velocity);
        float realfriction = player.OnSlick ? mp.FrictionSlick : mp.Friction;
        if (f2 > 0f && realfriction > 0f)
        {
            float speed = MathF.Sqrt(f2);
            float S = mp.StopSpeed;
            const float dt_r = FrictionReplicaDt;

            float f;
            float independentGeometric = MathF.Pow(1f - realfriction * dt_r, dt / dt_r);
            if (S < speed && speed < S / independentGeometric)
            {
                Vector3 n = QMath.Normalize(player.Velocity);
                player.Velocity = n;
                f = S - S * realfriction * (dt - (dt_r * MathF.Log(S / speed)) / MathF.Log(1f - realfriction * dt_r));
            }
            else if (speed >= S)
            {
                f = independentGeometric;
            }
            else
            {
                f = 1f - realfriction * dt * S / speed;
            }
            f = MathF.Max(0f, f);
            player.Velocity *= f;
        }

        // --- accelerate (QC ground branch: simple Quake accelerate, NOT PM_Accelerate) ---
        float addspeed = wishspeed - QMath.Dot(player.Velocity, wishdir);
        if (addspeed > 0f)
        {
            float accel = player.OnSlick ? mp.SlickAccelerate : mp.Accelerate;
            float accelspeed = MathF.Min(accel * dt * wishspeed, addspeed);
            player.Velocity += accelspeed * wishdir;
        }
    }

    // QC PHYS_FRICTION_REPLICA_DT = 0.00390625 (1/256).
    private const float FrictionReplicaDt = 0.00390625f;

    // ===============================================================================================
    //  Air branch — QW air-accel + strafe blends + airstop + CPM aircontrol (com_phys_air)
    // ===============================================================================================
    private static void AirBranch(Entity player, in MovementParameters mp, float dt, Vector3 viewAngles, Vector3 move)
    {
        (Vector3 wishdir, float wishspeed) = WishDir2D(viewAngles, move);

        float maxairspd = mp.MaxAirSpeed;          // com_phys_vel_max
        wishspeed = MathF.Min(wishspeed, maxairspd);

        float airaccelqw = mp.AirAccelQW;
        float wishspeed0 = wishspeed;
        if (player.IsDucked)
            wishspeed *= 0.5f;

        float airaccel = mp.AirAccelerate;          // com_phys_acc_rate_air

        bool accelerating = QMath.Dot(player.Velocity, wishdir) > 0f;
        float wishspeed2 = wishspeed;

        // CPM: airstopaccelerate (slow down when pushing against current horizontal velocity)
        if (mp.AirStopAccelerate != 0f)
        {
            float dot = QMath.Dot(QMath.Normalize(PMAccelerate.Vec2(player.Velocity)), wishdir);
            if (dot < 0f)
            {
                if (mp.AirStopAccelerateFull)
                    airaccel = mp.AirStopAccelerate;
                else
                    airaccel += (airaccel - mp.AirStopAccelerate) * dot;
            }
        }

        float strafity = PMAccelerate.IsMoveInDirection(move, -90f) + PMAccelerate.IsMoveInDirection(move, +90f);

        if (mp.MaxAirStrafeSpeed != 0f)
            wishspeed = MathF.Min(wishspeed, PMAccelerate.GeomLerp(mp.MaxAirSpeed, strafity, mp.MaxAirStrafeSpeed));

        if (mp.AirStrafeAccelerate != 0f)
            airaccel = PMAccelerate.GeomLerp(airaccel, strafity, mp.AirStrafeAccelerate);

        if (mp.AirStrafeAccelQW != 0f)
        {
            float chosen = strafity > 0.5f ? mp.AirStrafeAccelQW : mp.AirAccelQW;
            airaccelqw =
                ((chosen >= 0f) ? +1f : -1f)
                * (1f - PMAccelerate.GeomLerp(1f - MathF.Abs(mp.AirAccelQW), strafity, 1f - MathF.Abs(mp.AirStrafeAccelQW)));
        }

        if (mp.WarsowBunnyTurnAccel != 0f && accelerating && move.Y == 0f && move.X != 0f)
        {
            PMAccelerate.AirAccelerate(player, mp, dt, wishdir, wishspeed2);
        }
        else
        {
            float sidefric = maxairspd != 0f ? (mp.AirAccelSidewaysFriction / maxairspd) : 0f;
            // GameplayFixQ2AirAccelerate (live Base: ON) makes the accel step use THIS branch's strafe-clamped/
            // duck-halved wishspeed instead of wishspeed0 — the limiter on mid-air strafe acceleration. The air
            // branch is the only Accelerate call site where wishspeed != wishspeed0, so the flag is passed here
            // only (the water/ladder/fly calls pass wishspeed twice — the fix is a no-op there by construction).
            PMAccelerate.Accelerate(player, dt, wishdir, wishspeed, wishspeed0, airaccel, airaccelqw,
                mp.AirAccelQWStretchFactor, sidefric, mp.AirSpeedLimitNonQW, mp.GameplayFixQ2AirAccelerate);
        }

        if (mp.AirControl != 0f)
            PMAccelerate.Aircontrol(player, mp, dt, move, wishdir, wishspeed2);
    }

    // ===============================================================================================
    //  Swim branch — PM_swim (QC sys_phys_simulate, com_phys_water)
    // ===============================================================================================
    private static void SwimMove(Entity player, in MovementParameters mp, float dt, IMovementInput input, Vector3 viewAngles, Vector3 move)
    {
        // QW water-jump out of the surface (this mimics quakeworld code).
        if (input.ButtonJump && player.WaterLevel == WaterLevelSwimming && player.Velocity.Z >= -180f
            && player.ViewLoc is null && !IsFrozen(player))
        {
            Vector3 yawAngles = new(0f, viewAngles.Y, 0f);
            QMath.AngleVectors(yawAngles, out Vector3 fwd, out _, out _);
            Vector3 spot = player.Origin + 24f * fwd;
            spot.Z += 8f;
            TraceResult t1 = Api.Trace.Trace(spot, Vector3.Zero, Vector3.Zero, spot, MoveFilter.NoMonsters, player);
            if (t1.StartSolid)
            {
                spot.Z += 24f;
                TraceResult t2 = Api.Trace.Trace(spot, Vector3.Zero, Vector3.Zero, spot, MoveFilter.NoMonsters, player);
                if (!t2.StartSolid)
                {
                    player.Velocity = fwd * 50f;
                    player.Velocity = new Vector3(player.Velocity.X, player.Velocity.Y, 310f);
                    player.Flags &= ~EntFlags.OnGround;
                    player.Flags &= ~EntFlags.JumpReleased; // SET_JUMP_HELD
                }
            }
        }

        // full-3D wish velocity (Z component drives dive/surface)
        QMath.AngleVectors(viewAngles, out Vector3 forward, out Vector3 right, out _);
        Vector3 wishvel = forward * move.X + right * move.Y + new Vector3(0f, 0f, 1f) * move.Z;

        if (IsFrozen(player))
        {
            if (player.WaterLevel >= WaterLevelSubmerged && player.Velocity.Z >= -70f)
                wishvel = new Vector3(0f, 0f, 160f);             // resurface
            else if (player.WaterLevel >= WaterLevelSwimming && player.Velocity.Z > 0f)
                wishvel = new Vector3(0f, 0f, 1.3f * MathF.Min(player.Velocity.Z, 160f));
        }
        else
        {
            // QC physics.qc:267-272 — crouch is a SEPARATE `if` (dive at max speed); then viewloc forces a
            // constant downward drift (-160), ELSE (no viewloc) a still player drifts to the bottom (-60). The
            // `else if (wishvel == 0)` is attached to the viewloc test, NOT to crouch, so reproduce that order.
            if (input.ButtonCrouch)
                wishvel.Z = -mp.MaxSpeed;
            if (player.ViewLoc is not null)
                wishvel.Z = -160f;                               // 1D-rail: drift anyway
            else if (wishvel == Vector3.Zero)
                wishvel.Z = -60f;                                // drift towards the bottom
        }

        float wishspeed = wishvel.Length();
        Vector3 wishdir = wishspeed != 0f ? wishvel * (1f / wishspeed) : Vector3.Zero;
        wishspeed = MathF.Min(wishspeed, mp.MaxSpeed);

        wishspeed *= 0.7f; // water is slow

        // water friction
        float f = 1f - dt * mp.Friction;
        player.Velocity *= QMath.Bound(0f, f, 1f);

        f = wishspeed - QMath.Dot(player.Velocity, wishdir);
        if (f > 0f)
        {
            float accelspeed = MathF.Min(mp.Accelerate * dt * wishspeed, f);
            player.Velocity += accelspeed * wishdir;
        }

        // holding jump swims upward
        if (input.ButtonJump && player.ViewLoc is null && !IsFrozen(player))
        {
            float upspeed = player.WaterLevel >= WaterLevelSubmerged ? mp.MaxSpeed * 0.7f : 200f;
            player.Velocity = new Vector3(player.Velocity.X, player.Velocity.Y, upspeed);
        }

        // QC physics.qc:408-421 — a viewloc (1D-rail) water volume uses a plain addspeed accel (no QW pass);
        // otherwise the standard QW water acceleration (PM_Accelerate with accelqw 1, no clamp). Inert on
        // stock 3D maps (ViewLoc null), but a real Base branch.
        if (player.ViewLoc is not null)
        {
            float addspeed = wishspeed - QMath.Dot(player.Velocity, wishdir);
            if (addspeed > 0f)
            {
                float accelspeed = MathF.Min(mp.Accelerate * dt * wishspeed, addspeed);
                player.Velocity += accelspeed * wishdir;
            }
        }
        else
        {
            PMAccelerate.Accelerate(player, dt, wishdir, wishspeed, wishspeed, mp.Accelerate, 1f, 0f, 0f, 0f);
        }
    }

    // ===============================================================================================
    //  Ladder / fly branch — gravity-free PM_Accelerate (QC com_phys_ladder / noclip-fly path)
    // ===============================================================================================
    private static void LadderMove(Entity player, in MovementParameters mp, float dt, Vector3 viewAngles, Vector3 move)
    {
        UnsetOnGround(player);

        // ladder uses air friction with a gravity term folded into the half-step around the accel:
        // sys_phys_simulate applies (com_phys_friction_air): vel.z += grav/2; vel *= 1-dt*friction; vel.z += grav/2.
        float grav = -ApplyEntGravity(player, mp) * dt; // com_phys_gravity = -gravity*dt (entgravity-scaled)
        player.Velocity = new Vector3(player.Velocity.X, player.Velocity.Y, player.Velocity.Z + (-grav) * 0.5f);
        player.Velocity *= 1f - dt * mp.Friction;
        player.Velocity = new Vector3(player.Velocity.X, player.Velocity.Y, player.Velocity.Z + (-grav) * 0.5f);

        QMath.AngleVectors(viewAngles, out Vector3 forward, out Vector3 right, out _);
        Vector3 wishvel = forward * move.X + right * move.Y + new Vector3(0f, 0f, 1f) * move.Z;

        // QC physics.qc:277-278 — a viewloc (1D-rail) ladder drives the vertical wish from the PREVIOUS tick's
        // forward wishmove (movement_old.x). Inert on stock 3D maps (ViewLoc null).
        if (player.ViewLoc is not null)
            wishvel.Z = player.MovementForwardOld;

        // QC physics.qc:280-299 — a func_water "ladder" clamps the wish to the volume's speed and fills the
        // waterlevel/watertype FROM THE VOLUME BOUNDS (CheckWater early-returns for func_water and defers here).
        // Without this a func_water ladder leaves WaterLevelNone. Rare map feature, but a real Base branch.
        if (player.LadderEntity is { ClassName: "func_water" } water)
        {
            float fl = wishvel.Length();
            if (fl > water.Speed)
                wishvel *= water.Speed / fl;

            player.WaterType = (int)water.Skin;                       // QC watertype = ladder_entity.skin
            fl = water.Origin.Z + water.Maxs.Z;                      // surface plane (volume top)
            if (player.Origin.Z + player.ViewOfs.Z < fl)
                player.WaterLevel = WaterLevelSubmerged;
            else if (player.Origin.Z + (player.Mins.Z + player.Maxs.Z) * 0.5f < fl)
                player.WaterLevel = WaterLevelSwimming;
            else if (player.Origin.Z + player.Mins.Z + 1f < fl)
                player.WaterLevel = WaterLevelWetFeet;
            else
            {
                player.WaterLevel = WaterLevelNone;
                player.WaterType = (int)Contents.Empty;             // QC watertype = CONTENT_EMPTY
            }
        }

        float wishspeed = wishvel.Length();
        Vector3 wishdir = wishspeed != 0f ? wishvel * (1f / wishspeed) : Vector3.Zero;
        wishspeed = MathF.Min(wishspeed, mp.MaxSpeed);

        // ladders, noclip, fly: PM_Accelerate(dt, wishdir, wishspeed, wishspeed, accel, 1, 0, 0, 0)
        PMAccelerate.Accelerate(player, dt, wishdir, wishspeed, wishspeed, mp.Accelerate, 1f, 0f, 0f, 0f);
    }

    private static void FlyBranch(Entity player, in MovementParameters mp, float dt, Vector3 viewAngles, Vector3 move)
    {
        UnsetOnGround(player);

        // noclip/fly: air friction half-step (no gravity), then full-3D PM_Accelerate.
        player.Velocity *= 1f - dt * mp.Friction;

        QMath.AngleVectors(viewAngles, out Vector3 forward, out Vector3 right, out _);
        Vector3 wishvel = forward * move.X + right * move.Y + new Vector3(0f, 0f, 1f) * move.Z;

        float wishspeed = wishvel.Length();
        Vector3 wishdir = wishspeed != 0f ? wishvel * (1f / wishspeed) : Vector3.Zero;
        wishspeed = MathF.Min(wishspeed, mp.MaxSpeed);

        PMAccelerate.Accelerate(player, dt, wishdir, wishspeed, wishspeed, mp.Accelerate, 1f, 0f, 0f, 0f);
    }

    // ===============================================================================================
    //  Jetpack thrust — PM_jetpack (QC common/physics/player.qc)
    // ===============================================================================================
    private static void PMJetpack(Entity player, IMovementInput input, in MovementParameters mp, float dt, Vector3 viewAngles, Vector3 move, float maxspeedMod)
    {
        // QC PM_jetpack reads the g_jetpack_* STATs (Physics_UpdateStats fills them from the autocvars). Read
        // them live here so a server retuning the jetpack takes effect (the baked constants are the fallback /
        // stock balance-xonotic.cfg defaults). NOTE balance-xonotic.cfg sets g_jetpack_maxspeed_side 1200 (the
        // 1500 is the nexuiz25 balance), so the stock default is 1200. Cvar(name,default) treats a 0 read (cvar
        // unset) as "use default"; reverse_thrust's stock value is a genuine 0, so read it raw.
        float jpAccelSide   = JetpackCvar("g_jetpack_acceleration_side", JetpackAccelSide);
        float jpAccelUp     = JetpackCvar("g_jetpack_acceleration_up",   JetpackAccelUp);
        float jpAntiGravity = JetpackCvar("g_jetpack_antigravity",       JetpackAntiGravity);
        float jpMaxSpeedSide= JetpackCvar("g_jetpack_maxspeed_side",     JetpackMaxSpeedSide);
        float jpMaxSpeedUp  = JetpackCvar("g_jetpack_maxspeed_up",       JetpackMaxSpeedUp);
        float jpFuel        = JetpackCvar("g_jetpack_fuel",              JetpackFuel);
        float jpReverse     = Api.Cvars.GetFloat("g_jetpack_reverse_thrust"); // stock 0 -> read raw

        QMath.AngleVectors(viewAngles, out Vector3 forward, out Vector3 right, out _);
        Vector3 wishvel = forward * move.X + right * move.Y;
        // QC player.qc:742: maxairspd = PHYS_MAXAIRSPEED * max(1, maxspd_mod). Normal player maxspd_mod == 1 so
        // this is a strict no-op; a spectator/high-speed-mod jetpack scales the speedhack-clamp denominator.
        float maxairspd = mp.MaxAirSpeed * MathF.Max(1f, maxspeedMod);
        // normalize horizontal wish, scaled to <= 1 (fix speedhacks)
        float wlen = wishvel.Length();
        wishvel = QMath.Normalize(wishvel) * MathF.Min(1f, wlen / maxairspd);
        wishvel.Z = 0f;

        // up component from the leftover of the unit sphere
        wishvel.Z = MathF.Sqrt(MathF.Max(0f, 1f - QMath.Dot(wishvel, wishvel)));

        float aSide = jpAccelSide;
        float aUp = jpAccelUp;
        float aAdd = jpAntiGravity * mp.Gravity;
        bool reverse = jpReverse != 0f && input.ButtonCrouch;
        if (reverse) aUp = jpReverse;

        wishvel.X *= aSide;
        wishvel.Y *= aSide;
        wishvel.Z = wishvel.Z * aUp;
        wishvel.Z += aAdd;
        if (reverse) wishvel.Z *= -1f;

        // find the maximum achievable acceleration magnitude (QC closed-form over the unit sphere)
        float best = 0f;
        float aDiff = aSide * aSide - aUp * aUp;
        float ff;
        if (aDiff != 0f)
        {
            ff = aAdd * aUp / aDiff;
            if (ff > -1f && ff < 1f)
                best = (aDiff + aAdd * aAdd) * (aDiff + aUp * aUp) / aDiff;
        }
        ff = (aUp + aAdd) * (aUp + aAdd);
        if (ff > best) best = ff;
        ff = (aUp - aAdd) * (aUp - aAdd);
        if (ff > best) best = ff;
        best = MathF.Sqrt(best);

        float fxy = QMath.Bound(0f,
            1f - QMath.Dot(player.Velocity, QMath.Normalize(new Vector3(wishvel.X, wishvel.Y, 0f))) / jpMaxSpeedSide, 1f);
        float fz;
        if (wishvel.Z - mp.Gravity > 0f)
            fz = QMath.Bound(0f, 1f - player.Velocity.Z / jpMaxSpeedUp, 1f);
        else
            fz = QMath.Bound(0f, 1f + player.Velocity.Z / jpMaxSpeedUp, 1f);

        float fvel = wishvel.Length();
        wishvel.X *= fxy;
        wishvel.Y *= fxy;
        wishvel.Z = (wishvel.Z - mp.Gravity) * fz + mp.Gravity;

        fvel = MathF.Min(1f, wishvel.Length() / best);
        float f;
        if (jpFuel != 0f && !player.UnlimitedAmmo)
        {
            float fuel = player.GetResource(ResourceType.Fuel);
            f = MathF.Min(1f, fuel / (jpFuel * dt * fvel));
        }
        else f = 1f;

        if (f > 0f && wishvel != Vector3.Zero)
        {
            player.Velocity += wishvel * f * dt;
            UnsetOnGround(player);

            if (!player.UnlimitedAmmo)
                player.TakeResource(ResourceType.Fuel, jpFuel * dt * fvel * f);

            player.UsingJetpack = true;
            player.PauseRegenFinished = MathF.Max(player.PauseRegenFinished, Now() + PauseFuelRegen);
        }
    }

    // ===============================================================================================
    //  Jump + jetpack activation — CheckPlayerJump / PlayerJump / CheckWaterJump
    // ===============================================================================================

    /// <summary>
    /// QC <c>CheckPlayerJump</c>: run <see cref="PlayerJump"/>, then decide whether the jetpack thrust is
    /// active this tick (held jump in mid-air with cl_jetpack_jump, or the dedicated jetpack key + fuel).
    /// Also debounces the jump-held flag and triggers water-jump-out checks. Returns true if jetpack is on.
    /// </summary>
    private bool CheckPlayerJump(Entity player, IMovementInput input, in MovementParameters mp, Vector3 move)
    {
        bool jetpackJump = JetpackJumpEnabled; // cl_jetpack_jump mode (1 here); mode 2 would latch the thrust
        // QC: `if (JETPACK_JUMP(this) < 2) UNSET IT_USING_JETPACK;` — mode 1 clears the flag every frame so
        // it must be re-asserted by an active thrust this tick (mode 2 would keep it latched between frames).
        player.UsingJetpack = false;

        bool wantThrust = false;
        if (input.ButtonJump || input.ButtonJetpack)
        {
            (bool playerjump, bool airJumpHint) = PlayerJump(player, input, mp, move);

            bool airJump = !playerjump || airJumpHint;
            bool activate = (jetpackJump && airJump && input.ButtonJump) || input.ButtonJetpack;
            // QC CheckPlayerJump fuel gate uses PHYS_JETPACK_FUEL (read live, same as PM_jetpack).
            float jpFuel = JetpackCvar("g_jetpack_fuel", JetpackFuel);
            bool hasFuel = jpFuel == 0f || player.GetResource(ResourceType.Fuel) > 0f || player.UnlimitedAmmo;

            if (!player.HasJetpack) { /* no jetpack item */ }
            else if (player.JetpackStopped) { /* stopped until released */ }
            else if (!hasFuel)
            {
                player.JetpackStopped = true;
                player.UsingJetpack = false;
            }
            else if (activate && !IsFrozen(player))
            {
                player.UsingJetpack = true;
                wantThrust = true;
            }
        }
        else
        {
            player.JetpackStopped = false;
            player.UsingJetpack = false;
        }

        if (!input.ButtonJump)
            player.Flags |= EntFlags.JumpReleased; // UNSET_JUMP_HELD

        if (player.WaterLevel == WaterLevelSwimming)
            CheckWaterJump(player, input);

        return wantThrust && player.UsingJetpack;
    }

    /// <summary>
    /// QC <c>PlayerJump</c>: the jump itself, with the frozen/typing/water/doublejump guards, the
    /// jumpspeedcap baseline, and the landing friction. Returns (handled, airJumpHint) where airJumpHint
    /// is the (possibly mutator-granted) "this counts as an air jump" flag CheckPlayerJump uses for the
    /// jetpack. Mirrors the QC <c>bool</c> return + <c>M_ARGV(2, doublejump)</c> out value.
    /// </summary>
    private (bool handled, bool airJumpHint) PlayerJump(Entity player, IMovementInput input, in MovementParameters mp, Vector3 move)
    {
        if (IsFrozen(player))
            return (true, false);               // no jumping while frozen
        if (input.Typing)
            return (true, false);               // no jumping while typing
        // QC PlayerJump (common/physics/player.qc:395): if(this.player_blocked) return true — dom roundbased
        // blocks jump for the same window that zeroes movement.
        if (RoundMoveForbidden?.Invoke(player) == true)
            return (true, false);               // no jumping while round-blocked

        // QC PlayerJump (common/physics/player.qc): the air-jump grant starts FALSE — sv_doublejump does NOT
        // pre-grant a free midair re-jump. The doublejump mutator (DoublejumpMutator, port of
        // common/mutators/mutator/doublejump/doublejump.qc) governs it via the PlayerJump hook below: it only
        // grants the extra jump after a tracebox confirms the player is on/just-above a walkable surface, AND it
        // clips velocity into that plane. Default sv_doublejump 0 → the mutator is inert and this stays false,
        // byte-identical to mp.DoubleJump==false (the golden movement-parity traces are unaffected). (T51)
        bool doublejump = false;
        float mjumpheight = (mp.JumpVelocityCrouch != 0f && player.IsDucked) ? mp.JumpVelocityCrouch : mp.JumpVelocity;
        // QC buff/jump.qc PlayerPhysics hook overwrites STAT(MOVEVARS_JUMPVELOCITY) with g_buffs_jump_velocity
        // while the Jump buff is held. BuffsMutator.OnPlayerPhysics (a PlayerPhysics handler that runs at line 196,
        // before this jump) writes that value into player.JumpVelocityOverride (0 = no override). Honor it here —
        // it REPLACES the cvar-built jump velocity (crouch included), exactly like the QC stat overwrite.
        if (player.JumpVelocityOverride != 0f)
            mjumpheight = player.JumpVelocityOverride;
        bool trackJump = mp.TrackCanJump;       // cl_movement_track_canjump (folded into sv track for the headless sim)

        // EV_PlayerJump mutator hook (multijump/walljump grant an extra jump; bloodloss forbids it).
        // Carry the per-player resolved DOUBLEJUMP stat (mp.DoubleJump = Physics_ClientOption("doublejump",
        // sv_doublejump)) so the doublejump mutator gates on PHYS_DOUBLEJUMP(player) per Base, not just the cvar.
        var pj = new MutatorHooks.PlayerJumpArgs(player, mjumpheight, doublejump, mp.DoubleJump)
        {
            // Carry the prediction leg so PlayerJump hooks can gate their #ifdef SVQC side-effects
            // (e.g. walljump's smoke ring / jump voice) off the predicting client — matching Base,
            // where those run only on SVQC while the velocity impulse runs shared on CSQC+SVQC.
            Predicted = input.Predicted,
        };
        if (MutatorHooks.PlayerJump.Call(ref pj))
            return (true, pj.Multijump);
        mjumpheight = pj.JumpHeight;
        doublejump = pj.Multijump;

        // water: jump out of the surface (handled here so it works even at the waterline)
        if (player.WaterLevel >= WaterLevelSwimming)
        {
            if (player.ViewLoc is not null)
            {
                doublejump = true;
                mjumpheight *= 0.7f;
                trackJump = true;
            }
            else
            {
                player.Velocity = new Vector3(player.Velocity.X, player.Velocity.Y, mp.MaxSpeed * 0.7f);
                return (true, false);
            }
        }

        if (!doublejump && (player.Flags & EntFlags.OnGround) == 0)
            return (IsJumpHeld(player), false); // can't jump in the air; report held state

        if (mp.TrackCanJump) trackJump = true;
        if (trackJump && IsJumpHeld(player))
            return (true, doublejump);

        // sv_jumpspeedcap_min / _max baseline velocity bounds.
        if (!float.IsNaN(mp.JumpSpeedCapMin))
        {
            float minjumpspeed = mjumpheight * mp.JumpSpeedCapMin;
            if (player.Velocity.Z < minjumpspeed)
                mjumpheight += minjumpspeed - player.Velocity.Z;
        }
        if (!float.IsNaN(mp.JumpSpeedCapMax))
        {
            bool onRamp = false;
            {
                Vector3 up = player.Origin + new Vector3(0f, 0f, 0.01f);
                Vector3 down = player.Origin - new Vector3(0f, 0f, 0.01f);
                TraceResult tr = Api.Trace.Trace(up, player.Mins, player.Maxs, down, MoveFilter.Normal, player);
                onRamp = tr.Fraction < 1f && tr.PlaneNormal.Z < 0.98f && mp.JumpSpeedCapMaxDisableOnRamps;
            }
            if (!onRamp)
            {
                float maxjumpspeed = mjumpheight * mp.JumpSpeedCapMax;
                if (player.Velocity.Z > maxjumpspeed)
                    mjumpheight -= player.Velocity.Z - maxjumpspeed;
            }
        }

        // landing friction (just landed after >0.3s of airtime, not via ground / slick)
        if (!player.WasOnGround && !WasOnSlick(player))
        {
            if (player.LastGroundTime < Now() - 0.3f)
            {
                float f = QMath.Bound(0f, 1f - mp.FrictionOnLand, 1f);
                Vector3 v = player.Velocity;
                v.X *= f; v.Y *= f;
                player.Velocity = v;
            }
            player.JumpPadCount = 0;
        }

        // apply the jump
        Vector3 vel = player.Velocity;
        vel.Z += mjumpheight;
        player.Velocity = vel;

        // QC plays the jump grunt under #ifdef SVQC (common/physics/player.qc:491-495) — the authoritative
        // server tick only, GATED on autocvar_g_jump_grunt (stock 0 = SILENT). Suppress it on CLIENT-SIDE
        // PREDICTION (and its reconcile replays): the sound is networked from the server, and a predicted jump
        // would re-fire it on every render-frame replay of the in-flight jump input (and a mispredicted jump
        // would play a phantom one). Mirrors the footstep/landing and punch-decay gates (UpdateMovementSounds /
        // CheckPunch) — those #ifdef SVQC cues were already gated.
        //
        // QC: PlayerSound(this, playersound_jump, CH_PLAYER, VOL_BASE, VOICETYPE_PLAYERSOUND, 1) — the per-model
        // jump VOICE, NOT a fixed player/jump.wav. "jump" resolves through the player's model .sounds manifest
        // (LoadPlayerSounds: jump -> sound/player/<pack>/player/jump), falling back to the default pack — same
        // resolution as the wall-jump grunt (WalljumpMutator). VOL_BASE matches Base's PlayerSound volume.
        // (The companion animdecide_setaction(this, ANIMACTION_JUMP, true) at player.qc:492 has no anim-action
        // seam in the port yet — same as the walljump port — so it stays unported.)
        if (!input.Predicted && Api.Services is not null && Api.Cvars.GetFloat("g_jump_grunt") != 0f)
            SoundSystem.PlayPlayerSound(player, "jump", Sounds.ModelSoundsFile(player.Model, (int)player.Skin),
                SoundLevels.VolBase, SoundLevels.AttenNorm);

        player.Flags &= ~EntFlags.OnGround;     // UNSET_ONGROUND
        player.OnSlick = false;                 // UNSET_ONSLICK
        player.GroundEntity = null;
        player.Flags &= ~EntFlags.JumpReleased; // SET_JUMP_HELD
        _ = move;
        return (true, doublejump);
    }

    /// <summary>
    /// QC <c>CheckWaterJump</c>: when swimming and facing a wall at waist height with open space at eye
    /// height, set FL_WATERJUMP and launch the player up + forward out of the water.
    /// </summary>
    private static void CheckWaterJump(Entity player, IMovementInput input)
    {
        QMath.AngleVectors(player.Angles == Vector3.Zero ? input.ViewAngles : player.Angles, out Vector3 forward, out _, out _);
        forward.Z = 0f;
        forward = QMath.Normalize(forward);

        Vector3 start = player.Origin;
        start.Z += 8f;
        Vector3 end = start + forward * 24f;
        TraceResult t = Api.Trace.Trace(start, Vector3.Zero, Vector3.Zero, end, MoveFilter.Normal, player);
        if (t.Fraction < 1f) // solid at waist
        {
            start.Z = start.Z + player.Maxs.Z - 8f;
            end = start + forward * 24f;
            player.MoveDir = t.PlaneNormal * -50f;
            TraceResult t2 = Api.Trace.Trace(start, Vector3.Zero, Vector3.Zero, end, MoveFilter.Normal, player);
            if (t2.Fraction == 1f) // open at eye level
            {
                player.Velocity = new Vector3(player.Velocity.X, player.Velocity.Y, 225f);
                player.Flags |= EntFlags.WaterJump;
                player.TeleportTime = Now() + 2f; // safety net
                player.Flags &= ~EntFlags.JumpReleased; // SET_JUMP_HELD
            }
        }
    }

    // ===============================================================================================
    //  Water / slick / crouch detection
    // ===============================================================================================

    /// <summary>
    /// QC <c>_Movetype_CheckWater</c> (distilled): probe the player's hull midpoint and feet/eye against
    /// the liquid contents to set <see cref="Entity.WaterLevel"/> / <see cref="Entity.WaterType"/>. A
    /// func_water ladder volume overrides this in <see cref="LadderMove"/>.
    /// </summary>
    private static void CheckWater(Entity player)
    {
        // func_water ladder volume drives waterlevel directly (handled in LadderMove); skip the probe.
        if (player.LadderEntity is { ClassName: "func_water" })
            return;

        if (Api.Services is null)
        {
            player.WaterLevel = WaterLevelNone;
            return;
        }

        Vector3 point = player.Origin;
        // feet
        point.Z = player.Origin.Z + player.Mins.Z + 1f;
        int cont = Api.Trace.PointContents(point);
        if ((cont & SuperContentsLiquidsMask) == 0)
        {
            player.WaterLevel = WaterLevelNone;
            player.WaterType = (int)Contents.Empty;
            return;
        }

        player.WaterType = ContentsFromSuper(cont);
        player.WaterLevel = WaterLevelWetFeet;

        // waist
        point.Z = player.Origin.Z + (player.Mins.Z + player.Maxs.Z) * 0.5f;
        if ((Api.Trace.PointContents(point) & SuperContentsLiquidsMask) != 0)
        {
            player.WaterLevel = WaterLevelSwimming;
            // eye / view
            point.Z = player.Origin.Z + player.ViewOfs.Z;
            if ((Api.Trace.PointContents(point) & SuperContentsLiquidsMask) != 0)
                player.WaterLevel = WaterLevelSubmerged;
        }
    }

    /// <summary>QC <c>PM_check_slick</c>: read Q3SURFACEFLAG_SLICK off the surface directly below the player.</summary>
    private static void CheckSlick(Entity player)
    {
        if ((player.Flags & EntFlags.OnGround) == 0)
            return;
        Vector3 down = player.Origin - new Vector3(0f, 0f, 1f);
        TraceResult tr = Api.Trace.Trace(player.Origin, player.Mins, player.Maxs, down, MoveFilter.NoMonsters, player);
        player.OnSlick = (tr.DpHitQ3SurfaceFlags & Q3SurfaceFlagSlick) != 0;
    }

    /// <summary>
    /// QC <c>PM_ClientMovement_UpdateStatus</c> crouch handling: when the crouch input is held (or forced),
    /// shrink the hull and lower the view; when released, un-crouch only if the standing hull fits. Drives
    /// <see cref="Entity.IsDucked"/>. Call from the host's PreThink (kept public so the input layer can run
    /// it before <see cref="Move"/>); also invoked here so the headless sim crouches deterministically.
    /// </summary>
    public static void UpdateCrouch(Entity player, IMovementInput input, in MovementParameters mp)
    {
        if (Api.Services is null)
            return; // crouch needs a trace to stand up safely; skip in headless unit tests w/o a world

        bool doCrouch = input.ButtonCrouch;

        // QC PM_ClientMovement_UpdateStatus:191-193 — a viewloc (1D side-scroller rail) without the FREEMOVE
        // spawnflag FORCES crouch while pressing "down" (movement.x < 0). Inert on stock 3D maps (ViewLoc is
        // always null), but a real Base branch. VIEWLOC_FREEMOVE = BIT(2) = 4 (viewloc.qh).
        if (player.ViewLoc is { } vloc && (vloc.SpawnFlags & 4) == 0 && input.MoveValues.X < 0f)
            doCrouch = true;

        // QC PM_ClientMovement_UpdateStatus:194-201 — the force-UNCROUCH chain, in QC's exact precedence
        // (each an `else if`, so the FIRST matching condition wins and the rest are skipped):
        //   have_hook → in-vehicle → (frozen || dead).
        // have_hook: a grappling hook held in ANY weapon slot forces standing (so a hooked player isn't stuck
        // ducked). QC loops weaponentities[slot] and checks .hook; the port stores the hook on the per-slot
        // WeaponSlotState, so walk the populated slots.
        bool haveHook = false;
        player.ForEachWeaponSlot(s => { if (s.Hook is not null) haveHook = true; });

        if (haveHook)
            doCrouch = false;
        else if (player.Vehicle is not null) // QC PHYS_INVEHICLE(this) == (this.vehicle != NULL)
            doCrouch = false;
        else if (IsFrozen(player) || (player.DeadState != DeadFlag.No))
            doCrouch = false;

        // EV_PlayerCanCrouch mutator hook (bloodloss forces crouch below its threshold).
        var cc = new MutatorHooks.PlayerCanCrouchArgs(player, doCrouch);
        MutatorHooks.PlayerCanCrouch.Call(ref cc);
        doCrouch = cc.DoCrouch;

        // QC PM_ClientMovement_UpdateStatus:206-208 — disable crouching on Q1BSP, which lacks a suitable
        // cliphull (the crouch hull would equal the standing hull). Base tests STAT(PL_CROUCH_MAX).z ==
        // STAT(PL_MAX).z. Inert on stock Xonotic maps (Q3BSP: crouch maxs.z 25 != standing maxs.z 45), but a
        // real Base branch — reproduced for fidelity on a hypothetical Q1BSP map / a server retuning the hulls.
        if (CrouchMaxs.Z == mp.PlayerMaxs.Z)
            doCrouch = false;

        if (doCrouch)
        {
            if (!player.IsDucked)
            {
                player.IsDucked = true;
                player.Mins = CrouchMins;
                player.Maxs = CrouchMaxs;
                player.ViewOfs = CrouchViewOfs; // QC: this.view_ofs = STAT(PL_CROUCH_VIEW_OFS) — lower the eye
            }
        }
        else if (player.IsDucked)
        {
            // only stand up if the standing hull isn't blocked
            TraceResult tr = Api.Trace.Trace(player.Origin, mp.PlayerMins, mp.PlayerMaxs, player.Origin, MoveFilter.Normal, player);
            if (!tr.StartSolid)
            {
                player.IsDucked = false;
                player.Mins = mp.PlayerMins;
                player.Maxs = mp.PlayerMaxs;
                // QC: this.view_ofs = STAT(PL_VIEW_OFS). A dead+ducked player still reaches this un-crouch (as in
                // QC, do_crouch is forced false when dead), but the death code owns the eye (DamageSystem sets a
                // view-from-the-floor offset), so only restore the standing eye for a live player.
                if (player.DeadState == DeadFlag.No)
                    player.ViewOfs = StandViewOfs;
            }
        }
    }

    // ===============================================================================================
    //  Onground detection
    // ===============================================================================================
    private static bool DetermineOnGround(Entity player, in MovementParameters mp)
    {
        // QC sys_phys_update reads the PERSISTENT FL_ONGROUND flag to pick the ground vs air branch — it does
        // NOT re-detect the floor here. The flag is maintained by the previous tick's WalkMove (the floor-hit
        // in _Movetype_FlyMove + the DOWNTRACEONGROUND re-acquire) and is cleared by PlayerJump. An extra
        // down-trace here would wrongly resurrect on-ground on the jump tick (PlayerJump cleared the flag but
        // the player hasn't physically left the floor yet), forcing the ground branch and breaking jumps /
        // bunnyhopping — see the forward_jump_arc / bunnyhop golden traces.
        return (player.Flags & EntFlags.OnGround) != 0;
    }

    // ===============================================================================================
    //  Integration for the gravity-free branches (water/ladder/jetpack/fly/noclip)
    // ===============================================================================================
    private void Integrate(Entity player, in MovementParameters mp, float dt, Vector3 viewAngles, bool applyGravity, bool noCollide)
    {
        if (noCollide)
        {
            // MOVETYPE_NOCLIP: free flight, no collision.
            player.Origin += player.Velocity * dt;
            return;
        }
        // Slide-and-step move (no gravity here; the branch already integrated buoyancy/thrust).
        WalkMove(player, mp, dt, applyGravity, viewAngles);
    }

    // ===============================================================================================
    //  SV_NudgeOutOfSolid (sv_phys.c) — recover an entity that starts a move embedded in solid.
    // ===============================================================================================

    /// <summary>How far (units) we'll try to push a stuck player out of solid — a little over a step height.
    /// A deeper embed than this is left as-is (the move treats it as blocked) rather than teleported far.</summary>
    private const float MaxNudge = 38f;

    /// <summary>
    /// If <paramref name="player"/> begins embedded in solid (a zero-extent box trace at its origin reports
    /// startsolid), search nearby for the closest free position and move it there — the C# successor to DP's
    /// <c>SV_NudgeOutOfSolid</c>. Candidate directions are tried in priority order — straight UP first (so the
    /// player ends standing on the surface it was stuck in), then the four cardinals, then down — at increasing
    /// distances, so the first free spot found is the minimal displacement (a "could have just moved" nudge, not
    /// a respawn). <see cref="MoveFilter.NoMonsters"/> so we push out of the world / brush models, never off
    /// other players. Cheap when not stuck: a single trace, then return.
    /// </summary>
    private static void NudgeOutOfSolid(Entity player)
    {
        if (!IsStuckAt(player, player.Origin))
            return;

        Vector3 origin = player.Origin;
        ReadOnlySpan<Vector3> dirs = stackalloc Vector3[]
        {
            new(0f, 0f, 1f),                                                 // up — prefer standing on top
            new(1f, 0f, 0f), new(-1f, 0f, 0f), new(0f, 1f, 0f), new(0f, -1f, 0f),
            new(0f, 0f, -1f),                                                // down — last resort
        };

        for (float dist = 1f; dist <= MaxNudge; dist += dist < 8f ? 1f : 4f)
        {
            for (int d = 0; d < dirs.Length; d++)
            {
                Vector3 cand = origin + dirs[d] * dist;
                if (!IsStuckAt(player, cand))
                {
                    player.Origin = cand;
                    if (Log.WillTrace)
                        Log.Trace($"[nudge] freed stuck player: {origin} -> {cand} (+{dirs[d] * dist})");
                    return;
                }
            }
        }

        // No free spot within MaxNudge — leave the player put; the slide-move will report it blocked.
        Log.Trace($"[nudge] could NOT free stuck player at {origin} (mins {player.Mins}, maxs {player.Maxs})");
    }

    /// <summary>True when the player's hull is embedded in solid at <paramref name="pos"/> (a zero-extent box
    /// trace there reports startsolid). Uses MOVE_NOMONSTERS so only the world / brush models count.</summary>
    private static bool IsStuckAt(Entity player, Vector3 pos)
        => Api.Trace.Trace(pos, player.Mins, player.Maxs, pos, MoveFilter.NoMonsters, player).StartSolid;

    // ===============================================================================================
    //  WalkMove / FlyMove — SV_WalkMove + SV_FlyMove slide-and-step (movetypes.qc / walk.qc)
    // ===============================================================================================
    private void WalkMove(Entity player, in MovementParameters mp, float dt, bool applyGravity, Vector3 viewAngles = default)
    {
        bool oldOnGround = (player.Flags & EntFlags.OnGround) != 0;
        Vector3 startOrigin = player.Origin;
        Vector3 startVelocity = player.Velocity;

        // Primary slide (with immediate stair-stepping when stepmultipletimes is on). steppedUp latches if the
        // in-loop stair-step actually lifted the hull over an obstacle this tick (for the step-up velocity clamp).
        int clip = FlyMove(player, mp, dt, applyGravity, StepMultipleTimes ? mp.StepHeight : 0f, out Vector3 stepNormal, out bool steppedUp);

        // DOWNTRACEONGROUND: re-acquire the floor if the slide didn't register one (walk.qc:30-55). NOTE:
        // this only KEEPS an existing on-ground state alive (clip |= 1 suppresses the clear below) and
        // refreshes GroundEntity for mover carrying — it must never SET the flag. On-ground is granted
        // exclusively by a real floor collision (FlyMove, which also clips velocity.z into the plane) or by
        // the stepdown==2 path, so "OnGround ⇒ velocity.z was ground-clipped" holds — the ADDITIVE
        // PlayerJump (velocity_z += jumpvelocity) depends on that invariant. Granting it here marked a
        // falling player on-ground with velocity.z ≈ -270 un-clipped whenever a landing tick ended within
        // 1u above the floor, and the held bunnyhop jump came out dead/stunted (JumpGrantReproTests pins
        // both paths).
        if (DownTraceOnGround && (clip & 1) == 0)
        {
            Vector3 up = player.Origin + new Vector3(0f, 0f, 1f);
            Vector3 down = player.Origin - new Vector3(0f, 0f, 1f);
            TraceResult tr = Api.Trace.Trace(up, player.Mins, player.Maxs, down, MoveFilter.Normal, player);
            if (tr.Fraction < 1f && tr.PlaneNormal.Z > OnGroundNormalZ)
            {
                clip |= 1;
                player.GroundEntity = tr.Ent;
            }
        }

        // if the move did not hit the ground at any point, we're not on ground (walk.qc:57-58 — clear-only;
        // Base's `else` arm there is the dead sv_wallclip pm_time branch, not a SET_ONGROUND).
        if ((clip & 1) == 0)
            player.Flags &= ~EntFlags.OnGround;

        if ((clip & 8) != 0) // teleport
            return;
        if ((player.Flags & EntFlags.WaterJump) != 0)
            return;

        // Port extension: the primary slide stepped UP over an obstacle this tick (in-loop stair-step). The step
        // is purely positional and preserves velocity, so a player who jumped into the step keeps full upward
        // velocity and "launches" to step+jump height. Optionally scale/cap that carried upward velocity. No-op at
        // the stock defaults (scale 1, max -1). Applied here so the snapshot below captures the clamped velocity
        // (a stair-recovery revert then restores the clamped value, keeping the tick self-consistent).
        if (steppedUp)
            ApplyStepUpSpeedClamp(player, mp);

        // ===== SV_WalkMove stair recovery (walk.qc ~80) =====
        Vector3 originalOrigin = player.Origin;
        Vector3 originalVelocity = player.Velocity;
        EntFlags originalFlags = player.Flags;
        Entity? originalGround = player.GroundEntity;

        bool blockedStep = (clip & 2) != 0;
        if (blockedStep)
        {
            // if not actually trying to move into the step, return
            if (MathF.Abs(startVelocity.X) < 0.03125f && MathF.Abs(startVelocity.Y) < 0.03125f)
                return;

            // return if attempting to jump while airborne (Quake2 double-jump bug guard), unless sv_jumpstep
            if (!mp.JumpStep && !oldOnGround && player.WaterLevel == 0)
                return;

            // try moving up and forward to go up a step (back to start pos first)
            player.Origin = startOrigin;
            player.Velocity = startVelocity;

            Vector3 upmove = new(0f, 0f, mp.StepHeight);
            if (!PushEntity(player, upmove, out _))
                return; // teleported on the up-step

            // move forward with z velocity zeroed, then restore it
            player.Velocity = new Vector3(player.Velocity.X, player.Velocity.Y, 0f);
            int clip2 = FlyMove(player, mp, dt, applyGravity, 0f, out stepNormal, out _);
            player.Velocity = new Vector3(player.Velocity.X, player.Velocity.Y, player.Velocity.Z + startVelocity.Z);
            if ((clip2 & 8) != 0)
                return; // teleported on the forward move

            // check for stuckness (limited cliphull precision): if no horizontal progress, revert.
            if (clip2 != 0
                && MathF.Abs(originalOrigin.Y - player.Origin.Y) < 0.03125f
                && MathF.Abs(originalOrigin.X - player.Origin.X) < 0.03125f)
            {
                player.Origin = originalOrigin;
                player.Velocity = originalVelocity;
                player.Flags = originalFlags;
                player.GroundEntity = originalGround;
                return;
            }

            // extra friction based on view angle (SV_WallFriction) when still blocked on a wall.
            // Mirrors QC walk.qc:146 — sv_wallfriction is 1 in stock so this gate is live, but WallFriction()
            // is a deliberate no-op because the stock QC _Movetype_WallFriction body is commented out.
            if ((clip2 & 2) != 0 && mp.WallFriction != 0)
                WallFriction(player, viewAngles, stepNormal);

            // Port extension: this explicit up/forward step just RE-ADDED start_velocity.z (line above), so it is the
            // other place a step-up carries upward velocity through. Clamp it the same way (no-op at stock defaults).
            ApplyStepUpSpeedClamp(player, mp);
            return;
        }

        // ===== step-down (walk.qc tail): keep the player glued to the ground walking down stairs/slopes =====
        bool skip =
            mp.StepDown == 0
            || player.WaterLevel >= WaterLevelSubmerged
            || startVelocity.Z >= (1f / 32f)
            || !oldOnGround
            || (player.Flags & EntFlags.OnGround) != 0
            || (mp.StepDownMaxSpeed != 0f && startVelocity.Length() >= mp.StepDownMaxSpeed && !player.OnSlick);
        if (skip)
            return;

        Vector3 downmove = new(0f, 0f, -mp.StepHeight + startVelocity.Z * dt);
        if (!PushEntity(player, downmove, out TraceResult dtr))
            return; // teleported on the down-step

        if (dtr.Fraction < 1f && dtr.PlaneNormal.Z > OnGroundNormalZ)
        {
            if (mp.StepDown == 2)
            {
                player.Flags |= EntFlags.OnGround;
                player.GroundEntity = dtr.Ent;
            }
        }
        else
        {
            // didn't land on good ground; undo the step-down (avoids hopping up too-steep slopes)
            player.Origin = originalOrigin;
            player.Velocity = originalVelocity;
            player.Flags = originalFlags;
            player.GroundEntity = originalGround;
        }
    }

    /// <summary>
    /// QC <c>_Movetype_WallFriction</c> (SV_WallFriction). Originally scaled the wall-parallel velocity by
    /// <c>d = (stepnormal · v_forward) + 0.5</c> (only when <c>d &lt; 0</c>) while pressed against a wall the
    /// player faces. <b>Intentional no-op:</b> the body is commented out in the stock Xonotic QC
    /// (common/physics/movetypes/movetypes.qc), so although <c>sv_wallfriction</c> is 1 in stock (the
    /// Darkplaces engine default — no Xonotic .cfg sets it) and the call gate is therefore live, the term
    /// never modifies velocity in stock play. We mirror that exactly: keep the cvar at its true value and the
    /// gate live, but leave the math disabled here. Sibling no-op: <c>MoveTypePhysics.WallFriction</c>.
    /// </summary>
    private static void WallFriction(Entity player, Vector3 viewAngles, Vector3 stepNormal)
    {
        // No-op, matching the commented-out stock QC body. Ported math preserved below — re-enable ONLY to
        // follow a (modded) server that un-comments _Movetype_WallFriction; doing so diverges from stock:
        //   if (stepNormal == Vector3.Zero) return;
        //   QMath.AngleVectors(viewAngles, out Vector3 forward, out _, out _);
        //   float d = QMath.Dot(stepNormal, forward) + 0.5f;
        //   if (d < 0f)
        //   {
        //       float i = QMath.Dot(stepNormal, player.Velocity);
        //       Vector3 into = i * stepNormal;
        //       Vector3 side = player.Velocity - into;
        //       player.Velocity = new Vector3(side.X * d, side.Y * d, player.Velocity.Z); // QC: vel_{x,y} *= d, z untouched
        //   }
        _ = player; _ = viewAngles; _ = stepNormal;
    }

    /// <summary>
    /// Port extension (NOT in stock Xonotic): scale and/or hard-cap the UPWARD (positive) <c>velocity.z</c> a
    /// player carries THROUGH a step-up this tick. Stock step-up is purely positional — it lifts the hull up to
    /// <see cref="MovementParameters.StepHeight"/> and preserves velocity — so jumping into a stair/bump keeps the
    /// full jump velocity and "launches" you to step+jump height. <c>sv_step_upspeed_scale</c> (default 1) multiplies
    /// that surviving upward velocity; <c>sv_step_upspeed_max</c> (default -1 = off) caps it. The positional lift is
    /// untouched, so stair TRAVERSAL is unchanged — only the residual launch velocity is tamed. A no-op at the stock
    /// defaults (and whenever velocity.z &lt;= 0), so walking up stairs — velocity.z == 0, see the stair_step_up
    /// golden trace — and all non-stepping motion are byte-identical to before. Called from <see cref="WalkMove"/>
    /// at the two points a step-up carries vertical velocity (the in-loop primary step and the explicit recovery).
    /// </summary>
    private static void ApplyStepUpSpeedClamp(Entity player, in MovementParameters mp)
    {
        float vz = player.Velocity.Z;
        if (vz <= 0f)
            return; // only limit UPWARD launch; never add downward pull or touch a descending/level player

        float clamped = vz;
        if (mp.StepUpSpeedScale != 1f)
            clamped *= mp.StepUpSpeedScale;
        if (mp.StepUpSpeedMax >= 0f && clamped > mp.StepUpSpeedMax)
            clamped = mp.StepUpSpeedMax;

        if (clamped != vz)
            player.Velocity = new Vector3(player.Velocity.X, player.Velocity.Y, clamped);
    }

    /// <summary>
    /// SV_FlyMove: gravity half-step, then up to <see cref="MaxClipPlanes"/> trace-and-slide iterations
    /// with crease handling, plus immediate stair-step when <paramref name="stepheight"/> &gt; 0. Returns
    /// the QC blocked-flag bitmask (bit0=floor, bit1=wall/step-out, bit3=teleported);
    /// <paramref name="stepNormal"/> receives the wall plane normal on a step (for wall friction).
    /// </summary>
    private int FlyMove(Entity player, in MovementParameters mp, float dt, bool applyGravity, float stepheight,
        out Vector3 stepNormal, out bool steppedUp)
    {
        stepNormal = Vector3.Zero;
        steppedUp = false; // latches when the in-loop stair-step lifts the hull over an obstacle (for the up-speed clamp)
        if (dt <= 0f)
            return 0;

        int blockedflag = 0;
        int numplanes = 0;
        float timeLeft = dt;
        float grav = 0f;

        Vector3 restoreVelocity = player.Velocity;
        Span<Vector3> planes = stackalloc Vector3[MaxClipPlanes];

        if (applyGravity)
        {
            grav = ApplyEntGravity(player, mp) * dt;
            // QC: if(!GAMEPLAYFIX_NOGRAVITYONGROUND || !IS_ONGROUND(this)) — skip gravity entirely on ground.
            if (!NoGravityOnGround || (player.Flags & EntFlags.OnGround) == 0)
            {
                if (GravityUnaffectedByTicrate)
                    player.Velocity = new Vector3(player.Velocity.X, player.Velocity.Y, player.Velocity.Z - grav * 0.5f);
                else
                    player.Velocity = new Vector3(player.Velocity.X, player.Velocity.Y, player.Velocity.Z - grav);
            }
        }

        Vector3 originalVelocity = player.Velocity;
        Vector3 primalVelocity = player.Velocity;

        for (int bump = 0; bump < MaxClipPlanes; ++bump)
        {
            if (player.Velocity == Vector3.Zero)
                break;

            Vector3 push = player.Velocity * timeLeft;
            if (!PushEntity(player, push, out TraceResult trace))
            {
                blockedflag |= 8; // teleported by a touch
                break;
            }

            if (trace.StartSolid && trace.AllSolid)
            {
                player.Velocity = restoreVelocity;
                return 3;
            }

            if (trace.Fraction == 1f)
                break;

            timeLeft *= 1f - trace.Fraction;

            float myFraction = trace.Fraction;
            Vector3 myPlaneNormal = trace.PlaneNormal;

            if (myPlaneNormal.Z != 0f)
            {
                if (myPlaneNormal.Z > OnGroundNormalZ)
                {
                    blockedflag |= 1;
                    player.Flags |= EntFlags.OnGround;
                    player.GroundEntity = trace.Ent;
                }
            }
            else if (stepheight != 0f)
            {
                // immediate stair-step: up, forward, back down
                Vector3 org = player.Origin;
                Vector3 steppush = new(0f, 0f, stepheight);
                push = player.Velocity * timeLeft;

                if (!PushEntity(player, steppush, out _)) { blockedflag |= 8; break; }
                if (!PushEntity(player, push, out TraceResult fwd)) { blockedflag |= 8; break; }
                float trace2Fraction = fwd.Fraction;
                steppush = new Vector3(0f, 0f, org.Z - player.Origin.Z);
                if (!PushEntity(player, steppush, out _)) { blockedflag |= 8; break; }

                if (player.Origin.X - org.X != 0f || player.Origin.Y - org.Y != 0f)
                {
                    timeLeft *= 1f - trace2Fraction;
                    numplanes = 0;
                    steppedUp = true; // a stair-step over an obstacle was accepted this tick
                    continue;
                }
                else
                {
                    player.Origin = org;
                    blockedflag |= 2;
                    stepNormal = myPlaneNormal;
                }
            }
            else
            {
                blockedflag |= 2; // wall/step returned to caller
                stepNormal = myPlaneNormal;
            }

            if (myFraction >= 0.001f)
            {
                originalVelocity = player.Velocity;
                numplanes = 0;
            }

            if (numplanes >= MaxClipPlanes)
            {
                player.Velocity = Vector3.Zero;
                return 3;
            }

            planes[numplanes] = myPlaneNormal;
            ++numplanes;

            // make velocity parallel to all clip planes
            Vector3 newVelocity = Vector3.Zero;
            int plane;
            for (plane = 0; plane < numplanes; ++plane)
            {
                newVelocity = ClipVelocity(originalVelocity, planes[plane], 1f);
                int newplane;
                for (newplane = 0; newplane < numplanes; ++newplane)
                {
                    if (newplane != plane && QMath.Dot(newVelocity, planes[newplane]) < 0f)
                        break;
                }
                if (newplane == numplanes)
                    break;
            }

            if (plane != numplanes)
            {
                player.Velocity = newVelocity;
            }
            else
            {
                if (numplanes != 2)
                {
                    player.Velocity = Vector3.Zero;
                    return 7;
                }
                Vector3 dir = QMath.Normalize(QMath.Cross(planes[0], planes[1]));
                float d = QMath.Dot(dir, player.Velocity);
                player.Velocity = dir * d;
            }

            if (QMath.Dot(player.Velocity, primalVelocity) <= 0f)
            {
                player.Velocity = Vector3.Zero;
                break;
            }
        }

        if (applyGravity && GravityUnaffectedByTicrate
            && (!NoGravityOnGround || (player.Flags & EntFlags.OnGround) == 0))
            player.Velocity = new Vector3(player.Velocity.X, player.Velocity.Y, player.Velocity.Z - grav * 0.5f);

        return blockedflag;
    }

    /// <summary>
    /// SV_PushEntity: sweep the player's hull along <paramref name="push"/> via <see cref="Api.Trace"/>,
    /// move to the trace endpoint, dispatch the blocking touch, and report the trace. Returns false only
    /// when a touch teleported the player (modelled via <see cref="Entity.TeleportTime"/> bumping).
    /// </summary>
    private static bool PushEntity(Entity player, Vector3 push, out TraceResult trace)
    {
        Vector3 start = player.Origin;
        Vector3 end = start + push;
        trace = Api.Trace.Trace(start, player.Mins, player.Maxs, end, MoveFilter.Normal, player);

        if (trace.StartSolid)
        {
            // QC workaround: retry world-only; if still stuck, fraction 0 and report blocked.
            TraceResult worldTrace = Api.Trace.Trace(start, player.Mins, player.Maxs, end, MoveFilter.WorldOnly, player);
            if (worldTrace.StartSolid)
            {
                trace.Fraction = 0f;
                return true; // not a teleport; caller checks StartSolid/AllSolid
            }
            trace = worldTrace;
        }

        float teleBefore = player.TeleportTime;
        player.Origin = trace.EndPos;

        // SV_Impact: fire the blocking entity's touch (and the player's), as the slide-move expects.
        if (trace.Ent is { } hit && !hit.IsFreed)
        {
            if (player.Touch is { } pt && player.Solid != Solid.Not)
                pt(player, hit);
            if (!player.IsFreed && !hit.IsFreed && hit.Touch is { } ht && hit.Solid != Solid.Not)
                ht(hit, player);
        }

        // a touch function may have teleported the player (setorigin during touch) -> abort the move.
        if (player.TeleportTime != teleBefore)
            return false;
        return true;
    }

    /// <summary>QC <c>_Movetype_ClipVelocity</c> with the 0.1 STOP_EPSILON snap per axis.</summary>
    private static Vector3 ClipVelocity(Vector3 vel, Vector3 normal, float overbounce)
    {
        vel -= QMath.Dot(vel, normal) * normal * overbounce;
        if (vel.X is > -0.1f and < 0.1f) vel.X = 0f;
        if (vel.Y is > -0.1f and < 0.1f) vel.Y = 0f;
        if (vel.Z is > -0.1f and < 0.1f) vel.Z = 0f;
        return vel;
    }

    // ===============================================================================================
    //  small helpers
    // ===============================================================================================
    private static (Vector3 dir, float speed) WishDir2D(Vector3 viewAngles, Vector3 move)
    {
        Vector3 yawAngles = new(0f, viewAngles.Y, 0f);
        QMath.AngleVectors(yawAngles, out Vector3 forward, out Vector3 right, out _);
        Vector3 wishvel = forward * move.X + right * move.Y; // Z handled by gravity/jump
        float wishspeed = wishvel.Length();
        Vector3 wishdir = wishspeed != 0f ? wishvel * (1f / wishspeed) : Vector3.Zero;
        return (wishdir, wishspeed);
    }

    private static float ApplyEntGravity(Entity player, in MovementParameters mp)
        => (player.Gravity != 0f ? player.Gravity : 1f) * mp.Gravity;

    private static void UnsetOnGround(Entity player) => player.Flags &= ~EntFlags.OnGround;

    private static bool IsJumpHeld(Entity player) => (player.Flags & EntFlags.JumpReleased) == 0;
    private static bool WasOnSlick(Entity player) => player.OnSlick; // FL_ONSLICK tracked as a bool

    /// <summary>QC PHYS_FROZEN(this): the gametype freeze stat OR the STATUSEFFECT_Frozen effect.</summary>
    private static bool IsFrozen(Entity player)
    {
        if (player.FrozenStat != 0)
            return true;
        var f = Gameplay.StatusEffectsCatalog.Frozen;
        return f is not null && Gameplay.StatusEffectsCatalog.Has(player, f);
    }

    private static int ContentsFromSuper(int superContents)
    {
        if ((superContents & SuperContentsLava) != 0) return (int)Contents.Lava;
        if ((superContents & SuperContentsSlime) != 0) return (int)Contents.Slime;
        if ((superContents & SuperContentsWater) != 0) return (int)Contents.Water;
        return (int)Contents.Empty;
    }

    // --- view punch decay (QC PM_check_punch, common/physics/player.qc) ---

    /// <summary>
    /// Port of <c>PM_check_punch</c> (common/physics/player.qc, <c>#ifdef SVQC</c>): linearly bleed the view
    /// punch toward zero each authoritative tick — <c>punchangle</c> by 10°/s, <c>punchvector</c> by 30 u/s,
    /// preserving direction (magnitude − rate·dt, re-normalized). The punch is SET on firing
    /// (<see cref="WeaponFiring.SetupShot"/>'s recoil → <c>punchangle.x = -recoil</c>) and networked to the
    /// owner, which adds it to the rendered view angles. Server-authoritative only: suppressed on client
    /// prediction replays (the value is networked, not predicted) so it isn't double-decayed.
    /// </summary>
    private static void CheckPunch(Entity player, float dt, bool predicted)
    {
        if (predicted)
            return;
        Vector3 pa = player.PunchAngle;
        if (pa != Vector3.Zero)
        {
            float f = pa.Length() - 10f * dt;
            player.PunchAngle = f > 0f ? Vector3.Normalize(pa) * f : Vector3.Zero;
        }
        Vector3 pv = player.PunchVector;
        if (pv != Vector3.Zero)
        {
            float f = pv.Length() - 30f * dt;
            player.PunchVector = f > 0f ? Vector3.Normalize(pv) * f : Vector3.Zero;
        }
    }

    // --- movement sounds (QC PM_check_hitground / footsteps in sys_phys_postupdate) ---

    private static void UpdateMovementSounds(Entity player, in MovementParameters mp, bool predicted, float priorLastGroundTime)
    {
        if (Api.Services is null)
            return;
        // QC plays footsteps/landing only under #ifdef SVQC — the authoritative server tick. Suppress them on
        // client-side prediction (and its reconciliation replays) so a predicted landing doesn't double up with
        // the networked one and replays don't multiply the sound. WasFlying/footstep timing is then owned solely
        // by the authoritative sim (the server entity), which is the one that actually emits the cue.
        if (predicted)
            return;
        if (player is Player { IsObserver: true })
            return;
        if (player.DeadState != DeadFlag.No)
            return;

        // Mirror QC ecs/systems/physics.qc:86-96. On the ground: maybe play the landing sound, then footsteps.
        // While genuinely airborne (real clearance below — IsFlying): latch WasFlying. The landing sound is gated
        // on WasFlying, NOT on a raw "was-airborne→on-ground" edge: OnGround flickers on stairs, slopes and small
        // bumps, which made the old edge re-fire the FALL sound every few frames (the landing-spam bug). WasFlying
        // only latches after an actual fall, so FALL now plays exactly once per landing.
        if (player.OnGround)
        {
            CheckHitground(player);
            Footsteps(player, mp, priorLastGroundTime);
        }
        else if (IsFlying(player))
        {
            player.WasFlying = true;
        }
    }

    /// <summary>QC <c>PM_check_hitground</c> (player.qc:664): on a real landing (<see cref="Entity.WasFlying"/>
    /// latched by <see cref="IsFlying"/>) play the FALL sound ONCE and throttle the next footstep. Emitted on
    /// CH_PLAYER (auto) so it neither cuts off nor is cut off by a following footstep; muffled while ducked.</summary>
    private static void CheckHitground(Entity player)
    {
        if (!player.WasFlying)
            return;
        player.WasFlying = false;
        if (player.WaterLevel >= WaterLevelSwimming)
            return;
        // QC PM_check_hitground bails on a ladder or while any weapon slot holds an active grappling hook
        // (player.qc:669-676): a hooked player swinging in to a surface shouldn't trigger the landing thud.
        // The ladder is modelled via player.LadderEntity; the hook is the per-slot WeaponSlotState.Hook (the
        // same field UpdateCrouch's have_hook reads), so walk the populated slots like the QC loop.
        if (player.LadderEntity is not null)
            return;
        bool haveHook = false;
        player.ForEachWeaponSlot(s => { if (s.Hook is not null) haveHook = true; });
        if (haveHook)
            return;
        // QC nextstep = time + 0.3 + random()*0.1: jittered suppression window after landing.
        player.LastFootstepTime = Now() + FootstepIntervalBase + FootstepJitter();
        if (TraceSteps(player, out bool metal))
        {
            float vol = player.IsDucked ? SoundLevels.VolMuffled : SoundLevels.VolBase;
            PlayMovementSound(player, metal ? "FALL_METAL" : "FALL", vol);
        }
    }

    /// <summary>QC <c>PM_Footsteps</c> (player.qc:689): periodic footsteps while moving on the ground, throttled
    /// to <see cref="FootstepIntervalBase"/> (jittered) and gated on speed + g_footsteps. Silent while ducked.
    /// Emitted on CH_PLAYER (auto) so consecutive steps stack rather than cancel each other.</summary>
    private static void Footsteps(Entity player, in MovementParameters mp, float priorLastGroundTime)
    {
        // QC: if (!autocvar_g_footsteps) return; — server-side master gate (xonotic-server.cfg:306 default 1).
        // Read raw (treat unset as the stock 1, so an absent cvar keeps footsteps on as in Base).
        if (Api.Cvars.GetString("g_footsteps") == "0")
            return;
        if (player.IsDucked)
            return;
        float now = Now();
        // QC: if (time >= this.lastground + 0.2) return; — only within 0.2s of the last grounded tick. OnGround
        // flickers on stairs/slopes so this gates footsteps to genuinely-grounded motion. Base reads the
        // PRIOR-tick lastground (PM_Footsteps runs before sys_phys_postupdate writes lastground=time), so the
        // port feeds the captured prior value here instead of the now-updated player.LastGroundTime — without
        // this, the gate would read `now` (set this same tick) and never fire (the inert-gate bug).
        if (now >= priorLastGroundTime + 0.2f)
            return;
        float speed2 = Vec2LenSq(player.Velocity);
        float threshold = mp.MaxSpeed * FootstepSpeedThreshold;
        if (speed2 < threshold * threshold)
            return;
        // QC nextstep window: (time > nextstep) || (time < nextstep - 10). LastFootstepTime mirrors .nextstep
        // (a FUTURE time). The second disjunct re-arms a clock that jumped far backwards (round reset / new map).
        if (!(now > player.LastFootstepTime || now < player.LastFootstepTime - 10f))
            return;
        // QC nextstep = time + 0.3 + random()*0.1 (jittered cadence; audio-only, server-local RNG).
        player.LastFootstepTime = now + FootstepIntervalBase + FootstepJitter();
        if (TraceSteps(player, out bool metal))
            PlayMovementSound(player, metal ? "STEP_METAL" : "STEP", SoundLevels.VolBase);
    }

    /// <summary>QC <c>random() * 0.1</c> footstep-cadence jitter — cosmetic, server-only, so drawn from a
    /// dedicated RNG that does NOT advance the shared deterministic <see cref="Prandom"/> stream.</summary>
    private static float FootstepJitter() => (float)FootstepJitterRng.NextDouble() * FootstepIntervalJitter;

    /// <summary>Downward step-surface probe (QC <c>tracebox origin → origin-'0 0 1'</c>): false when the surface
    /// is NOSTEPS (play nothing); otherwise reports via <paramref name="metal"/> whether it is a METALSTEPS surface.</summary>
    private static bool TraceSteps(Entity player, out bool metal)
    {
        Vector3 down = player.Origin - new Vector3(0f, 0f, 1f);
        TraceResult tr = Api.Trace.Trace(player.Origin, player.Mins, player.Maxs, down, MoveFilter.NoMonsters, player);
        metal = (tr.DpHitQ3SurfaceFlags & Q3SurfaceFlagMetalSteps) != 0;
        return (tr.DpHitQ3SurfaceFlags & Q3SurfaceFlagNoSteps) == 0;
    }

    /// <summary>Emit a movement-sound variant (STEP/FALL/…) on CH_PLAYER (auto, so plays stack) at ATTEN_NORM —
    /// QC <c>GlobalSound(this, gs, CH_PLAYER, …)</c>. No-op when the group isn't registered.</summary>
    private static void PlayMovementSound(Entity player, string group, float volume)
    {
        GameSound? snd = Sounds.ByName(group);
        if (snd is null)
            return;
        Api.Sound.Play(player, SoundChannel.PlayerAuto,
            SoundVariantGroups.ResolveGlobalSample(snd), volume, SoundLevels.AttenNorm);
    }

    /// <summary>QC <c>bool IsFlying</c> (player.qc:843): airborne, not swimming, and with &gt;24u of clearance
    /// directly below. Latches <see cref="Entity.WasFlying"/> so the landing sound fires only after a genuine
    /// fall. Distinct from the EV_IsFlying mutator hook (<see cref="CallIsFlying"/>), which forces the fly
    /// movement branch.</summary>
    private static bool IsFlying(Entity player)
    {
        if (player.OnGround)
            return false;
        if (player.WaterLevel >= WaterLevelSwimming)
            return false;
        Vector3 end = player.Origin - new Vector3(0f, 0f, 24f);
        TraceResult tr = Api.Trace.Trace(player.Origin, player.Mins, player.Maxs, end, MoveFilter.Normal, player);
        return tr.Fraction >= 1f;
    }

    private static float Now() => Api.Services is not null ? Api.Clock.Time : 0f;

    // cl_jetpack_jump: in the headless sim there's no per-client cvar; default ON so a held jump in the air
    // activates the jetpack (Xonotic's default cl_jetpack_jump is 1). Hosts can gate via ButtonJetpack.
    private const bool JetpackJumpEnabled = true;

    private static float Vec2LenSq(Vector3 v) => v.X * v.X + v.Y * v.Y;
}

// =================================================================================================
// Installation (wired by the lead in GameInit.InstallGameplaySystems):
//
//     Movement.System = new PlayerPhysics();
// =================================================================================================
