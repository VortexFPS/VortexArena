// Port of common/items/item/armor.{qh,qc}: CLASS(Armor, Pickup) + ArmorSmall/Medium/Big/Mega, and each
// item_armor*_init (the m_iteminit seeding max_armorvalue from g_pickup_armor*_max and RES_ARMOR from
// g_pickup_armor*). The give is Item_GiveAmmoTo for RES_ARMOR reading the world item's per-spawn cap
// (QC item.max_armorvalue), set by ItemInit at spawn.

using System.Numerics;
using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Base for armor pickups — port of common/items/item/armor.{qh,qc} CLASS(Armor, Pickup) and its
/// concrete subclasses. A resource item: on pickup it gives RES_ARMOR up to a cap
/// (g_pickup_armor*_max -> item.max_armorvalue).
/// </summary>
public abstract class ArmorPickup : Pickup
{
    /// <summary>Amount of armor given (QC g_pickup_armor*); the cvar fallback (stock balance value).</summary>
    public float Amount;

    /// <summary>Per-pickup armor cap fallback (QC g_pickup_armor*_max -> item.max_armorvalue). Stock = 200.</summary>
    public float MaxAmount = 200f;

    /// <summary>QC m_color — '0 1 0' for all armor.</summary>
    public Vector3 Color = new(0f, 1f, 0f);

    /// <summary>The g_pickup_* cvar name this item's amount comes from (QC item_armor*_init).</summary>
    protected string AmountCvar = "";

    /// <summary>The g_pickup_*_max cvar name this item's cap comes from (QC item_armor*_init).</summary>
    protected string MaxCvar = "";

    /// <summary>QC instanceOfArmor — true for every armor pickup.</summary>
    public override bool IsArmor => true;

    /// <summary>World-item model name; set by subclasses.</summary>
    protected string? ItemModel;

    /// <summary>
    /// QC item_armor*_init: seed the world item's per-spawn cap (max_armorvalue) from g_pickup_armor*_max and
    /// the given armor (RES_ARMOR) from g_pickup_armor*, each only when unset, with the stock value as fallback.
    /// (Armor's init has no q3-compat .count override — unlike health.)
    /// </summary>
    public override void ItemInit(Entity item)
    {
        if (item.MaxArmorValue == 0f)
            item.MaxArmorValue = ItemPickupRules.CvarOr(MaxCvar, MaxAmount);
        if (item.GetResource(ResourceType.Armor) == 0f)
            item.SetResourceExplicit(ResourceType.Armor, ItemPickupRules.CvarOr(AmountCvar, Amount));
    }

    /// <summary>
    /// Item_GiveTo for an armor resource item (QC Item_GiveAmmoTo for RES_ARMOR) — bump RES_ARMOR toward the
    /// world item's cap (QC item.max_armorvalue), honouring pickup-anyway. Returns true if any armor was given.
    /// </summary>
    public override bool GiveTo(Entity player, Entity worldItem)
    {
        float cap = worldItem.MaxArmorValue > 0f ? worldItem.MaxArmorValue : MaxAmount;
        float amount = worldItem.GetResource(ResourceType.Armor);
        if (amount == 0f) amount = Amount; // defensive (ItemInit not run)
        int pickupAnyway = System.Math.Max(worldItem.PickupAnyway, ItemDef.PickupAnyway);
        return ItemPickupRules.GiveAmmoTo(worldItem, player, ResourceType.Armor, amount, cap, pickupAnyway);
    }
}

/// <summary>ArmorSmall — item_armor_small (+5, cap 200). common/items/item/armor.qh.</summary>
[Item]
public sealed class ArmorSmall : ArmorPickup
{
    public override bool IsSmall => true; // QC IS_SMALL: no ###item### target
    public ArmorSmall()
    {
        NetName = "armor_small";
        DisplayName = "Small armor";
        ItemModel = "item_armor_small.md3"; // MDL_ArmorSmall_ITEM
        Model = ItemModel;
        Amount = 5f;  AmountCvar = "g_pickup_armorsmall";
        MaxAmount = 200f; MaxCvar = "g_pickup_armorsmall_max";
        RespawnTime = 15f; // g_pickup_respawntime_armor_small
        ItemDef.PickupSound = "ArmorSmall";
        ItemDef.Color = Color;
    }
}

/// <summary>ArmorMedium — item_armor_medium (+25, cap 200). common/items/item/armor.qh.</summary>
[Item]
public sealed class ArmorMedium : ArmorPickup
{
    public ArmorMedium()
    {
        NetName = "armor_medium";
        DisplayName = "Medium armor";
        ItemModel = "item_armor_medium.md3"; // MDL_ArmorMedium_ITEM
        Model = ItemModel;
        Amount = 25f; AmountCvar = "g_pickup_armormedium";
        MaxAmount = 200f; MaxCvar = "g_pickup_armormedium_max";
        RespawnTime = 20f; // g_pickup_respawntime_armor_medium
        ItemDef.PickupSound = "ArmorMedium";
        ItemDef.Color = Color;
    }
}

/// <summary>ArmorBig — item_armor_big (+50, cap 200). common/items/item/armor.qh.</summary>
[Item]
public sealed class ArmorBig : ArmorPickup
{
    public ArmorBig()
    {
        NetName = "armor_big";
        DisplayName = "Big armor";
        ItemModel = "item_armor_big.md3"; // MDL_ArmorBig_ITEM
        Model = ItemModel;
        Amount = 50f; AmountCvar = "g_pickup_armorbig";
        MaxAmount = 200f; MaxCvar = "g_pickup_armorbig_max";
        RespawnTime = 30f; // g_pickup_respawntime_armor_big
        // QC ArmorBig keeps the default (small) box (no m_maxs ATTRIB).
        ItemDef.PickupSound = "ArmorBig";
        ItemDef.Color = Color;
    }
}

/// <summary>ArmorMega — item_armor_mega (+100, cap 200, large box, armor25 sound). common/items/item/armor.qh.</summary>
[Item]
public sealed class ArmorMega : ArmorPickup
{
    public ArmorMega()
    {
        NetName = "armor_mega";
        DisplayName = "Mega armor";
        ItemModel = "item_armor_large.md3"; // MDL_ArmorMega_ITEM
        Model = ItemModel;
        Amount = 100f; AmountCvar = "g_pickup_armormega";
        MaxAmount = 200f; MaxCvar = "g_pickup_armormega_max";
        RespawnTime = 30f; // g_pickup_respawntime_armor_mega
        Maxs = ItemBoxes.LargeMaxs;   // QC ArmorMega m_maxs = ITEM_L_MAXS
        Mins = ItemBoxes.DefaultMins;
        ItemDef.PickupSound = "ArmorMega"; // SND_ArmorMega (armor25)
        ItemDef.Color = Color;
    }
}
