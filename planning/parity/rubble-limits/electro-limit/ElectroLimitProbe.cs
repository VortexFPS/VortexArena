using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Damage;
using VortexArena.Common.Physics;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Net;
using VortexArena.Server;
using Vec = System.Numerics.Vector3;

namespace VortexArena.Game.Net;

public sealed partial class ElectroLimitProbe : Node
{
    private ServerNet? _server;
    private ClientNet? _alpha, _beta;
    private float _now;
    private Vec _alphaAim, _betaAim;
    private readonly List<Vec> _alphaBlasts = new(), _betaBlasts = new();

    public override async void _Ready()
    {
        try
        {
            var collision = new CollisionWorld();
            collision.AddBrush(Brush.FromBox(new Vec(-3000, -3000, -100), new Vec(3000, 3000, -32), SuperContents.Solid));
            collision.BuildGrid();
            Vec first = new(1216, 248, -8), second = new(1216, 328, -8), observer = new(1216, 448, -8);
            var world = new GameWorld(collision, new EntityDict[] { new("info_player_deathmatch", first), new("info_player_deathmatch", observer) });
            world.Boot("dm");
            var cvars = world.Services.Cvars;
            cvars.Set("g_spawnshieldtime", "0");
            cvars.Set("g_projectiles_damage", "0");
            cvars.Set("g_projectiles_keep_owner", "1");
            var electro = (Electro)Weapons.ByName("electro")!;
            electro.Secondary.Count = 1;
            electro.Secondary.Ammo = 0;
            electro.Secondary.Refire = 1.2f;
            electro.Secondary.Lifetime = 80f;
            electro.Secondary.Stick = true;
            electro.Secondary.StickLifetime = 75f;
            electro.Secondary.TouchExplode = false;
            electro.Secondary.Speed = 1f;
            electro.Secondary.SpeedUp = 0f;
            electro.Secondary.Damage = 30f;
            electro.Secondary.EdgeDamage = 30f;
            electro.Secondary.Force = 0f;
            electro.Secondary.Radius = 64f;
            electro.Secondary.Limit = 0;

            int port = int.TryParse(System.Environment.GetEnvironmentVariable("ELECTRO_PROBE_PORT"), out int value) ? value : 29858;
            _server = ServerNet.Start(world, port, maxClients: 2) ?? throw new InvalidOperationException("server start");
            _alpha = Connect(port, "Alpha", _alphaBlasts);
            _beta = Connect(port, "Beta", _betaBlasts);
            await Pump(90);
            Check(_alpha.Accepted && _beta.Accepted, "both negotiated clients");
            _alpha.SendStringCommand("join");
            _beta.SendStringCommand("join");
            await Pump(60);
            Player a = world.Clients.Players.Single(p => p.NetName == "Alpha");
            Player b = world.Clients.Players.Single(p => p.NetName == "Beta");
            Check(!a.IsObserver && !b.IsObserver, "both owners joined");
            if (System.Environment.GetEnvironmentVariable("ELECTRO_FOLLOW") == "1")
            {
                bool observeOnly = System.Environment.GetEnvironmentVariable("ELECTRO_FOLLOW_RECORD_ONLY") == "1";
                bool controlled = System.Environment.GetEnvironmentVariable("ELECTRO_INPUT_CONTROL") == "1";
                if (controlled)
                {
                    _alphaAim = new Vec(0, 90, 0);
                    _betaAim = new Vec(0, 270, 0);
                    float muzzle = float.Parse(System.Environment.GetEnvironmentVariable("ELECTRO_MUZZLE_FORWARD") ?? "58",
                        System.Globalization.CultureInfo.InvariantCulture);
                    WeaponFiring.RegisterMuzzleOffset(electro.RegistryId, new Vec(muzzle, 0, 0));
                    cvars.Set("g_antilag", "0");
                    cvars.Set("sys_ticrate", "0.015625");
                }
                electro.Secondary.Limit = 1;
                electro.Secondary.Speed = 300f;
                Api.Entities.SetOrigin(b, second);
                if (controlled)
                    await Pump(5);
                Entity attached = await Launch(a, first, "attached");
                Check(attached.MoveType == MoveType.Follow && ReferenceEquals(attached.Aiment, b), "orb attached to Beta on ordinary collision");
                Vec start = attached.Origin;
                long order = attached.ElectroRubbleOrder;
                float savedDeadline = observeOnly ? attached.NextThink : ReadFollowDeadline(attached);
                GD.Print($"STICKY order={order} deadline={savedDeadline:R} offset={attached.ViewOfs} attachYaw={attached.PunchAngle.Y:R}");
                _betaAim = controlled ? new Vec(0, 90, 0) : Vec.Zero;
                Api.Entities.SetOrigin(b, observer);
                await Pump(6);
                if (controlled)
                    GD.Print($"CARRIER moved origin={b.Origin} yaw={b.Angles.Y:R} inputYaw={b.ViewAngles.Y:R}");
                Check(MathF.Abs(attached.Origin.Y - start.Y) > 100f, "live orb moved with Beta");
                FollowEvidence("moved", attached);
                float beforeDeath = world.Time;
                Combat.Damage(b, b, b, 10000f, DeathTypes.Kill, b.Origin, Vec.Zero);
                await Pump(1, world.Simulation.TickSeconds);
                GD.Print($"LOSS firstStep={world.Time - beforeDeath:R} move={attached.MoveType}");
                await Pump(10);
                FollowEvidence("dead", attached);
                if (!observeOnly)
                    Check(attached.MoveType == MoveType.Toss && attached.Solid == Solid.Corpse && attached.Aiment is null
                        && ReferenceEquals(attached.Owner, a) && attached.ElectroRubbleOrder == order && attached.NextThink == savedDeadline,
                        "death drops a collidable orb with its original owner, cap order and deadline");
                GD.Print($"DROPPED order={attached.ElectroRubbleOrder} deadline={attached.NextThink:R}");

                Check(world.Clients.Spawn(b), "respawn target before the next launch");
                Api.Entities.SetOrigin(b, second);
                Entity replacement = await Launch(a, first, "reattached");
                Check(attached.IsFreed && _alphaBlasts.Count == 1 && _betaBlasts.Count == 1,
                    "next launch forces the dropped oldest orb to detonate for both clients");
                _beta.Dispose();
                _beta = null;
                await Pump(100);
                FollowEvidence("disconnected", replacement);
                Check(b.IsFreed, "Beta disconnected through the ENet session");
                if (!observeOnly)
                {
                    Check(replacement.IsFreed && !Orbs().Any() && _alphaBlasts.Count == 1,
                        "disconnect removes the attachment without another impact");
                    Entity next = await Launch(a, first, "after_disconnect");
                    Check(!next.IsFreed && ReferenceEquals(next.Owner, a) && Orbs().Count() == 1 && _alphaBlasts.Count == 1,
                        "the deleted orb leaves the cap slot available without a forced detonation");
                }
                GetTree().Quit();
                return;
            }
            Entity unlimited = await Launch(a, new Vec(-1040, -448, -8), "unlimited");
            electro.Secondary.Limit = 2;
            Entity old = await Launch(a, first, "old_stuck");
            electro.Secondary.StickLifetime = 35f;
            Entity other = await Launch(b, observer, "other_owner");
            Entity middle = await Launch(a, second, "at_limit");
            Check(old.MoveType == MoveType.None && old.NextThink > middle.NextThink, "oldest stuck orb has the later deadline");
            Vec oldOrigin = old.Origin;
            Entity? newest = await FireAndSettle(a, first, "evict_oldest");
            Check(old.IsFreed && !middle.IsFreed && !other.IsFreed && !unlimited.IsFreed, "sticky launch age, owner and list membership");
            Check(newest is { IsFreed: false }, "mode zero protects overlapping newest orb from the ordinary blast");
            Check(_alphaBlasts.Count == 1 && _alphaBlasts.SequenceEqual(_betaBlasts)
                && Vec.Distance(_alphaBlasts[0], oldOrigin) < 1f, "both clients observed only the forced blast");

            electro.Secondary.Limit = 1;
            cvars.Set("g_projectiles_damage", "2");
            Vec middleOrigin = middle.Origin;
            await FireAndSettle(a, second, "collateral");
            Check(middle.IsFreed && !other.IsFreed && !unlimited.IsFreed && newest is { IsFreed: true },
                "lowered cap removed two listed orbs and the overlapping launch died from collateral");
            Check(Orbs().Count() == 2, "uncounted launch and other owner remain");
            Check(_alphaBlasts.Count == 4 && _alphaBlasts.SequenceEqual(_betaBlasts)
                && Vec.Distance(_alphaBlasts[1], middleOrigin) < 1f && Vec.Distance(_alphaBlasts[2], oldOrigin) < 1f
                && Vec.Distance(_alphaBlasts[3], middleOrigin) < 64f,
                "both clients observed forced detonation followed by collateral detonation");
            GetTree().Quit();

            async Task<Entity> Launch(Player actor, Vec origin, string tag)
                => (await FireAndSettle(actor, origin, tag)) ?? throw new InvalidOperationException($"missing launch {tag}");

            async Task<Entity?> FireAndSettle(Player actor, Vec origin, string tag)
            {
                var before = Orbs().ToHashSet();
                actor.MoveType = MoveType.None;
                actor.Velocity = Vec.Zero;
                Api.Entities.SetOrigin(actor, origin);
                actor.Angles = new Vec(0, 90, 0);
                actor.UnlimitedAmmo = true;
                actor.ActiveWeaponId = electro.RegistryId;
                var slot = new WeaponSlot(0);
                var state = actor.WeaponState(slot);
                state.State = WeaponFireState.Ready;
                state.ButtonAttack2 = true;
                state.CurrentWeaponId = electro.RegistryId;
                GD.Print($"ORBS {tag}");
                if (tag is "attached" or "reattached")
                {
                    GD.Print($"INPUTS shooter={actor.Origin} eye={actor.ViewOfs} hull={actor.Mins}/{actor.Maxs} yaw={actor.Angles.Y:R} " +
                        $"carrier={b.Origin} hull={b.Mins}/{b.Maxs} yaw={b.Angles.Y:R} gravity={world.Simulation.Gravity:R} tick={world.Simulation.TickSeconds:R}");
                }
                electro.WrThink(actor, slot, FireMode.Secondary);
                if (tag is "attached" or "reattached")
                {
                    var launched = Orbs().Single(orb => !before.Contains(orb));
                    GD.Print($"LAUNCH origin={launched.Origin} velocity={launched.Velocity} hull={launched.Mins}/{launched.Maxs}");
                }
                await Pump(40);
                foreach (var orb in Orbs())
                    GD.Print($"ORB slot={orb.Index} owner={orb.Owner?.NetName} order={orb.ElectroRubbleOrder} " +
                        $"move={orb.MoveType} origin={orb.Origin} deadline={orb.NextThink:F3} health={orb.Health:F1}");
                return Orbs().SingleOrDefault(orb => !before.Contains(orb));
            }
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }

    private static IEnumerable<Entity> Orbs() => Api.Entities.FindByClass("electro_orb");

    private static void FollowEvidence(string phase, Entity orb)
        => GD.Print($"FOLLOW {phase} owner={orb.Owner?.NetName} target={orb.Aiment?.NetName} targetFreed={orb.Aiment?.IsFreed} " +
            $"order={orb.ElectroRubbleOrder} move={orb.MoveType} solid={orb.Solid} origin={orb.Origin} next={orb.NextThink:F3}");

    // The disposable probe reads the actual saved expiry captured by Electro.OrbStick's follow callback.
    private static float ReadFollowDeadline(Entity orb)
    {
        object target = orb.Think?.Target ?? throw new InvalidOperationException("missing follow callback");
        return (float)(target.GetType().GetField("deadline")?.GetValue(target)
            ?? throw new InvalidOperationException("follow expiry capture changed"));
    }

    private ClientNet Connect(int port, string name, List<Vec> blasts)
    {
        var transport = NetTransport.Client.Connect("127.0.0.1", port) ?? throw new InvalidOperationException(name);
        var client = new ClientNet(transport, new StaticStep(), () => new InputCommand
        {
            DeltaTime = 1f / 60f, ViewAngles = name == "Alpha" ? _alphaAim : _betaAim,
        }) { LocalPlayerName = name };
        client.EffectReceived += e =>
        {
            if (e.EffectName != "ELECTRO_BALLEXPLODE" && e.EffectName != "electro_ballexplode")
                return;
            blasts.Add(e.Origin);
            GD.Print($"BLAST receiver={name} origin={e.Origin}");
        };
        return client;
    }

    private async Task Pump(int frames, float dt = 1f / 30f)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            _now += dt;
            _server!.Tick(dt);
            _alpha!.Poll();
            _beta?.Poll();
            _alpha.SendInput(_now);
            _beta?.SendInput(_now);
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
    }
}
