using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Net;
using NVec3 = System.Numerics.Vector3;
using PType = VortexArena.Game.Client.ProjectileCatalog.ProjectileType;
using BodyFamily = VortexArena.Game.Client.ProjectileCatalog.BodyFamily;

namespace VortexArena.Game.Client;

/// <summary>
/// Renders networked projectile entities — the Godot successor to CSQC's <c>CSQCProjectile</c> /
/// <c>Projectile_Draw</c> path (qcsrc/client/weapons/projectile.qc). The server spawns rockets, grenades,
/// plasma, blaster/electro/crylink bolts etc. as plain <see cref="Entity"/> objects with a model, origin
/// and velocity; this turns each into a per-entity visual (a <see cref="MeshInstance3D"/> body or a glowing
/// <see cref="Sprite3D"/> for bolts) that follows the entity, oriented to its velocity.
///
/// The per-projectile <b>trail, model scale, spin and looping fly sound</b> come from
/// <see cref="ProjectileCatalog"/> — the faithful port of the <c>ENT_CLIENT_PROJECTILE</c> HANDLE table, so a
/// rocket smokes and rolls (EFFECT_TR_ROCKET + z-spin 720 + <c>rocket_fly</c> loop), electro draws a blue
/// plasma trail, crylink purple shards, the fireball is particle-only fire, the hagar rocket is scaled to
/// 0.75, etc., rather than one generic exhaust for everything.
///
/// It owns nothing about gameplay: <see cref="OnSpawn"/>/<see cref="OnUpdate"/>/<see cref="OnRemove"/> are
/// driven by <see cref="ClientWorld"/> from the entity stream, and <see cref="Process"/> interpolates each
/// live visual toward its entity's current origin every frame. Origins/velocities are Quake-space and
/// converted at the boundary with <see cref="Coords"/>.
/// </summary>
public partial class ProjectileRenderer : Node3D
{
    /// <summary>The Godot visual bound to one projectile entity.</summary>
    public sealed class Visual
    {
        public required Entity Entity;
        public required Node3D Root;          // follows the entity each frame
        public Node3D? Body;                  // the mesh/sprite that spins/tumbles (child of Root)
        public List<GpuParticles3D> Trails = new(); // continuous exhaust/glow layers (empty for trailless kinds)
        public OmniLight3D? Light;            // dynamic point light (rockets/plasma/fireball)
        public PType Type;
        public Vector3 SpinDegPerSec;         // local tumble/roll rate (QC avelocity / Projectile_Draw rot)
        public Vector3 LastPos;               // for velocity-from-motion when Entity.Velocity is unset
        public ProjectilePredictor Predictor; // CSQC Projectile_Draw: snap-to-server + local velocity extrapolation
        public string? SegmentTrail;          // faithful per-segment trail effect (DP CL_ParticleTrail); null = legacy emitters
        public NVec3 TrailPos;                // Quake-space tail of the last emitted trail segment

        /// <summary>
        /// Client clock time the entity is expected to expire (QC <c>death_time</c>) — the anchor for the electro
        /// orb's <c>(ltime-time)</c> shrink pulse. The orb's <c>death_time</c> isn't networked, so it's cached at
        /// spawn as <c>now + g_balance_electro_secondary_lifetime</c> (the orb's <c>NextThink = now + lifetime</c>
        /// in <see cref="VortexArena.Common.Gameplay.Weapons.Electro"/>). 0 = no scale pulse for this visual.
        /// </summary>
        public float DeathTime;
    }

    /// <summary>The electro secondary orb's lifetime (QC <c>g_balance_electro_secondary_lifetime</c>, default 4s) —
    /// the orb's <c>NextThink = now + lifetime</c>, used to seed <see cref="Visual.DeathTime"/> for the shrink pulse
    /// when the entity carries no networked death time.</summary>
    private const float ElectroOrbLifetime = 4f;

    private readonly Dictionary<int, Visual> _visuals = new();

    /// <summary>Reused per-frame iteration buffer so <see cref="Process"/> never allocates a snapshot list.</summary>
    private readonly List<Visual> _iterBuffer = new();

    /// <summary>Soft round glow sprite texture — identical for every energy bolt, so built once and shared.</summary>
    private static ImageTexture? _glowTexture;

    /// <summary>Dependency: the effect system, used when a projectile is removed/explodes (optional).</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>When true, projectiles cast a small dynamic light. Off saves a lot of light updates.</summary>
    [Export] public bool DynamicLights { get; set; } = true;

    /// <summary>When true, attach a looping fly sound to projectiles that have one (rocket/electro/fireball/…).</summary>
    [Export] public bool LoopSounds { get; set; } = true;

    /// <summary>
    /// When true (default), each projectile is moved by the CSQC client-side predictor — it snaps to the latest
    /// authoritative origin and extrapolates locally by velocity between snapshots, so a fired bolt leaves the
    /// muzzle at full speed with no interpolation delay or ease-in. When false, fall back to the old behaviour
    /// (exponentially ease the node toward the networked origin), kept for A/B feel-testing. Live-driven from
    /// <c>cl_projectile_prediction</c> by <see cref="ClientWorld"/>.
    /// </summary>
    public bool Predict { get; set; } = true;

    /// <summary>
    /// Resolves a QC sound sample path (e.g. "weapons/rocket_fly") to a Godot audio resource path. Host-set
    /// (VFS-backed); defaults to the <c>res://sound/&lt;sample&gt;.ogg</c> convention. A null/blank result
    /// silences the loop (graceful miss while the audio import is pending).
    /// </summary>
    public Func<string, string?>? SoundResolver { get; set; }

    /// <summary>Loads a sample straight to an <see cref="AudioStream"/> from the mounted VFS (host-set to
    /// <c>AssetLoader.LoadSound</c>). Tried before the <see cref="SoundResolver"/> <c>res://</c> fallback.</summary>
    public Func<string, AudioStream?>? AudioLoader { get; set; }

    /// <summary>
    /// Builds a fully-textured render node for a model VFS path (host-set to <c>AssetLoader.LoadModel</c>), used
    /// to draw a projectile with its REAL model — the QC <c>setmodel(MDL_PROJECTILE_*)</c> — instead of the
    /// procedural <see cref="ProjectileCatalog.BodyFamily"/> mesh. So a rocket draws models/rocket.md3 (the
    /// <c>RL</c> body plus the additive <c>RocketThrust</c> flame cone) and a grenade draws grenademodel.md3.
    /// Null (or a null return / missing content) falls back to the procedural body — keeps headless tests and
    /// asset-less runs working.
    /// </summary>
    public Func<string, Node3D?>? ModelFactory { get; set; }

    // =================================================================================================
    //  Lifecycle hooks (driven by ClientWorld)
    // =================================================================================================

    /// <summary>Begin rendering a projectile entity. Idempotent: re-spawning the same index updates in place.</summary>
    public Visual? OnSpawn(Entity entity)
    {
        if (entity is null)
            return null;
        if (_visuals.TryGetValue(entity.Index, out Visual? existing))
        {
            existing.Entity = entity;
            return existing;
        }

        PType type = ProjectileCatalog.Classify(entity);
        ProjectileCatalog.Desc desc = ProjectileCatalog.DescOf(type);
        var root = new Node3D { Name = $"proj#{entity.Index}_{type}" };

        // The real model (rocket.md3 / grenademodel.md3) when the host wired a factory and the content is
        // present, else the procedural fallback body. The model is authored Quake-forward (+X), so after the
        // loader's Coords.ToGodot it faces Godot -Z — the same axis OrientToVelocity aims the root down, and the
        // same axis ApplySpin rolls about. No extra orientation fix-up needed (the capsule's 90° tilt is not).
        Node3D body = BuildModelBody(desc) ?? BuildBody(desc);
        if (desc.ModelScale is > 0f and not 1f)
            body.Scale = Vector3.One * desc.ModelScale;
        root.AddChild(body);

        // QC Projectile_Draw tints the PROJECTILE_ROCKETMINSTA_LASER body to the firer's team palette
        // (projectile.qc:504-506: colormod = colormapPaletteColor(colormap & 0x0F) when colormap > 0). The bolt
        // networks the owner's team in Colormap → ClientEntityView puts it on Entity.Team; apply the team color to
        // the body (the real elaser model's meshes via the skin colormod, or the procedural glow sprite's modulate).
        if (type == PType.RocketMinstaLaser)
            ApplyTeamColormod(body, (int)entity.Team);

        // Trail. Preferred: the FAITHFUL per-segment path (DP CL_ParticleTrail) — each frame the follow loop
        // feeds the flight segment to the faithful sim, whose trailspacing stepping + shared accumulator
        // reproduce DP's exact trail density (the electro plasma line is one particle every 2qu — a rate-based
        // GPU emitter can't make that). Falls back to the legacy continuous GpuParticles emitters in
        // all-modern mode (cl_particles_modern 2) or when the effect isn't in the catalog.
        List<GpuParticles3D> trails;
        string? segmentTrail = null;
        if (!string.IsNullOrEmpty(desc.TrailEffect) && Effects?.UseFaithfulTrails == true)
        {
            segmentTrail = desc.TrailEffect;
            trails = new List<GpuParticles3D>();
        }
        else
        {
            trails = BuildTrails(desc, Coords.ToGodot(entity.Velocity));
            foreach (GpuParticles3D tr in trails) root.AddChild(tr);
        }

        OmniLight3D? light = (DynamicLights && desc.HasLight) ? BuildLight(desc) : null;
        // (N6) Projectile glows are the most numerous transient light in a firefight, so they go under the
        // budget like the effect flashes. Registration is a no-op outside a match (no LightBudget node).
        LightBudget.Register(light, LightBudget.Role.Dynamic);
        if (light is not null) root.AddChild(light);

        Vector3 startPos = Coords.ToGodot(entity.Origin);
        root.Position = startPos;

        AddChild(root);

        // Seed the client-side predictor from the spawn snapshot (Quake-space origin+velocity) so the very
        // first rendered frame is already at full speed (CSQC Projectile_ReceiveEntity sets origin+velocity).
        var predictor = new ProjectilePredictor();
        predictor.Spawn(entity.Origin, entity.Velocity);
        // Orient after entering the tree (LookAt works on the global transform).
        OrientToVelocity(root, entity);

        // Looping fly sound (QC loopsound): rocket/electro/fireball/seeker/vehicle rockets.
        if (LoopSounds && !string.IsNullOrEmpty(desc.LoopSound))
            AttachLoopSound(root, desc.LoopSound!);

        var visual = new Visual
        {
            Entity = entity,
            Root = root,
            Body = body,
            Trails = trails,
            Light = light,
            Type = type,
            SpinDegPerSec = desc.SpinDegPerSec,
            LastPos = startPos,
            Predictor = predictor,
            SegmentTrail = segmentTrail,
            TrailPos = entity.Origin,
            // Electro orb only: anchor the (ltime-time) shrink pulse. The orb's death_time isn't networked, so
            // cache spawn-time + g_balance_electro_secondary_lifetime (the orb's NextThink). Other types: 0 (no pulse).
            DeathTime = type == PType.Electro ? Now() + ElectroOrbLifetime : 0f,
        };
        _visuals[entity.Index] = visual;

        // Bind as the entity's presence link so the sim can find its node (mirrors EntityNode.Bind).
        entity.Presence ??= new ProjectilePresence(visual.Root);
        return visual;
    }

    /// <summary>Pull one projectile's current origin/velocity onto its visual (called per network update).</summary>
    public void OnUpdate(Entity entity)
    {
        if (entity is null || !_visuals.TryGetValue(entity.Index, out Visual? v))
            return;
        v.Entity = entity; // _Process does the actual transform follow (smooth interpolation)

        // CSQCProjectile(..., true) "change type" — the porto flips its networked type from PORTO_RED to
        // PORTO_BLUE mid-flight when a combined shot lays its in-portal and continues as the out-portal
        // (porto.qc:251). The classifier keys on the entity's EF_RED/EF_BLUE bits, so re-classify on each update
        // and recolour the bolt's glow + light when it changes, so the blue variant renders mid-flight.
        PType reclass = ProjectileCatalog.Classify(entity);
        if (reclass != v.Type)
        {
            v.Type = reclass;
            ApplyTypeGlow(v, ProjectileCatalog.DescOf(reclass));
        }
    }

    /// <summary>Recolour a live visual's body glow + dynamic light to a new type descriptor (porto red->blue swap).</summary>
    private static void ApplyTypeGlow(Visual v, ProjectileCatalog.Desc desc)
    {
        if (v.Body is Sprite3D sprite && GodotObject.IsInstanceValid(sprite))
            sprite.Modulate = desc.GlowColor;
        if (v.Light is { } light && GodotObject.IsInstanceValid(light))
            light.LightColor = desc.GlowColor;
    }

    /// <summary>Stop rendering a projectile (it hit something / expired). Optionally play an impact effect.</summary>
    public void OnRemove(Entity entity, string? impactEffect = null)
    {
        if (entity is null) return;
        OnRemove(entity.Index, entity.Origin, impactEffect);
    }

    /// <summary>Stop rendering by index; spawns <paramref name="impactEffect"/> at <paramref name="origin"/> if given.</summary>
    public void OnRemove(int index, NVec3 origin, string? impactEffect = null)
    {
        if (!_visuals.Remove(index, out Visual? v))
            return;

        // Impact effect: ONLY the explicit one a caller passes (the offline demo driver, which has no server
        // feeding it effects). On the live net path the SERVER is authoritative for impacts — every ported
        // projectile weapon emits its wr_impacteffect equivalent (ROCKET_EXPLODE, ELECTRO_IMPACT, …) through
        // EffectEmitter, networked to every client including the listen-server local one. A type-default spawn
        // here too played every impact TWICE (and "exploded" projectiles culled by round cleanup, which never
        // explode upstream).
        if (!string.IsNullOrEmpty(impactEffect))
            Effects?.Spawn(impactEffect!, origin);

        // Let each active trail layer finish emitting its tail before the node disappears (detach + linger).
        foreach (GpuParticles3D trail in v.Trails)
        {
            if (!GodotObject.IsInstanceValid(trail))
                continue;
            trail.Emitting = false;
            trail.Reparent(this, keepGlobalTransform: true);
            float linger = (float)trail.Lifetime + 0.2f;
            SceneTreeTimer t = GetTree().CreateTimer(linger);
            GpuParticles3D trailRef = trail;
            t.Timeout += () => { if (GodotObject.IsInstanceValid(trailRef)) trailRef.QueueFree(); };
        }

        if (GodotObject.IsInstanceValid(v.Root))
            v.Root.QueueFree();
    }

    /// <summary>True if a projectile entity is currently rendered.</summary>
    public bool IsTracking(int index) => _visuals.ContainsKey(index);

    /// <summary>Number of live projectile visuals (diagnostics).</summary>
    public int LiveCount => _visuals.Count;

    // =================================================================================================
    //  Per-frame follow / interpolation
    // =================================================================================================

    public override void _Process(double delta)
    {
        Process((float)delta);
    }

    /// <summary>Advance every visual toward its entity's current Quake origin (call once per frame).</summary>
    public void Process(float delta)
    {
        using var _projScope = VortexArena.Game.Client.FrameProfiler.Scope("proj"); // [profiling] projectile interp/spin
        if (_visuals.Count == 0)
            return;

        // Iterate over a snapshot since a removal could mutate the dictionary mid-loop in edge cases.
        // Reuse a persistent buffer rather than allocating a fresh List every frame.
        _iterBuffer.Clear();
        foreach (Visual v in _visuals.Values)
            _iterBuffer.Add(v);
        foreach (Visual v in _iterBuffer)
        {
            if (v.Entity.IsFreed)
            {
                OnRemove(v.Entity, null);
                continue;
            }
            if (!GodotObject.IsInstanceValid(v.Root))
                continue;

            if (Predict)
            {
                // Client-side prediction (CSQC Projectile_Draw): snap to the latest authoritative origin and
                // extrapolate locally by velocity between snapshots. v.Entity.Origin is the RAW server origin
                // (ClientEntityView feeds projectiles the un-interpolated pose for exactly this), v.Entity.Velocity
                // the networked velocity — both Quake-space. The result leaves the muzzle at full speed with no
                // interpolation delay or ease-in. Per the catalog, a detonate-on-impact flier stops at a wall and
                // a gravity-free BOUNCEMISSILE reflects off it (world-only sweep) so the bolt doesn't overrun.
                ProjectileCatalog.CollisionMode mode = ProjectileCatalog.CollisionFor(v.Type);
                NVec3 predicted = mode == ProjectileCatalog.CollisionMode.None
                    ? v.Predictor.Step(v.Entity.Origin, v.Entity.Velocity, delta)
                    : v.Predictor.Step(v.Entity.Origin, v.Entity.Velocity, delta, TraceWorldDelegate,
                        bounce: mode == ProjectileCatalog.CollisionMode.Bounce, bounceFactor: 1f);
                v.Root.Position = Coords.ToGodot(predicted);
            }
            else
            {
                // Fallback (cl_projectile_prediction 0): the old exponential ease toward the networked origin,
                // kept for A/B feel-testing. Smooths jitter but trails the true position and eases in at spawn.
                Vector3 target = Coords.ToGodot(v.Entity.Origin);
                float dist2 = v.Root.Position.DistanceSquaredTo(target);
                if (dist2 > 256f * 256f)
                    v.Root.Position = target;
                else
                    v.Root.Position = v.Root.Position.Lerp(target, Mathf.Clamp(delta * 30f, 0f, 1f));
            }

            OrientToVelocity(v.Root, v.Entity, v.Root.Position - v.LastPos);
            v.LastPos = v.Root.Position;

            // Faithful trail segment (DP CL_ParticleTrail): feed this frame's flight segment to the sim —
            // its trailspacing stepping + shared accumulator make the per-frame calls compose into the
            // exact DP trail density regardless of frame rate or projectile speed.
            if (v.SegmentTrail is not null)
            {
                NVec3 cur = Coords.ToQuake(v.Root.Position);
                NVec3 seg = cur - v.TrailPos;
                if (NVec3.Dot(seg, seg) > 0.01f)
                {
                    Effects?.SpawnTrailSegment(v.SegmentTrail, v.TrailPos, cur, v.Entity.Velocity);
                    v.TrailPos = cur;
                }
            }

            // Electro orb expiry shrink (QC electro_orb_draw, electro.qc:32: ...*bound(0,(ltime-time)*4,1)).
            // As the orb's remaining lifetime drops below 0.25s the body scales toward 0; outside that window the
            // factor stays 1 so the static scale is unchanged. Guarded on PType.Electro so no other projectile's
            // scale animates. Applied BEFORE the spin so the local roll keeps composing on the freshly-set scale.
            if (v.Type == PType.Electro && v.Body is not null && v.DeathTime > 0f
                && GodotObject.IsInstanceValid(v.Body))
            {
                float remaining = v.DeathTime - Now();
                float scaleFactor = Mathf.Clamp(remaining * 4f, 0f, 1f);
                float baseScale = ProjectileCatalog.DescOf(v.Type).ModelScale;
                if (baseScale is <= 0f or 1f) baseScale = 1f;
                v.Body.Scale = Vector3.One * (baseScale * scaleFactor);
            }

            // Spin/tumble the body locally on top of the velocity-aligned root (QC Projectile_Draw rot /
            // avelocity): rocket rolls (z), bouncing grenade tumbles sideways (y), hookbomb pitches (x), …
            if (v.Body is not null && v.SpinDegPerSec != Vector3.Zero && GodotObject.IsInstanceValid(v.Body))
                ApplySpin(v.Body, v.SpinDegPerSec, delta);
        }
    }

    /// <summary>Cached world sweep delegate fed to <see cref="VortexArena.Net.ProjectilePredictor.Step"/> so
    /// predicted projectiles collide with the map. World-only (CSQC <c>move_nomonsters = MOVE_WORLDONLY</c>):
    /// a point sweep against map geometry, ignoring entities. On a listen server this is the real BSP; on a
    /// pure client (flat-floor stub / headless) <c>Api.Services</c> is null and it reports no hit (fly straight).</summary>
    private static readonly VortexArena.Net.ProjectileWorldTrace TraceWorldDelegate = TraceWorld;

    private static VortexArena.Net.ProjectileTraceHit TraceWorld(NVec3 start, NVec3 end)
    {
        if (VortexArena.Common.Services.Api.Services is null)
            return new VortexArena.Net.ProjectileTraceHit(false, end, default);
        VortexArena.Common.Services.TraceResult tr = VortexArena.Common.Services.Api.Trace.Trace(
            start, NVec3.Zero, NVec3.Zero, end, MoveFilter.WorldOnly, null);
        return tr.Fraction < 1f
            ? new VortexArena.Net.ProjectileTraceHit(true, tr.EndPos, tr.PlaneNormal)
            : new VortexArena.Net.ProjectileTraceHit(false, end, default);
    }

    /// <summary>Current client clock time (mirrors <c>ClientWorld.Now</c>): the simulation clock the electro orb's
    /// cached <see cref="Visual.DeathTime"/> is measured against. 0 in a headless/clockless harness — there the
    /// orb's <see cref="Visual.DeathTime"/> is also seeded from this same 0, so the shrink stays inert (factor 1).</summary>
    private static float Now()
        => VortexArena.Common.Services.Api.Services?.Clock?.Time ?? 0f;

    private static void ApplySpin(Node3D body, Vector3 spinDegPerSec, float delta)
    {
        if (spinDegPerSec.X != 0f) body.RotateObjectLocal(Vector3.Right, Mathf.DegToRad(spinDegPerSec.X * delta));
        if (spinDegPerSec.Y != 0f) body.RotateObjectLocal(Vector3.Up, Mathf.DegToRad(spinDegPerSec.Y * delta));
        if (spinDegPerSec.Z != 0f) body.RotateObjectLocal(Vector3.Back, Mathf.DegToRad(spinDegPerSec.Z * delta));
    }

    // =================================================================================================
    //  Node construction (driven by the catalog descriptor)
    // =================================================================================================

    /// <summary>
    /// Render the projectile's REAL model (<see cref="ProjectileCatalog.Desc.ModelPath"/>) via the host
    /// <see cref="ModelFactory"/>, or null to fall back to the procedural <see cref="BuildBody"/>. The models
    /// are authored Quake-forward (+X — the rocket/grenade meshes are ~2× longer on Quake X), so after the
    /// loader's per-vertex <c>Coords.ToGodot</c> their nose points Godot <b>+X</b>. <see cref="OrientToVelocity"/>
    /// therefore aims the root's +X down the velocity, and <see cref="ApplySpin"/> rolls about +X (the nose).
    /// </summary>
    private Node3D? BuildModelBody(ProjectileCatalog.Desc desc)
    {
        if (string.IsNullOrEmpty(desc.ModelPath) || ModelFactory is null)
            return null;
        // [crash fix 2026-07-26] one built tree per projectile model EVER; every projectile shares its
        // mesh (SharedMeshCache) — the per-spawn MD3 ArrayMesh was residual finalizer churn (#3). The
        // procedural bodies below already share via _bodyResCache; this brings the MD3 family in line.
        MeshInstance3D? model = SharedMeshCache.Instantiate(desc.ModelPath!, () => ModelFactory(desc.ModelPath!));
        if (model is null)
            return null;
        model.Name = "Body";
        return model;
    }

    // Per-(family,color) cache of the procedural body's Mesh + StandardMaterial3D. Every projectile of a given
    // type draws an IDENTICAL solid body (radius/color derive from the catalog Desc, nothing per-instance), so
    // the heavy Resources are built once and SHARED across instances (Godot lets many MeshInstance3D reference
    // the same Mesh/material) — only the lightweight MeshInstance3D node is per-projectile. Mirrors the
    // _trailResCache / _glowTexture pattern; removes the CapsuleMesh/SphereMesh + StandardMaterial3D allocs per
    // spawn (and the first-of-a-color pipeline compile churn) in a firefight.
    private static readonly Dictionary<(BodyFamily, int), (Mesh Mesh, StandardMaterial3D Mat)> _bodyResCache = new();

    /// <summary>Shared solid-body Mesh + material for a (family, color) — built once, reused across instances.</summary>
    private static (Mesh Mesh, StandardMaterial3D Mat) BodyMeshRes(BodyFamily family, Color color)
    {
        var key = (family, ColorKey5(color));
        if (_bodyResCache.TryGetValue(key, out (Mesh Mesh, StandardMaterial3D Mat) res))
            return res;
        // Rocket = elongated capsule along forward (Godot -Z); grenade = sphere.
        bool rocket = family == BodyFamily.RocketMesh;
        Mesh mesh = rocket
            ? new CapsuleMesh { Radius = 2.0f, Height = 12f }
            : new SphereMesh { Radius = 3f, Height = 6f };
        var mat = new StandardMaterial3D { AlbedoColor = color, Metallic = 0.4f, Roughness = 0.6f };
        res = (mesh, mat);
        _bodyResCache[key] = res;
        return res;
    }

    /// <summary>Quantise a color to a 5-bit-per-channel cache key (matches <c>Decals.SolidTexture</c>) so a
    /// continuum of tints collapses to a bounded set of shared resources.</summary>
    private static int ColorKey5(Color c)
        => ((int)(c.R * 31) << 10) | ((int)(c.G * 31) << 5) | (int)(c.B * 31);

    private static Node3D BuildBody(ProjectileCatalog.Desc desc)
    {
        Color color = desc.GlowColor;

        switch (desc.Body)
        {
            case BodyFamily.RocketMesh:
            case BodyFamily.GrenadeMesh:
            {
                // A small solid body, built once per (family,color) and shared across instances.
                bool rocket = desc.Body == BodyFamily.RocketMesh;
                (Mesh mesh, StandardMaterial3D mat) = BodyMeshRes(desc.Body, color);
                var mi = new MeshInstance3D { Name = "Mesh", Mesh = mesh, MaterialOverride = mat };
                if (!rocket)
                    return mi; // sphere: symmetric, spin acts directly on it
                // Capsule's long axis is +Y; lay it along the body's NOSE axis (Godot +X) to match the real
                // rocket model, so OrientToVelocity (+X → velocity) points it the right way. Keep that tilt on a
                // CHILD so the returned body stays identity and ApplySpin's +X roll stays a clean barrel-roll
                // about the nose (symmetric capsule → invisible) rather than a transverse tumble.
                mi.RotationDegrees = new Vector3(0f, 0f, -90f);
                var holder = new Node3D { Name = "Body" };
                holder.AddChild(mi);
                return holder;
            }
            default: // GlowSprite / FireSprite — a bright additive billboard (no real model needed).
            {
                var sprite = new Sprite3D
                {
                    Name = "Glow",
                    Texture = _glowTexture ??= MakeGlowTexture(),
                    Modulate = color,
                    Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                    Shaded = false,
                    DoubleSided = true,
                    PixelSize = desc.Body == BodyFamily.FireSprite ? 0.4f : 0.25f,
                    NoDepthTest = false,
                    AlphaCut = SpriteBase3D.AlphaCutMode.Disabled,
                    TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
                };
                return sprite;
            }
        }
    }

    /// <summary>
    /// Tint a projectile body to a team palette colormod — the QC <c>ROCKETMINSTA_LASER</c> case in
    /// <c>Projectile_Draw</c> (projectile.qc:504-506: <c>colormod = colormapPaletteColor(colormap &amp; 0x0F)</c>
    /// when <c>colormap &gt; 0</c>). A real model body (the elaser meshes) routes through the skin-shader
    /// colormod (<see cref="ModelTint.SetColormod"/> over its <see cref="MeshInstance3D"/>s); the procedural glow
    /// sprite multiplies its <see cref="Sprite3D.Modulate"/> by the team color. A colorless map (team 0 — FFA /
    /// unknown) is left untinted, matching the QC <c>colormap &gt; 0</c> guard.
    /// </summary>
    private static void ApplyTeamColormod(Node3D body, int colormap)
    {
        if (body is null)
            return;
        Color team = ModelTint.TeamColor(colormap, out bool hasTeam);
        if (!hasTeam)
            return; // QC: only tints when colormap > 0
        // Procedural glow sprite: multiply its modulate (the bolt billboard) by the team color.
        if (body is Sprite3D sprite)
        {
            sprite.Modulate *= team;
            return;
        }
        // Real model body: drive the skin-shader colormod uniform across its meshes (plain-material meshes
        // ignore the unset uniform — harmless, exactly as for player models).
        var meshes = new List<MeshInstance3D>();
        CollectMeshes(body, meshes);
        if (meshes.Count > 0)
            ModelTint.SetColormod(meshes, team);
    }

    /// <summary>Depth-first collect every <see cref="MeshInstance3D"/> at or under <paramref name="node"/>.</summary>
    private static void CollectMeshes(Node node, List<MeshInstance3D> dst)
    {
        if (node is MeshInstance3D mi)
            dst.Add(mi);
        foreach (Node child in node.GetChildren())
            CollectMeshes(child, dst);
    }

    // Per-projectile-type cache of the trail's ParticleProcessMaterial + DrawPass mesh. Every projectile of a
    // given type produces an IDENTICAL trail — all params derive from the catalog Desc, nothing per-instance — so
    // the heavy Resources are built once and SHARED across instances (Godot lets many GpuParticles3D reference the
    // same ProcessMaterial/mesh). Only the lightweight GpuParticles3D node is per-projectile. Removes ~5 Resource
    // allocations per projectile spawn (proc mat + gradient + ramp texture + quad + mesh material) in a firefight.
    private readonly System.Collections.Generic.Dictionary<PType, (ParticleProcessMaterial Proc, Mesh Mesh)> _trailResCache = new();

    /// <summary>
    /// The trail layers for a projectile (T5): the REAL effectinfo trail (a rocket's grey smoke + backward
    /// orange fire core + sparks) when the catalog names one and the atlas is mounted, else the legacy single
    /// hand-tuned emitter. <paramref name="initialVelocityGodot"/> seeds velocity-inheriting blocks (the fire
    /// core that streams backward out of the nozzle).
    /// </summary>
    private List<GpuParticles3D> BuildTrails(ProjectileCatalog.Desc desc, Vector3 initialVelocityGodot)
    {
        // Prefer the faithful layered effectinfo trail. Pass no tint so the file's own per-layer colors win.
        if (Effects is not null && !string.IsNullOrEmpty(desc.TrailEffect))
        {
            List<GpuParticles3D>? layers = Effects.BuildProjectileTrailEmitters(desc.TrailEffect!, initialVelocityGodot);
            if (layers is { Count: > 0 })
                return layers;
        }
        // Fallback: the legacy single emitter (also covers trail names absent from effectinfo, e.g. the Generic
        // catch-all). Returns an empty list for trailless kinds (Blaster/HLAC).
        var list = new List<GpuParticles3D>();
        GpuParticles3D? legacy = BuildTrail(desc);
        if (legacy is not null) list.Add(legacy);
        return list;
    }

    private GpuParticles3D? BuildTrail(ProjectileCatalog.Desc desc)
    {
        if (desc.Trail is not { } c)
            return null;

        var p = new GpuParticles3D
        {
            Name = "Trail",
            Amount = Math.Max(1, c.Amount),
            Lifetime = c.Life,
            OneShot = false,
            Emitting = true,
            LocalCoords = false, // emit in world space so the trail stays behind the moving projectile
            Explosiveness = 0f,
            // World-space (LocalCoords=false) particles are LEFT BEHIND the fast-moving emitter, far outside the
            // node's auto AABB — so Godot frustum-culls the whole trail the instant the emitter's tiny AABB
            // leaves view, making the trail vanish/flicker. Pin a generous box so the smoke stays drawn.
            VisibilityAabb = new Aabb(new Vector3(-256f, -256f, -256f), new Vector3(512f, 512f, 512f)),
        };

        if (!_trailResCache.TryGetValue(desc.Type, out (ParticleProcessMaterial Proc, Mesh Mesh) res))
        {
            var mat = new ParticleProcessMaterial
            {
                // A non-zero direction is required for the spread cone to produce any spawn velocity — Direction
                // Zero leaves the puffs pinned at the emit point. Drift them gently upward like real exhaust smoke.
                Direction = Vector3.Up,
                Spread = 25f,
                InitialVelocityMin = 2f,
                InitialVelocityMax = 10f,
                Gravity = new Vector3(0f, c.Gravity, 0f),
                ScaleMin = c.Scale * 0.5f,
                ScaleMax = c.Scale,
                // T2: the tint lives ONLY in the color ramp here; the base Color stays White so it isn't
                // multiplied a second time (and the mesh AlbedoColor below is White too).
                Color = Colors.White,
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Point,
            };
            var ramp = new Gradient();
            ramp.SetColor(0, c.Color);
            ramp.SetColor(1, new Color(c.Color.R, c.Color.G, c.Color.B, 0f));
            mat.ColorRamp = new GradientTexture1D { Gradient = ramp };

            // Apply the real particlefont atlas sprite when the effectinfo catalog is mounted.
            // The sprite defines the particle SHAPE (smoke wisps for TR_ROCKET, sparkle dots for TR_NEXUIZPLASMA,
            // fire blobs for fireball trails, etc.) while c.Color continues to tint it — matching the
            // StandardMaterial3D formula: albedo_texture × albedo_color × vertex_color_from_ramp.
            Texture2D? sprite = !string.IsNullOrEmpty(desc.TrailEffect)
                ? Effects?.QueryTrailSprite(desc.TrailEffect)
                : null;

            var quad = new QuadMesh { Size = new Vector2(1f, 1f) };
            var meshMat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = c.Additive ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                // Particle billboarding discards per-particle scale unless this is set (godot#74897) — without it
                // the trail puffs collapsed to a 1×1 dot and the rocket/grenade flew with no visible smoke.
                BillboardKeepScale = true,
                VertexColorUseAsAlbedo = true,
                AlbedoColor = Colors.White, // T2: tint via the ramp/vertex color only (de-compounded)
            };
            if (sprite is not null)
            {
                meshMat.AlbedoTexture = sprite;
                meshMat.TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear;
            }
            quad.Material = meshMat;

            res = (mat, quad);
            _trailResCache[desc.Type] = res;
        }

        p.ProcessMaterial = res.Proc;
        p.DrawPass1 = res.Mesh;
        return p;
    }

    /// <summary>
    /// Pre-build the shared per-type trail Resources for every projectile type at map-load, so the first rocket/
    /// plasma/grenade of a match doesn't construct its trail material + gradient on its render frame. Idempotent
    /// (the cache is keyed by type); the throwaway emitter node is freed immediately. (Note: this amortizes the
    /// CPU-side construction — the GPU shader pipeline still compiles on the trail's first actual draw.)
    /// </summary>
    public void WarmupTrails()
    {
        // Build the per-type warm instances to populate the shared Resource caches, then free them (the cache
        // persists). The GPU warm pass (A2) instead RENDERS the same instances before freeing them.
        foreach (Node3D n in BuildWarmupInstances())
            n.QueueFree();
    }

    /// <summary>
    /// Build one hidden body + trail-layer set per projectile type for the offscreen GPU warm pass (A2,
    /// <see cref="GpuWarmPass"/>). Each references the SAME cached Resources a real projectile uses
    /// (<c>_bodyResCache</c> / the effectinfo trail materials), so rendering them once offscreen compiles the
    /// trail + body draw pipelines — the first rocket/plasma/grenade in play then hits a warm GPU. Both the
    /// procedural body AND the real model body (when <see cref="ModelFactory"/> is wired) are warmed: the
    /// bolt models (laser.mdl / elaser.mdl / hlac_bullet.md3) compile brand-new autosprite-deform
    /// ShaderMaterials that only exist on the model path, so warming just the procedural stand-in would
    /// leave the first blaster/electro shot to eat the pipeline compile. Nodes are NOT parented here; the
    /// warm pass owns, renders, and frees them.
    /// </summary>
    public List<Node3D> BuildWarmupInstances()
    {
        // A nominal forward velocity so the effectinfo path's velocity-inheriting blocks build their materials.
        Vector3 nominal = new(0f, 0f, -1200f);
        var list = new List<Node3D>();
        var warmedModels = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (PType t in System.Enum.GetValues<PType>())
        {
            ProjectileCatalog.Desc d = ProjectileCatalog.DescOf(t);
            list.Add(BuildBody(d));
            // The real model body, deduped by path (elaser.mdl serves two types). Null-safe: headless /
            // asset-less runs have no ModelFactory and skip straight past.
            if (!string.IsNullOrEmpty(d.ModelPath) && warmedModels.Add(d.ModelPath!)
                && BuildModelBody(d) is { } model)
                list.Add(model);
            foreach (GpuParticles3D e in BuildTrails(d, nominal))
                list.Add(e);
        }
        return list;
    }

    private OmniLight3D BuildLight(ProjectileCatalog.Desc desc)
    {
        // DP attaches the trail effect's no-fade light block to the mover every frame (cl_particles.c:1645
        // "glowing entity") — e.g. TR_NEXUIZPLASMA carries 'lightradius 90, lightcolor 1.5 3 6', the tight
        // saturated blue glow that paints the walls as an electro bolt flies. Use those exact values when the
        // effectinfo defines them (same HDR mapping as EffectSystem.SpawnInfoLight: unit hue × energy); the
        // catalog GlowColor stays the fallback for trailless/unparsed types.
        if (!string.IsNullOrEmpty(desc.TrailEffect) && Effects?.TrailLightFor(desc.TrailEffect) is { } tl)
        {
            float maxc = MathF.Max(1f, MathF.Max(tl.Color.X, MathF.Max(tl.Color.Y, tl.Color.Z)));
            return new OmniLight3D
            {
                Name = "Light",
                LightColor = new Color(tl.Color.X / maxc, tl.Color.Y / maxc, tl.Color.Z / maxc),
                LightEnergy = MathF.Min(8f, maxc),
                OmniRange = Math.Clamp(tl.Radius, 1f, 2000f),
                ShadowEnabled = false,
            };
        }
        return new OmniLight3D
        {
            Name = "Light",
            LightColor = desc.GlowColor,
            LightEnergy = desc.Type is PType.Rocket or PType.Rpc ? 2.0f : 1.4f,
            OmniRange = desc.Body == BodyFamily.FireSprite ? 220f : 160f,
            ShadowEnabled = false,
        };
    }

    /// <summary>Attach a looping spatial fly sound to the projectile root (QC <c>loopsound</c>). Graceful miss.</summary>
    private void AttachLoopSound(Node3D root, string sample)
    {
        AudioStream? stream = LoadLoopStream(sample);
        if (stream is null)
            return;

        var player = new AudioStreamPlayer3D
        {
            Name = "FlySound",
            Stream = stream,
            MaxDistance = 2048f,
            Autoplay = true,
        };
        // Restart on finish so short samples keep looping for the projectile's life (cheap, no glitch for
        // seamless loops; imported .ogg loop points are honored when present).
        player.Finished += () => { if (GodotObject.IsInstanceValid(player)) player.Play(); };
        root.AddChild(player);
    }

    /// <summary>Resolve a loop sample to a stream: the VFS <see cref="AudioLoader"/> first, then the
    /// <see cref="SoundResolver"/> <c>res://</c> fallback. Null = silent (graceful miss).</summary>
    private AudioStream? LoadLoopStream(string sample)
    {
        AudioStream? stream = AudioLoader?.Invoke(sample);
        if (stream is not null)
            return stream;

        string? resPath = SoundResolver is not null ? SoundResolver(sample) : $"res://sound/{sample}.ogg";
        if (string.IsNullOrEmpty(resPath) || !ResourceLoader.Exists(resPath))
            return null;
        try { return ResourceLoader.Load<AudioStream>(resPath); }
        catch { return null; }
    }

    /// <summary>Build a soft round glow texture for energy-bolt sprites (radial alpha falloff).</summary>
    private static ImageTexture MakeGlowTexture()
    {
        const int size = 32;
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        float c = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = (x - c) / c, dy = (y - c) / c;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            float a = Mathf.Clamp(1f - d, 0f, 1f);
            a *= a; // sharpen the core
            img.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>
    /// Aim the root so the body's NOSE (Godot +X — see <see cref="BuildModelBody"/>) points down the entity's
    /// velocity (the QC <c>angles = vectoangles(velocity)</c>). We can't use <see cref="Node3D.LookAt"/> (it
    /// aims -Z, a 90° miss for these +X-forward models — the old sideways-tumble bug); instead build an
    /// orthonormal basis with +X along velocity and a world-up-stable cross for the other two axes. The body's
    /// accumulated roll lives on the child, so re-setting the root basis here every frame never wipes the spin.
    /// </summary>
    private static void OrientToVelocity(Node3D root, Entity e, Vector3? motionFallback = null)
    {
        Vector3 vel = Coords.ToGodot(e.Velocity);
        if (vel.LengthSquared() < 1f && motionFallback is { } m && m.LengthSquared() > 1e-4f)
            vel = m;
        if (vel.LengthSquared() < 1e-4f)
            return;
        Vector3 x = vel.Normalized();                                                   // nose → velocity
        Vector3 upRef = Mathf.Abs(x.Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up; // avoid degeneracy
        Vector3 z = x.Cross(upRef).Normalized();                                         // a transverse axis
        Vector3 y = z.Cross(x).Normalized();                                             // completes RH frame
        root.Basis = new Basis(x, y, z); // columns (X,Y,Z); x×y = z, orthonormal — preserves root.Position
    }

    /// <summary>Presence link for a projectile entity (so the sim can reach its node, like EntityNode).</summary>
    private sealed class ProjectilePresence : IEntityPresence
    {
        public Node3D Node { get; }
        public ProjectilePresence(Node3D node) => Node = node;
    }
}
