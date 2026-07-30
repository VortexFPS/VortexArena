// Port of the per-entity gametype-object state and the shared gametype-entity infrastructure.
//
// In QuakeC the gametype objectives (CTF flags, KeyHunt keys, Keepaway/Nexball balls, Domination control
// points, Race/CTS checkpoints, Onslaught generators/control-points, Assault objectives) were ordinary
// edicts carrying a pile of flat ".fields" (.ctf_status / .owner / .flagcarried, .owner key carrier,
// .goalentity owner, .race_checkpoint, .health generator, ...). Entity is `partial`, so — exactly like
// Items/EntityResources.cs and MapObjects/MapObjectsCommon.cs — we promote those fields here in a NEW file
// without editing Entity.cs, and add the shared spawn/sound/scoring shims the gametype code reuses.
//
// The deterministic simulation (XonoticGodot.Engine) runs the entities' Think/Touch the same way the QC engine
// did; this file is Godot-free and depends only on the engine-services facade (Api.*).

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Framework
{
    /// <summary>
    /// The extra edict fields the QuakeC gametype objectives stored as flat <c>.field</c>s. Promoted here
    /// onto the partial <see cref="Entity"/> (ADR-0007) so the flag/key/ball/control-point/checkpoint/
    /// generator/objective entities keep their state without a side table. Names are prefixed (Gt*) where a
    /// generic name would collide with engine/mover fields already on the entity.
    /// </summary>
    public partial class Entity
    {
        // --- shared objective ownership / carry (QC .owner is the engine Owner; carrier/home bookkeeping) ---
        /// <summary>QC objective status enum value (CTF FLAG_BASE/.., Domination state, …). Meaning is per-gametype.</summary>
        public int GtStatus;
        /// <summary>The team this objective "belongs to" at home (CTF flag home team, key home team, generator team).</summary>
        public int GtHomeTeam = Gameplay.Teams.None;
        /// <summary>The player currently carrying this objective (QC flag.owner / key.owner / ball.owner), or null.</summary>
        public Entity? GtCarrier;
        /// <summary>Back-link on a player to the objective they carry (QC .flagcarried / .ballcarried / held key).</summary>
        public Entity? GtCarried;
        /// <summary>
        /// QC <c>.m_GameRules_scoring_vip</c> (sv_rules.qh:77): set true while this player carries a scoring
        /// objective the gametype considers "VIP" — the CTF flag, the Keepaway / Nexball ball, or a TKA key
        /// (GameRules_scoring_vip(player,true) on pickup, false on drop/score). Read by the nades bonus economy
        /// (a flag carrier accrues bonus-nade score at the flagcarrier rate). KeyHunt deliberately does NOT set
        /// this (it uses the per-player key count instead). Maintained centrally in
        /// <see cref="Gameplay.GametypeEntities.AttachToCarrier"/>/<see cref="Gameplay.GametypeEntities.DetachFromCarrier"/>.
        /// </summary>
        public bool GtScoringVip;

        // --- timing bookkeeping (QC .ctf_pickuptime / .ctf_droptime / .ctf_landtime / next_take_time) ---
        public float GtPickupTime;     // QC .ctf_pickuptime — when the objective was last taken (capture-time records)
        public float GtDropTime;       // QC .ctf_droptime — when it was dropped (auto-return timer)
        public float GtLandTime;       // QC .ctf_landtime — when a dropped objective came to rest
        public float GtNextTakeTime;   // QC .next_take_time — earliest a player may take again after losing it
        public float GtTouchCooldown;  // QC flag.wait — next time the world-touch sfx may play
        public Vector3 GtSpawnOrigin;  // QC .dropped_origin / ctf_spawnorigin — home base position to respawn to
        public Vector3 GtSpawnAngles;  // QC .mangle — home base angles

        // --- capture shield (CTF anti-camp) ---
        public bool GtCaptureShielded; // QC .ctf_captureshielded — player too far behind to be allowed to capture
        public Entity? GtShieldFlag;   // QC ctf_captureshield .enemy — the flag a shield entity guards

        // --- CTF flagcarrier auto-helpme (QC .wps_helpme_time, sv_ctf.qc Damage_Calculate) ---
        /// <summary>QC .wps_helpme_time — last time a low-health flag carrier auto-pinged help (antispam baseline).</summary>
        public float GtHelpMeTime;
        /// <summary>Sim time until which a flag carrier's "help me" waypoint flash stays lit (QC WaypointSprite_HelpMePing).</summary>
        public float GtHelpMeUntil;

        // --- CTF flag passing / throwing antispam + punish (QC player .throw_antispam/.throw_count/.throw_prevtime) ---
        public float GtThrowAntispam;  // QC .throw_antispam — earliest time the player may pass/throw again
        public int GtThrowCount;       // QC .throw_count — recent throw count for the punish ramp (-1 = on cooldown)
        public float GtThrowPrevTime;  // QC .throw_prevtime — when the throw-punish window last advanced

        // --- Domination control point (QC dom_controlpoint: .goalentity owner team, .enemy capturer, .delay tick) ---
        public int GtPointTeam = Gameplay.Teams.None; // owning team of a control point (QC goalentity.team)
        public Entity? GtCapturer;     // QC point.enemy — player who last captured the point (tick credit)
        public float GtNextTick;       // QC point.delay — absolute time of the next score tick
        public float GtPointAmt;       // QC .frags — per-tick score amount override (per-point)
        public float GtPointRate;      // QC .wait — per-point tick interval override
        public int GtPointId;          // stable id used by Onslaught control-point ownership maps

        // --- Race / CTS checkpoint (QC trigger_race_checkpoint: .race_checkpoint index, .race_place finish) ---
        public int GtCheckpointIndex;  // QC .race_checkpoint — this checkpoint's ordinal (0 = finish line)
        public bool GtIsFinishLine;    // QC the start/finish checkpoint (.race_place / the 0 checkpoint)
        public bool GtIsStartTimer;    // QC target_startTimer — crossing starts a CTS run
        public bool GtIsStopTimer;     // QC target_stopTimer — crossing finishes a CTS run

        // --- Onslaught generator / Assault objective (QC .health on the objective edict; max for waypoints) ---
        public float GtObjHealth;      // QC objective/generator RES_HEALTH (kept distinct from player Health)
        public float GtObjMaxHealth;   // QC objective/generator max_health
        public bool GtObjActive;       // QC objective is the currently-attackable one (Assault chain head)

        // --- generic objective event_damage (QC .event_damage on a non-player edict) ---
        // QC let any edict carry an event_damage callback (PlayerDamage, ons_GeneratorDamage,
        // ons_ControlPoint_Icon_Damage, …). Players are dispatched by the DamageSystem's own
        // PlayerDamage/PlayerCorpseDamage; this delegate lets a NON-player objective (Onslaught generator /
        // control-point icon) receive damage through the same Combat.Damage pipeline. Signature mirrors QC
        // event_damage(this, inflictor, attacker, damage, deathtype, hitloc, force) minus the .weaponentity arg.
        public System.Action<Entity, Entity?, Entity?, string, float, Vector3, Vector3>? GtEventDamage;

        // --- generic objective event_heal (QC .event_heal on a non-player edict) ---
        // QC's Heal(targ, inflictor, amount, limit) dispatches to targ.event_heal when set (server/damage.qc:956);
        // an Onslaught generator/control-point icon sets it to ons_GeneratorHeal / ons_ControlPoint_Icon_Heal so a
        // friendly Arc heal-beam / mage / bumblebee healgun tops up the OBJECTIVE health (GtObjHealth) rather than
        // the player Health resource. Returns true if any health was added. Signature mirrors QC event_heal.
        public System.Func<Entity, Entity?, float, float, bool>? GtEventHeal;

        // --- Onslaught control-point icon build state (QC the icon edict ons_ControlPoint_Icon_*) ---
        // The buildable icon spawned when a player touches an attackable control point: it ramps RES_HEALTH up
        // at GtBuildRate per think tick until GtObjMaxHealth, at which point the point flips. It can be damaged
        // mid-build (GtEventDamage). These are kept on the icon entity; the owning control-point id ties it back
        // to the Onslaught power graph.
        public float GtBuildRate;      // QC icon .count — RES_HEALTH gained per think while building/regenerating
        public float GtPainFinished;   // QC icon .pain_finished — debounce for under-attack notify + regen pause
        public bool GtIconBuilt;       // QC icon: false while building, true once it finished (point captured)
        public int GtIconCpId;         // the control-point id this icon builds (ties to Onslaught._cpNodes)

        // --- Onslaught generator un-shielded alarm (QC ons_GeneratorThink .wait) ---
        // The next sim time the recurring "your/the enemy generator is NOT shielded!" warning may fire again
        // (QC sets this.wait = time + 5 each time the alarm sounds). Kept on the generator entity; separate
        // from GtPainFinished (which the generator reuses for the 10 s under-attack debounce).
        public float GtAlarmWait;      // QC generator .wait — 5 s un-shielded alarm repeat timer

        // --- Onslaught spawn placement + teleport (QC player .ons_deathloc / .ons_spawn_by / .teleport_antispam) ---
        /// <summary>QC player <c>.ons_deathloc</c> — where the player last died, used to pick the nearest owned
        /// control point/generator to respawn near (spawn_at_controlpoints / spawn_at_generator). '0 0 0' = a fresh
        /// joiner / round reset (skip the nearest-node placement).</summary>
        public Vector3 GtOnsDeathLoc;
        /// <summary>QC player <c>.ons_spawn_by</c> — the control-point world entity the player chose from the
        /// click-radar to respawn at (ons_Teleport target on the next PlayerSpawn). Null = use the default spawn.</summary>
        public Entity? GtOnsSpawnBy;
        /// <summary>QC player <c>.teleport_antispam</c> — earliest sim time the player may use the CP-to-CP teleport
        /// (use key) again (g_onslaught_teleport_wait after the last teleport).</summary>
        public float GtTeleportAntispam;
        /// <summary>QC bot <c>.havocbot_ons_target</c> (sv_onslaught.qc:1340) — the control-point node this bot is
        /// currently most interested in attacking. The Onslaught offense role uses it for teammate-interest cost
        /// balancing (bots spread across attackable points). Null = no current control-point target.</summary>
        public Onslaught.OnsNode? GtOnsTarget;

        // --- Nexball basketball safe-pass lock (QC ball.enemy) ---
        /// <summary>QC <c>ball.enemy</c> — the teammate locked as a safe-pass target (set by the PreThink crosshair
        /// trace in nexball). The BallStealer secondary fires the ball toward this entity via the homing arc path.
        /// Null when no safe-pass lock is active.</summary>
        public Entity? GtSafePassTarget;
        /// <summary>QC <c>ball.wait</c> in the nexball PlayerPreThink safe-pass lock: the absolute sim time the
        /// current safe-pass lock (<see cref="GtSafePassTarget"/>) expires. When <c>wait &lt; time</c> the lock is
        /// cleared. Refreshed to <c>time + g_nexball_safepass_holdtime</c> each frame the crosshair stays on the
        /// teammate.</summary>
        public float GtSafePassExpire;

        // --- Nexball weapon-arena saved loadout (QC STAT(WEAPONS, player.(weaponentity))) ---
        /// <summary>QC <c>STAT(WEAPONS, player.(weaponentity))</c> in GiveBall/PlayerPreThink: the player's normal
        /// weapon set saved when the ball is picked up (the weapon-arena WEP_NEXBALL override replaces it); restored
        /// when the player stops carrying.</summary>
        public WepSet GtSavedWeaponSet;
        /// <summary>QC the weapon the player had active before picking up the ball (restored on lose-carrier in
        /// PlayerPreThink non-carrier branch).</summary>
        public int GtSavedActiveWeaponId = -1;

        // --- Invasion / monster-wave bookkeeping (QC spawned-by-wave marker) ---
        public bool GtWaveMonster;     // a monster that was spawned as part of an Invasion/Survival wave

        // --- scoreboard stats kept on the player edict (QC GameRules_scoring_add fields) ---
        public int GtPointTakes;       // QC DOM_TAKES — control points this player captured
        public int GtDeaths;           // QC SP_DEATHS — times this player died (incl. suicides)
        public int GtSuicides;         // QC SP_SUICIDES — times this player died with no other attacker
        public int GtKillCount;        // QC .killcount — consecutive frags without dying (spree)

        // --- Mayhem / Team Mayhem damage-score accrual (QC <c>.total_damage_dealt</c>, sv_mayhem.qc:31) ---
        // The running total of "useful" damage this player has dealt (frag_damage minus overkill excess, summed in
        // the PlayerDamage_SplitHealthArmor hook). Divided by the player's spawn health+armor in
        // MayhemCalculatePlayerScore to turn damage into score. Decremented for self/teammate/environmental
        // suicide damage. Zeroed on round/map reset (QC mayhem reset_map_players). Per-player; only Mayhem/TMayhem
        // touch it, so it stays 0 in every other gametype.
        public float GtTotalDamageDealt; // QC .total_damage_dealt
    }
}

namespace XonoticGodot.Common.Gameplay
{
    /// <summary>
    /// Shared helpers for the gametype-objective entities (CTF flags, KH keys, KA/NB balls, Domination
    /// points, Race checkpoints, Onslaught generators, Assault objectives). The Godot-free essence of the
    /// scaffolding every gametype's <c>sv_*.qc</c> reused: spawn a world objective entity, attach/detach it
    /// to a carrier, place it through the facade, play a sound, read time/cvars, and the
    /// <c>GameRules_scoring_add*</c> team/player score helpers.
    ///
    /// Deferred vs QC (NOTE — genuinely client/networking): waypoint sprites, CSQC objective networking,
    /// effects/particles, and the model/animation assets — these are presentation concerns that live in the
    /// Godot host, not the headless simulation.
    /// </summary>
    public static class GametypeEntities
    {
        /// <summary>Current sim time (QC <c>time</c>); 0 when no facade is wired (headless tests).</summary>
        public static float Now => Api.Services is null ? 0f : Api.Clock.Time;

        /// <summary>Frame delta (QC <c>frametime</c>); a 125fps default with no facade.</summary>
        public static float FrameTime => Api.Services is null ? 1f / 125f : Api.Clock.FrameTime;

        /// <summary>
        /// Read a float cvar IF it is actually set (non-empty string), so an explicit 0 is distinguishable
        /// from "unset" — matches the gametype files' own TryCvar idiom. Returns false (value 0) when unset
        /// or no services are wired.
        /// </summary>
        public static bool TryCvar(string name, out float value)
        {
            value = 0f;
            if (Api.Services is null)
                return false;
            string s = Api.Cvars.GetString(name);
            if (string.IsNullOrEmpty(s))
                return false;
            value = Api.Cvars.GetFloat(name);
            return true;
        }

        /// <summary>Read a float cvar, falling back to <paramref name="fallback"/> when unset/no facade.</summary>
        public static float Cvar(string name, float fallback) => TryCvar(name, out float v) ? v : fallback;

        /// <summary>
        /// QC <c>calculate_respawntime</c> (server/client.qc): the respawn delay scaled by how many players
        /// share the dying player's situation. With <paramref name="pcount"/> at or below
        /// respawn_delay_small_count the small delay applies; at or above respawn_delay_large_count the large
        /// delay; otherwise a linear interpolation. Defaults: small=large=2s (so it is exactly 2s at stock
        /// settings regardless of player count). <paramref name="pcount"/> includes the dying player.
        /// </summary>
        public static float RespawnDelay(int pcount = 1, bool independent = false)
        {
            float small = Cvar("g_respawn_delay_small", 2f);
            float large = Cvar("g_respawn_delay_large", 2f);
            float smallCount = Cvar("g_respawn_delay_small_count", 0f);
            float largeCount = Cvar("g_respawn_delay_large_count", 0f);
            // QC default counts: 1 for independent play, else 2 (need an enemy present for the short delay).
            if (smallCount == 0f) smallCount = independent ? 1f : 2f;
            if (largeCount == 0f) largeCount = independent ? 1f : 2f;

            if (pcount <= smallCount) return small;
            if (pcount >= largeCount) return large;
            return small + (large - small) * (pcount - smallCount) / (largeCount - smallCount);
        }

        /// <summary>Schedule a player's respawn (QC respawn_time = time + calculate_respawntime).</summary>
        public static void ScheduleRespawn(Player victim, int pcount = 1, bool independent = false)
            => victim.RespawnTime = Now + RespawnDelay(pcount, independent);

        /// <summary>Play a sound through the facade when both a sample and the facade are present (QC _sound guard).</summary>
        public static void Sound(Entity e, SoundChannel ch, string? sample)
        {
            if (Api.Services is not null && !string.IsNullOrEmpty(sample))
                Api.Sound.Play(e, ch, sample);
        }

        /// <summary>setorigin through the facade when present, else a plain assignment (headless tests).</summary>
        public static void SetOrigin(Entity e, Vector3 origin)
        {
            if (Api.Services is not null)
                Api.Entities.SetOrigin(e, origin);
            else
                e.Origin = origin;
        }

        /// <summary>setsize through the facade when present, else a plain assignment (headless tests).</summary>
        public static void SetSize(Entity e, Vector3 mins, Vector3 maxs)
        {
            if (Api.Services is not null)
                Api.Entities.SetSize(e, mins, maxs);
            else { e.Mins = mins; e.Maxs = maxs; e.Size = maxs - mins; }
        }

        /// <summary>
        /// Attach a held objective to a carrier (QC setattachment + carry offset): mark non-solid, stop its
        /// motion, set the carrier back-links, and place it on the carrier. The visual attachment tag is a
        /// client concern; we just track ownership and position so the simulation stays consistent.
        /// </summary>
        public static void AttachToCarrier(Entity obj, Entity carrier, Vector3 carryOffset)
        {
            obj.Solid = Solid.Not;            // QC SOLID_NOT before setorigin to avoid area-grid relinking
            obj.MoveType = MoveType.None;     // QC MOVETYPE_NONE — rides the carrier
            obj.TakeDamage = DamageMode.No;
            obj.Velocity = Vector3.Zero;
            obj.AVelocity = Vector3.Zero;     // a carried objective (key/flag) stops spinning while held
            obj.Angles = Vector3.Zero;
            obj.GtCarrier = carrier;
            carrier.GtCarried = obj;
            SetOrigin(obj, carrier.Origin + carryOffset);
        }

        /// <summary>
        /// QC <c>GameRules_scoring_vip(player, value)</c> (sv_rules.qh:78): mark/unmark a player as carrying a
        /// "VIP" scoring objective (CTF flag / Keepaway-Nexball ball / TKA key). Set true on pickup, false on
        /// drop/score. Read by the nades bonus economy (the flagcarrier accrual rate + the VIP-kill bonus) and
        /// any future VIP-aware scoring. KeyHunt does NOT call this (it uses the key count); see
        /// <see cref="ScoringIsVip"/>.
        /// </summary>
        public static void ScoringVip(Entity? player, bool value)
        {
            if (player is not null)
                player.GtScoringVip = value;
        }

        /// <summary>QC <c>GameRules_scoring_is_vip(player)</c> (sv_rules.qh:79): is this player a VIP carrier?</summary>
        public static bool ScoringIsVip(Entity? player) => player is not null && player.GtScoringVip;

        /// <summary>
        /// QC <c>FOR_EACH_KH_KEY(key.owner == player)</c> (sv_nades.qc:769): how many Key Hunt keys this player
        /// currently carries. Counts the live <c>item_kh_key</c> objective entities whose carrier is the player.
        /// 0 in every non-KeyHunt gametype (no such entities exist). Used by the nades bonus economy to scale the
        /// flagcarrier accrual rate by the held key count.
        /// </summary>
        public static int KeyHuntKeyCount(Entity? player)
        {
            if (player is null || Api.Services is null)
                return 0;
            int n = 0;
            foreach (Entity key in Api.Entities.FindByClass("item_kh_key"))
                if (ReferenceEquals(key.GtCarrier, player))
                    ++n;
            return n;
        }

        /// <summary>
        /// Detach a held objective from its carrier (QC clear flagcarried + setattachment NULL) and clear the
        /// carry back-links. Leaves the objective wherever the caller places it next.
        /// </summary>
        public static void DetachFromCarrier(Entity obj)
        {
            Entity? carrier = obj.GtCarrier;
            if (carrier is not null && ReferenceEquals(carrier.GtCarried, obj))
                carrier.GtCarried = null;
            obj.GtCarrier = null;
        }

        /// <summary>
        /// Spawn a world objective entity through the facade (QC spawn() + classname + bbox + solid trigger).
        /// Returns the new entity already placed at <paramref name="origin"/>, or null when no facade is wired
        /// (headless tests that don't exercise the entity layer fall back to the gametype's plain state).
        /// </summary>
        public static Entity? SpawnObjective(string className, Vector3 origin, int team,
            Vector3 mins, Vector3 maxs, EntityTouch? touch = null, EntityThink? think = null)
        {
            if (Api.Services is null)
                return null;
            Entity e = Api.Entities.Spawn();
            e.ClassName = className;
            e.Flags = EntFlags.Item | EntFlags.NoTarget; // QC FL_ITEM | FL_NOTARGET on objectives
            e.Team = team;
            e.GtHomeTeam = team;
            e.Solid = Solid.Trigger;
            e.MoveType = MoveType.None;
            e.TakeDamage = DamageMode.No;
            SetSize(e, mins, maxs);
            SetOrigin(e, origin);
            e.GtSpawnOrigin = origin;
            e.GtSpawnAngles = e.Angles;
            e.Touch = touch;
            e.Think = think;
            return e;
        }

        // ====================================================================
        //  Scoring shims — GameRules_scoring_add / GameRules_scoring_add_team
        // ====================================================================

        /// <summary>
        /// QC <c>GameRules_scoring_add_team(player, SCORE, n)</c> reduced to the headless model: add to the
        /// team's running total kept by the gametype. The caller passes its own team-score dictionary because
        /// each gametype owns its score table (TeamScores / TeamCaps / TeamRounds / TeamGoals).
        /// </summary>
        public static void AddTeamScore(Dictionary<int, int> table, int team, int delta)
        {
            if (team == Gameplay.Teams.None || delta == 0)
                return;
            table[team] = (table.TryGetValue(team, out int cur) ? cur : 0) + delta;
        }
    }
}
