// Client-side music player — the Godot node that actually plays background music for maps.
//
// Faithful port of QC TargetMusic_Advance (common/mapobjects/target/music.qc:179-213):
//   Priority (highest wins): trigger_music > target_music > cdtrack (default)
//
// QC maintains a .state (0..1) PER listed entity and ramps it up (frametime/fade_time) for the best
// source and down (frametime/fade_rate) for all others — ALL sources play simultaneously at their own
// volume. The port replicates this exactly: one AudioStreamPlayer per distinct track path (created
// on demand, stopped and freed when state reaches 0), with SourceState tracking the per-source ramp.
//
// For the listen-server path, target_music/trigger_music entities live in the same process, so we read
// their state directly (no networking needed). The server's GameWorld exposes the entities via the
// standard Api.Entities scan.

using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;

namespace VortexArena.Game.Client;

/// <summary>
/// Non-positional music playback node (the cdtrack / target_music / trigger_music renderer).
/// Added as a child of <see cref="ClientWorld"/> or the net host node. Each frame it evaluates
/// the priority stack and crossfades between tracks.
/// </summary>
public sealed partial class MusicPlayer : Node
{
    // ---- configuration (set by the host before/after AddChild) ----

    /// <summary>The cdtrack for the current map (resolved from the mapinfo file). Empty = no default music.</summary>
    public string CdTrack { get; set; } = "";

    /// <summary>
    /// Loads a sound sample path to a decoded <see cref="AudioStream"/> from the VFS (same delegate as
    /// <c>ClientWorld.AudioLoader</c>). Used to load the music .ogg files from the content packs.
    /// </summary>
    public Func<string, AudioStream?>? AudioLoader { get; set; }

    /// <summary>Default fade-in/fade-out time in seconds when switching tracks.</summary>
    public float DefaultFadeTime { get; set; } = 2.0f;

    /// <summary>Volume multiplier from bgmvolume cvar (0-1). Updated externally each frame or on cvar change.</summary>
    public float BgmVolume { get; set; } = 0.7f;

    /// <summary>
    /// QC <c>target_music_kill()</c> at <c>NextLevel</c>: when the match reaches intermission the map music is
    /// stopped (the cdtrack / target_music / trigger_music sources are silenced) so the scoreboard plays over
    /// silence rather than the looping map track. Set by the host from the networked intermission flag. (The
    /// Base intermission <c>cd loop sv_intermission_cdtrack</c> switch is empty by default, so killing the map
    /// music is the visible behaviour.)
    /// </summary>
    public bool Intermission { get; set; }

    /// <summary>
    /// QC FixIntermissionClient: the value of <c>sv_intermission_cdtrack</c> — a space-separated list of music
    /// tracks; when intermission begins, Base picks a random word (RandomSelection over the words) and
    /// <c>cd loop &lt;track&gt;</c>s it for the scoreboard. Empty (the stock default) = no switch, so the map
    /// music is simply killed (see <see cref="Intermission"/>). Set by the host from the server cvar.
    /// </summary>
    public string IntermissionCdTrack { get; set; } = "";

    // The track chosen once when intermission begins (RandomSelection is a one-shot pick, not re-rolled each
    // frame). Reset when intermission ends so a fresh match re-rolls.
    private string _chosenIntermissionTrack = "";
    private bool _intermissionLatched;
    private readonly System.Random _intermissionRng = new();

    // ---- live state — per-source model (QC TargetMusic_Advance .state per entity) ----
    // QC maintains a .state (0..1 ramp) PER listed entity: the "best" source ramps UP by frametime/fade_time,
    // ALL others ramp DOWN by frametime/fade_rate, and every source plays simultaneously at state*volume*bgm.
    // The port replicates this faithfully: one AudioStreamPlayer per distinct track path (pooled; stopped and
    // removed when state reaches 0), with a per-track SourceState recording the ramp position + fade params.

    private sealed class SourceState
    {
        public AudioStreamPlayer? Player;  // Godot player node (null until the stream loads)
        public float State;               // QC .state: 0..1 ramp (0 = silent, 1 = full volume)
        public float FadeTime;            // QC fade_time: seconds to ramp UP (from source entity or DefaultFadeTime)
        public float FadeRate;            // QC fade_rate: seconds to ramp DOWN
        public float Volume;              // target volume from the source entity (1 = full)
        public bool LoadFailed;           // true if the stream couldn't be loaded (skip forever)
    }

    // keyed by resolved track path (case-insensitive)
    private readonly Dictionary<string, SourceState> _sources = new(StringComparer.OrdinalIgnoreCase);

    // ---- music source scan ----
    // These are populated by the host (listen server) each frame or on entity state change.
    // For simplicity in the listen-server path, we scan the entity list directly.

    /// <summary>
    /// Optional: direct reference to the server world's entity list for scanning target_music / trigger_music.
    /// On the listen-server path the host sets this so we can read entity state directly.
    /// </summary>
    public IReadOnlyList<Entity>? EntityList { get; set; }

    /// <summary>Current server time (for trigger_music touch freshness check).</summary>
    public float ServerTime { get; set; }

    /// <summary>
    /// S5 (sv_threaded): the host's shared sim gate, set by NetGame ONLY when the listen server runs its
    /// simulation on a dedicated worker thread. <see cref="EntityList"/> is then the live server entity table
    /// the worker mutates, so the per-frame scan in <see cref="EvaluateMusicSources"/> must hold this gate to
    /// avoid racing a concurrent spawn/relink. Null on the default single-threaded path → no lock is taken and
    /// the scan is byte-for-byte the old behaviour.
    /// </summary>
    public object? SimGate { get; set; }

    // ---- constants ----
    private const string MusicBus = "Music";
    private const float TouchFreshnessWindow = 0.2f; // a trigger_music is "active" if touched within this many seconds

    public override void _Ready()
    {
        // Per-source model: no pre-allocated slots; AudioStreamPlayers are created on demand per track.
        // If we have a cdtrack at boot, prime the source so it starts at state 0 and ramps in.
        if (!string.IsNullOrEmpty(CdTrack))
            EnsureSource(CdTrack, 1f, DefaultFadeTime, DefaultFadeTime);
    }

    public override void _Process(double delta)
    {
        using var _scope = FrameProfiler.Scope("music"); // [profiling] §18: out of proc:other
        float dt = (float)delta;

        // --- evaluate the priority stack to find the best music source ---
        // S5: the scan reads the live server entity list; when sv_threaded the host hands us its gate so this
        // can't race the worker's spawn/relink. SimGate is null on the default path → no lock, byte-identical.
        string bestTrack;
        float bestVolume, fadeIn, fadeOut;
        if (SimGate is not null)
        {
            // WS1 stage 3: never BLOCK the render thread behind a mid-flight sim tick for a music scan — if
            // the gate is contended, skip this frame's re-evaluation (music state freezes one frame; inaudible).
            bool taken = false;
            try
            {
                System.Threading.Monitor.TryEnter(SimGate, 0, ref taken);
                if (!taken)
                    return;
                EvaluateMusicSources(out bestTrack, out bestVolume, out fadeIn, out fadeOut);
            }
            finally
            {
                if (taken) System.Threading.Monitor.Exit(SimGate);
            }
        }
        else
        {
            EvaluateMusicSources(out bestTrack, out bestVolume, out fadeIn, out fadeOut);
        }

        // QC target_music_kill() at NextLevel + the FixIntermissionClient `cd loop sv_intermission_cdtrack`
        // switch: at intermission, if sv_intermission_cdtrack names one or more tracks, pick a random word once
        // (QC RandomSelection over FOREACH_WORD) and loop it for the scoreboard; otherwise silence the map music
        // entirely.
        if (Intermission)
        {
            if (!_intermissionLatched)
            {
                _intermissionLatched = true;
                _chosenIntermissionTrack = PickIntermissionTrack();
            }
            bestTrack = _chosenIntermissionTrack;   // chosen track, or "" to kill the music
            if (!string.IsNullOrEmpty(bestTrack))
            {
                bestVolume = 1f;
                fadeIn = DefaultFadeTime;
                fadeOut = DefaultFadeTime;
            }
        }
        else if (_intermissionLatched)
        {
            // Intermission ended (restart / next map) — re-roll on the next intermission.
            _intermissionLatched = false;
            _chosenIntermissionTrack = "";
        }

        // --- per-source state ramp (QC TargetMusic_Advance) ---
        // Ensure the best source exists; for every source ramp state up (if best) or down (if not), then
        // apply volumes. This is the faithful port of the QC per-entity .state model.
        AdvanceAllSources(bestTrack, bestVolume, fadeIn, fadeOut, dt);
    }

    // ========================================================================
    //  Priority evaluation
    // ========================================================================

    // (§11 R7) The music sources are map-static (trigger_music/target_music spawn at map load), so the
    // per-frame priority scan iterates this small cached subset instead of walking EVERY entity (the old
    // full-list scan ran 3 passes × all entities × frame, with per-entity string ClassName compares —
    // under the SimGate lock when sv_threaded). Rebuilt when the EntityList reference changes (map load)
    // and on a slow safety interval in case a mod spawns one mid-match.
    private readonly List<Entity> _triggerMusic = new();
    private readonly List<Entity> _targetMusic = new();
    private IReadOnlyList<Entity>? _scannedList;
    private float _nextRescanAt = float.NegativeInfinity;
    private const float RescanInterval = 5f;

    private void RefreshMusicEntities()
    {
        bool stale = !ReferenceEquals(EntityList, _scannedList)
                     || ServerTime >= _nextRescanAt
                     || _nextRescanAt > ServerTime + RescanInterval;   // server clock went backwards
        if (!stale)
            return;
        _scannedList = EntityList;
        _nextRescanAt = ServerTime + RescanInterval;
        _triggerMusic.Clear();
        _targetMusic.Clear();
        if (EntityList is null)
            return;
        for (int i = 0; i < EntityList.Count; i++)
        {
            Entity e = EntityList[i];
            if (e.IsFreed) continue;
            if (e.ClassName == "trigger_music") _triggerMusic.Add(e);
            else if (e.ClassName == "target_music") _targetMusic.Add(e);
        }
    }

    private void EvaluateMusicSources(out string bestTrack, out float bestVolume, out float fadeIn, out float fadeOut)
    {
        bestTrack = "";
        bestVolume = 1f;
        fadeIn = DefaultFadeTime;
        fadeOut = DefaultFadeTime;

        // Highest priority: trigger_music (player inside a brush volume)
        if (EntityList is not null)
        {
            RefreshMusicEntities();

            // Scan for active trigger_music whose touch is fresh (player inside it this frame).
            // QC Ent_TriggerMusic_Think runs for the local client each frame; the LAST entity
            // processed whose volume contains the player wins (latest in CL_LinkPacketEntities).
            // Mirror that by picking the entry with the highest PushLTime stamp (most recently
            // touched) — equivalent to "last one wins" when multiple volumes overlap.
            Entity? bestTrigger = null;
            float bestTriggerTime = float.NegativeInfinity;
            for (int i = 0; i < _triggerMusic.Count; i++)
            {
                Entity e = _triggerMusic[i];
                if (e.IsFreed) continue;
                if (e.Active != MapMover.ActiveActive) continue;
                if (string.IsNullOrEmpty(e.Noise)) continue;
                // The touch handler stamps PushLTime each frame the player overlaps.
                // Consider it "active" if touched within the freshness window; keep the
                // most-recently-touched one (highest PushLTime) to match QC last-wins order.
                if (ServerTime - e.PushLTime <= TouchFreshnessWindow
                    && e.PushLTime > bestTriggerTime)
                {
                    bestTrigger = e;
                    bestTriggerTime = e.PushLTime;
                }
            }
            if (bestTrigger is not null)
            {
                bestTrack = ResolveMusicPath(bestTrigger.Noise);
                bestVolume = bestTrigger.Volume > 0f ? bestTrigger.Volume : 1f;
                (fadeIn, fadeOut) = Fades(bestTrigger); // the REAL fade_time/fade_rate map keys (not .speed)
                return;
            }

            // Medium priority: a TRIGGERED target_music whose .lifetime window is still open.
            // QC (music.qc TargetMusic_Advance + Net_TargetMusic): a used target_music with lifetime>0
            // becomes music_target and outranks the default only while time < activation + lifetime; a used
            // one with lifetime 0 instead REPLACES the music_default slot (handled in the default pass below).
            Entity? bestTarget = null;
            for (int i = 0; i < _targetMusic.Count; i++)
            {
                Entity e = _targetMusic[i];
                if (e.IsFreed) continue;
                if (e.Active != MapMover.ActiveActive) continue;
                if (string.IsNullOrEmpty(e.Noise)) continue;
                if (string.IsNullOrEmpty(e.TargetName)) continue;     // untargeted = default slot
                if (e.MusicActivationTime < 0f) continue;             // never triggered
                if (e.MusicLifetime <= 0f) continue;                  // lifetime 0 => default slot
                if (ServerTime >= e.MusicActivationTime + e.MusicLifetime) continue; // expired
                bestTarget = e;
                break;
            }
            if (bestTarget is not null)
            {
                bestTrack = ResolveMusicPath(bestTarget.Noise);
                bestVolume = bestTarget.Volume > 0f ? bestTarget.Volume : 1f;
                (fadeIn, fadeOut) = Fades(bestTarget);
                return;
            }

            // Default slot — overrides cdtrack. Prefer the most recently ACTIVATED lifetime-0 targeted
            // track (QC: Net_TargetMusic with tim==0 reassigns music_default), else the untargeted default.
            Entity? def = null;
            for (int i = 0; i < _targetMusic.Count; i++)
            {
                Entity e = _targetMusic[i];
                if (e.IsFreed) continue;
                if (e.Active != MapMover.ActiveActive) continue;
                if (string.IsNullOrEmpty(e.Noise)) continue;
                if (!string.IsNullOrEmpty(e.TargetName))
                {
                    // A used lifetime-0 track claims the default slot; latest activation wins.
                    if (e.MusicLifetime > 0f || e.MusicActivationTime < 0f) continue;
                    if (def is null || string.IsNullOrEmpty(def.TargetName)
                        || e.MusicActivationTime > def.MusicActivationTime)
                        def = e;
                }
                else if (def is null)
                {
                    def = e; // the untargeted map default (loses to any claimed lifetime-0 track)
                }
            }
            if (def is not null)
            {
                bestTrack = ResolveMusicPath(def.Noise);
                bestVolume = def.Volume > 0f ? def.Volume : 1f;
                (fadeIn, fadeOut) = Fades(def);
                return;
            }
        }

        // Lowest priority: cdtrack (the map's mapinfo default).
        if (!string.IsNullOrEmpty(CdTrack))
        {
            bestTrack = CdTrack;
            bestVolume = 1f;
            fadeIn = DefaultFadeTime;
            fadeOut = DefaultFadeTime;
        }
    }

    /// <summary>A source's fade pair: the plumbed QC <c>fade_time</c>/<c>fade_rate</c> map keys, falling back
    /// to <see cref="DefaultFadeTime"/> when unset (0). QC's 0 means "instant"; the port keeps its established
    /// gentle default crossfade instead — no shipped map carries the keys, so nothing regresses.</summary>
    private (float fadeIn, float fadeOut) Fades(Entity e) => (
        e.MusicFadeIn > 0f ? e.MusicFadeIn : DefaultFadeTime,
        e.MusicFadeOut > 0f ? e.MusicFadeOut : DefaultFadeTime);

    // ========================================================================
    //  Per-source state engine (QC TargetMusic_Advance faithful port)
    // ========================================================================

    /// <summary>
    /// Ensure a <see cref="SourceState"/> entry exists for <paramref name="track"/> and update its fade
    /// params. Called when evaluating the best source so the entry exists before <see cref="AdvanceAllSources"/>
    /// ticks it. If the source was already known its fade params are refreshed (in case the entity's
    /// fade_time/fade_rate map keys changed).
    /// </summary>
    private SourceState EnsureSource(string track, float volume, float fadeIn, float fadeOut)
    {
        if (!_sources.TryGetValue(track, out SourceState? src))
        {
            src = new SourceState { State = 0f };
            _sources[track] = src;
        }
        src.Volume   = volume > 0f ? volume : 1f;
        src.FadeTime = fadeIn  > 0f ? fadeIn  : DefaultFadeTime;
        src.FadeRate = fadeOut > 0f ? fadeOut : DefaultFadeTime;
        return src;
    }

    /// <summary>
    /// Port of QC <c>TargetMusic_Advance</c> (music.qc:179-213): for every known source advance its
    /// <c>.state</c> ramp up (if it IS the best) or down (otherwise), ensure its AudioStreamPlayer exists
    /// and is playing, then apply <c>state * volume * bgmvolume</c> to its volume. Sources that reach
    /// state 0 are stopped and removed.
    /// </summary>
    private void AdvanceAllSources(string bestTrack, float bestVolume, float fadeIn, float fadeOut, float dt)
    {
        float bgm = Mathf.Clamp(BgmVolume, 0f, 1f);

        // Ensure the best source entry is present with up-to-date params (QC: it is in TargetMusic_list).
        if (!string.IsNullOrEmpty(bestTrack))
            EnsureSource(bestTrack, bestVolume, fadeIn, fadeOut);

        // Track which sources completed a fade-out this frame so we can remove them after the loop.
        List<string>? toRemove = null;

        foreach (var kv in _sources)
        {
            string track = kv.Key;
            SourceState src = kv.Value;

            bool isBest = string.Equals(track, bestTrack, StringComparison.OrdinalIgnoreCase);

            // QC TargetMusic_Advance: ramp up for the best source, down for all others.
            if (isBest)
            {
                // state += (fade_time > 0) ? frametime/fade_time : 1.0 (instant)
                float step = src.FadeTime > 0f ? dt / src.FadeTime : 1f;
                src.State = System.Math.Min(1f, src.State + step);
            }
            else
            {
                // state -= (fade_rate > 0) ? frametime/fade_rate : 1.0 (instant)
                float step = src.FadeRate > 0f ? dt / src.FadeRate : 1f;
                src.State = System.Math.Max(0f, src.State - step);
            }

            float vol = src.State * src.Volume * bgm;

            if (!src.LoadFailed && src.Player is null && src.State > 0f)
            {
                // Lazy-create the AudioStreamPlayer the first time this source needs to be audible.
                AudioStream? stream = LoadMusicStream(track);
                if (stream is null)
                {
                    src.LoadFailed = true;
                }
                else
                {
                    AudioStream looping = AudioLoop.MakeLooping(stream, out bool nativeLoop);
                    var player = new AudioStreamPlayer { Name = $"Music_{track.Replace('/', '_')}", Bus = MusicBus, VolumeDb = -80f };
                    AddChild(player);
                    player.Stream = looping;
                    if (!nativeLoop)
                        player.Finished += () => { if (IsInstanceValid(player) && player.Stream == looping) player.Play(); };
                    player.Play();
                    src.Player = player;
                }
            }

            if (src.Player is { } p && IsInstanceValid(p))
            {
                if (vol <= 0.001f)
                {
                    // QC: source silent — stop and clean up the node; record for removal from dictionary.
                    p.Stop();
                    p.QueueFree();
                    src.Player = null;
                    if (src.State <= 0f)
                        (toRemove ??= new()).Add(track);
                }
                else
                {
                    // Ensure the player is playing (it might have been stopped before reaching 0 on a prior frame).
                    if (!p.Playing) p.Play();
                    p.VolumeDb = Mathf.LinearToDb(vol);
                }
            }
        }

        if (toRemove is not null)
            foreach (string key in toRemove)
                _sources.Remove(key);
    }

    // ========================================================================
    //  Audio loading
    // ========================================================================

    private AudioStream? LoadMusicStream(string trackPath)
    {
        if (string.IsNullOrEmpty(trackPath))
            return null;

        // Try the AudioLoader (VFS-based, reads from mounted content packs).
        AudioStream? stream = AudioLoader?.Invoke(trackPath);
        if (stream is not null)
            return stream;

        // Fallback: try as a res:// path.
        string resPath = $"res://sound/{trackPath}";
        if (!resPath.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) &&
            !resPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            resPath += ".ogg";
        if (ResourceLoader.Exists(resPath))
        {
            try { return ResourceLoader.Load<AudioStream>(resPath); }
            catch { return null; }
        }
        return null;
    }

    /// <summary>
    /// QC FixIntermissionClient: pick a random word from <see cref="IntermissionCdTrack"/> (the
    /// <c>sv_intermission_cdtrack</c> value), reproducing the QC <c>RandomSelection_Init();
    /// FOREACH_WORD(... RandomSelection_AddString(it, 1, 1)); RandomSelection_chosen_string</c> dance. Empty
    /// value (the stock default) → "" (no track, the map music is killed). Each word is added with equal
    /// weight, so the choice is uniform over the words.
    /// </summary>
    private string PickIntermissionTrack()
    {
        if (string.IsNullOrWhiteSpace(IntermissionCdTrack))
            return "";
        string[] words = IntermissionCdTrack.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return "";
        // Resolve to the VFS path the AudioLoader expects (bare "12" → "cdtracks/track012", etc.) — the same
        // normalization the cdtrack/target_music paths get, so the chosen track loads identically.
        return ResolveMusicPath(words[_intermissionRng.Next(words.Length)]);
    }

    // ========================================================================
    //  Track path resolution
    // ========================================================================

    /// <summary>
    /// Resolve a music track reference to the VFS path the AudioLoader expects.
    /// Handles the DP conventions:
    ///   - A bare number like "12" -> "cdtracks/track012" (3-digit zero-padded)
    ///   - A bare name like "neon" -> "cdtracks/neon"
    ///   - A path like "sound/cdtracks/neon.ogg" -> passed through (strip sound/ prefix for AudioLoader)
    ///   - A path like "cdtracks/neon" -> passed through
    /// </summary>
    public static string ResolveMusicPath(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "";

        string trimmed = raw.Trim();

        // If it already has a directory separator, it's a relative path — normalize.
        if (trimmed.Contains('/') || trimmed.Contains('\\'))
        {
            // Strip leading "sound/" if present (AudioLoader roots under sound/ itself).
            if (trimmed.StartsWith("sound/", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(6);
            // Strip extension (AudioLoader probes .ogg then .wav).
            if (trimmed.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ||
                trimmed.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(0, trimmed.Length - 4);
            return trimmed;
        }

        // Bare value: check if it's a number (DP "cd loop 12" -> track012).
        if (int.TryParse(trimmed, out int trackNum) && trackNum > 0)
            return $"cdtracks/track{trackNum:D3}";

        // Bare name: assume it's under cdtracks/.
        return $"cdtracks/{trimmed}";
    }
}
