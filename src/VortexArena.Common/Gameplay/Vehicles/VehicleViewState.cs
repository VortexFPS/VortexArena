using System;

// port: the per-player VEHICLE HUD view-state DATA block — the vehicle counterpart of WepentViewState. Like its
// weapon sibling it physically lives under XonoticGodot.Common (the authoritative producer, VehicleViewResolver,
// also lives in .Common and .Common cannot reference .Net — the dependency runs .Net -> .Common). The WIRE codec
// (VehicleViewCodec.Write/Read, which needs BitWriter/BitReader from .Net) lives separately in the .Net assembly
// so the two never drift. The NAMESPACE is deliberately XonoticGodot.Net (same as WepentViewState) so the .Net
// codec and every render-proxy reference (Entity.VehicleView, NetEntityState.VehicleView) resolve unchanged — a
// type's namespace is independent of the assembly it compiles into.
namespace XonoticGodot.Net;

/// <summary>
/// The per-player vehicle HUD view-state wire block — the vehicle counterpart of <see cref="WepentViewState"/>.
/// It carries the seated-vehicle HUD scalars (health / shield / energy / dual ammo / dual reload bars, the active
/// vehicle id, a per-vehicle weapon-2 sub-mode, and the missile-lock strength + flags) for OTHER players, so a
/// spectatee or third-person view of a vehicle pilot draws its reticle, bars and lock strength correctly. It
/// rides the entity stream under the <c>EntityField.VehicleView</c> mask bit (1 &lt;&lt; 23), appended LAST in
/// both WriteDelta/ReadDelta (after the ObjState block), decoded onto <c>Entity.VehicleView</c>.
///
/// <para><see cref="VehKind"/> mirrors the QC <c>TE_CSQC_VEHICLESETUP</c> hud id: HUD_NORMAL=0 (on foot / not
/// seated → the whole block is <see cref="None"/> and the mask bit stays clear), then 1 racer, 2 raptor,
/// 3 spiderbot, 4 bumblebee, 5 bumblebee-gun (gunner) up to HUD_BUMBLEBEE_GUN. Because an on-foot or observing
/// player resolves to <see cref="None"/> (VehKind 0, every field 0) the bit clears and costs one zero mask, the
/// same "idle entity costs nothing" contract as the wepent block.</para>
///
/// <para>The WIRE codec is <c>VehicleViewCodec.Write</c>/<c>VehicleViewCodec.Read</c> in the .Net assembly (the
/// one place that can see BitWriter/BitReader); that single Write/Read pair is the source of truth for the
/// fixed 11-byte layout, so the server append and the client read can never drift. The lock-target WORLD
/// position is intentionally NOT on this wire — only <see cref="LockTargetValid"/> + <see cref="LockStrength"/>
/// ride it; the precise aux lock-crosshair projection stays a host-only nicety while the remote path shows the
/// reticle, bars and strength. <see cref="Equals(in VehicleViewState)"/> does a boxing-free field-by-field
/// compare so <c>NetEntityState.Diff</c> only sets the mask bit when the block actually changed.</para>
/// </summary>
public struct VehicleViewState
{
    /// <summary>Vehicle health [0,1] (Base <c>0.01 * STAT(VEHICLESTAT_HEALTH)</c>; ×255 → byte on the wire).</summary>
    public float Health;
    /// <summary>Vehicle shield [0,1] (Base VEHICLESTAT_SHIELD; ×255 → byte).</summary>
    public float Shield;
    /// <summary>Vehicle energy [0,1] (Base VEHICLESTAT_ENERGY; ×255 → byte).</summary>
    public float Energy;
    /// <summary>Primary weapon ammo [0,1] (Base VEHICLESTAT_AMMO1; ×255 → byte).</summary>
    public float Ammo1;
    /// <summary>Secondary weapon ammo [0,1] (Base VEHICLESTAT_AMMO2; ×255 → byte).</summary>
    public float Ammo2;
    /// <summary>Primary weapon reload [0,1] (Base VEHICLESTAT_RELOAD1; ×255 → byte).</summary>
    public float Reload1;
    /// <summary>Secondary weapon reload [0,1] (Base VEHICLESTAT_RELOAD2; ×255 → byte).</summary>
    public float Reload2;
    /// <summary>The active vehicle HUD id — mirrors the QC <c>TE_CSQC_VEHICLESETUP</c> hud id (HUD_NORMAL=0 none /
    /// not seated, 1 racer, 2 raptor, 3 spiderbot, 4 bumblebee, 5 bumblebee-gun .. HUD_BUMBLEBEE_GUN). 0 ==
    /// on-foot so the mask bit clears.</summary>
    public byte VehKind;
    /// <summary>Per-vehicle weapon-2 sub-mode (<c>VehW2Mode</c>): raptor RSM_BOMB=1 / RSM_FLARE=2, spiderbot
    /// SBRM_*; 0 for modeless vehicles.</summary>
    public byte W2Mode;
    /// <summary>Missile-lock strength [0,1] (Base <c>VehLockStrength</c>; ×255 → byte).</summary>
    public float LockStrength;
    /// <summary>True when a missile-lock target is currently valid (wire Flags bit0). The target's WORLD position
    /// is deliberately NOT networked — only this flag + <see cref="LockStrength"/> reach the remote view.</summary>
    public bool LockTargetValid;
    /// <summary>True when the raptor bomb dropmark prediction is ready (wire Flags bit1 = raptor reload2 &gt;= 1 in
    /// bomb mode).</summary>
    public bool DropmarkPredictReady;

    /// <summary>The all-zero baseline — VehKind 0 (HUD_NORMAL / on foot), every field 0. A player equal to this
    /// never sets the <c>VehicleView</c> mask bit, so an on-foot or observing player costs one zero mask.</summary>
    public static readonly VehicleViewState None = new()
    {
        Health = 0f,
        Shield = 0f,
        Energy = 0f,
        Ammo1 = 0f,
        Ammo2 = 0f,
        Reload1 = 0f,
        Reload2 = 0f,
        VehKind = 0,
        W2Mode = 0,
        LockStrength = 0f,
        LockTargetValid = false,
        DropmarkPredictReady = false,
    };

    /// <summary>True when the player is seated in a vehicle (<see cref="VehKind"/> != 0) — i.e. there is a vehicle
    /// HUD to draw / network. Drives whether <c>NetEntityState.Diff</c> bothers to set the mask bit.</summary>
    public readonly bool IsActive => VehKind != 0;

    /// <summary>Boxing-free field-by-field equality, used by <c>NetEntityState.Diff</c> to decide whether the
    /// <c>VehicleView</c> mask bit needs setting. NOT an <c>object.Equals</c> override (no allocation / no value
    /// copy of <paramref name="other"/>).</summary>
    public readonly bool Equals(in VehicleViewState other) =>
        Health == other.Health
        && Shield == other.Shield
        && Energy == other.Energy
        && Ammo1 == other.Ammo1
        && Ammo2 == other.Ammo2
        && Reload1 == other.Reload1
        && Reload2 == other.Reload2
        && VehKind == other.VehKind
        && W2Mode == other.W2Mode
        && LockStrength == other.LockStrength
        && LockTargetValid == other.LockTargetValid
        && DropmarkPredictReady == other.DropmarkPredictReady;

    /// <summary>Code a [0,1] value as a byte (×255, rounded, clamped [0,255]) — the uniform bar/strength
    /// quantization. Out-of-range inputs saturate rather than wrap. Shared by <c>VehicleViewCodec</c> (the wire
    /// codec) and kept here so the quantization rule lives with the data definition — the wepent idiom.</summary>
    public static byte EncodeUnit(float value)
    {
        int q = (int)MathF.Round(value * 255f);
        if (q < 0) q = 0;
        else if (q > 255) q = 255;
        return (byte)q;
    }
}
