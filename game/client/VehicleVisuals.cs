using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Formats.Md3;
using VKind = VortexArena.Game.Client.VehicleCatalog.VehicleKind;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>
/// The client-side visual driver for a single vehicle — the Godot successor to the per-vehicle CSQC
/// presentation the libs flag with the bulk of the 27 <c>TODO(port,client)</c> markers
/// (Base/.../qcsrc/common/vehicles/vehicle/*.qc <c>vr_spawn</c>/<c>*_frame</c>/<c>vr_death</c>). One of these is
/// attached per networked vehicle entity; it builds the body + sub-models from <see cref="VehicleCatalog"/> and
/// each frame turns a fed <see cref="VehicleVisualState"/> into:
///
/// <list type="bullet">
///   <item><b>Rotor spinners</b> — the Raptor's two counter-rotating props (engine_left +900°/s,
///         engine_right −900°/s), spinning while the vehicle is alive.</item>
///   <item><b>Gun-barrel spin</b> — the Spiderbot minigun barrels accelerate while firing and spin down when
///         idle (the "barrels" tag).</item>
///   <item><b>Engine sounds</b> — idle/move crossfaded by speed, with a boost overlay (Racer), as looping
///         spatial sources (SND_VEH_* engine set).</item>
///   <item><b>Scale-0.5 model hack</b> — the Racer's half-scale body.</item>
///   <item><b>Frame-driven body</b> — the Spiderbot's walk/strafe/idle/jump leg frames.</item>
///   <item><b>Gib sub-entities</b> — on death, debris chunks burst from the body and fall + fade, with the
///         death sound and an explosion.</item>
///   <item><b>Heal-beam (BRG_*)</b> — the Bumblebee's networked heal beam, drawn from the gun tag to the heal
///         target each frame while active.</item>
///   <item><b>Muzzle flash</b> — the primary gun's EFFECT_*_MUZZLEFLASH on the firing edge.</item>
/// </list>
///
/// Dependencies (model/effects/beams/sound) are injected so the node stays host-agnostic; a missing model just
/// renders placeholder bodies/rotors at the catalog's fallback offsets, so every mechanism is visible even
/// without the exact <c>.dpm</c> art. Coordinates: state vectors are Quake-space and convert at the boundary.
/// </summary>
public partial class VehicleVisuals : Node3D
{
    /// <summary>The live presentation inputs for a vehicle (fed by the host / net layer each frame).</summary>
    public struct State
    {
        /// <summary>Engine load 0..1 (crossfades idle→move sound; 0 = parked).</summary>
        public float Speed01;
        /// <summary>Primary gun firing this frame (barrel spin + muzzle flash).</summary>
        public bool Firing;
        /// <summary>Boost active (Racer boost sound overlay).</summary>
        public bool Boosting;
        /// <summary>Alive — set false once to trigger the death gibs.</summary>
        public bool Alive;
        /// <summary>Body frame for frame-driven vehicles (Spiderbot legs).</summary>
        public int Frame;
        /// <summary>Heal-beam end point in Quake space (Bumblebee BRG_*); null = no beam.</summary>
        public NVec3? HealTarget;

        public static State Default => new() { Alive = true, Speed01 = 0f };
    }

    private VehicleCatalog.Desc _desc = null!;
    private Node3D _body = null!;
    private ModelAnimator? _bodyAnim;                 // frame-driven legs (spiderbot)
    private readonly List<Node3D> _rotors = new();     // continuous spinners (raptor props)
    private readonly List<(Node3D node, Node3D? barrel)> _guns = new();
    private float _barrelSpin;                          // current minigun barrel speed (deg/s)
    private MeshInstance3D? _healBeam;                  // persistent heal beam (bumblebee)

    private AudioStreamPlayer3D? _engineIdle, _engineMove, _engineBoost;
    private bool _dead;
    private bool _wasFiring;

    // Injected dependencies (host-wired; all optional → graceful placeholders/misses).
    public EffectSystem? Effects { get; set; }
    public BeamRenderer? Beams { get; set; }
    public Func<string, Md3Data?>? ModelResolver { get; set; }
    /// <summary>Material facade for texturing the vehicle body/guns (ModelLoader.BuildModel/ModelAnimator material resolution).</summary>
    public VortexArena.Game.Loaders.AssetSystem? Assets { get; set; }
    public Func<string, string?>? SoundResolver { get; set; }
    /// <summary>VFS-backed sample → <see cref="AudioStream"/> loader (host-set to <c>AssetLoader.LoadSound</c>);
    /// tried before the <see cref="SoundResolver"/> <c>res://</c> fallback so engine/idle/boost loops play from
    /// the mounted content packs.</summary>
    public Func<string, AudioStream?>? AudioLoader { get; set; }

    /// <summary>The networked vehicle entity this visual reflects (set by the renderer; drives the per-frame state).</summary>
    public VortexArena.Common.Framework.Entity? Bound { get; set; }

    /// <summary>Build the visuals for a vehicle kind. Call once after construction + dependency injection.</summary>
    public void Build(VKind kind)
    {
        VehicleCatalog.Desc? desc = VehicleCatalog.DescOf(kind);
        if (desc is null)
        {
            // Not a known vehicle — a neutral placeholder so something renders.
            _desc = new VehicleCatalog.Desc { Kind = VKind.None };
            AddChild(Placeholder("Body", new Vector3(48f, 24f, 64f), new Color(0.4f, 0.4f, 0.45f)));
            return;
        }
        _desc = desc;

        BuildBody();
        BuildRotors();
        BuildGuns();
        BuildEngineSounds();
        if (_desc.HealBeam)
            BuildHealBeam();
    }

    /// <summary>Convenience: classify by classname/model, then build.</summary>
    public void Build(string classNameOrModel) => Build(VehicleCatalog.Classify(classNameOrModel));

    // =====================================================================================
    //  Per-frame drive
    // =====================================================================================

    /// <summary>Feed the live presentation state (call each frame). Triggers death gibs on the alive→dead edge.</summary>
    public void Apply(in State s, float delta)
    {
        if (!_dead && !s.Alive)
        {
            Die(s);
            return;
        }
        if (_dead)
            return;

        // Rotors spin in _Process (self-driven, so they turn whether or not a host pushes state each frame).
        DriveBarrels(s, delta);
        DriveEngineSound(s);
        DriveBodyFrame(s);
        DriveHealBeam(s);
        DriveMuzzle(s);
    }

    public override void _Process(double delta)
    {
        using var _scope = FrameProfiler.Scope("vehicle.vis"); // [profiling] §18: out of proc:other
        // If the host doesn't push state, at least keep the rotors turning + idle sound while alive.
        if (!_dead && _rotors.Count > 0)
            SpinRotors((float)delta);
    }

    // =====================================================================================
    //  Build
    // =====================================================================================

    private void BuildBody()
    {
        Md3Data? md3 = _desc.Model.Length > 0 ? ModelResolver?.Invoke(_desc.Model) : null;
        if (md3 is not null && md3.FrameCount > 1 && _desc.FrameDrivenBody)
        {
            _bodyAnim = ModelAnimator.Create(md3, "Body", Assets);
            _body = _bodyAnim;
            AddChild(_bodyAnim);
        }
        else if (md3 is not null)
        {
            _body = new Node3D { Name = "Body" };
            _body.AddChild(ModelLoader.BuildModel(md3, 0, Assets));
            if (md3.Tags.Count > 0) _body.AddChild(ModelLoader.BuildTags(md3, 0));
            AddChild(_body);
        }
        else
        {
            _body = new Node3D { Name = "Body" };
            _body.AddChild(Placeholder("Hull", new Vector3(48f, 22f, 70f), new Color(0.35f, 0.37f, 0.4f)));
            AddChild(_body);
        }

        if (_desc.Scale is > 0f and not 1f)
            _body.Scale = Vector3.One * _desc.Scale; // Racer scale-0.5 model hack
    }

    private void BuildRotors()
    {
        foreach (VehicleCatalog.RotorSpec spec in _desc.Rotors)
        {
            var rotor = new Node3D { Name = $"rotor_{spec.Tag}" };
            // A simple two-blade prop placeholder (or the prop model if a tag-mounted child exists later).
            rotor.AddChild(Placeholder("Prop", new Vector3(56f, 2f, 6f), new Color(0.15f, 0.15f, 0.17f)));
            Mount(rotor, spec.Tag, spec.FallbackOffset);
            rotor.SetMeta("spin", spec.SpinDegPerSec);
            _rotors.Add(rotor);
        }
    }

    private void BuildGuns()
    {
        foreach (VehicleCatalog.GunSpec spec in _desc.Guns)
        {
            var gun = new Node3D { Name = $"gun_{spec.Tag}" };
            gun.AddChild(Placeholder("GunBody", new Vector3(8f, 8f, 26f), new Color(0.2f, 0.2f, 0.22f)));

            Node3D? barrel = null;
            if (!string.IsNullOrEmpty(spec.BarrelTag))
            {
                barrel = new Node3D { Name = "barrels" };
                barrel.AddChild(Placeholder("Barrel", new Vector3(5f, 5f, 30f), new Color(0.12f, 0.12f, 0.13f)));
                barrel.Position = new Vector3(0f, 0f, -14f);
                gun.AddChild(barrel);
            }
            Mount(gun, spec.Tag, spec.FallbackOffset);
            _guns.Add((gun, barrel));
        }
    }

    private void BuildEngineSounds()
    {
        _engineIdle = LoopPlayer("EngineIdle", _desc.Engine.Idle, 0f);
        _engineMove = LoopPlayer("EngineMove", _desc.Engine.Move, 0f);
        if (!string.IsNullOrEmpty(_desc.Engine.Boost))
            _engineBoost = LoopPlayer("EngineBoost", _desc.Engine.Boost!, 0f);
    }

    private void BuildHealBeam()
    {
        // A persistent additive green beam, hidden until a heal target is fed (Bumblebee BRG_START/END).
        _healBeam = new MeshInstance3D
        {
            Name = "HealBeam",
            Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                AlbedoColor = new Color(0.3f, 1f, 0.4f, 0.8f),
            },
        };
        AddChild(_healBeam);
    }

    // =====================================================================================
    //  Drive
    // =====================================================================================

    private void SpinRotors(float delta)
    {
        foreach (Node3D rotor in _rotors)
        {
            if (!GodotObject.IsInstanceValid(rotor)) continue;
            Vector3 spin = rotor.GetMeta("spin", Vector3.Zero).AsVector3();
            rotor.RotateObjectLocal(Vector3.Up, Mathf.DegToRad(spin.Y * delta));
            if (spin.X != 0f) rotor.RotateObjectLocal(Vector3.Right, Mathf.DegToRad(spin.X * delta));
            if (spin.Z != 0f) rotor.RotateObjectLocal(Vector3.Back, Mathf.DegToRad(spin.Z * delta));
        }
    }

    private void DriveBarrels(in State s, float delta)
    {
        // The minigun barrels spool up while firing (toward ~1800°/s) and spin down when idle (QC barrel spin).
        float target = s.Firing ? 1800f : 0f;
        _barrelSpin = Mathf.MoveToward(_barrelSpin, target, 4000f * delta);
        if (_barrelSpin <= 0.01f) return;
        foreach ((Node3D _, Node3D? barrel) in _guns)
            if (barrel is not null && GodotObject.IsInstanceValid(barrel))
                barrel.RotateObjectLocal(Vector3.Back, Mathf.DegToRad(_barrelSpin * delta));
    }

    private void DriveEngineSound(in State s)
    {
        // Crossfade idle↔move by speed; overlay boost. (QC engine sound by throttle.)
        float move = Mathf.Clamp(s.Speed01, 0f, 1f);
        SetVol(_engineIdle, (1f - move) * 0.6f + 0.15f);
        SetVol(_engineMove, move * 0.8f);
        SetVol(_engineBoost, s.Boosting ? 0.9f : 0f);
    }

    private void DriveBodyFrame(in State s)
    {
        // Spiderbot legs: the server-computed body frame plays directly (walk/strafe/idle/jump).
        if (_bodyAnim is not null)
            _bodyAnim.SetRawFrame(s.Frame);
    }

    private void DriveHealBeam(in State s)
    {
        if (_healBeam is null) return;
        if (s.HealTarget is not { } targetQuake)
        {
            _healBeam.Visible = false;
            return;
        }

        // Build a thin quad strip from the heal gun (this vehicle's origin-ish) to the target, in this node's
        // local space. The gun origin is the mounted heal-gun tag if present, else the body center.
        Vector3 start = HealGunLocalOrigin();
        Vector3 end = ToLocal(Coords.ToGodot(targetQuake));
        _healBeam.Mesh = BuildBeamQuad(start, end, 3.5f);
        _healBeam.Visible = true;
    }

    private void DriveMuzzle(in State s)
    {
        if (s.Firing && !_wasFiring && Effects is not null && _desc.MuzzleEffect.Length > 0 && _guns.Count > 0)
        {
            // Flash at the first gun's muzzle (world → Quake for the effect system).
            Node3D gun = _guns[0].node;
            if (GodotObject.IsInstanceValid(gun))
            {
                Vector3 muzzleGodot = gun.GlobalPosition - gun.GlobalTransform.Basis.Z * 18f;
                Effects.MuzzleFlash(_desc.MuzzleEffect, Coords.ToQuake(muzzleGodot), Coords.ToQuake(-gun.GlobalTransform.Basis.Z) * 200f);
            }
        }
        _wasFiring = s.Firing;
    }

    // =====================================================================================
    //  Death — gib sub-entities (vr_death)
    // =====================================================================================

    private void Die(in State s)
    {
        _dead = true;

        SetVol(_engineIdle, 0f); SetVol(_engineMove, 0f); SetVol(_engineBoost, 0f);
        if (_healBeam is not null) _healBeam.Visible = false;

        // Explosion + death sound at the body.
        Effects?.Explosion(Coords.ToQuake(GlobalPosition));
        PlayOneShot(_desc.DeathSound);

        // Gib chunks: small debris that bursts out, falls, and fades (QC gib entities).
        var rng = new Random(GetInstanceId().GetHashCode());
        for (int i = 0; i < _desc.GibCount; i++)
            SpawnGib(rng);

        // Hide the intact body/guns/rotors; the node self-frees after the gibs settle.
        if (GodotObject.IsInstanceValid(_body)) _body.Visible = false;
        foreach (Node3D r in _rotors) if (GodotObject.IsInstanceValid(r)) r.Visible = false;
        foreach ((Node3D g, _) in _guns) if (GodotObject.IsInstanceValid(g)) g.Visible = false;

        SceneTreeTimer t = GetTree().CreateTimer(3.0f);
        t.Timeout += () => { if (GodotObject.IsInstanceValid(this)) QueueFree(); };
    }

    private void SpawnGib(Random rng)
    {
        var gib = new MeshInstance3D
        {
            Name = "gib",
            Mesh = new BoxMesh { Size = new Vector3(8f + (float)rng.NextDouble() * 8f, 8f, 8f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = _desc.GibTint, Metallic = 0.3f },
            Position = new Vector3((float)(rng.NextDouble() - 0.5) * 20f, 10f, (float)(rng.NextDouble() - 0.5) * 20f),
        };
        AddChild(gib);

        // Toss it out and let it fall (a short scripted arc; the sim never sees these cosmetic gibs).
        var vel = new Vector3((float)(rng.NextDouble() - 0.5) * 140f, 120f + (float)rng.NextDouble() * 120f,
            (float)(rng.NextDouble() - 0.5) * 140f);
        var spin = new Vector3((float)rng.NextDouble() * 8f, (float)rng.NextDouble() * 8f, (float)rng.NextDouble() * 8f);
        var driver = new GibDriver { Gib = gib, Velocity = vel, Spin = spin };
        AddChild(driver);
    }

    /// <summary>A tiny self-contained mover for a cosmetic vehicle gib (gravity arc + tumble + fade).</summary>
    private sealed partial class GibDriver : Node
    {
        public MeshInstance3D Gib = null!;
        public Vector3 Velocity;
        public Vector3 Spin;
        private float _age;

        public override void _Process(double delta)
        {
            float dt = (float)delta;
            _age += dt;
            if (!GodotObject.IsInstanceValid(Gib)) { QueueFree(); return; }
            Velocity += new Vector3(0f, -680f * dt, 0f); // gravity (Godot up = +Y)
            Gib.Position += Velocity * dt;
            Gib.Rotation += Spin * dt;
            if (_age > 2.5f) { Gib.QueueFree(); QueueFree(); }
        }
    }

    // =====================================================================================
    //  Helpers
    // =====================================================================================

    /// <summary>Attach a sub-node at the body tag marker if the model exposes one, else at the fallback offset.</summary>
    private void Mount(Node3D child, string tag, Vector3 fallbackOffset)
    {
        Marker3D? marker = FindTagMarker(tag);
        if (marker is not null)
        {
            marker.AddChild(child);
        }
        else
        {
            child.Position = fallbackOffset;
            AddChild(child);
        }
    }

    private Marker3D? FindTagMarker(string tag)
    {
        if (_bodyAnim is not null)
            return _bodyAnim.GetTag(tag);
        // Search the static body's tag markers (ModelLoader.BuildTags markers are named by tag).
        return GodotObject.IsInstanceValid(_body) ? FindMarkerRecursive(_body, tag) : null;
    }

    private static Marker3D? FindMarkerRecursive(Node node, string name)
    {
        foreach (Node c in node.GetChildren())
        {
            if (c is Marker3D m && m.Name == name) return m;
            Marker3D? deep = FindMarkerRecursive(c, name);
            if (deep is not null) return deep;
        }
        return null;
    }

    private Vector3 HealGunLocalOrigin()
    {
        Marker3D? m = FindTagMarker(_desc.HealGunTag);
        if (m is not null && GodotObject.IsInstanceValid(m))
            return ToLocal(m.GlobalPosition);
        return new Vector3(0f, 8f, -24f); // fallback: front-ish of the body
    }

    private static ArrayMesh BuildBeamQuad(Vector3 a, Vector3 b, float width)
    {
        Vector3 seg = b - a;
        Vector3 dir = seg.LengthSquared() > 1e-6f ? seg.Normalized() : Vector3.Forward;
        Vector3 side = dir.Cross(Vector3.Up);
        if (side.LengthSquared() < 1e-4f) side = dir.Cross(Vector3.Right);
        side = side.Normalized() * (width * 0.5f);

        var verts = new Vector3[] { a - side, a + side, b + side, b - side };
        var uvs = new Vector2[] { new(0, 0), new(1, 0), new(1, 1), new(0, 1) };
        var idx = new int[] { 0, 1, 2, 0, 2, 3 };

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = idx;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>Load a sample to an <see cref="AudioStream"/>: the VFS audio loader first, then res:// path.</summary>
    private AudioStream? LoadStream(string sample)
    {
        AudioStream? stream = AudioLoader?.Invoke(sample);
        if (stream is not null) return stream;
        string? resPath = SoundResolver is not null ? SoundResolver(sample) : $"res://sound/{sample}.ogg";
        if (string.IsNullOrEmpty(resPath) || !ResourceLoader.Exists(resPath)) return null;
        try { return ResourceLoader.Load<AudioStream>(resPath); } catch { return null; }
    }

    private AudioStreamPlayer3D? LoopPlayer(string name, string sample, float startVol)
    {
        AudioStream? stream = LoadStream(sample);
        if (stream is null) return null;

        // Seamless looping via the shared helper (Ogg/MP3 loop natively on a duplicated stream; other types fall
        // back to a Finished→replay) — the same looping path ClientWorld uses for the Arc beam loop. The engine
        // idle/move/boost crossfade rides three of these, mixed by volume in DriveEngineSound.
        AudioStream looping = AudioLoop.MakeLooping(stream, out bool nativeLoop);
        var p = new AudioStreamPlayer3D
        {
            Name = name,
            Stream = looping,
            MaxDistance = 3072f,
            VolumeDb = startVol <= 0f ? -80f : Mathf.LinearToDb(startVol),
            Autoplay = true,
        };
        if (!nativeLoop)
            p.Finished += () => { if (GodotObject.IsInstanceValid(p)) p.Play(); }; // keep the loop alive (WAV/other)
        AddChild(p);
        return p;
    }

    private static void SetVol(AudioStreamPlayer3D? p, float linear)
    {
        if (p is null || !GodotObject.IsInstanceValid(p)) return;
        p.VolumeDb = linear <= 0.001f ? -80f : Mathf.LinearToDb(Mathf.Clamp(linear, 0.001f, 1f));
    }

    private void PlayOneShot(string sample)
    {
        if (string.IsNullOrEmpty(sample)) return;
        AudioStream? stream = LoadStream(sample);
        if (stream is null) return;
        var p = new AudioStreamPlayer3D { Name = "death", Stream = stream, MaxDistance = 4096f, Autoplay = true };
        AddChild(p);
    }

    private static MeshInstance3D Placeholder(string name, Vector3 size, Color color)
        => new()
        {
            Name = name,
            Mesh = new BoxMesh { Size = size },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = color, Metallic = 0.3f, Roughness = 0.7f },
        };
}
