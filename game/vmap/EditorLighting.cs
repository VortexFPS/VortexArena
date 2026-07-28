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
    /// Global illumination (SDFGI) for live bounce — design doc §10.1 rung 2. DEFAULT OFF.
    ///
    /// It works, and it does gather from the map's own lights: measured on stormkeep it adds +2.47 mean
    /// brightness with those lights present and +0.07 with them zeroed. But enabling it SUPPRESSES those same
    /// lights' direct real-time contribution on GI-static geometry — Godot expects the GI solution to supply
    /// them. Measured with the sun off so nothing else could contribute: point lights alone give 8.78 mean
    /// with SDFGI off and 2.77 with it on, regardless of the lights' bake mode. Trading the map's own
    /// fixtures for a bounce term a fraction of their size is a bad trade, so this stays off until the
    /// interaction is understood.
    /// </summary>
    public const string CvarGlobalIllumination = "cl_editor_gi";

    /// <summary>Smallest SDFGI cascade cell, in Quake units. Smaller resolves finer geometry and costs more.</summary>
    public const string CvarGiCellSize = "cl_editor_gi_cellsize";

    /// <summary>SDFGI cascade count (1-8). More covers a larger map at the same detail, for more memory.</summary>
    public const string CvarGiCascades = "cl_editor_gi_cascades";

    /// <summary>Bounce strength.</summary>
    public const string CvarGiEnergy = "cl_editor_gi_energy";

    /// <summary>
    /// Light3D bake mode for the map's point lights: 0 disabled, 1 static, 2 dynamic. This is what decides
    /// whether a light participates in global illumination at all, so it is the knob that determines whether
    /// GI can see the lights the map is actually lit by.
    /// </summary>
    public const string CvarLightBakeMode = "cl_editor_light_bakemode";

    /// <summary>
    /// Screen-space ambient occlusion — the live stand-in for the <c>-dirty</c> pass q3map2 bakes into a
    /// lightmap (stormkeep compiled with <c>-dirty -dirtscale 2</c>). This is what puts the dark back in
    /// corners and creases; without it, direct light alone leaves every junction the same brightness as the
    /// flats around it, which is most of what reads as "flat" next to a baked map.
    /// </summary>
    public const string CvarSsao = "cl_editor_ssao";

    /// <summary>AO strength.</summary>
    public const string CvarSsaoIntensity = "cl_editor_ssao_intensity";

    /// <summary>AO sampling radius in QUAKE units — a corner is only dark within about this distance of it.</summary>
    public const string CvarSsaoRadius = "cl_editor_ssao_radius";

    /// <summary>
    /// Light falloff exponent. 2 is inverse-square, which is what q3map2 bakes and what gives tight bright
    /// pools falling to dark; Godot's default of 1 is a soft linear-ish ramp that spreads every fixture's
    /// light evenly across a room and flattens it.
    /// </summary>
    public const string CvarFalloff = "cl_editor_light_falloff";

    /// <summary>
    /// Let the sky light the world. ON is Godot's default and it is why the editor world never went properly
    /// dark: with a sky background every surface receives reflected sky light regardless of the map's own
    /// lighting, measured at 42.4 of the 47.7 mean brightness on stormkeep with every light and the ambient
    /// floor switched off. A sealed Q3 interior should be lit by its fixtures, not by the sky outside it.
    /// </summary>
    public const string CvarSkyLight = "cl_editor_sky_light";

    /// <summary>
    /// Quake light intensity → Godot omni RANGE, in Quake units. q3map2's point falloff is
    /// <c>intensity / distance²</c>, so the radius at which a light stops mattering goes as sqrt(intensity)
    /// times a constant that folds in q3map2's pointscale and its cutoff threshold.
    ///
    /// This constant was 48, which put a typical <c>light 40</c> fixture's range at 304 units. Measured on
    /// stormkeep, the NEAREST fixture to the camera was 279 units away — inside its own range, but far enough
    /// out on the falloff curve that its contribution was ~0.7%, and the map's 119 lights together moved mean
    /// scene brightness by 0.00. That is the whole of "the built-in lights aren't working": they were on, in
    /// the right places, and their reach stopped just short of everything. Quake rooms are 256-1024 units, so
    /// a fixture has to carry that far to light the room it is in.
    /// </summary>
    /// (Second calibration: 158 made a light 40 fixture reach ~1000 units, and 119 such spheres overlap so
    /// heavily that Forward+'s per-cell light cap starts dropping lights arbitrarily — rooms went DARK while
    /// the GPU melted at 80-117 ms. Ranges must stay tight enough that only a handful of lights cover any
    /// point; 110 puts a typical fixture at ~700 units, one room's reach.)
    private const float RangePerSqrtIntensity = 110f;

    /// <summary>Clamp so one enormous light value cannot swallow the map.</summary>
    private const float MaxRange = 4096f;

    /// <summary>Multiplier on the derived range, for taste.</summary>
    public const string CvarRangeScale = "cl_editor_light_range";

    /// <summary>
    /// Emit real light from <c>q3map_surfaceLight</c> faces (default on).
    ///
    /// This is most of a Xonotic map's illumination. The point-light entities are the minority term —
    /// stormkeep's are mostly `light 40` accents — while the glowing ceiling strips and panels carry values
    /// like 250-3000 and are what q3map2 actually lights the rooms with, by converting each emissive surface
    /// into light emitters at compile time. An editor that honours only the `light` entities renders those
    /// panels dark and the rooms under them black, which is why the fixtures read as "not working" even after
    /// every entity-light bug was fixed. One omni per emissive face, energy from emit x area, capped.
    /// </summary>
    public const string CvarSurfaceLights = "cl_editor_surface_lights";

    /// <summary>
    /// Precompute the fixtures' light into the mesh instead of rendering them as real-time lights (default
    /// on). See <see cref="EditorLightBake"/> for why: a real-time light cannot be both far-reaching and
    /// cheap, and per-pixel light cost is what made the editor scale badly with window size.
    /// </summary>
    public const string CvarBakeLights = "cl_editor_light_bake";

    /// <summary>Brightness of the baked light. A shader uniform, so it re-lights with no rebuild.</summary>
    public const string CvarBakeScale = "cl_editor_bake_scale";

    /// <summary>
    /// Trace shadow rays during the bake (default on). This is what gives fixtures real shadows — the thing a
    /// budgeted real-time light cannot do, since only a handful can afford a shadow map. Costs bake time, not
    /// frame time.
    /// </summary>
    public const string CvarBakeShadows = "cl_editor_bake_shadows";

    /// <summary>
    /// One bounce of indirect light in the bake (default on) — q3map2's <c>-bounce</c>, cheaply. This is what
    /// keeps traced shadows readable instead of pitch black: direct light received by each region is re-emitted
    /// as virtual sources and gathered in a second pass.
    /// </summary>
    public const string CvarBakeBounce = "cl_editor_bake_bounce";

    /// <summary>Bounce count for the bake. Default 8, matching stormkeep's own compile (-bounce 8).</summary>
    public const string CvarBakeBounces = "cl_editor_bake_bounces";

    /// <summary>
    /// Response curve on the baked light (live, no rebake). 1 is the physical linear average — which reads
    /// flat; a compiled lightmap's punch lives in its response, and >1 restores it. Default 1.3.
    /// </summary>
    public const string CvarBakeGamma = "cl_editor_bake_gamma";

    /// <summary>
    /// Bloom over the editor world. Default OFF: the baked light is HDR and the fixture strips sit at the
    /// top of its range, so glow smears them across the whole frame — a uniform lift that reads as flat and
    /// that no lighting knob can counteract, because bloom is downstream of all of them.
    /// </summary>
    public const string CvarGlow = "cl_editor_glow";

    /// <summary>
    /// Distance between baked samples in Quake units — this bake's luxel size (q3map2's -samplesize, whose
    /// default is 16). Smaller resolves finer shadows and costs samples QUADRATICALLY.
    /// </summary>
    public const string CvarLuxel = "cl_editor_bake_luxel";

    /// <summary>Ambient-occlusion strength baked per sample, 0..1 — q3map2's -dirty. 0 disables it.</summary>
    public const string CvarDirt = "cl_editor_bake_dirt";

    /// <summary>Cap on generated surface lights (the largest emitters win). Keeps pathological maps bounded.</summary>
    private const int MaxSurfaceLights = 224;

    /// <summary>
    /// Compensation for the inverse-square falloff. Godot's attenuation exponent steepens the curve toward the
    /// edge of the range, so matching a linear light's mid-range brightness needs more energy at the source.
    /// </summary>
    private const float EnergyForFalloff = 2.5f;

    /// <summary>How far from the camera the sun's shadow is computed, in Quake units.</summary>
    public const string CvarSunShadowDistance = "cl_editor_sun_shadow_distance";

    /// <summary>
    /// Sun brightness, separate from the fixtures' <see cref="CvarBrightness"/>. Separate because "is the sun
    /// doing all the work" and "are the map's own lights doing anything" are different questions, and one
    /// scale that moves both cannot answer either.
    /// </summary>
    public const string CvarSunScale = "cl_editor_sun_scale";

    private readonly List<Light3D> _points = new();
    private DirectionalLight3D? _sun;
    private Light3D.BakeMode _bakeMode = Light3D.BakeMode.Dynamic;
    private float _falloff = 2f;
    private float _rangeScale = 1f;
    private float _nextShadowSort;

    /// <summary>Number of lights built (diagnostics / HUD).</summary>
    public int LightCount => _points.Count;

    /// <summary>True when a sun was recovered from the map's sky shader rather than defaulted.</summary>
    public bool HasMapSun { get; private set; }

    /// <summary>Direction TOWARD the sun (Quake space), for the bake's bounce pass.</summary>
    public System.Numerics.Vector3 SunDirToSun { get; private set; }

    /// <summary>Sun colour, for the bake's bounce pass.</summary>
    public Color SunColor { get; private set; } = Colors.White;

    /// <summary>Sun energy as built, for the bake's bounce pass.</summary>
    public float SunEnergy { get; private set; }

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
        rig._falloff = ReadFloat(cvars, CvarFalloff, 2f);
        rig._rangeScale = ReadFloat(cvars, CvarRangeScale, 1f);
        rig._bakeMode = (int)ReadFloat(cvars, CvarLightBakeMode, 2f) switch
        {
            0 => Light3D.BakeMode.Disabled,
            1 => Light3D.BakeMode.Static,
            _ => Light3D.BakeMode.Dynamic,
        };

        rig.Baking = ReadFloat(cvars, CvarBakeLights, 1f) != 0f;

        foreach (VmapEntity entity in doc.Entities)
        {
            if (!entity.ClassName.Equals("light", StringComparison.OrdinalIgnoreCase))
                continue;
            if (rig.TryBuildLight(entity, doc, brightness) is { } light)
                rig.Adopt(light);
        }

        rig.BuildSun(doc, assets, brightness, cvars);
        // After the sun, so the summary line reports whether it came from the map. Order is otherwise free.
        if (ReadFloat(cvars, CvarSurfaceLights, 1f) != 0f)
            rig.BuildSurfaceLights(doc, assets, brightness);

        return rig;
    }

    /// <summary>
    /// One <c>light</c> entity → an omni, or a SPOT when it aims at a <c>target</c>.
    ///
    /// A Q3 light with a <c>target</c> key is a spotlight: it points at the entity whose <c>targetname</c>
    /// matches (usually an <c>info_null</c>) and q3map2 bakes a cone, not a sphere. Building those as omnis
    /// lights the whole room instead of the pool the mapper aimed, which is both wrong and much brighter than
    /// intended — stormkeep has 6 of them.
    /// </summary>
    private Light3D? TryBuildLight(VmapEntity entity, VmapDocument doc, float brightness)
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

        float range = Math.Min(MaxRange, MathF.Sqrt(intensity) * RangePerSqrtIntensity * _rangeScale);
        float energy = brightness * Math.Clamp(intensity / 40f, 0.35f, 8f) * EnergyForFalloff;

        // Aimed light: find the target and build a cone along the direction to it.
        if (entity.Fields.TryGetValue("target", out string? target) && !string.IsNullOrWhiteSpace(target)
            && FindTargetOrigin(doc, target) is { } aim && aim != origin)
        {
            var spot = new SpotLight3D
            {
                Name = $"EditorSpot_{entity.Id}",
                Position = Coords.ToGodot(origin),
                LightColor = color,
                LightEnergy = energy,
                SpotRange = range,
                SpotAttenuation = _falloff,
                // Q3's spot cone is derived from the target distance and the light's radius; without a radius
                // key a moderate cone matches how these read in the bake far better than a full sphere.
                SpotAngle = 45f,
                SpotAngleAttenuation = 1f,
                ShadowEnabled = false,
                LightSpecular = 0.25f,
                LightBakeMode = _bakeMode,
            };
            // A ceiling spot aims straight down, making the default up vector colinear with the aim —
            // Godot warns per light. Any perpendicular up will do; pick one based on the aim direction.
            Vector3 aimG = Coords.ToGodot(aim);
            Vector3 dir = (aimG - spot.Position).Normalized();
            spot.LookAtFromPosition(spot.Position, aimG, Mathf.Abs(dir.Y) > 0.99f ? Vector3.Right : Vector3.Up);
            return spot;
        }

        var light = new OmniLight3D
        {
            Name = $"EditorLight_{entity.Id}",
            Position = Coords.ToGodot(origin),
            LightColor = color,
            // Energy is deliberately NOT the raw Quake intensity: that number feeds an inverse-square bake,
            // while range already encodes the falloff here. Keeping energy near 1 leaves the map's relative
            // brightness in the range, where it reads correctly, and leaves one cvar for overall taste.
            // Scaled by the map's own intensity, not flat. With inverse-square falloff (below) a flat energy
            // makes every fixture equally weak at mid-range, which reads as "the map's lights do nothing";
            // q3map2's own light values are the map author's statement of relative brightness and should be
            // what drives it. Normalised around a typical Xonotic fixture (light 40) so the common case lands
            // near 1 and a deliberately bright light is genuinely brighter.
            LightEnergy = energy,
            OmniRange = range,
            ShadowEnabled = false,       // granted by budget in Update()
            LightSpecular = 0.25f,
            LightBakeMode = _bakeMode,
            OmniAttenuation = _falloff,
        };
        _ = doc;
        return light;
    }

    /// <summary>
    /// The sun, from the sky shader's <c>q3map_sun</c> when the map defines one. Xonotic skies usually do;
    /// when none is found a soft default from above keeps exteriors readable rather than flat.
    /// </summary>
    /// <summary>
    /// Real light from <c>q3map_surfaceLight</c> faces, CLUSTERED — the live stand-in for q3map2 converting
    /// emissive surfaces into emitters at compile time (see <see cref="CvarSurfaceLights"/>).
    ///
    /// Clustered, not one omni per face, for both of the reasons the per-face version failed in playtest:
    /// <list type="bullet">
    ///   <item><b>Correctness</b> — energy is weighted by emit x area, and a single thin strip's share is
    ///     tiny, so per-face every fixture clamped to the minimum and threw almost nothing: bright panels,
    ///     dark rooms. A ceiling of strips is ONE light source to the bake, and summing the cluster's
    ///     emit x area restores the intended output.</item>
    ///   <item><b>Cost</b> — 343 overlapping realtime omnis measured 81-117 ms of GPU on a 3080 (10 fps).
    ///     Clustering collapses stormkeep's 469 emissive faces into a few dozen lights.</item>
    /// </list>
    /// </summary>
    private void BuildSurfaceLights(VmapDocument doc, AssetSystem assets, float brightness)
    {
        // ---- gather every emissive face, bucketed into coarse spatial cells ----------------------------
        const float ClusterCell = 320f;   // Quake units; about one room section per cell
        var clusters = new Dictionary<(int, int, int), (NVec3 PosW, NVec3 NormW, float W)>();
        int faces = 0;

        foreach (VmapBrush brush in doc.Brushes)
        {
            if (brush.IsToolBrush)
                continue;

            NVec3[][] windings = VmapWinding.BuildBrushWindings(brush);
            for (int i = 0; i < windings.Length && i < brush.Faces.Count; i++)
            {
                NVec3[] w = windings[i];
                if (w.Length < 3)
                    continue;

                VmapFace face = brush.Faces[i];
                float emit = VmapMapBuilder.SurfaceEmit(assets, face.Material);
                if (emit <= 0f)
                    continue;

                NVec3 centroid = NVec3.Zero;
                foreach (NVec3 v in w)
                    centroid += v;
                centroid /= w.Length;

                float area = 0f;
                for (int t = 1; t + 1 < w.Length; t++)
                    area += 0.5f * NVec3.Cross(w[t] - w[0], w[t + 1] - w[0]).Length();
                if (area < 4f)
                    continue;

                faces++;
                float weight = emit * area;
                var key = ((int)MathF.Floor(centroid.X / ClusterCell),
                           (int)MathF.Floor(centroid.Y / ClusterCell),
                           (int)MathF.Floor(centroid.Z / ClusterCell));
                (NVec3 posW, NVec3 normW, float sumW) = clusters.GetValueOrDefault(key);
                clusters[key] = (posW + centroid * weight, normW + face.Plane.Normal * weight, sumW + weight);
            }
        }

        // ---- one light per cluster, energy from the SUMMED emit x area ---------------------------------
        var candidates = new List<(float Weight, OmniLight3D Light)>();
        foreach (((int, int, int) _, (NVec3 posW, NVec3 normW, float sumW)) in clusters)
        {
            if (sumW <= 0f)
                continue;
            NVec3 centre = posW / sumW;
            NVec3 normal = normW.LengthSquared() > 1e-6f ? NVec3.Normalize(normW) : new NVec3(0f, 0f, 1f);

            // The divisor normalises "a hall's worth of ceiling strips" to a solidly bright source; the bake
            // weighs sources exactly this way (its light per surface is emit x area at -pointscale).
            float energy = brightness * Math.Clamp(sumW / 400_000f, 0.5f, 8f) * EnergyForFalloff;
            // TIGHT range cap, deliberately: the first calibration let big emitters (lava pools) reach 4096
            // units, and a map full of overlapping giant volumes trips Forward+'s per-cell light cap — lights
            // get dropped arbitrarily, so rooms went dark even though every cluster was at maximum energy.
            // A surface light should own its room, not the map.
            float range = Math.Clamp(MathF.Sqrt(sumW) * 0.35f, 224f, 900f) * _rangeScale;

            var light = new OmniLight3D
            {
                Name = $"EditorSurfLight_{candidates.Count}",
                Position = Coords.ToGodot(centre + normal * 32f),
                LightColor = Colors.White,
                LightEnergy = energy,
                OmniRange = range,
                // Softer than the entity lights' inverse-square: this omni stands in for an AREA source, and
                // area light falls off more gently than a point — the harsher curve read as a hotspot on the
                // fixture with blackness a metre away, which is the reported "doesn't affect its surroundings".
                OmniAttenuation = 1.4f,
                ShadowEnabled = false,
                LightSpecular = 0.1f,
                LightBakeMode = _bakeMode,
                // Far clusters stop costing fragment work; a mapper sees nearby rooms lit and distant ones
                // fade to their emission-only look, which is the right trade on a 343-light budget blowout.
                DistanceFadeEnabled = true,
                DistanceFadeBegin = 2500f,
                DistanceFadeLength = 1500f,
            };
            candidates.Add((energy * range, light));
        }

        // Largest emitters win the cap; the rest are dropped LOUDLY (house rule: no silent truncation).
        candidates.Sort(static (x, y) => y.Weight.CompareTo(x.Weight));
        int kept = Math.Min(MaxSurfaceLights, candidates.Count);
        int entityLights = Baking ? _bake.Count : _points.Count;
        for (int i = 0; i < kept; i++)
        {
            // The cluster stands in for a PANEL, not a point: its bake radius is what buys the penumbra.
            OmniLight3D cl = candidates[i].Light;
            Adopt(cl, Math.Clamp(cl.OmniRange * 0.12f, 24f, 96f));
        }
        for (int i = kept; i < candidates.Count; i++)
            candidates[i].Light.QueueFree();

        SurfaceLightCount = kept;
        GD.Print($"[EditorLighting] {entityLights} entity lights, {kept} surface-light clusters from {faces} emissive faces"
            + (candidates.Count > kept ? $" ({candidates.Count - kept} clusters dropped by the cap)" : "")
            + $", sun={(HasMapSun ? "map" : "default")}, mode={(Baking ? "BAKED" : "realtime")}");
    }

    /// <summary>
    /// Take ownership of a light: as a live scene node when rendering in real time, or as a bake definition
    /// (and nothing in the scene) when baking. One funnel so both paths always see the same light set —
    /// baked and real-time output only ever differ by HOW the same lights are applied.
    /// </summary>
    private void Adopt(Light3D light, float areaRadius = 0f)
    {
        if (Baking)
        {
            float range = light is OmniLight3D o ? o.OmniRange : ((SpotLight3D)light).SpotRange;
            // BakedFixtureBoost: the real-time path had to keep fixtures weak so hundreds of overlapping
            // volumes stayed affordable; a bake has no such constraint, and q3map2's look is FIXTURE-
            // dominated with the sun as one contributor among many. The boost restores that ratio, and
            // cl_editor_bake_scale brings the total back to the reference level.
            _bake.Add(new BakedLight(
                Coords.ToQuake(light.Position), light.LightColor, light.LightEnergy * BakedFixtureBoost,
                range, areaRadius));
            light.QueueFree();
            return;
        }
        _points.Add(light);
        AddChild(light);
    }

    /// <summary>True when fixture light is precomputed into the mesh rather than rendered live.</summary>
    public bool Baking { get; private set; }

    /// <summary>Fixture-energy multiplier applied only in the bake (see <see cref="Adopt"/>).</summary>
    private const float BakedFixtureBoost = 2.6f;

    /// <summary>
    /// Drop the fixture lights for a build that RESAMPLES the retained bake. The rig was constructed
    /// expecting to be baked; without this the same fixtures would also be submitted as real-time lights and
    /// the world would double-light itself on every edit.
    /// </summary>
    public void SuppressBakeLights() => _bake.Clear();

    /// <summary>The lights to bake; empty when rendering them in real time.</summary>
    public IReadOnlyList<BakedLight> BakeLights => _bake;

    private readonly List<BakedLight> _bake = new();

    /// <summary>Lights the rig owns, however they are applied (diagnostics / HUD).</summary>
    public int TotalLightCount => Baking ? _bake.Count : _points.Count;

    /// <summary>Surface lights actually built (diagnostics / HUD).</summary>
    public int SurfaceLightCount { get; private set; }

    /// <summary>Origin of the entity whose <c>targetname</c> matches, or null.</summary>
    private static NVec3? FindTargetOrigin(VmapDocument doc, string target)
    {
        foreach (VmapEntity e in doc.Entities)
            if (e.Fields.TryGetValue("targetname", out string? name)
                && string.Equals(name, target, StringComparison.OrdinalIgnoreCase)
                && e.Fields.TryGetValue("origin", out string? o)
                && TryVector(o, out NVec3 v))
                return v;
        return null;
    }

    private void BuildSun(VmapDocument doc, AssetSystem assets, float brightness, CvarService? cvars)
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
            LightEnergy = ReadFloat(cvars, CvarSunScale, 1f)
                * (sun is not null ? Math.Clamp(sun.Intensity / 150f, 0.15f, 2f) : 0.6f),
            ShadowEnabled = true,
            LightSpecular = 0.2f,
            LightBakeMode = _bakeMode,
            // Godot's directional shadow only covers DirectionalShadowMaxDistance around the camera, and its
            // default is 100 units. A Quake map is thousands of units across, so at the default the sun is
            // unshadowed almost everywhere — it shines straight through walls and floods a sealed interior
            // with an even light that no geometry blocks. That is what makes a lit indoor map look like it is
            // lit only by the sun, with nothing casting a shadow.
            DirectionalShadowMaxDistance = ReadFloat(cvars, CvarSunShadowDistance, 6000f),
            // Two splits, not four: the shadow cost scales with the number of cascades rendered, and a mapper
            // needs "is this wall blocking the sun" answered, not film-quality cascade transitions.
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits,
        };
        Vector3 sunDir = Coords.ToGodot(fromSun).Normalized();
        light.LookAtFromPosition(Vector3.Zero, sunDir, Mathf.Abs(sunDir.Y) > 0.99f ? Vector3.Right : Vector3.Up);

        SunDirToSun = -fromSun;   // fromSun is the direction the light TRAVELS; the bounce wants the reverse
        SunColor = light.LightColor;
        SunEnergy = light.LightEnergy;

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
            foreach (Light3D light in _points)
                if (GodotObject.IsInstanceValid(light))
                    light.ShadowEnabled = false;
            return;
        }

        _sorted.Clear();
        for (int i = 0; i < _points.Count; i++)
        {
            Light3D light = _points[i];
            if (!GodotObject.IsInstanceValid(light))
                continue;
            // Distance to the light's REACH, not its centre: a big light whose centre is far can still be the
            // one lighting the room the camera is standing in.
            float reach = light is OmniLight3D o ? o.OmniRange : ((SpotLight3D)light).SpotRange;
            float d = cameraPosition.DistanceTo(light.Position) - reach;
            _sorted.Add((d, i));
        }
        _sorted.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));

        for (int rank = 0; rank < _sorted.Count; rank++)
        {
            Light3D light = _points[_sorted[rank].Index];
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

        bool gi = ReadFloat(cvars, CvarGlobalIllumination, 1f) != 0f;

        // Applied repeatedly (the settings are cvars a mapper changes mid-session), so skip the write when
        // nothing moved — re-assigning SdfgiEnabled would otherwise restart the cascades every frame.
        int signature = HashCode.Combine(gi, ReadFloat(cvars, CvarAmbient, 0.04f), ReadFloat(cvars, CvarGlow, 0f),
            ReadFloat(cvars, CvarGiCellSize, 8f), ReadFloat(cvars, CvarGiCascades, 4f),
            ReadFloat(cvars, CvarGiEnergy, 1.6f),
            HashCode.Combine(ReadFloat(cvars, CvarSsao, 1f), ReadFloat(cvars, CvarSsaoIntensity, 4f),
                ReadFloat(cvars, CvarSsaoRadius, 48f), ReadFloat(cvars, CvarSkyLight, 0f)));
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
            _savedReflection = env.ReflectedLightSource;
            _savedSsao = env.SsaoEnabled;
        }
        _appliedEnv = env;
        _appliedSignature = signature;

        // Bloom is downstream of every lighting knob, so while it is on, tuning them cannot fix a washed
        // frame. Saved and restored with the rest of the borrowed environment.
        if (!_savedGlowValid)
        {
            _savedGlow = env.GlowEnabled;
            _savedGlowValid = true;
        }
        env.GlowEnabled = ReadFloat(cvars, CvarGlow, 0f) != 0f;

        // With GI supplying the fill, the flat term only has to stop pure black in a cascade's blind spot.
        float ambient = ReadFloat(cvars, CvarAmbient, 0.04f) * (gi ? 0.25f : 1f);
        env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        env.AmbientLightColor = new Color(0.55f, 0.58f, 0.66f);
        env.AmbientLightEnergy = ambient;

        // The sky is not allowed to light a sealed interior. This is the single largest term in why the
        // editor world never looked like the baked one: it lifted every surface uniformly, which is exactly
        // the definition of flat.
        bool skyLight = ReadFloat(cvars, CvarSkyLight, 0f) != 0f;
        env.ReflectedLightSource = skyLight
            ? Godot.Environment.ReflectionSource.Bg
            : Godot.Environment.ReflectionSource.Disabled;

        // AO — the live stand-in for q3map2's baked -dirty pass.
        bool ssao = ReadFloat(cvars, CvarSsao, 1f) != 0f;
        env.SsaoEnabled = ssao;
        if (ssao)
        {
            env.SsaoRadius = ReadFloat(cvars, CvarSsaoRadius, 48f);
            env.SsaoIntensity = ReadFloat(cvars, CvarSsaoIntensity, 4f);
            env.SsaoPower = 2f;
            env.SsaoDetail = 0.5f;
            env.SsaoHorizon = 0.06f;
            env.SsaoSharpness = 0.98f;
            // AO darkens ambient/indirect only by default; letting it bite into direct light too is what
            // makes a corner read as a corner when the only light in the room is a nearby fixture.
            env.SsaoLightAffect = 0.35f;
        }

        env.SdfgiEnabled = gi;
        if (!gi)
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
    /// Silence the scene's generic "Sun" while the editor lights the world itself.
    ///
    /// The host adds a fixed <c>DirectionalLight3D</c> at a hardcoded angle (NetGame's <c>Sun</c>) so that
    /// PLAYERS and items are lit — the shipped world never needed it, being drawn unshaded from a baked
    /// lightmap. The moment the editor's world became lit, that light started washing every surface in the
    /// map from one direction at full strength, independent of anything the map itself defines. It is the
    /// single largest reason the lit editor still looked flat: 119 fixtures and a recovered sun were all
    /// competing with a uniform floodlight nobody asked for.
    ///
    /// Suppressed rather than removed, and restored on the way out, because it is the host's node and the
    /// match still needs it.
    /// </summary>
    public static void SuppressSceneSun(Node sceneRoot, bool suppress)
    {
        if (sceneRoot is null)
            return;
        if (sceneRoot.FindChild("Sun", true, false) is not DirectionalLight3D sun || !GodotObject.IsInstanceValid(sun))
            return;

        if (suppress)
        {
            if (!_sunSuppressed)
            {
                _savedSceneSunEnergy = sun.LightEnergy;
                _sunSuppressed = true;
            }
            sun.LightEnergy = 0f;
            sun.ShadowEnabled = false;
        }
        else if (_sunSuppressed)
        {
            sun.LightEnergy = _savedSceneSunEnergy;
            sun.ShadowEnabled = true;
            _sunSuppressed = false;
        }
    }

    private static bool _savedGlow, _savedGlowValid;

    private static bool _sunSuppressed;
    private static float _savedSceneSunEnergy = 1f;

    /// <summary>Put back the environment the match had before the editor imposed its own preset.</summary>
    public static void RestoreEnvironment()
    {
        if (_savedGlowValid && _appliedEnv is { } ge)
        {
            ge.GlowEnabled = _savedGlow;
            _savedGlowValid = false;
        }

        if (_appliedEnv is null || !GodotObject.IsInstanceValid(_appliedEnv))
        {
            _appliedEnv = null;
            return;
        }

        _appliedEnv.AmbientLightSource = _savedAmbientSource;
        _appliedEnv.AmbientLightColor = _savedAmbientColor;
        _appliedEnv.AmbientLightEnergy = _savedAmbientEnergy;
        _appliedEnv.SdfgiEnabled = _savedSdfgi;
        _appliedEnv.ReflectedLightSource = _savedReflection;
        _appliedEnv.SsaoEnabled = _savedSsao;
        _appliedEnv = null;
        _appliedSignature = 0;
    }

    private static Godot.Environment? _appliedEnv;
    private static int _appliedSignature;
    private static Godot.Environment.AmbientSource _savedAmbientSource;
    private static Color _savedAmbientColor;
    private static float _savedAmbientEnergy;
    private static bool _savedSdfgi;
    private static Godot.Environment.ReflectionSource _savedReflection;
    private static bool _savedSsao;

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
