using Godot;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Game;

/// <summary>
/// The presentation-side binding between a simulation <see cref="Entity"/> (Quake coords, Godot-free)
/// and a Godot <see cref="Node3D"/>. Implements <see cref="IEntityPresence"/> so it can be hung off
/// <see cref="Entity.Presence"/> (the client-only link declared in Framework/Entity.cs, kept as an
/// interface so the sim stays Godot-free per ADR-0008).
///
/// Each frame it pulls the entity's authoritative origin/yaw and writes them to the node's transform,
/// converting Quake (Z-up) -> Godot (Y-up) at the boundary. The sim owns the state; this node only
/// reflects it.
/// </summary>
public partial class EntityNode : Node3D, IEntityPresence
{
    /// <summary>The bound simulation entity. Set this (and optionally <see cref="Entity.Presence"/>) after construction.</summary>
    public Entity? Entity { get; set; }

    // Visibility has two independent owners that must AND together: gameplay (item respawn-ghost / death fade)
    // and the §12.8 entity PVS cull. Routing both through these setters keeps them from clobbering each other
    // (a faded-out item behind a wall stays hidden; an in-PVS ghosting item still shows its ghost).
    private bool _gameplayVisible = true;
    private bool _pvsVisible = true;
    private bool _effectiveVisible = true;

    /// <summary>Effective render visibility (gameplay AND pvs), mirrored in managed state so the central
    /// drive loop (<see cref="DriveSync"/>) can gate the transform sync without an interop <c>Visible</c> read.</summary>
    public bool EffectiveVisible => _effectiveVisible;

    /// <summary>Recompute the ANDed Godot visibility from the two owners; on a hidden→visible regain, force a
    /// fresh sync next drive (the entity's origin may have changed while we were skipping its sync).</summary>
    private void ApplyVisible()
    {
        bool eff = _gameplayVisible && _pvsVisible;
        bool regained = eff && !_effectiveVisible;
        _effectiveVisible = eff;
        Visible = eff;
        if (regained) _syncValid = false;
    }

    /// <summary>Gameplay-side visibility (item ghost / death-fade). ANDs with the PVS cull for the final flag.</summary>
    public void SetGameplayVisible(bool v)
    {
        if (_gameplayVisible == v) return;
        _gameplayVisible = v;
        ApplyVisible();
    }

    /// <summary>(§12.8) PVS-cull visibility — true unless the entity's bounds are outside the camera's PVS.
    /// ANDs with the gameplay flag so neither owner overrides the other.</summary>
    public void SetPvsVisible(bool v)
    {
        if (_pvsVisible == v) return;
        _pvsVisible = v;
        ApplyVisible();
    }

    /// <summary>Attach to an entity and register this node as its presence link.</summary>
    public void Bind(Entity entity)
    {
        Entity = entity;
        entity.Presence = this;
        SyncFromEntity();
    }

    // R1 dirty-gate: SyncFromEntity is a pure function of (Origin, Angles, ScaleFactor, ItemAnimate) — plus the
    // render clock, but ONLY while ItemAnimate != 0 (bob/spin pickups) or ModelSpinRotate (MF_ROTATE key spin). So
    // the marshalled Position/Basis/Scale writes can be skipped whenever none of those inputs changed. The node's own _Process is disabled
    // (ClientWorld drives DriveSync from its central _entityNodes loop) to remove the per-node native->managed
    // callback — the dominant render-submission (rcpu) tax at ~150 entities × high fps.
    private System.Numerics.Vector3 _lastOrigin, _lastAngles;
    private float _lastScale;
    private byte _lastItemAnimate;
    private bool _syncValid;

    /// <summary>Drive one frame of transform sync (called from <c>ClientWorld._Process</c>'s central loop),
    /// skipping the interop writes when nothing the sync reads has changed. An animating pickup
    /// (<c>ItemAnimate != 0</c>) is time-driven, so it always re-syncs.</summary>
    public void DriveSync()
    {
        Entity? e = Entity;
        if (e is null) return;
        // Time-driven anims must re-sync every frame regardless of the dirty-gate: ItemAnimate (bob/spin pickups)
        // and ModelSpinRotate (MF_ROTATE — the pickup-key 100°/s spin main adds in SyncFromEntity, which reads the
        // render clock but is NOT coupled to ItemAnimate). Otherwise skip when nothing the sync reads changed.
        if (e.ItemAnimate == 0 && !e.ModelSpinRotate && _syncValid
            && e.Origin == _lastOrigin && e.Angles == _lastAngles
            && e.ScaleFactor == _lastScale && e.ItemAnimate == _lastItemAnimate)
            return; // unchanged → skip the marshalled Position/Basis/Scale writes
        SyncFromEntity();
        _lastOrigin = e.Origin;
        _lastAngles = e.Angles;
        _lastScale = e.ScaleFactor;
        _lastItemAnimate = e.ItemAnimate;
        _syncValid = true;
    }

    /// <summary>Force the next <see cref="DriveSync"/> to re-sync regardless of the dirty-gate (model swap /
    /// visibility regain — see <see cref="ApplyVisible"/>).</summary>
    public void ForceSync() => _syncValid = false;

    /// <summary>Copy the bound entity's origin and yaw onto this node's transform (Quake -> Godot).</summary>
    public void SyncFromEntity()
    {
        if (Entity is null)
            return;

        Vector3 position = Coords.ToGodot(Entity.Origin);
        float yawDeg = Entity.Angles.Y;

        // QC ItemDraw bob+spin (client/items/items.qc): a world pickup with an animation class floats on a sine
        // wave and spins about yaw, all client-side. The bob's base offset also lifts the model clear of the
        // floor it rests on (otherwise a tall item like megahealth renders half-sunk). Resting items use absolute
        // client time so a class bobs in phase, matching QC's anim_start_time == 0 path.
        if (Entity.ItemAnimate != 0)
        {
            float time = Api.Services is not null ? Api.Clock.Time : 0f;
            (float bobHeight, float yawSpinDeg) = ItemBobAnim.Sample(Entity.ItemAnimate, time);
            position.Y += bobHeight; // Quake +Z (up) maps to Godot +Y under ToGodot=(x,z,-y)
            yawDeg += yawSpinDeg;
        }

        // QC MF_ROTATE (csqcmodel_hooks.qc:617-623, the pickup-key model flag): a steady yaw spin of
        // '0 100 0' * fmod(time, 3.6) added on top of the entity's base angles (100°/s, wrapping every 3.6 s =
        // a full 360°). Keys-only in all of Base; carried as a render-only bool on the shared edict.
        if (Entity.ModelSpinRotate)
        {
            float time = Api.Services is not null ? Api.Clock.Time : 0f;
            yawDeg += 100f * (time % 3.6f);
        }

        Position = position;

        // [T48] Pitched/rolled entities (misc_gamemodel props on courtfun/space-elevator etc.) need the FULL
        // orientation, not just yaw. Use the proven AngleVectors → Coords.ToGodot columns basis (the same
        // convention FirstPersonView/the camera use — see NetGame's note that a negated-yaw Euler flips
        // handedness): mesh verts are ToGodot-converted, so the node basis is the Quake rotation conjugated
        // by ToGodot — columns X=ToGodot(forward), Y=ToGodot(up), Z=ToGodot(right). Gated to entities that
        // actually carry pitch/roll so the long-standing yaw-only path below stays byte-identical for
        // players/items/monsters (changing their convention is a separate, visually-verified pass).
        // [lean] A networked playermodel lean offset (PlayerLean — Zero for everyone while at rest/disabled)
        // composes onto the same full-basis path: composed = base ∘ lean (the original QC's
        // LA = Multiply(FromAngles(angles), LF) order), all in Quake space BEFORE the one Godot conversion.
        bool hasLean = Entity.LeanAngles != System.Numerics.Vector3.Zero;
        if (hasLean || Entity.Angles.X != 0f || Entity.Angles.Z != 0f)
        {
            var angles = new System.Numerics.Vector3(Entity.Angles.X, yawDeg, Entity.Angles.Z);
            if (hasLean)
                angles = XonoticGodot.Common.Math.QMath.AnglesTransformMultiply(angles, Entity.LeanAngles);
            XonoticGodot.Common.Math.QMath.AngleVectors(angles,
                out System.Numerics.Vector3 fwd, out System.Numerics.Vector3 right, out System.Numerics.Vector3 up);
            Basis = new Basis(Coords.ToGodot(fwd), Coords.ToGodot(up), Coords.ToGodot(right));
        }
        else
        {
            // Quake yaw is rotation about +Z (up). Under ToGodot=(x,z,-y), +Z maps to Godot +Y, and a
            // positive Quake yaw (+X toward +Y) becomes a negative rotation about Godot +Y.
            Rotation = new Vector3(0f, -Mathf.DegToRad(yawDeg), 0f);
        }

        // [T48] Prop scale (QC .scale → Entity.ScaleFactor, models.qc g_model_init): applied for the
        // listen-server/demo paths, where the shared entity carries it. 0 = unset (remote clients miss it
        // until an EntityField.Scale protocol bump — deferred, see the T48 brief).
        float s = Entity.ScaleFactor;
        if (s > 0f && System.MathF.Abs(s - 1f) > 0.001f)
            Scale = new Vector3(s, s, s);
    }
}
