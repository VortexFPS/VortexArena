using System.Globalization;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server.Bot;

/// <summary>
/// Waypoint type flags (QC WAYPOINTFLAG_*, server/bot/api.qh). Only the bits that affect navigation
/// behaviour in this port are wired up; the editor-only bits (PROTECTED/USEFUL/DEAD_END) are carried
/// through verbatim so a round-trip of the <c>.waypoints</c> file preserves them.
/// </summary>
[Flags]
public enum WaypointFlags
{
    None = 0,
    Generated = 1 << 23, // auto-generated (item/teleporter waypoint), not written to .waypoints
    Item = 1 << 22,
    Teleport = 1 << 21, // teleports, warpzones and jumppads (a single wp00 destination link)
    Personal = 1 << 19,
    Protected = 1 << 18,
    Useful = 1 << 17,
    DeadEnd = 1 << 16,
    Ladder = 1 << 15,
    Jump = 1 << 14,    // bot must jump along the outgoing link
    CustomJp = 1 << 13,
    Crouch = 1 << 12,  // bot must crouch while traversing
    Support = 1 << 11,
}

/// <summary>
/// A single navigation node — the C# successor to a QuakeC <c>spawnfunc_waypoint</c> edict
/// (server/bot/default/waypoints.qc). A waypoint is either a point (Mins == Maxs == 0, a "wp") or a
/// box (a "waybox", e.g. a teleporter trigger volume); for a box, <see cref="Mins"/>/<see cref="Maxs"/>
/// are the half-extents relative to <see cref="Origin"/> and <see cref="IsBox"/> is true.
///
/// In QC the outgoing links live in 32 flat <c>.entity wp00..wp31</c> fields with parallel
/// <c>wpXXmincost</c> costs; here they collapse to a single <see cref="Links"/> adjacency list.
/// </summary>
public sealed class Waypoint
{
    /// <summary>Center of the waypoint (QC <c>.origin</c>).</summary>
    public Vector3 Origin;

    /// <summary>Box half-extents relative to <see cref="Origin"/> (QC <c>.mins</c>); zero for a point waypoint.</summary>
    public Vector3 Mins;

    /// <summary>Box half-extents relative to <see cref="Origin"/> (QC <c>.maxs</c>); zero for a point waypoint.</summary>
    public Vector3 Maxs;

    public WaypointFlags Flags;

    /// <summary>
    /// Static danger bias (QC <c>.dmg</c>, set by botframe_updatedangerousobjects). Added to a route's running
    /// cost when this waypoint is reached (navigation_markroutes: <c>cost2 = cost + wp.dmg</c>), so routes
    /// statically detour around lava/rocket zones instead of only braking at the last moment. 0 = no danger.
    /// </summary>
    public float Danger;

    /// <summary>Dense index into <see cref="WaypointNetwork.Nodes"/> (assigned on add). -1 until added.</summary>
    public int Index = -1;

    /// <summary>
    /// Bot-visit counter (QC <c>.cnt</c> on waypoints). Incremented each time a bot rates this waypoint as
    /// its assault target approach-point so bots spread across waypoints near the same objective rather than
    /// all converging on the single nearest one (QC <c>havocbot_goalrating_ast_targets</c>:
    /// <c>++best.cnt</c>). Reset to 0 when the network is rebuilt for a new map.
    /// </summary>
    public int VisitCount;

    /// <summary>Outgoing links (QC <c>wp00..wp31</c>). Each carries a precomputed travel cost.</summary>
    public readonly List<WaypointLink> Links = new();

    public bool IsBox => Mins != Vector3.Zero || Maxs != Vector3.Zero;

    /// <summary>World-space lower bound (QC <c>.absmin</c>).</summary>
    public Vector3 AbsMin => Origin + Mins;

    /// <summary>World-space upper bound (QC <c>.absmax</c>).</summary>
    public Vector3 AbsMax => Origin + Maxs;

    /// <summary>Box center (QC <c>(absmin + absmax) * 0.5</c>), == <see cref="Origin"/> for a point.</summary>
    public Vector3 Center => (AbsMin + AbsMax) * 0.5f;

    public bool HasFlag(WaypointFlags f) => (Flags & f) != 0;

    /// <summary>
    /// Closest point on this waypoint to <paramref name="from"/> (QC navigation_markroutes_checkwaypoint
    /// box-clamp). For a point waypoint this is just <see cref="Origin"/>.
    /// </summary>
    public Vector3 ClosestPoint(Vector3 from)
    {
        if (!IsBox) return Origin;
        var lo = AbsMin;
        var hi = AbsMax;
        return new Vector3(
            QMath.Clamp(from.X, lo.X, hi.X),
            QMath.Clamp(from.Y, lo.Y, hi.Y),
            QMath.Clamp(from.Z, lo.Z, hi.Z));
    }
}

/// <summary>An outgoing link from one waypoint to another with a precomputed traversal cost.</summary>
public readonly struct WaypointLink
{
    public readonly Waypoint To;
    /// <summary>Travel cost (seconds-ish; QC waypoint_getlinkcost). 999 marks a known-unwalkable link.</summary>
    public readonly float Cost;

    public WaypointLink(Waypoint to, float cost)
    {
        To = to;
        Cost = cost;
    }
}

/// <summary>
/// The waypoint navigation graph for a map — the C# successor to QuakeC's <c>g_waypoints</c> intrusive
/// list plus the link bookkeeping in waypoints.qc. Owns the nodes, the spatial "nearest" query, the
/// <c>.waypoints</c>/<c>.waypoints.cache</c> loaders, and an A* shortest-path search.
///
/// The QC engine keeps Dijkstra state (wpcost/enemy back-pointers) directly on each waypoint edict and
/// recomputes it every strategy frame (navigation_markroutes). Here that transient state is kept out of
/// <see cref="Waypoint"/> and computed locally per search so the graph itself stays immutable and the
/// network is safe to share across bots.
/// </summary>
public sealed class WaypointNetwork
{
    private readonly List<Waypoint> _nodes = new();

    public IReadOnlyList<Waypoint> Nodes => _nodes;
    public int Count => _nodes.Count;

    // Spatial hash for O(1) FindAt during link loading. LoadLinks/LoadHardwiredLinks match each cache line's
    // from/to origin back to a node within 1u (cache) / 5u (hardwired); the naive scan was O(nodes) PER line, so
    // loading the shipped 76 KB catharsis cache cost ~2M distance checks on the single first-bot frame — a visible
    // movement stutter when bots join (cf. graceful hitch recovery, which only SMOOTHS such a spike). Built lazily
    // and rebuilt if the node set changed; FindAt is only called in the link-load phase, where node count is fixed
    // (AutoLink, the only other graph mutation, adds links not nodes — and runs only when no cache ships). Cell =
    // FindHashCell so any FindAt radius ≤ that is covered by a 3×3×3 (27-cell) probe around the query cell.
    private const float FindHashCell = 5f;
    private Dictionary<(int, int, int), List<Waypoint>>? _findHash;
    private int _findHashCount = -1;

    // ---- cost model (QC waypoint_getlinearcost / waypoint_gettravelcost) ----

    /// <summary>QC autocvar_sv_maxspeed default (xonotic-server.cfg). Used as the cost denominator.</summary>
    public float MaxSpeed = 320f;

    /// <summary>QC autocvar_sv_gravity default. Drives the fall-time term of travel cost.</summary>
    public float Gravity = 800f;

    /// <summary>Apparent jump height (QC jumpheight_vec.z = sv_jumpvelocity^2/(2*sv_gravity) = 270^2/1600 ≈ 45.56); falls beyond this add a time penalty.</summary>
    public float JumpHeight = 45.5625f;

    /// <summary>QC autocvar_sv_jumpvelocity default. Used to derive <see cref="JumpHeightTime"/>.</summary>
    public float JumpVelocity = 270f;

    /// <summary>QC bot_ai_bunnyhop_skilloffset (default 7): at this bot skill or above, routing assumes the 1.25x bunnyhop speedup.</summary>
    public int BunnyhopSkillOffset = 7;

    /// <summary>
    /// Bot skill this network's costs are computed for (QC global <c>skill</c>). At/above
    /// <see cref="BunnyhopSkillOffset"/> the linear cost uses the 1.25x bunnyhop speedup, matching
    /// waypoint_getlinearcost. The shared graph is built once per map at the server's current skill.
    /// </summary>
    public int Skill;

    /// <summary>QC jumpheight_time = sv_jumpvelocity / sv_gravity (bot.qc:620); the rise-time of a jump, added to the JUMP-source fall cost.</summary>
    public float JumpHeightTime => Gravity > 0f ? JumpVelocity / Gravity : 0f;

    /// <summary>
    /// Linear (distance/speed) cost (QC waypoint_getlinearcost). The bunnyhop 1.25x speedup applies at
    /// <see cref="Skill"/> &gt;= <see cref="BunnyhopSkillOffset"/>, matching Base so high-skill route costs
    /// (and their tie-breaks) line up.
    /// </summary>
    public float LinearCost(float dist)
        => dist / (Skill >= BunnyhopSkillOffset ? MaxSpeed * 1.25f : MaxSpeed);

    /// <summary>Linear cost while submerged (QC waypoint_getlinearcost_underwater): ~0.7x walk speed.</summary>
    public float LinearCostUnderwater(float dist) => dist / (MaxSpeed * 0.7f);

    /// <summary>Linear cost while crouched (QC waypoint_getlinearcost_crouched): ~0.5x walk speed.</summary>
    public float LinearCostCrouched(float dist) => dist / (MaxSpeed * 0.5f);

    /// <summary>
    /// Travel cost between two world points (QC waypoint_gettravelcost / waypoint_gettravelcost_inwater) —
    /// linear xy/3d cost plus a fall-time term for big drops, with the QC underwater (slower) and crouched
    /// (slower) half-path adjustments. <paramref name="fromInWater"/>/<paramref name="toInWater"/> mark
    /// whether each endpoint is submerged, and <paramref name="crouch"/> that the link is traversed crouched.
    /// </summary>
    public float TravelCost(Vector3 from, Vector3 to, bool fromIsJump = false,
        bool fromInWater = false, bool toInWater = false, bool crouch = false)
        => TravelCost(from, to, fromIsJump, fromInWater, toInWater, crouch, crouch);

    /// <summary>
    /// Travel cost with per-endpoint crouch flags, a faithful port of waypoint_gettravelcost (waypoints.qc):
    /// both-submerged and both-crouched early-return the pure underwater/crouched linear cost; the fall term
    /// uses the JUMP-source variant (jumpheight_time + sqrt((height+jumpheight)/(g/2))) when
    /// <paramref name="fromIsJump"/>, else sqrt(height/(g/2)); and a single submerged/crouched endpoint
    /// averages the normal cost with the slower variant ((c + slow)/2).
    /// </summary>
    public float TravelCost(Vector3 from, Vector3 to, bool fromIsJump,
        bool fromInWater, bool toInWater, bool fromCrouch, bool toCrouch)
    {
        float dist = (to - from).Length();

        // QC early returns: both endpoints submerged → pure underwater cost; both crouched → pure crouched cost.
        if (fromInWater && toInWater)
            return LinearCostUnderwater(dist);
        if (fromCrouch && toCrouch)
            return LinearCostCrouched(dist);

        float c = LinearCost(dist);

        float height = from.Z - to.Z;
        if (height > JumpHeight && Gravity > 0f)
        {
            float heightCost = fromIsJump
                ? JumpHeightTime + MathF.Sqrt((height + JumpHeight) / (Gravity / 2f)) // JUMP-source: add the rise-time
                : MathF.Sqrt(height / (Gravity / 2f));
            c = LinearCost(new Vector3(to.X - from.X, to.Y - from.Y, 0f).Length()); // xy distance cost
            if (heightCost > c)
                c = heightCost;
        }

        // QC half-path adjustments: a single submerged or crouched endpoint averages the normal cost with the slow one.
        if (fromInWater || toInWater)
            return (c + LinearCostUnderwater(dist)) / 2f;
        if (fromCrouch || toCrouch)
            return (c + LinearCostCrouched(dist)) / 2f;

        return c;
    }

    // ---- construction / linking ----

    /// <summary>Spawn a point waypoint (QC waypoint_spawn with m1 == m2).</summary>
    public Waypoint Add(Vector3 origin, WaypointFlags flags = WaypointFlags.None)
        => Add(origin, Vector3.Zero, Vector3.Zero, flags);

    /// <summary>
    /// Spawn a waypoint/waybox (QC waypoint_spawn). <paramref name="mins"/>/<paramref name="maxs"/> are
    /// box half-extents relative to <paramref name="origin"/>; pass zero for a point waypoint.
    /// </summary>
    public Waypoint Add(Vector3 origin, Vector3 mins, Vector3 maxs, WaypointFlags flags = WaypointFlags.None)
    {
        var wp = new Waypoint { Origin = origin, Mins = mins, Maxs = maxs, Flags = flags, Index = _nodes.Count };
        _nodes.Add(wp);
        return wp;
    }

    /// <summary>
    /// Add a directed link a-&gt;b with auto-computed cost (QC waypoint_addlink). Pass
    /// <paramref name="bidirectional"/> to also add b-&gt;a.
    /// </summary>
    public void Link(Waypoint a, Waypoint b, bool bidirectional = false, float? cost = null)
    {
        float c = cost ?? TravelCost(a.ClosestPoint(b.Origin), b.ClosestPoint(a.Origin), a.HasFlag(WaypointFlags.Jump));
        AddLinkOnce(a, b, c);
        if (bidirectional)
            AddLinkOnce(b, a, cost ?? TravelCost(b.ClosestPoint(a.Origin), a.ClosestPoint(b.Origin), b.HasFlag(WaypointFlags.Jump)));
    }

    private static void AddLinkOnce(Waypoint a, Waypoint b, float cost)
    {
        for (int i = 0; i < a.Links.Count; i++)
        {
            if (ReferenceEquals(a.Links[i].To, b))
            {
                a.Links[i] = new WaypointLink(b, cost);
                return;
            }
        }
        a.Links.Add(new WaypointLink(b, cost));
    }

    /// <summary>
    /// Auto-link every waypoint to nearby waypoints within <paramref name="maxDist"/> (QC
    /// waypoint_schedulerelinkall → waypoint_addlink_for). Each candidate link is gated by a
    /// <see cref="BotTracewalk"/> reachability test (the QC tracewalk: can a player actually walk/step/swim
    /// from a to b?), so links only form along traversable ground — matching real Xonotic instead of pure
    /// distance/LOS. Teleport/jump/ladder/custom waypoints keep their hand-authored outgoing links
    /// (QC WPFLAGMASK_NORELINK). Falls back to a straight LOS gate when no collision world is available
    /// (<paramref name="requireLineOfSight"/>), or pure distance when that's off too.
    /// </summary>
    public void AutoLink(float maxDist = 1050f, bool requireLineOfSight = true, int maxLinksPerNode = 32)
    {
        bool canTrace = Api.Services is not null;
        // QC WPFLAGMASK_NORELINK = TELEPORT|LADDER|JUMP|CUSTOM_JP|SUPPORT. waypoint_addlink (waypoints.qc:1135)
        // refuses an OUTGOING auto-link from such a source UNLESS it's JUMP or SUPPORT (those reach the
        // box/JUMP/SUPPORT forbid block in waypoint_think instead). So TELEPORT/LADDER/CUSTOM_JP sources never
        // get an outgoing auto-link, but they can still RECEIVE incoming links.
        const WaypointFlags addlinkRefuse = WaypointFlags.Teleport | WaypointFlags.Ladder | WaypointFlags.CustomJp;

        // QC waypoint_think (waypoints.qc:1160-1239): for each unordered pair, link both directions independently.
        // bbox overlap → unconditional bidirectional link; else PVS cull, XY-distance cull (1050, or 100 for a
        // crouch↔normal pair), then per-direction tracewalk gated by the box/JUMP/SUPPORT forbid rules.
        for (int ai = 0; ai < _nodes.Count; ai++)
        {
            var a = _nodes[ai];
            for (int bi = ai + 1; bi < _nodes.Count; bi++)
            {
                var b = _nodes[bi];

                // boxes overlap → link both ways (subject only to the addlink NORELINK refusal). (QC :1162-1168)
                if (BoxesOverlap(a, b))
                {
                    if ((a.Flags & addlinkRefuse) == 0) LinkAuto(a, b);
                    if ((b.Flags & addlinkRefuse) == 0) LinkAuto(b, a);
                    continue;
                }

                // PVS cull: a candidate the source can't possibly see is never linked (QC checkpvs, :1172).
                // Conservative — CheckPvs returns true on an unvised map, so this only prunes genuine occlusion.
                if (canTrace && !Api.Trace.CheckPvs(a.Origin, b.Origin))
                    continue;

                // XY-distance cull. Base shortens crouch↔normal pairs to 100qu (rough cost makes long crouch
                // links wasteful); a crouch↔crouch pair keeps the full 1050. (QC :1187-1206)
                bool aCrouch = a.HasFlag(WaypointFlags.Crouch);
                bool bCrouch = b.HasFlag(WaypointFlags.Crouch);
                float maxd = ((aCrouch || bCrouch) && !(aCrouch && bCrouch)) ? 100f : maxDist;
                Vector3 dv = b.ClosestPoint(a.Origin) - a.ClosestPoint(b.Origin);
                dv.Z = 0f;
                if (dv.LengthSquared() >= maxd * maxd)
                    continue;

                Vector3 ab = a.ClosestPoint(b.Origin);
                Vector3 ba = b.ClosestPoint(a.Origin);

                // outgoing a→b: forbidden if a is a box / JUMP / SUPPORT source, or a refused (NORELINK) source.
                // (QC :1212-1223; the it.SUPPORT_WP incoming-forbid has no port back-pointer and is omitted.)
                if ((a.Flags & addlinkRefuse) == 0 && !ForbidOutgoing(a))
                {
                    bool reach = !canTrace || BotTracewalk.CanWalk(ab, ba, _playerMins, _playerMaxs, b.IsBox ? (b.AbsMax.Z - ba.Z) : 0f);
                    if (reach && a.Links.Count < maxLinksPerNode) LinkAuto(a, b);
                }

                // reverse b→a: same forbid rules with the roles swapped. (QC :1226-1237)
                if ((b.Flags & addlinkRefuse) == 0 && !ForbidOutgoing(b))
                {
                    bool reach = !canTrace || BotTracewalk.CanWalk(ba, ab, _playerMins, _playerMaxs, a.IsBox ? (a.AbsMax.Z - ab.Z) : 0f);
                    if (reach && b.Links.Count < maxLinksPerNode) LinkAuto(b, a);
                }
            }
        }
    }

    // QC waypoint_think (:1212/:1226): a box / JUMP / SUPPORT source forbids its OUTGOING auto-links.
    private static bool ForbidOutgoing(Waypoint w)
        => w.IsBox || (w.Flags & (WaypointFlags.Jump | WaypointFlags.Support)) != 0;

    // bbox overlap (QC boxesoverlap on absmin/absmax).
    private static bool BoxesOverlap(Waypoint a, Waypoint b)
    {
        Vector3 amin = a.AbsMin, amax = a.AbsMax, bmin = b.AbsMin, bmax = b.AbsMax;
        return amin.X <= bmax.X && amax.X >= bmin.X
            && amin.Y <= bmax.Y && amax.Y >= bmin.Y
            && amin.Z <= bmax.Z && amax.Z >= bmin.Z;
    }

    // Add a directed auto-link a→b with the faithful per-endpoint crouch/jump-source travel cost.
    private void LinkAuto(Waypoint a, Waypoint b)
    {
        Vector3 from = a.ClosestPoint(b.Origin);
        Vector3 to = b.ClosestPoint(a.Origin);
        Link(a, b, cost: TravelCost(from, to, a.HasFlag(WaypointFlags.Jump),
            fromInWater: false, toInWater: false,
            fromCrouch: a.HasFlag(WaypointFlags.Crouch), toCrouch: b.HasFlag(WaypointFlags.Crouch)));
    }

    /// <summary>Player hull used for tracewalk reachability tests (QC PL_MIN_CONST/PL_MAX_CONST).</summary>
    private static readonly Vector3 _playerMins = new(-16f, -16f, -24f);
    private static readonly Vector3 _playerMaxs = new(16f, 16f, 45f);

    /// <summary>
    /// Walk-distance budget for STRATEGY-TIME tracewalks (variance program 2026-07-11 — the residual bot-tick
    /// tail: Release p99 6.2ms / max 17.1ms, all in bot.strategy/bot.seed long walks). Covers the full seed-ring
    /// growth (2250qu) with slack, so in-budget behavior is unchanged; beyond it a DIRECT walk is never the
    /// right strategy answer (the waypoint router owns long routes). Budgeted mode also bounds the per-step
    /// find-the-floor sweep (see <see cref="BotTracewalk.CanWalk"/>). Graph building (AutoLink) stays UNBOUNDED
    /// — cached waypoint links must keep exact QC semantics.
    /// </summary>
    internal const float StrategyWalkMaxDist = 2600f;

    /// <summary>
    /// Floor-snap a candidate waypoint origin (QC waypoint_fixorigin / waypoint_fixorigin_down_dir,
    /// waypoints.qc:1957-1971): tracebox the player hull straight down up to 3000qu from just above the position
    /// and drop the origin to where it lands, so an item/teleporter waypoint sits on the ground the bot stands on
    /// rather than floating at the item's pickup height. Re-tries lifted up if the initial probe starts solid.
    /// Falls back to the QC-era fixed +24 lift when no collision world is available (offline graph build).
    /// </summary>
    private static Vector3 FixOrigin(Vector3 position)
    {
        if (Api.Services is null)
            return position + new Vector3(0f, 0f, 24f);

        Vector3 endpos = position + new Vector3(0f, 0f, -3000f);
        Entity? ignore = null;
        TraceResult tr = Api.Trace.Trace(position + new Vector3(0f, 0f, 1f), _playerMins, _playerMaxs, endpos, MoveFilter.NoMonsters, ignore);
        if (tr.StartSolid)
            tr = Api.Trace.Trace(position + new Vector3(0f, 0f, 1f - _playerMins.Z / 2f), _playerMins, _playerMaxs, endpos, MoveFilter.NoMonsters, ignore);
        if (tr.StartSolid)
            tr = Api.Trace.Trace(position + new Vector3(0f, 0f, 1f - _playerMins.Z), _playerMins, _playerMaxs, endpos, MoveFilter.NoMonsters, ignore);
        if (tr.Fraction < 1f)
            position = tr.EndPos;
        return position;
    }

    /// <summary>
    /// Auto-generate a navigable waypoint graph from the live map entities (QC the
    /// <c>waypoint_spawnforitem</c> / <c>waypoint_spawnforteleporter</c> / <c>waypoint_spawn_fromeditor</c>
    /// auto-waypointing): drop a waypoint on every item and spawn point, a box waypoint over every
    /// teleporter/jumppad trigger with a one-way Teleport link to its destination, then walk-link the rest
    /// (<see cref="AutoLink"/>). Lets bots navigate a map that ships no hand-authored <c>.waypoints</c> file.
    /// Returns the number of waypoints generated.
    /// </summary>
    public int GenerateFromEntities(IReadOnlyList<XonoticGodot.Common.Framework.Entity> entities, bool autoLink = true)
    {
        // index destinations by targetname so a trigger's .target resolves to its exit point.
        var byTargetName = new Dictionary<string, XonoticGodot.Common.Framework.Entity>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entities)
            if (!string.IsNullOrEmpty(e.TargetName) && !byTargetName.ContainsKey(e.TargetName))
                byTargetName[e.TargetName] = e;

        // QC bot_waypoints_for_items = autocvar_g_waypoints_for_items (world.qc:937): "0" never, "1" unless the
        // map disables it, "2" always; waypoint_spawnforitem (waypoints.qc:2004) early-returns when 0. This is
        // the FILELESS auto-generate fallback (a port-only playability helper that does not exist in Base), so
        // item waypoints are kept ON by default to give bots goals to navigate — but a host that EXPLICITLY
        // enables the cvar (1/2) still gets them, and the gate is wired so the value is honoured rather than
        // ignored. We only suppress when the host turned items off AND there are spawn points to keep the graph
        // connected (so the fallback never collapses to an empty graph).
        int forItemsCvar = (int)Cvars.FloatOr("g_waypoints_for_items", 2f);
        bool haveSpawns = false;
        foreach (var e in entities)
            if (e is { IsFreed: false } && (e.ClassName ?? "").StartsWith("info_player", StringComparison.Ordinal))
            { haveSpawns = true; break; }
        bool spawnItemWaypoints = forItemsCvar != 0 || !haveSpawns;

        int before = _nodes.Count;
        var destWaypoints = new Dictionary<XonoticGodot.Common.Framework.Entity, Waypoint>(ReferenceEqualityComparer.Instance);

        Waypoint DestFor(XonoticGodot.Common.Framework.Entity dst)
        {
            if (!destWaypoints.TryGetValue(dst, out var wp))
            {
                wp = Add(FixOrigin(dst.Origin), WaypointFlags.Generated);
                destWaypoints[dst] = wp;
            }
            return wp;
        }

        foreach (var e in entities)
        {
            if (e is null || e.IsFreed) continue;
            string cn = e.ClassName ?? "";

            // items + spawn points → a stand-on-it point waypoint, floor-snapped (QC waypoint_fixorigin).
            bool isItem = (e.Flags & XonoticGodot.Common.Framework.EntFlags.Item) != 0;
            bool isSpawn = cn.StartsWith("info_player", StringComparison.Ordinal);
            if (isItem || isSpawn)
            {
                if (isItem && !spawnItemWaypoints) continue; // QC bot_waypoints_for_items gate
                Add(FixOrigin(e.Origin),
                    WaypointFlags.Generated | (isItem ? WaypointFlags.Item : WaypointFlags.None));
                continue;
            }

            // teleporter / jumppad triggers → a box waypoint with a one-way Teleport link to the destination.
            bool isTeleport = cn == "trigger_teleport";
            bool isJumppad = cn == "trigger_push" || cn == "trigger_push_velocity";
            if (isTeleport || isJumppad)
            {
                XonoticGodot.Common.Framework.Entity? dst = null;
                if (!string.IsNullOrEmpty(e.Target)) byTargetName.TryGetValue(e.Target, out dst);
                var box = Add(e.Origin, e.Mins, e.Maxs, WaypointFlags.Generated | WaypointFlags.Teleport);
                if (dst is not null)
                {
                    var destWp = DestFor(dst);
                    // QC waypoint_spawnforteleporter cost = the trigger's travel TIME, not the plain box→dest walk
                    // cost: a teleporter is ~instant (a tiny fixed cost) while a jumppad's cost is the ballistic
                    // flight time of its launch arc (waypoint_spawnforteleporter_wz / trigger_push_get_push_time).
                    float cost = isJumppad ? JumppadFlightCost(e, box, destWp) : 0.05f;
                    Link(box, destWp, cost: cost); // one-way: enter the trigger → arrive at the destination
                }
            }
        }

        if (autoLink)
            AutoLink();
        return _nodes.Count - before;
    }

    /// <summary>
    /// Ballistic flight time of a jumppad launch (QC trigger_push_get_push_time / the spawnforteleporter_wz cost):
    /// the time the toucher spends airborne arcing from the pad to its destination. Computed from the pad's actual
    /// launch velocity (the same <see cref="XonoticGodot.Common.Gameplay.Jumppads.CalculateVelocity"/> solver that
    /// drives the pad in-game), so the auto-generated teleport link costs what the jump actually takes. Falls back
    /// to the plain box→destination travel cost if the arc can't be solved (no upward launch).
    /// </summary>
    private float JumppadFlightCost(XonoticGodot.Common.Framework.Entity pad, Waypoint box, Waypoint destWp)
    {
        Vector3 org = pad.Origin;
        Vector3 vel = XonoticGodot.Common.Gameplay.Jumppads.CalculateVelocity(org, pad.Enemy ?? pad, pad.Height, pad);
        // flight time of a ballistic arc back to launch height: t = 2*vz/g (QC's spawnforteleporter_wz push time).
        if (vel.Z > 0f && Gravity > 0f)
            return 2f * vel.Z / Gravity;
        return TravelCost(box.ClosestPoint(destWp.Origin), destWp.ClosestPoint(box.Origin));
    }

    // Reusable scratch for the multi-seed gather (NearestSeeds) — same single-threaded-sim safety as _nearScratch.
    private readonly List<(Waypoint Wp, float Cost)> _seedScratch = new();

    // Per-call "already attempted" mask for NearestSeeds, indexed by node list position. A node's tracewalk
    // reachability from a FIXED pos does not change as the search radius grows, so one attempt per call is exact
    // (not a heuristic cap). Without it, the rescan-per-growth loop re-TRACEWALKED every in-range node on every
    // radius step — with nothing reachable that is nodes × steps walk-sims (99 nodes × 67 steps at the on-ground
    // 50000 cap): the 50-350ms/frame bot.strategy melt behind the stormkeep "very slow with bots" report.
    private bool[] _seedTried = Array.Empty<bool>();

    /// <summary>
    /// Multiple route entry waypoints (QC navigation_markroutes_nearestwaypoints, navigation.qc:1043-1101): instead
    /// of routing from the single <see cref="Nearest"/> node, Base seeds the Dijkstra flood with EVERY waypoint
    /// reachable within an expanding radius — so the planner can pick a slightly-farther node that opens a better
    /// route (the single nearest is sometimes behind a wall / on the wrong side of a ledge). The radius starts at
    /// the increment and grows by it until at least one reachable seed is found, capped at the max: on-ground
    /// increment 750 / max 50000, in-air 500 / max 1500 (navigation.qc:1054-1058). Each returned seed carries its
    /// entry cost = the travel cost from <paramref name="pos"/> to that waypoint (the bot's initial gScore into the
    /// graph), so the A* multi-seed search starts each seed pre-charged with how far the bot already is.
    /// <paramref name="walkFromWp"/> follows navigation_findnearestwaypoint's direction sense (true = the bot walks
    /// pos→waypoint, the start seed). Returns an empty list if nothing is reachable in range (the caller falls back
    /// to <see cref="Nearest"/>).
    /// </summary>
    public IReadOnlyList<(Waypoint Wp, float Cost)> NearestSeeds(Vector3 pos, bool onGround, bool walkFromWp = true)
    {
        var seeds = _seedScratch;
        seeds.Clear();
        if (_nodes.Count == 0)
            return seeds;

        bool canTrace = Api.Services is not null;
        // QC navigation_markroutes_nearestwaypoints radius growth (navigation.qc:1054-1058).
        float inc = onGround ? 750f : 500f;
        float max = onGround ? 50000f : 1500f;
        // (perf 2026-07-03) Clamp the TRACED ring growth: a seed candidate N units out costs an N-long walk-sim
        // (a trace every ~32qu — a 5000qu attempt is 150+ traces, several ms EACH on a debug build; QC pays the
        // same walks at native-C trace speed). A far seed's entry cost is so high the flood almost never picks
        // it anyway; past the clamp the caller's fallbacks take over (ComputeRouteCosts → budgeted Nearest →
        // nearest-by-distance), the same degradation QC accepts when nothing is reachable. Test/graphless runs
        // (no collision world) keep the full QC growth.
        if (canTrace)
            max = MathF.Min(max, 2250f);

        if (_seedTried.Length < _nodes.Count) _seedTried = new bool[_nodes.Count];
        else Array.Clear(_seedTried, 0, _nodes.Count);

        // Tracewalk BUDGET per call: each attempt is a full walk-sim (a trace every ~32qu, potentially hundreds
        // over a long approach), so even one-attempt-per-node explodes when NOTHING near the bot is reachable
        // (a bot on a ledge/void edge burns the whole node list ≈ 100ms+ — the residual bot.strategy hitch
        // spikes). After the budget the caller's straight-line fallback takes over — the same degradation QC
        // accepts for an unreachable spot. (12 = perf 2026-07-03, halved from 24: with nearest-first ordering
        // the successful case hits in the first few walks; the budget only burns fully in the nothing-reachable
        // case, where it pays pure waste.)
        const int MaxWalkAttempts = 12;
        // Reachable-seed CAP (perf 2026-07-03; a bounded deviation from QC's take-everything-in-the-final-ring):
        // the multi-seed A* only needs a handful of entry candidates — every seed is pre-charged with its
        // bot→seed entry cost, so a farther seed almost never beats a nearer one. In a waypoint-dense area the
        // final ring holds 10-20 nodes and QC semantics tracewalked EVERY one (the dominant share of the ~100ms
        // strategy-pass hitches on a debug build); walking nearest-first and stopping at the cap keeps the seed
        // set = the K nearest reachable — deterministic and position-derived, so behavior stays stable.
        const int MaxSeeds = 8;
        int attempts = 0;

        for (float radius = inc; ; radius += inc)
        {
            float r2 = radius * radius;
            // Gather this ring's untried candidates, NEAREST-FIRST — so the walk budget is spent on the likely
            // entries and the early-out cap collects the nearest reachable seeds (the node-list-order scan this
            // replaces walked candidates in arbitrary order).
            var cand = _seedCand;
            cand.Clear();
            for (int i = 0; i < _nodes.Count; i++)
            {
                // one attempt per node per call — covers BOTH the QC seed-set dedupe (a node taken at a tighter
                // radius) AND the failed-tracewalk case the per-growth rescan would otherwise re-walk every step.
                if (_seedTried[i])
                    continue;
                float d2 = (_nodes[i].ClosestPoint(pos) - pos).LengthSquared();
                if (d2 <= r2)
                    cand.Add((d2, i));
            }
            cand.Sort(_byD2Idx);
            for (int c = 0; c < cand.Count; c++)
            {
                int i = cand[c].Idx;
                Waypoint wp = _nodes[i];
                if (canTrace && (attempts >= MaxWalkAttempts || seeds.Count >= MaxSeeds))
                    return seeds;   // budget/cap hit — return the nearest reachable found so far (often empty)
                _seedTried[i] = true;
                attempts++;
                Vector3 cp = wp.ClosestPoint(pos);
                bool reach = !canTrace || (walkFromWp
                    ? BotTracewalk.CanWalk(pos, cp, _playerMins, _playerMaxs, wp.IsBox ? (wp.AbsMax.Z - cp.Z) : 0f,
                        maxWalkDistance: StrategyWalkMaxDist)
                    : BotTracewalk.CanWalk(cp, pos, _playerMins, _playerMaxs,
                        maxWalkDistance: StrategyWalkMaxDist));
                if (reach)
                    seeds.Add((wp, TravelCost(pos, cp)));
            }
            if (seeds.Count > 0 || radius >= max)
                break;
        }
        return seeds;
    }

    // Ring-candidate scratch for NearestSeeds' nearest-first ordering (same single-threaded-sim safety as the
    // other scratches; index into _nodes so the tried-mask stays index-addressed).
    private readonly List<(float D2, int Idx)> _seedCand = new();
    private static readonly Comparison<(float D2, int Idx)> _byD2Idx = static (a, b) => a.D2.CompareTo(b.D2);

    // Reusable scratch for Nearest's candidate sort — avoids a per-call allocation. The sim drives bots
    // sequentially on one thread, so a shared buffer on the shared network is safe (Nearest isn't re-entrant).
    private readonly List<(float D2, Waypoint Wp)> _nearScratch = new();
    private static readonly Comparison<(float D2, Waypoint Wp)> _byD2 = static (a, b) => a.D2.CompareTo(b.D2);

    /// <summary>
    /// Nearest waypoint to a world position (QC navigation_findnearestwaypoint). Prefers a box waypoint that
    /// contains the point; otherwise scans by distance within <paramref name="maxDist"/> and — when a
    /// collision world is available and <paramref name="requireReachable"/> is set — confirms the candidate
    /// is actually reachable from <paramref name="pos"/> with a <see cref="BotTracewalk"/> (so the bot is
    /// routed via a node it can genuinely walk to, not one across a wall). Falls back to the nearest by pure
    /// distance if none pass the reachability test (better a far node than none).
    ///
    /// Performance: candidates are gathered then sorted nearest-first and the FIRST reachable one is returned —
    /// since closer-reachable always wins, this stops at the first successful <see cref="BotTracewalk.CanWalk"/>
    /// (typically one) instead of tracewalking every node in range as the old distance-keyed scan did.
    ///
    /// <paramref name="walkFromWp"/> picks the reachability direction, matching Base's
    /// navigation_findnearestwaypoint(ent, walkfromwp): the START waypoint (seeded from the bot) tests "can
    /// the bot walk FROM <paramref name="pos"/> TO the waypoint" (walkFromWp = true); the GOAL waypoint tests
    /// "can a player walk FROM the waypoint TO <paramref name="pos"/>" (walkFromWp = false), because the route
    /// approaches the goal from the waypoint, not the other way round. A one-way ledge/jump-down reaches in only
    /// one direction, so the correct sense keeps the planner from picking a node it can't actually traverse.
    /// </summary>
    public Waypoint? Nearest(Vector3 pos, float maxDist = 1050f, bool requireReachable = true, bool walkFromWp = true)
    {
        float maxD2 = maxDist * maxDist;
        bool canTrace = requireReachable && Api.Services is not null;

        var cand = _nearScratch;
        cand.Clear();
        for (int i = 0; i < _nodes.Count; i++)
        {
            Waypoint wp = _nodes[i];
            if (wp.IsBox)
            {
                var lo = wp.AbsMin;
                var hi = wp.AbsMax;
                if (pos.X >= lo.X && pos.X <= hi.X && pos.Y >= lo.Y && pos.Y <= hi.Y
                    && pos.Z >= lo.Z && pos.Z <= hi.Z)
                    return wp; // inside the box: definitive match
            }
            float d2 = (wp.ClosestPoint(pos) - pos).LengthSquared();
            if (d2 <= maxD2)
                cand.Add((d2, wp));
        }
        if (cand.Count == 0)
            return null;

        cand.Sort(_byD2);
        if (!canTrace)
            return cand[0].Wp; // no collision world: nearest by distance

        // Track the nearest reachable TELEPORT box separately for the jumppad fallback below.
        Waypoint? reachableTeleport = null;

        // Nearest-first: the first node we can actually walk to is the nearest reachable one. Direction follows
        // Base's walkfromwp: true → bot walks pos→waypoint (start node); false → player walks waypoint→pos
        // (goal node, the route arrives at the goal FROM the waypoint).
        // Tracewalk BUDGET (perf 2026-07-03): each attempt is a full walk-sim, and an adversarial position (a
        // MID-AIR player being goal-rated, a ledge/void spot) fails candidate after candidate — unbudgeted, this
        // walked the whole in-range list (the dominant share of the ~100ms `bot.rate` hitches on a debug build:
        // every enemy-player rating re-binds through here each strategy pass). The common case succeeds on the
        // first walk; after the budget we degrade to nearest-by-distance — the same fallback as the
        // nothing-reachable case below, which QC also accepts.
        const int MaxNearestWalks = 8;
        for (int i = 0; i < cand.Count && i < MaxNearestWalks; i++)
        {
            Waypoint wp = cand[i].Wp;
            Vector3 cp = wp.ClosestPoint(pos);
            bool reach = walkFromWp
                ? BotTracewalk.CanWalk(pos, cp, _playerMins, _playerMaxs, wp.IsBox ? (wp.AbsMax.Z - cp.Z) : 0f,
                    maxWalkDistance: StrategyWalkMaxDist)
                : BotTracewalk.CanWalk(cp, pos, _playerMins, _playerMaxs,
                    maxWalkDistance: StrategyWalkMaxDist);
            if (reach)
            {
                if (wp.HasFlag(WaypointFlags.Teleport))
                    reachableTeleport ??= wp; // nearest-first, so the first one is the nearest reachable box
                else
                    return wp; // nearest reachable PLAIN waypoint wins outright
            }
        }
        // QC navigation_findnearestwaypoint tail (navigation.qc:1000, IL_EACH(g_jumppads)): no plain waypoint was
        // reachable, so route via a reachable jumppad/teleporter box (its wp00 link carries the bot to the far side).
        if (reachableTeleport is not null)
            return reachableTeleport;
        return cand[0].Wp; // none reachable: fall back to the nearest by pure distance
    }

    // ---- A* pathfinding (deliverable: simple A* FindPath) ----

    /// <summary>
    /// A* shortest path between two graph nodes over the link costs (QC builds this implicitly via
    /// navigation_markroutes + back-pointer walk). Returns the node sequence from <paramref name="from"/> to
    /// <paramref name="to"/> inclusive, or null if unreachable. The heuristic is straight-line/MaxSpeed, an
    /// admissible lower bound on remaining travel cost so the result is optimal. Each node's static danger
    /// bias (<see cref="Waypoint.Danger"/>, QC <c>.dmg</c>) is added to the path cost on entry, matching
    /// navigation_markroutes so routes detour around marked hazards.
    /// </summary>
    // Reusable A* working set (resized when the graph grows). Reused across searches to avoid allocating four
    // arrays + a heap on every FindPath; the sim drives bots sequentially so a shared scratch is safe.
    private float[] _gScore = Array.Empty<float>();
    private float[] _fScore = Array.Empty<float>();
    private Waypoint?[] _cameFrom = Array.Empty<Waypoint?>();
    private bool[] _closed = Array.Empty<bool>();
    private MinHeap? _open;

    /// <summary>
    /// Multi-seed A* (QC navigation_markroutes seeds the flood from several near waypoints, not one): runs the
    /// same least-cost search as <see cref="FindPath(Waypoint, Waypoint)"/> but starts with EVERY seed in
    /// <paramref name="seeds"/> pre-charged with its entry cost (the travel cost from the bot to that seed,
    /// produced by <see cref="NearestSeeds"/>). The reconstructed path therefore begins at whichever seed lies on
    /// the cheapest overall route to <paramref name="to"/> — matching Base picking the best graph entry point
    /// rather than forcing the single geometrically-nearest one. Returns null if no seed can reach the goal.
    /// </summary>
    public List<Waypoint>? FindPath(IReadOnlyList<(Waypoint Wp, float Cost)> seeds, Waypoint to)
    {
        if (seeds.Count == 0)
            return null;

        int n = _nodes.Count;
        if (_gScore.Length < n)
        {
            _gScore = new float[n];
            _fScore = new float[n];
            _cameFrom = new Waypoint?[n];
            _closed = new bool[n];
        }
        float[] gScore = _gScore, fScore = _fScore;
        Waypoint?[] cameFrom = _cameFrom;
        bool[] closed = _closed;
        for (int i = 0; i < n; i++) { gScore[i] = float.PositiveInfinity; fScore[i] = float.PositiveInfinity; cameFrom[i] = null; closed[i] = false; }

        MinHeap open = _open ??= new MinHeap(n);
        open.Clear();
        // seed each entry node with its bot→seed entry cost (QC the nearestwaypoints wpcost seeding). cameFrom stays
        // null so Reconstruct stops at whichever seed the cheapest route used.
        for (int s = 0; s < seeds.Count; s++)
        {
            var (wp, cost) = seeds[s];
            if (wp.Index < 0) continue;
            float g = MathF.Max(0f, cost) + wp.Danger; // include the seed's own static danger bias (QC cost2)
            if (g < gScore[wp.Index])
            {
                gScore[wp.Index] = g;
                fScore[wp.Index] = g + Heuristic(wp, to);
                open.Push(wp.Index, fScore[wp.Index]);
            }
        }

        while (open.TryPop(out int currentIdx))
        {
            if (closed[currentIdx]) continue;
            var current = _nodes[currentIdx];
            if (ReferenceEquals(current, to))
                return Reconstruct(cameFrom, current);

            closed[currentIdx] = true;

            foreach (var link in current.Links)
            {
                var nb = link.To;
                if (nb.Index < 0 || closed[nb.Index]) continue;
                if (link.Cost >= 999f) continue; // known-unwalkable link

                float tentative = gScore[currentIdx] + link.Cost + nb.Danger;
                if (tentative < gScore[nb.Index])
                {
                    cameFrom[nb.Index] = current;
                    gScore[nb.Index] = tentative;
                    fScore[nb.Index] = tentative + Heuristic(nb, to);
                    open.Push(nb.Index, fScore[nb.Index]);
                }
            }
        }
        return null;
    }

    public List<Waypoint>? FindPath(Waypoint from, Waypoint to)
    {
        if (ReferenceEquals(from, to))
            return new List<Waypoint> { from };

        int n = _nodes.Count;
        if (_gScore.Length < n)
        {
            _gScore = new float[n];
            _fScore = new float[n];
            _cameFrom = new Waypoint?[n];
            _closed = new bool[n];
        }
        float[] gScore = _gScore, fScore = _fScore;
        Waypoint?[] cameFrom = _cameFrom;
        bool[] closed = _closed;
        for (int i = 0; i < n; i++) { gScore[i] = float.PositiveInfinity; fScore[i] = float.PositiveInfinity; cameFrom[i] = null; closed[i] = false; }

        // binary-heap open set keyed by fScore; entries may be stale (lazy decrease-key)
        MinHeap open = _open ??= new MinHeap(n);
        open.Clear();
        gScore[from.Index] = 0f;
        fScore[from.Index] = Heuristic(from, to);
        open.Push(from.Index, fScore[from.Index]);

        while (open.TryPop(out int currentIdx))
        {
            if (closed[currentIdx]) continue; // stale heap entry
            var current = _nodes[currentIdx];
            if (ReferenceEquals(current, to))
                return Reconstruct(cameFrom, current);

            closed[currentIdx] = true;

            foreach (var link in current.Links)
            {
                var nb = link.To;
                if (nb.Index < 0 || closed[nb.Index]) continue;
                if (link.Cost >= 999f) continue; // known-unwalkable link

                // QC navigation_markroutes: cost2 = cost + wp.dmg — add the destination's static danger bias so
                // routes pre-emptively detour around lava/rocket zones (set by botframe_updatedangerousobjects),
                // not just brake at the last moment. Admissible-heuristic optimality is preserved: Danger >= 0.
                float tentative = gScore[currentIdx] + link.Cost + nb.Danger;
                if (tentative < gScore[nb.Index])
                {
                    cameFrom[nb.Index] = current;
                    gScore[nb.Index] = tentative;
                    fScore[nb.Index] = tentative + Heuristic(nb, to);
                    open.Push(nb.Index, fScore[nb.Index]);
                }
            }
        }
        return null;
    }

    private float Heuristic(Waypoint a, Waypoint b) => (b.Origin - a.Origin).Length() / MaxSpeed;

    // ---- single-source route costs for goal rating (QC navigation_markroutes, navigation.qc) ----
    // QC computes a Dijkstra cost-to-reach for EVERY waypoint once per strategy frame (the .wpcost field), then
    // each navigation_routerating reads it. We mirror that: ComputeRouteCosts fills _routeCost[node] with the
    // shortest path cost from the bot's nearest waypoint; RouteCostTo(goal) = that node's cost + the straight tail
    // from the node to the goal. Seeded once per GoalRater.Start (token-gated, sim-thread), then read per candidate.
    private float[] _routeCost = Array.Empty<float>();
    private bool _routeCostValid;

    /// <summary>
    /// QC navigation_markroutes: single-source shortest-path (Dijkstra) cost from <paramref name="from"/>'s
    /// nearest waypoint to every reachable node, cached in <see cref="_routeCost"/>. Run once at the start of a
    /// strategy frame; <see cref="RouteCostTo"/> then reads it per rated goal. Reuses the A* working arrays +
    /// heap (sim drives bots sequentially, so the shared scratch is safe). Marks the cache invalid (so callers
    /// fall back to straight-line) when <paramref name="from"/> has no nearby waypoint.
    /// </summary>
    /// <returns>The entry-seed set the flood was seeded from (perf 2026-07-03): the SAME set
    /// <see cref="BotNavigation.SetGoal"/> needs for its multi-seed A* moments later in the strategy pass —
    /// returning it lets the brain reuse it instead of re-running the tracewalk-heavy <see cref="NearestSeeds"/>
    /// from the identical origin (the seed search is the dominant cost of a strategy pass on a debug build).
    /// Aliases a shared scratch: copy it before the next NearestSeeds/ComputeRouteCosts call.</returns>
    public IReadOnlyList<(Waypoint Wp, float Cost)> ComputeRouteCosts(Vector3 from, bool onGround = true)
    {
        _routeCostValid = false;
        int n = _nodes.Count;
        if (n == 0) return Array.Empty<(Waypoint, float)>();
        if (_routeCost.Length < n) _routeCost = new float[n];
        if (_closed.Length < n)
        {
            _gScore = new float[n]; _fScore = new float[n];
            _cameFrom = new Waypoint?[n]; _closed = new bool[n];
        }
        // QC navigation_markroutes seeds the Dijkstra flood from EVERY reachable nearby waypoint (the expanding
        // navigation_markroutes_nearestwaypoints search), each pre-charged with its bot->seed entry cost — not just
        // the single geometrically-nearest node. Gather the seeds; fall back to the single nearest when nothing is
        // reachable in range so the cache still seeds (and a graphless bot just gets the straight-line fallback).
        IReadOnlyList<(Waypoint Wp, float Cost)> seeds = NearestSeeds(from, onGround, walkFromWp: true);
        if (seeds.Count == 0)
        {
            Waypoint? src = Nearest(from, walkFromWp: true);
            if (src is null || src.Index < 0) return Array.Empty<(Waypoint, float)>();
            seeds = new[] { (src, (src.ClosestPoint(from) - from).Length() / MaxSpeed) };
        }

        float[] cost = _routeCost;
        bool[] closed = _closed;
        for (int i = 0; i < n; i++) { cost[i] = float.PositiveInfinity; closed[i] = false; }

        MinHeap open = _open ??= new MinHeap(n);
        open.Clear();
        // seed each entry waypoint with its bot->seed entry cost (the straight tail), matching QC's multi-seed
        // wpcost seeding; the cheapest seed for any given node wins via the relaxation below.
        for (int s = 0; s < seeds.Count; s++)
        {
            var (seedWp, entry) = seeds[s];
            if (seedWp.Index < 0) continue;
            float g = MathF.Max(0f, entry) + seedWp.Danger; // include the seed's own static danger bias (QC cost2)
            if (g < cost[seedWp.Index])
            {
                cost[seedWp.Index] = g;
                open.Push(seedWp.Index, g);
            }
        }
        while (open.TryPop(out int curIdx))
        {
            if (closed[curIdx]) continue;
            closed[curIdx] = true;
            var cur = _nodes[curIdx];
            foreach (var link in cur.Links)
            {
                var nb = link.To;
                if (nb.Index < 0 || closed[nb.Index]) continue;
                if (link.Cost >= 999f) continue; // known-unwalkable
                // QC cost2 = cost + wp.dmg: include the destination's static danger bias so routing avoids hazards.
                float tentative = cost[curIdx] + link.Cost + nb.Danger;
                if (tentative < cost[nb.Index])
                {
                    cost[nb.Index] = tentative;
                    open.Push(nb.Index, tentative);
                }
            }
        }
        _routeCostValid = true;
        return seeds;
    }

    /// <summary>
    /// Path cost (QC navigation_routerating's distance term) from the bot to a world goal: the cached route cost
    /// to the goal's nearest waypoint plus the straight tail from that waypoint to the goal. Returns
    /// <see cref="float.PositiveInfinity"/> when <see cref="ComputeRouteCosts"/> hasn't been seeded or the goal
    /// has no reachable waypoint — the caller then falls back to straight-line distance (graphless roaming).
    /// </summary>
    public float RouteCostTo(Vector3 goal)
    {
        if (!_routeCostValid) return float.PositiveInfinity;
        Waypoint? gw = Nearest(goal, walkFromWp: false);
        if (gw is null || gw.Index < 0) return float.PositiveInfinity;
        float c = _routeCost[gw.Index];
        if (float.IsPositiveInfinity(c)) return float.PositiveInfinity;
        return c + (gw.ClosestPoint(goal) - goal).Length() / MaxSpeed;
    }

    /// <summary>Route cost straight to a graph NODE (perf 2026-07-03): the goal already IS a waypoint, so read
    /// its own flood slot — the generic overloads would round-trip through a tracewalk-verified
    /// <see cref="Nearest"/> to rediscover the node they were handed (the roam-waypoint rating did exactly that
    /// for every shell candidate — a large share of an idle bot's rating pass on a debug build).</summary>
    public float RouteCostToWaypoint(Waypoint wp)
    {
        if (!_routeCostValid || wp.Index < 0 || wp.Index >= _routeCost.Length)
            return float.PositiveInfinity;
        return _routeCost[wp.Index];
    }

    /// <summary>Entity-goal overload of <see cref="RouteCostTo(Vector3)"/> using the QC nearest-waypoint CACHE —
    /// see <see cref="NearestForGoal"/>. Rating N items per strategy pass through the uncached path re-ran a
    /// tracewalk-heavy <see cref="Nearest"/> per item per pass (the residual 100ms+ bot.strategy hitches).</summary>
    public float RouteCostTo(Entity? target, Vector3 goal)
    {
        if (!_routeCostValid) return float.PositiveInfinity;
        Waypoint? gw = target is not null ? NearestForGoal(target, goal) : Nearest(goal, walkFromWp: false);
        if (gw is null || gw.Index < 0) return float.PositiveInfinity;
        float c = _routeCost[gw.Index];
        if (float.IsPositiveInfinity(c)) return float.PositiveInfinity;
        return c + (gw.ClosestPoint(goal) - goal).Length() / MaxSpeed;
    }

    // QC .nearestwaypoint / .nearestwaypointtimeout (navigation.qc navigation_findnearestwaypoint): the goal→
    // waypoint binding cached per goal ENTITY. Position-independent (unlike _routeCost, which re-floods per bot),
    // so a static item binds effectively once per match; a movable goal (player/projectile) re-binds on a short
    // timeout, exactly the QC split.
    private readonly Dictionary<Entity, (Waypoint? Wp, float Until)> _goalWpCache = new();

    /// <summary>The goal entity's nearest (walk-reachable) waypoint, via the QC nearest-waypoint cache. Public
    /// (perf/parity 2026-07-03) so <see cref="BotNavigation.SetGoal"/>'s goal-side lookup rides the same cache —
    /// QC navigation_routetogoal reads .nearestwaypoint there too; the port was re-running the tracewalk-heavy
    /// <see cref="Nearest"/> per route build.</summary>
    public Waypoint? NearestForGoal(Entity target, Vector3 pos)
    {
        float now = XonoticGodot.Common.Gameplay.MapMover.Now();
        if (_goalWpCache.TryGetValue(target, out (Waypoint? Wp, float Until) c))
        {
            if (target.IsFreed) { _goalWpCache.Remove(target); return null; }
            if (now < c.Until) return c.Wp;
        }
        Waypoint? wp = Nearest(pos, walkFromWp: false);
        // Movable goals re-bind on a short timeout (QC uses ~0.5s for players); static items bind for the match
        // (QC sets a far-future timeout — the binding only changes on a waypoint reconnect, which clears below).
        bool movable = (target.Flags & EntFlags.Client) != 0 || target.Velocity != Vector3.Zero;
        _goalWpCache[target] = (wp, now + (movable ? 0.5f : 1e9f));
        return wp;
    }

    // ---- per-waypoint static danger bias (QC botframe_updatedangerousobjects, navigation.qc:1874) ----

    /// <summary>
    /// Recompute each waypoint's static danger bias (QC <c>.dmg</c>, set by
    /// <c>botframe_updatedangerousobjects</c>): for every dangerous object (an entity flagged
    /// <see cref="Entity.BotDodge"/> — the g_bot_dodge intrusive list: in-flight rockets, turret beams, etc.),
    /// add to a waypoint's danger when the object is close enough that a player at the waypoint couldn't outrun
    /// its blast and has line of sight to it. The danger value is later folded into A* route costs
    /// (<see cref="FindPath"/>: <c>gScore + link.Cost + nb.Danger</c>) so bots pre-emptively route AROUND
    /// hazards instead of only braking at the last moment via the per-frame <see cref="BotDanger"/> probe.
    ///
    /// Faithful to navigation.qc:1874-1903: <c>d = waypoint_getlinearcost(rating) - waypoint_gettravelcost(o, v)</c>
    /// where <c>o</c> is the hazard center, <c>v</c> the hazard origin clamped to the waypoint's box, and the
    /// danger only counts when <c>d &gt; 0</c> AND the LOS traceline from <c>o</c> to <c>v</c> is unobstructed.
    /// Matches Base's quirk of only ever updating the FIRST <paramref name="maxUpdate"/> waypoints per call (the
    /// QC loop has no rotating cursor — it breaks at <c>c &gt;= maxupdate</c> from the head of g_waypoints).
    /// </summary>
    public void UpdateDangerousObjects(IReadOnlyList<XonoticGodot.Common.Framework.Entity> entities, int maxUpdate)
    {
        if (maxUpdate <= 0 || _nodes.Count == 0)
            return;

        bool canTrace = Api.Services is not null;
        int c = 0;
        foreach (var wp in _nodes)
        {
            Vector3 m1 = wp.AbsMin;
            Vector3 m2 = wp.AbsMax;
            float danger = 0f;

            for (int i = 0; i < entities.Count; i++)
            {
                var it = entities[i];
                if (it is null || it.IsFreed || !it.BotDodge) continue;

                // QC: v = it.origin clamped into the waypoint's box; o = the hazard's bbox center.
                Vector3 v = it.Origin;
                v = new Vector3(
                    QMath.Clamp(v.X, m1.X, m2.X),
                    QMath.Clamp(v.Y, m1.Y, m2.Y),
                    QMath.Clamp(v.Z, m1.Z, m2.Z));
                Vector3 o = (it.AbsMin + it.AbsMax) * 0.5f;

                // QC: d = linearcost(bot_dodgerating) - travelcost(o, v). The dodge object carries no waypoint
                // flags (no JUMP/CROUCH), so the entity-overload waypoint_gettravelcost reduces to the plain
                // (non-jump, non-crouch, non-submerged) travel cost here.
                float d = LinearCost(it.BotDodgeRating) - TravelCost(o, v);
                if (d > 0f)
                {
                    // QC: traceline(o, v, MOVE_NOMONSTERS, NULL); only count if unobstructed (fraction == 1).
                    bool clear = !canTrace
                        || Api.Trace.Trace(o, Vector3.Zero, Vector3.Zero, v, MoveFilter.NoMonsters, null).Fraction >= 1f;
                    if (clear)
                        danger += d;
                }
            }

            wp.Danger = danger;
            if (++c >= maxUpdate)
                break;
        }
    }

    private static List<Waypoint> Reconstruct(Waypoint?[] cameFrom, Waypoint current)
    {
        var path = new List<Waypoint> { current };
        var c = cameFrom[current.Index];
        while (c is not null)
        {
            path.Add(c);
            c = cameFrom[c.Index];
        }
        path.Reverse();
        return path;
    }

    // ---- .waypoints file format (QC waypoint_loadall / waypoint_load_links) ----

    /// <summary>
    /// The symmetry header parsed from a <c>.waypoints</c> file (QC the <c>//WAYPOINT_SYMMETRY</c> lines):
    /// the format version, plus the map's symmetry origin/axis/order so a half-authored graph can be mirrored.
    /// <see cref="Order"/> 0 means "no symmetry / autodetect"; ≥2 is a rotational symmetry order.
    /// </summary>
    public sealed class SymmetryHeader
    {
        public int Version;
        public Vector3 Origin;     // QC g_waypointeditor_symmetrical_origin
        public Vector3 Axis;       // QC g_waypointeditor_symmetrical_axis (m, q packed in x/y)
        public int Order;          // QC the symmetry order (0 = none/auto, 2..4 rotational, -1/-2 mirror)
    }

    /// <summary>The symmetry header of the last <see cref="LoadFromText"/>, or null if none was present.</summary>
    public SymmetryHeader? Symmetry { get; private set; }

    /// <summary>
    /// Parse a Xonotic <c>.waypoints</c> file (QC waypoint_loadall). Format: an optional <c>//WAYPOINT_*</c>
    /// comment header (version + symmetry, captured into <see cref="Symmetry"/>), then repeating triples of
    /// lines — m1 ("x y z" = origin+mins), m2 (origin+maxs), flags (int). A point waypoint has m1 == m2;
    /// otherwise it's a waybox. Returns a network with nodes added but NOT linked — call
    /// <see cref="LoadLinks"/> with the matching <c>.waypoints.cache</c>, <see cref="LoadHardwiredLinks"/>
    /// with the <c>.waypoints.hardwired</c> file, or <see cref="AutoLink"/>.
    /// </summary>
    public static WaypointNetwork LoadFromText(string text)
    {
        var net = new WaypointNetwork();
        using var reader = new StringReader(text);
        string? line;
        bool parsingComments = true;
        var pending = new List<string>(3);
        SymmetryHeader? sym = null;

        while ((line = reader.ReadLine()) is not null)
        {
            if (parsingComments)
            {
                if (line.StartsWith("//", StringComparison.Ordinal))
                {
                    sym ??= new SymmetryHeader();
                    ParseSymmetryComment(line, sym); // WAYPOINT_VERSION / WAYPOINT_SYMMETRY*
                    continue;
                }
                parsingComments = false;
            }

            pending.Add(line);
            if (pending.Count == 3)
            {
                if (TryParseVec(pending[0], out var m1) && TryParseVec(pending[1], out var m2)
                    && int.TryParse(pending[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int fl))
                {
                    var origin = (m1 + m2) * 0.5f;
                    var mins = m1 - origin;
                    var maxs = m2 - origin;
                    net.Add(origin, mins, maxs, (WaypointFlags)fl);
                }
                pending.Clear();
            }
        }
        net.Symmetry = sym;
        return net;
    }

    /// <summary>Parse one <c>//WAYPOINT_*</c> header comment into the symmetry header (QC the editor cvars).</summary>
    private static void ParseSymmetryComment(string line, SymmetryHeader sym)
    {
        // forms: "//WAYPOINT_VERSION 1", "//WAYPOINT_SYMMETRY_ORIGIN x y z",
        //        "//WAYPOINT_SYMMETRY_AXIS m q", "//WAYPOINT_SYMMETRY_ORDER n"
        string body = line.TrimStart('/').Trim();
        int sp = body.IndexOf(' ');
        if (sp < 0) return;
        string key = body[..sp].ToUpperInvariant();
        string val = body[(sp + 1)..].Trim();
        switch (key)
        {
            case "WAYPOINT_VERSION":
                int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out sym.Version);
                break;
            case "WAYPOINT_SYMMETRY_ORIGIN":
                if (TryParseVec(val, out var o)) sym.Origin = o;
                break;
            case "WAYPOINT_SYMMETRY_AXIS":
                if (TryParseVec(val + " 0", out var a)) sym.Axis = a; // m q -> (m, q, 0)
                break;
            case "WAYPOINT_SYMMETRY_ORDER":
                int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out sym.Order);
                break;
        }
    }

    /// <summary>Load a <c>.waypoints</c> file from disk (QC fopen path maps/&lt;map&gt;.waypoints).</summary>
    public static WaypointNetwork LoadFromFile(string path) => LoadFromText(File.ReadAllText(path));

    /// <summary>
    /// The host's one-call map-waypoint setup (QC the waypoint load-or-autogenerate path), built ONCE per map
    /// and shared across all bot brains. If the map ships a hand-authored <c>.waypoints</c> file, load its
    /// nodes, then wire the edges from the precompiled <c>.waypoints.cache</c> (just cost math — no tracewalk)
    /// plus any <c>.waypoints.hardwired</c> links; only when no cache ships do we pay the
    /// <see cref="AutoLink"/> tracewalk pass. Otherwise auto-generate a navigable graph from the live map
    /// entities (<see cref="GenerateFromEntities"/>) so bots can still play. Pass the loaded file texts
    /// (<paramref name="waypointFileText"/> null/empty → auto-generate) and the spawned entity list.
    ///
    /// <paramref name="skill"/> is the server's current bot <c>skill</c> (QC global): it is applied to
    /// <see cref="Skill"/> BEFORE any link cost is baked, so <see cref="LinearCost"/>'s 1.25x bunnyhop
    /// speedup (QC waypoint_getlinearcost: <c>skill &gt;= bot_ai_bunnyhop_skilloffset</c>) takes effect on
    /// the built costs — matching Base, where the global skill is set before the waypoint costs are computed.
    /// Defaults to 0 (no speedup) so non-host callers keep the prior behaviour.
    /// </summary>
    public static WaypointNetwork ForMap(string? waypointFileText, IReadOnlyList<XonoticGodot.Common.Framework.Entity> entities,
        string? linkCacheText = null, string? hardwiredText = null, int skill = 0, int bunnyhopSkillOffset = 7)
    {
        if (!string.IsNullOrWhiteSpace(waypointFileText))
        {
            var net = LoadFromText(waypointFileText);
            // Skill must be set before linking: link costs are baked once here (LoadLinks/AutoLink → TravelCost
            // → LinearCost reads Skill); QC reads the global skill live at each waypoint_getlinearcost call.
            net.Skill = skill;
            net.BunnyhopSkillOffset = bunnyhopSkillOffset;
            bool haveCache = !string.IsNullOrWhiteSpace(linkCacheText);
            if (haveCache)
                net.LoadLinks(linkCacheText!);
            if (!string.IsNullOrWhiteSpace(hardwiredText))
                net.LoadHardwiredLinks(hardwiredText!);
            // No precompiled link cache → compute reachability links now (the expensive tracewalk pass). With a
            // cache the edges are already loaded above, so AutoLink (and its O(N^2) tracewalks) is skipped.
            if (!haveCache)
                net.AutoLink();
            return net;
        }
        var auto = new WaypointNetwork();
        auto.Skill = skill;
        auto.BunnyhopSkillOffset = bunnyhopSkillOffset;
        auto.GenerateFromEntities(entities);
        return auto;
    }

    /// <summary>
    /// Load the link cache (QC waypoint_load_links) over an already-loaded network. Each non-comment line is
    /// "fromOrigin*toOrigin" (two "x y z" vectors separated by '*'); each adds one directed link, with cost
    /// auto-computed. Waypoints are matched to the nearest existing node within 1 unit (QC findradius 1).
    /// </summary>
    public void LoadLinks(string cacheText)
    {
        using var reader = new StringReader(cacheText);
        string? line;
        bool parsingComments = true;
        while ((line = reader.ReadLine()) is not null)
        {
            if (parsingComments)
            {
                if (line.StartsWith("//", StringComparison.Ordinal)) continue;
                parsingComments = false;
            }
            int star = line.IndexOf('*');
            if (star < 0) continue;
            if (!TryParseVec(line.AsSpan(0, star).ToString(), out var fromPos)) continue;
            if (!TryParseVec(line.AsSpan(star + 1).ToString(), out var toPos)) continue;

            var from = FindAt(fromPos);
            var to = FindAt(toPos);
            if (from is null || to is null) continue;
            Link(from, to); // directed (the cache lists each direction explicitly)
        }
    }

    /// <summary>
    /// Load a hardwired-link file (QC waypoint_load_hardwiredlinks): map maker-authored links the auto-linker
    /// must never remove. Format: lines of "fromOrigin*toOrigin" (two "x y z" vectors separated by '*'),
    /// with <c>//</c> and <c>#</c> comment lines ignored; a leading <c>*</c> marks a "special" link (kept,
    /// the marker is stripped). Each adds one directed link with auto-computed cost, matched to the nearest
    /// node within 5 units (QC findradius 5 for hardwired vs. 1 for the cache). Links added here are flagged
    /// on their source waypoint so <see cref="AutoLink"/> won't overwrite them.
    /// </summary>
    public void LoadHardwiredLinks(string hardwiredText)
    {
        using var reader = new StringReader(hardwiredText);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("#", StringComparison.Ordinal))
                continue;
            string s = line;
            // QC waypoint_load_hardwiredlinks (waypoints.qc:1501-1507): a leading '*' marks a "special" link
            // (jump/teleport extras that old Xonotic versions skip). The marker is stripped, then the link is
            // only added if it passes the special-link source-type filter below.
            bool isSpecial = s.StartsWith("*", StringComparison.Ordinal);
            if (isSpecial)
                s = s[1..];

            int star = s.IndexOf('*');
            if (star < 0) continue;
            if (!TryParseVec(s.AsSpan(0, star).ToString(), out var fromPos)) continue;
            if (!TryParseVec(s.AsSpan(star + 1).ToString(), out var toPos)) continue;

            var from = FindAt(fromPos, 5f);
            var to = FindAt(toPos, 5f);
            if (from is null || to is null) continue;

            if (!isSpecial)
            {
                // Normal hardwired link: always added + the source kept off the auto-relinker
                // (QC waypoint_addlink + waypoint_mark_hardwiredlink). CustomJp stands in for the absent
                // dedicated hardwired-link concept (it sets WPFLAGMASK_NORELINK so AutoLink skips this source).
                from.Flags |= WaypointFlags.CustomJp;
                Link(from, to);
            }
            else
            {
                // Special link: QC adds it ONLY when the source is a NORELINK source AND it is a JUMP/SUPPORT
                // waypoint or a TELEPORT box (waypoints.qc:1569-1573). Otherwise the special link is dropped.
                bool norelink = (from.Flags & (WaypointFlags.Teleport | WaypointFlags.Ladder
                    | WaypointFlags.Jump | WaypointFlags.CustomJp | WaypointFlags.Support)) != 0;
                bool jumpOrSupport = (from.Flags & (WaypointFlags.Jump | WaypointFlags.Support)) != 0;
                bool teleportBox = from.IsBox && from.HasFlag(WaypointFlags.Teleport);
                if (norelink && (jumpOrSupport || teleportBox))
                    Link(from, to);
            }
        }
    }

    // ---- .waypoints file writers (QC waypoint_saveall / waypoint_save_links / waypoint_save_hardwiredlinks) ----

    /// <summary>QC WAYPOINT_VERSION (waypoints.qc) — written into the saved file headers.</summary>
    public const string WaypointVersion = "1.04";

    /// <summary>
    /// QC <c>vtos</c>: a vector rendered "x y z" with each component rounded to 1 decimal place (the precision the
    /// shipped <c>.waypoints</c> files use). Round-trips with <see cref="TryParseVec"/>. Unquoted — matches the
    /// QC writers, which emit bare <c>vtos</c> output (the engine's vtos adds no quotes when fputs'd).
    /// </summary>
    private static string Vtos(Vector3 v) => string.Format(CultureInfo.InvariantCulture,
        "{0:0.0} {1:0.0} {2:0.0}", v.X, v.Y, v.Z);

    /// <summary>
    /// Serialize the node file (QC waypoint_saveall, waypoints.qc:1755): a <c>//WAYPOINT_VERSION/SYMMETRY/TIME</c>
    /// header followed by one triple (origin+mins, origin+maxs, flags) per NON-generated waypoint
    /// (WAYPOINTFLAG_GENERATED nodes are runtime-only and skipped, exactly as QC does). The inverse of
    /// <see cref="LoadFromText"/>; <paramref name="time"/> stamps the WAYPOINT_TIME line (UTC, QC's strftime).
    /// </summary>
    public string SaveToText(string? time = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("//WAYPOINT_VERSION ").Append(WaypointVersion).Append('\n');
        sb.Append("//WAYPOINT_SYMMETRY 0\n");
        sb.Append("//WAYPOINT_TIME ").Append(time ?? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
        foreach (var wp in _nodes)
        {
            if (wp.HasFlag(WaypointFlags.Generated)) continue; // QC: generated nodes are not persisted
            sb.Append(Vtos(wp.AbsMin)).Append('\n');
            sb.Append(Vtos(wp.AbsMax)).Append('\n');
            sb.Append(((int)wp.Flags).ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Serialize the precompiled link cache (QC waypoint_save_links, waypoints.qc:1716): a version/time header,
    /// then a <c>fromOrigin*toOrigin</c> line for each NON-hardwired, non-special outgoing link. Links whose
    /// source is a JUMP/SUPPORT/CUSTOM_JP waypoint are excluded (they live in the hardwired file instead), and
    /// hardwired links (CustomJp sources, the port's hardwired marker) are excluded — matching the QC IL_EACH
    /// filter <c>!(JUMP|SUPPORT|CUSTOM_JP)</c> + <c>!waypoint_is_hardwiredlink</c>. The inverse of <see cref="LoadLinks"/>.
    /// </summary>
    public string SaveLinksToText(string? time = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("//WAYPOINT_VERSION ").Append(WaypointVersion).Append('\n');
        if (!string.IsNullOrEmpty(time))
            sb.Append("//WAYPOINT_TIME ").Append(time).Append('\n');
        foreach (var wp in _nodes)
        {
            if ((wp.Flags & (WaypointFlags.Jump | WaypointFlags.Support | WaypointFlags.CustomJp)) != 0)
                continue;
            foreach (var link in wp.Links)
                sb.Append(Vtos(wp.Origin)).Append('*').Append(Vtos(link.To.Origin)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Serialize the hardwired/special-link file (QC waypoint_save_hardwiredlinks, waypoints.qc:1662): a
    /// <c>// HARDWIRED LINKS</c> section listing each hardwired link (<c>from*to</c>) followed by a
    /// <c>// SPECIAL LINKS</c> section listing each link from a JUMP/SUPPORT/CUSTOM_JP source (<c>*from*to</c>).
    /// The port's hardwired marker is the CustomJp source flag (no dedicated wphwXX mirror), so its hardwired
    /// links and its special links over the same set overlap — matching the QC behaviour where a CUSTOM_JP
    /// source's links appear in both sections. The inverse of <see cref="LoadHardwiredLinks"/>.
    /// </summary>
    public string SaveHardwiredLinksToText()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("// HARDWIRED LINKS\n");
        foreach (var wp in _nodes)
        {
            if (!wp.HasFlag(WaypointFlags.CustomJp)) continue; // hardwired marker
            foreach (var link in wp.Links)
                sb.Append(Vtos(wp.Origin)).Append('*').Append(Vtos(link.To.Origin)).Append('\n');
        }
        sb.Append("\n// SPECIAL LINKS\n");
        foreach (var wp in _nodes)
        {
            if ((wp.Flags & (WaypointFlags.Jump | WaypointFlags.Support | WaypointFlags.CustomJp)) == 0) continue;
            foreach (var link in wp.Links)
                sb.Append('*').Append(Vtos(wp.Origin)).Append('*').Append(Vtos(link.To.Origin)).Append('\n');
        }
        return sb.ToString();
    }

    // Bucket a position into the spatial-hash grid (FindHashCell-sized cells).
    private static (int, int, int) FindCell(Vector3 p) =>
        ((int)MathF.Floor(p.X / FindHashCell), (int)MathF.Floor(p.Y / FindHashCell), (int)MathF.Floor(p.Z / FindHashCell));

    private void EnsureFindHash()
    {
        if (_findHash is not null && _findHashCount == _nodes.Count) return;
        var h = new Dictionary<(int, int, int), List<Waypoint>>(_nodes.Count);
        foreach (var wp in _nodes)
        {
            var key = FindCell(wp.Origin);
            if (!h.TryGetValue(key, out var list)) h[key] = list = new List<Waypoint>(1);
            list.Add(wp);
        }
        _findHash = h;
        _findHashCount = _nodes.Count;
    }

    // Nearest node whose Origin is within <paramref name="radius"/> of <paramref name="pos"/>, or null. Used to
    // match a cache/hardwired link endpoint back to its node. O(1) via the spatial hash (the old O(nodes) scan was
    // the dominant cost of the first-bot-frame waypoint load). Falls back to a linear scan if the requested radius
    // exceeds one grid cell (the 27-cell probe could miss beyond that) — never the case for the shipped files.
    private Waypoint? FindAt(Vector3 pos, float radius = 1f)
    {
        float r2 = radius * radius;
        if (radius > FindHashCell)
        {
            Waypoint? lin = null; float linBest = r2;
            foreach (var wp in _nodes)
            {
                float d = (wp.Origin - pos).LengthSquared();
                if (d < linBest) { linBest = d; lin = wp; }
            }
            return lin;
        }

        EnsureFindHash();
        var (cx, cy, cz) = FindCell(pos);
        Waypoint? best = null; float bestD2 = r2;
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                    if (_findHash!.TryGetValue((cx + dx, cy + dy, cz + dz), out var list))
                        foreach (var wp in list)
                        {
                            float d2 = (wp.Origin - pos).LengthSquared();
                            if (d2 < bestD2) { bestD2 = d2; best = wp; }
                        }
        return best;
    }

    /// <summary>
    /// Parse a Quake vector literal "x y z" (the form vtos emits, 1 decimal place). The shipped
    /// <c>.waypoints</c>/<c>.waypoints.cache</c>/<c>.waypoints.hardwired</c> files single-quote each vector —
    /// <c>'-507.0 1112.6 409.0'</c> (DP's <c>vtos</c> emits the quotes; QC's <c>stov</c> strips them). The
    /// quotes must be stripped from the whole literal BEFORE splitting: vtos right-aligns each component, so a
    /// small value carries a leading space inside the quotes (<c>' 46.8 -380.6 536.0'</c>) and a per-token trim
    /// would leave the lone opening quote as its own token and shift the parse. Without this strip every line
    /// was rejected and the graph parsed to zero nodes, forcing the expensive auto-link tracewalk fallback.
    /// </summary>
    private static bool TryParseVec(string s, out Vector3 v)
    {
        v = Vector3.Zero;
        string t = s.Trim();
        if (t.Length >= 2 && t[0] == '\'' && t[^1] == '\'')
            t = t[1..^1]; // strip the surrounding vtos quotes (handles a leading-space component)
        var parts = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;
        // per-token trim too, in case a variant quotes each component individually ('x' 'y' 'z').
        if (!float.TryParse(parts[0].Trim('\''), NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) return false;
        if (!float.TryParse(parts[1].Trim('\''), NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) return false;
        if (!float.TryParse(parts[2].Trim('\''), NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) return false;
        v = new Vector3(x, y, z);
        return true;
    }

    /// <summary>A tiny binary min-heap over node indices keyed by float priority (A* open set).</summary>
    private sealed class MinHeap
    {
        private int[] _idx;
        private float[] _pri;
        private int _size;

        public MinHeap(int capacity)
        {
            capacity = System.Math.Max(capacity, 16);
            _idx = new int[capacity];
            _pri = new float[capacity];
        }

        /// <summary>Reset to empty for reuse across searches (keeps the backing arrays).</summary>
        public void Clear() => _size = 0;

        public void Push(int nodeIndex, float priority)
        {
            if (_size == _idx.Length)
            {
                Array.Resize(ref _idx, _size * 2);
                Array.Resize(ref _pri, _size * 2);
            }
            int i = _size++;
            _idx[i] = nodeIndex;
            _pri[i] = priority;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (_pri[parent] <= _pri[i]) break;
                Swap(i, parent);
                i = parent;
            }
        }

        public bool TryPop(out int nodeIndex)
        {
            if (_size == 0) { nodeIndex = -1; return false; }
            nodeIndex = _idx[0];
            _size--;
            if (_size > 0)
            {
                _idx[0] = _idx[_size];
                _pri[0] = _pri[_size];
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = 2 * i + 2, smallest = i;
                    if (l < _size && _pri[l] < _pri[smallest]) smallest = l;
                    if (r < _size && _pri[r] < _pri[smallest]) smallest = r;
                    if (smallest == i) break;
                    Swap(i, smallest);
                    i = smallest;
                }
            }
            return true;
        }

        private void Swap(int a, int b)
        {
            (_idx[a], _idx[b]) = (_idx[b], _idx[a]);
            (_pri[a], _pri[b]) = (_pri[b], _pri[a]);
        }
    }
}
