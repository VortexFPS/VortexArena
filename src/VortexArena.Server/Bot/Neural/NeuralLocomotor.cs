using System;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Math;
using VortexArena.Common.Physics;

namespace VortexArena.Server.Bot.Neural;

/// <summary>
/// The learned locomotor: one bot's instance of the observation buffers, the network scratch, and the
/// goal-direction slew limiter. Consumes a <see cref="MoveIntent"/>, produces a <see cref="MovementInput"/>.
///
/// <para>One of these per bot. The <see cref="PolicyNetwork"/> it evaluates is shared by every bot on the
/// server; only the per-caller state lives here.</para>
/// </summary>
public sealed class NeuralLocomotor
{
    private readonly NeuralObservation _obs = new();
    private readonly float[] _observation;
    private readonly float[] _output;
    private readonly bool[] _weaponReady = new bool[NeuralObservation.MovementWeapons.Length];
    private PolicyNetwork.Scratch _scratch;
    private PolicyNetwork _net;

    /// <summary>The slewed goal position the policy actually sees. See <see cref="SlewGoal"/>.</summary>
    private Vector3 _smoothedGoal;
    private bool _goalPrimed;

    /// <summary>The last action, kept so a throttled think can repeat it and the emitter can smooth the view.</summary>
    public NeuralAction LastAction { get; private set; } = NeuralAction.Neutral;

    /// <summary>The goal-frame forward used for the last decision, needed to project the wishmove.</summary>
    private Vector3 _lastFrame = Vector3.UnitX;

    /// <summary>
    /// TRAINING ONLY. When true, <see cref="Think"/> still builds the observation but takes its action from
    /// <see cref="PendingExternalAction"/> instead of evaluating the network.
    ///
    /// <para>This is how the trainer keeps the policy in Python: the environment runs the real observation
    /// builder, the real physics and the real weapon pipeline, and the sampling happens on the other side of
    /// the socket. Running the network here as well would mean keeping two copies of the weights in step
    /// mid-run, which is the classic way to train against a policy that is not the one you ship.</para>
    /// </summary>
    public bool UseExternalAction;

    /// <summary>The action to use this think when <see cref="UseExternalAction"/> is set.</summary>
    public NeuralAction PendingExternalAction;

    /// <summary>
    /// TRAINING ONLY, and only with <see cref="UseExternalAction"/>: skip the observation build inside the
    /// think and let the environment build it once, after the physics for the step has run.
    ///
    /// <para><b>Why this exists.</b> Without it the think rebuilds the observation and applies the pending
    /// action in the same call, so the action the trainer chose from the observation returned at the END of
    /// step N is applied against a freshly built observation at the START of step N+1 — a one-think lag that
    /// the live server does not have, because there the observation and the action come from the same
    /// forward pass. Measured on one network, one seed, stage 3: <b>41.2% arrivals evaluated in-locomotor,
    /// 9.3% through the lagged path</b>. Training through the worse of the two teaches the policy to
    /// compensate for a delay that will not be there, and stage 3 is jump timing, where 55 ms is the whole
    /// problem.</para>
    /// </summary>
    public bool DeferObservationBuild;

    /// <summary>
    /// The observation built by the most recent <see cref="Think"/>. The trainer reads it straight out of
    /// here, so what the policy is scored on is byte-for-byte what the live server would have fed it.
    /// </summary>
    public ReadOnlySpan<float> LastObservation => _observation;

    public NeuralLocomotor(PolicyNetwork net)
    {
        _net = net;
        _observation = new float[NeuralObservation.Size];
        _output = new float[ActionSpace.Size];
        _scratch = new PolicyNetwork.Scratch(net);
    }

    /// <summary>Swap in a newly loaded network (a <c>bot_neural_weights</c> change mid-match).</summary>
    public void SetNetwork(PolicyNetwork net)
    {
        _net = net;
        _scratch = new PolicyNetwork.Scratch(net);
    }

    /// <summary>
    /// Build the observation for the current state without producing an action. The training environment
    /// calls this once per step, after the physics, so the observation it returns is exactly the state the
    /// next action will be applied to. See <see cref="DeferObservationBuild"/>.
    /// </summary>
    public void BuildObservationOnly(Player bot, in MoveIntent intent, NavField? field, MapFeatures? features,
        Vector3 currentView, float now, bool traceFan)
    {
        MoveIntent seen = intent;
        seen.GoalPos = _goalPrimed ? _smoothedGoal : intent.GoalPos;
        _obs.Build(bot, seen, field, features, currentView, now, traceFan, _observation);
    }

    /// <summary>Clear the per-bot history at a spawn or teleport.</summary>
    public void Reset(Player bot, float now)
    {
        _obs.Reset(bot.Velocity, bot.OnGround, now);
        _goalPrimed = false;
        LastAction = NeuralAction.Neutral;
    }

    /// <summary>Adopt an externally replaced destination immediately instead of slewing from the stale one.</summary>
    public void SnapGoal(Vector3 goal)
    {
        _smoothedGoal = goal;
        _goalPrimed = true;
    }

    /// <summary>
    /// How fast the goal the policy sees may chase the goal the strategist picked, in qu/s. The strategist
    /// re-rates on a 5.5 to 7 second clock and a re-rate can swing the target across the map; without a
    /// limiter the whole observation's goal section steps discontinuously and the bot visibly flinches.
    ///
    /// <para>This is only half the smoothing. The other half, and the one that actually produces smooth
    /// motion, is the jerk penalty in the reward plus feeding the previous action back in as an input. Input
    /// smoothing alone yields a policy that snaps as soon as the smoothed signal arrives.</para>
    /// </summary>
    public const float GoalSlewRate = 2600f;

    /// <summary>
    /// Express the shared observation/action frame along the corridor look-ahead rather than at the final
    /// target. Changes what every action index MEANS, so it is fixed for a policy's lifetime: flipping it
    /// under a policy trained the other way scrambles that policy rather than degrading it.
    ///
    /// <para>A property rather than a <c>static readonly</c>, and that is not a style choice. A static
    /// initialiser latches on first touch of this type, which happens before anything has had a chance to
    /// set the cvar -- and <see cref="Cvars.Set"/> is itself a silent no-op until <c>Api.Services</c>
    /// exists. Latched, this read the fallback forever: an A/B across it compared a configuration against
    /// itself and looked exactly like a cleanly rejected hypothesis.</para>
    /// </summary>
    public static bool UseCorridorFrame => Cvars.FloatOr("bot_neural_corridor_frame", 0f) != 0f;

    /// <summary>
    /// Run one think. <paramref name="dt"/> is the time since the previous think (not the tick length), so
    /// the slew limiter and the view delta scale correctly under the skill-varying think throttle.
    /// </summary>
    public MovementInput Think(Player bot, in MoveIntent intent, NavField? field, MapFeatures? features,
        Vector3 currentView, float now, float dt, float maxSpeed, bool traceFan = true)
    {
        Vector3 goal = SlewGoal(intent.GoalPos, dt);

        MoveIntent seen = intent;
        seen.GoalPos = goal;

        // The frame everything is expressed in, recomputed here so the action projection at the end uses
        // exactly the frame the observation was built in.
        _lastFrame = ResolveFrame(bot, goal, currentView, intent.CorridorA);

        // The environment builds the observation itself when it is driving the step boundary; see
        // DeferObservationBuild.
        if (!DeferObservationBuild)
            _obs.Build(bot, seen, field, features, currentView, now, traceFan, _observation);

        NeuralAction action;
        if (UseExternalAction)
        {
            // The permit is re-applied here even though the trainer already masked it, because the two
            // enforcement points have to agree exactly or the policy learns against a rule the live game
            // does not apply.
            action = PendingExternalAction;
            if (!intent.WeaponMovementAllowed)
            {
                action.Attack1 = false;
                action.Attack2 = false;
                action.WeaponSelect = -1;
            }
        }
        else
        {
            _net.Evaluate(_observation, _scratch, _output);
            for (int i = 0; i < _weaponReady.Length; i++)
                _weaponReady[i] = NeuralObservation.MovementWeaponReady(bot, NeuralObservation.MovementWeapons[i]);
            action = ActionSpace.Decode(_output, intent.WeaponMovementAllowed, _weaponReady);
        }
        LastAction = action;
        _obs.NoteAction(action);

        // Apply the view delta. The policy owns the mouse: combat supplied a target angle in the intent and
        // the network decided the path to it, so nothing overrides the result here.
        Vector3 view = currentView;
        view.Y += action.YawDelta;
        view.X = Math.Clamp(view.X + action.PitchDelta, -90f, 90f);
        view.Z = 0f;
        view.Y -= MathF.Floor(view.Y / 360f) * 360f;

        Vector3 move = ActionSpace.ToMoveValues(action, _lastFrame, view.Y, maxSpeed);

        return new MovementInput
        {
            ViewAngles = view,
            MoveValues = move,
            FrameTime = dt,
            ButtonJump = action.Jump,
            ButtonCrouch = action.Crouch,
            ButtonAttack1 = action.Attack1,
            ButtonAttack2 = action.Attack2,
        };
    }

    /// <summary>
    /// The weapon the policy asked to hold this think, or null for "keep the current one". The caller
    /// performs the switch, because inventory changes belong to the brain rather than the locomotor.
    /// </summary>
    public Weapon? RequestedWeapon()
        => LastAction.WeaponSelect >= 0 && LastAction.WeaponSelect < NeuralObservation.MovementWeapons.Length
            ? Weapons.ByName(NeuralObservation.MovementWeapons[LastAction.WeaponSelect])
            : null;

    /// <summary>
    /// Move the observed goal toward the real one at a bounded rate. A goal further than one slew step away
    /// is approached; a nearby one is adopted outright.
    /// </summary>
    private Vector3 SlewGoal(Vector3 target, float dt)
    {
        if (!_goalPrimed) { _smoothedGoal = target; _goalPrimed = true; return target; }
        Vector3 delta = target - _smoothedGoal;
        float dist = delta.Length();
        float step = GoalSlewRate * MathF.Max(dt, 1e-3f);
        _smoothedGoal = dist <= step ? target : _smoothedGoal + delta * (step / dist);
        return _smoothedGoal;
    }

    /// <summary>
    /// The horizontal frame the observation and action share: toward the goal, falling back to the velocity
    /// and then to the view when the bot is stationary and goalless.
    ///
    /// <para>When <c>bot_neural_corridor_frame</c> is set, the frame points along the corridor look-ahead
    /// instead of at the final target. The two agree in open space and diverge at every corner, which is
    /// exactly where a real arena route differs from a generated one -- see the note on
    /// <paramref name="corridor"/>.</para>
    /// </summary>
    /// <param name="corridor">
    /// The corridor look-ahead, walked from the distance field, or a zero vector when there is none.
    ///
    /// <para>Why this is a candidate frame at all: the action's "forward" is whatever direction the frame
    /// names, so the frame decides what the policy's most-used action MEANS. Pointed at the final target it
    /// means "toward the target through whatever wall is in the way", and the policy has to learn to convert
    /// the route direction into target-relative coordinates -- a rotation that changes at every corner.
    /// Pointed along the corridor it means "the way to go", and that conversion disappears.</para>
    /// </param>
    private static Vector3 ResolveFrame(Player bot, Vector3 goal, Vector3 view, Vector3 corridor)
    {
        if (UseCorridorFrame)
        {
            Vector3 c = corridor - bot.Origin;
            c.Z = 0f;
            if (c.LengthSquared() >= 64f) return QMath.Normalize(c);
        }

        Vector3 f = goal - bot.Origin;
        f.Z = 0f;
        if (f.LengthSquared() >= 1f) return QMath.Normalize(f);

        f = new Vector3(bot.Velocity.X, bot.Velocity.Y, 0f);
        if (f.LengthSquared() >= 1f) return QMath.Normalize(f);

        QMath.AngleVectors(view, out Vector3 fwd, out _, out _);
        f = new Vector3(fwd.X, fwd.Y, 0f);
        return f.LengthSquared() >= 1e-6f ? QMath.Normalize(f) : Vector3.UnitX;
    }
}
