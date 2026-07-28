using Godot;
using XonoticGodot.Formats.Materials;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Formats.Vmap;
using XonoticGodot.Game.Loaders;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Vmap;

/// <summary>
/// Real-time lighting for the edited world (design doc §10.1, rung 1).
///
/// The shipped renderer draws the world with a lightmap baked by q3map2 into UV2 — data derived FROM the
/// geometry, which is exactly what the editor is changing. Move a wall and the bake describes a map that no
/// longer exists, so the editor drew fullbright instead: legible, but with no way to judge a room's contrast
/// or whether a corner is too dark. This lights the edited world from the map's OWN light definitions
/// instead, so what you see responds to what you build with no bake and no latency.
///
/// Two sources, because Xonotic maps use both and either alone is unrecognisable:
/// <list type="bullet">
///   <item><b>Point lights</b> — the <c>light</c> entities, which survive into the BSP only when the map was
///     compiled with <c>-keeplights</c>. Many maps were not, hence the ambient floor below.</item>
///   <item><b>Surface lights</b> — shaders carrying <c>q3map_surfaceLight</c>: the glowing strips and panels
///     that do most of the actual lighting work. They become emissive materials here. Emission alone does not
///     illuminate neighbours without GI — that is rung 2 — but it restores the read of a lit room, and the
///     same value is what an area light or the progressive baker will consume later.</item>
/// </list>
///
/// Shadow casting is budgeted rather than universal: a map like stormkeep has 119 point lights, and shadowed
/// omnis cost a cubemap render each. Only the nearest few to the camera cast, which is where a mapper is
/// looking anyway.
/// </summary>
public sealed partial class EditorLighting : Node3D
{
    /// <summary>Master switch: 0 draws the world fullbright, as before this existed.</summary>
    public const string CvarEnabled = "cl_editor_lighting";

    /// <summary>How many of the nearest point lights cast real shadows. 0 disables shadows entirely.</summary>
    public const string CvarShadowBudget = "cl_editor_light_shadows";

    /// <summary>Overall brightness multiplier on the map's own light values, for taste.</summary>
    public const string CvarBrightness = "cl_editor_light_scale";

    /// <summary>
    /// Ambient floor so an unlit or <c>-keeplights</c>-less map is dim rather than pitch black. A map with no
    /// recoverable lights at all must still be editable.
    /// </summary>
    public const string CvarAmbient = "cl_editor_light_ambient";

    /// <summary>
    /// Global illumination: 0 off, 1 SDFGI, 2 VoxelGI (design doc §10.1 rung 2).
    ///
    /// DEFAULT 0, because neither technique works on this content yet and both were measured, not assumed.
    /// On stormkeep, with the ambient floor removed so nothing could mask the result:
    /// <list type="bullet">
    ///   <item><b>SDFGI</b> moved mean scene brightness 40.6 → 41.2, and stayed at ~41 no matter how cell size
    ///     (1/4/32), cascade count or energy (up to 16×) were set. That flatness across every knob is the
    ///     signature of a technique with nothing to gather: SDFGI takes its light from the DIRECTIONAL light
    ///     and the sky, and an Xonotic interior has neither — 119 omni fixtures under a sealed roof, with a
    ///     dark space skybox outside.</item>
    ///   <item><b>VoxelGI</b> does inject point lights, but as configured it made the scene markedly DARKER
    ///     (40.6 → 11.0) rather than adding bounce — an arena-sized volume at Subdiv256 is ~15 units per
    ///     voxel, and something in that bake is losing light rather than propagating it.</item>
    /// </list>
    /// Both paths are left in and switchable so the next attempt starts from working plumbing rather than from
    /// scratch; neither is fit to be a default.
    /// </summary>
    public const string CvarGlobalIllumination = "cl_editor_gi";

    /// <summary>Smallest SDFGI cascade cell, in Quake units. Smaller resolves finer geometry and costs more.</summary>
    public const string CvarGiCellSize = "cl_editor_gi_cellsize";

    /// <summary>SDFGI cascade count (1-8). More covers a larger map at the same detail, for more memory.</summary>
    public const string CvarGiCascades = "cl_editor_gi_cascades";

    /// <summary>Bounce strength.</summary>
    public const string CvarGiEnergy = "cl_editor_gi_energy";

    /// <summary>
    /// Quake light intensity → Godot omni RANGE, in Quake units. q3map2's point falloff is
    /// <c>intensity / distance²</c> scaled by <c>-pointscale</c>; the useful radius is therefore about
    /// sqrt(intensity) times a constant. Tuned so stormkeep's <c>light 40</c> fixtures reach across a
    /// corridor rather than dying at the fitting.
    /// </summary>
    private const float RangePerSqrtIntensity = 48f;

    /// <summary>Clamp so one enormous light value cannot swallow the map.</summary>
    private const float MaxRange = 2048f;

    private readonly List<OmniLight3D> _points = new();
    private DirectionalLight3D? _sun;
    private float _nextShadowSort;

    /// <summary>Number of lights built (diagnostics / HUD).</summary>
    public int LightCount => _points.Count;

    /// <summary>True when a sun was recovered from the map's sky shader rather than defaulted.</summary>
    public bool HasMapSun { get; private set; }

    public static bool Enabled(CvarService? cvars) => ReadFloat(cvars, CvarEnabled, 1f) != 0f;

    /// <summary>
    /// Build the light rig for <paramref name="doc"/>. Returns a node holding every light; add it to the
    /// scene alongside the world it lights.
    /// </summary>
    public static EditorLighting Build(VmapDocument doc, AssetSystem assets, CvarService? cvars)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(assets);

        var rig = new EditorLighting { Name = "EditorLighting" };
        float brightness = ReadFloat(cvars, CvarBrightness, 1f);

        foreach (VmapEntity entity in doc.Entities)
        {
            if (!entity.ClassName.Equals("light", StringComparison.OrdinalIgnoreCase))
                continue;
            if (rig.TryBuildPointLight(entity, doc, brightness) is { } light)
            {
                rig._points.Add(light);
                rig.AddChild(light);
            }
        }

        rig.BuildSun(doc, assets, brightness);
        return rig;
    }

    /// <summary>
    /// One <c>light</c> entity → an omni (or a spot, when it aims at a <c>target</c>).
    /// </summary>
    private OmniLight3D? TryBuildPointLight(VmapEntity entity, VmapDocument doc, float brightness)
    {
        if (!entity.Fields.TryGetValue("origin", out string? originText)
            || !TryVector(originText, out NVec3 origin))
            return null;

        // QC/Q3 default when the key is absent is 300 (q3map2 light.c).
        float intensity = entity.Fields.TryGetValue("light", out string? lightText)
            && float.TryParse(lightText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float parsed)
            ? parsed
            : 300f;
        if (intensity <= 0f)
            return null;

        Color color = Colors.White;
        if (entity.Fields.TryGetValue("_color", out string? colorText) && TryVector(colorText, out NVec3 c))
            color = new Color(c.X, c.Y, c.Z);

        float range = Math.Min(MaxRange, MathF.Sqrt(intensity) * RangePerSqrtIntensity);

        var light = new OmniLight3D
        {
            Name = $"EditorLight_{entity.Id}",
            Position = Coords.ToGodot(origin),
            LightColor = color,
            // Energy is deliberately NOT the raw Quake intensity: that number feeds an inverse-square bake,
            // while range already encodes the falloff here. Keeping energy near 1 leaves the map's relative
            // brightness in the range, where it reads correctly, and leaves one cvar for overall taste.
            LightEnergy = brightness,
            OmniRange = range,
            ShadowEnabled = false,       // granted by budget in Update()
            LightSpecular = 0.25f,
        };
        _ = doc;
        return light;
    }

    /// <summary>
    /// The sun, from the sky shader's <c>q3map_sun</c> when the map defines one. Xonotic skies usually do;
    /// when none is found a soft default from above keeps exteriors readable rather than flat.
    /// </summary>
    private void BuildSun(VmapDocument doc, AssetSystem assets, float brightness)
    {
        SunParms? sun = null;

        foreach (VmapBrush brush in doc.Brushes)
        {
            foreach (VmapFace face in brush.Faces)
            {
                if ((face.SurfaceFlags & VmapGeometryBuilder.SurfaceSky) == 0)
                    continue;
                if (assets.GetShader(face.Material.Replace('\\', '/')) is { Sun: { } found })
                {
                    sun = found;
                    break;
                }
            }
            if (sun is not null)
                break;
        }

        HasMapSun = sun is not null;

        // Quake convention: `degrees` is the compass angle the light comes FROM, `elevation` its height above
        // the horizon. Build the direction it travels TOWARDS, then convert to Godot.
        float degrees = sun?.Degrees ?? 215f;
        float elevation = sun?.Elevation ?? 45f;
        float yaw = Mathf.DegToRad(degrees);
        float pitch = Mathf.DegToRad(elevation);

        var fromSun = new NVec3(
            -MathF.Cos(yaw) * MathF.Cos(pitch),
            -MathF.Sin(yaw) * MathF.Cos(pitch),
            -MathF.Sin(pitch));

        var light = new DirectionalLight3D
        {
            Name = "EditorSun",
            LightColor = sun is not null ? new Color(sun.Red, sun.Green, sun.Blue) : new Color(1f, 0.96f, 0.9f),
            // q3map_sun intensity is in bake units (typically 50-300); map it into a sane real-time energy
            // rather than passing it through, which would blow the exposure out entirely.
            LightEnergy = brightness * (sun is not null ? Math.Clamp(sun.Intensity / 150f, 0.15f, 2f) : 0.6f),
            ShadowEnabled = true,
            LightSpecular = 0.2f,
        };
        light.LookAtFromPosition(Vector3.Zero, Coords.ToGodot(fromSun), Vector3.Up);

        _sun = light;
        AddChild(light);
    }

    /// <summary>
    /// Per-frame upkeep: hand the shadow budget to the lights nearest the camera. Re-sorted a few times a
    /// second rather than every frame — the budget only has to follow the mapper around, and sorting 119
    /// lights per frame to change which eight cast shadows would cost more than the shadows.
    /// </summary>
    public void Update(Vector3 cameraPosition, CvarService? cvars, float now)
    {
        int budget = (int)ReadFloat(cvars, CvarShadowBudget, 6f);

        if (_sun is not null)
            _sun.ShadowEnabled = budget > 0;

        if (now < _nextShadowSort)
            return;
        _nextShadowSort = now + 0.25f;

        if (budget <= 0)
        {
            foreach (OmniLight3D light in _points)
                if (GodotObject.IsInstanceValid(light))
                    light.ShadowEnabled = false;
            return;
        }

        _sorted.Clear();
        for (int i = 0; i < _points.Count; i++)
        {
            OmniLight3D light = _points[i];
            if (!GodotObject.IsInstanceValid(light))
                continue;
            // Distance to the light's REACH, not its centre: a big light whose centre is far can still be the
            // one lighting the room the camera is standing in.
            float d = cameraPosition.DistanceTo(light.Position) - light.OmniRange;
            _sorted.Add((d, i));
        }
        _sorted.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));

        for (int rank = 0; rank < _sorted.Count; rank++)
        {
            OmniLight3D light = _points[_sorted[rank].Index];
            if (GodotObject.IsInstanceValid(light))
                light.ShadowEnabled = rank < budget;
        }
    }

    private readonly List<(float Distance, int Index)> _sorted = new();

    /// <summary>
    /// Environment setup for the lit editor: the ambient floor, and global illumination (design doc §10.1
    /// rung 2) when it is switched on.
    ///
    /// SDFGI is Godot's runtime GI — cascaded signed-distance fields, no bake, no chart atlas, and it survives
    /// geometry edits by construction, which is exactly the property the editing loop needs. It is also what
    /// turns direct-only lighting from flat into readable: the bounce fills the shadowed side of everything,
    /// and, more importantly here, the map's EMISSIVE surfaces (the <c>q3map_surfaceLight</c> strips and
    /// panels) finally light the rooms they are in rather than merely glowing.
    ///
    /// The ambient floor drops hard when GI is on. A flat ambient term and a GI solution are both "fill light",
    /// and running the old floor underneath the bounce would wash out precisely the contrast GI restores.
    /// </summary>
    public static void ApplyEnvironment(Godot.Environment env, CvarService? cvars)
    {
        if (env is null)
            return;

        int giMode = (int)ReadFloat(cvars, CvarGlobalIllumination, 1f);
        bool gi = giMode != 0;
        bool sdfgi = giMode == 1;

        // Applied repeatedly (the settings are cvars a mapper changes mid-session), so skip the write when
        // nothing moved — re-assigning SdfgiEnabled would otherwise restart the cascades every frame.
        int signature = HashCode.Combine(gi, ReadFloat(cvars, CvarAmbient, 0.18f),
            ReadFloat(cvars, CvarGiCellSize, 8f), ReadFloat(cvars, CvarGiCascades, 4f),
            ReadFloat(cvars, CvarGiEnergy, 1.6f));
        if (_appliedEnv == env && _appliedSignature == signature)
            return;

        // The game's own environment is SHARED, not ours: stash what it had the first time we touch it so
        // leaving the editor puts the match's lighting back rather than leaving it on our editing preset.
        if (_appliedEnv != env)
        {
            _savedAmbientSource = env.AmbientLightSource;
            _savedAmbientColor = env.AmbientLightColor;
            _savedAmbientEnergy = env.AmbientLightEnergy;
            _savedSdfgi = env.SdfgiEnabled;
        }
        _appliedEnv = env;
        _appliedSignature = signature;

        // With GI supplying the fill, the flat term only has to stop pure black in a cascade's blind spot.
        float ambient = ReadFloat(cvars, CvarAmbient, 0.18f) * (gi ? 0.25f : 1f);
        env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        env.AmbientLightColor = new Color(0.55f, 0.58f, 0.66f);
        env.AmbientLightEnergy = ambient;

        env.SdfgiEnabled = sdfgi;
        if (!sdfgi)
            return;

        // Q3 rooms are small: a corridor is ~128-256 units and the smallest cascade cell has to resolve
        // features at that scale or the bounce leaks through walls. Godot works in metres and the port treats
        // one Quake unit as one Godot unit, so the cell size is in Quake units too.
        env.SdfgiMinCellSize = ReadFloat(cvars, CvarGiCellSize, 8f);
        env.SdfgiCascades = (int)Math.Clamp(ReadFloat(cvars, CvarGiCascades, 4f), 1f, 8f);
        env.SdfgiUseOcclusion = true;          // without it light bleeds through the thin walls Q3 maps are full of
        env.SdfgiBounceFeedback = 0.5f;        // multi-bounce; the second bounce is most of the "lit room" read
        env.SdfgiEnergy = ReadFloat(cvars, CvarGiEnergy, 1.6f);
        env.SdfgiNormalBias = 1.1f;
        // Indoor Q3 maps are sealed, so sky light would only reach through the sky brushes that are genuinely
        // open — which is the correct behaviour and how the outdoor parts of a map get their fill.
        env.SdfgiReadSkyLight = true;
    }

    /// <summary>
    /// Build and bake a <see cref="VoxelGI"/> covering the map — the GI technique that actually applies here.
    ///
    /// SDFGI gathers its light from the DIRECTIONAL light and the sky only; point lights never inject into its
    /// cascades. An Xonotic interior is lit by neither — 119 omni fixtures under a sealed roof, with a dark
    /// space skybox outside — so SDFGI measured a flat ~1.5% brightness change on stormkeep no matter how its
    /// cell size, cascade count or energy were set. VoxelGI voxelises the scene and injects every light type,
    /// which is why it is the one that can see this map's lighting at all.
    ///
    /// The trade is that it needs an explicit bake and a bounded volume, so it is re-baked when the world is
    /// rebuilt rather than following edits for free.
    /// </summary>
    public static VoxelGI? BuildVoxelGi(VmapDocument doc, Node worldRoot, CvarService? cvars)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(worldRoot);

        if ((int)ReadFloat(cvars, CvarGlobalIllumination, 1f) != 2)
            return null;

        // The volume has to cover the map, and a VoxelGI's detail is its size divided by the subdivision, so an
        // arena-sized box is inherently coarse. That is the documented cost of this rung.
        var min = new NVec3(float.MaxValue);
        var max = new NVec3(float.MinValue);
        bool any = false;
        foreach (VmapBrush brush in doc.Brushes)
        {
            if (brush.IsToolBrush || !VmapWinding.TryGetBounds(brush, out NVec3 bmin, out NVec3 bmax))
                continue;
            min = NVec3.Min(min, bmin);
            max = NVec3.Max(max, bmax);
            any = true;
        }
        if (!any)
            return null;

        Vector3 gmin = Coords.ToGodot(min), gmax = Coords.ToGodot(max);
        Vector3 lo = gmin.Min(gmax), hi = gmin.Max(gmax);

        var voxel = new VoxelGI
        {
            Name = "EditorVoxelGi",
            Subdiv = VoxelGI.SubdivEnum.Subdiv256,
            Size = (hi - lo) + new Vector3(64f, 64f, 64f),
            Position = (lo + hi) * 0.5f,
        };
        worldRoot.AddChild(voxel);
        voxel.Bake(worldRoot, true);
        return voxel;
    }

    /// <summary>Put back the environment the match had before the editor imposed its own preset.</summary>
    public static void RestoreEnvironment()
    {
        if (_appliedEnv is null || !GodotObject.IsInstanceValid(_appliedEnv))
        {
            _appliedEnv = null;
            return;
        }

        _appliedEnv.AmbientLightSource = _savedAmbientSource;
        _appliedEnv.AmbientLightColor = _savedAmbientColor;
        _appliedEnv.AmbientLightEnergy = _savedAmbientEnergy;
        _appliedEnv.SdfgiEnabled = _savedSdfgi;
        _appliedEnv = null;
        _appliedSignature = 0;
    }

    private static Godot.Environment? _appliedEnv;
    private static int _appliedSignature;
    private static Godot.Environment.AmbientSource _savedAmbientSource;
    private static Color _savedAmbientColor;
    private static float _savedAmbientEnergy;
    private static bool _savedSdfgi;

    private static bool TryVector(string text, out NVec3 v)
    {
        v = NVec3.Zero;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
            return false;
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float, ic, out float x)
            || !float.TryParse(parts[1], System.Globalization.NumberStyles.Float, ic, out float y)
            || !float.TryParse(parts[2], System.Globalization.NumberStyles.Float, ic, out float z))
            return false;
        v = new NVec3(x, y, z);
        return true;
    }

    private static float ReadFloat(CvarService? cvars, string name, float fallback)
    {
        if (cvars is null)
            return fallback;
        string s = cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : cvars.GetFloat(name);
    }
}
