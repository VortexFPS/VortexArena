using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using GS = XonoticGodot.Common.Gameplay.Scoring.GameScores; // T7: alias the static score table for the per-mode ScoreRules

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Assault gametype — port of <c>CLASS(Assault, Gametype)</c>
/// (common/gametypes/gametype/assault/assault.qh + sv_assault.qc).
///
/// An asymmetric attack/defend mode. One team attacks a chain of objectives leading to a final power core,
/// the other defends. The attackers win the round by destroying the final objective (QC
/// <c>target_assault_roundend</c> fires <c>winning=1</c>); if the time limit elapses first, the defenders win
/// (QC <c>WinningCondition_Assault</c>: default to the defending team, overridden only when the round-end was
/// triggered). Assault is played as two rounds with the roles swapped — the team that destroyed the core
/// faster wins overall (QC <c>assault_new_round</c> sets round two's timelimit to round one's destruction
/// time).
///
/// Faithfully ported (the attack/defend win rule + objective chain):
///  - which team attacks vs defends (<see cref="AttackerTeam"/>, QC assault_attacker_team);
///  - the objective chain entities + target graph (<see cref="AddObjective"/>/<see cref="AddDecreaser"/>/
///    <see cref="AddDestructible"/>/<see cref="AddRoundEnd"/>, QC target_objective / target_objective_decrease
///    / func_assault_destructible / target_assault_roundend) where shooting the active destructible runs its
///    decreaser to whittle the objective's health, and destroying an objective activates the next
///    (<see cref="DamageDestructible"/> → <see cref="DecreaseObjective"/>, QC assault_objective_decrease_use);
///  - objective destruction → attackers win the round (<see cref="DestroyFinalObjective"/>, QC roundend.winning),
///    ending the match immediately in campaign (QC autocvar_g_campaign branch) or broadcasting CENTER_ASSAULT_-
///    OBJ_DESTROYED when a second round is to follow;
///  - the per-spawn role centerprint (<see cref="OnPlayerSpawn"/>, QC MUTATOR_HOOKFUNCTION(as, PlayerSpawn)):
///    "You are attacking!"/"You are defending!" keyed on the live attacker team;
///  - the defender timelimit win (<see cref="DriveFrame"/>/<see cref="TimeLimitReached"/>, QC
///    WinningCondition_Assault default-to-defender branch); the host calls <see cref="DriveFrame"/> per CheckRules
///    frame and wires <see cref="CanRoundStart"/>/<see cref="CanRoundEnd"/> into its round handler;
///  - the two-round role swap (<see cref="StartSecondRound"/>) keyed on the attackers' destruction time.
///
/// Deferred (NOTE — cross-boundary, needs host/CSQC wiring outside this file): turret team-swap (QC
/// assault_roundstart_use / TurretSpawn hook), the engine round-restart machinery (ReadyRestart_force + the 5s
/// AS_ROUND_DELAY freeze + map reset), the objective/wall models + waypoint sprites/health bars (CSQC), the
/// func_assault_wall toggle + destructible heal, the objective .message broadcast, the warmup-incompatible
/// ReadLevelCvars override, and the score networking/HUD.
/// </summary>
[GameType]
public sealed class Assault : GameType
{
    /// <summary>QC ASSAULT_VALUE_INACTIVE (sv_assault.qh): the health sentinel for an objective that isn't yet
    /// active. Base value is 1000 — aligned here (was 1e6) so a map that authors an objective health between 1000
    /// and 1e6 gates identically to Base.</summary>
    public const float ObjectiveInactive = 1000f;

    /// <summary>QC <c>AS_ROUND_DELAY</c> (sv_assault.qc:12): the inter-round freeze, in seconds. After the
    /// attackers destroy the core in round 1, the game is frozen for 5s (the <c>as_round</c> entity's nextthink)
    /// before round 2 begins with the roles swapped.</summary>
    public const float RoundDelay = 5f;

    /// <summary>
    /// Round/role state for a two-round Assault match (QC assault_attacker_team + the round counter and the
    /// attackers' best destruction time used to set round two's clock).
    /// </summary>
    public sealed class AssaultState
    {
        /// <summary>The team currently attacking (QC assault_attacker_team), defaults to <see cref="Teams.Red"/>.</summary>
        public int AttackerTeam = Teams.Red;

        /// <summary>Which round this is: 0 = first, 1 = second (roles swapped).</summary>
        public int Round;

        /// <summary>
        /// Seconds the first-round attackers took to destroy the core (QC time − game_starttime), or 0 if they
        /// never did. Round two's time limit is set from this so the second attackers must beat it.
        /// </summary>
        public float FirstRoundDestroyTime;
    }

    /// <summary>The single shared round/role state.</summary>
    public AssaultState State { get; } = new();

    private HookHandler<DeathEvent>? _deathHandler;

    // QC MUTATOR_HOOKFUNCTION(as, PlayerSpawn): on spawn, centerprint the player's role (attacking/defending).
    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _spawnHandler;

    // QC MUTATOR_HOOKFUNCTION(as, PlayHitsound): return true for func_assault_destructible victims (force hitsound).
    private HookHandler<MutatorHooks.PlayHitsoundArgs>? _playHitsoundHandler;

    // QC MUTATOR_HOOKFUNCTION(as, OnEntityPreSpawn): delete generic team spawns (info_player_team1..4) — Assault
    // uses its own attacker/defender spawn points.
    private HookHandler<MutatorHooks.OnEntityPreSpawnArgs>? _onEntityPreSpawnHandler;

    // QC MUTATOR_HOOKFUNCTION(as, VehicleInit): push a fresh vehicle's first think out by 0.5s (does not abort init).
    private HookHandler<MutatorHooks.VehicleInitArgs>? _vehicleInitHandler;

    /// <summary>Optional sink for the host/controller to react to a kill.</summary>
    public IMatchEvents? Events;

    /// <summary>QC checkrules end-of-round latch: true once the round (or the whole match) has been decided.</summary>
    public bool MatchEnded { get; private set; }

    /// <summary>The winning team color code once decided, or 0 if none yet.</summary>
    public int WinningTeam { get; private set; }

    /// <summary>
    /// QC <c>bprint(...)</c> chat-log sink (the global server console/chat broadcast). Assigned by the host
    /// (<c>GameWorld.Activate</c> → <c>Commands.ChatBroadcast</c>); null in a headless POJO. Used for the two
    /// Assault round-flow chat lines ("&lt;attacker&gt; destroyed the objective in &lt;time&gt;" on a round-1 core
    /// destruction, and "Starting second round..." when round 2 begins) — QC's <c>bprint</c> calls in
    /// <c>WinningCondition_Assault</c> / <c>assault_new_round</c>.
    /// </summary>
    public System.Action<string>? BPrint;

    /// <summary>
    /// QC <c>as_round</c> entity: while the attackers have won round 1 (non-campaign) and the 5s
    /// <see cref="RoundDelay"/> freeze is counting down, this is true and the match is frozen
    /// (<c>game_stopped = true</c>). <see cref="DriveFrame"/> fires <see cref="StartSecondRound"/> once it elapses.
    /// </summary>
    public bool SecondRoundPending { get; private set; }

    /// <summary>QC <c>as_round.nextthink</c>: the match-clock time the round-2 swap (<c>as_round_think</c>) fires.</summary>
    private float _secondRoundDueAt;

    /// <summary>
    /// QC <c>assault_new_round</c> → <c>ReadyRestart_force(true)</c> (the host-only slice): the engine round
    /// restart — reset every player + map object to its spawn state and re-stamp <c>game_starttime</c>. The pure
    /// objective/role swap is done by <see cref="StartSecondRound"/>; this callback (assigned by the host) performs
    /// the full map reset that this cross-boundary file can't do alone. No-op in a headless/POJO test.
    /// </summary>
    public System.Action? OnSecondRoundRestart;

    public Assault()
    {
        NetName = "as";
        DisplayName = "Assault";
        TeamGame = true;
    }

    // ============================================================================================
    //  Objective-chain ENTITIES (QC target_objective / _decrease / func_assault_destructible / roundend)
    // ============================================================================================

    /// <summary>An objective in the attack chain (QC target_objective: .health, .target = next objective).</summary>
    public sealed class Objective
    {
        public string Name = "";          // QC .targetname
        public string Target = "";        // QC .target — fired (SUB_UseTargets) when destroyed (activates next)
        public float Health = ObjectiveInactive; // QC RES_HEALTH (ASSAULT_VALUE_INACTIVE until activated)
        public bool Active => Health < ObjectiveInactive && Health > 0f;
        public bool Destroyed => Health <= 0f && Health > -ObjectiveInactive; // QC sets -1 when destroyed
    }

    /// <summary>A damage applicator (QC target_objective_decrease: links a destructible to its objective, .dmg).</summary>
    public sealed class Decreaser
    {
        public string Name = "";          // QC .targetname — the name a func_assault_destructible's .target fires
        public string Target = "";        // QC .target — the objective targetname it decreases (.enemy)
        public Objective? ObjectiveRef;   // resolved objective (QC this.enemy)
        public float Dmg = 100f;          // QC .dmg — health removed per activation
        public bool Spent;                // QC: a decreaser fires once per round (assault_sprite consumed)
    }

    /// <summary>A destructible wall the attackers shoot (QC func_assault_destructible: .health, triggers a decreaser).</summary>
    public sealed class Destructible
    {
        public string Target = "";        // QC .target — the decreaser targetname it triggers when destroyed
        public Decreaser? DecreaserRef;   // resolved decreaser
        public float Health = 100f;       // QC RES_HEALTH
        public float MaxHealth = 100f;
        public bool Active;               // QC: only the chain's current destructible is shootable
        public bool Destroyed => Health <= 0f;
        /// <summary>The world edict the BSP lump spawned for this wall (QC the func_assault_destructible entity), or
        /// null in a pure-logic/headless test. The live damage path (when wired) maps a damaged edict back to its
        /// Destructible via <see cref="DestructibleFor"/> and drives <see cref="DamageDestructible"/>.</summary>
        public Framework.Entity? WorldEntity;

        /// <summary>QC <c>func_assault_destructible.sprite</c>: the live objective waypoint sprite spawned when this
        /// destructible's objective is activated (WP_AssaultDestroy with a health bar), updated on each decrease and
        /// disowned when destroyed. Null while inactive / in a headless POJO with no waypoint registry.</summary>
        public Waypoints.WaypointSprite? Sprite;
    }

    /// <summary>
    /// A cosmetic/collision wall keyed to an objective (QC func_assault_wall + assault_wall_think): it is SOLID_BSP
    /// and visible while its objective is alive, and hides (model="" + SOLID_NOT) once the objective is destroyed
    /// (RES_HEALTH &lt; 0) — typically opening a path the attackers earn by destroying that objective.
    /// </summary>
    public sealed class Wall
    {
        public string Target = "";        // QC .target — the objective targetname this wall watches (.enemy)
        public Objective? ObjectiveRef;   // resolved objective (QC this.enemy)
        public Framework.Entity? WorldEntity; // the func_assault_wall edict whose solid/model is toggled
        public string Model = "";         // QC .mdl — the model restored when the wall is shown again
        public bool Hidden;               // last applied state (so we only re-stamp on a transition)
    }

    /// <summary>The objectives, decreasers, and destructibles on the map, keyed by name where needed.</summary>
    public readonly List<Objective> Objectives = new();
    public readonly List<Decreaser> Decreasers = new();
    public readonly List<Destructible> Destructibles = new();
    public readonly List<Wall> Walls = new();

    // Deferred spawn staging (QC INITPRIO_FINDTARGET): the BSP entity lump spawns objectives, decreasers and
    // destructibles in ARBITRARY order, so a decreaser/destructible may be spawned before the objective/decreaser
    // it links to (its .target names them). QC defers the .target→.enemy resolution to assault_setenemytoobjective
    // run at INITPRIO_FINDTARGET (after the whole lump). We mirror that: the map spawnfunc Stage*s the raw fields,
    // then ResolveObjectiveGraph() builds the real Decreaser/Destructible objects with their links resolved once
    // every objective/decreaser name is known. (The deterministic tests can still use the direct
    // AddObjective/AddDecreaser/AddDestructible API where the spawn order is controlled.)
    private readonly List<(string name, string target, float dmg)> _pendingDecreasers = new();
    private readonly List<(string decreaserName, float health, Framework.Entity? world)> _pendingDestructibles = new();
    private readonly List<(string objectiveName, string model, Framework.Entity? world)> _pendingWalls = new();

    /// <summary>QC target_assault_roundend present (the final objective's target). True once a round-end exists.</summary>
    public bool HasRoundEnd { get; private set; }

    public override void OnInit()
    {
        // QC: the objective chain is built from the map's target_objective / target_objective_decrease /
        // func_assault_destructible / target_assault_roundend entities (see AddObjective etc).
        Objectives.Clear();
        Decreasers.Clear();
        Destructibles.Clear();
        Walls.Clear();
        _pendingDecreasers.Clear();
        _pendingDestructibles.Clear();
        _pendingWalls.Clear();
        HasRoundEnd = false;
    }

    /// <summary>QC spawnfunc target_objective: add an objective (initially inactive).</summary>
    public Objective AddObjective(string name, string target = "")
    {
        var o = new Objective { Name = name, Target = target };
        Objectives.Add(o);
        return o;
    }

    /// <summary>QC spawnfunc target_objective_decrease: add a decreaser linked to objective <paramref name="objectiveName"/>.</summary>
    public Decreaser AddDecreaser(string objectiveName, float dmg = 100f, string name = "")
    {
        var d = new Decreaser { Name = name, Target = objectiveName, Dmg = dmg };
        d.ObjectiveRef = Objectives.Find(o => o.Name == objectiveName); // QC assault_setenemytoobjective
        Decreasers.Add(d);
        return d;
    }

    /// <summary>QC spawnfunc func_assault_destructible: add a shootable wall that triggers <paramref name="decreaser"/>.</summary>
    public Destructible AddDestructible(Decreaser decreaser, float health = 100f, bool active = false)
    {
        var w = new Destructible { DecreaserRef = decreaser, Health = health, MaxHealth = health, Active = active };
        Destructibles.Add(w);
        return w;
    }

    /// <summary>QC spawnfunc target_assault_roundend: marks the chain's terminal target.</summary>
    public void AddRoundEnd() => HasRoundEnd = true;

    // ============================================================================================
    //  Deferred objective-graph spawn (QC INITPRIO_FINDTARGET) — used by the BSP-lump spawnfuncs
    // ============================================================================================

    /// <summary>
    /// QC spawnfunc target_objective_decrease (the deferred half): stage a decreaser by its own
    /// <paramref name="name"/> (.targetname — what a func_assault_destructible's .target fires) and the
    /// <paramref name="objectiveName"/> it decreases (.target → .enemy). The real <see cref="Decreaser"/> is built
    /// in <see cref="ResolveObjectiveGraph"/> after the whole lump has spawned (arbitrary order). QC defaults the
    /// damage to 101 (<c>if(!this.dmg) this.dmg = 101;</c>), so pass <paramref name="dmg"/> 0 to take that default.
    /// </summary>
    public void StageDecreaser(string name, string objectiveName, float dmg)
        => _pendingDecreasers.Add((name, objectiveName, dmg > 0f ? dmg : 101f));

    /// <summary>
    /// QC spawnfunc func_assault_destructible (the deferred half): stage a destructible wall whose .target names
    /// the decreaser (<paramref name="decreaserName"/>) it triggers on destruction, with <paramref name="health"/>
    /// from the breakable setup (default 100), and an optional <paramref name="worldEntity"/> (the func_assault_-
    /// destructible edict the lump spawned — the bot role finds it by classname, and the live damage bridge maps it
    /// back to this Destructible). Linked to its decreaser in <see cref="ResolveObjectiveGraph"/>.
    /// </summary>
    public void StageDestructible(string decreaserName, float health, Framework.Entity? worldEntity = null)
        => _pendingDestructibles.Add((decreaserName, health > 0f ? health : 100f, worldEntity));

    /// <summary>
    /// QC spawnfunc func_assault_wall (the deferred half): stage a cosmetic/collision wall whose .target
    /// (<paramref name="objectiveName"/>) names the objective it watches (QC assault_setenemytoobjective sets
    /// <c>this.enemy</c>). The wall stays SOLID_BSP + visible while that objective lives and hides once it is
    /// destroyed (assault_wall_think). <paramref name="model"/> is QC <c>this.mdl</c> (restored when shown again),
    /// <paramref name="worldEntity"/> the func_assault_wall edict whose solid/model is toggled by
    /// <see cref="DriveWalls"/>. Linked to its objective in <see cref="ResolveObjectiveGraph"/>.
    /// </summary>
    public void StageWall(string objectiveName, string model, Framework.Entity? worldEntity = null)
        => _pendingWalls.Add((objectiveName, model, worldEntity));

    /// <summary>
    /// Map a damaged <c>func_assault_destructible</c> world edict back to its <see cref="Destructible"/> (QC the
    /// edict IS the destructible; the port keeps a POJO chain alongside the world entity). The live damage hook
    /// (a func_breakable event_damage / death bridge) calls this then <see cref="DamageDestructible"/>. Null if the
    /// edict isn't a tracked destructible.
    /// </summary>
    public Destructible? DestructibleFor(Framework.Entity worldEntity)
        => Destructibles.Find(w => ReferenceEquals(w.WorldEntity, worldEntity));

    /// <summary>
    /// QC INITPRIO_FINDTARGET pass (assault_setenemytoobjective + the destructible→decreaser SUB_UseTargets link):
    /// resolve every staged decreaser to its objective by name, and every staged destructible to its decreaser by
    /// name, now that the whole BSP entity lump has spawned. Idempotent (clears the staging once resolved); a
    /// staged entity whose target can't be found is dropped (QC objerror "fix the map" — we skip rather than fault).
    /// Call once after <c>SpawnMapEntities</c> (the host's post-spawn hook).
    /// </summary>
    public void ResolveObjectiveGraph()
    {
        // 1) decreasers: .target → the objective edict (QC assault_setenemytoobjective).
        foreach (var (name, target, dmg) in _pendingDecreasers)
            AddDecreaser(target, dmg, name);
        _pendingDecreasers.Clear();

        // 2) destructibles: .target → the decreaser by its targetname (QC the func_breakable SUB_UseTargets chain).
        foreach (var (decreaserName, health, world) in _pendingDestructibles)
        {
            Decreaser? dec = Decreasers.Find(d => d.Name == decreaserName);
            if (dec is not null)
            {
                Destructible w = AddDestructible(dec, health);
                w.Target = decreaserName;  // keep QC .target for inspection/round reset
                w.WorldEntity = world;     // the func_assault_destructible edict (bot role + live damage bridge)
            }
            // else: dangling .target (bad map) — QC objerrors; we skip so the rest of the chain still works.
        }
        _pendingDestructibles.Clear();

        // 3) walls: .target → the objective by its targetname (QC assault_setenemytoobjective on func_assault_wall).
        foreach (var (objectiveName, model, world) in _pendingWalls)
        {
            Objective? obj = Objectives.Find(o => o.Name == objectiveName);
            if (obj is not null)
                Walls.Add(new Wall { Target = objectiveName, ObjectiveRef = obj, WorldEntity = world, Model = model });
            // else: dangling .target (bad map) — QC objerrors; we skip so the rest of the chain still works.
        }
        _pendingWalls.Clear();

        // QC target_assault_roundstart (assault_roundstart_use_this, INITPRIO_FINDTARGET): once the graph is built,
        // arm the chain so the attackers have a live first objective to assault at round start.
        ResetObjectives();
        DriveWalls(); // QC assault_wall_think first tick (nextthink = time): stamp each wall's initial solid/model.
    }

    /// <summary>
    /// QC assault_wall_think (sv_assault.qc:178, nextthink = time + 0.2): for each func_assault_wall, show it
    /// (SOLID_BSP + its model) while its objective is alive (RES_HEALTH &gt;= 0) and hide it (model="" + SOLID_NOT)
    /// once the objective is destroyed (RES_HEALTH &lt; 0). Driven from the per-frame Assault arm; the world edict
    /// is only re-stamped on a state transition. The objective's destroyed health is QC -1 (RES_HEALTH &lt; 0),
    /// which the port models as <see cref="Objective.Health"/> &lt;= 0.
    /// </summary>
    public void DriveWalls()
    {
        // QC assault_wall_think runs on a 0.2s nextthink; the port re-evaluates every call (DriveFrame drives it each
        // frame). The state-transition guard below makes re-evaluation idempotent, so per-frame eval is functionally
        // equivalent to the 0.2s cadence (only the visible↔solid toggle matters, and it flips at most once per change).
        for (int i = 0; i < Walls.Count; i++)
        {
            Wall w = Walls[i];
            if (w.ObjectiveRef is null || w.WorldEntity is not { } edict || edict.IsFreed)
                continue;
            bool hide = w.ObjectiveRef.Health < 0f; // QC GetResource(this.enemy, RES_HEALTH) < 0
            if (hide == w.Hidden)
                continue; // no transition — leave the edict as-is (QC re-stamps every think; we only on change)
            w.Hidden = hide;
            if (hide)
            {
                edict.Model = "";                  // QC this.model = "";
                edict.Solid = Framework.Solid.Not; // QC this.solid = SOLID_NOT;
            }
            else
            {
                edict.Model = w.Model;             // QC this.model = this.mdl;
                edict.Solid = Framework.Solid.Bsp; // QC this.solid = SOLID_BSP;
                if (Api.Services is not null && !string.IsNullOrEmpty(w.Model))
                    Api.Entities.SetModel(edict, w.Model); // re-resolve the brush bounds so traces clip it again
            }
        }
    }

    /// <summary>
    /// QC assault_objective_use: activate an objective (set its health to <paramref name="health"/>, default
    /// 100) so its decreasers can whittle it. Called when the previous objective is destroyed (SUB_UseTargets).
    /// </summary>
    public void ActivateObjective(Objective o, float health = 100f)
    {
        o.Health = health;
        // QC target_objective_decrease_activate: re-arm the decreasers + destructibles aimed at this objective.
        foreach (var d in Decreasers)
            if (ReferenceEquals(d.ObjectiveRef, o))
            {
                d.Spent = false;
                foreach (var w in Destructibles)
                    if (ReferenceEquals(w.DecreaserRef, d))
                    {
                        w.Active = true;
                        SpawnObjectiveSprite(w); // QC: the objective marker the attackers see + shoot toward
                    }
            }
    }

    /// <summary>QC RADARICON_OBJECTIVE (every non-NONE radar icon is 1; the color distinguishes them).</summary>
    private const int RadarIconObjective = 1;

    /// <summary>
    /// QC <c>target_objective_decrease_activate</c> (sv_assault.qc:106, the WaypointSprite_SpawnFixed slice): spawn
    /// the active objective's waypoint sprite at the destructible's center, owned by the wall edict (so it tracks
    /// the wall), team = the live attacker team with SPRITERULE_TEAMPLAY, RADARICON_OBJECTIVE. A func_assault_-
    /// destructible shows WP_AssaultDestroy (attacker view) with a max-health/health bar (QC UpdateMaxHealth /
    /// UpdateHealth). Replaces any prior sprite on this wall. No-op in a headless POJO (no waypoint registry / no
    /// world edict).
    /// </summary>
    private void SpawnObjectiveSprite(Destructible w)
    {
        if (Api.Services is null || w.WorldEntity is not { } edict || edict.IsFreed)
            return;
        // QC: WaypointSprite_Disown any previous marker before re-spawning (re-activation across rounds).
        if (w.Sprite is not null)
        {
            Waypoints.WaypointSprites.Kill(w.Sprite);
            w.Sprite = null;
        }
        // QC sv_assault.qc:122-125: WaypointSprite_UpdateRule(spr, assault_attacker_team, SPRITERULE_TEAMPLAY)
        // then WaypointSprite_UpdateSprites(spr, WP_AssaultDefend, WP_AssaultDestroy, WP_AssaultDestroy). With team =
        // the ATTACKER team, the three-image rule means: the attacker's OWN team (model2) sees "Destroy", the ENEMY
        // (defender, model1) sees "Defend", and SPECTATORS (model3) see "Destroy". The destroy sprite carries the
        // health bar (QC `it.sprite`). The port resolves the per-viewer image in SendWaypoints via SpriteFor.
        Waypoints.WaypointSprite spr = Waypoints.WaypointSprites.SpawnFixed(
            "AssaultDefend", edict.Origin, State.AttackerTeam,
            WaypointObjectiveColor, RadarIconObjective, Waypoints.SpriteRule.Teamplay);
        Waypoints.WaypointSprites.UpdateSprites(spr, "AssaultDefend", "AssaultDestroy", "AssaultDestroy");
        spr.Owner = edict; // QC the sprite is spawned at 0.5*(absmin+absmax) and follows the wall edict
        // QC sv_assault.qc:121: spr.waypointsprite_visible_for_player = assault_decreaser_sprite_visible — the
        // marker is hidden once its linked objective is no longer active (RES_HEALTH >= ASSAULT_VALUE_INACTIVE, i.e.
        // not-yet-activated; a destroyed objective drops to -1 and the sprite is Disown'd in DamageDestructible).
        // The net layer (ServerNet.WaypointVisible) consults VisibleForPlayer first, so this gates every viewer.
        Objective? obj = w.DecreaserRef?.ObjectiveRef;
        spr.VisibleForPlayer = _ => obj is not null && obj.Health < ObjectiveInactive;
        Waypoints.WaypointSprites.UpdateMaxHealth(spr, w.MaxHealth);
        Waypoints.WaypointSprites.UpdateHealth(spr, w.MaxHealth > 0f ? w.Health / w.MaxHealth : -1f);
        w.Sprite = spr;
    }

    /// <summary>QC WP_Assault* color (orange — the Assault objective tint, all.inc + the port WaypointRegistry).</summary>
    private static readonly Vector3 WaypointObjectiveColor = new(1f, 0.5f, 0f);

    /// <summary>
    /// QC func_assault_destructible event_damage: an attacker damages the active destructible wall. When the
    /// wall's health reaches 0 it triggers its decreaser, whittling the linked objective (<see cref="DecreaseObjective"/>).
    /// Only the attacking team may damage it. Returns true if the wall was destroyed this call.
    /// </summary>
    public bool DamageDestructible(Destructible wall, int byTeam, float amount)
        => DamageDestructible(wall, byTeam, amount, null);

    /// <summary>
    /// QC func_assault_destructible event_damage with the attacker (overload of
    /// <see cref="DamageDestructible(Destructible,int,float)"/>): when the wall is destroyed the linked objective
    /// is whittled and <paramref name="actor"/> is credited the SCORE / ASSAULT_OBJECTIVES the chain awards
    /// (via <see cref="DecreaseObjective(Decreaser,int,Player?)"/>).
    /// </summary>
    public bool DamageDestructible(Destructible wall, int byTeam, float amount, Player? actor)
    {
        if (MatchEnded || !wall.Active || wall.Destroyed || amount <= 0f)
            return false;
        if (byTeam != State.AttackerTeam)
            return false; // QC: wrong team can't decrease objectives

        wall.Health -= amount;
        if (wall.Health > 0f)
        {
            // QC func_breakable damage → the func_assault_destructible's sprite health bar tracks the wall health
            // (WaypointSprite_UpdateHealth on every hit). Normalized 0..1 for the port's pre-normalized bar.
            if (wall.Sprite is not null && wall.MaxHealth > 0f)
                Waypoints.WaypointSprites.UpdateHealth(wall.Sprite, wall.Health / wall.MaxHealth);
            return false;
        }

        wall.Health = 0f;
        wall.Active = false;
        // QC assault_objective_decrease_use: WaypointSprite_Disown(trigger.assault_sprite, deadlifetime) once the
        // wall is destroyed (the marker fades, then the next objective's marker takes over).
        DisownSprite(wall);
        if (wall.DecreaserRef is { } dec)
            DecreaseObjective(dec, byTeam, actor);
        return true;
    }

    /// <summary>QC <c>WaypointSprite_Disown(..., waypointsprite_deadlifetime)</c>: fade out this wall's objective
    /// sprite over sv_waypointsprite_deadlifetime (default 1s), then drop it.</summary>
    private void DisownSprite(Destructible w)
    {
        if (w.Sprite is null)
            return;
        float dead = Api.Services is not null ? Api.Cvars.GetFloat("sv_waypointsprite_deadlifetime") : 1f;
        Waypoints.WaypointSprites.Disown(w.Sprite, dead > 0f ? dead : 1f);
        w.Sprite = null;
    }

    /// <summary>
    /// QC <c>destructible_heal</c> (sv_assault.qc:332, installed as the func_assault_destructible's
    /// <c>event_heal</c>): a friendly heal source (Arc heal-beam, heal nade, mage/bumblebee healgun) tops a
    /// partially-shot wall back up to its max health. Faithful to QC:
    /// <c>true_limit = (limit != RES_LIMIT_NONE) ? limit : targ.max_health;</c> — bail if the wall is already
    /// destroyed (hlth &lt;= 0) or already full (hlth &gt;= true_limit), else add <paramref name="amount"/>
    /// clamped to <c>true_limit</c>. Returns true if any health was added (so the caller can update the world
    /// edict + waypoint sprite, like QC's <c>WaypointSprite_UpdateHealth</c> + <c>func_breakable_colormod</c>).
    /// A wall can only be healed while it is still standing (QC's <c>hlth &lt;= 0</c> guard) — a fully destroyed
    /// wall that already fired its decreaser stays down for the round.
    /// </summary>
    public bool HealDestructible(Destructible wall, float amount, float limit)
    {
        if (MatchEnded || wall is null || amount <= 0f)
            return false;
        float trueLimit = limit != Resources.LimitNone ? limit : wall.MaxHealth;
        float hlth = wall.Health;
        if (hlth <= 0f || hlth >= trueLimit) // QC: already destroyed or already full
            return false;
        wall.Health = System.Math.Min(hlth + amount, trueLimit); // QC GiveResourceWithLimit
        // QC destructible_heal: if(targ.sprite) WaypointSprite_UpdateHealth(targ.sprite, ...).
        if (wall.Sprite is not null && wall.MaxHealth > 0f)
            Waypoints.WaypointSprites.UpdateHealth(wall.Sprite, wall.Health / wall.MaxHealth);
        return true;
    }

    /// <summary>
    /// QC assault_objective_decrease_use: a decreaser fires (once) and removes its <see cref="Decreaser.Dmg"/>
    /// from its objective's health. When that drops the objective to/below 0, the objective is destroyed —
    /// activate the next objective in the chain (its <see cref="Objective.Target"/>), or, if the chain's
    /// terminal target is the round-end, the attackers win the round (<see cref="DestroyFinalObjective"/>).
    /// </summary>
    public void DecreaseObjective(Decreaser dec, int byTeam) => DecreaseObjective(dec, byTeam, null);

    /// <summary>
    /// QC assault_objective_decrease_use with the attacker (overload of
    /// <see cref="DecreaseObjective(Decreaser,int)"/>): credits <paramref name="actor"/> the QC scoreboard awards —
    /// SCORE += the health removed (GameRules_scoring_add_team(actor, SCORE, dmg/hlth)) and, when the objective is
    /// destroyed, ASSAULT_OBJECTIVES +1 (GameRules_scoring_add_team(actor, ASSAULT_OBJECTIVES, 1)).
    /// </summary>
    public void DecreaseObjective(Decreaser dec, int byTeam, Player? actor)
    {
        if (MatchEnded || dec.Spent || byTeam != State.AttackerTeam)
            return;
        Objective? o = dec.ObjectiveRef;
        if (o is null || o.Health >= ObjectiveInactive)
            return; // not active

        dec.Spent = true; // QC: a decreaser can't activate again this round
        float removed = System.Math.Min(o.Health, dec.Dmg); // the health this decreaser actually strips
        o.Health = DamageObjective(o.Health, dec.Dmg);

        if (actor is not null)
            actor.ScoreFrags += (int)removed; // QC GameRules_scoring_add_team(actor, SCORE, dmg|hlth)

        if (o.Health > 0f && o.Health < ObjectiveInactive)
            return; // objective damaged but still standing

        // QC: the objective is destroyed → the actor banks ASSAULT_OBJECTIVES +1.
        if (actor is not null)
        {
            Scoring.ScoreField? f = Scoring.GameScores.Field("ASSAULT_OBJECTIVES");
            if (f is not null) Scoring.GameScores.AddToPlayer(actor, f, 1);
        }

        // Objective destroyed (QC SetResourceExplicit(enemy, -1)). Fire its target to activate the next one.
        Objective? next = Objectives.Find(n => !ReferenceEquals(n, o) && n.Name == o.Target);
        if (next is not null)
        {
            ActivateObjective(next);
            return;
        }

        // No next objective: the chain's terminal target is the round-end → attackers win the round.
        if (HasRoundEnd || string.IsNullOrEmpty(o.Target))
        {
            // QC ceil(time - game_starttime): the attack DURATION (used to clock round 2), not the raw clock.
            float elapsed = Api.Services is not null ? System.Math.Max(0f, Api.Clock.Time - GameStartTime) : 0f;
            DestroyFinalObjective(elapsed, State.Round >= 1);
        }
    }

    /// <summary>QC <c>Team_ColoredFullName(teamnum)</c> baked to a plain chat label for the bprint lines
    /// ("Red"/"Blue" — Assault is always red vs blue, teamplay_bitmask = BITS(2)). The full colored form lives
    /// in the notification token layer; the bprint echoes use the plain name to keep this file dependency-free.</summary>
    private static string TeamChatName(int team) => team == Teams.Red ? "^1Red team^7" : "^4Blue team^7";

    /// <summary>The team currently attacking (QC assault_attacker_team).</summary>
    public int AttackerTeam => State.AttackerTeam;

    /// <summary>The team currently defending (the other of the two Assault teams).</summary>
    public int DefenderTeam => State.AttackerTeam == Teams.Red ? Teams.Blue : Teams.Red;

    /// <summary>The team's objectives score (QC teamscores(team, ST_ASSAULT_OBJECTIVES) — SET to the 666 sentinel
    /// for the team that destroyed the final objective). Read through the unified GameScores two-slot team store.</summary>
    public int GetTeamObjectives(int team) => GS.TeamScore(team, GS.TeamSlotSecondary);

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        WinningTeam = 0;
        State.Round = 0;
        State.AttackerTeam = Teams.Red; // QC: NUM_TEAM_1 attacks first (swapped every round)
        State.FirstRoundDestroyTime = 0f;
        GS.ResetTeams(); // QC Score_ClearAll at match start: zero both team slots before declaring

        // QC sv_assault.qh GameRules_scoring(teamplay_bitmask, SFL_SORT_PRIO_SECONDARY, SFL_SORT_PRIO_SECONDARY, {
        // field_team(ST_ASSAULT_OBJECTIVES, "objectives", PRIMARY); field(SP_ASSAULT_OBJECTIVES, "objectives",
        // PRIMARY); }): the player primary is SP_ASSAULT_OBJECTIVES with SP_SCORE secondary (sprio = SECONDARY);
        // ST_SCORE (team slot 0) is the team secondary (stprio = SECONDARY); ST_ASSAULT_OBJECTIVES (team slot 1)
        // is the TEAM primary. Players rank by objectives destroyed, then score.
        GS.ScoreRulesBasics(teams: true);
        GS.TeamRulesBasics(scorePrio: Scoring.ScoreFlags.SortPrioSecondary); // ST_SCORE (slot 0) stprio = SECONDARY
        GS.SetTeamLabel(GS.TeamSlotSecondary, "objectives", Scoring.ScoreFlags.SortPrioPrimary); // ST_ASSAULT_OBJECTIVES (slot 1)
        GS.DeclareColumn("ASSAULT_OBJECTIVES", Scoring.ScoreFlags.None, "objectives");
        GS.SetSortKeys(GS.Field("ASSAULT_OBJECTIVES")!, GS.Score);
        GS.SeedTeams(2); // Assault is always red vs blue (teamplay_bitmask = BITS(2))

        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);

        // QC MUTATOR_HOOKFUNCTION(as, PlayerSpawn): tell each spawning player whether they attack or defend.
        _spawnHandler = OnPlayerSpawn;
        MutatorHooks.PlayerSpawn.Add(_spawnHandler);

        // QC MUTATOR_HOOKFUNCTION(as, PlayHitsound): return true for func_assault_destructible victims
        // (QC sv_assault.qc:593-598) to force a hitsound when shelling walls.
        _playHitsoundHandler = OnPlayHitsound;
        MutatorHooks.PlayHitsound.Add(_playHitsoundHandler);

        // QC MUTATOR_HOOKFUNCTION(as, OnEntityPreSpawn) (sv_assault.qc): Assault uses its OWN attacker/defender
        // spawn points (info_player_attacker/defender), so it DELETES any generic team spawns the map placed
        // (info_player_team1..4) — otherwise players would spawn at fixed team locations instead of the
        // role-appropriate attacker/defender area. The port's SpawnSystem.SpawnClassNames lists
        // info_player_team1..4 as live spawn classes, so without this an Assault map with team spawns would
        // pick them. Returning true from the hook DELETES the edict (QC delete(this)).
        _onEntityPreSpawnHandler = OnEntityPreSpawn;
        MutatorHooks.OnEntityPreSpawn.Add(_onEntityPreSpawnHandler);

        // QC MUTATOR_HOOKFUNCTION(as, VehicleInit) (sv_assault.qc): nudge a just-initialised vehicle's first
        // think out by 0.5s so it doesn't fire mid-roundstart. Returns false (does NOT abort init).
        _vehicleInitHandler = OnVehicleInit;
        MutatorHooks.VehicleInit.Add(_vehicleInitHandler);

        // QC target_objective sets .spawn_evalfunc = target_objective_spawn_evalfunc (sv_assault.qc:311): a spawn
        // spot whose .target names an objective that is inactive (health >= ASSAULT_VALUE_INACTIVE) or destroyed
        // (health < 0) scores '-1 0 0' (unusable), so attacker spawns near a not-yet/already-fallen objective are
        // deprioritized in favor of spots near the live objective. Install the spot-reject predicate here.
        SpawnSystem.SpotEvalReject = ShouldRejectSpawnSpot;
    }

    /// <summary>
    /// QC <c>target_objective_spawn_evalfunc</c> (sv_assault.qc:28): given a spawn spot, follow its <c>.target</c>
    /// to the objective it points at; reject the spot (return true) when that objective's health is destroyed
    /// (&lt; 0) or not-yet-active (&gt;= <see cref="ObjectiveInactive"/>). A spot that doesn't target an objective,
    /// or targets one that is currently active, is kept.
    /// </summary>
    private bool ShouldRejectSpawnSpot(Framework.Entity spot)
    {
        string targ = spot.Target;
        if (string.IsNullOrEmpty(targ))
            return false;
        for (int i = 0; i < Objectives.Count; i++)
        {
            Objective o = Objectives[i];
            if (o.Name != targ)
                continue;
            // QC: hlth < 0 (destroyed) || hlth >= ASSAULT_VALUE_INACTIVE (inactive) => '-1 0 0' (reject).
            return o.Health < 0f || o.Health >= ObjectiveInactive;
        }
        return false;
    }

    public override void Deactivate()
    {
        if (_spawnHandler is not null)
        {
            MutatorHooks.PlayerSpawn.Remove(_spawnHandler);
            _spawnHandler = null;
        }
        if (_playHitsoundHandler is not null)
        {
            MutatorHooks.PlayHitsound.Remove(_playHitsoundHandler);
            _playHitsoundHandler = null;
        }
        if (_onEntityPreSpawnHandler is not null)
        {
            MutatorHooks.OnEntityPreSpawn.Remove(_onEntityPreSpawnHandler);
            _onEntityPreSpawnHandler = null;
        }
        if (_vehicleInitHandler is not null)
        {
            MutatorHooks.VehicleInit.Remove(_vehicleInitHandler);
            _vehicleInitHandler = null;
        }
        // QC: the spawn_evalfunc chain is owned by the active gametype — drop the objective spawn bias on deactivate.
        if (SpawnSystem.SpotEvalReject == ShouldRejectSpawnSpot)
            SpawnSystem.SpotEvalReject = null;
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(as, PlayerSpawn): on spawn, centerprint the player's role — attackers get
    /// CENTER_ASSAULT_ATTACKING ("You are attacking!"), everyone else CENTER_ASSAULT_DEFENDING
    /// ("You are defending!"). Keyed on the live attacker team (which swaps each round).
    /// </summary>
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args)
    {
        Framework.Entity player = args.Player;
        if ((int)player.Team == State.AttackerTeam)
            NotificationSystem.Send(NotifBroadcast.One, player, MsgType.Center, "ASSAULT_ATTACKING");
        else
            NotificationSystem.Send(NotifBroadcast.One, player, MsgType.Center, "ASSAULT_DEFENDING");
        return false;
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(as, PlayHitsound) (sv_assault.qc:593-598): return true for a
    /// func_assault_destructible victim to force a hitsound when the attacker shoots a wall. The victim edict's
    /// classname is "func_assault_destructible" when it was spawned in the world; in a headless POJO test the
    /// damage path maps the edict back to a Destructible via <see cref="DestructibleFor"/>.
    /// </summary>
    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(as, OnEntityPreSpawn): Assault spawns players at its own info_player_attacker /
    /// info_player_defender points, so it DELETES any generic team spawn the map placed (info_player_team1..4)
    /// — returning true tells the spawn loop to delete the edict (QC <c>delete(this)</c>). Other classnames are
    /// left untouched (return false). The port's SpawnSystem otherwise treats info_player_team1..4 as live spawn
    /// classes, which would put players at fixed team locations rather than the role-appropriate area.
    /// </summary>
    private bool OnEntityPreSpawn(ref MutatorHooks.OnEntityPreSpawnArgs args)
    {
        string cn = args.Entity.ClassName;
        return cn == "info_player_team1" || cn == "info_player_team2"
            || cn == "info_player_team3" || cn == "info_player_team4";
    }

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(as, VehicleInit): delay a just-initialised vehicle's first think by 0.5s
    /// (<c>instance.nextthink = time + 0.5</c>) so it doesn't fire during a round restart. Returns false — this
    /// does NOT abort vehicle init (QC returns no abort). No-op in a headless POJO with no live clock.
    /// </summary>
    private bool OnVehicleInit(ref MutatorHooks.VehicleInitArgs args)
    {
        if (Api.Services is not null)
            args.Vehicle.NextThink = Api.Clock.Time + 0.5f; // QC instance.nextthink = time + 0.5
        return false; // QC: does not abort vehicle initialisation
    }

    private bool OnPlayHitsound(ref MutatorHooks.PlayHitsoundArgs args)
    {
        // QC: return (frag_victim.classname == "func_assault_destructible")
        Framework.Entity victim = args.Victim;
        if (victim.ClassName == "func_assault_destructible")
            return true;
        // Also check if it's a tracked Destructible in the chain (for unit tests / headless scenarios).
        if (DestructibleFor(victim) is not null)
            return true;
        return false;
    }

    /// <summary>
    /// QC target_objective_decrease_use: an attacker damaged an active objective. Subtract <paramref name="amount"/>
    /// from its health and return the remaining health. When it would drop to (or below) ~0 the objective is
    /// considered destroyed — the caller then advances to the next objective or, for the final one, calls
    /// <see cref="DestroyFinalObjective"/>. Only the attacking team may decrease objectives (QC actor.team check).
    /// </summary>
    public float DamageObjective(float currentHealth, float amount)
    {
        if (currentHealth >= ObjectiveInactive)
            return currentHealth; // not active yet
        float h = currentHealth - amount;
        return h < 0f ? -1f : h; // QC sets health to -1 when destroyed
    }

    /// <summary>
    /// QC target_assault_roundend_use: the attackers destroyed the final objective and win the round. In round
    /// one this records their destruction time and (in a full match) hands off to a second round with swapped
    /// roles; in round two (or campaign) it ends the match. The host passes the elapsed attack time so round
    /// two's clock can be set from it.
    /// </summary>
    public void DestroyFinalObjective(float elapsedAttackSeconds, bool isFinalRound)
    {
        if (MatchEnded)
            return;

        // The attacking team won this round.
        WinningTeam = State.AttackerTeam;
        // QC target_assault_roundend_use: the round-winning attackers' team score is SET to the 666 sentinel
        // (TeamScore_AddToTeam(attacker, ST_ASSAULT_OBJECTIVES, 666 - current)) so they top the team scoreboard.
        GS.SetTeamScore(State.AttackerTeam, GS.TeamSlotSecondary, 666);

        // QC WinningCondition_Assault: in campaign there is no second round, the match ends the instant the player
        // destroys the objective ("ent.cnt == 1 || autocvar_g_campaign" => WINNING_YES).
        bool campaign = TargetUtilities.IsCampaign?.Invoke() ?? false;

        if (isFinalRound || State.Round >= 1 || campaign)
        {
            MatchEnded = true; // QC: second round (or campaign single round) ends the match
            return;
        }

        // First round won by attackers: remember their time; a second round will swap roles.
        State.FirstRoundDestroyTime = elapsedAttackSeconds;
        // QC: starting round 2 — broadcast "Objective destroyed in <time>!" to everyone, with the seconds the
        // attackers took (Send_Notification(NOTIF_ALL, ..., CENTER_ASSAULT_OBJ_DESTROYED, ceil(time-game_starttime))).
        NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Center, "ASSAULT_OBJ_DESTROYED",
            (float)System.Math.Ceiling(elapsedAttackSeconds));

        // QC WinningCondition_Assault bprint: echo the core destruction to the server chat log (the centerprint
        // above is the player-facing copy). QC strcat(Team_ColoredFullName(attacker), " destroyed the objective
        // in ", process_time, "\n"). The seconds are ceil(time - game_starttime) (the attack duration).
        BPrint?.Invoke($"{TeamChatName(State.AttackerTeam)} destroyed the objective in {(int)System.Math.Ceiling(elapsedAttackSeconds)} seconds");

        // QC WinningCondition_Assault: schedule the second round — create the `as_round` entity with
        // nextthink = time + AS_ROUND_DELAY and freeze the game (game_stopped = true). DriveFrame fires the swap
        // (as_round_think → assault_new_round) once the 5s delay elapses. The clock is the match clock; in a
        // headless POJO (no Services) there is no live clock, so fall back to the elapsed attack time as the base.
        float now = Api.Services is not null ? Api.Clock.Time : elapsedAttackSeconds;
        SecondRoundPending = true;
        _secondRoundDueAt = now + RoundDelay;

        // QC: "make sure timelimit isn't hit while the game is blocked" — if the round's timelimit would lapse
        // during the freeze, bump it by AS_ROUND_DELAY/60 minutes so the freeze can't trip a timelimit win.
        if (Api.Services is not null)
        {
            float timelimit = Api.Cvars.GetFloat("timelimit");
            if (timelimit > 0f && now + RoundDelay >= GameStartTime + timelimit * 60f)
                SetTimelimitMinutes(timelimit + RoundDelay / 60f);
        }
    }

    /// <summary>
    /// QC <c>assault_new_round</c> (run from <c>as_round_think</c>): begin the second round with the attack/defend
    /// roles swapped. Round two's time limit is set to <see cref="AssaultState.FirstRoundDestroyTime"/> (QC
    /// <c>cvar_set("timelimit", ftos(ceil(time - AS_ROUND_DELAY - game_starttime) / 60))</c>) so the new attackers
    /// must beat the first team's destruction time. Resets the per-round win latch + objectives, then asks the host
    /// to restart the map (<see cref="OnSecondRoundRestart"/> = QC <c>ReadyRestart_force(true)</c>).
    /// </summary>
    public void StartSecondRound()
    {
        SecondRoundPending = false;
        State.Round = 1;
        State.AttackerTeam = DefenderTeam; // swap (QC NUM_TEAM_1 <-> NUM_TEAM_2)
        MatchEnded = false;
        WinningTeam = 0;

        // QC: round 2's timelimit = round 1's destruction time (in minutes). DriveFrame already consults
        // FirstRoundDestroyTime directly, but set the cvar too so the HUD/clock reads the round-2 limit.
        if (State.FirstRoundDestroyTime > 0f)
            SetTimelimitMinutes((float)System.Math.Ceiling(State.FirstRoundDestroyTime / 60f));

        // QC assault_new_round bprint: announce the round swap to the server chat log.
        BPrint?.Invoke("Starting second round...");

        ResetObjectives(); // QC assault_objective_reset on each new round
        OnSecondRoundRestart?.Invoke(); // QC ReadyRestart_force(true): full map/player reset + game_starttime re-stamp
    }

    /// <summary>Set the live <c>timelimit</c> cvar (minutes); no-op in a headless POJO with no Services.</summary>
    private static void SetTimelimitMinutes(float minutes)
    {
        if (Api.Services is not null)
            Api.Cvars.Set("timelimit", minutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// QC assault_objective_reset (per round start): reset every objective to inactive, un-spend the
    /// decreasers, and reset the destructible walls — then activate the first objective in the chain (the one
    /// no other objective targets) so the attackers have something to assault.
    /// </summary>
    public void ResetObjectives()
    {
        foreach (var o in Objectives)
            o.Health = ObjectiveInactive;
        foreach (var d in Decreasers)
            d.Spent = false;
        foreach (var w in Destructibles)
        {
            w.Health = w.MaxHealth;
            w.Active = false;
            // QC the round reset clears stale objective markers; ActivateObjective re-spawns the head's sprite.
            if (w.Sprite is not null)
            {
                Waypoints.WaypointSprites.Kill(w.Sprite);
                w.Sprite = null;
            }
            // QC ReadyRestart_force → the func_assault_destructible's func_breakable reset: a wall shot down in the
            // previous round comes back solid + shootable for the new round. Re-arm its world edict to match the POJO.
            if (w.WorldEntity is { } we && !we.IsFreed)
            {
                we.Health = w.MaxHealth;
                we.MaxHealth = w.MaxHealth;
                we.Solid = Framework.Solid.Bsp;
                we.TakeDamage = Framework.DamageMode.Aim;
            }
        }
        // The first objective is the one that nothing else targets (QC the roundstart-activated head).
        Objective? first = Objectives.Find(o => !Objectives.Exists(p => p.Target == o.Name));
        if (first is not null)
            ActivateObjective(first);
    }

    /// <summary>
    /// QC WinningCondition_Assault (the defender branch): the round's time limit elapsed without the attackers
    /// destroying the core, so the defenders win. This ALWAYS ends the match: QC only ever starts a second round
    /// off an attacker core-destruction (the <c>as_round</c> entity is created solely in the <c>ent.winning</c>
    /// branch); a defender timelimit win has no second round, so it is terminal in both round 1 and round 2.
    /// </summary>
    public void TimeLimitReached(bool isFinalRound = true)
    {
        if (MatchEnded || SecondRoundPending)
            return;
        WinningTeam = DefenderTeam; // QC: the defending team wins once the timelimit passes
        MatchEnded = true;          // QC: a defender timelimit win is always the end of the match
    }

    /// <summary>QC game_starttime — when the (current round's) match clock begins. The host raises it for the
    /// countdown and re-stamps it on a second round; default 0 (headless clock runs from t=0).</summary>
    public float GameStartTime { get; set; }

    /// <summary>
    /// QC WinningCondition_Assault, the per-frame win check (run from MUTATOR_HOOKFUNCTION(as, CheckRules_World)):
    /// if the attackers already destroyed the core this stays decided; otherwise, once the round's time limit
    /// elapses with the core still standing the defenders win. The host calls this once per frame (the
    /// CheckRules cadence) — it is a no-op until the time limit passes. <paramref name="now"/> is the current
    /// match clock (Api.Clock.Time); the QC default timelimit is 20 minutes (mapinfo default args "timelimit=20").
    ///
    /// Returns true once the match (or, mid-match, the round) is decided — the host then either ends the match
    /// (<see cref="MatchEnded"/>) or, for a non-final round, swaps roles via <see cref="StartSecondRound"/>.
    /// The faster-destroyer comparison falls out naturally: round two's time limit is set from
    /// <see cref="AssaultState.FirstRoundDestroyTime"/>, so if the second attackers don't beat it the defenders
    /// (= the first round's attackers) win on time.
    /// </summary>
    public bool DriveFrame(float now)
    {
        // QC assault_wall_think runs on its own nextthink loop independently of the win check: toggle every
        // func_assault_wall's solid/model with its objective's health each frame (cheap; only re-stamps on change).
        DriveWalls();

        if (MatchEnded)
            return true;

        // QC as_round_think (the as_round entity's nextthink): the attackers won round 1 and the 5s inter-round
        // freeze is counting down. Once it elapses, un-freeze and swap roles for round 2 (assault_new_round). While
        // it is pending WinningCondition_Assault returns WINNING_NO (the `if(as_round) return WINNING_NO;` guard).
        if (SecondRoundPending)
        {
            if (now >= _secondRoundDueAt)
                StartSecondRound();
            return false; // round not decided for the match: round 2 will run
        }

        if (WinningTeam != 0)
            return true; // attackers already destroyed the core this round (DestroyFinalObjective latched)

        // QC: timelimit defaults to 20 for Assault; in round two it is FirstRoundDestroyTime (the host re-stamps it).
        float timelimit = Api.Services is not null ? Api.Cvars.GetFloat("timelimit") : 0f;
        float limitSeconds = State.Round >= 1 && State.FirstRoundDestroyTime > 0f
            ? State.FirstRoundDestroyTime                 // round 2: beat round 1's destruction time
            : timelimit * 60f;                            // round 1 (or no recorded time): the map time limit

        if (limitSeconds <= 0f)
            return false; // no time limit configured — only objective destruction can decide the round

        if (now - GameStartTime < limitSeconds)
            return false; // time still on the clock; the round continues

        // Time elapsed with the core intact → the defenders win and the match ends (QC default SetWinners(defender);
        // a defender timelimit win is terminal — there is no second round off it in either round).
        TimeLimitReached();
        return true;
    }

    /// <summary>
    /// QC CA-style round gate: Assault has no per-round "enough players" precondition (the round simply runs the
    /// objective clock), so the round may always start. Exposed for the host to wire into
    /// <c>GameWorld.EnableRounds(CanRoundStart, CanRoundEnd, …)</c> in place of the generic defaults.
    /// </summary>
    public bool CanRoundStart() => true;

    /// <summary>
    /// QC round-end gate for the host's round handler: the round ends once it is decided — either the attackers
    /// destroyed the core (<see cref="WinningTeam"/> latched) or the time limit elapsed (<see cref="DriveFrame"/>).
    /// </summary>
    public bool CanRoundEnd() => MatchEnded || WinningTeam != 0;

    /// <summary>Player kills don't decide Assault (the objectives do); the handler just notifies the host.</summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;
        Events?.OnFrag(ev.Attacker as Player, victim, ev.DeathType);
        return false;
    }
}
