namespace VortexArena.QuakeC;

/// <summary>
/// What the engine-independent builtins (strings, maths, cvars, files) need from whatever is hosting
/// the VM. Everything a downloaded program can reach outside its own memory goes through here or
/// through builtins the host registers itself, so this is where its authority is bounded:
/// a path is always relative to the game's data area, a cvar write can be refused, a console command
/// can be filtered.
/// </summary>
public interface IQcHost
{
    /// <summary>Console output from the program (the print builtin). Text may carry Quake colour codes.</summary>
    void Print(string text);

    /// <summary>A non-fatal problem in the program worth showing a developer ("VM_Warning" in DarkPlaces).</summary>
    void Warning(string text);

    /// <summary>DarkPlaces' <c>developer</c> cvar: whether dprint output is shown.</summary>
    bool Developer { get; }

    /// <summary>DarkPlaces' <c>utf8_enable</c> cvar (Xonotic sets it to 1): whether string builtins count characters or bytes.</summary>
    bool Utf8Enabled { get; }

    /// <summary>Wall-clock seconds since start, for gettime(GETTIME_REALTIME) and entity-slot reuse.</summary>
    double RealTime { get; }

    // ---- cvars -------------------------------------------------------------------------------------

    bool CvarExists(string name);
    /// <summary>The cvar's value, or "" if it does not exist.</summary>
    string CvarString(string name);
    /// <summary>The cvar's value as a number (DarkPlaces atof semantics), or 0 if it does not exist.</summary>
    float CvarFloat(string name);
    string CvarDefaultString(string name);
    string CvarDescription(string name);
    /// <summary>DarkPlaces cvar_type flags (CVAR_TYPEFLAG_EXISTS = 1, SAVED = 2, PRIVATE = 4, ENGINE = 8, HASDESCRIPTION = 16, READONLY = 32), or 0.</summary>
    int CvarTypeFlags(string name);
    /// <summary>Sets a cvar on the program's behalf. The host may refuse (read-only or protected cvars).</summary>
    void CvarSet(string name, string value);
    /// <summary>Creates a cvar if it does not exist (the registercvar builtin). Returns false if it already did.</summary>
    bool RegisterCvar(string name, string value, int flags);
    /// <summary>Names of all cvars matching a prefix and not matching an anti-prefix (buf_cvarlist).</summary>
    IEnumerable<string> CvarNames(string prefix, string antiPrefix);

    /// <summary>Appends text to the console command buffer (the localcmd builtin).</summary>
    void LocalCommand(string text);

    // ---- files (always relative to the game's data area; never an absolute or parent path) --------

    /// <summary>Opens a file for reading from the virtual filesystem, or null.</summary>
    Stream? OpenRead(string path);
    /// <summary>Opens a file for writing in the user's data directory, or null if writing there is not permitted.</summary>
    Stream? OpenWrite(string path, bool append);
    /// <summary>Files matching a glob pattern (the search_begin builtin).</summary>
    IReadOnlyList<string> Search(string pattern, bool caseInsensitive, string? packFile);
    /// <summary>The pack a file would be read from (whichpack), or "" if it is a loose file or missing.</summary>
    string WhichPack(string path);
}
