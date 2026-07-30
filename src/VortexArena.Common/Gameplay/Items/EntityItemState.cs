// port: per-entity item/world-item state (qcsrc/server/items/items.qh + common/items/item.qh fields).
//
// World item entities in QC carry per-spawn fields the pickup/respawn logic reads and writes: the respawn
// time + jitter, the scheduled respawn time, the spawn-shield window (item_spawnshieldtime), the
// pickup-anyway override, and the temporary powerup timers stored on the world item before they're handed
// to the player (strength_finished, …).
//
// Entity is declared partial, and this lives in a new file, so extending it here is allowed by the task
// constraints (no existing file is modified). Field names are item-prefixed where a bare name already
// exists on a derived class (e.g. Player.RespawnTime), to avoid shadowing; the world-item "live" flag
// reuses the existing Entity.SpawnShieldExpire (QC's .spawnshieldtime, shared across entity types) rather
// than redeclaring it.

namespace XonoticGodot.Common.Framework
{
    public partial class Entity
    {
        // --- world-item identity (server/items/spawning.qh + StartItem) ---
        /// <summary>
        /// QC <c>.itemdef</c> — the <see cref="XonoticGodot.Common.Gameplay.Pickup"/> singleton this world item
        /// carries; <c>Item_Touch</c> dispatches its give through this (QC ITEM_HANDLE(Pickup, this.itemdef, …)).
        /// Null on a non-item entity. Typed as object to keep this Framework partial free of a Gameplay using.
        /// </summary>
        public object? ItemDefRef;

        /// <summary>
        /// QC <c>.itemdef</c> typed as the carried <see cref="XonoticGodot.Common.Gameplay.Pickup"/>. Strongly-typed
        /// accessor over <see cref="ItemDefRef"/> (kept <c>object?</c> on this Framework partial to avoid a
        /// Gameplay using). Set by <see cref="XonoticGodot.Common.Gameplay.StartItem.Spawn"/>; read by
        /// <c>Item_Touch</c> to dispatch the give (QC ITEM_HANDLE(Pickup, this.itemdef, …)).
        /// </summary>
        public XonoticGodot.Common.Gameplay.Pickup? Pickup
        {
            get => ItemDefRef as XonoticGodot.Common.Gameplay.Pickup;
            set => ItemDefRef = value;
        }

        /// <summary>
        /// QC <c>.m_isloot</c> (server/items/spawning.qh ITEM_IS_LOOT): true for a dropped/tossed loot item
        /// (thrown weapon, death-drop, nade-spawned). NOT derived from <see cref="Gravity"/> — the port's
        /// Entity.Gravity defaults to 1f, so a gravity test would wrongly mark every item loot. Loot items
        /// MOVETYPE_TOSS, despawn after <c>g_items_dropped_lifetime</c>, and never respawn.
        /// </summary>
        public bool ItemIsLoot;

        /// <summary>
        /// QC <c>.ok_item</c> (common/mutators/mutator/overkill/sv_overkill.qh): set on a world item that is an
        /// Overkill item — by the Overkill loot drop (<c>ok_DropItem</c>) and by a random-items spawn/replace/loot
        /// item when Overkill is enabled (sv_random_items.qc:274/303 <c>if (MUTATOR_IS_ENABLED(ok)) ok_item = true</c>).
        /// The Overkill FilterItem always lets an <c>ok_item</c> through (its own loot/replacement is never re-filtered).
        /// The port's Overkill FilterItem stands in via <see cref="ItemIsLoot"/> for its loot, but the random-items
        /// REPLACE/SPAWN items (which are NOT loot) need this explicit tag, exactly as QC sets it.
        /// </summary>
        public bool OkItem;

        /// <summary>
        /// QC <c>.m_isexpiring</c> (server/items/spawning.qh ITEM_IS_EXPIRING): a loot item whose powerup
        /// timers expire while it sits on the ground (its <c>nextthink</c> is the max powerup-finished time).
        /// </summary>
        public bool ItemIsExpiring;

        /// <summary>
        /// QC <c>ITS_EXPIRING</c> (common/items/item.qh BIT(7), a bit of the networked <c>.ItemStatus</c>) — set by
        /// <c>Item_Think</c> during the last <c>IT_DESPAWNFX_TIME</c> seconds of a loot item's life so the CLIENT
        /// runs the despawn animation (alpha fade + the accelerating <c>EFFECT_ITEM_DESPAWN</c> puffs,
        /// client/items/items.qc:191-210). DISTINCT from <see cref="ItemIsExpiring"/> (<c>.m_isexpiring</c>, the
        /// powerup-timer-on-the-ground flag): this one is a pure render/network status. Networked to the client via
        /// <c>NetEntityFlags.ItemExpiring</c> (the item snapshot); the client drives <see cref="XonoticGodot.Common.Gameplay.ItemDespawnFx"/>.
        /// </summary>
        public bool ItemExpiringFx;

        /// <summary>
        /// QC <c>ITS_ANIMATE1</c> / <c>ITS_ANIMATE2</c> (common/items/item.qh BIT(1)/BIT(2), bits of the networked
        /// <c>.ItemStatus</c>) — the client-side bob+spin animation CLASS a world pickup renders with, set at spawn
        /// (server/items/items.qc:1198-1213) and gated by spawnflag 1024 (no-animate). <c>0</c> = none (ammo/keys,
        /// static); <c>1</c> = ANIMATE1 (powerups &amp; weapons: yaw +180°/s, bob <c>10 + 8·sin(2t)</c>); <c>2</c> =
        /// ANIMATE2 (health &amp; armor: yaw -90°/s, bob <c>8 + 4·sin(3t)</c>). Purely a render/network status — the
        /// server never moves the item for the bob (the bbox is offset client-side). Networked via
        /// <c>NetEntityFlags.ItemAnimate1</c>/<c>ItemAnimate2</c>; the client drives it in <c>EntityNode</c>. The bob's
        /// base offset (8/10 units) is also what lifts the model clear of the floor it rests on.
        /// </summary>
        public byte ItemAnimate;

        // --- respawn scheduling (server/items/items.qc) ---
        /// <summary>QC .respawntime — seconds before this item respawns (0 = use default, -1 = never).</summary>
        public float ItemRespawnTime;
        /// <summary>QC .respawntimejitter — +/- random jitter added to the respawn time.</summary>
        public float ItemRespawnTimeJitter;
        /// <summary>QC .respawntimestart — initial spawn delay override (for items that don't start spawned).</summary>
        public float ItemRespawnTimeStart;
        /// <summary>QC .scheduledrespawntime — engine time the item is next due to reappear.</summary>
        public float ScheduledRespawnTime;
        /// <summary>QC .wait (item) — engine time the simple respawn-think fires.</summary>
        public float ItemWait;
        /// <summary>QC .item_respawncounter — ticks remaining in the respawn countdown.</summary>
        public int ItemRespawnCounter;

        /// <summary>
        /// QC <c>.waypointsprite_attached</c> (server/items/items.qc Item_RespawnCountdown) — the respawn-countdown
        /// waypoint sprite spawned on a timed item while it is on cooldown, killed when it respawns. For the
        /// SpectatorOnly items (Mega/Big Health+Armor) it carries <c>SPRITERULE_SPECTATOR</c> so only spectators
        /// (and, in warmup / <c>sv_itemstime==2</c>, everyone) see the respawn countdown marker. Typed as object to
        /// keep this Framework partial free of a Gameplay using; the item-respawn code casts it.
        /// </summary>
        public object? WaypointAttached;

        // --- pickup gating ---
        /// <summary>QC .item_spawnshieldtime — touches before this engine time are ignored (anti double-pick).</summary>
        public float ItemSpawnShieldExpire;
        /// <summary>QC .pickup_anyway — per-spawn override of m_pickupanyway (take even when capped).</summary>
        public int PickupAnyway;

        // NOTE: the world-item "live" flag (QC .spawnshieldtime, non-zero => the item can be taken/respawned)
        // reuses the existing Entity.SpawnShieldExpire field (declared in DamageEntityState.cs). For a player
        // that field is the spawn-shield expiry; for a world item it is the live marker — exactly as QC
        // overloads the same .spawnshieldtime field across entity types.

        // --- temporary powerup timers carried on the world item before give (items.qc) ---
        /// <summary>QC .strength_finished — strength duration this item grants.</summary>
        public float StrengthFinished;
        /// <summary>QC .invincible_finished — shield/invincible duration this item grants.</summary>
        public float InvincibleFinished;
        /// <summary>QC .speed_finished — speed duration this item grants.</summary>
        public float SpeedFinished;
        /// <summary>QC .invisibility_finished — invisibility duration this item grants.</summary>
        public float InvisibilityFinished;
        /// <summary>QC .superweapons_finished — superweapon duration this item grants.</summary>
        public float SuperweaponsFinished;

        /// <summary>True while the item is shown/available; mirrors QC Item_Show state (drives ITS_AVAILABLE).</summary>
        public bool ItemAvailable = true;

        /// <summary>
        /// QC <c>.new_toys</c> (common/mutators/mutator/new_toys/sv_new_toys.qc:109) — the map-authored weapon
        /// replacement list set on a <c>weapon_*</c> map entity (BSP key <c>"new_toys"</c>, e.g.
        /// <c>"new_toys" "vortex rifle"</c>). Read by the New Toys mutator's SetWeaponreplace handler to override
        /// what weapon the entity spawns as. <c>null</c> = no map key (use the global autoreplace mapping).
        /// </summary>
        public string? NewToys;

        /// <summary>
        /// QC <c>.m_isreplaced</c> (server/weapons/spawning.qc:11) — set on a secondary weapon entity spawned by a
        /// multi-token weaponreplace group so its own <c>weapon_defaultspawnfunc</c> skips the replace pass (it
        /// IS the replacement). Prevents infinite recursion + double-replacement.
        /// </summary>
        public bool IsReplacedWeapon;

        /// <summary>
        /// QC <c>.item_pickupsound_ent</c> (the override pickup sound a FilterItem hook stamps on a world item —
        /// e.g. New Toys' <c>SND_WEAPONPICKUP_NEW_TOYS</c> roflsound). When set, <c>PlayPickupSound</c> plays this
        /// SND_* name instead of the def's default pickup sound. <c>null</c> = use the def's sound.
        /// </summary>
        public string? ItemPickupSoundOverride;

        /// <summary>
        /// QC <c>.mdl</c> — the item's stored world-model name, so <c>Item_Show</c> can restore
        /// <see cref="Model"/> when re-showing a hidden/respawned item (Show clears Model to "" when hiding).
        /// Set by <see cref="XonoticGodot.Common.Gameplay.StartItem.Spawn"/> from the def's model.
        /// </summary>
        public string? ItemWorldModel;

        /// <summary>
        /// QC <c>ITS_STAYWEP</c> — set on a weapon-stay ghost (Item_Show mode 0 with g_weapon_stay): the item is
        /// translucent + still pickable but gives only the weapon, not ammo. The live/stay distinction is carried
        /// by <see cref="SpawnShieldExpire"/> (0 = stay); this is the explicit render/marker bit QC also sets.
        /// </summary>
        public bool ItemStayWeapon;

        /// <summary>
        /// QC <c>FL_PICKUPITEMS</c> (common/constants.qh BIT(18)) — set on a LIVE joined player in
        /// PutClientInServer alongside FL_CLIENT, and the FIRST gate of <c>Item_Touch</c>: only an entity with
        /// this flag (and alive, not the item's owner, past the spawn-shield) can pick up a world item. NOT the
        /// same as <see cref="EntFlags.Client"/> — QC only sets it on a spawned player, so an observer/projectile
        /// never collects items. Set by ClientManager on (re)spawn, cleared when the player leaves the game.
        /// </summary>
        public bool CanPickupItems;

        // --- physical-items ghost-entity state (sv_physical_items.qc:.spawn_origin / .spawn_angles / .cnt) ---
        // Set on the ghost (wep) entity spawned by the physical_items mutator, NOT on the real item.

        /// <summary>
        /// QC <c>.spawn_origin</c> (sv_physical_items.qc:33) — the ghost entity's home origin (where the real
        /// item originally sat). Used by <c>physical_item_think</c> to snap the ghost back when the real item
        /// is awaiting respawn, and by <c>physical_item_touch</c> / <c>physical_item_damage</c> to snap back
        /// after a NODROP/SKY contact or an environmental-kill hit.
        /// </summary>
        public System.Numerics.Vector3 PhysSpawnOrigin;

        /// <summary>
        /// QC <c>.spawn_angles</c> (sv_physical_items.qc:33) — the ghost entity's home angles. Paired with
        /// <see cref="PhysSpawnOrigin"/>: the ghost is rotated to these angles whenever it snaps home.
        /// </summary>
        public System.Numerics.Vector3 PhysSpawnAngles;

        /// <summary>
        /// QC <c>.cnt</c> on the ghost entity (sv_physical_items.qc:119: <c>wep.cnt = (item.owner != NULL)</c>)
        /// — true when this ghost was created for a DROPPED weapon (loot item), false for a MAP item. Dropped-
        /// weapon ghosts skip the respawn-reset, NODROP/SKY snap, and environmental-kill snap logic: QC's
        /// exemption condition is <c>if (!this.cnt)</c> on all three callbacks.
        /// </summary>
        public bool PhysIsDropped;

        // --- physical-items network suppression (set on the REAL item, not the ghost) ---
        /// <summary>
        /// QC <c>setSendEntity(item, func_null)</c> (sv_physical_items.qc) — once the physical_items mutator has
        /// hidden the real item behind its physics ghost, the real item STOPS being networked entirely (the ghost
        /// is the only thing clients see). DarkPlaces achieves this by clearing the item's SendEntity callback; the
        /// port has no per-entity SendEntity, so this sticky flag is the equivalent: when set, ServerNet's snapshot
        /// producer skips the entity (it stays a live server-side edict driving MOVETYPE_FOLLOW, but never enters
        /// the entity feed). Set on the REAL item by <c>PhysicalItemsMutator.OnItemSpawn</c>, never cleared (QC's
        /// suppression is permanent for the item's lifetime). Default false.
        /// </summary>
        public bool PhysNetSuppressed;
    }
}
