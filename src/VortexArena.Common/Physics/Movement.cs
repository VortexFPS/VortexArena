using System.Numerics;
using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Physics;

/// <summary>
/// The per-tick movement input for a player, abstracting QuakeC's <c>PHYS_INPUT_*</c> macros
/// (common/physics/player.qh) which map differently for client-prediction (CSQC) vs server (SVQC).
/// A client fills this from its input ring buffer; the server fills it from the received move command.
/// </summary>
public interface IMovementInput
{
    /// <summary>View angles in degrees (pitch=X down-positive, yaw=Y, roll=Z) — PHYS_INPUT_ANGLES.</summary>
    Vector3 ViewAngles { get; }

    /// <summary>Wish-move: forward(X), side(Y), up(Z) — PHYS_INPUT_MOVEVALUES.</summary>
    Vector3 MoveValues { get; }

    /// <summary>Tick length in seconds — PHYS_INPUT_TIMELENGTH.</summary>
    float FrameTime { get; }

    bool ButtonJump { get; }     // button2
    bool ButtonCrouch { get; }   // crouch
    bool ButtonUse { get; }
    bool ButtonAttack1 { get; }  // button0 (primary fire)
    bool ButtonAttack2 { get; }  // secondary fire

    /// <summary>
    /// PHYS_INPUT_BUTTON_HOOK (player.qh:157, == BUTTON6) — the +hook / offhand-fire button. The weapon
    /// system reads it each frame to drive the player's offhand weapon think (grapple hook, offhand blaster,
    /// nade prime/throw). Defaulted to <c>false</c> so existing input sources keep compiling; the net layer
    /// (ServerNet.ToMovementInput) and bots set it from the move command / brain.
    /// </summary>
    bool ButtonHook => false;

    /// <summary>
    /// PHYS_INPUT_BUTTON_JETPACK — the dedicated jetpack key (separate from <see cref="ButtonJump"/>).
    /// Defaulted to <c>false</c> as a default-interface-method so existing input sources keep compiling;
    /// a source that supports the jetpack overrides it. (CheckPlayerJump also activates the jetpack from
    /// a held jump in mid-air when <c>cl_jetpack_jump</c> is set, independent of this key.)
    /// </summary>
    bool ButtonJetpack => false;

    /// <summary>
    /// PHYS_INPUT_BUTTON_ZOOM / PHYS_INPUT_BUTTON_ZOOMSCRIPT (player.qh) — the +zoom bind is held. The rifle
    /// reads it to re-aim its shot straight from the eye while scoped (QC rifle.qc:16-20, pixel-accurate long
    /// shots). Defaulted to <c>false</c> as a default-interface-method so existing input sources keep compiling;
    /// the net layer (ServerNet.ToMovementInput) decodes it from the move command's <c>InputButtons.Zoom</c> bit.
    /// </summary>
    bool ButtonZoom => false;

    /// <summary>
    /// PHYS_INPUT_BUTTON_CHAT / PHYS_INPUT_BUTTON_MINIGAME — the player is typing in chat / a minigame, so
    /// jumping and movement intent are suppressed (QC PlayerJump / PM_check_blocked typing guards).
    /// Defaulted to <c>false</c> so existing input sources keep compiling.
    /// </summary>
    bool Typing => false;

    /// <summary>
    /// The one-shot client impulse on this tick (QC <c>CS(this).impulse</c>, usercmd.impulse) — the
    /// spectator free-flight speed ladder reads it (sv_physics.qc <c>sys_phys_spectator_control</c>: impulses
    /// 1-19/200-209/220-229 step SPECTATORSPEED). 0 = none. Defaulted to <c>0</c> as a default-interface-method
    /// so existing input sources keep compiling; the net layer (ServerNet.ToMovementInput) and the client
    /// predictor set it from the move command's impulse.
    /// </summary>
    int Impulse => 0;

    /// <summary>
    /// True when this tick is a CLIENT-SIDE PREDICTION step (and its reconciliation replays), false in the
    /// authoritative simulation (dedicated/listen server, bots, single-player demo). The shared movement code
    /// is non-authoritative-replayable, so anything with side effects the SERVER alone must own — currently the
    /// footstep/landing sounds (QC plays these under <c>#ifdef SVQC</c>) — is suppressed when this is set, so a
    /// predicted landing doesn't fire (and reconciliation replays don't multiply) the sound. Defaulted to
    /// <c>false</c> as a default-interface-method so existing input sources keep compiling; only the client
    /// predictor (<c>EntityMovementStep</c>) sets it true.
    /// </summary>
    bool Predicted => false;
}

/// <summary>A plain, fillable <see cref="IMovementInput"/> (used by the sim, the net layer, and tests).</summary>
public struct MovementInput : IMovementInput
{
    public Vector3 ViewAngles { get; set; }
    public Vector3 MoveValues { get; set; }
    public float FrameTime { get; set; }
    public bool ButtonJump { get; set; }
    public bool ButtonCrouch { get; set; }
    public bool ButtonUse { get; set; }
    public bool ButtonAttack1 { get; set; }
    public bool ButtonAttack2 { get; set; }
    public bool ButtonHook { get; set; }
    public bool ButtonJetpack { get; set; }
    public bool ButtonZoom { get; set; }
    public bool Typing { get; set; }
    public int Impulse { get; set; }
    public bool Predicted { get; set; }
}

/// <summary>
/// The shared player-movement simulation (QC <c>PM_Main</c> / SV_PlayerPhysics, common/physics/).
/// The SAME implementation runs on client (prediction) and server (authority), so it must be
/// deterministic (ADR-0010). The implementation lives in <c>XonoticGodot.Common.Physics</c> and is installed
/// onto <see cref="Movement"/> at boot.
/// </summary>
public interface IPlayerPhysics
{
    /// <summary>Advance one fixed tick of movement for <paramref name="player"/> using <paramref name="input"/>.</summary>
    void Move(Entity player, IMovementInput input);
}

/// <summary>Ambient access to the installed player-movement system (mirrors <c>Services.Api</c>).</summary>
public static class Movement
{
    private sealed class NoMovement : IPlayerPhysics
    {
        public void Move(Entity player, IMovementInput input) { /* no-op until installed */ }
    }

    public static IPlayerPhysics System { get; set; } = new NoMovement();

    public static void Move(Entity player, IMovementInput input) => System.Move(player, input);
}
