using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Port-O-Launch (Porto) — port of common/weapons/weapon/porto.{qh,qc}. A utility superweapon that
/// fires a bouncing projectile which creates one-way portals between flat surfaces. Primary shoots the
/// in-portal (type -1, or type 0 when "secondary"-mode portals are enabled); secondary shoots the
/// out-portal (type 1). The projectile uses MOVETYPE_BOUNCEMISSILE, reflecting off slick/clip surfaces
/// and creating a portal where it lands. It deals no damage and uses no ammo, and self-destructs after
/// its lifetime.
///
/// Identity/attributes from porto.qh; balance from bal-wep-xonotic.cfg (g_balance_porto_*).
/// This port covers the projectile launch + bounce/lifetime lifecycle, the porto_current single-portal
/// latch, the porto_forbidden gating, right_vector orientation tracking, the Strength speed boost, and the
/// full placement decision tree (in-portal / out-portal / combined red->blue chaining, success/fail
/// cleanup). The only deferred piece is the warpzone portal ENTITY spawn itself
/// (Portal_SpawnIn/OutPortalAtTrace), which is a separate subsystem outside the weapons module; this code
/// records the placement request for it. No-TrueAim (WEP_FLAG_NOTRUEAIM): launches straight from the eye.
/// </summary>
[Weapon]
public sealed class Porto : Weapon
{
    /// <summary>Per-fire-mode balance block — QC WEP_CVAR_BOTH(WEP_PORTO, (type <= 0), *).</summary>
    public struct ModeBalance
    {
        public float Animtime; // *_animtime
        public float Lifetime; // *_lifetime (portal projectile self-destruct)
        public float Refire;   // *_refire
        public float Speed;    // *_speed (launch speed)
    }

    public ModeBalance Primary;
    public ModeBalance Secondary;

    /// <summary>
    /// g_balance_porto_secondary — when 1, primary shoots the in-portal (type 0) and secondary the
    /// out-portal (type 1); when 0, primary shoots a combined red->blue portal (type -1).
    /// </summary>
    public bool SecondaryEnabled = true;

    public Porto()
    {
        NetName = "porto";
        DisplayName = "Port-O-Launch";
        Impulse = 0;
        // WEP_TYPE_OTHER | WEP_FLAG_SUPERWEAPON | WEP_FLAG_NODUAL | WEP_FLAG_NOTRUEAIM
        SpawnFlags = WeaponFlags.TypeOther | WeaponFlags.SuperWeapon | WeaponFlags.NoDual | WeaponFlags.NoTrueAim;
        Color = new Vector3(0.404f, 0.545f, 0.937f);
        ViewModel = "h_porto.iqm";  // MDL_PORTO_VIEW
        WorldModel = "v_porto.md3"; // MDL_PORTO_WORLD
        ItemModel = "g_porto.md3";  // MDL_PORTO_ITEM
    }

    // porto.qh: ATTRIB(PortoLaunch, w_crosshair, "gfx/crosshairporto"); ATTRIB(PortoLaunch, w_crosshair_size, 0.6).
    public override string? Crosshair => "gfx/crosshairporto";
    public override float CrosshairSize => 0.6f;

    public override void Configure()
    {
        Primary.Animtime = Bal("g_balance_porto_primary_animtime", 0.3f);
        Primary.Lifetime = Bal("g_balance_porto_primary_lifetime", 5f);
        Primary.Refire = Bal("g_balance_porto_primary_refire", 1.5f);
        Primary.Speed = Bal("g_balance_porto_primary_speed", 1000f);

        Secondary.Animtime = Bal("g_balance_porto_secondary_animtime", 0.3f);
        Secondary.Lifetime = Bal("g_balance_porto_secondary_lifetime", 5f);
        Secondary.Refire = Bal("g_balance_porto_secondary_refire", 1.5f);
        Secondary.Speed = Bal("g_balance_porto_secondary_speed", 1000f);

        SecondaryEnabled = BalBool("g_balance_porto_secondary", true);

        // autocvar_g_balance_powerup_strength_force (balance-xonotic.cfg:237 = 3). Read live so a balance/ruleset
        // variant (e.g. nexuiz25 = 4) is honoured rather than the previously-hardcoded constant.
        StrengthForce = Bal("g_balance_powerup_strength_force", 3f);
    }

    /// <summary>QC Q3SURFACEFLAG_SLICK — a portal projectile reflects off (won't place a portal on) slick faces.</summary>
    private const int Q3SurfaceFlagSlick = 0x2;
    /// <summary>QC Q3SURFACEFLAG_NOIMPACT — a portal projectile fails (no portal) on noimpact faces.</summary>
    private const int Q3SurfaceFlagNoImpact = 0x10;

    // METHOD(PortoLaunch, wr_think) — common/weapons/weapon/porto.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);

        // Only one portal projectile may be in flight, and porto_forbidden gates re-fires.
        if (st.PortoCurrent is not null && st.PortoCurrent.IsFreed) st.PortoCurrent = null;
        bool canFire = st.PortoCurrent is null && st.PortoForbidden <= 0;

        if (SecondaryEnabled)
        {
            // Each portal shot is refire-gated (QC weapon_prepareattack) on top of the one-portal canFire gate.
            if (fire == FireMode.Primary && canFire)
            {
                if (PrepareAttack(actor, slot, fire))
                    Attack(actor, slot, st, Primary, type: 0);   // in-portal
            }
            else if (fire == FireMode.Secondary && canFire)
            {
                if (PrepareAttack(actor, slot, fire))
                    Attack(actor, slot, st, Secondary, type: 1); // out-portal
            }
        }
        else
        {
            // Non-secondary mode: secondary HOLDS the aim angle (porto_v_angle), primary shoots the combined
            // red->blue portal (type -1) along the held angle. QC wr_think processes both buttons in one call
            // every tick (porto.qc:372-396); the port driver instead calls WrThink once per fire-mode, but the
            // driver records BOTH live buttons on the slot state (st.ButtonAttack2) before either call, so we can
            // reproduce the exact QC bitmask logic from the Primary tick (which always runs every frame):
            //   - while held: release the hold the instant ATCK2 is no longer down (QC `if (!(fire & 2)) held=0`);
            //   - while not held: capture+hold the current aim angle on a fresh ATCK2 press (QC `if (fire & 2)`).
            // We drive this ONLY on the Primary tick so the state machine ticks exactly once per frame with the
            // real ATCK2 edge (the Secondary tick is a no-op here). (Dormant in stock play: g_balance_porto_secondary
            // defaults to 1, so this branch is never reached.)
            if (fire == FireMode.Primary)
            {
                bool atck2 = st.ButtonAttack2; // QC (fire & 2)
                if (st.PortoVAngleHeld)
                {
                    if (!atck2)
                        st.PortoVAngleHeld = false; // QC: if (!(fire & 2)) porto_v_angle_held = 0;
                }
                else if (atck2)
                {
                    st.PortoVAngle = actor.Angles; // QC porto_v_angle = actor.v_angle
                    st.PortoVAngleHeld = true;      // QC porto_v_angle_held = 1
                }

                // QC: if (fire & 1) ... weapon_prepareattack ... W_Porto_Attack(type -1). The held angle (if any)
                // overrides the aim — Attack reads st.PortoVAngle when PortoVAngleHeld is set (porto.qc:386-387
                // makevectors(porto_v_angle) overriding the previously set angles).
                if (canFire && PrepareAttack(actor, slot, fire))
                    Attack(actor, slot, st, Primary, type: -1);
            }
        }
    }

    // Refire/animtime from the (cvar-seeded) per-mode balance blocks.
    public override float RefireFor(FireMode fire) => (fire == FireMode.Secondary ? Secondary : Primary).Refire;
    public override float AnimtimeFor(FireMode fire) => (fire == FireMode.Secondary ? Secondary : Primary).Animtime;

    // METHOD(PortoLaunch, wr_aim) — porto.qc:342. QC clears ATCK/ATCK2, then in NON-secondary mode runs
    // bot_aim(speed, lifetime) and presses ATCK if a target is found; in the default (secondary) mode it presses
    // nothing. The port's generic bot-aim path already leads by the primary speed (BotAimShotSpeed default =
    // primary speed cvar) and presses ATCK when a target is in the deviation cone — which reproduces the
    // non-secondary bot_aim decision. The only Base divergence is the secondary mode, where porto's wr_aim never
    // fires: veto all bot fire when secondary mode is on (the stock default), so a porto-holding bot holds fire.
    // (Porto never presses ATCK2 via the bot path — BotWantsSecondary stays the default false.)
    public override bool BotForbidsFire() => SecondaryEnabled;

    // W_Porto_Attack — launch a bouncing portal projectile that tracks its right_vector for orientation. porto.qc
    private void Attack(Entity actor, WeaponSlot slot, WeaponSlotState st, ModeBalance bal, int type)
    {
        // Porto uses no ammo (wr_checkammo always true).
        Vector3 aimAngles = (st.PortoVAngleHeld && !SecondaryEnabled) ? st.PortoVAngle : actor.Angles;
        QMath.AngleVectors(aimAngles, out Vector3 forward, out _, out _);
        // Always shoot from the eye along v_forward (W_SetupShot then override w_shotdir = v_forward).
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, recoil: 4f);
        Vector3 origin = shot.Origin;
        Vector3 dir = forward;

        // Strength powerup boosts the launch speed. QC: StatusEffects_active(STATUSEFFECT_Strength, actor),
        // whose netname is "strength" (NOT "buff_strength" — only buffs carry the buff_ prefix). Matches the
        // canonical query used elsewhere (Ctf.cs:801).
        float speed = bal.Speed;
        if (StatusEffectsCatalog.ByName("strength") is { } str && StatusEffectsCatalog.Has(actor, str))
            speed *= StrengthForce;

        Entity gren = Api.Entities.Spawn();
        gren.ClassName = "porto";
        gren.Owner = actor;
        gren.NetName = NetName;
        gren.Count = type; // QC gren.cnt = type (-1 combined, 0 in-portal, 1 out-portal)
        // QC W_Porto_Attack sets gren.effects = EF_RED, then CSQCProjectile(... type>0 ? PORTO_BLUE : PORTO_RED).
        // The port has no separate networked CSQC-type field — the client classifier (ProjectileCatalog.Classify)
        // derives red vs blue from the networked EF_RED/EF_BLUE bits — so seed the effect bit to the RENDER colour:
        // the out-portal (type 1) ships PORTO_BLUE (EF_BLUE); the in-portal/combined (type<=0) ships PORTO_RED
        // (EF_RED). The combined red->blue flip (OnTouch) only runs on the cnt<0 path, which keeps EF_RED here.
        gren.Effects = type > 0 ? EffectBlue : EffectRed;
        gren.MoveType = MoveType.BounceMissile;
        Projectiles.MakeTrigger(gren); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
        gren.Flags = EntFlags.Item; // QC FL_PROJECTILE
        // QC: gren.bot_dodge = true; gren.bot_dodgerating = 200; IL_PUSH(g_bot_dodge, gren). The BotBrain's
        // HavocbotDodge scan (the port's g_bot_dodge equivalent) reads these flags off every non-client edict, so
        // setting them here is all that's needed for a SUPERBOT to steer away from an incoming porto projectile.
        gren.BotDodge = true;
        gren.BotDodgeRating = 200f;
        gren.LTime = Api.Clock.Time; // portal_id (unique per shot)
        Api.Entities.SetSize(gren, Vector3.Zero, Vector3.Zero);
        Api.Entities.SetOrigin(gren, origin);

        // W_SetupProjVelocity_Basic(gren, speed, 0).
        gren.Velocity = dir * speed;
        gren.Angles = QMath.VecToAngles(gren.Velocity);

        // QC: fixedmakevectors(fixedvectoangles(gren.velocity)); gren.right_vector = v_right;
        // right_vector is the portal's roll axis; we carry it on AVelocity and reflect it on each bounce so
        // portal orientation tracks through reflections.
        QMath.AngleVectors(QMath.VecToAngles(gren.Velocity), out _, out Vector3 right, out _);
        gren.AVelocity = right;

        // nextthink = time + lifetime; W_Porto_Think self-destructs the unspent projectile.
        // QC W_Porto_Think: if the owner reconnected/respawned as a different player (playerid mismatch) it just
        // delete()s with no fail behaviour; otherwise W_Porto_Fail(false). We carry the firing slot so the
        // success/fail cleanup clears the right per-slot porto_current latch.
        gren.Think = self => PortoThink(self, slot);
        gren.NextThink = Api.Clock.Time + bal.Lifetime;
        gren.Touch = (self, other) => OnTouch(self, other, slot);

        // MUTATOR_CALLHOOK(EditProjectile, actor, gren) (porto.qc).
        var ep = new MutatorHooks.EditProjectileArgs(actor, gren);
        MutatorHooks.EditProjectile.Call(ref ep);

        st.PortoCurrent = gren; // porto_current registration (single portal in flight)
        Api.Sound.Play(actor, SoundChannel.Weapon, "porto/fire.wav");
    }

    // W_Porto_Touch — place a portal on a valid surface, reflect off slick/clip, fail on noimpact. porto.qc
    private void OnTouch(Entity self, Entity other, WeaponSlot slot)
    {
        // QC: if (toucher.classname == "portal") return; — a portal collision is handled by the portal itself.
        if (other.ClassName == "portal") return;

        // QC reads trace_dphitq3surfaceflags / trace_dphitcontents / trace_plane_normal from the collision trace
        // that produced this touch. The engine bouncemissile touch doesn't carry those here, so — exactly like
        // Base's creature-hit branch does (porto.qc:178) — we re-probe the impact face with a short worldonly
        // traceline and read the surface flags, contents, and TRUE plane normal off the result. This makes the
        // slick/clip + noimpact branches genuinely reachable and gives the portal its real impact-plane normal
        // (instead of the negated velocity). On a miss (no nearby solid) ProbeImpact falls back to -velocity.
        ImpactSurface surf = ProbeImpact(self, other);

        bool isCreature = other.TakeDamage != DamageMode.No || (other.Flags & EntFlags.Client) != 0;
        if (isCreature)
        {
            // QC creature-hit: trace straight down to the floor; if there's no nearby solid, or it's
            // slick/clip/noimpact, ignore the contact and keep flying (don't place a portal on a player).
            self.Angles = QMath.VecToAngles(self.Velocity);
            return;
        }

        // QC: not-owner — if the firing player is gone/changed, drop the projectile with the unsupported cue and
        // place nothing. We key ownership on the live owner reference (the port has no stable playerid; see TODO).
        if (self.Owner is null || self.Owner.IsFreed)
        {
            Api.Sound.Play(self, SoundChannel.ShotsAuto, "porto/unsupported.wav");
            CleanupProjectile(self, slot);
            return;
        }

        // QC SLICK / PLAYERCLIP reflect-and-continue: bounce off (don't place), reflecting BOTH the velocity and
        // the carried right_vector about the impact plane, and play the bounce cue (porto.qc:192-198).
        if (surf.IsSlickOrClip)
        {
            Api.Sound.Play(self, SoundChannel.ShotsAuto, "porto/bounce.wav");
            self.AVelocity = ReflectAbout(self.AVelocity, surf.Normal); // right_vector reflect
            self.Velocity = ReflectAbout(self.Velocity, surf.Normal);   // QC vectoangles(velocity reflected)
            self.Angles = QMath.VecToAngles(self.Velocity);
            return;
        }

        // QC NOIMPACT (sky / noimpact face): hard fail, no portal (porto.qc:199-205). On a combined (cnt<0) shot
        // QC clears ALL of the owner's portals (Portal_ClearAll_PortalsOnly, porto.qc:204), not just this shot's.
        if (surf.IsNoImpact)
        {
            Api.Sound.Play(self, SoundChannel.ShotsAuto, "porto/unsupported.wav");
            // QC: W_Porto_Fail(this, false) THEN, for a combined shot, Portal_ClearAll_PortalsOnly(realowner).
            if (self.Count < 0) PortalClearAll?.Invoke(self.Owner);
            PortoFail(self, slot, failhard: false, planeNormal: surf.Normal);
            return;
        }

        // QC CheckWireframeBox 96^3 surface-size gate (porto.qc Portal_SpawnIn/OutPortalAtTrace -> CheckWireframeBox):
        // a portal needs a flat 96qu surface to fit; on a wall too small/cluttered the QC spawner returns false, so
        // the touch acts like a placement failure (SND_PORTO_UNSUPPORTED + W_Porto_Fail, clearing all owner portals
        // for a combined cnt<0 shot). The port has no headless spawn success/fail handshake, so apply the same gate
        // here before any placement branch.
        if (!CheckWireframeBox(self, surf.Normal))
        {
            Api.Sound.Play(self, SoundChannel.ShotsAuto, "porto/unsupported.wav");
            if (self.Count < 0) PortalClearAll?.Invoke(self.Owner);
            PortoFail(self, slot, failhard: false, planeNormal: surf.Normal);
            return;
        }

        // Portal placement decision tree:
        //   type 0  -> in-portal only;  type 1 -> out-portal only;
        //   type -1 -> red->blue: place the in-portal then continue as blue to place the out-portal.
        if (self.Count == 0 || self.Count == 1)
        {
            // In/out-only: place; create on success. (PortalSpawner is fire-and-forget — there is no headless
            // success/fail handshake yet, so we treat placement as succeeding; see portal_spawn gap.)
            bool isIn = self.Count == 0;
            PlacePortal(self, surf.Normal, isInPortal: isIn);
            Api.Sound.Play(self, SoundChannel.ShotsAuto, "porto/create.wav");
            // QC Send_Notification(NOTIF_ONE, realowner, MSG_CENTER, CENTER_PORTO_CREATED_IN/OUT).
            NotifyOwner(self, isIn ? "PORTO_CREATED_IN" : "PORTO_CREATED_OUT");
            PortoSuccess(self, slot);
        }
        else if ((self.Effects & EffectRed) != 0)
        {
            // combined red shot: place the in-portal, flip to blue, reflect, keep flying to place the out-portal.
            self.Effects &= ~EffectRed;
            self.Effects |= EffectBlue;
            PlacePortal(self, surf.Normal, isInPortal: true);
            Api.Sound.Play(self, SoundChannel.ShotsAuto, "porto/create.wav");
            NotifyOwner(self, "PORTO_CREATED_IN"); // QC CENTER_PORTO_CREATED_IN on the in-portal of a combined shot
            self.AVelocity = ReflectAbout(self.AVelocity, surf.Normal); // QC: right_vector reflected off in-portal plane
            self.Velocity = ReflectAbout(self.Velocity, surf.Normal);
            self.Angles = QMath.VecToAngles(self.Velocity); // bounce onward as blue
        }
        else
        {
            // blue shot landed: place the out-portal and finish. QC (porto.qc:263) first verifies the owner's
            // current in-portal is the one THIS shot placed (realowner.portal_in.portal_id == this.portal_id);
            // on a mismatch (the in-portal was torn down/replaced mid-flight) it plays the unsupported cue, clears
            // ALL owner portals, and fails — never leaving a dangling out-portal.
            // When the host hook is unwired we can't verify the in-portal slot, so degrade to the prior
            // unconditional placement; when wired, a null return means "owner has no in-portal" → mismatch → fail.
            bool inMatches = PortalInId is null
                || PortalInId.Invoke(self.Owner) is { } inId && inId == (int)self.LTime;
            if (inMatches)
            {
                PlacePortal(self, surf.Normal, isInPortal: false);
                Api.Sound.Play(self, SoundChannel.ShotsAuto, "porto/create.wav");
                NotifyOwner(self, "PORTO_CREATED_OUT"); // QC CENTER_PORTO_CREATED_OUT
                PortoSuccess(self, slot);
            }
            else
            {
                Api.Sound.Play(self, SoundChannel.ShotsAuto, "porto/unsupported.wav");
                PortalClearAll?.Invoke(self.Owner); // QC Portal_ClearAll_PortalsOnly(realowner)
                PortoFail(self, slot, failhard: false, planeNormal: surf.Normal);
            }
        }
    }

    /// <summary>QC DPCONTENTS_PLAYERCLIP — clip brushes a porto reflects off instead of placing on.</summary>
    private const int DpContentsPlayerClip = 256;

    /// <summary>QC CheckWireframeBox 96qu portal extent — a portal needs a flat 96^3 region to fit at the hit.</summary>
    private const float WireframeBox = 96f;

    /// <summary>
    /// QC <c>CheckWireframeBox(this, org, v_right*96, v_up*96, v_forward*96)</c> (the portal surface-size gate
    /// Base runs inside Portal_SpawnIn/OutPortalAtTrace). A portal needs a clear flat 96^3 region at the wall. We
    /// approximate Base's edge-traced wireframe with a single 96^3 box-trace sat flush against the hit face
    /// (centre pushed out along the normal so it doesn't start embedded): if the box sits clear of geometry the
    /// surface is large/flat enough to host a portal. Mirrors the client preview's CheckWireframeBox.
    /// </summary>
    private bool CheckWireframeBox(Entity self, Vector3 normal)
    {
        if (normal.LengthSquared() <= 0.0001f) return true; // no usable plane → don't reject (fail open, as today)
        const float half = WireframeBox * 0.5f;
        Vector3 mins = new(-half, -half, -half);
        Vector3 maxs = new(half, half, half);
        Vector3 centre = self.Origin + Vector3.Normalize(normal) * (half + 1f);
        TraceResult tr = Api.Trace.Trace(centre, mins, maxs, centre, MoveFilter.WorldOnly, self);
        return !tr.StartSolid;
    }

    /// <summary>Impact-face data recovered from the re-probe trace: the Q3 surface category + the true plane normal.</summary>
    private readonly struct ImpactSurface
    {
        public readonly bool IsSlickOrClip; // Q3SURFACEFLAG_SLICK || DPCONTENTS_PLAYERCLIP
        public readonly bool IsNoImpact;    // Q3SURFACEFLAG_NOIMPACT (sky etc.)
        public readonly Vector3 Normal;     // impact plane normal (the wall, facing into the room)
        public ImpactSurface(bool slick, bool noimpact, Vector3 normal) { IsSlickOrClip = slick; IsNoImpact = noimpact; Normal = normal; }
    }

    /// <summary>
    /// Re-probe the surface this projectile just struck so OnTouch can read the QC <c>trace_dphit*</c> flags and the
    /// real impact plane (the engine bouncemissile touch doesn't carry them). Mirrors Base's own re-trace
    /// (porto.qc:178): a short worldonly traceline along the projectile's pre-bounce travel direction. The
    /// post-bounce velocity is the incoming velocity reflected about the wall, so the wall lies opposite the new
    /// velocity — we trace from just behind the origin to just ahead of it along that direction. Falls back to the
    /// negated-velocity normal (and no special surface) when nothing solid is within reach.
    /// </summary>
    private ImpactSurface ProbeImpact(Entity self, Entity other)
    {
        Vector3 fallback = SurfaceNormal(self.Velocity);
        if (self.Velocity.LengthSquared() <= 0.0001f)
            return new ImpactSurface(false, false, fallback);

        Vector3 into = -Vector3.Normalize(self.Velocity); // points into the wall the projectile bounced off
        Vector3 start = self.Origin - into * 4f;
        Vector3 end = self.Origin + into * 16f;
        TraceResult tr = Api.Trace.Trace(start, Vector3.Zero, Vector3.Zero, end, MoveFilter.WorldOnly, self);
        if (tr.Fraction >= 1f)
            return new ImpactSurface(false, false, fallback);

        bool slick = (tr.DpHitQ3SurfaceFlags & Q3SurfaceFlagSlick) != 0
                  || (tr.DpHitContents & DpContentsPlayerClip) != 0;
        bool noimpact = (tr.DpHitQ3SurfaceFlags & Q3SurfaceFlagNoImpact) != 0;
        Vector3 normal = tr.PlaneNormal.LengthSquared() > 0.0001f ? Vector3.Normalize(tr.PlaneNormal) : fallback;
        return new ImpactSurface(slick, noimpact, normal);
    }

    /// <summary>The placement plane normal, approximated from the negated travel direction (the wall faces back
    /// the way the projectile came). Used as the fallback when the impact re-probe finds no nearby solid.</summary>
    private static Vector3 SurfaceNormal(Vector3 velocity)
        => velocity.LengthSquared() > 0.0001f ? -Vector3.Normalize(velocity) : new Vector3(0, 0, 1);

    /// <summary>QC reflect: v -= 2*n*(v·n).</summary>
    private static Vector3 ReflectAbout(Vector3 v, Vector3 n)
        => v - 2f * n * Vector3.Dot(v, n);

    /// <summary>
    /// A request to realise a Porto portal as a warpzone (QC Portal_SpawnIn/OutPortalAtTrace). The host wires
    /// <see cref="PortalSpawner"/> to its <see cref="WarpzoneManager"/>: an in-portal creates a warpzone IN at
    /// the surface; the matching out-portal creates the linked OUT (paired by <see cref="PortalId"/> + owner).
    /// </summary>
    public readonly struct PortalRequest
    {
        public readonly System.Numerics.Vector3 Origin;        // the wall hit point
        public readonly System.Numerics.Vector3 SurfaceNormal; // the wall normal (warpzone forward, into the room)
        public readonly System.Numerics.Vector3 RightVector;   // QC right_vector — the portal's roll axis (carried/reflected on the projectile)
        public readonly bool IsInPortal;                       // in (entry) vs out (exit) portal
        public readonly int PortalId;                          // pairs the in/out of one shot
        public readonly Entity? Owner;
        public PortalRequest(System.Numerics.Vector3 origin, System.Numerics.Vector3 normal, System.Numerics.Vector3 rightVector, bool isIn, int id, Entity? owner)
        { Origin = origin; SurfaceNormal = normal; RightVector = rightVector; IsInPortal = isIn; PortalId = id; Owner = owner; }
    }

    /// <summary>
    /// The host's portal realiser (QC the warpzone portal subsystem). Null = no warpzone host wired (the
    /// projectile lifecycle still runs; it just places no walkable portal). Set by the server to create a
    /// warpzone from each <see cref="PortalRequest"/>.
    /// </summary>
    public static System.Action<PortalRequest>? PortalSpawner;

    /// <summary>
    /// Host hook for QC Portal_ClearWithID(owner, portal_id) — clears just the in/out portals of one shot (used
    /// when a combined cnt&lt;0 shot fails after already placing its in-portal). Null = no host wired (no-op).
    /// </summary>
    public static System.Action<Entity, int>? PortalClearWithId;

    /// <summary>
    /// Host hook for QC Portal_ClearAll_PortalsOnly(owner) — clears ALL of an owner's portals (used on
    /// player death/reset, and on a combined-shot noimpact/blue-stage hard fail). Null = no host wired (no-op).
    /// </summary>
    public static System.Action<Entity>? PortalClearAll;

    /// <summary>
    /// Host hook for QC <c>realowner.portal_in.portal_id</c> — the portal_id of the owner's CURRENT in-portal slot, or
    /// null when the owner has no in-portal placed. Used by the combined-shot (cnt&lt;0) blue stage to verify the
    /// out-portal pairs with the in-portal THIS shot just placed before committing it (porto.qc:263). Null host =
    /// unwired (the check then degrades to the prior unconditional placement).
    /// </summary>
    public static System.Func<Entity, int?>? PortalInId;

    /// <summary>
    /// Place an in/out portal at the projectile's current position/orientation, realised as a warpzone via
    /// <see cref="PortalSpawner"/> (the warpzone teleporter the player walks through). The plane forward is the
    /// impact-surface normal recovered by <see cref="ProbeImpact"/> (the true wall plane, falling back to the
    /// negated travel direction). The create sound is played by the caller (OnTouch) on the success branch,
    /// matching QC SND_PORTO_CREATE timing.
    /// </summary>
    private void PlacePortal(Entity self, Vector3 normal, bool isInPortal)
        // QC Portal_SpawnIn/OutPortalAtTrace(realowner, this.right_vector, this.portal_id): the warpzone derives the
        // portal angles from fixedvectoangles2(plane_normal, right_vector), so hand over the carried+reflected
        // right_vector (self.AVelocity) as the roll axis — not just the normal.
        => PortalSpawner?.Invoke(new PortalRequest(self.Origin, normal, self.AVelocity, isInPortal, (int)self.LTime, self.Owner));

    /// <summary>QC Send_Notification(NOTIF_ONE, this.realowner, MSG_CENTER, CENTER_PORTO_*): center-print the
    /// portal create/fail status to the firing player (skipped if the owner is gone). No-op headless (no Sink wired).</summary>
    private static void NotifyOwner(Entity self, string centerName)
    {
        if (self.Owner is { IsFreed: false } owner)
            NotificationSystem.Center(owner, centerName);
    }

    // W_Porto_Success — portal placed: clear the single-portal latch and remove the projectile.
    private void PortoSuccess(Entity self, WeaponSlot slot) => CleanupProjectile(self, slot);

    // W_Porto_Think — the lifetime ran out with the projectile still unspent.
    //   QC: if the owner reconnected/respawned as a different player -> plain delete (no fail behaviour);
    //       else W_Porto_Fail(this, false). W_Porto_Fail itself plays NO sound (the unsupported cue belongs to
    //       the touch-fail path only) — so a pure lifetime expiry is silent.
    private void PortoThink(Entity self, WeaponSlot slot)
    {
        // QC W_Porto_Think: a plain delete (no fail) if the owner is gone/reconnected as a different player.
        if (self.Owner is null || self.Owner.IsFreed)
        {
            CleanupProjectile(self, slot);
            return;
        }
        // QC sets trace_plane_normal = '0 0 0' before W_Porto_Fail, so the timeout-drop's setorigin adds no offset.
        PortoFail(self, slot, failhard: false, planeNormal: Vector3.Zero);
    }

    // W_Porto_Fail(this, failhard) — porto.qc:114. Soft-fail teardown for a combined (cnt<0) shot: clear any
    // in-portal this shot already placed, drop the single-portal latch, and — if this is a SOFT fail, the owner
    // is the same player, alive, and NO LONGER owns porto — drop the porto as a tossed pickup arcing back toward
    // the player (CENTER_PORTO_FAILED) when it would land within 128qu of them. A hard fail (death/reset) skips
    // the drop. The projectile is always removed at the end.
    private void PortoFail(Entity self, WeaponSlot slot, bool failhard, Vector3 planeNormal)
    {
        // QC: if (this.cnt < 0) Portal_ClearWithID(realowner, portal_id);  (clear the already-placed in-portal)
        if (self.Count < 0 && self.Owner is { IsFreed: false })
            PortalClearWithId?.Invoke(self.Owner, (int)self.LTime);

        Entity? owner = self.Owner;
        // QC soft-fail throwable-drop gate (porto.qc:128): cnt<0 && !failhard && playerid match && !dead &&
        // the player no longer owns porto. (The port keys "playerid match" on the live owner reference.)
        if (self.Count < 0 && !failhard
            && owner is { IsFreed: false }
            && owner.DeadState == DeadFlag.No
            && Registry<Weapon>.ByName("porto") is { } portoWep && !Inventory.HasWeapon(owner, portoWep))
        {
            TryThrowableDrop(self, slot, owner, planeNormal, portoWep);
        }

        CleanupProjectile(self, slot);
    }

    /// <summary>ITEM_D_MINS / ITEM_D_MAXS (common/items/item.qh:82-83) — the dropped-item hull the porto takes on
    /// before the toss so its tracetoss/nudge sees an item-sized box.</summary>
    private static readonly Vector3 ItemDropMins = new(-30f, -30f, 0f);
    private static readonly Vector3 ItemDropMaxs = new(30f, 30f, 48f);

    // QC W_Porto_Fail soft-fail drop body (porto.qc:131-146): size the projectile as a dropped item, nudge it
    // out of solid, compute the toss velocity that arcs it back toward the owner (trigger_push_calculatevelocity,
    // height 128), and — if the ballistic arc would land within 128qu of the owner — spawn the porto as tossed
    // loot (W_ThrowNewWeapon) and center-print CENTER_PORTO_FAILED.
    private void TryThrowableDrop(Entity self, WeaponSlot slot, Entity owner, Vector3 planeNormal, Weapon portoWep)
    {
        // setsize(this, ITEM_D_MINS, ITEM_D_MAXS); setorigin(this, this.origin + trace_plane_normal);
        Api.Entities.SetSize(self, ItemDropMins, ItemDropMaxs);
        Api.Entities.SetOrigin(self, self.Origin + planeNormal);
        // (QC nudgeoutofsolid_OrFallback gate: the engine extrication isn't reachable from this layer; after a
        // free-flight timeout the projectile origin is in open air, so proceed as if the nudge succeeded.)

        // velocity = trigger_push_calculatevelocity(origin, realowner, 128, this).
        self.Velocity = Jumppads.CalculateVelocity(self.Origin, owner, 128f, self);

        // tracetoss(this, this): does the arc land within 128qu of the owner? (QC vdist(trace_endpos-org,<,128))
        Vector3 landing = TraceTossEnd(self, self.Velocity);
        if ((landing - owner.Origin).Length() >= 128f)
            return;

        // W_ThrowNewWeapon(realowner, WEP_PORTO, 0, origin, velocity, weaponentity) — spawn the tossed pickup, then
        // CENTER_PORTO_FAILED. doreduce=false (the porto carries no ammo). The projectile `self` is removed by the
        // caller; ThrowNewWeapon spawns a fresh loot entity (QC spawns a new weapon_info, never reusing the gren).
        WeaponThrowing.ThrowNewWeapon(owner, portoWep, doreduce: false, self.Origin, self.Velocity, slot);
        NotifyOwner(self, "PORTO_FAILED");
    }

    /// <summary>Approximate QC <c>tracetoss</c> (mirrors MonsterAI.TraceToss): stepped ballistic integration of
    /// <paramref name="self"/> launched at <paramref name="vel"/> under gravity; returns the first contact point
    /// (or the end of the arc on a miss). Used to gate the soft-fail drop on "lands near the owner".</summary>
    private static Vector3 TraceTossEnd(Entity self, Vector3 vel)
    {
        float g = (Api.Services is not null ? Api.Cvars.GetFloat("sv_gravity") : 800f);
        if (g <= 0f) g = 800f;
        g *= self.Gravity != 0f ? self.Gravity : 1f;
        Vector3 pos = self.Origin;
        Vector3 v = vel;
        const float step = 0.05f;
        for (int i = 0; i < 80; i++) // ~4s of flight
        {
            Vector3 next = pos + v * step;
            TraceResult tr = Api.Trace.Trace(pos, self.Mins, self.Maxs, next, MoveFilter.Normal, self);
            if (tr.Fraction < 1f) return tr.EndPos;
            pos = next;
            v.Z -= g * step;
        }
        return pos;
    }

    // Shared teardown: clear the owner's per-slot porto_current latch for THIS projectile, then delete it.
    private void CleanupProjectile(Entity self, WeaponSlot slot)
    {
        if (self.Owner is not null)
        {
            var st = self.Owner.WeaponState(slot);
            if (ReferenceEquals(st.PortoCurrent, self)) st.PortoCurrent = null;
        }
        self.Touch = null;
        self.Think = null;
        Api.Entities.Remove(self);
    }

    /// <summary>autocvar_g_balance_powerup_strength_force — Strength powerup speed multiplier (default 3, seeded
    /// from the cvar in <see cref="Configure"/>).</summary>
    public float StrengthForce = 3f;
    // DP canonical EF_RED / EF_BLUE (dpextensions.qc:179 / :101) — the same bits networked in Entity.Effects
    // and read by the client to render the red (in-portal) vs blue (out-portal) porto projectile mid-flight.
    // (Previously 1<<14 / 1<<15, which collide with EF_SELECTABLE / EF_DOUBLESIDED and the client never saw.)
    private const int EffectRed = 128;   // EF_RED
    private const int EffectBlue = 64;   // EF_BLUE

    // METHOD(PortoLaunch, wr_resetplayer) — porto.qc:409. On respawn/reset, drop the single-portal latch so the
    // player can fire a fresh porto (QC: actor.porto_current = NULL).
    public override void WrResetPlayer(Entity actor, WeaponSlot slot)
    {
        WeaponSlotState st = actor.WeaponState(slot);
        st.PortoCurrent = null;
    }

    // W_Porto_Remove (porto.qc:151) — driven on player death by the QC Portal_ClearAll path
    // (server/portals.qc:586-590 → MakePlayerObserver / ClientDisconnect). On the owner's death we tear down ALL
    // of their live portals (Portal_ClearAll_PortalsOnly) and HARD-fail the in-flight projectile if it's still
    // theirs: clear the porto_current latch, clear this shot's in-portal if combined (cnt<0), and delete it. A hard
    // fail (failhard=true) skips the throwable-drop branch — a dead player can't drop the porto as a pickup.
    public override void WrPlayerDeath(Entity actor, WeaponSlot slot)
    {
        // Portal_ClearAll: remove every portal this owner placed (QC Portal_ClearAll_PortalsOnly).
        PortalClearAll?.Invoke(actor);

        WeaponSlotState st = actor.WeaponState(slot);
        Entity? gren = st.PortoCurrent;
        if (gren is null || gren.IsFreed) { st.PortoCurrent = null; return; }

        // W_Porto_Fail(this, failhard:true): a combined (cnt<0) shot's pending in-portal was already cleared by the
        // ClearAll above; clear the latch and delete the projectile (no sound, no throwable-drop on a hard fail).
        st.PortoCurrent = null;
        gren.Touch = null;
        gren.Think = null;
        Api.Entities.Remove(gren);
    }

    // METHOD(PortoLaunch, wr_checkammo1/2) — porto.qc (infinite ammo).
    public bool CheckAmmoPrimary(Entity actor) => true;
    public bool CheckAmmoSecondary(Entity actor) => true;
}
