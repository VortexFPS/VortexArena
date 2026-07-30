using System.Numerics;
using System.Runtime.CompilerServices;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Vaporizer (Nexuiz "MinstaNex") — port of common/weapons/weapon/vaporizer.{qh,qc}. A hitscan rail
/// superweapon firing an instant beam of energy for a huge fixed chunk of damage (in instagib it one-shots
/// players); secondary fires a Blaster-style knockback laser. The signature instagib behaviour — a 0-damage
/// cvar means "10000 damage" (instant kill) and each shot costs 1 cell — is modeled here.
///
/// Identity/attributes from vaporizer.qh; balance from bal-wep-xonotic.cfg (g_balance_vaporizer_*).
/// This port covers the primary railgun beam (piercing + knockback force), the Blaster-laser secondary
/// (driven through the Blaster weapon), and the full Rocket-Minsta mode (g_rm: explosion at the beam
/// endpoint + the secondary laser barrage). Only the achievements and CSQC beam particle are render-only.
/// </summary>
[Weapon]
public sealed class Vaporizer : Weapon
{
    /// <summary>Primary-fire (rail beam) balance — QC WEP_CVAR_PRI(WEP_VAPORIZER, *).</summary>
    public struct PrimaryBalance
    {
        public float Ammo;     // g_balance_vaporizer_primary_ammo (cells per shot, non-instagib)
        public float Animtime; // g_balance_vaporizer_primary_animtime
        public float Damage;   // g_balance_vaporizer_primary_damage (<=0 means instant-kill 10000)
        public float Force;    // g_balance_vaporizer_primary_force
        public float Refire;   // g_balance_vaporizer_primary_refire
    }

    public PrimaryBalance Primary;

    // QC vaporizer.qh w_reticle "gfx/reticle_nex" + vaporizer.qc wr_init/wr_zoom: the Vaporizer has no
    // weapon-specific zoom (ZoomOnSecondary stays false — wr_zoom returns true only on button_zoom ||
    // zoomscript_caught, NOT ATTACK2), but it DOES carry the NEX scope reticle, precached client-side
    // (wr_init: precache_pic w_reticle) and drawn while the dedicated +zoom button is held with the
    // Vaporizer out (ReticleOverlay.UpdateReticle picks the weapon scope on buttonZoom). Same image as the
    // sister Vortex. The crosshair (gfx/crosshairminstanex size 0.6) is wired centrally in CrosshairPanel.
    public override string? Reticle => "gfx/reticle_nex";

    // METHOD(Vaporizer, describe) — common/weapons/weapon/vaporizer.qc (MENUQC): the in-menu weapon-guide
    // prose shown by DialogMediaGuide. Four PAR() paragraphs (energy-beam superweapon overview + the instagib
    // one-shot/ExtraLife caveat, the Blaster-laser secondary, the superweapon-rarity / instagib-spawn note, and
    // the Cells ammo note). The QC builds these with COLORED_NAME(%s) substitutions — Vaporizer, the InstaGib
    // mutator, the Extra Life item, the Blaster, and the Cells item — which are pre-filled here with their plain
    // literals (same convention as Electro/Shotgun/Tuba). The trailing W_Guide_Keybinds(this) and
    // W_Guide_DPS_onlyOne lines are owned by the guide page and not reproduced here (those helpers aren't ported).
    public override string? GuideDescription =>
        "The Vaporizer is a unique weapon, firing a deadly beam of energy dealing a huge amount of damage. "
      + "In InstaGib, the beam has the ability to instantly kill enemies with a single shot, unless they have "
      + "an Extra Life.\n\n"
      + "The secondary fire shoots a laser identical to that fired by the Blaster, with strong knockback.\n\n"
      + "It is a superweapon, so isn't often found in game, except in InstaGib where all players spawn with it.\n\n"
      + "It consumes some Cells ammo with each shot.";

    public Vaporizer()
    {
        NetName = "vaporizer";
        AmmoType = ResourceType.Cells;   // QC ammo_type
        DisplayName = "Vaporizer";
        Impulse = 7;
        // WEP_FLAG_RELOADABLE | WEP_FLAG_CANCLIMB | WEP_FLAG_SUPERWEAPON | WEP_TYPE_HITSCAN | WEP_FLAG_NODUAL
        SpawnFlags = WeaponFlags.Reloadable | WeaponFlags.CanClimb | WeaponFlags.SuperWeapon
                   | WeaponFlags.TypeHitscan | WeaponFlags.NoDual;
        Color = new Vector3(0.592f, 0.557f, 0.824f);
        ViewModel = "h_minstanex.iqm";  // MDL_VAPORIZER_VIEW
        WorldModel = "v_minstanex.md3"; // MDL_VAPORIZER_WORLD
        ItemModel = "g_minstanex.md3";  // MDL_VAPORIZER_ITEM
    }

    public override void Configure()
    {
        Primary.Ammo = Bal("g_balance_vaporizer_primary_ammo", 10f);
        Primary.Animtime = Bal("g_balance_vaporizer_primary_animtime", 0.3f);
        Primary.Damage = Bal("g_balance_vaporizer_primary_damage", 150f);
        Primary.Force = Bal("g_balance_vaporizer_primary_force", 800f);
        Primary.Refire = Bal("g_balance_vaporizer_primary_refire", 1f);
    }

    // METHOD(Vaporizer, wr_think) — common/weapons/weapon/vaporizer.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);
        bool rm = Api.Services is not null && Api.Cvars.GetFloat("g_rm") != 0f;

        // Forced reload (vaporizer.qc:288-303): when reloading is enabled (reload_ammo != 0) and the clip has
        // dropped below the cheapest per-shot cost, reload before firing. vaporizer_ammo = (g_instagib ? 1 :
        // primary_ammo); if the Blaster-laser secondary also draws from the clip (blaster_ammo set) the floor is
        // the smaller of the two. reload_ammo defaults 0, so this is latent today (matches Base on the bare
        // config) but ports the branch faithfully. hagar_load (the RM rapid latch) is cleared on a reload.
        if (ReloadingAmmo() != 0f)
        {
            float vaporizerAmmo = InstagibActive ? 1f : Primary.Ammo;
            float blasterAmmo = Cvar("g_balance_blaster_primary_ammo", 0f);
            float floor = blasterAmmo > 0f ? MathF.Min(vaporizerAmmo, blasterAmmo) : vaporizerAmmo;
            if (st.ClipLoad < floor)
            {
                WrReload(actor, slot);
                st.HagarLoad = 0; // rocket minsta exclusive var (vaporizer.qc:294/301)
                return;
            }
        }

        // QC vaporizer.qc:353-354: releasing the laser button resets hagar_load so the next press restarts from
        // the fan + rapid_delay. In QC the rail-primary `if` and the laser `if` are independent (no early return),
        // and the trailing `else` clears hagar_load whenever neither fire condition held. The port's Primary call
        // runs every tick (driver upkeep) and may return early after the rail; so detect "laser button not held"
        // here — ATK2 up AND no out-of-cells RM primary fallback — and clear the latch before that return.
        bool primaryFallbackHeld = rm && actor.GetResource(ResourceType.Cells) <= 0f && st.ButtonAttack;
        if (fire == FireMode.Primary && !st.ButtonAttack2 && !primaryFallbackHeld)
            st.HagarLoad = 0;

        // Primary: rail beam — but in Rocket-Minsta when out of cells, primary falls back to the laser.
        // Gated by the primary refire (QC weapon_prepareattack); the out-of-cells RM fallthrough below uses
        // its own jump_interval gate so it can fire even when PrepareAttack would block the rail.
        if (fire == FireMode.Primary && (actor.GetResource(ResourceType.Cells) > 0f || !rm))
        {
            if (PrepareAttack(actor, slot, fire))
                Attack(actor, slot);
            return;
        }

        // Secondary (and the out-of-cells RM primary) fires the Blaster knockback laser, or the RM laser
        // barrage when g_rm + g_rm_laser are enabled. A manual jump_interval gate handles its refire so it
        // can fire independently of the rail beam (important for instagib). Each path requires its own button
        // be held — the driver calls WrThink(Primary) every tick for upkeep, so the Primary fallback must
        // check st.ButtonAttack itself (the Secondary call only happens while ATK2 is held).
        bool primaryFallback = fire == FireMode.Primary && primaryFallbackHeld;
        bool laserFire = fire == FireMode.Secondary || primaryFallback;

        // QC vaporizer.qc:316: the RM-laser path also fires when g_rm_laser == 2 (standalone laser, even without
        // g_rm). The barrage itself only depends on g_rm_laser, so resolve the firing button accordingly: with
        // g_rm_laser==2 the secondary (and the RM out-of-cells primary, which still needs g_rm) drives it.
        float gRmLaser = Api.Cvars.GetFloat("g_rm_laser");
        bool rmLaserMode = (rm && gRmLaser != 0f) || gRmLaser == 2f;

        if (laserFire)
        {
            if (rmLaserMode)
            {
                // QC vaporizer.qc:318-335: the hold-to-stream ramp. First press (jump_interval ready and not
                // loaded) fires the mode-0 FAN and arms hagar_load + jump_interval2 (rapid_delay). While held,
                // once jump_interval2 elapses, fire a single mode-1 bolt on the faster rapid_refire cadence.
                bool rapid = Cvar("g_rm_laser_rapid", 1f) != 0f;
                if (st.JumpInterval <= Api.Clock.Time && st.HagarLoad == 0)
                {
                    if (rapid) st.HagarLoad = 1; // hagar_load (the RM "held_down" latch; reused as a 0/1 flag)
                    st.JumpInterval = Api.Clock.Time + Cvar("g_rm_laser_refire", 0.7f);
                    st.JumpInterval2 = Api.Clock.Time + Cvar("g_rm_laser_rapid_delay", 0.6f);
                    RocketMinstaLaserBarrage(actor, slot, mode: 0);
                }
                else if (rapid && st.JumpInterval2 <= Api.Clock.Time && st.HagarLoad != 0)
                {
                    st.JumpInterval2 = Api.Clock.Time + Cvar("g_rm_laser_rapid_refire", 0.35f);
                    RocketMinstaLaserBarrage(actor, slot, mode: 1);
                }
            }
            else if (st.JumpInterval <= Api.Clock.Time)
            {
                // QC vaporizer.qc:340: jump_interval = time + WEP_CVAR_PRI(WEP_BLASTER, refire) *
                // W_WeaponRateFactor(actor) — the sv_weaponrate (+ Speed powerup/buff) scaling.
                st.JumpInterval = Api.Clock.Time + 0.7f * WeaponRateFactor(actor); // WEP_CVAR_PRI(WEP_BLASTER, refire)

                // QC vaporizer.qc:343-344: W_DecreaseAmmo(thiswep, actor, WEP_CVAR_PRI(WEP_BLASTER, ammo), weaponentity)
                // only fires when the Blaster primary_ammo cvar is set (default 0 — no ammo cost). Harmless at default
                // but ported faithfully so a server that sets g_balance_blaster_primary_ammo != 0 gets the right cost.
                float blasterAmmoCost = Cvar("g_balance_blaster_primary_ammo", 0f);
                if (blasterAmmoCost > 0f)
                    actor.TakeResource(AmmoType, blasterAmmoCost);

                BlasterSecondary(actor, slot);
            }
        }
    }

    // Refire/animtime from the (cvar-seeded) primary balance block (the rail beam). The secondary laser uses
    // its own jump_interval gate, so only the primary mode consults these.
    public override float RefireFor(FireMode fire) => Primary.Refire;
    public override float AnimtimeFor(FireMode fire) => Primary.Animtime;

    // METHOD(Vaporizer, wr_setup / wr_resetplayer) — vaporizer.qc:357-360 + 379-382: clear the impressive
    // every-2nd-hit streak (actor.vaporizer_lasthit = 0) when the weapon becomes active and on a player reset
    // (respawn / round restart), so the streak never carries across lives.
    public override void WrSetup(Entity actor, WeaponSlot slot) => SetLastHit(actor, 0);
    public override void WrResetPlayer(Entity actor, WeaponSlot slot) => SetLastHit(actor, 0);

    // METHOD(Vaporizer, wr_aim) — vaporizer.qc:278-284. With ammo (or IT_UNLIMITED_AMMO) the bot fires the rail
    // PRIMARY (a near-instant hitscan, bot_aim speed 1000000); out of ammo it falls back to the Blaster-laser
    // SECONDARY. The generic BotBrain owns the shot lead/decision; this hook owns only the primary/secondary
    // button pick, mirroring the Rifle/MachineGun pattern. (The bot brain leads a hitscan weapon straight at the
    // target — correct for the rail primary; the rare out-of-ammo secondary laser's projectile lead is not
    // separately modeled because the brain keys lead-speed off the weapon's hitscan flag, not the live fire mode.)
    public override bool BotWantsSecondary(float enemyDistance, float skill, ref BotAimState ctx)
    {
        // primary branch when (items & IT_UNLIMITED_AMMO) || GetResource(ammo_type) > 0; else secondary.
        bool unlimited = ctx.Actor is { } a && (a.UnlimitedAmmo || (a.Items & (int)ItemFlag.UnlimitedAmmo) != 0);
        bool hasAmmo = unlimited || (ctx.Actor is { } b && b.GetResource(AmmoType) > 0f);
        return !hasAmmo;
    }

    // W_Vaporizer_Attack — instant rail beam dealing a huge fixed chunk of damage. vaporizer.qc
    private void Attack(Entity actor, WeaponSlot slot)
    {
        // QC vaporizer.qc:133: capture IsFlying(actor) BEFORE the trace — FireRailgunBullet clobbers the trace
        // globals the yoda mid-air-kill check reads, so sample the shooter's airborne status here first.
        bool flying = IsFlying(actor);

        // damage = (primary_damage > 0) ? primary_damage : 10000 (instant kill).
        float damage = Primary.Damage > 0f ? Primary.Damage : 10000f;

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        // QC W_SetupShot(..., vaporizer_damage, thiswep.m_id): pass wep+maxDamage so the accuracy "fired"
        // denominator is credited (the sister Vortex does this; vaporizer.qc:136).
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, wep: this, maxDamage: damage);

        // FireRailgunBullet: pierces targets, applies the configured knockback force (+ falloff if set).
        // headshotNotify: true — the Vaporizer announces headshots (QC vaporizer.qc:145).
        Vector3 end = shot.Origin + shot.Dir * WeaponFiring.CurrentMaxShotDistance;
        Entity? hit = WeaponFiring.FireRailgunBullet(actor, shot.Origin, end, damage, RegistryId, Primary.Force,
            headshotNotify: true);

        // QC vaporizer.qc:139: sound played separately at VOL_BASE * 0.8 (and bypasses the strength sound).
        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/minstanexfire.wav", SoundLevels.VolBase * 0.8f);

        // Rail-endpoint trace (world-only) → the impact point + surface flags, used for the beam visual, the
        // impact effect/sound, and the Rocket-Minsta sky/noimpact explosion gate (QC trace_endpos / trace_
        // dphitq3surfaceflags after FireRailgunBullet). FireRailgunBullet pierces players, so a fresh world
        // trace gives the wall endpoint the beam actually stopped at.
        // [T45] warpzone-aware: the beam splits at a portal and the impact FX land on the far side.
        WarpzoneTraceResult impW = WeaponFiring.HitscanImpactTrace(actor, shot.Origin, end);
        TraceResult impTr = impW.Trace;
        Vector3 endpos = impTr.EndPos;

        // QC vaporizer.qc:156-157: the cylindric rail beam (gauntletbeam on a hit, lgbeam otherwise) + muzzle
        // flash (EFFECT_VORTEX_MUZZLEFLASH). The port nets a sweep effect (VAPORIZER_BEAM_HIT when a player was
        // pierced, VAPORIZER_BEAM otherwise) and reuses the shared Vortex muzzle flash.
        // QC VaporizerBeam_Draw (vaporizer.qc, CSQC): the beam is tinted client-side. cl_vaporizerbeam_teamcolor
        // (default 1) -> the SHOOTER's team palette color (colormapPaletteColor(team & 0x0F)); else the weapon's
        // default Color ('0.592 0.557 0.824'). Then boosted: rgb *= 1 + cl_vaporizerbeam_colorboost (0.7). The port
        // can't reproduce the bespoke cylindric gauntletbeam/lgbeam draw + 0.8 lifetime (CSQC-only), but it CAN tint
        // the networked sweep via the RGB Emit overload — exactly how the instagib plasma turret sends a team-
        // coloured VAPORIZER_BEAM (PlasmaTurret.cs). cl_vaporizerbeam_teamcolor defaults 1; read it without the
        // Cvar(...) 0->fallback footgun so an explicit 0 (weapon-default color) is honoured.
        bool beamTeamColor = Api.Services is null || Api.Cvars.GetFloat("cl_vaporizerbeam_teamcolor") != 0f;
        Vector3 beamRgb = beamTeamColor ? Teams.ColorRgb((int)actor.Team) : Color;
        beamRgb *= 1f + (Api.Services is null ? 0.7f : Api.Cvars.GetFloat("cl_vaporizerbeam_colorboost")); // colorboost 0.7
        var beam = Effects.ByName(hit is not null ? "VAPORIZER_BEAM_HIT" : "VAPORIZER_BEAM");
        if (impW.ZonesCrossed > 0)
        {
            // Two beam segments through the portal (Base WarpZone_TrailParticles semantics).
            EffectEmitter.Emit(beam, shot.Origin, impW.FirstCrossPoint, 0, beamRgb, beamRgb, except: null);
            EffectEmitter.Emit(beam, impW.FirstExitPoint, endpos, 0, beamRgb, beamRgb, except: null);
        }
        else
        {
            EffectEmitter.Emit(beam, shot.Origin, endpos, 0, beamRgb, beamRgb, except: null);
        }
        EffectEmitter.Emit("VORTEX_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);

        // QC vaporizer.qc:407-413 wr_impacteffect: EFFECT_VORTEX_IMPACT + SND_VAPORIZER_IMPACT (neximpact) at
        // the beam endpoint. The port emits impacts server-side like the Vortex.
        EffectEmitter.Emit("VORTEX_IMPACT", endpos);
        WeaponSplash.ImpactSoundAt(endpos, "weapons/neximpact.wav"); // SND_VAPORIZER_IMPACT

        // Rocket-Minsta: a Devastator-style explosion at the beam endpoint — but NOT when the beam hit sky or a
        // noimpact surface (QC vaporizer.qc:169-171: trace_dphitq3surfaceflags & (SKY | NOIMPACT)).
        if (Api.Services is not null && Api.Cvars.GetFloat("g_rm") != 0f
            && (impTr.DpHitQ3SurfaceFlags & (WeaponFiring.Q3SurfaceFlagSky | Q3SurfaceFlagNoImpact)) == 0)
            RocketMinstaExplosion(actor, endpos);

        // W_DecreaseAmmo (vaporizer.qc:173): (autocvar_g_instagib) ? 1 : primary_ammo. Base keys this on the
        // LIVE cvar only — a non-instagib Vaporizer pickup spends the full primary_ammo per shot.
        actor.TakeResource(AmmoType, InstagibActive ? 1f : Primary.Ammo);

        // Yoda (mid-air rail kill) + Impressive (every-other cross-team rail hit) announcements — QC
        // vaporizer.qc:141-167. QC sets the `yoda`/`impressive_hits` globals inside the Damage path
        // (server/damage.qc:646-651) on a cross-team damaging hit, with yoda also requiring a flying PLAYER
        // victim. FireRailgunBullet doesn't surface those globals, so (mirroring the sister Vortex) re-derive
        // them from the rail's first pierced victim. Done AFTER the trace + ammo, exactly like Base.
        Announce(actor, hit, flying);

        // Deferred (client render): the CSQC beam team-color/colorboost.
    }

    // Per-actor `vaporizer_lasthit` (QC .float vaporizer_lasthit, vaporizer.qc:161-167 + wr_setup/wr_resetplayer
    // reset to 0). QC stores it on the player edict; the port has no Vaporizer field on Entity (cross-file), so
    // we track it in a weak side-table keyed by actor — same observable "only every second consecutive hit"
    // cadence, no Entity.cs edit. Mirrors Vortex._vortexLastHit.
    private static readonly ConditionalWeakTable<Entity, StrongBox<int>> _vaporizerLastHit = new();

    private static int GetLastHit(Entity actor) => _vaporizerLastHit.TryGetValue(actor, out var b) ? b.Value : 0;
    private static void SetLastHit(Entity actor, int v) => _vaporizerLastHit.GetOrCreateValue(actor).Value = v;

    // Yoda / Impressive — port of vaporizer.qc:141-167. `flying` is the shooter's airborne status captured
    // before the trace. `hit` is FireRailgunBullet's first pierced victim (the rail's primary target). This
    // mirrors Vortex.Announce; the Vaporizer differs from the Vortex only in firing headshotNotify and reading
    // its own vaporizer_lasthit toggle (both handled here).
    private void Announce(Entity actor, Entity? hit, bool flying)
    {
        if ((actor.Flags & EntFlags.Client) == 0) return; // only real clients get announces

        // QC ++impressive_hits (damage.qc:646): a cross-team damaging hit on a hittable victim. The rail always
        // deals damage > 0, so any live, damageable, cross-team target counts as an impressive hit.
        bool impressiveHit = hit is not null
            && hit.TakeDamage != DamageMode.No
            && hit.DeadState == DeadFlag.No
            && !ReferenceEquals(hit, actor)
            && !Teams.SameTeam(hit, actor);

        // QC yoda (damage.qc:648-651): the victim is a non-special-death PLAYER and IsFlying(victim); plus the
        // vaporizer block also gates on the SHOOTER flying (vaporizer.qc:159 `if (yoda && flying)`).
        // QC instagib yoda (sv_instagib.qc:245-247): ALSO fires when the victim had alpha in (0,1) (cloaked/
        // invisible) — that branch sets the global `yoda=1` which the same `if (yoda && flying)` gate catches.
        // The port stores the instagib alpha-yoda on the target as InstagibAlphaYoda (set in
        // InstagibMutator.OnDamageCalculate), then clears it here after sampling so it doesn't bleed across shots.
        bool alphaYoda = hit is not null && hit.InstagibAlphaYoda;
        if (hit is not null) hit.InstagibAlphaYoda = false; // per-attack flag: consume it here
        if (impressiveHit && flying
            && (hit!.Flags & EntFlags.Client) != 0 && (IsFlying(hit) || alphaYoda))
            NotificationSystem.Announce(actor, "ACHIEVEMENT_YODA");

        // QC vaporizer.qc:161-167: impressive fires only when THIS shot AND the previous one both landed a hit
        // (actor.vaporizer_lasthit), then resets so it's every-other; vaporizer_lasthit is then set to this
        // shot's hit state.
        int impressive = impressiveHit ? 1 : 0;
        if (impressive != 0 && GetLastHit(actor) != 0)
        {
            NotificationSystem.Announce(actor, "ACHIEVEMENT_IMPRESSIVE");
            impressive = 0; // only every second time
        }
        SetLastHit(actor, impressive);
    }

    // W_RocketMinsta_Explosion — radius blast at the rail endpoint (g_rm_*). vaporizer.qc:108.
    private void RocketMinstaExplosion(Entity actor, Vector3 loc)
    {
        // QC tags the blast WEP_DEVASTATOR.m_id | HITTYPE_SPLASH and credits WEP_DEVASTATOR accuracy — so the
        // RocketMinsta mutator's devastator-keyed no-self-damage / force-gib hooks match, and the obituary/
        // accuracy attribute to the Devastator, not the Vaporizer. Resolve the Devastator's death tag and pass
        // it as the SPECIAL deathTag (RadiusDamage routes it through the string pipeline + ORs HITTYPE_SPLASH on
        // the indirect victims). accuracyWeapon = the Devastator credits "fired" damage (QC vaporizer.qc:110-111).
        var devastator = Weapons.ByName("devastator");
        string? devTag = devastator is not null ? Damage.DeathTypes.FromWeapon(devastator.NetName) : null;
        WeaponSplash.RadiusDamage(actor, loc,
            Cvar("g_rm_damage", 70f), Cvar("g_rm_edgedamage", 38f), Cvar("g_rm_radius", 140f),
            actor, RegistryId, Cvar("g_rm_force", 400f),
            accuracyWeapon: devastator, deathTag: devTag);

        // QC W_RocketMinsta_Explosion deals only the radius damage; the blast is tagged WEP_DEVASTATOR.m_id, so
        // CSQC routes the impact through the DEVASTATOR's wr_impacteffect (devastator.qc:569-575): EFFECT_ROCKET_
        // EXPLODE pointparticles + SND_ROCKET_IMPACT. RadiusDamage doesn't emit centrally (WeaponSplash.cs:275 —
        // the caller owns it), so emit the Devastator explosion presentation here at the rail endpoint, exactly
        // as Devastator.Explode does for a normal rocket detonation.
        EffectEmitter.Emit("ROCKET_EXPLODE", loc);
        WeaponSplash.ImpactSoundAt(loc, "weapons/rocket_impact.wav"); // SND_ROCKET_IMPACT
    }

    // W_Blaster_Attack via the Blaster weapon — the secondary knockback laser.
    private void BlasterSecondary(Entity actor, WeaponSlot slot)
    {
        var blaster = Registry<Weapon>.ByName("blaster") as Blaster;
        if (blaster is null) return;
        // Fire the Blaster's primary bolt directly from the actor's aim (QC makevectors(v_angle) +
        // W_Blaster_Attack). This is the Vaporizer's own refire-gated secondary (jump_interval), so it must
        // bypass the Blaster's refire gate — go through FirePrimaryDirect, not the gated WrThink.
        blaster.FirePrimaryDirect(actor, slot);
    }

    // W_RocketMinsta_Attack — a fan (mode 0) of short-lived bouncing laser bolts, or a single straight bolt
    // (mode 1, the rapid-fire stream). g_rm_laser_*. vaporizer.qc:222.
    private void RocketMinstaLaserBarrage(Entity actor, WeaponSlot slot, int mode)
    {
        // Cvar fallbacks are the Base mutators.cfg:447-460 defaults (the cfg ships these; the fallbacks fire
        // only on a bare config). count 3, damage 80, radius 150, force 400, speed 6000, lifetime 30, spread 0.05.
        // QC: laser_count = max(1, g_rm_laser_count); total = (mode==0) ? laser_count : 1 (vaporizer.qc:227-228).
        int laserCount = (int)MathF.Max(1f, Cvar("g_rm_laser_count", 3f));
        int count = (mode == 0) ? laserCount : 1;
        float damage = Cvar("g_rm_laser_damage", 80f);
        float radius = Cvar("g_rm_laser_radius", 150f);
        float force = Cvar("g_rm_laser_force", 400f);
        float speed = Cvar("g_rm_laser_speed", 6000f);
        // QC vaporizer.qc:256: spread = g_rm_laser_spread * (g_rm_laser_spread_random ? random() : 1).
        float spread = Cvar("g_rm_laser_spread", 0.05f);
        if (Cvar("g_rm_laser_spread_random", 0f) != 0f) spread *= Prandom.Float();
        float zspread = Cvar("g_rm_laser_zspread", 0f);
        float lifetime = Cvar("g_rm_laser_lifetime", 30f);
        // QC vaporizer.qc:263: W_CalculateProjectileVelocity(actor, actor.velocity, vel, /*forceAbsolute*/true).
        // With forceAbsolute true the Newtonian owner-velocity inheritance is bypassed (newton_style forced 0),
        // so the only live effect is the g_weaponspeedfactor scale (util.qc get_shotvelocity: spd * dir).
        float speedFactor = WeaponSpeedFactor();

        // QC vaporizer.qc:230,245: the bolts set up + tag as WEP_ELECTRO (proj.projectiledeathtype = WEP_ELECTRO
        // .m_id) — so the RocketMinsta mutator's electro-keyed no-self-damage hook matches and the kill feed /
        // accuracy attribute to the Electro, not the Vaporizer. Fall back to the Vaporizer id if Electro is
        // somehow unregistered.
        var electro = Weapons.ByName("electro");
        int laserDeathType = electro?.RegistryId ?? RegistryId;

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out Vector3 right, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, new Vector3(0, 0, -3), new Vector3(0, 0, -3));

        // QC vaporizer.qc:233: W_MuzzleFlash(WEP_ELECTRO) — the barrage uses electro muzzle effects.
        EffectEmitter.Emit("ELECTRO_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);

        for (int i = 0; i < count; ++i)
        {
            Entity proj = Api.Entities.Spawn();
            proj.ClassName = "plasma_prim";
            proj.Owner = actor;
            // QC nets each bolt as PROJECTILE_ROCKETMINSTA_LASER (vaporizer.qc:272) — a DISTINCT visual from the
            // generic plasma/vaporizer bolt: models/elaser.mdl + the EFFECT_ROCKETMINSTA_LASER trail. The port keys
            // the client's ProjectileCatalog off classname+netname, so tag the bolt's netname "rocketminsta" to pick
            // the RocketMinstaLaser descriptor (red laser, elaser model) instead of falling through to "plasma".
            proj.NetName = "rocketminsta";
            // QC CSQCProjectile_SendEntity nets realowner.team as the bolt's colormap; projectile.qc tints the
            // PROJECTILE_ROCKETMINSTA_LASER body to that team palette (colormod = colormapPaletteColor(colormap&0x0F)).
            // Carry the firer's team so the colormap networks (ServerNet nets Colormap = (int)Team) and the client
            // can apply the team colormod.
            proj.Team = actor.Team;
            proj.MoveType = MoveType.BounceMissile;
            Projectiles.MakeTrigger(proj); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
            proj.Flags = EntFlags.Item;
            Api.Entities.SetSize(proj, new Vector3(0, 0, -3), new Vector3(0, 0, -3));
            Api.Entities.SetOrigin(proj, shot.Origin);

            // QC vaporizer.qc:254-262. mode 0 (fan): velocity = (w_shotdir + (((i+0.5)/count)*2 - 1) * right *
            // spread) * speed; z += zspread*(random-0.5). mode 1 (rapid stream): a single straight bolt =
            // w_shotdir * speed (no fan, no zspread). QC uses (random()-0.5) → [-0.5,0.5] (not a signed [-1,1]).
            Vector3 v;
            if (mode == 0)
            {
                float lat = ((i + 0.5f) / count) * 2f - 1f;
                v = (shot.Dir + lat * right * spread) * speed;
                v.Z += zspread * (Prandom.Float() - 0.5f);
            }
            else
                v = shot.Dir * speed;
            // QC vaporizer.qc:263: W_CalculateProjectileVelocity(..., true) — g_weaponspeedfactor scale only.
            proj.Velocity = v * speedFactor;
            proj.Angles = QMath.VecToAngles(proj.Velocity);

            // QC vaporizer.qc:239-240 — flag the RM-laser bolt as a dodgeable hazard (rating = g_rm_laser_damage).
            proj.BotDodge = true;
            proj.BotDodgeRating = Cvar("g_rm_laser_damage", 150f);

            // Per-bolt damage. QC distinguishes the two explode paths: the DIRECT touch
            // (W_RocketMinsta_Laser_Touch, vaporizer.qc:212) just damages; the TIMEOUT (the .use think →
            // W_RocketMinsta_Laser_Explode, vaporizer.qc:194) additionally awards the Electrobitch achievement
            // when the blast lands on a flying enemy of another team. rm_laser_count = total (so damage/force are
            // divided by the same count the bolt was spawned with — captured here as `count`).
            int rmLaserCount = count;
            proj.Touch = (self, other) =>
            {
                WeaponSplash.RadiusDamage(self, self.Origin, damage / rmLaserCount, damage / rmLaserCount, radius,
                    self.Owner, laserDeathType, force / rmLaserCount, directHit: other);
                Api.Entities.Remove(self);
            };
            proj.Think = self =>
            {
                // QC TIMEOUT path: setthink(adaptor_think2use_hittype_splash) → adaptor_think2use(this) →
                // this.use(this, NULL, NULL) → W_RocketMinsta_Laser_Explode_use(this, NULL, /*trigger*/NULL) →
                // W_RocketMinsta_Laser_Explode(this, /*directhitentity*/NULL). The Electrobitch guard
                // (vaporizer.qc:196-199) tests directhitentity.takedamage==DAMAGE_AIM && IS_PLAYER(directhitentity)
                // && DIFF_TEAM && !IS_DEAD && IsFlying — but the timeout's directhitentity is the NULL world
                // entity, so the guard never passes. RocketMinstaLaserExplode reproduces the guard faithfully and
                // is invoked with NO direct-hit victim here, so (matching Base) the timeout never awards the
                // achievement. The Touch path (above) calls W_RocketMinsta_Laser_Damage directly with no guard.
                RocketMinstaLaserExplode(self, directHit: null, damage / rmLaserCount, radius,
                    force / rmLaserCount, laserDeathType);
            };
            proj.NextThink = Api.Clock.Time + lifetime;

            // MUTATOR_CALLHOOK(EditProjectile, actor, proj) — fired per laser bolt (vaporizer.qc RM laser barrage).
            var ep = new MutatorHooks.EditProjectileArgs(actor, proj);
            MutatorHooks.EditProjectile.Call(ref ep);
        }

        // QC vaporizer.qc:229: snd = (mode==0) ? SND_CRYLINK_FIRE : SND_ELECTRO_FIRE2. W_SetupShot_ProjectileSize
        // already plays this on CH_WEAPON_A; the port emits it here. mode-0 fan = crylink_fire, mode-1 stream =
        // electro_fire2.
        Api.Sound.Play(actor, SoundChannel.Weapon,
            mode == 0 ? "weapons/crylink_fire.wav" : "weapons/electro_fire2.wav");
    }

    // W_RocketMinsta_Laser_Explode — the TIMEOUT explode path (vaporizer.qc:194-205). Awards the Electrobitch
    // achievement when the blast's direct-hit victim is a flying enemy player (DAMAGE_AIM, different team, not
    // dead) — but the timeout adaptor passes a NULL directhitentity, so in practice the guard never passes
    // (faithful to Base; the touch path never reaches here). Then deals the radius damage and frees the bolt.
    private void RocketMinstaLaserExplode(Entity self, Entity? directHit, float damagePerBolt, float radius,
        float forcePerBolt, int laserDeathType)
    {
        if (directHit is not null && self.Owner is { } owner
            && directHit.TakeDamage == DamageMode.Aim && (directHit.Flags & EntFlags.Client) != 0
            && !Teams.SameTeam(directHit, owner) && !ReferenceEquals(directHit, owner) // QC DIFF_TEAM
            && directHit.DeadState == DeadFlag.No && IsFlying(directHit))
            NotificationSystem.Announce(owner, "ACHIEVEMENT_ELECTROBITCH");

        WeaponSplash.RadiusDamage(self, self.Origin, damagePerBolt, damagePerBolt, radius,
            self.Owner, laserDeathType, forcePerBolt, directHit: directHit);
        Api.Entities.Remove(self);
    }

    // QC W_WeaponSpeedFactor / W_CalculateProjectileVelocity(forceAbsolute=true): g_weaponspeedfactor (default 1).
    // Mirrors Devastator.WeaponSpeedFactor — only positive values take effect.
    private static float WeaponSpeedFactor()
    {
        if (Api.Services is null) return 1f;
        float f = Api.Cvars.GetFloat("g_weaponspeedfactor");
        return f > 0f ? f : 1f;
    }

    // bool IsFlying(entity) — airshot test (mirrors Devastator/Vortex): airborne, not swimming, ≥24u clearance.
    private static bool IsFlying(Entity e)
    {
        if (e.OnGround) return false;
        if (e.WaterLevel >= 2) return false; // WATERLEVEL_SWIMMING
        TraceResult tr = Api.Trace.Trace(e.Origin, e.Mins, e.Maxs,
            e.Origin - new Vector3(0f, 0f, 24f), MoveFilter.Normal, e);
        return tr.Fraction >= 1f;
    }

    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }

    // METHOD(Vaporizer, wr_checkammo1) — vaporizer.qc:362 (instagib needs 1 cell, else primary_ammo). QC re-reads
    // autocvar_g_instagib here, so the non-instagib loadout correctly requires the full primary_ammo per shot.
    public bool CheckAmmoPrimary(Entity actor)
        => actor.GetResource(AmmoType) >= (InstagibActive ? 1f : Primary.Ammo);

    // METHOD(Vaporizer, wr_checkammo2) — vaporizer.qc:370: the Blaster-laser secondary is free at the default
    // Blaster ammo 0 (return true); otherwise it needs WEP_CVAR_PRI(WEP_BLASTER, ammo) cells.
    public bool CheckAmmoSecondary(Entity actor)
    {
        float blasterAmmo = Cvar("g_balance_blaster_primary_ammo", 0f);
        return blasterAmmo <= 0f || actor.GetResource(AmmoType) >= blasterAmmo;
    }

    /// <summary>QC <c>autocvar_g_instagib</c>: the live instagib gate (vaporizer.qc re-reads this at every
    /// W_DecreaseAmmo / wr_checkammo call). A non-instagib map pickup of the Vaporizer correctly spends/checks
    /// <c>primary_ammo</c> (10 cells) rather than the instagib 1-cell cost. Returns <c>false</c> when services
    /// are unavailable (headless/test context without a live game — treat as no mutator).</summary>
    private bool InstagibActive => Api.Services is not null && Api.Cvars.GetFloat("g_instagib") != 0f;

    /// <summary>QC Q3SURFACEFLAG_NOIMPACT (BIT(0)) — a surface nothing impacts; the RM explosion is suppressed
    /// when the rail endpoint lands on it (or on sky), matching vaporizer.qc:170.</summary>
    private const int Q3SurfaceFlagNoImpact = 0x1;
}
