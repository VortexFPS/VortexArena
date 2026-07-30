using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Particles;
using VortexArena.Formats.Vfs;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client.Particles;

// =====================================================================================================
//  SDF RUNTIME SERVICE (planning/particles-dual-system.md §A.4 / §A.5 / §A.6)
//
//  Turns a map's chunked .psdf signed-distance field into Godot GPUParticlesCollisionSDF3D collider
//  nodes that the MODERN GPU particle backend samples for COLLIDED / COLLISION_NORMAL / COLLISION_DEPTH.
//  Owns the whole runtime lifecycle: resolve / load / generate the field, upload each occupied chunk to a
//  Texture3D collider, keep an LRU resident set under Godot's hard 7-texture-per-system cap, and answer
//  "is this point covered yet?" so the router can gate cl_particles_modern_nosdf (§A.6).
//
//  COORDINATE BRIDGE. The whole sim/collision stack is Quake space (Z-up, qu). The .psdf grid, chunk
//  CellMins and per-voxel distances are all Quake-space, chunk-local, ordered X-fastest then Y then Z
//  (PsdfFormat.SdfChunk.Distances). Godot colliders live in Y-up space, so every world position is bridged
//  through Coords (godot = (q.X, q.Z, -q.Y)) and the voxel slab is re-indexed into Godot box-local axis order
//  when building the Texture3D (see BuildSdfTexture). Distances are a SCALAR magnitude in qu == Godot units
//  (Coords is a pure axis swap, length-preserving), so they need no remap, only the voxel addressing does.
// =====================================================================================================

/// <summary>
/// Builds and manages the per-map set of <see cref="GpuParticlesCollisionSdf3D"/> nodes that back modern
/// particle collision. Add it once under the client world; call <see cref="BuildForMap"/> on map load,
/// <see cref="UpdateProximity"/> each frame with the live modern-emitter origins, and <see cref="Clear"/>
/// on map change. <see cref="HasCoverage"/> lets the router decide modern-vs-faithful per spawn (§A.6).
/// </summary>
public sealed partial class SdfCollisionService : Node3D
{
    // ---------------------------------------------------------------------------------------------
    //  §A.4 ENCODING CONTRACT (single source of truth for the texture upload)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The single-channel float format the chunk distance slab is uploaded as. The .psdf stores R16F on
    /// disk (§A.2) but we widen to 32-bit <see cref="Image.Format.Rf"/> in VRAM so the runtime upload never
    /// adds quantization on top of the generator's band-clamped distances — the texture is small relative to
    /// the 7-resident cap (128³ R16F ≈ 4.2 MB; Rf ≈ 8.4 MB) so the headroom is acceptable.
    ///
    /// TODO(§A.4, editor-bake validation): Godot's GPUParticlesCollisionSDF3D upload/sampling encoding
    /// (exact internal format, distance normalization, and local-space sampling convention) lives in
    /// renderer_rd/storage_rd/particles_storage.cpp + particles.glsl and is NOT documented. This service
    /// assumes the collider samples its Texture3D as RAW signed world-unit distances in box-local space with
    /// the box centered on its node origin (the only convention that makes a hand-built SDF agree with the
    /// editor's own bake). That assumption CANNOT be verified headlessly. The de-risk task is to bake a unit
    /// box scene with the editor's GPUParticlesCollisionSDF3D, dump its texture, and assert voxelwise
    /// agreement with <see cref="BuildSdfTexture"/>'s output within tolerance. Until that spike runs, treat
    /// the distance scale / sign / axis order below as PROVISIONAL — if the editor normalizes distances
    /// (e.g. by box half-extent) the fix is a single scale here, not a structural change.
    /// </summary>
    private const Image.Format SdfTextureFormat = Image.Format.Rf;

    // ---------------------------------------------------------------------------------------------
    //  Resident-set policy (§A.5 — 7 SDF textures per particle system, 32 colliders engine cap)
    // ---------------------------------------------------------------------------------------------

    /// <summary>Max chunk colliders kept ENABLED (texture resident) at once. Godot allows MAX_3D_TEXTURES=7
    /// per particle system per frame; we keep a larger LRU so panning across a corner doesn't thrash, and
    /// rely on each emitter's own AABB to intersect at most a handful. ≈16 × 8.4 MB ≈ 134 MB worst case.</summary>
    private const int ResidentBudget = 16;

    /// <summary>A live emitter's effective reach (qu) when deciding which chunks are "near". Matches the modern
    /// burst VisibilityAabb cap (§A.5: ≤ ±384qu) so an emitter spans ≤2 cells/axis.</summary>
    private const float EmitterReach = 384f;

    // ---------------------------------------------------------------------------------------------
    //  Per-chunk node record
    // ---------------------------------------------------------------------------------------------

    private sealed class ChunkNode
    {
        public required GpuParticlesCollisionSdf3D Node;
        public MeshInstance3D? DebugBox;          // §A.6 wireframe AABB, only when cl_particles_sdf_debug
        public required NVec3 CellMins;           // Quake-space mins of the cell AABB (no skirt)
        public required NVec3 CellMaxs;           // Quake-space maxs of the cell AABB (no skirt)
        public ulong LastTouched;                 // resident-LRU stamp (frame counter)
        public bool Enabled;
    }

    // ---------------------------------------------------------------------------------------------
    //  State
    // ---------------------------------------------------------------------------------------------

    private readonly List<ChunkNode> _chunks = new();
    private readonly object _chunksLock = new();  // _chunks is appended from a CallDeferred marshal; reads are main-thread

    private string _mapName = string.Empty;
    private float _skirt;                          // qu, mirrors SdfGenParams.Skirt
    private float _voxelSize = 8f;                 // qu, cl_particles_sdf_voxel; set in ResolveParams.
    private ulong _frame;                          // monotonic stamp for the resident LRU

    private CancellationTokenSource? _genCts;      // cancels in-flight async generation on Clear()

    // Completion accounting for the [ParticleSDF] log line (§A.3).
    private int _generatedChunks;
    private int _cachedChunks;
    private long _buildStartMs;
    private bool _buildLogged;

    public SdfCollisionService()
    {
        Name = "SdfCollisionService";
    }

    /// <summary>The CLIENT cvar store (MenuState.Cvars) for the cl_particles_sdf_* cvars; null falls back to
    /// Api.Cvars. MUST be the client store on a listen server (Api.Cvars there is the server store).</summary>
    public ICvarService? Cvars { get; set; }
    private float Cv(string name) => (Cvars ?? Api.Cvars).GetFloat(name);

    // =============================================================================================
    //  Public API
    // =============================================================================================

    /// <summary>
    /// Resolve and install the SDF field for <paramref name="mapName"/>. Search order (§A.2): (1) a
    /// compiler-baked <c>maps/&lt;map&gt;.psdf</c> via the VFS, (2) the user cache
    /// <c>user://sdfcache/&lt;map&gt;-&lt;hash16&gt;.psdf</c> (validated by bspHash + paramsHash), (3) if
    /// <see cref="ParticleCvars.SdfGenerate"/> is set, generate ASYNC on a worker and stream chunks back to
    /// the main thread. A stale user cache regenerates exactly once. Safe to call on every map load — it
    /// <see cref="Clear"/>s any prior field first.
    /// </summary>
    public void BuildForMap(string mapName, byte[] bspBytes, CollisionWorld collision, VirtualFileSystem vfs)
    {
        Clear();

        _mapName = NormalizeMapName(mapName);
        _buildStartMs = NowMs();
        _generatedChunks = 0;
        _cachedChunks = 0;
        _buildLogged = false;

        SdfGenParams prms = ResolveParams();
        _skirt = prms.Skirt;

        byte[] bspHash = PsdfFile.ComputeBspHash(bspBytes);
        uint paramsHash = PsdfFile.ComputeParamsHash(prms);

        // (1) Shipped / compiler-baked field inside a pk3: maps/<map>.psdf. Authoritative; no hash gate
        //     (a shipped file is trusted to match its map, like the lightmaps), but still paramsHash-checked
        //     so a generator/format bump rebakes rather than loading an incompatible slab.
        string shipped = $"maps/{_mapName}.psdf";
        if (TryLoadShipped(vfs, shipped, paramsHash, out SdfField? shippedField) && shippedField is not null)
        {
            InstallField(shippedField, generated: false);
            FinishBuild();
            return;
        }

        // (2) User cache. Validate by bspHash (map identity) AND paramsHash (generator/format identity).
        string cacheFile = PsdfFile.CacheFileName(_mapName, bspHash);
        string cacheVpath = $"sdfcache/{cacheFile}";
        if (TryLoadUserCache(cacheVpath, bspHash, paramsHash, out SdfField? cachedField) && cachedField is not null)
        {
            InstallField(cachedField, generated: false);
            FinishBuild();
            return;
        }

        // (3) Generate, if allowed. The cache miss above already covers "stale" (a bspHash/paramsHash
        //     mismatch fails validation -> falls through to here), so regeneration-on-stale is implicit and
        //     happens exactly once per BuildForMap call.
        if (Cv(ParticleCvars.SdfGenerate) == 0f)
        {
            // Generation disabled and nothing on disk: no colliders. Modern particles run collisionless
            // (cl_particles_modern_nosdf 1) or reroute to faithful (0) — HasCoverage() returns false.
            FinishBuild();
            return;
        }

        StartAsyncGeneration(collision, prms, bspHash, paramsHash, cacheVpath);
    }

    /// <summary>
    /// Enable the chunk colliders overlapping the live modern emitters and disable the rest, holding the
    /// resident set under <see cref="ResidentBudget"/> via LRU (§A.5 7-texture cap). Call each frame with the
    /// origins of the modern emitters that spawned this frame (cheap: AABB tests over the occupied chunks).
    /// </summary>
    public void UpdateProximity(IEnumerable<NVec3> emitterOrigins)
    {
        _frame++;

        // Mark chunks near any emitter as wanted this frame.
        var wanted = new HashSet<ChunkNode>();
        foreach (NVec3 o in emitterOrigins)
        {
            foreach (ChunkNode c in _chunks)
            {
                if (NearCell(c, o, EmitterReach))
                {
                    wanted.Add(c);
                    c.LastTouched = _frame;
                }
            }
        }

        // Small map / few chunks: keep everything resident (the budget is never exceeded).
        if (_chunks.Count <= ResidentBudget)
        {
            foreach (ChunkNode c in _chunks)
                SetChunkEnabled(c, true);
            return;
        }

        // Resident LRU: the wanted set plus the most-recently-touched chunks up to the budget.
        var resident = new HashSet<ChunkNode>(wanted);
        if (resident.Count < ResidentBudget)
        {
            foreach (ChunkNode c in _chunks.OrderByDescending(c => c.LastTouched))
            {
                if (resident.Count >= ResidentBudget)
                    break;
                resident.Add(c);
            }
        }

        foreach (ChunkNode c in _chunks)
            SetChunkEnabled(c, resident.Contains(c));
    }

    /// <summary>
    /// True once a RESIDENT chunk collider covers <paramref name="point"/> (Quake space). The router calls
    /// this per modern spawn to gate <see cref="ParticleCvars.ModernNoSdf"/> (§A.6): coverage is only "real"
    /// when the covering chunk's texture is actually enabled this frame, so a late-arriving / non-resident
    /// chunk reports no coverage and the spawn stays collisionless (or reroutes faithful) until it is brought
    /// in by <see cref="UpdateProximity"/>. Tested against the CELL AABB (not the skirted box) so coverage
    /// means "the point is inside the chunk's authoritative volume", not merely inside a neighbor's skirt.
    /// </summary>
    public bool HasCoverage(NVec3 point)
    {
        foreach (ChunkNode c in _chunks)
        {
            if (!c.Enabled)
                continue;
            if (InsideAabb(point, c.CellMins, c.CellMaxs))
                return true;
        }
        return false;
    }

    /// <summary>Remove every collider node and cancel any in-flight generation (map change).</summary>
    public void Clear()
    {
        _genCts?.Cancel();
        _genCts?.Dispose();
        _genCts = null;

        lock (_chunksLock)
        {
            foreach (ChunkNode c in _chunks)
            {
                c.DebugBox?.QueueFree();
                c.Node.QueueFree();
            }
            _chunks.Clear();
        }

        _mapName = string.Empty;
        _generatedChunks = 0;
        _cachedChunks = 0;
        _buildLogged = true; // nothing left to log
    }

    // =============================================================================================
    //  Load paths (§A.2 search order)
    // =============================================================================================

    private static bool TryLoadShipped(VirtualFileSystem vfs, string vpath, uint paramsHash, out SdfField? field)
    {
        field = null;
        if (!vfs.Exists(vpath))
            return false;
        try
        {
            byte[] bytes = vfs.ReadBytes(vpath);
            using var ms = new MemoryStream(bytes, writable: false);
            SdfField f = PsdfFile.Read(ms);
            // A shipped field must match the current generator/format, else its voxel layout / scale is
            // unknown to this build — fall through to regenerate rather than upload a mismatched slab.
            if (f.ParamsHash != paramsHash)
            {
                GD.Print($"[ParticleSDF] shipped {vpath} paramsHash mismatch ({f.ParamsHash:x8} != {paramsHash:x8}); ignoring");
                return false;
            }
            field = f;
            return true;
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[ParticleSDF] failed to read shipped {vpath}: {ex.Message}");
            return false;
        }
    }

    private bool TryLoadUserCache(string cacheVpath, byte[] bspHash, uint paramsHash, out SdfField? field)
    {
        field = null;
        string abs = UserCacheAbsolutePath(cacheVpath);
        if (!File.Exists(abs))
            return false;
        try
        {
            using FileStream fs = File.OpenRead(abs);
            SdfField f = PsdfFile.Read(fs);
            if (!HashesEqual(f.BspHash, bspHash) || f.ParamsHash != paramsHash)
            {
                // Stale (map edited / pk3 swapped, or generator/format bumped). Leave it on disk; the
                // generate path will overwrite it with a fresh slab. This is the "regenerate exactly once"
                // contract — we do NOT loop, we just fall through to (3).
                GD.Print($"[ParticleSDF] stale user cache {cacheVpath} (hash/params mismatch); regenerating");
                return false;
            }
            field = f;
            return true;
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[ParticleSDF] failed to read user cache {cacheVpath}: {ex.Message}");
            return false;
        }
    }

    // =============================================================================================
    //  Async generation (§A.3) — worker pool + main-thread node creation
    // =============================================================================================

    private void StartAsyncGeneration(CollisionWorld collision, SdfGenParams prms, byte[] bspHash,
        uint paramsHash, string cacheVpath)
    {
        _genCts = new CancellationTokenSource();
        CancellationToken token = _genCts.Token;

        // The field we accumulate on the worker for the post-completion cache write. Append is single-threaded
        // (Generate invokes onChunk serially per its contract), so no lock is needed on this list itself; the
        // node creation is marshalled to the main thread.
        var accumulated = new SdfField
        {
            VoxelSize = prms.VoxelSize,
            ChunkSize = prms.ChunkSize,
            Skirt = prms.Skirt,
            Thickness = prms.Thickness,
            GeneratorVersion = PsdfFile.GeneratorVersion,
            BspHash = bspHash,
            ParamsHash = paramsHash,
        };

        _ = Task.Run(() =>
        {
            try
            {
                var gen = new SdfGenerator(prms);
                gen.Generate(collision, chunk =>
                {
                    if (token.IsCancellationRequested)
                        return;
                    // Grid metadata is filled by the generator on the field it owns; we mirror the bits we
                    // need for the cache write off the first chunk's geometry if the generator doesn't expose
                    // them here. The chunk itself carries everything the runtime node needs.
                    accumulated.Chunks.Add(chunk);

                    // Marshal node creation onto the main thread — Godot scene-tree mutation is main-thread only.
                    SdfChunk captured = chunk;
                    Callable.From(() => OnChunkReady(captured, token)).CallDeferred();
                }, token);

                if (token.IsCancellationRequested)
                    return;

                // Persist the freshly generated field to the user cache for the warm path next load.
                WriteUserCache(cacheVpath, accumulated);

                Callable.From(FinishBuild).CallDeferred();
            }
            catch (OperationCanceledException)
            {
                // Map changed mid-generation; nothing to install.
            }
            catch (Exception ex)
            {
                GD.PushWarning($"[ParticleSDF] generation failed for '{_mapName}': {ex.Message}");
                Callable.From(FinishBuild).CallDeferred();
            }
        }, token);
    }

    /// <summary>Main-thread: turn one generated chunk into a collider node. (CallDeferred marshal target.)</summary>
    private void OnChunkReady(SdfChunk chunk, CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return;
        AddChunkNode(chunk, ref _generatedChunks);
    }

    private void WriteUserCache(string cacheVpath, SdfField field)
    {
        try
        {
            string abs = UserCacheAbsolutePath(cacheVpath);
            string? dir = Path.GetDirectoryName(abs);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            // Write to a temp file then move, so a crash mid-write never leaves a truncated cache that would
            // fail validation on the next load and trigger an avoidable regenerate.
            string tmp = abs + ".tmp";
            using (FileStream fs = File.Create(tmp))
                PsdfFile.Write(fs, field);
            if (File.Exists(abs))
                File.Delete(abs);
            File.Move(tmp, abs);
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[ParticleSDF] failed to write user cache {cacheVpath}: {ex.Message}");
        }
    }

    // =============================================================================================
    //  Field installation (synchronous load paths) + node construction
    // =============================================================================================

    private void InstallField(SdfField field, bool generated)
    {
        if (field.Skirt > 0f)
            _skirt = field.Skirt;
        int counter = 0;
        foreach (SdfChunk chunk in field.Chunks)
            AddChunkNode(chunk, ref counter);
        if (generated)
            _generatedChunks += counter;
        else
            _cachedChunks += counter;
    }

    /// <summary>
    /// Build one chunk's collider node (§A.5): a <see cref="GpuParticlesCollisionSdf3D"/> whose box is the
    /// cell AABB grown by <see cref="_skirt"/> on every side (overlapping neighbors intentional), centered on
    /// the cell center, with a Texture3D built from the chunk's distance slab. Adds it as a child; debug-mode
    /// also adds a wireframe box matching the cell AABB.
    /// </summary>
    private void AddChunkNode(SdfChunk chunk, ref int counter)
    {
        if (chunk.Res <= 0 || chunk.Distances.Length < chunk.Res * chunk.Res * chunk.Res)
            return;

        // Cell AABB in Quake space. CellMins is the chunk's cell mins (no skirt); the cell edge is
        // res * voxelSize (the slab covers exactly the cell, the skirt is geometry-gather only and is added
        // to the COLLIDER box, not to the sampled volume — see §A.3/§A.5). VoxelSize isn't carried per-chunk,
        // so it's re-derived from the active gen params (_voxelSize) resolved for this build.
        float chunkSize = chunk.Res * _voxelSize;
        NVec3 cellMins = chunk.CellMins;
        NVec3 cellMaxs = cellMins + new NVec3(chunkSize, chunkSize, chunkSize);
        NVec3 cellCenterQ = (cellMins + cellMaxs) * 0.5f;

        // Godot box: cell + 2*skirt total edge (skirt on each side). Size is full edge length.
        float boxEdge = chunkSize + 2f * _skirt;

        var node = new GpuParticlesCollisionSdf3D
        {
            Name = $"sdf_{chunk.Cx}_{chunk.Cy}_{chunk.Cz}",
            Size = new Vector3(boxEdge, boxEdge, boxEdge),
            // Box is centered on its node origin (the §A.4 sampling-convention assumption), so place the node
            // at the cell center in Godot space.
            GlobalPosition = Coords.ToGodot(cellCenterQ),
            Texture = BuildSdfTexture(chunk),
            Visible = false, // start disabled; UpdateProximity / small-map path enables.
        };

        var record = new ChunkNode
        {
            Node = node,
            CellMins = cellMins,
            CellMaxs = cellMaxs,
            LastTouched = 0,
            Enabled = false,
        };

        AddChild(node);

        if (Cv(ParticleCvars.SdfDebug) != 0f)
            record.DebugBox = AddDebugBox(cellCenterQ, chunkSize);

        lock (_chunksLock)
            _chunks.Add(record);

        // Small maps: AddChunkNode happens before the first UpdateProximity, so enable up to the budget here
        // (the proximity pass refines it). This keeps coverage available the instant a chunk lands.
        if (_chunks.Count <= ResidentBudget)
            SetChunkEnabled(record, true);

        counter++;
    }

    /// <summary>
    /// Encode a chunk's signed-distance slab into a single-channel float <see cref="ImageTexture3D"/> for the
    /// collider (§A.4 encoding contract; see <see cref="SdfTextureFormat"/> for the format + the validation
    /// TODO). The slab is stored Quake-axis X-fastest/Y/Z; the Godot collider samples in box-local axes, so
    /// the texel at Godot-box local (gx,gy,gz) must read the Quake voxel whose axes map onto that box corner.
    ///
    /// <para>Axis map (Coords: godot = (q.X, q.Z, -q.Y)): the box's local +X is Quake +X, +Y is Quake +Z, +Z
    /// is Quake -Y. So for output index (gx,gy,gz) in [0,res):
    ///   qx = gx ; qy = res-1-gz ; qz = gy ,
    /// and the source linear index is qx + res*(qy + res*qz). This re-indexes the slab once at upload; the
    /// distance VALUES are length-preserving under the axis swap so they pass through unchanged.</para>
    ///
    /// TODO(§A.4): the box-local sampling direction (whether Godot's collider treats the texture's first axis
    /// as +X or −X of the box, and its handedness) is the unverified bit — confirm against an editor bake and
    /// flip the qx/qy/qz derivation here if the dumped texture disagrees. The format + per-voxel value scale
    /// are the load-bearing parts; the axis order is a permutation that this comment pins for that check.
    /// </summary>
    private static ImageTexture3D BuildSdfTexture(SdfChunk chunk)
    {
        int res = chunk.Res;
        float[] src = chunk.Distances;

        // One Image per Z-slice (Godot's Texture3D ctor consumes an array of 2D layer images).
        var layers = new Godot.Collections.Array<Image>();
        int bytesPerTexel = sizeof(float);
        for (int gz = 0; gz < res; gz++)
        {
            var sliceBytes = new byte[res * res * bytesPerTexel];
            int o = 0;
            for (int gy = 0; gy < res; gy++)
            for (int gx = 0; gx < res; gx++)
            {
                // Box-local (gx,gy,gz) -> Quake voxel (qx,qy,qz) per the axis map above.
                int qx = gx;
                int qy = res - 1 - gz;
                int qz = gy;
                int srcIndex = qx + res * (qy + res * qz);
                float d = (srcIndex >= 0 && srcIndex < src.Length) ? src[srcIndex] : 0f;
                BitConverter.GetBytes(d).CopyTo(sliceBytes, o);
                o += bytesPerTexel;
            }
            layers.Add(Image.CreateFromData(res, res, false, SdfTextureFormat, sliceBytes));
        }

        var tex = new ImageTexture3D();
        tex.Create(SdfTextureFormat, res, res, res, useMipmaps: false, layers);
        return tex;
    }

    // =============================================================================================
    //  Resident-set helpers
    // =============================================================================================

    private static void SetChunkEnabled(ChunkNode c, bool enabled)
    {
        if (c.Enabled == enabled)
            return;
        c.Enabled = enabled;
        c.Node.Visible = enabled;
        // A disabled collider must not feed the particle system this frame; toggling Visible removes it from
        // the active collider set (Godot skips invisible collision volumes). Keeping the Texture3D resident on
        // the node is fine — VRAM is bounded by ResidentBudget because we never build more than the occupied
        // chunks and only the enabled ones bind into a system's 7-slot table.
        if (c.DebugBox is not null)
            c.DebugBox.Visible = enabled;
    }

    /// <summary>True if emitter origin <paramref name="o"/> (Quake) is within <paramref name="reach"/> of the
    /// chunk's CELL AABB (skirt excluded — proximity is about the authoritative volume).</summary>
    private static bool NearCell(ChunkNode c, NVec3 o, float reach)
    {
        float dx = MathF.Max(0f, MathF.Max(c.CellMins.X - o.X, o.X - c.CellMaxs.X));
        float dy = MathF.Max(0f, MathF.Max(c.CellMins.Y - o.Y, o.Y - c.CellMaxs.Y));
        float dz = MathF.Max(0f, MathF.Max(c.CellMins.Z - o.Z, o.Z - c.CellMaxs.Z));
        return dx <= reach && dy <= reach && dz <= reach;
    }

    private static bool InsideAabb(NVec3 p, NVec3 mins, NVec3 maxs) =>
        p.X >= mins.X && p.X <= maxs.X &&
        p.Y >= mins.Y && p.Y <= maxs.Y &&
        p.Z >= mins.Z && p.Z <= maxs.Z;

    // =============================================================================================
    //  Debug visualization (§A.6 cl_particles_sdf_debug)
    // =============================================================================================

    /// <summary>
    /// Add a translucent box outlining the chunk's CELL AABB (no skirt) so the chunk grid is visible when
    /// <c>cl_particles_sdf_debug</c> is set (§A.6). Godot's <see cref="StandardMaterial3D"/> has no per-material
    /// wireframe flag, so this is a faint unshaded green alpha shell (low alpha + no depth write) — enough to
    /// read each occupied chunk's extent without occluding the world. Positioned at the cell center in Godot
    /// space; starts hidden and follows the chunk's enabled state.
    /// </summary>
    private MeshInstance3D AddDebugBox(NVec3 cellCenterQ, float cellEdge)
    {
        var box = new BoxMesh { Size = new Vector3(cellEdge, cellEdge, cellEdge) };
        box.Material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            NoDepthTest = false,
            AlbedoColor = new Color(0.2f, 0.9f, 0.4f, 0.12f),
        };

        var mi = new MeshInstance3D
        {
            Name = "sdf_debug_box",
            Mesh = box,
            GlobalPosition = Coords.ToGodot(cellCenterQ),
            Visible = false,
        };
        AddChild(mi);
        return mi;
    }

    // =============================================================================================
    //  Build completion / logging
    // =============================================================================================

    private void FinishBuild()
    {
        if (_buildLogged)
            return;
        _buildLogged = true;
        int total = _chunks.Count;
        long ms = NowMs() - _buildStartMs;
        // §A.3 headless regression-guard line.
        GD.Print($"[ParticleSDF] {_mapName}: {total} chunks " +
                 $"({_generatedChunks} generated, {_cachedChunks} cached, {ms} ms)");
    }

    // =============================================================================================
    //  Params / path / hash helpers
    // =============================================================================================

    private SdfGenParams ResolveParams()
    {
        var p = new SdfGenParams();
        float chunk = Cv(ParticleCvars.SdfChunk);
        float voxel = Cv(ParticleCvars.SdfVoxel);
        if (chunk > 0f)
            p.ChunkSize = chunk;
        if (voxel > 0f)
            p.VoxelSize = voxel;
        _voxelSize = p.VoxelSize;
        return p;
    }

    /// <summary>Reduce a map name ("maps/foo.bsp", "maps/foo", "foo") to the bare basename used in the cache
    /// file name and the shipped <c>maps/&lt;map&gt;.psdf</c> path.</summary>
    private static string NormalizeMapName(string mapName)
    {
        string s = mapName.Replace('\\', '/');
        int slash = s.LastIndexOf('/');
        if (slash >= 0)
            s = s[(slash + 1)..];
        int dot = s.LastIndexOf('.');
        if (dot >= 0)
            s = s[..dot];
        return s;
    }

    /// <summary>Resolve <c>sdfcache/&lt;file&gt;</c> to an absolute OS path under <see cref="UserPaths.BaseDir"/>
    /// (<c>~/XonData/sdfcache/…</c>). The cache is a user-side artifact (regenerable), so it lives in the user
    /// data dir, not the VFS pk3 search path.</summary>
    private static string UserCacheAbsolutePath(string cacheVpath)
        => UserPaths.Resolve(cacheVpath);

    private static bool HashesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i])
                return false;
        return true;
    }

    /// <summary>Wall-clock milliseconds for the "T ms" build-time stat. Uses Godot's engine clock (not the
    /// sim clock) so the figure reflects real load latency including the async generation gap.</summary>
    private static long NowMs() => (long)Time.GetTicksMsec();
}
