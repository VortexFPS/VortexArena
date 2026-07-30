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
/// fast-flying chunks, each a bouncing MOVETYPE_BOUNCE body that fades out after cl_gibs_lifetime.
///
/// We load the real gib models from the mounted content (the MD3 limbs models/gibs/*.md3 and the Quake1
/// <c>chunk.mdl</c>, now handled by the host loader) via the model loader; a small generated mesh is the
/// fallback when the loader is unwired. Physics (gravity + ground bounce + tumble) are integrated client-side per tick,
/// exactly as the QC gib is a pure client drawable advanced by Movetype_Physics_MatchTicrate.
/// </summary>
public sealed partial class ModelGibs : Node3D
{
    /// <summary>Gib lifetime seconds (DP cl_gibs_lifetime default 14, trimmed so they don't pile up).</summary>
    [Export] public float GibLifetime { get; set; } = 8f;

    /// <summary>Hard cap on live gibs (DP cl_gibs_maxcount).</summary>
    [Export] public int MaxGibs { get; set; } = 64;

    private int _liveCount;

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

    /// <summary>Toss one gib of the given model. Public so callers can drop a single gib (e.g. a chunk).</summary>
    public Node3D Toss(string modelPath, NVec3 origin, NVec3 baseVel, NVec3 randVel, float floorZ, bool destroyOnTouch)
    {
        // QC: velocity = vconst*velocity_scale + vrand*velocity_random + up. We fold the cvars into sane
        // constants (scale 1, random 1, up 100) so it reads like the default config.
        NVec3 vel = baseVel + randVel + new NVec3(0f, 0f, 100f);

        Node3D mesh = BuildMesh(modelPath);
        var gib = new GibBody
        {
            Name = "gib",
            Position = Coords.ToGodot(origin),
            VelocityQuake = vel,
            Lifetime = GibLifetime * (1f + GD.Randf() * 0.15f),
            FloorZ = floorZ,
            DestroyOnTouch = destroyOnTouch,
            AngularVel = RandomVecG() * (vel.Length() * 0.02f),
        };
        gib.AddChild(mesh);

        AddChild(gib);
        gib.OnFreed = () => _liveCount = Math.Max(0, _liveCount - 1);
        _liveCount++;
        CullIfNeeded();
        return gib;
    }

    /// <summary>
    /// Toss the raptor cluster-bomb shell-fragment gibs (QC <c>RaptorCBShellfragToss</c> /
    /// <c>RaptorCBShellfragDraw</c>, raptor_weapons.qc:244-284, dispatched from the DEATH_VH_RAPT_FRAGMENT burst
    /// FX in damageeffects.qc:353-360). Three bouncing <c>clusterbomb_fragment.md3</c> drawables thrown outward
    /// from the burst point: gravity 0.15, an avelocity = ±|velocity| seed plus a per-frame ±15 tumble jitter,
    /// a 3s lifetime that fades over its final second (QC cnt = time+2, nextthink = time+3). Pure cosmetic
    /// debris — <paramref name="origin"/>/<paramref name="bombVel"/> are Quake space (the bursting bomb's pose).
    /// </summary>
    public void TossShellfrags(NVec3 origin, NVec3 bombVel)
    {
        for (int i = 1; i < 4; i++)
        {
            // QC damageeffects.qc: vel = normalize(w_org - (w_org + force_dir*16)) + randomvec()*128. We lack the
            // surface backoff (force_dir) headless, so seed a small outward/upward bias + the dominant random spray.
            NVec3 vel = new NVec3(0f, 0f, 0.4f) + RandomVec() * 128f;
            Node3D mesh = BuildMesh("models/vehicles/clusterbomb_fragment.md3");
            var frag = new GibBody
            {
                Name = "raptor_cb_shellfrag",
                Position = Coords.ToGodot(origin),
                VelocityQuake = vel,
                GravityScale = 0.15f,                       // QC sfrag.gravity = 0.15
                Lifetime = 3f,                              // QC sfrag.nextthink = time + 3
                FadeDuration = 1f,                          // QC cnt = time + 2 → fades over the final second
                FloorZ = float.NegativeInfinity,
                DestroyOnTouch = false,
                // QC: avelocity = prandomvec() * vlen(velocity); plus a +15/draw jitter (AngularJitter).
                AngularVel = RandomVecG() * vel.Length(),
                AngularJitter = 15f,
            };
            frag.AddChild(mesh);
            AddChild(frag);
            frag.OnFreed = () => _liveCount = Math.Max(0, _liveCount - 1);
            _liveCount++;
        }
        CullIfNeeded();
    }

    /// <summary>
    /// (engine-perf 2026-06-16) Build one hidden instance per DISTINCT gib model for the offscreen GPU pipeline
    /// warm pass (<see cref="GpuWarmPass"/>). The gib world-models render via the entity feed and are otherwise
    /// un-warmed, so the FIRST combat death first-instances their (mesh,material) pipeline mid-match — a
    /// synchronous SURFACE compile (the residual a RenderDoc capture pinned to the MD3-entity class). Uses the
    /// SAME <see cref="BuildMesh"/> factory a live <see cref="Toss"/> uses, so it warms exactly what plays: the
    /// real MD3 limb when <see cref="ModelLoader"/> resolves it, the generated chunk fallback otherwise. The
    /// returned nodes are unparented — the warm pass parents, renders, and frees them.
    /// </summary>
    public List<Node3D> BuildWarmupInstances()
    {
        var list = new List<Node3D>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // One opaque instance (the first-draw variant) + one alpha-override instance (the final-second fade
        // variant ApplyAlpha switches to — a distinct PSO otherwise compiled mid-match on the first gib fade).
        void Warm(string path)
        {
            if (!seen.Add(path)) return;
            list.Add(BuildMesh(path));
            list.Add(GpuWarmPass.AlphaWarm(BuildMesh(path)));
        }
        foreach (string mdl in LimbModels) Warm(mdl);   // arm (listed twice → deduped), chest, smallchest, leg1, leg2
        Warm("models/gibs/eye.md3");                    // the eye + bloody skull Splash() always tosses
        Warm("models/gibs/bloodyskull.md3");
        Warm("models/gibs/chunk.mdl");                  // fast chunks (real Quake1 MDL)
        return list;
    }

    // ------------------------------------------------------------------------------------------------

    private Node3D BuildMesh(string modelPath)
    {
        // All shipped gib models load through the host loader now, including the Quake1 chunk.mdl (MdlReader
        // added 2026-07); GeneratedChunk stays as the fallback when the loader is unwired or a parse fails.
        // [crash fix 2026-07-26] one built tree per gib model EVER; every gib shares its mesh
        // (SharedMeshCache) — the per-gib Md3Builder ArrayMesh (~3-7 finalizer-thread frees/s in combat)
        // was the #2 residual churn source.
        if (ModelLoader is not null)
        {
            MeshInstance3D? shared = SharedMeshCache.Instantiate(modelPath, () => ModelLoader(modelPath));
            if (shared is not null)
                return shared;
        }
        return GeneratedChunk();
    }

    // The fallback chunk mesh+material is identical for every generated gib, so build it once and share the Mesh
    // resource across all instances (only the lightweight MeshInstance3D node is per-gib). Avoids constructing a
    // BoxMesh + StandardMaterial3D — and compiling that material's shader on first draw — on the gib's frame.
    private static BoxMesh? _chunkMesh;

    /// <summary>A small reddish chunk used for chunk.mdl and any model that fails to load.</summary>
    private static MeshInstance3D GeneratedChunk()
    {
        _chunkMesh ??= new BoxMesh
        {
            Size = new Vector3(4f, 4f, 4f),
            Material = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.45f, 0.06f, 0.05f),
                Roughness = 0.9f,
            },
        };
        return new MeshInstance3D { Mesh = _chunkMesh };
    }

    private void CullIfNeeded()
    {
        if (_liveCount <= MaxGibs)
            return;
        foreach (Node child in GetChildren())
        {
            if (child is GibBody gb && GodotObject.IsInstanceValid(gb))
            {
                gb.QueueFree();
                _liveCount = Math.Max(0, _liveCount - 1);
                if (_liveCount <= MaxGibs)
                    break;
            }
        }
    }

    private static NVec3 RandomVec()
        => new((float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0));

    private static Vector3 RandomVecG()
        => new((float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0));

    // ================================================================================================
    //  Per-gib ballistic body (the client MOVETYPE_BOUNCE analogue from Gib_Draw)
    // ================================================================================================

    public sealed partial class GibBody : Node3D
    {
        public NVec3 VelocityQuake;
        public Vector3 AngularVel;
        public float Lifetime = 8f;
        public float FloorZ = float.NegativeInfinity;
        public bool DestroyOnTouch;
        public Action? OnFreed;

        /// <summary>QC <c>.gravity</c> scale (1 = full sv_gravity). The raptor shellfrags use 0.15.</summary>
        public float GravityScale = 1f;

        /// <summary>Per-tick avelocity jitter (QC <c>RaptorCBShellfragDraw: avelocity += randomvec()*15</c>); 0 = none.</summary>
        public float AngularJitter;

        /// <summary>How long the final fade-out lasts (QC gibs fade over their last second; shellfrags fade
        /// over the cnt..nextthink window, ~1s). The alpha ramps 0→1 over this many seconds before death.</summary>
        public float FadeDuration = 1f;

        private const float Gravity = 800f;       // sv_gravity; gibs use gravity 1 (full)
        private const float BounceFactor = 0.4f;  // gib bouncefactor-ish
        private float _age;
        private bool _resting;

        public override void _PhysicsProcess(double delta)
        {
            // #30 slowmo/pause: gib tosses are Base CSQC (cl.time-driven) — scale like the casings so gibs
            // hang frozen at slowmo 0 instead of settling on wall clock.
            float dt = VortexArena.Game.Client.ClientRenderTime.ScaleDelta((float)delta);
            if (dt <= 0f)
                return; // paused — hold everything in place
            _age += dt;
            if (_age >= Lifetime)
            {
                OnFreed?.Invoke();
                QueueFree();
                return;
            }

            if (!_resting)
            {
                VelocityQuake.Z -= Gravity * GravityScale * dt;
                // QC RaptorCBShellfragDraw: avelocity += randomvec() * 15 each draw — a continuous tumble jitter.
                if (AngularJitter != 0f)
                    AngularVel += RandomVecG() * (AngularJitter * dt);
                NVec3 posQ = Coords.ToQuake(Position) + VelocityQuake * dt;

                if (!float.IsNegativeInfinity(FloorZ) && posQ.Z <= FloorZ && VelocityQuake.Z < 0f)
                {
                    if (DestroyOnTouch)
                    {
                        // chunk.mdl-style: splat on first ground contact.
                        OnFreed?.Invoke();
                        QueueFree();
                        return;
                    }
                    posQ.Z = FloorZ;
                    VelocityQuake.Z = -VelocityQuake.Z * BounceFactor;
                    VelocityQuake.X *= 0.6f;
                    VelocityQuake.Y *= 0.6f;
                    AngularVel *= 0.5f;
                    if (VelocityQuake.Length() < 25f)
                    {
                        _resting = true;
                        VelocityQuake = default;
                    }
                }
                Position = Coords.ToGodot(posQ);
                RotateObjectLocal(Vector3.Up, AngularVel.Z * dt);
                RotateObjectLocal(Vector3.Right, AngularVel.X * dt);
            }

            // Fade over the final FadeDuration seconds (QC sets alpha = bound(0, nextthink-time, 1)).
            float remaining = Lifetime - _age;
            if (remaining < FadeDuration)
                ApplyAlpha(this, Math.Clamp(remaining / Math.Max(0.001f, FadeDuration), 0f, 1f));
        }

        private static void ApplyAlpha(Node node, float a)
        {
            // Per-INSTANCE fade (GeometryInstance3D.Transparency, Forward+). The old per-surface material
            // write hit the AssetSystem-cached SHARED material — and since the SharedMeshCache merge the
            // mesh is shared by every live gib of this model AND outlives the map (cl_persist_asset_cache):
            // all gibs faded in lockstep, and a gib dying mid-fade left the cached material near-invisible
            // for the rest of the session. Instance transparency touches no shared resource.
            if (node is GeometryInstance3D gi)
                gi.Transparency = 1f - a;
            foreach (Node child in node.GetChildren())
                ApplyAlpha(child, a);
        }
    }
}
