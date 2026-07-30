using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Faithful port of QC <c>sound_allowed(to, e)</c> (common/sounds/all.qc:9-25) and
/// <c>autocvar_bot_sound_monopoly</c> (all.qc:6): the per-entity gate that every SVQC sound emit
/// goes through. Two behaviours:
/// <list type="bullet">
///   <item><b>Owner-walk re-home</b>: if the emitting entity is a <c>body</c> (corpse), it resolves
///         up through <c>enemy → realowner → owner</c> to find the originating player. Sounds attributed
///         to a corpse or projectile are thus correctly gated/checked against their real owner.</item>
///   <item><b>bot_sound_monopoly</b>: when the cvar is 1, sounds emitted by any real client are
///         suppressed — only bot-sourced sounds pass. Default 0 (off).</item>
/// </list>
/// The port omits the QC MSG_ONE/"sounds to self always pass" branch because the port's sound API does
/// not carry a per-recipient <c>msg_entity</c> context at the point of the gate check; that branch is
/// a cosmetic edge-case (a player's own self-emit) that does not affect normal play.
/// </summary>
public static class SoundAllowedGate
{
    /// <summary>QC cvar name for the bot-monopoly flag (xonotic-server.cfg:489, default 0).</summary>
    private const string BotSoundMonopolyCvar = "bot_sound_monopoly";

    /// <summary>
    /// Walk entity <paramref name="e"/> up through <c>body→enemy / realowner / owner</c> to find the
    /// true sound-emitting entity (QC <c>sound_allowed</c> owner-walk, all.qc:13-18).
    /// </summary>
    public static Entity? WalkOwner(Entity? e)
    {
        for (int guard = 0; guard < 16 && e is not null; guard++)
        {
            if (e.IsCorpse && e.Enemy is not null)
                e = e.Enemy;
            else if (e.RealOwner is { } ro && !ReferenceEquals(ro, e))
                e = ro;
            else if (e.Owner is { } ow && !ReferenceEquals(ow, e))
                e = ow;
            else
                break;
        }
        return e;
    }

    /// <summary>
    /// Returns false if the sound should be suppressed (QC <c>sound_allowed</c>, all.qc:9-25):
    /// first resolves <paramref name="emitter"/> through the owner-walk (QC <c>body→enemy / realowner /
    /// owner</c>) to find the true sound-attributing entity, then applies the <c>bot_sound_monopoly</c> gate
    /// (when the cvar is 1 and the resolved entity is a real client → deny).
    /// Null emitter always passes (QC: <c>if(!e) return true</c>).
    /// </summary>
    public static bool IsAllowed(Entity? emitter)
    {
        if (emitter is null) return true;
        if (Api.Services is null) return true; // headless / no cvar service
        if (Api.Cvars.GetFloat(BotSoundMonopolyCvar) == 0f) return true;
        // Resolve the true owner (QC sound_allowed owner-walk: body→enemy / realowner / owner).
        // This re-homes corpse/projectile sounds to the originating player so the monopoly check
        // applies to the right entity — activates WalkOwner on the general emit path.
        Entity? resolved = WalkOwner(emitter);
        // bot_sound_monopoly = 1: real clients (FL_CLIENT + not a bot) are denied.
        bool isRealClient = resolved is not null
                            && (resolved.Flags & EntFlags.Client) != 0
                            && !resolved.IsCorpse
                            && resolved is Player p && !p.IsBot;
        return !isRealClient;
    }
}

// Playback helpers for the sound registry — the C# successor to QuakeC's sound() / GlobalSound() /
// play2all() call sites (common/sounds/sound.qh, common/effects/qc/globalsound.qc). These route a
// registered GameSound (or a bare sample path) through the engine facade Api.Sound.Play.
//
// QC distinguishes:
//   sound(e, chan, samp, vol, atten)        — a positional sound emitted *on* entity e        => PlayOn
//   _GlobalSound / play2all(samp)           — a non-positional sound heard by everyone         => PlayGlobal
//   PlayerSound(this, def, …)               — a voice/body sound on a player at VOL_BASEVOICE  => PlayOn (Voice/Body)
//
// Everything here ultimately calls ISoundService.Play(entity, channel, sample, volume, attenuation).

/// <summary>
/// The random-variant sound groups — the C# successor to QC's <c>SND_*_RANDOM()</c> picker functions
/// (common/sounds/all.inc) and the GlobalSound "&lt;base&gt; &lt;count&gt;" pairs. Two kinds:
///  * <b>registered groups</b> (RIC1..3, GIB_SPLAT01..04, GRENADE_BOUNCE1..6, NEXWHOOSH1..3, FLACEXP1..3):
///    N consecutive registered <see cref="GameSound"/>s; the picker returns one of them — QC indexes by
///    <c>SND_X1.m_id + floor(prandom()*N)</c>, here by name "&lt;PREFIX&gt;&lt;k&gt;".
///  * <b>numbered-file groups</b> (STEP, STEP_METAL, FALL, FALL_METAL): one registered base sound plus a
///    count of on-disk numbered files; the played sample is "&lt;base&gt;&lt;k&gt;.wav" (QC GlobalSound_sample).
/// All selection uses <see cref="Prandom"/> so it stays deterministic / network-reproducible (ADR-0010).
/// </summary>
public static class SoundVariantGroups
{
    /// <summary>QC GlobalSound "&lt;base&gt; &lt;count&gt;" counts (REGISTER_GLOBALSOUND in globalsound.qh).</summary>
    private static readonly Dictionary<string, int> GlobalCounts = new(StringComparer.Ordinal)
    {
        ["STEP"] = 6,
        ["STEP_METAL"] = 6,
        ["FALL"] = 4,
        ["FALL_METAL"] = 4,
    };

    /// <summary>The registered-variant group prefixes and their member counts (SND_*_RANDOM groups).</summary>
    private static readonly (string Prefix, int Count, int Start)[] RegisteredGroups =
    {
        ("RIC", 3, 1),             // ric1..ric3
        ("NEXWHOOSH", 3, 1),       // nexwhoosh1..3
        ("GRENADE_BOUNCE", 6, 1),  // grenade_bounce1..6
        ("FLACEXP", 3, 1),         // hagexp1..3 registered as FLACEXP1..3
        ("GIB_SPLAT0", 4, 1),      // gib_splat01..04 registered as GIB_SPLAT01..04
    };

    /// <summary>
    /// Pick a member of a registered random group by its base name (e.g. "RIC", "GIB_SPLAT0"). Returns the
    /// chosen <see cref="GameSound"/>, or null if the group/member isn't registered. QC SND_X_RANDOM().
    /// </summary>
    public static GameSound? PickRegistered(string groupPrefix)
    {
        foreach (var (prefix, count, start) in RegisteredGroups)
        {
            if (!string.Equals(prefix, groupPrefix, StringComparison.Ordinal)) continue;
            int k = start + Prandom.RangeInt(0, count);            // QC: m_id + floor(prandom()*count)
            return Sounds.ByName($"{prefix}{k}");
        }
        return null;
    }

    // convenience accessors mirroring the QC picker names
    public static GameSound? Ric() => PickRegistered("RIC");
    public static GameSound? NexWhoosh() => PickRegistered("NEXWHOOSH");
    public static GameSound? GrenadeBounce() => PickRegistered("GRENADE_BOUNCE");
    public static GameSound? FlacExp() => PickRegistered("FLACEXP");
    public static GameSound? GibSplat() => PickRegistered("GIB_SPLAT0");

    /// <summary>The numbered-variant count for a GlobalSound base name, or 0 if it isn't a counted group.</summary>
    public static int GlobalCount(string name) => GlobalCounts.TryGetValue(name, out int n) ? n : 0;

    /// <summary>
    /// Resolve the actual sample file for a counted GlobalSound (QC GlobalSound_sample): when the sound has
    /// a variant count, "&lt;base&gt;&lt;floor(prandom()*count)+1&gt;.wav"; otherwise "&lt;base&gt;.wav".
    /// </summary>
    public static string ResolveGlobalSample(GameSound sound)
    {
        int count = GlobalCount(sound.NetName);
        if (count > 0)
            return $"{sound.Sample}{Prandom.RangeInt(0, count) + 1}.wav";
        return $"{sound.Sample}.wav";
    }
}

/// <summary>
/// Routes registered sounds to <see cref="Api.Sound"/>. Stateless apart from a lazily-spawned shared
/// emitter used by <see cref="PlayGlobal(GameSound)"/> when no explicit emitter is supplied (the analogue
/// of QC playing a global sound on the world entity).
/// </summary>
public static class SoundSystem
{
    // Shared emitter for non-positional/global sounds (QC plays these on `world`). Spawned on first use so
    // the system stays inert until something actually plays a global sound. ATTEN_NONE => heard everywhere.
    private static Entity? _globalEmitter;

    private static Entity GlobalEmitter
    {
        get
        {
            if (_globalEmitter is null || _globalEmitter.IsFreed)
            {
                _globalEmitter = Api.Entities.Spawn();
                _globalEmitter.ClassName = "sound_global_emitter";
            }
            return _globalEmitter;
        }
    }

    // ---- play on a specific entity (QC sound(e, …)) ----

    /// <summary>
    /// Play a registered sound emitted on <paramref name="emitter"/>, using the sound's default channel,
    /// volume and attenuation. Successor to QC <c>sound(e, def.chan, SND(def), def.vol, def.atten)</c>.
    /// Applies the <c>bot_sound_monopoly</c> gate (QC <c>sound_allowed(MSG_BROADCAST, e)</c>, all.qc:9-25):
    /// when <c>bot_sound_monopoly=1</c> and the emitter is a real client, the sound is suppressed.
    /// No-op if the sound is null.
    /// </summary>
    public static void PlayOn(Entity emitter, GameSound? sound)
    {
        if (sound is null) return;
        if (!SoundAllowedGate.IsAllowed(emitter)) return;
        Api.Sound.Play(emitter, sound.EngineChannel, sound.Sample, sound.Volume, sound.Attenuation);
    }

    /// <summary>Play a registered sound on <paramref name="emitter"/> with explicit volume/attenuation overrides.
    /// Applies the <c>bot_sound_monopoly</c> gate (QC <c>sound_allowed</c>).</summary>
    public static void PlayOn(Entity emitter, GameSound? sound, float volume, float attenuation)
    {
        if (sound is null) return;
        if (!SoundAllowedGate.IsAllowed(emitter)) return;
        Api.Sound.Play(emitter, sound.EngineChannel, sound.Sample, volume, attenuation);
    }

    /// <summary>Play a registered sound on <paramref name="emitter"/> overriding the channel as well.
    /// Applies the <c>bot_sound_monopoly</c> gate (QC <c>sound_allowed</c>).</summary>
    public static void PlayOn(Entity emitter, GameSound? sound, SoundChannel channel, float volume, float attenuation)
    {
        if (sound is null) return;
        if (!SoundAllowedGate.IsAllowed(emitter)) return;
        Api.Sound.Play(emitter, channel, sound.Sample, volume, attenuation);
    }

    /// <summary>Look the sound up by name (QC <c>SND_&lt;name&gt;</c>) then play it on <paramref name="emitter"/>.
    /// Applies the <c>bot_sound_monopoly</c> gate.</summary>
    public static void PlayOn(Entity emitter, string soundName) => PlayOn(emitter, Sounds.ByName(soundName));

    /// <summary>Play a raw sample path on <paramref name="emitter"/> (QC <c>sound()</c> with a string literal).
    /// Applies the <c>bot_sound_monopoly</c> gate (QC <c>sound_allowed(MSG_BROADCAST, e)</c>).</summary>
    public static void PlayRaw(
        Entity emitter,
        string sample,
        SoundChannel channel = SoundChannel.Auto,
        float volume = SoundLevels.VolBase,
        float attenuation = SoundLevels.AttenNorm)
    {
        if (!SoundAllowedGate.IsAllowed(emitter)) return;
        Api.Sound.Play(emitter, channel, sample, volume, attenuation);
    }

    // ---- play2 / play2team — per-client / per-team 2D sends (QC play2, all.qc:116-140) ----

    /// <summary>
    /// Play a 2D sound for <paramref name="recipient"/> (QC <c>play2(e, filename)</c>, all.qc:116-120).
    /// Uses CH_INFO (Auto) channel, VOL_BASE (0.7) volume, ATTEN_NONE attenuation (matching QC); ATTEN_NONE
    /// plays it centered on the listener (2D, no spatialization).
    /// <para>DIVERGENCE: QC <c>play2</c> sends to <c>MSG_ONE</c> (<c>msg_entity = e</c>) so the cue is heard by
    /// EXACTLY that one client. The port's sound layer (<see cref="ISoundService.Play"/> / SoundWire) has no
    /// per-recipient targeting — it broadcasts to every connected client — so this is exact only on a single-human
    /// listen server. A true per-client send needs a per-recipient sound channel the snapshot protocol does not
    /// yet carry; until then this is the closest faithful approximation.</para>
    /// QC <c>play2</c> does NOT apply the bot_sound_monopoly gate (sound_allowed(MSG_ONE, NULL) returns true for
    /// the NULL emitter); the gate lives only at play2team level.
    /// </summary>
    public static void Play2(Entity recipient, GameSound? sound)
    {
        if (sound is null) return;
        // QC: play2 -> soundtoat(MSG_ONE, NULL, '0 0 0', CH_INFO, filename, VOL_BASE, ATTEN_NONE, 0)
        // soundtoat calls sound_allowed(MSG_ONE, NULL) which always returns true for NULL.
        // CH_INFO = 0 (SoundChannel.Auto); VOL_BASE = 0.7 (not the sound's registered volume)
        Api.Sound.Play(recipient, SoundChannel.Auto, sound.Sample, SoundLevels.VolBase, SoundLevels.AttenNone);
    }

    /// <summary>Play a 2D sound to one specific client by sound name (QC <c>play2(e, SND_name)</c>).</summary>
    public static void Play2(Entity recipient, string soundName)
        => Play2(recipient, Sounds.ByName(soundName));

    /// <summary>Play a raw 2D sample to one specific client (QC <c>play2(e, sample_path)</c>).</summary>
    public static void Play2Raw(Entity recipient, string sample, float volume = SoundLevels.VolBase)
        => Api.Sound.Play(recipient, SoundChannel.Auto, sample, volume, SoundLevels.AttenNone);

    /// <summary>
    /// Play a 2D sound to all real clients on a specific team (QC <c>play2team(t, filename)</c>,
    /// all.qc:136-140). The sound is heard only by clients on that team, at 2D with no spatialization.
    /// Respects the <c>bot_sound_monopoly</c> gate: when set, this early-returns (all sounds suppressed).
    /// <paramref name="recipients"/> should be the list of real clients on the server (e.g., from <c>Clients.Players</c>).
    /// </summary>
    public static void Play2Team(int teamId, GameSound? sound, IReadOnlyList<Entity>? recipients = null)
    {
        if (sound is null) return;
        if (recipients is null) return;
        // QC (all.qc:136-140): if (autocvar_bot_sound_monopoly) return;
        // FOREACH_CLIENT(IS_PLAYER(it) && IS_REAL_CLIENT(it) && it.team == t, play2(it, filename));
        if (Api.Services is not null && Api.Cvars.GetFloat("bot_sound_monopoly") != 0f)
            return;  // monopoly gate: suppress all sounds if set
        foreach (Entity it in recipients)
        {
            // IS_PLAYER (classname == "player", i.e. actively playing, not observer) && IS_REAL_CLIENT (not a bot).
            if (it is not Player p || p.IsBot || p.IsObserver) continue;
            if ((int)p.Team != teamId) continue;
            Play2(p, sound);
        }
    }

    /// <summary>Play a 2D sound to all real clients on a team by sound name.</summary>
    public static void Play2Team(int teamId, string soundName, IReadOnlyList<Entity>? recipients = null)
        => Play2Team(teamId, Sounds.ByName(soundName), recipients);

    /// <summary>Play a raw 2D sample to all real clients on a team.</summary>
    public static void Play2TeamRaw(int teamId, string sample, IReadOnlyList<Entity>? recipients = null,
        float volume = SoundLevels.VolBase)
    {
        if (recipients is null) return;
        if (Api.Services is not null && Api.Cvars.GetFloat("bot_sound_monopoly") != 0f)
            return;  // monopoly gate
        foreach (Entity it in recipients)
        {
            // QC FOREACH_CLIENT(IS_PLAYER(it) && IS_REAL_CLIENT(it) && it.team == t).
            if (it is not Player p || p.IsBot || p.IsObserver) continue;
            if ((int)p.Team != teamId) continue;
            Play2Raw(p, sample, volume);
        }
    }

    // ---- global / non-positional (QC play2all / _GlobalSound on world) ----

    /// <summary>
    /// Play a registered sound globally (heard by everyone, no spatialization) on the shared emitter.
    /// Forces ATTEN_NONE so distance doesn't attenuate it, matching QC announcer/global playback.
    /// Successor to QC <c>play2all(SND(def))</c> / a GlobalSound with ATTEN_NONE.
    /// Applies QC <c>play2all</c>'s <c>bot_sound_monopoly</c> gate (all.qc:143-145):
    /// when <c>bot_sound_monopoly=1</c>, global cues are suppressed entirely.
    /// </summary>
    public static void PlayGlobal(GameSound? sound)
    {
        if (sound is null) return;
        // QC play2all (all.qc:143-145): if (autocvar_bot_sound_monopoly) return;
        if (Api.Services is not null && Api.Cvars.GetFloat("bot_sound_monopoly") != 0f) return;
        Api.Sound.Play(GlobalEmitter, sound.EngineChannel, sound.Sample, sound.Volume, SoundLevels.AttenNone);
    }

    /// <summary>Play a registered sound globally with an explicit volume.
    /// Applies the <c>bot_sound_monopoly</c> gate (QC <c>play2all</c>, all.qc:143-145).</summary>
    public static void PlayGlobal(GameSound? sound, float volume)
    {
        if (sound is null) return;
        // QC play2all (all.qc:143-145): if (autocvar_bot_sound_monopoly) return;
        if (Api.Services is not null && Api.Cvars.GetFloat("bot_sound_monopoly") != 0f) return;
        Api.Sound.Play(GlobalEmitter, sound.EngineChannel, sound.Sample, volume, SoundLevels.AttenNone);
    }

    /// <summary>Look the sound up by name then play it globally.
    /// Applies the <c>bot_sound_monopoly</c> gate.</summary>
    public static void PlayGlobal(string soundName) => PlayGlobal(Sounds.ByName(soundName));

    /// <summary>Play a raw sample path globally (QC <c>play2all(samp)</c>).
    /// Applies QC <c>play2all</c>'s <c>bot_sound_monopoly</c> gate (all.qc:143-145).</summary>
    public static void PlayGlobalRaw(string sample, float volume = SoundLevels.VolBase)
    {
        // QC play2all (all.qc:143-145): if (autocvar_bot_sound_monopoly) return;
        if (Api.Services is not null && Api.Cvars.GetFloat("bot_sound_monopoly") != 0f) return;
        Api.Sound.Play(GlobalEmitter, SoundChannel.Auto, sample, volume, SoundLevels.AttenNone);
    }

    /// <summary>
    /// Play an announcer cue globally by its short name (e.g. "begin", "impressive") — looks up
    /// <c>ANNCE_&lt;NAME&gt;</c> in the catalog. Convenience over <see cref="PlayGlobal(string)"/> for the
    /// announcer family (notifications/all.inc MSG_ANNCE_NOTIF).
    /// </summary>
    public static void PlayAnnouncer(string shortName)
        => PlayGlobal(Sounds.ByName($"ANNCE_{shortName.ToUpperInvariant()}"));

    // ---- spamsound — per-entity touch-spam rate limit (QC spamsound, all.qc:124-134) ----

    /// <summary>
    /// Play a registered sound on <paramref name="emitter"/>, rate-limited to at most once per sim step
    /// via <see cref="Entity.SpamTime"/> (QC <c>spamsound(e,chan,samp,vol,atten)</c>, all.qc:124).
    /// Used by touch handlers that fire multiple times per frame (nade bounce, vehicle hit, monster
    /// body-impact) to prevent a touch-spam source from over-emitting. <paramref name="now"/> is the
    /// current sim time (QC <c>time</c>). Returns true if the sound was actually emitted.
    /// </summary>
    public static bool SpamSound(Entity emitter, GameSound? sound, float now)
    {
        if (sound is null) return false;
        if (!SoundAllowedGate.IsAllowed(emitter)) return false;
        if (now > emitter.SpamTime)
        {
            emitter.SpamTime = now;
            Api.Sound.Play(emitter, sound.EngineChannel, sound.Sample, sound.Volume, sound.Attenuation);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Play a raw sample on <paramref name="emitter"/> with spam-rate limiting (QC <c>spamsound</c> raw-path).
    /// </summary>
    public static bool SpamSoundRaw(Entity emitter, string sample, float now,
        SoundChannel channel = SoundChannel.Auto,
        float volume = SoundLevels.VolBase,
        float attenuation = SoundLevels.AttenNorm)
    {
        if (!SoundAllowedGate.IsAllowed(emitter)) return false;
        if (now > emitter.SpamTime)
        {
            emitter.SpamTime = now;
            Api.Sound.Play(emitter, channel, sample, volume, attenuation);
            return true;
        }
        return false;
    }

    // ---- random-variant groups (QC SND_*_RANDOM) ----

    /// <summary>
    /// Play a random member of a registered variant group on <paramref name="emitter"/> (QC
    /// <c>sound(e, ch, SND_X_RANDOM(), …)</c>). <paramref name="groupPrefix"/> is e.g. "RIC" or "GIB_SPLAT0".
    /// </summary>
    public static void PlayRandom(Entity emitter, string groupPrefix)
        => PlayOn(emitter, SoundVariantGroups.PickRegistered(groupPrefix));

    /// <summary>Play a random ricochet (QC <c>SND_RIC_RANDOM</c>). QC <c>wr_impacteffect</c> plays it on
    /// <c>CH_SHOTS</c> (== <see cref="SoundChannel.ShotsAuto"/>) — an AUTO channel so overlapping rics from a
    /// multi-bullet burst stack rather than cut each other off — at VOL_BASE / ATTN_NORM (the RIC sound's
    /// registered volume/attenuation). Forcing CH_SHOTS here matches Base byte-for-byte (rifle.qc:218,
    /// machinegun.qc impacteffect); the prior PlayOn used the RIC sound's default channel hint (Weapon).</summary>
    public static void PlayRic(Entity emitter)
    {
        GameSound? ric = SoundVariantGroups.Ric();
        if (ric is null) return;
        // QC wr_impacteffect: sound(actor, CH_SHOTS, SND_RIC_RANDOM(), ...). The RIC sounds are registered
        // with SoundChannelHint.Weapon (maps to CH_WEAPON_A = -1 by default), but Base plays the ricochet on
        // CH_SHOTS (-4 = ShotsAuto). Explicitly pass the channel to match Base (QC rifle.qc / shotgun.qc line).
        PlayOn(emitter, ric, SoundChannel.ShotsAuto, ric.Volume, ric.Attenuation);
    }

    /// <summary>Play a random gib splat (QC SND_GIB_SPLAT_RANDOM).</summary>
    public static void PlayGibSplat(Entity emitter) => PlayOn(emitter, SoundVariantGroups.GibSplat());

    /// <summary>Play a random grenade bounce (QC SND_GRENADE_BOUNCE_RANDOM).</summary>
    public static void PlayGrenadeBounce(Entity emitter) => PlayOn(emitter, SoundVariantGroups.GrenadeBounce());

    // ---- GlobalSound counted variants (QC GlobalSound_sample / _GlobalSound) ----

    /// <summary>
    /// Play a counted GlobalSound (STEP/FALL/…) on <paramref name="emitter"/>, picking a numbered variant
    /// file via <see cref="SoundVariantGroups.ResolveGlobalSample"/>. Successor to QC <c>GlobalSound(e, def, …)</c>.
    /// </summary>
    public static void PlayGlobalVariant(Entity emitter, GameSound? sound)
    {
        if (sound is null) return;
        // QC GlobalSound (globalsound.qc) applies GlobalSound_pitch(pitchscale*this.scale) before emit.
        // pitchscale is 1.0 at all call sites; ScaleFactor==0 (stock players) returns pitch 1.0 unchanged.
        float pitch = GlobalSoundPitch(emitter.ScaleFactor);
        Api.Sound.Play(emitter, sound.EngineChannel,
            SoundVariantGroups.ResolveGlobalSample(sound), sound.Volume, sound.Attenuation, pitch: pitch);
    }

    /// <summary>Look up a counted GlobalSound by name (QC SND_&lt;name&gt;) then play a variant on the emitter.</summary>
    public static void PlayGlobalVariant(Entity emitter, string soundName)
        => PlayGlobalVariant(emitter, Sounds.ByName(soundName));

    // ---- per-model player / voice sounds (QC PlayerSound / VoiceMessage) ----

    /// <summary>
    /// Play a player body sound (QC <c>PlayerSound(this, def, …)</c>) for <paramref name="emitter"/>,
    /// resolving the sample under the emitter's model sound directory. <paramref name="id"/> is a
    /// <see cref="Sounds.PlayerSoundIds"/> entry (e.g. "pain50", "jump"); <paramref name="modelSoundDir"/>
    /// is the model's sound dir (null/empty => the default pack). Channel defaults to CH_VOICE-ish Voice,
    /// volume to VOL_BASEVOICE, matching QC.
    /// </summary>
    public static void PlayPlayerSound(Entity emitter, string id, string? modelSoundDir = null,
        float volume = SoundLevels.VolBaseVoice, float attenuation = SoundLevels.AttenNorm)
    {
        string sample = Sounds.PlayerSoundSample(modelSoundDir, id);
        // QC PlayerSound plays pain/voice on the AUTO voice channel (CH_VOICE = -2) so overlapping cues stack
        // rather than cut each other off; the rate of these is bounded upstream (pain by the PainFinished
        // debounce, voice/death once per event), so they don't pile up.
        // QC PlayerSound macro also applies GlobalSound_pitch(pitchscale*this.scale) before emit.
        float pitch = GlobalSoundPitch(emitter.ScaleFactor);
        Api.Sound.Play(emitter, SoundChannel.VoiceAuto, sample, volume, attenuation, pitch: pitch);
    }

    /// <summary>
    /// Play a voice message (QC VoiceMessage — team radio / taunt) for <paramref name="emitter"/>. Same
    /// per-model resolution as <see cref="PlayPlayerSound"/>; <paramref name="id"/> is a
    /// <see cref="Sounds.VoiceMessageIds"/> entry (e.g. "attack", "taunt"). This is the simple "play on the
    /// emitter" path; for the full VOICETYPE recipient/gate routing use <see cref="GlobalSound"/>.
    /// </summary>
    public static void PlayVoiceMessage(Entity emitter, string id, string? modelSoundDir = null)
        => PlayPlayerSound(emitter, id, modelSoundDir, SoundLevels.VolBaseVoice, SoundLevels.AttenNorm);

    // ---- VOICETYPE routing (QC _GlobalSound, globalsound.qc:341-465) -------------------------------

    /// <summary>
    /// Route a voice message by its <see cref="VoiceMessages"/> id through the full VOICETYPE dispatch
    /// (QC <c>VoiceMessage(this, def, msg)</c> -&gt; <c>_GlobalSound</c>). Resolves the message's
    /// <see cref="VoiceType"/> and applies the matching recipient set + gates. <paramref name="recipients"/>
    /// is the live real-client roster the host supplies (QC FOREACH_CLIENT) — required for TEAMRADIO and
    /// TAUNT/AUTOTAUNT which broadcast to a subset; null/empty falls back to playing on the emitter only.
    /// </summary>
    public static void GlobalSound(Entity emitter, string voiceMessageId,
        IReadOnlyList<Entity>? recipients = null, string? modelSoundDir = null, bool fake = false)
    {
        VoiceType vt = VoiceMessages.VoiceTypeOf(voiceMessageId);
        _GlobalSound(emitter, voiceMessageId, vt, recipients, modelSoundDir, fake);
    }

    /// <summary>
    /// The C# successor to QC <c>_GlobalSound</c> (globalsound.qc:341): dispatch a voice/player sound to the
    /// recipients selected by <paramref name="voiceType"/>, applying the directional attenuation and the
    /// sv_autotaunt / sv_taunt / sv_gentle gates. The recipient sets mirror QC exactly:
    /// <list type="bullet">
    ///   <item><b>LASTATTACKER / LASTATTACKER_ONLY</b>: heard by the emitter's <see cref="Entity.Pusher"/>
    ///         (the last attacker), and — for LASTATTACKER (not _ONLY) — also by the emitter.</item>
    ///   <item><b>TEAMRADIO</b>: heard by every recipient on the emitter's team (QC SAME_TEAM).</item>
    ///   <item><b>AUTOTAUNT</b>: gated by <c>sv_autotaunt</c>; <b>TAUNT</b> gated by <c>sv_taunt</c> and
    ///         suppressed by <c>sv_gentle</c>; both broadcast to all recipients.</item>
    ///   <item><b>PLAYERSOUND</b>: broadcast to everyone at ATTEN_NORM.</item>
    /// </list>
    /// </summary>
    /// <param name="fake">QC <c>fake</c> (globalsound.qc:341): when true the sound is heard ONLY by the emitter
    /// (every voicetype branch collapses to <c>msg_entity = this; …</c> at MSG_ONE). Driven by the VoiceMessage
    /// macro from the chat flood/spectator gate (fake = IS_SPEC || IS_OBSERVER || Say-flood &lt; 0): a spectator,
    /// observer, or flood-faked speaker hears their own voice taunt but no one else does.</param>
    public static void _GlobalSound(Entity emitter, string voiceMessageId, VoiceType voiceType,
        IReadOnlyList<Entity>? recipients, string? modelSoundDir = null, bool fake = false)
    {
        // QC: if(this.classname == "body") return; — corpses don't speak.
        if (emitter is null || emitter.IsCorpse) return;

        string sample = Sounds.PlayerSoundSample(modelSoundDir, voiceMessageId);

        // QC _GlobalSound(globalsound.qc:341): pitch = GlobalSound_pitch(pitchscale * this.scale).
        // pitchscale is 1.0 at all current port call sites; this.scale maps to Entity.ScaleFactor
        // (MapObjectFieldsExtra partial; 0f = unset/default = 1x = no pitch shift for stock players).
        float pitch = GlobalSoundPitch(emitter.ScaleFactor);

        // QC reads cl_voice_directional per-recipient from CS_CVAR(it) for the directional cases.
        // The port has a single global cvar table (no per-client cvar stores), so we read the global
        // cvar value once and apply it uniformly — correct for a listen server where there is one client.
        // A dedicated-server path with per-client cvars can replace this read with a per-recipient lookup.
        float voiceDirectional = CvarFloat(VoiceCvars.ClVoiceDirectional);
        // QC LASTATTACKER/TEAMRADIO: atten = (cvar_cl_voice_directional == 1) ? ATTEN_MIN : ATTEN_NONE
        float directionalAtten = voiceDirectional == 1f ? SoundLevels.AttenMin : SoundLevels.AttenNone;

        switch (voiceType)
        {
            case VoiceType.LastAttackerOnly:
            case VoiceType.LastAttacker:
            {
                // QC: if(!fake) { if(!this.pusher) break; … } — a faked speaker skips the attacker emit.
                if (!fake && emitter.Pusher is { } attacker)
                    Emit(attacker, sample, SoundLevels.VolBaseVoice, directionalAtten, pitch);
                // QC: LASTATTACKER_ONLY stops here; LASTATTACKER also plays back to the speaker at ATTEN_NONE.
                if (voiceType == VoiceType.LastAttackerOnly) break;
                Emit(emitter, sample, SoundLevels.VolBaseVoice, SoundLevels.AttenNone, pitch);
                break;
            }

            case VoiceType.TeamRadio:
            {
                // QC: if(fake) { msg_entity = this; X(); } — a faked speaker hears it alone.
                if (fake) { Emit(emitter, sample, SoundLevels.VolBaseVoice, directionalAtten, pitch); break; }
                // QC: FOREACH_CLIENT(IS_REAL_CLIENT(it) && SAME_TEAM(it, this), …) at the directional atten.
                if (recipients is null) { Emit(emitter, sample, SoundLevels.VolBaseVoice, directionalAtten, pitch); break; }
                foreach (Entity it in recipients)
                    if (it is not null && Teams.SameTeam(it, emitter))
                        Emit(it, sample, SoundLevels.VolBaseVoice, directionalAtten, pitch);
                break;
            }

            case VoiceType.AutoTaunt:
            case VoiceType.Taunt:
            {
                // QC: gate the autotaunt on sv_autotaunt; the manual taunt on sv_taunt and !sv_gentle.
                if (voiceType == VoiceType.AutoTaunt)
                {
                    if (!CvarBool(VoiceCvars.SvAutotaunt)) break;
                }
                else
                {
                    // [W14b Stage 4] QC (globalsound.qc:406-407): a living manual-TAUNT speaker triggers the taunt
                    // animation BEFORE the sv_taunt / sv_gentle gates below (so the anim plays even with sv_taunt 0).
                    // QC: if(IS_PLAYER(this) && !IS_DEAD(this)) animdecide_setaction(this, ANIMACTION_TAUNT, true).
                    // IS_DEAD == deadflag != DEAD_NO; the port's corpse flag covers the same "not a live player" case.
                    if (emitter is not null && !emitter.IsCorpse && emitter.DeadState == DeadFlag.No)
                    {
                        float tauntNow = Api.Services is not null ? Api.Clock.Time : 0f;
                        var (act, start) = AnimDecide.SetAction(
                            emitter.AnimUpperAction, emitter.AnimActionStart, AnimDecide.AnimUpperAction.Taunt, tauntNow, restart: true);
                        emitter.AnimUpperAction = act;
                        emitter.AnimActionStart = start;
                    }
                }
                if (!CvarBool(VoiceCvars.SvTaunt)) break;
                if (CvarBool(VoiceCvars.SvGentle)) break;

                // QC taunt attenuation (globalsound.qc:419-421): when cl_voice_directional >= 1,
                // bound(ATTEN_MIN, cvar_cl_voice_directional_taunt_attenuation, ATTEN_MAX); else ATTEN_NONE.
                float tauntAtten;
                if (voiceDirectional >= 1f)
                {
                    float tauntAttenCvar = CvarFloat(VoiceCvars.ClVoiceDirectionalTauntAttenuation);
                    tauntAtten = System.Math.Clamp(tauntAttenCvar, SoundLevels.AttenMin, SoundLevels.AttenMax);
                }
                else
                {
                    tauntAtten = SoundLevels.AttenNone;
                }

                // QC: if(fake) { msg_entity = this; X(); } — a faked speaker hears it alone.
                if (fake) { Emit(emitter, sample, SoundLevels.VolBaseVoice, tauntAtten, pitch); break; }

                // QC AUTOTAUNT also rolls a PER-RECIPIENT random < that client's networked cvar_cl_autotaunt
                // (globalsound.qc:411-426) so each recipient opts in to hearing autotaunts. That gate is a
                // per-client cvar the port does not network (per-client cvar stores are deferred, like
                // cl_voice_directional), so we cannot resolve a recipient's cl_autotaunt server-side. Modeling it
                // with a single global cl_autotaunt (default 0) would silence ALL autotaunts, which is worse than
                // the prior behavior — so the per-client opt-in is left deferred and the server gates (sv_autotaunt
                // + sv_taunt + !sv_gentle, applied above) are the authoritative filter, matching the port's
                // emit-to-all model for the manual TAUNT.

                // QC broadcasts to all real clients (FOREACH_CLIENT(IS_REAL_CLIENT(it))) at the taunt atten.
                if (recipients is null) { Emit(emitter, sample, SoundLevels.VolBaseVoice, tauntAtten, pitch); break; }
                foreach (Entity it in recipients)
                {
                    if (it is null) continue;
                    Emit(it, sample, SoundLevels.VolBaseVoice, tauntAtten, pitch);
                }
                break;
            }

            case VoiceType.PlayerSound:
            default:
            {
                // QC: globalsound(MSG_ALL, …, ATTEN_NORM) — heard by everyone at the emitter's position.
                Emit(emitter, sample, SoundLevels.VolBaseVoice, SoundLevels.AttenNorm, pitch);
                break;
            }
        }
    }

    /// <summary>Emit a resolved voice sample on the CH_VOICE channel (QC sound7/soundto from _GlobalSound).
    /// <paramref name="pitch"/> is the model-scale pitch factor from <see cref="GlobalSoundPitch"/>; defaults to 1.0 (no shift).</summary>
    private static void Emit(Entity e, string sample, float volume, float attenuation, float pitch = 1f)
        => Api.Sound.Play(e, SoundChannel.VoiceAuto, sample, volume, attenuation, pitch: pitch);

    /// <summary>
    /// Model-scale pitch shift for a GlobalSound/PlayerSound emit (QC <c>GlobalSound_pitch(p)</c>,
    /// globalsound.qc). Converts an entity's <c>.scale</c> (the <see cref="Entity.ScaleFactor"/> field,
    /// declared in <c>MapObjectFieldsExtra</c>) to a playback pitch factor via the asymptotic gradient:
    /// <c>pitch = (b*d*(a-1) + a*c*(1-b)) / (d*(a-1) + c*(1-b))</c>
    /// where a=1.5 (max, tiny entity), b=0.75 (min, large entity), c=100 (nominal; scale==1.0 -> pitch==1.0).
    /// <para>Input <paramref name="scaleFactor"/> is the multiplier-form scale; internally converted to
    /// percentage (*100) to match the QC gradient's domain. 0f (QC default, unset) returns 1.0f (no shift).</para>
    /// </summary>
    internal static float GlobalSoundPitch(float scaleFactor)
    {
        // scaleFactor == 0 is the QC default (.scale unset) => treat as 1x, no pitch shift.
        if (scaleFactor == 0f) return 1f;
        const float a = 1.5f;    // QC GlobalSound_pitch: max pitch (tiny entity)
        const float b = 0.75f;   // QC GlobalSound_pitch: min pitch (huge entity)
        const float c = 100f;    // QC GlobalSound_pitch: nominal input (scaleFactor==1.0 => pitch==1.0)
        float d = scaleFactor * c;
        float denom = d * (a - 1f) + c * (1f - b);
        if (System.MathF.Abs(denom) < 1e-6f) return 1f;
        float pitch = (b * d * (a - 1f) + a * c * (1f - b)) / denom;
        return System.Math.Clamp(pitch, b, a);
    }

    /// <summary>Read a boolean cvar via the facade (a non-zero float is true; unset/no-services is false).</summary>
    private static bool CvarBool(string name)
        => Api.Services is not null && Api.Cvars.GetFloat(name) != 0f;

    /// <summary>Read a float cvar via the facade (0 when no services are installed).</summary>
    private static float CvarFloat(string name)
        => Api.Services is not null ? Api.Cvars.GetFloat(name) : 0f;

    /// <summary>Reset the shared emitter (test support; also drops a freed emitter reference).</summary>
    public static void Reset() => _globalEmitter = null;
}
