using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Tests;

/// <summary>
/// Regression guard for the spawn-loadout weapon equip (the weapon-fire driver, §E). A freshly-spawned player
/// must (1) own its loadout in the CANONICAL <see cref="WepSet"/> (<see cref="Entity.OwnedWeaponSet"/> — what
/// <see cref="Inventory"/> + the W_WeaponFrame driver read), not only the NetName set, and (2) have an active
/// weapon equipped. The spawn path previously filled only <c>Player.OwnedWeapons</c> (the NetName set), leaving
/// the WepSet empty and <c>ActiveWeaponId == -1</c>; once the fire driver started gating on the active weapon,
/// a spawned player could never fire — the "press fire and nothing happens" bug on the networked / listen-server
/// path. QC <c>client.qc PutPlayerInServer</c> equips <c>w_getbestweapon</c> on every (re)spawn.
/// </summary>
public class SpawnLoadoutTests
{
    public SpawnLoadoutTests()
    {
        GameRegistries.Bootstrap();                               // populate the Weapon registry (idempotent)
        Api.Services = new EngineServices(new CollisionWorld());  // start_* cvars + the entity table the spawn links into
    }

    [Fact]
    public void Spawn_PopulatesWepSet_AndEquipsBestWeapon()
    {
        var p = new Player { Flags = EntFlags.Client };
        SpawnSystem.PutPlayerInServer(p, new SpawnPoint(NVec3.Zero, NVec3.Zero));

        Assert.False(p.OwnedWeaponSet.IsEmpty);          // the WepSet authority is populated (was empty → no fire)
        Assert.True(p.ActiveWeaponId >= 0);              // a weapon is equipped, so the fire driver can fire it
        Assert.NotNull(Inventory.CurrentWeapon(p));      // and it resolves to a real Weapon
    }

    /// <summary>
    /// Regression guard (Wave-3 review blocker): every knockback consumer — the base damage push AND the
    /// globalforces mutator — multiplies the victim's velocity add by <see cref="Entity.DamageForceScale"/>.
    /// A spawn that leaves it 0 means a player takes NO knockback at all. QC <c>client.qc:676</c> seeds it from
    /// <c>g_player_damageforcescale</c> (default 2) on every (re)spawn; this asserts the port does the same.
    /// </summary>
    [Fact]
    public void Spawn_SeedsDamageForceScale_SoKnockbackApplies()
    {
        var p = new Player { Flags = EntFlags.Client };
        SpawnSystem.PutPlayerInServer(p, new SpawnPoint(NVec3.Zero, NVec3.Zero));

        Assert.True(p.DamageForceScale > 0f);            // was 0 → no knockback / globalforces inert
    }

    /// <summary>
    /// Regression for "shot fires too low until I crouch once": W_SetupShot's eye is <c>origin + view_ofs</c>, but
    /// the spawn path never seeded <c>view_ofs</c> (it defaulted to 0 → eye AT the origin → shot ~35u low), and
    /// <see cref="PlayerPhysics.UpdateCrouch"/> only sets it on a crouch EDGE — so the shot stayed low until the
    /// first crouch-leave. QC <c>PutPlayerInServer</c> sets <c>view_ofs = PL_VIEW_OFS</c> on every (re)spawn; this
    /// asserts the port seeds the STANDING eye + clears the duck state (a respawn-while-ducked must reset).
    /// </summary>
    [Fact]
    public void Spawn_SeedsStandingViewOfs_SoShotsLeaveFromTheEye()
    {
        // simulate stale state a respawn could carry: a crouched eye + duck flag from a prior life.
        var p = new Player { Flags = EntFlags.Client, ViewOfs = NVec3.Zero, IsDucked = true };
        SpawnSystem.PutPlayerInServer(p, new SpawnPoint(NVec3.Zero, NVec3.Zero));

        Assert.Equal(XonoticGodot.Common.Physics.PlayerPhysics.StandViewOfs, p.ViewOfs); // standing eye seeded (0,0,35)
        Assert.False(p.IsDucked);                                                         // duck state reset to standing
    }
}
