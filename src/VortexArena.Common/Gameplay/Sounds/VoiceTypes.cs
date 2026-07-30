// Port of Base/.../qcsrc/common/effects/qc/globalsound.qh (VOICETYPE_* constants).
//
// In QC each voice/player-sound carries a "voicetype" that selects HOW _GlobalSound routes it: who hears it
// (the whole server, the team, only the last attacker, …), at what attenuation, and which gameplay gate
// (sv_taunt / sv_autotaunt / sv_gentle / per-client cl_autotaunt) applies. The numeric values match QC
// exactly (globalsound.qh:64-69) so a registered VoiceMessage's m_playersoundvt round-trips unchanged.

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The voice-routing categories — the C# successor to the QC <c>VOICETYPE_*</c> constants
/// (common/effects/qc/globalsound.qh:64-69). These drive <see cref="SoundSystem"/>'s
/// <c>_GlobalSound</c> dispatch (which recipients, attenuation, and the taunt/gentle gates). The numeric
/// values are kept identical to QC so a <see cref="VoiceMessage"/>'s <see cref="VoiceMessage.VoiceType"/>
/// matches the registered <c>m_playersoundvt</c>.
/// </summary>
public enum VoiceType
{
    /// <summary>QC VOICETYPE_PLAYERSOUND (10): broadcast to everyone at ATTEN_NORM (pain/jump/fall body sounds).</summary>
    PlayerSound = 10,

    /// <summary>QC VOICETYPE_TEAMRADIO (11): sent only to real clients on the SAME team (comms-wheel radio).</summary>
    TeamRadio = 11,

    /// <summary>QC VOICETYPE_LASTATTACKER (12): heard by the speaker's last attacker (<c>this.pusher</c>) AND the speaker.</summary>
    LastAttacker = 12,

    /// <summary>QC VOICETYPE_LASTATTACKER_ONLY (13): heard ONLY by the speaker's last attacker, not the speaker.</summary>
    LastAttackerOnly = 13,

    /// <summary>QC VOICETYPE_AUTOTAUNT (14): an automatic taunt — gated by sv_autotaunt + per-client cl_autotaunt roll.</summary>
    AutoTaunt = 14,

    /// <summary>QC VOICETYPE_TAUNT (15): a manual taunt — plays the taunt anim, gated by sv_taunt and suppressed by sv_gentle.</summary>
    Taunt = 15,
}

/// <summary>
/// Constants shared by the voice-routing dispatch — the C# successor to the QC cvar names + channel
/// constants <c>_GlobalSound</c> reads (globalsound.qc:341-465). The shipped defaults match Xonotic's
/// config (all three taunt gates default off / 0).
/// </summary>
public static class VoiceCvars
{
    /// <summary>QC <c>autocvar_sv_taunt</c> (globalsound.qh:10) — shipped 0: server allows manual taunts.</summary>
    public const string SvTaunt = "sv_taunt";

    /// <summary>QC <c>autocvar_sv_autotaunt</c> (globalsound.qh:11) — shipped 0: server allows automatic taunts.</summary>
    public const string SvAutotaunt = "sv_autotaunt";

    /// <summary>QC <c>autocvar_sv_gentle</c> — shipped 0: when ≥1, taunts (and gore) are suppressed.</summary>
    public const string SvGentle = "sv_gentle";

    /// <summary>QC <c>cl_autotaunt</c> (REPLICATE cvar_cl_autotaunt) — per-client probability threshold for autotaunts.</summary>
    public const string ClAutotaunt = "cl_autotaunt";

    /// <summary>QC <c>cl_voice_directional</c> (REPLICATE cvar_cl_voice_directional) — per-client directional-voice mode.</summary>
    public const string ClVoiceDirectional = "cl_voice_directional";

    /// <summary>QC <c>cl_voice_directional_taunt_attenuation</c> (REPLICATE) — per-client taunt attenuation when directional.</summary>
    public const string ClVoiceDirectionalTauntAttenuation = "cl_voice_directional_taunt_attenuation";
}
