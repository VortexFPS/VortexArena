using System.Collections.Generic;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Math;
using VortexArena.Common.Services;

namespace VortexArena.Common.Gameplay;

/// <summary>Result of <see cref="WeaponFiring.SetupShot"/> — the C# successor to QC's w_shotorg / w_shotdir / w_shotend globals.</summary>
public readonly struct ShotInfo
{
    /// <summary>Muzzle origin the projectile/bullet leaves from (QC w_shotorg).</summary>
    public readonly Vector3 Origin;
    /// <summary>Normalized aim direction (QC w_shotdir).</summary>
    public readonly Vector3 Dir;
    /// <summary>Trueaim end point the shot is aimed at (QC w_shotend).</summary>
    public readonly Vector3 End;

    public ShotInfo(Vector3 origin, Vector3 dir, Vector3 end)
    {
        Origin = origin;
        Dir = dir;
        End = end;
    }
}

/// <summary>
/// Shared weapon firing helpers — the Godot-free core of the QC firing pipeline
/// (server/weapons/tracing.qc W_SetupShot*, FireRailgunBullet, fireBullet; common/weapons/calculations.qc).
///
/// These operate purely on <see cref="Entity"/> + the engine-services facade, so they unit-test headless.
/// Spread (deterministic PRNG), solid-penetration multi-hit, exponential distance falloff, knockback force,
/// railgun multi-pierce and the per-target headshot bbox (<see cref="Headshot"/>) are all implemented here.
/// Antilag take-back brackets the SetupShot trueaim/muzzle traces and the FireBullet/FireRailgunBullet damage
/// traces (g_antilag==2, via <see cref="LagComp"/>). Warpzone transforms and accuracy hitplot bookkeeping are the
/// remaining online/render-only gaps.
/// </summary>
public static class WeaponFiring
{
    /// <summary>QC <c>max_shot_distance</c> default (constants.qh) — the fallback trueaim trace range used until
    /// a map is loaded and <see cref="CurrentMaxShotDistance"/> is recomputed from the world bounds.</summary>
    public const float MaxShotDistance = 32768f;

    /// <summary>
    /// QC per-map <c>max_shot_distance</c> (world.qc:731 <c>min(230000, vlen(world.maxs - world.mins))</c>): the
    /// global hitscan/trueaim/turret trace range, recomputed each map from the world diagonal at boot (see
    /// <c>GameWorld.Boot</c>). Sentinel ranges (a caller passing the default) resolve to this; explicit
    /// "shoot to the edge of the world" sites read it directly. Defaults to the <see cref="MaxShotDistance"/>
    /// constant so headless callers before any map load behave exactly as before.
    /// </summary>
    public static float CurrentMaxShotDistance = MaxShotDistance;

    /// <summary>
    /// QC <c>max_shot_distance = min(230000, vlen(world.maxs - world.mins))</c> (world.qc:731): set the per-map
    /// global trace range from the world bounding box. NetRadiant caps a map side at 131072qu (≈227023qu corner
    /// to corner); the 230000 ceiling avoids float-precision issues on oversized maps. Called once per map at
    /// boot with the world's collision min/max.
    /// </summary>
    public static void SetMaxShotDistanceFromWorldBounds(Vector3 worldMins, Vector3 worldMaxs)
        => CurrentMaxShotDistance = System.Math.Min(230000f, QMath.VLen(worldMaxs - worldMins));

    /// <summary>
    /// Generic gun-muzzle offset in the actor's view frame, in Quake model-local coords (X = forward, Y = +left,
    /// Z = up) — the fallback for QC's per-model <c>movedir</c> (the weapon model's <c>tag_shot</c>, set in
    /// <c>CL_WeaponEntity_SetModel</c>) when a weapon has no registered per-model offset (a pure dedicated server
    /// with no models, or a weapon whose v_ model lacks a shot tag). Used by <see cref="SetupShot"/> to spawn the
    /// shot at the weapon rather than the camera. Hit-reg is unaffected (the shot dir is re-aimed at the trueaim
    /// point). Per-weapon offsets are registered via <see cref="RegisterMuzzleOffset"/> at weapon-model load.
    /// </summary>
    public static readonly Vector3 DefaultMuzzleOffset = new(12f, 0f, -8f);

    /// <summary>
    /// Per-weapon muzzle offset registry — the C# successor to QC's <c>ent.(weaponentity).movedir</c>, keyed by
    /// the weapon's RegistryId and holding the <c>tag_shot</c> position in MODEL-LOCAL Quake coords
    /// (X = forward, Y = +left, Z = up), exactly the value Base reads from the v_ model. The muzzle tag IS a
    /// parsed-model datum (MD3 tag origin / IQM-DPM bind bone, extracted by <c>VortexArena.Formats.MuzzleTag</c>),
    /// just not something Common can read itself (it has no model loader), so it is populated on the Godot side at
    /// weapon-model load and read here by <see cref="SetupShot"/> via the firing actor's
    /// <see cref="Entity.ActiveWeaponId"/>. Empty by default, so an un-registered weapon falls back to
    /// <see cref="DefaultMuzzleOffset"/> and today's behavior is kept.
    /// </summary>
    private static readonly Dictionary<int, Vector3> MuzzleOffsets = new();

    /// <summary>
    /// Register (or replace) the model-local <c>tag_shot</c> muzzle offset for weapon <paramref name="weaponId"/>
    /// (its RegistryId). Called by the host when a weapon's view model is loaded — mirrors Base setting
    /// <c>movedir</c> in <c>CL_WeaponEntity_SetModel</c>. The offset is in Quake model-local coords
    /// (X = forward, Y = +left, Z = up), fed unchanged into <see cref="SetupShot"/>'s view-basis formula.
    /// </summary>
    public static void RegisterMuzzleOffset(int weaponId, Vector3 modelLocalShotOffset)
    {
        if (weaponId < 0) return;
        MuzzleOffsets[weaponId] = modelLocalShotOffset;
    }

    /// <summary>Clear all registered per-weapon muzzle offsets (test isolation / a fresh content load).</summary>
    public static void ClearMuzzleOffsets() => MuzzleOffsets.Clear();

    /// <summary>True if weapon <paramref name="weaponId"/> has a registered per-model muzzle offset.</summary>
    public static bool TryGetMuzzleOffset(int weaponId, out Vector3 offset)
        => MuzzleOffsets.TryGetValue(weaponId, out offset);

    /// <summary>
    /// The muzzle offset <see cref="SetupShot"/> uses for <paramref name="actor"/>: the registered per-model
    /// <c>tag_shot</c> of the actor's active weapon (<see cref="Entity.ActiveWeaponId"/>) if any, else the
    /// generic <see cref="DefaultMuzzleOffset"/>. Model-local Quake coords (X = forward, Y = +left, Z = up).
    /// </summary>
    public static Vector3 MuzzleOffsetFor(Entity actor)
        => actor is not null && MuzzleOffsets.TryGetValue(actor.ActiveWeaponId, out Vector3 o)
            ? o : DefaultMuzzleOffset;

    /// <summary>QC Q3SURFACEFLAG_SKY — a shot hitting this surface stops (no impact/penetration).</summary>
    public const int Q3SurfaceFlagSky = 0x4;

    // QC weapclip surface-flag constants (dpdefs/upstream/csprogsdefs.qc + dpextensions.qc):
    //   Q3SURFACEFLAG_NODRAW  = 128  (0x80)  — nodraw (caulk / weapclip shaders)
    //   Q3SURFACEFLAG_NONSOLID = 16384 (0x4000) — nonsolid (trans)
    //   DPCONTENTS_OPAQUE = 4096 (0x1000) — only fully opaque brushes get this
    //
    // tracing.qc:424-427: a surface with NODRAW && !NONSOLID && !OPAQUE-contents is a weapclip.
    // weapclip shaders (common/weapclip, common/noimpact) are solid+nodraw+trans, so they block bullets
    // unless autocvar_g_ballistics_penetrate_clips (default 1) is nonzero.
    private const int Q3SurfaceFlagNoDraw  = 0x80;   // Q3SURFACEFLAG_NODRAW
    private const int Q3SurfaceFlagNonSolid = 0x4000; // Q3SURFACEFLAG_NONSOLID
    private const int DpContentsOpaque = 0x1000;      // DPCONTENTS_OPAQUE

    /// <summary>
    /// Port of W_SetupShot_Dir_ProjectileSize_Range (server/weapons/tracing.qc): from the actor's eye,
    /// trace forward along <paramref name="forward"/> to find the trueaim point, then nudge the muzzle
    /// origin out of any wall and recompute the shot direction toward the aim point.
    ///
    /// For penetrate-walls weapons (rifle/machinegun/okmg) QC deliberately trueaims only against bodies
    /// (DPCONTENTS_BODY|CORPSE), so the centered shot passes through glass/walls to hit a target behind
    /// them instead of stopping on the wall; we approximate that with a NoMonsters body trace when
    /// <paramref name="penetrateWalls"/> is set. At g_antilag==2 the trueaim + muzzle-nudge traces are
    /// bracketed by <see cref="LagComp"/> (QC tracing.qc:46/85/97), so w_shotend/w_shotorg/w_shotdir are
    /// computed against rewound enemy positions. Still deferred vs QC: warpzones, the trueaim minrange clamp,
    /// accuracy/hitplot bookkeeping, and g_antilag modes 1/3.
    /// </summary>
    public static ShotInfo SetupShot(Entity actor, Vector3 forward, Vector3 mins, Vector3 maxs,
        float range = -1f, bool penetrateWalls = false,
        Weapon? wep = null, float maxDamage = 0f, float recoil = 0f)
    {
        // A negative sentinel range means "use the per-map global max_shot_distance" (QC W_SetupShot defaults
        // its range arg to the global max_shot_distance); explicit positive ranges (per-weapon caps) stand.
        if (range < 0f) range = CurrentMaxShotDistance;

        // [T57] track max damage — the accuracy FIRED credit (QC tracing.qc:64-66): each weapon passes the
        // shot's potential damage (its QC W_SetupShot maxdamage arg) so the accuracy% denominator grows.
        // Zero maxDamage (porto/seeker missile) is filtered by accuracy_add's all-zero guard.
        if (wep is not null && (actor.Flags & EntFlags.Client) != 0 && WeaponAccuracyEvents.CanBeGoodDamage(actor))
            WeaponAccuracyEvents.Fired(actor, wep, maxDamage);

        // QC W_SetupShot: `if (!autocvar_g_norecoil) ent.punchangle_x = -recoil;` — the view kicks UP on firing
        // (pitch −recoil°), then decays via PM_check_punch (PlayerPhysics.CheckPunch, −10°/s). Players only (a
        // non-client shooter has no view). The aim/shot direction is NOT punched — only the rendered view is.
        if (recoil != 0f && (actor.Flags & EntFlags.Client) != 0
            && !(Api.Services is not null && Api.Cvars.GetFloat("g_norecoil") != 0f))
            actor.PunchAngle = new Vector3(-recoil, 0f, 0f);

        Vector3 eye = actor.Origin + actor.ViewOfs;
        Vector3 aimEnd = eye + forward * range;

        Vector3 shotEnd;
        Vector3 shotOrg;

        // [sv-antilag.setupshot.trueaim] Antilag bracket around the trueaim + muzzle-nudge traces (QC
        // tracing.qc:46/85/97): at g_antilag==2 Base routes ALL of W_SetupShot's traces through the
        // _antilag variants, so w_shotend/w_shotorg (and hence w_shotdir) are computed against REWOUND
        // enemy positions for every weapon (hitscan AND projectile). LagComp.Begin/End is the shared
        // facade FireBullet/Arc use; it rewinds players+monsters+nades to the shooter's view-time and is a
        // no-op unless a provider is installed and g_antilag==2 with cl_noantilag unset (BeginLagComp),
        // which matches Base's `antilag` flag (false for non-clients, takeback only at mode 2). On a client,
        // in a test, or a bot-only server it stays null and these traces use present positions, exactly as
        // before.
        LagComp.Begin(actor);
        try
        {

        // Trueaim: where does the centered shot actually land? For penetrate-walls weapons QC deliberately
        // trueaims only against bodies (so the shot aims at a target behind glass rather than stopping on the
        // glass); with no body-only trace filter here, we aim straight at the full-range point and let the
        // penetrating fireBullet pass through the wall to reach it.
        if (penetrateWalls)
        {
            shotEnd = aimEnd;
        }
        else
        {
            // [T45] trueaim through warpzones (QC W_SetupShot uses WarpZone_TraceLine): the centered shot may aim
            // at a target seen through a portal. If the aim crossed a portal the endpoint lives in the far frame,
            // so the shot DIRECTION (computed in the firing frame below) keeps the straight aimEnd — the fired
            // bullet/projectile re-warps through the same portal itself. With no portal crossed this is the plain
            // trueaim trace exactly as before.
            WarpzoneTraceResult aim = Api.Trace.TraceLineWarpzone(eye, aimEnd, MoveFilter.NoMonsters, actor);
            shotEnd = aim.ZonesCrossed > 0 ? aimEnd : aim.Trace.EndPos;
        }

        // Nudge the muzzle origin so it never sits inside a wall (QC tracebox of the projectile size).
        TraceResult org = Api.Trace.Trace(eye, mins, maxs, eye, MoveFilter.Normal, actor);
        shotOrg = org.StartSolid ? eye : org.EndPos;

        // Muzzle offset (QC W_SetupShot: ent.(weaponentity).movedir) — slide the origin from the eye to the gun
        // muzzle so the shot visibly leaves the WEAPON, not the camera. Only a player carries a view weapon. The
        // exact per-model offset is the weapon model's tag_shot, available from the parsed model data: the host
        // extracts it at weapon-model load (MuzzleTag/CL_WeaponEntity_SetModel) and registers it per weapon id, so
        // MuzzleOffsetFor returns that movedir; a weapon with no registered tag (dedicated server / tag-less model)
        // falls back to the generic DefaultMuzzleOffset. The offset is in model-local Quake coords (X=fwd, Y=+left,
        // Z=up) — Base's right*(-md.y) handles Quake +y=left — and the shot DIRECTION is recomputed to the trueaim
        // point below, so the round still lands on the crosshair. Tracebox each leg so the muzzle never ends up
        // inside a wall.
        Vector3 muzzle = MuzzleOffsetFor(actor);
        if (muzzle != Vector3.Zero && (actor.Flags & EntFlags.Client) != 0)
        {
            QMath.AngleVectors(actor.Angles, out _, out Vector3 right, out Vector3 up);
            Vector3 dv = right * -muzzle.Y + up * muzzle.Z;                         // sideways + vertical
            shotOrg = Api.Trace.Trace(shotOrg, mins, maxs, shotOrg + dv, MoveFilter.Normal, actor).EndPos;
            Vector3 fwd = QMath.Normalize(forward) * muzzle.X;                      // forward
            shotOrg = Api.Trace.Trace(shotOrg, mins, maxs, shotOrg + fwd, MoveFilter.Normal, actor).EndPos;
        }

        }
        finally
        {
            LagComp.End();
        }

        // QC W_SetupShot (tracing.qc:60-62): un-adjust trueaim if w_shotend is too close — if the centered shot
        // would land within g_trueaim_minrange (default 44) of the eye, ignore the trueaim point and aim straight
        // forward at the minrange distance. Without this, a point-blank trueaim hit (e.g. a wall right in your
        // face) would skew the muzzle-offset shot direction noticeably; clamping keeps it forward.
        float trueaimMinrange = Api.Services is null ? 44f : Api.Cvars.GetFloat("g_trueaim_minrange");
        if (trueaimMinrange > 0f && QMath.VLen(shotEnd - eye) < trueaimMinrange)
            shotEnd = eye + QMath.Normalize(forward) * trueaimMinrange;

        Vector3 shotDir = QMath.Normalize(shotEnd - shotOrg);
        if (shotDir == Vector3.Zero) shotDir = QMath.Normalize(forward);

        return new ShotInfo(shotOrg, shotDir, shotEnd);
    }

    /// <summary>W_SetupShot convenience overload (zero projectile size, v_forward direction).</summary>
    public static ShotInfo SetupShot(Entity actor, Vector3 forward, float range = -1f,
        bool penetrateWalls = false, Weapon? wep = null, float maxDamage = 0f, float recoil = 0f)
        => SetupShot(actor, forward, Vector3.Zero, Vector3.Zero, range, penetrateWalls, wep, maxDamage, recoil);

    /// <summary>
    /// Hitscan bullet (back-compat single-arg overload) — keeps existing call sites working.
    /// Equivalent to <see cref="FireBullet(Entity,Vector3,Vector3,float,float,int,float,float,float,float,float,float,float)"/>
    /// with no spread, no penetration, no falloff, no force.
    /// </summary>
    public static Entity? FireBullet(Entity actor, Vector3 start, Vector3 dir, float range, float damage, int deathType)
        => FireBullet(actor, start, dir, range, damage, deathType, spread: 0f, solidPenetration: 0f);

    /// <summary>
    /// Full port of fireBullet_falloff (server/weapons/tracing.qc): spreads <paramref name="dir"/> with the
    /// hitscan spread style, then traces from <paramref name="start"/> repeatedly, passing THROUGH solids up
    /// to <paramref name="solidPenetration"/> world units (damage attenuating by the penetration fraction)
    /// so a single bullet can hit several entities behind a thin wall. Each victim takes
    /// <paramref name="damage"/> scaled by exponential distance falloff plus a knockback impulse of
    /// <paramref name="force"/> along the bullet. Returns the first damageable entity hit (or null).
    ///
    /// Faithful to QC: spread (W_CalculateSpread hitscan style), solid penetration loop with
    /// <c>damage_fraction = penFrac ^ exponent</c>, exponential falloff for damage + force, double-hit
    /// guard, sky/out-of-world stop, and the head-AABB headshot multiplier (QC tracing.qc:441-445 — when
    /// <paramref name="headshotMultiplier"/> is nonzero and the segment passes through the victim's
    /// <see cref="Headshot"/> box, the running <c>damage</c> is scaled and the shooter gets the
    /// ANNCE_HEADSHOT announce, QC tracing.qc:528-529). Deferred (render/online): antilag takeback,
    /// warpzones, accuracy bookkeeping.
    ///
    /// <para><b>Tracer</b> (W1-weaponfire-fx seam): pass <paramref name="tracerEffect"/> — the weapon's bullet
    /// trail effect name (QC <c>tracer_effect</c>, e.g. "RIFLE"/"RIFLE_WEAK"/"TR_NEXUIZPLASMA") — to draw the
    /// signature tracer. Faithful to QC's <c>fireBullet_trace_callback</c> + the per-penetration
    /// <c>trailparticles</c>: a trail is swept from the segment start to each hit, but only for segments longer
    /// than QC's threshold (16u for the open-air leg, 4u for an in-solid leg through a player) so a point-blank
    /// shot doesn't spam a zero-length trail. null (the default) keeps the old no-tracer behavior.</para>
    /// </summary>
    public static Entity? FireBullet(Entity actor, Vector3 start, Vector3 dir, float range, float damage,
        int deathType, float spread, float solidPenetration,
        float falloffHalflife = 0f, float falloffMinDist = 0f, float falloffMaxDist = 0f,
        float force = 0f, float falloffForceHalflife = 0f, float headshotMultiplier = 0f,
        string? tracerEffect = null, string? deathTag = null)
    {
        // QC fireBullet's `if (snd != SND_Null) { sound(...); W_PlayStrengthSound(ent); }` (tracing.qc:161-165):
        // every bullet shot offers the Strength-fire sound to the powerups mutator (anti-spammed there, so the
        // per-pellet calls in a shotgun blast collapse to one cue).
        MutatorHooks.FireWPlayStrengthSound(actor);

        LagComp.Begin(actor); // rewind other players to the shooter's view-time for fair hit-reg (antilag.qc)
        try
        {
        // QC fireBullet passes autocvar_g_hitscan_spread_style (tracing.qc:370); xonotic default is 4 (gauss-2D
        // on the aim plane), a denser-centre, plane-constrained scatter — NOT the style-0 uniform sphere.
        dir = CalculateSpread(QMath.Normalize(dir), spread, mustNormalize: true, HitscanSpreadStyle);
        Vector3 end = start + dir * range;

        // [W1-weaponfire-fx] resolve the bullet tracer trail effect once (QC fireBullet's tracer_effect arg).
        Effect? tracer = string.IsNullOrEmpty(tracerEffect) ? null : Effects.ByName(tracerEffect);

        float solidPenetrationFraction = 1f;
        float damageFraction = 1f;
        float totalDamage = 0f; // QC total_damage — the running ballistic hit-credit cap (tracing.qc:378)
        Entity? lastHit = null;
        Entity? firstHit = null;
        bool headshot = false; // QC fireBullet_falloff: one of the hit targets was a headshot
        Vector3 cur = start;

        // QC g_ballistics_solidpenetration_exponent (xonotic-server.cfg:474 default 1) and
        // g_ballistics_mindistance (xonotic-server.cfg:470 default 2): the penetration exponent and the
        // minimum solid-thickness a bullet must clear to keep going. Read live so a server cvar tweak
        // takes; the Base xonotic defaults (1 / 2) hold for headless tests with no cvar service.
        float ballisticsExponent = Api.Services is null ? 1f
            : (Api.Cvars.GetFloat("g_ballistics_solidpenetration_exponent") is var be && be > 0f ? be : 1f);
        float ballisticsMinDistance = Api.Services is null ? 2f
            : (Api.Cvars.GetFloat("g_ballistics_mindistance") is var bd && bd > 0f ? bd : 2f);

        for (int guard = 0; guard < 32; ++guard)
        {
            // [T45] warpzone-aware sweep (QC fireBullet uses WarpZone_TraceLine): the segment crosses any linked
            // portals, so a bullet fired at a portal mouth continues out of the linked portal and hits a far-side
            // target. On a non-warpzone map this is exactly the plain trace. After a crossing, the running aim
            // direction and the segment end are rotated into the far-side frame so the penetration loop continues
            // straight on the far side (the accumulated transform is the C# WarpZone_trace_transform).
            Vector3 segStart = cur; // this segment's start, for the tracer trail (QC fireBullet_trace_callback)
            WarpzoneTraceResult wzr = Api.Trace.TraceLineWarpzone(cur, end, MoveFilter.Normal, actor);
            TraceResult tr = wzr.Trace;
            cur = tr.EndPos;
            Entity? hit = tr.Ent;
            if (wzr.ZonesCrossed > 0)
            {
                // carry the aim direction + the remaining segment end into the frame the trace ended in.
                dir = QMath.Normalize(wzr.Transform.TransformDirection(dir));
                end = wzr.Transform.TransformPoint(end);
            }

            // [W1-weaponfire-fx] open-air tracer trail (QC fireBullet_trace_callback): sweep the trail from the
            // segment start to the impact, but only when the segment is long enough (>16u) so a point-blank shot
            // doesn't draw a degenerate zero-length tracer. A portal crossing draws TWO correct segments — the
            // near side up to the portal, the far side from its exit (QC's per-sub-trace callback does the same
            // naturally) — instead of one line whose endpoints live in different frames.
            if (tracer is not null)
            {
                if (wzr.ZonesCrossed > 0)
                {
                    if ((wzr.FirstCrossPoint - segStart).Length() > 16f)
                        EffectEmitter.EmitTrail(tracer, segStart, wzr.FirstCrossPoint);
                    if ((cur - wzr.FirstExitPoint).Length() > 16f)
                        EffectEmitter.EmitTrail(tracer, wzr.FirstExitPoint, cur);
                }
                else if ((cur - segStart).Length() > 16f)
                {
                    EffectEmitter.EmitTrail(tracer, segStart, cur);
                }
            }

            if (tr.Fraction >= 1f) break;                          // hit nothing -> done
            if ((tr.DpHitQ3SurfaceFlags & Q3SurfaceFlagSky) != 0) break; // sky stops the bullet

            // QC tracing.qc:424-481 weapclip check: a surface that is NODRAW && !NONSOLID && !OPAQUE-content
            // is a weapclip (common/weapclip or common/noimpact shader — solid+nodraw+trans, used to clip shots
            // without a visible brush). The bullet passes through unless g_ballistics_penetrate_clips == 0.
            // Default is 1 (penetrates), so weapon clips are transparent to bullets by default.
            bool isWeapclip = (tr.DpHitQ3SurfaceFlags & Q3SurfaceFlagNoDraw) != 0
                           && (tr.DpHitQ3SurfaceFlags & Q3SurfaceFlagNonSolid) == 0
                           && (tr.DpHitContents & DpContentsOpaque) == 0;

            // Avoid hitting the same entity twice (engine re-hit) and self-damage.
            if (hit is not null && !ReferenceEquals(hit, actor) && !ReferenceEquals(hit, lastHit)
                && hit.TakeDamage != DamageMode.No)
            {
                lastHit = hit;
                firstHit ??= hit;

                // Head-AABB headshot (QC tracing.qc:441-445): scale the RUNNING damage so on a
                // multi-penetration shot the boosted damage carries to subsequent victims, faithful to QC
                // mutating its `damage` local. `cur` is this segment's start (== QC's post-trace `start`).
                if (headshotMultiplier != 0f && Headshot(hit, actor, cur, end))
                {
                    damage *= headshotMultiplier;
                    headshot = true;
                }

                // [T57] capture the accuracy gate BEFORE Damage (QC tracing.qc:446) so the kill shot counts.
                bool goodDamage = WeaponAccuracyEvents.IsGoodDamage(actor, hit);

                float dealt = damage * damageFraction;
                Vector3 dealtForce = dir * (force * damageFraction);
                if (falloffHalflife != 0f || falloffForceHalflife != 0f)
                {
                    float dist = (hit.Origin - actor.Origin).Length();
                    if (falloffHalflife != 0f)
                        dealt *= ExponentialFalloff(falloffMinDist, falloffMaxDist, falloffHalflife, dist);
                    if (falloffForceHalflife != 0f)
                        dealtForce *= ExponentialFalloff(falloffMinDist, falloffMaxDist, falloffForceHalflife, dist);
                }
                ApplyDamage(hit, actor, dealt, deathType, inflictor: actor, force: dealtForce, hitLoc: cur, deathTag: deathTag);

                // [T57] ballistic hit credit (QC tracing.qc:470-477): per-hit credit is the PENETRATION-scaled
                // damage WITHOUT the distance falloff, capped so a multi-target bullet never exceeds 100%.
                if (goodDamage)
                {
                    float addedDamage = MathF.Min(damage - totalDamage, damage * damageFraction);
                    totalDamage += damage * damageFraction;
                    WeaponAccuracyEvents.Hit(actor, Inventory.CurrentWeapon(actor), addedDamage); // add to hit
                }
            }

            // QC tracing.qc:480-481: weapclip stops the bullet when g_ballistics_penetrate_clips == 0.
            // Default 1 means clips are transparent; with 0 the weapclip acts as a hard shot boundary.
            if (isWeapclip)
            {
                float penetrateClips = Api.Services is null ? 1f : Api.Cvars.GetFloat("g_ballistics_penetrate_clips");
                if (penetrateClips == 0f) break;
            }

            // Penetrate the solid we just hit, if allowed. -1 means "no penetration ever".
            if (solidPenetration < 0f) break;

            float maxDist = solidPenetration * solidPenetrationFraction;
            if (maxDist <= ballisticsMinDistance) break;

            // Walk forward through the solid: advance to maxDist past the hit point.
            Vector3 throughStart = cur + dir * 0.03125f;  // tiny nudge into the solid
            Vector3 throughEnd = cur + dir * maxDist;
            TraceResult through = Api.Trace.Trace(throughEnd, Vector3.Zero, Vector3.Zero, throughStart,
                MoveFilter.Normal, actor);
            // through traces backward; if it never left solid (fraction 1) the wall is too thick -> stop.
            if (through.Fraction >= 1f) break;

            float distTaken = MathF.Max(ballisticsMinDistance, (through.EndPos - cur).Length());
            float fractionUsed = distTaken / maxDist;
            solidPenetrationFraction = MathF.Max(0f, solidPenetrationFraction - solidPenetrationFraction * fractionUsed);
            damageFraction = MathF.Pow(solidPenetrationFraction, ballisticsExponent);

            // [W1-weaponfire-fx] in-solid tracer (QC tracing.qc:520): only show the trail when the bullet passes
            // THROUGH a player (a non-BSP entity), and the crossed span is >4u, so the otherwise-invisible
            // penetration leg is visible.
            if (tracer is not null && hit is not null && hit.Solid != Solid.Bsp
                && (through.EndPos - cur).Length() > 4f)
                EffectEmitter.EmitTrail(tracer, cur, through.EndPos);

            cur = through.EndPos;
        }

        // QC tracing.qc:528-529: announce the headshot to the shooter.
        if (headshot && (actor.Flags & EntFlags.Client) != 0)
            NotificationSystem.Announce(actor, "HEADSHOT");

        return firstHit;
        }
        finally { LagComp.End(); }
    }

    /// <summary>
    /// Back-compat railgun overload (single target, no force/falloff) — keeps existing call sites working.
    /// </summary>
    public static Entity? FireRailgunBullet(Entity actor, Vector3 start, Vector3 end, float damage, int deathType)
        => FireRailgunBullet(actor, start, end, damage, deathType, force: 0f);

    /// <summary>Pick a random nexwhoosh sample path (QC <c>SND_NEXWHOOSH_RANDOM()</c>, all.inc:25-27) via the
    /// deterministic <see cref="Prandom"/> so server/predicting clients agree; falls back to the first variant.
    /// Returns a ".wav" path to match the explicit-path <see cref="ISoundService.PlayAt"/> convention.</summary>
    private static string NexWhooshSample()
    {
        GameSound? snd = SoundVariantGroups.NexWhoosh();
        string sample = snd?.Sample ?? "weapons/nexwhoosh1";
        return sample.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? sample : sample + ".wav";
    }

    /// <summary>
    /// Port of FireRailgunBullet (server/weapons/tracing.qc): an instant beam from <paramref name="start"/>
    /// to <paramref name="end"/> that PIERCES every entity along the way (each is made non-solid for the
    /// next trace, like QC's railgunhit list), damaging them all with exponential distance falloff and a
    /// knockback impulse of <paramref name="force"/> along the beam. The beam stops at the first world
    /// surface. Restores solidity afterwards. When <paramref name="headshotNotify"/> is set (QC's
    /// <c>headshot_notify</c> arg — true for the Vaporizer, false for the Vortex) and the beam passes through
    /// a victim's <see cref="Headshot"/> box, the shooter gets the ANNCE_HEADSHOT announce (QC tracing.qc:
    /// 267-268 + 343-344). Deferred: warpzones, antilag, the nearby-whoosh sound, the beam particle.
    /// </summary>
    public static Entity? FireRailgunBullet(Entity actor, Vector3 start, Vector3 end, float damage, int deathType,
        float force, float falloffMinDist = 0f, float falloffMaxDist = 0f,
        float falloffHalflife = 0f, float falloffForceHalflife = 0f, bool headshotNotify = false)
    {
        LagComp.Begin(actor); // rewind other players to the shooter's view-time for fair hit-reg (antilag.qc)
        try
        {
        Vector3 dir = QMath.Normalize(end - start);
        end += dir; // go a little into the wall so the final trace registers it

        // Walk the beam, collecting hits and temporarily removing each so we can reach the next.
        var pierced = new List<(Entity ent, Vector3 loc, float dist, Solid solid)>();
        Entity? first = null;
        bool headshot = false; // QC FireRailgunBullet: one of the pierced targets was a headshot
        Vector3 cur = start;
        Vector3 beamStart = start; // QC headshot uses the original start/end; updated into the far frame on a cross
        Vector3 beamEnd = end;
        for (int guard = 0; guard < 64; ++guard)
        {
            // [T45] warpzone-aware beam sweep (QC FireRailgunBullet uses WarpZone_TraceLine): the beam continues
            // through linked portals so the rail can pierce a target on the far side. Plain trace on a non-warpzone
            // map. After a crossing the beam's running start/end + direction move into the far-side frame so the
            // headshot box test and the next pierce segment stay in one consistent frame.
            WarpzoneTraceResult wzr = Api.Trace.TraceLineWarpzone(cur, end, MoveFilter.Normal, actor);
            TraceResult tr = wzr.Trace;
            Entity? hit = tr.Ent;
            if (wzr.ZonesCrossed > 0)
            {
                dir = QMath.Normalize(wzr.Transform.TransformDirection(dir));
                end = wzr.Transform.TransformPoint(end);
                beamStart = wzr.Transform.TransformPoint(beamStart);
                beamEnd = wzr.Transform.TransformPoint(beamEnd);
            }

            if (hit is null || tr.Fraction >= 1f) break; // hit world / nothing -> stop

            // Head-AABB headshot against the full beam (QC tracing.qc:267-268 uses the original start/end —
            // in the hit's frame after any portal crossing).
            if (headshotNotify && !headshot && Headshot(hit, actor, beamStart, beamEnd))
                headshot = true;

            // Record and make non-solid so the next trace passes through. Distance is measured along the beam in
            // the hit's frame (beamStart), so a far-side pierce gets a sensible falloff distance.
            pierced.Add((hit, tr.EndPos, (tr.EndPos - beamStart).Length(), hit.Solid));
            first ??= hit;

            if (hit.Solid == Solid.Bsp) break; // a world brush ends the beam
            hit.Solid = Solid.Not;
            cur = tr.EndPos;
        }

        // Restore solidity, then apply damage + falloff to everyone we passed through.
        foreach (var p in pierced) p.ent.Solid = p.solid;

        // QC tracing.qc:291-313 — the "nexwhoosh" beam fly-by: every REAL client (not the shooter, not a
        // spectator following the shooter) hears a very loud, fast-falling-off whoosh played at the nearest
        // point on the beam to THEM (VOL_BASEVOICE / ATTEN_IDLE on CH_SHOTS). QC sends it per-recipient via
        // soundtoat(MSG_ONE, …); the Common layer has only the broadcast positional PlayAt (heard by all with
        // 3D falloff), so we emit one positional whoosh per real client AT that client's nearest-beam-point —
        // each client's own 3D attenuation then makes it loud only for the player the rail nearly grazed,
        // reproducing the per-recipient effect. Bots/the shooter are skipped (QC IS_REAL_CLIENT && it != this).
        if (Api.Services is not null)
        {
            // QC `endpoint` / `length` — the beam's effective end is the first world surface (a world-only sweep
            // mirrors QC's loop terminating at SOLID_BSP); the nearest-point clamp uses the start→end segment.
            Vector3 beamEndPos = Api.Trace.Trace(start, Vector3.Zero, Vector3.Zero, end,
                MoveFilter.WorldOnly, actor).EndPos;
            float length = (beamEndPos - start).Length();
            foreach (Entity it in Api.Entities.FindByClass("player"))
            {
                if (it is null || it.IsFreed || ReferenceEquals(it, actor)) continue;
                if ((it.Flags & EntFlags.Client) == 0) continue;     // IS_REAL_CLIENT (no spectator-enemy data here)
                if (it is Player pl && pl.IsBot) continue;            // bots aren't real clients
                // QC: beampos = start + dir * bound(0, (it.origin - start)·dir, length) — nearest point on beam.
                float along = QMath.Clamp(Vector3.Dot(it.Origin - start, dir), 0f, length);
                Vector3 beampos = start + dir * along;
                Api.Sound.PlayAt(beampos, SoundChannel.ShotsAuto, NexWhooshSample(),
                    SoundLevels.VolBaseVoice, SoundLevels.AttenIdle);
            }
        }

        float totalDmg = 0f; // QC totaldmg — the per-target falloff-scaled good damage (tracing.qc:323-324)
        foreach (var p in pierced)
        {
            float foff = (falloffHalflife != 0f)
                ? ExponentialFalloff(falloffMinDist, falloffMaxDist, falloffHalflife, p.dist) : 1f;

            // [T57] gate captured BEFORE Damage (QC tracing.qc:323): credit accumulates per pierced target.
            if (WeaponAccuracyEvents.IsGoodDamage(actor, p.ent))
                totalDmg += damage * foff;

            if (p.ent.TakeDamage == DamageMode.No) continue;
            float ffs = (falloffForceHalflife != 0f)
                ? ExponentialFalloff(falloffMinDist, falloffMaxDist, falloffForceHalflife, p.dist) : 1f;
            ApplyDamage(p.ent, actor, damage * foff, deathType, inflictor: actor,
                force: dir * (force * ffs), hitLoc: p.loc);
        }

        // [T57] ONE hit credit per shot, capped at one shot's damage (QC tracing.qc:346-348) — a beam through
        // two players still credits min(bdamage, totaldmg); the >100% case shows as byte 255 over fired.
        WeaponAccuracyEvents.Hit(actor, Inventory.CurrentWeapon(actor), MathF.Min(damage, totalDmg));

        // QC tracing.qc:343-344: announce the headshot to the shooter.
        if (headshot && (actor.Flags & EntFlags.Client) != 0)
            NotificationSystem.Announce(actor, "HEADSHOT");

        return first;
        }
        finally { LagComp.End(); }
    }

    // =========================================================================
    //  [W1-weaponfire-fx] Shared weapon-fire FX / audio hooks (tracer above; ricochet, casing eject, melee
    //  woosh here). These wire the Common-side emission seams (EffectEmitter / SoundSystem) that already exist
    //  but had no live caller, so every hitscan/melee weapon's signature FX+audio is reachable from one place.
    //  Wave-2 weapon ports call these from their wr_think/impact paths instead of re-deriving the QC formulas.
    // =========================================================================

    /// <summary>Brass-casing kind — QC <c>casingtype</c> (common/effects/qc/casings.qc): the bullet shell vs
    /// the bigger shotgun shell, which differ in model/bounce/lifetime on the client.</summary>
    public enum CasingType
    {
        /// <summary>QC casingtype 0/3 — the small brass bullet casing (machinegun/rifle/okmg).</summary>
        Bullet = 0,
        /// <summary>QC casingtype 1 — the larger shotgun shell.</summary>
        Shell = 1,
    }

    /// <summary>
    /// [T45] Warpzone-aware WORLD-impact resolve for hitscan VISUALS: Base draws rail beams/tracers THROUGH
    /// zones (WarpZone_TrailParticles) and its impact effects land on the far side, because the wr_impacteffect
    /// endpoint comes from the warpzone-following trace. Every hitscan weapon's impact/beam previously used a
    /// PLAIN world trace here, so shooting into a portal visibly "hit the window" (beam stopped + impact burst
    /// + ricochet ON the glass) even while the damage crossed — the "attacks don't work through the warpzone"
    /// report. Impact FX/sound belong at <c>result.Trace.EndPos</c>; a beam draws start→<c>FirstCrossPoint</c>
    /// then <c>FirstExitPoint</c>→EndPos when <c>ZonesCrossed &gt; 0</c> (one straight segment otherwise).
    ///
    /// <para><b>Filter (fixed 2026-07-27, "Vortex shots go through walls").</b> This was <c>MOVE_WORLDONLY</c>,
    /// which clips against the static world and NOTHING else — so every hitscan beam and impact burst passed
    /// straight through brush ENTITIES: <c>func_door</c>, <c>func_wall</c>, <c>func_plat</c>, breakables, every
    /// moving platform. On any map whose walls/doors are brush entities the rail visibly speared through solid
    /// geometry and its impact landed somewhere behind it, while the DAMAGE (which uses the real
    /// <see cref="FireRailgunBullet"/> sweep) correctly stopped at the door — so the shot read as going through
    /// the wall and doing nothing.</para>
    ///
    /// <para><c>MOVE_NOMONSTERS</c> is the faithful filter: it clips world + <c>SOLID_BSP</c> brush entities and
    /// skips players, which is exactly where Base's beam ends. Base's <see cref="FireRailgunBullet"/> loop only
    /// makes <c>SOLID_SLIDEBOX</c> entities non-solid to pierce them, so it terminates on the first world surface
    /// OR brush entity — the same set NOMONSTERS stops at. Bullet weapons get the same correction (their impact
    /// puff/ricochet now lands ON a door instead of behind it).</para>
    /// </summary>
    public static WarpzoneTraceResult HitscanImpactTrace(Entity? actor, Vector3 start, Vector3 end)
        => Api.Trace.TraceLineWarpzone(start, end, MoveFilter.NoMonsters, actor);

    /// <summary>
    /// Shared bullet-impact FX + ricochet audio (W1-weaponfire-fx) — the C# successor to each bullet weapon's
    /// <c>wr_impacteffect</c> (e.g. machinegun.qc:417-421): emit the impact particle puff at the surface and,
    /// unless the shot is silent, play a random ricochet ping (QC <c>SND_RIC_RANDOM</c> on CH_SHOTS). This wires
    /// the previously-dead <see cref="SoundSystem.PlayRic"/> (registered, zero callers) so every hitscan weapon's
    /// signature ricochet finally sounds.
    /// </summary>
    /// <param name="actor">The shooter — the ricochet is emitted on them (QC <c>sound(actor, …)</c>).</param>
    /// <param name="impactPos">The surface impact point (QC <c>w_org</c>, the trace endpoint).</param>
    /// <param name="backoff">The impact surface normal (QC <c>w_backoff</c>) — the puff sprays back along it.</param>
    /// <param name="impactEffect">The weapon's impact effect name (e.g. "MACHINEGUN_IMPACT", "RIFLE_IMPACT").</param>
    /// <param name="silent">QC <c>w_issilent</c> — when set the ricochet sound is suppressed (the puff still plays).</param>
    public static void BulletImpactFx(Entity actor, Vector3 impactPos, Vector3 backoff,
        string impactEffect, bool silent = false)
    {
        // QC wr_impacteffect: org2 = w_org + w_backoff*2; pointparticles(EFFECT, org2, w_backoff*1000, 1).
        if (!string.IsNullOrEmpty(impactEffect))
            EffectEmitter.Emit(impactEffect, impactPos + backoff * 2f, backoff * 1000f);

        // QC shotgun.qc:404-409: if (!w_issilent && time - actor.prevric > 0.25) { if (w_random < 0.05)
        //   sound(actor, CH_SHOTS, SND_RIC_RANDOM(), ...); actor.prevric = time; }
        // So the ricochet ping is throttled to at most once per 0.25s PER actor, and even then only a 5% roll
        // (w_random < 0.05, w_random = prandom()) actually plays it — otherwise a 12-pellet blast would spray a
        // dozen overlapping rics. The prevric window is reset on every gate pass, sound or not (matching Base).
        float now = Api.Services is null ? 0f : Api.Clock.Time;
        if (!silent && actor is not null && now - actor.PrevRic > 0.25f)
        {
            if (Prandom.Float() < 0.05f)
                SoundSystem.PlayRic(actor);
            actor.PrevRic = now;
        }
    }

    /// <summary>
    /// Eject a spent brass casing (W1-weaponfire-fx) — the C# successor to QC <c>SpawnCasing</c>
    /// (common/effects/qc/casings.qc), called by the bullet weapons (machinegun.qc:111, rifle.qc:41,
    /// shotgun.qc:82, okmachinegun/okhmg). Computes the QC eject velocity in the shooter's view frame —
    /// <c>(rand*50+50)·right − (rand*25+25)·forward + (UP−rand*5)·up</c> — and queues a casing emission through
    /// the effect sink so the client spawns the shell (the existing <c>EffectSystem.SpawnCasing</c> consumer).
    /// Uses the deterministic <see cref="Prandom"/> so server and predicting client agree (ADR-0010). No-op
    /// unless the per-weapon <c>g_casings</c> gate passes and the actor is a player (only players have a view
    /// weapon to eject from).
    ///
    /// <para>The gate + up-velocity are PER casingtype, matching Base: the shotgun shell (casingtype 1) ejects
    /// at <c>g_casings &gt;= 1</c> with up <c>(30 − rand*5)</c> (shotgun.qc:78,82), while the bullet casing
    /// (casingtype 3) ejects at <c>g_casings &gt;= 2</c> with up <c>(70 − rand*5)</c>
    /// (machinegun.qc:108,111). g_casings doc (xonotic-server.cfg:231): 0=none, 1=shotgun only, 2=both.</para>
    /// </summary>
    /// <param name="actor">The shooting player (supplies the muzzle origin + view basis).</param>
    /// <param name="muzzle">The casing spawn origin — typically the shot origin (QC weapon <c>spawnorigin</c>).</param>
    /// <param name="type">Bullet shell vs shotgun shell (QC <c>casingtype</c>).</param>
    public static void EjectCasing(Entity actor, Vector3 muzzle, CasingType type = CasingType.Bullet)
    {
        if (actor is null || (actor.Flags & EntFlags.Client) == 0) return;
        // QC gate (xonotic-server.cfg:231): 1 = shotgun shell only, 2 = shotgun + bullet casings. So the shell
        // ejects at >= 1 (shotgun.qc:78) and the bullet casing at >= 2 (machinegun.qc:108). Default is 2.
        float casingGate = type == CasingType.Shell ? 1f : 2f;
        if (Api.Services is not null && Api.Cvars.GetFloat("g_casings") < casingGate) return;

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out Vector3 right, out Vector3 up);
        // QC SpawnCasing eject velocity (deterministic PRNG). The up-velocity is per casingtype: shotgun shell
        // uses (30 - rand*5) (shotgun.qc:82: -(rand*5 - 30)*v_up), the bullet casing (70 - rand*5)
        // (machinegun.qc:111: -(rand*5 - 70)*v_up).
        float upBase = type == CasingType.Shell ? 30f : 70f;
        Vector3 vel = (Prandom.Float() * 50f + 50f) * right
                    - (Prandom.Float() * 25f + 25f) * forward
                    + (upBase - Prandom.Float() * 5f) * up;

        // Route the casing through the effect sink as a velocity-carrying emission. CASING_BULLET/CASING_SHELL
        // are registered Effects (EffectsList.cs) so this resolves to a non-null Effect with a stable RegistryId
        // — it networks live (ServerNet.WriteEffect encodes the origin+velocity instead of dropping a null-Effect
        // request) and the client routes the name to EffectSystem.SpawnCasing (the real bouncing brass shell, not
        // a generic particle burst). The casing kind picks the shell vs bullet model client-side.
        string casingName = type == CasingType.Shell ? "casing_shell" : "casing_bullet";
        EffectEmitter.EmitByEffectInfoName(casingName, muzzle, vel, 1, except: null);
    }

    /// <summary>
    /// Melee swing FX + woosh audio (W1-weaponfire-fx) — the shared seam for the shotgun/arena melee swing
    /// (QC shotgun.qc: <c>W_Shotgun_Attack2</c> plays <c>SND_SHOTGUN_MELEE</c> on swing-start and
    /// <c>W_Shotgun_Melee_Think</c> emits <c>EFFECT_SHOTGUN_WOOSH</c> per swing trace). Call once per swing
    /// (the start sound) and/or per trace (the woosh effect). Both args are optional so a caller can emit just
    /// the effect or just the sound.
    /// </summary>
    /// <param name="actor">The swinging player (the swing sound is emitted on them).</param>
    /// <param name="wooshPos">The swing-trace endpoint where the woosh particle plays (QC <c>trace_endpos</c>).</param>
    /// <param name="wooshDir">The swing path direction the woosh sprays along (QC <c>-melee_path</c>).</param>
    /// <param name="wooshEffect">The woosh effect name (e.g. "SHOTGUN_WOOSH"); null/empty skips the particle.</param>
    /// <param name="swingSound">The swing-start sound name (e.g. "SHOTGUN_MELEE"); null/empty skips the audio.</param>
    public static void MeleeWoosh(Entity actor, Vector3 wooshPos, Vector3 wooshDir,
        string? wooshEffect = "SHOTGUN_WOOSH", string? swingSound = null)
    {
        // QC W_Shotgun_Melee_Think: Send_Effect(EFFECT_SHOTGUN_WOOSH, trace_endpos, -melee_path, 1).
        if (!string.IsNullOrEmpty(wooshEffect))
            EffectEmitter.Emit(wooshEffect, wooshPos, wooshDir, 1);

        // QC W_Shotgun_Attack2: sound(actor, CH_WEAPON_A, SND_SHOTGUN_MELEE, VOL_BASE, ATTEN_NORM).
        if (!string.IsNullOrEmpty(swingSound) && actor is not null)
            SoundSystem.PlayOn(actor, swingSound);
    }

    // =========================================================================
    //  Headshot head-AABB (server/weapons/tracing.qc Headshot + common/util.qc trace_hits_box)
    // =========================================================================

    /// <summary>
    /// Port of <c>Headshot</c> (server/weapons/tracing.qc:220-229): does the shot segment
    /// <paramref name="start"/>→<paramref name="end"/> pass through the head box of <paramref name="targ"/>?
    /// Only living, unfrozen, damageable players have a head box. The box is QC's exact head AABB centred on
    /// the victim's origin: horizontally 60% of the body box, vertically from <c>1.3*view_ofs_z - 0.3*maxs_z</c>
    /// up to <c>maxs_z</c>. (QC's per-axis basis-vector multiply collapses to the component-wise form below.)
    /// </summary>
    public static bool Headshot(Entity targ, Entity attacker, Vector3 start, Vector3 end)
    {
        // QC: if (!IS_PLAYER(targ) || IS_DEAD(targ) || STAT(FROZEN, targ) || !targ.takedamage) return false;
        if ((targ.Flags & EntFlags.Client) == 0 || targ.IsCorpse) return false;        // IS_PLAYER
        if (targ.DeadState != DeadFlag.No) return false;                                 // IS_DEAD
        if (IsFrozen(targ)) return false;                                                // STAT(FROZEN)
        if (targ.TakeDamage == DamageMode.No) return false;

        Vector3 org = targ.Origin; // antilag already applied to the victim's origin upstream
        Vector3 headMins = new(
            org.X + 0.6f * targ.Mins.X,
            org.Y + 0.6f * targ.Mins.Y,
            org.Z + 1.3f * targ.ViewOfs.Z - 0.3f * targ.Maxs.Z);
        Vector3 headMaxs = new(
            org.X + 0.6f * targ.Maxs.X,
            org.Y + 0.6f * targ.Maxs.Y,
            org.Z + targ.Maxs.Z);

        return TraceHitsBox(start, end, headMins, headMaxs);
    }

    /// <summary>QC <c>STAT(FROZEN, e)</c> — the gametype freeze stat OR the Frozen status effect.</summary>
    private static bool IsFrozen(Entity e)
        => e.FrozenStat != 0
        || (StatusEffectsCatalog.Frozen is { } f && StatusEffectsCatalog.Has(e, f));

    /// <summary>
    /// Port of <c>trace_hits_box</c> (common/util.qc:2219-2237): pure ray-vs-AABB slab test — does the
    /// segment <paramref name="start"/>→<paramref name="end"/> intersect the axis-aligned box
    /// [<paramref name="thmi"/>, <paramref name="thma"/>]? This is a plain math helper in QC (NOT an engine
    /// builtin), so it ports with zero engine dependency. Rebases everything to a ray from the origin to
    /// <c>end-start</c>, then intersects the running [a0,a1] parametric interval per axis.
    /// </summary>
    public static bool TraceHitsBox(Vector3 start, Vector3 end, Vector3 thmi, Vector3 thma)
    {
        end -= start;
        thmi -= start;
        thma -= start;
        // now it is a trace from 0 to end

        float a0 = 0f, a1 = 1f;
        if (!TraceHitsBox1d(end.X, thmi.X, thma.X, ref a0, ref a1)) return false;
        if (!TraceHitsBox1d(end.Y, thmi.Y, thma.Y, ref a0, ref a1)) return false;
        if (!TraceHitsBox1d(end.Z, thmi.Z, thma.Z, ref a0, ref a1)) return false;
        return true;
    }

    /// <summary>Port of <c>trace_hits_box_1d</c> (common/util.qc:2197-2217): one-axis slab clip.</summary>
    private static bool TraceHitsBox1d(float end, float thmi, float thma, ref float a0, ref float a1)
    {
        if (end == 0f)
        {
            // just check if 0 is in range for this axis
            if (0f < thmi) return false;
            if (0f > thma) return false;
        }
        else
        {
            // 0 -> end must stay within thmi -> thma
            a0 = MathF.Max(a0, MathF.Min(thmi / end, thma / end));
            a1 = MathF.Min(a1, MathF.Max(thmi / end, thma / end));
            if (a0 > a1) return false;
        }
        return true;
    }

    /// <summary>
    /// Route a weapon hit through the real damage pipeline (<see cref="Combat.Damage"/> ->
    /// <see cref="Damage.DamageSystem"/>): armor/health split, knockback, godmode/dead gating, and the
    /// Killed/Death-hook path now all live there (port of server/damage.qc). The
    /// <c>PlayerDamage_SplitHealthArmor</c> mutator hook (vampire etc.) fires inside the pipeline.
    ///
    /// The signature is kept caller-stable: existing weapons (Blaster/Vortex/Machinegun) call
    /// <c>ApplyDamage(hit, actor, damage, deathType)</c>; <paramref name="inflictor"/>,
    /// <paramref name="force"/> and <paramref name="hitLoc"/> are optional so those call sites are
    /// unaffected. <paramref name="deathType"/> stays an int weapon id at the call sites and is mapped to
    /// the pipeline's string deathtype tag here.
    /// </summary>
    public static void ApplyDamage(Entity target, Entity attacker, float damage, int deathType = 0,
        Entity? inflictor = null, Vector3 force = default, Vector3 hitLoc = default, string? deathTag = null)
    {
        if (damage <= 0f && force == Vector3.Zero) return;

        // Map the int deathtype id to the string tag the pipeline carries (DeathTypes stand-in for the QC
        // registry). The call sites pass the WEAPON RegistryId; 0 doubles as the "unattributed" sentinel
        // (which aliases weapon id 0 — Arc — whose beam therefore tags Generic; pre-existing convention).
        // [T57] FIX: this previously tagged FromWeapon(attacker.NetName) — the PLAYER's name — so every
        // weapon kill carried an unresolvable "weapon/<playername>" tag (generic kill feed, no per-weapon
        // attribution). Resolve the registry id to the weapon's NetName instead.
        // An explicit deathTag overrides the int-derived tag so a hitscan caller can carry HITTYPE_* bits
        // the int can't pack (QC deathtype | HITTYPE_SECONDARY) — e.g. the MachineGun mode-0 snipe secondary.
        string tag = deathTag ?? (deathType > 0 && deathType < Registry<Weapon>.Count
            ? Damage.DeathTypes.FromWeapon(Registry<Weapon>.ById(deathType).NetName)
            : Damage.DeathTypes.Generic);

        // inflictor defaults to the attacker for direct hitscan (QC passes the bullet's owner == attacker).
        Damage.Combat.Damage(target, inflictor ?? attacker, attacker, damage, tag, hitLoc, force);
    }

    // =========================================================================
    //  Shared spread / velocity / falloff math (common/weapons/calculations.qc,
    //  server/weapons/tracing.qc, lib/math.qh) — used by all weapons.
    // =========================================================================

    /// <summary>autocvar_g_weaponspreadfactor (default 1) — global spread scale.</summary>
    public static float WeaponSpreadFactor =>
        Api.Services is null ? 1f : (Api.Cvars.GetFloat("g_weaponspreadfactor") is var f && f > 0f ? f : 1f);

    /// <summary>QC <c>W_SPREAD_GAUSS_MAX_STDEV</c> (calculations.qh:5) — clamp on the gauss spread variate
    /// "to prevent the extremely rare wild shot".</summary>
    private const float SpreadGaussMaxStdev = 4f;

    /// <summary>autocvar_g_hitscan_spread_style (balance-xonotic.cfg:209 default 4 = gauss 2D plane) — the
    /// scatter distribution every <see cref="FireBullet"/> uses. Read live; falls back to the Base xonotic
    /// default 4 when no cvar service is installed (headless tests).</summary>
    public static int HitscanSpreadStyle =>
        Api.Services is null ? 4 : (int)Api.Cvars.GetFloat("g_hitscan_spread_style");

    /// <summary>QC <c>solve_cubic_pq(p, q)</c> (calculations.qc:73) — roots of the depressed cubic
    /// <c>x^3 + p·x + q = 0</c>, returned as a vec3.</summary>
    private static Vector3 SolveCubicPq(float p, float q)
    {
        float d = q * q / 4f + p * p * p / 27f;
        if (d < 0f)
        {
            // casus irreducibilis
            float a = 1f / 3f * MathF.Acos(-q / 2f * MathF.Sqrt(-27f / (p * p * p)));
            float u = MathF.Sqrt(-4f / 3f * p);
            return u * new Vector3(
                MathF.Cos(a + 2f / 3f * QMath.Pi),
                MathF.Cos(a + 4f / 3f * QMath.Pi),
                MathF.Cos(a));
        }
        else if (d == 0f)
        {
            if (p == 0f) return Vector3.Zero;
            float u = 3f * q / p;
            float vv = -u / 2f;
            return u >= vv ? new Vector3(vv, vv, u) : new Vector3(u, vv, vv);
        }
        else
        {
            // cardano (cbrt)
            float a = MathF.Cbrt(-q / 2f + MathF.Sqrt(d)) + MathF.Cbrt(-q / 2f - MathF.Sqrt(d));
            return new Vector3(a, a, a);
        }
    }

    /// <summary>QC <c>solve_cubic_abcd(a, b, c, d)</c> (calculations.qc:110) — roots of
    /// <c>a·x^3 + b·x^2 + c·x + d = 0</c> via the depressed-cubic substitution.</summary>
    private static Vector3 SolveCubicAbcd(float a, float b, float c, float d)
    {
        float p = 9f * a * c - 3f * b * b;
        float q = 27f * a * a * d - 9f * a * b * c + 2f * b * b * b;
        Vector3 v = SolveCubicPq(p, q);
        v = (v - b * Vector3.One) * (1f / (3f * a));
        if (a < 0f)
            v += new Vector3(1f, 0f, -1f) * (v.Z - v.X); // swap x, z
        return v;
    }

    /// <summary>QC <c>cliptoplane(v, p)</c> (calculations.qc:68) — project <paramref name="v"/> onto the plane
    /// whose normal is <paramref name="p"/> (remove the component along p): <c>v - (v·p)·p</c>.</summary>
    private static Vector3 ClipToPlane(Vector3 v, Vector3 p) => v - Vector3.Dot(v, p) * p;

    /// <summary>QC <c>findperpendicular(v)</c> (calculations.qc:125) — a unit vector perpendicular to
    /// <paramref name="v"/>: <c>normalize(cliptoplane('v.z -v.x v.y', v))</c>.</summary>
    private static Vector3 FindPerpendicular(Vector3 v)
        => QMath.Normalize(ClipToPlane(new Vector3(v.Z, -v.X, v.Y), v));

    /// <summary>
    /// Port of W_CalculateSpread (calculations.qc) used by both projectile and hitscan paths. The hitscan
    /// path passes the live <see cref="HitscanSpreadStyle"/> (Base xonotic default 4 = gauss-2D on the aim
    /// plane); projectile callers use style 0 (<c>dir + randomvec() * spread</c>). Uses the deterministic
    /// <see cref="Prandom"/> (QC random()/randomvec()/gsl_ran_ugaussian), so server and predicting client
    /// agree (ADR-0010). The per-style sigma factors and density functions mirror QC exactly.
    /// </summary>
    public static Vector3 CalculateSpread(Vector3 dir, float spread, bool mustNormalize, int spreadStyle = 0)
    {
        spread *= WeaponSpreadFactor;
        if (spread <= 0f) return mustNormalize ? QMath.Normalize(dir) : dir;

        Vector3 v1, v2;
        float sigma, dx, dy, r;
        switch (spreadStyle)
        {
            default:
            case 0:
                // baseline: randomvec() uniform in the unit ball (density sqrt(1-r^2))
                v1 = dir + Prandom.Vec() * spread;
                return mustNormalize ? QMath.Normalize(v1) : v1;
            case 1:
                // flattened sphere
                return QMath.Normalize(dir + ClipToPlane(Prandom.Vec() * spread, dir));
            case 2:
                // circle spread (stddev sqrt(1/2) at sigma=1, factor matches baseline stddev)
                sigma = spread * 0.89442719099991587855f;
                v1 = FindPerpendicular(dir);
                v2 = Vector3.Cross(dir, v1);
                dx = Prandom.Float() * 2f * QMath.Pi;
                dy = MathF.Sin(dx);
                dx = MathF.Cos(dx);
                r = MathF.Sqrt(Prandom.Float());
                return QMath.Normalize(dir + (v1 * dx + v2 * dy) * r * sigma);
            case 3: // gauss 3d
                sigma = spread * 0.44721359549996f;
                v1 = new Vector3(Prandom.Gaussian() * sigma, Prandom.Gaussian() * sigma, Prandom.Gaussian() * sigma);
                if (v1.LengthSquared() > SpreadGaussMaxStdev * SpreadGaussMaxStdev)
                    v1 = QMath.Normalize(v1) * SpreadGaussMaxStdev;
                v2 = dir + v1;
                return mustNormalize ? QMath.Normalize(v2) : v2;
            case 4: // gauss 2d (Base xonotic default) — clipped to the aim plane
                sigma = spread * 0.44721359549996f;
                v1 = new Vector3(Prandom.Gaussian() * sigma, Prandom.Gaussian() * sigma, Prandom.Gaussian() * sigma);
                if (v1.LengthSquared() > SpreadGaussMaxStdev * SpreadGaussMaxStdev)
                    v1 = QMath.Normalize(v1) * SpreadGaussMaxStdev;
                return QMath.Normalize(dir + ClipToPlane(v1, dir));
            case 5: // 1-r (linear falloff)
                sigma = spread * 1.154700538379252f;
                v1 = FindPerpendicular(dir);
                v2 = Vector3.Cross(dir, v1);
                dx = Prandom.Float() * 2f * QMath.Pi;
                dy = MathF.Sin(dx);
                dx = MathF.Cos(dx);
                r = SolveCubicAbcd(-2f, 3f, 0f, -Prandom.Float()).Y;
                return QMath.Normalize(dir + (v1 * dx + v2 * dy) * r * sigma);
            case 6: // 1-r^2 (quadratic falloff)
                sigma = spread * 1.095445115010332f;
                v1 = FindPerpendicular(dir);
                v2 = Vector3.Cross(dir, v1);
                dx = Prandom.Float() * 2f * QMath.Pi;
                dy = MathF.Sin(dx);
                dx = MathF.Cos(dx);
                r = MathF.Sqrt(1f - MathF.Sqrt(1f - Prandom.Float()));
                return QMath.Normalize(dir + (v1 * dx + v2 * dy) * r * sigma);
            case 7: // (1-r)(2-r) (stronger falloff)
                sigma = spread * 1.224744871391589f;
                v1 = FindPerpendicular(dir);
                v2 = Vector3.Cross(dir, v1);
                dx = Prandom.Float() * 2f * QMath.Pi;
                dy = MathF.Sin(dx);
                dx = MathF.Cos(dx);
                r = 1f - MathF.Sqrt(Prandom.Float());
                r = 1f - MathF.Sqrt(r);
                return QMath.Normalize(dir + (v1 * dx + v2 * dy) * r * sigma);
        }
    }

    /// <summary>
    /// Port of W_CalculateSpreadPattern(pattern 1, …) (calculations.qc): lay <paramref name="total"/> shots
    /// out as a fan — shot 0 is dead center, the rest sweep an even arc. Returns an offset whose Y/Z map to
    /// the right/up axes (X unused). <paramref name="bias"/> pulls outer shots back toward center by up to
    /// that fraction (deterministic <see cref="Prandom"/>).
    /// </summary>
    public static Vector3 CalculateSpreadPattern(int counter, int total, float bias = 0f)
    {
        if (counter == 0 || total <= 1) return Vector3.Zero;
        // makevectors('0 360 0' * (0.75 + (counter - 0.5) / (total - 1))) then s.y=fwd.x, s.z=fwd.y.
        float yawDeg = 360f * (0.75f + (counter - 0.5f) / (total - 1));
        QMath.AngleVectors(new Vector3(0f, yawDeg, 0f), out Vector3 fwd, out _, out _);
        Vector3 s = new(0f, fwd.X, fwd.Y);
        if (bias != 0f) s *= (1f - bias) + Prandom.Float() * bias;
        return s;
    }

    /// <summary>
    /// Port of W_SetupProjVelocity_Explicit (server/weapons/tracing.qc) projectile-velocity core: fold the
    /// up/z launch speeds into <paramref name="dir"/>, renormalize, apply spread, then scale by speed. The
    /// QC Newtonian velocity-inheritance (g_projectiles_newton_style) defaults OFF, so the result is just
    /// <c>speed * spread(normalize(dir + up*(upSpeed/speed) + z*(zSpeed/speed)))</c>.
    /// </summary>
    public static Vector3 ProjectileVelocity(Vector3 dir, Vector3 up, float speed,
        float upSpeed = 0f, float zSpeed = 0f, float spread = 0f)
    {
        if (speed == 0f) return Vector3.Zero;
        dir += up * (upSpeed / speed);
        dir.Z += zSpeed / speed;
        speed *= dir.Length();
        dir = QMath.Normalize(dir);
        dir = CalculateSpread(dir, spread, mustNormalize: false);
        return QMath.Normalize(dir) * speed;
    }

    /// <summary>
    /// Port of ExponentialFalloff (lib/math.qh): a multiplier in (0,1] that halves every
    /// <paramref name="halflifeDist"/> units of distance, clamped to [<paramref name="minDist"/>,
    /// <paramref name="maxDist"/>]. <paramref name="halflifeDist"/> &lt;= 0 disables falloff (returns 1).
    /// </summary>
    public static float ExponentialFalloff(float minDist, float maxDist, float halflifeDist, float d)
    {
        if (halflifeDist > 0f)
            return MathF.Pow(0.5f, (QMath.Clamp(d, minDist, maxDist) - minDist) / halflifeDist);
        if (halflifeDist < 0f)
            return MathF.Pow(0.5f, (QMath.Clamp(d, minDist, maxDist) - maxDist) / halflifeDist);
        return 1f;
    }
}
