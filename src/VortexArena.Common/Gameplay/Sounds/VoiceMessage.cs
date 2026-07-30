// Port of Base/.../qcsrc/common/effects/qc/globalsound.qh (REGISTER_VOICEMSG table) +
//      common/effects/qc/globalsound.qc GetVoiceMessage.
//
// In QC a voice message is a PlayerSound flagged instanceOfVoiceMessage, carrying its sample-field name
// (m_playersoundstr / m_playersoundfld) and a routing m_playersoundvt (a VOICETYPE_*). The comms-wheel
// "listed" set (attack/coverme/…/taunt/teamshoot) is offered to the player; the rest are situational
// (flagcarriertakingdamage, affirmative, …). This file models that table so _GlobalSound routing can look
// up a message's VoiceType by its bare id, exactly as QC's GetVoiceMessage(type) does.

using System.Collections.Generic;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// One voice message — the C# successor to a QC <c>REGISTER_VOICEMSG(id, vt, listed)</c> entry
/// (globalsound.qh:71-94). Carries the bare id (QC <c>m_playersoundstr</c>), its routing
/// <see cref="VoiceType"/> (QC <c>m_playersoundvt</c>), and whether it is offered in the comms wheel
/// (QC <c>instanceOfVoiceMessage</c> "listed" flag).
/// </summary>
public sealed class VoiceMessage
{
    /// <summary>Bare message id (QC <c>m_playersoundstr</c>), e.g. "attack", "taunt".</summary>
    public string Id { get; }

    /// <summary>Routing category (QC <c>m_playersoundvt</c>).</summary>
    public VoiceType VoiceType { get; }

    /// <summary>True if shown in the comms wheel (QC <c>instanceOfVoiceMessage</c> = listed). False = situational-only.</summary>
    public bool Listed { get; }

    public VoiceMessage(string id, VoiceType voiceType, bool listed)
    {
        Id = id;
        VoiceType = voiceType;
        Listed = listed;
    }
}

/// <summary>
/// The voice-message catalog — the C# successor to the QC <c>REGISTER_VOICEMSG</c> table + the
/// <c>GetVoiceMessage(type)</c> lookup (globalsound.qh / globalsound.qc:216-220). Mirrors the QC entries
/// (ids + VOICETYPE_* + listed flag) in registration order so a routing call can resolve a message's
/// <see cref="VoiceType"/> from its bare id.
/// </summary>
public static class VoiceMessages
{
    // QC REGISTER_VOICEMSG order (globalsound.qh:71-94). Listed = the comms-wheel set; the rest are situational
    // (some models lack sounds for them) but still registered so a route can resolve them.
    private static readonly VoiceMessage[] _all =
    {
        new("attack",        VoiceType.TeamRadio, true),
        new("attackinfive",  VoiceType.TeamRadio, true),
        new("coverme",       VoiceType.TeamRadio, true),
        new("defend",        VoiceType.TeamRadio, true),
        new("freelance",     VoiceType.TeamRadio, true),
        new("incoming",      VoiceType.TeamRadio, true),
        new("meet",          VoiceType.TeamRadio, true),
        new("needhelp",      VoiceType.TeamRadio, true),
        new("seenflag",      VoiceType.TeamRadio, true),
        new("taunt",         VoiceType.Taunt,     true),
        new("teamshoot",     VoiceType.LastAttacker, true),
        // NOTE: some models lack sounds for these:
        new("flagcarriertakingdamage", VoiceType.TeamRadio, false),
        new("getflag",       VoiceType.TeamRadio, false),
        // NOTE: ALL models lack sounds for these (only available in default sounds currently):
        new("affirmative",   VoiceType.TeamRadio, false),
        new("attacking",     VoiceType.TeamRadio, false),
        new("defending",     VoiceType.TeamRadio, false),
        new("roaming",       VoiceType.TeamRadio, false),
        new("onmyway",       VoiceType.TeamRadio, false),
        new("droppedflag",   VoiceType.TeamRadio, false),
        new("negative",      VoiceType.TeamRadio, false),
        new("seenenemy",     VoiceType.TeamRadio, false),
    };

    private static readonly Dictionary<string, VoiceMessage> _byId = BuildIndex();

    private static Dictionary<string, VoiceMessage> BuildIndex()
    {
        var d = new Dictionary<string, VoiceMessage>(System.StringComparer.Ordinal);
        foreach (var v in _all)
            d[v.Id] = v;
        return d;
    }

    /// <summary>All registered voice messages in QC registration order.</summary>
    public static IReadOnlyList<VoiceMessage> All => _all;

    /// <summary>The comms-wheel "listed" subset (QC <c>instanceOfVoiceMessage == true</c>).</summary>
    public static IEnumerable<VoiceMessage> Listed
    {
        get
        {
            foreach (var v in _all)
                if (v.Listed) yield return v;
        }
    }

    /// <summary>
    /// QC <c>GetVoiceMessage(type)</c> (globalsound.qc:216): resolve a voice message by its bare id, or null
    /// if no such message is registered.
    /// </summary>
    public static VoiceMessage? ById(string id)
        => id is not null && _byId.TryGetValue(id, out var v) ? v : null;

    /// <summary>
    /// The routing <see cref="VoiceType"/> for a bare id, falling back to <see cref="VoiceType.Taunt"/> — QC's
    /// <c>_GetPlayerSoundSampleField</c> defaults an unknown voice id to <c>playersound_taunt</c>
    /// (globalsound.qc:234), so an unrecognized message routes like a taunt.
    /// </summary>
    public static VoiceType VoiceTypeOf(string id)
        => ById(id)?.VoiceType ?? VoiceType.Taunt;
}
