using System.Numerics;
using System.Runtime.CompilerServices;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Grappling Hook — port of common/weapons/weapon/hook.{qh,qc}. Primary fires a chain that latches onto
/// geometry and reels the player in (a movement tool, consuming fuel); secondary lobs a gravity bomb
/// (MOVETYPE_TOSS, falling under its own gravity) that, on contact or at end of lifetime, pulls victims
/// inward with a strong negative-force radius blast spread out over a short duration. The bomb is shootable.
///
/// Identity/attributes from hook.qh; balance from bal-wep-xonotic.cfg (g_balance_hook_*).
/// This port covers the secondary gravity bomb (toss launch, contact/lifetime detonation, duration-spread
/// power-curve pull blast, shoot-down) AND the primary grapple: FireGrapplingHook launches the chain, it
/// latches on contact (GrapplingHook_Stop, recording the followed entity as .aiment) and reels the owner in
/// with both the elastic "tarzan" rubber-band variant (Base default g_grappling_hook_tarzan=2 — can also pull
/// a hooked player/nade, which is what feeds the vampirehook mutator) and the simple tarzan-0 straight reel,
/// driven by hook_state (FIRING/REMOVING/PULLING/RELEASING/WAITING_FOR_RELEASE) and the hooked-fuel drain.
/// Only the CSQC rope-LINE rendering (the dedicated ENT_CLIENT_HOOK stream) is left out.
/// </summary>
[Weapon]
public sealed class Hook : Weapon
{
    /// <summary>Primary-fire (grapple) balance — QC WEP_CVAR_PRI(WEP_HOOK, *).</summary>
    public struct PrimaryBalance
    {
        public float Ammo;            // g_balance_hook_primary_ammo (fuel to fire the hook)
        public float Animtime;        // g_balance_hook_primary_animtime
        public float HookedAmmo;      // g_balance_hook_primary_hooked_ammo (fuel/sec while reeling)
        public float HookedTimeFree;  // g_balance_hook_primary_hooked_time_free
        public float HookedTimeMax;   // g_balance_hook_primary_hooked_time_max
        public float Refire;          // g_balance_hook_primary_refire
    }

    /// <summary>Secondary-fire (gravity bomb) balance — QC WEP_CVAR_SEC(WEP_HOOK, *).</summary>
    public struct SecondaryBalance
    {
        public float Animtime;        // g_balance_hook_secondary_animtime
        public float Damage;          // g_balance_hook_secondary_damage
        public float DamageForceScale;// g_balance_hook_secondary_damageforcescale
        public float Duration;        // g_balance_hook_secondary_duration (blast spread over this time)
        public float EdgeDamage;      // g_balance_hook_secondary_edgedamage
        public float Force;           // g_balance_hook_secondary_force (negative = pull inward)
        public float Gravity;         // g_balance_hook_secondary_gravity
        public float Health;          // g_balance_hook_secondary_health (shootable bomb hp)
        public float Lifetime;        // g_balance_hook_secondary_lifetime
        public float Power;           // g_balance_hook_secondary_power (falloff exponent over duration)
        public float Radius;          // g_balance_hook_secondary_radius
        public float Refire;          // g_balance_hook_secondary_refire
        public float Speed;           // g_balance_hook_secondary_speed (initial upward speed)
    }

    public PrimaryBalance Primary;
    public SecondaryBalance Secondary;


    public Hook()
    {
        NetName = "hook";
        BotPickupBaseValue = 0;  // QC bot_pickupbasevalue ("rating" ATTRIB)
        AmmoType = ResourceType.Fuel;   // QC ammo_type
        DisplayName = "Grappling Hook";
        Impulse = 0;
        // WEP_FLAG_CANCLIMB | WEP_TYPE_SPLASH | WEP_FLAG_NOTRUEAIM
        SpawnFlags = WeaponFlags.CanClimb | WeaponFlags.TypeSplash | WeaponFlags.NoTrueAim;
        Color = new Vector3(0.471f, 0.817f, 0.392f);
        ViewModel = "h_hookgun.iqm";  // MDL_HOOK_VIEW
        WorldModel = "v_hookgun.md3"; // MDL_HOOK_WORLD
        ItemModel = "g_hookgun.md3";  // MDL_HOOK_ITEM
    }

    // hook.qh: ATTRIB(Hook, w_crosshair, "gfx/crosshairhook"); ATTRIB(Hook, w_crosshair_size, 0.5).
    public override string? Crosshair => "gfx/crosshairhook";
    public override float CrosshairSize => 0.5f;

    public override void Configure()
    {
        Primary.Ammo = Bal("g_balance_hook_primary_ammo", 5f);
        Primary.Animtime = Bal("g_balance_hook_primary_animtime", 0.3f);
        Primary.HookedAmmo = Bal("g_balance_hook_primary_hooked_ammo", 5f);
        Primary.HookedTimeFree = Bal("g_balance_hook_primary_hooked_time_free", 2f);
        Primary.HookedTimeMax = Bal("g_balance_hook_primary_hooked_time_max", 0f);
        Primary.Refire = Bal("g_balance_hook_primary_refire", 0.2f);

        Secondary.Animtime = Bal("g_balance_hook_secondary_animtime", 0.3f);
        Secondary.Damage = Bal("g_balance_hook_secondary_damage", 25f);
        Secondary.DamageForceScale = Bal("g_balance_hook_secondary_damageforcescale", 0f);
        Secondary.Duration = Bal("g_balance_hook_secondary_duration", 1.5f);
        Secondary.EdgeDamage = Bal("g_balance_hook_secondary_edgedamage", 5f);
        Secondary.Force = Bal("g_balance_hook_secondary_force", -2000f);
        Secondary.Gravity = Bal("g_balance_hook_secondary_gravity", 5f);
        Secondary.Health = Bal("g_balance_hook_secondary_health", 15f);
        Secondary.Lifetime = Bal("g_balance_hook_secondary_lifetime", 5f);
        Secondary.Power = Bal("g_balance_hook_secondary_power", 3f);
        Secondary.Radius = Bal("g_balance_hook_secondary_radius", 500f);
        Secondary.Refire = Bal("g_balance_hook_secondary_refire", 3f);
        Secondary.Speed = Bal("g_balance_hook_secondary_speed", 0f);

        // server/hook.qc autocvar_g_balance_grapplehook_* + autocvar_g_grappling_hook_tarzan. These were
        // previously hardcoded; read them from the balance cfg so the grapple matches Base values/feel.
        GrappleSpeedFly = Bal("g_balance_grapplehook_speed_fly", 1800f);
        GrappleSpeedPull = Bal("g_balance_grapplehook_speed_pull", 2000f);
        GrappleForceRubber = Bal("g_balance_grapplehook_force_rubber", 2000f);
        GrappleForceRubberOverstretch = Bal("g_balance_grapplehook_force_rubber_overstretch", 1000f);
        GrappleLengthMin = Bal("g_balance_grapplehook_length_min", 50f);
        GrappleStretch = Bal("g_balance_grapplehook_stretch", 50f);
        GrappleAirFriction = Bal("g_balance_grapplehook_airfriction", 0.2f);
        GrappleHealth = Bal("g_balance_grapplehook_health", 50f);
        GrappleNadeTime = Bal("g_balance_grapplehook_nade_time", 0.7f);
        // autocvar_g_balance_grapplehook_gravity: 0 = the chain flies straight (MOVETYPE_FLY); >0 makes it a
        // falling toss (MOVETYPE_TOSS with this gravity scale). Base default 0.
        GrappleGravity = Bal("g_balance_grapplehook_gravity", 0f);
        // g_balance_pause_fuel_regen: a hooked player's health/armor/fuel regen is inhibited for this long.
        PauseFuelRegen = Bal("g_balance_pause_fuel_regen", 2f);
        // autocvar_g_grappling_hook_tarzan: 0 = straight constant-speed reel, 2 = elastic rope that can
        // also pull players/nades. Base default is 2 (balance-xonotic.cfg:253).
        GrappleTarzan = (int)Bal("g_grappling_hook_tarzan", 2f);
    }

    /// <summary>g_balance_grapplehook_speed_fly — the launched chain's flight speed.</summary>
    public float GrappleSpeedFly = 1800f;
    /// <summary>g_balance_grapplehook_speed_pull — the reel-in pull speed.</summary>
    public float GrappleSpeedPull = 2000f;
    /// <summary>g_balance_grapplehook_force_rubber — spring force the elastic (tarzan) rope pulls with.</summary>
    public float GrappleForceRubber = 2000f;
    /// <summary>g_balance_grapplehook_force_rubber_overstretch — extra force applied when overstretched.</summary>
    public float GrappleForceRubberOverstretch = 1000f;
    /// <summary>g_balance_grapplehook_length_min — minimal rope length (stops pulling below this).</summary>
    public float GrappleLengthMin = 50f;
    /// <summary>g_balance_grapplehook_stretch — slack window before the rope yields more length / overstretches.</summary>
    public float GrappleStretch = 50f;
    /// <summary>g_balance_grapplehook_airfriction — air-friction while hanging on the elastic rope.</summary>
    public float GrappleAirFriction = 0.2f;
    /// <summary>g_balance_grapplehook_health — the chain's HP (shootable).</summary>
    public float GrappleHealth = 50f;
    /// <summary>g_balance_grapplehook_nade_time — re-think delay given to a hooked nade when released.</summary>
    public float GrappleNadeTime = 0.7f;
    /// <summary>g_balance_grapplehook_gravity — &gt;0 makes the chain fall (MOVETYPE_TOSS) instead of flying straight.</summary>
    public float GrappleGravity = 0f;
    /// <summary>g_balance_pause_fuel_regen — seconds a hooked player's regen is inhibited each tick while hooked.</summary>
    public float PauseFuelRegen = 2f;
    /// <summary>g_grappling_hook_tarzan — 0 = straight reel, &gt;=2 = elastic rope that can pull players/nades.</summary>
    public int GrappleTarzan = 2;

    /// <summary>
    /// QC <c>hook_shotorigin[s]</c> (server/hook.qc GrappleHookInit): the gun-muzzle offset the reel pulls
    /// toward, in view space (forward, -left, up). The offhand path hardcodes <c>'8 8 -12'</c> for every align;
    /// the in-hand path derives it from the weapon model tag (~the same). Used by GrapplingHookThink so the
    /// latch/length geometry matches Base's gun-hand offset rather than the player centre.
    /// </summary>
    public static readonly Vector3 HookShotOrigin = new(8f, 8f, -12f);

    // QC .hook_length on the chain edict: the current rubber-band rope length (the elastic/tarzan reel
    // shortens this toward minlength each tick). -1 = uninitialised (seeded to the current rope distance).
    // Kept off the chain entity via a per-hook weak table since WeaponSlotState (other file) carries no field.
    private static readonly ConditionalWeakTable<Entity, float[]> _hookLength = new();

    // The Hook's primary is continuous (reels every tick, rolls its own HookRefire), so only the secondary
    // gravity bomb uses the refire gate. Provide the gate's refire/animtime explicitly from the balance struct
    // (the base BalanceTiming cvar read would fall back to 1s if the balance cfg hasn't been loaded).
    public override float RefireFor(FireMode fire) => fire == FireMode.Secondary ? Secondary.Refire : Primary.Refire;
    public override float AnimtimeFor(FireMode fire) => fire == FireMode.Secondary ? Secondary.Animtime : 0f;

    // METHOD(Hook, wr_think) — common/weapons/weapon/hook.qc + server/hook.qc grapple lifecycle.
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);

        // The hook is a CONTINUOUS weapon (it reels every tick while primary is held), so it reads the held
        // button (st.ButtonAttack) directly rather than using the refire gate. The driver calls WrThink(Primary)
        // every tick — the lifecycle/reel state machine below therefore runs in this Primary call. The
        // secondary gravity bomb is the only thing the (ATK2-held) Secondary call does.
        if (fire == FireMode.Secondary)
        {
            // QC hook.qc wr_think: the secondary gravity bomb is gated by weapon_prepareattack on the secondary
            // refire (g_balance_hook_secondary_refire = 3s). Because the bomb is ammo-free, WITHOUT this gate
            // holding ATK2 spawns one bomb per server tick (~72/s) — unbounded projectile/knockback spam. Route
            // it through the refire gate like every other discrete secondary.
            if (PrepareAttack(actor, slot, fire))
                AttackGravityBomb(actor, slot);
            return;
        }

        if (st.ButtonAttack)
        {
            // Fire the grappling hook (FireGrapplingHook) on the press edge, gated by hook_refire and the
            // WAITING_FOR_RELEASE latch (so holding fire reels in rather than re-firing).
            if (st.Hook is null && (st.HookState & HookState.WaitingForRelease) == 0
                && st.HookRefire < Api.Clock.Time)
            {
                actor.TakeResource(AmmoType, Primary.Ammo);
                st.HookState |= HookState.Firing | HookState.WaitingForRelease;
            }
        }
        else
        {
            // primary released: drop the hook and clear the latch.
            st.HookState |= HookState.Removing;
            st.HookState &= ~HookState.WaitingForRelease;
        }

        // While hooked, drain fuel over time (after the free grace period) and stop if it runs out.
        if (st.Hook is not null)
        {
            // QC hook.qc wr_think: hook_refire = max(hook_refire, time + pri.refire * W_WeaponRateFactor(actor)).
            st.HookRefire = MathF.Max(st.HookRefire, Api.Clock.Time + Primary.Refire * WeaponRateFactor(actor));

            // QC: the hook also inhibits health regeneration, but only for 1 second (g_balance_pause_fuel_regen),
            // unless unlimited ammo. PauseRegenFinished gates the player's health/armor/fuel regen tick.
            bool unlimitedAmmo = actor.UnlimitedAmmo || (actor.Items & (1 << 0)) != 0; // IT_UNLIMITED_AMMO
            if (!unlimitedAmmo)
                actor.PauseRegenFinished = MathF.Max(actor.PauseRegenFinished, Api.Clock.Time + PauseFuelRegen);

            if (st.Hook.Health > 0f) // .state == 1 means latched (reuse Health>0 as the latched flag)
            {
                float hookedTimeMax = Primary.HookedTimeMax;
                if (hookedTimeMax > 0f && Api.Clock.Time > st.HookTimeHooked + hookedTimeMax)
                    st.HookState |= HookState.Removing;

                float hookedFuel = Primary.HookedAmmo;
                if (hookedFuel > 0f && Api.Clock.Time > st.HookTimeFuelDecrease && !unlimitedAmmo)
                {
                    float need = (Api.Clock.Time - st.HookTimeFuelDecrease) * hookedFuel;
                    if (actor.GetResource(AmmoType) >= need)
                    {
                        actor.TakeResource(AmmoType, need);
                        st.HookTimeFuelDecrease = Api.Clock.Time;
                    }
                    else
                    {
                        actor.SetResource(AmmoType, 0f);
                        st.HookState |= HookState.Removing;
                        // QC hook.qc: if (m_weapon != WEP_Null) W_SwitchWeapon_Force(actor, w_getbestweapon(...)).
                        // The slot's m_weapon is non-null only for the IN-HAND hook (the offhand drives a dedicated
                        // slot whose m_weapon stays WEP_Null, so the offhand never switches your main weapon away).
                        // Mirror that: switch to best only when this Hook is the actor's active (in-hand) weapon.
                        if (ReferenceEquals(Inventory.CurrentWeapon(actor), this)
                            && Inventory.GetBestWeapon(actor) is { } best)
                            Inventory.SwitchWeapon(actor, best);
                    }
                }
            }
            else
            {
                st.HookTimeHooked = Api.Clock.Time;
                st.HookTimeFuelDecrease = Api.Clock.Time + Primary.HookedTimeFree;
            }
        }

        // Crouch-slide releases the pull (HOOK_PULLING); without the crouch input flag we keep pulling.
        st.HookState |= HookState.Pulling;

        // Process the state machine: (re)fire or remove.
        if ((st.HookState & HookState.Firing) != 0)
        {
            if (st.Hook is not null) RemoveHook(st, actor);
            FireGrapplingHook(actor, slot, st);
            st.HookState &= ~HookState.Firing;
            // QC: hook_refire = max(hook_refire, time + g_balance_grapplehook_refire * W_WeaponRateFactor(actor)).
            st.HookRefire = MathF.Max(st.HookRefire, Api.Clock.Time + Primary.Refire * WeaponRateFactor(actor));
        }
        else if ((st.HookState & HookState.Removing) != 0)
        {
            if (st.Hook is not null) RemoveHook(st, actor);
            st.HookState &= ~HookState.Removing;
        }
    }

    // FireGrapplingHook (server/hook.qc) — launch the chain projectile that latches onto geometry.
    private void FireGrapplingHook(Entity actor, WeaponSlot slot, WeaponSlotState st)
    {
        // QC FireGrapplingHook top guard: `if (weaponLocked(actor) || actor.vehicle) return;`. A frozen player
        // (the reachable weaponLocked subset, mirroring WeaponFireDriver.WeaponLocked) or a player in a vehicle
        // can't fire the hook. The offhand path also zeroes the key in a vehicle upstream (GameWorld.cs), but the
        // direct weapon-fire path needs this guard too.
        if (IsWeaponLocked(actor) || actor.Vehicle is not null)
            return;

        Vector3 forward = QMath.Forward(actor.Angles);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward);
        // QC FireGrapplingHook: W_MuzzleFlash(WEP_HOOK, actor, weaponentity, w_shotorg, '0 0 0') — the hook's
        // generic muzzle effect (grapple_muzzleflash) at the gun muzzle. (Same effect the secondary emits.)
        EffectEmitter.Emit("HOOK_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);

        Entity hook = Api.Entities.Spawn();
        hook.ClassName = "grapplinghook";
        hook.Owner = actor;
        hook.NetName = NetName;
        // QC: set_movetype(missile, g_balance_grapplehook_gravity ? MOVETYPE_TOSS : MOVETYPE_FLY). With gravity on
        // the chain falls under its own gravity instead of flying dead straight.
        hook.MoveType = GrappleGravity != 0f ? MoveType.Toss : MoveType.Fly;
        if (GrappleGravity != 0f) hook.Gravity = GrappleGravity;
        Projectiles.MakeTrigger(hook); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
        hook.Flags = EntFlags.Item;
        hook.TakeDamage = DamageMode.Aim; // shootable
        hook.Health = 0f;                  // 0 = in flight; set >0 (latched flag) on GrapplingHook_Stop
        hook.MaxHealth = GrappleHealth;    // actual HP for shoot-down
        Api.Entities.SetSize(hook, new Vector3(-3, -3, -3), new Vector3(3, 3, 3));
        Api.Entities.SetOrigin(hook, shot.Origin);
        hook.Velocity = shot.Dir * GrappleSpeedFly;
        hook.Angles = QMath.VecToAngles(hook.Velocity);

        hook.Touch = (self, other) => GrapplingHookTouch(self, other, actor, slot);
        hook.Think = self => GrapplingHookThink(self, actor, slot);
        hook.NextThink = Api.Clock.Time;
        // GrapplingHook_Damage (server/hook.qc): on shoot-down credit the attacker as the owner's pusher for the
        // istypefrag frag-attribution window, then remove the hook (restoring the owner's movetype).
        hook.ProjectileDamage = (self, attacker) =>
        {
            if (attacker is not null && !ReferenceEquals(attacker, actor))
            {
                actor.Pusher = attacker;
                actor.PushLTime = Api.Clock.Time + Bal("g_maxpushtime", 8f);
                // QC server/hook.qc:305 — this.realowner.istypefrag = PHYS_INPUT_BUTTON_CHAT(this.realowner):
                // the grapple owner's chat-button state at shoot-down decides if the frag is a typefrag.
                actor.IsTypeFrag = actor.ButtonChat;
            }
            RemoveHook(actor.WeaponState(slot), actor);
        };

        // MUTATOR_CALLHOOK(EditProjectile, actor, missile) (server/hook.qc FireGrapplingHook).
        var ep = new MutatorHooks.EditProjectileArgs(actor, hook);
        MutatorHooks.EditProjectile.Call(ref ep);

        st.Hook = hook;
        Api.Sound.Play(actor, SoundChannel.Body, "weapons/hook_fire.wav");
    }

    // GrapplingHookTouch + GrapplingHook_Stop — latch the chain where it lands and start reeling.
    private void GrapplingHookTouch(Entity self, Entity other, Entity actor, WeaponSlot slot)
    {
        if (other.MoveType == MoveType.Follow) return;
        Api.Sound.Play(self, SoundChannel.Body, "weapons/hook_impact.wav");
        // GrapplingHook_Stop: Send_Effect(EFFECT_HOOK_IMPACT) + state=1 + MOVETYPE_NONE + stop.
        EffectEmitter.Emit("HOOK_IMPACT", self.Origin);
        self.Health = 1f;                  // latched flag (.state = 1)
        self.Velocity = Vector3.Zero;
        self.MoveType = MoveType.None;
        self.Touch = null;

        // server/hook.qc GrapplingHookTouch: SetMovetypeFollow(this, toucher) — UNCONDITIONALLY make the
        // latched chain FOLLOW whatever it hit (geometry OR a player/nade/vehicle), recording it as .aiment.
        // This is the precondition the vampirehook mutator drains off (thehook.aiment must be the hooked
        // player), and lets the elastic (tarzan>=2) reel pull a hooked player/nade in below.
        self.Aiment = other;
        self.PunchAngle = other.Angles;            // SetMovetypeFollow: original angles of the followed ent
        self.ViewOfs = self.Origin - other.Origin; // relative origin offset
        if ((other.Flags & EntFlags.Client) != 0)
        {
            // IS_PLAYER(aiment): clamp the follow offset into the player's bbox (util.qc SetMovetypeFollow).
            self.ViewOfs = new Vector3(
                QMath.Bound(other.Mins.X + 4f, self.ViewOfs.X, other.Maxs.X - 4f),
                QMath.Bound(other.Mins.Y + 4f, self.ViewOfs.Y, other.Maxs.Y - 4f),
                QMath.Bound(other.Mins.Z + 4f, self.ViewOfs.Z, other.Maxs.Z - 4f));
        }

        // keep thinking so the owner gets reeled in.
        self.Think = s => GrapplingHookThink(s, actor, slot);
        self.NextThink = Api.Clock.Time;
    }

    // GrapplingHookThink — reel the owner toward the latched chain end (tarzan rubber-band + simple reel). server/hook.qc
    private void GrapplingHookThink(Entity hook, Entity actor, WeaponSlot slot)
    {
        var st = actor.WeaponState(slot);
        if (!ReferenceEquals(st.Hook, hook)) { Api.Entities.Remove(hook); return; }

        // server/hook.qc GrapplingHookThink top guard: drop the hook if its followed entity vanished, the game
        // stopped, or the aiment is a non-nade projectile (can't sensibly reel off a rocket). (game_stopped /
        // round-not-started already force a remove via the weapon state machine + GameStopped checks.)
        if (hook.Health > 0f && hook.Aiment is { } aim0
            && (aim0.IsFreed || ((aim0.Flags & EntFlags.Item) != 0 && aim0.ClassName != "nade")))
        {
            RemoveHook(st, actor);
            return;
        }

        hook.NextThink = Api.Clock.Time;

        // MUTATOR_CALLHOOK(GrappleHookThink, this) — server/hook.qc GrapplingHookThink. vampirehook drains
        // health from the hooked player here: it reads thehook.aiment (the hooked player), which the latch above
        // now records via the SetMovetypeFollow port, so a direct hook hit on an enemy drains as in Base.
        var gh = new MutatorHooks.GrappleHookThinkArgs(hook);
        MutatorHooks.GrappleHookThink.Call(ref gh);

        if (hook.Health <= 0f) return; // still in flight, not latched yet

        // MOVETYPE_FOLLOW: a latched hook stays glued to whatever it hit. The port stores the followed entity
        // as hook.Aiment (set in GrapplingHookTouch); keep the chain's origin tracking a moving aiment so a hook
        // latched onto a moving player/platform doesn't stay fixed in world space (server/hook.qc set_movetype FOLLOW).
        if (hook.Aiment is { IsFreed: false } follow)
            Api.Entities.SetOrigin(hook, follow.Origin + hook.ViewOfs);

        // server/hook.qc GrapplingHookThink: the reel pulls toward the GUN MUZZLE, not the player centre —
        //   makevectors(v_angle); vs = hook_shotorigin[s] ('8 8 -12'); vs = v_forward*vs.x + v_right*-vs.y + v_up*vs.z;
        //   org = origin + view_ofs + vs;
        // (hook_shotorigin is the same '8 8 -12' for all aligns in the offhand path, and ~that for in-hand.) The
        // v_right*-vs.y term matches the port's v_right = -Left convention (BoneMatrix.cs). Use the player's view
        // angles (NadeThrow/NadeProjectile convention) so the latch/length geometry matches Base's gun-hand offset.
        Vector3 viewAngles = actor.ViewAngles == Vector3.Zero ? actor.Angles : actor.ViewAngles;
        QMath.AngleVectors(viewAngles, out Vector3 vf, out Vector3 vr, out Vector3 vu);
        Vector3 vs = vf * HookShotOrigin.X + vr * -HookShotOrigin.Y + vu * HookShotOrigin.Z;
        Vector3 myorg = actor.Origin + actor.ViewOfs + vs;
        Vector3 toHook = hook.Origin - myorg;
        float dist = toHook.Length();
        Vector3 dir = dist != 0f ? toHook * (1f / dist) : Vector3.Zero;

        // server/hook.qc GrapplingHookThink: tarzan (default 2) = elastic rubber-band rope (can also pull a
        // hooked player/nade); tarzan 0 = the simple straight constant-speed reel. The mutator hook may rewrite
        // tarzan/pull_entity, but the port's GrappleHookThinkArgs carries no such args yet, so use the cvar.
        int tarzan = GrappleTarzan;
        float frametime = Api.Clock.FrameTime;

        float[] hl = _hookLength.GetValue(hook, static _ => new float[1] { -1f });
        if (hl[0] < 0f) hl[0] = (myorg - hook.Origin).Length();

        if (tarzan != 0)
        {
            // Elastic rope: shorten the rope toward minlength, spring the player along it (with air friction),
            // and — for tarzan>=2 — push a hooked player/nade with half the impulse so they get reeled too.
            Vector3 v = actor.Velocity;
            Vector3 v0 = v;

            HookState hookState = actor.WeaponState(slot).HookState;

            if ((hookState & HookState.Pulling) != 0)
            {
                float newlength = MathF.Max(hl[0] - GrappleSpeedPull * frametime, GrappleLengthMin);
                if (newlength < dist - GrappleStretch) // overstretched?
                {
                    newlength = dist - GrappleStretch;
                    if (QMath.Dot(v, dir) < 0f) // only if not already moving in hook direction
                        v += frametime * dir * GrappleForceRubberOverstretch;
                }
                hl[0] = newlength;
            }

            if (actor.MoveType == MoveType.Fly)
                actor.MoveType = MoveType.Walk;

            if ((hookState & HookState.Releasing) != 0)
            {
                hl[0] = dist;
            }
            else
            {
                // then pull the player along the rope toward the latch point
                float spring = QMath.Bound(0f, (dist - hl[0]) / GrappleStretch, 1f);
                v *= 1f - frametime * GrappleAirFriction;
                v += frametime * dir * spring * GrappleForceRubber;

                Vector3 dv = QMath.Dot(v - v0, dir) * dir;
                if (tarzan >= 2 && hook.Aiment is { IsFreed: false } aim)
                {
                    bool aimWalks = aim.MoveType == MoveType.Walk;
                    bool aimNade = aim.ClassName == "nade";
                    if (aimWalks || aimNade)
                    {
                        v -= dv * 0.5f;
                        aim.Velocity -= dv * 0.5f;
                        aim.Flags &= ~EntFlags.OnGround;
                        if (aimNade)
                            aim.NextThink = Api.Clock.Time + GrappleNadeTime; // delay its think after letting go
                    }
                }

                actor.Flags &= ~EntFlags.OnGround;
            }

            // server/hook.qc: pull_entity.velocity is only written `if (!frozen_pulling && !(aiment.flags &
            // FL_PROJECTILE))`. frozen_pulling needs g_balance_grapplehook_pull_frozen (port models it as 0 →
            // always false), so the live guard is: don't reel off a projectile aiment (e.g. a hooked rocket).
            bool aimentIsProjectile = hook.Aiment is { IsFreed: false } aimv
                && (aimv.Flags & EntFlags.Item) != 0 && aimv.ClassName != "nade";
            if (!aimentIsProjectile)
                actor.Velocity = v;
        }
        else
        {
            // tarzan 0: the simple straight reel — pull toward a point just short of the hook; ramp down near it.
            Vector3 end = hook.Origin - dir * GrappleLengthMin;
            float edist = (end - myorg).Length();
            float spd;
            if (edist < 200f) spd = edist * (GrappleSpeedPull / 200f);
            else spd = GrappleSpeedPull;
            if (spd < 50f) spd = 0f;
            actor.Velocity = dir * spd;
            actor.MoveType = MoveType.Fly;          // QC sets the owner to MOVETYPE_FLY while reeling
            actor.Flags &= ~EntFlags.OnGround;
        }
    }

    // RemoveHook / GrapplingHookReset — drop the chain and restore the owner's movement.
    private void RemoveHook(WeaponSlotState st, Entity? owner = null)
    {
        // QC RemoveHook: if (player.move_movetype == MOVETYPE_FLY) set_movetype(player, MOVETYPE_WALK);
        // The non-tarzan straight reel puts the owner in MOVETYPE_FLY; restore WALK explicitly on remove so the
        // player doesn't keep flying after the hook is dropped (the tarzan reel already flips FLY->WALK per think).
        if (owner is not null && owner.MoveType == MoveType.Fly)
            owner.MoveType = MoveType.Walk;
        if (st.Hook is not null)
        {
            Api.Entities.Remove(st.Hook);
            st.Hook = null;
        }
    }

    /// <summary>
    /// QC <c>RemoveGrapplingHooks(entity pl)</c> (server/hook.qc:30): drop ALL of a player's grappling-hook
    /// chains (every weapon slot) and restore the owner's movetype (FLY → WALK, since the non-tarzan reel puts
    /// the owner in MOVETYPE_FLY). Called when the player is relocated/frozen/teleported so the hook doesn't
    /// stay latched through the move (teleport.qc:60, freezetag, status_effects/frozen, vehicles, client reset).
    /// </summary>
    public static void RemoveGrapplingHooks(Entity pl)
    {
        if (pl.MoveType == MoveType.Fly)
            pl.MoveType = MoveType.Walk;

        pl.ForEachWeaponSlot(s =>
        {
            if (s.Hook is not null)
            {
                Api.Entities.Remove(s.Hook);
                s.Hook = null;
            }
        });
    }

    // W_Hook_Attack2 — lob a gravity bomb that falls under its own gravity and pulls victims in. hook.qc
    private void AttackGravityBomb(Entity actor, WeaponSlot slot)
    {
        // QC leaves secondary ammo-free for now (WEAPONTODO), so no TakeResource here.
        Vector3 forward = QMath.Forward(actor.Angles);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, recoil: 4f); // QC hook.qc hookbomb recoil 4

        Entity gren = Api.Entities.Spawn();
        gren.ClassName = "hookbomb";
        gren.Owner = actor;
        gren.NetName = NetName;
        gren.MoveType = MoveType.Toss;
        Projectiles.MakeTrigger(gren); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
        gren.Flags = EntFlags.Item; // QC FL_PROJECTILE
        gren.Gravity = Secondary.Gravity;
        Api.Entities.SetSize(gren, Vector3.Zero, Vector3.Zero);
        Api.Entities.SetOrigin(gren, shot.Origin);

        gren.TakeDamage = DamageMode.Yes; // shootable
        gren.Health = Secondary.Health;

        // QC: gren.velocity = '0 0 1' * speed (just an initial vertical nudge; then it falls).
        gren.Velocity = new Vector3(0f, 0f, Secondary.Speed);
        gren.Angles = Vector3.Zero;

        // nextthink = time + lifetime; the lifetime think starts the blast (adaptor_think2use_hittype_splash).
        gren.Touch = (self, other) => StartBlast(self);
        gren.Think = self => StartBlast(self);
        gren.NextThink = Api.Clock.Time + Secondary.Lifetime;
        // W_Hook_Damage: a shot-down bomb starts its pull blast early.
        gren.ProjectileDamage = (self, attacker) => StartBlast(self);

        // MUTATOR_CALLHOOK(EditProjectile, actor, gren) (hook.qc W_Hook_Attack2).
        var ep = new MutatorHooks.EditProjectileArgs(actor, gren);
        MutatorHooks.EditProjectile.Call(ref ep);

        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/hookbomb_fire.wav");
        EffectEmitter.Emit("HOOK_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);
    }

    // W_Hook_Explode2 — begin the duration-spread gravity blast (pulls victims in over `duration`). hook.qc
    private void StartBlast(Entity self)
    {
        self.Touch = null;
        self.TakeDamage = DamageMode.No;
        // QC W_Hook_Explode2: this.effects |= EF_NODRAW — hide the bomb model while the pull ticks (the visible
        // explosion is the EFFECT_HOOK_EXPLODE particle below, not the bomb itself).
        self.Effects |= EffectFlags.NoDraw;
        self.MoveType = MoveType.None;
        EffectEmitter.Emit("HOOK_EXPLODE", self.Origin);

        // QC W_Hook_Attack2: missile.projectiledeathtype = WEP_HOOK.m_id | HITTYPE_SECONDARY. W_Hook_ExplodeThink
        // then ORs HITTYPE_SPAM after the first tick, so subsequent ticks no longer count toward team-damage
        // complaints / frozen-victim spam. Carry the secondary deathtype as a string so the hittype bits ride.
        string death = Damage.DeathTypes.WithHitType(Damage.DeathTypes.FromWeapon(NetName), Damage.DeathTypes.Secondary);

        // QC ticks W_Hook_ExplodeThink every 0.05s for `duration`, applying the *delta* of a power-curve
        // falloff each tick so the total blast == one full RadiusDamage. teleport_time marks the blast start.
        float startTime = Api.Clock.Time;
        self.NextThink = Api.Clock.Time;
        self.Think = s => ExplodeThink(s, startTime, lastFrac: 1f, death);
        self.Think(self);
    }

    // W_Hook_ExplodeThink — apply this tick's share of the pull blast; repeat until duration elapses. hook.qc
    private void ExplodeThink(Entity self, float startTime, float lastFrac, string death)
    {
        float dt = Api.Clock.Time - startTime;
        // dmg_remaining = clamp(1 - dt/duration, 0, 1) ^ power; f = previous_remaining - this_remaining.
        float remaining = MathF.Pow(QMath.Clamp(1f - dt / Secondary.Duration, 0f, 1f), Secondary.Power);
        float f = lastFrac - remaining;

        if (f > 0f)
        {
            WeaponSplash.RadiusDamage(self, self.Origin, f * Secondary.Damage, f * Secondary.EdgeDamage,
                Secondary.Radius, self.Owner, RegistryId, f * Secondary.Force, deathTag: death);
        }

        // QC: this.projectiledeathtype |= HITTYPE_SPAM (after the RadiusDamage of the FIRST tick onward).
        death = Damage.DeathTypes.WithHitType(death, Damage.DeathTypes.Spam);

        if (dt < Secondary.Duration)
        {
            self.NextThink = Api.Clock.Time + 0.05f;
            self.Think = s => ExplodeThink(s, startTime, remaining, death);
        }
        else
        {
            Api.Entities.Remove(self);
        }
    }

    // METHOD(Hook, wr_resetplayer) — common/weapons/weapon/hook.qc:240-249. On (re)spawn / round reset, drop
    // ALL of the player's grapple chains (RemoveGrapplingHooks restores FLY->WALK + clears every slot's .hook)
    // and reset this slot's hook timers (hook_time = 0, hook_refire = time). The port dispatches WrResetPlayer
    // per populated slot (SpawnSystem); RemoveGrapplingHooks(actor) is idempotent across the repeated calls.
    public override void WrResetPlayer(Entity actor, WeaponSlot slot)
    {
        RemoveGrapplingHooks(actor);
        var st = actor.WeaponState(slot);
        st.HookState = HookState.None;
        st.HookTimeHooked = 0f;
        st.HookTimeFuelDecrease = 0f;
        st.HookRefire = Api.Clock.Time;
    }

    // METHOD(Hook, wr_checkammo1) — hook.qc (needs fuel to fire the hook).
    public bool CheckAmmoPrimary(Entity actor) => actor.GetResource(AmmoType) >= Primary.Ammo;

    // METHOD(Hook, wr_checkammo2) — hook.qc (secondary is ammo-free for now).
    public bool CheckAmmoSecondary(Entity actor) => true;

    /// <summary>
    /// QC <c>weaponLocked(player)</c> (server/weapons/weaponsystem.qc), the reachable subset — mirrors
    /// <c>WeaponFireDriver.WeaponLocked</c> / CampcheckMutator.WeaponLocked: the gametype freeze stat
    /// (<see cref="Entity.FrozenStat"/>, e.g. Freeze Tag) OR the <c>STATUSEFFECT_Frozen</c> status effect. The
    /// game-start/game-stopped/player_blocked/LockWeapon halves are gated upstream / unmodeled in the port.
    /// </summary>
    private static bool IsWeaponLocked(Entity e)
        => e.FrozenStat != 0 || (StatusEffectsCatalog.Frozen is { } f && StatusEffectsCatalog.Has(e, f));
}
