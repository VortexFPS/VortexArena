// Port of common/items/item/ammo.{qh,qc}: CLASS(Ammo, Pickup) + Shells/Bullets/Rockets/Cells/Fuel and each
// ammo_*_init (the m_iteminit seeding RES_* from g_pickup_{shells,nails,rockets,cells,fuel}). The give is
// Item_GiveAmmoTo toward the resource's pickup cap (g_pickup_X_max, read by GetResourceLimit). Respawn time
// is the shared Ammo m_respawntime = g_pickup_respawntime_ammo (10s).

using System.Numerics;
using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Base for ammo pickups — port of common/items/item/ammo.{qh,qc} CLASS(Ammo, Pickup) and its concrete
/// subclasses (Shells, Bullets, Rockets, Cells, Fuel). Each gives a specific RES_* ammo resource toward
/// that resource's pickup cap (QC g_pickup_X -> g_pickup_X_max).
/// </summary>
public abstract class AmmoPickup : Pickup
{
    /// <summary>Which resource this ammo item replenishes (QC the matching RES_* / .ammo_* field).</summary>
    public ResourceType Resource;

    /// <summary>Amount given (QC g_pickup_{shells,nails,rockets,cells,fuel}); the cvar fallback.</summary>
    public float Amount;

    /// <summary>QC m_color — the ammo's HUD tint.</summary>
    public Vector3 Color;

    /// <summary>The g_pickup_* cvar name this item's amount comes from (QC ammo_*_init).</summary>
    protected string AmountCvar = "";

    /// <summary>QC instanceOfAmmo — true for every ammo pickup.</summary>
    public override bool IsAmmo => true;

    /// <summary>World-item model name; set by subclasses.</summary>
    protected string? ItemModel;

    /// <summary>
    /// QC ammo_*_init: seed the given ammo (RES_*) from g_pickup_X only when unset, with the stock value as
    /// the CvarOr fallback. Ammo has no per-item cap field — the give caps at the resource limit
    /// (GetResourceLimit -> g_pickup_X_max), exactly like QC's Item_GiveAmmoTo(autocvar_g_pickup_X_max).
    /// </summary>
    public override void ItemInit(Entity item)
    {
        if (item.GetResource(Resource) == 0f)
            item.SetResourceExplicit(Resource, ItemPickupRules.CvarOr(AmountCvar, Amount));
    }

    /// <summary>
    /// Item_GiveTo for an ammo resource item (QC Item_GiveAmmoTo) — give the world item's stored amount of
    /// <see cref="Resource"/> up to its pickup limit (<see cref="Resources.GetResourceLimit"/> reads
    /// g_pickup_X_max), honouring pickup-anyway. Returns true if any was given.
    /// </summary>
    public override bool GiveTo(Entity player, Entity worldItem)
    {
        float cap = Resources.GetResourceLimit(player, Resource);
        float amount = worldItem.GetResource(Resource);
        if (amount == 0f) amount = Amount; // defensive (ItemInit not run)
        int pickupAnyway = System.Math.Max(worldItem.PickupAnyway, ItemDef.PickupAnyway);
        return ItemPickupRules.GiveAmmoTo(worldItem, player, Resource, amount, cap, pickupAnyway);
    }
}

/// <summary>Shells — item_shells (+15, RES_SHELLS). common/items/item/ammo.qh.</summary>
[Item]
public sealed class Shells : AmmoPickup
{
    public Shells()
    {
        NetName = "shells";
        DisplayName = "Shells";
        Resource = ResourceType.Shells;
        Amount = 15f; AmountCvar = "g_pickup_shells";
        Color = new Vector3(0.604f, 0.647f, 0.671f);
        ItemModel = "a_shells.md3"; // MDL_Shells_ITEM
        Model = ItemModel;
        RespawnTime = 10f; // g_pickup_respawntime_ammo
        ItemDef.Color = Color;
    }
}

/// <summary>Bullets — item_bullets (+80, RES_BULLETS; QC cvar is g_pickup_nails). ammo.qh.</summary>
[Item]
public sealed class Bullets : AmmoPickup
{
    public Bullets()
    {
        NetName = "bullets";
        DisplayName = "Bullets";
        Resource = ResourceType.Bullets;
        Amount = 80f; AmountCvar = "g_pickup_nails";
        Color = new Vector3(0.678f, 0.941f, 0.522f);
        ItemModel = "a_bullets.mdl"; // MDL_Bullets_ITEM
        Model = ItemModel;
        RespawnTime = 10f;
        ItemDef.Color = Color;
    }
}

/// <summary>Rockets — item_rockets (+40, RES_ROCKETS). common/items/item/ammo.qh.</summary>
[Item]
public sealed class Rockets : AmmoPickup
{
    public Rockets()
    {
        NetName = "rockets";
        DisplayName = "Rockets";
        Resource = ResourceType.Rockets;
        Amount = 40f; AmountCvar = "g_pickup_rockets";
        Color = new Vector3(0.918f, 0.686f, 0.525f);
        ItemModel = "a_rockets.md3"; // MDL_Rockets_ITEM
        Model = ItemModel;
        RespawnTime = 10f;
        ItemDef.Color = Color;
    }
}

/// <summary>Cells — item_cells (+30, RES_CELLS; used by the Vortex). common/items/item/ammo.qh.</summary>
[Item]
public sealed class Cells : AmmoPickup
{
    public Cells()
    {
        NetName = "cells";
        DisplayName = "Cells";
        Resource = ResourceType.Cells;
        Amount = 30f; AmountCvar = "g_pickup_cells";
        Color = new Vector3(0.545f, 0.882f, 0.969f);
        ItemModel = "a_cells.md3"; // MDL_Cells_ITEM
        Model = ItemModel;
        RespawnTime = 10f;
        ItemDef.Color = Color;
    }
}

/// <summary>Fuel — item_fuel (+50, RES_FUEL; jetpack). common/items/item/ammo.qh.</summary>
[Item]
public sealed class Fuel : AmmoPickup
{
    public Fuel()
    {
        NetName = "fuel";
        DisplayName = "Fuel";
        Resource = ResourceType.Fuel;
        Amount = 50f; AmountCvar = "g_pickup_fuel";
        Color = new Vector3(0.984f, 0.878f, 0.506f);
        ItemModel = "g_fuel.md3"; // MDL_Fuel_ITEM
        Model = ItemModel;
        RespawnTime = 10f;
        ItemDef.Color = Color;
    }
}
