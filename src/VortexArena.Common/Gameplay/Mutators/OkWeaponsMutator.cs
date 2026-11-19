// Port of common/mutators/mutator/overkill/sv_weapons.qc REGISTER_MUTATOR(ok_weapons, ...)

using System.Globalization;
using System.Text.RegularExpressions;
using VortexArena.Common.Framework;
using VortexArena.Common.Services;

namespace VortexArena.Common.Gameplay;

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
        && (StandaloneEnabled()
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

    private const string StandaloneCvar = "g_overkill_weapons";

    // tokenize_console treats a leading quote as a quoted token and // at a token boundary as a comment.
    private static readonly Regex ExpressionTokens = new(
        """//[^\r\n]*|"(?<quoted>(?:[^"\\]|\\["\\]|\\(?!["\\]))*)"?|(?<word>[^\x00 \t\r\n]+)""");
    private static readonly Regex QuotedEscapes = new("""\\(["\\])""");
    private static readonly Regex NumericPrefix = new(@"^\s*[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?");

    // This order is observable: the shorter equality operators also capture triple equality tokens.
    private static readonly string[] ComparisonOperators = [">=", "<=", ">", "<", "==", "!="];

    private static bool StandaloneEnabled()
    {
        // The shipped config supplies 0. Preserve that default when booting without the config files.
        string expression = Api.Cvars.GetString(StandaloneCvar);
        if (expression.Length == 0 && !Api.Cvars.Has(StandaloneCvar))
            return false;

        bool negated = expression.StartsWith('-');
        if (negated || expression.StartsWith('+'))
            expression = expression[1..];

        foreach (Match match in ExpressionTokens.Matches(expression))
        {
            Group quoted = match.Groups["quoted"];
            Group word = match.Groups["word"];
            if (!quoted.Success && !word.Success)
                continue;

            string token = quoted.Success ? QuotedEscapes.Replace(quoted.Value, "$1") : word.Value;
            if (!Matches(token))
                return negated;
        }
        return !negated;
    }

    private static bool Matches(string token)
    {
        foreach (string op in ComparisonOperators)
        {
            int index = token.IndexOf(op, StringComparison.Ordinal);
            if (index < 0)
                continue;

            float left = ParseNumericPrefix(Api.Cvars.GetString(token[..index]));
            float right = ParseNumericPrefix(token[(index + op.Length)..]);
            return op switch
            {
                ">=" => left >= right,
                "<=" => left <= right,
                ">" => left > right,
                "<" => left < right,
                "==" => left == right,
                _ => left != right,
            };
        }

        bool negated = token.StartsWith('!');
        string name = negated ? token[1..] : token;
        float value = ParseNumericPrefix(name);
        // QC identifies literal numbers by comparing the token to ftos(stof(token)).
        string format = value == MathF.Truncate(value) ? "F0" : "F6";
        if (!string.Equals(name, value.ToString(format, CultureInfo.InvariantCulture), StringComparison.Ordinal))
            value = ParseNumericPrefix(Api.Cvars.GetString(name));
        return (value != 0f) != negated;
    }

    private static float ParseNumericPrefix(string token)
    {
        Match prefix = NumericPrefix.Match(token);
        return prefix.Success && float.TryParse(prefix.ValueSpan, NumberStyles.Float,
            CultureInfo.InvariantCulture, out float value) ? value : 0f;
    }
}
