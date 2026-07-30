using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Simulation;

namespace XonoticGodot.Server;

/// <summary>
/// The server-side entity-service decorator that fixes the "clients aren't in the entity table" gap
/// (the QC server core puts every client edict in the global entity list, so <c>find(world, classname,
/// "player")</c> and <c>findradius</c> see players — but in this port the engine
/// <see cref="EntityService"/>.<see cref="EntityService.Spawn"/> can only mint a bare
/// <see cref="Entity"/>, never a <see cref="Player"/> subclass, so <see cref="ClientManager"/> keeps its
/// players out of the table). This wrapper keeps a server-owned registry of live <see cref="Player"/>
/// edicts and <em>merges</em> them into the find/radius queries, so gameplay that scans by classname or
/// radius (splash damage, item pickup, bot target discovery, monster aggro) finds clients exactly like
/// QuakeC — without touching the read-only engine assembly.
///
/// It delegates every other entity operation (spawn/remove/setorigin/…) to the underlying engine table.
/// Newly minted (non-player) entities still live in the real table; players live in <see cref="_players"/>
/// and are linked here so their <c>AbsMin</c>/<c>AbsMax</c> stay current for radius tests.
///
/// <see cref="GameWorld"/> publishes a <see cref="ServerServices"/> wrapping the sim's
/// <see cref="EngineServices"/> as the ambient <see cref="Api.Services"/>, and registers/unregisters
/// players here from <see cref="ClientManager"/>.
/// </summary>
public sealed class ServerEntityService : IEntityService
{
    private readonly EntityService _inner;
    private readonly List<Player> _players = new();

    public ServerEntityService(EntityService inner) => _inner = inner;

    /// <summary>The underlying engine entity table (the authoritative non-client edict store).</summary>
    public EntityService Inner => _inner;

    /// <summary>The registered live player edicts merged into find/radius (QC the client list).</summary>
    public IReadOnlyList<Player> Players => _players;

    // ---- player registry (QC ClientConnect/ClientDisconnect linking the edict into the global list) ----

    /// <summary>
    /// Register a player so the find/radius builtins see it (QC the client edict joins the entity list on
    /// connect). Idempotent. Relinks its bounds so radius tests are immediately correct.
    /// </summary>
    public void RegisterPlayer(Player p)
    {
        if (!_players.Contains(p))
        {
            _players.Add(p);
            LinkPlayer(p);
        }
    }

    /// <summary>Unregister a player (QC ClientDisconnect removes the edict from the list).</summary>
    public void UnregisterPlayer(Player p) => _players.Remove(p);

    /// <summary>Recompute a player's AbsMin/AbsMax and relink it into the engine broadphase (QC SV_LinkEdict).
    /// Routing through the engine's LinkEdict keeps the area-grid footprint in step with the player on a
    /// teleport/spawn SetOrigin — the sim only relinks clients once per tick (after ClientMove), so without
    /// this a same-tick radius/box query would still find the player in its OLD cells.</summary>
    public void LinkPlayer(Player p) => _inner.LinkEdict(p);

    // ---- IEntityService: spawn/remove/spatial delegate straight to the engine table ----

    /// <summary>The engine table as an indexable list (<see cref="IEntityService.All"/>). Per the interface
    /// contract this is the NON-CLIENT table — players are not merged (they live in <see cref="Players"/>).</summary>
    public IReadOnlyList<Entity>? All => _inner.All;

    public Entity Spawn() => _inner.Spawn();

    public void Remove(Entity e)
    {
        if (e is Player p)
            _players.Remove(p);
        _inner.Remove(e);
    }

    public void SetOrigin(Entity e, Vector3 origin)
    {
        if (e is Player)
        {
            // Players don't live in the engine table, so SetOrigin there is a no-op for them; do the
            // QC SV_SetOrigin work (origin + relink) here so their bounds track for radius queries.
            e.Origin = origin;
            e.OldOrigin = origin;
            LinkPlayer((Player)e);
            return;
        }
        _inner.SetOrigin(e, origin);
    }

    public void SetSize(Entity e, Vector3 mins, Vector3 maxs)
    {
        if (e is Player)
        {
            e.Mins = mins;
            e.Maxs = maxs;
            e.Size = maxs - mins;
            LinkPlayer((Player)e);
            return;
        }
        _inner.SetSize(e, mins, maxs);
    }

    public void SetModel(Entity e, string model)
    {
        if (e is Player) { e.Model = model; return; }
        _inner.SetModel(e, model);
    }

    // ---- find / radius: MERGE the engine table with the player registry (the actual fix) ----

    /// <summary>
    /// QC <c>find(world, classname, X)</c>: every non-freed engine-table entity whose classname matches,
    /// PLUS every registered player whose classname matches ("player" by default). This is what makes
    /// <c>FindByClass("player")</c> return clients in this port.
    /// </summary>
    public IEnumerable<Entity> FindByClass(string className)
    {
        foreach (Entity e in _inner.FindByClass(className))
            yield return e;
        for (int i = 0; i < _players.Count; i++)
        {
            Player p = _players[i];
            if (!p.IsFreed && p.ClassName == className)
                yield return p;
        }
    }

    /// <summary>Allocation-free <see cref="FindByClass(string)"/>: the inner call clears + fills
    /// <paramref name="results"/> from the engine table, then matching registered players are appended.</summary>
    public void FindByClass(string className, List<Entity> results)
    {
        _inner.FindByClass(className, results); // clears + fills from the engine table (alloc-free)
        for (int i = 0; i < _players.Count; i++)
        {
            Player p = _players[i];
            if (!p.IsFreed && p.ClassName == className) results.Add(p);
        }
    }

    /// <summary>
    /// QC <c>findradius(org, r)</c>: every non-freed engine-table entity within the radius PLUS every
    /// registered player within range. Lets splash damage, item triggers, and bot scans hit clients.
    ///
    /// IMPORTANT (the splash-damage-multiplication fix): since the D1 area-grid broadphase, the ENGINE query
    /// already returns the live players — the sim links every client into the entity grid each tick
    /// (<c>SimulationLoop</c>: <c>LinkEdict(c)</c> after ClientMove). Re-yielding such a player from
    /// <see cref="_players"/> made RadiusDamage apply the same blast to them twice (on top of the grid's own
    /// negative-index dedup bug — see <c>EntityAreaGrid.Slot</c> — players took 2-5x damage/knockback per
    /// blast). The merge below now only adds a player the engine results don't already contain (covers a
    /// freshly registered player the grid hasn't linked yet), using the same nearest-point-on-bbox metric as
    /// the engine query (DP <c>sv_gameplayfix_findradiusdistancetobox</c>).
    /// </summary>
    public IEnumerable<Entity> FindInRadius(Vector3 origin, float radius)
    {
        var results = new List<Entity>();
        _inner.FindInRadius(origin, radius, results);
        float r2 = radius * radius;
        for (int i = 0; i < _players.Count; i++)
        {
            Player p = _players[i];
            if (p.IsFreed) continue;
            Vector3 nearest = Vector3.Clamp(origin, p.Origin + p.Mins, p.Origin + p.Maxs);
            if ((nearest - origin).LengthSquared() > r2) continue;
            if (!results.Contains(p))
                results.Add(p);
        }
        return results;
    }

    /// <summary>Allocation-free <see cref="FindInRadius(Vector3, float)"/>: the inner call clears + fills
    /// <paramref name="results"/> from the engine area grid (nearest-point metric), then registered players the
    /// engine results don't already contain are appended using the SAME nearest-point-on-bbox metric AND the
    /// same <c>!results.Contains</c> dedup as the iterator overload — so this alloc-free path returns the
    /// identical set, including the splash single-application fix (a player already linked into the grid is NOT
    /// added a second time, which previously made RadiusDamage apply a blast 2-5x).</summary>
    public void FindInRadius(Vector3 origin, float radius, List<Entity> results)
    {
        _inner.FindInRadius(origin, radius, results); // clears + fills (nearest-point metric, area-grid backed)
        float r2 = radius * radius;
        for (int i = 0; i < _players.Count; i++)
        {
            Player p = _players[i];
            if (p.IsFreed) continue;
            Vector3 nearest = Vector3.Clamp(origin, p.Origin + p.Mins, p.Origin + p.Maxs);
            if ((nearest - origin).LengthSquared() > r2) continue;
            if (!results.Contains(p))
                results.Add(p);
        }
    }

    /// <summary>QC/DP <c>findbox(mins, maxs)</c>: every engine-table entity whose linked bounds overlap the box
    /// (precise AABB, area-grid backed) PLUS registered players in the box the engine results don't already
    /// contain — the same fresh-player merge + <c>!Contains</c> dedup as
    /// <see cref="FindInRadius(Vector3, float, List{Entity})"/>, so a just-registered client the grid hasn't
    /// linked yet is still found (telefrag targets are mostly players). Players are tested on live
    /// <c>Origin+Mins/Maxs</c> exactly like the radius merge (their <c>AbsMin</c> may not be linked yet).</summary>
    public void FindInBox(Vector3 mins, Vector3 maxs, List<Entity> results)
    {
        _inner.FindInBox(mins, maxs, results); // clears + fills (precise AABB overlap, area-grid backed)
        for (int i = 0; i < _players.Count; i++)
        {
            Player p = _players[i];
            if (p.IsFreed) continue;
            Vector3 pMin = p.Origin + p.Mins, pMax = p.Origin + p.Maxs;
            if (mins.X > pMax.X || maxs.X < pMin.X
                || mins.Y > pMax.Y || maxs.Y < pMin.Y
                || mins.Z > pMax.Z || maxs.Z < pMin.Z) continue;
            if (!results.Contains(p))
                results.Add(p);
        }
    }
}

/// <summary>
/// The server's <see cref="IEngineServices"/> facade — a thin decorator over the sim's
/// <see cref="EngineServices"/> that swaps in <see cref="ServerEntityService"/> for the entity service so
/// clients are visible to find/radius (see that type). Trace/cvar/sound/model/clock pass straight through
/// to the engine implementations. <see cref="GameWorld"/> installs this as <see cref="Api.Services"/>.
/// </summary>
public sealed class ServerServices : IEngineServices
{
    private readonly EngineServices _inner;

    public ServerEntityService ServerEntities { get; }

    public ServerServices(EngineServices inner)
    {
        _inner = inner;
        ServerEntities = new ServerEntityService(inner.EntityTable);
    }

    public ITraceService Trace => _inner.Trace;
    public IEntityService Entities => ServerEntities;
    public ICvarService Cvars => _inner.Cvars;
    public ISoundService Sound => _inner.Sound;
    public IModelService Models => _inner.Models;
    public IGameClock Clock => _inner.Clock;
    // MUST forward Surfaces to the real SurfaceService — without this it fell through to the IEngineServices
    // default (NullSurfaceService), so every server-side getsurface* query returned empty. That broke the
    // warpzone brush->plane auto-derivation (WarpzoneManager.DerivePlaneFromBrush), so NO map warpzones ever
    // spawned (0 zones) → no teleport AND nothing for the portal renderer to match (dark placeholder).
    public ISurfaceService Surfaces => _inner.Surfaces;
}
