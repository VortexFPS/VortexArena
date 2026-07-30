using System;
using System.Collections.Generic;
using System.Globalization;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Engine.Effects;

// =====================================================================================================
//  Parsed effectinfo.txt data model — the C# successor to Darkplaces' particleeffectinfo_t and its
//  pt_/PBLEND_/PARTICLE_ enums (Base/darkplaces/cl_particles.c). Extracted to the Engine library so
//  the xUnit test project can exercise the parser without a Godot runtime.
//
//  The parse entry points live in EffectInfoParser; the I/O (VFS/disk loading) stays in the Godot
//  host (game/client/EffectInfo.cs) and delegates its inner loop here.
//
//  Numeric conventions are kept identical to DP so the values from the file map without re-tuning:
//   * color is hex RRGGBB; a particle picks a random lerp between Color[0] and Color[1].
//   * size  = {min, max, sizeincrease}: world-unit half-size chosen in [min,max]; sizeincrease is
//     units/sec growth (negative shrinks).  (particleeffectinfo_t.size[3])
//   * alpha = {min, max, alphafade}: opacity 0..256 (256 = fully opaque) chosen in [min,max];
//     alphafade is alpha-units faded per second.  (particleeffectinfo_t.alpha[3])
//   * time  = {min,max} lifetime seconds; if 0 DP derives life = alpha/min(1,alphafade).
//   * gravity is a multiplier on world gravity (DP sv_gravity, default 800); negative floats up.
//   * originjitter/velocityjitter add jitter*VectorRandom() (uniform unit sphere) per axis at spawn.
//   * velocitymultiplier scales the supplied emit velocity; velocityoffset adds a constant.
//   * count is countmultiplier (particles per requested count); countabsolute is an unconditional add.
// =====================================================================================================

/// <summary>DP particle kind (ptype_t). Drives default blend/orientation and a few behaviours.</summary>
public enum EiType
{
    AlphaStatic,  // pt_alphastatic — alpha-blended billboard (smoke-like, the baseline)
    Static,       // pt_static      — additive billboard (fire/energy)
    Spark,        // pt_spark       — velocity-stretched streak
    Beam,         // pt_beam        — oriented beam (HBEAM), only drawn as a trail
    Rain,         // pt_rain
    RainDecal,    // pt_raindecal
    Snow,         // pt_snow
    Bubble,       // pt_bubble      — underwater bubble
    Blood,        // pt_blood       — invmod blood spark; gravity forced to 1 by DP
    Smoke,        // pt_smoke       — additive billboard puff
    Decal,        // pt_decal       — projected onto the hit surface (invmod), not a free particle
    EntityParticle,
}

/// <summary>DP blend mode (pblend_t).</summary>
public enum EiBlend
{
    Alpha,   // PBLEND_ALPHA  — standard src-alpha
    Add,     // PBLEND_ADD    — additive (glow)
    InvMod,  // PBLEND_INVMOD — inverse-modulate (darkening; blood/decals)
}

/// <summary>DP orientation (porientation_t) — billboard vs spark-stretch vs beam.</summary>
public enum EiOrientation
{
    Billboard, // PARTICLE_BILLBOARD
    Spark,     // PARTICLE_SPARK (velocity-stretched)
    Oriented,  // PARTICLE_ORIENTED_DOUBLESIDED (decals)
    Beam,      // PARTICLE_HBEAM/VBEAM
}

/// <summary>
/// One parsed emitter — the C# mirror of <c>particleeffectinfo_t</c> with the same field set and
/// defaults (<c>baselineparticleeffectinfo</c>). Mutable during parse; consumed read-only at spawn.
/// </summary>
public sealed class EffectInfoEmitter
{
    // --- counts ---
    public float CountAbsolute;     // countabsolute
    public float CountMultiplier;   // count (or 1/trailspacing for trails)
    public float TrailSpacing;      // trailspacing (>0 => this block is a trail emitter)

    // --- kind / blend / orientation ---
    public EiType Type = EiType.AlphaStatic;
    public EiBlend Blend = EiBlend.Alpha;
    public EiOrientation Orientation = EiOrientation.Billboard;

    // --- color (hex RRGGBB pair) ---
    public uint Color0 = 0xFFFFFF;
    public uint Color1 = 0xFFFFFF;

    // --- texture range (atlas indices into the DP particlefont) — the sprite drawn for each particle ---
    public int Tex0 = 63;
    public int Tex1 = 63;

    // --- stain (the decal/stainmap a particle leaves where it hits a surface: blood splat, scorch) -------
    // DP particleeffectinfo_t.staintex[2]/staincolor[2]/stainsize[2]/stainalpha[2]. A pt_blood particle
    // that hits the world leaves a tex_blooddecal mark tinted by staincolor (cl_particles.c:3020-3041);
    // explosion/impact blocks declare these so the splat reuses the dedicated blood/scorch decal sprites.
    public int StainTex0 = -1, StainTex1 = -1;     // < 0 => no stain declared
    // DP baseline staincolor = {(unsigned int)-1, (unsigned int)-1} (cl_particles.c:271). staincolor is a
    // MODDING FACTOR on the particle's own colour (0x808080 = neutral), NOT an absolute tint; the sim casts
    // these to int and the -1 (== 0xFFFFFFFF) default takes the "stain = particle colour" shorthand branch
    // (cl_particles.c:740-764). Defaulting 0xFFFFFF (a positive int) would wrongly enter the modding branch.
    public uint StainColor0 = 0xFFFFFFFF, StainColor1 = 0xFFFFFFFF;
    // DP baseline stainsize = {2, 2} (cl_particles.c:274). 22 of the 30 staintex blocks in the shipped
    // effectinfo.txt omit stainsize and inherit this baseline; defaulting 1 would halve their splat size.
    public float StainSizeMin = 2f, StainSizeMax = 2f;
    public float StainAlphaMin = 1f, StainAlphaMax = 1f; // DP stainalpha is 0..1 (already normalized)

    /// <summary>True if this emitter declares a stain texture range (staintex tex0 tex1, tex1 exclusive).</summary>
    public bool HasStain => StainTex0 >= 0 && StainTex1 > StainTex0;

    // --- size {min,max,sizeincrease} / alpha {min,max,alphafade} / time {min,max} ---
    public float SizeMin = 1f, SizeMax = 1f, SizeIncrease;
    public float AlphaMin, AlphaMax = 256f, AlphaFade = 256f;
    public float TimeMin = 16777216f, TimeMax = 16777216f;

    // --- physics ---
    public float Gravity;        // multiplier on world gravity (negative floats up)
    public float Bounce;         // <0 removes on impact (blood splat)
    public float AirFriction;
    public float LiquidFriction;
    public float StretchFactor = 1f;
    public float VelocityMultiplier;

    // --- offsets / jitter (3-vectors) ---
    public NVec3 OriginOffset;
    public NVec3 RelativeOriginOffset;
    public NVec3 VelocityOffset;
    public NVec3 RelativeVelocityOffset;
    public NVec3 OriginJitter;
    public NVec3 VelocityJitter;

    // --- rotation {baseMin,baseMax,spinMin,spinMax} degrees & deg/sec ---
    public float RotateBaseMin, RotateBaseMax = 360f, RotateSpinMin, RotateSpinMax;

    // --- dlight ---
    public float LightRadius;        // lightradius (lightradiusstart)
    public float LightRadiusFade;    // lightradiusfade
    public float LightTime = 16777216f;
    public NVec3 LightColor = new(1f, 1f, 1f);

    // --- water gating (PARTICLEEFFECT_UNDERWATER / NOTUNDERWATER) ---
    public bool Underwater;
    public bool NotUnderwater;

    /// <summary>True if any of this block's lines actually set a parameter (DEFINED flag). Undefined
    /// placeholder blocks (an <c>effect</c> header with no following keywords) are skipped at spawn.</summary>
    public bool Defined;

    /// <summary>
    /// Persistent fractional particle accumulator — the C# mirror of <c>particleeffectinfo_t.particleaccumulator</c>
    /// (Base/darkplaces/cl_particles.c:55-57). Blood and sub-1-count effects spawn well under one particle per
    /// call; DP keeps the fraction here ACROSS calls and only emits whole particles, draining the remainder. The
    /// spawn loop adds this call's <c>cnt</c>, takes the integer part, and leaves the rest for next time — so
    /// e.g. <c>count 0.025</c> yields ~1 particle every ~40 calls instead of over-spawning each call.
    /// </summary>
    public double ParticleAccumulator;

    /// <summary>This block describes a trail emitter (trailspacing &gt; 0) or a beam (only valid as a trail).</summary>
    public bool IsTrailEmitter => TrailSpacing > 0f || Orientation == EiOrientation.Beam;

    // ------------------------------------------------------------------------------------------------
    //  Convenience accessors used by the Godot burst builder (decode the DP packed values)
    // ------------------------------------------------------------------------------------------------

    /// <summary>Midpoint of the color range as linear 0..1 RGB (the representative tint for a Godot burst).</summary>
    public (float R, float G, float B) MidColor()
    {
        (float r0, float g0, float b0) = Unpack(Color0);
        (float r1, float g1, float b1) = Unpack(Color1);
        return ((r0 + r1) * 0.5f, (g0 + g1) * 0.5f, (b0 + b1) * 0.5f);
    }

    public (float R, float G, float B) Color0Rgb() => Unpack(Color0);
    public (float R, float G, float B) Color1Rgb() => Unpack(Color1);

    private static (float, float, float) Unpack(uint hex)
        => (((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f);

    /// <summary>Representative initial opacity 0..1 (alpha midpoint / 256, clamped).</summary>
    public float MidAlpha01() => Math.Clamp((AlphaMin + AlphaMax) * 0.5f / 256f, 0f, 1f);

    /// <summary>
    /// The staincolor MODDING FACTOR (not an absolute tint) for the splat decal — DP staincolor scales the
    /// particle's own colour: <c>stain = staincolor * particlecolor / 0x8000</c> with the random lerp factor
    /// <c>l1+l2 = 256</c>, so a component byte <c>s</c> contributes <c>s/128</c> (0x80 = neutral 1.0). The
    /// <c>(unsigned int)-1</c> default (and any negative staincolor) is the "stain = particle colour" shorthand
    /// (cl_particles.c:740-764), i.e. a neutral factor of 1.0. The caller multiplies the particle colour by
    /// this, matching the live faithful sim's per-particle formula.
    /// </summary>
    public (float R, float G, float B) StainMidColor()
    {
        if ((int)StainColor0 < 0 || (int)StainColor1 < 0)
            return (1f, 1f, 1f); // -1 shorthand: stain = particle colour (no modding)
        (float r0, float g0, float b0) = ModFactor(StainColor0);
        (float r1, float g1, float b1) = ModFactor(StainColor1);
        return ((r0 + r1) * 0.5f, (g0 + g1) * 0.5f, (b0 + b1) * 0.5f);
    }

    // staincolor byte -> modding factor (0x80 -> 1.0). Mirrors DP's /0x8000 with the 256 lerp weight.
    private static (float, float, float) ModFactor(uint hex)
        => (((hex >> 16) & 0xFF) / 128f, ((hex >> 8) & 0xFF) / 128f, (hex & 0xFF) / 128f);

    /// <summary>Representative stain decal half-size (size midpoint) and opacity (0..1).</summary>
    public float StainMidSize() => MathF.Max(0.5f, (StainSizeMin + StainSizeMax) * 0.5f);
    public float StainMidAlpha01() => Math.Clamp((StainAlphaMin + StainAlphaMax) * 0.5f, 0f, 1f);

    /// <summary>
    /// The particle's effective <i>visible</i> lifetime in seconds. DP removes a particle when either its
    /// <c>time</c> elapses OR its alpha fades to zero (CL_NewParticle: a particle "is also removed if alpha
    /// drops to nothing"). The <c>time</c> field's giant 16777216 default means almost every combat particle
    /// is governed by the alpha fade: it dies in <c>alpha / alphafade</c> seconds. So we take the alpha-fade
    /// duration when no explicit <c>time</c> is set (and the min of the two when both apply), then clamp to a
    /// sane render range. This is why an explosion spark (alpha 256, alphafade 1300) lives ~0.2s, not 6s.
    /// </summary>
    public float Lifetime()
    {
        float midAlpha = MathF.Max(1f, (AlphaMin + AlphaMax) * 0.5f);
        bool explicitTime = TimeMin < 16777216f || TimeMax < 16777216f;
        float timeLife = explicitTime ? (TimeMin + TimeMax) * 0.5f : float.PositiveInfinity;
        float fadeLife = AlphaFade > 0.0001f ? midAlpha / AlphaFade : float.PositiveInfinity;

        float life = MathF.Min(timeLife, fadeLife);
        if (float.IsInfinity(life))
            life = 1f; // neither bound set (e.g. an everlasting static): use a short default for a one-shot.
        return Math.Clamp(life, 0.05f, 6f);
    }
}

/// <summary>
/// The effectinfo.txt parse core — the Godot-free portion of the C# successor to Darkplaces'
/// <c>CL_Particles_ParseEffectInfo</c> (Base/darkplaces/cl_particles.c). Extracted here so the xUnit
/// test project can exercise the tokeniser, keyword table and baseline defaults without a Godot runtime.
/// The I/O wrapper (VFS/disk loading and the <c>XonoticGodot.Game.Client.EffectInfo</c> catalog object)
/// stays in the Godot host and delegates its inner loop here.
///
/// Callers: <c>XonoticGodot.Game.Client.EffectInfo.Parse</c> (the live catalog), and directly from the
/// xUnit test suite.
/// </summary>
public static class EffectInfoParser
{
    /// <summary>
    /// Parse raw effectinfo text and populate <paramref name="byName"/>. Multiple same-named blocks
    /// accumulate (layer) into one list entry, exactly like DP. The dictionary is NOT cleared first so
    /// callers can do incremental / overlay parses; call <c>byName.Clear()</c> before a full reload.
    /// Returns the number of distinct effect names added (a name not already present in the dictionary).
    /// </summary>
    public static int Parse(
        string text,
        Dictionary<string, List<EffectInfoEmitter>> byName)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        EffectInfoEmitter? info = null;
        List<EffectInfoEmitter>? currentList = null;
        int added = 0;

        foreach (string[] argv in Tokenize(text))
        {
            if (argv.Length < 1)
                continue;
            string cmd = argv[0];

            if (cmd == "effect")
            {
                if (argv.Length != 2)
                    continue;
                string name = argv[1];
                bool isNew = !byName.ContainsKey(name);
                if (!byName.TryGetValue(name, out currentList))
                {
                    currentList = new List<EffectInfoEmitter>();
                    byName[name] = currentList;
                }
                if (isNew) added++;
                info = new EffectInfoEmitter();
                currentList.Add(info);
                continue;
            }

            if (info is null)
                continue; // command before any effect — skip (DP errors out; we're lenient)

            info.Defined = true;
            ApplyKeyword(info, cmd, argv);
        }

        return added;
    }

    /// <summary>
    /// Apply one keyword line to <paramref name="info"/>. Public so tests can synthesise a single keyword
    /// application without a full parse.
    /// </summary>
    public static void ApplyKeyword(EffectInfoEmitter info, string cmd, string[] argv)
    {
        switch (cmd)
        {
            case "countabsolute": info.CountAbsolute = F(argv, 1); break;
            case "count": info.CountMultiplier = F(argv, 1); break;
            case "trailspacing":
                info.TrailSpacing = F(argv, 1);
                if (info.TrailSpacing > 0f) info.CountMultiplier = 1f / info.TrailSpacing;
                break;

            case "type":
                info.Type = ParseType(Arg(argv, 1));
                (info.Blend, info.Orientation) = DefaultsFor(info.Type);
                if (info.Type == EiType.Blood) info.Gravity = 1f; // DP forces gravity on pt_blood
                break;
            case "blend": info.Blend = ParseBlend(Arg(argv, 1)); break;
            case "orientation": info.Orientation = ParseOrientation(Arg(argv, 1)); break;

            case "color":
                info.Color0 = H(argv, 1, info.Color0);
                info.Color1 = H(argv, 2, info.Color1);
                break;
            case "tex":
                info.Tex0 = (int)F(argv, 1);
                info.Tex1 = (int)F(argv, 2);
                break;

            case "size":
                info.SizeMin = F(argv, 1);
                info.SizeMax = F(argv, 2);
                break;
            case "sizeincrease": info.SizeIncrease = F(argv, 1); break;
            case "alpha":
                info.AlphaMin = F(argv, 1);
                info.AlphaMax = F(argv, 2);
                info.AlphaFade = F(argv, 3);
                break;
            case "time":
                info.TimeMin = F(argv, 1);
                info.TimeMax = F(argv, 2);
                break;

            case "gravity": info.Gravity = F(argv, 1); break;
            case "bounce": info.Bounce = F(argv, 1); break;
            case "airfriction": info.AirFriction = F(argv, 1); break;
            case "liquidfriction": info.LiquidFriction = F(argv, 1); break;
            case "stretchfactor": info.StretchFactor = F(argv, 1); break;
            case "velocitymultiplier": info.VelocityMultiplier = F(argv, 1); break;

            case "originoffset": info.OriginOffset = V(argv); break;
            case "relativeoriginoffset": info.RelativeOriginOffset = V(argv); break;
            case "velocityoffset": info.VelocityOffset = V(argv); break;
            case "relativevelocityoffset": info.RelativeVelocityOffset = V(argv); break;
            case "originjitter": info.OriginJitter = V(argv); break;
            case "velocityjitter": info.VelocityJitter = V(argv); break;

            case "rotate":
                info.RotateBaseMin = F(argv, 1);
                info.RotateBaseMax = F(argv, 2);
                info.RotateSpinMin = F(argv, 3);
                info.RotateSpinMax = F(argv, 4);
                break;

            case "lightradius": info.LightRadius = F(argv, 1); break;
            case "lightradiusfade": info.LightRadiusFade = F(argv, 1); break;
            case "lighttime": info.LightTime = F(argv, 1); break;
            case "lightcolor": info.LightColor = V(argv); break;

            case "underwater": info.Underwater = true; break;
            case "notunderwater": info.NotUnderwater = true; break;

            // Stains — the decal a particle leaves where it hits a surface (blood splat / scorch). Stored and
            // consumed by the blood-splat decal path; staintex/staincolor index the same particlefont atlas.
            case "staintex":
                info.StainTex0 = (int)F(argv, 1);
                info.StainTex1 = (int)F(argv, 2);
                break;
            case "staincolor":
                info.StainColor0 = H(argv, 1, info.StainColor0);
                info.StainColor1 = H(argv, 2, info.StainColor1);
                break;
            case "stainsize":
                info.StainSizeMin = F(argv, 1);
                info.StainSizeMax = F(argv, 2);
                break;
            case "stainalpha":
                info.StainAlphaMin = F(argv, 1);
                info.StainAlphaMax = F(argv, 2);
                break;

            // `stainless` DISABLES the stain (DP cl_particles.c:468): it stamps staintex[0]=staintex[1]=-2 and
            // resets staincolor/stainalpha/stainsize back to the baseline, so a block can suppress a stain. The -2
            // sentinel (like -1) reads as "no stain" everywhere downstream (HasStain stays false; the faithful sim
            // gates on staintex>=0), the difference from -1 being intent: -2 = explicitly stainless, -1 = none
            // declared. 0 occurrences in the shipped effectinfo.txt today, but we mirror DP so the keyword is faithful.
            case "stainless":
                info.StainTex0 = -2;
                info.StainTex1 = -2;
                info.StainColor0 = 0xFFFFFFFF;
                info.StainColor1 = 0xFFFFFFFF;
                info.StainAlphaMin = 1f;
                info.StainAlphaMax = 1f;
                info.StainSizeMin = 2f;
                info.StainSizeMax = 2f;
                break;

            // Recognised-but-unmodelled keywords (coronas, cubemaps, shadows, nearest): accepted so they don't
            // trip the "unknown command" path, but they have no visual analogue in the Godot port.
            case "lightshadow":
            case "lightcubemapnum":
            case "lightcorona":
            case "forcenearest":
                break;

            default:
                break; // unknown keyword — ignore (DP prints a warning; we stay quiet)
        }
    }

    /// <summary>Per-type default blend + orientation, matching DP's <c>particletype[]</c> table.</summary>
    public static (EiBlend, EiOrientation) DefaultsFor(EiType t) => t switch
    {
        EiType.AlphaStatic => (EiBlend.Alpha, EiOrientation.Billboard),
        EiType.Static => (EiBlend.Add, EiOrientation.Billboard),
        EiType.Spark => (EiBlend.Add, EiOrientation.Spark),
        EiType.Beam => (EiBlend.Add, EiOrientation.Beam),
        EiType.Rain => (EiBlend.Add, EiOrientation.Spark),
        // DP cl_particles.c:36 pt_raindecal = {PBLEND_ADD, PARTICLE_ORIENTED_DOUBLESIDED}.
        EiType.RainDecal => (EiBlend.Add, EiOrientation.Oriented),
        EiType.Snow => (EiBlend.Add, EiOrientation.Billboard),
        EiType.Bubble => (EiBlend.Add, EiOrientation.Billboard),
        // DP cl_particles.c:39 pt_blood = {PBLEND_INVMOD, PARTICLE_BILLBOARD}.
        EiType.Blood => (EiBlend.InvMod, EiOrientation.Billboard),
        EiType.Smoke => (EiBlend.Add, EiOrientation.Billboard),
        EiType.Decal => (EiBlend.InvMod, EiOrientation.Oriented),
        EiType.EntityParticle => (EiBlend.Alpha, EiOrientation.Billboard),
        _ => (EiBlend.Alpha, EiOrientation.Billboard),
    };

    // ------------------------------------------------------------------------------------------------
    //  Token helpers
    // ------------------------------------------------------------------------------------------------

    private static string Arg(string[] argv, int i) => i < argv.Length ? argv[i] : "";

    private static float F(string[] argv, int i)
    {
        if (i >= argv.Length) return 0f;
        // DP uses atof which stops at the first non-numeric char; TryParse(Invariant) covers the file's
        // "1.500000" / "-3" / "0.400000" forms. Hex (0x..) only appears for color/tex, handled by H().
        return float.TryParse(argv[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
    }

    private static uint H(string[] argv, int i, uint fallback)
    {
        if (i >= argv.Length) return fallback;
        string s = argv[i];
        try
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("0X", StringComparison.Ordinal))
                return Convert.ToUInt32(s.Substring(2), 16);
            // DP's strtol(.,.,0): a bare number is decimal here.
            return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint v) ? v : fallback;
        }
        catch { return fallback; }
    }

    /// <summary>Read a 3-vector from argv[1..3], 0-filling missing components (DP readfloats).</summary>
    private static NVec3 V(string[] argv)
        => new(F(argv, 1), F(argv, 2), F(argv, 3));

    /// <summary>
    /// Tokenise like DP's COM_ParseToken_Simple loop: split on whitespace into argv arrays, with a newline
    /// terminating a command. <c>//</c> starts a line comment; quotes group a token. The file is generated
    /// (no exotic escaping), so a straightforward scanner reproduces it.
    /// </summary>
    public static IEnumerable<string[]> Tokenize(string text)
    {
        var args = new List<string>();
        int i = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i];

            // Newline terminates the current command line.
            if (c == '\n' || c == '\r')
            {
                if (args.Count > 0)
                {
                    yield return args.ToArray();
                    args.Clear();
                }
                i++;
                continue;
            }

            // Whitespace between tokens.
            if (c == ' ' || c == '\t')
            {
                i++;
                continue;
            }

            // Line comment.
            if (c == '/' && i + 1 < n && text[i + 1] == '/')
            {
                while (i < n && text[i] != '\n')
                    i++;
                continue;
            }

            // Quoted token.
            if (c == '"')
            {
                i++;
                int start = i;
                while (i < n && text[i] != '"' && text[i] != '\n')
                    i++;
                args.Add(text.Substring(start, i - start));
                if (i < n && text[i] == '"')
                    i++;
                continue;
            }

            // Bare token: run up to the next whitespace/newline.
            int s = i;
            while (i < n && text[i] != ' ' && text[i] != '\t' && text[i] != '\n' && text[i] != '\r')
                i++;
            args.Add(text.Substring(s, i - s));
        }
        if (args.Count > 0)
            yield return args.ToArray();
    }

    private static EiType ParseType(string s) => s switch
    {
        "alphastatic" => EiType.AlphaStatic,
        "static" => EiType.Static,
        "spark" => EiType.Spark,
        "beam" => EiType.Beam,
        "rain" => EiType.Rain,
        "raindecal" => EiType.RainDecal,
        "snow" => EiType.Snow,
        "bubble" => EiType.Bubble,
        "blood" => EiType.Blood,
        "smoke" => EiType.Smoke,
        "decal" => EiType.Decal,
        "entityparticle" => EiType.EntityParticle,
        _ => EiType.AlphaStatic,
    };

    private static EiBlend ParseBlend(string s) => s switch
    {
        "alpha" => EiBlend.Alpha,
        "add" => EiBlend.Add,
        "invmod" => EiBlend.InvMod,
        _ => EiBlend.Alpha,
    };

    private static EiOrientation ParseOrientation(string s) => s switch
    {
        "billboard" => EiOrientation.Billboard,
        "spark" => EiOrientation.Spark,
        "oriented" => EiOrientation.Oriented,
        "beam" => EiOrientation.Beam,
        _ => EiOrientation.Billboard,
    };
}
