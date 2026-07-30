using System.Numerics;
using VortexArena.Common.Diagnostics;

namespace VortexArena.Server.Bot;

/// <summary>
/// Editing operations over a live <see cref="WaypointNetwork"/> — the port's answer to Base's
/// <c>wpeditor</c> command family (server/command/cmd.qc:341), driven by the map editor's Waypoint tool
/// (design doc §11.9).
///
/// It lives SERVER-side and mutates the server's own graph, because that is where the graph is: bots path
/// against <see cref="BotPopulation.Network"/>, so an editor working on a private copy would be editing
/// something no bot ever reads. The editor reaches it in-process — a listen host runs both halves — and the
/// caller is responsible for holding the sim gate while it does, exactly as the host console does for every
/// other world-mutating verb.
///
/// One deliberate divergence from Base. Its <c>spawn</c> and <c>remove</c> require <c>IS_PLAYER</c>, because a
/// waypoint goes at the player's feet; the editor's EDIT state is a free-flying observer with no feet, so
/// placement here is CROSSHAIR-based (Base's own <c>spawn crosshair</c> variant) and takes the position from
/// the caller rather than from a body.
/// </summary>
public static class WaypointEditor
{
    /// <summary>
    /// How close a click has to be to count as hitting a waypoint. Generous: waypoints are invisible points
    /// with no geometry, and the alternative to a forgiving radius is a mapper clicking repeatedly at
    /// something they can see marked on screen and being told there is nothing there.
    /// </summary>
    public const float PickRadius = 48f;

    /// <summary>Auto-link distance used when relinking after an edit (QC waypoint_addlink_for's 1050).</summary>
    public const float RelinkDistance = 1050f;

    /// <summary>The waypoint nearest <paramref name="point"/> within <see cref="PickRadius"/>, or null.</summary>
    public static Waypoint? Pick(WaypointNetwork net, Vector3 point, float radius = PickRadius)
    {
        ArgumentNullException.ThrowIfNull(net);

        Waypoint? best = null;
        float bestDist = radius * radius;
        foreach (Waypoint wp in net.Nodes)
        {
            float d = (wp.Center - point).LengthSquared();
            if (d < bestDist)
            {
                bestDist = d;
                best = wp;
            }
        }
        return best;
    }

    /// <summary>
    /// Place a waypoint (QC <c>waypoint_spawn_fromeditor</c>). <paramref name="flags"/> selects the kind:
    /// none for an ordinary node, <see cref="WaypointFlags.Jump"/> / <see cref="WaypointFlags.Crouch"/> /
    /// <see cref="WaypointFlags.Support"/> for the special ones.
    ///
    /// A jump or support waypoint is only half a statement — it needs a DESTINATION, which Base expresses by
    /// spawning a second waypoint afterwards. That pairing is the caller's business (see
    /// <see cref="LinkPending"/>); this just puts the node down.
    /// </summary>
    public static Waypoint Place(WaypointNetwork net, Vector3 origin, WaypointFlags flags = WaypointFlags.None)
    {
        ArgumentNullException.ThrowIfNull(net);
        Waypoint wp = net.Add(origin, flags);
        Log.Info($"waypoint: placed {Describe(wp)} at {origin.X:0} {origin.Y:0} {origin.Z:0}");
        return wp;
    }

    /// <summary>
    /// Remove a waypoint and every link that pointed at it (QC <c>waypoint_remove_fromeditor</c>).
    ///
    /// Unhooking the INCOMING links is the part that matters and the part a naive removal misses: a link is
    /// held as a reference from the source node, so dropping the node from the list leaves every neighbour
    /// still routing through an object that is no longer in the graph — bots would path into a waypoint that
    /// does not exist.
    /// </summary>
    public static bool Remove(WaypointNetwork net, Waypoint wp)
    {
        ArgumentNullException.ThrowIfNull(net);
        ArgumentNullException.ThrowIfNull(wp);

        if (!net.RemoveNode(wp))
            return false;

        Log.Info($"waypoint: removed {Describe(wp)}");
        return true;
    }

    /// <summary>
    /// Link <paramref name="from"/> to <paramref name="to"/> as a HARDWIRED link (QC
    /// <c>wpeditor hardwire</c>): a hand-authored connection that survives a relink, for the routes the
    /// automatic tracewalk cannot find on its own.
    /// </summary>
    public static void Hardwire(WaypointNetwork net, Waypoint from, Waypoint to)
    {
        ArgumentNullException.ThrowIfNull(net);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        // CustomJp is the port's hardwired marker — the writers key the hardwired file off it, so the flag has
        // to go on before the link or the link is written into the wrong file and lost on the next relink.
        from.Flags |= WaypointFlags.CustomJp;
        net.Link(from, to);
        Log.Info($"waypoint: hardwired {Describe(from)} -> {Describe(to)}");
    }

    /// <summary>
    /// Finish a pending two-part placement: a jump or support waypoint plus the destination spawned after it.
    /// Returns false when the pair does not make sense, which is the honest answer for a stray second click.
    /// </summary>
    public static bool LinkPending(WaypointNetwork net, Waypoint from, Waypoint to)
    {
        ArgumentNullException.ThrowIfNull(net);
        if (from is null || to is null || ReferenceEquals(from, to))
            return false;

        net.Link(from, to);

        // A SUPPORT waypoint's destination has its other incoming links removed (QC: "spawn another waypoint to
        // create destination from which all incoming links are removed"), which is the whole point of the kind:
        // it forces traffic through the supported route instead of leaving the problematic direct link in play.
        if (from.HasFlag(WaypointFlags.Support))
            net.RemoveIncomingLinksExcept(to, from);

        Log.Info($"waypoint: linked {Describe(from)} -> {Describe(to)}");
        return true;
    }

    /// <summary>Relink the whole graph as if every waypoint had just respawned (QC <c>relinkall</c>).</summary>
    public static void RelinkAll(WaypointNetwork net)
    {
        ArgumentNullException.ThrowIfNull(net);
        net.AutoLink(RelinkDistance);
        Log.Info($"waypoint: relinked {net.Nodes.Count} waypoints");
    }

    /// <summary>
    /// Waypoints with no way in or no way out (QC <c>wpeditor unreachable</c>) — the check that finds the
    /// mistakes, because a node a bot can enter and never leave is invisible until a bot gets stuck in it.
    /// </summary>
    public static List<Waypoint> Unreachable(WaypointNetwork net)
    {
        ArgumentNullException.ThrowIfNull(net);

        var hasIncoming = new HashSet<Waypoint>();
        foreach (Waypoint wp in net.Nodes)
            foreach (WaypointLink link in wp.Links)
                hasIncoming.Add(link.To);

        var bad = new List<Waypoint>();
        foreach (Waypoint wp in net.Nodes)
            if (wp.Links.Count == 0 || !hasIncoming.Contains(wp))
                bad.Add(wp);
        return bad;
    }

    /// <summary>Short human label for a waypoint, naming its kind.</summary>
    public static string Describe(Waypoint wp)
    {
        ArgumentNullException.ThrowIfNull(wp);
        string kind =
            wp.HasFlag(WaypointFlags.Jump) ? "jump" :
            wp.HasFlag(WaypointFlags.Crouch) ? "crouch" :
            wp.HasFlag(WaypointFlags.Support) ? "support" :
            wp.HasFlag(WaypointFlags.Teleport) ? "teleport" :
            wp.HasFlag(WaypointFlags.CustomJp) ? "hardwired" :
            wp.IsBox ? "waybox" : "waypoint";
        return $"{kind} #{wp.Index}";
    }
}
