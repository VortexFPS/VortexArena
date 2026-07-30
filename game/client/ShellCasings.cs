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
/// for cl_casings_*_time seconds and fades out.
///
/// We integrate the ballistic + bounce ourselves in <see cref="CasingBody._PhysicsProcess"/> (the sim runs
/// client-side; casings are pure client eye-candy in QC too, advanced by Movetype_Physics_MatchTicrate on the
/// CSQC side). When a <see cref="TraceHook"/> is wired (the client world tracer, see
/// <see cref="EffectSystem.SetCollisionWorld"/>) the casing does full MOVETYPE_BOUNCE world collision at the
/// Base <c>cl_casings_ticrate</c> (0.03125) fixed tic — reflecting off real brush faces — and is culled if it
/// spawned inside solid (the QC <c>trace_startsolid</c> cull). Without a tracer it falls back to a single
/// FloorZ ground plane. The lifetime fade + maxcount cull keep them from piling up, like the QC.
/// </summary>
public sealed partial class ShellCasings : Node3D
{
    /// <summary>Casing kind, matching the QC <c>casing.state</c> switch (1 = shotgun shell, else bullet).</summary>
    public enum CasingKind { Bullet = 0, Shell = 1 }

    /// <summary>Bullet-casing lifetime seconds (DP cl_casings_bronze_time default).</summary>
    [Export] public float BulletTime { get; set; } = 10f;

    /// <summary>Shell-casing lifetime seconds (DP cl_casings_shell_time default).</summary>
    [Export] public float ShellTime { get; set; } = 30f;

    /// <summary>Hard cap on live casings (DP cl_casings_maxcount).</summary>
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

    private int _liveCount;

    /// <summary>
    /// Optional host-supplied model loader (e.g. <c>AssetLoader.LoadModel</c>): given a virtual model path
    /// returns a fresh Godot node, or null on miss. When unset, casings render as a tiny generated cylinder.
    /// </summary>
    public Func<string, Node3D?>? ModelLoader { get; set; }

    /// <summary>
    /// Eject a casing from <paramref name="origin"/> (Quake space) with initial <paramref name="velocity"/>
    /// (Quake space). The casing tumbles, falls under gravity, bounces off the ground plane at
    /// <paramref name="floorZ"/> (Quake Z), and fades out. Returns the spawned body.
    /// </summary>
    public Node3D Spawn(NVec3 origin, NVec3 velocity, CasingKind kind = CasingKind.Bullet, float floorZ = float.NegativeInfinity)
    {
        // QC adds a little velocity jitter on receipt: casing.velocity += 2 * prandomvec().
        NVec3 vel = velocity + RandomVec() * 2f;
        float life = kind == CasingKind.Shell ? ShellTime : BulletTime;
        float bounce = kind == CasingKind.Shell ? 0.25f : 0.5f;

        Node3D mesh = BuildMesh(kind);

        var body = new CasingBody
        {
            Name = "casing",
            Position = Coords.ToGodot(origin),
            VelocityQuake = vel,
            BounceFactor = bounce,
            Lifetime = life,
            FloorZ = floorZ,
            Kind = kind,
            SoundHook = SoundHook,
            TraceHook = TraceHook,
            // QC: avelocity = '0 10 0' + 100*prandomvec() — a base yaw tumble of 10 deg/s plus a ±100 deg/s
            // per-axis random. (Quake avelocity is degrees/s, applied to angles; we store radians/s for the
            // Godot rotate, hence the DegToRad on each component.)
            AngularVel = new Vector3(
                Mathf.DegToRad((float)GD.RandRange(-100.0, 100.0)),
                Mathf.DegToRad(10f + (float)GD.RandRange(-100.0, 100.0)),
                Mathf.DegToRad((float)GD.RandRange(-100.0, 100.0))),
        };
        body.AddChild(mesh);

        AddChild(body);
        body.OnFreed = () => _liveCount = Math.Max(0, _liveCount - 1);
        _liveCount++;
        CullIfNeeded();
        return body;
    }

    /// <summary>
    /// (engine-perf 2026-06-16) Build one hidden instance per casing variant for the offscreen GPU pipeline warm
    /// pass (<see cref="GpuWarmPass"/>). Casings render via the entity feed and are otherwise un-warmed, so the
    /// FIRST weapon shot first-instances their (mesh,material) pipeline mid-match — a synchronous SURFACE compile.
    /// Uses the SAME <see cref="BuildMesh"/> factory a live <see cref="Spawn"/> uses (the real models when
    /// <see cref="ModelLoader"/> resolves them — IQM bullet brass, MDL shotgun shell — else the generated
    /// cylinder fallback). The returned nodes are
    /// unparented — the warm pass parents, renders, and frees them.
    /// </summary>
    public List<Node3D> BuildWarmupInstances()
        => new()
        {
            // Opaque first-draw variant + the alpha-override fade variant (ApplyAlpha switches to Alpha over the
            // final 0.4s — a distinct PSO otherwise compiled mid-match on the first casing fade).
            BuildMesh(CasingKind.Bullet), GpuWarmPass.AlphaWarm(BuildMesh(CasingKind.Bullet)),
            BuildMesh(CasingKind.Shell),  GpuWarmPass.AlphaWarm(BuildMesh(CasingKind.Shell)),
        };

    // ------------------------------------------------------------------------------------------------

    private Node3D BuildMesh(CasingKind kind)
    {
        // Prefer the real casing model. The shotgun shell is a Quake1 .mdl and the bullet casing an IQM; both
        // load through the host loader now (MdlReader added 2026-07). The generated cylinder stays as the
        // fallback if the loader is unwired or the model can't be parsed.
        string vpath = kind == CasingKind.Shell ? "models/casing_shell.mdl" : "models/casing_bronze.iqm";
        if (ModelLoader is not null)
        {
            // [crash fix 2026-07-26] one built tree per casing model EVER; every casing shares its mesh
            // (SharedMeshCache) — the per-casing IqmBuilder.Build (fresh ArrayMesh + AnimationLibrary,
            // 10-25 finalizer-thread frees/s under sustained fire) was the top residual churn source.
            MeshInstance3D? shared = SharedMeshCache.Instantiate(vpath, () => ModelLoader(vpath));
            if (shared is not null)
                return shared;
        }
        return GeneratedCasing(kind);
    }

    // Each casing kind's fallback mesh+material is identical across spawns, so build the two variants once and
    // share the Mesh resource (only the MeshInstance3D node is per-casing). Avoids constructing a CylinderMesh +
    // StandardMaterial3D — and the material's first-draw shader compile — on the casing's frame.
    private static CylinderMesh? _shellMesh;
    private static CylinderMesh? _bulletMesh;

    /// <summary>A tiny brass cylinder used when the real casing model can't be loaded.</summary>
    private static MeshInstance3D GeneratedCasing(CasingKind kind)
    {
        bool shell = kind == CasingKind.Shell;
        CylinderMesh mesh = shell
            ? _shellMesh ??= BuildCasingMesh(true)
            : _bulletMesh ??= BuildCasingMesh(false);
        // Lay the cylinder along its travel-ish axis (Godot cylinder is Y-up by default; rotate to be a pin).
        return new MeshInstance3D
        {
            Mesh = mesh,
            RotationDegrees = new Vector3(90f, 0f, 0f),
        };
    }

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

    private void CullIfNeeded()
    {
        if (_liveCount <= MaxCasings)
            return;
        // Free the oldest casing child past the cap (cheap FIFO over the node list).
        foreach (Node child in GetChildren())
        {
            if (child is CasingBody cb && GodotObject.IsInstanceValid(cb))
            {
                cb.QueueFree();
                _liveCount = Math.Max(0, _liveCount - 1);
                if (_liveCount <= MaxCasings)
                    break;
            }
        }
    }

    private static NVec3 RandomVec()
        => new((float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0));

    // ================================================================================================
    //  The per-casing ballistic body
    // ================================================================================================

    /// <summary>
    /// One casing: integrates a simple gravity + ground-bounce trajectory each physics tick (the client-side
    /// MOVETYPE_BOUNCE analogue from Casing_Draw), tumbles via an angular velocity, and fades+frees at the
    /// end of its lifetime. Velocity is stored in Quake space and converted for the Godot transform.
    /// </summary>
    public sealed partial class CasingBody : Node3D
    {
        public NVec3 VelocityQuake;
        public Vector3 AngularVel;
        public float BounceFactor = 0.5f;
        public float Lifetime = 1.5f;
        public float FloorZ = float.NegativeInfinity;
        public CasingKind Kind = CasingKind.Bullet;
        public Action? OnFreed;

        /// <summary>Bounce-sound hook (sample, originQuake), Base <c>Casing_Touch</c>. Null = silent.</summary>
        public Action<string, NVec3>? SoundHook;

        /// <summary>World-collision sweep hook (Base MOVETYPE_BOUNCE trace). Null = FloorZ ground-plane only.</summary>
        public Func<NVec3, NVec3, CasingTrace>? TraceHook;

        // DP world gravity (sv_gravity default). gib/casing entities use gravity 1 (full).
        private const float Gravity = 800f;
        // Base advances casings at a FIXED tic (cl_casings_ticrate, via Movetype_Physics_MatchTicrate) rather than
        // per-frame, so the bounce reflection is frame-rate independent. We accumulate real frame time and step the
        // sim in whole 0.03125s tics (catch-up clamped so a long stall can't spiral the casing through the world).
        private const float Ticrate = 0.03125f;     // cl_casings_ticrate
        private float _ticAccum;
        private float _age;
        private bool _onGround;
        // QC Casing_Touch throttles the bounce sound: this.nextthink = time + 0.2 (no sound while inside that
        // window). We accumulate elapsed life and gate on it so a casing rattling on the floor doesn't machine-gun.
        private float _nextSoundAt;

        public override void _PhysicsProcess(double delta)
        {
            // #30 slowmo/pause: the casing ballistic sim is Base CSQC (cl.time-driven) — scale by the client
            // render-time factor so casings freeze mid-air at slowmo 0 and tumble slow at fractional slowmo.
            float dt = VortexArena.Game.Client.ClientRenderTime.ScaleDelta((float)delta);
            if (dt <= 0f)
                return; // paused — hold age, position and sound state exactly where they are
            _age += dt;
            if (_age >= Lifetime)
            {
                OnFreed?.Invoke();
                QueueFree();
                return;
            }

            // Step the ballistic sim in fixed cl_casings_ticrate tics (Base Movetype_Physics_MatchTicrate), not
            // per-frame, so the bounce reflection is frame-rate independent. Clamp the catch-up so a long frame
            // stall can't tunnel the casing through the world (matches the engine's MatchTicrate clamping intent).
            if (!_onGround)
            {
                _ticAccum += dt;
                int maxTics = 4; // catch-up cap (≈125ms) — a stalled frame won't fling the casing
                while (_ticAccum >= Ticrate && maxTics-- > 0)
                {
                    _ticAccum -= Ticrate;
                    if (!StepTic(Ticrate))
                        return; // casing freed (startsolid cull)
                    if (_onGround)
                        break;
                }
                if (maxTics <= 0)
                    _ticAccum = 0f; // drop the backlog rather than chasing it
            }

            // Fade out over the final 0.4s of life.
            float remaining = Lifetime - _age;
            if (remaining < 0.4f)
            {
                float a = Math.Clamp(remaining / 0.4f, 0f, 1f);
                ApplyAlpha(this, a);
            }
        }

        /// <summary>
        /// Advance the casing one physics tic (Base MOVETYPE_BOUNCE under <c>Movetype_Physics_MatchTicrate</c>):
        /// integrate gravity, sweep the static world from the current to the next position, and on a hit reflect
        /// the velocity off the impact plane damped by <see cref="BounceFactor"/> (faithful to MOVETYPE_BOUNCE +
        /// <c>Casing_Touch</c>). When no <see cref="TraceHook"/> is wired it falls back to the legacy FloorZ
        /// ground plane. Returns false when the casing was freed (started inside a brush — Base's
        /// <c>trace_startsolid</c> cull). Tumbles via the angular velocity each tic.
        /// </summary>
        private bool StepTic(float dt)
        {
            // Gravity (Quake Z up).
            VelocityQuake.Z -= Gravity * dt;
            NVec3 fromQ = Coords.ToQuake(Position);
            NVec3 toQ = fromQ + VelocityQuake * dt;

            if (TraceHook is not null)
            {
                CasingTrace tr = TraceHook(fromQ, toQ);

                // Gun poking into a wall: the casing spawned inside solid — Base's Casing_Draw deletes it on
                // trace_startsolid rather than letting it sit embedded in the brush.
                if (tr.StartSolid)
                {
                    OnFreed?.Invoke();
                    QueueFree();
                    return false;
                }

                if (tr.Fraction < 1f && tr.Normal != NVec3.Zero)
                {
                    // Move to the impact point, then reflect the velocity off the surface and damp it
                    // (MOVETYPE_BOUNCE: v' = v - (1+bounce)*(v·n)*n).
                    NVec3 n = NVec3.Normalize(tr.Normal);
                    BounceCasingSound();

                    Position = Coords.ToGodot(tr.EndPos);
                    float vn = NVec3.Dot(VelocityQuake, n);
                    VelocityQuake -= n * ((1f + BounceFactor) * vn);
                    AngularVel *= 0.6f;

                    // Rest on a near-flat surface once it's barely moving (QC zeroes pitch/roll on ground).
                    if (n.Z > 0.7f && VelocityQuake.Length() < 20f)
                    {
                        _onGround = true;
                        VelocityQuake = default;
                    }
                }
                else
                {
                    Position = Coords.ToGodot(toQ);
                }
            }
            else
            {
                // Legacy FloorZ ground-plane bounce (no world tracer wired).
                if (!float.IsNegativeInfinity(FloorZ) && toQ.Z <= FloorZ && VelocityQuake.Z < 0f)
                {
                    BounceCasingSound();
                    toQ.Z = FloorZ;
                    VelocityQuake.Z = -VelocityQuake.Z * BounceFactor;
                    VelocityQuake.X *= 0.7f;
                    VelocityQuake.Y *= 0.7f;
                    AngularVel *= 0.6f;
                    if (VelocityQuake.Length() < 20f)
                    {
                        _onGround = true;
                        VelocityQuake = default;
                    }
                }
                Position = Coords.ToGodot(toQ);
            }

            // Tumble (QC avelocity = '0 10 0' + 100*prandomvec, a 3-axis angular velocity in rad/s here).
            if (!_onGround)
            {
                RotateObjectLocal(Vector3.Right, AngularVel.X * dt);
                RotateObjectLocal(Vector3.Up, AngularVel.Y * dt);
                RotateObjectLocal(Vector3.Back, AngularVel.Z * dt);
            }
            return true;
        }

        /// <summary>
        /// Play the Base <c>Casing_Touch</c> bounce sound: a random <c>brass*</c>/<c>casings*</c> impact when the
        /// casing hits a surface at speed (<c>vdist(velocity,>,50)</c>). Base bumps <c>nextthink = time + 0.2</c>
        /// on EVERY touch, so a casing grinding on the ground (touching every tic) keeps pushing the throttle
        /// ahead of <c>time</c> — the sound fires once on the real bounce and then stays silent, instead of
        /// re-triggering every 0.2s until the casing despawns (playtest-bugs #1). Uses the pre-bounce velocity,
        /// as the QC touch fires with the incoming velocity.
        /// </summary>
        private void BounceCasingSound()
        {
            bool ready = _age >= _nextSoundAt;
            _nextSoundAt = _age + 0.2f; // bump on EVERY touch (QC nextthink), so continuous contact self-silences
            if (ready && VelocityQuake.Length() > 50f)
                SoundHook?.Invoke(RandomImpactSound(Kind), Coords.ToQuake(Position));
        }

        /// <summary>
        /// A random casing-impact sample (Base <c>SND_BRASS_RANDOM</c> / <c>SND_CASINGS_RANDOM</c>): the shotgun
        /// shell uses <c>casings1-3</c>, every bullet casing uses <c>brass1-3</c>. Paths match the W_Sound()
        /// weapon-sound directory (<c>weapons/brass1.wav</c> etc.).
        /// </summary>
        private static string RandomImpactSound(CasingKind kind)
        {
            int n = GD.RandRange(1, 3);
            return kind == CasingKind.Shell ? $"weapons/casings{n}.wav" : $"weapons/brass{n}.wav";
        }

        private static void ApplyAlpha(Node node, float a)
        {
            // Per-INSTANCE fade (GeometryInstance3D.Transparency, Forward+): never write into the mesh-level
            // material — it's shared (the generated cylinder mesh across casings; SharedMeshCache/AssetSystem
            // for loaded models), so a material write faded every casing in lockstep. This also makes loaded
            // models actually fade (the old StandardMaterial3D-only path silently popped them).
            if (node is GeometryInstance3D gi)
                gi.Transparency = 1f - a;
            foreach (Node child in node.GetChildren())
                ApplyAlpha(child, a);
        }
    }
}
