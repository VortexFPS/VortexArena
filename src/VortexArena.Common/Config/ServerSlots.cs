// Port of the DarkPlaces server slot count: the svs.maxclients / svs.maxclients_next pair (server.h:28),
// the `maxplayers` console command (host_cmd.c:2517 MaxPlayers_f, registered at host_cmd.c:3070), and the
// deferred adoption at map start (host_cmd.c:375-380, inside Host_Map_f).
using System;
using System.Collections.Generic;
using System.Globalization;

namespace VortexArena.Common.Config;

/// <summary>
/// DP <c>svs.maxclients</c>: how many clients — humans AND bots together — a server may hold at once, plus the
/// <c>maxplayers</c> command that sets it. This is the number QC reads as the read-only engine global
/// <c>maxclients</c>, and it is the hard ceiling in both of that global's consumers:
/// <c>bot_fixcount</c> (bot.qc:643,655 — <c>bots = min(bots, maxclients - realplayers)</c>) and
/// <c>GetPlayerLimit</c> (client.qc:2160,2168).
///
/// <para>Upstream splits this across the engine/gamecode boundary; the port's equivalent boundary is
/// host (<c>game/</c>) vs. server gamecode (<c>VortexArena.Server</c>), so the host owns this state and hands it
/// to <see cref="T:VortexArena.Server.Bot.BotPopulation"/><c>.MaxClients</c> at server start — the same
/// engine-writes / gamecode-reads shape.</para>
///
/// <para><b>Why the value is deferred.</b> DP refuses to change a live server's count and applies the pending
/// value at the next <c>map</c> (host_cmd.c:375-380, where it also reallocs the client array). The port inherits
/// that constraint honestly rather than by fiat: the ENet peer count is fixed when the socket binds
/// (<c>NetTransport.Server.Start</c> → <c>CreateServer(port, maxClients, …)</c>), so it genuinely cannot move
/// under a running server.</para>
///
/// <para>Static because DP's is a global (<c>svs</c>) and every writer — the shipped
/// <c>xonotic-server.cfg:31</c> <c>maxplayers 16</c>, the operator's <c>server.cfg</c>, the console, the CLI
/// <c>--maxplayers</c>, and the Create Game menu — has to land in one place the host reads at start.
/// <see cref="Reset"/> exists for tests that care.</para>
/// </summary>
public static class ServerSlots
{
    /// <summary>DP <c>MAX_SCOREBOARD</c> (quakedef.h:144): "max number of players in game at once (255 protocol
    /// limit)". The port's own roster encoding agrees — <c>GametypeStatusBlock</c> writes a byte count followed
    /// by that many ushort net ids, so 255 is the ceiling on both sides.</summary>
    public const int MaxSlots = 255;

    /// <summary>The lower bound of DP's <c>bound(1, n, MAX_SCOREBOARD)</c> (host_cmd.c:2534).</summary>
    public const int MinSlots = 1;

    /// <summary>
    /// What a server starts with when nothing has said otherwise — a boot with no content tree mounted (a bare
    /// CI run, most tests), where no cfg is there to exec.
    ///
    /// <para>Kept in step with <c>data/core.pk3dir/vortex-server.cfg</c>'s shipped <c>maxplayers 32</c> so the
    /// compiled fallback and the shipped default are one number rather than two that drift. Upstream's 16
    /// (xonotic-server.cfg:31) is still what the unmodified Xonotic chain sets; the Vortex layer overrides it
    /// last, which is why this says 32.</para>
    /// </summary>
    public const int DefaultSlots = 32;

    private static int _maxClients = DefaultSlots;
    private static int _maxClientsNext = DefaultSlots;

    /// <summary>DP <c>svs.maxclients</c>: the count the RUNNING server was started with. Read by the host at
    /// server start and by the gamecode through <c>BotPopulation.MaxClients</c>; never moves under a live server.</summary>
    public static int MaxClients => _maxClients;

    /// <summary>DP <c>svs.maxclients_next</c>: the pending count a later <see cref="Adopt"/> will promote. This
    /// is what <c>maxplayers</c> with no argument reports, matching host_cmd.c:2523.</summary>
    public static int MaxClientsNext => _maxClientsNext;

    /// <summary>
    /// Host predicate for DP's <c>sv.active</c> check (host_cmd.c:2527): is a server live right now? Wired by
    /// the host that owns the socket; null (the default) means no host, i.e. no server — so a bare config load
    /// or a test never sees the "can not be changed while a server is running" branch.
    /// </summary>
    public static Func<bool>? IsServerActive { get; set; }

    /// <summary>
    /// DP <c>Host_Map_f</c> (host_cmd.c:375-380): promote the pending count as the server starts, and return it.
    /// The caller uses the result for BOTH the transport's peer cap and the gamecode's ceiling — upstream's one
    /// <c>svs.maxclients</c> backs both, so they must not drift apart here either.
    /// </summary>
    public static int Adopt() => _maxClients = _maxClientsNext;

    /// <summary>
    /// Set the PENDING count (DP writes only <c>svs.maxclients_next</c>; a running server keeps its own until
    /// the next map). Clamped like DP's <c>bound(1, n, MAX_SCOREBOARD)</c>.
    /// </summary>
    public static void Set(int slots) => _maxClientsNext = System.Math.Clamp(slots, MinSlots, MaxSlots);

    /// <summary>Restore the boot state — for tests/benches that assert on slot behaviour and must not leak it
    /// into the next case (the DP original is process-global and never needs this).</summary>
    public static void Reset()
    {
        _maxClients = _maxClientsNext = DefaultSlots;
        IsServerActive = null;
    }

    /// <summary>
    /// Register <c>maxplayers</c> (DP <c>Cmd_AddCommand</c>, host_cmd.c:3070) on <paramref name="interp"/>.
    ///
    /// <para>Registration ORDER matters: <c>xonotic-server.cfg:31</c> is a two-token line and <c>maxplayers</c>
    /// is not a cvar, so an interpreter that execs the tree without this handler falls through to
    /// <c>ConfigInterpreter</c>'s bare-assignment path and quietly creates a <c>maxplayers</c> CVAR that nothing
    /// reads — which is exactly what the port did before this existed. <see cref="ConfigLoader"/> therefore
    /// registers it on every interpreter it builds, before the first <c>ExecuteFile</c>.</para>
    ///
    /// <para><paramref name="print"/> is DP's <c>Con_Printf</c>. Defaults to the log so a config-time or bind-time
    /// invocation still says something; the console passes its own sink so a typed <c>maxplayers</c> answers in
    /// the console.</para>
    /// </summary>
    public static void RegisterCommand(ConfigInterpreter interp, Action<string>? print = null)
    {
        if (interp is null) throw new ArgumentNullException(nameof(interp));
        Action<string> echo = print ?? (s => Diagnostics.Log.Help(s));
        interp.RegisterCommand("maxplayers", argv => Command(argv, echo),
            "maxplayers <n> — limit on how many players (or bots) may be connected at once; applies at the next map");
    }

    /// <summary>DP <c>MaxPlayers_f</c> (host_cmd.c:2517-2542), argument-for-argument.</summary>
    private static void Command(IReadOnlyList<string> argv, Action<string> print)
    {
        if (argv.Count != 2)
        {
            print($"\"maxplayers\" is \"{_maxClientsNext}\"");
            return;
        }

        // DP prints the refusal and then records the pending value ANYWAY (host_cmd.c:2527-2537) — the operator
        // is told it won't take effect now, not that it was dropped.
        if (IsServerActive?.Invoke() == true)
        {
            print("maxplayers can not be changed while a server is running.");
            print("It will be changed on next server startup (\"map\" command).");
        }

        // DP uses atoi: an unparseable argument is 0, which the clamp then lifts to MinSlots.
        int n = int.TryParse(argv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
        n = System.Math.Clamp(n, MinSlots, MaxSlots);
        print($"\"maxplayers\" set to \"{n}\"");
        _maxClientsNext = n;

        // DP also forces `deathmatch` 0/1 here (host_cmd.c:2538-2541). The port models no `deathmatch` cvar —
        // mode selection is the gametype registry — so there is nothing to mirror.
    }
}
