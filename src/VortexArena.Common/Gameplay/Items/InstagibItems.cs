// Port of common/mutators/mutator/instagib/items.qh: CLASS(VaporizerCells, Ammo) and CLASS(ExtraLife, Powerup),
// their spawnfuncs (item_vaporizer_cells + item_minst_cells alias + item_extralife), and the per-item init that
// seeds the cell count from g_instagib_ammo_drop (ammo_vaporizercells_init) or blocks/unblocks the item.
//
// These are mutator-economy items: they only appear on the map during an active instagib match (via the random-
// powerup replacement deck or Devastator/Vortex replacement) and are never MutatorBlocked by default in the
// port (unlike Base where ONADD/ONREMOVE toggle ITEM_FLAG_MUTATORBLOCKED; the port's FilterItemDefinition hook
// already gates their presence for non-instagib matches). Their spawnfuncs are registered in ItemSpawnFuncs.

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// VaporizerCells — the instagib ammo item (<c>item_vaporizer_cells</c>, alias <c>item_minst_cells</c>).
/// Port of CLASS(VaporizerCells, Ammo) in items.qh: RES_CELLS, model <c>a_cells.md3</c>, icon
/// <c>ammo_supercells</c>, respawn 45 s, botvalue 2000. <see cref="ItemInit"/> seeds the cell count from
/// <c>g_instagib_ammo_drop</c> (default 5) when the world item doesn't already carry cells (QC
/// <c>ammo_vaporizercells_init</c>). The give is Item_GiveAmmoTo via the base AmmoPickup path.
/// </summary>
[Item]
public sealed class VaporizerCellsItem : AmmoPickup
{
    public VaporizerCellsItem()
    {
        NetName = "vaporizer_cells";
        DisplayName = "Vaporizer ammo";
        Resource = ResourceType.Cells;
        Amount = 5f;          // g_instagib_ammo_drop default 5 (items.qh ammo_vaporizercells_init)
        AmountCvar = "";      // use the per-item Amount; the cvar read is done in ItemInit below
        Model = "a_cells.md3"; // MDL_VaporizerCells_ITEM
        RespawnTime = 45f;    // items.qh m_respawntime 45
        Color = new System.Numerics.Vector3(0.816f, 0.941f, 0.541f); // items.qh m_color (HUD tint)
        ItemDef.Color = Color;
        ItemDef.PickupSound = "ITEMPICKUP"; // SND_VaporizerCells (Item_Sound("itempickup"))
        // Bot value 2000 noted; the port has no dedicated bot-value field (the base AmmoPickup doesn't either),
        // so this is recorded here as documentation. The bot AI will treat it as a cells pickup.
    }

    /// <summary>
    /// QC <c>ammo_vaporizercells_init(def, item)</c> (items.qh:14-17): seed cells from
    /// <c>g_instagib_ammo_drop</c> (default 5) only when the entity doesn't already carry cells (a FilterItem
    /// override may have pre-seeded it to a specific count). Unlike the other ammo items this uses a specific
    /// instagib cvar, not the generic g_pickup_cells.
    /// </summary>
    public override void ItemInit(Entity item)
    {
        if (item.GetResource(ResourceType.Cells) == 0f)
        {
            float drop = Api.Services is not null
                ? ItemPickupRules.CvarOr("g_instagib_ammo_drop", Amount)
                : Amount;
            item.SetResourceExplicit(ResourceType.Cells, drop);
        }
    }
}

/// <summary>
/// ExtraLife — the instagib extra-life powerup (<c>item_extralife</c>). Port of CLASS(ExtraLife, Powerup) in
/// items.qh: model <c>g_h100.md3</c>, icon <c>item_mega_health</c>, waypoint "Extra life" (blink 2). The give
/// is NOT through the normal powerup timer path — it is intercepted by InstagibMutator.OnItemTouch which grants
/// <c>g_instagib_extralives</c> armor "lives" (default 1) and returns MUT_ITEMTOUCH_PICKUP (consumed).
/// </summary>
[Item]
public sealed class ExtraLifeItem : PowerupPickup
{
    public ExtraLifeItem()
    {
        NetName = "extralife";          // QC ExtraLife.netname = "extralife"
        DisplayName = "Extra life";
        Model = "g_h100.md3";           // MDL_ExtraLife_ITEM
        // Large powerup bbox (from PowerupPickup base), 120 s respawn, IsPowerup = true.
        ItemDef.ItemId = ItemFlag.None;  // no IT_* carried bit; the give is fully custom (OnItemTouch)
        ItemDef.Color = new System.Numerics.Vector3(1f, 0f, 0f); // items.qh m_color red
        ItemDef.PickupSound = "MEGAHEALTH"; // SND_ExtraLife = Item_Sound("megahealth") (items.qh)
        // Waypoint "Extra life", blink 2 — bot-nav; recorded but the port has no waypoint blink system.
    }

    /// <summary>
    /// QC ExtraLife has no SVQC m_iteminit (only IT_RESOURCE). The base PowerupPickup.ApplyMutatorBlock runs the
    /// g_powerups / g_powerups_extralife gate — but ExtraLife is an instagib-only item that should only be
    /// blocked by instagib being off (the FilterItemDefinition hook already gates it in non-instagib matches).
    /// Use a bare no-op: have_pickup_item will not block it (no MUTATORBLOCKED flag) and InstagibMutator's
    /// FilterItemDefinition returns false (keep) for "extralife" so it never sees the global powerup block.
    /// </summary>
    public override void ItemInit(Entity item) { /* instagib items are not blocked by g_powerups */ }
}
