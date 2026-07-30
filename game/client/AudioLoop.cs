using Godot;

namespace VortexArena.Game.Client;

/// <summary>
/// Shared looping-audio helper — the one place that turns a decoded <see cref="AudioStream"/> into a seamless
/// loop for the positional looping sources (the Arc beam loop in <see cref="ClientWorld"/>, the vehicle engine
/// crossfade in <see cref="VehicleVisuals"/>). DP's <c>loopsound</c> equivalent on the render side.
///
/// The streams come from <c>AssetLoader.LoadSound</c>, which returns a CACHED, SHARED, immutable instance — so
/// a one-shot reusing the same sample must not inherit a loop flag. We therefore <see cref="Resource.Duplicate"/>
/// the stream before flipping its native loop bit. Ogg/MP3 loop seamlessly that way; other types (WAV) report
/// <c>nativeLoop=false</c> so the caller wires a <c>Finished → Play</c> fallback.
/// </summary>
internal static class AudioLoop
{
    /// <summary>
    /// Return a looping form of <paramref name="stream"/> — a duplicate with its native loop flag set for
    /// Ogg/MP3 (<paramref name="nativeLoop"/> true, seamless), or the stream unchanged for types without a loop
    /// flag (<paramref name="nativeLoop"/> false; the caller must re-play on <c>Finished</c>).
    /// </summary>
    public static AudioStream MakeLooping(AudioStream stream, out bool nativeLoop)
    {
        switch (stream)
        {
            case AudioStreamOggVorbis ogg:
            {
                var dup = (AudioStreamOggVorbis)ogg.Duplicate();
                dup.Loop = true;
                nativeLoop = true;
                return dup;
            }
            case AudioStreamMP3 mp3:
            {
                var dup = (AudioStreamMP3)mp3.Duplicate();
                dup.Loop = true;
                nativeLoop = true;
                return dup;
            }
            default:
                // WAV (and anything else without a loop flag): leave the cached stream untouched and let the
                // caller re-trigger it on Finished. (A duplicated WAV could set LoopMode, but the Finished
                // fallback is simpler and type-agnostic.)
                nativeLoop = false;
                return stream;
        }
    }
}
