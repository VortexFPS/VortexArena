// Port of Base/.../qcsrc/common/notifications/all.qh (msg_choice_choices, ReplicateVars, NOTIF_CHOICE_MAX)
//      + all.qc Send_Notification_Core's RECURSE_FROM_CHOICE (the per-client A/B selection).
//
// MSG_CHOICE lets each client pick between two phrasings of a notification (the classic verbose vs terse
// kill messages). In QC every choice notification gets a stable nent_choice_idx (0..NOTIF_CHOICE_MAX-1);
// each client carries an int array .msg_choice_choices[NOTIF_CHOICE_MAX] holding that client's selection
// per index (1 = option A, 2 = option B, 0 = suppress). The CLIENT populates it from its
// notification_CHOICE_* cvars via ReplicateVars (REPLICATE_SIMPLE), and the SERVER reads
// CS(ent).msg_choice_choices[idx] to pick which sub-notification to send to that client
// (Send_Notification_Core: case 1 -> optiona, case 2 -> optionb, default -> skip — but only when the
// server allows the choice: nent_challow_var && (warmup_stage || nent_challow_var == 2)).
//
// This C# port models that per-client array (NotificationChoiceState) and the cvar->array replication
// (ReplicateVars). It is consulted by the existing NotificationSystem MSG_CHOICE dispatch, which keys its
// per-client ChoiceValues map by the choice notification's RegistryName.

using System.Collections.Generic;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Per-client MSG_CHOICE selections — the C# successor to QC's <c>.msg_choice_choices[NOTIF_CHOICE_MAX]</c>
/// (notifications/all.qh:720). Holds one value per <see cref="Notification.ChoiceIdx"/>: 1 = option A
/// (the terse default), 2 = option B (verbose), 0 = suppress. The client fills it from its
/// <c>notification_CHOICE_*</c> cvars via <see cref="ReplicateFromCvars"/> (QC <c>ReplicateVars</c>); the
/// server reads it to decide which sub-notification to send to that client.
/// </summary>
public sealed class NotificationChoiceState
{
    /// <summary>QC <c>NOTIF_CHOICE_MAX</c> (notifications/all.qh:715): the per-client choice-array size. Upstream
    /// is 50; the port keeps that value because — unlike QC's MULTITEAM_CHOICE families that share ONE index —
    /// this port flattens each team-color choice into its own registered notification with its own index, so the
    /// distinct-choice count (currently 33) needs the full upstream headroom rather than QC's compressed total.</summary>
    public const int NotifChoiceMax = 50;

    /// <summary>QC choice value: send option A (the terse/default phrasing). Matches the <c>case 1</c> branch.</summary>
    public const int OptionA = 1;

    /// <summary>QC choice value: send option B (the verbose phrasing). Matches the <c>case 2</c> branch.</summary>
    public const int OptionB = 2;

    /// <summary>QC choice value: suppress the notification entirely (the <c>default:</c> branch).</summary>
    public const int Suppress = 0;

    // QC: .int msg_choice_choices[NOTIF_CHOICE_MAX]; (zero-initialized per client).
    private readonly int[] _choices = new int[NotifChoiceMax];

    /// <summary>
    /// The client's selection for a choice index (QC <c>msg_choice_choices[idx]</c>): 1=A, 2=B, 0=suppress.
    /// Out-of-range indices return <see cref="Suppress"/> (a defensive parity for the QC fixed array).
    /// </summary>
    public int Get(int choiceIdx)
        => (uint)choiceIdx < NotifChoiceMax ? _choices[choiceIdx] : Suppress;

    /// <summary>Set the client's selection for a choice index. No-op for an out-of-range index.</summary>
    public void Set(int choiceIdx, int value)
    {
        if ((uint)choiceIdx < NotifChoiceMax)
            _choices[choiceIdx] = value;
    }

    /// <summary>Reset every choice to <see cref="Suppress"/> (the zero-initialized client default).</summary>
    public void Clear()
    {
        for (int i = 0; i < _choices.Length; i++)
            _choices[i] = Suppress;
    }

    /// <summary>
    /// Seed every choice slot with <paramref name="value"/> (default <see cref="OptionA"/>, the terse phrasing).
    /// The SERVER uses this to initialize a fresh per-client state so a choice the client never replicated takes
    /// the QC cfg default (the <c>notification_CHOICE_*</c> cvars ship 1) rather than the zero-init
    /// <see cref="Suppress"/>: QC's client always replicates the cfg-seeded default, but this port's client skips
    /// pushing unset/unchanged choice cvars (ClientNet.PushReplicatedSet drops a "" read), so the server must
    /// supply the default QC would have sent. A later explicit <see cref="Set"/> overrides it — including an
    /// explicit 0 (the user disabling that centerprint).
    /// </summary>
    public void FillDefault(int value = OptionA)
    {
        for (int i = 0; i < _choices.Length; i++)
            _choices[i] = value;
    }

    /// <summary>
    /// QC <c>ReplicateVars</c> (notifications/all.qh:884): for every MSG_CHOICE notification (one per
    /// <see cref="Notification.ChoiceIdx"/> — team variants share an idx), read the client's
    /// <c>notification_CHOICE_&lt;cvarname&gt;</c> cvar and store it in the per-index array. The supplied
    /// <paramref name="cvar"/> resolves a cvar name to its current int value for this client (e.g. the
    /// per-client cvar table on the server, or the local cvars on the client). Unset cvars take
    /// <paramref name="defaultValue"/> (QC ACVNN default — terse / option A).
    /// </summary>
    public void ReplicateFromCvars(System.Func<string, int?> cvar, int defaultValue = OptionA)
    {
        foreach (var n in Notifications.All)
        {
            if (n.Type != MsgType.Choice)
                continue;
            // QC: FOREACH(Notifications, ... && (!it.nent_teamnum || it.nent_teamnum == NUM_TEAM_1)) — a team
            // choice is registered once per team but shares ONE nent_choice_idx; replicate it only from the
            // canonical (non-team / team-1) variant so the four team rows don't fight over the same slot.
            if (n.TeamNum != 0 && n.TeamNum != Teams.Red)
                continue;

            string cvarName = "notification_" + ChoiceCvarName(n);
            int value = cvar(cvarName) ?? defaultValue;
            Set(n.ChoiceIdx, value);
        }
    }

    /// <summary>
    /// QC <c>Get_Notif_CvarName</c> (notifications/all.qh:732): a team notification's cvar name strips the
    /// team-color suffix (e.g. "DEATH_TEAMKILL_RED" -> "DEATH_TEAMKILL") so all four team rows share one
    /// cvar; a non-team notification uses its bare name verbatim.
    /// </summary>
    public static string ChoiceCvarName(Notification n)
    {
        if (n.TeamNum == 0 || string.IsNullOrEmpty(n.TeamColorSuffix))
            return n.Name;
        // strip "_<COLOR>" (QC: substring(name, 0, -strlen(colorname) - 2)).
        string suffix = "_" + n.TeamColorSuffix;
        return n.Name.EndsWith(suffix, System.StringComparison.Ordinal)
            ? n.Name[..^suffix.Length]
            : n.Name;
    }

    /// <summary>
    /// Project this per-client state onto a <c>RegistryName -&gt; value</c> map keyed the way the
    /// <see cref="NotificationSystem"/> MSG_CHOICE dispatch consults it (its <c>ChoiceValues</c> dictionary).
    /// For each MSG_CHOICE notification, the value at its <see cref="Notification.ChoiceIdx"/> is recorded
    /// under the notification's typed <see cref="Notification.RegistryName"/>. Lets the host hand the active
    /// client's selections to the dispatcher without coupling the dispatcher to this type.
    /// </summary>
    public void ApplyTo(IDictionary<string, int> choiceValuesByName)
    {
        foreach (var n in Notifications.All)
        {
            if (n.Type != MsgType.Choice)
                continue;
            choiceValuesByName[n.RegistryName] = Get(n.ChoiceIdx);
        }
    }
}

/// <summary>
/// Assigns the stable per-choice index (QC <c>nent_choice_idx</c> / <c>nent_choice_count</c>) to every
/// MSG_CHOICE notification — the C# successor to the counting done inside the QC <c>MSG_CHOICE_NOTIF_</c>
/// macro (notifications/all.qh:851-864). A non-team choice consumes one index; a four-team choice family
/// (the MULTITEAM_CHOICE expansion) shares ONE index across its RED/BLUE/YELLOW/PINK rows, advancing the
/// counter only on the PINK (NUM_TEAM_4) row — exactly as QC does.
/// </summary>
public static class NotificationChoiceIndexer
{
    /// <summary>
    /// Walk the registered notifications in order and assign <see cref="Notification.ChoiceIdx"/>. Idempotent
    /// (re-running yields the same indices). Returns the total distinct choice count (QC
    /// <c>nent_choice_count</c>), which must not exceed <see cref="NotificationChoiceState.NotifChoiceMax"/>.
    /// </summary>
    public static int AssignChoiceIndices()
    {
        int count = 0;
        foreach (var n in Notifications.All)
        {
            if (n.Type != MsgType.Choice)
                continue;
            // QC: this.nent_choice_idx = nent_choice_count; then ++nent_choice_count only when the row is the
            // canonical (non-team) or the LAST team (NUM_TEAM_4) variant. Non-team -> teamnum 0 -> advance.
            n.ChoiceIdx = count;
            if (n.TeamNum == 0 || n.TeamNum == Teams.Pink)
                ++count;
        }
        return count;
    }
}
