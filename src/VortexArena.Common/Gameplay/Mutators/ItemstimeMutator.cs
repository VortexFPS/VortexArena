// Port of common/mutators/mutator/itemstime/itemstime.qc (the SVQC it_times producer + sync feeds).

using System.Collections.Generic;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Items-time mutator (server backend) — port of the SVQC half of
/// common/mutators/mutator/itemstime/itemstime.qc. Tracks the absolute respawn time of the "timed" pickups
/// (the powerups Strength + Shield, Mega/Big Health, Mega/Big Armor, and an aggregate Superweapons slot) and
/// publishes them so the client HUD <c>ItemsTimePanel</c> can draw the respawn countdowns. Enabled by
/// <c>sv_itemstime</c> (xonotic-server.cfg default <b>1</b>; 2 also sends to alive players — a HUD-visibility
/// tier handled client-side, so the server-side producer is the same).
///
/// QC kept <c>float it_times[REGISTRY_MAX(Items) + 1]</c> (the +1 is the superweapons slot), updated as items
/// are picked up / scheduled to respawn and synced to clients via the <c>itemstime</c> net message. The port
/// computes the equivalent table from the live world items' <see cref="Entity.ScheduledRespawnTime"/> (set by
/// the item-respawn scheduler in ItemPickupRules) and exposes it as <see cref="CurrentTimes"/> keyed by the
/// HUD panel's item names — the host/net layer pushes it into <c>ItemsTimePanel.SetItemTimes</c> each frame.
///
/// Ported faithfully: the timed-item set (Item_ItemsTime_Allow: powerups + mega/big health+armor + the
/// superweapons aggregate), the absolute-respawn-time table, and the NEGATIVE "available now" encoding from
/// <c>Item_ItemsTime_UpdateTime</c> (when one copy of an item is already up, the time is sent as <c>-t</c> so
/// the panel shows "ready" while another copy is still on cooldown).
///
/// KNOWN CROSS-FILE GAPS (not closable in this server-backend file — tracked in
/// planning/parity/registry/mutator-itemstime.yaml):
/// <list type="bullet">
/// <item>The per-player visibility TIERS (QC Item_ItemsTime_SetTimesForAllPlayers: sv_itemstime 1 = spectators
///   /warmup only, 2 = also alive players) require per-client networked itemstime state + the
///   MakePlayerObserver/PlayerSpawn/ClientConnect/reset_map_global sync hooks, none of which exist in the
///   single-view port. The producer here (the it_times table) is identical regardless of tier; the tier is a
///   pure send-gate, so the table is computed unconditionally and the gate is a presentation/net concern.</item>
/// <item>There is no <c>itemstime</c> CSQC net message (IT_Write / NET_HANDLE) nor a <c>STAT(ITEMSTIME)</c>
///   client stat; the host feeds <see cref="CurrentTimes"/> straight into <c>ItemsTimePanel</c> on the listen
///   server, so a pure remote client receives nothing. The panel's spectator/warmup/STAT(ITEMSTIME) enable
///   gate and the spectator respawn waypoint sprites are likewise unported. All of these live in the Net /
///   HUD / waypoints subsystems, not in this mutator.</item>
/// </list>
/// </summary>
[Mutator]
public sealed class ItemstimeMutator : MutatorBase
{
    public ItemstimeMutator() => NetName = "itemstime";

    // QC: REGISTER_MUTATOR(itemstime, true) — always registered; sv_itemstime gates the producer. Modeling
    // IsEnabled on the cvar is equivalent (the only behavior is publishing the times, which sv_itemstime gates).
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("sv_itemstime") != 0f;

    /// <summary>
    /// The live <c>sv_itemstime</c> tier (QC <c>STAT(ITEMSTIME)</c> = <c>autocvar_sv_itemstime</c>; stats.qh:138):
    /// 0 = off, 1 = spectators/warmup only, 2 = also alive players. The client HUD enable gate
    /// (<see cref="XonoticGodot.Game.Hud.ItemsTimePanel.ShouldDraw"/>) reads this — modeled here on the host since
    /// there is no networked STAT(ITEMSTIME) (the single-listen-server feed).
    /// </summary>
    public int Tier => Api.Services is null ? 0 : (int)Api.Cvars.GetFloat("sv_itemstime");

    /// <summary>
    /// The published respawn-time table (QC it_times), keyed by the HUD panel's item names
    /// (health_mega / health_big / armor_mega / armor_big / strength / shield / superweapons). A value &gt; time
    /// is the absolute respawn time; a value &lt; -1 is the "another copy available now" encoding; an item not on
    /// the map is absent (the panel hides it). The host/net layer feeds this to <c>ItemsTimePanel.SetItemTimes</c>.
    /// </summary>
    public IReadOnlyDictionary<string, float> CurrentTimes => _times;
    private readonly Dictionary<string, float> _times = new(System.StringComparer.Ordinal);

    private HookHandler<MutatorHooks.SvStartFrameArgs>? _onStartFrame;

    public override void Hook()
    {
        _onStartFrame ??= OnStartFrame;
        MutatorHooks.SvStartFrame.Add(_onStartFrame);
        Recompute(); // seed immediately on activation
    }

    public override void Unhook()
    {
        if (_onStartFrame is not null) MutatorHooks.SvStartFrame.Remove(_onStartFrame);
        _times.Clear();
    }

    // QC the producer re-syncs on reset_map_global / player spawn / connect; here we recompute each server
    // frame (cheap — a small scan) so CurrentTimes always reflects the live world for the per-frame HUD push.
    private bool OnStartFrame(ref MutatorHooks.SvStartFrameArgs args)
    {
        Recompute();
        return false;
    }

    /// <summary>
    /// QC the classname → HUD-panel-key map for the timed items (Item_ItemsTime_Allow's set). The world item
    /// entities carry these classnames (ItemSpawnFuncs); the superweapons aggregate is computed separately.
    /// </summary>
    private static readonly Dictionary<string, string> TimedItemClasses = new(System.StringComparer.Ordinal)
    {
        ["item_health_mega"] = "health_mega",
        ["item_health_big"]  = "health_big",
        ["item_armor_mega"]  = "armor_mega",
        ["item_armor_large"] = "armor_mega",   // alias
        ["item_armor25"]     = "armor_mega",   // alias
        ["item_armor_big"]   = "armor_big",
        ["item_strength"]    = "strength",
        ["item_shield"]      = "shield",
        ["item_invincible"]  = "shield",       // alias
    };

    /// <summary>
    /// QC <c>Item_ItemsTime_SpectatorOnly</c> (itemstime.qc:58): the subset of timed items that get a
    /// SPECTATOR-only respawn-countdown waypoint sprite — Mega/Big Health and Mega/Big Armor. (SVQC compiles
    /// <c>hud_panel_itemstime_hidebig=false</c>, so Big items are always included.) Powerups + superweapons are
    /// in the Allow set but are NOT SpectatorOnly. Classified by world-item classname, matching the
    /// <see cref="TimedItemClasses"/> producer keys.
    /// </summary>
    public static bool IsSpectatorOnlyItem(string className) =>
        TimedItemClasses.TryGetValue(className, out string? key)
        && (key == "health_mega" || key == "health_big" || key == "armor_mega" || key == "armor_big");

    /// <summary>
    /// QC <c>set_itemstime = Item_ItemsTime_Allow(e.itemdef) || (STAT(WEAPONS, e) &amp; WEPSET_SUPERWEAPONS)</c>
    /// (items.qc:328): the pickups that get a respawn-countdown waypoint sprite (and feed the itemstime panel) —
    /// powerups (<c>instanceOfPowerup</c>), Mega/Big Health + Armor, and superweapon weapon-pickups. Every OTHER
    /// pickup (small/medium health + armor, ammo, ordinary weapons) is NOT allowed, so it takes the plain
    /// no-waypoint respawn path instead of showing an "ITEM" marker. Classified from the world-item entity
    /// (its <see cref="Entity.Pickup"/> def + classname).
    /// </summary>
    public static bool IsItemsTimeAllowed(Entity item)
    {
        if (item.Pickup?.IsPowerup == true) return true;        // instanceOfPowerup (strength/shield + modded)
        if (IsSpectatorOnlyItem(item.ClassName)) return true;   // Mega/Big Health + Armor
        if (item.Pickup?.IsWeaponPickup == true)                // WEPSET_SUPERWEAPONS: a superweapon pickup
        {
            IReadOnlyList<Weapon> weapons = Registry<Weapon>.All;
            for (int i = 0; i < weapons.Count; i++)
                if (weapons[i].IsSuperWeapon && item.ClassName == "weapon_" + weapons[i].NetName)
                    return true;
        }
        return false;
    }

    // Recompute scratch state, persistent across ticks: this runs EVERY server frame (OnStartFrame), so a
    // fresh accumulator dictionary + a capturing local function + per-superweapon "weapon_"+name concats +
    // one FindByClass iterator per class (~10/tick) were a steady ~3 KB/tick of GC churn under load. The
    // accumulator is reused (Clear keeps capacity) and the full classname→key map — timed items + superweapon
    // pickups — is built once, so the steady-state pass allocates nothing.
    private readonly Dictionary<string, (bool avail, float minT, bool hasMin, bool seen)> _acc =
        new(System.StringComparer.Ordinal);
    private Dictionary<string, string>? _classKey; // TimedItemClasses + "weapon_<superweapon>" → key
    private int _classKeyWeaponCount = -1;         // rebuild guard (weapon registry is fixed after boot)

    private Dictionary<string, string> ClassKeyMap()
    {
        IReadOnlyList<Weapon> weapons = Registry<Weapon>.All;
        if (_classKey is not null && _classKeyWeaponCount == weapons.Count)
            return _classKey;

        var map = new Dictionary<string, string>(TimedItemClasses, System.StringComparer.Ordinal);
        // Superweapon weapon-pickups → the single aggregate slot (QC WEPSET_SUPERWEAPONS). The pickup
        // classname is "weapon_<netname>" (ItemSpawnFuncs SPAWNFUNC_WEAPON convention).
        for (int i = 0; i < weapons.Count; i++)
        {
            Weapon w = weapons[i];
            if (w.IsSuperWeapon)
                map["weapon_" + w.NetName] = "superweapons";
        }
        _classKeyWeaponCount = weapons.Count;
        return _classKey = map;
    }

    private static void Note(Dictionary<string, (bool avail, float minT, bool hasMin, bool seen)> acc,
        float now, string key, Entity item)
    {
        if (item.IsFreed || (item.Flags & EntFlags.Item) == 0) return;
        float t = item.ScheduledRespawnTime;
        bool avail = t <= now; // a copy that is up / not on cooldown
        if (!acc.TryGetValue(key, out var a))
            a = (false, 0f, false, false);
        if (avail) a.avail = true;
        else if (!a.hasMin || t < a.minT) { a.minT = t; a.hasMin = true; }
        a.seen = true;
        acc[key] = a;
    }

    /// <summary>
    /// Recompute <see cref="CurrentTimes"/> from the live world items. Mirrors QC's
    /// <c>Item_ItemsTime_SetTime</c> + <c>Item_ItemsTime_UpdateTime</c>: for each timed item key, take the
    /// minimum scheduled respawn time across all copies; if any copy is already up
    /// (<c>scheduledrespawntime &lt;= time</c>) encode the result as NEGATIVE ("another copy available now").
    /// Superweapon weapon-pickups feed the single aggregate "superweapons" slot.
    /// </summary>
    public void Recompute()
    {
        if (Api.Services is null) return;
        if (Api.Cvars.GetFloat("sv_itemstime") == 0f) { _times.Clear(); return; }

        float now = Api.Clock.Time;
        Dictionary<string, string> classKey = ClassKeyMap();
        var acc = _acc;
        acc.Clear();

        // Accumulate per key: (anyAvailable, minScheduled, hasMin, seen) — hasMin distinguishes "no cooldown
        // copy recorded yet" from "min == 0" so the min isn't pinned at 0 by an available copy. ONE pass over
        // the entity table (items never live in the player registry, so the non-client list is the full set);
        // the per-classname FindByClass fallback below serves IEntityService fakes without an All view —
        // accumulation is order-independent (min/OR), so both produce the identical table.
        if (Api.Entities.All is { } all)
        {
            for (int i = 0; i < all.Count; i++)
            {
                Entity e = all[i];
                if (!e.IsFreed && classKey.TryGetValue(e.ClassName, out string? key))
                    Note(acc, now, key, e);
            }
        }
        else
        {
            foreach (KeyValuePair<string, string> kv in classKey)
                foreach (Entity e in Api.Entities.FindByClass(kv.Key))
                    Note(acc, now, kv.Value, e);
        }

        _times.Clear();
        foreach (var kv in acc)
        {
            (bool avail, float minT, bool hasMin, bool seen) = kv.Value;
            if (!seen) continue;
            // QC Item_ItemsTime_UpdateTime: when a copy is available, send the min cooldown time NEGATED ("another
            // copy is up now"); else send the (positive) min cooldown time. If a copy is available and no other is
            // on cooldown, the time is 0 (everything is up).
            float t = hasMin ? minT : 0f;
            _times[kv.Key] = avail ? -t : t;
        }
    }
}
