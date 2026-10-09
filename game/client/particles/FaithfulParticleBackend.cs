using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;
using VortexArena.Common.Services;
using VortexArena.Engine.Particles;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client.Particles;

// =====================================================================================================
//  Faithful particle BACKEND facade (planning/particles-dual-system.md §C.1/§C.4). A Node3D that owns:
//
//    * a ParticleSim (the Godot-free CPU simulation, src/VortexArena.Engine/Particles/ParticleSim.cs),
//    * a FaithfulParticleRenderer child (the 3 blend-keyed MultiMesh batches).
//
//  The EffectSystem routes "original"-styled spawns here (the perfect-parity path). This facade is the
//  ONLY place that bridges the parsed game-side effectinfo model (EffectInfoEmitter, game/client/
//  EffectInfoParticle.cs) to the sim-facing snapshot (ParticleEmitterInfo, Engine/Particles): it converts
//  each emitter block 1:1 (the field sets + enum orders match by design) and caches the converted list
//  per EffectInfoEmitter-list so repeated spawns of the same effect don't re-allocate.
//
//  Per frame: advance the sim by the game clock, then push the live pool into the renderer using the
//  active camera's transform for the view origin/forward (Quake space — the renderer converts).
//
//  STAIN BRIDGE: collisions that leave a mark happen INSIDE the sim (it's headless, cl_particles.c:2996-
//  3042). The sim cannot reference Godot's Decals, so it surfaces marks as a callback the orchestrator
//  wires to this facade's <see cref="ForwardStain"/> (the sim's stain output -> ForwardStain). This facade
//  then drives the Godot Decals subsystem (projected scorch / blood splat) with the particlefont decal
//  cells. The Quake-space, Godot-free <see cref="ParticleStainEvent"/> is the payload on that seam.
// =====================================================================================================

/// <summary>
/// One stain mark raised by the faithful sim when a colliding/blood particle leaves a decal
/// (cl_particles.c:2996-3042) — the Godot-free payload the backend forwards to <see cref="Decals"/>.
/// All vectors are Quake space; color components are linear 0..1; <see cref="Alpha"/> is 0..1.
/// </summary>
public readonly struct ParticleStainEvent
{
    /// <summary>Hit position (Quake space) — the decal center.</summary>
    public readonly NVec3 Origin;
    /// <summary>Projection direction (Quake space): the impact velocity dir, or the inverse surface normal.
    /// Ignored when <see cref="Projected"/> is true (Decals raycasts for the surface instead).</summary>
    public readonly NVec3 Direction;
    /// <summary>Decal half-size in world units (effectinfo <c>stainsize</c> * particle size).</summary>
    public readonly float Radius;
    /// <summary>Linear tint 0..1 (DP staincolor * particle color, already de-inverted by the sim).</summary>
    public readonly float ColorR, ColorG, ColorB;
    /// <summary>Opacity 0..1 (DP stainalpha * particle alpha).</summary>
    public readonly float Alpha;
    /// <summary>Particlefont atlas cell for the decal sprite (DP staintex index).</summary>
    public readonly int DecalTexNum;
    /// <summary>True for a point-effect immediatebloodstain / scatter mark with no precomputed hit surface:
    /// the backend calls <see cref="Decals.SpawnProjected"/> (raycast for the nearest surface). False for a
    /// collision mark that already has a hit point + direction (straight <see cref="Decals.Spawn"/>).</summary>
    public readonly bool Projected;
    /// <summary>Max ray distance for the projected form (effectinfo <c>originjitter[0]</c>); unused otherwise.</summary>
    public readonly float MaxDist;

    /// <summary>True for a blood mark with no explicit staintex — the splat picks a random blooddecal cell
    /// and applies DP's blood decal scale/alpha (cl_particles.c:3033).</summary>
    public readonly bool IsBlood;

    public ParticleStainEvent(NVec3 origin, NVec3 direction, float radius,
        float colorR, float colorG, float colorB, float alpha, int decalTexNum,
        bool projected = false, float maxDist = 0f, bool isBlood = false)
    {
        Origin = origin;
        Direction = direction;
        Radius = radius;
        ColorR = colorR; ColorG = colorG; ColorB = colorB;
        Alpha = alpha;
        DecalTexNum = decalTexNum;
        Projected = projected;
        MaxDist = maxDist;
        IsBlood = isBlood;
    }
}

/// <summary>The faithful CPU particle backend: ParticleSim + MultiMesh renderer behind a spawn facade.</summary>
public sealed partial class FaithfulParticleBackend : Node3D
{
    private readonly ParticleSim _sim = new(new XorShiftParticleRng());
    private readonly ParticleSimRunner _runner;
    private FaithfulParticleRenderer _renderer = null!;

    /// <summary>
    /// (2026-10-08) The frame's spawns, update, cull, sort and pack run on a thread of their own, from where
    /// this node's <c>_Process</c> used to run the update until the frame is about to be drawn; the main thread
    /// records the spawns, goes on with the nodes that follow and only hands the packed buffer to the engine.
    /// <see cref="ParticleSimRunner"/> says why the particles come out the same (and
    /// <c>ParticleSimRunnerTests</c> holds it to that). False - the environment variable
    /// VORTEX_PARTICLES_WORKER=0 - runs everything in place as before.
    /// </summary>
    public static bool UseWorker = System.Environment.GetEnvironmentVariable("VORTEX_PARTICLES_WORKER") != "0";

    public FaithfulParticleBackend()
    {
        _runner = new ParticleSimRunner(_sim) { Threaded = UseWorker };
        _packOnWorker = PackOnWorker;
    }

    // What the pack needs, set by the main thread before the worker is started.
    private readonly Action _packOnWorker;
    private NVec3 _packViewOrigin, _packViewForward;
    private float _packTime;
    private FaithfulParticleRenderer.SyncSettings _packSettings;
    private bool _uploadPending;
    private ulong _advancedFrame = ulong.MaxValue;

    /// <summary>A tracer over the same world as the simulation's, for the main thread (the simulation's own is
    /// the worker's while an update runs). Same geometry, so the same answers.</summary>
    public ITraceService? MainThreadTrace { get; private set; }

    // Cache: one converted ParticleEmitterInfo list per source EffectInfoEmitter list (identity-keyed, so
    // the EffectSystem's stable per-effect block lists map to a stable converted snapshot — no per-spawn
    // allocation). ConditionalWeakTable lets the cache entry die with the source list.
    private readonly ConditionalWeakTable<IReadOnlyList<EffectInfoEmitter>, ParticleEmitterInfo[]> _convertCache = new();

    /// <summary>The particlefont atlas — injected by the orchestrator from EffectSystem.Font. Drives both
    /// the renderer's atlas build and the stain decal-cell lookup.</summary>
    public ParticleFont? Font { get; private set; }

    /// <summary>The projected-decal subsystem — injected from EffectSystem.Decals. Receives stain events.</summary>
    public Decals? Decals { get; private set; }

    /// <summary>The faithful surface-splat decal system (DP R_DecalSystem) — preferred over <see cref="Decals"/>
    /// for every particle mark when wired (multiplicative, surface-conforming; see DecalSplats).</summary>
    public DecalSplats? Splats { get; private set; }

    /// <summary>Wire the splat decal system (orchestrator). Marks route here instead of the Godot-Decal path.</summary>
    public void SetSplats(DecalSplats? splats) => Splats = splats;

    /// <summary>
    /// Wire the particle sim's collision tracer (orchestrator, at map load). Pass the CLIENT's static,
    /// world-only <see cref="VortexArena.Engine.Collision.TraceService"/> over the per-map collision world
    /// so bounces/content checks clip the static map BSP only — never the live SERVER world (which on a
    /// listen server cost a box-sweep of the whole entity broadphase under the tick lock per bouncing
    /// particle). See <see cref="ParticleSim.Trace"/>. Null reverts to the ambient Api.Trace.
    /// </summary>
    public void SetTrace(ITraceService? trace, ITraceService? mainThreadTrace = null)
    {
        Finish(flush: true);
        _sim.Trace = trace;
        MainThreadTrace = mainThreadTrace ?? trace;
    }

    /// <summary>
    /// Raised for an effectinfo <c>orientation beam</c> block spawned as a trail (the bullet tracer, the
    /// vaporizer and misc_laser beams): DarkPlaces stores one beam particle there, this pool holds sprites
    /// only, so the effect system draws it with its beam renderer. Set before the first spawn.
    /// </summary>
    public Action<BeamEvent>? OnBeam
    {
        get => _runner.OnBeam;
        set => _runner.OnBeam = value;
    }

    /// <summary>The live simulation (exposed for stats/HUD and the parity harness; do not mutate). Asking for
    /// it ends an update still running on the worker, so what is read is whole.</summary>
    public ParticleSim Sim
    {
        get
        {
            Finish(flush: true);
            return _sim;
        }
    }

    /// <summary>Warm-pass nodes for the renderer's MultiMesh pipelines (§11 R1) — empty before _Ready.</summary>
    public System.Collections.Generic.List<Node3D> BuildWarmupInstances()
        => _renderer is not null ? _renderer.BuildWarmupInstances() : new System.Collections.Generic.List<Node3D>();

    public override void _Ready()
    {
        _renderer = new FaithfulParticleRenderer { Name = "FaithfulRenderer" };
        if (_pendingCvars is not null)
            _renderer.Cvars = _pendingCvars;
        AddChild(_renderer);
        if (Font is not null)
            _renderer.BuildAtlas(Font);

        // STAIN BRIDGE: the headless sim raises Engine-side StainEvents on surface impact / immediate blood
        // stain (it can't touch Godot's Decals); adapt each to the Godot-free ParticleStainEvent and forward.
        // Color bytes -> linear 0..1; alpha is 0..1 for stains, 0..255 for the blood-no-staintex path -> map
        // both robustly; a negative texnum (blood picks a decal) falls back to the blood-decal cell band.
        _runner.OnStain = ev => ForwardStain(new ParticleStainEvent(
            ev.Org, ev.Dir, ev.Size,
            ev.ColorR / 255f, ev.ColorG / 255f, ev.ColorB / 255f,
            ev.Alpha > 1.5f ? ev.Alpha / 255f : ev.Alpha,
            ev.TexNum,
            ev.Projected, ev.MaxDist, ev.IsBlood));

        // The update started in _Process is ended, and its buffer handed over, just before the frame is drawn.
        _preDraw = Callable.From(OnFramePreDraw);
        RenderingServer.Singleton.Connect(RenderingServer.SignalName.FramePreDraw, _preDraw);
    }

    private Callable _preDraw;

    public override void _ExitTree()
    {
        Finish();
        if (RenderingServer.Singleton.IsConnected(RenderingServer.SignalName.FramePreDraw, _preDraw))
            RenderingServer.Singleton.Disconnect(RenderingServer.SignalName.FramePreDraw, _preDraw);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _runner.Dispose();
        base.Dispose(disposing);
    }

    private void OnFramePreDraw()
    {
        if (!_runner.InFlight && !_uploadPending) return;
        using var _scope = VortexArena.Common.Diagnostics.Prof.Sample("particles.join");
        Finish();
    }

    /// <summary>
    /// Ends the run the worker is on, if any: waits for it, delivers the marks it raised and hands the packed
    /// instances to the engine. With <paramref name="flush"/> the spawns recorded since are applied as well:
    /// everything that reads or changes the simulation from the main thread asks for that first.
    /// </summary>
    private void Finish(bool flush = false)
    {
        if (_runner.InFlight)
        {
            long began = VortexArena.Game.Legacy.LegacyPerfLog.Stamp();
            _runner.Join();
            VortexArena.Game.Legacy.LegacyPerfLog.Extra(VortexArena.Game.Legacy.LegacyPerfLog.XParticleWait, began);
        }
        if (flush) _runner.Flush();
        if (_uploadPending)
        {
            _uploadPending = false;
            _renderer.Upload();
        }
    }

    private void PackOnWorker()
    {
        long began = VortexArena.Game.Legacy.LegacyPerfLog.Stamp();
        _renderer.Pack(_sim.Pool, _sim.HighWater, _packViewOrigin, _packViewForward, _packTime, _packSettings);
        VortexArena.Game.Legacy.LegacyPerfLog.Extra(VortexArena.Game.Legacy.LegacyPerfLog.XParticleSync, began);
    }

    /// <summary>Set the particlefont (orchestrator wiring). Rebuilds the renderer atlas if already ready.</summary>
    public void SetFont(ParticleFont? font)
    {
        Finish();
        Font = font;
        if (_renderer is not null && font is not null)
            _renderer.BuildAtlas(font);
    }

    /// <summary>Set the projected-decal subsystem (orchestrator wiring) for stain forwarding.</summary>
    public void SetDecals(Decals? decals) => Decals = decals;

    /// <summary>Set the CLIENT cvar store (MenuState.Cvars) the sim + renderer read cl_particles* from. MUST
    /// be the client store — on a listen server Api.Cvars is the server store and cl_particles reads 0, which
    /// gates every spawn off (no particles render at all). Propagates to the sim and the renderer.</summary>
    public void SetCvars(VortexArena.Common.Services.ICvarService cvars)
    {
        Finish();
        _pendingCvars = cvars;
        _sim.Cvars = cvars;
        if (_renderer is not null)
            _renderer.Cvars = cvars;
    }
    private VortexArena.Common.Services.ICvarService? _pendingCvars;

    // ---------------------------------------------------------------------------------------------
    //  Spawn API
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Spawn a POINT effect: every block of one effect fires at <paramref name="origin"/> with the supplied
    /// emit <paramref name="velocity"/> (CL_NewParticlesFromEffectinfo with originmins==originmaxs and
    /// velmins==velmaxs). <paramref name="count"/> is DP's <c>pcount</c> (quality/countmultiplier applied
    /// inside the sim). Coordinates are Quake space (the sim's space).
    /// </summary>
    public void Spawn(IReadOnlyList<EffectInfoEmitter> blocks, NVec3 origin, NVec3 velocity, float count,
        uint tintRgba = 0xFFFFFFFFu)
    {
        if (blocks is null || blocks.Count == 0)
            return;
        ParticleEmitterInfo[] converted = Convert(blocks);
        _runner.Spawn(converted, count, origin, origin, velocity, velocity, tintRgba, wantTrail: false);
    }

    /// <summary>
    /// Spawn a TRAIL effect along the segment <paramref name="start"/> -> <paramref name="end"/> (DP passes
    /// the endpoints in originmins/originmaxs; trail blocks step along it by traillen/trailspacing). The
    /// supplied <paramref name="velocity"/> is the emit velocity passed through velmins==velmaxs.
    /// <paramref name="count"/> is DP's <c>pcount</c>.
    /// </summary>
    public void Trail(IReadOnlyList<EffectInfoEmitter> blocks, NVec3 start, NVec3 end, NVec3 velocity, float count,
        uint tintRgba = 0xFFFFFFFFu)
    {
        if (blocks is null || blocks.Count == 0)
            return;
        ParticleEmitterInfo[] converted = Convert(blocks);
        _runner.Spawn(converted, count, start, end, velocity, velocity, tintRgba, wantTrail: true);
    }

    /// <summary>Drop all live particles (map change / mode switch). Does not touch already-spawned decals.</summary>
    public void Clear()
    {
        Finish(flush: true);
        _sim.Clear();
    }

    // ---------------------------------------------------------------------------------------------
    //  EffectInfoEmitter -> ParticleEmitterInfo conversion (field-by-field; enums are cast-compatible
    //  because they share order, ParticleTypes.cs / EffectInfoParticle.cs). Cached per source list.
    // ---------------------------------------------------------------------------------------------

    private ParticleEmitterInfo[] Convert(IReadOnlyList<EffectInfoEmitter> blocks)
    {
        if (_convertCache.TryGetValue(blocks, out ParticleEmitterInfo[]? cached) && cached.Length == blocks.Count)
            return cached;

        var arr = new ParticleEmitterInfo[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
            arr[i] = ConvertOne(blocks[i]);

        _convertCache.AddOrUpdate(blocks, arr);  // upsert (handles a stale entry whose Count changed)
        return arr;
    }

    private static ParticleEmitterInfo ConvertOne(EffectInfoEmitter e) => new()
    {
        // counts
        CountAbsolute = e.CountAbsolute,
        CountMultiplier = e.CountMultiplier,
        TrailSpacing = e.TrailSpacing,

        // kind / blend / orientation (enums share order -> plain int cast)
        Type = (ParticleType)(int)e.Type,
        Blend = (ParticleBlend)(int)e.Blend,
        Orientation = (ParticleOrientation)(int)e.Orientation,

        // color
        Color0 = e.Color0,
        Color1 = e.Color1,

        // texture range
        Tex0 = e.Tex0,
        Tex1 = e.Tex1,

        // stain
        StainTex0 = e.StainTex0,
        StainTex1 = e.StainTex1,
        StainColor0 = e.StainColor0,
        StainColor1 = e.StainColor1,
        StainSizeMin = e.StainSizeMin,
        StainSizeMax = e.StainSizeMax,
        StainAlphaMin = e.StainAlphaMin,
        StainAlphaMax = e.StainAlphaMax,

        // size / alpha / time
        SizeMin = e.SizeMin,
        SizeMax = e.SizeMax,
        SizeIncrease = e.SizeIncrease,
        AlphaMin = e.AlphaMin,
        AlphaMax = e.AlphaMax,
        AlphaFade = e.AlphaFade,
        TimeMin = e.TimeMin,
        TimeMax = e.TimeMax,

        // physics
        Gravity = e.Gravity,
        Bounce = e.Bounce,
        AirFriction = e.AirFriction,
        LiquidFriction = e.LiquidFriction,
        StretchFactor = e.StretchFactor,
        VelocityMultiplier = e.VelocityMultiplier,

        // offsets / jitter
        OriginOffset = e.OriginOffset,
        RelativeOriginOffset = e.RelativeOriginOffset,
        VelocityOffset = e.VelocityOffset,
        RelativeVelocityOffset = e.RelativeVelocityOffset,
        OriginJitter = e.OriginJitter,
        VelocityJitter = e.VelocityJitter,

        // rotation
        RotateBaseMin = e.RotateBaseMin,
        RotateBaseMax = e.RotateBaseMax,
        RotateSpinMin = e.RotateSpinMin,
        RotateSpinMax = e.RotateSpinMax,

        // water gating
        Underwater = e.Underwater,
        NotUnderwater = e.NotUnderwater,
    };

    // ---------------------------------------------------------------------------------------------
    //  Stain bridge — forward the sim's collision marks to the Godot Decals subsystem.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Forward one sim stain mark to the Godot Decals subsystem (the wiring seam: the orchestrator
    /// subscribes the sim's stain output to this method, since the headless sim cannot reference Decals).
    /// <paramref name="ev"/> carries the Quake-space hit position, projection direction, decal atlas cell,
    /// color and size/alpha — exactly the inputs <see cref="Decals.Spawn"/>/<see cref="Decals.SpawnProjected"/>
    /// consume. Chooses the particlefont DECAL form of the stain cell so the mark reads as the real Xonotic
    /// scorch/blood sprite (cl_particles.c:2996-3042).
    /// </summary>
    public void ForwardStain(ParticleStainEvent ev)
    {
        DecalSplats? splats = Splats;
        if (splats is null)
            return;
        // cl_decals master toggle (DP CL_SpawnDecalParticleForSurface gates on cl_decals.integer; the
        // 2026-06-14 graphics audit flagged the port's toggle as dead — playtest #37 wires it). cl_decals
        // is a DP ENGINE cvar (default 1) that the shipped cfg tree never sets, so UNSET means ON — only an
        // explicit 0 disables (the cl_autopause unset-is-on pattern).
        VortexArena.Common.Services.ICvarService? cv = _pendingCvars;
        if (cv is not null)
        {
            string dec = cv.GetString("cl_decals");
            if (!string.IsNullOrEmpty(dec) && cv.GetFloat("cl_decals") == 0f)
                return;
        }

        // The event color is the raw INVMOD REMOVAL amount, exactly what DP feeds
        // R_DecalSystem_SplatEntities (the sim applies DP's per-path complements — see ParticleSim's
        // StainEvent contract). The splat shader multiplies wall · (1 − tex·color), so a white removal
        // darkens fully and a near-zero one is naturally invisible — no extra conversion here.
        var color = new Color(ev.ColorR, ev.ColorG, ev.ColorB);
        float alpha = Math.Clamp(ev.Alpha, 0f, 1f);
        float size = ev.Radius;
        int tex = ev.DecalTexNum;
        if (ev.IsBlood && tex < 0)
        {
            // DP :3033: tex_blooddecal[rand()&7], size · lhrandom(scalemin 1.5, scalemax 2), alpha
            // cl_particles_blood_decal_alpha·768 (saturates). Visual-only rolls — GD randomness keeps the
            // sim's RNG stream untouched (golden-trace parity).
            tex = 16 + (int)(GD.Randi() & 7);
            size *= (float)GD.RandRange(1.5, 2.0);
            alpha = 1f;
        }

        if (ev.Projected)
            // Point-effect type-decal: search the nearest surface (CL_SpawnDecalParticleForPoint :981).
            // MaxDist is the emitter's originjitter[0], carried on the event.
            splats.SplatPoint(ev.Origin, ev.MaxDist, size, color, alpha, tex);
        else
            splats.Splat(ev.Origin, ev.Direction, size, color, alpha, tex);
    }

    // ---------------------------------------------------------------------------------------------
    //  Per-frame: advance the sim, then sync the renderer to the live pool from the active camera.
    // ---------------------------------------------------------------------------------------------

    public override void _Process(double delta) => Advance(delta);

    /// <summary>
    /// The frame's particle step: what <c>_Process</c> does. An owner that knows no more particles will be
    /// spawned this frame before this node's own turn may call it earlier in the frame (legacy compatibility
    /// mode does, as soon as the client program has run), which gives the worker the rest of the frame; the
    /// node's own call is then nothing. A spawn between such an early call and the node's turn would be applied
    /// after the update instead of before it, so the early call is only for an owner that makes none.
    /// </summary>
    public void Advance(double delta)
    {
        ulong frame = Godot.Engine.GetProcessFrames();
        if (frame == _advancedFrame) return;
        _advancedFrame = frame;
        // An update still running from the frame before (no frame was drawn in between: a hidden window).
        Finish();

        // [profiling] the faithful sim+sync was the largest UNSCOPED per-frame cost (showed only as
        // proc:other in hitch dumps) — scope it so combat-frame attribution names it directly.
        using var _scope = VortexArena.Common.Diagnostics.Prof.Sample("particles.cpu");

        // Advance the faithful simulation on RENDER delta (a client visual clock, like the GPU particles) —
        // NOT Api.Clock.Time, which is the server sim clock and reads 0/paused on the render side, freezing
        // the sim (particles never age → never die → leak). The sim clamps frametime internally, so a load
        // hitch's large delta is bounded. Spawn (die) and update share this clock via ParticleSim.Now.
        // (hitch guard) Cap how far the sim clock advances in ONE frame. DP clamps particle frametime to 1.0 s
        // for correctness (particles don't fly off after a load pause) — that clamp is mirrored bit-faithfully
        // INSIDE ParticleSim.Update and is left untouched. But a full-second catch-up step turns any upstream
        // hitch into a particle amplifier: every live colliding particle box-sweeps a 1 s-long path and the
        // MultiMesh buffer balloons — the particles.cpu spike in the hitch logs. Visual particles never need to
        // fast-forward through a stall, so clamp the per-frame ADVANCE to a few render frames here. This is a
        // deliberate game-side divergence from DP's 1 s clamp; the faithful sim's own clamp stays bit-exact
        // (parity tests drive Update directly and are unaffected — this only bounds what the backend feeds it).
        const float MaxParticleStep = 0.05f;   // ~3 frames @ 60 fps; far tighter than DP's 1.0 s correctness clamp
        // #30 slowmo/pause: scale the ADVANCE by the client render-time factor (DP's cl.time — which particles
        // age on — advances by timescaled frametime, and not at all while paused). At slowmo 0 _clientTime holds
        // still, so Update() is a same-time no-op: particles hang frozen; no aging, no leak (paused sim emits
        // nothing new). This keeps the wall-clock SOURCE the doc above requires — only the step is scaled.
        _clientTime += Math.Min(VortexArena.Game.Client.ClientRenderTime.ScaleDelta((float)delta), MaxParticleStep);
        // Child scopes: a particles.cpu hitch names its half directly (sim integration vs renderer
        // cull/sort/pack) instead of restarting the whole who-is-it hunt.
        ParticleSim.UpdateSettings updateSettings = _sim.ReadUpdateSettings();

        // View origin/forward in Quake space, from the active camera (GetViewport().GetCamera3D()). The
        // renderer culls/sorts against this and converts to Godot at the boundary.
        NVec3 viewOrigin = default;
        NVec3 viewForward = new(1f, 0f, 0f);
        Camera3D? cam = GetViewport()?.GetCamera3D();
        if (cam is not null)
        {
            Transform3D xf = cam.GlobalTransform;
            viewOrigin = Coords.ToQuake(xf.Origin);
            // Godot cameras look down local -Z; convert that world direction to Quake space.
            Vector3 gFwd = -xf.Basis.Z;
            viewForward = Coords.ToQuake(gFwd);
        }

        if (_runner.Threaded != UseWorker)
        {
            _runner.Flush();
            _runner.Threaded = UseWorker;
        }
        if (!UseWorker)
        {
            long began = VortexArena.Game.Legacy.LegacyPerfLog.Stamp();
            using (VortexArena.Common.Diagnostics.Prof.Sample("particles.sim"))
                _sim.Update(_clientTime, updateSettings);
            VortexArena.Game.Legacy.LegacyPerfLog.Extra(VortexArena.Game.Legacy.LegacyPerfLog.XParticleSim, began);
            began = VortexArena.Game.Legacy.LegacyPerfLog.Stamp();
            using (VortexArena.Common.Diagnostics.Prof.Sample("particles.sync"))
                _renderer.Sync(_sim.Pool, _sim.HighWater, viewOrigin, viewForward, _clientTime);
            VortexArena.Game.Legacy.LegacyPerfLog.Extra(VortexArena.Game.Legacy.LegacyPerfLog.XParticleSync, began);
            return;
        }

        // The worker takes it from here: the update, then the cull, sort and pack into the renderer's buffers.
        // The main thread comes back for the result in Finish - when the frame is about to be drawn, or sooner
        // if something asks for the simulation.
        _packViewOrigin = viewOrigin;
        _packViewForward = viewForward;
        _packTime = _clientTime;
        _packSettings = _renderer.ReadSyncSettings();
        _uploadPending = true;
        _runner.Begin(_clientTime, updateSettings, _packOnWorker);
    }

    private float _clientTime;   // accumulating client render clock driving the sim
}
