using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// A 64-bit weapon-ownership bitset (QC WepSet, weapons/all.qh). Each weapon's RegistryId is a bit.
/// Replaces the QC WEPSET_* bitfield macros.
/// </summary>
public struct WepSet : IEquatable<WepSet>
{
    private ulong _bits;

    /// <summary>The raw 64-bit ownership bitmask — the wire form (the owner snapshot block replicates it so a
    /// pure remote client's weapons panel knows which weapons are owned). Bit index == RegistryId.</summary>
    public ulong Bits { readonly get => _bits; set => _bits = value; }

    public bool Has(int weaponId) => weaponId >= 0 && weaponId < 64 && (_bits & (1UL << weaponId)) != 0;
    public void Add(int weaponId) { if (weaponId is >= 0 and < 64) _bits |= 1UL << weaponId; }
    public void Remove(int weaponId) { if (weaponId is >= 0 and < 64) _bits &= ~(1UL << weaponId); }
    public void Clear() => _bits = 0;
    public bool IsEmpty => _bits == 0;
    public int CountSet => System.Numerics.BitOperations.PopCount(_bits);

    public bool Has(Weapon w) => Has(w.RegistryId);
    public void Add(Weapon w) => Add(w.RegistryId);
    public void Remove(Weapon w) => Remove(w.RegistryId);

    public IEnumerable<int> Ids()
    {
        for (int i = 0; i < 64; i++) if (Has(i)) yield return i;
    }

    public IEnumerable<Weapon> Weapons()
    {
        foreach (var id in Ids())
            if (id < Registry<Weapon>.Count) yield return Registry<Weapon>.ById(id);
    }

    public static WepSet FromWeapon(Weapon w) { var s = new WepSet(); s.Add(w); return s; }

    public bool Equals(WepSet other) => _bits == other._bits;
    public override bool Equals(object? o) => o is WepSet w && Equals(w);
    public override int GetHashCode() => _bits.GetHashCode();
    public static WepSet operator |(WepSet a, WepSet b) => new() { _bits = a._bits | b._bits };
    public static WepSet operator &(WepSet a, WepSet b) => new() { _bits = a._bits & b._bits };
}

/// <summary>
/// Weapon inventory operations on an entity (QC W_GiveWeapon / W_SwitchWeapon / W_GetCycleWeapon).
/// The authority for weapon ownership + selection.
/// </summary>
public static class Inventory
{
    public static WepSet GetWeapons(Entity e) => e.OwnedWeaponSet;

    public static bool HasWeapon(Entity e, Weapon w) => e.OwnedWeaponSet.Has(w);

    public static void GiveWeapon(Entity e, Weapon w)
    {
        e.OwnedWeaponSet.Add(w);
        if (e.ActiveWeaponId < 0) SwitchWeapon(e, w);
    }

    public static void RemoveWeapon(Entity e, Weapon w)
    {
        e.OwnedWeaponSet.Remove(w);
        if (e.ActiveWeaponId == w.RegistryId) SwitchToBest(e);
    }

    public static void ClearWeapons(Entity e)
    {
        e.OwnedWeaponSet.Clear();
        e.ActiveWeaponId = -1;
        e.SwitchWeaponId = -1;
    }

    public static Weapon? CurrentWeapon(Entity e) =>
        e.ActiveWeaponId >= 0 && e.ActiveWeaponId < Registry<Weapon>.Count ? Registry<Weapon>.ById(e.ActiveWeaponId) : null;

    /// <summary>
    /// Port of <c>W_SwitchWeapon_Force</c> (server/weapons/selection.qc): unconditionally set the slot's switch
    /// target to <paramref name="w"/>, recording the previously-switching weapon's id as the slot's
    /// <c>cnt</c>/<see cref="WeaponSlotState.PrevWeaponId"/> (so <c>W_LastWeapon</c> can switch back) and updating
    /// <see cref="WeaponSlotState.SelectWeapon"/>. This is the low-level primitive
    /// the fire driver consumes; ammo/ownership are NOT re-checked here (the caller did that). Kept as the public
    /// <c>SwitchWeapon</c> name the existing driver/bot call sites use.
    /// </summary>
    public static void SwitchWeapon(Entity e, Weapon w)
    {
        // QC W_SwitchWeapon_Force: w_ent.cnt = w_ent.m_switchweapon.m_id; record the outgoing switch target as
        // "last weapon", so the W_LastWeapon bind / Blaster-secondary can return to it.
        var st = e.WeaponState(new WeaponSlot(0));
        int outgoing = e.SwitchWeaponId >= 0 ? e.SwitchWeaponId : e.ActiveWeaponId;
        if (outgoing >= 0)
        {
            st.PrevWeaponId = outgoing;
            e.LastWeaponId = outgoing;
        }
        e.SwitchWeaponId = w.RegistryId;
        e.ActiveWeaponId = w.RegistryId;   // immediate mirror; the weapon raise/lower timing is layered on by the driver
        st.SelectWeapon = w.RegistryId;    // QC w_ent.selectweapon = wep.m_id
    }

    /// <summary>Pick the highest-impulse owned weapon (QC W_GetCycleWeapon / weaponorder best). </summary>
    public static Weapon? BestWeapon(Entity e)
    {
        Weapon? best = null;
        foreach (var w in e.OwnedWeaponSet.Weapons())
            if (best is null || w.Impulse > best.Impulse) best = w;
        return best;
    }

    public static void SwitchToBest(Entity e)
    {
        var w = GetBestWeapon(e) ?? BestWeapon(e);
        if (w is not null) SwitchWeapon(e, w);
        else
        {
            e.ActiveWeaponId = -1;
            e.SwitchWeaponId = -1;
            // -1 == "none" (this port has no Null weapon, so id 0 is real — Arc); GetCycleWeapon reads < 0.
            e.WeaponState(new WeaponSlot(0)).SelectWeapon = -1;
        }
    }

    // =====================================================================================================
    //  Weapon SELECTION — faithful port of server/weapons/selection.qc (W_GetCycleWeapon / W_SwitchWeapon /
    //  W_CycleWeapon / W_NextWeapon / W_PreviousWeapon / W_LastWeapon / W_SwitchToOtherWeapon).
    //  Driven by the impulse router (WeaponImpulses) which Commands.cs wires to the client impulse stream.
    // =====================================================================================================

    /// <summary>
    /// QC <c>weaponorder_byid</c> (common/weapons/weapon.qh): the weapons in registry-id order, as the
    /// space-separated id list <c>W_GetCycleWeapon</c> tokenizes. Built once from the registry.
    /// </summary>
    public static string WeaponOrderById
    {
        get
        {
            if (_weaponOrderById is null || _weaponOrderByIdCount != Registry<Weapon>.Count)
            {
                _weaponOrderByIdCount = Registry<Weapon>.Count;
                var sb = new System.Text.StringBuilder();
                // Unlike QC (id 0 == the Null weapon), this port registers no Null entry — every id in [0, Count)
                // is a real weapon, so the order list includes id 0.
                for (int i = 0; i < _weaponOrderByIdCount; i++)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(i);
                }
                _weaponOrderById = sb.ToString();
            }
            return _weaponOrderById;
        }
    }
    private static string? _weaponOrderById;
    private static int _weaponOrderByIdCount = -1;

    /// <summary>
    /// PER-CLIENT priority source (T54, the QC <c>CS_CVAR(this).cvar_cl_weaponpriority</c> field): returns the
    /// player's replicated weapon-priority list in NUMBER (registry-id) form — the post-fixup value the server's
    /// <c>sentcvar</c> handler stored (replicate.qh's <c>W_FixWeaponOrder_ForceComplete…</c> fixup) — or
    /// null/empty for "not replicated" (fall back to the global cvar read below). Installed by the server's
    /// <c>Commands</c> ctor; null by default so the listen-server/local path is unchanged.
    /// </summary>
    public static Func<Entity, string?>? PriorityProvider;

    /// <summary>
    /// QC <c>CS_CVAR(this).cvar_cl_weaponpriority</c> (the player's priority list). Consults the per-client
    /// <see cref="PriorityProvider"/> first (the sentcvar-replicated value, already in id form); otherwise
    /// cl_weaponpriority is read off the server cvar facade (the same value on a listen server / when not yet
    /// replicated). Tokens naming weapons absent from this port (Overkill weapons) are simply skipped by
    /// <see cref="GetCycleWeapon"/> since their NetName doesn't resolve.
    /// </summary>
    public static string WeaponPriority(Entity e)
    {
        string? per = PriorityProvider?.Invoke(e);
        if (!string.IsNullOrEmpty(per))
            return per;
        string s = Api.Services is not null ? Api.Cvars.GetString("cl_weaponpriority") : "";
        return string.IsNullOrEmpty(s) ? WeaponOrderByPriorityDefault : PriorityNamesToIds(s);
    }

    /// <summary>
    /// PER-CLIENT, PER-GROUP priority source (QC <c>CS_CVAR(this).cvar_cl_weaponpriorities[number]</c>, the ten
    /// replicated <c>cl_weaponpriority0..9</c> explosives/energy/hitscan/… category lists): given the player and a
    /// group number 0..9, returns that group's replicated priority list in NUMBER (registry-id) form (the
    /// post-fixup value the server's <c>sentcvar</c> handler stored — see Commands.cs CmdSentCvar's
    /// cl_weaponpriorityN W_FixWeaponOrder_AllowIncomplete branch), or null/empty for "not replicated" (fall back
    /// to the global cvar read in <see cref="WeaponPriorityForGroup"/>). Installed by the server's
    /// <c>Commands</c> ctor alongside <see cref="PriorityProvider"/>; null by default so the listen-server/local
    /// path reads the cvar facade directly.
    /// </summary>
    public static Func<Entity, int, string?>? PriorityProviderForGroup;

    /// <summary>
    /// QC <c>CS_CVAR(this).cvar_cl_weaponpriorities[number]</c> — the player's priority list for impulse-group
    /// <paramref name="number"/> (0..9). Consults the per-client per-group <see cref="PriorityProviderForGroup"/>
    /// first (the sentcvar-replicated value, already in id form); otherwise the corresponding
    /// <c>cl_weaponpriorityN</c> is read off the server cvar facade (name form → ids). Falls back to the main
    /// <see cref="WeaponPriority"/> list when the group cvar is unset/out-of-range, matching an unset category list.
    /// </summary>
    public static string WeaponPriorityForGroup(Entity e, int number)
    {
        if (number is < 0 or > 9)
            return WeaponPriority(e);

        string? per = PriorityProviderForGroup?.Invoke(e, number);
        if (!string.IsNullOrEmpty(per))
            return per;

        if (Api.Services is not null)
        {
            string s = Api.Cvars.GetString("cl_weaponpriority" + number);
            if (!string.IsNullOrEmpty(s))
                return PriorityNamesToIds(s);
        }
        // Unset group list → behave like QC's empty category (nothing to cycle); fall back to the main list so the
        // bind still moves the player to a usable weapon rather than no-op.
        return WeaponPriority(e);
    }

    /// <summary>
    /// PER-CLIENT by-impulse-group order source (QC <c>CS_CVAR(this).weaponorder_byimpulse</c>, the space-separated
    /// weapon-id list ordered by the weapons' impulse/group, used by <c>W_NextWeapon</c>/<c>W_PreviousWeapon</c>
    /// list==1). Returns the player's replicated order in NUMBER (registry-id) form, or null/empty to fall back to
    /// <see cref="WeaponOrderByImpulseDefault"/>. Installed by the server's <c>Commands</c> ctor; null by default.
    /// </summary>
    public static Func<Entity, string?>? WeaponOrderByImpulseProvider;

    /// <summary>
    /// QC <c>CS_CVAR(this).weaponorder_byimpulse</c>: the weapons in impulse/group order. Consults the per-client
    /// <see cref="WeaponOrderByImpulseProvider"/> first, otherwise the registry-derived default.
    /// </summary>
    public static string WeaponOrderByImpulse(Entity e)
    {
        string? per = WeaponOrderByImpulseProvider?.Invoke(e);
        return string.IsNullOrEmpty(per) ? WeaponOrderByImpulseDefault : per;
    }

    /// <summary>
    /// Stock <c>weaponorder_byimpulse</c> default: weapon ids sorted by ascending (Impulse, RegistryId) — i.e. the
    /// order the weapon keys 1..0 lay weapons out — built once from the registry. (QC fills this from
    /// W_FixWeaponOrder over the impulse order; absent a replicated value we approximate with the impulse sort.)
    /// </summary>
    public static string WeaponOrderByImpulseDefault
    {
        get
        {
            if (_weaponOrderByImpulse is null || _weaponOrderByImpulseCount != Registry<Weapon>.Count)
            {
                _weaponOrderByImpulseCount = Registry<Weapon>.Count;
                var ids = new List<int>();
                for (int i = 0; i < _weaponOrderByImpulseCount; i++) ids.Add(i);
                ids.Sort((a, b) =>
                {
                    int ia = Registry<Weapon>.ById(a).Impulse, ib = Registry<Weapon>.ById(b).Impulse;
                    return ia != ib ? ia.CompareTo(ib) : a.CompareTo(b);
                });
                _weaponOrderByImpulse = string.Join(' ', ids);
            }
            return _weaponOrderByImpulse;
        }
    }
    private static string? _weaponOrderByImpulse;
    private static int _weaponOrderByImpulseCount = -1;

    /// <summary>Stock fallback priority by id (registry order, reversed → strongest first) when no cvar is set.</summary>
    public static string WeaponOrderByPriorityDefault
    {
        get
        {
            var ids = new List<int>();
            for (int i = 0; i < Registry<Weapon>.Count; i++) ids.Add(i);
            ids.Reverse();
            return string.Join(' ', ids);
        }
    }

    /// <summary>Convert a space-separated weapon-NetName priority list to the id list GetCycleWeapon expects.</summary>
    private static string PriorityNamesToIds(string names)
    {
        var sb = new System.Text.StringBuilder();
        foreach (string n in names.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries))
        {
            if (Registry<Weapon>.ByName(n) is { } w)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(w.RegistryId);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Port of <c>w_getbestweapon</c> macro (selection.qh): the best owned+usable weapon per the player's
    /// priority list. <c>W_GetCycleWeapon(ent, cl_weaponpriority, dir=0, imp=-1, complain=false, skipmissing=true)</c>.
    /// </summary>
    public static Weapon? GetBestWeapon(Entity e)
    {
        int id = GetCycleWeapon(e, WeaponPriority(e), 0, -1, complain: false, skipMissing: true);
        return id >= 0 && id < Registry<Weapon>.Count ? Registry<Weapon>.ById(id) : null;
    }

    /// <summary>
    /// Port of <c>client_hasweapon</c> (server/weapons/selection.qc): does the actor own <paramref name="w"/>
    /// (and, when <paramref name="andAmmo"/>, have ammo for it)? <paramref name="complain"/> drives the
    /// unavailable feedback (throttled by <see cref="WeaponSlotState.HasWeaponComplainSpam"/>). The "is on the
    /// map" branch and the Mine-Layer "keep if mines placed" exception are honored where modeled.
    /// </summary>
    public static bool ClientHasWeapon(Entity e, Weapon? w, bool andAmmo, bool complain)
    {
        var st = e.WeaponState(new WeaponSlot(0));
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        if (now < st.HasWeaponComplainSpam)
            complain = false;
        if (complain)
            st.HasWeaponComplainSpam = now + 0.2f;

        if (w is null || w.RegistryId < 0)
            return false;

        if (e.OwnedWeaponSet.Has(w))
        {
            if (andAmmo)
            {
                bool f = e.UnlimitedAmmo || (e.Items & ItUnlimitedAmmo) != 0
                    || WeaponAmmo.Check(w, e, secondary: false)
                    || WeaponAmmo.Check(w, e, secondary: true);
                if (!f)
                {
                    // QC selection.qc:91-95: owned but no ammo + complain + real client → the UNAVAILABLE cue.
                    // play2(this, SND(UNAVAILABLE)) — a per-recipient 2D cue (CH_INFO / VOL_BASE / ATTEN_NONE);
                    // "weapons/unavailable". (Send_WeaponComplain text routing is owned by the notification unit.)
                    if (complain && e is Player { IsBot: false })
                        SoundSystem.Play2(e, "UNAVAILABLE");
                    return false;
                }
            }
            return true;
        }

        // Not owned. QC client_hasweapon (selection.qc:106-115): on the complain path, if the weapon EXISTS as a
        // spawn somewhere on the map (weaponsInMap & WepSet), point the player at it with WP_Weapon spawn-markers
        // (Weapon_whereis) instead of the "you don't have it" complaint. The port has no cached weaponsInMap flag,
        // so Weapon_whereis scans the live item set directly (same answer: a marker per matching spawn, or none).
        if (complain && e is Player { IsBot: false })
        {
            // QC selection.qc:109-112: g_showweaponspawns < 3 → just this weapon; == 3 → every weapon sharing the
            // pressed weapon's impulse group (so all alternatives bound to the same key are revealed).
            if (Api.Services is not null && (int)Api.Cvars.GetFloat("g_showweaponspawns") >= 3)
            {
                for (int i = 0; i < Registry<Weapon>.Count; i++)
                {
                    Weapon it = Registry<Weapon>.ById(i);
                    if (it.Impulse == w.Impulse)
                        Weapon_whereis(it, e);
                }
            }
            else
            {
                Weapon_whereis(w, e);
            }

            // QC selection.qc:117: after the where-is markers (or the "modified ownership" complaint), the
            // not-owned complain path always plays the UNAVAILABLE cue. play2(this, SND(UNAVAILABLE)) — a
            // per-recipient 2D cue (CH_INFO / VOL_BASE / ATTEN_NONE), resolving to "weapons/unavailable".
            SoundSystem.Play2(e, "UNAVAILABLE");
        }
        return false;
    }

    /// <summary>
    /// Port of <c>Weapon_whereis</c> (server/weapons/selection.qc:26): when a player presses a weapon key for a
    /// weapon they don't own — and <c>g_showweaponspawns</c> is on — spawn a short-lived <c>WP_Weapon</c> waypoint
    /// sprite over every matching weapon-spawn item, so the player can see where to pick it up. The markers use
    /// <c>RADARICON_NONE</c> (radar icon 0) so they appear only in-world, not on the team radar, and carry the
    /// weapon id (QC <c>wp_extra</c>) for the client's weapon-icon resolution. Lifetime -2 (QC: full alpha for 2s
    /// with no fade ramp, then killed — re-spawned on each press). <c>g_showweaponspawns</c>: 1 = on-key when unowned,
    /// 2 = include dropped loot, 3 = all weapons sharing the impulse (the 3-case is dispatched by the caller).
    /// </summary>
    public static void Weapon_whereis(Weapon weapon, Entity requester)
    {
        if (Api.Services is null || requester is not Player requesterPlayer)
            return;
        // QC: if (!autocvar_g_showweaponspawns || this.spawnflags & WEP_FLAG_HIDDEN) return;
        int showSpawns = (int)Api.Cvars.GetFloat("g_showweaponspawns");
        if (showSpawns == 0 || (weapon.SpawnFlags & WeaponFlags.Hidden) != 0)
            return;

        // QC IL_EACH(g_items, it.weapon == this.m_id && it.model, …): every live spawn of this weapon that
        // currently has a model (i.e. is shown — Item_Show clears the model when hidden/picked-up). The port maps
        // it.weapon == m_id to "the item's weapon set contains this weapon" (weapon pickups seed exactly one).
        System.Collections.Generic.IReadOnlyList<Entity>? all = Api.Entities.All;
        if (all is null)
            return;
        for (int i = 0; i < all.Count; i++)
        {
            Entity it = all[i];
            if (it.IsFreed || it.ItemDefRef is null)
                continue;
            if (it.Pickup is not WeaponPickup || !it.OwnedWeaponSet.Has(weapon))
                continue;
            if (!it.ItemAvailable || string.IsNullOrEmpty(it.Model)) // QC: && it.model (shown only)
                continue;
            // QC: if (ITEM_IS_LOOT(it) && autocvar_g_showweaponspawns < 2) continue;
            if (it.ItemIsLoot && showSpawns < 2)
                continue;

            // QC WaypointSprite_Spawn(WP_Weapon, -2, 0, NULL, it.origin + ('0 0 1'*it.maxs.z)*1.2, cl, 0, NULL,
            //   enemy, 0, RADARICON_NONE); wp.wp_extra = this.m_id.
            // showto = cl → visible only to the requesting player (personal). RADARICON_NONE → radarIcon 0
            // (in-world only). Lifetime -2 in QC means "full alpha for 2s, no fade ramp, then killed" (re-spawned
            // each press); the port matches with a 2s lifetime + the NoFade flag (hard kill at expiry).
            System.Numerics.Vector3 head = it.Origin
                + new System.Numerics.Vector3(0f, 0f, it.Maxs.Z * 1.2f);
            Waypoints.WaypointDef def = Waypoints.WaypointRegistry.Get("Weapon");
            Waypoints.WaypointSprite wp = Waypoints.WaypointSprites.Spawn(
                "Weapon", Waypoints.WaypointSprites.WhereisLifetime, 0f, null, default, head,
                0, def.Color, radarIcon: 0); // RADARICON_NONE
            wp.NoFade = true;                 // QC lifetime -2: full alpha for the lifetime, then hard kill (no fade)
            wp.WpExtra = weapon.RegistryId;   // QC wp.wp_extra = this.m_id (client weapon-icon resolution)
            // QC showto = cl: personal, visible only to the requester.
            wp.VisibleForPlayer = viewer => ReferenceEquals(viewer, requesterPlayer);
        }
    }

    /// <summary>
    /// Port of <c>W_GetCycleWeapon</c> (server/weapons/selection.qc): walk <paramref name="weaponOrder"/> (a
    /// space-separated weapon-id list) and resolve the weapon to switch to for the given direction
    /// (<paramref name="dir"/>: 0 = first usable / "go to this group", +1 = next, -1 = previous) and impulse group
    /// (<paramref name="imp"/>: an impulse-share group, or -1 for "the whole list"). Returns the chosen weapon id,
    /// or 0 if none. <paramref name="skipMissing"/> means only consider owned+usable weapons (ownership cursor
    /// cycling); <paramref name="complain"/> rotates the unavailable complaint across the group.
    /// </summary>
    public static int GetCycleWeapon(Entity e, string weaponOrder, float dir, float imp, bool complain, bool skipMissing)
    {
        var st = e.WeaponState(new WeaponSlot(0));

        // QC sentinel note: QC uses id 0 (the Null weapon) as "none". This port has no Null weapon — id 0 is real
        // — so "none" is -1 throughout (selectweapon / weaponCur / firstValid / prevValid). QC's
        // "selectweapon == 0" no-selection test therefore becomes "selectweapon < 0" here.
        int weaponCur;
        if (skipMissing || st.SelectWeapon < 0)
            weaponCur = e.SwitchWeaponId >= 0 ? e.SwitchWeaponId : -1;
        else
            weaponCur = st.SelectWeapon;

        bool switchToNext = dir == 0;
        bool switchToLast = false;

        int firstValid = -1, prevValid = -1;

        // complain bookkeeping (QC c / wepcomplainindex)
        int c = 0;
        Weapon? wepComplain = null;
        int wepComplainIndex = 0;

        // (QC's "have_other" group-presence test + the custom cl_weaponpriorityN group bitmask are only needed
        // for the impulse-group complain path; for imp == -1 — the cycle/best path that this port drives — the
        // straightforward traversal below matches QC.)

        string[] order = weaponOrder.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
        foreach (string tok in order)
        {
            if (!int.TryParse(tok, out int weaponWant)) continue;
            if (weaponWant < 0 || weaponWant >= Registry<Weapon>.Count) continue;
            Weapon wep = Registry<Weapon>.ById(weaponWant);

            if (imp >= 0 && wep.Impulse != (int)imp)
                continue;

            // skip weapons we don't own that aren't on the map (this port has no weaponsInMap concept yet, so a
            // not-owned weapon is simply skipped — matching QC once the hidden/mutatorblocked/map checks fall out).
            if (!e.OwnedWeaponSet.Has(wep))
            {
                if ((wep.SpawnFlags & WeaponFlags.Hidden) != 0)
                    continue;
                // not in map + (mutatorblocked || have_other) → skip; with no map-weapon tracking, skip unowned.
                continue;
            }

            if (complain)
            {
                if (wepComplain is null || st.WeaponComplainIndex == c)
                {
                    wepComplain = wep;
                    wepComplainIndex = c;
                }
                ++c;
            }

            if (!skipMissing || ClientHasWeapon(e, wep, andAmmo: true, complain: false))
            {
                if (switchToNext)
                    return weaponWant;
                if (firstValid < 0)
                    firstValid = weaponWant;
                if (weaponWant == weaponCur)
                {
                    if (dir >= 0)
                        switchToNext = true;
                    else if (prevValid >= 0)
                        return prevValid;
                    else
                        switchToLast = true;
                }
                prevValid = weaponWant;
            }
        }

        if (firstValid >= 0)
            return switchToLast ? prevValid : firstValid;

        // complain (but only for one weapon on the button that has been pressed)
        if (wepComplain is not null)
        {
            st.WeaponComplainIndex = wepComplainIndex + 1;
            ClientHasWeapon(e, wepComplain, andAmmo: true, complain: true);
        }
        return -1;
    }

    /// <summary>
    /// Port of <c>W_SwitchWeapon</c> (server/weapons/selection.qc): request a switch to <paramref name="w"/>. If
    /// the actor owns it (with ammo), force the switch; otherwise update only the select-cursor and complain.
    /// Returns false if the player does not have the weapon. (The QC "already out → reload" branch is honored when
    /// <c>cl_weapon_switch_reload</c> is set — modeled here via the always-on reload-on-reselect behavior.)
    /// </summary>
    public static bool SwitchWeaponWithComplain(Entity e, Weapon w)
    {
        if (e.SwitchWeaponId != w.RegistryId)
        {
            if (ClientHasWeapon(e, w, andAmmo: true, complain: true))
            {
                SwitchWeapon(e, w);
                return true;
            }
            e.WeaponState(new WeaponSlot(0)).SelectWeapon = w.RegistryId; // update selectweapon anyway
            return false;
        }
        // already switching to this weapon: QC re-presses trigger a reload if cl_weapon_switch_reload is set.
        if (Api.Services is not null && Api.Cvars.GetFloat("cl_weapon_switch_reload") != 0f)
            w.WrReload(e, new WeaponSlot(0));
        return true;
    }

    /// <summary>
    /// Port of <c>W_SwitchToOtherWeapon</c> (server/weapons/selection.qc): switch to the best weapon OTHER than
    /// the one currently held (temporarily mask it out of the owned set so <c>w_getbestweapon</c> can't pick it).
    /// </summary>
    public static void SwitchToOtherWeapon(Entity e)
    {
        Weapon? cur = CurrentWeapon(e);
        Weapon? ww;
        if (cur is not null && e.OwnedWeaponSet.Has(cur))
        {
            e.OwnedWeaponSet.Remove(cur);
            ww = GetBestWeapon(e);
            e.OwnedWeaponSet.Add(cur);
        }
        else
            ww = GetBestWeapon(e);
        if (ww is null) return;
        SwitchWeapon(e, ww);
    }

    /// <summary>Port of <c>W_CycleWeapon</c> (selection.qc): cycle by the custom priority list in <paramref name="dir"/>.</summary>
    public static void CycleWeapon(Entity e, string weaponOrder, float dir)
    {
        int w = GetCycleWeapon(e, weaponOrder, dir, -1, complain: true, skipMissing: true);
        if (w >= 0) SwitchWeaponWithComplain(e, Registry<Weapon>.ById(w));
    }

    /// <summary>
    /// Port of <c>weapon_priority_handle</c> (impulse.qc:89) → <c>W_CycleWeapon(this, cvar_cl_weaponpriorities[number],
    /// dir, …)</c>: cycle the per-GROUP custom priority list (cl_weaponpriority0..9) for impulse group
    /// <paramref name="number"/> (0..9) in <paramref name="dir"/> (prev=-1 / best=0 / next=+1). The impulse router
    /// (WeaponImpulses.PriorityHandle, 200-229) calls this so the group number selects the right category list,
    /// rather than always cycling the single main <c>cl_weaponpriority</c>.
    /// </summary>
    public static void CycleWeaponGroup(Entity e, int number, float dir)
    {
        CycleWeapon(e, WeaponPriorityForGroup(e, number), dir);
    }

    /// <summary>Port of <c>W_NextWeaponOnImpulse</c> (selection.qc): next weapon in the impulse-share group.</summary>
    public static void NextWeaponOnImpulse(Entity e, float imp)
    {
        bool impulseMode0 = Api.Services is null || Api.Cvars.GetFloat("cl_weaponimpulsemode") == 0f;
        int w = GetCycleWeapon(e, WeaponPriority(e), +1, imp, complain: true, skipMissing: impulseMode0);
        if (w >= 0) SwitchWeaponWithComplain(e, Registry<Weapon>.ById(w));
    }

    /// <summary>
    /// Port of <c>W_NextWeapon</c> (selection.qc): go to the next weapon. <paramref name="list"/> 0 = by id,
    /// 1 = by impulse group order, 2 = by priority. (Note QC: "next" steps with dir −1 through the order.)
    /// </summary>
    public static void NextWeapon(Entity e, int list)
    {
        if (list == 0) CycleWeapon(e, WeaponOrderById, -1);
        else if (list == 1) CycleWeapon(e, WeaponOrderByImpulse(e), -1); // QC weaponorder_byimpulse (list 1)
        else if (list == 2) CycleWeapon(e, WeaponPriority(e), -1);
    }

    /// <summary>Port of <c>W_PreviousWeapon</c> (selection.qc): go to the previous weapon (dir +1 through the order).</summary>
    public static void PreviousWeapon(Entity e, int list)
    {
        if (list == 0) CycleWeapon(e, WeaponOrderById, +1);
        else if (list == 1) CycleWeapon(e, WeaponOrderByImpulse(e), +1); // QC weaponorder_byimpulse (list 1)
        else if (list == 2) CycleWeapon(e, WeaponPriority(e), +1);
    }

    /// <summary>
    /// Port of <c>W_LastWeapon</c> (selection.qc): switch back to the previously-used weapon (the slot's
    /// <c>cnt</c>/<see cref="WeaponSlotState.PrevWeaponId"/>) if owned+usable, else switch to the best other weapon.
    /// </summary>
    public static void LastWeapon(Entity e)
    {
        int prev = e.WeaponState(new WeaponSlot(0)).PrevWeaponId;
        Weapon? wep = prev >= 0 && prev < Registry<Weapon>.Count ? Registry<Weapon>.ById(prev) : null;
        if (wep is not null && ClientHasWeapon(e, wep, andAmmo: true, complain: false))
            SwitchWeaponWithComplain(e, wep);
        else
            SwitchToOtherWeapon(e);
    }

    /// <summary>QC <c>IT_UNLIMITED_AMMO</c> = BIT(0) (common/items/item.qh).</summary>
    private const int ItUnlimitedAmmo = 1 << 0;
}
