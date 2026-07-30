using Godot;
using VortexArena.Formats.Bsp;
using VortexArena.Game.Menu;

namespace VortexArena.Game;

/// <summary>
/// A dynamic, whole-map colour tint — the engine-wide colormod for world geometry, plus a separate, usually
/// subtler grade for everything else (players / weapons / pickups). Both are real-time: set a value and every
/// affected surface re-tints on the next frame, with no per-material work.
///
/// <para><b>How it reaches the surfaces.</b> Two Godot <i>global shader parameters</i>
/// (<see cref="MapTintUniform"/> / <see cref="EntityTintUniform"/>, registered once in
/// <see cref="EnsureRegistered"/>) are read by the world and skin shaders as a final multiply. A global shader
/// parameter is broadcast to <b>every</b> material that declares it, so one
/// <see cref="RenderingServer.GlobalShaderParameterSet(StringName, Variant)"/> call re-tints the whole world at
/// once. The shaders that opt in:
/// <list type="bullet">
///   <item><see cref="Loaders.LightmapShader"/> — opaque + translucent + vertex-lit world surfaces (the bulk of a
///   map: walls, floors, ceilings, glass, grates) read <c>map_tint</c>.</item>
///   <item>The generated animated-stage shader (<see cref="Loaders.ShaderCompiler"/> — scrolling/anim textures,
///   e.g. lava) reads <c>map_tint</c>.</item>
///   <item><see cref="Loaders.PlayerSkinShader"/> — player/weapon/item skins read <c>entity_tint</c>.</item>
/// </list>
/// Surfaces that compile to a plain <see cref="StandardMaterial3D"/> (a minority of non-lightmapped world faces,
/// and effect/particle renderers) declare neither global and are left untinted — a known, documented gap rather
/// than a hard guarantee.</para>
///
/// <para><b>The tint value</b> is a per-channel multiplier in linear-ish display space: <c>(1,1,1)</c> is
/// identity (no tint). A <see cref="SetMapTint(Color, float)"/> call blends from white toward the chosen colour
/// by a <c>strength</c> in 0..1 (<c>strength 0</c> = off, <c>1</c> = full colour multiply), so a red tint at 0.6
/// desaturates the world toward red without going fully monochrome. The strength is folded into the multiplier
/// here, so the shaders stay a trivial <c>colour *= map_tint</c> and the registered default <c>(1,1,1)</c> is
/// always a safe identity.</para>
///
/// <para><b>Three drivers, one value.</b>
/// <list type="number">
///   <item><b>Map</b> — a map's <c>worldspawn</c> may declare <c>_map_tint</c>/<c>_map_tint_strength</c> (and
///   <c>_scene_tint</c>/<c>_scene_tint_strength</c>); <see cref="ApplyWorldspawn"/> pushes them as the baseline on
///   load (and resets to identity on a map with no keys, so a tint never carries between maps).</item>
///   <item><b>Code</b> — <see cref="SetMapTint(Color, float)"/> / <see cref="SetEntityTint(Color, float)"/> set
///   the baseline from gameplay (a scripted event, a mutator, a damage flash).</item>
///   <item><b>Cvar</b> — <c>r_map_tint</c>/<c>r_map_tint_strength</c> and <c>r_scene_tint</c>/
///   <c>r_scene_tint_strength</c>, polled live by <see cref="PollCvars"/> (each client frame). While a tint
///   strength cvar is &gt; 0 it <i>overrides</i> the map/code baseline (live editing for testing — e.g.
///   <c>set r_map_tint "1 0 0"; set r_map_tint_strength 0.6</c>); set the strength back to 0 and control returns
///   to the baseline. This is the "primarily so I can test it" path.</item>
/// </list>
/// Precedence is simply: an active cvar wins; otherwise the last baseline (map or code) holds.</para>
/// </summary>
public static class WorldTint
{
    /// <summary>Global-shader-parameter name the world shaders multiply their final colour by (vec3, default white).</summary>
    public static readonly StringName MapTintUniform = "map_tint";

    /// <summary>Global-shader-parameter name the model/skin shaders multiply their albedo by (vec3, default white).</summary>
    public static readonly StringName EntityTintUniform = "entity_tint";

    // The cvar names (registered with defaults in ClientSettings). Colours are "r g b" 0..1 (like worldspawn fog);
    // strength is 0..1 and doubles as the on/off switch for the live override.
    private const string MapTintCvar = "r_map_tint";
    private const string MapTintStrengthCvar = "r_map_tint_strength";
    private const string SceneTintCvar = "r_scene_tint";
    private const string SceneTintStrengthCvar = "r_scene_tint_strength";

    // Model-light gamma toggle (playtest r14 experiment A): rides this class because it is the same
    // machinery — a global shader parameter driven by a live-polled cvar. 1 = grid-lit models use DP's
    // gamma-space light response (see PlayerSkinShader.ModelLightGammaUniform); 0 (default — Bryan
    // preferred the linear look in the r15 A/B) = linear multiply.
    private const string ModelLightGammaCvar = "r_model_light_gamma";

    private static bool _registered;

    // Baseline (map/code-driven) effective multipliers, and the value last actually pushed to each global (so a
    // per-frame poll that computes the same value doesn't spam RenderingServer). A separate "cvar active" flag per
    // channel lets us restore the baseline the moment the override is switched off.
    private static Vector3 _mapBaseline = Vector3.One;
    private static Vector3 _entityBaseline = Vector3.One;
    private static Vector3 _mapApplied = Vector3.One;
    private static Vector3 _entityApplied = Vector3.One;
    private static bool _mapCvarActive;
    private static bool _entityCvarActive;

    /// <summary>
    /// Register the two global shader parameters (idempotent). MUST run before any world/skin shader using them
    /// compiles, or Godot reports an unknown-global error and the surface fails to render — so this is called once
    /// at startup from <c>Main._Ready</c>, well before the first map (and its shaders) load. Both default to the
    /// identity multiplier <c>(1,1,1)</c>, so a map with no tint, or a frame before any value is set, renders
    /// exactly as before.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (_registered)
            return;
        _registered = true;
        RenderingServer.GlobalShaderParameterAdd(
            MapTintUniform, RenderingServer.GlobalShaderParameterType.Vec3, Vector3.One);
        RenderingServer.GlobalShaderParameterAdd(
            EntityTintUniform, RenderingServer.GlobalShaderParameterType.Vec3, Vector3.One);
        RenderingServer.GlobalShaderParameterAdd(
            Loaders.PlayerSkinShader.ModelLightGammaUniform, RenderingServer.GlobalShaderParameterType.Float, 0.0f);
        // Real-time dynamic lights on world surfaces (DP r_shadow_realtime_dlight). Registered here because it
        // rides the same one-call-repaints-the-world mechanism as the tints.
        RenderingServer.GlobalShaderParameterAdd(
            WorldDlightUniform, RenderingServer.GlobalShaderParameterType.Float, 1.0f);
        // The editor's live baked-light controls (EditorWorldShader). Registered here with the other world
        // globals; the editor updates them per frame from its cvars.
        RenderingServer.GlobalShaderParameterAdd(
            "editor_bake_scale", RenderingServer.GlobalShaderParameterType.Float, 0.0043f);
        RenderingServer.GlobalShaderParameterAdd(
            "editor_bake_ambient", RenderingServer.GlobalShaderParameterType.Float, 0.004f);
        RenderingServer.GlobalShaderParameterAdd(
            "editor_bake_gamma", RenderingServer.GlobalShaderParameterType.Float, 1.05f);
        RenderingServer.GlobalShaderParameterAdd(
            "editor_bake_range", RenderingServer.GlobalShaderParameterType.Float, 48f);
        RenderingServer.GlobalShaderParameterAdd(
            "editor_deluxe", RenderingServer.GlobalShaderParameterType.Float, 1.0f);
        _mapApplied = _entityApplied = Vector3.One;
        _gammaApplied = 0f;
    }

    /// <summary>
    /// Set the world (map-surface) tint baseline: blend from white toward <paramref name="color"/> by
    /// <paramref name="strength"/> (0 = off, 1 = full). Applied immediately unless a live cvar override is active
    /// (in which case it becomes the value restored when the override is switched off). This is the public C# API
    /// for code-driven tints and the sink for a map's <c>worldspawn</c> keys.
    /// </summary>
    /// <summary>The world shader's dynamic-light strength uniform (see <see cref="SetWorldDlight"/>).</summary>
    public static readonly StringName WorldDlightUniform = "world_dlight";

    /// <summary>
    /// Strength of real-time point lights on WORLD surfaces — muzzle flashes, explosions, mapper dlights.
    /// 1 is the DP-like default; 0 restores the pre-dlight look exactly, because the baked lightmap lives in
    /// the shader's EMISSION and is untouched either way.
    /// </summary>
    public static void SetWorldDlight(float scale)
    {
        EnsureRegistered();
        RenderingServer.GlobalShaderParameterSet(WorldDlightUniform, Mathf.Max(0f, scale));
    }

    public static void SetMapTint(Color color, float strength)
    {
        _mapBaseline = Effective(color, strength);
        if (!_mapCvarActive)
            PushMap(_mapBaseline);
    }

    /// <summary>
    /// Set the "everything else" (players / weapons / pickups) tint baseline — same blend-from-white semantics as
    /// <see cref="SetMapTint"/>. Kept separate so a map/effect can tint the world strongly while only lightly
    /// grading the dynamic content (or vice versa).
    /// </summary>
    public static void SetEntityTint(Color color, float strength)
    {
        _entityBaseline = Effective(color, strength);
        if (!_entityCvarActive)
            PushEntity(_entityBaseline);
    }

    /// <summary>Reset both tints to identity (no tint). Used when loading a map that declares no tint keys.</summary>
    public static void Reset()
    {
        SetMapTint(Colors.White, 0f);
        SetEntityTint(Colors.White, 0f);
    }

    /// <summary>
    /// Push a map's <c>worldspawn</c> tint keys as the baseline (or identity when the map declares none, so a tint
    /// never bleeds from the previous map). Called from the per-map environment setup
    /// (<c>GameDemo.AddLighting</c> / <c>NetGame.AddLight</c>).
    ///
    /// <para>Recognised keys (all optional): <c>_map_tint</c> / <c>_scene_tint</c> as <c>"r g b"</c> (0..1), and
    /// <c>_map_tint_strength</c> / <c>_scene_tint_strength</c> (0..1). A colour with no explicit strength defaults
    /// to full (1). The keys are read straight off the worldspawn entity here (rather than through
    /// <see cref="MapLoader.Worldspawn"/>) to keep the whole tint feature self-contained.</para>
    /// </summary>
    public static void ApplyWorldspawn(BspData? bsp)
    {
        EnsureRegistered();
        if (bsp is null || bsp.Entities.Count == 0)
        {
            Reset();
            return;
        }
        System.Collections.Generic.IReadOnlyDictionary<string, string> ws = FindWorldspawn(bsp);
        (Color mapColor, float mapStrength) = ReadTintKeys(ws, "_map_tint", "_map_tint_strength");
        (Color sceneColor, float sceneStrength) = ReadTintKeys(ws, "_scene_tint", "_scene_tint_strength");
        SetMapTint(mapColor, mapStrength);
        SetEntityTint(sceneColor, sceneStrength);
    }

    /// <summary>The worldspawn entity (by classname; falls back to the first entity, Quake convention).</summary>
    private static System.Collections.Generic.IReadOnlyDictionary<string, string> FindWorldspawn(BspData bsp)
    {
        foreach (System.Collections.Generic.IReadOnlyDictionary<string, string> ent in bsp.Entities)
            if (ent.TryGetValue("classname", out string? cn) && cn == "worldspawn")
                return ent;
        return bsp.Entities[0];
    }

    /// <summary>Read a (colour, strength) tint pair from the worldspawn keys; a colour with no strength = full (1).</summary>
    private static (Color color, float strength) ReadTintKeys(
        System.Collections.Generic.IReadOnlyDictionary<string, string> ws, string colorKey, string strengthKey)
    {
        bool hasColor = ws.TryGetValue(colorKey, out string? c) && !string.IsNullOrWhiteSpace(c);
        Color color = hasColor ? ParseRgb(c!) : Colors.White;
        float strength = 0f;
        if (ws.TryGetValue(strengthKey, out string? s) && !string.IsNullOrWhiteSpace(s))
            strength = ParseFloat(s);
        else if (hasColor)
            strength = 1f;
        return (color, strength);
    }

    /// <summary>
    /// Poll the tint cvars (called each client frame). While a strength cvar is &gt; 0 the cvar value overrides the
    /// map/code baseline live (so a console <c>set</c> re-tints instantly — the testing path); the frame the
    /// strength returns to 0 the baseline is restored. Cheap: reads four cvars and only touches RenderingServer
    /// when the resulting multiplier actually changes.
    /// </summary>
    public static void PollCvars()
    {
        EnsureRegistered();

        float mapStrength = CvarF(MapTintStrengthCvar, 0f);
        if (mapStrength > 0f)
        {
            _mapCvarActive = true;
            PushMap(Effective(ParseRgb(CvarS(MapTintCvar)), mapStrength));
        }
        else if (_mapCvarActive)
        {
            _mapCvarActive = false;
            PushMap(_mapBaseline);
        }

        float sceneStrength = CvarF(SceneTintStrengthCvar, 0f);
        if (sceneStrength > 0f)
        {
            _entityCvarActive = true;
            PushEntity(Effective(ParseRgb(CvarS(SceneTintCvar)), sceneStrength));
        }
        else if (_entityCvarActive)
        {
            _entityCvarActive = false;
            PushEntity(_entityBaseline);
        }

        // r_model_light_gamma → the grid-lit models' response-curve toggle (unset = the default 0, linear).
        float gamma = CvarF(ModelLightGammaCvar, 0f) > 0.5f ? 1f : 0f;
        if (gamma != _gammaApplied)
        {
            _gammaApplied = gamma;
            RenderingServer.GlobalShaderParameterSet(Loaders.PlayerSkinShader.ModelLightGammaUniform, gamma);
        }
    }

    private static float _gammaApplied;

    // ---- internals ----------------------------------------------------------------------------------------

    /// <summary>Blend the identity multiplier (white) toward <paramref name="color"/> by a clamped strength.</summary>
    private static Vector3 Effective(Color color, float strength)
    {
        float s = Mathf.Clamp(strength, 0f, 1f);
        return new Vector3(
            Mathf.Lerp(1f, color.R, s),
            Mathf.Lerp(1f, color.G, s),
            Mathf.Lerp(1f, color.B, s));
    }

    private static void PushMap(Vector3 v)
    {
        if (v == _mapApplied)
            return;
        _mapApplied = v;
        RenderingServer.GlobalShaderParameterSet(MapTintUniform, v);
    }

    private static void PushEntity(Vector3 v)
    {
        if (v == _entityApplied)
            return;
        _entityApplied = v;
        RenderingServer.GlobalShaderParameterSet(EntityTintUniform, v);
    }

    /// <summary>Parse an "r g b" cvar string (0..1 per channel) into a colour; white on any parse failure.</summary>
    private static Color ParseRgb(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return Colors.White;
        string[] parts = s.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return Colors.White;
        return new Color(ParseFloat(parts[0]), ParseFloat(parts[1]), ParseFloat(parts[2]));
    }

    private static float ParseFloat(string s)
        => float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f;

    // Read from the SHARED menu/console store (MenuState.Cvars) — NOT the gameplay Api.Cvars facade. In a
    // networked match Api.Cvars is the listen server's PRIVATE store, and the shared→server cvar bridge only
    // mirrors cvars the server already has (Has) — so a client-only cvar like r_map_tint never reaches it and a
    // console `set` would appear to do nothing. The in-game console writes `set`/`seta` to MenuState.Cvars and
    // ClientSettings registers the tint defaults there, so this is the one store that sees every driver.
    // (distinguishes "unset" from "0" the same way ClientWorld's CvarF does.)
    private static string CvarS(string name) => MenuState.Cvars.GetString(name);

    private static float CvarF(string name, float fallback)
    {
        string s = MenuState.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : MenuState.Cvars.GetFloat(name);
    }
}
