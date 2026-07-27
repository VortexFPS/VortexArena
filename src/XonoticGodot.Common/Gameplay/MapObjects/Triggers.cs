// Port of the touch/relay trigger families from qcsrc/common/mapobjects/trigger/:
//   multi.qc    -> trigger_multiple, trigger_once
//   hurt.qc     -> trigger_hurt
//   heal.qc     -> trigger_heal, target_heal
//   gravity.qc  -> trigger_gravity (+ the exit-checker companion)
//   counter.qc  -> trigger_counter (+ COUNTER_PER_PLAYER)
//   relay.qc    -> trigger_relay, target_relay, target_delay
//   delay.qc    -> trigger_delay
//   secret.qc   -> trigger_secret
//   swamp.qc    -> trigger_swamp
//   impulse.qc  -> trigger_impulse (directional / accel / radial force fields)
//   keylock.qc  -> trigger_keylock
//
// These are volume/relay entities that fire their targets (SUB_UseTargets) on touch, on use, or on a
// schedule. trigger_hurt/heal also act directly on the toucher. Ported in full now: shootable triggers
// (multi_eventdamage via the damage pipeline's Death hook), the gravity-zone exit checker entity that
// restores gravity when a non-sticky zone is left, per-player counters + the "sequence" notifications
// (audible/secret-count parts), and the secret/swamp/impulse/keylock families. Genuinely out of scope:
// warpzone exact-trigger clipping and CSQC networking. CTS per-client .wait buffers are now ported
// (trigger_multiple / multi_trigger CTS branch keyed by Entity.Index via CtsTriggerTimes Dictionary).

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>The touch/relay/hurt/heal/gravity/counter/delay/secret/swamp/impulse/keylock trigger entities. Each setup is a spawnfunc.</summary>
public static class Triggers
{
    private static bool _deathHooked;

    /// <summary>Subscribe once to <see cref="Combat.Death"/> for shootable triggers (multi_eventdamage).</summary>
    private static void EnsureDeathHook()
    {
        if (_deathHooked)
            return;
        _deathHooked = true;
        Combat.Death.Add(OnDeath);
    }

    /// <summary>
    /// <see cref="Combat.Death"/> handler: a shootable trigger_multiple/once whose health is driven below 1
    /// fires (QC multi_eventdamage). We restore its health so the kill path's HP&gt;=1 early-out keeps the
    /// volume intact, then fire it with the attacker as activator.
    /// </summary>
    private static bool OnDeath(ref DeathEvent ev)
    {
        Entity self = ev.Victim;
        if (self.ClassName is not ("trigger_multiple" or "trigger_once"))
            return false;
        if (self.TakeDamage == DamageMode.No || self.MaxHealthMover == 0f)
            return false;

        // QC multi_eventdamage: a NOSPLASH trigger ignores indirect (non-special, splash) damage so it can't
        // be set off by nearby blasts — only by a direct shot.
        if ((self.SpawnFlags & MapMover.SpawnNoSplash) != 0
            && !DeathTypes.IsSpecial(ev.DeathType) && DeathTypes.HasHitType(ev.DeathType, DeathTypes.Splash))
            return false;

        // QC multi_eventdamage: a team-restricted shootable trigger only fires for a matching-team attacker
        // (or the opposite team with INVERT_TEAMS), mirroring the multi_touch team gate.
        if (self.Team != 0f && ev.Attacker is not null)
        {
            bool noInvert = (self.SpawnFlags & MapMover.SpawnInvertTeams) == 0;
            if (noInvert == (self.Team != ev.Attacker.Team))
                return false;
        }

        self.Health = self.MaxHealthMover; // keep it alive so the kill path's early-out fires
        self.Enemy = ev.Attacker;
        self.GoalEntity = ev.Inflictor;
        MultiTrigger(self);
        return false; // non-exclusive
    }

    // ===================================================================
    //  trigger_multiple / trigger_once
    // ===================================================================

    /// <summary><c>spawnfunc(trigger_multiple)</c> — repeatable volume trigger that fires its targets on touch.</summary>
    public static void MultipleSetup(Entity this_)
    {
        EnsureDeathHook();

        // sounds 1/2/3 -> the secret/talk/switch noises (QC).
        if (this_.Sounds == 1) this_.Noise = "misc/secret.wav";
        else if (this_.Sounds == 2) this_.Noise = "misc/talk.wav";
        else if (this_.Sounds == 3) this_.Noise = "misc/trigger1.wav";

        MapMover.InitTrigger(this_);
        this_.ClassName = "trigger_multiple";

        // QC spawnfunc(trigger_multiple): q3compat default .wait is 0.5 (vs Xonotic's 0.2). In q3compat
        // the field also waits forever when explicitly set to 0 in the map; the port cannot distinguish
        // "not set" from "set to 0" (both arrive as 0f), so we only apply the 0.5 default branch here.
        // The explicit-0-means-forever edge is recorded as a residual gap (negligible: rare q3 map pattern).
        bool isQ3 = CompatRemaps.IsQ3Compat;
        if (this_.Wait == 0f)
            this_.Wait = isQ3 ? 0.5f : 0.2f;

        this_.Use = MultiUse;

        // QC spawnfunc(trigger_multiple): in CTS, health/damage mode is not supported (multi.qc:230-233) and
        // instead we allocate the per-client .triggertimes buffer (multi.qc:246-248 `buf_create()`).
        bool isCts = CompatRemaps.IsCtsActive?.Invoke() ?? false;
        if (this_.Health != 0f && !isCts)
        {
            // Shootable trigger (health): killed to fire (event_damage = multi_eventdamage). Otherwise touch.
            this_.MaxHealthMover = this_.Health;
            this_.TakeDamage = DamageMode.Yes;
            this_.Solid = Solid.BBox;
            MapMover.SetOrigin(this_, this_.Origin); // make sure it links into the world
        }
        else if ((this_.SpawnFlags & MapMover.SpawnNoTouch) == 0)
        {
            this_.Touch = MultiTouch;
            // QC CTS branch: allocate the per-client wait-time dictionary (port of `buf_create()` +
            // bufstr_get/set keyed by etof(enemy)). Non-CTS: CtsTriggerTimes stays null (shared nextthink).
            if (isCts)
                this_.CtsTriggerTimes = new System.Collections.Generic.Dictionary<int, float>();
        }

        MapMover.IndexRegister(this_);
    }

    /// <summary><c>spawnfunc(trigger_once)</c> — like trigger_multiple but wait = -1 (fires once, then disabled).</summary>
    public static void OnceSetup(Entity this_)
    {
        this_.Wait = -1f;
        MultipleSetup(this_);
        this_.ClassName = "trigger_once";
    }

    /// <summary>QC <c>multi_use</c>: an external trigger fires this with the actor as activator.</summary>
    private static void MultiUse(Entity self, Entity actor)
    {
        self.GoalEntity = actor;
        self.Enemy = actor;
        MultiTrigger(self);
    }

    /// <summary>QC <c>multi_touch</c>: a creature touching the volume fires it (with facing/team checks).</summary>
    private static void MultiTouch(Entity self, Entity toucher)
    {
        if ((self.SpawnFlags & MapMover.SpawnAllEntities) == 0 && !MapMover.IsCreature(toucher))
            return;

        // QC multi_touch:110-117 q3compat red/blue spawnflag-1/2 team filter: in a 2-team Q3 map,
        // spawnflag bit 1 = red (NUM_TEAM_1) only, bit 2 = blue (NUM_TEAM_2) only. Xonotic maps
        // repurpose those bits for other meanings, so this gate is ONLY applied when q3compat is active.
        if (CompatRemaps.IsQ3Compat)
        {
            // AVAILABLE_TEAMS == 2 (2-team game) is implied for Q3 CTF maps; apply the filter when
            // either bit is set (mirroring QC `if(((f1)&&t1!=R)||((f2)&&t2!=B)) return`).
            if (((self.SpawnFlags & 1) != 0 && (int)toucher.Team != 1)    // spawnflag 1 = red team (NUM_TEAM_1 = 1)
            ||  ((self.SpawnFlags & 2) != 0 && (int)toucher.Team != 2))   // spawnflag 2 = blue team (NUM_TEAM_2 = 2)
                return;
        }
        else if (self.Team != 0f)
        {
            // team gate (QC multi_touch): a team-restricted trigger only fires for the matching team
            // (or the opposite team with INVERT_TEAMS). Fire only when
            //   (no-invert AND same-team) OR (invert AND different-team).
            bool noInvert = (self.SpawnFlags & MapMover.SpawnInvertTeams) == 0;
            if (noInvert == (self.Team != toucher.Team))
                return;
        }

        // facing check: if the trigger has a movedir ("angle"), the toucher must face roughly that way.
        if (self.MoveDir != Vector3.Zero)
        {
            Vector3 fwd = QMath.Forward(toucher.Angles);
            if (QMath.Dot(fwd, self.MoveDir) < 0f)
                return;
        }

        // pressed-keys gate (QC multi_touch:135-137): a key-gated trigger only fires while the PLAYER holds one
        // of the required keys. this.TriggerPressedKeys is the map-parsed mask (movement-key bits); the player's
        // live key state rides toucher.PressedKeys (written each frame by the input/dodging layer). Non-player
        // touchers ignore the gate (QC IS_PLAYER(toucher)).
        if (self.TriggerPressedKeys != 0 && (toucher.Flags & EntFlags.Client) != 0)
        {
            if ((toucher.PressedKeys & self.TriggerPressedKeys) == 0)
                return;
        }

        self.Enemy = toucher;
        self.GoalEntity = toucher;
        MultiTrigger(self);
    }

    /// <summary>QC <c>multi_trigger</c>: respect the rearm timer, fire targets, then schedule rearm/disable.</summary>
    public static void MultiTrigger(Entity self)
    {
        if ((self.SpawnFlags & MapMover.SpawnOnlyPlayers) != 0 && (self.Enemy?.Flags & EntFlags.Client) == 0)
            return; // only players

        float now = MapMover.Now();
        Entity? enemy = self.Enemy;

        // CTS per-client timing (QC multi_trigger:31-45): in CTS each client has its own independent
        // re-trigger window keyed by etof(enemy) (= Entity.Index). The check is:
        //   (a) if the client has a stored time AND hasn't respawned since → apply the wait/forever check
        //   (b) otherwise (no prior record, or re-spawned) → allow firing
        // Outside CTS the shared nextthink is used (same as before).
        if (self.CtsTriggerTimes is { } ctsTimes && enemy is not null && (enemy.Flags & EntFlags.Client) != 0)
        {
            int cnum = enemy.Index;
            if (ctsTimes.TryGetValue(cnum, out float storedTime))
            {
                // QC: `if (this.enemy.spawn_time <= triggertime)` → the player hasn't respawned since
                // triggering, so the wait check applies. If they DID respawn (spawn_time > storedTime)
                // the slot is stale — allow firing regardless of wait.
                float spawnTime = enemy is Player p ? p.SpawnTime : 0f;
                if (spawnTime <= storedTime)
                {
                    // Haven't respawned; apply the normal wait check.
                    // QC: `wait <= 0 && (q3compat || wait >= -1)` → wait forever; `triggertime + wait > time` → too soon.
                    // The port has no q3compat path, so the forever condition is `wait <= 0 && wait >= -1`
                    // (i.e. wait in the [−1, 0] range that is NOT the xon no-wait branch).
                    if ((self.Wait <= 0f && self.Wait >= -1f) || storedTime + self.Wait > now)
                        return;
                }
            }
        }
        else
        {
            // Non-CTS: standard single shared timer check.
            if (self.NextThink > now)
                return; // already triggered, still in the wait window
        }

        MapMover.Sound(enemy ?? self, SoundChannel.Auto, self.Noise);

        self.TakeDamage = DamageMode.No; // don't re-trigger via damage until reset

        MapMover.UseTargets(self, enemy, self.GoalEntity);

        // After firing: schedule rearm or disable, per-client or shared.
        if (self.CtsTriggerTimes is { } ctsTimesPost && enemy is not null && (enemy.Flags & EntFlags.Client) != 0)
        {
            // CTS: store the trigger time for this specific client (QC bufstr_set/bufstr_add with etof−1 index).
            ctsTimesPost[enemy.Index] = now;

            // QC CTS else-branch (multi.qc:84-86): disable for NON-CLIENT touchers the same as non-CTS wait==−1.
            // For clients, CTS triggers are always per-client: `this.nextthink = stof("inf")` blocks non-client
            // re-triggers via the shared check, but that shared check is bypassed when CtsTriggerTimes != null.
            // We keep NextThink at float.MaxValue to block the rare non-client path through the shared branch:
            self.NextThink = float.MaxValue;
        }
        else if (self.Wait > 0f)
        {
            this_SetRearm(self);
        }
        else if (self.Wait < -1f)
        {
            MultiWait(self); // xon "no wait" (wait == -2): rearm immediately
        }
        else
        {
            // wait == -1: fire once. Disable touch/use (can't delete inside a touch callback).
            self.Touch = null;
            self.Use = null;
        }
    }

    private static void this_SetRearm(Entity self)
    {
        self.Think = MultiWait;
        self.NextThink = MapMover.Now() + self.Wait;
    }

    /// <summary>QC <c>multi_wait</c>: the wait elapsed; re-arm a shootable trigger.</summary>
    private static void MultiWait(Entity self)
    {
        if (self.MaxHealthMover != 0f)
        {
            self.Health = self.MaxHealthMover;
            self.TakeDamage = DamageMode.Yes;
            self.Solid = Solid.BBox;
        }
    }

    // ===================================================================
    //  trigger_hurt
    // ===================================================================

    /// <summary>QC hurt.qh <c>SPAWNFLAG_HURT_SLOW</c> = BIT(0): keep the 1s creature cooldown even under q3compat.</summary>
    public const int HurtSlow = 1 << 0; // SPAWNFLAG_HURT_SLOW

    /// <summary><c>spawnfunc(trigger_hurt)</c> — damages anything damageable that touches it.</summary>
    public static void HurtSetup(Entity this_)
    {
        MapMover.InitTrigger(this_);
        this_.ClassName = "trigger_hurt";
        this_.Active = MapMover.ActiveActive;
        this_.Touch = HurtTouch;
        this_.Use = HurtUse;
        this_.Enemy = null; // "I hate you all" — attribute to the world unless taken over
        // QC spawnfunc(trigger_hurt): default .dmg is 10000 on Xonotic maps but 5 on a q3compat import
        // (the Q3 trigger_hurt is a gentle damage volume, not the instant-death pit Xonotic uses).
        if (this_.Dmg == 0f)
            this_.Dmg = CompatRemaps.IsQ3Compat ? 5f : 10000f;
        if (string.IsNullOrEmpty(this_.Message))
            this_.Message = "was in the wrong place";
        MapMover.IndexRegister(this_);
    }

    /// <summary>QC <c>trigger_hurt_use</c>: a player using it becomes the credited attacker.</summary>
    private static void HurtUse(Entity self, Entity actor)
    {
        self.Enemy = (actor.Flags & EntFlags.Client) != 0 ? actor : null;
    }

    /// <summary>QC <c>trigger_hurt_touch</c>: bite the toucher for `.dmg` on a per-toucher cooldown.</summary>
    private static void HurtTouch(Entity self, Entity toucher)
    {
        if (toucher.TakeDamage == DamageMode.No)
            return;
        if (self.Active != MapMover.ActiveActive)
            return;

        // team gate (QC trigger_hurt_touch): a team-restricted hurt volume only bites the matching team
        // (or the opposite team with INVERT_TEAMS).
        if (self.Team != 0f)
        {
            bool noInvert = (self.SpawnFlags & MapMover.SpawnInvertTeams) == 0;
            if (noInvert == (self.Team != toucher.Team))
                return;
        }

        if (MapMover.IsCreature(toucher))
        {
            // QC throttles creatures to one hit per `gametime` cooldown (per toucher): 1s on stock Xonotic,
            // but a fast 0.05s under q3compat UNLESS SPAWNFLAG_HURT_SLOW is set (hurt.qc creature branch). A
            // q3 trigger_hurt is a low-damage volume that bites rapidly; HURT_SLOW restores the 1s cadence.
            float cooldown = (CompatRemaps.IsQ3Compat && (self.SpawnFlags & HurtSlow) == 0) ? 0.05f : 1f;
            if (MapMover.Now() >= toucher.TriggerHurtTime + cooldown)
            {
                toucher.TriggerHurtTime = MapMover.Now();
                Entity attacker = (self.Enemy is not null && (self.Enemy.Flags & EntFlags.Client) != 0)
                    ? self.Enemy
                    : self;
                Combat.Damage(toucher, self, attacker, self.Dmg, DeathTypes.Void, toucher.Origin, Vector3.Zero);
            }
        }
        else if (MapMover.IsPushable(toucher))
        {
            // QC gates non-creatures on toucher.damagedbytriggers (projectiles/loot/bodies); the port has no
            // such field, so IsPushable stands in for it (same set, matching the TargetUtilities convention).
            // Those take damage every touch.
            Combat.Damage(toucher, self, self, self.Dmg, DeathTypes.Void, toucher.Origin, Vector3.Zero);
        }
    }

    // ===================================================================
    //  trigger_heal / target_heal
    // ===================================================================

    /// <summary><c>spawnfunc(trigger_heal)</c> — heals creatures that touch it, up to a topoff cap.</summary>
    public static void HealSetup(Entity this_)
    {
        MapMover.InitTrigger(this_);
        this_.ClassName = "trigger_heal";
        this_.Touch = HealTouch;
        HealInit(this_);
    }

    /// <summary><c>spawnfunc(target_heal)</c> — the use-activated variant of trigger_heal.</summary>
    public static void TargetHealSetup(Entity this_)
    {
        this_.ClassName = "target_heal";
        this_.Use = (self, actor) => HealTouch(self, actor);
        HealInit(this_);
    }

    private static void HealInit(Entity this_)
    {
        this_.Active = MapMover.ActiveActive;
        if (this_.Delay == 0f) this_.Delay = 1f;
        if (this_.Health == 0f) this_.Health = 10f;          // heal-per-tick amount
        if (this_.MaxHealthMover == 0f) this_.MaxHealthMover = 200f; // topoff cap
        if (string.IsNullOrEmpty(this_.Noise)) this_.Noise = "misc/mediumhealth.wav";
        MapMover.IndexRegister(this_);
    }

    /// <summary>QC <c>trigger_heal_touch</c>: top up the creature's health up to the cap on a cooldown.</summary>
    private static void HealTouch(Entity self, Entity toucher)
    {
        if (self.Active != MapMover.ActiveActive)
            return;
        if (!MapMover.IsCreature(toucher))
            return;
        if (toucher.TakeDamage == DamageMode.No || MapMover.IsDead(toucher))
            return;
        if (toucher.TriggerHealTime >= MapMover.Now())
            return;

        if (self.Delay > 0f)
            toucher.TriggerHealTime = MapMover.Now() + self.Delay;

        // QC Heal(targ, src, amount, limit): give up to `limit` total (the field topoff). Returns whether
        // any health was actually added (the toucher was below the cap).
        bool playTheSound = (self.SpawnFlags & HealSoundAlways) != 0;
        float before = toucher.GetResource(ResourceType.Health);
        bool healed = before < self.MaxHealthMover;
        if (healed)
            toucher.GiveResourceWithLimit(ResourceType.Health, self.Health, self.MaxHealthMover);

        // QC: play the heal sound when HEAL_SOUND_ALWAYS is set OR a heal actually happened (so a topped-off
        // creature in a HEAL_SOUND_ALWAYS field still gets the cue).
        if (playTheSound || healed)
            MapMover.Sound(toucher, SoundChannel.Auto, self.Noise);
    }

    public const int HealSoundAlways = 1 << 2; // HEAL_SOUND_ALWAYS

    // ===================================================================
    //  trigger_gravity
    // ===================================================================

    public const int GravitySticky = 1 << 0;        // GRAVITY_STICKY
    public const int GravityStartDisabled = 1 << 1; // GRAVITY_START_DISABLED

    /// <summary>
    /// <c>spawnfunc(trigger_gravity)</c> — sets the gravity multiplier on creatures inside the volume. STICKY
    /// zones leave the new gravity on the toucher forever; non-sticky zones spawn a per-toucher checker that
    /// restores the original gravity once the toucher leaves (with .cnt zone-priority arbitration).
    /// </summary>
    public static void GravitySetup(Entity this_)
    {
        if (this_.Gravity == 1f)
            return; // no-op zone

        MapMover.InitTrigger(this_);
        this_.ClassName = "trigger_gravity";
        this_.Touch = GravityTouch;
        this_.Active = MapMover.ActiveActive;
        if (!string.IsNullOrEmpty(this_.TargetName))
        {
            this_.Use = GravityUse; // legacy: a trigger toggles the zone on/off
            if ((this_.SpawnFlags & GravityStartDisabled) != 0)
                this_.Active = MapMover.ActiveNot;
        }
        MapMover.IndexRegister(this_);
    }

    /// <summary>QC <c>trigger_gravity_use</c>: toggle the zone active/inactive.</summary>
    private static void GravityUse(Entity self, Entity actor)
    {
        self.Active = self.Active == MapMover.ActiveActive ? MapMover.ActiveNot : MapMover.ActiveActive;
    }

    /// <summary>
    /// QC <c>trigger_gravity_touch</c>: apply the zone gravity to the toucher. For a non-sticky zone, attach
    /// (or refresh) a <see cref="GravityCheckThink"/> watchdog that restores the toucher's original gravity
    /// when it stops touching; higher-priority zones (larger <c>.cnt</c>) win arbitration.
    /// </summary>
    private static void GravityTouch(Entity self, Entity toucher)
    {
        if (self.Active == MapMover.ActiveNot)
            return;

        float g = self.Gravity;

        if ((self.SpawnFlags & GravitySticky) == 0)
        {
            if (toucher.GravityCheck is not null)
            {
                if (ReferenceEquals(self, toucher.GravityCheck.Enemy))
                {
                    // same zone: keep gravity for one more frame.
                    toucher.GravityCheck.Cnt = 2;
                    return;
                }
                // a different zone: only override if this one has higher priority (.cnt).
                if (self.Cnt > toucher.GravityCheck.Enemy!.Cnt)
                    GravityRemove(toucher);
                else
                    return;
            }

            Entity checker = Api.Services is not null ? Api.Entities.Spawn() : new Entity();
            checker.ClassName = "trigger_gravity_check";
            checker.Enemy = self;
            checker.Owner = toucher;
            checker.GravityRestore = toucher.Gravity; // remember what to put back
            checker.Think = GravityCheckThink;
            checker.NextThink = MapMover.Now();
            checker.Cnt = 2;
            toucher.GravityCheck = checker;

            if (toucher.Gravity != 0f)
                g *= toucher.Gravity; // multiply onto the toucher's existing gravity
        }

        if (toucher.Gravity != g)
        {
            toucher.Gravity = g;
            MapMover.Sound(toucher, SoundChannel.Auto, self.Noise);
        }
    }

    /// <summary>
    /// QC <c>trigger_gravity_check_think</c>: each frame <see cref="GravityTouch"/> resets <c>.cnt</c> to 2;
    /// here it decrements. If the toucher has left the zone, <c>.cnt</c> reaches 0 and we restore gravity.
    /// </summary>
    private static void GravityCheckThink(Entity self)
    {
        if (self.Cnt <= 0)
        {
            Entity owner = self.Owner!;
            if (ReferenceEquals(owner.GravityCheck, self))
                GravityRemove(owner);
            else
                MapMover.RemoveEntity(self);
            return;
        }
        --self.Cnt;
        self.NextThink = MapMover.Now();
    }

    /// <summary>QC <c>trigger_gravity_remove</c>: restore the toucher's saved gravity and drop the checker.</summary>
    private static void GravityRemove(Entity toucher)
    {
        Entity? checker = toucher.GravityCheck;
        if (checker is not null && ReferenceEquals(checker.Owner, toucher))
        {
            toucher.Gravity = checker.GravityRestore;
            MapMover.RemoveEntity(checker);
        }
        toucher.GravityCheck = null;
    }

    // ===================================================================
    //  trigger_counter
    // ===================================================================

    public const int CounterFireAtCount = 1 << 2; // COUNTER_FIRE_AT_COUNT — also fire targets at countdown
    public const int CounterPerPlayer = 1 << 3;   // COUNTER_PER_PLAYER — each player has their own count
    public const int CounterNoMessage = 1 << 0;   // SPAWNFLAG_NOMESSAGE — suppress the "N more"/complete prints

    /// <summary>
    /// <c>spawnfunc(trigger_counter)</c> — fires its targets only after being used `.count` times (default 2).
    /// Supports COUNTER_PER_PLAYER (each player accumulates independently via a per-(counter,player) store)
    /// and the "N more.."/"sequence complete" notifications (the audible + secret-count parts).
    /// </summary>
    public static void CounterSetup(Entity this_)
    {
        if (this_.Count == 0)
            this_.Count = 2;
        this_.CounterCnt = 0;
        this_.Use = CounterUse;
        this_.Active = MapMover.ActiveActive;
        this_.ClassName = "trigger_counter";
        MapMover.IndexRegister(this_);
    }

    /// <summary>QC <c>counter_use</c>: tick the (shared or per-player) counter; fire targets when it reaches `.count`.</summary>
    private static void CounterUse(Entity self, Entity actor)
    {
        if (self.Active != MapMover.ActiveActive)
            return;

        // Choose the store: shared on the trigger, or a per-(trigger,player) sub-entity.
        Entity store = self;
        if ((self.SpawnFlags & CounterPerPlayer) != 0)
        {
            if ((actor.Flags & EntFlags.Client) == 0)
                return; // only players
            store = GetOrCreatePerPlayerCounter(self, actor);
        }

        ++store.CounterCnt;
        if (store.CounterCnt > self.Count)
            return;

        bool doActivate = (self.SpawnFlags & CounterFireAtCount) != 0;
        bool announce = (actor.Flags & EntFlags.Client) != 0 && (self.SpawnFlags & CounterNoMessage) == 0;

        if (store.CounterCnt == self.Count)
        {
            // CENTER_SEQUENCE_COMPLETED — a text-only MSG_CENTER NOTIF_ONE notification in QC (no sound).
            if (announce)
                NotificationSystem.Send(NotifBroadcast.One, actor, MsgType.Center, "SEQUENCE_COMPLETED");

            doActivate = true;

            if (self.RespawnTimeMover != 0f)
            {
                self.Think = CounterReset;
                self.NextThink = MapMover.Now() + self.RespawnTimeMover;
            }
        }
        else if (announce)
        {
            // CENTER_SEQUENCE_COUNTER (>=4 to go) / _FEWMORE (<4, with the remaining count) — text-only
            // MSG_CENTER NOTIF_ONE notifications in QC (no sound). (counter.qc:53-59)
            int remaining = self.Count - store.CounterCnt;
            if (remaining >= 4)
                NotificationSystem.Send(NotifBroadcast.One, actor, MsgType.Center, "SEQUENCE_COUNTER");
            else
                NotificationSystem.Send(NotifBroadcast.One, actor, MsgType.Center, "SEQUENCE_COUNTER_FEWMORE", (float)remaining);
        }

        if (doActivate)
            MapMover.UseTargets(self, actor, null);
    }

    /// <summary>
    /// Find/create this player's private counter store for a COUNTER_PER_PLAYER trigger (QC g_counters IL).
    /// The store's <see cref="Entity.Owner"/> is the counter; its <see cref="Entity.Aiment"/> is the player
    /// (QC uses <c>.realowner</c>, which is a read-only alias of Owner in this port, so we use Aiment).
    /// </summary>
    private static Entity GetOrCreatePerPlayerCounter(Entity counter, Entity actor)
    {
        foreach (Entity c in MapObjectsState.Counters)
            if (ReferenceEquals(c.Owner, counter) && ReferenceEquals(c.Aiment, actor))
                return c;

        Entity store = Api.Services is not null ? Api.Entities.Spawn() : new Entity();
        store.ClassName = "counter";
        store.Owner = counter;
        store.Aiment = actor;
        store.CounterCnt = 0;
        MapObjectsState.Counters.Add(store);
        return store;
    }

    /// <summary>QC <c>counter_reset</c>: clear the shared count + remove all per-player stores.</summary>
    private static void CounterReset(Entity self)
    {
        self.Think = null;
        self.NextThink = 0f;
        self.CounterCnt = 0;
        self.Active = MapMover.ActiveActive;

        for (int i = MapObjectsState.Counters.Count - 1; i >= 0; i--)
        {
            Entity c = MapObjectsState.Counters[i];
            if (ReferenceEquals(c.Owner, self))
            {
                MapObjectsState.Counters.RemoveAt(i);
                MapMover.RemoveEntity(c);
            }
        }
    }

    // ===================================================================
    //  trigger_relay / target_relay / target_delay / trigger_delay
    // ===================================================================

    /// <summary><c>spawnfunc(trigger_relay)</c> / <c>target_relay</c> — fires its targets when used (a pure relay).</summary>
    public static void RelaySetup(Entity this_)
    {
        this_.Active = MapMover.ActiveActive;
        this_.Use = RelayUse;
        this_.ClassName = "trigger_relay";
        MapMover.IndexRegister(this_);
    }

    /// <summary>QC <c>relay_use</c>.</summary>
    private static void RelayUse(Entity self, Entity actor)
    {
        if (self.Active != MapMover.ActiveActive)
            return;
        MapMover.UseTargets(self, actor, null);
    }

    /// <summary><c>spawnfunc(target_delay)</c> — a relay with a default 1s delay (wait falls back into delay).</summary>
    public static void TargetDelaySetup(Entity this_)
    {
        if (this_.Wait == 0f) this_.Wait = 1f;
        if (this_.Delay == 0f) this_.Delay = this_.Wait; // Q3 field fallback
        RelaySetup(this_);
        this_.ClassName = "target_delay";
    }

    /// <summary>
    /// <c>spawnfunc(trigger_delay)</c> — when used, fires its targets after `.wait` seconds (a one-shot timer
    /// per activation). Distinct from target_delay's relay-with-delay; this schedules its own think.
    /// </summary>
    public static void DelaySetup(Entity this_)
    {
        if (this_.Wait == 0f) this_.Wait = 1f;
        this_.Use = DelayUse;
        this_.Active = MapMover.ActiveActive;
        this_.ClassName = "trigger_delay";
        MapMover.IndexRegister(this_);
    }

    /// <summary>QC <c>delay_use</c>: capture the activator and fire after `.wait`.</summary>
    private static void DelayUse(Entity self, Entity actor)
    {
        if (self.Active != MapMover.ActiveActive)
            return;
        self.Enemy = actor;
        self.GoalEntity = null;
        self.Think = DelayDelayedUse;
        self.NextThink = MapMover.Now() + self.Wait;
    }

    /// <summary>QC <c>delay_delayeduse</c>.</summary>
    private static void DelayDelayedUse(Entity self)
    {
        MapMover.UseTargets(self, self.Enemy, self.GoalEntity);
        self.Enemy = null;
        self.GoalEntity = null;
    }

    // ===================================================================
    //  trigger_secret (secret.qc)
    // ===================================================================

    /// <summary>
    /// <c>spawnfunc(trigger_secret)</c> — a trigger_once that also bumps the secrets-found counter. Only a
    /// player's touch fires it; it cannot itself be a target. Cannot be delayed (QC sets .delay = 0).
    /// </summary>
    public static void SecretSetup(Entity this_)
    {
        ++MapObjectsState.SecretsTotal;

        if (string.IsNullOrEmpty(this_.Message))
            this_.Message = "You found a secret!";

        if (string.IsNullOrEmpty(this_.Noise) && this_.Sounds == 0)
            this_.Sounds = 1; // misc/secret.wav
        this_.Noise = this_.Sounds switch
        {
            1 => "misc/secret.wav",
            2 => "misc/talk.wav",
            3 => "misc/trigger1.wav",
            _ => this_.Noise,
        };

        this_.Delay = 0f; // a secret cannot be delayed

        MapMover.InitTrigger(this_);
        this_.ClassName = "trigger_secret";
        this_.Touch = SecretTouch;
        MapMover.IndexRegister(this_);
    }

    /// <summary>QC <c>trigger_secret_touch</c>: count the secret and fire targets, then disarm (fires once).</summary>
    private static void SecretTouch(Entity self, Entity toucher)
    {
        if ((toucher.Flags & EntFlags.Client) == 0)
            return;

        ++MapObjectsState.SecretsFound;
        MapMover.Centerprint(toucher, self.Message); // QC centerprints "You found a secret!" (via SUB_UseTargets)
        MapMover.UseTargets(self, toucher, toucher);
        self.Touch = null; // can't delete inside a touch callback; just disarm
    }

    // ===================================================================
    //  trigger_swamp (swamp.qc) — slow + damage players standing inside
    // ===================================================================

    /// <summary>
    /// <c>spawnfunc(trigger_swamp)</c> — players inside are slowed (<c>.swamp_slowdown</c>) and damaged every
    /// <c>.swamp_interval</c> seconds. Implemented as a self-thinking volume that scans its radius each frame
    /// (QC swamp_think), marking touchers with their current swampslug and damaging on the interval.
    /// </summary>
    public static void SwampSetup(Entity this_)
    {
        MapMover.InitTrigger(this_);
        this_.ClassName = "trigger_swamp";
        this_.Active = MapMover.ActiveActive;
        this_.Think = SwampThink;
        this_.NextThink = MapMover.Now();

        if (this_.Dmg == 0f) this_.Dmg = 5f;
        if (this_.SwampInterval == 0f) this_.SwampInterval = 1f;
        if (this_.SwampSlowdown == 0f) this_.SwampSlowdown = 0.5f;
        MapMover.IndexRegister(this_);
    }

    /// <summary>
    /// QC <c>g_swamped</c> — the players currently claimed by some swamp this frame. Tracked here (rather than
    /// scanning all clients) so <see cref="SwampThink"/> can clear a player's stamp the frame it leaves the
    /// volume / the swamp goes inactive, matching QC's IL_EACH(g_swamped, …) clear-then-restamp pass.
    /// </summary>
    private static readonly List<Entity> _swamped = new();

    /// <summary>Scratch buffer reused for the per-frame <see cref="SwampThink"/> volume scan (replaces a
    /// per-think <c>.ToList()</c>). Swamp thinks run sequentially on the sim thread; the claim loop below runs
    /// no callbacks (it only stamps <c>SwampSlug</c>), so nothing can re-enter mid-iteration — one shared static
    /// is safe (the damage pass afterwards iterates <see cref="_swamped"/>, not this).</summary>
    private static readonly List<Entity> _swampScratch = new();

    /// <summary>QC <c>swamp_think</c>: re-mark the players inside the volume each frame and damage on interval.</summary>
    private static void SwampThink(Entity self)
    {
        self.NextThink = MapMover.Now();
        if (Api.Services is null)
            return;

        // QC: first drop every player this swamp had claimed last frame (so a player who left the volume — or
        // is in this swamp while it is inactive — has their stamp removed and regains full speed). They get
        // re-claimed below only if still inside an ACTIVE swamp.
        for (int i = _swamped.Count - 1; i >= 0; i--)
        {
            Entity it = _swamped[i];
            if (ReferenceEquals(it.SwampSlug, self))
            {
                it.SwampSlug = null;
                _swamped.RemoveAt(i);
            }
        }

        if (self.Active != MapMover.ActiveActive)
            return;

        // QC swamp_touch claims by trigger TOUCH (box-exact). FindInBox queries the volume's AABB directly
        // (precise overlap inside) — the old bounding-SPHERE FindInRadius (half-diagonal radius, no box trim)
        // over-claimed players standing near the volume's corners, swamp-slowing them while OUTSIDE the swamp.
        Api.Entities.FindInBox(self.AbsMin, self.AbsMax, _swampScratch);
        foreach (Entity it in _swampScratch)
        {
            if ((it.Flags & EntFlags.Client) == 0 || MapMover.IsDead(it))
                continue;
            // QC: it.swampslug.active == ACTIVE_NOT — only claim a player whose current swamp is inactive
            // (or who has no swamp). A player already in another ACTIVE swamp keeps it.
            if (it.SwampSlug is not null && it.SwampSlug.Active == MapMover.ActiveActive)
                continue;

            if (it.SwampSlug is null)
                _swamped.Add(it);
            it.SwampSlug = self; // marks them swamped (movement code reads SwampSlug.SwampSlowdown)
        }

        // QC: damage every player currently claimed by THIS swamp, on the per-toucher interval.
        foreach (Entity it in _swamped)
        {
            if (!ReferenceEquals(it.SwampSlug, self))
                continue;
            if (MapMover.Now() > it.SwampNextTime)
            {
                Combat.Damage(it, self, self, self.Dmg, DeathTypes.Swamp, it.Origin, Vector3.Zero);
                it.SwampNextTime = MapMover.Now() + self.SwampInterval;
            }
        }
    }

    // ===================================================================
    //  trigger_impulse (impulse.qc) — directional / accel / radial force fields
    // ===================================================================

    public const int FalloffNo = 0;        // FALLOFF_NO
    public const int FalloffLinear = 1;    // FALLOFF_LINEAR
    public const int FalloffLinearInv = 2; // FALLOFF_LINEAR_INV
    public const int ImpulseDirectionalSpeedTarget = 1 << 6; // IMPULSE_DIRECTIONAL_SPEEDTARGET

    private const float ImpulseMaxPushDeltaTime = 0.15f;
    private const float ImpulseDirectionalMaxAccelFactor = 8f;
    private const float ImpulseDefaultRadialStrength = 2000f;
    private const float ImpulseDefaultDirectionalStrength = 950f;
    private const float ImpulseDefaultAccelStrength = 0.9f;

    /// <summary>
    /// <c>spawnfunc(trigger_impulse)</c> — a force field: radial (gravity/repulsor) if <c>.radius</c> is set,
    /// directional (toward a target_position) if it has a target, otherwise a directionless accelerator /
    /// decelerator. Cvar multipliers (<c>g_triggerimpulse_*</c>) are read through the facade.
    /// </summary>
    public static void ImpulseSetup(Entity this_)
    {
        this_.Active = MapMover.ActiveActive;
        MapMover.InitTrigger(this_);
        this_.ClassName = "trigger_impulse";

        if (this_.ImpulseRadius != 0f)
        {
            if (this_.Strength == 0f)
                this_.Strength = ImpulseDefaultRadialStrength * Cvar("g_triggerimpulse_radial_multiplier", 1f);
            MapMover.SetOrigin(this_, this_.Origin);
            MapMover.SetSize(this_, new Vector3(-1f, -1f, -1f) * this_.ImpulseRadius, new Vector3(1f, 1f, 1f) * this_.ImpulseRadius);
            this_.Touch = ImpulseTouchRadial;
        }
        else if (!string.IsNullOrEmpty(this_.Target))
        {
            if (this_.Strength == 0f)
                this_.Strength = ImpulseDefaultDirectionalStrength * Cvar("g_triggerimpulse_directional_multiplier", 1f);
            this_.Touch = ImpulseTouchDirectional;
        }
        else
        {
            if (this_.Strength == 0f)
                this_.Strength = ImpulseDefaultAccelStrength;
            this_.Strength = MathF.Pow(this_.Strength, Cvar("g_triggerimpulse_accel_power", 1f)) * Cvar("g_triggerimpulse_accel_multiplier", 1f);
            this_.Touch = ImpulseTouchAccel;
        }
        MapMover.IndexRegister(this_);
    }

    /// <summary>Per-toucher push timestep (QC lastpushtime): seconds since the last impulse, capped, 0 first touch.</summary>
    private static float PushDeltaTime(Entity toucher)
    {
        float dt = MapMover.Now() - toucher.LastPushTime;
        if (dt > ImpulseMaxPushDeltaTime)
            dt = 0f;
        toucher.LastPushTime = MapMover.Now();
        return dt;
    }

    /// <summary>QC <c>trigger_impulse_touch_directional</c>: push toward the target at a constant rate (or accelerate to a speed cap).</summary>
    private static void ImpulseTouchDirectional(Entity self, Entity toucher)
    {
        if (self.Active != MapMover.ActiveActive || !MapMover.IsPushable(toucher))
            return;
        Entity? targ = MapMover.FindFirstByTargetName(self.Target);
        if (targ is null)
            return;

        float dt = PushDeltaTime(toucher);
        if (dt == 0f)
            return;

        Vector3 dir = QMath.Normalize(targ.Origin - self.Origin);
        if ((self.SpawnFlags & ImpulseDirectionalSpeedTarget) != 0)
        {
            float addspeed = self.Strength - QMath.Dot(toucher.Velocity, dir);
            if (addspeed > 0f)
            {
                float accel = MathF.Min(ImpulseDirectionalMaxAccelFactor * dt * self.Strength, addspeed);
                toucher.Velocity += accel * dir;
            }
        }
        else
        {
            toucher.Velocity += dir * self.Strength * dt;
        }
        toucher.Flags &= ~EntFlags.OnGround;
    }

    /// <summary>QC <c>trigger_impulse_touch_accel</c>: scale the toucher's velocity (damper/accelerator), ticrate-independent.</summary>
    private static void ImpulseTouchAccel(Entity self, Entity toucher)
    {
        if (self.Active != MapMover.ActiveActive || !MapMover.IsPushable(toucher))
            return;
        float dt = PushDeltaTime(toucher);
        if (dt == 0f)
            return;
        toucher.Velocity *= MathF.Pow(self.Strength, dt); // strength ** dt
    }

    /// <summary>QC <c>trigger_impulse_touch_radial</c>: push out from (or into) the field center with falloff.</summary>
    private static void ImpulseTouchRadial(Entity self, Entity toucher)
    {
        if (self.Active != MapMover.ActiveActive || !MapMover.IsPushable(toucher))
            return;
        float dt = PushDeltaTime(toucher);
        if (dt == 0f)
            return;

        float dist = MathF.Min(self.ImpulseRadius, (self.Origin - toucher.Origin).Length());
        float str = self.Falloff switch
        {
            FalloffLinear => (1f - dist / self.ImpulseRadius) * self.Strength,    // strongest at the center
            FalloffLinearInv => (dist / self.ImpulseRadius) * self.Strength,      // strongest at the rim
            _ => self.Strength,
        };
        toucher.Velocity += QMath.Normalize(toucher.Origin - self.Origin) * str * dt;
    }

    // ===================================================================
    //  trigger_keylock (keylock.qc) — fire target when all required keys are supplied
    // ===================================================================

    /// <summary>
    /// <c>spawnfunc(trigger_keylock)</c> — a key-gated relay. When a player touches it with all the required
    /// <c>.itemkeys</c>, it fires <c>.target</c> (and killtarget) and removes itself; with only some keys it
    /// fires <c>.target2</c> on a <c>.wait</c> cooldown. Removes itself if no keys were specified.
    /// </summary>
    public static void KeylockSetup(Entity this_)
    {
        if (this_.ItemKeys == 0)
        {
            MapMover.RemoveEntity(this_);
            return;
        }

        if (string.IsNullOrEmpty(this_.Message))
            this_.Message = "Unlocked!";

        if (string.IsNullOrEmpty(this_.Noise))
            this_.Noise = this_.Sounds switch
            {
                1 => "misc/secret.wav",
                2 => "misc/talk.wav",
                _ => "misc/trigger1.wav",
            };
        if (string.IsNullOrEmpty(this_.Noise1)) this_.Noise1 = "misc/decreasevalue.wav"; // some-keys sound
        if (string.IsNullOrEmpty(this_.Noise2)) this_.Noise2 = "misc/talk.wav";           // missing-key sound

        if (this_.Wait == 0f) this_.Wait = 5f;

        MapMover.InitTrigger(this_);
        this_.ClassName = "trigger_keylock";
        this_.Touch = KeylockTouch;
        MapMover.IndexRegister(this_);
    }

    /// <summary>QC <c>trigger_keylock_touch</c>: consume the player's matching keys; fire target/target2 accordingly.</summary>
    private static void KeylockTouch(Entity self, Entity toucher)
    {
        if ((toucher.Flags & EntFlags.Client) == 0)
            return;

        bool keyUsed = false;
        if (self.ItemKeys != 0)
        {
            int valid = self.ItemKeys & toucher.ItemKeys;
            if (valid != 0)
            {
                self.ItemKeys &= ~valid; // remove the supplied keys from the requirement
                keyUsed = true;
            }
        }

        if (self.ItemKeys != 0)
        {
            // at least one key still missing. QC names the still-missing keys in the centerprint
            // (item_keys_keylist of the post-consume requirement): "You also need X" when SOME were
            // supplied (CENTER_DOOR_LOCKED_ALSONEED), "You need X" when NONE were (CENTER_DOOR_LOCKED_NEED).
            string keylist = MapObjectsRegistry.ItemKeysKeylist(self.ItemKeys);
            if (keyUsed)
            {
                MapMover.Play2(toucher, self.Noise1); // QC keylock.qc:49 play2(toucher, this.noise1)
                NotificationSystem.Send(NotifBroadcast.One, toucher, MsgType.Center, "DOOR_LOCKED_ALSONEED", keylist);
                toucher.KeyDoorMessageTime = MapMover.Now() + 2f;
            }
            else if (toucher.KeyDoorMessageTime <= MapMover.Now())
            {
                MapMover.Play2(toucher, self.Noise2); // QC keylock.qc:56 play2(toucher, this.noise2)
                NotificationSystem.Send(NotifBroadcast.One, toucher, MsgType.Center, "DOOR_LOCKED_NEED", keylist);
                toucher.KeyDoorMessageTime = MapMover.Now() + 2f;
            }

            // fire target2 on the wait cooldown (the "partial" relay).
            if (self.Delay <= MapMover.Now() && !string.IsNullOrEmpty(self.Target2))
            {
                KeylockTrigger(self, toucher, self.Target2);
                self.Delay = MapMover.Now() + self.Wait;
            }
        }
        else
        {
            // all keys supplied: fire target, kill killtarget, remove self.
            MapMover.Play2(toucher, self.Noise); // QC keylock.qc:75 play2(toucher, this.noise)
            MapMover.Centerprint(toucher, self.Message); // QC centerprint(toucher, "Unlocked!")

            if (!string.IsNullOrEmpty(self.Target))
                KeylockTrigger(self, toucher, self.Target);
            if (!string.IsNullOrEmpty(self.KillTarget))
                foreach (Entity k in MapMover.FindByTargetName(self.KillTarget).ToList())
                    MapMover.RemoveEntity(k);

            MapMover.RemoveEntity(self);
        }
    }

    /// <summary>QC <c>trigger_keylock_trigger</c>: fire the <c>.use</c> of every entity named by <paramref name="s"/>.</summary>
    private static void KeylockTrigger(Entity self, Entity actor, string s)
    {
        foreach (Entity t in MapMover.FindByTargetName(s).ToList())
            t.Use?.Invoke(t, actor);
    }

    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }
}
