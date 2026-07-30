// Port of qcsrc/server/weapons/throwing.qc (W_ThrowNewWeapon / W_IsWeaponThrowable / W_ThrowWeapon /
// SpawnThrownWeapon).
//
// The loot machinery is the T35 item path: StartItem.SpawnLoot handles MOVETYPE_TOSS, the
// g_items_dropped_lifetime despawn, the 0.5s anti-instant-pick shield and the NODROP-brush kill; the
// WeaponPickup def's ItemInit seeds the item's weapon set + DEFAULT pickup ammo, which the doreduce tail
// below then clamps to what the thrower actually had (the QC anti-duplication order).

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Weapon tossing — the C# successor to <c>server/weapons/throwing.qc</c>: impulse-17 drops
/// (<see cref="ThrowWeapon"/>), death drops (<see cref="SpawnThrownWeapon"/>), and the shared loot spawner
/// (<see cref="ThrowNewWeapon"/>) the Piñata mutator also uses.
/// </summary>
public static class WeaponThrowing
{
    private static float Now => Api.Services is not null ? Api.Clock.Time : 0f;

    /// <summary>QC <c>game_starttime</c> via the same host seam StartItem uses (unset = 0, headless default).</summary>
    private static float GameStartTime => StartItem.GameStartTimeProvider?.Invoke() ?? 0f;

    // =================================================================================================
    //  W_ThrowNewWeapon (throwing.qc:22-114)
    // =================================================================================================

    /// <summary>
    /// Port of <c>W_ThrowNewWeapon(own, wpn, doreduce, org, velo, weaponentity)</c>: spawn weapon
    /// <paramref name="wpn"/> as a tossed loot pickup at <paramref name="org"/> with velocity
    /// <paramref name="velo"/>. Returns the amount of ammo carried, <c>-1</c> on spawn failure
    /// (startitem_failed — NODROP brush), or <c>0</c> for "no ammo count" (ammo-less weapon / death drop).
    /// </summary>
    public static float ThrowNewWeapon(Entity own, Weapon wpn, bool doreduce, Vector3 org, Vector3 velo,
        WeaponSlot slot)
    {
        ResourceType ammotype = wpn.AmmoType;

        Entity wep = Api.Entities.Spawn();
        // QC: ITEM_SET_LOOT (done by SpawnLoot's isLoot path); setorigin; velocity; owner = enemy = own;
        // wep.flags |= FL_TOSSED (throwing.qc:32) is a dead marker — FL_TOSSED is set here and read NOWHERE
        // else in the Base codebase, so it carries no behavior; intentionally unmodeled.
        Api.Entities.SetOrigin(wep, org);
        wep.Origin = org;
        wep.Velocity = velo;
        wep.Owner = wep.Enemy = own; // SpawnLoot clears Owner ("anyone can pick it up"); the 0.5s shield covers the thrower
        // (QC W_DropEvent wr_drop — Mine Layer recounts placed mines; no port weapon implements wr_drop yet.)

        // QC: wep.colormap = own.colormap (throwing.qc:33); StartItem then leaves loot's colormap untouched
        // (items.qc:1209 only greys non-loot to 1024), so the thrown gun inherits the thrower's team/player tint.
        // The csqcmodel derives glowmod from the colormap nibble client-side (the "// wep.glowmod will be set in
        // weapon_defaultspawnfunc" QC note), so writing the authoritative colormap is the load-bearing decision —
        // the established render seam (Entity.ColorMapOverride + RENDER_COLORMAPPED), same as MonsterAI/MapModels.
        wep.ColorMapOverride = ThrowerColormap(own);

        // ---- the superweapon time split (throwing.qc:39-71) ----
        if (wpn.IsSuperWeapon)
        {
            wep.ItemIsExpiring = true; // ITEM_SET_EXPIRING — set BEFORE SpawnLoot so its expiring nextthink applies
            if ((own.Items & (int)ItemFlag.UnlimitedSuperweapons) != 0)
            {
                wep.SuperweaponsFinished = Now + ItemPickupRules.CvarOr("g_balance_superweapons_time", 30f);
            }
            else if (StatusEffectsCatalog.Superweapon is { } swDef)
            {
                // QC counts from 1 then adds one per superweapon in the owner's CURRENT set (the W_ThrowWeapon
                // path already removed the thrown weapon's bit; the death-drop path has not — both faithful).
                int superweapons = 1;
                foreach (Weapon it in own.OwnedWeaponSet.Weapons())
                    if (it.IsSuperWeapon)
                        ++superweapons;

                float effectExpire = StatusEffectExpire(own, swDef);
                if (superweapons == 1)
                {
                    // the last one: the item inherits the WHOLE remaining superweapon window (absolute time —
                    // ItemTouch subtracts `time` at pickup, matching QC's expiring-loot convention).
                    wep.SuperweaponsFinished = effectExpire;
                    StatusEffectsCatalog.Remove(own, swDef);
                }
                else
                {
                    float timeleft = effectExpire - Now;
                    float weptimeleft = timeleft / superweapons;
                    wep.SuperweaponsFinished = Now + weptimeleft;
                    ReduceStatusEffectTime(own, swDef, weptimeleft);
                }
            }
        }

        // ---- weapon_defaultspawnfunc (throwing.qc:73): the loot StartItem path ----
        Entity? spawned = StartItem.SpawnLoot(wep, ItemSpawnFuncs.PickupFor(wpn));
        if (spawned is null)
            return -1f; // startitem_failed

        wep.PickupAnyway = 1; // "these are ALWAYS pickable" (throwing.qc:77)

        // ---- the ammo tail (throwing.qc:80-113) ----
        if (ammotype == ResourceType.None)
            return 0f;

        if (doreduce && WeaponStay == 2)
        {
            // give the loaded clip back to the player + schedule the (now-gone) weapon for reloading...
            GiveClipBack(own, wpn, slot);
            // ...and the thrown gun carries NO ammo (anti infinite-ammo abuse).
            wep.SetResourceExplicit(ammotype, 0f);
        }
        else if (doreduce)
        {
            GiveClipBack(own, wpn, slot);

            float ownerAmmo = own.GetResource(ammotype);
            float thisAmmo = MathF.Min(ownerAmmo, wep.GetResource(ammotype)); // clamp the ItemInit-seeded default
            wep.SetResourceExplicit(ammotype, thisAmmo);
            own.SetResource(ammotype, ownerAmmo - thisAmmo);
            return thisAmmo;
        }
        return 0f;
    }

    // QC throwing.qc:87-92/97-103: if the weapon's persistent clip is loaded, give the load back to the
    // player and mark the magazine "needs reload" (-1) so a re-pickup starts empty.
    private static void GiveClipBack(Entity own, Weapon wpn, WeaponSlot slot)
    {
        WeaponSlotState st = own.WeaponState(slot);
        int load = Weapon.GetWeaponLoad(st, wpn.RegistryId);
        if (load > 0)
        {
            own.GiveResource(wpn.AmmoType, load);
            Weapon.SetWeaponLoad(st, wpn.RegistryId, -1); // schedule the weapon for reloading
        }
    }

    // =================================================================================================
    //  W_IsWeaponThrowable (throwing.qc:116-126)
    // =================================================================================================

    /// <summary>
    /// Port of <c>W_IsWeaponThrowable(this, w)</c>: mutator veto, the g_pickup_items==0 / g_weaponarena
    /// gates, the Null weapon, and the per-weapon <c>g_balance_&lt;wep&gt;_weaponthrowable</c> balance cvar
    /// (stock 1 for everything except blaster / fireball / okhmg / okrpc — bal-wep-xonotic.cfg).
    /// </summary>
    public static bool IsWeaponThrowable(Entity own, Weapon? w)
    {
        if (w is null)
            return false; // WEP_Null

        // QC MUTATOR_CALLHOOK(ForbidDropCurrentWeapon, this, w). The port's single ForbidThrowCurrentWeapon
        // chain stands in for both QC hooks (its 3 subscribers — instagib/overkill/meleeonly — forbid by
        // player, not per-weapon; the per-weapon arg is a documented gap).
        var forbid = new MutatorHooks.ForbidThrowCurrentWeaponArgs(own);
        if (MutatorHooks.ForbidThrowCurrentWeapon.Call(ref forbid))
            return false;

        if (PickupItemsCvar() == 0 || WeaponArena())
            return false;

        return ItemPickupRules.CvarBoolOr($"g_balance_{w.NetName}_weaponthrowable", WeaponThrowableStock(w));
    }

    // bal-wep-xonotic.cfg stock weaponthrowable values (1 for all except these four).
    private static bool WeaponThrowableStock(Weapon w) => w.NetName switch
    {
        "blaster" => false,   // bal-wep-xonotic.cfg:43
        "fireball" => false,  // :671
        "okhmg" => false,     // :808
        "okrpc" => false,     // :895
        _ => true,
    };

    // =================================================================================================
    //  W_ThrowWeapon (throwing.qc:129-152)
    // =================================================================================================

    /// <summary>
    /// Port of <c>W_ThrowWeapon(this, weaponentity, velo, delta, doreduce)</c>: toss the actor's CURRENT
    /// weapon — the impulse-17 path. Gate chain in QC order, then remove the weapon (dual-rep), switch to
    /// the best remaining weapon, spawn the loot, and notify ITEM_WEAPON_DROP.
    /// </summary>
    public static void ThrowWeapon(Entity actor, WeaponSlot slot, Vector3 velo, Vector3 delta, bool doreduce)
    {
        Weapon? w = Inventory.CurrentWeapon(actor);
        if (w is null)
            return; // just in case
        if (Now < GameStartTime)
            return;
        var forbid = new MutatorHooks.ForbidThrowCurrentWeaponArgs(actor);
        if (MutatorHooks.ForbidThrowCurrentWeapon.Call(ref forbid))
            return;
        if (!ItemPickupRules.CvarBoolOr("g_weapon_throwable", true)   // xonotic-server.cfg:206 default 1
            || actor.WeaponState(slot).State != WeaponFireState.Ready // this.(weaponentity).state != WS_READY
            || !IsWeaponThrowable(actor, w))
            return;

        if (!actor.OwnedWeaponSet.Has(w))
            return; // !(STAT(WEAPONS, this) & set)

        // STAT(WEAPONS, this) &= ~set, BEFORE the switch/spawn (throwing.qc:144) — dual-rep removal.
        // Inventory.RemoveWeapon also runs the W_SwitchWeapon_Force(w_getbestweapon) auto-switch (:146).
        Inventory.RemoveWeapon(actor, w);
        if (actor is Player p)
            p.OwnedWeapons.Remove(w.NetName);

        float a = ThrowNewWeapon(actor, w, doreduce, actor.Origin + delta, velo, slot);
        if (a < 0f)
            return; // startitem_failed: silent loss
        NotificationSystem.Send(NotifBroadcast.One, actor, MsgType.Multi, "ITEM_WEAPON_DROP", w.RegistryId, a);
    }

    // =================================================================================================
    //  SpawnThrownWeapon (throwing.qc:154-161) — the death drop
    // =================================================================================================

    /// <summary>
    /// Port of <c>SpawnThrownWeapon(this, org, wep, weaponentity)</c>: on death, drop the held weapon
    /// (owned + throwable gate) with the QC toss impulse <c>randomvec()*125 + '0 0 200'</c>. doreduce is
    /// false: the loot keeps the ItemInit-seeded default pickup ammo.
    /// </summary>
    public static void SpawnThrownWeapon(Entity victim, Vector3 org, Weapon? wep, WeaponSlot slot)
    {
        if (wep is null)
            return;
        if (victim.OwnedWeaponSet.Has(wep) && IsWeaponThrowable(victim, wep))
            ThrowNewWeapon(victim, wep, doreduce: false, org, Prandom.Vec() * 125f + new Vector3(0f, 0f, 200f), slot);
    }

    // =================================================================================================
    //  helpers
    // =================================================================================================

    /// <summary>RENDER_COLORMAPPED (BIT(10)) — the render flag the csqcmodel reads to apply a colormap tint
    /// (same constant as MonsterAI/MapModels).</summary>
    private const int RenderColormapped = 1 << 10;

    /// <summary>
    /// QC <c>own.colormap</c> as inherited by the thrown loot (throwing.qc:33). For a player the engine maps
    /// <c>.colormap</c> through <c>.clientcolors</c>, which the csqcmodel resolves to <c>1024 + (shirt&lt;&lt;4) + pants</c>
    /// (the player's packed shirt/pants, team-forced in teamplay). The port resolves it directly here from
    /// <see cref="Player.ClientColors"/> (packed <c>16*shirt + pants</c>). A non-player thrower (e.g. a monster
    /// death drop) inherits its own already-set colormap via <see cref="Entity.ColorMapOverride"/>.
    /// </summary>
    private static int ThrowerColormap(Entity own)
    {
        if (own is Player p)
        {
            int pants = p.ClientColors & 0x0F;
            int shirt = (p.ClientColors >> 4) & 0x0F;
            return (1024 + (shirt << 4) + pants) | RenderColormapped;
        }
        // already-colormapped thrower (monster/turret): re-use its authoritative value, or none.
        return own.ColorMapOverride;
    }

    /// <summary>QC <c>StatusEffects_gettime(def, e)</c>: the ABSOLUTE expire time, 0 when not active.</summary>
    private static float StatusEffectExpire(Entity e, StatusEffectDef def)
    {
        foreach (var s in e.StatusEffects)
            if (s.DefId == def.RegistryId)
                return s.ExpireTime;
        return 0f;
    }

    /// <summary>QC <c>statuseffect_time[id] -= weptimeleft</c> (throwing.qc:66): shave the player's share.</summary>
    private static void ReduceStatusEffectTime(Entity e, StatusEffectDef def, float delta)
    {
        for (int i = 0; i < e.StatusEffects.Count; i++)
        {
            if (e.StatusEffects[i].DefId != def.RegistryId) continue;
            var cur = e.StatusEffects[i];
            cur.ExpireTime -= delta;
            e.StatusEffects[i] = cur;
            return;
        }
    }

    /// <summary>QC <c>g_weapon_stay</c> (0 off / 1 ghost / 2 stay-ammo) — same read as ItemPickupRules.</summary>
    private static int WeaponStay => Api.Services is null ? 0 : (int)Api.Cvars.GetFloat("g_weapon_stay");

    // QC autocvar_g_pickup_items default -1 (gametype default — truthy); an UNSET cvar reads as -1 (proceed),
    // exactly like StartItem.PickupItemsCvar.
    private static int PickupItemsCvar()
    {
        if (Api.Services is null) return -1;
        string s = Api.Cvars.GetString("g_pickup_items");
        if (string.IsNullOrEmpty(s)) return -1;
        return (int)Api.Cvars.GetFloat("g_pickup_items");
    }

    private static bool WeaponArena()
        => Api.Services is not null && Api.Cvars.GetFloat("g_weaponarena") != 0f;
}
