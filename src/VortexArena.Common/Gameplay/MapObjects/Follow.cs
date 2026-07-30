// Port of qcsrc/common/mapobjects/misc/follow.qc (misc_follow) — SVQC half.
//
// misc_follow attaches one map entity to another at INITPRIO_FINDTARGET. It finds a SOURCE (the entity to
// follow / attach to, by .killtarget) and a DESTINATION (the entity that follows / attaches, by .target),
// then — depending on spawnflags — either parents dst to src with setattachment (FOLLOW_ATTACH) or makes dst
// ride src via MOVETYPE_FOLLOW (no flag). FOLLOW_LOCAL chooses the "local" variant of each. The misc_follow
// edict itself is deleted afterward (so it is useless to CSQC, as the QC header notes), UNLESS .jointtype is
// set (then it persists carrying the src/dst it joined).
//
// Port notes:
//  * follow_sameorigin (util.qc:2110) is the MOVETYPE_FOLLOW + relative origin/angle setup — ported faithfully
//    (it is exactly dynlight_find_aiment's setup, the gameplay-meaningful half).
//  * attach_sameorigin (util.qc:2057) additionally bakes a tag-relative transform from gettaginfo/gettagindex
//    + AnglesTransform; that math is a RENDER-attachment detail (tag-space basis change). The server-meaningful
//    effect is the parent link, so the port issues SetAttachment(dst, src, tag) and documents the tag-transform
//    bake as a follow-up. setattachment(dst, src, message) (FOLLOW_LOCAL) is the same SetAttachment call.
//  * The dst.solid = SOLID_NOT line (attachment can't be solid) is mirrored exactly.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Framework
{
    public partial class Entity
    {
        /// <summary>
        /// QC <c>.v_angle</c> as the MOVETYPE_FOLLOW relative-angle bookkeeping (follow_sameorigin /
        /// dynlight_find_aiment): the followed entity's angle offset, stored so the follower's world angles can
        /// be recomputed each frame. Shared by misc_follow and dynlight (same QC field).
        /// </summary>
        public Vector3 VAngle;

        /// <summary>QC <c>.jointtype</c> — misc_follow joint kind; when set the misc_follow edict persists
        /// (carrying its src in .aiment and dst in .enemy) instead of deleting itself.</summary>
        public float JointType;
    }
}

namespace XonoticGodot.Common.Gameplay
{
    using XonoticGodot.Common.Framework;

    /// <summary><c>misc_follow</c> — attach/follow one entity to another at spawn. Registered by <see cref="MapObjectsRegistry"/>.</summary>
    public static class Follow
    {
        // ---- spawnflags (follow.qh:4-5) ----
        public const int Attach = 1 << 0; // FOLLOW_ATTACH — parent via setattachment (else MOVETYPE_FOLLOW)
        public const int Local = 1 << 1;  // FOLLOW_LOCAL  — the "local"/same-origin variant

        // The INITPRIO_FINDTARGET deferred-resolve queue (drained by MapObjectsRegistry.RunPostSpawn).
        private static readonly List<Entity> _pendingInit = new();

        /// <summary><c>spawnfunc(misc_follow)</c> (follow.qc:66-69): defer the attach to INITPRIO_FINDTARGET.</summary>
        public static void FollowSetup(Entity this_)
        {
            this_.ClassName = "misc_follow";
            // QC: InitializeEntity(this, follow_init, INITPRIO_FINDTARGET);
            if (!_pendingInit.Contains(this_))
                _pendingInit.Add(this_);
        }

        /// <summary>
        /// Resolve every queued misc_follow — the headless analogue of QC's INITPRIO_FINDTARGET pass (run by
        /// <see cref="MapObjectsRegistry.RunPostSpawn"/> after the door-link pass). Safe to call repeatedly.
        /// </summary>
        public static void RunDeferredInit()
        {
            if (_pendingInit.Count == 0)
                return;
            Entity[] batch = _pendingInit.ToArray();
            _pendingInit.Clear();
            foreach (Entity e in batch)
                if (!e.IsFreed)
                    FollowInit(e);
        }

        /// <summary>Port of <c>follow_init</c> (follow.qc:5-64).</summary>
        private static void FollowInit(Entity this_)
        {
            // QC: src = find(targetname, killtarget); dst = find(targetname, target);
            Entity? src = !string.IsNullOrEmpty(this_.KillTarget) ? MapMover.FindFirstByTargetName(this_.KillTarget) : null;
            Entity? dst = !string.IsNullOrEmpty(this_.Target) ? MapMover.FindFirstByTargetName(this_.Target) : null;

            // QC: if(!src && !dst) { objerror(...); return; }
            if (src is null && dst is null)
                return;

            if (this_.JointType != 0f)
            {
                // QC: a joint — the misc_follow edict must STAY, carrying src in .aiment and dst in .enemy.
                this_.Aiment = src;
                this_.Enemy = dst;
                return;
            }

            // QC: else if(!src || !dst) { objerror(...); return; }
            if (src is null || dst is null)
                return;

            if ((this_.SpawnFlags & Attach) != 0)
            {
                // attach (follow.qc:32-46)
                if ((this_.SpawnFlags & Local) != 0)
                    SetAttachment(dst, src, this_.Message);      // QC: setattachment(dst, src, this.message);
                else
                    AttachSameOrigin(dst, src, this_.Message);   // QC: attach_sameorigin(dst, src, this.message);

                dst.Solid = Solid.Not; // QC: dst.solid = SOLID_NOT; // solid doesn't work with attachment
                MapMover.RemoveEntity(this_); // QC: delete(this);
            }
            else
            {
                // follow (follow.qc:47-63)
                if ((this_.SpawnFlags & Local) != 0)
                {
                    // QC: set_movetype(dst, MOVETYPE_FOLLOW); dst.aiment = src;
                    //     dst.view_ofs = dst.origin; dst.v_angle = dst.angles;
                    dst.MoveType = MoveType.Follow;
                    dst.Aiment = src;
                    // dst.punchangle left unchanged (QC keeps it)
                    dst.ViewOfs = dst.Origin;
                    dst.VAngle = dst.Angles;
                }
                else
                {
                    FollowSameOrigin(dst, src); // QC: follow_sameorigin(dst, src);
                }

                MapMover.RemoveEntity(this_); // QC: delete(this);
            }
        }

        /// <summary>Port of <c>follow_sameorigin</c> (util.qc:2110-2117): MOVETYPE_FOLLOW + relative origin/angles.</summary>
        private static void FollowSameOrigin(Entity e, Entity to)
        {
            e.MoveType = MoveType.Follow;        // QC: set_movetype(e, MOVETYPE_FOLLOW);
            e.Aiment = to;                       // QC: e.aiment = to;
            e.PunchAngle = to.Angles;            // QC: e.punchangle = to.angles;
            e.ViewOfs = e.Origin - to.Origin;    // QC: e.view_ofs = e.origin - to.origin;
            e.VAngle = e.Angles - to.Angles;     // QC: e.v_angle = e.angles - to.angles;
        }

        /// <summary>
        /// Port of <c>attach_sameorigin</c> (util.qc:2057-2095): bake the entity's origin/angles into the parent
        /// tag's local space so that, once attached, it keeps its authored world pose. Needs the tag world-pose
        /// from the model service (QC's gettaginfo/gettagindex); when no model service is present (headless tests)
        /// or the tag can't be resolved, fall back to the bare parent link — the server-meaningful contract.
        /// </summary>
        private static void AttachSameOrigin(Entity e, Entity to, string tag)
        {
            tag ??= "";

            // QC: org = e.origin - gettaginfo(to, gettagindex(to, tag));  — v_forward/right/up = the tag basis.
            if (Api.Services is null ||
                !Api.Models.TryGetTag(to, tag, out Vector3 tagOrigin, out Vector3 vForward, out Vector3 vRight, out Vector3 vUp))
            {
                SetAttachment(e, to, tag);
                return;
            }

            Vector3 org = e.Origin - tagOrigin;

            // QC: tagscale = vlen(v_forward) ** -2;  t_forward = v_forward*tagscale; t_left = v_right*-tagscale; t_up = v_up*tagscale;
            float flen = vForward.Length();
            float tagscale = flen != 0f ? 1f / (flen * flen) : 0f; // undo a scale on the tag
            Vector3 tForward = vForward * tagscale;
            Vector3 tLeft = vRight * -tagscale;
            Vector3 tUp = vUp * tagscale;

            // QC: e.origin = (org·t_forward, org·t_left, org·t_up)  — tag-local origin.
            e.Origin = new Vector3(
                QMath.Dot(org, tForward),
                QMath.Dot(org, tLeft),
                QMath.Dot(org, tUp));

            // QC: e.angles = AnglesTransform_FromAngles(e.angles); fixedmakevectors(e.angles);
            // fixedmakevectors == makevectors with pitch negated (POSITIVE_PITCH_IS_DOWN); the matching pitch flip
            // is undone by FixedVecToAngles2 below, so the From/To*Angles round trip stays the identity convention.
            Vector3 fixedAngles = new Vector3(-e.Angles.X, e.Angles.Y, e.Angles.Z);
            QMath.AngleVectors(fixedAngles, out Vector3 eFwd, out _, out Vector3 eUp);

            Vector3 eForward = new Vector3(
                QMath.Dot(eFwd, tForward),
                QMath.Dot(eFwd, tLeft),
                QMath.Dot(eFwd, tUp));
            Vector3 eUpOut = new Vector3(
                QMath.Dot(eUp, tForward),
                QMath.Dot(eUp, tLeft),
                QMath.Dot(eUp, tUp));

            e.Angles = QMath.FixedVecToAngles2(eForward, eUpOut); // QC: e.angles = fixedvectoangles2(e_forward, e_up);

            SetAttachment(e, to, tag);  // QC: setattachment(e, to, tag); setorigin(e, e.origin);
        }

        /// <summary>setattachment through the model facade when present (headless tests have no model service).</summary>
        private static void SetAttachment(Entity e, Entity to, string tag)
        {
            if (Api.Services is not null)
                Api.Models.SetAttachment(e, to, tag ?? "");
        }
    }
}
