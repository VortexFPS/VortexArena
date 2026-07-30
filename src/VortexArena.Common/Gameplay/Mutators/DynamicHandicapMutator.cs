// Port of common/mutators/mutator/dynamic_handicap/sv_dynamic_handicap.qc

using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Dynamic Handicap mutator — port of common/mutators/mutator/dynamic_handicap/sv_dynamic_handicap.qc.
/// Auto-balances the match by giving stronger players (above the mean score) a damage penalty and weaker
/// players a buff, recomputed whenever scores or the roster change. Enabled by <c>g_dynamic_handicap</c>
/// (when handicap isn't otherwise disabled).
///
/// FULLY PORTED: <c>DynamicHandicap_UpdateHandicap</c> — compute the mean SP_SCORE across players, then for
/// each player derive <c>handicap = |(score - mean) * scale| ^ exponent</c>, sign it by whether the player is
/// below the mean, fold it to a multiplier (<c>handicap &gt;= 0 → handicap + 1</c>; else
/// <c>1 / (|handicap| + 1)</c>), clamp to [min, max], and apply it. The application side already exists in the
/// port (DamageSystem reads <see cref="Entity.HandicapTake"/>/<see cref="Entity.HandicapGive"/>); QC's
/// <c>Handicap_SetForcedHandicap(it, h, take?)</c> maps to setting both, so a stronger player both deals less
/// and takes more. Scores are read via <see cref="GameScores"/> (SP_SCORE).
///
/// Recompute triggers: QC fires DynamicHandicap_UpdateHandicap on FOUR functional hooks — PutClientInServer,
/// ClientDisconnect, MakePlayerObserver and AddedPlayerScore(SP_SCORE). All four are now ported: PlayerSpawn
/// (≈ PutClientInServer), <see cref="MutatorHooks.ClientDisconnect"/> and <see cref="MutatorHooks.MakePlayerObserver"/>
/// (roster shrink), and the POST-write <see cref="GameScores.AddedPlayerScoreHook"/> filtered to SP_SCORE (every
/// cap/objective/KH/Dom/CTF point, not just frags). PlayerDies additionally recomputes a kill's frag delta
/// immediately. So the handicap now tracks the live score/roster the instant it changes, matching Base.
///
/// Also ported: <c>Handicap_SetForcedHandicap</c>'s value&lt;=0 error guard, <c>Handicap_UpdateHandicapLevel</c>
/// (computes <see cref="Entity.HandicapLevel"/> — now networked end-to-end: the server stamps it on each score
/// row in <c>ScoreboardBlock.CaptureRows</c> [the C# stand-in for QC's ENTCS <c>.handicap_level</c> slice,
/// common/ent_cs.qc:180], and the client scoreboard draws the <c>player_handicap</c> icon tinted by it,
/// scoreboard.qc:1003-1009), and the BuildMutatorsString / BuildMutatorsPrettyString mutator-list tokens
/// (<c>:handicap</c> / <c>, Dynamic handicap</c>). The <c>:handicap</c> machine token is live via the gamelog
/// init (GameWorld); the <c>, Dynamic handicap</c> pretty token has no consumer yet — the port hasn't ported
/// QC's CSQC server-info "modifications" line (server/client.qc:1107 SendServerInfo), the sole caller of the
/// BuildMutatorsPrettyString chain in Base.
/// </summary>
[Mutator]
public sealed class DynamicHandicapMutator : MutatorBase
{
    public float Scale = 1f;     // g_dynamic_handicap_scale
    public float Exponent = 1f;  // g_dynamic_handicap_exponent
    public float Min;            // g_dynamic_handicap_min
    public float Max;            // g_dynamic_handicap_max

    public DynamicHandicapMutator() => NetName = "dynamic_handicap";

    /// <summary>
    /// QC <c>HANDICAP_DISABLED()</c> (server/handicap.qh:57) == <c>IS_GAMETYPE(CTS) || IS_GAMETYPE(RACE)</c>.
    /// The port selects the gametype as a C# object (never stamps a <c>g_cts</c>/<c>g_race</c> cvar), so this
    /// must be wired from the server side to <c>GameType is Cts or Race</c> — the same provider-seam pattern as
    /// <see cref="Items.ItemPickupRules.CtsActiveProvider"/>. Null provider = false (the common non-CTS/RACE case).
    /// Forwards to the shared <see cref="Handicap.DisabledProvider"/> so the whole forced-handicap subsystem
    /// (Handicap_Initialize / SetForcedHandicap / UpdateHandicapLevel) shares a single CTS/RACE gate.
    /// </summary>
    public static System.Func<bool>? HandicapDisabledProvider
    {
        get => Handicap.DisabledProvider;
        set => Handicap.DisabledProvider = value;
    }

    // QC: REGISTER_MUTATOR(dynamic_handicap, autocvar_g_dynamic_handicap && !HANDICAP_DISABLED());
    // HANDICAP_DISABLED() (server/handicap.qh:57) == (IS_GAMETYPE(CTS) || IS_GAMETYPE(RACE)) — in CTS/RACE the
    // whole handicap subsystem (and so this mutator) is hard-disabled.
    public override bool IsEnabled =>
        Api.Services is not null
        && Api.Cvars.GetFloat("g_dynamic_handicap") != 0f
        && !HandicapDisabled;

    // QC HANDICAP_DISABLED(): CTS or RACE gametype. Detected via the server-wired provider (GameType is Cts/Race),
    // NOT the bare g_cts/g_race cvars — the port never sets those, so reading them would leave this guard dead.
    private static bool HandicapDisabled => Handicap.Disabled;

    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _onSpawn;
    private HookHandler<MutatorHooks.PlayerDiesArgs>? _onDies;
    private HookHandler<MutatorHooks.MakePlayerObserverArgs>? _onObserver;
    private HookHandler<MutatorHooks.ClientDisconnectArgs>? _onDisconnect;
    private System.Action<Entity, ScoreField, int>? _onScore;

    public override void Hook()
    {
        // QC dynamic_handicap fires DynamicHandicap_UpdateHandicap from FOUR functional hooks:
        //   PutClientInServer (~PlayerSpawn), ClientDisconnect, MakePlayerObserver, AddedPlayerScore(SP_SCORE).
        // PlayerDies additionally covers a kill's frag-score change immediately (a kill is a death event).
        _onSpawn ??= OnPlayerSpawn;
        _onDies ??= OnPlayerDies;
        _onObserver ??= OnMakePlayerObserver;
        _onDisconnect ??= OnClientDisconnect;
        _onScore ??= OnAddedPlayerScore;
        MutatorHooks.PlayerSpawn.Add(_onSpawn);
        MutatorHooks.PlayerDies.Add(_onDies);
        MutatorHooks.MakePlayerObserver.Add(_onObserver);
        MutatorHooks.ClientDisconnect.Add(_onDisconnect);
        GameScores.AddedPlayerScoreHook += _onScore;

        if (Api.Services is not null)
        {
            Scale = Api.Cvars.GetFloat("g_dynamic_handicap_scale");
            Exponent = Api.Cvars.GetFloat("g_dynamic_handicap_exponent");
            Min = Api.Cvars.GetFloat("g_dynamic_handicap_min");
            Max = Api.Cvars.GetFloat("g_dynamic_handicap_max");
        }
    }

    public override void Unhook()
    {
        if (_onSpawn is not null) MutatorHooks.PlayerSpawn.Remove(_onSpawn);
        if (_onDies is not null) MutatorHooks.PlayerDies.Remove(_onDies);
        if (_onObserver is not null) MutatorHooks.MakePlayerObserver.Remove(_onObserver);
        if (_onDisconnect is not null) MutatorHooks.ClientDisconnect.Remove(_onDisconnect);
        if (_onScore is not null) GameScores.AddedPlayerScoreHook -= _onScore;
    }

    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args) { UpdateHandicap(); return false; }
    private bool OnPlayerDies(ref MutatorHooks.PlayerDiesArgs args) { UpdateHandicap(); return false; }
    private bool OnMakePlayerObserver(ref MutatorHooks.MakePlayerObserverArgs args) { UpdateHandicap(); return false; }
    private bool OnClientDisconnect(ref MutatorHooks.ClientDisconnectArgs args) { UpdateHandicap(); return false; }

    // QC MUTATOR_HOOKFUNCTION(dynamic_handicap, AddedPlayerScore): recompute ONLY when the changed field is
    // SP_SCORE (sv_dynamic_handicap.qc:113-120: `if (M_ARGV(0, entity) != SP_SCORE) return;`).
    private void OnAddedPlayerScore(Entity player, ScoreField field, int delta)
    {
        if (!ReferenceEquals(field, GameScores.Score)) return;
        UpdateHandicap();
    }

    // QC BuildMutatorsString (sv_dynamic_handicap.qc:88-91): advertise the active mutator in the server's
    // machine-readable mutator list (server browser / gamelog).
    public override string BuildMutatorsString(string s) => s + ":handicap";

    // QC BuildMutatorsPrettyString (sv_dynamic_handicap.qc:93-96): the human-readable scoreboard mutator line.
    public override string BuildMutatorsPrettyString(string s) => s + ", Dynamic handicap";

    // void DynamicHandicap_UpdateHandicap()
    public void UpdateHandicap()
    {
        if (Api.Services is null) return;

        // QC mean: FOREACH_CLIENT(IS_PLAYER(it)) — only LIVE players (excludes observers/spectators). In QC an
        // observing client is transmuted to classname "observer" so IS_PLAYER (classname == "player") skips it;
        // this port keeps the single Player edict and marks the observer phase with IsObserver, so that flag is
        // the IS_PLAYER discriminator here (a non-Player client edict is treated as a player, like a bare flag).
        float totalScore = 0f;
        int totalPlayers = 0;
        foreach (Entity it in Api.Entities.FindByClass("player"))
        {
            if (it.IsFreed || (it.Flags & EntFlags.Client) == 0) continue;
            if (it is Player ply && ply.IsObserver) continue;
            totalScore += GameScores.Get(it, GameScores.Score);
            ++totalPlayers;
        }
        if (totalPlayers == 0) return;

        // QC application: FOREACH_CLIENT(true) — EVERY client incl. spectators/observers, so a spectator's stale
        // forced handicap is clamped back too (it iterates the whole roster, not just IS_PLAYER). In this port
        // an observer keeps classname "player" + FL_CLIENT, so FindByClass("player") already covers all clients.
        float meanScore = totalScore / totalPlayers;
        foreach (Entity it in Api.Entities.FindByClass("player"))
        {
            if (it.IsFreed || (it.Flags & EntFlags.Client) == 0) continue;
            float score = GameScores.Get(it, GameScores.Score);
            float handicap = MathF.Abs((score - meanScore) * Scale);
            handicap = MathF.Pow(handicap, Exponent);
            if (score < meanScore) handicap = -handicap;
            if (handicap >= 0f) ++handicap;
            else handicap = 1f / (MathF.Abs(handicap) + 1f);
            handicap = ClampHandicap(handicap);
            // QC Handicap_SetForcedHandicap(it, h, false) then (it, h, true): the give (deal-less) and take
            // (receive-more) multipliers. DamageSystem reads both (HandicapTotal). Each call guards value<=0 and
            // tails into Handicap_UpdateHandicapLevel — now via the shared Handicap subsystem (server/handicap.qc).
            Handicap.SetForcedHandicap(it, handicap, false);
            Handicap.SetForcedHandicap(it, handicap, true);
        }
    }

    // float DynamicHandicap_ClampHandicap(float handicap)
    private float ClampHandicap(float handicap)
    {
        if (Min >= 0f && handicap < Min) handicap = Min;
        if (Max > 0f && handicap > Max) handicap = Max;
        return handicap;
    }
}
