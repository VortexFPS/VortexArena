using System;
using System.Collections.Generic;
using System.Linq;

namespace VortexArena.Common.Menu;

/// <summary>
/// Visible gameplay entries from the Xonotic Guide at 863cd3e8456e18f577f7fe5fef857ff55ae62cc0,
/// in menu order and expressed in native registry IDs. The free-text pages follow their declaration order.
/// </summary>
public static class GuideCatalogOrder
{
    private static readonly Dictionary<string, string[]> PinnedEntries = new(StringComparer.Ordinal)
    {
        ["Gametypes"] =
        [
            "as", "ca", "ctf", "cts", "dm", "dom", "duel", "freezetag", "inv", "ka", "kh", "lms",
            "mayhem", "nb", "ons", "rc", "surv", "tdm", "tka", "tmayhem",
        ],
        ["Weapons"] =
        [
            "blaster", "shotgun", "machinegun", "mortar", "minelayer", "electro", "crylink", "vortex",
            "hagar", "devastator", "porto", "vaporizer", "hook", "hlac", "tuba", "rifle", "fireball",
            "seeker", "arc", "okmachinegun", "oknex", "okshotgun", "okhmg", "okrpc",
        ],
        ["Items"] =
        [
            "bullets", "cells", "fuel", "rockets", "shells", "vaporizer_cells",
            "health_big", "health_medium", "health_mega", "health_small",
            "armor_big", "armor_medium", "armor_mega", "armor_small",
        ],
        ["Powerups"] = ["extralife", "fuel_regen", "invisibility", "jetpack", "invincible", "speed", "strength"],
        ["Buffs"] =
        [
            "buff_ammo", "buff_bash", "buff_disability", "buff_flight", "buff_inferno", "buff_jump",
            "buff_luck", "buff_magnet", "buff_medic", "buff_resistance", "buff_swapper", "buff_vampire",
            "buff_vengeance",
        ],
        ["Nades"] = ["normal", "napalm", "ice", "translocate", "spawn", "heal", "pokenade", "entrap", "veil", "ammo", "darkness"],
        ["Monsters"] = ["golem", "mage", "spider", "wyvern", "zombie"],
        ["Vehicles"] = ["bumblebee", "racer", "raptor", "spiderbot"],
        ["Turrets"] =
        [
            "ewheel", "flac", "fusreac", "hellion", "hk", "machinegun", "mlrs", "phaser", "plasma",
            "plasma_dual", "tesla", "walker",
        ],
        ["Mutators"] =
        [
            "nades", "dodging", "rocketflying", "grappling_hook", "instagib", "bloodloss", "cloaked",
            "offhand_blaster", "new_toys", "nix", "pinata", "touchexplode", "vampire", "walljump",
        ],
    };

    public static IEnumerable<T> Order<T>(string topic, IEnumerable<T> entries, Func<T, string> entryId)
    {
        if (!PinnedEntries.TryGetValue(topic, out var pinned))
            return entries;

        return entries.OrderBy(entry =>
        {
            int index = Array.IndexOf(pinned, entryId(entry));
            // OrderBy is stable, so native-only entries keep their relative order at the end.
            return index >= 0 ? index : pinned.Length;
        });
    }
}
