using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The client-impulse → weapon-selection router — the C# successor to the weapon half of QuakeC's
/// <c>server/impulse.qc</c> (the <c>weapon_group_handle</c> / <c>weapon_priority_handle</c> /
/// <c>weapon_byid_handle</c> handlers and the <c>weapon_next/prev_*</c>, <c>weapon_last</c>, <c>weapon_best</c>,
/// <c>weapon_drop</c>, <c>weapon_reload</c> impulses). The server feeds it a client's impulse number (the
/// <c>impulse N</c> console command) and it drives the <see cref="Inventory"/> selection API
/// (server/weapons/selection.qc), exactly as <c>ImpulseCommands</c> dispatches through the IMPULSES registry.
///
/// <para>This is a NEW selection/reload helper alongside <see cref="WeaponFireDriver"/> (per the T6 task scope);
/// the host (Commands.cs) routes the raw impulse number here via <see cref="Handle"/>.</para>
///
/// <para><b>Impulse numbers</b> are the literal Xonotic values from <c>common/impulses/all.qh</c> (kept in sync
/// with xonotic-client.cfg's weapon binds): group 1-9/14, priority prev/best/next 200-229, by-id 230-253, and
/// the next/prev/last/best/drop/reload singletons 10-20.</para>
/// </summary>
public static class WeaponImpulses
{
    // ---- impulse numbers (common/impulses/all.qh) ----
    private const int GroupFirst = 1;   // weapon_group_1..9 = 1..9
    private const int GroupLast = 9;
    private const int Group0 = 14;      // weapon_group_0 = 14

    private const int PriorityPrevFirst = 200; // weapon_priority_<0..9>_prev = 200..209
    private const int PriorityBestFirst = 210; // weapon_priority_<0..9>_best = 210..219
    private const int PriorityNextFirst = 220; // weapon_priority_<0..9>_next = 220..229

    private const int ByIdFirst = 230;  // weapon_byid_<0..23> = 230..253
    private const int ByIdLast = 253;

    private const int NextById = 10;
    private const int Last = 11;
    private const int PrevById = 12;
    private const int Best = 13;
    private const int NextByPriority = 15;
    private const int PrevByPriority = 16;
    private const int Drop = 17;
    private const int NextByGroup = 18;
    private const int PrevByGroup = 19;
    private const int Reload = 20;
    private const int Use = 21;       // IMPULSE(use) = 21 (all.qh:130) -> PlayerUseKey

    /// <summary>
    /// Route a raw client impulse (QC <c>ImpulseCommands</c> → the IMPULSES registry handler) onto the weapon
    /// selection/reload API. Returns true if the impulse was a weapon impulse and was handled (so the host can
    /// stop dispatch), false to let the host try its other impulse handlers (waypoints/cheats/etc.).
    ///
    /// <para>Mirrors the QC guards: a dead player has weapon-switch impulses *latched* (re-applied on respawn) by
    /// QC's <c>weapon_*_handle</c> via <c>this.impulse = imp</c>; this port simply ignores them while dead (the
    /// re-latch is a networking nicety not modeled here). Reload/drop are no-ops while dead.</para>
    /// </summary>
    public static bool Handle(Entity actor, int imp)
    {
        if (imp <= 0) return false;

        bool dead = IsDead(actor);

        // ---- weapon group (legacy shared-impulse cycling, 1-9 + 0) ----
        if (imp >= GroupFirst && imp <= GroupLast)
        {
            if (!dead) Inventory.NextWeaponOnImpulse(actor, imp);
            return true;
        }
        if (imp == Group0)
        {
            if (!dead) Inventory.NextWeaponOnImpulse(actor, 0);
            return true;
        }

        // ---- custom-priority cycling (prev/best/next, groups 0-9) ----
        // QC weapon_priority_handle (impulse.qc:89-104) cycles the PER-GROUP list cvar_cl_weaponpriorities[number]
        // (= cl_weaponpriority0..9), NOT the single main cl_weaponpriority. The group number is the impulse offset
        // within each block (prev 200-209 / best 210-219 / next 220-229).
        if (imp >= PriorityPrevFirst && imp <= PriorityPrevFirst + 9)
        {
            if (!dead) PriorityHandle(actor, dir: -1, number: imp - PriorityPrevFirst);
            return true;
        }
        if (imp >= PriorityBestFirst && imp <= PriorityBestFirst + 9)
        {
            if (!dead) PriorityHandle(actor, dir: 0, number: imp - PriorityBestFirst); // "best" within the priority group
            return true;
        }
        if (imp >= PriorityNextFirst && imp <= PriorityNextFirst + 9)
        {
            if (!dead) PriorityHandle(actor, dir: +1, number: imp - PriorityNextFirst);
            return true;
        }

        // ---- direct weapon by unique id (230..253) ----
        if (imp >= ByIdFirst && imp <= ByIdLast)
        {
            if (!dead) ByIdHandle(actor, imp - ByIdFirst);
            return true;
        }

        switch (imp)
        {
            case NextById:       if (!dead) Inventory.NextWeapon(actor, 0); return true;
            case PrevById:       if (!dead) Inventory.PreviousWeapon(actor, 0); return true;
            case NextByGroup:    if (!dead) Inventory.NextWeapon(actor, 1); return true;
            case PrevByGroup:    if (!dead) Inventory.PreviousWeapon(actor, 1); return true;
            case NextByPriority: if (!dead) Inventory.NextWeapon(actor, 2); return true;
            case PrevByPriority: if (!dead) Inventory.PreviousWeapon(actor, 2); return true;
            case Last:           if (!dead) Inventory.LastWeapon(actor); return true;
            case Best:
                if (!dead && Inventory.GetBestWeapon(actor) is { } b)
                    Inventory.SwitchWeaponWithComplain(actor, b);
                return true;
            case Reload:
                if (!dead) ReloadHandle(actor);
                return true;
            case Drop:
                // QC IMPULSE(weapon_drop) (server/impulse.qc:334-351): per slot, throw the current weapon.
                // (The vehicle gate is N/A on this path — no vehicle occupancy field reaches the impulse router.)
                if (!dead) DropHandle(actor);
                return true;
            case Use:
                // QC IMPULSE(use) (server/impulse.qc:409 -> PlayerUseKey, client.qc:2620): the +use key routed
                // through the impulse path (separate from the +use BUTTON rising edge). PlayerUseKey is the SINGLE
                // entry that drives the voluntary use-key actions: vehicle enter/exit AND, via its trailing
                // MUTATOR_CALLHOOK(PlayerUseKey), the CTF flag throw/pass/request-pass (ctf PlayerUseKey hook), the
                // Keepaway / Team Keepaway / Nexball / KeyHunt voluntary ball/key drop, and objective/door/button use.
                // VehicleBoarding.UseKey is the port's PlayerUseKey entry; it gates IS_DEAD / game_stopped itself, so
                // route unconditionally (do NOT pre-filter on `dead` here — QC routes the impulse to PlayerUseKey
                // even when dead and lets PlayerUseKey's own IS_PLAYER/IS_DEAD guards decide).
                UseHandle(actor);
                return true;
        }

        return false; // not a weapon impulse
    }

    /// <summary>
    /// QC <c>weapon_priority_handle</c> (impulse.qc:89-104): cycle the player's PER-GROUP custom priority list
    /// <c>cvar_cl_weaponpriorities[number]</c> (= <c>cl_weaponpriority0..9</c>, the explosives/energy/hitscan/…
    /// category lists) in <paramref name="dir"/> (prev=-1 / best=0 / next=+1). Routes through
    /// <see cref="Inventory.CycleWeaponGroup"/> so the group <paramref name="number"/> selects the right category
    /// list rather than always cycling the single main <c>cl_weaponpriority</c>.
    /// </summary>
    private static void PriorityHandle(Entity actor, int dir, int number)
        => Inventory.CycleWeaponGroup(actor, number, dir);

    /// <summary>
    /// QC <c>weapon_byid_handle</c> (server/impulse.qc:150): switch directly to the weapon whose
    /// <c>m_unique_impulse</c> is <c>WEP_IMPULSE_BEGIN + idx</c> — i.e. <c>Weapon_from_impulse(230 + idx)</c>
    /// (all.qh:150). That impulse is allocated in QC weapon DEFINITION order (the 19 hardcoded-impulse core
    /// weapons first, then the strcmp-sorted tail), NOT in the port's alphabetical
    /// <see cref="Registry{T}.Sort"/> id order — so <c>weapon_byid_0</c> is blaster, not the alphabetically-first
    /// weapon (arc). <see cref="WeaponOrder.WeaponByIdIndex"/> reproduces that QC unique-impulse order
    /// (weapon-local, leaving the global registry sort untouched). Uses the try-others fallback (QC
    /// <c>W_SwitchWeapon_TryOthers</c>).
    /// </summary>
    private static void ByIdHandle(Entity actor, int idx)
    {
        Weapon? w = WeaponOrder.WeaponByIdIndex(idx);
        if (w is null) return; // out of range / impulse-unreachable (QC Weapon_from_impulse → WEP_Null → no-op)
        // QC W_SwitchWeapon_TryOthers: switch; if not owned and cl_weapon_switch_fallback_to_impulse, fall back to
        // cycling that weapon's impulse group. Base registers cl_weapon_switch_fallback_to_impulse as 1 (main.qc:101),
        // so the fallback fires by default; this reads the live cvar (!=0f) so it tracks whatever the client set.
        if (!Inventory.SwitchWeaponWithComplain(actor, w)
            && Api.Services is not null && Api.Cvars.GetFloat("cl_weapon_switch_fallback_to_impulse") != 0f)
        {
            Inventory.NextWeaponOnImpulse(actor, w.Impulse);
        }
    }

    /// <summary>
    /// QC <c>IMPULSE(weapon_drop)</c> (server/impulse.qc:334-351): throw the current weapon forward.
    /// Velocity = <c>W_CalculateProjectileVelocity(this, this.velocity, v_forward*750, false)</c>
    /// (tracing.qc:174-183) — with stock <c>g_projectiles_newton_style 0</c> this is ABSOLUTE
    /// <c>v_forward * 750 * W_WeaponSpeedFactor</c>, no player-velocity inheritance. The dual-wield
    /// <c>dv = v_right * -movedir.y</c> sideways offset collapses to <c>'0 0 0'</c> (the port drives one slot).
    /// </summary>
    private static void DropHandle(Entity actor)
    {
        XonoticGodot.Common.Math.QMath.AngleVectors(actor.Angles, out System.Numerics.Vector3 forward, out _, out _);
        // QC W_WeaponSpeedFactor: g_weaponspeedfactor (default 1) — the only live term of
        // W_CalculateProjectileVelocity with newton_style 0.
        float factor = 1f;
        if (Api.Services is not null)
        {
            float f = Api.Cvars.GetFloat("g_weaponspeedfactor");
            if (f > 0f) factor = f;
        }
        WeaponThrowing.ThrowWeapon(actor, new WeaponSlot(0), forward * (750f * factor),
            System.Numerics.Vector3.Zero, doreduce: true);
    }

    /// <summary>
    /// QC <c>IMPULSE(use)</c> → <c>PlayerUseKey</c> (server/impulse.qc:409, client.qc:2620). Routes the voluntary
    /// use-key onto the port's <see cref="VehicleBoarding.UseKey"/> (the port's <c>PlayerUseKey</c> entry). That
    /// method handles the vehicle enter/exit half and fires the trailing <c>PlayerUseKey</c> mutator hook
    /// (<see cref="MutatorHooks.PlayerUseKey"/>) on which the Keepaway/Team Keepaway/Nexball/KeyHunt voluntary
    /// ball/key drop and objective/door use depend — KeyHunt's voluntary key-drop (kh_Key_DropOne) subscribes that
    /// hook live. (The CTF flag throw/pass-request handler is wired through the same hook once CTF subscribes it.)
    /// </summary>
    private static void UseHandle(Entity actor) => VehicleBoarding.UseKey(actor);

    /// <summary>
    /// QC <c>weapon_reload</c> impulse: reload the weapon(s) in the active slot(s). weaponLocked gates it (a
    /// frozen/blocked player can't reload); the reload itself runs the weapon's <c>wr_reload</c> → W_Reload.
    /// <para>For a non-<see cref="WeaponFlags.Reloadable"/> weapon QC's <c>wr_reload</c> is repurposed rather than a
    /// no-op: the Tuba's <c>wr_reload</c> (tuba.qc:360) cycles the instrument (Tuba → Accordion → Klein Bottle).
    /// The base <see cref="Weapon.WrReload"/> early-outs on the missing Reloadable flag, so route a non-Reloadable
    /// weapon that exposes an instrument-cycle (<see cref="Tuba.Reload"/>) to it directly — that handler exists with
    /// zero callers otherwise.</para>
    /// </summary>
    private static void ReloadHandle(Entity actor)
    {
        // QC IMPULSE(weapon_reload) (server/impulse.qc:357): `if (weaponLocked(this)) return;` — a locked weapon
        // refuses to reload. The reachable, gameplay-critical half of QC weaponLocked is the freeze (PHYS_FROZEN):
        // the gametype freeze stat (Entity.FrozenStat, e.g. Freeze Tag) OR the STATUSEFFECT_Frozen status effect.
        // A frozen player must not be able to reload (mirrors WeaponFireDriver.WeaponLocked).
        if (WeaponLocked(actor)) return;

        Weapon? w = Inventory.CurrentWeapon(actor);
        if (w is null) return;
        var slot = new WeaponSlot(0);
        // QC: non-Reloadable weapons whose wr_reload is repurposed (Tuba instrument cycle). The Reloadable early-out
        // in W_Reload would otherwise swallow this, so dispatch the instrument cycle before the generic reload.
        if ((w.SpawnFlags & WeaponFlags.Reloadable) == 0 && w is Tuba tuba)
        {
            tuba.Reload(actor, slot);
            return;
        }
        w.WrReload(actor, slot);
    }

    private static bool IsDead(Entity e) => e.DeadState != DeadFlag.No;

    /// <summary>
    /// QC <c>weaponLocked</c> (weaponsystem.qc), the reachable freeze half: the gametype freeze stat
    /// (<see cref="Entity.FrozenStat"/>, e.g. Freeze Tag) OR the <c>STATUSEFFECT_Frozen</c> status effect
    /// (QC <c>PHYS_FROZEN</c>). Mirrors <c>WeaponFireDriver.WeaponLocked</c> (which is file-private there). The
    /// game-start/game-stopped/timeout halves of QC weaponLocked are already gated by the dispatcher upstream;
    /// <c>player_blocked</c> and the <c>LockWeapon</c> mutator hook aren't modeled in the port yet.
    /// </summary>
    private static bool WeaponLocked(Entity player)
    {
        if (player.FrozenStat != 0)
            return true;
        StatusEffectDef? frozen = StatusEffectsCatalog.Frozen;
        return frozen is not null && StatusEffectsCatalog.Has(player, frozen);
    }
}
