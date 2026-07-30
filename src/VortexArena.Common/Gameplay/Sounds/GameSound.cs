using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

// Port of QuakeC's sound registry (common/sounds/sound.qh, all.inc, all.qh).
//
// In QC a sound is a registered Object: `CLASS(Sound)` with a `sound_str` path, enrolled via the
// `SOUND(name, path)` macro into the `Sounds` registry (REGISTRY(Sounds, BITS(9))). At precache time
// `_Sound_fixpath` resolves the bare path to an actual file (.wav on the server, the first existing of
// .wav/.ogg/.flac on the client). The constant `SND_<name>` then refers to that registered descriptor.
//
// Here a GameSound is the C# successor: one immutable descriptor per sound, enrolled into the self-
// registering `Sounds` catalog (NOT the attribute/reflection bootstrap — see SoundsList.RegisterAll).
// The descriptor carries the bare sample path, a default channel, and default volume/attenuation so a
// caller can `Api.Sound.Play(...)` (or use SoundSystem) without re-deriving those each time.

/// <summary>
/// Default mix channel for a <see cref="GameSound"/>. Mirrors the meaning of QuakeC's CH_* constants
/// (sounds/sound.qh) collapsed onto the engine facade's <see cref="SoundChannel"/>. QC distinguishes the
/// positive "single" channels (CH_*_SINGLE — one slot per entity; a new play replaces the previous sound
/// on that channel) from the negative "autochannel" variants (a fresh transient emitter per play, so
/// overlapping plays stack). That distinction is preserved via <see cref="GameSound.Single"/>; the channel
/// *family* is captured here.
/// </summary>
public enum SoundChannelHint
{
    /// <summary>CH_INFO / CH_AMBIENT — non-positional or announcer-style; maps to <see cref="SoundChannel.Auto"/>.</summary>
    Info = 0,
    /// <summary>CH_WEAPON_* / CH_SHOTS — weapon fire/impact; maps to <see cref="SoundChannel.Weapon"/>.</summary>
    Weapon = 1,
    /// <summary>CH_VOICE — player voice / taunts; maps to <see cref="SoundChannel.Voice"/>.</summary>
    Voice = 2,
    /// <summary>CH_TRIGGER — item pickups, world triggers; maps to <see cref="SoundChannel.Item"/>.</summary>
    Item = 3,
    /// <summary>CH_PLAYER / CH_PAIN — body sounds (pain, footsteps, gibs); maps to <see cref="SoundChannel.Body"/>.</summary>
    Body = 4,
}

/// <summary>
/// Standard mix levels/attenuations from QuakeC (sounds/sound.qh). VOL_BASE etc. are reproduced so the
/// ported sound tables read identically to the QC source.
/// </summary>
public static class SoundLevels
{
    // attenuation: 0 = audible everywhere, larger = falls off faster with distance
    public const float AttenNone = 0f;
    public const float AttenMin = 0.015625f;
    public const float AttenLow = 0.2f;
    public const float AttenNorm = 0.5f;
    public const float AttenLarge = 1f;
    public const float AttenIdle = 2f;
    public const float AttenStatic = 3f;
    public const float AttenMax = 3.984375f;

    // volume
    public const float VolBase = 0.7f;
    public const float VolBaseVoice = 1.0f;
    public const float VolMuffled = 0.35f;
}

/// <summary>
/// A single registered sound — the C# successor to QuakeC's <c>CLASS(Sound)</c> + <c>SOUND(name, path)</c>
/// (common/sounds/sound.qh, all.inc). One immutable instance per sound effect, enrolled into the
/// <see cref="Sounds"/> catalog. <see cref="RegistryName"/> (== <see cref="NetName"/>) gives deterministic
/// CL/SV ordering via <see cref="Registry{T}"/>, the analogue of QC's registry hash handshake.
/// </summary>
public sealed class GameSound : IRegistered
{
    public int RegistryId { get; set; }

    /// <summary>Stable identifier (QC <c>SND_&lt;name&gt;</c>), e.g. "ROCKET_IMPACT". Used for ordering + lookup.</summary>
    public string NetName { get; }

    /// <summary>
    /// Bare sample path relative to <c>sound/</c>, without extension — exactly QC's <c>sound_str</c>
    /// (e.g. "weapons/rocket_impact", "misc/talk", "announcer/default/begin"). The audio backend resolves
    /// the real file (.wav/.ogg/.flac) at load time, as QC's <c>_Sound_fixpath</c> does.
    /// </summary>
    public string Sample { get; }

    /// <summary>Default channel hint (QC CH_* at the call site). Callers may override per play.</summary>
    public SoundChannelHint Channel { get; }

    /// <summary>Default volume (QC VOL_* at the call site).</summary>
    public float Volume { get; }

    /// <summary>Default attenuation (QC ATTEN_* at the call site).</summary>
    public float Attenuation { get; }

    /// <summary>
    /// True if this sound plays on a "single" channel (QC CH_*_SINGLE, a positive channel number): a new
    /// play replaces any sound already playing on that channel for the same emitter. False = autochannel
    /// (QC negative channel): each play spawns its own transient emitter so plays overlap. The audio
    /// backend uses this to decide whether to cut the previous sound.
    /// </summary>
    public bool Single { get; }

    public string RegistryName => NetName;

    public GameSound(
        string netName,
        string sample,
        SoundChannelHint channel = SoundChannelHint.Info,
        float volume = SoundLevels.VolBase,
        float attenuation = SoundLevels.AttenNorm,
        bool single = false)
    {
        NetName = netName;
        Sample = sample;
        Channel = channel;
        Volume = volume;
        Attenuation = attenuation;
        Single = single;
    }

    /// <summary>
    /// Maps the gameplay-side channel hint onto the engine facade's <see cref="SoundChannel"/>, honoring
    /// <see cref="Single"/> for EVERY family: a non-single sound resolves to its NEGATIVE (auto) channel so
    /// overlapping plays STACK, exactly as the stock Xonotic call sites pass CH_WEAPON_A/CH_VOICE/CH_TRIGGER/
    /// CH_PLAYER (sound.qh — all auto). Previously Voice/Item/Body were forced onto positive (single) channels
    /// regardless of <see cref="Single"/>, so consecutive footsteps, gibs, impacts and pickups CANCELLED each
    /// other (a new play stopped the previous one on the same (entity, channel) key). Continuous sources that
    /// must not stack set <see cref="Single"/> = true (or use the dedicated loop path).
    /// </summary>
    public SoundChannel EngineChannel => Channel switch
    {
        SoundChannelHint.Weapon => Single ? SoundChannel.WeaponSingle : SoundChannel.WeaponAuto,  //  1 / -1
        SoundChannelHint.Voice => Single ? SoundChannel.Voice : SoundChannel.VoiceAuto,            //  2 / -2
        SoundChannelHint.Item => Single ? SoundChannel.Item : SoundChannel.TriggerAuto,            //  3 / -3
        SoundChannelHint.Body => Single ? SoundChannel.Player : SoundChannel.PlayerAuto,           //  7 / -7
        _ => SoundChannel.Auto,
    };

    public override string ToString() => $"SND_{NetName} ({Sample})";
}

/// <summary>
/// The sound catalog — the FOREACH(Sounds, …) target, successor to QC's <c>REGISTRY(Sounds, …)</c>.
/// Self-registering: gameplay seeds it by calling <see cref="RegisterAll"/> at boot (the lead wires that
/// into GameInit), NOT via the attribute/reflection bootstrap used for weapons/items/etc. The actual
/// table of sounds lives in <see cref="SoundsList"/>.
/// </summary>
public static class Sounds
{
    private static bool _done;

    public static IReadOnlyList<GameSound> All => Registry<GameSound>.All;
    public static int Count => Registry<GameSound>.Count;

    // ---- player-sound / voice-message catalog metadata (globalsound.qh) ----

    /// <summary>The default model sound directory (QC: the fallback when a model has no .sounds pack).</summary>
    public const string DefaultPlayerSoundDir = "sound/player/default.sounds";

    /// <summary>Player body-sound ids (QC REGISTER_PLAYERSOUND). Files live under a per-model dir at runtime.</summary>
    public static readonly string[] PlayerSoundIds =
    {
        "death", "drown", "fall", "falling", "gasp", "jump", "pain100", "pain25", "pain50", "pain75",
    };

    /// <summary>Voice-message ids (QC REGISTER_VOICEMSG): team radio + taunts (the "listed" set plus extras).</summary>
    public static readonly string[] VoiceMessageIds =
    {
        // team-radio (listed in the comms wheel)
        "attack", "attackinfive", "coverme", "defend", "freelance", "incoming",
        "meet", "needhelp", "seenflag", "taunt", "teamshoot",
        // not listed in the wheel but registered (some models ship these)
        "flagcarriertakingdamage", "getflag",
        // default-pack only
        "affirmative", "attacking", "defending", "roaming", "onmyway", "droppedflag",
        "negative", "seenenemy",
    };

    /// <summary>
    /// Host-installed resolver for a player-sound sample. QC's <c>LoadPlayerSounds</c> parses a model's
    /// <c>.sounds</c> manifest (id → real sample path, e.g. <c>jump → sound/player/espeak/player/jump</c>) and
    /// the <c>default.sounds</c> fallback. The headless gameplay layer can't read files, so the host (which
    /// owns the VFS + the <c>ModelSounds</c> parser) installs this delegate at boot. It receives the model's
    /// <c>.sounds</c> datafile vpath (QC <c>get_model_datafilename(model, skin, "sounds")</c>, may be
    /// null/empty for the default pack) and the player-sound <paramref name="id"/>, and returns the resolved
    /// sample path, or null to fall back to <see cref="PlayerSoundSampleRaw"/>. Unset (tests / headless) =&gt;
    /// the raw default-pack concat (which doesn't resolve on disk, exactly the pre-host behavior).
    /// </summary>
    public static Func<string?, string, string?>? ModelSoundResolver;

    /// <summary>
    /// Resolve a per-model player sound sample path. Routes through the host-installed
    /// <see cref="ModelSoundResolver"/> (which parses the model's <c>.sounds</c> manifest, QC
    /// <c>LoadPlayerSounds</c>) when present; otherwise falls back to <see cref="PlayerSoundSampleRaw"/>.
    /// <paramref name="modelSoundsFile"/> is the model's <c>.sounds</c> datafile vpath
    /// (QC <c>get_model_datafilename</c>); null/empty selects the default pack. <paramref name="id"/> is a
    /// PlayerSound/VoiceMessage id.
    /// </summary>
    public static string PlayerSoundSample(string? modelSoundsFile, string id)
        => ModelSoundResolver?.Invoke(modelSoundsFile, id) ?? PlayerSoundSampleRaw(modelSoundsFile, id);

    /// <summary>
    /// The pre-manifest string-concat fallback: <c>sound/&lt;modeldir&gt;/&lt;id&gt;</c> (or the default pack
    /// dir). Used when no <see cref="ModelSoundResolver"/> is installed, or it declines to resolve.
    /// </summary>
    public static string PlayerSoundSampleRaw(string? modelDir, string id)
        => string.IsNullOrEmpty(modelDir) ? $"{DefaultPlayerSoundDir}/{id}" : $"sound/{modelDir}/{id}";

    /// <summary>
    /// QC <c>get_model_datafilename(model, skin, "sounds")</c>: the per-model <c>.sounds</c> manifest vpath,
    /// e.g. <c>models/player/erebus.iqm</c> + skin 0 =&gt; <c>models/player/erebus.iqm_0.sounds</c>. Returns
    /// null for an empty model (=&gt; the default pack).
    /// </summary>
    public static string? ModelSoundsFile(string? model, int skin)
        => string.IsNullOrEmpty(model) ? null : $"{model}_{skin}.sounds";

    /// <summary>Look up a sound by its <see cref="GameSound.NetName"/> (QC SND_&lt;name&gt;). Null if absent.</summary>
    public static GameSound? ByName(string name) => Registry<GameSound>.ByName(name);

    public static GameSound ById(int id) => Registry<GameSound>.ById(id);

    /// <summary>Content hash over names, for the CL/SV agreement check (QC registry handshake).</summary>
    public static uint Hash => Registry<GameSound>.ContentHash();

    /// <summary>Enroll a descriptor (idempotent by name). Used by <see cref="SoundsList"/>.</summary>
    public static GameSound Register(GameSound sound)
    {
        Registry<GameSound>.Register(sound);
        return sound;
    }

    /// <summary>
    /// Populate the catalog with the representative sound set and fix deterministic ordering. Idempotent:
    /// safe to call more than once. The lead calls this once from GameInit. Analogue of QC's STATIC_INIT
    /// plus REGISTRY_SORT for the Sounds registry.
    /// </summary>
    public static void RegisterAll()
    {
        if (_done) return;
        _done = true;
        SoundsList.RegisterAll();
        Registry<GameSound>.Sort();
    }

    /// <summary>Reset (test support).</summary>
    public static void Reset()
    {
        _done = false;
        ModelSoundResolver = null;
        Registry<GameSound>.Clear();
    }
}
