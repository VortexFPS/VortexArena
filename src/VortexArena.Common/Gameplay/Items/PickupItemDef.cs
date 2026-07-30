// Port of common/items/item/pickup.qh (CLASS(Pickup)) + the per-item ATTRIBs the SVQC spawn driver reads:
//   m_iteminit (the resource/timer seeding from cvars), m_mins/m_maxs (the world-item bbox), m_respawntime /
//   m_respawntimejitter, and the instanceOf* discriminators (instanceOfHealth/Armor/Ammo/Powerup/WeaponPickup)
//   StartItem branches on.
//
// Extends the shared Pickup base (GameplayBases.cs declares `abstract partial class Pickup`) with the
// GameItem definition concept: every pickup carries a GameItemDef describing its IT_* id, colour, sounds,
// and pickup-anyway override (QC: a Pickup IS a GameItem subclass). Concrete pickups set ItemDef in their
// constructor (the resource pickups default a sensible IT_RESOURCE definition). Keeping this in a partial
// here means no existing file is modified.

using System.Numerics;
using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The world-item bounding boxes — port of the <c>const vector ITEM_*</c> in common/items/item.qh.
/// StartItem <c>setsize(this, def.m_mins, def.m_maxs)</c> from these so the touch-area-grid has a real volume.
/// </summary>
public static class ItemBoxes
{
    /// <summary>QC ITEM_S_MINS '-24 -24 0' — small (health/armor small/medium, ammo).</summary>
    public static readonly Vector3 SmallMins = new(-24f, -24f, 0f);
    /// <summary>QC ITEM_S_MAXS '24 24 48'.</summary>
    public static readonly Vector3 SmallMaxs = new(24f, 24f, 48f);
    /// <summary>QC ITEM_D_MINS '-30 -30 0' — default (the Pickup base box; also the large-item mins).</summary>
    public static readonly Vector3 DefaultMins = new(-30f, -30f, 0f);
    /// <summary>QC ITEM_D_MAXS '30 30 48'.</summary>
    public static readonly Vector3 DefaultMaxs = new(30f, 30f, 48f);
    /// <summary>QC ITEM_L_MAXS '30 30 70' — large (megas, big health, powerups). Mins use <see cref="DefaultMins"/>.</summary>
    public static readonly Vector3 LargeMaxs = new(30f, 30f, 70f);
}

public abstract partial class Pickup
{
    /// <summary>
    /// The shared item metadata (QC the GameItem half of the Pickup): IT_* id, colour, icon, models, and
    /// the pickup/respawn sounds. Defaults to a generic resource definition; concrete pickups replace or
    /// mutate it in their constructor. Never null so call sites (pickup sound, IT_* mask) can read it.
    /// </summary>
    public GameItemDef ItemDef { get; protected set; } = new()
    {
        ItemId = ItemFlag.Resource,
        SpawnFlags = GameItemSpawnFlag.Resource,
        PickupSound = "ITEMPICKUP",
        RespawnSound = "ITEMRESPAWN",
    };

    /// <summary>The pickup sound name played on the toucher (QC item_pickupsound). Convenience over ItemDef.</summary>
    public string PickupSoundName => ItemDef.PickupSound;

    // --- world-item bbox (QC m_mins / m_maxs ATTRIBs) -------------------------------------------------
    /// <summary>QC <c>m_mins</c> — the world-item bbox minimum (StartItem setsize). Defaults to the small box.</summary>
    public Vector3 Mins = ItemBoxes.SmallMins;
    /// <summary>QC <c>m_maxs</c> — the world-item bbox maximum. Small by default; megas/powerups override to large.</summary>
    public Vector3 Maxs = ItemBoxes.SmallMaxs;

    // --- respawn timing (QC m_respawntime / m_respawntimejitter ATTRIBs) ------------------------------
    /// <summary>QC <c>m_respawntime</c> — default seconds before this item respawns (StartItem seeds .respawntime).</summary>
    public float RespawnTime;
    /// <summary>QC <c>m_respawntimejitter</c> — +/- jitter added to the respawn time.</summary>
    public float RespawnTimeJitter;

    // --- instanceOf* discriminators (QC the CLASS hierarchy StartItem branches on) --------------------
    /// <summary>QC <c>instanceOfHealth</c> — a Health pickup (HealthSmall is excluded from the ###item### target).</summary>
    public virtual bool IsHealth => false;
    /// <summary>QC <c>instanceOfArmor</c> — an Armor pickup (ArmorSmall excluded from the ###item### target).</summary>
    public virtual bool IsArmor => false;
    /// <summary>QC <c>instanceOfAmmo</c> — an Ammo pickup (shells/bullets/rockets/cells/fuel).</summary>
    public virtual bool IsAmmo => false;
    /// <summary>QC <c>instanceOfWeaponPickup</c> — a weapon item (mirrors <see cref="GameItemDef.IsWeaponPickup"/>).</summary>
    public bool IsWeaponPickup => ItemDef.IsWeaponPickup;
    /// <summary>QC <c>instanceOfPowerup</c> — a powerup item (mirrors <see cref="GameItemDef.IsPowerup"/>).</summary>
    public bool IsPowerup => ItemDef.IsPowerup;

    /// <summary>
    /// True if this is the SMALL health or SMALL armor (QC <c>IS_SMALL(def)</c>): these are the only
    /// health/armor that do NOT get the <c>###item###</c> findnearest target in StartItem.
    /// </summary>
    public virtual bool IsSmall => false;

    /// <summary>
    /// QC <c>m_iteminit(def, item)</c> — seed the world item's resources / powerup timers / per-item cap from
    /// the <c>g_pickup_*</c> / <c>g_balance_powerup_*</c> cvars, and (for powerups) flag the def
    /// MUTATORBLOCKED when the powerup is disabled. Called by <see cref="StartItem.Spawn"/> BEFORE the
    /// FilterItem hook and the have_pickup_item gate (a mutator may inspect the seeded resources). The base is
    /// a no-op; the resource + powerup pickups override it. <paramref name="item"/> is the spawned world edict.
    /// </summary>
    public virtual void ItemInit(Entity item) { }

    /// <summary>
    /// QC <c>m_spawnfunc_hookreplace(this, e)</c> — called by the spawnfunc (before <c>StartItem</c>) to
    /// optionally REPLACE this def with a different one. The returned def is what <c>StartItem</c> receives; if
    /// it equals <c>this</c> the spawn is normal. The override in <see cref="JetpackItem"/> and
    /// <see cref="FuelRegenItem"/> returns the plain <c>ITEM_Fuel</c> def when the player's start loadout
    /// already grants the held item bit (jetpack/fuelregen), so a map item_jetpack / item_fuel_regen spawns as
    /// a fuel ammo drop instead of a redundant held-bit pickup — exactly like QC jetpack.qc:6-11 and
    /// fuelregen.qc:6-11.
    /// </summary>
    public virtual Pickup SpawnFuncHookReplace(Entity e) => this;
}
