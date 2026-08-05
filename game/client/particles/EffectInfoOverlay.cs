using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using VortexArena.Game.Client;   // EffectInfoEmitter, EiType/EiBlend/EiOrientation (game/client/EffectInfoParticle.cs)
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client.Particles;

// =====================================================================================================
//  EffectInfoOverlay — parser for the PORT-SIDE authoring overlay file `effectinfo_xg.txt`
//  (planning/particles-dual-system.md §D.1). This file NEVER touches the upstream shared effectinfo.txt:
//  it is read separately, so DP/Base content compatibility is preserved. The overlay layers TWO things on
//  top of the parsed effectinfo catalog:
//
//   1. Per-effect routing: `effect <name>` + `xg_style modern|original|auto` + `xg_preset <id>`. This
//      produces an EffectStyleEntry the router consults in mode 1 (cl_particles_modern 1) to decide the
//      backend for that effect.
//
//   2. Optional fallback/modern emitter blocks: the same standard effectinfo keywords (type/color/size/
//      alpha/…), so a modern-only effect (a NEW name not present in effectinfo.txt) can carry the faithful-
//      shaped `EffectInfoEmitter` blocks the modern->original translation needs for mode 0 (§D.2). For an
//      existing effectinfo effect overridden only with xg_style/xg_preset, no body is required.
//
//  Tokeniser + standard-keyword handling MIRROR EffectInfo.cs's CL_Particles_ParseEffectInfo port (the two
//  files share a syntax); the only additions are the `xg_*` directives. We re-implement (rather than reuse)
//  EffectInfo's private ApplyKeyword so the overlay's block bodies parse identically without exposing it.
// =====================================================================================================

/// <summary>
/// Reads and parses <c>effectinfo_xg.txt</c>: the per-effect style overrides plus any overlay-defined
/// fallback/modern emitter blocks. Sourced lazily from the mounted content VFS via <see cref="TextLoader"/>
/// (mirroring <see cref="EffectInfo.TextLoader"/>), with a direct-disk fallback. A miss leaves both maps
/// empty — every effect then falls back to <see cref="EffectStyleEntry.Default"/> (Auto) and no overlay
/// blocks, so the game runs unchanged with no overlay present.
/// </summary>
public sealed class EffectInfoOverlay
{
    /// <summary>The default virtual path the overlay parser reads when no <see cref="TextLoader"/> is supplied.</summary>
    public const string DefaultVPath = "effectinfo_xg.txt";

    // name (lower-cased) -> its authored routing record.
    private readonly Dictionary<string, EffectStyleEntry> _styles =
        new(StringComparer.OrdinalIgnoreCase);

    // name (lower-cased) -> the ordered overlay-defined emitter blocks (fallback/modern bodies).
    private readonly Dictionary<string, List<EffectInfoEmitter>> _blocks =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of distinct effect names the overlay declares (style and/or block body).</summary>
    public int Count => _styles.Count;

    /// <summary>True once a parse populated at least one effect override or block.</summary>
    public bool Loaded { get; private set; }

    /// <summary>
    /// Optional override for sourcing the raw overlay text (e.g. the host's VirtualFileSystem.ReadText).
    /// Given the virtual path, returns the file's text, or null/empty on miss. When unset the parser reads
    /// the file directly from the resolved content directory on disk. Mirrors <see cref="EffectInfo.TextLoader"/>.
    /// </summary>
    public Func<string, string?>? TextLoader { get; set; }

    // ================================================================================================
    //  Lookups
    // ================================================================================================

    /// <summary>The authored routing record for <paramref name="effectName"/>, or
    /// <see cref="EffectStyleEntry.Default"/> (Auto, no preset) when the overlay says nothing about it.</summary>
    public EffectStyleEntry GetStyle(string effectName)
    {
        if (string.IsNullOrEmpty(effectName))
            return EffectStyleEntry.Default;
        return _styles.TryGetValue(effectName, out EffectStyleEntry e) ? e : EffectStyleEntry.Default;
    }

    /// <summary>True if the overlay declares a style and/or block body for <paramref name="effectName"/>.</summary>
    public bool Has(string effectName)
        => !string.IsNullOrEmpty(effectName) &&
           (_styles.ContainsKey(effectName) || _blocks.ContainsKey(effectName));

    /// <summary>
    /// The overlay-defined emitter blocks for <paramref name="effectName"/> (the fallback/modern body), or
    /// false with an empty list when the overlay declared no body for it (style-only override).
    /// </summary>
    public bool TryGetBlocks(string effectName, out IReadOnlyList<EffectInfoEmitter> blocks)
    {
        if (!string.IsNullOrEmpty(effectName) &&
            _blocks.TryGetValue(effectName, out List<EffectInfoEmitter>? list) && list.Count > 0)
        {
            blocks = list;
            return true;
        }
        blocks = Array.Empty<EffectInfoEmitter>();
        return false;
    }

    // ================================================================================================
    //  Loading
    // ================================================================================================

    /// <summary>
    /// Load + parse <c>effectinfo_xg.txt</c>. Tries <see cref="TextLoader"/> first, then a direct disk read
    /// of the content tree. Returns true on success (at least one override/block parsed). A miss is benign —
    /// callers fall back to Auto routing with no overlay blocks.
    /// </summary>
    public bool Load(string vpath = DefaultVPath)
    {
        string? text = TryReadText(vpath);
        if (string.IsNullOrEmpty(text))
            return false;
        Parse(text);
        return Loaded;
    }

    // ================================================================================================
    //  Process-lifetime shared overlay (menu warm) — mirrors EffectInfo.GetShared; see that method for the
    //  rationale, the thread-safety argument and the immutable-after-parse assumption. Worth sharing even
    //  though no overlay ships in data/core.pk3dir today: the MISS is what every match currently repeats
    //  (a VFS probe plus the disk-path fallback walk), and this caches it.
    // ================================================================================================

    private static readonly object SharedGate = new();
    private static readonly Dictionary<string, EffectInfoOverlay> SharedByVPath =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The parsed overlay for <paramref name="vpath"/>, parsed once per process and shared by every
    /// match. Returns an overlay even on a miss (empty, <see cref="Loaded"/> false) — every lookup then
    /// answers the Auto default, exactly as an unparsed overlay does.</summary>
    public static EffectInfoOverlay GetShared(string vpath, Func<string, string?>? textLoader)
    {
        string key = vpath ?? DefaultVPath;
        lock (SharedGate)
        {
            if (SharedByVPath.TryGetValue(key, out EffectInfoOverlay? hit))
                return hit;
        }

        var built = new EffectInfoOverlay { TextLoader = textLoader };
        try { built.Load(key); }
        catch { /* leave empty; Auto everywhere */ }

        lock (SharedGate)
        {
            if (SharedByVPath.TryGetValue(key, out EffectInfoOverlay? raced))
                return raced;
            SharedByVPath[key] = built;
            return built;
        }
    }

    /// <summary>Drop every shared overlay — for a host that remounts content under a running process.</summary>
    public static void ClearShared()
    {
        lock (SharedGate)
            SharedByVPath.Clear();
    }

    private string? TryReadText(string vpath)
    {
        // 1) host-supplied loader (the real VFS).
        if (TextLoader is not null)
        {
            try
            {
                string? t = TextLoader(vpath);
                if (!string.IsNullOrEmpty(t))
                    return t;
            }
            catch { /* fall through to disk */ }
        }

        // 2) direct disk read from the resolved content directory — same convention as EffectInfo so the
        //    in-tree data/ tree is found (res:// rooted at the project dir).
        foreach (string abs in CandidateDiskPaths(vpath))
        {
            try
            {
                if (System.IO.File.Exists(abs))
                    return System.IO.File.ReadAllText(abs);
            }
            catch { /* try next */ }
        }
        return null;
    }

    private static IEnumerable<string> CandidateDiskPaths(string vpath)
    {
        string rel = vpath.Replace('\\', '/').TrimStart('/');
        string[] subPaths = rel.Contains('/')
            ? new[] { rel }
            : new[] { $"xonotic-data.pk3dir/{rel}", rel };

        string projectDir;
        try { projectDir = ProjectSettings.GlobalizePath("res://"); }
        catch { projectDir = AppContext.BaseDirectory; }

        string[] roots =
        {
            System.IO.Path.Combine(projectDir, "assets", "data"),
            projectDir,
        };

        foreach (string root in roots)
            foreach (string sub in subPaths)
            {
                string combined;
                try { combined = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, sub)); }
                catch { continue; }
                yield return combined;
            }
    }

    // ================================================================================================
    //  Parser
    // ================================================================================================

    /// <summary>Parse raw overlay text directly (also the unit-test entry; no I/O).</summary>
    public void Parse(string text)
    {
        _styles.Clear();
        _blocks.Clear();
        Loaded = false;
        if (string.IsNullOrEmpty(text))
            return;

        string? currentName = null;
        EffectInfoEmitter? block = null;        // the current effect's overlay-defined body (lazily created)
        ParticleStyle style = ParticleStyle.Auto;
        string? presetId = null;

        // Commit the accumulated style/preset for the current effect into the style map. Block bodies are
        // committed eagerly as they're created (so multiple same-named overlay blocks layer like DP).
        void CommitStyle()
        {
            if (currentName is null)
                return;
            // Only record a style entry when the overlay actually said something routable about this effect
            // (a style other than Auto, or a preset id) — a body-only block keeps Auto routing implicitly.
            if (style != ParticleStyle.Auto || presetId is not null)
                _styles[currentName] = new EffectStyleEntry(style, presetId);
        }

        foreach (string[] argv in Tokenize(text))
        {
            if (argv.Length < 1)
                continue;
            string cmd = argv[0];

            if (cmd == "effect")
            {
                if (argv.Length != 2)
                    continue;
                CommitStyle();                    // flush the previous effect's routing record
                currentName = argv[1];
                style = ParticleStyle.Auto;
                presetId = null;
                block = null;                     // a fresh body block is created on the first body keyword
                continue;
            }

            if (currentName is null)
                continue;                         // directive before any `effect` — skip (lenient)

            switch (cmd)
            {
                // --- the two xg_* routing directives ---
                case "xg_style":
                    style = ParseStyle(Arg(argv, 1));
                    continue;
                case "xg_preset":
                    presetId = argv.Length > 1 && !string.IsNullOrWhiteSpace(argv[1]) ? argv[1] : null;
                    continue;
            }

            // --- a standard effectinfo body keyword: this effect carries an overlay-defined block ---
            if (block is null)
            {
                block = new EffectInfoEmitter();
                if (!_blocks.TryGetValue(currentName, out List<EffectInfoEmitter>? list))
                {
                    list = new List<EffectInfoEmitter>();
                    _blocks[currentName] = list;
                }
                list.Add(block);
            }
            block.Defined = true;
            ApplyKeyword(block, cmd, argv);
        }

        CommitStyle();                            // flush the final effect

        Loaded = _styles.Count > 0 || _blocks.Count > 0;
    }

    private static ParticleStyle ParseStyle(string s) => s.ToLowerInvariant() switch
    {
        "modern" => ParticleStyle.Modern,
        "original" => ParticleStyle.Original,
        "faithful" => ParticleStyle.Original,   // alias
        "auto" => ParticleStyle.Auto,
        _ => ParticleStyle.Auto,
    };

    // ================================================================================================
    //  Standard effectinfo keyword handling — a faithful mirror of EffectInfo.ApplyKeyword so an
    //  overlay-defined block body parses byte-for-byte like an effectinfo.txt block (same defaults live in
    //  EffectInfoEmitter's field initialisers). Kept in sync with game/client/EffectInfo.cs.
    // ================================================================================================

    private static void ApplyKeyword(EffectInfoEmitter info, string cmd, string[] argv)
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

            // `stainless` DISABLES the stain (DP cl_particles.c:468) — identical semantics to EffectInfo.ApplyKeyword:
            // staintex[0]=staintex[1]=-2 and staincolor/stainalpha/stainsize reset to baseline. Kept in lockstep with
            // the main parser so an overlay block can suppress an inherited stain exactly as the base catalog does.
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

            // Recognised-but-unmodelled (coronas/cubemaps/shadows/nearest) — accepted, no visual analogue.
            case "lightshadow":
            case "lightcubemapnum":
            case "lightcorona":
            case "forcenearest":
                break;

            default:
                break; // unknown keyword — ignore (matches EffectInfo's lenient behaviour)
        }
    }

    // ------------------------------------------------------------------------------------------------
    //  Keyword value enums (identical to EffectInfo.cs)
    // ------------------------------------------------------------------------------------------------

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

    private static (EiBlend, EiOrientation) DefaultsFor(EiType t) => t switch
    {
        EiType.AlphaStatic => (EiBlend.Alpha, EiOrientation.Billboard),
        EiType.Static => (EiBlend.Add, EiOrientation.Billboard),
        EiType.Spark => (EiBlend.Add, EiOrientation.Spark),
        EiType.Beam => (EiBlend.Add, EiOrientation.Beam),
        EiType.Rain => (EiBlend.Add, EiOrientation.Spark),
        EiType.RainDecal => (EiBlend.Add, EiOrientation.Oriented),
        EiType.Snow => (EiBlend.Add, EiOrientation.Billboard),
        EiType.Bubble => (EiBlend.Add, EiOrientation.Billboard),
        EiType.Blood => (EiBlend.InvMod, EiOrientation.Billboard),
        EiType.Smoke => (EiBlend.Add, EiOrientation.Billboard),
        EiType.Decal => (EiBlend.InvMod, EiOrientation.Oriented),
        EiType.EntityParticle => (EiBlend.Alpha, EiOrientation.Billboard),
        _ => (EiBlend.Alpha, EiOrientation.Billboard),
    };

    // ------------------------------------------------------------------------------------------------
    //  Token helpers (identical to EffectInfo.cs)
    // ------------------------------------------------------------------------------------------------

    private static string Arg(string[] argv, int i) => i < argv.Length ? argv[i] : "";

    private static float F(string[] argv, int i)
    {
        if (i >= argv.Length) return 0f;
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
            return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint v) ? v : fallback;
        }
        catch { return fallback; }
    }

    private static NVec3 V(string[] argv)
        => new(F(argv, 1), F(argv, 2), F(argv, 3));

    /// <summary>Tokenise like EffectInfo.Tokenize (DP COM_ParseToken_Simple): whitespace/newline tokens,
    /// <c>//</c> line comments, quote-grouping. Newline terminates a command line.</summary>
    private static IEnumerable<string[]> Tokenize(string text)
    {
        var args = new List<string>();
        int i = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i];

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

            if (c == ' ' || c == '\t')
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < n && text[i + 1] == '/')
            {
                while (i < n && text[i] != '\n')
                    i++;
                continue;
            }

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

            int s = i;
            while (i < n && text[i] != ' ' && text[i] != '\t' && text[i] != '\n' && text[i] != '\r')
                i++;
            args.Add(text.Substring(s, i - s));
        }
        if (args.Count > 0)
            yield return args.ToArray();
    }
}
