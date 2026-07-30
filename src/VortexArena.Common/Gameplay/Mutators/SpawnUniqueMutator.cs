// Port of common/mutators/mutator/spawn_unique/sv_spawn_unique.qc

using System.Runtime.CompilerServices;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Unique Spawn mutator — port of common/mutators/mutator/spawn_unique/sv_spawn_unique.qc. Stops a player
/// from respawning on the exact spawnpoint they last used: while scoring spawn spots, the player's previous
/// spawnpoint gets an extremely low (but still selectable) priority, so they only land on it again when nothing
/// else is available. Enabled by the <c>g_spawn_unique</c> cvar.
///
/// Ported faithfully: the Spawn_Score hook (demote the repeat spot to priority 0.1) and the PlayerSpawn hook
/// (record the spot the player just spawned on). QC kept <c>.su_last_point</c> on the player edict; adding an
/// Entity field is out of this task's edit scope, so the per-player last-spot is held in a
/// <see cref="ConditionalWeakTable{TKey,TValue}"/> keyed by the player entity (GC-safe; the entry drops when the
/// player is collected) — the same per-entity mutator-state idea the stale_move_negation / vampirehook ports use.
/// </summary>
[Mutator]
public sealed class SpawnUniqueMutator : MutatorBase
{
    public SpawnUniqueMutator() => NetName = "spawn_unique";

    // QC: REGISTER_MUTATOR(spawn_unique, expr_evaluate(autocvar_g_spawn_unique)).
    public override bool IsEnabled =>
        Api.Services is not null && ExprEvaluate(Api.Cvars.GetString("g_spawn_unique"));

    // Per-player last spawnpoint (QC .entity su_last_point). Box the entity so it can be a CWT value.
    private static readonly ConditionalWeakTable<Entity, StrongRef> _lastPoint = new();

    private sealed class StrongRef { public Entity? Value; }

    private HookHandler<MutatorHooks.SpawnScoreArgs>? _onSpawnScore;
    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _onPlayerSpawn;

    public override void Hook()
    {
        _onSpawnScore ??= OnSpawnScore;
        _onPlayerSpawn ??= OnPlayerSpawn;
        MutatorHooks.SpawnScore.Add(_onSpawnScore);
        MutatorHooks.PlayerSpawn.Add(_onPlayerSpawn);
    }

    public override void Unhook()
    {
        if (_onSpawnScore is not null) MutatorHooks.SpawnScore.Remove(_onSpawnScore);
        if (_onPlayerSpawn is not null) MutatorHooks.PlayerSpawn.Remove(_onPlayerSpawn);
    }

    // MUTATOR_HOOKFUNCTION(spawn_unique, Spawn_Score)
    private bool OnSpawnScore(ref MutatorHooks.SpawnScoreArgs args)
    {
        // QC: if(spawn_spot == player.su_last_point) spawn_score.x = 0.1;
        if (_lastPoint.TryGetValue(args.Player, out StrongRef? r) && ReferenceEquals(r.Value, args.Spot))
            args.Priority = 0.1f; // extremely low priority but still selectable
        return false;
    }

    // MUTATOR_HOOKFUNCTION(spawn_unique, PlayerSpawn)
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args)
    {
        // QC: player.su_last_point = spawn_spot;
        StrongRef r = _lastPoint.GetValue(args.Player, static _ => new StrongRef());
        r.Value = args.Spot;
        return false;
    }

    /// <summary>
    /// Faithful port of QC <c>expr_evaluate(s)</c> (lib/cvar.qh:48). A mini-expression evaluator over a
    /// space-tokenized string: a leading '+' is stripped, a leading '-' negates the final result. Each token is
    /// either a binop (<c>cvar &gt;=/&lt;=/&gt;/&lt;/==/!=</c> against a number, or <c>cvar_string ===/!==</c>
    /// against a literal) or a bare term (optional <c>!</c> negation, then a literal number or cvar truthiness).
    /// An empty string tokenizes to zero terms and evaluates to TRUE (matching Base); any failing token makes the
    /// whole expression false (modulo the leading-sign negation). Used for the <c>g_spawn_unique</c> enable cvar.
    /// </summary>
    private static bool ExprEvaluate(string s)
    {
        s ??= string.Empty;

        // QC: leading '+' strip; leading '-' sets ret=true and strips.
        bool ret = false;
        if (s.Length > 0 && s[0] == '+')
            s = s.Substring(1);
        else if (s.Length > 0 && s[0] == '-')
        {
            ret = true;
            s = s.Substring(1);
        }

        bool exprFail = false;
        string[] tokens = TokenizeConsole(s);
        foreach (string tokRaw in tokens)
        {
            string tok = tokRaw;
            bool ok;
            // BINOP order matches Base exactly (strstrofs, first match wins). Note '==' is tested before
            // '===' / '!==' just as in QC, so a '===' literal is caught by the '==' branch first — a faithful quirk.
            if (TryBinop(tok, ">=", out string k, out string v)) ok = Cvar(k) >= Stof(v);
            else if (TryBinop(tok, "<=", out k, out v)) ok = Cvar(k) <= Stof(v);
            else if (TryBinop(tok, ">", out k, out v)) ok = Cvar(k) > Stof(v);
            else if (TryBinop(tok, "<", out k, out v)) ok = Cvar(k) < Stof(v);
            else if (TryBinop(tok, "==", out k, out v)) ok = Cvar(k) == Stof(v);
            else if (TryBinop(tok, "!=", out k, out v)) ok = Cvar(k) != Stof(v);
            else if (TryBinop(tok, "===", out k, out v)) ok = CvarString(k) == v;
            else if (TryBinop(tok, "!==", out k, out v)) ok = CvarString(k) != v;
            else
            {
                // Bare term: optional '!' negation, then numeric-literal-or-cvar truthiness.
                k = tok;
                bool b = true;
                if (k.Length > 0 && k[0] == '!')
                {
                    k = k.Substring(1);
                    b = false;
                }
                float f = Stof(k);
                // QC: boolean((ftos(f) == k) ? f : cvar(k)) == b — if k is a plain number use it, else look it up.
                float val = Ftos(f) == k ? f : Cvar(k);
                ok = (val != 0f) == b;
            }

            if (!ok)
            {
                exprFail = true;
                break;
            }
        }

        if (!exprFail)
            ret = !ret;
        return ret;
    }

    // QC strstrofs-based BINOP: find op in s; k = prefix, v = suffix after op. Returns false if op absent.
    private static bool TryBinop(string s, string op, out string k, out string v)
    {
        int o = s.IndexOf(op, System.StringComparison.Ordinal);
        if (o < 0) { k = string.Empty; v = string.Empty; return false; }
        k = s.Substring(0, o);
        v = s.Substring(o + op.Length);
        return true;
    }

    private static float Cvar(string name) => Api.Services is not null ? Api.Cvars.GetFloat(name) : 0f;

    private static string CvarString(string name) =>
        Api.Services is not null ? Api.Cvars.GetString(name) ?? string.Empty : string.Empty;

    // QC stof: parse a leading float, 0 on failure (lenient like the engine's atof).
    private static float Stof(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0f;
        return float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f;
    }

    // QC ftos: float-to-string. The engine prints integers without a decimal point and trims trailing zeros,
    // which is what the (ftos(f) == k) "is this a plain number literal" test in expr_evaluate relies on.
    private static string Ftos(float f) =>
        f == (int)f
            ? ((int)f).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : f.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    // QC tokenize_console: split on whitespace, honoring double-quoted spans. Empty string -> zero tokens.
    private static string[] TokenizeConsole(string s)
    {
        var list = new System.Collections.Generic.List<string>();
        if (string.IsNullOrEmpty(s)) return System.Array.Empty<string>();

        int i = 0, n = s.Length;
        var sb = new System.Text.StringBuilder();
        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(s[i])) i++;
            if (i >= n) break;

            sb.Clear();
            if (s[i] == '"')
            {
                i++;
                while (i < n && s[i] != '"') sb.Append(s[i++]);
                if (i < n) i++; // closing quote
            }
            else
            {
                while (i < n && !char.IsWhiteSpace(s[i])) sb.Append(s[i++]);
            }
            list.Add(sb.ToString());
        }
        return list.ToArray();
    }
}
