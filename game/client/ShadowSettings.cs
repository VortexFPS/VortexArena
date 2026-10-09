using System;
using Godot;
using VortexArena.Formats.Lighting;
using VortexArena.Game.Menu;

namespace VortexArena.Game.Client;

/// <summary>
/// DarkPlaces' shadow settings on this engine's shadow maps. Two things DarkPlaces calls shadows:
///
/// <para><b>Light shadows</b> (<c>r_shadow_shadowmapping</c> with <c>r_shadow_realtime_dlight_shadows</c> and
/// <c>r_shadow_realtime_world_shadows</c>): a realtime light's own shadow map; a shadowed pixel loses that
/// light's whole contribution. <see cref="LightBudget"/> decides which lights cast; this class makes the level
/// cast when any of them does (DarkPlaces' casters are the level's surfaces and every entity with
/// RENDER_SHADOW) and maps <c>r_shadow_shadowmapping_filterquality</c> to the engine's filter.</para>
///
/// <para><b>Model shadows</b> (<c>r_shadows</c>, 0 by default and never set by Xonotic's menu or presets): one
/// orthographic shadow map of the models, thrown along <c>r_shadows_throwdirection</c> (straight down by
/// default; values 1 and 2 are the same code in this DarkPlaces), sampled by every lit surface, which keeps
/// <c>1 - r_shadows_darken</c> of its lit colour in shadow. Here the scene's directional light is that map:
/// it lights nothing (the world and model shaders read only its shadow term, <c>dp_model_shadow</c>), its
/// casters are everything but the level (<see cref="WorldCellLayer"/>), and it exists only with DarkPlaces'
/// colour arithmetic. What differs from DarkPlaces: the map is the engine's (finer than DarkPlaces' 4 units a
/// texel, softened instead), a caster throws its shadow at any distance below it where DarkPlaces stops at
/// <c>r_shadows_throwdistance</c>, and brush models cast (DarkPlaces: only with r_shadows_castfrombmodels).</para>
/// </summary>
public static class ShadowSettings
{
    // The level's cell meshes (MapLoader), for switching their casting. Freed ones are dropped when walked.
    private static readonly System.Collections.Generic.List<GeometryInstance3D> s_cells = new();

    /// <summary>Called by MapLoader for every cell mesh of a level it builds (any thread).</summary>
    public static void RegisterWorldCell(GeometryInstance3D cell)
    {
        lock (s_cells) s_cells.Add(cell);
        s_seeded = false;   // the next poll applies the current setting to the new level
    }

    /// <summary>The render layer the level's cells are on with DarkPlaces' colour arithmetic (layer 12, alone):
    /// every camera and light sees it, and the model-shadow light leaves it out of its casters.</summary>
    public const uint WorldCellLayer = 1u << 11;

    /// <summary>True while the level's cells cast shadows (a light shadow option is on).</summary>
    public static bool WorldCasts { get; private set; }

    private static bool s_seeded;
    private static int s_filter = -99;
    private static int s_atlas;

    /// <summary>Once per frame from <see cref="LightBudget"/> (inside its profiler scope).</summary>
    public static void Poll(Node any, bool lightShadowsWanted)
    {
        if (!s_seeded || lightShadowsWanted != WorldCasts)
        {
            s_seeded = true;
            WorldCasts = lightShadowsWanted;
            GeometryInstance3D.ShadowCastingSetting setting = lightShadowsWanted || CvarOn("r_shadow_world_casts", false)
                ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off;
            lock (s_cells)
            {
                for (int i = s_cells.Count - 1; i >= 0; i--)
                {
                    GeometryInstance3D cell = s_cells[i];
                    if (!GodotObject.IsInstanceValid(cell)) { s_cells.RemoveAt(i); continue; }
                    if (cell.CastShadow != setting) cell.CastShadow = setting;
                }
            }
        }

        int filter = (int)DpLightModel.FilterFor((int)Cvar("r_shadow_shadowmapping_filterquality", -1f));
        if (filter != s_filter)
        {
            s_filter = filter;
            RenderingServer.ShadowQuality quality = filter switch
            {
                0 => RenderingServer.ShadowQuality.Hard,
                1 => RenderingServer.ShadowQuality.SoftLow,
                _ => RenderingServer.ShadowQuality.SoftMedium,
            };
            RenderingServer.PositionalSoftShadowFilterSetQuality(quality);
            RenderingServer.DirectionalSoftShadowFilterSetQuality(quality);
        }

        // r_shadow_shadowmapping_texturesize is DarkPlaces' atlas (8192: 2x3 blocks of at most 512 a light). The
        // engine's atlas gives a light a quadrant slot instead; 4096 holds the same number of lights at the same
        // 512 a face, so that is the ceiling here.
        int atlas = Math.Clamp(Mathf.NearestPo2((int)Cvar("r_shadow_shadowmapping_texturesize", 8192f)), 1024, 4096);
        if (atlas != s_atlas && any.IsInsideTree() && any.GetViewport() is { } viewport)
        {
            s_atlas = atlas;
            if (viewport.PositionalShadowAtlasSize != atlas) viewport.PositionalShadowAtlasSize = atlas;
        }
    }

    // ---- model shadows (r_shadows) ---------------------------------------------------------------------------

    private static WeakReference<DirectionalLight3D>? s_sun;
    private static float s_darkenApplied;
    private static bool s_modelShadowsOn;
    private static Vector3 s_directionApplied;

    /// <summary>Forget the previous level's sun (called when a level's environment is attached).</summary>
    public static void ResetLevel()
    {
        s_sun = null;
        s_modelShadowsOn = false;
        if (s_darkenApplied != 0f)
        {
            s_darkenApplied = 0f;
            RenderingServer.GlobalShaderParameterSet("dp_model_shadow", 0f);
        }
    }

    /// <summary>Once per client frame (NativeColour.Frame), only with DarkPlaces' colour arithmetic.</summary>
    public static void PollModelShadows(Node? world)
    {
        if (world is null || !world.IsInsideTree()) return;
        // r_shadow.c R_Shadow_PrepareModelShadows: r_shadows above 0, with the shadow-map mode on.
        bool on = Cvar("r_shadows", 0f) > 0f && Cvar("r_shadow_shadowmapping", 1f) != 0f;
        float darken = on ? Math.Clamp(Cvar("r_shadows_darken", 0.5f), 0f, 1f) : 0f;
        if (darken != s_darkenApplied)
        {
            s_darkenApplied = darken;
            RenderingServer.GlobalShaderParameterSet("dp_model_shadow", darken);
        }
        Vector3 direction = ThrowDirection();
        if (on == s_modelShadowsOn && (!on || direction == s_directionApplied)) return;

        DirectionalLight3D? sun = null;
        if (s_sun is null || !s_sun.TryGetTarget(out sun) || !GodotObject.IsInstanceValid(sun))
        {
            sun = world.GetTree().Root.FindChild("Sun", true, false) as DirectionalLight3D;
            if (sun is null) return;   // not built yet: tried again next frame
            s_sun = new WeakReference<DirectionalLight3D>(sun);
        }
        s_modelShadowsOn = on;
        s_directionApplied = direction;
        NativeColour.SunWanted = on;
        sun.Visible = on;
        sun.ShadowEnabled = on;
        if (!on) return;
        sun.LightEnergy = 1f;
        sun.LightColor = Colors.White;
        sun.ShadowCasterMask = ~WorldCellLayer & 0xFFFFF;
        sun.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits;
        sun.DirectionalShadowMaxDistance = 2048f;
        sun.DirectionalShadowFadeStart = 0.9f;
        sun.ShadowBias = 0.4f;
        sun.ShadowNormalBias = 2f;
        sun.ShadowBlur = 5f;
        // DarkPlaces' map is a quarter texel a unit (r_shadows_shadowmapscale 0.25 of the atlas quarter): coarse and
        // soft. A small map with a wide blur is the nearest this engine's filter comes to it.
        RenderingServer.DirectionalShadowAtlasSetSize(1024, true);
        // A directional light shines along its -Z axis.
        Vector3 up = MathF.Abs(direction.Y) > 0.99f ? Vector3.Forward : Vector3.Up;
        sun.GlobalBasis = Basis.LookingAt(direction, up);
    }

    /// <summary>r_shadows_throwdirection (a Quake vector, "0 0 -1" by default) in this engine's axes.</summary>
    private static Vector3 ThrowDirection()
    {
        string text = MenuState.Cvars.GetString("r_shadows_throwdirection");
        System.Numerics.Vector3 quake = new(0f, 0f, -1f);
        if (!string.IsNullOrWhiteSpace(text) && VortexArena.Common.Framework.VecParse.TryParseFloats(text, min: 3, out float[] p))
            quake = new System.Numerics.Vector3(p[0], p[1], p[2]);
        if (quake.LengthSquared() < 1e-8f) quake = new System.Numerics.Vector3(0f, 0f, -1f);
        return Coords.ToGodot(System.Numerics.Vector3.Normalize(quake));
    }

    private static bool CvarOn(string name, bool fallback)
    {
        string s = MenuState.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : MenuState.Cvars.GetFloat(name) != 0f;
    }

    private static float Cvar(string name, float fallback)
    {
        string s = MenuState.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : MenuState.Cvars.GetFloat(name);
    }
}
