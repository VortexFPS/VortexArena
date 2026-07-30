using System;
using System.Numerics;

namespace XonoticGodot.Net;

/// <summary>
/// The wire form of one positional sound record (DP <c>SV_StartSound</c>) inside a <c>SoundBundle</c>: the
/// sample path, its world origin, volume/attenuation, the channel, the SOURCE entity's network id, and the
/// loop/stop flags. Godot-free so the server encoder (<c>ServerNet.WriteSound</c>) and the client decoder
/// (<c>ClientNet.HandleSoundBundle</c>) share ONE layout — and so the round-trip is unit-testable headlessly,
/// exactly like <see cref="EffectNetProtocol"/> / <c>MoveVarsBlock</c> living in this assembly.
///
/// The <see cref="SourceNetId"/> + <see cref="Loop"/>/<see cref="Stop"/> trio is what lets the client key a
/// persistent looping <c>AudioStreamPlayer3D</c> by <c>(entity, channel)</c> and replace/stop it (the Arc beam
/// loop, vehicle engines) — DP's entity+channel sound model. A one-shot leaves both flags false and the client
/// ignores the id (it just plays on the pooled spatial source).
/// </summary>
public struct SoundWire
{
    public string Sample;
    public Vector3 Origin;
    public float Volume;
    public float Attenuation;

    /// <summary>The emitting channel (<c>SoundChannel</c> as a byte): the key, with <see cref="SourceNetId"/>, for a loop/stop.</summary>
    public int Channel;

    /// <summary>The emitter entity's network id (the player/non-player net id the snapshot uses). The
    /// <c>(SourceNetId, Channel)</c> pair keys a looping sound so the client can follow/replace/stop it. 0 = none.</summary>
    public int SourceNetId;

    /// <summary>A persistent looping sound (QC <c>loopsound</c>): replace-by-<c>(entity, channel)</c>, not a one-shot.</summary>
    public bool Loop;

    /// <summary>End the sound on <c>(entity, channel)</c> — DP <c>sound(e, ch, SND_Null)</c>. Carries no sample.</summary>
    public bool Stop;

    /// <summary>Pitch scale (DP percentage encoding: wire byte 100 = 1.0x normal). Default 1.0f — all existing
    /// sounds play unchanged. Range 0.0–2.55 (byte 0–255).</summary>
    public float Pitch;

    private const int FlagLoop = 1 << 0;
    private const int FlagStop = 1 << 1;
    private const int FlagPitch = 1 << 2;

    /// <summary>Encode this record (inverse of <see cref="Read"/>). Layout: sample, origin xyz (raw floats),
    /// volume, attenuation, channel byte, source net-id (ushort), flags byte, [pitch byte if FlagPitch].</summary>
    public readonly void Write(BitWriter w)
    {
        w.WriteString(Sample ?? "");
        w.WriteFloat(Origin.X);
        w.WriteFloat(Origin.Y);
        w.WriteFloat(Origin.Z);
        w.WriteFloat(Volume);
        w.WriteFloat(Attenuation);
        w.WriteSByte(Channel);
        w.WriteUShort(SourceNetId);
        bool hasPitch = Pitch != 0f && Math.Abs(Pitch - 1f) > 0.001f;
        int flags = (Loop ? FlagLoop : 0) | (Stop ? FlagStop : 0) | (hasPitch ? FlagPitch : 0);
        w.WriteByte(flags);
        if (hasPitch)
            w.WriteByte(Math.Clamp((int)(Pitch * 100f), 0, 255));
    }

    /// <summary>Decode one record (inverse of <see cref="Write"/>). The caller checks <see cref="BitReader.BadRead"/>
    /// after this returns to discard a truncated record.</summary>
    public static SoundWire Read(ref BitReader r)
    {
        var s = new SoundWire
        {
            Sample = r.ReadString(),
            Origin = new Vector3(r.ReadFloat(), r.ReadFloat(), r.ReadFloat()),
            Volume = r.ReadFloat(),
            Attenuation = r.ReadFloat(),
            Channel = r.ReadSByte(),
            SourceNetId = r.ReadUShort(),
        };
        int flags = r.ReadByte();
        s.Loop = (flags & FlagLoop) != 0;
        s.Stop = (flags & FlagStop) != 0;
        s.Pitch = (flags & FlagPitch) != 0 ? r.ReadByte() / 100f : 1f;
        return s;
    }
}
