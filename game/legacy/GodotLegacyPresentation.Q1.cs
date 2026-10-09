// Quake 1 format maps (BSP 29, "BSP2", "2PSB", Half-Life 30) in a legacy session: the dispatch from LoadWorld,
// what the session keeps of such a level, and every place where the Quake 3 path's assumptions (a light grid,
// lightmap pages, faces with their own texture coordinates) have a Quake 1 counterpart. The reader is
// VortexArena.Formats.Bsp.Q1BspReader, the collision VortexArena.Engine.Collision.Q1HullCollision (through
// BspLegacyWorld.UseQ1Map); the drawing is Q1Level (game/Q1MapLoader.cs) with Q1SurfaceShader.
//
// What DarkPlaces does differently for such a map, and where it is here:
//   - light styles animate the lightmaps (gl_rsurf.c R_BuildLightMap): UpdateQ1Frame, Q1LightStyleTexture.
//   - r_refdef.scene.lightmapintensity is NOT multiplied by style 0 (gl_rmain.c R_UpdateVariables does that
//     for mod_brushq3 only): ApplyLightStyle leaves the world's lightmap scale at one.
//   - there is no light grid: a model is lit by the lightmap under it, ambient only
//     (model_brush.c Mod_BSP_LightPoint through r_shadow.c R_CompleteLightPoint): ApplyQ1ModelLight.
//   - a Quake 1 format file can be an ENTITY's model (Quake's ammo boxes, maps/b_*.bsp). Its surfaces are
//     drawn with the file's own lightmaps, not with the light under the entity (gl_rmain.c
//     R_UpdateCurrentTexture: the model has lightmap texture coordinates, so the "lightmap" branch applies
//     unless the entity has EF_DYNAMICMODELLIGHT): CreateQ1ModelNode.
using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Formats.Bsp;
using VortexArena.Game.Client;
using VortexArena.Game.Loaders;
using VortexArena.Legacy.Csqc;
using VortexArena.QuakeC;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Legacy;

public sealed partial class GodotLegacyPresentation
{
    // The level's map when it is a Quake 1 format one (then _levelBsp is null), and what is drawn of it.
    private Q1BspData? _levelQ1;
    private Q1Level? _q1;
    private string _q1WorldPath = "";
    private Q1Palette? _q1Palette;
    // Quake 1 format files used as entity models this level: null for a file that is not one or would not build.
    private readonly Dictionary<string, Q1Level?> _q1Models = new(StringComparer.Ordinal);
    private const int MaxQ1Models = 256;
    private const int MaxQ1ModelBytes = 8 << 20;
    private int _q1StyleStamp;
    private double _q1SettingsAt = double.NegativeInfinity;
    private Func<int, float>? _q1StyleValue;

    // The world is cut into cells of this many units for visibility and frustum culling (0: one mesh).
    // VORTEX_Q1_CELL overrides it, for measuring.
    private static readonly float s_q1CellSize =
        float.TryParse(System.Environment.GetEnvironmentVariable("VORTEX_Q1_CELL"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float cell) && cell >= 0 ? cell : 1024f;

    // True when the file starts like a Quake 1 format map. (Four bytes would do; the file system reads whole
    // files, and LoadQ1World reads it again - a map is read twice on the Quake 3 path as well.)
    private bool IsQ1Map(string map)
    {
        try { return Q1BspReader.IsQ1Format(_vfs.ReadBytes(map)); }
        catch (Exception e) when (e is not OutOfMemoryException) { return false; }
    }

    // LoadWorld for a Quake 1 format map. An exception goes to LoadWorld's handler, which records it as the
    // reason the level is not entered.
    private bool LoadQ1World(string map, string levelName)
    {
        _levelQ1 = null;
        _q1 = null;
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        Q1BspData q1 = BspLegacyWorld.ReadQ1(_vfs, map, _vfs.ReadBytes(map));
        double readSeconds = System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalSeconds;
        Map.UseQ1Map(map, q1);
        if (Map.Collision is { } collision) _effects.SetCollisionWorld(collision);
        double collisionSeconds = System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalSeconds - readSeconds;

        // The parts a Quake 3 map shares with this one (the entity text among them): worldspawn's sky and fog
        // keys are read by the same code.
        BspData shared = Q1BspTreeView.Create(q1);
        Sky? box = SkyboxLoader.TryBuild(shared, _assets.Assets);
        MapLoader.Worldspawn worldspawn = MapLoader.BuildWorldspawn(shared);
        (Color, float)? skyFog = null;
        if (worldspawn.HasFog && worldspawn.FogDensity > 0 && worldspawn.FogAlpha > 0)
        {
            float range = Math.Clamp(2048f / worldspawn.FogDensity + worldspawn.FogStart, worldspawn.FogStart, worldspawn.FogEnd);
            float amount = (1f - MathF.Exp(-worldspawn.FogDensity * 0.004f * MathF.Max(1f, range - worldspawn.FogStart))) * Math.Clamp(worldspawn.FogAlpha, 0f, 1f);
            skyFog = (worldspawn.FogColor, amount);
        }

        _q1Palette ??= Q1MapLoader.LoadPalette(_vfs);
        long buildBegan = System.Diagnostics.Stopwatch.GetTimestamp();
        Q1Level level = new(q1, map, _assets.Assets, _q1Palette, box, skyFog);
        level.SetWater(Q1Cvar("r_wateralpha", 1f), Q1Cvar("r_waterscroll", 1f));
        _mapRoot = level.BuildWorld(s_q1CellSize);
        _sceneRoot.AddChild(_mapRoot);
        double buildSeconds = System.Diagnostics.Stopwatch.GetElapsedTime(buildBegan).TotalSeconds;

        _levelQ1 = q1;
        _q1 = level;
        _q1WorldPath = map;
        _levelName = levelName;
        _q1StyleStamp++;
        _q1SettingsAt = double.NegativeInfinity;
        // No decals on a Quake 1 map yet: the splat system reads Quake 3 faces. (This empties what a level before left.)
        _effects.SetDecalGeometry(shared);
        // No light grid: models are lit from the lightmaps (ApplyQ1ModelLight).
        ModelLighting.ApplyMap(null);
        ApplyQ1Environment(shared, box);

        long submodelsBegan = LegacyPerfLog.Stamp();
        (int built, double submodelSeconds) = PrebuildQ1Submodels();
        LegacyPerfLog.Event($"precache: {built} map submodels built ahead", submodelsBegan);
        _note(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"map \"{map}\" loaded (Quake 1 format): {level.Describe()}; read {readSeconds:0.00} s, collision {collisionSeconds:0.00} s, world build {buildSeconds:0.00} s, {built} submodels in {submodelSeconds:0.00} s"));
        return true;
    }

    private float Q1Cvar(string name, float otherwise) => _cvars.Has(name) ? _cvars.GetFloat(name) : otherwise;

    // The level's environment: the sky box if worldspawn names one that exists, otherwise nothing behind the
    // world (the map's own sky is drawn on its sky surfaces, which also hide what is behind them:
    // r_q1bsp_skymasking); the fog; no ambient light and no sun (every model has its light from the lightmaps).
    private void ApplyQ1Environment(BspData shared, Sky? box)
    {
        if (_environment is null)
        {
            _environment = new WorldEnvironment { Name = "WorldEnvironment" };
            _sceneRoot.AddChild(_environment);
            _sun = new DirectionalLight3D { Name = "Sun", RotationDegrees = new Vector3(-50f, -30f, 0f), ShadowEnabled = false };
            _sceneRoot.AddChild(_sun);
        }
        Godot.Environment env = new()
        {
            BackgroundMode = box is null ? Godot.Environment.BGMode.Color : Godot.Environment.BGMode.Sky,
            BackgroundColor = Colors.Black,
            AmbientLightSource = Godot.Environment.AmbientSource.Disabled,
            AmbientLightEnergy = 0f,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        if (box is not null) env.Sky = box;
        MapLoader.ApplyFog(env, shared);
        ApplyDisplayBuffer(env, levelHasLightGrid: true);
        if (_sun is not null) _sun.Visible = false;
        _environment.Environment = env;
    }

    private void ReleaseQ1Level()
    {
        _levelQ1 = null;
        _q1 = null;
        _q1WorldPath = "";
        _q1Models.Clear();
    }

    // ---- per frame ---------------------------------------------------------------------------------------

    // BeginFrame: the light styles at cl.time (cl_main.c CL_RelinkLightFlashes), the animated textures, and -
    // four times a second - the cvars a server or the player may have changed.
    private void UpdateQ1Frame(double time)
    {
        if (_q1 is null && _q1Models.Count == 0) return;
        using var _scope = FrameProfiler.Scope("legacy.q1");
        if (Q1LightStyleTexture.Update(_lightStyles, time, Q1Cvar("r_lerplightstyles", 0f) != 0)) _q1StyleStamp++;
        _q1?.Update(time);
        foreach (Q1Level? model in _q1Models.Values) model?.Update(time);
        if (_q1 is { } level && Math.Abs(time - _q1SettingsAt) >= 0.25)
        {
            _q1SettingsAt = time;
            level.SetWater(Q1Cvar("r_wateralpha", 1f), Q1Cvar("r_waterscroll", 1f));
        }
    }

    // ---- "*N" ----------------------------------------------------------------------------------------------

    private (int Built, double Seconds) PrebuildQ1Submodels()
    {
        if (Headless || s_noPrecache || _q1 is not { } level) return (0, 0);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        int built = 0;
        for (int index = 1; index < level.Bsp.Models.Length; index++)
        {
            if (_prebuiltSubmodels.ContainsKey(index)) continue;
            if (level.BuildModel(index, _time) is not { } node) continue;
            node.Name = "Submodel" + index;
            _prebuiltSubmodels[index] = node;
            built++;
            if ((built & 31) == 0) ModelData.Working?.Invoke();
        }
        return (built, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);
    }

    // BuildSubmodel on a Quake 1 level.
    private void BuildQ1Submodel(Proxy proxy, string model)
    {
        if (_q1 is not { } level
            || !int.TryParse(model.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index)
            || index < 1 || index >= level.Bsp.Models.Length)
        {
            proxy.Failed = true;
            return;
        }
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_prebuiltSubmodels.Remove(index, out Node3D? node) || !GodotObject.IsInstanceValid(node)) node = level.BuildModel(index, _time);
        if (node is null)
        {
            proxy.Failed = true;
            return;
        }
        node.Visible = false;
        _sceneRoot.AddChild(node);
        _proxyNodes++;
        proxy.Node = node;
        proxy.IsSubmodel = true;
        proxy.Q1 = level;
        CollectGeometry(node, proxy.Geometry);
        foreach (GeometryInstance3D geometry in proxy.Geometry) geometry.Visible = true;
        SubmodelsBuilt++;
        SubmodelBuildSeconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
    }

    // ---- Quake 1 format files as entity models ---------------------------------------------------------------

    private static bool IsQ1ModelName(string model) => model.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase);

    // The level for a model file, built on first use; null if the file is not a Quake 1 format map (or is the
    // level itself, which the precache list also names, or is larger than an item box has any reason to be).
    private Q1Level? Q1ModelLevel(string model)
    {
        if (_q1Models.TryGetValue(model, out Q1Level? known)) return known;
        Q1Level? level = null;
        if (_q1Models.Count < MaxQ1Models && !string.Equals(model, _state?.WorldModel, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(model, _q1WorldPath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                byte[] file = _vfs.ReadBytes(model);
                if (file.Length <= MaxQ1ModelBytes && Q1BspReader.IsQ1Format(file))
                {
                    _q1Palette ??= Q1MapLoader.LoadPalette(_vfs);
                    level = new Q1Level(BspLegacyWorld.ReadQ1(_vfs, model, file), model, _assets.Assets, _q1Palette);
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _note($"model \"{model}\" (a Quake 1 format map) could not be built: {e.GetType().Name}: {e.Message}");
            }
        }
        _q1Models[model] = level;
        return level;
    }

    // CreateModelNode for a ".bsp" name: the node, or null with handled false when the file is not a Quake 1
    // format map and the ordinary model path should have it.
    private Node3D? CreateQ1ModelNode(string model, out bool handled)
    {
        handled = false;
        if (!IsQ1ModelName(model)) return null;
        if (Q1ModelLevel(model) is not { } level) return null;
        handled = true;
        return level.BuildModel(0, _time);
    }

    // An entity's frame on a brush model: not 0 selects the alternate chain of its "+" textures.
    private void ApplyQ1Frame(Proxy proxy, int frame)
    {
        if (proxy.Q1 is not { } level || proxy.Node is not { } node || proxy.LastFrame == frame) return;
        proxy.LastFrame = frame;
        level.SetAlternate(node, frame != 0, _time);
    }

    // ---- model light -------------------------------------------------------------------------------------

    private static readonly StringName s_gridAmbient = "grid_ambient";

    // The light of a model on a Quake 1 level: Mod_BSP_LightPoint at the entity's origin (the lightmap of the
    // surface below it, every style layer at its current value), as ambient light with no direction - what
    // R_CompleteLightPoint makes of a light point that reports no directed colour. The skin shader takes it
    // per instance (grid_lit 2: lit by the instance's own values). Sampled again when the entity has moved,
    // or when a light style changed.
    private void ApplyQ1ModelLight(Proxy proxy, NVec3 origin)
    {
        if (_levelQ1 is null || proxy.IsSubmodel || proxy.Q1 is not null || Map.Hulls is not { } hulls) return;
        if ((proxy.Bits & BitFullBright) != 0)
        {
            proxy.Q1LightStamp = -1;
            return;
        }
        if (proxy.Q1LightStamp == _q1StyleStamp && NVec3.DistanceSquared(origin, proxy.Q1LightAt) < 4f) return;
        proxy.Q1LightStamp = _q1StyleStamp;
        proxy.Q1LightAt = origin;
        _q1StyleValue ??= style => VortexArena.Formats.Materials.DpColour.LightStyleValue((uint)style < (uint)_lightStyles.Length ? _lightStyles[style] : null, _time);
        // A point over nothing answers black, as in DarkPlaces.
        hulls.LightPoint(origin, _q1StyleValue, out NVec3 ambient);
        Vector3 light = new(ambient.X, ambient.Y, ambient.Z);
        if (proxy.Q1LightSet && light.IsEqualApprox(proxy.Q1Light)) return;
        proxy.Q1LightSet = true;
        proxy.Q1Light = light;
        foreach (GeometryInstance3D geometry in proxy.Geometry)
        {
            if (geometry is not MeshInstance3D mesh || !GodotObject.IsInstanceValid(mesh)) continue;
            mesh.SetInstanceShaderParameter(PlayerSkinShader.GridLitUniform, 2f);
            mesh.SetInstanceShaderParameter(s_gridAmbient, light);
        }
    }

    // #92 getlight on a Quake 1 level: R_CompleteLightPoint's answer for a map whose light point is ambient only.
    private bool Q1GetLight(QcVector point, out QcVector ambient)
    {
        ambient = default;
        if (_levelQ1 is null || Map.Hulls is not { } hulls || !Finite(point)) return false;
        _q1StyleValue ??= style => VortexArena.Formats.Materials.DpColour.LightStyleValue((uint)style < (uint)_lightStyles.Length ? _lightStyles[style] : null, _time);
        hulls.LightPoint(N(point), _q1StyleValue, out NVec3 light);
        ambient = new QcVector(light.X, light.Y, light.Z);
        return true;
    }
}
