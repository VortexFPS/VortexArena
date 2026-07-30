using System.Numerics;
using System.Runtime.CompilerServices;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using XonoticGodot.Common.Gameplay.Damage;
using static XonoticGodot.Common.Gameplay.Effects;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Wyvern — port of common/monsters/monster/wyvern.{qh,qc}. A fragile flying reptile that glides after
/// prey and lobs explosive, burning fireballs from a distance. MONSTER_TYPE_FLY | MON_FLAG_RANGED | MON_FLAG_RIDE.
///
/// Identity/size from wyvern.qh; health/damage/speeds from monsters.cfg (g_monster_wyvern_*). Fully ported:
/// the flying movement (FL_FLY + MOVETYPE_FLY, vertical chase), the fireball (radius damage + knockback) for
/// both the melee and ranged branches with its 1s wind-up, and the post-impact burning (Fire_AddDamage) in
/// radius. Only client frame playback (incl. the falling deadthink anim) is CSQC.
/// </summary>
[Monster]
public sealed class Wyvern : Monster
{
    // Balance — g_monster_wyvern_* (monsters.cfg).
    public float FireballDamage = 50f;      // g_monster_wyvern_attack_fireball_damage
    public float FireballEdgeDamage = 20f;  // g_monster_wyvern_attack_fireball_edgedamage
    public float FireballForce = 50f;       // g_monster_wyvern_attack_fireball_force
    public float FireballRadius = 120f;     // g_monster_wyvern_attack_fireball_radius
    public float FireballSpeed = 1200f;     // g_monster_wyvern_attack_fireball_speed
    public float FireballDamageTime = 2f;   // g_monster_wyvern_attack_fireball_damagetime (burn duration)
    public float SpeedWalk = 120f;          // g_monster_wyvern_speed_walk
    public float SpeedRun = 250f;           // g_monster_wyvern_speed_run
    public float SpeedStop = 300f;          // g_monster_wyvern_speed_stop

    // METHOD(Wyvern, mr_pain): actor.pain_finished = time + 0.5 (the wyvern holds its pain reaction longer
    // than the generic 0.34s; matches the anim_pain1 '3 1 2' ~0.5s frame group). Drives MarkPain's PainFinished.
    public override float PainWindow => 0.5f;

    // METHOD(Wyvern, mr_anim) — wyvern.qc. The wyvern's MD3 (models/monsters/wyvern.dpm) frame-group layout:
    //   anim_idle '0 1 1'  anim_walk '1 1 1'  anim_run '2 1 1'  anim_pain1 '3 1 2'  anim_pain2 '4 1 2'
    //   anim_melee '5 1 5'  anim_shoot '6 1 5'  anim_die1 '7 1 0.5'  anim_die2 '8 1 0.5'
    // The first component is the group's start frame; that is what QC's setanim stamps onto .frame and what the
    // networked Entity.Frame drives client-side. mr_death plays die1 (falling), mr_deadthink plays die2 (landed).
    public override float? AnimFrame(MonsterAnimPhase phase, bool die2) => phase switch
    {
        MonsterAnimPhase.Idle => 0f,    // anim_idle '0 1 1'
        MonsterAnimPhase.Walk => 1f,    // anim_walk '1 1 1'
        MonsterAnimPhase.Run => 2f,     // anim_run '2 1 1'
        MonsterAnimPhase.Pain => 3f,    // anim_pain1 '3 1 2'
        MonsterAnimPhase.Attack => 6f,  // anim_shoot '6 1 5' (both melee+ranged lob the fireball)
        MonsterAnimPhase.Death => die2 ? 8f : 7f, // anim_die1 '7 …' falling -> anim_die2 '8 …' landed
        _ => 0f,
    };

    public Wyvern()
    {
        NetName = "wyvern";
        // Base ships NO models/monsters/wyvern.dpm_*.sounds file (find Base *.sounds = only golem + zombie), so
        // QC Monster_Sound resolves every voice cue (pain/death/melee/sight/idle/spawn) to an EMPTY sample and
        // sound7 plays nothing — the Base wyvern is silent for all voice cues (its fireball/burning effects play
        // their own SND_* directly). An empty SoundCues set mirrors that: MonsterSound advances the throttle
        // window but emits no sample, so no port-invented monsters/wyvern_<cue> sound fires (matching Spider/Mage).
        SoundCues = new System.Collections.Generic.HashSet<string>();
        DisplayName = "Wyvern";
        Model = "models/monsters/wyvern.dpm";
        StartHealth = 150f;                 // g_monster_wyvern_health
        Damage = 50f;                       // fireball damage
        Speed = 250f;                       // run speed
    }

    // Monster_Spawn + METHOD(Wyvern, mr_setup) — wyvern.qc. MONSTER_TYPE_FLY: fly movetype + FL_FLY.
    public override void Spawn(Entity e)
    {
        var st = MonsterAI.Setup(this, e);
        // QC wyvern mr_setup: setsize('-30 -30 -48', '30 30 30').
        Api.Entities.SetSize(e, new Vector3(-30, -30, -48), new Vector3(30, 30, 30));
        // QC mr_setup also seeds the eye height: this.view_ofs = '0 0 1' * (this.maxs.z * 0.35). No port monster
        // routes view_ofs through Setup, so (matching the registry's "folded into each descriptor's Spawn" note)
        // seed it inline here: 0.35 * 30 = 10.5, used by the eye-origin traces (ValidTarget/aim) below.
        e.ViewOfs = new Vector3(0, 0, 30f * 0.35f);

        // Clear any stale death-scatter flags so a respawned wyvern tumbles again when re-killed (the same
        // edict is reused across a death->respawn cycle; the weak-table entry would otherwise persist).
        _deathStates.Remove(e);

        // MONSTER_TYPE_FLY (wyvern.qh spawnflags): override the base STEP movetype with flight, and allow
        // vertical chase/wander (MONSTERFLAG_FLY_VERTICAL) so it can climb/dive toward prey.
        e.Flags |= EntFlags.Fly;
        e.MoveType = MoveType.Fly;
        e.Gravity = 0f;
        e.SpawnFlags |= MonsterAI.MonsterFlag_FlyVertical;

        st.WalkSpeed = MonsterAI.Cvar("g_monster_wyvern_speed_walk", SpeedWalk);
        st.RunSpeed = MonsterAI.Cvar("g_monster_wyvern_speed_run", SpeedRun);
        st.StopSpeed = MonsterAI.Cvar("g_monster_wyvern_speed_stop", SpeedStop);
        st.DamageForceScale = MonsterAI.Cvar("g_monster_wyvern_damageforcescale", 0.6f);
        st.MonsterLoot = MonsterAI.CvarString("g_monster_wyvern_loot", "cells");
    }

    // Per-entity flags for the death-launch scatter (mr_death) and the on-ground die2 swap (mr_deadthink).
    // The descriptor is a shared singleton, so this lives in a weak table keyed by the corpse entity; it
    // is reclaimed automatically when the corpse is removed/GC'd, and is reset on respawn (Spawn re-seeds).
    private sealed class DeathState { public bool Scattered; public bool Landed; }
    private static readonly ConditionalWeakTable<Entity, DeathState> _deathStates = new();

    // METHOD(Wyvern, mr_think) — wyvern.qc. The wyvern overrides mr_death + mr_deadthink; those QC methods
    // run on the shared brain tick (Monster_Think -> Monster_Dead_Think), so the port drives their effects
    // from here, on the dead branch, before delegating to the generic flying brain.
    public override void Think(Entity e)
    {
        var st = MonsterAI.StateOf(e);
        if (st is null) return;

        // mr_death / mr_deadthink: once dead, fling the corpse out of the air on the first dead frame, then
        // settle it once it lands. MarkDead leaves a flyer's velocity untouched (it only zeroes ground
        // monsters), so the scatter we add here survives. RunThink's dead branch handles corpse fade/respawn.
        if (e.DeadState != DeadFlag.No || e.Health <= 0f)
        {
            var ds = _deathStates.GetOrCreateValue(e);

            // METHOD(Wyvern, mr_death): random tumble so a killed wyvern falls out of the sky.
            //   velocity.x = 400*random() - 200; velocity.y = 400*random() - 200; velocity.z = 100*random() + 100;
            // setanim(anim_die1) is the falling animation (logical MonsterAnim.Death — already set by MarkDead).
            if (!ds.Scattered)
            {
                ds.Scattered = true;
                e.Velocity = new Vector3(
                    400f * Prandom.Float() - 200f,
                    400f * Prandom.Float() - 200f,
                    100f * Prandom.Float() + 100f);
                // Toss the corpse so it arcs and falls (MarkDead set MoveType.Toss); ensure gravity is back on
                // (the live wyvern flew with Gravity 0) so the scatter actually drops rather than coasting.
                e.Gravity = 1f;
            }

            // METHOD(Wyvern, mr_deadthink): if (IS_ONGROUND(actor)) setanim(anim_die2). die2 is the landed
            // corpse pose. Flag the landing so DriveAnimFrame swaps the networked Entity.Frame from the falling
            // die1 group to the landed die2 group (AnimFrame below); the corpse also stops drifting once grounded.
            else if (!ds.Landed && (e.Flags & EntFlags.OnGround) != 0)
            {
                ds.Landed = true;
                st.DeathLanded = true; // mr_deadthink: setanim(anim_die2) — DriveAnimFrame plays the die2 group
            }
        }

        MonsterAI.RunThink(e, st);
    }

    // M_Wyvern_Attack (wyvern.qc): both melee and ranged answer with a fireball, after a 1s wind-up delay.
    public override void Attack(Entity e, Entity target)
    {
        var st = MonsterAI.StateOf(e);
        if (st is null) return;
        if (MonsterAI.Now < st.AttackFinished) return;

        // QC: Monster_Delay(actor, 0, 1, M_Wyvern_Attack_Fireball) — wind up for 1s, then fire; the attack
        // itself is gated for anim_finished (1.2) + 0.2.
        st.Anim = MonsterAI.MonsterAnim.Attack;
        MonsterAI.QueueDelayedAttack(e, st, windUp: 1f, totalLock: 1.4f, action: self =>
        {
            if (self.Enemy is not null)
                FireFireball(self, MonsterAI.StateOf(self)!, self.Enemy);
        });
    }

    // M_Wyvern_Attack_Fireball (wyvern.qc): a flying missile that explodes (radius damage) on contact, then
    // ignites everything in its blast radius (Fire_AddDamage) for a burning damage-over-time.
    private void FireFireball(Entity e, MonsterAI.MonsterState st, Entity target)
    {
        MonsterAI.FaceTarget(e, target);
        Vector3 dir = QMath.Normalize((target.Origin + new Vector3(0, 0, 10)) - e.Origin);

        float burnTime = MonsterAI.Cvar("g_monster_wyvern_attack_fireball_damagetime", FireballDamageTime);
        float skill = MonsterAI.SkillMod(st);
        Entity owner = e;
        // wyvern.qc M_Wyvern_Attack_Fireball: the blast + the post-impact burn are DEATH_MONSTER_WYVERN.
        string deathType = DeathTypes.MonsterWyvern;

        Entity missile = MonsterAI.SpawnProjectile(e, st, dir, FireballSpeed,
            damage: FireballDamage, edgeDamage: FireballEdgeDamage, radius: FireballRadius,
            force: FireballForce, deathType: deathType,
            moveType: MoveType.FlyMissile, lifetime: 5f,
            sizeMin: new Vector3(-6, -6, -6), sizeMax: new Vector3(6, 6, 6),
            onExplode: p =>
            {
                // M_Wyvern_Attack_Fireball_Explode: Send_Effect(EFFECT_FIREBALL_EXPLODE, this.origin, '0 0 0', 1)
                // — the blast particle. EFFECT_FIREBALL_EXPLODE is registered as "FIREBALL_EXPLODE" (EffectsList).
                EffectEmitter.Emit("FIREBALL_EXPLODE", p.Origin);

                // QC: Fire_AddDamage(it, own, 5*skillmod, fireball_damagetime, deathtype) in radius — ignite.
                foreach (Entity it in Api.Entities.FindInRadius(p.Origin, FireballRadius))
                {
                    if (it == owner) continue;
                    if (it.TakeDamage != DamageMode.Aim) continue; // QC: only DAMAGE_AIM targets burn
                    MonsterFramework.AddFireDamage(it, owner, 5f * skill, burnTime, deathType);
                }
            });
        missile.AVelocity = new Vector3(300, 300, 300);

        Api.Sound.Play(e, SoundChannel.Weapon, "weapons/electro_fire.wav");
    }
}
