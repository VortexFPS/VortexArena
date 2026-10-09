using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using VortexArena.Common.Config;
using VortexArena.Game.Loaders;

namespace VortexArena.Game.Client;

/// <summary>
/// Console commands for comparing the native picture with another renderer frame for frame (the native
/// counterpart of a legacy review script's <c>colourdbg</c>, <c>shot</c> and <c>demopause</c>). Developer aids:
/// none of them changes a setting that is saved.
///
/// <list type="bullet">
///   <item><c>r_observe x y z [yaw pitch]</c> - pin the observer camera at a point in Quake coordinates
///   (<c>--observe</c> at run time; only meaningful in a session started with <c>--observe</c>, which is what
///   keeps the client an observer).</item>
///   <item><c>r_shot &lt;absolute path.png&gt;</c> - save the next drawn frame of the root viewport.</item>
///   <item><c>r_hud 0|1</c> - hide or show every 2D layer of the match (the console stays).</item>
///   <item><c>r_effect &lt;name&gt; x y z</c> - spawn an effectinfo effect at a point.</item>
///   <item><c>r_dumpmaterials</c> - the kinds of material on visible geometry, with counts.</item>
/// </list>
/// </summary>
public static class RenderDevCommands
{
    private static readonly List<(CanvasLayer Layer, bool Was)> s_hidden = new();

    public static void Register(ConfigInterpreter interp, Action<string> print)
    {
        interp.RegisterCommand("r_observe", args =>
        {
            if (args.Count < 4) { print("usage: r_observe x y z [yaw pitch]"); return; }
            var joined = new StringBuilder();
            for (int i = 1; i < args.Count; i++) joined.Append(i > 1 ? " " : "").Append(args[i]);
            VortexArena.Game.Net.ObserverCamera.Configure(joined.ToString(), null);
        }, "pin the observer camera: x y z [yaw pitch] in Quake coordinates (needs a session started with --observe)");

        interp.RegisterCommand("r_shot", args =>
        {
            if (args.Count < 2) { print("usage: r_shot <absolute path.png>"); return; }
            string path = args[1];
            void Save()
            {
                RenderingServer.FramePostDraw -= Save;
                if ((Godot.Engine.GetMainLoop() as SceneTree)?.Root is not { } root) return;
                Image? image = root.GetTexture()?.GetImage();
                if (image is null) { print("r_shot: no image (headless?)"); return; }
                Error error = image.SavePng(path);
                GD.Print(error == Error.Ok ? $"[r_shot] wrote {image.GetWidth()}x{image.GetHeight()} -> {path}" : $"[r_shot] {error} writing {path}");
            }
            RenderingServer.FramePostDraw += Save;
        }, "save the next drawn frame of the window to a PNG (absolute path)");

        interp.RegisterCommand("r_hud", args =>
        {
            bool on = args.Count < 2 || args[1] != "0";
            if ((Godot.Engine.GetMainLoop() as SceneTree)?.Root is not { } root) return;
            if (on)
            {
                foreach ((CanvasLayer layer, bool was) in s_hidden)
                    if (GodotObject.IsInstanceValid(layer)) layer.Visible = was;
                s_hidden.Clear();
                return;
            }
            Walk(root);
            void Walk(Node node)
            {
                if (node is CanvasLayer layer && node is not Console.ConsoleOverlay && !IsUnder(node, typeof(Console.ConsoleOverlay)))
                {
                    if (layer.Visible) { s_hidden.Add((layer, true)); layer.Visible = false; }
                }
                foreach (Node child in node.GetChildren()) Walk(child);
            }
        }, "0 hides every 2D layer of the match for a capture, 1 puts them back");

        interp.RegisterCommand("r_effect", args =>
        {
            if (args.Count < 5) { print("usage: r_effect <effectinfo name> x y z"); return; }
            if ((Godot.Engine.GetMainLoop() as SceneTree)?.Root.FindChild("Effects", true, false) is not EffectSystem effects) { print("r_effect: no effect system (no match running)"); return; }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (!float.TryParse(args[2], System.Globalization.NumberStyles.Float, inv, out float x) || !float.TryParse(args[3], System.Globalization.NumberStyles.Float, inv, out float y)
                || !float.TryParse(args[4], System.Globalization.NumberStyles.Float, inv, out float z)) { print("r_effect: bad position"); return; }
            effects.Spawn(args[1], new System.Numerics.Vector3(x, y, z));
        }, "spawn an effectinfo effect at a point in Quake coordinates (Xonotic's 'cmd pointparticles')");

        interp.RegisterCommand("r_dumpmaterials", _ => DumpMaterials(print), "list the kinds of material on visible geometry");
    }

    private static bool IsUnder(Node node, Type type)
    {
        for (Node? at = node.GetParent(); at is not null; at = at.GetParent())
            if (type.IsInstanceOfType(at)) return true;
        return false;
    }

    private static void DumpMaterials(Action<string> print)
    {
        if ((Godot.Engine.GetMainLoop() as SceneTree)?.Root is not { } root) return;
        Dictionary<string, (int Count, string Example)> rows = new();
        Walk(root);
        List<KeyValuePair<string, (int Count, string Example)>> sorted = new(rows);
        sorted.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));
        Out($"r_dumpmaterials: {sorted.Count} kinds of material on visible geometry");
        foreach ((string kind, (int count, string example)) in sorted) Out($"  x{count,4} {kind}   e.g. {example}");

        void Out(string line) { print(line); GD.Print(line); }

        void Walk(Node node)
        {
            if (node is Node3D { Visible: false }) return;
            if (node is MeshInstance3D { Mesh: { } mesh } instance)
            {
                for (int s = 0, n = mesh.GetSurfaceCount(); s < n; s++)
                    Add(instance.MaterialOverride ?? instance.GetSurfaceOverrideMaterial(s) ?? mesh.SurfaceGetMaterial(s), instance);
            }
            else if (node is MultiMeshInstance3D { Multimesh.Mesh: { } multi } many)
            {
                for (int s = 0, n = multi.GetSurfaceCount(); s < n; s++) Add(many.MaterialOverride ?? multi.SurfaceGetMaterial(s), many);
            }
            else if (node is GeometryInstance3D geometry) Add(geometry.MaterialOverride, geometry);
            foreach (Node child in node.GetChildren()) Walk(child);
        }

        void Add(Material? material, Node3D owner)
        {
            string kind = Describe(material) + " on " + owner.GetType().Name;
            rows[kind] = rows.TryGetValue(kind, out (int Count, string Example) row) ? (row.Count + 1, row.Example) : (1, PathTail(owner));
        }

        static string PathTail(Node node)
        {
            StringBuilder path = new();
            Node? at = node;
            for (int i = 0; i < 4 && at is not null; i++, at = at.GetParent()) path.Insert(0, "/" + at.Name);
            return path.ToString();
        }

        static string Describe(Material? material)
        {
            switch (material)
            {
                case null: return "(no material)";
                case ShaderMaterial shaded:
                    Shader? shader = shaded.Shader;
                    if (shader is null) return "ShaderMaterial(no shader)";
                    if (LightmapShader.IsLightmapShader(shader))
                        return "LightmapShader" + (shaded.GetShaderParameter("use_reflect").AsBool() ? "+reflect" : "") + (shaded.GetShaderParameter("use_gloss").AsBool() ? "+gloss" : "");
                    if (shader == PlayerSkinShader.Shader) return "PlayerSkinShader";
                    if (shader == Md3MorphShader.Shader) return "Md3MorphShader";
                    string code = shader.Code;
                    int end = code.IndexOf('\n');
                    string first = end > 0 ? code[..Math.Min(end, 90)] : "?";
                    int mode = code.IndexOf("render_mode", StringComparison.Ordinal);
                    string modeLine = mode >= 0 ? code[mode..Math.Min(code.Length, code.IndexOf(';', mode) + 1)] : "";
                    return $"ShaderMaterial[{first.Trim()} | {modeLine}] {shaded.ResourceName}";
                case BaseMaterial3D standard:
                    return $"{standard.GetClass()}[{standard.ShadingMode}, blend {standard.BlendMode}, transparency {standard.Transparency}, vertexcolor {standard.VertexColorUseAsAlbedo}, emission {standard.EmissionEnabled}, texture {(standard.AlbedoTexture is null ? "none" : "yes")}]";
                default: return material.GetClass();
            }
        }
    }
}
