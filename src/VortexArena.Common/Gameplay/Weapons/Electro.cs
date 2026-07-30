using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Electro — port of common/weapons/weapon/electro.{qh,qc}. A splash/combo weapon. Primary fires a
/// fast straight bolt (MOVETYPE_FLY) that bursts on impact; secondary lobs gravity-affected bouncing orbs
/// (MOVETYPE_BOUNCE) that detonate on a timer or contact. The signature mechanic is the "combo": when a
/// blast happens near a live orb the orb is triggered too, chaining a bigger explosion.
///
/// Identity/attributes from electro.qh; balance from bal-wep-xonotic.cfg (g_balance_electro_*).
/// This port covers the bolt and orb projectiles, their contact/lifetime detonation, the multi-orb
/// secondary burst (secondary_count), the in-flight midair-combo, the staggered combo chain (combo_speed
/// delay + recursive re-trigger), orb bounce factor/stop, shoot-down-into-combo, and the splash damage.
/// Sticky orbs (secondary_stick / MOVETYPE_FOLLOW), the orb limit (secondary_limit), and the combo
/// explode-over-time field (combo_duration) are ported but off at the stock xonotic balance; only orb
/// networking is omitted.
/// </summary>
[Weapon]
public sealed class Electro : Weapon
{
    /// <summary>Primary-fire (bolt) balance — QC WEP_CVAR_PRI(WEP_ELECTRO, *).</summary>
    public struct PrimaryBalance
    {
        public float Ammo;        // g_balance_electro_primary_ammo (cells)
        public float Animtime;    // g_balance_electro_primary_animtime
        public float ComboRadius; // g_balance_electro_primary_comboradius (orb-trigger radius on bolt blast)
        public float Damage;      // g_balance_electro_primary_damage
        public float EdgeDamage;  // g_balance_electro_primary_edgedamage
        public float Force;       // g_balance_electro_primary_force
        public float Lifetime;    // g_balance_electro_primary_lifetime
        public float Radius;      // g_balance_electro_primary_radius
        public float Refire;      // g_balance_electro_primary_refire
        public float Speed;       // g_balance_electro_primary_speed
        public float MidairComboRadius; // g_balance_electro_primary_midaircombo_radius (orb trigger while in flight)
        public bool  MidairComboExplode;// g_balance_electro_primary_midaircombo_explode
        public float MidairComboInterval;// g_balance_electro_primary_midaircombo_interval (re-probe period)
        public float MidairComboSpeed;  // g_balance_electro_primary_midaircombo_speed (first-orb chain delay = dist/speed)
        public bool  MidairComboOwn;    // g_balance_electro_primary_midaircombo_own (trigger the firer's own orbs)
        public bool  MidairComboTeammate;// g_balance_electro_primary_midaircombo_teammate (trigger teammates' orbs)
        public bool  MidairComboEnemy;  // g_balance_electro_primary_midaircombo_enemy (trigger enemies' orbs)
    }

    /// <summary>Secondary-fire (orb) balance — QC WEP_CVAR_SEC(WEP_ELECTRO, *).</summary>
    public struct SecondaryBalance
    {
        public float Ammo;             // g_balance_electro_secondary_ammo
        public float Animtime;         // g_balance_electro_secondary_animtime
        public float BounceFactor;     // g_balance_electro_secondary_bouncefactor
        public float BounceStop;       // g_balance_electro_secondary_bouncestop
        public int   Count;            // g_balance_electro_secondary_count (orbs per burst)
        public float Damage;           // g_balance_electro_secondary_damage
        public float DamageForceScale; // g_balance_electro_secondary_damageforcescale
        public float EdgeDamage;       // g_balance_electro_secondary_edgedamage
        public float Force;            // g_balance_electro_secondary_force
        public float Health;           // g_balance_electro_secondary_health (shootable orb hp)
        public float Lifetime;         // g_balance_electro_secondary_lifetime
        public float Radius;           // g_balance_electro_secondary_radius
        public float Refire;           // g_balance_electro_secondary_refire (delay between secondary BURSTS)
        public float Refire2;          // g_balance_electro_secondary_refire2 (primary lockout after an orb)
        public float Speed;            // g_balance_electro_secondary_speed
        public float SpeedUp;          // g_balance_electro_secondary_speed_up
        public bool  TouchExplode;     // g_balance_electro_secondary_touchexplode
        public bool  Stick;            // g_balance_electro_secondary_stick (orb glues to the surface it lands on)
        public float StickLifetime;    // g_balance_electro_secondary_stick_lifetime (-1 = keep the orb's death_time; 0 = explode on stick)
        public int   Limit;            // g_balance_electro_secondary_limit (max live orbs per firer; 0 = unlimited)
        public bool  DamagedByContents;// g_balance_electro_secondary_damagedbycontents (orb dies in lava/slime)
    }

    /// <summary>Combo balance — QC WEP_CVAR(WEP_ELECTRO, combo_*).</summary>
    public struct ComboBalance
    {
        public float ComboRadius; // g_balance_electro_combo_comboradius (chain reach from a triggered orb)
        public float Damage;      // g_balance_electro_combo_damage
        public float EdgeDamage;  // g_balance_electro_combo_edgedamage
        public float Force;       // g_balance_electro_combo_force
        public float Radius;      // g_balance_electro_combo_radius (blast radius of a combo)
        public float Duration;    // g_balance_electro_combo_duration (explode-over-time field length; 0 = instant blast)
    }

    public PrimaryBalance Primary;
    public SecondaryBalance Secondary;
    public ComboBalance Combo;


    public Electro()
    {
        NetName = "electro";
        AmmoType = ResourceType.Cells;   // QC ammo_type
        DisplayName = "Electro";
        Impulse = 5;
        // WEP_FLAG_NORMAL | WEP_FLAG_RELOADABLE | WEP_TYPE_SPLASH
        SpawnFlags = WeaponFlags.Normal | WeaponFlags.Reloadable | WeaponFlags.TypeSplash;
        Color = new Vector3(0.408f, 0.600f, 0.949f);
        ViewModel = "h_electro.iqm";  // MDL_ELECTRO_VIEW
        WorldModel = "v_electro.md3"; // MDL_ELECTRO_WORLD
        ItemModel = "g_electro.md3";  // MDL_ELECTRO_ITEM
    }

    /// <summary>
    /// METHOD(Electro, describe) — common/weapons/weapon/electro.qc:753-767. The in-menu weapon-guide prose:
    /// what primary/secondary/combo do, the ammo note, and the tactical tip. The QC builds this with PAR()
    /// paragraphs and COLORED_NAME(%s) substitutions (the weapon name, the CTF gametype, the Cells item); the
    /// names are pre-filled here with their literals. The trailing W_Guide_Keybinds line and the computed
    /// W_Guide_DPS_secondaryMultishotWithCombo metric line are not reproduced (those helpers aren't ported).
    /// </summary>
    public override string? GuideDescription =>
        "The Electro shoots electric balls forwards, dealing some splash damage when they burst on impact.\n\n"
      + "The secondary fire launches orbs that are influenced by gravity, so they can be laid around the map "
      + "at high traffic locations (like at Capture the Flag flag bases) to damage enemies that walk by. The "
      + "orbs burst after some time, and can be forced to burst in a \"combo\" if a primary fire ball bursts "
      + "near them.\n\n"
      + "It consumes some Cells ammo for each ball / orb.\n\n"
      + "The Electro is one of the best spam weapons to use in crowded areas, since combos can deal tons of "
      + "damage, if the enemy is close enough. Since the primary fire doesn't travel particularly fast, the "
      + "Electro is not useful in many other situations.";

    public override void Configure()
    {
        Primary.Ammo = Bal("g_balance_electro_primary_ammo", 4f);
        Primary.Animtime = Bal("g_balance_electro_primary_animtime", 0.3f);
        Primary.ComboRadius = Bal("g_balance_electro_primary_comboradius", 300f);
        Primary.Damage = Bal("g_balance_electro_primary_damage", 40f);
        Primary.EdgeDamage = Bal("g_balance_electro_primary_edgedamage", 20f);
        Primary.Force = Bal("g_balance_electro_primary_force", 200f);
        Primary.Lifetime = Bal("g_balance_electro_primary_lifetime", 5f);
        Primary.Radius = Bal("g_balance_electro_primary_radius", 100f);
        Primary.Refire = Bal("g_balance_electro_primary_refire", 0.6f);
        Primary.Speed = Bal("g_balance_electro_primary_speed", 2500f);
        Primary.MidairComboRadius = Bal("g_balance_electro_primary_midaircombo_radius", 0f);   // off by default in xonotic balance (on in samual/xdf)
        Primary.MidairComboExplode = BalBool("g_balance_electro_primary_midaircombo_explode", true);
        Primary.MidairComboInterval = Bal("g_balance_electro_primary_midaircombo_interval", 0.1f);
        Primary.MidairComboSpeed = Bal("g_balance_electro_primary_midaircombo_speed", 2000f);
        Primary.MidairComboOwn = BalBool("g_balance_electro_primary_midaircombo_own", true);
        Primary.MidairComboTeammate = BalBool("g_balance_electro_primary_midaircombo_teammate", true);
        Primary.MidairComboEnemy = BalBool("g_balance_electro_primary_midaircombo_enemy", true);

        Secondary.Ammo = Bal("g_balance_electro_secondary_ammo", 2f);
        Secondary.Animtime = Bal("g_balance_electro_secondary_animtime", 0.2f);
        Secondary.BounceFactor = Bal("g_balance_electro_secondary_bouncefactor", 0.3f);
        Secondary.BounceStop = Bal("g_balance_electro_secondary_bouncestop", 0.05f);
        Secondary.Count = BalInt("g_balance_electro_secondary_count", 3);
        Secondary.Damage = Bal("g_balance_electro_secondary_damage", 30f);
        Secondary.DamageForceScale = Bal("g_balance_electro_secondary_damageforcescale", 4f);
        Secondary.EdgeDamage = Bal("g_balance_electro_secondary_edgedamage", 15f);
        Secondary.Force = Bal("g_balance_electro_secondary_force", 50f);
        Secondary.Health = Bal("g_balance_electro_secondary_health", 5f);
        Secondary.Lifetime = Bal("g_balance_electro_secondary_lifetime", 4f);
        Secondary.Radius = Bal("g_balance_electro_secondary_radius", 150f);
        Secondary.Refire = Bal("g_balance_electro_secondary_refire", 1.2f);
        Secondary.Refire2 = Bal("g_balance_electro_secondary_refire2", 0.2f);
        Secondary.Speed = Bal("g_balance_electro_secondary_speed", 1000f);
        Secondary.SpeedUp = Bal("g_balance_electro_secondary_speed_up", 200f);
        Secondary.TouchExplode = BalBool("g_balance_electro_secondary_touchexplode", true);
        Secondary.Stick = BalBool("g_balance_electro_secondary_stick", false);            // OFF by default in xonotic balance
        Secondary.StickLifetime = Bal("g_balance_electro_secondary_stick_lifetime", -1f);  // -1 = inherit the orb's death_time
        Secondary.Limit = BalInt("g_balance_electro_secondary_limit", 0);                  // 0 = unlimited
        Secondary.DamagedByContents = BalBool("g_balance_electro_secondary_damagedbycontents", true); // ON by default

        Combo.ComboRadius = Bal("g_balance_electro_combo_comboradius", 300f);
        Combo.Damage = Bal("g_balance_electro_combo_damage", 50f);
        Combo.EdgeDamage = Bal("g_balance_electro_combo_edgedamage", 25f);
        Combo.Force = Bal("g_balance_electro_combo_force", 120f);
        Combo.Radius = Bal("g_balance_electro_combo_radius", 150f);
        Combo.Duration = Bal("g_balance_electro_combo_duration", 0f); // explode-over-time field length (OFF by default in xonotic balance; 1.5 in mario/testing)
        // QC WEP_CVAR(WEP_ELECTRO, combo_speed) — chain ripple delay = distance/speed. Was permanently 1000
        // (the field default) because Configure never seeded it; Base default is 2000.
        ComboSpeed = Bal("g_balance_electro_combo_speed", 2000f);
        ComboComboRadiusThruwall = Bal("g_balance_electro_combo_comboradius_thruwall", 200f);
        CombocSafeAmmoCheck = BalBool("g_balance_electro_combo_safeammocheck", true);
    }

    // METHOD(Electro, wr_think) — common/weapons/weapon/electro.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);
        float rate = WeaponRateFactor(actor);

        if (fire == FireMode.Primary)
        {
            // QC: if (time >= electro_secondarytime + refire2*rate && weapon_prepareattack(..., refire)) {...}
            // The refire2 interlock keeps the primary briefly locked out after lobbing an orb so the two modes
            // don't overlap; primary itself uses the shared ATTACK_FINISHED (positive attacktime).
            if (Api.Clock.Time >= st.ElectroSecondaryTime + Secondary.Refire2 * rate
                && PrepareAttack(actor, slot, fire, attackTime: Primary.Refire))
                AttackBolt(actor, slot);
        }
        else if (fire == FireMode.Secondary)
        {
            // QC: if (time >= electro_secondarytime + refire*rate && weapon_prepareattack(..., true, -1)) fire
            // the FIRST orb, set electro_count = count, electro_secondarytime = time, and schedule
            // W_Electro_CheckAttack after animtime to stream the rest one-per-tick while ATCK2 stays held.
            // attacktime = -1: the secondary runs on its private electro_secondarytime, not ATTACK_FINISHED.
            if (Api.Clock.Time >= st.ElectroSecondaryTime + Secondary.Refire * rate
                && PrepareAttack(actor, slot, fire, attackTime: -1f))
            {
                AttackOrb(actor, slot);
                st.ElectroCount = (int)MathF.Max(1f, Secondary.Count);
                st.ElectroSecondaryTime = Api.Clock.Time;
                ScheduleCheckAttack(actor, slot);
            }
        }
    }

    // W_Electro_CheckAttack — fires one more orb per animtime tick while electro_count > 1 and ATCK2 is held,
    // decrementing electro_count and rescheduling itself; otherwise the slot becomes READY. electro.qc
    private void CheckAttack(Entity actor, WeaponSlot slot)
    {
        var st = actor.WeaponState(slot);
        if (st.ElectroCount > 1 && st.ButtonAttack2
            && PrepareAttack(actor, slot, FireMode.Secondary, attackTime: -1f))
        {
            AttackOrb(actor, slot);
            --st.ElectroCount;
            st.ElectroSecondaryTime = Api.Clock.Time;
            ScheduleCheckAttack(actor, slot);
            return;
        }
        // QC w_ready(...): the streaming burst is done; return the slot to READY.
        if (st.State == WeaponFireState.InUse)
            st.State = WeaponFireState.Ready;
    }

    // weapon_thinkf(actor, weaponentity, WFRAME_FIRE2, secondary animtime, W_Electro_CheckAttack)
    private void ScheduleCheckAttack(Entity actor, WeaponSlot slot)
    {
        var st = actor.WeaponState(slot);
        float animtime = MathF.Max(0f, Secondary.Animtime) * WeaponRateFactor(actor);
        WeaponFireDriver.ScheduleThink(st, animtime, (pl, sl) => CheckAttack(pl, sl));
    }

    // Refire/animtime from the (cvar-seeded) per-mode balance blocks.
    public override float RefireFor(FireMode fire) => fire == FireMode.Secondary ? Secondary.Refire : Primary.Refire;
    public override float AnimtimeFor(FireMode fire) => fire == FireMode.Secondary ? Secondary.Animtime : Primary.Animtime;

    // METHOD(Electro, wr_aim) — common/weapons/weapon/electro.qc:599-626. The "electromooth" bot toggle: bots
    // mostly fire the fast straight primary bolt, but occasionally flip to a mortar-style secondary orb lob.
    //   * beyond 1000 units -> force primary (the lobbed orb is useless at range): clear the toggle (electro.qc:602-603).
    //   * while preferring primary  -> 1% chance per decision to flip to secondary (random() < 0.01, electro.qc:613).
    //   * while preferring secondary -> 3% chance per decision to flip back to primary (random() < 0.03, electro.qc:622).
    // QC presses the button for the CURRENT toggle state, then rolls the flip for the NEXT decision; returning true
    // routes the bot's already-decided shot onto the secondary button (BotBrain dispatch). The shot lead itself is
    // handled by the generic BotAim — QC's primary bot_aim leads by primary_speed (or near-hitscan when speed==0),
    // the secondary by secondary_speed/speed_up; in this port the lead rides the weapon's primary speed cvar via the
    // brain (a shared limitation with the Rifle's secondary; the mode pick + range gate are the faithful part here).
    public override bool BotWantsSecondary(float enemyDistance, float skill, ref BotAimState ctx)
    {
        if (enemyDistance > 1000f)
            ctx.SecondaryToggle = false; // vdist(... > 1000) -> bot_secondary_electromooth = false

        bool fireSecondary = ctx.SecondaryToggle; // the button QC presses THIS frame (pre-flip)
        if (!ctx.SecondaryToggle)
        {
            if (ctx.Random01 < 0.01f) // random() < 0.01 -> start preferring secondary next time
                ctx.SecondaryToggle = true;
        }
        else
        {
            if (ctx.Random01 < 0.03f) // random() < 0.03 -> go back to preferring primary next time
                ctx.SecondaryToggle = false;
        }
        return fireSecondary;
    }

    // QC wr_aim lead speed (electro.qc:599-626). The bot leads its shot by the projectile speed of the mode it is
    // about to fire: the straight primary bolt at primary_speed (bot_aim(..., speed, 0, lifetime)), the lobbed
    // secondary "electromooth" orb at secondary_speed (bot_aim(..., speed, speed_up, lifetime)). The brain calls
    // BotAimShotSpeed(default, ref ctx) in CurrentShotSpeed BEFORE BotWantsSecondary rolls the flip, so
    // ctx.SecondaryToggle here is the PRE-flip toggle — exactly the mode this frame's shot uses (same value
    // BotWantsSecondary reads to route the button). The horizontal speed is all bot_shotlead consumes; the orb's
    // speed_up=200 vertical component is the arc-only term (handled by the brain's lob arc), not the lead distance.
    public override float BotAimShotSpeed(float defaultSpeed) => Primary.Speed; // fallback: primary lead

    public override float BotAimShotSpeed(float defaultSpeed, ref BotAimState ctx)
        => ctx.SecondaryToggle ? Secondary.Speed : Primary.Speed;

    // W_Electro_Attack_Bolt — fast straight bolt that bursts on impact. electro.qc
    private void AttackBolt(Entity actor, WeaponSlot slot)
    {
        actor.TakeResource(AmmoType, Primary.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out _);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, new Vector3(0, 0, -3), new Vector3(0, 0, -3), recoil: 2f);

        Entity proj = Api.Entities.Spawn();
        proj.ClassName = "electro_bolt";
        proj.Owner = actor;
        proj.NetName = NetName;
        proj.MoveType = MoveType.Fly;
        Projectiles.MakeTrigger(proj); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
        proj.Flags = EntFlags.Item; // QC FL_PROJECTILE
        Api.Entities.SetSize(proj, new Vector3(0, 0, -3), new Vector3(0, 0, -3));
        Api.Entities.SetOrigin(proj, shot.Origin);

        // W_SetupProjVelocity_PRI: velocity = w_shotdir * speed.
        proj.Velocity = WeaponFiring.ProjectileVelocity(shot.Dir, Vector3.UnitZ, Primary.Speed);
        proj.Angles = QMath.VecToAngles(proj.Velocity);

        // QC electro.qc:336-337 — flag the bolt as a dodgeable hazard (rating = primary damage).
        proj.BotDodge = true;
        proj.BotDodgeRating = Primary.Damage;

        float deathTime = Api.Clock.Time + Primary.Lifetime;
        proj.Touch = (self, other) => ExplodeBolt(self, other);
        proj.Think = self => BoltThink(self, deathTime); // W_Electro_Bolt_Think: midair combo + lifetime
        proj.NextThink = Api.Clock.Time;

        // MUTATOR_CALLHOOK(EditProjectile, actor, proj) (electro.qc).
        var ep = new MutatorHooks.EditProjectileArgs(actor, proj);
        MutatorHooks.EditProjectile.Call(ref ep);

        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/electro_fire.wav");
        EffectEmitter.Emit("ELECTRO_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);
    }

    // W_Electro_Bolt_Think — while in flight, trigger nearby orbs into a combo (midaircombo); explode at end
    // of lifetime. electro.qc
    private void BoltThink(Entity self, float deathTime)
    {
        if (Api.Clock.Time >= deathTime)
        {
            ExplodeBolt(self, null);
            return;
        }

        // midair combo: convert nearby orbs into a chained combo while the bolt is still in flight (default OFF
        // in bal-wep-xonotic.cfg — midaircombo_radius 0 — but ON in samual/xdf, so the branch IS live there).
        // QC W_Electro_Bolt_Think:261-309 has its own loop (NOT W_Electro_TriggerCombo): per-orb own/teammate/
        // enemy gating + the first orb chains on midaircombo_speed (no combo_comboradius_thruwall LOS test).
        if (Primary.MidairComboRadius > 0f)
        {
            bool found = MidairTriggerCombo(self.Origin, Primary.MidairComboRadius, self.Owner);
            // if we triggered an orb, should we explode? if not, try again next probe (electro.qc:305-308).
            if (found && Primary.MidairComboExplode)
            {
                ExplodeBolt(self, null);
                return;
            }
            self.NextThink = MathF.Min(Api.Clock.Time + Primary.MidairComboInterval, deathTime);
            return;
        }
        // QC default branch (midaircombo off): nextthink = ltime — no per-tick think.
        self.NextThink = deathTime;
    }

    /// <summary>
    /// Port of the midair-combo loop in W_Electro_Bolt_Think (electro.qc:261-309). Distinct from
    /// W_Electro_TriggerCombo: it applies per-orb own/teammate/enemy gating (midaircombo_own/_teammate/_enemy),
    /// chains the orb on midaircombo_speed (not combo_speed), and skips the combo_comboradius_thruwall LOS test.
    /// Returns true if at least one orb was converted into a chained combo.
    /// </summary>
    private bool MidairTriggerCombo(Vector3 org, float rad, Entity? own)
    {
        if (Api.Services is null) return false;
        bool found = false;
        // Snapshot first: we mutate orbs (rename to electro_orb_chain + schedule) as we go.
        var orbs = Api.Entities.FindInRadius(org, rad)
            .Where(e => e.ClassName == "electro_orb" && !e.IsFreed).ToList();
        foreach (Entity e in orbs)
        {
            // QC: skip an orb owned by an independent player who isn't the firer (no independent players in the
            // default modes — dead-OFF-equivalent, but ported for faithfulness).
            if (e.Owner is { IsIndependentPlayer: true } && !ReferenceEquals(own, e.Owner))
                continue;

            // QC electro.qc:276-281 — per-orb team gate. this.owner==e.owner -> _own; SAME_TEAM -> _teammate; else _enemy.
            bool explode;
            if (ReferenceEquals(own, e.Owner))
                explode = Primary.MidairComboOwn;
            else if (own is not null && e.Owner is not null && Teams.SameTeam(own, e.Owner))
                explode = Primary.MidairComboTeammate;
            else
                explode = Primary.MidairComboEnemy;
            if (!explode) continue;

            // change owner to whoever caused the combo, lock it down, rename so a re-probe skips it.
            e.Owner = own;
            e.TakeDamage = DamageMode.No;
            e.ClassName = "electro_orb_chain";
            e.Touch = null;
            e.ProjectileDamage = null;

            // Only the first orb uses midaircombo_speed (delay = dist/speed, 0 = instant); the recursive ripple
            // (ExplodeCombo -> TriggerCombo) then uses the normal combo_speed for the rest (electro.qc:290-298).
            float delay = (Primary.MidairComboSpeed > 0f) ? (e.Origin - org).Length() / Primary.MidairComboSpeed : 0f;
            e.Think = self => ExplodeCombo(self);
            e.NextThink = Api.Clock.Time + delay;
            if (delay <= 0f) ExplodeCombo(e);

            found = true;
        }
        return found;
    }

    // W_Electro_Explode (bolt path) — bolt blast + trigger nearby orbs into a combo. electro.qc
    private void ExplodeBolt(Entity self, Entity? directHit)
    {
        // W_Electro_Explode (electro.qc:197-200): a direct bolt hit on a flying enemy earns the ELECTROBITCH
        // airshot announcement for the firer. Test BEFORE the projectile is removed.
        if (directHit is not null && self.Owner is { } owner
            && directHit.TakeDamage == DamageMode.Aim && (directHit.Flags & EntFlags.Client) != 0
            && !ReferenceEquals(directHit, owner) && !Teams.SameTeam(directHit, owner) // QC DIFF_TEAM
            && directHit.DeadState == DeadFlag.No && IsFlying(directHit))
            NotificationSystem.Announce(owner, "ACHIEVEMENT_ELECTROBITCH");

        self.Touch = null;
        self.Think = null;
        self.TakeDamage = DamageMode.No;

        // Trigger orbs within comboradius FIRST (so they chain off this blast).
        TriggerCombo(self.Origin, Primary.ComboRadius, self.Owner);

        // deathTag = plain weapon tag so the obituary reads MURDER_BOLT; directHit lets the struck target skip
        // the LOS reduction (QC passes directhitentity through to RadiusDamage).
        WeaponSplash.RadiusDamage(self, self.Origin, Primary.Damage, Primary.EdgeDamage, Primary.Radius,
            self.Owner, RegistryId, Primary.Force, directHit: directHit, accuracyWeapon: this, deathTag: DeathBolt);

        // wr_impacteffect (electro.qc:741): the plain PRIMARY impact is EFFECT_ELECTRO_IMPACT — the expanding
        // blue shockwave ring. ELECTRO_BALLEXPLODE belongs to the secondary orb (HITTYPE_SECONDARY, :726).
        EffectEmitter.Emit("ELECTRO_IMPACT", self.Origin);
        WeaponSplash.ImpactSound(self, "weapons/electro_impact.wav"); // QC SND_ELECTRO_IMPACT (wr_impacteffect)
        Api.Entities.Remove(self);
    }

    // W_Electro_Attack_Orb — gravity-affected bouncing orb that detonates on timer/contact. electro.qc
    private void AttackOrb(Entity actor, WeaponSlot slot)
    {
        // QC W_Electro_Attack_Orb:569-575 — secondary_limit (default 0 = unlimited): track live orbs per firer in
        // LimitedElectroBallRubbleList and, once the firer's orb count would exceed the cap, detonate the OLDEST orb
        // (adaptor_think2use_hittype_splash) before laying the new one. The port has no rubble linked-list, so scan
        // electro_orb by owner and force the lowest-NextThink (oldest spawned) to explode. Inert while limit == 0.
        if (Secondary.Limit > 0)
            EnforceOrbLimit(actor);

        actor.TakeResource(AmmoType, Secondary.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out _, out Vector3 up);
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, new Vector3(-4, -4, -4), new Vector3(4, 4, 4), recoil: 2f);
        // w_shotdir = v_forward — no TrueAim for orbs.
        Vector3 dir = forward;

        Entity orb = Api.Entities.Spawn();
        orb.ClassName = "electro_orb";
        orb.Owner = actor;
        orb.NetName = NetName;
        orb.MoveType = MoveType.Bounce;
        Projectiles.MakeTrigger(orb); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
        orb.Flags = EntFlags.Item; // QC FL_PROJECTILE
        orb.Gravity = 1f;
        Api.Entities.SetSize(orb, new Vector3(-4, -4, -4), new Vector3(4, 4, 4));
        Api.Entities.SetOrigin(orb, shot.Origin);

        orb.TakeDamage = DamageMode.Yes; // shootable
        orb.Health = Secondary.Health;
        orb.DamageForceScale = Secondary.DamageForceScale;
        orb.BounceFactor = Secondary.BounceFactor; // engine MOVETYPE_BOUNCE uses these
        orb.BounceStop = Secondary.BounceStop;
        // QC W_Electro_Attack_Orb:561-563 — proj.damagedbycontents = (cvar); if set, IL_PUSH(g_damagedbycontents).
        // The port's GameWorld.CreatureFrameAll sweep enrolls any DamagedByContents entity, so an orb resting in
        // lava/slime takes content damage and detonates (default ON). Content damage routes a NULL inflictor
        // through the orb's GtEventDamage shoot-down shim -> ProjectileDamage at hp<=0 (see note below).
        orb.DamagedByContents = Secondary.DamagedByContents;

        // W_SetupProjVelocity_UP_SEC: velocity = normalize(dir + up*(speed_up/speed)) * speed.
        orb.Velocity = WeaponFiring.ProjectileVelocity(dir, up, Secondary.Speed, Secondary.SpeedUp);
        orb.Angles = QMath.VecToAngles(orb.Velocity);

        // QC electro.qc:539-540 — flag the secondary orb as a dodgeable hazard (rating = secondary damage).
        orb.BotDodge = true;
        orb.BotDodgeRating = Secondary.Damage;

        // A bounced orb's kill line reads MURDER_COMBO (QC W_Electro_Orb_Touch:466 ORs HITTYPE_BOUNCE onto the
        // orb's projectiledeathtype). The port Entity has no projectiledeathtype field, so we track the bounce
        // on a captured flag the touch/think closures share, and fold it into the orb's deathTag at explode.
        bool[] bounced = { false };

        orb.Touch = (self, other) => OrbTouch(self, other, bounced);
        orb.Think = self => ExplodeOrb(self, bounced); // adaptor_think2use_hittype_splash at lifetime
        orb.NextThink = Api.Clock.Time + Secondary.Lifetime;
        // W_Electro_Orb_Damage (electro.qc:478-512): a shot-down orb that drops to <=0 HP detonates. QC branches on
        // is_combo = (inflictor.classname == "electro_orb_chain" || "electro_bolt"): an electro blast converts it to
        // a COMBO (crediting whoever shot it); ANY OTHER inflictor — another weapon, or a NULL inflictor from a
        // lava/slime contents death (CreatureFrame_hotliquids) — bursts it as a plain SECONDARY blast instead.
        // Projectiles.MakeShootable installs the QC W_CheckProjectileDamage gate + HP-subtract shim (exception 1 so a
        // combo passes the stock g_projectiles_damage -2; contents deaths pass via the contents branch of the gate),
        // sets self.DmgInflictor, then fires this ProjectileDamage at hp<=0.
        orb.ProjectileDamage = (self, attacker) =>
        {
            string? infl = self.DmgInflictor?.ClassName;
            bool isCombo = infl == "electro_orb_chain" || infl == "electro_bolt";
            if (isCombo)
            {
                self.Owner = attacker ?? self.Owner;
                self.Touch = null; self.Think = null; self.TakeDamage = DamageMode.No;
                WeaponSplash.RadiusDamage(self, self.Origin, Combo.Damage, Combo.EdgeDamage, Combo.Radius,
                    self.Owner, RegistryId, Combo.Force, accuracyWeapon: this, deathTag: DeathCombo);
                EffectEmitter.Emit("ELECTRO_COMBO", self.Origin);
                WeaponSplash.ImpactSound(self, "weapons/electro_impact_combo.wav"); // QC SND_ELECTRO_IMPACT_COMBO
                Api.Entities.Remove(self);
                return;
            }
            // Non-combo death (other weapon / lava / slime): QC sets use=W_Electro_Explode_use + adaptor_think2use,
            // i.e. the orb's normal SECONDARY explode. Reuse the same ExplodeOrb path the lifetime/touch use.
            ExplodeOrb(self, bounced);
        };
        // Wire the shoot-down-into-combo path live (Wave-1 seam): exception 1 = combo-able regardless of the
        // stock g_projectiles_damage -2, matching QC W_Electro_Orb_Damage's is_combo branch.
        Projectiles.MakeShootable(orb, exception: 1f);

        // MUTATOR_CALLHOOK(EditProjectile, actor, proj) (electro.qc). invincibleproj zeroes the orb's health
        // here, so a shot-down combo can't be triggered — matching QC.
        var ep = new MutatorHooks.EditProjectileArgs(actor, orb);
        MutatorHooks.EditProjectile.Call(ref ep);

        Api.Sound.Play(actor, SoundChannel.Weapon, "weapons/electro_fire2.wav");
        EffectEmitter.Emit("ELECTRO_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);
    }

    // W_Electro_Orb_Touch — burst on a player (touchexplode), otherwise bounce. electro.qc
    private void OrbTouch(Entity self, Entity other, bool[] bounced)
    {
        bool hitPlayer = other.TakeDamage == DamageMode.Aim || (other.Flags & EntFlags.Client) != 0;
        if (hitPlayer && Secondary.TouchExplode)
        {
            ExplodeOrb(self, bounced);
            return;
        }
        // QC W_Electro_Orb_Touch:462 — don't bounce-sound off / stick to the firer's OWN other projectiles.
        if (ReferenceEquals(other.Owner, self.Owner) && other.ClassName == self.ClassName)
            return;
        // QC W_Electro_Orb_Touch:464-475 sticky branch (g_balance_electro_secondary_stick, default 0 = OFF). On a
        // world/body contact, if stick is enabled either explode immediately (stick_lifetime 0) or glue the orb to
        // the surface it landed on (W_Electro_Orb_Stick). Inert while stick == false (the default), so the orb just
        // bounces as before.
        if (Secondary.Stick)
        {
            bounced[0] = true; // a stuck orb still reads MURDER_COMBO (HITTYPE_BOUNCE) on detonation
            if (Secondary.StickLifetime == 0f)
            {
                ExplodeOrb(self, bounced);
                return;
            }
            OrbStick(self, other, bounced);
            return;
        }
        // bounce off the world (engine MOVETYPE_BOUNCE handles the reflection); a bounced orb's kill reads
        // MURDER_COMBO (QC ORs HITTYPE_BOUNCE here).
        bounced[0] = true;
        Api.Sound.Play(self, SoundChannel.Body, "weapons/electro_bounce.wav");
        self.Angles = QMath.VecToAngles(self.Velocity);
    }

    // W_Electro_Orb_Stick (electro.qc:396-455) — glue a landed orb to the surface it touched, facing into the wall
    // (vectoangles(-trace_plane_normal)), MOVETYPE_NONE; if the toucher is a MOVING bmodel, ride it via MOVETYPE_FOLLOW
    // (SetMovetypeFollow, common/util.qc:2129). stick_lifetime >= 0 overrides the orb's remaining death_time; -1 keeps it.
    // The orb stays shootable/touchable as a contact mine until it detonates. Off-by-default (Secondary.Stick).
    private void OrbStick(Entity self, Entity other, bool[] bounced)
    {
        // Recover -trace_plane_normal at the impact point (the engine touch callback doesn't carry the collision
        // globals) by re-probing a short worldonly traceline along the incoming velocity, like Minelayer/Porto do.
        Vector3 vel = self.Velocity;
        Vector3 dir = vel.LengthSquared() > 0.0001f ? Vector3.Normalize(vel) : new Vector3(0f, 0f, -1f);
        TraceResult tr = Api.Trace.Trace(self.Origin, Vector3.Zero, Vector3.Zero,
            self.Origin + dir * 32f, MoveFilter.WorldOnly, self);
        Vector3 intoWall = tr.Fraction < 1f ? -tr.PlaneNormal : -dir; // = -trace_plane_normal

        if (intoWall.LengthSquared() > 0f)
            self.Angles = QMath.VecToAngles(intoWall); // angles = vectoangles(-trace_plane_normal)
        self.Velocity = Vector3.Zero;
        self.MoveType = MoveType.None; // lock in place (disables gravity)
        self.Gravity = 0f;

        // stick_lifetime: -1 inherits the orb's existing death_time (its lifetime think is left scheduled);
        // a non-negative value re-arms the detonation timer to time + stick_lifetime (electro.qc:443-446).
        if (Secondary.StickLifetime >= 0f)
        {
            self.Think = s => ExplodeOrb(s, bounced);
            self.NextThink = Api.Clock.Time + Secondary.StickLifetime;
        }

        Api.Sound.Play(self, SoundChannel.Body, "weapons/electro_bounce.wav");

        // If the surface is a MOVING bmodel (func_plat/func_door), glue the orb so it rides the platform (QC
        // SetMovetypeFollow). Static-world hits (worldspawn sentinel / MOVETYPE_NONE) leave it locked in place.
        if (other.MoveType != MoveType.None && other.ClassName != "worldspawn" && !ReferenceEquals(other, self))
            SetMovetypeFollow(self, other);
    }

    // SetMovetypeFollow(ent, e) (common/util.qc:2129) — make a stuck orb ride a moving bmodel via MOVETYPE_FOLLOW.
    // Mirrors the Minelayer port: relative origin/angle bookkeeping the PhysicsFollow integrator replays each tick,
    // plus solid = NOT (a FOLLOW entity is always non-solid).
    private static void SetMovetypeFollow(Entity ent, Entity e)
    {
        ent.MoveType = MoveType.Follow;
        ent.Solid = Solid.Not;
        ent.Aiment = e;
        ent.PunchAngle = e.Angles;            // the original angles of the bmodel
        ent.ViewOfs = ent.Origin - e.Origin;  // relative origin
        ent.VAngle = ent.Angles - e.Angles;   // relative angles
    }

    // QC W_Electro_Attack_Orb:569-575 LimitedChildrenRubble prune — when secondary_limit > 0, count the firer's live
    // electro_orb entities and detonate the OLDEST (lowest NextThink = earliest scheduled lifetime) once the cap is
    // reached, so a new orb can be laid. Detonating an orb is its normal SECONDARY blast (adaptor_think2use_hittype
    // _splash). Only called when Secondary.Limit > 0, so it never runs at stock defaults (limit 0 = unlimited).
    private void EnforceOrbLimit(Entity actor)
    {
        if (Api.Services is null) return;
        var mine = Api.Entities.FindByClass("electro_orb")
            .Where(e => !e.IsFreed && ReferenceEquals(e.Owner, actor)).ToList();
        // Account for the orb about to be spawned: prune until strictly fewer than the limit remain.
        while (mine.Count >= Secondary.Limit)
        {
            Entity oldest = mine[0];
            for (int i = 1; i < mine.Count; i++)
                if (mine[i].NextThink < oldest.NextThink) oldest = mine[i];
            mine.Remove(oldest);
            // Force the oldest orb to detonate now (its captured bounced flag is gone here, so explode as a plain
            // secondary blast — QC's adaptor_think2use is the orb's lifetime SECONDARY explode).
            oldest.Touch = null; oldest.Think = null; oldest.TakeDamage = DamageMode.No;
            WeaponSplash.RadiusDamage(oldest, oldest.Origin, Secondary.Damage, Secondary.EdgeDamage, Secondary.Radius,
                oldest.Owner, RegistryId, Secondary.Force, accuracyWeapon: this, deathTag: DeathOrb);
            EffectEmitter.Emit("ELECTRO_BALLEXPLODE", oldest.Origin);
            WeaponSplash.ImpactSound(oldest, "weapons/electro_impact.wav");
            Api.Entities.Remove(oldest);
        }
    }

    // W_Electro_Explode (orb path) — orb blast (secondary balance). electro.qc
    private void ExplodeOrb(Entity self, bool[] bounced)
    {
        self.Touch = null;
        self.Think = null;
        self.TakeDamage = DamageMode.No;

        // deathTag carries HITTYPE_SECONDARY (QC W_Electro_Attack_Orb: projectiledeathtype = m_id|HITTYPE_SECONDARY),
        // plus HITTYPE_BOUNCE if the orb had already bounced — so the obituary reads MURDER_ORBS (or MURDER_COMBO
        // for a bounced orb) and wr_impacteffect picks the ELECTRO_BALLEXPLODE/secondary branch.
        string death = bounced[0] ? Damage.DeathTypes.WithHitType(DeathOrb, Damage.DeathTypes.Bounce) : DeathOrb;
        WeaponSplash.RadiusDamage(self, self.Origin, Secondary.Damage, Secondary.EdgeDamage, Secondary.Radius,
            self.Owner, RegistryId, Secondary.Force, accuracyWeapon: this, deathTag: death);

        EffectEmitter.Emit("ELECTRO_BALLEXPLODE", self.Origin);
        WeaponSplash.ImpactSound(self, "weapons/electro_impact.wav"); // QC SND_ELECTRO_IMPACT (secondary wr_impacteffect)
        Api.Entities.Remove(self);
    }

    /// <summary>g_balance_electro_combo_speed — chain delay = distance/speed (0 = instant). Base default 2000 (seeded in Configure).</summary>
    public float ComboSpeed = 2000f;

    /// <summary>g_balance_electro_combo_comboradius_thruwall — orbs beyond this distance must pass an LOS trace to chain.</summary>
    public float ComboComboRadiusThruwall = 200f;

    /// <summary>g_balance_electro_combo_safeammocheck — secondary checkammo also requires the follow-up combo bolt's ammo.</summary>
    public bool CombocSafeAmmoCheck = true;

    // QC projectiledeathtype tags. The bolt is the plain weapon tag; the orb adds HITTYPE_SECONDARY; a combo
    // blast adds HITTYPE_BOUNCE (electro.qc:150,187 — "use THIS type for a combo because primary can't bounce").
    // These string tags (passed as deathTag to RadiusDamage) are what drive the MURDER_BOLT/ORBS/COMBO obituary
    // split in DeathMessages — the int RegistryId path can only ever produce MURDER_BOLT.
    private string DeathBolt => Damage.DeathTypes.FromWeapon(NetName);
    private string DeathOrb => Damage.DeathTypes.WithHitType(DeathBolt, Damage.DeathTypes.Secondary);
    private string DeathCombo => Damage.DeathTypes.WithHitType(DeathBolt, Damage.DeathTypes.Bounce);

    /// <summary>
    /// Port of W_Electro_TriggerCombo (electro.qc): every live electro_orb within <paramref name="rad"/> of
    /// <paramref name="org"/> is converted to a combo orb (classname "electro_orb_chain"), credited to
    /// <paramref name="own"/>, and scheduled to detonate after a combo_speed-based delay (distance/speed).
    /// Each detonation re-triggers orbs within combo_comboradius, so the chain ripples outward — exactly
    /// like QC's recursive setthink(W_Electro_ExplodeCombo) chaining.
    /// </summary>
    private void TriggerCombo(Vector3 org, float rad, Entity? own)
    {
        if (Api.Services is null) return;
        // Snapshot first: we mutate orbs while RadiusDamage re-enumerates the world.
        var orbs = Api.Entities.FindInRadius(org, rad)
            .Where(e => e.ClassName == "electro_orb" && !e.IsFreed).ToList();
        foreach (Entity e in orbs)
        {
            // QC W_Electro_TriggerCombo:85-96 thruwall test: an orb farther than combo_comboradius_thruwall must
            // pass a LOS trace from the blast, or the chain aborts to it (combos don't reach orbs around corners
            // beyond the thruwall distance). Within thruwall distance the chain always reaches (the default WarpZone
            // find is non-thruwall, but the explicit thruwall cvar re-enables near-range thru-wall chaining).
            if (ComboComboRadiusThruwall > 0f
                && (e.Origin - org).Length() > ComboComboRadiusThruwall)
            {
                TraceResult tr = Api.Trace.Trace(org, Vector3.Zero, Vector3.Zero, e.Origin, MoveFilter.NoMonsters, e);
                if (tr.Fraction < 1f)
                    continue; // through a wall and outside thruwall range — abort
            }

            e.Owner = own;
            e.ClassName = "electro_orb_chain"; // so a re-trigger doesn't pick it up again
            e.Touch = null;
            e.TakeDamage = DamageMode.No;
            e.ProjectileDamage = null;

            float delay = (ComboSpeed > 0f) ? (e.Origin - org).Length() / ComboSpeed : 0f;
            e.Think = self => ExplodeCombo(self);
            e.NextThink = Api.Clock.Time + delay;
            if (delay <= 0f) ExplodeCombo(e);
        }
    }

    // W_Electro_ExplodeCombo — a combo orb's blast; it first re-triggers nearby orbs (chain), then explodes.
    private void ExplodeCombo(Entity self)
    {
        // chain to orbs within combo_comboradius before blasting.
        TriggerCombo(self.Origin, Combo.ComboRadius, self.Owner);
        self.Think = null;
        // QC W_Electro_ExplodeCombo:172-178 — combo_duration > 0 (default 0/OFF) replaces the instant blast with a
        // persistent explode-over-time field (W_Electro_Orb_ExplodeOverTime) that ticks combo_damage every frame for
        // combo_duration seconds. Inert at stock defaults (Combo.Duration 0 -> the instant RadiusDamage below).
        if (Combo.Duration > 0f)
        {
            SpawnExplodeOverTime(self.Origin, self.Owner);
            Api.Entities.Remove(self);
            return;
        }
        // QC W_Electro_ExplodeCombo:187 — deathtype = m_id | HITTYPE_BOUNCE (a combo can't use the primary's
        // plain tag because primary can't bounce), so the obituary reads MURDER_COMBO.
        WeaponSplash.RadiusDamage(self, self.Origin, Combo.Damage, Combo.EdgeDamage, Combo.Radius,
            self.Owner, RegistryId, Combo.Force, accuracyWeapon: this, deathTag: DeathCombo);
        EffectEmitter.Emit("ELECTRO_COMBO", self.Origin);
        WeaponSplash.ImpactSound(self, "weapons/electro_impact_combo.wav"); // QC SND_ELECTRO_IMPACT_COMBO
        Api.Entities.Remove(self);
    }

    // W_Electro_Orb_ExplodeOverTime / W_Electro_ExplodeComboThink (electro.qc:115-162) — the combo_duration field:
    // a stationary orb that re-RadiusDamages every think for combo_duration seconds (a damage-over-time zone),
    // dealing combo_damage * PHYS_INPUT_TIMELENGTH (frame-scaled) each tick and ORing HITTYPE_SPAM so the repeated
    // hits don't re-spam blast effects/sounds. Only spawned when combo_duration > 0 (off-by-default in xonotic).
    private void SpawnExplodeOverTime(Vector3 org, Entity? own)
    {
        Entity field = Api.Entities.Spawn();
        field.ClassName = "electro_orb"; // QC keeps it an electro_orb so a fresh blast can still re-combo it
        field.Owner = own;
        field.NetName = NetName;
        field.MoveType = MoveType.None;
        field.TakeDamage = DamageMode.No;
        Api.Entities.SetOrigin(field, org);

        float endTime = Api.Clock.Time + Combo.Duration;
        // QC HITTYPE_SPAM combo deathtype — the recurring ticks read as a combo kill but suppress repeat blast fx.
        string spamDeath = Damage.DeathTypes.WithHitType(DeathCombo, Damage.DeathTypes.Spam);
        void Tick(Entity self)
        {
            if (Api.Clock.Time >= endTime) { Api.Entities.Remove(self); return; }
            float dt = Api.Clock.FrameTime; // PHYS_INPUT_TIMELENGTH
            WeaponSplash.RadiusDamage(self, self.Origin, Combo.Damage * dt, Combo.EdgeDamage * dt, Combo.Radius,
                self.Owner, RegistryId, Combo.Force * dt, accuracyWeapon: this, deathTag: spamDeath);
            self.NextThink = Api.Clock.Time; // run every sim tick for the duration
        }
        field.Think = self => Tick(self);
        field.NextThink = Api.Clock.Time;
        EffectEmitter.Emit("ELECTRO_COMBO", org); // one spawn-time combo burst (HITTYPE_SPAM suppresses the per-tick repeats)
        WeaponSplash.ImpactSound(field, "weapons/electro_impact_combo.wav");
    }

    // bool IsFlying(entity) — common/physics/player.qc:843, the airshot test: airborne, not swimming, and at
    // least 24u of clearance below (so a player skimming the ground doesn't count). Used by the ELECTROBITCH check.
    private static bool IsFlying(Entity e)
    {
        if (e.OnGround) return false;
        if (e.WaterLevel >= 2) return false; // WATERLEVEL_SWIMMING
        TraceResult tr = Api.Trace.Trace(e.Origin, e.Mins, e.Maxs,
            e.Origin - new Vector3(0f, 0f, 24f), MoveFilter.Normal, e);
        return tr.Fraction >= 1f;
    }

    // METHOD(Electro, wr_checkammo1) — electro.qc:667-671: have ammo if the shared pool OR the persistent
    // magazine (weapon_load[m_id], reloadable) can cover one primary shot. The clip-load OR-term is a no-op
    // while reload_ammo defaults 0 (clip never loaded), but is ported for faithfulness on a reload-enabled server.
    public bool CheckAmmoPrimary(Entity actor)
        => actor.GetResource(AmmoType) >= Primary.Ammo
        || GetWeaponLoad(actor.WeaponState(new WeaponSlot(0)), RegistryId) >= Primary.Ammo;

    // METHOD(Electro, wr_checkammo2) — electro.qc:674-687. QC combo_safeammocheck (default 1): "true if you can
    // fire at least one secondary blob AND one primary shot after it" — so the auto-switch leaves the weapon while
    // it can still afford the follow-up combo bolt, not just one bare orb. Pool OR magazine, same as wr_checkammo1.
    public bool CheckAmmoSecondary(Entity actor)
    {
        float need = CombocSafeAmmoCheck ? Secondary.Ammo + Primary.Ammo : Secondary.Ammo;
        return actor.GetResource(AmmoType) >= need
            || GetWeaponLoad(actor.WeaponState(new WeaponSlot(0)), RegistryId) >= need;
    }
}
