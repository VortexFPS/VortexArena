using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>
/// Model gibs — the real limb/chunk meshes a player throws when gibbed, instead of a generic particle
/// burst. The C# successor to CSQC's gibs.qc (the <c>net_gibsplash</c> handler + TossGib / Gib_Draw):
/// a gib splash of "type 1" tosses an eye, a bloody skull, then per-amount a spray of arms, chests, legs and
/// fast-flying chunks, each a bouncing MOVETYPE_BOUNCE body. Model gibs and Raptor fragments advance
/// fixed client tics against the map collision world and draw the remaining fraction separately.
///
/// (zero-hitch 2026-08-03) STRUCT-ARRAY SIM + MULTIMESH RENDER — the ShellCasings recipe. The previous
/// shape was one <c>GibBody : Node3D</c> per gib: at the 64 cap that is 64 scene nodes each with its own
/// <c>_PhysicsProcess</c> native callback, 64 render objects/draws, and a slaughter's spawn/QueueFree churn
/// burst (part of the draw-count spikes + unscoped/post-process hitch classes in the release captures).
/// The ballistics advance in one render callback and draw through one
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
    /// <summary>Lifetime in seconds when the client has no cl_gibs_lifetime setting.</summary>
    [Export] public float GibLifetime { get; set; } = 2.5f;

    /// <summary>Fallback live gib limit when no client cvar is available.</summary>
    [Export] public int MaxGibs { get; set; } = 100;

    internal ICvarService? Cvars { get; init; }
    internal DebrisTrace? WorldTrace { get; set; }

    /// <summary>Host model loader (e.g. <c>AssetLoader.LoadModel</c>); null =&gt; generated placeholder chunks.</summary>
    public Func<string, Node3D?>? ModelLoader { get; set; }
    internal Func<string, int, Node3D?>? SkinnedModelLoader { get; set; }

    // The MD3 limb models a normal (type 0x01) gib splash tosses (gibs.qc). The fast chunk.mdl is a Quake1 MDL.
    private static readonly (string Path, float Height, float BaseSpeed)[] LimbModels =
    {
        ("models/gibs/arm.md3", 8f, 90f),
        ("models/gibs/arm.md3", 8f, 90f),
        ("models/gibs/chest.md3", 0f, 80f),
        ("models/gibs/smallchest.md3", 0f, 80f),
        ("models/gibs/leg1.md3", -5f, 85f),
        ("models/gibs/leg2.md3", -5f, 85f),
    };
    private static readonly string[] RobotFragments = Enumerable.Range(1, 8)
        .Select(i => $"models/gibs/robo{i}.md3").ToArray();

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
        foreach (var limb in LimbModels) Add(limb.Path);
        Add("models/gibs/eye.md3");
        Add("models/gibs/bloodyskull.md3");
        Add("models/gibs/chunk.mdl");
        Add("models/gibs/robo.md3");
        foreach (string m in RobotFragments) Add(m);
        return all.ToArray();
    }

    private const float BounceFactor = 0.5f;  // QC MOVETYPE_BOUNCE default
    // Maximum gibbage on the stock wire, multiplied by EKG.
    internal const float MaxSplashAmount = 5f * 255f / 16f;
    private static readonly NVec3 GibHalfSize = new(8f);
    private const float SinkDepth = 16f;      // Quake units the gib settles into the floor over its fade window

    /// <summary>One simulated gib (pure data — no node, no allocation).</summary>
    private struct Gib
    {
        public int BatchIdx;         // which model batch renders it
        public DebrisMotion Motion;
        public double Age;
        public float Lifetime, FloorZ, GravityScale;
        public bool DestroyOnTouch;
        public uint FragmentRandom; // Per-fragment jitter state, advanced only on committed tics.
    }

    private readonly RubblePool<Gib> _pool = new();
    private readonly RubblePool<Gib> _shellfrags = new();
    private const int ShellfragLimit = 64;

    /// <summary>One MultiMesh batch per distinct gib model (mesh/material resolved once via SharedMeshCache).</summary>
    private sealed class Batch
    {
        public MultiMeshInstance3D Node = null!;
        public MultiMesh Mesh = null!;
        public Transform3D BaseXform;
        public int Visible;
    }

    private readonly List<Batch> _batches = new();
    private readonly Dictionary<(string Model, int Skin), int> _batchByModel = new();
    private int[] _packCounts = Array.Empty<int>();   // per-batch pack cursor, reused each frame

    /// <summary>
    /// Spawn a full gib splash at <paramref name="origin"/> (Quake space) with base <paramref name="velocity"/>,
    /// scaled by <paramref name="amount"/> after the client's gib settings. The ground plane at
    /// <paramref name="floorZ"/> is available when there is no map tracer.
    /// </summary>
    public void Splash(NVec3 origin, NVec3 velocity, float amount = 4f, float floorZ = float.NegativeInfinity,
        ModelSpecies species = ModelSpecies.Human)
    {
        if (!(amount > 0f) || !float.IsFinite(amount))
            return;
        // The stock wire carries at most 255/16 units; EKG multiplies the received amount by five.
        amount = Math.Min(amount, MaxSplashAmount);
        if (GibRandom() < amount)
            Toss("models/gibs/eye.md3", origin, velocity, RandomGibVector() * 150f, floorZ, false, origin, species);
        if (GibRandom() < amount)
            Toss("models/gibs/bloodyskull.md3", origin + RandomGibVector() * 16f, velocity, RandomGibVector() * 100f, floorZ, false, origin, species);

        // Per the QC loop: for c in 0..amount, gate each limb on (amount-c) so early iterations spawn more.
        for (int c = 0; c < amount; c++)
        {
            float randomValue = amount - c;
            foreach (var limb in LimbModels)
            {
                if (GibRandom() < randomValue)
                {
                    NVec3 jitter = RandomGibVector() * 16f + new NVec3(0f, 0f, limb.Height);
                    Toss(limb.Path, origin + jitter, velocity, RandomGibVector() * (GibRandom() * 120f + limb.BaseSpeed), floorZ, false, origin, species);
                }
            }
            // Fast chunks that splat on impact (the real Quake1 chunk.mdl).
            for (int k = 0; k < 4; k++)
                if (GibRandom() < randomValue)
                    Toss("models/gibs/chunk.mdl", origin + RandomGibVector() * 16f, velocity, RandomGibVector() * 450f, floorZ, true, origin, species);
        }
    }

    public void ImpactChunk(NVec3 origin, NVec3 velocity, float amount, ModelSpecies species)
    {
        // Type 3 tests probability once. EKG can make the toss certain, but never adds chunks per event.
        if (GibRandom() < amount)
            Toss("models/gibs/chunk.mdl", origin, velocity, RandomGibVector() * (GibRandom() * 30f + 20f),
                float.NegativeInfinity, true, origin, species);
    }

    /// <summary>Toss one gib of the given model. Public so callers can drop a single gib (e.g. a chunk).
    /// Returns this node (legacy signature — gibs no longer have per-spawn nodes).</summary>
    public Node3D Toss(string modelPath, NVec3 origin, NVec3 baseVel, NVec3 randVel, float floorZ, bool destroyOnTouch)
        => Toss(modelPath, origin, baseVel, randVel, floorZ, destroyOnTouch, origin);

    private Node3D Toss(string modelPath, NVec3 origin, NVec3 baseVel, NVec3 randVel, float floorZ,
        bool destroyOnTouch, NVec3 safeOrigin, ModelSpecies species = ModelSpecies.Human)
    {
        int limit = RubblePool<Gib>.ReadLimit(Cvars, "cl_gibs_maxcount", MaxGibs);
        if (!_pool.TrySpawn(limit, out int slot))
            return this;
        NVec3 vel = baseVel * Cvar("cl_gibs_velocity_scale", 0.5f)
            + randVel * Cvar("cl_gibs_velocity_random", 2f)
            + NVec3.UnitZ * Cvar("cl_gibs_velocity_up", 1f);
        if (origin != safeOrigin && WorldTrace is not null)
            origin = WorldTrace(safeOrigin, origin, GibHalfSize).EndPos;

        int skin = (int)species;
        if ((species is ModelSpecies.RobotRusty or ModelSpecies.RobotShiny)
            || (species == ModelSpecies.RobotSolid && modelPath == "models/gibs/chunk.mdl"))
        {
            modelPath = modelPath == "models/gibs/bloodyskull.md3"
                ? "models/gibs/robo.md3"
                : RobotFragments[GD.Randi() % (uint)RobotFragments.Length];
            skin = species == ModelSpecies.RobotShiny ? 1 : 0;
        }

        _pool[slot] = new Gib
        {
            BatchIdx = EnsureBatch(modelPath, skin),
            Motion = new DebrisMotion(origin, vel, RandomGibVector() * vel.Length() * Cvar("cl_gibs_avelocity_scale", 1f)),
            Lifetime = Cvar("cl_gibs_lifetime", GibLifetime) * (1f + GibRandom() * 0.15f),
            FloorZ = floorZ,
            DestroyOnTouch = destroyOnTouch,
            GravityScale = 1f,
        };
        return this;
    }

    /// <summary>
    /// Toss the raptor cluster-bomb shell-fragment gibs (QC <c>RaptorCBShellfragToss</c> /
    /// <c>RaptorCBShellfragDraw</c>, raptor_weapons.qc:244-284, dispatched from the DEATH_VH_RAPT_FRAGMENT burst
    /// FX in damageeffects.qc:353-360). Three bouncing <c>clusterbomb_fragment.md3</c> drawables thrown outward
    /// from the burst point: gravity 0.15, a ±|velocity| spin seed and ongoing tumble jitter,
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
            _shellfrags.TrySpawn(ShellfragLimit, out int slot);
            _shellfrags[slot] = new Gib
            {
                BatchIdx = EnsureBatch("models/vehicles/clusterbomb_fragment.md3"),
                Motion = new DebrisMotion(origin, vel, RandomVec() * vel.Length()),
                GravityScale = 0.15f,
                Lifetime = 3f,
                FloorZ = float.NegativeInfinity,
                FragmentRandom = GD.Randi() | 1u,
            };
        }
    }

    public override void _Process(double delta)
    {
        double dt = delta * ClientRenderTime.Scale;
        if (dt <= 0.0)
            return;

        if (_pool.IsEmpty && _shellfrags.IsEmpty)
        {
            for (int b = 0; b < _batches.Count; b++)
                if (_batches[b].Visible != 0) { _batches[b].Mesh.VisibleInstanceCount = 0; _batches[b].Visible = 0; }
            return;
        }

        using var _scope = FrameProfiler.Scope("gibs"); // house rule: per-frame node → scoped

        if (_packCounts.Length < _batches.Count)
            _packCounts = new int[_batches.Count];
        Array.Clear(_packCounts, 0, _packCounts.Length);

        DebrisTiming timing = DebrisTiming.Read(Cvars, "cl_gibs_ticrate", "cl_gibs_sloppy", 0.1f);
        PackGibs(_pool, dt, timing, GibHalfSize, removeOnNoImpact: true);
        PackGibs(_shellfrags, dt, timing, NVec3.Zero, removeOnNoImpact: false);

        for (int b = 0; b < _batches.Count; b++)
        {
            int want = _packCounts[b];
            if (_batches[b].Visible != want) { _batches[b].Mesh.VisibleInstanceCount = want; _batches[b].Visible = want; }
        }
    }

    private void PackGibs(RubblePool<Gib> pool, double dt, DebrisTiming timing,
        NVec3 halfSize, bool removeOnNoImpact)
    {
        for (int i = 0; i < pool.Length; i++)
        {
            if (!pool.IsActive(i))
                continue;

            ref Gib g = ref pool[i];
            g.Age += dt;
            if (g.Age >= g.Lifetime) { pool.Remove(i); continue; }

            float gravity = timing.Gravity * g.GravityScale;
            int steps = g.Motion.Schedule(dt, timing.Ticrate, out double step, out double remainder);
            for (int j = 0; j < steps && !g.Motion.Resting; j++)
            {
                if (!g.Motion.Step((float)step, gravity, BounceFactor, g.FloorZ, halfSize,
                    WorldTrace, g.DestroyOnTouch, removeOnNoImpact, removeInSolid: false, out _))
                {
                    pool.Remove(i);
                    break;
                }

                // QC adds angular noise per draw. Sample at committed tics so the native tumble
                // stays consistent across render rates, with an independent sequence per fragment.
                if (g.FragmentRandom != 0 && !g.Motion.Resting)
                {
                    g.Motion.AngularVelocity += new NVec3(NextJitter(ref g.FragmentRandom),
                        NextJitter(ref g.FragmentRandom), NextJitter(ref g.FragmentRandom)) * 15f;
                }
            }
            if (!pool.IsActive(i))
                continue;

            Batch b = _batches[g.BatchIdx];
            int idx = _packCounts[g.BatchIdx]++;
            float remaining = (float)(g.Lifetime - g.Age);
            float sink = (1f - Math.Clamp(remaining, 0f, 1f)) * SinkDepth;
            Transform3D pose = g.Motion.Draw((float)remainder, gravity, timing.Sloppy, g.FloorZ,
                halfSize, WorldTrace, flattenOnGround: false, sink, out _);
            b.Mesh.SetInstanceTransform(idx, pose * b.BaseXform);
        }
    }

    private static float NextJitter(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (state >> 8) * (2f / 16777216f) - 1f;
    }

    private int EnsureBatch(string modelPath, int skin = 0)
    {
        var key = (modelPath, skin);
        if (_batchByModel.TryGetValue(key, out int idx))
            return idx;
        (Mesh mesh, Material? mat, Transform3D baseXf) = ResolveMesh(modelPath, skin);
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = mesh,
            InstanceCount = RubblePool<Gib>.Capacity + ShellfragLimit,   // fixed forever — never resized (GPU realloc)
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
        _batchByModel[key] = idx;
        return idx;
    }

    /// <summary>The gib mesh + material + the source node's own transform (folded into every instance).
    /// Prefers the real model via <see cref="SharedMeshCache"/> ([crash fix 2026-07-26]: one built tree per
    /// gib model EVER); generated chunk fallback when the loader is unwired or the parse fails.</summary>
    private (Mesh, Material?, Transform3D) ResolveMesh(string modelPath, int skin = 0)
    {
        string key = skin == 0 ? modelPath : $"{modelPath}#skin{skin}";
        if (SharedMeshCache.Instantiate(key,
                () => SkinnedModelLoader?.Invoke(modelPath, skin) ?? ModelLoader?.Invoke(modelPath)) is { } mi)
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

    private float Cvar(string name, float fallback)
    {
        float value = Cvars?.Has(name) == true ? Cvars.GetFloat(name) : fallback;
        return float.IsFinite(value) ? value : fallback;
    }

    // CSQC lib/random.qc uses three unsigned prandom() values, each in [0, 1).
    // The Raptor code uses the separate, signed randomvec() distribution.
    private static float GibRandom() => (GD.Randi() >> 16) / 65536f;

    private static NVec3 RandomGibVector()
    {
        NVec3 vector;
        do { vector = new NVec3(GibRandom(), GibRandom(), GibRandom()); } while (vector.LengthSquared() > 1f);
        return vector;
    }

}
