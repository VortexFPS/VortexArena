using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Physics;
using XonoticGodot.Net;

namespace XonoticGodot.Tests.Camera;

/// <summary>
/// Test-side <see cref="IMovementStep"/> adapter — the parity twin of the production
/// <c>game/net/EntityMovementStep</c> (which lives in the Godot assembly the test project can't reference). It
/// loads a <see cref="PredictedState"/> onto a persistent carrier <see cref="Entity"/>, runs ONE authoritative
/// <see cref="PlayerPhysics"/> tick (the same sim the server runs), and reads the result back — so client
/// prediction and the "server" simulation in <see cref="CameraPipeline"/> stay in lockstep exactly as in play.
///
/// <para>The wish-move scaling matches <c>WishMoveScaling</c> — Base's <c>sys_phys_fixspeed</c> stuffcmds
/// every client's cl_*speed to <c>max(sv_maxspeed, sv_maxairspeed)</c> = 360 stock on ALL axes — kept in sync
/// by hand since that type is <c>internal</c> to the game assembly. The carrier's hull/flags persist across
/// ticks (as in <c>EntityMovementStep</c>); the harness sets <c>Api.Clock.Time</c> once per frame so a
/// reconcile replay re-simulates the stored inputs "at now", never advancing the clock per replayed tick.</para>
/// </summary>
public sealed class PlayerPhysicsStep : IMovementStep
{
    // sys_phys_fixspeed: max(sv_maxspeed, sv_maxairspeed) = 360 stock — must match WishMoveScaling.Speed()
    // (tests run on unset cvars, where WishMoveScaling falls back to the same stock 360).
    private const float ForwardSpeed = 360f, SideSpeed = 360f, UpSpeed = 360f;

    private readonly PlayerPhysics _physics = new();
    private readonly Entity _carrier;

    public PlayerPhysicsStep(Vector3 mins, Vector3 maxs, float standingViewOfsZ = 35f)
    {
        _carrier = new Entity
        {
            Mins = mins,
            Maxs = maxs,
            Gravity = 1f,
            ViewOfs = new Vector3(0f, 0f, standingViewOfsZ),
            // JumpReleased so the first jump can fire; OnGround is overwritten from PredictedState each Step.
            Flags = EntFlags.JumpReleased,
            ClassName = "player",
        };
    }

    /// <summary>The live carrier — the harness reads <see cref="Entity.ViewOfs"/> (eye height) and crouch state.</summary>
    public Entity Carrier => _carrier;

    /// <summary>Parity twin of <c>EntityMovementStep.Frozen</c>: while true, run NO movement (pre-match freeze).</summary>
    public bool Frozen { get; set; }

    public void Step(ref PredictedState state, in InputCommand cmd, in PlayerState vars)
    {
        _carrier.Origin = state.Origin;
        _carrier.Velocity = state.Velocity;
        if (state.OnGround) _carrier.Flags |= EntFlags.OnGround;
        else _carrier.Flags &= ~EntFlags.OnGround;

        // Pre-match freeze: no movement (mirror the server's canMove=false) → state passes through unchanged.
        if (Frozen)
        {
            state.Origin = _carrier.Origin;
            state.Velocity = _carrier.Velocity;
            state.OnGround = _carrier.OnGround;
            return;
        }

        InputButtons b = cmd.TypedButtons;
        var input = new MovementInput
        {
            ViewAngles = cmd.ViewAngles,
            MoveValues = new Vector3(cmd.Forward * ForwardSpeed, cmd.Side * SideSpeed, cmd.Up * UpSpeed),
            FrameTime = cmd.DeltaTime > 0f ? cmd.DeltaTime : 1f / 72f,
            ButtonJump = (b & InputButtons.Jump) != 0,
            ButtonCrouch = (b & InputButtons.Crouch) != 0,
            ButtonAttack1 = (b & InputButtons.Attack) != 0,
            ButtonAttack2 = (b & InputButtons.Attack2) != 0,
            ButtonUse = (b & InputButtons.Use) != 0,
            Predicted = true,
        };

        _physics.Move(_carrier, input);

        state.Origin = _carrier.Origin;
        state.Velocity = _carrier.Velocity;
        state.OnGround = _carrier.OnGround;
    }
}
