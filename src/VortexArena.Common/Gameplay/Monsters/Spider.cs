using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using XonoticGodot.Common.Gameplay.Damage;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Spider — port of common/monsters/monster/spider.{qh,qc}. A mechanical arachnid that slows fast prey
/// with a web projectile, then closes in for high-damage bites. MON_FLAG_MELEE | MON_FLAG_RANGED | MON_FLAG_RIDE.
///
/// Identity/size from spider.qh; health/damage/speeds from monsters.cfg (g_monster_spider_*). Fully ported:
/// the bite (melee), the bouncing web projectile (ranged, within web range) and the Webbed status it applies
/// in radius on impact (halving the victim's move speed — and any webbed monster's, mirrored in MonsterAI's
/// move-speed scaling). Only client frame playback and the web projectile model are CSQC.
/// </summary>
[Monster]
public sealed class Spider : Monster
{
    // Balance — g_monster_spider_* (monsters.cfg).
    public float BiteDamage = 35f;      // g_monster_spider_attack_bite_damage
    public float BiteDelay = 1.5f;      // g_monster_spider_attack_bite_delay
    public float WebDelay = 3f;         // g_monster_spider_attack_web_delay
    public float WebSpeed = 1300f;      // g_monster_spider_attack_web_speed
    public float WebSpeedUp = 150f;     // g_monster_spider_attack_web_speed_up
    public float WebRange = 800f;       // g_monster_spider_attack_web_range
    public float WebDamageTime = 7f;    // g_monster_spider_attack_web_damagetime (Webbed duration)
    public float SpeedWalk = 400f;      // g_monster_spider_speed_walk
    public float SpeedRun = 500f;       // g_monster_spider_speed_run
    public float SpeedStop = 100f;      // g_monster_spider_speed_stop

    public Spider()
    {
        NetName = "spider";
        DisplayName = "Spider";
        Model = "models/monsters/spider.dpm";
        // The Base spider ships NO models/monsters/spider.dpm_*.sounds file (only golem + zombie do), so QC
        // Monster_Sound resolves every voice cue (pain/death/melee/sight/idle/spawn) to an EMPTY sample and
        // sound7 plays nothing — the Base spider is silent for all voice cues (only its electro_fire2 web cue,
        // played directly via SND_SpiderAttack_FIRE, sounds). An empty SoundCues set mirrors that: MonsterSound
        // advances the throttle window but emits no sample, so no port-invented monsters/spider_<cue>.wav fires.
        SoundCues = new System.Collections.Generic.HashSet<string>();
        StartHealth = 180f;             // g_monster_spider_health
        Damage = 35f;                   // bite damage
        Speed = 500f;                   // run speed
    }

    // METHOD(Spider, mr_pain) — spider.qc:192: pain_finished = animstate_endtime, i.e. the length of the pain
    // anim group. The spider's pain1/pain2 groups (spider.dpm.framegroups: '298 11 25' / '309 11 25') are 11
    // frames at 25 fps = 0.44s, wider than the generic 0.34s baseline. MarkPain reads this as the pain window.
    public override float PainWindow => 11f / 25f; // anim_pain1/pain2 '7/8' = 11 frames @ 25 fps

    // METHOD(Spider, mr_anim) — spider.qc:208. The spider.dpm frame-group start indices (spider.dpm.framegroups:
    // group 0 = bite, 1 = death01, 2 = death02, 3 = fire01, 5 = idle, 7 = pain01, 8 = pain02, 10 = walkforward).
    // QC stamps the first component of each animfixfps vector onto .frame; DriveAnimFrame plays it client-side
    // (CSQCMODEL_AUTOUPDATE). The logical Attack phase covers both the bite (anim_melee '0') and the web shoot
    // (anim_shoot '3'); QC mr_pain/mr_death pick pain1/pain2 and die1/die2 at random — the driver's per-event
    // AnimVariant carries the pain pick, and the death die1/die2 pair maps off the corpse-pose flag like the
    // sibling monsters (Mage/Zombie).
    public override float? AnimFrame(MonsterAnimPhase phase, bool die2) => AnimFrame(phase, die2, 0);

    public override float? AnimFrame(MonsterAnimPhase phase, bool die2, int variant) => phase switch
    {
        MonsterAnimPhase.Idle => 5f,                // anim_idle '5 1 1'
        MonsterAnimPhase.Walk => 10f,               // anim_walk '10 1 1'
        MonsterAnimPhase.Run => 10f,                // anim_run '10 1 1' (same group as walk in QC)
        MonsterAnimPhase.Attack => 0f,              // anim_melee '0 1 5' (bite); anim_shoot '3' folds in like siblings
        MonsterAnimPhase.Shoot => 3f,               // anim_shoot '3 1 1' (web fire)
        MonsterAnimPhase.Pain => variant != 0 ? 8f : 7f, // anim_pain1 '7 1 1' / anim_pain2 '8 1 1' (QC random per pain)
        MonsterAnimPhase.Death => die2 ? 2f : 1f,   // anim_die1 '1 1 1' / anim_die2 '2 1 1' (QC random pick)
        _ => 5f,                                     // fall back to idle
    };

    // METHOD(Spider, mr_death) — spider.qc:200: setanim(random() > 0.5 ? anim_die2 : anim_die1). A HIGH roll
    // picks anim_die2 (frame 2), a low roll picks anim_die1 (frame 1). MonsterAI.MarkDead calls this once,
    // stores the result in MonsterState.DeathLanded, and DriveAnimFrame passes it as the die2 flag to AnimFrame
    // above. Without this override the base defaults to false (die1 always), dropping the QC random die1/die2
    // pick (matching the sibling Zombie, which also overrides RollDeathVariant).
    public override bool RollDeathVariant() => MonsterRandom.Next() > 0.5f;

    // METHOD(Spider, describe) — spider.qc:252-260. The MENUQC bestiary prose describing the spider.
    public override string? Describe() =>
        "The Spider is a large mechanically-enhanced arachnoid adept at hunting speedy enemies. " +
        "To slow down its target, the Spider launches a synthetic web-like substance from its cannons. " +
        "Approaching its enwebbed prey, the Spider will inflict a series of high damage bites.";

    // Monster_Spawn + METHOD(Spider, mr_setup) — spider.qc
    public override void Spawn(Entity e)
    {
        // mr_setup: if (!RES_HEALTH) SetResourceExplicit(RES_HEALTH, g_monster_spider_health). Seed StartHealth
        // from the cvar BEFORE Setup so a server override of g_monster_spider_health is honoured (Setup seeds
        // e.Health from StartHealth then applies MONSTER_SKILLMOD, matching QC's health *= skillmod ordering).
        StartHealth = MonsterAI.Cvar("g_monster_spider_health", StartHealth);

        var st = MonsterAI.Setup(this, e);
        // QC spider mr_setup: setsize('-30 -30 -25', '30 30 30'). QC setsize() also seeds the eye height
        // view_ofs = 0.35 * maxs.z (= 0.35 * 30 = 10.5) used by the monster's aim/LOS traces
        // (MonsterAI uses self.Origin + self.ViewOfs); Api.Entities.SetSize only writes mins/maxs, so seed
        // view_ofs explicitly here. (No Quake-resize: the spider is Xonotic-native, SizeQuake == false.)
        var maxs = new Vector3(30, 30, 30);
        Api.Entities.SetSize(e, new Vector3(-30, -30, -25), maxs);
        e.ViewOfs = new Vector3(0, 0, 0.35f * maxs.Z);

        st.WalkSpeed = MonsterAI.Cvar("g_monster_spider_speed_walk", SpeedWalk);
        st.RunSpeed = MonsterAI.Cvar("g_monster_spider_speed_run", SpeedRun);
        st.StopSpeed = MonsterAI.Cvar("g_monster_spider_speed_stop", SpeedStop);
        st.DamageForceScale = MonsterAI.Cvar("g_monster_spider_damageforcescale", 0.6f);
        st.MonsterLoot = MonsterAI.CvarString("g_monster_spider_loot", "health_medium");
    }

    // METHOD(Spider, mr_think) — spider.qc
    public override void Think(Entity e)
    {
        var st = MonsterAI.StateOf(e);
        if (st is null) return;
        MonsterAI.RunThink(e, st);
    }

    // M_Spider_Attack (spider.qc): melee bite when close, web projectile when within web range.
    public override void Attack(Entity e, Entity target)
    {
        var st = MonsterAI.StateOf(e);
        if (st is null) return;

        float dist = (target.Origin - e.Origin).Length();
        if (dist <= st.AttackRange)
        {
            // MONSTER_ATTACK_MELEE -> bite. QC wr_think (fire & 2) picks the bite anim at random:
            //   Monster_Attack_Melee(..., ((random() > 0.5) ? actor.anim_melee : actor.anim_shoot), ...)
            // so each bite plays EITHER the melee group (anim_melee '0', the Attack phase) OR the shoot group
            // (anim_shoot '3', the Shoot phase). Mirror that coin-flip onto the networked anim phase (the
            // chosen frame is stamped on Entity.Frame by DriveAnimFrame, same as the random pain/death picks).
            st.Anim = MonsterRandom.Next() > 0.5f ? MonsterAI.MonsterAnim.Shoot : MonsterAI.MonsterAnim.Attack;
            // spider.qc M_Spider_Attack melee: the bite is DEATH_MONSTER_SPIDER.
            MonsterAI.MeleeAttack(e, st, BiteDamage, st.AttackRange, BiteDelay,
                DeathTypes.MonsterSpider);
        }
        else if (dist <= WebRange && MonsterAI.Now >= st.AttackDelay)
        {
            // MONSTER_ATTACK_RANGED -> launch a web (SpiderAttack wr_think, fire 1).
            FireWeb(e, st, target);
        }
    }

    // M_Spider_Attack_Web (spider.qc): spawn the bouncing web projectile aimed at the enemy. On impact it
    // applies STATUSEFFECT_Webbed in a small radius (no direct damage), halving move speed for web_damagetime.
    private void FireWeb(Entity e, MonsterAI.MonsterState st, Entity target)
    {
        st.AttackDelay = MonsterAI.Now + WebDelay;
        st.AttackFinished = MonsterAI.Now + WebDelay;
        // QC wr_think (NPC web branch): setanim(actor.anim_shoot) — the spider plays its web-fire group ('3'),
        // distinct from the bite's anim_melee ('0'). Drive the Shoot phase so DriveAnimFrame stamps frame 3.
        st.Anim = MonsterAI.MonsterAnim.Shoot;

        MonsterAI.FaceTarget(e, target);
        Vector3 dir = QMath.Normalize((target.Origin + new Vector3(0, 0, 10)) - e.Origin);

        float webDuration = MonsterAI.Cvar("g_monster_spider_attack_web_damagetime", WebDamageTime);

        // The web does no direct impact damage (radius 0 damage); its effect is the Webbed slow on contact.
        // It bounces (bouncefactor 0.3 / bouncestop 0.05) until it hits something and pops.
        Entity web = MonsterAI.SpawnProjectile(e, st, dir, WebSpeed, damage: 0f, edgeDamage: 0f,
            radius: 25f, force: 0f, deathType: DeathTypes.MonsterSpider, // spider.qc: DEATH_MONSTER_SPIDER
            moveType: MoveType.Bounce, lifetime: 5f,
            bounceFactor: 0.3f, bounceStop: 0.05f,
            makeTrigger: true,              // spider.qc:134 PROJECTILE_MAKETRIGGER
            onExplode: p =>
            {
                // M_Spider_Attack_Web_Explode: Send_Effect(EFFECT_ELECTRO_IMPACT, this.origin, '0 0 0', 1).
                EffectEmitter.Emit("ELECTRO_IMPACT", p.Origin);

                // STATUSEFFECT_Webbed to everything alive in radius except other spiders (QC web explode).
                foreach (Entity it in Api.Entities.FindInRadius(p.Origin, 25f))
                {
                    if (it == p) continue;
                    if (it.TakeDamage == DamageMode.No) continue;
                    if (it.DeadState != DeadFlag.No || it.Health <= 0f) continue;
                    if (it.NetName == "spider") continue; // spiders are immune to their own web
                    MonsterFramework.ApplyFor(MonsterFramework.Webbed, it, webDuration, 1f, p.Owner);
                }
            });
        // QC nets the web as CSQCProjectile(proj, true, PROJECTILE_ELECTRO, true): the electro ORB visual
        // (ebomb model + TR_NEXUIZPLASMA plasma trail + electro_fly loop). SpawnProjectile stamps NetName from
        // the monster def ("spider"), which ProjectileCatalog.Resolve maps to the GENERIC fallback. Override the
        // NetName so Resolve's `Has(s, "electro_orb", "electro")` branch matches -> ProjectileType.Electro,
        // giving the web its QC-faithful orb/plasma/electro_fly presentation. (Deathtype is carried separately
        // via the deathType argument, and the Webbed self-exclusion filter keys off the VICTIM's NetName, so
        // renaming this projectile is safe.)
        web.NetName = "electro_orb";

        // QC W_SetupProjVelocity_Explicit(..., web_speed, web_speed_up, ...) adds an upward launch component.
        web.Velocity += new Vector3(0, 0, MonsterAI.Cvar("g_monster_spider_attack_web_speed_up", WebSpeedUp));

        // QC fires SND_SpiderAttack_FIRE (electro_fire2) TWICE per web shot: once from W_SetupShot_Dir on the
        // weapon channel (CH_WEAPON_B) during wr_think, then again explicitly in M_Spider_Attack_Web on CH_SHOTS.
        // Reproduce both cues (the only sounds the Base spider actually makes — its voice cues are silent; see
        // the empty SoundCues set above).
        Api.Sound.Play(e, SoundChannel.Weapon, "weapons/electro_fire2.wav");   // W_SetupShot_Dir SND_SpiderAttack_FIRE
        Api.Sound.Play(e, SoundChannel.ShotsAuto, "weapons/electro_fire2.wav"); // M_Spider_Attack_Web sound() CH_SHOTS
    }
}

/// <summary>
/// The spiderweb player-slow — port of the QC
/// <c>MUTATOR_HOOKFUNCTION(spiderweb, PlayerPhysics_UpdateStats)</c> (spider.qc:26):
/// <c>if (StatusEffects_active(STATUSEFFECT_Webbed, player)) STAT(MOVEVARS_HIGHSPEED, player) *= 0.5;</c>.
/// A webbed player (caught in a spider's web blast) moves at HALF top speed for the Webbed duration — the
/// spider's signature crowd-control. A self-contained <c>[Mutator]</c> riding <c>MutatorHooks.PlayerPhysics</c>
/// (the same hook the entrap-nade / Speed-powerup / buff slows ride) that multiplies <see cref="Entity.SpeedMultiplier"/>
/// by 0.5; the PlayerPhysics integrator folds it into <c>MovementParameters.ApplyHighSpeed</c>. QC registers the
/// spiderweb mutator with <c>REGISTER_MUTATOR(spiderweb, true)</c> — always enabled — so the only gate here is a
/// live service context.
/// </summary>
[Mutator]
public sealed class SpiderWebMutator : MutatorBase
{
    public SpiderWebMutator() => NetName = "spiderweb";

    // QC: REGISTER_MUTATOR(spiderweb, true) — unconditionally enabled.
    public override bool IsEnabled => Api.Services is not null;

    private HookHandler<MutatorHooks.PlayerPhysicsArgs>? _onPhysics;

    public override void Hook()
    {
        _onPhysics ??= OnPlayerPhysics;
        MutatorHooks.PlayerPhysics.Add(_onPhysics);
    }

    public override void Unhook()
    {
        if (_onPhysics is not null) MutatorHooks.PlayerPhysics.Remove(_onPhysics);
    }

    // MUTATOR_HOOKFUNCTION(spiderweb, PlayerPhysics_UpdateStats):
    //   if (StatusEffects_active(STATUSEFFECT_Webbed, player)) STAT(MOVEVARS_HIGHSPEED, player) *= 0.5;
    private bool OnPlayerPhysics(ref MutatorHooks.PlayerPhysicsArgs args)
    {
        Entity player = args.Player;
        if (StatusEffectsCatalog.Has(player, MonsterFramework.Webbed))
            player.SpeedMultiplier *= 0.5f;
        return false;
    }
}
