using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Formats.Materials;
using XonoticGodot.Formats.Md3;
using XonoticGodot.Formats.Sidecars;

namespace XonoticGodot.Game.Loaders.Models;

/// <summary>
/// Turns a parsed <see cref="Md3Data"/> (the Godot-free id Tech 3 MD3 importer output) into a Godot
/// scene node. MD3 is a <b>vertex-morph</b> format (no skeleton — contrast <see cref="DpmBuilder"/>):
/// geometry is a stack of per-frame vertex positions and "animation" is selecting/blending two frames.
///
/// <para>The returned root is an <see cref="Md3Morph"/> node that:</para>
/// <list type="bullet">
///   <item>Holds a <see cref="MeshInstance3D"/> built from a chosen frame (default 0); each MD3 surface is
///     one <see cref="ArrayMesh"/> surface with its material resolved through the skin remap.</item>
///   <item>Exposes <see cref="Md3Morph.SetFrame"/> / <see cref="Md3Morph.LerpFrames"/> to rebuild the mesh
///     by linearly interpolating vertex positions/normals between two frames (DarkPlaces R_AliasLerpVerts),
///     and a small clip table (from <c>.framegroups</c>) for range playback.</item>
///   <item>Mounts each MD3 <see cref="Md3Tag"/> as a <see cref="Marker3D"/> attachment socket
///     (<c>tag_weapon</c>, <c>tag_shot</c>, …), honouring <see cref="SkinFile.TagAliases"/> /
///     <see cref="SkinFile.TagOrder"/>, and re-poses them as frames change. <see cref="Md3Morph.GetTag"/>
///     returns a tag's <see cref="Transform3D"/> for any frame.</item>
/// </list>
///
/// All positions/normals/axes convert Quake (Z-up) -> Godot (Y-up) at the boundary via <see cref="Coords"/>.
/// </summary>
public static class Md3Builder
{
    /// <summary>
    /// Build a morphing, tagged MD3 model node.
    /// </summary>
    /// <param name="md3">Parsed MD3 data.</param>
    /// <param name="assets">Material facade; surface materials resolve through <see cref="AssetSystem.ResolveMaterial"/>.</param>
    /// <param name="skin">
    /// Optional <c>.skin</c> sidecar. <see cref="SkinFile.MeshToTexture"/> overrides per-surface materials
    /// (and <c>nodraw</c> hides surfaces); <see cref="SkinFile.TagOrder"/> renames tag sockets positionally.
    /// </param>
    /// <param name="framegroups">Optional clip ranges from a <c>.framegroups</c> sidecar (else one full-range clip).</param>
    /// <returns>An <see cref="Md3Morph"/> (a <see cref="Node3D"/>) ready to add to the scene.</returns>
    public static Node3D Build(
        Md3Data md3,
        AssetSystem assets,
        SkinFile? skin = null,
        IReadOnlyList<FrameGroup>? framegroups = null)
    {
        ArgumentNullException.ThrowIfNull(md3);
        var morph = new Md3Morph();
        morph.Initialize(md3, assets, skin, framegroups);
        return morph;
    }
}

/// <summary>
/// The runtime node returned by <see cref="Md3Builder.Build"/>. It owns the parsed <see cref="Md3Data"/>,
/// a single <see cref="MeshInstance3D"/> it rebuilds when the displayed frame changes, and the tag sockets.
/// It is buildable standalone (no dependency on the entity-driven client animator); callers either drive it
/// with <see cref="SetFrame"/>/<see cref="LerpFrames"/>/<see cref="Play"/> or query tags via
/// <see cref="GetTag"/>.
/// </summary>
public partial class Md3Morph : Node3D
{
    /// <summary>A named animation = a contiguous MD3 frame range played at a rate, optionally looped.</summary>
    public readonly record struct Clip(string Name, int FirstFrame, int FrameCount, float Fps, bool Loop)
    {
        public int LastFrame => FirstFrame + Math.Max(1, FrameCount) - 1;
    }

    private Md3Data _md3 = null!;
    private AssetSystem? _assets;
    private MeshInstance3D _mesh = null!;
    private Node3D _tagsRoot = null!;

    // Resolved per-surface materials (null = hidden via skin nodraw, surface skipped).
    // autospriteAxial: null = ordinary surface; false/true = the shader carries deformVertexes
    // autosprite/autosprite2 and the material is the baked-CUSTOM0/1 deform ShaderMaterial — ApplyFrame
    // must emit the per-quad frames (AutospriteQuads) alongside the geometry.
    private readonly List<(Md3Surface surface, Material? material, bool visible, bool? autospriteAxial)> _surfaces = new();

    // Tag sockets: model tag + its display name (alias-applied) + the live marker node.
    private readonly List<(Md3Tag tag, string displayName, Marker3D marker)> _tags = new();
    // displayName/originalName -> tag, for GetTag lookups by either spelling.
    private readonly Dictionary<string, Md3Tag> _tagByName = new(StringComparer.Ordinal);

    private readonly Dictionary<string, Clip> _clips = new(StringComparer.OrdinalIgnoreCase);
    private Clip _current;
    private bool _hasClip;
    private float _playhead;
    private bool _playing;

    /// <summary>Global playback speed multiplier for clip playback.</summary>
    [Export] public float TimeScale { get; set; } = 1f;

    /// <summary>The mesh node this morpher rebuilds (exposed so callers can read it / override materials).</summary>
    public MeshInstance3D MeshInstance => _mesh;

    /// <summary>Root holding the tag <see cref="Marker3D"/>s (parent weapons/effects onto the matching child).</summary>
    public Node3D TagsRoot => _tagsRoot;

    /// <summary>Number of morph frames in the model.</summary>
    public int FrameCount => _md3?.FrameCount ?? 0;

    /// <summary>The currently playing clip name, or "" when idle / frame-pinned.</summary>
    public string CurrentClip => _hasClip ? _current.Name : "";

    // ===============================================================================================
    //  Construction
    // ===============================================================================================

    internal void Initialize(Md3Data md3, AssetSystem? assets, SkinFile? skin, IReadOnlyList<FrameGroup>? framegroups)
    {
        _md3 = md3 ?? throw new ArgumentNullException(nameof(md3));
        _assets = assets;
        Name = string.IsNullOrEmpty(md3.Name) ? "Md3Model" : SanitizeName(md3.Name);

        ResolveSurfaces(skin);

        _mesh = new MeshInstance3D { Name = "Mesh" };
        AddChild(_mesh);

        BuildTagSockets(skin);

        RegisterClips(framegroups);

        // Show frame 0 to start.
        ApplyFrame(0, 0, 0f);

        // Default to the first registered clip if any (idle preferred), else hold frame 0.
        if (_clips.TryGetValue("idle", out Clip idle))
            Play(idle.Name);
        else if (_clips.Count > 0)
        {
            foreach (Clip c in _clips.Values) { Play(c.Name); break; }
        }
    }

    /// <summary>
    /// Resolve each surface's material once. The shader path is <c>skin.MeshToTexture[surfaceName]</c> when
    /// present (per-mesh override / team skin), else the surface's own first shader name. A <c>nodraw</c>
    /// override marks the surface hidden (skipped from the mesh).
    ///
    /// <para>A quad-shaped surface whose shader carries <c>deformVertexes autosprite</c>/<c>autosprite2</c>
    /// (Base's bolt models — laser/elaser/hlac) takes the dedicated deform material
    /// (<see cref="AssetSystem.ResolveAutospriteMaterial"/>) and is flagged so <see cref="ApplyFrame"/>
    /// bakes the per-quad CUSTOM0/1 frames; anything else keeps the ordinary
    /// <see cref="AssetSystem.ResolveMaterial"/> path (which approximates autosprite with a billboard —
    /// still the behavior for non-quad geometry and BSP surfaces).</para>
    /// </summary>
    private void ResolveSurfaces(SkinFile? skin)
    {
        foreach (Md3Surface surface in _md3.Surfaces)
        {
            string fallback = surface.Shaders.Length > 0 ? surface.Shaders[0] : surface.Name;

            string shader = fallback;
            bool visible = true;
            if (skin is not null && skin.MeshToTexture.TryGetValue(surface.Name, out string? remap))
            {
                if (SkinFile.IsNoDraw(remap))
                    visible = false;
                else if (!string.IsNullOrEmpty(remap))
                    shader = remap;     // empty remap means "leave default", so keep the fallback
            }

            Material? material = null;
            bool? autospriteAxial = null;
            if (visible && _assets is not null)
            {
                ShaderDef? def = _assets.GetShader(shader);
                if (def != null && ShaderCompiler.AutospriteAxial(def) is bool axial
                    && surface.VertexCount > 0 && (surface.VertexCount & 3) == 0)
                {
                    ShaderMaterial? deform = _assets.ResolveAutospriteMaterial(shader);
                    if (deform != null)
                    {
                        material = deform;
                        autospriteAxial = axial;
                    }
                }
                material ??= _assets.ResolveMaterial(shader);
            }
            _surfaces.Add((surface, material, visible, autospriteAxial));
        }
    }

    /// <summary>
    /// Create one <see cref="Marker3D"/> per MD3 tag under a "Tags" root, posed at frame 0. Display names
    /// honour the <c>.skin</c> positional convention: <c>skin.TagOrder[i]</c> (if any) renames the i-th tag;
    /// a non-empty alias from <see cref="SkinFile.TagAliases"/> takes precedence over the bare token. Both
    /// the display name and the original MD3 name resolve through <see cref="GetTag"/>.
    /// </summary>
    private void BuildTagSockets(SkinFile? skin)
    {
        _tagsRoot = new Node3D { Name = "Tags" };
        AddChild(_tagsRoot);

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < _md3.Tags.Count; i++)
        {
            Md3Tag tag = _md3.Tags[i];

            string displayName = tag.Name;
            if (skin is not null && i < skin.TagOrder.Count)
            {
                string token = skin.TagOrder[i];
                // The Quake3 .skin convention: the tag_* token IS the name; an optional alias after the
                // comma overrides it when present.
                if (skin.TagAliases.TryGetValue(token, out string? alias) && !string.IsNullOrEmpty(alias))
                    displayName = alias;
                else if (!string.IsNullOrEmpty(token))
                    displayName = token;
            }
            if (string.IsNullOrEmpty(displayName))
                displayName = $"tag{i}";

            // Keep node names unique even if two tags collapse to the same display name.
            string nodeName = displayName;
            int dedup = 1;
            while (!usedNames.Add(nodeName))
                nodeName = $"{displayName}_{dedup++}";

            var marker = new Marker3D { Name = SanitizeName(nodeName) };
            _tagsRoot.AddChild(marker);
            _tags.Add((tag, displayName, marker));

            // Allow GetTag() by either the original MD3 name or the .skin display name.
            _tagByName[tag.Name] = tag;
            _tagByName[displayName] = tag;
        }
    }

    /// <summary>Register clips from framegroups, or a single full-range clip when none are supplied.</summary>
    private void RegisterClips(IReadOnlyList<FrameGroup>? framegroups)
    {
        if (framegroups is { Count: > 0 })
        {
            int idx = 0;
            foreach (FrameGroup g in framegroups)
            {
                string name = string.IsNullOrEmpty(g.Name) ? $"anim_{idx}" : g.Name;
                AddClip(name, g.FirstFrame, g.FrameCount, g.Fps, g.Loop);
                idx++;
            }
        }
        else if (_md3.FrameCount > 0)
        {
            AddClip("all", 0, _md3.FrameCount, 20f, true);
        }
    }

    // ===============================================================================================
    //  Clip table + playback
    // ===============================================================================================

    /// <summary>Register (or replace) a named clip; frame indices are clamped to the model's range.</summary>
    public void AddClip(string name, int firstFrame, int frameCount, float fps = 10f, bool loop = true)
    {
        int max = Math.Max(1, _md3.FrameCount);
        firstFrame = Math.Clamp(firstFrame, 0, max - 1);
        frameCount = Math.Clamp(frameCount, 1, max - firstFrame);
        // De-dup names so two unnamed groups don't clobber one another.
        string key = name;
        int suffix = 1;
        while (_clips.ContainsKey(key) && !string.Equals(_clips[key].Name, name, StringComparison.Ordinal))
            key = $"{name}_{suffix++}";
        _clips[key] = new Clip(key, firstFrame, frameCount, fps <= 0f ? 10f : fps, loop);
    }

    /// <summary>Start playing a registered clip from its first frame. No-op (returns false) if unknown.</summary>
    public bool Play(string clipName)
    {
        if (!_clips.TryGetValue(clipName, out Clip clip))
            return false;
        _current = clip;
        _hasClip = true;
        _playhead = 0f;
        _playing = true;
        ApplyFrame(clip.FirstFrame, clip.FirstFrame, 0f);
        return true;
    }

    /// <summary>Switch clips only when a different one is requested (avoids restarting the same loop).</summary>
    public bool PlayIfChanged(string clipName)
    {
        if (_hasClip && string.Equals(_current.Name, clipName, StringComparison.OrdinalIgnoreCase))
            return true;
        return Play(clipName);
    }

    public void Pause() => _playing = false;
    public void Resume() => _playing = true;

    /// <summary>
    /// Pin a single raw MD3 frame (QC <c>self.frame = N</c>), rebuilding the mesh and tags, with no playback.
    /// </summary>
    public void SetFrame(int frame)
    {
        _hasClip = false;
        _playing = false;
        int f = Math.Clamp(frame, 0, Math.Max(0, _md3.FrameCount - 1));
        ApplyFrame(f, f, 0f);
    }

    /// <summary>
    /// Explicitly display a morph between two raw frames at weight <paramref name="t"/> (0 = A, 1 = B),
    /// rebuilding the mesh and re-posing tags. Stops clip playback. This is the standalone morph-lerp API.
    /// </summary>
    public void LerpFrames(int frameA, int frameB, float t)
    {
        _hasClip = false;
        _playing = false;
        int n = Math.Max(0, _md3.FrameCount - 1);
        ApplyFrame(Math.Clamp(frameA, 0, n), Math.Clamp(frameB, 0, n), Math.Clamp(t, 0f, 1f));
    }

    public override void _Process(double delta) => Advance((float)delta);

    /// <summary>Advance the current clip's playhead and rebuild the morph mesh (call once per frame).</summary>
    public void Advance(float delta)
    {
        if (!_playing || !_hasClip || _md3.FrameCount <= 1)
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

        int local0 = (int)MathF.Floor(_playhead);
        int local1 = _current.Loop ? (local0 + 1) % _current.FrameCount : Math.Min(local0 + 1, _current.FrameCount - 1);
        float frac = _playhead - MathF.Floor(_playhead);

        int frameA = _current.FirstFrame + Math.Clamp(local0, 0, _current.FrameCount - 1);
        int frameB = _current.FirstFrame + Math.Clamp(local1, 0, _current.FrameCount - 1);
        ApplyFrame(frameA, frameB, frac);
    }

    // ===============================================================================================
    //  Tag queries
    // ===============================================================================================

    /// <summary>
    /// The attachment transform of a tag for a given frame, in Godot space. Accepts either the original MD3
    /// tag name or its <c>.skin</c> display/alias name. Returns <see cref="Transform3D.Identity"/> for an
    /// unknown tag. This is the load-bearing weapon/effect attach query (<c>gettaginfo</c>).
    /// </summary>
    public Transform3D GetTag(string name, int frame)
    {
        if (string.IsNullOrEmpty(name) || !_tagByName.TryGetValue(name, out Md3Tag? tag) || tag is null)
            return Transform3D.Identity;
        if (tag.Transforms.Length == 0)
            return Transform3D.Identity;
        int f = Math.Clamp(frame, 0, tag.Transforms.Length - 1);
        return TagTransform(tag.Transforms[f]);
    }

    /// <summary>The live <see cref="Marker3D"/> socket for a tag (by original or display name), or null.</summary>
    public Marker3D? GetTagMarker(string name)
    {
        foreach ((Md3Tag tag, string displayName, Marker3D marker) in _tags)
            if (string.Equals(tag.Name, name, StringComparison.Ordinal) ||
                string.Equals(displayName, name, StringComparison.Ordinal))
                return marker;
        return null;
    }

    private static Transform3D TagTransform(Md3TagTransform xf)
    {
        var basis = new Basis(
            Coords.ToGodot(xf.AxisX),
            Coords.ToGodot(xf.AxisY),
            Coords.ToGodot(xf.AxisZ));
        return new Transform3D(basis, Coords.ToGodot(xf.Origin));
    }

    // ===============================================================================================
    //  The morph itself
    // ===============================================================================================

    /// <summary>
    /// Rebuild the mesh interpolating each vertex between <paramref name="frameA"/> and
    /// <paramref name="frameB"/> by <paramref name="t"/>, attach resolved materials, and re-pose tags. When
    /// A == B (and t == 0) this is a plain single-frame snapshot.
    /// </summary>
    private void ApplyFrame(int frameA, int frameB, float t)
    {
        var mesh = new ArrayMesh();
        int surfaceIndex = 0;

        foreach ((Md3Surface surface, Material? material, bool visible, bool? autospriteAxial) in _surfaces)
        {
            if (!visible)
                continue;
            int vcount = surface.VertexCount;
            if (vcount <= 0 || surface.Triangles.Length == 0 || surface.FrameVertices.Length == 0)
                continue;

            int fa = Math.Clamp(frameA, 0, surface.FrameVertices.Length - 1);
            int fb = Math.Clamp(frameB, 0, surface.FrameVertices.Length - 1);
            Md3Vertex[] va = surface.FrameVertices[fa];
            Md3Vertex[] vb = surface.FrameVertices[fb];
            if (va.Length < vcount || vb.Length < vcount)
                continue;

            var positions = new Vector3[vcount];
            var normals = new Vector3[vcount];
            var uvs = new Vector2[vcount];
            // Autosprite surfaces bake their quad frames from the QUAKE-space morphed vertices
            // (AutospriteQuads is Godot-free; the s/t scalars are rotation-invariant, so only the
            // per-quad center/axis convert at the boundary below).
            System.Numerics.Vector3[]? qpos = autospriteAxial != null ? new System.Numerics.Vector3[vcount] : null;
            bool morph = fa != fb && t > 0f;
            for (int v = 0; v < vcount; v++)
            {
                System.Numerics.Vector3 p, nrm;
                if (morph)
                {
                    p = System.Numerics.Vector3.Lerp(va[v].Position, vb[v].Position, t);
                    nrm = System.Numerics.Vector3.Lerp(va[v].Normal, vb[v].Normal, t);
                }
                else
                {
                    p = va[v].Position;
                    nrm = va[v].Normal;
                }
                if (qpos != null)
                    qpos[v] = p;
                positions[v] = Coords.ToGodot(p);
                Vector3 gn = Coords.ToGodot(nrm);
                normals[v] = gn.LengthSquared() > 1e-8f ? gn.Normalized() : Vector3.Up;
                uvs[v] = v < surface.TexCoords.Length
                    ? new Vector2(surface.TexCoords[v].X, surface.TexCoords[v].Y)
                    : Vector2.Zero;
            }

            var indices = new int[surface.Triangles.Length];
            for (int i = 0; i < surface.Triangles.Length; i++)
            {
                int idx = surface.Triangles[i];
                indices[i] = (idx >= 0 && idx < vcount) ? idx : 0;
            }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = positions;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            arrays[(int)Mesh.ArrayType.Index] = indices;

            Mesh.ArrayFormat flags = 0;
            if (qpos != null && BakeAutospriteCustoms(surface, qpos, autospriteAxial!.Value)
                    is (float[] c0, float[] c1))
            {
                // CUSTOM0 = (quad center, s), CUSTOM1 = (axis, t) — RGBA float, read by the deform
                // vertex shader (AutospriteShaderGen). The flags word declares the custom formats.
                arrays[(int)Mesh.ArrayType.Custom0] = c0;
                arrays[(int)Mesh.ArrayType.Custom1] = c1;
                flags = (Mesh.ArrayFormat)(
                    ((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift) |
                    ((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom1Shift));
            }
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, flags);

            if (material is not null)
                mesh.SurfaceSetMaterial(surfaceIndex, material);
            surfaceIndex++;
        }

        _mesh.Mesh = mesh;
        PoseTags(frameA, frameB, t);
    }

    /// <summary>
    /// Bake the CUSTOM0/1 vertex-attribute arrays for an autosprite surface: per vertex,
    /// <c>(center.xyz, s)</c> and <c>(axis.xyz, t)</c> with center/axis converted to Godot space. Returns
    /// null when the surface isn't quad-shaped after all (the caller then emits plain geometry and the
    /// deform material's CUSTOM reads are zero — the quad simply collapses to its center rather than
    /// rendering garbage; in practice ResolveSurfaces already guarded vcount % 4).
    /// </summary>
    private static (float[], float[])? BakeAutospriteCustoms(
        Md3Surface surface, System.Numerics.Vector3[] qpos, bool axial)
    {
        int vcount = qpos.Length;
        var s = new float[vcount];
        var t = new float[vcount];
        var quads = new AutospriteQuads.Quad[vcount / 4];
        if (!AutospriteQuads.Bake(qpos, surface.TexCoords, axial, s, t, quads))
            return null;

        var c0 = new float[vcount * 4];
        var c1 = new float[vcount * 4];
        for (int v = 0; v < vcount; v++)
        {
            AutospriteQuads.Quad quad = quads[v / 4];
            Vector3 center = Coords.ToGodot(quad.Center);
            Vector3 axis = Coords.ToGodot(quad.Axis);
            int d = v * 4;
            c0[d + 0] = center.X; c0[d + 1] = center.Y; c0[d + 2] = center.Z; c0[d + 3] = s[v];
            c1[d + 0] = axis.X;   c1[d + 1] = axis.Y;   c1[d + 2] = axis.Z;   c1[d + 3] = t[v];
        }
        return (c0, c1);
    }

    /// <summary>Re-pose each tag marker by interpolating its transform between the two frames.</summary>
    private void PoseTags(int frameA, int frameB, float t)
    {
        foreach ((Md3Tag tag, string _, Marker3D marker) in _tags)
        {
            if (tag.Transforms.Length == 0)
                continue;
            int fa = Math.Clamp(frameA, 0, tag.Transforms.Length - 1);
            int fb = Math.Clamp(frameB, 0, tag.Transforms.Length - 1);
            if (fa == fb || t <= 0f)
            {
                marker.Transform = TagTransform(tag.Transforms[fa]);
                continue;
            }
            Md3TagTransform a = tag.Transforms[fa];
            Md3TagTransform b = tag.Transforms[fb];
            Vector3 origin = Coords.ToGodot(System.Numerics.Vector3.Lerp(a.Origin, b.Origin, t));
            var basis = new Basis(
                Coords.ToGodot(System.Numerics.Vector3.Lerp(a.AxisX, b.AxisX, t)),
                Coords.ToGodot(System.Numerics.Vector3.Lerp(a.AxisY, b.AxisY, t)),
                Coords.ToGodot(System.Numerics.Vector3.Lerp(a.AxisZ, b.AxisZ, t))).Orthonormalized();
            marker.Transform = new Transform3D(basis, origin);
        }
    }

    /// <summary>Strip characters Godot disallows in node names (':' '/' '@' '%' and dots).</summary>
    private static string SanitizeName(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "Node";
        Span<char> buf = stackalloc char[raw.Length];
        int n = 0;
        foreach (char c in raw)
            buf[n++] = c is ':' or '/' or '@' or '%' or '.' ? '_' : c;
        return new string(buf[..n]);
    }
}
