using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace VortexArena.Formats.Lighting;

/// <summary>
/// A DarkPlaces <c>.rtlights</c> file: the authored real-time world lights for a map, the thing
/// <c>r_shadow_realtime_world</c> renders and <c>r_editlights</c> writes.
///
/// <para><b>Format</b> (DP <c>r_shadow.c</c>: <c>R_Shadow_SaveWorldLights</c> writes it,
/// <c>R_Shadow_LoadWorldLights</c> reads it). Plain text, one light per line, whitespace-separated, in one of
/// three progressively shorter forms — the writer picks the shortest that loses nothing:</para>
/// <code>
/// [!]x y z radius r g b style "cubemap" corona ax ay az coronasize ambient diffuse specular flags   (18)
/// [!]x y z radius r g b style "cubemap" corona ax ay az                                             (13)
/// [!]x y z radius r g b style                                                                       (8)
/// </code>
/// <para>A leading <c>!</c> on the x field means <b>this light casts no shadow</b>. The short form is emitted
/// when everything it omits is at its default (<c>coronasizescale 0.25, ambientscale 0, diffusescale 1,
/// specularscale 1, flags LIGHTFLAG_REALTIMEMODE</c>) and there is no cubemap, corona or rotation — which is
/// why a real file mixes forms line by line, as the shipped Xonotic ones do.</para>
///
/// <para><b>Coordinates are Quake</b> (Z-up), and <c>radius</c> is in Quake units. DP's own comment on the
/// field is "brightness (not really radius anymore)", which is worth knowing before treating it as a pure
/// distance: it is the attenuation scale.</para>
///
/// <para>Only six stock Xonotic maps ship one of these — <c>bromine</c>, <c>fuse</c>, <c>glowplant</c>,
/// <c>implosion</c>, <c>runningman</c>, <c>techassault</c> — and no Xonotic effects preset below <c>ultra</c>
/// turns the mode on. So this is fidelity for the maps that authored it and for custom content, not a
/// prerequisite for looking like Xonotic.</para>
/// </summary>
public static class RtLightsFile
{
    /// <summary>DP <c>LIGHTFLAG_NORMALMODE</c> — the light participates when realtime world lighting is OFF.</summary>
    public const int FlagNormalMode = 1;

    /// <summary>DP <c>LIGHTFLAG_REALTIMEMODE</c> — the light participates when realtime world lighting is ON.</summary>
    public const int FlagRealtimeMode = 2;

    /// <summary>One authored world light. Field meanings and defaults are DP's <c>dlight_t</c>.</summary>
    public sealed class Light
    {
        /// <summary>Position, Quake axes.</summary>
        public Vector3 Origin;

        /// <summary>Attenuation scale in Quake units (DP: "brightness, not really radius anymore").</summary>
        public float Radius;

        /// <summary>Colour; typically 0..1 per channel but may exceed 1 for an overbright light.</summary>
        public Vector3 Color = Vector3.One;

        /// <summary>Light-style index to modulate brightness by (0 = steady).</summary>
        public int Style;

        /// <summary>False when the line carried a leading <c>!</c> — DP's "no shadow" marker.</summary>
        public bool Shadow = true;

        /// <summary>Cubemap light filter (a gobo), empty for none. Relative to <c>cubemaps/</c> in DP.</summary>
        public string CubemapName = string.Empty;

        /// <summary>Corona flare intensity; 0 = no flare.</summary>
        public float Corona;

        /// <summary>Orientation, Quake pitch/yaw/roll. Only meaningful with a cubemap filter.</summary>
        public Vector3 Angles;

        /// <summary>Corona radius as a fraction of <see cref="Radius"/> (DP default 0.25).</summary>
        public float CoronaSizeScale = 0.25f;

        /// <summary>Per-light weighting of the ambient term (DP default 0).</summary>
        public float AmbientScale;

        /// <summary>Per-light weighting of the diffuse term (DP default 1).</summary>
        public float DiffuseScale = 1f;

        /// <summary>Per-light weighting of the specular term (DP default 1).</summary>
        public float SpecularScale = 1f;

        /// <summary>LIGHTFLAG_* bits; DP default is <see cref="FlagRealtimeMode"/>.</summary>
        public int Flags = FlagRealtimeMode;
    }

    /// <summary>
    /// Parse a <c>.rtlights</c> file. Unparseable lines are skipped rather than fatal — DP does the same, and
    /// a single bad line in a shipped map should cost that one light, not the whole file.
    /// <paramref name="skipped"/> reports how many were dropped so a caller can log it.
    /// </summary>
    public static List<Light> Parse(string text, out int skipped)
    {
        var lights = new List<Light>();
        skipped = 0;
        if (string.IsNullOrEmpty(text))
            return lights;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim('\r', ' ', '\t');
            if (line.Length == 0)
                continue;
            Light? l = ParseLine(line);
            if (l is null)
                skipped++;
            else
                lights.Add(l);
        }
        return lights;
    }

    /// <summary>Parse one line; null when it does not hold at least the 8-field short form.</summary>
    public static Light? ParseLine(string line)
    {
        // The cubemap name is a quoted field that may contain spaces, so a plain Split would mis-tokenise a
        // line like `... 0 "my cube" 0 ...`. Tokenise with quote awareness instead.
        List<string> t = Tokenize(line);
        if (t.Count < 8)
            return null;

        var l = new Light();
        string first = t[0];
        if (first.StartsWith('!'))
        {
            l.Shadow = false;          // DP's leading-! = this light casts no shadow
            first = first[1..];
        }

        if (!F(first, out float x) || !F(t[1], out float y) || !F(t[2], out float z)
            || !F(t[3], out float radius)
            || !F(t[4], out float r) || !F(t[5], out float g) || !F(t[6], out float b)
            || !I(t[7], out int style))
            return null;

        l.Origin = new Vector3(x, y, z);
        l.Radius = radius;
        l.Color = new Vector3(r, g, b);
        l.Style = style;

        if (t.Count >= 13)
        {
            l.CubemapName = t[8].Trim('"');
            if (F(t[9], out float corona)) l.Corona = corona;
            if (F(t[10], out float ax) && F(t[11], out float ay) && F(t[12], out float az))
                l.Angles = new Vector3(ax, ay, az);
        }
        if (t.Count >= 18)
        {
            if (F(t[13], out float cs)) l.CoronaSizeScale = cs;
            if (F(t[14], out float amb)) l.AmbientScale = amb;
            if (F(t[15], out float dif)) l.DiffuseScale = dif;
            if (F(t[16], out float spec)) l.SpecularScale = spec;
            if (I(t[17], out int flags)) l.Flags = flags;
        }
        return l;
    }

    /// <summary>
    /// Write lights back in DP's format, choosing the same shortest-adequate form per line that
    /// <c>R_Shadow_SaveWorldLights</c> chooses, so a file this port writes is byte-comparable with one DP
    /// wrote for the same lights and remains loadable by DarkPlaces itself.
    /// </summary>
    public static string Write(IEnumerable<Light> lights)
    {
        var sb = new StringBuilder();
        foreach (Light l in lights)
        {
            string bang = l.Shadow ? "" : "!";
            bool nonDefaultScales = l.CoronaSizeScale != 0.25f || l.AmbientScale != 0f
                                    || l.DiffuseScale != 1f || l.SpecularScale != 1f
                                    || l.Flags != FlagRealtimeMode;
            bool hasExtras = l.CubemapName.Length > 0 || l.Corona != 0f
                             || l.Angles.X != 0f || l.Angles.Y != 0f || l.Angles.Z != 0f;

            if (nonDefaultScales)
                sb.Append(CultureInfo.InvariantCulture,
                    $"{bang}{F6(l.Origin.X)} {F6(l.Origin.Y)} {F6(l.Origin.Z)} {F6(l.Radius)} " +
                    $"{F6(l.Color.X)} {F6(l.Color.Y)} {F6(l.Color.Z)} {l.Style} \"{l.CubemapName}\" " +
                    $"{F6(l.Corona)} {F6(l.Angles.X)} {F6(l.Angles.Y)} {F6(l.Angles.Z)} " +
                    $"{F6(l.CoronaSizeScale)} {F6(l.AmbientScale)} {F6(l.DiffuseScale)} {F6(l.SpecularScale)} " +
                    $"{l.Flags}\n");
            else if (hasExtras)
                sb.Append(CultureInfo.InvariantCulture,
                    $"{bang}{F6(l.Origin.X)} {F6(l.Origin.Y)} {F6(l.Origin.Z)} {F6(l.Radius)} " +
                    $"{F6(l.Color.X)} {F6(l.Color.Y)} {F6(l.Color.Z)} {l.Style} \"{l.CubemapName}\" " +
                    $"{F6(l.Corona)} {F6(l.Angles.X)} {F6(l.Angles.Y)} {F6(l.Angles.Z)}\n");
            else
                sb.Append(CultureInfo.InvariantCulture,
                    $"{bang}{F6(l.Origin.X)} {F6(l.Origin.Y)} {F6(l.Origin.Z)} {F6(l.Radius)} " +
                    $"{F6(l.Color.X)} {F6(l.Color.Y)} {F6(l.Color.Z)} {l.Style}\n");
        }
        return sb.ToString();
    }

    /// <summary>DP writes every float with <c>%f</c>, i.e. six decimal places.</summary>
    private static string F6(float v) => v.ToString("F6", CultureInfo.InvariantCulture);

    /// <summary>Whitespace tokeniser that keeps a quoted field (the cubemap name) in one piece.</summary>
    private static List<string> Tokenize(string line)
    {
        var outp = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length) break;
            if (line[i] == '"')
            {
                int end = line.IndexOf('"', i + 1);
                if (end < 0) { outp.Add(line[i..]); break; }
                outp.Add(line[i..(end + 1)]);
                i = end + 1;
            }
            else
            {
                int start = i;
                while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
                outp.Add(line[start..i]);
            }
        }
        return outp;
    }

    private static bool F(string s, out float v) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static bool I(string s, out int v)
    {
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
            return true;
        // DP writes style with %d but some hand-edited files carry a float; accept it rather than drop the light.
        if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
        {
            v = (int)f;
            return true;
        }
        return false;
    }
}
