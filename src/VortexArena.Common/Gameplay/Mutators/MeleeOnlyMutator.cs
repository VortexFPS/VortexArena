using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Melee Only mutator — port of common/mutators/mutator/melee_only/sv_melee_only.qc. Everyone spawns
/// with only the Shotgun (used for its melee secondary) and no shells; small health/armor pickups are
/// removed. Enabled by the <c>g_melee_only</c> cvar (and only when not instagib/overkill/arena).
///
/// Core behavior ported: the start loadout (shotgun, zero shells), the small-item filter, and the
/// throw/arena/random-weapon forbids.
/// </summary>
[Mutator]
public sealed class MeleeOnlyMutator : MutatorBase
{
    public MeleeOnlyMutator() => NetName = "melee_only";

    // QC: REGISTER_MUTATOR(melee_only, expr_evaluate(autocvar_g_melee_only)
    //   && !MUTATOR_IS_ENABLED(mutator_instagib) && !MUTATOR_IS_ENABLED(ok)
    //   && !MapInfo_LoadedGametype.m_weaponarena).
    // autocvar_g_melee_only is a STRING evaluated with expr_evaluate (like rocketflying), not a float.
    public override bool IsEnabled =>
        Api.Services is not null
        && ExprEvaluate(Api.Cvars.GetString("g_melee_only"))
        && !OtherEnabled("instagib")
        && !OtherEnabled("overkill")
        && Api.Cvars.GetFloat("g_weaponarena") == 0f; // QC !MapInfo_LoadedGametype.m_weaponarena

    // QC MUTATOR_IS_ENABLED reads the other mutator's enable predicate (not its added state), so
    // activation order between them can't race. (overkill's NetName is "ok"'s registration in the port.)
    private static bool OtherEnabled(string netName)
        => Mutators.ByName(netName) is { } m && m.IsEnabled;

    /// <summary>QC <c>expr_evaluate(s)</c> for a cvar string: false for "" / "0" / "false", true otherwise.</summary>
    private static bool ExprEvaluate(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        s = s.Trim();
        if (s == "0" || string.Equals(s, "false", System.StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private HookHandler<MutatorHooks.SetStartItemsArgs>? _onSetStartItems;
    private HookHandler<MutatorHooks.SetWeaponArenaArgs>? _onSetWeaponArena;
    private HookHandler<MutatorHooks.ForbidRandomStartWeaponsArgs>? _onForbidRandom;
    private HookHandler<MutatorHooks.ForbidThrowCurrentWeaponArgs>? _onForbidThrow;
    private HookHandler<MutatorHooks.FilterItemDefinitionArgs>? _onFilterItemDef;
    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _onPlayerSpawn;

    public override void Hook()
    {
        _onSetStartItems ??= OnSetStartItems;
        _onSetWeaponArena ??= OnSetWeaponArena;
        _onForbidRandom ??= OnForbidRandomStartWeapons;
        _onForbidThrow ??= OnForbidThrow;
        _onFilterItemDef ??= OnFilterItemDefinition;
        _onPlayerSpawn ??= OnPlayerSpawn;

        MutatorHooks.SetStartItems.Add(_onSetStartItems, HookOrder.Last); // QC CBC_ORDER_LAST
        MutatorHooks.SetWeaponArena.Add(_onSetWeaponArena);
        MutatorHooks.ForbidRandomStartWeapons.Add(_onForbidRandom);
        MutatorHooks.ForbidThrowCurrentWeapon.Add(_onForbidThrow);
        MutatorHooks.FilterItemDefinition.Add(_onFilterItemDef);
        MutatorHooks.PlayerSpawn.Add(_onPlayerSpawn);
    }

    public override void Unhook()
    {
        if (_onSetStartItems is not null) MutatorHooks.SetStartItems.Remove(_onSetStartItems);
        if (_onSetWeaponArena is not null) MutatorHooks.SetWeaponArena.Remove(_onSetWeaponArena);
        if (_onForbidRandom is not null) MutatorHooks.ForbidRandomStartWeapons.Remove(_onForbidRandom);
        if (_onForbidThrow is not null) MutatorHooks.ForbidThrowCurrentWeapon.Remove(_onForbidThrow);
        if (_onFilterItemDef is not null) MutatorHooks.FilterItemDefinition.Remove(_onFilterItemDef);
        if (_onPlayerSpawn is not null) MutatorHooks.PlayerSpawn.Remove(_onPlayerSpawn);
    }

    // The melee_only arena loadout (QC start_weapons = WEPSET(SHOTGUN)) applied per-spawn through the
    // weapon inventory: only the shotgun (used for its melee secondary), and switch to it.
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args)
    {
        Entity player = args.Player;
        Weapon? shotgun = Weapons.ByName("shotgun");
        if (shotgun is not null)
        {
            Inventory.ClearWeapons(player);
            Inventory.GiveWeapon(player, shotgun);
            Inventory.SwitchToBest(player);
        }
        player.SetResource(ResourceType.Shells, 0f); // QC: no shells, melee-only
        return false;
    }

    // MUTATOR_HOOKFUNCTION(melee_only, SetStartItems, CBC_ORDER_LAST)
    private bool OnSetStartItems(ref MutatorHooks.SetStartItemsArgs args)
    {
        StartLoadout l = args.Loadout;
        l.AmmoShells = 0f;
        l.SetWeapons("shotgun"); // QC: WEPSET(SHOTGUN)
        return false;
    }

    private bool OnSetWeaponArena(ref MutatorHooks.SetWeaponArenaArgs args)
    {
        args.Arena = "off";
        return false;
    }

    private bool OnForbidRandomStartWeapons(ref MutatorHooks.ForbidRandomStartWeaponsArgs args) => true;
    private bool OnForbidThrow(ref MutatorHooks.ForbidThrowCurrentWeaponArgs args) => true;

    // MUTATOR_HOOKFUNCTION(melee_only, BuildMutatorsString) — sv_melee_only.qc:44-47:
    //   M_ARGV(0, string) = strcat(M_ARGV(0, string), ":MeleeOnly");
    // The machine token for g_mutatormsg / the server-browser mutators field. Reaches the live
    // MutatorActivation.BuildMutatorsString chain (run from GameWorld GameLogInit).
    public override string BuildMutatorsString(string s) => s + ":MeleeOnly";

    // MUTATOR_HOOKFUNCTION(melee_only, BuildMutatorsPrettyString) — sv_melee_only.qc:49-52:
    //   M_ARGV(0, string) = strcat(M_ARGV(0, string), ", Melee only Arena");
    // The human-readable "modifications" label; the leading ", " is stripped once by the caller.
    public override string BuildMutatorsPrettyString(string s) => s + ", Melee only Arena";

    // MUTATOR_HOOKFUNCTION(melee_only, FilterItem) — strip small health/armor (the only "extra" sustain
    // melee_only leaves out so fights stay close-range). QC switches on ITEM_HealthSmall / ITEM_ArmorSmall;
    // matched here on the definition's classname or netname tag (the registry's instanceOf stand-in).
    private bool OnFilterItemDefinition(ref MutatorHooks.FilterItemDefinitionArgs args)
    {
        Entity def = args.Definition;
        if (def.ClassName is "item_health_small" or "item_armor_small")
            return true; // disallow spawning
        if (def.NetName is "health_small" or "armor_small")
            return true;
        return false;
    }
}
