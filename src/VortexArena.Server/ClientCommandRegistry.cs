// Port of server/command/reg.qh (CLIENT_COMMANDS vs SERVER_COMMANDS registries) and the
// server/command/cmd.qc SV_ParseClientCommand dispatch split + floodcheck-exempt switch.
//
// QC has TWO command registries: SERVER_COMMANDS (sv_cmd.qc / banning.qc — server-console/rcon only) and
// CLIENT_COMMANDS (cmd.qc — client-callable via `cmd <name>`). SV_ParseClientCommand, the engine entry for a
// client's `clc_stringcmd`, dispatches a client command ONLY through CheatCommand, CommonCommand_macro_command
// (server/command/common.qc — also client-callable) and ClientCommand_macro_command (CLIENT_COMMANDS). It does
// NOT route the generic family (set/seta/cvar/toggle/rpn/maplist/settemp/… in common/command/generic.qc) nor the
// SERVER_COMMANDS (restart/endmatch/kick/map/gotomap/ban/…), so those are server-only and must be rejected when
// a client issues them.
//
// Our port collapses both QC registries into ONE Commands table keyed by name; this registry is the membership
// test that restores the QC privilege separation: a name here is a CLIENT_COMMAND / common-command / cheat and
// may be invoked with a non-null caller; anything else is SERVER-ONLY (caller must be null). See the T47 gate in
// Commands.Execute and the 3-gate pre-filter in ServerNet.HandleClientCommand.

using System;
using System.Collections.Generic;

namespace XonoticGodot.Server;

/// <summary>
/// The C# successor to QC's split between <c>CLIENT_COMMANDS</c> (server/command/cmd.qc — a client may
/// invoke these via <c>cmd &lt;name&gt;</c>) and <c>SERVER_COMMANDS</c> (server/command/sv_cmd.qc +
/// banning.qc — rcon / server-console only). The port registers every command in one
/// <see cref="Commands"/> table, so this static allowlist is what re-establishes the privilege boundary:
/// <see cref="IsClientCallable"/> is true exactly for the QC <c>CLIENT_COMMAND</c> entries, the
/// client-reachable <c>CommonCommand_*</c> verbs (server/command/common.qc, dispatched for clients by
/// <c>CommonCommand_macro_command</c> in <c>SV_ParseClientCommand</c>), and the <c>CheatCommand</c> verbs
/// (gated internally by <c>sv_cheats</c>). Everything else — the admin <c>SERVER_COMMAND</c>s and the
/// cvar-reflection / generic family — is server-only and is rejected when a client issues it.
/// </summary>
public static class ClientCommandRegistry
{
    /// <summary>
    /// QC <c>CLIENT_COMMANDS</c> (reg.qh) — the ~22 verbs a client may invoke with <c>cmd &lt;name&gt;</c>,
    /// registered in server/command/cmd.qc. Only the ones the port actually registers in <see cref="Commands"/>
    /// are listed; Base-only verbs with no port handler yet (selfstuff/clear_bestcptimes/mv_getpicture/wpeditor)
    /// are omitted (they'd never resolve anyway). Names match the table keys (lowercase, OrdinalIgnoreCase).
    /// </summary>
    private static readonly HashSet<string> ClientCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        // ---- QC CLIENT_COMMAND(...) entries (cmd.qc:1110-1131) ----
        "autoswitch",   // CLIENT_COMMAND(autoswitch)
        "clear_ignores",// CLIENT_COMMAND(clear_ignores)
        "clientversion",// CLIENT_COMMAND(clientversion) — internal, sent by the client on connect
        "ignore",       // CLIENT_COMMAND(ignore)
        "join",         // CLIENT_COMMAND(join)
        "kill",         // CLIENT_COMMAND(kill)
        "minigame",     // CLIENT_COMMAND(minigame)
        "physics",      // CLIENT_COMMAND(physics)
        "ready",        // CLIENT_COMMAND(ready)
        "say",          // CLIENT_COMMAND(say)
        "say_team",     // CLIENT_COMMAND(say_team)
        "selectteam",   // CLIENT_COMMAND(selectteam)
        "color",        // engine `color` command (host_cmd.c) → SV_ChangeTeam; FFA-only recolor (teamplay.qc:1340)
        "sentcvar",     // CLIENT_COMMAND(sentcvar) — internal, a client pushing a replicated cl_* cvar
        "spectate",     // CLIENT_COMMAND(spectate)
        "suggestmap",   // CLIENT_COMMAND(suggestmap)
        "tell",         // CLIENT_COMMAND(tell)
        "voice",        // CLIENT_COMMAND(voice)
        "unignore",     // CLIENT_COMMAND(unignore)

        // ---- port-local CLIENT aliases / additions ----
        // `ready_up` is the port's cfg alias of `ready` (xonotic-client.cfg ready_up → cmd ready).
        "ready_up",
        // Impulses + the weapon_* aliases: in DP these ride the engine usercmd (not clc_stringcmd), but the port
        // routes them through Commands.Execute with caller=p on the input-frame path (ServerNet.HandleInputFrame
        // → `impulse N`), so they are legitimately client-invoked and must be allowed.
        "impulse",
        "weapon_next", "weapon_prev", "weaplast", "weapon_last", "weapon_best", "weapon_drop", "reload",
        // Player-deployed waypoints (QC server/impulse.qc IMPULSE(waypoint_*) — engine impulses in DP). The port's
        // QuickMenu/userbinds emit these named verbs through the client console, so they arrive as client commands
        // and must be allowed (each drops/clears the caller's own waypoint sprite — a per-player action).
        "waypoint_personal_here", "waypoint_personal_crosshair", "waypoint_personal_death",
        "waypoint_here_follow", "waypoint_here_here", "waypoint_here_crosshair", "waypoint_here_death",
        "waypoint_danger_here", "waypoint_danger_crosshair", "waypoint_danger_death",
        "waypoint_clear_personal", "waypoint_clear",

        // ---- QC CommonCommand_* (server/command/common.qc) — client-reachable via CommonCommand_macro_command ----
        "cvar_changes",   // CommonCommand_cvar_changes
        "cvar_purechanges",// CommonCommand_cvar_purechanges
        "editmob",        // CommonCommand_editmob (gated to admins/console inside the handler in Base)
        "info",           // CommonCommand_info
        "ladder",         // CommonCommand_ladder
        "lsmaps",         // CommonCommand_lsmaps
        "printmaplist",   // CommonCommand_printmaplist
        "rankings",       // CommonCommand_rankings
        "records",        // CommonCommand_records
        "teamstatus",     // CommonCommand_teamstatus
        "time",           // CommonCommand_time
        "timein",         // CommonCommand_timein
        "timeout",        // CommonCommand_timeout
        "vote",           // COMMON_COMMAND(vote) → VoteCommand — the core client voting verb (call/yes/no/…)
        "who",            // CommonCommand_who
        // port cfg aliases of `editmob` (spawnmob/killmob) — same client-reachability as editmob.
        "spawnmob", "killmob",

        // ---- QC CheatCommand verbs (server/cheats.qc) — client-callable, gated internally by sv_cheats ----
        "god", "notarget", "noclip", "fly", "give", "usetarget", "killtarget", "teleporttotarget",
        "pointparticles", "trailparticles", "make", "penalty",

        // ---- Onslaught (QC the SV_ParseClientCommand mutator hook in onslaught/sv_onslaught.qc). The click-radar
        //      pick `cmd ons_spawn [x y z]` is a client command (a player drives it from the radar HUD), gated
        //      server-side to the active Onslaught gametype + IS_PLAYER inside the handler. ----
        "ons_spawn",

        // ---- sandbox (QC the SV_ParseClientCommand mutator hook in sandbox/sv_sandbox.qc). The client `sandbox`
        //      alias (commands.cfg:193) emits `cmd g_sandbox <sub> …`, so the wire verb is `g_sandbox`. It is a
        //      client command (a player drives the build mode), gated server-side by g_sandbox/g_sandbox_readonly
        //      inside the handler (Commands.CmdSandbox → SandboxMutator.HandleCommand). ----
        "g_sandbox",

        // ---- superspec (QC the SV_ParseClientCommand mutator hook in superspec/sv_superspec.qc). The spectator
        //      option/follow verbs are client commands (a spectator drives them), gated server-side by
        //      g_superspectate (the mutator's added-state) + the IS_PLAYER guard inside HandleCommand. ----
        "superspec", "autospec", "superspec_itemfilter", "followpowerup", "followstrength", "followshield",

        // ---- `help` — QC SV_ParseClientCommand handles `cmd help` inline (cmd.qc:1254-1276), printing the client
        //      + common command lists. Reachable by a client in Base, so allow it. (The port's help lists more
        //      names than Base's client-help, but the verbs themselves stay gated, so it's at most a name hint.)
        "help",
    };

    /// <summary>
    /// QC <c>SV_ParseClientCommand</c>'s floodcheck-exempt switch (cmd.qc:1193-1216): commands NOT subject to the
    /// per-client command flood bucket. <c>begin/download/pause/prespawn/spawn</c> are engine-handled,
    /// <c>mv_getpicture/wpeditor/sentcvar</c> are server-handled internal commands, <c>say/say_team/tell</c> have
    /// their own chat flood control, and <c>minigame</c> floods only for the common subcommands (handled
    /// separately in the gate). Tested against <c>strtolower(argv(0))</c>.
    /// </summary>
    private static readonly HashSet<string> FloodExempt = new(StringComparer.OrdinalIgnoreCase)
    {
        "begin",        // handled by engine in host_cmd.c
        "download",     // handled by engine in cl_parse.c
        "mv_getpicture",// handled by server in cmd.qc
        "wpeditor",     // handled by server in cmd.qc
        "pause",        // handled by engine in host_cmd.c
        "prespawn",     // handled by engine in host_cmd.c
        "sentcvar",     // handled by server in cmd.qc
        "spawn",        // handled by engine in host_cmd.c
        "say", "say_team", "tell", // chat has its own flood control in chat.qc
    };

    /// <summary>
    /// True when <paramref name="commandName"/> is a QC CLIENT_COMMAND / client-reachable common command / cheat
    /// — i.e. a client may invoke it with a non-null caller. False for the admin SERVER_COMMANDS and the generic
    /// cvar-reflection family (those require a null caller / rcon). Case-insensitive (QC <c>strtolower(argv(0))</c>).
    /// </summary>
    public static bool IsClientCallable(string commandName)
        => !string.IsNullOrEmpty(commandName) && ClientCommands.Contains(commandName);

    /// <summary>
    /// QC <c>SV_ParseClientCommand</c>'s floodcheck-exempt test: true for the engine/server/chat-handled commands
    /// that bypass the per-client command flood bucket. The <c>minigame</c> partial exemption (flood only for the
    /// common subcommands) is handled by <see cref="IsCommandFloodExempt"/>.
    /// </summary>
    public static bool IsFloodExempt(string commandName)
        => !string.IsNullOrEmpty(commandName) && FloodExempt.Contains(commandName);

    /// <summary>QC <c>MINIGAME_COMMON_CMD[]</c> (common/minigames/sv_minigames.qh:14): the minigame subcommands
    /// that ARE flood-controlled (everything else under <c>minigame</c> is a gameplay move and exempt).</summary>
    private static readonly string[] MinigameCommonCmd =
        { "create", "join", "list", "list-sessions", "end", "part", "invite" };

    /// <summary>
    /// QC <c>SV_ParseClientCommand</c>'s full floodcheck-exempt switch (cmd.qc:1193-1216): the flat
    /// <see cref="IsFloodExempt"/> set PLUS the <c>minigame</c> partial case — minigame floods ONLY for the
    /// common subcommands (and an empty arg), and is exempt for any individual-minigame move command (gameplay
    /// moves shouldn't be limited). <paramref name="verb"/> is <c>strtolower(argv(0))</c>, <paramref name="arg1"/>
    /// is <c>argv(1)</c>.
    /// </summary>
    public static bool IsCommandFloodExempt(string verb, string arg1)
    {
        if (IsFloodExempt(verb))
            return true;
        if (string.Equals(verb, "minigame", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(arg1))
                return false; // QC: empty arg → goto flood_control (NOT exempt)
            foreach (string c in MinigameCommonCmd)
                if (c == arg1)
                    return false; // a common subcommand → flood-controlled
            return true; // an individual-minigame move command → exempt
        }
        return false;
    }

    /// <summary>Throw-on-invalid UTF-8 codec for the GATE 1 round-trip (vs the lenient default that substitutes U+FFFD).</summary>
    private static readonly System.Text.UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// GATE 1 — QC <c>SV_ParseClientCommand</c>'s UTF-8 round-trip check (cmd.qc:1172-1178). QC drops a command
    /// whose <c>chr2str(str2chr(...))</c> re-encoding differs from the original — i.e. it carried invalid UTF-8.
    /// The faithful port equivalent: the decoded string must contain no U+FFFD replacement char (which the wire
    /// decoder substitutes for malformed bytes) AND must survive a strict encode→decode round-trip (which rejects
    /// lone surrogates that can't be encoded as UTF-8). Well-formed ASCII / UTF-8 passes unchanged.
    /// </summary>
    public static bool IsValidUtf8Command(string command)
    {
        if (string.IsNullOrEmpty(command))
            return true;
        // A U+FFFD (the replacement char) means the wire decoder already hit malformed bytes (QC: didn't round-trip).
        if (command.IndexOf('�') >= 0)
            return false;
        try
        {
            byte[] bytes = StrictUtf8.GetBytes(command);
            return StrictUtf8.GetString(bytes) == command;
        }
        catch (System.Text.EncoderFallbackException) { return false; }
        catch (System.Text.DecoderFallbackException) { return false; }
    }

    /// <summary>
    /// GATE 3 — the per-client command flood-bucket update (server/command/cmd.qc:1232-1250). Pure mirror of the
    /// QC budget: with <c>mod_time = frameStart + count*time</c>, the command is REJECTED (returns false, leaving
    /// <paramref name="floodTime"/> unchanged) when <c>mod_time &lt; floodTime</c>; otherwise it is ACCEPTED and
    /// <paramref name="floodTime"/> advances to <c>max(frameStart, floodTime) + time</c> (the QC
    /// micro-optimization where <c>mod_time - count*time == frameStart</c>). The <c>count*time</c> headroom lets a
    /// fresh client burst <c>count</c> commands before the cursor catches up; <c>time &lt; 0</c> disables the
    /// limiter (commands.cfg: "-1" = no limit). Stateless w.r.t. the cvar reads so a host/test supplies them.
    /// </summary>
    /// <param name="floodTime">The client's flood cursor (QC <c>CS(this).cmd_floodtime</c>); advanced on accept.</param>
    /// <param name="frameStart">The sim frame-start time (QC <c>gettime(GETTIME_FRAMESTART)</c>).</param>
    /// <param name="antispamCount">QC <c>autocvar_sv_clientcommand_antispam_count</c> (default 8).</param>
    /// <param name="antispamTime">QC <c>autocvar_sv_clientcommand_antispam_time</c> (default 1.0; &lt;0 = no limit).</param>
    /// <returns>True to allow the command; false to reject it as flood.</returns>
    public static bool TryPassFlood(ref float floodTime, float frameStart, float antispamCount, float antispamTime)
    {
        if (antispamTime < 0f)
            return true; // QC: "-1" = no limit
        float modTime = frameStart + antispamCount * antispamTime;
        // QC cmd.qc:1241 — `if (mod_time < store.cmd_floodtime)`: reject ONLY when the ceiling is STRICTLY behind
        // the cursor. When they are equal (modTime == floodTime) the condition is FALSE and the command is
        // ACCEPTED — so a fresh client may burst EXACTLY `count` commands in one frame (the count-th, whose
        // ceiling sits AT the cursor, still passes). Using <= here would reject that count-th command, allowing
        // only count-1 per burst and diverging from Base. The comparison must be strictly less-than.
        if (modTime < floodTime)
            return false; // too much spam, halt (cursor untouched)
        floodTime = Math.Max(frameStart, floodTime) + antispamTime;
        return true;
    }
}
