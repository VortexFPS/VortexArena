using System.Linq;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T54 client-cvar replication tests: the server-side <c>sentcvar</c> command (QC ClientCommand_sentcvar,
/// server/command/cmd.qc:804-836 + the SVQC REPLICATE receive leg, lib/replicate.qh:79-100), its per-client
/// store + fixups + bridges (autoswitch / cl_physics → the per-peer preset resolution / cl_weaponpriority →
/// <see cref="Inventory.PriorityProvider"/>), and the g_movement_highspeed seed that now actually scales
/// movement (Physics_UpdateStats:47-51).
/// </summary>
[Collection("GlobalState")]
public class CvarReplicationTests
{
    public CvarReplicationTests()
    {
        // session statics a leaked net session / earlier test could have left behind
        MovementParameters.PredictionOverride = null;
        MovementParameters.PresetProvider = null;
    }

    private static GameWorld NewWorld()
    {
        var world = new GameWorld(new CollisionWorld()) { MapName = "boil" };
        world.Boot("dm");
        return world;
    }

    private static Player NewCaller(string name = "p")
        => new() { NetName = name, Flags = EntFlags.Client, PlayerId = 1 };

    // ============================================================================== sentcvar: store + gates

    [Fact]
    public void Sentcvar_StoresAllowlistedCvar_PerClient()
    {
        var world = NewWorld();
        Player a = NewCaller("a");
        Player b = NewCaller("b");
        CommandContext ctx = world.Commands.Execute("sentcvar cl_noantilag 1", isServerConsole: false, caller: a);
        Assert.Equal("", ctx.Output.Trim()); // QC prints nothing on the happy path
        Assert.True(world.Commands.GetClientCvarBool(a, "cl_noantilag"));
        Assert.False(world.Commands.GetClientCvarBool(b, "cl_noantilag")); // per-client, not shared
        Assert.Equal("1", world.Commands.GetClientCvar(a, "cl_noantilag"));
    }

    [Fact]
    public void Sentcvar_NullCaller_Rejected()
    {
        var world = NewWorld();
        CommandContext ctx = world.Commands.Execute("sentcvar cl_noantilag 1", isServerConsole: true);
        Assert.Contains("client command", ctx.Output);
    }

    [Fact]
    public void Sentcvar_ShortArgs_PrintsUsage()
    {
        var world = NewWorld();
        CommandContext ctx = world.Commands.Execute("sentcvar cl_noantilag", isServerConsole: false, caller: NewCaller());
        Assert.Contains("Usage:^3 cmd sentcvar <cvar> <arguments>", ctx.Output); // cmd.qc:831
    }

    [Fact]
    public void Sentcvar_NonAllowlisted_StoresNothing_AndNeverTouchesTheWorldStore()
    {
        var world = NewWorld();
        Player p = NewCaller();
        string before = world.Services.Cvars.GetString("sv_cheats");
        world.Commands.Execute("sentcvar sv_cheats 1", isServerConsole: false, caller: p);
        Assert.Equal(before, world.Services.Cvars.GetString("sv_cheats")); // T47 privilege separation
        Assert.Equal("", world.Commands.GetClientCvar(p, "sv_cheats"));

        world.Commands.Execute("sentcvar g_balance_blaster_primary_damage 9999", isServerConsole: false, caller: p);
        Assert.Equal("", world.Commands.GetClientCvar(p, "g_balance_blaster_primary_damage"));
    }

    [Fact]
    public void Sentcvar_Autoswitch_BridgesIntoGetAutoswitch()
    {
        var world = NewWorld();
        Player p = NewCaller();
        world.Commands.Execute("sentcvar cl_autoswitch 1", isServerConsole: false, caller: p);
        Assert.True(world.Commands.GetAutoswitch(p));
        world.Commands.Execute("sentcvar cl_autoswitch 0", isServerConsole: false, caller: p);
        Assert.False(world.Commands.GetAutoswitch(p));
    }

    // ===================================================================== sentcvar: notification_CHOICE_* (A5 #8)

    /// <summary>
    /// [A5 #8] QC ReplicateVars replicates each <c>notification_&lt;CHOICE&gt;</c> cvar (the client's verbose/terse
    /// kill-message preference) into <c>CS(this).msg_choice_choices[idx]</c>. The port's gate must ACCEPT a
    /// registered choice cvar (so the preference reaches the server's per-client choice array) while still
    /// REJECTING an unregistered <c>notification_*</c> string (privilege separation — never the world store).
    /// </summary>
    [Fact]
    public void Sentcvar_NotificationChoice_AcceptedAndStoredPerClient_ButUnregisteredRejected()
    {
        Notifications.RegisterAll(); // ensure the choice registry + indices exist
        var world = NewWorld();
        Player p = NewCaller();

        // Resolve a real registered choice + its replicated cvar name the way both ends build it.
        var frag = Notifications.All.First(n => n.Type == MsgType.Choice && n.Name == "FRAG");
        string cvar = "notification_" + NotificationChoiceState.ChoiceCvarName(frag);

        // option B (verbose) — accepted, lands in the per-client store AND the choice array.
        world.Commands.Execute($"sentcvar {cvar} 2", isServerConsole: false, caller: p);
        Assert.Equal("2", world.Commands.GetClientCvar(p, cvar));
        NotificationChoiceState? st = world.Commands.GetChoiceState(p);
        Assert.NotNull(st);
        Assert.Equal(NotificationChoiceState.OptionB, st!.Get(frag.ChoiceIdx));

        // an unregistered notification_* name is a non-allowlisted REPLICATE field → stored nowhere (T47).
        world.Commands.Execute("sentcvar notification_NOT_A_REAL_CHOICE 2", isServerConsole: false, caller: p);
        Assert.Equal("", world.Commands.GetClientCvar(p, "notification_NOT_A_REAL_CHOICE"));

        // and it must never have reached the world cvar store either.
        Assert.Equal("", world.Services.Cvars.GetString("notification_NOT_A_REAL_CHOICE"));
    }

    // ============================================================================== weapon priority fixup

    [Fact]
    public void Sentcvar_WeaponPriority_NumbersAndForceCompletes_AndFeedsThePriorityProvider()
    {
        var world = NewWorld();
        Player p = NewCaller();
        world.Commands.Execute("sentcvar cl_weaponpriority \"vortex blaster\"", isServerConsole: false, caller: p);

        string stored = world.Commands.GetClientCvar(p, "cl_weaponpriority");
        Assert.NotEqual("", stored);
        string[] toks = stored.Split(' ');
        int vortexId = Registry<Weapon>.ByName("vortex")!.RegistryId;
        int blasterId = Registry<Weapon>.ByName("blaster")!.RegistryId;
        // the named weapons lead, in the user's order (W_NumberWeaponOrder), …
        Assert.Equal(vortexId.ToString(), toks[0]);
        Assert.Equal(blasterId.ToString(), toks[1]);
        // …then ForceComplete appends every remaining non-SPECIALATTACK weapon (W_FixWeaponOrder_ForceComplete).
        int nonSpecial = 0;
        for (int i = 0; i < Registry<Weapon>.Count; i++)
            if ((Registry<Weapon>.ById(i).SpawnFlags & WeaponFlags.SpecialAttack) == 0)
                nonSpecial++;
        Assert.True(toks.Length >= nonSpecial, $"complete order should cover all {nonSpecial} weapons, got {toks.Length}");

        // the selection code reads the per-client value through Inventory.PriorityProvider (wired by Commands).
        Assert.Equal(stored, Inventory.WeaponPriority(p));
    }

    [Fact]
    public void WeaponPriority_FallsBackToTheGlobalPath_WhenNotReplicated()
    {
        var world = NewWorld();
        world.Services.Cvars.Set("cl_weaponpriority", ""); // ensure the global path is the empty→default branch
        Player stranger = NewCaller("stranger");
        // no sentcvar for this player → provider returns "" → the pre-T54 global-cvar/default path.
        Assert.Equal(Inventory.WeaponOrderByPriorityDefault, Inventory.WeaponPriority(stranger));
    }

    // ============================================================================== cl_physics → preset resolution

    [Fact]
    public void Sentcvar_ClPhysics_FeedsThePresetTable_AndTheVectorResolution()
    {
        var world = NewWorld();
        Player p = NewCaller();
        try
        {
            world.Services.Cvars.Set("g_physics_clientselect", "1");
            world.Services.Cvars.Set("g_physics_clientselect_options", "xonotic cpma");
            world.Services.Cvars.Set("g_physics_cpma_maxspeed", "320");

            world.Commands.Execute("sentcvar cl_physics cpma", isServerConsole: false, caller: p);
            Assert.Equal("cpma", world.Commands.GetClientCvar(p, "cl_physics"));

            // the same table slot `cmd physics` writes — and what the snapshot's per-peer resolve reads.
            float[] globals = XonoticGodot.Net.MoveVarsBlock.Capture(world.Services.Cvars);
            float[] resolved = XonoticGodot.Net.MoveVarsBlock.CaptureResolved(
                world.Services.Cvars, world.Commands.GetClientCvar(p, "cl_physics"), globals,
                world.Services.CvarsImpl.Has);
            int maxspeedIdx = System.Array.IndexOf(XonoticGodot.Net.MoveVarsBlock.MovementCvars, "sv_maxspeed");
            Assert.Equal(320f, resolved[maxspeedIdx]);
        }
        finally
        {
            world.Services.Cvars.Set("g_physics_clientselect", "0"); // restore the shipped default (shared state)
        }
    }

    [Fact]
    public void PhysicsCommand_WritesTheSameTableSlot_AsSentcvar()
    {
        var world = NewWorld();
        Player p = NewCaller();
        try
        {
            world.Services.Cvars.Set("g_physics_clientselect", "1");
            world.Services.Cvars.Set("g_physics_clientselect_options", "xonotic cpma");
            world.Commands.Execute("physics cpma", isServerConsole: false, caller: p);
            Assert.Equal("cpma", world.Commands.GetClientCvar(p, "cl_physics"));

            // the QC default branch reports the current set from the same slot
            CommandContext cur = world.Commands.Execute("physics whatisthis", isServerConsole: false, caller: p);
            Assert.Contains("Current physics set: ^3cpma", cur.Output);
        }
        finally
        {
            world.Services.Cvars.Set("g_physics_clientselect", "0");
        }
    }

    [Fact]
    public void ForgetPlayer_DropsThePerClientState()
    {
        var world = NewWorld();
        Player p = NewCaller();
        world.Commands.Execute("sentcvar cl_noantilag 1", isServerConsole: false, caller: p);
        world.Commands.Execute("sentcvar cl_autoswitch 1", isServerConsole: false, caller: p);
        world.Commands.ForgetPlayer(p);
        Assert.False(world.Commands.GetClientCvarBool(p, "cl_noantilag"));
        Assert.False(world.Commands.GetAutoswitch(p));
    }

    // ============================================================================== g_movement_highspeed

    [Fact]
    public void HighSpeed_FoldsLikePhysicsUpdateStats()
    {
        // pure struct math — no services needed. Non-q3compat (the stock path, player.qc:58-64):
        MovementParameters mp = MovementParameters.Defaults;
        mp.ApplyHighSpeed(2f);
        Assert.Equal(720f, mp.MaxSpeed);                  // :51 — always scaled
        Assert.Equal(1800f, mp.AirSpeedLimitNonQW);       // :64
        Assert.Equal(PMAccelerate.AdjustAirAccelQW(-0.8f, 2f), mp.AirAccelQW);          // :60
        Assert.Equal(PMAccelerate.AdjustAirAccelQW(-0.95f, 2f), mp.AirStrafeAccelQW);   // :61-63 (non-zero)
        Assert.Equal(100f, mp.MaxAirStrafeSpeed);         // :115 — scaled in NEITHER branch
        Assert.Equal(360f, mp.MaxAirSpeed);               // :119 — NOT scaled when !q3compat

        // q3-compat (:54-57,:116-117): QW vars raw, maxairspeed scales instead.
        MovementParameters q3 = MovementParameters.Defaults;
        q3.HighSpeedQ3Compat = true;
        q3.ApplyHighSpeed(2f);
        Assert.Equal(720f, q3.MaxSpeed);
        Assert.Equal(720f, q3.MaxAirSpeed);
        Assert.Equal(-0.8f, q3.AirAccelQW);
        Assert.Equal(-0.95f, q3.AirStrafeAccelQW);
        Assert.Equal(900f, q3.AirSpeedLimitNonQW);
    }

    [Fact]
    public void HighSpeedCvar_ActuallyScalesGroundMovement()
    {
        // A tiny Move()-level sim on an analytic halfspace floor: with g_movement_highspeed 2 the steady-state
        // run speed roughly doubles vs the unset default — proving the SpeedMultiplier seed reads the cvar
        // (PlayerPhysics, the Physics_UpdateStats:47/:50 seed) and the unset store stays at the golden behavior.
        float defaultSpeed = SteadyRunSpeed(highspeed: null);
        float doubledSpeed = SteadyRunSpeed(highspeed: "2");
        Assert.InRange(defaultSpeed, 300f, 380f);   // stock sv_maxspeed 360
        Assert.InRange(doubledSpeed, 600f, 760f);   // ×2 fold
    }

    private static float SteadyRunSpeed(string? highspeed)
    {
        IEngineServices? saved = Api.Services;
        try
        {
            MutatorHooks.PlayerJump.Clear();
            MutatorHooks.PlayerCanCrouch.Clear();
            MutatorHooks.PlayerPhysics.Clear();

            // a single halfspace floor: solid at z <= 0
            var world = AnalyticWorld.FromPlanes(new (int, float[])[]
            {
                (AnalyticWorld.ContSolid, new float[] { 0f, 0f, 1f, 0f }),
            });
            var clock = new MutableClock();
            var services = new HighspeedTestServices(world, clock);
            if (highspeed is not null)
                services.Cvars.Set("g_movement_highspeed", highspeed);
            Api.Services = services;

            var physics = new PlayerPhysics();
            var player = new Entity
            {
                Origin = new Vector3(0f, 0f, 24.03f),
                Velocity = Vector3.Zero,
                Mins = new Vector3(-16f, -16f, -24f),
                Maxs = new Vector3(16f, 16f, 45f),
                Gravity = 1f,
                Flags = EntFlags.OnGround,
            };
            var input = new MovementInput
            {
                ViewAngles = Vector3.Zero,
                MoveValues = new Vector3(800f, 0f, 0f), // enough wish-speed headroom for the ×2 cap
                FrameTime = 1f / 72f,
            };
            for (int t = 0; t < 360; t++) // 5 s — far past the friction/accel equilibrium
            {
                physics.Move(player, input);
                clock.Time += input.FrameTime;
            }
            return new Vector3(player.Velocity.X, player.Velocity.Y, 0f).Length();
        }
        finally
        {
            Api.Services = saved!;
        }
    }

    /// <summary>The parity harness services but with a REAL cvar store (so g_movement_highspeed is settable).</summary>
    private sealed class HighspeedTestServices : IEngineServices
    {
        private readonly MovementTestServices _inner;
        public HighspeedTestServices(AnalyticWorld world, MutableClock clock) => _inner = new MovementTestServices(world, clock);
        public ITraceService Trace => _inner.Trace;
        public IEntityService Entities => _inner.Entities;
        public ICvarService Cvars { get; } = new CvarService();
        public ISoundService Sound => _inner.Sound;
        public IModelService Models => _inner.Models;
        public IGameClock Clock => _inner.Clock;
    }

    // ============================================================================== wire-vector equivalence

    [Fact]
    public void FromValues_OnACapturedVector_MatchesFromCvars()
    {
        var world = NewWorld();
        world.Services.Cvars.Set("sv_maxspeed", "400");
        world.Services.Cvars.Set("sv_gravity", "750");
        world.Services.Cvars.Set("g_movement_highspeed", "1.5");
        try
        {
            // FromCvars reads the ambient store (the booted world's); Capture+FromValues must agree with it.
            MovementParameters live = MovementParameters.FromCvars();
            MovementParameters wire = MovementParameters.FromValues(
                XonoticGodot.Net.MoveVarsBlock.Capture(world.Services.Cvars));
            Assert.Equal(live.MaxSpeed, wire.MaxSpeed);
            Assert.Equal(live.Gravity, wire.Gravity);
            Assert.Equal(live.Friction, wire.Friction);
            Assert.Equal(live.AirAccelQW, wire.AirAccelQW);
            Assert.Equal(live.HighSpeed, wire.HighSpeed);
            Assert.Equal(live.JumpVelocity, wire.JumpVelocity);
            Assert.Equal(float.IsNaN(live.JumpSpeedCapMin), float.IsNaN(wire.JumpSpeedCapMin));
            Assert.Equal(live.StepDown, wire.StepDown);
            Assert.Equal(live.WallFriction, wire.WallFriction);
        }
        finally
        {
            world.Services.Cvars.Set("sv_maxspeed", "360");
            world.Services.Cvars.Set("sv_gravity", "800");
            world.Services.Cvars.Set("g_movement_highspeed", "1");
        }
    }
}
