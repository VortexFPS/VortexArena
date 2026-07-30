namespace XonoticGodot.Net;

/// <summary>
/// The owner-only inventory stats (QC the ammo/items STATs: <c>RES_SHELLS/BULLETS/ROCKETS/CELLS/FUEL</c>,
/// <c>STAT_WEAPONS</c>, <c>IT_UNLIMITED_AMMO</c>, plus the <c>STAT_ITEMS</c> flag bits the powerups panel reads —
/// jetpack / fuel regenerator / unlimited superweapons). Resolved server-side off the AUTHORITATIVE player and
/// replicated on the owner block so a PURE remote / dedicated-server client's full HUD (ammo panel, weapons
/// panel, powerups item rows, crosshair ring color) draws the same values the listen host reads straight off
/// <c>LocalServerPlayer</c>.
///
/// <para>This single Write/Read pair is the source of truth for the wire layout, so the server append
/// (<c>ServerNet.WriteOwnerState</c>) and the client read (<c>ClientNet.HandleSnapshot</c>) can never drift —
/// hand-appended owner-block halves drifting is exactly what desynced the whole owner block once before (see
/// <see cref="OwnerWeaponRings"/>). Guarded by <c>OwnerInventoryTests</c>.</para>
///
/// <para>Ammo pools are floats (float resources server-side; fuel burns fractionally). The 64-bit weapon bitset
/// rides as lo/hi 32-bit halves because <see cref="BitWriter.WriteULong"/> is a 32-bit write.</para>
/// </summary>
public struct OwnerInventory
{
    public float Shells;
    public float Bullets;
    public float Rockets;
    public float Cells;
    public float Fuel;

    /// <summary>QC STAT_WEAPONS: the owned-weapon bitset, bit index == weapon RegistryId.</summary>
    public ulong WeaponBits;

    /// <summary>QC IT_UNLIMITED_AMMO.</summary>
    public bool UnlimitedAmmo;

    /// <summary>QC STAT_ITEMS flag bits (<c>Entity.Items</c> — the ItemFlag enum): jetpack / fuel regenerator /
    /// unlimited superweapons, the possession rows the powerups panel draws.</summary>
    public int ItemFlags;

    /// <summary>The "no owner data" zero state a client holds before its first snapshot.</summary>
    public static OwnerInventory None => default;

    /// <summary>Append the inventory block (fixed layout). Mirror of <see cref="Read"/>.</summary>
    public readonly void Write(BitWriter w)
    {
        w.WriteFloat(Shells);
        w.WriteFloat(Bullets);
        w.WriteFloat(Rockets);
        w.WriteFloat(Cells);
        w.WriteFloat(Fuel);
        w.WriteULong((uint)(WeaponBits & 0xFFFFFFFFUL));
        w.WriteULong((uint)(WeaponBits >> 32));
        w.WriteBool(UnlimitedAmmo);
        w.WriteULong((uint)ItemFlags);
    }

    /// <summary>Read the inventory block in the same order <see cref="Write"/> appended it. Always consumes the
    /// full fixed layout to keep the owner block aligned, even on underflow (BitReader zero-fills).</summary>
    public static OwnerInventory Read(ref BitReader r)
    {
        var inv = new OwnerInventory
        {
            Shells = r.ReadFloat(),
            Bullets = r.ReadFloat(),
            Rockets = r.ReadFloat(),
            Cells = r.ReadFloat(),
            Fuel = r.ReadFloat(),
        };
        ulong lo = r.ReadULong();
        ulong hi = r.ReadULong();
        inv.WeaponBits = lo | (hi << 32);
        inv.UnlimitedAmmo = r.ReadBool();
        inv.ItemFlags = (int)r.ReadULong();
        return inv;
    }
}
