// port: client-side render-proxy fields for the wepent (per-weapon/per-player) and objstream
// (turret/objective) networking channels, decoded onto the CLIENT proxy entity by ClientEntityView.
//
// Entity is declared partial (see Framework/Entity.cs and the many EntityXxxState.cs partials), so this NEW
// file only ADDS members to the existing class — no existing file is modified. These fields mirror the
// networked render slice onto the proxy so the Phase-3 consumers (the wepent viewmodel/HUD ring renderer,
// the turret head node, the objective HUD bar) can read them each frame. They are RENDER-ONLY: the client
// fills them from the decoded NetEntityState; the server sim never reads them. They sit alongside the W14a
// csqcmodel render-mirror fields (UpperAction/SwitchWeapon/WepAlpha/GunAlign in EntityGameplayState.cs) for
// cohesion — same proxy, same decode path, same lifetime.
//
// The namespace is XonoticGodot.Common.Framework to match the rest of the partial Entity declarations (the
// partial type's namespace is fixed regardless of the physical Gameplay/ folder location).

namespace XonoticGodot.Common.Framework
{
    public partial class Entity
    {
        // =====================================================================================
        //  [wepent] per-weapon / per-player entity-state render proxy
        // =====================================================================================
        // Decoded from NetEntityState.WepentView (the WepentViewState block) onto the proxy so the wepent
        // ring/viewmodel renderer can draw OTHER players' / the spectatee's weapon charge rings, clip/heat
        // state and viewmodel/beam animation — the local player's own rings still come from the owner-only
        // OwnerWeaponRings block (the owner is delta-excluded from the entity stream). Mirrors the existing
        // W14a SwitchWeapon/WepPhase/WepAlpha/GunAlign net-decoded proxy fields in EntityGameplayState.cs.

        /// <summary>QC wepent view-state block (vortex/oknex charge, clip/heat, viewmodel frame, beam state)
        /// decoded onto this client render proxy from <c>NetEntityState.WepentView</c>. Initialised to
        /// <see cref="XonoticGodot.Net.WepentViewState.None"/> (all-default, <c>IsActive == false</c>); a player
        /// with no charge / no clip never sets the wire bit, so it stays None. Read by the Phase-1 wepent ring
        /// renderer (and the Phase-2 viewmodel/beam consumers); the server sim never reads it.</summary>
        public XonoticGodot.Net.WepentViewState WepentView = XonoticGodot.Net.WepentViewState.None;

        // =====================================================================================
        //  [vehicleview] vehicle HUD / chase-cam render-proxy slice
        // =====================================================================================
        // Decoded from NetEntityState.VehicleView (the VehicleViewState block) onto the proxy so the vehicle
        // HUD (health/shield/energy/ammo bars, weapon2 mode, lock reticle + lock strength) and the vehicle
        // chase camera can drive off a remotely-networked pilot. A remote pilot reads its OWN block off
        // ClientNet.LocalState; a spectator following a pilot reads it off the entity stream onto this proxy.
        // All-zero (VehKind 0 == VehicleViewState.None) means on-foot/observing, so the wire bit stays clear
        // and an ordinary player/projectile/item costs nothing for this group.

        /// <summary>QC vehicle view-state block (HUD health/shield/energy/ammo + reload, vehicle kind,
        /// weapon2 mode, lock strength + flags) decoded onto this client render proxy from
        /// <c>NetEntityState.VehicleView</c>. Initialised to
        /// <see cref="XonoticGodot.Net.VehicleViewState.None"/> (all-default, <c>IsActive == false</c>): a
        /// player on foot / observing never sets the wire bit, so it stays None. A remote pilot reads its own
        /// block off <c>ClientNet.LocalState</c>; a spectator following a pilot reads it off the entity stream
        /// onto this proxy. Read by the vehicle HUD + vehicle chase camera; the server sim never reads it.</summary>
        public XonoticGodot.Net.VehicleViewState VehicleView = XonoticGodot.Net.VehicleViewState.None;

        // =====================================================================================
        //  [objstream] turret / objective render-proxy slice
        // =====================================================================================
        // Decoded from NetEntityState (EntityField.TurretHead / EntityField.ObjState) onto the proxy so the
        // Phase-3 consumers — the turret head node and the objective HUD health bar — can drive the head aim /
        // idle spin and the generator/icon build/health bar. RESERVED this track: only the ClientEntityView
        // decode writes them; the head node + HUD bar read them. Zero/default on non-turret/non-objective
        // entities (NetEntityState.Diff leaves the mask bit clear, so a normal player/projectile/item costs
        // nothing for these groups). Mirrors the W14a UpperAction/SwitchWeapon proxy fields above.

        /// <summary>QC <c>tur_head.angles.x</c> — the turret head's body-relative pitch (degrees), decoded from
        /// <c>NetEntityState.TurHeadPitch</c>. The Phase-3 head node applies it to the head bone/node pose.</summary>
        public float TurHeadPitch;
        /// <summary>QC <c>tur_head.angles.y</c> — the turret head's body-relative yaw (degrees), decoded from
        /// <c>NetEntityState.TurHeadYaw</c>.</summary>
        public float TurHeadYaw;
        /// <summary>QC <c>tur_head.avelocity.y</c> — the head yaw angular velocity (deg/s), decoded from
        /// <c>NetEntityState.TurHeadAVelYaw</c>; the Phase-3 head node integrates it for the idle head spin
        /// (FusionReactor/Tesla <c>tur_head.angles += avelocity*dt</c>).</summary>
        public float TurHeadAVelYaw;
        /// <summary>QC <c>.active</c> — the turret is awake / team-owned (idle vs slewing pose), decoded from
        /// <c>NetEntityState.TurFlags</c> bit0.</summary>
        public bool TurActive;

        /// <summary>Decoded objective health fraction in [0,1] (<c>GtObjHealth/GtObjMaxHealth</c>), recovered from
        /// the <c>NetEntityState.ObjHealthByte</c> hp/255 byte. Defaults to 1 (full) so a freshly-created proxy
        /// reads as intact until its first objstream update. Read by the generator/icon HUD health bar.</summary>
        public float ObjHealthFrac = 1f;
        /// <summary>Decoded objective build/own state, from <c>NetEntityState.ObjState</c>: 0 = neutral/idle,
        /// 1 = building (icon mid-build), 2 = built/captured/owned, 3 = destroyed. Selects the HUD bar mode
        /// (cpicon build-bar vs generator health-bar).</summary>
        public byte ObjState;
    }
}
