using System;
using System.Globalization;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Services;

namespace VortexArena.Game.Client;

/// <summary>
/// A screen-space vignette — a soft darkened gradient framing the edges of the view. There is no QuakeC
/// ancestor (Darkplaces had no vignette); this is a new client-only cosmetic effect that composites a
/// full-screen <see cref="ColorRect"/> driven by a radial-gradient <see cref="ShaderMaterial"/> on its own
/// <see cref="CanvasLayer"/>, sitting above the 3D view (and the <see cref="ViewEffects"/> tint) but BELOW the
/// HUD so health/ammo/crosshair stay crisp. It mirrors the <see cref="ViewEffects"/> structure (full-rect quad,
/// live cvar reads) but self-drives in <see cref="_Process"/> instead of being fed by the host.
///
/// <para><b>The gradient model.</b> The shader computes a radial coordinate <c>t</c> that is 0 at the screen
/// centre and 1 at the screen edge (&gt;1 toward the corners). <c>cl_vignette_roundness</c> morphs the
/// iso-darkness contours from screen-hugging rectangles (0) to ellipses/circles (1). The colour is a
/// piecewise-linear ramp over a list of "bands": each band has a colour, an opacity, and a thickness (the
/// radial width over which it blends toward the next band inward). The innermost band always fades to fully
/// transparent at the centre, so the middle of the screen is never darkened.</para>
///
/// <para><b>Cvars</b> (all <c>cl_</c>, archived):
/// <list type="bullet">
///   <item><c>cl_vignette</c> — master on/off (1 = on).</item>
///   <item><c>cl_vignette_preset</c> — a number: <c>0</c> = off, <c>1</c> = subtle, <c>2</c> = medium (default),
///         <c>3</c> = heavy, <c>4</c> = cinematic, <c>5</c> = custom. The built-in presets are all-black with a
///         transparent centre; only <c>5</c> (custom) reads the per-band cvars below.</item>
///   <item><c>cl_vignette_roundness</c> — 0..1 shape (rectangular ↔ round). [custom]</item>
///   <item><c>cl_vignette_bands</c> — number of active colour bands, 1..4. [custom]</item>
///   <item><c>cl_vignette_band{N}_color</c> — "r g b" (0..1) of band N (N = 1 outermost … 4 innermost). [custom]</item>
///   <item><c>cl_vignette_band{N}_alpha</c> — band N opacity 0..1. [custom]</item>
///   <item><c>cl_vignette_band{N}_thickness</c> — band N radial thickness 0..1. [custom]</item>
/// </list>
/// Example: <c>set cl_vignette_preset 5; set cl_vignette_band1_color "0.1 0 0.2"; set cl_vignette_band1_alpha 0.6</c>.</para>
/// </summary>
public partial class VignetteOverlay : CanvasLayer
{
    /// <summary>Max colour bands (band 1 = outermost/edge … band 4 = innermost).</summary>
    private const int MaxBands = 4;

    /// <summary>Stops uploaded to the shader: one per band plus the transparent centre anchor.</summary>
    private const int MaxStops = MaxBands + 1;

    private ColorRect _rect = null!;
    private ShaderMaterial _material = null!;
    private Sig _last;     // last applied cvar snapshot — skip the re-upload while nothing changed
    private bool _hasLast;

    // One band of the gradient: an "r g b" colour, an opacity, and the radial thickness over which it blends
    // inward to the next band (the innermost band blends to a fully-transparent centre).
    private readonly record struct Band(Color Color, float Alpha, float Thickness);

    // A snapshot of every cvar that affects the result — compared frame-to-frame so we only rebuild + re-upload
    // the shader uniforms when something actually changed (no per-frame allocation in the steady state).
    private readonly record struct Sig(
        bool Enabled, int Preset, float Roundness, int Bands,
        Color C1, float A1, float T1,
        Color C2, float A2, float T2,
        Color C3, float A3, float T3,
        Color C4, float A4, float T4);

    public override void _Ready()
    {
        // Above the 3D view + the ViewEffects damage/liquid tint (layer -1), below the HUD (layers 4/5) so the
        // vignette frames the world without dimming health/ammo/crosshair.
        Layer = 1;

        // Idempotent — never clobbers a value the cfg/menu store already set (also registered at boot by
        // ClientSettings so the menu/console see them pre-match).
        RegisterDefaults(Api.Cvars);

        _material = new ShaderMaterial { Shader = new Shader { Code = ShaderCode } };

        _rect = new ColorRect
        {
            Name = "Vignette",
            // Full-rect anchors so it tracks any window resize (the shader works in UV space, 0..1).
            AnchorRight = 1f,
            AnchorBottom = 1f,
            OffsetLeft = 0f,
            OffsetTop = 0f,
            OffsetRight = 0f,
            OffsetBottom = 0f,
            // The shader overwrites COLOR wholesale, so the rect's own colour is irrelevant; keep white.
            Color = Colors.White,
            // Purely cosmetic — never eat input.
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Material = _material,
        };
        AddChild(_rect);

        Apply(ReadSig());
    }

    public override void _Process(double delta)
    {
        using var _prof = FrameProfiler.Scope("clientmisc");
        Sig s = ReadSig();
        if (!_hasLast || !s.Equals(_last))
            Apply(s);
    }

    // -------------------------------------------------------------------------------------------------
    //  Resolve the live cvars → gradient stops → shader uniforms.
    // -------------------------------------------------------------------------------------------------
    private void Apply(Sig s)
    {
        _last = s;
        _hasLast = true;

        ResolveBands(s, out float roundness, out Band[] bands);

        if (!s.Enabled || bands.Length == 0)
        {
            _rect.Visible = false;
            return;
        }

        // Build the stops from the edge inward, then reverse to ascending (centre → edge) for the shader.
        int n = bands.Length;
        int stopCount = n + 1; // bands + transparent centre anchor
        var descPos = new float[stopCount];
        var descCol = new Vector4[stopCount];

        float p = 1f; // the outermost band sits at the screen edge (t = 1)
        float maxAlpha = 0f;
        for (int k = 0; k < n; k++)
        {
            descPos[k] = Mathf.Max(p, 0f);
            Color c = bands[k].Color;
            float a = Mathf.Clamp(bands[k].Alpha, 0f, 1f);
            descCol[k] = new Vector4(c.R, c.G, c.B, a);
            maxAlpha = Mathf.Max(maxAlpha, a);
            p -= Mathf.Max(bands[k].Thickness, 0f); // step inward by this band's thickness
        }
        // The transparent centre anchor: same hue as the innermost band, alpha 0, at the inner edge of band n.
        Color inner = bands[n - 1].Color;
        descPos[n] = Mathf.Max(p, 0f);
        descCol[n] = new Vector4(inner.R, inner.G, inner.B, 0f);

        // Nothing visible (all bands transparent) → don't draw a fully-transparent quad.
        if (maxAlpha <= 0.001f)
        {
            _rect.Visible = false;
            return;
        }

        var pos = new float[MaxStops];
        var col = new Vector4[MaxStops];
        for (int i = 0; i < stopCount; i++)
        {
            pos[i] = descPos[stopCount - 1 - i]; // reverse → ascending positions (centre → edge)
            col[i] = descCol[stopCount - 1 - i];
        }
        // Pad the unused tail with the outer (edge) stop so the shader's dynamic indexing stays in range.
        for (int i = stopCount; i < MaxStops; i++)
        {
            pos[i] = pos[stopCount - 1];
            col[i] = col[stopCount - 1];
        }

        var colArr = new Godot.Collections.Array();
        var posArr = new Godot.Collections.Array();
        for (int i = 0; i < MaxStops; i++)
        {
            colArr.Add(col[i]);
            posArr.Add(pos[i]);
        }

        _material.SetShaderParameter(URoundness, Mathf.Clamp(roundness, 0f, 1f));
        _material.SetShaderParameter(UStopCount, stopCount);
        _material.SetShaderParameter(UStopColor, colArr);
        _material.SetShaderParameter(UStopPos, posArr);
        _rect.Visible = true;
    }

    // C2: cached StringName uniform names. Apply is change-gated (only on a signature change), so these mint no
    // StringName per frame today — but passing a string literal to SetShaderParameter (a StringName parameter)
    // would, so they are cached here. STANDING RULE: never pass a string literal to a Godot API typed
    // StringName/NodePath from a per-frame path — cache it as a static readonly StringName (see godot#105750 /
    // planning/PERFORMANCE_REPORT.md C2/C3). The XG0002 analyzer flags new violations inside _Process/_PhysicsProcess/_Draw.
    private static readonly StringName URoundness = "roundness";
    private static readonly StringName UStopCount = "stop_count";
    private static readonly StringName UStopColor = "stop_color";
    private static readonly StringName UStopPos = "stop_pos";

    /// <summary>
    /// Resolve the selected preset (or the custom cvars) into a roundness + an ordered band list (band 0 =
    /// outermost/edge). The built-in presets are all-black with a transparent centre — only <c>custom</c> honours
    /// the per-band colour/alpha/thickness cvars. An unknown preset name falls back to <c>subtle</c>.
    /// </summary>
    private static void ResolveBands(in Sig s, out float roundness, out Band[] bands)
    {
        switch (s.Preset)
        {
            case 0: // off
                roundness = 0.60f;
                bands = Array.Empty<Band>();
                return;

            case 2: // medium
                roundness = 0.60f;
                bands = new[]
                {
                    new Band(Colors.Black, 0.50f, 0.30f),
                    new Band(Colors.Black, 0.18f, 0.42f),
                };
                return;

            case 3: // heavy
                roundness = 0.55f;
                bands = new[]
                {
                    new Band(Colors.Black, 0.78f, 0.28f),
                    new Band(Colors.Black, 0.34f, 0.45f),
                };
                return;

            case 4: // cinematic
                roundness = 0.70f;
                bands = new[]
                {
                    new Band(Colors.Black, 0.70f, 0.45f),
                    new Band(Colors.Black, 0.22f, 0.40f),
                };
                return;

            case 5: // custom
                roundness = s.Roundness;
                int count = Mathf.Clamp(s.Bands, 1, MaxBands);
                Band[] all =
                {
                    new(s.C1, s.A1, s.T1),
                    new(s.C2, s.A2, s.T2),
                    new(s.C3, s.A3, s.T3),
                    new(s.C4, s.A4, s.T4),
                };
                bands = all[..count];
                return;

            case 1: // subtle (default)
            default:
                // The default "slight" vignette: a single soft black band that fades in over the outer ~45%.
                roundness = 0.60f;
                bands = new[] { new Band(Colors.Black, 0.30f, 0.45f) };
                return;
        }
    }

    private static Sig ReadSig() => new(
        CvarF("cl_vignette", 1f) != 0f,
        (int)CvarF("cl_vignette_preset", 2f),
        CvarF("cl_vignette_roundness", 0.60f),
        (int)CvarF("cl_vignette_bands", 2f),
        CvarC("cl_vignette_band1_color", Colors.Black), CvarF("cl_vignette_band1_alpha", 0.50f), CvarF("cl_vignette_band1_thickness", 0.30f),
        CvarC("cl_vignette_band2_color", Colors.Black), CvarF("cl_vignette_band2_alpha", 0.18f), CvarF("cl_vignette_band2_thickness", 0.42f),
        CvarC("cl_vignette_band3_color", Colors.Black), CvarF("cl_vignette_band3_alpha", 0.00f), CvarF("cl_vignette_band3_thickness", 0.20f),
        CvarC("cl_vignette_band4_color", Colors.Black), CvarF("cl_vignette_band4_alpha", 0.00f), CvarF("cl_vignette_band4_thickness", 0.20f));

    /// <summary>
    /// Register the <c>cl_vignette_*</c> defaults (archived, like the DP <c>seta</c> audio cvars). Idempotent —
    /// keeps any value the user's config or the menu already set. Called both at boot (from
    /// <c>ClientSettings.ApplyAll</c>, into the shared menu/console store) and from <see cref="_Ready"/> (into the
    /// gameplay store) so the cvars exist everywhere they're read.
    /// </summary>
    public static void RegisterDefaults(ICvarService cvars)
    {
        if (cvars is null)
            return;

        const CvarFlags save = CvarFlags.Save;
        cvars.Register("cl_vignette", "1", save);
        cvars.Register("cl_vignette_preset", "2", save); // 0 off / 1 subtle / 2 medium / 3 heavy / 4 cinematic / 5 custom
        cvars.Register("cl_vignette_roundness", "0.6", save);
        cvars.Register("cl_vignette_bands", "2", save);
        // Custom-mode band defaults mirror the "medium" preset (a sane starting point on switching to custom).
        cvars.Register("cl_vignette_band1_color", "0 0 0", save);
        cvars.Register("cl_vignette_band1_alpha", "0.5", save);
        cvars.Register("cl_vignette_band1_thickness", "0.3", save);
        cvars.Register("cl_vignette_band2_color", "0 0 0", save);
        cvars.Register("cl_vignette_band2_alpha", "0.18", save);
        cvars.Register("cl_vignette_band2_thickness", "0.42", save);
        cvars.Register("cl_vignette_band3_color", "0 0 0", save);
        cvars.Register("cl_vignette_band3_alpha", "0", save);
        cvars.Register("cl_vignette_band3_thickness", "0.2", save);
        cvars.Register("cl_vignette_band4_color", "0 0 0", save);
        cvars.Register("cl_vignette_band4_alpha", "0", save);
        cvars.Register("cl_vignette_band4_thickness", "0.2", save);
    }

    // ---- live cvar reads (fall back only when genuinely unset; an explicit 0 is honoured) -------------------

    private static float CvarF(string name, float fallback)
    {
        if (Api.Services is null)
            return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : Api.Cvars.GetFloat(name);
    }

    private static Color CvarC(string name, Color fallback)
    {
        if (Api.Services is null)
            return fallback;
        string s = Api.Cvars.GetString(name);
        if (string.IsNullOrWhiteSpace(s))
            return fallback;
        string[] parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3
            || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float r)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float g)
            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
            return fallback;
        return new Color(r, g, b);
    }

    // A multi-band radial vignette. `t` is a radial coordinate: 0 at the screen centre, 1 at the screen edge
    // (>1 toward the corners). `roundness` morphs the iso-darkness contours from screen-hugging rectangles (0)
    // to ellipses/circles (1). The colour is a piecewise-linear ramp over up to 5 stops (4 colour bands plus a
    // transparent centre anchor), uploaded from C# as `stop_pos` (ascending, centre→edge) and `stop_color`
    // (rgba; alpha = band opacity).
    private const string ShaderCode = @"
shader_type canvas_item;

uniform float roundness : hint_range(0.0, 1.0) = 0.85;
uniform int stop_count = 2;
uniform vec4 stop_color[5];
uniform float stop_pos[5];

void fragment() {
    vec2 p = (UV - vec2(0.5)) * 2.0;            // -1..1 per axis
    float box = max(abs(p.x), abs(p.y));         // rectangle-hugging: 1 on every border
    float circ = length(p);                      // round: 1 at edge mid, ~1.41 at corner
    float t = mix(box, circ, clamp(roundness, 0.0, 1.0));

    vec4 col = stop_color[0];                     // below the innermost stop -> transparent centre
    for (int i = 0; i < stop_count - 1; i++) {
        if (t >= stop_pos[i]) {
            float seg = max(stop_pos[i + 1] - stop_pos[i], 1e-5);
            float f = clamp((t - stop_pos[i]) / seg, 0.0, 1.0);
            col = mix(stop_color[i], stop_color[i + 1], f);
        }
    }
    if (t >= stop_pos[stop_count - 1]) {
        col = stop_color[stop_count - 1];         // beyond the outer stop -> full edge colour
    }

    COLOR = col;
}
";
}
