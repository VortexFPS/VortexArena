// Port of Base/darkplaces/host.c Host_Init (the order in which a dedicated server comes up: engine
// cvars registered, then the game's default configuration executed) and Host_AddConfigText ("exec
// default.cfg" by way of quake.rc), reduced to what the server program reads.
using VortexArena.Common.Config;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy.Csqc;

namespace VortexArena.Legacy.Server;

/// <summary>
/// Everything a server needs that is not the level: the mounted game data, the cvar store with
/// DarkPlaces' engine cvars and the game's defaults, the console interpreter, and the file and cvar
/// services the program's builtins go through. One per server process; levels come and go on it.
/// </summary>
public sealed class SvEnvironment : IDisposable
{
    public VirtualFileSystem Files { get; }
    public CvarService Cvars { get; }
    public ConfigInterpreter Interpreter { get; }
    public LegacyQcHost Services { get; }
    public int EngineCvars { get; }
    public bool DefaultsExecuted { get; }
    /// <summary>svs.maxclients as the configuration's "maxplayers" left it (DarkPlaces' default is 8).</summary>
    public int MaxPlayers { get; private set; } = 8;

    /// <param name="dataDirectory">A Xonotic "data" directory (its packs and default.cfg).</param>
    /// <param name="writeRoot">Where files the program writes go (DarkPlaces' user directory), or null to refuse writes.</param>
    /// <param name="print">The program's console output.</param>
    /// <param name="warning">VM warnings.</param>
    /// <exception cref="DirectoryNotFoundException">Nothing could be mounted from the directory.</exception>
    /// <param name="dedicated">sv_dedicated: true for a dedicated server (the default), false for a
    /// listen server - Xonotic's configuration and program branch on it ("if_dedicated", g_start_delay,
    /// sv_autopause, idle kicking).</param>
    public SvEnvironment(string dataDirectory, string? writeRoot, Action<string>? print = null, Action<string>? warning = null, bool dedicated = true)
    {
        Files = new VirtualFileSystem();
        if (!Directory.Exists(dataDirectory) || !Files.MountGameDir(dataDirectory))
        {
            Files.Dispose();
            throw new DirectoryNotFoundException($"nothing could be mounted from \"{dataDirectory}\"");
        }
        Cvars = new CvarService();
        Interpreter = new ConfigInterpreter(Cvars, path => Files.Exists(path) ? Files.ReadText(path) : null);
        // The engine's own cvars first, as DarkPlaces registers them before any configuration runs.
        EngineCvars = CsqcEngineCvars.Register(Cvars);
        Set("pr_checkextension", "1");
        Set("utf8_enable", "1");
        Set("developer", "0");
        // A dedicated server: Xonotic's configuration branches on this ("if_dedicated").
        Set("sv_dedicated", dedicated ? "1" : "0");
        // cmd.c Cmd_Exec, on default.cfg for GAME_XONOTIC: "compatibility for versions prior to
        // 2020-05-25" - the engine inserts these before the game's own configuration runs. With
        // sv_qcstats the engine leaves stats 220 and up (the movement variables) to the program.
        Set("sv_qcstats", "1");
        Set("mod_q1bsp_zero_hullsize_cutoff", "8.03125");
        // cl_cmd, menu_cmd and cmd exist on a client only; the configuration aliases around them on
        // a dedicated server, and a stray one must be a no-op rather than an unknown-cvar assignment.
        foreach (string clientOnly in new[] { "cl_cmd", "menu_cmd", "cmd" }) Interpreter.RegisterCommand(clientOnly, _ => { });
        // "maxplayers <n>" is a command, not a cvar (sv_ccmds.c SV_MaxPlayers_f): it sizes svs.clients,
        // and only works before a level is running. Xonotic's server configuration issues one.
        Interpreter.RegisterCommand("maxplayers", argv =>
        {
            if (argv.Count == 2 && int.TryParse(argv[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n))
                MaxPlayers = Math.Clamp(n, 1, Protocol.DpProtocol.MaxScoreboard);
        }, "sets limit on how many players (or bots) may be connected to the server at once");
        DefaultsExecuted = Interpreter.ExecuteFile("default.cfg");

        Services = new LegacyQcHost(Cvars, Files)
        {
            WriteRoot = writeRoot,
            PrintSink = print ?? (_ => { }),
            WarningSink = warning ?? (_ => { }),
            // DarkPlaces hides only CF_PRIVATE cvars from QuakeC. The server program is the game's own
            // and legitimately reads its passwords (g_password, sv_vote_master_password); what stays
            // hidden is what DarkPlaces hides: remote-console and key material.
            IsPrivateCvar = name => name.StartsWith("rcon_", StringComparison.Ordinal) || name.StartsWith("crypto_", StringComparison.Ordinal),
        };
    }

    private void Set(string name, string value)
    {
        if (Cvars.Has(name)) Cvars.Set(name, value);
        else Cvars.Register(name, value);
    }

    /// <summary>Sets a cvar, creating it if need be (a command-line "+set").</summary>
    public void SetCvar(string name, string value) => Set(name, value);

    /// <summary>Loads and starts a level. Null (with the reason printed through <paramref name="print"/>) if the program or the map cannot be loaded.</summary>
    public SvqcHost? StartLevel(string map, SvqcHostOptions? options = null, SvClient[]? clients = null, Action<string>? print = null)
    {
        options ??= new SvqcHostOptions();
        string progsName = Cvars.GetString("sv_progs") is { Length: > 0 } configured ? configured : options.ProgsName;
        if (Services.ReadFile(progsName) is not { } program)
        {
            print?.Invoke($"server: {progsName} is not in the game data\n");
            return null;
        }
        SvqcHost host;
        try { host = new SvqcHost(program, Services, Cvars, Interpreter, Files, options, clients); }
        catch (SvqcLoadException e)
        {
            print?.Invoke(e.Message + "\n");
            return null;
        }
        if (print is not null) host.Print = print;
        if (!host.SpawnServer(map) && host.State == SvState.Dead)
        {
            host.Dispose();
            return null;
        }
        return host;
    }

    public void Dispose() => Files.Dispose();
}
