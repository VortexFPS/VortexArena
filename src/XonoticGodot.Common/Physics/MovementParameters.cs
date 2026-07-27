using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Physics;

/// <summary>
/// The movement tunables read from cvars once per tick, ported from the Xonotic <c>STAT(MOVEVARS_*)</c>
/// set that <c>Physics_UpdateStats</c> (qcsrc/common/physics/player.qc) fills from the <c>autocvar_sv_*</c>
/// movement variables. This is the C# equivalent of the per-player movevars block: the ported movement
/// functions read everything they need from one of these structs instead of from the global cvar table,
/// which keeps the hot path allocation-free and the math easy to unit-test.
///
/// Defaults below are the stock <b>Xonotic</b> physics set (physicsX.cfg, mirrored by the
/// <c>g_physics_xonotic_*</c> block of physics.cfg). <see cref="FromCvars"/> reads live values via
/// <see cref="Api.Cvars"/> and falls back to these when a cvar is unset (GetFloat returns 0).
/// </summary>
public struct MovementParameters
{
    // --- ground movement ---
    public float MaxSpeed;        // sv_maxspeed            (360)
    public float Accelerate;      // sv_accelerate          (15)
    public float Friction;        // sv_friction            (6)
    public float FrictionSlick;   // sv_friction_slick      (0.5)
    public float StopSpeed;       // sv_stopspeed           (100)
    public float SlickAccelerate; // sv_slickaccelerate     (15)
    public float FrictionOnLand;  // sv_friction_on_land    (0)  -> PHYS_FRICTION_ONLAND

    // --- air movement (Xonotic QW air-accel) ---
    public float MaxAirSpeed;            // sv_maxairspeed              (360)
    public float AirAccelerate;          // sv_airaccelerate            (2)
    public float AirAccelQW;             // sv_airaccel_qw              (-0.8)
    public float AirStrafeAccelQW;       // sv_airstrafeaccel_qw        (-0.95)
    public float AirAccelQWStretchFactor;// sv_airaccel_qw_stretchfactor(2)
    public float AirSpeedLimitNonQW;     // sv_airspeedlimit_nonqw      (900)
    public float AirAccelSidewaysFriction;// sv_airaccel_sideways_friction (0)
    public float MaxAirStrafeSpeed;      // sv_maxairstrafespeed        (100)
    public float AirStrafeAccelerate;    // sv_airstrafeaccelerate      (18)
    public float AirStopAccelerate;      // sv_airstopaccelerate        (3)
    public bool  AirStopAccelerateFull;  // sv_airstopaccelerate_full   (0)

    // --- CPM-style air control ---
    public float AirControl;        // sv_aircontrol         (100)
    public int   AirControlFlags;   // sv_aircontrol_flags   (0)
    public float AirControlPower;   // sv_aircontrol_power   (2)
    public float AirControlPenalty; // sv_aircontrol_penalty (0)

    // --- Warsow bunny (the alternative air-accel selected by warsowbunny_turnaccel != 0) ---
    public float WarsowBunnyTurnAccel;       // sv_warsowbunny_turnaccel        (0 -> disabled)
    public float WarsowBunnyAirForwardAccel; // sv_warsowbunny_airforwardaccel  (1.00001)
    public float WarsowBunnyTopSpeed;        // sv_warsowbunny_topspeed         (925)
    public float WarsowBunnyAccel;           // sv_warsowbunny_accel            (0.1593)
    public float WarsowBunnyBackToSideRatio; // sv_warsowbunny_backtosideratio  (0.8)

    // --- jumping ---
    public float JumpVelocity;       // sv_jumpvelocity        (260)
    public float JumpVelocityCrouch; // sv_jumpvelocity_crouch (0 -> use JumpVelocity)
    public float JumpSpeedCapMin;    // sv_jumpspeedcap_min    (NaN -> disabled)
    public float JumpSpeedCapMax;    // sv_jumpspeedcap_max    (NaN -> disabled)
    public bool  JumpSpeedCapMaxDisableOnRamps; // sv_jumpspeedcap_max_disable_on_ramps (1)
    public bool  TrackCanJump;       // sv_track_canjump       (0)
    public bool  DoubleJump;         // sv_doublejump          (0 -> no air re-jump in stock Xonotic)
    public bool  JumpStep;           // sv_jumpstep            (1 -> allow stepping while jumping)

    // --- gravity / integration ---
    public float Gravity;            // sv_gravity             (800)

    /// <summary>QC <c>autocvar_sv_gameplayfix_q2airaccelerate</c> (stats.qh:396, inline default <b>1</b>;
    /// xonotic-server.cfg:562 also sets 1; replicated to CSQC prediction via <c>MOVEFLAG_Q2AIRACCELERATE</c>,
    /// stats.qh:401 / movetypes.qh:49). Applied INSIDE <c>PM_Accelerate</c> (player.qc:288-289):
    /// <c>wishspeed0 = wishspeed</c> — i.e. the air accel step becomes <c>accel·dt·wishspeed</c> with the
    /// strafe-GeomLerp/duck-clamped wishspeed, NOT the pre-clamp wishspeed0. This is what limits mid-air
    /// STRAFE acceleration in live Xonotic (pure strafe: step from 100, not 350) — with the flag off the
    /// port out-turned Base ~3× (637.6 vs 242.7 deg/s at 400 u/s, StrafeParityProbeTests). Straight-line
    /// air movement is unaffected (wishspeed == wishspeed0 there), which is why the golden corpus never
    /// caught it. An explicitly-set 0 must be honored (a server may legitimately run the Q1 behavior).</summary>
    public bool  GameplayFixQ2AirAccelerate; // sv_gameplayfix_q2airaccelerate (1)

    /// <summary>PORT EXTENSION <c>sv_player_softcollision</c> (default ON): player movement hulls pass through
    /// other players (<c>MoveFilter.NoPlayers</c>) and the server's post-move <c>PlayerSeparation</c> pass pushes
    /// overlapping bodies apart. 0 = stock solid player-vs-player clipping. PlayerPhysics reads the cvar ambiently
    /// (<c>PlayerClipFilter</c>, stamped on the client by MoveVarsBlock.Apply), so this field is informational —
    /// carried for wire completeness like <see cref="NudgeOutOfSolid"/>.</summary>
    public bool  PlayerSoftCollision;        // sv_player_softcollision (1, port extension)

    // --- stair stepping / hull ---
    public float StepHeight;         // sv_stepheight          (31 in Xonotic; spec/task note 34)
    public int   StepDown;           // sv_gameplayfix_stepdown(2)  0=off 1=on 2=on+set-onground
    public float StepDownMaxSpeed;   // sv_gameplayfix_stepdown_maxspeed (400)
    public int   WallFriction;       // sv_wallfriction        (1 in stock: DP engine default, no .cfg sets it; gate is live but the QC _Movetype_WallFriction body is commented out, so the term is a no-op — see PlayerPhysics.WallFriction)

    // --- step-up vertical-velocity limiter (PORT EXTENSION, not in stock Xonotic) -------------------
    // Stock step-up is purely POSITIONAL: when the slide-move runs into a step it lifts the hull up to
    // StepHeight in one tick and preserves the player's existing velocity (the stair_step_up golden trace
    // keeps velocity.z == 0 when walking; a JUMPING player keeps its full jump velocity). The visible
    // result is that jumping into a stair/bump "launches" you to (step height + jump apex). These two knobs
    // let a host scale and/or hard-cap the UPWARD (positive) velocity.z that survives a step-up THIS tick,
    // so the launch can be tamed without breaking stair traversal (the positional lift is untouched).
    // Defaults are a strict no-op (scale 1, max disabled) → byte-identical to stock, golden traces unchanged.
    /// <summary>sv_step_upspeed_scale (default 1 = vanilla): multiplies the positive velocity.z that survives a
    /// step-up. 0 = "step up but add no upward motion" (the player settles on the step instead of launching).</summary>
    public float StepUpSpeedScale;   // sv_step_upspeed_scale  (1)
    /// <summary>sv_step_upspeed_max (default -1 = disabled): hard cap on the positive velocity.z after a step-up.
    /// A value &gt;= 0 clamps the carried upward velocity to at most this many u/s; -1 leaves it uncapped.</summary>
    public float StepUpSpeedMax;     // sv_step_upspeed_max    (-1 -> disabled)

    // --- the high-speed modifier + remaining MOVEVARS breadth (T54) ---
    /// <summary>QC <c>STAT(MOVEVARS_HIGHSPEED) = autocvar_g_movement_highspeed</c> (player.qc:47) — the BASE
    /// per-player top-speed multiplier the PlayerPhysics mutator hooks (Speed powerup, entrap nade, buffs)
    /// multiply onto. Unset reads 1 (xonotic-server.cfg:586 ships 1). PlayerPhysics seeds
    /// <c>player.SpeedMultiplier</c> from this each tick; the fold happens at <see cref="ApplyHighSpeed"/>.</summary>
    public float HighSpeed;          // g_movement_highspeed   (1)
    /// <summary>QC <c>autocvar_g_movement_highspeed_q3_compat</c> (player.qc:52-65, xonotic-server.cfg:587
    /// ships 0): selects WHICH stats the maxspd_mod fold scales — see <see cref="ApplyHighSpeed"/>.</summary>
    public bool  HighSpeedQ3Compat;  // g_movement_highspeed_q3_compat (0)
    /// <summary>DP <c>sv_gameplayfix_nudgeoutofsolid</c> (default ON) — replicated so a server that disables the
    /// embedded-in-solid recovery doesn't desync prediction. PlayerPhysics still reads the cvar ambiently
    /// (stamped by MoveVarsBlock.Apply on the client), so this field is informational.</summary>
    public bool  NudgeOutOfSolid;    // sv_gameplayfix_nudgeoutofsolid (1)
    /// <summary>QC <c>STAT(MOVEVARS_WALLCLIP)</c> (stats.qh:448, autocvar_sv_wallclip — no cfg sets it, 0).
    /// Carried for replication completeness; no consumer in the port yet (doc-only, like QC where only the
    /// movetypes wallclip experiment reads it).</summary>
    public int   WallClip;           // sv_wallclip            (0)
    /// <summary>QC <c>STAT(NOSTEP)</c> (stats.qh:235, cvar sv_nostep, default 0). Doc-only until the
    /// movetypes step path consults it.</summary>
    public bool  NoStep;             // sv_nostep              (0)
    /// <summary>QC <c>STAT(SLICK_APPLYGRAVITY)</c> (stats.qh:365, default 0). PlayerPhysics currently pins the
    /// stock value as a const (PlayerPhysics.SlickApplyGravity) — this field carries the replicated value for a
    /// future live consumer.</summary>
    public bool  SlickApplyGravity;  // sv_slick_applygravity  (0)

    /// <summary>Player bounding box mins. QC <c>autocvar_sv_player_mins</c> = '-16 -16 -24'.</summary>
    public Vector3 PlayerMins;
    /// <summary>Player bounding box maxs. QC <c>autocvar_sv_player_maxs</c> = '16 16 45'.</summary>
    public Vector3 PlayerMaxs;

    /// <summary>
    /// Build the parameter block from live cvars (<paramref name="prefix"/> defaults to <c>"sv_"</c>),
    /// substituting the stock Xonotic defaults whenever a cvar is unset. Mirrors the assignments in
    /// <c>Physics_UpdateStats</c> but without the per-client overrides / high-speed modifier, which the
    /// caller can apply on top (see <see cref="ApplyHighSpeed"/>).
    /// </summary>
    // The prefix+name cvar-name strings, interned once per (prefix, name) pair. FromCvars runs PER PLAYER MOVE
    // (server tick × clients + every client-prediction replay), and a naive `prefix + "maxspeed"` concat on each
    // of the ~45 reads below allocated ~2.5 KB per call — the dominant sim.move GC churn under bot load. The
    // cache is tiny (one prefix in practice).
    //
    // S5 (sv_threaded): this NAME cache is a process-global mutable static shared across threads. The two-world
    // split isolates the cvar STORE per thread, but FromCvars on BOTH the server-sim thread and the main-thread
    // prediction replay still hit THIS cache — so it MUST be thread-safe even though each thread reads a different
    // store. A ConcurrentDictionary makes GetOrAdd atomic (no torn write, no lost entry) at no measurable cost on
    // the single-threaded path (the same lookup, one tiny dictionary, one prefix in practice — the GetOrAdd
    // fast-path is a plain read after the first miss). Drop-in: the key/value types are unchanged.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Prefix, string Name), string> _cvarNameCache = new();

    private static string N(string prefix, string name)
        => _cvarNameCache.TryGetValue((prefix, name), out string? full)
            ? full
            : _cvarNameCache.GetOrAdd((prefix, name), static k => k.Prefix + k.Name);

    public static MovementParameters FromCvars(string prefix = "sv_")
    {
        MovementParameters p = Defaults;

        // Floats: read the cvar, keep the Xonotic default if the cvar is unset (GetFloat -> 0).
        // We treat 0 as "unset" only for variables whose Xonotic value is non-zero; the genuinely-zero
        // Xonotic defaults (e.g. aircontrol_penalty) are simply overwritten by GetFloat's 0 anyway.
        p.MaxSpeed                  = Cvar(N(prefix, "maxspeed"),                 p.MaxSpeed);
        p.Accelerate                = Cvar(N(prefix, "accelerate"),              p.Accelerate);
        p.Friction                  = Cvar(N(prefix, "friction"),                p.Friction);
        p.FrictionSlick             = Cvar(N(prefix, "friction_slick"),          p.FrictionSlick);
        p.StopSpeed                 = Cvar(N(prefix, "stopspeed"),               p.StopSpeed);
        p.SlickAccelerate           = Cvar(N(prefix, "slickaccelerate"),         p.SlickAccelerate);
        p.FrictionOnLand            = CvarRaw(N(prefix, "friction_on_land"),      p.FrictionOnLand);

        p.MaxAirSpeed               = Cvar(N(prefix, "maxairspeed"),             p.MaxAirSpeed);
        p.AirAccelerate             = Cvar(N(prefix, "airaccelerate"),           p.AirAccelerate);
        p.AirAccelQW                = CvarRaw(N(prefix, "airaccel_qw"),           p.AirAccelQW);
        p.AirStrafeAccelQW          = CvarRaw(N(prefix, "airstrafeaccel_qw"),     p.AirStrafeAccelQW);
        p.AirAccelQWStretchFactor   = Cvar(N(prefix, "airaccel_qw_stretchfactor"), p.AirAccelQWStretchFactor);
        p.AirSpeedLimitNonQW        = Cvar(N(prefix, "airspeedlimit_nonqw"),     p.AirSpeedLimitNonQW);
        p.AirAccelSidewaysFriction  = CvarRaw(N(prefix, "airaccel_sideways_friction"), p.AirAccelSidewaysFriction);
        p.MaxAirStrafeSpeed         = Cvar(N(prefix, "maxairstrafespeed"),       p.MaxAirStrafeSpeed);
        p.AirStrafeAccelerate       = Cvar(N(prefix, "airstrafeaccelerate"),     p.AirStrafeAccelerate);
        p.AirStopAccelerate         = Cvar(N(prefix, "airstopaccelerate"),       p.AirStopAccelerate);
        p.AirStopAccelerateFull     = CvarBool(N(prefix, "airstopaccelerate_full"), p.AirStopAccelerateFull);

        p.AirControl                = Cvar(N(prefix, "aircontrol"),              p.AirControl);
        p.AirControlFlags           = (int)CvarRaw(N(prefix, "aircontrol_flags"), p.AirControlFlags);
        p.AirControlPower           = Cvar(N(prefix, "aircontrol_power"),        p.AirControlPower);
        p.AirControlPenalty         = CvarRaw(N(prefix, "aircontrol_penalty"),    p.AirControlPenalty);

        p.WarsowBunnyTurnAccel      = CvarRaw(N(prefix, "warsowbunny_turnaccel"), p.WarsowBunnyTurnAccel);
        p.WarsowBunnyAirForwardAccel= Cvar(N(prefix, "warsowbunny_airforwardaccel"), p.WarsowBunnyAirForwardAccel);
        p.WarsowBunnyTopSpeed       = Cvar(N(prefix, "warsowbunny_topspeed"),    p.WarsowBunnyTopSpeed);
        p.WarsowBunnyAccel          = Cvar(N(prefix, "warsowbunny_accel"),       p.WarsowBunnyAccel);
        p.WarsowBunnyBackToSideRatio= Cvar(N(prefix, "warsowbunny_backtosideratio"), p.WarsowBunnyBackToSideRatio);

        p.JumpVelocity              = Cvar(N(prefix, "jumpvelocity"),            p.JumpVelocity);
        p.JumpVelocityCrouch        = CvarRaw(N(prefix, "jumpvelocity_crouch"),   p.JumpVelocityCrouch);
        p.JumpSpeedCapMin           = CvarNan(N(prefix, "jumpspeedcap_min"),      p.JumpSpeedCapMin);
        p.JumpSpeedCapMax           = CvarNan(N(prefix, "jumpspeedcap_max"),      p.JumpSpeedCapMax);
        p.JumpSpeedCapMaxDisableOnRamps = CvarBool(N(prefix, "jumpspeedcap_max_disable_on_ramps"), p.JumpSpeedCapMaxDisableOnRamps);
        p.TrackCanJump              = CvarBool(N(prefix, "track_canjump"),        p.TrackCanJump);
        p.DoubleJump                = CvarBool(N(prefix, "doublejump"),           p.DoubleJump);
        p.JumpStep                  = CvarBool(N(prefix, "jumpstep"),             p.JumpStep);

        p.Gravity                   = Cvar(N(prefix, "gravity"),                 p.Gravity);
        p.GameplayFixQ2AirAccelerate = CvarBool(N(prefix, "gameplayfix_q2airaccelerate"), p.GameplayFixQ2AirAccelerate);
        p.PlayerSoftCollision       = CvarBool(N(prefix, "player_softcollision"), p.PlayerSoftCollision);

        p.StepHeight                = Cvar(N(prefix, "stepheight"),              p.StepHeight);
        p.StepDown                  = (int)CvarRaw(N(prefix, "gameplayfix_stepdown"), p.StepDown);
        p.StepDownMaxSpeed          = Cvar(N(prefix, "gameplayfix_stepdown_maxspeed"), p.StepDownMaxSpeed);
        p.WallFriction              = (int)CvarRaw(N(prefix, "wallfriction"),     p.WallFriction);
        // EXISTS-gated (CvarRaw): a configured 0 is meaningful (scale 0 / max 0 both kill upward step velocity),
        // so an explicit 0 must read back as 0; only a genuinely-unset cvar falls back to the no-op default.
        p.StepUpSpeedScale          = CvarRaw(N(prefix, "step_upspeed_scale"),     p.StepUpSpeedScale);
        p.StepUpSpeedMax            = CvarRaw(N(prefix, "step_upspeed_max"),        p.StepUpSpeedMax);

        // T54 breadth. g_movement_highspeed: unset must read 1, and a deliberate 0 must be honored (it would
        // freeze movement, but that's what the cvar says) — so gate on the STRING being present, not the float.
        p.HighSpeed                 = Api.Cvars.GetString("g_movement_highspeed").Length == 0
                                        ? p.HighSpeed
                                        : Api.Cvars.GetFloat("g_movement_highspeed");
        p.HighSpeedQ3Compat         = CvarBool("g_movement_highspeed_q3_compat", p.HighSpeedQ3Compat);
        p.NudgeOutOfSolid           = Api.Cvars.GetString(N(prefix, "gameplayfix_nudgeoutofsolid")) != "0"; // DP default ON
        p.WallClip                  = (int)CvarRaw(N(prefix, "wallclip"),          p.WallClip);
        p.NoStep                    = CvarBool(N(prefix, "nostep"),                p.NoStep);
        p.SlickApplyGravity         = CvarBool(N(prefix, "slick_applygravity"),    p.SlickApplyGravity);

        // Hull: QC reads these from autocvar_sv_player_mins/maxs (vector cvars). We don't have a vector
        // cvar accessor on the facade, so the Xonotic hull is taken from Defaults. A host that wants to
        // override the hull can set the fields after FromCvars().
        // (PlayerMins/PlayerMaxs already carry the Xonotic defaults via `Defaults`.)

        return p;
    }

    /// <summary>
    /// The stock Xonotic movement set (physicsX.cfg / g_physics_xonotic_*). Also serves as the fallback
    /// table for <see cref="FromCvars"/>.
    /// </summary>
    public static readonly MovementParameters Defaults = new()
    {
        MaxSpeed = 360f,
        Accelerate = 15f,
        Friction = 6f,
        FrictionSlick = 0.5f,
        StopSpeed = 100f,
        SlickAccelerate = 15f,
        FrictionOnLand = 0f,

        MaxAirSpeed = 360f,
        AirAccelerate = 2f,
        AirAccelQW = -0.8f,
        AirStrafeAccelQW = -0.95f,
        AirAccelQWStretchFactor = 2f,
        AirSpeedLimitNonQW = 900f,
        AirAccelSidewaysFriction = 0f,
        MaxAirStrafeSpeed = 100f,
        AirStrafeAccelerate = 18f,
        AirStopAccelerate = 3f,
        AirStopAccelerateFull = false,

        AirControl = 100f,
        AirControlFlags = 0,
        AirControlPower = 2f,
        AirControlPenalty = 0f,

        WarsowBunnyTurnAccel = 0f,
        WarsowBunnyAirForwardAccel = 1.00001f,
        WarsowBunnyTopSpeed = 925f,
        WarsowBunnyAccel = 0.1593f,
        WarsowBunnyBackToSideRatio = 0.8f,

        JumpVelocity = 260f,
        JumpVelocityCrouch = 0f,
        JumpSpeedCapMin = float.NaN,
        JumpSpeedCapMax = float.NaN,
        JumpSpeedCapMaxDisableOnRamps = true,
        TrackCanJump = false,
        DoubleJump = false,  // sv_doublejump 0 — no air re-jump in stock Xonotic
        JumpStep = true,     // sv_jumpstep 1 — allow the up/forward step while airborne

        Gravity = 800f,
        GameplayFixQ2AirAccelerate = true, // sv_gameplayfix_q2airaccelerate — QC autocvar inline default 1
        PlayerSoftCollision = true,        // sv_player_softcollision — port extension, default ON (Vortex Arena)

        StepHeight = 31f, // Xonotic physicsX.cfg value; task brief mentions 34 (the Nexuiz/Vecxis value).
        StepDown = 2,
        StepDownMaxSpeed = 400f,
        StepUpSpeedScale = 1f,  // sv_step_upspeed_scale 1  — no-op (keep full upward velocity through a step)
        StepUpSpeedMax = -1f,   // sv_step_upspeed_max  -1  — disabled (no upward-velocity cap after a step)
        WallFriction = 1, // sv_wallfriction 1 — Darkplaces engine default (QC autocvar_sv_wallfriction has no
                          // initializer and no Xonotic .cfg sets it; a live engine dump confirms 1). The net effect
                          // is still NO wall friction: the stock QC _Movetype_WallFriction body is commented out, and
                          // PlayerPhysics.WallFriction mirrors that no-op — so the corpus stays byte-identical.

        HighSpeed = 1f,            // g_movement_highspeed 1 (xonotic-server.cfg:586)
        HighSpeedQ3Compat = false, // g_movement_highspeed_q3_compat 0 (:587)
        NudgeOutOfSolid = true,    // sv_gameplayfix_nudgeoutofsolid — DP default ON
        WallClip = 0,              // sv_wallclip — no cfg sets it
        NoStep = false,            // sv_nostep 0
        SlickApplyGravity = false, // sv_slick_applygravity 0

        PlayerMins = new Vector3(-16f, -16f, -24f),
        PlayerMaxs = new Vector3(16f, 16f, 45f),
    };

    /// <summary>
    /// Apply the <c>maxspd_mod</c> fold the way <c>Physics_UpdateStats</c> does (player.qc:50-65,116-119):
    /// MaxSpeed always scales ("also slow walking", :51); then — when NOT q3-compat (the stock path, :58-64) —
    /// AirSpeedLimitNonQW scales and the two QW accel fractions are re-stretched via
    /// <see cref="PMAccelerate.AdjustAirAccelQW"/> (AirStrafeAccelQW only when non-zero, the QC ternary :61-63);
    /// when q3-compat (:54-57,:116-117) the QW vars stay RAW and MaxAirSpeed scales instead. NOTE
    /// MaxAirStrafeSpeed is scaled in NEITHER branch (player.qc:115 has no ×mod — an earlier port revision
    /// wrongly scaled it here). Call after <see cref="FromCvars"/>/<see cref="FromValues"/> when the
    /// effective multiplier (HighSpeed × mutator SpeedMultiplier) differs from 1.
    /// </summary>
    public void ApplyHighSpeed(float maxspeedMod)
    {
        if (maxspeedMod == 1f) return;
        MaxSpeed *= maxspeedMod;
        if (HighSpeedQ3Compat)
        {
            MaxAirSpeed *= maxspeedMod;     // player.qc:117 — q3compat scales maxairspeed, leaves the QW vars raw
        }
        else
        {
            AirSpeedLimitNonQW *= maxspeedMod;
            AirAccelQW = PMAccelerate.AdjustAirAccelQW(AirAccelQW, maxspeedMod);
            if (AirStrafeAccelQW != 0f)
                AirStrafeAccelQW = PMAccelerate.AdjustAirAccelQW(AirStrafeAccelQW, maxspeedMod);
        }
    }

    // =================================================================================================
    //  Wire-vector decode + the per-player resolution seams (T54)
    // =================================================================================================

    // CLR gotcha: a struct may not declare a static FIELD of Nullable<itself> (generic layout cycle →
    // TypeLoadException at runtime), so the override storage lives in a nested class behind properties.
    private static class OverrideStore
    {
        internal static MovementParameters? Prediction;
        internal static System.Func<Entity, float[]?>? Provider;
    }

    /// <summary>
    /// CLIENT-side per-player physics override: when the server replicates a preset-RESOLVED movevar vector
    /// (g_physics_clientselect — the per-peer block after the global MoveVarsBlock in the snapshot), the net
    /// layer parks <c>FromValues(resolved)</c> here and the PREDICTED move (<c>input.Predicted</c>) reads it
    /// instead of the cvar store. Null (the default) = stock behavior, byte-identical to <see cref="FromCvars"/>.
    /// Cleared on disconnect (ClientNet.Dispose) — reset in tests that set it.
    /// </summary>
    public static MovementParameters? PredictionOverride
    {
        get => OverrideStore.Prediction;
        set => OverrideStore.Prediction = value;
    }

    /// <summary>
    /// SERVER-side per-player physics resolver: returns the entity's preset-resolved movevar vector (in
    /// MoveVarsBlock order) or null for "no per-player physics" (the stock path). Installed by ServerNet when
    /// hosting; null (the default) = every player moves on the global cvars. Cleared in ServerNet.Dispose.
    /// </summary>
    public static System.Func<Entity, float[]?>? PresetProvider
    {
        get => OverrideStore.Provider;
        set => OverrideStore.Provider = value;
    }

    /// <summary>
    /// The per-tick parameter read for <see cref="PlayerPhysics.Move"/> — QC's per-player stat read. The
    /// PREDICTED leg (client prediction replays) takes <see cref="PredictionOverride"/> when set; the
    /// authoritative leg consults <see cref="PresetProvider"/>; both default to the ambient
    /// <see cref="FromCvars"/> — the pre-T54 path, so a host with clientselect off is byte-identical.
    /// </summary>
    public static MovementParameters Resolve(Entity player, bool predicted)
    {
        if (predicted)
            return PredictionOverride ?? FromCvars();
        return PresetProvider?.Invoke(player) is { } v ? FromValues(v) : FromCvars();
    }

    /// <summary>
    /// Build the parameter block positionally from a replicated <c>MoveVarsBlock</c> vector (the wire twin of
    /// <see cref="FromCvars"/> — keep the assignment order in lockstep with <c>MoveVarsBlock.MovementCvars</c>).
    /// A PRESENT wire slot is AUTHORITATIVE and assigned verbatim: <c>MoveVarsBlock.Capture</c>/<c>CaptureResolved</c>
    /// always emit every slot (they loop the full <c>MovementCvars</c> length), so a 0/false on the wire is a real
    /// configured 0/false — NOT "unset" — and must survive (sv_gameplayfix_stepdown 0, sv_airstrafeaccel_qw 0 on the
    /// cpma/quake3/warsow presets, else stepdown+airstrafe would snap back to the Xonotic default and diverge from
    /// Base, where player.qc:23-42 Physics_ClientOption returns the cvar value verbatim). The jumpspeedcaps already
    /// encode "disabled" as NaN (MoveVarsBlock.CaptureOne), so a real 0 (the xdf/quake3/cpma presets) passes through
    /// as a genuine 0 cap. The <see cref="Defaults"/> fallback applies ONLY to the short-vector tail an older peer
    /// omits — guarded by the <c>i &lt; v.Length</c> length check, so verbatim-on-present loses nothing there.
    /// </summary>
    public static MovementParameters FromValues(float[] v)
    {
        MovementParameters p = Defaults;
        int i = 0;
        // F: a "magnitude" field whose stock default is non-zero — a 0 on the wire means the server's cvar was
        // unset (Capture's bare GetFloat reads 0 for an absent cvar), so fall back to the port default. This MUST
        // mirror FromCvars' Cvar helper (value==0 → fallback) for the wire/cvar-equivalence contract to hold.
        float F(float fallback) { float x = i < v.Length ? v[i] : fallback; i++; return x != 0f ? x : fallback; }
        // Raw/B: fields that can legitimately be a configured 0/false (airstrafeaccel_qw, stepdown, the bool set).
        // A PRESENT wire slot is AUTHORITATIVE and assigned verbatim — Capture/CaptureResolved always emit every
        // slot, so a 0/false here is a real configured value (the cpma/quake3/warsow presets set stepdown 0,
        // airstrafeaccel_qw 0; player.qc:23-42 returns the cvar value verbatim) and must NOT snap back to the
        // Xonotic default. The Defaults fallback applies ONLY to the short-vector tail an older peer omits, guarded
        // by the i<v.Length length check.
        float Raw(float fallback) { float x = i < v.Length ? v[i] : fallback; i++; return x; }
        bool B(bool fallback) { bool x = i < v.Length ? v[i] != 0f : fallback; i++; return x; }
        float Nan() { float x = i < v.Length ? v[i] : float.NaN; i++; return x; } // NaN sentinel already on the wire

        p.MaxSpeed = F(p.MaxSpeed); p.Accelerate = F(p.Accelerate); p.Friction = F(p.Friction);
        p.FrictionSlick = F(p.FrictionSlick); p.StopSpeed = F(p.StopSpeed);
        p.SlickAccelerate = F(p.SlickAccelerate); p.FrictionOnLand = Raw(p.FrictionOnLand);

        p.MaxAirSpeed = F(p.MaxAirSpeed); p.AirAccelerate = F(p.AirAccelerate);
        p.AirAccelQW = Raw(p.AirAccelQW); p.AirStrafeAccelQW = Raw(p.AirStrafeAccelQW);
        p.AirAccelQWStretchFactor = F(p.AirAccelQWStretchFactor); p.AirSpeedLimitNonQW = F(p.AirSpeedLimitNonQW);
        p.AirAccelSidewaysFriction = Raw(p.AirAccelSidewaysFriction);
        p.MaxAirStrafeSpeed = F(p.MaxAirStrafeSpeed); p.AirStrafeAccelerate = F(p.AirStrafeAccelerate);
        p.AirStopAccelerate = F(p.AirStopAccelerate); p.AirStopAccelerateFull = B(p.AirStopAccelerateFull);

        p.AirControl = F(p.AirControl); p.AirControlFlags = (int)Raw(p.AirControlFlags);
        p.AirControlPower = F(p.AirControlPower); p.AirControlPenalty = Raw(p.AirControlPenalty);

        p.WarsowBunnyTurnAccel = Raw(p.WarsowBunnyTurnAccel);
        p.WarsowBunnyAirForwardAccel = F(p.WarsowBunnyAirForwardAccel);
        p.WarsowBunnyTopSpeed = F(p.WarsowBunnyTopSpeed); p.WarsowBunnyAccel = F(p.WarsowBunnyAccel);
        p.WarsowBunnyBackToSideRatio = F(p.WarsowBunnyBackToSideRatio);

        p.JumpVelocity = F(p.JumpVelocity); p.JumpVelocityCrouch = Raw(p.JumpVelocityCrouch);
        p.JumpSpeedCapMin = Nan(); p.JumpSpeedCapMax = Nan();
        p.JumpSpeedCapMaxDisableOnRamps = B(p.JumpSpeedCapMaxDisableOnRamps);
        p.TrackCanJump = B(p.TrackCanJump); p.DoubleJump = B(p.DoubleJump); p.JumpStep = B(p.JumpStep);

        p.Gravity = F(p.Gravity);

        p.StepHeight = F(p.StepHeight); p.StepDown = (int)Raw(p.StepDown);
        p.StepDownMaxSpeed = F(p.StepDownMaxSpeed); p.WallFriction = (int)Raw(p.WallFriction);

        // v7 tail: HighSpeed rides the wire with unset→1 already applied at Capture, so honor a genuine 0.
        p.HighSpeed = i < v.Length ? v[i] : p.HighSpeed; i++;
        p.HighSpeedQ3Compat = B(p.HighSpeedQ3Compat);
        p.NudgeOutOfSolid = i < v.Length ? v[i] != 0f : p.NudgeOutOfSolid; i++; // Capture sends unset→1
        p.WallClip = (int)Raw(p.WallClip);
        p.NoStep = B(p.NoStep);
        p.SlickApplyGravity = B(p.SlickApplyGravity);

        // step-up velocity limiter tail (PORT EXTENSION): a PRESENT slot is authoritative and assigned verbatim
        // (Capture sends the unset→default — 1 / -1 — so a real configured 0 survives); the Defaults fallback
        // applies only to the short-vector tail an older peer omits (guarded by i < v.Length).
        p.StepUpSpeedScale = i < v.Length ? v[i] : p.StepUpSpeedScale; i++;
        p.StepUpSpeedMax   = i < v.Length ? v[i] : p.StepUpSpeedMax; i++;

        // v8 tail: sv_gameplayfix_q2airaccelerate (default ON; Capture sends unset→1 via AbsentDefaults, so a
        // configured 0 — the Q1 behavior — survives the wire; absent tail from an older peer keeps the default).
        p.GameplayFixQ2AirAccelerate = B(p.GameplayFixQ2AirAccelerate);

        // v9 tail (port extension): sv_player_softcollision (default ON; Capture sends unset→1 via AbsentDefaults,
        // so a configured 0 — stock solid players — survives the wire).
        p.PlayerSoftCollision = B(p.PlayerSoftCollision);

        return p;
    }

    // --- cvar read helpers -------------------------------------------------------------------------

    // For "magnitude" cvars whose Xonotic default is non-zero: treat a 0 read (cvar absent) as "use default".
    private static float Cvar(string name, float fallback)
    {
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }

    // CVAR_TYPEFLAG_EXISTS gate (player.qc:31/38) — the same EXISTS semantics PhysicsPreset.Exists uses when no
    // explicit `exists` func is passed: an absent cvar reads "" (CvarService.GetString of a missing key), a
    // present one reads a non-empty string EVEN when its value is "0". ICvarService has no Has(), so probe the
    // string length exactly like PhysicsPreset.Exists' null-fallback branch.
    private static bool Exists(string name) => Api.Cvars.GetString(name).Length != 0;

    // For cvars that can legitimately be 0 or negative (qw fractions, penalties, flags, on_land, stepdown):
    // an explicitly-set 0 (sv_gameplayfix_stepdown 0, sv_airstrafeaccel_qw 0 on the cpma/quake3/warsow presets)
    // must read back 0 — gate the fallback on cvar EXISTENCE, not value==0, so only a genuinely-absent cvar uses
    // the port default (player.qc:23-42 Physics_ClientOption returns the cvar value verbatim, a real 0 is real).
    private static float CvarRaw(string name, float fallback)
        => Exists(name) ? Api.Cvars.GetFloat(name) : fallback;

    private static bool CvarBool(string name, bool fallback)
        => Exists(name) ? Api.Cvars.GetFloat(name) != 0f : fallback;

    // jumpspeedcap_min/max default to "nan" in Xonotic; a missing cvar reads 0 which would wrongly enable
    // the cap, so we keep NaN unless the host registered a finite value.
    private static float CvarNan(string name, float fallback)
    {
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }
}
