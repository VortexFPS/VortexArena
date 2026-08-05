using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>
/// The effectinfo.txt parser and catalog — the C# successor to Darkplaces'
/// <c>CL_Particles_ParseEffectInfo</c> (Base/darkplaces/cl_particles.c). It tokenises the file the same
/// way (whitespace/newline tokens, <c>//</c> comments), accumulates one <see cref="EffectInfoEmitter"/>
/// per <c>effect &lt;name&gt;</c> block starting from the DP baseline defaults, and groups blocks by name
/// (multiple same-named blocks layer into one effect, exactly like DP). Lookups are case-insensitive,
/// since the libs reference effects by either the EFFECT_* spelling or the lower-case effectinfo name.
///
/// The text is sourced lazily from the mounted content VFS (the configured <c>res://data</c>
/// tree); a host that already has a VFS can inject one via <see cref="TextLoader"/>, otherwise we
/// read the file directly off disk. A miss leaves the catalog empty and callers fall back to the heuristic
/// classifier — so the game still runs with no content mounted.
/// </summary>
public sealed class EffectInfo
{
    /// <summary>The default path the parser reads when no <see cref="TextLoader"/> is supplied.</summary>
    public const string DefaultVPath = "effectinfo.txt";

    // name (lower-cased) -> the ordered list of emitter blocks that share that name.
    private readonly Dictionary<string, List<EffectInfoEmitter>> _byName =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of distinct effect names parsed.</summary>
    public int Count => _byName.Count;

    /// <summary>True once a parse has populated at least one effect.</summary>
    public bool Loaded { get; private set; }

    /// <summary>
    /// Optional override for sourcing the raw file text (e.g. the host's VirtualFileSystem.ReadText).
    /// Given the virtual path, returns the file's text, or null/empty on miss. When unset the parser reads
    /// the file directly from the resolved content directory on disk.
    /// </summary>
    public Func<string, string?>? TextLoader { get; set; }

    /// <summary>Return the layered emitter blocks for <paramref name="effectName"/>, or null if unknown.</summary>
    public IReadOnlyList<EffectInfoEmitter>? Get(string effectName)
    {
        if (string.IsNullOrEmpty(effectName))
            return null;
        return _byName.TryGetValue(effectName, out List<EffectInfoEmitter>? list) ? list : null;
    }

    public bool Has(string effectName)
        => !string.IsNullOrEmpty(effectName) && _byName.ContainsKey(effectName);

    // ================================================================================================
    //  Loading
    // ================================================================================================

    /// <summary>
    /// Load + parse effectinfo.txt. Tries <see cref="TextLoader"/> first, then a direct disk read of the
    /// content tree. Idempotent-ish: re-parses and replaces the catalog. Returns true on success.
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
    //  Process-lifetime shared catalog (menu warm)
    // ================================================================================================

    private static readonly object SharedGate = new();
    private static readonly Dictionary<string, EffectInfo> SharedByVPath =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The parsed catalog for <paramref name="vpath"/>, parsed once per process and shared by every match.
    /// Returns a catalog even on a read miss (empty, <see cref="Loaded"/> false) so callers keep their
    /// heuristic fallback and a miss is not re-probed on every map load.
    ///
    /// <para><b>Why this exists.</b> effectinfo.txt is ~9.4k lines / 169 KB that expand into ~1000 layered
    /// emitter blocks, it is entirely map-independent, and it was re-parsed from scratch by every match
    /// (<c>EffectSystem.EnsureInfoLoaded</c>, forced at map load by <c>NetGame</c>'s
    /// <c>Effects.Warmup()</c>). Sharing lets <see cref="Client.MenuAssetWarmer"/> pay it once during menu
    /// dwell, and makes every subsequent map change / server switch a cache hit — the same shape as the
    /// process-lifetime <c>AssetLoader</c> from Phase 1 of the loading-speed work.</para>
    ///
    /// <para><b>Thread-safety.</b> Callable from the streamer's worker lane (the menu warm) or the main
    /// thread (a match's own load). The parse runs OUTSIDE the lock so two callers never serialize behind
    /// one 169 KB tokenise, then the winner is published under a re-check — the same publish pattern
    /// <c>AssetSystem.WarmTextureOffThread</c> uses. A loser discards its own parse and returns the winner,
    /// so every caller sees one instance.</para>
    ///
    /// <para><b>Sharing is safe because the catalog is immutable after parse.</b> <see cref="Parse"/> is the
    /// only writer of <c>_byName</c>, a cached instance is never re-loaded, and <see cref="TextLoader"/> is
    /// read only inside <see cref="Load"/>. The one standing assumption is that consumers do not mutate the
    /// <see cref="EffectInfoEmitter"/> blocks handed back by <see cref="Get"/> — already true today, since
    /// those blocks are shared across every spawn of an effect within a match.</para>
    ///
    /// <para><b>Mount lifetime.</b> Keyed by vpath only, so this assumes the content mounts do not change
    /// under a running process (they are established at boot). <see cref="ClearShared"/> exists for a host
    /// that remounts.</para>
    /// </summary>
    public static EffectInfo GetShared(string vpath, Func<string, string?>? textLoader)
    {
        string key = vpath ?? DefaultVPath;
        lock (SharedGate)
        {
            if (SharedByVPath.TryGetValue(key, out EffectInfo? hit))
                return hit;
        }

        // Parse outside the lock — 169 KB of tokenising should not block another thread that only wants a
        // catalog that is already built.
        var built = new EffectInfo { TextLoader = textLoader };
        try { built.Load(key); }
        catch { /* leave empty; callers fall back to the heuristic classifier */ }

        lock (SharedGate)
        {
            if (SharedByVPath.TryGetValue(key, out EffectInfo? raced))
                return raced;   // someone else won; drop ours so every caller sees one instance
            SharedByVPath[key] = built;
        }

        // Printed on the COLD parse only, so "did the sharing actually work" is observable rather than
        // assumed: this must appear exactly ONCE per process, before/independent of the per-match
        // "[EffectSystem] effectinfo: N effects" line, which prints on every match either way. A second
        // occurrence means a match missed the cache. (GD.Print off the main thread follows
        // AssetSystem.WarmTextureOffThread's precedent — this can run on the streamer's worker lane.)
        GD.Print($"[EffectInfo] parsed '{key}': {built.Count} effects (process-lifetime, shared by every match).");
        return built;
    }

    /// <summary>Drop every shared catalog — for a host that remounts content under a running process.</summary>
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

        // 2) direct disk read from the resolved content directory. We mirror DataPaths.Resolve's
        //    convention (res:// rooted at the project dir) so the in-tree assets/ dir is found.
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
        // The effectinfo lives at <data>/xonotic-data.pk3dir/effectinfo.txt in the content repo; allow the
        // caller to pass either the bare name or a full relative path.
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
    //  Parser — faithful port of CL_Particles_ParseEffectInfo
    // ================================================================================================

    /// <summary>Parse raw effectinfo text directly (also the unit-test entry; no I/O).</summary>
    public void Parse(string text)
    {
        _byName.Clear();
        Loaded = false;
        if (string.IsNullOrEmpty(text))
            return;

        EffectInfoEmitter? info = null;
        List<EffectInfoEmitter>? currentList = null;

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
                if (!_byName.TryGetValue(name, out currentList))
                {
                    currentList = new List<EffectInfoEmitter>();
                    _byName[name] = currentList;
                }
                info = new EffectInfoEmitter();
                currentList.Add(info);
                continue;
            }

            if (info is null)
                continue; // command before any effect — skip (DP errors out; we're lenient)

            info.Defined = true;
            ApplyKeyword(info, cmd, argv);
        }

        Loaded = _byName.Count > 0;
    }

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

    // ------------------------------------------------------------------------------------------------
    //  Keyword value enums
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

    /// <summary>Per-type default blend + orientation, matching DP's <c>particletype[]</c> table.</summary>
    private static (EiBlend, EiOrientation) DefaultsFor(EiType t) => t switch
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
    private static IEnumerable<string[]> Tokenize(string text)
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
}
