// DarkPlaces' realtime light arithmetic (r_shadow.c, shader_glsl.h MODE_LIGHTSOURCE) in engine-free form: what
// the game's light shaders and its light and shadow settings are written from, and what the tests check.
using System;

namespace VortexArena.Formats.Lighting;

public static class DpLightModel
{
    /// <summary>
    /// The falloff of a realtime light at <paramref name="distanceOverRadius"/> (r_shadow.c
    /// R_Shadow_MakeTextures_SamplePoint with the default r_shadow_lightattenuationlinearscale 2 and
    /// r_shadow_lightattenuationdividebias 1): <c>(1 - d) * 2 / (1 + d * d)</c>, at most 1, zero from the radius on.
    /// </summary>
    public static float Attenuation(float distanceOverRadius, float linearScale = 2f, float divideBias = 1f)
    {
        float d = distanceOverRadius;
        if (!(d < 1f)) return 0f;
        if (d < 0f) d = 0f;
        return Math.Clamp((1f - d) * linearScale / (divideBias + d * d), 0f, 1f);
    }

    /// <summary>
    /// The engine's own range window of an omni light whose attenuation exponent is 0, <c>(1 - (d/r)^4)^2</c>:
    /// what a light shader is handed, and from which it takes <c>d/r</c> back (<see cref="DistanceFromWindow"/>).
    /// </summary>
    public static float EngineWindow(float distanceOverRadius)
    {
        float d = Math.Clamp(distanceOverRadius, 0f, 1f);
        float d4 = d * d * d * d;
        return (1f - d4) * (1f - d4);
    }

    /// <summary>The inverse of <see cref="EngineWindow"/>: d/r = (1 - sqrt(window))^(1/4).</summary>
    public static float DistanceFromWindow(float window)
        => MathF.Pow(MathF.Max(1f - MathF.Sqrt(Math.Clamp(window, 0f, 1f)), 0f), 0.25f);

    // ---- the three per-light scales, carried to the light shader in the light's specular parameter ----------
    // A light shader is told one number about a light besides its colour: SPECULAR_AMOUNT (twice the light's
    // specular property). DarkPlaces' lights have three (ambientscale, diffusescale, specularscale: the last
    // three fields of an .rtlights line), so they travel packed: ambient in steps of 1/100 (0..1), diffuse in
    // steps of 1/50 (0..2), specular as it is (0..<8). A value below 8 is an unpacked light: ambient 0,
    // diffuse 1, specular that value.

    public const float PackBase = 8f;
    public const float DiffuseSteps = 101f;

    public static float PackScales(float ambient, float diffuse, float specular)
    {
        float a = MathF.Round(Math.Clamp(ambient, 0f, 1f) * 100f);
        float d = MathF.Round(Math.Clamp(diffuse, 0f, 2f) * 50f);
        float s = Math.Clamp(specular, 0f, 7.99f);
        return PackBase * (1f + d + DiffuseSteps * a) + s;
    }

    public static (float Ambient, float Diffuse, float Specular) UnpackScales(float packed)
    {
        if (packed < PackBase) return (0f, 1f, MathF.Max(packed, 0f));
        float whole = MathF.Floor(packed / PackBase);
        float s = packed - whole * PackBase;
        whole -= 1f;
        float a = MathF.Floor(whole / DiffuseSteps);
        float d = whole - a * DiffuseSteps;
        return (a / 100f, d / 50f, s);
    }

    /// <summary>
    /// What a surface takes from one light, as a factor on the light's colour (shader_glsl.h MODE_LIGHTSOURCE and
    /// r_shadow.c R_Shadow_RenderLighting): <c>(ambient + diffuse * sat(N.L)) * attenuation</c>, where with
    /// <c>r_shadow_usenormalmap 0</c> the diffuse scale is added to the ambient one and nothing depends on N.L.
    /// </summary>
    public static float DiffuseFactor(float ambientScale, float diffuseScale, float nDotL, float attenuation, bool useNormalMap = true)
    {
        float ambient = ambientScale, diffuse = diffuseScale;
        if (!useNormalMap) { ambient += diffuse; diffuse = 0f; }
        return (ambient + diffuse * Math.Clamp(nDotL, 0f, 1f)) * attenuation;
    }

    /// <summary>
    /// GDShader helpers for a light() function on a buffer of display values. <c>dp_light_att</c> turns the
    /// engine's ATTENUATION (range window times shadow) into DarkPlaces' falloff; <c>dp_light_scales</c> unpacks
    /// SPECULAR_AMOUNT (<see cref="PackScales"/>; the engine hands over twice the property).
    /// </summary>
    public const string ShaderFunctions = @"
float dp_light_att(float engine_attenuation) {
    float q = pow(max(1.0 - sqrt(clamp(engine_attenuation, 0.0, 1.0)), 0.0), 0.25);
    return clamp((1.0 - q) * 2.0 / (1.0 + q * q), 0.0, 1.0);
}
vec3 dp_light_scales(float specular_amount) {
    float packed = specular_amount * 0.5;
    if (packed < 8.0) { return vec3(0.0, 1.0, max(packed, 0.0)); }
    float whole = floor(packed / 8.0);
    float s = packed - whole * 8.0;
    whole -= 1.0;
    float a = floor(whole / 101.0);
    return vec3(a / 100.0, (whole - a * 101.0) / 50.0, s);
}
";

    // ---- shadow settings ------------------------------------------------------------------------------------

    /// <summary>The engine's soft-shadow filter for a DarkPlaces configuration: 0 hard, 1 soft-low, 2 soft-medium.</summary>
    public enum ShadowFilter { Hard = 0, SoftLow = 1, SoftMedium = 2 }

    /// <summary>
    /// r_shadow_shadowmapping_filterquality to a filter (r_shadow.c R_Shadow_SetShadowMode): -1 picks one
    /// percentage-closer level on the hardware this game runs on (3x3 bilinear taps), 0 and 1 are the bare
    /// comparison, 2 and 3 are one level, 4 is two (5x5).
    /// </summary>
    public static ShadowFilter FilterFor(int filterQuality) => filterQuality switch
    {
        < 0 => ShadowFilter.SoftLow,
        0 or 1 => ShadowFilter.Hard,
        2 or 3 => ShadowFilter.SoftLow,
        _ => ShadowFilter.SoftMedium,
    };

    /// <summary>
    /// Does a light cast shadows (r_shadow.c R_Shadow_PrepareLight: castshadows)? Shadow mapping must be on
    /// (r_shadow_shadowmapping: without it this DarkPlaces draws no light shadows at all), the light must not be a
    /// no-shadow light (the '!' of an .rtlights line), and its class must be switched on: a level's light by
    /// r_shadow_realtime_world_shadows, a dynamic one by r_shadow_realtime_dlight_shadows.
    /// </summary>
    public static bool CastsShadow(bool shadowMapping, bool lightHasShadow, bool isWorldLight, bool worldShadows, bool dlightShadows)
        => shadowMapping && lightHasShadow && (isWorldLight ? worldShadows : dlightShadows);

    /// <summary>
    /// Is a level's light drawn (r_shadow.c R_Shadow_PrepareLights: flag = rtworld ? LIGHTFLAG_REALTIMEMODE :
    /// LIGHTFLAG_NORMALMODE)? A light of an .rtlights file has REALTIMEMODE (2) unless the line says otherwise.
    /// </summary>
    public static bool WorldLightDrawn(int flags, bool realtimeWorld)
        => (flags & (realtimeWorld ? RtLightsFile.FlagRealtimeMode : RtLightsFile.FlagNormalMode)) != 0;

    /// <summary>The corona of a light (r_shadow.c R_Shadow_DrawCoronas): colour scale and half-size.</summary>
    public static (float ColourScale, float HalfSize) Corona(float corona, float coronaSizeScale, float radius, float r_coronas)
        => (corona * r_coronas * 0.25f, radius * coronaSizeScale);

    /// <summary>
    /// The model shadows' darkening (r_shadows; shader_glsl.h: mix(1 - r_shadows_darken, 1, lit)): the factor on
    /// the lit colour for a fraction <paramref name="lit"/> of unshadowed samples.
    /// </summary>
    public static float ModelShadowFactor(float darken, float lit)
    {
        float floor = Math.Clamp(1f - darken, 0f, 1f);
        return floor + (1f - floor) * Math.Clamp(lit, 0f, 1f);
    }
}
