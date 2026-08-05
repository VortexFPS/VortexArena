using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Game.Loaders;
using VortexArena.Game.Menu;

namespace VortexArena.Game.Client;

/// <summary>
/// The gameplay-legible rim light (<b>N8</b>) — a Vortex-original, not a DarkPlaces feature.
///
/// <para><b>What it is for.</b> In a fast arena shooter the expensive moment is target identification: is that
/// a teammate, and is that the one carrying the powerup? Both are currently answered by HUD elements the
/// player has to look away to read, or by a nametag that only appears under the crosshair. A rim light answers
/// them in peripheral vision, on the model itself, at any distance the model is visible: a thin band along the
/// silhouette, coloured by the thing you needed to know.</para>
///
/// <para><b>Why every part of it is a cvar, and why it is off by default.</b> This is a
/// competitive-information change, not a cosmetic one — it makes a player easier to see, in the dark, from
/// behind cover edges. That is a balance decision, so it ships off, the strength is tunable, and the two
/// drivers (team identity and powerup carrier) toggle independently. A server that wants it uniform can force
/// the cvars; a player who finds it distracting can turn it off without losing anything else.</para>
///
/// <para><b>Cvars</b>: <c>cl_rimlight</c> (master, default 0), <c>cl_rimlight_strength</c>,
/// <c>cl_rimlight_teams</c> (colour teammates/enemies by team), <c>cl_rimlight_powerup</c> (pulse on a
/// powerup carrier), <c>cl_rimlight_power</c> (falloff exponent — higher is a thinner band).</para>
/// </summary>
public static class RimLight
{
    private static readonly StringName RimColorUniform = "rim_color";
    private static readonly StringName RimPowerUniform = "rim_power";

    /// <summary>Last-pushed rim state per entity, so an unchanged rim costs no interop.</summary>
    public struct RimCache
    {
        public Color Color;
        public float Power;
        public bool Valid;
    }

    /// <summary>
    /// Resolve and push the rim for one model. <paramref name="colormap"/> is the entity's team/colormap value
    /// (the same one <see cref="ModelTint.ApplyAppearance"/> takes), <paramref name="hasPowerup"/> true while
    /// the entity carries a powerup. Pushing black is how the rim is turned off, and it is done rather than
    /// skipped so a player who drops a powerup stops glowing on the next frame.
    /// </summary>
    public static void Apply(IReadOnlyList<MeshInstance3D> meshes, int colormap, bool hasPowerup,
                             float now, ref RimCache cache)
    {
        if (meshes.Count == 0)
            return;

        Color rim = Resolve(colormap, hasPowerup, now, out float power);

        if (cache.Valid && cache.Color == rim && Mathf.IsEqualApprox(cache.Power, power))
            return;

        var v = new Vector3(rim.R, rim.G, rim.B);
        for (int i = 0; i < meshes.Count; i++)
        {
            meshes[i].SetInstanceShaderParameter(RimColorUniform, v);
            meshes[i].SetInstanceShaderParameter(RimPowerUniform, power);
        }
        cache = new RimCache { Color = rim, Power = power, Valid = true };
    }

    /// <summary>The rim colour for this entity's state; black when the feature is off or nothing applies.</summary>
    private static Color Resolve(int colormap, bool hasPowerup, float now, out float power)
    {
        power = MathF.Max(0.5f, Cvar("cl_rimlight_power", 2.5f));
        if (Cvar("cl_rimlight", 0f) == 0f)
            return ModelTint.Black;

        float strength = MathF.Max(0f, Cvar("cl_rimlight_strength", 0.6f));
        if (strength <= 0f)
            return ModelTint.Black;

        // A powerup carrier outranks the team rim: it is the more urgent fact, and stacking the two would
        // just produce a muddy colour that says neither.
        if (hasPowerup && Cvar("cl_rimlight_powerup", 1f) != 0f)
        {
            // Pulse so it reads as "carrying something" rather than "is a different team".
            float pulse = 0.65f + 0.35f * MathF.Sin(now * 6f);
            float s = strength * pulse * 1.5f;
            return new Color(s, s * 0.85f, s * 0.25f);   // warm gold
        }

        if (Cvar("cl_rimlight_teams", 1f) != 0f)
        {
            Color team = ModelTint.TeamColor(colormap, out bool hasTeam);
            if (hasTeam)
                return new Color(team.R * strength, team.G * strength, team.B * strength);
        }

        return ModelTint.Black;
    }

    private static float Cvar(string name, float fallback)
    {
        string s = MenuState.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : MenuState.Cvars.GetFloat(name);
    }
}
