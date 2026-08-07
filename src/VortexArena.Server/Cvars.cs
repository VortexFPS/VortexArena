using VortexArena.Common.Services;

namespace VortexArena.Server;

/// <summary>
/// A console variable's static metadata — the C# successor to one QuakeC <c>autocvar_*</c> declaration
/// plus its <c>seta</c>/<c>set</c> registration in the default configs (xonotic-server.cfg /
/// _balance/*.cfg / bot cvars). QuakeC autocvars are compile-time bound globals seeded from the engine
/// cvar store; this port keeps the same idea as data — a <see cref="Name"/> → <see cref="DefaultValue"/>
/// mapping with <see cref="CvarFlags"/> — so <see cref="Cvars.RegisterDefaults"/> can stamp them into the
/// ambient <see cref="ICvarService"/> at boot, and gameplay reads them through the normal cvar facade.
/// </summary>
public readonly struct CvarDef
{
    /// <summary>The cvar name (QC the autocvar suffix, e.g. <c>sv_gravity</c> for <c>autocvar_sv_gravity</c>).</summary>
    public readonly string Name;

    /// <summary>The default string value (QC the value in the <c>set</c> command). Parsed to a float on read.</summary>
    public readonly string DefaultValue;

    /// <summary>Engine cvar flags (QC the <c>set</c> vs <c>seta</c> archive/notify distinction).</summary>
    public readonly CvarFlags Flags;

    /// <summary>One-line description (QC the trailing config comment); diagnostics only.</summary>
    public readonly string Description;

    public CvarDef(string name, string defaultValue, CvarFlags flags = CvarFlags.None, string description = "")
    {
        Name = name;
        DefaultValue = defaultValue;
        Flags = flags;
        Description = description;
    }

    /// <summary>Convenience overload for a non-archived cvar with just a description (flags default to None).</summary>
    public CvarDef(string name, string defaultValue, string description)
        : this(name, defaultValue, CvarFlags.None, description) { }
}

/// <summary>
/// The server cvar framework — the C# successor to the Xonotic autocvar system + the default server
/// configuration (xonotic-server.cfg and the balance/bot cfgs it execs). It is the data backing for the
/// many <c>Api.Cvars.GetFloat(...)</c> reads scattered across the gameplay layer: at boot
/// <see cref="RegisterDefaults"/> stamps a baseline of well-known server cvars (with their stock Xonotic
/// defaults and flags) into the ambient <see cref="ICvarService"/> so those reads return sane values
/// instead of 0, and a host can override any of them before/after boot via the normal cvar facade or the
/// <see cref="Commands"/> <c>set</c>/<c>cvar</c> commands.
///
/// This deliberately holds no state of its own — the engine <see cref="ICvarService"/> remains the single
/// source of truth (QC's cvar store). This class is the registry of <em>defaults</em> + a set of typed,
/// clamped convenience accessors mirroring QC's <c>autocvar_*</c> globals and helper reads (bound/notify).
/// </summary>
public static class Cvars
{
    // =============================================================================================
    // the default table (QC the stock server/balance/bot cfg values)
    // =============================================================================================

    private const CvarFlags Save = CvarFlags.Save;
    private const CvarFlags Notify = CvarFlags.Notify;

    /// <summary>
    /// The baseline server cvar defaults — a curated slice of xonotic-server.cfg + balanceXonotic.cfg +
    /// the bot cvars, covering the cvars the ported server core / bot AI actually read. Not exhaustive
    /// (QC has thousands); a host execs its own cfg over the top. Values match stock Xonotic.
    /// </summary>
    public static readonly CvarDef[] Defaults =
    {
        // ---- world / physics ----
        // Only sv_gravity is registered here: it is a genuine world cvar that GameWorld writes per-map
        // (Cvars.Set("sv_gravity")) and many subsystems read (Cvars.Gravity, MonsterAI, vehicles, jumppads,
        // moving brushes). The per-tick *movement tunables* — sv_maxspeed / sv_maxairspeed / sv_jumpvelocity
        // / sv_accelerate / sv_airaccelerate / sv_friction / sv_stopspeed / sv_stepheight /
        // sv_gameplayfix_stepdown — are deliberately NOT pre-registered: their single source of truth is
        // MovementParameters.FromCvars(), which already substitutes the stock Xonotic values (physicsX.cfg)
        // when a cvar is unset. Registering them here only duplicated that table — and the hand-typed copies
        // had drifted wrong (e.g. sv_maxairspeed 30 vs stock 360), so on a host that does NOT load the real
        // cfg tree they SHADOWED the correct fallbacks and produced wrong physics. Leaving them out lets the
        // authoritative defaults win; a loaded physicsX.cfg still sets the live cvars for the few callers that
        // read them directly (vehicles/mutators, which each carry their own correct self-default too).
        new("sv_gravity", "800", Notify, "world gravity (u/s^2)"),

        // Step-up vertical-velocity limiter (PORT EXTENSION — see PlayerPhysics.ApplyStepUpSpeedClamp). Unlike the
        // magnitude movement tunables above (deliberately UNregistered so FromCvars owns their defaults), these two
        // ARE registered: (1) so the in-game console/menu can SEE + complete them (RegisterDefaults stamps the shared
        // MenuState.Cvars store at MenuState.Boot), and (2) so the listen-server cvar bridge — which only forwards a
        // console `set` to the server tick when the server store already Has(name) (NetGame._sharedCvarBridge) —
        // actually applies a change. SAFE from the shadowing trap that comment warns about: the registered value is
        // the NO-OP identity, byte-equal to MovementParameters.Defaults (scale 1 / max -1 = disabled), so FromCvars'
        // EXISTS-gated CvarRaw reads back exactly the same default it would have fallen back to. KEEP these two values
        // in lockstep with MovementParameters.Defaults.StepUpSpeedScale / StepUpSpeedMax.
        new("sv_step_upspeed_scale", "1", "step-up upward-velocity multiplier (1 = vanilla launch, 0 = step up without launching)"),
        new("sv_step_upspeed_max", "-1", "step-up upward-velocity hard cap in u/s (-1 = disabled/uncapped)"),

        // Global time scale (Darkplaces host_timescale / Xonotic slowmo): the real->sim time mapping for the WHOLE
        // simulation. <1 = slow motion, >1 = fast, 1 = real time, 0 = paused. ServerNet.StepWorld feeds it to
        // SimulationLoop.TimeScale and NetGame scales the client's input cadence + render clock by the same value,
        // so prediction stays in lockstep (this is the same time-mapping that makes movement frame-rate-independent).
        // Registered (like the step-up knobs) so the console/menu can set it and the listen-server cvar bridge applies it.
        new("slowmo", "1", Notify, "global time scale: <1 slow-motion, >1 fast, 1 = real time, 0 = paused"),

        // DP overload semantics (frametime parity audit 2026-07-11; read live in ServerNet.StepWorld). DP sheds
        // sim time under overload (sv_main.c:2604 caps the owed backlog at 0.1s and discards the rest) and
        // wall-clock-budgets the catch-up loop (:2676 aborttime) — the game runs briefly, uniformly slow instead
        // of paying its time-debt with burst ticks that spike the already-slow frames (the r16 wobble oscillator).
        // DP sys_ticrate (sv_main.c:166; the "server physics rate" every DP admin knows): seconds of sim
        // time per fixed server tick, applied at GameWorld.Boot and re-read LIVE each host frame by
        // ServerNet.StepWorld -> SimulationLoop.TickSeconds (clamped [0.001, 0.1] like DP's sane band), so a
        // console change retunes the running server exactly like DP. Lineage: the DP ENGINE default is 1/72 s
        // (72.2 Hz) — registered here as the sentinel "0" because the decimal string 0.0138889 parses a hair
        // ABOVE the exact binary 1/72 the sim compares against (an Advance by exactly one tick-quantum then
        // runs zero ticks — caught by MaxVelocityClampTests); 0/unset maps to the exact constant. Xonotic
        // SHIPS 0.015625 (64 Hz) in xonotic-common.cfg; the VORTEX config layer sets 0.0166667 (60 Hz) in
        // vortex-server.cfg (Bryan 2026-08-03) — see the cfg for the reasoning + the one-line reverts.
        new("sys_ticrate", "0", "seconds of sim time per server tick (DP sys_ticrate). 0 = engine default (exact 1/72 s, DP parity); 0.015625 = 64 Hz (Xonotic\u0027s shipped default, and the Vortex default via vortex-server.cfg)"),

        new("sv_overload_timedrop", "1", "1 = DP parity: cap owed sim backlog at 0.1s, shedding the excess (overload = brief uniform slow-motion); 0 = legacy preserve-and-burst catch-up"),
        new("sv_catchup_wallbudget_ms", "0", "wall-clock ms a frame may spend running catch-up ticks before deferring the rest (first owed tick always runs; DP aborttime analogue); 0 = unlimited (default — the 2026-07-11 playtest found no felt benefit; the timedrop + soft cap already bound catch-up)"),

        // [T45] warpzone self-targeting (lib/warpzone/server.qc WarpZone_InitStep_FindTarget). Behaviour is already
        // correct without this entry (Warpzone.cs reads it via Api.Cvars.GetFloat, which returns 0 when unset), but
        // registering it makes it visible to cvarlist/the menu and lets the listen-server `set` bridge apply changes.
        new("sv_warpzone_allow_selftarget", "0", "1 = a warpzone may target itself (default 0: never self-link)"),

        // ---- match flow / limits (mapinfo + xonotic-server.cfg) ----
        new("timelimit", "20", Notify, "match time limit in minutes (0 = none)"),
        new("fraglimit", "0", Notify, "score limit (0 = none)"),
        new("leadlimit", "0", Notify, "lead margin to win (0 = none)"),
        // overtime / sudden death (xonotic-server.cfg:273-275; gametypes-server.cfg leadlimit_and_fraglimit).
        // Read by OverTimeManager (QC world.qc InitiateSuddenDeath / InitiateOvertime / WinningCondition_Scores):
        // a tied timed match adds normal overtimes (up to timelimit_overtimes) then enters suddendeath.
        new("timelimit_overtime", "2", "duration in minutes of one added overtime, added to the timelimit"),
        new("timelimit_overtimes", "0", "how many overtimes to add at max"),
        new("timelimit_suddendeath", "5", "minutes suddendeath lasts after all overtimes are added and still no winner"),
        new("leadlimit_and_fraglimit", "0", "both leadlimit AND fraglimit must be reached"),
        new("g_maxplayers", "0", "0 = unlimited player slots"),
        new("minplayers", "0", "fill FFA up to this many with bots"),
        new("minplayers_per_team", "0", "fill each team up to this many with bots"),

        // ---- warmup / ready-restart (xonotic-server.cfg) ----
        new("g_warmup", "0", "enable warmup stage"),
        new("g_warmup_limit", "180", "warmup time limit in seconds (-1 = until ready, 0 = use timelimit)"),
        new("g_warmup_allow_timeout", "0"),
        new("g_warmup_majority_factor", "0.8", "fraction of players that must ready up"),
        new("sv_ready_restart", "0"),
        new("sv_ready_restart_after_countdown", "0", "reset players and map items after the countdown ended, instead of at the beginning of the countdown"),
        new("sv_ready_restart_repeatable", "0"),
        // QC map_minplayers (world.qc:697): per-map worldspawn key, the g_warmup<0 minimum-player lower bound (0 = none).
        new("map_minplayers", "0", "minimum players for the map (g_warmup -1 lower bound)"),
        // QC g_start_delay (xonotic-common.cfg:185-186): the pre-match join window (0 listen / 15 dedicated). Listen
        // server default; a dedicated server raises it to 15 in its config.
        new("g_start_delay", "0", "delay before the game starts, so everyone can join (15 recommended on a public server)"),

        // ---- intermission / map change (xonotic-server.cfg) ----
        new("sv_mapchange_delay", "5", "scoreboard hold before map change"),
        new("sv_intermission_cdtrack", "", "music track(s) looped at intermission (empty = keep none); a random word is chosen"),
        new("sv_autoscreenshot", "0", "force clients with cl_autoscreenshot to screenshot the end-of-match scoreboard"),
        new("g_maplist_votable", "6", "candidates on the end-of-match map vote"),
        new("g_maplist_votable_timeout", "30", "map vote duration"),
        new("g_maplist_votable_abstain", "0"),

        // ---- teamplay / balance (xonotic-server.cfg) ----
        new("teamplay", "0", Notify),
        new("g_balance_teams", "1", "automatic team balance"),
        new("g_balance_teams_prevent_imbalance", "1"),
        // QC server/teamplay.qc QueueNeeded / TeamBalance_QueuedPlayersTagIn (xonotic-server.cfg): during a match
        // (not warmup/campaign, >=2 humans) a joiner whose chosen team would unbalance the teams is held as an
        // observer in a join queue and tagged in only when a deficit opens. Default off in Base. (The join-queue
        // tag-in machinery itself is not yet ported — registering the cvar gives the gate real data; see
        // sv-teamplay.queue.)
        new("g_balance_teams_queue", "0", "hold joiners in a queue during the match to keep teams balanced"),
        // QC server/teamplay.qc TeamBalance_RemoveExcessPlayers (xonotic-server.cfg:295-296): on a leave that
        // unbalances a 2-team match, move the NEWEST joiner on the overfull team to spectators — after a
        // g_balance_teams_remove_wait-second warning (0 = immediately). Default off in Base.
        new("g_balance_teams_remove", "0", "move the newest excess player to spectators on a leave (2-team)"),
        new("g_balance_teams_remove_wait", "10", "seconds to warn before moving an excess player to spectators"),
        new("g_balance_teams_skill", "1", "weigh balance by player skill, not just count"),
        // QC xonotic-server.cfg:297-298: skill-weighting tuning. The significance threshold is the z-score (in
        // standard deviations) a team-vs-player skill gap must exceed before it is treated as real; the unranked
        // factor scales the assumed skill of clients with no rating relative to the server average.
        new("g_balance_teams_skill_significance_threshold", "1.645",
            "team/player skill differences count as significant past this many standard deviations"),
        new("g_balance_teams_skill_unranked_factor", "0.666",
            "assumed skill of unranked clients as a fraction of the server-average skill"),
        new("g_changeteam_bantime", "30"),
        // QC xonotic-server.cfg:446 g_balance_kill_delay: the countdown (seconds) between a `kill` command / team
        // change and the player actually dying — the kill-indicator/announcer countdown reads this.
        new("g_balance_kill_delay", "2", "delay (seconds) before a `kill` command actually kills you"),
        // QC xonotic-server.cfg:447 g_balance_kill_antispam: added to the next-allowed-kill time on a repeat `kill`
        // (clientkill_nexttime carry-forward) so mashing `kill` keeps extending the countdown. XPM/XDF set 0.
        new("g_balance_kill_antispam", "5", "time added to the next allowed `kill` when you mash the command"),
        // QC sv_teamnagger (xonotic-server.cfg:300): team-size gap threshold for the unbalanced-teams nag; g_warmup
        // won't end while it's tripped. The warmup badteams gate (ReadyCount) reads this via Teamplay.
        new("sv_teamnagger", "2", "team size difference threshold for the unbalanced-teams nag (0 = off)"),
        // The engine `teamplay` cvar (DP exposes it as the QC global every QC teamplay read compiles to). Base's
        // InitGameplayMode sets it per gametype; the port mirrors it at GameWorld boot. RUNTIME state — never set
        // it by hand. (playtest #35: it was never written at all, so every Cvars.Teamplay reader was silently
        // non-team — bots attacked CTF teammates, warmup's teamplay_lockonrestart was dead.)
        new("teamplay", "0", "team game in progress (set by the gametype at match boot)"),
        // QC autocvar_bot_typefrag: allow bots to shoot players who are typing (BUTTON_CHAT). Base default 0 =
        // spare typing players (bot_shouldattack, bot/default/aim.qc:120).
        new("bot_typefrag", "0", "allow bots to shoot players who are typing in chat"),
        // QC teamplay_lockonrestart (xonotic-server.cfg:29): lock teams once the match restarts (ReadyRestart_force).
        new("teamplay_lockonrestart", "0", "lock teams once all players readied up and the game restarted"),
        // QC server/teamplay.qc:41-44 + xonotic-server.cfg: the forced-team id/IP lists. Player_DetermineForcedTeam
        // matches a connecting client's crypto-id/IP against these space-separated lists to pin them to a team;
        // g_forced_team_otherwise (red|blue|yellow|pink|spectate|default) is the fallback for unlisted clients.
        new("g_forced_team_red", "", "space-separated id/IP list forced onto the red team"),
        new("g_forced_team_blue", "", "space-separated id/IP list forced onto the blue team"),
        new("g_forced_team_yellow", "", "space-separated id/IP list forced onto the yellow team"),
        new("g_forced_team_pink", "", "space-separated id/IP list forced onto the pink team"),
        new("g_forced_team_otherwise", "default", "forced-team action for unlisted clients (red|blue|yellow|pink|spectate|default)"),

        // ---- health / armor regen + rot (balance-xonotic.cfg; values match stock so regen works without
        //      a loaded balance cfg — the port's old 0 defaults left health regen OFF out of the box) ----
        new("g_balance_health_regen", "0.08", Notify),
        new("g_balance_health_regenlinear", "0.5", Notify),
        new("g_balance_health_regenstable", "100", Notify),
        new("g_balance_health_rot", "0.02", Notify),
        new("g_balance_health_rotlinear", "1", Notify),
        new("g_balance_health_rotstable", "100", Notify),
        new("g_balance_health_limit", "200", Notify),
        new("g_balance_armor_regen", "0", Notify),
        new("g_balance_armor_regenlinear", "0", Notify),
        new("g_balance_armor_regenstable", "100", Notify),
        new("g_balance_armor_rot", "0.02", Notify),
        new("g_balance_armor_rotlinear", "1", Notify),
        new("g_balance_armor_rotstable", "100", Notify),
        new("g_balance_armor_limit", "200", Notify),
        new("g_balance_armor_blockpercent", "0.7", Notify),
        new("g_balance_health_start", "100", Notify),
        new("g_balance_armor_start", "0", Notify),
        new("g_balance_fuel_regen", "0.1", Notify),
        new("g_balance_fuel_regenlinear", "0", Notify),
        new("g_balance_fuel_regenstable", "50", Notify),
        new("g_balance_fuel_rot", "0.05", Notify),
        new("g_balance_fuel_rotlinear", "0", Notify),
        new("g_balance_fuel_rotstable", "100", Notify),
        new("g_balance_fuel_limit", "100", Notify),
        // damage-pause windows (after a hit / after a pickup / freshly spawned). The *_spawn timers PRIME the
        // rot-pause at spawn so an above-stable spawn (e.g. 200 HP CA/LMS) holds before decaying.
        new("g_balance_pause_health_regen", "5", Notify, "no health/armor regen for N s after damage"),
        new("g_balance_pause_health_regen_spawn", "0", Notify),
        new("g_balance_pause_health_rot", "1", Notify, "no health rot for N s after a pickup"),
        new("g_balance_pause_health_rot_spawn", "5", Notify),
        new("g_balance_pause_armor_rot", "1", Notify),
        new("g_balance_pause_armor_rot_spawn", "5", Notify),
        new("g_balance_pause_fuel_regen", "2", Notify),
        new("g_balance_pause_fuel_rot", "5", Notify),
        new("g_balance_pause_fuel_rot_spawn", "10", Notify),

        // ---- combat damage (xonotic-server.cfg + balance-xonotic.cfg). The DamageSystem reads these; without
        //      them team modes did real friendly fire (mode-2 fallback) instead of stock mode-4 graphics-only. ----
        new("teamplay_mode", "4", Notify, "1=no FF, 2=FF+self, 3=no FF+self, 4=obey g_friendlyfire*/g_mirrordamage*"),
        new("g_friendlyfire", "0.5", Notify, "teamplay_mode 4 friendly-fire factor"),
        new("g_friendlyfire_virtual", "1", Notify, "show FF as graphics only (no HP lost)"),
        new("g_friendlyfire_virtual_force", "1", Notify, "still apply knockback for virtual FF"),
        new("g_mirrordamage", "0.7", Notify, "teamplay_mode 4 mirror-damage factor"),
        new("g_mirrordamage_virtual", "1", Notify, "show mirror damage as graphics only"),
        new("g_mirrordamage_onlyweapons", "0", Notify),
        new("g_teamdamage_threshold", "40", Notify, "team damage before mirror punishment kicks in"),
        new("g_teamdamage_resetspeed", "20", Notify),
        new("g_maxpushtime", "8", Notify, "credited-attacker window for environmental deaths"),
        // teleporter telefrag rules (xonotic-server.cfg:59-61; common/mapobjects/teleporters.qh autocvar_g_telefrags*).
        // Registered so a server override (set g_telefrags 0, etc.) actually applies — the port's Teleporters.cs
        // previously used hardcoded fallbacks that matched these defaults but were inert. Stock defaults: all 1.
        new("g_telefrags", "1", Notify, "enable telefragging (kill anyone standing on a teleport/portal exit)"),
        new("g_telefrags_teamplay", "1", Notify, "1 = never telefrag teammates"),
        new("g_telefrags_avoid", "1", Notify, "random-destination teleporters avoid exits where a telefrag would occur"),
        new("g_weapondamagefactor", "1", Notify, "global weapon damage multiplier"),
        new("g_weaponforcefactor", "1", Notify, "global weapon force multiplier"),
        new("g_balance_selfdamagepercent", "0.65", Notify, "self-damage (rocket/blaster-jump) scale"),
        new("g_balance_damagepush_speedfactor", "2.5", Notify),
        new("g_player_damageforcescale", "2", Notify, "knockback multiplier vs players"),
        new("g_spawnshieldtime", "1", "seconds of spawn invulnerability (0 = none; lost on fire)"),
        new("g_spawnshield_blockdamage", "1", Notify, "1 = spawn shield fully blocks damage"),
        new("g_spawn_alloweffects", "3", "spawn FX: 1=particles, 2=sound, 3=both"),
        new("g_spawn_furthest", "0.5", "fraction of spawns biased far from players"),
        new("g_spawn_useallspawns", "0"),
        // ---- anti-abuse spawn scoring (spawn-system-analysis 2026-07-06). R1 ships ON (a deliberate divergence
        //      from Base; the Duel gametype disables it for the match — Duel.Activate saves + sets
        //      g_spawn_avoid_los 0, Deactivate restores); the rest default off/faithful. ----
        new("g_spawn_avoid_los", "1", "[R1] avoid spawning where a live enemy has line of sight to the spot (0 = Base)"),
        new("g_spawn_avoid_los_distance", "1250", "[R1] only enemies within this range (qu) count for the LOS check"),
        new("g_spawn_avoid_death_radius", "0", "[R2] demote spawns within this radius (qu) of where you just died (0 = off)"),
        new("g_spawn_avoid_death_time", "8", "[R2] seconds over which the death-point avoidance decays to nothing"),
        new("g_spawn_furthest_topfraction", "0", "[R3] far pick: 0 = Base dist^5 roulette; >0 = uniform among spots within this fraction of the max weight"),
        new("g_spawn_distance_enemies_only", "0", "[R4] teamplay: measure spawn distance to the nearest ENEMY only, not teammates (0 = Base)"),
        new("g_spawn_occupied_repick", "1", "[R5] if the chosen spot is occupied by a live player, re-pick once instead of overlapping"),
        new("sv_spawn_debug", "0", "log one line per spawn (nearest-enemy distance + enemy LOS) for tuning; NOT Base's spawn_debug"),
        new("sv_gibhealth", "100", Notify, "health below -N gibs the corpse"),
        new("sv_gentle", "0", Notify, "1 = suppress gore + pain/death voices"),
        new("g_forced_respawn", "0", "1 = auto-respawn at g_respawn_delay_max even without pressing fire"),

        // ---- warmup loadout (balance-xonotic.cfg g_warmup_start_*; only used while g_warmup is on) ----
        new("g_warmup_allguns", "1", "warmup gives all weapons"),
        new("g_warmup_start_health", "100", Notify),
        new("g_warmup_start_armor", "100", Notify),
        new("g_warmup_start_ammo_shells", "30", Notify),
        new("g_warmup_start_ammo_nails", "160", Notify),
        new("g_warmup_start_ammo_rockets", "80", Notify),
        new("g_warmup_start_ammo_cells", "30", Notify),
        new("g_warmup_start_ammo_fuel", "0", Notify),

        // ---- contents (water/lava/slime/fall) damage (balance-contents) ----
        new("g_balance_contents_damagerate", "0.2", Notify, "tick interval for liquid/contents damage"),
        new("g_balance_contents_playerdamage_drowning", "30", Notify, "drown dps"),
        new("g_balance_contents_drowndelay", "10", Notify, "air time before drowning"),
        new("g_balance_contents_playerdamage_lava", "50", Notify),
        new("g_balance_contents_playerdamage_lava_burn", "0", Notify),
        new("g_balance_contents_playerdamage_lava_burn_time", "5", Notify),
        new("g_balance_contents_playerdamage_slime", "40", Notify),
        new("g_balance_contents_projectiledamage", "10000", Notify),
        new("g_balance_falldamage_deadminspeed", "250", Notify),
        new("g_balance_falldamage_factor", "0.15", Notify),
        new("g_balance_falldamage_minspeed", "900", Notify),
        new("g_balance_falldamage_maxdamage", "40", Notify),
        new("g_balance_falldamage_onlyvertical", "0", Notify),
        new("g_maxspeed", "0", Notify, "shooting-star kill speed (0 = off)"),

        // ---- respawn timing (xonotic-server.cfg) ----
        new("g_respawn_delay_small", "2"),
        new("g_respawn_delay_small_count", "0", "players-per-team at which the small delay applies (<=0 = min)"),
        new("g_respawn_delay_large", "2"),
        new("g_respawn_delay_large_count", "8", "players-per-team at which the large delay applies"),
        new("g_respawn_delay_max", "5", "max wait before forced respawn (needs g_forced_respawn 1)"),
        new("g_respawn_delay_forced", "0"),
        new("g_respawn_waves", "0"),
        new("g_respawn_ghosts", "1"),

        // ---- bot management (bot cvars; defaults = SHIPPED xonotic-server.cfg:120-195 values — these matter
        //      when the real cfg tree isn't mounted, so they must not drift from stock) ----
        new("bot_number", "0", "how many bots to keep on the server"),
        new("bot_vs_human", "0", "force bots onto one team vs humans"),
        new("bot_join_empty", "0"),
        new("bot_prefix", "[BOT]"),
        new("bot_suffix", ""),
        new("bot_usemodelnames", "0"),
        new("bot_config_file", "bots.txt", "name and path of the bot configuration file"),
        new("skill", "8", "bot skill 0..10 (>100 = superbot); xonotic-server.cfg ships 8"),
        new("skill_auto", "0"),
        new("bot_god", "0"),
        new("bot_nofire", "0"),
        new("bot_ignore_bots", "0"),
        new("bot_wander_enable", "1"),

        // ---- bot AI tuning (bot cvars; defaults from xonotic-server.cfg:135-183) ----
        new("bot_ai_thinkinterval", "0.05"),
        new("bot_ai_strategyinterval", "7"),
        new("bot_ai_strategyinterval_movingtarget", "5.5"),
        new("bot_ai_enemydetectioninterval", "2"),
        new("bot_ai_enemydetectioninterval_stickingtoenemy", "4"),
        new("bot_ai_enemydetectionradius", "10000"),
        new("bot_ai_chooseweaponinterval", "0.5"),
        // Port benchmark/demo knob (NOT a Base cvar): >0 = seconds per weapon; bots step through their
        // owned loadout one weapon at a time (entity slot phases them apart) instead of range-picking.
        // Used by the perf-run demo scenario together with g_weaponarena; keep 0 for normal play.
        new("bot_ai_weapon_rotate", "0"),
        new("bot_ai_dangerdetectioninterval", "0.25"),
        new("bot_ai_dangerdetectionupdates", "64"),
        new("bot_ai_friends_aware_pickup_radius", "500"),
        new("bot_ai_ignoregoal_timeout", "3"),
        new("bot_ai_keyboard_distance", "250"),
        new("bot_ai_keyboard_threshold", "0.57"),
        new("bot_ai_timeitems", "1", "let bots time item respawns"),
        new("bot_ai_timeitems_minrespawndelay", "25"),
        new("bot_ai_weapon_combo", "1"),
        new("bot_ai_weapon_combo_threshold", "0.4"),
        new("bot_ai_aimskill_offset", "1.8"),
        new("bot_ai_aimskill_think", "1"),
        new("bot_ai_aimskill_mouse", "1"),
        new("bot_ai_aimskill_fixedrate", "15"),
        new("bot_ai_aimskill_blendrate", "2"),
        new("bot_ai_aimskill_firetolerance", "1"),
        new("bot_ai_bunnyhop_skilloffset", "7", "skill at/above which bots bunnyhop"),
        new("bot_ai_bunnyhop_dir_deviation_max", "20"),
        new("bot_ai_bunnyhop_downward_pitch_max", "30"),
        new("bot_ai_bunnyhop_turn_angle_min", "4"),
        new("bot_ai_bunnyhop_turn_angle_max", "80"),
        new("bot_ai_bunnyhop_turn_angle_reduction", "40"),
        new("bot_ai_custom_weapon_priority_distances", "300 850"),
        // The shipped xonotic-server.cfg values, NOT empty strings. These are the fallbacks used when the cfg
        // tree is not mounted (headless tests, a bare host), and an empty list makes PickFromPriority return
        // null so the bot falls through to "highest-impulse owned weapon" — a different weapon choice than
        // stock at every range. The file's own contract at the top of this section says these must not drift
        // from stock; they had.
        new("bot_ai_custom_weapon_priority_far", "vaporizer oknex vortex rifle electro devastator mortar hagar hlac crylink blaster okmachinegun machinegun fireball seeker okshotgun shotgun tuba minelayer"),
        new("bot_ai_custom_weapon_priority_mid", "vaporizer devastator oknex vortex fireball seeker mortar electro okmachinegun machinegun arc crylink hlac hagar okshotgun shotgun blaster rifle tuba minelayer"),
        new("bot_ai_custom_weapon_priority_close", "vaporizer oknex vortex okshotgun shotgun okmachinegun machinegun arc hlac tuba seeker hagar crylink mortar electro devastator blaster fireball rifle minelayer"),
        // QC api.qh:8 / xonotic-server.cfg:373. Was never registered, and Waypoint.cs read it with a hardcoded
        // fallback of 2 against Base's 0 — so it was both invisible to `search`/apropos and inverted.
        new("g_waypoints_for_items", "0",
            "make waypoints out of items; 0 = never, 1 = unless the mapper prevents it, 2 = always"),
        new("bot_navigation_ignoreplayers", "0"),

        // ---- idle kick (QC server/client.qc PlayerFrame sv_maxidle block) ----
        // sv_maxidle: seconds before an idle PLAYER or join-queue client is kicked (0 = disabled, the default).
        // sv_maxidle_playertospectator: if > 0 and a player is idle, move-to-spec instead of kick (Base 60).
        // sv_maxidle_minplayers: minimum human clients before the idle kick is active (Base 2; 0 = always active).
        // sv_maxidle_alsokickspectators: also apply the idle kick to observers/spectators (source default 0).
        // sv_maxidle_slots: free-slot threshold — if > 0 AND sv_dedicated, only kick when free slots <= this.
        // sv_maxidle_slots_countbots: count bots when computing the free-slot occupancy.
        new("sv_maxidle", "0", "seconds of player inactivity before kick (0 = off)"),
        // Source autocvar defaults (server/client.qh:38-39): playertospectator 60, minplayers 2 — the port had
        // shipped both at 0, which silently disabled move-to-spec and let the idle kick arm with no humans present.
        new("sv_maxidle_playertospectator", "60", "move-to-spec instead of kick when >0 (seconds)"),
        new("sv_maxidle_minplayers", "2", "minimum players before the idle kick activates"),
        // Source autocvar default is 0 (the shipped xonotic-server.cfg:452 overrides it to 1); we keep the source
        // default like the rest of this table so a loaded cfg still wins — registry marks this row match:true.
        new("sv_maxidle_alsokickspectators", "0", "also idle-kick spectators/observers"),
        new("sv_maxidle_slots", "0", "free-slot threshold for dedicated-server idle kick (0 = always)"),
        new("sv_maxidle_slots_countbots", "0", "count bots toward sv_maxidle_slots occupancy"),
        // g_maxplayers_spectator_blocktime: spectator grace before the sv_spectate=0 kick fires (xonotic-server.cfg:33 ships 10).
        new("g_maxplayers_spectator_blocktime", "10", "grace period (s) before kicking spectators when sv_spectate=0"),

        // ---- server identity / admin (xonotic-server.cfg) ----
        new("sv_dedicated", "0", CvarFlags.ReadOnly),
        new("sv_spectate", "1"),
        new("hostname", "Vortex Arena Server", Save),
        new("g_maplist", "", Save, "the map rotation"),
        // QC server/client.qh:53 / xonotic-server.cfg:5: max player name length (not counting color codes)
        // enforced by PlayerFrame; a longer name is truncated and the player warned. 0 = no limit.
        new("sv_name_maxlength", "64", "max player name length (not counting color codes) allowed by the server"),
        // QC xonotic-server.cfg:370 / 503: the welcome-screen MOTD + mutator message shown to joining players
        // (SendWelcomeMessage, server/client.qc:1130/1132). Both default empty (no extra welcome text).
        new("sv_motd", "", "additional information to show on the welcome screen that greets joining players"),
        new("g_mutatormsg", "", "mutator message shown on the welcome screen"),

        // ---- DS-7: the modern master (Conductor) announce lane ----
        // Additive to the classic dpmaster heartbeat, which is untouched. sv_public gates BOTH lanes and is
        // deliberately NOT registered here: DP leaves it unregistered/0, so a listen server that never asked
        // to be public cannot leak itself to a public directory by upgrading. server.cfg.example ships
        // `set sv_public 1` for operators who do want listing.
        new("sv_master_url", "https://conductor.vortexfps.org",
            "base URL of the master server this host announces to (self-hosters point this at their own)"),
        // The adoption OFFER, not adoption itself: it only asks Conductor to show this box in its queue.
        // Accepting there still grants nothing until the runner dials out and proves the private key behind
        // the fingerprint below, so an operator who flips this on has not handed over control by doing so.
        new("conductor_control", "0", "1 = offer this server for Conductor orchestration (needs conductor_control_key)"),
        // Lowercase hex sha256 of the runner's public key. Set by the runner in the instance's server.cfg
        // rather than read out of the runner's key file, because the game must not know where a launcher
        // keeps its data directory. Empty (a server with no runner beside it) omits the offer entirely.
        new("conductor_control_key", "", "sha256 fingerprint of the runner's control key, written by the runner"),
        // Map catalog (map-catalog-v1 §10): report which .pk3 packages this server carries, so players can
        // see and download its map pool from the server browser. Default ON — the announce only ever carries
        // the 64-byte pool hash, and the pool itself moves only when the master has never seen it, from this
        // server or any other. 0 omits the field entirely, which the master reads as "does not report a
        // catalog" and NOT as "has no maps".
        new("sv_master_catalog", "1", "1 = report this server's .pk3 map pool to the master (map catalog v1)"),
        // Where a player can fetch this server's packages, e.g. https://maps.example.org — the package's file
        // name is appended. Empty by default: most operators have nowhere to publish, and the master may
        // mirror packages into its own content store anyway. Advisory in any case (map catalog §5): the
        // master never follows this URL, and the client verifies the package hash after downloading, so a
        // wrong one costs a download attempt and nothing else.
        new("sv_master_catalog_url", "", "base URL where this server's .pk3 packages can be downloaded ('' = none)"),

        // ---- voting (server/command/vote.qc) ----
        new("sv_vote_call", "1", "allow players to call votes"),
        new("sv_vote_change", "0", "allow changing a vote after casting"),
        new("sv_vote_commands", "restart fraglimit chmap gotomap nextmap endmatch reducematchtime extendmatchtime allready kick kickban cointoss shuffleteams", "votable command whitelist"),
        new("sv_vote_master", "0", "allow vote-master subsystem"),
        new("sv_vote_master_callable", "0", "allow calling a vote to become master"),
        new("sv_vote_master_commands", "movetoauto movetored movetoblue movetoyellow movetopink movetospec", "extra commands the master may run"),
        new("sv_vote_master_password", "", "password to log in as vote master ('' = disabled)"),
        new("sv_vote_master_playerlimit", "2", "min players for a master vote to pass"),
        new("sv_vote_singlecount", "0", "don't recount on each vote (only at timeout)"),
        new("sv_vote_timeout", "30", "seconds a called vote stays open"),
        new("sv_vote_wait", "120", "cooldown after calling a vote"),
        new("sv_vote_stop", "5", "cooldown after stopping your own vote"),
        new("sv_vote_majority_factor", "0.5", "overall yes-fraction to pass"),
        new("sv_vote_majority_factor_of_voted", "0.5", "yes-fraction of those who voted (at timeout)"),
        new("sv_vote_no_stops_vote", "0", "caller voting no cancels their own vote"),
        new("sv_vote_nospectators", "0", "0=spectators vote, 1=except warmup/intermission, 2=never"),
        new("sv_vote_gamestart", "0", "allow vote calling before the match starts"),
        new("sv_vote_limit", "160", "max votable command length (0 = no limit)"), // commands.cfg:361
        new("sv_vote_debug", "0", "include bots as voters; print debug banner"),
        new("sv_vote_override_mostrecent", "0", "allow voting a recently-played map"),
        new("sv_status_privacy", "1", Save, "hide IP/crypto_id from who/status replies shown to clients"), // commands.cfg:157

        // ---- cheats (server/cheats.qc) ----
        new("sv_cheats", "0", Notify, "0=off, 1=cheats, 2=cheats for non-players + wider teleport"),
        new("g_grab_range", "200", "non-cheat object drag range"),
        new("g_max_info_autoscreenshot", "3"), // xonotic-server.cfg:643
        new("sv_clones", "0", "max clones a player may spawn (cheat)"),
        new("g_allow_checkpoints", "0", "speedrun checkpoint teleport is not a cheat"),

        // ---- bans (server/ipban.qc + command/banning.qc) ----
        new("g_banned_list", "", "serialized local ban list"),
        new("g_banned_list_idmode", "1", "IP bans only apply to clients without a crypto id"),
        new("g_ban_telluser", "1", "tell a kicked client they are banned"),
        new("g_ban_default_bantime", "5400", "default ban duration (s)"),
        new("g_ban_default_masksize", "3", "default IP mask size (1..4)"),
        new("g_chatban_list", "", "muted players (IP/id prefixes)"),
        new("g_playban_list", "", "forced-spectate players (IP/id prefixes)"),
        new("g_voteban_list", "", "vote-banned players (IP/id prefixes)"),
        // QC xonotic-server.cfg:436 — default 0 (play-banned players ARE allowed into minigames out of the box).
        new("g_playban_minigames", "0", "disallow playbanned players (forced to spectate) from minigames"),

        // ---- event log + player stats (server/gamelog.qc + common/playerstats.qc) ----
        new("sv_eventlog", "0", "enable the event log"),
        new("sv_eventlog_console", "0", "echo the event log to the console"),
        new("sv_eventlog_files", "0", "write the event log to files"),
        new("sv_eventlog_files_counter", "0", "match counter for log filenames"),
        new("sv_eventlog_files_nameprefix", "ServerLog-"),
        new("sv_eventlog_files_namesuffix", ".log"),
        new("sv_eventlog_files_timestamps", "1"),
        new("sv_eventlog_ipv6_delimiter", "0", "replace ':' with '_' in logged IPs"),
        new("g_playerstats_gamereport_uri", "", "stats upload endpoint ('' = disabled)"),
        // ---- score log (server/world.qc DumpStats / sv_cmd.qc printstats) ----
        new("sv_logscores_console", "0", "print scores to server console"), // xonotic-server.cfg:336
        new("sv_logscores_file", "0", "print scores to file"),              // xonotic-server.cfg:337
        new("sv_logscores_filename", "scores.log", "score-log filename"),   // xonotic-server.cfg:338
        new("sv_logscores_bots", "0", "exclude bots by default"),           // xonotic-server.cfg:339 (Base default 0, NOT 1)
        // QC g_score_resetonjoin (xonotic-server.cfg:302): 0 = keep score on (re)join (default), 1 = always wipe,
        // -1 = wipe unless the PreferPlayerScore_Clear hook vetoes. Read by GameScores.ClearPlayerOnJoin on the
        // spectator→player join path; MUST be registered so an admin's 1/-1 override actually applies.
        new("g_score_resetonjoin", "0",
            "reset players' scores when they rejoin the match (-1 = only where speccing+rejoining could be abused)"),

        // ---- serverflags sources (server/world.qc readlevelcvars → the networked `serverflags` global, the
        //      client gates fullbright + the pickup timer off these). Defaults from xonotic-server.cfg:611/614. ----
        new("sv_allow_fullbright", "1", "allow clients to use r_fullbright without the night-vision overlay"),
        new("sv_forbid_pickuptimer", "0", "disallow clients from seeing item pickup timers"),
        // The published serverflags bitfield (GameWorld.ReadLevelCvars mirrors ServerFlags.Value here); the
        // shared-store client reads it where QC reads the engine `serverflags` global.
        new("serverflags", "0", "computed server flags (SERVERFLAG_* bits); set by the server at map init"),

        // ---- diagnostics / logging (engine cvar; gates lib/log.qh → VortexArena.Common.Diagnostics.Log) ----
        // QC autocvar_developer: 0 = off, 1 = LOG_TRACE + LOG_INFO headers, 2 = + LOG_DEBUG and full source
        // locations. Read live by Log.Emit on every call, so `set developer 1` / `toggle developer` are instant.
        new("developer", "0", "developer/debug log verbosity (0=off, 1=trace+info, 2=+debug)"),

        // ---- client physics selection (server/command/cmd.qc ClientCommand_physics). Ships 0 → the `physics`
        //      command prints "disabled" unless an admin opts in (physics.cfg). Options string is physics.cfg's. ----
        new("g_physics_clientselect", "0", "allow clients to select their physics set"),
        new("g_physics_clientselect_options", "xonotic nexuiz vecxis quake quake2 quake3 cpma bones xdf", "the selectable physics sets"),

        // ---- monster editor (server/command/common.qc editmob). monsters.cfg ships g_monsters_edit 0,
        //      g_monsters_max 20, g_monsters_max_perplayer 0 — so editmob spawn is OFF by default (per-player 0). ----
        new("g_monsters_edit", "0", "0=off, 1=edit own monsters, 2=edit any (admin)"),
        new("g_monsters_max", "20", "max monsters alive server-wide"),
        new("g_monsters_max_perplayer", "0", "max monsters a single player may have (0 = none)"),

        // ---- campaign (server/campaign.qc) ----
        new("g_campaign", "0", "in-campaign master switch"),
        new("g_campaign_skill", "0", Save, "global bot-skill offset for the campaign"),
        new("g_campaign_forceteam", "0"),
        new("_campaign_index", "0", "current campaign map index"),
        new("_campaign_name", "", "current campaign id"),
        new("_campaign_testrun", "0", "campaign test run (instant win)"),

        // ---- timeout / timein (server/command/common.qc) ----
        new("sv_timeout", "0", "allow players to pause via timeout"),
        new("sv_timeout_leadtime", "4", "countdown before a timeout begins"),
        new("sv_timeout_length", "120", "max timeout duration"),
        new("sv_timeout_number", "2", "timeouts allowed per player"),
        new("sv_timeout_resumetime", "3", "countdown when resuming"),

        // ---- map rotation / map vote (server/intermission.qc + mapvoting.qc) ----
        new("g_maplist_index", "0", "rotation cursor into g_maplist"),
        new("g_maplist_mostrecent", "", "recently played maps (excluded from rotation)"),
        new("g_maplist_mostrecent_count", "3", "how many recent maps to remember"),
        new("g_maplist_shuffle", "1", "shuffle the rotation on load"),
        new("g_maplist_selectrandom", "0", "pick the next map at random"),
        new("g_maplist_votable_reduce_time", "15", "reduce the number of options shown after this amount of time during the vote; 0 = disable"),
        new("g_maplist_votable_reduce_count", "2", "options kept after reduce; if < 2, keep all maps that got any votes (if at least 2 did)"),
        new("g_maplist_votable_show_suggester", "1", "show which player suggested a map"),
        new("g_maplist_votable_suggestions", "2", "player-suggested map slots on the ballot"),
        new("g_maplist_votable_suggestions_override_mostrecent", "0"),
        new("sv_vote_gametype", "0", "run a gametype vote before the map vote"),
        new("sv_vote_gametype_timeout", "20"),
        // QC xonotic-server.cfg ships "dm tdm ca ctf" (ca was missing here).
        new("sv_vote_gametype_options", "dm tdm ca ctf"),
        new("sv_vote_gametype_reduce_time", "10", "reduce gametype options after this many seconds; 0 = disable"),
        new("sv_vote_gametype_reduce_count", "2", "gametype options kept after reduce"),
        new("sv_vote_gametype_detail", "1", "show vote counts during the gametype vote"),
        new("sv_vote_gametype_default_current", "1", "the current gametype wins a tie among 0-vote options"),
        new("sv_vote_gametype_maplist_reset", "1", "reset g_maplist when the gametype changes"),
        new("samelevel", "0", "restart the same map instead of rotating"),
        new("lastlevel", "0"),
        new("quit_when_empty", "0", "if set, the server quits at match end when only bots remain"),
        new("quit_and_redirect", "", "if set to a server address, all clients are redirected there at match end"),
        new("timelimit_increment", "5", "minutes added by extendmatchtime"),
        new("timelimit_decrement", "5", "minutes removed by reducematchtime"),
        new("timelimit_min", "5"),
        new("timelimit_max", "60"),

        // ---- demo recording (engine sv_autodemo; host-driven) ----
        new("sv_autodemo", "0", "record a server demo of the whole match"),
        new("sv_autodemo_perclient", "0", "record a per-client demo (0=off,1=players,2=all)"),
        new("sv_autodemo_perclient_discardable", "0", "per-client demos may be auto-deleted"),

        // ---- world-item pickups (T35: common/items/* + server/items/items.qc; values from balance-xonotic.cfg
        //      + _all.cfg). NOTE: the SHIPPED g_pickup_items / g_powerups default is -1 ("use gametype default"),
        //      which needs a per-gametype computation the headless port doesn't do — so we seed 1 (force-spawn)
        //      here so stock maps spawn their items + powerups out of the box. A host can set 0 / -1 over the top.
        new("g_pickup_items", "1", "force item spawning (-1 = gametype default; seeded 1 so items spawn)"),
        new("g_weapon_stay", "0", "1 = ghost weapons (no ammo), 2 = ghost weapons refill ammo"),
        new("g_items_dropped_lifetime", "20", "seconds a dropped loot item survives before despawning"),
        new("g_pickup_weapons_anyway", "0", "always pick up a weapon even if already owned"),

        // powerups: master + per-type toggles (seeded on, like _all.cfg's per-type 1s) + the balance durations.
        new("g_powerups", "1", "force powerup spawning (-1 = gametype default; seeded 1)"),
        new("g_powerups_stack", "0", "stack powerup timers on re-pickup instead of refreshing"),
        new("g_powerups_strength", "1", "allow Strength powerups to spawn"),
        new("g_powerups_shield", "1", "allow Shield powerups to spawn"),
        new("g_powerups_speed", "1", "allow Speed powerups to spawn"),
        new("g_powerups_invisibility", "1", "allow Invisibility powerups to spawn"),
        new("g_powerups_jetpack", "1", "allow Jetpacks to spawn"),
        new("g_powerups_fuelregen", "1", "allow Fuel Regenerators to spawn"),
        new("g_balance_powerup_strength_time", "30", Notify, "Strength powerup duration (s)"),
        new("g_balance_powerup_invincible_time", "30", Notify, "Shield powerup duration (s)"),
        new("g_balance_powerup_speed_time", "30", Notify, "Speed powerup duration (s)"),
        new("g_balance_powerup_invisibility_time", "30", Notify, "Invisibility powerup duration (s)"),
        new("g_balance_superweapons_time", "30", Notify, "default superweapon ammo duration (s)"),

        // health pickups (amount + per-pickup cap). balance-xonotic.cfg.
        new("g_pickup_healthsmall", "5", Notify), new("g_pickup_healthsmall_max", "200", Notify),
        new("g_pickup_healthmedium", "25", Notify), new("g_pickup_healthmedium_max", "200", Notify),
        new("g_pickup_healthbig", "50", Notify), new("g_pickup_healthbig_max", "200", Notify),
        new("g_pickup_healthmega", "100", Notify), new("g_pickup_healthmega_max", "200", Notify),

        // armor pickups (amount + per-pickup cap). balance-xonotic.cfg.
        new("g_pickup_armorsmall", "5", Notify), new("g_pickup_armorsmall_max", "200", Notify),
        new("g_pickup_armormedium", "25", Notify), new("g_pickup_armormedium_max", "200", Notify),
        new("g_pickup_armorbig", "50", Notify), new("g_pickup_armorbig_max", "200", Notify),
        new("g_pickup_armormega", "100", Notify), new("g_pickup_armormega_max", "200", Notify),

        // ammo pickups (amount + per-resource cap). balance-xonotic.cfg. (bullets cvar is g_pickup_nails.)
        new("g_pickup_shells", "15", Notify), new("g_pickup_shells_max", "60", Notify),
        new("g_pickup_nails", "80", Notify), new("g_pickup_nails_max", "320", Notify),
        new("g_pickup_rockets", "40", Notify), new("g_pickup_rockets_max", "160", Notify),
        new("g_pickup_cells", "30", Notify), new("g_pickup_cells_max", "180", Notify),
        new("g_pickup_fuel", "50", Notify), new("g_pickup_fuel_max", "100", Notify),
        new("g_pickup_fuel_jetpack", "100", Notify, "fuel a Jetpack pickup grants"),

        // respawn times (xonotic-server.cfg / balance). Per-class for health/armor; shared for ammo; long for powerups.
        new("g_pickup_respawntime_health_small", "15"), new("g_pickup_respawntime_health_medium", "15"),
        new("g_pickup_respawntime_health_big", "20"), new("g_pickup_respawntime_health_mega", "30"),
        new("g_pickup_respawntime_armor_small", "15"), new("g_pickup_respawntime_armor_medium", "20"),
        new("g_pickup_respawntime_armor_big", "30"), new("g_pickup_respawntime_armor_mega", "30"),
        new("g_pickup_respawntime_ammo", "10"),
        new("g_pickup_respawntime_powerup", "120"),
        new("g_pickup_respawntime_weapon", "10"),
        new("g_pickup_respawntime_superweapon", "120"),

        // (§12.8) DP-faithful networking entity culling (DP sv_send.c SV_MarkWriteEntityStateToClient): the
        // server stops SENDING a client the entities whose bounds fall outside that client's PVS — the bandwidth
        // + draw-call win, and the half of DP's entity culling we were missing (we only used PVS for bot LOS).
        // Box-tested per recipient (conservative: a model spanning a cluster boundary stays sent; a viewer in
        // solid / unvised map / dead-or-spectating recipient sends everything). Default 1 = DP's default.
        // See ServerNet.BuildEntitySet/RelevantEntitiesFor.
        new("sv_cullentities_pvs", "1", "PVS-cull networked entities per client (DP SV_MarkWriteEntityStateToClient)"),

        // ---- itemstime mutator (common/mutators/mutator/itemstime/itemstime.qc) ----
        // The itemstime mutator tracks respawn times for "timed" items (Mega/Big Health+Armor, Strength, Shield,
        // Superweapons) and sends them to clients so the HUD panel can draw countdowns. sv_itemstime (xonotic-
        // server.cfg:421, default 1) is the SERVER gate: 0 off / 1 spectators+warmup / 2 also alive players. It
        // gates both the HUD respawn-time table (ItemstimeMutator/ServerNet.SendItemsTime) AND the per-peer
        // visibility of the SPRITERULE_SPECTATOR respawn waypoint sprites that Item_RespawnCountdown spawns for
        // the SpectatorOnly set (Mega/Big health+armor) — see ServerNet.WaypointVisible, a faithful port of QC
        // WaypointSprite_visible_for_player (waypointsprites.qc:982). MUST be registered: it is read with a
        // default-0 fallback, so leaving it unregistered would read 0 and disable the whole mutator in production.
        //
        // NOTE: Base ALSO has a separate CLIENT draw-side cvar g_waypointsprite_itemstime (xonotic-client.cfg:518,
        // default 2) used by Draw_WaypointSprite's SPRITERULE_SPECTATOR case (waypointsprites.qc:495) to decide
        // whether a *received* spectator sprite is actually drawn. These are NOT the same cvar and do NOT share a
        // default (server 1 vs client 2). The port has not ported the client-side g_waypointsprite_itemstime gate;
        // it relies on the server-side sv_itemstime gate alone, which under stock cvars yields the same observable
        // result (the server won't network a spectator sprite to a live player in a live round unless sv_itemstime
        // ==2). Residual gap: a client overriding g_waypointsprite_itemstime to a non-default value has no effect,
        // because the client never receives the sprite's rule to re-filter on (the rule is resolved server-side).
        new("sv_itemstime", "1", "0 off / 1 spectators/warmup only / 2 also playing players (item respawn HUD + waypoint visibility)"),
    };

    /// <summary>
    /// Register every entry of <see cref="Defaults"/> into the ambient <see cref="ICvarService"/> (QC the
    /// autocvar registration done as the configs are exec'd at startup). Idempotent per-name: an already-set
    /// cvar keeps its value (so a host that set overrides before boot wins), matching
    /// <see cref="ICvarService.Register"/>'s "registration is idempotent" contract. Safe to call from
    /// <see cref="GameWorld.Boot"/> after the facade is published.
    /// </summary>
    public static void RegisterDefaults()
    {
        if (Api.Services is null)
            return;
        foreach (CvarDef d in Defaults)
            Api.Cvars.Register(d.Name, d.DefaultValue, d.Flags);

        // g_vortexversion: this build's version string, the counterpart of upstream's g_xonoticversion.
        //
        // Registered HERE, in code, rather than in a .cfg for two reasons. It has to exist BEFORE the config
        // chain runs so a cfg can interpolate `$g_vortexversion` — and MenuState.Boot calls this immediately
        // before ConfigLoader.Load, which is what makes that ordering hold. And its value comes from the
        // binary, not from a file: upstream's g_xonoticversion ships as the literal "git", a placeholder
        // their release tooling substitutes, so it says nothing about a build made from source. (That cvar
        // is also simply absent here — it is set only in xonotic-common.cfg, which the port's boot chain
        // does not exec, so `$g_xonoticversion` expanded to nothing and took the default hostname's version
        // with it.)
        Api.Cvars.Register("g_vortexversion", VortexArena.Common.BuildInfo.Version);
    }

    /// <summary>Register one extra cvar default (a host/mutator adding its own autocvar at boot).</summary>
    public static void Register(string name, string defaultValue, CvarFlags flags = CvarFlags.None)
    {
        if (Api.Services is not null)
            Api.Cvars.Register(name, defaultValue, flags);
    }

    // =============================================================================================
    // typed accessors (QC the autocvar_* globals + cvar()/cvar_string()/bound reads)
    // =============================================================================================

    /// <summary>QC <c>autocvar_NAME</c> as a float (0 if unset). Equivalent to <c>cvar(name)</c>.</summary>
    public static float Float(string name) => Api.Services is not null ? Api.Cvars.GetFloat(name) : 0f;

    /// <summary>QC <c>autocvar_NAME</c> as a string ("" if unset). Equivalent to <c>cvar_string(name)</c>.</summary>
    public static string String(string name) => Api.Services is not null ? Api.Cvars.GetString(name) : "";

    /// <summary>Does this cvar exist in the live table? (QC the engine's command-vs-cvar dispatch check —
    /// used by the console/rcon cvar-set fallback so a whitelisted cvar-style vote actually applies).</summary>
    public static bool Has(string name) => Api.Services is not null && Api.Cvars.Has(name);

    /// <summary>QC <c>cvar(name) != 0</c> — the common bool-cvar idiom.</summary>
    public static bool Bool(string name) => Float(name) != 0f;

    /// <summary>QC <c>(int)cvar(name)</c>.</summary>
    public static int Int(string name) => (int)Float(name);

    /// <summary>
    /// Read a float cvar but fall back to <paramref name="fallback"/> when the cvar is unset/empty (QC the
    /// frequent <c>x = cvar(name); if (!x) x = default;</c> pattern). Distinguishes "unset" from "set to 0".
    /// </summary>
    public static float FloatOr(string name, float fallback)
    {
        if (Api.Services is null)
            return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name);
    }

    /// <summary>QC <c>bound(lo, cvar(name), hi)</c>: a clamped float-cvar read.</summary>
    public static float Bound(string name, float lo, float hi)
    {
        float v = Float(name);
        return v < lo ? lo : (v > hi ? hi : v);
    }

    /// <summary>Set a cvar through the facade (QC <c>cvar_set</c>); honors the ReadOnly flag.</summary>
    public static void Set(string name, string value)
    {
        if (Api.Services is not null)
            Api.Cvars.Set(name, value);
    }

    /// <summary>Set a numeric cvar (QC <c>cvar_set(name, ftos(value))</c>).</summary>
    public static void Set(string name, float value)
        => Set(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    // =============================================================================================
    // a few hot, named convenience reads (QC autocvar_* the server core touches every frame)
    // =============================================================================================

    public static float Gravity => FloatOr("sv_gravity", 800f);
    public static float MaxSpeed => FloatOr("sv_maxspeed", 360f);      // stock Xonotic (physicsX.cfg)
    public static float JumpVelocity => FloatOr("sv_jumpvelocity", 260f); // stock Xonotic (physicsX.cfg)
    public static float TimeLimitMinutes => Float("timelimit");
    public static float FragLimit => Float("fraglimit");
    public static bool Teamplay => Bool("teamplay");
    public static bool WarmupStage => Bool("g_warmup");
    public static float WarmupLimit => FloatOr("g_warmup_limit", -1f);
    public static float MapChangeDelay => FloatOr("sv_mapchange_delay", 5f);
    public static int BotNumber => Int("bot_number");
    public static float Skill => FloatOr("skill", 8f); // xonotic-server.cfg:129 ships skill 8
}
