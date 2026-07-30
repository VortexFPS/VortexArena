using System;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Shared projectile setup helpers — the Godot-free successor to the QC projectile macros in
/// <c>server/weapons/common.qh</c>.
/// </summary>
public static class Projectiles
{
    // SUPERCONTENTS bits the projectile hit mask uses (DP collision.h). Common can't reference the Engine's
    // SuperContents class (Engine depends on Common, not the reverse), so mirror the bit values here — exactly
    // as PlayerPhysics already does for the liquid bits. These MUST match XonoticGodot.Engine.Collision.SuperContents.
    private const int SuperContentsSolid  = 0x00000001;
    private const int SuperContentsBody   = 0x02000000;
    private const int SuperContentsCorpse = 0x20000000;

    /// <summary>
    /// Port of <c>PROJECTILE_MAKETRIGGER</c> (server/weapons/common.qh:33): make <paramref name="e"/> a
    /// projectile that is TRANSPARENT to player movement but still collides with the world, player/monster
    /// bodies and corpses.
    ///
    /// In Darkplaces a player's own movement trace masks <c>SOLID|BODY|PLAYERCLIP</c> — deliberately WITHOUT
    /// <c>CORPSE</c>. By giving the projectile <see cref="Solid.Corpse"/> (so its body brush sits in the CORPSE
    /// content channel) the player's hull passes straight through it instead of being blocked by it. The
    /// explicit <see cref="Entity.DpHitContentsMask"/> = <c>SOLID|BODY|CORPSE</c> keeps the projectile's OWN
    /// trace hitting bodies and corpses (SOLID_CORPSE alone would drop the CORPSE bit per
    /// SV_GenericHitSuperContentsMask). This is what stops a rocket/grenade colliding with — and detonating on —
    /// its firer (notably the predicting local player on a listen server, whose carrier entity is a DISTINCT
    /// instance from the projectile's server-side <see cref="Entity.Owner"/>, so the owner trace-exception
    /// cannot protect it).
    ///
    /// Deferred vs QC: <c>clipgroup</c> (so a burst of same-owner projectiles don't collide with each other) and
    /// the <c>g_projectiles_interact</c> variants — neither affects the firer-self-hit this fixes.
    /// </summary>
    public static void MakeTrigger(Entity e)
    {
        e.Solid = Solid.Corpse;
        e.DpHitContentsMask = SuperContentsSolid | SuperContentsBody | SuperContentsCorpse;
    }

    /// <summary>
    /// [W1-projectile-net] Make a damageable projectile actually shootable — route incoming RadiusDamage onto its
    /// <see cref="Entity.ProjectileDamage"/> shoot-down callback (QC <c>W_*_Grenade_Damage</c> →
    /// <c>W_PrepareExplosionByDamage</c>). The damage pipeline already dispatches a non-player victim's
    /// <see cref="Entity.GtEventDamage"/> (DamageSystem.EventDamage), so installing this shim there is what
    /// finally lets a player shoot a grenade/rocket/mine/orb/tag/bolt out of the air — the per-weapon
    /// <c>ProjectileDamage</c> delegates are otherwise dead (never invoked except by BreakablehookMutator).
    ///
    /// <para>The weapon must already have set <see cref="Entity.TakeDamage"/> = <see cref="DamageMode.Yes"/>,
    /// seeded <see cref="Entity.Health"/> (the projectile's shoot-down hp) and assigned
    /// <see cref="Entity.ProjectileDamage"/>. Call this AFTER those (and after MUTATOR EditProjectile). The shim:</para>
    /// <list type="number">
    ///   <item>halts on already-dead (hp &lt;= 0) — recursion guard, QC <c>W_*_Damage</c> first line;</item>
    ///   <item>runs the <see cref="CheckProjectileDamage"/> <c>g_projectiles_damage</c> gate (per
    ///         <paramref name="exception"/>) — under the stock <c>g_projectiles_damage -2</c> only an explicit
    ///         exception (electro combo / hagar join / ML mines) passes, exactly like Base;</item>
    ///   <item>subtracts <paramref name="damage"/> from <see cref="Entity.Health"/> (QC
    ///         <c>TakeResource(this, RES_HEALTH, damage)</c>);</item>
    ///   <item>fires <see cref="Entity.ProjectileDamage"/> (the weapon's W_PrepareExplosionByDamage explode/knock
    ///         handler), passing the attacker, when (and only when) hp has reached 0 — a graze that doesn't
    ///         deplete the projectile no longer detonates it.</item>
    /// </list>
    ///
    /// <paramref name="exception"/> is the QC <c>W_CheckProjectileDamage</c> exception value (default −1 = "no
    /// exceptions"; a weapon that is meant to be combo-able regardless of <c>g_projectiles_damage</c> — electro
    /// orb, hagar, ML mine — passes <c>1</c>).
    ///
    /// <para><paramref name="onHit"/> is an OPTIONAL per-hit side effect that runs on EVERY damaging hit while the
    /// projectile is still alive (hp &gt; 0), BEFORE the <c>g_projectiles_damage</c> gate and the hp subtraction —
    /// matching the QC ordering where some <c>W_*_Damage</c> handlers act before the gate/TakeResource (e.g. the
    /// Mine Layer's <c>damageforcescale</c> knock-loose: bounce + .wait re-stick + avelocity spin). It receives
    /// <c>(self, inflictor, attacker, force)</c>. Most projectiles leave this null (they only react at hp&lt;=0 via
    /// <see cref="Entity.ProjectileDamage"/>).</para>
    ///
    /// <para><paramref name="damageScale"/> is an OPTIONAL function that scales the damage dealt to the projectile
    /// health (applied AFTER the gate, BEFORE hp subtraction). Receives <c>(self, attacker, damage)</c> and returns
    /// the scaled damage. Most projectiles leave this null (full damage). Used by Seeker missiles to implement
    /// <c>damage * 0.25</c> self-scaling when the firer shoots their own missile.</para>
    /// </summary>
    /// <para><paramref name="exceptionFn"/> is an OPTIONAL per-hit override of the <paramref name="exception"/>
    /// value. When supplied it is evaluated PER damaging hit with <c>(self, inflictor)</c> and its result is used
    /// for the <see cref="CheckProjectileDamage"/> gate instead of the fixed <paramref name="exception"/>. This
    /// reproduces the QC handlers whose exception depends on the inflictor (e.g. the Mine Layer's
    /// <c>is_from_enemy ? 1 : -1</c> — an enemy may shoot a mine down, but the owner cannot shoot their own mine
    /// under stock <c>g_projectiles_damage -2</c>). Most projectiles leave this null (fixed exception).</para>
    public static void MakeShootable(Entity e, float exception = -1f,
        Action<Entity, Entity?, Entity?, Vector3>? onHit = null,
        Func<Entity, Entity?, float, float>? damageScale = null,
        Func<Entity, Entity?, float>? exceptionFn = null)
    {
        e.GtEventDamage = (self, inflictor, attacker, deathType, damage, _, force) =>
            ShootDown(self, inflictor, attacker, deathType, damage, force,
                exceptionFn?.Invoke(self, inflictor) ?? exception, onHit, damageScale);
    }

    /// <summary>
    /// The installed shoot-down shim (QC <c>W_*_Grenade_Damage</c> generalized). Kept internal so the weapons can
    /// also call the gate directly if they need the bool result. <paramref name="damage"/> is the (already
    /// armor/teamplay-resolved) damage the pipeline computed for this non-player victim.
    /// </summary>
    private static void ShootDown(Entity self, Entity? inflictor, Entity? attacker, string deathType,
        float damage, Vector3 force, float exception,
        Action<Entity, Entity?, Entity?, Vector3>? onHit,
        Func<Entity, Entity?, float, float>? damageScale)
    {
        if (self.Health <= 0f)
            return; // already exploding (recursion guard — QC GetResource(this, RES_HEALTH) <= 0)

        // QC W_*_Damage handlers that act BEFORE the gate/TakeResource (the Mine Layer knock-loose runs on every
        // surviving hit, ahead of W_CheckProjectileDamage). Fire the per-hit side effect here so it reproduces
        // faithfully — the projectile is still alive at this point.
        onHit?.Invoke(self, inflictor, attacker, force);

        if (!CheckProjectileDamage(inflictor?.RealOwner, self.RealOwner, deathType, exception))
            return; // g_projectiles_damage says to halt

        // Apply optional damage scaling (e.g., Seeker missile self-damage × 0.25).
        if (damageScale is not null)
            damage = damageScale(self, attacker, damage);

        self.Health -= damage; // QC TakeResource(this, RES_HEALTH, damage)
        if (self.Health > 0f)
            return; // graze: the projectile survives — do NOT detonate (W_PrepareExplosionByDamage only fires at <=0)

        // QC W_PrepareExplosionByDamage: stop taking damage, then run the weapon's explode handler (the per-weapon
        // ProjectileDamage callback — e.g. Mortar.Explode, Arc.ExplodeBolt, Minelayer.OnMineDamage). The callback
        // owns the actual burst (and a HP-aware callback like Minelayer's knock-loose can still branch on hp).
        self.TakeDamage = DamageMode.No;

        // QC W_PrepareExplosionByDamage owner-reassign: when a CLIENT shot the projectile down and the server isn't
        // running g_projectiles_keep_owner, credit the kill to the shooter (owner = realowner = attacker). Under the
        // stock balance-xonotic.cfg default (g_projectiles_keep_owner 1) this is a no-op; the Nexuiz/overkill/samual/
        // xdf balances set it 0, where the shooter-down kill is then credited to whoever shot it. RealOwner aliases
        // Owner in the port, so assigning Owner is the full QC `owner = realowner = attacker`.
        if (attacker is not null && (attacker.Flags & EntFlags.Client) != 0
            && Api.Cvars.GetFloat("g_projectiles_keep_owner") == 0f)
            self.Owner = attacker;

        // Expose the damaging inflictor to the explode handler (QC W_*_Damage reads `inflictor`). The Electro orb
        // uses it to take Base's combo-vs-secondary branch (electro.qc:484): an electro_orb_chain/electro_bolt
        // inflictor combos, a NULL inflictor (lava/slime contents death) bursts as a plain secondary blast.
        self.DmgInflictor = inflictor;

        // QC W_PrepareExplosionByDamage: "do not explode NOW but in the NEXT FRAME! because recursive calls to
        // RadiusDamage are not allowed" (common.qc:100-103: nextthink = time; setthink(this, explode)). This shim
        // runs INSIDE the damage pipeline (EventDamage, itself reached from a RadiusDamage find-loop), so firing
        // the explode synchronously would re-enter RadiusDamage recursively — exactly what Base defers to avoid.
        // Schedule the per-weapon explode handler on the next think tick instead (overwriting the projectile's
        // own periodic think, like setthink does). The MUTATOR_CALLHOOK(PrepareExplosionByDamage) Base fires here
        // has ZERO subscribers in stock xonotic-data, so it is faithfully omitted (a no-op hook).
        Action<Entity, Entity?>? explode = self.ProjectileDamage;
        if (explode is not null)
        {
            self.Think = s => explode(s, attacker);
            self.NextThink = Api.Clock.Time; // QC nextthink = time → runs on the next sim step
        }
    }

    /// <summary>
    /// Port of <c>W_CheckProjectileDamage</c> (server/weapons/common.qc:45) — the <c>g_projectiles_damage</c>
    /// ladder deciding whether a projectile may take this damage. <paramref name="inflictorOwner"/> is the
    /// damaging projectile's owner (or the attacker), <paramref name="projOwner"/> the shot-at projectile's
    /// owner, <paramref name="exception"/> the per-call override (−1 = none). Mirrors Base exactly:
    /// −2 never, −1 exception-only, 0 contents+exception, 1 self+contents+exception, 2 all (exception overrides).
    /// </summary>
    public static bool CheckProjectileDamage(Entity? inflictorOwner, Entity? projOwner, string deathType, float exception)
    {
        bool isFromContents = deathType == DeathTypes.Lava || deathType == DeathTypes.Slime;
        bool isFromOwner = ReferenceEquals(inflictorOwner, projOwner) && projOwner is not null;
        bool isFromException = exception != -1f;

        float mode = Api.Cvars.GetFloat("g_projectiles_damage");

        if (mode <= -2f)
            return false; // no damage to projectiles at all
        if (mode == -1f)
            return isFromException;                       // exception-only
        if (mode == 0f)
            return isFromException || isFromContents;     // contents + exception
        if (mode == 1f)
            return isFromException || isFromContents || isFromOwner; // self + contents + exception
        // mode == 2 (or any other value): allow all damage (exception is moot — already true).
        return true;
    }
}
