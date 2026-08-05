using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Game.Loaders;
using VortexArena.Game.Menu;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>
/// Makes dynamic lights reach models that are lit by the map's baked light grid (<b>N2</b>), and drives the
/// gameplay-legible rim light (<b>N8</b>).
///
/// <para><b>N2 — the gap this closes.</b> The light grid is <i>baked</i>: it knows where the map's lamps are
/// and nothing else. So after F1-B an explosion lit the walls beautifully and left the player standing in it
/// exactly as dim as before. DarkPlaces has the same split — a grid-textured entity does not sample dlights —
/// which is why this is filed as a new feature rather than parity. The fix is the one DP already computes for
/// its <i>non</i>-grid entities: <c>R_CompleteLightPoint</c>'s <c>LP_DYNLIGHT</c> term, a first-order spherical
/// harmonic accumulation of every dynamic light in range, collapsed to one ambient colour, one directed colour
/// and one direction. That triple is exactly the shape of the skin shader's lobe 2, so it drops straight in on
/// top of the per-pixel grid with no shader change.</para>
///
/// <para>Per-entity rather than per-pixel, deliberately. A per-pixel version means a second light volume and a
/// per-fragment loop; a per-entity lobe costs one pass over a capped light list per model per frame and gets
/// the thing that actually reads on screen — the player lighting up orange when a rocket goes off next to
/// them, from the correct side.</para>
///
/// <para><b>N8 — the rim light</b> is a Vortex-original: a thin light along a model's silhouette, coloured by
/// something the player needs to know at a glance. Team colour for teammates, a pulse for a powerup carrier.
/// This is gameplay information delivered through the lighting rather than through more HUD, which is the
/// whole argument for it in a game where target identification happens in a fraction of a second. It is also
/// a competitive-information change, so it is OFF by default and every part of it is a cvar.</para>
/// </summary>
public static class ModelLightProbe
{
    /// <summary>DP's attenuation constants (<c>r_shadow_lightattenuationlinearscale</c> 2,
    /// <c>r_shadow_lightattenuationdividebias</c> 1) — the same curve <c>R_CompleteLightPoint</c> uses.</summary>
    private const float LinearScale = 2f;
    private const float DivideBias = 1f;

    /// <summary>Most lights folded into one probe. Beyond this the nearest win; the budget already capped them.</summary>
    private const int MaxLights = 8;

    /// <summary>
    /// Accumulate the dynamic lights around <paramref name="godotOrigin"/> into one (ambient, directed,
    /// direction) triple, in the skin shader's lobe-2 shape. Returns false when nothing is in range, which the
    /// caller should treat as "push zeros" rather than "skip" — a stale lobe would keep an explosion's light
    /// on a model long after it faded.
    /// </summary>
    public static bool Probe(Vector3 godotOrigin, out Vector3 ambient, out Vector3 directed, out Vector3 direction)
    {
        ambient = Vector3.Zero;
        directed = Vector3.Zero;
        direction = Vector3.Up;

        LightBudget? budget = LightBudget.Instance;
        if (budget is null)
            return false;

        // First-order spherical harmonics, as R_CompleteLightPoint does: sa = ambient accumulator, sx/sy/sz =
        // the directional moments, sd = the weighted-average direction ("bent normal").
        Vector3 sa = Vector3.Zero, sx = Vector3.Zero, sy = Vector3.Zero, sz = Vector3.Zero, sd = Vector3.Zero;
        int used = 0;

        foreach ((Light3D light, float range) in budget.Lit())
        {
            if (used >= MaxLights)
                break;
            Vector3 rel = light.GlobalPosition - godotOrigin;
            float dist2 = rel.LengthSquared();
            float r2 = range * range;
            if (r2 <= 0f || dist2 >= r2)
                continue;

            float dist = Mathf.Sqrt(dist2) / range;
            float intensity = (1f - dist) * LinearScale / (DivideBias + dist * dist);
            if (intensity <= 0f)
                continue;
            used++;

            Color lc = light.LightColor;
            float e = light.LightEnergy * intensity;
            var color = new Vector3(lc.R * e, lc.G * e, lc.B * e);
            float mag = color.Length();
            Vector3 n = dist2 > 1e-6f ? rel / Mathf.Sqrt(dist2) : Vector3.Up;

            sa += 0.5f * color;
            sx += n.X * color;
            sy += n.Y * color;
            sz += n.Z * color;
            sd += mag * n;
        }

        if (used == 0)
            return false;

        direction = sd.LengthSquared() > 1e-8f ? sd.Normalized() : Vector3.Up;
        directed = new Vector3(
            direction.X * sx.X + direction.Y * sy.X + direction.Z * sz.X,
            direction.X * sx.Y + direction.Y * sy.Y + direction.Z * sz.Y,
            direction.X * sx.Z + direction.Y * sy.Z + direction.Z * sz.Z);
        // DP subtracts a third of the directed term back out of ambient, so a strongly directional light does
        // not also flood the model with fill.
        ambient = sa - 0.333f * directed;
        ambient = new Vector3(MathF.Max(0f, ambient.X), MathF.Max(0f, ambient.Y), MathF.Max(0f, ambient.Z));
        return true;
    }

    /// <summary>Cached last-pushed lobe-2 values, so an unchanged probe costs no interop.</summary>
    public struct ProbeCache
    {
        public Vector3 Ambient, Directed, Direction;
        public bool Valid;
    }

    /// <summary>
    /// Probe at <paramref name="godotOrigin"/> and push the result onto <paramref name="meshes"/> as the skin
    /// shader's lobe 2, change-gated. No-op unless a GPU light grid is bound: without one, lobe 2 carries the
    /// CPU grid sample (or the model is on the PBR path entirely) and writing dynamic light there would fight
    /// whoever owns it.
    /// </summary>
    public static void Apply(IReadOnlyList<MeshInstance3D> meshes, Vector3 godotOrigin, ref ProbeCache cache)
    {
        if (meshes.Count == 0 || !ModelLighting.HasGrid || !Enabled())
            return;

        if (!Probe(godotOrigin, out Vector3 amb, out Vector3 dif, out Vector3 dir))
        {
            amb = Vector3.Zero;
            dif = Vector3.Zero;
            dir = Vector3.Up;
        }

        float scale = Strength();
        amb *= scale;
        dif *= scale;

        if (cache.Valid
            && (cache.Ambient - amb).LengthSquared() < 1e-6f
            && (cache.Directed - dif).LengthSquared() < 1e-6f
            && (cache.Direction - dir).LengthSquared() < 1e-6f)
            return;

        for (int i = 0; i < meshes.Count; i++)
        {
            MeshInstance3D mi = meshes[i];
            mi.SetInstanceShaderParameter(PlayerSkinShader.GridAmbientUniform, amb);
            mi.SetInstanceShaderParameter(PlayerSkinShader.GridDiffuseUniform, dif);
            mi.SetInstanceShaderParameter(PlayerSkinShader.GridDirUniform, dir);
        }
        cache = new ProbeCache { Ambient = amb, Directed = dif, Direction = dir, Valid = true };
    }

    /// <summary><c>r_model_dlight</c> — do dynamic lights reach grid-lit models? 1 by default.</summary>
    public static bool Enabled() => Cvar("r_model_dlight", 1f) != 0f;

    /// <summary><c>r_model_dlight_scale</c> — how strongly. 1 = the DP attenuation curve unmodified.</summary>
    private static float Strength() => MathF.Max(0f, Cvar("r_model_dlight_scale", 1f));

    private static float Cvar(string name, float fallback)
    {
        string s = MenuState.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : MenuState.Cvars.GetFloat(name);
    }
}
