// The IT_* item-flags model and the GameItem definition concept — the C# successor to
// common/items/item.qh (the IT_* bit constants + the CLASS(GameItem) base) and the way QC marks a player's
// held powerups/keys on .items and tags each Pickup definition with an m_itemid.
//
// In QC every item kind (resource / weapon / powerup / buff) is a GameItem subclass with an m_itemid bit,
// a color, an icon, models/sounds, a respawn sound, and a "pickup anyway" override. Pickups carry that
// definition; world item entities carry per-spawn state (respawn timers, the spawn-shield window, the
// pickup-anyway flag, the team). This file ports the flags + the GameItem metadata; ItemEntityState.cs
// adds the per-spawn fields, and ItemPickupRules.cs ports Item_GiveTo / Item_ScheduleRespawn.

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Item flag bits — the C# successor to QuakeC's <c>IT_*</c> constants (common/items/item.qh). These mark
/// a player's <see cref="XonoticGodot.Common.Framework.Entity.Items"/> field (held powerups, keys, jetpack…)
/// and identify a <see cref="GameItemDef.ItemId"/>. Bit values match QC exactly so any networked
/// <c>.items</c> stat stays compatible.
/// </summary>
[System.Flags]
public enum ItemFlag
{
    None = 0,

    /// <summary>IT_UNLIMITED_AMMO — using a weapon doesn't reduce ammo. BIT(0).</summary>
    UnlimitedAmmo = 1 << 0,
    /// <summary>IT_UNLIMITED_SUPERWEAPONS — superweapons don't expire. BIT(1).</summary>
    UnlimitedSuperweapons = 1 << 1,
    /// <summary>IT_JETPACK — the jetpack item. BIT(2).</summary>
    Jetpack = 1 << 2,
    /// <summary>IT_USING_JETPACK — jetpack button held (confirmation). BIT(3).</summary>
    UsingJetpack = 1 << 3,
    /// <summary>IT_FUEL_REGEN — fuel regeneration trigger. BIT(4).</summary>
    FuelRegen = 1 << 4,
    /// <summary>IT_RESOURCE — marks an item as a resource (health/armor/ammo). BIT(5).</summary>
    Resource = 1 << 5,
    /// <summary>IT_KEY1 — key 1 (door/keyhunt). BIT(6).</summary>
    Key1 = 1 << 6,
    /// <summary>IT_KEY2 — key 2. BIT(7).</summary>
    Key2 = 1 << 7,
    /// <summary>IT_BUFF — buff item marker. BIT(8).</summary>
    Buff = 1 << 8,
    /// <summary>IT_INVISIBILITY — invisibility powerup. BIT(9).</summary>
    Invisibility = 1 << 9,
    /// <summary>IT_INVINCIBLE — shield/invincibility powerup. BIT(10).</summary>
    Invincible = 1 << 10,
    /// <summary>IT_SUPERWEAPON — superweapon (suit). BIT(11).</summary>
    Superweapon = 1 << 11,
    /// <summary>IT_STRENGTH — strength powerup. BIT(12).</summary>
    Strength = 1 << 12,
    /// <summary>IT_SPEED — speed powerup. BIT(13).</summary>
    Speed = 1 << 13,

    /// <summary>
    /// IT_PICKUPMASK — the flags copied straight onto a player's .items when an item is picked up
    /// (strength/invincible are applied via status effects, not this mask). QC:
    /// IT_UNLIMITED_AMMO | IT_UNLIMITED_SUPERWEAPONS | IT_JETPACK | IT_FUEL_REGEN.
    /// </summary>
    PickupMask = UnlimitedAmmo | UnlimitedSuperweapons | Jetpack | FuelRegen,
}

/// <summary>
/// Item definition kinds — the C# successor to QC's <c>ITEM_FLAG_*</c> spawnflags on a GameItem
/// (common/items/item.qh): whether the item is usable normally, blocked by a mutator, or a resource.
/// </summary>
[System.Flags]
public enum GameItemSpawnFlag
{
    /// <summary>ITEM_FLAG_NORMAL — usable during normal gameplay. BIT(0).</summary>
    Normal = 1 << 0,
    /// <summary>ITEM_FLAG_MUTATORBLOCKED — disabled by an active mutator. BIT(1).</summary>
    MutatorBlocked = 1 << 1,
    /// <summary>ITEM_FLAG_RESOURCE — a resource, not a held item. BIT(2).</summary>
    Resource = 1 << 2,
}

/// <summary>
/// The shared metadata every item definition carries — the C# successor to QC's <c>CLASS(GameItem)</c>
/// attributes (m_id / m_name / m_icon / m_color / m_waypoint / m_glow / m_respawnsound / pickup sound).
/// Concrete <see cref="Pickup"/>s expose one via <see cref="Pickup.ItemDef"/>; weapons and powerups can
/// carry one too. This is data only — pickup behavior lives in <see cref="Pickup.GiveTo"/> /
/// <see cref="ItemPickupRules"/>.
/// </summary>
public class GameItemDef
{
    /// <summary>QC m_itemid — the IT_* bit identifying this item (IT_RESOURCE for health/armor/ammo).</summary>
    public ItemFlag ItemId;

    /// <summary>QC spawnflags — ITEM_FLAG_* (normal / mutator-blocked / resource).</summary>
    public GameItemSpawnFlag SpawnFlags = GameItemSpawnFlag.Normal;

    /// <summary>QC m_name — localized display name (e.g. "Strength").</summary>
    public string Name = "";

    /// <summary>QC m_icon — HUD icon string (e.g. "strength").</summary>
    public string Icon = "";

    /// <summary>QC m_color — HUD tint ('1 1 1' default).</summary>
    public System.Numerics.Vector3 Color = System.Numerics.Vector3.One;

    /// <summary>QC m_waypoint — waypoint sprite text (empty = none).</summary>
    public string Waypoint = "";

    /// <summary>QC m_waypointblink — waypoint blink rate (1 default, 2 for powerups).</summary>
    public int WaypointBlink = 1;

    /// <summary>QC m_glow — whether the world model glows (powerups).</summary>
    public bool Glow;

    /// <summary>QC m_model — world model name.</summary>
    public string? Model;

    /// <summary>QC m_sound / item_pickupsound — the pickup sound (SND_*) name (e.g. "ITEMPICKUP", "POWERUP").</summary>
    public string PickupSound = "ITEMPICKUP";

    /// <summary>QC m_respawnsound — the respawn sound name (SND_ITEMRESPAWN default).</summary>
    public string RespawnSound = "ITEMRESPAWN";

    /// <summary>
    /// QC m_pickupanyway — when &gt; 0, the item is taken even if the player is already at the cap (and
    /// re-given on every touch within the spawn-shield window). Resources honour this; weapons honour it
    /// for re-granting an owned weapon.
    /// </summary>
    public int PickupAnyway;

    /// <summary>True if this definition is a weapon pickup (QC instanceOfWeaponPickup).</summary>
    public bool IsWeaponPickup;

    /// <summary>True if this definition is a powerup (QC instanceOfPowerup).</summary>
    public bool IsPowerup;

    /// <summary>
    /// QC <c>m_botvalue</c> — base bot desirability for this item (used by the bot goal-rater via
    /// <c>bot_pickupbasevalue</c>). 0 = not specially valued (default). Powerups = 11000; Jetpack/FuelRegen
    /// = 3000 (they override the Powerup base); weapons/health/armor/ammo carry their own values (set on their
    /// own defs). See <c>powerups.qh</c> CLASS(Powerup) m_botvalue ATTRIB and the per-item overrides.
    /// </summary>
    public int BotValue;

    /// <summary>Whether the definition is allowed in the current ruleset (QC Item_IsDefinitionAllowed): not mutator-blocked.</summary>
    public bool IsAllowed => (SpawnFlags & GameItemSpawnFlag.MutatorBlocked) == 0;
}
