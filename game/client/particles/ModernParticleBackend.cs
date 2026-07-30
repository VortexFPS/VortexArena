using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Game.Client; // EffectInfoEmitter, EiType/EiBlend/EiOrientation, ParticleFont
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client.Particles;

// =====================================================================================================
//  ModernParticleBackend — the §B modern GPU backend node (planning/particles-dual-system.md §B). Builds a
//  GpuParticles3D per parsed emitter block whose PROCESS material is the custom `shader_type particles`
//  shader (DP's real spawn+integration+SDF response, ModernParticleShaders) instead of the approximate
//  ParticleProcessMaterial the faithful-GPU path uses, plus a draw-pass material from the modern draw shader
//  (atlas sprite + blend + soft particles + emissive boost).
//
//  This node is OWNED by EffectSystem (the orchestrator adds it as a child and routes modern spawns to it).
//  It needs the particlefont atlas to texture billboards and the Coords bridge to convert Quake→Godot; both
//  are injected by the orchestrator (Font property + the host already converts the origin/velocity it passes).
//
//  SDF collision: every emitter renders on ModernCollisionLayer (== default layer 1, which the camera draws
//  AND the SDF colliders' default cull_mask covers — see the constant's doc for why we must NOT move emitters
//  to a private layer). `collisionEnabled` is honored NOT by toggling a layer but by COMPILING-OUT the §B.2
//  bounce/blood branches in the process shader (a custom particles shader only reacts to COLLIDED in code, so
//  a shader with no bounce branch is collisionless even where colliders are present). The §B.2 RESPONSE lives
//  entirely in the process shader.
//
//  Caching: the compiled shaders are cached by feature SHAPE in ModernParticleShaders. Here we additionally
//  cache the per-blend draw QuadMesh by (blend, spark, sprite) so emitters sharing a draw shape share the
//  mesh+material (mirroring EffectSystem's _infoMeshCache idea). The per-emitter ShaderMaterial (carrying the
//  uniform values) is necessarily fresh per emitter.
// =====================================================================================================

/// <summary>The modern GPU particle backend (§B). <see cref="Spawn"/> builds the layered GpuParticles3D nodes
/// for one effect's blocks; <see cref="Clear"/> frees the live pool.</summary>
public partial class ModernParticleBackend : Node3D
{
    /// <summary>
    /// The VisualInstance3D render layer modern emitters live on, ALSO the layer the SdfCollisionService's
    /// collider <c>cull_mask</c> must include so its GPUParticlesCollisionSDF3D chunks act on modern particles.
    ///
    /// CRITICAL Godot detail: GPU-particle collision is gated by matching the EMITTER's VisualInstance3D
    /// VISIBILITY LAYERS against the collider's cull_mask — and those visibility layers are the SAME layers a
    /// Camera3D culls on for RENDERING. So a modern emitter MUST stay on a layer the active camera renders, or
    /// it goes invisible. We therefore keep modern emitters on the DEFAULT render layer (layer 1) and rely on
    /// the collider cull_mask defaulting to "all layers" (which includes layer 1). This constant is the single
    /// source of truth for that shared layer if the SDF service ever narrows its cull_mask: it should set
    /// cull_mask to <see cref="ModernCollisionMask"/> rather than excluding layer 1 (the constant stays 1).
    /// The backend does NOT reassign <c>Layers</c> away from this (doing so would break visibility).
    /// </summary>
    public const int ModernCollisionLayer = 1;

    /// <summary>The bitmask form of <see cref="ModernCollisionLayer"/> for VisualInstance3D.Layers / cull_mask.</summary>
    public const uint ModernCollisionMask = 1u << (ModernCollisionLayer - 1);

    /// <summary>The particlefont atlas (injected by the orchestrator/EffectSystem). When null, billboards use a
    /// flat white quad (no atlas mounted) — the modern math still runs, just untextured.</summary>
    public ParticleFont? Font { get; set; }

    /// <summary>Hard cap on live modern effect nodes; the oldest are culled past this (cheap GC guard, mirrors
    /// EffectSystem.MaxLiveEffects).</summary>
    [Export] public int MaxLiveEffects { get; set; } = 256;

    /// <summary>Global multiplier on particle counts (matches EffectSystem.DensityScale; the orchestrator keeps
    /// them in sync).</summary>
    [Export] public float DensityScale { get; set; } = 1f;

    private readonly Queue<Node3D> _live = new();

    // (blend, spark, sprite-instance) -> shared draw QuadMesh+ShaderMaterial. The draw shape is a pure function
    // of these (+ the preset draw features, folded into the shader the material references), so it's shareable.
    private readonly Dictionary<(int blend, bool spark, ulong sprite, int featHash), Mesh> _drawMeshCache = new();

    // =================================================================================================
    //  Public API
    // =================================================================================================

    /// <summary>
    /// Build the modern GPU nodes for one effect: one <see cref="GpuParticles3D"/> per parsed emitter block,
    /// each driven by the custom process shader (DP math) + the modern draw shader (the preset's features).
    /// <paramref name="origin"/>/<paramref name="velocity"/> are in QUAKE space (the sim convention); they are
    /// converted to Godot space here via <see cref="Coords"/>. <paramref name="count"/> scales the burst
    /// (DP pcount). When <paramref name="collisionEnabled"/>, emitters join <see cref="ModernCollisionMask"/>
    /// so the SDF colliders act on them. Returns the parent node (added to this backend's tree), or null if no
    /// block produced a visible emitter (e.g. all decal/beam blocks, which the modern path leaves to the shared
    /// CPU subsystems).
    /// </summary>
    public Node3D? Spawn(IReadOnlyList<EffectInfoEmitter> blocks, NVec3 origin, NVec3 velocity, float count,
        ModernPreset preset, bool collisionEnabled)
    {
        if (blocks is null || blocks.Count == 0)
            return null;

        var parent = new Node3D { Name = "modernfx", Position = Coords.ToGodot(origin) };
        bool any = false;

        // Velocity basis for the relative offsets (same convention as the faithful path: DP rotates
        // relativeoriginoffset/relativevelocityoffset through the makevectors-consistent angles of the emit
        // velocity). We pre-sum the rotated relatives into the flat offsets the shader receives.
        NVec3 basisDir = velocity;
        VortexArena.Common.Math.QMath.AngleVectors(
            VortexArena.Common.Math.QMath.FixedVecToAngles(basisDir),
            out NVec3 fwd, out NVec3 right, out NVec3 up);

        foreach (EffectInfoEmitter info in blocks)
        {
            if (!info.Defined)
                continue;
            // Decals/beams/raindecal/entityparticle are handled by the shared CPU subsystems (decals, beams),
            // not the modern GPU emitter — the modern backend only owns the free-particle types.
            if (info.Type is EiType.Decal or EiType.RainDecal or EiType.EntityParticle)
                continue;
            if (info.Orientation == EiOrientation.Beam)
                continue;

            GpuParticles3D? p = BuildEmitter(info, velocity, count, preset, collisionEnabled,
                fwd, right, up);
            if (p is not null)
            {
                parent.AddChild(p);
                any = true;
            }
        }

        if (!any)
        {
            parent.QueueFree();
            return null;
        }

        AddChild(parent);
        ScheduleFree(parent, ParentLinger(blocks));
        Track(parent);
        return parent;
    }

    /// <summary>Free every live modern effect node (map change / backend switch).</summary>
    public void Clear()
    {
        while (_live.TryDequeue(out Node3D? n))
            if (GodotObject.IsInstanceValid(n))
                n.QueueFree();
    }

    // =================================================================================================
    //  Per-block emitter build
    // =================================================================================================

    private GpuParticles3D? BuildEmitter(EffectInfoEmitter info, NVec3 velocity, float requestedCount,
        ModernPreset preset, bool collisionEnabled, NVec3 fwd, NVec3 right, NVec3 up)
    {
        // --- count: DP cnt = countabsolute + pcount*countmultiplier*quality (no trail term here — the modern
        // point burst). quality (DensityScale) multiplies ONLY the multiplier term, never countabsolute. ------
        float pcount = MathF.Max(1f, requestedCount);
        float cnt = info.CountAbsolute + pcount * info.CountMultiplier * DensityScale;
        if (cnt <= 0f)
            return null; // pure-dlight block — the modern backend emits no billboard (dlight is a shared system)
        int n = Math.Clamp((int)MathF.Ceiling(cnt), 1, 1024);

        bool isSpark = info.Orientation == EiOrientation.Spark;
        bool isSnow = info.Type == EiType.Snow;
        // The emitter Lifetime must be >= the LONGEST per-particle life the process shader can assign, or Godot
        // recycles a still-living particle before the shader's age>=life kill. info.Lifetime() is the per-emitter
        // midpoint; the shader clamps per-particle life to [0.05,6] (matching EffectInfoEmitter.Lifetime), so use
        // the upper bound of the explicit time range when present, else the midpoint, capped at the same 6s ceil.
        float life = info.Lifetime();
        bool explicitTimeRange = info.TimeMin < 16777216f || info.TimeMax < 16777216f;
        float emitterLife = explicitTimeRange
            ? Math.Clamp(MathF.Max(info.TimeMax, life), 0.05f, 6f)
            : life;

        // --- feature shapes (cache keys) ---------------------------------------------------------------
        var ep = new ModernEmitterParams
        {
            AirFriction = info.AirFriction,
            Bounce = info.Bounce,
            SizeIncrease = info.SizeIncrease * 2f, // half-size/sec -> edge/sec (the draw reads edge length)
            SpinMin = Deg2Rad(info.RotateSpinMin),
            SpinMax = Deg2Rad(info.RotateSpinMax),
            StretchFactor = info.StretchFactor > 0f ? info.StretchFactor : 1f,
            IsSpark = isSpark,
            IsSnow = isSnow,
            Blend = ToModernBlend(info.Blend),
        };
        var procFeat = ModernParticleShaders.ProcessFeatures.From(ep, preset, collisionEnabled);
        var drawFeat = ModernParticleShaders.DrawFeatures.From(ep, preset);

        var particles = new GpuParticles3D
        {
            Name = $"m_{info.Type}",
            Amount = n,
            Lifetime = MathF.Max(emitterLife, 0.05f),
            OneShot = true,
            Explosiveness = 1f,    // a point burst spawns all particles this frame (the shader scatters them)
            Emitting = true,
            // Generous AABB so a fast/expanding burst isn't culled when the emitter origin drifts off-screen.
            // Kept within ±384 (planning §A.5) so a modern emitter spans ≤2 SDF chunks/axis (the 7-texture cap).
            VisibilityAabb = new Aabb(new Vector3(-384f, -384f, -384f), new Vector3(768f, 768f, 768f)),
            // Stay on the default render layer (ModernCollisionLayer == 1): it is BOTH camera-visible AND the
            // layer the SDF colliders' default cull_mask includes, so collision works without breaking rendering.
            // collisionEnabled is honored by COMPILING-OUT the bounce branch (see ProcessFeatures.From) rather
            // than by moving the emitter off a visible layer — a custom particles shader only reacts to COLLIDED
            // in code, so a shader with no bounce branch is collisionless even where colliders are present.
            Layers = ModernCollisionMask,
        };

        // --- PROCESS material: the custom particles shader, with this block's uniforms -------------------
        var proc = new ShaderMaterial { Shader = ModernParticleShaders.GetProcessShader(procFeat) };
        FillProcessUniforms(proc, info, velocity, ep, procFeat, preset, fwd, right, up);
        particles.ProcessMaterial = proc;

        // --- DRAW pass: a billboard quad (spark → stretched) textured + blended per the draw shader ------
        Texture2D? sprite = Font?.CellInRange(info.Tex0, info.Tex1);
        particles.DrawPass1 = BuildDrawMesh(drawFeat, preset, sprite, info);

        // Spark orientation: the process shader writes a velocity-aligned basis into the model matrix, so the
        // node must NOT re-billboard it. Billboards use the camera-facing draw vertex path. (Both handled in
        // the draw shader; no Godot TransformAlign is needed since we own the basis.)
        return particles;
    }

    /// <summary>Set every process-shader uniform from the parsed block (converting Quake→Godot axes/units). The
    /// uniform names match ModernParticleShaders' declarations 1:1.</summary>
    private static void FillProcessUniforms(ShaderMaterial mat, EffectInfoEmitter info,
        NVec3 velocityQuake, in ModernEmitterParams ep,
        in ModernParticleShaders.ProcessFeatures f, in ModernPreset preset, NVec3 fwd, NVec3 right, NVec3 up)
    {
        // Origin range: a point burst spawns at origin..origin (the shader adds the box via origin_mins/maxs).
        // The emitter node sits at Coords.ToGodot(origin); the shader's EMISSION_TRANSFORM provides that, and
        // origin_mins/maxs are LOCAL spans about it. For a point effect both are zero (the jitter does the work).
        mat.SetShaderParameter("origin_mins", Vector3.Zero);
        mat.SetShaderParameter("origin_maxs", Vector3.Zero);

        // relativeoriginoffset rotates through the velocity basis; originoffset is a world-axis add. Pre-sum and
        // convert to Godot space (the shader works entirely in Godot space).
        NVec3 relOriginQ =
            info.RelativeOriginOffset.X * fwd
            + info.RelativeOriginOffset.Y * right
            + info.RelativeOriginOffset.Z * up;
        mat.SetShaderParameter("origin_offset", ToGodot(relOriginQ + info.OriginOffset));
        mat.SetShaderParameter("origin_jitter", AbsToGodot(info.OriginJitter));

        // Velocity ranges: DP lhrandom(velmins,velmaxs) is per-axis; effectinfo expresses the directed velocity
        // through velocitymultiplier*emitvel + velocityoffset, with velmins/maxs typically 0. We pass the emit
        // velocity separately (scaled by velocity_multiplier in the shader) and leave vel_mins/maxs at 0 unless
        // the block carried explicit velocity ranges (effectinfo doesn't expose per-axis velmin/max distinct from
        // velocityoffset, so 0..0 is faithful for the parsed model).
        mat.SetShaderParameter("vel_mins", Vector3.Zero);
        mat.SetShaderParameter("vel_maxs", Vector3.Zero);
        mat.SetShaderParameter("velocity_multiplier", info.VelocityMultiplier);

        NVec3 relVelQ =
            info.RelativeVelocityOffset.X * fwd
            + info.RelativeVelocityOffset.Y * right
            + info.RelativeVelocityOffset.Z * up;
        mat.SetShaderParameter("velocity_offset", ToGodot(info.VelocityOffset + relVelQ));
        mat.SetShaderParameter("velocity_jitter", AbsToGodot(info.VelocityJitter));
        mat.SetShaderParameter("emit_velocity", ToGodot(velocityQuake));

        // Sizes: DP size is world-unit HALF-size; the draw quad scale is the full edge, so 2*size.
        mat.SetShaderParameter("size_range", new Vector2(
            MathF.Max(0.01f, info.SizeMin) * 2f, MathF.Max(0.01f, info.SizeMax) * 2f));

        // Alpha 0..256; alphafade alpha-units/sec.
        mat.SetShaderParameter("alpha_range", new Vector2(info.AlphaMin, info.AlphaMax));
        mat.SetShaderParameter("alpha_fade", info.AlphaFade);

        // time: DP's giant 16777216 default means "no explicit time -> life from alpha fade". Pass 0..0 in that
        // case so the shader's `time_range.y > 0` branch picks the alpha-derived life; otherwise the real range.
        bool explicitTime = info.TimeMin < 16777216f || info.TimeMax < 16777216f;
        mat.SetShaderParameter("time_range", explicitTime
            ? new Vector2(info.TimeMin, info.TimeMax)
            : Vector2.Zero);

        // gravity multiplier (DP applies *800 in the shader).
        mat.SetShaderParameter("gravity_mult", info.Gravity);

        // rotate base range (radians).
        mat.SetShaderParameter("angle_range", new Vector2(
            Deg2Rad(info.RotateBaseMin), Deg2Rad(info.RotateBaseMax)));

        // colors: DP color[0]/color[1] (linear 0..1).
        (float r0, float g0, float b0) = info.Color0Rgb();
        (float r1, float g1, float b1) = info.Color1Rgb();
        mat.SetShaderParameter("color0", new Vector3(r0, g0, b0));
        mat.SetShaderParameter("color1", new Vector3(r1, g1, b1));

        // optional feature uniforms.
        if (f.AirFriction) mat.SetShaderParameter("air_friction", info.AirFriction);
        if (f.Bounce) mat.SetShaderParameter("bounce", info.Bounce);
        if (f.Spark) mat.SetShaderParameter("stretch_factor", ep.StretchFactor);
        if (f.SizeIncrease) mat.SetShaderParameter("size_increase", ep.SizeIncrease);
        if (f.Spin) mat.SetShaderParameter("spin_range", new Vector2(ep.SpinMin, ep.SpinMax));
        // curl_strength is a preset feature — set it only when the curl branch is compiled in (f.Curl is true
        // exactly when preset.CurlNoise > 0, so the uniform exists in the shader).
        if (f.Curl) mat.SetShaderParameter("curl_strength", preset.CurlNoise);
    }

    /// <summary>Build (or reuse) the draw-pass billboard quad for a draw feature shape + sprite. Spark quads are
    /// shaped wide/long like the faithful path's spark mesh but the actual stretch is driven per-frame by the
    /// process shader's basis; the mesh is just a unit quad the shader scales.</summary>
    private Mesh BuildDrawMesh(in ModernParticleShaders.DrawFeatures drawFeat, in ModernPreset preset,
        Texture2D? sprite, EffectInfoEmitter info)
    {
        int featHash = HashCode.Combine(drawFeat.Soft, drawFeat.Lit, drawFeat.Flipbook, drawFeat.EmissiveBoost,
            (int)drawFeat.Blend);
        var key = ((int)drawFeat.Blend, drawFeat.Spark, sprite is null ? 0UL : sprite.GetInstanceId(), featHash);
        if (_drawMeshCache.TryGetValue(key, out Mesh? cached))
            return cached;

        // A unit quad: the per-particle TRANSFORM basis (written by the process shader) carries the world size
        // (billboards) or the velocity-stretched dimensions (sparks). The draw vertex shader reads the column
        // lengths, so the base mesh is always 1×1 (the spark stretch is in the basis, not the mesh).
        var quad = new QuadMesh { Size = new Vector2(1f, 1f) };
        var dm = new ShaderMaterial { Shader = ModernParticleShaders.GetDrawShader(drawFeat) };
        if (sprite is not null)
            dm.SetShaderParameter("albedo_tex", sprite);
        if (drawFeat.Flipbook)
        {
            // Map the block's tex range to a flipbook strip; effectinfo cells aren't a contiguous HxV strip, so a
            // 1-row strip across the range count is the faithful-ish choice (the atlas band is horizontal).
            int frames = Math.Max(1, info.Tex1 - info.Tex0);
            dm.SetShaderParameter("flip_hframes", frames);
            dm.SetShaderParameter("flip_vframes", 1);
        }
        if (drawFeat.EmissiveBoost)
            dm.SetShaderParameter("emissive_boost", Math.Clamp(preset.EmissiveBoost, 0.1f, 8f));
        if (drawFeat.Soft)
            dm.SetShaderParameter("soft_distance", 24f);
        quad.Material = dm;

        _drawMeshCache[key] = quad;
        return quad;
    }

    // =================================================================================================
    //  Helpers
    // =================================================================================================

    private static ModernParticleShaders.ModernBlend ToModernBlend(EiBlend b) => b switch
    {
        EiBlend.Add => ModernParticleShaders.ModernBlend.Add,
        EiBlend.InvMod => ModernParticleShaders.ModernBlend.Sub, // inverse-modulate ≈ subtractive darkening
        _ => ModernParticleShaders.ModernBlend.Alpha,
    };

    private static float Deg2Rad(float deg) => deg * (MathF.PI / 180f);

    private static Vector3 ToGodot(NVec3 q) => Coords.ToGodot(q);

    /// <summary>Componentwise abs of a Quake vector mapped to Godot axes (for radii/jitter extents).</summary>
    private static Vector3 AbsToGodot(NVec3 q)
    {
        Vector3 g = Coords.ToGodot(q);
        return new Vector3(MathF.Abs(g.X), MathF.Abs(g.Y), MathF.Abs(g.Z));
    }

    private static float ParentLinger(IReadOnlyList<EffectInfoEmitter> blocks)
    {
        float max = 0.5f;
        foreach (EffectInfoEmitter b in blocks)
            if (b.Defined && b.Type != EiType.Decal)
                max = MathF.Max(max, b.Lifetime());
        return max + 0.6f;
    }

    private void Track(Node3D node)
    {
        _live.Enqueue(node);
        while (_live.Count > MaxLiveEffects && _live.TryDequeue(out Node3D? old))
            if (GodotObject.IsInstanceValid(old))
                old.QueueFree();
    }

    private void ScheduleFree(Node node, float seconds)
    {
        SceneTree? tree = IsInsideTree() ? GetTree() : node.GetTree();
        if (tree is null)
        {
            node.QueueFree();
            return;
        }
        SceneTreeTimer timer = tree.CreateTimer(seconds);
        timer.Timeout += () =>
        {
            if (GodotObject.IsInstanceValid(node))
                node.QueueFree();
        };
    }
}
