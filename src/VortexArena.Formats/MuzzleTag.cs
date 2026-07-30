using System;
using System.Numerics;
using XonoticGodot.Formats.Dpm;
using XonoticGodot.Formats.Iqm;
using XonoticGodot.Formats.Md3;

namespace XonoticGodot.Formats;

/// <summary>
/// Computes a weapon's per-model shot origin (QC <c>movedir</c>) in <b>model-LOCAL Quake coordinates</b>
/// (x = forward, y = +left, z = up) — the C# successor to Base's
/// <c>CL_WeaponEntity_SetModel</c> (qcsrc/common/weapons/all.qc:341-528). That offset becomes
/// <c>w_shotorg</c>'s muzzle slide in <c>W_SetupShot</c>.
///
/// <para><b>Base's exact selection + fallback order (all.qc:367-424), which this reproduces:</b>
/// <list type="number">
///   <item>load the <c>v_&lt;name&gt;.md3</c> VISUAL model and capture its <c>shot</c>/<c>tag_shot</c> index
///         (<c>v_shot_idx</c>, all.qc:369);</item>
///   <item>load the <c>h_&lt;name&gt;.iqm</c> HAND RIG and, if it exposes a <c>weapon</c>/<c>tag_weapon</c>
///         socket, attach the v_ model to it as the <c>weaponchild</c> (all.qc:381-394);</item>
///   <item><b>if the v_ model HAS a shot tag</b> (<c>v_shot_idx != 0</c>) →
///         <c>movedir = gettaginfo(weaponchild, v_shot_idx)</c> (all.qc:411-412): the v_ model's shot tag
///         transformed THROUGH the h_ rig's weapon-attach bone's FULL rest transform (rotation AND
///         translation), expressed in the weapon entity's local frame (entity at origin/angles 0);</item>
///   <item><b>else if the h_ rig has its own shot tag</b> → <c>movedir = gettaginfo(this=h_rig, idx)</c>
///         (all.qc:413-417): the h_ rig's own <c>tag_shot</c> model-space rest position;</item>
///   <item>else → <c>'0 0 0'</c> (all.qc:418-423, "shots TOTALLY wrong" warning) — the port returns null so the
///         caller keeps the generic muzzle fallback.</item>
/// </list></para>
///
/// <para><b>Shipped-content note (verified 2026-06-08 against every weapon under
/// <c>models/weapons</c>):</b> all 27 <c>v_*</c> visual models carry ZERO shot tags (the MD3 ones have
/// <c>num_tags == 0</c>; several "<c>.md3</c>" files are actually tag-less single-bone IQM/DPM). So
/// <c>v_shot_idx</c> is always 0 and Base — and therefore this port — always takes branch (4): the h_ rig's
/// own <c>tag_shot</c>. Branch (3) (the v_-shot-through-h_-weapon-bone composition) is faithful and kept for
/// any future weapon that ships a v_ shot tag, but is presently a no-op for stock content.</para>
///
/// <para>This is a pure parsed-data computation (no Godot, no render pipeline): for MD3 a tag stores its full
/// model-local frame (origin + 3x3 axes); for the skeletal IQM/DPM rigs a bone's model-local rest frame is the
/// parent chain composed (parents stored first). The result feeds <c>WeaponFiring.SetupShot</c>'s
/// <c>right*(-md.y) + up*md.z</c> / forward-by-<c>md.x</c> formula with NO conversion.</para>
/// </summary>
public static class MuzzleTag
{
    /// <summary>The tag/bone names a weapon muzzle can live under, in Base's resolution order (all.qc:369,416).</summary>
    private static readonly string[] ShotTagNames = { "shot", "tag_shot" };

    /// <summary>The attach socket the v_ model rides on, in Base's resolution order (setattachment …, all.qc:381).</summary>
    private static readonly string[] AttachBoneNames = { "weapon", "tag_weapon" };

    // =============================================================================================
    //  Single-model shot tag — Base's movedir when the model carries its own shot tag.
    //  (For the h_ rig this is branch (4); these overloads are also the building block of ComputeShotOrigin.)
    // =============================================================================================

    /// <summary>MD3: the tag origin is stored raw in model-local Quake coords (frame 0 = bind pose).</summary>
    public static Vector3? Extract(Md3Data md3) => ShotTagFrame(md3)?.Translation;

    /// <summary>
    /// IQM: resolve the bind-pose model-local translation of the shot bone by composing its rest TRS chain
    /// up the parent hierarchy (joints are stored parent-first, parent index &lt; child index).
    /// </summary>
    public static Vector3? Extract(IqmData iqm) => ShotTagFrame(iqm)?.Translation;

    /// <summary>
    /// DPM: resolve the bind-pose (frame 0) model-local translation of the shot bone by composing its
    /// parent-relative 3x4 pose chain up the hierarchy (bones are stored parent-first).
    /// </summary>
    public static Vector3? Extract(DpmData dpm) => ShotTagFrame(dpm)?.Translation;

    // =============================================================================================
    //  Base-faithful composition — movedir = (h_ weapon-attach bone rest) o (v_ shot tag), with the
    //  exact same v_-then-h_ branch order as CL_WeaponEntity_SetModel.
    // =============================================================================================

    /// <summary>
    /// A parsed weapon model whose format (MD3 / IQM / DPM) is unknown to the caller — exactly one field is
    /// set. Lets <see cref="ComputeShotOrigin"/> take the v_ visual model and h_ hand rig already magic-
    /// dispatched on the Godot side, while keeping this assembly Godot-free. Use the <see cref="Of"/> helpers.
    /// </summary>
    public readonly struct Model
    {
        private readonly Md3Data? _md3;
        private readonly IqmData? _iqm;
        private readonly DpmData? _dpm;

        private Model(Md3Data? md3, IqmData? iqm, DpmData? dpm) { _md3 = md3; _iqm = iqm; _dpm = dpm; }

        /// <summary>Wrap a parsed MD3 (or an empty model for a null/unparsable file).</summary>
        public static Model Of(Md3Data? md3) => new(md3, null, null);
        /// <summary>Wrap a parsed IQM.</summary>
        public static Model Of(IqmData? iqm) => new(null, iqm, null);
        /// <summary>Wrap a parsed DPM.</summary>
        public static Model Of(DpmData? dpm) => new(null, null, dpm);
        /// <summary>The empty model (no shot tag, no attach bone) — a missing/unknown file.</summary>
        public static Model None => default;

        /// <summary>The model's own <c>shot</c>/<c>tag_shot</c> full model-local rest frame, or null.</summary>
        internal Matrix4x4? ShotTagFrame()
            => _md3 is not null ? MuzzleTag.ShotTagFrame(_md3)
             : _iqm is not null ? MuzzleTag.ShotTagFrame(_iqm)
             : _dpm is not null ? MuzzleTag.ShotTagFrame(_dpm)
             : null;

        /// <summary>The model's <c>weapon</c>/<c>tag_weapon</c> attach-bone full model-local rest frame, or null.</summary>
        internal Matrix4x4? AttachBoneFrame()
            => _md3 is not null ? MuzzleTag.AttachBoneFrame(_md3)
             : _iqm is not null ? MuzzleTag.AttachBoneFrame(_iqm)
             : _dpm is not null ? MuzzleTag.AttachBoneFrame(_dpm)
             : null;

        /// <summary>
        /// Whether this h_ rig exposes a <c>weapon</c>/<c>tag_weapon</c> attach bone — Base's
        /// <c>gettagindex(this,"weapon")</c> test (all.qc:381-400) that decides the first-person render bucket:
        /// <list type="bullet">
        ///   <item><b>true (INVISIBLE-HAND, IQM rigs)</b>: Base creates a <c>weaponchild</c> and renders the
        ///   <c>v_</c> model attached to the <c>weapon</c> bone — the rig itself is a bare socket skeleton.</item>
        ///   <item><b>false (FULL-MODEL, DPM rigs)</b>: Base leaves <c>weaponchild</c> NULL and renders the
        ///   <b>h_ rig itself</b> (its own gun+hand mesh) as the viewmodel.</item>
        /// </list>
        /// </summary>
        public bool HasAttachBone() => AttachBoneFrame() is not null;
    }

    /// <summary>
    /// Classify a parsed h_ hand rig into its first-person render bucket (Base
    /// <c>CL_WeaponEntity_SetModel</c>, all.qc:381-400): a rig with a <c>weapon</c>/<c>tag_weapon</c> bone is an
    /// <b>invisible-hand</b> rig (render the <c>v_</c> model attached to that bone); a rig without one is a
    /// <b>full-model</b> rig (render the h_ rig itself). This is the single source of truth the live
    /// (<c>NetGame</c>) and demo (<c>GameDemo</c>) equip paths share. Returns <c>true</c> for invisible-hand.
    /// </summary>
    public static bool IsInvisibleHandRig(Model hRig) => hRig.HasAttachBone();

    /// <summary>
    /// Port of <c>CL_WeaponEntity_SetModel</c>'s <c>movedir</c> selection (all.qc:367-424), given the parsed
    /// <paramref name="vModel"/> (the <c>v_*</c> visual model) and <paramref name="hRig"/> (the <c>h_*</c>
    /// hand rig). Returns the shot origin in model-local Quake coords, or null when neither model carries a
    /// shot tag (Base's <c>'0 0 0'</c> case → caller keeps the generic fallback).
    ///
    /// <list type="number">
    ///   <item><b>v_ has a shot tag</b> → <c>(h_ weapon-attach bone rest) * (v_ shot-tag local)</c> — Base's
    ///         <c>gettaginfo(weaponchild, v_shot_idx)</c>, the FULL attach-bone rest (rotation + translation)
    ///         applied to the v_ shot tag's local point (all.qc:411-412). If the h_ rig has no weapon-attach
    ///         bone, Base falls to the v_ tag as-is (no weaponchild), matched here.</item>
    ///   <item><b>else the h_ rig has its own shot tag</b> → that bone's model-space rest (all.qc:413-417).</item>
    ///   <item>else → null.</item>
    /// </list>
    /// </summary>
    public static Vector3? ComputeShotOrigin(Model vModel, Model hRig)
    {
        // Branch (3): the v_ model has its own shot tag → transform it through the h_ rig's weapon-attach bone.
        if (vModel.ShotTagFrame() is { } vShot)
        {
            Vector3 vShotLocal = vShot.Translation;
            // gettaginfo(weaponchild, ...) composes the v_ shot tag through the weapon-attach bone's FULL rest
            // (rotation included), evaluated with the weapon entity at origin/angles 0 (all.qc:402-412).
            if (hRig.AttachBoneFrame() is { } attach)
                return Vector3.Transform(vShotLocal, attach);
            // No weapon-attach bone on the rig: Base does not create a weaponchild (all.qc:395-400), so the v_
            // model is the entity itself and movedir is the v_ shot tag's own model-local position.
            return vShotLocal;
        }

        // Branch (4): the v_ model has no shot tag → read the h_ rig's OWN shot tag (all.qc:413-417). This is
        // the live path for ALL stock weapons (every v_*.md3 has num_tags == 0).
        return hRig.ShotTagFrame()?.Translation;
    }

    // =============================================================================================
    //  Per-format frame extraction (full model-local rest matrix for a named tag/bone).
    // =============================================================================================

    /// <summary>MD3 shot tag → its full model-local frame (origin + 3x3 axes) at frame 0, or null.</summary>
    private static Matrix4x4? ShotTagFrame(Md3Data md3) => Md3TagFrame(md3, ShotTagNames);

    /// <summary>MD3 weapon-attach tag → its full model-local frame at frame 0, or null.</summary>
    private static Matrix4x4? AttachBoneFrame(Md3Data md3) => Md3TagFrame(md3, AttachBoneNames);

    private static Matrix4x4? Md3TagFrame(Md3Data md3, string[] names)
    {
        if (md3 is null) return null;
        foreach (string name in names)
        {
            if (md3.TagsByName.TryGetValue(name, out Md3Tag? tag) && tag.Transforms.Length > 0)
            {
                Md3TagTransform t = tag.Transforms[0];
                // MD3 stores the tag's three basis axes (forward/left/up) as the matrix columns + origin.
                return new Matrix4x4(
                    t.AxisX.X, t.AxisX.Y, t.AxisX.Z, 0f,
                    t.AxisY.X, t.AxisY.Y, t.AxisY.Z, 0f,
                    t.AxisZ.X, t.AxisZ.Y, t.AxisZ.Z, 0f,
                    t.Origin.X, t.Origin.Y, t.Origin.Z, 1f);
            }
        }
        return null;
    }

    /// <summary>IQM shot bone → its full model-local rest matrix, or null.</summary>
    private static Matrix4x4? ShotTagFrame(IqmData iqm) => IqmBoneFrame(iqm, ShotTagNames);

    /// <summary>IQM weapon-attach bone → its full model-local rest matrix, or null.</summary>
    private static Matrix4x4? AttachBoneFrame(IqmData iqm) => IqmBoneFrame(iqm, AttachBoneNames);

    private static Matrix4x4? IqmBoneFrame(IqmData iqm, string[] names)
    {
        if (iqm is null || iqm.Joints.Length == 0) return null;
        foreach (string name in names)
            for (int i = 0; i < iqm.Joints.Length; i++)
                if (NameMatches(iqm.Joints[i].Name, name))
                    return IqmBoneWorldRest(iqm, i);
        return null;
    }

    /// <summary>DPM shot bone → its full bind-frame model-local matrix, or null.</summary>
    private static Matrix4x4? ShotTagFrame(DpmData dpm) => DpmBoneFrame(dpm, ShotTagNames);

    /// <summary>DPM weapon-attach bone → its full bind-frame model-local matrix, or null.</summary>
    private static Matrix4x4? AttachBoneFrame(DpmData dpm) => DpmBoneFrame(dpm, AttachBoneNames);

    private static Matrix4x4? DpmBoneFrame(DpmData dpm, string[] names)
    {
        if (dpm is null || dpm.Bones.Length == 0 || dpm.Frames.Length == 0) return null;
        DpmFrame bind = dpm.Frames[0];
        if (bind.BonePoses.Length < dpm.Bones.Length) return null;
        foreach (string name in names)
            for (int i = 0; i < dpm.Bones.Length; i++)
                if (NameMatches(dpm.Bones[i].Name, name))
                    return DpmBoneWorldRest(dpm, bind, i);
        return null;
    }

    // ---------------------------------------------------------------------------------------------
    //  Hierarchy composition (parent-first storage → a single forward chain to world/model-local)
    // ---------------------------------------------------------------------------------------------

    /// <summary>World (model-local) rest matrix of IQM joint <paramref name="idx"/> = world(parent) * Local.</summary>
    private static Matrix4x4 IqmBoneWorldRest(IqmData iqm, int idx)
    {
        IqmJoint j = iqm.Joints[idx];
        Matrix4x4 local = Trs(j.Translate, j.Rotate, j.Scale);
        return j.Parent >= 0 ? local * IqmBoneWorldRest(iqm, j.Parent) : local;
    }

    /// <summary>World (model-local) rest matrix of DPM bone <paramref name="idx"/> at the bind frame.</summary>
    private static Matrix4x4 DpmBoneWorldRest(DpmData dpm, DpmFrame bind, int idx)
    {
        Matrix4x4 local = bind.BonePoses[idx].ToMatrix();
        int parent = dpm.Bones[idx].Parent;
        return parent >= 0 ? local * DpmBoneWorldRest(dpm, bind, parent) : local;
    }

    /// <summary>TRS compose in System.Numerics' row-vector convention (matches <c>DpmBonePose.ToMatrix</c>).</summary>
    private static Matrix4x4 Trs(Vector3 t, Quaternion r, Vector3 s)
        => Matrix4x4.CreateScale(s)
         * Matrix4x4.CreateFromQuaternion(r)
         * Matrix4x4.CreateTranslation(t);

    private static bool NameMatches(string boneName, string want)
        => string.Equals(boneName, want, StringComparison.OrdinalIgnoreCase);
}
