// Port of common/items/item/health.{qh,qc}: CLASS(Health, Pickup) + HealthSmall/Medium/Big/Mega, and each
// item_health*_init (the m_iteminit that seeds max_health from g_pickup_health*_max and RES_HEALTH from
// g_pickup_health*, with the q3-compat .count override). The give is Item_GiveAmmoTo for RES_HEALTH, reading
// the world item's per-spawn cap (QC item.max_health) — set by ItemInit at spawn.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Base for health pickups — port of common/items/item/health.{qh,qc} CLASS(Health, Pickup) and its
/// concrete subclasses. A resource item: on pickup it gives RES_HEALTH up to a cap.
///
/// QC handed the give amount + per-item cap to Item_GiveTo via the world item's stored resource and
/// item.max_health (set in item_health*_init from g_pickup_health*_max). <see cref="ItemInit"/> seeds those
/// onto the world edict at spawn; <see cref="GiveTo"/> applies Item_GiveAmmoTo reading the edict's cap.
/// </summary>
public abstract class HealthPickup : Pickup
{
    /// <summary>Amount of health given (QC g_pickup_health*); the cvar fallback (stock balance value).</summary>
    public float Amount;

    /// <summary>Per-pickup health cap fallback (QC g_pickup_health*_max -> item.max_health). Stock = 200.</summary>
    public float MaxAmount = 200f;

    /// <summary>QC m_color — '1 0 0' for all health.</summary>
    public Vector3 Color = new(1f, 0f, 0f);

    /// <summary>The g_pickup_* cvar name this item's amount comes from (QC item_health*_init).</summary>
    protected string AmountCvar = "";

    /// <summary>The g_pickup_*_max cvar name this item's cap comes from (QC item_health*_init).</summary>
    protected string MaxCvar = "";

    /// <summary>QC instanceOfHealth — true for every health pickup.</summary>
    public override bool IsHealth => true;

    /// <summary>World-item model name (resolved by the model service); set by subclasses, which also set <see cref="Pickup.Model"/>.</summary>
    protected string? ItemModel;

    /// <summary>
    /// QC item_health*_init: seed the world item's per-spawn cap (max_health) from g_pickup_health*_max and
    /// the given health (RES_HEALTH) from g_pickup_health* (q3-compat .count overrides), each only when unset,
    /// with the stock value as the CvarOr fallback (so a bare unit test still gets authentic amounts).
    /// </summary>
    public override void ItemInit(Entity item)
    {
        if (item.MaxHealth == 0f)
            item.MaxHealth = ItemPickupRules.CvarOr(MaxCvar, MaxAmount);
        if (item.GetResource(ResourceType.Health) == 0f)
        {
            // QC: q3compat && item.count ? item.count : autocvar_g_pickup_healthX. The port has no live q3compat
            // flag in this layer; honour a non-zero .count override (set from the map's "count" key) like QC.
            float amount = item.Count != 0 ? item.Count : ItemPickupRules.CvarOr(AmountCvar, Amount);
            item.SetResourceExplicit(ResourceType.Health, amount);
        }
    }

    /// <summary>
    /// Item_GiveTo for a health resource item (QC Item_GiveAmmoTo for RES_HEALTH): bump the player's health
    /// toward the world item's cap (QC item.max_health, seeded by <see cref="ItemInit"/>). Returns true if any
    /// health was given. The pickup sound + respawn scheduling are applied by <see cref="ItemPickupRules.GiveTo"/>.
    /// </summary>
    public override bool GiveTo(Entity player, Entity worldItem)
    {
        // QC passes item.max_health as the cap. Fall back to the def cap if ItemInit wasn't run (defensive).
        float cap = worldItem.MaxHealth > 0f ? worldItem.MaxHealth : MaxAmount;
        float amount = worldItem.GetResource(ResourceType.Health);
        if (amount == 0f) amount = Amount; // defensive (ItemInit not run)
        int pickupAnyway = System.Math.Max(worldItem.PickupAnyway, ItemDef.PickupAnyway);
        return ItemPickupRules.GiveAmmoTo(worldItem, player, ResourceType.Health, amount, cap, pickupAnyway);
    }
}

/// <summary>HealthSmall — item_health_small (+5, cap 200). common/items/item/health.qh.</summary>
[Item]
public sealed class HealthSmall : HealthPickup
{
    public override bool IsSmall => true; // QC IS_SMALL: no ###item### target
    public HealthSmall()
    {
        NetName = "health_small";
        DisplayName = "Small health";
        ItemModel = "g_h1.md3";    // MDL_HealthSmall_ITEM
        Model = ItemModel;
        Amount = 5f;  AmountCvar = "g_pickup_healthsmall";
        MaxAmount = 200f; MaxCvar = "g_pickup_healthsmall_max";
        RespawnTime = 15f; // g_pickup_respawntime_health_small
        ItemDef.PickupSound = "HealthSmall";
        ItemDef.Color = Color;
    }
}

/// <summary>HealthMedium — item_health_medium (+25, cap 200). common/items/item/health.qh.</summary>
[Item]
public sealed class HealthMedium : HealthPickup
{
    public HealthMedium()
    {
        NetName = "health_medium";
        DisplayName = "Medium health";
        ItemModel = "g_h25.md3";   // MDL_HealthMedium_ITEM
        Model = ItemModel;
        Amount = 25f; AmountCvar = "g_pickup_healthmedium";
        MaxAmount = 200f; MaxCvar = "g_pickup_healthmedium_max";
        RespawnTime = 15f; // g_pickup_respawntime_health_medium
        ItemDef.PickupSound = "HealthMedium";
        ItemDef.Color = Color;
    }
}

/// <summary>HealthBig — item_health_big (+50, cap 200, large box). common/items/item/health.qh.</summary>
[Item]
public sealed class HealthBig : HealthPickup
{
    public HealthBig()
    {
        NetName = "health_big";
        DisplayName = "Big health";
        ItemModel = "g_h50.md3";   // MDL_HealthBig_ITEM
        Model = ItemModel;
        Amount = 50f; AmountCvar = "g_pickup_healthbig";
        MaxAmount = 200f; MaxCvar = "g_pickup_healthbig_max";
        RespawnTime = 20f; // g_pickup_respawntime_health_big
        // HealthBig keeps the default (small) bbox in QC (only m_maxs of mega is ITEM_L_MAXS); QC health.qh does
        // NOT set HealthBig.m_maxs, so it inherits the small box like medium. (Big armor likewise.)
        ItemDef.PickupSound = "HealthBig";
        ItemDef.Color = Color;
    }
}

/// <summary>HealthMega — item_health_mega (+100, cap 200, large box, glow, MEGAHEALTH sound). health.qh.</summary>
[Item]
public sealed class HealthMega : HealthPickup
{
    public HealthMega()
    {
        NetName = "health_mega";
        DisplayName = "Mega health";
        ItemModel = "g_h100.md3";  // MDL_HealthMega_ITEM
        Model = ItemModel;
        Amount = 100f; AmountCvar = "g_pickup_healthmega";
        MaxAmount = 200f; MaxCvar = "g_pickup_healthmega_max";
        RespawnTime = 30f; // g_pickup_respawntime_health_mega
        Maxs = ItemBoxes.LargeMaxs;   // QC HealthMega m_maxs = ITEM_L_MAXS
        Mins = ItemBoxes.DefaultMins; // large items use the default mins
        // Mega health uses the dedicated megahealth pickup sound (QC MDL/SND_HealthMega). It does NOT glow in QC
        // (no m_glow ATTRIB on HealthMega — only powerups glow), but it has wpblink 2 (waypoint only).
        ItemDef.PickupSound = "MEGAHEALTH";
        ItemDef.Color = Color;
    }
}
