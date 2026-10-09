using Godot;
using VortexArena.Game.Loaders;
using VortexArena.Game.Menu;

namespace VortexArena.Game.Client;

/// <summary>
/// The native game's colour pipeline: DarkPlaces' arithmetic, the same one a legacy session uses
/// (planning/specs/legacy-compat.md section 15, and section 16 for the native game).
///
/// <para>Until October 2026 the native game decoded every texel and lightmap value to linear light, multiplied
/// there and encoded the product. DarkPlaces as Xonotic configures it (<c>vid_sRGB 0</c>,
/// <c>mod_q3bsp_sRGBlightmaps 0</c>) converts nothing: texel times lightmap times two on the stored values is the
/// pixel, and every blend happens on the values the screen shows. On the same view the old native picture had
/// about half of DarkPlaces' luminance and more saturation (stormkeep: 0.49). The native game now draws
/// Xonotic's maps, models, particles, decals, sky, fog and dynamic lights the way DarkPlaces draws them.</para>
///
/// <para><b>The switch.</b> <c>r_darkplaces_colour</c> (default 1). 0 is the previous native look, kept for one
/// release. It is read once, when the client starts: materials, sky, particle and decal shaders are generated
/// for one convention or the other, so a change takes effect at the next start.</para>
///
/// <para><b>What it sets</b> (each is what <c>LegacyColour</c> sets for a session): the world shader's
/// <c>world_gamma_space</c>, the model shader's <c>model_light_gamma = 2</c>, DarkPlaces' light-grid scale,
/// <see cref="DisplayFramebuffer"/> (the 3D buffer holds display values), <c>AssetSystem.DarkPlacesRules</c>
/// (a Quake 3 shader script becomes the one stage DarkPlaces draws), and the lightmap intensity of light
/// style 0 (1.03125). 2D - the menu and the HUD - is drawn after the 3D buffer is resolved and is not touched.</para>
/// </summary>
public static class NativeColour
{
    /// <summary>The cvar: 1 = DarkPlaces' colour arithmetic (default), 0 = the pre-October-2026 native look.</summary>
    public const string CvarName = "r_darkplaces_colour";

    /// <summary>True when the native game draws with DarkPlaces' colour arithmetic. Fixed at start.</summary>
    public static bool Enabled { get; private set; }

    /// <summary>
    /// r_refdef.scene.lightmapintensity of a Quake 3 level: the value of light style 0, "m" on every stock
    /// level, 12 * 22 / 256 (cl_main.c CL_RelinkLightFlashes; gl_rmain.c R_UpdateVariables). It scales every
    /// lightmapped and vertex-lit surface and every grid-lit model. 1 with the old look.
    /// </summary>
    public static float LightmapIntensity => Enabled ? VortexArena.Formats.Materials.DpColour.LightStyleValue("m", 0) : 1f;

    private static bool s_applied;

    /// <summary>Reads the cvar and sets the process-wide switches. Called once the cvar store holds the
    /// configuration and the command-line pins, before anything of a level is loaded.</summary>
    public static void ApplyAtBoot(AssetSystem? sharedAssets)
    {
        if (s_applied) return;
        s_applied = true;
        string text = MenuState.Cvars.GetString(CvarName);
        Enabled = string.IsNullOrWhiteSpace(text) || MenuState.Cvars.GetFloat(CvarName) != 0f;
        if (sharedAssets is not null) sharedAssets.DarkPlacesRules = Enabled;
        Push(Enabled);
        GD.Print(Enabled
            ? "[NativeColour] DarkPlaces colour arithmetic (r_darkplaces_colour 1)."
            : "[NativeColour] previous native colour (r_darkplaces_colour 0): linear-light combine.");
    }

    /// <summary>Sets the shared shaders' switches. A legacy session turns them on for itself and hands them
    /// back here when it ends (<c>LegacyColour.Leave</c>), so they return to what the native game asked for.</summary>
    public static void Push(bool on)
    {
        WorldTint.EnsureRegistered();
        RenderingServer.GlobalShaderParameterSet(LightmapShader.GammaSpaceUniform, on ? 1f : 0f);
        WorldTint.SetLegacyModelLight(on);
        ModelLighting.SetDarkPlacesScale(on);
        DisplayFramebuffer.Set(on);
    }

    private static double s_clock;
    private static bool s_sunChecked;

    /// <summary>Set by the model-shadow pass (r_shadows): it reuses the scene's sun as its shadow-casting light.</summary>
    public static bool SunWanted { get; set; }

    /// <summary>
    /// Once per client frame: the clock of the DarkPlaces-rule surface shaders (<c>dp_time</c>, cl.time in
    /// DarkPlaces: tcMod scroll, animMap, deformVertexes). A legacy session drives the same global from its own
    /// clock; the native game counts the time its world has been processed, so a paused game holds still.
    /// </summary>
    public static void Frame(double delta, Node? world = null)
    {
        if (!Enabled) return;
        if (!s_sunChecked && world is not null && world.IsInsideTree())
        {
            // The scene's generic sun (NetGame.AddLight) lit the models of the earlier look. Every model is now
            // lit from the level's light grid by its shader, which also hands the engine an albedo for the
            // dynamic lights - the sun would be added on top of it. DarkPlaces has no such light.
            s_sunChecked = true;
            if (world.GetTree().Root.FindChild("Sun", true, false) is DirectionalLight3D sun) sun.Visible = SunWanted;
        }
        ShadowSettings.PollModelShadows(world);
        s_clock += delta;
        RenderingServer.GlobalShaderParameterSet(DpSurfaceShader.TimeUniform, (float)s_clock);
    }

    /// <summary>
    /// The environment of a level whose 3D buffer holds display values: the colour-correction table that cancels
    /// the output transform's encoding, and no ambient light of the engine's own (every model is lit from the
    /// level's light grid by its shader, as in DarkPlaces). No-op with the old look.
    /// </summary>
    public static void ApplyEnvironment(Godot.Environment? env)
    {
        s_sunChecked = false;
        ShadowSettings.ResetLevel();
        if (env is null || !DisplayFramebuffer.Active) return;
        env.AdjustmentEnabled = true;
        env.AdjustmentBrightness = 1f;
        env.AdjustmentContrast = 1f;
        env.AdjustmentSaturation = 1f;
        env.AdjustmentColorCorrection = DisplayFramebuffer.InverseOutputTable();
        env.AmbientLightSource = Godot.Environment.AmbientSource.Disabled;
        env.AmbientLightEnergy = 0f;
        if (env.FogEnabled) env.FogLightColor = DisplayFramebuffer.ForEngine(env.FogLightColor);
    }
}
