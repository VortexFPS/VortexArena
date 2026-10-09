using Godot;
using VortexArena.Engine.Audio;

namespace VortexArena.Game.Audio;

/// <summary>
/// A looping sound that rides a node (a rocket's fly sound, a vehicle's engine): QuakeC's
/// "loopsound(entity, channel, sample, volume, attenuation)" for a client-side object that has no entity
/// number. One channel of the DarkPlaces mixer with CHANNELFLAG_FORCELOOP, moved to the node's place every
/// frame and stopped when the node leaves the tree.
/// </summary>
public sealed partial class DpLoopEmitter : Node3D
{
    public DpSfx? Sfx { get; set; }
    public float Volume { get; set; } = 1f;
    public float Attenuation { get; set; } = 0.5f;
    public float Speed { get; set; } = 1f;
    private int _channel = -1;

    private bool Alive => _channel >= 0 && Sfx is not null && DpAudio.Instance.Sound.IsChannelPlaying(_channel, Sfx, -1, 0);

    public override void _Ready()
    {
        if (Sfx is null) return;
        _channel = DpAudio.Instance.Sound.StartSound(-1, 0, Sfx, Coords.ToQuake(GlobalPosition), Volume, Attenuation, 0f, DpSoundSystem.ChannelFlagForceLoop, Speed);
    }

    public override void _Process(double delta)
    {
        using var _prof = VortexArena.Common.Diagnostics.Prof.Sample("dpaudio");
        if (Alive) DpAudio.Instance.Sound.SetChannelOrigin(_channel, Coords.ToQuake(GlobalPosition));
    }

    /// <summary>The loop's volume from here on (an engine's idle and running loops cross-fade).</summary>
    public void SetVolume(float volume)
    {
        Volume = volume;
        if (Alive) DpAudio.Instance.Sound.SetChannelVolume(_channel, volume);
    }

    public override void _ExitTree()
    {
        if (Alive) DpAudio.Instance.Sound.StopChannel(_channel);
        _channel = -1;
    }
}
