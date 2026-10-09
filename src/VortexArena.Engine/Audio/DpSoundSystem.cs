// Port of Base/darkplaces/snd_main.c: SND_PickChannel, SND_Spatialize_WithSfx, S_PlaySfxOnChannel,
// S_StartSound_StartPosition_Flags (with the changevolume sample), S_StopChannel, S_SetChannelFlag,
// S_StopSound, S_StopAllSounds, S_PauseGameSounds, S_SetChannelVolume, S_SetChannelSpeed,
// S_GetChannelPosition, S_GetEntChannelPosition, S_StaticSound, S_LocalSoundEx and the spatialisation
// half of S_Update; and of snd_mix.c: S_MixToBuffer, S_SoftClipPaintBuffer, S_SetUnderwaterIntensity,
// S_UnderwaterFilter and the 16 bit half of S_ConvertPaintBuffer.
//
// Not ported: the four Quake 1 leaf ambient channels (S_UpdateAmbientSounds: water and sky hum from a
// Quake BSP's leaves), speaker layouts other than stereo, Dolby Pro Logic encoding.
using System;
using System.Numerics;

namespace VortexArena.Engine.Audio;

/// <summary>
/// DarkPlaces' sound channels and software mixer. One instance is one "sound system": a table of channels
/// (512 dynamic ones that sounds are started on, then the static ones a level adds), the per-channel
/// left/right volumes recomputed against the listener once a frame, and the mixer that paints them into
/// a stereo buffer at the output rate.
///
/// Start, stop and <see cref="Update"/> are called by the game's frame; <see cref="Mix"/> may be called from
/// another thread. One lock covers both, as SndSys_LockRenderBuffer does in DarkPlaces.
/// </summary>
public sealed class DpSoundSystem
{
    public const int ChannelFlagForceLoop = 1 << 1, ChannelFlagLocalSound = 1 << 2, ChannelFlagPaused = 1 << 3, ChannelFlagFullVolume = 1 << 4;
    /// <summary>qdefs.h MAX_DYNAMIC_CHANNELS and MAX_CHANNELS (less the four leaf ambients).</summary>
    public const int MaxDynamicChannels = 512, MaxChannels = 8192;
    /// <summary>MAX_EDICTS: the program's own entities are numbered from here, and a disowned channel is given this number.</summary>
    public const int MaxEdicts = 32768;
    private const int PaintBufferSize = 2048, FetchBufferSize = 4096;

    /// <summary>The sample named "": starting it on an entity channel changes the volume, speed, flags and attenuation of what plays there.</summary>
    public static readonly DpSfx ChangeVolume = new("", Array.Empty<short>(), 1, 48000);

    private sealed class Channel
    {
        public DpSfx? Sfx;
        public float BaseVolume, BaseSpeed, MixSpeed, DistFade, VolumeLeft, VolumeRight;
        public int Flags, EntNum, EntChannel;
        public Vector3 Origin;
        public double Position;

        public void Clear()
        {
            Sfx = null;
            BaseVolume = BaseSpeed = MixSpeed = DistFade = VolumeLeft = VolumeRight = 0;
            Flags = EntNum = EntChannel = 0;
            Origin = default;
            Position = 0;
        }
    }

    private readonly object _lock = new();
    private Channel[] _channels = new Channel[MaxDynamicChannels];
    private int _totalChannels = MaxDynamicChannels;
    private readonly float[] _paint = new float[PaintBufferSize * 2];
    private readonly float[] _fetch = new float[FetchBufferSize * 2 + 4];
    private DpListener _listener = DpListener.Identity;
    private float _limiterMax;
    private float _underwaterIntensity, _underwaterAlpha = 1f, _underwaterAccumLeft, _underwaterAccumRight;
    // S_Update's spatialisation-control values.
    private int _spatialMethod;   // 0 none, 1 log, 2 pow, 3 threshold
    private float _spatialPower, _spatialMin, _spatialDiff, _spatialOffset, _spatialFactor;

    public DpSoundSystem()
    {
        for (int i = 0; i < _channels.Length; i++) _channels[i] = new Channel();
    }

    /// <summary>The cvars. Read when a sound starts (distfade, the identical-sound offset) and on every <see cref="Update"/>.</summary>
    public DpSoundSettings Settings { get; set; } = new();
    public IDpSoundWorld World { get; set; } = new DpNullSoundWorld();
    /// <summary>A number in [0, 1): lhrandom's source for the identical-sound offset.</summary>
    public Func<double> Random { get; set; } = System.Random.Shared.NextDouble;
    /// <summary>"cl.mtime[0] - cl.mtime[1]" while connected, else 0: the length of a server tick, which bounds the identical-sound offset.</summary>
    public double ServerTickSeconds { get; set; }

    /// <summary>cls.soundstats: channels with a sample, and of those the ones with a volume.</summary>
    public int TotalSounds { get; private set; }
    public int MixedSounds { get; private set; }
    public int StaticChannels { get { lock (_lock) return _totalChannels - MaxDynamicChannels; } }

    // ---------------------------------------------------------------------------------------------------
    //  Starting and stopping
    // ---------------------------------------------------------------------------------------------------

    private static bool IsChanSingle(int n) => n is >= 1 and <= 127;

    // SND_PickChannel.
    private int PickChannel(int entnum, int entchannel)
    {
        int firstToDie = -1, firstLifeLeft = 0x7fffffff;
        // "entity channels try to replace the existing sound on the channel"
        if (IsChanSingle(entchannel))
            for (int i = 0; i < MaxDynamicChannels; i++)
            {
                Channel ch = _channels[i];
                if (ch.EntNum == entnum && ch.EntChannel == entchannel)
                {
                    ch.Sfx = null;   // "always override sound from same entity"
                    return i;
                }
            }
        IDpSoundWorld world = World;
        bool newIsView = world.IsViewEntity(entnum);
        for (int i = 0; i < MaxDynamicChannels; i++)
        {
            Channel ch = _channels[i];
            DpSfx? sfx = ch.Sfx;
            if (sfx is null) return i;
            // "don't let monster sounds override player sounds"
            if (world.IsViewEntity(ch.EntNum) && !newIsView) continue;
            // "don't override looped sounds"
            if ((ch.Flags & ChannelFlagForceLoop) != 0 || sfx.LoopStart < sfx.TotalLength) continue;
            int lifeLeft = (int)(sfx.TotalLength - ch.Position);
            if (lifeLeft < firstLifeLeft)
            {
                firstLifeLeft = lifeLeft;
                firstToDie = i;
            }
        }
        if (firstToDie == -1) return -1;
        _channels[firstToDie].Sfx = null;
        return firstToDie;
    }

    // S_PlaySfxOnChannel.
    private bool PlaySfxOnChannel(DpSfx sfx, Channel target, int flags, Vector3 origin, float volume, float attenuation, bool isStatic, int entnum, int entchannel, int startPos, float speed)
    {
        DpSoundSettings s = Settings;
        if (sfx.LoopStart < sfx.TotalLength || (flags & ChannelFlagForceLoop) != 0)
        {
            if (!s.StartLoopingSounds) return false;
        }
        else if (!s.StartNonLoopingSounds) return false;

        target.Clear();
        target.Origin = origin;
        target.Flags = flags;
        target.Position = startPos;
        target.EntNum = entnum;
        target.EntChannel = entchannel;
        target.DistFade = isStatic ? attenuation / (64.0f * s.SoundRadius) : attenuation / s.SoundRadius;
        target.BaseVolume = volume;
        target.BaseSpeed = speed;
        Spatialize(target, isStatic, sfx);
        target.Sfx = sfx;
        return true;
    }

    /// <summary>
    /// S_StartSound_StartPosition_Flags. Returns the channel index, or -1. <paramref name="sfx"/> may be
    /// <see cref="ChangeVolume"/>. <paramref name="startPosition"/> is in seconds.
    /// </summary>
    public int StartSound(int entnum, int entchannel, DpSfx? sfx, Vector3 origin, float volume, float attenuation, float startPosition = 0, int flags = 0, float speed = 1f)
    {
        if (sfx is null) return -1;
        lock (_lock)
        {
            DpSoundSettings s = Settings;
            if (ReferenceEquals(sfx, ChangeVolume))
            {
                if (!IsChanSingle(entchannel)) return -1;
                for (int i = 0; i < MaxDynamicChannels; i++)
                {
                    Channel ch = _channels[i];
                    if (ch.EntNum == entnum && ch.EntChannel == entchannel)
                    {
                        ch.BaseVolume = volume;
                        ch.BaseSpeed = speed;
                        // "for(i = 1; i > 0 && (i <= flags || i <= (int) channels[ch_idx].flags); i <<= 1)": only the settable flags change.
                        for (int bit = 1; bit > 0 && (bit <= flags || bit <= ch.Flags); bit <<= 1)
                            if (((flags ^ ch.Flags) & bit) != 0 && bit is ChannelFlagForceLoop or ChannelFlagPaused or ChannelFlagFullVolume or ChannelFlagLocalSound)
                                ch.Flags = (flags & bit) != 0 ? ch.Flags | bit : ch.Flags & ~bit;
                        ch.DistFade = attenuation / s.SoundRadius;
                        Spatialize(ch, false, ch.Sfx);
                        return i;
                    }
                }
                return -1;
            }
            // "if (sfx->fetcher == NULL) return -1": a file that is not there.
            if (sfx.Failed || !sfx.FormatKnown) return -1;

            int index = PickChannel(entnum, entchannel);
            if (index < 0) return -1;
            Channel target = _channels[index];

            // "if an identical sound has also been started this frame, offset the pos a bit to keep it
            // from just making the first one louder"
            int startPos = (int)(startPosition * sfx.Rate);
            if (startPos == 0)
                for (int i = 0; i < MaxDynamicChannels; i++)
                {
                    Channel check = _channels[i];
                    if (ReferenceEquals(check, target)) continue;
                    if (ReferenceEquals(check.Sfx, sfx) && check.Position == 0 && check.BaseSpeed == speed)
                    {
                        float maxTime = s.IdenticalSoundRandomizationTime;
                        float maxTicsDelta = (float)(s.IdenticalSoundRandomizationTics * ServerTickSeconds);
                        float maxDelta = maxTicsDelta == 0 || MathF.Abs(maxTicsDelta) > MathF.Abs(maxTime)
                            ? maxTime
                            : MathF.Abs(maxTicsDelta) * (maxTime > 0 ? 1 : -1);
                        // "use negative pos offset to delay this sound effect": lhrandom(0, maxdelta * speed).
                        startPos = (int)((Random() * (1.0 - 1.0 / 32768.0) + 0.5 / 32768.0) * (maxDelta * sfx.Rate));
                        break;
                    }
                }

            return PlaySfxOnChannel(sfx, target, flags, origin, volume, attenuation, false, entnum, entchannel, startPos, speed) ? index : -1;
        }
    }

    /// <summary>S_StaticSound: a looping sound fixed in the world, on a channel of its own after the dynamic ones. <paramref name="attenuation"/> is the wire value (64 times the QuakeC one).</summary>
    public int StaticSound(DpSfx? sfx, Vector3 origin, float volume, float attenuation)
    {
        if (sfx is null || sfx.Failed || !sfx.FormatKnown) return -1;
        lock (_lock)
        {
            if (_totalChannels == MaxChannels) return -1;
            if (_totalChannels == _channels.Length)
            {
                Channel[] grown = new Channel[Math.Min(MaxChannels, _channels.Length * 2)];
                Array.Copy(_channels, grown, _channels.Length);
                for (int i = _channels.Length; i < grown.Length; i++) grown[i] = new Channel();
                _channels = grown;
            }
            int index = _totalChannels++;
            if (!PlaySfxOnChannel(sfx, _channels[index], ChannelFlagForceLoop, origin, volume, attenuation, true, 0, 0, 0, 1.0f))
            {
                _totalChannels--;
                return -1;
            }
            return index;
        }
    }

    /// <summary>S_LocalSoundEx: on the view entity, unattenuated, and not paused with the game. <paramref name="viewEntity"/> is cl.viewentity.</summary>
    public int LocalSound(DpSfx? sfx, int channel, float volume, int viewEntity)
    {
        lock (_lock)
        {
            int index = StartSound(viewEntity, channel, sfx, Vector3.Zero, volume, 0);
            if (index >= 0) _channels[index].Flags |= ChannelFlagLocalSound;
            return index;
        }
    }

    /// <summary>S_StopChannel.</summary>
    public void StopChannel(int index)
    {
        lock (_lock)
            if ((uint)index < (uint)_totalChannels) _channels[index].Sfx = null;
    }

    /// <summary>S_StopSound: the first dynamic channel with that entity and entity channel.</summary>
    public void StopSound(int entnum, int entchannel)
    {
        lock (_lock)
            for (int i = 0; i < MaxDynamicChannels; i++)
                if (_channels[i].EntNum == entnum && _channels[i].EntChannel == entchannel)
                {
                    _channels[i].Sfx = null;
                    return;
                }
    }

    /// <summary>S_StopAllSounds: everything, the static sounds included ("no statics").</summary>
    public void StopAllSounds()
    {
        lock (_lock)
        {
            for (int i = 0; i < _totalChannels; i++) _channels[i].Clear();
            _totalChannels = MaxDynamicChannels;
        }
    }

    /// <summary>S_PauseGameSounds: every channel that is not a local sound.</summary>
    public void PauseGameSounds(bool paused)
    {
        lock (_lock)
            for (int i = 0; i < _totalChannels; i++)
            {
                Channel ch = _channels[i];
                if (ch.Sfx is not null && (ch.Flags & ChannelFlagLocalSound) == 0)
                    ch.Flags = paused ? ch.Flags | ChannelFlagPaused : ch.Flags & ~ChannelFlagPaused;
            }
    }

    public bool SetChannelFlag(int index, int flag, bool value)
    {
        if (flag is not (ChannelFlagForceLoop or ChannelFlagPaused or ChannelFlagFullVolume or ChannelFlagLocalSound)) return false;
        lock (_lock)
        {
            if ((uint)index >= (uint)_totalChannels) return false;
            Channel ch = _channels[index];
            ch.Flags = value ? ch.Flags | flag : ch.Flags & ~flag;
            return true;
        }
    }

    public void SetChannelVolume(int index, float volume)
    {
        lock (_lock)
            if ((uint)index < (uint)_totalChannels) _channels[index].BaseVolume = volume;
    }

    public void SetChannelSpeed(int index, float speed)
    {
        lock (_lock)
            if ((uint)index < (uint)_totalChannels) _channels[index].BaseSpeed = speed;
    }

    /// <summary>A fixed sound's place, for a caller that moves it itself (a channel on entity 0 or below follows nothing).</summary>
    public void SetChannelOrigin(int index, Vector3 origin)
    {
        lock (_lock)
            if ((uint)index < (uint)_totalChannels) _channels[index].Origin = origin;
    }

    /// <summary>Is a sample still playing on this channel index?</summary>
    public bool IsChannelActive(int index)
    {
        lock (_lock) return (uint)index < (uint)_totalChannels && _channels[index].Sfx is not null;
    }

    /// <summary>Is this channel still playing this sample for this entity channel (the index was not given to another sound)?</summary>
    public bool IsChannelPlaying(int index, DpSfx sfx, int entnum, int entchannel)
    {
        lock (_lock)
        {
            if ((uint)index >= (uint)_totalChannels) return false;
            Channel ch = _channels[index];
            return ReferenceEquals(ch.Sfx, sfx) && ch.EntNum == entnum && ch.EntChannel == entchannel;
        }
    }

    /// <summary>S_GetChannelPosition: seconds into the sample, or -1.</summary>
    public float GetChannelPosition(int index)
    {
        lock (_lock)
        {
            if ((uint)index >= (uint)_totalChannels || _channels[index].Sfx is not { } sfx) return -1;
            return (float)(_channels[index].Position / sfx.Rate);
        }
    }

    /// <summary>S_GetEntChannelPosition.</summary>
    public float GetEntChannelPosition(int entnum, int entchannel)
    {
        lock (_lock)
        {
            for (int i = 0; i < _totalChannels; i++)
                if (_channels[i].EntNum == entnum && _channels[i].EntChannel == entchannel)
                    return _channels[i].Sfx is { } sfx ? (float)(_channels[i].Position / sfx.Rate) : -1;
            return -1;
        }
    }

    /// <summary>The left and right volume a channel is being mixed at (tests, the debug overlay).</summary>
    public bool TryGetChannelVolumes(int index, out float left, out float right)
    {
        lock (_lock)
        {
            left = right = 0;
            if ((uint)index >= (uint)_totalChannels || _channels[index].Sfx is null) return false;
            left = _channels[index].VolumeLeft;
            right = _channels[index].VolumeRight;
            return true;
        }
    }

    // ---------------------------------------------------------------------------------------------------
    //  Spatialisation
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The volume part of SND_Spatialize_WithSfx: a channel's base volume times the cvars of its class,
    /// the clamps, the master volume and ReplayGain. <paramref name="entnum"/> selects the class
    /// (program entity, world, player, other).
    /// </summary>
    public static float MasterVolume(DpSoundSettings s, float baseVolume, bool isStatic, int flags, int entnum, int entchannel, int maxClients, DpSfx? sfx)
    {
        float mastervol = baseVolume;
        if (isStatic) mastervol *= s.StaticVolume;
        else if ((flags & ChannelFlagFullVolume) == 0)
        {
            if ((uint)entchannel < 8u)
            {
                if (entnum >= MaxEdicts) mastervol *= s.CsqcChannelVolume[entchannel];
                else if (entnum == 0) mastervol *= s.WorldChannelVolume[entchannel];
                else if (entnum > 0 && entnum <= maxClients) mastervol *= s.PlayerChannelVolume[entchannel];
                else mastervol *= s.EntChannelVolume[entchannel];
                mastervol *= s.ChannelVolume[entchannel];
            }
            else mastervol *= s.ExtraChannelVolume?.Invoke(Math.Abs(entchannel)) ?? 1.0f;
        }
        // "If this channel does not manage its own volume (like CD tracks)"
        if ((flags & ChannelFlagFullVolume) == 0) mastervol *= s.Volume;
        if (s.MaxChannelVolume > 0) mastervol = Math.Clamp(mastervol, 0.0f, 10.0f * s.MaxChannelVolume);
        // "always apply "master""
        mastervol *= s.MasterVolume;
        // "add in ReplayGain very late; prevent clipping when close"
        if (sfx is not null && sfx.VolumePeak > 0)
        {
            mastervol *= sfx.VolumeMult;
            if (s.MaxChannelVolume > 0 && mastervol * sfx.VolumePeak > s.MaxChannelVolume)
                mastervol = s.MaxChannelVolume / sfx.VolumePeak;
        }
        if (s.MaxChannelVolume > 0) mastervol = MathF.Min(mastervol, s.MaxChannelVolume);
        return MathF.Max(0.0f, mastervol);
    }

    /// <summary>"pow(1 - min(1, f), exponent) * pow(0.1, 0.1 * decibel * f)" with f = distance * distfade.</summary>
    public static double DistanceFactor(DpSoundSettings s, double distance, double distFade)
    {
        double f = distance * distFade;
        return (s.AttenuationExponent == 0 ? 1.0 : Math.Pow(1.0 - Math.Min(1.0, f), s.AttenuationExponent))
             * (s.AttenuationDecibel == 0 ? 1.0 : Math.Pow(0.1, 0.1 * s.AttenuationDecibel * f));
    }

    // The values S_Update derives from the snd_spatialization_* cvars.
    private void UpdateSpatialControl(DpSoundSettings s)
    {
        _spatialMin = s.SpatializationMin;
        _spatialDiff = s.SpatializationMax - _spatialMin;
        if (!s.SpatializationControl)
        {
            _spatialMethod = 0;
            return;
        }
        _spatialPower = s.SpatializationPower;
        double minTrans, maxTrans;
        if (_spatialPower == 0)
        {
            _spatialMethod = 1;
            minTrans = Math.Log(Math.Max(1, s.SpatializationMinRadius));
            maxTrans = Math.Log(Math.Max(1, s.SpatializationMaxRadius));
        }
        else
        {
            _spatialMethod = 2;
            minTrans = Math.Pow(s.SpatializationMinRadius, _spatialPower);
            maxTrans = Math.Pow(s.SpatializationMaxRadius, _spatialPower);
        }
        if (minTrans - maxTrans == 0)
        {
            _spatialMethod = 3;
            _spatialOffset = s.SpatializationMinRadius;
        }
        else
        {
            _spatialOffset = (float)minTrans;
            _spatialFactor = (float)(1 / (maxTrans - minTrans));
        }
    }

    // SND_Spatialize_WithSfx.
    private void Spatialize(Channel ch, bool isStatic, DpSfx? sfx)
    {
        DpSoundSettings s = Settings;
        IDpSoundWorld world = World;

        // "update sound origin if we know about the entity"
        if (ch.EntNum > 0 && s.SoundsMoveWithEntities && ch.EntNum != MaxEdicts)
        {
            Vector3 origin = ch.Origin;
            switch (world.EntityOrigin(ch.EntNum, ref origin))
            {
                case DpEntityOrigin.Moved: ch.Origin = origin; break;
                case DpEntityOrigin.Removed: ch.EntNum = MaxEdicts; break;
            }
        }

        float mastervol = MasterVolume(s, ch.BaseVolume, isStatic, ch.Flags, ch.EntNum, ch.EntChannel, world.MaxClients, sfx);
        ch.MixSpeed = ch.BaseSpeed;

        // "anything coming from the view entity will always be full volume";
        // "make sounds with ATTN_NONE have no spatialization". The stereo layout's ambientvolume is 1.
        if (ch.DistFade == 0 || world.IsViewEntity(ch.EntNum))
        {
            ch.VolumeLeft = ch.VolumeRight = mastervol;
            return;
        }

        Vector3 toSource = ch.Origin - _listener.Origin;
        float dist = toSource.Length();
        float intensity = mastervol * (float)DistanceFactor(s, dist, ch.DistFade);
        if (!(intensity > 0))
        {
            ch.VolumeLeft = ch.VolumeRight = 0;
            return;
        }
        if (s.Occlusion != 0 && world.Occluded(_listener.Origin, ch.Origin, s.Occlusion)) intensity *= 0.5f;

        // The source in the listener's frame, normalised (a zero vector stays zero): x forward, y left, z up.
        Vector3 local = new(Vector3.Dot(toSource, _listener.Forward), Vector3.Dot(toSource, _listener.Left), Vector3.Dot(toSource, _listener.Up));
        float length = local.Length();
        if (length != 0) local /= length;
        float f;
        switch (_spatialMethod)
        {
            case 1:
                f = dist == 0
                    ? _spatialMin + _spatialDiff * (_spatialFactor < 0 ? 1 : 0)
                    : _spatialMin + _spatialDiff * Math.Clamp((MathF.Log(dist) - _spatialOffset) * _spatialFactor, 0, 1);
                local *= f;
                break;
            case 2:
                f = (MathF.Pow(dist, _spatialPower) - _spatialOffset) * _spatialFactor;
                local *= _spatialMin + _spatialDiff * Math.Clamp(f, 0, 1);
                break;
            case 3:
                local *= _spatialMin + _spatialDiff * (dist < _spatialOffset ? 1 : 0);
                break;
        }
        // The stereo layout: two ears turned 90 and 270 degrees, "dotscale" 0.5 and "dotbias" 0.5:
        // volume = intensity * max(0, x * 0.5 + 0.5) with x the source's component towards that ear.
        float left = intensity * MathF.Max(0, local.Y * 0.5f + 0.5f);
        float right = intensity * MathF.Max(0, -local.Y * 0.5f + 0.5f);
        if (s.SwapStereo) (left, right) = (right, left);
        ch.VolumeLeft = left;
        ch.VolumeRight = right;
    }

    /// <summary>
    /// The per-frame half of S_Update: take the listener, re-spatialise every channel that has a sample,
    /// fold each static sound into the first static channel playing the same sample ("so we don't mix five
    /// torches every frame"), and move the under-water filter towards its target.
    /// <paramref name="realFrameTime"/> is cl.realframetime; <paramref name="underwater"/> is cl.view_underwater.
    /// </summary>
    public void Update(in DpListener listener, double realFrameTime = 0, bool underwater = false)
    {
        lock (_lock)
        {
            DpSoundSettings s = Settings;
            _listener = listener;
            UpdateSpatialControl(s);

            // S_SetUnderwaterIntensity.
            float target = underwater ? Math.Clamp(s.WaterFx, 0f, 2f) : 0f;
            if (_underwaterIntensity < target) _underwaterIntensity = MathF.Min(_underwaterIntensity + (float)realFrameTime * 4f, target);
            else if (_underwaterIntensity > target) _underwaterIntensity = MathF.Max(_underwaterIntensity - (float)realFrameTime * 4f, target);
            _underwaterAlpha = _underwaterIntensity != 0 ? MathF.Exp(-_underwaterIntensity * MathF.Log(12f)) : 1f;

            int total = 0, mixed = 0;
            Channel? combine = null;
            for (int i = 0; i < _totalChannels; i++)
            {
                Channel ch = _channels[i];
                if (ch.Sfx is null) continue;
                total++;
                Spatialize(ch, i >= MaxDynamicChannels, ch.Sfx);
                if (i > MaxDynamicChannels)
                {
                    if (ch.VolumeLeft == 0 && ch.VolumeRight == 0) continue;
                    if (!(combine is not null && !ReferenceEquals(combine, ch) && ReferenceEquals(combine.Sfx, ch.Sfx)))
                    {
                        combine = null;
                        for (int j = MaxDynamicChannels; j < i; j++)
                            if (ReferenceEquals(_channels[j].Sfx, ch.Sfx))
                            {
                                combine = _channels[j];
                                break;
                            }
                    }
                    if (combine is not null && !ReferenceEquals(combine, ch) && ReferenceEquals(combine.Sfx, ch.Sfx))
                    {
                        combine.VolumeLeft += ch.VolumeLeft;
                        combine.VolumeRight += ch.VolumeRight;
                        ch.VolumeLeft = ch.VolumeRight = 0;
                    }
                }
                if (ch.VolumeLeft != 0 || ch.VolumeRight != 0) mixed++;
            }
            TotalSounds = total;
            MixedSounds = mixed;
        }
    }

    // ---------------------------------------------------------------------------------------------------
    //  Mixing
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// S_MixToBuffer: paint <paramref name="frames"/> stereo frames at <paramref name="outputRate"/> into
    /// <paramref name="output"/> (interleaved left, right; overwritten). Each channel is resampled by linear
    /// interpolation with a 16.16 fixed-point step, exactly as DarkPlaces does; then the limiter
    /// (snd_softclip), the under-water filter and the 16 bit conversion's clamp are applied.
    /// </summary>
    public void Mix(Span<float> output, int frames, int outputRate)
    {
        lock (_lock)
        {
            int done = 0;
            while (done < frames)
            {
                int block = Math.Min(frames - done, PaintBufferSize);
                MixBlock(block, outputRate);
                _paint.AsSpan(0, block * 2).CopyTo(output.Slice(done * 2, block * 2));
                done += block;
            }
        }
    }

    private void MixBlock(int totalMixFrames, int outputRate)
    {
        DpSoundSettings s = Settings;
        float[] paint = _paint, fetchBuffer = _fetch;
        Array.Clear(paint, 0, totalMixFrames * 2);

        for (int channelIndex = 0; channelIndex < _totalChannels; channelIndex++)
        {
            Channel ch = _channels[channelIndex];
            DpSfx? sfx = ch.Sfx;
            if (sfx is null) continue;
            if ((ch.Flags & ChannelFlagPaused) != 0) continue;
            int totalLength = sfx.TotalLength;
            if (totalLength == 0)
            {
                // DarkPlaces leaves an empty sample on its channel for good; nothing can come of it, so it is let go.
                if (sfx.Failed || sfx.Complete) ch.Sfx = null;
                continue;
            }

            double posd = ch.Position;
            // "speedd = ch->mixspeed * sfx->format.speed / snd_renderbuffer->format.speed": float arithmetic in C
            // (a float times two unsigned ints), widened afterwards. Done in double, 44100 / 48000 comes out
            // 2e-8 different and the position drifts from DarkPlaces' by a thousandth of a sample a second.
            float speedf = ch.MixSpeed * (float)sfx.Rate;
            speedf /= (float)outputRate;
            double speedd = speedf;
            if (!(speedd > 0)) continue;
            float volLeft = ch.VolumeLeft, volRight = ch.VolumeRight;
            float maxvol = MathF.Max(volLeft, volRight);
            bool silent = s.OutputWidth switch { 1 => maxvol < 1.0f / 256.0f, 2 => maxvol < 1.0f / 65536.0f, _ => maxvol < 1.0e-13f };

            int loopStart = sfx.LoopStart < totalLength ? sfx.LoopStart : (ch.Flags & ChannelFlagForceLoop) != 0 ? 0 : totalLength;
            bool looping = loopStart < totalLength;

            // A sample still being decoded: hold the channel until what this block reads is there.
            if (!sfx.Complete)
            {
                double last = Math.Max(0, posd) + (totalMixFrames + 1) * speedd + 2;
                if (sfx.AvailableFrames < Math.Min(totalLength, (long)Math.Ceiling(last)) || (looping && last >= totalLength)) continue;
            }

            short[] data = sfx.Data;
            int channels = sfx.Channels;
            int paintAt = 0, istartframe = 0, count;
            for (int wantframes = totalMixFrames; wantframes > 0; posd += count * speedd, wantframes -= count)
            {
                // "for a delayed sound we have to eat into the delay first". DarkPlaces does not move its
                // paint pointer here, so a sound whose delay ends inside a block is painted from the START
                // of the block and the block's last frames go without it: up to one block (43 ms at 48 kHz)
                // early, then a gap of the same length. Kept, because it is what DarkPlaces puts out; a
                // delay arises only when identical sounds start together (snd_identicalsoundrandomization_time < 0).
                if (posd < 0)
                {
                    count = Math.Clamp((int)Math.Floor(-posd / speedd) + 1, 1, wantframes);
                    continue;
                }

                // "compute a fetch size that won't overflow our buffer"
                count = wantframes;
                int ilengthframes;
                for (;;)
                {
                    istartframe = (int)Math.Floor(posd);
                    int iendframe = (int)Math.Floor(posd + (count - 1) * speedd);
                    ilengthframes = count > 1 ? iendframe - istartframe + 2 : 2;
                    if (ilengthframes <= FetchBufferSize) break;
                    count -= count >> 2;
                }

                if (!silent) Array.Clear(fetchBuffer, 0, ilengthframes * channels);

                // "if looping, do multiple fetches"
                int fetched = 0;
                for (;;)
                {
                    int fetch = Math.Min(ilengthframes - fetched, totalLength - istartframe);
                    if (fetch > 0)
                    {
                        if (!silent)
                        {
                            int from = istartframe * channels, to = fetched * channels, n = fetch * channels;
                            for (int k = 0; k < n; k++) fetchBuffer[to + k] = data[from + k] * (1.0f / 32768.0f);
                        }
                        istartframe += fetch;
                        fetched += fetch;
                    }
                    if (istartframe == totalLength && looping && fetched < ilengthframes)
                    {
                        posd += loopStart - totalLength;
                        istartframe = loopStart;
                    }
                    else break;
                }

                int at = 0;
                int indexfrac = (int)Math.Floor((posd - Math.Floor(posd)) * 65536.0);
                int indexfracstep = (int)Math.Floor(speedd * 65536.0);
                if (!silent)
                {
                    if (channels == 2)
                        for (int i = 0; i < count; i++, paintAt++)
                        {
                            float lerp1 = indexfrac * (1.0f / 65536.0f), lerp0 = 1.0f - lerp1;
                            float sample0 = fetchBuffer[at] * lerp0 + fetchBuffer[at + 2] * lerp1;
                            float sample1 = fetchBuffer[at + 1] * lerp0 + fetchBuffer[at + 3] * lerp1;
                            paint[paintAt * 2] += sample0 * volLeft;
                            paint[paintAt * 2 + 1] += sample1 * volRight;
                            indexfrac += indexfracstep;
                            at += 2 * (indexfrac >> 16);
                            indexfrac &= 0xFFFF;
                        }
                    else
                        for (int i = 0; i < count; i++, paintAt++)
                        {
                            float lerp1 = indexfrac * (1.0f / 65536.0f), lerp0 = 1.0f - lerp1;
                            float sample0 = fetchBuffer[at] * lerp0 + fetchBuffer[at + 1] * lerp1;
                            paint[paintAt * 2] += sample0 * volLeft;
                            paint[paintAt * 2 + 1] += sample0 * volRight;
                            indexfrac += indexfracstep;
                            at += indexfrac >> 16;
                            indexfrac &= 0xFFFF;
                        }
                }
            }
            ch.Position = posd;
            if (!looping && istartframe == totalLength) ch.Sfx = null;
        }

        // S_SoftClipPaintBuffer: "let's do a simple limiter instead, seems to sound better".
        if ((s.SoftClip == 1 && s.OutputWidth <= 2) || s.SoftClip > 1)
        {
            float maxvol = MathF.Max(1.0f, _limiterMax * (1.0f - totalMixFrames / (0.4f * outputRate)));
            for (int i = 0; i < totalMixFrames * 2; i++)
            {
                float x = paint[i];
                if (MathF.Abs(x) > maxvol) maxvol = MathF.Abs(x);
                paint[i] = x / maxvol;
            }
            _limiterMax = maxvol;
        }

        // S_UnderwaterFilter.
        if (_underwaterIntensity == 0)
        {
            if (totalMixFrames > 0)
            {
                _underwaterAccumLeft = paint[(totalMixFrames - 1) * 2];
                _underwaterAccumRight = paint[(totalMixFrames - 1) * 2 + 1];
            }
        }
        else
        {
            float alpha = _underwaterAlpha, left = _underwaterAccumLeft, right = _underwaterAccumRight;
            for (int i = 0; i < totalMixFrames; i++)
            {
                left += alpha * (paint[i * 2] - left);
                right += alpha * (paint[i * 2 + 1] - right);
                paint[i * 2] = left;
                paint[i * 2 + 1] = right;
            }
            _underwaterAccumLeft = left;
            _underwaterAccumRight = right;
        }

        // S_ConvertPaintBuffer, 16 bit: "val = (int)(sample * 32768.0f); bound(-32768, val, 32767)".
        if (s.OutputWidth == 2)
            for (int i = 0; i < totalMixFrames * 2; i++)
                paint[i] = Math.Clamp((int)(paint[i] * 32768.0f), -32768, 32767) * (1.0f / 32768.0f);
    }
}
