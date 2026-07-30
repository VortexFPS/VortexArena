using System.Globalization;
using System.Text;

namespace XonoticGodot.Formats.Materials;

/// <summary>
/// Shared GDShader (GLSL) source emitters for Quake 3 stage directives — the <c>tcMod</c> stack,
/// <c>rgbGen</c> colour modulation, and the Q3 waveform expressions they build on. Extracted from the
/// game-side <c>ShaderCompiler</c> so both the generic animated-stage shader and the autosprite deform
/// shader (<see cref="AutospriteShaderGen"/>) emit identical, testable GLSL. Pure string building — no
/// Godot dependency; the caller wraps the source in a <c>Shader</c> resource.
///
/// Conventions shared by every emitter: the running UV lives in a local <c>vec2 uv</c>, the running
/// texel in <c>vec4 c</c>, and time is the built-in <c>TIME</c>. Q3 applies tcMods in source order, so
/// callers must walk <see cref="ShaderStage.TcMods"/> in order.
/// </summary>
public static class Q3StageGlsl
{
    /// <summary>Emit the GLSL for one tcMod operation, transforming the running <c>uv</c>.</summary>
    public static void EmitTcMod(StringBuilder sb, TcMod m)
    {
        switch (m.Type)
        {
            case TcModType.Scroll:
                sb.Append("    uv += vec2(").Append(Flt(m.P(0))).Append(", ").Append(Flt(m.P(1)))
                  .Append(") * TIME;            // tcMod scroll\n");
                break;
            case TcModType.Scale:
                sb.Append("    uv *= vec2(").Append(Flt(m.P(0))).Append(", ").Append(Flt(m.P(1)))
                  .Append(");                   // tcMod scale\n");
                break;
            case TcModType.Rotate:
            {
                // degrees per second about the (0.5,0.5) center.
                string rad = Flt(m.P(0) * (System.MathF.PI / 180f));
                sb.Append("    {\n");
                sb.Append("        float a = ").Append(rad).Append(" * TIME;       // tcMod rotate\n");
                sb.Append("        float s = sin(a), co = cos(a);\n");
                sb.Append("        uv -= vec2(0.5);\n");
                sb.Append("        uv = vec2(co*uv.x - s*uv.y, s*uv.x + co*uv.y);\n");
                sb.Append("        uv += vec2(0.5);\n");
                sb.Append("    }\n");
                break;
            }
            case TcModType.Stretch:
            {
                WaveForm w = m.Wave ?? new WaveForm();
                // stretch scales UV about center by 1/wave(t) (Q3 divides by the wave value).
                sb.Append("    {\n");
                sb.Append("        float w = ").Append(WaveExpr(w)).Append(";   // tcMod stretch\n");
                sb.Append("        float inv = (abs(w) < 0.0001) ? 1.0 : 1.0 / w;\n");
                sb.Append("        uv = (uv - vec2(0.5)) * inv + vec2(0.5);\n");
                sb.Append("    }\n");
                break;
            }
            case TcModType.Turb:
            {
                // base amp phase freq → sine warp of UV by position+time.
                string bas = Flt(m.P(0)), amp = Flt(m.P(1)), ph = Flt(m.P(2)), fr = Flt(m.P(3));
                sb.Append("    {\n");
                sb.Append("        float ph = ").Append(ph).Append(";              // tcMod turb\n");
                sb.Append("        float fr = ").Append(fr).Append(";\n");
                sb.Append("        float amp = ").Append(amp).Append(";\n");
                sb.Append("        uv.x += amp * sin((uv.y + ").Append(bas).Append(" + TIME*fr + ph) * 6.2831853);\n");
                sb.Append("        uv.y += amp * sin((uv.x + ").Append(bas).Append(" + TIME*fr + ph) * 6.2831853);\n");
                sb.Append("    }\n");
                break;
            }
            case TcModType.Transform:
                // 2x2 + translate. Static, but fold it in for completeness.
                sb.Append("    uv = vec2(").Append(Flt(m.P(0))).Append("*uv.x + ").Append(Flt(m.P(2)))
                  .Append("*uv.y + ").Append(Flt(m.P(4))).Append(", ")
                  .Append(Flt(m.P(1))).Append("*uv.x + ").Append(Flt(m.P(3)))
                  .Append("*uv.y + ").Append(Flt(m.P(5))).Append("); // tcMod transform\n");
                break;
            case TcModType.Page:
            {
                // tcMod page <w> <h> <delay> — DP's "poor man's animMap" (gl_rmain.c Q3TCMOD_PAGE): the
                // texture is a w×h flipbook atlas; every <delay> seconds advance one page, translating UV
                // by ((idx % w)/w, (idx / w)/h). idx = floor(fract(time / (delay*w*h)) * w*h). Electro's
                // bolt crackle is `tcMod page 4 1 0.1` — 4 horizontal pages at 10 fps.
                float w = m.P(0), h = m.P(1), delay = m.P(2);
                if (w >= 1f && h >= 1f && delay > 0f)
                {
                    string cycle = Flt(delay * w * h);
                    sb.Append("    {\n");
                    sb.Append("        float pf = fract(TIME / ").Append(cycle).Append(");   // tcMod page ")
                      .Append(Flt(w)).Append('x').Append(Flt(h)).Append('\n');
                    sb.Append("        float idx = floor(pf * ").Append(Flt(w * h)).Append(");\n");
                    sb.Append("        uv += vec2(mod(idx, ").Append(Flt(w)).Append(") / ").Append(Flt(w))
                      .Append(", floor(idx / ").Append(Flt(w)).Append(") / ").Append(Flt(h)).Append(");\n");
                    sb.Append("    }\n");
                }
                break;
            }
            default:
                break; // entityTranslate: no time animation we can express statically
        }
    }

    /// <summary>
    /// Emit the <c>rgbGen</c> colour modulation onto the running <c>c</c> (playtest r14 D — the mortar
    /// sight's <c>rgbGen wave sawtooth 0 1 0 10</c> blink). Only the forms a generated stage can honor:
    /// <c>wave</c> (Q3 waveform, clamped 0..1 exactly as the fixed-function vertex colour clamped) and
    /// <c>const</c>. Identity/vertex/entity forms keep the default untouched colour — same as before.
    /// </summary>
    public static void EmitRgbGen(StringBuilder sb, ColorGen? cg)
    {
        if (cg is { Type: ColorGenType.Wave, Wave: not null })
        {
            WaveForm w = cg.Wave;
            // value = base + amplitude * func(phase + time*freq), func over one period of x in [0,1).
            sb.Append("    float wx = fract(").Append(Flt(w.Phase)).Append(" + TIME * ").Append(Flt(w.Frequency))
              .Append(");   // rgbGen wave ").Append(w.RawName).Append('\n');
            string func = w.Func switch
            {
                WaveFunc.Sin => "sin(wx * 6.2831853)",
                WaveFunc.Square => "(wx < 0.5 ? 1.0 : -1.0)",
                // Q3 triangle table: 0 -> 1 over the first quarter, back to 0, then mirrored negative.
                WaveFunc.Triangle => "(1.0 - 4.0 * abs(fract(wx + 0.25) - 0.5))",
                WaveFunc.Sawtooth => "wx",
                WaveFunc.InverseSawtooth => "(1.0 - wx)",
                _ => "sin(wx * 6.2831853)", // noise/unknown: a periodic stand-in beats a frozen constant
            };
            sb.Append("    c.rgb *= clamp(").Append(Flt(w.Base)).Append(" + ").Append(Flt(w.Amplitude))
              .Append(" * ").Append(func).Append(", 0.0, 1.0);\n");
        }
        else if (cg is { Type: ColorGenType.Const } cc && cc.Parms.Length >= 3)
        {
            sb.Append("    c.rgb *= vec3(").Append(Flt(cc.Parms[0])).Append(", ").Append(Flt(cc.Parms[1]))
              .Append(", ").Append(Flt(cc.Parms[2])).Append(");   // rgbGen const\n");
        }
    }

    /// <summary>A Q3 waveform evaluated at TIME, as a GLSL expression: base + amp * wave(phase + freq*TIME).</summary>
    public static string WaveExpr(WaveForm w) => WaveExprPhased(w, "0.0");

    /// <summary>Waveform expression with an extra spatial phase term added (for surface-varying deforms).</summary>
    public static string WaveExprPhased(WaveForm w, string extraPhase)
    {
        string bas = Flt(w.Base);
        string amp = Flt(w.Amplitude);
        string ph = Flt(w.Phase);
        string fr = Flt(w.Frequency);
        // t = phase + extraPhase + freq*TIME ; argument in [0,1) cycles.
        string t = $"({ph} + {extraPhase} + {fr} * TIME)";
        string wave = w.Func switch
        {
            WaveFunc.Sin => $"sin({t} * 6.2831853)",
            WaveFunc.Triangle => $"(abs(fract({t}) * 2.0 - 1.0) * 2.0 - 1.0)",
            WaveFunc.Square => $"(fract({t}) < 0.5 ? 1.0 : -1.0)",
            WaveFunc.Sawtooth => $"(fract({t}) * 2.0 - 1.0)",
            WaveFunc.InverseSawtooth => $"(1.0 - fract({t}) * 2.0)",
            WaveFunc.Noise => $"(sin({t} * 12.9898) * 0.5)", // cheap pseudo-noise
            _ => "0.0",
        };
        return $"({bas} + {amp} * {wave})";
    }

    /// <summary>The Godot alpha-scissor cutoff for a Q3 <c>alphaFunc</c> comparator (GE128/GT0/LT128).</summary>
    public static float AlphaCutoff(string alphaFunc)
    {
        // Q3: GT0 → >0 (cutoff ~0.004), LT128 → <0.5, GE128 → >=0.5. We approximate with a single scissor
        // threshold (the common case is GE128 ≈ 0.5).
        string f = alphaFunc.ToUpperInvariant();
        if (f.Contains("128")) return 0.5f;
        if (f.Contains("GT0") || f.Contains("GE0")) return 0.004f;
        return 0.5f;
    }

    /// <summary>Format a float for GLSL source: invariant culture, always with a decimal point.</summary>
    public static string Flt(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v))
            v = 0f;
        return v.ToString("0.0######", CultureInfo.InvariantCulture);
    }
}
