// Port of qcsrc/client/csqcmodel_hooks.qc — CSQCModel_Effects_Apply (lines 545-661). Each rendered frame this
// turns a model's EF_* effect bits into dynamic lights / particle emissions / render-flag tweaks, and its MF_*
// model flags (+ EF_BRIGHTFIELD) into a projectile trail; it also drives the jetpack loop (MF_ROCKET). The pure
// EF_*/MF_* constants + the MF→trail mapping live in VortexArena.Engine.Simulation.CsqcModelEffectFlags
// (unit-tested); this is the thin Godot adapter (lights/materials/sound/trail are Godot-side).
//
// PARITY GAPS (the wire doesn't carry everything CSQC had locally — see the T58 report):
//   • csqcmodel_modelflags (MF_*) is NOT networked. NetEntityState carries Effects (int) only. So MF_* trails
//     and the MF_ROCKET jetpack loop are derived from the LOCALLY-supplied modelFlags arg (0 for plain remote
//     players). EF_BRIGHTFIELD (which IS in Effects) still maps to TR_NEXUIZPLASMA faithfully.
//   • adddynamiclight is a per-frame immediate-mode light in DP; QC spawns an INDEPENDENT light for each set
//     EF_* light bit (BRIGHTLIGHT/DIMLIGHT/BLUE/RED/FLAME/SHOCK), so a model with two light bits shows two
//     lights (csqcmodel_hooks.qc:557-593). Here a small pool of persistent OmniLight3D per model is re-aimed/
//     toggled each frame from CsqcModelEffects.State (one pool slot per set bit) so we don't churn nodes — same
//     visual result as DP's N independent lights.
//   • boxparticles(absmin..absmax, velocity) becomes an EffectSystem point-burst at the origin each frame
//     (the port has no box-volume particle primitive); EF_FLAME/EF_SHOCK/EF_STARDUST names already exist.

using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Services;
using VortexArena.Engine.Simulation;
using VortexArena.Game.Loaders;
using NVec3 = System.Numerics.Vector3;
using EFlags = VortexArena.Engine.Simulation.CsqcModelEffectFlags;

namespace VortexArena.Game.Client;

/// <summary>
/// Applies a model's networked <see cref="Entity.Effects"/> bits (and locally-supplied MF_* model flags) to its
/// rendered Godot node each frame, mirroring <c>CSQCModel_Effects_Apply</c>.
/// </summary>
public static class CsqcModelEffects
{
    /// <summary>
    /// Persistent per-model effect state the caller (one per rendered entity) holds across frames: the dynamic
    /// light node (re-used like DP's per-frame light) and the jetpack-loop flag (QC <c>.snd_looping</c>).
    /// </summary>
    public sealed class State
    {
        public readonly List<OmniLight3D> Lights = new(); // pool of EF_*-driven dynamic lights (one per set
                                       // light bit, like DP's N independent adddynamiclight calls); reused/aimed
                                       // each frame, unused slots hidden
        public int LoopChannel;        // QC .snd_looping (0 = no loop); the jetpack fly loop's channel
        public string LastTrail = "";  // the trail name currently applied (so we only re-route on change)
        public int LastEffects;        // last frame's effect bits + ghost — so the caller re-runs once to RESET
                                       // render-flags (additive/fullbright/depthhack/shadow/visibility) on clear

        // Cached flattened MeshInstance3D list for the current model node (3.2-2). The per-frame render-flag
        // passes (ResetRenderFlags + each set-bit setter) used to each re-walk the tree via GetChildren(), which
        // marshals a fresh Godot.Collections.Array (+ nested iterator allocs) per visited node — exactly during
        // the busy frames a glowing/flaming/ghost model is on screen. Built once per model node and reused;
        // rebuilt when the root node changes (a model swap, detected by instance id) or a cached mesh is freed.
        public readonly List<MeshInstance3D> CachedMeshes = new();
        public ulong CachedMeshesRootId;

        // (§11 R8) Last tint pushed by the per-frame appearance pass — ModelTint.ApplyAppearance skips the
        // 4×meshes SetInstanceShaderParameter interop when neither the colors nor the mesh list changed.
        public ModelTint.TintCache Tint;
    }

    /// <summary>One DP <c>adddynamiclight</c>: a unit position offset + range + (possibly &gt;1) color.</summary>
    private readonly record struct LightSpec(float Range, Color Color, NVec3 Offset);

    // Reused across calls instead of allocating per glowing entity per frame: Apply runs once per entity per
    // frame on the (single) main thread and fully consumes the list via DriveLights before it returns, so one
    // shared scratch buffer is safe and removes the steady-state GC drip from models carrying EF_* light bits.
    private static readonly List<LightSpec> _lightScratch = new(4);

    /// <summary>
    /// QC <c>CSQCModel_Effects_Apply(this)</c> for one frame. <paramref name="root"/> is the model's rendered
    /// node (its meshes get the render-flag tweaks), <paramref name="e"/> the networked entity (Effects + origin),
    /// <paramref name="modelFlags"/> the locally-classified MF_* set (0 when unknown). Returns the trail-effect
    /// name to apply to this model (or null for none) so the caller can route it to its trail system.
    /// <para><paramref name="forced"/> is the optional Wave-3 per-player presentation override
    /// (<see cref="CsqcModelAppearance.ForcedAppearance"/>): its extra EF_* bits OR onto the networked effects and
    /// its MF_* bits OR onto <paramref name="modelFlags"/>, so a gametype/mutator can drive role-glow lights,
    /// powerup/flame visuals, MF trails, or the jetpack loop per player. Omit it for the unchanged behavior.</para>
    /// </summary>
    public static string? Apply(EffectSystem? fx, Node3D root, Entity e, State st, int modelFlags, float frameTime,
        ISoundService? sound, bool isRespawnGhost,
        CsqcModelAppearance.ForcedAppearance forced = default)
    {
        // Wave-3 per-player presentation override: OR the forced EF_* bits (role glow / powerup / flame) onto the
        // networked effects, and OR the forced MF_* model flags onto the locally-classified set. Default-constructed
        // `forced` (the existing call site, no override) contributes nothing — bit-identical to the prior behavior.
        // ExtraEffects is masked to the presentation-owned bits so a driver can never force NODRAW/SELECTABLE/etc.
        int effectsBits = e.Effects | (forced.ExtraEffects & EFlags.ForcedEffectFlags);
        modelFlags |= forced.ModelFlags;

        // QC: int eff = csqcmodel_effects & ~CSQCMODEL_EF_RESPAWNGHOST;  (the ghost bit is handled separately)
        int eff = effectsBits & ~EFlags.CSQCMODEL_EF_RESPAWNGHOST;
        NVec3 origin = e.Origin;

        // The model's flattened mesh list, cached on State and reused across frames (3.2-2) so the render-flag
        // passes below don't each re-walk the tree via GetChildren().
        List<MeshInstance3D> meshes = EnsureMeshCache(st, root);

        // QC clears renderflags/effects/traileffect FIRST each frame — reset our Godot equivalents so flags
        // don't accumulate (additive/fullbright/depthtest/noshadow visibility all back to the model defaults).
        ResetRenderFlags(meshes);
        // QC spawns an INDEPENDENT adddynamiclight per set light bit (csqcmodel_hooks.qc:557-593), so accumulate
        // one LightSpec per bit (not a single overwriting light) and drive N pooled lights below. Reuses the
        // shared scratch buffer (consumed by DriveLights before this returns) — no per-entity-per-frame alloc.
        List<LightSpec> lights = _lightScratch;
        lights.Clear();
        string? tref = null;

        // EF_BRIGHTFIELD → TR_NEXUIZPLASMA trail (csqcmodel_hooks.qc:554-555).
        if ((eff & EFlags.EF_BRIGHTFIELD) != 0)
            tref = EFlags.BrightFieldTrail;
        // EF_MUZZLEFLASH ignored.
        if ((eff & EFlags.EF_BRIGHTLIGHT) != 0) lights.Add(new LightSpec(400f, new Color(3f, 3f, 3f), NVec3.Zero));
        if ((eff & EFlags.EF_DIMLIGHT) != 0)    lights.Add(new LightSpec(200f, new Color(1.5f, 1.5f, 1.5f), NVec3.Zero));
        // EF_NODRAW or alpha<0 → drawmask 0 (hide). (The port has no per-entity alpha < 0; NODRAW is networked.)
        bool hidden = (eff & EFlags.EF_NODRAW) != 0;
        if ((eff & EFlags.EF_ADDITIVE) != 0) SetAdditive(meshes, true);
        if ((eff & EFlags.EF_BLUE) != 0) lights.Add(new LightSpec(200f, new Color(0.15f, 0.15f, 1.5f), NVec3.Zero));
        if ((eff & EFlags.EF_RED) != 0)  lights.Add(new LightSpec(200f, new Color(1.5f, 0.15f, 0.15f), NVec3.Zero));
        // EF_NOGUNBOB ignored.
        if ((eff & EFlags.EF_FULLBRIGHT) != 0) SetFullbright(meshes, true);
        // QC emits the EF_FLAME/EF_SHOCK/EF_STARDUST particles via boxparticles with dt = bound(0, frametime, 0.1);
        // a zero dt (paused frame) emits nothing, so gate the per-frame burst on frameTime > 0.
        bool emit = frameTime > 0f;
        if ((eff & EFlags.EF_FLAME) != 0)
        {
            // EF_FLAME: an orange dynamic light at origin+'0 0 10' + the EF_FLAME particle burst.
            lights.Add(new LightSpec(200f, new Color(1f, 0.35f, 0f), new NVec3(0f, 0f, 10f)));
            if (emit) fx?.Spawn("EF_FLAME", origin, e.Velocity, 1);
        }
        if ((eff & EFlags.EF_SHOCK) != 0)
        {
            // EF_SHOCK: a bright blue-white light + the EF_SHOCK particle burst (ARC_LIGHTNING fallback handled
            // by the effect catalog name resolution).
            lights.Add(new LightSpec(50f, new Color(3.1f, 4.4f, 10f), NVec3.Zero));
            if (emit) fx?.Spawn("EF_SHOCK", origin, NVec3.Zero, 1);
        }
        if ((eff & EFlags.EF_STARDUST) != 0 && emit)
            fx?.Spawn("EF_STARDUST", origin, e.Velocity, 1);
        if ((eff & EFlags.EF_NOSHADOW) != 0) SetCastShadow(meshes, false);
        if ((eff & EFlags.EF_NODEPTHTEST) != 0) SetNoDepthTest(meshes, true);
        // EF_DOUBLESIDED / EF_NOSELFSHADOW / EF_DYNAMICMODELLIGHT: engine passthroughs (no port material analog).

        // MF_* → trail (csqcmodel_hooks.qc:611-632): the last matching flag wins. MF_ROTATE has no trail.
        string? mfTrail = EFlags.ModelFlagToTrail(modelFlags);
        if (mfTrail is not null)
            tref = mfTrail;

        // Drive the pooled dynamic lights — one per accumulated spec this frame (DP's N independent lights).
        // A hidden model (EF_NODRAW) spawns none.
        DriveLights(root, st, hidden ? System.Array.Empty<LightSpec>() : (IReadOnlyList<LightSpec>)lights, origin);

        // EF_NODRAW (or alpha<0) → drawmask 0 in QC; here, hide/show the model node. (NODRAW is an EF bit, so
        // LastEffects below captures it — the caller re-runs once after it clears to restore Visible=true.)
        root.Visible = !hidden;

        // RESPAWNGHOST → additive (csqcmodel_hooks.qc:641-642). Also handled in the glow path (ModelTint).
        if (isRespawnGhost)
            SetAdditive(meshes, true);

        // Jetpack loop (csqcmodel_hooks.qc:645-660): MF_ROCKET on → start SND_JETPACK_FLY loop on CH_TRIGGER_SINGLE
        // once; off → stop it. Only reachable when modelFlags carries MF_ROCKET (the local jetpack), since MF_* is
        // not networked for remote players.
        DriveJetpackLoop(e, st, sound, (modelFlags & EFlags.MF_ROCKET) != 0);

        st.LastTrail = tref ?? "";
        // Remember whether any render-flag-affecting bit was applied this frame (EF_* incl. the ghost→additive),
        // so the caller re-runs ONE more frame after they clear to reset the flags (else additive/fullbright/
        // depthhack/no-shadow stick). modelFlags only drives trail+jetpack, not material flags, so it's excluded.
        st.LastEffects = effectsBits | (isRespawnGhost ? EFlags.CSQCMODEL_EF_RESPAWNGHOST : 0);
        return tref;
    }

    /// <summary>QC CH_TRIGGER_SINGLE = 3 (common/sounds/sound.qh:13) = CHAN_ITEM — the jetpack fly loop channel.</summary>
    private const SoundChannel JetpackChannel = SoundChannel.Item;

    private static void DriveJetpackLoop(Entity e, State st, ISoundService? sound, bool rocket)
    {
        if (sound is null)
            return;
        if (rocket)
        {
            if (st.LoopChannel == 0)
            {
                // SND_JETPACK_FLY at VOL_BASE (1) / cl_jetpack_attenuation (default 2), looping on the channel.
                float atten = CvarF("cl_jetpack_attenuation", 2f);
                sound.Play(e, JetpackChannel, "JETPACK_FLY", volume: 1f, attenuation: atten, loop: true);
                st.LoopChannel = (int)JetpackChannel;
            }
        }
        else if (st.LoopChannel != 0)
        {
            sound.Stop(e, (SoundChannel)st.LoopChannel);
            st.LoopChannel = 0;
        }
    }

    // ---- render-flag application on the model's meshes ------------------------------------------------------
    // QC sets renderflags on the whole entity. The faithful Godot map:
    //   • CastShadow / Visible are GeometryInstance3D node properties — per-instance, ALWAYS safe + effective.
    //   • additive / fullbright / nodepthtest are MATERIAL properties. We only touch a MeshInstance3D's
    //     MaterialOverride when it is a per-instance BaseMaterial3D (e.g. the placeholder box). We deliberately
    //     do NOT mutate SurfaceSetMaterial materials (resolved models set those) because AssetSystem CACHES +
    //     SHARES them across entities (see AssetSystem._materialCache) — mutating one would corrupt every entity
    //     using that texture (the RC3/RC4 lesson). Net: these three render-flags apply only to per-instance-
    //     material meshes; on shared/shader-material models they're a documented parity gap (no per-instance
    //     additive/fullbright/depthhack render path exists in the port yet).

    /// <summary>
    /// Expose the per-model cached, flattened mesh list (rebuilding it on a swap / freed mesh exactly like the
    /// effects pass) so the per-frame appearance pass (<see cref="ModelTint.ApplyAppearance(System.Collections.Generic.IReadOnlyList{MeshInstance3D},int,bool,float,bool)"/>)
    /// can reuse the SAME cache keyed on the SAME <paramref name="root"/> — no second tree-walk and no risk of
    /// the two lists diverging (3.2-2). Routes through <see cref="EnsureMeshCache"/> so invalidation (instance-id
    /// change + freed-mesh validity scan, incl. the staggered placeholder→real swap) stays identical.
    /// </summary>
    public static List<MeshInstance3D> GetCachedMeshes(State st, Node3D root) => EnsureMeshCache(st, root);

    /// <summary>
    /// Override the per-player FORCED glowmod on a cached mesh list — the glow companion to
    /// <see cref="ModelTint.SetColormod"/>, for the Wave-3 presentation layer to tint a player's model glow by
    /// role (gametype color, powerup) on top of the colormap-derived glowmod the appearance pass set. Call AFTER
    /// <see cref="ModelTint.ApplyAppearance(System.Collections.Generic.IReadOnlyList{MeshInstance3D},int,bool,float,bool,ref ModelTint.TintCache)"/>
    /// and invalidate that pass's change-gate (set <c>Tint.Valid=false</c>) so the real glowmod repaints the frame
    /// the override clears — exactly the pattern the frozen-tint overlay uses for colormod. No-op when
    /// <paramref name="forced"/> has no forced glowmod (<see cref="CsqcModelAppearance.ForcedAppearance.HasForcedGlowmod"/>).
    /// </summary>
    public static void ApplyForcedGlowmod(IReadOnlyList<MeshInstance3D> meshes, CsqcModelAppearance.ForcedAppearance forced)
    {
        if (!forced.HasForcedGlowmod)
            return;
        var glow = new Color(forced.ForcedGlowmod.r, forced.ForcedGlowmod.g, forced.ForcedGlowmod.b);
        for (int i = 0; i < meshes.Count; i++)
            meshes[i].SetInstanceShaderParameter(PlayerSkinShader.GlowmodUniform, glow);
    }

    /// <summary>Return the model's cached mesh list, rebuilding it when the model node changed (a swap, by
    /// instance id) or a cached mesh was freed. The validity scan is O(meshes) of cheap native calls — no
    /// GetChildren() marshaling, which is the whole point (3.2-2). Built once and reused otherwise.</summary>
    private static List<MeshInstance3D> EnsureMeshCache(State st, Node3D root)
    {
        ulong id = root.GetInstanceId();
        bool stale = st.CachedMeshesRootId != id;
        if (!stale)
            for (int i = 0; i < st.CachedMeshes.Count; i++)
                if (!GodotObject.IsInstanceValid(st.CachedMeshes[i])) { stale = true; break; }
        if (stale)
        {
            st.CachedMeshes.Clear();
            CollectMeshes(root, st.CachedMeshes);
            st.CachedMeshesRootId = id;
        }
        return st.CachedMeshes;
    }

    private static void ResetRenderFlags(List<MeshInstance3D> meshes)
    {
        SetAdditive(meshes, false);
        SetFullbright(meshes, false);
        SetNoDepthTest(meshes, false);
        SetCastShadow(meshes, true);
    }

    // True only for a material safe to mutate per-frame: a per-instance BaseMaterial3D set as MaterialOverride.
    // (SurfaceSetMaterial materials are shared/cached, so we never reach them via MaterialOverride.)
    private static void SetAdditive(List<MeshInstance3D> meshes, bool on)
    {
        foreach (MeshInstance3D mi in meshes)
            if (mi.MaterialOverride is BaseMaterial3D m)
                m.BlendMode = on ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix;
    }

    private static void SetFullbright(List<MeshInstance3D> meshes, bool on)
    {
        foreach (MeshInstance3D mi in meshes)
            if (mi.MaterialOverride is BaseMaterial3D m)
                m.ShadingMode = on ? BaseMaterial3D.ShadingModeEnum.Unshaded : BaseMaterial3D.ShadingModeEnum.PerPixel;
    }

    private static void SetNoDepthTest(List<MeshInstance3D> meshes, bool on)
    {
        foreach (MeshInstance3D mi in meshes)
            if (mi.MaterialOverride is BaseMaterial3D m)
                m.NoDepthTest = on;
    }

    private static void SetCastShadow(List<MeshInstance3D> meshes, bool cast)
    {
        foreach (MeshInstance3D mi in meshes)
            mi.CastShadow = cast ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off;
    }

    /// <summary>
    /// Drive a pool of OmniLight3D children, one per <paramref name="specs"/> entry — DP spawns N independent
    /// per-frame dynamic lights (one per set EF_* light bit), so a model with e.g. EF_DIMLIGHT|EF_RED shows both.
    /// The pool is reused across frames (grown on demand); slots past the current spec count are hidden, not freed.
    /// </summary>
    private static void DriveLights(Node3D root, State st, IReadOnlyList<LightSpec> specs, NVec3 originQuake)
    {
        for (int i = 0; i < specs.Count; i++)
        {
            // Grow the pool to cover this slot.
            while (st.Lights.Count <= i || !GodotObject.IsInstanceValid(st.Lights[i]))
            {
                var l = new OmniLight3D { Name = $"csqc_fx_light{st.Lights.Count}" };
                root.AddChild(l);
                if (st.Lights.Count <= i) st.Lights.Add(l);
                else st.Lights[i] = l;
            }
            OmniLight3D light = st.Lights[i];
            LightSpec spec = specs[i];
            // DP light colors exceed 1 (e.g. '3 3 3'); split into a unit hue + energy like EffectSystem.SpawnInfoLight.
            float maxc = Mathf.Max(1f, Mathf.Max(spec.Color.R, Mathf.Max(spec.Color.G, spec.Color.B)));
            light.Visible = true;
            light.OmniRange = Mathf.Clamp(spec.Range, 1f, 2000f);
            light.LightColor = new Color(spec.Color.R / maxc, spec.Color.G / maxc, spec.Color.B / maxc);
            light.LightEnergy = Mathf.Min(8f, maxc);
            // The light sits at the model origin (+ offset) in world space (the node tracks the entity).
            light.GlobalPosition = Coords.ToGodot(originQuake + spec.Offset);
        }
        // Hide any pool slots not used this frame (effect bits cleared).
        for (int i = specs.Count; i < st.Lights.Count; i++)
            if (GodotObject.IsInstanceValid(st.Lights[i]))
                st.Lights[i].Visible = false;
    }

    /// <summary>Stop any jetpack loop + free the pooled lights when the model is torn down (the caller calls this on remove).</summary>
    public static void Release(Entity e, State st, ISoundService? sound)
    {
        if (st.LoopChannel != 0 && sound is not null)
        {
            sound.Stop(e, (SoundChannel)st.LoopChannel);
            st.LoopChannel = 0;
        }
        foreach (OmniLight3D light in st.Lights)
            if (GodotObject.IsInstanceValid(light))
                light.QueueFree();
        st.Lights.Clear();
    }

    private static float CvarF(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : Api.Cvars.GetFloat(name);
    }

    /// <summary>Recursively flatten every <see cref="MeshInstance3D"/> under <paramref name="node"/> into
    /// <paramref name="into"/>. Called once per model node (then cached on <see cref="State"/>), so the
    /// per-node GetChildren() marshaling happens at build time, not every frame (3.2-2).</summary>
    private static void CollectMeshes(Node node, List<MeshInstance3D> into)
    {
        if (node is MeshInstance3D mi)
            into.Add(mi);
        foreach (Node child in node.GetChildren())
            CollectMeshes(child, into);
    }
}
