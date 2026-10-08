// Port of Base/darkplaces/cl_particles.c CL_Particles_LoadEffectInfo, CL_Particles_ParseEffectInfo
// (the name table only), CL_ParticleEffectIndexForName, CL_ParticleEffectNameForIndex and
// standardeffectnames[]; with common.c COM_ParseToken_Simple as that parser calls it.
using System.Text;

namespace VortexArena.Legacy.Csqc;

/// <summary>
/// The numbering of particle effects. An effect travels as a number - in svc_pointparticles, in
/// Xonotic's own messages, and as the result of the particleeffectnum builtin - and the number is the
/// order in which effect NAMES first appear: 35 names built into the engine, then each new name in
/// effectinfo.txt, then each new name in the map's own maps/&lt;map&gt;_effectinfo.txt. Server and client
/// number them independently from the same files, so a client that numbers differently plays the
/// wrong effect without any error.
///
/// Only the names are kept. What an effect looks like is the presentation's business.
/// </summary>
public sealed class CsqcEffectInfo
{
    /// <summary>MAX_PARTICLEEFFECTNAME.</summary>
    public const int MaxNames = 4096;
    /// <summary>MAX_PARTICLEEFFECTINFO: definitions (not names) per load; reaching it stops the parse.</summary>
    public const int MaxInfos = 8192;

    /// <summary>standardeffectnames[]: effect numbers 1..35 are fixed (effectnameindex_t in client.h).</summary>
    public static readonly string[] StandardNames =
    {
        "",
        "TE_GUNSHOT", "TE_GUNSHOTQUAD", "TE_SPIKE", "TE_SPIKEQUAD", "TE_SUPERSPIKE", "TE_SUPERSPIKEQUAD",
        "TE_WIZSPIKE", "TE_KNIGHTSPIKE", "TE_EXPLOSION", "TE_EXPLOSIONQUAD", "TE_TAREXPLOSION", "TE_TELEPORT",
        "TE_LAVASPLASH", "TE_SMALLFLASH", "TE_FLAMEJET", "EF_FLAME", "TE_BLOOD", "TE_SPARK", "TE_PLASMABURN",
        "TE_TEI_G3", "TE_TEI_SMOKE", "TE_TEI_BIGEXPLOSION", "TE_TEI_PLASMAHIT", "EF_STARDUST", "TR_ROCKET",
        "TR_GRENADE", "TR_BLOOD", "TR_WIZSPIKE", "TR_SLIGHTBLOOD", "TR_KNIGHTSPIKE", "TR_VORESPIKE",
        "TR_NEHAHRASMOKE", "TR_NEXUIZPLASMA", "TR_GLOWTRAIL", "SVC_PARTICLE",
    };

    /// <summary>EFFECT_SVC_PARTICLE: the effect the particle builtin (#48) and svc_particle use.</summary>
    public const int SvcParticle = 35;

    // Parameter counts (the command word included) of every line the C checks with checkparms. A
    // mismatch does not skip the line: the macro's `break` leaves the line loop, so the REST OF THE
    // FILE is ignored - including every effect name it would have added. That has to be reproduced
    // for the numbers to match on a file with a typo in it.
    private static readonly Dictionary<string, int> ParameterCounts = new(StringComparer.Ordinal)
    {
        ["countabsolute"] = 2, ["count"] = 2, ["type"] = 2, ["blend"] = 2, ["orientation"] = 2,
        ["color"] = 3, ["tex"] = 3, ["size"] = 3, ["sizeincrease"] = 2, ["alpha"] = 4, ["time"] = 3,
        ["gravity"] = 2, ["bounce"] = 2, ["airfriction"] = 2, ["liquidfriction"] = 2,
        ["originoffset"] = 4, ["relativeoriginoffset"] = 4, ["velocityoffset"] = 4, ["relativevelocityoffset"] = 4,
        ["originjitter"] = 4, ["velocityjitter"] = 4, ["velocitymultiplier"] = 2,
        ["lightradius"] = 2, ["lightradiusfade"] = 2, ["lighttime"] = 2, ["lightcolor"] = 4,
        ["lightshadow"] = 2, ["lightcubemapnum"] = 2, ["lightcorona"] = 3,
        ["underwater"] = 1, ["notunderwater"] = 1, ["trailspacing"] = 2, ["stretchfactor"] = 2,
        ["staincolor"] = 3, ["stainalpha"] = 3, ["stainsize"] = 3, ["staintex"] = 3,
        ["rotate"] = 5, ["forcenearest"] = 1,
    };

    private readonly List<string> _names = new(StandardNames);
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private int _infos;
    // The (name, defined) state of each definition, which the "effect" line needs: a repeated
    // "effect X" line continues the previous definition of X only while that one is still empty.
    private readonly List<(int Name, bool Defined)> _definitions = new();

    public CsqcEffectInfo()
    {
        for (int i = 1; i < StandardNames.Length; i++) _index.TryAdd(StandardNames[i], i);
    }

    /// <summary>Names by number; index 0 is unused.</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>Messages the C prints while parsing (a malformed line, too many effects).</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>
    /// CL_Particles_LoadEffectInfo: effectinfo.txt, then the map's own file.
    /// <paramref name="worldNameNoExtension"/> is "maps/x" (cl.worldnamenoextension), or empty for none.
    /// </summary>
    /// <param name="customFile">cl_particles_reloadeffects with an argument: that file instead of
    /// effectinfo.txt, and no per-map file.</param>
    public static CsqcEffectInfo Load(Func<string, byte[]?> readFile, string worldNameNoExtension, string? customFile = null)
    {
        ArgumentNullException.ThrowIfNull(readFile);
        CsqcEffectInfo info = new();
        string mainFile = string.IsNullOrEmpty(customFile) ? "effectinfo.txt" : customFile;
        if (readFile(mainFile) is { } main) info.Parse(main, mainFile);
        if (!string.IsNullOrEmpty(worldNameNoExtension) && string.IsNullOrEmpty(customFile))
        {
            string mapFile = worldNameNoExtension + "_effectinfo.txt";
            if (readFile(mapFile) is { } perMap) info.Parse(perMap, mapFile);
        }
        return info;
    }

    /// <summary>CL_ParticleEffectIndexForName: the number, or 0 if there is no effect of that name.</summary>
    public int IndexForName(string name) => name.Length > 0 && _index.TryGetValue(name, out int index) ? index : 0;

    /// <summary>CL_ParticleEffectNameForIndex, or null.</summary>
    public string? NameForIndex(int index) => index >= 1 && index < _names.Count ? _names[index] : null;

    public void Parse(byte[] file, string fileName)
    {
        // The C compares bytes; Latin-1 keeps every byte a distinct char without a decoding step that
        // could merge two different byte sequences into one name.
        Parse(Encoding.Latin1.GetString(file), fileName);
    }

    /// <summary>CL_Particles_ParseEffectInfo. Adds the file's new names in order of first appearance.</summary>
    public void Parse(string text, string fileName)
    {
        int position = 0;
        bool haveEffect = false;
        int current = -1;
        List<string> argv = new(16);
        for (int line = 1; ; line++)
        {
            argv.Clear();
            while (true)
            {
                if (!ParseToken(text, ref position, out string token)) return;
                if (token == "\n") break;
                if (argv.Count < 16) argv.Add(token.Length > 1023 ? token[..1023] : token);
            }
            if (argv.Count < 1) continue;

            if (argv[0] == "effect")
            {
                if (argv.Count != 2) { Warn(fileName, line, argv, 2); return; }
                if (_infos >= MaxInfos)
                {
                    Warnings.Add($"{fileName}:{line}: too many effects!");
                    return;
                }
                // Names are stored in MAX_QPATH (128-byte) buffers but looked up by the whole word, so
                // a name too long to store never matches its own stored copy and is added again.
                string name = argv[1].Length > 127 ? argv[1][..127] : argv[1];
                int nameIndex = 0;
                if (argv[1].Length > 127 || !_index.TryGetValue(name, out nameIndex))
                {
                    // "if we run out of names, abort"
                    if (_names.Count >= MaxNames)
                    {
                        Warnings.Add($"{fileName}:{line}: too many effects!");
                        return;
                    }
                    // An empty name cannot be stored (the table's free slots are the empty ones); the C
                    // "adds" it to the first free slot, which stays free.
                    if (name.Length == 0) nameIndex = _names.Count;
                    else
                    {
                        nameIndex = _names.Count;
                        _names.Add(name);
                        _index.TryAdd(name, nameIndex); // the C's linear search finds the first

                    }
                }

                // Reuse a definition of this name that has no commands yet, else start a new one.
                current = _definitions.FindIndex(d => !d.Defined && d.Name == nameIndex);
                if (current < 0)
                {
                    current = _definitions.Count;
                    _definitions.Add((nameIndex, false));
                    _infos++;
                }
                haveEffect = true;
                continue;
            }

            if (!haveEffect)
            {
                Warnings.Add($"{fileName}:{line}: command {argv[0]} encountered before effect");
                return;
            }
            _definitions[current] = (_definitions[current].Name, true);
            // "stainless" takes anything; an unknown command is skipped with a message.
            if (argv[0] == "stainless") continue;
            if (!ParameterCounts.TryGetValue(argv[0], out int expected))
            {
                Warnings.Add($"{fileName}:{line}: skipping unknown command {argv[0]}");
                continue;
            }
            if (argv.Count != expected) { Warn(fileName, line, argv, expected); return; }
        }
    }

    private void Warn(string fileName, int line, List<string> argv, int expected) =>
        Warnings.Add($"{fileName}:{line}: error while parsing: {argv[0]} given {argv.Count} parameters, should be {expected} parameters");

    private static bool IsWhitespace(char c) => c is '\0' or ' ' or '\t' or '\r' or '\n';

    // COM_ParseToken_Simple(&text, returnnewline: true, parsebackslash: false, parsecomments: true).
    // A line ending is itself a token ("\n"); the end of the text ends the parse.
    private static bool ParseToken(string data, ref int at, out string token)
    {
        token = "";
        while (true)
        {
            while (true)
            {
                if (at >= data.Length || data[at] == '\0') return false;
                char c = data[at];
                if (!IsWhitespace(c) || c is '\n' or '\r') break;
                at++;
            }

            // "handle Windows line ending"
            if (data[at] == '\r' && at + 1 < data.Length && data[at + 1] == '\n') at++;

            char next = at + 1 < data.Length ? data[at + 1] : '\0';
            if (data[at] == '/' && next == '/')
            {
                while (at < data.Length && data[at] is not ('\n' or '\r' or '\0')) at++;
                continue;
            }
            if (data[at] == '/' && next == '*')
            {
                at++;
                while (at < data.Length && data[at] != '\0' && !(data[at] == '*' && at + 1 < data.Length && data[at + 1] == '/')) at++;
                if (at < data.Length && data[at] != '\0') at++;
                if (at < data.Length && data[at] != '\0') at++;
                continue;
            }
            break;
        }

        if (data[at] == '"')
        {
            int start = ++at;
            while (at < data.Length && data[at] is not ('"' or '\0')) at++;
            token = data[start..at];
            if (at < data.Length && data[at] == '"') at++;
            return true;
        }
        if (data[at] is '\r' or '\n')
        {
            at++;
            token = "\n";
            return true;
        }
        int begin = at;
        while (at < data.Length && !IsWhitespace(data[at])) at++;
        token = data[begin..at];
        return true;
    }
}
