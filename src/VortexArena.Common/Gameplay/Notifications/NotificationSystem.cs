// Server-side notification dispatch — the C# successor to QuakeC's Send_Notification
// (common/notifications/all.qc). Game code (damage/obituaries, CTF, items, gametype round logic) calls
// Send(...) with a broadcast mode, an optional target client, the notification, and its args. We validate
// the arg count against the notification's declared StringCount/FloatCount (exactly like Send_Notification
// errors on a mismatch), format the message, fan MSG_MULTI out to its sub-notifications, and hand the
// result to a swappable sink. The default sink records dispatches in memory; the host swaps in a
// networking sink (the ENT_CLIENT_NOTIFICATION protocol) once the client wiring exists.
//
// Token expansion: like QC's NOTIF_ARGUMENT_LIST (notifications/all.qh), the notification's declared
// Args string names one token per template "%s" slot (e.g. "s1 s2loc spree_lost"). NotifTokens resolves
// each token to its display string from the supplied s1..s4 / f1..f4 args (location suffixes, kill-spree
// phrases, weapon/buff names, team names, frag stats, …) and those strings are sprintf'd into the template
// in order. MSG_CHOICE is dispatched here too (picks optiona/optionb by the per-client choice value).
//
// Out of scope (client-side / deferred): centerprint placement & queueing, the announcer sound queue, and
// the ENT_CLIENT_NOTIFICATION wire protocol. Gentle-mode selection is supported via GentleMode.

using System.Globalization;
using System.Text;
using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

/// <summary>Who a notification goes to — the C# successor to QC's <c>ENUMCLASS(NOTIF)</c> (notifications/all.qh).</summary>
public enum NotifBroadcast
{
    /// <summary>Send to one client and their spectators. QC NOTIF_ONE.</summary>
    One = 0,
    /// <summary>Send ONLY to one client (no spectators). QC NOTIF_ONE_ONLY.</summary>
    OneOnly = 1,
    /// <summary>Send only to one team and their spectators. QC NOTIF_TEAM.</summary>
    Team = 2,
    /// <summary>Send to one team and their spectators, except one person. QC NOTIF_TEAM_EXCEPT.</summary>
    TeamExcept = 3,
    /// <summary>Send to everyone. QC NOTIF_ALL.</summary>
    All = 4,
    /// <summary>Send to everyone except one person and their spectators. QC NOTIF_ALL_EXCEPT.</summary>
    AllExcept = 5,
}

/// <summary>
/// A formatted notification ready to deliver — what the networking layer would push to clients.
/// Records the resolved notification, the broadcast/target, the formatted message text (for INFO/CENTER),
/// the announcer sound (for ANNCE), and the raw args (so a real network sink can re-encode them).
/// </summary>
public readonly struct NotificationDispatch
{
    public readonly Notification Notification;
    public readonly NotifBroadcast Broadcast;
    public readonly Entity? Target;

    /// <summary>The formatted message (INFO/CENTER), or the announcer sound name (ANNCE), or "" (MULTI fans out).</summary>
    public readonly string Text;

    public readonly string[] StringArgs;
    public readonly float[] FloatArgs;

    /// <summary>
    /// The message family carried on the wire. Normally <see cref="Notification"/>'s own
    /// <see cref="Notification.Type"/>, but a <see cref="MsgType.CenterKill"/> retraction overrides it (the
    /// dispatch then borrows an arbitrary registered notification as a carrier — see
    /// <see cref="NotificationSystem.SendCenterKill"/> — and the real intent is in <see cref="WireType"/>).
    /// </summary>
    public readonly MsgType WireType;

    public NotificationDispatch(Notification notification, NotifBroadcast broadcast, Entity? target,
        string text, string[] stringArgs, float[] floatArgs, MsgType? wireType = null)
    {
        Notification = notification;
        Broadcast = broadcast;
        Target = target;
        Text = text;
        StringArgs = stringArgs;
        FloatArgs = floatArgs;
        WireType = wireType ?? notification.Type;
    }

    public override string ToString()
        => $"[{Broadcast}] {Notification.RegistryName}: {Text}";
}

/// <summary>
/// Receives notification dispatches. The default <see cref="NotificationSystem.Recorder"/> buffers them;
/// the host swaps in a networking sink once client wiring exists. (Analogue of Net_Write_Notification.)
/// </summary>
public interface INotificationSink
{
    void Dispatch(in NotificationDispatch dispatch);
}

/// <summary>
/// The ambient notification entry point — the C# stand-in for QC's <c>Send_Notification</c>. Server gameplay
/// calls <see cref="Send(NotifBroadcast, Entity?, MsgType, string, object[])"/>; the active <see cref="Sink"/>
/// decides what happens. Errors (unknown name, wrong type, arg-count mismatch) are surfaced via
/// <see cref="LastError"/> and a no-op dispatch, mirroring how Send_Notification logs and bails.
/// </summary>
public static class NotificationSystem
{
    /// <summary>Buffers dispatches in memory; useful for the headless server and tests until networking lands.</summary>
    public sealed class RecordingSink : INotificationSink
    {
        private readonly List<NotificationDispatch> _log = new();
        public IReadOnlyList<NotificationDispatch> Log => _log;
        public int Count => _log.Count;
        public void Dispatch(in NotificationDispatch dispatch) => _log.Add(dispatch);
        public void Clear() => _log.Clear();
        public NotificationDispatch Last => _log[^1];
    }

    public static readonly RecordingSink Recorder = new();

    /// <summary>The active sink. Defaults to <see cref="Recorder"/>; the host replaces it with a networking sink.</summary>
    public static INotificationSink Sink { get; set; } = Recorder;

    /// <summary>If true, gentle-mode message variants are preferred when present (QC normal_or_gentle / cl_gentle).</summary>
    public static bool GentleMode { get; set; }

    /// <summary>Last error from a failed Send (arg mismatch, unknown notification, …). Cleared on each successful Send.</summary>
    public static string? LastError { get; private set; }

    // =====================================================================================
    //  Send entry points (Send_Notification)
    // =====================================================================================

    /// <summary>
    /// Send a notification of <paramref name="type"/> by its bare <paramref name="name"/> (e.g. Info,
    /// "DEATH_SELF_FALL"). String args come first, then float args, mirroring the QC <c>s1..s4, f1..f4</c>
    /// ordering. Floats may be passed as any numeric type; everything else is treated as a string arg.
    /// </summary>
    public static bool Send(NotifBroadcast broadcast, Entity? target, MsgType type, string name, params object[] args)
    {
        var notif = Notifications.ByName(type, name);
        if (notif is null)
        {
            Fail($"Send_Notification: could not find {Notification.TypePrefix(type)}_{name}");
            return false;
        }
        return Send(broadcast, target, notif, args);
    }

    /// <summary>Send a resolved notification entity directly (QC Send_Notification with a Notification).</summary>
    public static bool Send(NotifBroadcast broadcast, Entity? target, Notification notif, params object[] args)
    {
        LastError = null;

        // NOTIF_ONE_ONLY to a non-client is a silent no-op in QC.
        if (broadcast == NotifBroadcast.OneOnly && target is null)
            return false;

        // Split args into strings then floats, in QC order, and validate the counts.
        if (!SplitArgs(notif, args, out string[] strs, out float[] flts, out string err))
        {
            Fail($"Argument mismatch for Send_Notification({notif.RegistryName}): {err}");
            return false;
        }

        switch (notif.Type)
        {
            case MsgType.Multi:
                DispatchMulti(broadcast, target, notif, strs, flts);
                return true;

            case MsgType.Choice:
                DispatchChoice(broadcast, target, notif, strs, flts);
                return true;

            case MsgType.Annce:
                // The announcer "text" is just the sound name; the client queues/plays it.
                Sink.Dispatch(new NotificationDispatch(notif, broadcast, target, notif.Sound, strs, flts));
                return true;

            default: // Info / Center
                string text = FormatTokens(notif, strs, flts);
                Sink.Dispatch(new NotificationDispatch(notif, broadcast, target, text, strs, flts));
                return true;
        }
    }

    /// <summary>
    /// Per-client choice value for each MSG_CHOICE notification (QC <c>msg_choice_choices[idx]</c>): 1 =
    /// option A (terse), 2 = option B (verbose), 0/default = suppress. Keyed by the choice's typed name.
    /// The host can set these from per-client cvars; the global default applies when a key is absent.
    /// </summary>
    public static readonly Dictionary<string, int> ChoiceValues = new(StringComparer.Ordinal);

    /// <summary>Default choice value used when a notification has no per-client entry in <see cref="ChoiceValues"/>.</summary>
    public static int DefaultChoiceValue { get; set; } = 1;

    /// <summary>True during warmup, governing whether <see cref="NotifAllowed.Warmup"/> choices honour the client's value.</summary>
    public static bool WarmupStage { get; set; }

    /// <summary>Convenience: broadcast an INFO line to everyone (the common kill-feed path).</summary>
    public static bool Info(string name, params object[] args) => Send(NotifBroadcast.All, null, MsgType.Info, name, args);

    /// <summary>Convenience: send a CENTER message to one client.</summary>
    public static bool Center(Entity target, string name, params object[] args)
        => Send(NotifBroadcast.One, target, MsgType.Center, name, args);

    /// <summary>Convenience: play an ANNCE sound for one client.</summary>
    public static bool Announce(Entity target, string name, params object[] args)
        => Send(NotifBroadcast.One, target, MsgType.Annce, name, args);

    /// <summary>
    /// Retract a centerprint group remotely — the C# successor to QC <c>Kill_Notification(..., MSG_CENTER_KILL,
    /// cpid)</c> (Send_Notification with MSG_CENTER_KILL, notifications/all.qc:1481). The client routes this to
    /// <c>centerprint_Kill(cpid)</c> (graceful group fade-out) or, when <paramref name="cpid"/> is empty/CPID_Null,
    /// <c>centerprint_KillAll()</c>. Used on countdown abort (warmup/intermission) and CTF line retraction.
    /// </summary>
    /// <param name="cpid">The CPID_* group to retract (e.g. "CPID_ROUND"); empty/null clears every group.</param>
    public static bool SendCenterKill(NotifBroadcast broadcast, Entity? target, string? cpid = null)
    {
        LastError = null;
        if (broadcast == NotifBroadcast.OneOnly && target is null)
            return false;

        // The wire carries only the type (CenterKill) + the cpid (in Text); the registry id is unused by the
        // client kill path, so we hang the dispatch off any always-registered center notification as a carrier.
        var carrier = Notifications.ByName(MsgType.Center, "COUNTDOWN_BEGIN")
                      ?? Notifications.ByName(MsgType.Center, "COUNTDOWN_GAMESTART");
        if (carrier is null)
            return false;

        Sink.Dispatch(new NotificationDispatch(carrier, broadcast, target,
            cpid ?? "", System.Array.Empty<string>(), System.Array.Empty<float>(), MsgType.CenterKill));
        return true;
    }

    /// <summary>
    /// Set (or clear) the centerprint gametype title — the C# successor to QC <c>centerprint_SetTitle</c> /
    /// <c>centerprint_ClearTitle</c> (client/announcer.qc Announcer_Gamestart). Pass the already-formatted
    /// title text (e.g. "^BGDeathmatch"); an empty/null title clears it. Routed through the notification
    /// channel as <see cref="MsgType.CenterTitle"/>.
    /// </summary>
    public static bool SendCenterTitle(NotifBroadcast broadcast, Entity? target, string? title)
    {
        var carrier = Notifications.ByName(MsgType.Center, "COUNTDOWN_BEGIN")
                      ?? Notifications.ByName(MsgType.Center, "COUNTDOWN_GAMESTART");
        if (carrier is null)
            return false;
        Sink.Dispatch(new NotificationDispatch(carrier, broadcast, target,
            title ?? "", System.Array.Empty<string>(), System.Array.Empty<float>(), MsgType.CenterTitle));
        return true;
    }

    /// <summary>
    /// Set the centerprint duel title — the C# successor to QC <c>centerprint_SetDuelTitle</c>
    /// (client/announcer.qc Announcer_Duel). The two duelers' names travel as string args (s1=left, s2=right).
    /// Routed through the notification channel as <see cref="MsgType.CenterDuelTitle"/>.
    /// </summary>
    public static bool SendCenterDuelTitle(NotifBroadcast broadcast, Entity? target, string left, string right)
    {
        var carrier = Notifications.ByName(MsgType.Center, "COUNTDOWN_BEGIN")
                      ?? Notifications.ByName(MsgType.Center, "COUNTDOWN_GAMESTART");
        if (carrier is null)
            return false;
        Sink.Dispatch(new NotificationDispatch(carrier, broadcast, target,
            "", new[] { left ?? "", right ?? "" }, System.Array.Empty<float>(), MsgType.CenterDuelTitle));
        return true;
    }

    /// <summary>
    /// Push a raw centerprint line to a client — the C# successor to the engine <c>centerprint(client, text)</c>
    /// builtin (QC chat /tell + team-message private centerprints, map/trigger/door/item <c>.message</c> text,
    /// target_print, MOTD). The literal text is shown via <c>centerprint_AddStandard</c> with no cpid group.
    /// Routed through the notification channel as <see cref="MsgType.CenterRaw"/>.
    /// </summary>
    public static bool SendCenterRaw(NotifBroadcast broadcast, Entity? target, string text)
    {
        LastError = null;
        if (broadcast == NotifBroadcast.OneOnly && target is null)
            return false;
        if (string.IsNullOrEmpty(text))
            return false;
        var carrier = Notifications.ByName(MsgType.Center, "COUNTDOWN_BEGIN")
                      ?? Notifications.ByName(MsgType.Center, "COUNTDOWN_GAMESTART");
        if (carrier is null)
            return false;
        Sink.Dispatch(new NotificationDispatch(carrier, broadcast, target,
            text, System.Array.Empty<string>(), System.Array.Empty<float>(), MsgType.CenterRaw));
        return true;
    }

    // =====================================================================================
    //  Internals
    // =====================================================================================

    private static void DispatchMulti(NotifBroadcast broadcast, Entity? target, Notification multi,
        string[] strs, float[] flts)
    {
        // Fan out to whichever sub-notifications are set (QC Local_Notification_WOVA on each).
        // The sub-notifs share the same arg list; each consumes the prefix it declared.
        TryDispatchSub(broadcast, target, multi.MultiAnnce, MsgType.Annce, strs, flts);
        TryDispatchSub(broadcast, target, multi.MultiInfo, MsgType.Info, strs, flts);
        TryDispatchSub(broadcast, target, multi.MultiCenter, MsgType.Center, strs, flts);
    }

    private static void TryDispatchSub(NotifBroadcast broadcast, Entity? target, string? bareName, MsgType type,
        string[] strs, float[] flts)
    {
        if (string.IsNullOrEmpty(bareName)) return;
        var sub = Notifications.ByName(type, bareName);
        if (sub is null)
        {
            // Don't fail the whole multi for a missing sub; record nothing and move on (a sub may be a TODO).
            LastError = $"MSG_MULTI sub {Notification.TypePrefix(type)}_{bareName} not registered";
            return;
        }

        // Sub-notifs may declare fewer args than the multi supplies; clamp to what they consume.
        string[] subStrs = Slice(strs, sub.StringCount);
        float[] subFlts = Slice(flts, sub.FloatCount);

        if (type == MsgType.Annce)
            Sink.Dispatch(new NotificationDispatch(sub, broadcast, target, sub.Sound, subStrs, subFlts));
        else
            Sink.Dispatch(new NotificationDispatch(sub, broadcast, target, FormatTokens(sub, subStrs, subFlts), subStrs, subFlts));
    }

    /// <summary>
    /// Resolve a MSG_CHOICE to option A or B by the per-client choice value, then dispatch that option
    /// (QC <c>case MSG_CHOICE</c>). When the server doesn't allow the choice (not warmup and not A_ALWAYS,
    /// or the client value is the default "suppress"), QC defaults to option A; a value of 2 selects B,
    /// 1 keeps A, anything else suppresses (returns without dispatching).
    /// </summary>
    private static void DispatchChoice(NotifBroadcast broadcast, Entity? target, Notification choice,
        string[] strs, float[] flts)
    {
        string? optName = choice.ChoiceOptionA; // QC: found_choice = notif.nent_optiona;
        bool allowed = choice.ChoiceAllowed != NotifAllowed.Never
            && (WarmupStage || choice.ChoiceAllowed == NotifAllowed.Always);
        if (allowed)
        {
            int value = ChoiceValues.TryGetValue(choice.RegistryName, out int v) ? v : DefaultChoiceValue;
            switch (value)
            {
                case 1: break;                                  // option A
                case 2: optName = choice.ChoiceOptionB; break;  // option B
                default: return;                                // not enabled anyway
            }
        }
        if (string.IsNullOrEmpty(optName)) return;

        var opt = Notifications.ByName(choice.ChoiceType, optName);
        if (opt is null)
        {
            LastError = $"MSG_CHOICE option {Notification.TypePrefix(choice.ChoiceType)}_{optName} not registered";
            return;
        }

        string[] subStrs = Slice(strs, opt.StringCount);
        float[] subFlts = Slice(flts, opt.FloatCount);
        if (opt.Type == MsgType.Annce)
            Sink.Dispatch(new NotificationDispatch(opt, broadcast, target, opt.Sound, subStrs, subFlts));
        else
            Sink.Dispatch(new NotificationDispatch(opt, broadcast, target, FormatTokens(opt, subStrs, subFlts), subStrs, subFlts));
    }

    private static string SelectTemplate(Notification n)
        => (GentleMode && !string.IsNullOrEmpty(n.Gentle)) ? n.Gentle : n.Normal;

    /// <summary>
    /// Split the caller's args into the string block then the float block, validating against the
    /// notification's declared counts (QC stringcount + floatcount == count check).
    /// </summary>
    private static bool SplitArgs(Notification n, object[] args, out string[] strs, out float[] flts, out string err)
    {
        strs = Array.Empty<string>();
        flts = Array.Empty<float>();
        err = "";

        int wantStr = n.StringCount, wantFlt = n.FloatCount;
        if (args.Length != wantStr + wantFlt)
        {
            err = $"stringcount({wantStr}) + floatcount({wantFlt}) != count({args.Length})";
            return false;
        }

        strs = new string[wantStr];
        flts = new float[wantFlt];
        for (int i = 0; i < wantStr; i++)
            strs[i] = args[i]?.ToString() ?? "";
        for (int i = 0; i < wantFlt; i++)
        {
            object a = args[wantStr + i];
            flts[i] = a switch
            {
                float f => f,
                double d => (float)d,
                int n2 => n2,
                long l => l,
                IConvertible c => SafeToSingle(c),
                _ => 0f,
            };
        }
        return true;
    }

    private static float SafeToSingle(IConvertible c)
    {
        try { return c.ToSingle(CultureInfo.InvariantCulture); }
        catch { return 0f; }
    }

    private static string[] Slice(string[] src, int count)
    {
        if (count >= src.Length) return src;
        var dst = new string[count];
        Array.Copy(src, dst, count);
        return dst;
    }

    private static float[] Slice(float[] src, int count)
    {
        if (count >= src.Length) return src;
        var dst = new float[count];
        Array.Copy(src, dst, count);
        return dst;
    }

    /// <summary>
    /// Format a notification by resolving its declared arg tokens (QC <c>args</c> / NOTIF_ARGUMENT_LIST)
    /// into display strings, then sprintf'ing them into the template's <c>%s</c> slots in order. This is the
    /// C# successor to QC's Local_Notification_sprintf: each token in <see cref="Notification.Args"/> yields
    /// one string, and the Nth <c>%s</c> consumes the Nth token's value.
    /// </summary>
    private static string FormatTokens(Notification n, string[] strs, float[] flts)
    {
        string template = SelectTemplate(n);
        if (string.IsNullOrEmpty(template)) return "";
        string[] values = NotifTokens.Resolve(n.Args, strs, flts, n.Normal);
        return Sprintf(template, values);
    }

    /// <summary>
    /// Minimal positional sprintf: substitutes each <c>%s</c> (and numeric specifiers, treated as a string
    /// here) with the next value from <paramref name="values"/>, left-to-right. <c>%%</c> is a literal
    /// percent. Width/precision flags (e.g. <c>%.2f</c>) are skipped. Mirrors how QC feeds pre-stringified
    /// tokens into the format.
    /// </summary>
    private static string Sprintf(string template, string[] values)
    {
        var sb = new StringBuilder(template.Length + 16);
        int vi = 0;
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if (c != '%' || i + 1 >= template.Length) { sb.Append(c); continue; }

            int j = i + 1;
            while (j < template.Length && (char.IsDigit(template[j]) || template[j] == '.')) j++;
            if (j >= template.Length) { sb.Append(c); continue; }
            char conv = template[j];

            switch (conv)
            {
                case '%': sb.Append('%'); i = j; break;
                case 's':
                case 'd':
                case 'i':
                case 'f':
                    sb.Append(vi < values.Length ? values[vi++] : "");
                    i = j;
                    break;
                default: sb.Append(c); break; // unknown specifier: emit '%' literally
            }
        }
        return sb.ToString();
    }

    private static void Fail(string message) => LastError = message;
}
