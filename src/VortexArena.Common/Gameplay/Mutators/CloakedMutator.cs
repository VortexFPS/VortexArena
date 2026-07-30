using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Cloaked mutator — port of common/mutators/mutator/cloaked/sv_cloaked.qc. Lowers the default alpha
/// of players and their weapons so everyone is semi-transparent. Enabled by the <c>g_cloaked</c> cvar.
///
/// Core behavior ported: the SetDefaultAlpha override that sets the player/weapon default alpha from
/// <c>g_balance_cloaked_alpha</c>. This is now LIVE — <see cref="GameWorld"/> fires
/// <c>MutatorHooks.FireSetDefaultAlpha()</c> at worldspawn (Wave-1 alpha-net seam) and seeds
/// <c>GameWorld.DefaultPlayerAlpha</c>/<c>DefaultWeaponAlpha</c>, which spawn/death read and the client
/// renders via <c>PlayerModel.ApplyAlpha</c>.
///
/// The active-mutators pretty-string contribution (QC <c>BuildMutatorsPrettyString</c> → ", Cloaked",
/// suppressed in CTS) is ported via <see cref="BuildMutatorsPrettyString"/>. The MENUQC create-game checkbox
/// (<c>g_cloaked</c> "Cloaked" / "All players are almost invisible") lives in the menu's DialogMutators; the
/// in-game per-mutator describe page is part of the absent menu-mutator describe system (shared infra gap).
/// </summary>
[Mutator]
public sealed class CloakedMutator : MutatorBase
{
    /// <summary>QC autocvar_g_balance_cloaked_alpha — the default alpha applied to players/weapons.</summary>
    public float Alpha = 0.25f;

    public CloakedMutator() => NetName = "cloaked";

    // QC: expr_evaluate(cvar_string("g_cloaked")).
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_cloaked") != 0f;

    private HookHandler<MutatorHooks.SetDefaultAlphaArgs>? _onSetDefaultAlpha;

    public override void Hook()
    {
        _onSetDefaultAlpha ??= OnSetDefaultAlpha;
        MutatorHooks.SetDefaultAlpha.Add(_onSetDefaultAlpha);

        if (Api.Services is not null)
        {
            float a = Api.Cvars.GetFloat("g_balance_cloaked_alpha");
            if (a != 0f) Alpha = a;
        }
    }

    public override void Unhook()
    {
        if (_onSetDefaultAlpha is not null) MutatorHooks.SetDefaultAlpha.Remove(_onSetDefaultAlpha);
    }

    // MUTATOR_HOOKFUNCTION(cloaked, SetDefaultAlpha)
    private bool OnSetDefaultAlpha(ref MutatorHooks.SetDefaultAlphaArgs args)
    {
        // QC: default_player_alpha = autocvar_g_balance_cloaked_alpha;
        //     default_weapon_alpha = default_player_alpha; return true;
        // Re-read the autocvar live (Base reads autocvar_* on every hook fire); fall back to the cached
        // 0.25 default if the cvar is unregistered/0 (matches the Hook() guard — 0 means "unset" here).
        float a = Alpha;
        if (Api.Services is not null)
        {
            float live = Api.Cvars.GetFloat("g_balance_cloaked_alpha");
            if (live != 0f) a = live;
        }
        args.PlayerAlpha = a;
        args.WeaponAlpha = a; // default_weapon_alpha = default_player_alpha
        return true; // QC returns true (handled).
    }

    // MUTATOR_HOOKFUNCTION(cloaked, BuildMutatorsPrettyString) — sv_cloaked.qc:15-18:
    //   if (!g_cts) M_ARGV(0, string) = strcat(M_ARGV(0, string), ", Cloaked");
    // Append ", Cloaked" to the human-readable active-mutators string, suppressed in the CTS gametype.
    public override string BuildMutatorsPrettyString(string s)
    {
        // QC g_cts gate — mirrors the !g_cts read PowerupsMutator uses for its CTS-suppressed broadcasts.
        bool cts = Api.Services is not null && Api.Cvars.GetFloat("g_cts") != 0f;
        return cts ? s : s + ", Cloaked";
    }
}
