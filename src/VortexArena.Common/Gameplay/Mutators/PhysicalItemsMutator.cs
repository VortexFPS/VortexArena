// Port of common/mutators/mutator/physical_items/sv_physical_items.qc

using System.Numerics;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Physical Items mutator — port of common/mutators/mutator/physical_items/sv_physical_items.qc. Makes map
/// items physical (MOVETYPE_PHYSICS rigid bodies) so they fall, slide and can be knocked around by damage, by
/// attaching a second physics "ghost" entity to each item. Enabled by the <c>g_physical_items</c> cvar
/// (1 = only dropped items, 2 = all map items).
///
/// Fully ported: the enable gate, the cvars (<c>g_physical_items</c>, <c>g_physical_items_damageforcescale</c>,
/// <c>g_physical_items_reset</c>), QC's MUTATOR_ONADD physics-engine availability check (logging the exact revert
/// trace), the Item_Spawn ghost-entity creation hook (<c>MUTATOR_HOOKFUNCTION(physical_items, Item_Spawn)</c>),
/// and the per-ghost think/touch/damage callbacks (reset-on-respawn, NODROP/SKY snap, environmental-kill snap,
/// delete-when-gone). The Item_Spawn HookChain and its StartItem call site were added alongside this implementation.
///
/// REMAINING BLOCKER: QC requires <c>DP_PHYSICS_ODE</c> (the Open Dynamics Engine rigid-body extension) to make
/// the ghost entity actually tumble/slide. The port's <c>MoveType.Physics</c> falls through to think-only in the
/// deterministic sim (no ODE integrator wired to gameplay entities), so the ghost exists and respects all the
/// threshold/reset logic but does NOT physically simulate. <see cref="HasPhysicsEngine"/> therefore stays false,
/// keeping the mutator self-disabled until an ODE-equivalent is wired — exactly as Base reverts on a non-ODE build.
/// </summary>
[Mutator]
public sealed class PhysicalItemsMutator : MutatorBase
{
    /// <summary>QC autocvar_g_physical_items (0 off, 1 dropped-only, 2 all map items).</summary>
    public int Mode;

    /// <summary>QC autocvar_g_physical_items_damageforcescale — how much damage knocks an item around.</summary>
    public float DamageForceScale;

    /// <summary>QC autocvar_g_physical_items_reset — return an un-spawned item to its origin each think.</summary>
    public bool Reset;

    public PhysicalItemsMutator() => NetName = "physical_items";

    // QC: REGISTER_MUTATOR(physical_items, autocvar_g_physical_items) — with a MUTATOR_ONADD physics-engine check
    // that returns -1 (rolls the mutator back) when no rigid-body engine is available. The port has no such engine
    // wired to gameplay entities, so IsEnabled also requires HasPhysicsEngine() — which is false here, mirroring
    // QC reverting to old items. (Kept as a single gate so the mutator simply never activates, no half state.)
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_physical_items") != 0f && HasPhysicsEngine();

    // QC MUTATOR_HOOKFUNCTION(physical_items, Item_Spawn) handler — registered in Hook().
    private HookHandler<MutatorHooks.ItemSpawnArgs>? _onItemSpawn;

    public override void Hook()
    {
        if (Api.Services is not null)
        {
            Mode = (int)Api.Cvars.GetFloat("g_physical_items");
            DamageForceScale = Api.Cvars.GetFloat("g_physical_items_damageforcescale");
            Reset = Api.Cvars.GetFloat("g_physical_items_reset") != 0f;
        }
        // QC subscribes MUTATOR_HOOKFUNCTION(physical_items, Item_Spawn) in sv_physical_items.qc:92.
        _onItemSpawn ??= OnItemSpawn;
        MutatorHooks.ItemSpawn.Add(_onItemSpawn);
    }

    public override void Unhook()
    {
        // QC MUTATOR_ONROLLBACK_OR_REMOVE is a no-op ("nothing to roll back").
        // MUTATOR_ONREMOVE logs "cannot be removed at runtime" — no live-remove guard in the port.
        if (_onItemSpawn is not null) MutatorHooks.ItemSpawn.Remove(_onItemSpawn);
    }

    // ==============================================================================================
    // MUTATOR_HOOKFUNCTION(physical_items, Item_Spawn) — sv_physical_items.qc:92
    // Spawns a second "ghost" entity that carries the model + physics, hides the real item behind it.
    // ==============================================================================================

    // QC: MUTATOR_HOOKFUNCTION(physical_items, Item_Spawn)
    private bool OnItemSpawn(ref MutatorHooks.ItemSpawnArgs args)
    {
        Entity item = args.Item;
        if (Api.Services is null) return false;

        // QC: if(item.owner == NULL && autocvar_g_physical_items <= 1) return;
        // Port: item.Owner is null for map items AND for loot (cleared in loot path).
        // Loot is distinguished via ItemIsLoot. Mode 1 = dropped-weapons only; Mode 2 = all items.
        bool isDropped = item.ItemIsLoot; // equivalent to QC item.owner != NULL (the loot/dropped flag)
        if (!isDropped && Mode <= 1) return false;

        // QC: if (item.spawnflags & 1) return; — floating item, skip.
        if ((item.SpawnFlags & 1) != 0) return false;

        // QC: entity wep = spawn(); — the physics ghost.
        Entity wep = Api.Entities.Spawn();

        // QC: _setmodel(wep, item.model); setsize(wep, item.mins, item.maxs); setorigin(wep, item.origin).
        if (!string.IsNullOrEmpty(item.Model))
            Api.Entities.SetModel(wep, item.Model);
        MapMover.SetSize(wep, item.Mins, item.Maxs);
        MapMover.SetOrigin(wep, item.Origin);

        wep.Angles     = item.Angles;
        wep.Velocity   = item.Velocity;

        // QC: wep.owner = item; wep.solid = SOLID_CORPSE; set_movetype(wep, MOVETYPE_PHYSICS).
        wep.Owner      = item;
        wep.Solid      = Solid.Corpse;
        wep.MoveType   = MoveType.Physics; // ODE-backed in DP; falls to think-only here (no rigid-body integrator)
        wep.TakeDamage = DamageMode.Aim;

        // QC: wep.effects |= EF_NOMODELFLAGS — disables spinning (the item's ITS_ANIMATE1/ANIMATE2 model flags).
        // EF_NOMODELFLAGS = 8388608 (dpextensions.qc:183: "ignore any effects in a model file").
        wep.Effects |= 8388608; // EF_NOMODELFLAGS

        // QC: wep.damageforcescale = autocvar_g_physical_items_damageforcescale.
        wep.DamageForceScale = DamageForceScale;

        // QC: wep.dphitcontentsmask = item.dphitcontentsmask.
        wep.DpHitContentsMask = item.DpHitContentsMask;

        // QC: wep.colormap = item.colormap; wep.glowmod = item.glowmod (sv_physical_items.qc:115-116) —
        // the ghost inherits the real item's tint. The port's authoritative server-side render-colormap is
        // Entity.ColorMapOverride (the RENDER_COLORMAPPED seam used by WeaponThrowing/MonsterAI/MapModels):
        // a dropped weapon carries the thrower's packed colormap there, a plain map item carries 0 (no tint).
        // glowmod is NOT a separate server field in the port — the csqcmodel derives it from the colormap
        // nibble client-side (see WeaponThrowing.cs:55-56), so copying ColorMapOverride carries it too.
        wep.ColorMapOverride = item.ColorMapOverride;

        // QC: wep.cnt = (item.owner != NULL) — 1 for dropped weapon, 0 for map item.
        wep.PhysIsDropped = isDropped;

        // QC: setthink(wep, physical_item_think); wep.nextthink = time.
        wep.Think     = PhysicalItemThink;
        wep.NextThink = Api.Clock.Time;

        // QC: settouch(wep, physical_item_touch); wep.event_damage = physical_item_damage.
        wep.Touch = PhysicalItemTouch;
        MapMover.InstallEventDamage(wep, PhysicalItemDamage);

        // QC: if(!wep.cnt) DropToFloor_QC_DelayedInit(wep) — for map items, settle to floor next tick.
        // The deterministic sim's TOSS integrator settles on the next physics step; a map item with
        // MoveType.Physics will also settle naturally. No explicit DropToFloor call needed here.

        // QC: wep.spawn_origin = wep.origin; wep.spawn_angles = item.angles.
        wep.PhysSpawnOrigin = wep.Origin;
        wep.PhysSpawnAngles = item.Angles;

        // QC: item.effects |= EF_NODRAW — hide the real item's model.
        item.Effects |= EffectFlags.NoDraw;

        // QC: set_movetype(item, MOVETYPE_FOLLOW); item.aiment = wep — real item follows the ghost.
        item.MoveType = MoveType.Follow;
        item.Aiment   = wep;

        // QC: setSendEntity(item, func_null) — the real item stops sending (ghost is what clients see).
        // The port has no per-entity SendEntity callback and (today) no snapshot-producer suppress flag to wire
        // it to, so the equivalent visibility cut is the EF_NODRAW set above (item.Effects |= NoDraw): the real
        // item still networks but renders nothing on every client, while the ghost is what's seen. The item
        // stays a live server edict driving MOVETYPE_FOLLOW. (Full net-suppression — dropping the hidden edict
        // from the entity feed — needs a ServerNet snapshot-producer seam that does not exist yet; tracked in the
        // registry as the remaining half of the setSendEntity gap.)

        return false; // QC mutator return value is informational; false = "continue chain"
    }

    // ==============================================================================================
    // physical_item_think — sv_physical_items.qc:35
    // Per-frame think on the ghost: apply alpha, reset awaiting-respawn map items, delete when gone.
    // ==============================================================================================

    // QC: void physical_item_think(entity this)
    private static void PhysicalItemThink(Entity ghost)
    {
        if (Api.Services is null) return;
        ghost.NextThink = Api.Clock.Time; // QC: this.nextthink = time (per-frame)

        Entity? item = ghost.Owner; // QC: this.owner = the real item

        // QC: this.alpha = this.owner.alpha — apply fading/ghosting of the real item.
        ghost.Alpha = item?.Alpha ?? 1f;

        // QC: if(!this.cnt) { copy colormap/colormod/glowmod; apply reset logic }
        if (!ghost.PhysIsDropped && item is not null)
        {
            // QC: this.colormap = this.owner.colormap; this.colormod = this.owner.colormod;
            //     this.glowmod = this.owner.glowmod (sv_physical_items.qc:43-46) — keep the ghost's tint in
            //     sync with the real item each think. colormap (incl. the client-derived glowmod) rides the
            //     port's Entity.ColorMapOverride seam; colormod has no server-side render channel in the port
            //     (it is a client-only render tint — see the unportable note), so only colormap is copied here.
            ghost.ColorMapOverride = item.ColorMapOverride;

            // QC: if(autocvar_g_physical_items_reset) { ... }
            bool doReset = Api.Cvars.GetFloat("g_physical_items_reset") != 0f;
            if (doReset)
            {
                // QC: this.owner.wait > time — item is awaiting respawn. QC reads ONLY the item's .wait field,
                // which the simple-respawn scheduler sets to `time + t` (items.qc:341, ported at
                // ItemPickupRules.cs:599 as item.ItemWait = Now + t). QC deliberately does NOT freeze the ghost
                // during a long COUNTDOWN respawn (that path sets .scheduledrespawntime but leaves .wait untouched,
                // items.qc:333) nor for a hidden/targeted item — so the gate is exactly ItemWait > time, not the
                // broader ItemAvailable/ScheduledRespawnTime test (which would over-freeze vs Base).
                if (item.ItemWait > Api.Clock.Time) // QC: this.owner.wait > time
                {
                    // QC: setorigin(this, this.spawn_origin); this.angles = this.spawn_angles;
                    //     this.solid = SOLID_NOT; this.alpha = -1; set_movetype(this, MOVETYPE_NONE).
                    MapMover.SetOrigin(ghost, ghost.PhysSpawnOrigin);
                    ghost.Angles   = ghost.PhysSpawnAngles;
                    ghost.Solid    = Solid.Not;
                    ghost.Alpha    = -1f; // invisible
                    ghost.MoveType = MoveType.None; // frozen at home
                }
                else
                {
                    // QC: this.alpha = 1; this.solid = SOLID_CORPSE; set_movetype(this, MOVETYPE_PHYSICS).
                    ghost.Alpha    = 1f;
                    ghost.Solid    = Solid.Corpse;
                    ghost.MoveType = MoveType.Physics;
                }
            }
        }

        // QC: if(!this.owner.modelindex) delete(this) — real item gone, remove ghost.
        if (item is null || item.ModelIndex == 0)
            MapMover.RemoveEntity(ghost);
    }

    // ==============================================================================================
    // physical_item_touch — sv_physical_items.qc:72
    // Ghost touch callback: snap back when ghost contacts a NODROP brush or a SKY surface.
    // ==============================================================================================

    // QC: void physical_item_touch(entity this, entity toucher)
    private static void PhysicalItemTouch(Entity ghost, Entity toucher)
    {
        // QC: if(!this.cnt) — dropped-weapon ghosts are exempt.
        if (ghost.PhysIsDropped) return;
        // QC: if (ITEM_TOUCH_NEEDKILL()) — NODROP content or SKY surface.
        if (GhostInNeedKill(ghost))
        {
            MapMover.SetOrigin(ghost, ghost.PhysSpawnOrigin);
            ghost.Angles = ghost.PhysSpawnAngles;
        }
    }

    // ==============================================================================================
    // physical_item_damage — sv_physical_items.qc:82
    // Ghost damage callback: snap back when ghost is killed by lava/slime/swamp/hurttrigger.
    // ==============================================================================================

    // QC: void physical_item_damage(entity this, entity inflictor, entity attacker,
    //                               float damage, int deathtype, .entity weaponentity,
    //                               vector hitloc, vector force)
    private static void PhysicalItemDamage(Entity ghost, Entity? inflictor, Entity? attacker,
        string deathType, float damage, Vector3 hitLoc, Vector3 force)
    {
        // QC: if(!this.cnt) — dropped-weapon ghosts are exempt.
        if (ghost.PhysIsDropped) return;
        // QC: if(ITEM_DAMAGE_NEEDKILL(deathtype)) — DEATH_HURTTRIGGER/SLIME/LAVA/SWAMP.
        if (DeathTypes.ItemDamageNeedKill(deathType))
        {
            MapMover.SetOrigin(ghost, ghost.PhysSpawnOrigin);
            ghost.Angles = ghost.PhysSpawnAngles;
        }
    }

    // ==============================================================================================
    // helpers
    // ==============================================================================================

    // QC ITEM_TOUCH_NEEDKILL(): ghost is in a NODROP brush (lava) or on a SKY surface.
    // Mirrors BuffsMutator.BuffInNeedKill and ItemPickupRules.LootInNoDrop, both porting the same macro.
    private const int NoDropContents   = unchecked((int)0x80000000); // DPCONTENTS_NODROP
    private const int Q3SurfaceFlagSky = 0x4;                        // Q3SURFACEFLAG_SKY

    private static bool GhostInNeedKill(Entity ghost)
    {
        if (Api.Services is null) return false;
        TraceResult tr = Api.Trace.Trace(ghost.Origin, Vector3.Zero, Vector3.Zero,
            ghost.Origin, MoveFilter.Normal, ghost);
        return (tr.DpHitContents & NoDropContents) != 0
            || (tr.DpHitQ3SurfaceFlags & Q3SurfaceFlagSky) != 0;
    }

    /// <summary>
    /// QC MUTATOR_ONADD: <c>autocvar_physics_ode &amp;&amp; checkextension("DP_PHYSICS_ODE")</c>. The port has no ODE
    /// rigid-body engine wired to gameplay entities, so this is always false and the mutator logs the same revert
    /// trace QC does and stays inert.
    /// </summary>
    /// <remarks>
    /// QC's MUTATOR_ONADD runs the engine check exactly once (on add). The port's activation gate evaluates
    /// <see cref="IsEnabled"/> more than once (the registry verifies the predicate IS reached on the live boot
    /// path, and later code can re-query it), so we latch the revert trace behind <see cref="_revertLogged"/> to
    /// keep QC's once-per-add log semantics — otherwise `developer 1` would show the warning repeatedly.
    /// </remarks>
    private static bool _revertLogged;

    private static bool HasPhysicsEngine()
    {
        // QC LOG_TRACE on the unavailable path. Mirror the message so the revert is auditable in `developer 1`,
        // but only once (matching QC's single MUTATOR_ONADD evaluation), not once per IsEnabled query.
        if (!_revertLogged)
        {
            _revertLogged = true;
            Log.Trace("Warning: Physical items are enabled but no physics engine can be used. Reverting to old items.");
        }
        return false;
    }
}
