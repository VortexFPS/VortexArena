using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Diagnostics;
using VortexArena.Formats.Iqm;
using VortexArena.Formats.Sidecars;
using SN = System.Numerics;

namespace VortexArena.Game.Loaders.Models;

/// <summary>
/// Turns a parsed <see cref="IqmData"/> (the engine-neutral IQM importer output) into a live Godot scene:
/// a <see cref="Skeleton3D"/>, a skinned <see cref="MeshInstance3D"/> bound to it, and an
/// <see cref="AnimationPlayer"/> holding every clip in an <see cref="AnimationLibrary"/>. IQM is the format
/// for all of Xonotic's animated content (players, monsters, vehicles, view weapons): one shared vertex
/// buffer skinned to a bone hierarchy, with animation stored as per-frame bone-LOCAL transforms.
///
/// === Coordinate convention (the crux) ===
/// The sim/asset layer is Quake space (X fwd, Y left, Z up); Godot is Y-up (X right, Y up, Z back). The
/// boundary map is <see cref="Coords.ToGodot"/> = (x, z, -y), which as a 3x3 is the proper rotation
///   M = [[1,0,0],[0,0,1],[0,-1,0]]   (a -90 deg rotation about the world X axis, det = +1).
///
/// A bone transform takes points from bone-local space to MODEL/world space, so it is a transform expressed
/// IN model space — to re-express it in Godot's model space we conjugate by M (a similarity transform):
///   W_godot = M * W_quake * M^-1.
/// Conjugation is a homomorphism, so it commutes with the parent chain:
///   W_godot[i] = M (W_q[parent] * L_q[i]) M^-1 = (M W_q[parent] M^-1)(M L_q[i] M^-1) = W_godot[parent] * conj(L_q[i]).
/// We therefore compute each bone's *world* Quake rest, conjugate it to Godot, then localize
///   L_godot[i] = W_godot[parent]^-1 * W_godot[i]
/// and feed that as the bone REST. This is provably consistent with the per-vertex map (which is just the
/// translation half of the same M), so the rest pose is undistorted. NOTE: a bone-LOCAL transform cannot be
/// conjugated on its own and re-parented — M does not commute with the parent's translation — which is why
/// both the rest pose AND every animation frame are converted through world space here, never per-local.
///
/// Quaternions: <see cref="SN.Quaternion"/> (x,y,z,w) maps component-for-component to
/// <see cref="Godot.Quaternion"/> (x,y,z,w); the basis change is then applied via the matrix conjugation
/// above (built once as <c>Mxf</c>/<c>MxfInv</c>), not by hand-deriving a rotated quaternion.
///
/// === Bind pose / skin ===
/// Bone rests carry the full Godot-space world pose (localized), so the canonical Godot bind pose
/// (rest_global^-1 per bone) is exactly what the skinned vertices expect. We hand skinning entirely to
/// <see cref="Skeleton3D.CreateSkinFromRestTransforms"/>, which builds that Skin from the rests we set — no
/// hand-rolled bind-pose inverse that could fight the convention.
///
/// === Animations / framegroups ===
/// IQM ships named clips (<see cref="IqmData.Anims"/>); a sibling <c>.iqm.framegroups</c> sidecar may instead
/// define/override the ranges (see <see cref="FrameGroup"/>). When framegroups are supplied they win, named
/// by their 5th token or by a recognised label (idle/run/jump/death/...) derived per index. Each clip becomes
/// one <see cref="Animation"/> with a position+rotation+scale 3D track per bone, keyed across its frame span at
/// 1/fps, looped per the flag, registered into one <see cref="AnimationLibrary"/> on the
/// <see cref="AnimationPlayer"/>.
/// </summary>
public static class IqmBuilder
{
    /// <summary>Name of the single <see cref="AnimationLibrary"/> registered on the player. The default
    /// (empty) library lets callers address clips by bare name — <c>player.Play("idle")</c> — matching the MD3
    /// and DPM builders so the client drives any model format identically.</summary>
    public const string LibraryName = "";

    // The Quake->Godot basis change M (a proper rotation, det +1) and its inverse, as Godot Transform3Ds with
    // zero translation. M is sourced directly from Coords.ToGodot so it can never drift from the project's one
    // source of truth: Godot's Basis(x,y,z) ctor takes the COLUMN vectors, and column j is M*e_j = the image of
    // Quake axis j. (ToGodot(1,0,0)=(1,0,0); ToGodot(0,1,0)=(0,0,-1); ToGodot(0,0,1)=(0,1,0).)
    private static readonly Transform3D Mxf;
    private static readonly Transform3D MxfInv;

    static IqmBuilder()
    {
        var m = new Basis(
            Coords.ToGodot(new SN.Vector3(1f, 0f, 0f)),  // column 0 = image of Quake +X
            Coords.ToGodot(new SN.Vector3(0f, 1f, 0f)),  // column 1 = image of Quake +Y
            Coords.ToGodot(new SN.Vector3(0f, 0f, 1f))); // column 2 = image of Quake +Z
        Mxf = new Transform3D(m, Vector3.Zero);
        MxfInv = Mxf.AffineInverse(); // pure rotation => transpose, but AffineInverse is exact and clear.
    }

    /// <summary>
    /// Conjugate a bone's WORLD transform from Quake model space into Godot model space
    /// (<c>M · W · M⁻¹</c>, the similarity transform from the class summary). The runtime skeletal poser
    /// (<c>PlayerSkeleton</c>/CPU bones) computes bone matrices in Quake world space; pushing them onto the
    /// <see cref="Skeleton3D"/> built here must go through this exact conjugation so the posed mesh stays
    /// consistent with the world-conjugated rests + skin.
    /// </summary>
    public static Transform3D ConjugateQuakeWorldToGodot(in Transform3D quakeWorld) => Mxf * quakeWorld * MxfInv;

    // =================================================================================================
    //  Public entry point
    // =================================================================================================

    /// <summary>The fixed name of the skeleton node a built model carries — also the animation track-path
    /// prefix, which is why <see cref="BuildAnimationLibrary"/> can build clips without the live skeleton.</summary>
    public const string SkeletonNodeName = "Skeleton3D";

    /// <summary>
    /// The render geometry every instance of one (model, skin) shares: the skinned <see cref="ArrayMesh"/> and
    /// the rest-pose <see cref="Skin"/>. Both are pure functions of the parsed IQM + skin remap, so N wearers
    /// of one model need ONE of each — the DarkPlaces model_t contract (Mod_ForName caches; entities reference).
    /// <see cref="Build"/> fills an empty instance and reuses a populated one; <c>AssetLoader</c> owns the cache
    /// and its lifetime. NOTE (2026-08-02, measured): on today's 6-bot benchmark this is perf-NEUTRAL — the load
    /// window builds ~40-70 DISTINCT models once each, so sharing pays nothing there (see the perf-deep-dive
    /// doc §2). It is kept for architectural correctness and the N-wearer cases the bench doesn't exercise:
    /// forcemodels, large player counts, corpse copies, mid-match joins, and VRAM at scale.
    ///
    /// LIFETIME CONTRACT (see PlayerModel._ExitTree / ViewEntityRenderer.DisposeOwnedMeshes): once shared, a
    /// mesh/skin is CACHE-owned and must NOT be disposed when any single wearer leaves the tree.
    /// <see cref="IsShared"/> is the check the per-model teardowns use to skip exactly these resources — the
    /// same exclusion the surface materials and the AnimationLibrary already rely on.
    /// </summary>
    public sealed class SharedSkinnedGeometry
    {
        public ArrayMesh? Mesh;
        public Skin? Skin;
    }

    // Instance ids of every resource handed out through a SharedSkinnedGeometry. A HashSet of ids (not the
    // objects) so this registry never itself keeps a freed resource alive, and so the check is O(1) on the
    // teardown path. Written on the main thread during a build, read on the main thread during teardown.
    private static readonly HashSet<ulong> _sharedResourceIds = new();

    /// <summary>Register a resource as cache-owned + shared across model instances (see
    /// <see cref="SharedSkinnedGeometry"/>).</summary>
    public static void MarkShared(Resource? r)
    {
        if (r is not null)
            _sharedResourceIds.Add(r.GetInstanceId());
    }

    /// <summary>True when this resource is shared across instances, so a single instance's teardown must not
    /// dispose it. Cheap enough for the per-mesh teardown walk. Safe on an already-disposed wrapper (answers
    /// false) — teardown paths run during shutdown and must never throw.</summary>
    public static bool IsShared(Resource? r)
        => r is not null && GodotObject.IsInstanceValid(r) && _sharedResourceIds.Contains(r.GetInstanceId());

    /// <summary>
    /// Build a posed, skinned, animated model from <paramref name="iqm"/>. Returns a root <see cref="Node3D"/>
    /// whose children are the <see cref="Skeleton3D"/> (carrying the <see cref="MeshInstance3D"/>) and an
    /// <see cref="AnimationPlayer"/>. Materials come from <c>assets.ResolveMaterial(mesh.Material)</c>; if
    /// <paramref name="framegroups"/> is non-empty it defines the animation clips (overriding the IQM's own).
    /// (§12.3-1) <paramref name="prebuiltAnims"/>/<paramref name="prebuiltDefaultClip"/>: a clip library the
    /// off-thread parse phase already built via <see cref="BuildAnimationLibrary"/> (the ~130 ms of track-key
    /// work) — when supplied it is attached verbatim and the inline build is skipped.
    /// </summary>
    public static Node3D Build(IqmData iqm, AssetSystem assets, IReadOnlyList<FrameGroup>? framegroups = null,
        SkinFile? skin = null, AnimationLibrary? prebuiltAnims = null, string? prebuiltDefaultClip = null,
        SharedSkinnedGeometry? shared = null)
    {
        ArgumentNullException.ThrowIfNull(iqm);

        var root = new Node3D { Name = "IqmModel" };

        // ---- 1. Skeleton (bones + Godot-space rests) -------------------------------------------------
        Skeleton3D skeleton;
        using (Prof.Sample("iqm.skeleton"))
        {
            skeleton = BuildSkeleton(iqm);
            root.AddChild(skeleton);
        }

        // ---- 2. Skinned mesh -------------------------------------------------------------------------
        // Geometry is a pure function of (IqmData, SkinFile): `shared` lets the caller cache one ArrayMesh +
        // Skin per (model, skin) and hand it to every instance (the DP model_t contract). Per-player appearance
        // never touches these — colormod/glowmod/shirt/pants ride SetInstanceShaderParameter and alpha rides
        // GeometryInstance3D.Transparency, both per-INSTANCE. A cached entry whose resource was disposed anyway
        // (an unguarded teardown path) degrades to a rebuild, never a dead handle: the failure mode of a stale
        // cache must be a slow frame, not an ObjectDisposedException mid-_Process.
        var meshInstance = new MeshInstance3D { Name = "Mesh" };
        if (shared?.Mesh is { } cachedMesh && GodotObject.IsInstanceValid(cachedMesh))
        {
            meshInstance.Mesh = cachedMesh;
        }
        else
        {
            using (Prof.Sample("iqm.mesh"))
                meshInstance.Mesh = BuildMesh(iqm, assets, skin);
            if (shared is not null)
            {
                shared.Mesh = (ArrayMesh)meshInstance.Mesh;
                MarkShared(shared.Mesh);
            }
        }

        // Skin: bind poses = rest_global^-1 for each bone, generated from the rests we just set. This is the
        // canonical Godot binding and matches our world-conjugated rests exactly (no manual inverse). Identical
        // across instances (the rests are), so it caches alongside the mesh; Godot makes a per-MeshInstance
        // SkinReference internally, so sharing the resource keeps each wearer's pose independent.
        if (HasSkinning(iqm))
        {
            meshInstance.Skin = shared?.Skin is { } cachedSkin && GodotObject.IsInstanceValid(cachedSkin)
                ? cachedSkin
                : skeleton.CreateSkinFromRestTransforms();
            if (shared is not null && !ReferenceEquals(shared.Skin, meshInstance.Skin))
            {
                shared.Skin = meshInstance.Skin;
                MarkShared(shared.Skin);
            }
        }
        // The MeshInstance must live under the Skeleton3D so its Skeleton NodePath resolves to it (here "..").
        skeleton.AddChild(meshInstance);
        meshInstance.Skeleton = meshInstance.GetPathTo(skeleton);

        // ---- 3. Animations ---------------------------------------------------------------------------
        var player = new AnimationPlayer { Name = "AnimationPlayer" };
        root.AddChild(player);
        // Bone-track paths are "Skeleton3D:bone" — relative to the MODEL ROOT (Skeleton3D is a child of it),
        // not the skeleton itself. RootNode must therefore be the model root (= the player's parent).
        player.RootNode = player.GetPathTo(root);

        AnimationLibrary library;
        string? defaultClip;
        if (prebuiltAnims is not null)
        {
            library = prebuiltAnims;
            defaultClip = prebuiltDefaultClip;
        }
        else
        {
            using (Prof.Sample("iqm.anims"))
                library = BuildAnimationLibrary(iqm, framegroups, out defaultClip);
        }
        player.AddAnimationLibrary(LibraryName, library);

        // Auto-play a sensible default (idle if present, else the first clip) so a freshly built model moves.
        // With the default (empty) library the play name is just the clip name (no "lib/" prefix).
        if (defaultClip is not null)
        {
            player.Autoplay = string.IsNullOrEmpty(LibraryName) ? defaultClip : $"{LibraryName}/{defaultClip}";
        }

        return root;
    }

    // =================================================================================================
    //  1. Skeleton
    // =================================================================================================

    /// <summary>
    /// Create one bone per <see cref="IqmJoint"/> and set its REST to the joint's local TRS converted into
    /// Godot space via the world-conjugation described in the class summary. Bone GLOBAL rests are then
    /// recoverable from the skeleton itself (<see cref="Skeleton3D.GetBoneGlobalRest"/>), which the attachment
    /// helper and the skin both rely on, so this returns just the populated skeleton.
    /// </summary>
    private static Skeleton3D BuildSkeleton(IqmData iqm)
    {
        var skeleton = new Skeleton3D { Name = SkeletonNodeName };
        IqmJoint[] joints = iqm.Joints;
        int n = joints.Length;
        // Each bone's Godot-space WORLD rest, kept locally so children can localize against their parent.
        var worldGodotRest = new Transform3D[n];

        // First pass: add bones + parent links. IQM guarantees parent < child, so a single forward pass works.
        // Bone names are sanitised because they double as animation track sub-paths ("Skeleton3D:bone"), where
        // ':' '/' '%' '@' would break the NodePath. Attachment lookups sanitise their query the same way.
        // BoneNames is the single source of the naming so the off-thread animation build can never drift.
        string[] names = BoneNames(iqm);
        for (int i = 0; i < n; i++)
            skeleton.AddBone(names[i]);
        for (int i = 0; i < n; i++)
        {
            int parent = joints[i].Parent;
            if (parent >= 0 && parent < n)
                skeleton.SetBoneParent(i, parent);
        }

        // Second pass: world Quake rest -> conjugate to Godot world -> localize -> SetBoneRest.
        var worldQuake = new Transform3D[n];
        for (int i = 0; i < n; i++)
        {
            Transform3D localQuake = QuakeTrsToTransform(joints[i].Translate, joints[i].Rotate, joints[i].Scale);
            int parent = joints[i].Parent;
            worldQuake[i] = (parent >= 0 && parent < n) ? worldQuake[parent] * localQuake : localQuake;

            // Conjugate the WORLD transform into Godot space (similarity transform M * W * M^-1).
            Transform3D worldGodot = Mxf * worldQuake[i] * MxfInv;
            worldGodotRest[i] = worldGodot;

            // Localize against the (already-Godot) parent world so the rest is bone-local, as Godot expects.
            Transform3D localGodot = (parent >= 0 && parent < n)
                ? worldGodotRest[parent].AffineInverse() * worldGodot
                : worldGodot;

            skeleton.SetBoneRest(i, localGodot);
            // Pose to rest initially so the bound mesh shows the rest pose before any animation plays.
            skeleton.SetBonePosePosition(i, localGodot.Origin);
            skeleton.SetBonePoseRotation(i, localGodot.Basis.GetRotationQuaternion());
            skeleton.SetBonePoseScale(i, localGodot.Basis.Scale);
        }

        return skeleton;
    }

    // =================================================================================================
    //  2. Skinned mesh
    // =================================================================================================

    /// <summary>
    /// Build one <see cref="ArrayMesh"/> surface per <see cref="IqmMesh"/>, slicing the shared vertex/triangle
    /// buffers. Positions/normals go through <see cref="Coords.ToGodot"/>; BlendIndexes fill the Bones array as
    /// int4 (skeleton-global joint indices — NOT re-based), BlendWeights fill the Weights array normalized to
    /// sum 1; the surface material is resolved from the mesh's shader name. Note only the triangle *vertex*
    /// indices are re-based into the surface-local window, since each Godot surface indexes its own Vertex array.
    /// </summary>
    // (hitch-fix §2, 2026-06-15) Exact-size scratch reuse for the per-surface mesh arrays. ArrayMesh.AddSurfaceFromArrays
    // copies the managed arrays into Godot's mesh buffers SYNCHRONOUSLY at the Godot.Collections.Array assignment (it
    // marshals to a Packed* Variant), so the scratch can be reused on the next surface/build — but ONLY at the EXACT
    // length (an oversized buffer, e.g. from ArrayPool, would marshal garbage tail vertices = corrupted mesh; that is
    // why this keys by length and never hands Godot a too-long array). One pool PER SLOT because all six arrays are
    // alive simultaneously until AddSurfaceFromArrays. Reuse hits across instances of the same model (identical surface
    // sizes) — the bot-spawn wave. [ThreadStatic]: BuildMesh runs on the main thread; bounded by distinct surface sizes.
    [ThreadStatic] private static Dictionary<int, Vector3[]>? _posPool;
    [ThreadStatic] private static Dictionary<int, Vector3[]>? _normPool;
    [ThreadStatic] private static Dictionary<int, Vector2[]>? _uvPool;
    [ThreadStatic] private static Dictionary<int, int[]>? _bonePool;
    [ThreadStatic] private static Dictionary<int, float[]>? _weightPool;
    [ThreadStatic] private static Dictionary<int, int[]>? _idxPool;

    private static T[] RentExact<T>(ref Dictionary<int, T[]>? pool, int len)
    {
        pool ??= new Dictionary<int, T[]>();
        if (!pool.TryGetValue(len, out T[]? buf))
        {
            buf = new T[len];
            pool[len] = buf;
        }
        return buf;
    }

    private static ArrayMesh BuildMesh(IqmData iqm, AssetSystem assets, SkinFile? skin)
    {
        var mesh = new ArrayMesh { ResourceName = "IqmMesh" };
        bool skinned = HasSkinning(iqm);
        int totalVerts = iqm.Positions.Length;

        int surfaceIndex = 0;
        foreach (IqmMesh sub in iqm.Meshes)
        {
            int vcount = sub.VertexCount;
            int first = sub.FirstVertex;
            if (vcount <= 0 || sub.TriangleCount <= 0)
                continue;
            if (first < 0 || first + vcount > totalVerts)
                continue;

            // _N.skin per-mesh material override (DP skin remap), keyed by the IQM mesh NAME. A mesh mapped to
            // common/nodraw renders nothing; otherwise the remap replaces the baked shader name. Mirrors how
            // Md3Builder applies its SkinFile, so every model format honors the same sidecar (RC5: IQM ignored it).
            // EffectiveMaterialName is shared with the off-thread predecode pass, so the two can't disagree.
            string? materialName = EffectiveMaterialName(sub, skin, out bool remapped);
            if (materialName is null)
                continue; // skin hides this mesh

            var positions = RentExact(ref _posPool, vcount);
            var normals = RentExact(ref _normPool, vcount);
            var uvs = RentExact(ref _uvPool, vcount);
            // Godot wants 4 bone indices + 4 weights per vertex when skinned.
            int[]? bones = skinned ? RentExact(ref _bonePool, vcount * 4) : null;
            float[]? weights = skinned ? RentExact(ref _weightPool, vcount * 4) : null;

            bool haveNormals = iqm.Normals is { Length: > 0 };
            for (int v = 0; v < vcount; v++)
            {
                int g = first + v; // global vertex index into the shared arrays.
                positions[v] = Coords.ToGodot(iqm.Positions[g]);

                if (haveNormals && g < iqm.Normals!.Length)
                {
                    Vector3 gn = Coords.ToGodot(iqm.Normals[g]);
                    normals[v] = gn.LengthSquared() > 1e-12f ? gn.Normalized() : Vector3.Up;
                }
                else
                {
                    normals[v] = Vector3.Up; // synthesized later by the engine if needed; placeholder for now.
                }

                uvs[v] = g < iqm.TexCoords.Length
                    ? new Vector2(iqm.TexCoords[g].X, iqm.TexCoords[g].Y)
                    : Vector2.Zero;

                if (skinned)
                {
                    FillSkinVertex(iqm, g, v, bones!, weights!);
                }
            }

            // Triangles are GLOBAL indices; re-base into this surface's [0, vcount) window so the surface is
            // self-contained (Godot surfaces index their own Vertex array).
            int triStart = sub.FirstTriangle * 3;
            int triEnd = (sub.FirstTriangle + sub.TriangleCount) * 3;
            triEnd = Math.Min(triEnd, iqm.Triangles.Length);
            var indices = RentExact(ref _idxPool, Math.Max(0, triEnd - triStart));
            int upper = first + vcount;
            for (int t = triStart, k = 0; t < triEnd; t++, k++)
            {
                int gi = iqm.Triangles[t];
                int local = gi - first;
                // Guard against a triangle that references outside this mesh's vertex window.
                indices[k] = (gi >= first && gi < upper) ? local : 0;
            }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = positions;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            if (skinned)
            {
                arrays[(int)Mesh.ArrayType.Bones] = bones;
                arrays[(int)Mesh.ArrayType.Weights] = weights;
            }
            arrays[(int)Mesh.ArrayType.Index] = indices;

            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

            // Resolve and attach the surface material from the (possibly skin-remapped) shader name. If a
            // remap resolved to the magenta fallback (a variant-only name with no shader/texture), keep the
            // baked material so a bad remap can't blank an otherwise-valid surface.
            Material? material;
            using (Prof.Sample("iqm.materials"))   // texture decode + upload live in here (skin/_norm/_gloss)
            {
                material = ResolveMaterialSafe(assets, materialName);
                if (remapped && material is not null && ReferenceEquals(material, assets.FallbackMaterial()))
                    material = ResolveMaterialSafe(assets, sub.Material);
            }
            if (material is not null)
                mesh.SurfaceSetMaterial(surfaceIndex, material);

            // Name the surface for debugging/skin remap tooling.
            mesh.SurfaceSetName(surfaceIndex, string.IsNullOrEmpty(sub.Name) ? $"surface_{surfaceIndex}" : sub.Name);
            surfaceIndex++;
        }

        return mesh;
    }

    /// <summary>
    /// Write the 4 bone indices + 4 weights for one vertex. BlendIndexes are joint indices (already global to
    /// the skeleton, so no re-basing) in <see cref="IqmData"/>'s flat <c>vertex*4</c> layout; weights are
    /// 0..255 bytes normalized to 0..1 floats. Falls back to a rigid bind to bone 0 if the file lacks
    /// per-vertex skin data for this index.
    /// </summary>
    private static void FillSkinVertex(IqmData iqm, int globalVertex, int localVertex, int[] bones, float[] weights)
    {
        int b = localVertex * 4;
        int g = globalVertex * 4;
        byte[]? bi = iqm.BlendIndexes;
        byte[]? bw = iqm.BlendWeights;

        if (bi is null || bw is null || g < 0 || g + 4 > bi.Length || g + 4 > bw.Length)
        {
            // Unskinned vertex inside a skinned model: pin fully to the root bone so it stays attached.
            bones[b] = 0; bones[b + 1] = 0; bones[b + 2] = 0; bones[b + 3] = 0;
            weights[b] = 1f; weights[b + 1] = 0f; weights[b + 2] = 0f; weights[b + 3] = 0f;
            return;
        }

        int jointCount = iqm.Joints.Length;
        float sum = 0f;
        for (int j = 0; j < 4; j++)
        {
            int idx = bi[g + j];
            // BlendIndexes index into Joints (== skeleton bones); clamp defensively against malformed data so
            // Godot's skin lookup never dereferences a bone that doesn't exist.
            if (idx >= jointCount)
                idx = 0;
            float w = bw[g + j] / 255f;
            bones[b + j] = idx;
            weights[b + j] = w;
            sum += w;
        }

        // Renormalize so the four weights sum to exactly 1 (IQM stores ~255 total but rounding leaves slack;
        // Godot does not renormalize for us). If all-zero (degenerate), pin to the first listed bone.
        if (sum > 1e-6f)
        {
            float inv = 1f / sum;
            weights[b] *= inv; weights[b + 1] *= inv; weights[b + 2] *= inv; weights[b + 3] *= inv;
        }
        else
        {
            weights[b] = 1f; weights[b + 1] = 0f; weights[b + 2] = 0f; weights[b + 3] = 0f;
        }
    }

    // =================================================================================================
    //  3. Animations
    // =================================================================================================

    /// <summary>The bone names a built skeleton will carry, derived from the IQM joints alone (the same
    /// fallback + sanitisation <see cref="BuildSkeleton"/> applies) — what lets the animation library build
    /// OFF-THREAD, before any <see cref="Skeleton3D"/> exists.</summary>
    private static string[] BoneNames(IqmData iqm)
    {
        IqmJoint[] joints = iqm.Joints;
        var names = new string[joints.Length];
        for (int i = 0; i < joints.Length; i++)
            names[i] = string.IsNullOrEmpty(joints[i].Name) ? $"bone_{i}" : SanitizeBoneName(joints[i].Name);
        return names;
    }

    /// <summary>
    /// Build one <see cref="AnimationLibrary"/> holding every clip. Each clip is one
    /// <see cref="IqmAnim"/> (or, when supplied, one <see cref="FrameGroup"/> which overrides the ranges),
    /// expressed as per-bone position/rotation/scale 3D tracks keyed across its frame span at 1/fps.
    /// (§12.3-1) Needs NO live skeleton — bone names + track paths derive from the IQM data and the fixed
    /// <see cref="SkeletonNodeName"/> — so the (~130 ms of track-key work for a player model) can run on the
    /// streamer's worker thread inside the parse phase. Building a Resource on a worker and handing it to
    /// the main thread once is the supported Godot threading pattern (one thread at a time).
    /// </summary>
    public static AnimationLibrary BuildAnimationLibrary(
        IqmData iqm, IReadOnlyList<FrameGroup>? framegroups, out string? defaultClip)
    {
        defaultClip = null;
        var library = new AnimationLibrary();
        IqmFrame[] frames = iqm.Frames;
        string[] boneNames = BoneNames(iqm);
        int boneCount = boneNames.Length;
        if (frames.Length == 0 || boneCount == 0)
            return library; // static model: no clips, the rest pose stands.

        // Build the working clip list: framegroups win when present and non-empty.
        var clips = new List<ClipSpec>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (framegroups is { Count: > 0 })
        {
            // (playtest #36) `.framegroups` lines are NAMELESS (the trailing "// fire" is a comment the parser
            // never sees), so clip names must be SYNTHESIZED — and the slot ORDER is the contract, exactly like
            // the player animdecide registry (#32). A nameless FOUR-group skeletal rig is Base's weapon-hand
            // contract (CL_WeaponEntity_SetModel, common/weapons/all.qc: anim_fire1='0…', anim_fire2='1…',
            // anim_idle='2…', anim_reload='3…' — every shipped h_*.iqm.framegroups has exactly these 4 rows), so
            // name those slots fire/fire2/idle/reload — the names ViewModel's clip lookups expect. Before this,
            // the generic player-canonical names landed on weapons as idle/run/runbackwards/strafeleft: "idle"
            // WAS the fire clip (guns looped their fire animation at rest) and "fire"/"reload" didn't exist.
            bool allNameless = true;
            for (int i = 0; i < framegroups.Count && allNameless; i++)
                allNameless = string.IsNullOrEmpty(framegroups[i].Name);
            string[]? slotNames = allNameless && framegroups.Count == WeaponSlotNames.Length ? WeaponSlotNames : null;

            for (int i = 0; i < framegroups.Count; i++)
            {
                FrameGroup fg = framegroups[i];
                string baseName = !string.IsNullOrEmpty(fg.Name) ? fg.Name
                    : slotNames is not null ? slotNames[i]
                    : DefaultClipName(i);
                clips.Add(new ClipSpec(UniqueName(baseName, usedNames), fg.FirstFrame, fg.FrameCount, fg.Fps, fg.Loop));
            }
        }
        else if (iqm.Anims.Length > 0)
        {
            for (int i = 0; i < iqm.Anims.Length; i++)
            {
                IqmAnim a = iqm.Anims[i];
                string baseName = !string.IsNullOrEmpty(a.Name) ? a.Name : DefaultClipName(i);
                float fps = a.FrameRate > 0f ? a.FrameRate : 20f;
                clips.Add(new ClipSpec(UniqueName(baseName, usedNames), a.FirstFrame, a.FrameCount, fps, a.Loop));
            }
        }
        else
        {
            // No clip metadata at all: expose the whole frame stack as one looping "all" clip at 20 fps.
            clips.Add(new ClipSpec("all", 0, frames.Length, 20f, true));
        }

        // Per-MODEL scratch shared across every clip build (track paths, track-index maps, frame-world
        // buffers). These were re-allocated per clip — for a player model (~35 clips × ~60 bones) that was
        // thousands of identical strings/NodePaths/arrays inside the first-build burst on the streamer
        // worker (the iqm.anims scope of the bot-join alloc storm). Contents are per-bone, identical for
        // every clip of the model, so one set serves all clips.
        var scratch = new AnimScratch(boneNames);

        foreach (ClipSpec clip in clips)
        {
            Animation anim = BuildOneAnimation(iqm, scratch, clip);
            library.AddAnimation(clip.Name, anim);

            // Choose an autoplay default: prefer any "idle"-named clip, else fall back to the first clip.
            bool isIdle = clip.Name.Contains("idle", StringComparison.OrdinalIgnoreCase);
            if (defaultClip is null || isIdle)
                defaultClip = clip.Name;
        }

        return library;
    }

    /// <summary>Reused per-model buffers for <see cref="BuildOneAnimation"/>: one animation track path per
    /// bone (<c>"Skeleton3D:bone"</c>, pre-converted to <see cref="NodePath"/> — the conversion allocates a
    /// managed wrapper + native path, so it must not repeat per clip), the per-clip track-index maps, and
    /// the two frame-world scratch arrays. NodePath construction is pure data (no scene access) and safe on
    /// the streamer worker, where the first build of each model runs.</summary>
    private sealed class AnimScratch
    {
        public readonly NodePath[] BonePaths;
        public readonly int[] PosTrack, RotTrack, SclTrack;
        public readonly Transform3D[] WorldQuake, WorldGodot;

        public AnimScratch(string[] boneNames)
        {
            int n = boneNames.Length;
            BonePaths = new NodePath[n];
            for (int i = 0; i < n; i++)
                BonePaths[i] = new NodePath($"{SkeletonNodeName}:{boneNames[i]}");
            PosTrack = new int[n];
            RotTrack = new int[n];
            SclTrack = new int[n];
            WorldQuake = new Transform3D[n];
            WorldGodot = new Transform3D[n];
        }
    }

    /// <summary>
    /// Build a single <see cref="Animation"/> for one clip: a position+rotation+scale track per bone, keyed at
    /// each frame in the clip's span at 1/fps. Each frame's bone-LOCAL Quake TRS is converted to a Godot
    /// bone-LOCAL transform through the same world-conjugation as the rest pose (computed fresh per frame so
    /// the non-commuting parent translation is handled correctly).
    /// </summary>
    private static Animation BuildOneAnimation(IqmData iqm, AnimScratch s, ClipSpec clip)
    {
        int boneCount = s.BonePaths.Length;
        IqmFrame[] frames = iqm.Frames;
        IqmJoint[] joints = iqm.Joints;

        // Clamp the clip span into the real frame range (framegroups are not pre-clamped to this model).
        int firstFrame = Math.Clamp(clip.FirstFrame, 0, frames.Length - 1);
        int frameCount = Math.Clamp(clip.FrameCount, 1, frames.Length - firstFrame);
        float fps = clip.Fps > 0f ? clip.Fps : 20f;
        float dt = 1f / fps;

        var anim = new Animation
        {
            // Length is the time of the LAST keyed frame; the player handles loop wrap.
            Length = Math.Max(dt, (frameCount - 1) * dt),
            LoopMode = clip.Loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None,
        };

        // Create the 3 tracks per bone up front and remember their indices (in the per-model scratch).
        int[] posTrack = s.PosTrack, rotTrack = s.RotTrack, sclTrack = s.SclTrack;
        for (int bone = 0; bone < boneCount; bone++)
        {
            // Paths are relative to the AnimationPlayer.RootNode (the model root): "<skeleton>:<bone>".
            // The skeleton's node name is the fixed SkeletonNodeName, so no live node is needed here.
            NodePath basePath = s.BonePaths[bone];

            posTrack[bone] = anim.AddTrack(Animation.TrackType.Position3D);
            anim.TrackSetPath(posTrack[bone], basePath);

            rotTrack[bone] = anim.AddTrack(Animation.TrackType.Rotation3D);
            anim.TrackSetPath(rotTrack[bone], basePath);

            sclTrack[bone] = anim.AddTrack(Animation.TrackType.Scale3D);
            anim.TrackSetPath(sclTrack[bone], basePath);
        }

        // Frame-world scratch reused across every frame of every clip of this model.
        Transform3D[] worldGodot = s.WorldGodot; // each bone's Godot-space WORLD transform this frame
        Transform3D[] worldQuake = s.WorldQuake; // its Quake-space WORLD transform (pre-conjugation)

        for (int f = 0; f < frameCount; f++)
        {
            IqmFrame frame = frames[firstFrame + f];
            IqmBonePose[] poses = frame.Bones;
            float time = f * dt;

            // Pass 1: chain frame bone-local TRS through parents into Quake world, then conjugate by M into
            // Godot world. Computing the world fresh each frame is what makes the (non-commuting) parent
            // translation convert correctly — a per-bone-local conjugation would distort the pose.
            ComputeFrameWorlds(joints, poses, boneCount, worldQuake, worldGodot);

            // Pass 2: localize each bone against its Godot parent world and key the local TRS.
            for (int bone = 0; bone < boneCount; bone++)
            {
                int parent = bone < joints.Length ? joints[bone].Parent : -1;
                Transform3D localGodot = (parent >= 0 && parent < boneCount)
                    ? worldGodot[parent].AffineInverse() * worldGodot[bone]
                    : worldGodot[bone];

                Vector3 pos = localGodot.Origin;
                Quaternion rot = localGodot.Basis.GetRotationQuaternion();
                Vector3 scale = localGodot.Basis.Scale;

                anim.PositionTrackInsertKey(posTrack[bone], time, pos);
                anim.RotationTrackInsertKey(rotTrack[bone], time, rot);
                anim.ScaleTrackInsertKey(sclTrack[bone], time, scale);
            }
        }

        return anim;
    }

    private static readonly Transform3D Identity = Transform3D.Identity;

    /// <summary>
    /// Fill <paramref name="worldGodot"/> with each bone's Godot-space WORLD transform for one frame: build the
    /// Quake world (into the caller's <paramref name="worldQuake"/> scratch) by chaining frame bone-local TRS
    /// through parents, then conjugate by M. Parents precede children (IQM guarantees parent &lt; child), so one
    /// forward pass suffices. Both scratch arrays are owned by the caller and reused across frames.
    /// </summary>
    private static void ComputeFrameWorlds(
        IqmJoint[] joints, IqmBonePose[] poses, int boneCount,
        Transform3D[] worldQuake, Transform3D[] worldGodot)
    {
        for (int bone = 0; bone < boneCount; bone++)
        {
            Transform3D localQuake = bone < poses.Length
                ? QuakeTrsToTransform(poses[bone].Translate, poses[bone].Rotate, poses[bone].Scale)
                : (bone < joints.Length
                    ? QuakeTrsToTransform(joints[bone].Translate, joints[bone].Rotate, joints[bone].Scale)
                    : Identity);

            int parent = bone < joints.Length ? joints[bone].Parent : -1;
            worldQuake[bone] = (parent >= 0 && parent < boneCount)
                ? worldQuake[parent] * localQuake
                : localQuake;

            worldGodot[bone] = Mxf * worldQuake[bone] * MxfInv;
        }
    }

    // =================================================================================================
    //  4. Attachment query helper
    // =================================================================================================

    /// <summary>
    /// World-space (model-relative) REST transform of a named bone, for attaching weapons/effects to a socket
    /// (the IQM analogue of an MD3 tag; this is what the <c>gettaginfo</c>/<c>setattachment</c> facade queries).
    /// Returns <see cref="Transform3D.Identity"/> if the bone name is unknown. For the LIVE animated transform,
    /// use the skeleton's <c>GetBoneGlobalPose(idx)</c> at runtime; this returns the static bind/rest pose.
    /// </summary>
    public static Transform3D GetBoneGlobalRest(Skeleton3D skeleton, string boneName)
    {
        if (skeleton is null || string.IsNullOrEmpty(boneName))
            return Transform3D.Identity;
        // Bones were added under sanitised names; sanitise the query so callers can pass the raw IQM joint name.
        int idx = skeleton.FindBone(SanitizeBoneName(boneName));
        if (idx < 0)
            idx = skeleton.FindBone(boneName); // fall back to an exact match if the caller pre-sanitised.
        if (idx < 0)
            return Transform3D.Identity;
        // GetBoneGlobalRest composes the per-bone rests up the parent chain into model space — exactly the
        // Godot-space world rest we built, so attachment sockets line up with the conjugated bone convention.
        return skeleton.GetBoneGlobalRest(idx);
    }

    /// <summary>
    /// Convenience overload: find the model's <see cref="Skeleton3D"/> under <paramref name="modelRoot"/> (as
    /// returned by <see cref="Build"/>) and query a bone's global rest in one call.
    /// </summary>
    public static Transform3D GetBoneGlobalRest(Node3D modelRoot, string boneName)
    {
        Skeleton3D? skel = FindSkeleton(modelRoot);
        return skel is null ? Transform3D.Identity : GetBoneGlobalRest(skel, boneName);
    }

    /// <summary>Locate the <see cref="Skeleton3D"/> child of a model root built by <see cref="Build"/>.</summary>
    public static Skeleton3D? FindSkeleton(Node3D modelRoot)
    {
        if (modelRoot is null)
            return null;
        foreach (Node child in modelRoot.GetChildren())
            if (child is Skeleton3D s)
                return s;
        return null;
    }

    // =================================================================================================
    //  Shared math + utilities
    // =================================================================================================

    /// <summary>
    /// Compose a bone-local Quake TRS (translate * rotate * scale) into a <see cref="Transform3D"/> carrying
    /// RAW Quake numbers (no axis swap yet — the swap happens once at world level via conjugation). The
    /// quaternion maps component-for-component (x,y,z,w)->(x,y,z,w).
    /// </summary>
    private static Transform3D QuakeTrsToTransform(SN.Vector3 translate, SN.Quaternion rotate, SN.Vector3 scale)
    {
        var q = new Quaternion(rotate.X, rotate.Y, rotate.Z, rotate.W);
        // Guard a zero/denormal quaternion (some exporters write (0,0,0,0)) -> identity rotation.
        if (!(q.LengthSquared() > 1e-12f))
            q = Quaternion.Identity;
        else
            q = q.Normalized();

        // Compose R * S (rotation THEN non-uniform scale, applied in the bone's local space). Note Godot's
        // Basis.Scaled pre-multiplies (S * B), which only equals R * S for uniform scale; build R * S
        // explicitly via post-multiplication so non-uniform bone scale is not sheared.
        Basis basis = new Basis(q) * Basis.FromScale(new Vector3(scale.X, scale.Y, scale.Z));
        return new Transform3D(basis, new Vector3(translate.X, translate.Y, translate.Z));
    }

    /// <summary>True if the model carries per-vertex skin data (so the mesh should be bound to the skeleton).</summary>
    private static bool HasSkinning(IqmData iqm)
        => iqm.Joints.Length > 0 && iqm.BlendIndexes is { Length: > 0 } && iqm.BlendWeights is { Length: > 0 };

    /// <summary>
    /// The material name a mesh will actually resolve after the <c>_N.skin</c> remap — null when the skin
    /// hides the mesh (common/nodraw). Shared by <see cref="BuildMesh"/> and the off-thread texture-predecode
    /// pass (§12.3-1) so the predecoded set always matches what the build loads.
    /// </summary>
    public static string? EffectiveMaterialName(IqmMesh sub, SkinFile? skin, out bool remapped)
    {
        remapped = false;
        string materialName = sub.Material;
        if (skin is not null && !string.IsNullOrEmpty(sub.Name)
            && skin.MeshToTexture.TryGetValue(sub.Name, out string? remap) && !string.IsNullOrEmpty(remap))
        {
            if (SkinFile.IsNoDraw(remap))
                return null; // skin hides this mesh
            materialName = remap;
            remapped = true;
        }
        // A RAW nodraw mesh material also hides (no skin remap needed) — the invisible-hand weapon rigs'
        // skeleton plane is baked as material 'nodraw'; rendered, it was the black landing-dip triangle (r11).
        if (SkinFile.IsNoDraw(materialName))
            return null;
        return materialName;
    }

    /// <summary>Resolve a material through the asset facade, tolerating a null facade (build-order safety).</summary>
    private static Material? ResolveMaterialSafe(AssetSystem assets, string materialName)
    {
        if (assets is null || string.IsNullOrEmpty(materialName))
            return null;
        return assets.ResolveModelMaterial(materialName);
    }

    /// <summary>A normalized clip definition independent of whether it came from an IQM anim or a framegroup.</summary>
    private readonly record struct ClipSpec(string Name, int FirstFrame, int FrameCount, float Fps, bool Loop);

    /// <summary>Synthesize a clip name from its index, biased toward Xonotic's canonical anim order when it
    /// lines up (idle/run/jump/...); otherwise <c>anim_&lt;i&gt;</c>. Pure naming convenience — ranges are
    /// authoritative.</summary>
    private static string DefaultClipName(int i) => i >= 0 && i < CanonicalAnimNames.Length
        ? CanonicalAnimNames[i]
        : $"anim_{i}";

    /// <summary>Base's weapon-hand rig slot contract (CL_WeaponEntity_SetModel, common/weapons/all.qc:373-376):
    /// a nameless 4-group `.framegroups` set is fire/fire2/idle/reload BY POSITION. Shared with
    /// <see cref="DpmBuilder"/> — the DPM hand rigs (h_electro/h_crylink/h_rl/h_gl/h_hagar) carry the same
    /// nameless 4-slot convention. (playtest #36/r9)</summary>
    internal static readonly string[] WeaponSlotNames = { "fire", "fire2", "idle", "reload" };

    // Common Xonotic player-anim ordering used when a framegroup/anim has no explicit name. Not load-bearing.
    private static readonly string[] CanonicalAnimNames =
    {
        "idle", "run", "runbackwards", "strafeleft", "straferight",
        "jump", "duck", "duckwalk", "duckjump", "draw", "pain", "death1", "death2",
    };

    /// <summary>De-duplicate clip names (Godot library keys must be unique) by appending an index suffix.</summary>
    private static string UniqueName(string baseName, HashSet<string> used)
    {
        string name = string.IsNullOrEmpty(baseName) ? "anim" : baseName;
        if (used.Add(name))
            return name;
        int k = 1;
        while (!used.Add($"{name}_{k}"))
            k++;
        return $"{name}_{k}";
    }

    /// <summary>
    /// Replace characters Godot forbids in node/bone names — the NodePath separators <c>: / @ %</c> and the
    /// dot — with underscores, so a bone name can be embedded verbatim in an animation track sub-path
    /// (<c>"Skeleton3D:bone"</c>). Matches the sibling MD3/DPM builders' sanitiser.
    /// </summary>
    private static string SanitizeBoneName(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return raw;
        Span<char> buf = stackalloc char[raw.Length];
        int n = 0;
        foreach (char c in raw)
            buf[n++] = c is ':' or '/' or '@' or '%' or '.' ? '_' : c;
        return new string(buf[..n]);
    }
}
