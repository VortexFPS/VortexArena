// Port of common/mutators/mutator/kick_teamkiller/sv_kick_teamkiller.qc

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Kick Teamkiller mutator — port of common/mutators/mutator/kick_teamkiller/sv_kick_teamkiller.qc. In
/// teamplay, a real client whose teamkill count exceeds <c>g_kick_teamkiller_rate</c> teamkills/minute (and is
/// at least <c>g_kick_teamkiller_lower_limit</c>) is, per <c>g_kick_teamkiller_severity</c>, kicked (1),
/// banned (2), or play-banned/observed (default). Enabled when <c>g_kick_teamkiller_rate &gt; 0</c>.
///
/// PORTED: the PlayerDies detection — the teamplay + non-warmup + real-client gate, the
/// <c>teamkills &gt;= lower_limit &amp;&amp; teamkills &gt;= rate * playtime / 60</c> threshold (reading the
/// attacker's SP_TEAMKILLS score via GameScores) — and the severity switch with its severity-correct
/// notifications. The default (severity 0) PLAY-BAN path is enforced Common-side: the offender is silenced
/// (RESPAWN_SILENT), force-spectated (PutObserverInServer ≈ IsObserver), and added to the
/// <c>g_playban_list</c> cvar (cons of netaddress + crypto_idfp), exactly as the QC default case.
///
/// SUBSTRATE: the force-spectate (severity 0), the severity-1 <c>dropclient_schedule</c> kick, and the
/// severity-2 <c>Ban_KickBanClient</c> IP-ban all reach into the Server layer (XonoticGodot.Server:
/// ClientManager.PutObserverInServer / Bans.DropClient / Bans.KickBanClient), which Common cannot reference
/// (Server references Common, not vice-versa). The host injects those actions via <see cref="ForceObserver"/>,
/// <see cref="KickClient"/> and <see cref="BanClient"/> (wired in GameWorld.WireServerInfrastructure), exactly
/// the same delegate-seam pattern as MinigameSessionManager.ObserverForcer. With those wired every branch runs
/// the real punitive action on the live PlayerDies path.
/// </summary>
[Mutator]
public sealed class KickTeamkillerMutator : MutatorBase
{
    public float Rate;        // g_kick_teamkiller_rate (teamkills/minute)
    public float LowerLimit;  // g_kick_teamkiller_lower_limit
    public int   Severity;    // g_kick_teamkiller_severity (1 kick, 2 ban, else play-ban)
    public float BanTime;     // g_kick_teamkiller_bantime (severity 2, seconds)

    public KickTeamkillerMutator() => NetName = "kick_teamkiller";

    // QC: REGISTER_MUTATOR(kick_teamkiller, (autocvar_g_kick_teamkiller_rate > 0));
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_kick_teamkiller_rate") > 0f;

    /// <summary>QC <c>PutObserverInServer(attacker, true, true)</c>: the host's real force-spectate (Server
    /// ClientManager.PutObserverInServer — resets MoveType.None/Solid.Not/TakeDamage.No/FragsSpectator). Injected
    /// by GameWorld; without it the default branch falls back to a bare IsObserver flip.</summary>
    public System.Action<Entity>? ForceObserver { get; set; }

    /// <summary>QC <c>dropclient_schedule(attacker)</c> (severity 1): the host's kick pipeline (Server
    /// Bans.DropClient). Returns whether the drop was scheduled (QC gates the kick broadcast on a true return).
    /// Injected by GameWorld; null until wired.</summary>
    public System.Func<Entity, bool>? KickClient { get; set; }

    /// <summary>QC <c>Ban_KickBanClient(attacker, bantime, masksize, "Team Killing")</c> (severity 2): the host's
    /// IP-ban pipeline (Server Bans.KickBanClient). Args: offender, bantime seconds, masksize. Injected by
    /// GameWorld; null until wired.</summary>
    public System.Action<Entity, float, int>? BanClient { get; set; }

    private HookHandler<MutatorHooks.PlayerDiesArgs>? _onPlayerDies;

    public override void Hook()
    {
        _onPlayerDies ??= OnPlayerDies;
        MutatorHooks.PlayerDies.Add(_onPlayerDies);

        if (Api.Services is not null)
        {
            Rate = Api.Cvars.GetFloat("g_kick_teamkiller_rate");
            LowerLimit = Api.Cvars.GetFloat("g_kick_teamkiller_lower_limit");
            Severity = (int)Api.Cvars.GetFloat("g_kick_teamkiller_severity");
            BanTime = Api.Cvars.GetFloat("g_kick_teamkiller_bantime");
        }
    }

    public override void Unhook()
    {
        if (_onPlayerDies is not null) MutatorHooks.PlayerDies.Remove(_onPlayerDies);
    }

    // MUTATOR_HOOKFUNCTION(kick_teamkiller, PlayerDies)
    private bool OnPlayerDies(ref MutatorHooks.PlayerDiesArgs args)
    {
        if (Api.Services is null) return false;
        // QC: if (!teamplay) return;
        if (!GameScores.Teamplay) return false;
        // QC: if (warmup_stage) return;  — teamkills during warmup must not count toward the threshold.
        if (NotificationSystem.WarmupStage) return false;

        Entity? attacker = args.Attacker;
        // QC: if (!IS_REAL_CLIENT(attacker)) return;  (a connected, non-bot player entity in this port)
        if (attacker is null || (attacker.Flags & EntFlags.Client) == 0) return false;
        if (attacker is Player { IsBot: true }) return false;

        int teamkills = GameScores.Get(attacker, GameScores.TeamKills);
        // QC: playtime = time - CS(attacker).startplaytime — seconds since this client last switched from
        // spectator to player (Player.StartPlayTime, stamped in PutPlayerInServer on the spectator→player edge),
        // NOT since level start. This makes the rate denominator per-client, so a mid-match joiner crosses the
        // rate gate at the same teamkill cadence as Base. A non-Player attacker (already excluded above) or a
        // never-joined client has StartPlayTime 0, collapsing to the level-start clock as before.
        float startPlayTime = (attacker as Player)?.StartPlayTime ?? 0f;
        float playtime = Api.Clock.Time - startPlayTime;

        // QC: rate is teamkills/minutes, playtime in seconds.
        if (teamkills < LowerLimit || teamkills < Rate * playtime / 60f)
            return false;

        // QC switch(autocvar_g_kick_teamkiller_severity): 1 = kick, 2 = IP-ban, default = play-ban/observe.
        switch (Severity)
        {
            case 1:
            {
                // QC: if (dropclient_schedule(attacker)) Send_Notification(... INFO_QUIT_KICK_TEAMKILL ...).
                // The drop runs through the Server ban pipeline (Bans.DropClient), injected as KickClient. QC
                // gates the broadcast on the schedule succeeding — mirror that with the delegate's return.
                if (KickClient?.Invoke(attacker) ?? false)
                    NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, "QUIT_KICK_TEAMKILL",
                        attacker.NetName);
                return false;
            }
            case 2:
            {
                // QC: attacker.respawn_flags = RESPAWN_SILENT; Ban_KickBanClient(attacker, bantime, masksize,
                //     "Team Killing"); Send_Notification(... INFO_QUIT_KICK_TEAMKILL ...).
                SetRespawnSilent(attacker);
                // QC masksize = autocvar_g_ban_default_masksize; bantime = autocvar_g_kick_teamkiller_bantime.
                int masksize = (int)Api.Cvars.GetFloat("g_ban_default_masksize");
                BanClient?.Invoke(attacker, BanTime, masksize);
                NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, "QUIT_KICK_TEAMKILL",
                    attacker.NetName);
                return false;
            }
            default:
            {
                // QC default (play-ban + force-spectate) — fully enforceable Common-side.
                // attacker.respawn_flags = RESPAWN_SILENT;
                SetRespawnSilent(attacker);

                // Build the playban id to append: the offender's netaddress and/or crypto_idfp, each only if
                // not already in the list (QC cons(theid, ...) guarded by !PlayerInIPList / !PlayerInIDList).
                string list = Api.Cvars.GetString("g_playban_list") ?? "";
                string theid = "";
                string addr = (attacker as Player)?.NetAddress ?? "";
                string idfp = (attacker as Player)?.PersistentId ?? "";
                if (addr.Length > 0 && !ListContains(list, addr)) theid = Cons(theid, addr);
                if (idfp.Length > 0 && !ListContains(list, idfp)) theid = Cons(theid, idfp);

                // QC: PutObserverInServer(attacker, true, true) — force the teamkiller to spectate. The real
                // transition (MoveType.None/Solid.Not/TakeDamage.No/FragsSpectator) lives in the Server layer
                // (ClientManager.PutObserverInServer), injected as ForceObserver; fall back to a bare flag flip
                // only if the host never wired it (keeps the offender at least scoreboard-spectating).
                if (ForceObserver is not null) ForceObserver(attacker);
                else if (attacker is Player p) { p.IsObserver = true; p.WantsJoin = 0; }

                // QC: cvar_set("g_playban_list", cons(autocvar_g_playban_list, theid));
                if (theid.Length > 0)
                    Api.Cvars.Set("g_playban_list", Cons(list, theid));

                // QC: Send_Notification(NOTIF_ALL, NULL, MSG_INFO, INFO_QUIT_PLAYBAN_TEAMKILL, attacker.netname);
                NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, "QUIT_PLAYBAN_TEAMKILL",
                    attacker.NetName);
                // QC: Send_Notification(NOTIF_ONE, attacker, MSG_CENTER, CENTER_QUIT_PLAYBAN_TEAMKILL);
                NotificationSystem.Send(NotifBroadcast.One, attacker, MsgType.Center, "QUIT_PLAYBAN_TEAMKILL");

                // QC: if (PlayerInList(attacker, autocvar_g_playban_list)) TRANSMUTE(Observer, attacker);
                // The transmute is already modeled by IsObserver above (ADR-0007 keeps a single Player edict).
                return false;
            }
        }
    }

    // QC: attacker.respawn_flags = RESPAWN_SILENT; (don't network the respawn countdown for the punished kill).
    private static void SetRespawnSilent(Entity attacker)
    {
        if (attacker is Player p) p.RespawnFlags |= RespawnFlag.Silent;
    }

    // QC cons(a, b): space-join two list tokens, skipping empties (server/miscfunctions or util cons()).
    private static string Cons(string a, string b)
    {
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        return a + " " + b;
    }

    // QC PlayerInIPList / PlayerInIDList membership probe against a space-separated list string.
    private static bool ListContains(string list, string token)
    {
        if (token.Length == 0 || list.Length == 0) return false;
        foreach (string part in list.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
            if (part == token) return true;
        return false;
    }
}
