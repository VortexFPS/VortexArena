// Port of the CSQC half of qcsrc/common/mapobjects/func/rainsnow.qc (Draw_RainSnow, rainsnow.qc:101-119)
// + the DP particle backends CL_ParticleRain (Base/darkplaces/cl_particles.c:2088-2143): the client
// renderer for func_rain / func_snow weather volumes.
//
// Per volume it keeps ONE pooled GpuParticles3D whose emission box is recomputed each frame as the
// intersection of the brush volume with (view ± drawdist) — drawdist = .fade_end if set, else the
// cl_rainsnow_maxdrawdist cvar (default 1000, the shipped xonotic-client.cfg value). The per-second
// particle budget follows the QC density formula (RainSnow.DrawCount — bound(1, 0.1*count*(sx/1024)*
// (sy/1024), 65535)), capped for the Godot emitter; DP's rain quadruples the count with size 0.5
// stretched sparks at alpha 32-64, snow draws size-1 billboards at alpha 64-128, colored from the Quake
// palette at colorbase .cnt (default 12, light gray). cl_particles_rain / cl_particles_snow (default 1)
// gate each type, as in DP.
//
// Facade-scanning like LaserRenderer (listen-server/demo seam). NOTE: no shipped stock map carries these
// entities — this node idles at zero cost on every stock map.

using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>func_rain/func_snow weather renderer. Hosted by <see cref="ClientWorld"/>.</summary>
public partial class WeatherSystem : Node3D
{
    /// <summary>The listener/view position provider (Quake space) — wired by the host (ClientWorld).</summary>
    public Func<NVec3?>? ViewOriginProvider { get; set; }

    private const float RescanInterval = 2f;
    private const int MaxAmountPerVolume = 4000;

    // The first 16 Quake palette entries are a grayscale ramp — the slice rain/snow's default colorbase
    // 12..15 lands in (DP color = particlepalette[colorbase + (rand&3)]).
    private static readonly byte[] QuakeGrayRamp =
        { 0, 15, 31, 47, 63, 75, 91, 107, 123, 139, 155, 171, 187, 203, 219, 235 };

    private sealed class WeatherVolume
    {
        public Entity Entity = null!;
        public GpuParticles3D Particles = null!;
        public bool IsRain;
        public float Lifetime;
    }

    private readonly Dictionary<Entity, WeatherVolume> _volumes = new();
    private float _rescanIn;
    private bool _cvarsRegistered;

    public override void _Process(double delta)
    {
        using var _scope = FrameProfiler.Scope("weather"); // [profiling] §18: out of proc:other
        if (Api.Services is null)
            return;

        if (!_cvarsRegistered)
        {
            _cvarsRegistered = true;
            Api.Cvars.Register("cl_rainsnow_maxdrawdist", "1000"); // shipped xonotic-client.cfg default
            Api.Cvars.Register("cl_particles_rain", "1");          // DP engine cvar defaults
            Api.Cvars.Register("cl_particles_snow", "1");
        }

        _rescanIn -= (float)delta;
        if (_rescanIn <= 0f)
        {
            Rescan();
            _rescanIn = RescanInterval;
        }

        if (_volumes.Count == 0)
            return;

        NVec3? view = ViewOriginProvider?.Invoke();
        foreach (WeatherVolume v in _volumes.Values)
            UpdateVolume(v, view);
    }

    private void Rescan()
    {
        Scan("func_rain", isRain: true);
        Scan("func_snow", isRain: false);
    }

    private void Scan(string className, bool isRain)
    {
        foreach (Entity e in Api.Entities.FindByClass(className))
        {
            if (e.IsFreed || _volumes.ContainsKey(e))
                continue;
            _volumes[e] = Build(e, isRain);
        }
    }

    private WeatherVolume Build(Entity e, bool isRain)
    {
        // Fall velocity + per-particle lifetime: DP lifetime = fall-height / |dirz| (one full traversal).
        NVec3 dest = e.Dest;
        float fallHeight = MathF.Max(64f, (e.Maxs.Z - e.Mins.Z));
        float life = Mathf.Clamp(fallHeight / MathF.Max(1f, MathF.Abs(dest.Z)), 0.4f, 12f);

        Vector3 velG = Coords.ToGodot(dest);
        float speed = velG.Length();

        var mat = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            Direction = speed > 0.01f ? velG / speed : Vector3.Down,
            Spread = 0f,
            InitialVelocityMin = speed,
            InitialVelocityMax = speed,
            Gravity = Vector3.Zero, // straight-line fall (DP rain/snow particles are unaccelerated)
        };
        // Color: palette[cnt .. cnt+3] — model the &3 dither as a ramp across the four palette grays.
        Color c0 = PaletteGray(e.Cnt);
        Color c1 = PaletteGray(e.Cnt + 3);
        float aMin = (isRain ? 32f : 64f) / 256f;
        float aMax = (isRain ? 64f : 128f) / 256f;
        var initGrad = new Gradient();
        initGrad.SetColor(0, new Color(c0.R, c0.G, c0.B, aMin));
        initGrad.SetColor(1, new Color(c1.R, c1.G, c1.B, aMax));
        mat.ColorInitialRamp = new GradientTexture1D { Gradient = initGrad };

        // Snow drifts a little (DP randomizes snow velocity by ±16/axis); rain falls dead straight.
        if (!isRain)
        {
            mat.Spread = 8f;
            mat.InitialVelocityMin = MathF.Max(0f, speed - 16f);
            mat.InitialVelocityMax = speed + 16f;
        }

        var particles = new GpuParticles3D
        {
            Name = $"{(isRain ? "rain" : "snow")}#{e.Index}",
            Amount = 1, // sized on the first per-frame update from the clipped box
            Lifetime = life,
            OneShot = false,
            Explosiveness = 0f,
            Emitting = false,
            ProcessMaterial = mat,
            DrawPass1 = isRain ? RainMesh() : SnowMesh(),
        };
        AddChild(particles);

        return new WeatherVolume { Entity = e, Particles = particles, IsRain = isRain, Lifetime = life };
    }

    private void UpdateVolume(WeatherVolume v, NVec3? viewQ)
    {
        Entity e = v.Entity;
        if (e.IsFreed || !GodotObject.IsInstanceValid(v.Particles))
            return;

        // DP gates each weather type on its cl_particles_* cvar.
        bool enabled = Api.Cvars.GetFloat(v.IsRain ? "cl_particles_rain" : "cl_particles_snow") != 0f;
        if (!enabled || viewQ is not { } view)
        {
            v.Particles.Emitting = false;
            return;
        }

        // effbox = volume ∩ (view ± drawdist) (Draw_RainSnow:103-108).
        float drawdist = e.FadeEndDist > 0f ? e.FadeEndDist : Api.Cvars.GetFloat("cl_rainsnow_maxdrawdist");
        if (drawdist <= 0f)
            drawdist = RainSnow.DefaultMaxDrawDist;

        NVec3 volMin = e.Origin + e.Mins;
        NVec3 volMax = e.Origin + e.Maxs;
        NVec3 boxMin = NVec3.Max(view - new NVec3(drawdist), volMin);
        NVec3 boxMax = NVec3.Min(view + new NVec3(drawdist), volMax);
        if (boxMin.X >= boxMax.X || boxMin.Y >= boxMax.Y || boxMin.Z >= boxMax.Z)
        {
            v.Particles.Emitting = false; // outside the draw distance — DP renders nothing
            return;
        }

        NVec3 size = boxMax - boxMin;
        // particles/sec for the clipped box (the QC density formula), DP rain ×4 (CL_ParticleRain count*4).
        float perSec = RainSnow.DrawCount(e.ParticleCount, size.X, size.Y) * (v.IsRain ? 4f : 1f);
        int amount = System.Math.Clamp((int)(perSec * v.Lifetime), 1, MaxAmountPerVolume);

        // Changing Amount restarts the emitter — only re-size on meaningful (>25%) budget changes.
        if (amount > v.Particles.Amount * 5 / 4 || amount < v.Particles.Amount * 3 / 4)
            v.Particles.Amount = amount;

        v.Particles.Position = Coords.ToGodot((boxMin + boxMax) * 0.5f);
        if (v.Particles.ProcessMaterial is ParticleProcessMaterial mat)
        {
            Vector3 halfG = AbsToGodot(size * 0.5f);
            mat.EmissionBoxExtents = new Vector3(
                MathF.Max(1f, halfG.X), MathF.Max(1f, halfG.Y), MathF.Max(1f, halfG.Z));
        }
        v.Particles.VisibilityAabb = new Aabb(
            -AbsToGodot(size * 0.5f) - new Vector3(64f, 64f, 64f),
            AbsToGodot(size) + new Vector3(128f, 128f, 128f));
        v.Particles.Emitting = true;
    }

    // --- meshes: rain = a thin velocity-aligned streak (DP pt_rain spark-stretch, size 0.5); snow = a
    //     small billboard (size 1.0). Both unshaded additive-ish quads. ---

    private static Mesh RainMesh()
    {
        var quad = new QuadMesh { Size = new Vector2(0.5f, 24f) }; // thin vertical streak
        quad.Material = WeatherMaterial(BaseMaterial3D.BillboardModeEnum.FixedY);
        return quad;
    }

    private static Mesh SnowMesh()
    {
        var quad = new QuadMesh { Size = new Vector2(2f, 2f) };
        quad.Material = WeatherMaterial(BaseMaterial3D.BillboardModeEnum.Particles);
        return quad;
    }

    private static StandardMaterial3D WeatherMaterial(BaseMaterial3D.BillboardModeEnum billboard) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        BlendMode = BaseMaterial3D.BlendModeEnum.Add,
        BillboardMode = billboard,
        BillboardKeepScale = true,
        VertexColorUseAsAlbedo = true,
        DisableReceiveShadows = true,
    };

    private static Color PaletteGray(int index)
    {
        int i = System.Math.Clamp(index, 0, QuakeGrayRamp.Length - 1);
        float g = QuakeGrayRamp[i] / 255f;
        return new Color(g, g, g);
    }

    private static Vector3 AbsToGodot(NVec3 q)
    {
        Vector3 g = Coords.ToGodot(q);
        return new Vector3(MathF.Abs(g.X), MathF.Abs(g.Y), MathF.Abs(g.Z));
    }
}
