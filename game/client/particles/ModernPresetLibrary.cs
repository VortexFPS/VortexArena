using System;
using System.Collections.Generic;

namespace VortexArena.Game.Client.Particles;

// =====================================================================================================
//  ModernPresetLibrary — the named ModernPreset recipes (planning/particles-dual-system.md §D.1). The
//  authoring overlay (effectinfo_xg.txt: `xg_preset <id>`) and the router reference these by string id; the
//  body of each recipe is a curated combination of the §B.3 modern draw features. The overlay may then
//  override individual knobs on top of the recipe (handled by the parser, not here).
//
//  Pure value lookups — no Godot deps — so this is unit-testable and shared by the router and the backend.
// =====================================================================================================

/// <summary>Static catalog of named <see cref="ModernPreset"/> recipes (soft-smoke, ember-shower, shockwave,
/// ribbon-trail, lit-explosion). <see cref="TryGet"/> resolves an id (case-insensitive) to a preset.</summary>
public static class ModernPresetLibrary
{
    /// <summary>Soft volumetric smoke: depth-faded (no hard wall seam), lit so it catches scene light, gentle
    /// curl-noise billow, flipbook through the smoke band, neutral emissive (smoke isn't a light source).</summary>
    public static ModernPreset SoftSmoke => new()
    {
        SoftParticles = true,
        Lit = true,
        CurlNoise = 18f,
        RibbonTrail = false,
        Flipbook = true,
        EmissiveBoost = 1f,
    };

    /// <summary>A dense shower of bright embers/debris: unshaded additive sparks, soft-faded so they don't pop
    /// against geometry, a little curl turbulence for drift, HDR-boosted so the hot embers bloom. Discrete
    /// billboards (the velocity-stretch is driven by the spark math in the process shader, not a ribbon).</summary>
    public static ModernPreset EmberShower => new()
    {
        SoftParticles = true,
        Lit = false,
        CurlNoise = 10f,
        RibbonTrail = false,
        Flipbook = false,
        EmissiveBoost = 2.2f,
    };

    /// <summary>An expanding shockwave ring/flash: unshaded additive, flipbook through the fire/ring band so the
    /// front animates as it grows, strongly HDR-boosted for the blast bloom. No soft-fade (the ring is meant to
    /// read crisply against walls) and no turbulence (it expands cleanly).</summary>
    public static ModernPreset Shockwave => new()
    {
        SoftParticles = false,
        Lit = false,
        CurlNoise = 0f,
        RibbonTrail = false,
        Flipbook = true,
        EmissiveBoost = 3.0f,
    };

    /// <summary>A connected ribbon/tube trail for a projectile streak: unshaded additive, soft-faded at surface
    /// intersections, modest HDR. RibbonTrail selects the strip draw pass; no flipbook (one streak texture).</summary>
    public static ModernPreset RibbonTrail_ => new()
    {
        SoftParticles = true,
        Lit = false,
        CurlNoise = 0f,
        RibbonTrail = true,
        Flipbook = false,
        EmissiveBoost = 1.6f,
    };

    /// <summary>A lit explosion fireball: depth-faded soft particles, LIT so the smoke half shadows realistically,
    /// curl turbulence for the rolling boil, flipbook fire animation, strong HDR for the core bloom.</summary>
    public static ModernPreset LitExplosion => new()
    {
        SoftParticles = true,
        Lit = true,
        CurlNoise = 30f,
        RibbonTrail = false,
        Flipbook = true,
        EmissiveBoost = 2.6f,
    };

    // Id → recipe. Keys are the canonical hyphenated overlay ids; lookup is case-insensitive and also accepts
    // the underscore spelling (some authoring tools normalize hyphens to underscores).
    private static readonly Dictionary<string, ModernPreset> _byId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["soft-smoke"] = SoftSmoke,
        ["ember-shower"] = EmberShower,
        ["shockwave"] = Shockwave,
        ["ribbon-trail"] = RibbonTrail_,
        ["lit-explosion"] = LitExplosion,
    };

    /// <summary>Resolve a named preset id (case-insensitive; '-' and '_' interchangeable) to its recipe. Returns
    /// false (and <see cref="ModernPreset.Default"/>) for an unknown/empty id, so callers can fall back cleanly.</summary>
    public static bool TryGet(string id, out ModernPreset preset)
    {
        if (!string.IsNullOrEmpty(id))
        {
            if (_byId.TryGetValue(id, out preset))
                return true;
            // Accept the underscore spelling of any hyphenated id (ember_shower == ember-shower).
            string normalized = id.Replace('_', '-');
            if (_byId.TryGetValue(normalized, out preset))
                return true;
        }
        preset = ModernPreset.Default;
        return false;
    }

    /// <summary>The set of recognised preset ids (for the authoring overlay validator / --fx-demo enumeration).</summary>
    public static IReadOnlyCollection<string> Ids => _byId.Keys;
}
