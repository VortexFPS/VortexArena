// Port of qcsrc/common/mapobjects/func/door.qc (+ door.qh, door_rotating.qc).
//
// "Doors are similar to buttons, but can spawn a fat trigger field around them to open without a touch,
//  and they link together to form simultaneous double/quad doors." (QC header)
//
// A func_door is a MOVETYPE_PUSH brush that slides between its closed position (pos1 = spawn origin) and
// open position (pos2 = pos1 + movedir * (|movedir·size| - lip)). It opens on use/touch/damage via
// SUB_CalcMove, waits `.wait`, then closes; TOGGLE doors stay until re-triggered; START_OPEN swaps
// pos1/pos2; a shootable door (health) opens when killed; a blocked door reverses (and bites for `.dmg`).
//
// Now ported in full: LinkDoors connected-component grouping (double/quad doors move as a unit via the
// owner/enemy chain), the separate spawned door_spawnfield touch volume, Quake-1/QL key locks
// (itemkeys / gold/silver), shootable doors via the damage pipeline's Death hook, the chain-open through
// owner/enemy, and door_rotating axis selection. The DOOR_ROTATING_BIDIR / BIDIR_IN_DOWN trigger-side
// reverse-open path (door_use's trigger.trigger_reverse handling) is now ported too — the firing trigger is
// threaded via MapMover.CurrentUseTrigger. Genuinely client-only bits (CSQC networking, CPMA sound
// overrides) are out of scope for the headless sim.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// <c>func_door</c> / <c>func_door_rotating</c> — sliding (and rotating) BSP doors. Registered as
/// spawnfuncs; the BSP entity lump invokes <see cref="DoorSetup"/> for "func_door".
/// </summary>
public static class Doors
{
    // ---- door.qh spawnflag bits ----
    public const int StartOpen = 1 << 0;   // DOOR_START_OPEN — spawn open, operate in reverse
    public const int DontLink = 1 << 2;    // DOOR_DONT_LINK
    public const int GoldKey = 1 << 3;     // SPAWNFLAGS_GOLD_KEY
    public const int SilverKey = 1 << 4;   // SPAWNFLAGS_SILVER_KEY
    public const int Toggle = 1 << 5;      // DOOR_TOGGLE — wait in both states for a trigger
    public const int NoSplash = 1 << 8;    // NOSPLASH (defs.qh) — generic anti-splashdamage spawnflag
    public const int NonSolid = 1 << 10;   // DOOR_NONSOLID
    public const int Crush = 1 << 11;      // DOOR_CRUSH — instakill blockers

    // ---- door_rotating.qh spawnflag bits ----
    public const int RotatingBidir = 1 << 1;        // DOOR_ROTATING_BIDIR
    public const int RotatingBidirInDown = 1 << 3;  // DOOR_ROTATING_BIDIR_IN_DOWN
    public const int RotatingXAxis = 1 << 6;        // DOOR_ROTATING_XAXIS
    public const int RotatingYAxis = 1 << 7;        // DOOR_ROTATING_YAXIS

    // QC key bits (item_keys): BIT(0) gold, BIT(1) silver.
    private const int KeyGoldBit = 1 << 0;
    private const int KeySilverBit = 1 << 1;

    private static bool _deathHooked;

    /// <summary>
    /// <c>spawnfunc(func_door)</c> — set up a sliding door. Faithful to the QC field assignment order
    /// (keys, movedir, brush init, defaults, pos1/pos2, touch/use/blocked hooks, then deferred LinkDoors).
    /// </summary>
    public static void DoorSetup(Entity this_)
    {
        EnsureDeathHook();
        DoorInitKeys(this_);

        MapMover.SetMovedir(this_);

        if (!MapMover.InitMovingBrushTrigger(this_))
            return;
        this_.Effects |= AdvancedMovers.EfLowPrecision; // QC: this.effects |= EF_LOWPRECISION (door.qc:779)
        this_.ClassName = "door";

        this_.Blocked = DoorBlocked;
        this_.Use = DoorUse;
        this_.Reset = DoorReset; // QC door.qc: this.reset = door_reset (round-restart re-arm)
        this_.Active = MapMover.ActiveActive;

        if ((this_.SpawnFlags & NonSolid) != 0)
            this_.Solid = Solid.Not;

        InitShared(this_);

        // pos1 = closed (spawn origin); pos2 = pos1 + movedir*(|movedir·size| - lip)
        this_.Pos1 = this_.Origin;
        Vector3 absMoveDir = new(MathF.Abs(this_.MoveDir.X), MathF.Abs(this_.MoveDir.Y), MathF.Abs(this_.MoveDir.Z));
        this_.Pos2 = this_.Pos1 + this_.MoveDir * (QMath.Dot(absMoveDir, this_.Size) - this_.Lip);

        if (this_.Speed == 0f)
            this_.Speed = 100f;

        // DOOR_START_OPEN: spawn at pos2 and swap, so it operates in reverse.
        if ((this_.SpawnFlags & StartOpen) != 0)
            DoorInitStartOpen(this_);

        this_.Touch = DoorTouch;

        this_.MoverState = MapMover.StateBottom;

        MapMover.IndexRegister(this_);

        // LinkDoors can't run until every door has spawned (so sizes are known); QC defers it to
        // INITPRIO_LINKDOORS. Headless we run it at the end of the registration pass via DeferredLink.
        QueueLink(this_);
    }

    /// <summary>
    /// <c>spawnfunc(func_door_rotating)</c> — a door that swings about an axis instead of sliding. Core
    /// open/close uses the angle-move driver between two angle positions; QC abuses .movedir to denote the
    /// axis and .angles_y for the swing magnitude.
    /// </summary>
    public static void DoorRotatingSetup(Entity this_)
    {
        EnsureDeathHook();

        // QC abuses "movedir" to denote the rotation axis.
        if ((this_.SpawnFlags & RotatingXAxis) != 0)
            this_.MoveDir = new Vector3(0f, 0f, 1f);
        else if ((this_.SpawnFlags & RotatingYAxis) != 0)
            this_.MoveDir = new Vector3(1f, 0f, 0f);
        else // Z (yaw)
            this_.MoveDir = new Vector3(0f, 1f, 0f);

        float swing = this_.Angles.Y != 0f ? this_.Angles.Y : 90f;
        this_.MoveDir *= swing;
        this_.Angles = Vector3.Zero;
        this_.AVelocity = this_.MoveDir;

        if (!MapMover.InitMovingBrushTrigger(this_))
            return;
        this_.Velocity = Vector3.Zero;
        this_.ClassName = "door_rotating";

        this_.Blocked = DoorBlocked;
        this_.Use = DoorUse;
        this_.Reset = DoorReset; // QC door.qc: this.reset = door_reset (shared with func_door)
        this_.Active = MapMover.ActiveActive;

        // pos1 = closed angles (zero), pos2 = open-angle delta (movedir).
        this_.Pos1 = Vector3.Zero;
        this_.Pos2 = this_.MoveDir;

        if ((this_.SpawnFlags & StartOpen) != 0)
        {
            // door_rotating_init_startopen: spawn open, run in reverse.
            this_.Angles = this_.MoveDir;
            this_.Pos2 = Vector3.Zero;
            this_.Pos1 = this_.MoveDir;
        }

        InitShared(this_);

        if (this_.Speed == 0f)
            this_.Speed = 50f;
        this_.Lip = 0f; // used to remember the reverse-open direction for door_rotating

        this_.Touch = DoorTouch;
        this_.MoverState = MapMover.StateBottom;

        MapMover.IndexRegister(this_);
        QueueLink(this_);
    }

    /// <summary>QC <c>door_init_keys</c>: translate the gold/silver spawnflags into itemkey bits (func_door only).</summary>
    private static void DoorInitKeys(Entity this_)
    {
        if ((this_.SpawnFlags & GoldKey) != 0)
            this_.ItemKeys |= KeyGoldBit;
        if ((this_.SpawnFlags & SilverKey) != 0)
            this_.ItemKeys |= KeySilverBit;
    }

    /// <summary>QC <c>door_init_shared</c> (the func_door/func_door_rotating common defaults).</summary>
    private static void InitShared(Entity this_)
    {
        this_.MaxHealthMover = this_.Health;

        if (string.IsNullOrEmpty(this_.Noise))
            this_.Noise = "misc/talk.wav";   // unlock / message sound
        if (string.IsNullOrEmpty(this_.Noise3))
            this_.Noise3 = "misc/talk.wav";  // still-locked sound

        if ((this_.Dmg != 0f || (this_.SpawnFlags & Crush) != 0) && string.IsNullOrEmpty(this_.Message))
            this_.Message = "was squished";

        // QC door_init_shared only assigns the medplat soundpack when `sounds > 0 || q3compat`
        // (Q3 doors are hard-coded to have sounds); otherwise Noise1/Noise2 stay empty and the
        // move/stop _sound calls are no-ops. ApplyDoorSounds reads Entity.Sounds and gates this.
        MapMover.ApplyDoorSounds(this_);

        if (this_.Wait == 0f)
            this_.Wait = 3f;
        if (this_.Lip == 0f)
            this_.Lip = 8f;

        this_.MoverState = MapMover.StateBottom;

        // Shootable door (health set in the map): can be opened by damage (event_damage = door_damage).
        if (this_.Health != 0f)
            this_.TakeDamage = DamageMode.Yes;

        // QC: a key door never auto-returns.
        if (this_.ItemKeys != 0)
            this_.Wait = -1f;
    }

    /// <summary>
    /// QC <c>door_reset</c> (door.qc:648): the round-restart hook. Snap the door home (pos1 = closed),
    /// stop it, restore STATE_BOTTOM + ACTIVE_ACTIVE, drop any pending think, and re-init the key bits.
    /// For func_door_rotating the "home" is the closed ANGLE (pos1) rather than a translated origin.
    /// </summary>
    public static void DoorReset(Entity this_)
    {
        if (this_.ClassName == "door_rotating")
            this_.Angles = this_.Pos1;           // rotating door: closed angle (origin doesn't translate)
        else
            MapMover.SetOrigin(this_, this_.Pos1); // sliding door: snap to the closed origin

        this_.Velocity = Vector3.Zero;
        this_.AVelocity = Vector3.Zero;
        this_.MoverState = MapMover.StateBottom;
        this_.Active = MapMover.ActiveActive;
        this_.Think = null;
        this_.NextThink = 0f;

        // QC #ifdef SVQC: door_init_keys(this) — re-arm the gold/silver key bits.
        DoorInitKeys(this_);
        // (QC also raises SendFlags |= SF_TRIGGER_RESET for CSQC; door networking is latent/commented in Base.)
    }

    /// <summary>QC <c>door_init_startopen</c>: place at pos2, then swap pos1/pos2 so it runs in reverse.</summary>
    private static void DoorInitStartOpen(Entity this_)
    {
        MapMover.SetOrigin(this_, this_.Pos2);
        (this_.Pos1, this_.Pos2) = (this_.Origin, this_.Pos1);
    }

    // ================= LinkDoors =================

    // QC defers LinkDoors to INITPRIO_LINKDOORS (after every entity has spawned). Headless we collect the
    // freshly-spawned doors and link them once the registration pass calls RunDeferredLinks().
    private static readonly List<Entity> _pendingLink = new();

    private static void QueueLink(Entity door)
    {
        if (!_pendingLink.Contains(door))
            _pendingLink.Add(door);
    }

    /// <summary>
    /// Run LinkDoors for every door spawned since the last call (the headless analogue of QC's
    /// INITPRIO_LINKDOORS pass). The lead should call this once after spawning the BSP entity lump; it is
    /// also safe to call lazily — the first door touch/use will trigger it if it hasn't run yet.
    /// </summary>
    public static void RunDeferredLinks()
    {
        if (_pendingLink.Count == 0)
            return;
        var batch = _pendingLink.ToArray();
        _pendingLink.Clear();
        foreach (Entity d in batch)
            if (!d.IsFreed && d.Enemy is null)
                LinkDoors(d);
    }

    private static void EnsureLinked(Entity door)
    {
        if (door.Enemy is null)
        {
            _pendingLink.Remove(door);
            LinkDoors(door);
        }
    }

    /// <summary>
    /// Port of <c>LinkDoors</c> (door.qc): flood the doors that touch this one into a single owner/enemy
    /// loop (so a double/quad door opens as a unit), collect their shared health/targetname/message/size,
    /// and — unless the group is shootable / triggered / key-locked — spawn the fat door_spawnfield touch
    /// volume around the whole group.
    /// </summary>
    public static void LinkDoors(Entity this_)
    {
        if (this_.Enemy is not null)
            return; // already linked by another door

        if ((this_.SpawnFlags & DontLink) != 0)
        {
            this_.Owner = this_.Enemy = this_;
            if (this_.Health != 0f) return;
            if (!string.IsNullOrEmpty(this_.TargetName)) return;
            if (this_.Items != 0) return;
            DoorSpawnField(this_, this_.AbsMin, this_.AbsMax);
            return;
        }

        // Connected-component flood over doors of the same classname whose bboxes touch (+4u slop).
        MapMover.FindConnectedComponent(
            this_,
            setLink: (e, v) => e.Enemy = v,
            getLink: e => e.Enemy,
            next: LinkDoorsNextEnt,
            iscon: LinkDoorsIsConnected,
            pass: this_);

        // Set owner and close the chain into a loop (QC).
        for (Entity t = this_; ; t = t.Enemy!)
        {
            t.Owner = this_;
            if (t.Enemy is null) { t.Enemy = this_; break; }
        }

        // Collect health, targetname, message, and the union bbox.
        Vector3 cmins = this_.AbsMin, cmaxs = this_.AbsMax;
        for (Entity t = this_; ; t = t.Enemy!)
        {
            if (t.Health != 0f && this_.Health == 0f) this_.Health = t.Health;
            if (!string.IsNullOrEmpty(t.TargetName) && string.IsNullOrEmpty(this_.TargetName)) this_.TargetName = t.TargetName;
            if (!string.IsNullOrEmpty(t.Message) && string.IsNullOrEmpty(this_.Message)) this_.Message = t.Message;
            cmins = Vector3.Min(cmins, t.AbsMin);
            cmaxs = Vector3.Max(cmaxs, t.AbsMax);
            if (ReferenceEquals(t.Enemy, this_)) break;
        }

        // Distribute the collected health/targetname/message across the whole chain.
        for (Entity t = this_; ; t = t.Enemy!)
        {
            t.Health = this_.Health;
            t.MaxHealthMover = this_.Health;
            if (this_.Health != 0f) t.TakeDamage = DamageMode.Yes;
            MapMover.SetTargetName(t, this_.TargetName);
            t.Message = this_.Message;
            if (ReferenceEquals(t.Enemy, this_)) break;
        }

        // Shootable / triggered / key doors don't get a field; they're driven remotely.
        if (this_.Health != 0f) return;
        if (!string.IsNullOrEmpty(this_.TargetName)) return;
        if (this_.Items != 0) return;

        DoorSpawnField(this_, cmins, cmaxs);
    }

    /// <summary>QC <c>LinkDoors_nextent</c>: the next unlinked door of the same classname.</summary>
    private static Entity? LinkDoorsNextEnt(Entity? cursor, Entity near, Entity pass)
    {
        // walk doors of pass.classname, skipping DONT_LINK or already-linked ones.
        bool passedCursor = cursor is null;
        foreach (Entity e in MapMover.AllByClass(pass.ClassName))
        {
            if (!passedCursor)
            {
                if (ReferenceEquals(e, cursor)) passedCursor = true;
                continue;
            }
            if ((e.SpawnFlags & DontLink) != 0 || e.Enemy is not null)
                continue;
            return e;
        }
        return null;
    }

    /// <summary>QC <c>LinkDoors_isconnected</c>: bboxes overlap within a 4-unit slop.</summary>
    private static bool LinkDoorsIsConnected(Entity e1, Entity e2, Entity pass)
    {
        const float delta = 4f;
        if (e1.AbsMin.X > e2.AbsMax.X + delta || e1.AbsMin.Y > e2.AbsMax.Y + delta || e1.AbsMin.Z > e2.AbsMax.Z + delta
         || e2.AbsMin.X > e1.AbsMax.X + delta || e2.AbsMin.Y > e1.AbsMax.Y + delta || e2.AbsMin.Z > e1.AbsMax.Z + delta)
            return false;
        return true;
    }

    /// <summary>QC <c>door_spawnfield</c>: spawn the fat (mins-60..maxs+60) touch volume that opens the group.</summary>
    private static void DoorSpawnField(Entity this_, Vector3 fmins, Vector3 fmaxs)
    {
        if (Api.Services is null)
            return;
        Entity trigger = Api.Entities.Spawn();
        trigger.ClassName = "doortriggerfield";
        trigger.MoveType = MoveType.None;
        trigger.Solid = Solid.Trigger;
        trigger.Owner = this_;
        trigger.Touch = DoorTriggerTouch;
        MapMover.SetSize(trigger, fmins - new Vector3(60f, 60f, 8f), fmaxs + new Vector3(60f, 60f, 8f));
    }

    // ================= activation =================

    /// <summary>
    /// QC <c>door_use(this, actor, trigger)</c>: open (or, for TOGGLE doors that are open, close) the whole
    /// linked group. The QC third <c>trigger</c> argument (the entity that fired the door) is threaded via
    /// <see cref="MapMover.CurrentUseTrigger"/> — it is non-null only on the SUB_UseTargets path and carries
    /// the <c>.trigger_reverse</c> flag that drives bidirectional rotating doors.
    /// </summary>
    public static void DoorUse(Entity self, Entity actor)
    {
        EnsureLinked(self);            // safety net if the post-spawn link pass hasn't run yet
        Entity? owner = self.Owner;
        if (owner is null)
            return;
        if (owner.Active != MapMover.ActiveActive)
            return;
        Entity door = owner;

        // QC door_use's `trigger` parameter: the firing trigger (carries .trigger_reverse). NULL on
        // touch/damage/blocked opens — only set during a SUB_UseTargets dispatch.
        Entity? trigger = MapMover.CurrentUseTrigger;
        int triggerReverse = trigger?.TriggerReverse ?? 0;

        if ((door.SpawnFlags & Toggle) != 0
            && (door.MoverState == MapMover.StateUp || door.MoverState == MapMover.StateTop))
        {
            // close every door in the chain (QC do/while over .enemy)
            Entity e = door;
            do
            {
                DoorGoDownAny(e);
                e = e.Enemy!;
            } while (!ReferenceEquals(e, door) && e is not null);
            return;
        }

        // open every paired door in the chain.
        Entity o = door;
        do
        {
            if (o.ClassName == "door")
            {
                DoorGoUp(o, actor);
            }
            else
            {
                // door_rotating: DOOR_ROTATING_BIDIR reverses the swing direction when the firing trigger
                // set .trigger_reverse (QC door.qc:248-258).
                if ((o.SpawnFlags & RotatingBidir) != 0 && triggerReverse != 0
                    && o.Lip != 666f && o.MoverState == MapMover.StateBottom)
                {
                    o.Lip = 666f;            // remember the reverse-open direction (undone in HitBottom)
                    o.Pos2 = -o.Pos2;
                }

                // DOOR_ROTATING_BIDIR_IN_DOWN: while closing, don't let the door re-open if it is triggered
                // from the wrong side (QC door.qc:253-258).
                bool blockReopen = (o.SpawnFlags & RotatingBidir) != 0
                    && (o.SpawnFlags & RotatingBidirInDown) != 0
                    && o.MoverState == MapMover.StateDown
                    && ((o.Lip == 666f && triggerReverse == 0) || (o.Lip != 666f && triggerReverse != 0));

                if (!blockReopen)
                    DoorRotatingGoUp(o, actor);
            }
            o = o.Enemy!;
        } while (!ReferenceEquals(o, door) && o is not null);
    }

    /// <summary>QC <c>door_touch</c>: a player touching the door prints its message (touch-open is via the field).</summary>
    public static void DoorTouch(Entity self, Entity other)
    {
        Entity door = self.Owner ?? self;

        if ((other.Flags & EntFlags.Client) == 0)
            return;
        if (door.DoorFinished > MapMover.Now())
            return;
        if (door.Active != MapMover.ActiveActive)
            return;

        door.DoorFinished = MapMover.Now() + 2f;

        if (door.Dmg == 0f && !string.IsNullOrEmpty(door.Message))
        {
            // QC door.qc:313 centerprint(toucher, message) + play2(toucher, noise) — 2D VOL_BASE/ATTEN_NONE.
            MapMover.Centerprint(other, door.Message);
            MapMover.Play2(other, door.Noise);
        }
    }

    /// <summary>
    /// QC <c>door_trigger_touch</c>: the spawned field opens the linked door group when a live creature /
    /// projectile enters — if it isn't already up and the keys (if any) are satisfied.
    /// </summary>
    public static void DoorTriggerTouch(Entity self, Entity toucher)
    {
        Entity owner = self.Owner!;

        // QC: dead/zero-health non-creatures don't open it.
        if (toucher.GetResource(ResourceType.Health) < 1f
            && !((MapMover.IsCreature(toucher) || (toucher.Flags & EntFlags.Item) != 0) && !MapMover.IsDead(toucher)))
            return;
        if (owner.Active != MapMover.ActiveActive)
            return;
        if (owner.MoverState == MapMover.StateUp)
            return;

        // key lock check.
        if (!DoorCheckKeys(self, toucher))
            return;

        if (owner.MoverState == MapMover.StateTop)
        {
            // refresh the wait timer across the chain.
            if (owner.NextThink < owner.LTime + owner.Wait)
            {
                Entity e = owner;
                do { e.NextThink = e.LTime + e.Wait; e = e.Enemy!; } while (!ReferenceEquals(e, owner));
            }
            return;
        }

        DoorUse(owner, toucher);
    }

    /// <summary>QC <c>door_check_keys</c>: consume the player's matching keys; only opens once all are given.</summary>
    private static bool DoorCheckKeys(Entity field, Entity player)
    {
        Entity door = field.Owner ?? field;

        if (door.ItemKeys == 0)
            return true; // no key needed
        if ((player.Flags & EntFlags.Client) == 0)
            return false; // only a player can hold a key

        int valid = door.ItemKeys & player.ItemKeys;
        door.ItemKeys &= ~valid; // remove the keys that were supplied

        if (door.ItemKeys == 0)
        {
            // all required keys given -> unlock (door.qc:182-186).
            MapMover.Play2(player, door.Noise); // QC door.qc:183 play2(player, door.noise) — 2D VOL_BASE/ATTEN_NONE
            NotificationSystem.Send(NotifBroadcast.One, player, MsgType.Center, "DOOR_UNLOCKED");
            return true;
        }

        // Still missing keys: name them in the centerprint, throttled with the sound to once per 2s.
        // QC distinguishes the player who had NONE of the needed keys (valid==0 -> CENTER_DOOR_LOCKED_NEED,
        // "You need …") from the player who gave SOME but not all (-> CENTER_DOOR_LOCKED_ALSONEED, "You also
        // need …") (door.qc:189-209).
        if (player.KeyDoorMessageTime <= MapMover.Now())
        {
            MapMover.Play2(player, door.Noise3); // QC door.qc:194/206 play2(player, door.noise3) — 2D VOL_BASE/ATTEN_NONE
            string keylist = MapObjectsRegistry.ItemKeysKeylist(door.ItemKeys);
            NotificationSystem.Send(NotifBroadcast.One, player, MsgType.Center,
                valid == 0 ? "DOOR_LOCKED_NEED" : "DOOR_LOCKED_ALSONEED", keylist);
            player.KeyDoorMessageTime = MapMover.Now() + 2f;
        }
        return false;
    }

    // ---- per-classname go_up / go_down dispatch (sliding vs rotating) ----

    private static void DoorGoUpAny(Entity e, Entity? actor)
    {
        if (e.ClassName == "door") DoorGoUp(e, actor);
        else DoorRotatingGoUp(e, actor);
    }

    private static void DoorGoDownAny(Entity e)
    {
        if (e.ClassName == "door") DoorGoDown(e);
        else DoorRotatingGoDown(e);
    }

    /// <summary>QC <c>door_go_up</c>: slide to pos2 (open), fire targets, schedule auto-close after wait.</summary>
    public static void DoorGoUp(Entity this_, Entity? actor)
    {
        if (this_.MoverState == MapMover.StateUp)
            return; // already opening
        if (this_.MoverState == MapMover.StateTop)
        {
            this_.NextThink = this_.LTime + this_.Wait; // already open: reset the wait timer
            return;
        }

        MapMover.Sound(this_, SoundChannel.Voice, this_.Noise2);
        this_.MoverState = MapMover.StateUp;
        MapMover.CalcMove(this_, this_.Pos2, MapMover.SpeedType.Linear, this_.Speed, DoorHitTop);

        FireOpenTargets(this_, actor);
    }

    /// <summary>QC <c>door_go_down</c>: slide back to pos1 (closed); re-arm shootable doors.</summary>
    public static void DoorGoDown(Entity this_)
    {
        MapMover.Sound(this_, SoundChannel.Voice, this_.Noise2);
        if (this_.MaxHealthMover != 0f)
        {
            this_.TakeDamage = DamageMode.Yes;
            this_.Health = this_.MaxHealthMover;
        }
        this_.MoverState = MapMover.StateDown;
        MapMover.CalcMove(this_, this_.Pos1, MapMover.SpeedType.Linear, this_.Speed, DoorHitBottom);
    }

    /// <summary>QC <c>door_rotating_go_up</c>: swing to the open angle (pos2), fire targets, schedule close.</summary>
    public static void DoorRotatingGoUp(Entity this_, Entity? actor)
    {
        if (this_.MoverState == MapMover.StateUp)
            return;
        if (this_.MoverState == MapMover.StateTop)
        {
            this_.NextThink = this_.LTime + this_.Wait;
            return;
        }

        MapMover.Sound(this_, SoundChannel.Voice, this_.Noise2);
        this_.MoverState = MapMover.StateUp;
        MapMover.CalcAngleMove(this_, this_.Pos2, MapMover.SpeedType.Linear, this_.Speed, DoorRotatingHitTop);

        FireOpenTargets(this_, actor);
    }

    /// <summary>QC <c>door_rotating_go_down</c>: swing back to the closed angle (pos1); re-arm shootable doors.</summary>
    public static void DoorRotatingGoDown(Entity this_)
    {
        MapMover.Sound(this_, SoundChannel.Voice, this_.Noise2);
        if (this_.MaxHealthMover != 0f)
        {
            this_.TakeDamage = DamageMode.Yes;
            this_.Health = this_.MaxHealthMover;
        }
        this_.MoverState = MapMover.StateDown;
        MapMover.CalcAngleMove(this_, this_.Pos1, MapMover.SpeedType.Linear, this_.Speed, DoorRotatingHitBottom);
    }

    /// <summary>Fire a door's open-targets (QC blanks .message so the touch message isn't doubled).</summary>
    private static void FireOpenTargets(Entity this_, Entity? actor)
    {
        string oldMessage = this_.Message;
        this_.Message = "";
        MapMover.UseTargets(this_, actor, null);
        this_.Message = oldMessage;
    }

    /// <summary>QC <c>door_hit_top</c>: reached open; TOGGLE stays, otherwise auto-close after wait.</summary>
    private static void DoorHitTop(Entity this_)
    {
        MapMover.Sound(this_, SoundChannel.Voice, this_.Noise1);
        this_.MoverState = MapMover.StateTop;
        if ((this_.SpawnFlags & Toggle) != 0)
            return; // don't come down automatically
        if (this_.Wait < 0f)
            return; // wait == -1: never return
        this_.Think = DoorGoDown;
        this_.NextThink = this_.LTime + this_.Wait;
    }

    /// <summary>QC <c>door_hit_bottom</c>: reached closed.</summary>
    private static void DoorHitBottom(Entity this_)
    {
        MapMover.Sound(this_, SoundChannel.Voice, this_.Noise1);
        this_.MoverState = MapMover.StateBottom;
    }

    /// <summary>QC <c>door_rotating_hit_top</c>.</summary>
    private static void DoorRotatingHitTop(Entity this_)
    {
        MapMover.Sound(this_, SoundChannel.Voice, this_.Noise1);
        this_.MoverState = MapMover.StateTop;
        if ((this_.SpawnFlags & Toggle) != 0)
            return;
        if (this_.Wait < 0f)
            return;
        this_.Think = DoorRotatingGoDown;
        this_.NextThink = this_.LTime + this_.Wait;
    }

    /// <summary>QC <c>door_rotating_hit_bottom</c>: undo a BIDIR reverse-open if one was applied.</summary>
    private static void DoorRotatingHitBottom(Entity this_)
    {
        MapMover.Sound(this_, SoundChannel.Voice, this_.Noise1);
        if (this_.Lip == 666f) // reverse-open marker
        {
            this_.Pos2 = -this_.Pos2;
            this_.Lip = 0f;
        }
        this_.MoverState = MapMover.StateBottom;
    }

    /// <summary>QC <c>door_blocked</c>: bite/crush a blocker and reverse direction (unless wait &lt; 0).</summary>
    public static void DoorBlocked(Entity self, Entity blocker)
    {
        if ((self.SpawnFlags & Crush) != 0 && blocker.TakeDamage != DamageMode.No)
        {
            Combat.Damage(blocker, self, self, 10000f, DeathTypes.Void, blocker.Origin, Vector3.Zero);
        }
        else
        {
            if (self.Dmg != 0f && blocker.TakeDamage != DamageMode.No)
                Combat.Damage(blocker, self, self, self.Dmg, DeathTypes.Void, blocker.Origin, Vector3.Zero);

            // reverse direction for live blockers (don't bounce off dead/dying stuff)
            if (!MapMover.IsDead(blocker) && blocker.TakeDamage != DamageMode.No && self.Wait >= 0f)
            {
                if (self.MoverState == MapMover.StateDown)
                    DoorGoUpAny(self, null);
                else
                    DoorGoDownAny(self);
            }
            // QC door_blocked: a live blocker on a never-return / TOGGLE door (wait < 0) does NOT reverse the
            // door; instead SUB_CalcMovePause(this) freezes the eased mover on its easing curve so it doesn't
            // lurch forward when the blocker clears (linear doors have no controller, so this is a no-op there).
            else if (!MapMover.IsDead(blocker) && blocker.TakeDamage != DamageMode.No && self.Wait < 0f)
            {
                MapMover.CalcMovePause(self);
            }
            else if (self.Dmg != 0f && blocker.TakeDamage != DamageMode.No && MapMover.IsDead(blocker))
            {
                Combat.Damage(blocker, self, self, 10000f, DeathTypes.Void, blocker.Origin, Vector3.Zero); // gib
            }
        }
    }

    // ================= shootable doors via the damage pipeline =================

    /// <summary>
    /// Subscribe once to <see cref="Combat.Death"/>. The headless DamageSystem has no per-entity
    /// <c>event_damage</c>; instead, when a shootable door's health is driven below 1 the pipeline fires the
    /// Death hook BEFORE corpse-ifying. We restore the owner's health (so the kill path's "resuscitated to
    /// HP&gt;=1, don't die" early-out leaves the door intact) and open it — exactly QC's <c>door_damage</c>.
    /// </summary>
    private static void EnsureDeathHook()
    {
        if (_deathHooked)
            return;
        _deathHooked = true;
        Combat.Death.Add(OnDeath);
    }

    private static bool OnDeath(ref DeathEvent ev)
    {
        Entity v = ev.Victim;
        if (v.ClassName is not ("door" or "door_rotating"))
            return false;
        Entity door = v.Owner ?? v;

        // NOSPLASH: a non-special splash (blast) hit deals no damage to the door (QC door_damage
        // early-returns before TakeResource). The pipeline already drove health below 1, so resuscitate
        // the owner and leave it closed — the kill path's "resuscitated to HP>=1, don't die" early-out
        // keeps the brush intact and the door never opens from splash.
        // QC reads NOSPLASH off the hit brush (`this`), i.e. the victim, not the group owner.
        if ((v.SpawnFlags & NoSplash) != 0
            && !DeathTypes.IsSpecial(ev.DeathType) && DeathTypes.HasHitType(ev.DeathType, DeathTypes.Splash))
        {
            door.Health = door.MaxHealthMover;
            return false;
        }

        // key doors can't be damage-opened (QC door_damage early-returns when itemkeys remain).
        if (door.ItemKeys != 0)
        {
            door.Health = door.MaxHealthMover; // keep it alive so the kill path's early-out fires
            return false;
        }

        door.Health = door.MaxHealthMover;       // SetResourceExplicit(owner, max_health)
        door.TakeDamage = DamageMode.No;         // will be reset on close
        DoorUse(door, ev.Attacker ?? door);
        return false; // non-exclusive: other death subscribers still run
    }
}
