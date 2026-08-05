using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Formats.Dpm;
using VortexArena.Formats.Sidecars;

namespace VortexArena.Game.Loaders.Models;

/// <summary>
/// Turns a parsed <see cref="DpmData"/> (the Godot-free DarkPlaces DPM importer output) into a Godot
/// scene: a <see cref="Skeleton3D"/> driving skinned <see cref="MeshInstance3D"/>s, with the model's
/// animation frames baked into an <see cref="AnimationPlayer"/>.
///
/// <para>DPM is a <b>skeletal</b> format (unlike MD3's vertex morph, see <see cref="Md3Builder"/>):</para>
/// <list type="bullet">
///   <item><b>Bones.</b> Each <see cref="DpmFrame"/> stores a per-bone 3x4 pose that is
///     <i>parent-relative</i>. The bind pose is frame 0; world-space bind matrices are produced by a
///     single forward pass chaining parent -> child (the format guarantees a parent's index precedes its
///     child's). Bone rests are set from those world matrices, converted Quake -> Godot via
///     <see cref="Coords"/>.</item>
///   <item><b>Skinning.</b> DPM vertices carry a <i>variable</i> number of weighted bone influences;
///     Godot's vertex format supports at most four. We keep the four largest influences per vertex and
///     renormalise them. The bind position is the influence-weighted sum of each weight's bone-relative
///     <see cref="DpmBoneWeight.Origin"/> transformed by that bone's frame-0 world matrix (DarkPlaces'
///     skinning, frozen at the bind pose).</item>
///   <item><b>Animation.</b> Every frame's per-bone parent-relative pose becomes bone-local
///     translation/rotation/scale tracks on an <see cref="Animation"/>. Frames are carved into named
///     clips by the optional <c>.framegroups</c> sidecar, else a single full-range clip is emitted.</item>
/// </list>
///
/// Materials are resolved from each mesh's shader name through <see cref="AssetSystem.ResolveMaterial"/>.
/// </summary>
public static class DpmBuilder
{
    /// <summary>Godot caps skinning at four bone influences per vertex; we clamp DPM's variable set to this.</summary>
    private const int MaxBonesPerVertex = 4;

    /// <summary>
    /// Meta key under which <see cref="Build"/> stores the built model's frame-group clip names IN FRAME-GROUP
    /// ORDER (a <see cref="Godot.Collections.Array"/> of strings). The client's <c>DpmFrameDriver</c> reads it to
    /// map a networked frame-GROUP ordinal (DarkPlaces <c>Mod_FrameGroupify</c>: <c>self.frame = N</c> plays the
    /// Nth group) to the corresponding <see cref="AnimationPlayer"/> clip. Absent on models with no clips.
    /// </summary>
    public const string FrameGroupClipsMeta = "xon_dpm_clips";

    // The Quake->Godot basis change M (= Coords.ToGodot applied to each axis, as Basis columns) and its
    // inverse, used to conjugate bone WORLD transforms into Godot space (W_godot = M * W_quake * M^-1).
    // A bone transform is expressed IN model space, so converting it to Godot model space is a similarity
    // transform — NOT a per-axis column swap of the local pose (that distorts the parent chain). The same
    // M is the linear part of Coords.ToGodot, so conjugated bones stay coincident with the C*(skinned
    // vertex) geometry: the per-bone delta pose*rest^-1 reduces to M*(W_q,f * W_q,0^-1)*M^-1, exactly the
    // conjugated Quake motion. Built once from Coords so it can never drift from the project's source of truth.
    private static readonly Transform3D Mxf = new(
        new Basis(
            Coords.ToGodot(new System.Numerics.Vector3(1f, 0f, 0f)),   // column 0 = image of Quake +X
            Coords.ToGodot(new System.Numerics.Vector3(0f, 1f, 0f)),   // column 1 = image of Quake +Y
            Coords.ToGodot(new System.Numerics.Vector3(0f, 0f, 1f))),  // column 2 = image of Quake +Z
        Vector3.Zero);

    private static readonly Transform3D MxfInv = Mxf.AffineInverse();

    /// <summary>
    /// OFF-THREAD-SAFE (pure data): the material names a build of <paramref name="dpm"/> will resolve — one
    /// per mesh shader name, deduplicated. DPM carries no <c>.skin</c> remap, so this is simply the set the
    /// per-mesh <c>ResolveMaterial</c> calls in <see cref="Build"/> will ask for. Lets the off-thread texture
    /// predecode warm exactly what the main-thread build then loads (the DPM counterpart of
    /// <see cref="Md3Builder.EffectiveMaterials"/>).
    /// </summary>
    public static List<string> EffectiveMaterials(DpmData dpm)
    {
        var mats = new List<string>(4);
        if (dpm is null)
            return mats;
        foreach (DpmMesh mesh in dpm.Meshes)
            if (!string.IsNullOrEmpty(mesh.ShaderName) && !mats.Contains(mesh.ShaderName))
                mats.Add(mesh.ShaderName);
        return mats;
    }

    /// <summary>
    /// Build a posed, skinned, animated DPM model.
    /// </summary>
    /// <param name="dpm">Parsed DPM data (bind pose = frame 0).</param>
    /// <param name="assets">Material facade; surface shader names resolve through <see cref="AssetSystem.ResolveMaterial"/>.</param>
    /// <param name="framegroups">
    /// Optional clip ranges (from a <c>.framegroups</c> sidecar). When null/empty a single clip spanning
    /// all frames is created.
    /// </param>
    /// <returns>
    /// A <see cref="Node3D"/> root holding the <see cref="Skeleton3D"/> (whose children are the skinned
    /// <see cref="MeshInstance3D"/>s) and an <see cref="AnimationPlayer"/> named "AnimationPlayer".
    /// </returns>
    public static Node3D Build(DpmData dpm, AssetSystem assets, IReadOnlyList<FrameGroup>? framegroups = null)
    {
        ArgumentNullException.ThrowIfNull(dpm);

        var root = new Node3D { Name = "DpmModel" };

        int boneCount = dpm.Bones.Length;

        // ---- Skeleton -----------------------------------------------------------------------------
        // World-space bind matrices (frame 0), in Quake space, used both to seat bone rests and to skin
        // the bind-pose vertices.
        System.Numerics.Matrix4x4[] worldBind = ComputeWorldBind(dpm, frame: 0);

        var skeleton = new Skeleton3D { Name = "Skeleton3D" };
        root.AddChild(skeleton);

        for (int b = 0; b < boneCount; b++)
        {
            DpmBone bone = dpm.Bones[b];
            skeleton.AddBone(string.IsNullOrEmpty(bone.Name) ? $"bone{b}" : bone.Name);
        }
        // Parent links are set in a second pass: AddBone appends at the end, so a parent that comes first
        // already exists by the time we wire children (DPM guarantees parent index < child index, but we
        // do it as a separate pass to avoid depending on AddBone's return value).
        for (int b = 0; b < boneCount; b++)
        {
            int parent = dpm.Bones[b].Parent;
            if (parent >= 0 && parent < boneCount)
                skeleton.SetBoneParent(b, parent);
        }
        // Bone rests = the bind pose (frame 0) as bone-LOCAL Godot transforms. We build each bone's Godot
        // WORLD via conjugation, then localize against the Godot parent world — the only convention that
        // stays consistent with the C*(skinned vertex) geometry (a per-axis swap of the local pose would
        // distort the parent chain). CreateSkinFromRestTransforms then derives bind inverses from these.
        DpmBonePose[] bindPoses = dpm.Frames.Length > 0 ? dpm.Frames[0].BonePoses : Array.Empty<DpmBonePose>();
        Transform3D[] bindWorldGodot = ComputeGodotWorld(dpm.Bones, bindPoses);
        for (int b = 0; b < boneCount; b++)
        {
            Transform3D rest = Localize(bindWorldGodot, dpm.Bones, b);
            skeleton.SetBoneRest(b, rest);
            // Seed the live pose to rest so the bound mesh shows the bind pose before any clip plays.
            skeleton.SetBonePosePosition(b, rest.Origin);
            skeleton.SetBonePoseRotation(b, rest.Basis.GetRotationQuaternion());
            skeleton.SetBonePoseScale(b, rest.Basis.Scale);
        }

        // A Skin derived from the rest transforms maps mesh bind-space -> bone space for every bone, so
        // bones referenced by skin weights resolve regardless of mesh ordering.
        Skin skin = skeleton.CreateSkinFromRestTransforms();

        // ---- Skinned meshes -----------------------------------------------------------------------
        int meshIndex = 0;
        foreach (DpmMesh mesh in dpm.Meshes)
        {
            MeshInstance3D? mi = BuildMeshInstance(mesh, worldBind, skin, assets, meshIndex);
            if (mi is not null)
            {
                skeleton.AddChild(mi);
                // The MeshInstance must live under the Skeleton3D so this NodePath resolves to it.
                mi.Skeleton = mi.GetPathTo(skeleton);
                meshIndex++;
            }
        }

        // ---- Animations ---------------------------------------------------------------------------
        var player = new AnimationPlayer { Name = "AnimationPlayer" };
        root.AddChild(player);
        // Bone-track paths are "Skeleton3D:bone" — relative to the MODEL ROOT (the skeleton is a child of it),
        // so RootNode must be the model root (= the player's parent), not the skeleton itself.
        player.RootNode = player.GetPathTo(root);
        // Bake the frame-group clips and capture their names IN FRAME-GROUP ORDER. DarkPlaces' Mod_FrameGroupify
        // makes a skeletal model's networked `.frame = N` select the Nth frame GROUP (not a raw pose), so the
        // server stamps Entity.Frame with the group ORDINAL (QC mr_anim: spider idle '5', walk '10', bite '0',
        // shoot '3', pain1/2 '7'/'8', die1/2 '1'/'2'). Stash the ordered clip names on the root as metadata so a
        // frame-driver (DpmFrameDriver, wired in ClientWorld for frame-driven monster entities) can map that
        // ordinal back to the clip and play it — the DPM analog of ModelAnimator.FollowEntityFrame for MD3.
        List<string> clipNames = BuildAnimations(dpm, skeleton, player, framegroups);
        if (clipNames.Count > 0)
        {
            var meta = new Godot.Collections.Array();
            foreach (string n in clipNames)
                meta.Add(n);
            root.SetMeta(FrameGroupClipsMeta, meta);
        }

        return root;
    }

    // ===============================================================================================
    //  Skeleton math
    // ===============================================================================================

    /// <summary>
    /// Compose world-space bone matrices for a frame by chaining parent -> child. DPM poses are
    /// parent-relative and stored with System.Numerics' row-vector convention (see
    /// <see cref="DpmBonePose.ToMatrix"/>), so a child's world matrix is <c>local * parentWorld</c>.
    /// </summary>
    private static System.Numerics.Matrix4x4[] ComputeWorldBind(DpmData dpm, int frame)
    {
        int boneCount = dpm.Bones.Length;
        var world = new System.Numerics.Matrix4x4[boneCount];
        if (boneCount == 0)
            return world;

        DpmBonePose[] poses = (frame >= 0 && frame < dpm.Frames.Length)
            ? dpm.Frames[frame].BonePoses
            : Array.Empty<DpmBonePose>();

        for (int b = 0; b < boneCount; b++)
        {
            System.Numerics.Matrix4x4 local = (b < poses.Length) ? poses[b].ToMatrix() : System.Numerics.Matrix4x4.Identity;
            int parent = dpm.Bones[b].Parent;
            world[b] = (parent >= 0 && parent < b)
                ? local * world[parent]   // row-vector: apply local first, then parent's world
                : local;
        }
        return world;
    }

    /// <summary>
    /// Convert a DPM parent-relative pose into a <see cref="Transform3D"/> carrying RAW Quake numbers (no
    /// axis swap — the swap happens once at world level via <see cref="Mxf"/> conjugation). DPM stores the
    /// pose with its three basis COLUMNS as <see cref="DpmBonePose.Right"/>/<see cref="DpmBonePose.Up"/>/
    /// <see cref="DpmBonePose.Forward"/> (the images of the basis vectors) and translation
    /// <see cref="DpmBonePose.Origin"/>; Godot's <see cref="Basis"/> ctor also takes column vectors, so this
    /// is the column-vector form of the DPM local matrix. Chaining these in Godot's convention
    /// (<c>parent * local</c>) reproduces the Quake world transform.
    /// </summary>
    private static Transform3D QuakePoseToTransform(DpmBonePose pose)
    {
        var basis = new Basis(
            new Vector3(pose.Right.X, pose.Right.Y, pose.Right.Z),
            new Vector3(pose.Up.X, pose.Up.Y, pose.Up.Z),
            new Vector3(pose.Forward.X, pose.Forward.Y, pose.Forward.Z));
        return new Transform3D(basis, new Vector3(pose.Origin.X, pose.Origin.Y, pose.Origin.Z));
    }

    /// <summary>
    /// Compute each bone's Godot-space WORLD transform for one frame's bone poses: chain the raw-Quake
    /// local poses through parents (Quake world), then conjugate by <see cref="Mxf"/>. Parents precede
    /// children (DPM guarantees parent index &lt; child index), so one forward pass suffices. The
    /// localized form of these is what bone rests / animation keys use, keeping bones consistent with the
    /// <c>C * (skinned vertex)</c> geometry.
    /// </summary>
    private static Transform3D[] ComputeGodotWorld(DpmBone[] bones, DpmBonePose[] poses)
    {
        int n = bones.Length;
        var worldQuake = new Transform3D[n];
        var worldGodot = new Transform3D[n];
        for (int b = 0; b < n; b++)
        {
            Transform3D local = (b < poses.Length) ? QuakePoseToTransform(poses[b]) : Transform3D.Identity;
            int parent = bones[b].Parent;
            worldQuake[b] = (parent >= 0 && parent < b) ? worldQuake[parent] * local : local;
            worldGodot[b] = Mxf * worldQuake[b] * MxfInv;
        }
        return worldGodot;
    }

    /// <summary>Bone-LOCAL Godot transform: the bone's Godot world relative to its parent's Godot world.</summary>
    private static Transform3D Localize(Transform3D[] worldGodot, DpmBone[] bones, int b)
    {
        int parent = bones[b].Parent;
        return (parent >= 0 && parent < worldGodot.Length)
            ? worldGodot[parent].AffineInverse() * worldGodot[b]
            : worldGodot[b];
    }

    // ===============================================================================================
    //  Skinned mesh assembly
    // ===============================================================================================

    private static MeshInstance3D? BuildMeshInstance(
        DpmMesh mesh,
        System.Numerics.Matrix4x4[] worldBind,
        Skin skin,
        AssetSystem assets,
        int meshIndex)
    {
        int vcount = mesh.Vertices.Length;
        if (vcount == 0 || mesh.Triangles.Length == 0)
            return null;

        var positions = new Vector3[vcount];
        var normals = new Vector3[vcount];
        var uvs = new Vector2[vcount];
        var boneIdx = new int[vcount * MaxBonesPerVertex];
        var boneWts = new float[vcount * MaxBonesPerVertex];

        for (int v = 0; v < vcount; v++)
        {
            DpmVertex vert = mesh.Vertices[v];

            // Bind-pose position/normal = influence-weighted sum of each weight transformed by its bone's
            // frame-0 world matrix. This is computed over ALL weights so geometry is exact even though the
            // skin payload (below) only keeps the top four.
            System.Numerics.Vector3 pos = System.Numerics.Vector3.Zero;
            System.Numerics.Vector3 nrm = System.Numerics.Vector3.Zero;
            foreach (DpmBoneWeight w in vert.Weights)
            {
                if (w.BoneNum < 0 || w.BoneNum >= worldBind.Length)
                    continue;
                System.Numerics.Matrix4x4 m = worldBind[w.BoneNum];
                pos += System.Numerics.Vector3.Transform(w.Origin, m) * w.Influence;
                nrm += System.Numerics.Vector3.TransformNormal(w.Normal, m) * w.Influence;
            }

            positions[v] = Coords.ToGodot(pos);
            Vector3 gnrm = Coords.ToGodot(nrm);
            normals[v] = gnrm.LengthSquared() > 1e-8f ? gnrm.Normalized() : Vector3.Up;
            uvs[v] = v < mesh.TexCoords.Length
                ? new Vector2(mesh.TexCoords[v].X, mesh.TexCoords[v].Y)
                : Vector2.Zero;

            // Top-4 weights (renormalised) for the GPU skin.
            WriteTopWeights(vert.Weights, worldBind.Length, boneIdx, boneWts, v * MaxBonesPerVertex);
        }

        // (playtest r9) DPM triangles are wound OPPOSITE to the IQM/MD3 convention relative to Godot's
        // cull_back — emitted verbatim, every DPM weapon rig rendered inside-out: front faces culled, interior
        // faces visible ("see through the geometry", brightest on the devastator/mortar). Swap each triangle
        // to (0,2,1) so the outside faces the camera, exactly like the other builders' file-order winding does.
        var indices = new int[mesh.Triangles.Length];
        for (int t = 0; t + 2 < mesh.Triangles.Length; t += 3)
        {
            int a = mesh.Triangles[t], b = mesh.Triangles[t + 1], c = mesh.Triangles[t + 2];
            indices[t] = (a >= 0 && a < vcount) ? a : 0;
            indices[t + 1] = (c >= 0 && c < vcount) ? c : 0; // swapped
            indices[t + 2] = (b >= 0 && b < vcount) ? b : 0; // swapped
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = positions;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Bones] = boneIdx;     // int[] length vcount*4 => 4 bones/vertex
        arrays[(int)Mesh.ArrayType.Weights] = boneWts;   // float[] length vcount*4
        arrays[(int)Mesh.ArrayType.Index] = indices;

        var arrayMesh = new ArrayMesh();
        arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        Material? material = assets?.ResolveModelMaterial(mesh.ShaderName);
        if (material is not null)
            arrayMesh.SurfaceSetMaterial(0, material);

        return new MeshInstance3D
        {
            Name = string.IsNullOrEmpty(mesh.ShaderName) ? $"Mesh{meshIndex}" : SanitizeName(mesh.ShaderName),
            Mesh = arrayMesh,
            Skin = skin,
            // The .Skeleton NodePath is set by the caller (GetPathTo) once this is parented to the skeleton.
        };
    }

    /// <summary>
    /// Pick the four heaviest influences from a vertex's variable weight list, renormalise them to sum to
    /// 1, and write them into the flat bone-index / weight buffers at <paramref name="dst"/>. Unused slots
    /// are zero-filled (bone 0, weight 0), which Godot treats as no contribution.
    /// </summary>
    private static void WriteTopWeights(
        DpmBoneWeight[] weights,
        int boneCount,
        int[] boneIdx,
        float[] boneWts,
        int dst)
    {
        // Selection of the top-4 by influence. Small N (DPM rarely exceeds 4 already) so a linear
        // insertion into a fixed 4-slot buffer is cheapest and allocation-free.
        Span<int> bestBone = stackalloc int[MaxBonesPerVertex];
        Span<float> bestInf = stackalloc float[MaxBonesPerVertex];
        for (int k = 0; k < MaxBonesPerVertex; k++) { bestBone[k] = 0; bestInf[k] = 0f; }

        foreach (DpmBoneWeight w in weights)
        {
            int bone = (w.BoneNum >= 0 && w.BoneNum < boneCount) ? w.BoneNum : 0;
            float inf = w.Influence;
            if (inf <= 0f)
                continue;

            // Find the smallest current slot; replace it if this influence is larger.
            int minSlot = 0;
            for (int k = 1; k < MaxBonesPerVertex; k++)
                if (bestInf[k] < bestInf[minSlot])
                    minSlot = k;
            if (inf > bestInf[minSlot])
            {
                bestInf[minSlot] = inf;
                bestBone[minSlot] = bone;
            }
        }

        float sum = 0f;
        for (int k = 0; k < MaxBonesPerVertex; k++)
            sum += bestInf[k];

        if (sum <= 0f)
        {
            // Degenerate vertex (no usable weights): bind it rigidly to bone 0 so it isn't collapsed.
            boneIdx[dst] = 0;
            boneWts[dst] = 1f;
            for (int k = 1; k < MaxBonesPerVertex; k++)
            {
                boneIdx[dst + k] = 0;
                boneWts[dst + k] = 0f;
            }
            return;
        }

        float inv = 1f / sum;
        for (int k = 0; k < MaxBonesPerVertex; k++)
        {
            boneIdx[dst + k] = bestBone[k];
            boneWts[dst + k] = bestInf[k] * inv;
        }
    }

    // ===============================================================================================
    //  Animation baking
    // ===============================================================================================

    /// <summary>
    /// Bake the model's frames into clips on <paramref name="player"/>. Each bone gets position, rotation
    /// and scale tracks addressing <c>Skeleton3D:boneName</c>; key values are the per-frame parent-relative
    /// pose in Godot space (the bone-local TRS the AnimationPlayer applies on top of rest). Clips come from
    /// <paramref name="framegroups"/>, or one full-range clip when none are supplied.
    /// </summary>
    private static List<string> BuildAnimations(
        DpmData dpm,
        Skeleton3D skeleton,
        AnimationPlayer player,
        IReadOnlyList<FrameGroup>? framegroups)
    {
        // Clip names emitted IN FRAME-GROUP ORDER (index i = the i'th group), so a frame-driver can address the
        // clip by the networked frame-group ordinal.
        var emittedNames = new List<string>();
        int frameCount = dpm.Frames.Length;
        if (frameCount == 0 || dpm.Bones.Length == 0)
            return emittedNames;

        var library = new AnimationLibrary();

        IReadOnlyList<FrameGroup> groups = (framegroups is { Count: > 0 })
            ? framegroups
            : new List<FrameGroup> { new(0, frameCount, 20f, true, "all") };

        // Pre-decode every frame's bone-LOCAL Godot transforms once (reused across overlapping clips). Each
        // frame: chain raw-Quake locals -> Quake world -> conjugate to Godot world -> localize. This is the
        // same world-conjugation as the rest pose, so keys are bone-local TRS the AnimationPlayer applies.
        var localPerFrame = new Transform3D[frameCount][];
        for (int f = 0; f < frameCount; f++)
        {
            Transform3D[] worldGodot = ComputeGodotWorld(dpm.Bones, dpm.Frames[f].BonePoses);
            var arr = new Transform3D[dpm.Bones.Length];
            for (int b = 0; b < dpm.Bones.Length; b++)
                arr[b] = Localize(worldGodot, dpm.Bones, b);
            localPerFrame[f] = arr;
        }

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        // (playtest r9) The weapon-hand slot contract, same as IqmBuilder: a NAMELESS 4-group `.framegroups`
        // set is fire/fire2/idle/reload BY POSITION (CL_WeaponEntity_SetModel). The DPM hand rigs
        // (h_electro/h_crylink/h_rl/h_gl/h_hagar) ship exactly that; the old `anim_{i}` synthesis meant
        // ViewModel's fire/idle/reload lookups never matched a DPM rig — no viewmodel animation at all.
        bool allNameless = true;
        foreach (FrameGroup g in groups)
            if (!string.IsNullOrEmpty(g.Name)) { allNameless = false; break; }
        string[]? slotNames = allNameless && groups.Count == IqmBuilder.WeaponSlotNames.Length
            ? IqmBuilder.WeaponSlotNames : null;

        int groupIndex = 0;
        foreach (FrameGroup group in groups)
        {
            int first = Math.Clamp(group.FirstFrame, 0, frameCount - 1);
            int count = Math.Clamp(group.FrameCount, 1, frameCount - first);
            float fps = group.Fps > 0f ? group.Fps : 20f;

            var anim = new Animation
            {
                Length = Math.Max(count - 1, 0) / fps,
                LoopMode = group.Loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None,
            };

            for (int b = 0; b < dpm.Bones.Length; b++)
            {
                string boneName = skeleton.GetBoneName(b);
                // Relative to AnimationPlayer.RootNode (the skeleton): "<skeleton>:<bone>".
                string path = $"{skeleton.Name}:{boneName}";

                int posTrack = anim.AddTrack(Animation.TrackType.Position3D);
                int rotTrack = anim.AddTrack(Animation.TrackType.Rotation3D);
                int sclTrack = anim.AddTrack(Animation.TrackType.Scale3D);
                anim.TrackSetPath(posTrack, path);
                anim.TrackSetPath(rotTrack, path);
                anim.TrackSetPath(sclTrack, path);

                for (int i = 0; i < count; i++)
                {
                    int frame = first + i;
                    float time = i / fps;
                    Transform3D xf = localPerFrame[frame][b];
                    Basis basis = xf.Basis;
                    Vector3 scale = basis.Scale;
                    anim.PositionTrackInsertKey(posTrack, time, xf.Origin);
                    anim.RotationTrackInsertKey(rotTrack, time, basis.GetRotationQuaternion());
                    anim.ScaleTrackInsertKey(sclTrack, time, scale);
                }
            }

            string name = slotNames is not null && groupIndex < slotNames.Length
                ? UniqueName(slotNames[groupIndex], usedNames)
                : ClipName(group, groupIndex, usedNames);
            library.AddAnimation(name, anim);
            emittedNames.Add(name);
            groupIndex++;
        }

        player.AddAnimationLibrary("", library);
        return emittedNames;
    }

    /// <summary>Resolve a unique, sensible clip name for a frame group (its name, or a synthesised one).</summary>
    private static string ClipName(FrameGroup group, int index, HashSet<string> used)
        => UniqueName(string.IsNullOrEmpty(group.Name) ? $"anim_{index}" : group.Name, used);

    /// <summary>De-duplicate a clip name against the already-emitted set (`name`, `name_1`, …).</summary>
    private static string UniqueName(string baseName, HashSet<string> used)
    {
        string name = baseName;
        int suffix = 1;
        while (!used.Add(name))
            name = $"{baseName}_{suffix++}";
        return name;
    }

    /// <summary>Strip characters Godot disallows in node names (':' '/' '@' '%' and dots).</summary>
    private static string SanitizeName(string raw)
    {
        Span<char> buf = stackalloc char[raw.Length];
        int n = 0;
        foreach (char c in raw)
            buf[n++] = c is ':' or '/' or '@' or '%' or '.' ? '_' : c;
        return new string(buf[..n]);
    }
}
