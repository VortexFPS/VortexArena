using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Services;
using VortexArena.Game.Menu;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>
/// Corona flares around lights — the port of DarkPlaces' <c>r_coronas</c> (<b>F2</b>).
///
/// <para><b>Why this matters more than its size suggests.</b> <c>r_coronas 1</c> is set in <b>every</b>
/// Xonotic effects preset, including <c>low</c> and <c>omg</c>. It is not a high-end extra: its absence is a
/// divergence from the <i>default</i> Xonotic look on every map that authors a light with a corona. A DP
/// corona is a bright additive flare drawn at the light's position, sized <c>coronasizescale × radius</c>
/// (DP's default <c>coronasizescale</c> is 0.25) and scaled by the light's own <c>corona</c> intensity.</para>
///
/// <para><b>Which lights flare.</b> Only those that ask (<c>Corona &gt; 0</c> in the light budget's roster),
/// which in practice means mapper-placed lights and <c>.rtlights</c> world lights. Effect lights get none —
/// that is DP's data, not a simplification: <c>effectinfo.txt</c> authors no coronas, and Xonotic's own CSQC
/// says of the burning-player dlight "no PFLAGS_CORONA, it looks bad"
/// (<c>csqcmodel_hooks.qc:585</c>).</para>
///
/// <para><b>Occlusion.</b> DP fades a corona by the fraction of its pixels that pass the depth test, using a
/// GPU occlusion query (<c>r_coronas_occlusionquery</c>). Godot has no per-object occlusion query exposed to
/// scripts, so this traces from the eye to the light instead and fades over a few frames. That is a
/// different mechanism with the same purpose and one visible difference: DP fades <i>partially</i> when a
/// flare is half-behind a pillar, whereas a single ray is all-or-nothing per frame — the temporal smoothing
/// below is what turns that back into a fade rather than a blink.</para>
/// </summary>
public sealed partial class CoronaRenderer : Node3D
{
    /// <summary>How fast a corona fades in/out when its occlusion state flips (units per second).</summary>
    private const float FadeRate = 6f;

    /// <summary>Cap on simultaneous flares. The budget already ranks and caps the lights themselves.</summary>
    private const int MaxCoronas = 32;

    private sealed class Flare
    {
        public MeshInstance3D Node = null!;
        public float Visibility;   // 0..1, smoothed
    }

    private readonly List<Flare> _pool = new();
    private readonly Dictionary<ulong, float> _visibility = new();

    private static Shader? _shader;
    private ShaderMaterial? _material;
    private QuadMesh? _quad;

    private static readonly StringName ColorUniform = "corona_color";

    public override void _Process(double delta)
    {
        using var _prof = FrameProfiler.Scope("coronas");

        float brightness = Cvar("r_coronas", 1f);
        LightBudget? budget = LightBudget.Instance;
        if (brightness <= 0f || budget is null)
        {
            HideAll();
            return;
        }

        Camera3D? cam = GetViewport()?.GetCamera3D();
        if (cam is null || !GodotObject.IsInstanceValid(cam))
        {
            HideAll();
            return;
        }

        bool occlude = Cvar("r_coronas_occlusionquery", 1f) != 0f && Api.Services is not null;
        NVec3 eye = Coords.ToQuake(cam.GlobalPosition);
        float dt = (float)delta;

        int used = 0;
        foreach ((Light3D light, float corona, float size) in budget.Coronas())
        {
            if (used >= MaxCoronas)
                break;

            ulong id = light.GetInstanceId();
            float target = 1f;
            if (occlude)
            {
                NVec3 lp = Coords.ToQuake(light.GlobalPosition);
                TraceResult tr = Api.Trace.Trace(eye, NVec3.Zero, NVec3.Zero, lp, MoveFilter.WorldOnly, null);
                target = tr.Fraction >= 1f ? 1f : 0f;
            }

            // Smooth toward the target so a flare passing behind a pillar fades rather than blinking — the
            // stand-in for DP's fractional occlusion query (see the type doc).
            _visibility.TryGetValue(id, out float vis);
            vis = Mathf.MoveToward(vis, target, FadeRate * dt);
            _visibility[id] = vis;
            if (vis <= 0.01f)
                continue;

            float range = light switch
            {
                OmniLight3D o => o.OmniRange,
                SpotLight3D s => s.SpotRange,
                _ => 0f,
            };
            if (range <= 0f)
                continue;

            MeshInstance3D quad = Acquire(used++);
            quad.GlobalPosition = light.GlobalPosition;
            // DP sizes the flare as coronasizescale x radius; the quad spans -1..1 so the scale IS the radius.
            float r = MathF.Max(1f, range * MathF.Max(0.01f, size));
            quad.Scale = new Vector3(r, r, r);
            Color c = light.LightColor;
            float energy = corona * brightness * vis * MathF.Max(0.05f, light.LightEnergy);
            quad.SetInstanceShaderParameter(ColorUniform, new Vector3(c.R * energy, c.G * energy, c.B * energy));
            quad.Visible = true;
        }

        for (int i = used; i < _pool.Count; i++)
            _pool[i].Node.Visible = false;

        // Drop smoothing state for lights that are gone, so the dictionary cannot grow across a match.
        if (_visibility.Count > 256)
            _visibility.Clear();
    }

    private MeshInstance3D Acquire(int index)
    {
        while (_pool.Count <= index)
        {
            var mi = new MeshInstance3D
            {
                Name = $"corona{_pool.Count}",
                Mesh = SharedQuad(),
                MaterialOverride = SharedMaterial(),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Visible = false,
            };
            AddChild(mi);
            _pool.Add(new Flare { Node = mi });
        }
        return _pool[index].Node;
    }

    private QuadMesh SharedQuad() => _quad ??= new QuadMesh { Size = new Vector2(2f, 2f) };

    /// <summary>
    /// The flare material: a camera-facing additive disc with a soft falloff. Billboarded in the vertex stage
    /// rather than by rotating the node, so it faces the camera exactly and costs nothing on the CPU.
    /// Additive and depth-test-OFF: a corona is a lens/eye artefact, so it belongs on top of the scene, and
    /// the occlusion trace above — not the depth buffer — is what decides whether it is there at all.
    /// </summary>
    private ShaderMaterial SharedMaterial()
    {
        if (_material is not null)
            return _material;
        _shader ??= new Shader { Code = CoronaShaderCode };
        return _material = new ShaderMaterial { Shader = _shader };
    }

    private const string CoronaShaderCode = @"// VortexArena corona flare (r_coronas). Generated in C#.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, depth_test_disabled, blend_add;
instance uniform vec3 corona_color = vec3(1.0);
void vertex() {
    // Billboard: keep the model's translation+scale, replace its rotation with the view's, so the quad
    // always faces the camera. (MODELVIEW's basis becomes axis-aligned with the scale preserved.)
    MODELVIEW_MATRIX = VIEW_MATRIX * mat4(
        vec4(length(MODEL_MATRIX[0].xyz), 0.0, 0.0, 0.0),
        vec4(0.0, length(MODEL_MATRIX[1].xyz), 0.0, 0.0),
        vec4(0.0, 0.0, length(MODEL_MATRIX[2].xyz), 0.0),
        MODEL_MATRIX[3]);
}
void fragment() {
    // Soft radial falloff: bright core, long tail. pow() shapes it into a flare rather than a flat disc.
    float d = clamp(length(UV * 2.0 - 1.0), 0.0, 1.0);
    float a = pow(1.0 - d, 3.0);
    ALBEDO = vec3(0.0);
    EMISSION = corona_color * a;
    ALPHA = a;
}
";

    private void HideAll()
    {
        foreach (Flare f in _pool)
            if (GodotObject.IsInstanceValid(f.Node))
                f.Node.Visible = false;
    }

    private static float Cvar(string name, float fallback)
    {
        string s = MenuState.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : MenuState.Cvars.GetFloat(name);
    }
}
