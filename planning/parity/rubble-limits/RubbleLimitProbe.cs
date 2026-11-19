using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Damage;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Formats.Vfs;
using VortexArena.Game.Client;
using VortexArena.Game.Loaders;
using VortexArena.Game.Menu;
using VortexArena.Server;
using QVec = System.Numerics.Vector3;

public partial class RubbleLimitProbe : Node3D
{
    private const string CasingLimit = "cl_casings_maxcount";
    private const string GibLimit = "cl_gibs_maxcount";
    private const string Eye = "models/gibs/eye.md3";
    private const string Skull = "models/gibs/bloodyskull.md3";

    private static void Check(bool condition, string phase)
    {
        if (!condition)
            throw new InvalidOperationException(phase);
        GD.Print($"PASS {phase}");
    }

    private static MultiMesh[] Batches(Node parent)
        => parent.GetChildren().OfType<MultiMeshInstance3D>().Select(node => node.Multimesh).ToArray();

    private static float[] Positions(Node parent)
        => Batches(parent).SelectMany(mesh => Enumerable.Range(0, mesh.VisibleInstanceCount)
            .Select(i => mesh.GetInstanceTransform(i).Origin.X)).OrderBy(x => x).ToArray();

    private static void CheckPositions(Node parent, string phase, params int[] expected)
    {
        var actual = Positions(parent);
        Check(actual.Length == expected.Length && actual.Zip(expected, (x, y) => MathF.Abs(x - y) < 2f).All(ok => ok),
            $"{phase} [{string.Join(", ", actual.Select(x => x.ToString("F1")))}]");
    }

    private static CollisionWorld DebrisWorld(int wallFlags = 0)
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(new QVec(0, -500, -100), new QVec(32, 500, 500), SuperContents.Solid, wallFlags));
        world.AddBrush(Brush.FromBox(new QVec(-500, -500, -32), new QVec(0, 500, 0), SuperContents.Solid));
        return world;
    }

    private EffectSystem DebrisEffects(int wallFlags = 0)
    {
        var effects = new EffectSystem();
        AddChild(effects);
        effects.Casings.SetProcess(false);
        effects.Gibs.SetProcess(false);
        effects.SetCollisionWorld(DebrisWorld(wallFlags));
        return effects;
    }

    private void CheckGibOptions(AssetLoader assets, VirtualFileSystem vfs)
    {
        var cvars = MenuState.Cvars;
        foreach (var setting in VortexArena.Engine.Particles.ParticleCvars.Defaults)
            cvars.Set(setting.Name, setting.Default);
        cvars.Set("ekg", "0");
        cvars.Set("cl_gentle", "0");
        cvars.Set("cl_gentle_gibs", "0");
        cvars.Set("cl_nogibs", "0");
        cvars.Set("cl_particlegibs", "0");
        cvars.Set(GibLimit, "100");
        var effects = new EffectSystem
        {
            ModelLoader = name => assets.LoadModel(name),
            TextureLoader = assets.LoadTexture,
            VfsTextLoader = name => vfs.Exists(name) ? vfs.ReadText(name) : null,
        };
        AddChild(effects);
        effects.SetCollisionWorld(new CollisionWorld());
        effects.Gibs.SetProcess(false);
        var origin = new QVec(0, 0, 96);
        int Models()
        {
            effects.Spawn("MODEL_GIB_SPLASH", origin);
            effects.Gibs._Process(0.001);
            return Positions(effects.Gibs).Length;
        }
        int before = Models();
        Check(before == 12, $"ordinary splash emits twelve models ({before})");
        var shower = effects.FaithfulParticles.Sim;
        var showerCells = shower.Pool.Take(shower.HighWater).Where(p => p.Active).Select(p => p.TexNum).ToArray();
        Check(showerCells.Length is >= 133 and <= 135 && showerCells.All(tex => tex is >= 24 and < 32),
            $"human shower uses the shipped spark and smoke cells ({showerCells.Length} particles)");
        cvars.Set("ekg", "1");
        int after = Models();
        Check(after - before == 52, $"EKG emits fifty-two models ({after - before})");
        cvars.Set("ekg", "0");
        cvars.Set("cl_nogibs", "0.5");
        GD.Seed(1902);
        before = after;
        after = Models();
        Check(after - before is > 0 and < 12, $"fractional suppression samples individual pieces ({after - before})");
        cvars.Set("cl_nogibs", "1");
        Check(Models() == after, "complete suppression emits no additional model");
        cvars.Set("cl_particlegibs", "1");
        void CheckDissolve(string expected)
        {
            var blocks = effects.GetInfoBlocks(expected);
            Check(blocks is { Count: > 0 }, $"mounted {expected} definition");
            effects.FaithfulParticles.Clear();
            effects.Spawn("model_gib_splash_2", origin);
            effects.Gibs._Process(0.001);
            var sim = effects.FaithfulParticles.Sim;
            var live = sim.Pool.Take(sim.HighWater).Where(p => p.Active).ToArray();
            Check(live.Length > 0 && Positions(effects.Gibs).Length == after
                && live.All(p => blocks!.Any(b => p.TexNum >= b.Tex0 && p.TexNum <= b.Tex1)),
                $"{expected} uses its mounted atlas cells and no models ({live.Length} particles)");
        }
        effects.FaithfulParticles.Clear();
        effects.Spawn("MODEL_GIB_SPLASH", origin);
        int ActiveParticles()
        {
            var sim = effects.FaithfulParticles.Sim;
            return sim.Pool.Take(sim.HighWater).Count(p => p.Active);
        }
        Check(ActiveParticles() == 0, "nogibs also suppresses the particle-only splash");
        cvars.Set("cl_gentle", "1");
        CheckDissolve("morphed_damage_dissolve");
        cvars.Set("cl_gentle", "0");
        cvars.Set("cl_gentle_gibs", "2");
        CheckDissolve("damage_dissolve");
        cvars.Set("cl_gentle_gibs", "3");
        CheckDissolve("happy_damage_dissolve");
        cvars.Set("cl_gentle_gibs", "0");
        cvars.Set("cl_nogibs", "0");
        CheckDissolve("particlegibs_damage_dissolve");
        cvars.Set("cl_particlegibs", "0");
        cvars.Set(GibLimit, "0");
        effects.FaithfulParticles.Clear();
        cvars.Set("cl_particles_quality", "0.5");
        effects.Spawn("model_gib_splash_2", origin);
        Check(ActiveParticles() == 31,
            "half-quality alien shower runs 25 calls through its species-specific block (31 particles)");
        effects.FaithfulParticles.Clear();
        cvars.Set("cl_particles_quality", "1");
        effects.Spawn("model_gib_splash_5", origin);
        var robot = effects.FaithfulParticles.Sim;
        Check(robot.Pool.Take(robot.HighWater).Any(p => p.Active && p.TexNum is >= 71 and <= 74),
            "robot shower emits the shipped metal spark and smoke artwork");
        effects.FaithfulParticles.Clear();
        cvars.Set("cl_particles_quality", "0");
        effects.Spawn("MODEL_GIB_SPLASH", origin);
        Check(ActiveParticles() == 0, "zero quality suppresses the shower");
        effects.QueueFree();
        cvars.Set("cl_particles_quality", "1");
        cvars.Set(GibLimit, "100");

        Transform3D TossAndMeasure()
        {
            // An unskinned fixture mesh has an identity model transform, so this reads the body's pose.
            var bodies = new ModelGibs { Cvars = cvars };
            AddChild(bodies);
            bodies.SetProcess(false);
            bodies.Toss("probe", origin, QVec.UnitX * 100, QVec.UnitX * 20, float.NegativeInfinity, false);
            bodies._Process(0.05);
            var pose = Batches(bodies).Single().GetInstanceTransform(0);
            bodies._Process(3.0);
            Check(Positions(bodies).Length == 0, "ordinary model expired by three seconds");
            bodies.QueueFree();
            return pose;
        }
        var stockPose = TossAndMeasure();
        Check(MathF.Abs(stockPose.Origin.X - 4.5f) < 0.01f && !stockPose.Basis.IsEqualApprox(Basis.Identity),
            "stock launch blends base and random velocity and tumbles");
        cvars.Set("cl_gibs_velocity_scale", "0");
        cvars.Set("cl_gibs_velocity_random", "0");
        cvars.Set("cl_gibs_velocity_up", "101");
        cvars.Set("cl_gibs_avelocity_scale", "0");
        cvars.Set("cl_gibs_lifetime", "2");
        var changed = TossAndMeasure();
        Check(MathF.Abs(changed.Origin.X) < 0.01f && MathF.Abs(changed.Origin.Y - stockPose.Origin.Y - 5f) < 0.01f
            && changed.Basis.IsEqualApprox(Basis.Identity), "live velocity, lift and zero angular scale change the launch");
        cvars.Set("cl_gibs_lifetime", "0");
        var instant = new ModelGibs { Cvars = cvars };
        AddChild(instant);
        instant.Toss("probe", origin, QVec.Zero, QVec.Zero, float.NegativeInfinity, false);
        instant._Process(0.001);
        Check(Positions(instant).Length == 0, "a live zero lifetime expires on the next draw");
        instant.QueueFree();
        cvars.Set("cl_gibs_lifetime", "2.5");
        cvars.Set("cl_gibs_velocity_scale", "0.5");
        cvars.Set("cl_gibs_velocity_random", "2");
        cvars.Set("cl_gibs_velocity_up", "1");
        cvars.Set("cl_gibs_avelocity_scale", "1");
    }

    private void CheckDebrisMotion()
    {
        var cvars = MenuState.Cvars;
        const string casingTic = "cl_casings_ticrate";
        const string casingSloppy = "cl_casings_sloppy";
        const string gibTic = "cl_gibs_ticrate";
        const string gibSloppy = "cl_gibs_sloppy";

        var timing = DebrisTiming.Read(cvars, casingTic, casingSloppy, 0.03125f);
        Check(timing.Ticrate == 0.03125f && timing.Sloppy, "unset casing timing defaults");
        timing = DebrisTiming.Read(cvars, gibTic, gibSloppy, 0.1f);
        Check(timing.Ticrate == 0.1f && timing.Sloppy, "unset gib timing defaults");
        var motion = new DebrisMotion(QVec.Zero, QVec.Zero, QVec.Zero);
        const float boundary = 0.03125f;
        float justBefore = MathF.BitDecrement(boundary);
        Check(motion.Schedule(justBefore, boundary, out _, out _) == 0, "last float before a tic remains residual");
        Check(motion.Schedule(boundary - justBefore, boundary, out _, out _) == 1, "exactly one tic commits at its boundary");
        Check(motion.Schedule(0.625, 0.015625f, out _, out double debt) == 32 && debt == 0.125,
            "32 tic cap retains unpaid time");
        Check(motion.Schedule(0.015625, 0.015625f, out _, out debt) == 9 && debt == 0,
            "next frame consumes catch-up debt");
        motion.Schedule(0.02, 0.1f, out _, out _);
        Check(motion.Schedule(0.03, 0f, out double step, out debt) == 1
            && System.Math.Abs(step - 0.05) < 1e-8 && debt == 0, "zero ticrate consumes pending time in one step");

        motion = new DebrisMotion(QVec.Zero, QVec.UnitZ * 100f, QVec.UnitY * 90f);
        var residual = motion.Draw(0.05f, 800f, true, float.NegativeInfinity, QVec.Zero, null, false, 0f, out _);
        Check(MathF.Abs(residual.Origin.Y - 4f) < 0.001f
            && residual.Basis.IsEqualApprox(new Basis(Vector3.Up, Mathf.DegToRad(4.5f)))
            && motion.Origin == QVec.Zero && motion.Velocity == QVec.UnitZ * 100f && motion.Angles == QVec.Zero,
            "residual gravity and tumble leave committed state intact");

        int bumps = 0;
        TraceResult BlockingSweep(QVec from, QVec _, QVec __)
        {
            bumps++;
            return new TraceResult { EndPos = from, PlaneNormal = QVec.UnitX };
        }
        motion = new DebrisMotion(QVec.Zero, QVec.UnitX * 10f, QVec.Zero);
        motion.Step(0.1f, 0f, 0.5f, float.NegativeInfinity, QVec.Zero, BlockingSweep,
            removeOnTouch: false, removeOnNoImpact: false, removeInSolid: false, out _);
        Check(bumps == 5, "one committed tic caps repeated brush impacts at five");

        cvars.Set(casingTic, "0.1");
        cvars.Set(casingSloppy, "0");
        cvars.Set(gibTic, "0.1");
        cvars.Set(gibSloppy, "0");
        // Wall positions below assume a 100 qu/s launch towards the wall.
        cvars.Set("cl_gibs_velocity_scale", "1");
        var effects = DebrisEffects();
        int sounds = 0;
        effects.Casings.SoundHook = (_, _) => sounds++;
        effects.Spawn("CASING_BULLET", new QVec(-10, 0, 30), new QVec(200, 0, 0));
        effects.Casings._Process(0.075);
        float clipped = Positions(effects.Casings).Single();
        Check(MathF.Abs(clipped) < 0.2f && sounds == 0, "strict casing residual clips at the BSP wall without sound");
        cvars.Set(casingSloppy, "1");
        effects.Casings._Process(0.005);
        Check(Positions(effects.Casings).Single() > 5f && sounds == 0, "sloppy casing residual passes through without committing");
        effects.Casings._Process(0.03);
        Check(Positions(effects.Casings).Single() < -0.75f && sounds == 1, "committed casing reflection and sound occur once");
        effects.SpawnCasing(new QVec(5, 0, 30), new QVec(40, 0, 0));
        effects.Casings._Process(0.1);
        Check(Positions(effects.Casings).Length == 1, "committed casing trace culls a spawn inside solid");
        cvars.Set(casingSloppy, "0");
        effects.SpawnCasing(new QVec(5, 0, 30), new QVec(40, 0, 0));
        effects.Casings._Process(0.01);
        Check(Positions(effects.Casings).Length == 1, "strict casing residual also culls startsolid");

        effects.Gibs.Toss(Eye, new QVec(-12, 0, 30), new QVec(100, 0, 0), default, 0f, false);
        effects.Gibs.Toss("models/gibs/chunk.mdl", new QVec(-12, 4, 30), new QVec(100, 0, 0), default, 0f, true);
        effects.Gibs._Process(0.06);
        Check(Positions(effects.Gibs).Length == 2 && Positions(effects.Gibs).All(x => MathF.Abs(x + 8) < 0.2f),
            "strict gib residual uses the eight-unit hull without removing chunks");
        cvars.Set(gibSloppy, "1");
        effects.Gibs._Process(0.01);
        Check(Positions(effects.Gibs).All(x => x > -6f), "sloppy gib residual ignores the wall");
        effects.Gibs._Process(0.04);
        Check(Positions(effects.Gibs).Single() < -8.3f, "committed gib wall hit reflects a limb and removes a chunk");
        effects.QueueFree();

        effects = DebrisEffects(wallFlags: Q3SurfaceFlags.NoImpact);
        effects.SpawnCasing(new QVec(-10, 0, 30), new QVec(200, 0, 0));
        effects.Gibs.Toss(Eye, new QVec(-12, 0, 30), new QVec(100, 0, 0), default, 0f, false);
        effects.Casings._Process(0.1);
        effects.Gibs._Process(0.1);
        Check(Positions(effects.Casings).Length + Positions(effects.Gibs).Length == 0, "NOIMPACT removes casing and ordinary gib");
        effects.QueueFree();

        effects = DebrisEffects();
        effects.Gibs.Toss("models/gibs/chunk.mdl", new QVec(-12, 0, 30), new QVec(100, 0, 0), default, 0f, true);
        effects.Gibs._Process(0.02);
        Check(Positions(effects.Gibs).Length == 1, "positive gib ticrate leaves an unfinished step");
        cvars.Set(gibTic, "0");
        effects.Gibs._Process(0.03);
        Check(Positions(effects.Gibs).Length == 0, "live zero gib ticrate commits the unfinished step");
        effects.QueueFree();

        cvars.Set(gibTic, "0.1");
        cvars.Set(casingTic, "0.03125");
        cvars.Set(casingSloppy, "0");
        cvars.Set(gibSloppy, "0");
        Transform3D[] Run(int fps)
        {
            GD.Seed(241);
            var scene = DebrisEffects();
            scene.Spawn("CASING_SHELL", new QVec(-30, 0, 52), new QVec(160, 0, 70));
            scene.Gibs.Toss(Eye, new QVec(-20, 0, 38), new QVec(180, 0, 30), default, 0f, false);
            scene.Spawn("RAPTOR_BOMB_SPREAD", new QVec(-20, 0, 50));
            const double duration = 0.67;
            int frames = (int)(fps * duration);
            for (int i = 0; i <= frames; i++)
            {
                double delta = i == frames ? duration - (double)frames / fps : 1.0 / fps;
                scene.Casings._Process(delta);
                scene.Gibs._Process(delta);
            }
            var poses = Batches(scene.Casings).Concat(Batches(scene.Gibs))
                .SelectMany(batch => Enumerable.Range(0, batch.VisibleInstanceCount).Select(batch.GetInstanceTransform))
                .ToArray();
            scene.QueueFree();
            return poses;
        }

        var reference = Run(60);
        Check(reference.Length == 5, "frame-rate fixture retains both debris kinds and three Raptor fragments");
        foreach (int fps in new[] { 30, 144 })
        {
            var actual = Run(fps);
            Check(reference.Length == actual.Length && reference.Zip(actual, (a, b) =>
                a.Origin.DistanceTo(b.Origin) < 0.001f && a.Basis.IsEqualApprox(b.Basis)).All(equal => equal),
                $"casing, model gib and Raptor poses agree at {fps} and 60 FPS");
        }
    }

    public override async void _Ready()
    {
        try
        {
            VortexArena.Game.WorldTint.EnsureRegistered();
            using var vfs = new VirtualFileSystem();
            Check(vfs.MountContentRoot(ProjectSettings.GlobalizePath("res://data")), "mount shipped assets");
            var assets = new AssetLoader(vfs);
            CheckGibOptions(assets, vfs);
            var effects = new EffectSystem { ModelLoader = name => assets.LoadModel(name) };
            AddChild(effects);
            var casings = effects.Casings;
            var gibs = effects.Gibs;
            casings.SetProcess(false);
            gibs.SetProcess(false);

            CheckDebrisMotion();
            var cvars = MenuState.Cvars;
            foreach (string gate in new[] { "cl_gentle_gibs", "cl_gentle", "cl_particlegibs", "cl_nogibs" })
                cvars.Set(gate, "0");
            void Casing(int x, bool shell = false) => effects.Spawn(shell ? "CASING_SHELL" : "CASING_BULLET", new QVec(x, 0, 10));
            void Gib(int x, bool skull = false) => gibs.Toss(skull ? Skull : Eye, new QVec(x, 0, 10), default, default, 0, false);
            void Pack()
            {
                casings._Process(0.001);
                gibs._Process(0.001);
            }

            cvars.Set(CasingLimit, "3");
            cvars.Set(GibLimit, "3");
            Casing(10); Casing(20, true); Casing(30); Casing(40, true);
            Gib(10); Gib(20, true); Gib(30); Gib(40, true);
            Pack();
            CheckPositions(casings, "casings evict oldest across both kinds", 20, 30, 40);
            CheckPositions(gibs, "gibs evict oldest across models", 20, 30, 40);
            Check(Batches(casings).All(batch => batch.Mesh is not CylinderMesh)
                && Batches(gibs).All(batch => batch.Mesh is not BoxMesh), "shipped casing and gib meshes used");

            cvars.Set(CasingLimit, "2");
            cvars.Set(GibLimit, "2");
            Pack();
            CheckPositions(casings, "casing shrink waits for spawn", 20, 30, 40);
            CheckPositions(gibs, "gib shrink waits for spawn", 20, 30, 40);
            Casing(50); Gib(50);
            Pack();
            CheckPositions(casings, "casing shrink keeps newest old item and incoming item", 40, 50);
            CheckPositions(gibs, "gib shrink keeps newest old item and incoming item", 40, 50);

            cvars.Set(CasingLimit, "0");
            cvars.Set(GibLimit, "0");
            Pack();
            CheckPositions(casings, "casing zero waits for spawn", 40, 50);
            CheckPositions(gibs, "gib zero waits for spawn", 40, 50);
            Casing(60);
            effects.Spawn("MODEL_GIB_SPLASH", new QVec(60, 0, 10));
            Pack();
            CheckPositions(casings, "casing zero clears on spawn");
            CheckPositions(gibs, "model gib splash clears at zero");
            effects.SpawnRaptorShellfrags(new QVec(80, 0, 10), default);
            Pack();
            CheckPositions(gibs, "raptor fragments retain their own budget", 80, 80, 80);

            cvars.Set(CasingLimit, "130");
            cvars.Set(GibLimit, "130");
            for (int i = 0; i < 130; i++)
            {
                Casing(i);
                Gib(i);
            }
            Pack();
            Check(Positions(casings).Length == 130 && Positions(gibs).Length == 133, "live limits rise above 100");

            cvars.Set(CasingLimit, "1e20");
            cvars.Set(GibLimit, "1e20");
            for (int i = 0; i < 1100; i++)
            {
                Casing(i);
                Gib(i);
            }
            Pack();
            Check(Positions(casings).Length == 1024 && Positions(gibs).Length == 1027, "oversized cvars stay bounded");

            var world = new GameWorld(new CollisionWorld());
            world.Boot("dm");
            var sink = new EffectEmitter.RecordingSink();
            EffectEmitter.Sink = sink;
            var body = new Entity { ClassName = PlayerBody.ClassName, TakeDamage = DamageMode.Aim, Alpha = 1f };
            body.SetResourceExplicit(ResourceType.Health, 100);
            Combat.Damage(body, body, body, 100000, DeathTypes.Kill, body.Origin, default);
            var splash = sink.Log.Single(request => request.EffectName == "model_gib_splash");
            Check(splash.Effect is not null && body.Alpha < 0, "corpse damage emits a registered model splash");

            cvars.Set(GibLimit, "2");
            effects.Spawn(splash.Effect!.Name, new QVec(0, 0, 10), splash.Velocity, splash.Count);
            Pack();
            Check(Positions(gibs).Length == 5, "normal model splash obeys live gib cap and leaves fragments");
            var camera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = 35,
                Position = new Vector3(0, 18, 42) };
            AddChild(camera);
            camera.LookAt(new Vector3(0, 10, 0));
            camera.MakeCurrent();
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-35, -25, 0) });

            cvars.Set(CasingLimit, "0");
            cvars.Set(GibLimit, "0");
            Casing(0); Gib(0);
            Pack();
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            byte[] empty = GetViewport().GetTexture().GetImage().GetData();

            cvars.Set(CasingLimit, "2");
            cvars.Set(GibLimit, "2");
            Casing(-15); Casing(-7, true); Gib(3); Gib(14, true);
            Pack();
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            Image visible = GetViewport().GetTexture().GetImage();
            int changes = empty.Zip(visible.GetData(), (a, b) => a != b).Count(changed => changed);
            Check(changes > 500, $"Compatibility viewport renders the real batches ({changes} changed channels)");
            if (System.Environment.GetEnvironmentVariable("RUBBLE_CAPTURE_PATH") is { } capture)
                Check(visible.SavePng(capture) == Error.Ok, "save viewport evidence");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }
}
