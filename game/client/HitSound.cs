// Port of qcsrc/client/view.qc HitSound() + UpdateDamage() — the client-side hit/typehit/kill feedback sounds.
// The state machine (accumulate → antispam window → pitch; stat-time-advance typehit/kill) lives in the
// testable VortexArena.Net.HitSoundLogic; this wrapper owns the cvar reads and the Godot audio player.

using Godot;
using VortexArena.Engine.Simulation;
using VortexArena.Net;

namespace VortexArena.Game.Client;

/// <summary>
/// The three client hit-feedback sounds (QC <c>HitSound()</c> in <c>view.qc</c>), driven per frame by the
/// owner's networked/local feedback stats via <see cref="Update"/>:
/// <list type="bullet">
///   <item><b>misc/hit</b> — the damage-confirm beep. Damage accumulates across the antispam window
///   (<c>cl_hitsound_antispam_time</c>, 0.05s) and plays ONE beep per window — never dropped, at most one
///   window late. <c>cl_hitsound</c>: 0 off, 1 fixed pitch, 2 pitch falls with damage, 3 rises.</item>
///   <item><b>misc/typehit</b> — the team-hit / chatting-victim "dink" (TYPEHIT_TIME advance).</item>
///   <item><b>misc/kill</b> — the kill confirm (KILL_TIME advance). The server's flush gives the kill
///   priority over the hit beep, so the killing blow plays ONLY this.</item>
/// </list>
/// All three are non-positional cues on the SFX bus, played through a small VOICE POOL so they OVERLAP.
/// QC uses <c>CH_INFO</c>, which is channel <b>0</b> (common/sounds/sound.qh:6) — in Quake/DP channel 0
/// auto-allocates a free channel and never stops a playing sound, so Base's cues ring out over each other.
/// A single shared player that Stop()s first is NOT equivalent: <c>misc/kill.wav</c> is 1.25 s and the next
/// hit beep lands 0.05 s later (<c>cl_hitsound_antispam_time</c>), so the kill confirm was being cut after
/// ~4% of its length, and sustained fire chopped every beep to 50 ms instead of layering.
/// Typehit/kill are NOT gated by <c>cl_hitsound</c>, exactly like QC.
/// </summary>
public sealed class HitSound
{
    // Enough concurrent voices to cover the realistic worst case: a 1.25 s kill confirm overlapping a train
    // of 0.26 s beeps arriving every 0.05 s (~6 live) plus headroom. When all are busy the oldest is reused,
    // which is what DP's SND_PickChannel does once it runs out of channels.
    private const int VoiceCount = 8;

    private readonly CvarService? _cvars;
    private readonly HitSoundLogic _logic = new();
    private readonly AudioStreamPlayer?[] _voices = new AudioStreamPlayer?[VoiceCount];
    private int _nextVoice;
    private AudioStream? _hitStream, _typeHitStream, _killStream;
    private bool _hitProbed, _typeHitProbed, _killProbed; // one probe per sample; a miss stays silent
    private Node? _parent;

    /// <summary>Load a feedback sample from the mounted VFS (host-set loader; probes .ogg then .wav).</summary>
    public System.Func<string, AudioStream?>? AudioLoader { get; set; }

    public HitSound(CvarService? cvars)
    {
        _cvars = cvars;
    }

    /// <summary>Attach to a parent node (so the AudioStreamPlayer can live in the scene tree).</summary>
    public void Attach(Node parent)
    {
        _parent = parent;
    }

    /// <summary>Forget the stat baselines: the next <see cref="Update"/> re-seeds silently (reconnect).</summary>
    public void Reset() => _logic.Reset();

    /// <summary>
    /// Per-frame update with the owner's feedback stats — a listen host passes the live server Player's
    /// fields, a pure client the networked <c>ClientNet.LocalState</c> slice. Returns true when NEW confirmed
    /// damage registered this frame (the crosshair hit-indication flash — QC crosshair.qc:387 reads the same
    /// accumulator).
    /// </summary>
    public bool Update(bool haveArc, int spectatee, float hitTime, float damageTotal, float typeHitTime, float killTime)
    {
        int mode = (int)CvarOr("cl_hitsound", 1f);
        float antispam = CvarOr("cl_hitsound_antispam_time", HitSoundLogic.DefaultAntispamTime);
        float maxPitch = CvarOr("cl_hitsound_max_pitch", HitSoundLogic.DefaultMaxPitch);
        float minPitch = CvarOr("cl_hitsound_min_pitch", HitSoundLogic.DefaultMinPitch);
        float nomDamage = CvarOr("cl_hitsound_nom_damage", HitSoundLogic.DefaultNomDamage);

        float now = Time.GetTicksMsec() / 1000f;
        HitSoundCues cues = _logic.Update(now, mode, antispam, maxPitch, minPitch, nomDamage,
            haveArc, spectatee, hitTime, damageTotal, typeHitTime, killTime);

        // The server flush gives at most one of these per SERVER frame, but two can still land in one client
        // frame. QC's call order is hit → typehit → kill (view.qc:956/967/974) and channel 0 layers rather
        // than replacing, so all of them play; matching the order keeps the mix identical to Base.
        if (cues.PlayHit)
            Play(ref _hitStream, ref _hitProbed, "misc/hit", cues.HitPitch);
        if (cues.PlayTypeHit)
            Play(ref _typeHitStream, ref _typeHitProbed, "misc/typehit", 1f);
        if (cues.PlayKill)
            Play(ref _killStream, ref _killProbed, "misc/kill", 1f);
        return cues.NewDamage;
    }

    /// <summary>Read a float cvar, falling back to the QC default when the cvar isn't registered/set
    /// (the store returns 0 for unknown names, which would silently disable/degenerate the curve).</summary>
    private float CvarOr(string name, float fallback)
    {
        if (_cvars is null) return fallback;
        string s = _cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : _cvars.GetFloat(name);
    }

    private void Play(ref AudioStream? stream, ref bool probed, string sample, float pitch)
    {
        if (stream is null)
        {
            if (probed) return; // known-missing sample — stay silent instead of re-probing every beep
            // Only LATCH the probe once a real loader has had its say. The HUD can be built before
            // AudioLoader is wired (NetGame only assigns it when the asset system exists), and latching on
            // that null-loader attempt made the sample silent for the whole session even after assets mount.
            bool hadLoader = AudioLoader is not null;
            stream = AudioLoader?.Invoke(sample);
            if (stream is null)
            {
                // Fallback: the res:// convention. Probe BOTH extensions like AssetLoader.LoadSound does —
                // the shipped typehit is .ogg, so a .wav-only fallback could never resolve it.
                foreach (string ext in new[] { ".ogg", ".wav" })
                {
                    string resPath = $"res://sound/{sample}{ext}";
                    if (!ResourceLoader.Exists(resPath)) continue;
                    try { stream = ResourceLoader.Load<AudioStream>(resPath); }
                    catch { /* silent */ }
                    if (stream is not null) break;
                }
            }
            if (stream is null)
            {
                probed = hadLoader; // genuinely missing → stop probing; no loader yet → retry next cue
                return;
            }
        }

        AudioStreamPlayer? voice = TakeVoice();
        if (voice is null)
            return;

        // NO Stop() on a busy voice unless the pool is exhausted: QC's CH_INFO is channel 0, which layers.
        voice.Stream = stream;
        voice.PitchScale = pitch;
        voice.Play();
    }

    /// <summary>A free voice, else the round-robin oldest (DP's SND_PickChannel steals when channels run
    /// out). Null until the pool can be parented into the tree.</summary>
    private AudioStreamPlayer? TakeVoice()
    {
        if (_parent is null || !GodotObject.IsInstanceValid(_parent))
            return null;

        for (int i = 0; i < _voices.Length; i++)
        {
            AudioStreamPlayer? v = _voices[i];
            if (v is null || !GodotObject.IsInstanceValid(v))
            {
                v = new AudioStreamPlayer { Name = $"HitSound{i}", Bus = "SFX" };
                // QC plays VOL_BASE; kept slightly quieter (the port's existing deliberate tweak) so the beep
                // doesn't mask the announcer.
                v.VolumeDb = Mathf.LinearToDb(0.7f);
                _parent.AddChild(v);
                _voices[i] = v;
                return v;
            }
            if (!v.Playing)
                return v;
        }

        // All busy — reuse the next in rotation (the approximate least-recently-started).
        AudioStreamPlayer? steal = _voices[_nextVoice];
        _nextVoice = (_nextVoice + 1) % _voices.Length;
        return steal;
    }
}
