using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay.Nades;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>
/// Renders the networked nade <b>orb</b> entities — the heal / ammo / entrap / veil (and any other
/// orb-type) fountain spheres a thrown nade leaves behind. The Godot successor to the CSQC
/// <c>orb_setup</c> / <c>orb_draw</c> path (qcsrc/common/mutators/mutator/nades/nade.qc): the server spawns
/// a <c>nade_orb</c> entity (<see cref="VortexArena.Net.NetEntityKind.NadeOrb"/>) carrying its
/// <c>OrbType</c> (a <see cref="NadeRegistry"/> id), absolute <c>OrbExpire</c> time and <c>OrbRadius</c>;
/// this turns each into a tinted sphere that
/// <list type="bullet">
///   <item>SCALES UP from nothing toward its radius over the first ~0.25s of life (QC
///   <c>self.scale = bound(0,(time-spawn)*4,1)*radius</c>),</item>
///   <item>FADES + shrinks over its last ~0.25s as the orb expires (QC
///   <c>alpha = bound(0,(self.ltime-time)*4,1)</c>),</item>
///   <item>SPINS slowly about world-up (QC <c>avelocity = '0 0 90'</c>).</item>
/// </list>
///
/// Modeled on <see cref="ProjectileRenderer"/>: a per-orb root <see cref="Node3D"/> at the orb's origin with
/// a <c>Body</c> child built from <c>models/sphere.md3</c> (host-wired <see cref="ModelFactory"/>) tinted to
/// the per-type <see cref="NadeDef.Color"/>; when the model is absent it falls back to an additive glow
/// sprite. The heavy tinted material is cached per orb-type, mirroring
/// <see cref="ProjectileRenderer"/>'s <c>_bodyResCache</c>.
///
/// It owns nothing about gameplay: <see cref="OnSpawn"/>/<see cref="OnUpdate"/>/<see cref="OnRemove"/> are
/// driven by <see cref="ClientWorld"/> from the entity stream, and <see cref="Process"/> advances each live
/// orb's scale/fade/spin every frame. <see cref="ActiveOrbs"/> exposes the live orbs (Quake-space origin,
/// radius, color, current alpha) so <c>ViewEffects.UpdateOrbColorFlash</c> can paint the 2D in-orb screen
/// tint. Origins are Quake-space and converted at the boundary with <see cref="Coords"/>.
/// </summary>
public partial class NadeOrbRenderer : Node3D
{
    /// <summary>The Godot visual bound to one nade-orb entity.</summary>
    private sealed class Orb
    {
        public required Entity Entity;
        public required Node3D Root;     // positioned at the orb origin each frame
        public Node3D? Body;             // the sphere/sprite that scales/fades/spins (child of Root)
        public Sprite3D? Sprite;         // non-null when the Body is the additive-glow fallback
        public List<MeshInstance3D> Meshes = new(); // the real-model meshes whose material alpha we drive
        public byte Type;                // OrbType — the NadeRegistry id (1..11)
        public Color Tint;               // the per-type m_color (NadeDef.Color), opaque
        public float Radius;             // OrbRadiusClient — the orb radius in qu (drives target scale + flash test)
        public float SpawnTime;          // client clock time the orb was first seen (scale-up anchor)
        public float Expire;             // NadeOrbExpire — absolute client clock time the orb fades out
        public float Alpha = 1f;         // current rendered alpha (cached for ActiveOrbs / the 2D flash)
    }

    /// <summary>The model scale of a 1-unit sphere model so that <c>scale = radius/SphereModelRadius</c> draws
    /// a sphere of the orb's radius. <c>models/sphere.md3</c> is authored ~16qu radius (DP's stock unit sphere).
    /// The procedural fallback sphere is built at radius 1 so it uses the raw radius scale.</summary>
    private const float SphereModelRadius = 16f;

    private readonly Dictionary<int, Orb> _orbs = new();

    /// <summary>Reused per-frame iteration buffer so <see cref="Process"/> never allocates a snapshot list.</summary>
    private readonly List<Orb> _iterBuffer = new();

    /// <summary>Reused snapshot for <see cref="ActiveOrbs"/> so the per-frame 2D flash query never allocates.</summary>
    private readonly List<(NVec3 Origin, float Radius, Color Color, float Alpha)> _activeBuffer = new();

    /// <summary>Soft round glow sprite texture for the additive-sprite fallback — shared across orbs.</summary>
    private static ImageTexture? _glowTexture;

    /// <summary>
    /// Builds a fully-textured render node for a model VFS path (host-set to <c>AssetLoader.LoadModel</c>), used
    /// to draw the orb with its real <c>models/sphere.md3</c> sphere. Null (or a null return / missing content)
    /// falls back to the additive glow sprite — keeps headless tests and asset-less runs working.
    /// </summary>
    public Func<string, Node3D?>? ModelFactory { get; set; }

    /// <summary>The sphere model the orb draws (QC <c>setmodel("models/sphere.md3")</c> in <c>orb_setup</c>).</summary>
    private const string OrbModelPath = "models/sphere.md3";

    // Per-orb-type cache of the tinted sphere Mesh + material, mirroring ProjectileRenderer._bodyResCache. Every
    // orb of a given type draws an IDENTICAL sphere (radius is applied via node scale, color derives from the
    // NadeDef), so the heavy Resources are built once and SHARED across instances.
    private static readonly Dictionary<byte, (Mesh Mesh, StandardMaterial3D Mat)> _bodyResCache = new();

    // =================================================================================================
    //  Lifecycle hooks (driven by ClientWorld)
    // =================================================================================================

    /// <summary>Begin rendering a nade-orb entity. Idempotent: re-spawning the same index updates in place.</summary>
    public void OnSpawn(Entity orb)
    {
        if (orb is null)
            return;
        if (_orbs.TryGetValue(orb.Index, out Orb? existing))
        {
            existing.Entity = orb;
            OnUpdate(orb);
            return;
        }

        byte type = (byte)Mathf.Clamp(orb.NadeBonusType, 0, 11);
        Color tint = TintFor(type);
        float radius = orb.OrbRadiusClient > 0f ? orb.OrbRadiusClient : 250f;

        var root = new Node3D { Name = $"nadeorb#{orb.Index}_{type}" };

        // Real sphere.md3 when the host wired a factory and the content is present, else the additive-glow
        // fallback. The sphere is symmetric, so the slow world-up spin reads identically either way.
        Sprite3D? sprite = null;
        var meshes = new List<MeshInstance3D>();
        Node3D body = BuildModelBody(type, tint, meshes);
        if (body is null)
        {
            sprite = BuildGlowSprite(tint);
            body = sprite;
            meshes.Clear();
        }
        body.Name = "Body";
        // Start fully collapsed; Process scales it up over the first ~0.25s (QC orb spawns at scale 0).
        body.Scale = Vector3.Zero;
        root.AddChild(body);

        root.Position = Coords.ToGodot(orb.Origin);
        AddChild(root);

        var entry = new Orb
        {
            Entity = orb,
            Root = root,
            Body = body,
            Sprite = sprite,
            Meshes = meshes,
            Type = type,
            Tint = tint,
            Radius = radius,
            SpawnTime = Now(),
            Expire = orb.NadeOrbExpire,
        };
        _orbs[orb.Index] = entry;

        // Bind as the entity's presence link so the sim can find its node (mirrors EntityNode.Bind).
        orb.Presence ??= new NadeOrbPresence(root);
    }

    /// <summary>Pull one orb's current origin/type/radius/expiry onto its visual (called per network update).</summary>
    public void OnUpdate(Entity orb)
    {
        if (orb is null || !_orbs.TryGetValue(orb.Index, out Orb? o))
            return;
        o.Entity = orb;
        o.Expire = orb.NadeOrbExpire;
        if (orb.OrbRadiusClient > 0f)
            o.Radius = orb.OrbRadiusClient;

        // OrbType can in principle change (a fountain re-keyed mid-life); recolour the body if so. ClientEntityView
        // routes the orb's networked OrbType onto the proxy entity's NadeBonusType field (it has no dedicated orb-type
        // member; the field is unused on a non-player orb entity).
        byte type = (byte)Mathf.Clamp(orb.NadeBonusType, 0, 11);
        if (type != o.Type)
        {
            o.Type = type;
            o.Tint = TintFor(type);
            ApplyTint(o);
        }
        // _Process does the actual position follow / scale / fade / spin.
    }

    /// <summary>Stop rendering an orb (it expired / was cleaned up). Frees the node.</summary>
    public void OnRemove(int index)
    {
        if (!_orbs.Remove(index, out Orb? o))
            return;
        if (GodotObject.IsInstanceValid(o.Root))
            o.Root.QueueFree();
    }

    /// <summary>True if an orb entity is currently rendered.</summary>
    public bool IsTracking(int index) => _orbs.ContainsKey(index);

    /// <summary>Number of live orb visuals (diagnostics).</summary>
    public int LiveCount => _orbs.Count;

    // =================================================================================================
    //  Per-frame scale-up / fade / spin
    // =================================================================================================

    public override void _Process(double delta)
    {
        Process((float)delta);
    }

    /// <summary>Advance every orb's position follow, scale-up, fade and spin (call once per frame).</summary>
    public void Process(float delta)
    {
        if (_orbs.Count == 0)
            return;

        // House rule: a new per-frame node ships with a Prof scope registered in FrameProfiler.TopLevelNodeScopes
        // (else its time leaks into proc:other). Placed after the empty early-out so it costs nothing when idle.
        using var _prof = FrameProfiler.Scope("nadeorbs");
        float now = Now();

        // Iterate over a snapshot since a removal could mutate the dictionary mid-loop.
        _iterBuffer.Clear();
        foreach (Orb o in _orbs.Values)
            _iterBuffer.Add(o);

        foreach (Orb o in _iterBuffer)
        {
            if (o.Entity.IsFreed)
            {
                OnRemove(o.Entity.Index);
                continue;
            }
            if (!GodotObject.IsInstanceValid(o.Root))
                continue;

            // Follow the orb's authoritative origin (the orb is static after spawn, but freezetag/spawn orbs
            // and the napalm ball can move, so track it each frame rather than pinning at spawn).
            o.Root.Position = Coords.ToGodot(o.Entity.Origin);

            if (o.Body is null || !GodotObject.IsInstanceValid(o.Body))
                continue;

            // (a) SCALE-UP from spawn (QC orb_draw: self.scale grows toward radius over ~0.25s):
            //     grow = bound(0, (time - spawn) * 4, 1).
            float grow = Mathf.Clamp((now - o.SpawnTime) * 4f, 0f, 1f);

            // (b) FADE in the last ~0.25s (QC orb_draw: alpha = bound(0, (self.ltime - time) * 4, 1)):
            //     when no expire is known (0), stay fully opaque.
            float fade = o.Expire > 0f ? Mathf.Clamp((o.Expire - now) * 4f, 0f, 1f) : 1f;
            o.Alpha = fade;

            // The sphere model / glow sprite is authored at SphereModelRadius; scale so the drawn radius is the
            // orb radius, then modulate by both the spawn grow ramp and the expiry fade (so it SHRINKS as it fades).
            float modelScale = o.Radius / SphereModelRadius;
            o.Body.Scale = Vector3.One * (modelScale * grow * fade);

            ApplyAlpha(o, fade);

            // (c) SPIN about world-up (QC avelocity '0 0 90' → 90 deg/sec yaw). In Godot, Quake-Z is Godot-Y,
            //     so the yaw axis is Vector3.Up. RotateObjectLocal keeps the scale untouched.
            o.Body.RotateObjectLocal(Vector3.Up, Mathf.DegToRad(90f * delta));
        }
    }

    /// <summary>
    /// The live orbs as (Quake-space Origin, OrbRadiusClient, m_color tint, current alpha) — fed to
    /// <c>ViewEffects.UpdateOrbColorFlash</c> for the 2D in-orb color flash containment test. Reuses a
    /// persistent buffer; the result is valid until the next call.
    /// </summary>
    public IReadOnlyList<(NVec3 Origin, float Radius, Color Color, float Alpha)> ActiveOrbs()
    {
        _activeBuffer.Clear();
        foreach (Orb o in _orbs.Values)
        {
            if (o.Entity.IsFreed)
                continue;
            _activeBuffer.Add((o.Entity.Origin, o.Radius, o.Tint, o.Alpha));
        }
        return _activeBuffer;
    }

    // =================================================================================================
    //  Node construction
    // =================================================================================================

    /// <summary>
    /// Build the orb's real <c>models/sphere.md3</c> body via the host <see cref="ModelFactory"/>, tinted to the
    /// per-type color, collecting its meshes into <paramref name="meshes"/> so <see cref="ApplyAlpha"/> can drive
    /// their transparency each frame. Returns null when no factory is wired or the model is absent (caller then
    /// uses the additive-glow fallback).
    /// </summary>
    private Node3D? BuildModelBody(byte type, Color tint, List<MeshInstance3D> meshes)
    {
        if (ModelFactory is null)
            return null;
        Node3D? model = ModelFactory(OrbModelPath);
        if (model is null)
            return null;

        // Tint the sphere meshes to the orb color with an alpha-capable material so the expiry fade reads.
        (Mesh _, StandardMaterial3D mat) = BodyRes(type, tint);
        CollectMeshes(model, meshes);
        foreach (MeshInstance3D mi in meshes)
            mi.MaterialOverride = mat;
        return model;
    }

    /// <summary>Shared tinted sphere Mesh + alpha-capable material for an orb type — built once, reused.</summary>
    private static (Mesh Mesh, StandardMaterial3D Mat) BodyRes(byte type, Color tint)
    {
        if (_bodyResCache.TryGetValue(type, out (Mesh Mesh, StandardMaterial3D Mat) res))
            return res;
        // A unit-radius sphere for the procedural fallback (scaled to the orb radius via node scale).
        var mesh = new SphereMesh { Radius = 1f, Height = 2f };
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(tint.R, tint.G, tint.B, 1f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            EmissionEnabled = true,
            Emission = tint,
            EmissionEnergyMultiplier = 0.6f,
            Metallic = 0.0f,
            Roughness = 0.7f,
        };
        res = (mesh, mat);
        _bodyResCache[type] = res;
        return res;
    }

    /// <summary>The additive-glow fallback body when the sphere model is absent (headless / asset-less runs).</summary>
    private static Sprite3D BuildGlowSprite(Color tint)
    {
        return new Sprite3D
        {
            Name = "Glow",
            Texture = _glowTexture ??= MakeGlowTexture(),
            Modulate = new Color(tint.R, tint.G, tint.B, 1f),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            Shaded = false,
            DoubleSided = true,
            // A unit pixel-size: with the orb scale (~radius) applied the billboard covers the orb footprint.
            PixelSize = 1f / SphereModelRadius,
            NoDepthTest = false,
            AlphaCut = SpriteBase3D.AlphaCutMode.Disabled,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
        };
    }

    /// <summary>Re-tint a live orb's body after an OrbType change (recolour meshes / glow sprite).</summary>
    private void ApplyTint(Orb o)
    {
        if (o.Sprite is not null && GodotObject.IsInstanceValid(o.Sprite))
        {
            o.Sprite.Modulate = new Color(o.Tint.R, o.Tint.G, o.Tint.B, o.Sprite.Modulate.A);
            return;
        }
        (Mesh _, StandardMaterial3D mat) = BodyRes(o.Type, o.Tint);
        foreach (MeshInstance3D mi in o.Meshes)
            if (GodotObject.IsInstanceValid(mi))
                mi.MaterialOverride = mat;
    }

    /// <summary>Drive the orb body's render alpha for the spawn/expiry fade. The glow sprite uses its modulate
    /// alpha; the real-model meshes use a per-orb material alpha (so concurrent orbs of a type don't share a
    /// fading material, a fresh per-instance material is set the first time the alpha leaves 1).</summary>
    private void ApplyAlpha(Orb o, float alpha)
    {
        if (o.Sprite is not null && GodotObject.IsInstanceValid(o.Sprite))
        {
            Color m = o.Sprite.Modulate;
            o.Sprite.Modulate = new Color(m.R, m.G, m.B, alpha);
            return;
        }
        // Real model: only diverge from the shared opaque material once the orb is actually fading, then keep a
        // per-instance material so each orb fades independently.
        foreach (MeshInstance3D mi in o.Meshes)
        {
            if (!GodotObject.IsInstanceValid(mi))
                continue;
            if (mi.MaterialOverride is not StandardMaterial3D mat)
                continue;
            if (alpha >= 1f && mat.AlbedoColor.A >= 1f)
                continue;
            if (mat.AlbedoColor.A >= 1f && alpha < 1f)
            {
                // Clone the shared material so fading this orb doesn't dim every orb of the type.
                mat = (StandardMaterial3D)mat.Duplicate();
                mi.MaterialOverride = mat;
            }
            Color a = mat.AlbedoColor;
            mat.AlbedoColor = new Color(a.R, a.G, a.B, alpha);
        }
    }

    /// <summary>Depth-first collect every <see cref="MeshInstance3D"/> at or under <paramref name="node"/>.</summary>
    private static void CollectMeshes(Node node, List<MeshInstance3D> dst)
    {
        if (node is MeshInstance3D mi)
            dst.Add(mi);
        foreach (Node child in node.GetChildren())
            CollectMeshes(child, dst);
    }

    /// <summary>The per-type orb tint — the <see cref="NadeDef.Color"/> (QC <c>m_color</c>): heal red, ammo blue,
    /// entrap green, veil pale-green, etc. Unknown ids fall back to white.</summary>
    private static Color TintFor(byte type)
    {
        NadeRegistry.RegisterAll();
        NadeDef? def = NadeRegistry.ById(type);
        if (def is null)
            return Colors.White;
        NVec3 c = def.Color;
        // The QC m_color is an RGB tint; some defs leave it (0,0,0) — treat that as untinted white.
        if (c.X <= 0f && c.Y <= 0f && c.Z <= 0f)
            return Colors.White;
        return new Color(c.X, c.Y, c.Z, 1f);
    }

    /// <summary>Build a soft round glow texture for the fallback sprite (radial alpha falloff).</summary>
    private static ImageTexture MakeGlowTexture()
    {
        const int size = 32;
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        float c = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = (x - c) / c, dy = (y - c) / c;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            float a = Mathf.Clamp(1f - d, 0f, 1f);
            a *= a; // sharpen the core
            img.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>Current client clock time (mirrors <see cref="ProjectileRenderer"/>'s clock): the simulation clock
    /// the orb's spawn-time and expiry are measured against. 0 in a headless/clockless harness — there the orb's
    /// spawn time seeds from this same 0, so the scale-up still resolves to grow=0 then ramps once the clock runs.</summary>
    private static float Now()
        => VortexArena.Common.Services.Api.Services?.Clock?.Time ?? 0f;

    /// <summary>Presence link for an orb entity (so the sim can reach its node, like EntityNode).</summary>
    private sealed class NadeOrbPresence : IEntityPresence
    {
        public Node3D Node { get; }
        public NadeOrbPresence(Node3D node) => Node = node;
    }
}
