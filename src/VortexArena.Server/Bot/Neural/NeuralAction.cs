using System;
using System.Numerics;
using VortexArena.Common.Math;

namespace VortexArena.Server.Bot.Neural;

/// <summary>
/// The policy's decision for one think, decoded from the network's raw output.
///
/// <para><b>Wishmove is nine-way discrete, not continuous.</b> A human strafe-jumps by holding exactly one
/// strafe key, and <c>PMAccelerate.Aircontrol</c> rewards precisely that input shape. A continuous head has
/// to discover that the optimum sits at a corner of the input square, which is slower to find and noisier
/// once found. Nine categories put the optimum on a basis vector from the first step.</para>
///
/// <para><b>View is a delta, not an absolute.</b> The policy owns the mouse: combat hands it a target angle
/// (already lead-compensated and already degraded by the bot's skill error model) and the policy decides the
/// path the crosshair takes to get there. Emitting absolute angles instead would make every aim correction a
/// teleport and would leave nothing for the jerk penalty to smooth.</para>
/// </summary>
public struct NeuralAction
{
    /// <summary>Degrees of yaw the policy may add in one think. Roughly a fast human flick at 20 Hz.</summary>
    public const float MaxYawRate = 22f;

    /// <summary>Degrees of pitch per think. Tighter than yaw: humans move the mouse further sideways.</summary>
    public const float MaxPitchRate = 12f;

    /// <summary>Wish-move forward component, -1..1.</summary>
    public float MoveForward;

    /// <summary>Wish-move right component, -1..1.</summary>
    public float MoveRight;

    public bool Jump;
    public bool Crouch;

    /// <summary>Degrees to add to the view yaw this think, already clamped to <see cref="MaxYawRate"/>.</summary>
    public float YawDelta;

    /// <summary>Degrees to add to the view pitch this think, already clamped to <see cref="MaxPitchRate"/>.</summary>
    public float PitchDelta;

    public bool Attack1;
    public bool Attack2;

    /// <summary>
    /// Index into <see cref="NeuralObservation.MovementWeapons"/>, or -1 for "keep the current weapon".
    /// Only acted on when the intent permits weapon movement.
    /// </summary>
    public int WeaponSelect;

    /// <summary>An action that does nothing: the safe output when the policy is unavailable.</summary>
    public static NeuralAction Neutral => new() { WeaponSelect = -1 };
}

/// <summary>
/// The network's output layout, and the decode from raw logits to a <see cref="NeuralAction"/>.
///
/// <para>Mirrored in <c>va_neural/layout.py</c> in
/// <see href="https://github.com/VortexFPS/NeuralBotLab">VortexFPS/NeuralBotLab</see>;
/// <c>NeuralBotTests.ActionLayoutMatchesPythonMirror</c> and
/// <c>NeuralBotTests.LayoutDescriptorMatchesTheCrossLanguageContract</c> are the guards against the two
/// drifting.</para>
/// </summary>
public static class ActionSpace
{
    // Categorical heads, as [start, count) ranges into the output vector.
    public const int MoveStart = 0, MoveCount = 9;
    public const int JumpStart = MoveStart + MoveCount, JumpCount = 2;
    public const int CrouchStart = JumpStart + JumpCount, CrouchCount = 2;
    public const int Attack1Start = CrouchStart + CrouchCount, Attack1Count = 2;
    public const int Attack2Start = Attack1Start + Attack1Count, Attack2Count = 2;
    public const int WeaponStart = Attack2Start + Attack2Count, WeaponCount = 4;

    // Continuous heads.
    public const int YawIndex = WeaponStart + WeaponCount;
    public const int PitchIndex = YawIndex + 1;

    /// <summary>Total network outputs.</summary>
    public const int Size = PitchIndex + 1;

    /// <summary>
    /// The nine wishmove categories: eight compass directions plus a null. Index 0 is "no input", which the
    /// policy needs in order to coast; a bot that must always press something cannot hold a bunnyhop line.
    /// </summary>
    private static readonly (float Fwd, float Right)[] MoveTable =
    {
        (0f, 0f),
        (1f, 0f), (0.7071f, 0.7071f), (0f, 1f), (-0.7071f, 0.7071f),
        (-1f, 0f), (-0.7071f, -0.7071f), (0f, -1f), (0.7071f, -0.7071f),
    };

    /// <summary>
    /// Turn raw network outputs into an action.
    /// </summary>
    /// <param name="output">The network's <see cref="Size"/> outputs.</param>
    /// <param name="weaponAllowed">
    /// From <see cref="MoveIntent.WeaponMovementAllowed"/>. False masks the attack and weapon-select heads
    /// off entirely: the policy cannot fire, whatever it wanted. A hard mask rather than a reward penalty
    /// because the deterministic combat logic has to be able to *rely* on having the weapon.
    /// </param>
    /// <param name="weaponReady">
    /// Per-movement-weapon availability (owned and with ammo), so the policy cannot select a devastator it
    /// does not have. Masking here rather than letting the switch silently fail keeps the observation the
    /// policy trained against honest.
    /// </param>
    public static NeuralAction Decode(ReadOnlySpan<float> output, bool weaponAllowed, ReadOnlySpan<bool> weaponReady)
    {
        if (output.Length < Size) throw new ArgumentException($"need {Size} outputs", nameof(output));

        int move = ArgMax(output.Slice(MoveStart, MoveCount));
        (float fwd, float right) = MoveTable[move];

        var a = new NeuralAction
        {
            MoveForward = fwd,
            MoveRight = right,
            Jump = ArgMax(output.Slice(JumpStart, JumpCount)) == 1,
            Crouch = !ForceNoCrouch && ArgMax(output.Slice(CrouchStart, CrouchCount)) == 1,
            YawDelta = Squash(output[YawIndex]) * NeuralAction.MaxYawRate,
            PitchDelta = Squash(output[PitchIndex]) * NeuralAction.MaxPitchRate,
            WeaponSelect = -1,
        };

        if (!weaponAllowed)
            return a; // attacks and weapon selection stay off; movement is untouched.

        a.Attack1 = ArgMax(output.Slice(Attack1Start, Attack1Count)) == 1;
        a.Attack2 = ArgMax(output.Slice(Attack2Start, Attack2Count)) == 1;

        // Weapon head: index 0 is "keep current", 1..3 select a movement weapon. An unavailable weapon's
        // logit is skipped rather than clamped, so the choice falls to the best available alternative
        // instead of always collapsing to "keep current".
        int best = 0;
        float bestVal = output[WeaponStart];
        for (int i = 1; i < WeaponCount; i++)
        {
            if (i - 1 >= weaponReady.Length || !weaponReady[i - 1]) continue;
            if (output[WeaponStart + i] <= bestVal) continue;
            bestVal = output[WeaponStart + i];
            best = i;
        }
        a.WeaponSelect = best - 1;   // -1 = keep current

        // Firing with nothing selected and no movement weapon ready is a wasted button press that the
        // weapon driver would reject anyway; suppress it so the recorded action matches what happened.
        if (a.WeaponSelect < 0 && !AnyReady(weaponReady))
        {
            a.Attack1 = false;
            a.Attack2 = false;
        }
        return a;
    }

    private static bool AnyReady(ReadOnlySpan<bool> ready)
    {
        for (int i = 0; i < ready.Length; i++) if (ready[i]) return true;
        return false;
    }

    private static int ArgMax(ReadOnlySpan<float> logits)
    {
        int best = 0;
        float bestVal = logits[0];
        for (int i = 1; i < logits.Length; i++)
        {
            if (logits[i] <= bestVal) continue;
            bestVal = logits[i];
            best = i;
        }
        return best;
    }

    /// <summary>
    /// tanh, so the continuous heads land in [-1,1] whatever the trainer's output scale. The policy learns
    /// against the same squash on the Python side.
    /// </summary>
    private static float Squash(float v) => MathF.Tanh(v);

    /// <summary>
    /// Convert an action's goal-frame wishmove into the view-relative <c>MoveValues</c> the physics wants.
    ///
    /// <para>This late projection is what makes the aim constraint survivable. The policy chose a direction
    /// in the frame it perceives the world in (the direction of travel); the physics wants it relative to
    /// wherever the view ended up pointing. Doing the conversion here, after the view delta has been
    /// applied, means a combat-driven 90 degree view swing re-projects the movement automatically instead of
    /// veering the bot off its line.</para>
    /// </summary>
    /// <param name="action">The decoded action (goal-frame wishmove).</param>
    /// <param name="frameForward">The unit goal-frame forward, XY only.</param>
    /// <param name="viewYaw">The view yaw AFTER the action's delta has been applied, in degrees.</param>
    /// <param name="maxSpeed">Wish-move magnitude the caller scales to (QC sv_maxspeed).</param>
    /// <summary>
    /// Diagnostic: hold crouch off regardless of what the crouch head says.
    ///
    /// <para>Measured on a stage-1 policy, per-head entropy over 12,800 real observations: the crouch head
    /// sits at 93.1% of its own uniform maximum, so it carries almost no preference -- yet its argmax is
    /// "crouch" on 96% of states, and the shipped policy takes the argmax. An arbitrary coin flip on a head
    /// training never gave a reason to set therefore becomes "crouch always". This measures what that
    /// costs.</para>
    /// </summary>
    public static bool ForceNoCrouch => Cvars.FloatOr("bot_neural_force_nocrouch", 0f) != 0f;

    public static Vector3 ToMoveValues(in NeuralAction action, Vector3 frameForward, float viewYaw, float maxSpeed)
    {
        // World-space wish direction: the action's (forward, right) rotated out of the goal frame. The
        // frame's right vector is (fy, -fx), matching QMath.AngleVectors' convention.
        float fx = frameForward.X, fy = frameForward.Y;
        float flen = MathF.Sqrt(fx * fx + fy * fy);
        if (flen < 1e-4f) { fx = 1f; fy = 0f; } else { fx /= flen; fy /= flen; }

        var world = new Vector3(
            fx * action.MoveForward + fy * action.MoveRight,
            fy * action.MoveForward - fx * action.MoveRight,
            0f);

        // Project into the view frame the physics uses. Yaw-only basis, the same one BotNavigation.ComposeMove
        // builds (QC makevectors(v_angle.y * '0 1 0')), so forward/side do not tilt with pitch.
        QMath.AngleVectors(new Vector3(0f, viewYaw, 0f), out Vector3 vf, out Vector3 vr, out _);
        return new Vector3(QMath.Dot(world, vf), QMath.Dot(world, vr), 0f) * maxSpeed;
    }
}
