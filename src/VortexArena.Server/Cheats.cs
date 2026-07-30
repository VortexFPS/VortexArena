using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server;

/// <summary>
/// The server cheat system — the Godot-free essence of server/cheats.qc (<c>CheatInit</c> /
/// <c>CheatsAllowed</c> / <c>CheatCommand</c> + the cheat accounting). It gates cheats behind
/// <c>sv_cheats</c> (snapshotted at init, like QC's <c>gamestart_sv_cheats</c>, so a mid-match cvar change
/// doesn't take effect until restart) and implements the simple, pure-logic cheats: <c>god</c>, <c>notarget</c>,
/// <c>noclip</c>, <c>fly</c>, <c>give</c>, <c>usetarget</c>/<c>killtarget</c>, <c>teleporttotarget</c>, the
/// <c>R00T</c> radius-nuke impulse, and the <c>GIVE_ALL</c> impulse. Each successful cheat bumps a
/// per-player and a global cheat counter (QC <c>cheatcount</c> / <c>cheatcount_total</c>), which the campaign
/// reads to refuse saving progress when cheats were used.
///
/// Faithful to QC: <see cref="Allowed"/> requires BOTH the snapshot AND the live <c>sv_cheats</c> (except the
/// <see cref="Player"/>-level <c>maycheat</c> override); dead players can't cheat unless <paramref name="ignoreDead"/>;
/// at <c>sv_cheats &gt;= 2</c> non-players (observers) may cheat too. A refused cheat command broadcasts the QC
/// "Player ... tried to use cheat '...'" notice (QC <c>CheatsAllowed</c>'s <c>logattempt</c> branch). The
/// emergency <see cref="Teleport"/> impulse (info_autoscreenshot, else a random map location). The
/// remaining cheats — the clone/copybody clones, the speedrun personal-waypoint snapshot/restore, and the
/// drag object-carry subsystem + race map-editor tooling — depend on engine services (CopyBody, a per-frame
/// cheat hook, te_lightning1, file I/O) outside this Godot-free core and are not part of it; the
/// gameplay-state cheats above are complete.
/// </summary>
public sealed class Cheats
{
    /// <summary>QC <c>cheatcount_total</c>: cumulative successful cheats this map (campaign progress gate).</summary>
    public int CheatCountTotal { get; private set; }

    /// <summary>QC <c>gamestart_sv_cheats</c>: the <c>sv_cheats</c> value snapshotted at <see cref="Init"/>.</summary>
    public int GameStartCheats { get; private set; }

    /// <summary>Per-player successful-cheat count (QC the edict's <c>.cheatcount</c>).</summary>
    private readonly Dictionary<Player, int> _perPlayer = new();

    /// <summary>Diagnostics sink (QC bprint/sprint). Defaults to swallowing; a host/test can capture.</summary>
    public Action<string>? Log { get; set; }

    private void Echo(string s) => Log?.Invoke(s);

    /// <summary>QC <c>CheatInit</c>: snapshot <c>sv_cheats</c>; reset the counters for a fresh map.</summary>
    public void Init()
    {
        GameStartCheats = Cvars.Int("sv_cheats");
        CheatCountTotal = 0;
        _perPlayer.Clear();
    }

    /// <summary>How many successful cheats this player has used this map (QC <c>this.cheatcount</c>).</summary>
    public int CheatCountOf(Player p) => _perPlayer.TryGetValue(p, out int n) ? n : 0;

    private void AddCheats(Player p, int n)
    {
        if (n <= 0) return;
        CheatCountTotal += n;
        _perPlayer[p] = CheatCountOf(p) + n;
    }

    /// <summary>
    /// QC <c>CheatsAllowed</c>: is <paramref name="p"/> allowed to cheat right now? Requires both the snapshot
    /// and the live <c>sv_cheats</c> (or the player's <c>MayCheat</c> override). Dead players are refused unless
    /// <paramref name="ignoreDead"/>; observers (non-players) only at <c>sv_cheats &gt;= 2</c>.
    /// When refused at the final <c>sv_cheats==0</c> fall-through and <paramref name="logAttempt"/> is set,
    /// broadcasts the QC "Player ... tried to use cheat '<paramref name="cheatName"/>'" notice (QC's
    /// <c>bprintf</c> branch). The dead-player and observer guards refuse SILENTLY, as in Base.
    /// </summary>
    public bool Allowed(Player p, bool ignoreDead = false, bool logAttempt = false, string? cheatName = null,
        bool cloneImpulse = false)
    {
        // QC CheatsAllowed returns 0 SILENTLY on the dead-player and observer guards; only the
        // final sv_cheats==0 fall-through logs the attempt. Match that placement.
        if (!ignoreDead && p.IsDead) return false;
        if (GameStartCheats < 2 && (p.Flags & EntFlags.Client) == 0) return false; // observer guard
        // QC cheats.qc:66-68 — the CLONE_MOVING/CLONE_STANDING impulses are permitted INDEPENDENTLY of sv_cheats
        // while the player is still under their per-life clone budget (this.lip < sv_clones). lip counts the clones
        // already spawned this life.
        if (cloneImpulse && p.Lip < Cvars.Int("sv_clones")) return true;
        if (p.MayCheat) return true;
        if (GameStartCheats != 0 && Cvars.Int("sv_cheats") != 0) return true;
        return Refuse(p, logAttempt, cheatName);
    }

    /// <summary>
    /// QC <c>CheatsAllowed</c>'s refusal tail: when a player is not allowed to cheat and logging was requested,
    /// broadcast the attempt (QC <c>bprintf</c>); always returns false so callers can <c>return Refuse(...)</c>.
    /// </summary>
    private bool Refuse(Player p, bool logAttempt, string? cheatName)
    {
        if (logAttempt)
        {
            if (!string.IsNullOrEmpty(cheatName))
                Echo($"Player {p.NetName}^7 tried to use cheat '{cheatName}'");
            else
                Echo($"Player {p.NetName}^7 tried to use an unknown cheat");
        }
        return false;
    }

    /// <summary>
    /// QC <c>CheatCommand</c>: dispatch a <c>cheat</c>-class command (<paramref name="argv"/>[0] = name). Returns
    /// true if a cheat was attempted (allowed and handled), matching QC's "consumed" return. The caller routes a
    /// client's <c>cmd</c> here before the normal client-command table.
    /// </summary>
    public bool Command(Player p, string[] argv, object? gameType = null)
    {
        if (argv.Length == 0) return false;
        string verb = argv[0].ToLowerInvariant();
        switch (verb)
        {
            case "god":
                if (!Allowed(p, logAttempt: true, cheatName: verb)) return false;
                p.Flags ^= EntFlags.GodMode;
                Echo((p.Flags & EntFlags.GodMode) != 0 ? "godmode ON" : "godmode OFF");
                if ((p.Flags & EntFlags.GodMode) != 0) AddCheats(p, 1);
                return true;

            case "notarget":
                if (!Allowed(p, logAttempt: true, cheatName: verb)) return false;
                p.Flags ^= EntFlags.NoTarget;
                Echo((p.Flags & EntFlags.NoTarget) != 0 ? "notarget ON" : "notarget OFF");
                if ((p.Flags & EntFlags.NoTarget) != 0) AddCheats(p, 1);
                return true;

            case "noclip":
                if (!Allowed(p, logAttempt: true, cheatName: verb)) return false;
                if (p.MoveType != MoveType.Noclip) { p.MoveType = MoveType.Noclip; Echo("noclip ON"); AddCheats(p, 1); }
                else { p.MoveType = MoveType.Walk; Echo("noclip OFF"); }
                return true;

            case "fly":
                if (!Allowed(p, logAttempt: true, cheatName: verb)) return false;
                if (p.MoveType != MoveType.Fly) { p.MoveType = MoveType.Fly; Echo("flymode ON"); AddCheats(p, 1); }
                else { p.MoveType = MoveType.Walk; Echo("flymode OFF"); }
                return true;

            case "give":
                if (!Allowed(p, logAttempt: true, cheatName: verb)) return false;
                if (GiveItems(p, argv)) AddCheats(p, 1);
                return true;

            case "usetarget":
                if (!Allowed(p, logAttempt: true, cheatName: verb)) return false;
                FireNamedTarget(p, argv.Length > 1 ? argv[1] : "", killTarget: false);
                AddCheats(p, 1);
                return true;

            case "killtarget":
                if (!Allowed(p, logAttempt: true, cheatName: verb)) return false;
                FireNamedTarget(p, argv.Length > 1 ? argv[1] : "", killTarget: true);
                AddCheats(p, 1);
                return true;

            case "teleporttotarget":
                if (!Allowed(p, logAttempt: true, cheatName: verb)) return false;
                // QC: DID_CHEAT only when teleport_findtarget found a destination and the teleport ran.
                if (TeleportToTarget(p, argv.Length > 1 ? argv[1] : "")) AddCheats(p, 1);
                return true;

            case "pointparticles":
                if (!Allowed(p, logAttempt: true, cheatName: verb)) return false;
                // QC cheats.qc:299: `cmd pointparticles <effectname> <position> <velocity> <countmultiplier>`.
                // argc must be 5 (verb + 4 args). position is a 0..1 lerp along the crosshair line; velocity is
                // a "x y z" vector; countmultiplier scales the spawn count. Send_Effect_(name, start, vel, mul).
                if (argv.Length == 5)
                {
                    float posF = StoF(argv[2]);
                    AimTrace(p, WeaponFiring.CurrentMaxShotDistance, out _, out Vector3 hit);
                    Vector3 start = (1f - posF) * p.Origin + posF * hit;
                    Vector3 vel = StoV(argv[3]);
                    int countMul = (int)System.MathF.Round(StoF(argv[4]));
                    EffectEmitter.EmitByEffectInfoName(argv[1], start, vel, countMul);
                    AddCheats(p, 1);
                    return true;
                }
                Echo("Usage:^3 sv_cheats 1; restart; cmd pointparticles <effectname> <position> <velocity> <countmultiplier>");
                Echo("  Where <position> is a number from 0 to 1 representing distance on the crosshair line,");
                Echo("  and <velocity> is a vector \"x y z\"");
                return true;

            case "trailparticles":
                if (!Allowed(p, logAttempt: true, cheatName: verb)) return false;
                // QC cheats.qc:316: `cmd trailparticles <effectname>` — W_SetupShot + traceline along the aim line,
                // then __trailparticles(this, effectnum, w_shotorg, trace_endpos) draws the trail to the hit point.
                if (argv.Length == 2)
                {
                    Vector3 shotorg = p.Origin + p.ViewOfs;
                    AimTrace(p, WeaponFiring.CurrentMaxShotDistance, out _, out Vector3 trailEnd);
                    EffectEmitter.EmitTrail(Effects.ByEffectInfoName(argv[1]), shotorg, trailEnd);
                    AddCheats(p, 1);
                    return true;
                }
                Echo("Usage: sv_cheats 1; restart; cmd trailparticles <effectname>");
                return true;

            case "make":
                if (!Allowed(p, logAttempt: true, cheatName: verb)) return false;
                if (argv.Length == 3) { if (Make(p, argv[1], (int)StoF(argv[2]))) AddCheats(p, 1); return true; }
                Echo("Usage:^3 sv_cheats 1; restart; cmd make <modelname> <mode>");
                Echo("  where <mode> can be 0, 1 or 2");
                return true;

            case "penalty":
                if (!Allowed(p, logAttempt: true, cheatName: verb)) return false;
                // QC cheats.qc:378: `cmd penalty <duration> <reason>` — race_ImposePenaltyTime(this, dur, reason).
                // The reason string only drives a CSQC HUD notification (client-side); the gameplay state (a freeze
                // in a plain race, or an added accumulator in qualifying) is what SetPenalty reproduces.
                if (argv.Length == 3)
                {
                    if (gameType is XonoticGodot.Common.Gameplay.Race race)
                        race.SetPenalty(p, StoF(argv[1]));
                    AddCheats(p, 1);
                    return true;
                }
                Echo("Usage:^3 sv_cheats 1; restart; cmd penalty <duration> <reason>))");
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// QC the <c>GIVE_ALL</c> impulse (impulse 99): equivalent to <c>give all</c>. Returns true when a cheat
    /// was applied (so the caller bumps the cheat count).
    /// </summary>
    public bool GiveAll(Player p)
    {
        if (!Allowed(p, logAttempt: true, cheatName: "impulse 99")) return false;
        bool ok = GiveItems(p, new[] { "give", "all" });
        if (ok) AddCheats(p, 1);
        return ok;
    }

    /// <summary>
    /// QC the <c>R00T</c> impulse (impulse 148, <c>CheatImpulse</c> CHIMPULSE_R00T): nuke a random living enemy.
    /// Picks a uniformly-random live enemy player from <paramref name="roster"/> (QC RandomSelection over
    /// <c>IS_PLAYER(it) &amp;&amp; !IS_DEAD(it) &amp;&amp; DIFF_TEAM(it, this)</c>), falling back to the caller
    /// when there is none, then spawns the rocket-explode effect + impact sound at the victim and applies
    /// <c>RadiusDamage(1000 dmg, 0 edge, 128 radius, 500 force, DEATH_CHEAT)</c>. Logs "404 Sportsmanship not
    /// found." and counts a cheat. <paramref name="roster"/> is the connected-client list (observers and dead
    /// players are filtered out here).
    /// </summary>
    public bool R00t(Player p, IReadOnlyList<Player> roster)
    {
        if (!Allowed(p, logAttempt: true, cheatName: "impulse 148")) return false;

        // QC: RandomSelection over every IS_PLAYER && !IS_DEAD && DIFF_TEAM enemy (reservoir-sample one uniformly).
        Entity victim = p;
        int seen = 0;
        for (int i = 0; i < roster.Count; i++)
        {
            Player it = roster[i];
            if (it.IsObserver || it.IsDead) continue; // QC IS_PLAYER(it) && !IS_DEAD(it)
            if (it.Team == p.Team) continue; // QC DIFF_TEAM(it, this): a.team != b.team (FFA: all 0 -> self-nuke)
            seen++;
            if (XonoticGodot.Common.Math.Prandom.RangeInt(0, seen) == 0)
                victim = it;
        }

        // QC: Send_Effect(EFFECT_ROCKET_EXPLODE) + sound(CH_SHOTS, SND_ROCKET_IMPACT) at the victim.
        EffectEmitter.Emit("ROCKET_EXPLODE", victim.Origin);
        WeaponSplash.ImpactSound(victim, "weapons/rocket_impact.wav");

        // QC: RadiusDamage(spawn at victim, this, 1000, 0, 128, NULL, NULL, 500, DEATH_CHEAT, DMG_NOWEP, victim).
        WeaponSplash.RadiusDamage(
            inflictor: victim, center: victim.Origin, damage: 1000f, edgeDamage: 0f, radius: 128f,
            attacker: p, deathType: 0, force: 500f, directHit: victim,
            deathTag: XonoticGodot.Common.Gameplay.Damage.DeathTypes.Cheat);

        Echo("404 Sportsmanship not found.");
        AddCheats(p, 1);
        return true;
    }

    /// <summary>
    /// QC the <c>teleporttotarget</c> cheat command (<c>CheatCommand</c> case): teleport the caller to a named
    /// teleport-destination target. QC spawns a transient <c>cheattriggerteleport</c>, sets <c>.target</c>,
    /// runs <c>teleport_findtarget</c>, and (when a destination was found) <c>Simple_TeleportPlayer</c>.
    /// </summary>
    private static bool TeleportToTarget(Player p, string targetName)
        => Teleporters.TeleportToTarget(p, targetName);

    /// <summary>
    /// QC the <c>TELEPORT</c> impulse (impulse 143, <c>CheatImpulse</c> CHIMPULSE_TELEPORT): emergency teleport.
    /// While noclipping, prefer the nearest <c>info_autoscreenshot</c> point (consumed on use); otherwise fall
    /// back to <c>MoveToRandomMapLocation</c> — the QC engine builtin scattered points in the world bounds, but
    /// here (lacking a headless world-bounds sampler) we use the spawnpoint-sampling fallback path QC itself drops
    /// to (<see cref="BallEntity.RandomMapLocation"/>). On success: zero the velocity, snap the view (fixangle),
    /// and count a cheat. Returns true when a destination was found.
    /// </summary>
    public bool Teleport(Player p)
    {
        if (!Allowed(p, logAttempt: true, cheatName: "impulse 143")) return false;
        if (Api.Services is null) return false;

        // QC: if(this.move_movetype == MOVETYPE_NOCLIP) { e = find(NULL, classname, "info_autoscreenshot"); ... }
        if (p.MoveType == MoveType.Noclip)
        {
            Entity? shot = null;
            foreach (Entity e in Api.Entities.FindByClass("info_autoscreenshot"))
            {
                if (!e.IsFreed) { shot = e; break; }
            }
            if (shot is not null)
            {
                Echo("Emergency teleport used info_autoscreenshot location");
                MapMover.SetOrigin(p, shot.Origin);
                p.Angles = shot.Angles;
                p.FixAngle = true;
                p.FixAngleAngles = shot.Angles;
                p.Velocity = Vector3.Zero;
                Api.Entities.Remove(shot); // QC delete(e) — the point is consumed
                AddCheats(p, 1);
                return true;
            }
        }

        // QC: MoveToRandomMapLocation(...) — random reachable spot. We use the spawnpoint-sampling fallback.
        Vector3 dest = BallEntity.RandomMapLocation(p.Origin);
        if (dest != p.Origin)
        {
            Echo("Emergency teleport used random location");
            MapMover.SetOrigin(p, dest);
            // QC: this.angles_x = -this.angles.x; this.fixangle = true; (only pitch is flipped)
            Vector3 a = new(-p.Angles.X, p.Angles.Y, p.Angles.Z);
            p.Angles = a;
            p.FixAngle = true;
            p.FixAngleAngles = a;
            p.Velocity = Vector3.Zero;
            AddCheats(p, 1);
            return true;
        }

        Echo("Emergency teleport could not find a good location, forget it!");
        return false;
    }

    /// <summary>
    /// QC <c>CHIMPULSE_SPEEDRUN_INIT</c> (impulse 30, cheats.qc:144): snapshot the player's restorable state into
    /// its personal speedrun checkpoint. NOT a cheat by itself — QC's case skips IS_CHEAT (the impulse shares the
    /// <c>waypoint_personal_here</c> key) and never bumps the cheat count, so this runs unconditionally (no sv_cheats
    /// gate) and adds nothing to the counters. The waypoint SPRITE half is deployed separately on the same impulse.
    /// </summary>
    public void SpeedrunInit(Player p)
        => XonoticGodot.Common.Gameplay.Speedrun.SnapshotPersonal(p);

    /// <summary>
    /// QC <c>CHIMPULSE_SPEEDRUN</c> (impulse 141, cheats.qc:188): teleport the player back to its personal
    /// checkpoint and restore the snapshotted state. QC gates it as a cheat ONLY when <c>g_allow_checkpoints</c> is
    /// off: <c>if(!g_allow_checkpoints) IS_CHEAT(...)</c> at the top (refuse + don't restore when cheats disallowed)
    /// and <c>if(!g_allow_checkpoints) DID_CHEAT()</c> after a successful restore (count it). With checkpoints
    /// allowed it is a free reset (no gate, no count). Returns true when the restore ran.
    /// </summary>
    public bool Speedrun(Player p)
    {
        bool checkpointsAllowed = Cvars.Bool("g_allow_checkpoints");
        // QC: if(!autocvar_g_allow_checkpoints) IS_CHEAT(...) — when checkpoints aren't a free reset, this is a
        // cheat and the sv_cheats gate applies (IS_CHEAT breaks out of the switch on refusal, before restoring).
        if (!checkpointsAllowed && !Allowed(p, logAttempt: true, cheatName: "impulse 141"))
            return false;

        bool restored = XonoticGodot.Common.Gameplay.Speedrun.RestorePersonal(p, Echo);
        // QC: if(restored && !autocvar_g_allow_checkpoints) DID_CHEAT(); — count only the cheating (gated) restore.
        if (restored && !checkpointsAllowed) AddCheats(p, 1);
        return restored;
    }

    // =============================================================================================
    // give (QC GiveItems — the cheat-side give parser, reduced to the gameplay-state slice)
    // =============================================================================================

    /// <summary>
    /// QC <c>GiveItems</c> (the cheat <c>give</c> command): now routes through the shared op-aware
    /// <see cref="XonoticGodot.Common.Gameplay.GiveItems"/> (T35) — <c>GiveItems(actor, 0, tokenize)</c> with
    /// <c>argv[0]=="give"</c> dropped — so <c>give all</c> / <c>give allweapons</c> / <c>give max 50 health</c> /
    /// <c>give &lt;weapon&gt;</c> all use the faithful grammar (the FALLTHROUGH aggregates, the operator prefixes)
    /// and populate BOTH weapon-ownership reps. Returns true if anything changed (QC <c>got</c> &gt; 0).
    /// </summary>
    private bool GiveItems(Player p, string[] argv)
        => XonoticGodot.Common.Gameplay.GiveItems.Apply(p, argv, 1, argv.Length) != 0;

    /// <summary>
    /// QC the <c>usetarget</c>/<c>killtarget</c> cheats: spawn a transient entity carrying the named target
    /// (in the <c>.target</c> or <c>.killtarget</c> slot), fire <see cref="MapMover.UseTargets"/> (which uses
    /// or removes the matched entities), then drop the transient.
    /// </summary>
    private static void FireNamedTarget(Player p, string targetName, bool killTarget)
    {
        if (string.IsNullOrEmpty(targetName) || Api.Services is null)
            return;
        Entity trigger = Api.Entities.Spawn();
        if (killTarget) trigger.KillTarget = targetName;
        else trigger.Target = targetName;
        MapMover.UseTargets(trigger, p, null);
        Api.Entities.Remove(trigger);
    }

    // =============================================================================================
    // particle/make cheats (QC cheats.qc cases pointparticles/trailparticles/make) — aim helpers
    // =============================================================================================

    /// <summary>
    /// QC <c>crosshair_trace(this)</c> / <c>W_SetupShot</c>+<c>traceline</c>: trace from the caller's eyes along
    /// its view angles out to <paramref name="dist"/>. Mirrors Commands.TraceLookedAtMonster/Chat.CrosshairTrace.
    /// <paramref name="normal"/> = the hit surface normal (QC <c>trace_plane_normal</c>); <paramref name="endPos"/>
    /// = the impact point (QC <c>trace_endpos</c>). Hit-nothing leaves a far endpos and a zero normal.
    /// </summary>
    private static bool AimTrace(Player p, float dist, out Vector3 normal, out Vector3 endPos)
        => AimTrace(p, dist, out normal, out endPos, out _, out _);

    /// <summary>
    /// QC <c>crosshair_trace</c> / <c>W_SetupShot</c>+<c>traceline</c> variant that also reports the trace
    /// fraction (<paramref name="fraction"/>, QC <c>trace_fraction</c>) and the hit surface's Q3 surface flags
    /// (<paramref name="surfaceFlags"/>, QC <c>trace_dphitq3surfaceflags</c>) so the <c>make</c> cheat can apply
    /// Base's full bad-surface guard (reject NOIMPACT/sky as well as hit-nothing).
    /// </summary>
    private static bool AimTrace(Player p, float dist, out Vector3 normal, out Vector3 endPos,
        out float fraction, out int surfaceFlags)
    {
        Vector3 eyes = p.Origin + p.ViewOfs;
        Vector3 aim = p.ViewAngles != Vector3.Zero ? p.ViewAngles : p.Angles;
        XonoticGodot.Common.Math.QMath.AngleVectors(aim, out Vector3 forward, out _, out _);
        Vector3 end = eyes + forward * dist;
        if (Api.Services is null) { normal = Vector3.Zero; endPos = end; fraction = 1f; surfaceFlags = 0; return false; }
        TraceResult tr = Api.Trace.Trace(eyes, Vector3.Zero, Vector3.Zero, end, MoveFilter.Normal, p);
        normal = tr.PlaneNormal;
        endPos = tr.EndPos;
        fraction = tr.Fraction;
        surfaceFlags = tr.DpHitQ3SurfaceFlags;
        return tr.Fraction < 1f; // hit something
    }

    /// <summary>
    /// QC the <c>make</c> cheat command (cheats.qc:331): traceline 2048 along the aim line, then spawn a
    /// <c>func_breakable</c> with 1000 health and the requested model at the hit point. <paramref name="mode"/> 1
    /// surface-aligns the model to the hit normal; mode 0 validates the spot fits (tracebox start-solid → fail).
    /// Returns true when something was actually placed (QC's DID_CHEAT condition). The QC <c>e.mdl="rocket_explode"</c>
    /// wreck/explosion model is presentation-only (not carried by the port's breakable) and is skipped.
    /// </summary>
    private bool Make(Player p, string model, int mode)
    {
        if (Api.Services is null) return false;
        AimTrace(p, 2048f, out Vector3 normal, out Vector3 endPos, out float fraction, out int surfaceFlags);
        // QC cheats.qc:338 — reject when the surface is NOIMPACT (Q3SURFACEFLAG_NOIMPACT, set on sky too) OR the
        // trace hit nothing (fraction == 1). The port previously only rejected on hit-nothing, so a sky/noimpact
        // face would still get a breakable placed against it.
        const int Q3SurfaceFlagNoImpact = 16; // dpextensions Q3SURFACEFLAG_NOIMPACT
        if ((surfaceFlags & Q3SurfaceFlagNoImpact) != 0 || fraction >= 1f)
        {
            Echo("cannot make stuff there (bad surface)");
            return false;
        }

        Entity e = Api.Entities.Spawn();
        e.ClassName = "func_breakable";
        e.Model = model;
        e.Health = 1000f;
        e.Effects |= 8388608; // EF_NOMODELFLAGS (dpextensions: ignore model file effects)
        MapMover.SetOrigin(e, endPos);
        if (mode == 1)
        {
            // QC cheats.qc:352-353: e.angles = fixedvectoangles2(trace_plane_normal, v_forward);
            //                      e.angles = AnglesTransform_ApplyToAngles(e.angles, '-90 0 0'); // stand models up
            XonoticGodot.Common.Math.QMath.AngleVectors(
                p.ViewAngles != Vector3.Zero ? p.ViewAngles : p.Angles, out Vector3 fwd, out _, out _);
            Vector3 a = XonoticGodot.Common.Math.QMath.FixedVecToAngles2(normal, fwd);
            a = XonoticGodot.Common.Math.QMath.AnglesTransformApplyToAngles(a, new Vector3(-90f, 0f, 0f));
            e.Angles = a;
        }
        Breakable.BreakableSetup(e);

        if (mode == 0)
        {
            // QC: tracebox at the breakable's own box — if start-solid there's no space, so drop it and fail.
            TraceResult vt = Api.Trace.Trace(e.Origin, e.Mins, e.Maxs, e.Origin, MoveFilter.Normal, e);
            if (vt.StartSolid)
            {
                Api.Entities.Remove(e);
                Echo("cannot make stuff there (no space)");
                return false;
            }
        }
        return true;
    }

    private static float StoF(string s)
        => float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f;

    /// <summary>QC <c>stov</c>: parse a "x y z" string into a vector (missing/garbage components → 0).</summary>
    private static Vector3 StoV(string s)
    {
        string[] parts = s.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        float x = parts.Length > 0 ? StoF(parts[0]) : 0f;
        float y = parts.Length > 1 ? StoF(parts[1]) : 0f;
        float z = parts.Length > 2 ? StoF(parts[2]) : 0f;
        return new Vector3(x, y, z);
    }
}
