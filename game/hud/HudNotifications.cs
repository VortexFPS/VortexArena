using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Gameplay;

namespace VortexArena.Game.Hud;

/// <summary>
/// The client-side notification router — the Godot successor to CSQC's <c>Local_Notification</c> /
/// <c>Net_Notification</c> dispatch (Base/.../qcsrc/client/announcer.qc + the MSG_* handlers in
/// common/notifications/all.qc). The server serializes each <see cref="NotificationDispatch"/> and the
/// client (<c>game/net/ClientNet</c>) decodes it into (notification, type, text, string-args, float-args);
/// this class is what turns that decoded event into the actual on-screen / audible result, closing the
/// "server→client notification channel" gap:
///
/// <list type="bullet">
///   <item><b>MSG_CENTER</b> → <see cref="CenterPrintPanel.Push(string,float,string,int)"/> (centerprint,
///         with the QC <c>cpid</c> replace/kill id and the live <c>^COUNT</c> countdown token).</item>
///   <item><b>MSG_INFO</b> → <see cref="NotifyPanel"/>: a kill-feed obituary when the notification carries a
///         kill-notify <see cref="Notification.Icon"/> (attacker = s2, victim = s1, like QC), or a plain
///         info line otherwise (connects/scores/item lines).</item>
///   <item><b>MSG_ANNCE</b> → the announcer voice sample (a non-positional UI sound), resolved by
///         <see cref="AnnouncerResolver"/> (host-provided, VFS-backed) or the stock
///         <c>res://sound/announcer/default/&lt;snd&gt;.ogg</c> convention.</item>
/// </list>
///
/// It is decoupled from the net layer on purpose (it takes the already-decoded fields, not a
/// <c>ClientNet</c> type), so either the real network path OR the in-process gameplay path can feed it. For
/// the single-process demo, <see cref="InstallLocalSink"/> hooks <see cref="NotificationSystem.Sink"/> so a
/// server-side <c>Send_Notification</c> in this process is mirrored straight onto the HUD (mirrors how
/// <c>ClientWorld</c> installs an <see cref="IEffectSink"/> for effects).
/// </summary>
public sealed class HudNotifications
{
    private readonly Hud _hud;

    /// <summary>Resolves an announcer sound name (QC <c>snd</c>, e.g. "headshot") to a Godot <c>res://</c> audio
    /// resource path; host-provided. Used only as the fallback when <see cref="AudioLoader"/> is unset or misses.</summary>
    public Func<string, string?>? AnnouncerResolver { get; set; }

    /// <summary>Loads an announcer sample straight to an <see cref="AudioStream"/> from the mounted asset VFS
    /// (host-set to <c>AssetLoader.LoadSound</c>). The primary path — reads <c>sound/announcer/&lt;voice&gt;/&lt;snd&gt;.ogg</c>
    /// out of the mounted content. Tried before the <see cref="AnnouncerResolver"/> <c>res://</c> fallback.</summary>
    public Func<string, AudioStream?>? AudioLoader { get; set; }

    /// <summary>The announcer voice pack (QC <c>cl_announcer</c>); art lives at <c>sound/announcer/&lt;voice&gt;/</c>.
    /// Xonotic ships only "default".</summary>
    public string AnnouncerVoice { get; set; } = "default";

    /// <summary>Master gain for announcer voices (QC <c>cl_autotaunt</c>/<c>cl_announcer</c> volume), 0..1.</summary>
    public float AnnouncerVolume { get; set; } = 1f;

    private AudioStreamPlayer? _announcer;
    private INotificationSink? _previousSink;

    // ---- Announcer queue + anti-spam (Port of Base/.../common/notifications/all.qc
    //      Local_Notification_sound / Local_Notification_Queue_Add / _Process) ----

    /// <summary>
    /// Anti-spam window in seconds — QC <c>autocvar_cl_announcer_antispam</c> (announcer.qh:4, shipped 2):
    /// if the SAME sound is requested within this interval of its previous play, it is skipped
    /// (Local_Notification_sound, all.qc:987).
    /// </summary>
    public float AntiSpamInterval { get; set; } = 2f;

    /// <summary>QC <c>NOTIF_QUEUE_MAX</c> (notifications/all.qh:383): the deepest the announcer queue grows.</summary>
    private const int MaxQueueSize = 10;

    /// <summary>One queued announcer play (QC notif_queue_entity/type/time parallel arrays, all.qh:384-386).</summary>
    private readonly struct QueuedAnnce
    {
        public QueuedAnnce(string sound, double scheduledTime) { Sound = sound; ScheduledTime = scheduledTime; }
        /// <summary>The announcer sound name to play (QC the queued notif's nent_snd).</summary>
        public string Sound { get; }
        /// <summary>Engine time this entry is scheduled to fire (QC notif_queue_time[i]).</summary>
        public double ScheduledTime { get; }
    }

    private readonly List<QueuedAnnce> _announcerQueue = new();

    /// <summary>QC <c>notif_queue_next_time</c> (all.qh:388): the time the NEXT queued play may fire.</summary>
    private double _queueNextTime;

    // QC Local_Notification_sound dedup state: the last sound played and when (prev_soundfile/prev_soundtime).
    private string _lastAnnouncerSound = "";
    private double _lastAnnouncerTime = -100.0;

    /// <summary>
    /// Optional sound-length lookup (QC <c>soundlength(AnnouncerFilename(snd))</c>) used to derive the queue
    /// spacing when a notification's queuetime is 0. Host-set; returns the sample duration in seconds, or a
    /// non-positive value if unknown (then a small default spacing is used). Mirrors QC guessing the length.
    /// </summary>
    public Func<string, float>? SoundLength { get; set; }

    /// <summary>Fallback queue spacing (seconds) when neither a notification queuetime nor a sound length is known.</summary>
    private const double DefaultQueueSpacing = 1.0;

    public HudNotifications(Hud hud) => _hud = hud ?? throw new ArgumentNullException(nameof(hud));

    // =====================================================================================
    //  Entry point — one decoded notification (from ClientNet or the local sink)
    // =====================================================================================

    /// <summary>
    /// Route one decoded notification to the HUD. <paramref name="notif"/> may be null if the registry id
    /// didn't resolve (then we fall back to <paramref name="text"/> alone). <paramref name="text"/> is the
    /// server-formatted message (INFO/CENTER) or the announcer sound name (ANNCE); <paramref name="strs"/>/
    /// <paramref name="flts"/> are the raw s1..s4 / f1..f4 args (so the client can re-derive attacker/victim
    /// and the countdown count).
    /// </summary>
    public void OnNotification(Notification? notif, MsgType type, string text, string[] strs, float[] flts)
    {
        // QC ARG_CS key-bind tokens (pass_key/nade_key/join_key): the server formatted these to a sentinel
        // because only the client knows its binds (Base resolves them CSQC-side via getcommandkey). Swap each
        // sentinel for the local key now, before the text reaches the centerprint / kill-feed panels.
        if (!string.IsNullOrEmpty(text) && text.Contains(NotifTokens.KeyBindSentinelPrefix, StringComparison.Ordinal))
            text = SubstituteKeyBinds(text);

        switch (type)
        {
            case MsgType.Center:
                ShowCenter(notif, text, flts);
                break;
            case MsgType.Info:
                ShowInfo(notif, text, strs);
                break;
            case MsgType.Annce:
                // The announcer "text" is the sound name (the server sent notif.Sound as the text). The
                // notification's queuetime governs the queue spacing (QC notif.nent_queuetime).
                PlayAnnouncer(!string.IsNullOrEmpty(text) ? text : notif?.Sound ?? "", notif?.Queuetime ?? 0f);
                break;
            case MsgType.CenterKill:
                // QC MSG_CENTER_KILL (all.qc:1372): retract a centerprint group, or all groups when the cpid is
                // empty (CPID_Null → centerprint_KillAll). The cpid travels in `text`.
                if (string.IsNullOrEmpty(text))
                    _hud.CenterPrint.ClearAll();
                else
                    _hud.CenterPrint.Kill(text);
                break;
            case MsgType.CenterTitle:
                // QC centerprint_SetTitle/_ClearTitle (announcer.qc Announcer_Gamestart): the gametype-name title
                // above the countdown. Empty text clears it.
                if (string.IsNullOrEmpty(text))
                    _hud.CenterPrint.ClearTitle();
                else
                    _hud.CenterPrint.SetTitle(text);
                break;
            case MsgType.CenterDuelTitle:
                // QC centerprint_SetDuelTitle (announcer.qc Announcer_Duel): "left vs right". Names in s1/s2.
                _hud.CenterPrint.SetDuelTitle(strs.Length > 0 ? strs[0] : "", strs.Length > 1 ? strs[1] : "");
                break;
            case MsgType.CenterRaw:
                // QC raw centerprint() builtin (chat /tell, map/trigger .message, target_print, MOTD): push the
                // literal text via centerprint_AddStandard (no cpid group).
                if (!string.IsNullOrEmpty(text))
                    _hud.CenterPrint.Add(text);
                break;
            default:
                // MSG_MULTI/MSG_CHOICE are fanned out server-side into INFO/CENTER/ANNCE before the wire, so
                // they never arrive here; ignore defensively.
                break;
        }
    }

    /// <summary>Convenience overload taking the same tuple <c>ClientNet.NotificationEvent</c> carries.</summary>
    public void OnNotification(in DecodedNotification ev)
        => OnNotification(ev.Notification, ev.Type, ev.Text, ev.StringArgs, ev.FloatArgs);

    /// <summary>
    /// Replace each QC <c>ARG_CS</c> key-bind sentinel (<see cref="NotifTokens.KeyBindSentinelPrefix"/> ..
    /// <see cref="NotifTokens.KeyBindSentinelSuffix"/>, carrying the bare token name) with the display name of
    /// the key currently bound to that command — the C# successor to QC resolving <c>pass_key</c>/<c>nade_key</c>/
    /// <c>join_key</c> CSQC-side via <c>getcommandkey</c> (notifications/all.qh). The lookup uses the client's live
    /// <see cref="VortexArena.Engine.Console.BindTable"/> (the same table NetGame reads for the respawn-jump hint);
    /// an unbound command falls back to QC's human-readable descriptive name.
    /// </summary>
    private static string SubstituteKeyBinds(string text)
    {
        int idx;
        while ((idx = text.IndexOf(NotifTokens.KeyBindSentinelPrefix, StringComparison.Ordinal)) >= 0)
        {
            int start = idx + NotifTokens.KeyBindSentinelPrefix.Length;
            int end = text.IndexOf(NotifTokens.KeyBindSentinelSuffix, start, StringComparison.Ordinal);
            if (end < 0)
                break; // malformed: leave the rest as-is rather than loop forever
            string token = text.Substring(start, end - start);
            string key = ResolveKeyBind(token);
            text = string.Concat(text.AsSpan(0, idx), key,
                text.AsSpan(end + NotifTokens.KeyBindSentinelSuffix.Length));
        }
        return text;
    }

    /// <summary>
    /// Map a key-bind token to the key bound to its command (QC <c>getcommandkey(descriptive, command)</c>).
    /// The command strings are the ones the port's bind table actually stores from <c>binds-xonotic.cfg</c>:
    /// <c>+use</c> (CTF flag-pass request / vehicle enter / onslaught teleport, key <c>f</c>), <c>weapon_drop</c>
    /// (the drop-weapon bind that primes/throws the nade, keys <c>g</c>/<c>BACKSPACE</c>) and <c>+jump</c>
    /// (spectator join, <c>SPACE</c>). When no key is bound,
    /// <see cref="VortexArena.Engine.Console.BindTable.CommandKey"/> returns the descriptive fallback (Base's
    /// <c>_("drop flag")</c>/<c>_("throw nade")</c>/<c>_("jump")</c>).
    /// </summary>
    private static string ResolveKeyBind(string token) => token switch
    {
        "pass_key" => VortexArena.Engine.Console.BindTable.CommandKey("drop flag", "+use"),
        "nade_key" => VortexArena.Engine.Console.BindTable.CommandKey("throw nade", "weapon_drop"),
        "join_key" => VortexArena.Engine.Console.BindTable.CommandKey("jump", "+jump"),
        _ => "",
    };

    /// <summary>A net-decoupled mirror of <c>ClientNet.NotificationEvent</c> the host can adapt to.</summary>
    public readonly record struct DecodedNotification(
        Notification? Notification, MsgType Type, string Text, string[] StringArgs, float[] FloatArgs);

    // =====================================================================================
    //  MSG_CENTER
    // =====================================================================================

    private void ShowCenter(Notification? notif, string text, float[] flts)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        string cpid = notif?.Cpid ?? "";

        // QC durcnt ("DURATION COUNT") is resolved at centerprint time (Local_Notification_centerprint_Add,
        // notifications/all.qc:1069): token 0 is the display duration (arg_slot[0]) and token 1 the ^COUNT
        // count (arg_slot[1]). Each token is a literal, an fN float-arg reference, or item_centime
        // (== notification_item_centerprinttime = 1.5). The notification carries the raw durcnt; we resolve it
        // here from the float args. An absent/"0 0" durcnt → duration 0 (the panel's default time) and no count.
        ResolveDurcnt(notif?.Durcnt ?? "", flts, out float duration, out int durcntCount);

        int count = -1;
        if (text.Contains(CenterPrintPanel.CountToken, StringComparison.Ordinal))
        {
            // The ^COUNT countdown number comes from the durcnt count token when present, else from f1 (the
            // standard countdown notifs encode "1 fN", so this matches; a "0 0"/absent durcnt falls back to f1).
            count = durcntCount >= 0 ? durcntCount
                  : (flts.Length > 0 ? Math.Max(0, (int)flts[0]) : -1);
        }

        _hud.CenterPrint.Push(text, duration, cpid, count);
    }

    /// <summary>QC notification_item_centerprinttime (common/notifications/all.qh:310). The display time of
    /// item-pickup centerprints; mirrored here as the resolution of the durcnt <c>item_centime</c> token.</summary>
    private const float ItemCenterprintTime = 1.5f;

    /// <summary>
    /// Resolve a MSG_CENTER durcnt ("DURATION COUNT") spec — the C# successor to the arg_slot loop in QC
    /// <c>Local_Notification_centerprint_Add</c> (notifications/all.qc:1069). Token 0 → <paramref name="duration"/>
    /// (seconds; 0/absent ⇒ the panel default time), token 1 → <paramref name="count"/> (the ^COUNT count;
    /// -1 ⇒ none). Each token is the literal <c>item_centime</c> (== notification_item_centerprinttime),
    /// an <c>fN</c> float-arg reference (1-based into <paramref name="flts"/>), or a literal number.
    /// </summary>
    private static void ResolveDurcnt(string durcnt, float[] flts, out float duration, out int count)
    {
        // QC default when durcnt is "" / "0 0": duration 0 (→ panel default time), no countdown.
        duration = 0f;
        count = -1;
        if (string.IsNullOrEmpty(durcnt))
            return;

        string[] tok = durcnt.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tok.Length > 0) duration = ResolveDurcntToken(tok[0], flts);
        if (tok.Length > 1)
        {
            float c = ResolveDurcntToken(tok[1], flts);
            count = c > 0f ? (int)c : (c == 0f ? -1 : 0); // 0 ⇒ no countdown (QC stof("0") with no ^COUNT)
        }
    }

    private static float ResolveDurcntToken(string token, float[] flts)
    {
        if (string.Equals(token, "item_centime", StringComparison.Ordinal))
        {
            // QC ftos(autocvar_notification_item_centerprinttime); read the cvar if present, else the 1.5 default.
            if (VortexArena.Common.Services.Api.Services is not null)
            {
                float v = VortexArena.Common.Services.Api.Cvars.GetFloat("notification_item_centerprinttime");
                if (v != 0f) return v;
            }
            return ItemCenterprintTime;
        }
        // fN → the (N-1)th float arg (QC's f1..f4 map to arg_slot via the same numbered tokens).
        if (token.Length >= 2 && (token[0] == 'f' || token[0] == 'F')
            && int.TryParse(token.AsSpan(1), out int idx) && idx >= 1 && idx <= flts.Length)
            return flts[idx - 1];
        // literal number (QC: default branch stof(selected)).
        return float.TryParse(token, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float lit) ? lit : 0f;
    }

    // =====================================================================================
    //  MSG_INFO  (kill feed + info lines)
    // =====================================================================================

    private void ShowInfo(Notification? notif, string text, string[] strs)
    {
        // A kill-notify icon marks an obituary (QC: MSG_INFO entries with an icon go to HUD_Notify with the
        // weapon/death pic). Murders carry s1=victim, s2=attacker (notifications/all.inc DEATH_MURDER args);
        // self-deaths carry s1=victim only.
        string icon = notif?.Icon ?? "";
        if (!string.IsNullOrEmpty(icon) && strs.Length >= 1)
        {
            bool murder = strs.Length >= 2 && !string.IsNullOrEmpty(strs[1]);
            string victim = strs[0];
            string? attacker = murder ? strs[1] : null;
            _hud.Notify.Push(attacker, victim, icon);
            return;
        }

        // Non-kill info (connects, scores, item lines): show as a plain right-aligned line (attacker-less),
        // which NotifyPanel renders as the victim slot alone. Color codes in the text are honored.
        if (!string.IsNullOrWhiteSpace(text))
            _hud.Notify.Push(null, text, "");
    }

    // =====================================================================================
    //  MSG_ANNCE  (announcer voice)
    // =====================================================================================

    /// <summary>
    /// Queue an announcer voice sample by its bare name — QC <c>Local_Notification_Queue_Add</c>
    /// (notifications/all.qc:1131). <paramref name="queueTime"/> is the notification's queuetime (QC
    /// nent_queuetime): 0 guesses the spacing from the sample length (<see cref="SoundLength"/>); -1 plays
    /// immediately without occupying a queue slot. When the queue is free (<c>time &gt; queue_next_time</c>)
    /// the sound runs now and reserves the next slot at <c>now + queue_time</c>; otherwise it is appended at
    /// the running <c>queue_next_time</c> (dropped if the queue is at <see cref="MaxQueueSize"/>).
    /// </summary>
    public void PlayAnnouncer(string soundName, float queueTime = 0f)
    {
        if (string.IsNullOrWhiteSpace(soundName) || AnnouncerVolume <= 0f)
            return;

        double now = Time.GetTicksMsec() / 1000.0;

        // QC: if (queue_time == 0) queue_time = soundlength(AnnouncerFilename(notif.nent_snd)).
        double spacing = queueTime;
        if (queueTime == 0f)
        {
            float len = SoundLength?.Invoke(soundName) ?? 0f;
            spacing = len > 0f ? len : DefaultQueueSpacing;
        }

        // QC: if (queue_time == -1 || time > notif_queue_next_time) -> run immediately, reserve next slot.
        if (queueTime == -1f || now > _queueNextTime)
        {
            QueueRun(soundName, now);
            _queueNextTime = now + (queueTime == -1f ? 0.0 : spacing);
            return;
        }

        // Otherwise enqueue at the running next-time (QC notif_queue_time[len] = notif_queue_next_time).
        if (_announcerQueue.Count >= MaxQueueSize)
            return;
        _announcerQueue.Add(new QueuedAnnce(soundName, _queueNextTime));
        _queueNextTime += spacing;
    }

    /// <summary>
    /// Advance the announcer queue — QC <c>Local_Notification_Queue_Process()</c> (notifications/all.qc:1158):
    /// if the front entry's scheduled time has arrived, run it and shift the queue left. Call once per frame
    /// (the host drives this from the HUD update tick).
    /// </summary>
    public void ProcessAnnouncerQueue()
    {
        if (_announcerQueue.Count == 0)
            return;
        double now = Time.GetTicksMsec() / 1000.0;
        // QC: if (!notif_queue_length || notif_queue_time[0] > time) return;
        if (_announcerQueue[0].ScheduledTime > now)
            return;

        QueuedAnnce front = _announcerQueue[0];
        _announcerQueue.RemoveAt(0);   // QC shift-left
        QueueRun(front.Sound, now);
    }

    /// <summary>
    /// QC <c>Local_Notification_Queue_Run</c> -&gt; <c>Local_Notification_sound</c> (all.qc:1121/985): play the
    /// sound, applying the dedup anti-spam — skip if it matches the previous sound AND falls inside the
    /// <see cref="AntiSpamInterval"/> window (QC <c>soundfile != prev_soundfile || time &gt;= prev_soundtime +
    /// antispam</c>). On a play, record it as the new prev_soundfile/prev_soundtime.
    /// </summary>
    private void QueueRun(string soundName, double now)
    {
        // QC Local_Notification_sound antispam: same file inside the window -> blocked.
        if (soundName == _lastAnnouncerSound && now < _lastAnnouncerTime + AntiSpamInterval)
            return;

        AudioStream? stream = LoadAnnouncerStream(soundName);

        // Record prev_soundfile/prev_soundtime even when the sample is missing: QC sets these whenever the
        // antispam check passes (it strcpy's before the _sound call would no-op on a bad sample).
        _lastAnnouncerSound = soundName;
        _lastAnnouncerTime = now;

        if (stream is null)
            return;

        EnsureAnnouncer();
        _announcer!.Stream = stream;
        _announcer.VolumeDb = Mathf.LinearToDb(Mathf.Clamp(AnnouncerVolume, 0.001f, 1f));
        _announcer.Play();
    }

    /// <summary>
    /// Resolve an announcer voice name to a stream: the VFS <see cref="AudioLoader"/> first
    /// (<c>sound/announcer/&lt;voice&gt;/&lt;snd&gt;.ogg</c> from the mounted content), then the
    /// <see cref="ResolveAnnouncer"/> <c>res://</c> fallback. Null = silent (missing voice/sample).
    /// </summary>
    private AudioStream? LoadAnnouncerStream(string soundName)
    {
        AudioStream? stream = AudioLoader?.Invoke($"announcer/{AnnouncerVoice}/{soundName}");
        if (stream is not null)
            return stream;

        string? resPath = ResolveAnnouncer(soundName);
        if (string.IsNullOrEmpty(resPath) || !ResourceLoader.Exists(resPath))
            return null;
        try { return ResourceLoader.Load<AudioStream>(resPath); }
        catch { return null; }
    }

    private string? ResolveAnnouncer(string soundName)
    {
        if (AnnouncerResolver is not null)
            return AnnouncerResolver(soundName);
        // Stock layout: sound/announcer/<voice>/<snd>.ogg — default voice when the host hasn't chosen one.
        return $"res://sound/announcer/default/{soundName}.ogg";
    }

    private void EnsureAnnouncer()
    {
        if (_announcer is not null && GodotObject.IsInstanceValid(_announcer))
            return;
        _announcer = new AudioStreamPlayer { Name = "Announcer", Bus = "Master" };
        _hud.AddChild(_announcer);
    }

    // =====================================================================================
    //  In-process bridge (single-process demo) — mirror NotificationSystem.Sink onto the HUD
    // =====================================================================================

    /// <summary>
    /// Install a <see cref="INotificationSink"/> that mirrors every in-process <c>Send_Notification</c> onto
    /// this HUD (chaining the previously installed sink so recording/networking still happen). Call from the
    /// host once the HUD exists; <see cref="UninstallLocalSink"/> on teardown. This is the notification
    /// analogue of <c>ClientWorld.RenderSink</c> for effects — it makes the demo show notifications without a
    /// network round-trip. A real networked client instead feeds <see cref="OnNotification"/> from ClientNet.
    /// </summary>
    public void InstallLocalSink()
    {
        if (NotificationSystem.Sink is HudSink)
            return;
        _previousSink = NotificationSystem.Sink;
        NotificationSystem.Sink = new HudSink(this, _previousSink);
    }

    /// <summary>Restore the notification sink that was active before <see cref="InstallLocalSink"/>.</summary>
    public void UninstallLocalSink()
    {
        if (NotificationSystem.Sink is HudSink hs)
            NotificationSystem.Sink = hs.Inner;
    }

    /// <summary>
    /// A notification sink that forwards each dispatch to whatever sink was previously installed (so the
    /// recorder/network sink keeps working) and then mirrors it onto the HUD. Building Godot nodes must happen
    /// on the scene thread, so HUD work is deferred via <see cref="Callable"/> (a dispatch may originate from
    /// a sim tick).
    /// </summary>
    private sealed class HudSink : INotificationSink
    {
        private readonly HudNotifications _owner;
        public INotificationSink Inner { get; }

        public HudSink(HudNotifications owner, INotificationSink inner)
        {
            _owner = owner;
            Inner = inner;
        }

        public void Dispatch(in NotificationDispatch d)
        {
            Inner.Dispatch(d);

            // Capture by value (struct copy of the immutable arrays' references — they're not mutated).
            Notification n = d.Notification;
            MsgType type = d.WireType; // normally n.Type; a CenterKill retraction overrides it
            string text = d.Text;
            string[] strs = d.StringArgs;
            float[] flts = d.FloatArgs;
            HudNotifications owner = _owner;

            Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(owner._hud))
                    owner.OnNotification(n, type, text, strs, flts);
            }).CallDeferred();
        }
    }
}
