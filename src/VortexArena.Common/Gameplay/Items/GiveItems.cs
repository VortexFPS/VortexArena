// Port of GiveItems (qcsrc/server/items/items.qc:1435) + its op helpers GiveResourceValue/GiveBit/GiveWeapon/
// GiveStatusEffect (items.qc:1295-1433). The shared, op-aware give-token grammar behind the cheat `give`
// command, target_items, and target_give. Mirrors QC's operator semantics:
//   val defaults to 999, op to OP_SET; a numeric token sets val (and `continue`s, NOT resetting); the
//   no/max/min/plus/minus prefixes set op (and `continue`); a NAME token applies (val,op) then RESETS val=999,
//   op=OP_SET. The ALL/all/allweapons/allammo aggregates use C FALLTHROUGH (ALL runs all the lower tiers too) —
//   reproduced here with explicit goto-case so `give all` cascades into allweapons + allammo.
//
// Weapon ownership is DUAL-REP: GiveWeapon writes BOTH Entity.OwnedWeaponSet (via Inventory) AND
// Player.OwnedWeapons (the NetName set), per the port contract.

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>QC the give operators (items.qc): SET / MIN / MAX / PLUS / MINUS.</summary>
public enum GiveOp { Set, Min, Max, Plus, Minus }

/// <summary>
/// The shared op-aware item-give grammar — QC <c>GiveItems</c>. <see cref="Apply"/> walks a token list and
/// applies resources / weapons / held-item bits / powerup status effects to an entity with the full operator
/// semantics. Used by the cheat <c>give</c>, <c>target_items</c>, and <c>target_give</c> so all three share one
/// faithful implementation. Returns the count of changes (QC <c>got</c>).
/// </summary>
public static class GiveItems
{
    private static float Now => Api.Services != null ? Api.Clock.Time : 0f;

    /// <summary>Apply the whole token list to <paramref name="e"/> (QC GiveItems over the full argv).</summary>
    public static int Apply(Entity e, string[] tokens) => Apply(e, tokens, 0, tokens.Length);

    /// <summary>
    /// QC <c>GiveItems(e, beginarg, endarg)</c>: walk <paramref name="tokens"/>[<paramref name="begin"/>..
    /// <paramref name="end"/>) with the operator grammar and apply each name token. Returns the change count.
    /// Powerup default-time + the superweapon-default-time tail are applied as in QC.
    /// </summary>
    public static int Apply(Entity e, string[] tokens, int begin, int end)
    {
        float val = 999f;
        GiveOp op = GiveOp.Set;
        int got = 0;

        // QC PREGIVE_WEAPONS: snapshot the owned weapons so the superweapon-default-time tail can detect a
        // freshly-acquired superweapon (had none before, has one now).
        WepSet saveWeapons = e.OwnedWeaponSet;

        for (int i = begin; i < end && i < tokens.Length; i++)
        {
            string cmd = tokens[i];
            if (string.IsNullOrEmpty(cmd)) continue;

            // QC: a numeric token (incl. "0") sets val and continues (does NOT reset op or get re-reset).
            if (cmd == "0" || float.TryParse(cmd, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                val = float.Parse(cmd, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture);
                continue;
            }

            switch (cmd)
            {
                // ---- operator prefixes (continue — do NOT reset val/op) ----
                case "no":   op = GiveOp.Max; val = 0f; continue;
                case "max":  op = GiveOp.Max; continue;
                case "min":  op = GiveOp.Min; continue;
                case "plus": op = GiveOp.Plus; continue;
                case "minus": op = GiveOp.Minus; continue;

                // ---- aggregate cascade (QC C switch FALLTHROUGH: ALL -> all -> allweapons -> allammo) ----
                case "ALL":
                    got += GiveBit(e, ItemFlag.FuelRegen, op, val);
                    got += GiveStatusAll(e, op, val); // all powerup status effects
                    got += GiveBit(e, ItemFlag.UnlimitedAmmo | ItemFlag.UnlimitedSuperweapons, op, val);
                    goto case "all";
                case "all":
                    got += GiveBit(e, ItemFlag.Jetpack, op, val);
                    got += GiveResourceValue(e, ResourceType.Health, op, val);
                    got += GiveResourceValue(e, ResourceType.Armor, op, val);
                    goto case "allweapons";
                case "allweapons":
                    foreach (Weapon w in Weapons.All)
                        if (!IsBlockedOrHidden(w))
                            got += GiveWeapon(e, w, op, val);
                    goto case "allammo";
                case "allammo":
                    got += GiveResourceValue(e, ResourceType.Cells, op, val);
                    got += GiveResourceValue(e, ResourceType.Shells, op, val);
                    got += GiveResourceValue(e, ResourceType.Bullets, op, val);
                    got += GiveResourceValue(e, ResourceType.Rockets, op, val);
                    got += GiveResourceValue(e, ResourceType.Fuel, op, val);
                    break;

                // ---- held-item bits ----
                case "unlimited_ammo":
                    got += GiveBit(e, ItemFlag.UnlimitedAmmo | ItemFlag.UnlimitedSuperweapons, op, val); break;
                case "unlimited_weapon_ammo":
                    got += GiveBit(e, ItemFlag.UnlimitedAmmo, op, val); break;
                case "unlimited_superweapons":
                    got += GiveBit(e, ItemFlag.UnlimitedSuperweapons, op, val); break;
                case "jetpack":
                    got += GiveBit(e, ItemFlag.Jetpack, op, val); break;
                case "fuel_regen":
                    got += GiveBit(e, ItemFlag.FuelRegen, op, val); break;

                // ---- powerup status effects ----
                case "strength":
                    got += GiveStatusEffect(e, "strength", op, val); break;
                case "invincible":
                case "shield":
                    got += GiveStatusEffect(e, "shield", op, val); break;
                case "speed":
                    got += GiveStatusEffect(e, "speed", op, val); break;
                case "invisibility":
                    got += GiveStatusEffect(e, "invisibility", op, val); break;
                case "superweapons":
                    got += GiveStatusEffect(e, "superweapon", op, val); break;

                // ---- resources ----
                case "cells":   got += GiveResourceValue(e, ResourceType.Cells, op, val); break;
                case "shells":  got += GiveResourceValue(e, ResourceType.Shells, op, val); break;
                case "nails":
                case "bullets": got += GiveResourceValue(e, ResourceType.Bullets, op, val); break;
                case "rockets": got += GiveResourceValue(e, ResourceType.Rockets, op, val); break;
                case "health":  got += GiveResourceValue(e, ResourceType.Health, op, val); break;
                case "armor":   got += GiveResourceValue(e, ResourceType.Armor, op, val); break;
                case "fuel":    got += GiveResourceValue(e, ResourceType.Fuel, op, val); break;

                default:
                    // QC default: a buff name, else a weapon netname (or its deprecated alias). The buff match
                    // is `buff_Available(it) && Buff_CompatName(cmd) == it.netname` — Buff_CompatName maps the
                    // Q3/WOP deprecated aliases (doubler->inferno, scout->bash, ...) to the canonical buff name
                    // first, so `give doubler` etc. resolve. The port's buff netnames carry a "buff_" prefix.
                    StatusEffectDef? buff = StatusEffectsCatalog.ByName("buff_" + BuffCompatName(cmd));
                    if (buff is not null && buff.IsBuff)
                    {
                        got += GiveBuff(e, buff, op, val);
                    }
                    else
                    {
                        Weapon? w = Weapons.ByName(cmd);
                        if (w is not null)
                            got += GiveWeapon(e, w, op, val);
                    }
                    break;
            }

            // QC: reset after every non-continuing (NAME / aggregate) token.
            val = 999f;
            op = GiveOp.Set;
        }

        // QC superweapon-default-time tail (items.qc:1617): if the entity just acquired a superweapon (had none
        // in the snapshot, has one now) and the superweapon effect isn't active, grant the default time.
        var superDef = StatusEffectsCatalog.ByName("superweapon");
        if (superDef is not null && !StatusEffectsCatalog.Has(e, superDef))
        {
            bool hadSuper = OwnsSuperWeapon(saveWeapons);
            bool hasSuper = OwnsSuperWeapon(e.OwnedWeaponSet);
            if (!hadSuper && hasSuper && (Api.Services is null || Api.Cvars.GetFloat("g_weaponarena") == 0f))
                StatusEffectsCatalog.Apply(e, superDef, ItemPickupRules.CvarOr("g_balance_superweapons_time", 30f));
        }

        return got;
    }

    // =====================================================================================
    //  op helpers (items.qc:1295-1433)
    // =====================================================================================

    // QC GiveResourceValue (items.qc:1385): apply the op to the resource and SetResourceExplicit.
    private static int GiveResourceValue(Entity e, ResourceType res, GiveOp op, float val)
    {
        float v0 = e.GetResource(res);
        float newVal = op switch
        {
            GiveOp.Set => val,
            GiveOp.Min => System.MathF.Max(v0, val),
            GiveOp.Max => System.MathF.Min(v0, val),
            GiveOp.Plus => v0 + val,
            GiveOp.Minus => v0 - val,
            _ => v0,
        };
        return e.SetResourceExplicit(res, newVal) ? 1 : 0;
    }

    // QC GiveBit (items.qc, the items-flag analogue of GiveWeapon): set/clear the flag by op + val.
    private static int GiveBit(Entity e, ItemFlag bits, GiveOp op, float val)
    {
        int before = e.Items;
        int mask = (int)bits;
        switch (op)
        {
            case GiveOp.Set:
                if (val > 0f) e.Items |= mask; else e.Items &= ~mask;
                break;
            case GiveOp.Min:
            case GiveOp.Plus:
                if (val > 0f) e.Items |= mask;
                break;
            case GiveOp.Max:
                if (val <= 0f) e.Items &= ~mask;
                break;
            case GiveOp.Minus:
                if (val > 0f) e.Items &= ~mask;
                break;
        }
        return e.Items != before ? 1 : 0;
    }

    // QC GiveWeapon (items.qc:1295): set/clear the weapon by op + val, exactly mirroring QC's switch:
    //   SET: val>0 -> add, else remove. MIN/PLUS: val>0 -> add (never remove). MAX: val<=0 -> remove (never
    //   add). MINUS: val>0 -> remove. DUAL-REP: both the WepSet (Inventory) AND Player.OwnedWeapons (NetName).
    private static int GiveWeapon(Entity e, Weapon w, GiveOp op, float val)
    {
        bool had = e.OwnedWeaponSet.Has(w);
        bool add = false, remove = false;
        switch (op)
        {
            case GiveOp.Set:
                if (val > 0f) add = true; else remove = true;
                break;
            case GiveOp.Min:
            case GiveOp.Plus:
                if (val > 0f) add = true;
                break;
            case GiveOp.Max:
                if (val <= 0f) remove = true;
                break;
            case GiveOp.Minus:
                if (val > 0f) remove = true;
                break;
        }

        if (add && !had)
        {
            Inventory.GiveWeapon(e, w);                       // Entity.OwnedWeaponSet
            if (e is Player p) p.OwnedWeapons.Add(w.NetName); // Player.OwnedWeapons (dual-rep)
            return 1;
        }
        if (remove && had)
        {
            Inventory.RemoveWeapon(e, w);
            if (e is Player p) p.OwnedWeapons.Remove(w.NetName);
            return 1;
        }
        return 0;
    }

    // QC GiveStatusEffect (items.qc:1402): set/extend/reduce the effect's remaining time by op + val.
    private static int GiveStatusEffect(Entity e, string effectName, GiveOp op, float val)
    {
        StatusEffectDef? def = StatusEffectsCatalog.ByName(effectName);
        if (def is null) return 0;

        bool had = StatusEffectsCatalog.Has(e, def);
        float remaining = had ? ExistingRemaining(e, def) : 0f;       // time-from-now
        // QC works in absolute time (time + remaining); compute the new absolute, then back to a duration.
        float curAbs = Now + remaining;
        float newAbs = op switch
        {
            GiveOp.Set => Now + val,
            GiveOp.Min => System.MathF.Max(curAbs, Now + val),
            GiveOp.Max => System.MathF.Min(curAbs, Now + val),
            GiveOp.Plus => curAbs + val,
            GiveOp.Minus => curAbs - val,
            _ => curAbs,
        };
        if (newAbs <= Now)
        {
            if (had) StatusEffectsCatalog.Remove(e, def);
        }
        else
        {
            StatusEffectsCatalog.Apply(e, def, newAbs - Now);
        }
        bool have = StatusEffectsCatalog.Has(e, def);
        return had != have ? 1 : 0;
    }

    // QC GiveBuff (items.qc:1326): like GiveStatusEffect but clears other buffs first when applying.
    private static int GiveBuff(Entity e, StatusEffectDef buff, GiveOp op, float val)
    {
        bool had = StatusEffectsCatalog.Has(e, buff);
        float remaining = had ? ExistingRemaining(e, buff) : 0f;
        float curAbs = Now + remaining;
        float newAbs = op switch
        {
            GiveOp.Set => Now + val,
            GiveOp.Min => System.MathF.Max(curAbs, Now + val),
            GiveOp.Max => System.MathF.Min(curAbs, Now + val),
            GiveOp.Plus => curAbs + val,
            GiveOp.Minus => curAbs - val,
            _ => curAbs,
        };
        if (newAbs <= Now)
        {
            if (had) StatusEffectsCatalog.Remove(e, buff);
        }
        else
        {
            // QC buff_RemoveAll first (only one buff at a time), then apply.
            foreach (var d in StatusEffectsCatalog.All)
                if (d.IsBuff && StatusEffectsCatalog.Has(e, d))
                    StatusEffectsCatalog.Remove(e, d);
            StatusEffectsCatalog.Apply(e, buff, newAbs - Now);
        }
        bool have = StatusEffectsCatalog.Has(e, buff);
        return had != have ? 1 : 0;
    }

    private static int GiveStatusAll(Entity e, GiveOp op, float val)
    {
        // QC ALL: FOREACH(StatusEffects, instanceOfPowerupStatusEffect) — strength/shield/speed/invisibility.
        int got = 0;
        got += GiveStatusEffect(e, "strength", op, val);
        got += GiveStatusEffect(e, "shield", op, val);
        got += GiveStatusEffect(e, "speed", op, val);
        got += GiveStatusEffect(e, "invisibility", op, val);
        return got;
    }

    private static float ExistingRemaining(Entity e, StatusEffectDef def)
    {
        foreach (var s in e.StatusEffects)
            if (s.DefId == def.RegistryId) return System.MathF.Max(0f, s.ExpireTime - Now);
        return 0f;
    }

    private static bool OwnsSuperWeapon(WepSet set)
    {
        foreach (var w in set.Weapons())
            if (w.IsSuperWeapon) return true;
        return false;
    }

    // QC allweapons gate: skip WEP_FLAG_MUTATORBLOCKED | WEP_FLAG_HIDDEN weapons.
    private static bool IsBlockedOrHidden(Weapon w)
        => (w.SpawnFlags & (WeaponFlags.MutatorBlocked | WeaponFlags.Hidden)) != 0;

    // Port of Buff_CompatName (qcsrc/common/mutators/mutator/buffs/buffs.qc:4): map the Q3TA/Q3A/WOP deprecated
    // buff aliases to the canonical buff netname so `give doubler`/`give scout`/etc. resolve. Returns the input
    // unchanged for canonical names. (The port's catalog stores buffs with a "buff_" prefix; this returns the
    // bare canonical name to be prefixed by the caller.)
    private static string BuffCompatName(string buffName) => buffName switch
    {
        "ammoregen" => "ammo",            // Q3TA ammoregen
        "doubler" => "inferno",           // Q3TA doubler
        "scout" => "bash",                // Q3TA scout
        "guard" => "resistance",          // Q3TA guard
        "revival" or "regen" => "medic",  // WOP revival, Q3A regen
        "jumper" => "jump",               // WOP jumper
        "invulnerability" => "vampire",   // Q3TA invulnerability
        "kamikaze" => "vengeance",        // Q3TA kamikaze
        "teleporter" => "swapper",        // Q3A personal teleporter
        _ => buffName,
    };
}
