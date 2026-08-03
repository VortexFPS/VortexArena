using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;

namespace VortexArena.Server.Bot;

/// <summary>
/// The C# port of <c>navigation_unstuck</c> (server/bot/default/navigation.qc:1908-2007) and the
/// <c>AI_STATUS_STUCK</c> state it services — the one mechanism Base has for "I cannot reach anything from
/// here, shake myself loose", and the last thing standing between a wedged bot and standing still forever.
///
/// <para><b>How Base drives it.</b> <c>navigation_goalrating_end</c> raises <c>AI_STATUS_STUCK</c> when a
/// whole rating pass produced no goal entity (navigation.qc:1861-1867, gated on <c>bot_wander_enable</c>).
/// While the bit is set the goal rater short-circuits (navigation.qc:1833, :1848) and <c>bot_think</c> calls
/// this instead, every think (bot.qc:150-151). It does two things at once:</para>
/// <list type="number">
///   <item><b>Shake loose.</b> Until a reachable waypoint is known, route to a RANDOM nearby waypoint (and
///     re-roll it 10% of thinks). The QC comment is explicit that this is what unwedges a bot "from bad spots
///     or when other bots of the same team block all their ways" — the physical motion matters more than the
///     destination.</item>
///   <item><b>Scan.</b> Walk the non-generated waypoints within <see cref="SearchRadius"/>, ONE PER THINK,
///     tracewalking each, and remember the FARTHEST reachable one. When the queue is exhausted, route there
///     and clear the stuck bit.</item>
/// </list>
///
/// <para>The one-per-think scan and the shared queue owner are not incidental: a full reachability scan is
/// many tracewalks, and Base deliberately spreads it over frames and lets only one bot at a time own the
/// queue so a room full of stuck bots does not multiply the cost. This port keeps both properties — the owner
/// token lives on <see cref="BotPopulation"/> alongside the strategy token.</para>
///
/// <para>See planning/bot-ai-parity-2026-08-03.md D4.</para>
/// </summary>
public sealed class BotUnstuck
{
    /// <summary>QC <c>search_radius</c> (navigation.qc:1921): how far to look for a reachable waypoint.</summary>
    public const float SearchRadius = 1000f;

    private readonly BotBrain _brain;

    /// <summary>QC <c>this.aistatus &amp; AI_STATUS_STUCK</c> (bot.qh:17, BIT(11)): "cannot reach any goal".</summary>
    public bool IsStuck { get; private set; }

    // QC's per-queue globals (bot_waypoint_queue_*), scoped to the owning bot while it holds the token.
    private readonly List<Waypoint> _queue = new();
    private int _cursor;
    private Waypoint? _bestGoal;
    private float _bestGoalRating;

    public BotUnstuck(BotBrain brain) => _brain = brain;

    /// <summary>
    /// QC navigation_goalrating_end (navigation.qc:1861-1867): a rating pass that produced no goal entity puts
    /// the bot into the stuck state, so long as <c>bot_wander_enable</c> is on.
    /// </summary>
    public void NoteRatingProducedNothing()
    {
        if (IsStuck) return;
        if (!Cvars.Bool("bot_wander_enable")) return;
        IsStuck = true;
        Reset();
    }

    /// <summary>QC <c>aistatus &amp;= ~AI_STATUS_STUCK</c>: the bot found somewhere to go.</summary>
    public void Clear()
    {
        if (!IsStuck) return;
        IsStuck = false;
        Reset();
    }

    private void Reset()
    {
        _queue.Clear();
        _cursor = 0;
        _bestGoal = null;
        _bestGoalRating = 0f;
    }

    /// <summary>
    /// One think's worth of unstuck work (QC navigation_unstuck). Returns true if it took an action the caller
    /// should not undo this frame. Does nothing unless the bot is stuck, the map has hand-authored waypoints
    /// (QC checks for a non-GENERATED waypoint first — an auto-generated graph has nothing meaningful to wander
    /// to), and this bot currently owns the shared queue.
    /// </summary>
    public bool Think(Player bot, WaypointNetwork? net, BotNavigation nav)
    {
        if (!IsStuck || net is null || net.Count == 0) return false;
        if (!Cvars.Bool("bot_wander_enable")) { Clear(); return false; }

        // QC navigation.qc:1913-1920: bail unless the map has at least one hand-authored waypoint.
        if (!net.HasUserWaypoints) return false;

        // QC: only the queue owner works the scan; others wait their turn (one scan across the server).
        if (!_brain.TryOwnUnstuckQueue()) return false;

        if (_queue.Count == 0)
        {
            BuildQueue(bot, net);
            if (_queue.Count == 0)
            {
                // QC "stuck, cannot walk to any waypoint at all" — release the token so another bot can try.
                _brain.ReleaseUnstuckQueue();
                return false;
            }
        }

        // ---- evaluate ONE waypoint this think (QC navigation.qc:1935-1950) ----
        Waypoint candidate = _queue[_cursor++];
        if (BotTracewalk.CanWalk(bot.Origin, candidate.ClosestPoint(bot.Origin), nav.Mins, nav.Maxs,
                candidate.IsBox ? candidate.AbsMax.Z - candidate.ClosestPoint(bot.Origin).Z : 0f))
        {
            // QC rates by SQUARED distance and keeps the FARTHEST reachable one — deliberately: the far
            // waypoint is the one most likely to be outside whatever pocket the bot is wedged in.
            float d = (bot.Origin - candidate.Origin).LengthSquared();
            if (d > _bestGoalRating)
            {
                _bestGoalRating = d;
                _bestGoal = candidate;
            }
        }

        // ---- shake loose while the scan runs (QC navigation.qc:1951-1959) ----
        // "this is usually sufficient to unstuck bots from bad spots or when other bots of the same team
        // block all their ways". Route to the waypoint we just looked at, re-rolling occasionally.
        if (_bestGoal is null && (!nav.HasGoal || _brain.NextRandom() < 0.1f))
        {
            nav.ClearRoute();
            nav.SetGoal(bot.Origin, candidate.Center, net, goalEntity: null, onGround: bot.OnGround);
            _brain.DelayStrategy(1f + _brain.NextRandom() * 2f); // QC navigation_goalrating_timeout_expire(1 + random*2)
        }

        // ---- queue exhausted: commit to the farthest reachable waypoint (QC navigation.qc:1961-1978) ----
        if (_cursor >= _queue.Count)
        {
            if (_bestGoal is not null)
            {
                nav.ClearRoute();
                nav.SetGoal(bot.Origin, _bestGoal.Center, net, goalEntity: null, onGround: bot.OnGround);
                Clear(); // QC aistatus &= ~AI_STATUS_STUCK
            }
            else
            {
                // Nothing reachable at all. Drop the queue so the next pass rebuilds it (the world moves:
                // a teammate steps aside, a door opens), and let another bot have the token meanwhile.
                Reset();
            }
            _brain.ReleaseUnstuckQueue();
        }
        return true;
    }

    /// <summary>
    /// QC navigation.qc:1980-2005: gather every non-generated waypoint within <see cref="SearchRadius"/> into
    /// the scan queue. Generated (item/teleporter) waypoints are excluded because they are derived from things
    /// the bot already failed to route to.
    /// </summary>
    private void BuildQueue(Player bot, WaypointNetwork net)
    {
        Reset();
        float r2 = SearchRadius * SearchRadius;
        foreach (Waypoint wp in net.Nodes)
        {
            if (wp.HasFlag(WaypointFlags.Generated)) continue;
            if ((wp.Origin - bot.Origin).LengthSquared() > r2) continue;
            _queue.Add(wp);
        }
    }
}
