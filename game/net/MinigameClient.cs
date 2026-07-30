using System;
using System.Collections.Generic;
using VortexArena.Common.Gameplay;
using VortexArena.Game.Hud;

namespace VortexArena.Game.Net;

/// <summary>
/// The client-side minigame coordinator — the C# successor to CSQC's <c>cl_minigames.qc</c>
/// (Base/.../qcsrc/common/minigames/cl_minigames.qc): it tracks the locally-active session + the local
/// player's team, opens/closes the board overlay + in-game menu on activation, and forwards every board click
/// / menu action back to the server as a <c>cmd minigame …</c> line.
///
/// QC networks each minigame entity individually and activates the local <c>player_pointer</c> when its slot
/// arrives; the port instead receives a whole-session envelope (<see cref="MinigameNetState.Envelope"/>) on the
/// reliable <see cref="NetControl.MinigameState"/> channel (raised as <see cref="ClientNet.MinigameStateReceived"/>).
/// A non-null envelope session is the active game (QC <c>activate_minigame</c>); an empty one means the player
/// left (QC <c>deactivate_minigame</c>). Moves travel the other way as <c>cmd minigame move &lt;tile&gt;</c>
/// (grid games) or <c>cmd minigame &lt;verb&gt;</c> (Pong throw / add-AI / TTT next / …) — the analogue of QC
/// <c>minigame_cmd</c> (a <c>localcmd("cmd minigame "+…)</c>).
///
/// All the Godot UI (<see cref="MinigameRenderer"/> + <see cref="MinigameMenu"/>) lives in <c>game/hud</c>; this
/// type is the net→UI glue, so it stays here in <c>game/net</c> alongside <see cref="ClientNet"/>.
/// </summary>
public sealed class MinigameClient
{
    private readonly VortexArena.Game.Hud.Hud _hud;
    private readonly MinigameMenu _menu;

    /// <summary>Sends a <c>cmd minigame …</c> line to the server (wired by <see cref="NetGame"/> to
    /// <see cref="ClientNet.SendStringCommand"/>). QC <c>minigame_cmd</c> (a localcmd to the server).</summary>
    private readonly Action<string> _sendCommand;

    /// <summary>The locally-active session (QC <c>active_minigame</c>), or null when not in a minigame.</summary>
    public MinigameSession? Active { get; private set; }

    /// <summary>The local player's team in the active session (QC <c>minigame_self.team</c>); 0 = none.</summary>
    public int LocalTeam { get; private set; }

    /// <summary>The session netnames the client has heard of, for the Join menu (QC the list-sessions reply).
    /// Populated from <c>cmd minigame list-sessions</c> ServerPrint lines (see <see cref="NoteSessionListLine"/>)
    /// and from each received snapshot's own netname.</summary>
    public IReadOnlyCollection<string> KnownSessions => _knownSessions;
    private readonly HashSet<string> _knownSessions = new(StringComparer.Ordinal);

    public MinigameClient(VortexArena.Game.Hud.Hud hud, MinigameMenu menu, Action<string> sendCommand)
    {
        _hud = hud;
        _menu = menu;
        _sendCommand = sendCommand;

        // Board clicks → a move command (grid games). QC each *_hud_board turns a click into minigame_cmd("…").
        _hud.Minigame.OnMove += OnBoardMove;
        // The in-game menu emits raw "cmd minigame …" lines (create/join/the per-game actions); pass them on.
        _menu.SendCommand += SendCommand;
        // The menu asks for the latest joinable-session list when the Join submenu is opened.
        _menu.RequestSessionList += () => SendCommand("list-sessions");
        _menu.LiveSessions = () => _knownSessions;
    }

    /// <summary>Apply a received session envelope (QC activate/deactivate_minigame): a non-null session opens the
    /// board overlay + records the local team; an empty envelope hides the board (the player left/ended).</summary>
    public void OnEnvelope(MinigameNetState.Envelope env)
    {
        if (env.Session is null)
        {
            // QC deactivate_minigame: the local player left or the session ended.
            Active = null;
            LocalTeam = 0;
            _hud.Minigame.Show(null);
            _menu.SetActive(null);
            return;
        }

        // QC activate_minigame: track the session + local team, show the board (it gates moves on LocalTeam).
        Active = env.Session;
        LocalTeam = env.LocalTeam;
        _knownSessions.Add(env.NetName);
        _hud.Minigame.LocalTeam = env.LocalTeam;
        _hud.Minigame.Show(env.Session);
        _menu.SetActive(env.Session);
    }

    /// <summary>Toggle the in-game minigame menu (QC the <c>+minigamemenu</c> / "menu" bind). Mirror the QC
    /// HUD_MinigameMenu_Open/Close — but only while a minigame is active or the player wants to start one.</summary>
    public void ToggleMenu() => _menu.Toggle();

    /// <summary>Feed a <c>cmd minigame list-sessions</c> ServerPrint reply line: each line is a session netname,
    /// recorded for the Join menu (QC the client's session list). Ignores the usage/empty lines.</summary>
    public void NoteSessionListLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        // A netname is "<gameid>_<n>"; the create/join replies and usage lines contain spaces, so only single
        // tokens with an underscore are taken as session ids (a conservative filter on the shared print channel).
        string s = line.Trim();
        if (s.IndexOf(' ') < 0 && s.IndexOf('_') > 0)
            _knownSessions.Add(s);
    }

    private void OnBoardMove(string tile) => SendCommand($"move {tile}");

    /// <summary>Send a <c>cmd minigame &lt;args&gt;</c> line (QC minigame_cmd). The server's <c>minigame</c>
    /// command parses the verb/tail; a bare tile/column forwards to the grid game's move.</summary>
    private void SendCommand(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
            return;
        _sendCommand($"minigame {args}");
    }

    /// <summary>Unsubscribe (host teardown).</summary>
    public void Dispose()
    {
        _hud.Minigame.OnMove -= OnBoardMove;
        _menu.SendCommand -= SendCommand;
    }
}
