using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Game;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>
/// Model gibs — the real limb/chunk meshes a player throws when gibbed, instead of a generic particle
/// burst. The C# successor to CSQC's gibs.qc (the <c>net_gibsplash</c> handler + TossGib / Gib_Draw):
/// a gib splash of "type 1" tosses an eye, a bloody skull, then per-amount a spray of arms, chests, legs and
/// fast-flying chunks, each a bouncing MOVETYPE_BOUNCE body. Physics (gravity + ground bounce + tumble) are
/// integrated client-side, exactly as the QC gib is a pure client drawable advanced by
/// Movetype_Physics_MatchTicrate.
///
/// (zero-hitch 2026-08-03) STRUCT-ARRAY SIM + MULTIMESH RENDER — the ShellCasings recipe. The previous
/// shape was one <c>GibBody : Node3D</c> per gib: at the 64 cap that is 64 scene nodes each with its own
/// <c>_PhysicsProcess</c> native callback, 64 render objects/draws, and a slaughter's spawn/QueueFree churn
/// burst (part of the draw-count spikes + unscoped/post-process hitch classes in the release captures).
/// Now the ballistics live in ONE struct pool advanced by ONE <c>_PhysicsProcess</c>, rendered as one
/// fixed-capacity <see cref="MultiMesh"/> batch PER DISTINCT GIB MODEL (~10 total: the limb set, eye,
/// skull, chunk, the raptor shellfrag) — buffers sized once and never resized, zero per-gib nodes, zero
/// steady-state allocation.
///
/// One deliberate visual deviation: end-of-life is the classic idTech CORPSE SINK (the gib settles into the
/// floor over its fade window) instead of the old per-instance alpha fade — per-instance transparency does
/// not compose with the shared skin ShaderMaterials under MultiMesh, and sinking bodies are period-correct.
/// </summary>
public sealed partial class ModelGibs : Node3D
{
    /// <summary>Gib lifetime seconds (DP cl_gibs_lifetime default 14, trimmed so they don't pile up).</summary>
    [Export] public float GibLifetime { get; set; } = 8f;

    /// <summary>Hard cap on live gibs (DP cl_gibs_maxcount). Also each model batch's fixed capacity.</summary>
    [Export] public int MaxGibs { get; set; } = 64;

    /// <summary>Host model loader (e.g. <c>AssetLoader.LoadModel</c>); null =&gt; generated placeholder chunks.</summary>
    public Func<string, Node3D?>? ModelLoader { get; set; }

    // The MD3 limb models a normal (type 0x01) gib splash tosses (gibs.qc). The fast chunk.mdl is a Quake1 MDL.
    private static readonly string[] LimbModels =
    {
        "models/gibs/arm.md3",
        "models/gibs/arm.md3",
        "models/gibs/chest.md3",
        "models/gibs/smallchest.md3",
        "models/gibs/leg1.md3",
        "models/gibs/leg2.md3",
    };

    /// <summary>
    /// Every distinct gib model the client can ever spawn — the limb set plus the three
    /// <see cref="Splash"/> always tosses. Map-independent and fixed, so it is also the work-list the
    /// MENU-time asset warm uses (<c>MenuAssetWarmer</c>) and the source
    /// <see cref="BuildWarmupInstances"/> iterates, which is what keeps the warmed set and the spawnable
    /// set from drifting apart. Deduplicated (arm.md3 appears twice in <see cref="LimbModels"/>, once per arm).
    /// </summary>
    public static IReadOnlyList<string> AllModelPaths { get; } = BuildAllModelPaths();

    private static string[] BuildAllModelPaths()
    {
        var all = new List<string>(LimbModels.Length + 3);
        void Add(string p) { if (!all.Contains(p, StringComparer.OrdinalIgnoreCase)) all.Add(p); }
        foreach (string m in LimbModels) Add(m);
        Add("models/gibs/eye.md3");
        Add("models/gibs/bloodyskull.md3");
        Add("models/gibs/chunk.mdl");
        return all.ToArray();
    }

    private const float Gravity = 800f;       // sv_gravity; gibs use gravity 1 (full)
    private const float BounceFactor = 0.4f;  // gib bouncefactor-ish
    private const float SinkDepth = 16f;      // Quake units the gib settles into the floor over its fade window

    /// <summary>One simulated gib (pure data — no node, no allocation).</summary>
    private struct Gib
    {
        public bool Active;
        public int BatchIdx;         // which model batch renders it
        public uint Seq;             // spawn order (oldest = lowest; the cap evicts by this)
        public NVec3 PosQuake;
        public NVec3 Vel;            // Quake space
        public Basis Rot;            // accumulated tumble (Godot space)
        public Vector3 AngularVel;   // rad/s per local axis (QC avelocity semantics preserved below)
        public float Lifetime, FloorZ, Age;
        public float GravityScale;   // QC .gravity (raptor shellfrags: 0.15)
        public float AngularJitter;  // QC RaptorCBShellfragDraw: avelocity += randomvec()*15 per draw
        public float FadeDuration;   // the sink window before death (was the alpha-fade window)
        public bool DestroyOnTouch;  // chunk.mdl-style: splat on first ground contact
        public bool Resting;
    }

    private Gib[] _pool = Array.Empty<Gib>();
    private uint _spawnSeq;

    /// <summary>One MultiMesh batch per distinct gib model (mesh/material resolved once via SharedMeshCache).</summary>
    private sealed class Batch
    {
        public MultiMeshInstance3D Node = null!;
        public MultiMesh Mesh = null!;
        public Transform3D BaseXform;
        public int Visible;
    }

    private readonly List<Batch> _batches = new();
    private readonly Dictionary<string, int> _batchByModel = new(StringComparer.OrdinalIgnoreCase);
    private int[] _packCounts = Array.Empty<int>();   // per-batch pack cursor, reused each frame

    /// <summary>
    /// Spawn a full gib splash at <paramref name="origin"/> (Quake space) with base <paramref name="velocity"/>,
    /// scaled by <paramref name="amount"/> (the QC gibbage multiplier, ~1..15). Bounces off the ground plane at
    /// <paramref name="floorZ"/>. This is the type 0x01 ("full") splash; lesser types fall out as a few chunks.
    /// </summary>
    public void Splash(NVec3 origin, NVec3 velocity, float amount = 4f, float floorZ = float.NegativeInfinity)
    {
        amount = Math.Clamp(amount, 1f, 16f);

        // Always toss an eye and a bloody skull (QC tosses these unconditionally with prandom gates).
        Toss("models/gibs/eye.md3", origin, velocity, RandomVec() * 150f, floorZ, destroyOnTouch: false);
        Toss("models/gibs/bloodyskull.md3", origin + RandomVec() * 16f, velocity, RandomVec() * 100f, floorZ, false);

        // Per the QC loop: for c in 0..amount, gate each limb on (amount-c) so early iterations spawn more.
        for (int c = 0; c < amount; c++)
        {
            float randomValue = amount - c;
            foreach (string mdl in LimbModels)
            {
                if (GD.Randf() < randomValue)
                {
                    NVec3 jitter = RandomVec() * 16f + new NVec3(0f, 0f, 4f);
                    Toss(mdl, origin + jitter, velocity, RandomVec() * (GD.Randf() * 120f + 85f), floorZ, false);
                }
            }
            // Fast chunks that splat on impact (the real Quake1 chunk.mdl).
            for (int k = 0; k < 4; k++)
                if (GD.Randf() < randomValue)
                    Toss("models/gibs/chunk.mdl", origin + RandomVec() * 16f, velocity, RandomVec() * 450f, floorZ, destroyOnTouch: true);
        }
    }

    /// <summary>Toss one gib of the given model. Public so callers can drop a single gib (e.g. a chunk).
    /// Returns this node (legacy signature — gibs no longer have per-spawn nodes).</summary>
    public Node3D Toss(string modelPath, NVec3 origin, NVec3 baseVel, NVec3 randVel, float floorZ, bool destroyOnTouch)
    {
        // QC: velocity = vconst*velocity_scale + vrand*velocity_random + up. We fold the cvars into sane
        // constants (scale 1, random 1, up 100) so it reads like the default config.
        NVec3 vel = baseVel + randVel + new NVec3(0f, 0f, 100f);
        ref Gib g = ref AllocSlot(modelPath);
        g.PosQuake = origin;
        g.Vel = vel;
        g.Lifetime = GibLifetime * (1f + GD.Randf() * 0.15f);
        g.FloorZ = floorZ;
        g.DestroyOnTouch = destroyOnTouch;
        g.GravityScale = 1f;
        g.AngularJitter = 0f;
        g.FadeDuration = 1f;   // QC gibs fade over their last second — now the sink window
        g.AngularVel = RandomVecG() * (vel.Length() * 0.02f);
        return this;
    }

    /// <summary>
    /// Toss the raptor cluster-bomb shell-fragment gibs (QC <c>RaptorCBShellfragToss</c> /
    /// <c>RaptorCBShellfragDraw</c>, raptor_weapons.qc:244-284, dispatched from the DEATH_VH_RAPT_FRAGMENT burst
    /// FX in damageeffects.qc:353-360). Three bouncing <c>clusterbomb_fragment.md3</c> drawables thrown outward
    /// from the burst point: gravity 0.15, an avelocity = ±|velocity| seed plus a per-frame ±15 tumble jitter,
    /// a 3s lifetime that settles over its final second (QC cnt = time+2, nextthink = time+3). Pure cosmetic
    /// debris — <paramref name="origin"/>/<paramref name="bombVel"/> are Quake space (the bursting bomb's pose).
    /// </summary>
    public void TossShellfrags(NVec3 origin, NVec3 bombVel)
    {
        for (int i = 1; i < 4; i++)
        {
            // QC damageeffects.qc: vel = normalize(w_org - (w_org + force_dir*16)) + randomvec()*128. We lack the
            // surface backoff (force_dir) headless, so seed a small outward/upward bias + the dominant random spray.
            NVec3 vel = new NVec3(0f, 0f, 0.4f) + RandomVec() * 128f;
            ref Gib g = ref AllocSlot("models/vehicles/clusterbomb_fragment.md3");
            g.PosQuake = origin;
            g.Vel = vel;
            g.GravityScale = 0.15f;                     // QC sfrag.gravity = 0.15
            g.Lifetime = 3f;                            // QC sfrag.nextthink = time + 3
            g.FadeDuration = 1f;                        // QC cnt = time + 2 → settles over the final second
            g.FloorZ = float.NegativeInfinity;
            g.DestroyOnTouch = false;
            // QC: avelocity = prandomvec() * vlen(velocity); plus a +15/draw jitter (AngularJitter).
            g.AngularVel = RandomVecG() * vel.Length();
            g.AngularJitter = 15f;
        }
    }

    /// <summary>Claim a pool slot for one gib of <paramref name="modelPath"/> (evicting the oldest when
    /// full — DP cl_gibs_maxcount) and stamp the shared fields; the caller fills the rest.</summary>
    private ref Gib AllocSlot(string modelPath)
    {
        if (_pool.Length == 0)
            _pool = new Gib[Math.Max(1, MaxGibs)];

        int slot = -1;
        uint oldest = uint.MaxValue; int oldestIdx = 0;
        for (int i = 0; i < _pool.Length; i++)
        {
            if (!_pool[i].Active) { slot = i; break; }
            if (_pool[i].Seq < oldest) { oldest = _pool[i].Seq; oldestIdx = i; }
        }
        if (slot < 0) slot = oldestIdx;

        ref Gib g = ref _pool[slot];
        g.Active = true;
        g.Seq = ++_spawnSeq;
        g.BatchIdx = EnsureBatch(modelPath);
        g.Rot = Basis.Identity;
        g.Age = 0f;
        g.Resting = false;
        return ref g;
    }

    /// <summary>ONE physics callback for every live gib (was one per gib). Advances the pool, then packs the
    /// per-model MultiMesh instance transforms.</summary>
    public override void _PhysicsProcess(double delta)
    {
        // #30 slowmo/pause: gib tosses are Base CSQC (cl.time-driven) — scale like the casings so gibs
        // hang frozen at slowmo 0 instead of settling on wall clock.
        float dt = VortexArena.Game.Client.ClientRenderTime.ScaleDelta((float)delta);
        if (dt <= 0f)
            return; // paused — hold everything in place

        bool any = false;
        for (int i = 0; i < _pool.Length; i++)
            if (_pool[i].Active) { any = true; break; }
        if (!any)
        {
            for (int b = 0; b < _batches.Count; b++)
                if (_batches[b].Visible != 0) { _batches[b].Mesh.VisibleInstanceCount = 0; _batches[b].Visible = 0; }
            return;
        }

        using var _scope = VortexArena.Game.Client.FrameProfiler.Scope("gibs"); // house rule: per-frame node → scoped

        if (_packCounts.Length < _batches.Count)
            _packCounts = new int[_batches.Count];
        Array.Clear(_packCounts, 0, _packCounts.Length);

        for (int i = 0; i < _pool.Length; i++)
        {
            ref Gib g = ref _pool[i];
            if (!g.Active)
                continue;

            g.Age += dt;
            if (g.Age >= g.Lifetime) { g.Active = false; continue; }

            if (!g.Resting)
            {
                g.Vel.Z -= Gravity * g.GravityScale * dt;
                // QC RaptorCBShellfragDraw: avelocity += randomvec() * 15 each draw — a continuous tumble jitter.
                if (g.AngularJitter != 0f)
                    g.AngularVel += RandomVecG() * (g.AngularJitter * dt);
                NVec3 posQ = g.PosQuake + g.Vel * dt;

                if (!float.IsNegativeInfinity(g.FloorZ) && posQ.Z <= g.FloorZ && g.Vel.Z < 0f)
                {
                    if (g.DestroyOnTouch)
                    {
                        // chunk.mdl-style: splat on first ground contact.
                        g.Active = false;
                        continue;
                    }
                    posQ.Z = g.FloorZ;
                    g.Vel.Z = -g.Vel.Z * BounceFactor;
                    g.Vel.X *= 0.6f;
                    g.Vel.Y *= 0.6f;
                    g.AngularVel *= 0.5f;
                    if (g.Vel.Length() < 25f)
                    {
                        g.Resting = true;
                        g.Vel = default;
                    }
                }
                g.PosQuake = posQ;
                // Tumble — the exact axis/component pairing of the old RotateObjectLocal calls (Up gets the
                // avelocity Z component, Right gets X; the QC only ever spins these two).
                g.Rot *= new Basis(Vector3.Up, g.AngularVel.Z * dt);
                g.Rot *= new Basis(Vector3.Right, g.AngularVel.X * dt);
            }

            Batch b = _batches[g.BatchIdx];
            int idx = _packCounts[g.BatchIdx]++;
            if (idx >= MaxGibs)
                continue;
            // End-of-life corpse sink (see the class note): settle SinkDepth into the floor over FadeDuration.
            float remaining = g.Lifetime - g.Age;
            float sink = remaining < g.FadeDuration
                ? (1f - Math.Clamp(remaining / Math.Max(0.001f, g.FadeDuration), 0f, 1f)) * SinkDepth : 0f;
            Vector3 pos = Coords.ToGodot(g.PosQuake) + new Vector3(0f, -sink, 0f);
            b.Mesh.SetInstanceTransform(idx, new Transform3D(g.Rot, pos) * b.BaseXform);
        }

        for (int b = 0; b < _batches.Count; b++)
        {
            int want = Math.Min(_packCounts[b], MaxGibs);
            if (_batches[b].Visible != want) { _batches[b].Mesh.VisibleInstanceCount = want; _batches[b].Visible = want; }
        }
    }

    // ------------------------------------------------------------------------------------------------
    //  Batches + meshes
    // ------------------------------------------------------------------------------------------------

    private int EnsureBatch(string modelPath)
    {
        if (_batchByModel.TryGetValue(modelPath, out int idx))
            return idx;
        (Mesh mesh, Material? mat, Transform3D baseXf) = ResolveMesh(modelPath);
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = mesh,
            InstanceCount = Math.Max(1, MaxGibs),   // fixed forever — never resized (GPU realloc)
            VisibleInstanceCount = 0,
        };
        var node = new MultiMeshInstance3D
        {
            Name = "gibs_" + System.IO.Path.GetFileNameWithoutExtension(modelPath),
            Multimesh = mm,
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            // Instances scatter across the map inside one batch — never cull the lot on a stale AABB.
            ExtraCullMargin = 16384f,
        };
        AddChild(node);
        _batches.Add(new Batch { Node = node, Mesh = mm, BaseXform = baseXf });
        idx = _batches.Count - 1;
        _batchByModel[modelPath] = idx;
        return idx;
    }

    /// <summary>The gib mesh + material + the source node's own transform (folded into every instance).
    /// Prefers the real model via <see cref="SharedMeshCache"/> ([crash fix 2026-07-26]: one built tree per
    /// gib model EVER); generated chunk fallback when the loader is unwired or the parse fails.</summary>
    private (Mesh, Material?, Transform3D) ResolveMesh(string modelPath)
    {
        if (ModelLoader is not null && SharedMeshCache.Instantiate(modelPath, () => ModelLoader(modelPath)) is { } mi)
        {
            Material? mat = mi.MaterialOverride;
            if (mat is null && mi.GetSurfaceOverrideMaterialCount() > 0)
                mat = mi.GetSurfaceOverrideMaterial(0);
            Mesh m = mi.Mesh;
            Transform3D xf = mi.Transform;
            mi.QueueFree();   // only needed its resolved (mesh, material, transform)
            return (m, mat, xf);
        }
        _chunkMesh ??= new BoxMesh
        {
            Size = new Vector3(4f, 4f, 4f),
            Material = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.45f, 0.06f, 0.05f),
                Roughness = 0.9f,
            },
        };
        return (_chunkMesh, null, Transform3D.Identity);
    }

    private static BoxMesh? _chunkMesh;

    /// <summary>
    /// One hidden instance per DISTINCT gib model for the offscreen GPU pipeline warm pass — as MULTIMESH
    /// instances, because instanced rendering is its own vertex format and therefore its own pipeline (warming
    /// a plain MeshInstance3D would leave the live batches' pipelines cold). Same meshes/materials the live
    /// batches resolve; <see cref="AllModelPaths"/> keeps this set and the spawnable set from drifting apart.
    /// The warm pass parents, renders, and frees them.
    /// </summary>
    public List<Node3D> BuildWarmupInstances()
    {
        var list = new List<Node3D>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string mdl in AllModelPaths)
        {
            if (!seen.Add(mdl))
                continue;
            (Mesh mesh, Material? mat, Transform3D baseXf) = ResolveMesh(mdl);
            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = mesh,
                InstanceCount = 1,
                VisibleInstanceCount = 1,
            };
            mm.SetInstanceTransform(0, baseXf);
            list.Add(new MultiMeshInstance3D
            {
                Name = "gib_warm_" + System.IO.Path.GetFileNameWithoutExtension(mdl),
                Multimesh = mm,
                MaterialOverride = mat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            });
        }
        return list;
    }

    private static NVec3 RandomVec()
        => new((float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0));

    private static Vector3 RandomVecG()
        => new((float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0));
}
