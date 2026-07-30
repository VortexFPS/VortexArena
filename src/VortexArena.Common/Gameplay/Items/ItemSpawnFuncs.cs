// Port of the item_*/weapon_* spawnfuncs: the SPAWNFUNC_ITEM(classname, ITEM_X) table across
// common/items/item/*.qh + the powerup .qh files + server/items/spawning.qc (the compat aliases), and
// weapon_defaultspawnfunc (server/weapons/spawning.qc:29) for the weapon_* classnames.
//
// Each spawnfunc resolves a classname to a Pickup def and calls StartItem.Spawn (mirroring QC's
// SPAWNFUNC_BODY: `if (item && Item_IsDefinitionAllowed(item)) StartItem(this, item); else delete`). Weapon
// items wrap the Weapon registry entry in a synthetic WeaponPickup (QC wpn.m_pickup) that seeds the world
// item's weapon set + pickup ammo. Registered into SpawnFuncs by MapObjectsRegistry via Register().

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Installs the item_*/weapon_* classname spawnfuncs into <see cref="SpawnFuncs"/> (QC SPAWNFUNC_ITEM +
/// weapon_defaultspawnfunc). <see cref="Register"/> is called once from <see cref="MapObjectsRegistry"/>.
/// </summary>
public static class ItemSpawnFuncs
{
    // weapon-pickup defs are synthesized lazily (one per Weapon, keyed by NetName) so they're stable singletons.
    private static readonly Dictionary<string, WeaponPickup> _weaponPickups = new(StringComparer.Ordinal);

    // the canonical classname for each item NetName (QC m_canonical_spawnfunc), used by StartItem to set
    // this.classname. Built from the same table Register() installs.
    private static readonly Dictionary<string, string> _netNameToClassName = new(StringComparer.Ordinal);

    /// <summary>
    /// QC the SPAWNFUNC_ITEM table + the compat aliases + weapon_defaultspawnfunc: register every item_* /
    /// weapon_* classname into <see cref="SpawnFuncs"/>. Idempotent (re-registering overwrites). Resolving a
    /// def by NetName from <see cref="Items"/> means the [Item]-registered pickups (health/armor/
    /// ammo/powerups) are picked up automatically; only the classname→NetName mapping + the compat aliases +
    /// the weapon path live here.
    /// </summary>
    public static void Register()
    {
        // Clear the per-weapon pickup cache + the canonical-name map: a re-Register follows a GameRegistries
        // re-Bootstrap (new Weapon/Pickup instances), so stale wrappers/ids must be dropped (test isolation).
        _weaponPickups.Clear();
        _netNameToClassName.Clear();

        // ---- resource + powerup items: classname -> the [Item] Pickup NetName (QC SPAWNFUNC_ITEM) ----
        // (the canonical spawnfunc classname for each def is the FIRST entry that maps to it.)
        RegisterItem("item_health_small", "health_small");
        RegisterItem("item_health_medium", "health_medium");
        RegisterItem("item_health_big", "health_big");
        RegisterItem("item_health_mega", "health_mega");

        RegisterItem("item_armor_small", "armor_small");
        RegisterItem("item_armor_medium", "armor_medium");
        RegisterItem("item_armor_big", "armor_big");
        RegisterItem("item_armor_mega", "armor_mega");

        RegisterItem("item_shells", "shells");
        RegisterItem("item_bullets", "bullets");
        RegisterItem("item_rockets", "rockets");
        RegisterItem("item_cells", "cells");
        RegisterItem("item_fuel", "fuel");

        RegisterItem("item_strength", "strength");
        RegisterItem("item_shield", "invincible");        // ShieldItem.NetName == "invincible"
        RegisterItem("item_speed", "speed");
        RegisterItem("item_invisibility", "invisibility");
        RegisterItem("item_jetpack", "jetpack");
        RegisterItem("item_fuel_regen", "fuel_regen");

        // ---- instagib economy items (items.qh SPAWNFUNC_ITEM + alias) ----
        // VaporizerCells (item_vaporizer_cells, alias item_minst_cells) + ExtraLife (item_extralife).
        // These are mutator-economy items that only appear during instagib matches (spawned by the random-powerup
        // replacement deck or Devastator/Vortex replacement). They live here so the map parser resolves the
        // classname even on non-instagib maps (FilterItemDefinition removes them when instagib is off).
        RegisterItem("item_vaporizer_cells", "vaporizer_cells"); // QC SPAWNFUNC_ITEM(item_vaporizer_cells, ITEM_VaporizerCells)
        AliasItem("item_minst_cells", "vaporizer_cells");        // QC SPAWNFUNC_ITEM(item_minst_cells, ITEM_VaporizerCells)
        RegisterItem("item_extralife", "extralife");             // QC SPAWNFUNC_ITEM(item_extralife, ITEM_ExtraLife)

        // ---- alias spawnfuncs that share a def (QC SPAWNFUNC_ITEM aliases in the powerup .qh) ----
        AliasItem("item_invincible", "invincible");       // -> ITEM_Shield
        AliasItem("item_buff_speed", "speed");            // -> ITEM_Speed (a powerup, not a buff)
        AliasItem("item_buff_invisibility", "invisibility"); // -> ITEM_Invisibility (a powerup, not a buff)

        // ---- map buff items (QC buffs.qh BUFF_SPAWNFUNCS / BUFF_SPAWNFUNC_Q3COMPAT) ----
        // item_buff_<type> (+ _team1.._team4 per-team variants) and item_buff_random spawn a live buff pickup
        // carrying that type (null = randomize). The Q3/QL/WOP holdable/item classnames map via Buff_CompatName.
        RegisterBuffSpawnFuncs();

        // ---- compatibility spawn functions (server/items/spawning.qc:99-105) ----
        // QC SPAWNFUNC_ITEM(item_armor1, autocvar_sv_mapformat_is_quake3 ? ITEM_ArmorSmall : ITEM_ArmorMedium)
        // (spawning.qc:99): Quake green armor is a Xonotic armor SHARD (small) on a Q3-format map, a medium armor
        // otherwise. Resolved at SPAWN time (like the QC per-spawn macro body) so the live sv_mapformat_is_quake3
        // cvar (default true) picks the right size — defaulting to ArmorSmall on the stock Q3 map format.
        SpawnFuncs.Register("item_armor1",
            e => ItemSpawn(e, CompatRemaps.IsMapformatQuake3() ? "armor_small" : "armor_medium"));
        AliasItem("item_armor25", "armor_mega");          // Nexuiz Mega Armor
        AliasItem("item_armor_large", "armor_mega");
        AliasItem("item_health1", "health_small");
        AliasItem("item_health25", "health_medium");
        AliasItem("item_health100", "health_mega");
        AliasItem("item_health_large", "health_big");

        // ---- Q3/QL/CPMA/Q1/Q2/WoP compat ITEM remaps (T52: server/compat/{quake3,quake,quake2,wop}.qc) ----
        // Each is a SPAWNFUNC_ITEM(classname, ITEM_X) in the compat .qc; the def is the matching [Item] NetName.
        // quake3.qc:102-115 — armor / powerups / medkit.
        AliasItem("item_armor_body", "armor_mega");       // ITEM_ArmorMega
        AliasItem("item_armor_combat", "armor_big");      // ITEM_ArmorBig
        AliasItem("item_armor_shard", "armor_small");     // ITEM_ArmorSmall
        AliasItem("item_armor_green", "armor_medium");    // ITEM_ArmorMedium (CCTF)
        AliasItem("item_quad", "strength");               // ITEM_Strength
        AliasItem("item_enviro", "invincible");           // ITEM_Shield (ShieldItem.NetName == "invincible")
        AliasItem("item_haste", "speed");                 // ITEM_Speed
        AliasItem("item_invis", "invisibility");          // ITEM_Invisibility
        AliasItem("holdable_medkit", "armor_big");        // ITEM_ArmorBig (we have no holdables)
        // quake2.qc:9-11 — Q2 / CPMA armor + invuln.
        AliasItem("item_armor_jacket", "armor_medium");   // ITEM_ArmorMedium
        AliasItem("item_invulnerability", "invincible");  // ITEM_Shield
        // quake.qc:17-20 — Q1 ammo box / armors / health (the health branches on spawnflag 2).
        AliasItem("item_spikes", "bullets");              // ITEM_Bullets
        AliasItem("item_armor2", "armor_mega");           // ITEM_ArmorMega
        AliasItem("item_armorInv", "armor_mega");         // ITEM_ArmorMega
        // QC: item_health -> this.spawnflags & 2 ? ITEM_HealthMega : ITEM_HealthMedium
        SpawnFuncs.Register("item_health",
            e => ItemSpawn(e, (e.SpawnFlags & 2) != 0 ? "health_mega" : "health_medium"));
        // wop.qc:18-42 — World of Padman items + ammo boxes (weapons are in the compat-weapon block below).
        AliasItem("item_padpower", "strength");           // ITEM_Strength
        AliasItem("item_climber", "invincible");          // ITEM_Shield
        AliasItem("item_speedy", "speed");                // spawnfunc_item_speed
        AliasItem("item_visionless", "invisibility");     // spawnfunc_item_invisibility
        AliasItem("item_armor_padshield", "armor_mega");  // ITEM_ArmorMega
        AliasItem("holdable_floater", "jetpack");         // ITEM_Jetpack
        AliasItem("ammo_pumper", "shells");               // ITEM_Shells (WoP ammo are plain ammo items, no scaling)
        AliasItem("ammo_nipper", "bullets");              // ITEM_Bullets
        AliasItem("ammo_balloony", "rockets");            // ITEM_Rockets
        AliasItem("ammo_bubbleg", "rockets");             // ITEM_Rockets
        AliasItem("ammo_boaster", "cells");               // ITEM_Cells
        AliasItem("ammo_betty", "rockets");               // ITEM_Rockets
        AliasItem("ammo_imperius", "cells");              // ITEM_Cells

        // ---- Q3/QL/CPMA/Q1/WoP compat WEAPON remaps (T52: SPAWNFUNC_Q3WEAPON / SPAWNFUNC_WEAPON) ----
        // The weapon is resolved at SPAWN time (CompatRemaps.WeaponForClassname), like QC's per-spawn macro
        // body, so the cvar-dependent picks (nailgun / plasmagun / bfg) honour the live cvars. WoP's
        // weapon_punchy / _nipper / etc. are SPAWNFUNC_WEAPON too and resolve through the same table.
        foreach (string cls in CompatWeaponClassnames)
        {
            string c = cls; // capture
            SpawnFuncs.Register(c, e =>
            {
                Weapon? w = CompatRemaps.WeaponForClassname(c);
                if (w is null) { MapMover.RemoveEntity(e); return; }
                WeaponSpawn(e, w);
            });
        }

        // ---- Q3 compat AMMO remaps (T52: SPAWNFUNC_Q3AMMO) ----
        // Each Q3 ammo classname seeds the matching resource from rint(.count * GetAmmoConsumption(weapon)),
        // scaled by the SPAWNFUNC_Q3 multiplier, then spawns the ammo item (or deletes if the weapon is
        // ammo-less). Resolved at spawn time so the cvar-branching pairs (nails/cells/bfg) stay live.
        foreach (string cls in CompatAmmoClassnames)
        {
            string c = cls; // capture
            SpawnFuncs.Register(c, e => AmmoSpawn(e, c));
        }

        // ---- weapon_* (QC weapon_defaultspawnfunc): one spawnfunc per Weapon registry entry ----
        foreach (Weapon w in Weapons.All)
        {
            string cls = "weapon_" + w.NetName;
            Weapon wep = w; // capture
            SpawnFuncs.Register(cls, e => WeaponSpawn(e, wep));
            // weapon items have no item_*-style NetName; their canonical classname is weapon_<netname>.
            _netNameToClassName[w.NetName] = cls;
        }
    }

    // The 13 QC buffs that carry an item_buff_<type> map spawnfunc (buffs.qh BUFF_SPAWNFUNCS calls, one per
    // buff/<type>.qh). item_buff_random (null type) is added separately below.
    private static readonly string[] BuffSpawnTypes =
    {
        "ammo", "bash", "disability", "flight", "inferno", "jump", "luck",
        "magnet", "medic", "resistance", "swapper", "vampire", "vengeance",
    };

    // The teamplay-only team_forced suffixes (QC BUFF_SPAWNFUNCS: _team1.._team4 -> NUM_TEAM_1..NUM_TEAM_4).
    private static readonly (string suffix, int team)[] BuffTeamVariants =
    {
        ("_team1", Teams.Red), ("_team2", Teams.Blue), ("_team3", Teams.Yellow), ("_team4", Teams.Pink),
    };

    // The Q3TA/Q3A/WOP holdable/item compat classnames -> canonical buff short name (QC buffs/<type>.qh
    // BUFF_SPAWNFUNC_Q3COMPAT lines; the buffname is what Buff_CompatName maps).
    private static readonly (string className, string buff)[] BuffCompatClassnames =
    {
        ("item_ammoregen", "ammo"),                  // Q3TA ammoregen
        ("item_scout", "bash"),                      // Q3TA scout
        ("item_flight", "flight"),                   // (WOP flight)
        ("item_doubler", "inferno"),                 // Q3TA doubler
        ("item_jumper", "jump"),                     // WOP jumper
        ("item_regen", "medic"),                     // Q3A regen
        ("item_revival", "medic"),                   // WOP revival
        ("item_guard", "resistance"),                // Q3TA guard
        ("holdable_teleporter", "swapper"),          // Q3A personal teleporter
        ("holdable_invulnerability", "vampire"),     // Q3TA invulnerability
        ("holdable_kamikaze", "vengeance"),          // Q3TA kamikaze
    };

    // QC buffs.qh BUFF_SPAWNFUNCS / BUFF_SPAWNFUNC_Q3COMPAT: install every item_buff_* + compat classname so a
    // map can place buff pickups. Each routes to BuffsMutator.SpawnMapBuff (buff_Init), which gates on g_buffs
    // and frees the edict when buffs are off / no type is available.
    private static void RegisterBuffSpawnFuncs()
    {
        // item_buff_random — a null type forces randomization (QC BUFF_SPAWNFUNCS(random, NULL)).
        SpawnFuncs.Register("item_buff_random", e => BuffsMutator.SpawnMapBuff(e, null, 0));

        foreach (string t in BuffSpawnTypes)
        {
            string type = t; // capture
            SpawnFuncs.Register("item_buff_" + type, e => BuffsMutator.SpawnMapBuff(e, type, 0));
            foreach (var (suffix, team) in BuffTeamVariants)
            {
                int tm = team;
                SpawnFuncs.Register("item_buff_" + type + suffix, e => BuffsMutator.SpawnMapBuff(e, type, tm));
            }
        }

        foreach (var (className, buff) in BuffCompatClassnames)
        {
            string b = buff; // capture
            SpawnFuncs.Register(className, e => BuffsMutator.SpawnMapBuff(e, b, 0));
        }
    }

    // Register a classname -> the [Item] Pickup with the given NetName, and record it as that def's canonical name.
    private static void RegisterItem(string className, string netName)
    {
        SpawnFuncs.Register(className, e => ItemSpawn(e, netName));
        if (!_netNameToClassName.ContainsKey(netName))
            _netNameToClassName[netName] = className; // first classname registered is canonical
    }

    // Register an ALIAS classname pointing at the same def (does NOT override the canonical classname).
    private static void AliasItem(string className, string netName)
        => SpawnFuncs.Register(className, e => ItemSpawn(e, netName));

    // The compat WEAPON classnames (SPAWNFUNC_Q3WEAPON in quake3.qc + SPAWNFUNC_WEAPON in quake.qc / wop.qc).
    // Resolved per-spawn via CompatRemaps.WeaponForClassname so the cvar-branching picks stay live.
    private static readonly string[] CompatWeaponClassnames =
    {
        // Q3 / QL / CPMA / Team Arena (quake3.qc)
        "weapon_shotgun", "weapon_machinegun", "weapon_grenadelauncher", "weapon_prox_launcher",
        "weapon_chaingun", "weapon_hmg", "weapon_nailgun", "weapon_lightning", "weapon_plasmagun",
        "weapon_railgun", "weapon_bfg", "weapon_grapplinghook", "weapon_rocketlauncher", "weapon_gauntlet",
        // Q1 (quake.qc)
        "weapon_supernailgun", "weapon_supershotgun",
        // World of Padman (wop.qc)
        "weapon_punchy", "weapon_nipper", "weapon_pumper", "weapon_boaster", "weapon_splasher",
        "weapon_bubbleg", "weapon_balloony", "weapon_betty", "weapon_imperius",
    };

    // The Q3 compat AMMO classnames (the SPAWNFUNC_Q3AMMO half of each SPAWNFUNC_Q3 in quake3.qc).
    private static readonly string[] CompatAmmoClassnames =
    {
        "ammo_shells", "ammo_bullets", "ammo_grenades", "ammo_mines", "ammo_belt", "ammo_hmg",
        "ammo_nails", "ammo_lightning", "ammo_cells", "ammo_slugs", "ammo_bfg", "ammo_rockets",
    };

    // ---- the Q3 ammo spawnfunc (QC SPAWNFUNC_Q3AMMO body, quake3.qh:18-25) ----
    private static void AmmoSpawn(Entity e, string className)
    {
        CompatRemaps.AmmoRemap? remap = CompatRemaps.AmmoForClassname(className);
        if (remap is null) { MapMover.RemoveEntity(e); return; } // unmapped (weapon registry missing) -> delete

        // QC: scale .count + seed the resource (CompatRemaps.ApplyAmmoRemap), then SPAWNFUNC_BODY the ammo item.
        // A null Pickup means the weapon is ammo-less (FIREBALL): QC's SPAWNFUNC_BODY deletes the entity.
        Pickup? def = CompatRemaps.ApplyAmmoRemap(e, remap.Value);
        if (def is null || !def.ItemDef.IsAllowed)
        {
            MapMover.RemoveEntity(e); // QC SPAWNFUNC_BODY else-branch: startitem_failed = true; delete(this).
            return;
        }
        StartItem.Spawn(e, def);
    }

    /// <summary>QC <c>m_canonical_spawnfunc</c> for a def — the classname StartItem assigns to this.classname.</summary>
    public static string CanonicalSpawnFunc(Pickup def)
        => _netNameToClassName.TryGetValue(def.NetName, out string? cls) ? cls
            : (def.IsWeaponPickup ? "weapon_" + def.NetName : "item_" + def.NetName);

    // ---- the resource/powerup item spawnfunc (QC SPAWNFUNC_BODY) ----
    private static void ItemSpawn(Entity e, string netName)
    {
        Pickup? def = Items.ByName(netName);
        if (def is null || !def.ItemDef.IsAllowed)
        {
            // QC SPAWNFUNC_BODY else-branch: startitem_failed = true; delete(this).
            MapMover.RemoveEntity(e);
            return;
        }
        // QC m_spawnfunc_hookreplace(this, e): let the def optionally substitute itself with a different def
        // before StartItem runs. This is how jetpack/fuelregen replace themselves with ITEM_Fuel when the
        // player already gets the held-bit as a start item (jetpack.qc:6-11, fuelregen.qc:6-11).
        Pickup effective = def.SpawnFuncHookReplace(e);
        StartItem.Spawn(e, effective);
        // (StartItem removes the edict itself on a failed permanent spawn; nothing more to do here.)
    }

    // ---- the weapon item spawnfunc (QC weapon_defaultspawnfunc, server/weapons/spawning.qc:29) ----
    private static void WeaponSpawn(Entity e, Weapon w)
    {
        // QC weapon_defaultspawnfunc (spawning.qc:33): the weaponreplace + SetWeaponreplace mutator pass runs only
        // for a real (non-loot, non-already-replaced) map weapon entity. The port's loot/thrown path reuses
        // PickupFor + StartItem.SpawnLoot directly, so any e reaching here is a map spawn — run the full pass.
        if (!e.ItemIsLoot && !e.IsReplacedWeapon)
        {
            // NOTE (deferred): QC weapon_defaultspawnfunc rejects a WEP_FLAG_MUTATORBLOCKED weapon outright here
            // (startitem_failed). The port does NOT yet enforce that at map-spawn — doing so changes weapon spawning
            // project-wide (compat-remapped arc/minelayer, Weapons.All[0]-based item tests) and needs its own pass
            // with the spawn-rejection test contract updated. Left as a separate parity item so the weaponreplace
            // pass below (the New-Toys gap this file closes) ships without that broad behavior change.

            // QC: s = W_Apply_Weaponreplace(wpn.netname); MUTATOR_CALLHOOK(SetWeaponreplace, this, wpn, s);
            //     s = M_ARGV(2, string); — the New Toys SetWeaponreplace handler rewrites s to the map "new_toys"
            // key (or the global autoreplace mapping), then re-applies the per-weapon weaponreplace cvar.
            string s = W_Apply_Weaponreplace(w.NetName);
            s = MutatorHooks.FireSetWeaponreplace(e, w, s);
            if (s.Length == 0)
                { MapMover.RemoveEntity(e); return; } // empty result deletes the entity (QC startitem_failed)

            string[] toks = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // QC: a >=2 token list links the extra weapons as an --internalteam replacement group (the engine
            // shows one). The port has no team-item select group at this layer, so the extra tokens spawn their
            // own world weapon (faithful set of weapons present; the single-show grouping is the deviation noted
            // in StartItem). argv(0) stays as THIS entity's weapon.
            if (toks.Length >= 2)
            {
                for (int i = 1; i < toks.Length; i++)
                {
                    Weapon? wep = Weapons.ByName(toks[i]);
                    if (wep is null) continue;
                    Entity rep = Api.Entities.Spawn();
                    rep.ClassName = e.ClassName;
                    rep.Origin = e.Origin;
                    rep.OldOrigin = e.Origin;
                    rep.Angles = e.Angles;
                    rep.TargetName = e.TargetName;
                    rep.Target = e.Target;
                    rep.SpawnFlags = e.SpawnFlags;
                    rep.Team = e.Team;
                    rep.IsReplacedWeapon = true; // QC replacement.m_isreplaced = true (skips a recursive replace)
                    WeaponSpawn(rep, wep);
                }
            }

            // QC: wpn = Weapon_from_name(argv(0)); if (wpn == WEP_Null) { delete; startitem_failed; return; }
            Weapon? primary = Weapons.ByName(toks[0]);
            if (primary is null)
                { MapMover.RemoveEntity(e); return; }
            w = primary;
        }

        WeaponPickup def = PickupFor(w);

        // QC weapon_defaultspawnfunc: default respawntime; default pickup ammo if the edict didn't set it; the
        // g_pickup_weapons_anyway pickup_anyway. The ammo + weapon-set seeding happen in WeaponPickup.ItemInit.
        if (e.ItemRespawnTime == 0f)
            e.ItemRespawnTime = ItemPickupRules.CvarOr(
                w.IsSuperWeapon ? "g_pickup_respawntime_superweapon" : "g_pickup_respawntime_weapon", 10f);
        if (ItemPickupRules.CvarBoolOr("g_pickup_weapons_anyway", false))
            e.PickupAnyway = 1;

        StartItem.Spawn(e, def);
    }

    /// <summary>
    /// QC <c>W_Apply_Weaponreplace(string in)</c> (server/weapons/spawning.qc:13): token-walk <paramref name="in"/>;
    /// for each token resolve the weapon and substitute its own <c>weaponreplace</c> cvar (single-level), dropping
    /// any <c>"0"</c> token, then rebuild the space-joined list. An unknown token is kept verbatim (QC's
    /// <c>Weapon_from_name</c> null case leaves <c>replacement = it</c>).
    /// </summary>
    public static string W_Apply_Weaponreplace(string input)
    {
        // QC FOREACH_WORD(in, true, ...): per token resolve the weapon's own weaponreplace (else keep the token),
        // drop a whole-string "0" (this slot spawns nothing), and cons() the replacement onto the output. cons
        // joins with a single space; tokenize_console later re-splits, so a multi-name replacement string is fine.
        string outStr = "";
        foreach (string tok in input.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string replacement = tok;
            Weapon? w = Weapons.ByName(tok);
            if (w is not null)
                replacement = w.WeaponReplace.Length > 0 ? w.WeaponReplace : tok;
            if (replacement == "0") continue; // QC: replacement == "0" => drop
            outStr = outStr.Length == 0 ? replacement : outStr + " " + replacement; // QC cons(out, replacement)
        }
        return outStr;
    }

    /// <summary>The stable per-weapon <see cref="WeaponPickup"/> def (QC <c>wpn.m_pickup</c>). Public so the
    /// thrown-weapon path (<see cref="WeaponThrowing"/>, T57) reuses the same loot def the map spawnfunc uses.</summary>
    public static WeaponPickup PickupFor(Weapon w)
    {
        if (!_weaponPickups.TryGetValue(w.NetName, out WeaponPickup? wp))
            _weaponPickups[w.NetName] = wp = new WeaponPickup(w);
        return wp;
    }
}

/// <summary>
/// A synthetic <see cref="Pickup"/> wrapping a <see cref="Weapon"/> — the C# successor to QC <c>wpn.m_pickup</c>
/// (the WeaponPickup GameItem a weapon's m_pickup ATTRIB points at). Marks the def a weapon pickup, carries the
/// weapon's item model + colour, and in <see cref="ItemInit"/> seeds the world item's weapon set (so Item_GiveTo's
/// weapon block grants it) and the pickup ammo (so a fresh weapon comes with ammo). One stable instance per weapon.
/// </summary>
public sealed class WeaponPickup : Pickup
{
    private readonly Weapon _weapon;

    public WeaponPickup(Weapon w)
    {
        _weapon = w;
        NetName = w.NetName;
        DisplayName = w.DisplayName;
        Model = w.ItemModel ?? w.WorldModel;
        Mins = ItemBoxes.DefaultMins; // QC weapons use the default Pickup box
        Maxs = ItemBoxes.DefaultMaxs;
        RespawnTime = 10f;            // g_pickup_respawntime_weapon (overridden per-edict in WeaponSpawn)
        ItemDef.IsWeaponPickup = true; // QC instanceOfWeaponPickup
        ItemDef.ItemId = ItemFlag.None; // weapons carry no IT_* bit (the weapon set is the ownership rep)
        ItemDef.Color = w.Color;
        ItemDef.PickupSound = "WEAPONPICKUP";
    }

    /// <summary>
    /// QC weapon_defaultspawnfunc body: put this weapon in the world item's weapon set (STAT(WEAPONS,this)) and,
    /// if its ammo resource isn't already set on the edict, seed the pickup ammo (WEP_CVAR(pickup_ammo), else the
    /// matching g_pickup_&lt;resource&gt; amount). Item_GiveTo then grants the weapon + that ammo on touch.
    /// </summary>
    public override void ItemInit(Entity item)
    {
        item.OwnedWeaponSet.Add(_weapon);

        if (_weapon.AmmoType != ResourceType.None && item.GetResource(_weapon.AmmoType) == 0f)
        {
            float ammo = ItemPickupRules.CvarOr($"g_balance_{_weapon.NetName}_pickup_ammo", PickupAmmoFallback());
            if (ammo > 0f)
                item.SetResourceExplicit(_weapon.AmmoType, ammo);
        }
    }

    // A reasonable stock pickup-ammo per resource when the per-weapon cvar is unset — the matching ammo-item
    // amount (QC's WEP_CVAR pickup_ammo values are close to these). Faithful enough for the pickup give.
    private float PickupAmmoFallback() => _weapon.AmmoType switch
    {
        ResourceType.Shells  => ItemPickupRules.CvarOr("g_pickup_shells", 15f),
        ResourceType.Bullets => ItemPickupRules.CvarOr("g_pickup_nails", 80f),
        ResourceType.Rockets => ItemPickupRules.CvarOr("g_pickup_rockets", 40f),
        ResourceType.Cells   => ItemPickupRules.CvarOr("g_pickup_cells", 30f),
        ResourceType.Fuel    => ItemPickupRules.CvarOr("g_pickup_fuel", 50f),
        _ => 0f,
    };
}
