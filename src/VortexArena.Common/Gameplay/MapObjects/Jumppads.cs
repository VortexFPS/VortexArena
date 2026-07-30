// Port of qcsrc/common/mapobjects/trigger/jumppads.qc (trigger_push + target_push).
//
// A trigger_push launches the toucher on a ballistic arc toward a target (a target_push / target_position /
// info_notnull point, or another jumppad's destination), or — if it has no target — straight along its
// movedir at speed*10. The arc velocity is solved by trigger_push_calculatevelocity so the toucher reaches
// the target apex (`.height` controls how high the arc peaks).
//
// Core behavior ported FAITHFULLY: trigger_push_calculatevelocity (the projectile-motion solver, including
// solve_quadratic and the up/down-jump flighttime branch), the touch launch, weighted-random target
// resolution, the no-target movedir push, PUSH_ONCE self-removal, the jumppad sound/effect debounce,
// trigger_push_velocity (the player-directional XY/Z velocity pads with add/bidir/clamp modes), the
// teamplay pad ownership, the message-jumppad centerprint, and the kill-credit (pushltime/istypefrag) reset.
// Genuinely out of scope: warpzones, the bot waypoint trajectory probing (trigger_push_test / tracetoss),
// CSQC networking, and (pending cross-file infra) the ANIMACTION_JUMP one-shot jump pose (the port has no
// networked anim-action seam — same as PlayerPhysics jump and the walljump grunt; the locomotion blend still
// shows the airborne pose because the launch clears the on-ground flag). The jumppadcount/jumppadsused
// multi-pad bookkeeping IS ported (JumppadPush), as are the q3compat gravity correction + target_push 1.5s debounce.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary><c>trigger_push</c> (jumppad) + <c>target_push</c>/<c>target_position</c> destinations. Each setup is a spawnfunc.</summary>
public static class Jumppads
{
    // jumppads.qh spawnflag bits.
    public const int PushOnce = 1 << 0;   // PUSH_ONCE — legacy single-use
    public const int PushStatic = 1 << 12; // PUSH_STATIC — push from the jumppad center, not the toucher origin

    // trigger_push_velocity spawnflag bits (player-directional velocity pads).
    public const int PvPlayerDirXy = 1 << 0; // PUSH_VELOCITY_PLAYERDIR_XY
    public const int PvAddXy = 1 << 1;       // PUSH_VELOCITY_ADD_XY
    public const int PvPlayerDirZ = 1 << 2;  // PUSH_VELOCITY_PLAYERDIR_Z
    public const int PvAddZ = 1 << 3;        // PUSH_VELOCITY_ADD_Z
    public const int PvBidirectionalXy = 1 << 4; // PUSH_VELOCITY_BIDIRECTIONAL_XY
    public const int PvBidirectionalZ = 1 << 5;  // PUSH_VELOCITY_BIDIRECTIONAL_Z
    public const int PvClampNegativeAdds = 1 << 6; // PUSH_VELOCITY_CLAMP_NEGATIVE_ADDS

    /// <summary>QC <c>NUM_JUMPPADSUSED</c> (jumppads.qh:27): size of the per-player recent-jumppad ring.</summary>
    private const int NumJumppadsUsed = 3;

    private const string CvarGravity = "sv_gravity";
    private const float DefGravity = 800f;

    /// <summary><c>spawnfunc(trigger_push)</c>.</summary>
    public static void PushSetup(Entity this_)
    {
        MapMover.SetMovedir(this_);
        MapMover.InitTrigger(this_);
        this_.ClassName = "trigger_push";
        this_.Active = MapMover.ActiveActive;
        this_.Use = PushUse;
        this_.Touch = PushTouch;

        if (this_.Speed == 0f)
            this_.Speed = 1000f;
        this_.MoveDir *= this_.Speed * 10f; // no-target push velocity

        if (string.IsNullOrEmpty(this_.Noise))
            this_.Noise = "misc/jumppad.wav";

        MapMover.IndexRegister(this_);

        // Resolve the destination (if exactly one) like trigger_push_findtarget.
        FindTarget(this_);
    }

    /// <summary>
    /// <c>spawnfunc(trigger_push_velocity)</c> — a player-directional velocity pad: instead of an arc to a
    /// target it sets/adds the toucher's XY (<c>.speed</c>) and Z (<c>.count</c>) velocity, optionally along
    /// the toucher's own facing, bidirectionally, or clamped (the PUSH_VELOCITY_* spawnflags).
    /// </summary>
    public static void PushVelocitySetup(Entity this_)
    {
        MapMover.SetMovedir(this_);
        MapMover.InitTrigger(this_);
        this_.ClassName = "trigger_push_velocity";
        this_.Active = MapMover.ActiveActive;
        this_.Use = PushUse;
        this_.Touch = PushVelocityTouch;

        if (string.IsNullOrEmpty(this_.Noise))
            this_.Noise = "misc/jumppad.wav";

        MapMover.IndexRegister(this_);
        FindTarget(this_);
    }

    /// <summary><c>spawnfunc(target_push)</c> / <c>target_position</c> / <c>info_notnull</c> — a jump destination point.</summary>
    public static void TargetPushSetup(Entity this_)
    {
        this_.MAngle = this_.Angles;
        MapMover.SetOrigin(this_, this_.Origin);
        this_.Use = TargetPushUse;
        this_.ClassName = "target_push";

        // Q3 .angles/.speed pusher variant (no target): becomes a movedir push itself.
        if (string.IsNullOrEmpty(this_.Target))
        {
            if (this_.Speed == 0f)
                this_.Speed = 1000f;
            MapMover.SetMovedir(this_);
            this_.MoveDir *= this_.Speed;
        }

        if (string.IsNullOrEmpty(this_.Noise))
            this_.Noise = "misc/jumppad.wav";

        MapMover.IndexRegister(this_);
    }

    /// <summary>QC <c>trigger_push_findtarget</c> (headless core): cache the single destination if there is one.</summary>
    private static void FindTarget(Entity this_)
    {
        if (string.IsNullOrEmpty(this_.Target))
            return;
        var dests = MapMover.FindByTargetName(this_.Target).ToList();
        this_.Enemy = dests.Count == 1 ? dests[0] : null;
        // (QC also runs trigger_push_test/tracetoss here to probe the arc for the bot waypoint graph; that
        //  is a bot-navigation concern, not gameplay state, so it lives in the bot layer.)
    }

    /// <summary>QC <c>trigger_push_use</c>: teamplay claims the pad for the activator's team.</summary>
    private static void PushUse(Entity self, Entity actor)
    {
        // QC: if(teamplay) { this.team = actor.team; SendFlags |= …; } — gameplay half ported; net is client.
        self.Team = actor.Team;
    }

    /// <summary>QC <c>trigger_push_touch</c>: launch the toucher; PUSH_ONCE pads remove themselves after.</summary>
    public static void PushTouch(Entity self, Entity toucher)
    {
        if (self.Active == MapMover.ActiveNot)
            return;

        bool success = JumppadPush(self, toucher, isVelocityPad: false);

        if (success && (self.SpawnFlags & PushOnce) != 0 && Api.Services is not null)
        {
            self.Touch = null;
            MapMover.RemoveEntity(self);
        }
    }

    /// <summary>QC <c>trigger_push_velocity_touch</c>: a velocity pad sets/adds the toucher's velocity.</summary>
    public static void PushVelocityTouch(Entity self, Entity toucher)
    {
        if (self.Active == MapMover.ActiveNot)
            return;
        // team-owned pad: only its team is launched (QC DIFF_TEAM).
        if (self.Team != 0f && toucher.Team != self.Team)
            return;
        JumppadPush(self, toucher, isVelocityPad: true);
    }

    /// <summary>QC <c>target_push_use</c>: a non-jumppad trigger fires the destination's push on the activator.</summary>
    private static void TargetPushUse(Entity self, Entity actor)
    {
        // QC guards against being fired by a trigger_push or itself.
        JumppadPush(self, actor, isVelocityPad: false);
    }

    /// <summary>
    /// QC <c>jumppad_push</c> (core): compute and apply the launch velocity to <paramref name="targ"/>.
    /// <paramref name="isVelocityPad"/> selects the player-directional velocity solver (trigger_push_velocity)
    /// vs the ballistic arc (trigger_push). Returns true if the entity was pushable and launched.
    ///
    /// <paramref name="predicted"/> mirrors QC's <c>#ifdef CSQC</c> path used by client-side movement
    /// prediction: it applies the launch velocity + <c>UNSET_ONGROUND</c> (the parts QC runs on BOTH client and
    /// server) but SKIPS every server-only (<c>#ifdef SVQC</c>) side effect — the jumppad sound + flash, the
    /// <c>jumppadcount</c>/centerprint/animdecide bookkeeping, <c>SUB_UseTargets</c>, and the PUSH_ONCE removal
    /// (the caller must not delete the pad in prediction). This lets the predicted local player feel a pad in
    /// lockstep with the server's authoritative launch without double-firing sounds/targets or mutating world
    /// state during the reconcile replay. Default false = the full server behavior (every existing caller).
    /// </summary>
    public static bool JumppadPush(Entity self, Entity targ, bool isVelocityPad, bool predicted = false)
    {
        if (!MapMover.IsPushable(targ))
            return false;

        Vector3 org = targ.Origin;
        if ((self.SpawnFlags & PushStatic) != 0)
            org = (self.AbsMin + self.AbsMax) * 0.5f;

        // velocity pads remember the last pad that pushed each entity (QC .last_pushed) so an "add" mode pad
        // doesn't compound every frame the toucher overlaps it.
        bool alreadyPushed = false;
        if (isVelocityPad)
        {
            if (ReferenceEquals(self, targ.LastPushedPad))
                alreadyPushed = true;
            else
                targ.LastPushedPad = self;
        }

        Entity? dest = self.Enemy;
        if (dest is null && !string.IsNullOrEmpty(self.Target))
        {
            dest = PickDestination(self);
            if (dest is null)
                return false;
        }

        if (dest is not null)
        {
            targ.Velocity = isVelocityPad
                ? CalculateVelocityPad(self, org, dest, self.Speed, self.Count, targ, alreadyPushed)
                : CalculateVelocity(org, dest, self.Height, targ);
        }
        else if (!isVelocityPad)
        {
            // no target: straight movedir push (already scaled by speed*10 / speed).
            targ.Velocity = self.MoveDir;
        }
        else
        {
            return false; // a velocity pad with no target is an error in QC
        }

        if (!isVelocityPad)
            targ.Flags &= ~EntFlags.OnGround; // QC UNSET_ONGROUND (velocity pads keep ground for add-modes) — BOTH cl+sv

        // Everything below is QC #ifdef SVQC (server-authoritative): the client prediction (predicted=true)
        // applies ONLY the velocity + UNSET_ONGROUND above, exactly like jumppad_push on CSQC.
        if (predicted)
            return true;

        // sound/effect debounce + center-print message (QC), for client touchers.
        if ((targ.Flags & EntFlags.Client) != 0)
        {
            if (self.PushLTime < MapMover.Now() && !(MapMover.IsDead(targ) && targ.Velocity == Vector3.Zero))
            {
                // QC jumppads.qc:367-373: a Q3 target_push uses a longer 1.5s debounce and skips the flash;
                // every other pad flashes (EFFECT_JUMPPAD) and uses the stock 0.2s debounce.
                if (CompatRemaps.IsQ3Compat && self.ClassName == "target_push")
                    self.PushLTime = MapMover.Now() + 1.5f;
                else
                    self.PushLTime = MapMover.Now() + 0.2f;
                MapMover.Sound(targ, SoundChannel.Auto, self.Noise);
            }
            // QC jumppads.qc:380-388: multi-pad combo bookkeeping. Bump jumppadcount once per DISTINCT pad in
            // the current air-time (the jumppadsused ring dedupes a re-touch of the same pad), so a player who
            // chains several pads gets a >1 count (read by bot nav / the combo trace). jumppadcount is reset on
            // land/jump in PlayerPhysics.
            Entity?[] used = targ.JumpPadsUsed ??= new Entity?[NumJumppadsUsed];
            bool found = false;
            for (int i = 0; i < targ.JumpPadCount && i < NumJumppadsUsed; i++)
                if (ReferenceEquals(used[i], self))
                    found = true;
            if (!found)
            {
                used[targ.JumpPadCount % NumJumppadsUsed] = self;
                ++targ.JumpPadCount;
            }

            // QC: a message-jumppad center-prints this.message to the launched real client (jumppads.qc:392-393).
            // Real-client/non-empty gating is done inside the seam; bots fall through (lastteleport stamp).
            MapMover.Centerprint(targ, self.Message);
            targ.LastTeleportTime = MapMover.Now();
            targ.LastTeleportOrigin = targ.Origin;

            // QC: reset who pushed you into a hazard so the jumppad doesn't keep crediting an old attacker for a
            // later kill (jumppads.qc:407-409). (animdecide_setaction(ANIMACTION_JUMP) is the third-person jump
            // pose only — the port has no animdecide action seam, so it stays a documented presentation gap.)
            targ.PushLTime = 0f;
            targ.IsTypeFrag = false;
        }

        // If the jumppad's destination itself has targets, fire them (QC: SUB_UseTargets(this.enemy,...)).
        if (self.Enemy is not null && !string.IsNullOrEmpty(self.Enemy.Target))
            MapMover.UseTargets(self.Enemy, targ, self);

        return true;
    }

    /// <summary>
    /// QC <c>jumppad_push</c> destination pick: weighted-random over the targets by <c>.cnt</c> (all at the
    /// same priority, so it's a pure weighted reservoir). Deterministic via <see cref="Prandom"/>.
    /// </summary>
    private static Entity? PickDestination(Entity self)
    {
        var sel = new MapMover.RandomSelection();
        sel.Reset();
        foreach (Entity e in MapMover.FindByTargetName(self.Target))
            sel.Add(e, e.Cnt != 0 ? e.Cnt : 1, 1f);
        return sel.Chosen;
    }

    /// <summary>
    /// Port of <c>trigger_push_velocity_calculatevelocity</c> (jumppads.qc): combine an XY and a Z velocity,
    /// each either set from the player's own facing (<paramref name="speed"/>/<paramref name="count"/>) or
    /// from the ballistic solution toward <paramref name="tgt"/>, optionally added to the current velocity,
    /// optionally bidirectional (flip to match the player's heading), optionally clamped against backwards adds.
    /// </summary>
    private static Vector3 CalculateVelocityPad(Entity self, Vector3 org, Entity tgt, float speed, float count, Entity pushed, bool alreadyPushed)
    {
        int sf = self.SpawnFlags;
        bool playerDirXy = (sf & PvPlayerDirXy) != 0, addXy = (sf & PvAddXy) != 0;
        bool playerDirZ = (sf & PvPlayerDirZ) != 0, addZ = (sf & PvAddZ) != 0;
        bool bidirXy = (sf & PvBidirectionalXy) != 0, bidirZ = (sf & PvBidirectionalZ) != 0;
        bool clampNeg = (sf & PvClampNegativeAdds) != 0;

        Vector3 flatVel = new(pushed.Velocity.X, pushed.Velocity.Y, 0f);
        Vector3 sdir = QMath.Normalize(flatVel);
        float zdir = pushed.Velocity.Z;
        if (zdir != 0f) zdir = MathF.Sign(zdir);

        Vector3 vsTgt = Vector3.Zero;
        float vzTgt = 0f;
        if (!playerDirXy || !playerDirZ)
        {
            Vector3 velTgt = CalculateVelocity(org, tgt, 0f, pushed); // ht=0 ballistic
            vsTgt = new Vector3(velTgt.X, velTgt.Y, 0f);
            vzTgt = velTgt.Z;

            if (bidirXy && QMath.Dot(QMath.Normalize(vsTgt), sdir) < 0f)
                vsTgt = -vsTgt;
            if (bidirZ && MathF.Sign(vzTgt) != zdir && zdir != 0f)
                vzTgt = -vzTgt;
        }

        Vector3 vs = playerDirXy ? sdir * speed : vsTgt;
        float vz = playerDirZ ? zdir * count : vzTgt;

        if (addXy)
        {
            Vector3 vsAdd = new(pushed.Velocity.X, pushed.Velocity.Y, 0f);
            if (alreadyPushed) vs = vsAdd;
            else
            {
                vs += vsAdd;
                if (clampNeg && QMath.Dot(QMath.Normalize(vs), sdir) < 0f)
                    vs = Vector3.Zero;
            }
        }

        if (addZ)
        {
            float vzAdd = pushed.Velocity.Z;
            if (alreadyPushed) vz = vzAdd;
            else
            {
                vz += vzAdd;
                if (clampNeg && MathF.Sign(vz) != zdir && zdir != 0f)
                    vz = 0f;
            }
        }

        return new Vector3(vs.X, vs.Y, vz);
    }

    /// <summary>
    /// Faithful port of <c>trigger_push_calculatevelocity</c> (jumppads.qc): solve the projectile-motion
    /// launch velocity from <paramref name="org"/> so the entity reaches <paramref name="tgt"/>'s midpoint,
    /// arcing to a peak set by <paramref name="ht"/> (absolute value = apex height above the higher of org
    /// and target; sign selects whether the apex is inside or outside the trajectory).
    /// </summary>
    public static Vector3 CalculateVelocity(Vector3 org, Entity tgt, float ht, Entity pushedEntity)
    {
        Vector3 torg = tgt.Origin + (tgt.Mins + tgt.Maxs) * 0.5f;

        float grav = Gravity();
        if (pushedEntity.Gravity != 0f)
            grav *= pushedEntity.Gravity;

        // Q3 has frametime-dependent gravity, but its trigger_push velocity calc doesn't account for it; QC
        // corrects for that so entities land where they would in Q3 @ gravity 800/125fps (jumppads.qc:48-49:
        // grav /= 750/800 — the exact float 0.9375). Only on a Q3/Q3DF import map.
        if (CompatRemaps.IsQ3Compat)
            grav /= 750f / 800f;

        float zdist = torg.Z - org.Z;
        Vector3 flat = torg - org;
        flat.Z = 0f;
        float sdist = flat.Length();
        Vector3 sdir = QMath.Normalize(flat);

        // how high do we need to push?
        float jumpheight = MathF.Abs(ht);
        if (zdist > 0f)
            jumpheight += zdist;

        // vertical launch speed for that apex.
        float vz = MathF.Sqrt(MathF.Abs(2f * grav * jumpheight));

        // start downward only for a downjump whose apex lies outside the jump (ht < 0).
        if (ht < 0f && zdist < 0f)
            vz = -vz;

        // solve "z(ti) = zdist": 0.5*grav*t^2 - vz*t + zdist = 0
        (float sx, float sy, bool two) = SolveQuadratic(0.5f * grav, -vz, zdist);
        if (!two)
            sy = sx; // roundoff fallback: assume equal roots
        if (zdist == 0f)
            sx = sy; // avoid the 0 root

        float flighttime;
        if (zdist < 0f)
        {
            // down-jump: take the larger root in both sub-cases.
            flighttime = sy;
        }
        else
        {
            // up-jump: apex-outside (ht<0) takes the smaller root, otherwise the larger.
            flighttime = ht < 0f ? sx : sy;
        }

        float vs = flighttime != 0f ? sdist / flighttime : 0f;
        return sdir * vs + new Vector3(0f, 0f, vz);
    }

    /// <summary>
    /// Port of QC <c>solve_quadratic(a, b, c)</c> (lib/math): returns the two real roots ascending and a flag
    /// for whether two distinct real roots exist. Degenerate (a==0) and complex cases collapse like QC.
    /// </summary>
    private static (float x, float y, bool twoRoots) SolveQuadratic(float a, float b, float c)
    {
        if (a == 0f)
        {
            if (b == 0f)
                return (0f, 0f, false);
            float r = -c / b;
            return (r, r, false);
        }

        float disc = b * b - 4f * a * c;
        if (disc < 0f)
            return (0f, 0f, false); // no real roots

        float sq = MathF.Sqrt(disc);
        float x0 = (-b - sq) / (2f * a);
        float x1 = (-b + sq) / (2f * a);
        if (x0 > x1) (x0, x1) = (x1, x0);
        return (x0, x1, disc > 0f);
    }

    private static float Gravity() => Api.Services is null ? DefGravity : Cvar(CvarGravity, DefGravity);

    private static float Cvar(string name, float fallback)
    {
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }
}
