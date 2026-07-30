using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The seamless-portal transform — the deterministic core of <c>lib/warpzone</c> (common.qc
/// <c>WarpZone_SetUp</c> / <c>WarpZone_TransformOrigin</c> / <c>_TransformVelocity</c> / <c>_TransformAngles</c>).
/// A warpzone glues two surfaces (an IN plane and an OUT plane) so an entity crossing the IN surface emerges
/// from the OUT surface with its position, velocity and view rotated by the relative plane transform — a 180°
/// turn (you exit the OTHER way) composed with the rotation between the two planes. This preserves momentum
/// so movement is continuous (strafe-jumps carry through).
///
/// The transform is <c>R·v</c> for directions and <c>outOrigin + R·(v − inOrigin)</c> for positions, where R
/// maps the IN basis to the 180°-flipped OUT basis (inFwd→−outFwd, inRight→−outRight, inUp→outUp). This is
/// the QC <c>warpzone_transform</c>/<c>warpzone_shift</c> pair expressed with explicit forward/right/up bases
/// (no AnglesTransform vector encoding). The seamless VIEW rendering through the portal is a client concern.
/// </summary>
public readonly struct WarpzoneTransform
{
    private readonly Vector3 _inFwd, _inRight, _inUp;
    private readonly Vector3 _outFwd, _outRight, _outUp;
    private readonly Vector3 _inOrigin, _outOrigin;
    public readonly bool Valid;

    public Vector3 InOrigin => _inOrigin;
    public Vector3 OutOrigin => _outOrigin;
    public Vector3 InForward => _inFwd;
    public Vector3 OutForward => _outFwd;
    /// <summary>The plane in-plane bases (right/up), for anchoring a window-aligned portal frustum + its quad UVs.</summary>
    public Vector3 InRight => _inRight;
    public Vector3 InUp => _inUp;
    public Vector3 OutRight => _outRight;
    public Vector3 OutUp => _outUp;

    /// <summary>
    /// QC <c>WarpZone_SetUp(e, my_org, my_ang, other_org, other_ang)</c>: build the transform from the IN plane
    /// (origin + Euler angles, forward = surface normal toward the approaching entity) to the OUT plane.
    /// </summary>
    public WarpzoneTransform(Vector3 inOrigin, Vector3 inAngles, Vector3 outOrigin, Vector3 outAngles)
    {
        QMath.AngleVectors(inAngles, out _inFwd, out _inRight, out _inUp);
        QMath.AngleVectors(outAngles, out _outFwd, out _outRight, out _outUp);
        _inOrigin = inOrigin;
        _outOrigin = outOrigin;
        Valid = true;
    }

    /// <summary>R·v — rotate a direction/velocity through the portal (QC WarpZone_TransformVelocity).</summary>
    public Vector3 Rotate(Vector3 v)
    {
        float a = Vector3.Dot(_inFwd, v), b = Vector3.Dot(_inRight, v), c = Vector3.Dot(_inUp, v);
        // map the IN basis to the 180°-flipped OUT basis: inFwd→−outFwd, inRight→−outRight, inUp→outUp.
        return -_outFwd * a - _outRight * b + _outUp * c;
    }

    /// <summary>QC WarpZone_TransformOrigin: outOrigin + R·(v − inOrigin).</summary>
    public Vector3 TransformOrigin(Vector3 v) => _outOrigin + Rotate(v - _inOrigin);

    /// <summary>QC WarpZone_TransformVelocity: R·v (no translation).</summary>
    public Vector3 TransformVelocity(Vector3 v) => Rotate(v);

    /// <summary>
    /// QC WarpZone_TransformVAngles: rotate the view forward vector through the portal, re-derive angles, and
    /// NORMALIZE. The trailing <see cref="QMath.AnglesTransformNormalize"/> is essential — Base ends
    /// WarpZone_TransformVAngles with AnglesTransform_Normalize, and the view consumer clamps pitch to [−89, 89];
    /// without normalization a <see cref="QMath.FixedVecToAngles"/> result for an exit facing even slightly DOWN is
    /// pitch ≈ −355, which clamps to −89 → the view snaps STRAIGHT UP (the warpzone exit-angle bug). Roll is not
    /// modeled here (forward-only); a roll-imparting / tilted plane pair is a separate follow-up.
    /// </summary>
    public Vector3 TransformAngles(Vector3 angles)
    {
        QMath.AngleVectors(angles, out Vector3 fwd, out _, out _);
        return QMath.AnglesTransformNormalize(QMath.FixedVecToAngles(Rotate(fwd)), minimizeRoll: false);
    }

    /// <summary>The inverse transform (the OUT→IN direction), for tracing/queries the other way.</summary>
    public WarpzoneTransform Inverse() => new(_outOrigin, AnglesOf(_outFwd, _outUp), _inOrigin, AnglesOf(_inFwd, _inUp));

    private static Vector3 AnglesOf(Vector3 fwd, Vector3 up) => QMath.FixedVecToAngles(fwd); // roll from up not modeled

    /// <summary>
    /// [T45] Decompose this transform into its affine form <c>p → R·p + shift</c> for the trace/radius
    /// recursion accumulator (<see cref="WarpzoneTransformChain"/>). The rotation rows are emitted so
    /// that <c>(R·v)[k] == Dot(rowK, v)</c>; <paramref name="shift"/> is <c>outOrigin − R·inOrigin</c>. This is
    /// the explicit-basis equivalent of QC's <c>warpzone_transform</c>/<c>warpzone_shift</c> pair
    /// (lib/warpzone/common.qc), composed by the chain exactly as <c>WarpZone_Accumulator_AddTransform</c> does.
    /// </summary>
    public void GetAffine(out Vector3 row0, out Vector3 row1, out Vector3 row2, out Vector3 shift)
    {
        // Rotate(v) = -outFwd*(inFwd·v) - outRight*(inRight·v) + outUp*(inUp·v) (see Rotate above); so the k-th
        // output component is Dot(rowK, v) with rowK = -outFwd[k]*inFwd - outRight[k]*inRight + outUp[k]*inUp.
        row0 = -_outFwd.X * _inFwd - _outRight.X * _inRight + _outUp.X * _inUp;
        row1 = -_outFwd.Y * _inFwd - _outRight.Y * _inRight + _outUp.Y * _inUp;
        row2 = -_outFwd.Z * _inFwd - _outRight.Z * _inRight + _outUp.Z * _inUp;
        // shift = outOrigin - R·inOrigin (so TransformPoint(inOrigin) == outOrigin), matching TransformOrigin.
        Vector3 rotIn = new(
            Vector3.Dot(row0, _inOrigin),
            Vector3.Dot(row1, _inOrigin),
            Vector3.Dot(row2, _inOrigin));
        shift = _outOrigin - rotIn;
    }
}

/// <summary>
/// A single seamless portal in the world — the C# successor to the QuakeC <c>trigger_warpzone</c> edict
/// (lib/warpzone/server.qc). It pairs an IN trigger volume with the transform to its linked OUT plane. When an
/// entity moving INTO the IN surface touches it, <see cref="Warpzones.Teleport"/> warps it to the exit with its
/// momentum preserved. Linked by <see cref="Entity.Target"/>/<see cref="Entity.TargetName"/> (the other zone),
/// like the QC warpzone target wiring; <see cref="WarpzoneManager"/> resolves the pairs.
/// </summary>
public sealed class Warpzone
{
    /// <summary>The trigger entity (the IN surface volume), or null in a headless test that uses the POJO transform.</summary>
    public Entity? Trigger;

    /// <summary>The transform from this zone's IN plane to its linked exit (set when the pair is linked).</summary>
    public WarpzoneTransform Transform;

    /// <summary>QC <c>.aiment</c> — the explicit-orientation entity (a <c>trigger_warpzone_position</c> / killtarget
    /// target_position) used to re-derive the IN plane on a moving-warpzone think or a runtime reconnect. Null when
    /// the zone orients purely from its brush geometry.</summary>
    public Entity? Aiment;

    /// <summary>QC <c>spawnflags &amp; 1</c> (server.qc:628) — a MOVING warpzone runs a per-frame
    /// <c>WarpZone_Think</c> that re-derives its transform whenever the zone or its partner has moved. Off for a
    /// static map warpzone (the common case).</summary>
    public bool Moving;

    /// <summary>QC <c>.warpzone_isboxy</c> (util_server.qc <c>WarpZoneLib_ExactTrigger_Init</c>): the trigger is a
    /// plain box (no inline brush model, OR the mapper overrode the model bounds with explicit mins/maxs), so Base
    /// skips the per-touch exact-surface match (<c>EXACTTRIGGER_TOUCH</c>) and uses the AABB directly. The port's
    /// touch already gates on the plane-side test + AABB overlap (no exact-surface trace), so this is recorded for
    /// fidelity but the touch path is the same either way.</summary>
    public bool IsBoxy;

    /// <summary>QC <c>.scale</c> (server.qc:662 — defaults from <c>.modelscale</c> then 1): the warpzone trigger
    /// volume is sized <c>mins*scale</c>..<c>maxs*scale</c> (util_server.qc:40). A mapper-set scale grows/shrinks the
    /// crossing volume.</summary>
    public float Scale = 1f;

    /// <summary>QC <c>warpzone_save_origin/_angles/_eorigin/_eangles</c> (server.qc:25-28): the cached brush
    /// origin/angles of this zone and its partner from the last transform derivation, so <c>WarpZone_Think</c> only
    /// re-derives when something actually moved.</summary>
    public Vector3 SaveOrigin, SaveAngles, SaveEOrigin, SaveEAngles;

    /// <summary>QC warpzone_origin/_angles — the IN plane this zone presents.</summary>
    public Vector3 InOrigin, InAngles;

    /// <summary>The linked exit plane (the partner zone's IN plane), resolved by <see cref="WarpzoneManager.Link"/>.</summary>
    public Vector3 OutOrigin, OutAngles;

    /// <summary>The target name linking this zone to its partner (QC .target / .targetname).</summary>
    public string TargetName = "", Target = "";

    /// <summary>QC <c>.enemy</c> — the linked partner zone (the OUT side). Set when the pair is linked; used to fire
    /// the partner's targets on a crossing (QC <c>SUB_UseTargets_SkipTargets(this.enemy, ...)</c>).</summary>
    public Warpzone? Partner;

    /// <summary>The owning player for a Porto-weapon portal (QC <c>portal.owner</c>), or null for a map warpzone.
    /// Used by <see cref="WarpzoneManager.ClearAllPortoPortals"/> to tear down every portal of one owner on death/reset.</summary>
    public Entity? Owner;

    /// <summary>For a Porto-weapon portal: whether this zone is the owner's IN-portal (true, QC <c>own.portal_in</c>) or
    /// OUT-portal (false, QC <c>own.portal_out</c>). Used to enforce QC's one-in/one-out-per-owner replacement
    /// (<c>Portal_SetInPortal</c>/<c>Portal_SetOutPortal</c>) so re-firing tears down the prior portal of that role.</summary>
    public bool IsInPortal;

    /// <summary>QC <c>portal.portal_activatetime</c> (server/portals.qc:301-306): the time before which the portal
    /// OWNER (the player who just shot the in-portal, QC <c>this.aiment</c>) is NOT re-teleported by their own
    /// portal — instead the activate window slides forward 0.1s. Lets the owner step away from the muzzle of their
    /// fresh portal without immediately falling back through it. 0 for a map warpzone (no owner). Porto only.</summary>
    public float ActivateTime;

    /// <summary>QC <c>portal.teleport_time</c> (server/portals.qc:399, set in Portal_Connect): the time the portal pair
    /// was connected. A portal telefrag within 1s of this awards the <c>ANNCE_ACHIEVEMENT_AMAZING</c> announcer
    /// (portals.qc:192-194). Porto portals only (0 for a map warpzone).</summary>
    public float TeleportTime;

    /// <summary>QC <c>portal.fade_time</c> (server/portals.qc:395-396, 211, 507-508): the absolute time a Porto portal
    /// pair expires. Set to <c>time + g_balance_portal_lifetime</c> (default 15) when the pair connects
    /// (Portal_Connect) and RECHARGED to a fresh <c>time + lifetime</c> on every successful crossing
    /// (Portal_TeleportPlayer:211) so an actively-used portal keeps living; <c>Portal_Think</c> removes the pair once
    /// <c>time &gt; fade_time</c>. 0 = no expiry (a map warpzone, or <c>g_balance_portal_lifetime &lt; 0</c>). Porto only.</summary>
    public float FadeTime;

    public bool Linked => Transform.Valid;
}

/// <summary>
/// The warpzone manager — holds the world's warpzones, links each to its partner (by targetname), and performs
/// the seamless teleport when an entity crosses a zone. The C# successor to lib/warpzone's server-side
/// WarpZone_Teleport + the trigger touch. Godot-free: operates on <see cref="Entity"/> through the facade.
/// </summary>
public sealed class WarpzoneManager
{
    private readonly List<Warpzone> _zones = new();
    public IReadOnlyList<Warpzone> Zones => _zones;

    /// <summary>
    /// QC <c>.warpzone_teleport_finishtime</c> — the per-entity same-frame re-teleport guard (server.qc:186). After a
    /// crossing the entity emerges potentially inside/touching the PARTNER zone (or re-touches this one as the touch
    /// pass re-fires); a teleport is suppressed while <c>time &lt;= finishtime</c> so a single cross can't ping-pong
    /// between the two zones in one frame. Keyed by entity (QC stores it on the edict); pruned lazily when freed.
    /// </summary>
    private readonly Dictionary<Entity, float> _teleportFinishTime = new();

    /// <summary>Register a warpzone (QC trigger_warpzone spawnfunc → IL_PUSH(g_warpzones)).</summary>
    public Warpzone Add(Warpzone wz) { _zones.Add(wz); return wz; }

    /// <summary>
    /// QC WarpZone_SetUp wiring: link each zone to its <see cref="Warpzone.Target"/> partner, computing the
    /// IN→partner-IN transform. A zone whose partner is missing stays unlinked (inert). Call after all zones spawn.
    /// </summary>
    public void Link()
    {
        // [T45] QC sv_warpzone_allow_selftarget (lib/warpzone/server.qc WarpZone_InitStep_FindTarget, default 0):
        // a zone never selects ITSELF as its partner unless the cvar is set. Read once per link pass.
        bool allowSelfTarget = Api.Services is not null && Api.Cvars.GetFloat("sv_warpzone_allow_selftarget") != 0f;

        // Forward pass: each zone carrying a .target links to that partner (the symmetric common case where
        // both zones name each other).
        foreach (Warpzone wz in _zones)
        {
            Warpzone? partner = FindByTargetName(wz.Target, allowSelfTarget ? null : wz);
            if (partner is not null) LinkOneWay(wz, partner);
        }
        // Reverse pass — QC's two-way `this.enemy = e2; e2.enemy = this`: a zone that carries no .target but IS
        // targeted by another still links, pointing back at whoever targets it (asymmetric maps).
        foreach (Warpzone wz in _zones)
        {
            if (wz.Linked || string.IsNullOrEmpty(wz.TargetName)) continue;
            foreach (Warpzone other in _zones)
                if (!ReferenceEquals(other, wz) && other.Target == wz.TargetName) { LinkOneWay(wz, other); break; }
        }
    }

    private static void LinkOneWay(Warpzone wz, Warpzone partner)
    {
        wz.OutOrigin = partner.InOrigin;
        wz.OutAngles = partner.InAngles;
        wz.Transform = new WarpzoneTransform(wz.InOrigin, wz.InAngles, partner.InOrigin, partner.InAngles);
        wz.Partner = partner; // QC this.enemy = partner — for the partner-zone target relay on a crossing
    }

    private Warpzone? FindByTargetName(string name, Warpzone? excludeSelf = null)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (Warpzone wz in _zones)
        {
            if (ReferenceEquals(wz, excludeSelf)) continue; // QC !sv_warpzone_allow_selftarget: never self-link
            if (wz.TargetName == name) return wz;
        }
        return null;
    }

    /// <summary>
    /// QC <c>WarpZone_Touch</c> + <c>WarpZone_Teleport</c>: warp <paramref name="e"/> through <paramref name="wz"/> —
    /// transform its origin, velocity, angles and view, momentum preserved. The crossing is gated by the QC
    /// plane-side test <c>WarpZone_PlaneDist(this, origin + view_ofs) &gt;= 0 → skip</c> (server.qc:193): an entity is
    /// only teleported once it is on the FAR (negative) side of the IN plane — i.e. it has actually crossed the
    /// surface — regardless of its velocity sign, exactly like Base (an entity loitering just past the plane with
    /// outward/zero velocity is still teleported). The QC same-frame re-teleport guard
    /// (<c>warpzone_teleport_finishtime</c>) prevents a ping-pong between the paired zones in one frame. Returns true
    /// if the teleport happened.
    /// </summary>
    public bool Teleport(Entity e, Warpzone wz)
    {
        if (!wz.Linked) return false;

        // QC server.qc:189 — "FIXME needs a better check to know what is safe to teleport": a MOVETYPE_FOLLOW
        // entity (or one tag-attached to a parent, QC .tag_entity) is NOT teleported — it has no independent
        // movement, it sticks to its carrier, so warping its origin would tear it off. MOVETYPE_NONE is the OTHER
        // QC skip, but the port's MoveType defaults to None (0) and the live player/projectile paths don't all
        // explicitly set MOVETYPE_WALK, so gating on None here would wrongly block real crossings — that half of
        // the pre-gate is intentionally NOT modelled (see warpzones.teleport.player unresolved). FOLLOW is safe:
        // nothing on the touch path is a follow-attached toucher, and a follow carrier rides its .aiment anyway.
        if (e.MoveType == MoveType.Follow)
            return false;

        // QC server.qc:186 — already teleported this frame (the entity emerges in/near the partner zone and would
        // immediately re-touch). Suppress until the guard expires.
        float now = MapMover.Now();
        if (_teleportFinishTime.TryGetValue(e, out float finish))
        {
            if (e.IsFreed) _teleportFinishTime.Remove(e);   // don't pin a freed edict (QC stores it on the entity)
            else if (now <= finish) return false;
        }

        // QC Portal_Touch portal_activatetime self-skip (server/portals.qc:301-306): a Porto portal does NOT
        // re-teleport its own OWNER (the player who just shot the in-portal, QC this.aiment) during the 0.1s
        // activate window — instead the window slides forward 0.1s so the owner can clear the muzzle of their fresh
        // portal without immediately falling back through it. Only the warpzone's owner is skipped; everyone else
        // crosses normally. (No-op for map warpzones, which carry no owner.)
        if (wz.Owner is not null && ReferenceEquals(e, wz.Owner) && now < wz.ActivateTime)
        {
            wz.ActivateTime = now + 0.1f;
            return false;
        }

        // Porto-portal-only crossing exclusions (QC Portal_Touch, server/portals.qc:275-314). Map warpzones (no
        // owner) skip these — a flag CAN cross a map warpzone, and there is no independent-player gate there.
        if (wz.Owner is { IsFreed: false } portalAiment)
        {
            // QC portals.qc:275-276 — the CTF flag is never portalled (it must travel the map, not warp).
            if (e.ClassName == "item_flag_team")
                return false;

            // QC portals.qc:307-314 — you cannot walk through someone ELSE's portal in an independent-player
            // (LMS-style) mode: the toucher (and, for a projectile, its aiment owner) is gated against the portal's
            // owner. IsIndependentPlayer is currently always false (no independent mode ported), so this is a
            // faithful no-op today that becomes live if such a mode is added.
            if (!ReferenceEquals(e, portalAiment) && (e.Flags & EntFlags.Client) != 0
                && (e.IsIndependentPlayer || portalAiment.IsIndependentPlayer))
                return false;
            if (e.Aiment is { } eAiment && !ReferenceEquals(eAiment, portalAiment)
                && (eAiment.Flags & EntFlags.Client) != 0
                && (eAiment.IsIndependentPlayer || portalAiment.IsIndependentPlayer))
                return false;
        }

        // QC server.qc:193 — WarpZone_PlaneDist(this, toucher.origin + toucher.view_ofs) >= 0 → wrong side, don't
        // teleport yet. PlaneDist = (point - warpzone_origin)·warpzone_forward; the entity must be PAST the plane
        // (negative side) before it warps. This is the plane-side test, NOT a velocity-direction gate.
        Vector3 point = e.Origin + e.ViewOfs;
        if (Vector3.Dot(point - wz.Transform.InOrigin, wz.Transform.InForward) >= 0f)
            return false; // not yet across the seam

        // sv_warpzone_trace 1 — one-line crossing dump (entry→exit origin/velocity/angles + the zone planes) for
        // diagnosing exit-angle/momentum reports live. Default off. Captured pre-transform; printed post.
        bool traceCross = Api.Services is not null && Api.Cvars.GetFloat("sv_warpzone_trace") != 0f;
        Vector3 preO = e.Origin, preV = e.Velocity, preA = e.Angles;

        // QC WarpZone_Teleport (server.qc:84/88/137): the transform is applied to the EYE point (`o0 = origin +
        // view_ofs`) and the entity is placed at `o1 - view_ofs` — view_ofs is a local upright offset that does NOT
        // rotate with the crossing. Identical to transforming the origin for an upright zone pair (R·view_ofs ==
        // view_ofs) and degenerate for non-clients (view_ofs '0 0 0'), but a TILTED zone warps the eye, not the feet.
        Vector3 newOrigin = wz.Transform.TransformOrigin(e.Origin + e.ViewOfs) - e.ViewOfs;
        e.Velocity = wz.Transform.TransformVelocity(e.Velocity);
        e.Angles = wz.Transform.TransformAngles(e.Angles);
        if (traceCross)
            System.Console.WriteLine(
                $"[wz] cross client={(e.Flags & EntFlags.Client) != 0} '{e.ClassName}' o {preO}->{newOrigin} v {preV}->{e.Velocity} a {preA}->{e.Angles}"
                + $" | in o={wz.Transform.InOrigin} f={wz.Transform.InForward} | out o={wz.Transform.OutOrigin} f={wz.Transform.OutForward}");
        // QC WarpZone_TeleportPlayer (server.qc:51) — `player.fixangle = true`. The exit facing is
        // SERVER-AUTHORITATIVE in Base: the engine snaps the client view to the teleported `player.angles`. Stamp
        // the same fixangle channel here so the listen host snaps the LOCAL view to the rotated facing
        // (NetGame reads `LocalServerPlayer.FixAngle`). Without this the only view-snap was the client predictor's
        // one-shot `carrier.FixAngle` (TriggerTouch.PredictWarpzonesAmbient) — which EntityMovementStep clears
        // every replayed tick, and which the server-warps-then-reconciles race on a 0-latency listen host drops
        // before it is read: the body + velocity warp but the VIEW stays put, so you emerge looking 90° into the
        // now-perpendicular exit corridor (the "comes out at the wrong angle / rotated sideways" report). The math
        // (TransformAngles) was already Base-faithful for these perpendicular vertical-wall zones — the snap simply
        // never fired. FixAngleAngles carries the already-transformed exit facing the host applies verbatim.
        if ((e.Flags & EntFlags.Client) != 0)
        {
            e.FixAngle = true;
            e.FixAngleAngles = e.Angles;
            // QC WarpZone_TeleportPlayer (server.qc:54-58): a BOT's aim state must follow the seam — Base sets
            // `player.v_angle = player.angles` + bot_aim_reset for bot clients. The port's BotBrain re-seeds its
            // aim from bot.ViewAngles at think start (and writes it back over .Angles at think end), so a STALE
            // ViewAngles steers the bot straight back INTO the zone it just exited — an endless cross→re-cross
            // loop that also thrashes navigation (route clears + forced strategy re-rates every crossing).
            // Human clients get ViewAngles re-stamped from input every tick, so this is human-harmless.
            e.ViewAngles = e.Angles;
        }
        e.AVelocity = wz.Transform.TransformVelocity(e.AVelocity);
        // QC WarpZone_TeleportPlayer (server.qc:46-47): stamp the teleport bookkeeping — lastteleport_origin is
        // the PRE-warp origin, lastteleporttime the crossing time (anti-stuck + bot-nav consumers read these).
        e.LastTeleportOrigin = e.Origin;
        e.LastTeleportTime = now;
        if (Api.Services is not null) Api.Entities.SetOrigin(e, newOrigin);
        else e.Origin = newOrigin;
        e.OldOrigin = newOrigin; // QC: cancel interpolation across the seam (a teleport, not a slide)
        // QC WarpZone_TeleportPlayer (server.qc:63) BITXOR_ASSIGN(player.effects, EF_TELEPORT_BIT) — toggle the
        // networked teleport bit so the client cancels its model interpolation across the seam (no stretch on the
        // remote model crossing a zone).
        e.Effects ^= EffectFlags.Teleport;
        // QC WarpZone_TeleportPlayer (server.qc:65-66) — `if (IS_PLAYER(player)) BITCLR_ASSIGN(player.flags,
        // FL_ONGROUND)`: a player crossing the seam is airborne until the next ground trace re-grounds them. The
        // PREDICTED crossing (TriggerTouch.PredictWarpzonesAmbient) already clears it; without the same clear HERE
        // the server keeps ground friction + step-snap for a tick while the prediction doesn't — the reconcile then
        // rubber-bands the exit and eats momentum at the seam.
        if ((e.Flags & EntFlags.Client) != 0)
            e.Flags &= ~EntFlags.OnGround;

        // QC Portal_Think_TryTeleportPlayer (server/portals.qc:248-262): when a player crosses a Porto portal, any
        // grappling-hook chain they have OUT is relocated through the SAME transform so it stays latched across the
        // seam instead of snapping (the hook entity sits on the far side of the wall and would otherwise tear off as
        // the owner warps away). Base iterates the player's weaponentity.hook and warps its origin + velocity by the
        // portal transform. Map warpzones (no owner) skip this — it is a Porto-portal-only side effect (the hook
        // would re-touch the warpzone trigger on its own anyway). Only a CLIENT crossing has hooks to carry.
        if (wz.Owner is not null && (e.Flags & EntFlags.Client) != 0)
        {
            e.ForEachWeaponSlot(s =>
            {
                if (s.Hook is { IsFreed: false } hook)
                {
                    Vector3 hookOrigin = wz.Transform.TransformOrigin(hook.Origin);
                    hook.Velocity = wz.Transform.TransformVelocity(hook.Velocity);
                    hook.AVelocity = wz.Transform.TransformVelocity(hook.AVelocity);
                    if (Api.Services is not null) Api.Entities.SetOrigin(hook, hookOrigin);
                    else hook.Origin = hookOrigin;
                    hook.OldOrigin = hookOrigin; // cancel interpolation across the seam (a teleport, not a slide)
                }
            });
        }

        // QC server.qc:355 — stamp the same-frame re-teleport guard so the partner-zone touch (which the entity now
        // overlaps after emerging) doesn't immediately warp it straight back. Base uses
        // time + PHYS_INPUT_FRAMETIME - dt as a back-teleport guard; with no per-move dt here we hold for one frame.
        _teleportFinishTime[e] = now + MapMover.FrameTime();

        // QC Portal_TeleportPlayer (server/portals.qc:188-201): a Porto-portal crossing force-telefrags whoever is
        // standing at the exit (TELEPORT_FLAGS_PORTAL → FORCE_TDEATH), then EITHER awards the Amazing announcer (on a
        // telefrag within 1s of portal creation) OR — if no telefrag — credits the portal owner as the victim's
        // "pusher" for a hazard kill-credit window. The two are mutually exclusive (QC `if(tdeath_hit) … else if …`).
        // Map warpzones (no owner) run no tdeath and no kill-credit. The telefragger/inflictor is the portal owner so
        // the gib is scored to whoever placed the portal.
        // QC Portal_TeleportPlayer:211/395-396 — a successful crossing RECHARGES both portals' lifetime to a fresh
        // time + g_balance_portal_lifetime, so an actively-used Porto pair keeps living (only an idle pair times out).
        if (wz.Owner is not null && wz.FadeTime != 0f)
        {
            float fresh = PortalFadeTime();
            wz.FadeTime = fresh;
            if (wz.Partner is { } portalPartner) portalPartner.FadeTime = fresh;
        }

        if (wz.Owner is { IsFreed: false } portalOwner && (e.Flags & EntFlags.Client) != 0)
        {
            bool tdeathHit = Teleporters.PortalForceTelefrag(e, portalOwner, portalOwner, newOrigin);
            if (tdeathHit)
            {
                // QC: if(time < teleporter.teleport_time + 1) Send_Notification(ANNCE_ACHIEVEMENT_AMAZING).
                if (wz.TeleportTime != 0f && now < wz.TeleportTime + 1f)
                    NotificationSystem.Announce(e, "ACHIEVEMENT_AMAZING");
            }
            else if (!ReferenceEquals(e, portalOwner) && (portalOwner.Flags & EntFlags.Client) != 0)
            {
                e.Pusher = portalOwner;
                e.PushLTime = now + (Api.Services is not null && Api.Cvars.GetFloat("g_maxpushtime") != 0f
                    ? Api.Cvars.GetFloat("g_maxpushtime") : 8f);
                e.IsTypeFrag = e.ButtonChat;
            }

            // QC MUTATOR_CALLHOOK(PortalTeleport, player) (server/portals.qc): fire after the Porto-portal
            // crossing completes (telefrag + kill-credit already applied) so subscribers (CTF flag-drop, etc.)
            // see the player in its post-teleport state. Only for Porto portals (map warpzones have no owner).
            OnPortalTeleport?.Invoke(e);
        }

        // QC WarpZone_Touch fires the targets of BOTH this zone and its partner (this.enemy) on a crossing, with the
        // QC skip masks: this → SkipTargets BIT(1)|BIT(3); this.enemy → BIT(1)|BIT(2) (server.qc:218-219). A map can
        // chain a relay/sound/door off either side of a warpzone. No-op when neither trigger carries a target.
        if (wz.Trigger is { } trig)
            MapMover.UseTargetsEx(trig, e, trig, preventReuse: false,
                skipTargets: MapMover.SkipTarget1 | MapMover.SkipTarget3);
        if (wz.Partner?.Trigger is { } partnerTrig)
            MapMover.UseTargetsEx(partnerTrig, e, e, preventReuse: false,
                skipTargets: MapMover.SkipTarget1 | MapMover.SkipTarget2);
        return true;
    }

    /// <summary>
    /// QC <c>MUTATOR_CALLHOOK(PortalTeleport, player)</c> (server/portals.qc): fired once per successful
    /// Porto-portal crossing by a client (non-projectile) entity. Subscribers (CTF, Buffalo mutator, etc.)
    /// react to a player walking through a player-placed portal — drop a carried flag, swap teleport buffs,
    /// etc. Null when no subscriber is registered (fast path: the Warpzone teleport is extremely hot).
    /// </summary>
    public static event System.Action<Entity>? OnPortalTeleport;

    /// <summary>
    /// QC trigger_warpzone spawnfunc: create + register a warpzone from explicit IN/OUT plane parameters and a
    /// touch volume. The touch warps any crossing player/projectile. <paramref name="target"/>/<paramref name="targetName"/>
    /// link it to its partner (call <see cref="Link"/> after all zones spawn). Returns the zone.
    /// </summary>
    public Warpzone Spawn(Vector3 inOrigin, Vector3 inAngles, string targetName, string target,
        Vector3 mins, Vector3 maxs)
    {
        var wz = new Warpzone { InOrigin = inOrigin, InAngles = inAngles, TargetName = targetName, Target = target };
        SpawnTriggerFor(wz, mins, maxs);
        return Add(wz);
    }

    /// <summary>
    /// QC <c>WarpZone_InitStep_UpdateTransform</c>: derive a warpzone's IN plane (origin + angles) from the
    /// geometry of its brush model via the <c>getsurface*</c> builtins — area-weighted average of every
    /// (non-trigger) triangle's normal and centroid. This is the auto-orientation a mapper gets by just
    /// drawing a planar <c>trigger_warpzone</c> brush (no explicit angle). When <paramref name="aiment"/> (a
    /// <c>target_position</c>) is given, its origin/angles seed the result and the brush plane only corrects
    /// the orientation. Returns ok=false when the brush is non-planar / has no usable surface (the QC errors).
    /// </summary>
    public (Vector3 origin, Vector3 angles, bool ok) DerivePlaneFromBrush(Entity brush, Entity? aiment = null)
    {
        if (Api.Services is null) return (Vector3.Zero, Vector3.Zero, false);
        ISurfaceService surf = Api.Surfaces;

        Vector3 org = brush.Origin;
        if (org == Vector3.Zero) org = 0.5f * (brush.Mins + brush.Maxs);

        Vector3 norm = Vector3.Zero, point = Vector3.Zero, refNorm = Vector3.Zero;
        float area = 0f;
        for (int i_s = 0; ; i_s++)
        {
            string tex = surf.GetSurfaceTexture(brush, i_s);
            if (string.IsNullOrEmpty(tex)) break;                 // past the last surface (QC `if (!tex) break`)
            if (tex == "textures/common/trigger" || tex == "trigger") continue;
            refNorm += surf.GetSurfaceNormal(brush, i_s);         // reliable (vertex-normal) FRONT normal — for SIGN
            int nt = surf.GetSurfaceNumTriangles(brush, i_s);
            for (int i_t = 0; i_t < nt; i_t++)
            {
                Vector3 tri = surf.GetSurfaceTriangle(brush, i_s, i_t);
                Vector3 a = surf.GetSurfacePoint(brush, i_s, (int)tri.X);
                Vector3 b = surf.GetSurfacePoint(brush, i_s, (int)tri.Y);
                Vector3 c = surf.GetSurfacePoint(brush, i_s, (int)tri.Z);
                Vector3 n = Vector3.Cross(c - a, b - a);
                float w = n.Length();
                area += w;
                norm += n;
                point += w * (a + b + c);
            }
        }

        if (area > 0f)
        {
            // The geometric cross-product normal's SIGN depends on the BSP triangle winding (Cross(c-a,b-a) is the
            // negated standard triangle normal), and the no-aiment path below applies no correction — so an
            // aiment-less warpzone (e.g. glowplant) derived a BACKWARDS plane normal, which flipped the whole
            // warp transform (wrong teleport-exit angle AND wrong portal-camera view). Orient it to agree with the
            // surface's reliable rendering FRONT normal (the averaged vertex normals, which always face outward).
            if (Vector3.Dot(norm, refNorm) < 0f) norm = -norm;
            norm *= 1f / area;
            point *= 1f / (3f * area);
            if (norm.Length() < 0.99f) area = 0f;                  // surfaces don't agree on a plane → non-planar
            norm = QMath.Normalize(norm);
        }

        Vector3 ang = Vector3.Zero;
        if (aiment is not null)
        {
            org = aiment.Origin;
            ang = aiment.Angles;
            if (area > 0f)
            {
                org -= Vector3.Dot(org - point, norm) * norm;       // project the target onto the plane
                QMath.AngleVectors(ang, out Vector3 fwd, out _, out Vector3 up);
                if (Vector3.Dot(norm, fwd) < 0f) norm = -norm;       // face out of the trigger
                ang = QMath.FixedVecToAngles2(norm, up);             // keep roll, aim exactly against the plane
            }
        }
        else if (area > 0f)
        {
            org = point;
            ang = QMath.FixedVecToAngles(norm);
        }
        else
        {
            return (Vector3.Zero, Vector3.Zero, false);              // QC: error("cannot infer origin/angles")
        }

        return (org, ang, true);
    }

    /// <summary>
    /// Spawn a warpzone whose IN plane is auto-derived from <paramref name="brush"/>'s geometry (a planar
    /// <c>trigger_warpzone</c> brush model) via <see cref="DerivePlaneFromBrush"/>. The brush itself becomes the
    /// touch volume. <paramref name="target"/>/<paramref name="targetName"/> link it to its partner — call
    /// <see cref="Link"/> after all zones spawn. Returns null when the plane can't be inferred.
    /// </summary>
    public Warpzone? SpawnFromBrush(Entity brush, string targetName, string target, Entity? aiment = null)
    {
        (Vector3 org, Vector3 ang, bool ok) = DerivePlaneFromBrush(brush, aiment);
        if (!ok) return null;
        var wz = new Warpzone
        {
            InOrigin = org, InAngles = ang, TargetName = targetName, Target = target, Trigger = brush,
            Aiment = aiment, Moving = (brush.SpawnFlags & 1) != 0,
            Scale = brush.WarpzoneScale != 0f ? brush.WarpzoneScale : 1f, IsBoxy = brush.WarpzoneIsBoxy,
        };
        brush.ClassName = "trigger_warpzone";
        brush.Solid = Solid.Trigger;
        brush.Touch = (self, other) => { if (!other.IsFreed && other.Solid != Solid.Not) Teleport(other, wz); };
        return Add(wz);
    }

    /// <summary>Create the touch-volume entity for a zone (QC the warpzone brush trigger), if a facade is wired.</summary>
    private void SpawnTriggerFor(Warpzone wz, Vector3 mins, Vector3 maxs)
    {
        if (Api.Services is null) return;
        Entity t = Api.Entities.Spawn();
        t.ClassName = "trigger_warpzone";
        t.Solid = Solid.Trigger;
        t.MoveType = MoveType.None;
        Api.Entities.SetSize(t, mins, maxs);
        Api.Entities.SetOrigin(t, wz.InOrigin);
        t.Touch = (self, other) => { if (!other.IsFreed && other.Solid != Solid.Not) Teleport(other, wz); };
        wz.Trigger = t;
    }

    private static readonly Vector3 PortoMins = new(-48, -48, -48);
    private static readonly Vector3 PortoMaxs = new(48, 48, 48);

    // QC own.portal_in / own.portal_out: the owner's CURRENT in/out portal slot. Each owner holds at most one of
    // each; placing a fresh portal of a role tears down the prior one in that slot (Portal_SetInPortal /
    // Portal_SetOutPortal, server/portals.qc:547-578). Keyed by owner so a re-fire after a completed pair replaces
    // it rather than leaving a second live pair behind.
    private readonly Dictionary<Entity, Warpzone> _portalIn = new();
    private readonly Dictionary<Entity, Warpzone> _portalOut = new();

    /// <summary>
    /// QC <c>realowner.portal_in.portal_id</c>: the portal_id (the porto shot's time-id, encoded in the in-portal's
    /// <c>porto_&lt;id&gt;_in</c> target name) of <paramref name="owner"/>'s CURRENT in-portal slot, or null when they
    /// have none. Wire <c>Porto.PortalInId</c> to this so the combined-shot blue stage verifies the out-portal pairs
    /// with the in-portal it just placed (porto.qc:263) before committing it.
    /// </summary>
    public int? PortalInId(Entity? owner)
    {
        if (owner is null || !_portalIn.TryGetValue(owner, out Warpzone? wz)) return null;
        // TargetName is "porto_<id>_in"; recover <id>.
        string name = wz.TargetName;
        int us0 = name.IndexOf('_');
        int us1 = us0 >= 0 ? name.IndexOf('_', us0 + 1) : -1;
        if (us0 < 0 || us1 < 0) return null;
        return int.TryParse(name.AsSpan(us0 + 1, us1 - us0 - 1), out int id) ? id : null;
    }

    /// <summary>
    /// QC Portal_SpawnIn/OutPortalAtTrace + Portal_SetInPortal/Portal_SetOutPortal: realise a Porto-weapon portal as
    /// a warpzone. The plane forward is the wall's surface normal (so an entity entering emerges out of the partner).
    /// Each owner holds at most one in-portal and one out-portal (the QC <c>own.portal_in</c>/<c>own.portal_out</c>
    /// slots): placing a new portal of a role first removes the owner's prior portal of that SAME role (Base
    /// disconnects + Portal_Remove the old), keeping the other role, then re-links the pair when both slots are
    /// filled. So re-firing after a completed pair replaces it instead of leaving two live pairs. Wire
    /// <c>Porto.PortalSpawner</c> to this on the host.
    /// </summary>
    public void PlacePortoPortal(Vector3 origin, Vector3 surfaceNormal, Vector3 rightVector, bool isInPortal, int portalId, Entity? owner)
    {
        // QC fixedvectoangles2(plane_normal, right_vector) (server/portals.qc): the wall normal is the portal's
        // forward, and right_vector (carried + reflected on the porto projectile) fixes its roll. Fall back to the
        // normal-only derivation when no usable roll axis was handed over.
        Vector3 angles = rightVector.LengthSquared() > 0.0001f
            ? QMath.FixedVecToAngles2(surfaceNormal, rightVector)
            : QMath.FixedVecToAngles(surfaceNormal); // forward = the wall normal
        // QC Portal_Spawn (portals.qc:646): portal.portal_activatetime = time + 0.1 — a 0.1s grace where the owner
        // who just shot this portal isn't pulled straight back through it (see Warpzone.ActivateTime / Teleport).
        var wz = new Warpzone
        {
            InOrigin = origin, InAngles = angles, Owner = owner, IsInPortal = isInPortal,
            ActivateTime = MapMover.Now() + 0.1f, TargetName = $"porto_{portalId}_{(isInPortal ? "in" : "out")}",
        };
        SpawnTriggerFor(wz, PortoMins, PortoMaxs);
        Add(wz);

        // A map/test caller with no owner can't track per-owner slots — fall back to immediate pairing by id.
        if (owner is null)
        {
            // (legacy/no-owner path) link to a pending same-id zone if one is waiting.
            foreach (Warpzone other in _zones)
                if (!ReferenceEquals(other, wz) && other.Owner is null
                    && other.TargetName == $"porto_{portalId}_{(isInPortal ? "out" : "in")}" && !other.Linked)
                { LinkPair(other, wz); break; }
            return;
        }

        Dictionary<Entity, Warpzone> sameRole = isInPortal ? _portalIn : _portalOut;
        Dictionary<Entity, Warpzone> otherRole = isInPortal ? _portalOut : _portalIn;

        // QC Portal_SetInPortal: if(own.portal_in){ if(own.portal_out) Portal_Disconnect(in,out); Portal_Remove(in,0); }
        // Remove the owner's prior portal of THIS role (disconnecting it from its partner first so the partner — the
        // other role's slot — survives to be reconnected with the new portal).
        if (sameRole.TryGetValue(owner, out Warpzone? prior))
        {
            sameRole.Remove(owner);
            if (prior.Partner is { } priorPartner) priorPartner.Partner = null; // Portal_Disconnect
            RemoveZone(prior); // Portal_Remove(prior, 0): tear down just this role's old portal
        }

        // own.portal_<role> = portal.
        sameRole[owner] = wz;

        // if(own.portal_<other>){ Portal_Connect(in, out); } — both slots filled, (re)link the two-way pair.
        if (otherRole.TryGetValue(owner, out Warpzone? partner) && _zones.Contains(partner))
            LinkPair(wz, partner);
    }

    /// <summary>
    /// QC <c>Portal_ClearWithID(own, id)</c>: tear down just the in/out portals of one Porto shot (used when a
    /// combined cnt&lt;0 shot soft-fails after already placing its in-portal, so the orphaned in-portal isn't left
    /// behind). Removes the still-pending in-portal for <paramref name="owner"/>+<paramref name="portalId"/>, plus
    /// any already-linked partner, unregistering each warpzone and deleting its trigger volume. Wire
    /// <c>Porto.PortalClearWithId</c> to this on the host.
    /// </summary>
    public void ClearPortoPortal(Entity? owner, int portalId)
    {
        // Remove any zone (and, via RemoveZone, its slot bookkeeping) tagged for this shot's id.
        for (int i = _zones.Count - 1; i >= 0; i--)
        {
            Warpzone wz = _zones[i];
            if (wz.TargetName == $"porto_{portalId}_in" || wz.TargetName == $"porto_{portalId}_out")
                RemoveZone(wz);
        }
    }

    /// <summary>
    /// QC <c>Portal_ClearAll_PortalsOnly(own)</c>: tear down EVERY portal belonging to <paramref name="owner"/> (both
    /// the pending in-portals and the fully-placed in/out pairs), regardless of shot id. Driven on player death/reset
    /// (QC <c>Portal_ClearAll</c>, called from MakePlayerObserver / ClientDisconnect / the race respawn). Wire
    /// <c>Porto.PortalClearAll</c> to this on the host.
    /// </summary>
    public void ClearAllPortoPortals(Entity? owner)
    {
        // Any Porto zone of this owner (in-portal, out-portal, pending or linked) — RemoveZone also clears the
        // owner's portal_in/portal_out slots.
        for (int i = _zones.Count - 1; i >= 0; i--)
        {
            Warpzone wz = _zones[i];
            if (ReferenceEquals(wz.Owner, owner) && wz.TargetName.StartsWith("porto_", System.StringComparison.Ordinal))
                RemoveZone(wz);
        }
        if (owner is not null) { _portalIn.Remove(owner); _portalOut.Remove(owner); }
    }

    /// <summary>Unregister a warpzone and free its trigger-volume edict (if the host owns one). Also drops the zone
    /// from its owner's portal_in/portal_out slot so a re-fire doesn't try to reconnect to a torn-down portal.</summary>
    private void RemoveZone(Warpzone wz)
    {
        _zones.Remove(wz);
        if (wz.Partner is { } partner) partner.Partner = null;
        if (wz.Owner is { } own)
        {
            if (_portalIn.TryGetValue(own, out Warpzone? pin) && ReferenceEquals(pin, wz)) _portalIn.Remove(own);
            if (_portalOut.TryGetValue(own, out Warpzone? pout) && ReferenceEquals(pout, wz)) _portalOut.Remove(own);
        }
        if (wz.Trigger is { } trig && Api.Services is not null && !trig.IsFreed)
            Api.Entities.Remove(trig);
        wz.Trigger = null;
    }

    /// <summary>
    /// QC <c>autocvar_g_balance_portal_lifetime</c> (server/portals.qh:4, balance-xonotic.cfg:272 = 15): the absolute
    /// expiry time for a freshly-connected/used Porto portal pair — <c>(lifetime &gt;= 0) ? time + lifetime : 0</c>
    /// (Portal_Connect:395, Portal_TeleportPlayer:211). A negative lifetime disables expiry (returns 0). Read live so a
    /// server/balance override applies. (Read raw via GetFloat: the cvar is registered with a positive default, and a
    /// negative value is a deliberate "never expire" — never masked.)
    /// </summary>
    private static float PortalFadeTime()
    {
        float lifetime = Api.Services is not null ? Api.Cvars.GetFloat("g_balance_portal_lifetime") : 15f;
        if (Api.Services is not null && string.IsNullOrEmpty(Api.Cvars.GetString("g_balance_portal_lifetime")))
            lifetime = 15f; // unregistered (test path) → the Base default
        return lifetime >= 0f ? MapMover.Now() + lifetime : 0f;
    }

    /// <summary>
    /// QC <c>Portal_Think</c> expiry sweep (server/portals.qc:507-508): remove any Porto portal pair whose lifetime has
    /// elapsed (<c>fade_time &amp;&amp; time &gt; fade_time</c>). Base runs this per-portal in the portal's own think; the
    /// warpzone realisation has no per-portal edict think, so the server drives this once per frame alongside
    /// <see cref="ThinkMovingZones"/>. Removing one half of a pair tears down its partner too (Portal_Remove recurses
    /// over <c>.enemy</c>), matching Base's both-portals-vanish-together expiry. Map warpzones (FadeTime 0) are skipped.
    /// </summary>
    public void ExpirePortals()
    {
        float now = MapMover.Now();
        for (int i = _zones.Count - 1; i >= 0; i--)
        {
            if (i >= _zones.Count) continue; // a prior partner removal shrank the list
            Warpzone wz = _zones[i];
            if (wz.Owner is null || wz.FadeTime == 0f || now <= wz.FadeTime)
                continue;
            // QC Portal_Remove(this, 0) natural-expiry tail (server/portals.qc): a timed-out portal plays
            // SND_PORTO_EXPIRE on CH_SHOTS at the portal before vanishing. (The CH_SHOTS SND_PORTO_EXPLODE +
            // EFFECT_ROCKET_EXPLODE belong to the SHOT-DOWN path — Portal_Remove(this,1) on health<0 — which has
            // no warpzone equivalent because the warpzone realisation carries no portal HEALTH entity; see
            // unportable[]. The SUB_SetFade(time,0.5) visual fade likewise needs the absent portal model.) Both
            // halves of the pair vanish together (Portal_Remove recurses over .enemy), so each plays its own cue.
            EmitPortalExpire(wz);
            // QC Portal_Remove(this, 0): the natural-expiry teardown tears down both halves of the pair.
            if (wz.Partner is { } partner) { EmitPortalExpire(partner); RemoveZone(partner); }
            RemoveZone(wz);
        }
    }

    /// <summary>
    /// QC <c>Portal_Remove</c> natural-expiry sound (server/portals.qc): <c>sound(this, CH_SHOTS, SND_PORTO_EXPIRE,
    /// VOL_BASE, ATTEN_NORM)</c> — play the portal's expire cue at its location as it times out. Emitted at the
    /// portal's IN-plane origin (the warpzone has no persistent portal edict, so it can't host an entity-channel
    /// sound after teardown) on CH_SHOTS (-4), matching the SND_PORTO_* channel the Porto weapon uses elsewhere.
    /// </summary>
    private static void EmitPortalExpire(Warpzone wz)
    {
        if (Api.Services is null) return;
        Vector3 at = wz.Trigger is { IsFreed: false } trig ? trig.Origin : wz.InOrigin;
        Api.Sound.PlayAt(at, SoundChannel.ShotsAuto /* CH_SHOTS (-4) */, "porto/expire.wav");
    }

    /// <summary>Link two warpzones into a two-way portal (each transforms toward the other's IN plane).</summary>
    public void LinkPair(Warpzone a, Warpzone b)
    {
        a.OutOrigin = b.InOrigin; a.OutAngles = b.InAngles;
        a.Transform = new WarpzoneTransform(a.InOrigin, a.InAngles, b.InOrigin, b.InAngles);
        a.Partner = b;
        b.OutOrigin = a.InOrigin; b.OutAngles = a.InAngles;
        b.Transform = new WarpzoneTransform(b.InOrigin, b.InAngles, a.InOrigin, a.InAngles);
        b.Partner = a;
        // QC Portal_Connect: teleporter.teleport_time = time — stamp the connect time on a Porto pair so a telefrag
        // within 1s of creation awards the Amazing announcer (Teleport / Portal_TeleportPlayer:192-194). Also stamp
        // the 15s expiry (QC Portal_Connect:395-396 fade_time = time + g_balance_portal_lifetime; both portals share
        // it), so a Porto pair times out like Base instead of persisting for the whole match (ExpirePortals sweep).
        if (a.Owner is not null || b.Owner is not null)
        {
            a.TeleportTime = b.TeleportTime = MapMover.Now();
            a.FadeTime = b.FadeTime = PortalFadeTime();
        }
    }

    // =============================================================================================
    //  Map-entity warpzones — QC spawnfunc(trigger_warpzone) / spawnfunc(trigger_warpzone_position)
    //  registers each, then the deferred WarpZone_StartFrame init pass derives planes + links pairs.
    // =============================================================================================
    private readonly List<Entity> _pendingZones = new();
    private readonly List<Entity> _pendingPositions = new();

    /// <summary>
    /// The static→instance bridge target: <see cref="WarpzoneSpawns.Sink"/> (wired by the host's Boot, mirroring
    /// <c>Porto.PortalSpawner</c>) routes each spawned <c>trigger_warpzone</c>/<c>_position</c> edict here, where
    /// it is dispatched by classname. Registration only — the plane/link are deferred to <see cref="InitMapZones"/>.
    /// </summary>
    public void OnMapEntity(Entity e)
    {
        if (e.ClassName == "trigger_warpzone_position") AddMapPosition(e);
        else AddMapZone(e);
    }

    /// <summary>QC <c>spawnfunc(trigger_warpzone)</c>: register the brush (resolve its inline model for the
    /// trigger volume + surface queries). The plane/link are finalized in <see cref="InitMapZones"/>.</summary>
    public void AddMapZone(Entity brush)
    {
        // QC server.qc:662-665 — .scale defaults from .modelscale then 1.
        float scale = brush.ScaleFactor != 0f ? brush.ScaleFactor
            : (brush.ModelScale != 0f ? brush.ModelScale : 1f);
        brush.ScaleFactor = scale;

        // QC WarpZoneLib_ExactTrigger_Init (util_server.qc:8): a box trigger (no inline model) is "boxy" — Base
        // skips the exact-surface match. With an inline model, mapper-set mins/maxs ALSO mark it boxy and override
        // the model's bounds. Capture the mapper bounds BEFORE SetModel (which would replace them with model bounds).
        Vector3 mapperMins = brush.Mins, mapperMaxs = brush.Maxs;
        bool hasMapperBounds = mapperMins != Vector3.Zero || mapperMaxs != Vector3.Zero;
        bool hasModel = !string.IsNullOrEmpty(brush.Model);

        if (Api.Services is not null && hasModel)
            Api.Entities.SetModel(brush, brush.Model); // real "*N" bounds (touch volume) + findable surfaces

        bool isBoxy;
        if (!hasModel)
            isBoxy = true; // QC: no model -> box trigger
        else if (hasMapperBounds)
        {
            // QC: mapper mins/maxs override the model bounds + mark boxy.
            brush.Mins = mapperMins; brush.Maxs = mapperMaxs;
            isBoxy = true;
        }
        else isBoxy = false;

        // QC util_server.qc:40 — size the trigger by .scale (mins*scale .. maxs*scale).
        if (scale != 1f && scale != 0f)
        {
            Vector3 sMins = brush.Mins * scale, sMaxs = brush.Maxs * scale;
            if (Api.Services is not null) Api.Entities.SetSize(brush, sMins, sMaxs);
            else { brush.Mins = sMins; brush.Maxs = sMaxs; brush.Size = sMaxs - sMins; }
        }

        brush.WarpzoneScale = scale;
        brush.WarpzoneIsBoxy = isBoxy;
        _pendingZones.Add(brush);
    }

    /// <summary>QC <c>spawnfunc(trigger_warpzone_position)</c>: the explicit-orientation helper; indexed so a
    /// zone can find it by targetname, stashed so <see cref="InitMapZones"/> can attach it as a zone's aiment.</summary>
    public void AddMapPosition(Entity pos)
    {
        MapMover.IndexRegister(pos);
        _pendingPositions.Add(pos);
    }

    /// <summary>
    /// QC <c>WarpZone_StartFrame</c> init (deferred to after every entity spawned): for each registered zone,
    /// resolve its explicit orientation (.aiment) — via <c>killtarget</c> (the zone names a target_position) or a
    /// <c>trigger_warpzone_position</c> whose <c>.target</c> names the zone — derive the IN plane from the brush
    /// geometry (<see cref="DerivePlaneFromBrush"/> / <c>getsurface*</c>), build the touch volume, then link the
    /// pairs (<see cref="Link"/>). Run once after <c>SpawnMapEntities</c>; clears the pending sets.
    /// </summary>
    public void InitMapZones()
    {
        foreach (Entity zone in _pendingZones)
        {
            Entity? aiment = ResolveAiment(zone);
            SpawnFromBrush(zone, zone.TargetName, zone.Target, aiment);
        }
        Link();
        SeedMovingSnapshots();
        _pendingZones.Clear();
        _pendingPositions.Clear();
    }

    /// <summary>
    /// QC <c>WarpZone_Think</c> snapshot seed (server.qc:716-728 warpzone_save_*): record each MOVING zone's brush
    /// origin/angles and its partner's, so <see cref="ThinkMovingZones"/> only re-derives when something actually
    /// moved. Run once after the initial link + after any reconnect.
    /// </summary>
    private void SeedMovingSnapshots()
    {
        foreach (Warpzone wz in _zones)
        {
            if (!wz.Moving || wz.Trigger is null) continue;
            wz.SaveOrigin = wz.Trigger.Origin;
            wz.SaveAngles = wz.Trigger.Angles;
            wz.SaveEOrigin = wz.Partner?.Trigger?.Origin ?? Vector3.Zero;
            wz.SaveEAngles = wz.Partner?.Trigger?.Angles ?? Vector3.Zero;
        }
    }

    /// <summary>
    /// QC <c>WarpZone_Think</c> (server.qc:714-731): for every MOVING warpzone (spawnflag 1), re-derive the IN plane
    /// + transform whenever the zone or its partner brush has moved/rotated since the last derivation. Call once per
    /// server frame (it self-gates on the warpzone_save_* snapshot so a static map costs nothing). A no-op when the
    /// map has no moving warpzones.
    /// </summary>
    public void ThinkMovingZones()
    {
        for (int i = 0; i < _zones.Count; i++)
        {
            Warpzone wz = _zones[i];
            if (!wz.Moving || wz.Trigger is null) continue;
            Entity? e = wz.Partner?.Trigger;
            bool moved = wz.Trigger.Origin != wz.SaveOrigin
                || wz.Trigger.Angles != wz.SaveAngles
                || (e?.Origin ?? Vector3.Zero) != wz.SaveEOrigin
                || (e?.Angles ?? Vector3.Zero) != wz.SaveEAngles;
            if (!moved) continue;

            // QC: WarpZone_InitStep_UpdateTransform(this) + (this.enemy), then FinalizeTransform both.
            RederivePlane(wz);
            if (wz.Partner is { } partner) RederivePlane(partner);
            if (wz.Partner is { } p2)
            {
                LinkOneWay(wz, p2);
                LinkOneWay(p2, wz);
            }
            wz.SaveOrigin = wz.Trigger.Origin;
            wz.SaveAngles = wz.Trigger.Angles;
            wz.SaveEOrigin = e?.Origin ?? Vector3.Zero;
            wz.SaveEAngles = e?.Angles ?? Vector3.Zero;
        }
    }

    /// <summary>QC <c>WarpZone_InitStep_UpdateTransform</c> re-run for an already-spawned zone: re-derive its IN
    /// plane (origin/angles) from the current brush geometry + aiment. Keeps the zone if the brush is non-planar.</summary>
    private void RederivePlane(Warpzone wz)
    {
        if (wz.Trigger is null) return;
        (Vector3 org, Vector3 ang, bool ok) = DerivePlaneFromBrush(wz.Trigger, wz.Aiment);
        if (!ok) return;
        wz.InOrigin = org;
        wz.InAngles = ang;
        if (Api.Services is not null && wz.Trigger is { IsFreed: false } trig)
            Api.Entities.SetOrigin(trig, org);
    }

    /// <summary>
    /// QC <c>WarpZones_Reconnect</c> (server.qc:702-712): clear every zone's partner link and re-pair from current
    /// targets — used by the runtime <c>trigger_warpzone_reconnect</c>. With no filter every zone is re-derived +
    /// relinked. Run after any zone is added/removed/retargeted at runtime.
    /// </summary>
    public void Reconnect()
    {
        // Porto-weapon portals (Owner != null) are NOT map warpzones and must keep their existing two-way link — a
        // runtime reconnect re-derives only the map's trigger_warpzone brushes.
        foreach (Warpzone wz in _zones)
            if (wz.Owner is null) { wz.Partner = null; wz.Transform = default; RederivePlane(wz); }
        Link();
        SeedMovingSnapshots();
    }

    /// <summary>
    /// QC <c>trigger_warpzone_reconnect_use</c> (server.qc:785-805): re-derive + re-link only the zones matching the
    /// reconnect entity's <see cref="Entity.Target"/> (empty = all). Spawnflag bit 1 ("don't reconnect a zone a
    /// player can currently see") is honoured only insofar as we have no client-visibility test here — the
    /// conservative port reconnects regardless (the QC visibility skip is a cosmetic seam-pop avoidance, not a
    /// correctness gate). Wire to <see cref="WarpzoneSpawns.ReconnectSink"/>.
    /// </summary>
    public void OnReconnectUse(Entity reconnect) => ReconnectByTarget(reconnect.Target);

    /// <summary>Re-derive + re-link the zones whose <see cref="Warpzone.Target"/> matches <paramref name="target"/>
    /// (null/empty = every zone), then their partners (QC matches for .target, server.qc:790).</summary>
    public void ReconnectByTarget(string? target)
    {
        bool all = string.IsNullOrEmpty(target);
        // Collect the zones to reconnect (matching .target) + their existing partners (server.qc:803 e.enemy too).
        var touched = new HashSet<Warpzone>();
        foreach (Warpzone wz in _zones)
            if (wz.Owner is null && (all || wz.Target == target)) // skip Porto portals (keep their link)
            {
                touched.Add(wz);
                if (wz.Partner is { Owner: null } p) touched.Add(p);
            }
        if (touched.Count == 0) return;

        foreach (Warpzone wz in touched) { wz.Partner = null; wz.Transform = default; RederivePlane(wz); }
        Link();              // re-pair (a no-touched zone keeps its existing link; Link is idempotent on Linked ones)
        SeedMovingSnapshots();
    }

    /// <summary>
    /// QC <c>WarpZone_StartFrame</c> per-frame observer pass (server.qc:751-776): an OBSERVER / SOLID_NOT client is
    /// not caught by the touch-trigger pass (a SOLID_NOT mover fires no touches), so the engine warps them through a
    /// zone manually each frame — <c>WarpZone_Find</c> the overlapping zone, plane-side gate, then
    /// <c>WarpZone_Teleport(e, -1, 0)</c> WITHOUT firing the zone's targets (a spectator passing through must not
    /// trip relays). Call once per server frame with the connected clients; a no-op on a map with no warpzones.
    /// </summary>
    public void WarpObservers(IReadOnlyList<Entity> clients)
    {
        if (_zones.Count == 0) return;
        for (int i = 0; i < clients.Count; i++)
        {
            Entity it = clients[i];
            if (it.IsFreed) continue;
            // QC: IS_OBSERVER(it) || it.solid == SOLID_NOT — observers and non-solid (e.g. dead/respawning) clients.
            bool nonSolid = it.Solid == Solid.Not;
            if (!(nonSolid || (it is Player { IsObserver: true }))) continue;

            Warpzone? wz = FindOverlapping(it);
            if (wz is null || !wz.Linked) continue;
            // QC: WarpZone_PlaneDist(e, origin + view_ofs) <= 0 → on/past the plane.
            Vector3 point = it.Origin + it.ViewOfs;
            if (Vector3.Dot(point - wz.Transform.InOrigin, wz.Transform.InForward) > 0f) continue;
            TeleportNoTargets(it, wz); // NOT firing targets (server.qc:765)
        }
    }

    /// <summary>QC <c>WarpZone_Find</c> (common.qc): the warpzone whose trigger box overlaps the entity's box, or
    /// null. Used by the per-frame observer warp (the touch pass can't catch a SOLID_NOT mover).</summary>
    private Warpzone? FindOverlapping(Entity e)
    {
        Vector3 mn = e.Origin + e.Mins, mx = e.Origin + e.Maxs;
        foreach (Warpzone wz in _zones)
        {
            if (wz.Trigger is not { IsFreed: false } t) continue;
            if (mn.X <= t.AbsMax.X && mx.X >= t.AbsMin.X
                && mn.Y <= t.AbsMax.Y && mx.Y >= t.AbsMin.Y
                && mn.Z <= t.AbsMax.Z && mx.Z >= t.AbsMin.Z)
                return wz;
        }
        return null;
    }

    /// <summary>
    /// The observer/SOLID_NOT crossing (QC <c>WarpZone_Teleport(e, -1, 0)</c>): warp origin/velocity/angles through
    /// the seam exactly like <see cref="Teleport"/> but WITHOUT the target relay or the kill-credit (a spectator
    /// passing through is invisible to the map's logic). The same-frame finishtime guard still applies so the
    /// observer doesn't ping-pong between the paired zones in one frame.
    /// </summary>
    private void TeleportNoTargets(Entity e, Warpzone wz)
    {
        float now = MapMover.Now();
        if (_teleportFinishTime.TryGetValue(e, out float finish))
        {
            if (e.IsFreed) { _teleportFinishTime.Remove(e); }
            else if (now <= finish) return;
        }
        // QC WarpZone_Teleport (server.qc:84/88/137): the transform is applied to the EYE point (`o0 = origin +
        // view_ofs`) and the entity is placed at `o1 - view_ofs` — view_ofs is a local upright offset that does NOT
        // rotate with the crossing. Identical to transforming the origin for an upright zone pair (R·view_ofs ==
        // view_ofs) and degenerate for non-clients (view_ofs '0 0 0'), but a TILTED zone warps the eye, not the feet.
        Vector3 newOrigin = wz.Transform.TransformOrigin(e.Origin + e.ViewOfs) - e.ViewOfs;
        e.Velocity = wz.Transform.TransformVelocity(e.Velocity);
        e.Angles = wz.Transform.TransformAngles(e.Angles);
        // QC WarpZone_TeleportPlayer fixangle — a free-flying observer has a view too; snap it to the exit facing
        // authoritatively (same channel the host reads). See the note in Teleport for why the predictor's one-shot
        // snap is not reliable on its own.
        if ((e.Flags & EntFlags.Client) != 0)
        {
            e.FixAngle = true;
            e.FixAngleAngles = e.Angles;
            e.ViewAngles = e.Angles; // QC server.qc:54-58 — the bot aim conduit (see Teleport); human-harmless
        }
        e.AVelocity = wz.Transform.TransformVelocity(e.AVelocity);
        // QC WarpZone_TeleportPlayer (server.qc:46-47) teleport bookkeeping (pre-warp origin + crossing time).
        e.LastTeleportOrigin = e.Origin;
        e.LastTeleportTime = now;
        if (Api.Services is not null) Api.Entities.SetOrigin(e, newOrigin);
        else e.Origin = newOrigin;
        e.OldOrigin = newOrigin;
        e.Effects ^= EffectFlags.Teleport; // QC EF_TELEPORT_BIT — cancel client interp across the seam
        _teleportFinishTime[e] = now + MapMover.FrameTime();
    }

    /// <summary>The explicit-orientation entity for a zone (QC WarpZone_InitStep_FindOriginTarget + the
    /// trigger_warpzone_position reverse-attach), or null to orient purely from the brush plane.</summary>
    private Entity? ResolveAiment(Entity zone)
    {
        // mechanism 1 — killtarget: the zone names a target_position direction arrow (QC clears killtarget after).
        if (!string.IsNullOrEmpty(zone.KillTarget))
        {
            Entity? found = MapMover.FindFirstByTargetName(zone.KillTarget);
            zone.KillTarget = "";
            if (found is not null) return found;
        }
        // mechanism 2 — a trigger_warpzone_position whose .target names this zone.
        if (!string.IsNullOrEmpty(zone.TargetName))
            foreach (Entity pos in _pendingPositions)
                if (pos.Target == zone.TargetName) return pos;
        return null;
    }
}

/// <summary>
/// The map-entity spawnfuncs for warpzones (QC <c>spawnfunc(trigger_warpzone)</c> /
/// <c>spawnfunc(trigger_warpzone_position)</c>). Stateless like every map-object spawnfunc, so they reach the
/// match's instance <see cref="WarpzoneManager"/> through the static <see cref="Sink"/> — wired by the host's
/// Boot to <see cref="WarpzoneManager.OnMapEntity"/>, exactly as <c>Porto.PortalSpawner</c> bridges the Porto
/// weapon to the same manager.
/// </summary>
public static class WarpzoneSpawns
{
    /// <summary>Route a spawned warpzone edict to this match's manager. Null = no manager (e.g. unit tests).</summary>
    public static System.Action<Entity>? Sink;

    public static void TriggerWarpzoneSetup(Entity e) { e.ClassName = "trigger_warpzone"; Sink?.Invoke(e); }
    public static void TriggerWarpzonePositionSetup(Entity e) { e.ClassName = "trigger_warpzone_position"; Sink?.Invoke(e); }

    /// <summary>
    /// QC <c>spawnfunc(misc_warpzone_position)</c> (server.qc:642) — the CANONICAL position spawnfunc.
    /// <c>spawnfunc(trigger_warpzone_position)</c> (server.qc:648) merely delegates to it, so a map authored with the
    /// bare <c>misc_warpzone_position</c> name carries an identical explicit-orientation helper. Routed to the
    /// manager as a <c>trigger_warpzone_position</c> (same handling), so its orientation is no longer silently dropped.
    /// </summary>
    public static void MiscWarpzonePositionSetup(Entity e) { e.ClassName = "trigger_warpzone_position"; Sink?.Invoke(e); }

    /// <summary>
    /// QC <c>spawnfunc(trigger_warpzone_reconnect)</c> / <c>spawnfunc(target_warpzone_reconnect)</c>
    /// (server.qc:807-815): a triggerable entity that, when fired, re-derives + re-links the world's warpzones at
    /// runtime (<see cref="WarpzoneManager.ReconnectByTarget"/>). Used by maps with moving / runtime-rewired
    /// warpzones. The <c>use</c> handler reads this entity's <see cref="Entity.Target"/> (which zones to reconnect,
    /// empty = all) and <see cref="Entity.SpawnFlags"/> bit 1 (skip zones currently visible to a client).
    /// </summary>
    public static void TriggerWarpzoneReconnectSetup(Entity e)
    {
        e.Use = (self, _) => ReconnectSink?.Invoke(self);
    }

    /// <summary>Host bridge for the reconnect <c>use</c> (mirrors <see cref="Sink"/>): wired to
    /// <see cref="WarpzoneManager.OnReconnectUse"/>. Null = no manager (unit tests without a host).</summary>
    public static System.Action<Entity>? ReconnectSink;

    /// <summary>
    /// QC <c>spawnfunc(func_camera)</c> / <c>spawnfunc(func_warpzone_camera)</c> (lib/warpzone/server.qc): a
    /// map-placed security camera. The camera entity resolves its <see cref="Entity.Target"/> (a
    /// <c>target_position</c> / <c>info_notnull</c> aim point), stores its camera POSE (origin + the angles that
    /// look from the camera toward that target, exactly as <c>info_autoscreenshot</c> aims at its target), becomes a
    /// non-moving trigger volume, and — on a CLIENT viewer crossing the trigger — stamps the viewer's
    /// AUTHORITATIVE <see cref="Entity.FixAngle"/>/<see cref="Entity.FixAngleAngles"/>: the SAME view-orient channel
    /// the warpzone owner block uses (<see cref="WarpzoneManager.Teleport"/>) and that <c>NetGame</c> consumes
    /// (ConsumePredictedFixAngle). This closes the AUTHORITY half of <c>warpzones.camera</c> with no new wire
    /// fields. The see-through camera-surface RENDER (the <c>dpcamera</c> portal view) stays delegated to
    /// <c>PortalRenderer</c> on the listen host — out of scope here, consistent with the partial render liveness.
    ///
    /// The target is resolved lazily on the FIRST touch as well as at setup, because the <c>target_position</c> aim
    /// point may not have spawned yet when the camera's spawnfunc runs (the BSP entity lump spawns in file order, so
    /// a forward target reference is common — mirrors <c>info_autoscreenshot</c>'s deferred find-target pass).
    /// </summary>
    public static void FuncCameraSetup(Entity e)
    {
        e.ClassName = "func_camera";

        // The camera presents a touchable view volume but never moves on its own (QC SOLID_TRIGGER / MOVETYPE_NONE).
        e.Solid = Solid.Trigger;
        e.MoveType = MoveType.None;

        // The camera POSE (origin + the angles looking from the camera toward its target) is held in the touch
        // closure rather than on the edict — there is no networked camera-pose field, and none is needed (the pose
        // is pure server-side AUTHORITY that drives the existing FixAngle/FixAngleAngles channel). Resolve the aim
        // target now if it already exists; otherwise the Touch handler resolves it lazily on the first crossing,
        // because the target_position aim point may not have spawned yet when this spawnfunc runs (BSP lump order —
        // mirrors info_autoscreenshot's deferred find-target pass).
        bool posed = TryResolveCameraPose(e, out Vector3 cameraAngles);

        e.Touch = (self, other) =>
        {
            if (other.IsFreed || other.Solid == Solid.Not) return;
            // Only a CLIENT viewer's view is re-oriented (the same gate the warpzone teleport uses); a projectile or
            // item crossing the camera volume is unaffected.
            if ((other.Flags & EntFlags.Client) == 0) return;

            // Lazy resolution: if the aim target hadn't spawned at setup time, resolve it on the first crossing.
            if (!posed)
            {
                posed = TryResolveCameraPose(self, out cameraAngles);
                if (!posed) return; // misconfigured camera (no resolvable target) — nothing to orient to
            }

            // Stamp the AUTHORITATIVE fixed-view channel — the host applies these angles verbatim (NetGame reads
            // LocalServerPlayer.FixAngle / ConsumePredictedFixAngle), snapping the viewer to the camera's facing.
            other.FixAngle = true;
            other.FixAngleAngles = cameraAngles;
        };

        // Route to the match manager (mirrors the other warpzone spawnfuncs) so it participates on the normal path.
        Sink?.Invoke(e);
    }

    /// <summary>
    /// Resolve the camera's <see cref="Entity.Target"/> aim point and compute the camera POSE angles — the angles
    /// looking from the camera toward the target (QC <c>info_autoscreenshot</c> aim: <c>vectoangles(target − origin)</c>
    /// with the pitch flip). Falls back to the target's own explicit angles when the camera sits AT its target
    /// (degenerate aim vector). Returns false (with <paramref name="angles"/> = default) when no target resolves yet.
    /// </summary>
    private static bool TryResolveCameraPose(Entity e, out Vector3 angles)
    {
        angles = default;
        Entity? target = !string.IsNullOrEmpty(e.Target) ? MapMover.FindFirstByTargetName(e.Target) : null;
        if (target is null) return false;

        Vector3 dir = target.Origin - e.Origin;
        if (dir.LengthSquared() > 0.0001f)
        {
            // QC info_autoscreenshot_findtarget: a = vectoangles(target - origin); a.x = -a.x (the pitch flip that
            // matches the fixedvectoangles convention the view consumer expects).
            angles = QMath.VecToAngles(dir);
            angles.X = -angles.X;
            angles.Z = e.Angles.Z; // preserve any mapper roll on the camera body
        }
        else
        {
            // Camera coincident with its target (or a self-aimed dpcamera): use the target's explicit orientation.
            angles = target.Angles;
        }
        return true;
    }
}
