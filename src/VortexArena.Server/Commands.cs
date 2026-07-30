using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using Waypoints = XonoticGodot.Common.Gameplay.Waypoints;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server;

/// <summary>
/// The context handed to a console-command handler — the C# successor to QuakeC's command argv/source
/// plumbing (server/command/sv_cmd.qc + common/command/generic.qc, the <c>argc</c>/<c>argv(i)</c> reads and
/// the rcon/client source distinction). Carries the parsed argument vector and a sink for command output so
/// a handler stays host-agnostic (a dedicated server writes to its console; a listen server to the client).
/// </summary>
public sealed class CommandContext
{
    private readonly string[] _argv;
    private readonly StringBuilder _out = new();

    /// <summary>True when the command came from rcon / the server console (QC <c>CMD_REQUEST_COMMAND</c> rcon).</summary>
    public bool IsServerConsole { get; }

    /// <summary>The issuing player, when a client typed the command (QC <c>caller</c>); null from the console.</summary>
    public Player? Caller { get; }

    /// <summary>QC <c>argc</c>: the token count, including the command name at index 0.</summary>
    public int ArgCount => _argv.Length;

    public CommandContext(string[] argv, bool isServerConsole, Player? caller)
    {
        _argv = argv;
        IsServerConsole = isServerConsole;
        Caller = caller;
    }

    /// <summary>QC <c>argv(i)</c>: the i-th token ("" if out of range). Index 0 is the command name.</summary>
    public string Arg(int i) => i >= 0 && i < _argv.Length ? _argv[i] : "";

    /// <summary>The full token vector (QC the argv array) — index 0 is the command name.</summary>
    public string[] Argv => _argv;

    /// <summary>QC <c>stof(argv(i))</c>: the i-th token as a float (0 if absent/unparsable).</summary>
    public float ArgFloat(int i)
        => float.TryParse(Arg(i), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f;

    /// <summary>QC <c>argv(i)</c> joined from <paramref name="first"/> to the end (e.g. a <c>say</c> message).</summary>
    public string ArgTail(int first)
    {
        if (first >= _argv.Length) return "";
        return string.Join(' ', _argv, first, _argv.Length - first);
    }

    /// <summary>Write a line of command output (QC <c>print()</c> / <c>sprint(caller, ...)</c>).</summary>
    public void Print(string line) => _out.AppendLine(line);

    /// <summary>The accumulated output so far (the host relays it to the console/client).</summary>
    public string Output => _out.ToString();
}

/// <summary>One registered console command (QC a <c>GENERIC_COMMAND</c>/<c>SERVER_COMMAND</c> entry).</summary>
public sealed class ConsoleCommand
{
    public string Name { get; }
    public string Help { get; }

    /// <summary>The handler. Returns true if it consumed the command (QC the request-COMMAND return).</summary>
    public Func<CommandContext, bool> Handler { get; }

    public ConsoleCommand(string name, string help, Func<CommandContext, bool> handler)
    {
        Name = name;
        Help = help;
        Handler = handler;
    }
}

/// <summary>
/// The server console-command registry — the C# successor to the server/command/ command bus
/// (sv_cmd.qc <c>SV_ParseServerCommand</c> / <c>GameCommand_*</c> + common/command/generic.qc): a
/// name → handler table plus a dispatcher that tokenizes a command line and routes it. Mirrors the QC
/// <c>SERVER_COMMAND(name, help) { … }</c> registrations: this class registers the common server admin
/// commands (<c>restart</c>, <c>endmatch</c>, <c>kick</c>, <c>say</c>, <c>vote</c>, <c>bot_add</c>/<c>bot_remove</c>,
/// <c>map</c>/<c>gotomap</c>) and the cvar reflection commands (<c>set</c>/<c>seta</c>/<c>cvar</c>/<c>toggle</c>),
/// all acting on a live <see cref="GameWorld"/>.
///
/// A host owns one of these per world, calls <see cref="RegisterBuiltins"/> once (done in the ctor), and
/// feeds it console/rcon/client input via <see cref="Execute"/>. Output is collected on the
/// <see cref="CommandContext"/> for the host to relay.
/// </summary>
public sealed class Commands
{
    private readonly Dictionary<string, ConsoleCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly GameWorld _world;

    /// <summary>Optional sink the host wires to receive a "kick this player" request (QC dropclient).</summary>
    public Action<Player, string>? KickHandler { get; set; }

    /// <summary>Optional sink the host wires to receive chat lines for broadcast (QC the say bus).</summary>
    public Action<Player?, string, bool>? ChatHandler { get; set; }

    /// <summary>
    /// Optional sink the host wires for server announcements broadcast to everyone (QC <c>bprint</c>) — vote
    /// results, ban notices, timeout/teamplay messages. Distinct from <see cref="ChatHandler"/> (player chat).
    /// </summary>
    public Action<string>? ChatBroadcast { get; set; }

    /// <summary>
    /// [T46] Optional per-player chat delivery sink — QC <c>sprint(client, text)</c>. The chat engine
    /// (<see cref="Chat"/>) routes each recipient's line through this so team/private/spectator/ignore filtering
    /// happens BEFORE delivery (the routing is in <see cref="Chat.Say"/>, not the net layer). The host wires it to
    /// <c>ServerNet.SendChatToPlayer</c>. When unwired, <see cref="Chat.Say"/> still computes routing (and the
    /// public-say case falls back to <see cref="ChatHandler"/> so a bare host without the per-player wire keeps
    /// broadcasting public chat as before — see <see cref="DeliverChatLine"/>).
    /// </summary>
    public Action<Player, string>? ChatToPlayer { get; set; }

    /// <summary>[T46] Optional server-console echo sink — QC <c>dedicated_print(text)</c>. The chat engine echoes
    /// each delivered public/team/private line to the SERVER console through this (distinct from a client print).</summary>
    public Action<string>? ChatConsole { get; set; }

    /// <summary>
    /// [T46] The server chat engine (port of server/chat.qc <c>Say</c>) — owns formatmessage, per-say-type flood,
    /// recipient routing and ignore/mute handling. The say/say_team/tell/ignore/unignore/clear_ignores commands
    /// route through it. Created lazily in the ctor (it needs the live <see cref="GameWorld"/>).
    /// </summary>
    public Chat Chat { get; }

    /// <summary>Optional sink the host wires to add bots (QC bot_cmd add). Args: name, skill.</summary>
    public Func<string?, float?, bool>? AddBotHandler { get; set; }

    /// <summary>Optional sink the host wires to remove a bot by name (QC bot_remove).</summary>
    public Func<string?, bool>? RemoveBotHandler { get; set; }

    /// <summary>Optional sink the host wires to load a named map (QC changelevel). Args: map name.</summary>
    public Action<string>? ChangeLevelHandler { get; set; }

    /// <summary>
    /// Optional sink the host wires to shut the server down (QC <c>localcmd("quit\n")</c>). Invoked at match end
    /// by the <c>quit_when_empty</c> override in <see cref="GameWorld.DriveEndOfMatchMapFlow"/>. When unwired the
    /// decision is still taken (no map change happens) but the process keeps running.
    /// </summary>
    public Action? QuitHandler { get; set; }

    /// <summary>
    /// Optional sink the host wires to redirect every client to another server at match end (QC
    /// <c>redirection_target</c> → the per-client <c>reconnect</c> stuffcmd). Arg: the target server address from
    /// <c>quit_and_redirect</c>. When unwired the redirect is a no-op (no map change happens either).
    /// </summary>
    public Action<string>? RedirectHandler { get; set; }

    /// <summary>
    /// Optional sink the host wires for the single-player <c>lastlevel</c> override (QC
    /// <c>cvar_settemp_restore(); localcmd("set lastlevel 0\ntogglemenu 1\n")</c>) — show the menu after the
    /// final campaign/last map instead of rotating. When unwired the decision is taken but no menu opens.
    /// </summary>
    public Action? LastLevelHandler { get; set; }

    /// <summary>
    /// QC <c>localcmd("\nsv_vote_gametype_hook_all\n")</c> and
    /// <c>localcmd("\nsv_vote_gametype_hook_", gametype_string, "\n")</c> from <c>GameTypeVote_SetGametype</c>:
    /// called twice after the gametype vote finishes — once with <c>"all"</c> and once with the winning gametype
    /// name (e.g. <c>"dm"</c>). The host may wire this to execute any per-gametype server-side alias. When
    /// unwired the switch still happens (gametype cvar is set), but the aliases don't run.
    /// </summary>
    public Action<string>? GameTypeVoteHookHandler { get; set; }

    /// <summary>
    /// QC <c>localcmd("\nsv_hook_gameend\n")</c> from <c>NextLevel</c> (server/world.qc:1463): the trailing
    /// admin-script alias fired once per match the instant intermission begins, after the MatchEnd mutator hook.
    /// The host may wire this to execute the server-side <c>sv_hook_gameend</c> console alias. The stock alias is
    /// empty (a no-op, matching Base); a server.cfg that redefines it runs against the server's cvar store. When
    /// unwired the end-of-match latch still happens; only the alias body doesn't run.
    /// </summary>
    public Action? GameEndHookHandler { get; set; }

    /// <summary>
    /// The sim-clock deferred-command queue (DP <c>defer</c> / <c>Cbuf_Execute_Deferred</c>). Backs the
    /// <c>defer</c> + <c>nextframe</c> commands; pumped each server tick by <c>GameWorld.OnStartFrame</c>
    /// (<c>Commands.Deferred.Pump(Time, cmd =&gt; Commands.Execute(cmd, isServerConsole: true))</c>). The passed
    /// <c>restart</c> vote enqueues <c>defer 1 restart</c> here.
    /// </summary>
    public DeferredCommands Deferred { get; } = new();

    /// <summary>
    /// The precomputed common-command reply strings (QC getreplies.qc): printmaplist/lsmaps/records/rankings/
    /// ladder/the monster list + cvar_changes/cvar_purechanges. Rebuilt by <c>GameWorld</c> after boot/map-load
    /// via <see cref="CommandReplies.Recompute"/>; the reply commands read the cached fields.
    /// </summary>
    public CommandReplies Replies { get; }

    /// <summary>QC <c>CS_CVAR(caller).cvar_cl_autoswitch</c>: the per-client auto-weapon-switch flag, kept off the
    /// shared Player type (a small per-player table here). Set by the <c>autoswitch</c> command (and synced by a
    /// replicated <c>sentcvar cl_autoswitch</c>), read by the pickup→bestweapon logic when that consults it.
    /// Default unset == 0 (off) until the client sends it.</summary>
    private readonly Dictionary<Player, bool> _autoswitch = new();

    /// <summary>QC <c>CS(caller).version</c>: the client's reported game version (the internal <c>clientversion</c> command).</summary>
    private readonly Dictionary<Player, float> _clientVersion = new();

    /// <summary>
    /// The per-client replicated-cvar store — the C# successor to QC's <c>CS_CVAR(player).cvar_cl_*</c> fields
    /// (lib/replicate.qh: the SVQC side of REPLICATE keeps each client's pushed cvar values on its clientstate
    /// entity). Written ONLY by the caller-gated <c>sentcvar</c> command (and the <c>physics</c>/<c>autoswitch</c>
    /// verbs that share state with it) — NEVER the world cvar store, which would be the T47 privilege-separation
    /// hole. Keyed by Player; dropped in <see cref="ForgetPlayer"/> on disconnect.
    /// </summary>
    private readonly Dictionary<Player, Dictionary<string, string>> _clientCvars = new();

    /// <summary>
    /// The cvars a client may push via <c>cmd sentcvar</c> — the ported slice of the QC REPLICATE registrations
    /// (common/replicate.qh:15-23 + physics/player.qc:7-9) that have live consumers here. Anything else is
    /// silently ignored (QC stores only known REPLICATE fields; an unknown name writes nothing).
    /// </summary>
    private static readonly HashSet<string> SentCvarAllowlist = new(StringComparer.Ordinal)
    {
        "cl_weaponpriority",
        "cl_weaponpriority0", "cl_weaponpriority1", "cl_weaponpriority2", "cl_weaponpriority3",
        "cl_weaponpriority4", "cl_weaponpriority5", "cl_weaponpriority6", "cl_weaponpriority7",
        "cl_weaponpriority8", "cl_weaponpriority9",
        "cl_autoswitch", "cl_autoswitch_cts",
        "cl_noantilag",
        "cl_physics",
        "cl_movement_track_canjump",
        "cl_jetpack_jump",
        // Dodging per-client opt-in (common/mutators/mutator/dodging/cl_dodging.qc:3-4 REPLICATE). cl_dodging is
        // the sv_dodging_clientselect opt-in flag; cl_dodging_timeout is the double-tap window. Wired below via
        // DodgingMutator.ClientDodgingProvider / TimeoutProvider.
        "cl_dodging", "cl_dodging_timeout",
        // Multijump per-client opt-in/out/cap (common/mutators/mutator/multijump/multijump.qc:5 +
        // multijump.qh:4 REPLICATE_INIT(int, cvar_cl_multijump)). -1 = use the g_multijump_client server
        // default; 0 = opt out; >1 = cap off for me. Wired below via MultijumpMutator.ClientMultijumpProvider.
        "cl_multijump",
        // Remaining Base REPLICATE fields (common/replicate.qh): stored per-client so the server can honor them
        // exactly like QC (every REPLICATE field lands in the per-client store). cl_handicap(+damage_given/taken)
        // feed Handicap_GetVoluntaryHandicap (wired below via VoluntaryHandicapProvider); cl_clippedspectating,
        // cl_autoscreenshot and g_xonoticversion are read by their own server-side consumers as those land.
        "cl_autoscreenshot",
        "cl_clippedspectating",
        "cl_handicap", "cl_handicap_damage_given", "cl_handicap_damage_taken",
        "g_xonoticversion",
    };

    /// <summary>QC <c>notification_CHOICE_*</c> REPLICATE prefix (notifications/all.qh:884 ReplicateVars). The full
    /// set is data-driven (one cvar per registered MSG_CHOICE notification), so the gate matches the prefix and
    /// then confirms the trailing name resolves to a registered choice cvar before accepting — never an arbitrary
    /// <c>notification_*</c> string (privilege-separation: an attacker-controlled wire name must reach only the
    /// per-client choice store, never the world cvar table).</summary>
    private const string ChoiceCvarPrefix = "notification_";

    /// <summary>True if <paramref name="name"/> is a registered MSG_CHOICE replication cvar (QC the per-choice
    /// <c>notification_&lt;CHOICE_NAME&gt;</c> the client pushes via ReplicateVars). Built lazily from the
    /// notification registry so it tracks exactly the choices that exist.</summary>
    private static bool IsRegisteredChoiceCvar(string name)
    {
        if (!name.StartsWith(ChoiceCvarPrefix, StringComparison.Ordinal))
            return false;
        return RegisteredChoiceCvarNames.Contains(name);
    }

    // The set of notification_<CHOICE> cvar names, one per registered MSG_CHOICE notification (team variants
    // collapse to one shared cvar via ChoiceCvarName). Built LAZILY on first use — the notification registry is
    // populated by Notifications.RegisterAll() at GameInit, which is guaranteed to run before any client can send
    // a sentcvar, but AFTER this type may be statically touched; a static-init build would capture an empty set.
    private static HashSet<string>? _registeredChoiceCvarNames;
    private static HashSet<string> RegisteredChoiceCvarNames
    {
        get
        {
            // Rebuild if the registry has grown since we last cached (covers tests that register late).
            if (_registeredChoiceCvarNames is null)
            {
                var s = new HashSet<string>(StringComparer.Ordinal);
                foreach (var n in Notifications.All)
                    if (n.Type == MsgType.Choice)
                        s.Add(ChoiceCvarPrefix + NotificationChoiceState.ChoiceCvarName(n));
                _registeredChoiceCvarNames = s;
            }
            return _registeredChoiceCvarNames;
        }
    }

    /// <summary>QC <c>CS(caller).msg_choice_choices[]</c> (notifications/all.qh:720): each client's replicated
    /// MSG_CHOICE selections, populated by the <c>sentcvar notification_CHOICE_*</c> receive leg. The host reads
    /// this (via <see cref="GetChoiceState"/>) to feed the per-recipient notification dispatch. Dropped in
    /// <see cref="ForgetPlayer"/> on disconnect.</summary>
    private readonly Dictionary<Player, NotificationChoiceState> _choiceState = new();

    /// <summary>The replicated MSG_CHOICE selections for a client (QC <c>CS(p).msg_choice_choices</c>), or null
    /// if the client has sent none. Lets the host drive the notification dispatch from per-client preferences.</summary>
    public NotificationChoiceState? GetChoiceState(Player p)
        => _choiceState.TryGetValue(p, out var st) ? st : null;

    /// <summary>Store a received <c>notification_&lt;CHOICE&gt;</c> value into the caller's per-client choice array
    /// (QC <c>CS(this).msg_choice_choices[it.nent_choice_idx] = …</c>). Resolves the choice index by matching the
    /// cvar name to the registered choice's collapsed cvar name.</summary>
    private void StoreChoiceCvar(Player p, string cvarName, string value)
    {
        if (!_choiceState.TryGetValue(p, out var st))
        {
            _choiceState[p] = st = new NotificationChoiceState();
            // QC's client replicates EVERY notification_CHOICE_* cvar (cfg-seeded default 1); this port's client
            // skips pushing unset/unchanged ones, so seed the QC default (option A / terse) here. Choices the
            // client never pushes then resolve to A instead of the zero-init suppress — without this a client who
            // customized only some choices would have the rest silently suppressed during warmup.
            st.FillDefault(NotificationChoiceState.OptionA);
        }
        int v = int.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int parsed)
            ? parsed : NotificationChoiceState.OptionA;
        foreach (var n in Notifications.All)
        {
            if (n.Type != MsgType.Choice) continue;
            if (cvarName == ChoiceCvarPrefix + NotificationChoiceState.ChoiceCvarName(n))
                st.Set(n.ChoiceIdx, v);
        }
    }

    /// <summary>QC <c>mapvote_maps_suggestions</c> / <c>mapvote_maps_suggesters</c>: maps players suggested for the
    /// end-of-match ballot (the <c>suggestmap</c> command), each paired with the suggester's netname (QC
    /// <c>mapvote_maps_suggesters[i] = this.netname</c>). Deduped by map, order kept.</summary>
    private readonly List<(string Map, string Suggester)> _mapSuggestions = new();

    /// <summary>QC the per-client auto-switch flag read (default off). Exposed so the pickup path can honor it.</summary>
    public bool GetAutoswitch(Player p) => _autoswitch.TryGetValue(p, out bool v) && v;

    /// <summary>QC <c>CS_CVAR(p).cvar_&lt;name&gt;</c>: the per-client replicated cvar value, or
    /// <paramref name="fallback"/> when the client never sent it.</summary>
    public string GetClientCvar(Player p, string name, string fallback = "")
        => _clientCvars.TryGetValue(p, out var t) && t.TryGetValue(name, out string? v) ? v : fallback;

    /// <summary>Boolean read of a per-client replicated cvar (QC <c>boolean(stoi(...))</c> semantics via
    /// <see cref="InterpretBoolean"/>).</summary>
    public bool GetClientCvarBool(Player p, string name, bool fallback = false)
        => _clientCvars.TryGetValue(p, out var t) && t.TryGetValue(name, out string? v) ? InterpretBoolean(v) : fallback;

    /// <summary>Float read of a per-client replicated cvar (QC <c>stof</c> — unparsable → 0).</summary>
    public float GetClientCvarFloat(Player p, string name, float fallback = 0f)
        => _clientCvars.TryGetValue(p, out var t) && t.TryGetValue(name, out string? v)
            ? (float.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f)
            : fallback;

    /// <summary>Parse the X and Y of a QC vector cvar string (<c>"x y z"</c>, e.g. the legacy <c>cl_handicap</c>
    /// <c>'2 0 0'</c>); missing/unparsable components → 0 (QC stov semantics). Z is unused.</summary>
    private static (float x, float y) ParseHandicapVectorXY(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (0f, 0f);
        string[] parts = value.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
        float Comp(int i) => i < parts.Length
            && float.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f;
        return (Comp(0), Comp(1));
    }

    private void SetClientCvar(Player p, string name, string value)
    {
        if (!_clientCvars.TryGetValue(p, out var t))
            _clientCvars[p] = t = new Dictionary<string, string>(StringComparer.Ordinal);
        t[name] = value;
    }

    /// <summary>
    /// QC <c>vercmp</c> / <c>vercmp_recursive</c> (lib/string.qh:543-572): compare two version strings token by
    /// token (split on '.'). For each token pair the QC algorithm is: first the numeric difference
    /// <c>stof(s1) - stof(s2)</c> (QC <c>stof</c> parses the leading number, non-numeric → 0); if that ties, a
    /// case-insensitive <c>strcasecmp</c>; if that ties too, the side that ran out of tokens first is smaller
    /// (so "0.8" &lt; "0.8.6"). Returns &lt;0 if lhs &lt; rhs, 0 if equal, &gt;0 if lhs &gt; rhs. (The QC
    /// <c>"git"</c> early-outs in <c>vercmp</c> are handled by the caller's strstrofs git checks before this
    /// runs, so they are intentionally not duplicated here.)
    /// </summary>
    private static int VersionCompare(string lhs, string rhs)
    {
        // QC vercmp early-out: strcasecmp(v1, v2) == 0.
        if (string.Equals(lhs, rhs, StringComparison.OrdinalIgnoreCase))
            return 0;
        return VersionCompareRecursive(lhs, rhs);
    }

    /// <summary>QC <c>vercmp_recursive</c> (lib/string.qh:543): one token (up to the first '.') of each side,
    /// numeric-then-case-insensitive compare, recurse on the remainder.</summary>
    private static int VersionCompareRecursive(string v1, string v2)
    {
        int dot1 = v1.IndexOf('.');
        int dot2 = v2.IndexOf('.');
        string s1 = dot1 == -1 ? v1 : v1.Substring(0, dot1);
        string s2 = dot2 == -1 ? v2 : v2.Substring(0, dot2);

        // QC: float r = stof(s1) - stof(s2); if (r != 0) return r;
        float r = QcStof(s1) - QcStof(s2);
        if (r < 0) return -1;
        if (r > 0) return 1;

        // QC: r = strcasecmp(s1, s2); if (r != 0) return r;
        int c = string.Compare(s1, s2, StringComparison.OrdinalIgnoreCase);
        if (c != 0) return c;

        // QC token-count tiebreak: the side with no remaining '.' token is smaller.
        if (dot1 == -1)
            return (dot2 == -1) ? 0 : -1;
        if (dot2 == -1)
            return 1;
        return VersionCompareRecursive(v1.Substring(dot1 + 1), v2.Substring(dot2 + 1));
    }

    /// <summary>QC <c>stof</c> semantics: parse the leading numeric prefix of a string; any non-numeric content
    /// (or an empty string) yields 0.</summary>
    private static float QcStof(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0f;
        int i = 0;
        if (s[0] == '+' || s[0] == '-') i = 1;
        int start = i;
        bool dot = false;
        while (i < s.Length && (char.IsDigit(s[i]) || (s[i] == '.' && !dot)))
        {
            if (s[i] == '.') dot = true;
            i++;
        }
        if (i == start) return 0f; // no digits after the optional sign
        string num = s.Substring(0, i);
        return float.TryParse(num, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f;
    }

    /// <summary>
    /// QC PlayerPreThink version-nagging logic (server/client.qc:2925-2943): if the client's
    /// <c>g_xonoticversion</c> differs from the server's, send a version-mismatch notification. Called once
    /// per player session when the version_nagtime timer fires. Does nothing if the client hasn't sent its
    /// version string yet (the g_xonoticversion replicated cvar is empty).
    /// </summary>
    public void CheckClientVersion(ClientManager.ClientInfo client, float serverTime)
    {
        if (client.VersionNagTime == 0f || serverTime <= client.VersionNagTime)
            return; // Timer not armed or not yet fired.
        client.VersionNagTime = 0f; // Fire once only.

        Player p = client.Player;
        string clientVersion = GetClientCvar(p, "g_xonoticversion", "");
        if (string.IsNullOrEmpty(clientVersion))
            return; // Client hasn't sent version yet — nothing to compare.

        string serverVersion = Api.Cvars.GetString("g_xonoticversion") ?? "";
        if (string.IsNullOrEmpty(serverVersion))
            return; // Server has no version set — skip check.

        // Check if either client or server is a git/autobuild version; if so, send the BETA notification.
        // QC: strstrofs(clientVersion, "git", 0) >= 0 || strstrofs(clientVersion, "autobuild", 0) >= 0
        bool clientIsGit = clientVersion.Contains("git", StringComparison.OrdinalIgnoreCase)
            || clientVersion.Contains("autobuild", StringComparison.OrdinalIgnoreCase);
        bool serverIsGit = serverVersion.Contains("git", StringComparison.OrdinalIgnoreCase)
            || serverVersion.Contains("autobuild", StringComparison.OrdinalIgnoreCase);

        if (clientIsGit)
        {
            // Git client — no notification (QC has an empty case here).
        }
        else if (serverIsGit)
        {
            // Git server with a release client — send BETA notification.
            NotificationSystem.Send(NotifBroadcast.OneOnly, p, MsgType.Info, "VERSION_BETA",
                serverVersion, clientVersion);
        }
        else
        {
            // Both are release versions — compare them.
            int cmp = VersionCompare(clientVersion, serverVersion);
            if (cmp < 0) // Client is older than server.
                NotificationSystem.Send(NotifBroadcast.OneOnly, p, MsgType.Info, "VERSION_OUTDATED",
                    serverVersion, clientVersion);
            else if (cmp > 0) // Client is newer than server.
                NotificationSystem.Send(NotifBroadcast.OneOnly, p, MsgType.Info, "VERSION_OLD",
                    serverVersion, clientVersion);
            // else: versions match — no notification.
        }
    }

    /// <summary>
    /// Drop every per-client table entry for a departed player (the QC clientstate entity being freed on
    /// disconnect). The net layer calls this from its disconnect/bot-removal paths so the dictionaries don't
    /// retain dead Player references across a long-running server.
    /// </summary>
    public void ForgetPlayer(Player p)
    {
        _clientCvars.Remove(p);
        _autoswitch.Remove(p);
        _clientVersion.Remove(p);
        _choiceState.Remove(p);
        // Drop any waypoints this player deployed (personal pings never time out on their own) so a disconnecting
        // player doesn't leak a forever-marker — QC WaypointSprite_ClearOwned runs on the player's removal.
        Waypoints.WaypointSprites.ClearOwned(p);
    }

    public Commands(GameWorld world)
    {
        _world = world;
        Replies = new CommandReplies(world);
        Chat = new Chat(world); // [T46] the chat engine (registers its g_chat_* cvar defaults on construction)
        RegisterClientCommandCvars();
        RegisterBuiltins();

        // Per-client weapon priority (T54): point the selection code's per-player priority source at this
        // world's replicated-cvar table (QC w_getbestweapon reading CS_CVAR(this).cvar_cl_weaponpriority).
        // The stored value is already fixed-up NUMBER form (see CmdSentCvar); "" → Inventory falls back to the
        // global cvar read, so a player that never replicated behaves exactly as before. Static: the latest
        // world's Commands owns it (a torn-down world's players simply read "" → fallback).
        Inventory.PriorityProvider = e => e is Player p ? GetClientCvar(p, "cl_weaponpriority", "") : null;

        // Per-client weapon autoswitch-on-pickup (QC Item_GiveTo head/tail reads CS_CVAR(player).cvar_cl_autoswitch
        // / cvar_cl_autoswitch_cts + g_cts). Point the pickup path's seams at this world's per-client state exactly
        // like PriorityProvider above — without this wiring AutoswitchEnabled() is always false and picking up a
        // better weapon never auto-switches in a real match. Non-Player entities (never reached for a real toucher)
        // read off. cl_autoswitch_cts unset defaults to -1 (QC client/main.qc registercvar "-1") = "use normal".
        ItemPickupRules.AutoswitchProvider = e => e is Player p && GetAutoswitch(p);
        ItemPickupRules.CtsAutoswitchProvider = e =>
            e is Player p ? (int)GetClientCvarFloat(p, "cl_autoswitch_cts", -1f) : -1;
        ItemPickupRules.CtsActiveProvider = () => _world.GameType is Cts;

        // QC HANDICAP_DISABLED() (server/handicap.qh:57) == IS_GAMETYPE(CTS) || IS_GAMETYPE(RACE): the whole
        // handicap subsystem (incl. the dynamic_handicap mutator) is hard-disabled in CTS/RACE. The port never
        // stamps g_cts/g_race, so point the mutator's gate at the live GameType object (same seam as above).
        DynamicHandicapMutator.HandicapDisabledProvider = () => _world.GameType is Cts or Race;

        // QC Handicap_GetVoluntaryHandicap(player, receiving) (server/handicap.qc): the per-client self-imposed
        // handicap from the REPLICATE'd cl_handicap*/damage_{given,taken} cvars (now allowlisted + stored above),
        // multiplied into the damage handicap by DamageSystem.HandicapTotal. Same forwards-compat vector branch as
        // QC: prefer the new scalar cvars, fall back to the legacy cl_handicap vector (.y = take, .x = give), bound
        // to [1, 10]. HANDICAP_DISABLED() (CTS/RACE) → 1.
        XonoticGodot.Common.Gameplay.Damage.DamageSystem.VoluntaryHandicapProvider = (e, receiving) =>
        {
            if (_world.GameType is Cts or Race) return 1f; // HANDICAP_DISABLED()
            if (e is not Player p) return 1f;
            // cl_handicap is a vector "x y z" string; parse x (give) / y (take). Missing → 0.
            (float hx, float hy) = ParseHandicapVectorXY(GetClientCvar(p, "cl_handicap", ""));
            float value;
            if (receiving)
            {
                float taken = GetClientCvarFloat(p, "cl_handicap_damage_taken", 0f);
                value = taken > 1f ? taken : (hy > 0f ? hy : hx);
            }
            else
            {
                float given = GetClientCvarFloat(p, "cl_handicap_damage_given", 0f);
                value = given > 1f ? given : hx;
            }
            return System.Math.Clamp(value, 1f, 10f);
        };

        // Per-client multijump opt-in/out/cap (QC multijump.qc:29-34 PHYS_MULTIJUMP_CLIENT(player) =
        // CS_CVAR(player).cvar_cl_multijump, a REPLICATE'd field). Point the mutator's per-client source at this
        // world's replicated-cvar table, same seam as PriorityProvider/CtsAutoswitchProvider above. Without this
        // wiring the value is always -1 ("server decides") and a player who sets cl_multijump 0 (opt out) or 2+
        // (disable) is ignored. Unset defaults to -1 (mutators.cfg:473 seta cl_multijump -1). Non-Player entities
        // (never reached for a real player) read -1 = "server decides".
        MultijumpMutator.ClientMultijumpProvider = e =>
            e is Player p ? (int)GetClientCvarFloat(p, "cl_multijump", -1f) : -1;

        // Per-client dodging opt-in (QC dodging/sv_dodging.qc:49/54 PHYS_DODGING_ENABLED(s) =
        // CS_CVAR(s).cvar_cl_dodging, a REPLICATE'd bool). Consulted by PM_dodging only when
        // sv_dodging_clientselect is set. Point the mutator's per-client source at this world's replicated-cvar
        // table — same seam as ClientMultijumpProvider above. Without it the gate always reads the server-side
        // cl_dodging, so every client shares one value instead of opting in individually. Unset defaults to false
        // (mutators.cfg:12 seta cl_dodging 0). Non-Player entities (never reached for a real player) read false.
        DodgingMutator.ClientDodgingProvider = e =>
            e is Player p && GetClientCvarFloat(p, "cl_dodging", 0f) != 0f;

        // Per-client double-tap window (QC dodging/sv_dodging.qc:50 PHYS_DODGING_TIMEOUT(s) =
        // CS_CVAR(s).cvar_cl_dodging_timeout, a REPLICATE'd float). Base reads the timeout off the dodging
        // player's own replicated cl_dodging_timeout each frame, so each client sets their own window. Point the
        // mutator's per-client source at this world's replicated-cvar table — same seam as ClientDodgingProvider
        // above. Without it the timeout is read once server-side at Hook() and shared by every client. Unset
        // defaults to 0.2 (mutators.cfg seta cl_dodging_timeout 0.2). Non-Player entities read the same default.
        DodgingMutator.TimeoutProvider = e =>
            e is Player p ? GetClientCvarFloat(p, "cl_dodging_timeout", 0.2f) : 0.2f;

        // Per-client free-fly spectator wall-collision (QC server/client.qc:2589
        // wouldclip = CS_CVAR(this).cvar_cl_clippedspectating). Point ClientManager's free-fly movetype recompute at
        // this world's replicated-cvar store: a spectator who sets cl_clippedspectating 1 collides with the world
        // (MOVETYPE_FLY_WORLDONLY) instead of the default noclip pass-through. Already allowlisted + stored
        // (SentCvarAllowlist) — this is the missing server-side CONSUMER. Unset defaults to 0 (false → noclip), the
        // Base default. Non-Player entities (never a real spectator) read false.
        ClientManager.ClippedSpectatingProvider = p => GetClientCvarFloat(p, "cl_clippedspectating", 0f) != 0f;
    }

    /// <summary>
    /// [T47] Register the per-client command-flood antispam cvar defaults (port of commands.cfg:155-156, read in
    /// server/command/cmd.qc SV_ParseClientCommand as <c>autocvar_sv_clientcommand_antispam_time</c> /
    /// <c>_count</c>). Idempotent through <see cref="Cvars.Register"/> (an already-set value is kept), the same
    /// boot-time pattern <see cref="Chat"/> uses for its g_chat_* defaults. Defaults match Base exactly:
    /// time 1.0s, count 8.
    /// </summary>
    private static void RegisterClientCommandCvars()
    {
        Cvars.Register("sv_clientcommand_antispam_time", "1");
        Cvars.Register("sv_clientcommand_antispam_count", "8");
    }

    /// <summary>The registered commands (read-only), e.g. for a <c>help</c>/completion dump.</summary>
    public IReadOnlyCollection<ConsoleCommand> All => _commands.Values;

    /// <summary>QC <c>SERVER_COMMAND(name, help)</c>: register (or replace) a command handler.</summary>
    public void Register(string name, string help, Func<CommandContext, bool> handler)
        => _commands[name] = new ConsoleCommand(name, help, handler);

    /// <summary>Remove a command (host teardown / replacing a builtin).</summary>
    public bool Unregister(string name) => _commands.Remove(name);

    public bool Has(string name) => _commands.ContainsKey(name);

    // =============================================================================================
    // dispatch (QC SV_ParseServerCommand: tokenize, look up, invoke)
    // =============================================================================================

    /// <summary>
    /// Tokenize and dispatch one command line (QC <c>tokenize_console</c> → command lookup → handler). The
    /// first token is the command name (case-insensitive). Returns a context carrying the handler output;
    /// <see cref="CommandContext.Output"/> reports "unknown command" if nothing matched. <paramref name="caller"/>
    /// is the issuing player for a client command (null = server console / rcon).
    /// </summary>
    public CommandContext Execute(string commandLine, bool isServerConsole = true, Player? caller = null)
    {
        string[] argv = Tokenize(commandLine);
        var ctx = new CommandContext(argv, isServerConsole, caller);
        if (argv.Length == 0)
            return ctx;

        // [T47] Privilege separation (port of SV_ParseClientCommand's SERVER_COMMANDS vs CLIENT_COMMANDS split,
        // server/command/cmd.qc + reg.qh). A REMOTE-client-issued command — the SV_ParseClientCommand path: a
        // non-console source (!isServerConsole) carrying a caller — may ONLY invoke a verb in the CLIENT_COMMANDS /
        // client-reachable common / cheat allowlist. The admin SERVER_COMMANDS (kick/ban/map/gotomap/endmatch/
        // settemp/set/seta/toggle/rcon-style verbs) and the generic cvar-reflection family are server-console/rcon
        // only; reject them when a client tries, matching QC where SV_ParseClientCommand never routes a client
        // through the SERVER_COMMAND / generic registries.
        //
        // The boundary is isServerConsole (the rcon/console-trust flag), NOT merely a non-null caller: the local
        // listen-server OPERATOR is the server console and runs with full privilege while still carrying its
        // LocalServerPlayer as caller (so kill/say/team act on the host's player). The remote-client net path
        // (ServerNet.HandleClientCommand) passes isServerConsole:false, so it is gated; the host console must pass
        // isServerConsole:true to keep admin power (see the integrationContract host wire-up note). The server's
        // own caller-bearing internal invocations (impulse on the input path, sentcvar) are allowlisted, so they
        // pass either way.
        if (!isServerConsole && caller is not null && !ClientCommandRegistry.IsClientCallable(argv[0]))
        {
            ctx.Print($"Unknown command \"{argv[0]}\"");
            return ctx;
        }

        // QC SV_ParseClientCommand (server/command/cmd.qc:1181): "if we're banned, don't even parse the command" —
        // Ban_MaybeEnforceBanOnce(this) re-checks the ban list on a remote client's command, not just at connect, so
        // a ban INSERTED after the client connected (e.g. an admin `ban`s them mid-session, or the list changed on a
        // map carry-over) still drops them the moment they next act. A no-op for an unbanned client; for a banned one
        // it notifies + drops and we must stop parsing (the client entity is gone). Server-console/rcon is exempt.
        if (!isServerConsole && caller is not null && _world.Bans.MaybeEnforceBan(caller))
            return ctx;

        if (_commands.TryGetValue(argv[0], out ConsoleCommand? cmd))
        {
            try { cmd.Handler(ctx); }
            catch (Exception ex) { ctx.Print($"command '{argv[0]}' failed: {ex.Message}"); }
        }
        else if (isServerConsole && Cvars.Has(argv[0]))
        {
            // QC/DarkPlaces Cmd_ExecuteString fallthrough: a token that is not a registered command but names a
            // cvar reads it (no value given) or sets it (value given). Restricted to the trusted console / rcon /
            // vote-execution path (isServerConsole) — a remote client naming a cvar was already rejected above as
            // a non-client-callable verb, so this can't become an arbitrary client cvar-set. This is what makes
            // cvar-style votes actually apply: `fraglimit`/`timelimit` (and admin-added cvar votes) are in the
            // default sv_vote_commands whitelist, and a passed vote runs through here as `<cvar> <value>`.
            if (ctx.ArgCount >= 2)
                Cvars.Set(argv[0], ctx.ArgTail(1));
            else
                ctx.Print($"\"{argv[0]}\" is \"{Cvars.String(argv[0])}\"");
        }
        else
        {
            ctx.Print($"Unknown command \"{argv[0]}\"");
        }
        return ctx;
    }

    /// <summary>
    /// QC <c>tokenize_console</c> (the relevant slice): split on whitespace, honoring double-quoted tokens so
    /// <c>say "hello world"</c> is two tokens. Backslash escapes are not handled (QC tokenize_console keeps
    /// them literal in most server commands; the few that need it read fullspawndata instead).
    /// </summary>
    public static string[] Tokenize(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return System.Array.Empty<string>();

        var tokens = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0)
            tokens.Add(sb.ToString());
        return tokens.ToArray();
    }

    // =============================================================================================
    // builtin commands (QC the common GameCommand_* + generic cvar commands)
    // =============================================================================================

    private void RegisterBuiltins()
    {
        // ---- cvar reflection (QC common/command/generic.qc set/seta/toggle + cvar) ----
        Register("set", "set <cvar> <value> — set a console variable", CmdSet);
        Register("seta", "seta <cvar> <value> — set + archive a console variable", CmdSet);
        Register("cvar", "cvar <name> [value] — get or set a console variable", CmdCvar);
        Register("toggle", "toggle <cvar> [a b ...] — cycle a cvar through values (default 0/1)", CmdToggle);

        // ---- match flow (QC GameCommand restart/endmatch/reducematchtime/extendmatchtime) ----
        Register("restart", "restart — restart the match (warmup → countdown → live)", CmdRestart);
        Register("endmatch", "endmatch — end the current match immediately (go to intermission)", CmdEndMatch);
        // QC GameCommand_reduce/extendmatchtime: delta = ±timelimit_increment/decrement*60s (sv_cmd.qc:752/1297).
        Register("reducematchtime", "reducematchtime — reduce the match time limit",
            ctx => ChangeMatchTime(ctx, -Cvars.FloatOr("timelimit_decrement", 5f) * 60f));
        Register("extendmatchtime", "extendmatchtime — extend the match time limit",
            ctx => ChangeMatchTime(ctx, +Cvars.FloatOr("timelimit_increment", 5f) * 60f));

        // ---- players / chat (QC GameCommand kick + the say bus) ----
        Register("kick", "kick <player> [reason] — remove a player from the server", CmdKick);
        Register("say", "say <message> — broadcast a chat message", CmdSay);
        // [T46] the chat family (QC server/command/cmd.qc ClientCommand_say_team/tell/ignore/unignore/clear_ignores).
        Register("say_team", "say_team <message> — send a chat message to your teammates", CmdSayTeam);
        Register("tell", "tell <client> <message> — send a private message to a player", CmdTell);
        Register("ignore", "ignore <client> — keep a player's messages out of your chat log", CmdIgnore);
        Register("unignore", "unignore <client> — stop ignoring a player", CmdUnignore);
        Register("clear_ignores", "clear_ignores — remove all of your ignores", CmdClearIgnores);

        // ---- voting (QC server/command/vote.qc the vote command family) ----
        Register("vote", "vote <call|yes|no|abstain|stop|status|master|help> [args] — match/map voting", CmdVote);

        // ---- bots (QC bot_cmd add / bot_remove / setbots) ----
        Register("bot_add", "bot_add [name] [skill] — add an AI player", CmdBotAdd);
        Register("bot_remove", "bot_remove [name] — remove a bot", CmdBotRemove);
        Register("setbots", "setbots <n> — keep this many bots on the server", CmdSetBots);
        Register("removebots", "removebots — remove all bots", CmdRemoveBots);

        // ---- map change (QC changelevel / gotomap / nextmap) ----
        Register("map", "map <name> — change to a map immediately", CmdMap);
        Register("gotomap", "gotomap <name> — queue the next map (after intermission)", CmdGotoMap);
        Register("nextmap", "nextmap [name] — get or set the next map", CmdNextMap);

        // ---- match flow (QC GameCommand allready/resetmatch/gametype/cointoss) ----
        Register("allready", "allready — end warmup and start the match now", CmdAllReady);
        Register("resetmatch", "resetmatch — soft restart back to warmup, keeping teams", CmdResetMatch);
        Register("gametype", "gametype <mode> — change the active gametype", CmdGameType);
        Register("cointoss", "cointoss [a] [b] — broadcast a random coin flip", CmdCoinToss);

        // ---- teamplay admin (QC lockteams/unlockteams/shuffleteams/moveplayer/allspec/nospectators) ----
        Register("lockteams", "lockteams — prevent team joins/switches", CmdLockTeams);
        Register("unlockteams", "unlockteams — allow team joins/switches", CmdUnlockTeams);
        Register("shuffleteams", "shuffleteams — randomly redistribute players across teams", CmdShuffleTeams);
        Register("moveplayer", "moveplayer <player> <team|spec> — move a player to a team or spectate", CmdMovePlayer);
        Register("allspec", "allspec [reason] — force all players to spectate", CmdAllSpec);
        Register("nospectators", "nospectators — temporarily disallow spectating", CmdNoSpectators);

        // ---- weapon selection / reload (QC server/impulse.qc — the impulse → selection.qc routing) ----
        // The client weapon binds send `impulse N`; ImpulseCommands routes N onto the selection/reload API.
        Register("impulse", "impulse <n> — process a client impulse (weapon switch/reload/etc.)", CmdImpulse);
        // Convenience console aliases mirroring xonotic-client.cfg's weapon_* aliases (each maps to an impulse).
        Register("weapon_next", "weapon_next — switch to the next weapon", ctx => CmdWeaponImpulse(ctx, 10));
        Register("weapon_prev", "weapon_prev — switch to the previous weapon", ctx => CmdWeaponImpulse(ctx, 12));
        Register("weaplast", "weaplast — switch to the last used weapon", ctx => CmdWeaponImpulse(ctx, 11));
        Register("weapon_last", "weapon_last — switch to the last used weapon", ctx => CmdWeaponImpulse(ctx, 11));
        Register("weapon_best", "weapon_best — switch to the best weapon", ctx => CmdWeaponImpulse(ctx, 13));
        Register("weapon_drop", "weapon_drop — drop the current weapon", ctx => CmdWeaponImpulse(ctx, 17));
        Register("reload", "reload — reload the current weapon", ctx => CmdWeaponImpulse(ctx, 20));

        // ---- player-deployed waypoints (QC server/impulse.qc IMPULSE(waypoint_*)). In DP these ride engine
        //      impulses; the port's QuickMenu/userbinds emit the named verbs through the client console, so they
        //      arrive here as client commands. Each routes to the WaypointSprites Deploy/Attach/Clear API. ----
        Register("waypoint_personal_here", "waypoint_personal_here — drop a personal waypoint at your feet",
            ctx => CmdWaypointDeploy(ctx, WpDeploy.Personal, WpLocation.Here));
        Register("waypoint_personal_crosshair", "waypoint_personal_crosshair — drop a personal waypoint",
            ctx => CmdWaypointDeploy(ctx, WpDeploy.Personal, WpLocation.Crosshair));
        Register("waypoint_personal_death", "waypoint_personal_death — drop a personal waypoint at your death spot",
            ctx => CmdWaypointDeploy(ctx, WpDeploy.Personal, WpLocation.Death));
        Register("waypoint_here_follow", "waypoint_here_follow — attach a HELP ME marker that follows you",
            ctx => CmdWaypointDeploy(ctx, WpDeploy.Helpme));
        Register("waypoint_here_here", "waypoint_here_here — drop a HERE marker at your feet",
            ctx => CmdWaypointDeploy(ctx, WpDeploy.Here, WpLocation.Here));
        Register("waypoint_here_crosshair", "waypoint_here_crosshair — drop a HERE marker",
            ctx => CmdWaypointDeploy(ctx, WpDeploy.Here, WpLocation.Crosshair));
        Register("waypoint_here_death", "waypoint_here_death — drop a HERE marker at your death spot",
            ctx => CmdWaypointDeploy(ctx, WpDeploy.Here, WpLocation.Death));
        Register("waypoint_danger_here", "waypoint_danger_here — drop a DANGER marker at your feet",
            ctx => CmdWaypointDeploy(ctx, WpDeploy.Danger, WpLocation.Here));
        Register("waypoint_danger_crosshair", "waypoint_danger_crosshair — drop a DANGER marker",
            ctx => CmdWaypointDeploy(ctx, WpDeploy.Danger, WpLocation.Crosshair));
        Register("waypoint_danger_death", "waypoint_danger_death — drop a DANGER marker at your death spot",
            ctx => CmdWaypointDeploy(ctx, WpDeploy.Danger, WpLocation.Death));
        Register("waypoint_clear_personal", "waypoint_clear_personal — clear your personal waypoint",
            ctx => CmdWaypointClear(ctx, personalOnly: true));
        Register("waypoint_clear", "waypoint_clear — clear all of your waypoints",
            ctx => CmdWaypointClear(ctx, personalOnly: false));

        // ---- per-player commands (QC ClientCommand ready/join/spectate/selectteam/kill) ----
        Register("ready", "ready — toggle your ready state during warmup", CmdReady);
        Register("join", "join — join the game as a player", CmdJoin);
        Register("spectate", "spectate — become a spectator", CmdSpectate);
        Register("selectteam", "selectteam <red|blue|yellow|pink|auto> — choose a team", CmdSelectTeam);
        // QC the engine `color` command → SV_ChangeTeam (server/teamplay.qc:1340): in DP `color`/`topcolor`/
        // `bottomcolor` are engine-handled (host_cmd.c) and call SV_ChangeTeam, which only re-colors in a NON-team
        // game. The port routes it through the command bus so the clientcolors recolor is reachable.
        Register("color", "color <pants> [shirt] — set your player color (FFA only)", CmdColor);
        Register("kill", "kill — suicide (respawn)", CmdKill);
        Register("ready_up", "ready_up — alias of ready", CmdReady);

        // ---- Onslaught click-radar spawn/teleport (QC the ons_spawn SV_ParseClientCommand hook,
        //      common/gametypes/gametype/onslaught/sv_onslaught.qc:1937). `cmd ons_spawn [x y z]` picks a control
        //      point from the radar — teleport (alive) or respawn-at (dead). A no-op outside Onslaught. ----
        Register("ons_spawn", "ons_spawn [x y z] — teleport to / respawn at the clicked control point (Onslaught)", CmdOnsSpawn);

        // ---- minigames (QC server/command/cmd.qc ClientCommands "minigame" → ClientCommand_minigame) ----
        Register("minigame", "minigame <create|join|list|list-sessions|part|end|invite> [args] — play a minigame", CmdMinigame);

        // ---- timeout / timein (QC common/command timeout) ----
        Register("timeout", "timeout — pause the match", CmdTimeout);
        Register("timein", "timein — resume from a timeout", CmdTimein);

        // ---- bans (QC command/banning.qc) ----
        Register("ban", "ban <address> [bantime] [reason] — ban an address", CmdBan);
        Register("kickban", "kickban <player> [bantime] [masksize] [reason] — kick + ban a player", CmdKickBan);
        Register("unban", "unban <banid> — lift a ban by its banlist index", CmdUnban);
        Register("banlist", "banlist — list the active bans", CmdBanList);
        Register("mute", "mute <player> — mute a player in chat", CmdMute);
        Register("unmute", "unmute <player> — unmute a player", CmdUnmute);
        Register("playban", "playban <player> — force a player to spectate", CmdPlayBan);
        Register("unplayban", "unplayban <player> — allow a play-banned player to join", ctx => CmdListRemove(ctx, "g_playban_list", "unplaybanned"));
        Register("voteban", "voteban <player> — ban a player from voting", ctx => CmdListAdd(ctx, "g_voteban_list", "voteban"));
        Register("unvoteban", "unvoteban <player> — lift a voteban", ctx => CmdListRemove(ctx, "g_voteban_list", "unvoteban"));

        // ---- anticheat (QC GameCommand_anticheat, sv_cmd.qc:232) ----
        Register("anticheat", "anticheat <client> — dump a player's anticheat verdict to the event log", CmdAnticheat);

        // ---- cheats (QC CheatCommand — routed for a client via cmd) ----
        Register("god", "god — toggle godmode (needs sv_cheats)", CmdCheat);
        Register("notarget", "notarget — toggle notarget (needs sv_cheats)", CmdCheat);
        Register("noclip", "noclip — toggle noclip (needs sv_cheats)", CmdCheat);
        Register("fly", "fly — toggle flymode (needs sv_cheats)", CmdCheat);
        Register("give", "give <all|weapon|resource amount> — give items (needs sv_cheats)", CmdCheat);
        Register("usetarget", "usetarget <targetname> — fire a named target (needs sv_cheats)", CmdCheat);
        Register("killtarget", "killtarget <targetname> — remove a named target (needs sv_cheats)", CmdCheat);
        Register("teleporttotarget", "teleporttotarget <targetname> — teleport to a named teleport target (needs sv_cheats)", CmdCheat);
        Register("pointparticles", "pointparticles <effect> <pos 0..1> <vel> <countmul> — emit a point effect on the aim line (needs sv_cheats)", CmdCheat);
        Register("trailparticles", "trailparticles <effect> — emit a trail effect along the aim line (needs sv_cheats)", CmdCheat);
        Register("make", "make <model> <mode 0|1|2> — spawn a func_breakable at the aim point (needs sv_cheats)", CmdCheat);
        Register("penalty", "penalty <duration> <reason> — impose a race penalty time (needs sv_cheats)", CmdCheat);

        // ---- sandbox (QC the SV_ParseClientCommand hook in common/mutators/mutator/sandbox/sv_sandbox.qc). The
        //      client `sandbox` alias emits `cmd g_sandbox <sub> …`; route it to the SandboxMutator's HandleCommand
        //      (the C# successor to that hook). A no-op unless g_sandbox is enabled (the mutator gates it). ----
        Register("g_sandbox", "g_sandbox <subcommand> [args] — sandbox build mode (object_spawn/edit/attach/…)", CmdSandbox);

        // ---- superspec (QC the SV_ParseClientCommand hook in common/mutators/mutator/superspec/sv_superspec.qc).
        //      The spectator-only verbs that toggle the auto-/super-spectate option flags and the manual follow*
        //      switches. Each routes to SuperSpecMutator.HandleCommand; a no-op unless g_superspectate is enabled
        //      (the mutator is added) and the caller is a spectator/observer (the mutator's IS_PLAYER guard). ----
        Register("superspec", "superspec [help|clear|silent|verbose|item_message] [on|off] — spectator message options", CmdSuperspec);
        Register("autospec", "autospec [help|clear|strength|shield|mega_health|mega_armor|flag_grab|observer_only|show_what|item_msg|followkiller|all] [on|off] — auto-spectate options", CmdSuperspec);
        Register("superspec_itemfilter", "superspec_itemfilter [help|clear|show|<classnames>] — item-message classname filter", CmdSuperspec);
        Register("followpowerup", "followpowerup — spectate the first player holding Strength or Shield", CmdSuperspec);
        Register("followstrength", "followstrength — spectate the first player holding Strength", CmdSuperspec);
        Register("followshield", "followshield — spectate the first player holding Shield", CmdSuperspec);

        // ---- campaign (QC sv_cmd warp) ----
        Register("warp", "warp [level] — campaign level warp", CmdWarp);

        // ---- settemp (QC generic settemp / settemp_restore) ----
        Register("settemp", "settemp <cvar> <value> — temporarily set a cvar (restored on map end)", CmdSettemp);
        Register("settemp_restore", "settemp_restore — restore all settemp cvars", CmdSettempRestore);

        // ---- introspection (QC GameCommand help / common/command help/who/teamstatus/time/info) ----
        Register("help", "help [command] — list commands or describe one", CmdHelp);
        Register("status", "status — print match/roster status", CmdStatus);
        Register("teamstatus", "teamstatus — print player/team scores", CmdTeamStatus);
        Register("printstats", "printstats — dump score log (non-final) to console", CmdPrintStats);
        Register("who", "who — list connected clients", CmdWho);
        Register("time", "time — print the server time readouts", CmdTime);
        Register("info", "info <request> — print an admin info string", CmdInfo);
        Register("cvar_changes", "cvar_changes — list server cvars changed from their defaults", CmdCvarChanges);
        Register("cvar_purechanges", "cvar_purechanges — list gameplay cvars changed from their defaults", CmdCvarPureChanges);

        // ---- engine `defer` (DP cmd.c CF_SHARED) — run a command after a delay; the passed `restart` vote
        //      enqueues `defer 1 restart` so the announcer/result shows before the match restarts. ----
        Register("defer", "defer <seconds> <command> | defer clear — run a command after a delay", CmdDefer);

        // ---- generic commands (DP common/command/generic.qc + rpn.qc) — ALSO registered on the server bus so a
        //      client's `cmd rpn`/`cmd maplist`/… reaches them (QC routes the generic family through both
        //      programs). The console surface (ConsoleCommands) carries the same family for cfg/menu use. ----
        Register("rpn", "rpn <expression> — RPN calculator", CmdRpn);
        Register("addtolist", "addtolist <cvar> <value> — append a value to a list cvar", CmdAddToList);
        Register("removefromlist", "removefromlist <cvar> <value> — remove a value from a list cvar", CmdRemoveFromList);
        Register("maplist", "maplist <add|remove|shuffle|cleanup> [<map>] — edit g_maplist", CmdMaplist);
        Register("nextframe", "nextframe <command> — run a command on the next server tick", CmdNextFrame);

        // ---- server client commands (QC server/command/cmd.qc ClientCommand_*) ----
        Register("voice", "voice <voicetype> [message] — play a voice taunt", CmdVoice);
        Register("suggestmap", "suggestmap <map> — suggest a map for the end-of-match vote", CmdSuggestMap);
        Register("autoswitch", "autoswitch <on|off> — auto-switch to a better weapon on pickup", CmdAutoswitch);
        Register("physics", "physics <set> — select a client physics set", CmdPhysics);
        Register("clientversion", "clientversion <version> — (internal) report the client game version", CmdClientVersion);
        Register("sentcvar", "sentcvar <cvar> <value> — (internal) a client pushing a replicated cl_* cvar", CmdSentCvar);

        // ---- common reply commands (QC server/command/common.qc — cached reply strings) ----
        Register("records", "records [<page>] — show the gametype records", CmdRecords);
        Register("rankings", "rankings — show the race rankings for this map", CmdRankings);
        Register("lsmaps", "lsmaps — list the maps available on this server", CmdLsmaps);
        Register("printmaplist", "printmaplist — print the current map rotation", CmdPrintMapList);
        Register("ladder", "ladder — show the cross-map race ladder", CmdLadder);

        // ---- monster editor (QC server/command/common.qc editmob + the spawnmob/killmob cfg aliases) ----
        Register("editmob", "editmob <butcher|spawn|skin|movetarget|kill|name> [args] — edit/spawn monsters", CmdEditMob);
        Register("spawnmob", "spawnmob <type> [moveflag] — spawn a monster (alias of editmob spawn)", ctx => CmdEditMobAlias(ctx, "spawn"));
        Register("killmob", "killmob — kill the monster you're looking at (alias of editmob kill)", ctx => CmdEditMobAlias(ctx, "kill"));

        // ---- debug/admin tools (QC server/command/sv_cmd.qc debug commands) ----
        Register("adminmsg", "adminmsg <clients> \"<message>\" [infobartime] — send an admin message to players", CmdAdminMsg);
        Register("delrec", "delrec <rank> [map] — delete race records up to a rank", CmdDelRec);
        Register("effectindexdump", "effectindexdump — dump the named-effect registry (EFFECT_* name → network id)", CmdEffectIndexDump);
    }

    private bool CmdSet(CommandContext ctx)
    {
        if (ctx.ArgCount < 3)
        {
            ctx.Print($"usage: {ctx.Arg(0)} <cvar> <value>");
            return true;
        }
        // QC: 'seta' implies the Save (archive) flag; ensure the cvar exists with that flag, then set.
        if (string.Equals(ctx.Arg(0), "seta", StringComparison.OrdinalIgnoreCase))
            Cvars.Register(ctx.Arg(1), ctx.Arg(2), CvarFlags.Save);
        Cvars.Set(ctx.Arg(1), ctx.Arg(2));
        return true;
    }

    private bool CmdCvar(CommandContext ctx)
    {
        if (ctx.ArgCount < 2)
        {
            ctx.Print("usage: cvar <name> [value]");
            return true;
        }
        string name = ctx.Arg(1);
        if (ctx.ArgCount >= 3)
        {
            Cvars.Set(name, ctx.Arg(2));
            return true;
        }
        ctx.Print($"\"{name}\" is \"{Cvars.String(name)}\"");
        return true;
    }

    private bool CmdToggle(CommandContext ctx)
    {
        if (ctx.ArgCount < 2)
        {
            ctx.Print("usage: toggle <cvar> [value1 value2 ...]");
            return true;
        }
        string name = ctx.Arg(1);
        // QC toggle: with an explicit value list, advance to the next; otherwise flip 0<->1.
        if (ctx.ArgCount > 2)
        {
            string cur = Cvars.String(name);
            int idx = -1;
            for (int i = 2; i < ctx.ArgCount; i++)
                if (ctx.Arg(i) == cur) { idx = i; break; }
            int next = idx < 0 ? 2 : (idx + 1 >= ctx.ArgCount ? 2 : idx + 1);
            Cvars.Set(name, ctx.Arg(next));
        }
        else
        {
            Cvars.Set(name, Cvars.Bool(name) ? "0" : "1");
        }
        return true;
    }

    private bool CmdRestart(CommandContext ctx)
    {
        _world.RestartMatch();
        ctx.Print("match restarted");
        return true;
    }

    private bool CmdEndMatch(CommandContext ctx)
    {
        _world.EndMatch();
        ctx.Print("match ended");
        return true;
    }

    private bool ChangeMatchTime(CommandContext ctx, float deltaSeconds)
    {
        // QC changematchtime (sv_cmd.qc:48): delta in SECONDS, clamped to [timelimit_min, timelimit_max]*60,
        // offset by the elapsed time since game_starttime, and bailing out at the unlimited / already-min/max edges.
        float mi = Cvars.FloatOr("timelimit_min", 5f) * 60f;
        float ma = Cvars.FloatOr("timelimit_max", 60f) * 60f;
        float delta = deltaSeconds;
        if (delta == 0f) return true;
        // QC: if(autocvar_timelimit < 0) return; (a negative timelimit is "no limit, cannot change").
        float limMinutes = Cvars.Float("timelimit");
        if (limMinutes < 0f) { ctx.Print("timelimit is unlimited; cannot change"); return true; }

        if (mi <= 10f) mi = 10f;                         // QC: at least ten sec in the future
        float cur = _world.Time - _world.GameStartTime;
        if (cur > 0f) mi += cur;                          // QC: from current time!

        float lim = limMinutes * 60f;
        float update;
        if (delta > 0f)
        {
            if (lim == 0f) { ctx.Print("timelimit is unlimited; cannot extend"); return true; } // QC: can't increase
            if (lim < ma) update = System.Math.Min(ma, lim + delta);
            else { ctx.Print("timelimit is already at maximum"); return true; }                  // QC: above max → FAIL
        }
        else
        {
            if (lim == 0f) update = System.Math.Max(mi, ma);   // QC: infinite → reduce to max if allowed
            else if (lim > mi) update = System.Math.Max(mi, lim + delta);
            else { ctx.Print("timelimit is already at minimum"); return true; }                  // QC: below min → FAIL
        }

        float newMinutes = update / 60f;
        Cvars.Set("timelimit", newMinutes);
        ctx.Print($"timelimit is now {newMinutes} minutes");
        return true;
    }

    private bool CmdKick(CommandContext ctx)
    {
        if (ctx.ArgCount < 2)
        {
            ctx.Print("usage: kick <player> [reason]");
            return true;
        }
        Player? target = FindPlayerByName(ctx.Arg(1));
        if (target is null)
        {
            ctx.Print($"no player matching \"{ctx.Arg(1)}\"");
            return true;
        }
        string reason = ctx.ArgTail(2);
        if (KickHandler is not null)
        {
            KickHandler(target, reason);
            ctx.Print($"kicked {target.NetName}{(string.IsNullOrEmpty(reason) ? "" : $" ({reason})")}");
        }
        else
        {
            // no host kick pipeline wired: drop them through the client manager directly.
            _world.Clients.ClientDisconnect(target);
            ctx.Print($"removed {target.NetName}");
        }
        return true;
    }

    /// <summary>
    /// QC <c>GameCommand_anticheat</c> (sv_cmd.qc:232): dump a chosen player's live anticheat verdict to the
    /// event log on demand (<c>sv_cmd anticheat &lt;client&gt;</c>). Resolves the target by entity number or name
    /// (QC <c>GetIndexedEntity</c> + <c>VerifyClientEntity</c>), then runs <c>anticheat_report_to_eventlog</c> —
    /// which (like the disconnect path) emits the <c>:anticheat:</c> lines to <see cref="GameLog"/> gated on
    /// <c>sv_eventlog</c>. Admin/console only (the privilege split rejects a remote client before this runs).
    /// </summary>
    private bool CmdAnticheat(CommandContext ctx)
    {
        if (ctx.ArgCount < 2)
        {
            ctx.Print("Usage: anticheat <client>");
            ctx.Print("  <client> is the entity number or name of the player.");
            return true;
        }
        Player? target = FindPlayerByNameOrId(ctx.Arg(1));
        if (target is null)
        {
            ctx.Print($"anticheat: no player matching \"{ctx.Arg(1)}\".");
            return true;
        }
        // QC anticheat_report_to_eventlog self-gates on autocvar_sv_eventlog; the port gates at the call site
        // exactly like the disconnect path (GameWorld.InfraClientDisconnect), feeding the same GameLog.Echo sink.
        if (Cvars.Bool("sv_eventlog"))
            _world.AntiCheat.ReportToEventLog(target, _world.Time, _world.GameLog.Echo);
        else
            ctx.Print("anticheat: sv_eventlog is disabled; enable it to record the report.");
        return true;
    }

    private bool CmdSay(CommandContext ctx)
    {
        // QC ClientCommand_say: argc >= 2 → Say(caller, false, NULL, msg, 1). [T46]
        string msg = ctx.ArgTail(1);
        if (string.IsNullOrEmpty(msg))
            return true;

        // Prefer the per-player chat engine (faithful routing/flood/ignore). When the host has NOT wired the
        // per-player delivery sink (ChatToPlayer) — a bare host or a test with only the legacy broadcast — fall
        // back to the old ChatHandler broadcast so a public `say` still reaches every client's console as before.
        if (ChatToPlayer is not null)
        {
            Chat.Say(ctx.Caller, /*teamsay*/ 0, /*privatesay*/ null, msg, /*floodcontrol*/ true);
        }
        else if (ChatHandler is not null)
        {
            // A broadcast pipeline is wired: the message reaches EVERYONE (incl. the sender) through it, so do
            // NOT also echo to ctx.Output — on a listen server the host is its own client and would see it twice.
            ChatHandler.Invoke(ctx.Caller, msg, /*teamOnly*/ false);
        }
        else
        {
            // No pipeline (a bare test / a host with no chat relay): echo locally so `say` isn't silent.
            ctx.Print($"{(ctx.Caller?.NetName ?? "server")}: {msg}");
        }
        return true;
    }

    /// <summary>QC <c>ClientCommand_say_team</c> (cmd.qc:661): argc >= 2 → Say(caller, true, NULL, msg, 1). [T46]</summary>
    private bool CmdSayTeam(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("say_team is a client command"); return true; }
        string msg = ctx.ArgTail(1);
        if (string.IsNullOrEmpty(msg))
            return true;
        Chat.Say(ctx.Caller, /*teamsay*/ 1, /*privatesay*/ null, msg, /*floodcontrol*/ true);
        return true;
    }

    /// <summary>
    /// QC <c>ClientCommand_tell</c> (cmd.qc:931): argc >= 3 → resolve the target client, then
    /// Say(caller, false, tell_to, msg, true). A connecting caller, a self-tell, or an unresolvable target each
    /// print the QC error string. The message is argv(2..) (the target token is argv(1)). [T46]
    /// </summary>
    private bool CmdTell(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("tell is a client command"); return true; }
        if (ctx.ArgCount < 3)
        {
            ctx.Print("Usage:^3 cmd tell <client> <message>");
            return true;
        }
        Player? target = FindPlayerByNameOrId(ctx.Arg(1));
        if (target is null)
        {
            ctx.Print($"tell: could not find a player matching \"{ctx.Arg(1)}\".");
            return true;
        }
        if (ReferenceEquals(target, ctx.Caller))
        {
            ctx.Print("You can't ^2tell^7 a message to yourself.");
            return true;
        }
        Chat.Say(ctx.Caller, /*teamsay*/ 0, /*privatesay*/ target, ctx.ArgTail(2), /*floodcontrol*/ true);
        return true;
    }

    /// <summary>
    /// QC <c>ClientCommand_ignore</c> (cmd.qc:452): add the named/numbered target to the caller's ignore list. The
    /// QC gate cascade: empty arg, unresolvable target, self-ignore, already-ignored, then add (and the
    /// list-full case). Keyed by PersistentId in the port — a target without one can't be ignored. [T46]
    /// </summary>
    private bool CmdIgnore(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("ignore is a client command"); return true; }
        if (ctx.ArgCount < 2 || ctx.Arg(1) == "")
        {
            ctx.Print("This command requires an argument. Use a player's name or their ID from the ^2status^7 command.");
            return true;
        }
        Player? target = FindPlayerByNameOrId(ctx.Arg(1));
        if (target is null || target.IsBot)
        {
            ctx.Print($"ignore: could not find a real player matching \"{ctx.Arg(1)}\".");
            return true;
        }
        if (ReferenceEquals(target, ctx.Caller))
        {
            ctx.Print("You can't ^2ignore^7 yourself.");
            return true;
        }
        if (Chat.IgnorePlayerInList(ctx.Caller, target))
        {
            ctx.Print($"{target.NetName} ^7is already ignored!");
            return true;
        }
        int r = Chat.IgnoreAddPlayer(ctx.Caller, target);
        if (r == 0)
            ctx.Print($"You may only ignore up to {Chat.IgnoreMaxPlayers} players, remove one before trying again.");
        else
            ctx.Print($"You will no longer receive messages from {target.NetName}^7 for this match, use ^2unignore^7 to hear them again.");
        return true;
    }

    /// <summary>QC <c>ClientCommand_unignore</c> (cmd.qc:988): remove the named/numbered target from the caller's
    /// ignore list. [T46]</summary>
    private bool CmdUnignore(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("unignore is a client command"); return true; }
        if (ctx.ArgCount < 2 || ctx.Arg(1) == "")
        {
            ctx.Print("This command requires an argument. Use a player's name or their ID from the ^2status^7 command.");
            return true;
        }
        Player? target = FindPlayerByNameOrId(ctx.Arg(1));
        if (target is null || target.IsBot)
        {
            ctx.Print($"unignore: could not find a real player matching \"{ctx.Arg(1)}\".");
            return true;
        }
        if (ReferenceEquals(target, ctx.Caller))
        {
            ctx.Print("You can't ^2unignore^7 yourself.");
            return true;
        }
        Chat.IgnoreRemovePlayer(ctx.Caller, target);
        ctx.Print($"You can now receive messages from {target.NetName} ^7again.");
        return true;
    }

    /// <summary>QC <c>ClientCommand_clear_ignores</c> (cmd.qc:226): clear the caller's whole ignore list. [T46]</summary>
    private bool CmdClearIgnores(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("clear_ignores is a client command"); return true; }
        Chat.IgnoreClearAll(ctx.Caller);
        ctx.Print("All ignores cleared!");
        return true;
    }

    private bool CmdVote(CommandContext ctx) => _world.Voting.Execute(ctx);

    private bool CmdBotAdd(CommandContext ctx)
    {
        string? name = ctx.ArgCount >= 2 ? ctx.Arg(1) : null;
        float? skill = ctx.ArgCount >= 3 ? ctx.ArgFloat(2) : null;
        bool ok = AddBotHandler?.Invoke(name, skill) ?? false;
        ctx.Print(ok ? $"added bot {name ?? "(auto)"}" : "no bot pipeline wired (set Commands.AddBotHandler)");
        return true;
    }

    private bool CmdBotRemove(CommandContext ctx)
    {
        string? name = ctx.ArgCount >= 2 ? ctx.Arg(1) : null;
        bool ok = RemoveBotHandler?.Invoke(name) ?? false;
        ctx.Print(ok ? "removed bot" : "no bot to remove");
        return true;
    }

    private bool CmdMap(CommandContext ctx)
    {
        if (ctx.ArgCount < 2)
        {
            ctx.Print("usage: map <name>");
            return true;
        }
        ChangeLevelHandler?.Invoke(ctx.Arg(1));
        ctx.Print($"changing to {ctx.Arg(1)}");
        return true;
    }

    private bool CmdGotoMap(CommandContext ctx)
    {
        if (ctx.ArgCount < 2)
        {
            ctx.Print("usage: gotomap <name>");
            return true;
        }
        _world.QueuedNextMap = ctx.Arg(1);
        _world.EndMatch();
        ctx.Print($"next map: {ctx.Arg(1)}");
        return true;
    }

    private bool CmdHelp(CommandContext ctx)
    {
        if (ctx.ArgCount >= 2 && _commands.TryGetValue(ctx.Arg(1), out var cmd))
        {
            ctx.Print(cmd.Help);
            return true;
        }
        var names = new List<string>(_commands.Keys);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        ctx.Print("commands: " + string.Join(", ", names));
        return true;
    }

    private bool CmdStatus(CommandContext ctx)
    {
        ctx.Print($"gametype: {_world.GameType?.NetName ?? "(none)"}  time: {_world.Time:0.0}");
        ctx.Print($"players: {_world.Clients.PlayerCount} ({_world.Clients.BotCount} bots)");
        foreach (var row in _world.Scores.Sorted())
            ctx.Print($"  {row.Player.NetName,-16} score {row.Score,4}  k/d {row.Kills}/{row.Deaths}");
        if (_world.Intermission.Running)
            ctx.Print("** intermission **");
        return true;
    }

    // =============================================================================================
    // bots / match flow (QC setbots/removebots/allready/resetmatch/gametype/cointoss/nextmap)
    // =============================================================================================

    private bool CmdSetBots(CommandContext ctx)
    {
        int n = (int)ctx.ArgFloat(1);
        SettempCvars.Set("bot_number", n);
        ctx.Print($"bot_number set to {n}");
        // top up / trim via the host's bot pipeline if wired.
        int have = _world.Clients.BotCount;
        for (int i = have; i < n; i++) AddBotHandler?.Invoke(null, null);
        for (int i = have; i > n; i--) RemoveBotHandler?.Invoke(null);
        return true;
    }

    private bool CmdRemoveBots(CommandContext ctx)
    {
        SettempCvars.Set("bot_number", 0);
        int removed = 0;
        while (_world.Clients.BotCount > 0 && (RemoveBotHandler?.Invoke(null) ?? false)) removed++;
        ctx.Print($"removed {removed} bot(s)");
        return true;
    }

    private bool CmdNextMap(CommandContext ctx)
    {
        if (ctx.ArgCount >= 2) { _world.QueuedNextMap = ctx.Arg(1); ctx.Print($"next map: {ctx.Arg(1)}"); }
        else ctx.Print($"next map: {(string.IsNullOrEmpty(_world.QueuedNextMap) ? "(rotation)" : _world.QueuedNextMap)}");
        return true;
    }

    private bool CmdAllReady(CommandContext ctx)
    {
        if (!_world.Warmup.WarmupStage) { ctx.Print("Not in warmup."); return true; }
        _world.RestartMatch();
        ctx.Print("all ready — starting match");
        return true;
    }

    private bool CmdResetMatch(CommandContext ctx)
    {
        _world.RestartMatch();
        ctx.Print("match reset");
        return true;
    }

    private bool CmdGameType(CommandContext ctx)
    {
        if (ctx.ArgCount < 2) { ctx.Print("usage: gametype <mode>"); return true; }
        // a gametype change takes effect on the next map; queue a restart on the same map under the new type.
        Cvars.Set("gametype", ctx.Arg(1));
        _world.QueuedNextMap = string.IsNullOrEmpty(_world.MapName) ? _world.QueuedNextMap : _world.MapName;
        ctx.Print($"gametype will change to {ctx.Arg(1)} on the next map");
        return true;
    }

    private bool CmdCoinToss(CommandContext ctx)
    {
        // QC GameCommand_cointoss (sv_cmd.qc:480): with two user options argv(1)/argv(2) they get a "^7" prefix;
        // the defaults are "^1HEADS"/"^4TAILS". The flip is random() > 0.5 → result1 (use the shared deterministic
        // Prandom so a headless server is reproducible while still being an unbiased 50/50 — not the old clock LSB).
        bool custom = ctx.ArgCount >= 3;
        string result1 = custom ? $"^7{ctx.Arg(1)}" : "^1HEADS";
        string result2 = custom ? $"^7{ctx.Arg(2)}" : "^4TAILS";
        string result = XonoticGodot.Common.Math.Prandom.Float() > 0.5f ? result1 : result2;
        // QC: Send_Notification(NOTIF_ALL, NULL, MSG_MULTI, MULTI_COINTOSS, choice) → broadcast to everyone.
        ChatBroadcast?.Invoke($"^2* Coin toss: {result}");
        ctx.Print($"coin toss: {result}");
        return true;
    }

    // =============================================================================================
    // teamplay admin (QC lockteams/unlockteams/shuffleteams/moveplayer/allspec/nospectators)
    // =============================================================================================

    private bool CmdLockTeams(CommandContext ctx)
    {
        if (!_world.Teamplay.IsTeamGame) { ctx.Print("Teams can only be locked in a team game."); return true; }
        _world.TeamsLocked = true;
        ChatBroadcast?.Invoke("^2* Teams are now locked.");
        return true;
    }

    private bool CmdUnlockTeams(CommandContext ctx)
    {
        if (!_world.Teamplay.IsTeamGame) { ctx.Print("Teams can only be locked in a team game."); return true; }
        _world.TeamsLocked = false;
        ChatBroadcast?.Invoke("^2* Teams are now unlocked.");
        return true;
    }

    private bool CmdShuffleTeams(CommandContext ctx)
    {
        // QC GameCommand_shuffleteams (server/command/sv_cmd.qc:1382): if a gametype deferred the shuffle to the
        // next round reset (CA/FreezeTag SV_ParseServerCommand set shuffleteams_on_reset_map = round-live), don't
        // disrupt the live round — announce it and let reset_map run the shuffle (vote.qc:368-372). The CA/FT
        // SV_ParseServerCommand hook is modeled here directly: defer iff the active round is live.
        if (_world.Rounds is { IsRoundStarted: true } && _world.GameType is ClanArena)
        {
            _world.ShuffleTeamsOnResetMap = true;
            ChatBroadcast?.Invoke("Players will be shuffled when this round is over.");
            return true;
        }

        ShuffleTeamsNow(ctx);
        return true;
    }

    /// <summary>
    /// QC <c>shuffleteams()</c> (server/command/sv_cmd.qc:1340): the actual reservoir-shuffled round-robin team
    /// redistribution. Invoked immediately by <see cref="CmdShuffleTeams"/>, or deferred to the next
    /// <see cref="GameWorld.ResetMap"/> when a CA-style round was live (QC shuffleteams_on_reset_map).
    /// </summary>
    public void ShuffleTeamsNow(CommandContext? ctx = null)
    {
        // QC shuffleteams() (sv_cmd.qc:1340).
        if (!_world.Teamplay.IsTeamGame) { ctx?.Print("Can't shuffle teams when currently not playing a team game."); return; }

        // QC FOREACH_CLIENT(IS_PLAYER||INGAME): the present playing roster (the port treats non-observers as in-game).
        var players = new List<Player>();
        foreach (Player p in _world.Clients.Players)
            if (!p.IsObserver && p.FragsStatus != Player.FragsSpectator)
                players.Add(p);

        // QC: refuse the shuffle if any player is pinned to a forced team (g_forced_team_*).
        foreach (Player p in players)
            if (_world.Teamplay.HasRealForcedTeam(p))
            { ctx?.Print("Can't shuffle teams because at least one player has a forced team."); return; }

        int teamCount = _world.Teamplay.TeamCount;
        if (teamCount <= 0) { ctx?.Print("Can't shuffle teams: no teams available."); return; }

        // QC FOREACH_CLIENT_RANDOM (server/utils.qh): a reservoir shuffle of the roster using random(), so the
        // round-robin team assignment falls on a randomized order (not a stable one). Prandom.Float() == QC random().
        var shuffled = new List<Player>(players.Count);
        foreach (Player it in players)
        {
            int j = (int)MathF.Floor(XonoticGodot.Common.Math.Prandom.Float() * (shuffled.Count + 1));
            if (j == shuffled.Count) shuffled.Add(it);
            else { shuffled.Add(shuffled[j]); shuffled[j] = it; }
        }

        int teamIndex = 0;
        foreach (Player p in shuffled)
        {
            int target = Teams.All[teamIndex % teamCount];
            if ((int)p.Team != target)
            {
                // QC sv_cmd.qc:1374: shuffleteams moves each player via MoveToTeam(it, target, 6) — the lock is
                // backed up/disabled/restored so the shuffle goes through even with teams locked, then the move
                // kills + clears score on the real change (and fires the Player_ChangeTeam/ChangedTeam hooks).
                _world.Teamplay.MoveToTeam(p, target);
            }
            teamIndex = (teamIndex + 1) % teamCount;
        }
        ChatBroadcast?.Invoke("Successfully shuffled the players around randomly.");
    }

    private bool CmdMovePlayer(CommandContext ctx)
    {
        if (ctx.ArgCount < 3) { ctx.Print("usage: moveplayer <player> <red|blue|yellow|pink|auto|spec>"); return true; }
        Player? target = FindPlayerByNameOrId(ctx.Arg(1)); // accept "#<index>" (the moveto* vote form) as well as a name
        if (target is null) { ctx.Print($"no player matching \"{ctx.Arg(1)}\""); return true; }
        string dest = ctx.Arg(2).ToLowerInvariant();
        if (dest is "spec" or "spectator" or "spectate")
        {
            _world.Clients.PutObserverInServer(target); // QC: real observer transition, not just the scoreboard flag
            ctx.Print($"moved {target.NetName} to spectators");
            return true;
        }
        if (!_world.Teamplay.IsTeamGame) { ctx.Print("not a team game"); return true; }
        int team = dest == "auto" ? _world.Teamplay.AssignBestTeam(target, _world.Clients.Players) : TeamFromName(dest);
        if (team == Teams.None) { ctx.Print($"invalid team \"{dest}\""); return true; }
        // QC sv_cmd.qc:1139: movetoteam routes through MoveToTeam(client, team_id, 6) — back up + disable + restore
        // the team lock so the admin move bypasses lockteams (the lock is only enforced on the player join path),
        // then kill + score-clear on the real change and fire the Player_ChangeTeam/ChangedTeam hooks (mutator veto).
        _world.Teamplay.MoveToTeam(target, team);
        ctx.Print($"moved {target.NetName} to {Teams.Name(team)}");
        return true;
    }

    private bool CmdAllSpec(CommandContext ctx)
    {
        string reason = ctx.ArgTail(1);
        int n = 0;
        // Snapshot the roster: PutObserverInServer may mutate roster-adjacent state, so iterate a copy.
        foreach (Player p in new List<Player>(_world.Clients.Players))
            if (!p.IsObserver) { _world.Clients.PutObserverInServer(p); n++; }
        ChatBroadcast?.Invoke($"^2* All players moved to spectators{(string.IsNullOrEmpty(reason) ? "" : $" ({reason})")}");
        ctx.Print($"moved {n} player(s) to spectators");
        return true;
    }

    private bool CmdNoSpectators(CommandContext ctx)
    {
        SettempCvars.Set("sv_spectate", "0");
        ChatBroadcast?.Invoke("^2* Spectating is now disabled.");
        return true;
    }

    // =============================================================================================
    // per-player commands (QC ClientCommand ready/join/spectate/selectteam/kill)
    // =============================================================================================

    private bool CmdReady(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("ready is a client command"); return true; }
        if (!_world.Warmup.WarmupStage) { ctx.Print("Not in warmup."); return true; }
        bool ready = _world.ToggleReady(ctx.Caller);
        ChatBroadcast?.Invoke($"^2* {ctx.Caller.NetName} is {(ready ? "ready" : "NOT ready")}");
        return true;
    }

    private bool CmdJoin(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("join is a client command"); return true; }
        Player p = ctx.Caller;
        // QC Join_Try (client.qc:2274): a non-INGAME client in g_playban_list cannot (re)join — the play-ban's
        // load-bearing enforcement. A player who isn't currently in play is one sitting as observer/spectator;
        // a live (dead) player is already INGAME and falls through to respawn.
        // Base: Send_Notification(NOTIF_ONE_ONLY, this, MSG_CENTER, CENTER_JOIN_PLAYBAN) then return false.
        // The port sends the same center notification (no sprint — Base does not sprint here).
        if ((p.IsObserver || p.FragsStatus == Player.FragsSpectator)
            && Bans.PlayerInList(p, "g_playban_list"))
        {
            if (!p.IsBot)
                NotificationSystem.Send(NotifBroadcast.OneOnly, p, MsgType.Center, "JOIN_PLAYBAN");
            return true;
        }
        // QC Join_Try -> QueueNeeded (server/teamplay.qc): during a live match (g_balance_teams_queue) a joiner who
        // hasn't picked a team (wants_join 0 = any) is held in the join queue as an observer when joining now would
        // unbalance the teams; QueuedPlayersTagIn later pulls them in when a deficit opens. Bots/warmup/cvar-off
        // fall through to the immediate join. A forced-team player never queues. Checked BEFORE the observer→player
        // transition below (QC checks QueueNeeded in Join_Try, before Join/PutClientInServer makes them a player),
        // so a queued joiner genuinely stays an observer rather than being flipped to FragsPlayer first.
        if (_world.Teamplay.IsTeamGame && (int)p.Team == Teams.None && !_world.TeamsLocked
            && !_world.Teamplay.HasRealForcedTeam(p)
            && _world.Teamplay.QueueNeeded(p, p.WantsJoin, _world.Clients.Players,
                   _world.Warmup.WarmupStage, Cvars.Bool("g_campaign")))
        {
            _world.Teamplay.EnqueueJoin(p, p.WantsJoin);
            return true; // stay an observer; tag-in happens from BalanceTeamsTick when a slot opens.
        }
        if (p.FragsStatus == Player.FragsSpectator)
            p.FragsStatus = Player.FragsPlayer;
        if (_world.Teamplay.IsTeamGame && (int)p.Team == Teams.None && !_world.TeamsLocked)
            _world.Teamplay.AssignBestTeam(p, _world.Clients.Players);
        // QC Join: an OBSERVER goes through the observer→player transition (clears IsObserver/Spectatee, restores
        // MOVETYPE_WALK/SOLID/DAMAGE via PutPlayerInServer); a live (dead) player just respawns.
        if (p.IsObserver)
            _world.Clients.Join(p);
        else
            _world.Clients.Spawn(p);
        ctx.Print("joined the game");
        return true;
    }

    private bool CmdSpectate(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("spectate is a client command"); return true; }
        if (!Cvars.Bool("sv_spectate")) { ctx.Print("Spectating is not allowed."); return true; }
        Player p = ctx.Caller;
        // QC MUTATOR_HOOKFUNCTION(lms, ClientCommand_Spectate) ⇒ MUT_SPECCMD_RETURN (sv_lms.qc:704): a ranked
        // (out-of-game) LMS player can no longer become a real spectator — they stay a ranked observer of the
        // match. Keyed on frags == FRAGS_PLAYER_OUT_OF_GAME (the eliminated state), which here is the LmsState's
        // OutOfGame + an assigned finishing rank.
        if (_world.GameType is XonoticGodot.Common.Gameplay.LastManStanding lms)
        {
            var lst = lms.GetState(p);
            if (lst.OutOfGame && lst.Rank > 0)
                return true; // forbidden — already eliminated/ranked, can't drop to a free spectator
        }
        // optional <client> arg → follow that player (QC Spectate(this, GetFilteredEntity(argv(1)))). The follow
        // path is NOT deferred in QC (cmd.qc:862 calls Spectate directly), so keep the immediate observer+follow:
        // run the REAL observer transition (free-fly, non-solid, model hidden, weapons stripped), then follow.
        if (ctx.ArgCount >= 2 && ctx.Arg(1) != "0")
        {
            _world.Clients.PutObserverInServer(p);
            Player? tgt = FindPlayerByName(ctx.Arg(1));
            if (tgt is not null && !tgt.IsObserver && !tgt.IsDead && !ReferenceEquals(tgt, p))
                _world.Clients.Spectate(p, tgt);
            else
                ctx.Print($"no player matching \"{ctx.Arg(1)}\"");
            ctx.Print("now spectating");
            return true;
        }
        // QC MUTATOR_HOOKFUNCTION(ca, ClientCommand_Spectate) (sv_clanarena.qc): before the generic kill-
        // countdown spectate, let CA intercept the command. For a live CA player this sends INFO_CA_LEAVE
        // (MUT_SPECCMD_FORCE) and records _prevTeam. The actual PutObserverInServer still runs below via the
        // countdown; GametypeSpectateCommand only sets up the CA state. Non-CA modes return false (no-op).
        _world.Clients.GametypeSpectateCommand(p);
        // QC ClientCommand_spectate (cmd.qc:886): a plain `spectate` defers the observer transition through
        // ClientKill_TeamChange(caller, -2) — the g_balance_kill_delay countdown (grey CENTER_TEAMCHANGE_SPECTATE
        // print + the cancel window), not an instant PutObserverInServer. Begin resolves -2 → PutObserverInServer
        // on expiry (or immediately when the delay is <= 0).
        _world.KillCountdown.Begin(p, -2);
        ctx.Print("now spectating");
        return true;
    }

    private bool CmdSelectTeam(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("selectteam is a client command"); return true; }
        if (!_world.Teamplay.IsTeamGame) { ctx.Print("Not a team game."); return true; }
        if (_world.TeamsLocked) { ctx.Print("Teams are locked."); return true; }
        if (ctx.ArgCount < 2) { ctx.Print("usage: selectteam <red|blue|yellow|pink|auto>"); return true; }
        // QC cmd.qc:701: a forced-team player can't selectteam at all.
        if (_world.Teamplay.HasRealForcedTeam(ctx.Caller))
        {
            ctx.Print("selectteam can not be used as your team is forced");
            return true;
        }
        Player caller = ctx.Caller;
        string sel = ctx.Arg(1).ToLowerInvariant();
        int oldTeam = (int)caller.Team;

        if (sel == "auto")
        {
            // QC ClientKill_TeamChange targetteam == -1, team <= 0 branch (clientkill.qc:110-116): an observer / not-
            // yet-teamed player auto-selecting defers the autoselect to Join() (team_selected = -1; return) so the
            // join queue isn't skipped — no countdown. Apply the chosen team immediately as their pending join team.
            if (oldTeam <= 0)
            {
                _world.Teamplay.AssignBestTeam(caller, _world.Clients.Players); // sets caller.Team to the best team
                ctx.Print($"auto-selected {Teams.Name((int)caller.Team)}");
                return true;
            }
            // QC ClientKill_TeamChange targetteam == -1 (clientkill.qc:118-132): decide the best team NOW so the
            // balancer treats this player as on the team they're about to join, and so we can early-out if they're
            // already on a best team instead of counting down for nothing. AssignBestTeam computes (and would apply)
            // the best team; restore the live team afterwards so the move stays DEFERRED through the countdown — the
            // countdown's PerformTeamChange applies it at expiry, exactly like SetPlayerTeam in QC.
            int best = _world.Teamplay.AssignBestTeam(caller, _world.Clients.Players);
            caller.Team = oldTeam;
            if (best == Teams.None) { ctx.Print($"invalid team \"{sel}\""); return true; }
            // QC clientkill.qc:127-131: already on a best team → CENTER_TEAMCHANGE_ALREADYBEST, no countdown.
            if (best == oldTeam)
            {
                if (!caller.IsBot)
                    NotificationSystem.Send(NotifBroadcast.OneOnly, caller, MsgType.Center, "TEAMCHANGE_ALREADYBEST");
                ctx.Print("already on the best team");
                return true;
            }
            // QC joinAllowed → ClientKill_TeamChange(caller, best): defer the move through the kill countdown.
            _world.KillCountdown.Begin(caller, best);
            ctx.Print($"changing to {Teams.Name(best)}");
            return true;
        }

        int team = TeamFromName(sel);
        if (team == Teams.None) { ctx.Print($"invalid team \"{sel}\""); return true; }
        // QC cmd.qc:730 (INFO/CENTER_TEAMCHANGE_SAME): an explicit re-select of your current team is a no-op.
        if (oldTeam == team) { ctx.Print("already on that team"); return true; }
        // QC cmd.qc:746 (g_balance_teams_prevent_imbalance): an explicit (non-auto) switch to a stronger/larger
        // team mid-match is rejected — a human may only switch to a current best (smallest/lowest-score) team.
        // Warmup, bots, and a disabled cvar are allowed through inside the gate.
        if (!_world.Teamplay.IsTeamAllowedForSwitch(caller, team, _world.Clients.Players, _world.Warmup.WarmupStage))
        {
            // QC cmd.qc:754-755: a rejected switch to a stronger/larger team prints the INFO line to everyone
            // and a CENTER nag to the would-be switcher (CPID_PREVENT_JOIN group).
            ctx.Print("You cannot change to a larger team.");
            NotificationSystem.Send(NotifBroadcast.OneOnly, caller, MsgType.Info, "TEAMCHANGE_STRONGERTEAM");
            NotificationSystem.Send(NotifBroadcast.OneOnly, caller, MsgType.Center, "TEAMCHANGE_STRONGERTEAM");
            return true;
        }
        // QC cmd.qc:764 ClientKill_TeamChange(caller, team_num): defer the team move through the g_balance_kill_delay
        // countdown (the team-colored CENTER_TEAMCHANGE_<team> print + cancel window). The actual team set + kill is
        // run by KillCountdown.PerformTeamChange at expiry (or immediately when the delay is <= 0).
        _world.KillCountdown.Begin(caller, team);
        ctx.Print($"changing to {Teams.Name(team)}");
        return true;
    }

    /// <summary>
    /// QC the engine <c>color</c> command → <c>SV_ChangeTeam</c> (server/teamplay.qc:1340). DP's engine accepts
    /// either one combined value (<c>color N</c>, the packed <c>16*shirt + pants</c>) or two values
    /// (<c>color &lt;pants&gt; &lt;shirt&gt;</c>). We decode to the packed clientcolors and call
    /// <see cref="Teamplay.ChangeTeam"/>, which only re-colors in a NON-team game (in teamplay the color is owned
    /// by the team). A no-op (with no error) in a team game, matching SV_ChangeTeam's early-out.
    /// </summary>
    private bool CmdColor(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("color is a client command"); return true; }
        if (ctx.ArgCount < 2) { ctx.Print("usage: color <pants> [shirt]"); return true; }

        int packed;
        if (ctx.ArgCount >= 3 && int.TryParse(ctx.Arg(2), out int shirt) && int.TryParse(ctx.Arg(1), out int pants))
            // Two-arg form: pants (low nibble) + shirt (high nibble) — the engine `color <bottom> <top>` form.
            packed = (16 * (shirt & 0x0F)) + (pants & 0x0F);
        else if (int.TryParse(ctx.Arg(1), out int combined))
            packed = combined & 0xFF;
        else { ctx.Print("usage: color <pants> [shirt]"); return true; }

        _world.Teamplay.ChangeTeam(ctx.Caller, packed);
        return true;
    }

    private bool CmdKill(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("kill is a client command"); return true; }
        Player p = ctx.Caller;
        if (p.FragsStatus == Player.FragsSpectator) return true;
        if (p.IsDead) { ctx.Print("already dead"); return true; }
        // QC server/clientkill.qc ClientKill → ClientKill_TeamChange(this, 0): defer the suicide through the
        // g_balance_kill_delay countdown (spoken kill announcer + CENTER_TEAMCHANGE_SUICIDE print). Begin() falls
        // back to an immediate kill when the delay is <= 0, so the player always dies (then respawns) as before.
        _world.KillCountdown.Begin(p, 0);
        return true;
    }

    /// <summary>
    /// QC the <c>ons_spawn</c> client command (MUTATOR_HOOKFUNCTION(ons, SV_ParseClientCommand),
    /// sv_onslaught.qc:1937): the click-radar pick — <c>cmd ons_spawn [x y z]</c> with the clicked 2D map position
    /// (defaults to the player's own origin when omitted). Routes to <see cref="Onslaught.HandleOnsSpawnCommand"/>
    /// which teleports a live player between own control points, or — for a dead player — sets the chosen node so
    /// the next respawn places them there (forcing the respawn). A no-op unless Onslaught is the active gametype.
    /// </summary>
    private bool CmdOnsSpawn(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("ons_spawn is a client command"); return true; }
        if (_world.GameType is not Onslaught ons) return true; // QC: hook only present under the ons mutator
        Player p = ctx.Caller;
        if (p.FragsStatus == Player.FragsSpectator || p.IsObserver) return true; // QC IS_PLAYER(player)
        // QC: pos defaults to player.origin, then argv(1..3) override x/y/z.
        Vector3 pos = p.Origin;
        if (ctx.ArgCount > 1) pos.X = ctx.ArgFloat(1);
        if (ctx.ArgCount > 2) pos.Y = ctx.ArgFloat(2);
        if (ctx.ArgCount > 3) pos.Z = ctx.ArgFloat(3);
        string? msg = ons.HandleOnsSpawnCommand(p, pos);
        if (msg is not null) ctx.Print(msg); // QC sprint(player, …) — the failure/info reply
        return true;
    }

    // =============================================================================================
    // player-deployed waypoints (QC server/impulse.qc IMPULSE(waypoint_*) → waypointsprites.qc Deploy/Attach)
    // =============================================================================================

    /// <summary>Which player-deployed waypoint a <c>waypoint_*</c> command drops (the QC sprite + radar icon).</summary>
    private enum WpDeploy { Personal, Here, Danger, Helpme }

    /// <summary>
    /// Which location a waypoint impulse targets — QC's three-way split across each sprite group
    /// (impulse.qc:414-494): _here = player's current origin, _crosshair = endpoint of a server-side
    /// crosshair trace (<c>WarpZone_crosshair_trace(this)</c>), _death = <c>this.death_origin</c>
    /// (the position latched at the player's last death). The _death variants also guard on
    /// <c>if (!this.death_origin) return;</c> — here we treat a zero DeathOrigin as "not set yet".
    /// </summary>
    private enum WpLocation { Here, Crosshair, Death }

    /// <summary>
    /// QC the <c>IMPULSE(waypoint_personal_*/here_*/danger_*/here_follow)</c> handlers (server/impulse.qc:414-494):
    /// drop the corresponding marker through the <see cref="Waypoints.WaypointSprites"/> Deploy/Attach API so it
    /// rides the live per-tick waypoint net to every eligible client. A client command — needs a live player.
    /// </summary>
    private bool CmdWaypointDeploy(CommandContext ctx, WpDeploy which, WpLocation loc = WpLocation.Here)
    {
        if (ctx.Caller is null) { ctx.Print("waypoint is a client command"); return true; }
        Player p = ctx.Caller;
        if (p.FragsStatus == Player.FragsSpectator || p.IsObserver) return true; // not a live player
        int team = _world.Teamplay.IsTeamGame ? (int)p.Team : 0;

        // Resolve the deploy position from the location specifier. QC WarpZone_crosshair_trace traces from the
        // player's eyes along the view angles (like a hitscan); the death variant guards on this.death_origin!=0.
        Vector3? pos = WaypointResolveLocation(p, loc);
        if (pos is null) return true; // QC: death variant "if (!this.death_origin) return;" — silently skip

        switch (which)
        {
            case WpDeploy.Personal:
                // QC WaypointSprite_DeployPersonal(WP_Waypoint, this, origin/trace_endpos/death_origin, RADARICON_WAYPOINT)
                WaypointDeployPersonal(p, pos.Value);
                ctx.Print(loc == WpLocation.Crosshair ? "personal waypoint spawned at crosshair"
                        : loc == WpLocation.Death     ? "personal waypoint spawned at death location"
                                                      : "personal waypoint spawned at location");
                break;
            case WpDeploy.Here:
                // QC WaypointSprite_DeployFixed(WP_Here, false, this, origin/trace_endpos/death_origin, RADARICON_HERE)
                WaypointDeployFixed(p, "Here", team, pos.Value);
                ctx.Print(loc == WpLocation.Crosshair ? "HERE spawned at crosshair"
                        : loc == WpLocation.Death     ? "HERE spawned at death location"
                                                      : "HERE spawned at location");
                break;
            case WpDeploy.Danger:
                // QC WaypointSprite_DeployFixed(WP_Danger, false, this, origin/trace_endpos/death_origin, RADARICON_DANGER)
                WaypointDeployFixed(p, "Danger", team, pos.Value);
                ctx.Print(loc == WpLocation.Crosshair ? "DANGER spawned at crosshair"
                        : loc == WpLocation.Death     ? "DANGER spawned at death location"
                                                      : "DANGER spawned at location");
                break;
            case WpDeploy.Helpme:
                // QC IMPULSE(waypoint_here_follow): teamplay-only, live player only. The MUTATOR_CALLHOOK(HelpMePing)
                // override is modeled inside WaypointSprites.Attach: when a carrier sprite already rides the player,
                // it pings that sprite instead of spawning a new helpme marker (matching the CTF hook's behavior).
                if (!_world.Teamplay.IsTeamGame || p.IsDead) return true;
                WaypointDeployHelpme(p, team);
                ctx.Print("HELP ME attached");
                break;
        }
        return true;
    }

    /// <summary>
    /// Resolve the deploy position for a waypoint impulse.
    /// <list type="bullet">
    ///   <item><see cref="WpLocation.Here"/> — the player's current origin (QC <c>this.origin</c>).</item>
    ///   <item><see cref="WpLocation.Crosshair"/> — endpoint of a server-side trace along the player's aim
    ///         direction (QC <c>WarpZone_crosshair_trace(this)</c> → <c>trace_endpos</c>). Falls back to
    ///         <c>p.Origin</c> when the trace service is unavailable.</item>
    ///   <item><see cref="WpLocation.Death"/> — <c>Player.DeathOrigin</c> (QC <c>this.death_origin</c>).
    ///         Returns <c>null</c> when zero (not yet set — QC guards <c>if (!this.death_origin) return;</c>).</item>
    /// </list>
    /// </summary>
    private static Vector3? WaypointResolveLocation(Player p, WpLocation loc)
    {
        switch (loc)
        {
            case WpLocation.Here:
                return p.Origin;

            case WpLocation.Crosshair:
                // QC WarpZone_crosshair_trace(this): traceline from the player's eyes along their view direction.
                // The QC version also walks through WarpZones; the port does a plain server trace (no WarpZone
                // redirect logic — acceptable simplification, as WarpZone awareness would need the warpzone spatial
                // index the port doesn't expose from the impulse path).
                if (Api.Services is not null)
                {
                    Vector3 eyes = p.Origin + p.ViewOfs;
                    Vector3 aim  = p.ViewAngles != Vector3.Zero ? p.ViewAngles : p.Angles;
                    XonoticGodot.Common.Math.QMath.AngleVectors(aim, out Vector3 fwd, out _, out _);
                    Vector3 end = eyes + fwd * WeaponFiring.CurrentMaxShotDistance;
                    var tr = Api.Trace.Trace(eyes, Vector3.Zero, Vector3.Zero, end, MoveFilter.Normal, p);
                    return tr.EndPos;
                }
                return p.Origin; // fallback for test/offline context

            case WpLocation.Death:
                // QC: if (!this.death_origin) return; — death_origin is zero until the player has died once.
                // Player.DeathOrigin is latched in DamageSystem.Killed (Obituary path) and is Vector3.Zero by default.
                if (p.DeathOrigin == Vector3.Zero)
                    return null; // not set yet — skip silently, matching QC
                return p.DeathOrigin;

            default:
                return p.Origin;
        }
    }

    private static void WaypointDeployPersonal(Player p, Vector3 pos)
        => Waypoints.WaypointSprites.DeployPersonal("Waypoint", p, pos,
            Waypoints.WaypointRegistry.Get("Waypoint").RadarIcon);

    private static void WaypointDeployFixed(Player p, string sprite, int team, Vector3 pos)
    {
        Waypoints.WaypointDef def = Waypoints.WaypointRegistry.Get(sprite);
        Waypoints.WaypointSprites.DeployFixed(sprite, false, p, pos, team, def.Color, def.RadarIcon);
    }

    private static void WaypointDeployHelpme(Player p, int team)
    {
        Waypoints.WaypointDef def = Waypoints.WaypointRegistry.Get("Helpme");
        Waypoints.WaypointSprites.Attach("Helpme", p, false, team, def.Color, def.RadarIcon);
    }

    /// <summary>QC <c>IMPULSE(waypoint_clear[_personal])</c> (server/impulse.qc:496-521): remove the caller's
    /// personal waypoint, or all of their deployed waypoints. In CTS/race with <c>g_allow_checkpoints</c> set, the
    /// personal-waypoint clear additionally deletes the runner's <c>.personal</c> speedrun checkpoint and self-kills
    /// them (QC routes it through <c>ClientKill</c>) so they reset to the start — but QC wraps both in
    /// <c>if (this.personal)</c>, so a runner who never snapshotted a checkpoint is neither killed nor reset
    /// (gated here on <see cref="Player.PersonalCheckpoint"/> being non-null).</summary>
    private bool CmdWaypointClear(CommandContext ctx, bool personalOnly)
    {
        if (ctx.Caller is null) { ctx.Print("waypoint is a client command"); return true; }
        Player p = ctx.Caller;
        if (personalOnly)
        {
            Waypoints.WaypointSprites.ClearPersonal(p);
            ctx.Print("personal waypoint cleared");
        }
        else
        {
            Waypoints.WaypointSprites.ClearOwned(p);
            ctx.Print("all waypoints cleared");
        }
        // QC impulse.qc:504/517: in CTS/race with g_allow_checkpoints set, the personal-waypoint clear deletes the
        // .personal speedrun checkpoint AND self-kills the runner so they reset to the start — but QC wraps BOTH in
        // `if (this.personal)`: a runner who never snapshotted a checkpoint is NOT killed and nothing is deleted.
        // Now that the .personal checkpoint is modelled (Player.PersonalCheckpoint via the SPEEDRUN_INIT snapshot),
        // honor that gate: only act when a checkpoint exists, and clear it on the personal-waypoint clear.
        if (personalOnly && p.PersonalCheckpoint is not null
            && (_world.GameType is XonoticGodot.Common.Gameplay.Cts
                || _world.GameType is XonoticGodot.Common.Gameplay.Race)
            && Cvars.Bool("g_allow_checkpoints")
            && !p.IsObserver && p.FragsStatus != Player.FragsSpectator && !p.IsDead)
        {
            // QC: delete(this.personal); this.personal = NULL; ClientKill(this);
            p.PersonalCheckpoint = null;
            _world.KillCountdown.Begin(p, 0);
        }
        return true;
    }

    // =============================================================================================
    // minigames (QC common/minigames/sv_minigames.qc ClientCommand_minigame)
    // =============================================================================================

    /// <summary>
    /// QC <c>ClientCommand_minigame(caller, request, argc, command)</c>: the per-player <c>cmd minigame …</c>
    /// dispatcher. Gated on <c>sv_minigames</c> + a real client caller (a server-console invocation is rejected
    /// — minigames are a per-player command). The subcommand tokens are the QC MINIGAME_COMMON_CMD literals:
    /// <c>create</c>/<c>join</c>/<c>list</c>/<c>list-sessions</c>/<c>part</c>/<c>end</c>/<c>invite</c>; any other
    /// first token while the caller is in a game is forwarded to the game as a move (QC the "cmd" event). A bad
    /// verb prints the multi-line usage block. Always returns true (consumed).
    /// </summary>
    private bool CmdMinigame(CommandContext ctx)
    {
        // QC: if(!autocvar_sv_minigames){ sprint "Minigames are not enabled!\n"; return; }
        if (!Cvars.Bool("sv_minigames"))
        {
            ctx.Print("Minigames are not enabled!");
            return true;
        }
        // QC entry point is a CLIENT command (per-player, caller-gated), NOT a SERVER_COMMAND. A console/rcon
        // invocation has no caller to seat in a session, so reject it (mirrors the QC caller dependency).
        if (ctx.Caller is null)
        {
            ctx.Print("minigame is a client command");
            return true;
        }
        // QC playban gate (sv_minigames.qc:318-323): a play-banned caller can't create/join/move while
        // g_playban_minigames is set. Ships 0 (xonotic-server.cfg:436) so this is inactive by default; only
        // diverges when an admin opts in. Reuses the same g_playban_list membership the playban command writes.
        if (Cvars.Bool("g_playban_minigames") && Bans.PlayerInList(ctx.Caller, "g_playban_list"))
        {
            // QC also fires CENTER_JOIN_PLAYBAN here; the port surfaces the sprint text (the center-print notif
            // is the HUD layer's job, not the command output channel).
            ctx.Print("You aren't allowed to play minigames because you are banned from them in this server.");
            return true;
        }

        MinigameSessionManager mg = _world.Minigames;
        Player caller = ctx.Caller;
        string verb = ctx.Arg(1);

        // QC: "create" <game> (argc>2) → start_minigame.
        if (verb == "create" && ctx.ArgCount > 2)
        {
            MinigameSession? session = mg.Create(caller, ctx.Arg(2));
            ctx.Print(session is not null
                ? $"Created minigame session: {session.NetName}"
                : "Cannot start minigame session!");
            return true;
        }
        // QC: "join" <session> (argc>2) → join_minigame.
        if (verb == "join" && ctx.ArgCount > 2)
        {
            MinigameSession? session = mg.JoinByName(caller, ctx.Arg(2));
            ctx.Print(session is not null
                ? $"Joined: {session.NetName}"
                : "Cannot join given minigame session!");
            return true;
        }
        // QC: "list" → FOREACH(Minigames) sprint "<netname> (<message>) ".
        if (verb == "list")
        {
            foreach (Minigame m in Minigames.All)
                ctx.Print($"{m.NetName} ({m.DisplayName}) ");
            return true;
        }
        // QC: "list-sessions" → walk minigame_sessions sprint each .netname.
        if (verb == "list-sessions")
        {
            foreach (MinigameSession s in mg.Sessions)
                ctx.Print(s.NetName);
            return true;
        }
        // QC: "end"|"part" → if active part_minigame + "Left minigame session", else "You aren't playing…".
        if (verb is "end" or "part")
        {
            if (mg.ActiveSessionOf(caller) is not null)
            {
                mg.Part(caller);
                ctx.Print("Left minigame session");
            }
            else
            {
                ctx.Print("You aren't playing any minigame...");
            }
            return true;
        }
        // QC: "invite" <player> (argc>2) → resolve the target + invite_minigame + success/err sprint.
        if (verb == "invite" && ctx.ArgCount > 2)
        {
            if (mg.ActiveSessionOf(caller) is null)
            {
                ctx.Print("You aren't playing any minigame...");
                return true;
            }
            Player? target = FindPlayerByNameOrId(ctx.Arg(2));
            string error = mg.Invite(caller, target, out _);
            if (error.Length == 0)
                ctx.Print($"You have invited {target!.NetName} to join your game of {mg.ActiveSessionOf(caller)!.Game.DisplayName}");
            else
                ctx.Print($"Could not invite: {error}.");
            return true;
        }
        // QC fall-through: an unrecognized first token while in a game is the game's "cmd" event (a move). The
        // port forwards the WHOLE tail (verb + args) so Pong's "throw"/"move 1"/"pong_aimore" and the grid
        // games' bare tile/column reach Minigame.Move (ForwardMove marks the session dirty on acceptance).
        if (mg.ActiveSessionOf(caller) is not null && verb.Length > 0)
        {
            if (mg.ForwardMove(caller, ctx.ArgTail(1)))
                return true;
        }
        else if (verb.Length > 0)
        {
            ctx.Print($"Wrong command:^1 {ctx.ArgTail(0)}");
        }

        // QC: the multi-line Usage dump (printed on a bad verb / no active game).
        ctx.Print("");
        ctx.Print("Usage:^3 cmd minigame create <minigame>");
        ctx.Print("  Start a new minigame session");
        ctx.Print("Usage:^3 cmd minigame join <session>");
        ctx.Print("  Join an exising minigame session");
        ctx.Print("Usage:^3 cmd minigame list");
        ctx.Print("  List available minigames");
        ctx.Print("Usage:^3 cmd minigame list-sessions");
        ctx.Print("  List available minigames sessions");
        ctx.Print("Usage:^3 cmd minigame part|end");
        ctx.Print("  Leave the current minigame");
        ctx.Print("Usage:^3 cmd minigame invite <player>");
        ctx.Print("  Invite the given player to join you in a minigame");
        return true;
    }

    /// <summary>QC <c>GetIndexedEntity(argc, n)</c> (the minigame invite target): resolve a "#&lt;id&gt;" token or
    /// a name to a connected player. Reuses <see cref="FindPlayerByName"/> for the name path.</summary>
    private Player? FindPlayerByNameOrId(string token)
    {
        if (token.StartsWith('#') && int.TryParse(token.AsSpan(1), out int idx))
        {
            foreach (Player p in _world.Clients.Players)
                if (p.Index == idx || p.PlayerId == idx)
                    return p;
            return null;
        }
        return FindPlayerByName(token);
    }

    // =============================================================================================
    // weapon impulses (QC server/impulse.qc ImpulseCommands → selection.qc)
    // =============================================================================================

    /// <summary>
    /// QC <c>ImpulseCommands</c> (server/impulse.qc): a client sent <c>impulse N</c>. Route the weapon-related
    /// impulse numbers onto the selection/reload API (<see cref="WeaponImpulses.Handle"/>) plus the GIVE_ALL cheat
    /// impulse (99). The remaining non-weapon impulses (waypoints / clone / teleport / drag cheats) aren't handled
    /// here — they're owned by other tasks — so they no-op with a note. QC gates impulses on game_stopped / timeout /
    /// round-not-started; the game-stopped + timeout + round-not-started (drop/reload/use) gates are all honored.
    /// </summary>
    private bool CmdImpulse(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("impulse is a client command"); return true; }
        int imp = (int)ctx.ArgFloat(1);
        return DispatchImpulse(ctx, ctx.Caller, imp);
    }

    /// <summary>A console alias (weapon_next/prev/best/last/drop/reload) resolved to its impulse number.</summary>
    private bool CmdWeaponImpulse(CommandContext ctx, int imp)
    {
        if (ctx.Caller is null) { ctx.Print("weapon commands are client commands"); return true; }
        return DispatchImpulse(ctx, ctx.Caller, imp);
    }

    private bool DispatchImpulse(CommandContext ctx, Player caller, int imp)
    {
        if (imp <= 0) return true;
        // QC MapVote_Tick (server/mapvoting.qc:730-736): during the end-of-match map vote, impulses 1..mapvote_count
        // are votes for the matching ballot candidate (1-indexed) — NOT weapon impulses — and they must be honored
        // even though the match is in intermission. CastVote runs the availability + range guards itself (and
        // re-routes the abstain slot), so just translate the 1-based impulse to the 0-based candidate index.
        if (_world.MapVote.Running)
        {
            _world.MapVote.CastVote(caller, imp - 1);
            return true;
        }
        // QC ImpulseCommands: no impulses while the match is stopped (game_stopped) or while the game is FROZEN
        // by an active timeout (server/impulse.qc gates only on timeout_status == TIMEOUT_ACTIVE). The lead-time
        // COUNTDOWN before the pause (timeout_status == TIMEOUT_LEADTIME) must NOT block impulses, so gate on the
        // active-pause predicate (IsPaused == Status==ActivePause), not the broader .Active (Status != Inactive).
        if (_world.Intermission.Running) return true;
        // QC impulse.qc:379 `if (MinigameImpulse(this, imp)) return;` — a player seated in a minigame session
        // consumes the impulse as a minigame MOVE before anything else (QC runs it even BEFORE the timeout gate,
        // right after game_stopped). Route it here so a player in a minigame pressing a weapon key drives a
        // minigame move instead of weapon selection. MinigameSessionManager.Impulse no-ops (returns false) when
        // the caller isn't in an active session, leaving the normal weapon path untouched.
        if (_world.Minigames.Impulse(caller, imp)) return true;
        if (_world.Timeout.IsPaused) return true;
        // QC impulse.qc:383-395 — round-not-started gate: while a round handler is ACTIVE but the round hasn't
        // started (the CA/Freezetag pre-round warmup countdown), forbid the three "act on the world" impulses
        // weapon_drop(17)/weapon_reload(20)/use(21). Other impulses (weapon switching, etc.) stay allowed.
        // QC round_handler_IsActive() == "a round handler exists and is running": in the port that is exactly a
        // non-null GameWorld.Rounds (set only by EnableRounds for the round-based gametypes), and the round is not
        // yet live when IsRoundStarted is false (WaitingToStart / Countdown phases).
        if (_world.Rounds is { IsRoundStarted: false }
            && (imp == 17 || imp == 20 || imp == 21))
            return true;
        // [T37] SEAM C: a seated pilot's impulse routes to the per-vehicle mode switch/cycle BEFORE weapon
        // impulses (QC vehicle_impulse runs first). The existing C2S impulse path then makes the weapon-group
        // keys (1/2/3, weapnext/prev) switch vehicle modes when seated with zero extra net work.
        if (caller.Vehicle is not null && !VehicleCommon.IsDead(caller.Vehicle) && VehicleBoarding.Impulse(caller, imp))
            return true;
        // QC CheatImpulse (impulse.qc:398, cheats.qc:184): impulse 99 (CHIMPULSE_GIVE_ALL) — route it to
        // Cheats.GiveAll, which runs the sv_cheats gate (and counts the cheat) itself. The clone/speedrun/teleport/
        // drag impulses need engine services and remain unported; R00T (148) is handled just below.
        if (imp == 99)
        {
            _world.Cheats.GiveAll(caller); // silently no-ops (returns false) when sv_cheats disallows it, like QC
            return true;
        }
        // QC CheatImpulse CHIMPULSE_R00T (148, cheats.qc:264): nuke a random living enemy. Like GIVE_ALL it runs
        // the sv_cheats gate + counts the cheat itself, and silently no-ops when disallowed. Clone (140/142) and
        // speedrun (141) impulses need engine services (CopyBody / personal-waypoint) and remain unported.
        if (imp == 148)
        {
            _world.Cheats.R00t(caller, _world.Clients.Players);
            return true;
        }
        // QC CheatImpulse CHIMPULSE_TELEPORT (143, cheats.qc:235): emergency teleport (info_autoscreenshot, else a
        // random map location). Runs the sv_cheats gate + counts the cheat itself, silently no-ops when disallowed.
        if (imp == 143)
        {
            _world.Cheats.Teleport(caller);
            return true;
        }
        // QC CheatImpulse CHIMPULSE_SPEEDRUN (141, cheats.qc:188): teleport back to the personal speedrun checkpoint
        // and restore the snapshotted resources/weapons/items/status-effects (the checkpoint is taken by SPEEDRUN_INIT
        // = impulse 30, handled in the waypoint switch below). Gated as a cheat only when g_allow_checkpoints is off
        // (Cheats.Speedrun runs that gate + the cheat count itself); a free reset otherwise. CLONE_MOVING(140)/
        // CLONE_STANDING(142) remain unported (need a CopyBody entity-clone service the port doesn't expose).
        if (imp == 141)
        {
            // QC CHIMPULSE_SPEEDRUN trailing MUTATOR_CALLHOOK(AbortSpeedrun, this): on a successful teleport-back
            // the in-progress race/CTS run is no longer legitimate, so reset that racer's lap clock/checkpoint
            // progress (timing only — the Speedrun restore already did the teleport). Cheats.Speedrun returns true
            // only when the restore actually ran (checkpoint existed + not start-solid + sv_cheats gate passed).
            if (_world.Cheats.Speedrun(caller))
            {
                if (_world.GameType is XonoticGodot.Common.Gameplay.Race race)
                    race.AbortSpeedrun(caller);
                else if (_world.GameType is XonoticGodot.Common.Gameplay.Cts cts)
                    cts.AbortSpeedrun(caller);
            }
            return true;
        }
        // QC waypoint impulses (impulse.qc:414-521, all.qh:133-144): personal/here/danger waypoint-sprite deploy
        // and the HELP-ME follow ping + clear-personal/clear-all. The _here variants deploy at the player's origin,
        // the _crosshair variants at the server-side crosshair trace endpoint, the _death variants at the latched
        // death position (guarded: no-op when death_origin is zero, i.e. the player hasn't died yet this life).
        // QC impulse numbers: personal_here=30, personal_crosshair=31, personal_death=32,
        //   here_follow(helpme)=33, here_here=34, here_crosshair=35, here_death=36,
        //   danger_here=37, danger_crosshair=38, danger_death=39, clear_personal=47, clear_all=48.
        switch (imp)
        {
            case 30:
                // QC CHIMPULSE_SPEEDRUN_INIT (cheats.qc:144): impulse 30 is BOTH waypoint_personal_here AND the
                // speedrun-checkpoint snapshot (same impulse number). CheatImpulse runs first and snapshots the
                // player's restorable state into its .personal checkpoint (not a cheat — no sv_cheats gate, no count),
                // then falls through to the IMPULSES registry which deploys the personal waypoint SPRITE. Reproduce
                // that order: snapshot, then deploy the sprite.
                _world.Cheats.SpeedrunInit(caller);
                return CmdWaypointDeploy(ctx, WpDeploy.Personal, WpLocation.Here);
            case 31: return CmdWaypointDeploy(ctx, WpDeploy.Personal, WpLocation.Crosshair);
            case 32: return CmdWaypointDeploy(ctx, WpDeploy.Personal, WpLocation.Death);
            case 33: return CmdWaypointDeploy(ctx, WpDeploy.Helpme);
            case 34: return CmdWaypointDeploy(ctx, WpDeploy.Here,     WpLocation.Here);
            case 35: return CmdWaypointDeploy(ctx, WpDeploy.Here,     WpLocation.Crosshair);
            case 36: return CmdWaypointDeploy(ctx, WpDeploy.Here,     WpLocation.Death);
            case 37: return CmdWaypointDeploy(ctx, WpDeploy.Danger,   WpLocation.Here);
            case 38: return CmdWaypointDeploy(ctx, WpDeploy.Danger,   WpLocation.Crosshair);
            case 39: return CmdWaypointDeploy(ctx, WpDeploy.Danger,   WpLocation.Death);
            case 47: return CmdWaypointClear(ctx, personalOnly: true);
            case 48: return CmdWaypointClear(ctx, personalOnly: false);
        }

        // QC IMPULSE(use) -> PlayerUseKey (impulse.qc:409, client.qc:2620): the +use key routed through the
        // impulse path. This must run the FULL PlayerUseKey sequence — not just the vehicle enter/exit +
        // PlayerUseKey mutator hook that WeaponImpulses.UseHandle -> VehicleBoarding.UseKey covers, but ALSO the
        // CTF flag throw / pass-request (QC MUTATOR_HOOKFUNCTION(ctf, PlayerUseKey), sv_ctf.qc:2452). CTF's hook
        // needs the live roster (to find a carrier to request a pass from), which the generic PlayerUseKeyArgs
        // chain doesn't carry, so it's driven explicitly here — identically to the +use BUTTON edge
        // (ServerNet.UseKeyEdge), so a player who BINDS `impulse 21` gets the same flag-throw/pass as `+use`.
        if (imp == 21)
        {
            VehicleBoarding.UseKey(caller);
            if (_world.GameType is XonoticGodot.Common.Gameplay.Ctf ctf)
                ctf.HandleUseKey(caller, _world.Clients.Players);
            // QC MUTATOR_HOOKFUNCTION(ons, PlayerUseKey) (sv_onslaught.qc:1990): next to an own control point,
            // +use opens the click-radar (the HUD is client-side; HandleUseKey is the faithful server-side gate).
            else if (_world.GameType is XonoticGodot.Common.Gameplay.Onslaught ons)
                ons.HandleUseKey(caller);
            return true;
        }

        if (!WeaponImpulses.Handle(caller, imp))
            ctx.Print($"impulse {imp} not handled");
        return true;
    }

    // =============================================================================================
    // timeout / timein (QC common/command timeout)
    // =============================================================================================

    private bool CmdTimeout(CommandContext ctx)
    {
        if (_world.Timeout.CallTimeout(ctx.Caller, out string err)) ctx.Print("timeout called");
        else ctx.Print(err);
        return true;
    }

    private bool CmdTimein(CommandContext ctx)
    {
        if (_world.Timeout.CallTimein(ctx.Caller, out string err)) ctx.Print("timein");
        else ctx.Print(err);
        return true;
    }

    // =============================================================================================
    // bans (QC command/banning.qc)
    // =============================================================================================

    private bool CmdBan(CommandContext ctx)
    {
        if (ctx.ArgCount < 2) { ctx.Print("usage: ban <address> [bantime] [reason]"); return true; }
        string ip = ctx.Arg(1);
        float bantime = ctx.ArgCount >= 3 && ctx.ArgFloat(2) > 0f ? ctx.ArgFloat(2) : Cvars.FloatOr("g_ban_default_bantime", 5400f);
        int reasonArg = ctx.ArgCount >= 3 && ctx.ArgFloat(2) > 0f ? 3 : 2;
        string reason = ctx.ArgCount > reasonArg ? ctx.ArgTail(reasonArg) : "No reason provided";
        _world.Bans.Insert(ip, bantime, reason);
        ctx.Print($"banned {ip} for {bantime:0}s");
        return true;
    }

    private bool CmdKickBan(CommandContext ctx)
    {
        if (ctx.ArgCount < 2) { ctx.Print("usage: kickban <player> [bantime] [masksize] [reason]"); return true; }
        Player? target = FindPlayerByName(ctx.Arg(1));
        if (target is null) { ctx.Print($"no player matching \"{ctx.Arg(1)}\""); return true; }
        float bantime = ctx.ArgCount >= 3 && ctx.ArgFloat(2) > 0f ? ctx.ArgFloat(2) : Cvars.FloatOr("g_ban_default_bantime", 5400f);
        int masksize = ctx.ArgCount >= 4 ? (int)ctx.ArgFloat(3) : Cvars.Int("g_ban_default_masksize");
        string reason = ctx.ArgCount >= 5 ? ctx.ArgTail(4) : "No reason provided";
        _world.Bans.KickBanClient(target, bantime, masksize, reason);
        ctx.Print($"kickbanned {target.NetName}");
        return true;
    }

    private bool CmdUnban(CommandContext ctx)
    {
        string arg = ctx.Arg(1).TrimStart('#');
        if (!int.TryParse(arg, out int id) || id < 0) { ctx.Print("usage: unban <banid>"); return true; }
        ctx.Print(_world.Bans.Delete(id) ? $"unbanned #{id}" : $"no ban #{id}");
        return true;
    }

    private bool CmdBanList(CommandContext ctx)
    {
        // Bans.View routes through Bans.Log → ChatBroadcast; also echo to the caller.
        var captured = new List<string>();
        Action<string>? prev = _world.Bans.Log;
        _world.Bans.Log = s => captured.Add(s);
        _world.Bans.View();
        _world.Bans.Log = prev;
        foreach (string s in captured) ctx.Print(s);
        return true;
    }

    private bool CmdPlayBan(CommandContext ctx)
    {
        Player? target = FindPlayerByName(ctx.Arg(1));
        if (target is null) { ctx.Print($"no player matching \"{ctx.Arg(1)}\""); return true; }
        Bans.AddToList(target, "g_playban_list");
        // QC BanCommand_playban (banning.qc:186): run the REAL observer transition (PutObserverInServer), not just
        // flip the FRAGS_SPECTATOR scoreboard sentinel — otherwise the play-banned player stays a solid, shootable,
        // still-scoring actor (the same SPEC4/LOOP2 trap CmdSpectate already fixed).
        _world.Clients.PutObserverInServer(target);
        // QC banning.qc:187-188: if g_playban_minigames, also kick the player out of any in-progress minigame now.
        if (Cvars.Bool("g_playban_minigames"))
            _world.Minigames.Part(target);
        ctx.Print($"play-banned {target.NetName}");
        return true;
    }

    private bool CmdListAdd(CommandContext ctx, string listCvar, string verb)
    {
        Player? target = FindPlayerByName(ctx.Arg(1));
        if (target is null) { ctx.Print($"no player matching \"{ctx.Arg(1)}\""); return true; }
        Bans.AddToList(target, listCvar);
        ctx.Print($"{verb} {target.NetName}");
        return true;
    }

    /// <summary>QC <c>mute</c> (the chatban admin verb): add the player to the chatban list AND set the live
    /// <see cref="Player.Muted"/> fake-accept flag (server/chat.qc:255 — a muted player's chat is faked). [T46]</summary>
    private bool CmdMute(CommandContext ctx)
    {
        Player? target = FindPlayerByName(ctx.Arg(1));
        if (target is null) { ctx.Print($"no player matching \"{ctx.Arg(1)}\""); return true; }
        Bans.AddToList(target, "g_chatban_list");
        target.Muted = true;
        ctx.Print($"muted {target.NetName}");
        return true;
    }

    /// <summary>QC <c>unmute</c>: remove from the chatban list AND clear <see cref="Player.Muted"/>. [T46]</summary>
    private bool CmdUnmute(CommandContext ctx)
    {
        Player? target = FindPlayerByName(ctx.Arg(1));
        if (target is null) { ctx.Print($"no player matching \"{ctx.Arg(1)}\""); return true; }
        Bans.RemoveFromList(target, "g_chatban_list");
        target.Muted = false;
        ctx.Print($"unmuted {target.NetName}");
        return true;
    }

    private bool CmdListRemove(CommandContext ctx, string listCvar, string verb)
    {
        Player? target = FindPlayerByName(ctx.Arg(1));
        if (target is null) { ctx.Print($"no player matching \"{ctx.Arg(1)}\""); return true; }
        Bans.RemoveFromList(target, listCvar);
        ctx.Print($"{verb} {target.NetName}");
        return true;
    }

    // =============================================================================================
    // cheats (QC CheatCommand) + campaign warp + settemp
    // =============================================================================================

    private bool CmdCheat(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("cheat commands are client-only"); return true; }
        // Pass the live gametype so the `penalty` cheat can reach the active Race instance (race_ImposePenaltyTime).
        if (!_world.Cheats.Command(ctx.Caller, ctx.Argv, _world.GameType))
            ctx.Print("Cheats are not enabled on this server (sv_cheats).");
        return true;
    }

    private bool CmdWarp(CommandContext ctx)
    {
        if (!Cvars.Bool("g_campaign")) { ctx.Print("Not in a campaign."); return true; }
        if (ctx.ArgCount >= 2) _world.Campaign.LevelWarp((int)ctx.ArgFloat(1));
        else _world.Campaign.LevelWarp(-1);
        ctx.Print("campaign warp");
        return true;
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(sandbox, SV_ParseClientCommand)</c> (common/mutators/mutator/sandbox/sv_sandbox.qc:
    /// the <c>cmd_name == "g_sandbox"</c> branch). The client `sandbox` alias emits <c>cmd g_sandbox &lt;sub&gt; …</c>,
    /// so this routes the parsed argv (argv[0]="g_sandbox", argv[1]=subcommand) to the SandboxMutator backend.
    /// Client-only (a player drives the build mode). The mutator gates on g_sandbox/g_sandbox_readonly internally,
    /// so an unenabled / read-only server is faithfully inert. Feedback reaches the player via the mutator's
    /// host-wired <c>PrintTo</c> (GameWorld); nothing is echoed to ctx so the player isn't double-printed.
    /// </summary>
    private bool CmdSandbox(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("sandbox is a client command"); return true; }
        if (Mutators.ByName("sandbox") is not SandboxMutator sandbox)
            return true; // not registered (shouldn't happen — it's a [Mutator]); silently ignore like Base when off
        // QC: the hook only acts when the mutator is added (g_sandbox enabled). When disabled the command is a no-op
        // (Base's SV_ParseClientCommand never reaches the hook because the mutator isn't subscribed).
        if (!sandbox.Added)
            return true;
        sandbox.HandleCommand(ctx.Caller, ctx.Argv);
        return true;
    }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(superspec, SV_ParseClientCommand)</c> (common/mutators/mutator/superspec/
    /// sv_superspec.qc:141-358). Routes the spectator verbs (superspec / autospec / superspec_itemfilter /
    /// followpowerup / followstrength / followshield) to <see cref="SuperSpecMutator.HandleCommand"/>. Client-only
    /// (Base: the engine entry is a client stringcmd). The mutator gates on its added-state + the IS_PLAYER guard
    /// internally, so an unenabled server or a live player issuing it is faithfully inert. Output reaches the
    /// player via the mutator's host-wired <c>PrintTo</c> (NetGame.WireSuperspec); nothing is echoed to ctx.
    /// </summary>
    private bool CmdSuperspec(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("superspec is a client command"); return true; }
        if (Mutators.ByName("superspec") is not SuperSpecMutator superspec)
            return true; // not registered (shouldn't happen — it's a [Mutator])
        // QC: the hook only acts when the mutator is added (g_superspectate enabled). When disabled it's a no-op
        // (Base's SV_ParseClientCommand never reaches the hook because the mutator isn't subscribed).
        if (!superspec.Added)
            return true;
        superspec.HandleCommand(ctx.Caller, ctx.Argv);
        return true;
    }

    private bool CmdSettemp(CommandContext ctx)
    {
        if (ctx.ArgCount < 3) { ctx.Print("usage: settemp <cvar> <value>"); return true; }
        SettempCvars.Set(ctx.Arg(1), ctx.Arg(2));
        ctx.Print($"settemp {ctx.Arg(1)} = {ctx.Arg(2)}");
        return true;
    }

    private bool CmdSettempRestore(CommandContext ctx)
    {
        int n = SettempCvars.Restore();
        ctx.Print($"restored {n} cvar(s)");
        return true;
    }

    // =============================================================================================
    // introspection (QC teamstatus/who/time/info/cvar_changes)
    // =============================================================================================

    // QC Score_NicePrint column-layout constants (server/scores.qc:826-827).
    private const int NicePrintNameWidth = 22;   // NAMEWIDTH
    private const int NicePrintScoresWidth = 58; // SCORESWIDTH

    /// <summary>
    /// QC <c>CommonCommand_teamstatus</c> (server/command/common.qc:593) → <c>Score_NicePrint(caller)</c>
    /// (server/scores.qc:914): print a formatted text standings table to the requesting player. Reproduces the
    /// QC column layout faithfully — a per-team header row (team-colored name + each team-score slot), then each
    /// player (name padded/truncated to NAMEWIDTH + every labeled SP_* column), then the spectators block. The
    /// per-column width is <c>bound(6, floor(SCORESWIDTH / labeledColumns - 1), 9)</c> and column values are
    /// color-coded by their sort priority (primary ^3 / secondary ^5 / other ^7) via Score_NicePrint_ItemColor.
    /// </summary>
    private bool CmdTeamStatus(CommandContext ctx)
    {
        // QC: t = count of labeled Scores columns; w = bound(6, floor(SCORESWIDTH/t - 1), 9).
        var fields = XonoticGodot.Common.Gameplay.Scoring.GameScores.Fields;
        int labeled = 0;
        foreach (var f in fields)
            if (f.Label != "") ++labeled;
        int w = labeled > 0
            ? Math.Clamp((int)MathF.Floor((float)NicePrintScoresWidth / labeled - 1), 6, 9)
            : 6;

        bool teamGame = _world.Teamplay?.IsTeamGame ?? false;

        // QC: p = PlayerScore_Sort(...); t = -1; if (!teamscores) Score_NicePrint_Team(to, -1, w);
        var sorted = _world.Scores.Sorted();
        int lastTeam = -1;
        if (!teamGame)
            NicePrintTeamHeader(ctx, -1, w, fields);
        foreach (var row in sorted)
        {
            int team = (int)row.Player.Team;
            // QC: in teamplay, emit a team header each time the running team changes.
            if (teamGame && lastTeam != team)
                NicePrintTeamHeader(ctx, team, w, fields);
            NicePrintPlayer(ctx, row, w, fields);
            lastTeam = team;
        }

        // QC: FOREACH_CLIENT(!IS_PLAYER(it), ...) — the spectators block (header once, then each spectator).
        bool printedSpecHeader = false;
        foreach (Player p in _world.Clients.Players)
        {
            if (p.FragsStatus != Player.FragsSpectator) continue; // == !IS_PLAYER (observer/spectator)
            if (!printedSpecHeader) { ctx.Print("Spectators:"); printedSpecHeader = true; }
            ctx.Print("  " + NicePrintPlayerName(p));
        }
        return true;
    }

    /// <summary>QC <c>Score_NicePrint_ItemColor(vflags)</c>: primary ^3 / secondary ^5 / other ^7.</summary>
    private static string NicePrintItemColor(XonoticGodot.Common.Gameplay.Scoring.ScoreFlags fl)
    {
        if ((fl & XonoticGodot.Common.Gameplay.Scoring.ScoreFlags.SortPrioPrimary) != 0) return "^3";
        if ((fl & XonoticGodot.Common.Gameplay.Scoring.ScoreFlags.SortPrioSecondary) != 0) return "^5";
        return "^7";
    }

    /// <summary>QC <c>Team_ColoredFullName(teamid)</c>: "^cColor Team^7" for a NUM_TEAM_* code.</summary>
    private static string NicePrintTeamColoredFullName(int team)
    {
        string code = team switch
        {
            Teams.Red => "^1",
            Teams.Blue => "^4",
            Teams.Yellow => "^3",
            Teams.Pink => "^6",
            _ => "^7",
        };
        return code + Teams.Name(team) + " Team^7";
    }

    /// <summary>QC <c>playername(p.netname, p.team, false)</c>: the uncolored display name (raw netname).</summary>
    private static string NicePrintPlayerName(Player p) => p.NetName;

    /// <summary>QC <c>Score_NicePrint_Team</c> (server/scores.qc:838): the per-team header row.</summary>
    private void NicePrintTeamHeader(CommandContext ctx, int team, int w,
        IReadOnlyList<XonoticGodot.Common.Gameplay.Scoring.ScoreField> fields)
    {
        var sb = new StringBuilder();
        // QC: if the team has a scorekeeper, prefix the colored team name + each labeled team-score slot;
        // else just "Scores:". team == -1 (the no-teamplay path) has no scorekeeper → "Scores:".
        if (team >= 1)
        {
            sb.Append(NicePrintTeamColoredFullName(team));
            for (int slot = 0; slot < XonoticGodot.Common.Gameplay.Scoring.GameScores.MaxTeamScore; ++slot)
            {
                if (XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamLabel(slot) == "") continue;
                var fl = XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamFlags(slot);
                // slot 0 (ST_SCORE) honors the gametype's owned total via Scores.TeamScore; other slots read
                // the GameScores team store directly (QC reads sk.(teamscores(i)) for every slot).
                int sc = slot == XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamSlotScore
                    ? _world.Scores.TeamScore(team)
                    : XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamScore(team, slot);
                sb.Append(' ').Append(NicePrintItemColor(fl))
                  .Append(XonoticGodot.Common.Gameplay.Scoring.GameScores.ScoreString(fl, sc, 0));
            }
        }
        else
        {
            sb.Append("Scores:");
        }

        // QC: strpad(max(0, NAMEWIDTH - strlennocol(s)), "") — pad the name region out to NAMEWIDTH.
        int pad = Math.Max(0, NicePrintNameWidth - VisibleLen(sb.ToString()));
        sb.Append(' ', pad);

        // QC: each labeled column header, truncated to w chars, left-justified to width w (strpad(-w, ...)).
        foreach (var f in fields)
        {
            if (f.Label == "") continue;
            string head = f.Label.Length > w ? f.Label.Substring(0, w) : f.Label;
            sb.Append(' ').Append(NicePrintItemColor(f.Flags)).Append(head.PadRight(w));
        }
        ctx.Print(sb.ToString());
    }

    /// <summary>QC <c>Score_NicePrint_Player</c> (server/scores.qc:873): one player's standings row.</summary>
    private void NicePrintPlayer(CommandContext ctx, PlayerScoreRow row, int w,
        IReadOnlyList<XonoticGodot.Common.Gameplay.Scoring.ScoreField> fields)
    {
        // QC: s = "  " + playername; then loop — i = strlennocol(s) - NAMEWIDTH; while i > 0 trim i raw chars
        // off the end and recompute (color codes mean a raw trim may not drop the visible length by i); once
        // i <= 0, pad |i| trailing spaces and break.
        string s = "  " + NicePrintPlayerName(row.Player);
        for (;;)
        {
            int i = VisibleLen(s) - NicePrintNameWidth;
            if (i > 0)
                s = s.Substring(0, Math.Max(0, s.Length - i));
            else
            {
                s += new string(' ', -i);
                break;
            }
        }
        var sb = new StringBuilder(s);

        foreach (var f in fields)
        {
            if (f.Label == "") continue;
            int sc = XonoticGodot.Common.Gameplay.Scoring.GameScores.Get(row.Player, f);
            string val = XonoticGodot.Common.Gameplay.Scoring.GameScores.ScoreString(f.Flags, sc, 0);
            sb.Append(' ').Append(NicePrintItemColor(f.Flags)).Append(val.PadRight(w));
        }
        ctx.Print(sb.ToString());
    }

    /// <summary>QC <c>strlennocol</c>: visible length, ignoring ^-color codes.</summary>
    private static int VisibleLen(string s) => XonoticGodot.Common.Diagnostics.Log.StripColors(s).Length;

    // =============================================================================================
    // score log (QC DumpStats / GetPlayerScoreString / GetTeamScoreString / GetScoreLogLabel,
    // server/world.qc:1264 + server/scores.qc:599) — `printstats` command + intermission call
    // =============================================================================================

    /// <summary>
    /// QC <c>GetScoreLogLabel(label, fl)</c> (server/scores.qc:599): append sort-priority + lower-is-better
    /// suffixes to the column name so log-parsers know which column wins ties and which direction is better.
    /// Order: append <c>&lt;</c> for LowerIsBetter, then <c>!!</c> for PRIMARY, <c>!</c> for SECONDARY.
    /// </summary>
    private static string GetScoreLogLabel(string label, XonoticGodot.Common.Gameplay.Scoring.ScoreFlags fl)
    {
        if ((fl & XonoticGodot.Common.Gameplay.Scoring.ScoreFlags.LowerIsBetter) != 0)
            label += "<";
        if ((fl & XonoticGodot.Common.Gameplay.Scoring.ScoreFlagsExtensions.SortPrioMask) == XonoticGodot.Common.Gameplay.Scoring.ScoreFlags.SortPrioPrimary)
            label += "!!";
        else if ((fl & XonoticGodot.Common.Gameplay.Scoring.ScoreFlagsExtensions.SortPrioMask) == XonoticGodot.Common.Gameplay.Scoring.ScoreFlags.SortPrioSecondary)
            label += "!";
        return label;
    }

    /// <summary>
    /// QC <c>GetPlayerScoreString(pl, shortString)</c> (server/scores.qc:610): when <paramref name="pl"/> is null,
    /// returns the comma-separated column-label header in declaration order: primary!!, then secondary!, then others;
    /// when non-null, returns the matching comma-separated numeric values from that player's scorekeeper. The
    /// <paramref name="shortString"/> param mirrors QC (0 = full; 1 = primary+secondary; 2 = primary only) — the
    /// call sites in DumpStats always pass 0 (shortString=false).
    /// </summary>
    private static string GetPlayerScoreString(Player? pl, bool shortString = false)
    {
        var sb = new System.Text.StringBuilder();
        var fields = XonoticGodot.Common.Gameplay.Scoring.GameScores.Fields;
        if (pl is null)
        {
            // label header — primary first, then secondary, then the rest (unless shortString trims them)
            foreach (var f in fields)
                if (f.Label != "" && !f.ClientOnly && (f.Flags & XonoticGodot.Common.Gameplay.Scoring.ScoreFlagsExtensions.SortPrioMask) == XonoticGodot.Common.Gameplay.Scoring.ScoreFlags.SortPrioPrimary)
                    sb.Append(GetScoreLogLabel(f.Label, f.Flags)).Append(',');
            if (!shortString)
            {
                foreach (var f in fields)
                    if (f.Label != "" && !f.ClientOnly && (f.Flags & XonoticGodot.Common.Gameplay.Scoring.ScoreFlagsExtensions.SortPrioMask) == XonoticGodot.Common.Gameplay.Scoring.ScoreFlags.SortPrioSecondary)
                        sb.Append(GetScoreLogLabel(f.Label, f.Flags)).Append(',');
                foreach (var f in fields)
                    if (f.Label != "" && !f.ClientOnly
                        && (f.Flags & XonoticGodot.Common.Gameplay.Scoring.ScoreFlagsExtensions.SortPrioMask) != XonoticGodot.Common.Gameplay.Scoring.ScoreFlags.SortPrioPrimary
                        && (f.Flags & XonoticGodot.Common.Gameplay.Scoring.ScoreFlagsExtensions.SortPrioMask) != XonoticGodot.Common.Gameplay.Scoring.ScoreFlags.SortPrioSecondary)
                        sb.Append(GetScoreLogLabel(f.Label, f.Flags)).Append(',');
            }
        }
        else
        {
            // numeric values — same order: primary, secondary, rest
            foreach (var f in fields)
                if (f.Label != "" && !f.ClientOnly && (f.Flags & XonoticGodot.Common.Gameplay.Scoring.ScoreFlagsExtensions.SortPrioMask) == XonoticGodot.Common.Gameplay.Scoring.ScoreFlags.SortPrioPrimary)
                    sb.Append(XonoticGodot.Common.Gameplay.Scoring.GameScores.Get(pl, f)).Append(',');
            if (!shortString)
            {
                foreach (var f in fields)
                    if (f.Label != "" && !f.ClientOnly && (f.Flags & XonoticGodot.Common.Gameplay.Scoring.ScoreFlagsExtensions.SortPrioMask) == XonoticGodot.Common.Gameplay.Scoring.ScoreFlags.SortPrioSecondary)
                        sb.Append(XonoticGodot.Common.Gameplay.Scoring.GameScores.Get(pl, f)).Append(',');
                foreach (var f in fields)
                    if (f.Label != "" && !f.ClientOnly
                        && (f.Flags & XonoticGodot.Common.Gameplay.Scoring.ScoreFlagsExtensions.SortPrioMask) != XonoticGodot.Common.Gameplay.Scoring.ScoreFlags.SortPrioPrimary
                        && (f.Flags & XonoticGodot.Common.Gameplay.Scoring.ScoreFlagsExtensions.SortPrioMask) != XonoticGodot.Common.Gameplay.Scoring.ScoreFlags.SortPrioSecondary)
                        sb.Append(XonoticGodot.Common.Gameplay.Scoring.GameScores.Get(pl, f)).Append(',');
            }
        }
        // QC: substring(out, 0, strlen(out) - 1) — strip trailing comma
        if (sb.Length > 0 && sb[sb.Length - 1] == ',') sb.Length--;
        return sb.ToString();
    }

    /// <summary>
    /// QC <c>GetTeamScoreString(tm, shortString)</c> (server/scores.qc:671): tm=0 → column-label header;
    /// tm&gt;0 → numeric team-score values (primary, then secondary, then rest). Like GetPlayerScoreString,
    /// shortString=false (the only caller, DumpStats, always passes 0).
    /// </summary>
    private static string GetTeamScoreString(int tm, bool shortString = false)
    {
        var sb = new System.Text.StringBuilder();
        if (tm == 0)
        {
            // label header
            for (int i = 0; i < XonoticGodot.Common.Gameplay.Scoring.GameScores.MaxTeamScore; i++)
                if (XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamLabel(i) != ""
                    && (XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamFlags(i) & XonoticGodot.Common.Gameplay.Scoring.ScoreFlagsExtensions.SortPrioMask) == XonoticGodot.Common.Gameplay.Scoring.ScoreFlags.SortPrioPrimary)
                    sb.Append(GetScoreLogLabel(XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamLabel(i), XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamFlags(i))).Append(',');
            if (!shortString)
            {
                for (int i = 0; i < XonoticGodot.Common.Gameplay.Scoring.GameScores.MaxTeamScore; i++)
                    if (XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamLabel(i) != ""
                        && (XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamFlags(i) & XonoticGodot.Common.Gameplay.Scoring.ScoreFlagsExtensions.SortPrioMask) == XonoticGodot.Common.Gameplay.Scoring.ScoreFlags.SortPrioSecondary)
                        sb.Append(GetScoreLogLabel(XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamLabel(i), XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamFlags(i))).Append(',');
                for (int i = 0; i < XonoticGodot.Common.Gameplay.Scoring.GameScores.MaxTeamScore; i++)
                    if (XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamLabel(i) != ""
                        && (XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamFlags(i) & XonoticGodot.Common.Gameplay.Scoring.ScoreFlagsExtensions.SortPrioMask) != XonoticGodot.Common.Gameplay.Scoring.ScoreFlags.SortPrioPrimary
                        && (XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamFlags(i) & XonoticGodot.Common.Gameplay.Scoring.ScoreFlagsExtensions.SortPrioMask) != XonoticGodot.Common.Gameplay.Scoring.ScoreFlags.SortPrioSecondary)
                        sb.Append(GetScoreLogLabel(XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamLabel(i), XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamFlags(i))).Append(',');
            }
        }
        else
        {
            // numeric values: QC teamscorekeepers[tm-1] non-null check — we always have storage so write directly.
            for (int i = 0; i < XonoticGodot.Common.Gameplay.Scoring.GameScores.MaxTeamScore; i++)
                if (XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamLabel(i) != ""
                    && (XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamFlags(i) & XonoticGodot.Common.Gameplay.Scoring.ScoreFlagsExtensions.SortPrioMask) == XonoticGodot.Common.Gameplay.Scoring.ScoreFlags.SortPrioPrimary)
                    sb.Append(XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamScore(tm, i)).Append(',');
            if (!shortString)
            {
                for (int i = 0; i < XonoticGodot.Common.Gameplay.Scoring.GameScores.MaxTeamScore; i++)
                    if (XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamLabel(i) != ""
                        && (XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamFlags(i) & XonoticGodot.Common.Gameplay.Scoring.ScoreFlagsExtensions.SortPrioMask) == XonoticGodot.Common.Gameplay.Scoring.ScoreFlags.SortPrioSecondary)
                        sb.Append(XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamScore(tm, i)).Append(',');
                for (int i = 0; i < XonoticGodot.Common.Gameplay.Scoring.GameScores.MaxTeamScore; i++)
                    if (XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamLabel(i) != ""
                        && (XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamFlags(i) & XonoticGodot.Common.Gameplay.Scoring.ScoreFlagsExtensions.SortPrioMask) != XonoticGodot.Common.Gameplay.Scoring.ScoreFlags.SortPrioPrimary
                        && (XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamFlags(i) & XonoticGodot.Common.Gameplay.Scoring.ScoreFlagsExtensions.SortPrioMask) != XonoticGodot.Common.Gameplay.Scoring.ScoreFlags.SortPrioSecondary)
                        sb.Append(XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamScore(tm, i)).Append(',');
            }
        }
        if (sb.Length > 0 && sb[sb.Length - 1] == ',') sb.Length--;
        return sb.ToString();
    }

    /// <summary>
    /// QC <c>DumpStats(final)</c> (server/world.qc:1264): emit the colon-delimited score-log block to the
    /// console / event-log / file sinks as appropriate. When <paramref name="final"/> is true (intermission),
    /// the header token is <c>:scores:</c> and the eventlog sink is enabled; when false (printstats command),
    /// the header token is <c>:status:</c>, always to console, never to eventlog. The QC sink logic:
    /// <list type="bullet">
    /// <item>to_console = sv_logscores_console (overridden to true when !final)</item>
    /// <item>to_eventlog = sv_eventlog (cleared when !final; also cleared when sv_eventlog_console avoids doubles)</item>
    /// <item>file: the port omits the per-call file open/close (Base appends to sv_logscores_filename) — the
    ///   event-log file sink (sv_eventlog_files) already covers structured logging for the port.</item>
    /// </list>
    /// Called at intermission from <see cref="GameWorld.NextLevel"/> (final=true) and via the
    /// <c>printstats</c> server command (final=false).
    /// </summary>
    internal void DumpStats(bool final)
    {
        bool toConsole = Cvars.Bool("sv_logscores_console");
        bool toEventlog = final && Cvars.Bool("sv_eventlog");
        if (!final)
            toConsole = true; // printstats: always to console

        // QC: if sv_eventlog_console is on, the eventlog already writes to console → suppress to_console to avoid doubles
        if (toEventlog && Cvars.Bool("sv_eventlog_console"))
            toConsole = false;

        string gametype = _world.GameType?.NetName ?? "dm";
        string mapName = _world.MapName ?? "";
        string s = (final ? ":scores:" : ":status:") + gametype + "_" + mapName + ":" + (int)MathF.Round(_world.Time);
        if (toConsole) _world.GameLog.ConsoleSink?.Invoke(s);
        if (toEventlog) _world.GameLog.Echo(s);

        // :labels:player:<col!!,col!,col,...>
        s = ":labels:player:" + GetPlayerScoreString(null);
        if (toConsole) _world.GameLog.ConsoleSink?.Invoke(s);
        if (toEventlog) _world.GameLog.Echo(s);

        // per-player rows: real clients (human + bots). QC: IS_REAL_CLIENT || (IS_BOT_CLIENT && logscores_bots).
        bool logBots = Cvars.Bool("sv_logscores_bots");
        foreach (Player p in _world.Clients.Players)
        {
            if (p.IsBot && !logBots) continue;

            float jointime = _world.Clients.InfoOf(p)?.JoinTime ?? 0f;
            int elapsed = (int)MathF.Round(MathF.Max(0f, _world.Time - jointime));
            bool isPlayer = p.FragsStatus != Player.FragsSpectator;
            string teamOrSpec = isPlayer ? ((int)p.Team).ToString(System.Globalization.CultureInfo.InvariantCulture) : "spectator";

            // QC :player:see-labels:<scores>:<elapsed>:<team|spectator>:<playerid>:<name>
            // console omits playerid (LOG_HELP just gets name appended); eventlog includes playerid.
            string prefix = ":player:see-labels:" + GetPlayerScoreString(p) + ":" + elapsed + ":" + teamOrSpec + ":";
            if (toConsole) _world.GameLog.ConsoleSink?.Invoke(prefix + p.NetName);
            if (toEventlog) _world.GameLog.Echo(prefix + p.PlayerId + ":" + p.NetName);
        }

        // teamplay: :labels:teamscores: + per-team :teamscores:see-labels: for teams 1..15
        if (XonoticGodot.Common.Gameplay.Scoring.GameScores.Teamplay)
        {
            s = ":labels:teamscores:" + GetTeamScoreString(0);
            if (toConsole) _world.GameLog.ConsoleSink?.Invoke(s);
            if (toEventlog) _world.GameLog.Echo(s);

            for (int i = 1; i < 16; i++)
            {
                // QC: `else if((sk = teamscorekeepers[tm - 1]))` — only emit for teams that actually have a
                // scorekeeper. The port keys team scores by team COLOUR CODE (Red=4/Blue=13/Yellow=12/Pink=9),
                // so the active team set is the key set of the score dict; an unused colour-code index gets no row.
                if (!XonoticGodot.Common.Gameplay.Scoring.GameScores.TeamScores.ContainsKey(i)) continue;
                string ts = GetTeamScoreString(i);
                if (ts == "") continue; // no labeled team-score slots at all
                s = ":teamscores:see-labels:" + ts + ":" + i;
                if (toConsole) _world.GameLog.ConsoleSink?.Invoke(s);
                if (toEventlog) _world.GameLog.Echo(s);
            }
        }

        if (toConsole) _world.GameLog.ConsoleSink?.Invoke(":end");
        if (toEventlog) _world.GameLog.Echo(":end");
    }

    /// <summary>QC <c>sv_cmd.qc:1249</c> <c>printstats</c>: dump a non-final score snapshot to the console.
    /// Always prints regardless of sv_logscores_console; never to the eventlog (final=false).</summary>
    private bool CmdPrintStats(CommandContext ctx)
    {
        DumpStats(final: false);
        return true;
    }

    /// <summary>
    /// QC <c>sv_cmd.qc:88</c> <c>GameCommand_adminmsg</c>: send an admin message to one or more players. Resolves
    /// targets by entity ID or name (comma-separated), then to each valid target sends BOTH a centerprint
    /// (<c>^3&lt;admin&gt;:\n^7&lt;msg&gt;</c>) AND a chat line (<c>^3&lt;admin&gt;^7: &lt;msg&gt;</c>), exactly as QC.
    /// The admin name is QC <c>GetCallerName(NULL)</c> — note QC ALWAYS passes NULL here, so it is the server admin
    /// nick (<c>sv_adminnick</c>, or "SERVER ADMIN" when unset), never the actual caller's name. The success summary
    /// is broadcast (QC <c>bprint</c>). The headless core has no infobar, so the <c>infobartime</c> path falls back
    /// to the centerprint/chat delivery; the value is accepted for cfg compatibility. Usage:
    /// <c>sv_cmd adminmsg &lt;clients&gt; "&lt;message&gt;" [infobartime]</c>.
    /// </summary>
    private bool CmdAdminMsg(CommandContext ctx)
    {
        if (ctx.ArgCount < 3)
        {
            ctx.Print("usage: adminmsg <clients> \"<message>\" [infobartime]");
            ctx.Print("  <clients> is a list (separated by commas) of player entity ID's or nicknames");
            ctx.Print("  If infobartime is provided, the message would be sent to infobar (no infobar in headless core)");
            ctx.Print("  Otherwise, it will be sent as a centerprint message.");
            return true;
        }

        string clientArg = ctx.Arg(1).Replace(",", " "); // QC strreplace(",", " ", targets)
        string message = ctx.Arg(2);
        if (string.IsNullOrEmpty(message)) // QC guard: if ((targets) && (admin_message))
            return true;

        // QC GetCallerName(NULL): adminmsg always attributes to the server admin nick, NOT the caller.
        // sv_adminnick when set, else "SERVER ADMIN" (server/command/common.qc:33-37).
        string adminNick = Cvars.String("sv_adminnick");
        if (string.IsNullOrEmpty(adminNick)) adminNick = "SERVER ADMIN";

        var successful = new List<string>();
        var tokens = clientArg.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);

        foreach (string token in tokens)
        {
            Player? target = FindPlayerByNameOrId(token);
            if (target is null)
            {
                ctx.Print($"adminmsg: no player matching \"{token}\"");
                continue;
            }

            // QC (infobartime == 0 path): centerprint(client, "^3<admin>:\n^7<msg>") AND
            //   sprint(client, "...^3<admin>^7: <msg>\n"). The headless core has no infobar, so the infobartime
            //   path also lands here. Centerprint via the live notification channel; chat line via ChatToPlayer.
            NotificationSystem.SendCenterRaw(NotifBroadcast.OneOnly, target, $"^3{adminNick}:\n^7" + message);
            ChatToPlayer?.Invoke(target, $"^3{adminNick}^7: {message}");
            successful.Add(target.NetName);
        }

        // QC: if (successful) bprint("Successfully sent message '", admin_message, "' to ", successful, ".\n");
        //     else LOG_INFO("No players given (...) could receive the message.");
        if (successful.Count > 0)
            ChatBroadcast?.Invoke($"Successfully sent message '{message}' to {string.Join(", ", successful)}.");
        else
            ctx.Print("No players could receive the message.");
        return true;
    }

    /// <summary>
    /// QC <c>sv_cmd.qc:628</c> <c>delrec</c> → <c>race_deleteTime(map, pos)</c> (server/race.qc:461): delete race
    /// records from the given rank up through <c>RANKINGS_CNT</c> on the current map (or a specified map), shifting
    /// higher ranks down. Usage: <c>sv_cmd delrec &lt;rank&gt; [&lt;map&gt;]</c>. The rank is 1-based (rank 1 = server
    /// record). race_deleteTime operates on the single global <c>record_type</c> (RACE_RECORD by default,
    /// CTS_RECORD under the CTS gametype) — NOT both tables — so this resolves the active gametype's record type
    /// the same way QC's <c>record_type</c> global does (cf. CommandReplies.GetRankings).
    /// </summary>
    private bool CmdDelRec(CommandContext ctx)
    {
        if (ctx.ArgCount < 2)
        {
            ctx.Print("usage: delrec <rank> [map]");
            ctx.Print("  <rank> is the ranking up to which records will be deleted");
            ctx.Print("  if <map> is not provided it will use the current map");
            return true;
        }

        if (!int.TryParse(ctx.Arg(1), out int rank) || rank < 1)
        {
            ctx.Print("rank must be a positive integer");
            return true;
        }

        string map = ctx.ArgCount >= 3 ? ctx.Arg(2) : _world.MapName;
        if (string.IsNullOrEmpty(map))
        {
            ctx.Print("no map loaded");
            return true;
        }

        // QC race_deleteTime acts on the single global record_type, which is CTS_RECORD under the CTS gametype and
        // RACE_RECORD otherwise (server/world.qc:872 default + the per-gametype overrides). Resolve it the same way
        // CommandReplies.GetRankings does, so delrec deletes exactly the table the active mode files times into.
        string gt = _world.GameType?.NetName ?? "";
        string recordType = gt == "cts"
            ? XonoticGodot.Common.Gameplay.RaceRecords.CtsRecord
            : XonoticGodot.Common.Gameplay.RaceRecords.RaceRecord;
        XonoticGodot.Common.Gameplay.RaceRecords.DeleteTime(map, recordType, rank);
        ctx.Print($"Deleted records up to rank {rank} on map {map}");
        return true;
    }

    /// <summary>
    /// QC <c>sv_cmd.qc</c> <c>GameCommand_effectindexdump</c>: dump the networked named-effect registry —
    /// each <c>EFFECT_*</c> entry's effectinfo name and its network id — bracketed by <c>begin</c>/<c>end</c>
    /// lines, exactly as the QC <c>FOREACH(Effects, true, ...)</c> sprint loop does. QC also writes the dump to
    /// <c>effectinfo_dump.txt</c> for the offline effectinfo round-trip tool; the headless core has no map-side
    /// file write here, so this delivers the load-bearing console dump (the id↔name table). <see cref="Effects"/>
    /// is the C# successor to QC's <c>REGISTRY(Effects)</c>; <see cref="Effects.All"/> is already network-id
    /// ordered (post-<c>Effects.Sort</c>, <c>RegistryId == index</c>), matching the QC registry iteration order
    /// and content hash so the dumped ids agree with what is networked on the wire.
    /// </summary>
    private bool CmdEffectIndexDump(CommandContext ctx)
    {
        // QC: sprint(caller, "begin\n"); FOREACH(Effects, true, { sprint(caller, ... it.eent_eff_name, it.m_id) });
        // it.eent_eff_name is the effectinfo.txt name (port: Effect.NetName); it.m_id is the network id
        // (port: Effect.RegistryId). EFFECT_Null (id 0, empty NetName) is included, like QC's FOREACH(..., true, ...).
        ctx.Print("begin");
        foreach (XonoticGodot.Common.Gameplay.Effect fx in XonoticGodot.Common.Gameplay.Effects.All)
            ctx.Print($"effect {fx.NetName} is {fx.RegistryId}");
        ctx.Print($"end ({XonoticGodot.Common.Gameplay.Effects.Count} effects)");
        return true;
    }

    private bool CmdWho(CommandContext ctx)
    {
        // QC CommonCommand_who (common.qc): the ent/name/ping/pl/time/ip/crypto_id table with the
        // sv_status_privacy hiding. privacy = caller && sv_status_privacy (the server console always sees all).
        bool privacy = !ctx.IsServerConsole && Cvars.Bool("sv_status_privacy");
        // QC: separator = strreplace("%", " ", strcat(argv(1) ? argv(1) : " ", "^7")). The col gaps use the
        // separator; default is a single space.
        string separator = (ctx.ArgCount >= 2 && ctx.Arg(1) != "" ? ctx.Arg(1) : " ").Replace("%", " ") + "^7";
        string sp = separator; // inter-column separator

        ctx.Print("List of client information" + (privacy ? " (some data is hidden for privacy)" : "") + ":");
        ctx.Print($" #{"ent",-3}{sp}{"nickname",-20}{sp}{"ping",-5}{sp}{"pl",-3}{sp}{"time",-9}{sp}{"ip",-16}{sp}crypto_id ");

        int total = 0;
        var fmt = System.Globalization.CultureInfo.InvariantCulture;
        foreach (Player p in _world.Clients.Players)
        {
            string addr, crypto;
            if (p.IsBot) { addr = "null/botclient"; crypto = "null/botclient"; }
            else if (privacy) { addr = "hidden"; crypto = "hidden"; }
            else { addr = string.IsNullOrEmpty(p.NetAddress) ? "" : p.NetAddress; crypto = p.PersistentId; }

            // ping / packetloss: the headless core carries no live ping (the net host owns it); 0 here.
            int ping = 0, pl = 0;
            // QC process_time(1, time - jointime) = HH:MM:SS since join.
            float jointime = _world.Clients.InfoOf(p)?.JoinTime ?? 0f;
            int secs = (int)MathF.Max(0f, _world.Time - jointime);
            string t = $"{secs / 3600:00}:{(secs / 60) % 60:00}:{secs % 60:00}";

            ctx.Print($" #{p.Index,-3}{sp}{Truncate(p.NetName, 20),-20}{sp}{ping.ToString(fmt),-5}{sp}{pl.ToString(fmt),-3}{sp}{t,-9}{sp}{addr,-16}{sp}{crypto} ");
            total++;
        }
        // QC maxclients (engine slot count). The Godot-free core has no engine maxclients cvar; fall back to the
        // live roster size when it is unset so the footer still reads sensibly.
        int slots = Cvars.Int("maxclients");
        if (slots <= 0) slots = total;
        ctx.Print($"Finished listing {total} client(s) out of {slots} slots.");
        return true;
    }

    /// <summary>QC the <c>%-20.20s</c> precision: truncate to at most <paramref name="max"/> chars.</summary>
    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

    private bool CmdTime(CommandContext ctx)
    {
        // QC CommonCommand_time (common.qc): print the several engine clock readouts. The port maps the sim clock
        // (time/frame-start) to _world.Time and the wall-clock readouts (realtime/uptime/local/gmtime) to the host
        // process clock — GETTIME_REALTIME is the OS clock, uptime the seconds since process start.
        var fmt = System.Globalization.CultureInfo.InvariantCulture;
        ctx.Print($"time = {_world.Time.ToString("0.######", fmt)}");
        ctx.Print($"frame start = {_world.Time.ToString("0.######", fmt)}");
        double realtime = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
        ctx.Print($"realtime = {realtime.ToString("0.######", fmt)}");
        double uptime = (DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds;
        ctx.Print($"uptime = {uptime.ToString("0.######", fmt)}");
        ctx.Print($"localtime = {DateTime.Now.ToString("ddd MMM dd HH:mm:ss yyyy", fmt)}");
        ctx.Print($"gmtime = {DateTime.UtcNow.ToString("ddd MMM dd HH:mm:ss yyyy", fmt)}");
        return true;
    }

    private bool CmdInfo(CommandContext ctx)
    {
        if (ctx.ArgCount < 2) { ctx.Print("usage: info <request>"); return true; }
        string s = Cvars.String($"sv_info_{ctx.Arg(1)}");
        ctx.Print(string.IsNullOrEmpty(s) ? $"no info for \"{ctx.Arg(1)}\"" : s);
        return true;
    }

    private bool CmdCvarChanges(CommandContext ctx)
    {
        // QC CommonCommand_cvar_changes: print_to(caller, cvar_changes). The log is rebuilt lazily so it
        // reflects any cvars changed after boot (QC builds it once at init; rebuilding on read is a superset).
        _world.Commands.Replies.Recompute();
        ctx.Print(_world.Commands.Replies.CvarChanges);
        return true;
    }

    private bool CmdCvarPureChanges(CommandContext ctx)
    {
        // QC CommonCommand_cvar_purechanges: print_to(caller, cvar_purechanges).
        _world.Commands.Replies.Recompute();
        ctx.Print(_world.Commands.Replies.CvarPurechanges);
        return true;
    }

    // =============================================================================================
    // engine defer (DP cmd.c Cmd_Defer_f) + the generic command family on the server bus
    // =============================================================================================

    /// <summary>
    /// DP <c>Cmd_Defer_f</c>: <c>defer</c> (list pending), <c>defer clear</c> (drop all), <c>defer &lt;s&gt;
    /// &lt;command&gt;</c> (enqueue). The port enqueues <c>ArgTail(2)</c> (so an unquoted multi-word
    /// <c>defer 1 say hi</c> also works — a harmless superset of DP's single-token <c>argv(2)</c>). The pump
    /// fires queued commands back through <see cref="Execute"/> from the server tick (GameWorld.OnStartFrame).
    /// </summary>
    private bool CmdDefer(CommandContext ctx)
    {
        if (ctx.ArgCount == 1)
        {
            foreach (string line in Deferred.Describe(_world.Time))
                ctx.Print(line);
            return true;
        }
        if (ctx.ArgCount == 2 && string.Equals(ctx.Arg(1), "clear", StringComparison.OrdinalIgnoreCase))
        {
            Deferred.Clear();
            return true;
        }
        if (ctx.ArgCount >= 3 && ctx.ArgTail(2).Length > 0)
        {
            Deferred.Defer(ctx.ArgFloat(1), ctx.ArgTail(2), _world.Time);
            return true;
        }
        ctx.Print("usage: defer <seconds> <command>");
        ctx.Print("       defer clear");
        return true;
    }

    /// <summary>QC GENERIC_COMMAND(rpn): run the RPN VM against the server cvar store, printing diagnostics.</summary>
    private bool CmdRpn(CommandContext ctx)
    {
        XonoticGodot.Engine.Console.Rpn.Run(ctx.Argv, _world.Services.CvarsImpl, ctx.Print);
        return true;
    }

    /// <summary>QC GenericCommand_addtolist: append a value to a list cvar (deduped, at the END).</summary>
    private bool CmdAddToList(CommandContext ctx)
    {
        if (ctx.ArgCount < 3) { ctx.Print($"Usage: {ctx.Arg(0)} <cvar> <value>"); return true; }
        string cvar = ctx.Arg(1), value = ctx.Arg(2);
        string cur = Cvars.String(cvar);
        if (cur == "") { Cvars.Set(cvar, value); return true; }
        foreach (string w in XonoticGodot.Engine.Console.WordList.Words(cur))
            if (w == value) return true; // already present
        Cvars.Set(cvar, XonoticGodot.Engine.Console.WordList.Cons(cur, value));
        return true;
    }

    /// <summary>QC GenericCommand_removefromlist: rebuild a list cvar keeping only words != value.</summary>
    private bool CmdRemoveFromList(CommandContext ctx)
    {
        if (ctx.ArgCount != 3) { ctx.Print($"Usage: {ctx.Arg(0)} <cvar> <value>"); return true; }
        string cvar = ctx.Arg(1), removal = ctx.Arg(2);
        string rebuilt = "";
        foreach (string w in XonoticGodot.Engine.Console.WordList.Words(Cvars.String(cvar)))
            if (w != removal) rebuilt = XonoticGodot.Engine.Console.WordList.Cons(rebuilt, w);
        Cvars.Set(cvar, rebuilt);
        return true;
    }

    /// <summary>QC GenericCommand_maplist: add (PREPEND) / remove / shuffle / cleanup g_maplist.</summary>
    private bool CmdMaplist(CommandContext ctx)
    {
        switch (ctx.Arg(1))
        {
            case "add" when ctx.ArgCount == 3:
            {
                string cur = Cvars.String("g_maplist");
                // QC PREPENDS for maplist add (unlike addtolist's append).
                Cvars.Set("g_maplist", cur == "" ? ctx.Arg(2) : ctx.Arg(2) + " " + cur);
                return true;
            }
            case "remove" when ctx.ArgCount == 3:
            {
                string rebuilt = "";
                foreach (string w in XonoticGodot.Engine.Console.WordList.Words(Cvars.String("g_maplist")))
                    if (w != ctx.Arg(2)) rebuilt = XonoticGodot.Engine.Console.WordList.Cons(rebuilt, w);
                Cvars.Set("g_maplist", rebuilt);
                return true;
            }
            case "shuffle":
                Cvars.Set("g_maplist", XonoticGodot.Engine.Console.WordList.Shuffle(Cvars.String("g_maplist"), _maplistRng));
                return true;
            case "cleanup":
                // No reachable map catalog here → identity (keep all words), matching the console surface (R4).
                return true;
        }
        ctx.Print("Usage: maplist <action> [<map>] — actions: add, cleanup, remove, shuffle");
        return true;
    }

    private readonly Random _maplistRng = new();

    /// <summary>QC GenericCommand_nextframe: run a command on the next server tick (defer with delay 0).</summary>
    private bool CmdNextFrame(CommandContext ctx)
    {
        if (ctx.ArgCount < 2) { ctx.Print("Usage: nextframe <command>"); return true; }
        Deferred.Defer(0f, ctx.ArgTail(1), _world.Time);
        return true;
    }

    // =============================================================================================
    // server client commands (QC server/command/cmd.qc ClientCommand_*)
    // =============================================================================================

    /// <summary>
    /// QC <c>ClientCommand_voice</c>: play a voice taunt. Validates the voice type against the known set, then
    /// (unless dead/spectating, which silently no-op) emits the taunt sound from the caller. The taunt sample
    /// registry (<c>allvoicesamples</c>/<c>GetVoiceMessage</c>) isn't ported, so this validates against a
    /// built-in name list and emits a by-name sound on the Voice channel (deviation R7 — not full asset parity).
    /// </summary>
    private bool CmdVoice(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("voice is a client command"); return true; }
        if (ctx.ArgCount < 2)
        {
            ctx.Print($"Usage:^3 cmd voice <voicetype> [<message>]");
            ctx.Print($"  one of: {string.Join(" ", VoiceTypes)}");
            return true;
        }
        string voiceType = ctx.Arg(1).ToLowerInvariant();
        if (System.Array.IndexOf(VoiceTypes, voiceType) < 0)
        {
            // QC sprints the invalid warning even to dead/spec callers (so they can learn the names in peace).
            ctx.Print($"Invalid voice. Use one of: {string.Join(" ", VoiceTypes)}");
            return true;
        }
        Player p = ctx.Caller;
        if (p.IsDead) return true;                                   // QC cmd.qc:1047 — dead can't taunt (silent)
        if (p.IsObserver || p.FragsStatus == Player.FragsSpectator) return true; // QC cmd.qc:1053 — no body (silent)

        // QC cmd.qc:1059-1061 — the optional [<message>] arg (everything from token 2 onward).
        string msg = ctx.ArgCount >= 3 ? ctx.ArgTail(2) : "";

        // QC VoiceMessage(caller, e, msg) macro (globalsound.qh:144-155): route the message text through Say first
        // (so a voice/radio command's text appears in chat, is flood-throttled via the chat flood field, and the
        // spectator gating downgrades a spectator say), THEN gate the sound on the resulting flood code.
        //   ownteam = (voicetype == VOICETYPE_TEAMRADIO) -> teamsay
        //   fake = IS_SPEC || IS_OBSERVER || flood < 0   (spec/observer already returned above, so fake == flood<0)
        //   flood == 0 -> the macro `break`s: NO sound at all (the message was rejected/muted-silent by Say).
        VoiceType vt = VoiceMessages.VoiceTypeOf(voiceType);
        bool ownteam = vt == VoiceType.TeamRadio;
        int flood = Chat.Say(p, ownteam ? 1 : 0, /*privatesay*/ null, msg, /*floodcontrol*/ true);
        bool fake;
        if (flood < 0) fake = true;
        else if (flood > 0) fake = false;
        else return true; // flood == 0: Say rejected the line (over flood limit) — emit no voice sound at all.

        // QC _GlobalSound: route the voice message through the full VOICETYPE dispatch so it reaches the correct
        // recipients with the right gates/attenuation — TEAMRADIO goes to the emitter's team, TAUNT/AUTOTAUNT honour
        // sv_taunt/sv_autotaunt/sv_gentle, LASTATTACKER hits the last attacker. A `fake` sound (flooded speaker) is
        // heard only by the emitter. VoiceMessages.VoiceTypeOf resolves the routing from the bare id (an unknown id
        // defaults to TAUNT, matching QC's _GetPlayerSoundSampleField fallback). The live real-client roster
        // (Clients.Players) is the recipient set QC iterates via FOREACH_CLIENT.
        if (Api.Services is not null)
            SoundSystem.GlobalSound(p, voiceType, _world.Clients.Players, fake: fake);
        return true;
    }

    /// <summary>The voice-taunt types (QC the registered voice messages / <c>allvoicesamples</c> names).</summary>
    private static readonly string[] VoiceTypes =
    {
        "death", "drown", "fall", "gasp", "jump", "pain25", "pain50", "pain75", "pain100",
        "attack", "attackinfo", "needhelp", "seenflag", "taunt", "teamshoot",
        "coverme", "defend", "freelance", "incoming", "meet", "offense", "onmyway", "roger", "yes", "no",
    };

    /// <summary>
    /// QC <c>ClientCommand_suggestmap</c> → <c>MapVote_Suggest</c> (mapvoting.qc:130-166): record a player's map
    /// suggestion for the end-of-match vote, returning the QC status string. The gate cascade ported here (in QC
    /// order): empty arg (the command's usage, handled below), the <c>g_maplist_votable_suggestions</c> disable
    /// switch (ships 2; 0 == off → "Suggestions are not accepted on this server."), the map-availability check
    /// (QC <c>GameTypeVote_MapInfo_FixName</c> → null = not available → "...is not available on this server."),
    /// the already-suggested dedup, then accept.
    ///
    /// <para>Deferred (a separate seam, per T56): QC's <c>mapvote_initialized</c> "voting already in progress"
    /// gate, the <c>Map_IsRecent</c> recent-map gate, the <c>MapInfo_CheckMap</c> gametype-support gate, and the
    /// downstream ballot fill (<c>mapvote_maps_suggestions[]</c>). The port keeps its own deduped suggestion list
    /// (<see cref="_mapSuggestions"/>) — the ballot wiring is the <see cref="MapVoting"/> task.</para>
    /// </summary>
    private bool CmdSuggestMap(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("suggestmap is a client command"); return true; }
        if (ctx.ArgCount < 2 || ctx.Arg(1) == "")
        {
            // QC ClientCommand_suggestmap: empty arg → the usage block (MapVote_Suggest's "" → usage path).
            ctx.Print("Usage:^3 cmd suggestmap <map>");
            return true;
        }
        string map = ctx.Arg(1);
        // QC mapvoting.qc:134 — the disable switch (0 == suggestions off; ships 2).
        if (Cvars.Float("g_maplist_votable_suggestions") == 0f)
        {
            ctx.Print("Suggestions are not accepted on this server.");
            return true;
        }
        // QC mapvoting.qc:138-140 — GameTypeVote_MapInfo_FixName(m) returns null when the map isn't on the
        // server. The port mirrors that with the same map-existence check the console maplist side uses
        // (Rotation.MapExists / MapInfo_CheckMap), which a host wires to the asset catalog.
        if (!_world.Rotation.MapExists(map))
        {
            ctx.Print("The map you suggested is not available on this server.");
            return true;
        }
        // QC mapvoting.qc:141-142 — the recent-map gate (unless _suggestions_override_mostrecent): a map that just
        // played can't be re-suggested so players get a rotation. Map_IsRecent reads g_maplist_mostrecent.
        if (!Cvars.Bool("g_maplist_votable_suggestions_override_mostrecent") && MapRotation.IsRecent(map))
        {
            ctx.Print("This server does not allow for recent maps to be played again. Please be patient for some rounds.");
            return true;
        }
        // QC mapvoting.qc:144-145 — gametype-support gate (skipped during a gametype vote, which the port doesn't run).
        if (!Cvars.Bool("sv_vote_gametype") && !_world.Rotation.MapSupportsGametype(map))
        {
            ctx.Print("The map you suggested does not support the current gametype.");
            return true;
        }
        // QC mapvoting.qc:147-149 — already-suggested dedup.
        if (_mapSuggestions.Any(s => string.Equals(s.Map, map, StringComparison.OrdinalIgnoreCase)))
        {
            ctx.Print("This map was already suggested.");
            return true;
        }
        // QC mapvoting.qc:160-161 — store the map AND the suggester's netname (mapvote_maps_suggesters[i]).
        _mapSuggestions.Add((map, ctx.Caller.NetName ?? ""));
        // QC mapvoting.qc:163-164 — if(autocvar_sv_eventlog) GameLogEcho(":vote:suggested:<map>:<playerid>").
        if (Cvars.Bool("sv_eventlog"))
            _world.GameLog.Echo(":vote:suggested:" + map + ":" + ctx.Caller.PlayerId);
        // QC mapvoting.qc:165 — strcat("Suggestion of ", m, " accepted.").
        ctx.Print($"Suggestion of {map} accepted.");
        return true;
    }

    /// <summary>The (map, suggester) pairs players have suggested for the next end-of-match ballot
    /// (QC mapvote_maps_suggestions / mapvote_maps_suggesters).</summary>
    public IReadOnlyList<(string Map, string Suggester)> MapSuggestions => _mapSuggestions;

    /// <summary>Clear the suggestion list (the host calls this when a new ballot is built).</summary>
    public void ClearMapSuggestions() => _mapSuggestions.Clear();

    /// <summary>QC <c>ClientCommand_autoswitch</c>: set the per-client auto-switch flag and report it.</summary>
    private bool CmdAutoswitch(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("autoswitch is a client command"); return true; }
        if (ctx.ArgCount < 2)
        {
            ctx.Print("Usage:^3 cmd autoswitch <selection>");
            return true;
        }
        bool on = InterpretBoolean(ctx.Arg(1));
        _autoswitch[ctx.Caller] = on;
        ctx.Print($"^1autoswitch is currently turned {(on ? "on" : "off")}.");
        return true;
    }

    /// <summary>
    /// QC <c>ClientCommand_physics</c>: client physics-set selection. Gated on <c>g_physics_clientselect</c>,
    /// which SHIPS 0 — so the common path is the "disabled" print. <c>list</c>/<c>help</c> prints the options;
    /// a valid set (or <c>default</c>) stuffs <c>seta cl_physics &lt;set&gt;</c> (recorded here) + a success line.
    /// </summary>
    private bool CmdPhysics(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("physics is a client command"); return true; }
        string command = ctx.Arg(1).ToLowerInvariant();

        if (!Cvars.Bool("g_physics_clientselect"))
        {
            ctx.Print("Client physics selection is currently disabled.");
            return true;
        }
        if (command == "list" || command == "help")
        {
            ctx.Print("Available physics sets: ");
            ctx.Print($"{Cvars.String("g_physics_clientselect_options")} default");
            return true;
        }
        string options = Cvars.String("g_physics_clientselect_options");
        bool valid = command == "default"
            || System.Array.IndexOf(options.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), command) >= 0;
        if (valid)
        {
            // QC stuffcmd(caller, "seta cl_physics <set>") — recorded server-side in the per-client cvar table
            // (the same slot a replicated `sentcvar cl_physics` writes), which the snapshot's per-peer preset
            // resolution reads each tick (T54).
            SetClientCvar(ctx.Caller, "cl_physics", command);
            ctx.Print($"^2Physics set successfully changed to ^3{command}");
            return true;
        }
        // QC default branch: report the current set, then fall through to the usage block (QC's default: → usage).
        ctx.Print($"Current physics set: ^3{GetClientCvar(ctx.Caller, "cl_physics", "")}");
        ctx.Print("Usage:^3 cmd physics <physics>");
        ctx.Print("  See 'cmd physics list' for available physics sets.");
        ctx.Print("  Argument 'default' resets to standard physics.");
        return true;
    }

    /// <summary>
    /// QC <c>ClientCommand_sentcvar</c> (server/command/cmd.qc:804-836) + the SVQC REPLICATE receive leg
    /// (lib/replicate.qh:79-100): a client pushing one of its replicated <c>cl_*</c> cvars to the server's
    /// per-client store. Caller-gated (never the server console) and allowlisted — the value is an
    /// attacker-controlled wire string, so it must NEVER reach the world cvar store (the T47
    /// privilege-separation class). The QC fixup funcs run on the RECEIVED value (replicate.qh:63-73):
    /// cl_weaponpriority → number + force-complete; cl_weaponpriority0..9 → fix without completing. The
    /// deprecated server→client GetCvars f==0 "request" direction (getreplies.qc:392 warns it's deprecated)
    /// is intentionally not ported — clients push via the sendcvar watcher instead.
    /// </summary>
    private bool CmdSentCvar(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("sentcvar is a client command"); return true; }
        if (ctx.ArgCount < 3)
        {
            // QC cmd.qc:827-832 — the default/usage block ("Incorrect parameters" then usage).
            ctx.Print($"Incorrect parameters for ^2{ctx.Arg(0)}^7");
            ctx.Print("Usage:^3 cmd sentcvar <cvar> <arguments>");
            return true;
        }

        string name = ctx.Arg(1);
        bool isChoice = IsRegisteredChoiceCvar(name);
        if (!SentCvarAllowlist.Contains(name) && !isChoice)
            return true; // unknown REPLICATE field → stored nowhere (QC writes no field), silently consumed

        string value = ctx.Arg(2);
        if (value.Length > 1024)
            value = value[..1024]; // clamp the wire string (defensive; QC strings are engine-bounded)

        // QC REPLICATE fixups, applied to the received value before it lands in the per-client field.
        if (name == "cl_weaponpriority")
        {
            // W_FixWeaponOrder_ForceComplete_AndBuildImpulseList (weapons/all.qc:799-818 registration): the
            // client may send NAME or NUMBER form; number it, then force-complete. Stored in NUMBER form —
            // exactly what Inventory.GetCycleWeapon consumes (the impulse-list half is the client's own concern).
            value = Common.Gameplay.WeaponOrder.FixWeaponOrderForceComplete(
                Common.Gameplay.WeaponOrder.NumberWeaponOrder(value),
                _world.Services.CvarsImpl.GetDefault("cl_weaponpriority"));
        }
        else if (name.StartsWith("cl_weaponpriority", StringComparison.Ordinal) && name.Length == "cl_weaponpriority".Length + 1)
        {
            // cl_weaponpriorityN — W_FixWeaponOrder_AllowIncomplete (fix, do NOT complete).
            value = Common.Gameplay.WeaponOrder.FixWeaponOrder(
                Common.Gameplay.WeaponOrder.NumberWeaponOrder(value), complete: false);
        }

        SetClientCvar(ctx.Caller, name, value);

        // Bridges into the pre-existing T56 per-client state, so both write paths share one source of truth.
        if (name == "cl_autoswitch")
            _autoswitch[ctx.Caller] = InterpretBoolean(value);

        // QC the SVQC REPLICATE leg for notification_CHOICE_*: store the client's selection in its per-client
        // msg_choice_choices array (CS(this).msg_choice_choices[idx]). The cvar's int value is 1=A / 2=B /
        // 0=suppress (notifications/all.qh:720). We walk the registered choices to find the index this cvar drives
        // (team variants share ONE cvar + idx — ChoiceCvarName collapses the color suffix). The host reads this
        // back via GetChoiceState to feed the per-recipient notification dispatch.
        if (isChoice)
            StoreChoiceCvar(ctx.Caller, name, value);

        // QC prints nothing on the happy path.
        return true;
    }

    /// <summary>
    /// QC <c>ClientCommand_clientversion</c> (internal — the client sends it on connect): record the reported
    /// game version. The port's handshake already enforces build parity (NetProtocol.BuildParity), so this is
    /// largely vestigial — it records the value and never crashes (the team/observe seat logic is the handshake's).
    /// </summary>
    private bool CmdClientVersion(CommandContext ctx)
    {
        if (ctx.Caller is null) { ctx.Print("clientversion is a client command"); return true; }
        if (ctx.ArgCount < 2) return true; // QC: empty arg → no-op (falls to usage, but it's internal)
        // QC: ($gameversion → 1, else stof). The literal "$gameversion" is the unexpanded macro from the client.
        float version = ctx.Arg(1) == "$gameversion" ? 1f : ctx.ArgFloat(1);
        _clientVersion[ctx.Caller] = version;
        return true;
    }

    /// <summary>The version a client reported via <c>clientversion</c> (0 if none); the handshake supersedes it.</summary>
    public float GetClientVersion(Player p) => _clientVersion.TryGetValue(p, out float v) ? v : 0f;

    // =============================================================================================
    // common reply commands (QC server/command/common.qc — print the cached reply strings)
    // =============================================================================================

    private bool CmdRecords(CommandContext ctx)
    {
        // QC precomputes the reply caches at world init; the port rebuilds lazily on read so they reflect the
        // live g_maplist/monster registry without depending on the Boot seam (a cheap superset of QC).
        Replies.Recompute();
        // QC CommonCommand_records: a page arg prints records_reply[num-1] (1..10); else all non-empty pages.
        int num = (int)ctx.ArgFloat(1);
        if (num > 0 && num <= 10 && Replies.RecordsReply[num - 1] != "")
        {
            ctx.Print(Replies.RecordsReply[num - 1]);
            return true;
        }
        bool any = false;
        for (int i = 0; i < 10; i++)
            if (Replies.RecordsReply[i] != "") { ctx.Print(Replies.RecordsReply[i]); any = true; }
        if (!any) ctx.Print("No records available"); // honest empty (no race-records store at this seam — R8)
        return true;
    }

    private bool CmdRankings(CommandContext ctx) { Replies.Recompute(); ctx.Print(Replies.RankingsReply); return true; }
    private bool CmdLsmaps(CommandContext ctx) { Replies.Recompute(); ctx.Print(Replies.LsmapsReply); return true; }
    private bool CmdPrintMapList(CommandContext ctx) { Replies.Recompute(); ctx.Print(Replies.MaplistReply); return true; }
    private bool CmdLadder(CommandContext ctx) { Replies.Recompute(); ctx.Print(Replies.LadderReply); return true; }

    // =============================================================================================
    // monster editor (QC server/command/common.qc CommonCommand_editmob)
    // =============================================================================================

    /// <summary>The spawnmob/killmob cfg aliases: rewrite the argv so editmob sees the subcommand at argv(1).</summary>
    private bool CmdEditMobAlias(CommandContext ctx, string sub)
    {
        // Build an argv that looks like "editmob <sub> <rest...>" so CmdEditMob's argv(1)/argv(2)/argv(3) line up.
        var argv = new List<string> { "editmob", sub };
        for (int i = 1; i < ctx.ArgCount; i++) argv.Add(ctx.Arg(i));
        return CmdEditMob(new CommandContext(argv.ToArray(), ctx.IsServerConsole, ctx.Caller), ctx);
    }

    private bool CmdEditMob(CommandContext ctx) => CmdEditMob(ctx, ctx);

    /// <summary>
    /// QC <c>CommonCommand_editmob</c> (server/command/common.qc): the monster editor. <paramref name="outCtx"/>
    /// is where output goes (so the spawnmob/killmob aliases print to the caller's real context). Subcommands:
    /// <c>spawn</c> (trace-spawn via <see cref="MonsterAI.SpawnMonster"/>; <c>spawn list</c> → the monster reply),
    /// <c>kill</c> (Damage the looked-at monster), <c>skin</c>/<c>movetarget</c>/<c>name</c> (edit the looked-at
    /// monster), <c>butcher</c> (server-only: remove ALL monsters + zero counts). Gated like QC (campaign off,
    /// g_monsters, g_monsters_edit, ownership, alive/not-spectating, max counts).
    /// </summary>
    private bool CmdEditMob(CommandContext ctx, CommandContext outCtx)
    {
        // QC: disabled in singleplayer; no g_monsters check here (it may be toggled mid-match).
        if (Cvars.Bool("g_campaign")) { outCtx.Print("Monster editing is disabled in singleplayer"); return true; }
        Replies.Recompute(); // so `editmob spawn list` reflects the live monster registry (QC the cached reply)

        Player? caller = ctx.Caller;
        string verb = ctx.Arg(1);
        string argument = ctx.Arg(2);

        // QC: trace from the caller's eyes to find the monster being looked at (WarpZone_TraceLine + v_forward*100).
        Entity? mon = caller is not null ? TraceLookedAtMonster(caller, 100f) : null;
        bool isVisible = mon is not null;

        switch (verb)
        {
            case "name":
            {
                if (caller is null) { outCtx.Print("Only players can edit monsters"); return true; }
                if (argument == "") break; // escape to usage
                if (!Cvars.Bool("g_monsters_edit")) { outCtx.Print("Monster editing is disabled"); return true; }
                if (!OwnsOrAdmin(mon, caller)) { outCtx.Print("This monster does not belong to you"); return true; }
                if (!isVisible) { outCtx.Print("You must look at your monster to edit it"); return true; }
                string oldName = mon!.NetName;
                mon.NetName = argument;
                outCtx.Print($"Your pet '{oldName}' is now known as '{mon.NetName}'");
                return true;
            }
            case "spawn":
            {
                if (caller is null) { outCtx.Print("Only players can spawn monsters"); return true; }
                if (argument == "") break; // escape to usage
                string argLower = argument.ToLowerInvariant();
                int moveflag = ctx.Arg(3) != "" ? (int)ctx.ArgFloat(3) : 1; // QC: follow owner if not defined

                if (argLower == "list") { outCtx.Print(Replies.MonsterlistReply); return true; }

                int monCount = CountMonstersOwnedBy(caller);

                if (!MonsterAI.MasterSwitchEnabled("g_monsters")) { outCtx.Print("Monsters are disabled"); return true; }
                if (Cvars.FloatOr("g_monsters_max", 0f) <= 0f || Cvars.FloatOr("g_monsters_max_perplayer", 0f) <= 0f)
                { outCtx.Print("Monster spawning is disabled"); return true; }
                if (caller.FragsStatus == Player.FragsSpectator || caller.IsObserver) { outCtx.Print("You must be playing to spawn a monster"); return true; }
                // QC common.qc:369 MUTATOR_CALLHOOK(AllowMobSpawning) → invasion's AllowMobSpawning hook blocks
                // spawnmob during an invasion (sv_invasion.qc: "You cannot spawn monsters during an invasion!").
                if (_world.GameType is XonoticGodot.Common.Gameplay.Invasion) { outCtx.Print("You cannot spawn monsters during an invasion!"); return true; }
                // QC common.qc:370: can't spawn while seated in a vehicle (gate goes after IS_PLAYER, before the dead check).
                if (caller.Vehicle is not null) { outCtx.Print("You can't spawn monsters while driving a vehicle"); return true; }
                if (caller.IsDead) { outCtx.Print("You can't spawn monsters while dead"); return true; }
                if (monCount >= (int)Cvars.Float("g_monsters_max")) { outCtx.Print("The maximum monster count has been reached"); return true; }
                if (monCount >= (int)Cvars.Float("g_monsters_max_perplayer")) { outCtx.Print("You can't spawn any more monsters"); return true; }

                bool found = false;
                foreach (Monster m in Monsters.All)
                    if (m.NetName == argLower) { found = true; break; }
                if (!found && argLower != "random" && argLower != "anyrandom") { outCtx.Print("Invalid monster"); return true; }

                if (Api.Services is null) { outCtx.Print("Cannot spawn monsters right now"); return true; }
                // QC: WarpZone_TraceBox(view, mins, maxs, view + v_forward*150, true, caller) → spawnmonster(...).
                Vector3 origin = TraceSpawnPoint(caller, 150f);
                Entity? spawned = MonsterAI.SpawnMonster(Api.Entities.Spawn(), argLower, null, caller, caller,
                    origin, respawn: false, removeIfInvalid: false, moveFlags: moveflag);
                outCtx.Print(spawned is not null ? $"Spawned {spawned.NetName}" : "Failed to spawn monster");
                return true;
            }
            case "kill":
            {
                if (caller is null) { outCtx.Print("Only players can kill monsters"); return true; }
                if (!OwnsOrAdmin(mon, caller)) { outCtx.Print("This monster does not belong to you"); return true; }
                if (!isVisible) { outCtx.Print("You must look at your monster to edit it"); return true; }
                // QC: Damage(mon, NULL, NULL, health + max_health + 200, DEATH_KILL, ...).
                float lethal = mon!.Health + mon.MaxHealth + 200f;
                XonoticGodot.Common.Gameplay.Damage.Combat.Damage(mon, null, null, lethal,
                    XonoticGodot.Common.Gameplay.Damage.DeathTypes.Kill, mon.Origin, System.Numerics.Vector3.Zero);
                outCtx.Print($"Your pet '{mon.NetName}' has been brutally mutilated");
                return true;
            }
            case "skin":
            {
                if (caller is null) { outCtx.Print("Only players can edit monsters"); return true; }
                if (argument == "") break;
                if (!Cvars.Bool("g_monsters_edit")) { outCtx.Print("Monster editing is disabled"); return true; }
                if (!isVisible) { outCtx.Print("You must look at your monster to edit it"); return true; }
                if (!OwnsOrAdmin(mon, caller)) { outCtx.Print("This monster does not belong to you"); return true; }
                mon!.Skin = ctx.ArgFloat(2);
                outCtx.Print($"Monster skin successfully changed to {Ftos(mon.Skin)}");
                return true;
            }
            case "movetarget":
            {
                if (caller is null) { outCtx.Print("Only players can edit monsters"); return true; }
                if (argument == "") break;
                if (!Cvars.Bool("g_monsters_edit")) { outCtx.Print("Monster editing is disabled"); return true; }
                if (!isVisible) { outCtx.Print("You must look at your monster to edit it"); return true; }
                if (!OwnsOrAdmin(mon, caller)) { outCtx.Print("This monster does not belong to you"); return true; }
                if (MonsterAI.StateOf(mon!) is { } mst) mst.MoveFlags = (int)ctx.ArgFloat(2);
                outCtx.Print($"Monster move target successfully changed to {Ftos(ctx.ArgFloat(2))}");
                return true;
            }
            case "butcher":
            {
                // QC: SERVER-ONLY (caller must be null / the console).
                if (caller is not null) { outCtx.Print("This command is not available to players"); return true; }
                // QC common.qc:428 MUTATOR_CALLHOOK(AllowMobButcher) → invasion's AllowMobButcher hook blocks
                // butcher during an invasion (sv_invasion.qc: "This command does not work during an invasion!").
                if (_world.GameType is XonoticGodot.Common.Gameplay.Invasion) { outCtx.Print("This command does not work during an invasion!"); return true; }
                int removed = RemoveAllMonsters();
                outCtx.Print(removed > 0
                    ? $"Killed {removed} monster{(removed == 1 ? "" : "s")}"
                    : "No monsters to kill");
                return true;
            }
        }

        // QC usage block.
        outCtx.Print("Usage:^3 editmob <command> [<arguments>]");
        outCtx.Print("  Where <command> can be butcher spawn skin movetarget kill name");
        outCtx.Print("  spawn, skin, movetarget and name require <arguments>");
        outCtx.Print("  spawn also takes arguments list and random");
        outCtx.Print("  Monster will follow owner if third argument of spawn command is not defined");
        return true;
    }

    /// <summary>QC <c>mon.realowner == caller || autocvar_g_monsters_edit &gt;= 2</c>: the editmob ownership gate.</summary>
    private static bool OwnsOrAdmin(Entity? mon, Player caller)
        => mon is not null && (ReferenceEquals(mon.Owner, caller) || Cvars.Float("g_monsters_edit") >= 2f);

    /// <summary>Count the live monsters owned by <paramref name="caller"/> (QC IL_EACH(g_monsters, realowner == caller)).</summary>
    private static int CountMonstersOwnedBy(Player caller)
    {
        if (Api.Services is null) return 0;
        int n = 0;
        foreach (Entity e in Api.Entities.FindByClass("monster"))
            if (MonsterAI.StateOf(e) is not null && ReferenceEquals(e.Owner, caller))
                n++;
        return n;
    }

    /// <summary>QC editmob butcher: Monster_Remove every monster + zero the global counts. Returns the count removed.</summary>
    private int RemoveAllMonsters()
    {
        if (Api.Services is null) return 0;
        var victims = new List<Entity>();
        foreach (Entity e in Api.Entities.FindByClass("monster"))
            if (MonsterAI.StateOf(e) is not null)
                victims.Add(e);
        foreach (Entity e in victims)
        {
            MonsterAI.Forget(e);       // drop the MonsterState (QC the edict deletion bookkeeping)
            Api.Entities.Remove(e);
        }
        // QC: monsters_total = monsters_killed = totalspawned = 0 — reset the spawn budget counters.
        MonsterAI.ResetCounters();
        return victims.Count;
    }

    /// <summary>
    /// QC editmob's look-at: trace from the caller's eyes forward and return the monster hit, or null. The port
    /// uses <c>Api.Trace</c> with a forward vector from the player's view angles (the eyes = origin + view_ofs).
    /// </summary>
    private static Entity? TraceLookedAtMonster(Player caller, float dist)
    {
        if (Api.Services is null) return null;
        Vector3 eyes = caller.Origin + caller.ViewOfs;
        // QC makevectors(caller.v_angle): aim along the VIEW angles (fall back to body angles when unset).
        Vector3 aim = caller.ViewAngles != Vector3.Zero ? caller.ViewAngles : caller.Angles;
        XonoticGodot.Common.Math.QMath.AngleVectors(aim, out Vector3 forward, out _, out _);
        Vector3 end = eyes + forward * dist;
        XonoticGodot.Common.Services.TraceResult tr = Api.Trace.Trace(
            eyes, System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero, end,
            XonoticGodot.Common.Framework.MoveFilter.Normal, caller);
        if (tr.Ent is { } hit && MonsterAI.StateOf(hit) is not null)
            return hit;
        return null;
    }

    /// <summary>QC editmob spawn's trace box: the spawn endpos in front of the caller (origin + v_forward*dist).</summary>
    private static Vector3 TraceSpawnPoint(Player caller, float dist)
    {
        Vector3 eyes = caller.Origin + caller.ViewOfs;
        Vector3 aim = caller.ViewAngles != Vector3.Zero ? caller.ViewAngles : caller.Angles;
        XonoticGodot.Common.Math.QMath.AngleVectors(aim, out Vector3 forward, out _, out _);
        Vector3 end = eyes + forward * dist;
        if (Api.Services is null) return end;
        XonoticGodot.Common.Services.TraceResult tr = Api.Trace.Trace(
            eyes, caller.Mins, caller.Maxs, end, XonoticGodot.Common.Framework.MoveFilter.Normal, caller);
        return tr.EndPos;
    }

    /// <summary>QC <c>ftos</c>: integer floats print with no decimal point.</summary>
    private static string Ftos(float v)
        => v == System.MathF.Floor(v)
            ? ((long)v).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>QC <c>InterpretBoolean</c> (lib/bool.qh): yes/true/on → true; no/false/off → false; else stof != 0.</summary>
    private static bool InterpretBoolean(string input) => input.ToLowerInvariant() switch
    {
        "yes" or "true" or "on" => true,
        "no" or "false" or "off" => false,
        _ => float.TryParse(input, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float f) && f != 0f,
    };

    private static int TeamFromName(string name) => name.ToLowerInvariant() switch
    {
        "red" or "1" => Teams.Red,
        "blue" or "2" => Teams.Blue,
        "yellow" or "3" => Teams.Yellow,
        "pink" or "4" => Teams.Pink,
        _ => Teams.None,
    };

    private Player? FindPlayerByName(string name)
    {
        // exact match first, then case-insensitive prefix (QC GetFilteredNumber-ish leniency).
        foreach (Player p in _world.Clients.Players)
            if (string.Equals(p.NetName, name, StringComparison.OrdinalIgnoreCase))
                return p;
        foreach (Player p in _world.Clients.Players)
            if (p.NetName.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                return p;
        return null;
    }
}
