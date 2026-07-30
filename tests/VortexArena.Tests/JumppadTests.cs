using System.Numerics;
using XonoticGodot.Common;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Jump-pad (trigger_push / target_push) wiring tests — the spawn → link → touch → launch chain.
///
/// The jump-pad LAUNCH MATH (<see cref="Jumppads.CalculateVelocity"/>, the faithful port of QC
/// <c>trigger_push_calculatevelocity</c>) was already in place, and the <c>trigger_push</c> spawnfunc
/// (<see cref="Jumppads.PushSetup"/>) builds a SOLID_TRIGGER volume with a <c>.touch</c>. What was missing
/// was the per-tick <b>touch-triggers pass</b> for the player: the ported <see cref="PlayerPhysics"/> does its
/// own slide-move and only dual-dispatches touch on the SOLID it collides with — a non-solid SOLID_TRIGGER
/// jump-pad is invisible to that sweep. QC fires those volumes via SV_TouchTriggers (engine
/// SV_LinkEdict_TouchAreaGrid) after the move; <see cref="SimulationLoop"/> now runs that pass for clients,
/// so a player walking through a jump-pad gets flung along the arc.
///
/// These tests drive a real <see cref="SimulationLoop"/> tick (the exact fixed code path: ClientMove →
/// movement → TouchAreaGrid → trigger_push_touch → launch) and assert the launch velocity equals the QC
/// solver's result — and, as a regression guard, that a player NOT overlapping the pad is untouched.
/// </summary>
public class JumppadTests
{
    // A jump-pad volume sitting on the floor, and its destination point up + forward (+X) of it.
    private static readonly Vector3 PadMins = new(-48f, -48f, 0f);
    private static readonly Vector3 PadMaxs = new(48f, 48f, 32f);
    private static readonly Vector3 PadOrigin = Vector3.Zero;          // the trigger's own origin (box is absolute)
    private static readonly Vector3 TargetOrigin = new(256f, 0f, 200f); // info_notnull destination
    private const float PadHeight = 128f;                              // ".height" — apex above the higher endpoint
    private const float Gravity = 800f;

    private sealed class Harness
    {
        public EngineServices Services = null!;
        public SimulationLoop Sim = null!;
        public Entity Player = null!;
        public Entity Pad = null!;
        public Entity Target = null!;
        public MovementInput Input;
    }

    /// <summary>
    /// Stand up an engine facade + sim loop on a flat floor, spawn a <c>target_position</c> destination and a
    /// <c>trigger_push</c> aimed at it (through the real spawnfuncs), and add a player to the client list whose
    /// movement is driven by the ported <see cref="PlayerPhysics"/>. Mirrors the server's per-client tick.
    /// </summary>
    private static Harness Build(Vector3 playerOrigin, Vector3 wishMove)
    {
        // Hermetic: the targetname index is process-global static; drop any leftover from another test.
        MapMover.ClearIndex();

        var world = new CollisionWorld();
        // A big floor slab so the player has ground (Quake Z up): top at Z=0.
        world.AddBrush(Brush.FromBox(new Vector3(-4096f, -4096f, -64f), new Vector3(4096f, 4096f, 0f), SuperContents.Solid));
        world.BuildGrid();

        var services = new EngineServices(world);
        GameInit.Boot(services);                  // Api.Services = services; Movement.System + spawnfuncs registered
        Api.Cvars.Set("sv_gravity", Gravity.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var sim = new SimulationLoop(services, world) { Gravity = Gravity };

        // --- destination point (QC target_position / info_notnull): a passive arc target. ---
        Entity target = Api.Entities.Spawn();
        target.Origin = TargetOrigin;
        target.TargetName = "jp_dest";
        Assert.True(SpawnFuncs.TrySpawn("target_position", target), "target_position should be registered");
        Api.Entities.SetOrigin(target, TargetOrigin); // relink AbsMin/AbsMax after the spawnfunc

        // --- the jump-pad (QC trigger_push): a SOLID_TRIGGER box aimed at the destination. ---
        Entity pad = Api.Entities.Spawn();
        pad.Origin = PadOrigin;
        pad.Mins = PadMins;
        pad.Maxs = PadMaxs;
        pad.Size = PadMaxs - PadMins;
        pad.Target = "jp_dest";
        pad.Height = PadHeight;
        Assert.True(SpawnFuncs.TrySpawn("trigger_push", pad), "trigger_push should be registered");
        // PushSetup -> InitTrigger keeps Mins/Maxs; relink so AbsMin/AbsMax cover the box for the touch test.
        Api.Entities.SetOrigin(pad, PadOrigin);

        Assert.Equal(Solid.Trigger, pad.Solid);
        Assert.NotNull(pad.Touch);
        Assert.Same(target, pad.Enemy); // trigger_push_findtarget cached the single destination

        // --- the player (a SOLID_SLIDEBOX client driven by the ported movement sim). ---
        Entity player = Api.Entities.Spawn();
        player.ClassName = "player";
        player.MoveType = MoveType.Walk;
        player.Solid = Solid.SlideBox;
        player.Flags |= EntFlags.Client | EntFlags.JumpReleased;
        player.Mins = new Vector3(-16f, -16f, -24f);
        player.Maxs = new Vector3(16f, 16f, 45f);
        player.ViewOfs = new Vector3(0f, 0f, 35f);
        player.Gravity = 1f;
        player.Health = 100f;
        player.Origin = playerOrigin;
        player.OldOrigin = playerOrigin;
        Api.Entities.SetOrigin(player, playerOrigin);

        var h = new Harness
        {
            Services = services,
            Sim = sim,
            Player = player,
            Pad = pad,
            Target = target,
            Input = new MovementInput
            {
                ViewAngles = Vector3.Zero,        // facing +X (toward the destination)
                MoveValues = wishMove,
                FrameTime = SimulationLoop.TicRate,
            },
        };

        sim.Clients.Add(player);
        sim.ClientMove = e => Movement.Move(e, h.Input);
        return h;
    }

    /// <summary>
    /// END-TO-END: a player standing in the jump-pad volume is flung along the ballistic arc once the per-tick
    /// touch-triggers pass fires the pad's <c>.touch</c>. The resulting velocity must match the QC
    /// <c>trigger_push_calculatevelocity</c> solver (the existing, faithful port) exactly.
    /// </summary>
    [Fact]
    public void Player_OverlappingTriggerPush_IsLaunchedAlongQcArc()
    {
        // Drop the player INSIDE the pad's footprint, resting just above the floor (hull bottom −24 → origin 24).
        var h = Build(playerOrigin: new Vector3(0f, 0f, 24f), wishMove: Vector3.Zero);
        h.Player.Flags |= EntFlags.OnGround; // standing on the floor at the start

        // The launch velocity the faithful QC solver computes for THIS pad/target/toucher.
        Vector3 expected = Jumppads.CalculateVelocity(h.Player.Origin, h.Target, h.Pad.Height, h.Player);

        // Sanity on the expectation itself: a forward-up arc (toward +X, big +Z), well above a jump.
        Assert.True(expected.Z > 400f, $"expected a strong upward launch, got {expected}");
        Assert.True(expected.X > 0f, $"expected a forward (+X) launch toward the target, got {expected}");

        // One tick: ClientMove runs the player physics, then the engine's TouchAreaGrid fires the pad.
        Vector3 velBefore = h.Player.Velocity;
        h.Sim.Tick();

        // The pad launched the player: velocity is now the QC arc solution, and the on-ground flag is cleared.
        Vector3 v = h.Player.Velocity;
        Assert.False(h.Player.OnGround, "trigger_push UNSET_ONGROUND should have cleared the ground flag");
        Assert.True(v.Z > 400f, $"player should have been launched upward; before={velBefore}, after={v}");

        // Exact-arc match: the touched pad set velocity = trigger_push_calculatevelocity(...). Allow a tiny
        // tolerance only for the single gravity half-step the slide-move applies AFTER the launch this tick.
        float gravHalfStep = Gravity * SimulationLoop.TicRate * 0.5f;
        Assert.True(MathF.Abs(v.X - expected.X) < 0.01f, $"X: got {v.X}, expected {expected.X}");
        Assert.True(MathF.Abs(v.Y - expected.Y) < 0.01f, $"Y: got {v.Y}, expected {expected.Y}");
        Assert.True(MathF.Abs(v.Z - expected.Z) <= gravHalfStep + 0.01f,
            $"Z: got {v.Z}, expected ~{expected.Z} (±{gravHalfStep:F3} gravity half-step)");
    }

    /// <summary>
    /// END-TO-END (walking in): a player walking forward into the pad volume is launched once it overlaps —
    /// the realistic "step onto the jump-pad" case. Proves the touch fires off the player's own slide-move
    /// relink, not just a hand-placed overlap.
    /// </summary>
    [Fact]
    public void Player_WalkingIntoTriggerPush_GetsFlung()
    {
        // Start just OUTSIDE the −X edge of the pad (pad spans X∈[−48,48]; player hull half-width 16), on the
        // floor, walking +X straight into it.
        var h = Build(playerOrigin: new Vector3(-80f, 0f, 24f), wishMove: new Vector3(400f, 0f, 0f));
        h.Player.Flags |= EntFlags.OnGround;

        float maxZ = float.MinValue;
        bool launched = false;
        // A handful of ticks is plenty to cross ~16 units into the volume at running speed.
        for (int i = 0; i < 40 && !launched; i++)
        {
            h.Sim.Tick();
            maxZ = MathF.Max(maxZ, h.Player.Velocity.Z);
            if (h.Player.Velocity.Z > 300f)
                launched = true;
        }

        Assert.True(launched, $"player walking into the jump-pad was never launched (peak vel.Z={maxZ:F1})");
    }

    /// <summary>
    /// REGRESSION GUARD: a player nowhere near the pad is NOT launched (the touch-triggers pass must be a
    /// box-overlap test, not a blanket "touch every trigger"). Without this, a too-eager pass would fling
    /// players across the map.
    /// </summary>
    [Fact]
    public void Player_FarFromTriggerPush_IsNotLaunched()
    {
        // 1000 units away on +X, standing still on the floor.
        var h = Build(playerOrigin: new Vector3(1000f, 0f, 24f), wishMove: Vector3.Zero);
        h.Player.Flags |= EntFlags.OnGround;

        for (int i = 0; i < 10; i++)
            h.Sim.Tick();

        // No upward launch — at most it's resting/settling on the floor (small |vel.Z|).
        Assert.True(h.Player.Velocity.Z < 100f,
            $"a player far from the pad must not be launched, got vel.Z={h.Player.Velocity.Z}");
    }

    /// <summary>
    /// Pins the launch MATH itself (independent of the touch wiring): the QC solver gives an arc that, when the
    /// toucher rises with vel.Z and falls under gravity for the solved flight time, reaches the target's Z.
    /// Guards against a regression in <see cref="Jumppads.CalculateVelocity"/> masking a wiring fix.
    /// </summary>
    [Fact]
    public void CalculateVelocity_SolvesABallisticArcToTheTarget()
    {
        MapMover.ClearIndex();
        Api.Services = new EngineServices(new CollisionWorld());
        Api.Cvars.Set("sv_gravity", Gravity.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var toucher = new Entity { Origin = PadOrigin, Gravity = 1f, Mins = new Vector3(-16, -16, -24), Maxs = new Vector3(16, 16, 45) };
        var target = new Entity { Origin = TargetOrigin };

        Vector3 launch = Jumppads.CalculateVelocity(PadOrigin, target, PadHeight, toucher);

        // Horizontal speed and flight time: integrate the vertical motion z(t) = vz*t − 0.5*g*t² and check it
        // crosses the target's height (the target's box midpoint) at the time the horizontal closes the gap.
        Vector3 torg = target.Origin + (target.Mins + target.Maxs) * 0.5f;
        Vector3 flat = torg - PadOrigin; flat.Z = 0f;
        float sdist = flat.Length();
        float hspeed = new Vector2(launch.X, launch.Y).Length();
        Assert.True(hspeed > 1f, "needs forward speed to travel to the target");

        float flightTime = sdist / hspeed;
        float zAtArrival = PadOrigin.Z + launch.Z * flightTime - 0.5f * Gravity * flightTime * flightTime;
        Assert.True(MathF.Abs(zAtArrival - torg.Z) < 1.0f,
            $"the arc should reach the target height {torg.Z:F1} at arrival; got {zAtArrival:F1}");

        // Apex height check: the arc peaks at |height| above the higher endpoint (QC jumpheight = |ht| + zdist).
        float apex = PadOrigin.Z + launch.Z * launch.Z / (2f * Gravity);
        float expectedApex = MathF.Max(PadOrigin.Z, torg.Z) + PadHeight;
        Assert.True(MathF.Abs(apex - expectedApex) < 1.5f,
            $"arc apex {apex:F1} should be ~{expectedApex:F1} (|height| above the higher endpoint)");
    }

    /// <summary>Spawn a SOLID_NOT client prediction carrier (the local-player ghost) standing in the pad volume.</summary>
    private static Entity SpawnPredictionCarrier()
    {
        Entity carrier = Api.Entities.Spawn();
        carrier.ClassName = "player";
        carrier.Solid = Solid.Not;                       // the predictor's carrier is deliberately SOLID_NOT
        carrier.Flags |= EntFlags.Client | EntFlags.OnGround;
        carrier.Mins = new Vector3(-16f, -16f, -24f);
        carrier.Maxs = new Vector3(16f, 16f, 45f);
        carrier.Gravity = 1f;
        carrier.Health = 100f;
        carrier.Origin = new Vector3(0f, 0f, 24f);       // inside the pad's footprint, on the floor
        Api.Entities.SetOrigin(carrier, carrier.Origin);
        return carrier;
    }

    /// <summary>
    /// CLIENT PREDICTION: the local-player movement predictor's jump-pad pass
    /// (<see cref="TriggerTouch.PredictJumppadsAmbient"/>) launches a SOLID_NOT prediction carrier overlapping a
    /// trigger_push — applying ONLY the QC <c>jumppad_push</c> CSQC behavior (velocity + UNSET_ONGROUND) and NONE
    /// of the server-only (<c>#ifdef SVQC</c>) side effects (sound/effect debounce, SUB_UseTargets). This is what
    /// keeps the predicted local player in lockstep with the server's authoritative launch — without it the
    /// server launches but the client predicts an ordinary jump/fall, and the reconcile jitters the camera (the
    /// "jump through the floor / bounce" felt on a pad).
    /// </summary>
    [Fact]
    public void PredictJumppads_LaunchesSolidNotCarrier_VelocityOnly_NoSideEffects()
    {
        // Reuse the harness (Api + a trigger_push aimed at a target); park the harness player far away.
        var h = Build(playerOrigin: new Vector3(1000f, 0f, 24f), wishMove: Vector3.Zero);
        Entity carrier = SpawnPredictionCarrier();

        Vector3 expected = Jumppads.CalculateVelocity(carrier.Origin, h.Target, h.Pad.Height, carrier);
        float padLTimeBefore = h.Pad.PushLTime;          // server-only debounce; must stay untouched by prediction

        TriggerTouch.PredictJumppadsAmbient(carrier);

        // Launched along the SAME QC arc the server would compute — exactly (no move/gravity step in prediction).
        Vector3 v = carrier.Velocity;
        Assert.True(MathF.Abs(v.X - expected.X) < 0.01f, $"X: got {v.X}, expected {expected.X}");
        Assert.True(MathF.Abs(v.Y - expected.Y) < 0.01f, $"Y: got {v.Y}, expected {expected.Y}");
        Assert.True(MathF.Abs(v.Z - expected.Z) < 0.01f, $"Z: got {v.Z}, expected {expected.Z}");

        // UNSET_ONGROUND ran (it's shared CSQC+SVQC) ...
        Assert.False(carrier.OnGround, "predicted launch must clear the on-ground flag (QC UNSET_ONGROUND)");
        // ... but every server-only side effect was skipped: the pad's sound/effect debounce is untouched.
        Assert.Equal(padLTimeBefore, h.Pad.PushLTime);
    }

    /// <summary>
    /// Proves WHY the dedicated prediction pass exists: the server touch pass <see cref="TriggerTouch.Run"/>
    /// early-outs on a SOLID_NOT mover (DP's SV_TouchTriggers skips non-solid movers), so it would NEVER launch
    /// the prediction carrier — only <see cref="TriggerTouch.PredictJumppadsAmbient"/> does.
    /// </summary>
    [Fact]
    public void Run_SkipsSolidNotCarrier_ButPredictPassLaunchesIt()
    {
        var h = Build(playerOrigin: new Vector3(1000f, 0f, 24f), wishMove: Vector3.Zero);
        Entity carrier = SpawnPredictionCarrier();

        // The server pass refuses a SOLID_NOT mover: no launch, ground flag intact.
        TriggerTouch.RunAmbient(carrier);
        Assert.Equal(Vector3.Zero, carrier.Velocity);
        Assert.True(carrier.OnGround, "Run must not touch a SOLID_NOT mover");

        // The prediction pass DOES launch it.
        TriggerTouch.PredictJumppadsAmbient(carrier);
        Assert.True(carrier.Velocity.Z > 400f, $"prediction should launch the SOLID_NOT carrier; got {carrier.Velocity}");
        Assert.False(carrier.OnGround);
    }
}
