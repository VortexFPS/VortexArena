using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Running Guns mutator — port of common/mutators/mutator/running_guns/sv_running_guns.qc. Hides the
/// player models but keeps their weapons visible, so the map looks like floating guns running around.
/// Enabled by the <c>g_running_guns</c> cvar.
///
/// Core behavior ported: the SetDefaultAlpha override (player alpha -1 → invisible, weapon alpha +1 →
/// fully visible). The hook is fired live at worldspawn via <see cref="MutatorHooks.FireSetDefaultAlpha"/>
/// (GameWorld seeds <c>DefaultPlayerAlpha</c>/<c>DefaultWeaponAlpha</c> from the returned values), so the
/// player-invisible / gun-visible look is applied to spawned entities.
/// </summary>
[Mutator]
public sealed class RunningGunsMutator : MutatorBase
{
    public RunningGunsMutator() => NetName = "running_guns";

    // QC SVQC: REGISTER_MUTATOR(running_guns, autocvar_g_running_guns).
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_running_guns") != 0f;

    private HookHandler<MutatorHooks.SetDefaultAlphaArgs>? _onSetDefaultAlpha;

    public override void Hook()
    {
        _onSetDefaultAlpha ??= OnSetDefaultAlpha;
        MutatorHooks.SetDefaultAlpha.Add(_onSetDefaultAlpha);
    }

    public override void Unhook()
    {
        if (_onSetDefaultAlpha is not null) MutatorHooks.SetDefaultAlpha.Remove(_onSetDefaultAlpha);
    }

    // MUTATOR_HOOKFUNCTION(running_guns, SetDefaultAlpha)
    // Fired live at worldspawn through MutatorHooks.FireSetDefaultAlpha; GameWorld reads the resolved
    // PlayerAlpha/WeaponAlpha into DefaultPlayerAlpha/DefaultWeaponAlpha for player/weapon spawn.
    private bool OnSetDefaultAlpha(ref MutatorHooks.SetDefaultAlphaArgs args)
    {
        // QC: default_player_alpha = -1; default_weapon_alpha = +1; return true;
        args.PlayerAlpha = -1f; // negative alpha → model hidden (QC convention)
        args.WeaponAlpha = 1f;
        return true;
    }
}
