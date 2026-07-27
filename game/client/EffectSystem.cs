using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Particles;
using XonoticGodot.Game.Client.Particles;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Client;

/// <summary>
/// The client-side renderer for the named particle-effect catalog (<see cref="Effects"/>): the Godot
/// successor to CSQC's effectinfo.txt particle player. The server emits <c>EffectRequest</c>s by EFFECT_*
/// name (see <c>EffectEmitter</c> / <c>Send_Effect</c>); this turns each name into a live Godot particle
/// node placed at the converted (Quake -> Godot) origin.
///
/// This is the GENERAL mechanism that subsumes the per-item <c>TODO(port,client)</c> markers scattered
/// across the libs ("EFFECT_EXPLOSION_SMALL", "EFFECT_*_MUZZLEFLASH", "EFFECT_*_ROCKET_TRAIL", spark/blood
/// bursts, teleport, etc.): rather than hand-coding a node per call-site, each effect name is classified by
/// a small set of prefix/substring rules into a <see cref="EffectClass"/>, and a per-class factory builds a
/// preset <see cref="GpuParticles3D"/> (one-shot burst or swept trail). New effectinfo names automatically
/// land in a reasonable class; named overrides refine the common ones (explosions, muzzleflashes, blood…).
///
/// Coordinate boundary: origins/velocities arrive in Quake space (the sim's convention) and are converted
/// with <see cref="Coords.ToGodot"/> here, exactly like every other render-side node.
///
/// Lifetime: every spawned node self-frees once its particles finish (one-shot <c>Emitting</c> + a
/// <c>SceneTreeTimer</c> sized to lifetime), so the caller fires and forgets.
/// </summary>
public partial class EffectSystem : Node3D
{
    /// <summary>Broad visual families an EFFECT_* name resolves to; each maps to a particle factory.</summary>
    public enum EffectClass
    {
        Explosion,    // explosion_*, *_explode, te_explosion, generator finalexplosion
        Smoke,        // smoke_*, smoking, smoke_ring, booster_smoke, torch
        Blood,        // blood, tr_blood, tr_slightblood
        Spark,        // sparks, *_sparks, te_spark, electricity
        MuzzleFlash,  // *_muzzleflash
        Impact,       // *_impact (bullet/laser/plasma hit puffs)
        Trail,        // tr_*, *_trail, *_thrust, fireball/firemine/seeker trails (IsTrail effects)
        Teleport,     // teleport, spawn, spawn_point, respawn_ghost
        Beam,         // *_beam, arc_lightning, *_pass (networked line effects; thin drawn here)
        Electro,      // electro_*, arc_*, shock — bluish energy bursts
        Fire,         // fire*, ef_flame, firefield, rage
        Pickup,       // item_pickup/respawn/despawn, healing, regen, armorrepair
        Generic,      // anything unclassified — a small neutral puff
    }

    /// <summary>A resolved color tint for an effect (RGB), already converted from the QC 0..1 emission color.</summary>
    private readonly record struct Tint(Color Color, bool HasOverride);

    // Cache the name->class resolution so repeated emissions of the same effect don't re-run the classifier.
    private readonly Dictionary<string, EffectClass> _classCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional beam renderer: beam-class effects (lightning/arc/laser/pass) draw as a real beam
    /// between origin and the velocity end-point instead of a particle burst when this is set.</summary>
    public BeamRenderer? Beams { get; set; }

    /// <summary>Global multiplier on particle counts (lets the host scale effect density for perf).</summary>
    [Export] public float DensityScale { get; set; } = 1f;

    /// <summary>Hard cap on simultaneous one-shot effect nodes; oldest are culled past this (cheap GC guard).</summary>
    [Export] public int MaxLiveEffects { get; set; } = 256;

    /// <summary>
    /// (3.2-1) When set, the single-node heuristic bursts from <see cref="BuildBurst"/> are reused from a
    /// free-list instead of <c>new</c>+<c>QueueFree</c>'d per spawn — killing the GpuParticles3D node /
    /// native-handle / finalizer / tree churn in firefights. Default FALSE: every code path then falls
    /// through to today's allocate-and-free behavior, so default visuals/allocation are byte-identical.
    /// Flipping this changes ONLY allocation behavior, not visuals — the same cached mesh + freshly-built
    /// ParticleProcessMaterial are carried by the reused node. Only the heuristic <see cref="BuildBurst"/>
    /// path participates; trails, the effectinfo fx_info tree, attached muzzle flashes and the GPU warm pass
    /// stay on QueueFree.
    /// </summary>
    // (§11 R11) Default ON since the rendering-pipeline pass: the pooled lifecycle (ScheduleRelease →
    // free-list) replaces the per-burst new GpuParticles3D + QueueFree node churn in firefights. The
    // attached-muzzleflash and GPU-warm callers still pass pool:false and keep their QueueFree lifecycle.
    [Export] public bool PoolBurstNodes { get; set; } = true;

    private readonly Queue<Node3D> _live = new();

    // (3.2-1) Per-(class, 5-bit tint bucket) free-list of idle one-shot burst nodes, ready to re-emit. A
    // released burst is detached from the tree (no parent) and parked here; AcquireBurst pops one (skipping
    // any Godot freed out from under us) and ConfigureBurst fully re-sets it. Managed-side dictionaries (NOT
    // SetMeta) so we never convert a string literal to a StringName on the hot path (XG0002 analyzer).
    private readonly Dictionary<(EffectClass, int), Stack<GpuParticles3D>> _burstPool = new();
    // Reverse lookup: a tracked/armed burst node -> its pool key, so ReleaseBurst can re-file it. Membership
    // also marks "this node is a live pooled burst": ReleaseBurst removes it (a second release becomes a
    // no-op) and Track's cull consults it to release rather than free an early-culled burst.
    private readonly Dictionary<GpuParticles3D, (EffectClass, int)> _burstKey = new();
    private const int MaxPooledPerKey = 24; // cap idle nodes retained per (class,tint) bucket

    // =================================================================================================
    //  effectinfo.txt-driven path (T20): the real parsed catalog and the decal/casing sub-systems.
    //  The heuristic EffectClass machinery below is retained as a FALLBACK for names not in the file.
    // =================================================================================================

    /// <summary>
    /// The parsed effectinfo.txt catalog. When an effect name is present here, the burst is built from the
    /// REAL parameters (type/color/size/alpha/count/velocity/jitter/gravity/blend/light) rather than the
    /// name-classified heuristic. Loaded lazily on first use; a miss leaves it empty (heuristic fallback).
    /// Exposed so the host can inject a VFS text loader (<see cref="EffectInfo.TextLoader"/>) before use.
    /// </summary>
    public EffectInfo Info { get; } = new();

    /// <summary>
    /// The DP particle-texture atlas (particles/particlefont.tga + .txt). Built lazily from the host loaders
    /// below; when loaded, every particle billboard and projected decal is textured with the REAL sprite the
    /// effect's <c>tex</c>/<c>staintex</c> index selects (fireball/smoke/spark/blood/scorch) instead of a flat
    /// solid-color quad. Null/not-loaded => the solid-quad fallback (the game still runs with no atlas mounted).
    /// </summary>
    public ParticleFont? Font { get; private set; }

    /// <summary>Host-supplied texture loader (e.g. <c>AssetSystem.LoadTexture</c>) — resolves the particlefont
    /// atlas image. Set by the client before first use; combined with <see cref="VfsTextLoader"/> to build the
    /// <see cref="Font"/> and to source effectinfo.txt straight from the mounted VFS.</summary>
    public Func<string, Texture2D?>? TextureLoader { get; set; }

    /// <summary>Host-supplied text loader (e.g. <c>VirtualFileSystem.ReadText</c>) — reads effectinfo.txt and
    /// the particlefont UV table from the mounted content packs.</summary>
    public Func<string, string?>? VfsTextLoader { get; set; }

    private bool _infoLoadAttempted;

    /// <summary>Projected impact decals (bullet holes / scorch / blood stains). Created on first add to the tree.</summary>
    public Decals Decals { get; private set; } = null!;

    /// <summary>Ejected weapon shell casings. Created on first add to the tree.</summary>
    public ShellCasings Casings { get; private set; } = null!;

    /// <summary>Real model-gib bursts (player death / robot chunks). Created on first add to the tree.</summary>
    public ModelGibs Gibs { get; private set; } = null!;

    // =================================================================================================
    //  Dual particle system (planning/particles-dual-system.md). The router resolves each effectinfo
    //  spawn to the faithful CPU backend (mode 0 default) or the modern GPU backend (mode 2), per the
    //  cl_particles_modern cvar + per-effect authored style + SDF coverage. Created as children in _Ready;
    //  Font/Decals/style-registry are wired in EnsureInfoLoaded once the atlas + VFS loaders are ready.
    // =================================================================================================

    /// <summary>The faithful CPU particle backend (perfect-parity MultiMesh path; the mode-0 default).</summary>
    public FaithfulParticleBackend FaithfulParticles { get; private set; } = null!;

    /// <summary>The modern GPU particle backend (custom shader_type particles + SDF collision; mode 2).</summary>
    public ModernParticleBackend ModernParticles { get; private set; } = null!;

    /// <summary>The chunked-SDF collision service (built at map load via <see cref="BuildSdfForMap"/>).</summary>
    public SdfCollisionService Sdf { get; private set; } = null!;

    /// <summary>Per-effect authored style/preset overlay (effectinfo_xg.txt). Drives mode-1 routing.</summary>
    public EffectStyleRegistry Styles { get; } = new();

    /// <summary>Routes effectinfo spawns to the faithful/modern backend (§D.2). Null until _Ready.</summary>
    public ParticleRouter Router { get; private set; } = null!;

    /// <summary>Faithful surface-splat decals (DP R_DecalSystem) — every particle mark draws here.</summary>
    public DecalSplats Splats { get; private set; } = null!;

    /// <summary>
    /// Wire the map's static collision world so decal splats conform to the real brush faces (DP
    /// R_DecalSystem clips the mark against the surfaces around the impact). Call after map load with the
    /// world from <c>MapLoader.BuildCollision</c>; without it splats fall back to flat quads.
    /// </summary>
    public void SetCollisionWorld(CollisionWorld world)
    {
        if (Splats is not null)
            Splats.World = world;

        // Give the faithful particle sim its OWN static, world-only tracer over the CLIENT collision world
        // (NetGame builds it per map via MapLoader.BuildCollision). This mirrors DP's CL_TraceLine: particle
        // bounces/content checks clip the static map BSP only — never live players/items/projectiles, and
        // never the SERVER's collision world. A new TraceService(world) has entities=null (no entity sweep)
        // and ConcurrencyGate=null (no lock): on a listen server the ambient Api.Trace IS the server world, so
        // every bouncing spark used to box-sweep the live entity broadphase under the server-tick gate — the
        // dominant combat-frame hitch. This tracer runs only on the main thread alongside the splat consumer
        // of the same (client-only) world, so it needs no gate.
        FaithfulParticles?.SetTrace(new TraceService(world));

        // Give the casing sim a world-only tracer too, so brass does full MOVETYPE_BOUNCE world collision
        // (reflect off real brush faces at cl_casings_ticrate) instead of the FloorZ ground-plane fallback —
        // faithful to Base Movetype_Physics_MatchTicrate. Same client-only, gate-free tracer rationale as above
        // (never the server world, never live entities). The sweep is a near-point box (casings are tiny brass).
        if (Casings is not null)
        {
            var casingTrace = new TraceService(world);
            Casings.TraceHook = (from, to) =>
            {
                TraceResult r = casingTrace.Trace(from, NVec3.Zero, NVec3.Zero, to, MoveFilter.WorldOnly, null);
                return new ShellCasings.CasingTrace(r.Fraction, r.EndPos, r.PlaneNormal, r.StartSolid);
            };
        }
    }

    /// <summary>
    /// True when projectile trails should spawn through the faithful per-segment path (DP CL_ParticleTrail)
    /// instead of the legacy continuous GpuParticles emitters: the faithful backend exists and the renderer
    /// is not in all-modern mode. Read per projectile spawn (live mode switches apply to new projectiles).
    /// </summary>
    public bool UseFaithfulTrails =>
        FaithfulParticles is not null &&
        (int)XonoticGodot.Game.Menu.MenuState.Cvars.GetFloat(ParticleCvars.Modern) != 2;

    /// <summary>
    /// DP CL_ParticleTrail: spawn one trail segment of <paramref name="effectName"/> along
    /// <paramref name="from"/> → <paramref name="to"/> (Quake space) with the projectile's
    /// <paramref name="velocity"/> as the emit velocity (trail blocks apply their velocitymultiplier to it —
    /// e.g. the electro trail's −0.1 backward drift). The faithful sim steps each trailspacing block along
    /// the segment with the shared fractional accumulator, so per-frame calls compose into a continuous,
    /// speed-independent trail exactly like DP. Returns false when the faithful path is off (mode 2).
    /// </summary>
    public bool SpawnTrailSegment(string effectName, NVec3 from, NVec3 to, NVec3 velocity)
    {
        if (!UseFaithfulTrails)
            return false;
        EnsureInfoLoaded();
        Effect? effect = ResolveEffect(effectName);
        IReadOnlyList<EffectInfoEmitter>? blocks = LookupInfo(effectName, effect);
        if (blocks is null || blocks.Count == 0)
            return false;
        FaithfulParticles.Trail(blocks, from, to, velocity, 1);
        return true;
    }

    /// <summary>
    /// Wire the map's RENDER geometry for decal splats (DP's R_DecalSystem clips marks against the visible
    /// surface triangles, not the collision brushes — the difference shows on bevelled trim and patches,
    /// where a brush-clipped mark stops at the wrong edge). Call after map load with the loaded BSP.
    /// </summary>
    public void SetDecalGeometry(XonoticGodot.Formats.Bsp.BspData bsp)
    {
        if (Splats is null || bsp is null)
            return;
        try { Splats.SetGeometry(bsp); }
        catch (Exception ex) { GD.PushWarning($"[DecalSplats] SetGeometry failed: {ex.Message}"); }
    }

    /// <summary>
    /// Optional host-supplied model loader (e.g. <c>AssetLoader.LoadModel</c>) shared with the casing and
    /// gib systems so they can render the real brass/limb meshes from the mounted content. When unset they
    /// fall back to generated placeholder meshes. Setting this late (after _Ready) is fine.
    /// </summary>
    public Func<string, Node3D?>? ModelLoader
    {
        get => _modelLoader;
        set
        {
            _modelLoader = value;
            if (Casings is not null) Casings.ModelLoader = value;
            if (Gibs is not null) Gibs.ModelLoader = value;
        }
    }
    private Func<string, Node3D?>? _modelLoader;

    public override void _Ready()
    {
        // Sub-systems live as children so they share this node's tree lifetime and transform.
        Decals = new Decals { Name = "Decals" };
        AddChild(Decals);
        Casings = new ShellCasings { Name = "Casings", ModelLoader = _modelLoader };
        AddChild(Casings);
        Gibs = new ModelGibs { Name = "Gibs", ModelLoader = _modelLoader };
        AddChild(Gibs);

        // Dual particle backends + SDF service live as children (shared transform/lifetime). Font/Decals and
        // the style overlay are wired lazily in EnsureInfoLoaded (after the atlas + VFS loaders are present).
        //
        // CLIENT CVAR STORE: the cl_particles* gates/quality/mode are CLIENT cvars the console/menu write into
        // MenuState.Cvars. The faithful sim, renderer, router and SDF service MUST read them from there — NOT
        // the ambient Api.Cvars, which on a listen server is the SERVER store (it lacks the client particle
        // cvars, so cl_particles reads 0 and every spawn is gated off → nothing renders). See SetCvars.
        ICvarService clientCvars = XonoticGodot.Game.Menu.MenuState.Cvars;

        FaithfulParticles = new FaithfulParticleBackend { Name = "FaithfulParticles" };
        AddChild(FaithfulParticles);
        FaithfulParticles.SetCvars(clientCvars);
        Splats = new DecalSplats { Name = "DecalSplats" };
        AddChild(Splats);
        FaithfulParticles.SetSplats(Splats);
        ModernParticles = new ModernParticleBackend { Name = "ModernParticles" };
        AddChild(ModernParticles);
        Sdf = new SdfCollisionService { Name = "SdfCollision", Cvars = clientCvars };
        AddChild(Sdf);
        Router = new ParticleRouter(FaithfulParticles, ModernParticles, Styles, Sdf) { Cvars = clientCvars };
    }

    /// <summary>
    /// Pre-warm the effect catalog + particle atlas off the hot fire path (DP precaches these at client/map
    /// init — cl_particles.c CL_Particles_ParseEffectInfo — not on the first te_particleeffect). Call this once
    /// at map load / spawn, AFTER the host has wired <see cref="TextureLoader"/>/<see cref="VfsTextLoader"/>.
    /// It only builds CPU-side resources (parse effectinfo.txt, decode the atlas image, pre-crop the common
    /// sprite bands) — it spawns NO visible particles. Idempotent: it shares <see cref="EnsureInfoLoaded"/>'s
    /// guard, so a later lazy Spawn is a no-op and the warm-up never runs twice.
    /// </summary>
    public void Warmup()
    {
        EnsureInfoLoaded();
        // Pre-crop the particlefont cells the common bursts use (smoke/dust 0-7, scorch/bullet decals 8-15,
        // blood decals 16-23, blood drops 24-31, ring 32, sparkle 38-46, fire/flame 48-55, white dot 63,
        // electro bolts 70-74). Cell()/DecalCell() cache per index, so the first real explosion/muzzleflash/
        // blood burst no longer pays the GetRegion + ImageTexture.CreateFromImage crop on its frame.
        if (Font?.Loaded == true)
        {
            for (int i = 0; i <= 74; i++)
            {
                Font.Cell(i);
                if (i <= 23) Font.DecalCell(i); // decal-form cells (luminance->alpha) for scorch/blood marks
            }
        }
    }

    /// <summary>A curated set of the most common effectinfo effects to warm the GPU pipeline for (A2): every
    /// weapon's explosion (the growth-shader fireball + smoke), a muzzleflash, a bullet/impact puff, sparks and
    /// blood. Names absent from the mounted effectinfo are skipped silently.</summary>
    private static readonly string[] WarmupEffectNames =
    {
        "rocket_explode", "grenade_explode", "hagar_explode", "electro_combo", "crylink_impact",
        "rocketlauncher_muzzleflash", "machinegun_muzzleflash", "TR_ROCKET", "TR_NEXUIZPLASMA",
        "bullet_impact", "spark_explode", "blood", "TE_EXPLOSION", "electro_impact",
    };

    /// <summary>
    /// Build one representative HIDDEN instance per effect family — a heuristic burst per <see cref="EffectClass"/>
    /// plus a curated set of common effectinfo bursts (<see cref="WarmupEffectNames"/>) — for the offscreen GPU
    /// warm pass (A2, <see cref="GpuWarmPass"/>). Each instance references the SAME cached materials/meshes a real
    /// burst uses (A1's <c>_heurMeshCache</c>/<c>_infoMeshCache</c>), so rendering them once offscreen compiles
    /// every particle draw + process pipeline (incl. the growth ShaderMaterial and the Sub/InvMod blend) — the
    /// first real explosion/muzzleflash/blood in play then hits a warm GPU. The returned nodes are NOT added to
    /// this system's tree or live pool; the warm pass parents, renders, and frees them.
    /// </summary>
    public List<Node3D> BuildWarmupInstances()
    {
        EnsureInfoLoaded();
        var list = new List<Node3D>();

        // One heuristic burst per class (Explosiveness handled inside BuildBurst; a small forward velocity so the
        // directional emitters build their cones). Covers the StandardMaterial3D mix/add draw pipelines.
        foreach (EffectClass kind in System.Enum.GetValues<EffectClass>())
        {
            if (kind == EffectClass.Beam) continue; // beams draw via BeamRenderer, warmed separately
            Tint tint = DefaultTint(kind, string.Empty);
            list.Add(BuildBurst(kind, Vector3.Zero, new Vector3(0f, 50f, 0f), 1, tint));
        }

        // A curated set of real effectinfo bursts (detached) so their specific draw materials — the growth
        // ShaderMaterial, the InvMod/Sub blend, velocity-aligned sparks — and process pipelines compile too.
        foreach (string name in WarmupEffectNames)
        {
            Effect? eff = ResolveEffect(name);
            IReadOnlyList<EffectInfoEmitter>? blocks = LookupInfo(name, eff);
            if (blocks is null || blocks.Count == 0)
                continue;
            Node3D? built = BuildFromInfo(blocks, NVec3.Zero, NVec3.Zero, 1, null, isTrail: false, attach: false);
            if (built is not null)
                list.Add(built);
        }

        // (§11 R1) The FAITHFUL path's pipelines — the default backend since the dual-system merge, and
        // entirely separate resources from the legacy bursts above: the premul/invmod MultiMesh shaders
        // (incl. the spark vertex path) and the DecalSplats blend_mul shader. Without these the first real
        // explosion/impact mark compiled 2-3 pipelines mid-play.
        if (FaithfulParticles is not null)
            list.AddRange(FaithfulParticles.BuildWarmupInstances());
        if (Splats is not null)
            list.Add(Splats.BuildWarmupInstance());

        // (engine-perf 2026-06-16) The gib limbs + brass casings render via the entity feed (cev.process) and are
        // otherwise un-warmed: the first combat death/shot first-instances their (mesh,material) pipeline mid-match
        // → a synchronous SURFACE compile (the residual a RenderDoc capture pinned to the MD3-entity class). Their
        // BuildWarmupInstances() warms exactly what plays — the real MD3/IQM limb+brass once the host wires their
        // ModelLoader, the generated fallback otherwise — via the same BuildMesh factory the live spawn uses.
        if (Gibs is not null) list.AddRange(Gibs.BuildWarmupInstances());
        if (Casings is not null) list.AddRange(Casings.BuildWarmupInstances());

        // One representative effect flash so the warm viewport renders with a clustered OmniLight active.
        // (The live fx-light pool itself is seeded by the BuildFromInfo warm bursts above — their
        // lightradius blocks route through the pooled SpawnInfoLight during the loading screen.)
        list.Add(new OmniLight3D { Name = "warm_fx_light", OmniRange = 60f, LightEnergy = 2f });

        return list;
    }

    /// <summary>Lazily load effectinfo.txt the first time we need it (so a TextLoader set after _Ready still applies).</summary>
    private void EnsureInfoLoaded()
    {
        if (_infoLoadAttempted)
            return;
        _infoLoadAttempted = true;

        // Source effectinfo.txt straight from the mounted VFS when the host wired a text loader (so the
        // override/ precedence applies); otherwise the parser's disk fallback finds the content repo copy.
        if (VfsTextLoader is not null)
            Info.TextLoader = VfsTextLoader;
        try { Info.Load(); }
        catch { /* leave empty; heuristic fallback covers it */ }

        // Build the particle-texture atlas once. A miss leaves Font not-loaded and callers fall back to the
        // solid-quad/disc path, so this never blocks rendering.
        try { Font = ParticleFont.Load(TextureLoader, VfsTextLoader); }
        catch { Font = null; }

        // Wire the dual particle backends now that the atlas + decal subsystem exist. Idempotent (shares this
        // method's _infoLoadAttempted guard). The per-effect style overlay (effectinfo_xg.txt) is parsed from
        // the same VFS text loader; absent => every effect resolves to ParticleStyle.Auto (faithful by default).
        if (FaithfulParticles is not null)
        {
            FaithfulParticles.SetFont(Font);
            FaithfulParticles.SetDecals(Decals);
        }
        if (Splats is not null)
            Splats.Font = Font;
        if (ModernParticles is not null)
            ModernParticles.Font = Font;
        try { Styles.Load(VfsTextLoader); }
        catch { /* no overlay => Auto everywhere */ }

        int mode = (int)XonoticGodot.Game.Menu.MenuState.Cvars.GetFloat(ParticleCvars.Modern);
        string modeName = mode switch { 0 => "original", 2 => "modern", _ => "mixed" };
        GD.Print($"[EffectSystem] effectinfo: {Info.Count} effects, particlefont atlas: " +
                 (Font?.Loaded == true ? "loaded" : "MISSING (solid-quad fallback)") +
                 $", style overlay: {Styles.Count} effects");
        // Headless regression / mode-matrix guard (planning §F.2): the active particle backend per cl_particles_modern.
        GD.Print($"[Particles] backend={modeName} (cl_particles_modern={mode})");
    }

    /// <summary>
    /// Build the chunked-SDF collision field for the loaded map (planning §A). The host (NetGame/GameDemo)
    /// calls this once after <c>MapLoader.BuildCollision</c> returns — it has the raw BSP bytes (for the cache
    /// hash), the static <see cref="CollisionWorld"/>, and the mounted VFS. Cheap when a cache/shipped .psdf
    /// exists; otherwise generates asynchronously on a worker (cl_particles_sdf_generate gates generation).
    /// Only meaningful for modern-collision (cl_particles_modern 1/2); harmless in mode 0 (default).
    /// </summary>
    public void BuildSdfForMap(string mapName, byte[] bspBytes, CollisionWorld collision,
        XonoticGodot.Formats.Vfs.VirtualFileSystem vfs)
    {
        if (Sdf is null || collision is null || bspBytes is null)
            return;
        try { Sdf.BuildForMap(mapName, bspBytes, collision, vfs); }
        catch (Exception ex) { GD.PushWarning($"[ParticleSDF] BuildForMap failed: {ex.Message}"); }
    }

    // =================================================================================================
    //  Public API — the entry point the net/client layer calls
    // =================================================================================================

    /// <summary>
    /// Spawn a one-shot effect by EFFECT_* registry name or effectinfo name (e.g. "EXPLOSION_BIG",
    /// "rocketlauncher_muzzleflash"). <paramref name="origin"/>/<paramref name="velocity"/> are in Quake
    /// space. For point effects <paramref name="count"/> scales the burst; for trail effects (resolved
    /// from the catalog) <paramref name="velocity"/> is treated as the trail END point. A non-null
    /// <paramref name="color"/> tints the particles (QC eent_net_color override).
    /// Returns the created node (already added to the tree), or null if the name is empty/Null.
    /// </summary>
    public Node3D? Spawn(string effectName, NVec3 origin, NVec3 velocity = default, float count = 1f, Color? color = null)
    {
        if (string.IsNullOrEmpty(effectName))
            return null;

        // Casing eject (W_*_Attack SpawnCasing): the bullet/shell casing temp-entities aren't pointparticles —
        // they spawn a tumbling, bouncing brass entity. WeaponFiring.EjectCasing emits them as the CASING_BULLET/
        // CASING_SHELL effects (carrying the QC eject velocity in Velocity); route those to the dedicated casing
        // sim instead of the generic particle-burst heuristic so a real shell actually ejects. We match BOTH the
        // EFFECT_* registry name ("CASING_BULLET", the networked path via Effect.Name) and the effectinfo name
        // ("casing_bullet", the in-process listen-server path via EffectRequest.EffectName).
        if (effectName == "CASING_BULLET" || effectName == "casing_bullet")
            return SpawnCasing(origin, velocity, shell: false);
        if (effectName == "CASING_SHELL" || effectName == "casing_shell")
            return SpawnCasing(origin, velocity, shell: true);

        // Raptor cluster-bomb burst: the DEATH_VH_RAPT_FRAGMENT FX tosses cosmetic shell-fragment gibs
        // (RaptorCBShellfragToss) carrying the bursting bomb's velocity. The gameplay layer emits this as the
        // RAPTOR_BOMB_SPREAD effect with the bomb velocity in Velocity; toss the frags, then fall through so the
        // raptor_bomb_spread particle puff (the effectinfo block) still renders alongside them.
        if (effectName == "RAPTOR_BOMB_SPREAD" || effectName == "raptor_bomb_spread")
            SpawnRaptorShellfrags(origin, velocity);

        Effect? effect = ResolveEffect(effectName);
        // EFFECT_Null and the empty effectinfo string are intentional no-renders.
        if (effect is not null && string.IsNullOrEmpty(effect.NetName))
            return null;

        EffectClass kind = Classify(effectName, effect);
        bool isTrail = effect?.IsTrail ?? IsTrailName(effectName);

        // Beam-class effects (lightning/arc/laser/pass/heal) are a line between two points (origin → velocity
        // end-point, the QC beam convention), drawn as a real beam rather than a particle burst when a beam
        // renderer is available. Electric ones crackle (jagged te_csqc_lightningarc); laser/rail/heal beams are
        // a straight cylinder (Draw_CylindricLine).
        if (kind == EffectClass.Beam && Beams is not null && velocity != default)
        {
            Color? beamTint = color is { } bc ? bc : null;
            // Electric combo/turret zaps crackle (jagged te_csqc_lightningarc: ARC_LIGHTNING). The steady
            // arc-WEAPON beam is a smooth cylinder (Base cl_arcbeam_simple default Draw_CylindricLine), so the
            // ARC_BEAM_LINE family (ARC_BEAM_LINE / ARC_BEAM_LINE_HEAL) draws STRAIGHT even though the name
            // contains "arc". HasAny is case-insensitive, so one "arc_beam_line" needle excludes both spellings.
            bool jagged = HasAny(effectName, "lightning", "shock", "electro", "tesla")
                || (HasAny(effectName, "arc") && !HasAny(effectName, "arc_beam_line"));
            if (jagged)
                return Beams.Arc(origin, velocity, beamTint);

            // Per-variant cylindric-beam thickness (Base draws each beam at its own width — the BeamRenderer
            // default of 6f is a single fallback that flattens those differences). Select by effect id so the
            // broadcast arc/vortex beam draws at the right thickness/brightness on every client; the id reaches
            // us as either the EFFECT_* name or the lower-case effectinfo name, and HasAny is case-insensitive,
            // so one needle catches both spellings. No new wire data — render-side selection only.
            float width = 6f;
            if (HasAny(effectName, "arc_beam_line_burst"))
            {
                // Base ARC_BT_BURST: the fatter burst-mode cylinder (arc.qc cl_arcbeam burst width).
                width = 14f;
            }
            else if (HasAny(effectName, "arc_beam_line"))
            {
                // Base normal/heal arc beam (ARC_BEAM_LINE / ARC_BEAM_LINE_HEAL): the standard 8f cylinder.
                // HasAny(...arc_beam_line) also matches the burst spelling, so this branch sits AFTER the burst
                // test above (burst wins).
                width = 8f;
            }
            else if (HasAny(effectName, "vortex_beam", "nex_beam"))
            {
                // VORTEX_BEAM: QC scales the beam's alpha/fade by sqrt(charge) (vortex.qc:61) so a barely-charged
                // shot draws faint and a full one bright. The port has no per-emission alpha on the wire, but in
                // the team-tint path the broadcast colour IS vortex_glowcolor(team, max(0.25, sqrt(charge))), so
                // the charge-derived brightness already lives in the tint's RGB magnitude. Recover it and fold it
                // into the beam's ALPHA (and lightly into width) so a low-charge beam renders dimmer/thinner —
                // matching the sqrt(charge) alpha gap without abusing the colour channel. On the default
                // (no-tint) path beamTint is null, so the beam keeps its native effectinfo colour at full alpha.
                if (beamTint is { } vt)
                {
                    // RGB magnitude of the charge-scaled glow stands in for sqrt(charge); clamp to the
                    // max(0.25, sqrt(charge)) floor the team path already applies so the beam never vanishes.
                    float brightness = Math.Clamp(MathF.Max(vt.R, MathF.Max(vt.G, vt.B)), 0.25f, 1f);
                    beamTint = new Color(vt.R, vt.G, vt.B, vt.A * brightness);
                    // Lerp the cylinder thickness from a thin low-charge beam (0.6×) up to the full 6f default.
                    width = 6f * (0.6f + 0.4f * brightness);
                }
            }
            return Beams.Beam(origin, velocity, beamTint, width);
        }

        // --- effectinfo.txt-driven path (T20) ------------------------------------------------------------
        // If the file defines this effect, layer one Godot burst per parsed emitter block from the REAL
        // params and return. This subsumes the heuristic for everything in the file; only names absent from
        // effectinfo fall through to the EffectClass machinery below.
        EnsureInfoLoaded();
        IReadOnlyList<EffectInfoEmitter>? blocks = LookupInfo(effectName, effect);
        if (blocks is not null && blocks.Count > 0)
        {
            // Dual particle system (§D.2): route to the faithful (mode 0 default) or modern (mode 2) backend
            // per cl_particles_modern + the effect's authored style. The backend owns the spawn (the faithful
            // pool / modern emitter is persistent, not a per-call node), so a handled route returns null. Only
            // an unwired backend falls through to the legacy GpuParticles3D path below.
            if (Router is not null && Router.Route(effectName, blocks, origin, velocity, count, isTrail, color))
            {
                // DP spawns each block's dlight INDEPENDENT of the particles (cl_particles.c:1631-1652, before
                // the spawnparticles gate) — the routed backend draws only sprites, so the light flash that
                // sells an explosion (electro_impact's blue 'lightcolor 3.1 4.4 10' room-wash, the muzzle
                // flashes, the combo's radius-400 burst) still belongs to this layer.
                SpawnRoutedBlockLights(blocks, origin, velocity, isTrail, color);
                return null;
            }

            Node3D? built = BuildFromInfo(blocks, origin, velocity, count, color, isTrail);
            if (built is not null)
                return built;
            // BuildFromInfo returns null only if every block was a non-render kind (e.g. all decals at a
            // point with no surface) — that's a legitimate no-op, mirror it rather than re-running heuristics.
            return null;
        }

        Tint tint = color is { } c ? new Tint(c, true) : DefaultTint(kind, effectName);

        Vector3 godotOrigin = Coords.ToGodot(origin);

        Node3D node;
        if (isTrail)
        {
            // Trail: sweep from origin to the velocity-end point (QC trailparticles convention).
            Vector3 godotEnd = Coords.ToGodot(velocity == default ? origin : velocity);
            node = BuildTrail(kind, godotOrigin, godotEnd, tint);
        }
        else
        {
            int n = Math.Max(1, (int)MathF.Round(count * DensityScale));
            // (3.2-1) Only the world-space burst path opts into node pooling (attached muzzleflashes / warm-pass
            // instances pass pool:false and keep their QueueFree lifecycle). Default PoolBurstNodes=false => new.
            node = BuildBurst(kind, godotOrigin, Coords.ToGodot(velocity), n, tint, pool: PoolBurstNodes);
        }

        AddChild(node);
        // Now that the node is in the tree, schedule its self-removal once the one-shot burst finishes.
        // GpuParticles3D.Lifetime is the per-particle life; add a margin for the last particles to fade.
        float linger = (float)(node is GpuParticles3D gp ? gp.Lifetime : 1f) + 0.5f;
        // (3.2-1) A pooled burst is returned to the free-list (not freed) when it finishes; otherwise QueueFree.
        // _burstKey membership marks the node as a live pooled burst (populated in ConfigureBurst when pooling).
        if (node is GpuParticles3D burst && _burstKey.ContainsKey(burst))
            ScheduleRelease(burst, linger);
        else
            ScheduleFree(node, linger);
        Track(node);
        return node;
    }

    /// <summary>Spawn directly from a structured <see cref="EffectRequest"/> (the net layer's decoded form).</summary>
    public Node3D? Spawn(in EffectRequest request)
    {
        Color? tint = null;
        // QC networks a min/max color range; use the midpoint as the representative tint when present.
        if (request.ColorMin != default || request.ColorMax != default)
        {
            NVec3 mid = (request.ColorMin + request.ColorMax) * 0.5f;
            tint = new Color(mid.X, mid.Y, mid.Z);
        }
        string name = request.Effect?.Name ?? request.EffectName;
        return Spawn(name, request.Origin, request.Velocity, request.Count, tint);
    }

    /// <summary>Convenience: explosion at a Quake-space point (te_explosion analogue).</summary>
    public Node3D? Explosion(NVec3 origin) => Spawn("TE_EXPLOSION", origin);

    /// <summary>Convenience: a muzzle flash at a Quake-space point/direction for a given weapon family name.</summary>
    public Node3D? MuzzleFlash(string effectName, NVec3 origin, NVec3 direction)
        => Spawn(effectName, origin, direction, 1);

    /// <summary>
    /// Returns the particlefont atlas sprite for a named trail effect (e.g. "TR_ROCKET", "TR_NEXUIZPLASMA"),
    /// for use by the <see cref="ProjectileRenderer"/>'s continuous-emitter trail builder. Loads effectinfo
    /// lazily on first call. Returns null when the atlas isn't mounted, the effect isn't in effectinfo, or
    /// every block is a non-sprite type (decal / bubble / beam / underwater-only).
    /// </summary>
    public Texture2D? QueryTrailSprite(string trailEffectName)
    {
        if (string.IsNullOrEmpty(trailEffectName)) return null;
        EnsureInfoLoaded();
        Effect? effect = ResolveEffect(trailEffectName);
        IReadOnlyList<EffectInfoEmitter>? blocks = LookupInfo(trailEffectName, effect);
        if (blocks is null) return null;
        foreach (EffectInfoEmitter block in blocks)
        {
            if (!block.Defined || block.Underwater) continue;
            if (block.Type is EiType.Decal or EiType.Bubble or EiType.Beam) continue;
            Texture2D? sprite = Font?.CellInRange(block.Tex0, block.Tex1);
            if (sprite is not null) return sprite;
        }
        return null;
    }

    /// <summary>
    /// The parsed effectinfo emitter blocks for a name (raw name, then the registry NetName) — the lookup
    /// the PERSISTENT map emitters (func_pointparticles / misc_laser end fx, game/client/MapParticleEmitters
    /// + LaserRenderer) use to configure long-lived <see cref="GpuParticles3D"/> nodes instead of churning a
    /// transient node per frame through <see cref="Spawn(string, NVec3, NVec3, int, Color?)"/>. Loads
    /// effectinfo lazily; null when the file is missing or the name is unknown.
    /// </summary>
    public IReadOnlyList<EffectInfoEmitter>? GetInfoBlocks(string effectName)
    {
        if (string.IsNullOrEmpty(effectName))
            return null;
        EnsureInfoLoaded();
        return LookupInfo(effectName, ResolveEffect(effectName));
    }

    /// <summary>The billboard mesh for one emitter block (sprite from the block's atlas range, blend per the
    /// parsed mode) — public so the persistent map emitters reuse the proven particle-material path.</summary>
    public Mesh BuildEmitterMesh(EffectInfoEmitter info, Color color)
        => BuildInfoMesh(info, color, Font?.CellInRange(info.Tex0, info.Tex1));

    // --- decals / casings / model-gibs entry points (T20) -------------------------------------------
    // These are the public hooks the net/client layer calls for the CSQC temp-entities that aren't plain
    // pointparticles: the `casings` casing-eject TE (casings.qc), the `net_gibsplash` gib splash
    // (gibs.qc), and a directly-requested impact decal. effectinfo `type decal` blocks already drive
    // Decals.Spawn through the normal Spawn() path; this is the explicit decal entry for callers that have a
    // surface normal/impact direction in hand.

    /// <summary>
    /// Eject a shell casing (the <c>casings</c> CSQC temp-entity): <paramref name="shell"/> selects the
    /// shotgun shell vs the default bullet casing. Origin/velocity are Quake space; <paramref name="floorZ"/>
    /// is an optional ground plane (Quake Z) the casing bounces on. No-op if the casing system isn't ready.
    /// </summary>
    public Node3D? SpawnCasing(NVec3 origin, NVec3 velocity, bool shell = false, float floorZ = float.NegativeInfinity)
        => Casings?.Spawn(origin, velocity,
            shell ? ShellCasings.CasingKind.Shell : ShellCasings.CasingKind.Bullet, floorZ);

    /// <summary>
    /// Spawn a full model-gib splash (the type-1 <c>net_gibsplash</c>): tosses the real limb/skull/eye MD3
    /// meshes outward from <paramref name="origin"/> (Quake space) scaled by <paramref name="amount"/>
    /// (the QC gibbage multiplier). Use this for player gib deaths instead of a generic blood burst.
    /// </summary>
    public void SpawnGibs(NVec3 origin, NVec3 velocity, float amount = 4f, float floorZ = float.NegativeInfinity)
        => Gibs?.Splash(origin, velocity, amount, floorZ);

    /// <summary>
    /// Toss the raptor cluster-bomb shell-fragment gibs (QC <c>RaptorCBShellfragToss</c>, dispatched from the
    /// DEATH_VH_RAPT_FRAGMENT burst FX). Cosmetic bouncing debris thrown from the bursting bomb (Quake space).
    /// </summary>
    public void SpawnRaptorShellfrags(NVec3 origin, NVec3 bombVel)
        => Gibs?.TossShellfrags(origin, bombVel);

    /// <summary>
    /// Drop a single impact decal at a Quake-space point, projected along <paramref name="impactDir"/> (the
    /// shot/explosion direction). For callers that resolve their own decal; the effectinfo path projects
    /// decals automatically for effects that declare a <c>type decal</c> block.
    /// </summary>
    public Node3D? SpawnDecal(NVec3 origin, NVec3 impactDir, float radius, Color color, float alpha = 1f)
        => Decals?.Spawn(origin, impactDir, radius, color, alpha);

    // =================================================================================================
    //  Name -> class resolution
    // =================================================================================================

    private static Effect? ResolveEffect(string name)
        => Effects.ByName(name) ?? Effects.ByEffectInfoName(name);

    /// <summary>Classify an effect name into a visual family (cached). Uses the catalog flag for trails.</summary>
    public EffectClass Classify(string effectName, Effect? effect = null)
    {
        if (_classCache.TryGetValue(effectName, out EffectClass cached))
            return cached;

        EffectClass kind = ClassifyUncached(effectName, effect ?? ResolveEffect(effectName));
        _classCache[effectName] = kind;
        return kind;
    }

    private static EffectClass ClassifyUncached(string name, Effect? effect)
    {
        // Normalise: match against both the EFFECT_* id and the effectinfo NetName (lower-case), since the
        // libs reference effects by either spelling. Substring tests are ordinal/case-insensitive.
        string id = name;
        string net = effect?.NetName ?? name;

        // Drawn line-beams (te_csqc_lightningarc + the Bumblebee heal/damage rays + the Arc weapon's cylindric
        // beam) — these are registered as trail Effects ONLY so the count-0 beam emission survives the
        // EffectEmitter.Emit/Encode point-count guard and networks (see EffectsList ARC_LIGHTNING / HEAL_BEAM /
        // DAMAGE_BEAM / ARC_BEAM_LINE[_HEAL]). They must classify as Beam (drawn via BeamRenderer between origin
        // and the velocity end-point), NOT as a particle Trail, so this explicit check precedes the trail-flag
        // branch. ARC_BEAM_LINE / ARC_BEAM_LINE_HEAL both contain the "arc_beam_line" substring, so one needle
        // catches the heal and damage variants. Particle-trail beams (arc_beam/arc_beam_heal/nex_beam) are
        // unaffected — they lack "_line" and are matched only by the generic BEAM/trail rules below.
        if (HasAny(id, "LIGHTNING", "HEAL_BEAM", "DAMAGE_BEAM", "ARC_BEAM_LINE")
            || HasAny(net, "lightning", "heal_beam", "damage_beam", "arc_beam_line"))
            return EffectClass.Beam;

        // Trails first — the catalog flag is authoritative; otherwise fall through to name heuristics.
        if ((effect?.IsTrail ?? false) || HasAny(net, "tr_", "_trail", "_thrust") || HasAny(id, "_TRAIL"))
        {
            // A trail still has a flavour: fire/plasma/blood trails read differently.
            if (HasAny(net, "blood")) return EffectClass.Blood;
            return EffectClass.Trail;
        }

        // Muzzleflashes (every weapon's *_muzzleflash / *_MUZZLEFLASH).
        if (HasAny(id, "MUZZLEFLASH") || HasAny(net, "muzzleflash"))
            return EffectClass.MuzzleFlash;

        // Explosions (explosion_*, *_explode, finalexplosion, te_explosion, ballexplode, joinexplode).
        if (HasAny(id, "EXPLO") || HasAny(net, "explo", "explode", "_ballexplode", "joinexplode", "blowup"))
            return EffectClass.Explosion;

        // Teleport / spawn-in / respawn ghost.
        if (HasAny(id, "TELEPORT", "SPAWN", "RESPAWN") || HasAny(net, "teleport", "spawn_event", "spawn_point", "respawn"))
            return EffectClass.Teleport;

        // Beams & passes (drawn as a thin line/streak between two points).
        if (HasAny(id, "BEAM", "LIGHTNING", "PASS", "LASER_BEAM") || HasAny(net, "beam", "lightning", "_pass", "g3"))
            return EffectClass.Beam;

        // Blood.
        if (HasAny(id, "BLOOD") || HasAny(net, "blood"))
            return EffectClass.Blood;

        // Sparks (sparks, *_sparks, te_spark, electricity_sparks, kaball_sparks).
        if (HasAny(id, "SPARK") || HasAny(net, "spark"))
            return EffectClass.Spark;

        // Electro / arc / shock — bluish energy bursts (impacts and combos).
        if (HasAny(id, "ELECTRO", "ARC", "SHOCK") || HasAny(net, "electro", "arc_", "shock", "lightning"))
            return EffectClass.Electro;

        // Fire family (fireball without trail, flame, firefield, firemine point, rage).
        if (HasAny(id, "FIRE", "FLAME", "RAGE") || HasAny(net, "fire", "flame", "rage", "torch"))
            return EffectClass.Fire;

        // Smoke (smoke_*, smoking, smoke_ring).
        if (HasAny(id, "SMOKE", "SMOKING") || HasAny(net, "smoke", "smoking"))
            return EffectClass.Smoke;

        // Item / pickup / heal / regen.
        if (HasAny(id, "ITEM_", "PICKUP", "HEALING", "REGEN", "ARMOR_REPAIR", "AMMO")
            || HasAny(net, "item_", "pickup", "healing", "regen", "armorrepair", "ammoregen"))
            return EffectClass.Pickup;

        // Impacts (*_impact) — generic hit puff; checked after the more specific families above.
        if (HasAny(id, "IMPACT") || HasAny(net, "impact"))
            return EffectClass.Impact;

        return EffectClass.Generic;
    }

    private static bool IsTrailName(string name)
        => HasAny(name, "_TRAIL", "tr_", "_trail", "_thrust");

    private static bool HasAny(string haystack, params string[] needles)
    {
        foreach (string n in needles)
            if (haystack.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // =================================================================================================
    //  Default tints per class (used when the emission carries no explicit color override)
    // =================================================================================================

    private static Tint DefaultTint(EffectClass kind, string name)
    {
        Color c = kind switch
        {
            EffectClass.Explosion => new Color(1.0f, 0.55f, 0.15f),   // orange fireball
            EffectClass.Smoke => new Color(0.35f, 0.35f, 0.37f),       // grey
            EffectClass.Blood => new Color(0.55f, 0.04f, 0.04f),       // dark red
            EffectClass.Spark => new Color(1.0f, 0.85f, 0.4f),         // yellow-white
            EffectClass.MuzzleFlash => new Color(1.0f, 0.9f, 0.55f),   // bright muzzle yellow
            EffectClass.Impact => new Color(0.75f, 0.7f, 0.6f),        // dusty puff
            EffectClass.Trail => new Color(0.9f, 0.7f, 0.4f),          // warm exhaust
            EffectClass.Teleport => new Color(0.5f, 0.7f, 1.0f),       // cyan-blue
            EffectClass.Beam => new Color(0.7f, 0.85f, 1.0f),          // pale energy
            EffectClass.Electro => new Color(0.35f, 0.55f, 1.0f),      // electro blue
            EffectClass.Fire => new Color(1.0f, 0.45f, 0.1f),          // fire orange
            EffectClass.Pickup => new Color(0.4f, 1.0f, 0.6f),         // green sparkle
            _ => new Color(0.8f, 0.8f, 0.8f),
        };

        // A couple of weapon-family hue refinements keyed off the name (plasma=blue, crylink=purple).
        if (HasAny(name, "plasma", "vortex", "nex", "hagar")) c = c.Lerp(new Color(0.4f, 0.6f, 1.0f), 0.4f);
        else if (HasAny(name, "crylink")) c = c.Lerp(new Color(0.8f, 0.4f, 1.0f), 0.5f);
        else if (HasAny(name, "rocket", "grenade", "mortar")) c = c.Lerp(new Color(1.0f, 0.5f, 0.1f), 0.3f);

        return new Tint(c, false);
    }

    // =================================================================================================
    //  Particle factories — one per class. Each returns a self-freeing node.
    // =================================================================================================

    /// <summary>
    /// Build (or, when <paramref name="pool"/>, reuse) a single one-shot heuristic burst node and configure it.
    /// <paramref name="pool"/> is honored ONLY by the world-space <see cref="Spawn"/> burst path (it passes
    /// <see cref="PoolBurstNodes"/>); the attached-muzzleflash and GPU-warm callers leave it false so their
    /// nodes are freshly <c>new</c>'d and never enter the pool (different ownership/lifecycle — see those
    /// callers). When false this is byte-identical to the original <c>new GpuParticles3D</c> + configure.
    /// </summary>
    private Node3D BuildBurst(EffectClass kind, Vector3 pos, Vector3 vel, int count, Tint tint, bool pool = false)
    {
        GpuParticles3D particles = pool ? AcquireBurst(kind, tint) : new GpuParticles3D();
        ConfigureBurst(particles, kind, pos, vel, count, tint, pool);
        return particles;
    }

    /// <summary>
    /// Apply the per-class heuristic burst preset onto <paramref name="particles"/>. This doubles as the RESET
    /// path for a pooled node: it stops any leftover emission, reassigns every sizing/timing/material property,
    /// <c>Restart()</c>s to drop particles from a prior burst, then re-arms emission — so a reused node carries
    /// nothing stale from its previous use. On a freshly-<c>new</c>'d node the extra reset steps are cheap no-ops,
    /// keeping a single divergence-free code path. When <paramref name="pool"/> the node's pool key is recorded
    /// in <see cref="_burstKey"/> so it can later be re-filed by <see cref="ReleaseBurst"/>.
    /// </summary>
    private void ConfigureBurst(GpuParticles3D particles, EffectClass kind, Vector3 pos, Vector3 vel, int count, Tint tint, bool pool = false)
    {
        // Per-class burst tuning: (baseCount, lifetime, speedMin, speedMax, scale, gravityZ, spread).
        (int baseCount, float life, float vMin, float vMax, float scale, float grav, float spread) = kind switch
        {
            EffectClass.Explosion => (48, 0.9f, 60f, 320f, 1.6f, -120f, 1f),
            EffectClass.Smoke => (16, 1.6f, 8f, 40f, 2.2f, 30f, 0.6f),
            EffectClass.Blood => (20, 0.7f, 30f, 140f, 0.5f, -400f, 0.7f),
            EffectClass.Spark => (24, 0.6f, 120f, 360f, 0.18f, -500f, 0.9f),
            EffectClass.MuzzleFlash => (12, 0.12f, 40f, 160f, 0.5f, 0f, 0.4f),
            EffectClass.Impact => (12, 0.45f, 20f, 90f, 0.35f, -200f, 0.8f),
            EffectClass.Teleport => (40, 1.1f, 30f, 110f, 0.6f, 60f, 1f),
            EffectClass.Electro => (30, 0.7f, 80f, 260f, 0.4f, 0f, 1f),
            EffectClass.Fire => (24, 0.8f, 20f, 90f, 1.0f, 120f, 0.7f),
            EffectClass.Pickup => (20, 1.0f, 20f, 80f, 0.4f, 80f, 0.9f),
            EffectClass.Beam => (16, 0.35f, 40f, 120f, 0.4f, 0f, 1f),
            EffectClass.Trail => (10, 0.5f, 10f, 50f, 0.5f, 0f, 0.5f),
            _ => (12, 0.6f, 20f, 100f, 0.5f, 0f, 0.8f),
        };

        int n = Math.Clamp(Math.Max(baseCount, count), 1, 1024);

        // (3.2-1) RESET-FIRST: stop any leftover emission before re-arming a reused node (no-op on a fresh one).
        particles.Emitting = false;
        particles.Name = $"fx_{kind}";
        particles.Amount = n;
        particles.Lifetime = life;
        particles.OneShot = true;
        particles.Explosiveness = kind == EffectClass.MuzzleFlash ? 1f : 0.85f;
        particles.Position = pos;
        // Directional emitters (muzzle/blood/spark) bias along the velocity; otherwise spherical.
        // Defense-in-depth for a reused node: a prior life as some other class could have left these set even
        // though BuildBurst never sets them itself (only BuildInfoBurst does — and that path isn't pooled yet).
        particles.LocalCoords = false;
        particles.TransformAlign = GpuParticles3D.TransformAlignEnum.Disabled;
        particles.Visible = true; // a released node was hidden (see ReleaseBurst)

        var mat = new ParticleProcessMaterial
        {
            Direction = vel.LengthSquared() > 1f ? vel.Normalized() : Vector3.Up,
            Spread = spread * 180f * 0.5f,
            InitialVelocityMin = vMin,
            InitialVelocityMax = vMax,
            Gravity = new Vector3(0f, grav, 0f),
            ScaleMin = scale * 0.6f,
            ScaleMax = scale,
            // T2: tint lives ONLY in the color ramp (ApplyColorRamp encodes it) + this White base avoids the
            // base × ramp × albedo triple-multiply that darkened the heuristic bursts.
            Color = Colors.White,
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = kind == EffectClass.Smoke ? 6f : 2f,
        };
        // Fade alpha over life and cool the color toward dark for fire/explosion.
        ApplyColorRamp(mat, kind, tint.Color);
        // Reassigning ProcessMaterial/DrawPass1 REPLACES the prior emission shape/velocity/ramp + mesh.
        particles.ProcessMaterial = mat;
        particles.DrawPass1 = BuildParticleMesh(kind, tint.Color);
        // Clear any still-live particles from a prior burst (GPU buffer) AFTER mat/mesh are assigned and BEFORE
        // re-arming emission, so a reused node never spits leftover particles from its previous direction.
        particles.Restart();
        particles.Emitting = true; // arm LAST

        if (pool)
            _burstKey[particles] = (kind, ColorKey5(tint.Color));
    }

    /// <summary>(3.2-1) Pop an idle burst node for (class, 5-bit tint) from the free-list, or build a fresh one
    /// when the bucket is empty. Skips any pooled node Godot freed out from under us (e.g. on tree exit).</summary>
    private GpuParticles3D AcquireBurst(EffectClass kind, Tint tint)
    {
        var key = (kind, ColorKey5(tint.Color));
        if (_burstPool.TryGetValue(key, out Stack<GpuParticles3D>? stack))
        {
            while (stack.Count > 0)
            {
                GpuParticles3D n = stack.Pop();
                if (GodotObject.IsInstanceValid(n))
                    return n;
            }
        }
        return new GpuParticles3D();
    }

    /// <summary>(3.2-1) Return a finished burst node to its free-list instead of freeing it. Idempotent: a node
    /// that's already been released (no longer in <see cref="_burstKey"/>) is a no-op, so the timer-release and
    /// the Track-cull release of the same node can't double-handle it. Detaches the node from the tree, hides it
    /// and drops its live particles so a parked node holds nothing on the GPU.</summary>
    private void ReleaseBurst(GpuParticles3D p)
    {
        if (!GodotObject.IsInstanceValid(p))
            return;
        // Not currently a live tracked burst (already pooled / never pooled) -> nothing to do.
        if (!_burstKey.Remove(p, out (EffectClass, int) key))
            return;

        p.Emitting = false;
        p.Visible = false;
        p.Restart(); // drop any in-flight particles so the parked node renders nothing
        p.GetParent()?.RemoveChild(p); // detach but DO NOT free

        Stack<GpuParticles3D> stack = _burstPool.TryGetValue(key, out Stack<GpuParticles3D>? s)
            ? s
            : (_burstPool[key] = new Stack<GpuParticles3D>());
        if (stack.Count < MaxPooledPerKey)
            stack.Push(p);
        else
            p.QueueFree(); // bucket full — let this one go
    }

    private Node3D BuildTrail(EffectClass kind, Vector3 start, Vector3 end, Tint tint)
    {
        // A trail (the heuristic fallback, used only for names absent from effectinfo) lays its particles
        // stepped ALONG the segment in one frame (T1), like the effectinfo path — not scattered through the
        // segment's bounding box over time. The emitter sits at `start` and the emission-point set places each
        // particle down the line.
        Vector3 seg = end - start;
        float len = seg.Length();

        (float life, float scale, float grav) = kind switch
        {
            EffectClass.Blood => (0.8f, 0.3f, -200f),
            EffectClass.Fire => (0.7f, 0.7f, 60f),
            _ => (0.6f, 0.45f, 0f),
        };

        // Density scales with segment length so a long trail isn't sparse (≈1 particle per 6 units).
        int n = Math.Clamp((int)(len / 6f) + 8, 8, 512);

        var particles = new GpuParticles3D
        {
            Name = $"trail_{kind}",
            Amount = n,
            Lifetime = life,
            OneShot = true,
            Explosiveness = 1.0f, // lay the whole line down this frame (the points distribute it in space)
            Emitting = true,
            Position = start,
            VisibilityAabb = new Aabb(new Vector3(-len - 32f, -len - 32f, -len - 32f), new Vector3(2f * len + 64f, 2f * len + 64f, 2f * len + 64f)),
        };

        var mat = new ParticleProcessMaterial
        {
            Direction = Vector3.Up,
            Spread = 12f,
            InitialVelocityMin = 0f,
            InitialVelocityMax = 20f,
            Gravity = new Vector3(0f, grav, 0f),
            ScaleMin = scale * 0.5f,
            ScaleMax = scale,
            Color = Colors.White, // T2: tint via the color ramp only (see BuildBurst)
        };
        // Emission points stepped along the segment (local to `start`), with a tiny jitter so a thick effect
        // doesn't read as a perfectly straight pixel line.
        var pointImg = Image.CreateEmpty(n, 1, false, Image.Format.Rgbf);
        for (int i = 0; i < n; i++)
        {
            float t = (i + 0.5f) / n;
            Vector3 p = seg * t + new Vector3(
                (float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0));
            pointImg.SetPixel(i, 0, new Color(p.X, p.Y, p.Z));
        }
        mat.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Points;
        mat.EmissionPointTexture = ImageTexture.CreateFromImage(pointImg);
        mat.EmissionPointCount = n;
        ApplyColorRamp(mat, kind, tint.Color);
        particles.ProcessMaterial = mat;
        particles.DrawPass1 = BuildParticleMesh(kind, tint.Color);
        return particles;
    }

    /// <summary>Fade particles toward transparent over their life; warm classes also cool toward dark.</summary>
    private static void ApplyColorRamp(ParticleProcessMaterial mat, EffectClass kind, Color baseColor)
    {
        var ramp = new Gradient();
        bool warm = kind is EffectClass.Explosion or EffectClass.Fire or EffectClass.MuzzleFlash;
        ramp.SetColor(0, warm ? new Color(1f, 1f, 0.85f, 1f) : baseColor);
        ramp.AddPoint(0.5f, baseColor);
        // End transparent (and darker for warm classes, like smoke cooling out of a fireball).
        Color end = warm ? new Color(0.2f, 0.18f, 0.18f, 0f) : new Color(baseColor.R, baseColor.G, baseColor.B, 0f);
        ramp.SetColor(ramp.GetPointCount() - 1, end);

        var tex = new GradientTexture1D { Gradient = ramp };
        mat.ColorRamp = tex;
    }

    // Per-(class, sprite) cache of the heuristic draw mesh+material. The mesh+material is a pure function of
    // (kind, sprite) — the albedo is always White (T2: the tint rides the per-particle vertex color, so the
    // `color` arg never reaches the material) — so it's shareable across emitters exactly like _trailResCache.
    // CellInRange still randomises the sprite cell per spawn, so keying by the RESOLVED sprite instance keeps
    // the per-burst wisp/blob variety while sharing the QuadMesh + StandardMaterial3D (and its first-use
    // pipeline compile). Removes 2 Godot Resource allocations per heuristic-burst spawn.
    private readonly Dictionary<(EffectClass, ulong), Mesh> _heurMeshCache = new();

    /// <summary>The billboard mesh each particle draws: a small unshaded quad tinted to the class and (when the
    /// atlas is mounted) textured with a representative particlefont sprite for that family. Cached + shared.</summary>
    private Mesh BuildParticleMesh(EffectClass kind, Color color)
    {
        (int t0, int t1) = HeuristicTex(kind);
        Texture2D? sprite = Font?.CellInRange(t0, t1);
        var key = (kind, sprite is null ? 0UL : sprite.GetInstanceId());
        if (_heurMeshCache.TryGetValue(key, out Mesh? cachedMesh))
            return cachedMesh;

        var quad = new QuadMesh { Size = new Vector2(1f, 1f) };
        bool additive = kind is EffectClass.Explosion or EffectClass.MuzzleFlash or EffectClass.Spark
            or EffectClass.Electro or EffectClass.Fire or EffectClass.Beam or EffectClass.Teleport
            or EffectClass.Pickup;
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = additive ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            // Keep the per-particle scale through billboarding (godot#74897); without it large puffs collapse
            // to the 1×1 base quad and vanish.
            BillboardKeepScale = true,
            VertexColorUseAsAlbedo = true,
            // T2: White albedo — the tint is carried by the per-particle vertex COLOR (process material ramp).
            AlbedoColor = Colors.White,
            // Use the particle billboard path; let the shader read per-particle color from the process mat.
            DisableReceiveShadows = true,
        };
        ApplySprite(mat, sprite);
        quad.Material = mat;
        _heurMeshCache[key] = quad;
        return quad;
    }

    /// <summary>Quantise a color to a 5-bit-per-channel cache key (matches <c>Decals.SolidTexture</c>) so a
    /// continuum of tints collapses to a bounded set of shared resources.</summary>
    private static int ColorKey5(Color c)
        => ((int)(c.R * 31) << 10) | ((int)(c.G * 31) << 5) | (int)(c.B * 31);

    /// <summary>A representative particlefont tex range for a heuristic class (the effectinfo-less fallback),
    /// using the same DP atlas bands the file does: smoke 0-7, fire/flame 48-55, blood particle 24-31,
    /// sparkle 38-46, the white dot 63, the electro bolts 70-74, the ring 32.</summary>
    private static (int, int) HeuristicTex(EffectClass kind) => kind switch
    {
        EffectClass.Explosion => (48, 56),   // fire blobs
        EffectClass.Fire => (48, 56),
        EffectClass.MuzzleFlash => (48, 56),
        EffectClass.Smoke => (0, 8),         // smoke wisps
        EffectClass.Impact => (0, 8),        // dust puff
        EffectClass.Blood => (24, 32),       // blood droplets
        EffectClass.Spark => (63, 64),       // bright dot
        EffectClass.Electro => (70, 74),     // lightning sprites
        EffectClass.Teleport => (32, 33),    // ring
        EffectClass.Pickup => (38, 46),      // sparkle
        EffectClass.Beam => (63, 64),
        EffectClass.Trail => (63, 64),
        _ => (63, 64),                       // generic white particle
    };

    // =================================================================================================
    //  effectinfo.txt-driven burst construction (T20) — the faithful CL_NewParticlesFromEffectinfo port
    // =================================================================================================

    // DP world gravity (sv_gravity default); effectinfo `gravity` is a multiplier on this. We negate it for
    // the Godot ParticleProcessMaterial.Gravity Y (Godot Y is up; positive gravity should pull down).
    private const float DpGravity = 800f;

    /// <summary>Resolve the parsed emitter list for a name, trying the raw name then the registry NetName.</summary>
    private IReadOnlyList<EffectInfoEmitter>? LookupInfo(string effectName, Effect? effect)
    {
        if (!Info.Loaded)
            return null;
        IReadOnlyList<EffectInfoEmitter>? blocks = Info.Get(effectName);
        if (blocks is null && effect is not null && !string.IsNullOrEmpty(effect.NetName))
            blocks = Info.Get(effect.NetName);
        return blocks;
    }

    /// <summary>
    /// Build the layered set of Godot nodes for an effectinfo effect — one per emitter block, exactly as DP
    /// runs every same-named block in sequence. Returns a parent <see cref="Node3D"/> holding them, or null
    /// if no block produced a visible node. <paramref name="velocity"/> is the emit velocity for a point
    /// effect, or the trail END point for a trail effect (then the segment is origin..velocity).
    /// </summary>
    private Node3D? BuildFromInfo(IReadOnlyList<EffectInfoEmitter> blocks, NVec3 origin, NVec3 velocity,
        float count, Color? colorOverride, bool isTrail, bool attach = true)
    {
        var parent = new Node3D { Name = "fx_info", Position = Coords.ToGodot(origin) };
        bool any = false;

        // For a trail, originmins..originmaxs span the segment; for a point effect both are the origin and
        // the emit velocity is the supplied velocity (DP passes velocitymins==velocitymaxs for point fx).
        NVec3 originMin = origin;
        NVec3 originMax = isTrail && velocity != default ? velocity : origin;
        NVec3 emitVel = isTrail ? default : velocity;
        float traillen = (originMax - originMin).Length();

        // DP cl_particles.c:1600-1602: compute the spawn-center water state ONCE per call and gate each block
        // by it (underwater/notunderwater). Only WATER|SLIME count as "underwater" (not lava) — matching DP.
        NVec3 center = (originMin + originMax) * 0.5f;
        bool underwater = Api.Services is not null
            && (Api.Trace.PointContents(center) & (SuperContents.Water | SuperContents.Slime)) != 0;

        // The velocity-derived forward/right/up basis the relative origin/velocity offsets rotate into (DP
        // cl_particles.c:1745-1752). DP uses AnglesFromVectors(.., flippitch:false) → the makevectors-consistent
        // (round-trippable) angles, i.e. our FixedVecToAngles; for a trail the basis comes from the trail dir.
        NVec3 basisDir = isTrail ? (originMax - originMin) : emitVel;
        XonoticGodot.Common.Math.QMath.AngleVectors(
            XonoticGodot.Common.Math.QMath.FixedVecToAngles(basisDir),
            out NVec3 fwd, out NVec3 right, out NVec3 up);

        foreach (EffectInfoEmitter info in blocks)
        {
            if (!info.Defined)
                continue;

            // Skip underwater-only blocks when dry and notunderwater-only blocks when submerged (DP:1625-1628).
            if (info.Underwater && !underwater)
                continue;
            if (info.NotUnderwater && underwater)
                continue;

            // Beam emitters only draw as a trail; if we have a beam renderer, route to it and skip particles.
            if (info.Orientation == EiOrientation.Beam)
            {
                if (Beams is not null && isTrail && velocity != default)
                {
                    Beams.Beam(origin, velocity, InfoTintColor(info, colorOverride));
                    any = true;
                }
                continue;
            }

            // Spawn the dlight this block requests (explosion flash etc.), independent of particles (DP spawns
            // the dlight before the cnt==0 particle check). Mark `any` so a pure-dlight block (cnt<=0: F3 cases
            // like TE_SMALLFLASH / grapple_muzzleflash / jumppad_activate) keeps the parent that holds the light.
            if (info.LightRadius > 0f)
            {
                SpawnInfoLight(info, originMax, colorOverride);
                any = true;
            }

            switch (info.Type)
            {
                case EiType.Decal:
                {
                    // The offscreen GPU warm pass (attach:false) only needs the PARTICLE pipelines compiled; a
                    // projected decal would raycast into the live map and leave a stray scorch mark, so skip it.
                    if (!attach)
                        break;
                    // pt_decal (DP cl_particles.c:1674-1681): fire 32 rays out to originjitter[0] and project
                    // onto the NEAREST surface using its NORMAL — the emit velocity plays NO role. DP first
                    // shifts the center by relativeoriginoffset rotated into the velocity basis.
                    NVec3 baseAt = isTrail ? originMax : center;
                    NVec3 at = baseAt
                        + info.RelativeOriginOffset.X * fwd
                        + info.RelativeOriginOffset.Y * right
                        + info.RelativeOriginOffset.Z * up;
                    // INVMOD decals store the inverse of the visible color (white -> black scorch); invert so the
                    // Godot Decal's modulate lands the same mark instead of a bright square.
                    Color dcol = InfoDecalColor(info, colorOverride, useStain: false);
                    float radius = (info.SizeMin + info.SizeMax) * 0.5f;
                    // The scorch/bullet-mark sprite the block's tex range selects (8-15 in the atlas), in the
                    // Decal-node form (alpha = mark luminance) so it projects a shaped mark, not an opaque square.
                    ImageTexture? dtex = Font?.CellInRange(info.Tex0, info.Tex1, decal: true);
                    // maxdist = originjitter[0] (DP), the ray search radius for the nearest surface.
                    if (Decals?.SpawnProjected(at, info.OriginJitter.X, radius, dcol, info.MidAlpha01(), dtex) is not null)
                        any = true; // a decal landed on a surface (DP only spawns when bestfrac < 1)
                    break;
                }

                case EiType.RainDecal:
                case EiType.EntityParticle:
                    // RainDecal (a falling-rain splat decal) and EntityParticle (particles parented to a moving
                    // entity) need machinery the burst builder doesn't model; skip them rather than fake it.
                    break;

                // NOTE: pt_rain/pt_snow are NOT weather-only — Xonotic uses `type snow` for plain gameplay
                // particles (item_despawn's orange puff, plasma/wiz/vore trails, goldendust, the minsta lasers,
                // nex_beam). Treat them as a normal burst (the heuristic Pickup/etc. path would have too); the
                // drift/orientation nuance of true snow is a known approximation, not a reason to drop the puff.
                case EiType.Rain:
                case EiType.Snow:
                default:
                {
                    GpuParticles3D? p = BuildInfoBurst(info, originMin, originMax, emitVel, traillen, count,
                        colorOverride, isTrail, fwd, right, up);
                    if (p is not null)
                    {
                        // Position is relative to the parent (already at origin); emit positions are absolute,
                        // so park the emitter at the parent origin and let the process material place particles.
                        parent.AddChild(p);
                        any = true;

                        // DP collides particles vs world when bounce!=0; a pt_blood particle that hits solid leaves
                        // a blood decal+stain (cl_particles.c:3020-3041), and blood is emitted with bounce -1. Full
                        // per-particle world-tracing is architectural, so route the blood-splat to the decal system:
                        // a stain on the nearest surface around the spawn (no surface in range => none, like blood
                        // sprayed into open air). Gated on p!=null so the stain rate tracks the (fractional-count)
                        // particle rate as DP does, not once per call. Non-blood bounce<0 leaves NO decal in DP.
                        // Skipped during the warm pass (attach:false) — a stain would raycast into the live map.
                        if (attach && info.Type == EiType.Blood)
                            SpawnBloodSplat(info, center, colorOverride);
                    }
                    break;
                }
            }
        }

        if (!any)
        {
            parent.QueueFree();
            return null;
        }

        // attach:false — the offscreen GPU warm pass (A2) owns the node itself (parents it under its warm
        // viewport, renders a couple of frames to compile the pipelines, then frees it); skip the live-pool
        // bookkeeping so a warm burst never enters the visible scene or the MaxLiveEffects cull queue.
        if (!attach)
            return parent;

        AddChild(parent);
        ScheduleFree(parent, InfoParentLinger(blocks));
        Track(parent);
        return parent;
    }

    /// <summary>
    /// Drop a blood stain on the nearest surface around a blood emitter's spawn (F6) — the bounded stand-in for
    /// DP's per-particle blood-vs-world collision (cl_particles.c:3020-3041), where a pt_blood particle that hits
    /// solid leaves a <c>tex_blooddecal</c> mark. We don't trace each particle, so we project ONE stain at the
    /// spawn center via the nearest-surface raycast (no surface in range => no stain, like blood into open air).
    /// </summary>
    private void SpawnBloodSplat(EffectInfoEmitter info, NVec3 center, Color? colorOverride)
    {
        // (playtest #37) Route through the DP-faithful surface-clipped splats (DecalSplats), NOT the legacy
        // Godot Decal projection box: a projection volume paints THROUGH thin geometry inside its box and
        // smears across corners — the reported through-wall/streaking marks. This was the last live caller
        // of the legacy path. SplatPoint = CL_SpawnDecalParticleForPoint (probe rays, nearest surface, splat
        // clipped to the real triangles).
        if (Splats is null || !GodotObject.IsInstanceValid(Splats))
            return;
        // Use the declared stain (staintex/staincolor/stainsize/stainalpha) when present — that's the dedicated
        // blood-decal sprite (atlas 16-23) and tint DP leaves where blood hits. The blood particle color is
        // stored INVMOD-inverted (0xA8FFFF cyan -> dark red), so InfoDecalColor inverts it and folds in the
        // neutral staincolor. The splat shader wants the REMOVAL amount (wall · (1 − tex·color)) — the
        // complement of that display color. Falls back to the emitter's own size/color when no stain declared.
        Color blood = InfoDecalColor(info, colorOverride, useStain: true);
        var removal = new Color(1f - blood.R, 1f - blood.G, 1f - blood.B);
        float halfSize = info.HasStain
            ? MathF.Max(1f, info.StainMidSize())
            : MathF.Max(1f, info.SizeMax * 0.75f);
        float alpha = info.HasStain
            ? MathF.Max(0.4f, info.StainMidAlpha01())
            : MathF.Max(0.4f, info.MidAlpha01());
        // Atlas cell: the declared staintex band, else DP's blood-decal band 16..23 (cl_particles.c:3033).
        int tex = info.HasStain && info.StainTex0 >= 0
            ? info.StainTex0 + (info.StainTex1 > info.StainTex0 ? (int)(GD.Randi() % (uint)(info.StainTex1 - info.StainTex0)) : 0)
            : 16 + (int)(GD.Randi() & 7);
        // Maxdist: a small surface-search radius so the stain stays local to the hit.
        Splats.SplatPoint(center, 32f, halfSize, removal, alpha, tex);
    }

    /// <summary>Build one GPU-particle emitter from a single parsed emitter block (the per-block spawn loop).</summary>
    private GpuParticles3D? BuildInfoBurst(EffectInfoEmitter info, NVec3 originMin, NVec3 originMax,
        NVec3 emitVel, float traillen, float requestedCount, Color? colorOverride, bool isTrail,
        NVec3 fwd, NVec3 right, NVec3 up)
    {
        // DP cl_particles.c:1710-1721: cnt = countabsolute + (pcount*countmultiplier)*quality, plus the
        // trailspacing term for a trail — quality (our DensityScale) multiplies ONLY the multiplier/trailspacing
        // terms, NEVER countabsolute (F10). Then the persistent per-info accumulator drains integer particles and
        // carries the fraction across calls, so sub-1 counts (e.g. count 0.025) spawn ~1 particle every ~40 calls
        // instead of over-spawning every call (F2).
        float pcount = MathF.Max(1f, requestedCount);
        float cnt = info.CountAbsolute + pcount * info.CountMultiplier * DensityScale;
        if (isTrail && info.TrailSpacing > 0f && traillen > 0f)
            cnt += (traillen / info.TrailSpacing) * DensityScale;

        // DP: if (cnt == 0) continue; — a pure-dlight block (TE_SMALLFLASH, grapple muzzleflash/impact,
        // jumppad_activate) emits only its dlight, no billboard (F3). The dlight was already spawned by the caller.
        if (cnt <= 0f)
            return null;

        // Persistent fractional accumulator (DP particleeffectinfo_t.particleaccumulator), bounded as DP does.
        info.ParticleAccumulator = Math.Clamp(info.ParticleAccumulator + cnt, 0.0, 16384.0);
        int n = (int)info.ParticleAccumulator; // integer particles to spawn this call
        info.ParticleAccumulator -= n;         // keep the remainder for next time
        if (n <= 0)
            return null; // fraction not yet accumulated to a whole particle — nothing to draw this call
        n = Math.Clamp(n, 1, 1024);

        // Colors: DP picks a random lerp between color[0] and color[1] per particle; Godot's process material
        // has a single base color, so use the midpoint and let the (optional) emission override win.
        Color baseColor = InfoTintColor(info, colorOverride);

        // Sizes are world-unit half-sizes; Godot quad scale is the full edge, so 2*size. sizeincrease is
        // units/sec growth — fold it into a scale-over-life curve.
        float sizeMin = MathF.Max(0.01f, info.SizeMin) * 2f;
        float sizeMax = MathF.Max(0.01f, info.SizeMax) * 2f;
        float life = info.Lifetime();

        var particles = new GpuParticles3D
        {
            Name = $"ei_{info.Type}",
            Amount = n,
            Lifetime = life,
            OneShot = true,
            // Both a point burst and a (T1) line trail spawn ALL their particles this frame: the burst explodes
            // outward, the trail lays its particles down the segment at once (the emission-point set below already
            // distributes them in space, so there's nothing to spread over time — a beam must appear instantly).
            Explosiveness = 1.0f,
            Emitting = true,
            // Pin a generous visibility box so a burst whose particles fly out far (velocityjitter up to 900) +
            // grow big isn't frustum-culled as a whole once the emitter point drifts off-screen (Godot's auto
            // AABB tracks only the emitter origin). Defensive — cheap for a short-lived one-shot.
            VisibilityAabb = new Aabb(new Vector3(-384f, -384f, -384f), new Vector3(768f, 768f, 768f)),
        };

        var mat = new ParticleProcessMaterial();

        // --- emission position: origin range + originjitter (an ellipsoid radius per axis) -------------
        // Place the emission box to span originMin..originMax (a point if both equal), centered on the parent
        // origin. Add originjitter as extra box extents (DP adds jitter*VectorRandom per axis).
        // DP cl_particles.c:1751 & 1773: the emission origin gets relativeoriginoffset rotated into the velocity
        // basis (forward/right/up), then a flat originoffset added in world axes (F5). Both are constant per
        // emitter, so fold them into the span center here.
        NVec3 relOrigin =
            info.RelativeOriginOffset.X * fwd
            + info.RelativeOriginOffset.Y * right
            + info.RelativeOriginOffset.Z * up;

        if (isTrail && traillen > 0.001f)
        {
            // T1 — a trail lays its particles stepped ALONG the segment (DP cl_particles.c:1763-1780), each
            // jittered by originjitter per axis, ALL this frame. We encode the per-particle spawn positions into
            // a Godot emission-POINT texture (one RGB pixel = one local position) instead of spraying a box. This
            // is what makes the vortex beam (nex_beam, 4 trail blocks) read as a crisp line of particles in the
            // frame it fired, rather than a cloud fading in across the segment's bounding box over its lifetime.
            NVec3 segStart = originMin + relOrigin + info.OriginOffset;
            NVec3 segEnd = originMax + relOrigin + info.OriginOffset;
            NVec3[] ptsQ = XonoticGodot.Engine.Effects.TrailGeometry.PointsAlongSegment(
                segStart, segEnd, n, info.OriginJitter, () => (float)GD.RandRange(-1.0, 1.0));
            var pointImg = Image.CreateEmpty(n, 1, false, Image.Format.Rgbf);
            for (int i = 0; i < n; i++)
            {
                Vector3 g = Coords.ToGodot(ptsQ[i] - originMin); // local to the parent (which sits at originMin)
                pointImg.SetPixel(i, 0, new Color(g.X, g.Y, g.Z));
            }
            mat.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Points;
            mat.EmissionPointTexture = ImageTexture.CreateFromImage(pointImg);
            mat.EmissionPointCount = n;
            particles.Position = Vector3.Zero;
        }
        else
        {
            NVec3 spanCenterQ = (originMin + originMax) * 0.5f + relOrigin + info.OriginOffset;
            NVec3 spanHalfQ = (originMax - originMin) * 0.5f;
            // Convert the Quake-space jitter/half-extent magnitudes to Godot axes (abs, since extents are radii).
            Vector3 jitterG = AbsToGodot(info.OriginJitter);
            Vector3 spanHalfG = AbsToGodot(spanHalfQ);
            Vector3 centerOffsetG = Coords.ToGodot(spanCenterQ - originMin); // parent is at originMin
            mat.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box;
            mat.EmissionBoxExtents = new Vector3(
                MathF.Max(0.01f, spanHalfG.X + jitterG.X),
                MathF.Max(0.01f, spanHalfG.Y + jitterG.Y),
                MathF.Max(0.01f, spanHalfG.Z + jitterG.Z));
            particles.Position = centerOffsetG;
        }

        // --- velocity: emitVel*velmult + velocityoffset + relativevelocityoffset(basis) + velocityjitter*rand
        // DP cl_particles.c:1752 adds relativevelocityoffset rotated into the velocity basis (F5).
        NVec3 relVel =
            info.RelativeVelocityOffset.X * fwd
            + info.RelativeVelocityOffset.Y * right
            + info.RelativeVelocityOffset.Z * up;
        NVec3 baseVelQ = emitVel * info.VelocityMultiplier + info.VelocityOffset + relVel;
        Vector3 baseVelG = Coords.ToGodot(baseVelQ);
        float baseSpeed = baseVelG.Length();
        if (baseSpeed > 0.001f)
        {
            mat.Direction = baseVelG / baseSpeed;
            mat.InitialVelocityMin = baseSpeed;
            mat.InitialVelocityMax = baseSpeed;
            mat.Spread = 0f;
        }
        else
        {
            mat.Direction = Vector3.Up;
            mat.InitialVelocityMin = 0f;
            mat.InitialVelocityMax = 0f;
            mat.Spread = 0f;
        }
        // velocityjitter is an isotropic-ish random add; model it with a spherical velocity-pivot spread plus a
        // randomized speed magnitude scaled to the average jitter radius. This reproduces the "spray" feel.
        Vector3 vjG = AbsToGodot(info.VelocityJitter);
        float jitterSpeed = (vjG.X + vjG.Y + vjG.Z) / 3f;
        if (jitterSpeed > 0.001f)
        {
            // Add the jitter as extra velocity spread: widen the cone and the speed band.
            mat.Spread = baseSpeed > 0.001f ? 60f : 180f;
            mat.InitialVelocityMin = MathF.Max(0f, mat.InitialVelocityMin - jitterSpeed);
            mat.InitialVelocityMax = mat.InitialVelocityMax + jitterSpeed;
            // Keep the originjitter Box emission shape even with no base velocity: DP always spawns within
            // trailpos +/- originjitter[axis] regardless of velocity (cl_particles.c:1773). Switching to a Sphere
            // here left EmissionSphereRadius at Godot's default 1.0 and collapsed the originjitter spawn volume —
            // explosions/impacts (originjitter 8-16) emitted from a near-point instead of the intended cloud.
        }

        // --- gravity: multiplier on world gravity, pulling down (-Z Quake = -Y Godot) ------------------
        mat.Gravity = new Vector3(0f, -DpGravity * info.Gravity, 0f);

        // --- bounce (F6, partial): DP ricochets particles off world when bounce>0 (cl_particles.c:3048-3053).
        // Wire Godot's rigid particle collision so positive-bounce emitters (sparks/casings spray) bounce IF the
        // scene has GpuParticlesCollision* colliders; with none present (the current scene) this is inert, not a
        // regression. bounce<0 (blood removal+stain) is handled by SpawnBloodSplat, not here. DP bounce is the
        // restitution coefficient, which maps directly onto CollisionBounce.
        if (info.Bounce > 0.0001f)
        {
            mat.CollisionMode = ParticleProcessMaterial.CollisionModeEnum.Rigid;
            mat.CollisionBounce = Math.Clamp(info.Bounce, 0f, 1f);
            mat.CollisionFriction = 0f;
        }

        // --- air friction (F4) -------------------------------------------------------------------------
        // DP cl_particles.c:2972-2976 applies velocity-PROPORTIONAL (exponential) decay every frame for ANY
        // nonzero airfriction, including NEGATIVE (which accelerates): v(t) = v0 * exp(-airfriction * t).
        // Godot's process-material Damping is LINEAR (units/sec) and can't accelerate, so:
        //  * positive airfriction -> a linear Damping equal to the exponential's AVERAGE deceleration over the
        //    particle life: total speed lost = v0*(1-exp(-k*life)); / life gives the mean units/sec. This is
        //    dimensionally correct and far closer than the old k*v0*0.5 constant.
        //  * negative airfriction (acceleration) -> Godot can't ramp speed up via Damping, so instead of
        //    DROPPING it (the old '>0f' guard silently dropped 57 emitters) we leave Damping at 0 and raise the
        //    initial speed to the exponential's average speed over life, so the particle covers ~the same
        //    distance the acceleration would have produced.
        float k = info.AirFriction;
        if (k > 0.0001f)
        {
            float decay = (1f - MathF.Exp(-k * life)) / MathF.Max(0.0001f, life); // mean (units/sec) per unit speed
            float d = baseSpeed * decay;
            mat.DampingMin = d;
            mat.DampingMax = d;
        }
        else if (k < -0.0001f && baseSpeed > 0.001f)
        {
            // Mean of v0*exp(-k*t) over [0,life] (k<0 => grows) = v0*(exp(-k*life)-1)/(-k*life). Scale the
            // initial speed by that mean/v0 ratio so the emitted particles read as the faster, spreading spray.
            float klife = -k * life;
            float meanRatio = (MathF.Exp(klife) - 1f) / MathF.Max(0.0001f, klife);
            meanRatio = MathF.Min(meanRatio, 8f); // cap the boost so a long-lived emitter can't explode the speed
            mat.InitialVelocityMin *= meanRatio;
            mat.InitialVelocityMax *= meanRatio;
        }

        // --- size + sizeincrease ----------------------------------------------------------------------
        // DP grows/shrinks each particle by `sizeincrease` (world units/sec) over its life. The Godot-native
        // way is ParticleProcessMaterial.scale_curve (a CurveTexture), but that path is ENGINE-BUGGY: a scale
        // curve combined with a non-Point emission shape (we use Box) + a non-default amount + Particle
        // billboard mode makes GPUParticles3D silently STOP DRAWING (godotengine/godot#75748, #76332 — closed
        // "won't fix"). That is exactly why every explosion fire/smoke and every rocket/grenade smoke-TRAIL
        // emitter — all of which carry `sizeincrease` — rendered INVISIBLE, while the sparks/decals/debris
        // (no sizeincrease, so no scale curve) drew fine. So instead of a scale curve we bake the size SPAN
        // (birth..death) straight into scale_min/scale_max: the burst spawns puffs across that range, reading
        // as an expanding cloud for positive sizeincrease (or holding the big birth size for the negative-
        // sizeincrease fire flash), and ALWAYS renders. We lose per-particle temporal growth; visibility wins.
        // T6: a billboard particle with a nonzero sizeincrease GROWS over its life in a custom draw shader
        // (scaled by INSTANCE_CUSTOM.z = lifetime phase) — the muzzle flash pops then shrinks, the nex_impact
        // shockwave ring expands, smoke billows. Sparks keep the baked span (they're velocity-aligned, not
        // billboarded). When not growing, fall back to baking the size SPAN into scale_min/max (the proven
        // always-renders path that sidesteps godot#75748's invisible-billboard scale-curve bug).
        float? growRatio = ComputeGrowthRatio(info, sizeMin, sizeMax, life);
        if (growRatio is not null)
        {
            mat.ScaleMin = sizeMin;
            mat.ScaleMax = sizeMax;
            EnableLifetimePhase(mat); // drive INSTANCE_CUSTOM.z 0→1 over life for the growth shader
        }
        else
        {
            float grow = info.SizeIncrease * 2f * life; // total edge-size change over the particle's life
            float endMin = sizeMin + grow, endMax = sizeMax + grow;
            mat.ScaleMin = MathF.Max(0.4f, MathF.Min(sizeMin, endMin));
            mat.ScaleMax = MathF.Max(mat.ScaleMin, MathF.Max(sizeMax, endMax));
        }

        // --- color + alpha over life (alphafade) ------------------------------------------------------
        // CRITICAL (T2): the tint must be applied in exactly ONE place or it compounds. Godot multiplies the
        // particle color through base Color × ColorRamp × ColorInitialRamp, then the draw mesh multiplies that
        // vertex COLOR by its AlbedoColor. Setting the tint on ALL of them (the old bug) cubed it — a 0x808080
        // smoke rendered at ~0.125 instead of 0.5. So: the ColorRamp carries ONLY the alpha fade (white RGB);
        // the draw mesh's AlbedoColor is White (see the BuildInfoMesh call below); and the tint lives in EITHER
        // ColorInitialRamp (per-particle random lerp, DP color[0]..color[1]) or the flat base Color.
        bool perParticleColor = colorOverride is null && info.Color0 != info.Color1;
        mat.Color = perParticleColor ? Colors.White : baseColor;
        ApplyInfoColorRamp(mat, info);
        // Per-particle color randomization: DP picks a random lerp between color[0] and color[1] per particle.
        // ColorInitialRamp assigns each particle a random tint sampled from the gradient at spawn time. With the
        // base Color White and the ramp white-RGB, this is the SOLE tint when it's set.
        if (perParticleColor)
        {
            Color c0 = new Color(((info.Color0 >> 16) & 0xFF) / 255f, ((info.Color0 >> 8) & 0xFF) / 255f, (info.Color0 & 0xFF) / 255f);
            Color c1 = new Color(((info.Color1 >> 16) & 0xFF) / 255f, ((info.Color1 >> 8) & 0xFF) / 255f, (info.Color1 & 0xFF) / 255f);
            var initGrad = new Gradient();
            initGrad.SetColor(0, c0);
            initGrad.SetColor(1, c1);
            mat.ColorInitialRamp = new GradientTexture1D { Gradient = initGrad };
        }

        // --- rotation (rotate base/spin) --------------------------------------------------------------
        if (info.RotateSpinMin != 0f || info.RotateSpinMax != 0f || info.RotateBaseMax != info.RotateBaseMin)
        {
            mat.AngleMin = info.RotateBaseMin;
            mat.AngleMax = info.RotateBaseMax;
            float spinMin = info.RotateSpinMin, spinMax = info.RotateSpinMax;
            mat.AngularVelocityMin = spinMin;
            mat.AngularVelocityMax = spinMax;
        }

        // T3 — velocity-aligned sparks. DP draws pt_spark / orientation=spark as a quad STRETCHED along the
        // particle's velocity (cl_particles.c:2812-2825), not a camera-facing billboard. Godot's
        // ZBillboardYToVelocity transform-align orients each particle's local +Y to its velocity while still
        // facing the camera about that axis — exactly the spark streak. The elongated quad (its long axis = +Y)
        // then reads as an outward-flying streak that falls with gravity, instead of a fixed screen-space sliver.
        if (info.Orientation == EiOrientation.Spark)
            particles.TransformAlign = GpuParticles3D.TransformAlignEnum.ZBillboardYToVelocity;

        particles.ProcessMaterial = mat;
        // Sprite: one representative atlas cell from the block's [tex0,tex1) range. DP randomises the cell PER
        // PARTICLE; we pick one PER EMITTER (CellInRange already randomises within the range, so repeated bursts
        // still vary the wisp/blob). We deliberately do NOT pack the range into a ParticlesAnimHFrames sprite
        // strip + AnimOffset: that GPU sprite-sheet path rendered every multi-cell billboard INVISIBLE (the fire
        // `static`, the `smoke`, the dark `alphastatic` smoke, debris — all tex ranges), while the single-cell
        // emitters (spark tex 40) drew fine. One static cell uses that same proven single-sprite path so the
        // fire and smoke puffs actually show, at the cost of per-particle cell variety (a fair trade).
        Texture2D? sprite = Font?.CellInRange(info.Tex0, info.Tex1);
        // T2: pass White as the mesh albedo — the tint already lives in the process material's color pipeline
        // (mat.Color / ColorInitialRamp), read through as the per-particle vertex COLOR. (BuildEmitterMesh, the
        // persistent-map-emitter caller, still passes its baseColor — its own pipeline is separate.)
        particles.DrawPass1 = BuildInfoMesh(info, Colors.White, sprite,
            sparkAspect: SparkAspect(info, baseSpeed, jitterSpeed), growRatio: growRatio);
        return particles;
    }

    // =================================================================================================
    //  Projectile trails (T5) — effectinfo-driven CONTINUOUS emitters for a flying projectile
    // =================================================================================================

    /// <summary>
    /// Build the per-block CONTINUOUS trail emitters for a named effectinfo trail (TR_ROCKET, TR_CRYLINKPLASMA,
    /// FIREBALL, …) so the <see cref="ProjectileRenderer"/> can give a projectile its REAL layered trail instead
    /// of one invented puff stream. Each non-underwater block that declares a <c>trailspacing</c> becomes a
    /// world-space (LocalCoords=false) emitter whose per-second particle rate tracks the projectile's
    /// speed / trailspacing — so a rocket smokes grey, streams an orange fire core BACKWARD (velocitymultiplier
    /// -1.5) and throws bright sparks, exactly the DP layering. <paramref name="initialVelocityGodot"/> is the
    /// projectile's spawn velocity (Godot space); velocitymultiplier blocks inherit it (baked at spawn —
    /// projectiles fly ~straight, so this stays faithful without per-frame updates). The emitters are NOT yet
    /// parented (the caller adds them as children of the projectile root). Returns null when effectinfo isn't
    /// loaded or the name has no usable trail block — the caller then keeps its legacy single-emitter trail.
    /// </summary>
    public List<GpuParticles3D>? BuildProjectileTrailEmitters(
        string trailEffectName, Vector3 initialVelocityGodot, Color? tint = null)
    {
        if (string.IsNullOrEmpty(trailEffectName))
            return null;
        EnsureInfoLoaded();
        IReadOnlyList<EffectInfoEmitter>? blocks = LookupInfo(trailEffectName, ResolveEffect(trailEffectName));
        if (blocks is null || blocks.Count == 0)
            return null;

        float speed = initialVelocityGodot.Length();
        Vector3 velDir = speed > 0.001f ? initialVelocityGodot / speed : Vector3.Forward;
        if (speed < 1f) speed = 1200f; // velocity not yet networked at spawn — assume a typical projectile speed

        var emitters = new List<GpuParticles3D>();
        foreach (EffectInfoEmitter info in blocks)
        {
            if (!info.Defined || info.Underwater) continue;        // skip placeholder + underwater-only blocks
            if (info.TrailSpacing <= 0f) continue;                 // not a per-distance trail block
            if (info.Type is EiType.Decal or EiType.RainDecal or EiType.EntityParticle) continue;
            if (info.Orientation == EiOrientation.Beam) continue;  // beams aren't projectile trails

            GpuParticles3D? e = BuildOneTrailEmitter(info, speed, velDir, tint);
            if (e is not null) emitters.Add(e);
        }
        return emitters.Count > 0 ? emitters : null;
    }

    /// <summary>Configure one continuous world-space trail emitter from a parsed trail block (T5). Mirrors the
    /// per-block physics/color/size of <see cref="BuildInfoBurst"/>, but emits per-distance at the projectile.</summary>
    private GpuParticles3D? BuildOneTrailEmitter(EffectInfoEmitter info, float speed, Vector3 velDir, Color? tint)
    {
        float life = info.Lifetime();
        // DP spawns one trail particle per `trailspacing` units of travel; the per-second rate is therefore
        // speed/trailspacing. Godot's emit rate is Amount/Lifetime, so Amount = rate*life (× DensityScale).
        float rate = (speed / info.TrailSpacing) * DensityScale;
        int amount = Math.Clamp((int)MathF.Ceiling(rate * life), 2, 256);

        var p = new GpuParticles3D
        {
            Name = $"trail_{info.Type}",
            Amount = amount,
            Lifetime = life,
            OneShot = false,
            Emitting = true,
            LocalCoords = false, // particles stay in world space, left behind the moving projectile
            Explosiveness = 0f,  // continuous stream
            VisibilityAabb = new Aabb(new Vector3(-256f, -256f, -256f), new Vector3(512f, 512f, 512f)),
        };

        var mat = new ParticleProcessMaterial();
        Color baseColor = InfoTintColor(info, tint);
        bool perParticleColor = tint is null && info.Color0 != info.Color1;

        // emission: a small box around the projectile origin; originjitter gives the trail its thickness.
        Vector3 jitterG = AbsToGodot(info.OriginJitter);
        mat.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box;
        mat.EmissionBoxExtents = new Vector3(
            MathF.Max(0.05f, jitterG.X), MathF.Max(0.05f, jitterG.Y), MathF.Max(0.05f, jitterG.Z));

        // velocity: projectile velocity * velocitymultiplier + velocityoffset (world space). A velmult of -1.5
        // (TR_ROCKET fire core) streams the particles BACKWARD out of the nozzle; the smoke layer's velmult≈0
        // leaves them roughly stationary; velocityjitter sprays the sparks.
        Vector3 baseVelG = velDir * (speed * info.VelocityMultiplier) + Coords.ToGodot(info.VelocityOffset);
        float baseSpeed = baseVelG.Length();
        if (baseSpeed > 0.001f)
        {
            mat.Direction = baseVelG / baseSpeed;
            mat.InitialVelocityMin = baseSpeed;
            mat.InitialVelocityMax = baseSpeed;
        }
        else
        {
            mat.Direction = Vector3.Up;
            mat.InitialVelocityMin = 0f;
            mat.InitialVelocityMax = 0f;
        }
        Vector3 vjG = AbsToGodot(info.VelocityJitter);
        float jitterSpeed = (vjG.X + vjG.Y + vjG.Z) / 3f;
        if (jitterSpeed > 0.001f)
        {
            mat.Spread = baseSpeed > 0.001f ? 45f : 180f;
            mat.InitialVelocityMin = MathF.Max(0f, mat.InitialVelocityMin - jitterSpeed);
            mat.InitialVelocityMax += jitterSpeed;
        }

        mat.Gravity = new Vector3(0f, -DpGravity * info.Gravity, 0f);

        // air friction (same exponential→linear approximation as the burst path)
        float k = info.AirFriction;
        if (k > 0.0001f)
        {
            float decay = (1f - MathF.Exp(-k * life)) / MathF.Max(0.0001f, life);
            float d = baseSpeed * decay;
            mat.DampingMin = d;
            mat.DampingMax = d;
        }
        else if (k < -0.0001f && baseSpeed > 0.001f)
        {
            float klife = -k * life;
            float meanRatio = MathF.Min((MathF.Exp(klife) - 1f) / MathF.Max(0.0001f, klife), 8f);
            mat.InitialVelocityMin *= meanRatio;
            mat.InitialVelocityMax *= meanRatio;
        }

        // size + sizeincrease (T6: temporal growth via the draw shader when nonzero, else the baked span)
        float sizeMin = MathF.Max(0.01f, info.SizeMin) * 2f;
        float sizeMax = MathF.Max(0.01f, info.SizeMax) * 2f;
        float? growRatio = ComputeGrowthRatio(info, sizeMin, sizeMax, life);
        if (growRatio is not null)
        {
            mat.ScaleMin = sizeMin;
            mat.ScaleMax = sizeMax;
            EnableLifetimePhase(mat);
        }
        else
        {
            float grow = info.SizeIncrease * 2f * life;
            mat.ScaleMin = MathF.Max(0.4f, MathF.Min(sizeMin, sizeMin + grow));
            mat.ScaleMax = MathF.Max(mat.ScaleMin, MathF.Max(sizeMax, sizeMax + grow));
        }

        // color + alpha (de-compounded — the tint lives in exactly one place; see BuildInfoBurst/T2)
        mat.Color = perParticleColor ? Colors.White : baseColor;
        ApplyInfoColorRamp(mat, info);
        if (perParticleColor)
        {
            Color c0 = new Color(((info.Color0 >> 16) & 0xFF) / 255f, ((info.Color0 >> 8) & 0xFF) / 255f, (info.Color0 & 0xFF) / 255f);
            Color c1 = new Color(((info.Color1 >> 16) & 0xFF) / 255f, ((info.Color1 >> 8) & 0xFF) / 255f, (info.Color1 & 0xFF) / 255f);
            var initGrad = new Gradient();
            initGrad.SetColor(0, c0);
            initGrad.SetColor(1, c1);
            mat.ColorInitialRamp = new GradientTexture1D { Gradient = initGrad };
        }

        if (info.RotateSpinMin != 0f || info.RotateSpinMax != 0f || info.RotateBaseMax != info.RotateBaseMin)
        {
            mat.AngleMin = info.RotateBaseMin;
            mat.AngleMax = info.RotateBaseMax;
            mat.AngularVelocityMin = info.RotateSpinMin;
            mat.AngularVelocityMax = info.RotateSpinMax;
        }

        if (info.Orientation == EiOrientation.Spark)
            p.TransformAlign = GpuParticles3D.TransformAlignEnum.ZBillboardYToVelocity;

        p.ProcessMaterial = mat;
        Texture2D? sprite = Font?.CellInRange(info.Tex0, info.Tex1);
        p.DrawPass1 = BuildInfoMesh(info, Colors.White, sprite,
            sparkAspect: SparkAspect(info, baseSpeed, jitterSpeed), growRatio: growRatio);
        return p;
    }

    // =================================================================================================
    //  Temporal sizeincrease (T6) — grow/shrink each billboard particle over its life in a draw shader
    // =================================================================================================

    /// <summary>
    /// The end:start size ratio for a billboard particle that grows/shrinks over its life (DP sizeincrease,
    /// world units/sec). Returns null when there's no growth to animate (sizeincrease ~0) or the particle is a
    /// velocity-aligned spark (those keep the baked span). <paramref name="sizeMin"/>/<paramref name="sizeMax"/>
    /// are the spawn edge sizes (2*half-size). The shader lerps the quad scale 1→ratio over the lifetime phase.
    /// </summary>
    private static float? ComputeGrowthRatio(EffectInfoEmitter info, float sizeMin, float sizeMax, float life)
    {
        if (info.Orientation == EiOrientation.Spark || MathF.Abs(info.SizeIncrease) <= 0.0001f)
            return null;
        float avgEdge = MathF.Max(0.01f, (sizeMin + sizeMax) * 0.5f);
        float endEdge = avgEdge + info.SizeIncrease * 2f * life; // sizeincrease is half-size/sec -> *2 edge/sec
        return Math.Clamp(endEdge / avgEdge, 0.02f, 12f);
    }

    /// <summary>Drive <c>INSTANCE_CUSTOM.z</c> from 0 (birth) to 1 (death) over the particle's lifetime so the
    /// growth draw shader can read the lifetime phase — anim_speed 1, no offset, no loop (the value isn't a
    /// sprite frame here, just the phase ramp).</summary>
    private static void EnableLifetimePhase(ParticleProcessMaterial mat)
    {
        mat.AnimSpeedMin = 1f;
        mat.AnimSpeedMax = 1f;
        mat.AnimOffsetMin = 0f;
        mat.AnimOffsetMax = 0f;
    }

    // One compiled growth shader per blend mode (the render_mode is baked into the source), shared across
    // every growing emitter; only the lightweight ShaderMaterial (grow + sprite uniforms) is per-emitter.
    private static readonly Dictionary<BaseMaterial3D.BlendModeEnum, Shader> _growthShaders = new();

    /// <summary>The cached growth shader for a blend mode: a camera-facing billboard (keeping per-particle scale
    /// and rotation) that scales the quad by <c>mix(1, grow, lifetimePhase)</c>, reproducing DP's per-particle
    /// sizeincrease. Built once per blend; the same Godot billboard-particle vertex math the StandardMaterial3D
    /// path uses, plus the growth term — so it renders identically except for the temporal size.</summary>
    private static Shader GrowthShader(BaseMaterial3D.BlendModeEnum blend)
    {
        if (_growthShaders.TryGetValue(blend, out Shader? cached))
            return cached;
        string blendMode = blend switch
        {
            BaseMaterial3D.BlendModeEnum.Add => "blend_add",
            BaseMaterial3D.BlendModeEnum.Sub => "blend_sub",
            _ => "blend_mix",
        };
        var shader = new Shader
        {
            Code =
                "shader_type spatial;\n" +
                // Keep depth TESTING on (walls occlude the particle) but depth WRITE off for transparency
                // (depth_draw_opaque = transparent frags don't write depth). depth_test_disabled was the bug
                // that let the rocket explosion / smoke draw THROUGH walls.
                "render_mode " + blendMode + ", unshaded, cull_disabled, shadows_disabled, depth_draw_opaque;\n" +
                "uniform sampler2D albedo_tex : source_color, filter_linear;\n" +
                "uniform float grow = 1.0;\n" +
                "void vertex() {\n" +
                "    float angle = INSTANCE_CUSTOM.x;\n" +
                "    float phase = clamp(INSTANCE_CUSTOM.z, 0.0, 1.0);\n" +
                "    float gs = mix(1.0, grow, phase);\n" +
                "    vec3 r = normalize(INV_VIEW_MATRIX[0].xyz) * (length(MODEL_MATRIX[0].xyz) * gs);\n" +
                "    vec3 u = normalize(INV_VIEW_MATRIX[1].xyz) * (length(MODEL_MATRIX[1].xyz) * gs);\n" +
                "    vec3 f = normalize(INV_VIEW_MATRIX[2].xyz);\n" +
                "    mat4 bb = mat4(vec4(r, 0.0), vec4(u, 0.0), vec4(f, 0.0), MODEL_MATRIX[3]);\n" +
                "    mat4 rot = mat4(vec4(cos(angle), -sin(angle), 0.0, 0.0), vec4(sin(angle), cos(angle), 0.0, 0.0), vec4(0.0, 0.0, 1.0, 0.0), vec4(0.0, 0.0, 0.0, 1.0));\n" +
                "    MODELVIEW_MATRIX = VIEW_MATRIX * (bb * rot);\n" +
                "}\n" +
                "void fragment() {\n" +
                "    vec4 t = texture(albedo_tex, UV);\n" +
                "    ALBEDO = t.rgb * COLOR.rgb;\n" +
                "    ALPHA = t.a * COLOR.a;\n" +
                "}\n",
        };
        _growthShaders[blend] = shader;
        return shader;
    }

    /// <summary>
    /// The length:width aspect ratio for a velocity-stretched spark quad (T3). DP stretches a spark to
    /// <c>len = max(stretch*0.04*speed, size*0.5)</c> along velocity with cross-width <c>size</c>
    /// (cl_particles.c:2812-2825). Our quad is <c>(0.5, aspect)</c> scaled by ScaleMin/Max (≈2*size), so the
    /// aspect is <c>stretch*0.04*speed / (size)</c>, clamped to a sane streak range. <paramref name="baseSpeed"/>
    /// and <paramref name="jitterSpeed"/> are the emitter's directed + jitter speeds (the spark's expected speed).
    /// </summary>
    private static float SparkAspect(EffectInfoEmitter info, float baseSpeed, float jitterSpeed)
    {
        if (info.Orientation != EiOrientation.Spark)
            return 8f;
        float stretch = info.StretchFactor > 0f ? info.StretchFactor : 1f;
        float expectedSpeed = baseSpeed + jitterSpeed;
        float size = MathF.Max(0.5f, info.SizeMin + info.SizeMax); // ~2*avg half-size (the quad's scaled edge)
        return Math.Clamp(stretch * 0.04f * expectedSpeed / size, 1f, 30f);
    }

    /// <summary>The representative tint for an emitter: the explicit emission override, else the color midpoint.</summary>
    private static Color InfoTintColor(EffectInfoEmitter info, Color? colorOverride)
    {
        if (colorOverride is { } c)
            return c;
        (float r, float g, float b) = info.MidColor();
        return new Color(r, g, b);
    }

    /// <summary>
    /// The visible tint for a PROJECTED decal (scorch / stain). DP draws decals with PBLEND_INVMOD, where the
    /// stored color is the INVERSE of what the player sees (a scorch stores white -> renders black; blood stores
    /// cyan 0xA8FFFF -> renders dark red). A Godot <see cref="Decal"/> modulates rather than inverse-modulates,
    /// so we invert here to land the same final color. An explicit emission override is taken as-is (already a
    /// final color). When a stain color is supplied (0xRRGGBB, typically 0x808080 neutral) it multiplies the
    /// inverted base, matching DP's particle*staincolor product.
    /// </summary>
    private static Color InfoDecalColor(EffectInfoEmitter info, Color? colorOverride, bool useStain)
    {
        if (colorOverride is { } c)
            return c;
        (float r, float g, float b) = info.MidColor();
        bool invmod = info.Blend == EiBlend.InvMod;
        if (invmod) { r = 1f - r; g = 1f - g; b = 1f - b; } // stored inverse -> visible color
        if (useStain && info.HasStain)
        {
            (float sr, float sg, float sb) = info.StainMidColor();
            r *= sr; g *= sg; b *= sb; // particle color * staincolor MODDING FACTOR (DP /0x8000 product; 0x808080 neutral)
        }
        return new Color(r, g, b);
    }

    /// <summary>Fade alpha from the initial opacity to 0 over the particle life (DP alphafade). The RGB is left
    /// WHITE so this ramp carries ONLY the alpha curve — the tint comes from <c>mat.Color</c> / the initial ramp
    /// (T2 single-application; multiplying a tinted ramp on top of a tinted base/albedo is what darkened bursts).</summary>
    private static void ApplyInfoColorRamp(ParticleProcessMaterial mat, EffectInfoEmitter info)
    {
        float a0 = info.MidAlpha01();
        // alphafade is alpha-units/sec; over `life` seconds it drops a0 by (alphafade/256)*life. Clamp to 0.
        float life = info.Lifetime();
        float a1 = Math.Clamp(a0 - (info.AlphaFade / 256f) * life, 0f, a0);

        var ramp = new Gradient();
        ramp.SetColor(0, new Color(1f, 1f, 1f, a0));
        // For additive/blood, fade fully to transparent at the end so the burst dissipates cleanly.
        ramp.SetColor(1, new Color(1f, 1f, 1f, info.AlphaFade > 0f ? 0f : a1));
        mat.ColorRamp = new GradientTexture1D { Gradient = ramp };
    }

    /// <summary>Composite cache key for <see cref="BuildInfoMesh"/>: everything that determines the mesh+material.</summary>
    private readonly record struct InfoMeshKey(
        ulong Sprite, int Blend, bool Spark, int SparkAspectQ, int GrowRatioQ, int ColorKey, int CellCount);

    // Per-determinant cache of the effectinfo draw mesh+material (the PRIMARY effect path: every burst block and
    // every projectile-trail emitter draws through here). The mesh+material is a pure function of the key below;
    // CellInRange randomises the sprite cell per spawn, so keying by the resolved sprite instance KEEPS the
    // per-emitter sprite variety while sharing the QuadMesh + (StandardMaterial3D | growth ShaderMaterial) and
    // its first-use pipeline compile across emitters. Removes 2–3 Godot Resource allocations per emitter spawn.
    private readonly Dictionary<InfoMeshKey, Mesh> _infoMeshCache = new();

    /// <summary>The billboard mesh for one emitter, blended per the parsed blend mode (add/alpha/invmod) and
    /// textured with the particlefont sprite (<paramref name="sprite"/>, null => a flat tinted quad).
    /// <paramref name="cellCount"/> &gt; 1 enables sprite-sheet particle animation so AnimOffset can randomise
    /// the starting cell per particle (one cell of the horizontal strip per particle). Cached + shared.</summary>
    private Mesh BuildInfoMesh(EffectInfoEmitter info, Color color, Texture2D? sprite, int cellCount = 1,
        float sparkAspect = 8f, float? growRatio = null)
    {
        // Sparks are velocity-stretched in DP (length = max(stretch*0.04*speed, size*0.5), width = size). The
        // GpuParticles node aligns the quad's +Y to velocity (TransformAlign, set in BuildInfoBurst); here we
        // shape the quad so width≈size and length≈aspect*size. The per-particle scale (ScaleMin/Max ≈ 2*size)
        // multiplies both, so a (0.5,aspect) base quad gives world width 0.5*2*size=size and length aspect*size.
        bool isSpark = info.Orientation == EiOrientation.Spark;
        BaseMaterial3D.BlendModeEnum blend = info.Blend switch
        {
            EiBlend.Add => BaseMaterial3D.BlendModeEnum.Add,
            EiBlend.InvMod => BaseMaterial3D.BlendModeEnum.Sub, // inverse-modulate ≈ subtractive darkening
            _ => BaseMaterial3D.BlendModeEnum.Mix,
        };

        var key = new InfoMeshKey(
            sprite is null ? 0UL : sprite.GetInstanceId(),
            (int)blend, isSpark,
            isSpark ? (int)MathF.Round(MathF.Max(1f, sparkAspect)) : 0,   // spark quad length (mesh size)
            growRatio is { } g0 ? (int)MathF.Round(g0 * 16f) : -1,        // growth-shader `grow` uniform (-1 = none)
            ColorKey5(color), cellCount);
        if (_infoMeshCache.TryGetValue(key, out Mesh? cached))
            return cached;

        var quad = new QuadMesh
        {
            Size = isSpark ? new Vector2(0.5f, MathF.Max(1f, sparkAspect)) : new Vector2(1f, 1f),
        };

        // T6: a growing billboard particle (sizeincrease != 0, not a spark) uses the custom growth shader so it
        // expands/shrinks over its life. Only when an atlas sprite is present (the real game) — the no-atlas
        // fallback keeps the flat StandardMaterial3D quad. Everything else uses the proven StandardMaterial3D.
        if (growRatio is { } gr && !isSpark && sprite is not null)
        {
            var grow = new ShaderMaterial { Shader = GrowthShader(blend) };
            grow.SetShaderParameter("grow", gr);
            grow.SetShaderParameter("albedo_tex", sprite);
            quad.Material = grow;
        }
        else
        {
            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = blend,
                // Sparks are oriented by the node's TransformAlign (Y→velocity); leave billboarding OFF so the mesh
                // uses that velocity-aligned transform (which carries the per-particle scale). Everything else uses
                // particle billboarding.
                BillboardMode = isSpark ? BaseMaterial3D.BillboardModeEnum.Disabled : BaseMaterial3D.BillboardModeEnum.Particles,
                // CRITICAL: particle billboarding DISCARDS the per-particle scale (scale_min/max from the process
                // material) unless keep-scale is on (godotengine/godot#74897). Without this the big fire/smoke
                // puffs (scale 66-128) collapsed to their 1×1 base quad — a near-invisible dot — which is why only
                // the sparks (a 0.15×2 base quad that reads even at scale 1) showed. Keep the scale so they render.
                BillboardKeepScale = true,
                VertexColorUseAsAlbedo = true,
                AlbedoColor = color,
                DisableReceiveShadows = true,
            };
            // Sprite-sheet particle animation: each particle starts on a random frame (AnimOffset) so the burst
            // shows a mix of smoke wisps / fire blobs / spark dots rather than identical copies.
            if (cellCount > 1)
            {
                mat.ParticlesAnimHFrames = cellCount;
                mat.ParticlesAnimVFrames = 1;
                mat.ParticlesAnimLoop = false;
            }
            ApplySprite(mat, sprite);
            quad.Material = mat;
        }
        _infoMeshCache[key] = quad;
        return quad;
    }

    /// <summary>
    /// Texture a particle billboard with an atlas sprite. The sprite is the alpha SHAPE (smoke wisp, fire
    /// blob, spark dot, blood drop); the per-particle vertex color (from the process material's color ramp)
    /// tints it (StandardMaterial3D multiplies albedo_texture * albedo_color * vertex_color). The sprite's own
    /// alpha channel hides the transparent atlas margins, so no scissor is needed (and for additive blends the
    /// near-black borders add nothing anyway).
    /// </summary>
    private static void ApplySprite(StandardMaterial3D mat, Texture2D? sprite)
    {
        if (sprite is null)
            return;
        mat.AlbedoTexture = sprite;
        mat.TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear;
    }

    /// <summary>
    /// The dlight pass for spawns the dual-system ROUTER handled (the faithful/modern backends draw only
    /// sprites/decals). Mirrors the DP block loop's light section (cl_particles.c:1631-1652): per defined
    /// block with <c>lightradius</c> &gt; 0 — after the underwater gates, independent of any particle gating —
    /// place the flash at the trail END (originmaxs) for trail-class spawns, else at the effect center.
    /// The flashes are pool-backed (§11 R3) and fade/recycle via this system's <see cref="_Process"/>.
    /// </summary>
    private void SpawnRoutedBlockLights(IReadOnlyList<EffectInfoEmitter> blocks, NVec3 origin, NVec3 velocity,
        bool isTrail, Color? colorOverride)
    {
        NVec3 end = isTrail && velocity != default ? velocity : origin;
        NVec3 center = (origin + end) * 0.5f;
        bool underwater = false, underwaterKnown = false;

        foreach (EffectInfoEmitter info in blocks)
        {
            if (!info.Defined || info.LightRadius <= 0f)
                continue;
            if (info.Underwater || info.NotUnderwater)
            {
                if (!underwaterKnown) // probe lazily; most blocks carry no water gate
                {
                    underwater = Api.Services is not null
                        && (Api.Trace.PointContents(center) & (SuperContents.Water | SuperContents.Slime)) != 0;
                    underwaterKnown = true;
                }
                if (info.Underwater && !underwater) continue;
                if (info.NotUnderwater && underwater) continue;
            }

            SpawnInfoLight(info, isTrail ? end : center, colorOverride);
        }
    }

    /// <summary>
    /// The light block a trail effect would attach to its mover, for the projectile renderer's flight glow —
    /// DP re-adds a per-frame rtlight at the trail head from any lightradius block with no fade
    /// (cl_particles.c:1645-1651 "glowing entity"); a persistent OmniLight on the projectile is the stable
    /// equivalent. Returns the first defined block with lightradius &gt; 0, as (radius, lightcolor).
    /// </summary>
    public (float Radius, NVec3 Color)? TrailLightFor(string effectName)
    {
        EnsureInfoLoaded();
        IReadOnlyList<EffectInfoEmitter>? blocks = LookupInfo(effectName, ResolveEffect(effectName));
        if (blocks is null)
            return null;
        foreach (EffectInfoEmitter info in blocks)
            if (info.Defined && info.LightRadius > 0f)
                return (info.LightRadius, info.LightColor);
        return null;
    }

    // ---------------------------------------------------------------------------------------------
    //  (§11 R3) Pooled effect flash lights. Every lightradius>0 block used to create an OmniLight3D +
    //  a SceneTree Tween + QueueFree per spawn — constant node churn in a firefight (explosions are
    //  several blocks each). The flashes are short (≤1.5 s) and DP itself recycles a fixed dlight slot
    //  array, so a small reused pool + one per-frame fade pass replaces all of it; saturation steals
    //  the OLDEST (most-faded) flash. Lights are absolute-positioned children of this system — a flash
    //  is a positional snapshot (DP CL_AllocLightFlash), it never follows a mover.
    // ---------------------------------------------------------------------------------------------

    private sealed class FxLight
    {
        public OmniLight3D Node = null!;
        public float Age;
        public float Duration;
        public float Energy0;
    }

    /// <summary>Hard cap on simultaneously-live effect flashes (the oldest is recycled past this).</summary>
    private const int MaxFxLights = 24;

    private readonly List<FxLight> _liveFxLights = new();   // append order == age order (oldest first)
    private readonly Queue<FxLight> _freeFxLights = new();

    private FxLight AcquireFxLight()
    {
        if (_freeFxLights.TryDequeue(out FxLight? pooled))
            return pooled;
        if (_liveFxLights.Count >= MaxFxLights)
        {
            FxLight oldest = _liveFxLights[0];
            _liveFxLights.RemoveAt(0);
            return oldest;
        }
        var node = new OmniLight3D { Name = "fx_light", Visible = false };
        AddChild(node);
        return new FxLight { Node = node };
    }

    /// <summary>Light the flash an effectinfo block requests (lightradius/lightcolor/lightradiusfade) at an
    /// absolute Quake-space position. Pool-backed; faded and recycled by <see cref="_Process"/>.</summary>
    private void SpawnInfoLight(EffectInfoEmitter info, NVec3 atQuake, Color? colorOverride)
    {
        // DP color scales lightcolor by the tint; we use the parsed lightcolor (or the override hue) directly.
        Color lcol = colorOverride is { } c
            ? c
            : new Color(info.LightColor.X, info.LightColor.Y, info.LightColor.Z);
        // lightcolor components routinely exceed 1 (e.g. "8 4 1"); normalise into a unit hue + energy.
        float maxc = MathF.Max(1f, MathF.Max(lcol.R, MathF.Max(lcol.G, lcol.B)));

        FxLight l = AcquireFxLight();
        l.Node.Position = Coords.ToGodot(atQuake);
        l.Node.OmniRange = Math.Clamp(info.LightRadius, 1f, 2000f);
        l.Node.LightColor = new Color(lcol.R / maxc, lcol.G / maxc, lcol.B / maxc);
        l.Energy0 = MathF.Min(8f, maxc);
        l.Node.LightEnergy = l.Energy0;
        l.Node.Visible = true;
        // Fade the flash: lightradiusfade is radius-units/sec, so the flash lasts ~radius/fade seconds.
        l.Duration = info.LightRadiusFade > 0f
            ? Math.Clamp(info.LightRadius / info.LightRadiusFade, 0.05f, 1.5f)
            : 0.15f;
        l.Age = 0f;
        _liveFxLights.Add(l);
    }

    /// <summary>The one fx-light ager: linear energy fade over each flash's duration, then release to the
    /// pool. Replaces the per-flash SceneTree Tween + QueueFree.</summary>
    public override void _Process(double delta)
    {
        int count = _liveFxLights.Count;
        if (count == 0)
            return;
        // #30 slowmo/pause: flash-light fades are CSQC dlight decay in Base (cl.time) — scale with the client
        // render-time factor so a paused game holds the flashes instead of fading them out on wall clock.
        float dt = XonoticGodot.Game.Client.ClientRenderTime.ScaleDelta((float)delta);
        for (int i = count - 1; i >= 0; i--)
        {
            FxLight l = _liveFxLights[i];
            l.Age += dt;
            float frac = l.Duration > 0f ? 1f - l.Age / l.Duration : 0f;
            if (frac <= 0f)
            {
                l.Node.Visible = false;
                _liveFxLights.RemoveAt(i);
                _freeFxLights.Enqueue(l);
                continue;
            }
            l.Node.LightEnergy = l.Energy0 * frac;
        }
    }

    /// <summary>The parent node's linger time: the longest particle life across its blocks plus a fade margin.</summary>
    private static float InfoParentLinger(IReadOnlyList<EffectInfoEmitter> blocks)
    {
        float max = 0.5f;
        foreach (EffectInfoEmitter b in blocks)
            if (b.Defined && b.Type != EiType.Decal)
                max = MathF.Max(max, b.Lifetime());
        return max + 0.6f;
    }

    /// <summary>Magnitude of a Quake-space vector mapped to Godot axes (componentwise abs; for radii/extents).</summary>
    private static Vector3 AbsToGodot(NVec3 q)
    {
        Vector3 g = Coords.ToGodot(q);
        return new Vector3(MathF.Abs(g.X), MathF.Abs(g.Y), MathF.Abs(g.Z));
    }

    // =================================================================================================
    //  Lifetime bookkeeping
    // =================================================================================================

    private void Track(Node3D node)
    {
        _live.Enqueue(node);
        while (_live.Count > MaxLiveEffects && _live.TryDequeue(out Node3D? old))
        {
            if (!GodotObject.IsInstanceValid(old))
                continue;
            // (3.2-1) An early-culled pooled burst is still reusable — return it to the free-list rather than
            // free it (ReleaseBurst is a no-op if it already finished via its timer). All other nodes free.
            if (old is GpuParticles3D burst && _burstKey.ContainsKey(burst))
                ReleaseBurst(burst);
            else
                old.QueueFree();
        }
    }

    /// <summary>Queue the node for removal after <paramref name="seconds"/> (one-shot particles are done by then).</summary>
    private void ScheduleFree(Node node, float seconds)
    {
        // The node has already been AddChild'd to this system; if the system is in the tree the child is too.
        SceneTree? tree = IsInsideTree() ? GetTree() : node.GetTree();
        if (tree is null)
        {
            // Not in the tree yet (effect spawned before the system was added) — free on a fixed safety net.
            node.QueueFree();
            return;
        }
        SceneTreeTimer timer = tree.CreateTimer(seconds);
        timer.Timeout += () =>
        {
            if (GodotObject.IsInstanceValid(node))
                node.QueueFree();
        };
    }

    /// <summary>(3.2-1) Like <see cref="ScheduleFree"/> but returns the finished burst to the pool instead of
    /// freeing it. Mirrors ScheduleFree's SceneTreeTimer mechanism; the timeout calls <see cref="ReleaseBurst"/>,
    /// which is idempotent so a node already released by the Track-cull is handled harmlessly.</summary>
    private void ScheduleRelease(GpuParticles3D node, float seconds)
    {
        SceneTree? tree = IsInsideTree() ? GetTree() : node.GetTree();
        if (tree is null)
        {
            // No tree to time against — fall back to the pooled release immediately (still better than a free).
            ReleaseBurst(node);
            return;
        }
        SceneTreeTimer timer = tree.CreateTimer(seconds);
        timer.Timeout += () => ReleaseBurst(node);
    }

    /// <summary>(3.2-1) On teardown, free every idle node still parked in the burst pool. Pooled idle nodes are
    /// detached (no parent) and not in <see cref="_live"/>, so without this they'd leak as orphaned handles when
    /// the system leaves the tree (map change / shutdown).</summary>
    public override void _ExitTree()
    {
        foreach (Stack<GpuParticles3D> stack in _burstPool.Values)
        {
            while (stack.Count > 0)
            {
                GpuParticles3D n = stack.Pop();
                if (GodotObject.IsInstanceValid(n))
                    n.QueueFree();
            }
        }
        _burstPool.Clear();
        _burstKey.Clear();
    }
}
