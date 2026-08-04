using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;

namespace VortexArena.Server.Bot;

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

    /// <summary>
    /// QC <c>.ignoregoal</c> (roles.qc:140 <c>if (it == this.ignoregoal) continue;</c>): a goal the danger probe
    /// found unreachable, skipped DURING rating so the rest of the pass still competes. The brain stamps this
    /// before running the role and clears it when <see cref="IgnoreGoalUntil"/> lapses.
    ///
    /// <para>The port used to apply it to the WINNER of the whole pass instead, so one bad goal blanked the
    /// entire rating and the bot got nothing at all. See planning/bot-ai-parity-2026-08-03.md D24.</para>
    /// </summary>
    public Entity? IgnoreGoal;

    /// <summary>Sim time at which <see cref="IgnoreGoal"/> stops applying (QC <c>.ignoregoaltime</c>).</summary>
    public float IgnoreGoalUntil;

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

    /// <summary>
    /// Rate a candidate goal (QC navigation_routerating): value <paramref name="f"/> discounted by travel cost.
    ///
    /// <para>An UNREACHABLE candidate is not rated at all. QC gates the whole rating on
    /// <c>if (nwp &amp;&amp; nwp.wpcost &lt; 10000000)</c> (navigation.qc:1408) — a goal the route flood never
    /// reached simply does not compete. The port used to fall back to a straight-line cost, so bots picked goals
    /// behind walls and across gaps and then walked into geometry until a watchdog fired; worse, the unreachable
    /// candidate still set <see cref="HasGoal"/>, which suppressed the roam fallback that would have given the bot
    /// a reachable goal. See planning/bot-ai-parity-2026-08-03.md D5.</para>
    /// </summary>
    public void Rate(Vector3 from, Entity? target, Vector3 goalPos, float f, float rangeBias)
    {
        if (f <= 0f) return;
        // QC roles.qc:140: the ignored goal is skipped as a CANDIDATE, leaving the rest of the pass intact.
        if (target is not null && ReferenceEquals(target, IgnoreGoal) && Api.Clock.Time < IgnoreGoalUntil)
            return;
        float cost = float.PositiveInfinity;
        if (_routeSeeded && _routeNet is not null)
        {
            cost = _routeNet.RouteCostTo(target, goalPos); // entity goals ride the QC nearest-waypoint cache
            if (float.IsPositiveInfinity(cost))
                return; // QC: nwp.wpcost >= 10000000 — the flood never reached it, so it is not a candidate
        }
        else
        {
            // No route field (graphless roaming / tests): straight-line cost in the same unit.
            cost = (goalPos - from).Length() / System.MathF.Max(1f, Cvars.MaxSpeed);
        }
        Commit(goalPos, target, RatingFor(f, rangeBias, cost));
    }

    /// <summary>
    /// QC navigation_routerating's rating formula (navigation.qc:1225-1226, :1418).
    ///
    /// <para><b><paramref name="rangeBias"/> must be converted to travel-cost units</b>
    /// (<c>waypoint_getlinearcost</c>): it is authored as a distance in qu, while <paramref name="cost"/> is a
    /// time in seconds. The port used to compare the two directly, which made the distance factor span
    /// 0.9986..0.9917 with the stock rangebias 2000 — a 0.7% spread across an entire map. Goal choice was
    /// effectively distance-blind: every bot walked to the single highest-value item on the map from anywhere,
    /// and all of Base's per-call-site rangebias tuning (2000 items, 4000 assault, 5000 dom/race, 10000 CTF/ons,
    /// 100000 KH keys) collapsed into one behaviour. See planning/bot-ai-parity-2026-08-03.md F7.</para>
    ///
    /// <para><b>Intended divergence:</b> QC also converts <paramref name="f"/> (navigation.qc:1226). That is a
    /// single constant scale applied to every candidate in a pass (the denominator is maxspeed and skill, both
    /// fixed for the pass), so it cannot change which goal wins — it only rescales the numbers. We leave
    /// <paramref name="f"/> in raw item-value units so ratings stay on the readable BOT_RATING_* scale the
    /// per-item tests pin, and so a debug overlay shows "8000" rather than "20.1".</para>
    /// </summary>
    private float RatingFor(float f, float rangeBias, float cost)
    {
        float bias = LinearCost(rangeBias);
        return f * (bias / (bias + cost));
    }

    /// <summary>QC <c>waypoint_getlinearcost</c>: a distance in qu expressed as the time to walk it.</summary>
    private float LinearCost(float dist)
        => _routeNet is not null ? _routeNet.LinearCost(dist) : dist / System.MathF.Max(1f, Cvars.MaxSpeed);

    private void Commit(Vector3 goalPos, Entity? target, float rating)
    {
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
        float cost;
        if (_routeSeeded && _routeNet is not null)
        {
            cost = _routeNet.RouteCostToWaypoint(wp);
            if (float.IsPositiveInfinity(cost))
                return; // unreachable in the flood — not a candidate (QC navigation.qc:1408)
        }
        else
        {
            cost = (wp.Center - from).Length() / System.MathF.Max(1f, Cvars.MaxSpeed);
        }
        Commit(wp.Center, null, RatingFor(f, rangeBias, cost));
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
            "inv" or "invasion" => BotObjectiveRoles.RoleInvasion,     // hunt the monster waves (port improvement)
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
        // QC havocbot_role_generic (roles.qc:219): rate only when the goal-rating clock expired; the role
        // itself runs every token hold (the brain re-stamps the clock after a rating pass).
        if (!brain.GoalRatingTimedOut) return;
        brain.BeginGoalRating(rater);
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

        // Per-pass arsenal snapshot (lazy): the bot's owned-weapon set can't change mid-pass, so the
        // Weapons.All walk ItemValue needs (arsenal value + owned-ammo-types) runs at most once per
        // GoalrateItems call instead of once per rated item.
        ArsenalCache arsenal = default;

        // Fill the rater's reused scratch (alloc-free) instead of allocating a findradius iterator each call.
        // The body only reads/rates (no spawn/free), so index-iterating the snapshot directly is safe.
        Api.Entities.FindInRadius(org, radius, rater.Scratch);
        for (int si = 0; si < rater.Scratch.Count; si++)
        {
            Entity it = rater.Scratch[si];
            if (it.IsFreed || ReferenceEquals(it, bot)) continue;
            if ((it.Flags & EntFlags.Item) == 0) continue;
            // EntFlags.Item is ALSO the port's FL_PROJECTILE marker (every weapon sets `Flags = EntFlags.Item`
            // with a weapon NetName), so a live rocket/mine passes the flag test and then matches the weapon
            // branch of ItemValue — an in-flight devastator rocket rated ~8000 and won the goal outright, i.e.
            // bots pathed INTO incoming rockets and treated a laid mine as a permanent attractor. Harmless
            // before this branch only because item values were ≤ 1 and always lost.
            // QC iterates IL_EACH(g_items, it.bot_pickup, …) — a list projectiles are not on. The port's
            // equivalent discriminator is Owner: a real pickup is explicitly ownerless ("anyone can pick it
            // up", StartItem.cs:228), while a projectile always carries its firer. Dropped loot is owned only
            // for its 0.5 s anti-instant-pick shield, during which it genuinely isn't available to others.
            if (it.Owner is not null) continue;

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

            // QC roles.qc:143-163 — "Check if the item can be picked up safely". A bot that routes to an item
            // sitting in lava brakes at the edge, marks the goal unreachable, and re-rates from the same spot;
            // with the danger probe that reads as a bot oscillating at a hazard lip forever.
            if (it.ItemIsLoot)   // QC ITEM_IS_LOOT: a dropped weapon/ammo, not a map spawn
            {
                // Dropped loot: only rate it once it has landed, and not if it landed in lava.
                if (!it.OnGround) continue;
                var down = Api.Trace.Trace(pos, Vector3.Zero, Vector3.Zero,
                    pos - new Vector3(0f, 0f, 1500f), MoveFilter.NoMonsters, null);
                if (InLava(down.EndPos + new Vector3(0f, 0f, 1f))) continue;
            }
            else if (InLava(it.Origin + (it.Mins + it.Maxs) * 0.5f))
            {
                continue;
            }

            if (!PickableCheckPlayers(brain, org, it, pos)) continue;

            float value = ItemValue(brain, it, ref arsenal);
            rater.Rate(org, it, pos, value * ratingScale, 2000f);
        }
    }

    /// <summary>QC <c>IN_LAVA(point)</c>: is this point inside a lava (or slime) volume?</summary>
    private static bool InLava(Vector3 point)
        => Api.Services is not null
           && (Api.Trace.PointContents(point)
               & (Engine.Collision.SuperContents.Lava | Engine.Collision.SuperContents.Slime)) != 0;

    /// <summary>
    /// QC <c>havocbot_goalrating_item_pickable_check_players</c> (roles.qc:60-104): in TEAM games, don't race a
    /// teammate for a pickup neither of you urgently needs.
    ///
    /// <para>Finds the nearest teammate who could be left to take this item and the nearest enemy, then rates
    /// it only if an enemy is closer than that teammate (contest it), the teammate is beyond
    /// <c>bot_ai_friends_aware_pickup_radius</c> (nobody's claim), or the bot is practically standing on it.
    /// Without this every bot on a team converges on the same pickup, they funnel into one doorway and
    /// body-block each other — a very common flavour of the reported corner-sticking.</para>
    ///
    /// <para>Note QC's <c>if (!IS_REAL_CLIENT(it)) continue;</c> at roles.qc:73-74: the deference is only ever
    /// extended to HUMAN teammates. Bots do not defer to each other, so a team of bots still competes; the rule
    /// exists to stop bots stealing items out from under the players they are playing with.</para>
    /// </summary>
    private static bool PickableCheckPlayers(BotBrain brain, Vector3 org, Entity item, Vector3 itemOrg)
    {
        if (!Cvars.Teamplay) return true;

        var bot = brain.Bot;
        float friendDist2 = float.MaxValue, enemyDist2 = float.MaxValue;
        foreach (Player p in brain.Players())
        {
            if (ReferenceEquals(p, bot) || p.IsDead || p.IsFreed) continue;
            float d2 = (p.Origin - itemOrg).LengthSquared();
            if (p.Team == bot.Team)
            {
                if (p.IsBot) continue;                   // QC IS_REAL_CLIENT: defer to humans only
                if (d2 > friendDist2) continue;
                if (CanBeLeftToTeammate(bot, p, item)) friendDist2 = d2;
            }
            else if (d2 < enemyDist2)
            {
                enemyDist2 = d2;
            }
        }

        float radius = Cvars.FloatOr("bot_ai_friends_aware_pickup_radius", 500f);
        float mine2 = (itemOrg - org).LengthSquared();
        return (enemyDist2 < friendDist2 && mine2 < enemyDist2)
            || friendDist2 > radius * radius
            || (mine2 < friendDist2 && mine2 < 200f * 200f);
    }

    /// <summary>
    /// QC <c>havocbot_goalrating_item_can_be_left_to_teammate</c> (roles.qc:45-58): would this teammate get
    /// more out of the item than we would? Each clause is "the item gives X and they have no more X than us".
    /// </summary>
    private static bool CanBeLeftToTeammate(Player bot, Player mate, Entity item)
    {
        if (item.GetResource(ResourceType.Health) > 0f
            && mate.GetResource(ResourceType.Health) <= bot.GetResource(ResourceType.Health)) return true;
        if (item.GetResource(ResourceType.Armor) > 0f
            && mate.GetResource(ResourceType.Armor) <= bot.GetResource(ResourceType.Armor)) return true;
        if (!string.IsNullOrEmpty(item.NetName)
            && Weapons.ByName(item.NetName) is { } w && !Inventory.HasWeapon(mate, w)) return true;
        if (IsPowerup(item)) return true;
        if (bot.UnlimitedAmmo) return true;
        foreach (ResourceType ammo in AmmoResources)
            if (item.GetResource(ammo) > 0f && mate.GetResource(ammo) <= bot.GetResource(ammo)) return true;
        return false;
    }

    /// <summary>
    /// Lazy per-rating-pass snapshot of the bot's weapon inventory, filled by <see cref="EnsureArsenal"/> on
    /// first use: the summed <c>bot_pickupbasevalue</c> of owned weapons (QC weapon_pickupevalfunc's arsenal
    /// discount) and which ammo types an owned weapon feeds on (indexed like <see cref="AmmoResources"/>).
    /// </summary>
    private struct ArsenalCache
    {
        public bool Computed;
        public float Value;
        public byte OwnedAmmoMask; // bit i = bot owns a weapon whose AmmoType == AmmoResources[i]
    }

    private static void EnsureArsenal(Entity bot, ref ArsenalCache cache)
    {
        if (cache.Computed) return;
        cache.Computed = true;
        foreach (Weapon w in Weapons.All)
        {
            if (!Inventory.HasWeapon(bot, w)) continue;
            cache.Value += w.BotPickupBaseValue;
            for (int i = 0; i < AmmoResources.Length; i++)
                if (w.AmmoType == AmmoResources[i])
                {
                    cache.OwnedAmmoMask |= (byte)(1 << i);
                    break;
                }
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
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        StatusEffectDef? strength = StatusEffectsCatalog.ByName("strength");
        StatusEffectDef? shield = StatusEffectsCatalog.ByName("shield");
        foreach (var e in brain.Players())
        {
            if (!BotBrain.ShouldAttack(bot, e)) continue;
            float d2 = (e.Origin - org).LengthSquared();
            if (d2 < 100f * 100f || d2 > radius2) continue;
            // QC: ignore enemies moving faster than 2x maxspeed (teleporting / launched) — horizontal only.
            var hv = new Vector3(e.Velocity.X, e.Velocity.Y, 0f);
            if (hv.LengthSquared() > maxSpeed2) continue;

            // QC roles.qc:201 — the advantage term is (health + ARMOR) on both sides, not health alone. With
            // armor dropped, a fully-kitted bot (100/100 against an enemy's 100/0) read itself as even and
            // clamped to t = 1 instead of ~1.67, so it stopped pressing fights it was winning and took item
            // goals instead.
            float advantage = ((bot.Health + bot.GetResource(ResourceType.Armor))
                             - (e.Health + e.GetResource(ResourceType.Armor))) / 150f;
            float t = System.Math.Clamp(1f + advantage, 0f, 3f);
            // QC skill>3: fold in live Strength/Shield timers (StatusEffects_gettime, roles.qc:203-210) —
            // press the advantage while OUR powerup has >1s left; back off a powered-up enemy. The -1 keeps
            // a bot from committing to a chase its powerup won't survive.
            if (brain.Skill > 3f)
            {
                if (strength is not null)
                {
                    if (now < StatusEffectsCatalog.GetTime(bot, strength, now) - 1f) t += 0.5f;
                    if (now < StatusEffectsCatalog.GetTime(e, strength, now) - 1f) t -= 0.5f;
                }
                if (shield is not null)
                {
                    if (now < StatusEffectsCatalog.GetTime(bot, shield, now) - 1f) t += 0.2f;
                    if (now < StatusEffectsCatalog.GetTime(e, shield, now) - 1f) t -= 0.4f;
                }
            }
            t += System.Math.Max(0f, 8f - brain.Skill) * 0.05f;
            // QC roles.qc:212 is `ratingscale *= t` — it COMPOUNDS across the loop, mutating the running scale
            // rather than applying t to a fixed base. So each enemy in radius raises the weight of the next,
            // and with 3+ enemies nearby enemy goals climb well above item goals: the bot commits to the fight
            // instead of wandering off for a pickup. Reproducing the mutation, including QC's `if (ratingscale
            // > 0)` guard — a t of 0 (badly outgunned) latches the scale at zero and stops rating enemies for
            // the rest of the pass, which is the bot deciding this crowd is not worth engaging at all.
            ratingScale *= t;
            if (ratingScale > 0f)
                rater.Rate(org, e, e.Origin, ratingScale * RatingEnemy, 2000f);
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
    /// Need-based item value — the port of QC's <c>bot_pickupevalfunc</c> family (server/items/items.qc:885-979).
    /// CRITICAL SCALE CONTRACT: these return values on the QC <c>BOT_PICKUP_RATING</c> scale (LOW 2500 /
    /// MID 5000 / HIGH 10000, health+armor up to 2× their 5000 base by need), NOT 0..1. The role's ratingscale
    /// (10000-80000) × 0.0001 then lands item goals in the same few-thousand band as enemy goals (2500·t) and
    /// below hard objectives (flags/CPs at 10000+) — exactly the QC priority ladder.
    /// [parity 2026-07-11: the old 0..1 values made every item ~4 orders of magnitude too weak, so bots never
    /// detoured for health/armor/weapons in ANY mode — a root cause of "bots feed and ignore pickups".]
    /// </summary>
    private static float ItemValue(BotBrain brain, Entity item, ref ArsenalCache arsenal)
    {
        var bot = brain.Bot;
        string name = string.IsNullOrEmpty(item.NetName) ? item.ClassName : item.NetName;

        // Health / armor (QC healtharmor_pickupevalfunc): rating = m_botvalue (5000 for all sizes) × min(2, c),
        // where c measures how much the pickup would matter right now. Size is expressed through c (a mega is
        // 100 HP → c is huge at low health), not through the base value.
        float itemHealth = item.GetResource(ResourceType.Health);
        float itemArmor = item.GetResource(ResourceType.Armor);
        if (itemHealth > 0f || itemArmor > 0f || Mentions(name, "health") || Mentions(name, "armor"))
        {
            const float baseValue = 5000f; // QC ATTRIB(Health/Armor, m_botvalue, 5000)
            float c = 0f;
            float health = System.MathF.Max(0f, bot.Health);
            float armor = bot.GetResource(ResourceType.Armor);
            // QC gates each resource on the item's own pickup cap (item.max_armorvalue / item.max_health);
            // the port items don't carry those, so gate on the bot's resource limits (equal for the common
            // items; only mega-overheal differs, and its huge c at low health dominates anyway).
            if (itemArmor > 0f && armor < Resources.GetResourceLimit(bot, ResourceType.Armor))
                c = itemArmor / System.MathF.Max(1f, armor * (2f / 3f) + health * (1f / 3f));
            // Gate on the RESOURCE LIMIT (200), not Player.MaxHealth. QC compares against the ITEM's own
            // max_health, which is 200 for every stock health item (balance-xonotic.cfg), while MaxHealth is
            // the 100 spawn value — so this read `health < 100` and a topped-up bot rated EVERY health item 0
            // (GoalRater.Rate early-returns on <= 0, so mega health was not even a candidate). The armor arm
            // above already used the limit; this is the asymmetry, not a deliberate choice.
            if (itemHealth > 0f && health < Resources.GetResourceLimit(bot, ResourceType.Health))
                c = itemHealth / System.MathF.Max(1f, health);
            if (c <= 0f && itemHealth <= 0f && itemArmor <= 0f)
                c = 0.5f; // name-matched but resource-less item entity: modest fallback pull
            float value = baseValue * System.MathF.Min(2f, c);
            // [PORT IMPROVEMENT — Duel item denial; Base bots only rate items they need] In Duel, controlling
            // the big stack items (mega health / big+mega armor, ≥50) IS the game: taking them when topped up
            // denies the opponent the resource. High-skill duel bots keep a LOW-rating floor on them so they
            // sweep the majors between fights (rangebias 2000 keeps it to nearby ones) without outranking real
            // needs, enemies, or a genuine low-health detour.
            if (brain.GameType is Duel && brain.Skill >= 7f && (itemHealth >= 50f || itemArmor >= 50f))
                value = System.MathF.Max(value, 2500f); // BOT_PICKUP_RATING_LOW
            return value;
        }

        // Weapon pickup (QC weapon_pickupevalfunc, items.qc:887-907): an unowned weapon returns its own
        // bot_pickupbasevalue (per-weapon "rating" ATTRIB, 0-10000) discounted by how stacked the bot's
        // arsenal already is (c = 1 - bound(0, Σowned/20000, 1)·0.5); an owned one is only worth its ammo
        // (QC falls through to ammo_pickupevalfunc).
        if (!string.IsNullOrEmpty(item.NetName) && Weapons.ByName(item.NetName) is Weapon wpn)
        {
            if (!bot.HasWeapon(item.NetName))
            {
                EnsureArsenal(bot, ref arsenal);
                float c = 1f - System.Math.Clamp(arsenal.Value / 20000f, 0f, 1f) * 0.5f;
                return wpn.BotPickupBaseValue * c;
            }
            // Owned (QC ammo_pickupevalfunc, weapon-pickup branch): the weapon's ammo value scaled by need,
            // plus 10% of the weapon's own base. An ammoless weapon (RES_NONE → no ammo item) rates 0.
            if (wpn.AmmoType == ResourceType.None)
                return 0f;
            float ammoCap = Resources.GetResourceLimit(bot, wpn.AmmoType);
            float botAmmo = bot.GetResource(wpn.AmmoType);
            float itemAmmo = item.GetResource(wpn.AmmoType);
            float need = (itemAmmo > 0f && botAmmo < ammoCap)
                ? itemAmmo / System.MathF.Max(0.5f, botAmmo) // QC noammorating = 0.5
                : 0f;
            return AmmoBotValue(wpn.AmmoType) * System.MathF.Min(need, 2f) + wpn.BotPickupBaseValue * 0.1f;
        }

        // Ammo box (QC ammo_pickupevalfunc, plain-ammo branch): rated only when the bot OWNS a weapon that
        // feeds on this resource (QC: item_resource stays NULL otherwise → rating 0), then the ammo def's
        // m_botvalue × a 0..2 need factor.
        for (int i = 0; i < AmmoResources.Length; i++)
        {
            ResourceType ammo = AmmoResources[i];
            float amt = item.GetResource(ammo);
            if (amt <= 0f) continue;
            EnsureArsenal(bot, ref arsenal);
            if ((arsenal.OwnedAmmoMask & (1 << i)) == 0)
                return 0f;
            float cap = Resources.GetResourceLimit(bot, ammo);
            float botAmt = bot.GetResource(ammo);
            float c = (botAmt < cap) ? amt / System.MathF.Max(0.5f, botAmt) : 0f;
            return AmmoBotValue(ammo) * System.MathF.Min(c, 2f);
        }

        // Powerup / generic pickup (QC generic_pickupevalfunc): m_botvalue directly — Powerup 11000,
        // Jetpack/FuelRegen 3000.
        int botValue = item.Pickup?.ItemDef.BotValue ?? 0;
        if (botValue > 0)
            return botValue;

        // Legacy name-match fallback for items not yet carrying a Pickup ref: the glowing powerups rate at
        // the QC powerup value. BUFFS are NOT powerups — QC sv_buffs.qc:459-461 gives a buff relic
        // generic_pickupevalfunc with bot_pickupbasevalue 1000, so rating it 11000 made bots abandon a needed
        // mega health to chase every relic.
        if (Mentions(name, "powerup") || Mentions(name, "strength") || Mentions(name, "shield")
            || Mentions(name, "invincible"))
            return 11000f;
        if (Mentions(name, "buff"))
            return 1000f;
        // QC generic_pickupevalfunc returns bot_pickupbasevalue, which is 0 for anything without an explicit
        // m_botvalue — NOT a LOW floor. A blanket 2500 gave every unrecognized entity that happens to carry
        // EntFlags.Item (objective flags/keys, the keepaway ball) a phantom item-goal pull.
        return 0f;
    }

    private static readonly ResourceType[] AmmoResources =
    {
        ResourceType.Shells, ResourceType.Bullets, ResourceType.Rockets, ResourceType.Cells, ResourceType.Fuel,
    };

    /// <summary>QC the ammo item defs' <c>m_botvalue</c> ATTRIBs (common/items/item/ammo.qh: Shells 1000,
    /// Bullets/Rockets/Cells 1500, Fuel 2000).</summary>
    private static float AmmoBotValue(ResourceType res) => res switch
    {
        ResourceType.Shells => 1000f,
        ResourceType.Bullets => 1500f,
        ResourceType.Rockets => 1500f,
        ResourceType.Cells => 1500f,
        ResourceType.Fuel => 2000f,
        _ => 0f,
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
        // NOT buffs: QC gates the respawn-camp lead and the jitter exemption on itemdef.instanceOfPowerup,
        // which is false for a buff relic (its own itemdef family). See ItemValue's buff arm.
        string name = string.IsNullOrEmpty(item.NetName) ? item.ClassName : item.NetName;
        return Mentions(name, "powerup") || Mentions(name, "strength") || Mentions(name, "shield")
            || Mentions(name, "invincible");
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

/// <summary>
/// QC <c>HAVOCBOT_CTF_ROLE_*</c> (sv_ctf.qh): the six CTF bot sub-roles the QC state machine cycles through
/// (values kept for log familiarity). <see cref="None"/> triggers the QC reset_role position balancing on
/// the first <see cref="BotObjectiveRoles.RoleCtf"/> invocation.
/// </summary>
public enum CtfBotRole
{
    None      = 0,
    Defense   = 2,  // havocbot_role_ctf_defense   — guard our base (timeout 30 s → reset)
    Middle    = 4,  // havocbot_role_ctf_middle    — hold the map middle (timeout 10 s → reset)
    Offense   = 8,  // havocbot_role_ctf_offense   — push the enemy base (timeout 120 s → reset)
    Carrier   = 16, // havocbot_role_ctf_carrier   — bring the enemy flag home (no timeout)
    Retriever = 32, // havocbot_role_ctf_retriever — TEMPORARY: return our stolen flag (timeout ~10-20 s → previous)
    Escort    = 64, // havocbot_role_ctf_escort    — TEMPORARY: follow our flag carrier (timeout 30-90 s → previous)
}

/// <summary>
/// QC Freeze Tag bot roles (sv_freezetag.qc havocbot_role_ft_offense / havocbot_role_ft_freeing): the two
/// roles alternate on 20-30 s timeouts; offense also flips to freeing when it is the last unfrozen teammate.
/// </summary>
public enum FtBotRole
{
    None = 0, // unassigned → first call picks randomly (QC HavocBot_ChooseRole ft)
    Offense,  // fight (items 12000 / enemies 10000 / free 9000)
    Freeing,  // thaw teammates (free 20000 / items 10000 / enemies 5000)
}
