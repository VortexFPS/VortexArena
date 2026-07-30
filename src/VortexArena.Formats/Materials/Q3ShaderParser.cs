using System;
using System.Collections.Generic;
using System.Globalization;

namespace XonoticGodot.Formats.Materials;

/// <summary>
/// Parses Quake 3 / Darkplaces <c>.shader</c> material scripts into <see cref="ShaderDef"/> POCOs.
///
/// A <c>.shader</c> file is a flat list of <c>shadername { ... }</c> blocks. Each block holds global
/// directives (one per line) and nested <c>{ ... }</c> render stages. This parser is a faithful, but
/// engine-neutral, reimplementation of Darkplaces' <c>Mod_LoadQ3Shaders</c> (see
/// <c>Base/darkplaces/model_shared.c</c>):
/// <list type="bullet">
/// <item><b>Tokenizer</b> mirrors <c>COM_ParseToken_QuakeC</c>: <c>//</c> line comments and
/// <c>/* ... */</c> block comments are stripped; <c>"..."</c> is one token; <c>{</c>/<c>}</c> are
/// structural; and — when reading a directive line — a newline is returned as a sentinel so a directive
/// is exactly the tokens up to the end of its line.</item>
/// <item><b>Case-insensitive</b> keyword and shader-name matching.</item>
/// <item><b>Robust</b>: a malformed shader (e.g. missing <c>{</c>) is skipped and parsing resumes at the
/// next top-level token; the parser never throws on bad shader content. An optional warning sink reports
/// what was skipped.</item>
/// <item><b>dp_ remap</b>: a leading <c>dp_</c> on the first token of a line is rewritten to <c>dp</c>,
/// exactly as DP does, so <c>dp_water</c>/<c>dp_reflect</c> match <c>dpwater</c>/<c>dpreflect</c>.</item>
/// </list>
/// On a redeclared shader name the <b>first</b> definition wins (DP keeps the first and ignores later
/// duplicates), and across files the first file to define a name wins via <see cref="ParseFiles"/>.
/// </summary>
public static class Q3ShaderParser
{
    /// <summary>Darkplaces caps a shader at 8 stages (<c>Q3SHADER_MAXLAYERS</c>); extra stages are parsed but dropped.</summary>
    private const int MaxLayers = 8;

    /// <summary>
    /// Parses one <c>.shader</c> file's text into a name→<see cref="ShaderDef"/> map (case-insensitive keys).
    /// Never throws on malformed shader bodies; pass <paramref name="onWarning"/> to observe skips.
    /// </summary>
    public static IReadOnlyDictionary<string, ShaderDef> Parse(string text, Action<string>? onWarning = null)
    {
        var result = new Dictionary<string, ShaderDef>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text))
            return result;

        var tz = new Tokenizer(text);
        ParseInto(tz, result, onWarning);
        return result;
    }

    /// <summary>
    /// Parses and merges several <c>.shader</c> files (e.g. the whole <c>scripts/*.shader</c> set). Files
    /// are processed in the given order; the first definition of a name wins, matching DP's behaviour where
    /// the search order fixes precedence.
    /// </summary>
    public static IReadOnlyDictionary<string, ShaderDef> ParseFiles(IEnumerable<string> texts, Action<string>? onWarning = null)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var result = new Dictionary<string, ShaderDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text))
                continue;
            var tz = new Tokenizer(text);
            ParseInto(tz, result, onWarning);
        }
        return result;
    }

    private static void ParseInto(Tokenizer tz, Dictionary<string, ShaderDef> result, Action<string>? onWarning)
    {
        // Top level: <name> { ... } <name> { ... } ...
        while (tz.Next(returnNewline: false, out string name, out _))
        {
            // A stray '{' or '}' at top level is junk; resync.
            if (name == "{" || name == "}")
            {
                onWarning?.Invoke($"Q3Shader: unexpected '{name}' at top level, skipping.");
                continue;
            }

            // Expect an opening brace for the body.
            if (!tz.Next(returnNewline: false, out string brace, out _))
            {
                // EOF right after a name: nothing more to do.
                onWarning?.Invoke($"Q3Shader: shader '{name}' has no body (EOF); skipping.");
                break;
            }
            if (brace != "{")
            {
                // Malformed: a name not followed by '{'. The token we just read is the best candidate for
                // the *next* shader's name, so push it back and let the loop re-anchor on it. This recovers
                // gracefully from braceless junk lines without desyncing the rest of the file. (DP instead
                // aborts the whole file here; we prefer to salvage the well-formed shaders that follow.)
                onWarning?.Invoke($"Q3Shader: shader '{name}' not followed by '{{' (found '{brace}'); skipping to next.");
                if (brace != "{" && brace != "}")
                    tz.PushBack(brace);
                continue;
            }

            ShaderDef def;
            try
            {
                def = ParseShaderBody(tz, name, onWarning);
            }
            catch (Exception ex)
            {
                // Defensive: should not happen (the body parser is non-throwing), but never let one bad
                // shader abort the whole file. Skip to the matching close brace if we can.
                onWarning?.Invoke($"Q3Shader: error parsing shader '{name}': {ex.Message}; skipping.");
                continue;
            }

            // First declaration wins (DP ignores later duplicates).
            if (!result.ContainsKey(def.Name))
                result.Add(def.Name, def);
            else
                onWarning?.Invoke($"Q3Shader: shader '{def.Name}' already defined; ignoring redeclaration.");
        }
    }

    /// <summary>Parses the body between the shader's outer braces. Assumes the opening '{' was consumed.</summary>
    private static ShaderDef ParseShaderBody(Tokenizer tz, string name, Action<string>? onWarning)
    {
        var def = new ShaderDef { Name = name };

        while (true)
        {
            // Peek the next structural token (no newline sentinel here, like DP's outer loop).
            if (!tz.Next(returnNewline: false, out string tok, out _))
                break; // EOF inside a shader: return what we have.

            if (tok == "}")
                break; // end of shader

            if (tok == "{")
            {
                var stage = ParseStage(tz, def, onWarning);
                if (def.Stages.Count < MaxLayers)
                    def.Stages.Add(stage);
                // else: parsed-but-dropped (DP still parses so $lightmap etc. are consumed).
                continue;
            }

            // Otherwise 'tok' is the first word of a global directive line. Collect the rest of the line.
            var parms = ReadDirectiveLine(tz, tok);
            try
            {
                ApplyGlobalDirective(def, parms, onWarning);
            }
            catch (Exception ex)
            {
                onWarning?.Invoke($"Q3Shader: shader '{name}' bad directive '{string.Join(' ', parms)}': {ex.Message}");
            }
        }

        return def;
    }

    /// <summary>Parses one stage <c>{ ... }</c>. Assumes the opening '{' was consumed.</summary>
    private static ShaderStage ParseStage(Tokenizer tz, ShaderDef owner, Action<string>? onWarning)
    {
        var stage = new ShaderStage();

        while (true)
        {
            if (!tz.Next(returnNewline: true, out string tok, out _))
                break; // EOF inside a stage
            if (tok == "\n")
                continue; // blank line
            if (tok == "}")
                break; // end of stage

            // 'tok' starts a stage directive line.
            var parms = ReadDirectiveLine(tz, tok);
            try
            {
                ApplyStageDirective(stage, parms, owner, onWarning);
            }
            catch (Exception ex)
            {
                onWarning?.Invoke($"Q3Shader: shader '{owner.Name}' bad stage directive '{string.Join(' ', parms)}': {ex.Message}");
            }
        }

        // Derive the high-level blend classification once the factors are known.
        stage.BlendMode = ClassifyBlend(stage.BlendSrc, stage.BlendDst, stage.HasBlendFunc);
        return stage;
    }

    /// <summary>
    /// Reads the remainder of a directive line into a parameter list whose first element is
    /// <paramref name="first"/>. Stops at a newline sentinel or a '}' (the '}' is NOT consumed here when it
    /// terminates the line — but DP's tokenizer hands back '}' as its own token, so we detect and rewind it
    /// by leaving the brace handling to the caller's loop). Applies the DP <c>dp_</c>→<c>dp</c> remap to the
    /// first token.
    /// </summary>
    private static List<string> ReadDirectiveLine(Tokenizer tz, string first)
    {
        var parms = new List<string> { RemapDpPrefix(first) };
        while (tz.Next(returnNewline: true, out string t, out _))
        {
            if (t == "\n")
                break;
            if (t == "}")
            {
                // A '}' ends both the line and the enclosing block. Push it back so the block loop sees it.
                tz.PushBack(t);
                break;
            }
            parms.Add(t);
        }
        return parms;
    }

    private static string RemapDpPrefix(string token)
    {
        // DP: if the first token starts with "dp_", rewrite to "dp" + rest.
        if (token.Length > 3 && (token[0] == 'd' || token[0] == 'D')
                             && (token[1] == 'p' || token[1] == 'P')
                             && token[2] == '_')
            return "dp" + token.Substring(3);
        return token;
    }

    // ---------------------------------------------------------------------------------------------
    // Global (shader-level) directives.
    // ---------------------------------------------------------------------------------------------

    private static void ApplyGlobalDirective(ShaderDef def, List<string> p, Action<string>? onWarning)
    {
        if (p.Count == 0)
            return;
        string key = p[0];

        switch (key.ToLowerInvariant())
        {
            case "surfaceparm":
                if (p.Count >= 2)
                    def.SurfaceParms.Add(p[1].ToLowerInvariant());
                return;

            case "cull":
                def.Cull = ParseCull(p.Count >= 2 ? p[1] : "");
                return;

            case "nopicmip":
                def.NoPicmip = true;
                return;

            case "nomipmaps":
            case "nomipmap":
                def.NoMipmaps = true;
                return;

            case "polygonoffset":
                def.PolygonOffset = true;
                return;

            case "sort":
                if (p.Count >= 2)
                {
                    def.Sort = p[1];
                    def.SortValue = ResolveSort(p[1]);
                }
                return;

            case "deformvertexes":
                ParseDeform(def, p, onWarning);
                return;

            case "skyparms":
                ParseSkyParms(def, p);
                return;

            case "sky":
                // Bare "sky <name>": DP records the skybox name and forces the sky surfaceparm.
                def.SurfaceParms.Add("sky");
                if (p.Count >= 2 && !string.Equals(p[1], "-", StringComparison.Ordinal))
                    def.SkyParms = new SkyParms { FarBox = p[1] };
                return;

            case "fogparms":
                ParseFogParms(def, p);
                return;

            // ---- Darkplaces extensions (already dp-prefixed by RemapDpPrefix). ----
            case "dpglosstexture":
                if (p.Count >= 2) def.Dp.GlossTexture = p[1];
                return;
            case "dpreflectcube":
                if (p.Count >= 2) def.Dp.ReflectCube = p[1];
                return;
            case "dpnortlight":
                def.Dp.NoRtLight = true;
                return;
            case "dpshadow":
                def.Dp.Shadow = true;
                return;
            case "dpnoshadow":
                def.Dp.NoShadow = true;
                return;
            case "dpmeshcollisions":
                def.Dp.MeshCollisions = true;
                return;
            case "dpcamera":
                def.Dp.Camera = true;
                return;
            case "dpglossintensitymod":
                if (p.Count >= 2) def.Dp.GlossIntensityMod = F(p[1]);
                return;
            case "dpglossexponentmod":
                if (p.Count >= 2) def.Dp.GlossExponentMod = F(p[1]);
                return;
            case "dprtlightambient":
                if (p.Count >= 2) def.Dp.RtLightAmbient = F(p[1]);
                return;
            case "dpoffsetmapping":
                ParseOffsetMapping(def, p);
                return;
            case "dpreflect":
                if (p.Count >= 6)
                    def.Dp.Reflect = new DpReflect
                    {
                        Factor = F(p[1]), R = F(p[2]), G = F(p[3]), B = F(p[4]), A = F(p[5]),
                    };
                return;
            case "dprefract":
                if (p.Count >= 5)
                    def.Dp.Refract = new DpRefract { Factor = F(p[1]), R = F(p[2]), G = F(p[3]), B = F(p[4]) };
                return;
            case "dpwater":
                if (p.Count >= 12)
                    def.Dp.Water = new DpWater
                    {
                        ReflectMin = F(p[1]), ReflectMax = F(p[2]),
                        RefractFactor = F(p[3]), ReflectFactor = F(p[4]),
                        RefractR = F(p[5]), RefractG = F(p[6]), RefractB = F(p[7]),
                        ReflectR = F(p[8]), ReflectG = F(p[9]), ReflectB = F(p[10]),
                        WaterAlpha = F(p[11]),
                    };
                return;
            case "dpwaterscroll":
                if (p.Count >= 3) def.Dp.WaterScroll = new[] { F(p[1]), F(p[2]) };
                return;
            case "dptransparentsort":
                if (p.Count >= 2) def.Dp.TransparentSort = p[1];
                return;
            case "dppolygonoffset":
                def.Dp.PolygonOffset = true;
                def.PolygonOffset = true;
                if (p.Count >= 3) def.Dp.PolygonOffsetParms = new[] { F(p[1]), F(p[2]) };
                else if (p.Count >= 2) def.Dp.PolygonOffsetParms = new[] { F(p[1]), 0f };
                return;
            // dpshaderkill* are cvar-conditional in DP. We can't evaluate cvars here, so we keep them raw
            // (the compiler/host can decide). Fall through to Raw.
        }

        // Unrecognized OR compiler-only (q3map_*, qer_*) directives: keep the line verbatim in Raw.
        def.Raw[key] = p.Count > 1 ? string.Join(' ', p.GetRange(1, p.Count - 1)) : string.Empty;
    }

    // ---------------------------------------------------------------------------------------------
    // Stage (layer) directives.
    // ---------------------------------------------------------------------------------------------

    private static void ApplyStageDirective(ShaderStage s, List<string> p, ShaderDef owner, Action<string>? onWarning)
    {
        if (p.Count == 0)
            return;

        switch (p[0].ToLowerInvariant())
        {
            case "map":
            case "clampmap":
                if (p.Count >= 2)
                {
                    s.MapTexture = p[1];
                    if (string.Equals(p[0], "clampmap", StringComparison.OrdinalIgnoreCase))
                        s.ClampMap = true;
                }
                return;

            case "animmap":
            case "animclampmap":
                if (p.Count >= 3)
                {
                    bool clamp = string.Equals(p[0], "animclampmap", StringComparison.OrdinalIgnoreCase);
                    var frames = p.GetRange(2, p.Count - 2).ToArray();
                    s.AnimMap = new AnimMap { Fps = F(p[1]), Frames = frames, Clamp = clamp };
                    if (clamp) s.ClampMap = true;
                }
                return;

            case "blendfunc":
                ParseBlendFunc(s, p);
                return;

            case "alphafunc":
                if (p.Count >= 2) s.AlphaFunc = p[1];
                return;

            case "rgbgen":
                s.RgbGen = ParseColorGen(p);
                return;

            case "alphagen":
                s.AlphaGen = ParseColorGen(p);
                return;

            case "tcgen":
            case "texgen":
                s.TcGen = ParseTcGen(p);
                return;

            case "tcmod":
                ParseTcMod(s, p, onWarning);
                return;

            case "depthwrite":
                s.DepthWrite = true;
                return;

            case "depthfunc":
                if (p.Count >= 2) s.DepthFunc = p[1];
                return;

            case "detail":
                s.Detail = true;
                return;

            // Recognized-but-ignored stage tokens (no effect on our model): alphamap, etc. Drop silently.
            default:
                // Stages have no raw bag in DP; unknown stage directives are non-load-bearing. Swallow.
                return;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Field parsers.
    // ---------------------------------------------------------------------------------------------

    private static void ParseBlendFunc(ShaderStage s, List<string> p)
    {
        s.HasBlendFunc = true;
        if (p.Count == 2)
        {
            // Shorthand forms.
            s.BlendRaw = new[] { p[1] };
            switch (p[1].ToLowerInvariant())
            {
                case "add":
                    s.BlendSrc = BlendFactor.One; s.BlendDst = BlendFactor.One; break;
                case "addalpha":
                    s.BlendSrc = BlendFactor.SrcAlpha; s.BlendDst = BlendFactor.One; break;
                case "filter":
                    s.BlendSrc = BlendFactor.DstColor; s.BlendDst = BlendFactor.Zero; break;
                case "blend":
                    s.BlendSrc = BlendFactor.SrcAlpha; s.BlendDst = BlendFactor.OneMinusSrcAlpha; break;
                default:
                    // Unknown shorthand: leave factors unset; classification will be Custom.
                    s.HasBlendFunc = false;
                    break;
            }
        }
        else if (p.Count >= 3)
        {
            s.BlendRaw = new[] { p[1], p[2] };
            s.BlendSrc = ParseBlendFactor(p[1]);
            s.BlendDst = ParseBlendFactor(p[2]);
        }
        else
        {
            s.HasBlendFunc = false;
        }
    }

    private static BlendFactor ParseBlendFactor(string tok) => tok.ToUpperInvariant() switch
    {
        "GL_ONE" => BlendFactor.One,
        "GL_ZERO" => BlendFactor.Zero,
        "GL_SRC_COLOR" => BlendFactor.SrcColor,
        "GL_ONE_MINUS_SRC_COLOR" => BlendFactor.OneMinusSrcColor,
        "GL_SRC_ALPHA" => BlendFactor.SrcAlpha,
        "GL_ONE_MINUS_SRC_ALPHA" => BlendFactor.OneMinusSrcAlpha,
        "GL_DST_COLOR" => BlendFactor.DstColor,
        "GL_ONE_MINUS_DST_COLOR" => BlendFactor.OneMinusDstColor,
        "GL_DST_ALPHA" => BlendFactor.DstAlpha,
        "GL_ONE_MINUS_DST_ALPHA" => BlendFactor.OneMinusDstAlpha,
        _ => BlendFactor.One, // DP defaults an unparsable factor to GL_ONE.
    };

    private static BlendMode ClassifyBlend(BlendFactor src, BlendFactor dst, bool hasBlend)
    {
        if (!hasBlend)
            return BlendMode.Opaque;
        // GL_ONE GL_ZERO == opaque overwrite.
        if (src == BlendFactor.One && dst == BlendFactor.Zero) return BlendMode.Opaque;
        if (src == BlendFactor.One && dst == BlendFactor.One) return BlendMode.Add;
        if (src == BlendFactor.SrcAlpha && dst == BlendFactor.OneMinusSrcAlpha) return BlendMode.Blend;
        // Modulate: dst*src either way around.
        if (src == BlendFactor.DstColor && dst == BlendFactor.Zero) return BlendMode.Filter;
        if (src == BlendFactor.Zero && dst == BlendFactor.SrcColor) return BlendMode.Filter;
        return BlendMode.Custom;
    }

    private static ColorGen ParseColorGen(List<string> p)
    {
        // p = [rgbgen|alphagen, mode, parm...]   (mode may be missing in malformed input)
        if (p.Count < 2)
            return new ColorGen { Type = ColorGenType.Unset };

        string mode = p[1].ToLowerInvariant();
        if (mode == "wave")
        {
            // rgbGen wave <func> <base> <amp> <phase> <freq>
            var wave = ParseWave(p, funcIndex: 2);
            return new ColorGen { Type = ColorGenType.Wave, Wave = wave };
        }

        var parms = FloatsFrom(p, 2);
        ColorGenType t = mode switch
        {
            "identity" => ColorGenType.Identity,
            "identitylighting" => ColorGenType.IdentityLighting,
            "const" => ColorGenType.Const,
            "entity" => ColorGenType.Entity,
            "oneminusentity" => ColorGenType.OneMinusEntity,
            "vertex" => ColorGenType.Vertex,
            "exactvertex" => ColorGenType.ExactVertex,
            "oneminusvertex" => ColorGenType.OneMinusVertex,
            "lightingdiffuse" => ColorGenType.LightingDiffuse,
            "lightingspecular" => ColorGenType.LightingSpecular,
            "portal" => ColorGenType.Portal,
            _ => ColorGenType.Unset,
        };
        return new ColorGen { Type = t, Parms = parms };
    }

    private static TcGen ParseTcGen(List<string> p)
    {
        if (p.Count < 2)
            return new TcGen { Type = TcGenType.Unset };
        var parms = FloatsFrom(p, 2);
        TcGenType t = p[1].ToLowerInvariant() switch
        {
            "base" => TcGenType.Texture,
            "texture" => TcGenType.Texture,
            "lightmap" => TcGenType.Lightmap,
            "environment" => TcGenType.Environment,
            "vector" => TcGenType.Vector,
            _ => TcGenType.Unset,
        };
        return new TcGen { Type = t, Parms = parms };
    }

    private static void ParseTcMod(ShaderStage s, List<string> p, Action<string>? onWarning)
    {
        if (p.Count < 2)
            return;
        string mode = p[1].ToLowerInvariant();

        if (mode == "stretch")
        {
            // tcMod stretch <func> <base> <amp> <phase> <freq>
            var wave = ParseWave(p, funcIndex: 2);
            s.TcMods.Add(new TcMod { Type = TcModType.Stretch, RawType = p[1], Wave = wave });
            return;
        }

        var parms = FloatsFrom(p, 2);
        TcModType? t = mode switch
        {
            "scale" => TcModType.Scale,
            "scroll" => TcModType.Scroll,
            "rotate" => TcModType.Rotate,
            "turb" => TcModType.Turb,
            "transform" => TcModType.Transform,
            "entitytranslate" => TcModType.EntityTranslate,
            "page" => TcModType.Page,
            _ => null,
        };
        if (t == null)
        {
            onWarning?.Invoke($"Q3Shader: unknown tcMod '{mode}'.");
            return;
        }
        s.TcMods.Add(new TcMod { Type = t.Value, RawType = p[1], Parms = parms });
    }

    private static void ParseDeform(ShaderDef def, List<string> p, Action<string>? onWarning)
    {
        if (p.Count < 2)
            return;
        string mode = p[1].ToLowerInvariant();

        switch (mode)
        {
            case "wave":
            {
                // deformVertexes wave <div> <func> <base> <amp> <phase> <freq>
                float div = p.Count >= 3 ? F(p[2]) : 0f;
                var wave = ParseWave(p, funcIndex: 3);
                def.Deforms.Add(new DeformVertexes
                {
                    Type = DeformType.Wave, RawType = p[1], Parms = new[] { div }, Wave = wave,
                });
                return;
            }
            case "move":
            {
                // deformVertexes move <x> <y> <z> <func> <base> <amp> <phase> <freq>
                float x = p.Count >= 3 ? F(p[2]) : 0f;
                float y = p.Count >= 4 ? F(p[3]) : 0f;
                float z = p.Count >= 5 ? F(p[4]) : 0f;
                var wave = ParseWave(p, funcIndex: 5);
                def.Deforms.Add(new DeformVertexes
                {
                    Type = DeformType.Move, RawType = p[1], Parms = new[] { x, y, z }, Wave = wave,
                });
                return;
            }
            default:
            {
                DeformType? t = mode switch
                {
                    "normal" => DeformType.Normal,
                    "bulge" => DeformType.Bulge,
                    "autosprite" => DeformType.Autosprite,
                    "autosprite2" => DeformType.Autosprite2,
                    "projectionshadow" => DeformType.ProjectionShadow,
                    "text0" => DeformType.Text0,
                    "text1" => DeformType.Text1,
                    "text2" => DeformType.Text2,
                    "text3" => DeformType.Text3,
                    "text4" => DeformType.Text4,
                    "text5" => DeformType.Text5,
                    "text6" => DeformType.Text6,
                    "text7" => DeformType.Text7,
                    _ => null,
                };
                if (t == null)
                {
                    onWarning?.Invoke($"Q3Shader: unknown deformVertexes '{mode}'.");
                    return;
                }
                def.Deforms.Add(new DeformVertexes { Type = t.Value, RawType = p[1], Parms = FloatsFrom(p, 2) });
                return;
            }
        }
    }

    private static void ParseSkyParms(ShaderDef def, List<string> p)
    {
        // skyParms <farbox> <cloudheight> <nearbox> ; '-' means none. DP forces the sky surfaceparm.
        def.SurfaceParms.Add("sky");
        string? far = p.Count >= 2 ? Dash(p[1]) : null;
        string? cloud = p.Count >= 3 ? Dash(p[2]) : null;
        string? near = p.Count >= 4 ? Dash(p[3]) : null;
        def.SkyParms = new SkyParms { FarBox = far, CloudHeight = cloud, NearBox = near };
    }

    private static void ParseFogParms(ShaderDef def, List<string> p)
    {
        // fogParms ( r g b ) <distance>  — parentheses are separate tokens in Q3; collect the numbers.
        var nums = new List<float>();
        for (int i = 1; i < p.Count && nums.Count < 4; i++)
        {
            string t = p[i];
            if (t == "(" || t == ")")
                continue;
            // Some files write "(0" or "0)" as glued tokens; strip stray parens.
            t = t.Trim('(', ')');
            if (t.Length == 0)
                continue;
            if (TryF(t, out float v))
                nums.Add(v);
        }
        if (nums.Count >= 4)
            def.FogParms = new FogParms { Red = nums[0], Green = nums[1], Blue = nums[2], Distance = nums[3] };
        else if (nums.Count == 3)
            def.FogParms = new FogParms { Red = nums[0], Green = nums[1], Blue = nums[2], Distance = 0f };
    }

    private static void ParseOffsetMapping(ShaderDef def, List<string> p)
    {
        // dpoffsetmapping <mode> [scale] [bias|match|match8|match16 <value>]
        string mode = p.Count >= 2 ? p[1] : "-";
        float? scale = p.Count >= 3 && TryF(p[2], out float sc) ? sc : (float?)null;
        string? biasKind = null;
        float? biasValue = null;
        if (p.Count >= 5)
        {
            biasKind = p[3];
            if (TryF(p[4], out float bv))
                biasValue = bv;
        }
        def.Dp.OffsetMapping = new OffsetMapping
        {
            Mode = mode, Scale = scale, BiasKind = biasKind, BiasValue = biasValue,
        };
    }

    private static CullMode ParseCull(string arg) => arg.ToLowerInvariant() switch
    {
        "none" => CullMode.None,
        "disable" => CullMode.None,
        "twosided" => CullMode.None,
        "back" => CullMode.Back,
        "backside" => CullMode.Back,
        "backsided" => CullMode.Back,
        "front" => CullMode.Front,
        "" => CullMode.Front,
        _ => CullMode.Front,
    };

    /// <summary>Resolves a Q3 <c>sort</c> key (named or integer) to its conventional priority.</summary>
    private static int? ResolveSort(string arg)
    {
        switch (arg.ToLowerInvariant())
        {
            case "portal": return 1;
            case "sky": return 2;
            case "opaque": return 3;
            case "decal": return 4;
            case "seethrough": return 5;
            case "banner": return 6;
            case "underwater": return 8;
            case "additive": return 9;
            case "nearest": return 16;
            default:
                return int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : (int?)null;
        }
    }

    /// <summary>Parses a waveform <c>&lt;func&gt; &lt;base&gt; &lt;amp&gt; &lt;phase&gt; &lt;freq&gt;</c> starting at <paramref name="funcIndex"/>.</summary>
    private static WaveForm ParseWave(List<string> p, int funcIndex)
    {
        string raw = funcIndex < p.Count ? p[funcIndex] : "sin";
        WaveFunc func = ParseWaveFunc(raw);
        var parms = FloatsFrom(p, funcIndex + 1);
        return new WaveForm { Func = func, RawName = raw, Parms = parms };
    }

    private static WaveFunc ParseWaveFunc(string s)
    {
        // DP supports an optional "user<n>" prefix; fold it onto the base function name.
        string name = s;
        if (name.Length >= 4 && name.StartsWith("user", StringComparison.OrdinalIgnoreCase))
        {
            // Skip "user" and one following digit if present.
            int idx = 4;
            if (idx < name.Length && char.IsDigit(name[idx]))
                idx++;
            name = name.Substring(idx);
        }
        return name.ToLowerInvariant() switch
        {
            "sin" => WaveFunc.Sin,
            "square" => WaveFunc.Square,
            "triangle" => WaveFunc.Triangle,
            "sawtooth" => WaveFunc.Sawtooth,
            "inversesawtooth" => WaveFunc.InverseSawtooth,
            "noise" => WaveFunc.Noise,
            "none" => WaveFunc.None,
            _ => WaveFunc.None,
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Numeric helpers (invariant culture; never throw — bad numbers parse to 0).
    // ---------------------------------------------------------------------------------------------

    private static float F(string s) => TryF(s, out float v) ? v : 0f;

    private static bool TryF(string s, out float v) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static float[] FloatsFrom(List<string> p, int start)
    {
        if (start >= p.Count)
            return Array.Empty<float>();
        var arr = new float[p.Count - start];
        for (int i = start; i < p.Count; i++)
            arr[i - start] = F(p[i]);
        return arr;
    }

    private static string? Dash(string s) => string.Equals(s, "-", StringComparison.Ordinal) ? null : s;

    // ---------------------------------------------------------------------------------------------
    // Tokenizer — mirrors Darkplaces COM_ParseToken_QuakeC.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A cursor over the shader text yielding tokens. Mirrors DP's <c>COM_ParseToken_QuakeC</c>:
    /// skips <c>//</c> and <c>/* */</c> comments and whitespace; treats <c>"..."</c> as one token,
    /// <c>{</c>/<c>}</c>/<c>(</c>/<c>)</c> as single-character tokens; and, when
    /// <c>returnNewline</c> is set, emits a <c>"\n"</c> sentinel at end of each logical line so directive
    /// lines can be delimited. A one-token push-back buffer supports the brace/line interplay.
    /// </summary>
    private sealed class Tokenizer
    {
        private readonly string _s;
        private int _pos;
        private string? _pushed;
        private bool _pushedWasNewline;

        public Tokenizer(string s) => _s = s ?? string.Empty;

        public void PushBack(string token)
        {
            _pushed = token;
            _pushedWasNewline = token == "\n";
        }

        /// <summary>
        /// Reads the next token. When <paramref name="returnNewline"/> is true, a logical end-of-line is
        /// reported as the token <c>"\n"</c>. Returns false at end of input.
        /// </summary>
        public bool Next(bool returnNewline, out string token, out bool quoted)
        {
            if (_pushed != null)
            {
                token = _pushed;
                quoted = false;
                bool wasNl = _pushedWasNewline;
                _pushed = null;
                _pushedWasNewline = false;
                // A pushed-back newline is only meaningful when the caller wants newlines.
                if (wasNl && !returnNewline)
                    return Next(returnNewline, out token, out quoted);
                return true;
            }

            quoted = false;
            int n = _s.Length;

            while (true)
            {
                // Skip spaces/tabs (and, when not returning newlines, line breaks too).
                while (_pos < n && IsInlineSpace(_s[_pos]))
                    _pos++;

                // Newline handling.
                if (_pos < n && (_s[_pos] == '\n' || _s[_pos] == '\r'))
                {
                    if (returnNewline)
                    {
                        // Consume a single CR/LF/CRLF and report one newline token.
                        if (_s[_pos] == '\r' && _pos + 1 < n && _s[_pos + 1] == '\n')
                            _pos += 2;
                        else
                            _pos++;
                        token = "\n";
                        return true;
                    }
                    _pos++; // swallow newline when not significant
                    continue;
                }

                if (_pos >= n)
                {
                    token = string.Empty;
                    return false;
                }

                // Comments.
                if (_s[_pos] == '/' && _pos + 1 < n && _s[_pos + 1] == '/')
                {
                    _pos += 2;
                    while (_pos < n && _s[_pos] != '\n' && _s[_pos] != '\r')
                        _pos++;
                    continue; // a newline (or EOF) follows; loop will handle it
                }
                if (_s[_pos] == '/' && _pos + 1 < n && _s[_pos + 1] == '*')
                {
                    _pos += 2;
                    while (_pos + 1 < n && !(_s[_pos] == '*' && _s[_pos + 1] == '/'))
                        _pos++;
                    _pos = Math.Min(_pos + 2, n); // consume closing */ (or run to EOF)
                    continue;
                }

                break; // positioned at the start of a real token
            }

            // Quoted string (single token; no newline inside).
            if (_s[_pos] == '"')
            {
                _pos++;
                int start = _pos;
                while (_pos < n && _s[_pos] != '"' && _s[_pos] != '\n' && _s[_pos] != '\r')
                    _pos++;
                token = _s.Substring(start, _pos - start);
                if (_pos < n && _s[_pos] == '"') _pos++;
                quoted = true;
                return true;
            }

            // Structural single-character tokens.
            char c = _s[_pos];
            if (c == '{' || c == '}' || c == '(' || c == ')')
            {
                token = c.ToString();
                _pos++;
                return true;
            }

            // Bare word: read until whitespace, a comment start, or a structural char.
            int wstart = _pos;
            while (_pos < n)
            {
                char d = _s[_pos];
                if (IsInlineSpace(d) || d == '\n' || d == '\r')
                    break;
                if (d == '{' || d == '}' || d == '(' || d == ')' || d == '"')
                    break;
                if (d == '/' && _pos + 1 < n && (_s[_pos + 1] == '/' || _s[_pos + 1] == '*'))
                    break;
                _pos++;
            }
            token = _s.Substring(wstart, _pos - wstart);
            return true;
        }

        private static bool IsInlineSpace(char c) => c == ' ' || c == '\t' || c == '\f' || c == '\v';
    }
}
