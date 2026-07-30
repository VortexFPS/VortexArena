// Registration of the named particle effects this port implements — the C# successor to the
// EFFECT(...) table in Base/.../qcsrc/common/effects/all.inc.
//
// The lead calls Effects.RegisterAll() once from GameInit (alongside the other RegisterAll()s).
// This is a self-registering catalog: no [attribute] reflection bootstrap (that path in Registries.cs
// only knows Weapon/Pickup/Mutator/GameType/Monster/Turret/Vehicle).
//
// Coverage: the COMPLETE all.inc EFFECT() registry — every entry that QC networks by id (the registry
// hash must match the engine's, so this is the authoritative ordered list). The commented-out QC entries
// (legacy per-team vaporizer/spawn/rocketminsta variants slated for removal post-0.9.0) are left out
// exactly as QC leaves them out. The richer effectinfo.txt particle *parameters* (color/size/type) are a
// client-side rendering concern resolved by name; they are not part of this server-side registry.

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Installs the named particle effects into <see cref="Effects"/>. Idempotent (registration is by name);
/// call <see cref="RegisterAll"/> once at boot. Names mirror the QC <c>EFFECT_*</c> identifiers exactly,
/// in <c>all.inc</c> order. EFFECT(istrail, NAME, "effectinfo") -> Effects.Register("NAME", "effectinfo", istrail).
/// </summary>
public static class EffectsList
{
    public static void RegisterAll()
    {
        // EFFECT_Null is registered first in QC (id 0). Keep that ordering convention.
        Effects.Register("Null", "", false);

        // ---- generic explosions / smoke ----
        Effects.Register("EXPLOSION_SMALL", "explosion_small");
        Effects.Register("EXPLOSION_MEDIUM", "explosion_medium");
        Effects.Register("EXPLOSION_BIG", "explosion_big");
        Effects.Register("SMOKE_SMALL", "smoke_small");
        Effects.Register("SMOKE_LARGE", "smoke_large");

        // ---- Arc (muzzleflash up top in QC, with the beam set below) ----
        Effects.Register("ARC_MUZZLEFLASH", "electro_muzzleflash");

        // ---- Blaster ----
        Effects.Register("BLASTER_IMPACT", "laser_impact");
        Effects.Register("BLASTER_MUZZLEFLASH", "laser_muzzleflash");

        // ---- Shotgun ----
        Effects.Register("SHOTGUN_IMPACT", "shotgun_impact");
        Effects.Register("SHOTGUN_MUZZLEFLASH", "shotgun_muzzleflash");
        Effects.Register("SHOTGUN_WOOSH", "shotgun_woosh");

        // ---- Arc beam family ----
        // The arc/heal beams are drawn as TRAILS (arc_beam is a `type spark` trailspacing-10 effect; heal is a
        // smoke trail) swept origin→hit-point, like VORTEX_BEAM. They MUST be isTrail so EffectEmitter.Emit
        // doesn't drop the count-0 beam emission as an empty point effect, and so the client sweeps the segment.
        Effects.Register("ARC_BEAM", "arc_beam", isTrail: true);
        Effects.Register("ARC_BEAM_HEAL", "arc_beam_heal", isTrail: true);
        Effects.Register("ARC_BEAM_HEAL_IMPACT", "arc_beam_healimpact");
        Effects.Register("ARC_BEAM_HEAL_IMPACT2", "healray_impact");
        Effects.Register("ARC_OVERHEAT", "arc_overheat");
        Effects.Register("ARC_OVERHEAT_FIRE", "arc_overheat_fire");
        Effects.Register("ARC_SMOKE", "arc_smoke");
        // ARC_LIGHTNING carries the te_csqc_lightningarc bolt (a from→hit-point line, the port's analogue of
        // Base's dedicated TE_CSQC_ARC NET_TEMP; EffectEmitter.TeCsqcLightningArc emits it count-0 with the END
        // point in velocity). Like ARC_BEAM/ARC_BEAM_HEAL it MUST be isTrail so EffectEmitter.Emit doesn't drop
        // the count-0 emission as an empty point effect (the count-0 point guard) — without this the electro
        // combo / Tesla turret / Golem zaps never network the arc to remote clients. The client routes the name
        // to BeamRenderer.Arc (the crackling bolt), so the trail flag only governs networking, not the visual.
        Effects.Register("ARC_LIGHTNING", "arc_lightning", isTrail: true);

        // The CYLINDRIC arc-weapon beam line (Base Draw_ArcBeam -> Draw_CylindricLine, the cl_arcbeam_simple
        // default). DISTINCT from the arc_beam/arc_beam_heal PARTICLE trails above (which DP draws too, as
        // trailparticles ALONGSIDE the cylinder): these two names carry no effectinfo block, so they short-circuit
        // at the EffectSystem Beam route (drawn via BeamRenderer.Beam between origin and the velocity end-point)
        // before the effectinfo lookup. Registered isTrail ONLY so the count-0 line emission survives the
        // EffectEmitter.Emit point-count guard (same trick as ARC_LIGHTNING) — the trail flag governs networking,
        // not the visual; the explicit Beam-class check in EffectSystem routes them to the drawn line.
        Effects.Register("ARC_BEAM_LINE", "arc_beam_line", isTrail: true);
        Effects.Register("ARC_BEAM_LINE_HEAL", "arc_beam_line_heal", isTrail: true);
        // The BURST arc-beam line (Base ARC_BT_BURST, a fatter cl_arcbeam burst-mode cylinder). Same Beam-route
        // family as the two lines above — the name contains 'ARC_BEAM_LINE' so EffectSystem.ClassifyUncached's
        // needle routes it to EffectClass.Beam, short-circuiting before the (absent) effectinfo lookup. The width
        // difference (burst 14 vs steady 8) is applied downstream in EffectSystem, not here. Carries no effectinfo
        // block; registered isTrail ONLY so the count-0 line emission survives EffectEmitter.Emit's point-count
        // guard (same trick as ARC_BEAM_LINE/_HEAL) — the trail flag governs networking, not the visual.
        Effects.Register("ARC_BEAM_LINE_BURST", "arc_beam_line_burst", isTrail: true);

        // ---- Machine Gun ----
        Effects.Register("MACHINEGUN_IMPACT", "machinegun_impact");
        Effects.Register("MACHINEGUN_MUZZLEFLASH", "uzi_muzzleflash");

        // ---- Mortar / Grenade Launcher ----
        Effects.Register("GRENADE_EXPLODE", "grenade_explode");
        Effects.Register("GRENADE_MUZZLEFLASH", "grenadelauncher_muzzleflash");

        // ---- Electro ----
        Effects.Register("ELECTRO_BALLEXPLODE", "electro_ballexplode");
        Effects.Register("ELECTRO_COMBO", "electro_combo");
        Effects.Register("ELECTRO_IMPACT", "electro_impact");
        Effects.Register("ELECTRO_MUZZLEFLASH", "electro_muzzleflash");

        // ---- Crylink ----
        Effects.Register("CRYLINK_IMPACT", "crylink_impactbig");
        Effects.Register("CRYLINK_IMPACT2", "crylink_impact");
        Effects.Register("CRYLINK_JOINEXPLODE", "crylink_joinexplode");
        Effects.Register("CRYLINK_MUZZLEFLASH", "crylink_muzzleflash");

        // ---- HLAC ----
        Effects.Register("GREEN_HLAC_IMPACT", "hlac_impact");
        Effects.Register("GREEN_HLAC_MUZZLEFLASH", "hlac_muzzleflash");

        // ---- Vortex ----
        Effects.Register("VORTEX_BEAM", "nex_beam", isTrail: true);
        Effects.Register("VORTEX_BEAM_OLD", "TE_TEI_G3");
        Effects.Register("VORTEX_IMPACT", "nex_impact");
        Effects.Register("VORTEX_MUZZLEFLASH", "nex_muzzleflash");

        // ---- Vaporizer (instagib) ----
        Effects.Register("VAPORIZER_BEAM", "TE_TEI_G3", isTrail: true);
        Effects.Register("VAPORIZER_BEAM_HIT", "TE_TEI_G3_HIT", isTrail: true);

        // ---- Brass casings (the QC `casings` NET_TEMP, common/effects/qc/casings.qc) ----
        // Base networks ejected shells via a dedicated REGISTER_NET_TEMP(casings); this port carries them on
        // the shared effect channel instead. Registering them as real Effects (rather than null-Effect
        // EmitByEffectInfoName requests, which ServerNet.WriteEffect drops) gives them a stable RegistryId so
        // the eject origin+velocity actually network; the client routes the name to EffectSystem.SpawnCasing
        // (the real bouncing brass entity), not the generic particle burst. casingtype 3 -> bullet, 1 -> shell.
        Effects.Register("CASING_BULLET", "casing_bullet");
        Effects.Register("CASING_SHELL", "casing_shell");

        // ---- Rifle ----
        Effects.Register("RIFLE_IMPACT", "machinegun_impact");
        Effects.Register("RIFLE_MUZZLEFLASH", "rifle_muzzleflash");
        Effects.Register("RIFLE", "tr_rifle", isTrail: true);
        Effects.Register("RIFLE_WEAK", "tr_rifle_weak", isTrail: true);

        // ---- Hagar ----
        Effects.Register("HAGAR_BOUNCE", "hagar_bounce");
        Effects.Register("HAGAR_EXPLODE", "hagar_explode");
        Effects.Register("HAGAR_MUZZLEFLASH", "hagar_muzzleflash");
        Effects.Register("HAGAR_ROCKET", "tr_hagar", isTrail: true);

        // ---- Devastator (rocket launcher) ----
        Effects.Register("ROCKET_EXPLODE", "rocket_explode");
        Effects.Register("ROCKET_GUIDE", "rocket_guide");
        Effects.Register("ROCKET_MUZZLEFLASH", "rocketlauncher_muzzleflash");

        // ---- Hook ----
        Effects.Register("HOOK_EXPLODE", "hookbomb_explode");
        Effects.Register("HOOK_IMPACT", "grapple_impact");
        Effects.Register("HOOK_MUZZLEFLASH", "grapple_muzzleflash");

        // ---- Seeker ----
        Effects.Register("SEEKER_MUZZLEFLASH", "seeker_muzzleflash");

        // ---- Fireball (Fireball weapon / mage) ----
        Effects.Register("FIREBALL", "fireball", isTrail: true);
        Effects.Register("FIREBALL_BFGDAMAGE", "fireball_bfgdamage");
        Effects.Register("FIREBALL_EXPLODE", "fireball_explode");
        Effects.Register("FIREBALL_LASER", "fireball_laser");
        Effects.Register("FIREBALL_MUZZLEFLASH", "fireball_muzzleflash");
        Effects.Register("FIREBALL_PRE_MUZZLEFLASH", "fireball_preattack_muzzleflash");

        // ---- Raptor (vehicle) ----
        Effects.Register("RAPTOR_CANNON_IMPACT", "raptor_cannon_impact");
        Effects.Register("RAPTOR_BOMB_IMPACT", "raptor_bomb_impact");
        Effects.Register("RAPTOR_BOMB_SPREAD", "raptor_bomb_spread");
        Effects.Register("RAPTOR_MUZZLEFLASH", "raptor_cannon_muzzleflash");

        // ---- Racer (vehicle) ----
        Effects.Register("RACER_BOOSTER", "wakizashi_booster_smoke");
        Effects.Register("RACER_IMPACT", "wakizashi_gun_impact");
        Effects.Register("RACER_MUZZLEFLASH", "wakizashi_gun_muzzleflash");
        Effects.Register("RACER_ROCKETLAUNCH", "wakizashi_rocket_launch");
        Effects.Register("RACER_ROCKET_EXPLODE", "wakizashi_rocket_explode");
        Effects.Register("RACER_ROCKET_TRAIL", "wakizashi_rocket_thrust", isTrail: true);

        // ---- Spiderbot (vehicle) ----
        Effects.Register("SPIDERBOT_ROCKETLAUNCH", "spiderbot_rocket_launch");
        Effects.Register("SPIDERBOT_ROCKET_TRAIL", "spiderbot_rocket_thrust", isTrail: true);
        Effects.Register("SPIDERBOT_ROCKET_EXPLODE", "spiderbot_rocket_explode");
        Effects.Register("SPIDERBOT_MINIGUN_IMPACT", "spiderbot_minigun_impact");
        Effects.Register("SPIDERBOT_MINIGUN_MUZZLEFLASH", "spiderbot_minigun_muzzleflash");

        // ---- Bumblebee (vehicle) ----
        Effects.Register("BUMBLEBEE_HEAL_MUZZLEFLASH", "healray_muzzleflash");
        Effects.Register("BUMBLEBEE_HEAL_IMPACT", "healray_impact");

        // ---- big plasma (turret / monster) ----
        Effects.Register("BIGPLASMA_IMPACT", "bigplasma_impact");
        Effects.Register("BIGPLASMA_MUZZLEFLASH", "bigplasma_muzzleflash");

        // ---- teleport ----
        Effects.Register("TELEPORT", "teleport");

        // ---- spawn / spawnpoint (neutral; legacy per-team variants are commented out in QC) ----
        Effects.Register("SPAWNPOINT", "spawn_point_neutral");
        Effects.Register("SPAWN", "spawn_event_neutral");

        // ---- ambient fields / regen ----
        Effects.Register("DARKFIELD", "darkfield");
        Effects.Register("ICEORGLASS", "iceorglass");
        Effects.Register("ICEFIELD", "icefield");
        Effects.Register("FIREFIELD", "firefield");
        Effects.Register("HEALING", "healing_fx");
        Effects.Register("ARMOR_REPAIR", "armorrepair_fx");
        Effects.Register("AMMO_REGEN", "ammoregen_fx");
        Effects.Register("LASER_BEAM_FAST", "nex242_misc_laser_beam_fast", isTrail: true);
        Effects.Register("RESPAWN_GHOST", "respawn_ghost");

        // ---- CTF flag effects (team-keyed; resolved via Effects.FlagTouch/Pass/Cap) ----
        Effects.Register("FLAG_TOUCH_RED", "redflag_touch");
        Effects.Register("FLAG_TOUCH_BLUE", "blueflag_touch");
        Effects.Register("FLAG_TOUCH_YELLOW", "yellowflag_touch");
        Effects.Register("FLAG_TOUCH_PINK", "pinkflag_touch");
        Effects.Register("FLAG_TOUCH_NEUTRAL", "neutralflag_touch");

        Effects.Register("PASS_RED", "red_pass", isTrail: true);
        Effects.Register("PASS_BLUE", "blue_pass", isTrail: true);
        Effects.Register("PASS_YELLOW", "yellow_pass", isTrail: true);
        Effects.Register("PASS_PINK", "pink_pass", isTrail: true);
        Effects.Register("PASS_NEUTRAL", "neutral_pass", isTrail: true);

        Effects.Register("CAP_RED", "red_cap");
        Effects.Register("CAP_BLUE", "blue_cap");
        Effects.Register("CAP_YELLOW", "yellow_cap");
        Effects.Register("CAP_PINK", "pink_cap");
        Effects.Register("CAP_NEUTRAL", "neutral_cap");

        // ---- item pickup / respawn / despawn ----
        Effects.Register("ITEM_PICKUP", "item_pickup");
        Effects.Register("ITEM_RESPAWN", "item_respawn");
        Effects.Register("ITEM_DESPAWN", "item_despawn");

        // ---- Onslaught (generator / electricity / shockwave) ----
        Effects.Register("ONS_ELECTRICITY_EXPLODE", "electro_ballexplode");
        Effects.Register("ONS_GENERATOR_DAMAGED", "torch_small");
        Effects.Register("ONS_GENERATOR_GIB", "onslaught_generator_gib_explode");
        Effects.Register("ONS_GENERATOR_EXPLODE", "onslaught_generator_smallexplosion");
        Effects.Register("ONS_GENERATOR_EXPLODE2", "onslaught_generator_finalexplosion");
        Effects.Register("ONS_SHOCKWAVE", "electro_combo");

        // ---- Keepaway ----
        Effects.Register("KA_BALL_RESPAWN", "electro_combo");

        // ---- misc weapons / ambient / nade / sparks ----
        Effects.Register("LASER_DEADLY", "laser_deadly");
        Effects.Register("FLAC_TRAIL", "TR_SEEKER", isTrail: true);
        Effects.Register("SEEKER_TRAIL", "TR_SEEKER", isTrail: true);
        Effects.Register("FIREMINE", "firemine", isTrail: true);
        Effects.Register("BALL_SPARKS", "kaball_sparks");
        Effects.Register("ELECTRIC_SPARKS", "electricity_sparks");
        Effects.Register("SPARKS", "sparks");
        Effects.Register("RAGE", "rage");
        Effects.Register("SMOKING", "smoking");
        Effects.Register("SMOKE_RING", "smoke_ring");
        Effects.Register("JUMPPAD", "jumppad_activate");
        Effects.Register("BULLET", "tr_bullet", isTrail: true);
        Effects.Register("BULLET_WEAK", "tr_bullet_weak", isTrail: true);

        // ---- engine te_* effects (DarkPlaces built-in particle effects) ----
        Effects.Register("EF_SHOCK", "EF_SHOCK");
        Effects.Register("EF_FLAME", "EF_FLAME");
        Effects.Register("EF_STARDUST", "EF_STARDUST");
        Effects.Register("TE_EXPLOSION", "TE_EXPLOSION");
        Effects.Register("TR_NEXUIZPLASMA", "TR_NEXUIZPLASMA", isTrail: true);
        Effects.Register("TR_CRYLINKPLASMA", "TR_CRYLINKPLASMA", isTrail: true);
        Effects.Register("TR_ROCKET", "TR_ROCKET", isTrail: true);
        Effects.Register("TR_GRENADE", "TR_GRENADE", isTrail: true);
        Effects.Register("TR_BLOOD", "TR_BLOOD", isTrail: true);
        Effects.Register("TR_WIZSPIKE", "TR_WIZSPIKE", isTrail: true);
        Effects.Register("TR_SLIGHTBLOOD", "TR_SLIGHTBLOOD", isTrail: true);
        Effects.Register("TR_KNIGHTSPIKE", "TR_KNIGHTSPIKE", isTrail: true);
        Effects.Register("TR_VORESPIKE", "TR_VORESPIKE", isTrail: true);
        Effects.Register("TE_SPARK", "TE_SPARK");
        // te_knightspike() builtin point-effect (effectinfo.txt: decal + static + 128 reddish-orange sparks).
        // Distinct from the TR_KNIGHTSPIKE *trail* above — this is the burst the Seeker tag-strike emits.
        Effects.Register("TE_KNIGHTSPIKE", "TE_KNIGHTSPIKE");
        // te_smallflash()/te_gunshot() engine builtins resolve their OWN effectinfo blocks (TE_SMALLFLASH /
        // TE_GUNSHOT), NOT a weapon's named muzzleflash/impact. Registered so the EffectEmitter.TeSmallflash /
        // TeGunshot wrappers network the correct block by id instead of a divergent substitute.
        Effects.Register("TE_SMALLFLASH", "TE_SMALLFLASH");
        Effects.Register("TE_GUNSHOT", "TE_GUNSHOT");
        // te_tarexplosion() engine builtin (the dark "tar" explosion, distinct from te_explosion). KeyHunt fires
        // it for a lost/destroyed key (sv_keyhunt.qc kh_Key_AssignTo te_tarexplosion(lostkey.origin)); effectinfo.txt
        // ships a dedicated TE_TAREXPLOSION block, so map the wrapper to it rather than substituting TE_EXPLOSION.
        Effects.Register("TE_TAREXPLOSION", "TE_TAREXPLOSION");

        // ---- RocketMinsta laser (neutral; per-team variants are commented out in QC) ----
        Effects.Register("ROCKETMINSTA_LASER", "rocketminsta_laser_neutral", isTrail: true);

        // ---- generic damage blood puff (te_blood analogue; not in all.inc but used by EffectEmitter.TeBlood) ----
        Effects.Register("BLOOD", "blood");

        // ---- Bumblebee gunner heal/damage rays (port-specific; not in all.inc) ----
        // Base draws these as a linked entity (bumble_raygun_send/_draw, a Draw_CylindricLine cylinder driven by
        // the vehicle's own networked state), NOT a Send_Effect particle. The port carries them on the shared
        // effect channel as beam-class emissions (EffectEmitter.TeBeam/TeHealBeam, the bolt's END point in
        // velocity and the colormod in the wire color fields). Registering them as real trail Effects (like the
        // casings precedent above) gives each a stable RegistryId so the beam ACTUALLY networks to remote
        // clients — a null-Effect EmitByEffectInfoName request is dropped by ServerNet.WriteEffect, so without
        // this only the listen-server in-process mirror ever rendered them. isTrail so the count-0 emission
        // survives EffectEmitter.Emit's point-count guard and the velocity (end point) is networked; the client
        // routes the "*_beam" name to BeamRenderer (a straight cylinder), tinted by the decoded wire color.
        Effects.Register("HEAL_BEAM", "heal_beam", isTrail: true);
        Effects.Register("DAMAGE_BEAM", "damage_beam", isTrail: true);

        // ---- Sandbox material-impact bursts (QC g_sandbox Send_Effect_("impact_"+material, ...)) ----
        // SandboxMutator emits these by effectinfo name (EmitByEffectInfoName, the port's Send_Effect_). The
        // shipped effectinfo.txt defines impact blocks for exactly these four materials; in Base a Send_Effect_
        // for any OTHER material falls through to __pointparticles(_particleeffectnum(name)) which returns -1
        // (no particles), so registering only these four is faithful — an unknown material correctly renders
        // nothing on both ends. Registered as point Effects so the burst networks by id (a null-Effect by-name
        // request is dropped by ServerNet.WriteEffect, so it would otherwise render only on the listen server).
        Effects.Register("IMPACT_METAL", "impact_metal");
        Effects.Register("IMPACT_STONE", "impact_stone");
        Effects.Register("IMPACT_WOOD", "impact_wood");
        Effects.Register("IMPACT_FLESH", "impact_flesh");

        // Deterministic CL/SV ordering + ids (mirrors QC Registry sort at boot).
        Effects.Sort();
    }
}
