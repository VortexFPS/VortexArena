// The native game's world sounds on DarkPlaces' channel table and mixer (src/VortexArena.Engine/Audio,
// game/audio). What Xonotic's server sends as svc_sound - a sample on an entity's channel - arrives here
// as OnSound / OnLoopingSound / OnStopSound; they are started exactly as cl_parse.c
// CL_ParseStartSoundPacket starts them (S_StartSound_StartPosition_Flags on the entity number and channel),
// and S_Update's per-frame work is DpFrame.
using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using VortexArena.Common.Diagnostics;
using VortexArena.Common.Services;
using VortexArena.Engine.Audio;
using VortexArena.Game.Audio;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

public partial class ClientWorld
{
    private sealed class DpLoop
    {
        public DpSfx Sfx = null!;
        public string Sample = "";
        public int Channel;
        public float SilentTime;
    }

    private sealed class DpWorldAdapter : IDpSoundWorld
    {
        public ClientWorld W = null!;
        public int Local = -1;
        public bool IsViewEntity(int entnum) => entnum > 0 && entnum == Local;
        public int MaxClients => 0;

        public DpEntityOrigin EntityOrigin(int entnum, ref NVec3 origin)
        {
            if (W._dpSceneOrigins is { } scripted) return scripted.TryGetValue(entnum, out NVec3 at) ? Set(ref origin, at) : DpEntityOrigin.Keep;
            if (W.EntityOriginResolver?.Invoke(entnum) is { } pos) return Set(ref origin, pos);
            return DpEntityOrigin.Keep;   // gone from the snapshot: the sound stays where it last was
        }

        private static DpEntityOrigin Set(ref NVec3 origin, NVec3 at)
        {
            origin = at;
            return DpEntityOrigin.Moved;
        }

        public bool Occluded(NVec3 listener, NVec3 source, int mode) => (mode & 1) != 0 && DpNative.PvsOccluded(W.Pvs, listener, source);
    }

    private readonly Dictionary<(int netId, int channel), DpLoop> _dpLoops = new();
    private readonly List<(int netId, int channel)> _dpLoopScratch = new();
    private DpWorldAdapter? _dpWorld;
    private static readonly bool s_dpNoIdleMute = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("VORTEX_AUDIO_NOIDLEMUTE"));
    private static readonly bool s_dpTrace = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("VORTEX_AUDIO_TRACE"));
    private double _dpTraceAt, _dpTraceClock;
    // For the trace: sounds started on the mixer, and sounds whose sample the mixer's bank did not find (they play on an engine node).
    private int _dpStarts, _dpMisses;
    private string _dpLastMiss = "";

    private DpSoundSystem DpSound()
    {
        DpAudio audio = DpAudio.Instance;
        if (_dpWorld is null)
        {
            _dpWorld = new DpWorldAdapter { W = this };
            audio.Sound.World = _dpWorld;
            audio.Sound.Settings = DpNative.Settings;
        }
        return audio.Sound;
    }

    private int DpLocalNetId() => AppearanceProvider?.Invoke()?.LocalNetId ?? -1;

    // S_StartSound for a networked sound. An emitter the client has no number for plays on the world's
    // automatic channel: with no entity to key on, a "single" channel would cut unrelated sounds short.
    private bool DpStartSound(string bare, NVec3 origin, float volume, float attenuation, int channel, int sourceNetId, float pitch)
    {
        if (DpNative.Bank(AudioLoader) is not { } bank) return false;
        // Not in the mounted game data: the engine-node path may still know it as a project resource.
        if (bank.Get(bare, forPlay: true) is not { Failed: false } sfx)
        {
            _dpMisses++;
            _dpLastMiss = bare;
            return false;
        }
        _dpStarts++;
        DpSoundSystem sound = DpSound();
        int entnum = sourceNetId > 0 ? sourceNetId : 0;
        if (entnum == 0 && channel > 0) channel = 0;
        if (!float.IsFinite(pitch) || pitch <= 0) pitch = 1f;
        sound.StartSound(entnum, channel, sfx, origin, Math.Clamp(volume, 0f, 1f), Math.Clamp(attenuation, 0f, 4f), 0f, 0, pitch);
        return true;
    }

    // QuakeC loopsound on an entity channel: CHANNELFLAG_FORCELOOP. A repeat of the same sample on the key
    // only refreshes volume and attenuation (the empty "change volume" sample does that in DarkPlaces).
    private bool DpStartLoop(int netId, int channel, string bare, NVec3 origin, float volume, float attenuation)
    {
        if (DpNative.Bank(AudioLoader) is not { } bank) return false;
        DpSoundSystem sound = DpSound();
        var key = (netId, channel);
        int engineChannel = channel > 0 ? channel : 1;   // a loop is stopped by its key, so it is always a single channel
        if (_dpLoops.TryGetValue(key, out DpLoop? loop) && loop.Sample == bare && sound.IsChannelPlaying(loop.Channel, loop.Sfx, netId, engineChannel))
        {
            loop.SilentTime = 0f;
            sound.StartSound(netId, engineChannel, DpSoundSystem.ChangeVolume, origin, Math.Clamp(volume, 0f, 1f), Math.Clamp(attenuation, 0f, 4f), 0f, DpSoundSystem.ChannelFlagForceLoop, 1f);
            return true;
        }
        if (bank.Get(bare, forPlay: true) is not { Failed: false } sfx) return false;
        int index = sound.StartSound(netId, engineChannel, sfx, origin, Math.Clamp(volume, 0f, 1f), Math.Clamp(attenuation, 0f, 4f), 0f, DpSoundSystem.ChannelFlagForceLoop, 1f);
        if (index >= 0) _dpLoops[key] = new DpLoop { Sfx = sfx, Sample = bare, Channel = index };
        return true;
    }

    private void DpStopSound(int netId, int channel)
    {
        DpSound().StopSound(netId, channel > 0 ? channel : 1);
        _dpLoops.Remove((netId, channel));
    }

    // The emitter left the snapshot: its loops end (the native netcode's rule; a server would have sent the
    // stop). Its one-shot sounds play out where they were, as DarkPlaces lets them.
    private void DpStopLoopsForEntity(int netId)
    {
        if (_dpLoops.Count == 0) return;
        _dpLoopScratch.Clear();
        foreach (var kv in _dpLoops)
            if (kv.Key.netId == netId) _dpLoopScratch.Add(kv.Key);
        foreach (var key in _dpLoopScratch) DpStopSound(key.netId, key.channel);
    }

    /// <summary>The listener for this frame: the active camera, or the scripted one of a test scene (Quake coordinates).</summary>
    private DpListener DpListenerNow()
    {
        if (_dpSceneListener is { } scripted) return scripted;
        Camera3D? cam = FrameCamera();
        if (cam is null) return _dpLastListener;
        Transform3D t = cam.GlobalTransform;
        Basis b = t.Basis.Orthonormalized();
        _dpLastListener = new DpListener { Origin = Coords.ToQuake(t.Origin), Forward = Coords.ToQuake(-b.Z), Left = Coords.ToQuake(-b.X), Up = Coords.ToQuake(b.Y) };
        return _dpLastListener;
    }

    private DpListener _dpLastListener = DpListener.Identity;

    // S_Update, once a frame.
    private void DpFrame(float delta)
    {
        using var _prof = Prof.Sample("dpaudio");
        DpAudio audio = DpAudio.Instance;
        DpSoundSystem sound = DpSound();
        DpSceneStep(delta);
        DpNative.ReadSettings();
        _dpWorld!.Local = DpLocalNetId();
        // snd_mutewhenidle: 1 = silent while the window is not the active one, 2 = only while minimised.
        int idle = (int)CvarF("snd_mutewhenidle", 1f);
        Window? window = GetWindow();
        audio.Blocked = !s_dpNoIdleMute && window is not null
            && ((idle == 1 && !window.HasFocus()) || (idle == 2 && window.Mode == Window.ModeEnum.Minimized));
        audio.GameFrame = Godot.Engine.GetProcessFrames();
        sound.Settings = DpNative.Settings;
        sound.World = _dpWorld;
        sound.ServerTickSeconds = 1.0 / Math.Max(1.0, Godot.Engine.PhysicsTicksPerSecond);
        DpListener listener = DpListenerNow();
        // cl.view_underwater (view.c V_CalcViewBlend): "CL_PointSuperContents(vieworigin) & SUPERCONTENTS_LIQUIDSMASK",
        // the contents of the point the view is drawn from - water, slime or lava. It drives snd_waterfx.
        bool underwater = Api.Services is not null
            && (Api.Trace.PointContents(listener.Origin) & VortexArena.Engine.Collision.SuperContents.LiquidsMask) != 0;
        sound.Update(listener, Math.Clamp(delta, 0f, 0.25f), underwater);

        // A loop its emitter stopped refreshing ends (the keep-alive of the native netcode, as before).
        if (_dpLoops.Count > 0)
        {
            _dpLoopScratch.Clear();
            foreach (var kv in _dpLoops)
            {
                kv.Value.SilentTime += delta;
                if (kv.Value.SilentTime > LoopKeepaliveTimeout) _dpLoopScratch.Add(kv.Key);
            }
            foreach (var key in _dpLoopScratch) DpStopSound(key.netId, key.channel);
        }

        if (DpAudio.OfflineFrames > 0 && _dpSceneRunning) audio.MixOffline(DpAudio.OfflineFrames);
        if (s_dpTrace && (_dpTraceClock += delta) >= _dpTraceAt)
        {
            _dpTraceAt = _dpTraceClock + 1.0;
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"[audio] native: scene t {_dpSceneClock:0.00} ear {listener.Origin.X:0.0} {listener.Origin.Y:0.0} {listener.Origin.Z:0.0} channels {sound.TotalSounds} mixed {sound.MixedSounds} volume {DpNative.Settings.Volume:0.##} master {DpNative.Settings.MasterVolume:0.##} radius {DpNative.Settings.SoundRadius:0} exponent {DpNative.Settings.AttenuationExponent:0.#} softclip {DpNative.Settings.SoftClip} maxchannel {DpNative.Settings.MaxChannelVolume:0.#} occlusion {DpNative.Settings.Occlusion} random {DpNative.Settings.IdenticalSoundRandomizationTime:0.##}/{DpNative.Settings.IdenticalSoundRandomizationTics:0.#} underwater {underwater} started {_dpStarts} missed {_dpMisses} \"{_dpLastMiss}\" {audio.TraceText()}"));
        }
    }

    // ---------------------------------------------------------------------------------------------------
    //  Test scenes (VORTEX_SND_SCENE=<script>): the audio comparison's events played through the game's
    //  own entry points, with a scripted listener. The format is _scratch/audio/tools/mkscene.py's.
    // ---------------------------------------------------------------------------------------------------

    private static readonly string? s_dpScenePath = System.Environment.GetEnvironmentVariable("VORTEX_SND_SCENE");
    private List<(double time, string kind, string[] args)>? _dpScene;
    private int _dpSceneNext;
    private double _dpSceneClock = -3.0;   // the scene begins three seconds after the world is up
    private bool _dpSceneRunning;
    private double? _dpSceneClockBase;
    private DpListener? _dpSceneListener;
    private Dictionary<int, NVec3>? _dpSceneOrigins;
    private readonly List<(double time, NVec3 angles, bool sweep)> _dpSceneAngles = new();
    // Where the scripted listener stands: the comparison recording's place on stormkeep, or VORTEX_SND_SCENE_EAR="x y z"
    // (another level, or a place under water).
    private static readonly NVec3 s_dpSceneEar = SceneEar();

    private static NVec3 SceneEar()
    {
        string[] parts = (System.Environment.GetEnvironmentVariable("VORTEX_SND_SCENE_EAR") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
            && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) return new NVec3(x, y, z);
        return new NVec3(-1155.1f, 792.7f, 80.6f);
    }

    private void DpSceneLoad()
    {
        if (string.IsNullOrEmpty(s_dpScenePath) || _dpScene is not null) return;
        _dpScene = new();
        try
        {
            foreach (string raw in System.IO.File.ReadAllLines(s_dpScenePath))
            {
                string line = raw.Split('#')[0].Trim();
                if (line.Length == 0) continue;
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                double time = double.Parse(parts[0], CultureInfo.InvariantCulture);
                if (parts[1] is "angles" or "angles_to")
                    _dpSceneAngles.Add((time, new NVec3(F(parts[2]), F(parts[3]), F(parts[4])), parts[1] == "angles_to"));
                else _dpScene.Add((time, parts[1], parts[2..]));
            }
            _dpScene.Sort((a, b) => a.time.CompareTo(b.time));
        }
        catch (Exception e) when (e is System.IO.IOException or FormatException or IndexOutOfRangeException)
        {
            GD.PrintErr("[audio] scene: " + e.Message);
        }
        _dpSceneOrigins = new();
        // The scene's own test samples: a directory of packages mounted over the game data (VORTEX_EXTRA_DATA).
        string? extra = System.Environment.GetEnvironmentVariable("VORTEX_EXTRA_DATA");
        if (!string.IsNullOrEmpty(extra) && AudioLoader?.Target is VortexArena.Game.Loaders.AssetLoader assets)
            GD.Print("[audio] scene: extra data " + extra + (assets.Vfs.MountGameDir(extra) ? " mounted" : " NOT mounted"));
    }

    private static float F(string text) => float.Parse(text, CultureInfo.InvariantCulture);

    private void DpSceneStep(float delta)
    {
        if (string.IsNullOrEmpty(s_dpScenePath)) return;
        DpSceneLoad();
        if (_dpScene is null) return;
        // An offline capture steps one server tick (1/64 s) a frame, like the recording the other engines play.
        // A real-time capture keeps the audio server's time (DpAudio.CaptureClock); otherwise the frame's.
        double mixed = DpAudio.OfflineFrames > 0 ? -1 : DpAudio.Instance.CaptureClock;
        if (DpAudio.OfflineFrames > 0) _dpSceneClock += 1.0 / 64;
        else if (mixed >= 0)
        {
            if (_dpSceneClockBase is not { } start) _dpSceneClockBase = start = mixed + 3.0;
            _dpSceneClock = mixed - start;
        }
        else _dpSceneClock += delta;
        if (_dpSceneClock < 0) return;
        _dpSceneRunning = true;

        // The view angles of this moment (pitch yaw roll, yaw turning left), as mkscene.py interpolates them.
        NVec3 angles = default;
        for (int i = 0; i < _dpSceneAngles.Count; i++)
        {
            if (_dpSceneAngles[i].time > _dpSceneClock + 1e-9) break;
            angles = _dpSceneAngles[i].angles;
            if (i + 1 < _dpSceneAngles.Count && _dpSceneAngles[i + 1].sweep && _dpSceneAngles[i + 1].time > _dpSceneAngles[i].time && _dpSceneClock < _dpSceneAngles[i + 1].time)
            {
                float k = (float)((_dpSceneClock - _dpSceneAngles[i].time) / (_dpSceneAngles[i + 1].time - _dpSceneAngles[i].time));
                angles += (_dpSceneAngles[i + 1].angles - angles) * k;
            }
        }
        float yaw = angles.Y * MathF.PI / 180f, pitch = angles.X * MathF.PI / 180f;
        NVec3 forward = new(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Cos(pitch) * MathF.Sin(yaw), -MathF.Sin(pitch));
        NVec3 left = new(-MathF.Sin(yaw), MathF.Cos(yaw), 0);
        _dpSceneListener = new DpListener { Origin = s_dpSceneEar, Forward = forward, Left = left, Up = NVec3.Cross(forward, left) };
        // The engine-node path hears through the engine's listener: put one there.
        if (!DpNative.Active)
        {
            _dpSceneEars ??= MakeSceneEars();
            _dpSceneEars.GlobalTransform = new Transform3D(new Basis(-Coords.ToGodot(left), Coords.ToGodot(NVec3.Cross(forward, left)), -Coords.ToGodot(forward)), Coords.ToGodot(s_dpSceneEar));
            _lastListener = Coords.ToGodot(s_dpSceneEar);
        }

        if (_dpSceneVehicle is not null && GodotObject.IsInstanceValid(_dpSceneVehicle)) _dpSceneVehicle.Apply(_dpSceneVehicleState, delta);
        while (_dpSceneNext < _dpScene.Count && _dpScene[_dpSceneNext].time <= _dpSceneClock + 1e-9)
        {
            (_, string kind, string[] a) = _dpScene[_dpSceneNext++];
            switch (kind)
            {
                case "sound":
                {
                    NVec3 at = s_dpSceneEar + new NVec3(F(a[3]), F(a[4]), F(a[5]));
                    int ent = int.Parse(a[0], CultureInfo.InvariantCulture);
                    if (ent > 0) _dpSceneOrigins![ent] = at;
                    OnSound(a[2], at, F(a[6]), F(a[7]), int.Parse(a[1], CultureInfo.InvariantCulture), ent, a.Length > 8 ? F(a[8]) : 1f);
                    break;
                }
                case "static":
                {
                    // A level's ambient sound (svc_spawnstaticsound). The engine-node path has no such thing: a loop on a made-up entity.
                    NVec3 at = s_dpSceneEar + new NVec3(F(a[1]), F(a[2]), F(a[3]));
                    if (DpNative.Active && DpNative.Bank(AudioLoader)?.Get(a[0], forPlay: true) is { Failed: false } sfx)
                        DpSound().StaticSound(sfx, at, F(a[4]), F(a[5]) * 64f);
                    else if (!DpNative.Active)
                    {
                        _dpSceneOrigins![30000 + _dpSceneNext] = at;
                        OnLoopingSound(30000 + _dpSceneNext, 1, a[0], at, F(a[4]), F(a[5]));
                    }
                    break;
                }
                case "stop": OnStopSound(int.Parse(a[0], CultureInfo.InvariantCulture), int.Parse(a[1], CultureInfo.InvariantCulture)); break;
                case "cvar":
                    if (Api.Services is not null) Api.Cvars.Set(a[0], string.Join(' ', a[1..]));
                    break;
                case "cmd":
                    if (a[0] == "stopsound")
                    {
                        if (DpNative.Active) DpSound().StopAllSounds();
                        else
                        {
                            foreach (var kv in _loopingSounds) DestroyLoop(kv.Value);
                            _loopingSounds.Clear();
                            foreach (ActiveOneShot shot in _activeOneShots)
                                if (GodotObject.IsInstanceValid(shot.Player)) shot.Player.Stop();
                        }
                    }
                    else if (a[0] is "play" or "play2" && a.Length > 1)
                        OnSound(a[1], s_dpSceneEar, 1f, a[0] == "play2" ? 0f : 1f, 0, 0, 1f);
                    break;
                // A vehicle's client-side sounds, through the code that plays them in a game:
                //   vehicle <classname> <f> <l> <u>     the vehicle's model and engine voice at a place (parked: idle)
                //   vehiclestate <speed 0..1> <boost 0|1> its engine load from here on
                //   vehicleat <f> <l> <u>                it is moved
                //   vehicledie                           it blows up
                //   alarm <health|shield|stophealth|stopshield>   the pilot's low-health / low-shield alarm
                case "vehicle":
                    if (_dpSceneVehicle is not null && GodotObject.IsInstanceValid(_dpSceneVehicle)) _dpSceneVehicle.QueueFree();
                    _dpSceneVehicle = NewVehicleVisuals("sceneVehicle", a[0]);
                    _dpSceneVehicle.Position = Coords.ToGodot(s_dpSceneEar + new NVec3(F(a[1]), F(a[2]), F(a[3])));
                    _dpSceneVehicleState = VehicleVisuals.State.Default;
                    break;
                case "vehiclestate":
                    _dpSceneVehicleState.Speed01 = F(a[0]);
                    _dpSceneVehicleState.Boosting = a.Length > 1 && a[1] == "1";
                    break;
                case "vehicleat":
                    if (_dpSceneVehicle is not null && GodotObject.IsInstanceValid(_dpSceneVehicle))
                        _dpSceneVehicle.Position = Coords.ToGodot(s_dpSceneEar + new NVec3(F(a[0]), F(a[1]), F(a[2])));
                    break;
                case "vehicledie": _dpSceneVehicleState.Alive = false; break;
                case "alarm":
                    switch (a[0])
                    {
                        case "health": VortexArena.Game.Hud.VehicleHud.AlarmOnMixer(AudioLoader, "vehicles/alarm", shield: false); break;
                        case "shield": VortexArena.Game.Hud.VehicleHud.AlarmOnMixer(AudioLoader, "vehicles/alarm_shield", shield: true); break;
                        case "stophealth": DpVehicleSounds.StopAlarm(DpSound(), shield: false); break;
                        case "stopshield": DpVehicleSounds.StopAlarm(DpSound(), shield: true); break;
                    }
                    break;
                case "end": GD.Print("[audio] AUDIOTEST-END"); break;
            }
        }
    }

    private AudioListener3D? _dpSceneEars;
    private VehicleVisuals? _dpSceneVehicle;
    private VehicleVisuals.State _dpSceneVehicleState;

    private AudioListener3D MakeSceneEars()
    {
        AudioListener3D ears = new() { Name = "SceneEars" };
        AddChild(ears);
        ears.MakeCurrent();
        return ears;
    }
}
