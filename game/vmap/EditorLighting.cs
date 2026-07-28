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

    /// <summary>Ambient floor, applied to the world environment so an unlit map is dim rather than black.</summary>
    public static void ApplyAmbient(Godot.Environment env, CvarService? cvars)
    {
        if (env is null)
            return;
        float ambient = ReadFloat(cvars, CvarAmbient, 0.18f);
        env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        env.AmbientLightColor = new Color(0.55f, 0.58f, 0.66f);
        env.AmbientLightEnergy = ambient;
    }

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
