// Shared ball-entity framework — the Godot-free essence of the QuakeC ball lifecycle reused by Keepaway,
// Team Keepaway, and Nexball (common/gametypes/gametype/keepaway/sv_keepaway.qc:ka_SpawnBalls/ka_RespawnBall/
// ka_TouchEvent/ka_DropEvent, and common/gametypes/gametype/nexball/sv_nexball.qc:SpawnBall/InitBall/
// ResetBall/GiveBall/DropBall).
//
// THE GAP THIS CLOSES (Wave-1 seam "ball-frame"): in the port, every gametype already has a SpawnBall method
// that builds the world ball, but NOTHING calls it for the procedural modes — QC spawned the Keepaway ball
// from a per-frame handler (ka_Handler_CheckBall/ka_SpawnBalls), NOT a map entity, so the map-entity objective
// spawner never triggers it. The result is that BallEntity stays null in a live match, no touch is ever
// registered, and no ball can be picked up. This file provides the host-side procedural spawner + the shared
// glide/idle-reset think, touch/carry/drop helpers, and a random-location relocate, exposed as a clear static
// API (BallEntity.SpawnForGametype / Relocate / DropOffsetVelocity / CarryOrbit). The round-drive seam
// (GameWorld) calls SpawnForGametype once the map has loaded and the match is live; the Wave-2 gametype agents
// configure the ball (model/bbox/effects/respawn-time and their own touch/think callbacks) via BallConfig.
//
// This is the simulation half only — the ball model/glow_trail/EF_DIMLIGHT, waypoint sprites, mod-icon, and
// respawn/spark particles are presentation concerns owned by the client (Wave 3), and are explicitly deferred.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>Which kind of ball the gametype wants (selects the QC defaults — model, bbox, movetype).</summary>
public enum BallKind
{
    /// <summary>QC keepawayball (Keepaway / Team Keepaway): a single procedural ball, bbox ±24, EF_DIMLIGHT,
    /// MOVETYPE_BOUNCE, relocates to a random map location on a respawn timer.</summary>
    KeepawayBall,

    /// <summary>QC nexball_basketball: a carry-able ball, bbox ±16, glide-resets to its spawn origin.</summary>
    NexballBasketball,

    /// <summary>QC nexball_football: a kickable (not carried) ball, bbox ±16, glide-resets to its spawn origin.</summary>
    NexballFootball,
}

/// <summary>
/// The per-spawn tunables + callbacks a gametype hands to <see cref="BallEntity.SpawnForGametype"/>. Defaults
/// match the QC for the chosen <see cref="BallKind"/>; a Wave-2 gametype agent overrides the bits it cares
/// about (its own <see cref="Touch"/>/<see cref="Think"/>, the relocate timer, the classname/model). All
/// fields are plain data so the framework stays Godot-free and headless-testable.
/// </summary>
public sealed class BallConfig
{
    /// <summary>QC classname of the spawned edict (keepawayball / nexball_basketball / nexball_football).
    /// Defaults from the kind; keep it stable since bot ball-finding queries by classname.</summary>
    public string ClassName = "";

    /// <summary>QC setmodel path (models/orbs/orbblue.md3 / models/nexball/ball.md3); "" = leave unset.</summary>
    public string Model = "";

    /// <summary>QC bbox (symmetric ±24 for KA, ±16 for NB). Set from the kind unless overridden.</summary>
    public Vector3 Mins;
    public Vector3 Maxs;

    /// <summary>QC e.effects (EF_DIMLIGHT=8 default for KA's loose ball / NB carry glow). 0 = none.</summary>
    public int Effects;

    /// <summary>QC e.glow_color (g_keepawayball_trail_color=254 / g_nexball_trail_color=254). 0 = no trail.</summary>
    public int TrailColor;

    /// <summary>QC e.damageforcescale (g_keepawayball_damageforcescale=2). 0 = engine default.</summary>
    public float DamageForceScale;

    /// <summary>QC this.bouncefactor for the MOVETYPE_BOUNCE ball (g_nexball_*_bouncefactor=0.6). 0 = engine
    /// default (0.5). Keepaway leaves it unset in QC, so it stays 0 -> engine default.</summary>
    public float BounceFactor;

    /// <summary>QC this.bouncestop (g_nexball_*_bouncestop=0.075). 0 = engine default (60/800).</summary>
    public float BounceStop;

    /// <summary>True iff the loose ball takes damage / is damaged-by-contents (KA: yes; NB: no).</summary>
    public bool TakesDamage;

    /// <summary>QC e.pushable (KA/NB both default 1) — the ball can be shoved by jumppads/explosions.</summary>
    public bool Pushable = true;

    /// <summary>Seconds a loose ball waits before relocating/resetting (QC g_keepawayball_respawntime=10 /
    /// g_nexball_delay_idle=10). &lt;= 0 disables the auto-respawn think.</summary>
    public float RespawnTime = 10f;

    /// <summary>
    /// True (Keepaway/TKA) = the loose-ball think RELOCATES the ball to a fresh random map location each cycle
    /// (QC ka_RespawnBall → MoveToRandomMapLocation). False (Nexball) = the ball glides back to its fixed
    /// spawn origin (QC ResetBall). Selects which built-in think <see cref="BallEntity.SpawnForGametype"/>
    /// installs when the gametype doesn't supply its own <see cref="Think"/>.
    /// </summary>
    public bool RelocateOnRespawn;

    /// <summary>The gametype's touch handler (QC settouch — ka_TouchEvent / basketball_touch). Required for a
    /// pick-up-able ball; null leaves the ball inert (a test stub).</summary>
    public EntityTouch? Touch;

    /// <summary>
    /// The gametype's own loose-ball think (QC setthink). When null, <see cref="BallEntity.SpawnForGametype"/>
    /// installs the shared built-in think (<see cref="BallEntity.RespawnThink"/>) driven by
    /// <see cref="RelocateOnRespawn"/> + <see cref="RespawnTime"/>. A gametype with a richer state machine
    /// (Nexball's 4-step ResetBall) supplies its own.
    /// </summary>
    public EntityThink? Think;

    /// <summary>Resolve the defaults for <paramref name="kind"/> in place (idempotent), then return self.</summary>
    public BallConfig WithKindDefaults(BallKind kind)
    {
        switch (kind)
        {
            case BallKind.KeepawayBall:
                if (ClassName == "") ClassName = "keepawayball";
                if (Mins == default && Maxs == default) { Mins = new Vector3(-24f, -24f, -24f); Maxs = new Vector3(24f, 24f, 24f); }
                if (Model == "") Model = "models/orbs/orbblue.md3";
                if (Effects == 0) Effects = BallEntity.EfDimLight;
                if (TrailColor == 0) TrailColor = 254;
                if (DamageForceScale == 0f) DamageForceScale = 2f;
                TakesDamage = true;
                RelocateOnRespawn = true;
                if (RespawnTime == 0f) RespawnTime = 10f;
                break;
            case BallKind.NexballBasketball:
            case BallKind.NexballFootball:
                if (ClassName == "") ClassName = kind == BallKind.NexballBasketball ? "nexball_basketball" : "nexball_football";
                if (Mins == default && Maxs == default) { Mins = new Vector3(-16f, -16f, -16f); Maxs = new Vector3(16f, 16f, 16f); }
                if (Model == "") Model = "models/nexball/ball.md3";
                if (TrailColor == 0) TrailColor = 254;
                // QC nexball_setstatus: this.bouncefactor/bouncestop = g_nexball_{basketball,football}_bounce*
                // (both kinds default 0.6 / 0.075). The engine MOVETYPE_BOUNCE integrator reads these off the edict.
                if (BounceFactor == 0f) BounceFactor = 0.6f;
                if (BounceStop == 0f) BounceStop = 0.075f;
                TakesDamage = false;
                RelocateOnRespawn = false;
                if (RespawnTime == 0f) RespawnTime = 10f; // QC g_nexball_delay_idle
                break;
        }
        return this;
    }
}

/// <summary>
/// The shared ball lifecycle (QC ka_SpawnBalls / ka_RespawnBall / ka_TouchEvent / ka_DropEvent and the Nexball
/// SpawnBall / ResetBall / GiveBall / DropBall). Keepaway, Team Keepaway, and Nexball all consume this:
///
///  - <see cref="SpawnForGametype"/> — the host-side procedural spawner the round-drive seam calls (replaces
///    QC's per-frame ka_Handler / map-entity spawn). It builds the world ball, applies the <see cref="BallConfig"/>,
///    wires touch/think, and relocates it to a starting position.
///  - <see cref="Relocate"/> — QC MoveToRandomMapLocation (with the SelectSpawnPoint fallback): move a loose
///    ball to a fresh random map location, re-arm the respawn timer, bounce it up.
///  - <see cref="GlideHome"/> — QC ResetBall: send a loose ball back to its spawn origin.
///  - <see cref="DropOffsetVelocity"/> / <see cref="CarryOrbit"/> — the QC drop scatter + carried-ball orbit anim.
///
/// All methods are static + Godot-free; the world entity is created through the <see cref="Api"/> facade exactly
/// like <see cref="GametypeEntities.SpawnObjective"/>, and degrade to no-ops when no facade is wired (headless).
/// </summary>
public static class BallEntity
{
    /// <summary>QC EF_DIMLIGHT (effects bit 8) — the loose Keepaway ball / carried Nexball glow.</summary>
    public const int EfDimLight = 8;

    /// <summary>QC ka_DropEvent: the 0.5 s self-recapture lockout after a drop (also used by thrown weapons).</summary>
    public const float SelfRecaptureLockout = 0.5f;

    // QC ka_BallThink_Carried orbit anim constants (#define BALL_XYSPEED 100 / BALL_XYDIST 24).
    public const float BallOrbitSpeed = 100f;
    public const float BallOrbitDist = 24f;

    /// <summary>QC ka_RespawnBall: the upward kick a relocated/reset ball is given ('0 0 200').</summary>
    public static readonly Vector3 RespawnVelocity = new(0f, 0f, 200f);

    /// <summary>
    /// QC ka_SpawnBalls / Nexball SpawnBall — the HOST-SIDE PROCEDURAL SPAWNER the round-drive seam calls.
    /// Create the world ball edict for <paramref name="kind"/> at <paramref name="origin"/>, apply
    /// <paramref name="config"/> (defaulted to the kind's QC values), wire the gametype's touch/think (or the
    /// built-in respawn think), and place it. For a relocating ball (Keepaway) the spawn immediately relocates
    /// it to a random map location like QC's ka_RespawnBall; for a fixed-home ball (Nexball) it stays at
    /// <paramref name="origin"/> and records it as the reset home.
    ///
    /// Returns the new entity, or null when no facade is wired (headless tests that don't exercise the entity
    /// layer fall back to the gametype's plain carrier state). The gametype stores the returned entity as its
    /// <c>BallEntity</c> and reads <see cref="Entity.GtSpawnOrigin"/> as the QC <c>spawnorigin</c>.
    /// </summary>
    /// <param name="origin">Initial / home origin (QC spawnorigin). A relocating ball moves off it at once.</param>
    /// <param name="config">Tunables + callbacks; null = the kind's QC defaults.</param>
    public static Entity? SpawnForGametype(BallKind kind, Vector3 origin, BallConfig? config = null)
    {
        BallConfig cfg = (config ?? new BallConfig()).WithKindDefaults(kind);

        if (Api.Services is null)
            return null;

        Entity e = Api.Entities.Spawn();
        e.ClassName = cfg.ClassName;
        e.Flags = EntFlags.Item | EntFlags.NoTarget; // QC FL_ITEM (+ NOTARGET so it isn't a bot/turret target)
        e.Team = Teams.None;
        e.GtHomeTeam = Teams.None;
        e.Solid = Solid.Trigger;                     // QC SOLID_TRIGGER before setsize (area-grid linking)
        GametypeEntities.SetSize(e, cfg.Mins, cfg.Maxs);
        e.MoveType = MoveType.Bounce;                // QC MOVETYPE_BOUNCE
        e.BounceFactor = cfg.BounceFactor;           // QC this.bouncefactor (0 -> engine default 0.5)
        e.BounceStop = cfg.BounceStop;               // QC this.bouncestop  (0 -> engine default 60/800)
        e.TakeDamage = cfg.TakesDamage ? DamageMode.Yes : DamageMode.No;
        e.DamageForceScale = cfg.DamageForceScale;   // QC e.damageforcescale (g_keepawayball_damageforcescale)
        e.Effects = cfg.Effects;                     // QC e.effects (EF_DIMLIGHT); glow_trail/glow_color are client
        if (!string.IsNullOrEmpty(cfg.Model))
            Api.Entities.SetModel(e, cfg.Model);
        GametypeEntities.SetOrigin(e, origin);
        e.GtSpawnOrigin = origin;                    // QC spawnorigin (Nexball ResetBall returns here)
        e.GtSpawnAngles = e.Angles;
        e.Touch = cfg.Touch;
        e.Think = cfg.Think ?? RespawnThink;
        // Stash the config on the edict so the built-in RespawnThink can read its timer/mode (gametypes that
        // supply their own Think ignore this).
        _configs.AddOrUpdate(e, cfg);

        if (cfg.RelocateOnRespawn)
        {
            // QC ka_SpawnBalls finishes by calling ka_RespawnBall(e): relocate to a random spot + arm the timer.
            Relocate(e, cfg.RespawnTime);
        }
        else
        {
            // QC Nexball SpawnBall: stay at the spawn origin; arm the idle think if a respawn time was given.
            if (cfg.RespawnTime > 0f && cfg.Think is null)
                e.NextThink = GametypeEntities.Now + cfg.RespawnTime;
        }
        return e;
    }

    // Per-ball config side table (so the shared RespawnThink can read the timer/mode without a field on Entity;
    // mirrors the way the gametypes keep their own ball reference). Small — at most a handful of balls live.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Entity, BallConfig> _configs = new();

    /// <summary>
    /// QC ka_RespawnBall (think): relocate a loose ball to a fresh RANDOM map location, re-arm the respawn
    /// timer, and bounce it upward. Mirrors QC's MoveToRandomMapLocation with the SelectSpawnPoint fallback
    /// (here: a random spawnpoint / a small jitter around the home origin when none exist). No-op while the
    /// ball is carried (a carried ball is non-solid with a different think).
    /// </summary>
    /// <param name="respawnTime">Seconds until the next relocate (the timer is re-armed); &lt;= 0 leaves it loose.</param>
    public static void Relocate(Entity ball, float respawnTime)
    {
        if (ball.GtCarrier is not null)
            return;

        Vector3 dest = RandomMapLocation(ball.GtSpawnOrigin);
        GametypeEntities.SetOrigin(ball, dest);

        ball.Solid = Solid.Trigger;
        ball.MoveType = MoveType.Bounce;
        ball.Velocity = RespawnVelocity;             // QC this.velocity = '0 0 200'
        ball.Angles = Vector3.Zero;                  // QC this.angles = '0 0 0'

        if (respawnTime > 0f)
            ball.NextThink = GametypeEntities.Now + respawnTime; // QC nextthink = time + respawntime
        else
            ball.NextThink = 0f;
    }

    /// <summary>
    /// QC Nexball ResetBall (collapsed): send a loose ball back to its fixed spawn home (<see cref="Entity.GtSpawnOrigin"/>),
    /// stop it, and re-arm the idle think. Used by the non-relocating ball kinds (Nexball football/basketball).
    /// The 4-step glide-back state machine (cnt 2→4) is a Nexball-specific refinement layered on top by the
    /// Nexball gametype; this is the simple "home + stop" baseline the framework guarantees.
    /// </summary>
    public static void GlideHome(Entity ball, float idleTime)
    {
        if (ball.GtCarrier is not null)
            return;
        GametypeEntities.SetOrigin(ball, ball.GtSpawnOrigin);
        ball.Velocity = Vector3.Zero;
        ball.Solid = Solid.Trigger;
        ball.MoveType = MoveType.Bounce;
        ball.NextThink = idleTime > 0f ? GametypeEntities.Now + idleTime : 0f;
    }

    /// <summary>
    /// The built-in loose-ball think installed when a gametype supplies no <see cref="BallConfig.Think"/>: per
    /// the ball's stored config, either <see cref="Relocate"/> (Keepaway) or <see cref="GlideHome"/> (Nexball),
    /// re-arming itself on its respawn timer. Safe to call on a carried ball (early-returns).
    /// </summary>
    public static void RespawnThink(Entity ball)
    {
        if (ball.GtCarrier is not null)
            return;
        float respawnTime = 10f;
        bool relocate = true;
        if (_configs.TryGetValue(ball, out BallConfig? cfg) && cfg is not null)
        {
            respawnTime = cfg.RespawnTime;
            relocate = cfg.RelocateOnRespawn;
        }
        if (relocate) Relocate(ball, respawnTime);
        else GlideHome(ball, respawnTime);
    }

    /// <summary>
    /// Attach a ball to its carrier (QC ka_TouchEvent / Nexball GiveBall pickup core): make it non-solid, stop
    /// it, set the carry back-links, and ride the carrier at <paramref name="carryOffset"/>. Thin wrapper over
    /// <see cref="GametypeEntities.AttachToCarrier"/> that also clears the loose-ball respawn think and removes
    /// damage-taking while carried. The scoring / VIP / notification side-effects stay with the gametype.
    /// </summary>
    public static void AttachToCarrier(Entity ball, Entity carrier, Vector3 carryOffset = default)
    {
        GametypeEntities.AttachToCarrier(ball, carrier, carryOffset);
        ball.NextThink = 0f;                 // QC: the carried-ball think is the gametype's, not the loose timer
        ball.TakeDamage = DamageMode.No;     // QC takedamage = DAMAGE_NO while carried
        // QC GameRules_scoring_vip(carrier, true): KA (sv_keepaway.qc:149), NB (sv_nexball.qc:170), TKA
        // (sv_tka.qc:139) all flag the ball carrier as a VIP (drives the nades flagcarrier bonus rate).
        GametypeEntities.ScoringVip(carrier, true);
    }

    /// <summary>
    /// Detach a ball from its carrier and drop it at the carrier's feet (QC ka_DropEvent / Nexball DropBall
    /// core): restore solid/bounce, re-enable damage iff the kind takes it, place it just above the carrier,
    /// arm the self-recapture lockout, and re-arm the respawn timer. Returns the carrier that lost it (or null).
    /// Scatter velocity is supplied by <see cref="DropOffsetVelocity"/>; the gametype keeps the sounds/notifs.
    /// </summary>
    /// <param name="respawnTime">Seconds until the loose ball relocates/resets; &lt;= 0 leaves it loose.</param>
    /// <param name="takesDamage">Whether the dropped ball is damageable again (KA: true; NB: false).</param>
    public static Entity? DropFromCarrier(Entity ball, float respawnTime, bool takesDamage = true)
    {
        Entity? carrier = ball.GtCarrier;
        GametypeEntities.DetachFromCarrier(ball);
        // QC GameRules_scoring_vip(player, false) on drop/score (sv_keepaway.qc:187, sv_nexball.qc:147/230).
        GametypeEntities.ScoringVip(carrier, false);

        ball.Solid = Solid.Trigger;          // QC SOLID_TRIGGER before setorigin (area-grid linking)
        ball.MoveType = MoveType.Bounce;     // QC MOVETYPE_BOUNCE
        ball.TakeDamage = takesDamage ? DamageMode.Yes : DamageMode.No;

        Vector3 dropOrigin = carrier is not null ? carrier.Origin + new Vector3(0f, 0f, 10f) : ball.Origin;
        GametypeEntities.SetOrigin(ball, dropOrigin); // QC setorigin(ball, player.origin + ... + '0 0 10')
        ball.Velocity = DropOffsetVelocity();         // QC '0 0 200' + crandom scatter

        // QC ka_DropEvent: previous_owner + wait = time + 0.5 (self-recapture lockout). We track these on the
        // edict so the gametype's touch can honor the lockout (Entity.GtNextTakeTime / GtCarrier-was).
        ball.GtNextTakeTime = GametypeEntities.Now + SelfRecaptureLockout;
        ball.Owner = carrier; // QC ball.previous_owner = player (used for the self-recapture lockout check)

        ball.Think = RespawnThink;
        ball.NextThink = respawnTime > 0f ? GametypeEntities.Now + respawnTime : 0f;
        return carrier;
    }

    /// <summary>
    /// QC ka_DropEvent drop scatter: <c>'0 0 200' + '0 100 0'*crandom() + '100 0 0'*crandom()</c> — an upward
    /// kick plus a deterministic horizontal jitter so a dropped ball doesn't land exactly underfoot.
    /// </summary>
    public static Vector3 DropOffsetVelocity()
        => RespawnVelocity
           + new Vector3(0f, 100f, 0f) * Prandom.Signed()
           + new Vector3(100f, 0f, 0f) * Prandom.Signed();

    /// <summary>
    /// QC ka_BallThink_Carried orbit anim: the position a carried ball should sit at, orbiting the carrier in
    /// the xy plane (radius <see cref="BallOrbitDist"/>, angular rate <see cref="BallOrbitSpeed"/>), keeping the
    /// carrier's z. <paramref name="cnt"/>/<paramref name="chainCount"/> spread multiple balls around the orbit
    /// (single ball ⇒ cnt=chainCount=1). <paramref name="time"/> is sim time (QC <c>time</c>).
    /// </summary>
    public static Vector3 CarryOrbit(Vector3 carrierOrigin, float time, int cnt = 1, int chainCount = 1)
    {
        if (chainCount <= 0) chainCount = 1;
        // QC makevectors(vec3(0, (360*cnt/chainCount) + (time%360)*BALL_XYSPEED, 0)) → v_forward in the xy plane.
        float yawDeg = (360f * cnt / chainCount) + (time % 360f) * BallOrbitSpeed;
        float yaw = yawDeg * (QMath.Pi / 180f);
        // v_forward for a pure-yaw rotation: (cos yaw, sin yaw, 0).
        float fx = MathF.Cos(yaw);
        float fy = MathF.Sin(yaw);
        return new Vector3(carrierOrigin.X + fx * BallOrbitDist, carrierOrigin.Y + fy * BallOrbitDist, carrierOrigin.Z);
    }

    /// <summary>
    /// QC ka_DropEvent self-recapture lockout test: true iff <paramref name="toucher"/> is the player who just
    /// dropped this ball and the 0.5 s lockout (<see cref="Entity.GtNextTakeTime"/>) hasn't expired — in which
    /// case the gametype's touch must IGNORE the pickup (so a carrier who drops can't instantly re-grab it).
    /// </summary>
    public static bool IsRecaptureLocked(Entity ball, Entity toucher)
        => ball.GtNextTakeTime > GametypeEntities.Now && ReferenceEquals(ball.Owner, toucher);

    // ============================================================================================
    //  Random map location (QC MoveToRandomMapLocation, with the SelectSpawnPoint fallback)
    // ============================================================================================

    private static readonly List<Entity> _spawnScratch = new();

    /// <summary>
    /// QC <c>MoveToRandomMapLocation</c> (with the <c>SelectSpawnPoint</c> fallback): pick a pseudo-random
    /// reachable spot on the map for a relocated ball. The engine builtin sampled random points in the world
    /// bounds and rejected ones in solid/lava/sky; lacking the headless world-bounds sampler here we use the
    /// faithful fallback path QC itself drops to — a random player spawnpoint — and, when the map has none
    /// (tests), a deterministic jitter around the ball's home origin. Deterministic via <see cref="Prandom"/>
    /// so the predicting client reproduces it.
    /// </summary>
    public static Vector3 RandomMapLocation(Vector3 fallbackOrigin)
    {
        if (Api.Services is not null)
        {
            // Gather every spawnpoint classname (info_player_deathmatch / _start / _teamN) and pick one at random.
            Entity? pick = null;
            int seen = 0;
            foreach (string cls in SpawnPointClassNames)
            {
                Api.Entities.FindByClass(cls, _spawnScratch);
                for (int i = 0; i < _spawnScratch.Count; i++)
                {
                    Entity sp = _spawnScratch[i];
                    if (sp.IsFreed) continue;
                    // Reservoir sampling so we make ONE uniform pick across all classnames without a combined list.
                    seen++;
                    if (Prandom.RangeInt(0, seen) == 0)
                        pick = sp;
                }
            }
            if (pick is not null)
                return pick.Origin;
        }

        // No spawnpoints (or no facade): jitter around the home origin so repeated relocates still move it.
        return fallbackOrigin + new Vector3(Prandom.Signed() * 256f, Prandom.Signed() * 256f, 16f);
    }

    /// <summary>The QC spawnpoint classnames a relocated ball samples (mirrors SpawnSystem.SpawnClassNames).</summary>
    public static readonly string[] SpawnPointClassNames =
    {
        "info_player_deathmatch",
        "info_player_start",
        "info_player_team1",
        "info_player_team2",
        "info_player_team3",
        "info_player_team4",
    };
}
