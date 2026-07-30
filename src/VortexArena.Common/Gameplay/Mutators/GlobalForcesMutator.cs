// Port of common/mutators/mutator/globalforces/sv_globalforces.qc

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Global Forces mutator — port of common/mutators/mutator/globalforces/sv_globalforces.qc. Knockback no
/// longer affects only the player hit: when someone takes damage, every nearby player gets shoved by the same
/// damage force (scaled). Great chaos for explosive-heavy modes. Enabled by the <c>g_globalforces</c> cvar.
///
/// Ported faithfully: the PlayerDamage_SplitHealthArmor hook, the <c>g_globalforces</c> force scale, the
/// optional self-skip (<c>g_globalforces_noself</c>), the self knockback scale (<c>g_globalforces_self</c>) for
/// the attacker, the range gate (<c>g_globalforces_range</c>), and the per-player velocity add through the
/// momentum-clamped <c>damage_explosion_calcpush</c> (replicated here; it is private in the damage system) using
/// each player's <c>damageforcescale</c> and <c>g_balance_damagepush_speedfactor</c>.
/// </summary>
[Mutator]
public sealed class GlobalForcesMutator : MutatorBase
{
    /// <summary>QC autocvar_g_globalforces — global force scale (the enable cvar doubles as the scale).</summary>
    public float Scale = 1f;

    /// <summary>QC autocvar_g_globalforces_noself — ignore self damage (don't shove anyone on a self-hit).</summary>
    public bool NoSelf = true;

    /// <summary>QC autocvar_g_globalforces_self — knockback scale applied to the attacker themselves.</summary>
    public float SelfScale = 1f;

    /// <summary>QC autocvar_g_globalforces_range — max range of effect (0 = unlimited).</summary>
    public float Range = 1000f;

    public GlobalForcesMutator() => NetName = "globalforces";

    // QC: REGISTER_MUTATOR(mutator_globalforces, autocvar_g_globalforces).
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_globalforces") != 0f;

    private HookHandler<GameHooks.PlayerDamageArgs>? _onPlayerDamage;

    public override void Hook()
    {
        _onPlayerDamage ??= OnPlayerDamage;
        GameHooks.PlayerDamageSplitHealthArmor.Add(_onPlayerDamage);

        if (Api.Services is not null)
        {
            Scale = Api.Cvars.GetFloat("g_globalforces"); // enable cvar IS the scale (QC: damage_force * autocvar)
            NoSelf = ReadBool("g_globalforces_noself", true);
            // QC reads autocvar_g_globalforces_self / _range directly, where 0 is a meaningful value
            // (self=0 -> attacker takes no self-knockback; range=0 -> unlimited radius). Fall back to the
            // QC AUTOCVAR defaults (sv_globalforces.qc:5-6: self=1, range=1000) only when the cvar is UNSET;
            // an explicit 0 must be honored. The old `if (v != 0) ...` guard wrongly discarded a set-to-0.
            SelfScale = ReadFloat("g_globalforces_self", 1f);
            Range = ReadFloat("g_globalforces_range", 1000f);
        }
    }

    public override void Unhook()
    {
        if (_onPlayerDamage is not null) GameHooks.PlayerDamageSplitHealthArmor.Remove(_onPlayerDamage);
    }

    // MUTATOR_HOOKFUNCTION(mutator_globalforces, BuildMutatorsString) — sv_globalforces.qc:9-11
    public override string BuildMutatorsString(string s) => s + ":GlobalForces";

    // MUTATOR_HOOKFUNCTION(mutator_globalforces, BuildMutatorsPrettyString) — sv_globalforces.qc:13-15
    public override string BuildMutatorsPrettyString(string s) => s + ", Global forces";

    // QC server-side IS_PLAYER (server/utils.qh:9) is classname-only — (v).classname == "player" — with NO dead
    // test, so a just-killed body still classed "player" is shoved by the spread. Match Base: client flag only,
    // do NOT exclude on DeadState (the old DeadState==No filter was a port divergence that froze corpses).
    private static bool IsPlayer(Entity e) =>
        (e.Flags & EntFlags.Client) != 0;

    // MUTATOR_HOOKFUNCTION(mutator_globalforces, PlayerDamage_SplitHealthArmor)
    private bool OnPlayerDamage(ref GameHooks.PlayerDamageArgs args)
    {
        if (Api.Services is null) return false;

        Entity attacker = args.Attacker;
        Entity target = args.Target;

        // QC: if (autocvar_g_globalforces_noself && frag_target == frag_attacker) return;
        if (NoSelf && ReferenceEquals(target, attacker)) return false;

        // QC: vector damage_force = M_ARGV(3, vector) * autocvar_g_globalforces;
        Vector3 damageForce = args.Force * Scale;
        if (damageForce == Vector3.Zero) return false;

        float speedFactor = Api.Cvars.GetFloat("g_balance_damagepush_speedfactor");

        // QC: FOREACH_CLIENT(IS_PLAYER(it) && it != frag_target, { ... })
        foreach (Entity it in Api.Entities.FindByClass("player"))
        {
            if (ReferenceEquals(it, target) || !IsPlayer(it)) continue;

            // QC: if (range) if (vdist(it.origin - frag_target.origin, >, range)) continue;
            if (Range != 0f && Vector3.Distance(it.Origin, target.Origin) > Range) continue;

            // QC: f = (it == frag_attacker) ? autocvar_g_globalforces_self : 1;
            float f = ReferenceEquals(it, attacker) ? SelfScale : 1f;
            // QC: it.velocity += damage_explosion_calcpush(f * it.damageforcescale * damage_force, it.velocity, speedfactor);
            it.Velocity += DamageExplosionCalcPush(f * it.DamageForceScale * damageForce, it.Velocity, speedFactor);
        }
        return false;
    }

    /// <summary>
    /// QC <c>damage_explosion_calcpush(explosion_f, target_v, speedfactor)</c> (common/weapons/calculations.qc),
    /// replicated here (the damage system keeps it private): below speedfactor 1 return the raw force (the
    /// formula would cause superjumps); otherwise scale by the momentum-projection multiplier.
    /// </summary>
    private static Vector3 DamageExplosionCalcPush(Vector3 explosionForce, Vector3 targetVel, float speedFactor)
    {
        if (speedFactor < 1f) return explosionForce;
        return explosionForce * ExplosionCalcPushGetMultiplier(explosionForce * speedFactor, targetVel);
    }

    /// <summary>
    /// QC <c>explosion_calcpush_getmultiplier(explosion_v, target_v)</c>: a = explosion_v·(explosion_v -
    /// target_v); if a&lt;=0 the target moves too fast to be hit (0); else a / (explosion_v·explosion_v).
    /// </summary>
    private static float ExplosionCalcPushGetMultiplier(Vector3 explosionV, Vector3 targetV)
    {
        float a = QMath.Dot(explosionV, explosionV - targetV);
        if (a <= 0f) return 0f;
        return a / QMath.Dot(explosionV, explosionV);
    }

    private static bool ReadBool(string name, bool fallback)
    {
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name) != 0f;
    }

    // Read a float cvar, falling back to the QC AUTOCVAR default only when UNSET (empty string).
    // An explicitly-set 0 is kept distinct from "unset" and honored.
    private static float ReadFloat(string name, float fallback)
    {
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name);
    }
}
