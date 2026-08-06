using System;
using Godot;
using VortexArena.Common.Diagnostics;
using VortexArena.Game.Menu;

namespace VortexArena.Game.Client;

/// <summary>
/// The cvar-driven half of the scene <see cref="Godot.Environment"/>: volumetric fog and light shafts
/// (<b>N3</b>) and real-time global illumination (<b>N9</b>). Both are Godot 4.6 Forward+ features with no
/// DarkPlaces counterpart that Xonotic ships — DP has a bounce grid, but no Xonotic preset ever enables it —
/// so these are additions rather than parity, and both default OFF.
///
/// <para><b>Why they default off, and stay off in the shipping presets.</b> The project's target is
/// DarkPlaces-class frame times. Volumetric fog is a froxel volume pass every frame; SDFGI is a cascaded
/// signed-distance-field probe update. Neither is cheap, and — more importantly for a competitive arena
/// shooter — <b>fog that hides players is a gameplay change, not a graphics one</b>. So the fog cvars are
/// documented as an option a player can turn off, they are absent from the low/med/normal presets, and the
/// map-declared fog that <see cref="MapLoader.ApplyFog"/> already reads is left alone: this adds a
/// <i>volumetric</i> layer on top, it does not reinterpret the mapper's fog keys.</para>
///
/// <para><b>Cvars</b> (all port-only; DP has no equivalent):
/// <list type="bullet">
///   <item><c>r_volumetricfog</c> — 0 off (default), 1 on. Density/albedo/emission follow.</item>
///   <item><c>r_volumetricfog_density</c> — 0.01 is a light haze, 0.05 is thick.</item>
///   <item><c>r_volumetricfog_shafts</c> — how strongly lights scatter into the volume, i.e. how much
///   "god ray" you get. This is what makes it worth having.</item>
///   <item><c>r_gi</c> — 0 off (default), 1 SDFGI. SDFGI needs no bake and handles open maps, which is what
///   makes it the practical choice for a map pool nobody is going to re-bake.</item>
///   <item><c>r_gi_cascades</c>, <c>r_gi_energy</c>, <c>r_gi_bounces</c> — SDFGI quality knobs.</item>
/// </list></para>
///
/// <para>Applied on map load and re-applied whenever one of the cvars changes, so all of it is live in the
/// console — the same contract <see cref="WorldTint"/> and <see cref="ModelLighting"/> use.</para>
/// </summary>
public static class SceneLightingSettings
{
    private static Godot.Environment? _env;

    /// <summary>The map's real sky, captured at <see cref="Attach"/> so <c>r_sky 0</c> can be undone.</summary>
    private static Sky? _sky;

    // Last-applied values, so the per-frame poll only touches the Environment when something moved.
    private static bool _fogOn, _giOn, _bloomOn, _skyOn;
    private static float _fogDensity, _fogShafts, _giEnergy;
    private static int _giCascades, _giBounces;
    private static bool _seeded;

    /// <summary>Bind the live scene environment and push the current cvar values. Call once per map.</summary>
    public static void Attach(Godot.Environment? env)
    {
        _env = env;
        _sky = env?.Sky;
        _seeded = false;
        Poll();
    }

    /// <summary>Re-capture the sky after a late swap (the pure-client ApplyMapSky path replaces it).</summary>
    public static void NoteSkyChanged(Sky? sky)
    {
        _sky = sky;
        // Re-assert r_sky 0 over the new sky object if the player has it off.
        if (_env is not null && GodotObject.IsInstanceValid(_env) && !_skyOn && _seeded)
            ApplySky(false);
    }

    /// <summary>Drop the binding (map teardown).</summary>
    public static void Detach() => _env = null;

    /// <summary>
    /// Read the cvars and apply anything that changed. Cheap: six cvar reads and an early-out, so it is safe
    /// on the per-frame client path next to <see cref="WorldTint.PollCvars"/>.
    /// </summary>
    public static void Poll()
    {
        if (_env is null || !GodotObject.IsInstanceValid(_env))
            return;

        bool fogOn = Cvar("r_volumetricfog", 0f) != 0f;
        float fogDensity = MathF.Max(0f, Cvar("r_volumetricfog_density", 0.015f));
        float fogShafts = Math.Clamp(Cvar("r_volumetricfog_shafts", 1f), 0f, 16f);
        bool giOn = Cvar("r_gi", 0f) != 0f;
        // r_bloom -> the Environment glow pass. The port's glow IS its r_bloom equivalent (hand-tuned
        // threshold-1.0 bloom on genuinely bright pixels, see NetGame.AddLight), so the cvar now actually
        // owns it. Unset reads as ON to preserve the shipped look; the presets set it explicitly.
        bool bloomOn = Cvar("r_bloom", 1f) != 0f;
        // r_sky 0 (DP: disable sky rendering for performance/visibility) -> flat black background. The sky
        // object is kept so 1 restores the map's real skybox without a reload.
        bool skyOn = Cvar("r_sky", 1f) != 0f;
        int giCascades = Math.Clamp((int)Cvar("r_gi_cascades", 4f), 1, 8);
        float giEnergy = MathF.Max(0f, Cvar("r_gi_energy", 1f));
        int giBounces = Math.Clamp((int)Cvar("r_gi_bounces", 1f), 0, 4);

        if (_seeded && fogOn == _fogOn && giOn == _giOn && bloomOn == _bloomOn && skyOn == _skyOn
            && Mathf.IsEqualApprox(fogDensity, _fogDensity)
            && Mathf.IsEqualApprox(fogShafts, _fogShafts)
            && Mathf.IsEqualApprox(giEnergy, _giEnergy)
            && giCascades == _giCascades && giBounces == _giBounces)
            return;

        bool fogTurnedOn = fogOn && (!_seeded || !_fogOn);
        bool giTurnedOn = giOn && (!_seeded || !_giOn);

        _seeded = true;
        _fogOn = fogOn; _fogDensity = fogDensity; _fogShafts = fogShafts;
        _giOn = giOn; _giCascades = giCascades; _giEnergy = giEnergy; _giBounces = giBounces;
        _bloomOn = bloomOn; _skyOn = skyOn;

        // ---- r_bloom / r_sky (the two Environment toggles the Effects tab always bound) --------------
        _env.GlowEnabled = bloomOn;
        ApplySky(skyOn);

        // ---- N3: volumetric fog + light shafts ----------------------------------------------------
        _env.VolumetricFogEnabled = fogOn;
        if (fogOn)
        {
            _env.VolumetricFogDensity = fogDensity;
            // The scatter term is what turns "there is fog" into "there are shafts of light through the
            // windows": it is how much each light injects into the froxel volume.
            _env.VolumetricFogGIInject = fogShafts;
            _env.VolumetricFogAlbedo = new Color(1f, 1f, 1f);
            _env.VolumetricFogEmission = new Color(0f, 0f, 0f);
            _env.VolumetricFogAnisotropy = 0.2f;
            // Bounded so a distant froxel does not cost the same as a near one on a huge open map.
            _env.VolumetricFogLength = 2048f;
            _env.VolumetricFogDetailSpread = 2f;
        }

        // ---- N9: SDFGI -----------------------------------------------------------------------------
        _env.SdfgiEnabled = giOn;
        if (giOn)
        {
            _env.SdfgiCascades = giCascades;
            _env.SdfgiEnergy = giEnergy;
            _env.SdfgiBounceFeedback = giBounces > 0 ? Math.Min(1f, giBounces * 0.5f) : 0f;
            _env.SdfgiUseOcclusion = true;
            _env.SdfgiReadSkyLight = true;
        }

        if (fogTurnedOn)
            Log.Info("[SceneLighting] volumetric fog ON — this is a per-frame froxel pass, and fog that hides " +
                     "players is a gameplay change: leave it off for competitive play.");
        if (giTurnedOn)
            Log.Info($"[SceneLighting] SDFGI ON ({giCascades} cascades) — real-time indirect bounce, well " +
                     "above the frame budget on anything but an ultra preset.");
    }

    /// <summary>Sky on = the map's captured sky; off = flat black (DP r_sky 0 renders no sky pass).</summary>
    private static void ApplySky(bool on)
    {
        if (_env is null || !GodotObject.IsInstanceValid(_env))
            return;
        if (on && _sky is not null)
        {
            _env.BackgroundMode = Godot.Environment.BGMode.Sky;
            _env.Sky = _sky;
        }
        else if (!on)
        {
            _env.BackgroundMode = Godot.Environment.BGMode.Color;
            _env.BackgroundColor = new Color(0f, 0f, 0f);
        }
    }

    private static float Cvar(string name, float fallback)
    {
        string s = MenuState.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : MenuState.Cvars.GetFloat(name);
    }
}
