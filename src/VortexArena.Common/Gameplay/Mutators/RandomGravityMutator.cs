// Port of common/mutators/mutator/random_gravity/sv_random_gravity.qc

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Random Gravity mutator — port of common/mutators/mutator/random_gravity/sv_random_gravity.qc
/// (Mario, inspired by Player 2). Every <c>g_random_gravity_delay</c> seconds the server rolls a new
/// <c>sv_gravity</c>: usually a low/positive value, occasionally (chance <c>g_random_gravity_negative_chance</c>)
/// a negative one, each clamped to <c>[g_random_gravity_min, g_random_gravity_max]</c>. The physics integrator
/// re-reads <c>sv_gravity</c> per tick, so the change takes effect immediately. Enabled by the
/// <c>g_random_gravity</c> cvar.
///
/// Ported: the per-frame roll on SV_StartFrame (gated by game-stopped + game_starttime + the round-start gate +
/// the delay schedule), the exact <c>cvar_set("sv_gravity", ...)</c> formula. The round-start gate reads the host-
/// wired <see cref="RoundHandler.RoundNotStartedProvider"/> seam; QC's <c>cvar_settemp</c> on enable (so the
/// original gravity is restored at match end) routes through <see cref="MutatorActivation.SettempCvar"/> into the
/// host's settemp restore stack.
///
/// SEAM: this hooks <see cref="MutatorHooks.SvStartFrame"/> (the Common-side per-frame chain) rather than the
/// server-core <c>ServerHooks.SvStartFrame</c>, because the Godot-free gameplay layer can't reference XonoticGodot.Server.
/// The server's per-frame loop must pump <see cref="MutatorHooks.SvStartFrame"/> for this to tick (crossTaskNeeds).
/// </summary>
[Mutator]
public sealed class RandomGravityMutator : MutatorBase
{
    public RandomGravityMutator() => NetName = "random_gravity";

    // QC: REGISTER_MUTATOR(random_gravity, cvar("g_random_gravity")).
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_random_gravity") != 0f;

    /// <summary>QC file-global <c>gravity_delay</c> — next sim time a re-roll is allowed.</summary>
    private float _gravityDelay;

    private HookHandler<MutatorHooks.SvStartFrameArgs>? _onStartFrame;

    public override void Hook()
    {
        _onStartFrame ??= OnStartFrame;
        MutatorHooks.SvStartFrame.Add(_onStartFrame);
        _gravityDelay = 0f;
        // QC MUTATOR_ONADD: cvar_settemp("sv_gravity", cvar_string("sv_gravity")) — push the current sv_gravity
        // onto the host's restore stack so the original gravity is restored at match end (this mutator mutates
        // sv_gravity destructively each roll). MutatorActivation.SettempCvar routes through the server's
        // SettempCvars stack when a host is wired; headless it falls back to a plain Set (no-op restore target).
        if (Api.Services is not null)
            MutatorActivation.SettempCvar("sv_gravity", Api.Cvars.GetString("sv_gravity"));
    }

    public override void Unhook()
    {
        if (_onStartFrame is not null) MutatorHooks.SvStartFrame.Remove(_onStartFrame);
    }

    // MUTATOR_HOOKFUNCTION(random_gravity, SV_StartFrame)
    private bool OnStartFrame(ref MutatorHooks.SvStartFrameArgs args)
    {
        if (Api.Services is null) return false;

        // QC: if(game_stopped || !cvar("g_random_gravity")) return false;
        if (VehicleCommon.GameStopped || Api.Cvars.GetFloat("g_random_gravity") == 0f) return false;
        float time = args.Time;
        // QC: if(time < gravity_delay) return false;
        if (time < _gravityDelay) return false;
        // QC: if(time < game_starttime) return false; — suppress re-rolls during the pre-match warmup/countdown.
        // StartItem.GameStartTimeProvider is the host-wired countdown-end seam (same one DamageSystem/Domination use);
        // unwired (headless tests) it reads 0, so the gate is a no-op there — matches "match already live".
        if (time < (StartItem.GameStartTimeProvider?.Invoke() ?? 0f)) return false;
        // QC: if(round_handler_IsActive() && !round_handler_IsRoundStarted()) return false; — in round modes
        // (CA/Freezetag/round Domination/…) do NOT roll gravity in the inter-round pre-start grace window. The
        // per-gametype RoundHandler isn't reachable from Common, so the host wires RoundHandler.RoundNotStartedProvider
        // (in GameWorld.EnableRounds) to the live handler; RoundGateBlocks() is false in non-round modes / headless.
        if (RoundHandler.RoundGateBlocks()) return false;

        float min = Api.Cvars.GetFloat("g_random_gravity_min");
        float max = Api.Cvars.GetFloat("g_random_gravity_max");
        float negativeChance = Api.Cvars.GetFloat("g_random_gravity_negative_chance");

        float gravity;
        if (Prandom.Float() >= negativeChance)
        {
            // QC: bound(min, random() - random() * -g_random_gravity_negative, max)
            float negative = Api.Cvars.GetFloat("g_random_gravity_negative");
            gravity = QMath.Bound(min, Prandom.Float() - Prandom.Float() * -negative, max);
        }
        else
        {
            // QC: bound(min, random() * g_random_gravity_positive, max)
            float positive = Api.Cvars.GetFloat("g_random_gravity_positive");
            gravity = QMath.Bound(min, Prandom.Float() * positive, max);
        }

        Api.Cvars.Set("sv_gravity", Ftos(gravity));

        // QC: gravity_delay = time + autocvar_g_random_gravity_delay;
        _gravityDelay = time + Api.Cvars.GetFloat("g_random_gravity_delay");
        return false;
    }

    /// <summary>QC <c>ftos</c> — float to its cvar string. Invariant culture so the cvar store parses it back.</summary>
    private static string Ftos(float v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // MUTATOR_HOOKFUNCTION(random_gravity, BuildMutatorsString) — sv_random_gravity.qc:42-45: append the
    // machine-readable ":RandomGravity" token to the server-info / gamelog mutator list.
    public override string BuildMutatorsString(string s) => s + ":RandomGravity";

    // MUTATOR_HOOKFUNCTION(random_gravity, BuildMutatorsPrettyString) — sv_random_gravity.qc:47-50: append the
    // human-readable ", Random gravity" token to the scoreboard / votescreen mutator list.
    public override string BuildMutatorsPrettyString(string s) => s + ", Random gravity";
}
