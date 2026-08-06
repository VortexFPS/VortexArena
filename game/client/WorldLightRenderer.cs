using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Diagnostics;
using VortexArena.Formats.Bsp;
using VortexArena.Formats.Lighting;
using VortexArena.Game.Loaders;
using VortexArena.Game.Menu;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>
/// Real-time world lighting from a map's authored lights — the port of DarkPlaces'
/// <c>r_shadow_realtime_world</c> (<b>F4</b>), including its cubemap light filters (<b>F7</b>).
///
/// <para><b>What this is and is not.</b> DarkPlaces has three ways to light a world: baked lightmaps (the
/// default), lightmaps plus transient dynamic lights, and <i>realtime world</i> — where a set of static
/// authored lights replaces the lightmaps and casts real shadows. This class is the third. It is fidelity for
/// the maps that authored it, not a prerequisite for looking like Xonotic: <b>no Xonotic effects preset below
/// <c>ultra</c> enables the mode</b>, and only six stock maps ship a <c>.rtlights</c> file
/// (<c>bromine</c>, <c>fuse</c>, <c>glowplant</c>, <c>implosion</c>, <c>runningman</c>, <c>techassault</c>).
/// So the default here is OFF, exactly as in Base.</para>
///
/// <para><b>Sources</b>, in DP's own precedence order (<c>R_Shadow_EditLights_Reload_f</c>): a
/// <c>maps/&lt;name&gt;.rtlights</c> file if present, otherwise the map's own <c>light</c> entities when
/// <c>r_shadow_realtime_world_importlightentitiesfrommap</c> allows it. DP has a third source, a <c>.lights</c>
/// file in hlight format, which no Xonotic map ships and which is not read here.</para>
///
/// <para><b>Lightmaps under realtime world.</b> DP re-admits the baked lightmaps at
/// <c>r_shadow_realtime_world_lightmaps</c> brightness (default <b>0</b> — fully replaced; DP's help text
/// suggests 0.5 for "a tenebrae-like appearance"). That is a global multiply on the world shader's baked
/// term, so it rides a global shader parameter rather than touching every material.</para>
///
/// <para><b>Cubemap filters (F7) are approximated, and the approximation is visible.</b> A DP light may carry
/// a cubemap that multiplies its colour along the light-to-fragment vector — how gobos, stained glass and
/// shaped beams are authored. Godot's <c>OmniLight3D</c> has no projector at all; only <c>SpotLight3D</c>
/// does, and it takes a flat 2-D texture. So a light with a cubemap is built as a SPOT light aimed by the
/// light's own angles, with the cubemap's forward face as its projector. A gobo that was authored to throw
/// light in six directions will throw it in one. Lights with no cubemap are unaffected and stay omni.</para>
/// </summary>
public sealed partial class WorldLightRenderer : Node3D
{
    /// <summary>Global shader param: multiplier on the world's baked lightmap term (DP
    /// <c>r_shadow_realtime_world_lightmaps</c>). 1 = normal; realtime-world mode drops it.</summary>
    public static readonly StringName LightmapScaleUniform = "world_lightmap_scale";

    private sealed class Built
    {
        public Light3D Node = null!;
        public RtLightsFile.Light Src = null!;
    }

    private readonly List<Built> _lights = new();
    private string _loadedMap = string.Empty;
    private bool _appliedOn;
    private float _appliedLightmaps = 1f;

    /// <summary>Set by the host so the loader can read the map's file and resolve gobo textures.</summary>
    public AssetLoader? Assets { get; set; }

    /// <summary>How many world lights are live (console readout / tests).</summary>
    public int Count => _lights.Count;

    /// <summary>(N7) The parsed source lights behind the live nodes - what rtlights_save writes back.</summary>
    public IReadOnlyList<RtLightsFile.Light> SourceLights => _source;

    private readonly List<RtLightsFile.Light> _source = new();

    /// <summary>(N7) Re-read this map's lights from disk without a map change (DP r_editlights_reload).</summary>
    public void ForceReload(string? mapName, BspData? bsp)
    {
        _loadedMap = string.Empty;
        LoadForMap(mapName, bsp);
    }

    /// <summary>
    /// (N7) Replace the light set with one built from the map's own <c>light</c> entities, and KEEP it - the
    /// way you bootstrap a .rtlights for a map that has never had one. Returns how many were built.
    /// </summary>
    public int ImportFromEntities(BspData? bsp)
    {
        List<RtLightsFile.Light> src = ImportFromMapEntities(bsp, out _);
        if (src.Count == 0)
            return 0;
        Clear();
        _source.AddRange(src);
        foreach (RtLightsFile.Light l in src)
            Build(l);
        return _lights.Count;
    }

    private static bool _registered;

    /// <summary>Register the lightmap-dimming global. Called from <see cref="WorldTint.EnsureRegistered"/>.</summary>
    public static void EnsureRegistered()
    {
        if (_registered)
            return;
        _registered = true;
        RenderingServer.GlobalShaderParameterAdd(
            LightmapScaleUniform, RenderingServer.GlobalShaderParameterType.Float, 1.0f);
    }

    // =================================================================================================
    //  Load
    // =================================================================================================

    /// <summary>
    /// Load the world lights for <paramref name="mapName"/>. Safe to call on every map change; a repeat call
    /// for the same map is a no-op. Clears the previous map's lights either way.
    /// </summary>
    public void LoadForMap(string? mapName, BspData? bsp)
    {
        string name = mapName ?? string.Empty;
        if (name == _loadedMap)
            return;
        _loadedMap = name;
        Clear();
        if (name.Length == 0)
            return;

        List<RtLightsFile.Light> src = ReadRtLights(name, out string source);
        if (src.Count == 0 && ImportFromMapAllowed())
            src = ImportFromMapEntities(bsp, out source);

        if (src.Count == 0)
        {
            Log.Info($"[WorldLights] {name}: no .rtlights and no importable light entities — " +
                     "realtime world lighting has nothing to render (this is the normal case).");
            return;
        }

        foreach (RtLightsFile.Light l in src)
            Build(l);

        Log.Info($"[WorldLights] {name}: {_lights.Count} world lights from {source}. " +
                 "Enable with r_shadow_realtime_world 1.");
    }

    /// <summary>Read <c>maps/&lt;name&gt;.rtlights</c> through the VFS, or an empty list.</summary>
    private List<RtLightsFile.Light> ReadRtLights(string mapName, out string source)
    {
        source = ".rtlights";
        if (Assets is null)
            return new List<RtLightsFile.Light>();
        string vpath = $"maps/{mapName}.rtlights";
        string? text;
        try { text = Assets.Vfs.ReadText(vpath); }
        catch { return new List<RtLightsFile.Light>(); }
        if (string.IsNullOrEmpty(text))
            return new List<RtLightsFile.Light>();

        List<RtLightsFile.Light> lights = RtLightsFile.Parse(text, out int skipped);
        if (skipped > 0)
            Log.Info($"[WorldLights] {vpath}: skipped {skipped} unparseable line(s).");
        return lights;
    }

    /// <summary>
    /// DP <c>r_shadow_realtime_world_importlightentitiesfrommap</c> (DP default 1).
    ///
    /// <para><b>Xonotic ships it as 0</b> (<c>xonotic-client.cfg:304</c>), with the reason in its own comment:
    /// "Whether build process uses keepLights is nontransparent and may change, so better make keepLights not
    /// matter." So on stock content this import never runs, even on the many maps whose entity lump still
    /// carries their <c>light</c> entities - stormkeep has 119 of them. That is deliberate on Base's part and
    /// is reproduced here rather than second-guessed: set the cvar to 1 to opt in, which is also what
    /// <c>rtlights_import</c> does explicitly regardless of the cvar.</para>
    /// </summary>
    private static bool ImportFromMapAllowed() =>
        Cvar("r_shadow_realtime_world_importlightentitiesfrommap", 1f) != 0f;

    /// <summary>
    /// Build lights from the map's own <c>light</c> entities — DP's fallback when no <c>.rtlights</c> exists
    /// (<c>R_Shadow_LoadWorldLightsFromMap_LightArghliteTyrlite</c>). The key set is the classic Quake/Q3 one:
    /// <c>origin</c>, <c>light</c>/<c>_light</c> for brightness, <c>_color</c>/<c>color</c> for colour,
    /// <c>style</c>. DP scales the radius by <c>r_editlights_quakelightsizescale</c>, which is kept.
    /// </summary>
    private List<RtLightsFile.Light> ImportFromMapEntities(BspData? bsp, out string source)
    {
        source = "map light entities";
        var outp = new List<RtLightsFile.Light>();
        if (bsp is null)
            return outp;

        float sizeScale = Cvar("r_editlights_quakelightsizescale", 1f);
        foreach (IReadOnlyDictionary<string, string> ent in bsp.Entities)
        {
            if (!ent.TryGetValue("classname", out string? cn) || !cn.StartsWith("light", StringComparison.OrdinalIgnoreCase))
                continue;
            // light_environment/sun entities are a directional source, not a point light; skip rather than
            // drop a 300-unit omni at the sun's arbitrary origin.
            if (cn.Contains("environment", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!ent.TryGetValue("origin", out string? o) || !TryVec(o, out NVec3 origin))
                continue;

            float radius = 300f;
            if (ent.TryGetValue("light", out string? ls) || ent.TryGetValue("_light", out ls))
            {
                // "_light" may be "r g b brightness"; the last component is the brightness in that form.
                string[] parts = ls!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4 && float.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float b4))
                    radius = b4;
                else if (float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float b1))
                    radius = b1;
            }

            var color = NVec3.One;
            if ((ent.TryGetValue("_color", out string? cs) || ent.TryGetValue("color", out cs))
                && TryVec(cs!, out NVec3 c))
            {
                // q3map colours are sometimes 0..255 and sometimes 0..1; normalise the 0..255 form.
                color = (c.X > 1.01f || c.Y > 1.01f || c.Z > 1.01f) ? c / 255f : c;
            }

            int style = 0;
            if (ent.TryGetValue("style", out string? ss))
                int.TryParse(ss, out style);

            outp.Add(new RtLightsFile.Light
            {
                Origin = origin,
                Radius = MathF.Max(1f, radius * sizeScale),
                Color = color,
                Style = style,
                // DP's importer marks imported lights NORMALMODE too, so they can participate without the
                // realtime-world switch. Kept, but the gate below still governs whether they render.
                Flags = RtLightsFile.FlagRealtimeMode | RtLightsFile.FlagNormalMode,
            });
        }
        return outp;
    }

    private static bool TryVec(string s, out NVec3 v)
    {
        v = NVec3.Zero;
        string[] p = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 3) return false;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var st = System.Globalization.NumberStyles.Float;
        if (!float.TryParse(p[0], st, ci, out float x) || !float.TryParse(p[1], st, ci, out float y)
            || !float.TryParse(p[2], st, ci, out float z)) return false;
        v = new NVec3(x, y, z);
        return true;
    }

    // =================================================================================================
    //  Build
    // =================================================================================================

    private void Build(RtLightsFile.Light l)
    {
        // Colour may exceed 1 for an overbright light; split hue from magnitude the way every other light
        // renderer here does, so a bright light brightens rather than clipping to white.
        float maxc = MathF.Max(1f, MathF.Max(l.Color.X, MathF.Max(l.Color.Y, l.Color.Z)));
        var hue = new Color(l.Color.X / maxc, l.Color.Y / maxc, l.Color.Z / maxc);
        float energy = MathF.Min(8f, maxc) * MathF.Max(0.05f, l.DiffuseScale);

        Texture2D? gobo = ResolveGobo(l.CubemapName);
        Light3D node;
        if (gobo is not null)
        {
            // F7 approximation — see the type doc. Aim it with the light's own angles.
            var spot = new SpotLight3D
            {
                SpotRange = l.Radius,
                SpotAngle = 60f,
                SpotAngleAttenuation = 1f,
                LightProjector = gobo,
            };
            spot.RotationDegrees = QuakeAnglesToGodot(l.Angles);
            node = spot;
        }
        else
        {
            node = new OmniLight3D { OmniRange = l.Radius };
        }

        node.Name = $"worldlight{_lights.Count}";
        node.Position = Coords.ToGodot(l.Origin);
        node.LightColor = hue;
        node.LightEnergy = energy;
        node.LightSpecular = MathF.Max(0f, l.SpecularScale);
        node.ShadowEnabled = false;   // the budget grants this, not the light
        node.Visible = false;         // ditto
        AddChild(node);

        LightBudget.Register(node, LightBudget.Role.World, noShadow: !l.Shadow,
                             corona: l.Corona, coronaSize: l.CoronaSizeScale);
        _lights.Add(new Built { Node = node, Src = l });
    }

    /// <summary>
    /// Resolve a cubemap-filter name to something Godot can project. DP names a CUBEMAP; Godot's spot
    /// projector takes a 2-D texture, so try the DP box-order forward face first and fall back to the bare
    /// name (a mapper who supplied a flat gobo). Null = no filter, and the light stays omni.
    /// </summary>
    private Texture2D? ResolveGobo(string cubemapName)
    {
        if (string.IsNullOrWhiteSpace(cubemapName) || Assets is null)
            return null;
        string b = cubemapName.Trim();
        foreach (string cand in new[] { b + "_px", b + "_ft", b })
        {
            Texture2D? t = Assets.Assets.LoadTexture(cand);
            if (t is not null)
                return t;
        }
        Log.Info($"[WorldLights] cubemap filter '{b}' not found — that light renders unfiltered.");
        return null;
    }

    /// <summary>Quake (pitch, yaw, roll) → Godot Euler degrees, matching how the port aims other entities.</summary>
    private static Vector3 QuakeAnglesToGodot(NVec3 a) => new(-a.X, a.Y - 90f, a.Z);

    // =================================================================================================
    //  Per-frame gate
    // =================================================================================================

    public override void _Process(double delta)
    {
        using var _prof = FrameProfiler.Scope("worldlights");

        bool on = Cvar("r_shadow_realtime_world", 0f) != 0f;
        if (on != _appliedOn)
        {
            _appliedOn = on;
            foreach (Built b in _lights)
                if (GodotObject.IsInstanceValid(b.Node))
                    LightBudget.SetOwnerVisible(b.Node, on);
            Log.Info(on
                ? (_lights.Count > 0
                    ? $"[WorldLights] realtime world lighting ON — {_lights.Count} lights."
                    : "[WorldLights] realtime world lighting ON but this map authored no lights — "
                      + "keeping the baked lightmaps at full brightness.")
                : "[WorldLights] realtime world lighting OFF — back to baked lightmaps.");
        }

        // DP re-admits the baked lightmaps at r_shadow_realtime_world_lightmaps brightness while the mode is
        // on (default 0 = fully replaced). With the mode off the lightmaps are the whole lighting, so the
        // multiplier must be 1 regardless of what the cvar says.
        // Dim the baked lightmaps ONLY when there are authored lights to replace them with. The mode being
        // on with an empty light set is the common case, not an edge case: r_shadow_realtime_world is set by
        // the ultra/ultimate presets, while just six stock maps ship a .rtlights file - so on every other map
        // the old code multiplied the lightmaps by r_shadow_realtime_world_lightmaps (default 0) and lit the
        // world with nothing at all. Replacing the lighting with an empty set is never what was wanted.
        bool replacing = on && _lights.Count > 0;
        float lm = replacing ? MathF.Max(0f, Cvar("r_shadow_realtime_world_lightmaps", 0f)) : 1f;
        if (!Mathf.IsEqualApprox(lm, _appliedLightmaps))
        {
            _appliedLightmaps = lm;
            EnsureRegistered();
            RenderingServer.GlobalShaderParameterSet(LightmapScaleUniform, lm);
        }

        if (!on || _lights.Count == 0)
            return;

        // Light styles animate a world light's brightness exactly as they animate a dynlight's radius
        // (DP: currentcolor = color x d_lightstylevalue).
        float t = (float)Time.GetTicksMsec() / 1000f;
        foreach (Built b in _lights)
        {
            if (b.Src.Style == 0 || !GodotObject.IsInstanceValid(b.Node))
                continue;
            float s = VortexArena.Common.Gameplay.LightStyles.Sample(b.Src.Style, t);
            float maxc = MathF.Max(1f, MathF.Max(b.Src.Color.X, MathF.Max(b.Src.Color.Y, b.Src.Color.Z)));
            b.Node.LightEnergy = MathF.Min(8f, maxc) * MathF.Max(0.05f, b.Src.DiffuseScale) * s;
        }
    }

    /// <summary>Free every built light (map change / teardown).</summary>
    public void Clear()
    {
        foreach (Built b in _lights)
        {
            if (!GodotObject.IsInstanceValid(b.Node))
                continue;
            LightBudget.Unregister(b.Node);
            b.Node.QueueFree();
        }
        _lights.Clear();
        _appliedOn = false;
    }

    private static float Cvar(string name, float fallback)
    {
        string s = MenuState.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : MenuState.Cvars.GetFloat(name);
    }
}
