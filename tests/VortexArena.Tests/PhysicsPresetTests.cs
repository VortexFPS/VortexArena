using System;
using XonoticGodot.Common.Services;
using XonoticGodot.Common.Physics;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for <see cref="PhysicsPreset"/> — the port of <c>Physics_Valid</c>/<c>Physics_ClientOption</c>
/// (qcsrc/common/physics/player.qc:18-42) and the vector-level per-peer resolution
/// (<see cref="MoveVarsBlock.CaptureResolved"/>) used by the v7 per-client physics block (T54).
/// </summary>
[Collection("GlobalState")]
public class PhysicsPresetTests
{
    public PhysicsPresetTests()
    {
        // statics other tests (or a leaked net session) may have left behind
        MovementParameters.PredictionOverride = null;
        MovementParameters.PresetProvider = null;
    }

    /// <summary>A store with the clientselect knobs + the real cpma/xdf preset rows from physics.cfg.</summary>
    private static CvarService NewStore(string clientSelect = "1")
    {
        var c = new CvarService();
        c.Set("g_physics_clientselect", clientSelect);
        c.Set("g_physics_clientselect_options", "xonotic nexuiz vecxis quake quake2 quake3 cpma bones xdf");
        // cpma rows (physics.cfg:256-291) — the spot-check targets
        c.Set("g_physics_cpma_aircontrol", "150");
        c.Set("g_physics_cpma_aircontrol_flags", "5");
        c.Set("g_physics_cpma_accelerate", "15");
        c.Set("g_physics_cpma_maxspeed", "320");
        c.Set("g_physics_cpma_gravity", "750");
        c.Set("g_physics_cpma_jumpspeedcap_min", "0");      // REAL zero — must NOT become NaN
        c.Set("g_physics_cpma_jumpspeedcap_max", "nan");    // the literal "nan" → NaN (cap disabled)
        c.Set("g_physics_cpma_track_canjump", "1");
        // xdf rows (physics.cfg:207-208)
        c.Set("g_physics_xdf_jumpspeedcap_min", "0");
        c.Set("g_physics_xdf_jumpspeedcap_max", "0.5");
        // the sv_* globals presets fall back to (stock Xonotic values from physicsX.cfg — sv_friction 6 is the
        // one the cpma set does NOT override, so it must be seeded here for the missing-row fallback to land on
        // the real default rather than an unset 0).
        c.Set("sv_maxspeed", "360");
        c.Set("sv_gravity", "800");
        c.Set("sv_aircontrol", "100");
        c.Set("sv_friction", "6");          // physicsX.cfg:12 — no g_physics_cpma_friction row → falls back here
        return c;
    }

    [Fact]
    public void ClientSelectOff_ReturnsTheFallback_ForEveryOption()
    {
        CvarService c = NewStore(clientSelect: "0");
        foreach ((string option, string fallbackCvar) in PhysicsPreset.Options)
        {
            float fallback = c.GetFloat(fallbackCvar);
            Assert.Equal(fallback, PhysicsPreset.Resolve(c, "cpma", option, fallback, c.Has));
        }
    }

    [Fact]
    public void ValidPreset_ResolvesThePresetCvar()
    {
        CvarService c = NewStore();
        Assert.Equal(150f, PhysicsPreset.Resolve(c, "cpma", "aircontrol", 100f, c.Has));
        Assert.Equal(5f, PhysicsPreset.Resolve(c, "cpma", "aircontrol_flags", 0f, c.Has));
        Assert.Equal(320f, PhysicsPreset.Resolve(c, "cpma", "maxspeed", 360f, c.Has));
        Assert.Equal(750f, PhysicsPreset.Resolve(c, "cpma", "gravity", 800f, c.Has));
    }

    [Fact]
    public void JumpSpeedCap_NanString_IsNaN_And_RealZero_StaysZero()
    {
        CvarService c = NewStore();
        // cpma: min is a REAL 0 (a genuine 0×jumpheight floor), max is the "nan" disable sentinel.
        Assert.Equal(0f, PhysicsPreset.Resolve(c, "cpma", "jumpspeedcap_min", float.NaN, c.Has));
        Assert.True(float.IsNaN(PhysicsPreset.Resolve(c, "cpma", "jumpspeedcap_max", float.NaN, c.Has)));
        // xdf: both REAL numbers.
        Assert.Equal(0f, PhysicsPreset.Resolve(c, "xdf", "jumpspeedcap_min", float.NaN, c.Has));
        Assert.Equal(0.5f, PhysicsPreset.Resolve(c, "xdf", "jumpspeedcap_max", float.NaN, c.Has));
    }

    [Fact]
    public void InvalidOrUnlistedSet_FallsThroughToTheDefaultChain()
    {
        CvarService c = NewStore();
        // "bogus" is not in g_physics_clientselect_options → chain 2 skipped, no clientselect_default → fallback.
        Assert.Equal(360f, PhysicsPreset.Resolve(c, "bogus", "maxspeed", 360f, c.Has));
        // ""/"default" are never valid (Physics_Valid, player.qc:18-21).
        Assert.False(PhysicsPreset.Valid(c, ""));
        Assert.False(PhysicsPreset.Valid(c, "default"));
        Assert.True(PhysicsPreset.Valid(c, "cpma"));
        Assert.False(PhysicsPreset.Valid(c, "cp")); // whole-word match (strhasword), not substring
    }

    [Fact]
    public void ClientselectDefault_AppliesToUnlistedSets_AndToClientsWithoutAPreset()
    {
        CvarService c = NewStore();
        // a "secret" set NOT in the options list, forced via g_physics_clientselect_default — QC deliberately
        // skips Physics_Valid for the default (player.qc:36).
        c.Set("g_physics_secret_maxspeed", "555");
        c.Set("g_physics_clientselect_default", "secret");
        Assert.Equal(555f, PhysicsPreset.Resolve(c, "", "maxspeed", 360f, c.Has));        // no client preset
        Assert.Equal(555f, PhysicsPreset.Resolve(c, "bogus", "maxspeed", 360f, c.Has));   // invalid client preset
        // a VALID client preset still wins over the default (chain order, player.qc:28-40).
        Assert.Equal(320f, PhysicsPreset.Resolve(c, "cpma", "maxspeed", 360f, c.Has));
    }

    [Fact]
    public void MissingPresetCvar_FallsThroughTheChain()
    {
        CvarService c = NewStore();
        // cpma defines no "friction" row in this store → EXISTS fails → fallback (the sv_* default).
        Assert.Equal(6f, PhysicsPreset.Resolve(c, "cpma", "friction", 6f, c.Has));
        // …and with a default set that DOES define it, the default chain catches it.
        c.Set("g_physics_secret_friction", "8");
        c.Set("g_physics_clientselect_default", "secret");
        Assert.Equal(8f, PhysicsPreset.Resolve(c, "cpma", "friction", 6f, c.Has));
    }

    [Fact]
    public void OptionFor_MapsExactlyThePresetResolvableMovevars()
    {
        // The two non-obvious option names (player.qc:141,148).
        Assert.Equal("stepdown", PhysicsPreset.OptionFor("sv_gameplayfix_stepdown"));
        Assert.Equal("doublejump", PhysicsPreset.OptionFor("sv_doublejump"));
        Assert.Equal("maxspeed", PhysicsPreset.OptionFor("sv_maxspeed"));
        // Globals (stats.qh entries WITH a cvar expression) are NOT preset-resolvable.
        Assert.Null(PhysicsPreset.OptionFor("sv_jumpstep"));
        Assert.Null(PhysicsPreset.OptionFor("sv_friction_on_land"));
        Assert.Null(PhysicsPreset.OptionFor("sv_wallfriction"));
        Assert.Null(PhysicsPreset.OptionFor("sv_gameplayfix_stepdown_maxspeed"));
        Assert.Null(PhysicsPreset.OptionFor("g_movement_highspeed"));
        Assert.Null(PhysicsPreset.OptionFor("sv_gameplayfix_nudgeoutofsolid"));
        // the stock 36-option QC block + one PORT EXTENSION (step_upspeed_max, for the "bryan" preset). The other
        // step-up cvar (sv_step_upspeed_scale) stays GLOBAL — no preset varies it — so it is NOT resolvable.
        Assert.Equal("step_upspeed_max", PhysicsPreset.OptionFor("sv_step_upspeed_max"));
        Assert.Null(PhysicsPreset.OptionFor("sv_step_upspeed_scale"));
        Assert.Equal(37, PhysicsPreset.Options.Length);
    }

    [Fact]
    public void CaptureResolved_ResolvesPresetEntries_AndCopiesGlobals()
    {
        CvarService c = NewStore();
        c.Set("sv_jumpstep", "1");
        float[] globals = MoveVarsBlock.Capture(c);
        float[] resolved = MoveVarsBlock.CaptureResolved(c, "cpma", globals, c.Has);
        Assert.Equal(globals.Length, resolved.Length);

        int IndexOf(string name) => System.Array.IndexOf(MoveVarsBlock.MovementCvars, name);
        Assert.Equal(320f, resolved[IndexOf("sv_maxspeed")]);                       // preset hit
        Assert.Equal(750f, resolved[IndexOf("sv_gravity")]);                        // preset hit
        Assert.Equal(150f, resolved[IndexOf("sv_aircontrol")]);                     // preset hit
        Assert.Equal(0f, resolved[IndexOf("sv_jumpspeedcap_min")]);                 // REAL zero preserved
        Assert.True(float.IsNaN(resolved[IndexOf("sv_jumpspeedcap_max")]));         // "nan" → NaN
        Assert.Equal(1f, resolved[IndexOf("sv_track_canjump")]);                    // preset hit
        Assert.Equal(globals[IndexOf("sv_jumpstep")], resolved[IndexOf("sv_jumpstep")]);             // global copied
        Assert.Equal(globals[IndexOf("g_movement_highspeed")], resolved[IndexOf("g_movement_highspeed")]); // global copied
        Assert.Equal(6f, resolved[IndexOf("sv_friction")]);                         // missing preset row → sv_ fallback

        // FromValues on the resolved vector carries the preset (incl. the real-0 cap) into the parameter struct.
        MovementParameters mp = MovementParameters.FromValues(resolved);
        Assert.Equal(320f, mp.MaxSpeed);
        Assert.Equal(750f, mp.Gravity);
        Assert.Equal(0f, mp.JumpSpeedCapMin);
        Assert.True(float.IsNaN(mp.JumpSpeedCapMax));
        Assert.True(mp.TrackCanJump);
    }

    [Fact]
    public void CaptureResolved_WithClientselectOff_EqualsTheGlobalVector()
    {
        CvarService c = NewStore(clientSelect: "0");
        float[] globals = MoveVarsBlock.Capture(c);
        float[] resolved = MoveVarsBlock.CaptureResolved(c, "cpma", globals, c.Has);
        Assert.Equal(MoveVarsBlock.Hash(globals), MoveVarsBlock.Hash(resolved)); // the wire's "no deviation" gate
    }

    /// <summary>
    /// Regression: an explicitly-configured 0 must survive BOTH decode paths. The cpma/quake3/warsow presets set
    /// <c>sv_gameplayfix_stepdown 0</c> and <c>sv_airstrafeaccel_qw 0</c> deliberately (player.qc:23-42
    /// Physics_ClientOption returns the cvar value verbatim — a real 0 is real). The wire (FromValues) must keep
    /// it because <see cref="MoveVarsBlock.Capture"/> always emits every slot; the cvar path (FromCvars) must keep
    /// it because the fallback gates on CVAR_TYPEFLAG_EXISTS (an absent cvar reads ""), not value==0. Before the
    /// fix the Raw/CvarRaw helpers clobbered the 0 back to the Xonotic default (StepDown 2 / airstrafeaccel_qw
    /// -0.95), diverging from Base on a non-Xonotic server.
    /// </summary>
    [Fact]
    public void ExplicitZero_StepdownAndAirstrafe_SurvivesBothDecodePaths()
    {
        var c = new CvarService();
        c.Set("sv_gameplayfix_stepdown", "0");
        c.Set("sv_airstrafeaccel_qw", "0");

        // WIRE path: Capture emits every slot, so the EXPLICITLY-set 0 is authoritative through FromValues
        // (it would snap back to StepDown 2 / airstrafeaccel_qw -0.95 under the old fall-back-on-0 helpers).
        MovementParameters wire = MovementParameters.FromValues(MoveVarsBlock.Capture(c));
        Assert.Equal(0, wire.StepDown);
        Assert.Equal(0f, wire.AirStrafeAccelQW);
        // The real production driver of a preset 0 is CaptureResolved (the preset row EXISTS → resolves to a
        // genuine 0, distinguishable from an unset global). Prove that vector carries the 0 too.
        var resStore = new CvarService();
        resStore.Set("g_physics_clientselect", "1");
        resStore.Set("g_physics_clientselect_options", "cpma");
        resStore.Set("g_physics_cpma_stepdown", "0");
        resStore.Set("g_physics_cpma_airstrafeaccel_qw", "0");
        float[] resolved = MoveVarsBlock.CaptureResolved(resStore, "cpma", MoveVarsBlock.Capture(resStore), resStore.Has);
        MovementParameters res = MovementParameters.FromValues(resolved);
        Assert.Equal(0, res.StepDown);
        Assert.Equal(0f, res.AirStrafeAccelQW);

        // CVAR path: FromCvars reads the ambient store; the EXISTS gate honors the explicit 0.
        IEngineServices? saved = Api.Services;
        try
        {
            Api.Services = new CvarOnlyServices(c);
            MovementParameters cvar = MovementParameters.FromCvars();
            Assert.Equal(0, cvar.StepDown);
            Assert.Equal(0f, cvar.AirStrafeAccelQW);
            // a genuinely-absent cvar still falls back to the port default.
            Api.Services = new CvarOnlyServices(new CvarService());
            Assert.Equal(2, MovementParameters.FromCvars().StepDown);
        }
        finally
        {
            Api.Services = saved!;
        }
    }

    [Fact]
    public void BryanPreset_ResolvesStepUpSpeedCap_OthersFallBackToGlobalDefault()
    {
        // The client-selectable "bryan" set (physics.cfg) carries its own sv_step_upspeed_max via the new
        // preset-resolvable option; every other set falls back to the global default (-1 = disabled), so the
        // existing presets are unchanged.
        var c = new CvarService();
        c.Set("g_physics_clientselect", "1");
        c.Set("g_physics_clientselect_options", "xonotic cpma bryan");
        c.Set("g_physics_bryan_step_upspeed_max", "1");
        const float globalDefault = -1f; // sv_step_upspeed_max unset → AbsentDefaults -1 on the wire

        Assert.Equal(1f, PhysicsPreset.Resolve(c, "bryan", "step_upspeed_max", globalDefault, c.Has));
        Assert.Equal(globalDefault, PhysicsPreset.Resolve(c, "cpma", "step_upspeed_max", globalDefault, c.Has));

        // The full per-peer wire vector (CaptureResolved → FromValues) carries the bryan cap and the cpma fallback.
        float[] globals = MoveVarsBlock.Capture(c); // sv_step_upspeed_max unset → -1 (AbsentDefaults), scale → 1
        MovementParameters bryan = MovementParameters.FromValues(MoveVarsBlock.CaptureResolved(c, "bryan", globals, c.Has));
        Assert.Equal(1f, bryan.StepUpSpeedMax);
        Assert.Equal(1f, bryan.StepUpSpeedScale); // scale stays global (1), only the cap is preset-driven
        MovementParameters cpma = MovementParameters.FromValues(MoveVarsBlock.CaptureResolved(c, "cpma", globals, c.Has));
        Assert.Equal(-1f, cpma.StepUpSpeedMax);
    }

    /// <summary>The full set of warsowbunny + air rows the <c>warsow</c> preset block defines (physics.cfg) — the
    /// ONLY stock preset whose <c>warsowbunny_turnaccel</c> is non-zero (9), which is what selects the
    /// <c>PM_AirAccelerate</c> branch (player.qc:360). Seeded onto a clientselect store so a warsow resolution
    /// drives the genuine Warsow-bunny path, not the QW path.</summary>
    private static CvarService NewWarsowStore()
    {
        var c = new CvarService();
        c.Set("g_physics_clientselect", "1");
        c.Set("g_physics_clientselect_options", "xonotic warsow");
        c.Set("g_physics_warsow_maxspeed", "320");
        c.Set("g_physics_warsow_maxairspeed", "320");
        c.Set("g_physics_warsow_warsowbunny_turnaccel", "9");        // != 0 → selects PM_AirAccelerate
        c.Set("g_physics_warsow_warsowbunny_airforwardaccel", "1.00001");
        c.Set("g_physics_warsow_warsowbunny_topspeed", "925");
        c.Set("g_physics_warsow_warsowbunny_accel", "0.1593");
        c.Set("g_physics_warsow_warsowbunny_backtosideratio", "0.8");
        c.Set("g_physics_warsow_airaccelerate", "1");
        c.Set("sv_maxspeed", "360");
        return c;
    }

    /// <summary>
    /// LIVENESS: the Warsow-bunny air-accel (PM_AirAccelerate, player.qc:343-375) is dead at the stock Xonotic
    /// preset (warsowbunny_turnaccel 0) and only goes live when a non-default preset sets turnaccel != 0. Drive
    /// the FULL live path — a <c>warsow</c>-resolved <see cref="MovementParameters"/> parked on
    /// <see cref="MovementParameters.PredictionOverride"/> (the predicted leg of <c>Resolve</c>) through
    /// <see cref="PlayerPhysics.Move"/> for an airborne strafing tick — and assert the Warsow-bunny branch
    /// actually ran by matching a hand-computed PM_AirAccelerate step (a value the stock QW air-accel cannot
    /// produce). Proves the AirBranch dispatch (PlayerPhysics.cs:563) reaches PMAccelerate.AirAccelerate.
    /// </summary>
    [Fact]
    public void WarsowPreset_DrivesPM_AirAccelerate_ThroughTheLiveMove()
    {
        CvarService store = NewWarsowStore();
        MovementParameters warsow =
            MovementParameters.FromValues(MoveVarsBlock.CaptureResolved(store, "warsow", MoveVarsBlock.Capture(store), store.Has));
        Assert.Equal(9f, warsow.WarsowBunnyTurnAccel); // sanity: the resolved block selects the Warsow path

        // Isolate the AirAccelerate leg: the warsow preset sets aircontrol 0 (no CPM redirect after the bunny
        // accel), so zero it here. (FromValues decodes aircontrol via the magnitude-fallback helper, which turns a
        // genuine 0 into the 100 default — a separate air-strafe-blends concern; pinning it here keeps THIS test
        // about the warsowbunny dispatch, with velocity.x unperturbed by a trailing Aircontrol pass.)
        warsow.AirControl = 0f;

        // No collision world needed: a strafing tick high in the air clears its full step.
        var world = new AnalyticWorld();
        var clock = new MutableClock();
        IEngineServices? saved = Api.Services;
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerJump.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerCanCrouch.Clear();
        XonoticGodot.Common.Gameplay.MutatorHooks.PlayerPhysics.Clear();
        try
        {
            Api.Services = new MovementTestServices(world, clock);
            MovementParameters.PredictionOverride = warsow; // predicted leg reads this instead of the cvars

            // Airborne, moving forward-only (move.y == 0, move.x != 0 — the AirBranch warsow guard), facing +X.
            var player = new XonoticGodot.Common.Framework.Entity
            {
                Origin = new System.Numerics.Vector3(0f, 0f, 1024f),
                Velocity = new System.Numerics.Vector3(400f, 0f, 0f),
                Angles = System.Numerics.Vector3.Zero,
                Mins = new System.Numerics.Vector3(-16f, -16f, -24f),
                Maxs = new System.Numerics.Vector3(16f, 16f, 45f),
                Gravity = 1f,
            };
            System.Numerics.Vector3 velBefore = player.Velocity;

            const float dt = 1f / 60f;
            var input = new MovementInput
            {
                ViewAngles = System.Numerics.Vector3.Zero,
                MoveValues = new System.Numerics.Vector3(warsow.MaxSpeed, 0f, 0f), // forward = maxspeed, side = 0
                FrameTime = dt,
                Predicted = true,
            };
            new PlayerPhysics().Move(player, input);

            // Recompute PM_AirAccelerate (player.qc:343-375) for the same inputs: wishdir = +X, wishspeed clamps
            // to maxairspeed (320). curspeed 400 > wishspeed → the "below 1.01×curspeed" branch.
            System.Numerics.Vector3 wishdir = new(1f, 0f, 0f);
            float wishspeed = warsow.MaxAirSpeed; // wishspeed bound to maxairspeed before the branch (AirBranch)
            float curspeed = 400f;
            float f = MathF.Max(0f, (warsow.WarsowBunnyTopSpeed - curspeed) / (warsow.WarsowBunnyTopSpeed - warsow.MaxSpeed));
            float ws = MathF.Max(curspeed, warsow.MaxSpeed) + warsow.WarsowBunnyAccel * f * warsow.MaxSpeed * dt;
            System.Numerics.Vector3 acceldir = System.Numerics.Vector3.Normalize(wishdir * ws - new System.Numerics.Vector3(curspeed, 0f, 0f));
            float addspeed = (wishdir * ws - new System.Numerics.Vector3(curspeed, 0f, 0f)).Length();
            float accelspeed = MathF.Min(addspeed, warsow.WarsowBunnyTurnAccel * warsow.MaxSpeed * dt);
            float expectedVx = curspeed + accelspeed * acceldir.X; // pure +X here (backtoside branch is collinear)

            // The Warsow path nudges velocity.x forward toward warsow topspeed; assert the live Move reproduced it.
            Assert.True(player.Velocity.X > velBefore.X,
                $"Warsow-bunny accel should push velocity.x up; before {velBefore.X}, after {player.Velocity.X}");
            Assert.Equal(expectedVx, player.Velocity.X, 2); // 2-dp: the live Move folds the identical AirAccelerate step
        }
        finally
        {
            MovementParameters.PredictionOverride = null;
            XonoticGodot.Common.Gameplay.MutatorHooks.PlayerJump.Clear();
            XonoticGodot.Common.Gameplay.MutatorHooks.PlayerCanCrouch.Clear();
            XonoticGodot.Common.Gameplay.MutatorHooks.PlayerPhysics.Clear();
            Api.Services = saved!;
        }
    }

    /// <summary>
    /// Pins <see cref="PMAccelerate.AirAccelerate"/> byte-for-byte against the Base <c>PM_AirAccelerate</c>
    /// formula (player.qc:343-375), independent of the Move harness — both the "accelerate toward topspeed"
    /// branch and the back-to-side redirect. Guards the warsowbunny math itself, the leaf the live path calls.
    /// </summary>
    [Fact]
    public void AirAccelerate_MatchesBaseFormula_BothBranches()
    {
        MovementParameters mp = MovementParameters.Defaults;
        mp.MaxSpeed = 320f; mp.MaxAirSpeed = 320f;
        mp.WarsowBunnyTurnAccel = 9f; mp.WarsowBunnyAccel = 0.1593f;
        mp.WarsowBunnyTopSpeed = 925f; mp.WarsowBunnyAirForwardAccel = 1.00001f;
        mp.WarsowBunnyBackToSideRatio = 0.8f;
        const float dt = 1f / 60f;

        // CASE A — curspeed (300) below the topspeed ramp, wishdir +X, side velocity present (back-to-side fires).
        var a = new XonoticGodot.Common.Framework.Entity { Velocity = new System.Numerics.Vector3(300f, 40f, 0f) };
        System.Numerics.Vector3 expectedA = BaseAirAccel(a.Velocity, new System.Numerics.Vector3(1f, 0f, 0f), 320f, mp, dt);
        PMAccelerate.AirAccelerate(a, mp, dt, new System.Numerics.Vector3(1f, 0f, 0f), 320f);
        Assert.Equal(expectedA.X, a.Velocity.X, 3);
        Assert.Equal(expectedA.Y, a.Velocity.Y, 3);

        // CASE B — wishspeed (200) ABOVE curspeed*1.01 (curspeed 100): the airforwardaccel clamp branch.
        var b = new XonoticGodot.Common.Framework.Entity { Velocity = new System.Numerics.Vector3(100f, 0f, 0f) };
        System.Numerics.Vector3 expectedB = BaseAirAccel(b.Velocity, new System.Numerics.Vector3(1f, 0f, 0f), 200f, mp, dt);
        PMAccelerate.AirAccelerate(b, mp, dt, new System.Numerics.Vector3(1f, 0f, 0f), 200f);
        Assert.Equal(expectedB.X, b.Velocity.X, 3);
        Assert.Equal(expectedB.Y, b.Velocity.Y, 3);
    }

    /// <summary>Independent re-transcription of Base PM_AirAccelerate (player.qc:343-375) for the assertions above.</summary>
    private static System.Numerics.Vector3 BaseAirAccel(
        System.Numerics.Vector3 vel, System.Numerics.Vector3 wishdir, float wishspeed, in MovementParameters mp, float dt)
    {
        if (wishspeed == 0f) return vel;
        System.Numerics.Vector3 curvel = vel; curvel.Z = 0f;
        float curspeed = curvel.Length();
        if (wishspeed > curspeed * 1.01f)
            wishspeed = MathF.Min(wishspeed, curspeed + mp.WarsowBunnyAirForwardAccel * mp.MaxSpeed * dt);
        else
        {
            float f = MathF.Max(0f, (mp.WarsowBunnyTopSpeed - curspeed) / (mp.WarsowBunnyTopSpeed - mp.MaxSpeed));
            wishspeed = MathF.Max(curspeed, mp.MaxSpeed) + mp.WarsowBunnyAccel * f * mp.MaxSpeed * dt;
        }
        System.Numerics.Vector3 wishvel = wishdir * wishspeed;
        System.Numerics.Vector3 acceldir = wishvel - curvel;
        float addspeed = acceldir.Length();
        acceldir = acceldir.Length() == 0f ? System.Numerics.Vector3.Zero : System.Numerics.Vector3.Normalize(acceldir);
        float accelspeed = MathF.Min(addspeed, mp.WarsowBunnyTurnAccel * mp.MaxSpeed * dt);
        if (mp.WarsowBunnyBackToSideRatio < 1f)
        {
            System.Numerics.Vector3 curdir = curvel.Length() == 0f ? System.Numerics.Vector3.Zero : System.Numerics.Vector3.Normalize(curvel);
            float dot = System.Numerics.Vector3.Dot(acceldir, curdir);
            if (dot < 0f) acceldir -= (1f - mp.WarsowBunnyBackToSideRatio) * dot * curdir;
        }
        return vel + accelspeed * acceldir;
    }

    /// <summary>Minimal services whose only live member is a real cvar store — FromCvars touches nothing else.</summary>
    private sealed class CvarOnlyServices : IEngineServices
    {
        public CvarOnlyServices(ICvarService cvars) => Cvars = cvars;
        public ICvarService Cvars { get; }
        public ITraceService Trace => null!;
        public IEntityService Entities => null!;
        public ISoundService Sound => null!;
        public IModelService Models => null!;
        public IGameClock Clock => null!;
    }

    [Fact]
    public void PredictionOverride_DrivesThePredictedResolve_AndDefaultsAreInert()
    {
        var player = new XonoticGodot.Common.Framework.Entity();
        // default-inert: no override, no provider → both legs read the ambient cvars (here: no services → a
        // FromCvars call would throw, so prove inertness via the override path only).
        MovementParameters mp = MovementParameters.Defaults;
        mp.MaxSpeed = 320f;
        MovementParameters.PredictionOverride = mp;
        try
        {
            Assert.Equal(320f, MovementParameters.Resolve(player, predicted: true).MaxSpeed);
        }
        finally
        {
            MovementParameters.PredictionOverride = null;
        }

        // the authoritative leg consults the provider with the entity.
        MovementParameters.PresetProvider = e =>
        {
            var c = NewStore();
            return MoveVarsBlock.CaptureResolved(c, "cpma", MoveVarsBlock.Capture(c), c.Has);
        };
        try
        {
            Assert.Equal(320f, MovementParameters.Resolve(player, predicted: false).MaxSpeed);
        }
        finally
        {
            MovementParameters.PresetProvider = null;
        }
    }
}
