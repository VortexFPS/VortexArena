using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Walljump mutator — port of common/mutators/mutator/walljump/walljump.qc. Tapping jump while
/// airborne and next to a wall bounces the player off it (away from the wall, with an upward boost).
/// Enabled by the <c>g_walljump</c> cvar.
///
/// Ported: the nearby-wall trace (PlayerTouchWall, skipping NOIMPACT surfaces), the delay gate, the
/// airborne / alive / unfrozen guards, the off-wall velocity impulse with the crouch-held downward slam,
/// and the jump grant via the PlayerJump multijump out-flag, the per-model jump voice, and the smoke-ring
/// particle emitted at the wall contact point.
/// </summary>
[Mutator]
public sealed class WalljumpMutator : MutatorBase
{
    /// <summary>QC STAT(WALLJUMP_FORCE) — magnitude of the off-wall horizontal push.</summary>
    public float WallForce = 300f;

    /// <summary>QC STAT(WALLJUMP_VELOCITY_XY_FACTOR) — horizontal velocity is divided by this after the push.</summary>
    public float XyFactor = 1.05f;

    /// <summary>QC STAT(WALLJUMP_VELOCITY_Z_FACTOR) — multiplier on jump velocity for the upward boost.</summary>
    public float ZFactor = 1f;

    /// <summary>QC STAT(WALLJUMP_DELAY) — minimum seconds between wall jumps.</summary>
    public float Delay = 0.3f;

    public WalljumpMutator() => NetName = "walljump";

    // QC SVQC: REGISTER_MUTATOR(walljump, autocvar_g_walljump);
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_walljump") != 0f;

    // QC: MENUQC describe() — the per-mutator guide page text (common/mutators/mutator/walljump/walljump.qc:77-85).
    // Two paragraphs; the %s fills resolve to COLORED_NAME(this) = "Wall jump" and strcat("^3", _("jump"), "^7") = "jump".
    public override string? GuideDescription =>
        "Wall jump is a mutator that enables jumping off walls for added mobility. " +
        "To do a wall jump, tap jump when against a wall.\n\n" +
        "This mutator adds a bit of versatility, allowing for more dynamic and unpredictable movement.";

    private HookHandler<MutatorHooks.PlayerJumpArgs>? _onJump;

    public override void Hook()
    {
        _onJump ??= OnPlayerJump;
        MutatorHooks.PlayerJump.Add(_onJump);

        if (Api.Services is not null)
        {
            // QC STATs read the autocvar unconditionally — 0 is a legal, meaningful value
            // (e.g. g_walljump_velocity_z_factor 0 for a pure-horizontal wall jump), so honor an
            // explicitly-set cvar even when it is 0 rather than masking it with the C# default.
            // A registered/shipped cvar (mutators.cfg ships all four) returns a non-empty string;
            // only fall back to the C# field default when the cvar is absent/unregistered.
            if (IsSet("g_walljump_force")) WallForce = Api.Cvars.GetFloat("g_walljump_force");
            if (IsSet("g_walljump_velocity_xy_factor")) XyFactor = Api.Cvars.GetFloat("g_walljump_velocity_xy_factor");
            if (IsSet("g_walljump_velocity_z_factor")) ZFactor = Api.Cvars.GetFloat("g_walljump_velocity_z_factor");
            if (IsSet("g_walljump_delay")) Delay = Api.Cvars.GetFloat("g_walljump_delay");

            static bool IsSet(string name) => !string.IsNullOrEmpty(Api.Cvars.GetString(name));
        }
    }

    public override void Unhook()
    {
        if (_onJump is not null) MutatorHooks.PlayerJump.Remove(_onJump);
    }

    // MUTATOR_HOOKFUNCTION(walljump, PlayerJump)
    private bool OnPlayerJump(ref MutatorHooks.PlayerJumpArgs args)
    {
        Entity player = args.Player;
        if (Api.Services is null) return false;
        float now = Api.Clock.Time;

        // QC guards: delay elapsed, airborne, walk-ish movetype, jump not held, not frozen, alive.
        if (now - player.LastWallJumpTime <= Delay) return false;
        if (player.OnGround) return false;
        if (player.MoveType is MoveType.None or MoveType.Follow or MoveType.Fly or MoveType.Noclip) return false;
        bool jumpHeld = (player.Flags & EntFlags.JumpReleased) == 0;
        if (jumpHeld) return false;
        if (player.DeadState != DeadFlag.No) return false;

        // QC: !STAT(FROZEN, player) && !StatusEffects_active(STATUSEFFECT_Frozen, player).
        var frozen = StatusEffectsCatalog.Frozen;
        if (frozen is not null && StatusEffectsCatalog.Has(player, frozen)) return false;

        Vector3 planeNormal = PlayerTouchWall(player, out Vector3 hitPos);
        if (planeNormal == Vector3.Zero)
            return false;

        // QC off-wall impulse.
        float jumpVel = JumpVelocity();
        Vector3 v = player.Velocity;
        v.X += planeNormal.X * WallForce; v.X /= XyFactor;
        v.Y += planeNormal.Y * WallForce; v.Y /= XyFactor;
        v.Z = jumpVel * ZFactor;
        // QC: if crouch held, invert the z (slam down off the wall).
        if (player.ButtonCrouch) v.Z *= -1f;
        player.Velocity = v;

        // QC #ifdef SVQC (walljump.qc:62-68): the stamps, the smoke ring, the jump voice and the anim
        // action all run SERVER-SIDE ONLY. Base registers walljump on CSQC too (so the velocity impulse
        // above predicts), but the block below is #ifdef SVQC and never runs in client prediction.
        // The port shares one static PlayerJump chain across both processes, so guard on !Predicted:
        // on the predicting client we apply only the (shared) impulse and let the server network the
        // effect/sound — emitting them here too would DOUBLE the server's networked smoke ring + voice
        // (a client-process EffectEmitter.Emit / SoundSystem call renders/plays locally via the client's
        // RenderSink and sound backend).
        if (!args.Predicted)
        {
            player.LastWallJumpTime = now;
            // QC: player.oldvelocity = player.velocity — the POST-impulse velocity, an anti-stick reference.
            // (Earlier the port wrote OldOrigin = Origin, which is the WRONG field: OldOrigin is the engine's
            // render-interpolation anchor, so stamping it cancelled interpolation on every wall jump. The shared
            // Entity.OldVelocity field — declared on the vehicles partial (Vehicles/VehicleCommon.cs) — carries the
            // faithful QC .oldvelocity value.)
            player.OldVelocity = player.Velocity;

            // QC: Send_Effect(EFFECT_SMOKE_RING, trace_endpos, plane_normal, 5) — smoke ring at the wall
            // contact point, oriented along the plane normal.
            EffectEmitter.Emit("SMOKE_RING", hitPos, planeNormal, 5);

            // QC: PlayerSound(player, playersound_jump, CH_PLAYER, VOL_BASE, VOICETYPE_PLAYERSOUND, 1) — the
            // per-model jump voice. "jump" resolves through the player's model .sounds manifest
            // (QC LoadPlayerSounds: jump -> sound/player/<pack>/player/jump), falling back to the default pack.
            // Passing the model's .sounds datafile (NOT null) is what makes the cue resolve to a shipped asset.
            SoundSystem.PlayPlayerSound(player, "jump", Sounds.ModelSoundsFile(player.Model, (int)player.Skin),
                SoundLevels.VolBase, SoundLevels.AttenNorm);
            // QC also: animdecide_setaction(player, ANIMACTION_JUMP, true) — no anim-action seam in the port yet (see todos).
        }

        args.Multijump = true; // QC: M_ARGV(2, bool) = true
        return false;
    }

    /// <summary>
    /// QC PlayerTouchWall(this): tracebox forward/back/left/right a short distance; return the plane normal
    /// of the first steep (near-vertical) non-NOIMPACT surface within range (and its contact point), else zero.
    /// </summary>
    private static Vector3 PlayerTouchWall(Entity player, out Vector3 hitPos)
    {
        const float dist = 10f;       // QC dist
        const float maxNormal = 0.2f; // QC max_normal — surface counts as "wall" if normal.z below this
        const float scaler = 100f;    // QC scaler — how far to cast

        Vector3 start = player.Origin;
        QMath.AngleVectors(player.Angles, out Vector3 forward, out Vector3 right, out _);

        Vector3 found = Vector3.Zero, foundPos = Vector3.Zero;

        bool Try(Vector3 target)
        {
            TraceResult tr = Api.Trace.Trace(start, player.Mins, player.Maxs, target, MoveFilter.Normal, player);
            if (tr.Fraction < 1f
                && (player.Origin - tr.EndPos).Length() < dist
                && tr.PlaneNormal.Z < maxNormal
                && (tr.DpHitQ3SurfaceFlags & MutatorConstants.Q3SurfaceFlagNoImpact) == 0)
            {
                found = tr.PlaneNormal;
                foundPos = tr.EndPos;
                return true;
            }
            return false;
        }

        _ = Try(start + forward * scaler)
            || Try(start - forward * scaler)
            || Try(start + right * scaler)
            || Try(start - right * scaler);

        hitPos = foundPos;
        return found;
    }

    private static float JumpVelocity()
    {
        if (Api.Services is null) return 260f;
        float v = Api.Cvars.GetFloat("sv_jumpvelocity");
        return v != 0f ? v : 260f;
    }
}
