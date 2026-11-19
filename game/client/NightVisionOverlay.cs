using System;
using Godot;
using VortexArena.Common.Gameplay;
using VortexArena.Game.Hud;

namespace VortexArena.Game.Client;

/// <summary>CSQC View_NightVision: tint the rendered scene, then add two independently animated noise layers.</summary>
internal sealed partial class NightVisionOverlay : Control
{
    private static readonly StringName NoiseTexture = "noise_texture";
    private static readonly StringName UvOrigin = "uv_origin";
    private static readonly StringName UvHorizontal = "uv_horizontal";
    private static readonly StringName UvVertical = "uv_vertical";
    private static readonly StringName Rgba = "rgba";

    private readonly Random _random = new();
    private readonly PinkNoise _backgroundNoise = new();
    private readonly PinkNoise _foregroundNoise = new();
    private ShaderMaterial? _background;
    private ShaderMaterial? _foreground;

    public NightVisionOverlay()
    {
        Name = "NightVision";
        Visible = false;
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    public void UpdateOverlay(float time, float frameTime, int serverFlags)
    {
        Visible = (WorldTint.FullbrightRequested || WorldTint.FakelightMode >= 2)
            && (serverFlags & ServerFlags.AllowFullbright) == 0;
        if (!Visible)
            return;

        EnsureLayers();

        float backgroundAlpha = _backgroundNoise.Sample(_random, frameTime * 1.5f) * 0.05f + 0.15f;
        var bgTopLeft = new Vector2(0.2f * Mathf.Sin(time * 0.3f), 0.3f * Mathf.Cos(time * 0.7f));
        var bgBottomLeft = new Vector2(0.6f * Mathf.Cos(time * 1.2f), 2.25f - 0.3f * Mathf.Sin(time * 2.2f));
        var bgTopRight = new Vector2(1.5f - 0.2f * Mathf.Sin(time * 0.5f), 0.5f * Mathf.Cos(time * 1.7f));
        UpdateLayer(_background!, bgTopLeft, bgTopRight - bgTopLeft, bgBottomLeft - bgTopLeft,
            new Vector4(1f, 1f, 1f, backgroundAlpha));

        float foregroundAlpha = _foregroundNoise.Sample(_random, frameTime * 0.1f) * 0.05f + 0.12f;
        float colorNoise = White(_random);
        var fgTopLeft = new Vector2(White(_random), White(_random));
        var fgVertical = new Vector2(0f, 3f * (1f + White(_random) * 0.2f));
        var fgHorizontal = new Vector2(2f * (1f + White(_random) * 0.3f), 0f);
        UpdateLayer(_foreground!, fgTopLeft, fgHorizontal, fgVertical,
            new Vector4(0.3f + 0.1f * colorNoise, 0.6f + 0.4f * colorNoise,
                0.4f + 0.2f * colorNoise, foregroundAlpha));
    }

    private void EnsureLayers()
    {
        if (_background is not null)
            return;

        AddLayer("Tint", new ShaderMaterial { Shader = new Shader { Code = TintShader } });

        var noiseShader = new Shader { Code = NoiseShader };
        _background = new ShaderMaterial { Shader = noiseShader };
        _background.SetShaderParameter(NoiseTexture, TextureCache.Get("gfx/nightvision-bg"));
        AddLayer("BackgroundNoise", _background);

        _foreground = new ShaderMaterial { Shader = noiseShader };
        _foreground.SetShaderParameter(NoiseTexture, TextureCache.Get("gfx/nightvision-fg"));
        AddLayer("ForegroundNoise", _foreground);
    }

    private void AddLayer(string name, ShaderMaterial material)
    {
        var rect = new ColorRect { Name = name, Material = material, MouseFilter = MouseFilterEnum.Ignore };
        rect.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(rect);
    }

    private static void UpdateLayer(ShaderMaterial layer, Vector2 origin, Vector2 horizontal, Vector2 vertical, Vector4 rgba)
    {
        layer.SetShaderParameter(UvOrigin, origin);
        layer.SetShaderParameter(UvHorizontal, horizontal);
        layer.SetShaderParameter(UvVertical, vertical);
        layer.SetShaderParameter(Rgba, rgba);
    }

    private static float White(Random random) => random.NextSingle() * 2f - 1f;

    // lib/noise.qh: three independently refreshed bands, with probabilities scaled to a 60 Hz reference.
    private sealed class PinkNoise
    {
        private float _first;
        private float _second;
        private float _third;

        public float Sample(Random random, float dt)
        {
            float frames = dt * 60f;
            if (random.NextSingle() > Mathf.Pow(0.3190f, frames))
                _first = 0.34848f * White(random);
            if (random.NextSingle() > Mathf.Pow(0.7756f, frames))
                _second = 0.28768f * White(random);
            if (random.NextSingle() > Mathf.Pow(0.9613f, frames))
                _third = 0.43488f * White(random);
            return _first + _second + _third;
        }
    }

    private const string TintShader = """
        shader_type canvas_item;
        render_mode unshaded, blend_mul;
        void fragment() {
            COLOR = vec4(0.5, 1.0, 0.3, 1.0);
        }
        """;

    private const string NoiseShader = """
        shader_type canvas_item;
        render_mode unshaded, blend_add;
        uniform sampler2D noise_texture : hint_default_black, filter_linear, repeat_enable;
        uniform vec2 uv_origin;
        uniform vec2 uv_horizontal;
        uniform vec2 uv_vertical;
        uniform vec4 rgba;
        void fragment() {
            vec2 noise_uv = uv_origin + UV.x * uv_horizontal + UV.y * uv_vertical;
            COLOR = texture(noise_texture, noise_uv) * rgba;
        }
        """;
}
