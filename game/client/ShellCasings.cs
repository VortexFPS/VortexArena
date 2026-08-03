using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Game;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>
/// Ejected shell casings — the small brass/shell meshes a weapon throws on each shot. The C# successor to
/// CSQC's casings.qc (the <c>casings</c> net temp-entity + Casing_Draw): MDL_CASING_BULLET
/// (models/casing_bronze.iqm) for bullet weapons, MDL_CASING_SHELL (models/casing_shell.mdl) for the
/// shotgun. A casing is a bouncing rigid body (MOVETYPE_BOUNCE, gravity 1, an avelocity tumble) that lives
/// for cl_casings_*_time seconds.
///
/// (zero-hitch 2026-08-03) STRUCT-ARRAY SIM + MULTIMESH RENDER. The previous shape was one
/// <c>CasingBody : Node3D</c> per casing — at the 100 cap that is 100 scene nodes each with its own
/// <c>_PhysicsProcess</c> native callback, 100 render objects, 100 draw calls, and a spawn/QueueFree churn
/// burst on every firefight (the release captures' draw-count spikes + unscoped/post-process hitch class).
/// Now the ballistics live in ONE struct pool advanced by ONE <c>_PhysicsProcess</c>, and rendering is TWO
/// fixed-capacity <see cref="MultiMesh"/> batches (one per casing kind): two render objects, two draws,
/// zero per-casing nodes, zero allocation after warmup. The sim itself is the same Base
/// <c>Movetype_Physics_MatchTicrate</c> port: fixed cl_casings_ticrate tics, MOVETYPE_BOUNCE reflection off
/// real brush faces via <see cref="TraceHook"/> (FloorZ ground-plane fallback), the startsolid cull, the
/// Casing_Touch bounce sound with its self-silencing throttle.
///
/// One deliberate visual deviation: end-of-life is a short SINK (the casing settles into the floor over the
/// final 0.4 s) instead of the old per-instance alpha fade — per-instance transparency does not compose with
/// the loaded models' skin ShaderMaterials under MultiMesh, and at casing scale the two read the same.
/// </summary>
public sealed partial class ShellCasings : Node3D
{
    /// <summary>Casing kind, matching the QC <c>casing.state</c> switch (1 = shotgun shell, else bullet).</summary>
    public enum CasingKind { Bullet = 0, Shell = 1 }

    /// <summary>Bullet-casing lifetime seconds (DP cl_casings_bronze_time default).</summary>
    [Export] public float BulletTime { get; set; } = 10f;

    /// <summary>Shell-casing lifetime seconds (DP cl_casings_shell_time default).</summary>
    [Export] public float ShellTime { get; set; } = 30f;

    /// <summary>Hard cap on live casings (DP cl_casings_maxcount). Also the fixed MultiMesh capacity.</summary>
    [Export] public int MaxCasings { get; set; } = 100;

    /// <summary>
    /// Positional bounce-sound hook (host-set to <see cref="ClientWorld.OnSound"/>): plays the casing-impact
    /// samples (<c>brass1-3</c> / <c>casings1-3</c>) on touch, faithful to Base <c>Casing_Touch</c> (casings.qc).
    /// Signature: (sample, originQuake). When unset, casings bounce silently.
    /// </summary>
    public Action<string, NVec3>? SoundHook { get; set; }

    /// <summary>
    /// Result of a casing world-collision sweep (Base <c>Movetype_Physics_MatchTicrate</c> MOVETYPE_BOUNCE
    /// trace over the static map BSP). <see cref="Fraction"/> 1 = no hit; <see cref="Normal"/> is the impact
    /// plane normal (Quake space); <see cref="StartSolid"/> = the casing started inside a brush (gun poking
    /// into a wall — Base deletes those, mirroring <c>Casing_Draw</c>'s <c>trace_startsolid</c> cull).
    /// </summary>
    public readonly record struct CasingTrace(float Fraction, NVec3 EndPos, NVec3 Normal, bool StartSolid);

    /// <summary>
    /// World-only collision sweep hook (start→end in Quake space), host-set from the client
    /// <c>TraceService</c> (see <see cref="EffectSystem.SetCollisionWorld"/>). When set, casings do full
    /// MOVETYPE_BOUNCE world collision (reflect off real brush faces) at the Base <c>cl_casings_ticrate</c>
    /// instead of the single ground-plane bounce; when null they fall back to the FloorZ ground plane.
    /// </summary>
    public Func<NVec3, NVec3, CasingTrace>? TraceHook { get; set; }

    /// <summary>
    /// Optional host-supplied model loader (e.g. <c>AssetLoader.LoadModel</c>): given a virtual model path
    /// returns a fresh Godot node, or null on miss. When unset, casings render as a tiny generated cylinder.
    /// </summary>
    public Func<string, Node3D?>? ModelLoader { get; set; }

    // DP world gravity (sv_gravity default). gib/casing entities use gravity 1 (full).
    private const float Gravity = 800f;
    // Base advances casings at a FIXED tic (cl_casings_ticrate, via Movetype_Physics_MatchTicrate) rather than
    // per-frame, so the bounce reflection is frame-rate independent.
    private const float Ticrate = 0.03125f;     // cl_casings_ticrate
    private const float SinkWindow = 0.4f;      // end-of-life sink duration (was the alpha-fade window)
    private const float SinkDepth = 2.5f;       // Quake units the casing settles into the floor

    /// <summary>One simulated casing (pure data — no node, no allocation).</summary>
    private struct Casing
    {
        public bool Active;
        public CasingKind Kind;
        public uint Seq;             // spawn order (oldest = lowest; the cap evicts by this)
        public NVec3 PosQuake;
        public NVec3 Vel;            // Quake space
        public Basis Rot;            // accumulated tumble (Godot space)
        public Vector3 AngularVel;   // rad/s per local axis
        public float BounceFactor, Lifetime, FloorZ, Age, TicAccum, NextSoundAt;
        public bool OnGround;
    }

    private Casing[] _pool = Array.Empty<Casing>();
    private uint _spawnSeq;

    // One MultiMesh batch per kind: mesh + material resolved once (SharedMeshCache / generated fallback),
    // instance buffer sized MaxCasings ONCE and never resized (an InstanceCount write reallocs the GPU
    // buffer — the FaithfulParticleRenderer lesson). VisibleInstanceCount is the per-frame limiter.
    private sealed class Batch
    {
        public MultiMeshInstance3D Node = null!;
        public MultiMesh Mesh = null!;
        public Transform3D BaseXform;   // the source model's own node transform, folded into every instance
        public int Visible;
    }

    private readonly Batch?[] _batches = new Batch?[2];   // indexed by CasingKind

    /// <summary>
    /// Eject a casing from <paramref name="origin"/> (Quake space) with initial <paramref name="velocity"/>
    /// (Quake space). The casing tumbles, falls under gravity, bounces off world faces (or the ground plane at
    /// <paramref name="floorZ"/>), and settles away at end of life. Returns this node (legacy signature —
    /// casings no longer have per-spawn nodes).
    /// </summary>
    public Node3D Spawn(NVec3 origin, NVec3 velocity, CasingKind kind = CasingKind.Bullet, float floorZ = float.NegativeInfinity)
    {
        if (_pool.Length == 0)
            _pool = new Casing[Math.Max(1, MaxCasings)];
        EnsureBatch(kind);

        // Find a free slot; pool full → evict the OLDEST (DP cl_casings_maxcount cull).
        int slot = -1;
        uint oldest = uint.MaxValue; int oldestIdx = 0;
        for (int i = 0; i < _pool.Length; i++)
        {
            if (!_pool[i].Active) { slot = i; break; }
            if (_pool[i].Seq < oldest) { oldest = _pool[i].Seq; oldestIdx = i; }
        }
        if (slot < 0) slot = oldestIdx;

        // QC adds a little velocity jitter on receipt: casing.velocity += 2 * prandomvec().
        ref Casing c = ref _pool[slot];
        c.Active = true;
        c.Kind = kind;
        c.Seq = ++_spawnSeq;
        c.PosQuake = origin;
        c.Vel = velocity + RandomVec() * 2f;
        c.Rot = Basis.Identity;
        // QC: avelocity = '0 10 0' + 100*prandomvec() — a base yaw tumble of 10 deg/s plus a ±100 deg/s
        // per-axis random (stored rad/s for the Godot rotate).
        c.AngularVel = new Vector3(
            Mathf.DegToRad((float)GD.RandRange(-100.0, 100.0)),
            Mathf.DegToRad(10f + (float)GD.RandRange(-100.0, 100.0)),
            Mathf.DegToRad((float)GD.RandRange(-100.0, 100.0)));
        c.BounceFactor = kind == CasingKind.Shell ? 0.25f : 0.5f;
        c.Lifetime = kind == CasingKind.Shell ? ShellTime : BulletTime;
        c.FloorZ = floorZ;
        c.Age = 0f; c.TicAccum = 0f; c.NextSoundAt = 0f;
        c.OnGround = false;
        return this;
    }

    /// <summary>ONE physics callback for every live casing (was one per casing). Advances the pool, then
    /// packs the per-kind MultiMesh instance transforms.</summary>
    public override void _PhysicsProcess(double delta)
    {
        // #30 slowmo/pause: the casing ballistic sim is Base CSQC (cl.time-driven) — scale by the client
        // render-time factor so casings freeze mid-air at slowmo 0 and tumble slow at fractional slowmo.
        float dt = VortexArena.Game.Client.ClientRenderTime.ScaleDelta((float)delta);
        if (dt <= 0f)
            return; // paused — hold age, position and sound state exactly where they are

        int live = 0;
        for (int i = 0; i < _pool.Length; i++)
            if (_pool[i].Active) { live = 1; break; }
        if (live == 0)
        {
            for (int k = 0; k < _batches.Length; k++)
                if (_batches[k] is { } idle && idle.Visible != 0) { idle.Mesh.VisibleInstanceCount = 0; idle.Visible = 0; }
            return;
        }

        using var _scope = VortexArena.Game.Client.FrameProfiler.Scope("casings"); // house rule: per-frame node → scoped

        Span<int> counts = stackalloc int[2];
        for (int i = 0; i < _pool.Length; i++)
        {
            ref Casing c = ref _pool[i];
            if (!c.Active)
                continue;

            c.Age += dt;
            if (c.Age >= c.Lifetime) { c.Active = false; continue; }

            // Step the ballistic sim in fixed tics (Base Movetype_Physics_MatchTicrate), clamped catch-up so
            // a long frame stall can't tunnel the casing through the world.
            if (!c.OnGround)
            {
                c.TicAccum += dt;
                int maxTics = 4; // ≈125ms backlog cap
                while (c.TicAccum >= Ticrate && maxTics-- > 0)
                {
                    c.TicAccum -= Ticrate;
                    if (!StepTic(ref c, Ticrate))
                        break;      // freed (startsolid cull)
                    if (c.OnGround)
                        break;
                }
                if (maxTics <= 0)
                    c.TicAccum = 0f; // drop the backlog rather than chasing it
                if (!c.Active)
                    continue;
            }

            // Write this casing's instance into its kind's batch.
            Batch? b = _batches[(int)c.Kind];
            if (b is null)
                continue;
            int idx = counts[(int)c.Kind]++;
            if (idx >= MaxCasings)
                continue;
            // End-of-life sink (see the class note): settle SinkDepth into the floor over the last SinkWindow.
            float remaining = c.Lifetime - c.Age;
            float sink = remaining < SinkWindow ? (1f - Math.Clamp(remaining / SinkWindow, 0f, 1f)) * SinkDepth : 0f;
            Vector3 pos = Coords.ToGodot(c.PosQuake) + new Vector3(0f, -sink, 0f);   // units map 1:1 (Coords swizzle only)
            b.Mesh.SetInstanceTransform(idx, new Transform3D(c.Rot, pos) * b.BaseXform);
        }

        for (int k = 0; k < _batches.Length; k++)
        {
            if (_batches[k] is not { } bb)
                continue;
            int want = Math.Min(counts[k], MaxCasings);
            if (bb.Visible != want) { bb.Mesh.VisibleInstanceCount = want; bb.Visible = want; }
        }
    }

    /// <summary>One MOVETYPE_BOUNCE tic (verbatim port of the old CasingBody.StepTic, on the struct).</summary>
    private bool StepTic(ref Casing c, float dt)
    {
        c.Vel.Z -= Gravity * dt;
        NVec3 fromQ = c.PosQuake;
        NVec3 toQ = fromQ + c.Vel * dt;

        if (TraceHook is not null)
        {
            CasingTrace tr = TraceHook(fromQ, toQ);

            // Gun poking into a wall: the casing spawned inside solid — Base's Casing_Draw deletes it on
            // trace_startsolid rather than letting it sit embedded in the brush.
            if (tr.StartSolid)
            {
                c.Active = false;
                return false;
            }

            if (tr.Fraction < 1f && tr.Normal != NVec3.Zero)
            {
                // Move to the impact point, then reflect the velocity off the surface and damp it
                // (MOVETYPE_BOUNCE: v' = v - (1+bounce)*(v·n)*n).
                NVec3 n = NVec3.Normalize(tr.Normal);
                BounceCasingSound(ref c);

                c.PosQuake = tr.EndPos;
                float vn = NVec3.Dot(c.Vel, n);
                c.Vel -= n * ((1f + c.BounceFactor) * vn);
                c.AngularVel *= 0.6f;

                // Rest on a near-flat surface once it's barely moving (QC zeroes pitch/roll on ground).
                if (n.Z > 0.7f && c.Vel.Length() < 20f)
                {
                    c.OnGround = true;
                    c.Vel = default;
                }
            }
            else
            {
                c.PosQuake = toQ;
            }
        }
        else
        {
            // Legacy FloorZ ground-plane bounce (no world tracer wired).
            if (!float.IsNegativeInfinity(c.FloorZ) && toQ.Z <= c.FloorZ && c.Vel.Z < 0f)
            {
                BounceCasingSound(ref c);
                toQ.Z = c.FloorZ;
                c.Vel.Z = -c.Vel.Z * c.BounceFactor;
                c.Vel.X *= 0.7f;
                c.Vel.Y *= 0.7f;
                c.AngularVel *= 0.6f;
                if (c.Vel.Length() < 20f)
                {
                    c.OnGround = true;
                    c.Vel = default;
                }
            }
            c.PosQuake = toQ;
        }

        // Tumble (QC avelocity), object-local axes exactly like the old RotateObjectLocal triple.
        if (!c.OnGround)
        {
            c.Rot *= new Basis(Vector3.Right, c.AngularVel.X * dt);
            c.Rot *= new Basis(Vector3.Up, c.AngularVel.Y * dt);
            c.Rot *= new Basis(Vector3.Back, c.AngularVel.Z * dt);
        }
        return true;
    }

    /// <summary>
    /// Play the Base <c>Casing_Touch</c> bounce sound: a random <c>brass*</c>/<c>casings*</c> impact when the
    /// casing hits a surface at speed (<c>vdist(velocity,>,50)</c>). Base bumps <c>nextthink = time + 0.2</c>
    /// on EVERY touch, so a casing grinding on the ground keeps pushing the throttle ahead of <c>time</c> —
    /// the sound fires once on the real bounce and then stays silent (playtest-bugs #1). Uses the pre-bounce
    /// velocity, as the QC touch fires with the incoming velocity.
    /// </summary>
    private void BounceCasingSound(ref Casing c)
    {
        bool ready = c.Age >= c.NextSoundAt;
        c.NextSoundAt = c.Age + 0.2f; // bump on EVERY touch (QC nextthink), so continuous contact self-silences
        if (ready && c.Vel.Length() > 50f)
            SoundHook?.Invoke(RandomImpactSound(c.Kind), c.PosQuake);
    }

    private static string RandomImpactSound(CasingKind kind)
    {
        int n = GD.RandRange(1, 3);
        return kind == CasingKind.Shell ? $"weapons/casings{n}.wav" : $"weapons/brass{n}.wav";
    }

    // ------------------------------------------------------------------------------------------------
    //  Batches + meshes
    // ------------------------------------------------------------------------------------------------

    private void EnsureBatch(CasingKind kind)
    {
        if (_batches[(int)kind] is not null)
            return;
        (Mesh mesh, Material? mat, Transform3D baseXf) = ResolveMesh(kind);
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = mesh,
            InstanceCount = Math.Max(1, MaxCasings),   // fixed forever — never resized (GPU realloc)
            VisibleInstanceCount = 0,
        };
        var node = new MultiMeshInstance3D
        {
            Name = kind == CasingKind.Shell ? "shell_casings" : "bullet_casings",
            Multimesh = mm,
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            // Instances scatter across the map inside one batch — never cull the lot on a stale AABB.
            ExtraCullMargin = 16384f,
        };
        AddChild(node);
        _batches[(int)kind] = new Batch { Node = node, Mesh = mm, BaseXform = baseXf };
    }

    /// <summary>The casing mesh + material + the source node's own transform (folded into every instance).
    /// Prefers the real model (IQM brass / MDL shell) via <see cref="SharedMeshCache"/>; generated cylinder
    /// fallback when the loader is unwired or the model can't be parsed.</summary>
    private (Mesh, Material?, Transform3D) ResolveMesh(CasingKind kind)
    {
        string vpath = kind == CasingKind.Shell ? "models/casing_shell.mdl" : "models/casing_bronze.iqm";
        if (ModelLoader is not null && SharedMeshCache.Instantiate(vpath, () => ModelLoader(vpath)) is { } mi)
        {
            Material? mat = mi.MaterialOverride;
            if (mat is null && mi.GetSurfaceOverrideMaterialCount() > 0)
                mat = mi.GetSurfaceOverrideMaterial(0);
            Mesh m = mi.Mesh;
            Transform3D xf = mi.Transform;
            mi.QueueFree();   // only needed its resolved (mesh, material, transform)
            return (m, mat, xf);
        }
        bool shell = kind == CasingKind.Shell;
        return (shell ? _shellMesh ??= BuildCasingMesh(true) : _bulletMesh ??= BuildCasingMesh(false),
                null,
                new Transform3D(new Basis(Vector3.Right, Mathf.DegToRad(90f)), Vector3.Zero));
    }

    private static CylinderMesh? _shellMesh;
    private static CylinderMesh? _bulletMesh;

    private static CylinderMesh BuildCasingMesh(bool shell)
    {
        Color brass = shell ? new Color(0.7f, 0.15f, 0.12f) : new Color(0.78f, 0.62f, 0.25f);
        return new CylinderMesh
        {
            TopRadius = shell ? 0.9f : 0.5f,
            BottomRadius = shell ? 0.9f : 0.5f,
            Height = shell ? 3.0f : 1.6f,
            RadialSegments = 6,
            Rings = 0,
            Material = new StandardMaterial3D
            {
                AlbedoColor = brass,
                Metallic = 0.8f,
                Roughness = 0.35f,
            },
        };
    }

    /// <summary>
    /// One hidden instance per casing variant for the offscreen GPU pipeline warm pass — as MULTIMESH
    /// instances, because instanced rendering is its own vertex format and therefore its own pipeline: warming
    /// a plain MeshInstance3D would leave the live MultiMesh's pipeline cold. Same meshes/materials the live
    /// batches resolve. The warm pass parents, renders, and frees them.
    /// </summary>
    public List<Node3D> BuildWarmupInstances()
    {
        var list = new List<Node3D>(2);
        foreach (CasingKind kind in new[] { CasingKind.Bullet, CasingKind.Shell })
        {
            (Mesh mesh, Material? mat, Transform3D baseXf) = ResolveMesh(kind);
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
                Name = $"casing_warm_{kind}",
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
}
