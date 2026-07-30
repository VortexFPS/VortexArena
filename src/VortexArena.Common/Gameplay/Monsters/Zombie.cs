using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using XonoticGodot.Common.Gameplay.Damage;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Zombie — port of common/monsters/monster/zombie.{qh,qc}. An undead melee bruiser: it charges the
/// nearest player, punches/bites in melee, and (at range) leaps to close the gap dealing contact damage.
/// MONSTER_TYPE_UNDEAD | MON_FLAG_MELEE | MON_FLAG_RIDE.
///
/// Identity/size from zombie.qh; health/damage/speeds from monsters.cfg (g_monster_zombie_*). Fully ported:
/// the melee charge + leap, the three-way melee anim choice, the block/defend stance, always-respawn at the
/// death point, the spawn-shield gating, and the pain/death handlers. Only client frame playback is CSQC.
/// </summary>
[Monster]
public sealed class Zombie : Monster
{
    // Balance — g_monster_zombie_* (monsters.cfg). These are the FALLBACK defaults; the live values are read
    // via MonsterAI.Cvar(...) at attack/spawn time (QC reads the autocvars live in zombie.qc), so a runtime
    // `set g_monster_zombie_attack_*` takes effect. Defaults match the shipped cfg.
    public float MeleeDamage = 55f;     // g_monster_zombie_attack_melee_damage
    public float MeleeDelay = 1f;       // g_monster_zombie_attack_melee_delay
    public float LeapDamage = 60f;      // g_monster_zombie_attack_leap_damage
    public float LeapForce = 55f;       // g_monster_zombie_attack_leap_force
    public float LeapSpeed = 500f;      // g_monster_zombie_attack_leap_speed
    public float LeapDelay = 1.5f;      // g_monster_zombie_attack_leap_delay
    public float SpeedWalk = 300f;      // g_monster_zombie_speed_walk
    public float SpeedRun = 600f;       // g_monster_zombie_speed_run
    public float SpeedStop = 100f;      // g_monster_zombie_speed_stop

    // Model-derived animation group durations (mr_anim: animfixfps uses frameduration(modelindex, groupidx) to
    // override the fallback fps with the actual per-group duration from models/monsters/zombie.dpm.framegroups).
    // QC: spawn_time = animstate_endtime = starttime + frameduration(model,30); the port reads the framegroups
    // file at compile time instead of calling frameduration at runtime (no server-side model load in the port).
    //
    // zombie.dpm.framegroups values (0-based index → numframes fps → duration):
    //   group  0 (anim_shoot '0 1 5'):      attackleap   1  56 30 →  56/30 ≈ 1.8667s
    //   group  4 (anim_melee1/2/3 '4 1 5'): attackstanding1 180 41 30 → 41/30 ≈ 1.3667s
    //   group  7 (anim_blockend '7 1 1'):   blockend    297 21 60 → 21/60 = 0.35s
    //   group  8 (anim_blockstart '8 1 1'): blockstart  318 21 60 → 21/60 = 0.35s
    //   group  9 (anim_die1 '9 1 0.5'):     deathback1  339 96 30 → 96/30 = 3.2s
    //   group 12 (anim_die2 '12 1 0.5'):    deathfront1 573 61 30 → 61/30 ≈ 2.0333s
    //   group 19 (anim_idle '19 1 1'):      idle       1115 121 10 → 121/10 = 12.1s
    //   group 20 (anim_pain1 '20 1 2'):     painback1  1236 11 30 → 11/30 ≈ 0.3667s
    //   group 22 (anim_pain2 '22 1 2'):     painfront1 1258 11 30 → 11/30 ≈ 0.3667s
    //   group 27 (anim_walk/run '27 1 1'):  runforward 1403 41 60 → 41/60 ≈ 0.6833s
    //   group 30 (anim_spawn '30 1 3'):     spawn      1526 61 30 → 61/30 ≈ 2.0333s
    //
    // QC Monster_Attack_Melee/Leap: attack_finished_single[0] = animstate_endtime if animstate_endtime > time,
    // else time + animtime. Since animstate_endtime is always in the future after setanim, the model duration
    // is always used (the QC `animtime` cvar fallback is never reached in practice when the model is loaded).
    private const float AnimDurSpawn  = 61f / 30f; // anim_spawn group 30: 61 frames @ 30 fps = 2.0333s
    private const float AnimDurMelee  = 41f / 30f; // anim_melee1/2/3 group 4: 41 frames @ 30 fps = 1.3667s
    private const float AnimDurLeap   = 56f / 30f; // anim_shoot group 0: 56 frames @ 30 fps = 1.8667s

    // The zombie voice pack ships .ogg (sound/monsters/zombie/{death,sight,idle}.ogg, all count 0 = bare name).
    public override string SoundExt => ".ogg";

    // QC Zombie spawnflags (zombie.qh:11) = MONSTER_TYPE_UNDEAD | MON_FLAG_MELEE | MON_FLAG_RIDE — NO
    // MON_FLAG_RANGED. The zombie is melee-only, so it never acquires a vehicle target (ValidTarget gate).
    public override bool IsRanged => false;

    public Zombie()
    {
        NetName = "zombie";
        DisplayName = "Zombie";
        Model = "models/monsters/zombie.dpm";
        // Voice cues DEFINED in models/monsters/zombie.dpm_0.sounds (Base). ranged/melee/pain/spawn are
        // commented out there -> empty sample -> Base zombie is SILENT for those cues. Only these three play.
        // All three have count 0 (bare name) and the zombie pack ships .ogg under sound/monsters/zombie/.
        SoundCues = new System.Collections.Generic.HashSet<string> { "death", "sight", "idle" };
        StartHealth = 200f;             // g_monster_zombie_health
        Damage = 55f;                   // representative (melee) damage
        Speed = 600f;                   // run speed (speed2)
    }

    // Monster_Spawn + METHOD(Zombie, mr_setup) — zombie.qc
    public override void Spawn(Entity e)
    {
        var st = MonsterAI.Setup(this, e);
        // QC zombie mr_setup: setsize('-18 -18 -25', '18 18 47'). QC Monster_Spawn_Setup then seeds
        // view_ofs = '0 0 1' * (maxs.z * 0.35) — the eye height the LOS/aim/danger traces read
        // (self.Origin + self.ViewOfs). MonsterAI.Setup does not seed it, so without this the eye sits at
        // the origin (ViewOfs 0). Folded into the descriptor's Spawn per the monster-framework parity note.
        Api.Entities.SetSize(e, new Vector3(-18, -18, -25), new Vector3(18, 18, 47));
        e.ViewOfs = new Vector3(0, 0, 47f * 0.35f); // 0.35 * maxs.z = 16.45

        st.WalkSpeed = MonsterAI.Cvar("g_monster_zombie_speed_walk", SpeedWalk);
        st.RunSpeed = MonsterAI.Cvar("g_monster_zombie_speed_run", SpeedRun);
        st.StopSpeed = MonsterAI.Cvar("g_monster_zombie_speed_stop", SpeedStop);
        st.DamageForceScale = MonsterAI.Cvar("g_monster_zombie_damageforcescale", 0.55f);
        st.MonsterLoot = MonsterAI.CvarString("g_monster_zombie_loot", "health_medium");

        // zombie.qc mr_setup: zombies ALWAYS respawn (NORESPAWN cleared), come back at the death point, and
        // respawn near-instantly. Once they've appeared they shouldn't re-appear, just respawn.
        st.NoRespawn = false;
        st.AlwaysRespawn = true;
        st.RespawnAtDeathPoint = true;
        st.RespawnTime = 0.2f;
        e.SpawnFlags &= ~MonsterAI.MonsterFlag_Appear;

        // Spawn animation gating: no thinking + no push while the spawn anim plays (QC spawn_time / shield).
        // QC: setanim(anim_spawn '30 1 3') then spawn_time = animstate_endtime, where animstate_endtime is set
        // by animfixfps via frameduration(modelindex, 30). The zombie.dpm.framegroups group 30 (spawn) contains
        // 61 frames at 30 fps → duration = AnimDurSpawn = 61/30 ≈ 2.033s (not the '3 fps' fallback of 1/3s).
        st.SpawnTime = MonsterAI.Now + AnimDurSpawn;
        st.DamageForceScale = 0.0001f; // no push while spawning (restored in Think once spawned)
        MonsterFramework.ApplyFor(MonsterFramework.SpawnShield, e, AnimDurSpawn);
        // QC mr_setup: setanim(actor.anim_spawn '30 1 3') — play the spawn pose (frame 30) while the brain idles
        // until SpawnTime. DriveAnimFrame stamps it onto the networked Frame each think.
        st.Anim = MonsterAI.MonsterAnim.Spawn;
    }

    // METHOD(Zombie, mr_think) — zombie.qc (drives the shared chase/attack loop).
    public override void Think(Entity e)
    {
        var st = MonsterAI.StateOf(e);
        if (st is null) return;

        // Restore the real knockback scale once the spawn animation has finished (QC mr_think).
        if (MonsterAI.Now >= st.SpawnTime && st.DamageForceScale < 0.5f)
            st.DamageForceScale = MonsterAI.Cvar("g_monster_zombie_damageforcescale", 0.55f);

        MonsterAI.RunThink(e, st);
    }

    // M_Zombie_Attack (zombie.qc): melee when close, leap when far.
    public override void Attack(Entity e, Entity target)
    {
        var st = MonsterAI.StateOf(e);
        if (st is null) return;

        float dist = (target.Origin - e.Origin).Length();
        if (dist <= st.AttackRange)
        {
            // Sometimes raise a block instead of swinging when hurt and the enemy is healthy (QC: 0.3 chance,
            // self health < 75, enemy health > 10).
            if (MonsterRandom.Next() < 0.3f && e.Health < 75f && target.Health > 10f)
            {
                DefendBlock(e, st);
                return;
            }

            // MONSTER_ATTACK_MELEE: punch/bite — QC rolls one of three melee anims (timing identical; the
            // frame choice is CSQC, so the roll is a no-op server-side beyond consuming a draw for parity).
            // QC reads autocvar_g_monster_zombie_attack_melee_{damage,delay} LIVE at attack time (zombie.qc:86).
            MonsterRandom.Next();
            st.Anim = MonsterAI.MonsterAnim.Attack;
            float meleeDamage = MonsterAI.Cvar("g_monster_zombie_attack_melee_damage", MeleeDamage);
            // QC Monster_Attack_Melee: attack_finished_single[0] = animstate_endtime (when model is loaded the
            // actual group duration always supersedes the animtime fallback). The anim_melee1/2/3 group (index 4,
            // zombie.dpm.framegroups: 41 frames @ 30 fps = AnimDurMelee ≈ 1.367s) drives the cooldown; the cvar
            // g_monster_zombie_attack_melee_delay (1s) is the QC fallback that fires only when frameduration
            // returns 0 (model not loaded). Use AnimDurMelee as the faithful animtime here.
            _ = MonsterAI.Cvar("g_monster_zombie_attack_melee_delay", MeleeDelay); // consume for parity
            // zombie.qc M_Zombie_Attack melee: DEATH_MONSTER_ZOMBIE_MELEE.
            MonsterAI.MeleeAttack(e, st, meleeDamage, st.AttackRange, AnimDurMelee,
                DeathTypes.MonsterZombieMelee);
        }
        else
        {
            // MONSTER_ATTACK_RANGED: leap toward the enemy (Monster_Attack_Leap + M_Zombie_Attack_Leap_Touch).
            // QC reads autocvar_g_monster_zombie_attack_leap_{speed,delay} LIVE at attack time (zombie.qc:90).
            float leapSpeed = MonsterAI.Cvar("g_monster_zombie_attack_leap_speed", LeapSpeed);
            // QC Monster_Attack_Leap: attack_finished_single[0] = animstate_endtime when model is loaded.
            // anim_shoot '0 1 5' maps to group 0 (zombie.dpm.framegroups: attackleap, 56 frames @ 30 fps =
            // AnimDurLeap ≈ 1.867s). The cvar g_monster_zombie_attack_leap_delay (1.5s) is the QC fallback.
            _ = MonsterAI.Cvar("g_monster_zombie_attack_leap_delay", LeapDelay); // consume for parity
            Vector3 forward = QMath.Forward(e.Angles);
            Vector3 vel = forward * leapSpeed + new Vector3(0, 0, 200);
            // QC: Monster_Attack_Leap(actor, actor.anim_shoot '0 1 5', ...) — the leap plays the shoot group.
            MonsterAI.Leap(e, st, vel, LeapTouch, AnimDurLeap, MonsterAI.MonsterAnim.Shoot);
        }
    }

    // M_Zombie_Attack_Leap_Touch (zombie.qc): deal contact damage when the leaping zombie hits a target,
    // then revert the touch so it doesn't spam, and clear the attack state.
    private void LeapTouch(Entity self, Entity other)
    {
        if (self.Health <= 0f) return;
        var st = MonsterAI.StateOf(self);

        if (other.TakeDamage != DamageMode.No)
        {
            // QC: face the moveto, scale to leap_force; reads autocvar_g_monster_zombie_attack_leap_{force,damage}
            // LIVE in the touch handler (zombie.qc:27-29).
            float leapForce = MonsterAI.Cvar("g_monster_zombie_attack_leap_force", LeapForce);
            float leapDamage = MonsterAI.Cvar("g_monster_zombie_attack_leap_damage", LeapDamage);
            Vector3 face = QMath.Normalize(QMath.VecToAngles((st?.MoveTo ?? other.Origin) - self.Origin))
                           * leapForce;
            float dmg = leapDamage * MonsterAI.SkillMod(st!);
            // zombie.qc M_Zombie_Attack_Leap_Touch: the leap contact hit is DEATH_MONSTER_ZOMBIE_JUMP.
            Combat.Damage(other, self, self, dmg, DeathTypes.MonsterZombieJump, other.Origin, face);
            self.Touch = (s, o) => MonsterAI.Touch(s, o); // instantly off to stop damage spam (QC Monster_Touch)
            if (st is not null) st.State = 0;
        }
    }

    // M_Zombie_Defend_Block (zombie.qc): briefly raise armor to 0.9 and freeze, then restore. Blocks the next
    // ~2.1s of attacks behind near-total armor (QC SetResourceExplicit(RES_ARMOR, 0.9) + Monster_Delay end).
    private void DefendBlock(Entity e, MonsterAI.MonsterState st)
    {
        e.ArmorValue = 0.9f;
        st.State = MonsterAI.MonsterState_AttackMelee; // freeze monster
        st.AttackFinished = MonsterAI.Now + 2.1f;
        st.AnimFinished = st.AttackFinished;

        // Restore the normal block armor after the block ends (QC M_Zombie_Defend_Block_End via Monster_Delay).
        // monsters.cfg:127 `set g_monsters_armor_blockpercent 0.5` — Base default fallback is 0.5, not 0.6.
        float restore = MonsterAI.Cvar("g_monsters_armor_blockpercent", 0.5f);
        MonsterAI.QueueDelayedAttack(e, st, 2f, 2.1f, self =>
        {
            if (self.Health <= 0f) return;
            self.ArmorValue = restore;
            // QC M_Zombie_Defend_Block_End: setanim(anim_blockend '7 1 1') once the block delay (2s) elapses.
            var dst = MonsterAI.StateOf(self);
            if (dst is not null) dst.Anim = MonsterAI.MonsterAnim.BlockEnd;
        });

        // QC M_Zombie_Defend_Block: setanim(anim_blockstart '8 1 1'). Set AFTER QueueDelayedAttack, which would
        // otherwise stamp the generic Attack phase — the block stance plays the blockstart pose, not melee.
        st.Anim = MonsterAI.MonsterAnim.Block;

        // Base M_Zombie_Defend_Block plays NO sound (only setanim blockstart). The zombie 'melee' cue is
        // commented out in zombie.dpm_0.sounds, so the prior monsters/zombie_melee.wav play was a divergence.
    }

    // QC mr_death: setanim(random() > 0.5 ? anim_die1 : anim_die2) — the zombie picks its death animation
    // immediately at the moment of death (NOT tied to corpse landing). MonsterAI.MarkDead calls this once and
    // stores the result in MonsterState.DeathLanded; DriveAnimFrame passes it as the die2 flag to AnimFrame.
    // A HIGH roll picks die1 (frame 9), a low roll picks die2 (frame 12), so return TRUE (=die2) only on the
    // low roll to match Base's per-roll mapping (distribution is 50/50 either way; this keeps which frame
    // plays on a given roll byte-faithful to zombie.qc:122 rather than inverted).
    public override bool RollDeathVariant() => !(MonsterRandom.Next() > 0.5f);

    // METHOD(Zombie, describe) — zombie.qc:178-189. The MENUQC bestiary prose (4 PAR() paragraphs) describing
    // the zombie's behaviour: charge+leap, melee punch/bite, block, and destroy-the-corpse-to-stop-it-rising.
    public override string? Describe() =>
        "Zombies are the undead remains of deceased soldiers, risen with a ravenous hunger and no sense of self-preservation. " +
        "When a Zombie senses a nearby player it will begin to charge its target at high speeds. " +
        "While charging, a Zombie may leap towards the player, dealing massive damage on contact. " +
        "If it gets close, the Zombie will punch and bite repeatedly. " +
        "When threatened the Zombie may hold up its hands to block incoming attacks briefly. " +
        "It is no small task to kill that which is already dead. Once a Zombie is defeated, destroy its corpse to prevent it from rising again!";

    // mr_anim (zombie.qc): the MD3 frame-group table. The first component of each animfixfps('N …') is the
    // group's start frame, which QC's setanim stamps onto .frame; the networked Entity.Frame drives the client
    // ModelAnimator (CSQCMODEL_AUTOUPDATE). MonsterAI.DriveAnimFrame calls this each think to play the phase.
    // The logical-phase enum maps the zombie's Base groups: spawn (30), blockstart/end (8/7) and shoot/leap (0)
    // each have their own phase; melee1/2/3 all share frame 4, walk==run.
    // Base alternates pain1/pain2 at random (mr_pain: random()>0.5 ? anim_pain1 : anim_pain2) and die1/die2 at
    // random (mr_death: random()>0.5 ? anim_die1 : anim_die2). The variant index from MonsterState.AnimVariant
    // (set by MarkPain) drives the pain pick; the die1/die2 pick is driven by MonsterState.DeathLanded (set by
    // RollDeathVariant above). Both variants are now reachable.
    public override float? AnimFrame(MonsterAnimPhase phase, bool die2) => AnimFrame(phase, die2, 0);

    public override float? AnimFrame(MonsterAnimPhase phase, bool die2, int variant) => phase switch
    {
        MonsterAnimPhase.Idle => 19f,      // anim_idle '19 1 1'
        MonsterAnimPhase.Walk => 27f,      // anim_walk '27 1 1'
        MonsterAnimPhase.Run => 27f,       // anim_run '27 1 1'
        MonsterAnimPhase.Attack => 4f,     // anim_melee1/2/3 '4 1 5'
        // QC mr_pain: random()>0.5 ? anim_pain1 '20 1 2' : anim_pain2 '22 1 2' — a HIGH roll picks pain1.
        // MarkPain sets variant 1 on the high roll (Next()>=0.5), so variant 1 => pain1 (20), variant 0 =>
        // pain2 (22). (Inverse of the golem's '>=0.5 ? pain2 : pain1' ordering — the zombie lists pain1 first.)
        MonsterAnimPhase.Pain => variant == 1 ? 20f : 22f,
        MonsterAnimPhase.Death => die2 ? 12f : 9f, // anim_die1 '9 1 0.5' / anim_die2 '12 1 0.5'
        MonsterAnimPhase.Spawn => 30f,     // anim_spawn '30 1 3'
        MonsterAnimPhase.Block => 8f,      // anim_blockstart '8 1 1'
        MonsterAnimPhase.BlockEnd => 7f,   // anim_blockend '7 1 1'
        MonsterAnimPhase.Shoot => 0f,      // anim_shoot '0 1 5' (the leap pose)
        _ => 19f,
    };
}
