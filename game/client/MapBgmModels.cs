using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;

namespace VortexArena.Game.Client;

/// <summary>Presentation-only poses for the local map's scripted client models.</summary>
public sealed partial class MapBgmModels : Node3D
{
    private sealed class Model(Entity source, EntityNode node, Entity pose, MapBgmScript.Playback playback, ModelFade? fade)
    {
        public Entity Source { get; } = source;
        public EntityNode Node { get; } = node;
        public Entity Pose { get; } = pose;
        public MapBgmScript.Playback Playback { get; } = playback;
        public ModelFade? Fade { get; } = fade;
    }

    private readonly List<Model> _models = new();
    private readonly Dictionary<int, Entity> _aliases = new();
    private readonly Dictionary<Shader, Shader?> _fadeShaders = new();

    internal MusicPlayer? Music { get; set; }
    internal object? SimGate { get; set; }

    internal void AddModel(Entity source, Node3D mesh, MapBgmScript.Playback playback, int aliasNetId)
    {
        var node = new EntityNode { Name = $"BgmModel_{source.Index}" };
        var pose = new Entity { Origin = source.Origin, Angles = source.Angles, ScaleFactor = source.ScaleFactor };
        AddChild(node);
        node.SetProcess(false);
        node.Bind(pose);
        node.AddChild(mesh);

        var fade = source.Lip != 0f ? new ModelFade(mesh, _fadeShaders) : null;
        _models.Add(new Model(source, node, pose, playback, fade));
        if (aliasNetId != 0)
            _aliases.Add(aliasNetId, source);
    }

    internal bool RendersAlias(int netId) => _aliases.TryGetValue(netId, out Entity? source) && !source.IsFreed;

    public override void _Process(double delta)
    {
        object? gate = SimGate;
        bool locked = false;
        try
        {
            if (gate is not null)
            {
                Monitor.TryEnter(gate, 0, ref locked);
                if (!locked) return;
            }

            float time = Music?.PlaybackPosition ?? -1f;
            for (int i = _models.Count - 1; i >= 0; --i)
            {
                Model model = _models[i];
                Entity source = model.Source;
                if (source.IsFreed)
                {
                    model.Node.QueueFree();
                    _models.RemoveAt(i);
                    continue;
                }

                MapBgmScript.Sample sample = model.Playback.Advance(time, (float)delta);
                float alpha = Math.Clamp(sample.ModelAlpha(source.Lip), 0f, 1f);
                model.Pose.Origin = source.Origin;
                model.Pose.Angles = source.Angles;
                if (sample.Amplitude >= 0f)
                {
                    if (source.BgmScript!.Angular)
                        model.Pose.Angles += source.MoveDir * sample.Amplitude;
                    else
                        model.Pose.Origin += source.MoveDir * sample.Amplitude;
                }
                model.Node.SetGameplayVisible(!source.Inactive && alpha >= 0.003f);
                model.Node.DriveSync();
                model.Fade?.SetAlpha(alpha);
            }
        }
        finally
        {
            if (locked) Monitor.Exit(gate!);
        }
    }

    // GeometryInstance3D.Transparency is Forward+-only. Use per-model material variants so authored
    // opacity also works with Compatibility and does not modify materials shared with the map.
    private sealed class ModelFade
    {
        private static readonly StringName AlphaUniform = "map_bgm_alpha";
        private static readonly Regex ShaderTokens = new(
            @"//[^\r\n]*|/\*[\s\S]*?\*/|\bvoid\s+fragment\s*\(\s*\)\s*\{|\bALPHA\b|[{}]",
            RegexOptions.CultureInvariant);
        private readonly List<(MeshInstance3D Mesh, int Surface, Material? Original, Material Fade)> _surfaces = new();
        private float _alpha = 1f;

        public ModelFade(Node3D node, Dictionary<Shader, Shader?> shaders) => Gather(node, shaders);

        private void Gather(Node node, Dictionary<Shader, Shader?> shaders)
        {
            if (node is MeshInstance3D mesh)
            {
                if (mesh.MaterialOverride is { } material)
                    Add(mesh, -1, material, material, shaders);
                else if (mesh.Mesh is { } geometry)
                    for (int i = 0; i < geometry.GetSurfaceCount(); i++)
                        Add(mesh, i, mesh.GetSurfaceOverrideMaterial(i), mesh.GetActiveMaterial(i), shaders);
            }
            foreach (Node child in node.GetChildren())
                Gather(child, shaders);
        }

        private void Add(MeshInstance3D mesh, int surface, Material? original, Material? material,
            Dictionary<Shader, Shader?> shaders)
        {
            if (material is ShaderMaterial { Shader: { } shader } source)
            {
                if (!shaders.TryGetValue(shader, out Shader? variant))
                    shaders[shader] = variant = CreateFadeShader(shader);
                if (variant is null) return;

                var copy = (ShaderMaterial)source.Duplicate();
                copy.Shader = variant;
                foreach (var uniform in shader.GetShaderUniformList())
                {
                    StringName name = uniform.AsGodotDictionary()["name"].AsStringName();
                    Variant value = source.GetShaderParameter(name);
                    if (value.VariantType != Variant.Type.Nil)
                        copy.SetShaderParameter(name, value);
                }
                _surfaces.Add((mesh, surface, original, copy));
            }
            else if (material is BaseMaterial3D standard)
            {
                var copy = (BaseMaterial3D)standard.Duplicate();
                copy.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                _surfaces.Add((mesh, surface, original, copy));
            }
        }

        public void SetAlpha(float alpha)
        {
            if (alpha == _alpha) return;
            bool changeMaterial = (_alpha < 1f) != (alpha < 1f);
            _alpha = alpha;
            foreach (var (mesh, surface, original, fade) in _surfaces)
            {
                if (alpha < 1f)
                {
                    if (fade is ShaderMaterial shader) shader.SetShaderParameter(AlphaUniform, alpha);
                    else if (fade is BaseMaterial3D standard)
                    {
                        Color color = ((BaseMaterial3D)(original ?? mesh.Mesh.SurfaceGetMaterial(surface))).AlbedoColor;
                        color.A *= alpha;
                        standard.AlbedoColor = color;
                    }
                }
                if (changeMaterial)
                {
                    Material? material = alpha < 1f ? fade : original;
                    if (surface < 0) mesh.MaterialOverride = material;
                    else mesh.SetSurfaceOverrideMaterial(surface, material);
                }
            }
        }

        private static Shader? CreateFadeShader(Shader source)
        {
            string code = source.Code;
            int start = -1;
            int depth = 0;
            bool writesAlpha = false;
            foreach (Match token in ShaderTokens.Matches(code))
            {
                if (token.Value[0] == '/') continue;
                if (start < 0)
                {
                    if (!token.Value.StartsWith("void", StringComparison.Ordinal)) continue;
                    start = token.Index;
                    depth = 1;
                }
                else if (token.Value == "{") depth++;
                else if (token.Value == "ALPHA") writesAlpha = true;
                else if (token.Value == "}" && --depth == 0)
                {
                    return new Shader
                    {
                        Code = code.Insert(token.Index, writesAlpha ? "\nALPHA *= map_bgm_alpha;\n" : "\nALPHA = map_bgm_alpha;\n")
                            .Insert(start, "uniform float map_bgm_alpha = 1.0;\n"),
                    };
                }
            }
            return null;
        }
    }
}
