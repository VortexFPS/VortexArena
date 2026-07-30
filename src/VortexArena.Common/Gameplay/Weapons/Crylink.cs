using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Crylink — port of common/weapons/weapon/crylink.{qh,qc}. A splash weapon firing a burst of
/// fast energy spikes that spread out and deflect off walls. Each spike does radius damage on contact
/// (and a reduced amount on each bounce), can bounce a limited number of times, and fades after its
/// lifetime. Primary spreads wide (6 shots); secondary fires a tighter group (5 shots) with negative
/// force (pulls victims toward impact).
///
/// Identity/attributes from crylink.qh; balance from bal-wep-xonotic.cfg (g_balance_crylink_*).
/// This port covers the multi-spike fan-out (circular pattern + secondary linear fan), per-spike
/// bounce/lifetime, the over-lifetime fade-damage scaling, the link-join "converge on release" mechanic,
/// and the link-explode chain detonation. Only client projectile networking/effects are left out.
/// </summary>
[Weapon]
public sealed class Crylink : Weapon
{
    /// <summary>Per-fire-mode balance block — QC WEP_CVAR_PRI/SEC(WEP_CRYLINK, *).</summary>
    public struct ModeBalance
    {
        public float Ammo;                // *_ammo (cells per shot)
        public float Animtime;            // *_animtime
        public float BounceDamageFactor;  // *_bouncedamagefactor
        public float Bounces;             // *_bounces (max bounce count)
        public float Damage;              // *_damage (per spike)
        public float EdgeDamage;          // *_edgedamage
        public float Force;               // *_force (negative = pull)
        public float MiddleLifetime;      // *_middle_lifetime
        public float MiddleFadeTime;      // *_middle_fadetime
        public float OtherLifetime;       // *_other_lifetime
        public float OtherFadeTime;       // *_other_fadetime
        public float Radius;              // *_radius
        public float Refire;              // *_refire
        public int   Shots;               // *_shots (spike count)
        public float Speed;               // *_speed
        public float Spread;              // *_spread
        public int   JoinExplode;         // *_joinexplode (convergence bonus blast on/off)
        public float JoinExplodeDamage;   // *_joinexplode_damage
        public float JoinExplodeEdge;     // *_joinexplode_edgedamage
        public float JoinExplodeRadius;   // *_joinexplode_radius
        public float JoinExplodeForce;    // *_joinexplode_force
    }

    public ModeBalance Primary;
    public ModeBalance Secondary;

    /// <summary>g_balance_crylink_secondary — whether secondary fire is enabled.</summary>
    public bool SecondaryEnabled = true;


    public Crylink()
    {
        NetName = "crylink";
        AmmoType = ResourceType.Cells;   // QC ammo_type
        DisplayName = "Crylink";
        Impulse = 6;
        // WEP_FLAG_NORMAL | WEP_FLAG_RELOADABLE | WEP_TYPE_SPLASH | WEP_FLAG_CANCLIMB
        SpawnFlags = WeaponFlags.Normal | WeaponFlags.Reloadable | WeaponFlags.TypeSplash | WeaponFlags.CanClimb;
        Color = new Vector3(0.918f, 0.435f, 0.976f);
        ViewModel = "h_crylink.iqm";  // MDL_CRYLINK_VIEW
        WorldModel = "v_crylink.md3"; // MDL_CRYLINK_WORLD
        ItemModel = "g_crylink.md3";  // MDL_CRYLINK_ITEM
    }

    public override void Configure()
    {
        Primary.Ammo = Bal("g_balance_crylink_primary_ammo", 3f);
        Primary.Animtime = Bal("g_balance_crylink_primary_animtime", 0.3f);
        Primary.BounceDamageFactor = Bal("g_balance_crylink_primary_bouncedamagefactor", 1f);
        Primary.Bounces = Bal("g_balance_crylink_primary_bounces", 1f);
        Primary.Damage = Bal("g_balance_crylink_primary_damage", 10f);
        Primary.EdgeDamage = Bal("g_balance_crylink_primary_edgedamage", 5f);
        Primary.Force = Bal("g_balance_crylink_primary_force", -50f);
        Primary.MiddleLifetime = Bal("g_balance_crylink_primary_middle_lifetime", 5f);
        Primary.MiddleFadeTime = Bal("g_balance_crylink_primary_middle_fadetime", 5f);
        Primary.OtherLifetime = Bal("g_balance_crylink_primary_other_lifetime", 5f);
        Primary.OtherFadeTime = Bal("g_balance_crylink_primary_other_fadetime", 5f);
        Primary.Radius = Bal("g_balance_crylink_primary_radius", 80f);
        Primary.Refire = Bal("g_balance_crylink_primary_refire", 0.7f);
        Primary.Shots = BalInt("g_balance_crylink_primary_shots", 6);
        Primary.Speed = Bal("g_balance_crylink_primary_speed", 2000f);
        Primary.Spread = Bal("g_balance_crylink_primary_spread", 0.08f);
        Primary.JoinExplode = BalInt("g_balance_crylink_primary_joinexplode", 1);
        Primary.JoinExplodeDamage = Bal("g_balance_crylink_primary_joinexplode_damage", 0f);
        Primary.JoinExplodeEdge = Bal("g_balance_crylink_primary_joinexplode_edgedamage", 0f);
        Primary.JoinExplodeRadius = Bal("g_balance_crylink_primary_joinexplode_radius", 0f);
        Primary.JoinExplodeForce = Bal("g_balance_crylink_primary_joinexplode_force", 0f);

        Secondary.Ammo = Bal("g_balance_crylink_secondary_ammo", 3f);
        Secondary.Animtime = Bal("g_balance_crylink_secondary_animtime", 0.2f);
        Secondary.BounceDamageFactor = Bal("g_balance_crylink_secondary_bouncedamagefactor", 0.5f);
        Secondary.Bounces = Bal("g_balance_crylink_secondary_bounces", 0f);
        Secondary.Damage = Bal("g_balance_crylink_secondary_damage", 8f);
        Secondary.EdgeDamage = Bal("g_balance_crylink_secondary_edgedamage", 4f);
        Secondary.Force = Bal("g_balance_crylink_secondary_force", -200f);
        Secondary.MiddleLifetime = Bal("g_balance_crylink_secondary_middle_lifetime", 5f);
        Secondary.MiddleFadeTime = Bal("g_balance_crylink_secondary_middle_fadetime", 5f);
        Secondary.OtherLifetime = Bal("g_balance_crylink_secondary_other_lifetime", 5f);
        Secondary.OtherFadeTime = Bal("g_balance_crylink_secondary_other_fadetime", 5f);
        Secondary.Radius = Bal("g_balance_crylink_secondary_radius", 100f);
        Secondary.Refire = Bal("g_balance_crylink_secondary_refire", 0.7f);
        Secondary.Shots = BalInt("g_balance_crylink_secondary_shots", 5);
        Secondary.Speed = Bal("g_balance_crylink_secondary_speed", 3000f);
        Secondary.Spread = Bal("g_balance_crylink_secondary_spread", 0.01f);
        Secondary.JoinExplode = BalInt("g_balance_crylink_secondary_joinexplode", 0);
        Secondary.JoinExplodeDamage = Bal("g_balance_crylink_secondary_joinexplode_damage", 0f);
        Secondary.JoinExplodeEdge = Bal("g_balance_crylink_secondary_joinexplode_edgedamage", 0f);
        Secondary.JoinExplodeRadius = Bal("g_balance_crylink_secondary_joinexplode_radius", 0f);
        Secondary.JoinExplodeForce = Bal("g_balance_crylink_secondary_joinexplode_force", 0f);

        SecondaryEnabled = BalBool("g_balance_crylink_secondary", true);

        // join/link balance (bal-wep-xonotic.cfg g_balance_crylink_*). Per-spike fade rate is now read from
        // *_middle_fadetime / *_other_fadetime above (Configure), not a single hardcoded 5s.
        PrimaryJoinSpread = Bal("g_balance_crylink_primary_joinspread", 0.2f);
        PrimaryJoinDelay = Bal("g_balance_crylink_primary_joindelay", 0.1f);
        PrimaryLinkExplode = BalInt("g_balance_crylink_primary_linkexplode", 0);
        SecondaryJoinSpread = Bal("g_balance_crylink_secondary_joinspread", 0f);
        SecondaryJoinDelay = Bal("g_balance_crylink_secondary_joindelay", 0f);
        SecondaryLinkExplode = BalInt("g_balance_crylink_secondary_linkexplode", 1);
        SecondarySpreadType = BalInt("g_balance_crylink_secondary_spreadtype", 1);
    }

    /// <summary>g_balance_crylink_*_joinspread — converge speed factor on release (0 = parallel, no convergence).</summary>
    public float PrimaryJoinSpread = 0.5f;
    public float SecondaryJoinSpread = 0f;
    /// <summary>g_balance_crylink_*_linkexplode — 1: chain-detonate the group on a friendly-safe hit; 2: always.</summary>
    public int PrimaryLinkExplode = 0;
    public int SecondaryLinkExplode = 0;
    /// <summary>g_balance_crylink_secondary_spreadtype — 1: circular pattern; 0: linear horizontal fan.</summary>
    public int SecondarySpreadType = 1;
    /// <summary>g_balance_crylink_*_joindelay — min time the group must persist before it can link-join.</summary>
    public float PrimaryJoinDelay = 0.1f;
    public float SecondaryJoinDelay = 0f;

    // METHOD(Crylink, wr_think) — common/weapons/weapon/crylink.qc
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire)
    {
        var st = actor.WeaponState(slot);

        // QC wr_think forced-reload pre-check (crylink.qc:494-498): if reloading is enabled
        // (g_balance_crylink_reload_ammo != 0) and the clip has dropped below the cheapest per-shot cost
        // (min(primary_ammo, secondary_ammo)), force a reload THIS tick. In QC this is the head of the
        // if/else-if chain whose other arms are the fire1/fire2 branches, so a forced reload SUPPRESSES the
        // firing branches that tick (but NOT the separate join-release block that follows — it always runs).
        // Inactive at stock balance (reload_ammo defaults 0), but now genuinely live: flip
        // g_balance_crylink_reload_ammo on and an under-stocked clip auto-reloads here exactly as Base.
        bool forcedReload = ReloadingAmmo() != 0f && st.ClipLoad < MathF.Min(Primary.Ammo, Secondary.Ammo);
        if (forcedReload)
            WrReload(actor, slot);

        // crylink_waitrelease: after firing a join-enabled group, the spikes stay spread while fire is HELD;
        // they converge (W_Crylink_LinkJoin) only once the player RELEASES — the weapon's defining "time the
        // convergence onto a target" mechanic (crylink.qc describe()). QC gates the join on BOTH the
        // button-RELEASE level check (!(fire & 1) / !(fire & 2)) AND a joindelay floor
        // (time > teleport_time == firetime + joindelay) — the joindelay is only a minimum floor, the release
        // is the trigger. The live held-button state is supplied each tick by WeaponFireDriver (st.ButtonAttack
        // /2); for the secondary group too, since this block runs in the every-tick WrThink(Primary) call where
        // SetButtons has already recorded this tick's ATK2 (even on the ticks WrThink(Secondary) isn't called).
        if (st.CrylinkWaitRelease != 0)
        {
            bool isPrimary = st.CrylinkWaitRelease == 1;
            bool released = isPrimary ? !st.ButtonAttack : !st.ButtonAttack2; // QC !(fire & 1) / !(fire & 2)
            if (released)
            {
                Entity? group = st.CrylinkLastGroup;
                bool groupAlive = group is not null && !group.IsFreed;
                float joinDelay = isPrimary ? PrimaryJoinDelay : SecondaryJoinDelay;
                // QC: !crylink_lastgroup || time > teleport_time. A live group converges once the joindelay
                // floor passes; if its head spike already died (this port's CrylinkLastGroup can dangle where
                // QC re-heads the queue to a live spike) there's nothing to converge, but we still drop out of
                // wait-release so a dead group can't strand the player unable to fire (QC clears it regardless).
                if (!groupAlive || Api.Clock.Time > group!.LTime + joinDelay)
                {
                    if (groupAlive)
                        LinkJoin(group!, (isPrimary ? PrimaryJoinSpread : SecondaryJoinSpread)
                            * (isPrimary ? Primary.Speed : Secondary.Speed));
                    st.CrylinkLastGroup = null;
                    st.CrylinkWaitRelease = 0;
                }
            }
        }

        // Each spike volley is refire-gated (QC weapon_prepareattack with the primary/secondary refire). The
        // CrylinkWaitRelease guard additionally blocks a new volley while the previous group waits to join.
        // A forced reload this tick is the QC if/else-if head and so suppresses both fire branches.
        if (!forcedReload && fire == FireMode.Primary && st.CrylinkWaitRelease != 1)
        {
            if (PrepareAttack(actor, slot, fire))
                Attack(actor, slot, st, Primary, secondary: false);
        }
        else if (!forcedReload && fire == FireMode.Secondary && SecondaryEnabled && st.CrylinkWaitRelease != 2)
        {
            if (PrepareAttack(actor, slot, fire))
                Attack(actor, slot, st, Secondary, secondary: true);
        }
    }

    // METHOD(Crylink, wr_aim) — crylink.qc:485-491. With a 10% chance the bot fires the primary (leading at
    // primary speed with primary middle_lifetime as the time-to-impact estimate); otherwise it fires the
    // secondary (leading at secondary speed with secondary middle_lifetime). The brain's BotBrain owns the shot
    // lead and decision; this hook owns only the 10/90 primary/secondary button pick.
    public override bool BotWantsSecondary(float enemyDistance, float skill, ref BotAimState ctx)
    {
        // 10% primary, 90% secondary (QC random() < 0.10)
        return ctx.Random01 >= 0.10f;
    }

    // Lead the target by the primary speed (matching QC bot_aim call on primary), which dominates the fire
    // decision since it has the (typical) faster speed. Secondary is also projected (via its own _middle_lifetime)
    // but BotWantsSecondary routes the final shot independently of the lead (common pattern: lead generic, button-route
    // per-weapon).
    public override float BotAimShotSpeed(float defaultSpeed) => Primary.Speed;

    // Refire/animtime from the (cvar-seeded) per-mode balance blocks.
    public override float RefireFor(FireMode fire) => (fire == FireMode.Secondary ? Secondary : Primary).Refire;
    public override float AnimtimeFor(FireMode fire) => (fire == FireMode.Secondary ? Secondary : Primary).Animtime;

    // QC wr_reload: W_Reload(actor, weaponentity, min(WEP_CVAR_PRI(ammo), WEP_CVAR_SEC(ammo)), SND_RELOAD)
    // — the reload's per-shot ammo floor is the cheaper of the two modes' costs, not the generic 1.
    protected override float ReloadingAmmoMin() => MathF.Min(Primary.Ammo, Secondary.Ammo);

    // W_Crylink_Attack / W_Crylink_Attack2 — spray `shots` spikes in a spread, linked into a converge group.
    private void Attack(Entity actor, WeaponSlot slot, WeaponSlotState st, ModeBalance bal, bool secondary)
    {
        actor.TakeResource(AmmoType, bal.Ammo);

        QMath.AngleVectors(actor.Angles, out Vector3 forward, out Vector3 right, out Vector3 up);

        // QC W_Crylink_Attack/Attack2 (crylink.qc:285-291,384-388): compute maxdmg for the accuracy
        // "fired" denominator (W_SetupShot maxdamage arg → accuracy_add fired credit).
        //   maxdmg = damage * shots * (1 + bouncedamagefactor * bounces) [+ joinexplode_damage if joinexplode]
        float maxDmg = bal.Damage * bal.Shots * (1f + bal.BounceDamageFactor * bal.Bounces);
        if (bal.JoinExplode != 0) maxDmg += bal.JoinExplodeDamage;
        ShotInfo shot = WeaponFiring.SetupShot(actor, forward, wep: this, maxDamage: maxDmg, recoil: 2f);

        int shots = bal.Shots;
        float damage = bal.Damage, edge = bal.EdgeDamage, radius = bal.Radius, force = bal.Force;
        float bounceFactor = bal.BounceDamageFactor;
        int maxBounces = (int)bal.Bounces;
        int linkExplode = secondary ? SecondaryLinkExplode : PrimaryLinkExplode;

        // QC projectiledeathtype = thiswep.m_id (| HITTYPE_SECONDARY for the secondary). The pipeline int
        // deathtype is RegistryId for both modes (impact/kill messages don't differ for crylink); the string
        // deathTag carries the HITTYPE bits so HITTYPE_SECONDARY (and HITTYPE_BOUNCE, added on a bounce) survive
        // to the obituary and to the splash-bit logic, exactly as Mortar threads its bounce/secondary tag.
        string baseDeathType = secondary
            ? DeathTypes.WithHitType(DeathTypes.FromWeapon(NetName), DeathTypes.Secondary)
            : DeathTypes.FromWeapon(NetName);

        // The QC center spike (counter == (shots-1)*0.5) carries *_middle_lifetime/*_middle_fadetime; the rest
        // use *_other_*. For the primary W_Crylink_Attack QC uses counter==0 as the middle spike instead.
        int middleIndex = secondary ? (int)((shots - 1) * 0.5f) : 0;

        var group = new List<Entity>(shots);
        for (int i = 0; i < shots; ++i)
        {
            Entity proj = Api.Entities.Spawn();
            proj.ClassName = "spike";
            proj.Owner = actor;
            proj.NetName = NetName;
            proj.MoveType = MoveType.BounceMissile;
            Projectiles.MakeTrigger(proj); // QC PROJECTILE_MAKETRIGGER (SOLID_CORPSE): transparent to the firer's movement
            proj.Flags = EntFlags.Item; // QC FL_PROJECTILE
            Api.Entities.SetSize(proj, Vector3.Zero, Vector3.Zero);
            Api.Entities.SetOrigin(proj, shot.Origin);

            // Spread: primary + secondary spreadtype 1 use the circular pattern; secondary spreadtype 0 is a
            // linear horizontal fan (s = w_shotdir + ((i+0.5)/shots*2 - 1) * right * spread).
            Vector3 dir;
            if (secondary && SecondarySpreadType == 0)
            {
                float lat = ((i + 0.5f) / shots) * 2f - 1f;
                dir = QMath.Normalize(shot.Dir + lat * right * (bal.Spread * WeaponFiring.WeaponSpreadFactor));
            }
            else
            {
                Vector3 s = WeaponFiring.CalculateSpreadPattern(i, shots) * (bal.Spread * WeaponFiring.WeaponSpreadFactor);
                dir = QMath.Normalize(shot.Dir + right * s.Y + up * s.Z);
            }
            proj.Velocity = WeaponFiring.ProjectileVelocity(dir, up, bal.Speed);
            proj.Angles = QMath.VecToAngles(proj.Velocity);

            // QC crylink.qc:306-307,405-406 — flag each spike as a dodgeable hazard (rating = per-mode spike damage).
            proj.BotDodge = true;
            proj.BotDodgeRating = bal.Damage;

            // The center spike uses middle_lifetime/middle_fadetime, the rest other_*. fade_time/fade_rate
            // scale the spike's damage down to 0 over fadetime once its lifetime starts running out. Each spike
            // reads its OWN fadetime cvar (the rate), not one collapsed value.
            bool isMiddle = i == middleIndex;
            float lifetime = isMiddle ? bal.MiddleLifetime : bal.OtherLifetime;
            float fadeTime = isMiddle ? bal.MiddleFadeTime : bal.OtherFadeTime;
            proj.MaxHealth = Api.Clock.Time + lifetime;         // QC .fade_time
            proj.Health = (fadeTime > 0f) ? 1f / fadeTime : 0f; // QC .fade_rate (reused .health field)
            proj.Count = maxBounces;                            // QC .cnt = remaining bounces
            proj.LTime = Api.Clock.Time;                        // group spawn time (for joindelay)

            proj.Think = self => Api.Entities.Remove(self);     // W_Crylink_Fadethink
            proj.NextThink = proj.MaxHealth + fadeTime;
            // QC realowner (the damage attacker) stays the firer even after the bounce clears proj.owner; the port
            // has a single Owner field, so capture `actor` here as the immutable attacker passed to RadiusDamage.
            Entity realOwner = actor;
            proj.Touch = (self, other) => OnTouch(self, other, realOwner, damage, edge, radius, force,
                bounceFactor, linkExplode, baseDeathType, group, secondary);

            // MUTATOR_CALLHOOK(EditProjectile, actor, proj) — fired per spike (crylink.qc).
            var ep = new MutatorHooks.EditProjectileArgs(actor, proj);
            MutatorHooks.EditProjectile.Call(ref ep);

            group.Add(proj);
        }

        Api.Sound.Play(actor, SoundChannel.Weapon, secondary ? "weapons/crylink_fire2.wav" : "weapons/crylink_fire.wav");
        EffectEmitter.Emit("CRYLINK_MUZZLEFLASH", shot.Origin, shot.Dir * 1000f, 1, except: actor);

        // Register the group for the converge-on-release link-join (only when joinspread is enabled).
        float joinSpread = secondary ? SecondaryJoinSpread : PrimaryJoinSpread;
        if (joinSpread != 0f && shots > 1)
        {
            st.CrylinkLastGroup = group[0];
            st.CrylinkWaitRelease = secondary ? 2 : 1;
            _groups[group[0]] = new GroupInfo(group, actor, bal, secondary, baseDeathType);
        }
    }

    /// <summary>The per-group bookkeeping a registered link-group needs after Attack returns: the spike list,
    /// the firer (for the joinexplode RadiusDamage attacker), the per-mode balance (joinexplode bonus + radius),
    /// the secondary flag (impact-fx selection) and the string deathtype (HITTYPE bits).</summary>
    private sealed record GroupInfo(List<Entity> Spikes, Entity Owner, ModeBalance Bal, bool Secondary, string DeathType);

    // Active link groups keyed by their head spike (the C# successor to the QC queuenext/queueprev ring).
    private readonly Dictionary<Entity, GroupInfo> _groups = new();

    /// <summary>
    /// Port of W_Crylink_LinkJoin (crylink.qc): retarget every live spike in the group so they converge on
    /// the group's average position/velocity, producing the signature "snap together then spread" pattern.
    /// jspeed = joinspread * initial speed controls how fast they converge. Returns the meeting origin and the
    /// time-to-meet (QC w_crylink_linkjoin_time), so WrThink can schedule the W_Crylink_LinkJoinEffect_Think
    /// convergence sparkle + joinexplode bonus at the right place/time.
    /// </summary>
    private (Vector3 origin, float time) LinkJoin(Entity head, float jspeed)
    {
        if (!_groups.TryGetValue(head, out var info)) return (head.Origin, 0f);
        _groups.Remove(head);
        var group = info.Spikes;
        group.RemoveAll(e => e.IsFreed);

        Vector3 avgOrg = Vector3.Zero, avgVel = Vector3.Zero;
        if (group.Count == 0) return (head.Origin, 0f);
        foreach (var p in group) { avgOrg += p.Origin; avgVel += p.Velocity; }
        avgOrg /= group.Count;
        avgVel /= group.Count;

        if (group.Count < 2)
            return (avgOrg, 0f); // nothing to do (QC returns avg_org)

        if (jspeed == 0f)
        {
            foreach (var p in group) p.Velocity = avgVel;
            // QC targ_origin = avg_org + HUGE * normalize(avg_vel); time stays 0.
            return (avgOrg, 0f);
        }

        // avg distance from center -> time to meet; aim each spike at the meeting point.
        float avgDist = 0f;
        foreach (var p in group) avgDist += (p.Origin - avgOrg).LengthSquared();
        avgDist = MathF.Sqrt(avgDist / group.Count);
        if (avgDist == 0f) return (avgOrg, 0f);

        float meetTime = avgDist / jspeed;
        Vector3 targ = avgOrg + meetTime * avgVel;
        foreach (var p in group)
        {
            p.Velocity = (targ - p.Origin) * (1f / meetTime);
            p.Angles = QMath.VecToAngles(p.Velocity);
        }

        // Schedule the convergence think at the meeting point (W_Crylink_LinkJoinEffect_Think): a head-tracked
        // entity that fires once the spikes are due to meet, evaluating the >=2-converged joinexplode bonus +
        // the EFFECT_CRYLINK_JOINEXPLODE sparkle. The group list lives on long enough via the head reference.
        ScheduleLinkJoinEffect(targ, meetTime, info);
        return (targ, meetTime);
    }

    /// <summary>
    /// Port of W_Crylink_LinkJoinEffect_Think (crylink.qc): at the convergence meeting time, count how many
    /// spikes are now very close to the meeting point; if at least 2 and the mode's joinexplode is enabled,
    /// deal the (n/shots)-scaled joinexplode bonus RadiusDamage and emit EFFECT_CRYLINK_JOINEXPLODE there.
    /// Scheduled as a self-deleting think entity, exactly as the QC linkjoineffect entity.
    /// </summary>
    private void ScheduleLinkJoinEffect(Vector3 pos, float meetTime, GroupInfo info)
    {
        Entity fx = Api.Entities.Spawn();
        fx.ClassName = "linkjoineffect";
        fx.Owner = info.Owner;
        Api.Entities.SetOrigin(fx, pos);
        fx.NextThink = Api.Clock.Time + meetTime;
        fx.Think = self =>
        {
            // QC: is there at least 2 projectiles very close to this.origin? (vlen2(p.org-this.org) < vlen2(p.vel)*frametime)
            float frameTime = Api.Clock.FrameTime;
            int n = 0;
            foreach (var p in info.Spikes)
            {
                if (p.IsFreed) continue;
                float distSq = (p.Origin - self.Origin).LengthSquared();
                float velSq = p.Velocity.LengthSquared();
                if (distSq < velSq * frameTime) ++n;
            }
            if (n >= 2 && info.Bal.JoinExplode != 0)
            {
                float scale = n / (float)info.Bal.Shots;
                WeaponSplash.RadiusDamage(self, self.Origin,
                    scale * info.Bal.JoinExplodeDamage, scale * info.Bal.JoinExplodeEdge,
                    scale * info.Bal.JoinExplodeRadius, info.Owner, RegistryId,
                    scale * info.Bal.JoinExplodeForce, accuracyWeapon: this, deathTag: info.DeathType);
                EffectEmitter.Emit("CRYLINK_JOINEXPLODE", self.Origin, Vector3.Zero, n);
            }
            Api.Entities.Remove(self);
        };
    }

    // W_Crylink_Touch — radius damage on contact (faded over lifetime); reduced damage on a bounce, until
    // bounces run out; chain-detonate the whole group when linkexplode says so. crylink.qc
    // `realOwner` is the captured firer (QC realowner — the damage attacker that survives owner=NULL on bounce);
    // `deathType` is the string deathtype carrying HITTYPE_SECONDARY (and HITTYPE_BOUNCE on a bounced spike).
    private void OnTouch(Entity self, Entity other, Entity realOwner, float damage, float edge, float radius,
        float force, float bounceFactor, int linkExplode, string deathType, List<Entity> group, bool secondary)
    {
        // QC wr_impacteffect keys the impact sprite/sound off HITTYPE_SECONDARY: secondary spikes use the smaller
        // CRYLINK_IMPACT2 (SND_CRYLINK_IMPACT2), primary the bigger CRYLINK_IMPACT (SND_CRYLINK_IMPACT).
        string impactFx = secondary ? "CRYLINK_IMPACT2" : "CRYLINK_IMPACT";
        string impactSnd = secondary ? "weapons/crylink_impact2.wav" : "weapons/crylink_impact.wav";

        // a = fade scalar in [0,1]: 1 - (time - fade_time) * fade_rate.
        float a = QMath.Clamp(1f - (Api.Clock.Time - self.MaxHealth) * self.Health, 0f, 1f);
        bool finalHit = self.Count <= 0 || other.TakeDamage != DamageMode.No;
        float f = (finalHit ? 1f : bounceFactor) * a;

        // QC RadiusDamage returns totaldamage = total_damage_to_creatures (damage.qc:931). The port now returns
        // this from WeaponSplash.RadiusDamage so the chain-detonate gate can be EXACT (crylink.qc:249:
        // `if (totaldamage && ((linkexplode==1 && !WouldHitFriendly)||(linkexplode==2)))`).
        // A secondary spike hitting a bare wall returns 0 → no chain-detonate, exactly matching Base.
        float totalDamage = WeaponSplash.RadiusDamage(self, self.Origin, f * damage, f * edge, radius, realOwner,
            RegistryId, f * force, directHit: other, accuracyWeapon: this, deathTag: deathType);

        // QC ordering: chain-detonate FIRST on ANY damaging touch (incl. a non-final bounce) when linkexplode
        // says so — linkexplode==1 is friendly-gated (refrains near teammates), linkexplode==2 is unconditional.
        bool chainDetonate = totalDamage > 0f &&
            ((linkExplode == 1 && !WouldHitFriendly(self, realOwner, radius)) || linkExplode == 2);
        if (chainDetonate)
        {
            LinkExplode(self, other, realOwner, group, damage, edge, radius, force, deathType, secondary);
            WeaponSplash.ImpactSound(self, impactSnd);
            EffectEmitter.Emit(impactFx, self.Origin);
            RemoveFromGroup(self, group);
            Api.Entities.Remove(self);
            return;
        }
        if (finalHit)
        {
            // QC just unlinks/deletes (no extra explode); the impact fx/sound play in wr_impacteffect on the client.
            WeaponSplash.ImpactSound(self, impactSnd);
            EffectEmitter.Emit(impactFx, self.Origin);
            RemoveFromGroup(self, group);
            Api.Entities.Remove(self);
            return;
        }

        // Survived a bounce: spend one bounce. MOVETYPE_BOUNCEMISSILE reflects the velocity in the engine.
        --self.Count;
        self.Angles = QMath.VecToAngles(self.Velocity);
        // QC clears owner so a bounced spike can hurt its own firer (the realOwner capture keeps the damage
        // attributed to the firer), and tags the deathtype with HITTYPE_BOUNCE for the obituary.
        self.Owner = null;
        if (!DeathTypes.HasHitType(deathType, DeathTypes.Bounce))
        {
            string bounced = DeathTypes.WithHitType(deathType, DeathTypes.Bounce);
            // Re-bind the touch closure so subsequent hits carry the bounce bit (and the now-cleared owner is
            // moot — realOwner stays the firer). The fade/bounce-factor state lives on the entity fields.
            self.Touch = (s, o) => OnTouch(s, o, realOwner, damage, edge, radius, force, bounceFactor,
                linkExplode, bounced, group, secondary);
        }
    }

    /// <summary>
    /// Port of W_Crylink_Touch_WouldHitFriendly (crylink.qc): scan the blast radius — if any damageable, live
    /// entity is an ENEMY, the explode is allowed (returns false); if the only damageable targets are teammates,
    /// the linkexplode==1 chain refrains (returns true). With no damageable target nearby returns false.
    /// </summary>
    private static bool WouldHitFriendly(Entity projectile, Entity realOwner, float rad)
    {
        Vector3 center = projectile.Origin + (projectile.Mins + projectile.Maxs) * 0.5f;
        bool hitFriendly = false;
        foreach (Entity head in Api.Entities.FindInRadius(center, rad + 16f)) // QC rad + MAX_DAMAGEEXTRARADIUS
        {
            if (head.TakeDamage == DamageMode.No || head.DeadState != DeadFlag.No) continue;
            if (Teams.SameTeam(head, realOwner))
                hitFriendly = true;
            else
                return false; // an enemy is in range — go ahead and explode
        }
        return hitFriendly;
    }

    // W_Crylink_LinkExplode — detonate every other spike in the group at its current position. `directHit` is
    // the original toucher (QC directhitentity), threaded into each link's RadiusDamage so the struck target
    // skips the LOS reduction and the force aims at it, exactly as the QC recursion passes it down the queue.
    private void LinkExplode(Entity except, Entity directHit, Entity realOwner, List<Entity> group, float damage,
        float edge, float radius, float force, string deathType, bool secondary)
    {
        string impactFx = secondary ? "CRYLINK_IMPACT2" : "CRYLINK_IMPACT";
        string impactSnd = secondary ? "weapons/crylink_impact2.wav" : "weapons/crylink_impact.wav";
        foreach (var e in group.ToArray())
        {
            if (ReferenceEquals(e, except) || e.IsFreed) continue;
            float a = QMath.Clamp(1f - (Api.Clock.Time - e.MaxHealth) * e.Health, 0f, 1f);
            // QC passes realowner (e.crylink_owner — always the firer, even after a bounce cleared e.owner) as
            // attacker and the original toucher as directhitentity.
            WeaponSplash.RadiusDamage(e, e.Origin, a * damage, a * edge, radius, realOwner, RegistryId, a * force,
                directHit: directHit, accuracyWeapon: this, deathTag: deathType);
            WeaponSplash.ImpactSound(e, impactSnd);
            EffectEmitter.Emit(impactFx, e.Origin);
            e.Touch = null; e.Think = null;
            Api.Entities.Remove(e);
        }
        // QC never emits EFFECT_CRYLINK_JOINEXPLODE from a chain detonation — that particle is the convergence
        // sparkle (W_Crylink_LinkJoinEffect_Think) only. So no JOINEXPLODE here (corrects the wrong-trigger port).
        group.Clear();
    }

    private void RemoveFromGroup(Entity e, List<Entity> group)
    {
        group.Remove(e);
        // If this was a registered group head, drop the registration.
        if (_groups.ContainsKey(e)) _groups.Remove(e);
    }

    // METHOD(Crylink, wr_checkammo1) — crylink.qc. The wait-release guard ("don't run out of ammo and switch
    // weapons while a join-group waits for release") needs the per-slot CrylinkWaitRelease/LastGroup state; the
    // dispatch (WeaponFireGate) hands us only `actor`, so we read the primary slot (slot 0) state, matching the
    // single-slot fire model this port uses for crylink.
    public bool CheckAmmoPrimary(Entity actor)
    {
        var st = actor.WeaponState(new WeaponSlot(0));
        if (st.CrylinkWaitRelease != 0 && st.CrylinkLastGroup is not null) return true;
        return actor.GetResource(AmmoType) >= Primary.Ammo;
    }

    // METHOD(Crylink, wr_checkammo2) — crylink.qc
    public bool CheckAmmoSecondary(Entity actor)
    {
        var st = actor.WeaponState(new WeaponSlot(0));
        if (st.CrylinkWaitRelease != 0 && st.CrylinkLastGroup is not null) return true;
        return actor.GetResource(AmmoType) >= Secondary.Ammo;
    }
}
