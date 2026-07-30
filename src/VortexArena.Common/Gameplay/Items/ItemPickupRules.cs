// Server-side item pickup + respawn — the C# successor to qcsrc/server/items/items.qc:
//   Item_GiveTo / Item_GiveAmmoTo (the type-agnostic give every item funnels through — QC Pickup.giveTo just
//   calls Item_GiveTo), Item_Touch (the FL_PICKUPITEMS / IS_DEAD / SOLID_TRIGGER / owner / spawnshield gate +
//   the give+respawn tail), Item_Show (the full model/solid/effects/STAYWEP + weapon-stay translucent path),
//   Item_Think (loot despawn), and the respawn scheduling (Item_ScheduleRespawn[In] / ScheduleInitialRespawn /
//   Respawn / RespawnThink / RespawnCountdown, with the player-count scaling + jitter).
//
// CSQC networking (the ENT_CLIENT_ITEM SendFlags, the waypoint-sprite countdown ping/sound) is out of scope;
// the respawn *timers*, the give *logic*, and the touch *gate* are ported faithfully. Resource gives flow
// through Resources.cs; weapon gives populate BOTH Entity.OwnedWeaponSet (Inventory) AND Player.OwnedWeapons
// (the NetName set — dual-rep).

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The item pickup + respawn rules (QC server/items/items.qc). Static, stateless apart from the shared
/// initial-respawn random (QC's <c>static float shared_random</c>); operates on world item entities and
/// the toucher.
/// </summary>
public static class ItemPickupRules
{
    /// <summary>QC ITEM_RESPAWN_TICKS — the waypoint countdown window before a long-respawn item reappears.</summary>
    public const float RespawnTicks = 10f;

    /// <summary>
    /// QC <c>IT_DESPAWNFX_TIME</c> (common/items/item.qh = 1.5) — the trailing window of a loot item's life during
    /// which it flags <c>ITS_EXPIRING</c> so the client plays the despawn animation (alpha fade + the accelerating
    /// <c>EFFECT_ITEM_DESPAWN</c> puffs). "enough to notice it's about to despawn and circle jump to grab it."
    /// </summary>
    public const float DespawnFxTime = 1.5f;

    /// <summary>QC DPCONTENTS_NODROP (the SUPERCONTENTS bit). Loot in a NODROP brush (lava) is deleted at spawn.</summary>
    public const int NoDropContents = unchecked((int)0x80000000);

    /// <summary>QC Q3SURFACEFLAG_SKY (q3 surface flag bit). Loot resting on a sky brush is killed on touch
    /// (ITEM_TOUCH_NEEDKILL's sky half — items.qh:122).</summary>
    public const int Q3SurfaceFlagSky = 0x4;

    private static float Now => Api.Services != null ? Api.Clock.Time : 0f;

    /// <summary>
    /// QC Item_ScheduleInitialRespawn's <c>static float shared_random</c> — for the default
    /// g_pickup_respawntime_initial_random==1 mode, every powerup scheduled in the same second draws the SAME
    /// random so they all appear at one synchronized time. Holds <c>floor(time) + random()</c>; the integer part
    /// is the floor(time) it was seeded at, so it is reused until floor(time) advances past it. Process-static
    /// to mirror the QC function-static lifetime.
    /// </summary>
    private static float _initialRespawnSharedRandom;

    // =====================================================================================
    //  Item_GiveTo — the type-agnostic give (items.qc:522). Reads the world item's resources / weapons /
    //  powerup timers / held-item flags and applies them to the player. Returns true if anything was taken.
    //  QC's per-item Pickup.giveTo just calls THIS, so there is one give path for every item kind.
    // =====================================================================================

    /// <summary>
    /// QC <c>Item_GiveTo(item, player)</c>: give the world item <paramref name="worldItem"/>'s contents to
    /// <paramref name="player"/> — the 7 resource gives (health/armor/shells/bullets/rockets/cells/fuel via
    /// <see cref="GiveAmmoTo"/>), the weapon block (each missing weapon granted, dual-rep), the IT_PICKUPMASK
    /// held-item transfer, and the powerup status-effect timers. Returns true if the player benefited
    /// (QC <c>pickedup</c>). Does NOT play the pickup sound or schedule the respawn — that is the Item_Touch tail.
    /// </summary>
    public static bool ItemGiveTo(Entity worldItem, Entity player)
    {
        int pickupAnyway = System.Math.Max(worldItem.PickupAnyway, worldItem.Pickup?.ItemDef.PickupAnyway ?? 0);
        bool pickedUp = false;

        // QC Item_GiveTo head (items.qc:525-545): if the player has cl_autoswitch on (and CTS-autoswitch is not
        // overriding it), remember whether they're currently on their best weapon so we can re-pick a better one
        // AFTER the give. Captured before any weapon is granted. CTS uses a separate forced-switch policy handled
        // in the tail. The port is single-slot (no MAX_WEAPONSLOTS loop) — slot 0 is the whole decision.
        bool useCtsAutoswitch = IsCtsActive() && worldItem.Pickup?.IsWeaponPickup == true && CtsAutoswitchCvar(player) != -1;
        bool wantSwitch = false;
        if (AutoswitchEnabled(player) && !useCtsAutoswitch)
        {
            int switchId = player.SwitchWeaponId >= 0 ? player.SwitchWeaponId : player.ActiveWeaponId;
            Weapon? best = Inventory.GetBestWeapon(player);
            // QC: switch if their switchweapon already IS the best (a better one may now exist), OR they somehow
            // don't even own their switchweapon.
            if (best is not null && switchId == best.RegistryId)
                wantSwitch = true;
            if (switchId < 0 || !player.OwnedWeaponSet.Has(switchId))
                wantSwitch = true;
        }

        // QC: the seven Item_GiveAmmoTo calls (resource limits from the per-item caps + the pickup-max cvars).
        pickedUp |= GiveAmmoTo(worldItem, player, ResourceType.Health, worldItem.GetResource(ResourceType.Health),
            worldItem.MaxHealth, pickupAnyway);
        pickedUp |= GiveAmmoTo(worldItem, player, ResourceType.Armor, worldItem.GetResource(ResourceType.Armor),
            worldItem.MaxArmorValue, pickupAnyway);
        pickedUp |= GiveAmmoTo(worldItem, player, ResourceType.Shells, worldItem.GetResource(ResourceType.Shells),
            CvarOr("g_pickup_shells_max", 60f), pickupAnyway);
        pickedUp |= GiveAmmoTo(worldItem, player, ResourceType.Bullets, worldItem.GetResource(ResourceType.Bullets),
            CvarOr("g_pickup_nails_max", 320f), pickupAnyway);
        pickedUp |= GiveAmmoTo(worldItem, player, ResourceType.Rockets, worldItem.GetResource(ResourceType.Rockets),
            CvarOr("g_pickup_rockets_max", 160f), pickupAnyway);
        pickedUp |= GiveAmmoTo(worldItem, player, ResourceType.Cells, worldItem.GetResource(ResourceType.Cells),
            CvarOr("g_pickup_cells_max", 180f), pickupAnyway);
        pickedUp |= GiveAmmoTo(worldItem, player, ResourceType.Fuel, worldItem.GetResource(ResourceType.Fuel),
            CvarOr("g_pickup_fuel_max", 100f), pickupAnyway);

        // QC weapon block: grant each weapon the player lacks (or re-grant when pickup_anyway + live).
        if (worldItem.Pickup?.IsWeaponPickup == true)
            pickedUp |= InventoryPickupItem(player, worldItem, worldItem.OwnedWeaponSet, pickupAnyway);

        // QC powerup-item center-prints (items.qc:581-587): a FuelRegen / Jetpack powerup the player doesn't yet
        // hold announces itself. QC keys these off the itemdef AND !(player.items & flag); the held-flag check is
        // exactly "this flag is newly granted", so we fold it into the IT_PICKUPMASK transfer below (its carries
        // only the not-yet-held flags). Done before the transfer so player.Items still lacks the flag here.
        if (worldItem.Pickup?.IsPowerup == true)
        {
            int newFlags = worldItem.Items & ~player.Items;
            if ((newFlags & (int)ItemFlag.FuelRegen) != 0)
                NotificationSystem.Center(player, "ITEM_FUELREGEN_GOT");
            else if ((newFlags & (int)ItemFlag.Jetpack) != 0)
                NotificationSystem.Center(player, "ITEM_JETPACK_GOT");
        }

        // QC IT_PICKUPMASK transfer: the held-item flags this item grants that the player lacks (jetpack /
        // fuelregen / unlimited*). strength/invincible are NOT in the mask — they go through the timers below.
        int its = worldItem.Items & ~player.Items & (int)ItemFlag.PickupMask;
        if (its != 0)
        {
            player.Items |= its;
            pickedUp = true;
        }

        // QC powerup timers (strength/invincible/speed/invisibility/superweapon -> StatusEffects_apply).
        pickedUp |= ApplyPowerupTimers(player, worldItem);

        // QC: always eat teamed entities (if(item.team) pickedup = true).
        if (worldItem.Team != 0f) pickedUp = true;

        if (!pickedUp)
            return false;

        // QC Item_GiveTo tail (items.qc:652-681): now that the give happened, perform the autoswitch.
        if (useCtsAutoswitch)
        {
            // CTS handling: cl_autoswitch_cts 1 = always switch to the picked-up weapon (if usable), -1/0 handled
            // above (use_cts_autoswitch false / never switch). Crude force-switch like QC.
            if (CtsAutoswitchCvar(player) == 1)
            {
                foreach (var w in worldItem.OwnedWeaponSet.Weapons())
                {
                    if (Inventory.ClientHasWeapon(player, w, andAmmo: true, complain: false))
                        Inventory.SwitchWeapon(player, w); // W_SwitchWeapon_Force
                }
            }
        }
        else if (wantSwitch)
        {
            // non-CTS: weaponpriority-based autoswitch to the (possibly new) best weapon.
            Weapon? best = Inventory.GetBestWeapon(player);
            int curSwitch = player.SwitchWeaponId >= 0 ? player.SwitchWeaponId : player.ActiveWeaponId;
            if (best is not null && curSwitch != best.RegistryId)
                Inventory.SwitchWeapon(player, best); // W_SwitchWeapon_Force
        }

        return true;
    }

    // =====================================================================================
    //  Autoswitch seams (QC CS_CVAR(player).cvar_cl_autoswitch / cvar_cl_autoswitch_cts + g_cts). These are
    //  per-client replicated cvars + a gametype query the host owns; mirror the Inventory.PriorityProvider
    //  pattern so the listen/local path works headless (no autoswitch) and the server wires the real source.
    // =====================================================================================

    /// <summary>
    /// QC <c>CS_CVAR(player).cvar_cl_autoswitch</c> — the per-client auto-weapon-switch flag. Set by the server's
    /// Commands (it owns the replicated per-client cvar table); null = no provider wired (headless/local), in
    /// which case autoswitch is off (matching a player who never replicated the cvar). Takes the entity so a
    /// future multi-actor host can disambiguate.
    /// </summary>
    public static Func<Entity, bool>? AutoswitchProvider;

    /// <summary>
    /// QC <c>CS_CVAR(player).cvar_cl_autoswitch_cts</c> — the per-client CTS autoswitch override (-1 = unset/use
    /// the normal autoswitch, 0 = never switch, 1 = always switch to the picked-up weapon). Null provider yields
    /// -1 (no override). Wired by the server alongside <see cref="AutoswitchProvider"/>.
    /// </summary>
    public static Func<Entity, int>? CtsAutoswitchProvider;

    /// <summary>QC <c>g_cts</c> — is the CTS gametype active? Null provider = false (the common DM/other case).</summary>
    public static Func<bool>? CtsActiveProvider;

    private static bool AutoswitchEnabled(Entity player) => AutoswitchProvider?.Invoke(player) ?? false;
    private static int CtsAutoswitchCvar(Entity player) => CtsAutoswitchProvider?.Invoke(player) ?? -1;
    private static bool IsCtsActive() => CtsActiveProvider?.Invoke() ?? false;

    /// <summary>
    /// Legacy entry retained for callers that pass an explicit pickup def (target_give host seam): give the
    /// world item to the player via the canonical <see cref="ItemGiveTo"/>, then — if anything was taken — play
    /// the pickup sound and schedule the respawn (the Item_Touch tail). Prefer <see cref="ItemTouch"/> for the
    /// full touch path; this is the give+tail without the FL_PICKUPITEMS gate.
    /// </summary>
    public static bool GiveTo(Pickup pickup, Entity player, Entity worldItem)
    {
        if (worldItem.Pickup is null) worldItem.Pickup = pickup;
        bool pickedUp = ItemGiveTo(worldItem, player);
        if (!pickedUp) return false;
        PlayPickupSound(worldItem, player);
        ScheduleRespawnAfterPickup(worldItem);
        return true;
    }

    // QC Item_GiveAmmoTo (items.qc:485): give a resource toward a cap, honouring the live/stay marker +
    // pickup_anyway + the g_weapon_stay==2 stay-ammo branch.
    /// <summary>
    /// Give <paramref name="amount"/> of <paramref name="res"/> to <paramref name="player"/> up to
    /// <paramref name="cap"/> (QC Item_GiveAmmoTo). When the item is LIVE (<see cref="Entity.SpawnShieldExpire"/>
    /// != 0) and the player is at/over the cap, nothing is given unless <paramref name="pickupAnyway"/> &gt; 0.
    /// When the item is a STAY weapon (marker == 0) and <c>g_weapon_stay==2</c>, the cap collapses to the item's
    /// own amount (the stay-ammo refill); otherwise a stay weapon gives no ammo at all. Returns true if any was
    /// given. Supports a negative <paramref name="amount"/> (take) like QC.
    /// </summary>
    public static bool GiveAmmoTo(Entity worldItem, Entity player, ResourceType res, float amount, float cap,
        int pickupAnyway)
    {
        if (amount == 0f) return false;
        float playerAmount = player.GetResource(res);

        if (worldItem.SpawnShieldExpire != 0f)
        {
            // live item: refuse if already capped (unless pickup_anyway).
            if (cap != Resources.LimitNone && playerAmount >= cap && pickupAnyway <= 0)
                return false;
        }
        else if (WeaponStay == 2)
        {
            // QC g_weapon_stay==2 stay-ammo path: cap at min(item amount, cap); refuse if already that full.
            cap = System.MathF.Min(amount, cap == Resources.LimitNone ? amount : cap);
            if (playerAmount >= cap) return false;
        }
        else
        {
            // stay weapon (marker 0) and not g_weapon_stay==2: no ammo from a ghost weapon.
            return false;
        }

        if (amount < 0f)
            // QC Item_GiveAmmoTo (items.qc:507): TakeResourceWithLimit(player, res, -amount, ammomax) — the drain
            // is floored at -ammomax, matching the give branch's ceiling. `cap` is the (possibly stay-adjusted)
            // ammomax computed above, passed verbatim exactly like QC (no stock item carries a negative amount, so
            // this is a faithful-but-latent path; cap is never LimitNone here — only the give branch reaches that).
            player.TakeResourceWithLimit(res, -amount, cap);
        else
            player.GiveResourceWithLimit(res, amount, cap);
        return true;
    }

    // QC Item_GiveTo powerup block: each *_finished field on the world item applies the matching status
    // effect, stacking or refreshing per g_powerups_stack.
    private static bool ApplyPowerupTimers(Entity player, Entity item)
    {
        bool any = false;
        bool stack = Api.Services != null && Api.Cvars.GetFloat("g_powerups_stack") != 0f;

        any |= ApplyTimer(player, item.StrengthFinished, stack, StatusEffectsCatalog.ByName("strength"));
        any |= ApplyTimer(player, item.InvincibleFinished, stack, StatusEffectsCatalog.ByName("shield"));
        any |= ApplyTimer(player, item.SpeedFinished, stack, StatusEffectsCatalog.ByName("speed"));
        any |= ApplyTimer(player, item.InvisibilityFinished, stack, StatusEffectsCatalog.ByName("invisibility"));
        // QC superweapons always stack (no g_powerups_stack gate): t = existing + item.superweapons_finished.
        any |= ApplySuperweaponTimer(player, item.SuperweaponsFinished, StatusEffectsCatalog.ByName("superweapon"));
        return any;
    }

    private static bool ApplyTimer(Entity player, float duration, bool stack, StatusEffectDef? def)
    {
        if (duration <= 0f || def is null) return false;
        float existing = ExistingRemaining(player, def);
        // QC: stack => total = existing + duration; else => total = max(existing, duration). The catalog Apply
        // sets ExpireTime = now + total, so pass the resulting total duration.
        float total = stack ? existing + duration : System.MathF.Max(existing, duration);
        StatusEffectsCatalog.Apply(player, def, total);
        return true;
    }

    // QC superweapons: StatusEffects_apply(t + item.superweapons_finished) — unconditional add to the existing.
    private static bool ApplySuperweaponTimer(Entity player, float duration, StatusEffectDef? def)
    {
        if (duration <= 0f || def is null) return false;
        float existing = ExistingRemaining(player, def);
        StatusEffectsCatalog.Apply(player, def, existing + duration);
        return true;
    }

    private static float ExistingRemaining(Entity player, StatusEffectDef def)
    {
        foreach (var s in player.StatusEffects)
            if (s.DefId == def.RegistryId) return System.MathF.Max(0f, s.ExpireTime - Now);
        return 0f;
    }

    // =====================================================================================
    //  Inventory_pickupitem — weapon pickups (items.qc Item_GiveTo weapon block, via the Inventory API).
    // =====================================================================================

    /// <summary>
    /// Give the weapons carried by world item <paramref name="worldItem"/> to <paramref name="player"/> —
    /// the C# successor to the weapon block of Item_GiveTo. Each weapon the player doesn't already own is
    /// granted; with m_pickupanyway set and the item live, owned weapons are re-granted too. Populates BOTH
    /// <see cref="Entity.OwnedWeaponSet"/> (via <see cref="Inventory.GiveWeapon"/>) AND <see cref="Player.OwnedWeapons"/>
    /// (the NetName set) — the dual-rep contract. Returns true if any weapon was granted.
    /// </summary>
    public static bool InventoryPickupItem(Entity player, Entity worldItem, WepSet itemWeapons, int pickupAnyway)
    {
        WepSet have = Inventory.GetWeapons(player);
        WepSet missing = itemWeapons & Complement(have);             // wp = w & ~player.weapons

        bool grant = !missing.IsEmpty || (worldItem.SpawnShieldExpire != 0f && pickupAnyway > 0);
        if (!grant) return false;

        foreach (var w in itemWeapons.Weapons())
        {
            if (missing.Has(w) || pickupAnyway > 0)
            {
                Inventory.GiveWeapon(player, w);                     // W_GiveWeapon (writes Entity.OwnedWeaponSet)
                // dual-rep: also add to Player.OwnedWeapons (the NetName set) — QC STAT(WEAPONS) is one bitset, but
                // the port keeps the player's NetName set separately (memory: must populate BOTH).
                if (player is Player p) p.OwnedWeapons.Add(w.NetName);
                // Notify the player they got the weapon (QC Item_NotifyWeapon -> INFO_ITEM_WEAPON_GOT).
                NotificationSystem.Send(NotifBroadcast.OneOnly, player, MsgType.Info, "ITEM_WEAPON_GOT", w.RegistryId);
            }
        }
        return true;
    }

    private static WepSet Complement(WepSet s)
    {
        // WepSet has no ~ operator; build the complement over the 64-bit space via the known weapon ids.
        var result = new WepSet();
        for (int i = 0; i < 64; i++)
            if (!s.Has(i)) result.Add(i);
        return result;
    }

    // =====================================================================================
    //  Item_Touch — the full touch gate + give + respawn tail (items.qc:686).
    // =====================================================================================

    /// <summary>
    /// QC <c>Item_Touch(this, toucher)</c>: the world-item touch handler set by <see cref="StartItem.Spawn"/>.
    /// Gate: the loot NODROP/sky-surface kill; then the FL_PICKUPITEMS / IS_DEAD / SOLID_TRIGGER /
    /// owner!=toucher / past-spawnshield checks. On pass: (optionally subtract <c>time</c> from an expiring
    /// item's timers), run the canonical <see cref="ItemGiveTo"/>, then — if anything was taken — fire the
    /// item's own targets, play the pickup sound, and either remove loot or schedule the (non-stay) respawn.
    /// </summary>
    public static void ItemTouch(Entity item, Entity toucher)
    {
        // (1) loot in a NODROP brush / on a sky surface is removed on touch (QC ITEM_TOUCH_NEEDKILL).
        if (item.ItemIsLoot && LootInNoDrop(item))
        {
            RemoveItem(item);
            return;
        }

        // (2) the QC gate: only a live, alive, non-owner picker, on a SOLID_TRIGGER item, past its spawnshield.
        if (!toucher.CanPickupItems
            || toucher.DeadState != DeadFlag.No
            || item.Solid != Solid.Trigger
            || ReferenceEquals(item.Owner, toucher)
            || Now < item.ItemSpawnShieldExpire)
        {
            return;
        }

        // (3) MUTATOR_CALLHOOK(ItemTouch, this, toucher) (items.qc:706) — fired here, after the gate and BEFORE
        //     the expiring-timer adjust + give, so a subscriber sees the item's raw powerup timers. QC
        //     Item_Touch does `if (MUTATOR_CALLHOOK(ItemTouch, this, toucher)) return;` — a handler returning
        //     MUT_ITEMTOUCH_RETURN (true) ABORTS the pickup (Freeze Tag blocks a frozen toucher; the stock
        //     superspec hook always returns CONTINUE so the common path proceeds).
        if (MutatorHooks.FireItemTouch(item, toucher))
            return;

        // (4) an expiring loot item's powerup timers are stored absolute-from-now; subtract `time` so the give's
        //     max(t, time + finished) treats them as remaining (QC items.qc:714-721). Restored if nothing taken.
        bool expiring = item.ItemIsExpiring;
        if (expiring)
        {
            item.StrengthFinished = System.MathF.Max(0f, item.StrengthFinished - Now);
            item.InvincibleFinished = System.MathF.Max(0f, item.InvincibleFinished - Now);
            item.SpeedFinished = System.MathF.Max(0f, item.SpeedFinished - Now);
            item.InvisibilityFinished = System.MathF.Max(0f, item.InvisibilityFinished - Now);
            item.SuperweaponsFinished = System.MathF.Max(0f, item.SuperweaponsFinished - Now);
        }

        bool gave = ItemGiveTo(item, toucher);
        if (!gave)
        {
            if (expiring)
            {
                // undo the timer adjust (QC items.qc:725-733).
                item.StrengthFinished += Now;
                item.InvincibleFinished += Now;
                item.SpeedFinished += Now;
                item.InvisibilityFinished += Now;
                item.SuperweaponsFinished += Now;
            }
            return;
        }

        // ----- the give+respawn tail (LABEL pickup) -----

        // QC: fire the item's own targets (unless the ###item### findnearest sentinel).
        if (!string.IsNullOrEmpty(item.Target) && item.Target != "###item###")
            MapMover.UseTargets(item, toucher, null);

        PlayPickupSound(item, toucher);

        // QC client/items/items.qc: the pickup particle burst plays as the item goes unavailable — for a
        // respawning item via the ITS_AVAILABLE→off transition (:271), for loot via ISF_REMOVEFX (:338).
        // Both funnel through here, so a single emit before the loot/respawn split covers both.
        EmitItemEffect("ITEM_PICKUP", item);

        // QC (items.qc:746): MUTATOR_CALLHOOK(ItemTouched, this, toucher); if (wasfreed(this)) return; — fired
        // AFTER the give + pickup sound. random_items re-randomizes the picked-up MAP item here: it spawns a fresh
        // replacement, schedules the replacement's respawn, and deletes THIS item — so the re-check below bails.
        MutatorHooks.FireItemTouched(item, toucher);
        if (item.IsFreed)
            return;

        // QC: loot is removed (no respawn).
        if (item.ItemIsLoot)
        {
            RemoveItem(item);
            return;
        }

        // QC: a stay weapon (spawnshieldtime == 0) already gave only the weapon — no respawn.
        if (item.SpawnShieldExpire == 0f)
            return;

        // QC items.qc:762-780: if(this.team) pick a random sibling of the same team to respawn (hide all,
        // then show and schedule the one that was randomly chosen — re-rolled each pickup so successive picks
        // can land on different members). For an un-teamed item just respawn this one.
        if (item.Team != 0f)
        {
            // QC: RandomSelection_Init(); IL_EACH(g_items, it.team == this.team, …hide + add to selection…);
            // e = RandomSelection_chosen_ent; Item_Show(e, 1); Item_ScheduleRespawn(e);
            var sel = new MapMover.RandomSelection();
            sel.Reset();
            if (Api.Services is not null && Api.Entities.All is { } all)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    Entity it = all[i];
                    if (!it.IsFreed && it.Team == item.Team && it.ItemDefRef is not null)
                    {
                        // QC: Item_Show(it, -1); scheduledrespawntime = 0; then add to random selection.
                        Show(it, -1);
                        it.ScheduledRespawnTime = 0f;
                        sel.Add(it, it.Cnt != 0 ? it.Cnt : 1, 0f);
                    }
                }
            }
            else
            {
                // headless / no All list: fall back to just this item (safe stub — QC behaviour requires
                // iterating g_items; the live path is always covered by the Api.Entities.All branch).
                sel.Add(item, item.Cnt != 0 ? item.Cnt : 1, 0f);
            }

            Entity? e = sel.Chosen ?? item;
            // QC: Item_Show(e, 1) — reset the chosen item's state so it is visible (extra sendflags ok).
            Show(e, 1);
            ScheduleRespawn(e);
        }
        else
        {
            ScheduleRespawn(item);
        }
    }

    // =====================================================================================
    //  Item_Show — the full visibility/solidity/effects toggle (items.qc:130).
    // =====================================================================================

    /// <summary>
    /// QC <c>Item_Show(e, mode)</c>: <paramref name="mode"/> &gt; 0 = available (model + SOLID_TRIGGER, live
    /// marker set), &lt; 0 = hidden (no model, SOLID_NOT), == 0 = the weapon-stay case (a weapon pickup with
    /// g_weapon_stay becomes a translucent STILL-pickable ghost with the live marker CLEARED — picking it up
    /// gives only the weapon, not ammo; everything else becomes hidden). Relinks via setorigin so the area grid
    /// updates. The live/stay marker is <see cref="Entity.SpawnShieldExpire"/> (QC <c>.spawnshieldtime</c>).
    /// </summary>
    public static void Show(Entity e, int mode)
    {
        // QC items.qc:131 clears EF_ADDITIVE|EF_STARDUST|EF_FULLBRIGHT|EF_NODEPTHTEST + ITS_STAYWEP each call,
        // then re-derives the status bits below. All four EF_* bits are mirrored in the port's EffectFlags and
        // honoured by the client (CsqcModelEffects.Apply: additive/fullbright/nodepthtest render the item model).
        e.Effects &= ~(EffectFlags.Additive | EffectFlags.Stardust | EffectFlags.FullBright | EffectFlags.NoDepthTest);
        e.ItemStayWeapon = false;

        if (mode > 0)
        {
            // normal, touchable.
            e.Model = e.ItemWorldModel ?? "";
            e.Solid = Solid.Trigger;
            e.SpawnShieldExpire = 1f;
            e.ItemAvailable = true;
        }
        else if (mode < 0)
        {
            // fully hidden.
            e.Model = "";
            e.Solid = Solid.Not;
            e.SpawnShieldExpire = 1f;
            e.ItemAvailable = false;
        }
        else
        {
            // mode == 0: the weapon-stay case.
            bool isWeapon = e.Pickup?.IsWeaponPickup == true;
            // QC items.qc:153-155 has an operator-precedence quirk: `?:` binds looser than `||`, so
            //   nostay = instanceOfWeaponPickup ? (isSuperweapon) : (false || e.team)
            // For a WEAPON pickup nostay is the superweapon bit ONLY — the `|| e.team` term is bound to the
            // non-weapon branch and is unreachable here (and the whole nostay value is only consumed when
            // isWeapon is true). Mirror the shipped behavior: drop the team term so teamed non-super weapons
            // still weapon-stay (as they do in Base).
            bool nostay = isWeapon && IsSuperWeaponItem(e);
            if (isWeapon && !nostay && WeaponStay != 0)
            {
                // translucent, STILL pickable, but the live marker is CLEARED (gives only the weapon, no ammo).
                e.Model = e.ItemWorldModel ?? "";
                e.Solid = Solid.Trigger;
                e.Effects |= EffectFlags.Stardust;
                e.SpawnShieldExpire = 0f; // 0 = stay marker (no ammo, no respawn)
                e.ItemAvailable = true;
                e.ItemStayWeapon = true;
            }
            else
            {
                e.Solid = Solid.Not;
                e.SpawnShieldExpire = 1f;
                e.ItemAvailable = false;
            }
        }

        // QC Item_Show tail (items.qc:176-189): re-derive the status/effect bits each call.
        //
        // The port has no networked ItemStatus byte (ITS_GLOW/ITS_ALLOWFB/ITS_ALLOWSI) — those CSQC status bits
        // exist in Base only to be translated into render EF_* flags client-side (client/items/items.qc:242-269:
        // ITS_ALLOWFB->EF_FULLBRIGHT, ITS_GLOW&AVAILABLE->EF_ADDITIVE|EF_FULLBRIGHT). The port emits the equivalent
        // EF_* directly on the world item, which the client already honours (CsqcModelEffects), so the rendered
        // result matches without the intermediate status byte.
        bool available = e.ItemAvailable;

        // ITS_GLOW (def.m_glow — powerups): CSQC adds EF_ADDITIVE|EF_FULLBRIGHT only while the item is AVAILABLE
        // (client/items/items.qc:255). Apply the same additive glow here when available.
        if (e.Pickup?.ItemDef.Glow == true && available)
            e.Effects |= EffectFlags.Additive | EffectFlags.FullBright;

        // EF_NODEPTHTEST (autocvar_g_nodepthtestitems): draw the item through walls. Set directly on .effects in
        // QC's Item_Show (items.qc:181) — a pure render bit honoured by CsqcModelEffects.SetNoDepthTest.
        if (CvarOr("g_nodepthtestitems", 0f) != 0f)
            e.Effects |= EffectFlags.NoDepthTest;

        // ITS_ALLOWFB (autocvar_g_fullbrightitems): the server-permits-fullbright bit. CSQC turns it into
        // EF_FULLBRIGHT unconditionally (client/items/items.qc:242). Apply EF_FULLBRIGHT here when enabled.
        if (CvarOr("g_fullbrightitems", 0f) != 0f)
            e.Effects |= EffectFlags.FullBright;

        // ITS_ALLOWSI (autocvar_sv_simple_items, Base default 1): permits the client to swap to the flat _simple
        // sprite (client/items/items.qc:78). The port has no _simple model-swap pipeline (no client model
        // resolver for the *_simple variants, no bob suppression), so this bit has no render effect yet; left
        // unset rather than faked. See the items-pickups simple-items gap.

        // QC: relink (solid may have changed) — setorigin(e, e.origin) updates the area grid link.
        if (Api.Services is not null)
            Api.Entities.SetOrigin(e, e.Origin);
    }

    /// <summary>Convenience: Show(item, available?1:-1). Kept for the existing respawn-scheduling call sites.</summary>
    public static void Show(Entity item, bool available) => Show(item, available ? 1 : -1);

    // =====================================================================================
    //  Item_Think — loot despawn (items.qc:192). Permanent items just relink on move.
    // =====================================================================================

    /// <summary>
    /// QC <c>Item_Think(this)</c> (server/items/items.qc:192): a loot item counts down to its
    /// <see cref="Entity.ItemWait"/> despawn time. While more than <see cref="DespawnFxTime"/> seconds of life
    /// remain it just re-ticks (never overshooting the window start, "ensuring full time for effects"); during the
    /// final <see cref="DespawnFxTime"/> seconds it raises <see cref="Entity.ItemExpiringFx"/> (QC
    /// <c>ItemStatus |= ITS_EXPIRING; SendFlags |= ISF_STATUS</c>) so the client runs the despawn animation, then
    /// hands off to <see cref="RemoveItem"/> at the wait time. A permanent item just keeps its think alive (the
    /// CSQC slow-update is delta-driven networking, handled by the snapshot system). Set as a loot item's think by
    /// <see cref="StartItem.Spawn"/>.
    /// </summary>
    public static void ItemThink(Entity item)
    {
        if (item.ItemIsLoot)
        {
            // QC items.qc:196 — still outside the despawn-fx window: re-tick at IT_UPDATE_INTERVAL but never past
            // the window start, so the client gets the full DespawnFxTime for the fade + puffs.
            if (Now < item.ItemWait - DespawnFxTime)
            {
                item.NextThink = System.MathF.Min(Now + ItemUpdateInterval, item.ItemWait - DespawnFxTime);
            }
            else
            {
                // QC items.qc:200 — despawning soon: flag ITS_EXPIRING (networked via the item snapshot's
                // NetEntityFlags.ItemExpiring, picked up by the delta compressor) so the client begins the
                // alpha fade + the accelerating EFFECT_ITEM_DESPAWN puffs (client/items/items.qc:191-210).
                item.ItemExpiringFx = true;

                if (Now < item.ItemWait - ItemUpdateInterval)
                {
                    item.NextThink = Now + ItemUpdateInterval;
                }
                else
                {
                    // QC: setthink(this, RemoveItem); nextthink = this.wait. Remove exactly at the wait time; if a
                    // late/direct think has already reached it, remove now (no spurious extra tick).
                    item.Think = RemoveItem;
                    item.NextThink = item.ItemWait;
                    if (Now >= item.ItemWait)
                        RemoveItem(item);
                }
            }
        }
        else
        {
            item.NextThink = Now;
        }
    }

    /// <summary>
    /// QC <c>IT_UPDATE_INTERVAL</c> (common/items/item.qh = 0.0625) — the loot-think cadence: "2hz probably enough
    /// to correct a desync caused by serious lag" (the comment is stale; it's 16hz). Also the boundary the despawn
    /// window's final-removal branch uses (<c>wait - IT_UPDATE_INTERVAL</c>).
    /// </summary>
    public const float ItemUpdateInterval = 0.0625f;

    // =====================================================================================
    //  Respawn scheduling (items.qc Item_ScheduleRespawn[In] / Item_ScheduleInitialRespawn).
    // =====================================================================================

    /// <summary>QC adjust_respawntime: scale the base respawn time by the player-count curve cvars.</summary>
    public static float AdjustRespawnTime(float normalRespawnTime, int playerCount)
    {
        float r = CvarOr("g_pickup_respawntime_scaling_reciprocal", 0f);
        float o = CvarOr("g_pickup_respawntime_scaling_offset", 0f);
        float l = CvarOr("g_pickup_respawntime_scaling_linear", 1f);
        if (r == 0f && l == 1f) return normalRespawnTime;
        if (playerCount >= 2) return normalRespawnTime * (r / (playerCount + o) + l);
        return normalRespawnTime;
    }

    /// <summary>
    /// Schedule <paramref name="item"/> to respawn in <paramref name="t"/> seconds (QC Item_ScheduleRespawnIn):
    /// long respawns (&gt; ITEM_RESPAWN_TICKS) start a countdown think; short ones respawn directly.
    /// </summary>
    public static void ScheduleRespawnIn(Entity item, float t)
    {
        // QC items.qc:328-329: route the item onto the respawn-COUNTDOWN (waypoint) path only when
        // (set_itemstime || the Item_ScheduleRespawn mutator hook) && (t - ITEM_RESPAWN_TICKS) > 0. set_itemstime
        // = Item_ItemsTime_Allow(itemdef) || superweapons — i.e. powerups, Mega/Big Health+Armor, superweapons.
        // Every OTHER pickup (small/medium health+armor, ammo, ordinary weapons) takes the plain no-waypoint
        // respawn path, so e.g. a small health shard does NOT get an "ITEM" respawn marker (playtest-bugs #12).
        bool setItemsTime = ItemstimeMutator.IsItemsTimeAllowed(item);
        bool hook = MutatorHooks.FireItemScheduleRespawn(item, t);
        if ((setItemsTime || hook) && t - RespawnTicks > 0f)
        {
            item.NextThink = Now + System.MathF.Max(0f, t - RespawnTicks);
            item.ScheduledRespawnTime = item.NextThink + RespawnTicks;
            item.ItemRespawnCounter = 0;
            item.Think = RespawnCountdown;
        }
        else
        {
            item.NextThink = Now;
            item.ScheduledRespawnTime = Now + t;
            item.ItemWait = Now + t;
            item.Think = RespawnThink;
        }
    }

    /// <summary>
    /// QC Item_ScheduleRespawn: hide the item and queue a respawn at base±jitter scaled by player count. A
    /// respawn time of -1 (or 0 in the port's "use default" convention) means "never respawns".
    /// </summary>
    public static void ScheduleRespawn(Entity item, int playerCount = 0)
    {
        if (item.ItemRespawnTime > 0f)
        {
            Show(item, 0); // QC Item_Show(e, 0): the weapon-stay-aware hide
            float adjusted = AdjustRespawnTime(item.ItemRespawnTime, playerCount);
            float respawnIn = adjusted + Prandom.Signed() * item.ItemRespawnTimeJitter; // crandom() == [-1,1)
            ScheduleRespawnIn(item, respawnIn);
        }
        else
        {
            Show(item, -1); // -1 => never respawns
        }
    }

    /// <summary>
    /// QC Item_ScheduleInitialRespawn: an item that doesn't start spawned (a powerup or superweapon) is hidden
    /// and queued for its first appearance after a randomized delay, offset by the game start time. Powerups
    /// route here from Item_Reset (do NOT spawn powerups at match start).
    /// </summary>
    public static void ScheduleInitialRespawn(Entity item, float gameStartTime, int playerCount = 0)
    {
        Show(item, 0);

        // QC autocvar_g_pickup_respawntime_initial_random (shipped default 1): 0 = respawntime + random*jitter;
        // 1 = ITEM_RESPAWN_TICKS .. respawntime+jitter via a SHARED random (all items scheduled the same second
        // appear together); 2 = same range but an independent random per item.
        int initialRandom = (int)CvarOr("g_pickup_respawntime_initial_random", 1f);
        float spawnIn;
        if (initialRandom == 0)
        {
            // range: respawntime .. respawntime + respawntimejitter
            spawnIn = item.ItemRespawnTime + Prandom.Float() * item.ItemRespawnTimeJitter;
        }
        else
        {
            float rnd;
            if (initialRandom == 1)
            {
                // QC: this works only if items are scheduled at the same time (the normal case). random() can't
                // return exactly 1, so floor(time) > shared_random correctly fires once floor(time) advances.
                float flo = System.MathF.Floor(Now);
                if (_initialRespawnSharedRandom == 0f || flo > _initialRespawnSharedRandom)
                    _initialRespawnSharedRandom = flo + Prandom.Float();
                rnd = _initialRespawnSharedRandom - flo;
            }
            else
            {
                rnd = Prandom.Float();
            }

            // range (prevents powerups spawning unexpectedly without waypoints):
            //   respawntime >= ITEM_RESPAWN_TICKS: ITEM_RESPAWN_TICKS .. respawntime + respawntimejitter
            //   else: 0 .. ITEM_RESPAWN_TICKS
            spawnIn = RespawnTicks + rnd * (item.ItemRespawnTime + item.ItemRespawnTimeJitter - RespawnTicks);
        }

        float delay = System.MathF.Max(0f, gameStartTime - Now)
            + (item.ItemRespawnTimeStart != 0f ? item.ItemRespawnTimeStart : spawnIn);
        ScheduleRespawnIn(item, System.MathF.Max(0f, delay));
    }

    /// <summary>QC Item_Respawn: make the item available again and clear the schedule.</summary>
    public static void Respawn(Entity item)
    {
        Show(item, 1);
        // QC client/items/items.qc:258 — the respawn sparkle on the ITS_AVAILABLE→on transition. Only real
        // respawns (and a powerup's first scheduled appearance) reach Respawn; a normal item's initial spawn
        // shows via Show(item, 1) directly in StartItem, so this never double-fires on map load.
        EmitItemEffect("ITEM_RESPAWN", item);
        // Drop any respawn-countdown waypoint sprite (QC kills it in Item_RespawnCountdown before Respawn; this
        // also covers the rare paths that reach Respawn without finishing the countdown tick).
        if (item.WaypointAttached is Waypoints.WaypointSprite wp)
        {
            Waypoints.WaypointSprites.Kill(wp);
            item.WaypointAttached = null;
        }
        item.ScheduledRespawnTime = 0f;
        // a short spawn-shield so it can't be insta-grabbed the same frame it appears.
        item.ItemSpawnShieldExpire = Now;
        item.NextThink = Now;
        item.Think = ItemThink;
    }

    // QC Item_RespawnThink: respawn once the wait time elapses.
    private static void RespawnThink(Entity item)
    {
        item.NextThink = Now;
        if (Now >= item.ItemWait) Respawn(item);
    }

    // QC Item_RespawnCountdown (items.qc:264-308): tick the per-second respawn countdown, then respawn when the
    // tick budget (ITEM_RESPAWN_TICKS) is spent. On the FIRST tick spawn a respawn-countdown waypoint sprite
    // attached to the item — WP_Weapon for weapon pickups, WP_Item for everything else — and, for the itemstime
    // SpectatorOnly items (Mega/Big Health+Armor), restrict it to SPRITERULE_SPECTATOR. Then EVERY tick: for each
    // client the waypoint is visible to, play the ITEMRESPAWNCOUNTDOWN sound, and ping the waypoint (radar pulse).
    private static void RespawnCountdown(Entity item)
    {
        if (item.ItemRespawnCounter >= (int)RespawnTicks)
        {
            // QC: WaypointSprite_Kill(this.waypointsprite_attached) before Item_Respawn.
            if (item.WaypointAttached is Waypoints.WaypointSprite kw)
                Waypoints.WaypointSprites.Kill(kw);
            item.WaypointAttached = null;
            Respawn(item);
            return;
        }
        item.NextThink = Now + 1f;
        item.ItemRespawnCounter++;
        if (item.ItemRespawnCounter == 1)
        {
            // QC items.qc:275-289: spawn the countdown waypoint. WP_Weapon (radar icon RADARICON_Weapon) for a
            // weapon pickup, WP_Item (RADARICON_Item) otherwise — matching the engine's per-item radar glyph.
            // Both attach to the item at the '0 0 64' head offset and never fade (lifetime 0; killed on respawn).
            bool isWeapon = item.Pickup?.IsWeaponPickup == true;
            string spriteName = isWeapon ? "Weapon" : "Item";
            System.Numerics.Vector3 color = isWeapon
                ? System.Numerics.Vector3.Zero               // WP_Weapon: black, weapon color resolved client-side
                : new System.Numerics.Vector3(1f, 0f, 1f);   // WP_Item: magenta
            // QC items.qc:289 — MUTATOR_CALLHOOK(Item_RespawnCountdown, this) on the first tick (after the
            // waypoint is spawned). The return value gates the SPRITERULE_SPECTATOR restriction below:
            // Item_ItemsTime_SpectatorOnly(def) && !mutator_returnvalue. Overkill returns true for its surviving
            // Mega/Big health+armor so those countdown waypoints show for EVERYONE, not just spectators.
            bool mutatorOverride = MutatorHooks.FireItemRespawnCountdown(item);
            Waypoints.WaypointSprite wp = Waypoints.WaypointSprites.Spawn(
                spriteName, 0f, 0f, item, new System.Numerics.Vector3(0f, 0f, 64f), item.Origin,
                0, color, radarIcon: 1,
                // QC: only the SpectatorOnly set (Mega/Big Health+Armor) gets SPRITERULE_SPECTATOR (the server
                // per-peer gate, ServerNet.WaypointVisible, reproduces the sv_itemstime 1/2 mode); every other
                // item's countdown waypoint is SPRITERULE_DEFAULT (visible to everyone, like a flag base). A
                // mutator (Overkill) returning true from Item_RespawnCountdown lifts the spectator restriction.
                rule: ItemstimeMutator.IsSpectatorOnlyItem(item.ClassName) && !mutatorOverride
                    ? Waypoints.SpriteRule.Spectator
                    : Waypoints.SpriteRule.Default,
                hideable: true);
            // QC: a WP_Weapon carries wp_extra = the weapon id so the client resolves the per-weapon icon/color
            // (waypointsprites.qc spritelookuptext/color). Stamp it for weapon respawns (parity with Weapon_whereis).
            if (isWeapon && item.OwnedWeaponSet is { IsEmpty: false } ws)
                foreach (int wid in ws.Ids()) { wp.WpExtra = wid; break; }
            // WaypointSprite_UpdateBuildFinished drives the build-progress bar to full at the scheduled respawn.
            Waypoints.WaypointSprites.UpdateBuildFinished(wp, item.ScheduledRespawnTime);
            item.WaypointAttached = wp;
        }

        // QC items.qc:291-307: every countdown tick, for each client that can see the waypoint, play the
        // per-second ITEMRESPAWNCOUNTDOWN cue, then ping the waypoint (radar ring pulse).
        if (item.WaypointAttached is Waypoints.WaypointSprite tickWp)
        {
            // QC soundto(MSG_ONE, this, CH_TRIGGER, SND(ITEMRESPAWNCOUNTDOWN)) per visible client. The port's
            // Common layer has no per-client soundto seam, so emit a single positional cue on the item entity
            // on CH_TRIGGER — every client near the item hears the countdown tick (the per-visible-client
            // gating is approximated by ATTEN_NORM falloff; SpectatorOnly items over-broadcast slightly to
            // nearby live players, the only divergence from QC's waypoint-visibility gate).
            if (Api.Services is not null)
                Api.Sound.Play(item, SoundChannel.TriggerAuto, "ITEMRESPAWNCOUNTDOWN");
            Waypoints.WaypointSprites.Ping(tickWp);
        }
    }

    // The give+respawn tail of Item_Touch for the legacy GiveTo entry: schedule the respawn for the taken item.
    private static void ScheduleRespawnAfterPickup(Entity item)
    {
        if (item.ItemIsLoot) { RemoveItem(item); return; }
        if (item.SpawnShieldExpire == 0f) return; // stay weapon: no respawn
        ScheduleRespawn(item);
    }

    // =====================================================================================
    //  helpers
    // =====================================================================================

    /// <summary>
    /// QC: play the world item's pickup sound on <paramref name="player"/> — a non-resource powerup on
    /// CH_TRIGGER_SINGLE, everything else on CH_TRIGGER; both via the item's pickup sound (the single-vs-auto
    /// distinction is presentation-side, so the port plays on the Item channel). Public so the target_give host
    /// seam (QC target_give_use plays item_pickupsound after ITEM_HANDLE(Pickup,…), give.qc:18-19) can play the
    /// sound the silent <see cref="ItemGiveTo"/> intentionally omits, without duplicating the sound logic.
    /// </summary>
    public static void PlayPickupSound(Entity worldItem, Entity player)
    {
        // QC .item_pickupsound_ent override (a FilterItem hook may stamp a per-item pickup sound, e.g. New Toys'
        // SND_WEAPONPICKUP_NEW_TOYS roflsound) wins over the def's default sound.
        string snd = worldItem.ItemPickupSoundOverride
            ?? worldItem.Pickup?.PickupSoundName ?? "ITEMPICKUP";
        if (Api.Services is not null && !string.IsNullOrEmpty(snd))
            // CH_TRIGGER (auto) so two quick pickups (e.g. armor + health in one pass) stack instead of the
            // second one cutting off the first — DP plays item_pickupsound on the auto trigger channel.
            Api.Sound.Play(player, SoundChannel.TriggerAuto, snd);
    }

    /// <summary>
    /// Spawn the CSQC item particle burst (<c>EFFECT_ITEM_PICKUP</c> / <c>_RESPAWN</c>) at the item's bbox
    /// centre. QC <c>client/items/items.qc</c> fires these in CSQC on the networked <c>ITS_AVAILABLE</c>
    /// transition — pickup at items.qc:271 (and the loot ISF_REMOVEFX path at :338), respawn at :258 — always
    /// at <c>(absmin + absmax) * 0.5</c>. The port emits them server-side on the authoritative pickup/respawn
    /// event over the same Send_Effect channel as <c>EFFECT_SPAWN</c>; this is equivalent to the client-side
    /// transition test but free of its <c>isnew</c>/PVS guards (we only fire on the real gameplay event, never
    /// when an item merely streams into a client's view). <c>origin + (mins + maxs) * 0.5</c> equals the QC
    /// bbox centre and stays correct headless (where the engine link doesn't maintain AbsMin/AbsMax).
    /// </summary>
    private static void EmitItemEffect(string effectName, Entity item)
        => EffectEmitter.Emit(effectName, item.Origin + (item.Mins + item.Maxs) * 0.5f);

    /// <summary>QC RemoveItem: remove the world item (the ISF_REMOVEFX delayed-removal is networking-only here).</summary>
    public static void RemoveItem(Entity item)
    {
        MapMover.RemoveEntity(item);
    }

    // =====================================================================================
    //  Item_Damage (items.qc:981) — loot destroyed by NEEDKILL damage (lava/slime/swamp).
    // =====================================================================================

    /// <summary>
    /// QC <c>Item_Damage</c> (items.qc:981-985): the <c>event_damage</c> handler wired on loot items
    /// (<see cref="StartItem.SpawnInternal"/> sets this on <see cref="Entity.GtEventDamage"/> when loot is
    /// created with <c>TakeDamage = DamageMode.Yes</c>). If the deathtype is a NEEDKILL environmental kill
    /// (DEATH_HURTTRIGGER/SLIME/LAVA/SWAMP — loot falling into lava after the initial NODROP-brush check),
    /// the item is removed immediately. Any other damage type is ignored: items aren't intended to be shot down.
    /// </summary>
    public static void ItemDamage(Entity item, Entity? inflictor, Entity? attacker,
        string deathType, float damage, System.Numerics.Vector3 hitLoc, System.Numerics.Vector3 force)
    {
        // QC: if(ITEM_DAMAGE_NEEDKILL(deathtype)) RemoveItem(this);
        if (DeathTypes.ItemDamageNeedKill(deathType))
            RemoveItem(item);
    }

    // =====================================================================================
    //  item_use (items.qc:987) — trigger-to-spawn (a relay/trigger fires a targetname'd item).
    //  item_setactive (items.qc:995) — show/hide via ACTIVE_* (relay_activate/_deactivate/_toggle).
    // =====================================================================================

    /// <summary>
    /// QC <c>item_use(this, actor, trigger)</c> (items.qc:987-993): wired on permanent items that have a
    /// <c>targetname</c> so a relay/trigger firing the item's targetname can spawn it into play.
    /// <list type="bullet">
    ///   <item>spawnflags &amp; 16 — q3compat immediate-touch: call the item's own touch handler with
    ///     <paramref name="actor"/> as the toucher (as if the actor walked over the item).</item>
    ///   <item>else (normal path) — call <see cref="Respawn"/> to make the item visible and start its
    ///     respawn cycle, exactly as if its respawn timer had fired.</item>
    /// </list>
    /// </summary>
    public static void ItemUse(Entity item, Entity actor)
    {
        if ((item.SpawnFlags & 16) != 0)
        {
            // QC: gettouch(this)(this, actor) — fire the touch handler directly (q3compat).
            item.Touch?.Invoke(item, actor);
        }
        else
        {
            // QC: Item_Respawn(this) — make the item available and start its respawn cycle.
            Respawn(item);
        }
    }

    /// <summary>
    /// QC <c>item_setactive(this, act)</c> (items.qc:995-1005): the per-item <c>.setactive</c> handler wired
    /// by <see cref="StartItem.SpawnInternal"/> on every permanent item. Receives an <c>ACTIVE_*</c> state from
    /// relay_activate / relay_deactivate / relay_activatetoggle (or any other <c>.setactive</c> dispatch) and
    /// calls <see cref="Show"/> on the item to match — <c>ACTIVE_ACTIVE</c> shows it (<c>mode 1</c>),
    /// <c>ACTIVE_NOT</c> hides it (<c>mode -1</c>). <c>ACTIVE_TOGGLE</c> flips the current state.
    /// </summary>
    public static void ItemSetActive(Entity item, int act)
    {
        // QC: old_status = this.active; toggle or set; if changed, Item_Show(1 or -1).
        int oldStatus = item.Active;
        if (act == MapMover.ActiveToggle)
            item.Active = (item.Active == MapMover.ActiveActive) ? MapMover.ActiveNot : MapMover.ActiveActive;
        else
            item.Active = act;

        if (item.Active != oldStatus)
            Show(item, item.Active == MapMover.ActiveActive ? 1 : -1);
    }

    // QC ITEM_TOUCH_NEEDKILL(): the loot's spot is a NODROP brush (lava) or a sky surface. We trace a zero-length
    // line at the item and test the hit contents + the q3 surface flags (the port has no separate dpstartcontents
    // global, so we fold the QC `(trace_dpstartcontents | trace_dphitcontents) & DPCONTENTS_NODROP` into the
    // single hit-contents test). The sky-surface half (Q3SURFACEFLAG_SKY) kills loot that lands on a sky brush.
    private static bool LootInNoDrop(Entity item)
    {
        if (Api.Services is null) return false;
        TraceResult tr = Api.Trace.Trace(item.Origin, System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero,
            item.Origin, MoveFilter.Normal, item);
        return (tr.DpHitContents & NoDropContents) != 0
            || (tr.DpHitQ3SurfaceFlags & Q3SurfaceFlagSky) != 0;
    }

    // QC: a superweapon weapon pickup never weapon-stays. The port has no per-item weapon link here; approximate
    // via the weapon registry (any owned-weapon-set bit that is a superweapon).
    private static bool IsSuperWeaponItem(Entity item)
    {
        foreach (var w in item.OwnedWeaponSet.Weapons())
            if (w.IsSuperWeapon) return true;
        return false;
    }

    /// <summary>QC <c>g_weapon_stay</c> (items.qh: cvar("g_weapon_stay")). 0 = off, 1 = ghost no-ammo, 2 = stay-ammo.</summary>
    private static int WeaponStay => Api.Services is null ? 0 : (int)Api.Cvars.GetFloat("g_weapon_stay");

    /// <summary>
    /// Read a float cvar, or <paramref name="fallback"/> (the stock value) if it's unset/empty. The port
    /// convention (Weapon.Bal / TargetUtilities.CvarOr): "unset" is the empty-string case, distinct from "0".
    /// Public so the item defs' <c>ItemInit</c> can seed amounts/timers with the same fallback semantics.
    /// </summary>
    public static float CvarOr(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name);
    }

    /// <summary>Read a bool cvar (<c>!= 0</c>), or <paramref name="fallback"/> if unset (the g_powerups toggles).</summary>
    public static bool CvarBoolOr(string name, bool fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name) != 0f;
    }
}
