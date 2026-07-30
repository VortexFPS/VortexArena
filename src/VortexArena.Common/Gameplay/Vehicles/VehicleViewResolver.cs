using XonoticGodot.Common.Framework;
using XonoticGodot.Net; // VehicleViewState — its DATA struct lives in THIS (.Common) assembly under the
                        // XonoticGodot.Net namespace; only the wire codec (VehicleViewCodec) lives in .Net.

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The single shared resolver of <see cref="VehicleViewState"/> off an AUTHORITATIVE seated <see cref="Player"/> —
/// the one source of truth for the per-pilot vehicle HUD scratch, the vehicle counterpart of
/// <see cref="WepentResolver"/>.
///
/// <para>It reads EXACTLY the same per-pilot fields the host's <c>NetGame.UpdateVehicleHud</c> reads today (the QC
/// <c>0.01 * STAT(VEHICLESTAT_*)</c> mirror: <c>vehicle_health/shield/energy/ammo1/ammo2/reload1/reload2</c>), plus
/// the active vehicle id, the weapon-2 sub-mode, and the missile-lock strength + flags — so the per-player
/// <c>VehicleView</c> entity-state block (feeding a spectatee / third-person view of a remote pilot) draws the
/// reticle, bars and lock strength from the same authoritative truth the local host crosshair uses.</para>
///
/// <para><b>Engine-free (ADR-0008):</b> this lives in <c>.Common</c>, which must not touch Godot. Unlike the host
/// HUD feeder it makes no <c>Coords</c>/world-projection calls — the lock-target WORLD position stays a host-only
/// nicety; only the <see cref="VehicleViewState.LockTargetValid"/> flag + <see cref="VehicleViewState.LockStrength"/>
/// ride the wire, so the resolver is pure data.</para>
/// </summary>
public static class VehicleViewResolver
{
    /// <summary>
    /// Resolve the per-pilot vehicle view-state for an authoritative seated <paramref name="p"/>. Returns
    /// <see cref="VehicleViewState.None"/> (the "on foot / no vehicle HUD" sentinel, VehKind 0) when the player is
    /// not in a vehicle, is dead, or is observing — matching <c>UpdateVehicleHud</c>'s HUD_NORMAL early-out, so an
    /// on-foot or observing player keeps the <c>VehicleView</c> mask bit clear and costs nothing.
    /// </summary>
    /// <param name="p">The authoritative player whose seated-vehicle scratch is read.</param>
    public static VehicleViewState Resolve(Player p)
    {
        Entity? veh = p.Vehicle;
        if (veh is null || p.IsDead || p.IsObserver)
            return VehicleViewState.None; // not piloting → every field stays at its no-data default (HUD_NORMAL)

        VehicleViewState v = VehicleViewState.None;

        // Per-pilot 0..100 percentages mirrored to [0,1] — the SAME fields UpdateVehicleHud reads (QC
        // 0.01 * STAT(VEHICLESTAT_*)). Health/Shield/Energy/Ammo1/Ammo2 + the dual reload bars.
        v.Health  = Clamp01(p.VehicleHealth * 0.01f);
        v.Shield  = Clamp01(p.VehicleShield * 0.01f);
        v.Energy  = Clamp01(p.VehicleEnergy * 0.01f);
        v.Ammo1   = Clamp01(p.VehicleAmmo1 * 0.01f);
        v.Ammo2   = Clamp01(p.VehicleAmmo2 * 0.01f);
        v.Reload1 = Clamp01(p.VehicleReload1 * 0.01f);
        v.Reload2 = Clamp01(p.VehicleReload2 * 0.01f);

        // VehKind — the TE_CSQC_VEHICLESETUP hud id. A bumblebee GUNNER seats in a gun SLOT entity (VehSlotIndex
        // 1/2, owner = body) and gets HUD_BUMBLEBEE_GUN (5) regardless of the body's NetName, so test that FIRST;
        // otherwise the body's descriptor NetName selects the art set (default racer = 1).
        v.VehKind = (veh.VehSlotIndex != 0 && veh.VehSlotOwner is not null)
            ? (byte)5 // bumblebee-gun (gunner)
            : (veh.VehicleDef?.NetName) switch
            {
                "raptor"    => (byte)2,
                "spiderbot" => (byte)3,
                "bumblebee" => (byte)4,
                _           => (byte)1, // racer (default)
            };

        // Per-vehicle weapon-2 sub-mode (raptor RSM_BOMB/RSM_FLARE, spiderbot SBRM_*); 0 for modeless vehicles.
        v.W2Mode = (byte)veh.VehW2Mode;

        // Missile-lock strength [0,1] + the lock flags. Only the VALIDITY of the lock target is networked — its
        // WORLD position is intentionally host-only (the precise aux lock-crosshair projection); the remote path
        // shows the reticle + bars + strength.
        v.LockStrength = Clamp01(veh.VehLockStrength);
        v.LockTargetValid = veh.VehLockTarget is { IsFreed: false };

        // Raptor bomb dropmark prediction ready (wire Flags bit1): raptor, bomb mode, and reload2 fully refilled
        // (QC the bomb dropmark crosshair only tracetoss-predicts while reload2 reads 100 — bombs ready).
        v.DropmarkPredictReady =
            veh.VehicleDef is Raptor
            && veh.VehW2Mode == (int)RaptorMode.Bomb
            && p.VehicleReload2 >= 99.5f;

        return v;
    }

    /// <summary>Clamp a value into [0,1] — the QC <c>0.01 * STAT</c> bar saturation, kept engine-free.</summary>
    private static float Clamp01(float v) => System.Math.Clamp(v, 0f, 1f);
}
