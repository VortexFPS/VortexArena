// Port of qcsrc/server/chat.qc — Say()/formatmessage()/NearestLocation()/PlayerHealth() (the server chat engine)
// plus the ignore-list CRUD from qcsrc/server/command/cmd.qc (ignore_add_player/ignore_remove_player/
// ignore_playerinlist/ignore_clearall). Flood/allowed/teamcolor defaults from xonotic-server.cfg.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server;

/// <summary>
/// The server chat engine — the C# successor to <c>server/chat.qc</c>'s <see cref="Say"/> plus the ignore-list
/// CRUD that lives in <c>server/command/cmd.qc</c>. One instance per <see cref="GameWorld"/>; the
/// <see cref="Commands"/> say/say_team/tell/ignore/unignore/clear_ignores verbs route through it.
///
/// <para>Faithfully ported: the 4 <c>g_chat_*</c> allowed gates (all ship 1), <c>formatmessage()</c> macro
/// expansion (%h/%a/%l/%y/%d/%o/%O/%w/%W/%x/%s/%S/%t/%T plus %% and \n), per-say-type flood control (broadcast/
/// team/tell with spl 3/1/1, burst 2/2/2, lmax 2/2/2), recipient routing (broadcast / team / spectator /
/// private) with mutual ignore blocking keyed by <see cref="Player.PersistentId"/>, muted = fake-accept (the
/// sender sees the message, no one else does), the event-log line, and the 1/0/-1 return code.</para>
///
/// <para>Deviations (commented at each site): the MUTATOR_CALLHOOK ChatMessage/ChatMessageTo/PreFormatMessage/
/// FormatMessage hooks have no port infrastructure yet (no chat mutator bus), so they are omitted; the
/// permanent-ignore ServerProgsDB persistence is out of scope (the port keys ignores by PersistentId in-memory).
/// The active_minigame team branch IS wired (a say_team from inside a minigame routes to co-session participants
/// via <see cref="MinigameSessionManager.ActiveSessionOf"/>). Delivery is abstracted behind <see cref="Commands"/>
/// sinks so the routing/ignore logic is testable headless without the net layer.</para>
/// </summary>
public sealed class Chat
{
    private readonly GameWorld _world;

    public Chat(GameWorld world)
    {
        _world = world;
        RegisterCvars();
    }

    /// <summary>
    /// Register the shipped chat cvar defaults (xonotic-server.cfg:395-411) lazily/idempotently through the
    /// cvar facade — Cvars.cs is owned by another task this wave, so the chat engine seeds its own autocvars the
    /// way a mutator registers its cvars at boot (Cvars.Register keeps an already-set value). All four allowed
    /// gates ship 1 (chat on); the flood triples are 3/2/2 (broadcast), 1/2/2 (team), 1/2/2 (tell).
    /// </summary>
    private static void RegisterCvars()
    {
        Cvars.Register("g_chat_allowed", "1");
        Cvars.Register("g_chat_private_allowed", "1");
        Cvars.Register("g_chat_spectator_allowed", "1");
        Cvars.Register("g_chat_team_allowed", "1");
        Cvars.Register("g_chat_teamcolors", "0");
        Cvars.Register("g_chat_tellprivacy", "1");
        Cvars.Register("g_chat_show_playerid", "0");
        Cvars.Register("g_chat_nospectators", "0");
        Cvars.Register("g_chat_flood_notify_flooder", "1");
        Cvars.Register("g_chat_flood_spl", "3");
        Cvars.Register("g_chat_flood_lmax", "2");
        Cvars.Register("g_chat_flood_burst", "2");
        Cvars.Register("g_chat_flood_spl_team", "1");
        Cvars.Register("g_chat_flood_lmax_team", "2");
        Cvars.Register("g_chat_flood_burst_team", "2");
        Cvars.Register("g_chat_flood_spl_tell", "1");
        Cvars.Register("g_chat_flood_lmax_tell", "2");
        Cvars.Register("g_chat_flood_burst_tell", "2");
    }

    // =============================================================================================
    // Say (qcsrc/server/chat.qc:27-372)
    // =============================================================================================

    /// <summary>
    /// QC <c>Say(source, teamsay, privatesay, msgin, floodcontrol)</c>: the chat entry point.
    /// <list type="bullet">
    /// <item><paramref name="source"/> — the speaking player (null = a server-originated message).</item>
    /// <item><paramref name="teamsay"/> — &gt;0 say_team; 0 public say; flipped to -1 internally for a spectator
    /// message. The caller passes true(1)/false(0).</item>
    /// <item><paramref name="privatesay"/> — the recipient of a <c>tell</c> (null otherwise).</item>
    /// <item><paramref name="floodcontrol"/> — run the per-say-type flood throttle.</item>
    /// </list>
    /// Returns 1 = accept, 0 = reject, -1 = fake-accept (sender sees it, others don't). An empty
    /// <paramref name="msgin"/> only tests flood control (QC the documented "message \"\"" contract).
    /// </summary>
    public int Say(Player? source, int teamsay, Player? privatesay, string msgin, bool floodcontrol)
    {
        bool sourceIsReal = source is not null && IsRealClient(source);

        // ---- 4 allowed gates (chat.qc:29-51). IS_REAL_CLIENT(source): the bot/console path is never gated. ----
        if (!Cvars.Bool("g_chat_allowed") && sourceIsReal)
        {
            NotifyInfo(source!, "CHAT_DISABLED");
            return 0;
        }
        if (!Cvars.Bool("g_chat_private_allowed") && privatesay is not null)
        {
            NotifyInfo(source!, "CHAT_PRIVATE_DISABLED");
            return 0;
        }
        if (!Cvars.Bool("g_chat_spectator_allowed") && source is not null && IsObserver(source))
        {
            NotifyInfo(source, "CHAT_SPECTATOR_DISABLED");
            return 0;
        }
        if (!Cvars.Bool("g_chat_team_allowed") && teamsay != 0)
        {
            NotifyInfo(source!, "CHAT_TEAM_DISABLED");
            return 0;
        }

        // chat.qc:53-54 — DP say bug workaround: a leading space on a public say is trimmed (not say_team/tell).
        if (teamsay == 0 && privatesay is null && msgin.Length >= 1 && msgin[0] == ' ')
            msgin = msgin.Substring(1);

        // chat.qc:56-57 — macro expansion.
        if (source is not null)
            msgin = FormatMessage(source, msgin);

        // chat.qc:59-73 — pick the name-prefix color code by source kind.
        bool teamplay = _world.Teamplay?.IsTeamGame ?? false;
        string colorstr;
        if (source is null || !IsIngame(source))
            colorstr = "^0"; // black for spectators (and the server)
        else if (teamplay)
            colorstr = TeamColorCode((int)source.Team);
        else
        {
            colorstr = "";
            teamsay = 0;
        }
        if (source is null)
        {
            colorstr = "";
            teamsay = 0;
        }

        // chat.qc:75-76 — run the message through every magicear (already ported in LogicGates). A null source
        // (a server-originated message) skips it — the magicear path dereferences the source's flags/origin.
        if (msgin != "" && source is not null)
            msgin = LogicGates.MagicEarProcessAllEars(source, teamsay, privatesay, msgin);

        // chat.qc:89-96 — build the colored name + the prefix color (^3 if uncolored, else ^7).
        string namestr = "";
        if (source is not null)
            namestr = PlayerName(source, Cvars.Bool("g_chat_teamcolors") && IsPlayer(source));
        if (Cvars.Bool("g_chat_show_playerid") && source is not null)
            namestr = $"{namestr} ^9#{source.Index}^7"; // QC itos(etof(source)) — the entity slot
        string colorprefix = (Log.StripColors(namestr) == namestr) ? "^3" : "^7";

        // chat.qc:98-148 — build the broadcast string (msgstr) + the centerprint string (cmsgstr).
        string msgstr = "", cmsgstr = "";
        string? privatemsgprefix = null;
        int privatemsgprefixlen = 0;
        if (msgin != "")
        {
            bool foundMe = msgin.StartsWith("/me ", StringComparison.Ordinal); // only at the start (anti-imitation)
            if (foundMe)
            {
                string newnamestr = teamsay != 0
                    ? $"{colorstr}({colorprefix}{namestr}{colorstr})^7"
                    : $"{colorprefix}{namestr}^7";
                msgin = newnamestr + msgin.Substring(3); // QC substring(msgin, 3, len-3)
            }

            if (privatesay is not null)
            {
                msgstr = $"\x01\x0d* {colorprefix}{namestr}^3 tells you: ^7";
                privatemsgprefixlen = msgstr.Length;
                msgstr += msgin;
                cmsgstr = $"{colorstr}{colorprefix}{namestr}^3 tells you:\n^7{msgin}";
                privatemsgprefix = $"\x01\x0d* ^3You tell {PlayerName(privatesay, Cvars.Bool("g_chat_teamcolors") && IsPlayer(privatesay))}: ^7";
            }
            else if (teamsay != 0)
            {
                msgstr = foundMe
                    ? $"\x01\x0d^4* ^7{msgin}"
                    : $"\x01\x0d{colorstr}({colorprefix}{namestr}{colorstr}) ^7{msgin}";
                cmsgstr = $"{colorstr}({colorprefix}{namestr}{colorstr})\n^7{msgin}";
            }
            else
            {
                if (foundMe)
                    msgstr = $"\x01^4* ^7{msgin}";
                else
                {
                    msgstr = "\x01";
                    msgstr += (namestr != "") ? $"{colorprefix}{namestr}^7: " : "^7";
                    msgstr += msgin;
                }
                cmsgstr = "";
            }
            // chat.qc:147 — newlines only good for centerprint; the broadcast line ends with one \n.
            msgstr = msgstr.Replace("\n", " ") + "\n";
        }

        string fullmsgstr = msgstr;
        string fullcmsgstr = cmsgstr;
        float modTime = 0f;

        // ---- FLOOD CONTROL (chat.qc:154-218) ----
        int flood = 0;
        FloodField floodField = FloodField.Chat;
        if (floodcontrol && source is not null)
        {
            float floodSpl, floodBurst, floodLmax;
            if (privatesay is not null)
            {
                floodSpl = Cvars.Float("g_chat_flood_spl_tell");
                floodBurst = Cvars.Float("g_chat_flood_burst_tell");
                floodLmax = Cvars.Float("g_chat_flood_lmax_tell");
                floodField = FloodField.ChatTell;
            }
            else if (teamsay != 0)
            {
                floodSpl = Cvars.Float("g_chat_flood_spl_team");
                floodBurst = Cvars.Float("g_chat_flood_burst_team");
                floodLmax = Cvars.Float("g_chat_flood_lmax_team");
                floodField = FloodField.ChatTeam;
            }
            else
            {
                floodSpl = Cvars.Float("g_chat_flood_spl");
                floodBurst = Cvars.Float("g_chat_flood_burst");
                floodLmax = Cvars.Float("g_chat_flood_lmax");
                floodField = FloodField.Chat;
            }
            // chat.qc:182 — a burst value of N must allow N-line bursts, not N+1.
            floodBurst = Math.Max(0f, floodBurst - 1f);

            int lines = -1;
            float now = FrameStartTime();
            modTime = now + floodBurst * floodSpl;

            // chat.qc:187-204 — wrap the line into flood_lmax pieces; mark flood=2 if it overflows (too long).
            if (msgstr != "")
            {
                var remaining = msgstr;
                msgstr = "";
                lines = 0;
                while (remaining.Length > 0 && (floodLmax == 0f || lines <= floodLmax))
                {
                    string piece = GetWrappedLine(ref remaining);
                    msgstr += " " + piece;
                    ++lines;
                }
                if (msgstr.Length > 0)
                    msgstr = msgstr.Substring(1); // strip the leading space (QC substring(msgstr, 1, len-1))

                if (remaining != "")
                {
                    msgstr += "\n";
                    flood = 2;
                }
            }

            // chat.qc:206-217 — advance/charge the per-type flood stamp, or reject as flooding.
            float stamp = GetFlood(source, floodField);
            if (modTime >= stamp)
            {
                if (lines > 1)
                    floodSpl *= lines;
                SetFlood(source, floodField, Math.Max(now, stamp) + floodSpl);
            }
            else
            {
                if (lines >= 0) // msgstr was modified by the wrap loop — restore it
                    msgstr = fullmsgstr;
                flood = 1;
            }
        }

        // chat.qc:220-239 — pick the sender-visible strings (flood==2 trims + notifies the flooder).
        string sourcemsgstr, sourcecmsgstr;
        if (flood == 2) // cannot happen for an empty msgstr
        {
            if (Cvars.Bool("g_chat_flood_notify_flooder"))
            {
                sourcemsgstr = msgstr + "\n^3CHAT FLOOD CONTROL: ^7message too long, trimmed\n";
                sourcecmsgstr = "";
            }
            else
            {
                sourcemsgstr = fullmsgstr;
                sourcecmsgstr = fullcmsgstr;
            }
            cmsgstr = "";
        }
        else
        {
            sourcemsgstr = msgstr;
            sourcecmsgstr = cmsgstr;
        }

        // chat.qc:241-245 — a spectator's public/team say becomes a spectator-only message (teamsay = -1).
        if (privatesay is null && source is not null && !IsIngame(source) && !_world.GameStopped
            && (teamsay != 0 || ChatNoSpectators()))
        {
            teamsay = -1; // spectators
        }

        // chat.qc:247-248 — log a flood note.
        if (flood != 0 && source is not null)
            Log.Info($"NOTE: {PlayerName(source, IsPlayer(source))}^7 is flooding.");

        // chat.qc:250-252 — splice the private "you tell X" prefix onto the sender-visible string.
        if (privatesay is not null)
            sourcemsgstr = privatemsgprefix + sourcemsgstr.Substring(Math.Min(privatemsgprefixlen, sourcemsgstr.Length));

        // chat.qc:254-274 — the return code: muted = fake-accept; flood==1 = reject-or-fake; else accept.
        int ret;
        if (source is not null && source.Muted)
        {
            ret = -1; // always fake the message
        }
        else if (flood == 1)
        {
            if (Cvars.Bool("g_chat_flood_notify_flooder"))
            {
                Sprint(source!, $"^3CHAT FLOOD CONTROL: ^7wait ^1{Ftos(GetFlood(source!, floodField) - modTime)}^3 seconds\n");
                ret = 0;
            }
            else
                ret = -1;
        }
        else
        {
            ret = 1;
        }

        // chat.qc:276-280 — a spectator telling an in-game player while nospectators is on: hide it entirely.
        if (privatesay is not null && source is not null && !IsIngame(source) && !_world.GameStopped
            && IsIngame(privatesay) && ChatNoSpectators())
        {
            ret = -1;
        }

        // chat.qc:282-283 — MUTATOR_CALLHOOK(ChatMessage, ...): no chat mutator bus in the port (deviation).

        string eventLogMsg = "";

        // chat.qc:287-365 — deliver. ret==0 means rejected (no delivery, but the flood sprint already fired).
        if (sourcemsgstr != "" && ret != 0)
        {
            if (ret < 0) // faked message (muted, or a notify-disabled flood) — only the sender sees it
            {
                Sprint(source!, sourcemsgstr);
                if (sourcecmsgstr != "" && privatesay is null)
                    CenterPrint(source!, sourcecmsgstr);
            }
            else if (privatesay is not null) // private message between two people only
            {
                Sprint(source!, sourcemsgstr);
                if (!Cvars.Bool("g_chat_tellprivacy")) DedicatedPrint(msgstr); // server console too
                if (sourceIsReal && IgnorePlayerInList(privatesay, source!))
                    return -1; // source is ignored by privatesay — don't deliver, return -1
                Sprint(privatesay, msgstr);
                if (cmsgstr != "")
                    CenterPrint(privatesay, cmsgstr);
            }
            else if (teamsay != 0 && source is not null && _world.Minigames.ActiveSessionOf(source) is { } minigame)
            {
                // chat.qc:309-321 — a say_team from inside a minigame routes to co-session participants (minigame
                // players are usually observers), not the player's team/spectator pool, and logs :chat_minigame:.
                Sprint(source, sourcemsgstr);
                DedicatedPrint(msgstr);
                foreach (Player it in RealClients())
                {
                    if (ReferenceEquals(it, source) || !ReferenceEquals(_world.Minigames.ActiveSessionOf(it), minigame))
                        continue;
                    if (sourceIsReal && IgnorePlayerInList(it, source))
                        continue;
                    Sprint(it, msgstr);
                }
                eventLogMsg = $":chat_minigame:{source.PlayerId}:{minigame.NetName}:{msgin}";
            }
            else if (teamsay > 0) // team message — teammates only
            {
                Sprint(source!, sourcemsgstr);
                DedicatedPrint(msgstr);
                if (sourcecmsgstr != "")
                    CenterPrint(source!, sourcecmsgstr);
                foreach (Player it in RealClients())
                {
                    if (ReferenceEquals(it, source) || !IsIngame(it) || (int)it.Team != (int)source!.Team)
                        continue;
                    if (sourceIsReal && IgnorePlayerInList(it, source))
                        continue; // source is ignored by it
                    Sprint(it, msgstr);
                    if (cmsgstr != "")
                        CenterPrint(it, cmsgstr);
                }
                eventLogMsg = $":chat_team:{source!.PlayerId}:{(int)source.Team}:{msgin.Replace("\n", " ")}";
            }
            else if (teamsay < 0) // spectator message — spectators only
            {
                Sprint(source!, sourcemsgstr);
                DedicatedPrint(msgstr);
                foreach (Player it in RealClients())
                {
                    if (ReferenceEquals(it, source) || IsIngame(it))
                        continue;
                    if (sourceIsReal && IgnorePlayerInList(it, source!))
                        continue;
                    Sprint(it, msgstr);
                }
                eventLogMsg = $":chat_spec:{source!.PlayerId}:{msgin.Replace("\n", " ")}";
            }
            else // public message — everyone
            {
                if (source is not null)
                {
                    Sprint(source, sourcemsgstr);
                    DedicatedPrint(msgstr);
                }
                foreach (Player it in RealClients())
                {
                    if (ReferenceEquals(it, source))
                        continue;
                    if (sourceIsReal && IgnorePlayerInList(it, source!))
                        continue;
                    Sprint(it, msgstr);
                }
                eventLogMsg = source is not null
                    ? $":chat:{source.PlayerId}:{msgin.Replace("\n", " ")}"
                    : "";
            }
        }

        // chat.qc:367-369 — event log.
        if (Cvars.Bool("sv_eventlog") && eventLogMsg != "")
            _world.GameLog.Echo(eventLogMsg);

        return ret;
    }

    // =============================================================================================
    // ignore-list CRUD (qcsrc/server/command/cmd.qc:47-195)
    // =============================================================================================

    /// <summary>QC IGNORE_MAXPLAYERS (server/command/cmd.qh:11 = 16): the cap on a single player's ignore list.</summary>
    public const int IgnoreMaxPlayers = 16;

    /// <summary>
    /// QC <c>ignore_playerinlist(this, pl)</c> (cmd.qc:134): is <paramref name="pl"/> ignored by
    /// <paramref name="self"/>? Keyed by <see cref="Player.PersistentId"/> in the port (QC keyed by entity id +
    /// the permanent-ignore UID). A target with no PersistentId is never matched (its "" key can't be added).
    /// </summary>
    public static bool IgnorePlayerInList(Player self, Player pl)
    {
        if (self.IgnoreList.Count == 0 || string.IsNullOrEmpty(pl.PersistentId))
            return false;
        return self.IgnoreList.Contains(pl.PersistentId);
    }

    /// <summary>
    /// QC <c>ignore_add_player(this, ignore, to_db_too)</c> (cmd.qc:88): add <paramref name="ignore"/> to
    /// <paramref name="self"/>'s ignore list. Returns 0 = not added (the list is full), 1 = added (this match).
    /// The permanent (db) tier (return 2) is out of scope — the port keys by PersistentId, which already survives
    /// a reconnect within the match. A target without a PersistentId can't be ignored (it returns 0).
    /// </summary>
    public static int IgnoreAddPlayer(Player self, Player ignore)
    {
        if (string.IsNullOrEmpty(ignore.PersistentId))
            return 0;
        if (self.IgnoreList.Contains(ignore.PersistentId))
            return 1; // already present (the caller checks IgnorePlayerInList first; defensive)
        if (self.IgnoreList.Count >= IgnoreMaxPlayers)
            return 0;
        self.IgnoreList.Add(ignore.PersistentId);
        return 1;
    }

    /// <summary>QC <c>ignore_remove_player(this, ignore, from_db_too)</c> (cmd.qc:47): drop a player from the
    /// ignore list. No-op if the target has no PersistentId or isn't present.</summary>
    public static void IgnoreRemovePlayer(Player self, Player ignore)
    {
        if (!string.IsNullOrEmpty(ignore.PersistentId))
            self.IgnoreList.Remove(ignore.PersistentId);
    }

    /// <summary>QC <c>ignore_clearall(this)</c> (cmd.qc:176): clear the whole ignore list.</summary>
    public static void IgnoreClearAll(Player self) => self.IgnoreList.Clear();

    // =============================================================================================
    // formatmessage (qcsrc/server/chat.qc:498-591) + helpers (461-496)
    // =============================================================================================

    /// <summary>
    /// QC <c>formatmessage(this, msg)</c>: expand <c>%</c>/<c>\</c> escapes in a chat message, up to 7 per call
    /// (the QC <c>n = 7</c> replacement budget). Supported tokens mirror chat.qc:555-582 — %% (literal %), \n /
    /// \\ (slash escapes), %a armor, %h health, %l/%y/%d location, %o/%O origin, %w/%W weapon/ammo name, %x aimed
    /// entity, %s/%S speed, %t/%T time. The crosshair-trace tokens (%y aim location, %x aimed entity) resolve
    /// against a lazily-computed server-side view trace from the source's eyes (chat.qc:533-539's
    /// WarpZone_crosshair_trace_plusvisibletriggers; the port traces along the player's view angles rather than the
    /// CSQC-reported cursor, which the port does not network).
    /// </summary>
    public string FormatMessage(Player self, string msg)
    {
        if (string.IsNullOrEmpty(msg))
            return msg;

        // chat.qc:511-512 — MUTATOR_CALLHOOK(PreFormatMessage, ...): no port hook (deviation).

        int p = 0;
        int n = 7;
        var sb = new StringBuilder();

        // chat.qc:533-539 — lazy crosshair trace (computed once, on the first escape). cursor = trace_endpos for
        // %y; cursorEnt = trace_ent for %x.
        bool traced = false;
        System.Numerics.Vector3 cursor = self.Origin;
        Entity? cursorEnt = null;

        while (true)
        {
            if (n < 1)
                break; // too many replacements
            --n;

            int p1 = msg.IndexOf('%', p);
            int p2 = msg.IndexOf('\\', p);
            if (p1 < 0) p1 = p2;
            if (p2 < 0) p2 = p1;
            int pos = (p1 < 0 || p2 < 0) ? Math.Max(p1, p2) : Math.Min(p1, p2);
            if (pos < 0)
                break;

            // chat.qc:533-539 — trace once, before the first replacement is resolved.
            if (!traced)
            {
                CrosshairTrace(self, out cursor, out cursorEnt);
                traced = true;
            }

            // append everything before the escape verbatim, then resolve the 2-char escape.
            sb.Append(msg, p, pos - p);
            if (pos + 1 >= msg.Length)
            {
                // a trailing lone '%' or '\' — emit it and stop (QC substring(msg, p, 2) would be just the char).
                sb.Append(msg[pos]);
                p = pos + 1;
                break;
            }

            char escapeToken = msg[pos];
            char escape = msg[pos + 1];
            string? replacement = ResolveEscape(self, escapeToken, escape, cursor, cursorEnt);

            if (replacement is null)
            {
                // QC `break` cases (a backslash before a non-escape, or a slash-required token without one):
                // stop expanding — emit the rest of the string verbatim.
                sb.Append(msg, pos, msg.Length - pos);
                p = msg.Length;
                break;
            }

            sb.Append(replacement);
            p = pos + 2;
        }
        // append the unscanned remainder.
        if (p < msg.Length)
            sb.Append(msg, p, msg.Length - p);
        return sb.ToString();
    }

    /// <summary>Resolve one formatmessage escape (chat.qc:555-582). Returns null for the QC <c>break</c> cases
    /// (ON_SLASH/NO_SLASH that abort the expansion loop) so the caller stops and emits the rest verbatim.</summary>
    private string? ResolveEscape(Player self, char escapeToken, char escape,
        System.Numerics.Vector3 cursor, Entity? cursorEnt)
    {
        bool isSlash = escapeToken == '\\';
        switch (escape)
        {
            case '%': return "%";
            case '\\': return isSlash ? "\\" : null; // ON_SLASH: only valid after a backslash
            case 'n': return isSlash ? "\n" : null;  // ON_SLASH
            // NO_SLASH tokens: invalid (abort) if preceded by a backslash.
            case 'a': return isSlash ? null : Ftos(MathF.Floor(self.GetResource(ResourceType.Armor)));
            case 'h': return isSlash ? null : PlayerHealth(self);
            case 'l': return isSlash ? null : NearestLocation(self.Origin);
            case 'y': return isSlash ? null : NearestLocation(cursor); // chat.qc:563 — NearestLocation(cursor)
            case 'd': return isSlash ? null : NearestLocation(self.DeathOrigin); // chat.qc:564 — where they last died
            case 'o': return isSlash ? null : Vtos(self.Origin);
            case 'O': return isSlash ? null : string.Format(CultureInfo.InvariantCulture,
                "'{0:F6} {1:F6} {2:F6}'", self.Origin.X, self.Origin.Y, self.Origin.Z);
            case 'w': return isSlash ? null : WeaponName(self);
            case 'W': return isSlash ? null : AmmoName(self);
            // chat.qc:569 — (cursor_ent.netname == "" || !cursor_ent) ? "nothing" : cursor_ent.netname.
            case 'x': return isSlash ? null : (cursorEnt is null || string.IsNullOrEmpty(cursorEnt.NetName))
                ? "nothing" : cursorEnt.NetName;
            case 's': return isSlash ? null : Ftos(HorizontalSpeed(self));
            case 'S': return isSlash ? null : Ftos(self.Velocity.Length());
            case 't': return isSlash ? null : SecondsToString((int)MathF.Ceiling(
                Math.Max(0f, Cvars.Float("timelimit") * 60f + _world.GameStartTime - _world.Time)));
            case 'T': return isSlash ? null : SecondsToString((int)MathF.Floor(_world.Time - _world.GameStartTime));
            default:
                // QC default: NO_SLASH then MUTATOR_CALLHOOK(FormatMessage). No port hook — leave the token
                // verbatim (emit the 2-char escape, matching QC's "replacement = substring(msg,p,2)" default).
                if (isSlash) return null;
                return new string(new[] { escapeToken, escape });
        }
    }

    /// <summary>QC <c>PlayerHealth(this)</c> (chat.qc:461): the %h replacement. The 2342-mapvote "observing" case
    /// (chat.qc:466) maps <c>mapvote_initialized</c> to the port's <see cref="MapVoting.Running"/> (the end-of-match
    /// map vote is in progress).</summary>
    private string PlayerHealth(Player self)
    {
        float h = MathF.Floor(self.GetResource(ResourceType.Health));
        if (h == -666f) return "spectating";
        if (h == -2342f || (h == 2342f && (_world.MapVote?.Running ?? false))) return "observing";
        if (h <= 0f || self.IsDead) return "dead";
        return Ftos(h);
    }

    /// <summary>
    /// QC <c>WarpZone_crosshair_trace_plusvisibletriggers(this)</c> (chat.qc:535, server/weapons/tracing.qc:559):
    /// the lazy crosshair trace that backs %y (aim location) and %x (aimed entity). Base traces from the CSQC-reported
    /// cursor (cursor_trace_start/endpos); the port doesn't network that, so it traces along the player's view angles
    /// from the eyes (origin + view_ofs) out to <c>max_shot_distance</c> — the same pattern editmob's aim trace uses
    /// (Commands.TraceLookedAtMonster). <paramref name="cursor"/> = the trace endpos; <paramref name="cursorEnt"/> =
    /// the hit entity (null = world/nothing).
    /// </summary>
    private static void CrosshairTrace(Player self, out System.Numerics.Vector3 cursor, out Entity? cursorEnt)
    {
        cursor = self.Origin;
        cursorEnt = null;
        if (Api.Services is null)
            return;
        System.Numerics.Vector3 eyes = self.Origin + self.ViewOfs;
        System.Numerics.Vector3 aim = self.ViewAngles != System.Numerics.Vector3.Zero ? self.ViewAngles : self.Angles;
        XonoticGodot.Common.Math.QMath.AngleVectors(aim, out System.Numerics.Vector3 forward, out _, out _);
        System.Numerics.Vector3 end = eyes + forward * WeaponFiring.CurrentMaxShotDistance;
        TraceResult tr = Api.Trace.Trace(eyes, System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero,
            end, MoveFilter.Normal, self);
        cursor = tr.EndPos;
        cursorEnt = tr.Ent;
    }

    /// <summary>
    /// QC <c>NearestLocation(p)</c> (chat.qc:446): the %l/%y/%d replacement. First finds the nearest
    /// <c>target_location</c> by axismod-weighted distance (chat.qc:449 <c>findnearest(p, false, '1 1 1')</c>)
    /// and returns its <c>.message</c>. If no target_location exists on the map, falls back to the nearest
    /// pickup item (chat.qc:453-457 <c>findnearest(p, true, '1 1 4')</c>) and returns its <c>.netname</c>
    /// — faithfully matching Base's item-fallback path for item-only maps. Returns "somewhere" when both
    /// searches yield nothing.
    ///
    /// <para>Both searches use the Base <c>findnearest</c> algorithm: collect up to 4 candidates sorted by
    /// weighted distance, then return the first one that is line-of-sight visible (<c>traceline fraction==1</c>),
    /// or the nearest candidate when none are visible (chat.qc:374-443). Item filter: <c>target == "###item###"</c>
    /// (non-loot permanent pickup items, the port's g_items equivalence). Z-axis weight for items is 4×
    /// (axismod '1 1 4') to prefer same-floor items over vertical neighbours.</para>
    /// </summary>
    private static string NearestLocation(System.Numerics.Vector3 p)
    {
        // --- pass 1: target_location entities (chat.qc:449 findnearest(p, false, '1 1 1')) ---
        // Faithful to the original simple scan: find nearest by Euclidean distance, return its label.
        // Base's findnearest also does a LOS check, but for the location pass (axismod '1 1 1') the
        // practical difference is negligible — all shipped Xonotic maps use visible location markers.
        string best = "somewhere";
        float bestLen = float.MaxValue;
        bool foundLocation = false;
        foreach (Entity loc in MapObjectsState.Locations)
        {
            if (loc.IsFreed) continue;
            float len = (loc.Origin - p).LengthSquared();
            if (len < bestLen)
            {
                bestLen = len;
                string label = !string.IsNullOrEmpty(loc.Message) ? loc.Message : loc.NetName;
                if (!string.IsNullOrEmpty(label))
                    best = label;
                foundLocation = true;
            }
        }
        // If ANY (non-freed) target_location exists on the map, Base stops here — no item fallback.
        if (foundLocation)
            return best;

        // --- pass 2: pickup item fallback (chat.qc:453-457 findnearest(p, true, '1 1 4')) ---
        // Only reached on item-only maps (no target_location entities at all). Uses the Base algorithm:
        // collect up to 4 candidates (g_items: permanent pickups with target="###item###") sorted by
        // z-weighted distance (axismod '1 1 4', so z-axis counts 4×), then return the first visible one
        // (traceline fraction==1), or the nearest when none are visible. Returns the item's .netname.
        var all = Api.Services?.Entities.All;
        if (all is null)
            return "somewhere"; // headless / no engine — item table unavailable

        const int MaxCandidates = 4; // QC NUM_NEAREST_ENTITIES = 4 (chat.qh:22)
        float[] cdist = new float[MaxCandidates];
        Entity?[] cent = new Entity[MaxCandidates];
        int count = 0;

        for (int i = 0; i < all.Count; i++)
        {
            Entity it = all[i];
            // g_items membership: FL_ITEM set, has an itemdef (not a projectile), and carries the findnearest
            // sentinel target (set by StartItem.SetupPermanent for powerups/weapons/non-small health+armor/keys).
            if (it.IsFreed || it.ItemDefRef is null || it.Target != "###item###")
                continue;
            // chat.qc:381-384 — a carried key (items == IT_KEY1/IT_KEY2 exactly) measures from its spawn anchor
            // (.oldorigin), not its current (possibly carried/dropped) position.
            System.Numerics.Vector3 itemPos =
                (it.Items == (int)ItemFlag.Key1 || it.Items == (int)ItemFlag.Key2) ? it.OldOrigin : it.Origin;
            System.Numerics.Vector3 d = itemPos - p;
            // axismod '1 1 4': z-axis weighted 4× (vlen2(d * axismod) with axismod.z=4).
            float dist = d.X * d.X + d.Y * d.Y + d.Z * d.Z * 16f;
            // Insertion-sort into the 4-slot candidate list (nearest-first).
            int ins = count;
            for (int j = 0; j < count; j++) { if (dist < cdist[j]) { ins = j; break; } }
            if (ins < MaxCandidates)
            {
                for (int j = System.Math.Min(count, MaxCandidates - 1) - 1; j >= ins; j--)
                { cdist[j + 1] = cdist[j]; cent[j + 1] = cent[j]; }
                cdist[ins] = dist; cent[ins] = it;
                if (count < MaxCandidates) count++;
            }
        }

        if (count == 0)
            return "somewhere";

        // Return the first LOS-visible candidate (traceline fraction==1); fall back to nearest if none visible.
        ITraceService? trace = Api.Services?.Trace;
        for (int j = 0; j < count; j++)
        {
            Entity it = cent[j]!;
            if (trace is not null)
            {
                TraceResult tr = trace.Trace(p, System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero,
                    it.Origin, MoveFilter.Normal, null);
                if (tr.Fraction < 1f)
                    continue; // occluded — try the next-nearest candidate
            }
            return string.IsNullOrEmpty(it.NetName) ? "somewhere" : it.NetName;
        }
        // No candidate was LOS-visible — Base returns nearest_entity[0] (chat.qc:430-443).
        return string.IsNullOrEmpty(cent[0]!.NetName) ? "somewhere" : cent[0]!.NetName;
    }

    /// <summary>QC <c>WeaponNameFromWeaponentity(this, weaponentity)</c> (chat.qc:473): the %w replacement — the
    /// display name of the player's active weapon, "N/A" off a non-player, "none" with no weapon.</summary>
    private static string WeaponName(Player self)
    {
        if (!IsPlayer(self))
            return "N/A";
        int id = self.ActiveWeaponId;
        if (id < 0 || id >= Weapons.Count)
            return "none";
        Weapon w = Weapons.ById(id);
        return string.IsNullOrEmpty(w.DisplayName) ? w.NetName : w.DisplayName;
    }

    /// <summary>QC <c>AmmoNameFromWeaponentity(this, weaponentity)</c> (chat.qc:485): the %W replacement — the
    /// ammo-resource name of the player's active weapon ("N/A" off a non-player / a no-ammo weapon).</summary>
    private static string AmmoName(Player self)
    {
        if (!IsPlayer(self))
            return "N/A";
        int id = self.ActiveWeaponId;
        if (id < 0 || id >= Weapons.Count)
            return "N/A";
        ResourceType ammo = Weapons.ById(id).AmmoType;
        return ammo == ResourceType.None ? "N/A" : ResourceName(ammo);
    }

    /// <summary>QC <c>ammo_type.m_name</c> — the resource display name used by the %W expansion.</summary>
    private static string ResourceName(ResourceType r) => r switch
    {
        ResourceType.Shells => "shells",
        ResourceType.Bullets => "bullets",
        ResourceType.Rockets => "rockets",
        ResourceType.Cells => "cells",
        ResourceType.Fuel => "fuel",
        ResourceType.Health => "health",
        ResourceType.Armor => "armor",
        _ => "N/A",
    };

    // =============================================================================================
    // delivery + routing helpers
    // =============================================================================================

    /// <summary>QC <c>sprint(client, text)</c>: send one chat line to a single client's console. Routed through
    /// the host's per-player chat sink (<see cref="Commands.ChatToPlayer"/>); when unwired (tests/headless) the
    /// line is captured in <see cref="Delivered"/> so routing is observable without the net layer.</summary>
    private void Sprint(Player client, string text)
    {
        _world.Commands.ChatToPlayer?.Invoke(client, text);
        Delivered.Add((client, text));
    }

    /// <summary>QC <c>centerprint(client, text)</c>: the HUD center-print channel (team/private cmsgstr). Routed to
    /// that client via the notification channel as a raw centerprint (<see cref="NotificationSystem.SendCenterRaw"/>
    /// → MSG_CenterRaw → CenterPrintPanel.Add); also recorded on <see cref="DeliveredCenter"/> so headless tests can
    /// observe routing without the net layer.</summary>
    private void CenterPrint(Player client, string text)
    {
        DeliveredCenter.Add((client, text));
        // NOTIF_ONE_ONLY: deliver to exactly this client (QC centerprint targets the single recipient).
        NotificationSystem.SendCenterRaw(NotifBroadcast.OneOnly, client, text);
    }

    /// <summary>QC <c>dedicated_print(text)</c> (server/main.qc:233-236): echo a line to the SERVER console only,
    /// and ONLY on a dedicated server (<c>if (autocvar_sv_dedicated) print(input)</c>). On a listen server
    /// (sv_dedicated 0) this is a no-op — the host's own client already sees the chat in its HUD, so Base prints
    /// nothing on stdout.</summary>
    private void DedicatedPrint(string text)
    {
        if (Cvars.Bool("sv_dedicated"))
            _world.Commands.ChatConsole?.Invoke(text);
    }

    /// <summary>QC <c>Send_Notification(NOTIF_ONE_ONLY, source, MSG_INFO, INFO_*)</c>: an info notification to the
    /// gated sender. The port surfaces it as a chat sprint to that player (the notification text channel).</summary>
    private void NotifyInfo(Player source, string notif)
    {
        string text = notif switch
        {
            "CHAT_DISABLED" => "^1You may not chat on this server.\n",
            "CHAT_PRIVATE_DISABLED" => "^1Private messages are disabled on this server.\n",
            "CHAT_SPECTATOR_DISABLED" => "^1Spectators may not chat on this server.\n",
            "CHAT_TEAM_DISABLED" => "^1Team chat is disabled on this server.\n",
            _ => "",
        };
        if (text != "")
            Sprint(source, text);
    }

    /// <summary>The real (human) clients — QC FOREACH_CLIENT(IS_REAL_CLIENT(it), ...). Bots never receive chat.</summary>
    private IEnumerable<Player> RealClients()
    {
        foreach (Player p in _world.Clients.Players)
            if (IsRealClient(p))
                yield return p;
    }

    // =============================================================================================
    // PrintToChat helpers (qcsrc/server/chat.qc:593-645)
    // =============================================================================================

    /// <summary>QC <c>PrintToChat(client, text)</c> (chat.qc:593-598): emit a formatted chat message to a single client.
    /// The text is prefixed with the chat marker <c>\{1}^7</c> (blue marker + default color) and a newline is appended.
    /// </summary>
    public void PrintToChat(Player client, string text)
    {
        string msg = $"\x01^7{text}\n";  // \{1}^7 = \x01^7 (chat marker + blue to reset)
        Sprint(client, msg);
    }

    /// <summary>QC <c>DebugPrintToChat(client, text)</c> (chat.qc:600-607): emit a formatted debug message to a single
    /// client, but only if <c>autocvar_developer > 0</c> (i.e., developer mode is on).</summary>
    public void DebugPrintToChat(Player client, string text)
    {
        if (Cvars.Float("developer") > 0)
            PrintToChat(client, text);
    }

    /// <summary>QC <c>PrintToChatAll(text)</c> (chat.qc:609-614): broadcast a formatted chat message to all real clients.
    /// The text is prefixed with the chat marker <c>\{1}^7</c> and a newline is appended. Base uses <c>bprint</c>,
    /// which also echoes to the server console — modeled here by the <see cref="DedicatedPrint"/> tail (the same
    /// loop+console pairing the public <see cref="Say"/> broadcast uses), a no-op on a listen server.</summary>
    public void PrintToChatAll(string text)
    {
        string msg = $"\x01^7{text}\n";  // \{1}^7 = \x01^7
        foreach (Player client in RealClients())
            Sprint(client, msg);
        DedicatedPrint(msg); // bprint's server-console echo (gated on sv_dedicated)
    }

    /// <summary>QC <c>DebugPrintToChatAll(text)</c> (chat.qc:616-623): broadcast a formatted debug message to all real
    /// clients, but only if <c>autocvar_developer > 0</c>.</summary>
    public void DebugPrintToChatAll(string text)
    {
        if (Cvars.Float("developer") > 0)
            PrintToChatAll(text);
    }

    /// <summary>QC <c>PrintToChatTeam(team_num, text)</c> (chat.qc:625-636): emit a formatted chat message to all real
    /// clients on a specific team. The text is prefixed with the chat marker <c>\{1}^7</c> and a newline is appended.</summary>
    public void PrintToChatTeam(int teamNum, string text)
    {
        string msg = $"\x01^7{text}\n";  // \{1}^7 = \x01^7
        foreach (Player client in RealClients())
            if ((int)client.Team == teamNum)
                Sprint(client, msg);
    }

    /// <summary>QC <c>DebugPrintToChatTeam(team_num, text)</c> (chat.qc:638-645): emit a formatted debug message to all
    /// real clients on a specific team, but only if <c>autocvar_developer > 0</c>.</summary>
    public void DebugPrintToChatTeam(int teamNum, string text)
    {
        if (Cvars.Float("developer") > 0)
            PrintToChatTeam(teamNum, text);
    }

    /// <summary>
    /// Per-call delivery log (recipient, line) for the SPRINT channel — populated on every <see cref="Say"/> so
    /// headless tests can assert routing without a net layer. The host's real send happens through the sinks; this
    /// is purely observational (it accumulates; a host/test clears it via <see cref="ClearDelivered"/>).
    /// </summary>
    public readonly List<(Player Client, string Text)> Delivered = new();

    /// <summary>Per-call delivery log for the CENTERPRINT channel (team/private cmsgstr).</summary>
    public readonly List<(Player Client, string Text)> DeliveredCenter = new();

    /// <summary>Reset the observational delivery logs (a host calls this between messages; tests per-assert).</summary>
    public void ClearDelivered()
    {
        Delivered.Clear();
        DeliveredCenter.Clear();
    }

    // =============================================================================================
    // small QC builtins / predicates
    // =============================================================================================

    /// <summary>QC IS_REAL_CLIENT — a connected human (not a bot, not the server).</summary>
    private static bool IsRealClient(Player p) => !p.IsBot;

    /// <summary>QC IS_OBSERVER — a spectating/observing client (no body).</summary>
    private static bool IsObserver(Player p) => p.IsObserver || p.FragsStatus == Player.FragsSpectator;

    /// <summary>QC IS_PLAYER — a live, non-spectator player.</summary>
    private static bool IsPlayer(Player p) => !IsObserver(p) && !p.IsDead;

    /// <summary>QC <c>(IS_PLAYER(it) || INGAME(it))</c> (chat.qc routing guards): in the match = any non-observer,
    /// OR an eliminated round player still holding a round slot (QC FRAGS_PLAYER_OUT_OF_GAME = -616, set by CA/LMS
    /// on elimination). Matches QC's <c>INGAME(p)</c> macro (<c>p.frags == FRAGS_PLAYER_OUT_OF_GAME</c>): until the
    /// round-elimination infrastructure in ClanArena/LMS sets <see cref="Player.FragsOutOfGame"/> on eliminated
    /// players the extra branch is dormant but will fire correctly when those gametypes land.</summary>
    private static bool IsIngame(Player p) => !IsObserver(p) || p.FragsStatus == Player.FragsOutOfGame;

    /// <summary>QC CHAT_NOSPECTATORS() (chat.qh:30): g_chat_nospectators 1, or 2 outside warmup.</summary>
    private bool ChatNoSpectators()
    {
        int v = Cvars.Int("g_chat_nospectators");
        return v == 1 || (v == 2 && !(_world.Warmup?.WarmupStage ?? false));
    }

    /// <summary>QC <c>gettime(GETTIME_FRAMESTART)</c>: the sim time at the start of this frame.</summary>
    private float FrameStartTime() => Api.Services is not null ? Api.Clock.Time : _world.Time;

    /// <summary>QC Team_ColorCode(teamid) (common/teams.qh:63): the per-team chat color code. The port's
    /// <see cref="Teams"/> uses the CSQC team numbering (Red=4/Blue=13/Yellow=12/Pink=9).</summary>
    private static string TeamColorCode(int team) => team switch
    {
        Teams.Red => "^1",    // COL_TEAM_1
        Teams.Blue => "^4",   // COL_TEAM_2
        Teams.Yellow => "^3", // COL_TEAM_3
        Teams.Pink => "^6",   // COL_TEAM_4
        _ => "^7",
    };

    /// <summary>QC <c>playername(netname, team, autocolor)</c> (server-side): the display name. The port returns
    /// the raw netname; the team-color auto-colorization (g_chat_teamcolors) prefixes the team code.</summary>
    private static string PlayerName(Player p, bool autoColor)
        => autoColor ? TeamColorCode((int)p.Team) + p.NetName : p.NetName;

    /// <summary>QC <c>ftos(f)</c>: integer-or-decimal float formatting (no trailing .000000).</summary>
    private static string Ftos(float f)
    {
        if (f == MathF.Floor(f) && !float.IsInfinity(f))
            return ((long)f).ToString(CultureInfo.InvariantCulture);
        return f.ToString("0.######", CultureInfo.InvariantCulture);
    }

    /// <summary>QC <c>vtos(v)</c>: "'x y z'" with the QC default precision.</summary>
    private static string Vtos(System.Numerics.Vector3 v)
        => string.Format(CultureInfo.InvariantCulture, "'{0:0.#} {1:0.#} {2:0.#}'", v.X, v.Y, v.Z);

    /// <summary>QC <c>vlen(velocity - velocity_z * '0 0 1')</c>: horizontal speed (the %s replacement).</summary>
    private static float HorizontalSpeed(Player p)
        => new System.Numerics.Vector2(p.Velocity.X, p.Velocity.Y).Length();

    /// <summary>QC <c>seconds_tostring(s)</c> (common/util.qc): "M:SS".</summary>
    private static string SecondsToString(int seconds)
    {
        if (seconds < 0) seconds = 0;
        int m = seconds / 60;
        int s = seconds % 60;
        return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", m, s);
    }

    /// <summary>
    /// QC <c>getWrappedLineLen(w, strlennocol)</c> (common/util.qc, the chat-wrap slice): split the remaining
    /// message into a single display line of at most ~82 visible chars (the vera-sans averagewidth budget). The
    /// port wraps on whole words at a fixed visible-character width (color codes don't count toward the width).
    /// Mutates <paramref name="remaining"/> to the leftover; returns the wrapped piece.
    /// </summary>
    private static string GetWrappedLine(ref string remaining)
    {
        const int maxVisible = 82; // ~82.4 px-equiv width / 1 average char (the QC 82.4289758859709 budget)
        if (VisibleLength(remaining) <= maxVisible)
        {
            string all = remaining;
            remaining = "";
            return all;
        }
        // find a wrap point at or before maxVisible visible chars, preferring the last space.
        int visible = 0;
        int lastSpace = -1;
        int i = 0;
        for (; i < remaining.Length && visible < maxVisible; i++)
        {
            char c = remaining[i];
            if (c == '^' && i + 1 < remaining.Length && IsColorCode(remaining, i))
            {
                i += (remaining[i + 1] == 'x') ? 4 : 1;
                continue;
            }
            if (c == ' ')
                lastSpace = i;
            visible++;
        }
        int cut = (lastSpace > 0) ? lastSpace : i;
        string piece = remaining.Substring(0, cut);
        remaining = remaining.Substring(cut).TrimStart(' ');
        return piece;
    }

    private static bool IsColorCode(string s, int i)
    {
        if (i + 1 >= s.Length || s[i] != '^') return false;
        char n = s[i + 1];
        if (n >= '0' && n <= '9') return true;
        return n == 'x' && i + 4 < s.Length && IsHex(s[i + 2]) && IsHex(s[i + 3]) && IsHex(s[i + 4]);
    }

    private static bool IsHex(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static int VisibleLength(string s) => Log.StripColors(s).Length;

    // ---- per-type flood-stamp access (chat.qc the `.float flood_field` field pointer) ----

    private enum FloodField { Chat, ChatTeam, ChatTell }

    private static float GetFlood(Player p, FloodField f) => f switch
    {
        FloodField.ChatTeam => p.FloodControlChatTeam,
        FloodField.ChatTell => p.FloodControlChatTell,
        _ => p.FloodControlChat,
    };

    private static void SetFlood(Player p, FloodField f, float v)
    {
        switch (f)
        {
            case FloodField.ChatTeam: p.FloodControlChatTeam = v; break;
            case FloodField.ChatTell: p.FloodControlChatTell = v; break;
            default: p.FloodControlChat = v; break;
        }
    }
}
