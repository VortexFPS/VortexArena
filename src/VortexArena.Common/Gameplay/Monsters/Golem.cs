using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using XonoticGodot.Common.Gameplay.Damage;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Golem — port of common/monsters/monster/golem.{qh,qc}. A powerful supermonster: a series of melee
/// punches up close, an occasional leaping ground-smash AoE, and a thrown electrified lightning chunk that
/// zaps nearby targets at range. MON_FLAG_SUPERMONSTER | MON_FLAG_MELEE | MON_FLAG_RANGED.
///
/// Registered (per task) under the NetName "golem"; this descriptor also covers the legacy
/// <c>monster_shambler</c> / <c>monster_dog</c> map spawns. Identity/size from golem.qh; health/damage/speeds
/// from monsters.cfg (g_monster_golem_*). Fully ported: the multi-swing melee combo, the delayed ground
/// smash (radius damage + knockback), and the thrown lightning chunk — which bounces, is itself shootable
/// (50 hp; destroying it detonates it early), fuses after 5s, and on detonation chains lightning zaps to
/// every target in a wide radius. Only client frame playback + the lightning arc beams are CSQC.
/// </summary>
[Monster]
public sealed class Golem : Monster
{
    // Balance — g_monster_golem_* (monsters.cfg).
    public float ClawDamage = 60f;          // g_monster_golem_attack_claw_damage
    public float SmashDamage = 50f;         // g_monster_golem_attack_smash_damage
    public float SmashForce = 100f;         // g_monster_golem_attack_smash_force
    public float SmashRange = 200f;         // g_monster_golem_attack_smash_range
    public float LightningDamage = 25f;     // g_monster_golem_attack_lightning_damage
    public float LightningDamageZap = 15f;  // g_monster_golem_attack_lightning_damage_zap
    public float LightningForce = 100f;     // g_monster_golem_attack_lightning_force
    public float LightningRadius = 50f;     // g_monster_golem_attack_lightning_radius
    public float LightningRadiusZap = 250f; // g_monster_golem_attack_lightning_radius_zap
    public float LightningSpeed = 1000f;    // g_monster_golem_attack_lightning_speed
    public float LightningSpeedUp = 150f;   // g_monster_golem_attack_lightning_speed_up
    public float SpeedWalk = 150f;          // g_monster_golem_speed_walk
    public float SpeedRun = 320f;           // g_monster_golem_speed_run
    public float SpeedStop = 300f;          // g_monster_golem_speed_stop

    // METHOD(Golem, describe) — golem.qc:308 (MENUQC): the 3-paragraph monsterpedia entry, built via
    // PAGE_TEXT_INIT() + three PAR(...) paragraphs joined by "\n\n" (lib/string.qh:659). Surfaced as descriptor
    // data; the port has no monster info-page UI yet (same as DisplayName), so nothing renders it.
    public override string Description =>
        "Golems are large powerful brutes capable of taking and dealing a beating. Keeping your distance is advised."
        + "\n\n"
        + "The Golem's primary melee attack is a series of punches. "
        + "On occasion the Golem may jump into the air, dealing massive damage in an area as it slams the ground."
        + "\n\n"
        + "To deal with distant foes, the Golem may throw a chunk of its electrified rocky exterior, zapping nearby targets on impact.";

    // METHOD(Golem, mr_pain) — golem.qc:240: actor.pain_finished = time + 0.5 (the golem holds its pain
    // reaction the full anim_pain1/pain2 '7/8 1 2' window of 0.5s, wider than the generic 0.34s).
    public override float PainWindow => 0.5f;

    // The golem voice pack ships under sound/monsters/golem/ (subdir, NOT a flat monsters/golem_<cue> path),
    // with the group counts from models/monsters/golem.dpm_0.sounds: death 3, pain 2, idle 2, sight/melee 0
    // (bare name). //spawn and //ranged are commented out there, so those cues resolve to nothing in Base —
    // they are deliberately ABSENT from SoundCues below.
    public override int SoundCueCount(string cue) => cue switch
    {
        "death" => 3,   // golem.dpm_0.sounds: death sound/monsters/golem/death 3
        "pain" => 2,    // pain sound/monsters/golem/pain 2
        "idle" => 2,    // idle sound/monsters/golem/idle 2
        _ => 0,         // sight / melee: count 0 (bare name)
    };

    // METHOD(Golem, mr_anim) — golem.qc:253. The golem MD3 (models/monsters/golem.dpm) frame-group layout
    // (first component of each animfixfps vector = the group start index QC stamps onto .frame):
    //   anim_idle '0 1 1'  anim_walk '1 1 1'  anim_run '2 1 1'  anim_melee2 '4 1 5'  anim_melee3 '5 1 5'
    //   anim_melee1 '6 1 5'  anim_pain1 '7 1 2'  anim_pain2 '8 1 2'  anim_spawn '12 1 5'
    //   anim_die1 '13 1 0.5'  anim_die2 '15 1 0.5'
    // The Attack phase covers both the melee combo (melee2/melee3) and the ranged smash (melee1). QC
    // M_Golem_Attack MELEE: setanim(random ? anim_melee2 : anim_melee3) — variant 0 -> melee2 '4', 1 -> melee3 '5'.
    // QC M_Golem_Attack RANGED smash: setanim(anim_melee1) — variant 2 -> melee1 '6'.
    // MonsterState.AnimVariant is set by QueueCombo (0/1 random) and by Smash() (2) so the correct group is stamped.
    // The 2-arg overload (variant=0, melee2 representative) is the fallback when no explicit variant was chosen.
    public override float? AnimFrame(MonsterAnimPhase phase, bool die2) => AnimFrame(phase, die2, 0);

    public override float? AnimFrame(MonsterAnimPhase phase, bool die2, int variant) => phase switch
    {
        MonsterAnimPhase.Idle => 0f,    // anim_idle '0 1 1'
        MonsterAnimPhase.Walk => 1f,    // anim_walk '1 1 1'
        MonsterAnimPhase.Run => 2f,     // anim_run '2 1 1'
        // anim_melee1 '6 1 5' (variant 2, smash — QC M_Golem_Attack RANGED: setanim(actor.anim_melee1))
        // anim_melee2 '4 1 5' (variant 0) / anim_melee3 '5 1 5' (variant 1) — random per melee combo dispatch.
        MonsterAnimPhase.Attack => variant == 2 ? 6f : (variant != 0 ? 5f : 4f),
        // anim_pain1 '7 1 2' (variant 0) / anim_pain2 '8 1 2' (variant 1) — random per pain event (golem.qc:241).
        MonsterAnimPhase.Pain => variant != 0 ? 8f : 7f,
        MonsterAnimPhase.Spawn => 12f,  // anim_spawn '12 1 5' (golem.qc:270)
        MonsterAnimPhase.Death => die2 ? 15f : 13f, // anim_die1 '13 …' -> anim_die2 '15 …'
        _ => 0f,
    };

    public Golem()
    {
        NetName = "golem";
        DisplayName = "Golem";
        Model = "models/monsters/golem.dpm";
        // Voice cues DEFINED (uncommented) in models/monsters/golem.dpm_0.sounds: death/sight/melee/pain/idle.
        // //ranged and //spawn are commented out there -> empty sample -> Base golem plays NOTHING for those.
        SoundCues = new System.Collections.Generic.HashSet<string> { "death", "sight", "melee", "pain", "idle" };
        StartHealth = 650f;                 // g_monster_golem_health
        Damage = 60f;                       // claw damage
        Speed = 320f;                       // run speed
    }

    // Monster_Spawn + METHOD(Golem, mr_setup) — golem.qc
    public override void Spawn(Entity e)
    {
        var st = MonsterAI.Setup(this, e);
        // QC golem mr_setup: setsize('-24 -24 -20', '24 24 88'). SetSize seeds mins/maxs/size + links the
        // edict but leaves view_ofs at (0,0,0); seed the eye height (0.35 * maxs.z = 30.8) so the AI's
        // CENTER_OR_VIEWOFS eye-origin (self.Origin + self.ViewOfs) traces from the head, not the feet.
        Vector3 maxs = new Vector3(24, 24, 88);
        Api.Entities.SetSize(e, new Vector3(-24, -24, -20), maxs);
        e.ViewOfs = new Vector3(0, 0, 0.35f * maxs.Z);

        st.AttackRange = 150f; // golem.qc mr_setup: attack_range = 150
        st.WalkSpeed = MonsterAI.Cvar("g_monster_golem_speed_walk", SpeedWalk);
        st.RunSpeed = MonsterAI.Cvar("g_monster_golem_speed_run", SpeedRun);
        st.StopSpeed = MonsterAI.Cvar("g_monster_golem_speed_stop", SpeedStop);
        st.DamageForceScale = MonsterAI.Cvar("g_monster_golem_damageforcescale", 0.1f);
        st.MonsterLoot = MonsterAI.CvarString("g_monster_golem_loot", "health_mega electro");

        // golem.qc mr_setup: setanim(anim_spawn); spawn_time = animstate_endtime, gating the brain + applying a
        // matching spawn shield. anim_spawn '12 1 5' is '<group> <framecount> <fps>' => duration framecount/fps
        // = 1/5 = 0.2s (same convention as anim_pain '7 1 2' => 0.5s, anim_die '13 1 0.5' => 2s). So the 0.2s
        // gate IS the QC-derived animstate_endtime, not a guess. Setup already applied the default spawn shield.
        float spawnAnim = 1f / 5f; // anim_spawn '12 1 5': framecount(1) / fps(5)
        st.SpawnTime = MonsterAI.Now + spawnAnim;
        MonsterFramework.ApplyFor(MonsterFramework.SpawnShield, e, spawnAnim);
        // QC mr_setup: setanim(anim_spawn) — play the spawn group '12' during the spawn window; RunThink's
        // spawn_time gate stamps this onto Entity.Frame until SpawnTime, then Move flips it to idle/walk/run.
        st.Anim = MonsterAI.MonsterAnim.Spawn;
    }

    // METHOD(Golem, mr_think) — golem.qc
    public override void Think(Entity e)
    {
        var st = MonsterAI.StateOf(e);
        if (st is null) return;
        MonsterAI.RunThink(e, st);
    }

    // M_Golem_Attack (golem.qc): melee combo up close; at range, either a ground smash (if within smash
    // range) or, less often, a thrown lightning chunk. Ranged is gated by golem_lastattack + grounded.
    public override void Attack(Entity e, Entity target)
    {
        var st = MonsterAI.StateOf(e);
        if (st is null) return;

        float dist = (target.Origin - e.Origin).Length();
        if (dist <= st.AttackRange)
        {
            // MONSTER_ATTACK_MELEE -> a combo of 1..3 claw swings, 0.5s apart (QC Monster_Delay swing_cnt).
            if (MonsterAI.Now < st.AttackFinished) return;
            int swings = System.Math.Clamp((int)MathF.Floor(MonsterRandom.Next() * 4f), 1, 3);
            // golem.qc M_Golem_Attack_Swing: each claw swing is DEATH_MONSTER_GOLEM_CLAW with a 0.8s per-swing
            // animtime (the Monster_Attack_Melee attack_finished window); cadence stays 0.5s × swing count.
            MonsterAI.QueueCombo(e, st, swings, ClawDamage, DeathTypes.MonsterGolemClaw, perSwingAnimTime: 0.8f);
        }
        else
        {
            // MONSTER_ATTACK_RANGED: gated by golem_lastattack (AttackDelay) + grounded (golem.qc).
            if (MonsterAI.Now < st.AttackDelay || (e.Flags & EntFlags.OnGround) == 0) return;

            float roll = MonsterRandom.Next();
            if (roll <= 0.5f && dist <= SmashRange)
            {
                Smash(e, st, target);
            }
            else if (roll <= 0.1f && dist >= SmashRange * 1.5f)
            {
                ThrowLightning(e, st, target);
            }
        }
    }

    // M_Golem_Attack_Smash (golem.qc): leap up and ground-pound, dealing radius damage in front of the golem.
    // QC defers the blast 1.1s behind the jump animation via Monster_Delay; we do the same.
    private void Smash(Entity e, MonsterAI.MonsterState st, Entity target)
    {
        st.AttackDelay = MonsterAI.Now + 3f + MonsterRandom.Next() * 1.5f; // golem_lastattack
        float skill = MonsterAI.SkillMod(st);
        // QC Monster_Attack_Check:478 — the ranged dispatch returns attack_success==1, so the melee voice cue
        // fires once here at dispatch (not on the deferred blast).
        MonsterAI.PlayMeleeCue(e, st);

        MonsterAI.QueueDelayedAttack(e, st, windUp: 1.1f, totalLock: 1.4f, action: self =>
        {
            Vector3 forward = QMath.Forward(self.Angles);
            // golem.qc:27 — Send_Effect(EFFECT_EXPLOSION_MEDIUM, (origin + v_forward*150) - ('0 0 1'*maxs.z), '0 0 0', 1):
            // a ground-pound dust/debris burst in front of the golem, dropped to its feet by the bbox top.
            Vector3 fxOrigin = (self.Origin + forward * 150f) - new Vector3(0, 0, self.Maxs.Z);
            EffectEmitter.Emit("EXPLOSION_MEDIUM", fxOrigin);

            Vector3 loc = self.Origin + forward * 50f;
            // golem.qc M_Golem_Attack_Smash: the ground-pound blast is DEATH_MONSTER_GOLEM_SMASH.
            WeaponSplash.RadiusDamage(self, loc, SmashDamage * skill, SmashDamage * skill * 0.5f, SmashRange,
                self, 0, SmashForce, deathTag: DeathTypes.MonsterGolemSmash);
            Api.Sound.Play(self, SoundChannel.Weapon, "weapons/rocket_impact.wav");
        });
        // QC M_Golem_Attack RANGED smash branch: setanim(actor, actor.anim_melee1, ...) = group '6 1 5'.
        // QueueDelayedAttack stamps the generic MonsterAnim.Attack (which AnimFrame maps to melee2/melee3);
        // override the variant to 2 so AnimFrame routes Attack+variant2 -> frame 6 (anim_melee1), matching QC.
        st.AnimVariant = 2;
    }

    // M_Golem_Attack_Lightning (golem.qc): lob a bouncing electrified chunk. It is shootable (50 hp): if a
    // player destroys it first it detonates early (W_PrepareExplosionByDamage); otherwise it fuses after 5s
    // or on contact. On detonation it does a small radius blast AND chains lightning zaps to every target in
    // a wide radius. QC defers the throw 0.6s behind the animation via Monster_Delay.
    private void ThrowLightning(Entity e, MonsterAI.MonsterState st, Entity target)
    {
        st.AttackDelay = MonsterAI.Now + 3f + MonsterRandom.Next() * 1.5f; // golem_lastattack
        float skill = MonsterAI.SkillMod(st);
        // QC Monster_Attack_Check:478 — ranged dispatch returns attack_success==1, fire the melee voice cue here.
        MonsterAI.PlayMeleeCue(e, st);
        // golem.qc: the thrown chunk's projectiledeathtype + its chained zaps are DEATH_MONSTER_GOLEM_ZAP.
        string zapDeath = DeathTypes.MonsterGolemZap;

        MonsterAI.QueueDelayedAttack(e, st, windUp: 0.6f, totalLock: 1.1f, action: self =>
        {
            if (self.Enemy is null) return;
            MonsterAI.FaceTarget(self, self.Enemy);
            Vector3 dir = QMath.Normalize((self.Enemy.Origin + new Vector3(0, 0, 10)) - self.Origin);

            Entity gren = MonsterAI.SpawnProjectile(self, st, dir, LightningSpeed,
                damage: LightningDamage, edgeDamage: LightningDamage, radius: LightningRadius,
                force: LightningForce, deathType: zapDeath,
                moveType: MoveType.Bounce, lifetime: 5f,
                sizeMin: new Vector3(-8, -8, -8), sizeMax: new Vector3(8, 8, 8),
                shootableHealth: 50f,           // golem.qc: gren takes damage (50 hp), explodes when destroyed
                bounceFactor: 0.5f, bounceStop: 0.075f,
                makeTrigger: true,              // golem.qc:141 PROJECTILE_MAKETRIGGER
                onExplode: p =>
                {
                    // golem.qc:59 — Send_Effect(EFFECT_ELECTRO_IMPACT, this.origin, '0 0 0', 1): the expanding
                    // electro impact puff at the detonation point (matches the spider web explode, Spider.cs:114).
                    EffectEmitter.Emit("ELECTRO_IMPACT", p.Origin);
                    // Chained zaps: arc to every damageable target in the wide zap radius (te_csqc_lightningarc
                    // beam is CSQC), each taking lightning_damage_zap * skillmod (QC FOREACH_ENTITY_RADIUS).
                    MonsterFramework.ChainedZaps(p, p.Owner, p.Origin, LightningRadiusZap,
                        LightningDamageZap, skill, zapDeath);
                    Api.Sound.Play(p, SoundChannel.Weapon, "weapons/electro_impact.wav");
                });
            // QC golem.qc:145 — CSQCProjectile(gren, true, PROJECTILE_GOLEM_LIGHTNING, true) with
            // model models/ebomb.mdl scale 2.5 (golem.qc:141 setsize + :145 gren.scale = 2.5).
            gren.Model = "models/ebomb.mdl";
            // QC sets gren.scale = 2.5 (golem.qc:145) — deferred: Entity carries no networked render-scale field
            // (the CSQCMODEL scale-bit contract is the deferred Wave-8 presentation frontier); the bolt draws at 1x.
            gren.Velocity += new Vector3(0, 0, LightningSpeedUp); // QC speed_up launch component

            // QC M_Golem_Attack_Lightning (golem.qc:132-168) emits NO launch sound — the only golem-lightning
            // cues are SND_MON_GOLEM_LIGHTNING_IMPACT (electro_impact, in onExplode) on detonation and the
            // monstersound_melee voice cue the ranged dispatch plays. No fabricated throw cue here.
        });
        // QC M_Golem_Attack RANGED lightning branch (golem.qc:206): setanim(actor, actor.anim_melee2, …)
        // = anim_melee2 '4' (variant 0), distinct from the smash branch's anim_melee1 '6' (variant 2).
        // QueueDelayedAttack stamps MonsterAnim.Attack but leaves AnimVariant untouched, so a stale variant
        // from a prior smash (2) or melee combo (1) would otherwise mis-render this throw — pin it to 0,
        // mirroring how Smash pins variant 2 for anim_melee1.
        st.AnimVariant = 0;
    }
}
