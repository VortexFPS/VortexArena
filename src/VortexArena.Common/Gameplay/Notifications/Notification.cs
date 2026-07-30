// Notification descriptors — the C# successor to QuakeC's REGISTER_NOTIFICATION table
// (common/notifications/all.qh macros + all.inc data). Covers the four message families the server
// emits: MSG_INFO (kill feed / console), MSG_CENTER (centerprint), MSG_ANNCE (announcer sounds),
// and MSG_MULTI (a bundle that fans out to an annce + info + center).
//
// A Notification carries its stable NAME, message type, the format string(s) it sprintf()s with the
// supplied args, and how many string/float args it expects (StringCount/FloatCount) so the send path
// can validate call sites exactly like Send_Notification does. The actual rendering (centerprint
// placement, HUD kill-notify icons, announcer queue) is client-side and out of scope here.
//
// Source of truth: Base/.../qcsrc/common/notifications/all.inc. A large slice of the ~727-entry table is
// registered in NotificationsList.cs (announcers, the full self/murder/weapon obituary families, CTF +
// MULTITEAM team expansions, item/connect/score lines, the death/CTF MULTI bundles, and the FRAG/CTF
// MSG_CHOICE notifications). MSG_CHOICE (verbose/terse option selection), gentle-mode selection, and the
// per-arg token expansion (s2loc/spree_inf/item_wepname/frag_stats/death_team/…) are implemented in
// NotificationSystem. The wire protocol (ENT_CLIENT_NOTIFICATION) remains a client-wiring concern.

using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

/// <summary>The notification message families (QC <c>ENUMCLASS(MSG)</c> in notifications/all.qh).</summary>
public enum MsgType
{
    /// <summary>Announcer sound (and optional personal cue). QC MSG_ANNCE.</summary>
    Annce = 0,
    /// <summary>"Global" information message — the kill feed / console line. QC MSG_INFO.</summary>
    Info = 1,
    /// <summary>"Personal" centerprint message. QC MSG_CENTER.</summary>
    Center = 2,
    /// <summary>Bundle that subcalls an annce and/or info and/or center notification. QC MSG_MULTI.</summary>
    Multi = 3,
    /// <summary>Picks between two sub-notifications by a per-client choice value (e.g. verbose vs terse). QC MSG_CHOICE.</summary>
    Choice = 4,
    /// <summary>
    /// Retract a centerprint group (or all groups) from the client. QC <c>MSG_CENTER_KILL</c>
    /// (common/notifications/all.qc:1372): the server-driven counterpart to <c>centerprint_Kill</c> /
    /// <c>centerprint_KillAll</c>. The dispatch carries the target CPID group in its text (empty = kill all).
    /// </summary>
    CenterKill = 5,
    /// <summary>
    /// Set the centerprint gametype title line above the countdown — the server-driven counterpart to QC
    /// <c>centerprint_SetTitle</c> (client/announcer.qc Announcer_Gamestart). The dispatch carries the title
    /// text (already "^BG"-prefixed); an empty text clears it (QC <c>centerprint_ClearTitle</c>).
    /// </summary>
    CenterTitle = 6,
    /// <summary>
    /// Set the centerprint duel title ("left vs right") — the server-driven counterpart to QC
    /// <c>centerprint_SetDuelTitle</c> (client/announcer.qc Announcer_Duel). The two player names travel in
    /// the dispatch's string args (s1=left, s2=right).
    /// </summary>
    CenterDuelTitle = 7,
    /// <summary>
    /// A raw engine <c>centerprint(client, text)</c> → panel push (QC the <c>centerprint()</c> builtin family:
    /// chat /tell + team-message private centerprints, map/trigger/door/item <c>.message</c> text, target_print,
    /// MOTD). The literal text travels in the dispatch's <see cref="NotificationDispatch.Text"/>; the client
    /// pushes it via <c>centerprint_AddStandard</c>. Has no cpid/notification registry entry.
    /// </summary>
    CenterRaw = 8,
}

/// <summary>
/// Whether a server permits a client's non-default MSG_CHOICE selection (QC <c>challow</c> A_* levels).
/// </summary>
public enum NotifAllowed
{
    /// <summary>A_NEVER — choice ignored; always optiona. QC challow 0.</summary>
    Never = 0,
    /// <summary>A_WARMUP — choice honoured only during warmup. QC challow 1.</summary>
    Warmup = 1,
    /// <summary>A_ALWAYS — choice always honoured. QC challow 2.</summary>
    Always = 2,
}

/// <summary>
/// One notification, enrolled into <see cref="Notifications"/>. Mirrors the QC notification entity:
/// a stable name, a <see cref="Type"/>, the expected arg counts, and type-specific payload.
/// <see cref="IRegistered.RegistryName"/> is the QC-style typed name (e.g. "INFO_DEATH_SELF_FALL"),
/// matching how QC keys the registry by <c>Get_Notif_Name</c>.
/// </summary>
public sealed class Notification : IRegistered
{
    public int RegistryId { get; set; }

    /// <summary>Bare name without the type prefix, e.g. "DEATH_SELF_FALL" (QC name field).</summary>
    public string Name = "";

    public MsgType Type;

    /// <summary>Whether this notification is enabled by default (QC nent_default != 0 / nent_enabled).</summary>
    public bool Enabled = true;

    /// <summary>Number of string args this notification consumes (QC nent_stringcount). Max 4.</summary>
    public int StringCount;

    /// <summary>Number of float args this notification consumes (QC nent_floatcount). Max 4.</summary>
    public int FloatCount;

    // --- MSG_INFO / MSG_CENTER payload ---
    /// <summary>The sprintf format for the normal (non-gentle) message (QC <c>normal</c>). Empty for pure annce/multi.</summary>
    public string Normal = "";
    /// <summary>The sprintf format for gentle mode (QC <c>gentle</c>); empty falls back to <see cref="Normal"/>.</summary>
    public string Gentle = "";
    /// <summary>Ordered arg tokens for the message (QC <c>args</c>, e.g. "s1 s2loc spree_lost"). Informational here.</summary>
    public string Args = "";
    /// <summary>HUD kill-notify icon name for MSG_INFO (QC <c>icon</c>); empty if none. Client-side.</summary>
    public string Icon = "";
    /// <summary>Centerprint id group for MSG_CENTER (QC CPID_*); empty = CPID_Null. Client-side grouping.</summary>
    public string Cpid = "";

    /// <summary>
    /// The raw MSG_CENTER duration/count spec (QC <c>nent_durcnt</c>, the durcnt macro arg), e.g.
    /// "item_centime 0", "1 f1", "4 0" or "" (== "0 0"). Token 0 is the display DURATION and token 1 the
    /// ^COUNT countdown count; each token is a literal number, an <c>fN</c> float-arg reference, or the
    /// <c>item_centime</c> token (== notification_item_centerprinttime = 1.5). Resolved at centerprint time
    /// (QC <c>Local_Notification_centerprint_Add</c>, notifications/all.qc:1069) — see
    /// <c>HudNotifications.ShowCenter</c>. Empty means "use the panel default duration / no countdown".
    /// </summary>
    public string Durcnt = "";

    // --- MSG_ANNCE payload ---
    /// <summary>Announcer sound filename without extension (QC <c>snd</c>), e.g. "headshot".</summary>
    public string Sound = "";
    /// <summary>Sound channel (QC <c>channel</c>; CH_INFO == 0).</summary>
    public int Channel;
    /// <summary>Sound volume (QC <c>vol</c>; VOL_BASEVOICE == 1.0).</summary>
    public float Volume = 1f;
    /// <summary>Attenuation / positioning (QC <c>position</c>; ATTEN_NONE == 0).</summary>
    public float Attenuation;
    /// <summary>
    /// Spacing between successive announcer plays in seconds (QC <c>queuetime</c>, the last MSG_ANNCE_NOTIF
    /// arg). 0 = guess from the sample length; -1 = play immediately without occupying the queue
    /// (Local_Notification_Queue_Add, notifications/all.qc:1131). Only meaningful for MSG_ANNCE.
    /// </summary>
    public float Queuetime;

    // --- MSG_MULTI references (resolved lazily by name to avoid declaration-order coupling) ---
    /// <summary>Name of the announcer sub-notification (QC anncename), or null.</summary>
    public string? MultiAnnce;
    /// <summary>Name of the info sub-notification (QC infoname), or null.</summary>
    public string? MultiInfo;
    /// <summary>Name of the center sub-notification (QC centername), or null.</summary>
    public string? MultiCenter;

    // --- MSG_CHOICE payload (resolved lazily by name) ---
    /// <summary>The message type the two choice options belong to (QC <c>chtype</c>), e.g. Center.</summary>
    public MsgType ChoiceType;
    /// <summary>Whether a non-default selection is permitted (QC <c>challow</c>).</summary>
    public NotifAllowed ChoiceAllowed;
    /// <summary>Bare name of option A — the default (QC <c>nent_optiona</c>).</summary>
    public string? ChoiceOptionA;
    /// <summary>Bare name of option B — chosen when the client's choice value is 2 (QC <c>nent_optionb</c>).</summary>
    public string? ChoiceOptionB;
    /// <summary>
    /// Stable per-choice index (QC <c>nent_choice_idx</c>): the slot in a client's
    /// <see cref="NotificationChoiceState"/> array that holds this choice's selection. Assigned by
    /// <see cref="NotificationChoiceIndexer.AssignChoiceIndices"/> after registration; team variants of one
    /// choice family share an index. Only meaningful for MSG_CHOICE.
    /// </summary>
    public int ChoiceIdx;

    // --- team-keyed notification metadata (QC nent_teamnum + the MULTITEAM color suffix) ---
    /// <summary>
    /// The team this notification belongs to (QC <c>nent_teamnum</c>, NUM_TEAM_1..4), or 0 for a non-team
    /// notification. Used by <see cref="NotificationChoiceState.ReplicateFromCvars"/> to replicate a team
    /// choice family from only its canonical row, and to derive the shared cvar name.
    /// </summary>
    public int TeamNum;

    /// <summary>
    /// The uppercase team-color suffix on this notification's name (QC the MULTITEAM token: "RED"/"BLUE"/
    /// "YELLOW"/"PINK"), or "" for a non-team notification. Stripped to form the shared choice cvar name
    /// (QC <c>Get_Notif_CvarName</c>).
    /// </summary>
    public string TeamColorSuffix = "";

    /// <summary>Total args expected, for the Send-time count check (QC stringcount + floatcount).</summary>
    public int ArgCount => StringCount + FloatCount;

    /// <summary>The QC-style typed registry name, e.g. "INFO_DEATH_SELF_FALL".</summary>
    public string RegistryName => $"{TypePrefix(Type)}_{Name}";

    public Notification() { }

    public static string TypePrefix(MsgType t) => t switch
    {
        MsgType.Annce => "ANNCE",
        MsgType.Info => "INFO",
        MsgType.Center => "CENTER",
        MsgType.Multi => "MULTI",
        MsgType.Choice => "CHOICE",
        _ => "MSG",
    };

    public override string ToString() => $"{RegistryName}({StringCount}s,{FloatCount}f)";
}

/// <summary>
/// The notification catalog — the C# successor to QC's <c>REGISTRY(Notifications)</c>. Self-registering:
/// definitions are added by <see cref="NotificationsList.RegisterAll"/>, which the lead calls once from
/// GameInit. Lookups are by the typed name (e.g. "INFO_DEATH_SELF_FALL") or by type + bare name.
/// </summary>
public static class Notifications
{
    public static IReadOnlyList<Notification> All => Registry<Notification>.All;
    public static int Count => Registry<Notification>.Count;

    /// <summary>Look up by the QC-style typed name, e.g. "CENTER_COUNTDOWN_BEGIN".</summary>
    public static Notification? ByName(string registryName) => Registry<Notification>.ByName(registryName);

    /// <summary>Look up by message type + bare name, e.g. (Info, "DEATH_SELF_FALL").</summary>
    public static Notification? ByName(MsgType type, string bareName)
        => Registry<Notification>.ByName($"{Notification.TypePrefix(type)}_{bareName}");

    public static Notification ById(int id) => Registry<Notification>.ById(id);

    /// <summary>Content hash over typed names in order — client and server must agree (QC registry handshake).</summary>
    public static uint Hash => Registry<Notification>.ContentHash();

    public static Notification Register(Notification n)
    {
        Registry<Notification>.Register(n);
        return n;
    }

    /// <summary>
    /// Populate the catalog with every implemented notification (delegates to
    /// <see cref="NotificationsList.RegisterAll"/>), then assign the stable per-choice indices used by the
    /// per-client MSG_CHOICE replication (QC <c>nent_choice_idx</c>; see
    /// <see cref="NotificationChoiceIndexer.AssignChoiceIndices"/>). The lead calls this once from GameInit.
    /// Idempotent (re-running yields the same registry + indices).
    /// </summary>
    public static void RegisterAll()
    {
        NotificationsList.RegisterAll();
        NotificationChoiceIndexer.AssignChoiceIndices();
    }

    public static void Sort() => Registry<Notification>.Sort();
    public static void Clear() => Registry<Notification>.Clear();

    // ---- builder helpers mirroring the QC MSG_*_NOTIF macros ----

    /// <summary>QC <c>MSG_INFO_NOTIF(name, default, strnum, flnum, args, hudargs, icon, normal, gentle)</c>.</summary>
    public static Notification Info(string name, int strCount, int floatCount, string args, string icon,
        string normal, string gentle = "", bool enabled = true)
        => Register(new Notification
        {
            Name = name, Type = MsgType.Info, Enabled = enabled,
            StringCount = strCount, FloatCount = floatCount,
            Args = args, Icon = icon, Normal = normal, Gentle = gentle,
        });

    /// <summary>
    /// QC <c>MSG_CENTER_NOTIF(name, default, strnum, flnum, args, cpid, durcnt, normal, gentle)</c>. The
    /// <paramref name="durcnt"/> spec (QC the durcnt macro arg) is appended LAST so the existing positional
    /// call sites keep binding; it carries the "DURATION COUNT" token pair (resolved at centerprint time).
    /// </summary>
    public static Notification Center(string name, int strCount, int floatCount, string args, string cpid,
        string normal, string gentle = "", bool enabled = true, string durcnt = "")
        => Register(new Notification
        {
            Name = name, Type = MsgType.Center, Enabled = enabled,
            StringCount = strCount, FloatCount = floatCount,
            Args = args, Cpid = cpid, Durcnt = durcnt, Normal = normal, Gentle = gentle,
        });

    /// <summary>
    /// QC <c>MSG_ANNCE_NOTIF(name, default, sound, channel, volume, position, queuetime)</c>. The
    /// <paramref name="queuetime"/> arg (QC the last macro arg) governs the announcer queue spacing and is
    /// appended LAST so the existing positional <c>enabled</c> call sites keep binding correctly.
    /// </summary>
    public static Notification Annce(string name, string sound, int channel = 0, float volume = 1f,
        float attenuation = 0f, bool enabled = true, float queuetime = 0f)
        => Register(new Notification
        {
            Name = name, Type = MsgType.Annce, Enabled = enabled,
            Sound = sound, Channel = channel, Volume = volume, Attenuation = attenuation,
            Queuetime = queuetime,
        });

    /// <summary>QC <c>MSG_MULTI_NOTIF(name, default, anncename, infoname, centername)</c> (sub-notifs referenced by bare name).</summary>
    public static Notification Multi(string name, string? annce, string? info, string? center, bool enabled = true)
        => Register(new Notification
        {
            Name = name, Type = MsgType.Multi, Enabled = enabled,
            MultiAnnce = annce, MultiInfo = info, MultiCenter = center,
        });

    /// <summary>
    /// QC <c>MSG_CHOICE_NOTIF(name, default, challow, chtype, optiona, optionb)</c>. Both options are bare
    /// names of <paramref name="optionType"/> notifications; <see cref="NotificationSystem"/> resolves and
    /// dispatches one based on the per-client choice value (subject to <paramref name="allowed"/>).
    /// </summary>
    public static Notification Choice(string name, NotifAllowed allowed, MsgType optionType,
        string optionA, string optionB, bool enabled = true)
        => Register(new Notification
        {
            Name = name, Type = MsgType.Choice, Enabled = enabled,
            ChoiceAllowed = allowed, ChoiceType = optionType,
            ChoiceOptionA = optionA, ChoiceOptionB = optionB,
        });
}
