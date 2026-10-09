using Godot;

namespace VortexArena.Game.Audio;

/// <summary>
/// The stream whose playback is the DarkPlaces mixer itself (<see cref="DpAudio"/>): the engine's audio
/// server asks <see cref="DpMixerPlayback"/> for each block of frames while it mixes that block, on its own
/// audio thread, so nothing waits in a queue between the mixer and the server.
/// </summary>
public sealed partial class DpMixerStream : AudioStream
{
    internal DpAudio? Owner;

    public override AudioStreamPlayback _InstantiatePlayback() => new DpMixerPlayback { Owner = Owner };
    public override string _GetStreamName() => "DpMixer";
    public override double _GetLength() => 0;
    public override bool _IsMonophonic() => true;
}

/// <summary>
/// <para>The audio server's side of <see cref="DpMixerStream"/>. The engine calls a playback's
/// <c>_mix(AudioFrame *buffer, float rate_scale, int frames)</c> for every block it mixes. Godot's C# API does
/// not declare that virtual (its generator leaves out every method with a pointer parameter, and so
/// <c>AudioStreamPlayback._Mix</c> does not exist), but the engine's call does not depend on the declaration:
/// a virtual of a scripted object is first offered to its script by NAME, with the arguments as Variants, and
/// a pointer travels as an integer (core/variant/native_ptr.h). A C# method named exactly <c>_mix</c> taking
/// (long, float, int) is therefore what the audio thread calls. That is engine behaviour rather than a
/// documented contract, so <see cref="DpAudio"/> checks at start that the calls arrive and otherwise returns
/// to the queued generator.</para>
///
/// <para>The call runs on the engine's audio thread. It must not allocate, and it must not touch the scene
/// tree; it takes the mixer's own lock, which the game's frame holds only for the length of a sound start or
/// of the per-frame spatialisation.</para>
/// </summary>
public sealed partial class DpMixerPlayback : AudioStreamPlayback
{
    internal DpAudio? Owner;
    private bool _playing;
    private long _frames;

    public override void _Start(double fromPos) => _playing = true;
    public override void _Stop() => _playing = false;
    public override bool _IsPlaying() => _playing;
    public override int _GetLoopCount() => 0;
    public override void _Seek(double position) { }
    public override double _GetPlaybackPosition() => Owner is { MixRate: > 0 } owner ? System.Threading.Interlocked.Read(ref _frames) / (double)owner.MixRate : 0;

#pragma warning disable IDE1006 // the engine calls this by its own name for the virtual
    /// <summary>AudioStreamPlayback::_mix: <paramref name="buffer"/> is an AudioFrame* (two floats a frame). Returns the frames written.</summary>
    public long _mix(long buffer, float rateScale, int frames)
    {
        if (Owner is not { } owner || frames <= 0 || buffer == 0) return 0;
        int mixed = owner.MixDirect((System.IntPtr)buffer, frames);
        System.Threading.Interlocked.Add(ref _frames, mixed);
        return mixed;
    }
#pragma warning restore IDE1006
}
