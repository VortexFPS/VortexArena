// Port of common/mutators/mutator/stale_move_negation/sv_stale_move_negation.qc

using System.Runtime.CompilerServices;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Stale-move Negation mutator — port of
/// common/mutators/mutator/stale_move_negation/sv_stale_move_negation.qc. Borrowed from fighting games:
/// repeatedly hitting with the SAME weapon makes that weapon weaker, while the others slowly recover, so a
/// player is rewarded for mixing up their weapon use. Enabled by the <c>g_smneg</c> cvar.
///
/// Ported faithfully: the per-attacker per-weapon "weight" array (QC <c>.float x_smneg_weight[]</c>), the
/// <c>smneg_multiplier(weight)</c> atan/tan power curve (verbatim), and the Damage_Calculate hook that scales
/// the damage AND the knockback force by the multiplier, then adds the dealt damage to the used weapon's weight
/// and decays every OTHER weapon's weight by <c>frag_damage * g_smneg_cooldown_factor</c>.
///
/// QC kept the weight array in the per-client (CS) entity's flat field namespace. Adding an Entity field is out
/// of this task's edit scope, so the per-attacker weights live in a <see cref="ConditionalWeakTable{TKey,TValue}"/>
/// keyed by the attacker entity (GC-safe; entry drops when the player is collected) — the same per-entity
/// mutator-state idea, self-contained in this file.
/// </summary>
[Mutator]
public sealed class StaleMoveNegationMutator : MutatorBase
{
    /// <summary>QC autocvar_g_smneg_bonus — allow weapons to become STRONGER than their baseline.</summary>
    public bool Bonus = true;

    /// <summary>QC autocvar_g_smneg_bonus_asymptote — damage is infinity at this bonus level.</summary>
    public float BonusAsymptote = 4f;

    /// <summary>QC autocvar_g_smneg_cooldown_factor — penalty cooldown factor (default 1/4).</summary>
    public float CooldownFactor = 0.25f;

    public StaleMoveNegationMutator() => NetName = "stale_move_negation";

    // QC: REGISTER_MUTATOR(mutator_smneg, autocvar_g_smneg).
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_smneg") != 0f;

    // Per-attacker per-weapon weight array (QC .float x_smneg_weight[REGISTRY_MAX(Weapons)]), keyed by weapon id.
    private static readonly ConditionalWeakTable<Entity, Dictionary<int, float>> _weights = new();

    private HookHandler<MutatorHooks.DamageCalculateArgs>? _onDamageCalc;

    public override void Hook()
    {
        _onDamageCalc ??= OnDamageCalculate;
        MutatorHooks.DamageCalculate.Add(_onDamageCalc);

        if (Api.Services is not null)
        {
            // QC AUTOCVAR defaults: g_smneg_bonus true, g_smneg_bonus_asymptote 4, g_smneg_cooldown_factor 1/4.
            // Base reads autocvars directly, so an explicit 0 IS honored (degenerate but faithful). Distinguish
            // "cvar unset" (keep the seeded default) from "explicitly 0" by checking the raw string first.
            Bonus = ReadBool("g_smneg_bonus", true);
            BonusAsymptote = ReadFloat("g_smneg_bonus_asymptote", BonusAsymptote);
            CooldownFactor = ReadFloat("g_smneg_cooldown_factor", CooldownFactor);
        }
    }

    public override void Unhook()
    {
        if (_onDamageCalc is not null) MutatorHooks.DamageCalculate.Remove(_onDamageCalc);
    }

    // MUTATOR_HOOKFUNCTION(mutator_smneg, BuildMutatorsString) — sv_stale_move_negation.qc:9-11:
    // M_ARGV(0, string) = strcat(M_ARGV(0, string), ":StaleMoveNegation"); — the machine/scoreboard token,
    // run via the MutatorActivation.BuildMutatorsString chain (GameWorld GameLogInit / server-browser field).
    public override string BuildMutatorsString(string s) => s + ":StaleMoveNegation";

    // MUTATOR_HOOKFUNCTION(mutator_smneg, BuildMutatorsPrettyString) — sv_stale_move_negation.qc:13-15:
    // M_ARGV(0, string) = strcat(M_ARGV(0, string), ", Stale-move negation"); — the human-readable label.
    public override string BuildMutatorsPrettyString(string s) => s + ", Stale-move negation";

    /// <summary>
    /// QC <c>smneg_multiplier(weight)</c> — ported verbatim:
    /// <code>
    /// a = g_smneg_bonus_asymptote;
    /// x = max(bonus ? (-a + .1) : 0, weight / start_health);
    /// z = (M_PI / 5) * a;
    /// f = (x > 0) ? atan(z / x) / (M_PI / 2) : tan(-(x / z)) + 1;
    /// </code>
    /// </summary>
    public float Multiplier(float weight)
    {
        float a = BonusAsymptote;
        float startHealth = StartHealth();
        float x = MathF.Max(Bonus ? (-a + 0.1f) : 0f, weight / startHealth);
        float z = (QMath.Pi / 5f) * a;
        float f = (x > 0f)
            ? (MathF.Atan(z / x) / (QMath.Pi / 2f))
            : (MathF.Tan(-(x / z)) + 1f);
        return f;
    }

    // MUTATOR_HOOKFUNCTION(mutator_smneg, Damage_Calculate)
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs args)
    {
        // QC: Weapon w = DEATH_WEAPONOF(deathtype); if (w == WEP_Null) return;
        string weaponName = DeathTypes.WeaponNetNameOf(args.DeathType);
        if (string.IsNullOrEmpty(weaponName)) return false;
        Weapon? w = Weapons.ByName(weaponName);
        if (w is null) return false;

        // QC: entity c = CS(frag_attacker); — per-attacker weights. (No attacker → nothing to negate.)
        Entity? attacker = args.Attacker;
        if (attacker is null) return false;
        Dictionary<int, float> weights = _weights.GetValue(attacker, static _ => new Dictionary<int, float>());

        int wid = w.RegistryId;
        weights.TryGetValue(wid, out float weight);

        float f = Multiplier(weight);
        // QC: frag_damage = M_ARGV(4) = f * M_ARGV(4); M_ARGV(6) = f * M_ARGV(6); // force
        float fragDamage = args.Damage = f * args.Damage;
        args.Force = f * args.Force;

        // QC: c.x_smneg_weight[w.m_id] = weight + frag_damage;
        weights[wid] = weight + fragDamage;

        // QC: restore = frag_damage * cooldown_factor; FOREACH(Weapons, it != WEP_Null && it != w, decay).
        float restore = fragDamage * CooldownFactor;
        foreach (Weapon other in Weapons.All)
        {
            if (other.RegistryId == wid) continue;
            weights.TryGetValue(other.RegistryId, out float ow);
            weights[other.RegistryId] = ow - restore;
        }
        return false;
    }

    /// <summary>QC global <c>start_health</c> — the spawn health (g_balance_health_start, default 100).</summary>
    private static float StartHealth()
    {
        if (Api.Services is null) return 100f;
        float h = Api.Cvars.GetFloat("g_balance_health_start");
        return h != 0f ? h : 100f;
    }

    private static bool ReadBool(string name, bool fallback)
    {
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name) != 0f;
    }

    // Like ReadBool: an unset cvar keeps the seeded default, but an explicit value (incl. 0) is honored,
    // matching Base reading the autocvar directly.
    private static float ReadFloat(string name, float fallback)
    {
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name);
    }
}
