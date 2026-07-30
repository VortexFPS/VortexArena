using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Invincible Projectiles mutator — port of
/// common/mutators/mutator/invincibleproj/sv_invincibleproj.qc. Makes fired projectiles indestructible:
/// any projectile with health has it zeroed on spawn, which (in QC) disables the damage calculations that
/// could shoot it down. Enabled by the <c>g_invincible_projectiles</c> cvar.
/// </summary>
[Mutator]
public sealed class InvincibleProjectilesMutator : MutatorBase
{
    public InvincibleProjectilesMutator() => NetName = "invincibleprojectiles";

    // QC: expr_evaluate(autocvar_g_invincible_projectiles).
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_invincible_projectiles") != 0f;

    private HookHandler<MutatorHooks.EditProjectileArgs>? _onEditProjectile;

    public override void Hook()
    {
        _onEditProjectile ??= OnEditProjectile;
        MutatorHooks.EditProjectile.Add(_onEditProjectile);
    }

    public override void Unhook()
    {
        if (_onEditProjectile is not null) MutatorHooks.EditProjectile.Remove(_onEditProjectile);
    }

    // MUTATOR_HOOKFUNCTION(invincibleprojectiles, EditProjectile)
    private bool OnEditProjectile(ref MutatorHooks.EditProjectileArgs args)
    {
        Entity proj = args.Projectile;
        // QC: if the projectile has health, SetResourceExplicit(proj, RES_HEALTH, 0) so it can't be shot down.
        if (proj.GetResource(ResourceType.Health) != 0f)
            proj.SetResourceExplicit(ResourceType.Health, 0f);
        return false;
    }

    // MUTATOR_HOOKFUNCTION(invincibleprojectiles, BuildMutatorsString) — sv_invincibleproj.qc:17-20
    public override string BuildMutatorsString(string s) => s + ":InvincibleProjectiles";

    // MUTATOR_HOOKFUNCTION(invincibleprojectiles, BuildMutatorsPrettyString) — sv_invincibleproj.qc:22-25
    public override string BuildMutatorsPrettyString(string s) => s + ", Invincible Projectiles";
}
