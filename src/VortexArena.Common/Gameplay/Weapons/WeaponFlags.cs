namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Weapon spawnflags — faithful port of the WEP_TYPE_* / WEP_FLAG_* bit constants in
/// common/weapons/weapon.qh. Stored in <see cref="Weapon.SpawnFlags"/> (an <c>int</c> bitfield),
/// so these are <c>const int</c> rather than a [Flags] enum to OR together exactly as the QC does.
/// </summary>
public static class WeaponFlags
{
    public const int TypeOther       = 1 << 0;  // WEP_TYPE_OTHER — not for damaging people
    public const int TypeSplash      = 1 << 1;  // WEP_TYPE_SPLASH — splash damage
    public const int TypeHitscan     = 1 << 2;  // WEP_TYPE_HITSCAN
    public const int CanClimb        = 1 << 3;  // WEP_FLAG_CANCLIMB — usable for movement
    public const int Normal          = 1 << 4;  // WEP_FLAG_NORMAL — in the "most weapons" set
    public const int Hidden          = 1 << 5;  // WEP_FLAG_HIDDEN
    public const int Reloadable      = 1 << 6;  // WEP_FLAG_RELOADABLE
    public const int SuperWeapon     = 1 << 7;  // WEP_FLAG_SUPERWEAPON
    public const int MutatorBlocked  = 1 << 8;  // WEP_FLAG_MUTATORBLOCKED
    public const int TypeMeleePri    = 1 << 9;  // WEP_TYPE_MELEE_PRI
    public const int TypeMeleeSec    = 1 << 10; // WEP_TYPE_MELEE_SEC
    public const int DualWield       = 1 << 11; // WEP_FLAG_DUALWIELD
    public const int NoDual          = 1 << 12; // WEP_FLAG_NODUAL
    public const int PenetrateWalls  = 1 << 13; // WEP_FLAG_PENETRATEWALLS
    public const int Bleed           = 1 << 14; // WEP_FLAG_BLEED
    public const int NoTrueAim       = 1 << 15; // WEP_FLAG_NOTRUEAIM
    public const int SpecialAttack   = 1 << 16; // WEP_FLAG_SPECIALATTACK
}
