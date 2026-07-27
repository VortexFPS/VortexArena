namespace XonoticGodot.Common.Framework;

/// <summary>Entity movement integrator selector (QC MOVETYPE_*). See planning/specs/determinism-and-physics.md.</summary>
public enum MoveType
{
    None = 0,
    Walk = 3,
    Step = 4,
    Fly = 5,
    Toss = 6,
    Push = 7,
    Noclip = 8,
    FlyMissile = 9,
    Bounce = 10,
    BounceMissile = 11,
    Follow = 12,
    /// <summary>QC MOVETYPE_PHYSICS (dpextensions.qc:1707) — ODE-backed rigid-body physics. The port has no
    /// physics-engine integrator, so the engine simulation falls through to its default (think-only) case, the
    /// same as Base without ODE. Used by the sandbox "physics 2" object mode so the serialized movetype byte is
    /// Base-identical (32).</summary>
    Physics = 32,
    FlyWorldOnly = 33,
}

/// <summary>Collision solidity (QC SOLID_*).</summary>
public enum Solid
{
    Not = 0,
    Trigger = 1,
    BBox = 2,
    SlideBox = 3,
    Bsp = 4,
    Corpse = 5,
}

/// <summary>Death state (QC DEAD_*).</summary>
public enum DeadFlag
{
    No = 0,
    Dying = 1,
    Dead = 2,
    Respawnable = 3,
    Respawning = 4,
}

/// <summary>Respawn behavior flags (QC RESPAWN_* in server/client.qh).</summary>
[Flags]
public enum RespawnFlag
{
    None = 0,
    /// <summary>QC RESPAWN_FORCE: auto-respawn at respawn_time_max even without a button press (g_forced_respawn).</summary>
    Force = 1,
    /// <summary>QC RESPAWN_SILENT: don't network the respawn countdown timer to the client.</summary>
    Silent = 2,
    /// <summary>QC RESPAWN_DENY: respawning is blocked entirely (the player stays dead).</summary>
    Deny = 4,
}

/// <summary>takedamage values (QC DAMAGE_*).</summary>
public enum DamageMode
{
    No = 0,
    Yes = 1,
    Aim = 2,
}

/// <summary>Trace entity filter (QC tracebox tryents / MOVE_*).</summary>
public enum MoveFilter
{
    Normal = 0,
    NoMonsters = 1,
    Missile = 2,
    WorldOnly = 3,
    HitModel = 4,
    /// <summary>PORT EXTENSION (no DP MOVE_* equivalent): clips exactly like <see cref="Normal"/> except
    /// PLAYER entities (SOLID_SLIDEBOX + FL_CLIENT) never block the move. Used by the player movement hull
    /// traces under <c>sv_player_softcollision</c> so players slip through each other while the server's
    /// post-move separation pass (PlayerSeparation) pushes overlapping bodies apart. Monsters (SLIDEBOX +
    /// FL_MONSTER) and everything else still clip, and weapon/melee traces keep <see cref="Normal"/> so
    /// players remain shootable.</summary>
    NoPlayers = 5,
}

/// <summary>Entity flag bits (QC FL_*).</summary>
[Flags]
public enum EntFlags
{
    None = 0,
    Fly = 1,
    Swim = 2,
    Client = 8,
    InWater = 16,
    Monster = 32,
    GodMode = 64,
    NoTarget = 128,
    Item = 256,
    OnGround = 512,
    PartialGround = 1024,
    WaterJump = 2048,
    JumpReleased = 4096,
}

/// <summary>Legacy point-content values (QC CONTENT_*). Engine traces use richer SUPERCONTENTS bitmasks.</summary>
public enum Contents
{
    Empty = -1,
    Solid = -2,
    Water = -3,
    Slime = -4,
    Lava = -5,
    Sky = -6,
}
