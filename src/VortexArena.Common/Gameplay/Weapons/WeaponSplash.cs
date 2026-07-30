using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Shared splash-damage helper for the projectile weapons — the Godot-free essence of
/// RadiusDamageForSource (server/g_damage.qc). Everything within <paramref name="radius"/> of
/// <paramref name="center"/> takes damage scaled linearly from the core <c>damage</c> at the center down
/// to <c>edgeDamage</c> at the rim, plus an outward knockback impulse scaled the same way, all routed
/// through the real damage pipeline via <see cref="WeaponFiring.ApplyDamage"/>.
///
/// This is a NEW shared helper (it deliberately does NOT touch WeaponFiring.cs) factored out so Mortar,
/// Devastator, Crylink, Electro and Hagar share one faithful blast model instead of each copying the
/// Blaster's private version. The Blaster keeps its own equivalent private method; behaviour matches.
///
/// Deferred vs QC (same gaps the Blaster flags): the energy-conserving knockback cubic (lives in the damage
/// pipeline, DamageSystem.cs), self-damage radius scaling, and Damage_DamageInfo blast networking.
/// </summary>
public static class WeaponSplash
{
    /// <summary>QC <c>MAX_DAMAGEEXTRARADIUS</c> (server/damage.qh:127): the broadphase pad QC adds to the
    /// findradius search so the precise per-target nearest-point test is what actually decides hits.</summary>
    private const float MaxDamageExtraRadius = 16f;

    /// <summary>
    /// Full headless port of RadiusDamageForSource (server/damage.qc). Everything within
    /// <paramref name="radius"/> of <paramref name="center"/> takes damage interpolated from
    /// <paramref name="damage"/> at the center to <paramref name="edgeDamage"/> at the rim, plus an outward
    /// knockback impulse. Faithful to QC:
    /// <list type="bullet">
    /// <item>knockback direction points from the blast toward the victim's bbox/view center (not from the
    /// victim's origin), and its magnitude is <c>(finaldmg / max(core, edge)) * force</c> — i.e. it tracks
    /// the damage falloff exactly, as QC does;</item>
    /// <item><paramref name="forceScale"/> shapes the knockback per axis (QC force_xyzscale, damage.qc:831-836
    /// — a zero component leaves that axis unscaled): the Blaster's force_zscale launch boost rides Z, the
    /// Devastator's force_xyscale rides X/Y;</item>
    /// <item>line-of-sight: if a wall blocks the blast from the victim, damage and force are reduced by the
    /// through-floor factors (QC g_throughfloor_damage / _force) — a single LOS trace stands in for QC's
    /// multi-sample box test;</item>
    /// <item>self-damage scaling (g_balance_selfdamagepercent) is NOT applied here — the damage pipeline
    /// (DamageSystem.Apply) applies it exactly once, as QC's Damage() does;</item>
    /// <item>the direct-hit entity takes the blast without the LOS reduction (QC skips the box test for it).</item>
    /// </list>
    /// The Damage_DamageInfo blast-effect networking is the only deferred piece (client render).
    /// </summary>
    /// <summary>
    /// Returns the total damage dealt to creatures (iscreature targets) — QC RadiusDamageForSource's
    /// <c>total_damage_to_creatures</c> return value (damage.qc:931). Callers that only need the side-effects
    /// (damage + force application) can discard the return value; callers that gate behaviour on whether any
    /// creature was actually hit (e.g. W_Crylink_Touch's chain-detonate gate, crylink.qc:249) must capture it.
    /// </summary>
    public static float RadiusDamage(Entity inflictor, Vector3 center, float damage, float edgeDamage,
        float radius, Entity? attacker, int deathType, float force = 0f, Vector3? forceScale = null,
        Entity? directHit = null, Weapon? accuracyWeapon = null, string? deathTag = null)
    {
        if (Api.Services is null || radius <= 0f) return 0f;
        Entity src = attacker ?? inflictor;

        // balance-xonotic.cfg:200-201 — both 0.75 (the old 0.5/0.7 fallbacks matched neither balance nor QC).
        float throughFloorDmg = Cvar("g_throughfloor_damage", 0.75f);
        float throughFloorForce = Cvar("g_throughfloor_force", 0.75f);

        // [T57] QC stat_damagedone (damage.qc:909-914): the splash accuracy-hit tally. The weapon explosion
        // call sites pass their Weapon as accuracyWeapon (QC derives it via DEATH_WEAPONOF(deathtype); the
        // port's int deathType can't carry vehicle/special sources apart, so the credit is explicit —
        // null (vehicles/monsters/breakables) means no credit, matching QC's DEATH_ISSPECIAL gate).
        float statDamageDone = 0f;
        // QC RadiusDamageForSource total_damage_to_creatures (damage.qc:911): damage dealt to ANY creature
        // (iscreature), regardless of team. This is the value RadiusDamage RETURNS in QC — used by
        // W_Crylink_Touch to gate chain-detonation on "did this blast actually hurt anything alive?".
        float totalDamageToCreatures = 0f;

        // QC searches a padded radius (rad + MAX_DAMAGEEXTRARADIUS, damage.qc:746) so the per-target
        // nearest-point check below — not the broadphase pre-filter — is the binding constraint.
        // [T45] WARPZONE-AWARE find (QC RadiusDamageForSource calls WarpZone_FindRadius): the result reaches
        // victims through linked portals, each tagged with the blast origin in ITS OWN frame (hit.LocalBlastOrigin)
        // so distance/falloff/LOS for a far-side victim are measured from the portal-shifted blast point. With no
        // warpzones in the world this is exactly the plain findradius with identity transforms (every hit's
        // LocalBlastOrigin == center). Snapshot into a method-LOCAL list (not a shared static): ApplyDamage below
        // can spawn/free entities and re-enter RadiusDamage (damage → death → secondary explosion), so the buffer
        // must be per-call; iterating the live grid result would be unsafe (the loop relinks the world).
        List<WarpzoneRadiusHit> targets = new();
        WarpzoneRadiusQuery.FindRadiusWarpzone(WarpzoneTrace.AmbientManager, center,
            radius + MaxDamageExtraRadius, targets);
        for (int i = 0; i < targets.Count; i++)
        {
            WarpzoneRadiusHit hit = targets[i];
            Entity e = hit.Entity;
            if (e.TakeDamage == DamageMode.No) continue;

            // The blast origin in THIS victim's frame (QC WarpZone_findradius_findorigin): the original center for
            // a same-room victim, or the portal-shifted origin for one reached through a warpzone. All of this
            // victim's distance/force/LOS math below is measured from blastOrg, in the victim's own world frame.
            Vector3 blastOrg = hit.LocalBlastOrigin;

            // QC RadiusDamageForSource measures distance to the NEAREST POINT on the target's bbox (so a
            // point-blank / direct hit takes full core damage — the bbox-center metric undershot close range).
            Vector3 targetCenter = e.Origin + (e.Mins + e.Maxs) * 0.5f;
            Vector3 nearest = Vector3.Clamp(blastOrg, e.Origin + e.Mins, e.Origin + e.Maxs);
            float dist = (nearest - blastOrg).Length();
            if (dist > radius) continue;

            float frac = 1f - dist / radius;                 // in [0,1] since dist <= radius
            float finalDmg = damage * frac + edgeDamage * (1f - frac);
            if (finalDmg <= 0f) continue;

            // Knockback reference point (QC's RadiusDamageForSource `center`, gated by g_player_damageplayercenter):
            // for a SELF-hit (blaster/rocket jump) the push is aimed from the blast toward the attacker's EYE,
            // not the bbox center. The eye sits higher above a floor blast, so a shot at your own feet launches
            // you more vertically — QC special-cases targ==attacker to CENTER_OR_VIEWOFS (origin+view_ofs). Other
            // players use the bbox center (default damageplayercenter 1); with it 0, all players use the eye.
            // (QC's extra movedir.z shot-origin nudge for self is deferred — a sub-unit refinement.)
            Vector3 forceRef = targetCenter;
            if ((e.Flags & EntFlags.Client) != 0)
            {
                bool useBoxCenterForOthers = Cvar("g_player_damageplayercenter", 1f) != 0f;
                if (!useBoxCenterForOthers || ReferenceEquals(e, src))
                    forceRef = e.Origin + e.ViewOfs;
            }

            // Knockback toward the reference point, magnitude scaled by the damage falloff (QC formula).
            Vector3 forceVec = Vector3.Zero;
            float denom = MathF.Max(damage, edgeDamage);
            if (force != 0f && denom > 0f)
            {
                Vector3 dirDelta = forceRef - blastOrg;
                float dirLen = dirDelta.Length();
                Vector3 dir = dirLen > 0f ? dirDelta / dirLen : Vector3.UnitZ;
                forceVec = dir * ((finalDmg / denom) * force);
                if (forceScale is { } s)
                {
                    // QC damage.qc:831-836: each axis only scales when its component is non-zero.
                    if (s.X != 0f) forceVec.X *= s.X;
                    if (s.Y != 0f) forceVec.Y *= s.Y;
                    if (s.Z != 0f) forceVec.Z *= s.Z;
                }
            }

            // Line of sight (QC damage.qc:838-905): the direct-hit target is always fully hit; for others,
            // trace from the blast to MULTIPLE points on the victim's box — the NEAREST point first (QC seeds
            // the loop with its running `nearest`), then uniformly random box points — and BLEND damage/force
            // by the visible fraction: factor = throughfloor + (1 - throughfloor) * hitratio. The sample count
            // is adaptive from the allowed result stddev (xonotic-server.cfg:309-314: max stddev 2 dmg /
            // 10 force, steps bounded 1..100 player / 1..10 other), so a big rocket blast on a player samples
            // ~75 points while a grazing hit samples once. This replaces the previous single-ray-to-CENTER
            // BINARY check, whose full-force-or-0.7x coin flip whenever the one ray nicked a stair lip / ramp
            // edge made blaster-jump knockback inconsistent (the splash-duplication bug used to mask it).
            Vector3 hitLoc = nearest; // QC hitloc: the nearest box point (mean of visible samples below)
            if (!ReferenceEquals(e, directHit))
            {
                // QC: n = (1 / (2 * max stddev))^2 -> total = 0.25 * (max(mininv_f, mininv_d))^2
                float mininvD = finalDmg * (1f - throughFloorDmg) / Cvar("g_throughfloor_damage_max_stddev", 2f);
                float mininvF = forceVec.Length() * (1f - throughFloorForce) / Cvar("g_throughfloor_force_max_stddev", 10f);
                bool isPlayer = (e.Flags & EntFlags.Client) != 0;
                float steps = 0.25f * MathF.Pow(MathF.Max(mininvD, mininvF), 2f);
                float minSteps = Cvar(isPlayer ? "g_throughfloor_min_steps_player" : "g_throughfloor_min_steps_other", 1f);
                float maxSteps = Cvar(isPlayer ? "g_throughfloor_max_steps_player" : "g_throughfloor_max_steps_other", isPlayer ? 100f : 10f);
                int total = (int)MathF.Ceiling(QMath.Clamp(steps, minSteps, maxSteps));

                int hits = 0;
                Vector3 sample = nearest;
                Vector3 visibleAccum = Vector3.Zero;
                for (int c = 0; c < total; c++)
                {
                    // [T45] LOS from the (portal-shifted) blast origin, warpzone-aware (QC RadiusDamageForSource
                    // traces with WarpZone_TraceLine). For a same-room victim this is the plain LOS trace; for a
                    // far-side victim blastOrg already sits in the victim's frame so the trace stays local.
                    TraceResult los = Api.Trace.TraceLineWarpzone(blastOrg, sample,
                        MoveFilter.NoMonsters, inflictor).Trace;
                    if (los.Fraction >= 1f || ReferenceEquals(los.Ent, e))
                    {
                        ++hits;
                        visibleAccum += sample;
                    }
                    // next sample: a uniform random point inside the victim's bbox (QC random() -> Prandom,
                    // the deterministic PRNG every other weapon-spread call site uses, ADR-0010).
                    sample = new Vector3(
                        e.Origin.X + e.Mins.X + Prandom.Float() * (e.Maxs.X - e.Mins.X),
                        e.Origin.Y + e.Mins.Y + Prandom.Float() * (e.Maxs.Y - e.Mins.Y),
                        e.Origin.Z + e.Mins.Z + Prandom.Float() * (e.Maxs.Z - e.Mins.Z));
                }
                float hitRatio = (float)hits / total;
                finalDmg *= QMath.Clamp(throughFloorDmg + (1f - throughFloorDmg) * hitRatio, 0f, 1f);
                forceVec *= QMath.Clamp(throughFloorForce + (1f - throughFloorForce) * hitRatio, 0f, 1f);
                if (hits > 0)
                    hitLoc = visibleAccum / hits; // QC: hitloc = the mean visible sample
            }

            // QC damage.qc:909-914: if (targ.iscreature) { total_damage_to_creatures += finaldmg; if
            // (accuracy_isgooddamage(attacker, targ)) stat_damagedone += finaldmg; } — BOTH tallies are gated
            // on .iscreature (players/monsters, and vehicles/turrets in QC). The port mirrors this with
            // MapMover.IsCreature (Client|Monster flags — the faithful equivalent in this entity model, where
            // vehicles/turrets carry neither). This MUST NOT be the loop's broad TakeDamage != No filter: a
            // damageable NON-creature (another projectile/spike/grenade, a func_breakable, a door) passes
            // TakeDamage != No but is not iscreature, so it must NOT contribute to total_damage_to_creatures —
            // otherwise the crylink chain-detonate gate (crylink.qc:249) would fire on a spike that only
            // splashed a wall-object with no living target nearby, which is exactly the divergence this return
            // value exists to close.
            if (MapMover.IsCreature(e))
            {
                totalDamageToCreatures += finalDmg;
                // [T57] accuracy tally BEFORE Damage (QC damage.qc:913-914, isgooddamage gate).
                if (WeaponAccuracyEvents.IsGoodDamage(src, e))
                    statDamageDone += finalDmg;
            }

            // NOTE: self-damage scaling (g_balance_selfdamagepercent) is applied ONCE, authoritatively, inside
            // DamageSystem.Apply (QC damage.qc:614-615 — only Damage() scales it; RadiusDamageForSource does
            // NOT). Scaling it here too double-applied it (0.65^2 ≈ 0.42×), making rocket/blaster/electro-jumps
            // ~35% too cheap. The pipeline is now the single source of truth (DMG1).
            // QC passes `nearest` (the nearest/mean-visible box point) as hitloc, not the box center.
            //
            // [DMG-SPLASH] HITTYPE_SPLASH (QC damage.qc:917-920): every victim that is NOT the directHit
            // entity takes the blast tagged with HITTYPE_SPLASH so the kill-message/effect layer can tell a
            // splash kill from a direct hit; the direct-hit entity (and special / non-weapon deaths) keep the
            // plain tag. DamageSystem.SplashDeathType is the single Wave-1 seam that ORs the bit (it no-ops for
            // DEATH_ISSPECIAL deathtypes, matching QC's `|| DEATH_ISSPECIAL(deathtype)` exemption) and is
            // idempotent. We resolve the int weapon-id path to its string deathtype tag HERE (the same
            // id->NetName mapping WeaponFiring.ApplyDamage does) so the splash bit can be set on it too —
            // ApplyDamage(int) can only emit the plain weapon tag, so an indirect int-path hit must go through
            // the string pipeline to carry the bit. Direct hits keep the existing fast paths unchanged.
            bool isIndirect = !ReferenceEquals(e, directHit);

            // A non-null deathTag is a SPECIAL deathtype string (monster/turret/vehicle blast) the int
            // weapon-id path cannot encode (it maps to a weapon NetName or Generic); route it straight to
            // the pipeline so the obituary picks the monster/turret/vehicle line. Otherwise keep the legacy
            // int weapon-id path (ApplyDamage maps the id to the weapon's deathtype tag).
            if (deathTag is not null)
            {
                // SplashDeathType is a no-op for specials, so the monster/turret/vehicle tag is preserved
                // exactly as QC's DEATH_ISSPECIAL branch keeps the plain deathtype.
                string tag = isIndirect ? Damage.DamageSystem.SplashDeathType(deathTag) : deathTag;
                Damage.Combat.Damage(e, inflictor ?? src, src, finalDmg, tag, hitLoc, forceVec);
            }
            else if (isIndirect)
            {
                // Indirect weapon-blast victim: resolve the int weapon id to its NetName tag (mirroring
                // WeaponFiring.ApplyDamage) and OR HITTYPE_SPLASH on, then route through the string pipeline.
                string weaponTag = deathType > 0 && deathType < Registry<Weapon>.Count
                    ? Damage.DeathTypes.FromWeapon(Registry<Weapon>.ById(deathType).NetName)
                    : Damage.DeathTypes.Generic;
                weaponTag = Damage.DamageSystem.SplashDeathType(weaponTag);
                Damage.Combat.Damage(e, inflictor ?? src, src, finalDmg, weaponTag, hitLoc, forceVec);
            }
            else
            {
                // Direct-hit victim: keep the plain weapon deathtype via the int fast path (no splash bit).
                WeaponFiring.ApplyDamage(e, src, finalDmg, deathType, inflictor: inflictor, force: forceVec,
                    hitLoc: hitLoc);
            }
        }

        // [T57] ONE hit credit per blast, capped at one blast's max damage (QC damage.qc:928-929:
        // accuracy_add(attacker, DEATH_WEAPONOF(dt), 0, min(max(coredamage, edgedamage), stat_damagedone), 0)).
        if (accuracyWeapon is not null)
            WeaponAccuracyEvents.Hit(src, accuracyWeapon, MathF.Min(MathF.Max(damage, edgeDamage), statDamageDone));

        // QC RadiusDamageForSource returns total_damage_to_creatures (damage.qc:931). Callers that need to gate
        // on whether the blast actually hurt any living entity (e.g. W_Crylink_Touch chain-detonate at
        // crylink.qc:249: `if (totaldamage && ...)`) capture this; others discard it.
        return totalDamageToCreatures;
    }

    private static float Cvar(string name, float fallback)
    {
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }

    /// <summary>
    /// Play a weapon's impact/explosion sound at the blast — DP's per-weapon CSQC <c>wr_impacteffect</c>
    /// <c>sound(actor, CH_SHOTS, …)</c>. The port emits explosions SERVER-side (next to <c>EffectEmitter.Emit</c>),
    /// so this networks the cue to every client. <c>CH_SHOTS</c> (= <see cref="SoundChannel.ShotsAuto"/>) is an
    /// auto channel, so simultaneous blasts stack instead of cutting each other off; volume/attenuation default
    /// to VOL_BASE / ATTN_NORM. Use the <paramref name="emitter"/> overload for a projectile (the entity is at
    /// the blast point); use <see cref="ImpactSoundAt"/> for a hitscan trace endpoint (no entity there).
    /// No-op without services or with an empty sample.
    /// </summary>
    public static void ImpactSound(Entity emitter, string sample,
        float volume = SoundLevels.VolBase, float attenuation = SoundLevels.AttenNorm)
    {
        if (Api.Services is not null && !string.IsNullOrEmpty(sample))
            Api.Sound.Play(emitter, SoundChannel.ShotsAuto, sample, volume, attenuation);
    }

    /// <summary>Hitscan-impact variant of <see cref="ImpactSound"/>: play the impact sound at a world POINT (the
    /// trace endpoint, which has no entity) — fire-and-forget, staying put at the impact (DP plays these in
    /// wr_impacteffect at <c>w_org</c>).</summary>
    public static void ImpactSoundAt(Vector3 point, string sample,
        float volume = SoundLevels.VolBase, float attenuation = SoundLevels.AttenNorm)
    {
        if (Api.Services is not null && !string.IsNullOrEmpty(sample))
            Api.Sound.PlayAt(point, SoundChannel.ShotsAuto, sample, volume, attenuation);
    }
}
