// Port of common/mutators/mutator/overkill/oknex.{qh,qc}

using System.Numerics;
using System.Runtime.CompilerServices;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Overkill Nex — port of common/mutators/mutator/overkill/oknex.{qh,qc}. The Overkill loadout's
/// long-range rail: primary fires an instant piercing beam (W_OverkillNex_Attack → FireRailgunBullet,
/// damage 100, force 500); the secondary is the shared Overkill blaster jump (on <c>actor.jump_interval</c>).
/// Only granted by the Overkill mutator (WEP_FLAG_HIDDEN | WEP_FLAG_MUTATORBLOCKED); resolved by NetName
/// "oknex".
///
/// Identity/attributes from oknex.qh; balance from bal-wep-xonotic.cfg (g_balance_oknex_*). Ported: the rail
/// beam (no headshot announce, like the Vortex), the CSQC beam particle, the Yoda/Impressive rail announces,
/// the velocity-charge hook, the scope reticle/zoom, the forced reload, and the secondary blaster-jump.
///
/// CHARGE IS OFF BY DEFAULT (g_balance_oknex_charge 0) — so this is "Vortex without charge": <c>charge = 1</c>
/// and no charge math runs. The velocity-charge model (the oknex_charge GetPressedKeys hook,
/// charge_velocity_rate) is now ported inline in <see cref="WrThink"/> but is charge-gated, so it stays inert at
/// the shipped balance. The chargepool / wr_glow remain unported (charge-gated, parity risk §11: those paths are
/// only reachable once g_balance_oknex_charge is set).
/// </summary>
[Weapon]
public sealed class OkNex : Weapon
{
    /// <summary>Primary-fire balance block — QC WEP_CVAR_PRI(WEP_OVERKILL_NEX, *) + the (default-off) charge cvars.</summary>
    public struct Balance
    {
        public float Ammo;              // g_balance_oknex_primary_ammo
        public float Animtime;          // g_balance_oknex_primary_animtime
        public float Damage;            // g_balance_oknex_primary_damage
        public float Force;             // g_balance_oknex_primary_force
        public float Refire;            // g_balance_oknex_primary_refire
        public bool  Charge;            // g_balance_oknex_charge (default 0 → no charge)
        public float ChargeMinDmg;      // g_balance_oknex_charge_mindmg
        public float ChargeVelocityRate; // g_balance_oknex_charge_velocity_rate (default 0 -> velocity-charge off)
        public float ChargeMinSpeed;     // g_balance_oknex_charge_minspeed (default 400)
        public float ChargeMaxSpeed;     // g_balance_oknex_charge_maxspeed (default 800)
        public float ChargeShotMul;     // g_balance_oknex_charge_shot_multiplier
        public float ChargeStart;       // g_balance_oknex_charge_start
        public float ChargeLimit;       // g_balance_oknex_charge_limit
        public float ChargeRate;        // g_balance_oknex_charge_rate
        public int   Secondary;         // g_balance_oknex_secondary (2 = blaster jump; 0 = ATCK2 zoom, non-default)
        public int   SecondaryRefireType; // g_balance_oknex_secondary_refire_type (1 = own jump_interval timer)
        public float ReloadAmmo;        // g_balance_oknex_reload_ammo
        public float ReloadTime;        // g_balance_oknex_reload_time
    }

    public Balance Cvars;

    public OkNex()
    {
        NetName = "oknex";
        AmmoType = ResourceType.Cells;   // QC ammo_type RES_CELLS
        DisplayName = "Overkill Nex";
        Impulse = 7;
        // WEP_FLAG_HIDDEN | WEP_FLAG_RELOADABLE | WEP_TYPE_HITSCAN | WEP_FLAG_MUTATORBLOCKED
        SpawnFlags = WeaponFlags.Hidden | WeaponFlags.Reloadable | WeaponFlags.TypeHitscan
                   | WeaponFlags.MutatorBlocked;
        Color = new Vector3(0.459f, 0.765f, 0.835f);
        ViewModel = "h_ok_sniper.iqm";   // MDL_OK_NEX_VIEW
        WorldModel = "v_ok_sniper.md3";  // MDL_OK_NEX_WORLD
        ItemModel = "g_ok_sniper.md3";   // MDL_OK_NEX_ITEM
    }

    // QC oknex.qh w_reticle "gfx/reticle_nex" + oknex.qc wr_zoom/wr_zoomdir: the OK Nex is a sniper and carries
    // the NEX scope reticle, drawn full-screen while zoomed (the generic +zoom button path,
    // ReticleOverlay.UpdateReticle's buttonZoom branch). At stock balance g_balance_oknex_secondary == 2 (blaster
    // jump), so the QC ATCK2-zoom gate `!WEP_CVAR(secondary)` is FALSE — ATCK2 does NOT zoom; the scope shows only
    // via the dedicated zoom button. With a server setting secondary 0, ATCK2 becomes the zoom (and forgoes the
    // blaster secondary), which ZoomOnSecondary models faithfully.
    public override string? Reticle => "gfx/reticle_nex";
    public override bool ZoomOnSecondary => Cvars.Secondary == 0;

    public override void Configure()
    {
        Cvars.Ammo = Bal("g_balance_oknex_primary_ammo", 10f);
        Cvars.Animtime = Bal("g_balance_oknex_primary_animtime", 0.65f);
        Cvars.Damage = Bal("g_balance_oknex_primary_damage", 100f);
        Cvars.Force = Bal("g_balance_oknex_primary_force", 500f);
        Cvars.Refire = Bal("g_balance_oknex_primary_refire", 1f);
        Cvars.Charge = BalBool("g_balance_oknex_charge", false);
        Cvars.ChargeMinDmg = Bal("g_balance_oknex_charge_mindmg", 40f);
        Cvars.ChargeVelocityRate = Bal("g_balance_oknex_charge_velocity_rate", 0f);
        Cvars.ChargeMinSpeed = Bal("g_balance_oknex_charge_minspeed", 400f);
        Cvars.ChargeMaxSpeed = Bal("g_balance_oknex_charge_maxspeed", 800f);
        Cvars.ChargeShotMul = Bal("g_balance_oknex_charge_shot_multiplier", 0f);
        Cvars.ChargeStart = Bal("g_balance_oknex_charge_start", 0.5f);
        Cvars.ChargeLimit = Bal("g_balance_oknex_charge_limit", 1f);
        Cvars.ChargeRate = Bal("g_balance_oknex_charge_rate", 0.6f);
        Cvars.Secondary = BalInt("g_balance_oknex_secondary", 2);
        Cvars.SecondaryRefireType = BalInt("g_balance_oknex_secondary_refire_type", 1);
        Cvars.ReloadAmmo = Bal("g_balance_oknex_reload_ammo", 50f);
        Cvars.ReloadTime = Bal("g_balance_oknex_reload_time", 2f);
    }

    // METHOD(OverkillNex, wr_think)
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);

        // Charge regenerates toward the limit (only when charge is enabled — default OFF, so this is inert).
        if (Cvars.Charge && st.VortexCharge < Cvars.ChargeLimit)
            st.VortexCharge = MathF.Min(1f, st.VortexCharge + Cvars.ChargeRate * Api.Clock.FrameTime);

        // Velocity-charge (oknex_charge GetPressedKeys mutator hook, oknex.qc; same algorithm as the vanilla
        // vortex_charge hook ported inline in Vortex.WrThink): while HOLDING the OK-Nex and moving faster than
        // charge_minspeed, charge rises by charge_velocity_rate scaled by how close the xy speed is to
        // charge_maxspeed. Base registers this as the always-on oknex_charge mutator with `m_weapon ==
        // WEP_OVERKILL_NEX`; the fire driver only calls WrThink for the held weapon, so the per-tick condition is
        // identical and the hook is folded inline here (the same stand-in Vortex.cs uses). Stock
        // charge_velocity_rate is 0 (off), so this is inert at the shipped balance — matching Base.
        if (Cvars.Charge && Cvars.ChargeVelocityRate > 0f && Cvars.ChargeMaxSpeed > Cvars.ChargeMinSpeed)
        {
            float xyspeed = new Vector2(actor.Velocity.X, actor.Velocity.Y).Length();
            if (xyspeed > Cvars.ChargeMinSpeed)
            {
                xyspeed = MathF.Min(xyspeed, Cvars.ChargeMaxSpeed);
                float f = (xyspeed - Cvars.ChargeMinSpeed) / (Cvars.ChargeMaxSpeed - Cvars.ChargeMinSpeed);
                st.VortexCharge = MathF.Min(1f, st.VortexCharge + Cvars.ChargeVelocityRate * f * Api.Clock.FrameTime);
            }
        }

        // Secondary blaster-jump: refire_type 1 = own jump_interval; refire_type 0 = shared ATTACK_FINISHED.
        // QC oknex.qc:174 passes `true` (secondary ammo check) on the refire_type==0 path. NOTE: QC oknex
        // additionally gates that branch on WEP_CVAR(WEP_OVERKILL_NEX, secondary) == 2 (oknex.qc:171); both
        // refire_type==0 and secondary==2 are non-default, so this extra gate is a known residual nuance.
        OkWeapons.FireSecondaryBlasterJump(this, actor, slot, fire, Cvars.SecondaryRefireType, FireMode.Secondary);

        // forced reload
        if (Cvars.ReloadAmmo != 0f && st.ClipLoad < Cvars.Ammo)
        {
            WrReload(actor, slot);
            return;
        }

        if (fire == FireMode.Primary)
        {
            // weapon_prepareattack(thiswep, actor, weaponentity, false, refire)
            if (PrepareAttack(actor, slot, fire))
                Attack(actor, slot, st);
        }
    }

    public override float RefireFor(FireMode fire) => Cvars.Refire;
    public override float AnimtimeFor(FireMode fire) => Cvars.Animtime;

    // METHOD(OverkillNex, wr_setup / wr_resetplayer) — seed the per-slot charge (no-op when charge off) and reset
    // the impressive streak (QC wr_setup/wr_resetplayer clears the per-actor lasthit, mirroring the Vortex).
    public override void WrSetup(Entity actor, WeaponSlot slot)
    {
        SetLastHit(actor, 0);
        if (Cvars.Charge) actor.WeaponState(slot).VortexCharge = Cvars.ChargeStart;
    }

    // W_OverkillNex_Attack(thiswep, actor, weaponentity, false)
    private void Attack(Entity actor, WeaponSlot slot, WeaponSlotState st)
    {
        float mydmg = Cvars.Damage;
        float myforce = Cvars.Force;

        // charge = charge_mindmg/dmg + (1 - charge_mindmg/dmg) * oknex_charge; then consume via shot_multiplier.
        // With charge OFF (default) charge == 1 (Vortex-without-charge).
        float charge = 1f;
        if (Cvars.Charge && mydmg > 0f)
        {
            float baseFrac = Cvars.ChargeMinDmg / mydmg;
            charge = baseFrac + (1f - baseFrac) * st.VortexCharge;
            st.VortexCharge *= Cvars.ChargeShotMul; // AFTER computing damage/force
        }
        mydmg *= charge;
        myforce *= charge;

        // QC oknex.qc: capture IsFlying(actor) BEFORE the trace — the rail trace overwrites the trace state the
        // yoda mid-air-kill check reads, so sample the shooter's airborne status here (matches vortex.qc:122).
        bool flying = IsFlying(actor);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward);

        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/nexfire.wav");

        // FireRailgunBullet(actor, weaponentity, w_shotorg, w_shotorg + w_shotdir * max_shot_distance, mydmg, true, myforce, ...)
        // headshotNotify mirrors the Vortex (false): the OK Nex does not announce headshots.
        Vector3 end = shot.Origin + shot.Dir * WeaponFiring.CurrentMaxShotDistance;
        Entity? hit = WeaponFiring.FireRailgunBullet(actor, shot.Origin, end, mydmg, RegistryId, myforce,
            headshotNotify: false);

        // Yoda (mid-air rail kill) + Impressive (every-other cross-team rail hit) announcements — QC oknex.qc
        // mirrors vortex.qc:141-162. The port's FireRailgunBullet doesn't surface the yoda/impressive_hits
        // globals, so re-derive them from the first pierced victim (the rail's primary target): a live,
        // damageable, cross-team player is an impressive hit; if that victim is also airborne AND the shooter is
        // airborne, it's a yoda.
        Announce(actor, hit, flying);

        // "beam and muzzle flash done on client" (oknex.qc:105-106): mirror the Vortex port — the rail muzzle
        // flash (EFFECT_VORTEX_MUZZLEFLASH) + the beam-impact puff (wr_impacteffect EFFECT_VORTEX_IMPACT) and the
        // SND_OK_NEX_IMPACT (neximpact) ping at the surface the beam hit.
        Vector3 beamEnd = shot.Origin + shot.Dir * WeaponFiring.CurrentMaxShotDistance;
        // [T45] warpzone-aware: the beam splits at a portal and the impact FX land on the far side.
        WarpzoneTraceResult impW = WeaponFiring.HitscanImpactTrace(actor, shot.Origin, beamEnd);
        TraceResult impTr = impW.Trace;
        bool silent = (impTr.DpHitQ3SurfaceFlags & WeaponFiring.Q3SurfaceFlagSky) != 0 || impTr.Fraction >= 1f;
        if (!silent)
        {
            EffectEmitter.Emit("VORTEX_IMPACT", impTr.EndPos);
            WeaponSplash.ImpactSoundAt(impTr.EndPos, "weapons/neximpact.wav"); // QC SND_OK_NEX_IMPACT
        }
        EffectEmitter.Emit("VORTEX_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);

        // QC oknex.qc SendCSQCVortexBeamParticle(actor, charge): the rail BEAM between muzzle and impact (the same
        // EFFECT_VORTEX_BEAM the Vortex draws, Vortex.EmitBeam). Emit it server-side along the actual hit segment
        // (count 0 = trail). cl_particles_oldvortexbeam selects the legacy beam. Charge is 1 at stock (charge off),
        // so the sqrt(charge) alpha modulation and the team-tint path are inert here (deferred with the charge
        // subsystem, oknex.charge); the un-charged beam keeps its native nex_beam colour, matching the Vortex's
        // default-path emit.
        bool oldBeam = Api.Services is not null && Api.Cvars.GetFloat("cl_particles_oldvortexbeam") != 0f;
        string okBeam = oldBeam ? "VORTEX_BEAM_OLD" : "VORTEX_BEAM";
        if (impW.ZonesCrossed > 0)
        {
            EffectEmitter.Emit(okBeam, shot.Origin, impW.FirstCrossPoint, 0);   // muzzle -> the portal window
            EffectEmitter.Emit(okBeam, impW.FirstExitPoint, impTr.EndPos, 0);   // exit window -> the far impact
        }
        else
        {
            EffectEmitter.Emit(okBeam, shot.Origin, impTr.EndPos, 0);
        }

        // W_DecreaseAmmo(thiswep, actor, ammo) — clip-aware (WEP_FLAG_RELOADABLE): drains the magazine so the
        // wr_think forced-reload branch (clip_load < ammo) engages. oknex.qc:108 (ammo) + :157-162 (reload).
        DecreaseAmmo(actor, slot, Cvars.Ammo);
    }

    // Per-actor oknex "impressive" last-hit toggle (QC .float; the port has no OK-Nex field on Entity, so track
    // it in a weak side-table keyed by actor — same observable "every second consecutive hit" cadence, mirrors
    // Vortex's _vortexLastHit). Shared with the Vortex's rail-lasthit only in spirit; kept per-weapon-class.
    private static readonly ConditionalWeakTable<Entity, StrongBox<int>> _lastHit = new();
    private static int GetLastHit(Entity actor) => _lastHit.TryGetValue(actor, out var b) ? b.Value : 0;
    private static void SetLastHit(Entity actor, int v) => _lastHit.GetOrCreateValue(actor).Value = v;

    // Yoda / Impressive — port of oknex.qc (mirrors vortex.qc:141-162). `flying` is the shooter's airborne status
    // captured before the trace; `hit` is FireRailgunBullet's first pierced victim (the rail's primary target).
    private void Announce(Entity actor, Entity? hit, bool flying)
    {
        if ((actor.Flags & EntFlags.Client) == 0) return; // only real clients get announces

        // QC ++impressive_hits (damage.qc:646): a cross-team damaging hit on a hittable victim. The rail always
        // deals damage > 0, so any live, damageable, cross-team target counts.
        bool impressiveHit = hit is not null
            && hit.TakeDamage != DamageMode.No
            && hit.DeadState == DeadFlag.No
            && !ReferenceEquals(hit, actor)
            && !Teams.SameTeam(hit, actor);

        // QC yoda (damage.qc:648-651): the victim is a PLAYER and IsFlying(victim); the rail block also gates on
        // the SHOOTER flying (vortex.qc:154 `if (yoda && flying)`).
        if (impressiveHit && flying
            && (hit!.Flags & EntFlags.Client) != 0 && IsFlying(hit))
            NotificationSystem.Announce(actor, "ACHIEVEMENT_YODA");

        // QC vortex.qc:156-162: impressive fires only when THIS shot AND the previous one both landed a hit,
        // then resets so it's every-other.
        int impressive = impressiveHit ? 1 : 0;
        if (impressive != 0 && GetLastHit(actor) != 0)
        {
            NotificationSystem.Announce(actor, "ACHIEVEMENT_IMPRESSIVE");
            impressive = 0; // only every second time
        }
        SetLastHit(actor, impressive);
    }

    // bool IsFlying(entity) — common/physics/player.qc airshot test: airborne, not swimming, >= 24u clearance
    // below (a player skimming the ground doesn't count). Mirrors Vortex.IsFlying.
    private static bool IsFlying(Entity e)
    {
        if (e.OnGround) return false;
        if (e.WaterLevel >= 2) return false; // WATERLEVEL_SWIMMING
        TraceResult tr = Api.Trace.Trace(e.Origin, e.Mins, e.Maxs,
            e.Origin - new Vector3(0f, 0f, 24f), MoveFilter.Normal, e);
        return tr.Fraction >= 1f;
    }

    // METHOD(OverkillNex, wr_checkammo1)
    public bool CheckAmmoPrimary(Entity actor) => actor.GetResource(AmmoType) >= Cvars.Ammo;

    // METHOD(OverkillNex, wr_checkammo2) — Blaster secondary is unlimited (secondary == 2 = blaster).
    public bool CheckAmmoSecondary(Entity actor) => true;

    public override float ReloadingAmmo() => Cvars.ReloadAmmo;
    public override float ReloadingTime() => Cvars.ReloadTime;
}
