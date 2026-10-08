// Port of Base/darkplaces/sv_ccmds.c SV_Map_f / SV_Changelevel_f / SV_Restart_f as far as their argument
// checks and their messages go ("map <levelname>", "You must be running a server to changelevel. Use
// 'map' instead"), of the fact that a DarkPlaces process has ONE cvar store which its menu, its client
// and its server all read (cvar.c), and of netconn.c NetConn_OpenServerPorts' rule for which address a
// server listens on (net_address and the port, nothing else).
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using VortexArena.Common.Config;
using VortexArena.Engine.Simulation;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Server;

namespace VortexArena.Legacy.Local;

/// <summary>What a console line asks of the local server.</summary>
public enum LegacyMapCommand
{
    /// <summary>Not one of the commands below.</summary>
    None,
    /// <summary>"map x" / "devmap x": end whatever is running and start a new local game on x.</summary>
    StartMap,
    /// <summary>"changelevel x": the running server goes to x and takes its clients along.</summary>
    ChangeLevel,
    /// <summary>"restart": the running level starts again.</summary>
    Restart,
    /// <summary>"maps": list the maps.</summary>
    ListMaps,
    /// <summary>A command that starts a game this client cannot ("load": saved games).</summary>
    Unsupported,
    /// <summary>A recognised command with the wrong arguments; the usage text says how.</summary>
    Usage,
}

/// <summary>
/// A local game about to be started: the map, and everything of the player's configuration that the
/// server program reads.
///
/// HOW CVARS REACH THE SERVER. DarkPlaces has one cvar store per process: the menu's Create dialog
/// sets <c>g_ctf 1</c>, <c>bot_number 4</c>, <c>timelimit_override 10</c> and a mutator or two, then
/// says <c>map</c>, and the server program reads those very variables. Here the server has a store of
/// its own (it may run on its own thread, and what its program sets must not land in the player's
/// saved configuration), so the link is made explicitly, in one direction:
/// <list type="bullet">
/// <item>At the start, <see cref="LegacyLocalCvars.FromPlayer"/> copies every cvar the player's store
/// holds a value for that is not the shipped default into <see cref="Cvars"/> - which
/// <see cref="SvLocalGame"/> applies after the server has run the same default configuration. The
/// result is the store DarkPlaces' server would have seen.</item>
/// <item>While the game runs, a cvar the PLAYER changes (console, menu) is sent on
/// (<see cref="LegacyLocalServer.SetCvar"/>), so <c>bot_number 6</c> or <c>timelimit 5</c> typed in
/// the console acts at once.</item>
/// <item>One thing comes back: the campaign's progress. What else the server program sets (it sets
/// hundreds: per-map settings, <c>_campaign_*</c>, vote results) stays in the server's store and dies
/// with the game, which is the effect DarkPlaces gets from <c>set</c> (not <c>seta</c>) and from
/// <c>settemp</c> being restored when a map ends. But when a campaign level is won, server/campaign.qc
/// CampaignSaveCvar sets <c>g_campaign&lt;name&gt;_index</c> (and <c>_won</c> after the last level) and
/// rewrites <c>campaign.cfg</c>; in DarkPlaces the menu's level list reads that very variable on its
/// next frame, and the file is only for the next start of the game (quake.rc: "exec data/campaign.cfg").
/// So those cvars - <see cref="LegacyLocalCvars.IsCampaignProgress"/>, nothing else - are handed back
/// by <see cref="LegacyLocalServer.PlayerCvar"/> for the owner to put in the player's store. Only a
/// local game has such a path at all: a remote server's program does not run here, and what a remote
/// server's console text or client program sets is put back when the session ends.</item>
/// </list>
/// </summary>
public sealed class LegacyLocalGameRequest
{
    /// <summary>The map's base name ("stormkeep").</summary>
    public required string Map { get; init; }
    /// <summary>A game mode by short name ("dm", "ctf"), or null: the cvars decide (the menu has set them).</summary>
    public string? GameType { get; init; }
    /// <summary>bot_number, or null: the cvars decide.</summary>
    public int? Bots { get; init; }
    /// <summary>Player slots, bots included.</summary>
    public int MaxPlayers { get; init; } = 16;
    /// <summary>Cvars for the server's store, applied after its default configuration.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Cvars { get; init; } = Array.Empty<KeyValuePair<string, string>>();
    /// <summary>A UDP address to also listen on, or null (the default): no socket.</summary>
    public IPEndPoint? Listen { get; init; }
    /// <summary>Run the server on its own thread (<see cref="LegacyLocalServer"/>).</summary>
    public bool Threaded { get; init; } = true;

    /// <summary>The options for <see cref="SvLocalGame.Start"/>: a listen server (not dedicated), never public.</summary>
    public SvLocalGameOptions ToOptions(string dataDirectory, string? writeRoot)
    {
        int bots = Bots ?? 0;
        if (Bots is null)
            foreach ((string name, string value) in Cvars)
                if (name == "bot_number" && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) && float.IsFinite(number))
                    bots = (int)number;
        List<KeyValuePair<string, string>> cvars = new(Cvars.Count + 1);
        foreach (KeyValuePair<string, string> pair in Cvars)
        {
            if (!LegacyLocalCvars.ReachesServer(pair.Key, pair.Value)) continue;
            // An explicit game type is the caller's choice: a g_ctf 1 left in the player's store does not undo it
            // (SvLocalGame selects the mode first and applies the cvars after).
            if (GameType is not null && SvGameTypes.CvarFor(pair.Key) is not null) continue;
            cvars.Add(pair);
        }
        // An explicit bot count wins over a bot_number among the cvars (SvLocalGame applies its Bots first).
        if (Bots is { } wanted) cvars.Add(new KeyValuePair<string, string>("bot_number", Math.Max(0, wanted).ToString(CultureInfo.InvariantCulture)));
        return new SvLocalGameOptions
        {
            DataDirectory = dataDirectory, WriteRoot = writeRoot, Map = Map, GameType = GameType, Bots = Math.Max(0, bots),
            MaxPlayers = Math.Clamp(MaxPlayers, 1, 255), Listen = Listen, Dedicated = false, Cvars = cvars,
        };
    }
}

/// <summary>Which cvars cross from the player's store to a local server's (see <see cref="LegacyLocalGameRequest"/>).</summary>
public static class LegacyLocalCvars
{
    // The server's own identity and plumbing: set by the host, never by a configuration that was
    // written for a DarkPlaces process.
    private static readonly HashSet<string> s_never = new(StringComparer.Ordinal)
    {
        "sv_dedicated", "sv_progs", "csqc_progname", "csqc_progcrc", "csqc_progsize", "pr_checkextension", "utf8_enable", "sv_qcstats",
        "mod_q1bsp_zero_hullsize_cutoff", "port", "net_address", "net_address_ipv6", "cl_port", "sv_threaded",
    };

    // What only a client reads. In DarkPlaces these sit in the same store and the server ignores them;
    // sending them across would only fill the server's store with the player's video settings.
    private static readonly string[] s_clientOnly =
    {
        "vid_", "r_", "gl_", "snd_", "menu_", "_menu_", "hud_", "_hud_", "crosshair", "scr_", "con_", "in_", "joy", "cl_", "_cl_", "m_", "v_",
        "bgmvolume", "volume", "mastervolume", "chase_", "sbar_", "showfps", "shownetgraph", "fov", "sensitivity", "name", "playermodel", "playerskin",
        "rcon_", "crypto_", "legacy_",
    };

    /// <summary>
    /// Whether a cvar (with the value it is about to be given) goes to the local server at all.
    /// <c>sv_public</c> above 0 does not: master server heartbeats are not ported, and a game that
    /// cannot be listed must not be configured as if it were.
    /// </summary>
    public static bool ReachesServer(string name, string value)
    {
        if (name.Length is 0 or > 128 || value.Length > 4096 || s_never.Contains(name)) return false;
        foreach (char c in name)
            if (c <= ' ' || c is '"' or ';' or '$' or '\\' || c > '~') return false;
        foreach (string prefix in s_clientOnly)
            if (name.StartsWith(prefix, StringComparison.Ordinal)) return false;
        if (name == "sv_public" && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float publicity) && publicity > 0) return false;
        return true;
    }

    /// <summary>
    /// Whether a cvar the server program of a LOCAL game has set is the campaign's progress, which is
    /// the player's and goes back to the player's store: <c>g_campaign&lt;campaign name&gt;_index</c>
    /// (how many levels are unlocked) and <c>g_campaign&lt;campaign name&gt;_won</c>, the two that
    /// server/campaign.qc CampaignSaveCvar writes, holding a whole number that is not negative.
    /// Everything else a server program sets - <c>g_campaign</c>, <c>g_campaign_skill</c>,
    /// <c>_campaign_index</c>, limits, per-map settings - is refused here.
    /// </summary>
    public static bool IsCampaignProgress(string name, string value)
    {
        const string prefix = "g_campaign";
        if (name.Length <= prefix.Length || name.Length > 96 || !name.StartsWith(prefix, StringComparison.Ordinal)) return false;
        if (!name.EndsWith("_index", StringComparison.Ordinal) && !name.EndsWith("_won", StringComparison.Ordinal)) return false;
        foreach (char c in name)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.')) return false;
        if (value.Length is 0 or > 6) return false;
        foreach (char c in value)
            if (c is < '0' or > '9') return false;
        return true;
    }

    /// <summary>
    /// The player's settings as a local server needs them at its start: every cvar of
    /// <paramref name="player"/> that was set after the shipped defaults were locked (the player's
    /// config.cfg, the menu, the console) or differs from its default, and that
    /// <see cref="ReachesServer"/>. Sorted by name, so a start is reproducible.
    /// </summary>
    public static List<KeyValuePair<string, string>> FromPlayer(CvarService player)
    {
        ArgumentNullException.ThrowIfNull(player);
        List<KeyValuePair<string, string>> result = new();
        foreach (string name in player.Names)
        {
            if (!player.WasSetByUser(name) && !player.IsModified(name)) continue;
            string value = player.GetString(name);
            if (ReachesServer(name, value)) result.Add(new KeyValuePair<string, string>(name, value));
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return result;
    }
}

/// <summary>The console commands of a local game that are not the client's: which there are, and how their lines are read.</summary>
public static class LegacyLocalCommands
{
    /// <summary>
    /// Commands a DarkPlaces listen server's console runs on the SERVER (sv_ccmds.c, prvm_edict.c), by
    /// the names a client-side interpreter has to forward. Xonotic's own server commands (endmatch,
    /// gotomap, bot_cmd, ...) are aliases in its configuration that end in <c>sv_cmd</c>, so they need
    /// no entry here. <c>map</c>, <c>changelevel</c> and <c>restart</c> are read by <see cref="ParseMapCommand"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> ServerCommands = new[] { "sv_cmd", "kick", "status" };

    /// <summary>DarkPlaces' words for a level-changing command typed with no server running.</summary>
    public const string NoServerForChangeLevel = "You must be running a server to changelevel. Use 'map' instead";

    /// <summary>
    /// Reads "map x", "devmap x", "changelevel x", "restart", "maps", "load x" from a console line (or
    /// its argument vector joined by spaces). <paramref name="map"/> is the level's base name for the
    /// first three, checked by <see cref="IsMapName"/>; <paramref name="usage"/> is DarkPlaces' usage
    /// line when the arguments are wrong.
    /// </summary>
    public static LegacyMapCommand ParseMapCommand(string line, out string map, out string usage)
    {
        map = usage = "";
        List<string> argv = DpStuffTextTokens(line);
        if (argv.Count == 0) return LegacyMapCommand.None;
        switch (argv[0].ToLowerInvariant())
        {
            case "map":
            case "devmap":
                if (argv.Count != 2 || !IsMapName(argv[1]))
                {
                    usage = "map <levelname> : start a new game (kicks off all players)";
                    return LegacyMapCommand.Usage;
                }
                map = argv[1];
                return LegacyMapCommand.StartMap;
            case "changelevel":
                if (argv.Count != 2 || !IsMapName(argv[1]))
                {
                    usage = "changelevel <levelname> : continue game on a new level";
                    return LegacyMapCommand.Usage;
                }
                map = argv[1];
                return LegacyMapCommand.ChangeLevel;
            case "restart":
                if (argv.Count != 1)
                {
                    usage = "restart : restart current level";
                    return LegacyMapCommand.Usage;
                }
                return LegacyMapCommand.Restart;
            case "maps":
                return LegacyMapCommand.ListMaps;
            case "load":
                return LegacyMapCommand.Unsupported;
            default:
                return LegacyMapCommand.None;
        }
    }

    /// <summary>A level's base name as it may be put into "maps/NAME.bsp": no separators, no dots leading anywhere else.</summary>
    public static bool IsMapName(string name)
    {
        if (name.Length is 0 or > 64 || name[0] == '.') return false;
        foreach (char c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '+')) return false;
        return !name.Contains("..", StringComparison.Ordinal) && LegacyQcHost.IsSafePath("maps/" + name + ".bsp");
    }

    /// <summary>
    /// A console line that assigns a cvar the way a player types it: "name value", "set name value",
    /// "seta name value". False for anything else (one word, a command with several arguments).
    /// <paramref name="isCvar"/> says whether a first word names a cvar rather than a command.
    /// </summary>
    public static bool TryParseCvarAssignment(string line, Func<string, bool> isCvar, out string name, out string value)
    {
        name = value = "";
        List<string> argv = DpStuffTextTokens(line);
        if (argv.Count == 3 && argv[0] is "set" or "seta")
        {
            name = argv[1];
            value = argv[2];
            return name.Length > 0;
        }
        if (argv.Count == 2 && isCvar(argv[0]))
        {
            name = argv[0];
            value = argv[1];
            return true;
        }
        return false;
    }

    /// <summary>
    /// The <c>legacy_listen</c> setting: "" or "0" is off (no socket); "1" is this machine only
    /// (127.0.0.1); an IPv4 address, with or without ":port", is that address and no other - a LAN
    /// interface's address to let the LAN in. A name is not resolved and "0.0.0.0" has to be written
    /// out by whoever wants every interface. False (with the reason) for anything else.
    /// </summary>
    public static bool TryParseListen(string? setting, int defaultPort, out IPEndPoint? listen, out string problem)
    {
        listen = null;
        problem = "";
        string text = (setting ?? "").Trim();
        if (text.Length == 0 || text == "0") return true;
        int port = defaultPort is > 0 and <= 65535 ? defaultPort : 26000;
        if (text == "1")
        {
            listen = new IPEndPoint(IPAddress.Loopback, port);
            return true;
        }
        string host = text;
        int colon = text.LastIndexOf(':');
        if (colon > 0)
        {
            if (!int.TryParse(text.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is <= 0 or > 65535)
            {
                problem = $"legacy_listen \"{text}\": the port is not a number from 1 to 65535";
                return false;
            }
            host = text[..colon];
        }
        if (!IPAddress.TryParse(host, out IPAddress? address) || address.AddressFamily != AddressFamily.InterNetwork || host.Split('.').Length != 4)
        {
            problem = $"legacy_listen \"{text}\": expected 0 (off), 1 (this machine only) or an IPv4 address of this machine, optionally with :port";
            return false;
        }
        listen = new IPEndPoint(address, port);
        return true;
    }

    /// <summary>Rebuilds a command line from an argument vector, quoting what needs it.</summary>
    public static string Join(IReadOnlyList<string> argv, int first = 0) => CsqcConsole.JoinArguments(argv, first);

    // A console line's words: quotes group, "//" ends the line. One command only (nothing after a ';').
    private static List<string> DpStuffTextTokens(string line)
    {
        List<string> argv = new();
        List<string> commands = ConfigInterpreter.SplitIntoCommands(line ?? "");
        if (commands.Count == 0) return argv;
        string text = commands[0];
        int i = 0;
        while (i < text.Length && argv.Count < 64)
        {
            while (i < text.Length && text[i] <= ' ') i++;
            if (i >= text.Length) break;
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/') break;
            if (text[i] == '"')
            {
                int end = text.IndexOf('"', i + 1);
                if (end < 0) end = text.Length;
                argv.Add(text[(i + 1)..end]);
                i = Math.Min(text.Length, end + 1);
                continue;
            }
            int start = i;
            while (i < text.Length && text[i] > ' ') i++;
            argv.Add(text[start..i]);
        }
        return argv;
    }
}
