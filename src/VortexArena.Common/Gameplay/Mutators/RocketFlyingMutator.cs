// Port of common/mutators/mutator/rocketflying/sv_rocketflying.qc

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Rocket Flying mutator — port of common/mutators/mutator/rocketflying/sv_rocketflying.qc. Removes the
/// Devastator/Minelayer remote-detonation delay so a rocket can be detonated the instant it's fired (classic
/// "rocket flying" movement: ride your own blast). Enabled by the <c>g_rocket_flying</c> cvar.
///
/// Ported: the EditProjectile hook that clears the detonate delay on a freshly-fired "rocket"/"mine"
/// (<c>g_rocket_flying_disabledelays</c>, default true). QC sets <c>proj.spawnshieldtime = time</c>; the port's
/// rocket/mine detonate-gate field is <see cref="Entity.ProjectileDetonateTime"/> (the role QC overloaded onto
/// <c>.spawnshieldtime</c> for projectiles), set to <c>time</c> here. With the gate at <c>time</c> the timer
/// branch (<c>time &gt;= ProjectileDetonateTime</c>) is immediately true, so the rocket/mine can be detonated
/// the instant it is fired — both Devastator (<c>W_Devastator_RemoteExplode</c>) and Minelayer
/// (<c>W_MineLayer_RemoteExplode</c>) now read that field.
///
/// Distinct from <see cref="Entity.SpawnShieldExpire"/>, which stays the PLAYER spawn-shield (read by the
/// damage pipeline) — QC reused one <c>.spawnshieldtime</c> name for both roles, but the port splits them so a
/// rocket is never treated as spawn-shielded.
///
/// Also ported: the AllowRocketJumping hook (forces the Devastator's dedicated remote-jump self-boost on
/// regardless of <c>g_balance_devastator_remote_jump</c>) and the BuildMutatorsString /
/// BuildMutatorsPrettyString contributions (advertise ":RocketFlying" / ", Rocket Flying").
/// </summary>
[Mutator]
public sealed class RocketFlyingMutator : MutatorBase
{
    /// <summary>QC autocvar_g_rocket_flying_disabledelays (default true).</summary>
    public bool DisableDelays = true;

    public RocketFlyingMutator() => NetName = "rocketflying";

    // QC: REGISTER_MUTATOR(rocketflying, expr_evaluate(autocvar_g_rocket_flying)) — g_rocket_flying is a string.
    public override bool IsEnabled =>
        Api.Services is not null && ExprEvaluate(Api.Cvars.GetString("g_rocket_flying"));

    private HookHandler<MutatorHooks.EditProjectileArgs>? _onEditProjectile;
    private HookHandler<MutatorHooks.AllowRocketJumpingArgs>? _onAllowRocketJumping;

    public override void Hook()
    {
        _onEditProjectile ??= OnEditProjectile;
        MutatorHooks.EditProjectile.Add(_onEditProjectile);
        _onAllowRocketJumping ??= OnAllowRocketJumping;
        MutatorHooks.AllowRocketJumping.Add(_onAllowRocketJumping);
        if (Api.Services is not null)
            DisableDelays = ReadBool("g_rocket_flying_disabledelays", true);
    }

    public override void Unhook()
    {
        if (_onEditProjectile is not null) MutatorHooks.EditProjectile.Remove(_onEditProjectile);
        if (_onAllowRocketJumping is not null) MutatorHooks.AllowRocketJumping.Remove(_onAllowRocketJumping);
    }

    // MUTATOR_HOOKFUNCTION(rocketflying, AllowRocketJumping) — sv_rocketflying.qc:18-21: M_ARGV(0,bool)=true;
    private bool OnAllowRocketJumping(ref MutatorHooks.AllowRocketJumpingArgs args)
    {
        args.Allow = true; // force rocket jumping
        return true;
    }

    // MUTATOR_HOOKFUNCTION(rocketflying, BuildMutatorsString) — sv_rocketflying.qc:23-26
    public override string BuildMutatorsString(string s) => s + ":RocketFlying";

    // MUTATOR_HOOKFUNCTION(rocketflying, BuildMutatorsPrettyString) — sv_rocketflying.qc:28-31
    public override string BuildMutatorsPrettyString(string s) => s + ", Rocket Flying";

    // MUTATOR_HOOKFUNCTION(rocketflying, EditProjectile)
    private bool OnEditProjectile(ref MutatorHooks.EditProjectileArgs args)
    {
        Entity proj = args.Projectile;
        // QC: if(disabledelays && (proj.classname == "rocket" || proj.classname == "mine")) proj.spawnshieldtime = time;
        // The detonate gate is Entity.ProjectileDetonateTime (NOT SpawnShieldExpire, which is the player
        // spawn-shield); setting it to time puts the gate in the already-elapsed timer branch.
        if (DisableDelays && (proj.ClassName == "rocket" || proj.ClassName == "mine"))
            proj.ProjectileDetonateTime = Api.Services is not null ? Api.Clock.Time : 0f; // QC: proj.spawnshieldtime = time
        return false;
    }

    /// <summary>QC <c>expr_evaluate(s)</c> for a cvar string: false for "" / "0" / "false", true otherwise.</summary>
    private static bool ExprEvaluate(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        s = s.Trim();
        if (s == "0" || string.Equals(s, "false", System.StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static bool ReadBool(string name, bool fallback)
    {
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name) != 0f;
    }
}
