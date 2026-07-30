// Port of common/mutators/mutator/pinata/sv_pinata.qc.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Piñata mutator — port of <c>sv_pinata.qc</c>. When a player dies they scatter every OTHER weapon
/// they were carrying as real loot pickups (the HELD weapon drops via the normal death path,
/// <c>SpawnThrownWeapon</c> in the kill pipeline), each launched with the QC impulse
/// <c>randomvec()*175 + '0 0 325'</c> from <c>CENTER_OR_VIEWOFS</c>. Enabled by <c>g_pinata</c>
/// (mutators.cfg:557 default 0) and inert under instagib/overkill (QC
/// <c>!MUTATOR_IS_ENABLED(mutator_instagib) &amp;&amp; !MUTATOR_IS_ENABLED(ok)</c>).
/// </summary>
[Mutator]
public sealed class PinataMutator : MutatorBase
{
    /// <summary>QC <c>autocvar_g_pinata_offhand</c> (mutators.cfg:558 default 0): also run the throw loop for
    /// off-hand slots &gt; 0. The port drives a single weapon slot, so this is read but has no effect (QC's
    /// <c>if (slot &gt; 0 &amp;&amp; !offhand) break</c> never trips with one slot) — kept for cvar parity.</summary>
    public bool Offhand;

    public PinataMutator() => NetName = "pinata";

    // QC: REGISTER_MUTATOR(pinata, expr_evaluate(g_pinata) && !MUTATOR_IS_ENABLED(mutator_instagib) && !MUTATOR_IS_ENABLED(ok)).
    // g_pinata is a QC string evaluated via expr_evaluate (matches the sibling RocketFlyingMutator's
    // expr_evaluate(g_rocket_flying) idiom); ExprEvaluate handles expression-string values, not just 0/1.
    public override bool IsEnabled =>
        Api.Services is not null
        && ExprEvaluate(Api.Cvars.GetString("g_pinata"))
        && !OtherEnabled("instagib")
        && !OtherEnabled("overkill");

    private static float Stof(string s)
    {
        return float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f;
    }

    // QC ftos(f): the "canonical" float->string used to detect a literal-number token (e.g. "1" == ftos(1)).
    private static string Ftos(float f) =>
        f.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Faithful port of QC <c>expr_evaluate(string s)</c> (lib/cvar.qh:48). A boolean cvar-expression
    /// interpreter: an optional leading '+' (no-op) or '-' (negate the result); then each whitespace token is
    /// a predicate that must hold (logical AND) — either a comparison <c>var>=x</c> / <c>var&lt;=x</c> /
    /// <c>var&gt;</c> / <c>var&lt;</c> / <c>var==x</c> / <c>var!=x</c> (numeric, via cvar()) or
    /// <c>var===s</c> / <c>var!==s</c> (string, via cvar_string()), or a bare token which is either a literal
    /// number (its own truthiness) or a cvar name (cvar()'s truthiness), optionally '!'-prefixed to invert.
    /// If any predicate fails, the AND fails (and is NOT inverted by '-'); otherwise the running result flips.
    /// For the realistic literal defaults "0"/"1" this matches a plain truthiness check; the grammar covers
    /// expression-valued cvars (and, faithfully, <c>expr_evaluate("")</c> = true).
    /// </summary>
    private static bool ExprEvaluate(string s)
    {
        s ??= string.Empty;
        bool ret = false;
        if (s.Length > 0 && s[0] == '+') s = s.Substring(1);
        else if (s.Length > 0 && s[0] == '-') { ret = true; s = s.Substring(1); }

        bool exprFail = false;
        foreach (string tok in s.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries))
        {
            if (!ExprToken(tok)) { exprFail = true; break; }
        }
        if (!exprFail) ret = !ret;
        return ret;
    }

    // One whitespace token of expr_evaluate's AND chain — returns true if the predicate holds (QC: continue).
    private static bool ExprToken(string s)
    {
        int o;
        // Operators tested in EXACTLY QC's BINOP source order (>= <= == != === !==, then > <). strstrofs finds
        // the FIRST occurrence, so for a "var===x" token the leading "==" matches before "===" is ever tried —
        // a faithful quirk of expr_evaluate (lib/cvar.qh:69-80), not reordered here.
        if ((o = s.IndexOf(">=", System.StringComparison.Ordinal)) >= 0)
            return CvarF(s.Substring(0, o)) >= Stof(s.Substring(o + 2));
        if ((o = s.IndexOf("<=", System.StringComparison.Ordinal)) >= 0)
            return CvarF(s.Substring(0, o)) <= Stof(s.Substring(o + 2));
        if ((o = s.IndexOf("==", System.StringComparison.Ordinal)) >= 0)
            return CvarF(s.Substring(0, o)) == Stof(s.Substring(o + 2));
        if ((o = s.IndexOf("!=", System.StringComparison.Ordinal)) >= 0)
            return CvarF(s.Substring(0, o)) != Stof(s.Substring(o + 2));
        if ((o = s.IndexOf("===", System.StringComparison.Ordinal)) >= 0)
            return Cvar(s.Substring(0, o)) == s.Substring(o + 3);
        if ((o = s.IndexOf("!==", System.StringComparison.Ordinal)) >= 0)
            return Cvar(s.Substring(0, o)) != s.Substring(o + 3);
        if ((o = s.IndexOf('>')) >= 0)
            return CvarF(s.Substring(0, o)) > Stof(s.Substring(o + 1));
        if ((o = s.IndexOf('<')) >= 0)
            return CvarF(s.Substring(0, o)) < Stof(s.Substring(o + 1));

        // Bare token: literal number (its own value) or cvar name; optional leading '!' inverts.
        string k = s;
        bool b = true;
        if (k.Length > 0 && k[0] == '!') { k = k.Substring(1); b = false; }
        float f = Stof(k);
        // QC: boolean((ftos(f) == k) ? f : cvar(k)) — if k is a literal number use it, else read the cvar.
        float val = (Ftos(f) == k) ? f : CvarF(k);
        return (val != 0f) == b;
    }

    // cvar(name) / cvar_string(name) analogues; "" when services aren't up (cvar of a missing name is 0/"").
    private static float CvarF(string name) => Api.Services is null ? 0f : Api.Cvars.GetFloat(name);
    private static string Cvar(string name) => Api.Services is null ? string.Empty : Api.Cvars.GetString(name);

    // QC MUTATOR_IS_ENABLED reads the other mutator's enable predicate (not its added state, so activation
    // order between the three can't race).
    private static bool OtherEnabled(string netName)
        => Mutators.ByName(netName) is { } m && m.IsEnabled;

    private HookHandler<MutatorHooks.PlayerDiesArgs>? _onPlayerDies;

    public override void Hook()
    {
        _onPlayerDies ??= OnPlayerDies;
        MutatorHooks.PlayerDies.Add(_onPlayerDies);

        if (Api.Services is not null)
            Offhand = Api.Cvars.GetFloat("g_pinata_offhand") != 0f;
    }

    public override void Unhook()
    {
        if (_onPlayerDies is not null) MutatorHooks.PlayerDies.Remove(_onPlayerDies);
    }

    // MUTATOR_HOOKFUNCTION(pinata, PlayerDies) — sv_pinata.qc:7-30.
    private bool OnPlayerDies(ref MutatorHooks.PlayerDiesArgs args)
    {
        if (Api.Services is null) return false;
        Entity target = args.Target;
        var slot = new WeaponSlot(0);

        Weapon? held = Inventory.CurrentWeapon(target);
        // QC sv_pinata.qc:15-16: `if(frag_target.(weaponentity).m_weapon == WEP_Null) continue;` — a slot with
        // no held weapon throws nothing. The port drives a single slot (slot 0), so a null held weapon means the
        // QC loop continues with no further slots to scan -> scatter nothing at all.
        if (held is null) return true;
        // QC CENTER_OR_VIEWOFS(frag_target): a player isn't IS_DEAD yet at the PlayerDies hook, so this is
        // origin + view_ofs (the eye), not the bbox center.
        Vector3 org = target.Origin + target.ViewOfs;

        // FOREACH(Weapons, owned && != held && throwable): the held one drops via the normal death path.
        foreach (Weapon it in target.OwnedWeaponSet.Weapons())
        {
            if (ReferenceEquals(it, held))
                continue;
            if (!WeaponThrowing.IsWeaponThrowable(target, it))
                continue;
            WeaponThrowing.ThrowNewWeapon(target, it, doreduce: false, org,
                Prandom.Vec() * 175f + new Vector3(0f, 0f, 325f), slot);
        }
        return true; // QC hookfunction returns true (does not stop the chain)
    }

    // MUTATOR_HOOKFUNCTION(pinata, BuildMutatorsString) — sv_pinata.qc:32-35: strcat(s, ":Pinata").
    // The machine token used by the server browser / serverinfo active-mutators list.
    public override string BuildMutatorsString(string s) => s + ":Pinata";

    // MUTATOR_HOOKFUNCTION(pinata, BuildMutatorsPrettyString) — sv_pinata.qc:37-40: strcat(s, ", Piñata").
    // The human-readable token shown in the scoreboard/HUD active-mutators display.
    public override string BuildMutatorsPrettyString(string s) => s + ", Piñata";
}
