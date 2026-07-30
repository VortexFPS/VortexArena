using System;

// port: the per-player wepent HUD view-state DATA block. It lives in XonoticGodot.Common (not .Net) because the
// authoritative producer — WepentResolver, also in .Common — constructs and returns it, and .Common cannot
// reference .Net (the dependency runs .Net -> .Common). The WIRE codec (Write/Read, which needs BitWriter/
// BitReader from .Net) lives separately in XonoticGodot.Net/WepentViewCodec.cs as a static helper, so the two
// never drift. The NAMESPACE is deliberately kept as XonoticGodot.Net so every existing reference
// (ServerNet/ClientEntityView/NetEntity/tests use XonoticGodot.Net.WepentViewState) resolves unchanged — a
// type's namespace is independent of the assembly it is compiled into.
namespace XonoticGodot.Net;

/// <summary>
/// The per-player wepent HUD view-state wire block — the single source of truth for the layout, a mirror of
/// <c>OwnerWeaponRings</c>. Where <c>OwnerWeaponRings</c> carries the LOCAL player's own ring
/// scalars on the delta-excluded owner block (the listen host's / dedicated client's own crosshair rings),
/// this block carries OTHER players' copies — the spectatee in spectator mode and any third-person view — so
/// the charge / clip / load / heat / beam state of a watched remote player draws correctly. It rides the entity
/// stream under the <c>EntityField.WepentView</c> mask bit (1 &lt;&lt; 20), appended AFTER the existing Wepent
/// block in both WriteDelta/ReadDelta, decoded onto <c>Entity.WepentView</c>.
///
/// <para>The block's WIRE codec is <c>WepentViewCodec.Write</c>/<c>WepentViewCodec.Read</c> in the .Net
/// assembly (the one place that can see BitWriter/BitReader); that single Write/Read pair is the source of
/// truth for the wire layout, so the server append and the client read can never drift — the two halves
/// drifting (the server wrote the tail, the client never read it) is exactly what desyncs a delta block.
/// <see cref="Equals(in WepentViewState)"/> does a boxing-free field-by-field compare so <c>NetEntityState.Diff</c>
/// only sets the mask bit when the block actually changed; an idle / no-charge player never sets the bit (it
/// compares equal to <see cref="None"/>), matching the "idle entity costs one zero mask" contract. Guarded by
/// <c>WepentViewStateTests</c>.</para>
///
/// <para>Intended divergence (consistent with the existing 13i-coord / x255-alpha precision divergences): Base
/// <c>wepent.qc</c> codes some pools/charges at ×16; the port uses ×255 uniformly for every [0,1]
/// charge/pool/heat value, trading a tad more precision for one byte each. The owner block
/// (<c>OwnerWeaponRings</c>, nine floats) is UNCHANGED — it still serves the local player's own rings.
/// HagarLoadMax / MineLimit caps are NOT on this per-player wire (unlike <c>OwnerWeaponRings</c>); the
/// client clamps against the same <c>CrosshairPanel</c> defaults (4 / 3), so resolved caps stay owner-only.</para>
/// </summary>
public struct WepentViewState
{
    /// <summary>Vortex charge [0,1] (Base <c>vortex_charge</c>; ×255 → byte on the wire).</summary>
    public float VortexCharge;
    /// <summary>Okinawa/over-kill Nex charge [0,1] (Base <c>oknex_charge</c>; ×255 → byte).</summary>
    public float OknexCharge;
    /// <summary>Vortex charge-pool ammo [0,1] (Base <c>vortex_chargepool_ammo</c>; ×255 → byte).</summary>
    public float VortexChargePool;
    /// <summary>Oknex charge-pool ammo [0,1] (Base <c>oknex_chargepool_ammo</c>; ×255 → byte).</summary>
    public float OknexChargePool;
    /// <summary>Current clip load (Base <c>clip_load</c>, signed: -1 = "needs reload" sentinel preserved; short).</summary>
    public int ClipLoad;
    /// <summary>Clip size (Base <c>clip_size</c>; 0 = weapon has no clip → ring hidden; short).</summary>
    public int ClipSize;
    /// <summary>Hagar load (Base <c>hagar_load</c>; byte).</summary>
    public int HagarLoad;
    /// <summary>Live minelayer mine count (Base <c>minelayer_mines</c>; byte).</summary>
    public int MinelayerMines;
    /// <summary>Arc heat [0,1] (Base <c>arc_heat_percent</c>; ×255 → byte).</summary>
    public float ArcHeat;
    /// <summary>Networked viewmodel fire/idle/reload anim frame index (0 = idle/none; byte — Phase-2 consumer).</summary>
    public int ViewmodelFrame;
    /// <summary>Beam-state bitfield: bit0 = arc beam active, bit1 = arc beam burst/secondary, bit2 = electro beam
    /// active; 0 = no beam (byte — Phase-2 consumer).</summary>
    public int BeamState;

    /// <summary>The all-zero baseline (no charge / no clip / no beam). A player equal to this never sets the
    /// <c>WepentView</c> mask bit. Distinct from <c>OwnerWeaponRings.None</c>'s -1 sentinels — here zero
    /// means "no data on this per-player field" and <c>ClipSize == 0</c> hides the clip ring.</summary>
    public static readonly WepentViewState None = new()
    {
        VortexCharge = 0f,
        OknexCharge = 0f,
        VortexChargePool = 0f,
        OknexChargePool = 0f,
        ClipLoad = 0,
        ClipSize = 0,
        HagarLoad = 0,
        MinelayerMines = 0,
        ArcHeat = 0f,
        ViewmodelFrame = 0,
        BeamState = 0,
    };

    /// <summary>True when any field differs from <see cref="None"/> (i.e. there is something to draw / network).
    /// Drives whether <c>NetEntity</c> bothers to set the mask bit.</summary>
    public readonly bool IsActive =>
        VortexCharge > 0f
        || OknexCharge > 0f
        || VortexChargePool > 0f
        || OknexChargePool > 0f
        || ClipLoad > 0
        || ClipSize > 0
        || HagarLoad > 0
        || MinelayerMines > 0
        || ArcHeat > 0f
        || ViewmodelFrame != 0
        || BeamState != 0;

    /// <summary>Boxing-free field-by-field equality, used by <c>NetEntityState.Diff</c> to decide whether the
    /// <c>WepentView</c> mask bit needs setting. NOT an <c>object.Equals</c> override (no allocation / no value
    /// copy of <paramref name="other"/>).</summary>
    public readonly bool Equals(in WepentViewState other) =>
        VortexCharge == other.VortexCharge
        && OknexCharge == other.OknexCharge
        && VortexChargePool == other.VortexChargePool
        && OknexChargePool == other.OknexChargePool
        && ClipLoad == other.ClipLoad
        && ClipSize == other.ClipSize
        && HagarLoad == other.HagarLoad
        && MinelayerMines == other.MinelayerMines
        && ArcHeat == other.ArcHeat
        && ViewmodelFrame == other.ViewmodelFrame
        && BeamState == other.BeamState;

    /// <summary>Code a [0,1] value as a byte (×255, rounded, clamped [0,255]) — the uniform charge/pool/heat
    /// quantization. Out-of-range inputs saturate rather than wrap. Shared by <c>WepentViewCodec</c> (the wire
    /// codec) and kept here so the quantization rule lives with the data definition.</summary>
    public static int EncodeUnit(float value)
    {
        int q = (int)MathF.Round(value * 255f);
        if (q < 0) q = 0;
        else if (q > 255) q = 255;
        return q;
    }
}
