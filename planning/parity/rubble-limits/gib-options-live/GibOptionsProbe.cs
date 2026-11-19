using System;
using System.Reflection;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Physics;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Particles;
using VortexArena.Formats.Vfs;
using VortexArena.Game.Client;
using VortexArena.Game.Loaders;
using VortexArena.Game.Menu;
using VortexArena.Net;
using VortexArena.Server;
using Vec = System.Numerics.Vector3;

namespace VortexArena.Game.Net;

public sealed partial class GibOptionsProbe : Node3D
{
    private readonly int _port = int.TryParse(System.Environment.GetEnvironmentVariable("GIB_PROBE_PORT"), out int port)
        ? port : 29797;
    private readonly string _output = System.Environment.GetEnvironmentVariable("GIB_PROBE_OUTPUT")!;
    private readonly bool _visual = System.Environment.GetEnvironmentVariable("GIB_PROBE_VISUAL") == "1";
    private readonly bool _skipParticleDefaults = System.Environment.GetEnvironmentVariable("GIB_PROBE_SKIP_DEFAULTS") == "1";
    private readonly bool _orthographic = System.Environment.GetEnvironmentVariable("GIB_PROBE_ORTHOGRAPHIC") == "1";
    private ServerNet? _server;
    private ClientNet? _alpha;
    private ClientNet? _beta;
    private NetGame? _handler;
    private float _now;
    public static bool Recording { get; private set; }

    public override async void _Ready()
    {
        try
        {
            WorldTint.EnsureRegistered();
            using var vfs = new VirtualFileSystem();
            Check(vfs.MountContentRoot(ProjectSettings.GlobalizePath("res://data")), "mount shipped data");
            var assets = new AssetSystem(vfs);
            NativeColour.ApplyAtBoot(assets);
            var loader = new AssetLoader(vfs, assets);
            var cvars = MenuState.Cvars;
            if (!_skipParticleDefaults)
                foreach (var (name, value, archived) in ParticleCvars.Defaults)
                    cvars.Register(name, value, archived ? CvarFlags.Save : CvarFlags.None);
            cvars.Set("cl_particles", "1");
            cvars.Set("cl_particles_modern", System.Environment.GetEnvironmentVariable("GIB_PROBE_PARTICLE_MODE") ?? "0");
            cvars.Set("cl_particles_modern_nosdf", "1"); // The controlled floor has no authored SDF.
            cvars.Set("cl_particles_quality", "1");
            cvars.Set("cl_gibs_ticrate", "0.5");
            cvars.Set("cl_gibs_sloppy", "0");
            cvars.Set("cl_gibs_maxcount", "100");
            cvars.Set("cl_gibs_lifetime", "2.5");

            var collision = new CollisionWorld();
            collision.AddBrush(Brush.FromBox(new Vec(-3000, -3000, -100), new Vec(3000, 3000, -40), SuperContents.Solid));
            collision.BuildGrid();
            var shooterOrigin = new Vec(1216, 248, -8);
            var victimOrigin = new Vec(1216, 306, -8);
            EntityDict[] spawns =
            {
                new("info_player_deathmatch", shooterOrigin),
                new("info_player_deathmatch", new Vec(1216, 448, -8)),
            };
            var world = new GameWorld(collision, spawns) { MapName = "stormkeep" };
            world.Boot("dm");
            world.Services.Cvars.Set("g_casings", "0");
            world.Services.Cvars.Set("g_balance_shotgun_primary_damage", "300");
            world.Services.Cvars.Set("g_balance_shotgun_primary_bullets", "1");
            world.Services.Cvars.Set("g_balance_shotgun_primary_spread", "0");
            world.Services.Cvars.Set("g_antilag", "0");
            world.Services.Cvars.Set("g_spawnshieldtime", "0");
            var shotgun = (Shotgun)Weapons.ByName("shotgun")!;
            shotgun.Configure();
            _server = ServerNet.Start(world, _port, maxClients: 2) ?? throw new InvalidOperationException("server start");
            _alpha = Connect("Alpha");
            _beta = Connect("Beta");

            var render = new ClientWorld { Assets = assets, AudioLoader = loader.LoadSound };
            var serverEffects = EffectEmitter.Sink;
            AddChild(render);
            // In this fixture the observing renderer stands in for a separate client process.
            EffectEmitter.Sink = serverEffects;
            render.Effects.ModelLoader = path => loader.LoadModel(path);
            render.Effects.SetCollisionWorld(collision);
            _handler = new NetGame();
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(NetGame).GetField("_render", fields)!.SetValue(_handler, render);
            var onEffect = typeof(NetGame).GetMethod("OnEffectReceived", fields)!
                .CreateDelegate<Action<ClientNet.EffectEvent>>(_handler);
            Node3D? backdrop = null;
            if (_visual)
            {
                const string mapPath = "maps/stormkeep.bsp";
                var bsp = loader.ReadBsp(mapPath) ?? throw new InvalidOperationException("Stormkeep render map");
                // The paired stock .ent overlay also omits the inline brush entities.
                var inlineModels = Enumerable.Range(1, bsp.Models.Length - 1).ToHashSet();
                backdrop = MapLoader.BuildMap(bsp, assets, mapPath, droppedSubmodels: inlineModels);
                AddChild(backdrop);
                backdrop.Hide();
            }
            bool perspective = _visual && !_orthographic;
            var camera = new Camera3D
            {
                Projection = perspective ? Camera3D.ProjectionType.Perspective : Camera3D.ProjectionType.Orthogonal,
                Fov = FirstPersonView.ComputeVerticalFov(100f, 1f), Near = 1f, Size = 180f,
            };
            AddChild(camera);
            camera.MakeCurrent();
            _alpha.EffectReceived += effect =>
            {
                bool gib = ModelSpeciesEffects.TryGetGibEvent(effect.EffectName, out GibEffectKind kind, out _);
                // The visual A/B measures gib particles alone. The matrix retains the full recipient stream.
                if (!_visual || (Recording && gib))
                    onEffect(effect);
                if (!Recording || !gib)
                    return;
                GD.Print($"WIRE {kind} {effect.EffectName} count={effect.Count} origin={effect.Origin} velocity={effect.Velocity}");
                if (kind != GibEffectKind.Splash || _visual)
                    return;
                camera.Position = Coords.ToGodot(effect.Origin + new Vec(-60, -165, 100));
                camera.LookAt(Coords.ToGodot(effect.Origin));
            };
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55, -20, 0) });

            await Pump(120);
            Check(_alpha.Accepted && _beta.Accepted, "two accepted sessions");
            _alpha.SendStringCommand("join");
            await Pump(40);
            _beta.SendStringCommand("join");
            await Pump(40);
            Player shooter = world.Clients.Players.Single(p => p.NetName == "Alpha");
            Player victim = world.Clients.Players.Single(p => p.NetName == "Beta");
            Check(!shooter.IsObserver && !victim.IsObserver, "both players joined");

            await Trial("default", "0", "0", "0", "0");
            if (!_visual)
                await Trial("gentle1", "1", "0", "0", "0");
            await Trial("gentle2", "2", "0", "0", "0");
            if (!_visual)
                await Trial("gentle3", "3", "0", "0", "0");
            await Trial("particles", "0", "1", "0", "0");
            if (!_visual)
                await Trial("ekg", "0", "0", "1", "0");
            await Trial("nogibs", "0", "0", "0", "1");
            GetTree().Quit();

            async Task Trial(string name, string gentle, string particles, string ekg, string nogibs)
            {
                cvars.Set("cl_gentle", "0");
                cvars.Set("cl_gentle_gibs", gentle);
                cvars.Set("cl_particlegibs", particles);
                cvars.Set("ekg", ekg);
                cvars.Set("cl_nogibs", nogibs);
                Api.Entities.SetOrigin(shooter, new Vec(900, 248, -8));
                if (victim.IsDead)
                    Check(world.Clients.Spawn(victim), $"respawn before {name}");
                shooter.MoveType = MoveType.None;
                victim.MoveType = MoveType.None;
                Api.Entities.SetOrigin(shooter, shooterOrigin);
                Api.Entities.SetOrigin(victim, victimOrigin);
                shooter.Angles = new Vec(0, 90, 0);
                if (_visual)
                {
                    Vector3 position = Coords.ToGodot(shooter.Origin + shooter.ViewOfs);
                    camera.Position = position;
                    camera.LookAt(position + Vector3.Forward);
                    backdrop!.Show();
                    render.Effects.FaithfulParticles.Clear();
                    GD.Print($"CAMERA {camera.Projection} active={camera == GetViewport().GetCamera3D()} " +
                        $"eye={shooter.Origin + shooter.ViewOfs} fov={camera.Fov} alpha={cvars.GetString(ParticleCvars.Alpha)}");
                }
                shooter.UnlimitedAmmo = true;
                shooter.ActiveWeaponId = shotgun.RegistryId;
                var slot = new WeaponSlot(0);
                var state = shooter.WeaponState(slot);
                state.State = WeaponFireState.Ready;
                state.ButtonAttack = true;
                state.CurrentWeaponId = shotgun.RegistryId;

                GD.Print($"GIB_TRIAL {name}");
                Recording = true;
                shotgun.WrThink(shooter, slot, FireMode.Primary);
                if (_visual)
                {
                    int maxChanged = 0;
                    for (int frame = 1; frame <= 5; frame++)
                    {
                        await Pump(1);
                        maxChanged = Math.Max(maxChanged, await CaptureParticles(render, camera, name, frame));
                    }
                    Recording = false;
                    Check(victim.IsDead, $"lethal graphical shot for {name}");
                    if (cvars.GetFloat(ParticleCvars.Modern) == 0f && !_skipParticleDefaults)
                        Check(name == "nogibs" ? maxChanged == 0 : maxChanged > 100,
                            $"{name} changes {maxChanged} pixels with the Original batch visible");
                    backdrop!.Hide();
                    await Pump(82);
                    await ToSignal(GetTree().CreateTimer(3.2), SceneTreeTimer.SignalName.Timeout);
                    return;
                }
                await Pump(5);
                Recording = false;
                Check(victim.IsDead, $"lethal player shot for {name}");
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                GetViewport().GetTexture().GetImage().SavePng($"{_output}/{name}.png");
                var backend = render.Effects.FaithfulParticles;
                var sim = backend.Sim;
                int active = sim.Pool.Take(sim.HighWater).Count(particle => particle.Active);
                int uploaded = backend.GetChildren()
                    .SelectMany(node => node.GetChildren()).OfType<MultiMeshInstance3D>()
                    .Where(draw => draw.IsVisibleInTree())
                    .Sum(draw => RenderingServer.MultimeshGetVisibleInstances(draw.Multimesh.GetRid()));
                GD.Print($"VIEWPORT faithful active={active} uploaded={uploaded}");
                await Pump(82);
                await ToSignal(GetTree().CreateTimer(3.2), SceneTreeTimer.SignalName.Timeout);
            }
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }

    private async Task<int> CaptureParticles(ClientWorld render, Camera3D camera, string trial, int frame)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var backend = render.Effects.FaithfulParticles;
        var sim = backend.Sim;
        var packed = (from renderer in backend.GetChildren()
                      from draw in renderer.GetChildren().OfType<MultiMeshInstance3D>()
                      where draw.IsVisibleInTree()
                      select draw).ToArray();
        int submitted = 0, inFrustum = 0;
        float maxColour = 0f, maxAlpha = 0f, atlasAlpha = 0f;
        var atlas = render.Effects.Font?.AtlasTexture?.GetImage();
        if (atlas?.IsCompressed() == true)
            atlas.Decompress();
        foreach (var draw in packed)
        {
            var rid = draw.Multimesh.GetRid();
            int count = RenderingServer.MultimeshGetVisibleInstances(rid);
            float[] buffer = RenderingServer.MultimeshGetBuffer(rid);
            var material = (ShaderMaterial)draw.MaterialOverride;
            var cells = material.GetShaderParameter("cell_rects").AsGodotArray();
            submitted += count;
            for (int instance = 0; instance < count; instance++)
            {
                int offset = instance * 20;
                var center = new Vector3(buffer[offset + 3], buffer[offset + 7], buffer[offset + 11]);
                if (!camera.IsPositionInFrustum(center))
                    continue;
                inFrustum++;
                maxColour = Math.Max(maxColour, Math.Max(buffer[offset + 12], Math.Max(buffer[offset + 13], buffer[offset + 14])));
                maxAlpha = Math.Max(maxAlpha, buffer[offset + 15]);
                int slot = (int)buffer[offset + 16];
                if (atlas is null || (uint)slot >= (uint)cells.Count)
                    continue;
                Vector4 rect = cells[slot].AsVector4();
                int x = Math.Clamp((int)((rect.X + rect.Z / 2f) * atlas.GetWidth()), 0, atlas.GetWidth() - 1);
                int y = Math.Clamp((int)((rect.Y + rect.W / 2f) * atlas.GetHeight()), 0, atlas.GetHeight() - 1);
                atlasAlpha = Math.Max(atlasAlpha, atlas.GetPixel(x, y).A);
            }
        }
        Image shown = GetViewport().GetTexture().GetImage();
        GetTree().Paused = true;
        backend.Hide();
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image suppressed = GetViewport().GetTexture().GetImage();
        backend.Show();
        GetTree().Paused = false;
        shown.Convert(Image.Format.Rgba8);
        suppressed.Convert(Image.Format.Rgba8);
        byte[] shownPixels = shown.GetData(), suppressedPixels = suppressed.GetData();
        int changed = 0, maxDifference = 0;
        for (int i = 0; i < shownPixels.Length; i += 4)
        {
            int delta = Math.Max(Math.Abs(shownPixels[i] - suppressedPixels[i]), Math.Max(
                Math.Abs(shownPixels[i + 1] - suppressedPixels[i + 1]), Math.Abs(shownPixels[i + 2] - suppressedPixels[i + 2])));
            if (delta > 4)
                changed++;
            maxDifference = Math.Max(maxDifference, delta);
        }
        GD.Print($"PIXELS {trial} frame={frame} time={sim.Now:F3} live={sim.LiveCount} submitted={submitted} inFrustum={inFrustum} " +
            $"vertexRgb={maxColour:F3} vertexAlpha={maxAlpha:F3} atlasCenterAlpha={atlasAlpha:F3} pixels={changed} maxDelta={maxDifference}");
        Check(shown.SavePng($"{_output}/{trial}-{frame}-shown.png") == Error.Ok, "save shown frame");
        Check(suppressed.SavePng($"{_output}/{trial}-{frame}-suppressed.png") == Error.Ok, "save suppressed frame");
        return changed;
    }

    private ClientNet Connect(string name)
    {
        var transport = NetTransport.Client.Connect("127.0.0.1", _port) ?? throw new InvalidOperationException(name);
        return new ClientNet(transport, new StaticStep(), () => new InputCommand { DeltaTime = 1f / 60f })
        {
            LocalPlayerName = name,
        };
    }

    private async Task Pump(int frames)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            const float dt = 1f / 30f;
            _now += dt;
            _server!.Tick(dt);
            _alpha!.Poll();
            _beta!.Poll();
            _alpha.SendInput(_now);
            _beta.SendInput(_now);
        }
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        GD.Print($"PASS {label}");
    }

    private sealed class StaticStep : IMovementStep
    {
        public void Step(ref PredictedState state, in InputCommand command, in PlayerState player) { }
    }

    public override void _ExitTree()
    {
        _alpha?.Dispose();
        _beta?.Dispose();
        _server?.Dispose();
        _handler?.Free();
    }
}
