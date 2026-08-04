// Port of server/bot/default/bot.qc bot_serverframe (:689-833) + bot_fixcount (:623-687)
// + bot_spawn (:45-60) + the name/model slice of bot_setnameandstuff (:163-349)
// + the live per-tick input seam of ecs/systems/sv_physics.qc sys_phys_ai (:41-46).
using System.Globalization;
using System.Numerics;
using VortexArena.Common.Diagnostics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Physics;

namespace VortexArena.Server.Bot;

/// <summary>
/// The live bot population manager — the C# port of QC <c>bot_serverframe</c>: it keeps the roster converged
/// on the configured fill (<c>bot_number</c> / <c>minplayers</c> / <c>bot_vs_human</c>, one add per frame,
/// remove-newest above target), loads the map's waypoint graph once when the first bot appears, rotates the
/// strategy token (exactly one bot per frame may run goal rating), resyncs the live <c>skill</c> cvar onto
/// every brain, auto-readies bots during warmup, and — the input seam — hands GameWorld each bot's per-tick
/// <see cref="MovementInput"/> from its <see cref="BotBrain.ThinkProduce"/>, cached per sim tick so the
/// movement step and the weapon driver consume the SAME command (QC sys_phys_ai runs bot_think inside the
/// client physics step; both readers see the one usercmd).
///
/// Owned by <see cref="GameWorld"/> (created at Boot). Bot client lifecycle still flows through
/// <see cref="ClientManager"/> — its <c>OnBotConnected</c> hook routes EVERY bot connect (fixcount fill,
/// console bot_add, a host's direct ClientConnect) into <see cref="RegisterBot"/> so no bot is brainless.
/// </summary>
public sealed class BotPopulation
{
    private readonly GameWorld _world;

    /// <summary>Engine <c>maxclients</c> (the spawnclient capacity cap in bot_fixcount). Host-set; default 16.</summary>
    public int MaxClients = 16;

    /// <summary>The shared map waypoint graph (QC g_waypoints), loaded once on the first frame with bots.</summary>
    public WaypointNetwork? Network { get; private set; }

    /// <summary>
    /// The graph, loading it now if the lazy path has not fired yet.
    ///
    /// The waypoint editor needs this because the lazy load is gated on BOTS BEING PRESENT — an editing session
    /// runs with none, so <see cref="Network"/> would stay null forever and the tool would report an empty map
    /// on a level with a perfectly good graph. Marks the load done so the bot path does not redo it and discard
    /// the mapper's edits.
    /// </summary>
    public WaypointNetwork? EnsureWaypointNetwork()
    {
        if (_waypointsLoaded)
            return Network;

        _waypointsLoaded = true;
        Network = _world.LoadWaypointNetwork();
        foreach (BotBrain b in _brains)
            b.Network = Network;
        _itemPrewarmCursor = 0;
        _itemPrewarmDone = Network is null;
        return Network;
    }

    /// <summary>Fired when a bot leaves (fixcount removal, console remove, intermission teardown) so the net
    /// host can clean its per-player maps (ServerNet.ForgetPlayer). Fired from the disconnect chain — every
    /// removal path funnels through it.</summary>
    public event Action<Player>? BotRemoved;

    // brains in connect order (QC bot_list; the order drives strategy-token rotation + remove-newest).
    private readonly List<BotBrain> _brains = new();
    private readonly Dictionary<Player, BotBrain> _byPlayer = new();

    /// <summary>QC <c>currentbots</c>; -1 is the "recount next frame" sentinel armed while time &lt; 2.5.</summary>
    private int _currentBots;

    private float _nextThink;            // QC botframe_nextthink (fixcount backoff on spawn failure)
    private bool _waypointsLoaded;       // QC botframe_spawnedwaypoints (load-once latch)
    private int _itemPrewarmCursor;      // (perf) staggered item→waypoint cache prewarm — see block (g2)
    private bool _itemPrewarmDone = true;
    private float _lastSkillCvar = float.NaN; // QC the `skill` global resync (bot.qc:725-736)
    private int _tokenIndex;             // QC bot_strategytoken (index into _brains)
    private bool _tokenTaken = true;     // QC bot_strategytoken_taken (true → rotate next frame)

    /// <summary>QC <c>bot_waypoint_queue_owner</c>: the one bot currently running an unstuck reachability scan.</summary>
    private BotBrain? _unstuckQueueOwner;
    private int _seedCounter = 1;
    private float _autoskillNextThink;   // QC autoskill_nextthink (5 s autoskill recheck clock)
    private float _nextDangerTime;        // QC botframe_nextdangertime (danger-detection recompute clock)
    // QC the per-client totalfrags_lastcheck baseline: frags-since-last-autoskill is ScoreFrags - this.
    private readonly Dictionary<Player, int> _fragsLastCheck = new();

    // per-tick input cache (the both-readers-one-command seam; mirrors ServerNet's TickInputTime cache).
    // TickInput implements IMovementInput by delegating to its struct field so InputFor can return the SAME
    // long-lived object every call — returning the `MovementInput` struct as the interface would BOX it per
    // read (movement step + weapon driver × every bot × every tick). ZeroInput is boxed once for the same reason.
    private sealed class TickInput : IMovementInput
    {
        public float Time = -1f;
        public MovementInput Input;

        public Vector3 ViewAngles => Input.ViewAngles;
        public Vector3 MoveValues => Input.MoveValues;
        public float FrameTime => Input.FrameTime;
        public bool ButtonJump => Input.ButtonJump;
        public bool ButtonCrouch => Input.ButtonCrouch;
        public bool ButtonUse => Input.ButtonUse;
        public bool ButtonAttack1 => Input.ButtonAttack1;
        public bool ButtonAttack2 => Input.ButtonAttack2;
        public bool ButtonJetpack => Input.ButtonJetpack;
        public bool Typing => Input.Typing;
        public int Impulse => Input.Impulse;
        public bool Predicted => Input.Predicted;
    }
    private readonly Dictionary<Player, TickInput> _tickInputs = new();
    // Boxed ONCE into the interface (no per-call boxing on the return) and RE-boxed only when sys_ticrate
    // changes the live tick length - one allocation per rate change. Single-writer: the server tick thread.
    private static IMovementInput ZeroInput = new MovementInput { FrameTime = Engine.Simulation.SimulationLoop.TicRate };

    /// <summary>The live brains, in connect order (QC bot_list). Exposed for tests/diagnostics.</summary>
    public IReadOnlyList<BotBrain> Brains => _brains;

    /// <summary>The brain driving <paramref name="p"/>, or null if it isn't a managed bot.</summary>
    public BotBrain? BrainOf(Player p) => _byPlayer.TryGetValue(p, out BotBrain? b) ? b : null;

    public BotPopulation(GameWorld world)
    {
        _world = world;
    }

    // =============================================================================================
    // the per-frame loop (QC bot_serverframe)
    // =============================================================================================

    /// <summary>
    /// QC <c>bot_serverframe()</c> — called once per tick from GameWorld.OnStartFrame, at main.qc:372's
    /// position (after the warmup check, before the SV_StartFrame hooks).
    /// </summary>
    public void ServerFrame()
    {
        float time = _world.Time;

        // Variance program 2026-07-11: re-arm the per-tick STRATEGY tracewalk budget (see BotTracewalk).
        // One tick's worth of budgeted walks (seed search + goal rating + straight-shot checks, across ALL
        // bots) may spend at most this many traces; the overflow reports unreachable and the pass retries on
        // its normal interval. This is the hard bound that turns the bot-tick tail (Release p99 6.2ms /
        // max 17.1ms, all strategy walks) into a flat O(budget) cost.
        BotTracewalk.ResetTickBudget();

        // (a) intermission (bot.qc:691-702): after the match ends bots STAY unless every human left — then all
        // bots are dropped (so an abandoned server empties out).
        if (_world.Intermission.Running && _currentBots > 0)
        {
            if (_world.Clients.HumanCount == 0)
            {
                for (int i = _brains.Count - 1; i >= 0; i--)
                    _world.Clients.ClientDisconnect(_brains[i].Bot); // disconnect chain drops the brain + event
                _currentBots = 0;
            }
            return;
        }

        // (b) game stopped → no population churn (bot.qc:704).
        if (_world.GameStopped)
            return;

        // (c) early-map sentinel (bot.qc:712-723): don't add/remove until time 2.5, then recount what's there
        // (bots may persist across a map change on a long-lived host).
        if (time < 2.5f)
        {
            _currentBots = -1;
            return;
        }
        if (_currentBots == -1)
        {
            _currentBots = _world.Clients.BotCount;
        }

        // (d) live `skill` cvar resync (bot.qc:725-736): a console `skill N` retunes every bot. (QC also
        // re-costs waypoint links across the bunnyhop threshold; the port bakes the shared graph's costs once
        // at LoadWaypointNetwork using the server skill — see WaypointNetwork.ForMap's skill seed — rather than
        // re-costing live on a mid-match skill change.) Per-bot BotSkill is the authoritative knob; the cvar change writes
        // it for all bots, and the per-frame brain sync below picks up any direct BotSkill write too.
        float skillCvar = Cvars.Skill;
        if (skillCvar != _lastSkillCvar)
        {
            bool first = float.IsNaN(_lastSkillCvar);
            _lastSkillCvar = skillCvar;
            if (!first)
                foreach (BotBrain b in _brains)
                    b.Bot.BotSkill = skillCvar;
        }
        foreach (BotBrain b in _brains)
        {
            b.Skill = b.Bot.BotSkill;
            b.Nav.Skill = b.Bot.BotSkill;
            b.Nav.MoveSkill = b.Bot.BotMoveSkill;   // QC bot_moveskill term in the bunnyhop gate (midair zeroes it)
        }

        // (e) autoskill (bot.qc:741-747): every 5 s, if skill_auto is set, retune the `skill` cvar toward the
        // best human's recent frag rate (the cvar resync above then writes it onto every bot next frame).
        if (time > _autoskillNextThink)
        {
            float a = Cvars.Float("skill_auto");
            if (a != 0f)
                Autoskill(a);
            _autoskillNextThink = time + 5f;
        }

        // (f) population fill/trim (bot.qc:749-753): one add per frame; a failed spawn backs off 10s.
        if (time > _nextThink)
        {
            using (Prof.Sample("bots.fixcount"))
                if (!FixCount(time))
                    _nextThink = time + 10f;
        }

        // (g) waypoint load-once (bot.qc:761-784): the first frame with bots present loads the map graph
        // (.waypoints + .cache + .hardwired via the world's ConfigReader, else entity auto-generation).
        if (!_waypointsLoaded && _currentBots > 0)
        {
            _waypointsLoaded = true;
            Network = _world.LoadWaypointNetwork();
            foreach (BotBrain b in _brains)
                b.Network = Network;
            _itemPrewarmCursor = 0;          // arm the staggered item→waypoint prewarm below
            _itemPrewarmDone = Network is null;
        }

        // (g2) (perf 2026-07-03) Staggered item→waypoint prewarm: the FIRST strategy pass per bot used to pay
        // the whole QC .nearestwaypoint cache fill in one tick (~every rateable item × a tracewalk-verified
        // Nearest each — the 80-115ms join-window CPU-LOGIC hitches on a debug build, stormkeep census). Bind a
        // few items per frame here instead — the SAME cache with the SAME position-derived values
        // (WaypointNetwork.NearestForGoal), just filled off the strategy hot path. Static items bind once per
        // match; the cursor walks the entity list once per graph load.
        if (Network is { } net && !_itemPrewarmDone)
        {
            using var _prewarmScope = Prof.Sample("bots.prewarm");
            var all = Common.Services.Api.Entities.All;
            if (all is null)
                _itemPrewarmDone = true;
            else
            {
                const int PerFrame = 3;   // ~1-3 tracewalks each — a sub-millisecond-scale slice per tick
                int bound = 0;
                while (_itemPrewarmCursor < all.Count && bound < PerFrame)
                {
                    Entity e = all[_itemPrewarmCursor++];
                    if (e is { IsFreed: false } && (e.Flags & EntFlags.Item) != 0)
                    {
                        net.NearestForGoal(e, e.Origin);
                        bound++;
                    }
                }
                if (_itemPrewarmCursor >= all.Count)
                    _itemPrewarmDone = true;
            }
        }

        // (h) strategy token rotation (bot.qc:786-813): when the token was consumed, pass it to the next bot
        // WITHOUT a current goal (skipping dead bots); if none qualifies, simply the next one.
        RotateStrategyToken();

        // (i) warmup auto-ready (QC bot_think:153-157): a spawned bot readies up once per warmup stage.
        if (_world.Warmup.WarmupStage)
        {
            foreach (BotBrain b in _brains)
                if (!b.AutoReadied && !b.Bot.IsObserver)
                {
                    b.AutoReadied = true;
                    _world.Warmup.ToggleReady(b.Bot);
                }
        }
        else
        {
            foreach (BotBrain b in _brains)
                b.AutoReadied = false; // re-arm for the next warmup (ready set is cleared on restart)
        }

        // (j) botframe_updatedangerousobjects (bot.qc:815-822 → navigation.qc:1874): every
        // bot_ai_dangerdetectioninterval (0.25 s) recompute each waypoint's static .dmg danger bias from the
        // g_bot_dodge hazard list (in-flight rockets, turret beams). FindPath folds Waypoint.Danger into the A*
        // cost so bots route AROUND hazards, complementing the per-frame havocbot_checkdanger brake in BotBrain.
        if (Network is not null && time >= _nextDangerTime)
        {
            using var _dangerScope = Prof.Sample("bots.danger");
            float interval = Cvars.FloatOr("bot_ai_dangerdetectioninterval", 0.25f);
            // QC: catch up the clock if we fell more than 1.5 intervals behind, then advance one interval.
            if (_nextDangerTime < time - interval * 1.5f)
                _nextDangerTime = time;
            _nextDangerTime += interval;

            var all = Common.Services.Api.Entities.All;
            if (all is not null)
                Network.UpdateDangerousObjects(all, (int)Cvars.FloatOr("bot_ai_dangerdetectionupdates", 64f));
        }
    }

    /// <summary>
    /// The per-tick bot input source (the sys_phys_ai seam): produce this tick's command from the bot's brain,
    /// cached on the sim clock so GameWorld's movement step and weapon driver — which both pull input — consume
    /// the SAME command (mirrors ServerNet's per-tick cache for humans). A bot without a brain (not yet
    /// registered) stands still.
    /// </summary>
    public IMovementInput InputFor(Player p, float dt)
    {
        if (!_byPlayer.TryGetValue(p, out BotBrain? brain))
        {
            if (ZeroInput.FrameTime != _world.Simulation.TickSeconds)  // follow the live tick (sys_ticrate)
                ZeroInput = new MovementInput { FrameTime = _world.Simulation.TickSeconds };
            return ZeroInput;
        }
        if (!_tickInputs.TryGetValue(p, out TickInput? cache))
            _tickInputs[p] = cache = new TickInput();
        float now = _world.Time;
        if (cache.Time != now)
        {
            cache.Time = now;
            cache.Input = brain.ThinkProduce(p, dt);
        }
        return cache; // the IMovementInput-implementing cache object itself — no per-read struct box
    }

    // =============================================================================================
    // fixcount (QC bot_fixcount) — the bot_number/minplayers/bot_vs_human convergence
    // =============================================================================================

    /// <summary>QC <c>bot_fixcount(false)</c>: converge the roster on the target (ONE add per frame, trims all
    /// excess). Returns false on a spawn failure (server full), arming the 10s backoff.</summary>
    private bool FixCount(float time)
    {
        int activeRealPlayers = 0, realPlayers = 0;
        // QC MUTATOR_HOOKFUNCTION(lms, Bot_FixCount, CBC_ORDER_EXCLUSIVE) (sv_lms.qc:693): LMS overrides the
        // active-real-player count so that eliminated (out-of-game) players no longer keep a bot slot filled —
        // only INGAME real clients count as activerealplayers. Mirror that by checking OutOfGame on LmsState.
        bool lmsActive = _world.GameType is VortexArena.Common.Gameplay.LastManStanding;
        VortexArena.Common.Gameplay.LastManStanding? lms =
            lmsActive ? (VortexArena.Common.Gameplay.LastManStanding)_world.GameType! : null;
        foreach (ClientManager.ClientInfo c in _world.Clients.Clients)
        {
            if (c.IsBot) continue;
            realPlayers++;
            if (!c.Player.IsObserver)
            {
                // QC IS_PLAYER (joined, not observing). For LMS, also require INGAME (not eliminated/out-of-game).
                // Use LivesOf (0 when not in _states or Lives==0); a 0-lives player is out-of-game in LMS.
                if (lms is null || lms.LivesOf(c.Player) > 0)
                    activeRealPlayers++;
            }
        }

        int target = TargetBotCount(
            botVsHuman: Cvars.Float("bot_vs_human"),
            availableTeams: _world.Teamplay.IsTeamGame ? _world.Teamplay.TeamCount : 0,
            activeRealPlayers: activeRealPlayers,
            realPlayers: realPlayers,
            teamplay: _world.Teamplay.IsTeamGame,
            minPlayers: Cvars.Float("minplayers"),
            minPlayersPerTeam: Cvars.Float("minplayers_per_team"),
            botNumber: Cvars.Float("bot_number"),
            // QC bot_fixcount calls GetPlayerLimit() (server/client.qc:2155), which short-circuits to 2 for
            // duel — so bot-fill respects the 1v1 cap, not the raw g_maxplayers.
            playerLimit: _world.GameType is Duel ? Duel.PlayerLimit : Cvars.Int("g_maxplayers"),
            maxClients: MaxClients,
            currentBots: _currentBots,
            time: time,
            botJoinEmpty: Cvars.Bool("bot_join_empty"));

        // only add ONE bot per frame to avoid utter chaos (QC bot.qc:667-681, multiple_per_frame=false).
        if (_currentBots < target)
        {
            if (SpawnBot(null, null) is null)
                return false;
        }
        while (_currentBots > target && target >= 0)
            RemoveNewest();
        return true;
    }

    /// <summary>
    /// The pure fixcount target math (QC bot_fixcount:623-664), exposed for tests: how many bots should be on
    /// the server right now. <paramref name="availableTeams"/> is QC AVAILABLE_TEAMS (0 outside teamplay);
    /// <paramref name="playerLimit"/> is GetPlayerLimit() (g_maxplayers; 0 = unlimited).
    /// </summary>
    public static int TargetBotCount(float botVsHuman, int availableTeams, int activeRealPlayers, int realPlayers,
        bool teamplay, float minPlayers, float minPlayersPerTeam, float botNumber, int playerLimit, int maxClients,
        int currentBots, float time, bool botJoinEmpty)
    {
        // bot_vs_human: an all-bot team sized as |ratio| × active humans (QC bot.qc:642-643).
        if (botVsHuman != 0f && availableTeams == 2)
            return System.Math.Min((int)MathF.Ceiling(MathF.Abs(botVsHuman) * activeRealPlayers),
                maxClients - realPlayers);

        // QC bot.qc:644-660: fill while humans are around (or bot_join_empty, or the 5s level-change grace
        // that keeps last map's bots from being torn down before the humans rejoin).
        if (realPlayers > 0 || botJoinEmpty || (currentBots > 0 && time < 5f))
        {
            int min = teamplay
                ? System.Math.Max(0, (int)MathF.Floor(minPlayersPerTeam) * availableTeams)
                : System.Math.Max(0, (int)MathF.Floor(minPlayers));
            int minBots = System.Math.Max(0, (int)MathF.Floor(botNumber));

            int bots = System.Math.Max(minBots, min - activeRealPlayers);
            if (playerLimit > 0)
                bots = System.Math.Min(bots, System.Math.Max(playerLimit - activeRealPlayers, 0));
            return System.Math.Min(bots, maxClients - realPlayers);
        }

        return 0; // no players → remove bots (QC bot.qc:661-664)
    }

    // =============================================================================================
    // add / remove (QC bot_spawn / bot_removenewest) + the console handlers
    // =============================================================================================

    /// <summary>
    /// Console <c>bot_add</c> (and the setbots top-up): spawn one bot immediately AND raise <c>bot_number</c>
    /// so the faithful fixcount doesn't tear it down at the next convergence (QC's plain bot_spawn adds would
    /// be culled at the recount — the port keeps the console verbs useful by moving the floor with them).
    /// </summary>
    public BotBrain? AddBot(string? name = null, float? skill = null)
    {
        BotBrain? brain = SpawnBot(name, skill);
        if (brain is not null)
            Cvars.Set("bot_number", MathF.Max(Cvars.Float("bot_number") + 1f, _currentBots));
        return brain;
    }

    /// <summary>Console <c>bot_remove</c>: drop the newest bot (optionally matching <paramref name="name"/>)
    /// and lower <c>bot_number</c> with it so fixcount doesn't immediately re-add one.</summary>
    public bool RemoveBot(string? name = null)
    {
        Player? bot = null;
        foreach (BotBrain b in _brains)
            if (string.IsNullOrWhiteSpace(name)
                || b.Bot.NetName.Contains(name!, StringComparison.OrdinalIgnoreCase))
                bot = b.Bot; // last match → newest
        if (bot is null)
            return false;
        Cvars.Set("bot_number", MathF.Max(0f, Cvars.Float("bot_number") - 1f));
        return _world.Clients.ClientDisconnect(bot); // the disconnect chain drops the brain + fires BotRemoved
    }

    /// <summary>QC <c>bot_spawn</c>: connect a bot client (name/model from bots.txt) and bump currentbots.
    /// The brain is created by the ClientManager OnBotConnected hook → <see cref="RegisterBot"/>.</summary>
    private BotBrain? SpawnBot(string? name, float? skill)
    {
        (string botName, string model, string skin, int forcedTeam, int color, BotSkills skills)
            = string.IsNullOrWhiteSpace(name)
                ? PickNameAndModel()
                : (name!, "", "", 0, -1, default(BotSkills));

        ClientManager.ClientInfo info = _world.Clients.ClientConnect(isBot: true, netName: botName);
        Player p = info.Player;
        p.BotSkill = skill ?? Cvars.Skill;                  // QC the global `skill` cvar seeds new bots
        // QC the bots.txt per-bot fire-skill modifiers (bot.qc:282/285): carried so the aim fire decision +
        // max-fire-deviation reflect each bot's aggression/aim personality (default 0 when no column present).
        // QC bot.qc:275-290 — the full READSKILL set, stamped onto the bot so aim, movement, weapon choice and
        // reaction rate all carry this bot's personality rather than one shared skill number.
        p.BotKeyboardSkill   = skills.Keyboard;
        p.BotMoveSkill       = skills.Move;
        p.BotDodgeSkill      = skills.Dodge;
        p.BotPingSkill       = skills.Ping;
        p.BotWeaponSkill     = skills.Weapon;
        p.BotAggresSkill     = skills.Aggres;
        p.BotRangePreference = skills.RangePreference;
        p.BotAimSkill        = skills.Aim;
        p.BotOffsetSkill     = skills.Offset;
        p.BotMouseSkill      = skills.Mouse;
        p.BotThinkSkill      = skills.Think;
        p.BotAiSkill         = skills.Ai;

        // QC bot_forced_team (bot.qc:255-262 / client.qc:592): a bots.txt-pinned team (argv5) overrides the
        // auto-balance that ClientConnect just ran — but only in teamplay and not under bot_vs_human/2-team.
        // Pin it via Teamplay.SetBotForcedTeam (so future autobalance/shuffle keeps honoring it) and re-run
        // AssignBestTeam, which already early-outs on a pinned bot; the initial call ran before the pin existed.
        if (forcedTeam != 0 && _world.Teamplay.IsTeamGame
            && !(Cvars.Bool("bot_vs_human") && _world.Teamplay.TeamCount == 2))
        {
            _world.Teamplay.SetBotForcedTeam(p, forcedTeam);
            _world.Teamplay.AssignBestTeam(p, _world.Clients.Players);
        }

        // QC bot_skin (bot.qc:343 this.playerskin): a bots.txt skin index overrides the default player skin.
        // Stored so it survives respawns (FixPlayermodel re-honors the client's playerskin) and applied now
        // since ClientConnect already auto-joined+spawned the bot.
        if (int.TryParse(skin, NumberStyles.Integer, CultureInfo.InvariantCulture, out int skinIdx) && skinIdx >= 0)
            _preferredSkin[p] = skinIdx;

        // QC bot_setnameandstuff:315-318: setcolor(this, 16*shirt+pants) then save bot_preferredcolors. In FFA
        // the chosen shirt/pants is the bot's actual color; in a team game setcolor's team change is undone and
        // the team color wins (bot.qc:316 + the team-join SetPlayerColors), so only apply the personal color in
        // FFA where it's visible. Store the preference so it survives respawns (re-stamped in OnBotSpawned).
        if (color >= 0)
        {
            _preferredColor[p] = color;
            if (!_world.Teamplay.IsTeamGame)
                _world.Teamplay.SetColor(p, color); // QC setcolor (FFA: team stays neutral, clientcolors = color)
        }

        if (!string.IsNullOrEmpty(model))
            _preferredModel[p] = model;
        if (!string.IsNullOrEmpty(model) || _preferredSkin.ContainsKey(p))
            ApplyPreferredModel(p);                          // the connect auto-Join already spawned it
        // the OnBotConnected hook registered the brain; sync its skill now that BotSkill is final.
        if (_byPlayer.TryGetValue(p, out BotBrain? brain))
        {
            brain.Skill = p.BotSkill;
            brain.Nav.Skill = p.BotSkill;
        }
        return brain;
    }

    /// <summary>QC <c>bot_removenewest</c> (bot.qc:486-539): drop the most recently added bot — in a team game,
    /// the newest bot on the LARGEST team (so trims also rebalance).</summary>
    private void RemoveNewest()
    {
        if (_brains.Count == 0)
        {
            _currentBots = _world.Clients.BotCount; // defensive resync (count says bots exist but no brains)
            return;
        }

        Player? victim = null;
        if (_world.Teamplay.IsTeamGame)
        {
            // largest team by live player count (QC TeamBalance largest-team pick).
            int bestTeam = 0, bestCount = -1;
            foreach (int team in Teams.Active(_world.Teamplay.TeamCount))
            {
                int n = _world.Teamplay.CountTeam(team, _world.Clients.Players);
                if (n > bestCount) { bestCount = n; bestTeam = team; }
            }
            for (int i = _brains.Count - 1; i >= 0; i--)
                if ((int)_brains[i].Bot.Team == bestTeam) { victim = _brains[i].Bot; break; }
        }
        victim ??= _brains[^1].Bot;
        _world.Clients.ClientDisconnect(victim);
    }

    // =============================================================================================
    // brain lifecycle (wired from ClientManager / GameWorld)
    // =============================================================================================

    /// <summary>
    /// ClientManager <c>OnBotConnected</c> sink (QC bot_clientconnect + havocbot_setupbot): create the brain
    /// for a newly connected bot — role from the gametype, the live roster as its player set, the shared map
    /// graph. Counts it into <c>currentbots</c> (unless the pre-2.5s sentinel is armed; the recount adopts it).
    /// </summary>
    public void RegisterBot(Player p)
    {
        if (_byPlayer.ContainsKey(p))
            return;
        var brain = new BotBrain(p, Network, p.BotSkill, seed: _seedCounter++)
        {
            Role = BotRoles.ChooseRole(_world.GameType?.NetName),
            PlayerProvider = () => _world.Clients.Players,
            GameTypeNetName = _world.GameType?.NetName,
            GameType = _world.GameType,
            StrategyTokenHeld = false,                  // the population hands the token around
            MovementHold = MovementHeld,
        };
        brain.OnStrategyTokenUsed = () => _tokenTaken = true;
        // QC bot_waypoint_queue_owner (navigation.qc:1925-1932): one server-wide unstuck scan at a time, so a
        // room full of wedged bots costs one reachability sweep rather than N.
        brain.TryOwnUnstuckQueueHook = b =>
        {
            if (_unstuckQueueOwner is null || ReferenceEquals(_unstuckQueueOwner, b) || _unstuckQueueOwner.Bot.IsFreed)
            {
                _unstuckQueueOwner = b;
                return true;
            }
            return false;
        };
        brain.ReleaseUnstuckQueueHook = b =>
        {
            if (ReferenceEquals(_unstuckQueueOwner, b))
                _unstuckQueueOwner = null;
        };
        _brains.Add(brain);
        _byPlayer[p] = brain;
        if (_currentBots >= 0)
            _currentBots++;
    }

    /// <summary>GameWorld's disconnect hook (every bot removal path): drop the brain + caches, fire
    /// <see cref="BotRemoved"/> so the net host forgets the player.</summary>
    public void OnBotDisconnected(Player p)
    {
        if (_byPlayer.Remove(p, out BotBrain? brain))
        {
            int idx = _brains.IndexOf(brain);
            if (idx >= 0)
            {
                _brains.RemoveAt(idx);
                if (_tokenIndex > idx || _tokenIndex >= _brains.Count)
                    _tokenIndex = System.Math.Max(0, _tokenIndex - 1);
            }
            if (_currentBots > 0)
                _currentBots--;
        }
        _tickInputs.Remove(p);
        _preferredModel.Remove(p);
        _preferredSkin.Remove(p);
        _preferredColor.Remove(p);
        _fragsLastCheck.Remove(p);
        BotRemoved?.Invoke(p);
    }

    /// <summary>GameWorld's spawn hook for bots: re-apply the bots.txt model after every (re)spawn —
    /// PutPlayerInServer stamps sv_defaultplayermodel each time (QC FixPlayermodel honors the client's
    /// playermodel cvar, which for a QC bot carries the bots.txt pick).</summary>
    public void OnBotSpawned(Player p) => ApplyPreferredModel(p);

    /// <summary>QC bot_think's movement holds: the pre-match countdown + the campaign "wait for the player".</summary>
    private bool MovementHeld()
        => _world.Time < _world.GameStartTime && !_world.Warmup.WarmupStage
           || (Cvars.Bool("g_campaign") && !_world.Campaign.BotsMayStart);

    private readonly Dictionary<Player, string> _preferredModel = new();
    // QC .playerskin (bot.qc:343): the bots.txt skin index for a bot, re-applied after each spawn (which resets
    // p.Skin to sv_defaultplayerskin), matching QC FixPlayermodel honoring the client's playerskin.
    private readonly Dictionary<Player, int> _preferredSkin = new();
    // QC .bot_preferredcolors (bot.qc:318): the bots.txt-derived packed shirt/pants color, re-applied (FFA only)
    // after each spawn — mirrors QC re-stamping setcolor(e, e.bot_preferredcolors) for bots (bot.qc:418).
    private readonly Dictionary<Player, int> _preferredColor = new();

    private void ApplyPreferredModel(Player p)
    {
        if (_preferredModel.TryGetValue(p, out string? model) && !string.IsNullOrEmpty(model))
        {
            if (Common.Services.Api.Services is not null)
                Common.Services.Api.Entities.SetModel(p, model);
            else
                p.Model = model;
        }
        if (_preferredSkin.TryGetValue(p, out int skin))
            p.Skin = skin; // QC this.playerskin → the player's rendered skin index
        // QC bot.qc:418 re-stamps setcolor(e, e.bot_preferredcolors) for bots; in FFA the personal shirt/pants
        // is the bot's color (a team game forces the team color, so skip there — the team join owns ClientColors).
        if (_preferredColor.TryGetValue(p, out int color) && !_world.Teamplay.IsTeamGame)
            _world.Teamplay.SetColor(p, color);
    }

    // =============================================================================================
    // strategy token (QC bot.qc:786-813)
    // =============================================================================================

    private void RotateStrategyToken()
    {
        if (_brains.Count == 0)
            return;
        if (_tokenTaken)
        {
            _tokenTaken = false;
            // QC bot_relinkplayerlist (bot.qc:437) builds bot_list EXCLUDING IS_OBSERVER || bot_ispaused, and the
            // token (bot.qc:786-813) only ever walks bot_list — so an observer can never hold the token. Mirror
            // that: the candidate set is non-observer bots only. Pass 1 picks the first non-observer WITHOUT a
            // goal (skipping dead, per the QC break condition); pass 2 ("simply the next one") falls to the next
            // non-observer. If every bot is an observer (bot_list == NULL), the token can't rotate — leave it put.
            int n = _brains.Count;
            int next = -1;
            for (int step = 0; step < n; step++)
            {
                int idx = (_tokenIndex + 1 + step) % n;
                BotBrain b = _brains[idx];
                if (b.Bot.IsObserver) continue;               // not in bot_list
                // QC bot.qc:800 skips a FROZEN bot when handing out the token — it runs no role, so giving it
                // the token wastes the hold and starves the live bots' re-rating cadence.
                if (BotBrain.IsFrozen(b.Bot)) continue;
                if (next < 0) next = idx;                      // pass 2 fallback: first non-observer
                if (!b.Bot.IsDead && !b.Nav.HasGoal)          // pass 1: first goal-less (skip dead)
                {
                    next = idx;
                    break;
                }
            }
            if (next >= 0)
                _tokenIndex = next;
        }
        for (int i = 0; i < _brains.Count; i++)
            _brains[i].StrategyTokenHeld = i == _tokenIndex;
    }

    // =============================================================================================
    // autoskill (QC autoskill, bot.qc:569-613)
    // =============================================================================================

    /// <summary>
    /// QC <c>autoskill(factor)</c>: compare the best human's and best bot's frags gained since the last check;
    /// if bots lag by ≥2 (scaled by <paramref name="factor"/>) bump <c>skill</c> up (cap 17), if they lead by
    /// ≥2 bump it down (floor 0). When no human or no bot scored this window, only the counters are reset; in
    /// the "nothing to do" middle band the counters are KEPT so they keep accumulating (QC the early return).
    /// The <c>skill</c> cvar write is picked up by the per-frame resync above (branch d).
    /// </summary>
    private void Autoskill(float factor)
    {
        int bestBot = -1, bestPlayer = -1;
        foreach (ClientManager.ClientInfo c in _world.Clients.Clients)
        {
            Player p = c.Player;
            if (p.IsObserver) continue;                       // QC IS_PLAYER
            int gained = p.ScoreFrags - (_fragsLastCheck.TryGetValue(p, out int last) ? last : 0);
            if (c.IsBot)
                bestBot = System.Math.Max(bestBot, gained);
            else
                bestPlayer = System.Math.Max(bestPlayer, gained);
        }

        if (bestBot < 0 || bestPlayer < 0)
        {
            // no human or no bot scoring this window: do nothing, but fall through to reset the counters.
        }
        else if (bestBot <= bestPlayer * factor - 2f)
        {
            float skill = Cvars.Skill;
            if (skill < 17f)
                Cvars.Set("skill", skill + 1f);
        }
        else if (bestBot >= bestPlayer * factor + 2f)
        {
            float skill = Cvars.Skill;
            if (skill > 0f)
                Cvars.Set("skill", skill - 1f);
        }
        else
        {
            return; // "not doing anything" middle band: keep the counters, wait for them to accumulate.
        }

        // reset the per-client baselines (QC FOREACH_CLIENT it.totalfrags_lastcheck = it.totalfrags).
        foreach (ClientManager.ClientInfo c in _world.Clients.Clients)
            if (!c.Player.IsObserver)
                _fragsLastCheck[c.Player] = c.Player.ScoreFrags;
    }

    // =============================================================================================
    // bots.txt (QC bot_setnameandstuff — the name/model slice)
    // =============================================================================================

    // QC bots.txt rows: name (argv0), model (argv1), skin (argv2), shirt (argv3), pants (argv4), forced-team
    // (argv5, 1..4; 0 = none), then the 12 skill-modifier columns (argv6..17). The port carries the columns it
    // can apply faithfully: shirt/pants → ClientColors (16*shirt+pants), and the two fire-relevant skill
    // modifiers bot_aggresskill (argv11) + bot_aimskill (argv13) → the aim fire decision. The remaining 10 skill
    // columns stay folded into the single BotSkill knob (the documented skill.modifiers intended divergence).
    /// <summary>
    /// The 12 READSKILL per-bot modifier columns (QC bot.qc:264-290). Each is
    /// <c>stof(argv(prio)) * w</c> when the cell is filled, else a random draw
    /// <c>(2*random()-1) * r * w</c> — so a BLANK cell is not always 0: the four columns with r &gt; 0
    /// (keyboard, offset, mouse, think) get genuine per-bot variance from an empty cell, which is where much
    /// of a Xonotic roster's personality comes from. Suppressed in campaign mode, like QC.
    /// </summary>
    private readonly record struct BotSkills(
        float Keyboard, float Move, float Dodge, float Ping, float Weapon, float Aggres,
        float RangePreference, float Aim, float Offset, float Mouse, float Think, float Ai);

    private readonly record struct BotRow(
        string Name, string Model, string Skin, int ForcedTeam,
        int Color, BotSkills Skills);
    private List<BotRow>? _botRows; // parsed once per world

    /// <summary>
    /// Pick a name + model from the bot config file (QC bot_setnameandstuff over bot_config_file=bots.txt):
    /// tab-separated rows (name, model, skin, shirt, pants, team, 12 skill modifiers); rows whose name is not
    /// in use are preferred (QC the prio+1 weighting); the name is wrapped in bot_prefix/bot_suffix and
    /// de-duplicated with a "(N)" suffix; the model becomes models/player/&lt;model&gt;.iqm. Falls back to a
    /// small built-in name table when the file isn't mounted. The skin/shirt/pants columns and the 12 skill
    /// modifiers are unported (single-knob skill; see residuals).
    /// </summary>
    private (string name, string model, string skin, int forcedTeam, int color, BotSkills skills) PickNameAndModel()
    {
        _botRows ??= ParseBotFile();

        // names already in use (QC the cleanname conflict scan).
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Player q in _world.Clients.Players)
            used.Add(q.NetName);

        string prefix = Cvars.String("bot_prefix");
        string suffix = Cvars.String("bot_suffix");

        // prefer rows whose decorated name is unused (QC prio weighting), random among the preferred set.
        var candidates = new List<BotRow>();
        foreach (BotRow row in _botRows)
            if (!used.Contains(prefix + row.Name + suffix))
                candidates.Add(row);
        if (candidates.Count == 0)
            candidates = _botRows;

        string baseName = "Bot", model = "", skin = "";
        int forcedTeam = 0, color = -1;
        BotSkills skills = default;
        if (candidates.Count > 0)
        {
            BotRow row = candidates[_nameRng.Next(candidates.Count)];
            baseName = row.Name;
            model = row.Model;
            skin = row.Skin;
            forcedTeam = row.ForcedTeam;
            color = row.Color;
            skills = row.Skills;
        }

        string name = prefix + baseName + suffix;
        // duplicates get "(N)" (QC appends a counter when the name is taken).
        if (used.Contains(name))
        {
            int i = 2;
            while (used.Contains($"{name}({i})")) i++;
            name = $"{name}({i})";
        }

        // QC bot_setnameandstuff:249-253: a blank/negative shirt or pants cell randomizes that nibble to 0..14.
        // The whole-row color is packed 16*shirt+pants; if the row had no color cell at all (color < 0), draw
        // both nibbles now so every bot still gets a faithful random colormap (matches QC's per-cell default).
        if (color < 0)
            color = _nameRng.Next(15) * 16 + _nameRng.Next(15);

        string modelPath = string.IsNullOrEmpty(model) ? "" : $"models/player/{model}.iqm"; // QC appends .iqm
        return (name, modelPath, skin, forcedTeam, color, skills);
    }

    private readonly Random _nameRng = new(12345);

    /// <summary>
    /// The distinct, non-empty player-model paths the active bot config (bots.txt) can assign — the roster's
    /// candidate set. The client precaches exactly these under the loading screen (NetGame, perf §9.4) so the
    /// bots' models are texture-cache-hot when they first render, instead of decoding+uploading their IQM
    /// textures one-per-frame during the opening seconds of play (the match-start hitch storm). Parsed via the
    /// same <see cref="ParseBotFile"/> cache + <c>models/player/&lt;model&gt;.iqm</c> transform as
    /// <see cref="PickNameAndModel"/>, so the set is exactly what the picks can draw from.
    /// </summary>
    public IReadOnlyList<string> CandidateModelPaths()
    {
        _botRows ??= ParseBotFile();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var models = new List<string>();
        foreach (BotRow row in _botRows)
        {
            if (string.IsNullOrEmpty(row.Model)) continue;
            string path = $"models/player/{row.Model}.iqm";   // QC appends .iqm (matches PickNameAndModel)
            if (seen.Add(path)) models.Add(path);
        }
        return models;
    }

    /// <summary>Parse bot_config_file (bots.txt) rows via the world's ConfigReader; comments (// #) skipped.</summary>
    private List<BotRow> ParseBotFile()
    {
        var rows = new List<BotRow>();
        string file = Cvars.String("bot_config_file");
        if (string.IsNullOrEmpty(file)) file = "bots.txt";
        string? text = _world.ConfigReader?.Invoke(file);
        if (text is not null)
        {
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)
                    || line.StartsWith("#", StringComparison.Ordinal))
                    continue;
                string[] f = line.Split('\t');
                if (f.Length == 0 || string.IsNullOrWhiteSpace(f[0]))
                    continue;
                // argv(2) = skin index (QC bot_skin = argv(2), default "0"); a blank cell keeps the default.
                string skin = f.Length > 2 ? f[2].Trim() : "";
                // argv(5) = forced-team index (QC bot_forced_team = stof(argv(5))); kept only if 1..4.
                int forcedTeam = 0;
                if (f.Length > 5 && int.TryParse(f[5].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int t)
                    && t >= 1 && t <= 4)
                    forcedTeam = t;
                // argv(3)/argv(4) = shirt/pants color nibbles (QC bot_setnameandstuff:249-253). A blank or
                // negative cell → -1 here so PickNameAndModel randomizes that nibble (QC floor(random()*15)).
                // The packed color is 16*shirt + pants (QC setcolor). Color < 0 = "no cell" (randomize whole row).
                int shirt = ParseColorNibble(f, 3);
                int pants = ParseColorNibble(f, 4);
                int color = (shirt < 0 && pants < 0) ? -1 : System.Math.Max(0, shirt) * 16 + System.Math.Max(0, pants);
                // The 12 READSKILL columns start at argv(6) (QC prio = 6). Only the two fire-relevant modifiers
                // are carried: bot_aggresskill = stof(argv(11)) * 1 (prio 11), bot_aimskill = stof(argv(13)) * 2
                // (prio 13). Blank cells → 0 (the stock READSKILL reward factor for both is 0).
                // QC READSKILL table, prio starting at 6 (bot.qc:275-290): (weight, randomReward).
                var skills = new BotSkills(
                    Keyboard:        ReadSkillColumn(f, 6, 0.5f, 0.5f),
                    Move:            ReadSkillColumn(f, 7, 2f, 0f),
                    Dodge:           ReadSkillColumn(f, 8, 2f, 0f),
                    Ping:            ReadSkillColumn(f, 9, 0.5f, 0f),
                    Weapon:          ReadSkillColumn(f, 10, 2f, 0f),
                    Aggres:          ReadSkillColumn(f, 11, 1f, 0f),
                    RangePreference: ReadSkillColumn(f, 12, 1f, 0f),
                    Aim:             ReadSkillColumn(f, 13, 2f, 0f),
                    Offset:          ReadSkillColumn(f, 14, 2f, 0.5f),
                    Mouse:           ReadSkillColumn(f, 15, 1f, 0.5f),
                    Think:           ReadSkillColumn(f, 16, 1f, 0.5f),
                    Ai:              ReadSkillColumn(f, 17, 2f, 0f));
                rows.Add(new BotRow(f[0].Trim(), f.Length > 1 ? f[1].Trim() : "", skin, forcedTeam,
                    color, skills));
            }
        }
        if (rows.Count == 0)
        {
            // no bots.txt mounted: the old built-in table (kept so a bare test floor still names its bots).
            foreach (string n in new[] { "Hellfire", "Toxic", "Scorcher", "Discbot", "Nexus", "Eureka", "Sensible", "Mystery" })
                rows.Add(new BotRow(n, "", "", 0, -1, default));
        }
        return rows;
    }

    /// <summary>QC bot_setnameandstuff:249-253 shirt/pants cell: a present, non-negative color nibble, else -1
    /// (the caller randomizes a -1 nibble to 0..14, matching QC floor(random()*15)).</summary>
    private static int ParseColorNibble(string[] f, int idx)
    {
        if (idx >= f.Length) return -1;
        string s = f[idx].Trim();
        if (s.Length == 0) return -1;
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= 0 ? v : -1;
    }

    /// <summary>QC READSKILL(f, w, r) (bot.qc:266-272) for a present cell: <c>stof(argv(prio)) * w</c>. A blank
    /// cell returns 0 (the carried columns both have stock reward factor r = 0, so absent → 0; the random
    /// per-bot variance of the r&gt;0 columns is the documented single-knob skill.modifiers divergence).</summary>
    private static float ReadSkillColumn(string[] f, int idx, float weight, float randomReward)
    {
        string s = idx < f.Length ? f[idx].Trim() : "";
        if (s.Length > 0
            && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            return v * weight;

        // QC's else-branch: `(!autocvar_g_campaign) * (2 * random() - 1) * r * w`. A blank cell is NOT
        // automatically zero — for the four columns with r > 0 it draws genuine per-bot variance, which is
        // most of where a roster's personality comes from. Campaign play suppresses it so its opponents are
        // reproducible.
        if (randomReward <= 0f || Cvars.Bool("g_campaign")) return 0f;
        return (2f * (float)_skillRng.NextDouble() - 1f) * randomReward * weight;
    }

    /// <summary>RNG for the READSKILL random-variance columns (QC <c>random()</c> during bot_setnameandstuff).</summary>
    private static readonly System.Random _skillRng = new();

#if VA_BOTPLAYER
    // =============================================================================================
    //  BOT-PLAYER HARNESS (compile-gated — see Directory.Build.props / VaBotPlayer)
    //
    //  A brain bound to the LOCAL HUMAN player so an unattended run drives the real player pipeline.
    //  The player is NOT a bot: IsBot stays false, so GameWorld keeps sourcing its command from the net
    //  InputProvider — the brain's output travels the genuine client path (sample -> predict -> encode ->
    //  ENet -> server authority -> snapshot -> reconcile) instead of shortcutting into the sim. That is
    //  the whole point: spectating a bot exercises none of that.
    //
    //  The brain is kept OUT of _brains/_byPlayer so it cannot perturb population accounting, fixcount,
    //  the strategy-token rotation, or bot_vs_human counting. It holds the strategy token permanently
    //  instead (it is one extra actor in a test build; starving it of the token would leave it unable to
    //  pick goals, which defeats the harness).
    //
    //  THREADING: ThinkBotPlayer runs on the SIM thread (GameWorld start-frame, per tick) because the
    //  brain reads world state, waypoints and traces. The client reads the result from the RENDER thread,
    //  so the command is published as an immutable box swapped atomically — no lock, no torn command.
    // =============================================================================================

    /// <summary>One frame of synthesised player input, already normalised to the ±1 wish-move an
    /// <c>InputCommand</c> carries (the brain emits direction × <see cref="BotNavigation.MaxSpeed"/>).</summary>
    public readonly record struct BotPlayerCommand(
        Vector3 ViewAngles, float Forward, float Side, float Up,
        bool Jump, bool Crouch, bool Attack1, bool Attack2, bool Hook, bool Jetpack);

    private sealed class CommandBox { public BotPlayerCommand Cmd; }

    private BotBrain? _botPlayerBrain;
    private Player? _botPlayer;
    private volatile CommandBox _botPlayerCmd = new();

    /// <summary>True once a human player is under brain control.</summary>
    public bool BotPlayerAttached => _botPlayerBrain is not null;

    /// <summary>The latest synthesised command. Safe to read from any thread (immutable box).</summary>
    public BotPlayerCommand BotPlayerCommandLatest => _botPlayerCmd.Cmd;

    /// <summary>Bind a brain to <paramref name="p"/> (a REAL client). Idempotent per player.</summary>
    public void AttachBotPlayer(Player p, float skill = 5f)
    {
        if (_botPlayerBrain is not null && ReferenceEquals(_botPlayer, p))
            return;
        _botPlayer = p;

        // FORCED RESPAWN. A human slot normally lies dead until someone presses fire — with nobody at the
        // keyboard the harness would die once and spend the rest of the run as a corpse, quietly measuring
        // nothing. g_forced_respawn auto-respawns at g_respawn_delay_max instead, so an unattended session
        // keeps playing. Set here rather than left to the caller so the harness cannot be run without it.
        Cvars.Set("g_forced_respawn", "1");
        VortexArena.Common.Diagnostics.Log.Info(
            $"[bot-player] g_forced_respawn 1 (max wait {Cvars.FloatOr("g_respawn_delay_max", 5f):0}s) — "
            + "the harness respawns itself.");

        _botPlayerBrain = new BotBrain(p, Network, skill, seed: 9999)
        {
            Role = BotRoles.ChooseRole(_world.GameType?.NetName),
            PlayerProvider = () => _world.Clients.Players,
            GameTypeNetName = _world.GameType?.NetName,
            GameType = _world.GameType,
            StrategyTokenHeld = true,   // never rotated into the bot token ring — see the note above
            MovementHold = MovementHeld,
        };
    }

    /// <summary>Drop the brain (player left / map change).</summary>
    public void DetachBotPlayer()
    {
        _botPlayerBrain = null;
        _botPlayer = null;
        _botPlayerCmd = new CommandBox();
    }

    /// <summary>SIM THREAD, once per tick: think the bound brain and publish its command for the client.</summary>
    public void ThinkBotPlayer(float dt)
    {
        BotBrain? brain = _botPlayerBrain;
        Player? p = _botPlayer;
        if (brain is null || p is null || p.IsFreed)
            return;
        // A dead/observing player has nothing to drive; publish neutral so nothing is held down through
        // the respawn (a latched +attack would auto-fire the instant the player respawns). The alive↔dead
        // edges are counted so the heartbeat can PROVE the forced-respawn cycle is turning over — a harness
        // that dies once and stays down looks healthy in every other metric while measuring nothing.
        bool dead = p.IsObserver || p.Health <= 0f;
        if (dead != _botPlayerWasDead)
        {
            _botPlayerWasDead = dead;
            if (dead) _botPlayerDeaths++;
            else if (_botPlayerDeaths > 0) _botPlayerRespawns++;
        }
        if (dead)
        {
            _botPlayerCmd = new CommandBox();
            _botPlayerOriginValid = false;   // respawn teleports; don't count the corpse→spawn jump as travel
            return;
        }

        // The waypoint graph loads lazily on the first frame with bots; adopt it once it exists.
        if (brain.Network is null && Network is not null)
            brain.Network = Network;

        MovementInput mi = brain.ThinkProduce(p, dt);

        // The brain emits wish-move as direction × Nav.MaxSpeed; InputCommand carries ±1 (the client and
        // server both re-scale it via WishMoveScaling), so normalise against the magnitude it emitted at.
        float spd = brain.Nav.MaxSpeed > 0f ? brain.Nav.MaxSpeed : 320f;
        static float Unit(float v, float s) => System.Math.Clamp(v / s, -1f, 1f);

        _botPlayerCmd = new CommandBox
        {
            Cmd = new BotPlayerCommand(
                mi.ViewAngles,
                Unit(mi.MoveValues.X, spd), Unit(mi.MoveValues.Y, spd), Unit(mi.MoveValues.Z, spd),
                mi.ButtonJump, mi.ButtonCrouch, mi.ButtonAttack1, mi.ButtonAttack2,
                mi.ButtonHook, mi.ButtonJetpack),
        };

        // Heartbeat. An unattended run is worthless if the "player" is standing still against a wall, and
        // "the brain attached" does not prove it is playing — so report what it actually did. Distance is
        // integrated from the AUTHORITATIVE origin, i.e. it only moves if the synthesised input really made
        // the round trip through predict → encode → net → server physics.
        if (_botPlayerOriginValid)
            _botPlayerDist += (p.Origin - _botPlayerLastOrigin).Length();
        _botPlayerLastOrigin = p.Origin;
        _botPlayerOriginValid = true;
        if (mi.ButtonAttack1 || mi.ButtonAttack2) _botPlayerShots++;
        float now2 = _world.Time;
        if (now2 - _botPlayerLastReport >= 5f)
        {
            _botPlayerLastReport = now2;
            VortexArena.Common.Diagnostics.Log.Info(
                $"[bot-player] t={now2:0}s travelled={_botPlayerDist:0}qu speed={p.Velocity.Length():0}qu/s "
                + $"firing-ticks={_botPlayerShots} health={p.Health:0} frags={p.Frags} "
                + $"deaths={_botPlayerDeaths} respawns={_botPlayerRespawns} "
                + $"goal={(brain.Nav.HasGoal ? "yes" : "no")} enemy={(brain.Enemy is not null ? "yes" : "no")}");
        }
    }

    private Vector3 _botPlayerLastOrigin;
    private bool _botPlayerOriginValid;
    // Starts true: the player is typically attached pre-spawn (observer), and that initial not-yet-alive
    // state must not count as a death — the counters exist to prove the respawn cycle turns over.
    private bool _botPlayerWasDead = true;
    private int _botPlayerDeaths;
    private int _botPlayerRespawns;
    private float _botPlayerDist;
    private int _botPlayerShots;
    private float _botPlayerLastReport;
#endif
}
