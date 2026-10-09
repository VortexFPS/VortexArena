// DarkPlaces' reflective and refractive water (r_water 1; gl_rmain.c R_Water_*, shader_glsl.h MODE_WATER) in
// engine-free form: the numbers the game's water pass and its shader are written from, and what the tests check.
using System;
using System.Numerics;

namespace VortexArena.Formats.Materials;

public static class DpWaterModel
{
    /// <summary>Xonotic's r_water_refractdistort (xonotic-client.cfg; DarkPlaces' own default is 0.01).</summary>
    public const float XonoticRefractDistort = 0.003f;

    /// <summary>DarkPlaces' r_water_reflectdistort default, which Xonotic leaves alone.</summary>
    public const float DefaultReflectDistort = 0.01f;

    /// <summary>DarkPlaces' MAX_WATERPLANES.</summary>
    public const int MaxPlanes = 16;

    /// <summary>
    /// shader_glsl.h MODE_WATER: <c>Fresnel = pow(min(1, 1 - cos), 2) * ReflectFactor + ReflectOffset</c> with
    /// ReflectFactor = reflectmax - reflectmin and ReflectOffset = reflectmin (gl_rmain.c R_SetupShader_Surface);
    /// <paramref name="cosine"/> is the eye vector's z in the surface's tangent space. With
    /// <c>alphaGen vertex</c> both terms are multiplied by the vertex alpha (USEALPHAGENVERTEX).
    /// </summary>
    public static float Fresnel(float cosine, float reflectMin, float reflectMax, float vertexAlpha = 1f)
    {
        float grazing = MathF.Min(1f, 1f - cosine);
        return grazing * grazing * (reflectMax - reflectMin) * vertexAlpha + reflectMin * vertexAlpha;
    }

    /// <summary>
    /// The colour of the water's background pass: <c>mix(refraction * refractcolor, reflection * reflectcolor,
    /// Fresnel)</c>; with <c>alphaGen vertex</c> the refraction tint is mix(refractcolor, 1, vertex alpha).
    /// </summary>
    public static Vector3 Background(Vector3 refraction, Vector3 reflection, Vector3 refractColor, Vector3 reflectColor, float fresnel, float? vertexAlpha = null)
    {
        Vector3 tint = vertexAlpha is { } a ? Vector3.Lerp(refractColor, Vector3.One, a) : refractColor;
        return Vector3.Lerp(refraction * tint, reflection * reflectColor, fresnel);
    }

    /// <summary>
    /// The alpha of the ordinary material drawn over the background pass (gl_rmain.c: currentalpha *
    /// r_water_wateralpha while the water pass is on): the shader's last <c>dp_water</c> parameter.
    /// </summary>
    public static float SurfaceAlpha(float materialAlpha, float waterAlpha) => materialAlpha * waterAlpha;

    /// <summary>
    /// DistortScaleRefractReflect (gl_rmain.c): the cvar times the shader's factor, in units of the screen;
    /// the normal-map texel, <c>normalize(texel - 0.5).xy</c>, is scaled by it.
    /// </summary>
    public static (float Refract, float Reflect) Distort(float refractDistortCvar, float reflectDistortCvar, float refractFactor, float reflectFactor)
        => (refractDistortCvar * refractFactor, reflectDistortCvar * reflectFactor);

    /// <summary>
    /// R_Water_StartFrame: the water textures are the view's size times r_water_resolutionmultiplier, at least
    /// 16 and at most the view (Xonotic's menu: 0.25 "Blurred", 0.5 "Good", 1 "Sharp"; the presets use 0.25 up to
    /// high, 0.5 at ultra).
    /// </summary>
    public static (int Width, int Height) TextureSize(int viewWidth, int viewHeight, float resolutionMultiplier)
        => ((int)Math.Clamp(viewWidth * resolutionMultiplier, 16f, MathF.Max(16f, viewWidth)),
            (int)Math.Clamp(viewHeight * resolutionMultiplier, 16f, MathF.Max(16f, viewHeight)));

    /// <summary>
    /// R_Water_AddWaterPlane: a surface joins an existing plane when
    /// <c>1 - dot(normals) + |dist difference| * 0.001</c> is at most 0.001.
    /// </summary>
    public static float PlaneScore(Vector3 normalA, float distA, Vector3 normalB, float distB)
        => 1f - Vector3.Dot(normalA, normalB) + MathF.Abs(distA - distB) * 0.001f;

    public static bool SamePlane(Vector3 normalA, float distA, Vector3 normalB, float distB) => PlaneScore(normalA, distA, normalB, distB) <= 0.001f;

    /// <summary>
    /// R_SetupView: the clip plane of a water render is the water plane moved back by r_water_clippingplanebias
    /// (1 by default), unless the eye is within that bias of the plane.
    /// </summary>
    public static float ClipPlaneDist(float planeDist, float eyeDist, float bias = 1f)
        => eyeDist < planeDist + bias ? planeDist : planeDist - bias;

    /// <summary>The eye mirrored in the plane <c>dot(n, p) = dist</c> (Matrix4x4_Reflect).</summary>
    public static Vector3 MirrorPoint(Vector3 point, Vector3 normal, float dist)
        => point - 2f * (Vector3.Dot(point, normal) - dist) * normal;

    /// <summary>
    /// Is the reflective water pass drawn at all: r_water on, and the quality setting above "off"
    /// (<paramref name="quality"/> is this port's r_water_quality: 0 never, otherwise as r_water says).
    /// </summary>
    public static bool Enabled(float r_water, float quality) => r_water != 0f && quality > 0f;
}
