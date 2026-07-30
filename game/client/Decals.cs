using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Game;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>
/// Projected impact decals — bullet holes / scorch marks / blood stains left on the surface a shot or
/// explosion hit. The C# successor to Darkplaces' decal particles (<c>pt_decal</c> handled in
/// CL_NewParticlesFromEffectinfo, and the cl_decals system): an effectinfo block with <c>type decal</c>
/// spawns one here instead of a free-flying particle.
///
/// DP projects the decal along the impact <i>velocity</i> direction (AnglesFromVectors(velocity) in the
/// pt_decal branch; cl_decals_newsystem_bloodsmears uses particle velocity as the projection axis). We use
/// Godot's <see cref="Decal"/> node, which projects its texture down its local -Y through whatever geometry
/// is inside its box — so we orient -Y along the impact direction and size the box to the decal radius. No
/// raycast is needed; the engine clips the projection to the real surface, and decals on nothing are simply
/// invisible. Each decal fades out after a lifetime (cl_decals_time + cl_decals_fadetime) and self-frees.
/// </summary>
public sealed partial class Decals : Node3D
{
    /// <summary>How long a decal stays at full strength before it starts fading (DP cl_decals_time is 20;
    /// trimmed here so a long match doesn't accumulate thousands of nodes).</summary>
    [Export] public float DecalTime { get; set; } = 12f;

    /// <summary>Fade-out duration once <see cref="DecalTime"/> elapses (DP cl_decals_fadetime).</summary>
    [Export] public float FadeTime { get; set; } = 2f;

    /// <summary>Hard cap on live decals; oldest recycled past this (DP cl_decals_max, scaled down).</summary>
    [Export] public int MaxDecals { get; set; } = 256;

    private readonly Queue<Decal> _live = new();

    // Cache one solid-color texture per quantised tint so we don't allocate an ImageTexture per impact.
    private readonly Dictionary<int, ImageTexture> _texCache = new();

    /// <summary>
    /// Drop a decal at a Quake-space <paramref name="origin"/>, projected along <paramref name="dir"/>
    /// (the impact velocity, Quake space; zero =&gt; project straight down). <paramref name="radius"/> is the
    /// decal half-size in world units (effectinfo <c>size</c>); <paramref name="color"/> tints it and
    /// <paramref name="alpha"/> (0..1) is its opacity. Returns the created node (already in the tree).
    /// </summary>
    public Decal Spawn(NVec3 origin, NVec3 dir, float radius, Color color, float alpha = 1f, Texture2D? sprite = null)
    {
        radius = Math.Clamp(radius <= 0f ? 8f : radius, 1f, 256f);
        Vector3 gpos = Coords.ToGodot(origin);
        Vector3 gdir = dir == default ? Vector3.Down : Coords.ToGodot(dir).Normalized();

        // Build a basis whose -Y (Godot's decal projection axis) points along the impact direction. We pick
        // an arbitrary perpendicular for the in-plane axes; the mark is roughly radial so their roll is moot.
        Basis basis = BasisProjecting(gdir);

        // Box: wide/tall to the decal radius, depth a THIN slab so it grabs only the near surface. DP decals
        // are flat quads ON the surface (zero wrap); a deep Godot box projects through whatever it overlaps,
        // smearing the mark across corners and parallel geometry behind the wall.
        float depth = Math.Clamp(radius * 0.25f, 2f, 8f);

        // Prefer the real particlefont decal SPRITE (scorch / blood splat shape) the effect declared; fall
        // back to a generated soft disc when the atlas isn't mounted. Modulate tints + fades either one.
        var decal = new Decal
        {
            Name = "decal",
            Size = new Vector3(radius * 2f, depth, radius * 2f),
            Transform = new Transform3D(basis, gpos),
            TextureAlbedo = sprite ?? SolidTexture(color),
            Modulate = new Color(color.R, color.G, color.B, Math.Clamp(alpha, 0f, 1f)),
            AlbedoMix = 1f,
            // Fade the projection out on surfaces angled away from the projection axis — the box-projection
            // equivalent of DP's flat surface quad: without it the mark wraps around corners/edges onto
            // perpendicular faces inside the box.
            NormalFade = 0.35f,
            // Bias upper distance fade so it stays visible at gameplay ranges (DP intensitymultiplier).
            DistanceFadeEnabled = false,
        };

        AddChild(decal);
        FadeAndFree(decal);
        Track(decal);
        return decal;
    }

    /// <summary>
    /// Project a decal onto the NEAREST surface around a Quake-space <paramref name="origin"/>, mirroring DP's
    /// <c>CL_SpawnDecalParticleForPoint</c> (Base/darkplaces/cl_particles.c:981): fire 32 random rays out to
    /// <paramref name="maxDist"/> (the effectinfo <c>originjitter[0]</c>), keep the closest non-sky/non-NOMARKS
    /// hit, and orient the decal by that hit's surface NORMAL — the emit velocity plays no role (unlike the
    /// caller-supplied-direction <see cref="Spawn"/>). No surface within range =&gt; no decal (returns null),
    /// matching DP's <c>if (bestfrac &lt; 1)</c> guard. Used by the <c>type decal</c> effectinfo path.
    /// </summary>
    public Decal? SpawnProjected(NVec3 origin, float maxDist, float radius, Color color, float alpha = 1f,
        Texture2D? sprite = null)
    {
        // maxdist comes from originjitter[0]; DP's bullet decals use 6, the explosion scorch uses 40. Clamp the
        // ray length to a sane minimum so a zero-jitter block (which DP would still trace out to 0 and find
        // nothing for) at least probes the immediate surface the impact point sits on.
        float dist = MathF.Max(maxDist, 1f);

        if (Api.Services is null)
            // No trace service (e.g. a bare render test) — fall back to a straight-down projection so the mark
            // still appears rather than vanishing. DP always has a world to trace.
            return Spawn(origin, default, radius, color, alpha, sprite);

        bool found = false;
        float bestFrac = 1f;
        NVec3 bestPos = origin;
        NVec3 bestNormal = new(0f, 0f, 1f);

        // 32 rays out to maxdist on the unit sphere (DP VectorRandom + VectorMA), nearest non-NOMARKS wins.
        for (int i = 0; i < 32; i++)
        {
            NVec3 dir = RandomUnitVector();
            NVec3 end = origin + dir * dist;
            TraceResult tr = Api.Trace.Trace(origin, NVec3.Zero, NVec3.Zero, end, MoveFilter.WorldOnly, null);
            if (tr.Fraction >= bestFrac)
                continue;
            // Skip sky and surfaces flagged NOMARKS (sky etc.), exactly as DP filters bestfrac candidates.
            if ((tr.DpHitContents & SuperContents.Sky) != 0)
                continue;
            if ((tr.DpHitQ3SurfaceFlags & Q3SurfaceFlags.NoMarks) != 0)
                continue;
            bestFrac = tr.Fraction;
            bestPos = tr.EndPos;
            bestNormal = tr.PlaneNormal;
            found = true;
        }

        if (!found)
            return null; // DP: bestfrac stayed >= 1 → no surface in range, no decal.

        // DP orients the decal by the surface normal (CL_SpawnDecalParticleForSurface). Our Spawn() projects
        // a decal DOWN its local -Y along the passed direction, so feed it the inward direction (-normal): the
        // decal then projects into the surface it sits on.
        return Spawn(bestPos, -bestNormal, radius, color, alpha, sprite);
    }

    /// <summary>A uniformly-distributed unit vector — the C# mirror of DP's <c>VectorRandom</c> (rejection
    /// sample the unit cube, reject points outside/at the origin of the unit sphere, then normalise).</summary>
    private static NVec3 RandomUnitVector()
    {
        // DP's VectorRandom loops until |v|^2 in (0,1]; reproduce so the ray spread matches.
        for (int tries = 0; tries < 16; tries++)
        {
            float x = (float)GD.RandRange(-1.0, 1.0);
            float y = (float)GD.RandRange(-1.0, 1.0);
            float z = (float)GD.RandRange(-1.0, 1.0);
            float len2 = x * x + y * y + z * z;
            if (len2 > 0.0001f && len2 <= 1f)
            {
                float inv = 1f / MathF.Sqrt(len2);
                return new NVec3(x * inv, y * inv, z * inv);
            }
        }
        return new NVec3(0f, 0f, 1f);
    }

    // ------------------------------------------------------------------------------------------------

    /// <summary>An orthonormal basis whose local -Y axis equals <paramref name="down"/> (the projection
    /// direction). Used to aim a Godot Decal's projection along an arbitrary impact direction.</summary>
    private static Basis BasisProjecting(Vector3 down)
    {
        Vector3 y = (-down).Normalized();          // Decal projects toward -Y, so +Y is opposite the impact
        if (y.LengthSquared() < 0.0001f) y = Vector3.Up;
        // Pick a reference not parallel to y to derive the in-plane axes.
        Vector3 reference = MathF.Abs(y.Dot(Vector3.Up)) > 0.95f ? Vector3.Right : Vector3.Up;
        Vector3 x = reference.Cross(y).Normalized();
        Vector3 z = x.Cross(y).Normalized();
        return new Basis(x, y, z);
    }

    /// <summary>A cached 1x1 solid-color texture for the decal albedo (quantised by 5-bit-per-channel key).</summary>
    private ImageTexture SolidTexture(Color color)
    {
        int key = ((int)(color.R * 31) << 10) | ((int)(color.G * 31) << 5) | (int)(color.B * 31);
        if (_texCache.TryGetValue(key, out ImageTexture? cached))
            return cached;

        // A small radial disc gives a rounder mark than a flat square; bake a 16x16 alpha falloff.
        const int N = 16;
        var img = Image.CreateEmpty(N, N, false, Image.Format.Rgba8);
        float cx = (N - 1) * 0.5f, cy = (N - 1) * 0.5f, r = N * 0.5f;
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float d = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / r;
                float a = Math.Clamp(1f - d, 0f, 1f);
                a = a * a; // soften the edge
                img.SetPixel(x, y, new Color(color.R, color.G, color.B, a));
            }
        var tex = ImageTexture.CreateFromImage(img);
        _texCache[key] = tex;
        return tex;
    }

    private void FadeAndFree(Decal decal)
    {
        SceneTree? tree = IsInsideTree() ? GetTree() : decal.GetTree();
        if (tree is null)
        {
            decal.QueueFree();
            return;
        }

        // Hold, then tween Modulate alpha to zero over FadeTime, then free. Mirrors cl_decals_time/fadetime.
        // Bind the tween to the decal so it dies with the node (no dangling tween if it's culled early).
        var tween = decal.CreateTween();
        Color full = decal.Modulate;
        Color gone = new(full.R, full.G, full.B, 0f);
        tween.TweenProperty(decal, "modulate", full, DecalTime); // hold
        tween.TweenProperty(decal, "modulate", gone, FadeTime);  // fade
        tween.TweenCallback(Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(decal))
                decal.QueueFree();
        }));
    }

    private void Track(Decal decal)
    {
        _live.Enqueue(decal);
        while (_live.Count > MaxDecals && _live.TryDequeue(out Decal? old))
        {
            if (GodotObject.IsInstanceValid(old))
                old.QueueFree();
        }
    }
}
