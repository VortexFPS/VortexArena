// Per-argument token expansion for notifications — the C# successor to QuakeC's NOTIF_ARGUMENT_LIST
// (common/notifications/all.qh) plus the spree/frag helper functions. A notification's Args string names
// one token per template "%s" slot; Resolve turns each token into its display string using the supplied
// s1..s4 / f1..f4 values. This is the data half of Local_Notification_sprintf.
//
// Stateless: everything is pure given the args and a couple of cvars (read through Api.Cvars, honouring
// the Xonotic cvar names exactly: notification_show_location, notification_show_sprees, …). The richer
// CSQC-only presentation tokens (frag_ping/frag_stats — which need the killed player's health/armor/ping)
// are honoured: the server already passes those as floats (f2/f3/f4), so we format them the same way QC's
// CSQC does. Color codes in the produced strings are left for the client to expand.

using System.Globalization;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Resolves notification arg tokens (s1, s2loc, spree_inf, item_wepname, death_team, frag_stats, …) to
/// display strings. Mirrors QC's NOTIF_ARGUMENT_LIST one-token-per-case table.
/// </summary>
public static class NotifTokens
{
    /// <summary>
    /// Key-bind tokens (QC NOTIF_ARGUMENT_LIST <c>pass_key</c>/<c>nade_key</c>/<c>join_key</c>, all.qh) are
    /// <c>ARG_CS</c> — CSQC-only — because only the client knows its own binds (QC resolves them via
    /// <c>getcommandkey</c>). The port formats notification text SERVER-side, where binds are unknown, so we
    /// cannot resolve them here. Instead each key token expands to a recoverable client-substitution
    /// SENTINEL (<c>\x02KB:&lt;token&gt;\x02</c>) that survives the wire <c>WriteString</c> intact; the client
    /// (<c>HudNotifications.SubstituteKeyBinds</c>) replaces each sentinel with the local
    /// <c>BindTable.CommandKey</c> result. The STX delimiter never occurs in normal message/color-code text.
    /// </summary>
    public const string KeyBindSentinelPrefix = "KB:";

    /// <summary>The closing marker of a key-bind sentinel — see <see cref="KeyBindSentinelPrefix"/>.</summary>
    public const string KeyBindSentinelSuffix = "";

    private static string KeyBindSentinel(string token) => KeyBindSentinelPrefix + token + KeyBindSentinelSuffix;

    /// <summary>The KILL_SPREE_LIST milestones (count -> phrase), QC notifications/all.qh.</summary>
    private static readonly (int Count, string Center, string Normal, string Gentle)[] SpreeList =
    {
        (3,  "TRIPLE FRAG!", "%s^K1 made a TRIPLE FRAG!",   "%s^K1 made a TRIPLE SCORE!"),
        (5,  "RAGE!",        "%s^K1 unlocked RAGE!",        "%s^K1 made FIVE SCORES IN A ROW!"),
        (10, "MASSACRE!",    "%s^K1 started a MASSACRE!",   "%s^K1 made TEN SCORES IN A ROW!"),
        (15, "MAYHEM!",      "%s^K1 executed MAYHEM!",      "%s^K1 made FIFTEEN SCORES IN A ROW!"),
        (20, "BERSERKER!",   "%s^K1 is a BERSERKER!",       "%s^K1 made TWENTY SCORES IN A ROW!"),
        (25, "CARNAGE!",     "%s^K1 inflicts CARNAGE!",     "%s^K1 made TWENTY FIVE SCORES IN A ROW!"),
        (30, "ARMAGEDDON!",  "%s^K1 unleashes ARMAGEDDON!", "%s^K1 made THIRTY SCORES IN A ROW!"),
    };

    /// <summary>
    /// Resolve the space-separated <paramref name="args"/> tokens into one display string each, in order.
    /// <paramref name="strs"/> are s1.., <paramref name="flts"/> are f1.. (QC ordering). <paramref name="input"/>
    /// is the unformatted normal template (QC passes it to spree helpers for the newline-after-control-char
    /// heuristic). Unknown tokens resolve to an empty string (QC backtraces and stops; here we drop them so
    /// a missing token never crashes a recorded line).
    /// </summary>
    public static string[] Resolve(string args, string[] strs, float[] flts, string input)
    {
        if (string.IsNullOrWhiteSpace(args)) return System.Array.Empty<string>();
        string[] tokens = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        var result = new string[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
            result[i] = ResolveOne(tokens[i], strs, flts, input);
        return result;
    }

    private static string S(string[] s, int idx) => idx >= 0 && idx < s.Length ? s[idx] : "";
    private static float F(float[] f, int idx) => idx >= 0 && idx < f.Length ? f[idx] : 0f;

    private static string ResolveOne(string token, string[] s, float[] f, string input) => token switch
    {
        "s1" => S(s, 0),
        "s2" => S(s, 1),
        "s3" => S(s, 2),
        "s4" => S(s, 3),

        // location suffixes: " (near <name>)" when the location string is present and the cvar is on.
        "s2loc" => Loc(S(s, 1)),
        "s3loc" => Loc(S(s, 2)),
        "s4loc" => Loc(S(s, 3)),

        "f1" => Ftos(F(f, 0)),
        "f2" => Ftos(F(f, 1)),
        "f3" => Ftos(F(f, 2)),
        "f4" => Ftos(F(f, 3)),
        "f1dtime" => F(f, 0).ToString("0.00", CultureInfo.InvariantCulture),
        "f2dtime" => F(f, 1).ToString("0.00", CultureInfo.InvariantCulture),
        "f2primsec" => F(f, 1) != 0f ? "secondary" : "primary",
        "f3primsec" => F(f, 2) != 0f ? "secondary" : "primary",
        "f1secs" => CountSeconds((int)F(f, 0)),
        "f1points" => F(f, 0) == 1f ? "1 point" : $"{(int)F(f, 0)} points",
        "f1ord" => Ordinal((int)F(f, 0)),
        "f1time" => ProcessTime((int)F(f, 0)),

        // race times: the float args carry TIME_ENCODE'd (hundredths) integers; TIME_ENCODED_TOSTRING(n, true)
        // = clockedtime_tostring(n, true, true) is the compact mm:ss.hh / ss.hh string.
        "f1race_time" => RaceTime((int)F(f, 0)),
        "f2race_time" => RaceTime((int)F(f, 1)),
        "f3race_time" => RaceTime((int)F(f, 2)),
        // race_col: ^F1 when the position is 1 (a good/first time), else ^F2. Color codes left raw for the
        // client's CCR expansion (QC CCR wraps it; the port's HudText.Expand does the same expansion).
        "race_col" => (int)F(f, 0) == 1 ? "^F1" : "^F2",
        // race_diff: ^3[+0.0] when equal, ^1[+diff] when f2 slower, ^2[-diff] when f2 faster (encoded ints).
        "race_diff" => RaceDiff((int)F(f, 1), (int)F(f, 2)),

        // kill-spree phrasing
        "spree_cen" => ShowSprees() ? SpreeCen((int)F(f, 0)) : "",
        "spree_inf" => ShowSprees() ? SpreeInf(1, input, S(s, 1), (int)F(f, 1)) : "",
        "spree_end" => ShowSprees() ? SpreeInf(-1, "", "", (int)F(f, 0)) : "",
        "spree_lost" => ShowSprees() ? SpreeInf(-2, "", "", (int)F(f, 0)) : "",

        // item names (f1 = registry id)
        "item_wepname" => WeaponName((int)F(f, 0)),
        "item_buffname" => BuffName((int)F(f, 0)),
        "item_wepammo" => F(f, 1) > 0f ? $" with {(int)F(f, 1)} {WeaponAmmoName((int)F(f, 0))}" : "",

        // team names: SVQC uses f1 directly (1..4), CSQC uses f1-1; we take the server convention (f1 = team idx 1..4).
        "death_team" => TeamColoredFullName((int)F(f, 0)),

        // hash-replace tokens: the port formats server-side, so we take the ARG_SV variants (all.qh:465-466):
        // "s3#s2" -> s3, "#s2" -> s2 (the CSQC HASH_REPLACE in-template attacker-name splice is a client-only
        // path the port doesn't take). The "#" in the template is consumed verbatim alongside the %s.
        "s3#s2" => S(s, 2),
        "#s2" => S(s, 1),

        // key-bind hints (QC ARG_CS pass_key/nade_key/join_key, getcommandkey lookups): resolved CLIENT-side
        // (only the client knows its binds) — emit a sentinel the client swaps for the local key (see
        // KeyBindSentinelPrefix). Used by CTF_PASS_REQUESTED/ONS_TELEPORT/VEHICLE_ENTER* (pass_key = +use),
        // NADE_THROW (nade_key = the +hook/offhand button) and the spectator-join hint (join_key = +jump).
        "pass_key" => KeyBindSentinel("pass_key"),
        "nade_key" => KeyBindSentinel("nade_key"),
        "join_key" => KeyBindSentinel("join_key"),

        // frag presentation (server passes the killed player's ping/health/armor as floats)
        "frag_ping" => FragPing(true, F(f, 1)),
        "frag_stats" => FragStats(F(f, 1), F(f, 2), F(f, 3)),

        _ => "", // unknown token (QC backtraces); drop it
    };

    // ---- helpers (QC util.qc / all.qh) ----

    private static bool ShowLocation() => CvarBool("notification_show_location", false);
    private static bool ShowSprees() => CvarBool("notification_show_sprees", true);
    private static bool ShowSpreesCenter() => CvarBool("notification_show_sprees_center", true);
    private static int ShowSpreesInfo() => CvarInt("notification_show_sprees_info", 3);

    private static string Loc(string locationName)
    {
        if (!ShowLocation() || string.IsNullOrEmpty(locationName)) return "";
        return $" (near {locationName})";
    }

    private static string Ftos(float v)
    {
        // QC ftos: integer floats print without a decimal point.
        if (v == System.MathF.Floor(v)) return ((long)v).ToString(CultureInfo.InvariantCulture);
        return v.ToString("0.######", CultureInfo.InvariantCulture);
    }

    // QC count_seconds: "%d second(s)".
    private static string CountSeconds(int n) => n == 1 ? "1 second" : $"{n} seconds";

    // QC count_ordinal: 1st/2nd/3rd/Nth.
    private static string Ordinal(int n)
    {
        int mod100 = n % 100;
        string suffix = (mod100 is >= 11 and <= 13) ? "th" : (n % 10) switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th",
        };
        return $"{n}{suffix}";
    }

    // QC process_time(2, t): mm:ss for a seconds count.
    private static string ProcessTime(int seconds)
    {
        int m = seconds / 60, s = seconds % 60;
        return $"{m}:{s:00}";
    }

    // QC TIME_ENCODED_TOSTRING(n, true) = clockedtime_tostring(n, true, true): an encoded-hundredths race
    // time -> a compact "mm:ss.hh" / "ss.hh" string. Shares GameScores' exact clockedtime_tostring port.
    private static string RaceTime(int encoded) => Scoring.GameScores.TimeEncodedToString(encoded, compact: true);

    // QC "race_diff" (all.qh:448): the signed delta between two encoded race times.
    //   |f2 - f3| renders as "0.00"  -> "^3[+0.0]"  (a tie)
    //   f2 > f3 (this time slower)   -> "^1[+<mmssth(f2-f3)>]"
    //   f2 < f3 (this time faster)   -> "^2[-<mmssth(f3-f2)>]"
    // Color codes are left raw for the client's CCR expansion.
    private static string RaceDiff(int f2, int f3)
    {
        if (RaceTime(System.Math.Abs(f2 - f3)) == "0.00")
            return "^3[+0.0]";
        return f2 > f3
            ? $"^1[+{RaceTime(f2 - f3)}]"
            : $"^2[-{RaceTime(f3 - f2)}]";
    }

    // QC notif_arg_spree_cen: centered spree banner (with " " suffix).
    private static string SpreeCen(int spree)
    {
        if (!ShowSpreesCenter()) return "";
        if (spree > 1)
        {
            foreach (var item in SpreeList)
                if (item.Count == spree)
                    return item.Center + " ";
            // non-milestone: show a generic spree count unless special-only
            if (!CvarBool("notification_show_sprees_center_specialonly", true))
                return $"{spree} frag spree! ";
            return "";
        }
        if (spree == -1) return "First blood! ";  // firstblood
        if (spree == -2) return "First victim! "; // first victim
        return "";
    }

    // QC notif_arg_spree_inf: info-line spree phrasing. type 1 = attacker spree (prefix, uses player name),
    // -1 = "ending their N frag spree", -2 = "ending it with N frags" (the victim-lost suffix).
    private static string SpreeInf(int type, string input, string player, int spree)
    {
        switch (type)
        {
            case 1:
            {
                if ((ShowSpreesInfo() & 2) == 0) return "";
                string nl = CvarBool("notification_show_sprees_info_newline", true) ? "\n" : "";
                if (spree > 1)
                {
                    foreach (var item in SpreeList)
                        if (item.Count == spree)
                            return $"{player}{nl} ";
                    if (!CvarBool("notification_show_sprees_info_specialonly", true))
                        return $"{player}^K1 has {spree} frags in a row! {nl}";
                    return "";
                }
                if (spree == -1) return $"{player}^K1 drew first blood! {nl}";
                return "";
            }
            case -1:
                if (spree > 1 && (ShowSpreesInfo() & 1) != 0)
                    return $", ending their {spree} frag spree";
                return "";
            case -2:
                if (spree > 1 && (ShowSpreesInfo() & 1) != 0)
                    return $", losing their {spree} frag spree";
                return "";
        }
        return "";
    }

    // QC notif_arg_frag_ping (CSQC): " (Ping ^F1N^BG)" or " (^F1Bot^BG)" for a negative ping.
    private static string FragPing(bool newline, float ping)
    {
        string s = newline ? "\n" : " ";
        return ping < 0 ? $"{s}(^F1Bot^BG)" : $"{s}(Ping ^F1{(int)ping}^BG)";
    }

    // QC notif_arg_frag_stats: "\n(Health ^1H^BG / Armor ^2A^BG)<ping>" or "\n(^F4Dead^BG)<ping>".
    private static string FragStats(float health, float armor, float ping)
    {
        string p = FragPing(false, ping);
        return health > 1f
            ? $"\n(Health ^1{(int)health}^BG / Armor ^2{(int)armor}^BG){p}"
            : $"\n(^F4Dead^BG){p}";
    }

    // QC item_wepname: REGISTRY_GET(Weapons, f1).m_name. Weapons are an attribute-registered registry.
    private static string WeaponName(int id)
    {
        if (id >= 0 && id < Registry<Weapon>.Count)
        {
            var w = Registry<Weapon>.ById(id);
            return string.IsNullOrEmpty(w.DisplayName) ? w.NetName : w.DisplayName;
        }
        return "";
    }

    // QC notif_arg_item_wepammo: weapon (f1) ammo type name (cells/rockets/shells/bullets/fuel).
    // Returns the ammo type's m_name in lowercase, or empty if no ammo.
    private static string WeaponAmmoName(int id)
    {
        if (id >= 0 && id < Registry<Weapon>.Count)
        {
            var w = Registry<Weapon>.ById(id);
            if (w.AmmoType == ResourceType.None)
                return ""; // ammo-less weapon
            return w.AmmoType switch
            {
                ResourceType.Shells => "shells",
                ResourceType.Bullets => "bullets",
                ResourceType.Rockets => "rockets",
                ResourceType.Cells => "cells",
                ResourceType.Fuel => "fuel",
                _ => "",
            };
        }
        return "";
    }

    // QC item_buffname: REGISTRY_GET(StatusEffects, f1).m_name. Buffs live in StatusEffectsCatalog.
    private static string BuffName(int id)
    {
        if (id >= 0 && id < Registry<StatusEffectDef>.Count)
            return Registry<StatusEffectDef>.ById(id).Name;
        return "";
    }

    // QC Team_ColoredFullName(teamnum): a colored "Red Team"/.. for team idx 1..4.
    private static string TeamColoredFullName(int teamIndex)
    {
        (string color, string name) = teamIndex switch
        {
            1 => ("^1", "Red Team"),
            2 => ("^4", "Blue Team"),
            3 => ("^3", "Yellow Team"),
            4 => ("^6", "Pink Team"),
            _ => ("^7", "Neutral Team"),
        };
        return color + name;
    }

    private static bool CvarBool(string name, bool fallback)
    {
        if (Api.Services is null) return fallback;
        // An UNSET cvar reads as an empty string (CvarService.GetString of a missing key); only then do we
        // fall back to the QC autocvar default. An explicitly-set cvar (incl. an explicit "0") is honored,
        // so we never force a flag on/off when the user set it. Same unset-detection convention as
        // MovementParameters.Exists / PhysicsPreset.
        if (Api.Cvars.GetString(name).Length == 0) return fallback;
        return Api.Cvars.GetFloat(name) != 0f;
    }

    private static int CvarInt(string name, int fallback)
    {
        if (Api.Services is null) return fallback;
        if (Api.Cvars.GetString(name).Length == 0) return fallback;
        return (int)Api.Cvars.GetFloat(name);
    }
}
