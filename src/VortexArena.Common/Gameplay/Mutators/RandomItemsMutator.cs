// Port of common/mutators/mutator/random_items/sv_random_items.qc (+ sv_random_items.qh)

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Random Items mutator — port of common/mutators/mutator/random_items/sv_random_items.qc (Lyberta). Two
/// independent behaviors: <c>g_random_items</c> replaces every map item with a random one (and re-randomizes it
/// each respawn), and <c>g_random_loot</c> drops a handful of random loot items where a player dies. Enabled when
/// either cvar is set.
///
/// Ported here:
///  - the enable gate + the loot cvars (<c>g_random_loot_min/max/time/spread</c>);
///  - the full item-type probability machinery (<see cref="RandomItemType"/> +
///    <see cref="GetRandomItemClassName"/> / <see cref="GetRandomVanillaItemClassName"/>): a weighted pick over
///    the configured per-type probability cvars, then a per-item pick within the chosen type — faithfully mirroring
///    RandomItems_GetRandomVanillaItemClassName over ALL five types (health/armor/resource/weapon/powerup), each
///    resolved against the live <see cref="Items"/> / <see cref="Weapons"/> registries using the canonical
///    spawnfunc classnames (<see cref="ItemSpawnFuncs.CanonicalSpawnFunc"/>);
///  - the PlayerDies loot drop (<c>g_random_loot</c>): spawn floor(min + random()*max) loot items at the corpse,
///    each a random classname launched on a random spread, as a real touch-pickupable loot pickup via
///    <see cref="StartItem.SpawnLoot"/> (the same path the thrown-weapon / Overkill drops use).
///
/// MAP-replacement half (<c>g_random_items</c>): now LIVE. The entity-level <see cref="MutatorHooks.FilterItem"/>
/// hook (fired from <see cref="StartItem"/> at the QC items.qc:1031 seam) replaces a spawning map item with a random
/// one (<see cref="ReplaceMapItem"/> → <c>RandomItems_ReplaceMapItem</c>: the <c>g_random_items_replace_*</c> cvar
/// drives the literal-"random" weighted pick vs the tokenized candidate list), and the
/// <see cref="MutatorHooks.ItemTouched"/> hook re-randomizes a picked-up map item each respawn. A static
/// <see cref="_isSpawning"/> recursion guard (QC <c>random_items_is_spawning</c>) stops the replacement item from
/// re-replacing itself, and replaced/loot items spawned under Overkill are tagged <see cref="Entity.OkItem"/>.
/// The LOOT half (<c>g_random_loot</c>) is fully live + faithful. The Overkill item-pool override is wired via the
/// <see cref="MutatorHooks.RandomItemsGetClassName"/> mod-injection hook (OverkillMutator); the Instagib override
/// is deferred until its VaporizerCells/ExtraLife items are ported (their classnames cannot resolve to a pickup yet).
/// </summary>
[Mutator]
public sealed class RandomItemsMutator : MutatorBase
{
    /// <summary>QC RANDOM_ITEM_TYPE_* (sv_random_items.qh:11) — the item categories a random pick chooses among.</summary>
    [Flags]
    public enum RandomItemType
    {
        None = 0,
        Health = 1 << 0,
        Armor = 1 << 1,
        Resource = 1 << 2,
        Weapon = 1 << 3,
        Powerup = 1 << 4,
        All = (1 << 5) - 1,   // QC RANDOM_ITEM_TYPE_ALL = BITS(5)
    }

    /// <summary>QC autocvar_g_random_items.</summary>
    public bool RandomItems;

    /// <summary>QC autocvar_g_random_loot.</summary>
    public bool RandomLoot;

    /// <summary>QC autocvar_g_random_loot_min.</summary>
    public float LootMin;

    /// <summary>QC autocvar_g_random_loot_max.</summary>
    public float LootMax;

    /// <summary>QC autocvar_g_random_loot_time — seconds the dropped loot stays (Base/cfg default 10).</summary>
    public float LootTime = 10f;

    /// <summary>QC autocvar_g_random_loot_spread — how far loot can be thrown.</summary>
    public float LootSpread = 200f;

    public RandomItemsMutator() => NetName = "random_items";

    // QC: REGISTER_MUTATOR(random_items, (autocvar_g_random_items || autocvar_g_random_loot)).
    public override bool IsEnabled =>
        Api.Services is not null
        && (Api.Cvars.GetFloat("g_random_items") != 0f || Api.Cvars.GetFloat("g_random_loot") != 0f);

    /// <summary>
    /// QC <c>random_items_is_spawning</c> (sv_random_items.qc:42): the recursion guard set around a spawn/replace
    /// so the replacement item's OWN FilterItem (which re-enters StartItem) does NOT re-replace it. Static because
    /// QC's is a file global and the replace path can re-enter through the shared StartItem driver.
    /// </summary>
    private static bool _isSpawning;

    private HookHandler<MutatorHooks.PlayerDiesArgs>? _onPlayerDies;
    private HookHandler<MutatorHooks.FilterItemArgs>? _onFilterItem;
    private HookHandler<MutatorHooks.ItemTouchedArgs>? _onItemTouched;

    public override void Hook()
    {
        _onPlayerDies ??= OnPlayerDies;
        _onFilterItem ??= OnFilterItem;
        _onItemTouched ??= OnItemTouched;
        MutatorHooks.PlayerDies.Add(_onPlayerDies, HookOrder.Last);    // QC CBC_ORDER_LAST
        MutatorHooks.FilterItem.Add(_onFilterItem, HookOrder.Last);    // QC CBC_ORDER_LAST
        MutatorHooks.ItemTouched.Add(_onItemTouched, HookOrder.Last);  // QC CBC_ORDER_LAST

        if (Api.Services is not null)
        {
            RandomItems = Api.Cvars.GetFloat("g_random_items") != 0f;
            RandomLoot = Api.Cvars.GetFloat("g_random_loot") != 0f;
            LootMin = Api.Cvars.GetFloat("g_random_loot_min");
            LootMax = Api.Cvars.GetFloat("g_random_loot_max");
            float lt = Api.Cvars.GetFloat("g_random_loot_time");
            if (lt != 0f) LootTime = lt;
            float ls = Api.Cvars.GetFloat("g_random_loot_spread");
            if (ls != 0f) LootSpread = ls;
        }
    }

    public override void Unhook()
    {
        if (_onPlayerDies is not null) MutatorHooks.PlayerDies.Remove(_onPlayerDies);
        if (_onFilterItem is not null) MutatorHooks.FilterItem.Remove(_onFilterItem);
        if (_onItemTouched is not null) MutatorHooks.ItemTouched.Remove(_onItemTouched);
    }

    // QC MUTATOR_HOOKFUNCTION(random_items, BuildMutatorsString) (sv_random_items.qc:312).
    public override string BuildMutatorsString(string s) => s + ":random_items";

    // QC MUTATOR_HOOKFUNCTION(random_items, BuildMutatorsPrettyString) (sv_random_items.qc:317).
    public override string BuildMutatorsPrettyString(string s) => s + ", Random items";

    // QC MUTATOR_HOOKFUNCTION(random_items, FilterItem, CBC_ORDER_LAST) (sv_random_items.qc:323): replace a
    // spawning MAP item with a random one. Returns true to FORBID (delete) the original — the replacement has
    // already spawned via RandomItems_ReplaceMapItem.
    private bool OnFilterItem(ref MutatorHooks.FilterItemArgs args)
    {
        if (!RandomItems) return false;          // QC: if (!autocvar_g_random_items) return false;
        if (_isSpawning) return false;           // QC: if (random_items_is_spawning) return false;
        Entity item = args.Item;
        if (item.ItemIsLoot) return false;       // QC: if (ITEM_IS_LOOT(item)) return false;
        return ReplaceMapItem(item) is not null; // QC: RandomItems_ReplaceMapItem(item) == NULL ? false : true.
    }

    // QC MUTATOR_HOOKFUNCTION(random_items, ItemTouched, CBC_ORDER_LAST) (sv_random_items.qc:347): re-randomize a
    // picked-up MAP item — replace it, schedule the replacement's respawn, delete the original.
    private bool OnItemTouched(ref MutatorHooks.ItemTouchedArgs args)
    {
        if (!RandomItems) return false;          // QC: if (!autocvar_g_random_items) return;
        Entity item = args.Item;
        if (item.ItemIsLoot) return false;       // QC: if (ITEM_IS_LOOT(item)) return;
        Entity? newItem = ReplaceMapItem(item);
        if (newItem is null) return false;       // QC: if (new_item == NULL) return;
        ItemPickupRules.ScheduleRespawn(newItem); // QC: Item_ScheduleRespawn(new_item);
        ItemPickupRules.RemoveItem(item);         // QC: delete(item);
        return false;
    }

    /// <summary>
    /// Port of <c>RandomItems_ReplaceMapItem(item)</c> (sv_random_items.qc:233): pick a replacement classname for
    /// <paramref name="item"/> from its <c>g_random_items_replace_&lt;classname&gt;</c> cvar (the literal "random"
    /// draws from the weighted tables; otherwise a uniform pick from the tokenized candidate list), and, if it
    /// differs from the current classname, spawn that classname as a PERMANENT (non-loot) item copying the
    /// original's placement. Returns the spawned item, or null when there is no replacement / the spawn freed
    /// itself. Sets the recursion guard around the spawn so the replacement doesn't re-replace.
    /// </summary>
    private Entity? ReplaceMapItem(Entity item)
    {
        if (Api.Services is null) return null;

        // QC RandomItems_GetItemReplacementClassNames: cvar_string("g_random_items_replace_<classname>").
        string cvar = $"g_random_items_replace_{item.ClassName}";
        string newClassnames = Api.Cvars.GetString(cvar);
        if (string.IsNullOrEmpty(newClassnames)) return null; // QC: missing cvar -> warn, return NULL.

        string newClassname;
        if (newClassnames == "random")
        {
            newClassname = GetRandomItemClassName("random_items"); // QC: draw from the weighted tables.
            if (string.IsNullOrEmpty(newClassname)) return null;
        }
        else
        {
            // QC: tokenize_console; 1 entry -> as-is; else argv(floor(random()*n)).
            string[] toks = newClassnames.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (toks.Length == 0) return null;
            newClassname = toks.Length == 1 ? toks[0] : toks[Prandom.RangeInt(0, toks.Length)];
        }

        if (newClassname == item.ClassName) return null; // QC: chosen == current -> no change.

        // QC: random_items_is_spawning = true; spawn(); Item_CopyFields(item, new_item); classname; lifetime = -1
        //     (permanent, not loot); if (ok) ok_item = true; Item_Initialise(new_item); guard = false.
        bool prevGuard = _isSpawning;
        _isSpawning = true;
        Entity newItem = Api.Entities.Spawn();
        CopyMapPlacement(item, newItem);
        newItem.ClassName = newClassname; // QC new_item.classname = strzone(new_classname) (before Item_Initialise).
        newItem.ItemIsLoot = false;
        if (OverkillEnabled())
            newItem.OkItem = true;
        // SpawnFuncs dispatch IS the port's Item_Initialise for a classname (resolves the def + runs StartItem).
        bool spawned = SpawnFuncs.TrySpawn(newClassname, newItem);
        _isSpawning = prevGuard;

        if (!spawned || newItem.IsFreed) return null; // QC: wasfreed(new_item) ? NULL : new_item.
        return newItem;
    }

    // QC Item_CopyFields: copy the placement fields the new map item inherits from the one it replaces (origin,
    // angles, spawnflags, target(name), team, noalign) so it sits exactly where the original was authored.
    private static void CopyMapPlacement(Entity from, Entity to)
    {
        to.Origin = from.Origin;
        to.OldOrigin = from.Origin;
        if (Api.Services is not null)
            Api.Entities.SetOrigin(to, to.Origin);
        to.Angles = from.Angles;
        to.SpawnFlags = from.SpawnFlags;
        to.Target = from.Target;
        to.TargetName = from.TargetName;
        to.Team = from.Team;
        to.NoAlign = from.NoAlign;
    }

    // QC MUTATOR_IS_ENABLED(ok): read the Overkill mutator's enable predicate (matches OverkillMutator.OtherEnabled).
    private static bool OverkillEnabled() => Mutators.ByName("overkill") is { } m && m.IsEnabled;

    // MUTATOR_HOOKFUNCTION(random_items, PlayerDies)
    private bool OnPlayerDies(ref MutatorHooks.PlayerDiesArgs args)
    {
        if (!RandomLoot) return false; // QC: if (!autocvar_g_random_loot) return;
        if (Api.Services is null) return false;

        Entity victim = args.Target;
        Vector3 lootPos = victim.Origin + new Vector3(0f, 0f, 32f);
        // QC: floor(min + random() * max).
        int num = (int)MathF.Floor(LootMin + Prandom.Float() * LootMax);
        for (int i = 0; i < num; i++)
            SpawnLootItem(lootPos);
        return false;
    }

    /// <summary>
    /// Port of <c>RandomItems_SpawnLootItem(position)</c>: pick a random loot classname and spawn it at the
    /// position with a random spread velocity, as a real touch-pickupable loot item that despawns after
    /// <see cref="LootTime"/> seconds. Resolves the chosen classname to its <see cref="Pickup"/> def and spawns it
    /// through <see cref="StartItem.SpawnLoot"/> (QC <c>Item_Initialise</c> with lifetime &gt;= 0) — the same loot
    /// path the thrown-weapon / Overkill drops use, so the dropped item is MOVETYPE_TOSS and collectable on touch.
    /// </summary>
    private void SpawnLootItem(Vector3 position)
    {
        if (Api.Services is null) return;
        string className = GetRandomItemClassName("random_loot");
        if (string.IsNullOrEmpty(className)) return;

        Pickup? def = ResolvePickup(className);
        if (def is null) return; // QC SPAWNFUNC_BODY else-branch: nothing to spawn.

        // QC: spread.z = spread/2; spread += randomvec() * spread.
        Vector3 spread = new(0f, 0f, LootSpread / 2f);
        spread += Prandom.Vec() * LootSpread;

        // QC: random_items_is_spawning = true around the spawn (so the loot item's own FilterItem doesn't replace
        // it); if (MUTATOR_IS_ENABLED(ok)) ok_item = true.
        bool prevGuard = _isSpawning;
        _isSpawning = true;
        Entity item = Api.Entities.Spawn();
        Api.Entities.SetOrigin(item, position);
        item.Velocity = spread;
        item.ItemIsLoot = true; // QC Item_Initialise(lifetime>=0) makes it loot; SpawnLoot also tags it.
        if (OverkillEnabled())
            item.OkItem = true;

        // QC item.lifetime = autocvar_g_random_loot_time; Item_Initialise (loot path: MOVETYPE_TOSS, touch-pickup,
        // despawn after lifetime). SpawnLoot owns the toss/despawn/touch; on a failed spawn it removes the edict.
        StartItem.SpawnLoot(item, def, LootTime);
        _isSpawning = prevGuard;
    }

    /// <summary>
    /// Resolve a random-items classname (QC <c>m_canonical_spawnfunc</c>: <c>weapon_*</c> or <c>item_*</c>) back to
    /// its <see cref="Pickup"/> def for <see cref="StartItem.SpawnLoot"/>. Weapon classnames map through the synthetic
    /// per-weapon <see cref="WeaponPickup"/> (<see cref="ItemSpawnFuncs.PickupFor"/>); item classnames are matched by
    /// scanning the live <see cref="Items"/> registry for the def whose canonical spawnfunc equals the classname.
    /// </summary>
    private static Pickup? ResolvePickup(string className)
    {
        if (className.StartsWith("weapon_", StringComparison.Ordinal))
        {
            Weapon? w = Weapons.ByName(className["weapon_".Length..]);
            return w is null ? null : ItemSpawnFuncs.PickupFor(w);
        }
        foreach (Pickup def in Items.All)
        {
            if (def.IsWeaponPickup) continue;
            if (ItemSpawnFuncs.CanonicalSpawnFunc(def) == className)
                return def;
        }
        return null;
    }

    /// <summary>
    /// Port of <c>RandomItems_GetRandomItemClassName(prefix)</c> (sv_random_items.qc:54): the public entry. QC
    /// first fires the <c>RandomItems_GetRandomItemClassName</c> mutator hook so a mod (Overkill / Instagib) can
    /// substitute its OWN item pool; if a handler consumes it, that classname is returned. Otherwise it falls
    /// through to the vanilla weighted pick over ALL types.
    /// </summary>
    public string GetRandomItemClassName(string prefix)
    {
        // QC: if (MUTATOR_CALLHOOK(RandomItems_GetRandomItemClassName, prefix)) return M_ARGV(1, string);
        string? overridden = MutatorHooks.FireRandomItemsGetClassName(prefix);
        if (overridden is not null) return overridden;
        return GetRandomVanillaItemClassName(prefix, RandomItemType.All);
    }

    /// <summary>
    /// Port of <c>RandomItems_GetRandomVanillaItemClassName(prefix, types)</c>: weighted-pick an item TYPE from the
    /// configured per-type probability cvars (<c>g_{prefix}_{type}_probability</c>), then pick a concrete classname
    /// within that type; on an empty type, drop it from the mask and retry — matching the QC loop. All five types
    /// (health/armor/resource/weapon/powerup) resolve against the live <see cref="Items"/> / <see cref="Weapons"/>
    /// registries, keyed by the canonical spawnfunc classname.
    /// </summary>
    public string GetRandomVanillaItemClassName(string prefix, RandomItemType types)
    {
        if (types == RandomItemType.None) return "";

        while (types != RandomItemType.None)
        {
            // Weighted reservoir over the present types by their probability cvar (QC RandomSelection_AddFloat).
            RandomItemType chosenType = RandomItemType.None;
            float total = 0f;
            foreach (RandomItemType t in EachType(types))
            {
                float prob = Api.Services is null ? 0f : Api.Cvars.GetFloat(ProbCvar(prefix, t));
                if (prob <= 0f) continue;
                total += prob;
                if (Prandom.Float() * total <= prob) chosenType = t;
            }

            string className = chosenType == RandomItemType.Weapon
                ? GetRandomWeaponClassName(prefix)
                : GetRandomItemClassNameWithProperty(prefix, chosenType);

            if (className != "") return className;
            types &= ~chosenType;            // QC: types &= ~item_type;
            if (chosenType == RandomItemType.None) break; // no probable type at all → stop (avoid infinite loop)
        }
        return "";
    }

    /// <summary>
    /// Port of the WEAPON case of RandomItems_GetRandomVanillaItemClassName: weighted pick over the non-mutatorblocked
    /// weapons by <c>g_{prefix}_{m_canonical_spawnfunc}_probability</c> (the canonical spawnfunc is <c>weapon_{netname}</c>),
    /// returning that canonical classname.
    /// </summary>
    private string GetRandomWeaponClassName(string prefix)
    {
        if (Api.Services is null) return "";
        string chosen = "";
        float total = 0f;
        foreach (Weapon w in Weapons.All)
        {
            if ((w.SpawnFlags & WeaponFlags.MutatorBlocked) != 0) continue;
            string spawnFunc = "weapon_" + w.NetName; // QC it.m_canonical_spawnfunc
            float prob = Api.Cvars.GetFloat($"g_{prefix}_{spawnFunc}_probability");
            if (prob <= 0f) continue;
            total += prob;
            if (Prandom.Float() * total <= prob) chosen = spawnFunc;
        }
        return chosen;
    }

    /// <summary>
    /// Port of <c>RandomItems_GetRandomItemClassNameWithProperty(prefix, item_property)</c>: weighted pick over the
    /// <see cref="Items"/> registry restricted to the def matching <paramref name="type"/> (health/armor/resource/
    /// powerup), normal-spawnflag and allowed (QC <c>Item_IsDefinitionAllowed</c>), keyed by
    /// <c>g_{prefix}_{m_canonical_spawnfunc}_probability</c>, returning that canonical classname.
    /// </summary>
    private string GetRandomItemClassNameWithProperty(string prefix, RandomItemType type)
    {
        if (Api.Services is null) return "";
        string chosen = "";
        float total = 0f;
        foreach (Pickup def in Items.All)
        {
            if (!MatchesType(def, type)) continue;
            // QC: (it.spawnflags & ITEM_FLAG_NORMAL) && Item_IsDefinitionAllowed(it).
            if ((def.ItemDef.SpawnFlags & GameItemSpawnFlag.Normal) == 0) continue;
            if (!def.ItemDef.IsAllowed) continue;
            string spawnFunc = ItemSpawnFuncs.CanonicalSpawnFunc(def); // QC it.m_canonical_spawnfunc
            float prob = Api.Cvars.GetFloat($"g_{prefix}_{spawnFunc}_probability");
            if (prob <= 0f) continue;
            total += prob;
            if (Prandom.Float() * total <= prob) chosen = spawnFunc;
        }
        return chosen;
    }

    // QC the instanceOf* predicate selected per type in the WithProperty switch (Health/Armor/Ammo/Powerup).
    private static bool MatchesType(Pickup def, RandomItemType type) => type switch
    {
        RandomItemType.Health => def.IsHealth,
        RandomItemType.Armor => def.IsArmor,
        RandomItemType.Resource => def.IsAmmo,
        RandomItemType.Powerup => def.IsPowerup && !def.IsWeaponPickup,
        _ => false,
    };

    /// <summary>QC <c>sprintf("g_%s_%s_probability", prefix, type)</c> — the per-type probability cvar name.</summary>
    private static string ProbCvar(string prefix, RandomItemType t) => t switch
    {
        RandomItemType.Health => $"g_{prefix}_health_probability",
        RandomItemType.Armor => $"g_{prefix}_armor_probability",
        RandomItemType.Resource => $"g_{prefix}_resource_probability",
        RandomItemType.Weapon => $"g_{prefix}_weapon_probability",
        RandomItemType.Powerup => $"g_{prefix}_powerup_probability",
        _ => "",
    };

    private static IEnumerable<RandomItemType> EachType(RandomItemType mask)
    {
        if ((mask & RandomItemType.Health) != 0) yield return RandomItemType.Health;
        if ((mask & RandomItemType.Armor) != 0) yield return RandomItemType.Armor;
        if ((mask & RandomItemType.Resource) != 0) yield return RandomItemType.Resource;
        if ((mask & RandomItemType.Weapon) != 0) yield return RandomItemType.Weapon;
        if ((mask & RandomItemType.Powerup) != 0) yield return RandomItemType.Powerup;
    }
}
