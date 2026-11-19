using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Damage;
using VortexArena.Common.Physics;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Formats.Bsp;
using VortexArena.Formats.Vfs;
using VortexArena.Game.Client;
using VortexArena.Game.Loaders;
using VortexArena.Game.Menu;
using VortexArena.Net;
using VortexArena.Server;
using Vec = System.Numerics.Vector3;

namespace VortexArena.Game.Net;

public sealed partial class CasingNetworkProbe : Node3D
{
    private ServerNet _server = null!;
    private ClientNet _alpha = null!;
    private ClientNet _beta = null!;
    private NetGame? _handler;
    private float _now;
    private InputButtons _observerButtons;
    private readonly int _port = int.TryParse(System.Environment.GetEnvironmentVariable("CASING_PROBE_PORT"), out int port)
        ? port : 29779;

    private static void Check(bool passed, string label)
    {
        if (!passed) throw new InvalidOperationException(label);
        GD.Print($"PASS {label}");
    }

    private async Task Pump(int frames = 10)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            const float dt = 1f / 60f;
            _now += dt;
            _server.Tick(dt);
            foreach (ClientNet client in new[] { _alpha, _beta })
            {
                client.Poll();
                if (client.Accepted)
                    client.SendInput(_now);
            }
        }
    }

    public override async void _Ready()
    {
        try
        {
            WorldTint.EnsureRegistered();
            using var vfs = new VirtualFileSystem();
            Check(vfs.MountContentRoot(ProjectSettings.GlobalizePath("res://data")), "mounted shipped assets");
            BspData bsp = BspReader.Read(vfs.ReadBytes("maps/stormkeep.bsp"));
            var pvs = new BspPvs(bsp);
            var collision = new CollisionWorld();
            collision.AddBrush(Brush.FromBox(new Vec(-3000, -3000, -100), new Vec(3000, 3000, -40), SuperContents.Solid));
            collision.BuildGrid();
            var spawns = new[] { new Vec(1216, 248, -8), new Vec(1216, 448, -8) }
                .Select(origin => new EntityDict("info_player_deathmatch", origin)).ToArray();
            var world = new GameWorld(collision, spawns) { MapName = "stormkeep", Pvs = pvs };
            world.Boot("dm");
            _server = ServerNet.Start(world, _port, maxClients: 3) ?? throw new InvalidOperationException("server start");
            _alpha = Connect("Alpha");
            _beta = Connect("Beta");

            var settingsA = new Dictionary<string, string> { ["cl_casings"] = "1", ["r_drawviewmodel"] = "1" };
            var settingsB = new Dictionary<string, string>(settingsA);
            _alpha.ReplicatedCvarSource = name => settingsA.GetValueOrDefault(name, "");
            _beta.ReplicatedCvarSource = name => settingsB.GetValueOrDefault(name, "");

            var inputsA = new List<ClientNet.EffectEvent>();
            var inputsB = new List<ClientNet.EffectEvent>();
            var assets = new AssetLoader(vfs);
            var render = new ClientWorld();
            AddChild(render);
            var effects = render.Effects;
            effects.ModelLoader = name => assets.LoadModel(name);
            var camera = new Camera3D();
            AddChild(camera);
            var viewModel = new ViewModel();
            camera.AddChild(viewModel);

            // Assemble the production handler's scene dependencies without booting a second match.
            var handler = _handler = new NetGame();
            const BindingFlags instanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(NetGame).GetField("_render", instanceFields)!.SetValue(handler, render);
            typeof(NetGame).GetField("_camera", instanceFields)!.SetValue(handler, camera);
            typeof(NetGame).GetField("_viewModel", instanceFields)!.SetValue(handler, viewModel);
            var receiveEffect = typeof(NetGame).GetMethod("OnEffectReceived", instanceFields)!
                .CreateDelegate<Action<ClientNet.EffectEvent>>(handler);
            effects.Casings.SetProcess(false);
            effects.Gibs.SetProcess(false);
            MenuState.Cvars.Set("cl_casings_maxcount", "1");
            MenuState.Cvars.Set("cl_gibs_maxcount", "1");
            foreach (string gate in new[] { "cl_gentle_gibs", "cl_gentle", "cl_particlegibs", "cl_nogibs" })
                MenuState.Cvars.Set(gate, "0");

            _alpha.EffectReceived += effect =>
            {
                if (Relevant(effect)) inputsA.Add(effect);
            };
            void ReceiveBetaEffect(ClientNet.EffectEvent effect)
            {
                if (!Relevant(effect)) return;
                inputsB.Add(effect);
                receiveEffect(effect);
            }
            _beta.EffectReceived += ReceiveBetaEffect;

            await Pump(120);
            Check(_alpha.Accepted && _beta.Accepted, "two accepted ENet clients");
            _alpha.SendStringCommand("join");
            await Pump(40);
            _beta.SendStringCommand("spectate Alpha");
            await Pump(20);

            Player shooter = world.Clients.Players.Single(player => player.NetName == "Alpha");
            Player observer = world.Clients.Players.Single(player => player.NetName == "Beta");
            Check(!shooter.IsObserver && observer.IsObserver && observer.Spectatee == shooter, $"shooter and following observer (Alpha observer={shooter.IsObserver}, Beta observer={observer.IsObserver}, follows={observer.Spectatee?.NetName})");
            Check(world.Commands.GetClientCvar(observer, "cl_casings") == "1", "automatic client cvar replication");

            var shooterOrigin = new Vec(1216, 248, -8);
            Api.Entities.SetOrigin(shooter, shooterOrigin);
            shooter.MoveType = MoveType.None;
            await Shot("0", "", "");
            await Shot("1", "CASING_SHELL:view", "CASING_SHELL:view");
            await Shot("2", "CASING_SHELL:view,CASING_BULLET:view", "CASING_SHELL:view,CASING_BULLET:view");

            MenuState.Cvars.Set("cl_gunoffset", "7 4 3");
            MenuState.Cvars.Set("chase_active", "0");
            await Pump(2);
            Check(viewModel.GunOffset == new Vector3(7, 4, 3), "viewmodel read the nonzero cl_gunoffset");
            await Shot("2", "CASING_SHELL:view", "CASING_SHELL:view", shellOnly: true);
            CheckCasingPosition(new Vec(1212, 255, 33), "real client handler applies forward, left and up gun offsets");
            MenuState.Cvars.Set("chase_active", "1");
            await Shot("2", "CASING_SHELL:view", "CASING_SHELL:view", shellOnly: true);
            CheckCasingPosition(new Vec(1216, 248, 30), "real client handler suppresses the gun offset in chase mode");
            MenuState.Cvars.Set("cl_gunoffset", "0 0 0");
            MenuState.Cvars.Set("chase_active", "0");

            inputsA.Clear();
            inputsB.Clear();
            var endFrame = world.Simulation.EndFrame;
            world.Simulation.EndFrame = () =>
            {
                world.Simulation.EndFrame = endFrame;
                endFrame?.Invoke();
                WeaponFiring.EjectCasing(shooter, new Vec(1216, 248, 30), WeaponFiring.CasingType.Shell);
                world.Clients.StopSpectating(observer);
                observer.MoveType = MoveType.None;
                Api.Entities.SetOrigin(observer, new Vec(-1040, -448, 8));
                WeaponFiring.EjectCasing(shooter, new Vec(1216, 250, 30), WeaponFiring.CasingType.Shell);
                Api.Entities.SetOrigin(observer, new Vec(1216, 448, -8));
                WeaponFiring.EjectCasing(shooter, new Vec(1216, 252, 30));
                Api.Entities.SetOrigin(observer, new Vec(-1040, -448, 8));
            };
            await Pump();
            Check(Describe(inputsA) == "CASING_SHELL:view,CASING_SHELL:view,CASING_BULLET:view",
                "shooter received all three emissions from the same simulation frame");
            Check(Describe(inputsB) == "CASING_SHELL:view,CASING_BULLET:world"
                && inputsB[0].Origin.Y == 248 && inputsB[1].Origin.Y == 252,
                $"follower and PVS decisions use emission-time state, received [{Describe(inputsB)}]");
            world.Clients.Spectate(observer, shooter);

            inputsA.Clear();
            inputsB.Clear();
            int lastFollowY = 260;
            world.Simulation.EndFrame = () =>
            {
                endFrame?.Invoke();
                if (observer.Spectatee == shooter)
                    WeaponFiring.EjectCasing(shooter, new Vec(1216, ++lastFollowY, 30), WeaponFiring.CasingType.Shell);
            };
            _observerButtons = InputButtons.Attack2;
            for (int frame = 0; frame < 30 && observer.Spectatee is not null; frame++)
                await Pump(1);
            _observerButtons = 0;
            world.Simulation.EndFrame = endFrame;
            Check(observer.Spectatee is null, "observer released follow through normal ENet attack2 input");
            await Pump();
            Check(inputsB.Any(effect => effect.Origin.Y == lastFollowY && effect.FirstPersonCasing),
                "last casing before the normal follow release arrived first-person");
            world.Clients.Spectate(observer, shooter);

            settingsB["r_drawviewmodel"] = "0";
            await Pump(110);
            await Shot("2", "CASING_SHELL:view", "", shellOnly: true);
            settingsB["r_drawviewmodel"] = "1";
            settingsB["cl_casings"] = "0";
            await Pump(110);
            await Shot("2", "CASING_SHELL:view", "", shellOnly: true);
            settingsA["r_drawviewmodel"] = "0";
            settingsB["cl_casings"] = "1";
            await Pump(110);
            await Shot("2", "", "CASING_SHELL:view", shellOnly: true);

            settingsA["r_drawviewmodel"] = "1";
            settingsB["r_drawviewmodel"] = "0";
            _beta.SendStringCommand("join");
            await Pump(110);
            Check(!observer.IsObserver, "observer joined as an independent viewer");
            observer.MoveType = MoveType.None;
            Api.Entities.SetOrigin(observer, new Vec(1216, 448, -8));
            await Shot("2", "CASING_SHELL:view", "CASING_SHELL:world", shellOnly: true);
            Api.Entities.SetOrigin(observer, new Vec(-1040, -448, 8));
            await Shot("2", "CASING_SHELL:view", "", shellOnly: true);

            Api.Entities.SetOrigin(observer, new Vec(1216, 448, -8));
            inputsA.Clear();
            inputsB.Clear();
            var first = new Vec(1216, 248, 30);
            var last = new Vec(1216, 311, 30);
            WeaponFiring.EjectCasing(shooter, first, WeaponFiring.CasingType.Shell);
            var body = new Entity { ClassName = PlayerBody.ClassName, TakeDamage = DamageMode.Aim, Alpha = 1f };
            body.Origin = new Vec(1216, 300, 30);
            body.SetResourceExplicit(ResourceType.Health, 100);
            Combat.Damage(body, body, shooter, 100000, DeathTypes.Kill, body.Origin, default);
            WeaponFiring.EjectCasing(shooter, last);
            await Pump();
            Check(Describe(inputsA) == "CASING_SHELL:view,MODEL_GIB_SPLASH:world,CASING_BULLET:view", "shooter received casing, corpse splash, casing in enqueue order");
            Check(Describe(inputsB) == "CASING_SHELL:world,MODEL_GIB_SPLASH:world,CASING_BULLET:world", "viewer received casing, corpse splash, casing in enqueue order");
            effects.Casings._Process(0.001);
            effects.Gibs._Process(0.001);
            Vec[] casings = Positions(effects.Casings);
            Check(casings.Length == 1 && Vec.Distance(casings[0], last) < 0.2f, "same-frame cap retains the last casing across kinds");
            Check(Positions(effects.Gibs).Length == 1, "same-frame splash respects the independent gib cap");
            inputsA.Clear();
            inputsB.Clear();
            Api.Entities.SetOrigin(shooter, shooterOrigin);
            Api.Entities.SetOrigin(observer, new Vec(1216, 306, -8));
            shooter.Angles = new Vec(0, 90, 0);
            var shotgun = (Shotgun)Weapons.ByName("shotgun")!;
            world.Services.Cvars.Set("g_balance_shotgun_primary_damage", "300");
            world.Services.Cvars.Set("g_balance_shotgun_primary_bullets", "1");
            world.Services.Cvars.Set("g_balance_shotgun_primary_spread", "0");
            world.Services.Cvars.Set("g_antilag", "0");
            shotgun.Configure();
            shooter.UnlimitedAmmo = true;
            shooter.ActiveWeaponId = shotgun.RegistryId;
            var slot = new WeaponSlot(0);
            shooter.WeaponState(slot).State = WeaponFireState.Ready;
            shooter.WeaponState(slot).ButtonAttack = true;
            shooter.WeaponState(slot).CurrentWeaponId = shotgun.RegistryId;
            shotgun.WrThink(shooter, slot, FireMode.Primary);
            await Pump();
            GD.Print($"LETHAL Alpha [{Describe(inputsA)}] Beta [{Describe(inputsB)}] target dead={observer.IsDead}");
            Check(observer.IsDead && Describe(inputsA) == "MODEL_GIB_SPLASH:world,CASING_SHELL:view"
                && Describe(inputsB) == "MODEL_GIB_SPLASH:world,CASING_SHELL:world",
                "lethal shotgun attack sends the model splash before its shell");
            // Keep the send queue pending while a replacement client completes its handshake.
            world.Services.Cvars.Set("slowmo", "0");
            await Pump();
            inputsA.Clear();
            inputsB.Clear();
            Api.Entities.SetOrigin(observer, new Vec(1216, 448, -8));
            WeaponFiring.EjectCasing(shooter, new Vec(1216, 248, 30), WeaponFiring.CasingType.Shell);
            _beta.Dispose();
            _beta = Connect("Gamma");
            _beta.ReplicatedCvarSource = name => settingsB.GetValueOrDefault(name, "");
            _beta.EffectReceived += ReceiveBetaEffect;
            await Pump(120);
            Check(_beta.Accepted, "replacement session accepted with a casing queued");
            Player replacement = world.Clients.Players.Single(player => player.NetName == "Gamma");
            replacement.MoveType = MoveType.None;
            Api.Entities.SetOrigin(replacement, new Vec(1216, 448, -8));
            world.Services.Cvars.Set("slowmo", "1");
            await Pump();
            Check(Describe(inputsA) == "CASING_SHELL:view" && inputsB.Count == 0,
                "replacement session does not receive a casing selected for the previous session");
            await Shot("2", "CASING_SHELL:view", "CASING_SHELL:world", shellOnly: true);
            GetTree().Quit();

            void CheckCasingPosition(Vec expected, string label)
            {
                effects.Casings._Process(0.001);
                Vec[] positions = Positions(effects.Casings);
                Check(positions.Length == 1 && Vec.Distance(positions[0], expected) < 0.2f,
                    $"{label}, expected {expected}, rendered [{string.Join(", ", positions)}]");
            }

            async Task Shot(string mode, string alpha, string beta, bool shellOnly = false)
            {
                inputsA.Clear();
                inputsB.Clear();
                world.Services.Cvars.Set("g_casings", mode);
                WeaponFiring.EjectCasing(shooter, new Vec(1216, 248, 30), WeaponFiring.CasingType.Shell);
                if (!shellOnly)
                    WeaponFiring.EjectCasing(shooter, new Vec(1216, 250, 30));
                await Pump();
                string a = Describe(inputsA);
                string b = Describe(inputsB);
                Check(a == alpha && b == beta, $"mode {mode} shooter [{a}] Beta [{b}], expected [{alpha}] [{beta}]");
            }
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }

    private ClientNet Connect(string name)
    {
        var transport = NetTransport.Client.Connect("127.0.0.1", _port) ?? throw new InvalidOperationException(name);
        return new ClientNet(transport, new StaticMovementStep(), () => new InputCommand
        {
            DeltaTime = 1f / 60f,
            Buttons = name == "Beta" ? (int)_observerButtons : 0,
        })
        {
            LocalPlayerName = name,
        };
    }

    private sealed class StaticMovementStep : IMovementStep
    {
        public void Step(ref PredictedState state, in InputCommand cmd, in PlayerState vars) { }
    }

    private static bool Relevant(ClientNet.EffectEvent effect)
        => effect.EffectName is "CASING_SHELL" or "CASING_BULLET" or "MODEL_GIB_SPLASH";

    private static string Describe(List<ClientNet.EffectEvent> events)
        => string.Join(",", events.Select(effect => $"{effect.EffectName}:{(effect.FirstPersonCasing ? "view" : "world")}"));

    private static Vec[] Positions(Node parent)
        => parent.GetChildren().OfType<MultiMeshInstance3D>()
            .SelectMany(node => Enumerable.Range(0, node.Multimesh.VisibleInstanceCount)
                .Select(i => Coords.ToQuake(node.Multimesh.GetInstanceTransform(i).Origin))).ToArray();

    public override void _ExitTree()
    {
        _alpha?.Dispose();
        _beta?.Dispose();
        _server?.Dispose();
        _handler?.Free();
    }
}
