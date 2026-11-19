using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay.Nades;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>Draws a networked grenade orb and supplies its hull and opacity to the view flash.
/// The native snapshot carries expiry and size. First sight approximates the spawn time.</summary>
public partial class NadeOrbRenderer : Node3D
{
    /// <summary>The Godot visual bound to one nade-orb entity.</summary>
    private sealed class Orb
    {
        public required Entity Entity;
        public required Node3D Root;     // positioned at the orb origin each frame
        public Node3D? Body;             // the sphere/sprite that scales/fades/spins (child of Root)
        public Sprite3D? Sprite;         // non-null when the Body is the additive-glow fallback
        public StandardMaterial3D? Material; // private to this orb for independent tint and fade
        public Vector3 SpinAxis;
        public byte Type;                // OrbType — the NadeRegistry id (1..11)
        public Color Tint;               // the per-type m_color (NadeDef.Color), opaque
        public float Radius;             // OrbRadiusClient — the orb radius in qu (drives target scale + flash test)
        public float SpawnTime;          // client clock time the orb was first seen (scale-up anchor)
        public float Expire;             // NadeOrbExpire — absolute client clock time the orb fades out
        public float Alpha = 1f;         // current rendered alpha (cached for ActiveOrbs / the 2D flash)
    }

    // Frame zero maxs.x in the shipped MD3. Xonotic orb_setup uses the model's X extent.
    private const float ShieldModelRadius = 104.29747f;

    private readonly Dictionary<int, Orb> _orbs = new();

    /// <summary>Reused per-frame iteration buffer so <see cref="Process"/> never allocates a snapshot list.</summary>
    private readonly List<Orb> _iterBuffer = new();

    /// <summary>Reused snapshot for <see cref="ActiveOrbs"/> so the per-frame 2D flash query never allocates.</summary>
    private readonly List<(NVec3 Origin, float Radius, Color Color, float Alpha)> _activeBuffer = new();

    /// <summary>Soft round glow sprite texture for the additive-sprite fallback — shared across orbs.</summary>
    private static ImageTexture? _glowTexture;

    public Func<string, Node3D?>? ModelFactory { get; set; }
    public Func<string, Texture2D?>? TextureFactory { get; set; }
    public Func<float>? ServerTime { get; set; }
    private const string OrbModelPath = "models/ctf/shield.md3";

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

        Sprite3D? sprite = null;
        StandardMaterial3D? material = null;
        Node3D? body = ModelFactory?.Invoke(OrbModelPath);
        if (body is null)
            body = sprite = BuildGlowSprite(tint);
        else
        {
            material = new StandardMaterial3D
            {
                AlbedoColor = tint,
                AlbedoTexture = TextureFactory?.Invoke("textures/nade_orb"),
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            };
            var meshes = new List<MeshInstance3D>();
            CollectMeshes(body, meshes);
            foreach (var mesh in meshes)
                mesh.MaterialOverride = material;
        }
        float angle = GD.Randf() * Mathf.Tau;
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
            Material = material,
            SpinAxis = new Vector3(Mathf.Sin(angle), Mathf.Cos(angle), 0f),
            Type = type,
            Tint = tint,
            Radius = radius,
            SpawnTime = ServerTime?.Invoke() ?? 0f,
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
            SetColor(o);
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
        float now = ServerTime?.Invoke() ?? 0f;

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

            float age = MathF.Max(0f, now - o.SpawnTime);
            float lifetime = MathF.Max(0.001f, o.Expire - o.SpawnTime);
            float progress = Mathf.Clamp(age / lifetime, 0f, 1f);
            float t = Mathf.Clamp(age / (0.25f * MathF.Min(lifetime, 1f)), 0f, 1f);
            float grow = t * (2f - t);
            o.Alpha = Mathf.Sqrt(1f - progress);
            float scale = o.Radius * 0.6f / (o.Sprite is null ? ShieldModelRadius : 16f);
            // Keep scale off the rotation basis until after rotating. A zero scale destroys its orientation.
            float phase = 6f * lifetime * (Mathf.Pow(24f, progress) - 1f) / Mathf.Log(24f);
            o.Body.Basis = new Basis(o.SpinAxis, Mathf.DegToRad(phase))
                .Scaled(Vector3.One * MathF.Max(0.0001f, scale * grow));
            SetColor(o);
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
            PixelSize = 1f,
            NoDepthTest = false,
            AlphaCut = SpriteBase3D.AlphaCutMode.Disabled,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
        };
    }

    private static void SetColor(Orb o)
    {
        Color color = new(o.Tint.R, o.Tint.G, o.Tint.B, o.Alpha);
        if (o.Material is not null)
            o.Material.AlbedoColor = color;
        else if (o.Sprite is not null)
            o.Sprite.Modulate = color;
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

    /// <summary>Presence link for an orb entity (so the sim can reach its node, like EntityNode).</summary>
    private sealed class NadeOrbPresence : IEntityPresence
    {
        public Node3D Node { get; }
        public NadeOrbPresence(Node3D node) => Node = node;
    }
}
