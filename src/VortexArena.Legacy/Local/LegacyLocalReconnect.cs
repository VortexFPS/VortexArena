// Port of Base/darkplaces/sv_ccmds.c SV_Map_f's last step for a listen server: "if (sv.active &&
// host.hook.ConnectLocal != NULL) host.hook.ConnectLocal();" (host_cmd.c Host_Map_f before that:
// "if (sv.active && cls.state == ca_disconnected) Cmd_ExecuteString("connect local")").
namespace VortexArena.Legacy.Local;

/// <summary>
/// What the local player's client does when the server of its own game starts a NEW game with "map".
///
/// "map" is not "changelevel": SV_Map_f shuts the running game down, which tells every client
/// svc_disconnect, and starts the level as a fresh server. In DarkPlaces the local client is then sent
/// straight back in ("connect local"), so the player sees one level end and the next begin. Xonotic's
/// campaign moves to its next level exactly this way (common/campaign_setup.qc CampaignSetup:
/// "disconnect", then MapInfo_LoadMap with "map"), and so does losing a campaign level (the same level
/// again). A client that took that svc_disconnect for the end of the game dropped the player to the
/// menu after every campaign level.
///
/// This class is the decision alone, so that it can be tested without a window: the owner tells it
/// what the server announced and what the client found, and asks each frame whether to connect.
/// Every other svc_disconnect of a local game (a kick, the server's own end) is still the end.
/// </summary>
public sealed class LegacyLocalReconnect
{
    private bool _restarting, _levelUp;

    /// <summary>True from the client's svc_disconnect until <see cref="TakeConnect"/> says to connect:
    /// the session is alive, has no connection and is waiting for the new game's level.</summary>
    public bool Waiting { get; private set; }

    /// <summary>How many times the client was sent back in.</summary>
    public int Reconnects { get; private set; }

    /// <summary><see cref="LegacyLocalServer.GameRestarting"/>: the server is dropping everyone for a new game.</summary>
    public void GameRestarting()
    {
        _restarting = true;
        _levelUp = false;
    }

    /// <summary><see cref="LegacyLocalServer.LevelChanged"/>: a level is running again and can be connected to.</summary>
    public void LevelChanged()
    {
        if (_restarting) _levelUp = true;
    }

    /// <summary>
    /// The client's connection was ended by the server. True if that was the new game's doing and the
    /// session must be kept for <see cref="TakeConnect"/>; false if it is the end of the game.
    /// </summary>
    public bool ClientDisconnected()
    {
        if (!_restarting) return false;
        Waiting = true;
        return true;
    }

    /// <summary>
    /// Asked every frame: true exactly once, when the client has been dropped and the new game's level is
    /// up - the moment for "connect local". The two can arrive in either order.
    /// </summary>
    public bool TakeConnect()
    {
        if (!Waiting || !_levelUp) return false;
        Waiting = _restarting = _levelUp = false;
        Reconnects++;
        return true;
    }

    /// <summary>The server stopped or failed: nothing is left to connect to.</summary>
    public void Cancel() => Waiting = _restarting = _levelUp = false;
}
