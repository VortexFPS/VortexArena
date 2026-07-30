using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server.Bot;

/// <summary>
/// A scored goal candidate produced during goal-rating (QC navigation_routerating's running best). The
/// brain picks the highest-rated one and routes to it. <see cref="Target"/> is the entity (item/enemy) or
/// null for a bare position goal (e.g. a roam waypoint).
/// </summary>
public readonly struct GoalRating
{
    public readonly Vector3 Position;
    public readonly Entity? Target;
    public readonly float Rating;

    public GoalRating(Vector3 position, Entity? target, float rating)
    {
        Position = position;
        Target = target;
        Rating = rating;
    }
}

/// <summary>
/// Accumulates rated goals during a strategy frame (QC navigation_goalrating_start/routerating/end). The
/// QC code weights item value against travel cost along the waypoint graph; this port uses value weighted
/// by inverse straight-line distance (rangebias / (rangebias + dist)), which preserves the "prefer near,
/// valuable goals" behaviour without recomputing the whole Dijkstra field every frame. Picking the actual
/// route is then left to <see cref="BotNavigation.SetGoal"/> (waypoint A*).
/// </summary>
public sealed class GoalRater
{
    private GoalRating _best;
    private bool _has;

    // Route context (QC navigation_markroutes' cached cost field): when set, Rate uses the waypoint-graph path
    // cost instead of straight-line distance. Seeded by the brain each strategy frame via SeedRoute, BEFORE the
    // role runs (and so before the role's own Start()); Start() must NOT clear these.
    private WaypointNetwork? _routeNet;
    private Vector3 _routeFrom;
    private bool _routeSeeded;

    public bool HasGoal => _has;
    public GoalRating Best => _best;

    /// <summary>Scratch buffer reused across this rater's FindInRadius/FindByClass scans (sim-thread, token-gated,
    /// so never re-entered): one List per brain instead of an iterator per goal-rating call. The list overloads
    /// clear it on entry, so each scan must be fully consumed before the next reuses it (none here nest a find
    /// while iterating it).</summary>
    internal readonly List<Entity> Scratch = new();

    public void Start()
    {
        _best = default;
        _has = false;
    }

    /// <summary>
    /// Seed the waypoint route-cost field for this strategy frame (QC navigation_markroutes from the bot). After
    /// this, <see cref="Rate"/> discounts a candidate by its real path cost from <paramref name="from"/> along the
    /// graph, falling back to straight-line when the graph can't reach the candidate. Pass net = null to keep the
    /// prior straight-line behaviour (graphless roaming / tests).
    /// </summary>
    /// <returns>The entry-seed set the flood used (null when net is null) — hand it to
    /// <see cref="BotNavigation.SetGoal"/> so the route build skips a second identical tracewalk search
    /// (aliases the network's scratch; copy before the next seed search — see ComputeRouteCosts).</returns>
    public IReadOnlyList<(Waypoint Wp, float Cost)>? SeedRoute(WaypointNetwork? net, Vector3 from, bool onGround = true)
    {
        _routeNet = net;
        _routeFrom = from;
        _routeSeeded = net is not null;
        return net?.ComputeRouteCosts(from, onGround);
    }

    /// <summary>Rate a candidate goal (QC navigation_routerating): value <paramref name="f"/> discounted by distance.</summary>
    public void Rate(Vector3 from, Entity? target, Vector3 goalPos, float f, float rangeBias)
    {
        if (f <= 0f) return;
        // QC navigation_routerating distance term: the Dijkstra path cost over the waypoint graph (.wpcost). When
        // the route field is seeded and the goal is graph-reachable, use it; else fall back to a straight-line
        // cost in the same unit (distance/MaxSpeed) so the rangebias scale matches the graph path.
        float cost = float.PositiveInfinity;
        if (_routeSeeded && _routeNet is not null)
            cost = _routeNet.RouteCostTo(target, goalPos); // entity goals ride the QC nearest-waypoint cache
        if (float.IsPositiveInfinity(cost))
            cost = (goalPos - from).Length() / System.MathF.Max(1f, Cvars.MaxSpeed);
        float rating = f * (rangeBias / (rangeBias + cost));
        if (!_has || rating > _best.Rating)
        {
            _best = new GoalRating(goalPos, target, rating);
            _has = true;
        }
    }

    /// <summary>Rate a WAYPOINT goal (perf 2026-07-03): the candidate already IS a graph node, so its route cost
    /// reads its own flood slot directly (<see cref="WaypointNetwork.RouteCostToWaypoint"/>) — the generic
    /// <see cref="Rate"/> path would tracewalk-Nearest its way back to the node it was handed, once per shell
    /// candidate in the roam rating. Same cost semantics (the nearest waypoint to a waypoint is itself).</summary>
    public void RateWaypoint(Vector3 from, Waypoint wp, float f, float rangeBias)
    {
        if (f <= 0f) return;
        float cost = float.PositiveInfinity;
        if (_routeSeeded && _routeNet is not null)
            cost = _routeNet.RouteCostToWaypoint(wp);
        if (float.IsPositiveInfinity(cost))
            cost = (wp.Center - from).Length() / System.MathF.Max(1f, Cvars.MaxSpeed);
        float rating = f * (rangeBias / (rangeBias + cost));
        if (!_has || rating > _best.Rating)
        {
            _best = new GoalRating(wp.Center, null, rating);
            _has = true;
        }
    }

    public void End() { /* QC navigation_goalrating_end commits navigation_bestgoal; we expose Best directly */ }
}

/// <summary>
/// Bot role/goal selection — the C# port of server/bot/default/havocbot/roles.qc and the
/// HavocBot_ChooseRole mutator dispatch. A role is a per-frame function that fills a <see cref="GoalRater"/>
/// with rated goals (items, enemies, roam waypoints). The brain runs the role on its strategy clock, then
/// routes to the winning goal.
///
/// The generic DM role (seek items + frags, fall back to roaming) and the per-gametype objective roles
/// (CTF / Domination / Onslaught / KeyHunt / Keepaway, in <see cref="BotObjectiveRoles"/>) are all wired:
/// <see cref="ChooseRole"/> dispatches on the gametype NetName so each bot rates its mode's objectives.
/// </summary>
public static class BotRoles
{
    /// <summary>QC BOT_RATING_ENEMY (roles.qh).</summary>
    private const float RatingEnemy = 2500f;

    private static readonly Random Rng = new();

    /// <summary>
    /// Pick the role function for a gametype (QC havocbot_chooserole / HavocBot_ChooseRole). Matches on the
    /// gametype's NetName; unknown/team gametypes fall back to <see cref="RoleGeneric"/>.
    /// </summary>
    public static BotRole ChooseRole(string? gameTypeNetName)
    {
        return (gameTypeNetName ?? "").ToLowerInvariant() switch
        {
            "ctf" => BotObjectiveRoles.RoleCtf,                 // havocbot_role_ctf_* (carrier/offense/defense/middle)
            "keyhunt" or "kh" => BotObjectiveRoles.RoleKeyHunt, // havocbot_role_kh_*
            "dom" or "domination" => BotObjectiveRoles.RoleDomination, // havocbot_role_dom
            "ons" or "onslaught" => BotObjectiveRoles.RoleOnslaught,   // havocbot_role_ons_*
            "ka" or "keepaway" or "tka" => BotObjectiveRoles.RoleKeepaway, // havocbot_role_ka_*
            "freezetag" or "ft" => BotObjectiveRoles.RoleFreezeTag, // havocbot_role_ft_freeing/offense
            "nexball" or "nb" => BotObjectiveRoles.RoleNexball,         // havocbot_role_nexball
            "assault" or "as" => BotObjectiveRoles.RoleAssault,        // havocbot_role_ass_*
            "cts" => BotObjectiveRoles.RoleCts,                        // havocbot_role_cts (run the course)
            "rc" or "race" => BotObjectiveRoles.RoleRace,              // havocbot_role_race (run the track)
            _ => RoleGeneric,
        };
    }

    /// <summary>
    /// Legacy DM role (QC havocbot_role_generic): rate items, enemy players, and roam waypoints, then the
    /// brain routes to the best. Self-contained so it works without team state.
    /// </summary>
    public static void RoleGeneric(BotBrain brain, GoalRater rater)
    {
        var bot = brain.Bot;
        rater.Start();
        GoalrateItems(brain, rater, bot.Origin, 10000f);
        GoalrateEnemyPlayers(brain, rater, bot.Origin, 10000f);
        GoalrateRoamWaypoints(brain, rater, bot.Origin, 3000f);
        rater.End();
    }

    /// <summary>Team-gametype fallback when no objective role applies: plays the DM role.</summary>
    public static void RoleGenericTeam(BotBrain brain, GoalRater rater) => RoleGeneric(brain, rater);

    // ---- goal-rating helpers (QC havocbot_goalrating_*) ----

    /// <summary>
    /// Rate nearby pickup items (QC havocbot_goalrating_items). Items are world entities flagged
    /// <see cref="EntFlags.Item"/>; value is a need-based score (health/armor low =&gt; want more). Faithful to
    /// QC: a taken item (Solid.Not) is still rated when its respawn is imminent — within a skill-scaled lead
    /// window (<c>bot_ai_timeitems</c>) — so high-skill bots time item respawns and camp the spawn. The
    /// passed <paramref name="ratingScale"/> is multiplied by QC's 0.0001 like the original.
    /// </summary>
    public static void GoalrateItems(BotBrain brain, GoalRater rater, Vector3 org, float radius, float scale = 10000f)
    {
        var bot = brain.Bot;
        float ratingScale = scale * 0.0001f; // QC multiplies the passed scale by 0.0001
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        bool timeItems = Cvars.Bool("bot_ai_timeitems");
        float minRespawnDelay = System.Math.Max(11f, Cvars.FloatOr("bot_ai_timeitems_minrespawndelay", 11f));

        // Fill the rater's reused scratch (alloc-free) instead of allocating a findradius iterator each call.
        // The body only reads/rates (no spawn/free), so index-iterating the snapshot directly is safe.
        Api.Entities.FindInRadius(org, radius, rater.Scratch);
        for (int si = 0; si < rater.Scratch.Count; si++)
        {
            Entity it = rater.Scratch[si];
            if (it.IsFreed || ReferenceEquals(it, bot)) continue;
            if ((it.Flags & EntFlags.Item) == 0) continue;

            if (it.Solid == Solid.Not)
            {
                // Item is taken/awaiting respawn. QC: only rate it if the bot times items and the respawn is
                // both long enough to be worth predicting AND coming up within a skill-scaled lead window.
                if (!timeItems) continue;
                if (it.ScheduledRespawnTime <= 0f) continue;
                if (it.RespawnTime < minRespawnDelay) continue;
                bool isPowerup = IsPowerup(it);
                // Jittered respawns aren't reliably predictable — but QC exempts powerups (it.respawntimejitter
                // && !it.itemdef.instanceOfPowerup), since a leading bot still wants to camp the mega/strength.
                if (it.RespawnTimeJitter != 0f && !isPowerup) continue;

                // Lead time the bot will pre-position by (QC havocbot_goalrating_items): powerups scale
                // skill/10 up to 6 s; ordinary items only get a 4 s lead from skill 9+.
                float lead = isPowerup
                    ? System.Math.Clamp(brain.Skill / 10f, 0f, 1f) * 6f
                    : (brain.Skill >= 9f ? 4f : 0f);
                if (now < it.ScheduledRespawnTime - lead) continue; // not soon enough to head there yet
            }

            var pos = (it.AbsMin + it.AbsMax) * 0.5f;
            if (pos == Vector3.Zero) pos = it.Origin;

            float value = ItemValue(brain, it);
            rater.Rate(org, it, pos, value * ratingScale, 2000f);
        }
    }

    /// <summary>
    /// Rate visible enemy players (QC havocbot_goalrating_enemyplayers). Distance-gated, LOS not required
    /// here (the QC version also rates non-visible to encourage pursuit). Skill nudges aggression.
    /// </summary>
    public static void GoalrateEnemyPlayers(BotBrain brain, GoalRater rater, Vector3 org, float radius, float scale = 10000f)
    {
        var bot = brain.Bot;
        // QC havocbot_goalrating_enemyplayers: bot_nofire suppresses chasing players entirely, and a
        // submerged bot won't pursue (it can't fight well underwater).
        if (Cvars.Bool("bot_nofire")) return;
        if (bot.WaterLevel > WaterLevelWetFeet) return;

        // QC the role passes a ratingscale (CTF/KA = 10000, Onslaught offense = 20000); QC multiplies by 0.0001.
        float ratingScale = scale * 0.0001f;
        float radius2 = radius * radius;
        float maxSpeed2 = Cvars.MaxSpeed * 2f;
        maxSpeed2 *= maxSpeed2;
        foreach (var e in brain.Players())
        {
            if (!BotBrain.ShouldAttack(bot, e)) continue;
            float d2 = (e.Origin - org).LengthSquared();
            if (d2 < 100f * 100f || d2 > radius2) continue;
            // QC: ignore enemies moving faster than 2x maxspeed (teleporting / launched) — horizontal only.
            var hv = new Vector3(e.Velocity.X, e.Velocity.Y, 0f);
            if (hv.LengthSquared() > maxSpeed2) continue;

            // health/armor advantage and low skill both increase aggression (QC t factor)
            float advantage = (bot.Health - e.Health) / 150f;
            float t = System.Math.Clamp(1f + advantage, 0f, 3f);
            t += System.Math.Max(0f, 8f - brain.Skill) * 0.05f;
            // NOTE: QC skill>3 also nudges t by the bot's/enemy's Strength/Shield powerup timers
            // (StatusEffects_gettime); the port has no entity-side status-effect expiry accessor yet, so that
            // refinement is deferred (see todos) — the base advantage+skill aggression is preserved here.
            rater.Rate(org, e, e.Origin, ratingScale * t * RatingEnemy, 2000f);
        }
    }

    /// <summary>QC WATERLEVEL_WETFEET: above this the bot is meaningfully submerged.</summary>
    private const int WaterLevelWetFeet = 1;

    /// <summary>
    /// Rate roam waypoints when nothing better exists (QC havocbot_goalrating_waypoints). Walks an outward-
    /// shrinking shell of waypoints around the bot with mild randomness, stopping at the first shell that
    /// rates a candidate, so idle bots wander toward a near-ish waypoint instead of freezing or teleporting
    /// across the map. Only contributes if no stronger goal was rated (checked via <see cref="GoalRater.HasGoal"/>).
    /// </summary>
    public static void GoalrateRoamWaypoints(BotBrain brain, GoalRater rater, Vector3 org, float radius)
    {
        if (rater.HasGoal) return; // only roam when there's no item/enemy goal (QC: navigation_bestgoal guard)
        var net = brain.Network;
        if (net is null || net.Count == 0) return;

        // QC: range=500; sradius = max(range, (0.5+rand*0.5)*sradius); then peel 500-qu shells off the top,
        // stopping at the first shell that contributes a goal (navigation_bestgoal break).
        const float range = 500f;
        float sradius = System.Math.Max(range, (0.5f + (float)Rng.NextDouble() * 0.5f) * radius);

        // Penalize waypoints near the bot's current/most-recent goal so it doesn't immediately re-pick where
        // it's already headed (QC wp_goal_prev0/prev1 history). The port only has the current routed goal,
        // so this approximates wp_goal_prev0; the older prev1 slot has no analogue yet (see todos).
        Vector3? recentGoal = brain.Nav.Current;
        float recentRange2 = (range * 1.5f) * (range * 1.5f);

        while (sradius > 100f)
        {
            float inner = System.Math.Max(100f, sradius - range);
            float outer2 = sradius * sradius;
            float inner2 = inner * inner;
            foreach (var wp in net.Nodes)
            {
                if (wp.HasFlag(WaypointFlags.Teleport)) continue;
                float d2 = (wp.Origin - org).LengthSquared();
                if (d2 >= outer2 || d2 <= inner2) continue;

                float f;
                if (recentGoal is Vector3 g && (wp.Origin - g).LengthSquared() < recentRange2)
                    f = 0.1f; // recently-targeted area — strongly deprioritized (QC f = 0.1)
                else
                    f = 0.5f + (float)Rng.NextDouble() * 0.5f;
                rater.RateWaypoint(org, wp, f, 2000f); // direct node-cost path — no per-candidate Nearest
            }
            if (rater.HasGoal) break; // QC: stop at the first shell that produced navigation_bestgoal
            sradius -= range;
        }
    }

    /// <summary>
    /// Need-based item value — the C# essence of each item's QC <c>bot_pickupevalfunc</c> (the health/armor
    /// pickups rate higher the more the bot lacks that resource; ammo rates higher when low; an unowned
    /// weapon rates high; powerups always pull). Reads the item's classname/NetName + the resource amount it
    /// grants (carried on the world item's resource fields) against the bot's current resources via
    /// <see cref="Resources"/>, so the value falls to ~0 once the bot is topped up (QC: don't path to an item
    /// you can't use).
    /// </summary>
    private static float ItemValue(BotBrain brain, Entity item)
    {
        var bot = brain.Bot;
        string name = string.IsNullOrEmpty(item.NetName) ? item.ClassName : item.NetName;

        // Health: want proportional to the missing fraction (QC commodity_pickupevalfunc for health).
        float itemHealth = item.GetResource(ResourceType.Health);
        if (itemHealth > 0f || Mentions(name, "health"))
        {
            float missing = 1f - bot.Health / System.Math.Max(1f, bot.MaxHealth);
            return System.Math.Max(0f, missing) + 0.25f;
        }

        // Armor: want proportional to the headroom under the armor cap.
        float itemArmor = item.GetResource(ResourceType.Armor);
        if (itemArmor > 0f || Mentions(name, "armor"))
        {
            float cap = System.Math.Max(1f, Resources.GetResourceLimit(bot, ResourceType.Armor));
            float missing = 1f - bot.GetResource(ResourceType.Armor) / cap;
            return System.Math.Max(0f, missing) * 0.9f + 0.1f;
        }

        // Weapon pickup: want strongly if not yet owned, mildly otherwise (for its ammo).
        if (!string.IsNullOrEmpty(item.NetName) && Weapons.ByName(item.NetName) is not null)
            return bot.HasWeapon(item.NetName) ? 0.25f : 0.9f;

        // Ammo: want proportional to how empty the matching pool is.
        foreach (ResourceType ammo in AmmoResources)
        {
            float amt = item.GetResource(ammo);
            if (amt <= 0f) continue;
            float cap = System.Math.Max(1f, Resources.GetResourceLimit(bot, ammo));
            float missing = 1f - bot.GetResource(ammo) / cap;
            return System.Math.Max(0.1f, missing * 0.6f);
        }

        // Powerup / unknown pickup: use the item def's m_botvalue when available (QC generic_pickupevalfunc
        // returns bot_pickupbasevalue directly; see powerups.qh CLASS(Powerup) m_botvalue = 11000, and the
        // jetpack/fuelregen.qh overrides m_botvalue = 3000). Normalized against 11000 (the powerup ceiling)
        // so the result lives in the same 0..1 range as the other branches above.
        int botValue = item.Pickup?.ItemDef.BotValue ?? 0;
        if (botValue > 0)
            return System.Math.Clamp(botValue / 11000f, 0f, 1f);

        // Legacy name-match fallback for items not yet carrying a Pickup ref (pre-existing behavior):
        // the four glowing powerups rate at 1.0 (highest); everything else at 0.3.
        if (Mentions(name, "powerup") || Mentions(name, "strength") || Mentions(name, "shield")
            || Mentions(name, "invincible") || Mentions(name, "buff"))
            return 1.0f;
        return 0.3f;
    }

    private static readonly ResourceType[] AmmoResources =
    {
        ResourceType.Shells, ResourceType.Bullets, ResourceType.Rockets, ResourceType.Cells, ResourceType.Fuel,
    };

    private static bool Mentions(string s, string token)
        => s.Contains(token, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// QC item.itemdef.instanceOfPowerup — whether a world item is a powerup (Strength/Shield/etc.). The port
    /// has no structured itemdef on the world entity, so this matches on the classname/NetName the same way
    /// <see cref="ItemValue"/> does. Used by item-respawn timing: skilled bots camp powerup respawns earlier
    /// (skill-scaled lead) and a powerup is rated even when its respawn is jittered.
    /// </summary>
    private static bool IsPowerup(Entity item)
    {
        string name = string.IsNullOrEmpty(item.NetName) ? item.ClassName : item.NetName;
        return Mentions(name, "powerup") || Mentions(name, "strength") || Mentions(name, "shield")
            || Mentions(name, "invincible") || Mentions(name, "buff");
    }
}

/// <summary>A bot role: fills the rater with goal candidates for this frame (QC <c>.havocbot_role</c>).</summary>
public delegate void BotRole(BotBrain brain, GoalRater rater);

/// <summary>
/// QC <c>havocbot_role</c> values for Key Hunt (sv_keyhunt.qc): the four possible KH bot sub-roles that
/// <see cref="BotObjectiveRoles.RoleKeyHunt"/> cycles through.  <see cref="None"/> is the unassigned
/// initial state that triggers the random-role-pick on the first invocation (mirrors Base's
/// <c>HavocBot_ChooseRole</c> random pick of offense/defense/freelancer at bot-spawn time).
/// </summary>
public enum KhBotRole
{
    None      = 0, // unassigned → first call picks a random starting role (QC HavocBot_ChooseRole)
    Freelancer,    // QC havocbot_role_kh_freelancer  (timeout 10-20 s, then random → offense|defense)
    Defense,       // QC havocbot_role_kh_defense     (timeout 20-30 s, then → freelancer)
    Offense,       // QC havocbot_role_kh_offense     (timeout 20-30 s, then → freelancer)
    Carrier,       // QC havocbot_role_kh_carrier     (no timeout — stays carrier until key is dropped)
}
