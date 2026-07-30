// port: qcsrc/common/resources/resources.qh (the per-entity resource fields)
//
// In QuakeC every resource is a flat entity field (.health, .armorvalue, .ammo_shells, ...).
// The C# entity-model (planning/specs/entity-model.md, ADR-0007) promotes these to typed members.
// Health already lives on the base Entity (engine-shared); the *gameplay* resource amounts
// (armor + the five ammo pools) are added here via the partial Entity class. We deliberately keep
// engine fields OUT of this file — only the resource amounts QC stored in .armorvalue / .ammo_*.
//
// Entity is declared partial, and this lives in a new file, so extending it here is allowed by the
// task constraints (no existing file is modified).

namespace XonoticGodot.Common.Framework
{
    public partial class Entity
    {
        // QC .armorvalue
        public float ArmorValue;

        // QC .ammo_shells / .ammo_nails (bullets) / .ammo_rockets / .ammo_cells / .ammo_fuel
        public float AmmoShells;
        public float AmmoBullets;
        public float AmmoRockets;
        public float AmmoCells;
        public float AmmoFuel;

        // QC item.max_armorvalue — per-entity armor cap used by armor pickups
        // (see common/items/item/armor.qh item_armor*_init). Health's cap reuses the base MaxHealth.
        public float MaxArmorValue;

        // QC item.count — Q3-compat override of the pickup amount (health.qh: q3compat && item.count).
        public int Count;
    }
}
