// Port of qcsrc/common/vehicles/sv_vehicles.qc (vehicles_enter / vehicles_exit / vehicle_impulse) +
//          qcsrc/server/client.qc (PlayerUseKey).
//
// The RUNTIME SEAM that makes the already-ported (and otherwise orphaned) Racer/Raptor/Spiderbot/Bumblebee
// boardable, drivable, and mode-switchable. The deep per-vehicle behaviour (the *_frame controllers, the
// weapons, death/respawn) is all in the descriptors (Racer.cs/Raptor.cs/Spiderbot.cs/Bumblebee.cs) and the
// shared core is in VehicleCommon.cs; what was missing was the plumbing that QC kept OUTSIDE the vehicle
// methods:
//   * vehicles_enter() (sv_vehicles.qc ~931)  — the GUARDS + the MULTISLOT gunner branch + the steal branch
//                                                + RemoveGrapplingHooks + MUTATOR_CALLHOOK(VehicleEnter),
//                                                wrapped around the descriptor's vr_enter (Enter()).
//   * PlayerUseKey() (client.qc ~2620)         — the +use key: seated -> exit, else the radius-250 search +
//                                                vehicles_enter.
//   * vehicles_exit() (sv_vehicles.qc ~775)    — the exit dispatcher (descriptor vr_exit + the link drop +
//                                                MUTATOR_CALLHOOK(VehicleExit)); for a MULTISLOT gunner the
//                                                slot's own exit path runs instead.
//   * vehicle_impulse() (sv_vehicles.qc ~912)  — the per-vehicle vehicles_impulse mode-switch/cycle that runs
//                                                BEFORE the weapon impulses for a seated player.
//
// It also carries the small ToInput() shim that materialises the boxed IMovementInput the server hands back
// each tick into the concrete MovementInput struct the descriptors read off Entity.VehInput.
//
// NAMESPACE NOTE: flat `XonoticGodot.Common.Gameplay` (NOT a nested .Vehicles) — same reason VehicleCommon.cs
// documents: a nested namespace would collide with the `Vehicles` catalog type in EntityClasses.cs.
//
// SCOPE (documented partials, faithful to the SHIPPED default config g_vehicles_enter=1 / radius 250):
//   * Touch-mode board (g_vehicles_enter 0) is NOT reachable — PlayerPhysics does not dual-dispatch .Touch
//     onto solids a player hits, so the QC vehicles_touch -> vehicles_enter path can't fire. The default is
//     use-key, so UseKey() below is the parity path; touch-mode is a flagged partial.
//   * The controller/targetname/ACTIVE_NOT/delayspawn init scheduling, the `g_vehicles_steal` enemy-board
//     gameplay (shield zero + flags backup + intruder waypoint), and vehicles_setreturn for a LIVING abandoned
//     vehicle are NOT ported here (cross-boundary / out of scope — see notes at each site). The
//     destroyed-vehicle respawn IS handled (descriptor Death()). RemoveGrapplingHooks(pl) IS now wired.
//   * Client-side seated prediction (disableclientprediction=1) is out of scope — the server is authoritative
//     and fully testable headlessly.

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The server-authoritative vehicle boarding / exit / impulse seam — the C# successor to the QC functions that
/// lived OUTSIDE the per-vehicle methods (<c>vehicles_enter</c>/<c>vehicles_exit</c>/<c>vehicle_impulse</c> in
/// sv_vehicles.qc and <c>PlayerUseKey</c> in client.qc). The boarding helper wraps each descriptor's
/// <see cref="Vehicle.Enter"/>/<see cref="Vehicle.Exit"/> with the guards, the multi-seat gunner branch, and the
/// mutator hooks QC ran around them.
///
/// NAMING NOTE: the type is <c>VehicleBoarding</c> (the file name) rather than QC's bare verb names so it reads
/// against the other vehicle helpers (<see cref="VehicleCommon"/>, <see cref="VehiclePhysics"/>).
/// </summary>
public static class VehicleBoarding
{
    private static float Time => Api.Services is not null ? Api.Clock.Time : 0f;

    // ===== shipped cvar defaults (sv_vehicles.qh) — read by name with the cfg fallback =====
    private const float DefaultEnter = 1f;          // g_vehicles_enter        (1 = use-key board)
    private const float DefaultEnterRadius = 250f;  // g_vehicles_enter_radius
    // g_vehicles (master) default 1; g_vehicles_allow_bots default 0; g_vehicles_steal default 0.

    // =====================================================================================
    // +use key — port of PlayerUseKey (server/client.qc ~2620).
    // =====================================================================================

    /// <summary>
    /// Port of <c>PlayerUseKey</c> (client.qc): the player pressed +use. If seated, exit the vehicle (QC
    /// <c>vehicles_exit(this.vehicle, VHEF_NORMAL)</c>); else, when use-key boarding is on
    /// (<c>g_vehicles_enter</c>), find the nearest boardable vehicle within <c>g_vehicles_enter_radius</c> and
    /// board it (QC the <c>WarpZone_FindRadius</c> loop -> <c>vehicles_enter</c>). Edge-driven by the caller
    /// (DP fires PlayerUseKey once per +use press, not per tick) — see the +use rising-edge detect in the net
    /// layer / GameWorld seam.
    /// </summary>
    public static void UseKey(Entity player)
    {
        if (player is not Player pl)
            return; // QC: if (!IS_PLAYER(this)) return;

        // QC: a stopped round (intermission/match end) blocks both enter and exit.
        if (VehicleCommon.GameStopped)
            return;

        if (pl.Vehicle is not null)
        {
            // QC: this.vehicle -> vehicles_exit(this.vehicle, VHEF_NORMAL).
            Exit(pl);
            return;
        }

        // QC: else if (autocvar_g_vehicles_enter) { ... radius search ... }
        // QC PlayerUseKey guards: not frozen, not dead, not an independent player. (When boarding is off, or the
        // player is dead, QC skips the radius search but STILL falls through to the trailing PlayerUseKey hook.)
        if (Cvar("g_vehicles_enter", DefaultEnter) != 0f && !pl.IsDead)
        {
            Entity? closest = FindBoardableInRadius(pl);
            if (closest is not null)
            {
                Enter(pl, closest);
                return; // boarded a vehicle (QC vehicles_enter; return) — the use-key hook does NOT fire.
            }
        }

        // QC PlayerUseKey tail (client.qc:2666): "a use key was pressed; call handlers" — fired only when the
        // press neither exited a seated vehicle nor boarded one. This is the live entry for the CTF flag
        // throw/pass-request, the Keepaway/Nexball ball drop, and the KeyHunt voluntary key-drop (kh_Key_DropOne).
        MutatorHooks.FirePlayerUseKey(pl);
    }

    /// <summary>
    /// Port of the <c>PlayerUseKey</c> <c>WarpZone_FindRadius</c> loop: the nearest <c>IS_VEHICLE &amp;&amp;
    /// !IS_DEAD &amp;&amp; takedamage != DAMAGE_NO</c> vehicle within <c>g_vehicles_enter_radius</c> that is
    /// either un-owned OR a same-team MULTISLOT carrier (a gunner seat is free). Returns null when none.
    /// </summary>
    private static Entity? FindBoardableInRadius(Player pl)
    {
        if (Api.Services is null)
            return null;

        float radius = Cvar("g_vehicles_enter_radius", DefaultEnterRadius);
        Entity? closest = null;
        float closestDistSq = 0f;

        foreach (Entity head in Api.Entities.FindInRadius(pl.Origin, radius))
        {
            // QC: if (IS_VEHICLE(head) && !IS_DEAD(head) && head.takedamage != DAMAGE_NO)
            if ((head.VehicleFlags & VehicleFlags.IsVehicle) == 0)
                continue;
            if (VehicleCommon.IsDead(head) || head.TakeDamage == DamageMode.No)
                continue;

            // QC: if (!head.owner || ((head.vehicle_flags & VHF_MULTISLOT) && SAME_TEAM(head.owner, this)))
            bool free = head.Owner is null;
            bool gunnerSeatOpen = (head.VehicleFlags & VehicleFlags.MultiSlot) != 0
                && head.Owner is not null && VehiclePhysics.SameTeam(head.Owner, pl);
            if (!free && !gunnerSeatOpen)
                continue;

            // QC: keep the closest by vlen2 (squared distance).
            float distSq = (pl.Origin - head.Origin).LengthSquared();
            if (closest is null || distSq < closestDistSq)
            {
                closest = head;
                closestDistSq = distSq;
            }
        }

        return closest;
    }

    // =====================================================================================
    // ENTER — port of vehicles_enter (sv_vehicles.qc ~931): the GUARDS + gunner branch + hook.
    // =====================================================================================

    /// <summary>
    /// Port of <c>vehicles_enter(pl, veh)</c>: the guards, the MULTISLOT gunner branch, then the actual board
    /// (the descriptor's <see cref="Vehicle.Enter"/> = QC <c>vr_enter</c>) and <c>MUTATOR_CALLHOOK(VehicleEnter)</c>.
    /// Returns true if <paramref name="player"/> ended up seated (as pilot OR gunner).
    ///
    /// The shared server core of seating (the owner/vehicle link, freezing the player's physics, team/ammo
    /// reset) lives in <see cref="VehicleCommon.EnterVehicle"/>, which the descriptor's Enter calls — so this
    /// method adds only the wrapping QC kept outside vr_enter.
    /// </summary>
    public static bool Enter(Entity player, Entity vehicle)
    {
        if (player is not Player pl)
            return false;

        // QC: if (IS_BOT_CLIENT(pl) && !autocvar_g_vehicles_allow_bots) return;
        if (pl.IsBot && Cvar("g_vehicles_allow_bots", 0f) == 0f)
            return false;

        // QC vehicles_enter guards: a real, alive, un-seated player, past the re-entry delay, and the vehicle
        // past its post-exit phase delay. (FROZEN/StatusEffects_Frozen and the independent-player check are
        // cross-boundary status concerns — modeled by the dead/seated checks here.)
        if (vehicle.VehPhase >= Time
            || pl.VehicleEnterDelay >= Time
            || pl.IsDead
            || pl.Vehicle is not null)
            return false;

        Vehicle? info = vehicle.VehicleDef;
        if (info is null)
            return false;

        // QC: the MULTISLOT gunner branch — if the body already has a pilot and the boarder is same-team, seat
        // them in a gun slot (info.vr_gunner_enter). Only the Bumblebee is MULTISLOT.
        if (Cvar("g_vehicles_enter", DefaultEnter) != 0f
            && (vehicle.VehicleFlags & VehicleFlags.MultiSlot) != 0
            && vehicle.Owner is not null
            && VehiclePhysics.SameTeam(pl, vehicle))
        {
            if (info is Bumblebee bb && bb.GunnerEnter(vehicle, pl))
            {
                // The gunner slot is the player's "vehicle" (set inside GunnerEnter). Fire the enter hook for
                // the gunner board too (QC fires VehicleEnter once after seating; for a gunner the slot is
                // pl.vehicle). NOTE: bot-driver wiring + the gunner CSQCVehicleSetup are cross-boundary.
                var ga = new MutatorHooks.VehicleEnterArgs(pl, vehicle);
                MutatorHooks.VehicleEnter.Call(ref ga);
                return true;
            }
        }

        // QC: if (veh.owner) return; — got here and didn't enter the gunner (full / not multislot) -> bail.
        if (vehicle.Owner is not null)
            return false;

        // QC: the teamplay enemy-board branch (sv_vehicles.qc:960-979). A different-team player CAN board a
        // team vehicle when g_vehicles_steal is on — the STOCK Base default is 1 (vehicles.cfg:5 +
        // sv_vehicles.qh:11), so vehicle-stealing is part of stock teamplay. Stealing zeroes the shield, backs
        // up the vehicle flags, and strips VHF_SHIELDREGEN (so the stolen craft cannot regen its shield back),
        // which vehicles_exit later restores from old_vehicle_flags. With steal off the enemy simply refuses.
        if (vehicle.Team != 0f && VehiclePhysics.DiffTeam(pl, vehicle))
        {
            if (Cvar("g_vehicles_steal", 1f) == 0f)
                return false;

            vehicle.VehicleShield = 0f;
            vehicle.OldVehicleFlags = vehicle.VehicleFlags; // backup so the exit restores SHIELDREGEN
            vehicle.VehicleFlags &= ~VehicleFlags.ShieldRegen;

            // NOTE — cross-boundary (CSQC/notify): the WP_VehicleIntruder radar waypoint
            // (g_vehicles_steal_show_waypoint) and the CENTER_VEHICLE_STEAL / _SELF centerprint notifications
            // are presentation and deferred; the authoritative steal state (shield zero + flag backup/strip) is
            // applied above so a stolen vehicle behaves correctly server-side.
        }

        // QC sv_vehicles.qc:981 vehicles_enter: RemoveGrapplingHooks(pl) — drop the boarder's own in-flight
        // grappling hooks (and restore FLY->WALK) so a held hook can't keep reeling the now-seated player.
        Hook.RemoveGrapplingHooks(pl);

        // Board as PILOT: the descriptor vr_enter runs VehicleCommon.EnterVehicle (the link + physics freeze +
        // team/ammo reset) then applies the per-vehicle movetype/HUD seed.
        info.Enter(vehicle, pl);

        // [sv-antilag.clear.on_spawn] QC clears the antilag ring on vehicle enter (sv_vehicles.qc) so a shot
        // can't rewind the now-seated player toward its pre-board position. Set the explicit one-shot flag the
        // net driver honors on its next record pass (Entity.AntilagNeedsClear). Clear the VEHICLE body's ring
        // too: a now-manned vehicle becomes an antilag target (its body is what shots hit), and its pre-board
        // history (or stale history from a previous pilot) must not rewind a shot back to where it was parked.
        pl.AntilagNeedsClear = true;
        vehicle.AntilagNeedsClear = true;

        // QC: MUTATOR_CALLHOOK(VehicleEnter, pl, veh) — fired after seating. Notify-style (return unused).
        var a = new MutatorHooks.VehicleEnterArgs(pl, vehicle);
        MutatorHooks.VehicleEnter.Call(ref a);

        // NOTE — cross-boundary (other systems/agents): CSQCVehicleSetup viewport/HUD networking, the
        // SVC_SETVIEWPORT/SETVIEWANGLES write, the temp_wepent weapon-slot swap, antilag_clear, the
        // CENTER_VEHICLE_ENTER notification, and the bot-driver gate — all client/net/bot/notify concerns.
        return true;
    }

    // =====================================================================================
    // EXIT — port of vehicles_exit (sv_vehicles.qc ~775): the dispatcher + the hook.
    // =====================================================================================

    /// <summary>
    /// Port of <c>vehicles_exit(this.vehicle, VHEF_NORMAL)</c> driven from <see cref="UseKey"/> while seated.
    /// For a MULTISLOT gunner (the player's "vehicle" is a gun SLOT, VHF_PLAYERSLOT) this runs the slot's exit
    /// (QC <c>vehic.vehicle_exit</c> -> bumblebee_gunner_exit) and returns; for a pilot it runs the descriptor's
    /// <see cref="Vehicle.Exit"/> (which computes the eject vector + calls <see cref="VehicleCommon.ExitVehicle"/>),
    /// then fires <c>MUTATOR_CALLHOOK(VehicleExit)</c>.
    /// </summary>
    public static void Exit(Entity player)
    {
        if (player is not Player pl || pl.Vehicle is null)
            return;

        Entity seat = pl.Vehicle;

        // [sv-antilag.clear.on_spawn] QC clears the antilag ring on vehicle exit too (sv_vehicles.qc:775 +
        // the per-vehicle exit fns) — the player is relocated to the eject point, so old history must not
        // rewind a shot back into the vehicle. One-shot flag honored by the net driver's next record pass.
        // Clear the vehicle body's ring as well so the next pilot doesn't inherit this occupant's history.
        pl.AntilagNeedsClear = true;
        seat.AntilagNeedsClear = true;

        // QC: if (vehic.vehicle_flags & VHF_PLAYERSLOT) { vehic.vehicle_exit(vehic, eject); return; }
        // A gunner's "vehicle" is the gun SLOT — exit it via the body's multi-seat exit, NOT the pilot path.
        if ((seat.VehicleFlags & VehicleFlags.PlayerSlot) != 0)
        {
            Entity? body = seat.VehSlotOwner ?? seat.Owner;
            if (body?.VehicleDef is Bumblebee bb)
            {
                bb.GunnerExit(seat, eject: false);
                // QC fires VehicleExit with the body as slot1 (player may have already been cleared inside
                // gunner_exit, so re-read defensively isn't needed — pass the player we started with).
                var ga = new MutatorHooks.VehicleExitArgs(pl, body);
                MutatorHooks.VehicleExit.Call(ref ga);
            }
            return;
        }

        // Pilot exit: the descriptor computes the eject origin/velocity and calls VehicleCommon.ExitVehicle.
        Entity vehicle = seat;
        vehicle.VehicleDef?.Exit(vehicle, pl);

        // QC: vehic.phase = time + 1 (post-exit re-entry delay on the vehicle) — VehicleCommon.ExitVehicle sets
        // the player's vehicle_enter_delay; mirror the vehicle-side phase gate here so an instant re-board is
        // blocked symmetrically.
        vehicle.VehPhase = Time + 1f;

        // QC: MUTATOR_CALLHOOK(VehicleExit, player, vehic).
        var a = new MutatorHooks.VehicleExitArgs(pl, vehicle);
        MutatorHooks.VehicleExit.Call(ref a);

        // QC vehicles_exit tail (sv_vehicles.qc:862): vehicles_setreturn(vehic). An abandoned LIVING vehicle
        // is scheduled to drift home to its spawn point after respawntime-1 (the alive-path delay inside
        // SetReturn) — without this an idle pilot-less vehicle would sit wherever it was parked forever. The
        // death path schedules its OWN return from the DamageVehicle tail; this covers the pilot-quit case.
        VehicleCommon.SetReturn(vehicle);

        // NOTE — cross-boundary (other systems/agents): SVC_SETVIEWPORT/SETVIEWANGLES + CSQCVehicleSetup,
        // the temp_wepent switch-weapon restore, the Kill_Notification(CPID_VEHICLES) centerprint clear,
        // and last_vehiclecheck. The WP_Vehicle return waypoint itself (vehicles_showwp) is still CSQC-deferred.
    }

    // =====================================================================================
    // IMPULSE — port of vehicle_impulse (sv_vehicles.qc ~912): per-vehicle mode-switch/cycle.
    // =====================================================================================

    /// <summary>
    /// Port of <c>vehicle_impulse(this, imp)</c>: route a seated player's impulse to the per-vehicle
    /// <c>vehicles_impulse</c> (Raptor/Spiderbot mode set + cycle) BEFORE the weapon impulses run. Returns true
    /// if the vehicle consumed it (the caller then skips the weapon-impulse path). Racer/Bumblebee have no
    /// per-vehicle impulse, so only the shared chase-cam toggle (imp 17) applies to them — and that is a client
    /// concern here (handled as a no-op, see below).
    ///
    /// The impulse numbers are the same the port's weapon command table uses (common/impulses/all.qh):
    /// weapon_group 1/2/3 = impulses 1/2/3 (set mode); weapon_next = 10, weapon_last = 11, weapon_prev = 12
    /// (cycle). raptor_impulse: 1-&gt;BOMB, 2-&gt;FLARE, next/prev cycle. spiderbot_impulse: 1-&gt;VOLLY,
    /// 2-&gt;GUIDE, 3-&gt;ARTILLERY, next/prev cycle.
    /// </summary>
    public static bool Impulse(Entity player, int imp)
    {
        if (player is not Player pl)
            return false;

        // QC vehicle_impulse: if (!this.vehicle) return false; if (IS_DEAD(this.vehicle)) return false;
        Entity? vehicle = pl.Vehicle;
        if (vehicle is null)
            return false;

        // For a gunner the "vehicle" is a gun SLOT; the body carries the descriptor + the W2MODE. Resolve the
        // body so a (future) gunner impulse would route — but only the pilot's Raptor/Spiderbot have modes, and
        // a Bumblebee gunner has none, so this resolves to the body harmlessly either way.
        Entity body = vehicle.VehSlotOwner ?? vehicle;
        if (VehicleCommon.IsDead(body))
            return false;

        // QC: f = this.vehicle.vehicles_impulse; if (f && f(this, imp)) return true; — the per-vehicle handler.
        switch (body.VehicleDef)
        {
            case Raptor:
                switch (imp)
                {
                    case 1: Raptor.SetMode(body, RaptorMode.Bomb); return true;
                    case 2: Raptor.SetMode(body, RaptorMode.Flare); return true;
                    case 10: Raptor.CycleMode(body, +1); return true;   // weapon_next  -> ++mode wrap
                    case 11:                                            // weapon_last  -> --mode (QC prev)
                    case 12: Raptor.CycleMode(body, -1); return true;   // weapon_prev  -> --mode wrap
                }
                break;

            case Spiderbot:
                switch (imp)
                {
                    case 1: Spiderbot.SetMode(body, SpiderbotRocketMode.Volley); return true;
                    case 2: Spiderbot.SetMode(body, SpiderbotRocketMode.Guided); return true;
                    case 3: Spiderbot.SetMode(body, SpiderbotRocketMode.Artillery); return true;
                    case 10: Spiderbot.CycleMode(body, +1); return true;
                    case 11:
                    case 12: Spiderbot.CycleMode(body, -1); return true;
                }
                break;
        }

        // QC: the shared case — case IMP_weapon_drop.impulse (= 17): stuffcmd("toggle cl_eventchase_vehicle").
        // The chase cam is a purely CLIENT cvar in this port; there is no server-side state to toggle. Consume
        // it for a SEATED player so it doesn't fall through to weapon_drop (which would try to throw a weapon a
        // seated pilot doesn't hold), matching QC's "return true" — the actual camera toggle is a client concern.
        if (imp == 17)
            return true;

        return false;
    }

    // =====================================================================================
    // ToInput — materialise the boxed IMovementInput into the MovementInput struct VehInput holds.
    // =====================================================================================

    /// <summary>
    /// Copy a (possibly interface-boxed) <see cref="IMovementInput"/> into a concrete <see cref="MovementInput"/>
    /// for stashing on <see cref="Entity.VehInput"/>. The server's <c>InputProvider</c> hands back the boxed
    /// interface (the concrete type may be the net layer's cached <see cref="MovementInput"/> or a host's
    /// <c>ZeroInput</c>); the descriptors read the struct. Copy every field defensively rather than casting
    /// (parity trap §5.1) so the seated vehicle frame sees the pilot's exact view angles, wish-move, frame time
    /// and every button.
    ///
    /// NOTE (parity §5.2): <see cref="IMovementInput.MoveValues"/> arriving here is already wish-VELOCITY scaled
    /// (the net layer ran cl_forwardspeed/sidespeed/upspeed), but every vehicle frame reads only the SIGN of
    /// MoveValues (<c>move.X &gt; 0 ?</c> / <c>!= 0</c> / <c>Sign(...)</c>), so the magnitude is benign — no
    /// rescale needed.
    /// </summary>
    public static MovementInput ToInput(IMovementInput input) => new()
    {
        ViewAngles = input.ViewAngles,
        MoveValues = input.MoveValues,
        FrameTime = input.FrameTime,
        ButtonJump = input.ButtonJump,
        ButtonCrouch = input.ButtonCrouch,
        ButtonUse = input.ButtonUse,
        ButtonAttack1 = input.ButtonAttack1,
        ButtonAttack2 = input.ButtonAttack2,
        ButtonJetpack = input.ButtonJetpack,
        Typing = input.Typing,
        Impulse = input.Impulse,
    };

    /// <summary>Read a float cvar through the facade, falling back to <paramref name="fallback"/> when unset / no services.</summary>
    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null)
            return fallback;
        // Distinguish "absent" (cfg default applies) from "present-and-0" via the raw string — g_vehicles_enter
        // is a boolean where an explicit 0 (touch mode) must NOT fall back to 1.
        string s = Api.Cvars.GetString(name);
        if (string.IsNullOrEmpty(s))
            return fallback;
        return Api.Cvars.GetFloat(name);
    }
}
