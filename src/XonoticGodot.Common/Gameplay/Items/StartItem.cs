// Port of StartItem (qcsrc/server/items/items.qc:1007) + Item_Reset (items.qc:783) + Item_FindTeam
// (items.qc:813) + item_use/item_setactive (items.qc:987-1005) — the spawn driver every item_*/weapon_*
// funnels through. Seeds the world edict from a Pickup def: runs the per-item ItemInit, sets the IT_* items
// mask + weapon set, the FL_ITEM flag, the model + bbox, then branches on loot vs permanent:
//   LOOT       — MOVETYPE_TOSS, gravity, Item_Think despawn at g_items_dropped_lifetime, anti-instant-pick
//                spawnshield, takedamage (with Item_Damage NEEDKILL handler), EXPIRING timer, NODROP-kill.
//   PERMANENT  — have_pickup_item gate (delete if mutator-blocked / g_pickup_items==0), respawntime default,
//                spawnflags&1/noalign suspension, drop-to-floor, the ###item### findnearest target,
//                item_use (trigger-to-spawn) + item_setactive (active/toggle relay) on targetname'd items.
// Tail: settouch(Item_Touch), then if(team) Item_FindTeam else Item_Reset. Powerups + superweapons route to
// ScheduleInitialRespawn (do NOT spawn at match start). The FilterItem / Item_Spawn / waypoint /
// Net_LinkEntity hooks are CSQC/mutator networking and are out of scope; the spawn STATE + touch wiring are
// ported faithfully.

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The world-item spawn driver — QC <c>StartItem</c> + <c>Item_Reset</c>. <see cref="ItemSpawnFuncs"/> resolves
/// a classname to a <see cref="Pickup"/> def and calls <see cref="Spawn"/>; loot producers (thrown weapons,
/// death drops) call <see cref="SpawnLoot"/>. Static + stateless.
/// </summary>
public static class StartItem
{
    /// <summary>
    /// True if the LAST <see cref="Spawn"/> failed (QC <c>startitem_failed</c>): the item was deleted (mutator-
    /// blocked, g_pickup_items==0, or in a NODROP brush). The spawnfunc reads this to know whether to keep the edict.
    /// </summary>
    public static bool LastSpawnFailed { get; private set; }

    private static float Now => Api.Services != null ? Api.Clock.Time : 0f;

    /// <summary>
    /// Host seam: the engine time the match begins (QC <c>game_starttime</c>), so the initial powerup/superweapon
    /// respawn is offset past the countdown. The orchestrator wires this to GameWorld.GameStartTime; unset = 0
    /// (immediate, the headless default).
    /// </summary>
    public static System.Func<float>? GameStartTimeProvider;

    private static float GameStartTime => GameStartTimeProvider?.Invoke() ?? 0f;

    /// <summary>
    /// Host seam: true when the loaded map is a Quake3/Quake-Live-compat map (QC <c>q3compat</c>, set from the
    /// presence of a <c>.arena</c>/<c>.defi</c> file at world.qc:964-965). On such maps the mapper-placed item
    /// origin sits in the MIDDLE of the bbox (Q3 "radius" 15) rather than at the bottom as in Xonotic, so the
    /// permanent-item spawn lowers the origin by <c>-15 - mins.z</c> (items.qc:1133). The port has no live
    /// per-map q3compat flag (CompatRemaps.cs:17 documents this port-wide gap), so this defaults to <c>false</c>
    /// (no offset) — matching the rest of the port's Q3 handling, which never applies ARENA/DEFI-only behavior.
    /// A host that gains real q3compat detection can wire this to activate the faithful offset below.
    /// </summary>
    public static System.Func<bool>? Q3CompatProvider;

    private static bool Q3Compat => Q3CompatProvider?.Invoke() ?? false;

    // =====================================================================================
    //  StartItem(this, def) — the spawn driver (items.qc:1007)
    // =====================================================================================

    /// <summary>
    /// QC <c>StartItem(this, def)</c>: initialise the world edict <paramref name="item"/> as a permanent map
    /// pickup of definition <paramref name="def"/>. Sets <see cref="LastSpawnFailed"/> and removes the edict on
    /// failure (mutator-blocked / g_pickup_items==0). Returns the spawned item, or null if it was deleted.
    /// </summary>
    public static Entity? Spawn(Entity item, Pickup def) => SpawnInternal(item, def, isLoot: false, lifetime: 0f);

    /// <summary>
    /// QC the loot path of StartItem (Item_Initialise with lifetime &gt;= 0): spawn <paramref name="def"/> as a
    /// dropped/tossed loot item at <paramref name="item"/> — MOVETYPE_TOSS, despawning after
    /// <paramref name="lifetime"/> seconds (0 = g_items_dropped_lifetime default), anti-instant-pick, never
    /// respawns. Used by thrown-weapon / death-drop producers. Returns the item, or null if killed (NODROP).
    /// </summary>
    public static Entity? SpawnLoot(Entity item, Pickup def, float lifetime = 0f)
        => SpawnInternal(item, def, isLoot: true, lifetime: lifetime);

    private static Entity? SpawnInternal(Entity item, Pickup def, bool isLoot, float lifetime)
    {
        LastSpawnFailed = true; // QC: early return means failure

        item.Pickup = def;
        item.ClassName = def.NetName.Length > 0 ? CanonicalClassName(def) : item.ClassName;
        item.ItemIsLoot = isLoot;

        // QC: def.m_iteminit(def, this) — seed resources / powerup timers / per-item cap (may set MUTATORBLOCKED).
        def.ItemInit(item);

        // QC: this.items = def.m_itemid; weapon set if a weapon pickup; flags = FL_ITEM | def.m_itemflags.
        item.Items = (int)def.ItemDef.ItemId;
        // (a weapon pickup's OwnedWeaponSet is seeded by WeaponPickup.ItemInit, called just above via
        // def.ItemInit — here we leave the carried weapon set as ItemInit set it.)
        item.Flags |= EntFlags.Item;

        // QC: MUTATOR_CALLHOOK(FilterItem, this) (items.qc:1031) — after the items/weapon/flags seeding (1018-1026)
        // and BEFORE the have-pickup gate (1037). Handlers inspect the def (instanceOfHealth/Armor/Powerup) and
        // return true to forbid the spawn; on true the item is deleted and the function returns early (failure).
        //
        // DEVIATION (port mechanics, not Base behavior): QC sets this.netname = def.m_name LATER (items.qc:1190),
        // AFTER the hook, because its handlers read def.instanceOf* directly. The port has no item-class registry,
        // so its FilterItem subscribers (MeleeOnly/Hook) stand in via the edict's ClassName/NetName tags — so the
        // NetName must already be live when the hook fires. ClassName is set above (line 66); assign NetName here,
        // before the hook, so those classname/netname checks see the real value. (The QC tail still re-assigns the
        // same def.m_name; here it's simply hoisted to the hook seam — same end state, no Base-behavior change.)
        item.NetName = def.NetName;
        var filterArgs = new MutatorHooks.FilterItemDefinitionArgs(item);
        if (MutatorHooks.FilterItemDefinition.Call(ref filterArgs))
        {
            // QC: delete(this); return; — the item is forbidden, so remove the edict and report the spawn failed
            // (LastSpawnFailed is already true from the top of the method). Returns null like the QC early return.
            ItemPickupRules.RemoveItem(item);
            return null;
        }

        // QC: the ENTITY-level FilterItem hook (the same MUTATOR_CALLHOOK(FilterItem, this) seam, items.qc:1031).
        // DISTINCT from the definition-level FilterItemDefinition above: this one can REPLACE the item with a
        // different classname (random_items spawns its replacement here from the live origin/spawnflags and returns
        // true). A true return DELETES this item (QC delete(this); return;). The replacement item runs its OWN
        // StartItem under the mutator's recursion guard, so it re-enters here without re-replacing.
        if (MutatorHooks.FireFilterItem(item))
        {
            ItemPickupRules.RemoveItem(item);
            return null;
        }

        // QC: set model + bbox BEFORE droptofloor / spawnshield (so the touch-area-grid has a real volume).
        // The defs carry the BARE model name (the QC Item_Model/W_Model macro argument); build the full VFS
        // path here or the asset loader can't find it ("not found in any mount: item_armor_large.md3").
        item.ItemWorldModel = ResolveModelPath(def);
        item.Model = item.ItemWorldModel ?? "";
        if (Api.Services is not null && !string.IsNullOrEmpty(item.Model))
            Api.Entities.SetModel(item, item.Model);
        MapMover.SetSize(item, def.Mins, def.Maxs);

        // QC: the client bob/spin animation class (server/items/items.qc:1198-1213), unless spawnflag 1024
        // (no-animate) is set. Powerups & weapons spin +180°/s and bob high (ANIMATE1); health & armor spin
        // -90°/s and bob low (ANIMATE2); ammo/keys stay static. Networked (NetEntityFlags) and driven on the
        // client (EntityNode). ANIMATE1 takes priority when both would apply (matches the client's if/else if).
        if ((item.SpawnFlags & 1024) == 0)
        {
            if (def.IsPowerup || def.IsWeaponPickup)
                item.ItemAnimate = 1;
            else if (def.IsHealth || def.IsArmor)
                item.ItemAnimate = 2;
        }

        if (isLoot)
        {
            if (!SetupLoot(item, lifetime))
                return null; // deleted (NODROP brush)
        }
        else
        {
            if (!SetupPermanent(item, def))
                return null; // deleted (mutator-blocked / g_pickup_items off)
        }

        // QC tail: settouch(Item_Touch); netname; skin; colormap for weapons; then Item_Reset (or Item_FindTeam).
        item.Touch = ItemPickupRules.ItemTouch;
        item.NetName = def.NetName; // QC this.netname = def.m_name (items.qc:1190) — already hoisted before the
                                    // FilterItem hook above; re-assigning the same value here keeps the tail faithful.

        // QC items.qc:1216-1226: if(this.team) Item_FindTeam else Item_Reset. A teamed item marks itself with
        // EF_NOGUNBOB (the search-marker sentinel used by Item_FindTeam to recognise un-initialised members),
        // defers its first appearance via InitializeEntity (INITPRIO_FINDTARGET = after all spawns), and sets
        // this.reset = Item_FindTeam (so map-reset re-runs the random pick). The port has no INITPRIO deferred
        // queue, so we run Item_FindTeam directly — at map load all teamed items are already spawned because the
        // BSP lump is processed sequentially before any gameplay ticks, so the full g_items set is available.
        if (!isLoot && item.Team != 0f)
        {
            item.Cnt = item.Cnt != 0 ? item.Cnt : 1; // QC: if(!this.cnt) this.cnt = 1 — default weight
            FindTeam(item);
        }
        else
        {
            Item_Reset(item);
        }

        // QC: MUTATOR_CALLHOOK(Item_Spawn, this) (items.qc:1231) — physical_items subscribes here to attach a
        // physics ghost and hide the real item. The hook is notify-style; the return value is informational only.
        MutatorHooks.FireItemSpawn(item);

        // Dev/CI (port-only): g_debug_items_start_unavailable "<substring>|all" — mark matching PERMANENT
        // items as already picked up at spawn (the same Item_ScheduleRespawn hide a real pickup runs), so the
        // awaiting-respawn ghost render can be framed deterministically (--observe + --screenshot) without
        // scripting a player to touch it. Empty/unset = off. Combine with a large g_pickup_respawntime_* to
        // keep the ghost up for the whole capture. Runs AFTER the Item_Spawn hook (a real pickup always
        // post-dates spawn hooks) and only on items that are actually AVAILABLE — a FindTeam-hidden teamed
        // loser or a delayed-start powerup must not get a respawn timer that would later show extra members.
        if (!isLoot && item.ItemAvailable && Api.Services is not null)
        {
            string dbgGhost = Api.Cvars.GetString("g_debug_items_start_unavailable");
            if (!string.IsNullOrWhiteSpace(dbgGhost)
                && (dbgGhost == "all" || item.ClassName.Contains(dbgGhost, System.StringComparison.OrdinalIgnoreCase)))
                ItemPickupRules.ScheduleRespawn(item);
        }

        LastSpawnFailed = false;
        return item;
    }

    /// <summary>
    /// Build the full VFS model path for a world item from the def's bare model name — the port's equivalent of
    /// QC <c>Item_Model</c> (<c>"models/items/" + m</c>, common/items/all.qc) and, for weapon pickups,
    /// <c>W_Model</c> (<c>"models/weapons/" + m</c>). The item/weapon defs store the bare macro argument
    /// (e.g. <c>item_armor_large.md3</c>, <c>v_laser.md3</c>); without the directory the asset loader reports
    /// "not found in any mount". A model that already contains a path separator is returned unchanged.
    /// (Public so the client GPU warm pass can build the IDENTICAL vpath a live spawn uses — engine-perf 2026-06-16.)
    /// </summary>
    public static string? ResolveModelPath(Pickup def)
    {
        string? m = def.Model;
        if (string.IsNullOrEmpty(m) || m.Contains('/'))
            return m;
        return (def.ItemDef.IsWeaponPickup ? "models/weapons/" : "models/items/") + m;
    }

    // QC StartItem loot branch (items.qc:1062-1098).
    private static bool SetupLoot(Entity item, float lifetime)
    {
        item.MoveType = MoveType.Toss;
        item.Gravity = 1f;

        item.Think = ItemPickupRules.ItemThink;
        item.NextThink = Now + ItemPickupRules.ItemUpdateInterval; // QC IT_UPDATE_INTERVAL
        float life = lifetime > 0f ? lifetime : ItemPickupRules.CvarOr("g_items_dropped_lifetime", 20f);
        item.ItemWait = Now + life;

        item.Owner = null;                       // anyone can pick it up...
        item.ItemSpawnShieldExpire = Now + 0.5f; // ...but not straight away (anti-instant-pick)
        item.SpawnShieldExpire = 1f;             // live marker (loot is a normal live item, just non-respawning)

        item.TakeDamage = DamageMode.Yes;        // QC: takedamage = DAMAGE_YES; event_damage = Item_Damage
        item.GtEventDamage = ItemPickupRules.ItemDamage; // QC items.qc:1077 event_damage = Item_Damage

        // QC: if EXPIRING, nextthink = max(strength/invincible/superweapons_finished) — the timers tick down on
        // the ground. (Set ItemIsExpiring on the edict before SpawnLoot to opt in; default off.)
        if (item.ItemIsExpiring)
        {
            float t = System.MathF.Max(item.StrengthFinished,
                System.MathF.Max(item.InvincibleFinished, item.SuperweaponsFinished));
            if (t > 0f) item.NextThink = t;
        }

        // QC: don't drop loot in a NODROP zone (lava) — traceline at the origin, delete if NODROP.
        if (Api.Services is not null)
        {
            TraceResult tr = Api.Trace.Trace(item.Origin, System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero,
                item.Origin, MoveFilter.Normal, item);
            if ((tr.DpHitContents & ItemPickupRules.NoDropContents) != 0)
            {
                ItemPickupRules.RemoveItem(item);
                return false;
            }
        }
        return true;
    }

    // QC StartItem permanent branch (items.qc:1099-1185).
    private static bool SetupPermanent(Entity item, Pickup def)
    {
        // QC: have_pickup_item(this) — delete a mutator-blocked item, or (g_pickup_items==0) any item, or
        // weaponarena weapons/ammo. Done AFTER ItemInit (which may have set MUTATORBLOCKED).
        if (!HavePickupItem(item, def))
        {
            ItemPickupRules.RemoveItem(item);
            return false;
        }

        // QC: default respawntime from def.m_respawntime if the edict didn't set one.
        if (item.ItemRespawnTime == 0f)
            item.ItemRespawnTime = def.RespawnTime;
        if (item.ItemRespawnTimeJitter == 0f)
            item.ItemRespawnTimeJitter = def.RespawnTimeJitter;

        // QC (items.qc:1122-1135): on a Quake3-compat map the mapper-placed origin is the MIDDLE of the bbox
        // (Q3 "radius" 15), but Xonotic anchors at the bottom — so lower the origin by (-15 - mins.z) and re-link.
        // Only the mapper-placed permanent item is shifted (loot keeps its tossed origin). Dormant by default:
        // the port has no live per-map q3compat flag (see Q3CompatProvider), so this no-ops on native maps. (The
        // QC `team` crc16 fallback in the same block is the team-item path, which the port wires separately.)
        if (Q3Compat)
        {
            var o = item.Origin;
            o.Z += -15f - def.Mins.Z;
            item.Origin = o;
            if (Api.Services is not null)
                Api.Entities.SetOrigin(item, item.Origin);
        }

        // QC: spawnflags&1 => noalign (suspended). The port's Entity.NoAlign is a bool (set by the map "noalign"
        // key); QC's float noalign>0 (suspended) maps to true here, <=0 (drop to floor) maps to false. (The QC
        // noalign<0 "skip drop AND skip filter" case is map-rare and collapses to drop-to-floor here.)
        if ((item.SpawnFlags & 1) != 0)
            item.NoAlign = true;

        if (item.NoAlign)
        {
            item.MoveType = MoveType.None; // suspended in the air
        }
        else
        {
            // QC DropToFloor_QC_DelayedInit(this): settle the item onto the floor at spawn. Do the drop
            // EXPLICITLY (not via the TOSS integrator) — TOSS settles the FULL bbox at first contact and, on a
            // start-solid placement, leaves a wide item like the mega health embedded in the floor. The Base drop
            // traces a SMALL Q3 box straight down so it finds the floor even where the full ±30 hull starts solid,
            // then rests the item there before the first snapshot (playtest-bugs #9). (Waypoint spawn is bot-nav,
            // skipped.)
            DropItemToFloor(item);
            item.MoveType = MoveType.Toss; // still TOSS so physical-items can knock it / it rests normally
        }

        item.SpawnShieldExpire = 1f; // live marker

        // QC items.qc:1152-1156: a targetname'd item gets item_use wired so a trigger firing its targetname
        // spawns it (spawnflags&16 → immediate touch, else Item_Respawn). Every permanent item also gets
        // item_setactive so relay_activate/_deactivate/_activatetoggle / target_activate can show/hide it.
        if (!string.IsNullOrEmpty(item.TargetName))
            item.Use = ItemPickupRules.ItemUse;     // QC: this.use = item_use (items.qc:1153)
        item.SetActive = ItemPickupRules.ItemSetActive; // QC: this.setactive = item_setactive (items.qc:1155)
        item.Active = MapMover.ActiveActive;            // QC: this.active = ACTIVE_ACTIVE (items.qc:1156)

        // QC: target = "###item###" (findnearest) for powerups, weapons, non-small health/armor, and keys.
        bool wantsTarget = def.IsPowerup || def.IsWeaponPickup
            || (def.IsHealth && !def.IsSmall)
            || (def.IsArmor && !def.IsSmall)
            || (def.ItemDef.ItemId & (ItemFlag.Key1 | ItemFlag.Key2)) != 0;
        if (wantsTarget && string.IsNullOrEmpty(item.Target))
            item.Target = "###item###";

        return true;
    }

    /// <summary>
    /// QC <c>DropToFloor_QC</c> for a world item (world.qc): drop the item straight down onto the floor at spawn.
    /// Traces a SMALL Q3 box (±15 wide, <c>mins.z .. mins.z+30</c> tall) rather than the item's full hull, so it
    /// finds the floor even where the full ±30 bbox starts solid, then rests the item at the impact. Runs at
    /// spawn (before the first snapshot) so the client's first origin is already settled — the TOSS integrator
    /// alone leaves a wide item (mega health) embedded on a start-solid placement (playtest-bugs #9). Keeps the
    /// placed origin if even the small box starts solid (QC's <c>trace_startsolid</c> guard).
    /// </summary>
    private static void DropItemToFloor(Entity item)
    {
        if (Api.Services is null)
            return;
        var start = item.Origin;
        var dropMins = new System.Numerics.Vector3(-15f, -15f, item.Mins.Z);
        var dropMaxs = new System.Numerics.Vector3(15f, 15f, item.Mins.Z + 30f);
        var end = start - new System.Numerics.Vector3(0f, 0f, 4096f); // QC droptofloor traces far down for the floor
        // MOVE_NORMAL (not NOMONSTERS): Base's droptofloor clips against ALL solid entities, incl. SOLID_BBOX
        // model/prop platforms — not just BSP + brush models. NOMONSTERS skipped a bbox surface a mapper item
        // rested on (e.g. the Stormkeep mega health), so the item fell THROUGH it to the BSP floor below and
        // clipped in. At spawn the only extra SOLID_BBOX hits are other items (rare, and Base-faithful).
        TraceResult tr = Api.Trace.Trace(start, dropMins, dropMaxs, end, MoveFilter.Normal, item);
        if (tr.StartSolid)
            return; // embedded even for the small box — keep the placed origin (QC keeps trace_endpos == start)
        Api.Entities.SetOrigin(item, tr.EndPos);
    }

    // =====================================================================================
    //  Item_Reset (items.qc:783) — (re)initialise an item's visible state at spawn / map reset.
    // =====================================================================================

    /// <summary>
    /// QC <c>Item_Reset(this)</c>: a targeted (use-spawned) item starts hidden; otherwise the item is shown
    /// (or hidden) per its current state. A loot item just returns. A powerup or a superweapon weapon routes to
    /// <see cref="ItemPickupRules.ScheduleInitialRespawn"/> — they do NOT spawn at match start.
    /// </summary>
    public static void Item_Reset(Entity item)
    {
        // QC: a targetname'd item (not spawnflags&16) starts hidden, waiting to be triggered.
        if (!string.IsNullOrEmpty(item.TargetName) && (item.SpawnFlags & 16) == 0)
        {
            ItemPickupRules.Show(item, -1);
            // QC items.qc:788-789: WaypointSprite_Kill(this.waypointsprite_attached) before going dormant.
            if (item.WaypointAttached is Waypoints.WaypointSprite hwp)
            {
                Waypoints.WaypointSprites.Kill(hwp);
                item.WaypointAttached = null;
            }
            item.NextThink = 0f;
            return;
        }

        // QC Item_Show(this, !this.state). StartItem sets this.state = 0 right before Item_Reset, so at spawn
        // !state == 1 (show). The port has no separate item .state field (the reset-infra re-call path is dormant
        // — see TargetUtilities), so the spawn path always shows; powerups are then re-hidden below.
        ItemPickupRules.Show(item, 1);

        if (item.ItemIsLoot)
            return;

        item.Think = ItemPickupRules.ItemThink;
        item.NextThink = Now;
        item.Active = MapMover.ActiveActive; // QC items.qc:802 this.active = ACTIVE_ACTIVE
        // QC items.qc:803-806: drop any lingering respawn-countdown waypoint when (re)resetting a non-loot item.
        if (item.WaypointAttached is Waypoints.WaypointSprite rwp)
        {
            Waypoints.WaypointSprites.Kill(rwp);
            item.WaypointAttached = null;
        }

        // QC: do NOT spawn powerups (or superweapon weapons) initially — schedule their first appearance instead.
        bool isPowerup = item.Pickup?.IsPowerup == true;
        bool isSuperWeapon = OwnsSuperWeapon(item);
        if (isPowerup || isSuperWeapon)
            ItemPickupRules.ScheduleInitialRespawn(item, GameStartTime);
    }

    // =====================================================================================
    //  helpers
    // =====================================================================================

    // QC have_pickup_item (items.qc:112).
    private static bool HavePickupItem(Entity item, Pickup def)
    {
        // QC: mutator-blocked def never spawns.
        if ((def.ItemDef.SpawnFlags & GameItemSpawnFlag.MutatorBlocked) != 0)
            return false;

        if (!def.IsPowerup)
        {
            // QC: g_pickup_items > 0 forces spawn; == 0 removes all; -1 = gametype default (proceed).
            int pickupItems = PickupItemsCvar();
            if (pickupItems > 0) return true;
            if (pickupItems == 0) return false;
            // QC weaponarena gate: no weapon/ammo pickups in an arena (the port reads g_weaponarena as a bool).
            if (Api.Services is not null && Api.Cvars.GetFloat("g_weaponarena") != 0f)
            {
                if (def.IsWeaponPickup || def.IsAmmo)
                    return false;
            }
        }
        return true;
    }

    // QC autocvar_g_pickup_items: shipped default -1 (gametype default). In a bare port with no gametype-default
    // computation, an UNSET cvar is treated as -1 (proceed), and the seeded default (Cvars.cs sets 1) forces spawn.
    private static int PickupItemsCvar()
    {
        if (Api.Services is null) return -1;
        string s = Api.Cvars.GetString("g_pickup_items");
        if (string.IsNullOrEmpty(s)) return -1;
        return (int)Api.Cvars.GetFloat("g_pickup_items");
    }

    private static bool OwnsSuperWeapon(Entity item)
    {
        foreach (var w in item.OwnedWeaponSet.Weapons())
            if (w.IsSuperWeapon) return true;
        return false;
    }

    // QC: this.classname = def.m_canonical_spawnfunc — the canonical "item_health_small" style classname. We map
    // each NetName to its spawnfunc classname; an unknown NetName keeps the def's NetName (defensive).
    private static string CanonicalClassName(Pickup def) => ItemSpawnFuncs.CanonicalSpawnFunc(def);

    // =====================================================================================
    //  Item_FindTeam (items.qc:813) — one-at-a-time team-item random select.
    // =====================================================================================

    /// <summary>
    /// QC <c>Item_FindTeam(this)</c> (items.qc:813): among all non-freed world items sharing the same
    /// <see cref="Entity.Team"/> key as <paramref name="anchor"/>, pick ONE at random (weighted by
    /// <c>.cnt</c>) and show it; hide all others and clear their think timers. The chosen item gets
    /// <c>Item_Reset</c> so it either appears immediately or schedules its initial respawn (powerups).
    /// The anchor is allowed to be any member of the group — only its team key matters.
    ///
    /// <para>
    /// In Base this is deferred via <c>InitializeEntity(this, Item_FindTeam, INITPRIO_FINDTARGET)</c> so it
    /// runs after all map entities are spawned. The port has no INITPRIO queue; instead
    /// <see cref="SpawnInternal"/> calls this directly from the spawn tail. Because the BSP entity lump is
    /// fully parsed before the first gameplay tick, ALL items with the same team key exist in the entity
    /// table by the time ANY spawn tail runs (the lump is a sequential list), so the direct call sees the
    /// complete group. (The only edge case — a teamed item spawned mid-game — would need a deferred path
    /// that doesn't exist yet; no stock Base map does this.)
    /// </para>
    /// </summary>
    public static void FindTeam(Entity anchor)
    {
        float teamKey = anchor.Team;
        if (teamKey == 0f) return;

        // QC: RandomSelection_Init(); IL_EACH(g_items, it.team == this.team, RandomSelection_AddEnt(it, it.cnt, 0))
        // Collect all registered items (FL_ITEM set, not freed) with the same team key.
        var sel = new MapMover.RandomSelection();
        sel.Reset();

        // prefer Api.Entities.All (single scan, O(N)); fall back to findchain-by-flag (headless test fakes).
        if (Api.Services is not null && Api.Entities.All is { } all)
        {
            for (int i = 0; i < all.Count; i++)
            {
                Entity it = all[i];
                if (!it.IsFreed && it.Team == teamKey && it.ItemDefRef is not null)
                    sel.Add(it, it.Cnt != 0 ? it.Cnt : 1, 0f);
            }
        }
        else
        {
            // headless: iterate entities reachable via chain from the index (no All list).
            // At minimum include the anchor itself so the method is never a no-op in tests.
            sel.Add(anchor, anchor.Cnt != 0 ? anchor.Cnt : 1, 0f);
        }

        Entity? chosen = sel.Chosen;
        if (chosen is null) return;

        // QC: hide all members except the chosen one; clear think timers on the hidden ones;
        // call Item_Reset on the chosen member so it shows / schedules its initial respawn normally.
        // Also clear the EF_NOGUNBOB marker on every member except 'this' (the one whose reset pointer
        // stays as Item_FindTeam for map-reset re-rolls) — the port keeps FindTeam wired unconditionally.
        if (Api.Services is not null && Api.Entities.All is { } all2)
        {
            for (int i = 0; i < all2.Count; i++)
            {
                Entity it = all2[i];
                if (!it.IsFreed && it.Team == teamKey && it.ItemDefRef is not null)
                {
                    if (!ReferenceEquals(it, chosen))
                    {
                        // QC: Item_Show(it, -1); kill waypoint; nextthink = 0
                        ItemPickupRules.Show(it, -1);
                        if (it.WaypointAttached is Waypoints.WaypointSprite wp2)
                        {
                            Waypoints.WaypointSprites.Kill(wp2);
                            it.WaypointAttached = null;
                        }
                        it.NextThink = 0f;
                    }
                    else
                    {
                        Item_Reset(it); // QC: Item_Reset(e) for the chosen member
                    }
                }
            }
        }
        else
        {
            // headless: just reset the chosen (anchor)
            Item_Reset(chosen);
        }
    }
}
