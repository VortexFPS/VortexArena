using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using XonoticGodot.Common.Gameplay.Damage;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Shared monster AI — the Godot-free core of <c>common/monsters/sv_monsters.qc</c>. This is the
/// successor to the procedural <c>Monster_Think</c> / <c>Monster_Move</c> / <c>Monster_Attack_Check</c>
/// driver and the <c>Monster_FindTarget</c> / <c>Monster_Attack_Melee</c> helpers, factored into one place
/// so every concrete <see cref="Monster"/> shares one faithful chase/attack loop.
///
/// This file is the task's required shared helper (subsystem-prefixed <c>MonsterAI</c>). It deliberately
/// does NOT modify <see cref="Entity"/>: QuakeC stored a large flat set of monster fields on the edict
/// (<c>.speed2</c>, <c>.stopspeed</c>, <c>.attack_range</c>, <c>.target_range</c>, <c>.state</c>,
/// <c>.last_enemycheck</c>, <c>.spawn_time</c>, the per-slot <c>.attack_finished_single[]</c>, …). Here
/// those promote onto a per-entity <see cref="MonsterState"/> component looked up via
/// <see cref="StateOf"/>, keeping the engine <see cref="Entity"/> untouched (ADR-0007, entity-model.md).
/// </summary>
public static class MonsterAI
{
    // ====================================================================================
    // Per-entity monster state (the QC monster .fields that don't live on the base edict)
    // ====================================================================================

    /// <summary>
    /// Gameplay state a spawned monster entity carries — the monster-specific edict fields from
    /// <c>sv_monsters.qh</c>. One instance per live monster entity, created by <see cref="Setup"/>.
    /// </summary>
    public sealed class MonsterState
    {
        /// <summary>The descriptor that spawned this entity (QC .monsterdef).</summary>
        public Monster Def = null!;

        // movement speeds (QC .speed / .speed2 / .stopspeed)
        public float WalkSpeed;   // .speed
        public float RunSpeed;    // .speed2
        public float StopSpeed;   // .stopspeed

        // attack/targeting ranges
        public float AttackRange; // .attack_range — melee if closer, ranged if farther
        public float TargetRange; // .target_range — max distance an enemy may be acquired/kept

        /// <summary>Current attack/move state (QC .state — MONSTER_ATTACK_MELEE / MONSTER_ATTACK_RANGED, or 0).</summary>
        public int State;

        /// <summary>Next time a fresh attack is allowed (QC .attack_finished_single[0]).</summary>
        public float AttackFinished;

        /// <summary>Next time we re-scan for an enemy (QC .last_enemycheck).</summary>
        public float LastEnemyCheck;

        /// <summary>When the spawn animation finishes and the monster may start thinking (QC .spawn_time).</summary>
        public float SpawnTime;

        /// <summary>Skill level 1..10 (QC .monster_skill); drives <see cref="SkillMod"/>.</summary>
        public int Skill = MonsterSkill.Easy;

        /// <summary>Knockback this monster applies to itself from damage (QC .damageforcescale).</summary>
        public float DamageForceScale = 0.8f;

        /// <summary>Max health captured at spawn (QC .max_health), for healing caps and respawn.</summary>
        public float MaxHealth;

        /// <summary>Per-monster scratch attack cooldown (e.g. golem.golem_lastattack, spider.spider_web_delay).</summary>
        public float AttackDelay;

        // --- movement state machine (QC .monster_moveflags / .monster_movestate / .moveto / .last_trace) ---

        /// <summary>How the monster moves when it has no enemy (QC .monster_moveflags). Default wander.</summary>
        public int MoveFlags = MonsterMove_Wander;

        /// <summary>Current resolved move state for this frame (QC .monster_movestate).</summary>
        public int MoveState;

        /// <summary>Destination the monster is steering toward (QC .moveto).</summary>
        public Vector3 MoveTo;

        /// <summary>Optional explicit override destination (QC .monster_moveto; Vector3.Zero = unset).</summary>
        public Vector3 MonsterMoveTo;

        /// <summary>Next time we may re-pick a wander/move target (QC .last_trace).</summary>
        public float LastTrace;

        /// <summary>Entity this monster follows in follow-move mode (QC .monster_follow).</summary>
        public Entity? Follow;

        /// <summary>Wander cadence + radius (QC .wander_delay / .wander_distance).</summary>
        public float WanderDelay = 2f;
        public float WanderDistance = 600f;

        /// <summary>Spawn location, restored to in spawnloc move mode and on reset/respawn (QC .pos1 / .pos2).</summary>
        public Vector3 SpawnOrigin;
        public Vector3 SpawnAngles;

        // --- timing / animation phase (QC .anim_finished / .pain_finished + the logical anim state) ---

        /// <summary>When the current pain animation ends; gates re-pain + walk anims (QC .pain_finished).</summary>
        public float PainFinished;

        /// <summary>When the current scripted animation ends (QC .anim_finished).</summary>
        public float AnimFinished;

        /// <summary>Earliest time the next throttled voice cue may play (QC .msound_delay): pain 1.2s, idle 7s.</summary>
        public float MSoundDelay;

        /// <summary>Logical animation phase driving timing (see <see cref="MonsterAnim"/>). Client playback is CSQC.</summary>
        public MonsterAnim Anim = MonsterAnim.Idle;

        /// <summary>
        /// The corpse has landed (QC <c>mr_deadthink</c> IS_ONGROUND): selects the landed-corpse death frame
        /// group (die2) over the falling one (die1) for monsters that split their death anim. Set by the
        /// descriptor's dead-think (e.g. the wyvern) and read by <c>DriveAnimFrame</c>; reset on respawn.
        /// </summary>
        public bool DeathLanded;

        /// <summary>
        /// Per-phase animation VARIANT index (QC <c>setanim(random &gt;= 0.5 ? anim_pain1 : anim_pain2)</c> and the
        /// golem's <c>anim_melee2/anim_melee3</c> random swing pick). Chosen once when a phase is (re)entered and
        /// read by <c>DriveAnimFrame</c> → the descriptor's <c>AnimFrame(phase, die2, variant)</c> so a monster with
        /// two interchangeable pain/attack groups alternates between them instead of always stamping the first.
        /// Defaults to 0 (the canonical group) for descriptors that don't use variants.
        /// </summary>
        public int AnimVariant;

        // --- lifecycle: death / respawn / loot / miniboss (QC .monster_lifetime / .candrop / spawnflags) ---

        /// <summary>Hard kill time once set (QC .monster_lifetime); 0 = none. Set to time+5 on death (corpse fade).</summary>
        public float Lifetime;

        /// <summary>Respawn delay after death (QC .respawntime). Default = g_monsters_respawn_delay (monsters.cfg = 20).</summary>
        public float RespawnTime = 20f;

        /// <summary>This monster drops loot on death (QC .candrop).</summary>
        public bool CanDrop = true;

        /// <summary>Loot item-list string (QC .monster_loot).</summary>
        public string MonsterLoot = "";

        /// <summary>Monster is a miniboss (QC MONSTERFLAG_MINIBOSS): tougher + miniboss loot.</summary>
        public bool IsMiniboss;

        /// <summary>This monster always respawns, overriding NORESPAWN (QC zombie mr_setup).</summary>
        public bool AlwaysRespawn;

        /// <summary>Respawn at the point of death rather than the spawn point (QC MONSTER_RESPAWN_DEATHPOINT).</summary>
        public bool RespawnAtDeathPoint;

        /// <summary>This monster never respawns (QC MONSTERFLAG_NORESPAWN).</summary>
        public bool NoRespawn;

        /// <summary>Spawned via a command / wave rather than placed in the map (QC MONSTERFLAG_SPAWNED).</summary>
        public bool Spawned;

        /// <summary>Has gone through a death→respawn cycle at least once (QC MONSTERFLAG_RESPAWNED).</summary>
        public bool Respawned;

        // --- combo / per-monster scratch (golem swing combo, spike-in-flight guard, etc.) ---

        /// <summary>Remaining queued melee swings in a combo (QC golem Monster_Delay swing count).</summary>
        public int ComboSwings;

        /// <summary>When the next queued combo swing lands (QC Monster_Delay nextthink cadence).</summary>
        public float NextComboSwing;

        /// <summary>Damage per queued combo swing.</summary>
        public float ComboSwingDamage;

        /// <summary>Deathtype each queued combo swing carries (QC golem claw = DEATH_MONSTER_GOLEM_CLAW).</summary>
        public string ComboDeathType = DeathTypes.Generic;

        /// <summary>Per-swing melee animtime for a combo swing (QC M_Golem_Attack_Swing passes 0.8 to
        /// Monster_Attack_Melee — the per-swing attack_finished window). Cadence (NextComboSwing) is separate.</summary>
        public float ComboSwingAnimTime = 0.5f;

        /// <summary>A deferred attack action that lands after a wind-up (QC Monster_Delay).</summary>
        public Action<Entity>? DelayedAction;

        /// <summary>When the deferred action fires (QC Monster_Delay nextthink).</summary>
        public float DelayedActionTime;

        /// <summary>The single in-flight homing spike, so only one exists at a time (QC .mage_spike).</summary>
        public Entity? ActiveSpike;

        /// <summary>When the mage may re-cast its shield (QC .mage_shield_delay).</summary>
        public float ShieldDelay;

        /// <summary>When a mage shield expires and armor must be restored (mirrors STATUSEFFECT_Shield expiry).</summary>
        public float ShieldExpire;

        /// <summary>Armor value to restore when the shield expires.</summary>
        public float ShieldRestoreArmor;

        /// <summary>QC <c>.sprite</c> — the floating WP_Monster health-bar waypoint sprite (spawned when
        /// <c>g_monsters_healthbars</c>; updated on each hit, killed on death). Null when healthbars are off.</summary>
        public Waypoints.WaypointSprite? Sprite;
    }

    /// <summary>
    /// Logical animation phase a monster is in (QC's per-frame Monster_Anim selection, reduced to the
    /// states that drive server-side TIMING: idle/walk/run while moving, attack while firing, pain on hit,
    /// death once dead). The actual frame playback is client-render (CSQC) — see the deferred note in
    /// <see cref="RunThink"/>. Kept as a logical state so timing/gating is faithful headless.
    /// </summary>
    public enum MonsterAnim { Idle, Walk, Run, Attack, Pain, Death, Spawn, Block, BlockEnd, Shoot }

    // QC stored monster state on the edict; we keep a side table keyed by the entity instance.
    private static readonly Dictionary<Entity, MonsterState> _states = new();

    /// <summary>Fetch the monster state for an entity, or null if it isn't an initialized monster.</summary>
    public static MonsterState? StateOf(Entity e) => _states.TryGetValue(e, out var s) ? s : null;

    /// <summary>Forget an entity's monster state (call on removal). Mirrors edict deletion in QC.</summary>
    public static void Forget(Entity e) => _states.Remove(e);

    // ====================================================================================
    // Spawn / setup (Monster_Spawn + Monster_Spawn_Setup, sv_monsters.qc)
    // ====================================================================================

    /// <summary>
    /// The shared portion of <c>Monster_Spawn</c> (sv_monsters.qc): stamp the engine fields every monster
    /// needs (FL_MONSTER, SOLID_BBOX, MOVETYPE_STEP, takedamage), seed default health/speeds/ranges, and
    /// create the <see cref="MonsterState"/>. Concrete monsters call this from their
    /// <see cref="Monster.Spawn"/> override, then apply their own size/model/specifics.
    /// </summary>
    public static MonsterState Setup(Monster def, Entity e)
    {
        var st = new MonsterState { Def = def };
        _states[e] = st;

        e.ClassName = "monster";
        e.NetName = def.NetName;
        e.Flags |= EntFlags.Monster;
        e.Solid = Solid.BBox;
        e.MoveType = MoveType.Step;

        // QC Monster_Spawn:1479 — dphitcontentsmask = SOLID|BODY|BOTCLIP|MONSTERCLIP (+ PLAYERCLIP when
        // g_monsters_playerclip_collisions, :1488). A monster is SOLID_BBOX, so without this the trace mask falls
        // through to the generic SOLID|BODY|CORPSE default (TraceService.GenericHitMask) — the monster would ignore
        // monster-clip/bot-clip AI brushes and wrongly clip corpses. Stamp the explicit Base mask here (the Invasion
        // gametype already stamps the same SOLID|BODY|BOTCLIP|MONSTERCLIP value; this makes EVERY monster faithful).
        e.DpHitContentsMask = SuperContentsSolid | SuperContentsBody | SuperContentsBotClip | SuperContentsMonsterClip;
        if (Cvar("g_monsters_playerclip_collisions", 1f) != 0f) // monsters.cfg: g_monsters_playerclip_collisions 1
            e.DpHitContentsMask |= SuperContentsPlayerClip;
        e.TakeDamage = DamageMode.Aim;
        e.DeadState = DeadFlag.No;
        e.Gravity = 1f;
        e.Enemy = null;
        e.Velocity = Vector3.Zero;

        if (!string.IsNullOrEmpty(def.Model))
            e.Model = def.Model!;

        // Carry the spawnflags the host stamped onto the edict into the state machine.
        st.Spawned = (e.SpawnFlags & MonsterFlag_Spawned) != 0;
        st.Respawned = (e.SpawnFlags & MonsterFlag_Respawned) != 0;
        st.NoRespawn = (e.SpawnFlags & MonsterFlag_NoRespawn) != 0;

        // Health: descriptor StartHealth seeds it unless the entity was pre-set (QC mr_setup pattern).
        if (e.Health <= 0f)
            e.Health = def.StartHealth;

        // Skill (QC: if (!this.monster_skill) this.monster_skill = cvar("g_monsters_skill")). A per-entity
        // .monster_skill set on the edict (e.g. a monster_spawner stamping its child) overrides the cvar.
        st.Skill = e.MonsterSkill > 0
            ? (int)System.Math.Clamp((float)e.MonsterSkill, 1f, 10f)
            : ResolveSkill();

        // Default armor (QC Monster_Spawn_Setup:1327-1328): if(!RES_ARMOR) SetResourceExplicit(RES_ARMOR,
        // bound(0.2, 0.5 * MONSTER_SKILLMOD(this), 0.9)). The monster's armor is a small FRACTION (the armor
        // BLOCK is ArmorValue/100 in healtharmor_applydamage — see MonsterEventDamage), so without this a monster
        // spawns with ArmorValue 0 and takes FULL damage. Seeded only when unset, mirroring QC's "ensure some
        // basic needs are met" defaulting (a descriptor may still override afterward, e.g. the zombie's block).
        if (e.ArmorValue <= 0f)
            e.ArmorValue = QMath.Bound(0.2f, 0.5f * SkillMod(st), 0.9f);

        // Miniboss: spawnflag or random chance (QC Monster_Miniboss_Setup). Boosts health + flags loot.
        if (!st.Respawned)
        {
            // QC Monster_Miniboss_Setup:531 — a mutator may veto miniboss status, which clears MONSTERFLAG_MINIBOSS
            // (so other code doesn't identify the monster as a miniboss) and skips the boost. No stock mutator uses
            // this hook, but the chain is wired for parity (FireMonsterCheckBossFlag).
            if (MutatorHooks.FireMonsterCheckBossFlag(e))
            {
                e.SpawnFlags &= ~MonsterFlag_Miniboss;
            }
            else
            {
                bool flagged = (e.SpawnFlags & MonsterFlag_Miniboss) != 0;
                if (flagged || Prandom.Float() * 100f < Cvar("g_monsters_miniboss_chance", 0f))
                {
                    e.Health += Cvar("g_monsters_miniboss_healthboost", 100f);
                    st.IsMiniboss = true;
                    e.SpawnFlags |= MonsterFlag_Miniboss; // QC:543 — identifier for other code
                    e.Effects |= EfRed; // QC Monster_Miniboss_Setup:523 — the red glow that marks a miniboss
                }
            }
        }

        // Debug/cheat presentation flags (QC Monster_Spawn:1484-1485): g_fullbrightplayers -> EF_FULLBRIGHT,
        // g_nodepthtestplayers -> EF_NODEPTHTEST (draw-through-walls). Both default 0 (off). Applied on every
        // spawn (not respawn-gated in QC). The g_monsters_edit -> grab=1 cheat (QC:1483) is NOT ported: the
        // port's grab/drag system is the sandbox SandboxObject path, with no .grab field on a plain Entity.
        if (Cvar("g_fullbrightplayers", 0f) != 0f)
            e.Effects |= EfFullbright;
        if (Cvar("g_nodepthtestplayers", 0f) != 0f)
            e.Effects |= EfNodepthtest;

        // Skill scaling (QC Monster_Spawn_Setup: health *= MONSTER_SKILLMOD on first/non-respawn spawn).
        if (!st.Respawned)
        {
            e.Health *= SkillMod(st);
            // Random skin 0..4 when unset (QC: skin = rint(random()*4)) — drives the mage's heal variant.
            if (e.Skin == 0f)
                e.Skin = MathF.Round(Prandom.Float() * 4f);
        }
        st.MaxHealth = e.Health;
        e.MaxHealth = e.Health;

        // Speed defaults: the descriptor's single Speed is the run speed; walk is ~half (QC sets all three
        // from per-monster cvars, mirrored here so monsters that don't override still move sensibly).
        st.RunSpeed = def.Speed > 0f ? def.Speed : 300f;
        st.WalkSpeed = st.RunSpeed * 0.5f;
        st.StopSpeed = 100f;

        st.TargetRange = Cvar("g_monsters_target_range", 2000f);
        st.AttackRange = Cvar("g_monsters_attack_range", 120f);
        st.DamageForceScale = Cvar("g_monsters_damageforcescale", 0.8f);
        st.RespawnTime = Cvar("g_monsters_respawn_delay", 20f); // monsters.cfg: g_monsters_respawn_delay 20

        st.SpawnTime = Now;
        st.LastEnemyCheck = st.SpawnTime + Prandom.Float(); // slight stagger (QC)
        st.PainFinished = Now;
        st.State = 0;
        st.AttackFinished = 0f;

        // Remember where we spawned (QC .pos1/.pos2) for spawnloc movement, reset and respawn.
        st.SpawnOrigin = e.Origin;
        st.SpawnAngles = e.Angles;
        st.MoveTo = e.Origin;

        // Brief post-spawn invulnerability (QC StatusEffects_apply(SpawnShield)).
        float shieldTime = Cvar("g_monsters_spawnshieldtime", 2f); // monsters.cfg: g_monsters_spawnshieldtime 2
        if (shieldTime > 0f)
            MonsterFramework.ApplyFor(MonsterFramework.SpawnShield, e, shieldTime);

        // QC Monster_Spawn:1457 — this.event_damage = Monster_Damage; this.reset = Monster_Reset. In the port the
        // monster carries its OWN event_damage as a GtEventDamage shim: DamageSystem.EventDamage already routes a
        // non-player edict with a GtEventDamage to it and returns, so a monster victim runs the monster pain/death
        // path (MonsterEventDamage) instead of being treated as a player. (event_heal/Monster_Heal is a deliberate
        // deferral — the port Mage heals directly via HealPulse, not through a per-entity heal delegate.)
        e.GtEventDamage = MonsterEventDamage;
        e.Reset = Reset;

        // QC seeds .damageforcescale on the edict so the GENERIC apply-push block (damage.qc:671) pushes the
        // monster too. The shim/MarkPain then adds force*damageforcescale AGAIN (sv_monsters.qc:1112) — QC double-
        // applies for a pushable monster, and that is faithful (recon §8). The live per-monster value (each
        // descriptor's Spawn overrides st.DamageForceScale after this call) is re-synced onto the edict every frame
        // in RunThink; here we seed the default so a hit before the first think still pushes.
        e.DamageForceScale = st.DamageForceScale;

        // QC Monster_Spawn_Setup:1364 — Monster_Sound(monstersound_spawn, 0, false, CH_VOICE): the spawn cue.
        MonsterSound(e, st, "spawn", 0f, false, SoundChannel.Voice);

        return st;
    }

    // ====================================================================================
    // Team / skill colors (monster_setupcolors / monster_changeteam, sv_monsters.qc:172/200)
    // ====================================================================================

    /// <summary>
    /// Port of <c>monster_setupcolors</c> (sv_monsters.qc:172): pick the monster's <c>.colormap</c> by team (in
    /// teamplay), by owner (a player-spawned monster), else by skill tier so monsters are tinted by difficulty.
    /// QC also sets <c>.glowmod = colormapPaletteColor(colormap &amp; 0x0F, false)</c>; the headless Entity has no
    /// glowmod field, but the csqcmodel derives the glowmod from the colormap nibble client-side, so writing the
    /// authoritative colormap (with RENDER_COLORMAPPED) is the load-bearing decision — the established colormap
    /// render seam (<see cref="Entity.ColorMapOverride"/>, the same field g_model_setcolormaptoactivator uses).
    /// </summary>
    public static void SetupColors(Entity e, MonsterState st)
    {
        int colormap;
        Entity? owner = e.Owner;
        if (IsTeamplay && e.Team != 0f)
            colormap = 1024 + ((int)e.Team - 1) * 17;
        else if (owner is not null && (owner.Flags & EntFlags.Client) != 0)
            colormap = owner.ColorMapOverride & ~Entity.RenderColormapped; // inherit the player's colormap value
        else if (st.Skill <= MonsterSkill.Easy)
            colormap = 1126;
        else if (st.Skill <= MonsterSkill.Medium)
            colormap = 1075;
        else if (st.Skill <= MonsterSkill.Hard)
            colormap = 1228;
        else if (st.Skill <= MonsterSkill.Insane)
            colormap = 1092;
        else if (st.Skill <= MonsterSkill.Nightmare)
            colormap = 1160;
        else
            colormap = 1024;

        // QC stores the bare colormap; the port's render seam expects RENDER_COLORMAPPED set when a colormap is
        // present (mirrors g_model_setcolormaptoactivator / MapModels.SetColormapToActivator).
        e.ColorMapOverride = colormap > 0 ? (colormap | Entity.RenderColormapped) : 0;
    }

    /// <summary>
    /// Port of <c>monster_changeteam</c> (sv_monsters.qc:200): only in teamplay — re-team the monster, (re)arm it
    /// as an attackable target, re-tint it for the new team, and (if it has a healthbar sprite) re-color the danger
    /// radar icon to the new team (QC:211-216 WaypointSprite_UpdateTeamRadar + sprite.team = newteam).
    /// </summary>
    public static void ChangeTeam(Entity e, float newTeam)
    {
        if (!IsTeamplay)
            return;
        e.Team = newTeam;
        // QC: if (!this.monster_attack) IL_PUSH(g_monster_targets, this); this.monster_attack = true; — the port
        // scans FindInRadius for live monsters in teamplay (FindTarget), so a re-teamed monster is already a
        // valid target without an intrusive-list push; re-tint to the new team.
        MonsterState? st = StateOf(e);
        if (st is not null)
        {
            SetupColors(e, st);
            // QC monster_changeteam:211-216: if (this.sprite) { WaypointSprite_UpdateTeamRadar(this.sprite,
            // RADARICON_DANGER, team?Team_ColorRGB:'1 0 0'); this.sprite.team = newteam; }
            if (st.Sprite is not null)
            {
                Vector3 col = newTeam != 0f ? Teams.ColorRgb((int)newTeam) : new Vector3(1f, 0f, 0f);
                Waypoints.WaypointSprites.UpdateTeamRadar(st.Sprite, st.Sprite.RadarIcon, col);
                st.Sprite.Team = (int)newTeam;
            }
        }
    }

    /// <summary>
    /// Port of the healthbar block in <c>Monster_Spawn_Setup</c> (sv_monsters.qc:1366-1376): spawn the floating
    /// WP_Monster waypoint sprite (RADARICON_DANGER) at <c>maxs.z + 15</c> above the monster, colored by team
    /// (<see cref="Teams.ColorRgb"/>) or red in FFA, and prime its max-health/health bar unless the monster is
    /// INVINCIBLE. The port's <c>UpdateHealth</c> takes a pre-normalized 0..1 fill (the CSQC client normalizes by
    /// max in QC), so the bar is fed <c>health / max_health</c>. Stored on <see cref="MonsterState.Sprite"/>.
    /// </summary>
    private static void SpawnHealthbar(Entity e, MonsterState st)
    {
        Vector3 col = e.Team != 0f ? Teams.ColorRgb((int)e.Team) : new Vector3(1f, 0f, 0f);
        // QC WaypointSprite_Spawn(WP_Monster, 0, 1024, this, '0 0 1'*(maxs.z+15), NULL, team, this, sprite, true,
        // RADARICON_DANGER): lifetime 0 (persists), maxdist 1024, follows the monster at maxs.z+15, hideable true.
        var offset = new Vector3(0f, 0f, e.Maxs.Z + 15f);
        Waypoints.WaypointSprite wp = Waypoints.WaypointSprites.Spawn(
            "Monster", 0f, 1024f, e, offset, e.Origin, (int)e.Team, col,
            radarIcon: 1, rule: Waypoints.SpriteRule.Default, hideable: true);
        st.Sprite = wp;

        // QC: if (!(spawnflags & MONSTERFLAG_INVINCIBLE)) { UpdateMaxHealth(max_health); UpdateHealth(RES_HEALTH); }
        if ((e.SpawnFlags & MonsterFlag_Invincible) == 0)
        {
            Waypoints.WaypointSprites.UpdateMaxHealth(wp, st.MaxHealth);
            Waypoints.WaypointSprites.UpdateHealth(wp, st.MaxHealth > 0f ? e.Health / st.MaxHealth : -1f);
        }
    }

    /// <summary>
    /// Port of <c>WaypointSprite_UpdateHealth(this.sprite, GetResource(this, RES_HEALTH))</c> (sv_monsters.qc:1104
    /// pain / :1155 corpse): refresh the floating health bar after a hit. No-op when healthbars are off (Sprite
    /// null). Feeds the pre-normalized 0..1 fill the port's bar expects.
    /// </summary>
    public static void UpdateHealthbar(MonsterState st, float health)
    {
        if (st.Sprite is null) return;
        Waypoints.WaypointSprites.UpdateHealth(st.Sprite, st.MaxHealth > 0f ? health / st.MaxHealth : -1f);
    }

    /// <summary>
    /// Set a monster's collision size, then apply the shared post-size finalization QC does in
    /// <c>Monster_Spawn_Setup</c> (sv_monsters.qc) AFTER the descriptor's <c>mr_setup</c> stamped the base size:
    /// (1) the Quake-resize (<c>if (g_monsters_quake_resize &amp;&amp; (spawnflags &amp; MONSTER_SIZE_QUAKE) &amp;&amp;
    /// !RESPAWNED) { scale *= 1.3; setsize(mins*1.3, maxs*1.3); }</c>) — the port scales the COLLISION bbox here
    /// (the visual <c>scale</c> needs a networked render-scale field the port lacks, see the unportable note);
    /// and (2) <c>view_ofs = '0 0 0.35' * maxs.z</c> — the eye height every monster LOS/aim/danger trace reads
    /// (<see cref="FindTarget"/>, <see cref="ValidTarget"/>, <see cref="FaceTarget"/>, <c>CheckDanger</c>,
    /// <see cref="WanderTarget"/>). Concrete monsters call this in their <see cref="Monster.Spawn"/> in place of
    /// the raw <c>Api.Entities.SetSize</c> so the eye is at ~35% of the bbox height, not at the origin/feet.
    /// </summary>
    public static void ApplySize(Monster def, Entity e, Vector3 mins, Vector3 maxs)
    {
        // QC Monster_Spawn_Setup: MONSTER_SIZE_QUAKE monsters spawn 1.3x larger (non-respawn only) when
        // g_monsters_quake_resize. Scale the collision bbox; the matching visual scale*=1.3 is render-deferred
        // (no Entity render-scale field — see unportable). Gated on the descriptor's SizeQuake flag (default off,
        // so the stock five are unaffected — none are Quake-imported).
        MonsterState? st = StateOf(e);
        bool respawned = st?.Respawned ?? ((e.SpawnFlags & MonsterFlag_Respawned) != 0);
        if (def.SizeQuake && !respawned && Cvar("g_monsters_quake_resize", 1f) != 0f) // monsters.cfg: g_monsters_quake_resize 1
        {
            mins *= 1.3f;
            maxs *= 1.3f;
        }

        if (Api.Services is not null)
            Api.Entities.SetSize(e, mins, maxs);
        else
            { e.Mins = mins; e.Maxs = maxs; e.Size = maxs - mins; }

        // QC Monster_Spawn_Setup: this.view_ofs = '0 0 0.35' * this.maxs.z — the monster's eye sits at 35% of the
        // bbox top. Without this seed view_ofs stays (0,0,0) and every eye-based trace (LOS/aim/danger) fires from
        // the origin (feet/center), shooting low and seeing through the floor. Set from the FINAL maxs (post-resize).
        e.ViewOfs = new Vector3(0f, 0f, 0.35f * maxs.Z);
    }

    // ====================================================================================
    // Map-placement / code spawn driver (Monster_Spawn + spawnmonster, sv_monsters.qc / sv_spawn.qc)
    // ====================================================================================

    /// <summary>
    /// Port of <c>Monster_Spawn(this, check_appear, mon)</c> (sv_monsters.qc) — the public driver every
    /// <c>monster_X</c> map spawnfunc calls. Validates the monster + the <c>g_monsters</c> master switch, honours
    /// the APPEAR spawnflag (defer to a trigger via <see cref="Monster.Spawn"/>-on-use), applies the per-monster
    /// setup (the descriptor's <see cref="Monster.Spawn"/> = QC <c>Monster_Spawn_Setup</c>/<c>mr_setup</c>), culls
    /// by Quake skill spawnflags, then wires the entity's use (acquire-attacker) and think (the live brain)
    /// hooks. Returns false if the host should remove the edict (invalid monster / disabled / culled).
    ///
    /// Differences from QC that are intentional: the per-monster field stamping (FL_MONSTER, solid, movetype,
    /// health, ranges, spawn shield, model) all lives inside <see cref="Setup"/> which the descriptor's Spawn
    /// invokes, so this driver does not repeat it. The QC <c>this.reset = Monster_Reset</c> round-restart hook is
    /// omitted — the headless <see cref="Entity"/> has no reset delegate (consistent with the turret/vehicle
    /// ports). bot-target / damagedbycontents intrusive-list pushes are engine bookkeeping (deferred).
    /// </summary>
    public static bool SpawnFromMap(Monster? def, Entity e, bool checkAppear)
    {
        // QC: if (!mon || mon == MON_Null) return false;
        if (def is null)
            return false;

        // QC: if (!autocvar_g_monsters) { Monster_Remove(this); return false; }
        if (!MasterSwitchEnabled("g_monsters"))
        {
            Remove(e);
            return false;
        }

        // QC Monster_Appear_Check: a monster with MONSTERFLAG_APPEAR doesn't spawn now — it sleeps with no
        // think, and a trigger firing its .use (Monster_Appear) materialises it later. Return true so the host
        // keeps the edict. We stash the descriptor on the state's Def via a placeholder MonsterState so the
        // deferred Appear can recover it (QC keeps it in .monsterdef).
        if (checkAppear && (e.SpawnFlags & MonsterFlag_Appear) != 0)
        {
            e.Think = null;
            e.NextThink = 0f;
            e.Flags |= EntFlags.Monster; // QC sets FL_MONSTER so it can still be butchered
            _appearDefs[e] = def;
            e.Use = (self, actor) =>
            {
                // Monster_Appear: adopt the trigger's activator as the first enemy, then spawn for real.
                if (_appearDefs.TryGetValue(self, out Monster? d))
                {
                    _appearDefs.Remove(self);
                    if (SpawnFromMap(d, self, false) && actor is not null)
                        self.Enemy = ValidTarget(self, actor, true) ? actor : self.Enemy;
                }
            };
            return true;
        }

        // QC Monster_Spawn_Setup is the per-monster mr_setup; the port folds Setup (the shared stamping) + the
        // per-type size/model/specifics into the descriptor's Spawn. Run it now.
        def.Spawn(e);

        // Quake-style skill culling (Monster_Spawn): a map can flag a monster absent on some skills. The
        // descriptor's Spawn already stamped the entity; if it's culled we remove it after the fact.
        if (ShouldCullForSkill(e))
        {
            Remove(e);
            return false;
        }

        // QC: if (this.team && !teamplay) this.team = 0;
        if (e.Team != 0f && !IsTeamplay)
            e.Team = 0f;

        MonsterState? st = StateOf(e);
        if (st is null) // Spawn failed to seed state (shouldn't happen) — treat as invalid.
        {
            Remove(e);
            return false;
        }

        // QC Monster_Spawn:1427 — ++monsters_total for a NATURAL (map-placed, first-life) monster only. Spawned
        // (mobspawn/wave) and respawned monsters are excluded from the scoreboard map-stats total. Placed where QC
        // does it: after the team gate, before the use/think wiring.
        if (!st.Spawned && !st.Respawned)
            MonstersTotal++;

        // QC Monster_Spawn:1525 — if (!(this.spawnflags & MONSTERFLAG_RESPAWNED)) monster_setupcolors(this).
        // Tints the monster by team / owner / skill tier (the difficulty colormap). Respawned monsters keep the
        // colormap they were spawned with, so this is gated to the first life.
        if (!st.Respawned)
            SetupColors(e, st);

        // QC Monster_Spawn_Setup:1366 — if (autocvar_g_monsters_healthbars) spawn a floating WP_Monster health-bar
        // waypoint sprite at maxs.z+15, RADARICON_DANGER, colored by team (Team_ColorRGB) or red. Updated on each
        // hit (MarkPain / MonsterDeadDamage) and killed on death (MarkDead). Default g_monsters_healthbars 0.
        if (Cvar("g_monsters_healthbars", 0f) != 0f)
            SpawnHealthbar(e, st);

        // QC: this.use = Monster_Use; (a trigger acquiring this monster points it at the activator).
        e.Use = (self, actor) =>
        {
            if (ValidTarget(self, actor, true))
                self.Enemy = actor;
        };

        // QC: setthink(this, Monster_Think); this.nextthink = time; — hand the brain to the descriptor's Think,
        // re-armed every frame (Monster_Think re-sets nextthink = time). This is what makes a hand-placed
        // monster actually run; the simulation loop's SV_RunThink fires it.
        e.Think = self => { self.NextThink = Now; st.Def.Think(self); };
        e.NextThink = Now;

        // QC sv_monsters.qc:1381: if (MUTATOR_CALLHOOK(MonsterSpawn, this)) return false; — a mutator may cancel
        // the spawn (e.g. instagib sets Mage skin=1; no mutator returns true so no cancel in practice).
        if (MutatorHooks.FireMonsterSpawn(e))
        {
            Remove(e);
            return false;
        }

        return true;
    }

    /// <summary>Pending APPEAR-monster descriptors keyed by edict, recovered when the trigger fires (QC .monsterdef).</summary>
    private static readonly Dictionary<Entity, Monster> _appearDefs = new();

    /// <summary>
    /// Port of <c>spawnmonster()</c> (sv_spawn.qc): resolve a monster type by netname (or "random"/"anyrandom"),
    /// stamp owner/team/move flags, and drive it through <see cref="SpawnFromMap"/> with <c>check_appear=false</c>
    /// (the code-spawn path used by <c>monster_spawner</c> and gametypes). <paramref name="respawn"/> false adds
    /// MONSTERFLAG_NORESPAWN; the spawn is always flagged MONSTERFLAG_SPAWNED. Returns the new monster entity, or
    /// null if no valid monster resolved and <paramref name="removeIfInvalid"/> is set.
    /// </summary>
    public static Entity? SpawnMonster(Entity e, string monster, Monster? monsterId, Entity? spawnedBy,
        Entity? follow, Vector3 origin, bool respawn, bool removeIfInvalid, int moveFlags)
    {
        // QC: e.spawnflags = MONSTERFLAG_SPAWNED; (+ NORESPAWN when !respwn).
        e.SpawnFlags = MonsterFlag_Spawned;
        if (!respawn)
            e.SpawnFlags |= MonsterFlag_NoRespawn;

        if (Api.Services is not null)
            Api.Entities.SetOrigin(e, origin);

        // Resolve the monster type (QC random/anyrandom/by-netname selection).
        bool allowAny = monster == "anyrandom";
        if (monster == "random" || allowAny)
        {
            monsterId = PickRandomMonster(allowAny);
        }
        else if (!string.IsNullOrEmpty(monster))
        {
            Monster? found = Monsters.ByName(monster);
            if (found is not null)
                monsterId = found;
            else if (monsterId is null)
            {
                if (removeIfInvalid)
                {
                    Remove(e);
                    return null;
                }
                // QC: fall back to a random valid monster when the requested name was bogus.
                return SpawnMonster(e, "random", null, spawnedBy, follow, origin, respawn, removeIfInvalid, moveFlags);
            }
        }

        // QC: e.realowner = spawnedby; (the port's RealOwner is Owner — used to count a spawner's monsters).
        if (spawnedBy is not null)
            e.Owner = spawnedBy;

        // QC: a player-spawned monster inherits the spawner's team + facing (and optionally follows its owner).
        bool playerSpawned = spawnedBy is not null && (spawnedBy.Flags & EntFlags.Client) != 0;
        if (playerSpawned)
        {
            if (IsTeamplay && Cvar("g_monsters_teams", 1f) != 0f)
                e.Team = spawnedBy!.Team;
            Vector3 a = e.Angles; a.Y = spawnedBy!.Angles.Y; e.Angles = a;
        }

        if (!SpawnFromMap(monsterId, e, false))
        {
            Remove(e); // QC removes even if told not to: nothing valid was spawned.
            return null;
        }

        // Post-spawn state tweaks: the move style (QC e.monster_moveflags) and — for a player summon with
        // g_monsters_owners — the follow target (QC e.monster_follow = own). Both live on the MonsterState that
        // SpawnFromMap created via the descriptor's Spawn.
        if (StateOf(e) is { } mst)
        {
            if (moveFlags != 0)
                mst.MoveFlags = moveFlags;
            if (playerSpawned && follow is not null && Cvar("g_monsters_owners", 1f) != 0f)
                mst.Follow = follow;
        }
        return e;
    }

    /// <summary>
    /// QC spawnmonster random branch: a uniformly-chosen monster. QC filters MON_FLAG_HIDDEN (unless
    /// <paramref name="allowAny"/>) and MONSTER_TYPE_PASSIVE; the port's five monsters are all normal
    /// spawnables, so every entry is eligible and the flag filter is currently a no-op.
    /// </summary>
    private static Monster? PickRandomMonster(bool allowAny)
    {
        _ = allowAny;
        int n = Monsters.Count;
        return n <= 0 ? null : Monsters.All[Prandom.RangeInt(0, n)];
    }

    /// <summary>QC Monster_Remove (sv_monsters.qc:947): kill the healthbar sprite, forget the monster state and
    /// delete the edict.</summary>
    private static void Remove(Entity e)
    {
        // QC Monster_Remove:959 — WaypointSprite_Kill(this.sprite): a removed monster must not leave an orphaned
        // floating health bar (e.g. a SPAWNED monster culled on round restart, or a skill-culled one).
        if (StateOf(e) is { Sprite: not null } st)
        {
            Waypoints.WaypointSprites.Kill(st.Sprite);
            st.Sprite = null;
        }
        Forget(e);
        if (Api.Services is not null)
            Api.Entities.Remove(e);
    }

    // ====================================================================================
    // Skill scaling (MONSTER_SKILLMOD, sv_monsters.qh)
    // ====================================================================================

    /// <summary>QC MONSTER_SKILLMOD(mon) = 0.5 + skill * ((1.2 - 0.3) / 10).</summary>
    public static float SkillMod(MonsterState st) => 0.5f + st.Skill * ((1.2f - 0.3f) / 10f);

    private static int ResolveSkill()
    {
        float s = Cvar("g_monsters_skill", MonsterSkill.Easy);
        return (int)System.Math.Clamp(s, 1f, 10f);
    }

    /// <summary>
    /// Quake-style skill-based spawn culling (Monster_Spawn, sv_monsters.qc): a map may flag a monster to be
    /// absent on certain skills via the NOTEASY/NOTMEDIUM/NOTHARD spawnflags. Returns true if the monster
    /// should NOT spawn at the current skill and the host should remove it. Call from a concrete monster's
    /// Spawn (or the host spawner) before finishing setup.
    /// </summary>
    public static bool ShouldCullForSkill(Entity e)
    {
        // QC keys this on this.monster_skill (the per-entity value resolved in Setup), so prefer the resolved
        // skill on the entity's state and only fall back to the cvar when state isn't seeded yet.
        int skill = StateOf(e)?.Skill ?? ResolveSkill();
        if (skill == MonsterSkill.Easy && (e.SpawnFlags & MonsterSkill_NotEasy) != 0) return true;
        if (skill == MonsterSkill.Medium && (e.SpawnFlags & MonsterSkill_NotMedium) != 0) return true;
        if (skill >= MonsterSkill.Hard && (e.SpawnFlags & MonsterSkill_NotHard) != 0) return true;
        return false;
    }

    // ====================================================================================
    // Targeting (Monster_FindTarget / Monster_ValidTarget / Monster_Enemy_Check)
    // ====================================================================================

    /// <summary>
    /// Port of <c>Monster_ValidTarget</c> (sv_monsters.qc): the target must exist, not be us, be alive and
    /// damageable, not be flagged no-target, be on a different team (when teamplay is on), not be a follow
    /// partner, be visible (alpha + traceline), and — when "infront" targeting is enabled — be within the
    /// monster's facing cone. <paramref name="skipFacing"/> skips the cone check (QC's skipfacing, used for
    /// touch/use acquisition where facing shouldn't matter). Warpzone PVS and the MonsterValidTarget mutator
    /// hook remain host concerns (deferred); everything balance-relevant is ported.
    /// </summary>
    public static bool ValidTarget(Entity self, Entity? targ, bool skipFacing = false)
    {
        if (self is null || targ is null || targ == self || targ.IsFreed) return false;

        // QC IS_VEHICLE(targ): a target driving a vehicle (the headless port marks a vehicle edict with a
        // VehicleDef descriptor). QC exempts a vehicle from the dead/health, notarget and alpha gates below
        // (it's a solid object, not a living/spectating client), so several checks carry a !IS_VEHICLE guard.
        var selfState = StateOf(self);
        bool targIsVehicle = targ.VehicleDef is not null;

        // QC Monster_ValidTarget:108 — (IS_VEHICLE(targ) && !MON_FLAG_RANGED): melee vs a vehicle is useless, so
        // a melee-only monster (e.g. the zombie) never acquires a vehicle. A ranged monster may.
        if (targIsVehicle && selfState is not null && !selfState.Def.IsRanged) return false;

        // QC Monster_ValidTarget:108 — game_stopped || time < game_starttime: no target acquisition before the
        // match clock starts or once the match is stopped (warmup/intermission).
        if (MatchHalted()) return false;
        if (targ.TakeDamage == DamageMode.No) return false;
        // QC Monster_ValidTarget:112 — the dead/zero-health gate carries !IS_VEHICLE (a vehicle is never "dead").
        if (!targIsVehicle && (targ.DeadState != DeadFlag.No || targ.Health <= 0f)) return false;
        if (self.DeadState != DeadFlag.No || self.Health <= 0f) return false;
        // QC Monster_ValidTarget:114 — FL_NOTARGET also carries !IS_VEHICLE.
        if (!targIsVehicle && (targ.Flags & EntFlags.NoTarget) != 0) return false;

        // QC Monster_ValidTarget:115 — (!autocvar_g_monsters_typefrag && PHYS_INPUT_BUTTON_CHAT(targ)): when
        // typefrag is off (default 1, so normally inactive), a target with the chat console open (typing) is
        // spared. Entity.ButtonChat is the live input mirror (written by PlayerPhysics from the typing intent).
        if (Cvar("g_monsters_typefrag", 1f) == 0f && targ.ButtonChat) return false;

        // Don't attack our follow partner, nor a monster following us (QC monster_follow guard).
        var st = selfState;
        if (st?.Follow == targ) return false;
        var ts = StateOf(targ);
        if (ts?.Follow == self) return false;

        // Team gate: monsters never attack same-team entities in teamplay (QC SAME_TEAM).
        if (IsTeamplay && self.Team != 0f && targ.Team == self.Team) return false;

        // QC Monster_ValidTarget:117 — (targ.alpha != 0 && targ.alpha < 0.5): a faded/cloaked target is too dim
        // to see. The port's Entity.Alpha is a live render-alpha (default 1 = opaque; PowerupsMutator drops it to
        // the invisibility alpha, CloakedMutator to 0.25), so a target turned mostly-transparent is now spared.
        // (QC's `alpha != 0` excludes the QC "0 == default opaque" sentinel; the port uses 1 as the opaque
        // default and 0 only for the gibbed/cleared marker, so we mirror QC by keeping the != 0 guard.)
        if (targ.Alpha != 0f && targ.Alpha < 0.5f) return false;

        // QC Monster_ValidTarget:119 — MUTATOR_CALLHOOK(MonsterValidTarget, this, targ) invalidates the target
        // (the powerups mutator forbids targeting a player holding Invisibility — monster stealth).
        if (MutatorHooks.FireMonsterValidTarget(self, targ)) return false;

        // Line of sight: QC Monster_ValidTarget:118 first does a coarse checkpvs(eye, targ) cull (gated by
        // g_monsters_lineofsight), THEN the fine traceline. The PVS pre-cull rejects a target in a non-visible
        // leaf even when an oblique traceline would clip through. Engine-only (compiled BSP PVS), so it's gated on
        // Api.Services like the turret port — deterministic tests with no PVS fall through to the traceline.
        if (Cvar("g_monsters_lineofsight", 1f) != 0f)
        {
            Vector3 eye = self.Origin + self.ViewOfs;
            Vector3 targCenter = (targ.AbsMin + targ.AbsMax) * 0.5f;
            if (Api.Services is not null && !Api.Trace.CheckPvs(eye, targCenter))
                return false; // not potentially visible — QC checkpvs coarse cull
            TraceResult tr = Api.Trace.Trace(eye, Vector3.Zero, Vector3.Zero, targCenter, MoveFilter.NoMonsters, self);
            if (tr.Fraction < 1f && tr.Ent != targ)
                return false; // solid in the way
        }

        // "Infront" targeting: only see enemies within our facing cone (QC monster_facing), unless this is
        // already our enemy or facing is skipped. Enabled by the cvar or the per-monster INFRONT spawnflag.
        bool infront = Cvar("g_monsters_target_infront", 0f) != 0f
                       || (st is not null && (self.SpawnFlags & MonsterFlag_Infront) != 0);
        if (!skipFacing && infront && self.Enemy != targ && !MonsterFacing(self, targ))
            return false;

        return true;
    }

    /// <summary>QC monster_facing: true when <paramref name="targ"/> is within the monster's forward cone.</summary>
    private static bool MonsterFacing(Entity self, Entity targ)
    {
        Vector3 forward = QMath.Forward(self.Angles);
        Vector3 targOrg = targ.Origin;
        Vector3 myOrg = self.Origin;
        if (Cvar("g_monsters_target_infront_2d", 1f) != 0f)
        {
            forward.Z = 0f; targOrg.Z = 0f; myOrg.Z = 0f;
        }
        float dot = QMath.Dot(QMath.Normalize(targOrg - myOrg), QMath.Normalize(forward));
        return dot > Cvar("g_monsters_target_infront_range", 0.3f);
    }

    /// <summary>
    /// Port of <c>Monster_FindTarget</c> (sv_monsters.qc): pick the closest valid target within
    /// <see cref="MonsterState.TargetRange"/>. QC scans the intrusive <c>g_monster_targets</c> list of
    /// attackable entities; here we scan entities in radius — players always, plus (in teamplay, where
    /// monsters can field enemies of each other) other monsters that aren't our teammates. Crouching targets
    /// are seen at 75% range (QC). The MonsterFindTarget mutator hook stays a host concern.
    /// </summary>
    public static Entity? FindTarget(Entity self, MonsterState st)
    {
        Entity? closest = null;
        float bestDistSq = float.MaxValue;
        Vector3 myCenter = self.Origin + self.ViewOfs;
        bool teamMonsters = IsTeamplay && Cvar("g_monsters_teams", 1f) != 0f;

        foreach (Entity it in Api.Entities.FindInRadius(self.Origin, st.TargetRange))
        {
            bool isClient = (it.Flags & EntFlags.Client) != 0;
            bool isMonster = (it.Flags & EntFlags.Monster) != 0 && it != self;
            // Candidates: players always; other monsters only when monsters can fight each other in teams.
            if (!isClient && !(teamMonsters && isMonster)) continue;

            // Crouch range scaling (QC: PHYS_INPUT_BUTTON_CROUCH(it) -> trange *= 0.75).
            float trange = st.TargetRange;
            if (isClient && IsCrouching(it))
                trange *= 0.75f;

            Vector3 theirMid = (it.AbsMin + it.AbsMax) * 0.5f;
            if ((theirMid - self.Origin).Length() > trange) continue;
            if (!ValidTarget(self, it, false)) continue;

            float d = (myCenter - theirMid).LengthSquared();
            if (d < bestDistSq) { bestDistSq = d; closest = it; }
        }
        return closest;
    }

    /// <summary>Whether a player target is crouched (QC PHYS_INPUT_BUTTON_CROUCH). Entity.ButtonCrouch is the
    /// live input mirror (written by the input layer before PlayerPhysics), so a crouching target is seen at
    /// 75% range as in QC — the stealth/range benefit is now modeled.</summary>
    private static bool IsCrouching(Entity e) => e.ButtonCrouch;

    /// <summary>
    /// Port of <c>Monster_Sound</c> (sv_monsters.qc:368): play one of the monster's voice cues, honouring the
    /// <c>g_monsters_sounds</c> master gate and the shared <c>msound_delay</c> throttle. <paramref name="cue"/>
    /// is the suffix of the fixed-path sample (sight/pain/death/idle/melee/attack/ranged/spawn) — the full
    /// <c>.sounds</c>-file GlobalSound lookup + skin-keyed Monster_Sounds_Update is a presentation gap left for
    /// the client (named no-op). When <paramref name="delayToo"/> the cue is skipped while still inside the
    /// previous throttle window (QC's pain 1.2s / idle 7s self-spam guards); every cue advances the window by
    /// <paramref name="soundDelay"/>.
    /// </summary>
    private static void MonsterSound(Entity self, MonsterState st, string cue, float soundDelay, bool delayToo,
        SoundChannel chan)
    {
        // QC: if (!autocvar_g_monsters_sounds || (delaytoo && time < this.msound_delay)) return;
        if (Cvar("g_monsters_sounds", 1f) == 0f) return;
        if (delayToo && Now < st.MSoundDelay) return;
        // QC: string sample = this.(samplefield); if (sample != "") sample = GlobalSound_sample(...);
        // sound7(this, chan, sample, ...). A cue that is COMMENTED OUT in the model's .sounds file (or whose
        // model ships no .sounds at all) yields an EMPTY sample, so the engine plays nothing — but the throttle
        // window is still advanced. We mirror that: only emit a sample for a cue this monster actually defines.
        // (SoundCues==null = legacy "not audited, play all"; an empty set = fully silent monster.)
        bool cueDefined = st.Def.SoundCues is null || st.Def.SoundCues.Contains(cue);
        if (st.Def.Model is not null && cueDefined)
        {
            // QC GlobalSound resolves the model .sounds line "sound/monsters/<dir>/<cue> <count>": when count>0
            // pick a random numbered variant (cue1..cueN), else the bare cue name. The samples live under the
            // monster's sound subdir (sound/monsters/<dir>/), NOT a flat monsters/<name>_<cue> path.
            int count = st.Def.SoundCueCount(cue);
            string suffix = count > 0 ? (Prandom.RangeInt(0, count) + 1).ToString() : "";
            Api.Sound.Play(self, chan, "monsters/" + st.Def.SoundDir + "/" + cue + suffix + st.Def.SoundExt);
        }
        st.MSoundDelay = Now + soundDelay; // QC: this.msound_delay = time + sound_delay
    }

    /// <summary>
    /// Play the dispatch-level <c>monstersound_melee</c> voice cue (QC <c>Monster_Attack_Check:468/478</c>:
    /// fired once per attack DISPATCH whenever the attackfunc returns <c>attack_success==1</c>, for melee AND
    /// ranged). The melee combo plays it from <see cref="QueueCombo"/>; ranged attacks (the golem smash /
    /// lightning) call this at dispatch since they succeed without a connecting traceline.
    /// </summary>
    public static void PlayMeleeCue(Entity self, MonsterState st)
        => MonsterSound(self, st, "melee", 0f, false, SoundChannel.Voice);

    /// <summary>
    /// Port of <c>Monster_Enemy_Check</c> (sv_monsters.qc): drop the current enemy if it became invalid or
    /// strayed out of range, otherwise keep it; if we have none, try to acquire one.
    /// </summary>
    public static void EnemyCheck(Entity self, MonsterState st)
    {
        if (self.Enemy is not null)
        {
            Entity en = self.Enemy;
            Vector3 targOrigin = (en.AbsMin + en.AbsMax) * 0.5f;
            bool tooFar = (self.Origin - targOrigin).Length() > st.TargetRange;

            // QC Monster_Enemy_Check:1247 re-traces LOS to the held enemy each second and drops it when something
            // solid (not the enemy itself) blocks the way — so an enemy that ducks behind a wall is RELEASED, it
            // isn't held until it strays out of range. (WarpZone_TraceLine -> our MOVE_NOMONSTERS traceline.)
            bool losBlocked = false;
            TraceResult tr = Api.Trace.Trace(self.Origin, Vector3.Zero, Vector3.Zero, targOrigin,
                MoveFilter.NoMonsters, self);
            if (tr.Fraction < 1f && tr.Ent != en)
                losBlocked = true;

            // QC also drops a frozen enemy (STAT(FROZEN, enemy)).
            bool frozenEnemy = StatusEffectsCatalog.Frozen != null
                               && StatusEffectsCatalog.Has(en, StatusEffectsCatalog.Frozen);

            // QC Monster_Enemy_Check:1256 — (enemy.alpha < 0.5 && enemy.alpha != 0): drop an enemy that has
            // faded out (turned invisible/cloaked). Entity.Alpha is now a live render-alpha (opaque default 1;
            // PowerupsMutator/CloakedMutator drop it), so a chased enemy that activates invisibility is released
            // — matching the now-live alpha gate in ValidTarget. (The != 0 guard mirrors QC's opaque sentinel.)
            bool fadedEnemy = en.Alpha < 0.5f && en.Alpha != 0f;

            if (en.IsFreed || en.DeadState != DeadFlag.No || en.Health < 1f
                || (en.Flags & EntFlags.NoTarget) != 0
                || frozenEnemy
                || fadedEnemy
                || en.TakeDamage == DamageMode.No
                || tooFar
                || losBlocked)
            {
                self.Enemy = null;
            }
            else
            {
                return; // current enemy still good
            }
        }

        self.Enemy = FindTarget(self, st);
        if (self.Enemy is not null)
            MonsterSound(self, st, "sight", 0f, false, SoundChannel.Voice); // QC monstersound_sight (no throttle)
    }

    // ====================================================================================
    // Movement (Monster_Move / Monster_Move_Target, sv_monsters.qc)
    // ====================================================================================

    /// <summary>
    /// Full <c>Monster_Move</c> (sv_monsters.qc): resolve the move target by movestate (enemy chase / follow /
    /// spawnloc / nomove / wander), steer toward it with a clamped yaw turn (shortangle), drive velocity, and
    /// avoid edge/lava danger ahead (re-pathing if the way is unsafe). Swimmers drown out of water; frozen
    /// monsters just brake. Run/walk/idle anim PHASE is selected here (client frame playback is CSQC).
    /// Ground monsters keep their gravity-driven Z; flyers/vertical swimmers may move in Z.
    /// </summary>
    public static void Move(Entity self, MonsterState st)
    {
        bool flyOrSwim = (self.Flags & (EntFlags.Fly | EntFlags.Swim)) != 0;

        // Frozen: no physics, idle (QC StatusEffects_active(Frozen) early-out).
        if (StatusEffectsCatalog.Frozen != null && StatusEffectsCatalog.Has(self, StatusEffectsCatalog.Frozen))
        {
            BrakeSimple(self, st.StopSpeed, flyOrSwim);
            st.Anim = MonsterAnim.Idle;
            return;
        }

        // Swimmer out of water: drown + thrash (QC FL_SWIM waterlevel branch).
        if ((self.Flags & EntFlags.Swim) != 0 && self.WaterLevel < WaterLevel_WetFeet)
        {
            if (Now >= st.LastTrace)
            {
                st.LastTrace = Now + 0.4f;
                Combat.Damage(self, null, null, 2f, DeathTypes.Drown, self.Origin, Vector3.Zero);
                Vector3 v = self.Velocity;
                if (Prandom.Float() < 0.5f) { v.Y += Prandom.Float() * 50f; v.X -= Prandom.Float() * 50f; }
                else { v.Y -= Prandom.Float() * 50f; v.X += Prandom.Float() * 50f; }
                v.Z += Prandom.Float() * 150f;
                self.Velocity = v;
            }
            self.MoveType = MoveType.Bounce;
            return;
        }

        // QC Monster_Move:826-840 — the match-state halt: game_stopped, the prematch clock (time < game_starttime),
        // and the round-not-started gate all force the monster to brake-and-idle (it doesn't roam/chase before the
        // match goes live or after it stops). RunThink's spawn_time gate covers `time < this.spawn_time`; the
        // campaign bots-may-start hold (QC sv_monsters.qc:846 `autocvar_g_campaign && !campaign_bots_may_start`) is
        // now wired via CampaignMovementHeld (the MonsterMove mutator hook + draggedby remain host concerns).
        if (MatchHalted() || CampaignMovementHeld())
        {
            BrakeSimple(self, st.StopSpeed, flyOrSwim);
            if (Now >= st.SpawnTime) st.Anim = MonsterAnim.Idle;
            return;
        }

        // QC Monster_Move:859-863 — prune a stale follow leader: drop monster_follow when the leader is on a
        // different team (teamplay + g_monsters_teams) or has become a spectator/observer, so the monster stops
        // tailing a leader it should no longer follow. (IS_SPEC/IS_OBSERVER -> a leader that left active play; the
        // headless proxy reads that as "no longer a live client".)
        if (st.Follow is not null)
        {
            bool leaderGone = st.Follow.IsFreed || (st.Follow.Flags & EntFlags.Client) == 0;
            bool diffTeam = IsTeamplay && Cvar("g_monsters_teams", 1f) != 0f
                            && self.Team != 0f && st.Follow.Team != self.Team;
            if (leaderGone || diffTeam)
                st.Follow = null;
        }

        // QC nades MonsterMove hook (entrap.qc:58-62): when a veiled monster's veil time lapses, restore its
        // pre-veil alpha. (The entrap run/walk slow from the same hook is applied at the speed computation below.)
        if (self.NadeVeilTime != 0f && self.NadeVeilTime <= Now)
        {
            self.Alpha = self.NadeVeilPrevAlpha;
            self.NadeVeilTime = 0f;
        }

        // QC Monster_Move:880 — Monster_Sound(monstersound_idle, 7, true, CH_VOICE) when the monster has no enemy:
        // the ~7s-throttled wandering idle voice. (delaytoo gates AND advances msound_delay so it can't spam.)
        if (self.Enemy is null)
            MonsterSound(self, st, "idle", 7f, true, SoundChannel.Voice);

        // Pick destination by movestate (Monster_Move_Target).
        bool running = self.Enemy is not null || st.MonsterMoveTo != Vector3.Zero;
        if (st.State != MonsterState_AttackMelee && (Now >= st.LastTrace || self.Enemy is not null))
            st.MoveTo = MoveTarget(self, st);

        // Don't move mid-melee (QC: state == MONSTER_ATTACK_MELEE freezes movement at our own origin).
        if (st.State == MonsterState_AttackMelee)
        {
            st.MoveTo = self.Origin;
            BrakeSimple(self, st.StopSpeed, flyOrSwim);
            return;
        }

        Vector3 moveTo = st.MoveTo;
        if (!flyOrSwim && (self.SpawnFlags & MonsterFlag_FlyVertical) == 0)
            moveTo.Z = self.Origin.Z; // ground movement is planar; gravity handles Z

        // Steer toward destination with a clamped yaw turn (QC steerlib_attract2 + shortangle_f clamp ±25°).
        Vector3 steerTo = moveTo - self.Origin;
        if (steerTo.LengthSquared() > 0f)
        {
            Vector3 wantAngles = QMath.VecToAngles(QMath.Normalize(steerTo));
            float yawDelta = QMath.Clamp(ShortAngle(wantAngles.Y - self.Angles.Y), -25f, 25f);
            Vector3 a = self.Angles;
            a.Y = AngleMod(a.Y + yawDelta);
            if (flyOrSwim) a.X = wantAngles.X; // flyers pitch toward the target directly
            self.Angles = a;
        }

        Vector3 forward = QMath.Forward(self.Angles);
        float vzKeep = self.Velocity.Z;

        // Danger check ahead (Monster_CheckDanger): look a bbox-width ahead (or along velocity if fast).
        float bboxWidth = System.Math.Min(self.Maxs.X - self.Mins.X, self.Maxs.Y - self.Mins.Y);
        Vector3 ahead = self.Origin + self.ViewOfs
            + (self.Velocity.Length() > bboxWidth * 5f ? self.Velocity * 0.2f : forward * bboxWidth);
        int danger = MonsterFramework.CheckDanger(self, ahead);

        float dist = (moveTo - self.Origin).Length();
        bool atTarget = dist <= System.Math.Max(16f, st.AttackRange * 0.5f);

        if (danger == 0 && !atTarget)
        {
            float speed = running ? st.RunSpeed : st.WalkSpeed;
            // Skill scaling, capped at 2.5x base to prevent craziness (QC bound on runspeed/walkspeed).
            float cap = (running ? st.RunSpeed : st.WalkSpeed) * 2.5f;
            speed = QMath.Bound(0f, speed * SkillMod(st), cap);
            // Webbed: a monster caught in spider web moves at half speed (QC spiderweb MonsterMove hook).
            if (StatusEffectsCatalog.Has(self, MonsterFramework.Webbed))
                speed *= 0.5f;
            // Entrapped: a monster inside a nades entrap orb is slowed (QC nades MonsterMove hook, entrap.qc:48
            // — scales BOTH run and walk speed by g_nades_entrap_speed while nade_entrap_time > time). The orb's
            // nade_entrap_touch flags nade_entrap_time on monsters the same as on players.
            if (self.NadeEntrapTime > Now)
                speed *= EntrapSpeed();
            // Stunned: a monster hit by a disability-buff carrier runs/walks slower (QC buffs MonsterMove hook,
            // disability.qc:19-28 — scales BOTH run and walk speed by g_buffs_disability_speed while Stunned).
            var stunnedDef = StatusEffectsCatalog.Stunned;
            if (stunnedDef is not null && StatusEffectsCatalog.Has(self, stunnedDef))
                speed *= DisabilitySpeed();
            MoveSimple(self, forward, speed, flyOrSwim, vzKeep);

            if (Now > st.PainFinished && Now > st.AnimFinished && st.State == 0)
                st.Anim = self.Velocity.Length() > 10f ? (running ? MonsterAnim.Run : MonsterAnim.Walk) : MonsterAnim.Idle;
        }
        else
        {
            BrakeSimple(self, st.StopSpeed, flyOrSwim);
            if (Now > st.AnimFinished && Now > st.PainFinished && st.State == 0 && self.Velocity.Length() <= 30f)
                st.Anim = MonsterAnim.Idle;
        }

        if (!flyOrSwim) self.Velocity.Z = vzKeep;

        // If this path was dangerous, re-pick a wander target next frame (QC).
        if (danger != 0)
        {
            st.LastTrace = Now + 0.3f;
            st.MoveTo = WanderTarget(self, st, self.Origin);
        }
    }

    /// <summary>Port of <c>Monster_Move_Target</c> (sv_monsters.qc): pick the destination for this frame.</summary>
    private static Vector3 MoveTarget(Entity self, MonsterState st)
    {
        // Enemy is always the preferred target.
        if (self.Enemy is not null)
        {
            st.MoveState = MonsterMove_Enemy;
            st.LastTrace = Now + 1.2f;
            return st.MonsterMoveTo != Vector3.Zero
                ? st.MonsterMoveTo
                : (self.Enemy.AbsMin + self.Enemy.AbsMax) * 0.5f;
        }

        switch (st.MoveFlags)
        {
            case MonsterMove_Follow:
                st.MoveState = MonsterMove_Follow;
                st.LastTrace = Now + 0.3f;
                if (st.Follow is not null)
                {
                    if ((self.Origin - st.Follow.Origin).Length() < st.WanderDistance)
                        st.LastTrace = Now + st.WanderDelay;
                    return WanderTarget(self, st, st.Follow.Origin);
                }
                return self.Origin;

            case MonsterMove_SpawnLoc:
                st.MoveState = MonsterMove_SpawnLoc;
                st.LastTrace = Now + 2f;
                return st.SpawnOrigin;

            case MonsterMove_NoMove:
                if (st.MonsterMoveTo != Vector3.Zero) { st.LastTrace = Now + 0.5f; return st.MonsterMoveTo; }
                st.MoveState = MonsterMove_NoMove;
                st.LastTrace = Now + 2f;
                return self.Origin;

            default:
            case MonsterMove_Wander:
                st.MoveState = MonsterMove_Wander;
                if (st.MonsterMoveTo != Vector3.Zero) { st.LastTrace = Now + 0.5f; return st.MonsterMoveTo; }
                st.LastTrace = Now + st.WanderDelay;
                return WanderTarget(self, st, self.Origin);
        }
    }

    /// <summary>Port of <c>Monster_WanderTarget</c> (sv_monsters.qc): a random nearby visible point.</summary>
    private static Vector3 WanderTarget(Entity self, MonsterState st, Vector3 around)
    {
        Vector3 ang = new(self.Angles.X, MathF.Round(Prandom.Float() * 500f), self.Angles.Z);
        Vector3 forward = QMath.Forward(ang);
        Vector3 pos = around + forward * st.WanderDistance;
        bool vertical = ((self.Flags & EntFlags.Fly) != 0 && (self.SpawnFlags & MonsterFlag_FlyVertical) != 0)
                        || (self.Flags & EntFlags.Swim) != 0;
        if (vertical)
        {
            pos.Z = Prandom.Float() * 200f;
            if (Prandom.Float() >= 0.5f) pos.Z = -pos.Z;
        }
        // Truncate so we don't try to walk into walls (QC traceline to the candidate).
        TraceResult tr = Api.Trace.Trace(self.Origin + self.ViewOfs, Vector3.Zero, Vector3.Zero, pos,
            MoveFilter.Normal, self);
        return tr.EndPos;
    }

    /// <summary>
    /// movelib_move_simple (common/physics/movelib.qh:36): <c>velocity = velocity*(1-blendrate) +
    /// (newdir*blendrate)*velo</c> with the monster blendrate 0.4 (QC Monster_Move:910). This is the EXACT QC
    /// blend (algebraically <c>velocity + (newdir*velo - velocity)*0.4</c>, which is what we compute), NOT a
    /// frametime approximation — Base's movelib blend is itself frametime-independent (a per-call lerp).
    /// </summary>
    private static void MoveSimple(Entity self, Vector3 forward, float speed, bool flyOrSwim, float vzKeep)
    {
        if (!flyOrSwim) forward.Z = 0f;
        forward = QMath.Normalize(forward);
        // QC: velocity*(1-0.4) + (forward*0.4)*speed  ==  velocity + (forward*speed - velocity)*0.4 (this form).
        Vector3 want = forward * speed;
        Vector3 v = self.Velocity + (want - self.Velocity) * 0.4f;
        self.Velocity = v;
        if (!flyOrSwim) self.Velocity.Z = vzKeep;
    }

    /// <summary>
    /// movelib_brake_simple (common/physics/movelib.qc:163): subtract <paramref name="stopSpeed"/> from the
    /// velocity MAGNITUDE (not a multiplicative decay) and preserve the Z component:
    /// <c>mspeed = max(0, vlen(v) - force); v = normalize(v) * mspeed; v.z = oldz;</c>. This is the exact QC
    /// brake — a flat per-call speed reduction, so a slow monster snaps to rest in one brake call rather than
    /// decaying geometrically. <paramref name="flyOrSwim"/> monsters keep Z in the brake too (their Z is part of
    /// the movement plane), matching QC which restores velocity_z unconditionally.
    /// </summary>
    private static void BrakeSimple(Entity self, float stopSpeed, bool flyOrSwim)
    {
        Vector3 v = self.Velocity;
        float vz = v.Z;                                     // QC: vz = this.velocity.z
        float mspeed = System.Math.Max(0f, v.Length() - stopSpeed); // QC: max(0, vlen(velocity) - force)
        v = QMath.Normalize(v) * mspeed;                    // QC: this.velocity = normalize(velocity) * mspeed
        v.Z = vz;                                           // QC: this.velocity_z = vz
        self.Velocity = v;
        _ = flyOrSwim; // QC restores velocity_z regardless of fly/swim — the param is kept for call-site symmetry
    }

    /// <summary>QC shortangle_f: shortest signed angular difference, in (-180, 180].</summary>
    private static float ShortAngle(float diff)
    {
        diff = AngleMod(diff);
        if (diff > 180f) diff -= 360f;
        return diff;
    }

    /// <summary>QC anglemods: wrap an angle into [0, 360).</summary>
    private static float AngleMod(float a)
    {
        a %= 360f;
        if (a < 0f) a += 360f;
        return a;
    }

    /// <summary>
    /// Port of <c>Monster_Move_2D</c> (sv_monsters.qc:1161): a self-contained "walk straight ahead in my facing
    /// direction, reverse 180° when I hit a wall, another monster, or a ledge (unless allow_jumpoff)" mover. It
    /// is a public Base API (sv_monsters.qh:111) with NO stock caller — mapper/custom monsters drive it directly
    /// (e.g. a side-scroller-style patrol). Ported faithfully for parity/registry completeness so a custom port
    /// monster has the same building block. <paramref name="mspeed"/> is the forward ground speed;
    /// <paramref name="allowJumpoff"/> lets the monster walk off ledges instead of turning around.
    ///
    /// The movement gate (QC :1163-1172) brakes + idles when the match isn't live: game_stopped / round not
    /// started / before game_starttime / before spawn_time / draggedby / campaign-bots-not-ready. The port folds
    /// game_stopped+round-not-started+prematch into <see cref="MatchHalted"/> and the campaign hold into
    /// <see cref="CampaignMovementHeld"/> (same seams the main <see cref="Move"/> uses); draggedby is a deferred
    /// host concern (no .draggedby field on the headless Entity, consistent with the rest of the framework).
    /// </summary>
    public static void Move2D(Entity self, float mspeed, bool allowJumpoff)
    {
        MonsterState? st = StateOf(self);
        if (st is null) return;

        // QC :1163 — the not-live brake-and-idle gate.
        if (MatchHalted() || CampaignMovementHeld() || Now < st.SpawnTime)
        {
            // QC :1168 — only show the idle anim once the spawn animation has finished.
            if (Now >= st.SpawnTime) st.Anim = MonsterAnim.Idle;
            BrakeSimple(self, 0.6f, false); // QC :1170 movelib_brake_simple(this, 0.6)
            DriveAnimFrame(self, st);
            return;
        }

        // QC :1174-1186 — trace 32u forward; reverse on a wall or another monster, but NOT on a player (we walk
        // INTO a player to attack it).
        Vector3 a = self.Origin + self.ViewOfs;       // CENTER_OR_VIEWOFS(this)
        Vector3 forward = QMath.Forward(self.Angles);
        Vector3 b = a + forward * 32f;
        TraceResult tr = Api.Trace.Trace(a, Vector3.Zero, Vector3.Zero, b, MoveFilter.Normal, self);

        bool reverse = tr.Fraction != 1f;
        if (tr.Ent is not null && (tr.Ent.Flags & EntFlags.Client) != 0) reverse = false;
        if (tr.Ent is not null && (tr.Ent.Flags & EntFlags.Monster) != 0) reverse = true;

        // QC :1188-1193 — ledge check: if not allowed to jump off and we're grounded, trace down 32u from the
        // forward point; nothing there (fraction == 1) means a ledge ahead, so reverse.
        if (!allowJumpoff && self.OnGround)
        {
            TraceResult down = Api.Trace.Trace(b, Vector3.Zero, Vector3.Zero, b - new Vector3(0f, 0f, 32f),
                MoveFilter.Normal, self);
            if (down.Fraction == 1f) reverse = true;
        }

        // QC :1195-1199 — flip 180° and re-derive the facing vector.
        if (reverse)
        {
            Vector3 ang = self.Angles;
            ang.Y = AngleMod(ang.Y - 180f);
            self.Angles = ang;
            forward = QMath.Forward(self.Angles);
        }

        // QC :1201 — movelib_move_simple_gravity(this, v_forward, mspeed, 1): blend toward forward*mspeed at
        // blendrate 1 (i.e. set velocity = forward*mspeed) but ONLY while on the ground; gravity owns Z.
        if (self.OnGround)
        {
            float vz = self.Velocity.Z;
            Vector3 fwd = forward; fwd.Z = 0f;
            self.Velocity = QMath.Normalize(fwd) * mspeed; // blendrate 1 => velocity = newdir*velo
            self.Velocity.Z = vz;
        }

        // QC :1203-1209 — walk anim when actually moving, else idle (gated by the pain/attack windows).
        if (Now > st.PainFinished && Now > st.AttackFinished)
            st.Anim = self.Velocity.Length() > 10f ? MonsterAnim.Walk : MonsterAnim.Idle;
        DriveAnimFrame(self, st);
    }

    // ====================================================================================
    // Attack dispatch (Monster_Attack_Check, sv_monsters.qc)
    // ====================================================================================

    /// <summary>
    /// Port of <c>Monster_Attack_Check</c> (sv_monsters.qc): when an enemy is present and the attack cooldown
    /// has elapsed, face it and hand off to the monster's <see cref="Monster.Attack"/>. QC selects the attack
    /// TYPE (melee vs ranged) here from the enemy distance and passes it into <c>monster_attackfunc</c>; our
    /// <see cref="Monster.Attack"/> re-derives that split itself from the same distance, so this routine only
    /// gates on enemy/cooldown and aims. Per-attack cooldowns are set inside each monster's Attack.
    /// </summary>
    public static void AttackCheck(Entity self, MonsterState st)
    {
        Entity? targ = self.Enemy;
        if (targ is null) return;
        if (Now < st.AttackFinished) return;

        FaceTarget(self, targ);   // QC monster_makevectors before attacking
        st.Def.Attack(self, targ);
    }

    /// <summary>Aim the monster at a target's bbox center (QC monster_makevectors: sets v_angle from delta).</summary>
    public static void FaceTarget(Entity self, Entity targ)
    {
        Vector3 mid = targ.Origin + (targ.Mins + targ.Maxs) * 0.5f;
        Vector3 dir = mid - (self.Origin + self.ViewOfs);
        if (dir.LengthSquared() > 0f)
            self.Angles = QMath.VecToAngles(dir);
    }

    // ====================================================================================
    // The per-think driver (Monster_Think, sv_monsters.qc)
    // ====================================================================================

    /// <summary>
    /// Port of <c>Monster_Think</c> (sv_monsters.qc): the once-per-frame brain. Acquire/validate an enemy
    /// (throttled to ~1s like QC), run the descriptor's per-think hook (<see cref="Monster.Think"/> stand-in,
    /// invoked by the concrete monster before calling this), move toward the goal, then try to attack.
    ///
    /// Concrete monsters call <see cref="RunThink"/> from their <see cref="Monster.Think"/> override after
    /// doing any monster-specific per-frame logic (e.g. the Mage's heal/shield decisions).
    /// </summary>
    public static void RunThink(Entity self, MonsterState st)
    {
        // Lifetime expiry: a timed monster suicides when its clock runs out (QC monster_lifetime kill).
        if (st.Lifetime != 0f && Now >= st.Lifetime && self.DeadState == DeadFlag.No)
        {
            Combat.Damage(self, self, self, self.Health + st.MaxHealth, DeathTypes.Kill, self.Origin, Vector3.Zero);
            return;
        }

        // Dead monsters run the dead-think branch (corpse fade / respawn timing), not the live brain.
        if (self.DeadState != DeadFlag.No || self.Health <= 0f)
        {
            DeadThink(self, st);
            DriveAnimFrame(self, st); // die1/die2 corpse pose (the descriptor maps the phase to a frame group)
            return;
        }

        // Keep the edict's .damageforcescale in lockstep with the per-monster value so the generic apply-push
        // (DamageSystem.ApplyKnockback, QC damage.qc:671) uses the right scale (descriptors set st.DamageForceScale
        // after Setup; e.g. the zombie drops it to ~0 during the spawn anim and restores it here in Think).
        self.DamageForceScale = st.DamageForceScale;

        // Fire any deferred/combo attack actions that have come due (QC Monster_Delay).
        RunDelayedActions(self, st);
        // Expire a timed mage shield, restoring armor (mirrors STATUSEFFECT_Shield expiry).
        RunStatusTimers(self, st);

        // Don't act until the spawn animation has finished (QC .spawn_time gate). The spawn pose set in Setup
        // (QC mr_setup setanim(anim_spawn)) still plays during this window — stamp it onto the networked Frame.
        if (Now < st.SpawnTime)
        {
            DriveAnimFrame(self, st);
            return;
        }

        // Frozen: drop the enemy and don't think (QC Monster_Think frozen branch); else re-acquire.
        bool frozen = StatusEffectsCatalog.Frozen != null && StatusEffectsCatalog.Has(self, StatusEffectsCatalog.Frozen);
        if (frozen)
        {
            self.Enemy = null;
        }
        else if (Now >= st.LastEnemyCheck)
        {
            EnemyCheck(self, st);
            st.LastEnemyCheck = Now + 1f; // check for enemies every second (QC)
        }

        // Clear an expired attack state (QC: state cleared once attack_finished passes). A ranged leap also
        // clears once the monster is back on the ground.
        if (st.State == MonsterState_AttackRanged && (self.Flags & EntFlags.OnGround) != 0)
        {
            st.State = 0;
            self.Touch = (s, o) => Touch(s, o);
        }
        if (st.State != 0 && Now >= st.AttackFinished)
            st.State = 0;

        Move(self, st);
        AttackCheck(self, st);
        // CSQC monster animation frame playback (Monster_Anim / CSQCMODEL_AUTOUPDATE): translate the logical
        // anim PHASE (maintained in st.Anim by Move/MarkPain/MarkDead) into the descriptor's concrete MD3
        // frame group and stamp it onto the networked Entity.Frame so the client ModelAnimator plays it.
        DriveAnimFrame(self, st);
    }

    /// <summary>
    /// Map the monster's logical <see cref="MonsterAnim"/> phase to its descriptor's concrete MD3 frame-group
    /// start index (QC <c>mr_anim</c>) and write it to the networked <see cref="Entity.Frame"/>, so the client
    /// model actually plays the named group (CSQCMODEL_AUTOUPDATE). Descriptors that haven't declared their
    /// frame table return <c>null</c> and leave <see cref="Entity.Frame"/> untouched (no regression).
    /// </summary>
    private static void DriveAnimFrame(Entity self, MonsterState st)
    {
        float? frame = st.Def.AnimFrame(ToPhase(st.Anim), st.DeathLanded, st.AnimVariant);
        if (frame.HasValue)
            self.Frame = frame.Value;
    }

    /// <summary>Bridge the internal <see cref="MonsterAnim"/> to the descriptor-layer <see cref="MonsterAnimPhase"/>.</summary>
    private static MonsterAnimPhase ToPhase(MonsterAnim a) => a switch
    {
        MonsterAnim.Walk => MonsterAnimPhase.Walk,
        MonsterAnim.Run => MonsterAnimPhase.Run,
        MonsterAnim.Attack => MonsterAnimPhase.Attack,
        MonsterAnim.Pain => MonsterAnimPhase.Pain,
        MonsterAnim.Death => MonsterAnimPhase.Death,
        MonsterAnim.Spawn => MonsterAnimPhase.Spawn,
        MonsterAnim.Block => MonsterAnimPhase.Block,
        MonsterAnim.BlockEnd => MonsterAnimPhase.BlockEnd,
        MonsterAnim.Shoot => MonsterAnimPhase.Shoot,
        _ => MonsterAnimPhase.Idle,
    };

    /// <summary>
    /// Port of <c>Monster_Touch</c> (sv_monsters.qc): a non-monster attackable that bumps into us and isn't
    /// already our enemy becomes our enemy (skipping the facing check). Concrete monsters can install this as
    /// their touch when not mid-attack so walking into a player aggroes the monster.
    /// </summary>
    public static void Touch(Entity self, Entity other)
    {
        if (other is null) return;
        if (self.Enemy == other) return;
        if ((other.Flags & EntFlags.Monster) != 0) return;
        if (Now < (StateOf(self)?.SpawnTime ?? 0f)) return;
        if (ValidTarget(self, other, true))
            self.Enemy = other;
    }

    /// <summary>Fire deferred wind-up actions and queued combo swings whose time has come (QC Monster_Delay).</summary>
    private static void RunDelayedActions(Entity self, MonsterState st)
    {
        if (st.DelayedAction is not null && Now >= st.DelayedActionTime)
        {
            var act = st.DelayedAction;
            st.DelayedAction = null;
            // QC Monster_Delay_Action re-validates the enemy + re-aims before firing.
            if (ValidTarget(self, self.Enemy, false))
            {
                if (self.Enemy is not null) FaceTarget(self, self.Enemy);
                act(self);
            }
        }

        if (st.ComboSwings > 0 && Now >= st.NextComboSwing)
        {
            st.ComboSwings--;
            st.NextComboSwing = Now + 0.5f;
            if (ValidTarget(self, self.Enemy, false) && self.Enemy is not null)
            {
                FaceTarget(self, self.Enemy);
                // QC M_Golem_Attack_Swing animtime = 0.8 (the per-swing attack_finished window); the swing
                // CADENCE stays 0.5s (NextComboSwing). The melee voice cue already fired once at dispatch
                // (QueueCombo), so suppress the per-swing cue here (QC plays it per dispatch, not per swing).
                MeleeAttack(self, st, st.ComboSwingDamage, st.AttackRange, st.ComboSwingAnimTime,
                    st.ComboDeathType, freeze: false, playMeleeCue: false);
            }
        }
    }

    /// <summary>Expire timed defensive effects: a mage shield restores armor when STATUSEFFECT_Shield ends.</summary>
    private static void RunStatusTimers(Entity self, MonsterState st)
    {
        if (st.ShieldExpire != 0f && Now >= st.ShieldExpire)
        {
            st.ShieldExpire = 0f;
            self.ArmorValue = st.ShieldRestoreArmor;
            if (StatusEffectsCatalog.Has(self, MonsterFramework.Shield))
                StatusEffectsCatalog.Remove(self, MonsterFramework.Shield);
        }
    }

    /// <summary>
    /// Port of <c>Monster_Dead_Think</c> (sv_monsters.qc): once the corpse lifetime elapses, either respawn
    /// (Monster_Respawn_Check) or fade out. Called from <see cref="RunThink"/> on the dead branch.
    /// </summary>
    private static void DeadThink(Entity self, MonsterState st)
    {
        if (st.Lifetime != 0f && Now >= st.Lifetime)
        {
            if (RespawnCheck(st))
                Respawn(self, st);
            else
            {
                st.Lifetime = 0f;
                // Fade + remove after a short delay (QC SUB_SetFade(time+3)).
                float removeAt = Now + 3f;
                self.Think = s => { s.NextThink = Now; if (Now >= removeAt) { Forget(s); Api.Entities.Remove(s); } };
                self.NextThink = Now;
            }
        }
    }

    // ====================================================================================
    // Melee + projectile attack helpers (Monster_Attack_Melee + projectile spawns)
    // ====================================================================================

    /// <summary>
    /// Port of <c>Monster_Attack_Melee</c> (sv_monsters.qc): traceline forward by <paramref name="range"/>
    /// and, if it hits a damageable entity, deal <paramref name="damage"/> * skillmod through the real
    /// damage pipeline (<see cref="Combat.Damage"/>) with outward knockback. Sets the monster's melee state
    /// + cooldown. Returns true if the swing connected with a damageable entity.
    /// </summary>
    public static bool MeleeAttack(Entity self, MonsterState st, float damage, float range,
        float animTime, string deathType, bool freeze = true, bool playMeleeCue = true)
    {
        if (freeze) st.State = MonsterState_AttackMelee;
        st.AttackFinished = Now + animTime;

        Vector3 eye = self.Origin + self.ViewOfs;
        Vector3 forward = QMath.Forward(self.Angles);
        Vector3 end = eye + forward * range;
        TraceResult tr = Api.Trace.Trace(eye, Vector3.Zero, Vector3.Zero, end, MoveFilter.Normal, self);

        Entity? hit = tr.Ent;
        if (hit is not null && hit.TakeDamage != DamageMode.No)
        {
            Vector3 force = QMath.Normalize(hit.Origin - self.Origin);
            Combat.Damage(hit, self, self, damage * SkillMod(st), deathType, hit.Origin, force);
            // QC Monster_Attack_Check:468/478 plays monstersound_melee once per attack DISPATCH on success — for
            // a single-swing melee monster (zombie/spider) the MeleeAttack call IS the dispatch, so play it here;
            // the golem combo fires its dispatch-level cue in QueueCombo and passes playMeleeCue=false per swing.
            if (playMeleeCue)
                MonsterSound(self, st, "melee", 0f, false, SoundChannel.Voice);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Port of <c>Monster_Attack_Leap_Check</c> + <c>Monster_Attack_Leap</c> (sv_monsters.qc): if the monster
    /// is grounded, off cooldown, alive and not already attacking, and a ballistic toss with <paramref name="vel"/>
    /// would land on the enemy, launch it and install <paramref name="touchFunc"/> as the contact handler.
    /// Sets the ranged state + cooldown. Returns true if the leap was performed.
    /// </summary>
    public static bool Leap(Entity self, MonsterState st, Vector3 vel, EntityTouch touchFunc, float animTime,
        MonsterAnim leapAnim = MonsterAnim.Attack)
    {
        if (st.State != 0) return false;
        if ((self.Flags & EntFlags.OnGround) == 0) return false;
        if (self.Health <= 0f || self.DeadState != DeadFlag.No) return false;
        if (Now < st.AttackFinished) return false;

        // QC tracetoss(this, this) with the candidate velocity: does the arc reach our enemy?
        if (self.Enemy is not null)
        {
            TraceResult tr = TraceToss(self, vel);
            if (tr.Ent != self.Enemy && tr.Ent is not null) return false; // would hit something else first
        }

        st.AttackFinished = Now + animTime;
        st.AnimFinished = Now + animTime;
        st.State = MonsterState_AttackRanged;
        st.Anim = leapAnim; // QC Monster_Attack_Leap(actor, actor.anim_shoot, ...): the leap plays the shoot group
        self.Touch = touchFunc;
        Vector3 o = self.Origin; o.Z += 1f; Api.Entities.SetOrigin(self, o);
        self.Velocity = vel;
        self.Flags &= ~EntFlags.OnGround;
        return true;
    }

    /// <summary>
    /// Approximate QC <c>tracetoss</c>: simulate the ballistic arc of <paramref name="self"/> launched at
    /// <paramref name="vel"/> under gravity and return the first thing it would hit. Stepped traceline
    /// integration (deterministic, headless) — enough to gate a leap on "will I land on my enemy?".
    ///
    /// [warpzones.combat.traceextras] WARPZONE-AWARE, matching QC <c>WarpZone_TraceToss_ThroughZone</c>
    /// (lib/warpzone/common.qc): each integration segment is swept with <see cref="WarpzoneTrace.TraceBoxWarpzone"/>
    /// so a toss arc that crosses a seamless portal continues on the far side — the segment endpoint comes back in
    /// the FINAL (post-portal) frame and the running velocity is rotated through the accumulated portal chain, so a
    /// monster's leap-aim correctly probes an enemy reached through a warpzone. With no warpzones in the world the
    /// warpzone-aware sweep collapses to exactly one plain <see cref="ITraceService.Trace"/> per step, so the arc is
    /// byte-for-byte unchanged on a non-warpzone map (the common case).
    /// </summary>
    private static TraceResult TraceToss(Entity self, Vector3 vel)
    {
        float g = Cvar("sv_gravity", 800f) * (self.Gravity != 0f ? self.Gravity : 1f);
        Vector3 pos = self.Origin;
        Vector3 v = vel;
        const float step = 0.05f;
        for (int i = 0; i < 80; i++) // ~4s of flight
        {
            Vector3 next = pos + v * step;
            // QC WarpZone_TraceToss runs each ballistic segment through WarpZone_TraceBox: a portal crossing
            // transforms the remaining arc to the far side. AmbientManager is null on a non-warpzone world, where
            // TraceBoxWarpzone is a single plain trace with an identity transform (arc unchanged).
            WarpzoneTraceResult wr = Api.Trace.TraceBoxWarpzone(pos, self.Mins, self.Maxs, next,
                MoveFilter.Normal, self);
            TraceResult tr = wr.Trace;
            if (tr.Fraction < 1f) return tr;
            // The segment endpoint is already in the FINAL portal frame; rotate the integrated velocity through the
            // same portal chain so gravity keeps pulling "down" correctly on the far side (QC re-applies the
            // warpzone transform to velocity across the seam). Identity chain == no-op on a non-warpzone map.
            pos = tr.EndPos;
            if (wr.ZonesCrossed > 0)
                v = wr.Transform.TransformDirection(v);
            v.Z -= g * step;
        }
        return TraceResult.Miss(pos);
    }

    /// <summary>
    /// Queue a deferred attack that fires after a wind-up (QC <c>Monster_Delay</c> with repeat_count 1): the
    /// monster freezes in its attack state for <paramref name="windUp"/> seconds, then — if the enemy is
    /// still valid — <paramref name="action"/> runs (e.g. the golem's smash landing, the wyvern's fireball).
    /// </summary>
    public static void QueueDelayedAttack(Entity self, MonsterState st, float windUp, float totalLock,
        Action<Entity> action)
    {
        st.State = MonsterState_AttackMelee;
        st.AttackFinished = Now + totalLock;
        st.AnimFinished = Now + totalLock;
        st.Anim = MonsterAnim.Attack;
        st.DelayedAction = action;
        st.DelayedActionTime = Now + windUp;
    }

    /// <summary>
    /// Queue a multi-swing melee combo (QC golem <c>Monster_Delay(this, swing_cnt, 0.5, …)</c>): lock the
    /// monster for the combo duration and land <paramref name="swings"/> melee hits 0.5s apart, handled in
    /// <see cref="RunDelayedActions"/>. <paramref name="perSwingAnimTime"/> is each swing's animtime (QC
    /// M_Golem_Attack_Swing passes 0.8 to Monster_Attack_Melee), distinct from the 0.5s swing cadence.
    /// </summary>
    public static void QueueCombo(Entity self, MonsterState st, int swings, float perSwingDamage, string deathType,
        float perSwingAnimTime = 0.5f)
    {
        swings = System.Math.Clamp(swings, 1, 3);
        st.State = MonsterState_AttackMelee;
        st.ComboSwings = swings;
        st.ComboSwingDamage = perSwingDamage;
        st.ComboDeathType = deathType;
        st.ComboSwingAnimTime = perSwingAnimTime;
        st.NextComboSwing = Now + 0.5f;
        // QC: anim_finished = attack_finished_single[0] = time + 0.5 * swing_cnt (the dispatch-level combo lock).
        st.AttackFinished = Now + 0.5f * swings;
        st.AnimFinished = st.AttackFinished;
        st.Anim = MonsterAnim.Attack;
        // QC M_Golem_Attack MELEE: setanim(random() >= 0.5 ? anim_melee2 : anim_melee3) — pick the swing variant
        // once at dispatch (descriptors with a single attack group ignore this; the golem alternates 4/5).
        st.AnimVariant = MonsterRandom.Next() >= 0.5f ? 1 : 0;
        // QC Monster_Attack_Check:468 plays monstersound_melee ONCE per attack DISPATCH on attack_success==1
        // (not per connecting swing). The golem's MELEE branch returns true, so the cue fires here, at dispatch.
        MonsterSound(self, st, "melee", 0f, false, SoundChannel.Voice);
    }

    /// <summary>
    /// Spawn a monster projectile (the shared base behind the per-monster ranged spawns: mage spike, wyvern
    /// fireball, spider web, golem lightning). Creates an owned entity flying along <paramref name="dir"/> at
    /// <paramref name="speed"/>, exploding (radius damage via <see cref="WeaponSplash.RadiusDamage"/>) on
    /// contact with a damageable entity or after <paramref name="lifetime"/> seconds.
    ///
    /// The deep per-projectile behaviours are layered on through the optional hooks: <paramref name="onExplode"/>
    /// fires after the blast (the wyvern's burning, the spider's web slow, the golem's chained zaps);
    /// <paramref name="onThink"/> runs each frame for guided flight (the mage spike's homing); and
    /// <paramref name="shootableHealth"/> &gt; 0 makes the projectile itself damageable so it can be shot down
    /// before it lands (the golem lightning chunk, W_PrepareExplosionByDamage), detonating early on death.
    /// </summary>
    public static Entity SpawnProjectile(Entity owner, MonsterState st, Vector3 dir, float speed,
        float damage, float edgeDamage, float radius, float force, string deathType,
        MoveType moveType = MoveType.FlyMissile, float lifetime = 5f, Vector3? sizeMin = null, Vector3? sizeMax = null,
        Action<Entity>? onExplode = null, Action<Entity>? onThink = null, float shootableHealth = 0f,
        float bounceFactor = 0f, float bounceStop = 0f, bool makeTrigger = false)
    {
        Vector3 mn = sizeMin ?? new Vector3(-4, -4, -4);
        Vector3 mx = sizeMax ?? new Vector3(4, 4, 4);

        Entity proj = Api.Entities.Spawn();
        proj.ClassName = "monster_projectile";
        proj.Owner = owner;
        proj.NetName = st.Def.NetName;
        proj.MoveType = moveType;
        // QC .bouncefactor / .bouncestop for MOVETYPE_BOUNCE projectiles (spider web 0.3/0.05, golem zap
        // 0.5/0.075). The engine MoveType.Bounce integrator reads these off the entity (PhysicsToss); a
        // zero leaves the engine on its built-in defaults (0.5 / 60/800), so passing 0f is a faithful no-op.
        proj.BounceFactor = bounceFactor;
        proj.BounceStop = bounceStop;
        // QC golem/spider PROJECTILE_MAKETRIGGER (SOLID_CORPSE + dphitcontentsmask SOLID|BODY|CORPSE): transparent
        // to the firer's movement so the monster's own projectile can't collide with / detonate on it. Opt-in
        // because not every caller of this shared spawner uses it — the mage spike is SOLID_BBOX (mage.qc:224)
        // and the wyvern fireball is SOLID_TRIGGER (wyvern.qc:36), both left as plain SOLID_BBOX here.
        if (makeTrigger)
            Projectiles.MakeTrigger(proj);
        else
            proj.Solid = Solid.BBox;
        proj.Flags = EntFlags.Item; // QC FL_PROJECTILE marker (no dedicated flag here)
        proj.GroundEntity = null;
        Api.Entities.SetSize(proj, mn, mx);

        Vector3 muzzle = owner.Origin + owner.ViewOfs + QMath.Normalize(dir) * 14f;
        Api.Entities.SetOrigin(proj, muzzle);
        proj.Velocity = QMath.Normalize(dir) * speed;
        proj.Angles = QMath.VecToAngles(proj.Velocity);

        // Shootable projectile (golem lightning chunk): take damage, detonate when destroyed.
        if (shootableHealth > 0f)
        {
            proj.TakeDamage = DamageMode.Yes;
            proj.Health = shootableHealth;
        }

        float deathTime = Now + lifetime;
        bool exploded = false;

        void Explode(Entity p)
        {
            if (exploded) return;
            exploded = true;
            p.Touch = null;
            p.Think = null;
            // Carry the projectile's special deathtype (mage spike / wyvern fireball / spider web / golem zap)
            // through the blast so a kill routes to the monster obituary line, not a generic weapon line.
            WeaponSplash.RadiusDamage(p, p.Origin, damage, edgeDamage, radius, p.Owner, 0, force, deathTag: deathType);
            onExplode?.Invoke(p); // burning / web slow / chained zaps (per-monster)
            // Drop the owner's one-spike-at-a-time reference if this was it (QC .mage_spike = NULL).
            var os = StateOf(p.Owner!);
            if (os is not null && os.ActiveSpike == p) os.ActiveSpike = null;
            Forget(p);
            Api.Entities.Remove(p);
        }

        proj.Touch = (self, other) =>
        {
            if (other == self.Owner) return;
            Explode(self);
        };
        proj.Think = self =>
        {
            self.NextThink = Now;
            // Shot down before it landed (Health drained by damage): detonate now.
            if (self.TakeDamage == DamageMode.Yes && self.Health <= 0f) { Explode(self); return; }
            if (onThink is not null)
            {
                onThink(self);
                if (self.IsFreed) return; // homing may have detonated us
                // Guidance may have requested detonation (set TakeDamage=Yes + Health<=0): honor it now.
                if (self.TakeDamage == DamageMode.Yes && self.Health <= 0f) { Explode(self); return; }
            }
            if (Now > deathTime) Explode(self);
        };
        proj.NextThink = Now;

        return proj;
    }

    // ====================================================================================
    // Pain + Death (Monster_Damage / Monster_Dead, sv_monsters.qc)
    // ====================================================================================

    /// <summary>
    /// QC <c>ITEM_DAMAGE_NEEDKILL(dt)</c> (server/items.qh:123): the "trap" deathtypes that bypass invulnerability
    /// — <c>DEATH_HURTTRIGGER</c> (the port's <see cref="DeathTypes.Void"/>), <c>DEATH_SLIME</c>, <c>DEATH_LAVA</c>,
    /// <c>DEATH_SWAMP</c>. Kept as a private helper here (rather than on the shared <see cref="DeathTypes"/>) since
    /// it is only needed by the monster INVINCIBLE gate (QC Monster_Damage:1085).
    /// </summary>
    private static bool NeedsKill(string? deathType)
    {
        string b = DeathTypes.BaseOf(deathType);
        return b == DeathTypes.Void || b == DeathTypes.Slime || b == DeathTypes.Lava || b == DeathTypes.Swamp;
    }

    /// <summary>
    /// Pain reaction (the take-damage path of <c>Monster_Damage</c>, sv_monsters.qc): apply self-knockback
    /// scaled by .damageforcescale, set a brief pain window that gates the pain anim phase, play the hurt
    /// sound, and — when health hits zero — run the death path. A <see cref="MonsterFramework.SpawnShield"/>
    /// or the INVINCIBLE flag soaks non-kill damage. Returns the damage actually taken (0 if blocked). Call
    /// this from the host's damage hook for monster entities; concrete monsters add their own pain anim flavor.
    /// </summary>
    public static float MarkPain(Entity self, MonsterState st, float take, Entity? attacker, string deathType,
        Vector3 force)
    {
        // Invincible / spawn-shielded monsters ignore non-explicit-kill damage (QC Monster_Damage:1085-1087 guards).
        bool isKill = deathType == DeathTypes.Kill;
        // QC:1085 — (spawnflags & INVINCIBLE) && deathtype != DEATH_KILL && !ITEM_DAMAGE_NEEDKILL(deathtype).
        // INVINCIBLE soaks ordinary damage, but NOT the kill command NOR the NEEDKILL "trap" deathtypes
        // (void/slime/lava/swamp) — those still hurt an invincible monster.
        if ((self.SpawnFlags & MonsterFlag_Invincible) != 0 && !isKill && !NeedsKill(deathType)) return 0f;
        // QC:1087 — (StatusEffects_active(SpawnShield) && deathtype != DEATH_KILL): the spawn shield soaks every
        // non-kill hit (NEEDKILL included), unlike INVINCIBLE.
        if (!isKill && StatusEffectsCatalog.Has(self, MonsterFramework.SpawnShield)) return 0f;

        if (take > 0f)
        {
            self.Health -= take;
            // QC mr_pain: actor.pain_finished = time + N. The generic monster keeps the zombie's 0.34s; a
            // monster that overrides mr_pain with a wider window (wyvern/golem = 0.5s) exposes it via PainWindow.
            st.PainFinished = Now + st.Def.PainWindow;
            st.Anim = MonsterAnim.Pain;
            // QC mr_pain: setanim(random() >= 0.5 ? anim_pain2 : anim_pain1) — pick a pain variant once per pain
            // event (descriptors with a single pain group ignore this; the golem alternates pain1 '7'/pain2 '8').
            st.AnimVariant = MonsterRandom.Next() >= 0.5f ? 1 : 0;
            // QC Monster_Damage:1101 — Monster_Sound(monstersound_pain, 1.2, true, CH_PAIN): the 1.2s throttle
            // (delaytoo) stops a stream of hits from machine-gunning the pain voice. CH_PAIN(-6) = the auto pain
            // channel so overlapping hurt cues stack rather than cut each other off.
            MonsterSound(self, st, "pain", 1.2f, true, SoundChannel.PainAuto);
        }

        // QC Monster_Damage:1104 — if (this.sprite) WaypointSprite_UpdateHealth(this.sprite, RES_HEALTH). Runs
        // unconditionally (even on a 0-take hit), so the floating health bar tracks every damage event. No-op when
        // healthbars are off (Sprite null).
        UpdateHealthbar(st, self.Health);

        // Pause health regen after a hit (QC sets .dmg_time; the port's regen pause is .pauseregen_finished).
        // (QC's body-impact spamsound + the take>50/>100 extra gib splashes are client VFX/audio — named no-ops.)
        if (take > 0f)
            self.PauseRegenFinished = System.Math.Max(self.PauseRegenFinished,
                Now + Cvar("g_balance_pause_health_regen", 5f));

        // Self-knockback (QC velocity += force * damageforcescale). This is the SECOND push: the generic apply-push
        // in DamageSystem.ApplyKnockback already added a calcpush using the seeded edict .damageforcescale. QC does
        // exactly the same double-add for a pushable monster (recon §8) — do not remove either.
        self.Velocity += force * st.DamageForceScale;

        if (self.Health <= 0f)
        {
            // QC Monster_Damage:1125-1136 — the death tail (run BEFORE the corpse setup clears .enemy).
            if (isKill)
                st.CanDrop = false; // killed by the mobkill command: no loot

            // QC: SUB_UseTargets(this, attacker, this.enemy) — fire the monster's map targets on death (a
            // monster wired to a trigger via .target). Uses .enemy as the QC `trigger` arg, captured here
            // before MarkDead nulls it. (QC also restores target2 = oldtarget2 for respawn; the port has no
            // .oldtarget2 field — a documented minor deferral, only relevant to a redirected-patrol respawn.)
            MapMover.UseTargets(self, attacker, self.Enemy);

            bool gibbed = self.Health <= -100f || isKill; // live-monster gib threshold is the LITERAL -100 (NOT sv_gibhealth)
            MarkDead(self, st, attacker, gibbed, deathType);
        }
        return take;
    }

    /// <summary>
    /// Full <c>Monster_Dead</c> (sv_monsters.qc): turn the monster into a corpse, drop loot, fire the death
    /// hook for scoring, set the corpse lifetime (so the dead-think branch fades or respawns it), and stop
    /// the brain. A gibbed monster is removed shortly after. Mirrors the engine field changes QC made.
    /// </summary>
    public static void MarkDead(Entity self, MonsterState st, Entity? attacker = null, bool gibbed = false,
        string deathType = "")
    {
        st.Lifetime = Now + 5f; // corpse lifetime; the dead-think branch fades/respawns at this time

        // Loot drop (monster_dropitem) — except when killed by the explicit kill command.
        if (deathType == DeathTypes.Kill) st.CanDrop = false;
        MonsterFramework.DropItem(self, st, attacker);

        // Death sound (QC Monster_Dead:1044 — monstersound_death, no throttle).
        MonsterSound(self, st, "death", 0f, false, SoundChannel.Voice);

        // QC Monster_Dead:1046 — ++monsters_killed, but ONLY for a NATURAL (map-placed, first-life) monster.
        // Command/wave-spawned (MONSTERFLAG_SPAWNED) and already-respawned (MONSTERFLAG_RESPAWNED) kills never
        // count toward the killed total fed to the scoreboard map-stats row.
        bool natural = !(st.Spawned || st.Respawned);
        if (natural)
            MonstersKilled++;

        // QC Monster_Dead:1049-1051 — a PLAYER who kills this monster scores g_monsters_score_kill, awarded for a
        // NATURAL kill OR (when g_monsters_score_spawned is set) a command/wave-spawned one too. GameRules_scoring_add
        // (sv_rules.qh:85) = PlayerScore_Add(attacker, SP_SCORE, +value); IS_PLAYER maps to the Client flag.
        if (attacker is not null && (attacker.Flags & EntFlags.Client) != 0
            && (Cvar("g_monsters_score_spawned", 0f) != 0f || natural))
        {
            int kill = (int)MathF.Round(Cvar("g_monsters_score_kill", 0f));
            if (kill != 0)
                Scoring.GameScores.AddToPlayer(attacker, Scoring.GameScores.Score, kill);
        }

        // Scoring / obituary hook (gametypes award the frag).
        var death = new DeathEvent { Victim = self, Attacker = attacker, Inflictor = self, DeathType = deathType };
        Combat.Death.Call(ref death);

        self.DeadState = DeadFlag.Dead;
        self.Solid = Solid.Corpse;
        self.TakeDamage = DamageMode.Aim;
        self.Enemy = null;
        self.Effects = 0;
        self.MoveType = MoveType.Toss;
        self.Touch = (s, o) => Touch(s, o); // reset in case the monster was mid-leap
        st.State = 0;
        st.AttackFinished = 0f;
        st.Anim = MonsterAnim.Death;
        // QC mr_death: setanim(random() > 0.5 ? anim_die1 : anim_die2) — the descriptor rolls its death
        // variant once at the moment of death (e.g. zombie picks die1/die2 randomly). The result is stored
        // in DeathLanded so DriveAnimFrame passes it as the die2 flag to AnimFrame. Monsters that use the
        // falling→landed split (wyvern) return false here (die1 = falling) and set DeathLanded=true later
        // via their dead-think; monsters that want an immediate random pick (zombie) return the roll directly.
        st.DeathLanded = st.Def.RollDeathVariant();

        if ((self.Flags & (EntFlags.Fly | EntFlags.Swim)) == 0)
            self.Velocity = Vector3.Zero;

        // QC Monster_Damage:1134 / Monster_Dead:961 — WaypointSprite_Kill(this.sprite): remove the floating
        // health-bar waypoint sprite when the monster dies. No-op when healthbars are off (Sprite null).
        Waypoints.WaypointSprites.Kill(st.Sprite);
        st.Sprite = null;

        // QC Monster_Damage:1136 — MUTATOR_CALLHOOK(MonsterDies, this, attacker, deathtype). The port's
        // MonsterDies equivalent is the direct NadeBonus.OnMonsterDies (there is no MutatorHooks.MonsterDies
        // chain): a player who kills a NATURAL enemy monster earns a minor bonus nade. The method guards the
        // attacker-is-client / spawned / same-team cases internally, so we pass monsterWasSpawned faithfully.
        Nades.NadeBonus.OnMonsterDies(attacker, self, monsterWasSpawned: !natural);

        if (gibbed)
        {
            // Already in pieces: remove almost immediately (QC SUB_Remove at time+0.1).
            float removeAt = Now + 0.1f;
            self.Think = s => { s.NextThink = Now; if (Now >= removeAt) { Forget(s); Api.Entities.Remove(s); } };
            self.NextThink = Now;
        }
    }

    // ====================================================================================
    // The monster damage event (Monster_Damage front + Monster_Dead_Damage corpse path)
    // ====================================================================================

    /// <summary>
    /// The monster's <c>.event_damage</c> (QC <c>Monster_Damage</c>, sv_monsters.qc:1083), installed onto
    /// <see cref="Entity.GtEventDamage"/> in <see cref="Setup"/>. <see cref="Damage.DamageSystem.EventDamage"/>
    /// routes every non-player edict with a <c>GtEventDamage</c> here and returns, so a monster victim runs the
    /// monster pain/death path instead of being treated as a player.
    ///
    /// QC swaps <c>.event_damage</c> to <c>Monster_Dead_Damage</c> once the monster becomes a corpse; the port
    /// keeps ONE shim installed and dispatches on <see cref="Entity.DeadState"/> instead (faithful + simpler):
    /// a live monster runs the armor-split + <see cref="MarkPain"/>; a corpse runs <see cref="MonsterDeadDamage"/>.
    /// The signature mirrors <c>GtEventDamage</c>: <c>(self, inflictor, attacker, deathtype, damage, hitloc, force)</c>.
    /// </summary>
    public static void MonsterEventDamage(Entity self, Entity? inflictor, Entity? attacker, string deathType,
        float damage, Vector3 hitLoc, Vector3 force)
    {
        _ = inflictor; // (parity slot: QC Monster_Damage receives the inflictor but only uses attacker/hitloc/force)
        MonsterState? st = StateOf(self);
        if (st is null) return; // not an initialized monster (shouldn't happen for an installed shim)

        // Corpse hit (QC: this.event_damage == Monster_Dead_Damage after death) — the dead/ungibbed body still
        // takes damage and gibs at a SECOND threshold (-50). No pain/death re-fires.
        if (self.DeadState != DeadFlag.No)
        {
            MonsterDeadDamage(self, st, attacker, damage, hitLoc, force);
            return;
        }

        // QC Monster_Damage:1085-1089 — the invulnerability/spawn-shield/ridden-fall gate runs in MarkPain
        // (which mirrors the INVINCIBLE + SpawnShield checks). Here we only do the armor split QC does at line
        // 1091 before calling mr_pain.
        //
        // QC Monster_Damage:1091 — healtharmor_applydamage(100, RES_ARMOR/100, deathtype, damage). The monster's
        // armor is a small FRACTION (~0.2..0.9 — see Setup/the zombie block), so armorblock = ArmorValue/100 ≈
        // 0.002..0.009 and the block is near-cosmetic BY DESIGN. Do NOT treat ArmorValue as a normal 0..1 block.
        float armorBlock = DeathTypes.BypassesArmor(deathType) ? 0f : self.ArmorValue / 100f; // drown/armorpierce zero it
        float save = System.Math.Clamp(damage * armorBlock, 0f, 100f); // bound(0, damage*armorblock, 100)
        float take = System.Math.Clamp(damage - save, 0f, damage);     // bound(0, damage - save, damage)

        MarkPain(self, st, take, attacker, deathType, force);
    }

    /// <summary>
    /// Port of <c>Monster_Dead_Damage</c> (sv_monsters.qc:1018) — the corpse damage path. The dead, NOT-yet-gibbed
    /// body subtracts the raw damage and, once its health drops past a SECOND threshold (-50, distinct from the
    /// live -100), gibs: a short-fuse removal at time+0.1 and the shim is disarmed so it can't re-fire. The two
    /// <c>Violence_GibSplash_At</c> calls are client VFX (named no-op here).
    /// </summary>
    public static void MonsterDeadDamage(Entity self, MonsterState st, Entity? attacker, float damage,
        Vector3 hitLoc, Vector3 force)
    {
        _ = st; _ = attacker; _ = hitLoc; _ = force; // (parity slots: QC passes these to Violence_GibSplash_At — host VFX)

        // QC: TakeResource(this, RES_HEALTH, damage). Health may go negative (a non-player has no resource clamp);
        // mirror MarkPain's direct .Health subtract (same backing store as RES_HEALTH).
        self.Health -= damage;

        if (self.Health <= -50f) // corpse gib threshold (NOT the live -100, NOT sv_gibhealth)
        {
            self.GtEventDamage = null; // QC: this.event_damage = func_null — stop re-entering the corpse path
            float removeAt = Now + 0.1f;
            self.Think = s => { s.NextThink = Now; if (Now >= removeAt) { Forget(s); Api.Entities.Remove(s); } };
            self.NextThink = Now;
        }
    }

    // ====================================================================================
    // Round-restart reset (Monster_Reset, sv_monsters.qc:999) + global counters
    // ====================================================================================

    /// <summary>
    /// Port of <c>Monster_Reset</c> (sv_monsters.qc:999), installed onto <see cref="Entity.Reset"/> in
    /// <see cref="Setup"/>: the round-restart hook the round handler calls on every entity. A command/wave-spawned
    /// monster (MONSTERFLAG_SPAWNED) is REMOVED on reset (it isn't part of the map). A natural map-placed monster
    /// is restored to its spawn point at full health, with enemy/goal/attack cleared.
    /// </summary>
    public static void Reset(Entity self)
    {
        MonsterState? st = StateOf(self);
        if (st is null) return;

        if (st.Spawned)
        {
            Remove(self); // QC: spawnflags & MONSTERFLAG_SPAWNED -> Monster_Remove(this); return;
            return;
        }

        if (Api.Services is not null)
            Api.Entities.SetOrigin(self, st.SpawnOrigin); // QC: setorigin(this, this.pos1)
        else
            self.Origin = st.SpawnOrigin;
        self.Angles = st.SpawnAngles;        // QC: this.angles = this.pos2
        self.Health = st.MaxHealth;          // QC: SetResourceExplicit(this, RES_HEALTH, this.max_health)
        self.Velocity = Vector3.Zero;        // QC: this.velocity = '0 0 0'
        self.Enemy = null;                   // QC: this.enemy = NULL
        self.GoalEntity = null;              // QC: this.goalentity = NULL
        st.AttackFinished = 0f;              // QC: this.attack_finished_single[0] = 0
        st.MoveTo = self.Origin;             // QC: this.moveto = this.origin
    }

    /// <summary>
    /// Call <see cref="Reset"/> on every live monster (the round-restart sweep). The QC round handler invokes each
    /// entity's <c>.reset</c>; this is the convenience entry point until that per-entity reset sweep is wired live
    /// (the same seam T14 deferred). Iterates a snapshot since <see cref="Reset"/> may remove SPAWNED monsters.
    /// </summary>
    public static void ResetAll()
    {
        if (Api.Services is null) return;
        foreach (Entity e in new List<Entity>(Api.Entities.FindByClass("monster")))
            e.Reset?.Invoke(e);
    }

    /// <summary>
    /// QC server globals <c>monsters_total</c> / <c>monsters_killed</c> (sv_monsters.qh), pushed to each client's
    /// STAT(MONSTERS_TOTAL/KILLED) by <c>monsters_setstatus</c> and read by the scoreboard map-stats row
    /// (Scoreboard_MapStats_Draw). Counted for NATURAL first-life spawns only (see <see cref="SpawnFromMap"/> /
    /// <see cref="MarkDead"/>). Process-global statics; reset per map/match via <see cref="ResetCounters"/>.
    /// </summary>
    public static int MonstersTotal { get; private set; }

    /// <inheritdoc cref="MonstersTotal"/>
    public static int MonstersKilled { get; private set; }

    /// <summary>Zero the global monster counters (QC worldspawn resets monsters_total/killed at map start).</summary>
    public static void ResetCounters()
    {
        MonstersTotal = 0;
        MonstersKilled = 0;
    }

    /// <summary>Port of <c>Monster_Respawn_Check</c> (sv_monsters.qc): may this corpse come back?</summary>
    private static bool RespawnCheck(MonsterState st)
    {
        if (st.AlwaysRespawn) return true;
        if (Cvar("g_monsters_respawn", 1f) == 0f || st.NoRespawn) return false;
        return true;
    }

    /// <summary>
    /// Port of <c>Monster_Respawn</c> + <c>Monster_Dead_Fade</c> respawn branch (sv_monsters.qc): restore the
    /// monster to full health at its spawn point (or its death point, if MONSTER_RESPAWN_DEATHPOINT) after
    /// the respawn delay, re-arm the live brain, and clear the dead state.
    /// </summary>
    private static void Respawn(Entity self, MonsterState st)
    {
        st.Respawned = true;
        Vector3 pos = st.RespawnAtDeathPoint ? self.Origin : st.SpawnOrigin;
        Vector3 ang = st.RespawnAtDeathPoint ? self.Angles : st.SpawnAngles;
        if (st.RespawnAtDeathPoint) { st.SpawnOrigin = self.Origin; st.SpawnAngles = self.Angles; }

        // Schedule the actual revive after respawntime (QC nextthink = time + respawntime).
        float reviveAt = Now + System.Math.Max(0.1f, st.RespawnTime);
        st.Lifetime = 0f;
        self.DeadState = DeadFlag.Respawning;
        self.TakeDamage = DamageMode.No;
        Api.Entities.SetOrigin(self, pos);
        self.Angles = ang;
        self.Health = st.MaxHealth;

        self.Think = s =>
        {
            s.NextThink = Now;
            if (Now < reviveAt) return;
            // Revive: re-stamp the live monster fields (subset of Monster_Spawn).
            s.DeadState = DeadFlag.No;
            s.Solid = Solid.BBox;
            s.TakeDamage = DamageMode.Aim;
            s.Health = st.MaxHealth;
            s.Enemy = null;
            s.Velocity = Vector3.Zero;
            if ((s.Flags & EntFlags.Fly) != 0) s.MoveType = MoveType.Fly;
            else s.MoveType = MoveType.Step;
            st.State = 0;
            st.AttackFinished = 0f;
            st.SpawnTime = Now;
            st.PainFinished = Now;
            st.Anim = MonsterAnim.Idle;
            st.DeathLanded = false; // a respawned monster's corpse-landed flag must clear so it isn't stuck on die2
            // QC Monster_Miniboss_Setup:523-528 re-applies EF_RED on a RESPAWNED miniboss (MarkDead cleared
            // s.Effects to 0). A regular monster comes back with no glow.
            if (st.IsMiniboss) s.Effects |= EfRed;
            // QC Monster_Respawn -> Monster_Spawn -> Monster_Spawn_Setup re-runs the healthbar block, so a
            // respawned monster regains its floating WP_Monster sprite (MarkDead killed + nulled the old one).
            if (st.Sprite is null && Cvar("g_monsters_healthbars", 0f) != 0f) SpawnHealthbar(s, st);
            // Re-arm the monster damage seam (the corpse left GtEventDamage installed, but a respawn re-establishes
            // the full live contract) + re-seed knockback receptivity for the generic apply-push.
            s.GtEventDamage = MonsterEventDamage;
            s.DamageForceScale = st.DamageForceScale;
            float shieldTime = Cvar("g_monsters_spawnshieldtime", 2f); // monsters.cfg: g_monsters_spawnshieldtime 2
            if (shieldTime > 0f) MonsterFramework.ApplyFor(MonsterFramework.SpawnShield, s, shieldTime);
            // Hand the brain back to the descriptor's Think.
            s.Think = self2 => { self2.NextThink = Now; st.Def.Think(self2); };
            s.NextThink = Now;
        };
        self.NextThink = Now;
    }

    // ====================================================================================
    // Small service shims
    // ====================================================================================

    /// <summary>Current sim time (QC <c>time</c>); 0 when no services are wired (headless tests).</summary>
    public static float Now => Api.Services is null ? 0f : Api.Clock.Time;

    /// <summary>Frame delta (QC <c>frametime</c>); 0 when no services are wired.</summary>
    public static float FrameTime => Api.Services is null ? 0f : Api.Clock.FrameTime;

    /// <summary>Read a balance/config float, falling back when unset or services are absent (QC autocvar default).</summary>
    /// <summary>QC autocvar_g_nades_entrap_speed (mutators.cfg:299, default 0.5): the entrap move-speed factor.</summary>
    private static float EntrapSpeed() => Cvar("g_nades_entrap_speed", 0.5f);

    // QC autocvar_g_buffs_disability_speed (0.7): the run/walk multiplier a Stunned monster moves at (buffs
    // MonsterMove hook, disability.qc:25-26).
    private static float DisabilitySpeed() => Cvar("g_buffs_disability_speed", 0.7f);

    public static float Cvar(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }

    /// <summary>
    /// Evaluate a boolean master on/off switch the QC way (<c>if (!autocvar_g_X)</c>): the switch is DISABLED
    /// only when the cvar is explicitly set to 0, and ENABLED when it is unset/absent (these switches default
    /// to 1 in monsters.cfg/turrets.cfg/vehicles.cfg). Unlike <see cref="Cvar"/>, this distinguishes "absent"
    /// from "present-and-0" via the raw string (an unset cvar reads as ""), so a server that sets
    /// <c>g_monsters 0</c> (e.g. ruleset-XPM.cfg) actually suppresses the NPC instead of falling back to 1.
    /// </summary>
    public static bool MasterSwitchEnabled(string name)
    {
        if (Api.Services is null) return true; // no cvar store (headless tests): default-on like the cfg
        string s = Api.Cvars.GetString(name);
        if (string.IsNullOrEmpty(s)) return true; // unset → default enabled (cfg default is 1)
        return Api.Cvars.GetFloat(name) != 0f;     // present → QC !autocvar semantics: 0 disables
    }

    /// <summary>Whether team play is active (QC <c>teamplay</c>); read from the gametype cvar.</summary>
    public static bool IsTeamplay => Cvar("teamplay", 0f) != 0f;

    /// <summary>
    /// QC <c>game_stopped || time &lt; game_starttime</c> (Monster_ValidTarget:108): monsters do nothing before
    /// the match clock starts or once it has been stopped (warmup/intermission/timeout). Reads the same live host
    /// seams the vehicle/mutator ports use — <see cref="VehicleCommon.GameStopped"/> (driven by the server each
    /// frame from <c>Intermission.Running || MatchEnded || Timeout.IsPaused</c>, GameWorld.cs SEAM E), falling back
    /// to the <c>g_game_stopped</c> cvar mirror — plus <see cref="StartItem.GameStartTimeProvider"/> for the prematch
    /// clock; headless tests with no host wire-up read as "match live".
    /// </summary>
    public static bool MatchHalted()
    {
        if (VehicleCommon.GameStopped) return true;
        if (Api.Services is not null && Api.Cvars.GetFloat("g_game_stopped") != 0f) return true;
        float gameStart = StartItem.GameStartTimeProvider?.Invoke() ?? 0f;
        return Now < gameStart;
    }

    /// <summary>
    /// QC <c>(autocvar_g_campaign &amp;&amp; !campaign_bots_may_start)</c> (sv_monsters.qc:846 Monster_Move /
    /// :1165 Monster_Move_2D): in a campaign, monster MOVEMENT is frozen until the human spawns. The server wires this
    /// to <c>g_campaign &amp;&amp; !Campaign.BotsMayStart</c>; unset (non-campaign play, headless tests) → never holds.
    /// This is the move-specific gate ONLY — QC does NOT key target acquisition (Monster_ValidTarget) off it, so it is
    /// kept out of <see cref="MatchHalted"/> (shared with ValidTarget).
    /// </summary>
    public static System.Func<bool>? CampaignBotHold;

    /// <summary>True when monster movement must hold for the campaign bots-may-start gate (see <see cref="CampaignBotHold"/>).</summary>
    public static bool CampaignMovementHeld() => CampaignBotHold?.Invoke() ?? false;

    /// <summary>QC WATERLEVEL_WETFEET: the threshold below which a swimmer is considered out of water.</summary>
    public const int WaterLevel_WetFeet = 1;

    /// <summary>Read a config string, falling back when unset or services are absent (QC autocvar string default).</summary>
    public static string CvarString(string name, string fallback)
    {
        if (Api.Services is null) return fallback;
        string v = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(v) ? fallback : v;
    }

    // QC const int MONSTER_ATTACK_MELEE / MONSTER_ATTACK_RANGED (sv_monsters.qh).
    public const int MonsterState_AttackMelee = 6;
    public const int MonsterState_AttackRanged = 7;

    // QC monster move flags (sv_monsters.qh MONSTER_MOVE_*).
    public const int MonsterMove_Follow = 1;
    public const int MonsterMove_Wander = 2;
    public const int MonsterMove_SpawnLoc = 3;
    public const int MonsterMove_NoMove = 4;
    public const int MonsterMove_Enemy = 5;

    // QC monster spawnflags (sv_monsters.qh MONSTERFLAG_* / MONSTERSKILL_* / MONSTER_RESPAWN_DEATHPOINT).
    public const int MonsterFlag_Appear = 1 << 1;
    public const int MonsterFlag_NoRespawn = 1 << 2;
    public const int MonsterFlag_FlyVertical = 1 << 3;
    public const int MonsterFlag_Infront = 1 << 5;
    public const int MonsterFlag_Miniboss = 1 << 6;
    public const int MonsterFlag_Invincible = 1 << 7;
    public const int MonsterSkill_NotEasy = 1 << 8;
    public const int MonsterSkill_NotMedium = 1 << 9;
    public const int MonsterSkill_NotHard = 1 << 10;
    public const int MonsterFlag_Spawned = 1 << 14;
    public const int MonsterFlag_Respawned = 1 << 15;
    /// <summary>QC MONSTER_RESPAWN_DEATHPOINT (reuses MONSTERFLAG_FLY_VERTICAL's bit per zombie.qh).</summary>
    public const int MonsterRespawn_DeathPoint = 1 << 3;

    /// <summary>DP <c>EF_RED = 128</c> (dpextensions.qc:179) — the red glow QC stamps on a miniboss (and re-applies on respawn).</summary>
    public const int EfRed = 128;

    /// <summary>DP <c>EF_FULLBRIGHT = 512</c> (dpextensions.qc:133) — set when g_fullbrightplayers (QC Monster_Spawn:1484).</summary>
    public const int EfFullbright = 512;

    /// <summary>DP <c>EF_NODEPTHTEST = 8192</c> (dpextensions.qc:141) — draw-through-walls when g_nodepthtestplayers (QC Monster_Spawn:1485).</summary>
    public const int EfNodepthtest = 8192;

    // SUPERCONTENTS_* bits for the monster's dphitcontentsmask (QC Monster_Spawn:1479/1488). Common can't
    // reference XonoticGodot.Engine.Collision.SuperContents, so they are mirrored here EXACTLY as Invasion.cs /
    // Nexball.cs / LagComp.cs do (DPCONTENTS_* values; TraceService honors Entity.DpHitContentsMask).
    private const int SuperContentsSolid       = 0x00000001; // QC DPCONTENTS_SOLID
    private const int SuperContentsBody        = 0x02000000; // QC DPCONTENTS_BODY
    private const int SuperContentsPlayerClip  = 0x00000100; // QC DPCONTENTS_PLAYERCLIP
    private const int SuperContentsMonsterClip = 0x00000200; // QC DPCONTENTS_MONSTERCLIP
    private const int SuperContentsBotClip     = 0x00000800; // QC DPCONTENTS_BOTCLIP
}

/// <summary>QC monster skill levels (sv_monsters.qh: MONSTER_SKILL_*).</summary>
public static class MonsterSkill
{
    public const int Easy = 1;
    public const int Medium = 3;
    public const int Hard = 5;
    public const int Insane = 7;
    public const int Nightmare = 10;
}

/// <summary>
/// Deterministic 0..1 source for monster decision-making — the stand-in for QuakeC's <c>random()</c> used
/// throughout the monster code (attack-type rolls, anim choices, leap/teleport chances). So monster
/// behaviour stays reproducible for headless tests and the deterministic simulation (ADR-0010), this now
/// unifies onto the project-wide deterministic PRNG <see cref="Prandom"/>: it is a thin compatibility
/// facade kept so existing monster call sites (<c>MonsterRandom.Next()</c>) keep working while all monster
/// randomness draws from the one server-seeded stream the predicting client reproduces.
/// </summary>
public static class MonsterRandom
{
    /// <summary>Reseed the shared deterministic stream (QC has no explicit monster seed; the sim sets this).</summary>
    public static void Seed(uint seed) => Prandom.Seed(seed);

    /// <summary>QC random(): a float in [0, 1), drawn from the shared deterministic PRNG.</summary>
    public static float Next() => Prandom.Float();
}
