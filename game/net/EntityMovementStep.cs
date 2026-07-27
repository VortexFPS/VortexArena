using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Physics;
using XonoticGodot.Net;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Net;

/// <summary>
/// Bridges the prediction loop's abstract <see cref="IMovementStep"/> to the shared, deterministic
/// <see cref="Movement"/> sim (QC PM_Main) by driving a real <see cref="Entity"/>. The
/// <see cref="Reconciler"/> hands us a <see cref="PredictedState"/> (origin/velocity/onground) and one
/// <see cref="InputCommand"/>; we load that onto the carrier entity, run one authoritative movement tick
/// against the same <see cref="XonoticGodot.Engine.Collision.CollisionWorld"/> the server uses, and read the
/// result back. Because it is the SAME sim the server runs, client prediction and server authority stay in
/// lockstep (ADR-0010 determinism), which is what keeps reconciliation corrections tiny.
///
/// The carrier entity is the client's local player entity (so its hull/flags/state persist across ticks);
/// pass the one <see cref="PlayerController"/> already spawned. The engine services / collision world must be
/// booted (<see cref="GameInit.Boot"/>) before this runs — on a listen server that is already the case, and a
/// remote client boots the same stack over the loaded map's collision world.
/// </summary>
public sealed class EntityMovementStep : IMovementStep
{
    private readonly Entity _carrier;

    public EntityMovementStep(Entity carrier) => _carrier = carrier;

    /// <summary>PRE-MATCH FREEZE mirror (set by the host each frame, like the carrier's DeadState). While true the
    /// predictor runs NO movement step — gravity, wishmove, jump, jumppad/teleport are all skipped — so the predicted
    /// body stays pinned EXACTLY where the server holds it (canMove=false, GameWorld.preMatchFreeze) during the
    /// pre-match countdown. Without this the client predicts a falling/creeping body while the server holds still, and
    /// the per-snapshot sub-32u reconcile error gets smoothed + re-armed every frame = the spawn-countdown vibrate.</summary>
    public bool Frozen { get; set; }

    public void Step(ref PredictedState state, in InputCommand cmd, in PlayerState vars)
    {
        // load the predicted state onto the carrier entity.
        _carrier.Origin = state.Origin;
        _carrier.Velocity = state.Velocity;
        SetOnGround(_carrier, state.OnGround);

        // Clear the per-tick teleport view-snap signal (QC .fixangle) before this tick's trigger passes. After the
        // full reconcile replay it reflects ONLY the newest replayed input, so it is true exactly on the tick the
        // local player crosses a teleporter — which is when NetGame snaps the view (one-shot, replay-safe).
        _carrier.FixAngle = false;

        // Key the predictors' one-shot pulses by this command's sequence (see TriggerTouch.PredictionSeq): a
        // reconcile REPLAY re-runs the same seq, so a seq-keyed pulse fires exactly once per real crossing.
        XonoticGodot.Engine.Simulation.TriggerTouch.PredictionSeq = cmd.Seq;

        // build the per-tick movement input from the command (the same conversion the server applies).
        InputButtons b = cmd.TypedButtons;
        // The InputCommand carries the wish-move normalized to ±1 (see NetGame.SampleInput); the move code must
        // rescale it to wish-velocity units. A live Xonotic client's cl_*speed cvars are stuffcmd'd to
        // max(sv_maxspeed, sv_maxairspeed) = 360 by Base's sys_phys_fixspeed (see WishMoveScaling) — the client
        // holds the replicated movevars (MoveVarsBlock.Apply), so both legs resolve the same value. CRITICAL:
        // keep this identical to ServerNet.ToMovementInput so predictor and converter stay in lockstep.
        var input = new MovementInput
        {
            ViewAngles = cmd.ViewAngles,
            MoveValues = WishMoveScaling.Scale(cmd.Forward, cmd.Side, cmd.Up),
            FrameTime = cmd.DeltaTime > 0f ? cmd.DeltaTime : XonoticGodot.Engine.Simulation.SimulationLoop.TicRate,
            ButtonJump = (b & InputButtons.Jump) != 0,
            ButtonCrouch = (b & InputButtons.Crouch) != 0,
            ButtonAttack1 = (b & InputButtons.Attack) != 0,
            ButtonAttack2 = (b & InputButtons.Attack2) != 0,
            ButtonUse = (b & InputButtons.Use) != 0,
            // PHYS_INPUT_BUTTON_CHAT — keep the predictor in lockstep with ServerNet.ToMovementInput so the typing
            // movement block (PM_check_blocked zeroes the wish-move) is predicted identically to the authority.
            Typing = (b & InputButtons.Chat) != 0,
            // This is client-side prediction (and its reconciliation replays) — suppress the server-owned
            // footstep/landing sounds so a predicted landing doesn't fire (and replays don't multiply) them.
            Predicted = true,
        };

        // PRE-MATCH FREEZE: mirror the server's canMove=false — run NO movement this tick so the predicted body
        // stays pinned at the seed (spawn). Returning the seed unchanged makes the reconcile error 0 against the
        // frozen server origin (no smoothed-error rubberband during the countdown). Resumes the instant go-live.
        if (Frozen)
        {
            state.Origin = _carrier.Origin;
            state.Velocity = _carrier.Velocity;
            state.OnGround = _carrier.OnGround;
            return;
        }

        // run one authoritative movement tick (gravity + friction/accel + slide/step collision).
        Movement.Move(_carrier, input);

        // Mirror the server (GameWorld: `p.Angles = input.ViewAngles`): the carrier's .Angles MUST carry the
        // live view angles. The warpzone/teleport predictors below rotate the EXIT facing via
        // TransformAngles(carrier.Angles); without this the carrier kept a stale/zero angle, so a warpzone snapped
        // the view to a FIXED wrong facing (the reported "comes out at the wrong angle") instead of the player's
        // actual view rotated into the exit plane. (Movement.Move already set .ViewAngles, not .Angles.)
        _carrier.Angles = input.ViewAngles;

        // Client-side jump-pad prediction (CSQC trigger_push): after the move, apply the launch of any jump-pad
        // the carrier now overlaps — exactly as the server does in its post-move TouchAreaGrid pass
        // (SimulationLoop) — so the predicted local player feels pads IN LOCKSTEP with authority. Without this
        // the server launches but the client predicts ordinary jump/fall, and reconciliation jitters the camera
        // (the "jump through the floor / bounce" felt on a pad). Velocity-only, no side effects (see TriggerTouch).
        XonoticGodot.Engine.Simulation.TriggerTouch.PredictJumppadsAmbient(_carrier);

        // Client-side teleporter prediction (CSQC Teleport_Touch): relocate + reproject the carrier through any
        // single-destination trigger_teleport it now overlaps, IN LOCKSTEP with the server's post-move teleport,
        // so a teleport doesn't rubber-band the camera (the reconcile would otherwise measure a teleport-sized
        // origin error and hard-snap). It also stamps the carrier's .fixangle so the host can snap the local view
        // to the exit facing this tick. Predicted mode = no sound/telefrag/targets (server-authoritative).
        XonoticGodot.Engine.Simulation.TriggerTouch.PredictTeleportsAmbient(_carrier);

        // Client-side warpzone prediction (CSQC WarpZone_FixPMove): warp the carrier through any linked
        // trigger_warpzone it now overlaps — origin/velocity/angles rotated by the seam transform — IN LOCKSTEP
        // with the server's post-move WarpZone_Touch, so a seamless-portal crossing doesn't rubber-band the camera
        // (the reconcile would otherwise measure a warp-sized origin error and hard-snap). Like the teleporter
        // predictor it stamps the carrier's .fixangle so the host snaps the local view to the rotated exit facing
        // this tick. Gate matches the authoritative WarpzoneManager.Teleport plane-side test exactly; predicted
        // mode = no SUB_UseTargets/projectile/stuck-recovery side effects (server-authoritative).
        XonoticGodot.Engine.Simulation.TriggerTouch.PredictWarpzonesAmbient(_carrier);

        // read the result back into the predicted state.
        state.Origin = _carrier.Origin;
        state.Velocity = _carrier.Velocity;
        state.OnGround = _carrier.OnGround;
    }

    private static void SetOnGround(Entity e, bool onGround)
    {
        if (onGround) e.Flags |= EntFlags.OnGround;
        else e.Flags &= ~EntFlags.OnGround;
    }
}

/// <summary>
/// The wish-move scaling a LIVE Xonotic client actually runs with: Base's <c>sys_phys_fixspeed</c>
/// (qcsrc/ecs/systems/sv_physics.qc:105-117) stuffcmds <c>cl_forwardspeed</c>/<c>cl_backspeed</c>/
/// <c>cl_sidespeed</c>/<c>cl_upspeed</c> = <c>max(sv_maxspeed, sv_maxairspeed)</c> to every client on spawn —
/// stock: <b>360 on all axes</b>. (The raw DP client defaults — 400/350/400, cl_input.c:364-367 — only apply
/// for the instant before that first stuffcmd lands; baking them here skewed the pure-strafe wishspeed to 350
/// vs Base's 360 and bent the strafity/movity input ANGLE for mixed W+A/D input, since Base's axes are
/// symmetric.) The per-player <c>maxspeed_mod</c> half of fixspeed (spectator ladder) is applied downstream in
/// <c>PlayerPhysics.Move</c>. Reads the live movevars — replicated to clients via <see cref="MoveVarsBlock"/> —
/// so a server running maxspeed ≠ 360 scales identically on both legs; unset cvars fall back to the stock 360.
/// The client predictor (<see cref="EntityMovementStep"/>) and the server converter (<see cref="ServerNet"/>)
/// BOTH call this so prediction stays in lockstep with authority.
/// </summary>
internal static class WishMoveScaling
{
    /// <summary>Stock <c>max(sv_maxspeed, sv_maxairspeed)</c> = 360 (physicsX.cfg sets both to 360).</summary>
    public const float StockSpeed = 360f;

    /// <summary>The live fixspeed value: <c>max(sv_maxspeed, sv_maxairspeed)</c>, unset → 360.</summary>
    public static float Speed()
    {
        if (XonoticGodot.Common.Services.Api.Services is null)
            return StockSpeed;
        float maxspeed = XonoticGodot.Common.Services.Api.Cvars.GetFloat("sv_maxspeed");
        float maxairspeed = XonoticGodot.Common.Services.Api.Cvars.GetFloat("sv_maxairspeed");
        if (maxspeed <= 0f) maxspeed = StockSpeed;       // unset → stock (the FromCvars fallback idiom)
        if (maxairspeed <= 0f) maxairspeed = StockSpeed;
        return System.MathF.Max(maxspeed, maxairspeed);
    }

    /// <summary>Scale a normalized (±1) wish-move into wish-velocity units (forward/side/up → cmd move).</summary>
    public static NVec3 Scale(float forward, float side, float up)
    {
        float spd = Speed();
        return new(forward * spd, side * spd, up * spd);
    }
}
