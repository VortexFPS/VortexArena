using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Physics;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Game.Client;
using VortexArena.Game.Hud;
using VortexArena.Net;
using VortexArena.Server;
using GVec3 = Godot.Vector3;

namespace VortexArena.Game.Net;

/// <summary>
/// An in-process loopback that runs the WHOLE networked stack end-to-end so it can be exercised (and headless
/// smoke-tested) without two machines: a real <see cref="ServerNet"/> on a <see cref="GameWorld"/> with a bot,
/// and a real <see cref="ClientNet"/> connected to it over localhost ENet. It wires every §2 piece together —
/// the build-parity + ECDSA session-auth handshake, delta-compressed snapshots, movevar replication, lag
/// compensation, and the client render path (<see cref="ClientEntityView"/> → <see cref="ClientWorld"/> +
/// <see cref="ViewEntityRenderer"/> held weapons + the <see cref="RadarPanel"/> ent_cs radar).
///
/// The client sees the bot as a remote networked Player (its own entity is excluded + predicted), so the
/// entity stream, weapon attachment, nameplate, and radar are all driven by genuine over-the-wire state.
/// Run it via <c>Main</c>'s <c>--net-loopback</c> command-line flag.
/// </summary>
public sealed partial class NetLoopback : Node3D
{
    private const int Port = 27600;

    private GameWorld _serverWorld = null!;
    private ServerNet _server = null!;
    private ClientNet _client = null!;
    private ClientWorld _render = null!;
    private ClientEntityView _entityView = null!;
    private RadarPanel _radar = null!;

    private float _now;
    private int _frame;
    private bool _loggedAccept;

    public override void _Ready()
    {
        // --- server: a test-floor world + a bot so the client has a remote entity to render ---
        _serverWorld = new GameWorld(BuildFloor());
        _serverWorld.Boot("dm");
        _serverWorld.Clients.ClientConnect(isBot: true, netName: "TargetBot");

        ServerNet? server = ServerNet.Start(_serverWorld, Port, maxClients: 8, serverName: "Loopback Server");
        if (server is null)
        {
            GD.PrintErr("[NetLoopback] could not start the server (port in use?).");
            return;
        }
        _server = server;

        // --- render layer (effects/projectiles/models) + the net→render bridge ---
        _render = new ClientWorld { Name = "Render" };
        AddChild(_render);

        // --- client: connect over localhost. A static movement step keeps the wire test focused on the
        //     snapshot/entity stream (we don't drive local movement here). ---
        ClientNet? client = ClientNet.Connect("127.0.0.1", Port, new StaticMovementStep(), SampleInput);
        if (client is null)
        {
            GD.PrintErr("[NetLoopback] could not create the client.");
            return;
        }
        _client = client;
        _client.LocalPlayerName = "LoopbackClient";

        _entityView = new ClientEntityView(_client, _render);
        AddChild(_entityView);

        // --- the ent_cs radar in a HUD layer ---
        var hud = new CanvasLayer { Name = "Hud" };
        AddChild(hud);
        _radar = new RadarPanel
        {
            Name = "Radar",
            Net = _client,
            Size = new Vector2(220, 220),
            Position = new Vector2(24, 24),
        };
        hud.AddChild(_radar);

        AddLight();
        GD.Print("[NetLoopback] server + client started on 127.0.0.1:" + Port);
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _now += dt;
        _frame++;

        _server?.Tick(dt);
        _client?.Poll();
        if (_client is { Accepted: true })
        {
            if (!_loggedAccept)
            {
                _loggedAccept = true;
                GD.Print($"[NetLoopback] handshake accepted: netId {_client.LocalNetId}, server '{_client.ServerName}'.");
            }
            _client.SendInput(_now);
        }

        // Periodic proof the entity stream is flowing (the bot should appear as a remote entity).
        if (_frame % 60 == 0 && _client is { Accepted: true })
            GD.Print($"[NetLoopback] frame {_frame}: {_client.RemoteIds.Count} remote entit{(_client.RemoteIds.Count == 1 ? "y" : "ies")} " +
                     $"(server time {_client.LatestServerTime:F2}).");
    }

    private InputCommand SampleInput() => new() { DeltaTime = 1f / 72f };

    private static CollisionWorld BuildFloor()
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(
            new System.Numerics.Vector3(-2048f, -2048f, -64f),
            new System.Numerics.Vector3(2048f, 2048f, 0f), SuperContents.Solid));
        world.BuildGrid();
        return world;
    }

    private void AddLight()
    {
        AddChild(new DirectionalLight3D { Name = "Sun", RotationDegrees = new GVec3(-50f, -30f, 0f) });
    }

    public override void _ExitTree()
    {
        _client?.Dispose();
        _server?.Dispose();
    }

    /// <summary>A no-op prediction step: the loopback exercises the snapshot/entity wire, not local movement.</summary>
    private sealed class StaticMovementStep : IMovementStep
    {
        public void Step(ref PredictedState state, in InputCommand cmd, in PlayerState vars) { }
    }
}
