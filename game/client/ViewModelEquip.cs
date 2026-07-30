using Godot;
using VortexArena.Game.Loaders;            // AssetLoader
using VortexArena.Game.Loaders.Models;     // IqmBuilder

namespace VortexArena.Game.Client;

/// <summary>
/// Builds the first-person weapon view-model node for a <c>v_*</c> weapon path, faithful to Base
/// <c>CL_WeaponEntity_SetModel</c> (all.qc:367-424). The single source of truth shared by every equip path —
/// the networked <see cref="Net.NetGame"/> client feeds it the active weapon's <c>v_</c> model each switch.
///
/// <para>The value itself is the model + attach offset to hand the <see cref="ViewModel"/>, plus whether the
/// rendered node is the <c>h_</c> HAND RIG itself (full-model DPM weapons) rather than the <c>v_</c> visual
/// model (invisible-hand IQM rigs). The <see cref="Build"/> / <see cref="WeaponAttachTransform"/> statics do the
/// rig classification + node construction.</para>
/// </summary>
internal readonly struct ViewModelEquip
{
    /// <summary>The built node to render as the first-person weapon (either the v_ model or the h_ rig).</summary>
    public Node3D? Model { get; init; }
    /// <summary>The local attach transform applied to <see cref="Model"/> under the ViewBasis.</summary>
    public Transform3D Attach { get; init; }
    /// <summary>True when <see cref="Model"/> is the h_ rig itself (full-model DPM weapons); false = v_ model.</summary>
    public bool IsHandRig { get; init; }

    /// <summary>The placeholder/missing equip (no model, identity attach, v_ path).</summary>
    public static ViewModelEquip None => new() { Model = null, Attach = Transform3D.Identity, IsHandRig = false };

    /// <summary>
    /// Build the first-person weapon node for a <c>v_*</c> model path, faithful to Base
    /// <c>CL_WeaponEntity_SetModel</c> (all.qc:367-424). The branch is keyed off the sibling <c>h_*</c> HAND
    /// RIG's bones:
    /// <list type="number">
    ///   <item><b>INVISIBLE-HAND</b> (the h_ rig exposes a <c>weapon</c>/<c>tag_weapon</c> bone — the IQM rigs
    ///   h_arc/h_nex/h_shotgun/…): render the <c>v_</c> VISUAL model attached to that bone's rest (position-only,
    ///   the legacy path), exactly as Base attaches the v_ <c>weaponchild</c> to the <c>weapon</c> bone.</item>
    ///   <item><b>FULL-MODEL</b> (the h_ rig has NO such bone — the DPM rigs h_rl/h_crylink/h_electro/h_gl/
    ///   h_hagar/ok_*): render the <b>h_ RIG ITSELF</b> (its own gun+hand mesh) at <c>attach = identity</c>, and
    ///   IGNORE the v_ model — Base leaves the weaponchild NULL and the rig IS the viewmodel. The rig is authored
    ///   in the same Quake→Godot model space as the v_ models, so its drawn gun and its own <c>tag_shot</c> shot
    ///   origin coincide (no rotation strip, no <c>tag_handle</c> offset — that hack is gone for these).</item>
    /// </list>
    /// When the h_ rig is missing/unparsable it degrades to the invisible-hand v_ path (legacy behaviour).
    /// </summary>
    public static ViewModelEquip Build(AssetLoader? assets, string vModelPath)
    {
        if (assets is null || string.IsNullOrEmpty(vModelPath))
            return None;

        // v_laser.md3 -> h_laser.iqm (the hand rigs are .iqm by name; magic dispatch handles IQM vs DPM).
        string hPath = vModelPath
            .Replace("/v_", "/h_")
            .Replace(".md3", ".iqm");

        // Classify the rig WITHOUT building a Godot node (Godot-free parsed-data probe). Missing/unparsable rig
        // or a path with no h_ sibling -> treat as invisible-hand so we keep the legacy v_ render.
        bool? invisibleHand = hPath == vModelPath ? null : assets.WeaponRigIsInvisibleHand(hPath);

        // FULL-MODEL (DPM rigs, no weapon bone): the h_ rig itself is the viewmodel. attach = identity.
        if (invisibleHand == false)
        {
            Node3D? hand = assets.LoadModel(hPath);
            if (hand is not null)
                return new ViewModelEquip { Model = hand, Attach = Transform3D.Identity, IsHandRig = true };
            // Rig classified full-model but failed to build — fall through to the v_ path as a last resort.
        }

        // INVISIBLE-HAND (IQM rigs with a weapon bone): render the h_ RIG ITSELF (its only mesh is a nodraw
        // plane — a pure animated skeleton) and ride the v_ visual model on the LIVE `weapon` bone via a
        // BoneAttachment3D — Base's setattachment(weaponchild, this, "weapon"): the rig's fire/reload/idle
        // clips animate the BONE, and the static v_ gun pumps with it. (playtest r9: the old path baked the
        // bone REST into a static offset and DISCARDED the rig, so no viewmodel animation could ever play for
        // these weapons — the fire clip had nothing to move.)
        if (invisibleHand == true)
        {
            Node3D? rig = assets.LoadModel(hPath);
            if (rig is not null)
            {
                Skeleton3D? skel = IqmBuilder.FindSkeleton(rig);
                int bone = skel?.FindBone("weapon") ?? -1;
                if (bone < 0) bone = skel?.FindBone("tag_weapon") ?? -1;
                Node3D? vModel = skel is not null && bone >= 0 ? assets.LoadModel(vModelPath) : null;
                if (skel is not null && bone >= 0 && vModel is not null)
                {
                    var att = new BoneAttachment3D { Name = "weapon_attach", BoneName = skel.GetBoneName(bone) };
                    skel.AddChild(att);
                    att.AddChild(vModel); // identity local: the bone pose IS the gun pose (bind rotation is identity on these rigs)
                    return new ViewModelEquip { Model = rig, Attach = Transform3D.Identity, IsHandRig = true };
                }
                rig.QueueFree(); // no usable skeleton/bone — legacy static-offset fallback below
            }
        }

        // Missing/unbuildable rig (or the live-rig wiring above found no bone): render the v_ model at the
        // rig's weapon-attach bone REST (position-only — see WeaponAttachTransform). Static, no fire anim —
        // the legacy degraded path.
        Node3D? built = assets.LoadModel(vModelPath);
        Transform3D attach = WeaponAttachTransform(assets, vModelPath);
        return new ViewModelEquip { Model = built, Attach = attach, IsHandRig = false };
    }

    /// <summary>
    /// The held-gun attach offset for a <c>v_*</c> weapon path: load the sibling <c>h_*</c> hand model and read
    /// its <c>weapon</c> / <c>tag_weapon</c> bone rest (in built Godot space). This mirrors Xonotic's
    /// <c>setattachment(weaponchild, this, "weapon")</c> — the hand model is the rig that positions the
    /// <c>v_</c> visual model in the lower-right hand. Used only on the INVISIBLE-HAND path now (full-model DPM
    /// weapons render the h_ rig itself, so they never reach here); the <c>tag_handle</c> socket of the DPM rigs
    /// is therefore no longer consulted. Returns identity (gun at the eye) if no hand model / bone resolves.
    /// </summary>
    public static Transform3D WeaponAttachTransform(AssetLoader? assets, string vModelPath)
    {
        if (assets is null)
            return Transform3D.Identity;

        // v_laser.md3 -> h_laser.iqm (the hand rigs are .iqm; magic dispatch handles IQM vs DPM).
        string hPath = vModelPath
            .Replace("/v_", "/h_")
            .Replace(".md3", ".iqm");
        if (hPath == vModelPath || !assets.Vfs.Exists(hPath))
            return Transform3D.Identity;

        Node3D? hand = assets.LoadModel(hPath);
        if (hand is null)
            return Transform3D.Identity;

        // The attachment socket the v_ model rides on, in order of the QC's preference. Only invisible-hand
        // (IQM) rigs reach this helper now, and they all expose a `weapon` bone (identity rotation), so the
        // legacy `tag_handle` DPM fallback is removed — a full-model DPM rig renders the h_ rig itself and never
        // gets here, so attaching the (wrong) v_ model to its `tag_handle` socket (the "twisted crylink" hack)
        // is no longer reachable.
        Transform3D attach = Transform3D.Identity;
        foreach (string bone in new[] { "weapon", "tag_weapon" })
        {
            Transform3D rest = IqmBuilder.GetBoneGlobalRest(hand, bone);
            if (rest != Transform3D.Identity)
            {
                attach = rest;
                break;
            }
        }
        hand.QueueFree();

        // POSITION the gun only — never re-orient it. The v_ model geometry is already authored pointing
        // "forward" (the same IQM the world pickup builds correctly via Coords.ToGodot), and ViewModel's
        // ViewBasis re-aims that built-in forward into the camera frame. The socket's bind ROTATION must not be
        // layered on top (the IQM `weapon` bones carry identity rotation, so this is a no-op for them); keep the
        // socket's translation (the held-hand offset) and any uniform scale, drop the rotation/shear.
        return new Transform3D(Basis.Identity.Scaled(attach.Basis.Scale), attach.Origin);
    }
}
