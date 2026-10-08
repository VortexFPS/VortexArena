// Port of Base/darkplaces/sv_ccmds.c SV_Map_f / SV_Changelevel_f / SV_Restart_f as a CLIENT process reaches
// them (a listen server's console): "map" ends whatever is running and starts a server on the level,
// "changelevel" and "restart" act on the running one, and each says what DarkPlaces says when it cannot.
using System;
using System.Collections.Generic;
using System.Net;
using VortexArena.Common.Services;
using VortexArena.Engine.Simulation;
using VortexArena.Formats.Vfs;
using VortexArena.Legacy.Local;

namespace VortexArena.Game.Legacy;

/// <summary>
/// Starting a local Xonotic game (the server program in this process, <see cref="LegacyLocalServer"/>) from
/// a console line or from the command line: which request a line becomes, with which of the player's
/// settings. The shell owns the session; this only decides what to ask it for.
/// </summary>
public static class LegacyLocalGames
{
    /// <summary>
    /// The LAN switch: "0" (the default) opens no socket at all - the local player is connected through two
    /// queues inside the process; "1" also listens on UDP at 127.0.0.1 (this machine only); an IPv4 address of
    /// this machine, optionally with :port, listens on that address and no other. Nothing is ever announced to
    /// a master server. Not saved: a game is opened to the network for one run, on purpose.
    /// </summary>
    public const string ListenCvar = "legacy_listen";
    /// <summary>1 (the default) runs a local game's server on a thread of its own; 0 on the main thread, as DarkPlaces does by default.</summary>
    public const string ThreadCvar = "legacy_server_thread";

    /// <summary>1 (the default) starts Xonotic's menu program with a local game started from the command line or
    /// the native console, as DarkPlaces has it running; 0 runs the game without it.</summary>
    public const string MenuCvar = "legacy_local_menu";

    public static void RegisterCvars(CvarService cvars)
    {
        cvars.Register(MenuCvar, "1", CvarFlags.None,
            "local Xonotic games started by --legacy-map or legacy_map: 1 also runs Xonotic's own menu program (its Join/Spectate dialog, team selection and in-game menu), 0 uses the native pause menu");
        cvars.Register(ListenCvar, "0", CvarFlags.None,
            "local Xonotic games: 0 no network socket (default); 1 also accept other players over UDP on 127.0.0.1 only; an IPv4 address of this machine[:port] accepts them on that address only (LAN). Never listed on a master server");
        cvars.Register(ThreadCvar, "1", CvarFlags.None,
            "local Xonotic games: 1 runs the server program on its own thread (default), 0 on the main thread (a level load then freezes the window)");
    }

    /// <summary>
    /// The request for a local game on <paramref name="map"/>. Started from Xonotic's menu, the server gets
    /// that menu's cvars - the game type, bots, limits and mutators its Create dialog has just set - and the
    /// player-slot count of its last "maxplayers". Without the menu, an explicit game type and bot count, or
    /// those of the local game that was running (<paramref name="running"/>). Null (with the reason) if the
    /// LAN setting cannot be read. Call it with no session on the menu's console, so that its cvars are the player's.
    /// </summary>
    public static LegacyLocalGameRequest? BuildRequest(string map, string? gameType, int? bots, LegacyMenu? menu, LegacyLocalGameRequest? running, CvarService playerCvars, out string problem)
    {
        int port = menu?.Console is { } console && console.Cvars.Has("port") && console.Cvars.GetFloat("port") is > 0 and <= 65535 ? (int)console.Cvars.GetFloat("port") : LegacyGame.DefaultPort;
        if (!LegacyLocalCommands.TryParseListen(playerCvars.GetString(ListenCvar), port, out IPEndPoint? listen, out problem)) return null;
        bool threaded = !playerCvars.Has(ThreadCvar) || playerCvars.GetFloat(ThreadCvar) != 0;
        if (menu?.Console is { } xonotic)
            return new LegacyLocalGameRequest
            {
                Map = map, GameType = gameType, Bots = bots, MaxPlayers = xonotic.MaxPlayers, Cvars = LegacyLocalCvars.FromPlayer(xonotic.Cvars), Listen = listen, Threaded = threaded,
            };
        return new LegacyLocalGameRequest
        {
            Map = map, GameType = gameType ?? running?.GameType, Bots = bots ?? running?.Bots, MaxPlayers = running?.MaxPlayers ?? 16,
            Cvars = running?.Cvars ?? Array.Empty<KeyValuePair<string, string>>(), Listen = listen, Threaded = threaded,
        };
    }

    /// <summary>
    /// Acts on "map x" / "devmap x" / "changelevel x" / "restart" / "maps" / "load x" as a listen server's
    /// console does. True if the line was one of them (whatever came of it); false if it is not this
    /// class's business.
    /// </summary>
    /// <param name="start">Ends whatever session is running and starts a local game on the map it is given.</param>
    public static bool HandleMapCommand(string line, LegacyMenu? menu, LegacyGame? current, CvarService playerCvars, Action<string> start, Action<string> print)
    {
        LegacyLocalGame? local = current is { IsLocal: true, Ended: false } ? new LegacyLocalGame(current) : null;
        VirtualFileSystem? files = menu?.Files ?? current?.Files;
        switch (LegacyLocalCommands.ParseMapCommand(line, out string map, out string usage))
        {
            case LegacyMapCommand.None:
                return false;
            case LegacyMapCommand.Usage:
                print(usage);
                return true;
            case LegacyMapCommand.Unsupported:
                print("Saved games cannot be loaded by this client.");
                return true;
            case LegacyMapCommand.ListMaps:
                if (files is null) print("maps: no Xonotic game data is mounted yet");
                else
                {
                    List<string> names = new();
                    foreach (string path in files.Find("maps/", "bsp"))
                    {
                        string name = System.IO.Path.GetFileNameWithoutExtension(path);
                        if (path.Length == "maps/".Length + name.Length + ".bsp".Length && LegacyLocalCommands.IsMapName(name)) names.Add(name);
                    }
                    names.Sort(StringComparer.OrdinalIgnoreCase);
                    print($"{names.Count} maps: {string.Join(' ', names)}");
                }
                return true;
            case LegacyMapCommand.Restart:
                if (local is null) return true;   // SV_Restart_f without a server does nothing
                local.Game.ServerCommand("restart");
                return true;
            case LegacyMapCommand.ChangeLevel:
                if (local is null) print(LegacyLocalCommands.NoServerForChangeLevel);
                else local.Game.ServerCommand("changelevel " + map);
                return true;
            case LegacyMapCommand.StartMap:
                // SV_SpawnServer's own check, made before anything is torn down: DarkPlaces shuts its server
                // down first and is then left with none; keeping the running game is the kinder order.
                if (files is not null && !files.Exists("maps/" + map + ".bsp"))
                {
                    print($"SpawnServer: no map file named maps/{map}.bsp");
                    return true;
                }
                // The LAN setting is read again when the request is built; saying so now keeps the running game.
                if (!LegacyLocalCommands.TryParseListen(playerCvars.GetString(ListenCvar), LegacyGame.DefaultPort, out _, out string problem))
                {
                    print(problem);
                    return true;
                }
                start(map);
                return true;
            default:
                return false;
        }
    }

    private sealed record LegacyLocalGame(LegacyGame Game);
}
