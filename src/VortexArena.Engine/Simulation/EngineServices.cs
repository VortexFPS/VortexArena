using System.Numerics;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;

namespace XonoticGodot.Engine.Simulation;

/// <summary>
/// Concrete <see cref="IEngineServices"/> — the C# reimplementation of the QuakeC builtins facade
/// (planning/specs/engine-services-facade.md). Aggregates the entity table, the collision/trace
/// service, the sim clock, and the cvar/sound/model services. A host wires it up once at startup:
/// <code>Api.Services = engineServices;</code>
/// </summary>
public sealed class EngineServices : IEngineServices
{
    public EntityService EntityTable { get; }
    public TraceService TraceImpl { get; }
    public CvarService CvarsImpl { get; }
    public SoundService SoundImpl { get; }
    public ModelService ModelsImpl { get; }
    public GameClock ClockImpl { get; }
    public SurfaceService SurfacesImpl { get; }

    public ITraceService Trace => TraceImpl;
    public IEntityService Entities => EntityTable;
    public ICvarService Cvars => CvarsImpl;
    public ISoundService Sound => SoundImpl;
    public IModelService Models => ModelsImpl;
    public IGameClock Clock => ClockImpl;
    public ISurfaceService Surfaces => SurfacesImpl;

    /// <summary>
    /// The map's compiled PVS, backing <c>checkpvs</c> (<see cref="ITraceService.CheckPvs"/>). The host that
    /// loads the BSP sets <c>new BspPvs(bsp)</c> here; null leaves every PVS query conservatively visible.
    /// </summary>
    public XonoticGodot.Formats.Bsp.BspPvs? Pvs { get => TraceImpl.Pvs; set => TraceImpl.Pvs = value; }

    /// <summary>
    /// Swap the static collision world the trace service clips against, in place (keeping the entity table, clock,
    /// cvars and models). A pure network client boots on a flat prediction floor and calls this once it has loaded
    /// the server's real map BSP, so the predicted local player collides with real geometry.
    /// </summary>
    public void SetCollisionWorld(CollisionWorld world) => TraceImpl.SetCollisionWorld(world);

    /// <param name="world">The collision world this facade traces against.</param>
    /// <param name="sharedCvars">
    /// An optional process-wide cvar store to reuse instead of a fresh one. The menu front-end creates the
    /// store once at boot (stock config + the user's saved prefs), then hands it to each match so a setting
    /// changed in the menu is live in-game and survives map changes. Null → a private store (tests, headless).
    /// </param>
    public EngineServices(CollisionWorld world, CvarService? sharedCvars = null)
    {
        EntityTable = new EntityService();
        TraceImpl = new TraceService(world, EntityTable);
        CvarsImpl = sharedCvars ?? new CvarService();
        SoundImpl = new SoundService();
        ModelsImpl = new ModelService();
        ClockImpl = new GameClock();
        SurfacesImpl = new SurfaceService(ModelsImpl);
        EntityTable.Models = ModelsImpl; // setmodel resolves index + brush bounds through the model catalog
    }
}

/// <summary>
/// The entity table — spawn/remove/find plus the spatial builtins (setorigin/setsize/setmodel) that
/// relink the entity's bounds. Successor to the QC entity-management builtins (spawn, remove, find,
/// findradius, setorigin, setsize, setmodel). Also serves the trace service its solid-entity list
/// (<see cref="TraceService.IEntityProvider"/>) and the physics context its relink hook.
/// </summary>
public sealed class EntityService : IEntityService, TraceService.IEntityProvider
{
    private readonly List<Entity> _all = new();          // dense, indexed by Entity.Index
    private readonly List<Entity> _solid = new();        // cache of non-freed solid entities for traces
    private readonly Queue<int> _freeSlots = new();
    private bool _solidDirty = true;

    /// <summary>The dynamic-entity broadphase (D1) — DP's SV_AreaGrid. Kept in sync with the flat <see cref="_all"/>
    /// list: every entity is linked on spawn + relinked on every <see cref="LinkEdict"/> + unlinked on free, so a
    /// box query returns the same set the old flat scan did, with far fewer candidates.</summary>
    private readonly Collision.EntityAreaGrid _grid = new();

    public IReadOnlyList<Entity> All => _all;

    /// <summary>
    /// The model catalog, used by <see cref="SetModel"/> to resolve a model name to its index and (for
    /// inline <c>*N</c> brush models) its bounds. Wired by <see cref="EngineServices"/> at construction.
    /// </summary>
    public ModelService? Models { get; set; }

    /// <summary>
    /// DP's PRVM_EDICT_MARK_SETORIGIN_CAUGHT counter: bumped on every <see cref="SetOrigin"/>. SV_Impact /
    /// SV_PushEntity sample it across a touch call to detect a setorigin-driven teleport and abort the move.
    /// </summary>
    public int SetOriginEpoch { get; private set; }

    public Entity Spawn()
    {
        Entity e;
        if (_freeSlots.Count > 0)
        {
            int idx = _freeSlots.Dequeue();
            e = new Entity { Index = idx };
            _all[idx] = e;
        }
        else
        {
            e = new Entity { Index = _all.Count };
            _all.Add(e);
        }
        _solidDirty = true;
        _grid.Link(e); // link from spawn (at its default bounds) so the grid set == the flat _all set for FindInRadius
        return e;
    }

    public void Remove(Entity e)
    {
        if (e.IsFreed) return;
        e.IsFreed = true;
        e.Solid = Solid.Not;
        e.Think = null;
        e.Touch = null;
        e.NextThink = 0f;
        if (e.Index >= 0 && e.Index < _all.Count)
            _freeSlots.Enqueue(e.Index);
        _solidDirty = true;
        _grid.Unlink(e);
    }

    // --- spatial builtins (relink bounds; DP SV_LinkEdict recomputes absmin/absmax) ---

    public void SetOrigin(Entity e, Vector3 origin)
    {
        e.Origin = origin;
        e.OldOrigin = origin;
        // DP marks the edict so SV_Impact/SV_PushEntity can detect a teleport that happens inside a touch.
        SetOriginEpoch++;
        LinkEdict(e);
    }

    public void SetSize(Entity e, Vector3 mins, Vector3 maxs)
    {
        e.Mins = mins;
        e.Maxs = maxs;
        e.Size = maxs - mins;
        LinkEdict(e);
    }

    /// <summary>
    /// SV_SetModel (sv_main.c) + SV_GetModelByIndex: record the model name, resolve its precache index, and
    /// — for an inline brush model (a name beginning with <c>*</c>, e.g. <c>"*3"</c>) — copy the model's
    /// mins/maxs onto the entity (DP <c>SetMinMaxSize(ent, mod->normalmins, mod->normalmaxs)</c>) and stash
    /// the brush set so a brush-aware trace can clip against it. Non-brush (alias/sprite) models just set
    /// the name + index; their hull stays whatever <see cref="SetSize"/> gave them.
    /// </summary>
    public void SetModel(Entity e, string model)
    {
        e.Model = model;
        e.ModelIndex = Models?.IndexOf(model) ?? 0;

        if (Models is not null && Models.TryGetModel(model, out ModelService.ModelDef def))
        {
            if (def.IsBrushModel)
            {
                // inline brush model: its bounds become the entity's hull (like a func_door/func_plat).
                e.Mins = def.Mins;
                e.Maxs = def.Maxs;
                e.Size = def.Maxs - def.Mins;
            }
        }
        LinkEdict(e);
    }

    /// <summary>
    /// SV_LinkEdict (sv_phys.c:804): recompute AbsMin/AbsMax from origin+mins/maxs, relink into the entity
    /// area-grid broadphase, and mark the solid cache dirty. AbsMin/AbsMax are kept exact for traces; the
    /// trigger touch test (<see cref="PhysicsContext.TouchAreaGrid"/>) applies DP's 1-unit areagrid expansion
    /// itself. The static-brush broadphase grid is the CollisionWorld's; the entity broadphase is the
    /// <see cref="EntityAreaGrid"/> (D1) — DP's SV_AreaGrid, replacing the former flat O(n) solid scan.
    /// </summary>
    public void LinkEdict(Entity e)
    {
        if (e.IsFreed) return;
        e.AbsMin = e.Origin + e.Mins;
        e.AbsMax = e.Origin + e.Maxs;
        _grid.Link(e);
        _solidDirty = true;
    }

    /// <summary>
    /// Gather every entity whose XY footprint overlaps [<paramref name="mins"/>,<paramref name="maxs"/>] into
    /// <paramref name="results"/> (cleared first), de-duplicated — the entity-area-grid broadphase (D1, DP
    /// <c>World_EntitiesInBox</c>). A conservative superset; the caller applies the precise per-entity test.
    /// Replaces a flat scan of every entity at the trace / trigger / splash-radius call sites.
    /// </summary>
    public void EntitiesInBox(Vector3 mins, Vector3 maxs, List<Entity> results)
        => _grid.EntitiesInBox(mins, maxs, results);

    public IEnumerable<Entity> FindByClass(string className)
    {
        for (int i = 0; i < _all.Count; i++)
        {
            var e = _all[i];
            if (!e.IsFreed && e.ClassName == className)
                yield return e;
        }
    }

    /// <summary>Allocation-free <c>find(classname)</c> (D1 residual): clears + fills <paramref name="results"/>
    /// with every non-freed entity whose classname matches, one pass over the dense table — no iterator alloc.</summary>
    public void FindByClass(string className, List<Entity> results)
    {
        results.Clear();
        for (int i = 0; i < _all.Count; i++)
        {
            Entity e = _all[i];
            if (!e.IsFreed && e.ClassName == className) results.Add(e);
        }
    }

    public IEnumerable<Entity> FindInRadius(Vector3 origin, float radius)
    {
        // Compat path: materialize via the list-filling overload. Callers on the hot splash-damage path should
        // prefer that overload to avoid this per-call list allocation (D1 / §3.2-6).
        var results = new List<Entity>();
        FindInRadius(origin, radius, results);
        return results;
    }

    /// <summary>
    /// Allocation-free, area-grid-accelerated <c>findradius</c> (D1): fills <paramref name="results"/> with every
    /// entity within <paramref name="radius"/> of <paramref name="origin"/>, measured to the NEAREST POINT on
    /// each entity's bbox (DP <c>sv_gameplayfix_findradiusdistancetobox</c>, default 1). The broadphase queries
    /// only the grid cells overlapping the radius box instead of scanning every entity; the precise nearest-point
    /// test then filters the candidates in place, so the result set is identical to the old flat scan.
    /// </summary>
    public void FindInRadius(Vector3 origin, float radius, List<Entity> results)
    {
        // Broadphase: gather candidates whose XY footprint overlaps the radius box (cleared + filled in results).
        // An entity within radius (nearest-point) necessarily has its AABB overlap origin±radius, so the grid
        // never misses one — it's a conservative superset the precise test below trims.
        var r3 = new Vector3(radius, radius, radius);
        _grid.EntitiesInBox(origin - r3, origin + r3, results);

        float r2 = radius * radius;
        int w = 0;
        for (int i = 0; i < results.Count; i++)
        {
            Entity e = results[i];
            if (e.IsFreed) continue;
            // Measure to the NEAREST POINT on the bbox, not its center (the blaster-jump 15%-miss fix): a
            // jumping player's center sits ~34u above their feet, so a floor blast under them must still land.
            Vector3 nearest = Vector3.Clamp(origin, e.Origin + e.Mins, e.Origin + e.Maxs);
            if ((nearest - origin).LengthSquared() <= r2)
                results[w++] = e;
        }
        results.RemoveRange(w, results.Count - w);
    }

    /// <summary>
    /// Allocation-free, area-grid-accelerated <c>findbox</c> (DP <c>VM_findbox</c>/<c>World_EntitiesInBox</c>):
    /// fills <paramref name="results"/> with every entity whose linked bounds (<c>AbsMin</c>/<c>AbsMax</c>)
    /// overlap [<paramref name="mins"/>, <paramref name="maxs"/>]. The precise AABB test is applied HERE (DP
    /// does the same engine-side), so callers replacing the QC findradius(origin, huge-radius) + per-entity
    /// boxesoverlap pattern (upstream xonotic-data b6e02fe3 — the telefrag loop) need no follow-up box filter.
    /// </summary>
    public void FindInBox(Vector3 mins, Vector3 maxs, List<Entity> results)
    {
        // Broadphase: the grid cells overlapping the box (conservative superset — XY-partitioned, cleared+filled).
        _grid.EntitiesInBox(mins, maxs, results);

        int w = 0;
        for (int i = 0; i < results.Count; i++)
        {
            Entity e = results[i];
            if (e.IsFreed) continue;
            // Precise: linked AbsMin/AbsMax overlap (QC boxesoverlap semantics — touching counts).
            if (mins.X <= e.AbsMax.X && maxs.X >= e.AbsMin.X
                && mins.Y <= e.AbsMax.Y && maxs.Y >= e.AbsMin.Y
                && mins.Z <= e.AbsMax.Z && maxs.Z >= e.AbsMin.Z)
                results[w++] = e;
        }
        results.RemoveRange(w, results.Count - w);
    }

    // --- TraceService.IEntityProvider ---

    public IReadOnlyList<Entity> SolidEntities
    {
        get
        {
            if (_solidDirty)
            {
                _solid.Clear();
                for (int i = 0; i < _all.Count; i++)
                {
                    var e = _all[i];
                    if (!e.IsFreed && e.Solid >= Solid.Trigger)
                        _solid.Add(e);
                }
                _solidDirty = false;
            }
            return _solid;
        }
    }

    /// <summary>Force the solid cache to rebuild (call when an entity's Solid changes outside the builtins).</summary>
    public void InvalidateSolidCache() => _solidDirty = true;

    /// <summary>
    /// <see cref="TraceService.IEntityProvider.TryGetEntityBrushModel"/>: resolve a SOLID_BSP entity's
    /// model-local clip brushes and its local→world transform. The brushes come from the model catalog
    /// (<see cref="ModelService.ModelDef.LocalClipBrushes"/> — loaded BSP brushes, or the AABB box fallback);
    /// the transform is <c>Matrix4x4_CreateFromQuakeEntity(origin, angles, scale 1)</c>.
    /// </summary>
    public bool TryGetEntityBrushModel(Entity e, out IReadOnlyList<Collision.Brush> localBrushes, out Collision.EntityMatrix toWorld)
    {
        localBrushes = System.Array.Empty<Collision.Brush>();
        toWorld = default;

        if (Models is null || string.IsNullOrEmpty(e.Model))
            return false;
        if (!Models.TryGetModel(e.Model, out ModelService.ModelDef def) || !def.IsBrushModel)
            return false;

        IReadOnlyList<Collision.Brush> brushes = def.LocalClipBrushes();
        if (brushes.Count == 0)
            return false;

        localBrushes = brushes;
        toWorld = Collision.EntityMatrix.FromQuakeEntity(e.Origin, e.Angles);
        return true;
    }
}

/// <summary>The simulation clock (QC time/frametime globals). Driven by <see cref="SimulationLoop"/>.</summary>
public sealed class GameClock : IGameClock
{
    public float Time { get; internal set; }
    public float FrameTime { get; internal set; }
}

/// <summary>
/// Dictionary-backed cvar store (QC cvar/cvar_set/registercvar). Honors Xonotic cvar names.
///
/// Beyond the bare <see cref="ICvarService"/> get/set/register, this carries the few extra capabilities the
/// menu front-end needs from the engine cvar store (the same ones DP's real cvar store exposes): a
/// <see cref="Changed"/> notification so several menu widgets bound to one cvar — and the
/// enable-on-dependency logic (<c>setDependent</c>) — stay in sync; per-cvar <see cref="GetDefault"/> /
/// <see cref="ResetToDefault"/> tracking for the reset dialogs; enumeration (<see cref="Names"/>) for the
/// cvar list; and an <see cref="MarkArchived"/>/<see cref="ArchivedNames"/> set so the menu can persist the
/// user's preferences to <c>user://config.cfg</c> (DP's <c>seta</c> archive flag). All additive and inert for
/// the headless server, which only ever uses get/set/register.
/// </summary>
public sealed class CvarService : ICvarService
{
    private sealed class Var
    {
        public string Value = "";
        public float FloatValue;
        public CvarFlags Flags;
        public string Default = "";
        public bool HasDefault;
        public bool Archived;
        // DP CF_DEFAULTSET: the Default has been snapshotted as the authoritative shipped baseline (LockDefaults).
        public bool DefaultLocked;
        // DP CF_ALLOCATED: this cvar was CREATED by a bare set/seta (a cfg line, the console, or user-config) —
        // NOT declared by a code Register. Only an allocated cvar with no locked default is force-saved (the
        // persist escape); a Register clears this, so a code-declared cvar (even one that registers after
        // LockDefaults, like ClientSettings.ApplyAll's cl_*/hud_*/snd_* set) is saved only when it differs from
        // its registered default — never force-dumped. This is the half my first cut dropped (it gated the escape
        // on !DefaultLocked alone), which is why every late-registered-but-unchanged cvar bloated config.cfg.
        public bool Allocated;
    }

    // WS1 stage 3 (sv_threaded): DUAL-MODE backing. Default = a plain Dictionary — the tick's hot path does
    // hundreds of GetFloat reads per player-move, and a ConcurrentDictionary there measured +32% ms/tick
    // (0.44 → 0.57 at 4 players), unacceptable as a default tax. A THREADED listen server calls
    // EnableConcurrentReads() before its worker starts: the backing swaps to a ConcurrentDictionary so the
    // MAIN thread's display/feed reads (music cdtrack, endscreen cvars, slowmo autopause…) can't race the sim
    // worker's writes (a plain Dictionary read during a resize is undefined) — and the read overhead lands on
    // the WORKER, off the render thread. One logical writer either way; per-Var field pairs (Value/FloatValue)
    // are written in place, so a concurrent reader can transiently see a fresh Value with the previous
    // FloatValue — benign for the display-read class.
    private Dictionary<string, Var>? _varsFast = new(StringComparer.Ordinal);
    private System.Collections.Concurrent.ConcurrentDictionary<string, Var>? _varsSafe;

    /// <summary>Swap to the thread-safe backing (idempotent). Call BEFORE any second thread touches the store.</summary>
    public void EnableConcurrentReads()
    {
        if (_varsSafe is not null) return;
        _varsSafe = new System.Collections.Concurrent.ConcurrentDictionary<string, Var>(_varsFast!, StringComparer.Ordinal);
        _varsFast = null;
    }

    private bool TryGetVar(string name, out Var v)
        => _varsSafe is not null ? _varsSafe.TryGetValue(name, out v!) : _varsFast!.TryGetValue(name, out v!);
    private void PutVar(string name, Var v)
    {
        if (_varsSafe is not null) _varsSafe[name] = v;
        else _varsFast![name] = v;
    }
    private bool HasVar(string name)
        => _varsSafe is not null ? _varsSafe.ContainsKey(name) : _varsFast!.ContainsKey(name);
    private IEnumerable<KeyValuePair<string, Var>> AllVars()
        => _varsSafe is not null ? _varsSafe : _varsFast!;
    private IReadOnlyCollection<string> VarNames()
        => _varsSafe is not null ? (IReadOnlyCollection<string>)_varsSafe.Keys : _varsFast!.Keys;

    /// <summary>
    /// Names that some code <see cref="GetFloat"/>/<see cref="GetString"/>-read while ABSENT from the store —
    /// i.e. cvars that were never registered nor set by a cfg, so the read silently returned 0/"" and the name
    /// is invisible to <c>cvarlist</c>/<c>search</c> (the <c>vid_vsync</c> class of bug). Surfaced by the
    /// <c>cvar_orphans</c> console command and (at <c>developer&gt;0</c>) logged once per name. A transiently
    /// absent name that is later <see cref="Set"/> stays in this set but is filtered out by
    /// <see cref="UnregisteredReadNames"/>, which only reports names still missing.
    /// </summary>
    private readonly HashSet<string> _unregisteredReads = new(StringComparer.Ordinal);

    /// <summary>Reentrancy guard: emitting the "unregistered read" log itself reads cvars (e.g. <c>developer</c>),
    /// which would recurse back into <see cref="NoteMissing"/> while that name is still absent.</summary>
    private bool _noting;

    /// <summary>
    /// Raised after a cvar's value actually changes (the name is the argument). Lets menu widgets re-read a
    /// cvar another widget/command just wrote, and drives <c>setDependent</c> enable/disable reactivity. Fired
    /// only on a real value change, so the bulk config load (no subscribers yet) costs nothing.
    /// </summary>
    public event Action<string>? Changed;

    public float GetFloat(string name)
    {
        if (TryGetVar(name, out var v)) return v.FloatValue;
        NoteMissing(name);
        return 0f;
    }

    public string GetString(string name)
    {
        if (TryGetVar(name, out var v)) return v.Value;
        NoteMissing(name);
        return "";
    }

    /// <summary>Record (and, at <c>developer&gt;0</c>, log once) a read of a cvar that isn't in the store.</summary>
    private void NoteMissing(string name)
    {
        if (_noting) return;                    // read triggered by our own logging — ignore
        lock (_unregisteredReads)               // cross-thread read-misses (threaded listen server) — see _vars
        {
            if (!_unregisteredReads.Add(name)) return; // already noted this name
        }
        _noting = true;
        try
        {
            // Log.Trace self-gates at developer>0, so this is silent in normal play. The set is still recorded
            // either way, so `cvar_orphans` can dump the full list retroactively after turning developer on.
            Log.Trace($"cvar \"{name}\" read but never registered or set — defaults to 0/\"\" and is hidden from " +
                      "cvarlist/search. Register a default (e.g. ClientSettings.RegisterEngineClientDefaults) or " +
                      "run `cvar_orphans`.");
        }
        finally { _noting = false; }
    }

    public void Set(string name, string value)
    {
        if (!TryGetVar(name, out var v))
        {
            // First value seen is the (inferred) baseline default, and the cvar is "allocated" (DP CF_ALLOCATED) —
            // created by a set, not a code Register. A later Register promotes it (clears Allocated + adopts the
            // real default), so an inferred default never masquerades as authoritative once code declares one.
            v = new Var { Default = value, HasDefault = true, Allocated = true };
            PutVar(name, v);
        }
        if ((v.Flags & CvarFlags.ReadOnly) != 0)
            return;
        bool changed = !string.Equals(v.Value, value, StringComparison.Ordinal);
        v.Value = value;
        v.FloatValue = ParseFloat(value);
        if (changed)
            Changed?.Invoke(name);
    }

    public void Register(string name, string defaultValue, CvarFlags flags = CvarFlags.None)
    {
        if (TryGetVar(name, out var existing))
        {
            // Idempotent for the VALUE (keep whatever a cfg/user already set), but a Register DECLARES the cvar:
            // fold in the flags and clear Allocated (it's no longer a bare set). Adopt the registered default as the
            // authoritative baseline UNLESS LockDefaults already fixed one from the packaged cfg tree — the packaged
            // default wins over a code fallback (DP keeps the allocated cvar's defstring). This promotion is what
            // lets a client cvar that registers AFTER LockDefaults (ClientSettings.ApplyAll) be saved only when it
            // differs from its real default, instead of being force-written by the allocated escape.
            existing.Flags |= flags;
            existing.Allocated = false;
            if (!existing.DefaultLocked) { existing.Default = defaultValue; existing.HasDefault = true; }
            return;
        }
        PutVar(name, new Var
        {
            Value = defaultValue,
            FloatValue = ParseFloat(defaultValue),
            Flags = flags,
            Default = defaultValue,
            HasDefault = true,
            // Allocated stays false: a Register-declared cvar has an authoritative default (its registered value),
            // so it is persisted only when changed — never force-saved by the allocated escape.
        });
    }

    // ---- menu-facing extras (the DP cvar-store features the front-end binds to) ---------------------------

    /// <summary>Whether a cvar exists in the store (set or registered).</summary>
    public bool Has(string name) => HasVar(name);

    /// <summary>Every known cvar name (for the cvar-list dialog / search). The concurrent backing's Keys is a
    /// point-in-time snapshot (runtime type ReadOnlyCollection) — exactly right for cross-thread listing.</summary>
    public IReadOnlyCollection<string> Names => VarNames();

    /// <summary>
    /// Names read while absent and STILL absent now — the live "orphan" cvars (read but never registered/set).
    /// Filters out any name that has since been <see cref="Set"/>, so transient pre-set reads don't show up.
    /// Backs the <c>cvar_orphans</c> console command. Note: a listen server keeps its OWN private store, so
    /// server-only cvars (<c>sv_*</c>, <c>g_*</c>) surface in that store's list / its developer-log, not this one.
    /// </summary>
    public IEnumerable<string> UnregisteredReadNames
    {
        get
        {
            string[] noted;
            lock (_unregisteredReads) // snapshot vs cross-thread Adds
            {
                noted = new string[_unregisteredReads.Count];
                _unregisteredReads.CopyTo(noted);
            }
            foreach (string n in noted)
                if (!HasVar(n))
                    yield return n;
        }
    }

    /// <summary>The cvar's baseline default (registered default, or the first value a cfg set). "" if unknown.</summary>
    public string GetDefault(string name) => TryGetVar(name, out var v) ? v.Default : "";

    /// <summary>True when the cvar's current value differs from its default (the reset dialogs flag these).</summary>
    public bool IsModified(string name)
        => TryGetVar(name, out var v) && v.HasDefault && !string.Equals(v.Value, v.Default, StringComparison.Ordinal);

    /// <summary>Restore a cvar to its default value (the reset-to-default affordance).</summary>
    public void ResetToDefault(string name)
    {
        if (TryGetVar(name, out var v) && v.HasDefault)
            Set(name, v.Default);
    }

    /// <summary>
    /// Re-apply the user's overrides from one store onto another: for every cvar in <paramref name="from"/> that
    /// the user actually CHANGED (<see cref="IsModified"/> — value differs from its cfg/registered default) AND that
    /// <paramref name="to"/> already <see cref="Has"/>, copy the value across (skipping any name in
    /// <paramref name="exclude"/>). Used by the listen server to carry console/menu cvar overrides onto a freshly
    /// (re)booted world store across a map change, without clobbering values <paramref name="to"/> loaded from a
    /// map/ruleset cfg that the user never touched. One-way and idempotent (<see cref="Set"/> skips no-op writes).
    /// </summary>
    public static void BackfillModified(CvarService from, CvarService to,
        IReadOnlySet<string>? exclude = null)
    {
        foreach (string name in from.VarNames())
        {
            if (exclude is not null && exclude.Contains(name)) continue;
            if (to.HasVar(name) && from.IsModified(name))
                to.Set(name, from.GetString(name));
        }
    }

    /// <summary>Mark a cvar as archived (DP <c>seta</c>) so the menu persists it to the user config.</summary>
    public void MarkArchived(string name)
    {
        if (TryGetVar(name, out var v))
            v.Archived = true;
    }

    /// <summary>True if the cvar is archived (declared <c>seta</c>/<see cref="CvarFlags.Save"/>, or menu-touched).</summary>
    public bool IsArchived(string name)
        => TryGetVar(name, out var v) && (v.Archived || (v.Flags & CvarFlags.Save) != 0);

    /// <summary>Every archived cvar name (what the menu writes to <c>user://config.cfg</c> as <c>seta</c>).</summary>
    public IEnumerable<string> ArchivedNames
    {
        get
        {
            foreach (var kv in AllVars())
                if (kv.Value.Archived || (kv.Value.Flags & CvarFlags.Save) != 0)
                    yield return kv.Key;
        }
    }

    /// <summary>
    /// DP <c>Cvar_LockDefaults</c>: snapshot every cvar's CURRENT value as its authoritative default. Call this
    /// once after the stock cfg tree has loaded but BEFORE layering the user's saved overrides, so
    /// <see cref="IsModified"/> and <see cref="ArchivedNamesToPersist"/> measure deltas against the shipped
    /// baseline rather than against whatever value the user happened to set last session.
    /// </summary>
    public void LockDefaults()
    {
        foreach (var kv in AllVars())
        {
            Var v = kv.Value;
            v.Default = v.Value;
            v.HasDefault = true;
            v.DefaultLocked = true;
        }
    }

    /// <summary>
    /// The archived cvars to write to <c>user://config.cfg</c> — DP <c>Cvar_WriteVariables</c>'s EXACT rule
    /// (cvar.c:992): <c>archive AND (value != defstring OR (CF_ALLOCATED AND !CF_DEFAULTSET))</c>. So a cvar is
    /// written when it differs from its default, OR when it is an <em>allocated</em> cvar (created by a bare
    /// set/seta, not a code Register) whose default was never locked — the genuinely user-created cvar we can't
    /// prove is at any default, so we keep it. A code-declared cvar (<see cref="Register"/> → not allocated) is
    /// written ONLY when changed, even if it registered after <see cref="LockDefaults"/>. The net effect: settings
    /// the user moved off the shipped default are saved; everything left at default is omitted — a lean diff, not
    /// a dump of every touched-or-registered cvar.
    /// </summary>
    public IEnumerable<string> ArchivedNamesToPersist
    {
        get
        {
            foreach (var kv in AllVars())
            {
                var v = kv.Value;
                if (!(v.Archived || (v.Flags & CvarFlags.Save) != 0))
                    continue;
                bool modified = v.HasDefault && !string.Equals(v.Value, v.Default, StringComparison.Ordinal);
                if (modified || (v.Allocated && !v.DefaultLocked))
                    yield return kv.Key;
            }
        }
    }

    private static float ParseFloat(string s)
        => float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f;
}

/// <summary>
/// A single server→client sound broadcast (DP SV_StartSound message): which entity emitted it, on what
/// channel, the precached sample, and its volume/attenuation. The headless sim has no audio device, so a
/// sound is an EVENT the host (a listen server's client, or the net layer) renders/transmits.
///
/// <see cref="Loop"/> marks a persistent looping sound keyed by <c>(Source, Channel)</c> (QC <c>loopsound</c>);
/// <see cref="Stop"/> marks the SND_Null channel-stop that ends one. A normal one-shot has both false. The pair
/// is mutually exclusive in practice (a stop carries an empty <see cref="Sample"/>).
/// </summary>
public readonly record struct SoundEvent(
    Entity? Source, SoundChannel Channel, string Sample, float Volume, float Attenuation,
    System.Numerics.Vector3 Origin, bool Loop = false, bool Stop = false, float Pitch = 1f);

/// <summary>
/// Server-side sound service — the C# successor to QC <c>sound()</c> / DP <c>SV_StartSound</c>. On the
/// headless sim a sound has no audible effect, so <see cref="Play"/>/<see cref="Stop"/> broadcast a
/// <see cref="SoundEvent"/> on <see cref="Broadcast"/>; the host (listen-server client, net replication, or a
/// test) subscribes to render or transmit it. This replaces the former no-op with the real server→client
/// emission path, including DP's entity+channel loop/stop model (a loop is replaced by a later loop on the
/// same entity+channel, and ended by <see cref="Stop"/>).
/// </summary>
public sealed class SoundService : ISoundService
{
    // S5 (sv_threaded) thread-ownership note: on the listen/host path EACH world owns ITS OWN SoundService
    // instance (the server world's, reached via Api.Sound on the sim thread). The only Broadcast subscriber is
    // ServerNet.OnSoundEmitted, which appends to ServerNet._soundQueue — and ServerNet.Tick (which both raises
    // and drains those sounds) runs entirely under the host's _simGate lock on the server-sim worker, so the
    // raise→capture→flush sequence is single-threaded by construction. No cross-thread access to this instance;
    // no locking is added here. (With sv_threaded 0 everything is on one thread, unchanged.)
    /// <summary>Fired for every <see cref="Play"/>/<see cref="Stop"/> — the server→client sound emission (SV_StartSound).</summary>
    public event Action<SoundEvent>? Broadcast;

    public void Play(Entity e, SoundChannel channel, string sample, float volume = 1f, float attenuation = 1f, bool loop = false, float pitch = 1f)
    {
        // QC sound() macro wraps EVERY positional emit in sound_allowed(MSG_BROADCAST, e) (common/sounds/sound.qh:43,
        // all.qc:9-25): when bot_sound_monopoly=1 a real-client-sourced sound is suppressed (and the owner-walk
        // re-homes a corpse/projectile emit to its originating player first). Most port emits already pass through
        // SoundSystem.PlayOn/SpamSound (which gate), but a handful of call sites hit Api.Sound.Play DIRECTLY (weapon
        // UNAVAILABLE, monster/turret/map-object cues) and would otherwise bypass the gate. Applying it here — the
        // single SV_StartSound chokepoint every Api.Sound.Play flows through — closes that bypass with one check.
        // IsAllowed early-returns true when bot_sound_monopoly==0 (the default) so this is a no-op in normal play,
        // and is idempotent for the SoundSystem family (re-checking returns the same answer). A null emitter passes.
        if (!SoundAllowedGate.IsAllowed(e)) return;
        // SV_StartSound emits from the entity's box center (DP uses ent.origin + 0.5*(mins+maxs)).
        Vector3 origin = e.Origin + (e.Mins + e.Maxs) * 0.5f;
        // (perf 2.1) snd.play: the listen host's in-process sound mirror runs INSIDE this event — a cold
        // sample decode here lands in whatever gameplay scope emitted (the mp.weapon melt hunt).
        using (Prof.Sample("snd.play"))
            Broadcast?.Invoke(new SoundEvent(e, channel, sample, volume, attenuation, origin, Loop: loop, Pitch: pitch));
    }

    public void PlayAt(Vector3 point, SoundChannel channel, string sample, float volume = 1f, float attenuation = 1f)
        // A point sound with NO emitter entity (DP CSQC wr_impacteffect impact sounds, played at the trace
        // endpoint). Source is null → the wire encodes net-id 0, so the client plays it at this fixed point
        // with no emitter-follow and no (entity,channel) keying — exactly right for a stationary impact.
        => Broadcast?.Invoke(new SoundEvent(null, channel, sample, volume, attenuation, point));

    public void Stop(Entity e, SoundChannel channel)
    {
        // SND_Null on a channel: end whatever sound is playing on (e, channel). Carries the entity origin so the
        // wire can still key the stop by the emitter's net id; the sample is empty (nothing new to play).
        Vector3 origin = e.Origin + (e.Mins + e.Maxs) * 0.5f;
        Broadcast?.Invoke(new SoundEvent(e, channel, "", 0f, 0f, origin, Stop: true));
    }
}

/// <summary>
/// Model / tag service — the C# successor to the QC model builtins (<c>precache_model</c>,
/// <c>setmodel</c>'s index lookup, <c>gettaginfo</c>, <c>setattachment</c>) and DP's model catalog
/// (SV_GetModelByIndex, Mod_Alias_GetTagMatrix). Models are registered by the map/asset loader; each
/// carries its bounds (for <see cref="EntityService.SetModel"/>) and an optional set of named tags
/// (local transforms) that <see cref="TryGetTag"/> composes with the entity transform to give a tag's
/// world-space basis — the real Mod_Alias_GetTagMatrix path, not the identity fallback.
/// </summary>
public sealed class ModelService : IModelService
{
    /// <summary>A model tag: a named local transform (origin + Euler angles) on the model's skeleton.</summary>
    public readonly record struct ModelTag(string Name, Vector3 Origin, Vector3 Angles);

    /// <summary>A registered model: its precache index, bounds, brush-model flag, and named tags.</summary>
    public sealed class ModelDef
    {
        public string Name = "";
        public int Index;
        public Vector3 Mins;
        public Vector3 Maxs;
        public bool IsBrushModel;                       // inline "*N" map brush model (bounds = the brush's)
        public readonly Dictionary<string, ModelTag> Tags = new(StringComparer.Ordinal);

        /// <summary>
        /// The model's collision brushes in MODEL-LOCAL space (origin-relative), supplied by the BSP loader
        /// for inline <c>*N</c> brush models. When a brush-aware trace clips against a SOLID_BSP entity it
        /// transforms these by the entity's matrix (Collision_ClipToGenericEntity). Null until the loader runs:
        /// in that case the trace falls back to the model AABB (DP's <c>bodymins/bodymaxs</c> box, the same
        /// degradation DP applies when a SOLID_BSP model has no <c>TraceBrush</c> function).
        /// </summary>
        public Collision.Brush[]? CollisionBrushes;

        /// <summary>
        /// The model's render surfaces in MODEL-LOCAL space, supplied by the BSP/model loader for the
        /// <c>getsurface*</c> builtins (<see cref="SurfaceService"/>). Null until a loader attaches geometry
        /// (e.g. <c>BspSurfaceBuilder</c> for inline <c>*N</c> models); the surface queries then return empty.
        /// </summary>
        public IReadOnlyList<ModelSurface>? Surfaces;

        // Cached local-space AABB box-brush fallback (one-element array) used when CollisionBrushes is null.
        // Built lazily and keyed on the current Mins/Maxs so a re-sized model rebuilds it.
        private Collision.Brush[]? _boxBrush;
        private Vector3 _boxMins, _boxMaxs;

        /// <summary>
        /// The local-space clip geometry for a brush-model trace: the loaded <see cref="CollisionBrushes"/>
        /// when present, otherwise a single AABB box brush spanning [Mins,Maxs] (the SOLID_BSP fallback). The
        /// box carries SUPERCONTENTS_SOLID so it blocks like a real brush. Returns an empty array for a
        /// non-brush / zero-size model.
        /// </summary>
        public IReadOnlyList<Collision.Brush> LocalClipBrushes()
        {
            if (CollisionBrushes is { Length: > 0 })
                return CollisionBrushes;
            if (Maxs.X <= Mins.X && Maxs.Y <= Mins.Y && Maxs.Z <= Mins.Z)
                return System.Array.Empty<Collision.Brush>();
            if (_boxBrush is null || _boxMins != Mins || _boxMaxs != Maxs)
            {
                _boxBrush = new[] { Collision.Brush.FromBox(Mins, Maxs, Collision.SuperContents.Solid) };
                _boxMins = Mins; _boxMaxs = Maxs;
            }
            return _boxBrush;
        }
    }

    private readonly List<ModelDef> _models = new();    // dense; index 0 reserved for "no model" (DP)
    private readonly Dictionary<string, ModelDef> _byName = new(StringComparer.Ordinal);

    // per-entity model-tag attachment (DP .tag_entity/.tag_index), kept off Entity (engine-side state).
    private readonly Dictionary<Entity, (Entity parent, string tag)> _attachments = new();

    public ModelService()
    {
        // index 0 == "" == no model (DP reserves modelindex 0).
        var none = new ModelDef { Name = "", Index = 0 };
        _models.Add(none);
        _byName[""] = none;
    }

    /// <summary>
    /// precache_model: register a model (idempotent) and return its index. <paramref name="mins"/>/
    /// <paramref name="maxs"/> are the model bounds (used by setmodel for inline brush models);
    /// <paramref name="isBrushModel"/> marks a <c>*N</c> inline map brush model.
    /// </summary>
    public int Register(string name, Vector3 mins = default, Vector3 maxs = default, bool isBrushModel = false)
    {
        if (_byName.TryGetValue(name, out var existing))
            return existing.Index;
        var def = new ModelDef
        {
            Name = name,
            Index = _models.Count,
            Mins = mins,
            Maxs = maxs,
            IsBrushModel = isBrushModel || (name.Length > 0 && name[0] == '*'),
        };
        _models.Add(def);
        _byName[name] = def;
        return def.Index;
    }

    /// <summary>Register a named tag (local transform) on a model (DP Mod_Alias tag table).</summary>
    public void RegisterTag(string modelName, string tagName, Vector3 localOrigin, Vector3 localAngles)
    {
        if (!_byName.TryGetValue(modelName, out var def))
        {
            Register(modelName);
            def = _byName[modelName];
        }
        def.Tags[tagName] = new ModelTag(tagName, localOrigin, localAngles);
    }

    /// <summary>SV_GetModelIndex: the precache index of a model name (0 if unregistered / empty).</summary>
    public int IndexOf(string name) => _byName.TryGetValue(name, out var d) ? d.Index : 0;

    /// <summary>Look up a registered model by name.</summary>
    public bool TryGetModel(string name, out ModelDef def)
    {
        if (_byName.TryGetValue(name, out var d)) { def = d; return true; }
        def = null!;
        return false;
    }

    /// <summary>
    /// gettaginfo (Mod_Alias_GetTagMatrix): resolve <paramref name="tagName"/>'s world transform for
    /// <paramref name="e"/>. Composes the entity transform (origin + angles) with the tag's local
    /// transform: world = entity · tag. Returns true when the tag was actually found on the entity's model;
    /// otherwise falls back to the entity's own basis at its origin (and returns false).
    /// </summary>
    public bool TryGetTag(Entity e, string tagName, out Vector3 origin, out Vector3 forward, out Vector3 right, out Vector3 up)
    {
        XonoticGodot.Common.Math.QMath.AngleVectors(e.Angles, out Vector3 ef, out Vector3 er, out Vector3 eu);

        if (_byName.TryGetValue(e.Model, out var def) && def.Tags.TryGetValue(tagName, out var tag))
        {
            // local tag basis
            XonoticGodot.Common.Math.QMath.AngleVectors(tag.Angles, out Vector3 tf, out Vector3 tr, out Vector3 tu);

            // world tag origin = entity origin + entity-basis * local tag origin
            origin = e.Origin
                + ef * tag.Origin.X
                + er * tag.Origin.Y
                + eu * tag.Origin.Z;

            // world tag basis = entity basis composed with the tag's local basis.
            // DP composes the rotation matrices; here we rotate each local axis into world space.
            forward = ef * tf.X + er * tf.Y + eu * tf.Z;
            right   = ef * tr.X + er * tr.Y + eu * tr.Z;
            up      = ef * tu.X + er * tu.Y + eu * tu.Z;
            return true;
        }

        // no tag data: identity at the entity's origin (DP behavior when the tag is missing).
        origin = e.Origin;
        forward = ef; right = er; up = eu;
        return false;
    }

    /// <summary>
    /// setattachment: attach <paramref name="e"/> to <paramref name="parent"/>'s tag. Records the parent
    /// (engine <see cref="Entity.Aiment"/>, consumed by the FOLLOW physics + the renderer) and the tag name
    /// so the renderer / a server-side follow can place the entity on the tag each frame.
    /// </summary>
    public void SetAttachment(Entity e, Entity parent, string tagName)
    {
        e.Aiment = parent;
        if (string.IsNullOrEmpty(tagName))
            _attachments.Remove(e);
        else
            _attachments[e] = (parent, tagName);
    }

    /// <summary>The recorded model-tag attachment for an entity (parent + tag), if any.</summary>
    public bool TryGetAttachment(Entity e, out Entity parent, out string tag)
    {
        if (_attachments.TryGetValue(e, out var a)) { parent = a.parent; tag = a.tag; return true; }
        parent = null!; tag = ""; return false;
    }
}
