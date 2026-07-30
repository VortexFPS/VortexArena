// Port of common/mutators/mutator/overkill/sv_weapons.qc REGISTER_MUTATOR(ok_weapons, ...)

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Overkill-weapons availability mutator — port of common/mutators/mutator/overkill/sv_weapons.qc's
/// <c>REGISTER_MUTATOR(ok_weapons, expr_evaluate(autocvar_g_overkill_weapons) || MUTATOR_IS_ENABLED(ok))</c>.
/// This is a SEPARATE, lightweight mutator from the full Overkill mode (<see cref="OverkillMutator"/>, the QC
/// <c>ok</c> mutator): its ONLY job is to un-block the five Overkill weapons so they may spawn from map
/// <c>weapon_*</c> entities / be added to an otherwise-normal game's weapon pool. It activates whenever EITHER
/// the standalone <c>g_overkill_weapons</c> string cvar evaluates true (a server adding the OK guns to a normal
/// game) OR the full Overkill mode is enabled (so the loadout/loot pipeline's OK weapons are givable too).
///
/// QC MUTATOR_ONADD: <c>WEP_OVERKILL_*.spawnflags &amp;= ~WEP_FLAG_MUTATORBLOCKED</c> on each of the five OK
/// weapons (shotgun/machinegun/nex/hmg/rpc); QC MUTATOR_ONREMOVE OR's the flag back. Modeled on
/// <see cref="NewToysMutator"/>'s identical weapon-unblock pattern. The activation/teardown is driven generically
/// by <see cref="MutatorActivation.Apply"/> (it calls <see cref="Hook"/> when <see cref="IsEnabled"/> first holds
/// and <see cref="Unhook"/> when it stops), so this row is LIVE the moment the cvar/overkill predicate is
/// satisfied — no extra wiring needed.
/// </summary>
[Mutator]
public sealed class OkWeaponsMutator : MutatorBase
{
    public OkWeaponsMutator() => NetName = "ok_weapons";

    /// <summary>QC the five Overkill weapons this mutator un-blocks (by NetName — the m_id stand-in).</summary>
    private static readonly HashSet<string> OkWeaponNames = new(StringComparer.Ordinal)
    {
        "okmachinegun", "okhmg", "oknex", "okrpc", "okshotgun",
    };

    // QC: REGISTER_MUTATOR(ok_weapons, expr_evaluate(autocvar_g_overkill_weapons) || MUTATOR_IS_ENABLED(ok)).
    // g_overkill_weapons is a STRING cvar evaluated with expr_evaluate; MUTATOR_IS_ENABLED(ok) reads the full
    // Overkill mutator's enable predicate (so the standalone weapons are also available in full Overkill mode).
    public override bool IsEnabled =>
        Api.Services is not null
        && (ExprEvaluate(Api.Cvars.GetString("g_overkill_weapons"))
            || Mutators.ByName("overkill") is { IsEnabled: true });

    public override void Hook()
    {
        // QC MUTATOR_ONADD: clear WEP_FLAG_MUTATORBLOCKED on the five OK weapons so they can spawn / be given.
        SetOkBlocked(false);
    }

    public override void Unhook()
    {
        // QC MUTATOR_ONREMOVE: re-block the OK weapons (OR WEP_FLAG_MUTATORBLOCKED back on).
        SetOkBlocked(true);
    }

    /// <summary>
    /// QC MUTATOR_ONADD/ONREMOVE: <c>WEP_OVERKILL_*.spawnflags (&amp;= ~ | |=) WEP_FLAG_MUTATORBLOCKED</c> on the
    /// five OK weapons. Sets or clears <see cref="WeaponFlags.MutatorBlocked"/> so they can / can't be given.
    /// </summary>
    private static void SetOkBlocked(bool blocked)
    {
        foreach (Weapon w in Weapons.All)
        {
            if (!OkWeaponNames.Contains(w.NetName)) continue;
            if (blocked) w.SpawnFlags |= WeaponFlags.MutatorBlocked;
            else w.SpawnFlags &= ~WeaponFlags.MutatorBlocked;
        }
    }

    /// <summary>QC <c>expr_evaluate(s)</c> for a cvar string: false for "" / "0" / "false", true otherwise.</summary>
    private static bool ExprEvaluate(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        s = s.Trim();
        if (s == "0" || string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
