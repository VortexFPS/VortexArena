using System.Numerics;
using XonoticGodot.Common.Diagnostics;

namespace XonoticGodot.Net;

/// <summary>
/// Fixed-capacity ring buffer of sequence-numbered <see cref="InputCommand"/>s the client has issued
/// but the server has not yet acknowledged — the input-frame ring buffer called out in the networking
/// spec (analogue of the engine's <c>CL_MAX_USERCMDS</c> history that <c>getinputstate</c> walks in
/// <c>cl_player.qc</c>). Capacity is a power of two (mask indexing) and matches
/// <c>CL_MAX_USERCMDS = 128</c> by default.
///
/// Allocation discipline: one array allocated up front, commands stored by value, no per-frame
/// allocation. The client pushes one command per tick and sends a redundant tail of recent commands
/// (unreliable transport); the server dedups by <see cref="InputCommand.Seq"/>.
/// </summary>
public sealed class PredictionBuffer
{
    /// <summary>Must be a power of two and equal the engine's CL_MAX_USERCMDS (csqcmodel/common.qh).</summary>
    public const int Capacity = 128;
    private const int Mask = Capacity - 1;

    private readonly InputCommand[] _commands = new InputCommand[Capacity];

    /// <summary>Sequence number that will be assigned to the next pushed command (the client's
    /// <c>clientcommandframe</c> cursor). Starts at 1 so 0 reads as "no command".</summary>
    public uint NextSeq { get; private set; } = 1;

    /// <summary>The most recent sequence acknowledged by the server (its <c>servercommandframe</c>);
    /// commands with <c>Seq &lt;= AckedSeq</c> are confirmed and replayed-past.</summary>
    public uint AckedSeq { get; private set; }

    /// <summary>Stamp a command with the next sequence number, store it in the ring, and return the seq.
    /// Overwrites the oldest slot if the client outruns acks by more than <see cref="Capacity"/> frames
    /// (which only happens under extreme loss — those inputs are already lost to the server anyway).</summary>
    public uint Push(in InputCommand cmd)
    {
        uint seq = NextSeq++;
        InputCommand c = cmd;
        c.Seq = seq;
        _commands[seq & Mask] = c;
        return seq;
    }

    /// <summary>Record the server's ack of the last input it processed. Monotonic: stale/out-of-order acks
    /// (unreliable transport) are ignored.</summary>
    public void Acknowledge(uint ackedSeq)
    {
        if (ackedSeq > AckedSeq && ackedSeq < NextSeq) AckedSeq = ackedSeq;
    }

    /// <summary>Number of unacknowledged commands currently in flight (those that will be replayed).</summary>
    public int UnackedCount => (int)(NextSeq - 1 - AckedSeq);

    /// <summary>Fetch a stored command by sequence number. Returns false if <paramref name="seq"/> is out of
    /// range or has been overwritten by a newer command (ring wrap). The reconcile loop's
    /// <c>getinputstate(seq)</c>.</summary>
    public bool TryGet(uint seq, out InputCommand cmd)
    {
        if (seq == 0 || seq >= NextSeq || NextSeq - seq > Capacity)
        {
            cmd = default;
            return false;
        }
        cmd = _commands[seq & Mask];
        return cmd.Seq == seq; // guard against a wrapped slot whose seq no longer matches
    }

    /// <summary>
    /// DP <c>CL_RotateMoves</c> (builtin #638, cl_input.c:1746): rotate the view angles of every UNACKED
    /// stored command (<c>Seq &gt; AckedSeq</c>) in place. Called when the client applies a warpzone crossing
    /// to its own view: pending commands were recorded in the PRE-warp frame, and once the reconcile base
    /// state is post-warp, replaying them un-rotated walks the predicted body back through the exit plane
    /// (the spurious re-crossings observed live). <paramref name="rotate"/> maps a pre-warp Euler view to the
    /// post-warp one (the zone's angle transform).
    /// </summary>
    public void RotatePendingViewAngles(System.Func<Vector3, Vector3> rotate)
    {
        for (uint seq = AckedSeq + 1; seq < NextSeq; seq++)
        {
            ref InputCommand c = ref _commands[seq & Mask];
            if (c.Seq == seq) // skip wrapped slots (outrun ring) — those inputs are lost anyway
                c.ViewAngles = rotate(c.ViewAngles);
        }
    }

    /// <summary>Serialize the most recent <paramref name="redundancy"/> commands (oldest-first) into a C2S
    /// packet — the "redundant send" the spec mandates over unreliable transport, so a single dropped
    /// datagram doesn't strand an input. Writes a count byte then each command body.</summary>
    public void WriteRedundant(BitWriter w, int redundancy = 3)
    {
        uint newest = NextSeq - 1;
        if (newest == 0) { w.WriteByte(0); return; }

        int avail = (int)System.Math.Min((uint)redundancy, newest);
        w.WriteByte(avail);
        // oldest of the window first so the receiver applies them in order
        for (int i = avail - 1; i >= 0; i--)
        {
            uint seq = newest - (uint)i;
            if (TryGet(seq, out InputCommand c))
                c.Serialize(w);
        }
    }
}

/// <summary>
/// Plugs the authoritative movement integrator into the reconcile loop. Implemented later by
/// <c>XonoticGodot.Engine</c> (the shared fixed-tick movement step, ADR-0004); the predictor here is
/// movement-agnostic. Takes the state to advance and the input for one frame and mutates the state
/// in place — exactly what <c>CSQC_ClientMovement_PlayerMove_Frame</c> + <c>Movetype_Physics</c> do per
/// <c>CSQCPlayer_Physics</c>.
/// </summary>
public interface IMovementStep
{
    /// <summary>Advance <paramref name="state"/> by one input frame. Pure w.r.t. its inputs (determinism
    /// is a hard prerequisite — ADR-0010): same (state, cmd, vars) ⇒ same result on client and server.</summary>
    void Step(ref PredictedState state, in InputCommand cmd, in PlayerState vars);
}

/// <summary>
/// The minimal predicted local-player state the reconcile loop snapshots and replays — origin/velocity
/// plus the onground flag (cl_player.qc <c>csqcplayer_origin</c>/<c>csqcplayer_velocity</c>/
/// <c>player_pmflags</c>). A small mutable struct, copied by value.
/// </summary>
public struct PredictedState
{
    public Vector3 Origin;
    public Vector3 Velocity;
    public bool OnGround;
}

/// <summary>
/// The client-side predict-and-reconcile loop, ported in structure from
/// <c>csqcmodel/cl_player.qc</c> (<c>CSQCPlayer_Unpredict</c> / <c>CSQCPlayer_PredictTo</c> /
/// <c>CSQCPlayer_SetPredictionError</c>). The valuable reuse the spec/ADR-0005 highlight — Godot offers
/// no equivalent.
///
/// Flow each client frame, given a fresh authoritative state for sequence <c>ackedSeq</c>:
/// <list type="number">
///   <item><b>Unpredict</b> — reset local state to the server's authoritative values (the last acked frame).</item>
///   <item><b>Replay</b> — re-run <see cref="IMovementStep"/> for every unacked <see cref="InputCommand"/>
///         (ackedSeq+1 .. newest), arriving at a fresh prediction.</item>
///   <item><b>Measure error</b> — compare the new prediction at the acked frame against the previous one;
///         feed it to the error-decay smoother (<see cref="SetPredictionError"/>), unless it is huge
///         (teleport/jumppad) in which case ignore it.</item>
///   <item><b>Smooth</b> — render adds the decaying error offset so corrections are visually gradual.</item>
/// </list>
///
/// The actual movement maths is injected via <see cref="IMovementStep"/>; this type owns the
/// sequencing, error measurement and decay, and the render-side <b>stair-smoothing</b> Z blend (the port of
/// <c>CSQCModel_ApplyStairSmoothing</c> / <c>cl_stairsmoothing</c>): when a replayed step snaps the predicted
/// origin up/down a stair, <see cref="NoteStep"/> records the jump and <see cref="GetStairSmoothOffset"/>
/// returns a decaying Z offset the camera adds so the view glides over steps instead of teleporting. The
/// first-person view-bob / weapon-sway blend (<c>CSQCPlayer_ApplySmoothing</c>) remains a pure render concern.
/// </summary>
public sealed class Reconciler
{
    private readonly PredictionBuffer _input;
    private readonly IMovementStep _movement;

    // --- error-decay smoothing state (cl_player.qc csqcplayer_predictionerror{o,v,time,factor}) ---
    private Vector3 _errorOrigin;
    private Vector3 _errorVelocity;
    private float _errorUntilTime;     // csqcplayer_predictionerrortime
    private float _errorFactor;        // csqcplayer_predictionerrorfactor

    // --- stair-smoothing state (csqcmodel CSQCModel_ApplyStairSmoothing) ---
    // The rendered camera Z LAGS the live predicted Z and catches up at StairSmoothSpeed, hard-clamped to within
    // one step height of the live Z every frame (so it can NEVER persist an offset on a slope — the bug the old
    // delta-accumulating/time-window model had). Advanced per RENDER FRAME by the REAL clamped frame dt (NOT the
    // server-synced/rebasing render clock, whose 1/72-quantized jumps made the catch-up jitter), exactly like the
    // reference's `smoothtime = bound(0, time - prevtime, 0.1)`.
    private float _renderedZ;          // absolute rendered camera Z (the smoothed eye height)
    private bool _stairInit;           // false until the first frame seeds _renderedZ to the live Z

    /// <summary>The current predicted state after the last <see cref="Reconcile"/> — what the camera/render
    /// reads (before adding the smoothing offset).</summary>
    public PredictedState Predicted { get; private set; }

    /// <summary>Diagnostic: the magnitude (qu) of the prediction error the LAST <see cref="Reconcile"/> measured —
    /// i.e. how far the freshly-corrected prediction sat from what we were already rendering. ~0 when client predict
    /// and server authority agree (the command-driven, fps-independent target); a steady nonzero value is the
    /// rubberband. A one-shot large value is a teleport/respawn. Surfaced for the camera-trace catharsis check.</summary>
    public float LastReconcileError { get; private set; }

    /// <summary>cl_movement_errorcompensation: 0 disables smoothing (snap to truth). Mirrors the autocvar.</summary>
    public float ErrorCompensation = 0f;

    /// <summary>Server tick rate (Hz) used to scale the error-decay factor. QC computes the factor as
    /// <c>errorcompensation / ticrate</c> where <c>ticrate</c> is the tick PERIOD in seconds (<c>sys_ticrate</c>);
    /// since this field is the rate in Hz (= 1/period), the port MULTIPLIES by it (see <see cref="SetPredictionError"/>).
    /// The resulting decay window is one tick (≈13.9 ms at 72 Hz), matching QC — NOT 72 s (the old divide-by-Hz bug
    /// that left every correction lingering near-forever).</summary>
    public float TickRate = 72f;

    /// <summary>PORT EXTENSION (<c>cl_movement_errorcompensation_force_time</c>, default 0.12 s): the longer, gentler
    /// decay window used when a prediction error looks like DAMAGE KNOCKBACK (a big velocity spike with a still-
    /// plausible origin delta). Stock Xonotic SKIPS those (CSQCPlayer_SetPredictionError's <c>vdist(v,&gt;,192)</c>
    /// guard) because jumppads/teleporters — the other causes of a big velocity spike — weren't predicted; this
    /// port predicts both (<c>EntityMovementStep</c>), so the remaining spike is an explosion shove, and smoothing
    /// it over ~0.12 s glides the view instead of popping it. The velocity itself still applies immediately (only
    /// the VIEW is eased). Set to 0 to fall back to the normal one-tick window even for knockback.</summary>
    public float ForceSmoothWindow = 0.12f;

    /// <summary>cl_stairsmoothing ENABLE toggle: &gt;0 = stair smoothing on (the reference has no time window — the
    /// blend rate is <see cref="StairSmoothSpeed"/>); 0 = off (the camera follows the predicted Z exactly).</summary>
    public float StairSmoothTime = 1f;

    /// <summary>cl_stairsmoothspeed (Base default 200): the FLOOR rate (u/s) at which the rendered Z catches up to
    /// the live predicted Z over a step. The catch-up speeds UP beyond this when the lag is large (see
    /// <see cref="StairCatchupTime"/>). 0 disables the speed cap (instant follow); the caller maps cl_stairsmoothspeed
    /// &lt;= 0 to <see cref="StairSmoothTime"/> = 0 (off), matching the reference's single-cvar semantics.</summary>
    public float StairSmoothSpeed = 200f;

    /// <summary>The live <c>sv_stepheight</c> (Xonotic 31): the MAX the rendered camera Z may lag the live Z — the
    /// reference's <c>PHYS_STEPHEIGHT</c> clamp (CSQCModel_ApplyStairSmoothing: <c>bound(v.z - PHYS_STEPHEIGHT, …)</c>).
    /// A real step can only raise/lower the eye by one step height, so the smoother's lag is bounded by exactly that
    /// (a larger value let the camera lag ~48u below the eye on steep ramps — the "camera too low sometimes" bug).
    /// Defaults to 31; the caller refreshes it from the replicated step height so a non-stock preset stays correct.</summary>
    public float StairStepHeight = 31f;

    /// <summary>PORT-EXTENSION knob (<c>cl_stairsmooth_snapspeed</c>, default 30 u/s) — the AIRBORNE vertical speed
    /// above which the smoother adds the velocity FEEDFORWARD (fast-follow) rather than the gentle ground catch-up.
    /// (Originally the threshold above which the camera SNAPPED to the true Z; the snap is now a fast feedforward
    /// follow, so this just gates "track the jump/fall arc 1:1" vs "ease gently".) Kept low (30) so it engages as
    /// soon as you're genuinely moving vertically, but above the ~11 u/s one-tick stair-flicker blip so a grounded
    /// step still eases through the gentle path. Name kept for config compatibility despite no longer snapping.</summary>
    public float StairSnapVerticalSpeed = 30f;

    /// <summary>PORT-EXTENSION anti-jitter knob #2 (<c>cl_stairsmooth_catchuptime</c>, default 0.1 s). The catch-up
    /// speed is raised, when needed, to close the CURRENT lag within this time (so the effective rate is
    /// <c>max(StairSmoothSpeed, |lag| / StairCatchupTime)</c>). At running speed you climb a staircase faster than a
    /// fixed 200 u/s catch-up, so the old fixed-speed smoother saturated and the ±step clamp yanked the camera up a
    /// few units on every step; scaling the catch-up with the lag keeps it inside the clamp (no yank) while staying
    /// gentle for a single small step (the floor speed dominates there) and easing in/out like the reference. 0 = use
    /// the fixed floor speed only (the old behaviour).</summary>
    public float StairCatchupTime = 0.1f;

    // Thresholds from CSQCPlayer_SetPredictionError: an origin jump past MaxErrorOrigin is a teleport (snap), and a
    // velocity jump past MaxErrorVelocity is a "spike" QC discarded. The port reinterprets the spike: with jumppads
    // and teleporters now predicted, a spike with an origin delta still within MaxForceOrigin is damage knockback —
    // smoothed over ForceSmoothWindow rather than discarded. A bigger origin delta is a real teleport, so snap.
    public const float MaxErrorOrigin = 32f;     // vdist(o, >, 32) — origin error above this is a teleport-class snap
    private const float MaxErrorVelocity = 192f;  // vdist(v, >, 192)

    // Knockback's origin delta = the in-flight velocity spike integrated over the unacked-command window (a few
    // ticks); even a strong rocket-jump stays well under this. A genuine cross-map teleport dwarfs it, so a spike
    // with an origin jump past this is a teleport (snap), not a shove to smooth.
    public const float MaxForceOrigin = 250f;

    // Fallback step-height clamp when StairStepHeight is left unset/0 (keep in sync with MovementParameters.StepHeight).
    private const float MaxStairStepFallback = 31f;

    public Reconciler(PredictionBuffer input, IMovementStep movement)
    {
        _input = input;
        _movement = movement;
    }

    /// <summary>(§13.4) Origin error past which a reconcile counts as a real desync (normal corrections are
    /// 0-15 u; a teleport raises one one-shot event). Latched + re-raised every 72 reconciles while sustained.</summary>
    private const float DesyncEventQu = 64f;
    private bool _desyncLatched;
    private int _desyncRepeat;

    /// <summary>
    /// Run one reconciliation: re-apply all unacked inputs on top of the authoritative
    /// <paramref name="serverState"/> (valid as of <paramref name="ackedSeq"/>), measure the prediction
    /// error against <paramref name="previousPredictionAtAck"/> (the state we had previously predicted for
    /// that same frame), and update the smoother. Returns the new predicted state.
    ///
    /// Mirrors <c>CSQCPlayer_PredictTo</c>: unpredict → replay; and the <c>CSQCPlayer_SetCamera</c> tail that
    /// calls <c>CSQCPlayer_SetPredictionError(e.origin - o, e.velocity - v0, …)</c>.
    /// </summary>
    public PredictedState Reconcile(
        in PredictedState serverState,
        uint ackedSeq,
        in PlayerState vars,
        float now,
        in PredictedState previousPredictionAtAck)
    {
        _input.Acknowledge(ackedSeq);

        // 1) Unpredict: start from the authoritative state.
        PredictedState s = serverState;

        // 2) Replay every unacked input (ackedSeq+1 .. newest), in order.
        uint newest = _input.NextSeq - 1;
        for (uint seq = ackedSeq + 1; seq <= newest; seq++)
        {
            if (!_input.TryGet(seq, out InputCommand cmd)) continue;
            _movement.Step(ref s, in cmd, in vars);
        }

        // 3) Measure the prediction error to smooth = the discontinuity AT THE RENDERED (newest) frame between
        //    what we were already showing and the freshly-corrected prediction. BOTH operands are at `newest`:
        //      previousPredictionAtAck = the OLD authoritative seed replayed to newest (captured before this
        //                                snapshot, i.e. the pose the camera was already rendering), and
        //      s                       = the NEW authoritative seed replayed to that SAME newest (just above).
        //    So this is ~0 whenever the corrected seed, replayed through the same inputs, reproduces the old
        //    prediction (deterministic agreement) and is nonzero ONLY by the genuine correction the new ack
        //    introduced — it does NOT scale with the player's travel across the in-flight window.
        //    Sign is (old − new) so UpdateCamera's `PredictedOrigin(=s, the new pose) + offset` starts at the
        //    OLD rendered position and glides to the new one (no pop).
        //
        //    HISTORY: the previous form `serverState − previousPredictionAtAck` diffed truth@ackedSeq against
        //    prediction@newest — two DIFFERENT timeline frames separated by the in-flight command depth — so even
        //    with a perfect predictor it injected ≈ −(displacement over the in-flight ticks) ≈ 5–15u every
        //    snapshot at running speed, a speed-proportional phantom correction (the "camera jumps while moving"
        //    bug). QC measures a same-frame residual (cl_player.qc CSQCPlayer_SetCamera: prediction@scf − truth@scf),
        //    ~0 when the sims agree; this restores that invariant.
        Vector3 oErr = previousPredictionAtAck.Origin - s.Origin;
        Vector3 vErr = previousPredictionAtAck.Velocity - s.Velocity;
        SetPredictionError(oErr, vErr, now);

        // (§13.4) Prediction-desync detector: a healthy predictor measures ~0-15 u corrections (teleports
        // excepted, one-shot); a SUSTAINED large error means client prediction and server authority have
        // genuinely diverged — the "camera through walls / projectiles fire from somewhere else" class the
        // sv_threaded real-play test hit, which no idle-client soak can catch. Latched with periodic
        // re-raise so the forensic log shows both the onset and the persistence.
        float errLen = oErr.Length();
        LastReconcileError = errLen; // diagnostic (camera-trace): ~0 = client/server agree; steady nonzero = rubberband
        if (errLen > DesyncEventQu)
        {
            if (!_desyncLatched || ++_desyncRepeat >= 72)
            {
                _desyncLatched = true;
                _desyncRepeat = 0;
                XonoticGodot.Common.Diagnostics.Prof.Event(
                    $"net: PREDICTION DESYNC {errLen:0}qu (predicted vs acked-replay origin)");
            }
        }
        else if (errLen < DesyncEventQu * 0.5f)
        {
            _desyncLatched = false;
            _desyncRepeat = 0;
        }

        // Stair smoothing is no longer measured here per-tick (the old per-tick delta model perpetually re-armed
        // on slopes); it is driven per RENDER FRAME from the live predicted Z in GetStairSmoothOffset.
        Predicted = s;
        return s;
    }

    /// <summary>
    /// Replay-only prediction: re-run the unacked inputs (acked+1 .. newest) on top of the last authoritative
    /// state and refresh <see cref="Predicted"/>, WITHOUT re-measuring/arming the prediction-error smoother.
    /// Call this every client input tick after pushing the new command (so the local view reflects the latest
    /// input immediately); call <see cref="Reconcile"/> only when a fresh server snapshot/ack arrives (that is
    /// the moment the error is measured against what we had predicted). Stair smoothing IS updated here, since
    /// a step can be introduced by simply replaying one more input.
    ///
    /// This is the split QC makes between <c>CSQCPlayer_PredictTo</c> (run each frame) and the error capture
    /// in <c>CSQCPlayer_SetCamera</c> (only on a new server frame): keeping them separate is what lets the
    /// error decay smoothly between snapshots instead of being re-pinned to full magnitude every tick.
    /// </summary>
    public PredictedState Predict(in PredictedState serverState, uint ackedSeq, in PlayerState vars, float now)
    {
        _ = now; // stair smoothing now runs per-frame in GetStairSmoothOffset, not per replayed tick
        PredictedState s = serverState;
        uint newest = _input.NextSeq - 1;
        for (uint seq = ackedSeq + 1; seq <= newest; seq++)
        {
            if (!_input.TryGet(seq, out InputCommand cmd)) continue;
            _movement.Step(ref s, in cmd, in vars);
        }

        Predicted = s;
        return s;
    }

    /// <summary>
    /// Accumulate a prediction error into the decaying smoother. Port of <c>CSQCPlayer_SetPredictionError</c>,
    /// extended: where stock QC DISCARDS a big velocity spike, this smooths it as damage knockback (see
    /// <see cref="ForceSmoothWindow"/>). A teleport-sized origin jump still snaps.
    /// </summary>
    public void SetPredictionError(Vector3 originError, Vector3 velocityError, float now)
    {
        float oLen = originError.Length();
        float vLen = velocityError.Length();

        // A big VELOCITY spike with a still-plausible ORIGIN delta is damage knockback (a rocket/blaster shove):
        // jumppads and teleporters — the other things that produce a >192 u/s spike — are now client-predicted
        // (EntityMovementStep.PredictJumppadsAmbient / PredictTeleportsAmbient), so they no longer reach this path.
        // Stock QC discarded the spike (vdist(v,>,192)); we instead SMOOTH it (over ForceSmoothWindow) so the shove
        // glides the view rather than popping it. Capped at MaxForceOrigin so a genuine cross-map teleport (whose
        // origin delta dwarfs any knockback's few-tick in-flight drift) still snaps below.
        bool isForce = vLen > MaxErrorVelocity && oLen <= MaxForceOrigin;

        // A position error too big to be knockback is a TELEPORT / RESPAWN / spawn (origin discontinuity) — OR a
        // genuine desync: SNAP, and CLEAR any accumulated smoothing so the camera jumps cleanly to the destination
        // instead of dragging a stale offset that floats it away. The port's equivalent of csqcmodel's
        // csqcmodel_teleported clearing the view smoothing on a networked teleport (lib/csqcmodel/cl_player.qc) —
        // QC reads a flag from the server; we infer the teleport from the origin jump. Without this, the small
        // per-snapshot error the predictor accumulates while the local player is a FROZEN observer (client predicts
        // gravity, server holds still) survives the observer→player spawn and floats the camera thousands of u up.
        if (oLen > MaxErrorOrigin && !isForce)
        {
            // Diagnostic: a position correction this large is a teleport/respawn — OR a genuine desync (the
            // client predicted somewhere the server didn't follow, e.g. a server-side stuck). Repeated lines
            // here while moving normally are the signature of a rubberband. Developer-gated (QC Con_DPrintf).
            if (Log.WillTrace)
                Log.Trace($"[reconcile] origin SNAP {oLen:0.0}u (>{MaxErrorOrigin}) — teleport/respawn or desync; smoothing reset");
            ResetError();
            return;
        }

        if (ErrorCompensation == 0f)
        {
            // Smoothing disabled (cl_movement_errorcompensation 0): snap to truth. A knockback then pops, as it
            // does in stock QC with the cvar off — the user asked for raw corrections.
            _errorFactor = 0f;
            return;
        }

        // Accumulate on top of any residual error still decaying, then (re)start the decay window. A knockback uses
        // the longer ForceSmoothWindow; a normal residual uses QC's one-tick window — errorcompensation / ticrate,
        // i.e. errorcompensation × TickRate(Hz) (the units fix: stock divides by the tick PERIOD in seconds, so the
        // port multiplies by the rate). Never SHORTEN an already-active (longer) window: the tiny residual the tick
        // AFTER a knockback must not truncate the force glide into a fast zip, so keep the later of the two expiries
        // and recompute the factor so the (re)accumulated error still decays to zero exactly at that expiry.
        if (Log.WillTrace && isForce)
            Log.Trace($"[reconcile] knockback shove {vLen:0}u/s (origin {oLen:0.0}u) — smoothing over {ForceSmoothWindow * 1000f:0}ms");
        _errorOrigin = GetPredictionErrorOrigin(now) + originError;
        _errorVelocity = GetPredictionErrorVelocity(now) + velocityError;

        // C2 bug-fix (port mode): cap the ACCUMULATED offset so a stream of same-direction residuals arriving
        // faster than the decay window can't build an ever-growing camera lag (the "slow drift" the apparatus
        // reproduces in Layer1_Port_LongerWindow). A single residual is always < MaxErrorOrigin (a bigger one
        // already snapped above), so clamping the accumulator to that bound preserves normal one-shot smoothing
        // while bounding pathological accumulation; the velocity error is clamped to its sibling threshold.
        float accO = _errorOrigin.Length();
        if (accO > MaxErrorOrigin) _errorOrigin *= MaxErrorOrigin / accO;
        float accV = _errorVelocity.Length();
        if (accV > MaxErrorVelocity) _errorVelocity *= MaxErrorVelocity / accV;

        float residualWindow = 1f / (ErrorCompensation * (TickRate > 0f ? TickRate : 1f));
        float newWindow = isForce && ForceSmoothWindow > 0f ? ForceSmoothWindow : residualWindow;
        float activeUntil = _errorUntilTime > now ? _errorUntilTime : now;
        _errorUntilTime = MathF.Max(activeUntil, now + newWindow);
        _errorFactor = 1f / (_errorUntilTime - now);
    }

    /// <summary>Current decaying origin offset to add to the rendered position. Port of
    /// <c>CSQCPlayer_GetPredictionErrorO</c>: linearly decays to zero by <c>_errorUntilTime</c>.</summary>
    public Vector3 GetPredictionErrorOrigin(float now)
    {
        if (now >= _errorUntilTime) return Vector3.Zero;
        return _errorOrigin * ((_errorUntilTime - now) * _errorFactor);
    }

    /// <summary>Current decaying velocity offset. Port of <c>CSQCPlayer_GetPredictionErrorV</c>.</summary>
    public Vector3 GetPredictionErrorVelocity(float now)
    {
        if (now >= _errorUntilTime) return Vector3.Zero;
        return _errorVelocity * ((_errorUntilTime - now) * _errorFactor);
    }

    // ---------------------------------------------------------------------
    // Stair smoothing (csqcmodel CSQCModel_ApplyStairSmoothing / cl_stairsmoothing)
    // ---------------------------------------------------------------------

    /// <summary>
    /// The vertical offset to SUBTRACT from the predicted origin Z before rendering, so the camera glides over
    /// stair-steps instead of popping. The rendered Z (<see cref="_renderedZ"/>) catches up to the LIVE predicted Z
    /// at <see cref="StairSmoothSpeed"/> u/s (sped up to close a large lag within <see cref="StairCatchupTime"/>) and
    /// is HARD-CLAMPED to within one step height of it — so it can never lag the true Z by more than a step (no
    /// persistent offset pinned on slopes/ramps). While following a genuine jump/fall (airborne, fast) it adds a
    /// velocity FEEDFORWARD so the camera keeps pace with the arc (≈ no lag, like the old snap) while still gliding
    /// over an instant step pop — i.e. it moves fast but SMOOTH over that motion instead of snapping to it.
    ///
    /// <para>Advanced once per RENDER FRAME. <paramref name="frameDt"/> is the REAL wall-clock frame delta (NOT
    /// the server-synced render clock, whose snapshot-rebased 1/72-quantized jumps made the catch-up jitter up/
    /// down); it is clamped to <c>[0, 0.1]</c> exactly like the reference's <c>smoothtime</c>. A placement-only
    /// call (frameDt 0) returns the current offset without advancing. Callers do
    /// <c>renderZ = predicted.Z - GetStairSmoothOffset(frameDt)</c>, i.e. the camera sits at <c>_renderedZ</c>.</para>
    /// </summary>
    public float GetStairSmoothOffset(float frameDt)
    {
        float trueZ = Predicted.Origin.Z;
        float step = StairStepHeight > 0f ? StairStepHeight : MaxStairStepFallback;

        // Disabled or first frame: (re)seed the rendered Z to the live Z, no offset.
        if (StairSmoothTime <= 0f || !_stairInit)
        {
            _renderedZ = trueZ;
            _stairInit = true;
            return 0f;
        }

        // Slide the rendered Z toward the live true Z (smoothtime = bound(0, dt, 0.1)). The base catch-up rate is the
        // configured floor (cl_stairsmoothspeed) OR — when the lag is large — fast enough to close the whole gap
        // within StairCatchupTime, whichever is greater. Scaling with the lag keeps the rendered Z INSIDE the ±step
        // clamp on a fast multi-step climb (no per-step yank), while the floor speed keeps a single small step gentle.
        float smoothtime = frameDt < 0f ? 0f : (frameDt > 0.1f ? 0.1f : frameDt);
        float gap = MathF.Abs(trueZ - _renderedZ);
        float floorSpeed = StairSmoothSpeed > 0f ? StairSmoothSpeed : float.MaxValue;
        float adaptiveSpeed = StairCatchupTime > 0f ? gap / StairCatchupTime : 0f;
        float rate = MathF.Max(floorSpeed, adaptiveSpeed);

        // Following a GENUINE vertical move (airborne, |velocity.z| above the threshold) we used to SNAP the rendered
        // Z straight to the live Z — which showed every step-up pop clipped mid-jump as an instant jump (the snap
        // felt while bunnyhopping up stairs). Instead, add a velocity FEEDFORWARD: move at the player's real vertical
        // speed so the camera keeps pace with the jump/fall arc with ~no lag (a steady jump still tracks 1:1, exactly
        // like the snap did), while the adaptive term on top still glides over an instant step pop within
        // ~StairCatchupTime — i.e. the camera moves FAST but SMOOTHLY over that movement instead of popping.
        if (!Predicted.OnGround && MathF.Abs(Predicted.Velocity.Z) > StairSnapVerticalSpeed)
            rate = MathF.Max(rate, MathF.Abs(Predicted.Velocity.Z) + adaptiveSpeed);

        float maxMove = rate * smoothtime;
        if (_renderedZ < trueZ) _renderedZ = MathF.Min(_renderedZ + maxMove, trueZ);
        else                    _renderedZ = MathF.Max(_renderedZ - maxMove, trueZ);
        // Safety clamp: the rendered Z may never lag the live Z by more than one step height (PHYS_STEPHEIGHT) so a
        // pathological case (a very steep continuous slope / a fast mover) can't pin an ever-growing offset.
        _renderedZ = QMathClamp(_renderedZ, trueZ - step, trueZ + step);

        return trueZ - _renderedZ;
    }

    /// <summary>Reset all smoothing state (e.g. on respawn / hard teleport).</summary>
    public void ResetError()
    {
        _errorOrigin = _errorVelocity = Vector3.Zero;
        _errorUntilTime = 0f;
        // Re-seed stair smoothing to the live Z next frame (a teleport/respawn must not drag a stale lag).
        _stairInit = false;
        _errorFactor = 0f;
    }

    // Local clamp (keep this file leaf-level / Common-Math-free on the hot path, like Quantize).
    private static float QMathClamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
}
