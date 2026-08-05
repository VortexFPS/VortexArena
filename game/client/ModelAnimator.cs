using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Formats.Md3;
using VortexArena.Formats.Sidecars;
using VortexArena.Common.Framework;
using VortexArena.Common.Services;
using VortexArena.Engine.Simulation;
using VortexArena.Game.Loaders;

namespace VortexArena.Game.Client;

/// <summary>
/// Drives MD3 vertex-morph animation playback for a model — the Godot successor to the CSQC
/// frame/anim playback the libs flag with <c>TODO(port,client): … walk/strafe/idle/jump …</c> and
/// <c>tur_head.frame = N</c> style frame sets (players, monsters, turret heads, vehicles).
///
/// MD3 stores geometry as a stack of per-frame vertex positions (see <see cref="Md3Data"/>); there is no
/// skeleton — "animation" is selecting and blending between two frames. Godot's <see cref="AnimationPlayer"/>
/// can't morph an arbitrary ArrayMesh per-frame cheaply, so this does it directly: it keeps the parsed
/// <see cref="Md3Data"/>, advances a playhead through a frame range at a frame rate, and on each tick rebuilds
/// the <see cref="MeshInstance3D"/>'s <see cref="ArrayMesh"/> by linearly interpolating vertex positions and
/// normals between the two bracketing frames (the exact thing DarkPlaces' R_AliasLerpVerts does). Attachment
/// <see cref="Md3Tag"/> markers are re-posed the same way so weapons/effects parented to a tag track the anim.
///
/// Animations are addressed by name through a small frame-range table (<see cref="AnimClip"/>): a model that
/// ships a frame layout (idle 0-29, run 30-59, …) registers clips, then callers <see cref="Play"/> "run" /
/// "idle" / "attack". When an <see cref="Entity"/> is bound, its <see cref="Entity.Frame"/> can also directly
/// drive the playhead (the raw networked frame index), matching QC's <c>self.frame</c> path.
///
/// Coordinates convert Quake -> Godot per vertex via <see cref="Coords"/>, identical to
/// <see cref="ModelLoader.BuildModel"/> (this is effectively its animated form).
/// </summary>
public partial class ModelAnimator : Node3D
{
    /// <summary>A named animation = a contiguous MD3 frame range played at a rate, optionally looped.</summary>
    public readonly record struct AnimClip(string Name, int FirstFrame, int FrameCount, float Fps, bool Loop)
    {
        public int LastFrame => FirstFrame + Math.Max(1, FrameCount) - 1;
    }

    private Md3Data _md3 = null!;
    private AssetSystem? _assets;
    // Per-surface resolved materials (mirrors Md3Morph): re-applied on every morph rebuild in ApplyFrame.
    private readonly List<(Md3Surface surface, Material? material, bool visible)> _surfaces = new();
    private MeshInstance3D _mesh = null!;

    /// <summary>
    /// Pre-allocated per-surface morph buffers, built once by <see cref="BuildMorphBuffers"/> and reused on
    /// every <see cref="ApplyFrame"/>. The static geometry (UV + triangle index) is marshalled into the
    /// surface's <see cref="Godot.Collections.Array"/> once and retained; only the morphing position/normal
    /// arrays are recomputed and re-uploaded each frame. This is what keeps per-frame playback allocation-free
    /// (no <c>new ArrayMesh</c> / no fresh vertex arrays per tick), eliminating the GC churn that otherwise
    /// stutters the game when several animated entities are visible.
    /// </summary>
    private sealed class SurfaceBuffers
    {
        public required Md3Surface Surface;
        public required Material? Material;
        public required Vector3[] Positions;   // mutated + re-uploaded each frame (CPU path) / on bracket change (GPU)
        public required Vector3[] Normals;     // mutated + re-uploaded each frame (CPU path) / on bracket change (GPU)
        public required Godot.Collections.Array Arrays; // holds the static UV/Index Variants across frames

        // --- GPU vertex-morph path (cl_gpu_morph, item 3.3 Tier-3) -----------------------------------------
        // Only populated when this surface uses GPU morph (Gpu==true). Then frameA lives in Positions/Normals
        // (ARRAY_VERTEX/ARRAY_NORMAL) and frameB in two RgbaFloat custom channels (CUSTOM0=pos, CUSTOM1=nrm),
        // both refreshed only when the (frameA,frameB) bracket changes; per render frame only morph_amount moves.
        public bool Gpu;
        public ShaderMaterial? MorphMaterial; // the wrapping Md3Morph material (replicates the StandardMaterial3D look)
        public float[]? CustomB0;             // CUSTOM0: frameB position, 4 floats/vertex (.w unused)
        public float[]? CustomB1;             // CUSTOM1: frameB normal,   4 floats/vertex (.w unused)
    }
    private readonly List<SurfaceBuffers> _surfaceBuffers = new();
    private ArrayMesh? _morphMesh;   // single persistent mesh — surfaces are updated in place, never reassigned
    private bool _morphInit;

    // GPU vertex-morph (cl_gpu_morph, item 3.3 Tier-3). All-or-nothing per model: GPU only when EVERY visible
    // surface resolved to a StandardMaterial3D/null (so the mesh never mixes a persistent GPU surface with the
    // CPU ClearSurfaces churn). Read once at build time; default 0 keeps the CPU path byte-identical. When on,
    // the surfaces are uploaded ONCE (with the custom-channel format) and thereafter only morph_amount is set
    // each frame — the streams refresh only when the (frameA,frameB) bracket changes (~frame-rate, not render-
    // rate). _lastBracketA/_lastBracketB track the last uploaded bracket so a t-only change skips the re-upload.
    private bool _gpuMorph;
    private bool _gpuSurfacesUploaded;
    private int _lastBracketA = int.MinValue, _lastBracketB = int.MinValue;

    // The morph output (surface buffers + tag poses) is a pure function of (frameA, frameB, t), so when those
    // inputs are byte-identical to the last applied frame — a static pickup, a weapon at rest, a clip parked on
    // one frame — re-emitting them would just re-upload the same vertex/normal buffers to the GPU. Track the last
    // applied triple and skip the rebuild when it recurs. Reset by BuildMorphBuffers so a (re)build re-applies.
    private bool _haveLastFrame;
    private int _lastFrameA = int.MinValue, _lastFrameB = int.MinValue;
    private float _lastFrameT = float.NaN;
    private Node3D? _tagsRoot;
    private readonly List<(Md3Tag tag, Marker3D marker)> _tagMarkers = new();

    private readonly Dictionary<string, AnimClip> _clips = new(StringComparer.OrdinalIgnoreCase);
    private AnimClip _current;
    private bool _hasClip;
    private float _playhead;     // fractional frame position WITHIN the current clip (0..FrameCount)
    private bool _playing;

    /// <summary>Optional bound entity: when set, <see cref="Entity.Frame"/> can drive the raw playhead.</summary>
    public Entity? Entity { get; set; }

    /// <summary>If true and an <see cref="Entity"/> is bound, follow <see cref="Entity.Frame"/> instead of a clip.</summary>
    [Export] public bool FollowEntityFrame { get; set; }

    /// <summary>
    /// When set (non-local player MD3 models — the CSQC <c>!isplayer</c> skeleton branch), the raw networked
    /// frame is routed through <see cref="CsqcFallbackFrame.Remap"/> before playback so a model missing the
    /// melee / duckwalk-variant anims falls back to a frame it has (csqcmodel_hooks.qc:702). Off by default.
    /// </summary>
    [Export] public bool UseFallbackFrame { get; set; }

    /// <summary>Global playback speed multiplier.</summary>
    [Export] public float TimeScale { get; set; } = 1f;

    /// <summary>The mesh node this animator rebuilds each tick (exposed so callers can attach materials).</summary>
    public MeshInstance3D MeshInstance => _mesh;

    /// <summary>The currently playing clip name, or "" when idle / frame-driven.</summary>
    public string CurrentClip => _hasClip ? _current.Name : "";

    /// <summary>
    /// True while a clip is actively advancing. A non-looping clip clears this when its playhead reaches the end
    /// (<see cref="Advance"/> sets <c>_playing = false</c>), so callers can detect when a one-shot fire/reload clip
    /// has finished and re-assert the idle clip (Base <c>viewmodel_draw</c> 335-337).
    /// </summary>
    public bool IsPlaying => _playing && _hasClip;

    // =================================================================================================
    //  Construction
    // =================================================================================================

    /// <summary>
    /// [crash fix 2026-07-26] Deterministic MAIN-THREAD release of every exclusively-owned native
    /// resource. Without this, the ArrayMesh + per-surface duplicated ShaderMaterials + marshalled
    /// Variant arrays are freed by GodotSharp finalizers on the .NET FINALIZER thread — whose
    /// RenderingServer::free calls race the main thread's ClearSurfaces/AddSurfaceFromArrays on the
    /// same RenderingDevice allocator (0xC0000374 mid-session heap corruption), and whose end-of-run
    /// backlog is what the at-exit "Attempted to free invalid ID" spam drains. sb.Material is the
    /// SHARED AssetSystem-cache material and is deliberately not touched.
    /// </summary>
    public override void _ExitTree()
    {
        if (GodotObject.IsInstanceValid(_mesh))
            _mesh.Mesh = null;
        _morphMesh?.Dispose();
        _morphMesh = null;
        foreach (SurfaceBuffers sb in _surfaceBuffers)
        {
            sb.MorphMaterial?.Dispose();
            sb.MorphMaterial = null;
            sb.Arrays.Dispose();
        }
        _surfaceBuffers.Clear();
        _surfaces.Clear();
        _tagMarkers.Clear();
        _morphInit = false;
        _gpuSurfacesUploaded = false;
    }

    /// <summary>
    /// Build an animator for a parsed MD3. Creates the initial mesh (frame 0) and tag markers, and
    /// auto-registers a sensible default clip set named from the MD3's frame names when recognisable
    /// (idle/run/walk/jump/death/attack), so a model is animatable out of the box.
    /// </summary>
    public static ModelAnimator Create(Md3Data md3, string? name = null, AssetSystem? assets = null, SkinFile? skin = null)
    {
        var a = new ModelAnimator { Name = string.IsNullOrEmpty(name) ? (md3.Name.Length > 0 ? md3.Name : "Model") : name };
        a.Initialize(md3, assets, skin);
        return a;
    }

    private void Initialize(Md3Data md3, AssetSystem? assets, SkinFile? skin)
    {
        _md3 = md3 ?? throw new ArgumentNullException(nameof(md3));
        _assets = assets;
        ResolveSurfaces(skin);

        _mesh = ModelLoader.BuildModel(_md3, 0, _assets, skin);
        _mesh.Name = "Mesh";
        AddChild(_mesh);

        _tagsRoot = ModelLoader.BuildTags(_md3, 0);
        AddChild(_tagsRoot);
        foreach (Node child in _tagsRoot.GetChildren())
            if (child is Marker3D m)
            {
                Md3Tag? tag = FindTag(m.Name);
                if (tag is not null)
                    _tagMarkers.Add((tag, m));
            }

        AutoRegisterClips();

        // Default to the first registered clip (idle if present), else just hold frame 0.
        if (_clips.TryGetValue("idle", out AnimClip idle)) Play(idle.Name);
        else if (_clips.Count > 0)
        {
            foreach (AnimClip c in _clips.Values) { Play(c.Name); break; }
        }
    }

    /// <summary>
    /// Resolve each surface's material once (skin remap by mesh name, else the surface's own first shader;
    /// a nodraw remap hides it), so <see cref="ApplyFrame"/> can re-attach per-surface materials on every
    /// morph rebuild. Mirrors <c>Md3Morph.ResolveSurfaces</c>. With no <see cref="AssetSystem"/> the materials
    /// stay null (untextured — legacy behavior).
    /// </summary>
    private void ResolveSurfaces(SkinFile? skin)
    {
        foreach (Md3Surface surface in _md3.Surfaces)
        {
            string shader = surface.Shaders.Length > 0 ? surface.Shaders[0] : surface.Name;
            bool visible = true;
            if (skin is not null && skin.MeshToTexture.TryGetValue(surface.Name, out string? remap))
            {
                if (SkinFile.IsNoDraw(remap))
                    visible = false;
                else if (!string.IsNullOrEmpty(remap))
                    shader = remap;
            }
            Material? material = visible ? _assets?.ResolveModelMaterial(shader) : null;
            _surfaces.Add((surface, material, visible));
        }
    }

    /// <summary>Look up the attachment marker for a tag by name (for parenting weapons/effects).</summary>
    public Marker3D? GetTag(string tagName)
    {
        foreach ((Md3Tag tag, Marker3D marker) in _tagMarkers)
            if (string.Equals(tag.Name, tagName, StringComparison.Ordinal))
                return marker;
        return null;
    }

    // =================================================================================================
    //  Clip registration + playback control
    // =================================================================================================

    /// <summary>Register (or replace) a named animation clip. Frame indices are clamped to the model's range.</summary>
    public void AddClip(string name, int firstFrame, int frameCount, float fps = 10f, bool loop = true)
    {
        int max = Math.Max(1, _md3.FrameCount);
        firstFrame = Math.Clamp(firstFrame, 0, max - 1);
        frameCount = Math.Clamp(frameCount, 1, max - firstFrame);
        _clips[name] = new AnimClip(name, firstFrame, frameCount, fps <= 0f ? 10f : fps, loop);
    }

    /// <summary>Start playing a registered clip from its first frame. No-op if the name is unknown.</summary>
    public bool Play(string clipName)
    {
        if (!_clips.TryGetValue(clipName, out AnimClip clip))
            return false;
        _current = clip;
        _hasClip = true;
        _playhead = 0f;
        _playing = true;
        FollowEntityFrame = false;
        ApplyFrame(clip.FirstFrame, clip.FirstFrame, 0f);
        return true;
    }

    /// <summary>Switch clips only if a different one is requested (avoids restarting the same loop each frame).</summary>
    public bool PlayIfChanged(string clipName)
    {
        if (_hasClip && string.Equals(_current.Name, clipName, StringComparison.OrdinalIgnoreCase))
            return true;
        return Play(clipName);
    }

    public void Pause() => _playing = false;
    public void Resume() => _playing = true;

    /// <summary>Jump straight to a raw MD3 frame index (QC <c>self.frame = N</c>), holding it (no playback).</summary>
    public void SetRawFrame(int frame)
    {
        _hasClip = false;
        _playing = false;
        int f = Math.Clamp(frame, 0, Math.Max(0, _md3.FrameCount - 1));
        ApplyFrame(f, f, 0f);
    }

    // =================================================================================================
    //  Per-frame advance
    // =================================================================================================

    public override void _Process(double delta)
    {
        Advance((float)delta);
    }

    /// <summary>Advance the playhead and rebuild the morph mesh for this frame (call once per frame).</summary>
    public void Advance(float delta)
    {
        using var _animScope = VortexArena.Game.Client.FrameProfiler.Scope("md3.morph"); // [profiling] all MD3 morph this frame
        if (_md3.FrameCount <= 1)
            return;

        // Entity-frame-driven mode: take the raw networked frame as the playhead directly.
        if (FollowEntityFrame && Entity is not null)
        {
            float ef = Entity.Frame;
            int f0 = (int)MathF.Floor(ef);
            int f1 = f0 + 1;
            float t = ef - f0;
            // Non-local player MD3 models: remap a missing anim frame to a present one (CSQC FallbackFrame).
            // QC runs CSQCPlayer_FallbackFrame on BOTH frame and frame2 independently (csqcmodel_hooks.qc:435-443),
            // so remap each bracketing endpoint — not just the floor frame — before interpolating between them.
            if (UseFallbackFrame)
            {
                f0 = CsqcFallbackFrame.Remap(f0, FrameDuration);
                f1 = CsqcFallbackFrame.Remap(f1, FrameDuration);
            }
            ApplyFrame(WrapRaw(f0), WrapRaw(f1), t);
            return;
        }

        if (!_playing || !_hasClip)
            return;

        _playhead += delta * _current.Fps * Math.Max(0f, TimeScale);

        float span = _current.FrameCount;
        if (_playhead >= span)
        {
            if (_current.Loop)
                _playhead = span <= 0f ? 0f : _playhead % span;
            else
            {
                _playhead = span - 0.0001f;
                _playing = false;
            }
        }

        // Bracket the two frames inside the clip and the interpolation weight between them.
        int local0 = (int)MathF.Floor(_playhead);
        int local1 = _current.Loop ? (local0 + 1) % _current.FrameCount : Math.Min(local0 + 1, _current.FrameCount - 1);
        float frac = _playhead - MathF.Floor(_playhead);

        int frameA = _current.FirstFrame + Math.Clamp(local0, 0, _current.FrameCount - 1);
        int frameB = _current.FirstFrame + Math.Clamp(local1, 0, _current.FrameCount - 1);
        ApplyFrame(frameA, frameB, frac);
    }

    private int WrapRaw(int f)
    {
        int n = _md3.FrameCount;
        if (n <= 0) return 0;
        return ((f % n) + n) % n;
    }

    /// <summary>
    /// QC <c>frameduration(modelindex, f)</c> probe for the fallback-frame remap: a positive duration when the
    /// model has frame <paramref name="f"/> (in <c>[0, FrameCount)</c>), 0 otherwise. MD3 morph has no stored
    /// per-frame duration, so a present frame returns a nominal 1/10s and an absent frame returns 0 — the only
    /// distinction <see cref="CsqcFallbackFrame.Remap"/> cares about (it tests <c>&gt; 0</c>).
    /// </summary>
    public float FrameDuration(int f) => (f >= 0 && f < _md3.FrameCount) ? 0.1f : 0f;

    /// <summary>True when the model has frame <paramref name="f"/> (QC <c>frameduration(...) &gt; 0</c>).</summary>
    public bool FrameExists(int f) => FrameDuration(f) > 0f;

    // =================================================================================================
    //  The morph itself — rebuild the ArrayMesh by lerping frame A -> frame B
    // =================================================================================================

    /// <summary>
    /// Allocate the persistent morph mesh and per-surface buffers once. The UV coordinates and triangle
    /// indices never change between MD3 frames, so they're computed and marshalled into each surface's
    /// <see cref="Godot.Collections.Array"/> here a single time and retained; only the position/normal arrays
    /// are re-filled and re-uploaded per frame in <see cref="ApplyFrame"/>. Skips degenerate / nodraw surfaces
    /// up front so the per-frame loop stays tight.
    /// </summary>
    private void BuildMorphBuffers()
    {
        _morphInit = true;
        _haveLastFrame = false;   // a (re)build must re-apply the current frame even if its inputs match the cache
        _gpuSurfacesUploaded = false;
        _lastBracketA = int.MinValue; _lastBracketB = int.MinValue;
        _morphMesh = new ArrayMesh { ResourceName = "Md3Morph" };

        // GPU vertex-morph gate (cl_gpu_morph), read ONCE here. All-or-nothing per model: GPU when EVERY
        // visible drawable surface resolved to a StandardMaterial3D/null (wrapped in Md3MorphShader) or a
        // PlayerSkinShader material (r16: the skin shader carries the same morph vertex stage, driven on a
        // per-surface Duplicate() — the CTF flag's team skin was the one hot morph model stuck on the CPU
        // path). Any OTHER ShaderMaterial (generated animated stage / hero) still can't carry a vertex()
        // morph and drops the WHOLE model back to CPU — mixing a persistent GPU surface with the CPU
        // ClearSurfaces path on one mesh is incompatible. (Live-toggling the cvar needs a rebuild; see
        // Advance.) Default ON since the §13 flip (registered "1" in ClientSettings; the fallback here
        // matches for store-less contexts). `cl_gpu_morph 0` restores the CPU upload path.
        _gpuMorph = CvarF("cl_gpu_morph", 1f) != 0f && AllSurfacesGpuEligible();

        foreach ((Md3Surface surface, Material? material, bool visible) in _surfaces)
        {
            if (!visible)
                continue;
            int vcount = surface.VertexCount;
            if (vcount <= 0 || surface.Triangles.Length == 0 || surface.FrameVertices.Length == 0)
                continue;

            var uvs = new Vector2[vcount];
            for (int v = 0; v < vcount; v++)
                uvs[v] = v < surface.TexCoords.Length
                    ? new Vector2(surface.TexCoords[v].X, surface.TexCoords[v].Y)
                    : Vector2.Zero;

            var indices = new int[surface.Triangles.Length];
            for (int i = 0; i < surface.Triangles.Length; i++)
            {
                int idx = surface.Triangles[i];
                indices[i] = (idx >= 0 && idx < vcount) ? idx : 0;
            }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            // UV + index are static across frames — assign once (marshalled to Packed* Variants and retained).
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            arrays[(int)Mesh.ArrayType.Index] = indices;

            var sb = new SurfaceBuffers
            {
                Surface = surface,
                Material = material,
                Positions = new Vector3[vcount],
                Normals = new Vector3[vcount],
                Arrays = arrays,
            };

            if (_gpuMorph)
            {
                // Wrap the resolved material into a morph-capable one and pre-size the two custom-channel
                // float buffers (4 floats/vertex, RgbaFloat). Frame data is filled in ApplyFrame on the first
                // apply / bracket change. A StandardMaterial3D (or null) wraps into the Md3Morph shader; a
                // PlayerSkinShader material (the CTF flag's team skin — r16) is Duplicate()d instead: the
                // duplicate shares the same Shader object (no new pipeline family) and copies the texture
                // params, the per-entity tints stay INSTANCE uniforms on the MeshInstance (unaffected), and
                // the per-surface copy gives this animator a private morph_amount to drive.
                sb.Gpu = true;
                sb.MorphMaterial = material is ShaderMaterial morphable
                    ? (ShaderMaterial)morphable.Duplicate() // skin/animated-stage shader with its own morph_amount
                    : BuildMorphMaterial(material as StandardMaterial3D);
                sb.CustomB0 = new float[vcount * 4];
                sb.CustomB1 = new float[vcount * 4];
            }

            _surfaceBuffers.Add(sb);
        }

        // Assign the persistent mesh to the instance once; from here on we mutate its surfaces in place so
        // the MeshInstance is never re-pointed at a new resource (which would re-trigger render setup).
        // MaterialOverride lives on the MeshInstance, not the mesh, so it survives in-place surface rebuilds.
        // [crash fix 2026-07-26] The frame-0 mesh ModelLoader.BuildModel uploaded in Initialize is orphaned
        // by this assignment — dispose it NOW on the main thread instead of leaving a fully-uploaded
        // mesh + its RD buffers for the finalizer thread (one per animator build).
        // (bisect-verified 2026-07-26: this dispose is NOT the residual-crash cause — both crash
        // signatures reproduce with it disabled. RenderingServer defers actual buffer frees internally,
        // so a same-frame Dispose of the replaced mesh is safe.)
        Mesh? initialMesh = _mesh.Mesh;
        _mesh.Mesh = _morphMesh;
        // (2026-08-02) Never dispose a CACHE-owned mesh (IqmBuilder.SharedSkinnedGeometry) — freeing it here
        // would pull the geometry out from under every other user. This animator only ever drives MD3 (its own
        // per-instance build), so the guard is insurance against a future IQM/DPM caller, same rule as
        // PlayerModel._ExitTree.
        if (initialMesh is not null && initialMesh != _morphMesh
            && !VortexArena.Game.Loaders.Models.IqmBuilder.IsShared(initialMesh))
            initialMesh.Dispose();

        // One-shot build breadcrumb: which morph path this model took and why (per-model, boot/spawn-time
        // volume). The r16 flag investigation needed exactly this to see WHY a model stayed on the CPU path.
        if (_md3.FrameCount > 1)
        {
            var mats = new System.Text.StringBuilder();
            foreach ((Md3Surface s, Material? m, bool vis) in _surfaces)
                if (vis) mats.Append(m switch
                {
                    null => "null ",
                    StandardMaterial3D => "std ",
                    ShaderMaterial sm2 when sm2.Shader?.Code?.Contains("morph_amount") == true => "morphable ",
                    ShaderMaterial => "shader! ", // no morph vertex stage — pins the model to the CPU path
                    _ => "other! ",
                });
            VortexArena.Common.Diagnostics.Log.Info(
                $"[md3morph] '{_md3.Name}' frames={_md3.FrameCount} gpu={_gpuMorph} mats: {mats.ToString().TrimEnd()}");
        }
    }

    /// <summary>
    /// True when EVERY visible, drawable surface resolved to a GPU-morphable material: a
    /// <see cref="StandardMaterial3D"/>/null (wrapped in <see cref="Md3MorphShader"/>), or a
    /// <see cref="PlayerSkinShader"/> material (r16: the shader now carries the same CUSTOM0/1 +
    /// <c>morph_amount</c> vertex stage, so a team-tintable skin model — the CTF flag, whose per-frame CPU
    /// rebuild cost 4.4ms with 14 flags — morphs GPU-side via a per-surface material Duplicate()). Any OTHER
    /// ShaderMaterial (a generated animated stage / hero material) still lacks a vertex() morph and drops the
    /// whole model to the CPU path (all-or-nothing — mixing persistent GPU surfaces with the CPU
    /// ClearSurfaces churn on one mesh is incompatible).
    /// </summary>
    private bool AllSurfacesGpuEligible()
    {
        foreach ((Md3Surface surface, Material? material, bool visible) in _surfaces)
        {
            if (!visible)
                continue;
            if (surface.VertexCount <= 0 || surface.Triangles.Length == 0 || surface.FrameVertices.Length == 0)
                continue;
            if (material is null or StandardMaterial3D)
                continue;
            // Any ShaderMaterial whose shader carries the morph_amount vertex stage is morph-capable —
            // PlayerSkinShader (the flag's team skin) and the generated animated-stage shaders (the flag's
            // scrolling-energy surface) both do; a hero/other material without it drops the model to CPU.
            if (material is ShaderMaterial sm && sm.Shader is Shader sh
                && sh.Code is string code && code.Contains("morph_amount"))
                continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Wrap a resolved <see cref="StandardMaterial3D"/> (or null) into an <see cref="Md3MorphShader"/> material:
    /// a GPU vertex-morph shader whose <c>fragment()</c> replicates the StandardMaterial3D look the CPU path
    /// draws (albedo + optional gloss→roughness / glow→emission). Mirrors how <see cref="PlayerSkinShader"/>
    /// replaces a StandardMaterial3D it can't express. Normal maps are intentionally NOT bound (has_normal stays
    /// false) to match today's morph look — the CPU StandardMaterial3D path supplies no tangents either, so a
    /// bound normal map would shift the look rather than preserve it (see the tangents note in the spec).
    /// </summary>
    private static ShaderMaterial BuildMorphMaterial(StandardMaterial3D? source)
    {
        var mat = new ShaderMaterial { Shader = Md3MorphShader.Shader };
        if (source is not null)
        {
            mat.SetShaderParameter(Md3MorphShader.AlbedoUniform, source.AlbedoTexture);

            // gloss→roughness: WireCompanions feeds the gloss map as the StandardMaterial3D RoughnessTexture
            // (grayscale, scalar Roughness 1.0); the shader samples .g and inverts it. Treat a bound roughness
            // texture as the gloss map (the only way the plain-material path sets one).
            if (source.RoughnessTexture is not null)
            {
                mat.SetShaderParameter(Md3MorphShader.GlossUniform, source.RoughnessTexture);
                mat.SetShaderParameter(Md3MorphShader.HasGlossUniform, true);
            }

            // glow→emission: WireCompanions sets EmissionTexture (+ black base emission) for the _glow companion.
            if (source.EmissionEnabled && source.EmissionTexture is not null)
            {
                mat.SetShaderParameter(Md3MorphShader.GlowUniform, source.EmissionTexture);
                mat.SetShaderParameter(Md3MorphShader.HasGlowUniform, true);
            }
        }
        return mat;
    }

    /// <summary>
    /// Read a float cvar once with a fallback default (copied from <c>CsqcModelEffects.CvarF</c>). Guards a null
    /// service container (editor / headless / tests where <see cref="Api.Services"/> isn't wired) by returning
    /// the fallback, so the GPU gate defaults to OFF (current CPU behavior) when cvars are unavailable.
    /// </summary>
    private static float CvarF(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : Api.Cvars.GetFloat(name);
    }

    /// <summary>
    /// Rebuild the mesh surfaces interpolating each vertex between <paramref name="frameA"/> and
    /// <paramref name="frameB"/> by <paramref name="t"/> (0 = A, 1 = B), and re-pose the tag markers — the
    /// per-tick cost of MD3 morph playback (DarkPlaces' R_AliasLerpVerts). Reuses the buffers built by
    /// <see cref="BuildMorphBuffers"/>: only the morphing position/normal arrays are recomputed and uploaded,
    /// on a single persistent <see cref="ArrayMesh"/>, so a tick allocates nothing on the managed heap.
    /// </summary>
    private void ApplyFrame(int frameA, int frameB, float t)
    {
        if (!_morphInit)
            BuildMorphBuffers();

        // Nothing to do when the interpolation inputs haven't moved since the last upload (see _haveLastFrame).
        if (_haveLastFrame && frameA == _lastFrameA && frameB == _lastFrameB && t == _lastFrameT)
            return;
        _haveLastFrame = true;
        _lastFrameA = frameA; _lastFrameB = frameB; _lastFrameT = t;

        if (_morphMesh is not null && _surfaceBuffers.Count > 0)
        {
            if (_gpuMorph)
                ApplyFrameGpu(frameA, frameB, t);
            else
                ApplyFrameCpu(frameA, frameB, t);
        }

        PoseTags(frameA, frameB, t);
    }

    /// <summary>
    /// The CPU morph path (default, <c>cl_gpu_morph 0</c>): lerp every vertex on the CPU and re-upload both
    /// morphing streams each tick. Byte-identical to the legacy single-method implementation — DarkPlaces'
    /// R_AliasLerpVerts done host-side.
    /// </summary>
    private void ApplyFrameCpu(int frameA, int frameB, float t)
    {
        _morphMesh!.ClearSurfaces();
        int surfaceIndex = 0;
        foreach (SurfaceBuffers sb in _surfaceBuffers)
        {
            Md3Surface surface = sb.Surface;
            int vcount = surface.VertexCount;
            int fa = Math.Clamp(frameA, 0, surface.FrameVertices.Length - 1);
            int fb = Math.Clamp(frameB, 0, surface.FrameVertices.Length - 1);
            Md3Vertex[] va = surface.FrameVertices[fa];
            Md3Vertex[] vb = surface.FrameVertices[fb];
            if (va.Length < vcount || vb.Length < vcount)
                continue;

            Vector3[] positions = sb.Positions;
            Vector3[] normals = sb.Normals;
            for (int v = 0; v < vcount; v++)
            {
                // Lerp in Quake space, then convert — equivalent to lerping the converted values.
                System.Numerics.Vector3 p = System.Numerics.Vector3.Lerp(va[v].Position, vb[v].Position, t);
                System.Numerics.Vector3 nrm = System.Numerics.Vector3.Lerp(va[v].Normal, vb[v].Normal, t);
                positions[v] = Coords.ToGodot(p);
                Vector3 gn = Coords.ToGodot(nrm);
                normals[v] = gn.LengthSquared() > 1e-8f ? gn.Normalized() : Vector3.Up;
            }

            // Only the morphing streams are re-uploaded; UV + index Variants stay as set in BuildMorphBuffers.
            sb.Arrays[(int)Mesh.ArrayType.Vertex] = positions;
            sb.Arrays[(int)Mesh.ArrayType.Normal] = normals;
            _morphMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, sb.Arrays);
            if (sb.Material is not null)
                _morphMesh.SurfaceSetMaterial(surfaceIndex, sb.Material);
            surfaceIndex++;
        }
    }

    /// <summary>
    /// The GPU vertex-morph path (<c>cl_gpu_morph 1</c>, all StandardMaterial3D/null surfaces). frameA lives in
    /// ARRAY_VERTEX/ARRAY_NORMAL and frameB in two RgbaFloat custom channels (CUSTOM0=pos, CUSTOM1=nrm); the
    /// Md3Morph shader's vertex() lerps them by <c>morph_amount</c>. Those two streams refresh only when the
    /// (frameA,frameB) bracket changes — at ~frame-rate, not render-rate — so the steady-state per-render-frame
    /// cost of a smoothly-playing clip is a single <c>morph_amount</c> uniform set per surface (the item-3.3 win).
    /// </summary>
    private void ApplyFrameGpu(int frameA, int frameB, float t)
    {
        // The streams (frameA + the frameB customs) only depend on the bracket; refresh them when it moves. The
        // model is all-or-nothing GPU, so a full ClearSurfaces + re-add is safe here (no CPU surface to preserve).
        bool bracketChanged = !_gpuSurfacesUploaded || frameA != _lastBracketA || frameB != _lastBracketB;
        if (bracketChanged)
        {
            _morphMesh!.ClearSurfaces();
            int surfaceIndex = 0;
            foreach (SurfaceBuffers sb in _surfaceBuffers)
            {
                Md3Surface surface = sb.Surface;
                int vcount = surface.VertexCount;
                int fa = Math.Clamp(frameA, 0, surface.FrameVertices.Length - 1);
                int fb = Math.Clamp(frameB, 0, surface.FrameVertices.Length - 1);
                Md3Vertex[] va = surface.FrameVertices[fa];
                Md3Vertex[] vb = surface.FrameVertices[fb];
                if (va.Length < vcount || vb.Length < vcount)
                    continue;

                Vector3[] positions = sb.Positions; // ARRAY_VERTEX = frameA (Godot space)
                Vector3[] normals = sb.Normals;      // ARRAY_NORMAL = frameA (Godot space)
                float[] cb0 = sb.CustomB0!;          // CUSTOM0 = frameB position, RgbaFloat (4 floats/vertex)
                float[] cb1 = sb.CustomB1!;          // CUSTOM1 = frameB normal,   RgbaFloat (4 floats/vertex)
                for (int v = 0; v < vcount; v++)
                {
                    // frameA into the standard streams (same Quake->Godot conversion as the CPU path).
                    positions[v] = Coords.ToGodot(va[v].Position);
                    Vector3 gnA = Coords.ToGodot(va[v].Normal);
                    normals[v] = gnA.LengthSquared() > 1e-8f ? gnA.Normalized() : Vector3.Up;

                    // frameB into the custom channels (already Godot space; the shader mixes A->B by morph_amount,
                    // equivalent to lerping Quake values then converting — see Md3MorphShader). Normalize to match.
                    Vector3 pB = Coords.ToGodot(vb[v].Position);
                    Vector3 gnB = Coords.ToGodot(vb[v].Normal);
                    Vector3 nB = gnB.LengthSquared() > 1e-8f ? gnB.Normalized() : Vector3.Up;
                    int o = v * 4;
                    cb0[o] = pB.X; cb0[o + 1] = pB.Y; cb0[o + 2] = pB.Z; cb0[o + 3] = 0f;
                    cb1[o] = nB.X; cb1[o + 1] = nB.Y; cb1[o + 2] = nB.Z; cb1[o + 3] = 0f;
                }

                sb.Arrays[(int)Mesh.ArrayType.Vertex] = positions;
                sb.Arrays[(int)Mesh.ArrayType.Normal] = normals;
                sb.Arrays[(int)Mesh.ArrayType.Custom0] = cb0;
                sb.Arrays[(int)Mesh.ArrayType.Custom1] = cb1;

                // Declare the two custom channels' format in the surface flags: one ArrayCustomFormat code per
                // channel, left-shifted into that channel's FormatCustomN shift, OR'd with the channel presence
                // bit. RgbaFloat => the Custom array is a float[] of 4 floats/vertex (verified against the Godot
                // 4.6 GodotSharp.xml docs for AddSurfaceFromArrays / ArrayType.Custom0/Custom1).
                const uint customFmt =
                    ((uint)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift)
                    | (uint)Mesh.ArrayFormat.FormatCustom0
                    | ((uint)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom1Shift)
                    | (uint)Mesh.ArrayFormat.FormatCustom1;

                // Pass blendShapes/lods explicitly null (positional) so the custom-format flags reach the 5th
                // arg regardless of the binding's parameter naming.
                _morphMesh.AddSurfaceFromArrays(
                    Mesh.PrimitiveType.Triangles, sb.Arrays,
                    null, null, (Mesh.ArrayFormat)customFmt);
                if (sb.MorphMaterial is not null)
                {
                    sb.MorphMaterial.SetShaderParameter(Md3MorphShader.MorphAmountUniform, t);
                    _morphMesh.SurfaceSetMaterial(surfaceIndex, sb.MorphMaterial);
                }
                surfaceIndex++;
            }
            _gpuSurfacesUploaded = true;
            _lastBracketA = frameA;
            _lastBracketB = frameB;
        }
        else
        {
            // Steady state: same bracket, only t moved. The single cheap interop call per surface (the win).
            foreach (SurfaceBuffers sb in _surfaceBuffers)
                sb.MorphMaterial?.SetShaderParameter(Md3MorphShader.MorphAmountUniform, t);
        }
    }

    /// <summary>Re-pose each attachment tag marker by interpolating its transform between the two frames.</summary>
    private void PoseTags(int frameA, int frameB, float t)
    {
        foreach ((Md3Tag tag, Marker3D marker) in _tagMarkers)
        {
            if (tag.Transforms.Length == 0)
                continue;
            int fa = Math.Clamp(frameA, 0, tag.Transforms.Length - 1);
            int fb = Math.Clamp(frameB, 0, tag.Transforms.Length - 1);
            Md3TagTransform a = tag.Transforms[fa];
            Md3TagTransform b = tag.Transforms[fb];

            Vector3 origin = Coords.ToGodot(System.Numerics.Vector3.Lerp(a.Origin, b.Origin, t));
            // Slerp the basis by lerping axes then re-orthonormalising (cheap, good enough for tags).
            var basis = new Basis(
                Coords.ToGodot(System.Numerics.Vector3.Lerp(a.AxisX, b.AxisX, t)),
                Coords.ToGodot(System.Numerics.Vector3.Lerp(a.AxisY, b.AxisY, t)),
                Coords.ToGodot(System.Numerics.Vector3.Lerp(a.AxisZ, b.AxisZ, t))).Orthonormalized();

            marker.Transform = new Transform3D(basis, origin);
        }
    }

    // =================================================================================================
    //  Auto clip detection from MD3 frame names
    // =================================================================================================

    /// <summary>
    /// Build clips from the MD3's frame names when they look like Quake/Xonotic anim labels. MD3 frame
    /// names typically read like "idle1", "run1", "death1"; we group contiguous same-prefix runs into a
    /// clip. Falls back to a single full-range "all" clip when names are absent/uninformative.
    /// </summary>
    private void AutoRegisterClips()
    {
        Md3Frame[] frames = _md3.Frames;
        if (frames.Length == 0)
        {
            AddClip("all", 0, Math.Max(1, _md3.FrameCount), 10f, true);
            return;
        }

        string Prefix(string s)
        {
            int end = s.Length;
            while (end > 0 && char.IsDigit(s[end - 1])) end--;
            return s[..end].ToLowerInvariant().Trim();
        }

        int runStart = 0;
        string runName = Prefix(frames[0].Name);
        bool anyNamed = false;

        void Flush(int start, int endExclusive, string label)
        {
            if (string.IsNullOrEmpty(label) || endExclusive <= start)
                return;
            anyNamed = true;
            (float fps, bool loop) = ClipPlayback(label);
            // De-dup names by appending the start frame if a clip with that name already exists.
            string name = _clips.ContainsKey(label) ? $"{label}@{start}" : label;
            AddClip(name, start, endExclusive - start, fps, loop);
        }

        for (int i = 1; i < frames.Length; i++)
        {
            string p = Prefix(frames[i].Name);
            if (p != runName)
            {
                Flush(runStart, i, runName);
                runStart = i;
                runName = p;
            }
        }
        Flush(runStart, frames.Length, runName);

        if (!anyNamed)
            AddClip("all", 0, _md3.FrameCount, 10f, true);
    }

    /// <summary>Per-anim-name default playback (fps + loop). Mirrors the QC frame-rate conventions roughly.</summary>
    private static (float fps, bool loop) ClipPlayback(string name)
    {
        if (name.Contains("idle") || name.Contains("stand") || name.Contains("wait")) return (8f, true);
        if (name.Contains("run") || name.Contains("walk") || name.Contains("strafe")) return (15f, true);
        if (name.Contains("jump") || name.Contains("fall")) return (12f, false);
        if (name.Contains("death") || name.Contains("die") || name.Contains("dead")) return (12f, false);
        if (name.Contains("attack") || name.Contains("shoot") || name.Contains("fire")) return (20f, false);
        if (name.Contains("pain") || name.Contains("hit")) return (15f, false);
        return (10f, true);
    }

    private Md3Tag? FindTag(string markerName)
    {
        foreach (Md3Tag tag in _md3.Tags)
            if (string.Equals(tag.Name, markerName, StringComparison.Ordinal))
                return tag;
        return null;
    }
}
