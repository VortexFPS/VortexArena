// What Xonotic's QuakeC gives a vehicle's sounds (Base/data/xonotic-data.pk3dir/qcsrc/common/vehicles):
// sv_vehicles.qh "const float VOL_VEHICLEENGINE = 1", the engine sounds of vehicle/racer.qc, raptor.qc and
// spiderbot.qc ("sound(vehic, CH_TRIGGER_SINGLE, SND_VEH_*, VOL_VEHICLEENGINE, ATTEN_NORM)"), the blow-up
// ("sound(this, CH_SHOTS, SND_ROCKET_IMPACT, VOL_BASE, ATTEN_NORM)") and cl_vehicles.qc vehicle_alarm
// ("sound(NULL, CH_PAIN_SINGLE | CH_TRIGGER_SINGLE, SND_VEH_ALARM*, VOL_BASEVOICE, ATTEN_NONE)").
using System.Numerics;

namespace VortexArena.Engine.Audio;

/// <summary>
/// The native game's vehicle sounds as calls on DarkPlaces' mixer: the channel, volume and attenuation each
/// one has in Xonotic. None of them changes pitch (the QuakeC never passes one: plain <c>sound</c>, not
/// <c>sound7</c>), so every channel runs at speed 1.
/// </summary>
public static class DpVehicleSounds
{
    /// <summary>sv_vehicles.qh VOL_VEHICLEENGINE.</summary>
    public const float VolVehicleEngine = 1f;
    /// <summary>sound.qh VOL_BASE and VOL_BASEVOICE.</summary>
    public const float VolBase = 0.7f, VolBaseVoice = 1f;
    /// <summary>sound.qh ATTEN_NORM and ATTEN_NONE.</summary>
    public const float AttenNorm = 0.5f, AttenNone = 0f;
    /// <summary>sound.qh CH_TRIGGER_SINGLE, CH_PAIN_SINGLE and CH_SHOTS (an automatic channel: sounds on it stack).</summary>
    public const int ChTriggerSingle = 3, ChPainSingle = 6, ChShots = -4;

    /// <summary>
    /// The engine voice of the client's vehicle model: three loops (idle, moving, boost) whose volumes follow
    /// the vehicle's speed (0..1) and its boost flag. The weights are the native client's own; QuakeC switches
    /// between whole samples instead (see the notes in VehicleVisuals). Each is a volume for a channel started
    /// at <see cref="VolVehicleEngine"/> and <see cref="AttenNorm"/>.
    /// </summary>
    public static (float Idle, float Move, float Boost) EngineMix(float speed01, bool boosting)
    {
        float move = speed01 < 0f || float.IsNaN(speed01) ? 0f : speed01 > 1f ? 1f : speed01;
        return (((1f - move) * 0.6f + 0.15f) * VolVehicleEngine, move * 0.8f * VolVehicleEngine, boosting ? 0.9f * VolVehicleEngine : 0f);
    }

    /// <summary>
    /// An engine loop riding a client-side model that has no entity number: entity -1, automatic channel,
    /// CHANNELFLAG_FORCELOOP, ATTEN_NORM. The caller moves it with <see cref="DpSoundSystem.SetChannelOrigin"/>.
    /// </summary>
    public static int StartEngineLoop(DpSoundSystem sound, DpSfx sfx, Vector3 origin, float volume)
        => sound.StartSound(-1, 0, sfx, origin, volume, AttenNorm, 0f, DpSoundSystem.ChannelFlagForceLoop, 1f);

    /// <summary>The blow-up at the vehicle's place: CH_SHOTS, VOL_BASE, ATTEN_NORM.</summary>
    public static int StartDeath(DpSoundSystem sound, DpSfx sfx, Vector3 origin)
        => sound.StartSound(-1, ChShots, sfx, origin, VolBase, AttenNorm);

    private static int AlarmChannel(bool shield) => shield ? ChTriggerSingle : ChPainSingle;

    /// <summary>
    /// vehicle_alarm: the low-health alarm on the client program's world entity's CH_PAIN_SINGLE, the low-shield
    /// alarm on its CH_TRIGGER_SINGLE, at VOL_BASEVOICE with no attenuation. A single channel: starting it again
    /// replaces what still plays there, as the two-second and one-second repeats do in Xonotic.
    /// </summary>
    public static int StartAlarm(DpSoundSystem sound, DpSfx sfx, bool shield)
        => sound.StartSound(DpSoundSystem.MaxEdicts, AlarmChannel(shield), sfx, Vector3.Zero, VolBaseVoice, AttenNone);

    /// <summary>vehicle_alarm with SND_Null: whatever plays on the alarm's channel ends.</summary>
    public static void StopAlarm(DpSoundSystem sound, bool shield) => sound.StopSound(DpSoundSystem.MaxEdicts, AlarmChannel(shield));
}
