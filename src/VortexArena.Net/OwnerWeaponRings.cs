namespace XonoticGodot.Net;

/// <summary>
/// The owner-only weapon-HUD ring scalars (QC the networked <c>wepent.*</c> fields: <c>vortex_charge</c> /
/// <c>vortex_chargepool_ammo</c> / <c>clip_load</c> / <c>clip_size</c> / <c>hagar_load</c> (+ <c>load_max</c>) /
/// <c>minelayer_mines</c> (+ limit) / <c>arc_heat_percent</c>). Resolved server-side off the AUTHORITATIVE
/// player and replicated on the owner block so a PURE remote / dedicated-server client draws the same crosshair
/// charge / clip / load / heat rings the listen host shows from its local slot state.
///
/// <para>This single Write/Read pair is the source of truth for the wire layout, so the server append
/// (<c>ServerNet.WriteOwnerState</c>) and the client read (<c>ClientNet.HandleSnapshot</c>) can never drift —
/// the two halves drifting (the server wrote the tail, the client never read it) is exactly what desynced the
/// whole owner block for a remote client. Guarded by <c>OwnerWeaponRingsTests</c>.</para>
///
/// <para>Each value carries the <c>CrosshairPanel</c> "no data" sentinel (charge/clip-load/hagar/mine/arc = -1,
/// clip-size = 0) when the active weapon doesn't own that ring, so only the held weapon's ring draws.</para>
/// </summary>
public struct OwnerWeaponRings
{
    public float VortexCharge;
    public float VortexChargePool;
    public float ClipLoad;
    public float ClipSize;
    public float HagarLoad;
    public float HagarLoadMax;
    public float MineCount;
    public float MineLimit;
    public float ArcHeat;

    /// <summary>The "no live weapon / no ring" sentinels — exactly the <c>CrosshairPanel</c> field defaults, so a
    /// client with no owner data (or whose held weapon owns no ring) draws nothing.</summary>
    public static OwnerWeaponRings None => new()
    {
        VortexCharge = -1f,
        VortexChargePool = -1f,
        ClipLoad = -1f,
        ClipSize = 0f,
        HagarLoad = -1f,
        HagarLoadMax = 4f,
        MineCount = -1f,
        MineLimit = 3f,
        ArcHeat = -1f,
    };

    /// <summary>Append the nine ring scalars (fixed layout). Mirror of <see cref="Read"/>.</summary>
    public readonly void Write(BitWriter w)
    {
        w.WriteFloat(VortexCharge);
        w.WriteFloat(VortexChargePool);
        w.WriteFloat(ClipLoad);
        w.WriteFloat(ClipSize);
        w.WriteFloat(HagarLoad);
        w.WriteFloat(HagarLoadMax);
        w.WriteFloat(MineCount);
        w.WriteFloat(MineLimit);
        w.WriteFloat(ArcHeat);
    }

    /// <summary>Read the nine ring scalars in the same order <see cref="Write"/> appended them. Always consumes
    /// exactly nine floats (36 bytes) to keep the owner block aligned, even on underflow (BitReader zero-fills).</summary>
    public static OwnerWeaponRings Read(ref BitReader r) => new()
    {
        VortexCharge = r.ReadFloat(),
        VortexChargePool = r.ReadFloat(),
        ClipLoad = r.ReadFloat(),
        ClipSize = r.ReadFloat(),
        HagarLoad = r.ReadFloat(),
        HagarLoadMax = r.ReadFloat(),
        MineCount = r.ReadFloat(),
        MineLimit = r.ReadFloat(),
        ArcHeat = r.ReadFloat(),
    };
}
