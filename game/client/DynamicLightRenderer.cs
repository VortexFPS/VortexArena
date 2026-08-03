// The render consumer for qcsrc/common/mapobjects/misc/dynlight.qc (dynlight) — the half DarkPlaces drives
// from the engine.
//
// A dynlight is a real-time dynamic light placed in a map (dynlight.qc:18 QUAKED): it sits still, travels a
// path_corner chain, FOLLOWs an entity, or attaches to a model tag, toggling on/off when triggered. The
// SVQC half (DynamicLight.cs) runs the full state machine (mode dispatch, toggle, path travel, follow) and
// keeps the live .light_lev / .color / .origin / .active on the shared entity. Base's QUAKED .pflags lines
// are commented out, but the DP engine still drives a realtime light from light_lev/color/style — DP reads
// those edict fields and adds a dynamic light each frame. This is the missing port consumer: one persistent
// Godot OmniLight3D per dynlight, positioned and colored from the server entity every frame.
//
// Field mapping (dynlight.qc:24-26):
//   .light_lev  -> light RADIUS in Quake units (default 200; 0 = off). 1 Quake unit = 1 Godot unit
//                  (Coords has no scale), so light_lev maps straight onto OmniLight3D.OmniRange, exactly as
//                  LaserRenderer maps its dlight radius.
//   .color      -> rgb + brightness ('1 1 1' = bright white; values up to 255 255 255 = "nuclear blast").
//                  The hue drives LightColor; the magnitude (>1) drives LightEnergy, mirroring LaserRenderer's
//                  color*5 normalize-hue-then-energy split.
//   .active     -> ACTIVE_NOT hides the light (toggled off via a trigger); light_lev==0 also hides it.
//   .origin     -> the light position (already reflects path travel / FOLLOW / tag-attach done server-side).
//
// Like LaserRenderer / MapParticleEmitters, this scans the AMBIENT entity facade
// (Api.Entities.FindByClass("dynlight")) so it lights up on the listen-server and demo paths where
// Api.Services is the live server world; a pure --connect client has no facade (or BSP) yet, so it idles
// there (the established seam, not a silent gap).
//
// Known approximations (documented residuals): .style lightstyle ANIMATION is now applied to dynlights via the
// worldspawn lightstyle table (LightStyles.Sample, server/world.qc:882-920) — a styled dynlight modulates its
// radius by the style's current brightness frame. Animated WORLD-BRUSH lightmaps (the larger use of named
// styles) still render steady (no lightmap-modulation subsystem). NOSHADOW is honored (shadows are disabled by default to keep the
// budget sane, matching the commented-out Base default and the LaserRenderer endlight). avelocity spin has
// no visible effect on an omnidirectional point light (no orientation), so it is intentionally not applied
// to the render node.

using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;

namespace VortexArena.Game.Client;

/// <summary>Persistent dynlight realtime-light renderer (the DP engine-driven light DynamicLight.cs lacked). Hosted by <see cref="ClientWorld"/>.</summary>
public partial class DynamicLightRenderer : Node3D
{
    private const float RescanInterval = 2f;   // facade re-scan cadence (dynlights can spawn via target_spawn)
    private const float MaxRange = 2048f;       // sane upper clamp on a mapper light_lev (Quake units)
    private const float MaxEnergy = 8f;         // upper clamp for "nuclear blast" (color > 1) brightness

    private sealed class LightNode
    {
        public Entity Entity = null!;
        public OmniLight3D Light = null!;
        // (draws 2026-08-02) PVS gating state — see MapParticleEmitters' twin fields.
        public int PvsCluster = -2;
        public System.Numerics.Vector3 PvsClusterAt;
    }

    private readonly Dictionary<Entity, LightNode> _lights = new();
    private float _rescanIn;

    public override void _Process(double delta)
    {
        if (Api.Services is null)
            return;

        using var _prof = FrameProfiler.Scope("dynlights");

        _rescanIn -= (float)delta;
        if (_rescanIn <= 0f)
        {
            Rescan();
            _rescanIn = RescanInterval;
        }

        // (draws 2026-08-02) PVS gate: an out-of-view dynlight otherwise still enters the clustered light
        // grid (its range reaches up to MaxRange), taxing every fragment its cluster covers. Same contract as
        // the emitters; `r_pvs_cull_dynlights 0` restores always-on. APPROXIMATION, stated honestly: the cull
        // keys on the LIGHT's cluster being visible from a viewpoint. A light around a corner that spills
        // through a doorway into a room you DO see can be culled a frame early/late (cluster visibility is
        // not transitive); the exact test is a PVS-row intersection per light per frame, which costs more
        // than these decorative lights do. Map dynlights are slow-moving ambience — the corner case reads as
        // a doorway glow fading a step sooner, and the cvar restores exactness-by-always-on.
        var culler = VortexArena.Game.WorldPvsCuller.Instance;
        bool pvsGate = culler is { CullActive: true }
            && Api.Cvars.GetFloat("r_pvs_cull_dynlights") != 0f;

        foreach (LightNode ln in _lights.Values)
        {
            if (pvsGate && !ln.Entity.IsFreed)
            {
                if (ln.PvsCluster == -2
                    || System.Numerics.Vector3.DistanceSquared(ln.Entity.Origin, ln.PvsClusterAt) > 32f * 32f)
                {
                    ln.PvsCluster = culler!.ClusterAt(ln.Entity.Origin);
                    ln.PvsClusterAt = ln.Entity.Origin;
                }
                if (!culler!.ClusterVisibleFromView(ln.PvsCluster))
                {
                    if (GodotObject.IsInstanceValid(ln.Light))
                        ln.Light.Visible = false;
                    continue;
                }
            }
            UpdateLight(ln);
        }
    }

    // =================================================================================================
    //  Facade scan / node lifecycle
    // =================================================================================================

    private void Rescan()
    {
        foreach (Entity e in Api.Entities.FindByClass("dynlight"))
        {
            if (e.IsFreed || _lights.ContainsKey(e))
                continue;
            _lights[e] = BuildLight(e);
        }

        // Drop freed dynlights (killtarget can remove them).
        List<Entity>? dead = null;
        foreach (var kv in _lights)
            if (kv.Key.IsFreed)
                (dead ??= new List<Entity>()).Add(kv.Key);
        if (dead is not null)
        {
            foreach (Entity e in dead)
            {
                if (GodotObject.IsInstanceValid(_lights[e].Light))
                    _lights[e].Light.QueueFree();
                _lights.Remove(e);
            }
        }
    }

    private LightNode BuildLight(Entity e)
    {
        // NOSHADOW (dynlight.qc:128 = PFLAGS_NOSHADOW, commented in Base) — shadows are off by default to keep
        // the render budget sane (a map can scatter many dynlights), matching the LaserRenderer endlight.
        var light = new OmniLight3D
        {
            Name = $"dynlight#{e.Index}",
            Visible = false,
            ShadowEnabled = false,
            OmniRange = 200f,
        };
        AddChild(light);
        return new LightNode { Entity = e, Light = light };
    }

    // =================================================================================================
    //  Per-frame light update (the DP engine-driven realtime light)
    // =================================================================================================

    private void UpdateLight(LightNode ln)
    {
        Entity e = ln.Entity;
        if (e.IsFreed || !GodotObject.IsInstanceValid(ln.Light))
            return;

        // Off when toggled inactive (dynlight_use/setactive set light_lev=0) or radius is zero.
        if (e.Active != MapMover.ActiveActive || e.LightLev <= 0f)
        {
            ln.Light.Visible = false;
            return;
        }

        ln.Light.Visible = true;
        ln.Light.Position = Coords.ToGodot(e.Origin);          // follows path travel / FOLLOW / tag-attach

        // QC .style lightstyle animation (server/world.qc:882-920 installs the named flicker/pulse/candle/strobe
        // table): a styled light samples its style string at 10 fps ('a'=dark .. 'm'=normal .. 'z'=double) and
        // modulates its radius by that brightness. Style 0 / an out-of-table index samples 1.0 (steady). Time
        // base is the engine clock so the animation is wall-clock-paced like Base's engine-driven lightmap anim.
        float styleBrightness = VortexArena.Common.Gameplay.LightStyles.Sample(
            e.LightStyle, (float)Time.GetTicksMsec() / 1000f);
        ln.Light.OmniRange = Math.Clamp(e.LightLev * styleBrightness, 0f, MaxRange);
        if (ln.Light.OmniRange <= 0f)
        {
            // a style frame at full dark ('a') turns the light fully off this frame.
            ln.Light.Visible = false;
            return;
        }

        // .color is rgb+brightness ('1 1 1' = bright white). Default to white if unset (matches the
        // spawnfunc default this.color = '1 1 1'). Split hue (LightColor) from magnitude (LightEnergy) so a
        // color > 1 ("nuclear blast") brightens rather than clipping — the same hue/energy split LaserRenderer
        // uses for its endpoint dlight.
        System.Numerics.Vector3 c = e.LightColor;
        if (c == System.Numerics.Vector3.Zero)
            c = System.Numerics.Vector3.One;
        float maxc = MathF.Max(1f, MathF.Max(c.X, MathF.Max(c.Y, c.Z)));
        ln.Light.LightColor = new Color(c.X / maxc, c.Y / maxc, c.Z / maxc);
        ln.Light.LightEnergy = MathF.Min(MaxEnergy, maxc);
    }
}
