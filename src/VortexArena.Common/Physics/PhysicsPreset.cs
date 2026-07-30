// Port of Base/data/xonotic-data.pk3dir/qcsrc/common/physics/player.qc — Physics_Valid (:18-21) and
// Physics_ClientOption (:23-42): per-client physics-set selection (g_physics_clientselect). A client picks a
// named preset (cl_physics, via `cmd physics <set>` or the sentcvar channel) and every preset-resolvable
// movement stat resolves through the g_physics_<set>_<option> cvar table (physics.cfg) before falling back to
// the global sv_* value.
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Physics;

/// <summary>
/// The QC <c>Physics_ClientOption</c> resolution chain, host-agnostic over <see cref="ICvarService"/>:
/// <list type="number">
///   <item><c>g_physics_clientselect</c> off → the <c>sv_*</c> default (player.qc:25-26);</item>
///   <item>the client's <c>cl_physics</c> is a <see cref="Valid"/> set AND <c>g_physics_&lt;set&gt;_&lt;option&gt;</c>
///         EXISTS → that cvar (player.qc:28-33);</item>
///   <item><c>g_physics_clientselect_default</c> is set (≠ ""/"default") AND its cvar EXISTS → that cvar —
///         deliberately NOT Physics_Valid-checked, so the default can be forced to an unlisted set
///         (player.qc:34-40, the NOTE comment);</item>
///   <item>else the <c>sv_*</c> default (player.qc:41).</item>
/// </list>
/// The jumpspeedcap options are STRING cvars whose stock value is the literal <c>"nan"</c> (cap disabled) while
/// some presets set a REAL 0 (g_physics_xdf_jumpspeedcap_min 0) — 0 and NaN are distinct values, so preset
/// reads go through <see cref="PresetFloat"/> ("nan" → <see cref="float.NaN"/>, numbers verbatim) rather than
/// the store's GetFloat.
/// </summary>
public static class PhysicsPreset
{
    /// <summary>
    /// The preset-resolvable option names — exactly the per-player <c>Physics_ClientOption</c> writes in
    /// <c>Physics_UpdateStats</c> (player.qc:51-148), which is also exactly the 36-var block each
    /// <c>g_physics_&lt;set&gt;_*</c> preset defines (physics.cfg). Maps option → its global <c>sv_*</c>
    /// fallback cvar name (the QC <c>autocvar_sv_*</c> default argument).
    /// </summary>
    public static readonly (string Option, string FallbackCvar)[] Options =
    {
        ("gravity",                              "sv_gravity"),
        ("airaccel_qw",                          "sv_airaccel_qw"),
        ("airstrafeaccel_qw",                    "sv_airstrafeaccel_qw"),
        ("airspeedlimit_nonqw",                  "sv_airspeedlimit_nonqw"),
        ("maxspeed",                             "sv_maxspeed"),
        ("jumpvelocity",                         "sv_jumpvelocity"),
        ("jumpvelocity_crouch",                  "sv_jumpvelocity_crouch"),
        ("maxairstrafespeed",                    "sv_maxairstrafespeed"),
        ("maxairspeed",                          "sv_maxairspeed"),
        ("airstrafeaccelerate",                  "sv_airstrafeaccelerate"),
        ("warsowbunny_turnaccel",                "sv_warsowbunny_turnaccel"),
        ("airaccel_qw_stretchfactor",            "sv_airaccel_qw_stretchfactor"),
        ("airaccel_sideways_friction",           "sv_airaccel_sideways_friction"),
        ("aircontrol",                           "sv_aircontrol"),
        ("aircontrol_flags",                     "sv_aircontrol_flags"),
        ("aircontrol_power",                     "sv_aircontrol_power"),
        ("aircontrol_penalty",                   "sv_aircontrol_penalty"),
        ("warsowbunny_airforwardaccel",          "sv_warsowbunny_airforwardaccel"),
        ("warsowbunny_topspeed",                 "sv_warsowbunny_topspeed"),
        ("warsowbunny_accel",                    "sv_warsowbunny_accel"),
        ("warsowbunny_backtosideratio",          "sv_warsowbunny_backtosideratio"),
        ("friction",                             "sv_friction"),
        ("friction_slick",                       "sv_friction_slick"),
        ("stepheight",                           "sv_stepheight"),
        ("accelerate",                           "sv_accelerate"),
        ("stopspeed",                            "sv_stopspeed"),
        ("airaccelerate",                        "sv_airaccelerate"),
        ("airstopaccelerate",                    "sv_airstopaccelerate"),
        ("airstopaccelerate_full",               "sv_airstopaccelerate_full"),
        ("slickaccelerate",                      "sv_slickaccelerate"),
        ("doublejump",                           "sv_doublejump"),                  // STAT(DOUBLEJUMP), player.qc:141
        ("jumpspeedcap_min",                     "sv_jumpspeedcap_min"),
        ("jumpspeedcap_max",                     "sv_jumpspeedcap_max"),
        ("jumpspeedcap_max_disable_on_ramps",    "sv_jumpspeedcap_max_disable_on_ramps"),
        ("track_canjump",                        "sv_track_canjump"),
        ("stepdown",                             "sv_gameplayfix_stepdown"),        // GAMEPLAYFIX_STEPDOWN, player.qc:148
        // PORT EXTENSION (not in stock QC): make the step-up launch cap preset-resolvable so a client-selectable
        // set (the "bryan" preset) can carry its own sv_step_upspeed_max. A set that doesn't define
        // g_physics_<set>_step_upspeed_max resolves to the global sv_step_upspeed_max (default -1 = disabled), so
        // every existing preset is unchanged. sv_step_upspeed_scale stays GLOBAL (no preset needs to vary it yet).
        ("step_upspeed_max",                     "sv_step_upspeed_max"),
    };

    /// <summary>
    /// The preset OPTION name a global movement cvar resolves through, or null when the cvar is NOT
    /// preset-resolvable (the stats.qh entries with a cvar expression — jumpstep, friction_on_land,
    /// wallfriction, stepdown_maxspeed, nostep, wallclip, slick_applygravity, the g_movement_highspeed pair,
    /// nudgeoutofsolid — are global: the same value goes to every client, no preset lookup).
    /// </summary>
    public static string? OptionFor(string svCvarName)
    {
        for (int i = 0; i < Options.Length; i++)
            if (Options[i].FallbackCvar == svCvarName)
                return Options[i].Option;
        return null;
    }

    /// <summary>
    /// QC <c>Physics_Valid</c> (player.qc:18-21): a non-empty, non-"default" set name listed in
    /// <c>g_physics_clientselect_options</c> (QC <c>strhasword</c> — whole-word, whitespace-separated).
    /// </summary>
    public static bool Valid(ICvarService cvars, string set)
    {
        if (string.IsNullOrEmpty(set) || set == "default")
            return false;
        foreach (string word in cvars.GetString("g_physics_clientselect_options")
                     .Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries))
            if (word == set)
                return true;
        return false;
    }

    /// <summary>
    /// QC <c>Physics_ClientOption(this, option, defaultval)</c> (player.qc:23-42). <paramref name="clPhysics"/>
    /// is the entity's replicated <c>cl_physics</c> — pass "" for a non-real client (bot), which skips chain 2
    /// but still resolves through <c>g_physics_clientselect_default</c> (chain 3 applies to ANY entity in QC).
    /// <paramref name="exists"/> is the CVAR_TYPEFLAG_EXISTS check; when null, falls back to GetString != ""
    /// (preset table values are never empty in physics.cfg, so this only misreads a cvar EXPLICITLY set empty).
    /// </summary>
    public static float Resolve(ICvarService cvars, string clPhysics, string option, float defaultval,
        System.Func<string, bool>? exists = null)
    {
        if (cvars.GetFloat("g_physics_clientselect") == 0f)
            return defaultval;

        if (Valid(cvars, clPhysics))
        {
            string s = "g_physics_" + clPhysics + "_" + option;
            if (Exists(cvars, s, exists))
                return PresetFloat(cvars, s);
        }
        string def = cvars.GetString("g_physics_clientselect_default");
        if (def != "" && def != "default")
        {
            // NOTE: not using Physics_Valid here, so the default can be forced to something normally
            // unavailable (player.qc:36).
            string s = "g_physics_" + def + "_" + option;
            if (Exists(cvars, s, exists))
                return PresetFloat(cvars, s);
        }
        return defaultval;
    }

    private static bool Exists(ICvarService cvars, string name, System.Func<string, bool>? exists)
        => exists is not null ? exists(name) : cvars.GetString(name).Length != 0;

    /// <summary>
    /// Read a preset cvar as a float with DP's <c>atof</c> semantics for the <c>"nan"</c> literal: "nan" →
    /// <see cref="float.NaN"/> (jumpspeedcap disabled), any number verbatim — a REAL 0 (xdf/quake3/cpma
    /// jumpspeedcap) stays 0, never conflated with NaN. Other non-numeric strings read 0 (DP atof).
    /// </summary>
    public static float PresetFloat(ICvarService cvars, string name)
    {
        string s = cvars.GetString(name);
        if (s.Equals("nan", System.StringComparison.OrdinalIgnoreCase))
            return float.NaN;
        return cvars.GetFloat(name);
    }
}
