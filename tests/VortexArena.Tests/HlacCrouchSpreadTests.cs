using System.Linq;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Pins the HLAC crouch-spread modifier to the QC ground truth:
///   hlac.qc:33-34  W_HLAC_Attack  — if (IS_DUCKED &amp;&amp; IS_ONGROUND) spread *= spread_crouchmod (after the
///                                    min/add/max spread calc, before projectile setup)
///   hlac.qc:79-80  W_HLAC_Attack2 — spread = WEP_CVAR_SEC(spread); same crouch gate before the bolt loop
///
/// The gate fires ONLY when the actor is BOTH ducked AND on the ground (mirrors Machinegun.CrouchSpreadMod).
/// Standing, or ducked-but-airborne, leaves spread untouched. The shipped multipliers are 0.25 (primary) and
/// 0.5 (secondary).
///
/// Spread is applied through the deterministic <see cref="Prandom"/> PRNG, so two runs from the same seed draw
/// the SAME random scatter vector — which makes the gate observable exactly: re-seeding then firing
/// standing vs ducked+grounded isolates the crouchmod factor as the only difference. The crouchmod=0 case is
/// the cleanest pin of all: zero spread means the bolt leaves perfectly along the aim direction.
///
/// Runs in the GlobalState collection (mutates the process-global registries + Api.Services).
/// </summary>
[Collection("GlobalState")]
public class HlacCrouchSpreadTests : IDisposable
{
    public void Dispose() => MutatorActivation.DeactivateAll();

    private static EngineServices Boot(params (string name, string value)[] cvars)
    {
        var facade = new EngineServices(new CollisionWorld());
        Api.Services = facade;
        VehicleCommon.GameStopped = false;
        GameRegistries.Reset();
        StatusEffectsCatalog.RegisterAll();
        foreach (var (n, v) in cvars) facade.Cvars.Set(n, v);
        GameRegistries.Bootstrap();
        Combat.System = new DamageSystem();
        MutatorActivation.Apply();
        return facade;
    }

    private static Entity NewPlayer(EngineServices f, bool ducked, bool onGround)
    {
        Entity e = f.Entities.Spawn();
        e.ClassName = "player";
        e.Flags = EntFlags.Client | (onGround ? EntFlags.OnGround : 0);
        e.IsDucked = ducked;
        e.Origin = Vector3.Zero;
        e.Angles = Vector3.Zero;        // forward == AngleVectors('0 0 0').forward (a fixed aim axis)
        e.Mins = new Vector3(-16, -16, -24);
        e.Maxs = new Vector3(16, 16, 45);
        e.Health = 100; e.MaxHealth = 100;
        e.TakeDamage = DamageMode.Yes;
        f.Entities.SetOrigin(e, Vector3.Zero);
        return e;
    }

    private static void Arm(Entity actor, Weapon w, WeaponSlot slot, FireMode fire)
    {
        actor.ActiveWeaponId = w.RegistryId;
        actor.SetResourceExplicit(ResourceType.Cells, 999f);
        var st = actor.WeaponState(slot);
        st.State = WeaponFireState.Ready;
        st.AttackFinished = 0f;
        if (fire == FireMode.Secondary) st.ButtonAttack2 = true;
        else st.ButtonAttack = true;
    }

    /// <summary>
    /// The aim axis a centered, ZERO-spread HLAC bolt actually leaves along. NOTE this is NOT the raw
    /// AngleVectors('0 0 0').forward: W_SetupShot recomputes the shot direction from the MUZZLE origin
    /// (DefaultMuzzleOffset = (12,0,-8), the gun tag) toward the trueaim endpoint, so a perfectly straight
    /// shot is tilted ~0.000244 (== 8/sqrt(8²+32768²)) off forward by that fixed muzzle parallax. We capture
    /// it empirically by firing a base-spread-0 shot (no random scatter), which isolates exactly that aim
    /// direction — the correct baseline for "the crouch gate zeroed the spread".
    /// </summary>
    private static Vector3 StraightFireAxis(FireMode fire)
    {
        // Standing, base spread 0 → CalculateSpread early-outs (spread <= 0), so the bolt leaves along the
        // pure muzzle→trueaim axis with no scatter. Independent of crouchmod (not ducked → gate idle).
        var (spreadCvar, _) = SpreadCvars(fire);
        Vector3 v = FireOneBolt(fire, ducked: false, onGround: true, (spreadCvar, "0"));
        return QMath.Normalize(v);
    }

    /// <summary>The base-spread / crouchmod cvar names for the given fire mode.</summary>
    private static (string spread, string crouchmod) SpreadCvars(FireMode fire) => fire == FireMode.Secondary
        ? ("g_balance_hlac_secondary_spread", "g_balance_hlac_secondary_spread_crouchmod")
        : ("g_balance_hlac_primary_spread_min", "g_balance_hlac_primary_spread_crouchmod");

    /// <summary>
    /// Fire one HLAC shot from a fresh world (deterministic PRNG seed) and return the spawned bolt velocities.
    /// Primary spawns a single bolt; secondary spawns <c>shots</c> bolts.
    /// </summary>
    private static Vector3[] FireBolts(FireMode fire, bool ducked, bool onGround,
        params (string name, string value)[] cvars)
    {
        var f = Boot(cvars);
        Prandom.Seed(1234);                 // identical PRNG state for every run -> same random scatter draw
        var hlac = (Hlac)Weapons.ByName("hlac")!;
        var slot = new WeaponSlot(0);
        var actor = NewPlayer(f, ducked, onGround);
        Arm(actor, hlac, slot, fire);

        hlac.WrThink(actor, slot, fire);

        return f.Entities.FindByClass("hlacbolt").Where(e => !e.IsFreed)
                .Select(e => e.Velocity).ToArray();
    }

    private static Vector3 FireOneBolt(FireMode fire, bool ducked, bool onGround,
        params (string name, string value)[] cvars)
    {
        Vector3[] v = FireBolts(fire, ducked, onGround, cvars);
        Assert.NotEmpty(v);
        return v[0];
    }

    // ---- Primary (W_HLAC_Attack, hlac.qc:33-34) ----------------------------------------------------------

    [Fact]
    public void Primary_DefaultCrouchmod_Is_Quarter()
    {
        // hlac.qc seeds g_balance_hlac_primary_spread_crouchmod 0.25.
        Boot();
        Assert.Equal(0.25f, ((Hlac)Weapons.ByName("hlac")!).Primary.SpreadCrouchmod, 4);
    }

    [Fact]
    public void Primary_DuckedAndGrounded_ZeroCrouchmod_FiresStraight()
    {
        // spread_crouchmod 0 -> ducked+grounded zeroes the spread -> the bolt leaves exactly along the aim
        // axis (CalculateSpread early-outs at spread <= 0). This is the unambiguous proof the gate fired.
        Vector3 v = FireOneBolt(FireMode.Primary, ducked: true, onGround: true,
            ("g_balance_hlac_primary_spread_min", "0.2"),   // a real base spread so the gate has something to cut
            ("g_balance_hlac_primary_spread_max", "1"),
            ("g_balance_hlac_primary_spread_crouchmod", "0"));

        Vector3 dir = QMath.Normalize(v);
        Vector3 axis = StraightFireAxis(FireMode.Primary);
        Assert.Equal(axis.X, dir.X, 4);
        Assert.Equal(axis.Y, dir.Y, 4);
        Assert.Equal(axis.Z, dir.Z, 4);
    }

    [Fact]
    public void Primary_Standing_KeepsSpread_NotStraight()
    {
        // Same base spread, but standing -> the gate does NOT fire, so the random scatter deflects the bolt
        // off the aim axis (it is NOT perfectly straight).
        Vector3 v = FireOneBolt(FireMode.Primary, ducked: false, onGround: true,
            ("g_balance_hlac_primary_spread_min", "0.2"),
            ("g_balance_hlac_primary_spread_max", "1"),
            ("g_balance_hlac_primary_spread_crouchmod", "0"));

        Vector3 dir = QMath.Normalize(v);
        Vector3 axis = StraightFireAxis(FireMode.Primary);
        // With a nonzero base spread and seed 1234 the scatter is real -> measurable deviation from the axis.
        Assert.True(Vector3.Distance(dir, axis) > 1e-3f,
            "standing shot must keep its spread (crouch gate must NOT fire when not ducked)");
    }

    [Fact]
    public void Primary_DuckedButAirborne_DoesNotApplyCrouchmod()
    {
        // IS_ONGROUND is required: ducked in the air leaves spread untouched. Same seed as the standing run,
        // so an unmodified spread reproduces the SAME bolt velocity bit-for-bit.
        Vector3 standing = FireOneBolt(FireMode.Primary, ducked: false, onGround: true,
            ("g_balance_hlac_primary_spread_min", "0.2"),
            ("g_balance_hlac_primary_spread_max", "1"));
        Vector3 duckedAir = FireOneBolt(FireMode.Primary, ducked: true, onGround: false,
            ("g_balance_hlac_primary_spread_min", "0.2"),
            ("g_balance_hlac_primary_spread_max", "1"));

        Assert.Equal(standing.X, duckedAir.X, 4);
        Assert.Equal(standing.Y, duckedAir.Y, 4);
        Assert.Equal(standing.Z, duckedAir.Z, 4);
    }

    [Fact]
    public void Primary_DuckedGrounded_Crouchmod1_MatchesStanding()
    {
        // spread_crouchmod 1 is a no-op multiply: the gate fires but the spread is unchanged, so a
        // ducked+grounded shot reproduces the standing shot exactly (same seed, same scatter).
        Vector3 standing = FireOneBolt(FireMode.Primary, ducked: false, onGround: true,
            ("g_balance_hlac_primary_spread_min", "0.2"),
            ("g_balance_hlac_primary_spread_max", "1"),
            ("g_balance_hlac_primary_spread_crouchmod", "1"));
        Vector3 crouched = FireOneBolt(FireMode.Primary, ducked: true, onGround: true,
            ("g_balance_hlac_primary_spread_min", "0.2"),
            ("g_balance_hlac_primary_spread_max", "1"),
            ("g_balance_hlac_primary_spread_crouchmod", "1"));

        Assert.Equal(standing.X, crouched.X, 4);
        Assert.Equal(standing.Y, crouched.Y, 4);
        Assert.Equal(standing.Z, crouched.Z, 4);
    }

    [Fact]
    public void Primary_DuckedGrounded_TightensSpread_VsStanding()
    {
        // The shipped 0.25 multiplier reduces (does not zero) the spread: the crouched bolt deviates LESS from
        // the aim axis than the standing bolt fired from the same seed.
        const string sMin = "g_balance_hlac_primary_spread_min";
        const string sMax = "g_balance_hlac_primary_spread_max";
        Vector3 axis = StraightFireAxis(FireMode.Primary);

        Vector3 standing = FireOneBolt(FireMode.Primary, ducked: false, onGround: true,
            (sMin, "0.2"), (sMax, "1"), ("g_balance_hlac_primary_spread_crouchmod", "0.25"));
        Vector3 crouched = FireOneBolt(FireMode.Primary, ducked: true, onGround: true,
            (sMin, "0.2"), (sMax, "1"), ("g_balance_hlac_primary_spread_crouchmod", "0.25"));

        float standDev = Vector3.Distance(QMath.Normalize(standing), axis);
        float crouchDev = Vector3.Distance(QMath.Normalize(crouched), axis);
        Assert.True(crouchDev < standDev,
            $"crouch must tighten spread (crouch dev {crouchDev} should be < standing dev {standDev})");
        Assert.True(crouchDev > 1e-4f, "0.25 only reduces spread, it does not zero it");
    }

    // ---- Secondary (W_HLAC_Attack2, hlac.qc:79-80) -------------------------------------------------------

    [Fact]
    public void Secondary_DefaultCrouchmod_Is_Half()
    {
        // hlac.qc seeds g_balance_hlac_secondary_spread_crouchmod 0.5.
        Boot();
        Assert.Equal(0.5f, ((Hlac)Weapons.ByName("hlac")!).Secondary.SpreadCrouchmod, 4);
    }

    [Fact]
    public void Secondary_DuckedAndGrounded_ZeroCrouchmod_AllBoltsStraight()
    {
        // With spread_crouchmod 0 every bolt in the burst leaves along the exact aim axis.
        Vector3[] bolts = FireBolts(FireMode.Secondary, ducked: true, onGround: true,
            ("g_balance_hlac_secondary_spread", "0.2"),
            ("g_balance_hlac_secondary_spread_crouchmod", "0"));

        Assert.NotEmpty(bolts);
        Vector3 axis = StraightFireAxis(FireMode.Secondary);
        foreach (Vector3 vel in bolts)
        {
            Vector3 dir = QMath.Normalize(vel);
            Assert.Equal(axis.X, dir.X, 4);
            Assert.Equal(axis.Y, dir.Y, 4);
            Assert.Equal(axis.Z, dir.Z, 4);
        }
    }

    [Fact]
    public void Secondary_Standing_KeepsSpread()
    {
        // Standing -> the gate does not fire, so the burst scatters (at least one bolt is off the aim axis).
        Vector3[] bolts = FireBolts(FireMode.Secondary, ducked: false, onGround: true,
            ("g_balance_hlac_secondary_spread", "0.2"),
            ("g_balance_hlac_secondary_spread_crouchmod", "0"));

        Assert.NotEmpty(bolts);
        Vector3 axis = StraightFireAxis(FireMode.Secondary);
        bool anyScattered = bolts.Any(v => Vector3.Distance(QMath.Normalize(v), axis) > 1e-3f);
        Assert.True(anyScattered, "standing burst must keep its spread (crouch gate must NOT fire)");
    }

    [Fact]
    public void Secondary_DuckedButAirborne_DoesNotApplyCrouchmod()
    {
        // IS_ONGROUND required for the secondary too: ducked in the air keeps the full spread, so the burst
        // reproduces the standing burst bolt-for-bolt under the same seed.
        Vector3[] standing = FireBolts(FireMode.Secondary, ducked: false, onGround: true,
            ("g_balance_hlac_secondary_spread", "0.2"));
        Vector3[] duckedAir = FireBolts(FireMode.Secondary, ducked: true, onGround: false,
            ("g_balance_hlac_secondary_spread", "0.2"));

        Assert.Equal(standing.Length, duckedAir.Length);
        for (int i = 0; i < standing.Length; i++)
        {
            Assert.Equal(standing[i].X, duckedAir[i].X, 4);
            Assert.Equal(standing[i].Y, duckedAir[i].Y, 4);
            Assert.Equal(standing[i].Z, duckedAir[i].Z, 4);
        }
    }
}
