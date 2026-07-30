// Port of qcsrc/common/mapobjects/trigger/teleport.qc + teleporters.qc + misc/teleport_dest.qc.
//
// A trigger_teleport is a touch volume that relocates the toucher to a teleport destination
// (info_teleport_destination / misc_teleporter_dest), reorienting it to the destination's angles and
// (optionally) reprojecting its speed along the new facing. Telefragging anyone already standing at the
// destination is part of the QC TeleportPlayer path.
//
// Ported in full: destination lookup by targetname, multi-destination weighted-random selection with
// telefrag-avoid priority (RandomSelection), the relocation (origin/angles/velocity), the speed handling
// (KEEP_SPEED vs reproject along facing, optional .speed clamp on the dest), the exact-box telefrag with
// teamplay gating + self-gib, teamplay teleporter ownership, and the teleport sound/effect debounce.
// Genuinely out of scope for the headless sim: warpzones, grappling-hook removal, bot waypoint spawning,
// the per-player min/max-speed STAT clamps, and CSQC effect/networking.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary><c>trigger_teleport</c> + the teleport destination entities. Each setup is a spawnfunc.</summary>
public static class Teleporters
{
    // teleport.qh spawnflag bits.
    public const int ObserversOnly = 1 << 0; // TELEPORT_OBSERVERS_ONLY
    public const int KeepSpeed = 1 << 1;     // TELEPORT_KEEP_SPEED — only skips the g_teleport_maxspeed clamp, NOT direction
    public const int InvertTeams = 1 << 2;   // INVERT_TEAMS (defs.qh) — flip the team-ownership gate

    // teleporters.qh TELEPORT_FLAG_* bits (which side effects a teleport produces). A map teleporter passes
    // TELEPORT_FLAGS_TELEPORTER (sound|particles|tdeath); a Porto portal passes TELEPORT_FLAGS_PORTAL (+force_tdeath).
    public const int FlagSound = 1 << 0;       // TELEPORT_FLAG_SOUND
    public const int FlagParticles = 1 << 1;   // TELEPORT_FLAG_PARTICLES
    public const int FlagTdeath = 1 << 2;      // TELEPORT_FLAG_TDEATH
    public const int FlagForceTdeath = 1 << 3; // TELEPORT_FLAG_FORCE_TDEATH (telefrag even with g_telefrags 0)
    public const int FlagsTeleporter = FlagSound | FlagParticles | FlagTdeath; // TELEPORT_FLAGS_TELEPORTER
    public const int FlagsPortal = FlagsTeleporter | FlagForceTdeath;          // TELEPORT_FLAGS_PORTAL

    /// <summary>QC nudge applied to a destination origin so the player hull clears the floor: 1 - mins.z - 24.</summary>
    public const float DestZNudge = 1f - (-24f) - 24f; // = 1 (PL_MIN.z = -24)

    /// <summary>
    /// QC <c>WarpZone_PostTeleportPlayer_Callback</c> tail (<c>teleporters.qc:310</c>): the server-only
    /// <c>anticheat_fixangle(pl)</c> call fired on a real, server-side (non-predicted) player teleport. The
    /// teleport view-snap forcibly sets the player's facing, so the anticheat opens its snap-aim suppression
    /// window — otherwise a legit teleport-induced view snap inflates the strafebot_new / idle_snapaim signal.
    /// The server (GameWorld) wires this to <c>AntiCheat.FixAngle</c>; left null (and inert) on a pure client.
    /// </summary>
    public static Action<Entity>? OnPlayerFixAngle;

    /// <summary><c>spawnfunc(trigger_teleport)</c>.</summary>
    public static void TeleportSetup(Entity this_)
    {
        this_.Angles = Vector3.Zero;
        this_.Active = MapMover.ActiveActive;
        MapMover.InitTrigger(this_);
        this_.ClassName = "trigger_teleport";
        this_.Use = TeleportUse;

        MapMover.IndexRegister(this_);

        if (string.IsNullOrEmpty(this_.Target))
        {
            // QC objerrors here; headless we leave the touch unset so it's inert.
            return;
        }

        // Resolve the (single) destination now if there's exactly one; otherwise pick at teleport time.
        FindTarget(this_);
    }

    /// <summary><c>spawnfunc(info_teleport_destination)</c> / <c>misc_teleporter_dest</c> — a teleport endpoint.</summary>
    public static void TeleportDestSetup(Entity this_)
    {
        this_.MAngle = this_.Angles; // destinations store their facing in mangle (angles cleared)
        this_.Angles = Vector3.Zero;
        this_.ClassName = "info_teleport_destination";
        MapMover.SetOrigin(this_, this_.Origin);
        MapMover.IndexRegister(this_); // so teleporters can find it by targetname
    }

    /// <summary>
    /// <c>spawnfunc(target_teleporter)</c> — a use-activated teleporter (no touch volume). QC defers the actual
    /// setup to <c>target_teleporter_checktarget</c> at INITPRIO_FINDTARGET (after every entity has spawned), so
    /// it can disambiguate the three ways a target_teleporter is used (destination / self-target teleporter /
    /// normal teleporter). Here we register the entity now (so other teleporters can find it by targetname) and
    /// queue the checktarget pass for <see cref="RunDeferredInit"/>.
    /// </summary>
    public static void TargetTeleporterSetup(Entity this_)
    {
        this_.ClassName = "target_teleporter";
        MapMover.IndexRegister(this_);
        if (!_pendingTargetTeleporter.Contains(this_))
            _pendingTargetTeleporter.Add(this_);
    }

    /// <summary>INITPRIO_FINDTARGET queue for <c>target_teleporter_checktarget</c> (drained by RunDeferredInit).</summary>
    private static readonly List<Entity> _pendingTargetTeleporter = new();

    /// <summary>
    /// Drain the queued <c>target_teleporter</c> disambiguation (QC INITPRIO_FINDTARGET pass), run after the
    /// whole BSP entity lump has spawned so the "is anything targeting me?" lookup sees every teleporter.
    /// </summary>
    public static void RunDeferredInit()
    {
        if (_pendingTargetTeleporter.Count == 0)
            return;
        Entity[] batch = _pendingTargetTeleporter.ToArray();
        _pendingTargetTeleporter.Clear();
        foreach (Entity e in batch)
            if (!e.IsFreed)
                TargetTeleporterCheckTarget(e);
    }

    /// <summary>
    /// QC <c>target_teleporter_checktarget</c> (teleport.qc:142-185). A target_teleporter is used three ways and
    /// we must figure out which: (1) a target_teleporter with NO target that IS targeted by some teleporter is a
    /// teleport DESTINATION (re-route to info_teleport_destination); (2) a target_teleporter with no target that
    /// is NOT targeted by any teleporter is a self-target teleporter (<c>.enemy = this</c>, mangle = angles);
    /// (3) otherwise it is a normal use-activated teleporter that teleports its activator to its target.
    /// </summary>
    private static void TargetTeleporterCheckTarget(Entity this_)
    {
        if (string.IsNullOrEmpty(this_.Target))
        {
            // No target: either a destination (if a teleporter targets us) or a self-target teleporter.
            bool isTeleporterTarget = false;
            foreach (Entity it in MapMover.FindEntitiesTargeting(this_.TargetName))
            {
                if (it.ClassName == "trigger_teleport" || it.ClassName == "target_teleporter")
                {
                    isTeleporterTarget = true;
                    break;
                }
            }

            if (isTeleporterTarget)
            {
                // A teleporter targets this — it's a destination. QC: spawnfunc_info_teleport_destination(this).
                TeleportDestSetup(this_);
                return;
            }

            // Not targeted by any teleporter — set up as a self-target teleporter (you teleport onto yourself).
            this_.Enemy = this_;
            this_.MAngle = this_.Angles;
            this_.Angles = Vector3.Zero;
        }

        // Set this entity up as a use-activated teleporter.
        this_.Active = MapMover.ActiveActive;
        this_.Use = TargetTeleportUse;

        // QC: only call teleport_findtarget if it has a target (a self-target teleporter already has .enemy set).
        if (!string.IsNullOrEmpty(this_.Target))
            FindTarget(this_);
    }

    /// <summary>QC <c>teleport_findtarget</c> (headless core): cache the destination if there's exactly one.</summary>
    private static void FindTarget(Entity this_)
    {
        var dests = MapMover.FindByTargetName(this_.Target).ToList();
        if (dests.Count == 1)
            this_.Enemy = dests[0]; // exactly one — cache it
        else
            this_.Enemy = null;     // 0 or many — resolve per teleport

        if (this_.Solid == Solid.Trigger)
            this_.Touch = TeleportTouch;
    }

    /// <summary>
    /// QC the <c>teleporttotarget</c> cheat (<c>server/cheats.qc</c> CheatCommand case): teleport
    /// <paramref name="player"/> to the teleport destination named <paramref name="targetName"/>. Spawns a
    /// transient <c>cheattriggerteleport</c> (a non-trigger, so <c>teleport_findtarget</c> never wires a touch),
    /// resolves its target the way <c>teleport_findtarget</c> does (cache the single destination in <c>.Enemy</c>,
    /// or leave it null for multi / a random per-teleport pick), then runs <see cref="SimpleTeleportPlayer"/>.
    /// Returns true when a destination was found and the teleport ran (QC's <c>!wasfreed(ent)</c> gate — the QC
    /// <c>objerror</c> on a missing target frees the transient and skips the teleport).
    /// </summary>
    public static bool TeleportToTarget(Entity player, string? targetName)
    {
        if (Api.Services is null || string.IsNullOrEmpty(targetName))
            return false;

        Entity ent = Api.Entities.Spawn();
        ent.ClassName = "cheattriggerteleport";
        ent.Target = targetName;

        // QC teleport_findtarget: count destinations, cache the single one in .enemy (0 -> objerror+free, many -> random).
        var dests = MapMover.FindByTargetName(targetName).ToList();
        if (dests.Count == 0)
        {
            Api.Entities.Remove(ent); // QC objerror("Teleporter with nonexistent target") -> wasfreed(ent)
            return false;
        }
        ent.Enemy = dests.Count == 1 ? dests[0] : null;

        Entity? dest = SimpleTeleportPlayer(ent, player);
        Api.Entities.Remove(ent);
        return dest is not null;
    }

    /// <summary>QC <c>trigger_teleport_use</c>: teamplay claims the teleporter for the activator's team.</summary>
    private static void TeleportUse(Entity self, Entity actor)
    {
        // QC: if(teamplay) this.team = actor.team; SendFlags |= SF_TRIGGER_UPDATE; — a team-owned teleporter
        // only works for that team. The networking SendFlags is client-side; the team assignment (gameplay,
        // teamplay-gated as in Base) is ported here, and is now enforced by the team gate in TeleportActive.
        if (GameScores.Teamplay)
            self.Team = actor.Team;
    }

    /// <summary>QC <c>Teleport_Active</c>: gate on active state, deadness, and OBSERVERS_ONLY.</summary>
    private static bool TeleportActive(Entity self, Entity player)
    {
        if (self.Active != MapMover.ActiveActive)
            return false;
        if (MapMover.IsDead(player))
            return false;
        // QC teleport.qc:38-39 — a team-claimed teleporter (this.team set, see TeleportUse) only relocates
        // matching-team players; INVERT_TEAMS flips the sense. DIFF_TEAM honors the teamplay global.
        //   if(this.team) if(((spawnflags & INVERT_TEAMS) == 0) == DIFF_TEAM(this, player)) return false;
        if (self.Team != 0f)
        {
            bool diffTeam = GameScores.Teamplay
                ? self.Team != player.Team
                : !ReferenceEquals(self, player);
            if (((self.SpawnFlags & InvertTeams) == 0) == diffTeam)
                return false;
        }
        if (self.ClassName == "trigger_teleport" && (self.SpawnFlags & ObserversOnly) != 0)
            return false;
        // QC also checks player.teleportable / vehicle / turret — approximated by "is a creature".
        if (!MapMover.IsCreature(player))
            return false;
        return true;
    }

    /// <summary>QC <c>Teleport_Touch</c>: relocate the toucher to the destination, then fire targets.</summary>
    public static void TeleportTouch(Entity self, Entity toucher)
    {
        if (!TeleportActive(self, toucher))
            return;

        // QC teleport.qc:59-61 (#ifdef SVQC): a hooked player drops the grappling hook on teleport so the chain
        // doesn't stay latched through the portal. Server-only; the touch path is always authoritative here.
        if ((toucher.Flags & EntFlags.Client) != 0)
            Hook.RemoveGrapplingHooks(toucher);

        Entity? dest = SimpleTeleportPlayer(self, toucher);

        // Fire the teleporter's own targets (QC blanks .target first so the dest isn't re-fired here).
        string s = self.Target;
        self.Target = "";
        MapMover.UseTargets(self, toucher, toucher);
        self.Target = s;

        if (dest is not null)
            MapMover.UseTargets(dest, toucher, toucher);
    }

    /// <summary>QC <c>target_teleport_use</c>: the use-activated teleport path.</summary>
    private static void TargetTeleportUse(Entity self, Entity actor)
    {
        if (!TeleportActive(self, actor))
            return;

        // QC teleport.qc:82-83 target_teleport_use: drop the player's grappling hooks before relocating.
        if ((actor.Flags & EntFlags.Client) != 0)
            Hook.RemoveGrapplingHooks(actor);

        Entity? dest = SimpleTeleportPlayer(self, actor);

        string s = self.Target;
        self.Target = "";
        MapMover.UseTargets(self, actor, actor);
        self.Target = s;

        if (dest is not null && !ReferenceEquals(dest, self))
            MapMover.UseTargets(dest, actor, actor);
    }

    /// <summary>
    /// QC <c>Simple_TeleportPlayer</c>: find the output destination, optionally clamp/reproject the
    /// player's speed, place the player at the destination, telefrag occupants. Returns the destination.
    /// </summary>
    public static Entity? SimpleTeleportPlayer(Entity teleporter, Entity player, bool predicted = false)
    {
        // Client prediction can only follow a teleporter with a single cached destination (teleporter.Enemy):
        // a multi-destination teleporter picks via RandomSelection, which is non-deterministic across client and
        // server, so CSQC ("sorry CSQC, random stuff ain't gonna happen") — and we — skip predicting it.
        Entity? dest = teleporter.Enemy ?? (predicted ? null : PickDestination(teleporter, player));
        if (dest is null)
            return null;

        // destination facing
        QMath.AngleVectors(dest.MAngle, out Vector3 forward, out _, out _);

        // optional hard speed cap on the destination (.speed)
        if (dest.Speed != 0f && player.Velocity.Length() > dest.Speed)
            player.Velocity = QMath.Normalize(player.Velocity) * MathF.Max(0f, dest.Speed);

        // QC Simple_TeleportPlayer: the out-velocity is ALWAYS the player's current speed reprojected along the
        // destination's forward — makevectors(e.mangle); to_velocity = v_forward * vlen(player.velocity). The
        // KEEP_SPEED spawnflag does NOT preserve the entry direction: per entities.ent it only "ignores
        // g_teleport_maxspeed", i.e. it skips the TELEPORT_MIN/MAXSPEED magnitude clamp (those STAT clamps are
        // out of scope here and default to 0 in stock physics, so KEEP_SPEED is a no-op for now). Reprojecting
        // unconditionally is what stops a player exiting in their ENTRY direction instead of the dest facing.
        Vector3 outVel = forward * player.Velocity.Length();

        // QC: locout = dest.origin + '0 0 1' * (1 - player.mins.z - 24)  — clear the floor by the hull height.
        Vector3 locout = dest.Origin + new Vector3(0f, 0f, 1f - player.Mins.Z - 24f);

        // QC Simple_TeleportPlayer passes TELEPORT_FLAGS_TELEPORTER (sound|particles|tdeath); the flash/sound
        // are emitted INSIDE TeleportPlayer under the 0.2s pushltime debounce (so the particle is debounced too,
        // matching Base), not unconditionally here.
        TeleportPlayer(teleporter, player, locout, dest.MAngle, outVel, FlagsTeleporter, predicted);
        return dest;
    }

    /// <summary>
    /// QC <c>Simple_TeleportPlayer</c> destination pick: weighted-random over every destination with the
    /// teleporter's target, biased by each dest's <c>.cnt</c> weight and — to avoid telefragging the
    /// teleportee — by a priority of 1 for destinations that are currently clear vs 0 for occupied ones.
    /// </summary>
    private static Entity? PickDestination(Entity teleporter, Entity player)
    {
        var sel = new MapMover.RandomSelection();
        sel.Reset();
        // QC STAT(TELEPORT_TELEFRAG_AVOID, player) = autocvar_g_telefrags_avoid (teleporters.qc:198, registered
        // default 1 — Cvars.cs). Read it LIVE here so `set g_telefrags_avoid 0` genuinely disables the avoidance
        // (CvarOr honors an explicit 0 while still defaulting to 1 on the unregistered/test path).
        bool telefragAvoid = CvarOr("g_telefrags_avoid", 1f) != 0f;
        foreach (Entity e in MapMover.FindByTargetName(teleporter.Target))
        {
            float weight = e.Cnt != 0 ? e.Cnt : 1;
            float priority = 1f;
            // QC STAT(TELEPORT_TELEFRAG_AVOID): prefer a destination that won't telefrag the teleportee.
            if (telefragAvoid && (player.Flags & EntFlags.Client) != 0)
            {
                Vector3 locout = e.Origin + new Vector3(0f, 0f, DestZNudge);
                if (CheckTdeath(player, locout))
                    priority = 0f;
            }
            sel.Add(e, weight, priority);
        }
        return sel.Chosen;
    }

    /// <summary>
    /// QC <c>check_tdeath</c>: would placing <paramref name="player"/> at <paramref name="at"/> telefrag a
    /// live player there? Used by destination selection to prefer clear exits.
    /// </summary>
    private static bool CheckTdeath(Entity player, Vector3 at)
    {
        if (Api.Services is null)
            return false;
        if ((player.Flags & EntFlags.Client) == 0 || MapMover.IsDead(player))
            return false;

        // QC TDEATHLOOP → findbox(deathmin, deathmax) (upstream b6e02fe3): the old findradius radius —
        // max(vlen(deathmin), vlen(deathmax)) — was the box's distance from the WORLD ORIGIN, scanning a sphere
        // that grew with the exit's map position and relying on a per-entity boxesoverlap to trim it. The exact
        // box query replaces both (the precise AABB test lives inside FindInBox). Fresh list per call: this is
        // the teleport-event path (not per-frame), and no shared scratch can be re-entered from here.
        Vector3 deathMin = at + player.Mins, deathMax = at + player.Maxs;
        var candidates = new List<Entity>();
        Api.Entities.FindInBox(deathMin, deathMax, candidates);
        foreach (Entity head in candidates)
        {
            if (ReferenceEquals(head, player) || head.TakeDamage == DamageMode.No)
                continue;
            if ((head.Flags & EntFlags.Client) == 0 || MapMover.IsDead(head))
                continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// QC <c>TeleportPlayer</c>: debounce + play the teleport sound, relocate the player (origin/angles/
    /// velocity), clear ground, telefrag the destination occupants (gated on <c>g_telefrags</c> and not while
    /// a round is pre-start), then set the kill-credit pusher window and remember the entry origin.
    /// Genuinely out of scope: the CSQC particle effect, the warpzone post-teleport callback, projectile
    /// re-owning, and the bot aim reset.
    /// </summary>
    public static void TeleportPlayer(Entity teleporter, Entity player, Vector3 to, Vector3 toAngles, Vector3 toVelocity, int tflags = FlagsTeleporter, bool predicted = false)
    {
        Entity telefragger = teleporter.Owner ?? player;
        Vector3 from = player.Origin;

        // QC makevectors(to_angles) — the exit flash sits 32u ahead of the destination facing (teleporters.qc:101).
        QMath.AngleVectors(toAngles, out Vector3 toForward, out _, out _);

        // Effect/sound debounce (QC pushltime: one teleport effect per teleporter per 0.2s, players only, and never
        // during client prediction — the sound/particles are a server-authoritative #ifdef SVQC side effect). Both
        // the flash AND the sound live under this debounce, gated by the TELEPORT_FLAG bits (teleporters.qc:80-105).
        if (!predicted && (player.Flags & EntFlags.Client) != 0 && teleporter.PushLTime < MapMover.Now())
        {
            if ((tflags & FlagSound) != 0)
                MapMover.Sound(player, SoundChannel.TriggerAuto /* CH_TRIGGER (-3) */, TeleportSound(teleporter.Noise));
            if ((tflags & FlagParticles) != 0)
            {
                // QC: Send_Effect(EFFECT_TELEPORT, player.origin, …) and (…, to + v_forward*32, …) — both ends.
                EffectEmitter.Emit("TELEPORT", from);
                EffectEmitter.Emit("TELEPORT", to + toForward * 32f);
            }
            teleporter.PushLTime = MapMover.Now() + 0.2f;
        }

        // Relocate.
        MapMover.SetOrigin(player, to);
        player.OldOrigin = to; // don't unstick back through the portal
        player.Angles = toAngles;
        player.Velocity = toVelocity;
        player.Flags &= ~EntFlags.OnGround; // QC UNSET_ONGROUND

        if ((player.Flags & EntFlags.Client) != 0)
        {
            // QC player.fixangle = true: snap the client's VIEW to the destination facing. The client predictor
            // reads this off the prediction carrier after the tick (NetGame) to snap _viewAngles immediately;
            // the server sets it on the authoritative player too (faithful, harmless without a fixangle wire).
            player.FixAngle = true;
            player.FixAngleAngles = toAngles;

            // The server-only (#ifdef SVQC) side effects are skipped during client prediction — telefrag, the
            // kill-credit pusher window, and the teleport-time bookkeeping are all authoritative.
            if (!predicted)
            {
                // QC teleporters.qc:122 BITXOR_ASSIGN(player.effects, EF_TELEPORT_BIT) — toggle the teleport
                // sparkle/restart-anim flash on the player model. A networked effect bit, so server-side only.
                player.Effects ^= EffectFlags.Teleport;

                // QC teleporters.qc:148-150 — the full telefrag gate:
                //   (tflags & TELEPORT_FLAG_TDEATH) && player.takedamage && !IS_DEAD(player)
                //   && !g_race && !g_cts
                //   && (autocvar_g_telefrags || (tflags & TELEPORT_FLAG_FORCE_TDEATH))
                //   && !(round_handler_IsActive() && !round_handler_IsRoundStarted())
                // FORCE_TDEATH (the Porto-portal path) telefrags even with g_telefrags 0. The !g_race/!g_cts and
                // round-pre-start suppression need a static gametype/round-state accessor not reachable from this
                // file (see todos) — TeleportRoundGateSuppressed is the seam-stub where they'll plug in.
                bool tdeathOn = (tflags & FlagTdeath) != 0
                    && player.TakeDamage != DamageMode.No && !MapMover.IsDead(player)
                    && (Cvar("g_telefrags", 1f) != 0f || (tflags & FlagForceTdeath) != 0)
                    && !TeleportRoundGateSuppressed();
                if (tdeathOn)
                    Telefrag(player, teleporter, telefragger, to);

                // kill-credit window: a teleporter with an owner credits it for hazard kills shortly after.
                if (teleporter.Owner is not null)
                {
                    player.Pusher = teleporter.Owner;
                    player.PushLTime = MapMover.Now() + Cvar("g_maxpushtime", 8f);
                    // QC teleporters.qc:166 player.istypefrag = PHYS_INPUT_BUTTON_CHAT(player) — hold the
                    // teleportee's chat-button state for the kill-credit window (a typefrag if they were typing).
                    player.IsTypeFrag = player.ButtonChat;
                }
                else
                {
                    player.PushLTime = 0f;
                    player.IsTypeFrag = false; // QC teleporters.qc:171 player.istypefrag = 0
                }

                player.LastTeleportTime = MapMover.Now();
                player.LastTeleportOrigin = from;

                // [sv-antilag.clear.on_spawn] A teleport relocates the player; wipe its lag-comp ring so a
                // shot can't rewind it back through the portal toward its pre-teleport position (Base fires
                // antilag_clear on teleport via the same lifecycle as spawn). Explicit, so a SHORT portal hop
                // that lands within the origin-jump heuristic's threshold still clears.
                player.AntilagNeedsClear = true;

                // QC WarpZone_PostTeleportPlayer_Callback (teleporters.qc:310, #ifdef SVQC + IS_PLAYER): a teleport
                // forcibly snaps the view, so open the anticheat snap-aim suppression window. Server-only — gated
                // by !predicted (the SVQC side), so client prediction never touches it. Inert until the server
                // wires OnPlayerFixAngle.
                OnPlayerFixAngle?.Invoke(player);
            }
        }
    }

    /// <summary>
    /// Port of QC <c>tdeath</c>: gib everything live and damageable whose bbox overlaps the freshly-placed
    /// player at <paramref name="at"/> (the exact telefragmin/telefragmax box loop). A teammate is spared only
    /// when <c>teamplay &amp;&amp; g_telefrags_teamplay &amp;&amp; same-team</c> (Base default g_telefrags_teamplay 1 =
    /// "never telefrag teammates"); a dead-body / monster teleportee gibs itself instead of telefragging others.
    /// </summary>
    /// <summary>
    /// QC <c>Portal_TeleportPlayer</c> force-telefrag (server/portals.qc:188-194): a Porto-portal crossing runs the
    /// same <c>tdeath</c> box as a map teleporter but with <c>TELEPORT_FLAGS_PORTAL</c> (FORCE_TDEATH — telefrag even
    /// with <c>g_telefrags 0</c>), and reports whether it hit a live player (the QC <c>tdeath_hit</c> counter) so the
    /// caller can award the <c>ANNCE_ACHIEVEMENT_AMAZING</c> announcer on a telefrag within 1s of portal creation.
    /// The warpzone crossing doesn't route through <see cref="TeleportPlayer"/>, so this is the dedicated entry it
    /// calls instead. Honors the same <see cref="TeleportRoundGateSuppressed"/> / takedamage / not-dead gate as the
    /// map-teleporter telefrag (FORCE_TDEATH bypasses only the <c>g_telefrags</c> cvar, not these).
    /// </summary>
    public static bool PortalForceTelefrag(Entity player, Entity teleporter, Entity telefragger, Vector3 at)
    {
        if (Api.Services is null)
            return false;
        // QC TeleportPlayer telefrag gate (teleporters.qc:148-150) with FORCE_TDEATH: takedamage && !dead && !rc/cts
        // && !pre-round. g_telefrags is bypassed by FORCE_TDEATH, so it's not consulted here.
        if (player.TakeDamage == DamageMode.No || MapMover.IsDead(player) || TeleportRoundGateSuppressed())
            return false;
        return Telefrag(player, teleporter, telefragger, at);
    }

    private static bool Telefrag(Entity player, Entity teleporter, Entity telefragger, Vector3 at)
    {
        if (Api.Services is null)
            return false;
        bool hit = false;

        // QC TDEATHLOOP(player.origin) → findbox(deathmin, deathmax) (upstream b6e02fe3): the box is the
        // player's bbox at the destination (telefragmin/max are zero here, so no union). The old findradius
        // radius — max(vlen(deathmin), vlen(deathmax)) — was the box's distance from the WORLD ORIGIN (a Base
        // bug the boxesoverlap filter kept correct but slow); the exact box query replaces both. Fresh list per
        // call: event-driven, and Combat.Damage below can run arbitrary death hooks — a shared scratch could be
        // re-entered mid-iteration.
        Vector3 deathMin = at + player.Mins, deathMax = at + player.Maxs;
        var candidates = new List<Entity>();
        Api.Entities.FindInBox(deathMin, deathMax, candidates);

        // QC autocvar_g_telefrags_teamplay (default 1, xonotic-server.cfg:60) — note the Base NAME/sense: a value
        // of 1 means SPARE teammates. (cl_/g_ unregistered at runtime → Cvar() returns this faithful default.)
        bool spareTeammates = GameScores.Teamplay && Cvar("g_telefrags_teamplay", 1f) != 0f;
        bool teleporteeAlive = (player.Flags & EntFlags.Client) != 0
            && !MapMover.IsDead(player) && player.GetResource(ResourceType.Health) >= 1f;

        foreach (Entity head in candidates)
        {
            if (ReferenceEquals(head, player) || head.TakeDamage == DamageMode.No)
                continue;

            if (teleporteeAlive)
            {
                // QC: skip when (teamplay && g_telefrags_teamplay && head.team == player.team) — teammate spared.
                if (spareTeammates && head.Team == player.Team)
                    continue;
                if ((head.Flags & EntFlags.Client) == 0 || MapMover.IsDead(head) || head.GetResource(ResourceType.Health) < 1f)
                    continue;
                hit = true; // QC ++tdeath_hit (counted before the damage, live-player telefrag only)
                Combat.Damage(head, teleporter, telefragger, 10000f, DeathTypes.Telefrag, head.Origin, Vector3.Zero);
            }
            else
            {
                // a dead body / monster gibs ITSELF rather than telefragging.
                Combat.Damage(telefragger, teleporter, telefragger, 10000f, DeathTypes.Telefrag, telefragger.Origin, Vector3.Zero);
                break;
            }
        }
        return hit;
    }

    /// <summary>
    /// QC SND(TELEPORT) with the optional teleporter.noise override: when noise is a space-separated word list,
    /// pick one at random (Base RandomSelection over FOREACH_WORD, all weight 1) instead of using it verbatim.
    /// </summary>
    private static string TeleportSound(string? noise)
    {
        if (string.IsNullOrWhiteSpace(noise))
            return "misc/teleport.wav";
        string[] words = noise.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 1)
            return words.Length == 1 ? words[0] : "misc/teleport.wav";
        // RandomSelection with uniform weight 1: each candidate has probability 1/totalWeight of being chosen.
        string chosen = words[0];
        float total = 0f;
        foreach (string w in words)
        {
            total += 1f;
            if (Prandom.Float() * total <= 1f)
                chosen = w;
        }
        return chosen;
    }

    /// <summary>
    /// QC <c>!g_race &amp;&amp; !g_cts</c> plus <c>!(round_handler_IsActive() &amp;&amp; !round_handler_IsRoundStarted())</c>
    /// telefrag suppression (teleporters.qc:148-150). Returns true (suppress the telefrag) when the active mode is
    /// Race (<c>rc</c>) or CTS (<c>cts</c>) — where a spawn/checkpoint overlap must NOT gib the occupant — or when a
    /// round-based gametype is in its pre-round grace window (round armed but not yet live), read through the
    /// host-wired <see cref="RoundHandler.RoundGateBlocks"/> seam. The gametype NetName comes from the live
    /// <see cref="GameScores.Gametype"/> global (set server-side by the active mode), matching the QC
    /// <c>g_race</c>/<c>g_cts</c> globals.
    /// </summary>
    private static bool TeleportRoundGateSuppressed()
    {
        string gt = GameScores.Gametype;
        if (gt == "rc" || gt == "cts")
            return true;
        return RoundHandler.RoundGateBlocks();
    }

    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }

    /// <summary>
    /// Read a float cvar but fall back to <paramref name="fallback"/> only when the cvar is unset/empty —
    /// distinguishing "unset" (use the Base default) from an explicit "set to 0". Needed for a cvar whose
    /// Base default is truthy but which a server may legitimately disable (e.g. <c>g_telefrags_avoid</c>),
    /// where the plain <see cref="Cvar"/> helper would wrongly mask a deliberate 0 back to the default.
    /// </summary>
    private static float CvarOr(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name);
    }
}
