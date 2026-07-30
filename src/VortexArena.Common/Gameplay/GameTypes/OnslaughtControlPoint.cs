// Port of common/gametypes/gametype/onslaught/sv_onslaught.qc — the control-point CAPTURE-BY-BUILD combat
// (ons_ControlPoint_Touch / ons_ControlPoint_Icon_Spawn / ons_ControlPoint_Icon_BuildThink /
// ons_ControlPoint_Icon_Think / ons_ControlPoint_Icon_Damage / ons_ControlPoint_Icon_Heal) and the generator
// damage pipeline (ons_GeneratorSetup / ons_GeneratorReset / ons_GeneratorDamage), plus the overtime
// generator-decay slice of Onslaught_CheckWinner.
//
// QC built an icon edict the moment a player touches an attackable control point; the icon ramps RES_HEALTH
// from g_onslaught_cp_buildhealth up to g_onslaught_cp_health, and ONLY THEN does the point flip to that
// team (and re-propagate power). The icon can be damaged/destroyed mid-build, in which case the point never
// captures. This file drives that loop headlessly onto real objective entities so weapon damage routes
// through the shared DamageSystem (Entity.GtEventDamage), reusing the existing Onslaught power graph
// (Onslaught.UpdateLinks / IsAttackable / CaptureControlPoint / DamageGenerator).
//
// Deferred vs QC (NOTE — genuinely client/networking): the CSQC icon/sprite networking (cpicon_send,
// WaypointSprite_*), particles/sounds, proximity-decap (g_onslaught_cp_proximitydecap), the build models
// (MDL_ONS_CP_PAD*), vehicle-touch, and the bot-target list — presentation / AI concerns. The capture
// outcome + the build/heal/destroy timing + the scoring are reproduced faithfully.

using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Onslaught control-point + generator combat layer (QC sv_onslaught.qc icon/generator funcs). Owned by
/// an <see cref="Onslaught"/> instance: it spawns the buildable capture icons + the generator entities, wires
/// their <see cref="Entity.GtEventDamage"/> into the shared damage pipeline, and runs the per-tick
/// build/heal/decay think. The point only flips (<see cref="Onslaught.CaptureControlPoint(int,int,Player?)"/>)
/// once an icon finishes building; the generator destruction routes through
/// <see cref="Onslaught.DamageGenerator(int,float,Player?)"/>.
/// </summary>
public sealed class OnslaughtControlPoint
{
    // ----- QC think rates (sv_onslaught.qh) -----
    /// <summary>QC ONS_CP_THINKRATE = 0.2 — the icon build/heal think interval.</summary>
    public const float CpThinkRate = 0.2f;
    /// <summary>QC GEN_THINKRATE = 1 — the generator alarm think interval (also the overtime decay step).</summary>
    public const float GenThinkRate = 1f;

    // ----- QC autocvar names + defaults (balance/gametypes cfgs) -----
    private const string CvarCpHealth      = "g_onslaught_cp_health";        // 1000 — icon full health
    private const string CvarCpBuildHealth = "g_onslaught_cp_buildhealth";   // 100 — health an icon starts at
    private const string CvarCpBuildTime   = "g_onslaught_cp_buildtime";     // 5   — seconds to build from build→full
    private const string CvarCpRegen       = "g_onslaught_cp_regen";         // 20  — slow repair rate once built (hp/s)
    private const string CvarGenHealth     = "g_onslaught_gen_health";       // 2500
    private const string CvarSuddenDeath   = "timelimit_suddendeath";        // 5   — overtime decay window (minutes)
    // QC ons_ControlPoint_Icon_Think proximity-decap autocvars (sv_onslaught.qc:18-21).
    private const string CvarProxDecap     = "g_onslaught_cp_proximitydecap";          // 0   — master toggle (default OFF)
    private const string CvarProxDecapDist = "g_onslaught_cp_proximitydecap_distance"; // 512 — qu radius
    private const string CvarProxDecapDps  = "g_onslaught_cp_proximitydecap_dps";      // 100 — hp/s per (friendly-enemy) head
    private const string CvarShieldForce   = "g_onslaught_shield_force";               // 100 — capture-shield push force

    private const float  DefShieldForce  = 100f;  // QC autocvar_g_onslaught_shield_force default (gametypes-server.cfg:585)
    private const float  ShieldHitboxScale = 1.20f; // QC ons_CaptureShield_Spawn: hitbox 20% larger than the object
    private const int    EfAdditive = 32; // QC EF_ADDITIVE (dpextensions: additive-blend render mode)
    private const int    EfLowPrecision = 4194304; // QC EF_LOWPRECISION (dpextensions): origin/angles bandwidth hint

    // ----- QC world model paths (cl_generator.qc MDL_ONS_GEN.., cl_controlpoint.qc MDL_ONS_CP.., MDL_ONS_CP_PAD*) -----
    // The server assigns these so the objective bodies actually RENDER on the client via the entity feed (the same
    // path the CTF flag / Domination point use — Entity.Model networks + renders). Progressive damage-model swaps
    // are server-authoritative (we pick the bucket model in GeneratorDamage/IconDamage), reproducing in effect what
    // CSQC generator_damage/cpicon_damage would derive from the networked hp fraction. The bob/spin/punch ANIMATION,
    // radar links, death-cam and explosion/particle FX remain genuinely client-side (see the spec's deferred note).
    private const string MdlGen        = "models/onslaught/generator.md3";       // QC MDL_ONS_GEN (full health)
    private const string MdlGenDead    = "models/onslaught/generator_dead.md3";  // QC MDL_ONS_GEN_DEAD (destroyed)
    private const string MdlCpPad1     = "models/onslaught/controlpoint_pad.md3";  // QC MDL_ONS_CP_PAD1 (neutral pad)
    private const string MdlCpPad2     = "models/onslaught/controlpoint_pad2.md3"; // QC MDL_ONS_CP_PAD2 (captured pad)
    private const string MdlCpIcon     = "models/onslaught/controlpoint_icon.md3"; // QC MDL_ONS_CP (icon, full health)

    /// <summary>
    /// QC cl_generator.qc generator_damage: the progressive generator model for a 0..10 damage bucket (QC sends
    /// frame = 10*(1-hp/max); CSQC swaps generator.md3 / generator_dmg1..9.md3 / generator_dead.md3 by that bucket).
    /// We resolve it server-side so the body visibly degrades as it's shot.
    /// </summary>
    private static string GeneratorModelForBucket(int bucket)
    {
        if (bucket <= 0) return MdlGen;          // pristine
        if (bucket >= 10) return MdlGenDead;     // destroyed
        return $"models/onslaught/generator_dmg{bucket}.md3"; // dmg1..dmg9
    }

    /// <summary>
    /// QC cl_controlpoint.qc cpicon_damage: the progressive build-icon model for a health fraction (QC swaps
    /// controlpoint_icon.md3 / _dmg1 / _dmg2 / _dmg3 by 75/50/25% thresholds). Resolved server-side so a damaged
    /// icon visibly cracks before it's destroyed.
    /// </summary>
    private static string IconModelForFraction(float frac)
    {
        if (frac > 0.75f) return MdlCpIcon;                              // QC MDL_ONS_CP
        if (frac > 0.50f) return "models/onslaught/controlpoint_icon_dmg1.md3"; // QC MDL_ONS_CP1
        if (frac > 0.25f) return "models/onslaught/controlpoint_icon_dmg2.md3"; // QC MDL_ONS_CP2
        return "models/onslaught/controlpoint_icon_dmg3.md3";                   // QC MDL_ONS_CP3
    }

    private const float DefCpHealth      = 1000f; // QC g_onslaught_cp_health default (gametypes-server.cfg:578)
    private const float DefCpBuildHealth = 100f;
    private const float DefCpBuildTime   = 5f;
    private const float DefCpRegen       = 20f; // QC g_onslaught_cp_regen default (gametypes-server.cfg:581)
    private const float DefGenHealth     = 2500f; // QC g_onslaught_gen_health default (gametypes-server.cfg:576)
    private const float DefSuddenDeath   = 5f;
    private const float DefProxDecapDist = 512f;
    private const float DefProxDecapDps  = 100f;

    private readonly Onslaught _ons;

    /// <summary>Every live build icon (QC the onslaught_controlpoint_icon edicts), keyed by control-point id.</summary>
    private readonly Dictionary<int, Entity> _icons = new();

    /// <summary>Every generator world entity (QC ons_worldgeneratorlist), keyed by team.</summary>
    private readonly Dictionary<int, Entity> _generators = new();

    /// <summary>True once the overtime decay announcement has fired (QC wpforenemy_announced).</summary>
    public bool OvertimeAnnounced { get; private set; }

    /// <summary>
    /// Per-team last CP under-attack notification time (QC <c>ons_notification_time[team]</c>, sv_onslaught.qc:384).
    /// Suppresses redundant <c>ONS_CONTROLPOINT_UNDERATTACK</c> play2team calls within 10 seconds per team.
    /// </summary>
    private readonly Dictionary<int, float> _cpNotifyTime = new();

    public OnslaughtControlPoint(Onslaught ons) => _ons = ons;

    /// <summary>Clear the icon/generator entity maps + the overtime latch (QC per-map reset). The graph itself
    /// is owned by <see cref="Onslaught"/> and cleared in its OnInit.</summary>
    public void Reset()
    {
        _icons.Clear();
        _generators.Clear();
        _cpNotifyTime.Clear();
        OvertimeAnnounced = false;
    }

    private static float Now => Api.Services is null ? 0f : Api.Clock.Time;

    public float CpHealth      => GametypeEntities.Cvar(CvarCpHealth, DefCpHealth);
    public float CpBuildHealth => GametypeEntities.Cvar(CvarCpBuildHealth, DefCpBuildHealth);
    public float CpBuildTime   { get { float v = GametypeEntities.Cvar(CvarCpBuildTime, DefCpBuildTime); return v > 0f ? v : DefCpBuildTime; } }
    public float CpRegen       => GametypeEntities.Cvar(CvarCpRegen, DefCpRegen);
    public float GenHealth     => GametypeEntities.Cvar(CvarGenHealth, DefGenHealth);

    /// <summary>The live build icon for a control point (or null), QC cp.goalentity.</summary>
    public Entity? IconFor(int controlPointId) => _icons.TryGetValue(controlPointId, out var e) ? e : null;

    /// <summary>The generator world entity for a team (or null), QC the onslaught_generator edict.</summary>
    public Entity? GeneratorEntity(int team) => _generators.TryGetValue(team, out var e) ? e : null;

    // ============================================================================================
    //  Generator setup + damage (QC ons_GeneratorSetup / ons_GeneratorReset / ons_GeneratorDamage)
    // ============================================================================================

    /// <summary>
    /// QC ons_GeneratorSetup + ons_GeneratorReset (spawnfunc onslaught_generator): register a team's generator
    /// in the <see cref="Onslaught"/> power graph AND spawn its world entity with RES_HEALTH = g_onslaught_gen_health,
    /// TakeDamage = AIM, and <see cref="Entity.GtEventDamage"/> wired to <see cref="GeneratorDamage"/> so weapon
    /// fire routes through the shared pipeline. Returns the world entity (null when no facade is wired — the
    /// gametype still tracks the generator via <see cref="Onslaught.AddGenerator"/>).
    /// </summary>
    public Entity? SpawnGenerator(int team, Vector3 origin) => SpawnGenerator(team, origin, null);

    /// <summary>
    /// QC ons_GeneratorSetup with the generator's map <c>.targetname</c> (<paramref name="name"/>) so an
    /// <c>onslaught_link</c> can reference it: same as <see cref="SpawnGenerator(int,Vector3)"/> but the graph
    /// node is name-indexed (<see cref="Onslaught.NodeByName"/>) for deferred link resolution.
    /// </summary>
    public Entity? SpawnGenerator(int team, Vector3 origin, string? name)
    {
        Onslaught.OnsNode node = _ons.GeneratorNode(team) ?? _ons.AddGenerator(team, name);
        float h = GenHealth;
        if (node.Gen is not null) { node.Gen.Health = h; node.Gen.MaxHealth = h; }

        Entity? e = GametypeEntities.SpawnObjective("onslaught_generator", origin, team,
            new Vector3(-52f, -52f, -14f), new Vector3(52f, 52f, 75f)); // QC generator bbox (approx)
        if (e is not null)
        {
            e.GtObjHealth = h;
            e.GtObjMaxHealth = h;
            e.GtHomeTeam = team;
            e.GtPointId = -team; // negative id namespace so it never collides with a control-point id
            e.TakeDamage = DamageMode.Aim;     // QC DAMAGE_AIM (shield is enforced inside GeneratorDamage)
            e.GtEventDamage = GeneratorDamage;
            // QC ons_GeneratorSetup (sv_onslaught.qc:1117): gen.event_heal = ons_GeneratorHeal — a friendly heal
            // (Arc beam / mage / bumblebee, via Combat.Heal) tops up the generator's GtObjHealth + graph health.
            e.GtEventHeal = (targ, _, amount, limit) => GeneratorHeal(team, targ, amount, limit);
            // QC ons_GeneratorSetup/ons_GeneratorReset (sv_onslaught.qc:1056): setthink(this, ons_GeneratorThink),
            // nextthink = time + GEN_THINKRATE — the recurring un-shielded alarm (see GeneratorThink).
            e.Think = GeneratorThink;
            e.GtAlarmWait = 0f;
            e.NextThink = Now + GenThinkRate;
            // QC ons_GeneratorSetup (sv_onslaught.qc): setmodel(this, MDL_ONS_GEN) — the generator body. The port
            // previously left Model empty so the generator networked as an invisible point; assign the world model
            // (+ EF_LOWPRECISION bandwidth hint) so it renders on the client like the CTF flag / Domination point.
            // Frame tracks the 0..10 damage bucket (QC frame = 10*(1-hp/max)) for any client that animates by frame.
            e.Model = MdlGen;
            e.Frame = 0f;
            e.Effects |= EfLowPrecision;
            _generators[team] = e;
            // QC ons_GeneratorSetup (sv_onslaught.qc:1079): ons_CaptureShield_Spawn(this, MDL_ONS_GEN_SHIELD) —
            // the spinning additive push-shield around the generator.
            SpawnCaptureShield(e, "models/onslaught/generator_shield.md3");
        }
        return e;
    }

    /// <summary>
    /// QC ons_GeneratorThink (sv_onslaught.qc:1021): the generator's recurring think. While the generator is
    /// alive but UN-shielded (an enemy controls a linked control point that exposes it), re-warn every 5 s — the
    /// owning team gets the CENTER_ONS_NOTSHIELDED_TEAM center print + the ONS_GENERATOR_ALARM siren (ATTEN_NONE),
    /// and the enemy team gets APP_TEAM_NUM(team, CENTER_ONS_NOTSHIELDED). A shielded (or game-stopped) generator
    /// stays silent. Re-arms itself every GEN_THINKRATE (1 s); the 5 s repeat is gated by GtAlarmWait (QC .wait).
    /// </summary>
    public void GeneratorThink(Entity self)
    {
        self.NextThink = Now + GenThinkRate; // QC: this.nextthink = time + GEN_THINKRATE

        // QC: if (game_stopped || this.isshielded || time < this.wait) return.
        // (game_stopped covers warmup + the round-over state — the port's RoundStarted() gate.)
        if (!RoundStarted())
            return;
        int team = self.GtHomeTeam;
        Onslaught.OnsNode? node = _ons.GeneratorNode(team);
        bool shielded = node is null || node.Shielded;
        if (shielded || Now < self.GtAlarmWait)
            return;

        self.GtAlarmWait = Now + 5f; // QC: this.wait = time + 5

        if (Api.Services is null)
            return;

        // QC FOREACH_CLIENT(IS_PLAYER(it) && IS_REAL_CLIENT(it), …): the owning team is warned its generator is
        // exposed (center + the siren to that player); enemies get the team-coloured "enemy generator unshielded"
        // center. Bots/spectators are skipped (IS_REAL_CLIENT). The siren is ATTEN_NONE (map-wide) in QC; the
        // headless port emits it once globally (matching the under-attack alarm's PlayGlobal approximation).
        string enemySuffix = Onslaught.TeamSuffix(team);
        bool sirenPlayed = false;
        foreach (Entity e in Api.Entities.FindByClass("player"))
        {
            if (e is not Player p || (p.Flags & EntFlags.Client) == 0 || p.IsBot)
                continue;
            if ((int)p.Team == team)
            {
                // QC: Send_Notification(NOTIF_ONE, it, MSG_CENTER, CENTER_ONS_NOTSHIELDED_TEAM); soundto siren.
                NotificationSystem.Send(NotifBroadcast.One, p, MsgType.Center, "ONS_NOTSHIELDED_TEAM");
                if (!sirenPlayed)
                {
                    SoundSystem.PlayGlobal(Sounds.ByName("ONS_GENERATOR_ALARM"));
                    sirenPlayed = true;
                }
            }
            else
            {
                // QC: Send_Notification(NOTIF_ONE, it, MSG_CENTER, APP_TEAM_NUM(this.team, CENTER_ONS_NOTSHIELDED)).
                NotificationSystem.Send(NotifBroadcast.One, p, MsgType.Center, $"ONS_NOTSHIELDED_{enemySuffix}");
            }
        }
    }

    /// <summary>
    /// QC ons_GeneratorDamage (sv_onslaught.qc:919): the generator's event_damage. Bails while warmup/round
    /// not started; a shielded generator ignores all non-self damage; otherwise subtract and, at &lt;=0 health,
    /// destroy it (the existing <see cref="Onslaught.DamageGenerator(int,float,Player?)"/> awards the attacker
    /// SCORE +100, re-propagates power, and ends the round). The DEATH_HURTTRIGGER self-damage of the overtime
    /// decay (attacker == this) bypasses the shield + the under-attack notify, exactly like QC.
    /// </summary>
    public void GeneratorDamage(Entity self, Entity? inflictor, Entity? attacker, string deathType,
        float damage, Vector3 hitLoc, Vector3 force)
    {
        if (damage <= 0f) return;
        // QC: if(warmup_stage || game_stopped) return; if(!round_handler_IsRoundStarted()) return;
        if (!RoundStarted())
            return;

        int team = self.GtHomeTeam;
        bool selfDamage = ReferenceEquals(attacker, self); // overtime decay path
        Onslaught.OnsNode? node = _ons.GeneratorNode(team);

        bool unshieldedForSelf = false;
        if (!selfDamage)
        {
            // QC: a shielded generator ignores the damage entirely (only the blocked-by-shield hint plays).
            if (node is not null && node.Shielded)
            {
                // QC ons_GeneratorDamage (sv_onslaught.qc:927-937): blocked-by-shield hint to the attacker.
                if (attacker is Player blocked && Now > self.GtPainFinished)
                {
                    // QC sv_onslaught.qc:933 play2(attacker, SND(ONS_DAMAGEBLOCKEDBYSHIELD)) — a per-recipient 2D cue
                    // (CH_INFO / VOL_BASE / ATTEN_NONE), not a positional emit on the attacker.
                    if (Api.Services is not null)
                        SoundSystem.Play2(blocked, Sounds.ByName("ONS_DAMAGEBLOCKEDBYSHIELD"));
                    self.GtPainFinished = Now + 1f;
                }
                return;
            }
            // QC ons_GeneratorDamage (sv_onslaught.qc:939-944): under-attack center (to the owning team) +
            // play2team alarm, debounced by pain_finished (10 s).
            // Base: FOREACH_CLIENT(IS_PLAYER&&IS_REAL_CLIENT&&SAME_TEAM) -> center; play2team(this.team, ...).
            if (Now > self.GtPainFinished)
            {
                self.GtPainFinished = Now + 10f;
                // QC (sv_onslaught.qc:942): FOREACH_CLIENT(IS_PLAYER && IS_REAL_CLIENT && SAME_TEAM(it, this))
                //   -> Send_Notification(NOTIF_ONE, it, MSG_CENTER, CENTER_GENERATOR_UNDERATTACK).
                // The center print goes ONLY to the generator's own team, not everyone — broadcasting to All
                // would wrongly warn the attacking team that they're "under attack".
                if (Api.Services is not null)
                {
                    foreach (Entity e in Api.Entities.FindByClass("player"))
                    {
                        if (e is not Player p || (p.Flags & EntFlags.Client) == 0 || p.IsBot)
                            continue;
                        if ((int)p.Team != team)
                            continue;
                        NotificationSystem.Send(NotifBroadcast.One, p, MsgType.Center, "GENERATOR_UNDERATTACK");
                    }
                    // QC (sv_onslaught.qc:943): play2team(this.team, SND(ONS_GENERATOR_UNDERATTACK)).
                    SoundSystem.Play2Team(team, Sounds.ByName("ONS_GENERATOR_UNDERATTACK"),
                        new List<Entity>(Api.Entities.FindByClass("player")));
                }
            }
        }
        else if (node is { Shielded: true })
        {
            // QC ons_GeneratorDamage: the overtime self-damage (attacker == this) bypasses the shield. The
            // existing DamageGenerator gates on the shield, so drop it for this self-inflicted hit (the
            // subsequent UpdateLinks on damage/destroy recomputes it).
            node.Shielded = false;
            unshieldedForSelf = true;
        }

        Player? by = attacker as Player;
        bool destroyed = _ons.DamageGenerator(team, damage, by); // applies HP, scoring, links, round-end
        // restore the shield if the self-damage hit didn't destroy the generator (it'd be re-derived next
        // UpdateLinks anyway, but keep the state coherent between decay ticks).
        if (unshieldedForSelf && !destroyed && node is not null)
            node.Shielded = true;
        // mirror the graph health back onto the entity (so the entity stays the source of truth for damage gate)
        Onslaught.GeneratorState? gen = _ons.GeneratorFor(team);
        self.GtObjHealth = gen is not null ? gen.Health : 0f;

        if (destroyed)
        {
            // QC ons_GeneratorDamage (sv_onslaught.qc:958-962): the generator-destroyed info notify, overtime
            // (self-damage) vs normal kill. The SCORE +100 attacker credit is handled in DamageGenerator.
            string suffix = Onslaught.TeamSuffix(team);
            Onslaught.Notify(NotifBroadcast.All, MsgType.Info,
                selfDamage ? $"ONSLAUGHT_GENDESTROYED_OVERTIME_{suffix}" : $"ONSLAUGHT_GENDESTROYED_{suffix}");
            // QC ons_GeneratorDamage (:406-equivalent generator explode): explosion sound on death.
            if (Api.Services is not null)
                SoundSystem.PlayOn(self, Sounds.ByName("ONS_GENERATOR_EXPLODE"));

            // QC ons_GeneratorDamage destroy (cl_generator.qc generator_construct/dead state): swap to the dead
            // generator model so the destroyed body is the wrecked shell, not the pristine generator. Server-
            // authoritative model swap (the entity feed re-networks Model — ClientEntityView.cs:236).
            self.Model = MdlGenDead;
            self.Frame = 10f;

            // QC: takedamage = DAMAGE_NO; event_damage = func_null; setthink(func_null).
            self.TakeDamage = DamageMode.No;
            self.GtEventDamage = null;
            self.Think = null;
            self.NextThink = 0f;
        }
        else
        {
            // QC cl_generator.qc generator_damage: the visible damage state degrades with health — QC networks
            // frame = 10*(1 - hp/max) and CSQC swaps generator.md3 → generator_dmg1..9.md3 by that bucket. Resolve
            // it server-side so the generator body visibly cracks as it's shot down (the 'generator damage frame'
            // the spec lists as missing). Clamped 0..9 (the dead model is set only on the destroy branch above).
            if (self.GtObjMaxHealth > 0f)
            {
                int bucket = (int)(10f * (1f - self.GtObjHealth / self.GtObjMaxHealth));
                if (bucket < 0) bucket = 0; else if (bucket > 9) bucket = 9;
                self.Frame = bucket;
                self.Model = GeneratorModelForBucket(bucket);
            }

            // QC ons_GeneratorDamage (sv_onslaught.qc:986-999): a hit sound on every (non-fatal) hit — a flaming
            // gib impact at chance damage/220, else ONS_HIT1/2 (random).
            if (Api.Services is not null)
            {
                if (XonoticGodot.Common.Math.Prandom.Float() < damage / 220f)
                    SoundSystem.PlayOn(self, Sounds.ByName("ONS_GENERATOR_EXPLODE")); // QC SND_ROCKET_IMPACT (port maps ONS explode→grenade_impact)
                else
                    SoundSystem.PlayOn(self, Sounds.ByName(
                        XonoticGodot.Common.Math.Prandom.Float() < 0.5f ? "ONS_HIT1" : "ONS_HIT2"));
            }
        }
    }

    // ============================================================================================
    //  Control-point touch + icon build/heal/damage (QC ons_ControlPoint_*)
    // ============================================================================================

    /// <summary>
    /// QC ons_ControlPoint_Setup (spawnfunc onslaught_controlpoint): register a neutral control-point node in
    /// the <see cref="Onslaught"/> power graph AND spawn its world trigger so a player touching it can start a
    /// capture. Touch dispatches to <see cref="ControlPointTouch"/>. Returns the world entity (or null headless).
    /// </summary>
    public Entity? SpawnControlPoint(int controlPointId, Vector3 origin) => SpawnControlPoint(controlPointId, origin, null);

    /// <summary>
    /// QC ons_ControlPoint_Setup with the control point's map <c>.targetname</c> (<paramref name="name"/>) so an
    /// <c>onslaught_link</c> can reference it, and its map <c>.target</c> (<paramref name="target"/>) fired on
    /// capture (QC <c>SUB_UseTargets</c> in ons_ControlPoint_Icon_BuildThink): same as
    /// <see cref="SpawnControlPoint(int,Vector3)"/> but the graph node is name-indexed
    /// (<see cref="Onslaught.NodeByName"/>) for deferred link resolution.
    /// </summary>
    public Entity? SpawnControlPoint(int controlPointId, Vector3 origin, string? name, string? target = null)
    {
        if (_ons.ControlPointNode(controlPointId) is null)
            _ons.AddControlPoint(controlPointId, name);

        Entity? e = GametypeEntities.SpawnObjective("onslaught_controlpoint", origin, Teams.None,
            new Vector3(-32f, -32f, 0f), new Vector3(32f, 32f, 128f), touch: ControlPointTouchEntity);
        if (e is not null)
        {
            e.GtPointId = controlPointId;
            // QC ons_ControlPoint_Icon_BuildThink (sv_onslaught.qc:605): SUB_UseTargets(this.owner, …) fires the
            // captured CP's map .target — a server-side map-logic hook (e.g. a captured point opens a door). Carry
            // the map .target onto the CP entity so IconBuildThink can fire it on completion.
            e.Target = target ?? "";
            e.Solid = Solid.BBox; // QC SOLID_BBOX (the pad), the touch trigger volume is the bbox here
            // QC ons_ControlPoint_Setup: setmodel(this, MDL_ONS_CP_PAD1) — the neutral control-point pad. Assign the
            // world model (+ EF_LOWPRECISION) so the pad renders on the client (was an invisible point); it swaps to
            // MDL_ONS_CP_PAD2 on capture (see IconBuildThink), reproducing the QC pad-model swap the spec lists as missing.
            e.Model = MdlCpPad1;
            e.Effects |= EfLowPrecision;
            // QC ons_ControlPoint_Setup (sv_onslaught.qc:787): ons_CaptureShield_Spawn(this, MDL_ONS_CP_SHIELD) —
            // the spinning additive push-shield around the control point.
            SpawnCaptureShield(e, "models/onslaught/controlpoint_shield.md3");

            // QC ons_ControlPoint_Setup (sv_onslaught.qc:777): SUB_UseTargets(this, this, NULL) — fire the CP's
            // map .target at spawn "to reset the structures, playerspawns etc." (a neutral CP unteams its linked
            // spawnpoints on map load). The node starts neutral/unlinked so WasLinked stays false (no double-fire
            // on the first IconThink link change).
            if (e.Target.Length > 0)
                MapMover.UseTargets(e, e, null);
        }
        return e;
    }

    /// <summary>Entity touch trampoline (QC settouch ons_ControlPoint_Touch): a live player may start a build.</summary>
    private void ControlPointTouchEntity(Entity self, Entity other)
    {
        if (other is Player p && !p.IsDead)
            ControlPointTouch(self, p);
    }

    /// <summary>
    /// QC ons_ControlPoint_Touch (sv_onslaught.qc:714): when a player touches a control point that is
    /// attackable by their team (QC ons_ControlPoint_Attackable returns 2 or 4 — a free/unshielded point with
    /// no icon yet), start building the capture icon (<see cref="SpawnIcon"/>). A point that already has a
    /// building/built icon, or is shielded, or is same-team, does nothing here.
    /// </summary>
    public void ControlPointTouch(Entity cpEnt, Player toucher)
    {
        int cpId = cpEnt.GtPointId;
        Onslaught.OnsNode? node = _ons.ControlPointNode(cpId);
        if (node is null)
            return;
        int team = (int)toucher.Team;
        // QC attackable values 2/4 == "free point, touch it": no icon yet, unshielded, capturable by this team.
        // (A point that already has an icon returns -1/-2/1/3; a shielded one returns 0.)
        if (_icons.ContainsKey(cpId))
            return;
        if (node.Shielded || node.Team == team || team == Teams.None)
            return;
        if (!_ons.IsAttackable(node, team))
            return;

        SpawnIcon(cpEnt, toucher);
    }

    /// <summary>
    /// QC ons_ControlPoint_Icon_Spawn (sv_onslaught.qc:620): create the buildable icon for <paramref name="cpEnt"/>,
    /// owned by <paramref name="player"/>'s team, starting at g_onslaught_cp_buildhealth and ramping toward
    /// g_onslaught_cp_health at a rate that finishes in g_onslaught_cp_buildtime seconds (QC e.count). Wires the
    /// icon's <see cref="Entity.GtEventDamage"/> (so it can be destroyed mid-build) and its build Think.
    /// </summary>
    public Entity? SpawnIcon(Entity cpEnt, Player player)
    {
        int cpId = cpEnt.GtPointId;
        int team = (int)player.Team;
        float maxH = CpHealth;
        float buildH = CpBuildHealth;
        // QC: e.count = (max_health - health) * ONS_CP_THINKRATE / buildtime — health gained per think tick.
        float perTick = (maxH - buildH) * CpThinkRate / CpBuildTime;

        Entity? icon = GametypeEntities.SpawnObjective("onslaught_controlpoint_icon",
            cpEnt.Origin + new Vector3(0f, 0f, 96f), team,
            new Vector3(-16f, -16f, -8f), new Vector3(16f, 16f, 8f), think: IconBuildThink);
        if (icon is not null)
        {
            icon.GtObjHealth = buildH;
            icon.GtObjMaxHealth = maxH;
            // QC ons_ControlPoint_Icon_Spawn: setmodel(e, MDL_ONS_CP) — the build-icon body (full-health model).
            // Assign it (+ EF_LOWPRECISION) so the capture icon renders + can swap to its damaged models as it's shot.
            icon.Model = MdlCpIcon;
            icon.Effects |= EfLowPrecision;
            icon.GtBuildRate = perTick;
            icon.GtIconCpId = cpId;
            icon.GtIconBuilt = false;
            icon.GtHomeTeam = team;
            icon.Solid = Solid.Not;            // QC SOLID_NOT while building (becomes SOLID_BBOX when built)
            icon.TakeDamage = DamageMode.Aim;  // QC DAMAGE_AIM — bot/weapon targetable
            icon.GtEventDamage = IconDamage;
            // QC ons_ControlPoint_Icon_Spawn (sv_onslaught.qc:635): e.event_heal = ons_ControlPoint_Icon_Heal — a
            // friendly heal (Arc beam / mage / bumblebee, via Combat.Heal) tops up the icon's GtObjHealth.
            icon.GtEventHeal = (targ, _, amount, limit) => IconHeal(targ, amount, limit);
            icon.GtPainFinished = 0f;
            icon.NextThink = Now + CpThinkRate;
            _icons[cpId] = icon;
        }

        // QC: cp.goalentity = e; cp.team = e.team — the point shows the builder's color WHILE BUILDING, but the
        // power-graph capture (node.Team / node.Captured) only flips when the icon finishes (so the eventual
        // CaptureControlPoint still registers a real capture and credits the builder). We store the builder +
        // tentative team on the cp ENTITY only, leaving the graph node neutral until completion.
        cpEnt.GtCapturer = player;       // QC cp.ons_toucher — the builder, credited on completion
        cpEnt.GtPointTeam = team;        // QC cp.team while building (display color)

        // QC ons_ControlPoint_Icon_Spawn (sv_onslaught.qc:640): the build-start sound on the control point.
        if (Api.Services is not null)
            SoundSystem.PlayOn(cpEnt, Sounds.ByName("ONS_CONTROLPOINT_BUILD"));
        return icon;
    }

    /// <summary>
    /// QC ons_ControlPoint_Icon_BuildThink (sv_onslaught.qc:554): while there is still power to this point
    /// (the existing graph keeps it linkable), add g_onslaught build per tick; when it reaches max, finish the
    /// capture — flip the point to the builder's team via <see cref="Onslaught.CaptureControlPoint(int,int,Player?)"/>
    /// (which credits ONS_CAPS/ONS_TAKES + SCORE +10 and re-propagates power) and switch to the slow regen Think.
    /// </summary>
    public void IconBuildThink(Entity self)
    {
        self.NextThink = Now + CpThinkRate;
        int cpId = self.GtIconCpId;
        Onslaught.OnsNode? node = _ons.ControlPointNode(cpId);
        if (node is null)
            return;

        // QC: only build if there is power (ons_ControlPoint_CanBeLinked); a point cut off mid-build just pauses.
        if (!CanBeLinked(node, self.GtHomeTeam))
            return;

        self.GtObjHealth += self.GtBuildRate;

        if (self.GtObjHealth >= self.GtObjMaxHealth)
        {
            self.GtObjHealth = self.GtObjMaxHealth;
            // QC: count = regen * ONS_CP_THINKRATE — slow repair from now on; switch to the steady-state Think.
            self.GtBuildRate = CpRegen * CpThinkRate;
            self.GtIconBuilt = true;
            self.Solid = Solid.BBox;           // QC SOLID_BBOX once built
            self.Think = IconThink;

            // QC ons_ControlPoint_Icon_BuildThink (sv_onslaught.qc:574): the "control point built" sound.
            if (Api.Services is not null)
                SoundSystem.PlayOn(self, Sounds.ByName("ONS_CONTROLPOINT_BUILT"));

            // QC: cp.iscaptured = true; capture credit to ons_toucher (SCORE +10 + ONS_CAPS); onslaught_updatelinks.
            Entity? cpEnt = ControlPointEntity(cpId);
            Player? toucher = cpEnt?.GtCapturer as Player;
            _ons.CaptureControlPoint(cpId, self.GtHomeTeam, toucher);

            // QC ons_ControlPoint_Icon_BuildThink (sv_onslaught.qc): setmodel(this.owner, MDL_ONS_CP_PAD2) — swap the
            // captured control point's pad to the 'built' model. Server-authoritative model swap (re-networked).
            if (cpEnt is not null)
                cpEnt.Model = MdlCpPad2;

            // QC ons_ControlPoint_Icon_BuildThink (sv_onslaught.qc:605): SUB_UseTargets(this.owner, this, NULL) —
            // fire the captured control point's map .target (server-side map logic, e.g. open a gate). The icon
            // is the trigger; the captured CP entity is the activator.
            if (cpEnt is not null && cpEnt.Target.Length > 0)
                MapMover.UseTargets(cpEnt, self, null);

            // QC ons_ControlPoint_Icon_BuildThink (sv_onslaught.qc:584-600): the capture notification. The port's
            // control point carries no map .message (CP name), so emit the NONAME variants (faithful when message=="").
            if (toucher is not null)
            {
                int t = self.GtHomeTeam;
                Onslaught.Notify(NotifBroadcast.All, MsgType.Info, "ONSLAUGHT_CAPTURE_NONAME", toucher.NetName);
                NotificationSystem.Send(NotifBroadcast.AllExcept, toucher, MsgType.Center,
                    $"ONS_CAPTURE_TEAM_NONAME_{Onslaught.TeamSuffix(t)}");
                NotificationSystem.Send(NotifBroadcast.One, toucher, MsgType.Center, "ONS_CAPTURE_NONAME");
            }
        }
    }

    /// <summary>
    /// QC ons_ControlPoint_Icon_Think (sv_onslaught.qc:477) — the steady-state icon think after the point is
    /// built: optional proximity-decap (g_onslaught_cp_proximitydecap, default OFF) then slowly regenerate
    /// RES_HEALTH back toward max (QC: 5s after the last hit). Re-arms itself.
    /// </summary>
    public void IconThink(Entity self)
    {
        self.NextThink = Now + CpThinkRate;

        // QC ons_ControlPoint_Icon_Think proximity-decap (sv_onslaught.qc:481-517): only when the owner point is
        // NOT shielded and the cvar is on. The (friendly - enemy) live-player count within
        // g_onslaught_cp_proximitydecap_distance changes RES_HEALTH by ±dps*THINKRATE each tick; at <=0 the icon
        // is destroyed via ons_ControlPoint_Icon_Damage (attacker = the lone enemy, or `this` to spread the
        // multi-attacker reward). A pure-friendly crowd repairs it instead.
        int cpId = self.GtIconCpId;
        Onslaught.OnsNode? node = _ons.ControlPointNode(cpId);
        if (node is not null && !node.Shielded && GametypeEntities.Cvar(CvarProxDecap, 0f) != 0f && Api.Services is not null)
        {
            int myTeam = self.GtHomeTeam;
            float dist = GametypeEntities.Cvar(CvarProxDecapDist, DefProxDecapDist);
            float dist2 = dist * dist;
            int friendly = 0, enemy = 0;
            Player? firstEnemy = null;
            foreach (Entity e in Api.Entities.FindByClass("player"))
            {
                if (e is not Player p || (p.Flags & EntFlags.Client) == 0 || p.IsDead)
                    continue;
                if ((p.Origin - self.Origin).LengthSquared() >= dist2)
                    continue;
                if ((int)p.Team == myTeam)
                    friendly++;
                else
                {
                    enemy++;
                    firstEnemy ??= p;
                }
            }

            float hlt = (friendly - enemy) * (GametypeEntities.Cvar(CvarProxDecapDps, DefProxDecapDps) * CpThinkRate);
            if (hlt < 0f)
                self.GtObjHealth += hlt; // QC TakeResource(RES_HEALTH, -hlt)
            else if (hlt > 0f)
                self.GtObjHealth = MathF.Min(self.GtObjHealth + hlt, self.GtObjMaxHealth); // QC GiveResourceWithLimit

            if (self.GtObjHealth <= 0f)
            {
                // QC: _enemy_count >= 1 here. With >1 enemy, pass `self` as the attacker so IconDamage spreads
                // the ONS_TAKES reward to every nearby enemy (the proximity self-destruct branch); else the lone enemy.
                Entity attacker = enemy > 1 ? self : (Entity?)firstEnemy ?? self;
                IconDamage(self, self, attacker, DeathTypes.Void, 1f, self.Origin, Vector3.Zero);
                return;
            }
        }

        // QC: if (time > pain_finished + 5) regenerate up to max at the slow rate.
        if (Now > self.GtPainFinished + 5f && self.GtObjHealth < self.GtObjMaxHealth)
        {
            self.GtObjHealth += self.GtBuildRate;
            if (self.GtObjHealth > self.GtObjMaxHealth)
                self.GtObjHealth = self.GtObjMaxHealth;
        }

        // QC ons_ControlPoint_Icon_Think (sv_onslaught.qc:527-540): when the point's powered (.islinked) state
        // flips, re-fire its map .target (SUB_UseTargets) so linked spawnpoints re-team — with the CP's team
        // temporarily zeroed while it's freshly UN-linked (a cut-off point unteams its spawnpoints). Fires once
        // per flip via the node's WasLinked latch.
        if (node is not null && node.Linked != node.WasLinked)
        {
            Entity? cpEnt = ControlPointEntity(cpId);
            if (cpEnt is not null && cpEnt.Target.Length > 0)
            {
                float t = cpEnt.Team;
                if (!node.Linked)
                    cpEnt.Team = Teams.None; // QC: if(!islinked) this.owner.team = 0
                MapMover.UseTargets(cpEnt, self, null);
                cpEnt.Team = t;              // QC: this.owner.team = t
            }
            node.WasLinked = node.Linked;
        }

        // QC ons_ControlPoint_Icon_Think damaged-fx (sv_onslaught.qc:542-551): the more damaged the icon, the
        // more often it sputters — at chance random() < 0.6 - hp/max it spawns an EFFECT_ELECTRIC_SPARKS
        // particle (client/presentation, deferred) AND plays an ambient spark crackle: ONS_SPARK1 at
        // random()>0.8, else ONS_SPARK2 at random()>0.5 (so ~20% SPARK1, ~30% SPARK2, ~50% silent that roll).
        if (Api.Services is not null && self.GtObjMaxHealth > 0f)
        {
            float frac = self.GtObjHealth / self.GtObjMaxHealth;
            if (XonoticGodot.Common.Math.Prandom.Float() < 0.6f - frac)
            {
                // QC re-rolls random() twice here; reproduce the exact branch structure (NOT else-if on one roll).
                if (XonoticGodot.Common.Math.Prandom.Float() > 0.8f)
                    SoundSystem.PlayOn(self, Sounds.ByName("ONS_SPARK1"));
                else if (XonoticGodot.Common.Math.Prandom.Float() > 0.5f)
                    SoundSystem.PlayOn(self, Sounds.ByName("ONS_SPARK2"));
            }
        }
    }

    /// <summary>
    /// QC ons_ControlPoint_Icon_Damage (sv_onslaught.qc:365): the icon's event_damage. A shielded point ignores
    /// the damage; otherwise subtract, and at &lt;=0 health destroy the icon — the point reverts to neutral
    /// (QC owner.iscaptured/islinked = false, team = 0, onslaught_updatelinks) and the destroyer is credited
    /// ONS_TAKES +1 / SCORE +10. The build is aborted (the point never captured).
    /// </summary>
    public void IconDamage(Entity self, Entity? inflictor, Entity? attacker, string deathType,
        float damage, Vector3 hitLoc, Vector3 force)
    {
        if (damage <= 0f) return;
        int cpId = self.GtIconCpId;
        Onslaught.OnsNode? node = _ons.ControlPointNode(cpId);

        // QC: this is protected by a shield → ignore the damage (only the blocked-by-shield hint plays).
        if (node is not null && node.Shielded)
        {
            // QC ons_ControlPoint_Icon_Damage (sv_onslaught.qc:369-381): play the blocked-by-shield hint to the
            // attacker (debounced by pain_finished).
            if (attacker is Player blocked && Now > self.GtPainFinished)
            {
                // QC sv_onslaught.qc:375 play2(attacker, SND(ONS_DAMAGEBLOCKEDBYSHIELD)) — a per-recipient 2D cue
                // (CH_INFO / VOL_BASE / ATTEN_NONE), not a positional emit on the attacker.
                if (Api.Services is not null)
                    SoundSystem.Play2(blocked, Sounds.ByName("ONS_DAMAGEBLOCKEDBYSHIELD"));
                self.GtPainFinished = Now + 1f;
            }
            return;
        }

        self.GtObjHealth -= damage;
        self.GtPainFinished = Now + 1f;

        // QC cl_controlpoint.qc cpicon_damage: the icon's visible model cracks with its health fraction (QC swaps
        // MDL_ONS_CP → CP1/CP2/CP3 by 75/50/25%). Resolve it server-side so a damaged capture icon shows the wear
        // (one of the '4 CP icon models' the spec lists as missing). The build-bar alpha + punch-on-hit stay client-side.
        if (self.GtObjMaxHealth > 0f && self.GtObjHealth > 0f)
            self.Model = IconModelForFraction(self.GtObjHealth / self.GtObjMaxHealth);

        // QC ons_ControlPoint_Icon_Damage (sv_onslaught.qc:383-388): notify the owning team that their CP is
        // under attack (play2team, debounced per team by ons_notification_time[this.team] with a 10 s window).
        // Base: if(IS_PLAYER(attacker)) if(time - ons_notification_time[this.team] > 10) { play2team(...); ... }
        if (attacker is Player && Api.Services is not null)
        {
            int cpTeam = self.GtHomeTeam;
            float lastNotify = _cpNotifyTime.GetValueOrDefault(cpTeam, float.MinValue);
            if (Now - lastNotify > 10f)
            {
                _cpNotifyTime[cpTeam] = Now;
                SoundSystem.Play2Team(cpTeam, Sounds.ByName("ONS_CONTROLPOINT_UNDERATTACK"),
                    new List<Entity>(Api.Entities.FindByClass("player")));
            }
        }

        // QC ons_ControlPoint_Icon_Damage (sv_onslaught.qc:399-402): a hit sound on every hit (random 1/2).
        if (Api.Services is not null)
            SoundSystem.PlayOn(self, Sounds.ByName(
                    XonoticGodot.Common.Math.Prandom.Float() < 0.5f ? "ONS_HIT1" : "ONS_HIT2"),
                SoundLevels.VolBase + 0.3f, SoundLevels.AttenNorm);

        if (self.GtObjHealth <= 0f)
        {
            // QC ons_ControlPoint_Icon_Damage (sv_onslaught.qc:406): the destroy/explode sound.
            if (Api.Services is not null)
                SoundSystem.PlayOn(self, Sounds.ByName("ONS_GENERATOR_EXPLODE"));

            // QC: award the destroyer, then reset the owner point to neutral. The attacker shown in the notify
            // is the weapon attacker normally, or — on the proximity self-destruct (attacker == self) — the first
            // nearby enemy, with the ONS_TAKES/SCORE reward spread to EVERY nearby enemy.
            Player? notifyAttacker = null;
            if (ReferenceEquals(attacker, self))
            {
                // QC ons_ControlPoint_Icon_Damage proximity self-destruct (sv_onslaught.qc:416-429): reward all
                // live enemy players within g_onslaught_cp_proximitydecap_distance with ONS_TAKES +1 / SCORE +5.
                int ownerTeam = node?.Team ?? Teams.None;
                if (Api.Services is not null)
                {
                    float dist = GametypeEntities.Cvar(CvarProxDecapDist, DefProxDecapDist);
                    float dist2 = dist * dist;
                    foreach (Entity e in Api.Entities.FindByClass("player"))
                    {
                        if (e is not Player p || (p.Flags & EntFlags.Client) == 0 || p.IsDead)
                            continue;
                        if ((int)p.Team == ownerTeam || (int)p.Team == Teams.None)
                            continue; // QC DIFF_TEAM(it, this)
                        if ((p.Origin - self.Origin).LengthSquared() >= dist2)
                            continue;
                        notifyAttacker ??= p; // QC: show the message only for the first such player
                        AddCol(p, "ONS_TAKES", 1); // QC GameRules_scoring_add(it, ONS_TAKES, 1)
                        p.ScoreFrags += 5;         // QC GameRules_scoring_add(it, SCORE, 5)
                    }
                }
            }
            else if (attacker is Player by)
            {
                AddCol(by, "ONS_TAKES", 1); // QC GameRules_scoring_add(attacker, ONS_TAKES, 1)
                by.ScoreFrags += 10;        // QC GameRules_scoring_add(attacker, SCORE, 10)
                notifyAttacker = by;
            }

            if (notifyAttacker is not null)
            {
                // QC ons_ControlPoint_Icon_Damage (sv_onslaught.qc:431-434): the CP-destroyed info notify. The
                // port's CP has no map .message, so use the NONAME (by-attacker) variant.
                int destroyedTeam = node?.Team ?? Teams.None;
                Onslaught.Notify(NotifBroadcast.All, MsgType.Info,
                    $"ONSLAUGHT_CPDESTROYED_NONAME_{Onslaught.TeamSuffix(destroyedTeam)}", notifyAttacker.NetName);
            }

            // QC: owner.goalentity = NULL; islinked = iscaptured = false; team = 0; onslaught_updatelinks().
            _icons.Remove(cpId);
            if (node is not null)
            {
                node.Team = Teams.None;
                node.Captured = false;
                node.Linked = false;
            }
            _ons.UpdateLinks();

            // QC ons_ControlPoint_Icon_Damage (sv_onslaught.qc:447): SUB_UseTargets(this.owner, this, NULL) —
            // fire the reverted control point's map .target (server-side map logic) when the icon is destroyed.
            Entity? cpEnt = ControlPointEntity(cpId);
            if (cpEnt is not null && cpEnt.Target.Length > 0)
                MapMover.UseTargets(cpEnt, self, null);

            // delete(this): retire the icon entity.
            self.Think = null;
            self.NextThink = 0f;
            self.TakeDamage = DamageMode.No;
            self.GtEventDamage = null;
            if (Api.Services is not null)
                Api.Entities.Remove(self);
        }
    }

    /// <summary>
    /// QC ons_ControlPoint_Icon_Heal (sv_onslaught.qc:460): heal an icon's RES_HEALTH toward its limit (the
    /// Healer mutator / heal trigger). Returns true if any health was added. No-op once destroyed or full.
    /// </summary>
    public bool IconHeal(Entity icon, float amount, float limit = 0f)
    {
        float trueLimit = limit > 0f ? limit : icon.GtObjMaxHealth;
        if (icon.GtObjHealth <= 0f || icon.GtObjHealth >= trueLimit)
            return false;
        icon.GtObjHealth = MathF.Min(icon.GtObjHealth + amount, trueLimit);
        return true;
    }

    /// <summary>
    /// QC ons_GeneratorHeal (sv_onslaught.qc:1005): heal a generator's RES_HEALTH toward its limit (a friendly
    /// Arc beam / mage / bumblebee healgun, dispatched via <see cref="Combat.Heal"/>). Mirrors the topped-up
    /// health onto BOTH the entity (GtObjHealth) and the graph <see cref="Onslaught.GeneratorState"/> so the
    /// damage gate stays coherent. Returns true if any health was added. No-op once destroyed or full.
    /// </summary>
    public bool GeneratorHeal(int team, Entity gen, float amount, float limit = 0f)
    {
        // QC: true_limit = (limit != RES_LIMIT_NONE) ? limit : max_health. The port passes 0/Resources.LimitNone
        // for "uncapped" → fall back to max_health, matching QC.
        float trueLimit = limit > 0f ? limit : gen.GtObjMaxHealth;
        if (gen.GtObjHealth <= 0f || gen.GtObjHealth >= trueLimit)
            return false;
        gen.GtObjHealth = MathF.Min(gen.GtObjHealth + amount, trueLimit);
        Onslaught.GeneratorState? state = _ons.GeneratorFor(team);
        if (state is not null)
            state.Health = gen.GtObjHealth; // keep the graph source-of-truth in sync with the entity
        return true;
    }

    // ============================================================================================
    //  Overtime generator-decay (QC the stalemate branch of Onslaught_CheckWinner :1172)
    // ============================================================================================

    /// <summary>
    /// QC Onslaught_CheckWinner overtime slice (sv_onslaught.qc:1172): once the timelimit (or the round end
    /// time) elapses, every generator self-damages by <c>max_health / max(30, 60 * timelimit_suddendeath)</c>
    /// per second, scaled UP by the number of enemy-linked control points (so a team that controls more of the
    /// map decays the enemy generator faster). The self-damage runs through <see cref="GeneratorDamage"/> (the
    /// attacker == the generator path), so it bypasses the shield and ends the round when a generator falls.
    /// Call once per second from the round handler / EndFrame when the match is in overtime.
    /// </summary>
    public void OvertimeDecayTick()
    {
        // QC Onslaught_CheckWinner overtime entry (sv_onslaught.qc:1178-1181): the FIRST tick after entering
        // overtime center-prints OVERTIME_CONTROLPOINT to all + plays the generator-decay alarm once (the
        // ons_stalemate edge). OvertimeAnnounced latches it so it fires exactly once per overtime spell.
        if (!OvertimeAnnounced)
        {
            Onslaught.Notify(NotifBroadcast.All, MsgType.Center, "OVERTIME_CONTROLPOINT");
            if (Api.Services is not null)
                SoundSystem.PlayGlobal(Sounds.ByName("ONS_GENERATOR_DECAY"));
        }
        OvertimeAnnounced = true;
        float window = MathF.Max(30f, 60f * GametypeEntities.Cvar(CvarSuddenDeath, DefSuddenDeath));

        // snapshot the generators (the dictionary may shrink as one is destroyed)
        var gens = new List<KeyValuePair<int, Entity>>(_generators);
        foreach (var kv in gens)
        {
            int team = kv.Key;
            Entity gen = kv.Value;
            if (gen.IsFreed || gen.TakeDamage == DamageMode.No)
                continue; // already destroyed

            // d starts at 1, +1 per enemy-linked control point (QC counts DIFF_TEAM linked CPs).
            float d = 1f;
            foreach (Onslaught.OnsNode n in _ons.Nodes)
                if (!n.IsGenerator && n.Linked && n.Team != team && n.Team != Teams.None)
                    d += 1f;

            d *= gen.GtObjMaxHealth / window;
            // QC: Damage(gen, gen, gen, d, DEATH_HURTTRIGGER, ...) — self-inflicted, routes via GeneratorDamage.
            // (DeathTypes.Void is the port's DEATH_HURTTRIGGER alias.)
            Combat.Damage(gen, gen, gen, d, DeathTypes.Void, gen.Origin, Vector3.Zero);
        }
    }

    /// <summary>Reset the overtime announcement latch when the match leaves overtime (QC else branch).</summary>
    public void ClearOvertime() => OvertimeAnnounced = false;

    // ============================================================================================
    //  CaptureShield (QC ons_CaptureShield_Spawn / ons_CaptureShield_Touch / _Customize / _Reset)
    // ============================================================================================

    /// <summary>QC autocvar_g_onslaught_shield_force (default 100) — the push impulse the shield applies.</summary>
    public float ShieldForce => GametypeEntities.Cvar(CvarShieldForce, DefShieldForce);

    /// <summary>
    /// QC ons_CaptureShield_Spawn (sv_onslaught.qc:81): spawn a co-located SOLID_TRIGGER + MOVETYPE_NOCLIP
    /// entity at the node's origin that pushes away any enemy player who touches it WHILE the node is shielded.
    /// The shield is a spinning (avelocity '7 0 11') additive-blend model whose hitbox is 20 % larger than the
    /// node (<see cref="ShieldHitboxScale"/>). It stores a back-link to the node world entity in GtShieldFlag
    /// (QC shield.enemy = this) so the touch handler can read the live shielded state + team. Its visibility
    /// (QC ons_CaptureShield_Customize) is a client concern; the server-side push + center-print are reproduced.
    /// </summary>
    private void SpawnCaptureShield(Entity nodeEnt, string shieldModel)
    {
        if (Api.Services is null)
            return;
        Entity s = Api.Entities.Spawn();
        s.ClassName = "ons_captureshield";
        s.Team = nodeEnt.Team;             // QC shield.team = this.team
        s.GtHomeTeam = nodeEnt.GtHomeTeam;
        s.GtShieldFlag = nodeEnt;          // QC shield.enemy = this (the guarded generator/control point)
        s.Solid = Solid.Trigger;           // QC SOLID_TRIGGER
        s.MoveType = MoveType.Noclip;      // QC MOVETYPE_NOCLIP
        s.Effects |= EfAdditive;           // QC EF_ADDITIVE
        s.AVelocity = new Vector3(7f, 0f, 11f); // QC avelocity '7 0 11'
        s.Model = shieldModel;
        s.Flags |= EntFlags.NoTarget;      // never a bot/weapon target
        // QC: setsize(shield, shield_extra_size * this.mins, shield_extra_size * this.maxs).
        GametypeEntities.SetSize(s, nodeEnt.Mins * ShieldHitboxScale, nodeEnt.Maxs * ShieldHitboxScale);
        GametypeEntities.SetOrigin(s, nodeEnt.Origin);
        s.Touch = ShieldTouchEntity;
    }

    /// <summary>
    /// QC ons_CaptureShield_Touch (sv_onslaught.qc:53): when an enemy player touches the shield WHILE the guarded
    /// node is shielded, push them away (0 damage, <see cref="ShieldForce"/> impulse along the outward normal),
    /// play ONS_DAMAGEBLOCKEDBYSHIELD and center-print the GENERATOR/CONTROLPOINT_SHIELDED reminder. A player on
    /// the node's team, or touching an attackable (un-shielded / capturable) node, passes through freely.
    /// </summary>
    private void ShieldTouchEntity(Entity self, Entity other)
    {
        if (other is not Player toucher || toucher.IsDead)
            return;
        Entity? nodeEnt = self.GtShieldFlag;
        if (nodeEnt is null)
            return;

        bool isGenerator = nodeEnt.ClassName == "onslaught_generator";
        Onslaught.OnsNode? node = isGenerator
            ? _ons.GeneratorNode(nodeEnt.GtHomeTeam)
            : _ons.ControlPointNode(nodeEnt.GtPointId);
        if (node is null)
            return;

        // QC: if(!this.enemy.isshielded && (ons_ControlPoint_Attackable(this.enemy, toucher.team) > 0 ||
        //         this.enemy.classname != "onslaught_controlpoint")) return; — an attackable (capturable) point
        // or an un-shielded generator lets the player through (they can build/attack it).
        int toucherTeam = (int)toucher.Team;
        bool attackable = !isGenerator && _ons.IsAttackable(node, toucherTeam);
        if (!node.Shielded && (attackable || isGenerator))
            return;
        // QC: if(SAME_TEAM(toucher, this)) return; — same-team players are never pushed.
        if (toucherTeam == self.Team)
            return;

        // QC: mymid = (absmin+absmax)*0.5; theirmid = (toucher.absmin+toucher.absmax)*0.5;
        //     Damage(toucher, this, this, 0, DEATH_HURTTRIGGER, DMG_NOWEP, mymid,
        //            normalize(theirmid - mymid) * ons_captureshield_force);
        Vector3 myMid    = (self.AbsMin + self.AbsMax) * 0.5f;
        Vector3 theirMid = (toucher.AbsMin + toucher.AbsMax) * 0.5f;
        Vector3 pushDir  = QNormalize(theirMid - myMid);
        Combat.Damage(toucher, self, self, 0f, DeathTypes.Void, myMid, pushDir * ShieldForce);

        // QC: if(IS_REAL_CLIENT(toucher)) { play2(...ONS_DAMAGEBLOCKEDBYSHIELD); Send_Notification(...SHIELDED); }
        if ((toucher.Flags & EntFlags.Client) != 0 && !toucher.IsBot && Api.Services is not null)
        {
            SoundSystem.Play2(toucher, Sounds.ByName("ONS_DAMAGEBLOCKEDBYSHIELD"));
            NotificationSystem.Send(NotifBroadcast.One, toucher, MsgType.Center,
                isGenerator ? "ONS_GENERATOR_SHIELDED" : "ONS_CONTROLPOINT_SHIELDED");
        }
    }

    private static Vector3 QNormalize(Vector3 v) { float l = v.Length(); return l > 0f ? v / l : Vector3.Zero; }

    // ============================================================================================
    //  helpers
    // ============================================================================================

    /// <summary>
    /// QC ons_ControlPoint_CanBeLinked: a point is buildable/holdable by <paramref name="team"/> only while it
    /// neighbors a powered (linked) node owned by that team. We derive it from the existing graph: any linked
    /// same-team neighbor (generator or captured CP). A point cut off from power pauses its build (QC).
    /// </summary>
    private static bool CanBeLinked(Onslaught.OnsNode node, int team)
    {
        foreach (Onslaught.OnsNode m in node.Neighbors)
            if (m.Linked && m.Captured && m.Team == team)
                return true;
        return false;
    }

    /// <summary>The player who started the build on a control point (QC cp.ons_toucher), or null.</summary>
    private Player? ControlPointBuilder(int cpId) => ControlPointEntity(cpId)?.GtCapturer as Player;

    /// <summary>The control-point world entity (QC the icon's <c>this.owner</c>) for a given id, or null.</summary>
    private Entity? ControlPointEntity(int cpId)
    {
        if (Api.Services is null)
            return null;
        foreach (Entity e in Api.Entities.FindByClass("onslaught_controlpoint"))
            if (e.GtPointId == cpId)
                return e;
        return null;
    }

    /// <summary>QC: warmup_stage/game_stopped gate + round_handler_IsRoundStarted. Headlessly: the round handler
    /// (when present) must report the round started; with no handler wired (a bare unit test) treat it as started
    /// so the deterministic combat is exercisable.</summary>
    private bool RoundStarted()
        => _ons.Handler is null || _ons.Handler.IsRoundStarted;

    /// <summary>QC GameRules_scoring_add(player, SP_X, n) for an ONS column (no-op if unregistered).</summary>
    private static void AddCol(Player p, string field, int n)
    {
        Scoring.ScoreField? f = Scoring.GameScores.Field(field);
        if (f is not null) Scoring.GameScores.AddToPlayer(p, f, n);
    }
}
