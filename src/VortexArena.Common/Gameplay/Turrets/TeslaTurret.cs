using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Tesla Coil Turret — port of common/turrets/turret/tesla.{qh,qc} (+ tesla_weapon.qc). A short-range area
/// defense turret (TUR_FLAG_HITSCAN) that electrocutes everything near it: it strikes the nearest valid target
/// with a high-voltage arc, which then CHAINS to nearby targets, each hop dealing 75% of the previous damage
/// over 85% of the previous range, up to 10 hops. No aiming/tracking (TFL_AIM_NO | TFL_TRACK_NO) — it's
/// omnidirectional. Identity/hitbox from tesla.qh; balance from turrets.cfg (<c>g_turrets_unit_tesla_*</c>).
///
/// Mechanic implemented faithfully: the chain-lightning (tesla_weapon.qc <c>toast</c> loop) — nearest LOS
/// target first, then jump to the closest not-yet-hit valid target within the shrinking range, applying the
/// decaying damage, never hitting the same entity twice per discharge. Custom firecheck (TFL_SHOOT_CUSTOM):
/// fires whenever a target is in range and it's cooled down, with scan throttle (mindelay 0.1s / maxdelay 1s)
/// and a 0.5s held-enemy re-validate window. The per-hop lightning arc visuals, the coil-head charge spin
/// (tr_think), and the idle crackle arc are all reproduced; the turret is silent server-side (the electro_fire
/// sound is player-only in Base wr_think) and deals zero knockback (toast force '0 0 0').
/// </summary>
[Turret]
public sealed class TeslaTurret : Turret
{
    // --- balance (turrets.cfg g_turrets_unit_tesla_*) ---
    private const float ShotDamage = 200f;
    // NOTE: Base defines g_turrets_unit_tesla_shot_force 400 but toast() damages with force '0 0 0'
    // (the tesla deals no knockback), so the configured force is intentionally never applied — see Toast().
    private const float ShotRefire = 1.5f;
    private const float TargetRange = 1000f;
    private const float TargetRangeMin = 0f;
    private const float AmmoMax = 1000f;
    private const float AmmoRecharge = 15f;
    private const float RespawnTime = 120f; // g_turrets_unit_tesla_respawntime (turrets.cfg)
    private const int MaxChainHops = 10;
    private const float ChainDamageFalloff = 0.75f; // damage *= 0.75 per hop
    private const float ChainRangeFalloff = 0.85f;  // range  *= 0.85 per hop

    // QC tesla.qc tr_setup: players + missiles, range-limited, team-checked. No range-limit on the chain hops.
    private const int Select = TurretAI.SelectPlayers | TurretAI.SelectMissiles
                             | TurretAI.SelectRangeLimits | TurretAI.SelectTeamCheck;
    // Chain hops use the validate flags WITHOUT range limits (QC drops RANGELIMITS for the chain).
    private const int ChainSelect = TurretAI.SelectPlayers | TurretAI.SelectMissiles | TurretAI.SelectTeamCheck;

    public TeslaTurret()
    {
        NetName = "tesla";
        DisplayName = "Tesla Coil";
        Model = "models/turrets/tesla_base.md3";
        HeadModel = "models/turrets/tesla_head.md3";   // QC tesla.qh head_model ATTRIB (the spinning coil head)
        StartHealth = 1000f;
        Range = TargetRange;
    }

    public override void Spawn(Entity e)
        => TurretSpawn.Init(this, e, new Vector3(-60f, -60f, 0f), new Vector3(60f, 60f, 128f),
            AmmoMax, AmmoRecharge, shotVolly: 0, respawnTime: RespawnTime);

    public override void Think(Entity e)
    {
        TurretState st = TurretAI.State(e);
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        float frameTime = Api.Services is not null ? Api.Clock.FrameTime : 0f;

        if (st.Ammo < st.AmmoMax)
            st.Ammo = System.Math.Min(st.Ammo + st.AmmoRecharge * frameTime, st.AmmoMax);

        st.ShotOrg = TurretAI.ShotOrigin(e);

        // tr_think (tesla.qc): spin the coil head and crackle while charged.
        TrThink(st, now);

        if (!st.Active) return;   // inactive (team-gated) or dead turrets don't discharge

        // Custom firecheck (tesla.qc turret_tesla_firecheck): scan-throttle (mindelay/maxdelay),
        // re-validate, then require cooldown + ammo + a target.

        // QC tesla.qc:51-82 turret_tesla_firecheck: rate-limited rescan via target_select_time
        // (mindelay 0.1 / maxdelay 1) and a separate 0.5-s enemy re-validate window. Cvar-backed
        // (QC autocvar_g_turrets_targetscan_*) with the turrets.cfg defaults, matching the generic
        // TurretAI.RunCombat throttle so a server override moves both paths together.
        float targetScanMinDelay = TurretAI.Cvar("g_turrets_targetscan_mindelay", 0.1f);
        float targetScanMaxDelay = TurretAI.Cvar("g_turrets_targetscan_maxdelay", 1f);

        bool doTargetScan = false;

        // Force a target re-scan every maxdelay seconds.
        if (now > st.TargetSelectTime + targetScanMaxDelay)
            doTargetScan = true;

        // Old target (if any) invalid? Re-validate at most every 0.5s.
        if (st.TargetValidateTime < now
            && !TurretAI.ValidTarget(e, e.Enemy, Select, TargetRangeMin, TargetRange))
        {
            e.Enemy = null;
            st.TargetValidateTime = now + 0.5f; // tesla.qc: re-validate the held target at most every 0.5s
            doTargetScan = true;
        }

        // But never more often than mindelay seconds!
        if (now < st.TargetSelectTime + targetScanMinDelay)
            doTargetScan = false;

        // Scan if we should, update the scan timestamp.
        if (doTargetScan)
        {
            e.Enemy = TurretAI.SelectTarget(e, Select, TargetRangeMin, TargetRange);
            st.TargetSelectTime = now;
        }

        if (e.Enemy is null) return;
        if (st.AttackFinished > now) return;
        if (st.Ammo < ShotDamage) return;

        Discharge(e);
        st.AttackFinished = now + ShotRefire;
        st.Ammo -= ShotDamage;
    }

    /// <summary>
    /// Port of <c>TeslaCoil.tr_think</c> (tesla.qc:11): spin the coil head proportional to charge
    /// ('0 45 0' below a full shot, '0 180 0' when charged, scaled by ammo/shot_dmg; zeroed when inactive),
    /// and — while charged and cooled down — probabilistically emit a short idle crackle arc from the muzzle.
    /// </summary>
    private void TrThink(TurretState st, float now)
    {
        if (!st.Active)
        {
            st.HeadAVelocity = Vector3.Zero;
            return;
        }

        if (st.Ammo < ShotDamage)
        {
            st.HeadAVelocity = new Vector3(0f, 45f, 0f) * (st.Ammo / ShotDamage);
        }
        else
        {
            st.HeadAVelocity = new Vector3(0f, 180f, 0f) * (st.Ammo / ShotDamage);

            if (st.AttackFinished > now) return; // only crackle once cooled down

            float f = st.Ammo / st.AmmoMax;
            if (f * f > Prandom.Float() && Prandom.Float() < 0.1f)
                EffectEmitter.TeCsqcLightningArc(st.ShotOrg, st.ShotOrg + Prandom.Vec() * 350f);
        }

        // Integrate the spin into the head angle (server-side state; the head-bone render is not wired yet).
        float frameTime = Api.Services is not null ? Api.Clock.FrameTime : 0f;
        st.HeadAngles += st.HeadAVelocity * frameTime;
    }

    public override bool ValidTarget(Entity self, Entity target)
        => TurretAI.ValidTarget(self, target, Select, TargetRangeMin, TargetRange);

    // tesla_weapon.qc toast-loop: arc to the nearest target, then chain outward with decaying damage/range.
    private void Discharge(Entity turret)
    {
        var hitAlready = new HashSet<Entity>();
        Vector3 from = TurretAI.State(turret).ShotOrg;

        float damage = ShotDamage;
        float range = TargetRange;

        // First hop uses the full target_range (matching the enemy we already selected).
        Entity? current = Toast(turret, from, range, damage, hitAlready);

        for (int i = 0; i < MaxChainHops && current is not null; i++)
        {
            damage *= ChainDamageFalloff;
            range *= ChainRangeFalloff;
            current = Toast(turret, current.Origin, range, damage, hitAlready);
        }

        // No discharge sound: a Base tesla TURRET is silent server-side — weapons/electro_fire.wav
        // (SND_TeslaCoilTurretAttack_FIRE) is emitted only inside the isPlayer-gated branch of wr_think
        // via W_SetupShot_Dir, which the turret actor never enters (tesla_weapon.qc). The per-hop arc
        // visual (TeCsqcLightningArc in Toast) and the idle crackle (TrThink) are the only FX.
    }

    /// <summary>
    /// Port of <c>toast()</c> (tesla_weapon.qc): find the closest not-yet-hit valid target within
    /// <paramref name="range"/> of <paramref name="from"/> that has clear line of sight, damage it, mark it hit,
    /// and return it (so the next hop chains from there). Returns null if the arc has nowhere to jump.
    /// </summary>
    private Entity? Toast(Entity turret, Vector3 from, float range, float damage, HashSet<Entity> hitAlready)
    {
        if (Api.Services is null) return null;

        Entity? target = null;
        float nearest = range + 1f;

        foreach (Entity e in Api.Entities.FindInRadius(from, range))
        {
            if (hitAlready.Contains(e)) continue;
            if (!TurretAI.ValidTarget(turret, e, ChainSelect, 0f, range)) continue;

            // LOS from the arc's current node to the target center (QC traceline MOVE_WORLDONLY).
            Vector3 center = TurretAI.TargetCenter(e);
            TraceResult tr = Api.Trace.Trace(from, Vector3.Zero, Vector3.Zero, center, MoveFilter.WorldOnly, turret);
            if (tr.Fraction < 1f && !ReferenceEquals(tr.Ent, e)) continue;

            float d = (e.Origin - from).Length();
            if (d < nearest)
            {
                nearest = d;
                target = e;
            }
        }

        if (target is not null)
        {
            // te_csqc_lightningarc(from, target.origin) — the visible high-voltage arc to this hop's target.
            EffectEmitter.TeCsqcLightningArc(from, target.Origin);
            // tesla_weapon.qc toast(): the arc + railgunhit are drawn for the nearest target, but Damage is
            // only applied if etarget != actor.realowner. Zero knockback force ('0 0 0' in QC — the tesla
            // deals no shove). DEATH_TURRET_TESLA each hop.
            if (!ReferenceEquals(target, turret.Owner ?? turret))
                Combat.Damage(target, turret, turret, damage, DeathTypes.TurretTesla,
                    target.Origin, Vector3.Zero);
            hitAlready.Add(target);
        }
        return target;
    }
}
