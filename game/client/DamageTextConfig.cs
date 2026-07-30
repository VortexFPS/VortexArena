// Port of the ~30 cl_damagetext_* cvars (xonotic-client.cfg:562-592) consumed by the CSQC DamageText draw
// (common/mutators/mutator/damagetext/cl_damagetext.qc). The shipped defaults are kept as the fallbacks.

using Godot;
using VortexArena.Common.Services;

namespace VortexArena.Game.Client;

/// <summary>
/// A snapshot of the <c>cl_damagetext_*</c> client cvars driving the floating damage numbers
/// (<see cref="DamageTextLayer"/>). Read once per frame via <see cref="Read"/> (so a runtime cvar change takes
/// effect live), with the shipped xonotic-client.cfg defaults as fallbacks. Vectors are stored as Godot
/// <see cref="Vector3"/>; the colors as <see cref="Color"/>.
/// </summary>
public readonly struct DamageTextConfig
{
    public readonly float Enabled;            // cl_damagetext (1)
    public readonly string Format;            // cl_damagetext_format ("-{total}")
    public readonly bool FormatVerbose;       // cl_damagetext_format_verbose (0)
    public readonly bool FormatHideRedundant; // cl_damagetext_format_hide_redundant (0)
    public readonly Color Color;              // cl_damagetext_color ("1 1 0")
    public readonly bool ColorPerWeapon;      // cl_damagetext_color_per_weapon (0)
    public readonly float SizeMin;            // cl_damagetext_size_min (10)
    public readonly float SizeMinDamage;      // cl_damagetext_size_min_damage (25)
    public readonly float SizeMax;            // cl_damagetext_size_max (16)
    public readonly float SizeMaxDamage;      // cl_damagetext_size_max_damage (140)
    public readonly float AlphaStart;         // cl_damagetext_alpha_start (1)
    public readonly float AlphaLifetime;      // cl_damagetext_alpha_lifetime (3)
    public readonly float Lifetime;           // cl_damagetext_lifetime (-1)
    public readonly Vector3 VelocityScreen;   // cl_damagetext_velocity_screen ("0 0 0")
    public readonly Vector3 VelocityWorld;    // cl_damagetext_velocity_world ("0 0 20")
    public readonly Vector3 OffsetScreen;     // cl_damagetext_offset_screen ("0 -45 0")
    public readonly Vector3 OffsetWorld;      // cl_damagetext_offset_world ("0 25 0")
    public readonly float AccumulateAlphaRel; // cl_damagetext_accumulate_alpha_rel (0.65)
    public readonly float AccumulateLifetime; // cl_damagetext_accumulate_lifetime (-1)
    public readonly int FriendlyFire;         // cl_damagetext_friendlyfire (1)
    public readonly Color FriendlyFireColor;  // cl_damagetext_friendlyfire_color ("1 0 0")
    public readonly bool Use2d;               // cl_damagetext_2d (1)
    public readonly Vector3 Pos2d;            // cl_damagetext_2d_pos ("0.47 0.53 0")
    public readonly float Alpha2dStart;       // cl_damagetext_2d_alpha_start (1)
    public readonly float Alpha2dLifetime;    // cl_damagetext_2d_alpha_lifetime (1.3)
    public readonly float Size2dLifetime;     // cl_damagetext_2d_size_lifetime (3)
    public readonly Vector3 Velocity2d;       // cl_damagetext_2d_velocity ("-25 0 0")
    public readonly Vector3 OverlapOffset2d;  // cl_damagetext_2d_overlap_offset ("0 -15 0")
    public readonly float CloseRange2d;       // cl_damagetext_2d_close_range (125)
    public readonly bool OutOfView2d;         // cl_damagetext_2d_out_of_view (1)

    private DamageTextConfig(bool _)
    {
        Enabled = F("cl_damagetext", 1f);
        Format = S("cl_damagetext_format", "-{total}");
        FormatVerbose = B("cl_damagetext_format_verbose", false);
        FormatHideRedundant = B("cl_damagetext_format_hide_redundant", false);
        Color = C("cl_damagetext_color", new Color(1f, 1f, 0f));
        ColorPerWeapon = B("cl_damagetext_color_per_weapon", false);
        SizeMin = F("cl_damagetext_size_min", 10f);
        SizeMinDamage = F("cl_damagetext_size_min_damage", 25f);
        SizeMax = F("cl_damagetext_size_max", 16f);
        SizeMaxDamage = F("cl_damagetext_size_max_damage", 140f);
        AlphaStart = F("cl_damagetext_alpha_start", 1f);
        AlphaLifetime = F("cl_damagetext_alpha_lifetime", 3f);
        Lifetime = F("cl_damagetext_lifetime", -1f);
        VelocityScreen = V("cl_damagetext_velocity_screen", new Vector3(0f, 0f, 0f));
        VelocityWorld = V("cl_damagetext_velocity_world", new Vector3(0f, 0f, 20f));
        OffsetScreen = V("cl_damagetext_offset_screen", new Vector3(0f, -45f, 0f));
        OffsetWorld = V("cl_damagetext_offset_world", new Vector3(0f, 25f, 0f));
        AccumulateAlphaRel = F("cl_damagetext_accumulate_alpha_rel", 0.65f);
        AccumulateLifetime = F("cl_damagetext_accumulate_lifetime", -1f);
        FriendlyFire = (int)F("cl_damagetext_friendlyfire", 1f);
        FriendlyFireColor = C("cl_damagetext_friendlyfire_color", new Color(1f, 0f, 0f));
        Use2d = B("cl_damagetext_2d", true);
        Pos2d = V("cl_damagetext_2d_pos", new Vector3(0.47f, 0.53f, 0f));
        Alpha2dStart = F("cl_damagetext_2d_alpha_start", 1f);
        Alpha2dLifetime = F("cl_damagetext_2d_alpha_lifetime", 1.3f);
        Size2dLifetime = F("cl_damagetext_2d_size_lifetime", 3f);
        Velocity2d = V("cl_damagetext_2d_velocity", new Vector3(-25f, 0f, 0f));
        OverlapOffset2d = V("cl_damagetext_2d_overlap_offset", new Vector3(0f, -15f, 0f));
        CloseRange2d = F("cl_damagetext_2d_close_range", 125f);
        OutOfView2d = B("cl_damagetext_2d_out_of_view", true);
    }

    /// <summary>Read the live cvar values (with shipped defaults). Falls back to all-defaults if no cvar service.</summary>
    public static DamageTextConfig Read() => new(true);

    private static float F(string name, float def)
    {
        if (Api.Services is null) return def;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? def : Api.Cvars.GetFloat(name);
    }

    private static string S(string name, string def)
    {
        if (Api.Services is null) return def;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? def : s;
    }

    private static bool B(string name, bool def) => F(name, def ? 1f : 0f) != 0f;

    private static Color C(string name, Color def)
    {
        Vector3 v = V(name, new Vector3(def.R, def.G, def.B));
        return new Color(v.X, v.Y, v.Z);
    }

    // Parse a "x y z" cvar string into a Vector3 (QC vector cvar).
    private static Vector3 V(string name, Vector3 def)
    {
        if (Api.Services is null) return def;
        string s = Api.Cvars.GetString(name);
        if (string.IsNullOrEmpty(s)) return def;
        string[] parts = s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        float x = parts.Length > 0 && float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float fx) ? fx : def.X;
        float y = parts.Length > 1 && float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float fy) ? fy : def.Y;
        float z = parts.Length > 2 && float.TryParse(parts[2], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float fz) ? fz : def.Z;
        return new Vector3(x, y, z);
    }
}
