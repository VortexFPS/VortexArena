// Port of server/weapons/accuracy.qc (the logic half: accuracy_add / accuracy_isgooddamage /
// accuracy_canbegooddamage / accuracy_byte).
//
// QC keeps the per-weapon tallies on a per-client `accuracy` sub-entity owned by the server scoring layer;
// this port keeps the TALLIES in XonoticGodot.Server.Scores (which subscribes to this bus) and only the
// pure logic here, so the weapons folder stays Godot/Server-free. With no subscriber (GameDemo, bare unit
// tests) every Add is a no-op, exactly like QC with no accuracy entity attached.

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Scoring;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The accuracy event bus — the C# seam for QC <c>accuracy_add(this, w, fired, hit, real)</c>
/// (server/weapons/accuracy.qc:102) plus the <c>accuracy_isgooddamage</c>/<c>accuracy_canbegooddamage</c>
/// gates every credit site evaluates. Weapons publish; <c>XonoticGodot.Server.Scores</c> subscribes.
/// </summary>
public static class WeaponAccuracyEvents
{
    /// <summary>One accuracy_add: <c>fired</c> = potential damage, <c>hit</c> = damage dealt (may exceed
    /// fired), <c>real</c> = post-excess damage (PlayerDamage).</summary>
    public delegate void AccuracyAddHandler(Entity attacker, Weapon weapon, float fired, float hit, float real);

    /// <summary>Subscribers receive every non-empty accuracy_add (QC writes the accuracy sub-entity here).</summary>
    public static event AccuracyAddHandler? Added;

    /// <summary>
    /// QC <c>attacker.score_frame_dmg += realdmg</c> (server/player.qc:443) — the per-frame damage-dealt
    /// score column feed. Separate from <see cref="Added"/> because QC applies it regardless of
    /// accuracy_isgooddamage (only the DIFF_TEAM/player gates apply).
    /// </summary>
    public static event System.Action<Entity, float>? DamageDealtScored;

    /// <summary>QC <c>this.score_frame_dmgtaken += realdmg</c> (server/player.qc:446).</summary>
    public static event System.Action<Entity, float>? DamageTakenScored;

    /// <summary>
    /// Port of <c>accuracy_add</c>'s caller-side guards (accuracy.qc:104-108): independent players, the Null
    /// weapon and all-zero adds publish nothing. The once-per-frame cnt guards + the SendFlags change
    /// detection live with the tallies (Scores), which has the rows.
    /// </summary>
    public static void Add(Entity? attacker, Weapon? weapon, float fired, float hit, float real)
    {
        if (attacker is null || weapon is null) return;          // w == WEP_Null
        if (attacker.IsIndependentPlayer) return;                 // IS_INDEPENDENT_PLAYER(this)
        if (fired == 0f && hit == 0f && real == 0f) return;       // !hit && !fired && !real
        Added?.Invoke(attacker, weapon, fired, hit, real);
    }

    /// <summary>accuracy_add(ent, wep, maxdamage, 0, 0) — the W_SetupShot fired credit (tracing.qc:66).</summary>
    public static void Fired(Entity attacker, Weapon weapon, float maxDamage)
        => Add(attacker, weapon, maxDamage, 0f, 0f);

    /// <summary>accuracy_add(ent, wep, 0, damage, 0) — a hit credit (tracing.qc:348/476, damage.qc:929).</summary>
    public static void Hit(Entity attacker, Weapon? weapon, float damage)
        => Add(attacker, weapon, 0f, damage, 0f);

    /// <summary>accuracy_add(attacker, awep, 0, 0, realdmg) — the PlayerDamage real credit (player.qc:442).</summary>
    public static void Real(Entity attacker, Weapon weapon, float realDamage)
        => Add(attacker, weapon, 0f, 0f, realDamage);

    /// <summary>Publish the per-frame damage-dealt score feed (player.qc:443).</summary>
    public static void ScoreFrameDamage(Entity attacker, float realDamage)
        => DamageDealtScored?.Invoke(attacker, realDamage);

    /// <summary>Publish the per-frame damage-taken score feed (player.qc:446).</summary>
    public static void ScoreFrameDamageTaken(Entity victim, float realDamage)
        => DamageTakenScored?.Invoke(victim, realDamage);

    /// <summary>
    /// Port of QC's <c>AccuracyTargetValid</c> mutator-hook return enum (server/mutators/events.qh): a gametype
    /// can override whether a given attacker→target hit counts toward weapon-accuracy stats. The default (no
    /// provider) is <see cref="Valid"/>, i.e. the normal "both sides must be clients" rule.
    /// </summary>
    public enum AccuracyTarget
    {
        /// <summary>MUT_ACCADD_VALID: continue if the target is a client (the default rule).</summary>
        Valid = 0,
        /// <summary>MUT_ACCADD_INVALID: always count the hit (used by Invasion so monster hits count).</summary>
        Invalid = 1,
        /// <summary>MUT_ACCADD_INDIFFERENT: never count the hit.</summary>
        Indifferent = 2,
    }

    /// <summary>
    /// QC <c>MUTATOR_CALLHOOK(AccuracyTargetValid, attacker, targ)</c>: a gametype-installed classifier consulted
    /// by <see cref="IsGoodDamage"/>. Null = no hook installed (= MUT_ACCADD_VALID, the default client-vs-client
    /// rule). Invasion installs one that returns <see cref="AccuracyTarget.Invalid"/> for monster targets so hits
    /// on monsters DO count toward accuracy (without it, a non-client monster would fail the IS_CLIENT gate).
    /// </summary>
    public static System.Func<Entity, Entity, AccuracyTarget>? TargetValidProvider;

    /// <summary>Drop every subscriber (test isolation — the bus is process-global).</summary>
    public static void Reset()
    {
        Added = null;
        DamageDealtScored = null;
        DamageTakenScored = null;
        TargetValidProvider = null;
    }

    // =================================================================================================
    //  the gates (accuracy.qc:133-157)
    // =================================================================================================

    /// <summary>
    /// Port of <c>accuracy_isgooddamage(attacker, targ)</c> (accuracy.qc:133-150): no credit during
    /// warmup/game-stopped, against the dead, against teammates (QC SAME_TEAM — identity in FFA, so
    /// self-damage never counts), or when either side isn't a client.
    ///
    /// <para>DEVIATION (documented invariant): QC allows damage to a dead target "in the frame it got
    /// killed" (<c>IS_DEAD(targ) &amp;&amp; time &gt; targ.death_time</c>) so multi-pellet kill shots count fully.
    /// The port has no <c>death_time</c> field — but EVERY credit call site (tracing.qc:323/446,
    /// damage.qc:913, player.qc:441) evaluates this gate BEFORE calling Damage(), when the target is still
    /// alive, so the simple "not dead" test below is behaviourally identical. Any future credit site that
    /// evaluates AFTER damage must add the death-frame rule first.</para>
    ///
    /// <para>The <see cref="TargetValidProvider"/> seam is the port of the AccuracyTargetValid mutator chain:
    /// a null provider is MUT_ACCADD_VALID (the default IS_CLIENT path); Invasion installs one returning
    /// MUT_ACCADD_INVALID for monster targets so monster hits count.</para>
    /// </summary>
    public static bool IsGoodDamage(Entity? attacker, Entity? targ)
    {
        if (attacker is null || targ is null) return false;

        // QC accuracy_isgooddamage: int mutator_check = MUTATOR_CALLHOOK(AccuracyTargetValid, attacker, targ);
        AccuracyTarget mutatorCheck = TargetValidProvider?.Invoke(attacker, targ) ?? AccuracyTarget.Valid;

        if (NotificationSystem.WarmupStage || GameScores.GameStopped) return false;
        if (targ.DeadState != DeadFlag.No || targ.IsCorpse) return false; // pre-damage call-site invariant
        if (SameTeam(attacker, targ)) return false;

        if (mutatorCheck == AccuracyTarget.Invalid) return true;          // QC: always count
        if (mutatorCheck != AccuracyTarget.Valid) return false;           // QC: MUT_ACCADD_INDIFFERENT → never count
        if ((targ.Flags & EntFlags.Client) == 0 || (attacker.Flags & EntFlags.Client) == 0) return false;
        return true;
    }

    /// <summary>
    /// Port of <c>accuracy_canbegooddamage(attacker)</c> (accuracy.qc:154-157): could potential damage
    /// count? Gates the fired credit so warmup shots don't inflate the denominator.
    /// </summary>
    public static bool CanBeGoodDamage(Entity? attacker)
        => attacker is not null
        && !NotificationSystem.WarmupStage
        && (attacker.Flags & EntFlags.Client) != 0;

    /// <summary>
    /// QC <c>SAME_TEAM(a, b)</c> (common/teams.qh): in teamplay equal teams; in FFA the SAME entity —
    /// which is what makes self-damage (rocket jumps) never count toward accuracy.
    /// </summary>
    public static bool SameTeam(Entity a, Entity b)
        => GameScores.Teamplay ? a.Team == b.Team : ReferenceEquals(a, b);

    /// <summary>
    /// Port of <c>accuracy_byte(n, d)</c> (accuracy.qc:17-24): 0 = never fired; 255 = &gt;100% (hit damage
    /// exceeded fired damage, e.g. one Vortex beam through two players); else <c>1 + rint(n*100/d)</c>
    /// (1..101 = accuracy% + 1). DP's <c>rint</c> builtin rounds half AWAY FROM ZERO (round-half-up here, since
    /// the argument <c>n*100/d</c> is always &gt;= 0), NOT round-half-to-even — so use
    /// <see cref="MidpointRounding.AwayFromZero"/>, otherwise e.g. 50.5% accuracy would tie-break to 50 instead
    /// of QC's 51.
    /// </summary>
    public static int AccuracyByte(float n, float d)
    {
        if (n < 0f || d <= 0f) return 0;
        if (n > d) return 255;
        return 1 + (int)System.MathF.Round(n * 100f / d, System.MidpointRounding.AwayFromZero);
    }
}
