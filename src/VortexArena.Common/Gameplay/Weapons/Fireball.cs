using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Fireball — port of common/weapons/weapon/fireball.{qh,qc}. A splash superweapon. Primary charges up
/// then launches a slow, large fireball (MOVETYPE_FLY) that deals heavy radius damage over a big radius on
/// impact, plus a BFG-style secondary blast on every visible enemy nearby. Secondary lobs gravity-affected
/// bouncing "firemines" (MOVETYPE_BOUNCE) that set players alight on contact. The fireball is shootable.
///
/// Identity/attributes from fireball.qh; balance from bal-wep-xonotic.cfg (g_balance_fireball_*).
/// This port covers both projectiles, the fireball's contact/lifetime explosion + the BFG secondary blast
/// on every visible enemy (LOS-gated, distance-scaled), the continuous laser scorch (W_Fireball_LaserPlay
/// applying a burning status effect), the firemine's bounce/ignite lifecycle (Fire_AddDamage burn over
/// damagetime), and shoot-down. Only the charge-up prefire animation frames are render-only. It uses no ammo.
/// </summary>
[Weapon]
public sealed class Fireball : Weapon
{
    /// <summary>Primary-fire (fireball) balance — QC WEP_CVAR_PRI(WEP_FIREBALL, *).</summary>
    public struct PrimaryBalance
    {
        public float Animtime;        // g_balance_fireball_primary_animtime
        public float BfgDamage;       // g_balance_fireball_primary_bfgdamage
        public float BfgForce;        // g_balance_fireball_primary_bfgforce
        public float BfgRadius;       // g_balance_fireball_primary_bfgradius
        public float Damage;          // g_balance_fireball_primary_damage
        public float DamageForceScale;// g_balance_fireball_primary_damageforcescale
        public float EdgeDamage;      // g_balance_fireball_primary_edgedamage
        public float Force;           // g_balance_fireball_primary_force
        public float Health;          // g_balance_fireball_primary_health (shootable; 0 = not)
        public float LaserBurnTime;   // g_balance_fireball_primary_laserburntime
        public float LaserDamage;     // g_balance_fireball_primary_laserdamage
        public float LaserEdgeDamage; // g_balance_fireball_primary_laseredgedamage
        public float LaserRadius;     // g_balance_fireball_primary_laserradius
        public float Lifetime;        // g_balance_fireball_primary_lifetime
        public float Radius;          // g_balance_fireball_primary_radius
        public float Refire;          // g_balance_fireball_primary_refire
        public float Refire2;         // g_balance_fireball_primary_refire2
        public float Speed;           // g_balance_fireball_primary_speed
        public float Spread;          // g_balance_fireball_primary_spread
    }

    /// <summary>Secondary-fire (firemine) balance — QC WEP_CVAR_SEC(WEP_FIREBALL, *).</summary>
    public struct SecondaryBalance
    {
        public float Animtime;        // g_balance_fireball_secondary_animtime
        public float Damage;          // g_balance_fireball_secondary_damage (fire-on-touch)
        public float DamageForceScale;// g_balance_fireball_secondary_damageforcescale
        public float DamageTime;      // g_balance_fireball_secondary_damagetime (burn duration)
        public float LaserBurnTime;   // g_balance_fireball_secondary_laserburntime
        public float LaserDamage;     // g_balance_fireball_secondary_laserdamage
        public float LaserEdgeDamage; // g_balance_fireball_secondary_laseredgedamage
        public float LaserRadius;     // g_balance_fireball_secondary_laserradius
        public float Lifetime;        // g_balance_fireball_secondary_lifetime
        public float Refire;          // g_balance_fireball_secondary_refire
        public float Speed;           // g_balance_fireball_secondary_speed (forward launch speed)
        public float SpeedUp;         // g_balance_fireball_secondary_speed_up (vertical launch speed)
        public float SpeedZ;          // g_balance_fireball_secondary_speed_z
        public float Spread;          // g_balance_fireball_secondary_spread
    }

    public PrimaryBalance Primary;
    public SecondaryBalance Secondary;

    public Fireball()
    {
        NetName = "fireball";
        DisplayName = "Fireball";
        Impulse = 9;
        // WEP_FLAG_SUPERWEAPON | WEP_TYPE_SPLASH | WEP_FLAG_NODUAL
        SpawnFlags = WeaponFlags.SuperWeapon | WeaponFlags.TypeSplash | WeaponFlags.NoDual;
        Color = new Vector3(0.941f, 0.522f, 0.373f);
        ViewModel = "h_fireball.iqm";  // MDL_FIREBALL_VIEW
        WorldModel = "v_fireball.md3"; // MDL_FIREBALL_WORLD
        ItemModel = "g_fireball.md3";  // MDL_FIREBALL_ITEM
    }

    public override void Configure()
    {
        Primary.Animtime = Bal("g_balance_fireball_primary_animtime", 0.4f);
        Primary.BfgDamage = Bal("g_balance_fireball_primary_bfgdamage", 100f);
        Primary.BfgForce = Bal("g_balance_fireball_primary_bfgforce", 0f);
        Primary.BfgRadius = Bal("g_balance_fireball_primary_bfgradius", 1000f);
        Primary.Damage = Bal("g_balance_fireball_primary_damage", 200f);
        Primary.DamageForceScale = Bal("g_balance_fireball_primary_damageforcescale", 0f);
        Primary.EdgeDamage = Bal("g_balance_fireball_primary_edgedamage", 50f);
        Primary.Force = Bal("g_balance_fireball_primary_force", 600f);
        Primary.Health = Bal("g_balance_fireball_primary_health", 0f);
        Primary.LaserBurnTime = Bal("g_balance_fireball_primary_laserburntime", 0.5f);
        Primary.LaserDamage = Bal("g_balance_fireball_primary_laserdamage", 80f);
        Primary.LaserEdgeDamage = Bal("g_balance_fireball_primary_laseredgedamage", 20f);
        Primary.LaserRadius = Bal("g_balance_fireball_primary_laserradius", 256f);
        Primary.Lifetime = Bal("g_balance_fireball_primary_lifetime", 15f);
        Primary.Radius = Bal("g_balance_fireball_primary_radius", 200f);
        Primary.Refire = Bal("g_balance_fireball_primary_refire", 2f);
        Primary.Refire2 = Bal("g_balance_fireball_primary_refire2", 0f);
        Primary.Speed = Bal("g_balance_fireball_primary_speed", 1200f);
        Primary.Spread = Bal("g_balance_fireball_primary_spread", 0f);

        Secondary.Animtime = Bal("g_balance_fireball_secondary_animtime", 0.3f);
        Secondary.Damage = Bal("g_balance_fireball_secondary_damage", 40f);
        Secondary.DamageForceScale = Bal("g_balance_fireball_secondary_damageforcescale", 4f);
        Secondary.DamageTime = Bal("g_balance_fireball_secondary_damagetime", 5f);
        Secondary.LaserBurnTime = Bal("g_balance_fireball_secondary_laserburntime", 0.5f);
        Secondary.LaserDamage = Bal("g_balance_fireball_secondary_laserdamage", 50f);
        Secondary.LaserEdgeDamage = Bal("g_balance_fireball_secondary_laseredgedamage", 20f);
        Secondary.LaserRadius = Bal("g_balance_fireball_secondary_laserradius", 110f);
        Secondary.Lifetime = Bal("g_balance_fireball_secondary_lifetime", 7f);
        Secondary.Refire = Bal("g_balance_fireball_secondary_refire", 1.5f);
        Secondary.Speed = Bal("g_balance_fireball_secondary_speed", 900f);
        Secondary.SpeedUp = Bal("g_balance_fireball_secondary_speed_up", 100f);
        Secondary.SpeedZ = Bal("g_balance_fireball_secondary_speed_z", 0f);
        Secondary.Spread = Bal("g_balance_fireball_secondary_spread", 0f);
    }

    // METHOD(Fireball, wr_think) — common/weapons/weapon/fireball.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        if (fire == FireMode.Primary)
        {
            // QC: weapon_prepareattack(..., primary refire) gates one fire, then the 5-frame charge-up chain
            // (W_Fireball_Attack1_Frame0..4) plays the prefire windup over ~4×animtime BEFORE the launch; the
            // launch (W_Fireball_Attack1) only happens at Frame4. PrepareAttack already commits WS_INUSE +
            // advances the refire timer + schedules a become-READY think — we override that pending think with
            // the prefire chain so the slot stays busy through the windup and only returns to READY after launch.
            // (QC's fireball_primarytime / refire2 secondary gate defaults to 0 — a no-op — so it is omitted.)
            if (PrepareAttack(actor, slot, fire))
                FireballPrefireFrame(actor, slot, 0);
        }
        else if (fire == FireMode.Secondary)
        {
            if (PrepareAttack(actor, slot, fire))
                Attack2(actor, slot);
        }
    }

    /// <summary>
    /// Port of the W_Fireball_Attack1_Frame0..4 charge-up chain (fireball.qc): each frame plays the swirling
    /// EFFECT_FIREBALL_PRE_MUZZLEFLASH at one of the four muzzle corners (Frame0..3), Frame0 also fires the
    /// SND_FIREBALL_PREFIRE2 "supercharge" cue, and Frame4 finally launches the fireball (W_Fireball_Attack1).
    /// Frames are spaced one animtime apart (weapon_thinkf), scaled by the weapon rate factor.
    /// </summary>
    private void FireballPrefireFrame(Entity actor, WeaponSlot slot, int frame)
    {
        WeaponSlotState st = actor.WeaponState(slot);
        // A weapon switch (or death) may have moved us off this action mid-windup; abort the chain if so.
        if (st.State != WeaponFireState.InUse || st.CurrentWeaponId != RegistryId)
            return;

        if (frame >= 4)
        {
            // W_Fireball_Attack1_Frame4: launch, then weapon_thinkf(..., animtime, w_ready).
            Attack1(actor, slot);
            WeaponFireDriver.ScheduleThink(st, MathF.Max(0f, Primary.Animtime) * WeaponRateFactor(actor),
                static (pl, sl) =>
                {
                    WeaponSlotState s2 = pl.WeaponState(sl);
                    if (s2.State == WeaponFireState.InUse) s2.State = WeaponFireState.Ready;
                });
            return;
        }

        // W_Fireball_AttackEffect: the prefire muzzleflash, nudged to the frame's muzzle corner.
        AttackEffect(actor, frame);
        if (frame == 0)
            // Frame0: sound(actor, CH_WEAPON_SINGLE, SND_FIREBALL_PREFIRE2, VOL_BASE, ATTEN_NORM).
            Api.Sound.Play(actor, SoundChannel.WeaponSingle, "weapons/fireball_prefire2.wav");

        // weapon_thinkf(..., animtime, <next frame>): chain the next charge frame after one animtime.
        int next = frame + 1;
        WeaponFireDriver.ScheduleThink(st, MathF.Max(0f, Primary.Animtime) * WeaponRateFactor(actor),
            (pl, sl) => FireballPrefireFrame(pl, sl, next));
    }

    /// <summary>
    /// Port of W_Fireball_AttackEffect (fireball.qc): emit EFFECT_FIREBALL_PRE_MUZZLEFLASH from the shot origin
    /// offset to the frame's muzzle corner (f_diff = ±1.25 right ±3.75 up). Frames 0..3 cycle the four corners.
    /// </summary>
    private void AttackEffect(Entity actor, int frame)
    {
        // f_diff per frame, matching W_Fireball_Attack1_Frame0..3 (x = up, y = right in QC's vector literal).
        (float up, float right) = frame switch
        {
            0 => (-1.25f, -3.75f),
            1 => (1.25f, -3.75f),
            2 => (-1.25f, 3.75f),
            _ => (1.25f, 3.75f),
        };
        QMath.AngleVectors(actor.Angles, out Vector3 forward, out Vector3 vright, out Vector3 vup);
        // W_SetupShot_ProjectileSize with recoil 0 / SND_Null: just resolve the shot origin+dir (no sound/recoil).
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, new Vector3(-16, -16, -16), new Vector3(16, 16, 16), recoil: 0f);
        Vector3 org = shot.Origin + up * vup + right * vright; // w_shotorg += f_diff.x*v_up + f_diff.y*v_right
        EffectEmitter.Emit("FIREBALL_PRE_MUZZLEFLASH", org, shot.Dir * 1000f, 1, except: actor);
    }

    // Refire/animtime from the (cvar-seeded) per-mode balance blocks.
    public override float RefireFor(FireMode fire) => fire == FireMode.Secondary ? Secondary.Refire : Primary.Refire;
    public override float AnimtimeFor(FireMode fire) => fire == FireMode.Secondary ? Secondary.Animtime : Primary.Animtime;

    // METHOD(Fireball, wr_aim) — common/weapons/weapon/fireball.qc. The bot toggles between the primary fireball
    // and the secondary firemine on a slow random clock (the actor's .bot_primary_fireballmooth). While the
    // toggle is clear it leads/fires the slow primary fireball; once it rolls onto the secondary it lobs firemines
    // until it rolls back. The brain leads + decides the shot generically (via BotAimShotSpeed below); this hook
    // only picks WHICH button that decided shot is routed to and advances the QC toggle's random flip.
    public override bool BotWantsSecondary(float enemyDistance, float skill, ref BotAimState ctx)
    {
        // ctx.SecondaryToggle mirrors actor.bot_primary_fireballmooth. The button QC presses THIS frame is the
        // PRE-flip toggle state (QC checks !bot_primary_fireballmooth, fires the matching mode, THEN may flip).
        bool fireSecondary = ctx.SecondaryToggle;
        if (!ctx.SecondaryToggle)
        {
            // primary path: random() < 0.02 -> start preferring the secondary firemine next time.
            if (ctx.Random01 < 0.02f)
                ctx.SecondaryToggle = true;
        }
        else
        {
            // secondary path: random() < 0.01 -> go back to the primary fireball next time.
            if (ctx.Random01 < 0.01f)
                ctx.SecondaryToggle = false;
        }
        return fireSecondary;
    }

    // METHOD(Fireball, wr_aim): QC calls bot_aim with DIFFERENT speeds depending on the current toggle:
    //   primary path:   bot_aim(speed 1200, up 0,   lifetime, false, false)
    //   secondary path: bot_aim(speed 900,  up 100, lifetime, true,  false)
    // The brain calls BotAimShotSpeed(defaultSpeed, ref ctx) BEFORE BotWantsSecondary (which routes the fire
    // button). ctx.SecondaryToggle carries the PRE-flip toggle value — the mode the bot will actually fire this
    // frame — so we read it here to select the correct lead speed. The lead is bot_shotlead, which uses ONLY the
    // HORIZONTAL shotspeed (aim.qc:336: targvelocity * (delay + dist/shotspeed)); the vertical speed_up=100 is
    // consumed solely by bot_aim's gravity arc (findtrajectorywithleading, applygravity=true) — handled
    // independently by the brain's BallisticArc. So the faithful secondary lead speed is the horizontal 900.
    public override float BotAimShotSpeed(float defaultSpeed) => Primary.Speed; // fallback: primary speed

    public override float BotAimShotSpeed(float defaultSpeed, ref BotAimState ctx)
    {
        // Secondary firemine leads by the horizontal shotspeed 900 (QC bot_aim's shotspeed arg → bot_shotlead);
        // speed_up 100 is the arc-only component, not part of the lead distance. Primary fireball leads by 1200.
        return ctx.SecondaryToggle ? Secondary.Speed : Primary.Speed;
    }

    // W_Fireball_Attack1 — launch a large, slow, shootable fireball that bursts on impact. fireball.qc
    private void Attack1(Entity actor, WeaponSlot slot)
    {
        // Fireball uses no ammo (wr_checkammo always true).
        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, new Vector3(-16, -16, -16), new Vector3(16, 16, 16), recoil: 2f);

        Entity proj = Api.Entities.Spawn();
        proj.ClassName = "plasma_prim";
        proj.Owner = actor;
        proj.NetName = NetName;
        proj.MoveType = MoveType.Fly;
        Projectiles.MakeTrigger(proj); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
        proj.Flags = EntFlags.Item; // QC FL_PROJECTILE
        proj.Team = actor.Team;
        Api.Entities.SetSize(proj, new Vector3(-16, -16, -16), new Vector3(16, 16, 16));
        Api.Entities.SetOrigin(proj, shot.Origin);

        // Shootable fireball: QC W_Fireball_Attack1 sets takedamage = DAMAGE_YES and event_damage =
        // W_Fireball_Damage UNCONDITIONALLY (health defaults to 0, but the fireball is still marked damageable).
        // W_Fireball_Damage returns early when RES_HEALTH <= 0, so a stock 0-HP fireball dies on any hit without
        // depleting — Projectiles.MakeShootable's ShootDown reproduces this exactly (its hp<=0 recursion guard
        // returns immediately at default 0 HP). A server raising fireball_health then gets the real
        // W_CheckProjectileDamage gate + RES_HEALTH subtraction + explode-on-deplete.
        proj.TakeDamage = DamageMode.Yes;
        proj.Health = Primary.Health;

        // W_SetupProjVelocity_PRI: velocity = w_shotdir * speed (spread normally 0).
        proj.Velocity = WeaponFiring.ProjectileVelocity(shot.Dir, Vector3.UnitZ, Primary.Speed, 0f, 0f, Primary.Spread);
        proj.Angles = QMath.VecToAngles(proj.Velocity);

        // QC fireball.qc:164-165 — flag the fireball as a dodgeable hazard (rating = primary damage).
        proj.BotDodge = true;
        proj.BotDodgeRating = Primary.Damage;

        // pushltime = time + lifetime is the explode-at-end-of-life deadline (W_Fireball_Think). cnt 0 = a
        // fresh fireball (not timed-out / shot-down): only then does the BFG sweep fire (W_Fireball_Explode).
        float deathTime = Api.Clock.Time + Primary.Lifetime;
        proj.Cnt = 0;
        proj.Touch = (self, other) => Explode(self, other);
        proj.Think = self => OnFireballThink(self, deathTime);
        proj.NextThink = Api.Clock.Time;
        // proj.use = W_Fireball_Explode_use: a map trigger/use detonates the fireball (directhit = the trigger).
        proj.Use = (self, activator) => Explode(self, activator);
        // W_Fireball_Damage: a shot-down fireball explodes when its HP is depleted (cnt=1 so the resulting blast
        // skips the BFG sweep, like a timed-out fireball). MakeShootable installs the W_CheckProjectileDamage gate
        // (exception -1, "no exceptions" — matching W_Fireball_Damage's W_CheckProjectileDamage(..., -1)) and the
        // RES_HEALTH subtraction; this ProjectileDamage callback is the W_PrepareExplosionByDamage explode handler
        // it fires at hp<=0.
        proj.ProjectileDamage = (self, attacker) => { self.Cnt = 1; Explode(self, null); };
        Projectiles.MakeShootable(proj, exception: -1f);

        // MUTATOR_CALLHOOK(EditProjectile, actor, proj) (fireball.qc W_Fireball_Attack1).
        var ep = new MutatorHooks.EditProjectileArgs(actor, proj);
        MutatorHooks.EditProjectile.Call(ref ep);

        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/fireball_fire2.wav");
        EffectEmitter.Emit("FIREBALL_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);
        // NOTE: QC CSQCProjectile(proj, PROJECTILE_FIREBALL) attaches the plasma flight trail (EFFECT "FIREBALL")
        // — a CSQC render concern networked off the projectile, deferred here like the other weapons' trails.
    }

    // W_Fireball_Think — tick the fireball; scorch a nearby enemy each tick; explode at end of lifetime. fireball.qc
    private void OnFireballThink(Entity self, float deathTime)
    {
        if (Api.Clock.Time > deathTime)
        {
            // QC: this.cnt = 1; this.projectiledeathtype |= HITTYPE_SPLASH; W_Fireball_Explode(this, NULL).
            // cnt = 1 suppresses the BFG sweep on a timed-out fireball (Explode's !cnt gate).
            self.Cnt = 1;
            Explode(self, null);
            return;
        }
        // Periodic laser scorch on a nearby enemy (sets them alight).
        LaserPlay(self, Primary.LaserRadius, Primary.LaserDamage, Primary.LaserEdgeDamage, Primary.LaserBurnTime);
        self.NextThink = Api.Clock.Time + 0.1f;
    }

    /// <summary>
    /// Port of W_Fireball_LaserPlay (fireball.qc): pick one nearby damageable enemy (weighted toward closer
    /// targets, preferring ones not already burning) within <paramref name="dist"/> and set it alight for
    /// <paramref name="burnTime"/> seconds, with the burn rate scaled by distance (damage at the core down
    /// to edgedamage at the rim). The burn routes through Fire_AddDamage (frame-rate-independent DOT + the
    /// re-ignition combine LEMMA), and skips frozen / independent / same-team players (QC's full filter).
    /// </summary>
    private void LaserPlay(Entity self, float dist, float damage, float edgeDamage, float burnTime, Entity? creditOwner = null)
    {
        if (damage <= 0f || Api.Services is null) return;

        // QC W_Fireball_LaserPlay credits this.realowner. For the firemine, the firer is decoupled from .owner
        // (movement passthrough) once the mine goes "hot", so the credit owner is threaded in explicitly to
        // survive the Owner=null decouple (the port's RealOwner aliases Owner). Primary fireball passes null.
        Entity owner = creditOwner ?? self.RealOwner ?? self;
        var burning = StatusEffectsCatalog.Burning;
        if (burning is null) return;

        // QC RandomSelection: weighted reservoir pick (weight 1/(1+d)) with a priority tier preferring
        // targets NOT already burning — same stochastic selection QC's RandomSelection_AddEnt performs.
        var sel = new MapMover.RandomSelection();
        sel.Reset();
        Vector3 chosenPoint = default;
        Vector3 chosenBlast = default;
        // QC W_Fireball_LaserPlay finds with WarpZone_FindRadius(this.origin, dist, true) — the scorch reaches
        // enemies through linked portals, each candidate tagged with the blast point shifted into ITS OWN frame
        // (hit.LocalBlastOrigin) so the body-point / distance / burn-rate math is measured from the portal-shifted
        // origin. With no warpzones in the world FindRadiusWarpzone returns exactly the plain FindInRadius set with
        // identity transforms (LocalBlastOrigin == self.Origin), so non-warpzone maps are byte-for-byte unchanged.
        // (Same warpzone-aware find WeaponSplash.RadiusDamage already uses for the direct blast.)
        var hits = new List<WarpzoneRadiusHit>();
        WarpzoneRadiusQuery.FindRadiusWarpzone(WarpzoneTrace.AmbientManager, self.Origin, dist, hits);
        foreach (WarpzoneRadiusHit hit in hits)
        {
            Entity e = hit.Entity;
            // The blast origin in THIS victim's frame (identity for a same-room target).
            Vector3 blastOrg = hit.LocalBlastOrigin;
            // QC skip: frozen (stat or status), the owner, independent players, non-AIM targets, and a same-team
            // player when the fireball has a (real)owner.
            if (e.TakeDamage != DamageMode.Aim || ReferenceEquals(e, owner) || e.IsIndependentPlayer)
                continue;
            if (e.FrozenStat != 0 || StatusEffectsCatalog.Frozen is { } fz && StatusEffectsCatalog.Has(e, fz))
                continue;
            bool isPlayer = (e.Flags & EntFlags.Client) != 0;
            // QC: (IS_PLAYER(e) && this.realowner && SAME_TEAM(e, this)). The realowner gate uses the threaded
            // credit owner so it survives the firemine's Owner=null decouple (RealOwner aliases Owner).
            bool hasRealOwner = (creditOwner ?? self.RealOwner) is not null;
            if (isPlayer && hasRealOwner && Teams.SameTeam(e, self))
                continue;

            Vector3 p = e.Origin + new Vector3(
                e.Mins.X + Prandom.Float() * (e.Maxs.X - e.Mins.X),
                e.Mins.Y + Prandom.Float() * (e.Maxs.Y - e.Mins.Y),
                e.Mins.Z + Prandom.Float() * (e.Maxs.Z - e.Mins.Z));
            // Distance measured from the portal-shifted blast origin in the victim's frame (QC measures from
            // this.origin, which WarpZone_FindRadius has already mapped into the far-side victim's frame).
            float d = (blastOrg - p).Length();
            if (d >= dist) continue;

            // QC: e.fireball_impactvec = p; RandomSelection_AddEnt(e, 1/(1+d), !Burning). Priority arg is the
            // not-burning flag (1 wins over 0 = prefer not-yet-burning), weight 1/(1+d) breaks the tier ties.
            float priority = StatusEffectsCatalog.Has(e, burning) ? 0f : 1f;
            sel.Add(e, 1f / (1f + d), priority);
            // The reservoir only keeps a reference; stash the body point AND the victim's blast frame so we can
            // recover the chosen one's vec and measure its rate distance in the same (possibly portal-shifted) frame.
            if (ReferenceEquals(sel.Chosen, e))
            {
                chosenPoint = p;
                chosenBlast = blastOrg;
            }
        }

        if (sel.Chosen is { } chosen)
        {
            // Rate distance in the chosen target's own frame (chosenBlast == self.Origin for a same-room target).
            float d = (chosenBlast - chosenPoint).Length();
            // QC: d = damage + (edgedamage-damage)*(d/dist) — the per-second burn rate scaled by distance.
            float rate = damage + (edgeDamage - damage) * (d / dist);
            // Fire_AddDamage(targ, owner, rate*burntime, burntime, projectiledeathtype | HITTYPE_BOUNCE):
            // total = rate*burntime over burntime seconds (combine-aware DOT).
            string dt = DeathTypes.WithHitType(DeathTypes.FromWeapon(NetName), DeathTypes.Bounce);
            StatusEffectsCatalog.FireAddDamage(chosen, owner, rate * burnTime, burnTime, dt);
            // Send_Effect(EFFECT_FIREBALL_LASER, this.origin, chosen.fireball_impactvec - this.origin, 1).
            EffectEmitter.Emit("FIREBALL_LASER", self.Origin, chosenPoint - self.Origin, 1);
        }
    }

    // W_Fireball_Explode — heavy radius damage + a BFG-style secondary blast on every visible enemy nearby,
    // then remove. fireball.qc
    private void Explode(Entity self, Entity? directHit)
    {
        self.Touch = null;
        self.Use = null;
        self.Think = null;
        self.TakeDamage = DamageMode.No;

        Entity owner = self.RealOwner ?? self;

        // QC: float d = health + armor of the realowner BEFORE the blast — the "did the owner survive?" baseline.
        float ownerHealthBefore = owner.GetResource(ResourceType.Health) + owner.GetResource(ResourceType.Armor);

        // 1. dist damage (the direct radius blast). RadiusDamage may kill the owner (self-blast), which is
        // exactly what the post-blast health+armor compare below detects.
        WeaponSplash.RadiusDamage(self, self.Origin, Primary.Damage, Primary.EdgeDamage, Primary.Radius,
            self.Owner, RegistryId, Primary.Force, directHit: directHit);

        // 2. BFG sweep — only if the owner SURVIVED the direct blast (health+armor AFTER >= before) AND this
        // fireball wasn't timed-out / shot-down (!cnt). Each visible enemy in bfgradius takes
        // bfgdamage*(1-sqrt(dist/bfgradius)). Re-read the owner's resources AFTER the blast (QC's compare).
        float ownerHealthAfter = owner.GetResource(ResourceType.Health) + owner.GetResource(ResourceType.Armor);
        if (ownerHealthAfter >= ownerHealthBefore && self.Cnt == 0
            && Primary.BfgRadius > 0f && Api.Services is not null)
        {
            // modeleffect_spawn(models/sphere/sphere.md3, ...) BFG visual — left to the CSQC effect layer.
            Vector3 center = self.Origin;
            foreach (Entity e in Api.Entities.FindInRadius(center, Primary.BfgRadius).ToList())
            {
                // QC filter: not the owner, must be DAMAGE_AIM, not independent, and — for a player with an owner —
                // a DIFFERENT team (no same-team BFG damage).
                if (ReferenceEquals(e, owner) || e.TakeDamage != DamageMode.Aim || e.IsIndependentPlayer)
                    continue;
                bool isPlayer = (e.Flags & EntFlags.Client) != 0;
                if (isPlayer && self.RealOwner is not null && Teams.SameTeam(e, self))
                    continue;

                Vector3 aim = e.Origin + e.ViewOfs; // QC e.origin + e.view_ofs
                // can we see the fireball from the target?
                TraceResult los1 = Api.Trace.Trace(aim, Vector3.Zero, Vector3.Zero, center, MoveFilter.Normal, e);
                if (los1.Fraction != 1f) continue;
                // can we see the player who shot the fireball? (second LOS — the shooter must be visible too.)
                Vector3 shooterEye = owner.Origin + owner.ViewOfs;
                TraceResult los2 = Api.Trace.Trace(aim, Vector3.Zero, Vector3.Zero, shooterEye, MoveFilter.Normal, e);
                if (!ReferenceEquals(los2.Ent, owner) && los2.Fraction != 1f) continue;

                float dist = (center - aim).Length();
                float points = 1f - MathF.Sqrt(dist / Primary.BfgRadius);
                if (points <= 0f) continue;

                float dmg = Primary.BfgDamage * points;
                Vector3 dir = QMath.Normalize(aim - center);
                Vector3 force = dir * (Primary.BfgForce * points);

                // accuracy_add(realowner, WEP_FIREBALL, 0, bfgdamage*points, 0) when it's good damage.
                if (WeaponAccuracyEvents.IsGoodDamage(owner, e))
                    WeaponAccuracyEvents.Hit(owner, this, dmg);

                // Damage(e, this, realowner, dmg, projectiledeathtype | HITTYPE_BOUNCE | HITTYPE_SPLASH, ..., aim, force).
                string dt = DeathTypes.WithHitType(
                    DeathTypes.WithHitType(DeathTypes.FromWeapon(NetName), DeathTypes.Bounce), DeathTypes.Splash);
                Combat.Damage(e, self, owner, dmg, dt, aim, force);

                // Send_Effect(EFFECT_FIREBALL_BFGDAMAGE, e.origin, -dir, 1).
                EffectEmitter.Emit("FIREBALL_BFGDAMAGE", e.Origin, -dir, 1);
            }
        }

        // QC SND_FIREBALL_IMPACT2 at ATTN_NORM*0.25 (wr_impacteffect) — heard from far, like the big BFG ball.
        WeaponSplash.ImpactSound(self, "weapons/fireball_impact2.wav", attenuation: SoundLevels.AttenNorm * 0.25f);
        EffectEmitter.Emit("FIREBALL_EXPLODE", self.Origin);
        Api.Entities.Remove(self);
    }

    // W_Fireball_Attack2 — lob a gravity-affected bouncing firemine that ignites players. fireball.qc
    private void Attack2(Entity actor, WeaponSlot slot)
    {
        QMath.AngleVectors(actor.Angles, out Vector3 forward, out Vector3 vright, out Vector3 up);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, new Vector3(-4, -4, -4), new Vector3(4, 4, 4), recoil: 2f);

        // QC W_Fireball_Attack2: c = bulletcounter % 4 picks one of the four muzzle corners (f_diff = ±1.25 up
        // ±3.75 right), then traceline(w_shotorg, w_shotorg + f_diff.x*v_up + f_diff.y*v_right) nudges the spawn
        // point to that corner (capped at the first obstruction). This spreads consecutive mines' spawn points.
        // bulletcounter was already incremented for this shot by the fire gate (WeaponFireGate.PrepareAttack).
        int c = actor.WeaponState(slot).BulletCounter & 3; // % 4 (BulletCounter is non-negative here)
        (float fUp, float fRight) = c switch
        {
            0 => (-1.25f, -3.75f),
            1 => (1.25f, -3.75f),
            2 => (-1.25f, 3.75f),
            _ => (1.25f, 3.75f),
        };
        Vector3 corner = shot.Origin + fUp * up + fRight * vright;
        Vector3 spawnOrg = shot.Origin;
        if (Api.Services is not null)
        {
            TraceResult nudge = Api.Trace.Trace(shot.Origin, Vector3.Zero, Vector3.Zero, corner, MoveFilter.Normal, actor);
            spawnOrg = nudge.EndPos; // w_shotorg = trace_endpos
        }
        else
        {
            spawnOrg = corner; // headless: no world to trace, just take the offset corner
        }

        Entity proj = Api.Entities.Spawn();
        proj.ClassName = "grenade";
        proj.Owner = actor;
        proj.NetName = NetName;
        proj.MoveType = MoveType.Bounce;
        Projectiles.MakeTrigger(proj); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
        proj.Flags = EntFlags.Item; // QC FL_PROJECTILE
        proj.Gravity = 1f;
        Api.Entities.SetSize(proj, new Vector3(-4, -4, -4), new Vector3(4, 4, 4));
        Api.Entities.SetOrigin(proj, spawnOrg);

        // W_SetupProjVelocity_UP_SEC: velocity = normalize(w_shotdir + up*(speed_up/speed)) * speed.
        proj.Velocity = WeaponFiring.ProjectileVelocity(shot.Dir, up, Secondary.Speed, Secondary.SpeedUp, Secondary.SpeedZ, Secondary.Spread);
        proj.Angles = QMath.VecToAngles(proj.Velocity);

        // QC fireball.qc:300-301 — flag the firemine as a dodgeable hazard (rating = secondary damage).
        proj.BotDodge = true;
        proj.BotDodgeRating = Secondary.Damage;

        float deathTime = Api.Clock.Time + Secondary.Lifetime;
        // QC sets proj.owner = proj.realowner = actor. The "make it hot" decouple later nulls only .owner; the
        // firer (realowner) is captured here so burn/obituary credit survives the Owner=null decouple — the port's
        // RealOwner is a computed alias of Owner, so it can't be relied on once Owner is cleared.
        Entity firer = actor;
        proj.Touch = (self, other) => FiremineTouch(self, other, firer);
        proj.Think = self => FiremineThink(self, deathTime, firer);
        proj.NextThink = Api.Clock.Time;

        // MUTATOR_CALLHOOK(EditProjectile, actor, proj) (fireball.qc W_Fireball_Attack2).
        var ep = new MutatorHooks.EditProjectileArgs(actor, proj);
        MutatorHooks.EditProjectile.Call(ref ep);

        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/fireball_fire.wav");
        // QC W_MuzzleFlash fires from the nudged w_shotorg (the f_diff muzzle corner).
        EffectEmitter.Emit("FIREBALL_MUZZLEFLASH", spawnOrg, shot.Dir * 1000f, 1, except: actor);
    }

    // W_Fireball_Firemine_Think — scorch a nearby enemy each tick; self-destruct at end of lifetime. fireball.qc
    private void FiremineThink(Entity self, float deathTime, Entity firer)
    {
        if (Api.Clock.Time > deathTime)
        {
            Api.Entities.Remove(self);
            return;
        }

        // "make it hot once it leaves its owner": after the mine has spent 3 ticks beyond laserradius of its
        // firer, drop the owner link so the mine can then scorch its own firer too (cnt counts ticks-away; it
        // resets while the mine is still near the owner). QC uses .owner (the movement-passthrough owner).
        if (self.Owner is { } mineOwner)
        {
            Vector3 ownerEye = mineOwner.Origin + mineOwner.ViewOfs;
            if ((self.Origin - ownerEye).Length() > Secondary.LaserRadius)
            {
                if (++self.Cnt == 3)
                    self.Owner = null;
            }
            else
            {
                self.Cnt = 0;
            }
        }

        // Credit the firer (realowner), which persists past the Owner=null decouple above.
        LaserPlay(self, Secondary.LaserRadius, Secondary.LaserDamage, Secondary.LaserEdgeDamage, Secondary.LaserBurnTime, firer);
        self.NextThink = Api.Clock.Time + 0.1f;
    }

    // W_Fireball_Firemine_Touch — set the player it lands on alight (Fire_AddDamage burn over damagetime),
    // else keep bouncing (HITTYPE_BOUNCE). fireball.qc
    private void FiremineTouch(Entity self, Entity other, Entity firer)
    {
        // QC: if (toucher.takedamage == DAMAGE_AIM && Fire_AddDamage(...) >= 0) { delete; return; }
        // Fire_AddDamage returns >= 0 only when it actually added burn (target alive + damage > 0); on an
        // already-dead target (or a re-ignite that adds nothing) it returns -1 and the mine keeps bouncing.
        if (other.TakeDamage == DamageMode.Aim)
        {
            // projectiledeathtype = WEP_FIREBALL.m_id | HITTYPE_SECONDARY (set at spawn).
            string dt = DeathTypes.WithHitType(DeathTypes.FromWeapon(NetName), DeathTypes.Secondary);
            // QC credits this.realowner; threaded firer survives the Owner=null decouple (RealOwner aliases Owner).
            float added = StatusEffectsCatalog.FireAddDamage(other, firer,
                Secondary.Damage, Secondary.DamageTime, dt);
            if (added >= 0f)
            {
                // QC wr_impacteffect secondary branch: "firemine goes out silently" — no impact sound/effect.
                Api.Entities.Remove(self);
                return;
            }
        }
        // didn't ignite: keep bouncing (engine MOVETYPE_BOUNCE reflects the velocity; the deathtype now carries
        // HITTYPE_BOUNCE in QC — a render/obituary nicety not modeled on the headless bounce).
        self.Angles = QMath.VecToAngles(self.Velocity);
    }

    // METHOD(Fireball, wr_checkammo1/2) — fireball.qc (infinite ammo).
    public bool CheckAmmoPrimary(Entity actor) => true;
    public bool CheckAmmoSecondary(Entity actor) => true;
}
