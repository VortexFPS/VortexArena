using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Server.Bot;

/// <summary>
/// Manages the bot population and drives each bot's brain every server frame — the C# port of the bot
/// management slice of server/bot/default/bot.qc (bot_spawn / bot_fixcount / bot_serverframe) and the
/// HavocBot strategy-token rotation.
///
/// Usage from the host server loop:
///   - call <see cref="AddBot"/> (once per desired bot) to create + spawn an AI <see cref="Player"/>;
///   - call <see cref="Frame"/> once per tick with the frame time — it ticks every live bot's
///     <see cref="BotBrain.Think"/>.
///
/// Player discovery: in this port clients are NOT in the engine entity table (see ClientManager), so the
/// controller maintains a roster (<see cref="Players"/>) and feeds it to each brain for enemy selection.
/// Register human players too (via <see cref="RegisterPlayer"/>) so bots can see and fight them.
///
/// This class is deliberately decoupled from <c>ClientManager</c>: it accepts a spawn callback so the host
/// can route bot creation through its own connect/spawn pipeline (recommended), or fall back to a built-in
/// spawn that constructs a <see cref="Player"/> and places it via <see cref="SpawnSystem"/>.
/// </summary>
public sealed class BotController
{
    /// <summary>
    /// Host hook that creates + spawns a bot player and returns it (e.g. ClientManager.ClientConnect(isBot:true)).
    /// Receives the requested bot net name. If null, <see cref="AddBot"/> uses <see cref="DefaultSpawn"/>.
    /// </summary>
    public Func<string, Player>? SpawnBot;

    /// <summary>The shared map waypoint graph handed to every bot brain (QC g_waypoints).</summary>
    public WaypointNetwork? Network;

    /// <summary>Default skill for newly added bots (QC <c>skill</c> cvar), 0..10 or &gt;100 for SUPERBOT.</summary>
    public float DefaultSkill = 5f;

    /// <summary>Active gametype NetName, used to pick each bot's role (QC havocbot_chooserole dispatch).</summary>
    public string? GameTypeNetName;

    /// <summary>The active gametype singleton, handed to each brain so objective roles can read its state.</summary>
    public GameType? GameType;

    /// <summary>
    /// Supplies each bot's skill (handed to <see cref="Teamplay.SkillProvider"/> by the host) so skill-weighted
    /// team balance can weigh bots. Maps a bot's player to its brain's skill.
    /// </summary>
    public float SkillOf(Player p)
    {
        int idx = _bots.FindIndex(b => ReferenceEquals(b.Bot, p));
        return idx >= 0 ? _bots[idx].Skill : DefaultSkill;
    }

    private readonly List<BotBrain> _bots = new();
    private readonly List<Player> _roster = new();
    private int _seedCounter = 1;

    /// <summary>The bot brains under management (QC bot_list).</summary>
    public IReadOnlyList<BotBrain> Bots => _bots;

    /// <summary>Every player the bots are aware of — bots and registered humans (QC the client list).</summary>
    public IReadOnlyList<Player> Players => _roster;

    /// <summary>QC <c>currentbots</c>.</summary>
    public int BotCount => _bots.Count;

    /// <summary>
    /// Register an externally-managed player (typically a human) so bots can target it. Idempotent. Bots
    /// added via <see cref="AddBot"/> are registered automatically.
    /// </summary>
    public void RegisterPlayer(Player p)
    {
        if (!_roster.Contains(p))
            _roster.Add(p);
    }

    /// <summary>Stop tracking a player (QC ClientDisconnect): removes from the roster and any bot brain.</summary>
    public void UnregisterPlayer(Player p)
    {
        _roster.Remove(p);
        int idx = _bots.FindIndex(b => ReferenceEquals(b.Bot, p));
        if (idx >= 0)
            _bots.RemoveAt(idx);
        // any bot targeting it drops the stale enemy next think (ShouldAttack rejects freed/dead)
    }

    /// <summary>
    /// Create, spawn and start driving a new bot (QC bot_spawn + havocbot_setupbot). Uses <see cref="SpawnBot"/>
    /// if set, else the built-in <see cref="DefaultSpawn"/>. Returns the bot's brain. <paramref name="skill"/>
    /// overrides <see cref="DefaultSkill"/> when provided.
    /// </summary>
    public BotBrain AddBot(string? name = null, float? skill = null)
    {
        string botName = name ?? $"[BOT] {_seedCounter}";
        Player p = SpawnBot is not null ? SpawnBot(botName) : DefaultSpawn(botName);
        p.IsBot = true;

        var brain = new BotBrain(p, Network, skill ?? DefaultSkill, seed: _seedCounter++)
        {
            Role = BotRoles.ChooseRole(GameTypeNetName),
            PlayerProvider = () => _roster, // bots see the controller roster (clients aren't in the entity table)
            GameTypeNetName = GameTypeNetName,
            GameType = GameType,            // objective roles read the gametype's state through this
        };
        // keep the network reference live in case it's assigned after construction
        brain.Network = Network;

        _bots.Add(brain);
        RegisterPlayer(p);
        return brain;
    }

    /// <summary>Remove a bot (QC bot_removenewest / disconnect): stop driving it and drop it from the roster.</summary>
    public bool RemoveBot(BotBrain brain)
    {
        bool removed = _bots.Remove(brain);
        _roster.Remove(brain.Bot);
        return removed;
    }

    /// <summary>
    /// Advance all bots one server frame (QC bot_serverframe -&gt; each bot's bot_think/havocbot_ai).
    /// <paramref name="dt"/> is the tick length in seconds. Dead bots are ticked too (their Think early-outs
    /// and clears the route, matching QC). Re-applies the shared network/role each frame so a mid-match
    /// network swap or gametype change propagates.
    /// </summary>
    public void Frame(float dt)
    {
        for (int i = 0; i < _bots.Count; i++)
        {
            var brain = _bots[i];
            if (brain.Bot.IsFreed)
            {
                _bots.RemoveAt(i--);
                _roster.Remove(brain.Bot);
                continue;
            }
            brain.Network = Network;
            brain.GameType = GameType;     // propagate a mid-match gametype reference to objective roles
            brain.Think(brain.Bot, dt);
        }
    }

    /// <summary>
    /// Built-in fallback spawn when no host <see cref="SpawnBot"/> is provided: construct a bot
    /// <see cref="Player"/> with a configured name (the <c>bot_prefix</c>/<c>bot_suffix</c> cvars), give it a
    /// negative entity index (the engine table can't mint a Player subclass), then place + load it out via the
    /// shared spawn lifecycle. For a production server prefer wiring <see cref="SpawnBot"/> to
    /// <c>ClientManager.ClientConnect(isBot:true)</c> so the bot also gets a score row + team assignment +
    /// the sim client list; this fallback covers the headless-sim/test path where those aren't needed.
    /// </summary>
    private Player DefaultSpawn(string botName)
    {
        // QC bot_setnameandstuff: decorate the name with the configured prefix/suffix.
        string prefix = Cvars.String("bot_prefix");
        string suffix = Cvars.String("bot_suffix");
        string fullName = $"{prefix}{(string.IsNullOrEmpty(prefix) ? "" : " ")}{botName}{suffix}";

        var p = new Player { NetName = fullName, IsBot = true };
        p.Index = -1000 - _bots.Count;
        p.IsFreed = false;
        p.Flags = EntFlags.Client;

        // place + load out via the shared spawn lifecycle (away from any roster players).
        // [R0c] targetCheck:true — Base runs Spawn_FilterOutBadSpots(..., targetcheck=true) for EVERYONE
        // (spawnpoints.qc:419); the emergency re-filter re-admits the spots if a broken map rejects them all. Inert
        // on stock DM (every spot active/untargeted); on Assault/Onslaught/Race it stops bots spawning on
        // inactive/destroyed-objective spots and gives Race the previous-checkpoint prio.
        var sp = SpawnSystem.SelectSpawnPoint(p, _roster.FindAll(q => !q.IsDead), targetCheck: true);
        if (sp is not null)
            SpawnSystem.PutPlayerInServer(p, sp.Value);
        else
        {
            // no spawnpoints: drop at origin with a default hull so the bot is at least valid
            p.Mins = SpawnSystem.PlayerMins;
            p.Maxs = SpawnSystem.PlayerMaxs;
            p.Origin = Vector3.Zero;
            p.Health = 100f;
            p.MaxHealth = 100f;
            p.DeadState = DeadFlag.No;
            p.MoveType = MoveType.Walk;
            p.Solid = Solid.SlideBox;
            foreach (string w in SpawnSystem.DefaultLoadout)
                p.OwnedWeapons.Add(w);
        }
        return p;
    }
}
