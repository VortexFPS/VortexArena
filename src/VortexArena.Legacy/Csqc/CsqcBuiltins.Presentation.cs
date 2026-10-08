// Port of Base/darkplaces/clvm_cmds.c VM_CL_sound, VM_CL_pointsound, VM_CL_ambientsound,
// VM_CL_setlistener, VM_CL_particle, VM_CL_trailparticles, VM_CL_pointparticles, VM_CL_boxparticles,
// VM_CL_effect, the VM_CL_te_* family, VM_CL_setattachment, VM_CL_gettagindex, VM_CL_gettaginfo,
// VM_CL_skel_*, VM_CL_frameforname, VM_CL_frameduration; prvm_cmds.c VM_localsound, VM_getsoundtime,
// VM_soundlength and VM_getsurface*. Each checks its arguments as the C does and passes the call on.
using System.Numerics;
using VortexArena.Legacy.Protocol;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Csqc;

public sealed partial class CsqcBuiltins
{
    // sound.h: the channel flags QuakeC may set (reliable, force-loop, paused, full volume).
    private const int SettableChannelFlags = 1 | 2 | 8 | 16;

    private static bool IsChannel(int channel) => channel is >= -128 and <= 127;

    private void RegisterSoundAndEffects()
    {
        Forward(8, "sound", Sound);
        Forward(48, "particle", Particle);
        Forward(74, "ambientsound", AmbientSound);
        Forward(177, "localsound", LocalSound);
        Forward(336, "trailparticles", TrailParticles);
        Forward(337, "pointparticles", PointParticles);
        Forward(351, "SetListener", SetListener);
        Forward(404, "effect", Effect);
        Forward(405, "te_blood", vm => TeCounted(vm, TempEntityType.Blood, "VM_CL_te_blood"));
        Forward(406, "te_bloodshower", TeBloodShower);
        Forward(407, "te_explosionrgb", TeExplosionRgb);
        Forward(408, "te_particlecube", TeParticleCube);
        Forward(409, "te_particlerain", vm => TeRain(vm, TempEntityType.ParticleRain, "VM_CL_te_particlerain"));
        Forward(410, "te_particlesnow", vm => TeRain(vm, TempEntityType.ParticleSnow, "VM_CL_te_particlesnow"));
        Forward(411, "te_spark", vm => TeCounted(vm, TempEntityType.Spark, "VM_CL_te_spark", allowZero: true));
        Forward(412, "te_gunshotquad", vm => TePoint(vm, TempEntityType.GunshotQuad, "VM_CL_te_gunshotquad"));
        Forward(413, "te_spikequad", vm => TePoint(vm, TempEntityType.SpikeQuad, "VM_CL_te_spikequad"));
        Forward(414, "te_superspikequad", vm => TePoint(vm, TempEntityType.SuperSpikeQuad, "VM_CL_te_superspikequad"));
        Forward(415, "te_explosionquad", vm => TePoint(vm, TempEntityType.ExplosionQuad, "VM_CL_te_explosionquad"));
        Forward(416, "te_smallflash", vm => TePoint(vm, TempEntityType.SmallFlash, "VM_CL_te_smallflash"));
        Forward(417, "te_customflash", TeCustomFlash);
        Forward(418, "te_gunshot", vm => TePoint(vm, TempEntityType.Gunshot, "VM_CL_te_gunshot"));
        Forward(419, "te_spike", vm => TePoint(vm, TempEntityType.Spike, "VM_CL_te_spike"));
        Forward(420, "te_superspike", vm => TePoint(vm, TempEntityType.SuperSpike, "VM_CL_te_superspike"));
        Forward(421, "te_explosion", vm => TePoint(vm, TempEntityType.Explosion, "VM_CL_te_explosion"));
        Forward(422, "te_tarexplosion", vm => TePoint(vm, TempEntityType.TarExplosion, "VM_CL_te_tarexplosion"));
        Forward(423, "te_wizspike", vm => TePoint(vm, TempEntityType.WizSpike, "VM_CL_te_wizspike"));
        Forward(424, "te_knightspike", vm => TePoint(vm, TempEntityType.KnightSpike, "VM_CL_te_knightspike"));
        Forward(425, "te_lavasplash", vm => TePoint(vm, TempEntityType.LavaSplash, "VM_CL_te_lavasplash"));
        Forward(426, "te_teleport", vm => TePoint(vm, TempEntityType.Teleport, "VM_CL_te_teleport"));
        Forward(427, "te_explosion2", TeExplosion2);
        Forward(428, "te_lightning1", vm => TeBeam(vm, TempEntityType.Lightning1, "VM_CL_te_lightning1"));
        Forward(429, "te_lightning2", vm => TeBeam(vm, TempEntityType.Lightning2, "VM_CL_te_lightning2"));
        Forward(430, "te_lightning3", vm => TeBeam(vm, TempEntityType.Lightning3, "VM_CL_te_lightning3"));
        Forward(431, "te_beam", vm => TeBeam(vm, TempEntityType.Beam, "VM_CL_te_beam"));
        Forward(433, "te_plasmaburn", vm => TePoint(vm, TempEntityType.PlasmaBurn, "VM_CL_te_plasmaburn"));
        Forward(457, "te_flamejet", vm => TeCounted(vm, TempEntityType.FlameJet, "VM_CL_te_flamejet"));
        Forward(483, "pointsound", PointSound);
        Forward(502, "boxparticles", BoxParticles);
        Forward(533, "getsoundtime", GetSoundTime);
        Forward(534, "soundlength", SoundLength);
    }

    private void RegisterModels()
    {
        Forward(263, "skel_create", SkelCreate);
        Forward(264, "skel_build", SkelBuild);
        Forward(265, "skel_get_numbones", vm => { Parms(1, "VM_CL_skel_get_numbones"); vm.ReturnFloat(_presentation.Models.SkelNumBones(ArgInt(0))); });
        Forward(266, "skel_get_bonename", vm => { Parms(2, "VM_CL_skel_get_bonename"); ReturnStringOrNull(_presentation.Models.SkelBoneName(ArgInt(0), ArgInt(1))); });
        Forward(267, "skel_get_boneparent", vm => { Parms(2, "VM_CL_skel_get_boneparent"); vm.ReturnFloat(_presentation.Models.SkelBoneParent(ArgInt(0), ArgInt(1))); });
        Forward(268, "skel_find_bone", vm => { Parms(2, "VM_CL_skel_find_bone"); vm.ReturnFloat(_presentation.Models.SkelFindBone(ArgInt(0), vm.ArgString(1))); });
        Forward(269, "skel_get_bonerel", vm => SkelGetBone(vm, false, "VM_CL_skel_get_bonerel"));
        Forward(270, "skel_get_boneabs", vm => SkelGetBone(vm, true, "VM_CL_skel_get_boneabs"));
        Forward(271, "skel_set_bone", vm => SkelSetBone(vm, false, "VM_CL_skel_set_bone"));
        Forward(272, "skel_mul_bone", vm => SkelSetBone(vm, true, "VM_CL_skel_mul_bone"));
        Forward(273, "skel_mul_bones", SkelMulBones);
        Forward(274, "skel_copybones", vm => { Parms(4, "VM_CL_skel_copybones"); _presentation.Models.SkelCopyBones(ArgInt(0), ArgInt(1), ArgInt(2), ArgInt(3)); });
        Forward(275, "skel_delete", vm => { Parms(1, "VM_CL_skel_delete"); _presentation.Models.SkelDelete(ArgInt(0)); });
        Forward(276, "frameforname", FrameForName);
        Forward(277, "frameduration", FrameDuration);
        Forward(434, "getsurfacenumpoints", vm => { if (Surface(vm, 2, "VM_getsurfacenumpoints", out int e, out string m)) vm.ReturnFloat(_presentation.Models.SurfaceNumPoints(e, m, ArgInt(1))); });
        Forward(435, "getsurfacepoint", vm => { if (Surface(vm, 3, "VM_getsurfacepoint", out int e, out string m)) vm.ReturnVector(_presentation.Models.SurfacePoint(e, m, ArgInt(1), ArgInt(2))); });
        Forward(436, "getsurfacenormal", vm => { if (Surface(vm, 2, "VM_getsurfacenormal", out int e, out string m)) vm.ReturnVector(_presentation.Models.SurfaceNormal(e, m, ArgInt(1))); });
        Forward(437, "getsurfacetexture", vm => { if (Surface(vm, 2, "VM_getsurfacetexture", out int e, out string m)) ReturnStringOrNull(_presentation.Models.SurfaceTexture(e, m, ArgInt(1))); });
        Forward(438, "getsurfacenearpoint", GetSurfaceNearPoint);
        Forward(439, "getsurfaceclippedpoint", vm => { if (Surface(vm, 3, "VM_getsurfaceclippedpoint", out int e, out string m)) vm.ReturnVector(_presentation.Models.SurfaceClippedPoint(e, m, ArgInt(1), vm.ArgVector(2))); });
        Here(443, "setattachment", SetAttachment);
        Forward(451, "gettagindex", GetTagIndex);
        Forward(452, "gettaginfo", GetTagInfo);
        Forward(486, "getsurfacepointattribute", vm => { if (Surface(vm, 4, "VM_getsurfacepointattribute", out int e, out string m)) vm.ReturnVector(_presentation.Models.SurfacePointAttribute(e, m, ArgInt(1), ArgInt(2), ArgInt(3))); });
        Forward(628, "getsurfacenumtriangles", vm => { if (Surface(vm, 2, "VM_getsurfacenumtriangles", out int e, out string m)) vm.ReturnFloat(_presentation.Models.SurfaceNumTriangles(e, m, ArgInt(1))); });
        Forward(629, "getsurfacetriangle", vm => { if (Surface(vm, 3, "VM_getsurfacetriangle", out int e, out string m)) vm.ReturnVector(_presentation.Models.SurfaceTriangle(e, m, ArgInt(1), ArgInt(2))); });
    }

    // ---- sound -------------------------------------------------------------------------------------

    private bool CheckVolume(string name, float volume, float attenuation)
    {
        if (!(volume >= 0 && volume <= 1))
        {
            Warning($"{name}: volume must be in range 0-1\n");
            return false;
        }
        if (!(attenuation >= 0 && attenuation <= 4))
        {
            Warning($"{name}: attenuation must be in range 0-4\n");
            return false;
        }
        return true;
    }

    // #8 void(entity e, float chan, string samp, float volume, float atten[, float pitchchange[, float flags]]) sound
    private void Sound(QcVm vm)
    {
        Parms(5, 7, "VM_CL_sound");
        int entity = vm.ArgEdict(0);
        int channel = ArgInt(1);
        string sample = vm.ArgString(2);
        float volume = vm.ArgFloat(3), attenuation = vm.ArgFloat(4);
        if (!CheckVolume("VM_CL_sound", volume, attenuation)) return;
        float pitchChange = vm.ArgCount < 6 ? 0 : vm.ArgFloat(5);
        // "we only let the qc set certain flags, others are off-limits"
        int flags = vm.ArgCount < 7 ? 0 : ArgInt(6) & SettableChannelFlags;

        // "sound_starttime exists instead of sound_startposition because in a networking sense you
        // might not know when something is being received"
        float startTime = _host.GetFloat(_g.SoundStartTime);
        float startPosition = startTime != 0 ? (float)(_state.Time - startTime) : 0;

        if (!IsChannel(channel))
        {
            Warning("VM_CL_sound: channel must be in range 0-127\n");
            return;
        }
        // CL_VM_GetEntitySoundOrigin: the entity's position (a tag-attached entity's true position is
        // the presentation's to refine; it has the entity number).
        _presentation.Sound.Start(entity, channel, sample, vm.FieldVector(entity, _f.Origin), volume, attenuation, startPosition, flags,
            pitchChange > 0.0f ? pitchChange * 0.01f : 1.0f);
    }

    // #483 void(vector origin, string sample, float volume, float attenuation) pointsound
    private void PointSound(QcVm vm)
    {
        Parms(4, "VM_CL_pointsound");
        float volume = vm.ArgFloat(2), attenuation = vm.ArgFloat(3);
        if (!CheckVolume("VM_CL_pointsound", volume, attenuation)) return;
        // "Send World Entity as Entity to Play Sound"
        _presentation.Sound.Start(0, 0, vm.ArgString(1), vm.ArgVector(0), volume, attenuation, 0, 0, 1);
    }

    // #74 void(vector pos, string samp, float vol, float atten) ambientsound
    private void AmbientSound(QcVm vm)
    {
        Parms(4, "VM_CL_ambientsound");
        _presentation.Sound.StartStatic(vm.ArgVector(0), vm.ArgString(1), vm.ArgFloat(2), vm.ArgFloat(3) * 64);
    }

    // #177 void(string sample[, float channel, float volume]) localsound
    private void LocalSound(QcVm vm)
    {
        Parms(1, 3, "VM_localsound");
        string sample = vm.ArgString(0);
        int channel = 0;
        float volume = 1;
        if (vm.ArgCount == 3)
        {
            channel = ArgInt(1);
            volume = vm.ArgFloat(2) == 0 ? 1 : vm.ArgFloat(2);
        }
        if (!_presentation.Sound.Local(sample, channel, volume))
        {
            vm.ReturnFloat(-4);
            Warning($"VM_localsound: Failed to play {sample} !\n");
        }
    }

    // #351 void(vector origin, vector forward, vector right, vector up) SetListener
    private void SetListener(QcVm vm)
    {
        Parms(4, "VM_CL_setlistener");
        _presentation.Sound.SetListener(vm.ArgVector(0), vm.ArgVector(1), vm.ArgVector(2), vm.ArgVector(3));
    }

    // #533 float(entity e, float channel) getsoundtime
    private void GetSoundTime(QcVm vm)
    {
        Parms(2, "VM_getsoundtime");
        int entity = vm.ArgEdict(0);
        int channel = ArgInt(1);
        if (!IsChannel(channel)) Warning($"VM_getsoundtime: bad channel {channel}\n");
        vm.ReturnFloat(_presentation.Sound.ChannelPosition(entity, channel));
    }

    // #534 float(string sample) soundlength
    private void SoundLength(QcVm vm)
    {
        Parms(1, "VM_soundlength");
        vm.ReturnFloat(_presentation.Sound.Length(vm.ArgString(0)));
    }

    // ---- particles ---------------------------------------------------------------------------------

    private static readonly LegacyParticleTint NoTint = new()
    {
        ColorMin = new QcVector(1, 1, 1), ColorMax = new QcVector(1, 1, 1), AlphaMin = 1, AlphaMax = 1, Fade = 1,
    };

    // #48 void(vector o, vector d, float color, float count) particle
    private void Particle(QcVm vm)
    {
        Parms(4, "VM_CL_particle");
        QcVector origin = vm.ArgVector(0), direction = vm.ArgVector(1);
        _presentation.Effects.ParticleEffect(CsqcEffectInfo.SvcParticle, ArgInt(3), origin, origin, direction, direction, ArgInt(2) & 0xFF);
    }

    // #336 void(entity ent, float effectnum, vector start, vector end[, float color]) trailparticles
    private void TrailParticles(QcVm vm)
    {
        Parms(4, 5, "VM_CL_trailparticles");
        int entity = vm.ArgEdict(0);
        int effect = ArgInt(1);
        QcVector velocity = vm.FieldVector(entity, _f.Velocity);
        if (effect < 0) return;
        _presentation.Effects.ParticleTrail(effect, 1, vm.ArgVector(2), vm.ArgVector(3), velocity, velocity, vm.ArgCount >= 5 ? ArgInt(4) : 0, NoTint);
    }

    // #337 void(float effectnum, vector origin, vector dir, float count[, float color]) pointparticles
    private void PointParticles(QcVm vm)
    {
        Parms(4, 5, "VM_CL_pointparticles");
        int effect = ArgInt(0);
        QcVector origin = vm.ArgVector(1), velocity = vm.ArgVector(2);
        if (effect < 0) return;
        _presentation.Effects.ParticleEffect(effect, vm.ArgFloat(3), origin, origin, velocity, velocity, vm.ArgCount >= 5 ? ArgInt(4) : 0);
    }

    // #502 void(float effectnum, entity own, vector origin_from, vector origin_to, vector dir_from,
    // vector dir_to, float count[, float extflags]) boxparticles. The flags choose which of the
    // particles_* globals tint the effect, and whether it is drawn as a trail.
    private void BoxParticles(QcVm vm)
    {
        Parms(7, 8, "VM_CL_boxparticles");
        int effect = ArgInt(0);
        if (effect < 0) return;
        int flags = vm.ArgCount >= 8 ? ArgInt(7) : 0;
        LegacyParticleTint tint = NoTint;
        if ((flags & 1) != 0) // read alpha
        {
            tint.AlphaMin = _host.GetFloat(_g.ParticlesAlphaMin);
            tint.AlphaMax = _host.GetFloat(_g.ParticlesAlphaMax);
        }
        if ((flags & 2) != 0) // read color
        {
            tint.ColorMin = _host.GetVector(_g.ParticlesColorMin);
            tint.ColorMax = _host.GetVector(_g.ParticlesColorMax);
        }
        if ((flags & 4) != 0) tint.Fade = _host.GetFloat(_g.ParticlesFade);
        if ((flags & 128) != 0) // draw as trail
            _presentation.Effects.ParticleTrail(effect, vm.ArgFloat(6), vm.ArgVector(2), vm.ArgVector(3), vm.ArgVector(4), vm.ArgVector(5), 0, tint);
        else
            _presentation.Effects.ParticleBox(effect, vm.ArgFloat(6), vm.ArgVector(2), vm.ArgVector(3), vm.ArgVector(4), vm.ArgVector(5), tint);
    }

    // #404 void(vector org, string modelname, float startframe, float endframe, float framerate) effect
    private void Effect(QcVm vm)
    {
        Parms(5, "VM_CL_effect");
        string model = vm.ArgString(1);
        if (_host.ModelBounds(model, out _, out _))
            _presentation.Effects.SpriteEffect(vm.ArgVector(0), model, ArgInt(2), ArgInt(3), vm.ArgFloat(4));
        else _host.Services.Print($"VM_CL_effect: Could not load model '{model}'\n");
    }

    // ---- temp-entity builtins: the same effects svc_temp_entity carries, asked for by the program ---

    private static Vector3 V(QcVector v) => new(v.X, v.Y, v.Z);

    private void Te(in DpTempEntity effect) => _presentation.Effects.TempEntity(effect);

    // te_gunshot and the other one-point effects: void(vector org).
    private void TePoint(QcVm vm, TempEntityType type, string name)
    {
        Parms(1, name);
        Te(new DpTempEntity { Type = type, Origin = V(vm.ArgVector(0)), Count = 1 });
    }

    // te_blood, te_spark, te_flamejet: void(vector org, vector velocity, float howmany).
    private void TeCounted(QcVm vm, TempEntityType type, string name, bool allowZero = false)
    {
        Parms(3, name);
        if (!allowZero && vm.ArgFloat(2) < 1) return;
        Te(new DpTempEntity { Type = type, Origin = V(vm.ArgVector(0)), Direction = V(vm.ArgVector(1)), Count = ArgInt(2) });
    }

    // #406 void(vector mincorner, vector maxcorner, float explosionspeed, float howmany) te_bloodshower
    private void TeBloodShower(QcVm vm)
    {
        Parms(4, "VM_CL_te_bloodshower");
        if (vm.ArgFloat(3) < 1) return;
        Te(new DpTempEntity { Type = TempEntityType.BloodShower, Origin = V(vm.ArgVector(0)), Origin2 = V(vm.ArgVector(1)), Speed = vm.ArgFloat(2), Count = ArgInt(3) });
    }

    // #407 void(vector org, vector color) te_explosionrgb
    private void TeExplosionRgb(QcVm vm)
    {
        Parms(2, "VM_CL_te_explosionrgb");
        Te(new DpTempEntity { Type = TempEntityType.ExplosionRgb, Origin = V(vm.ArgVector(0)), Color = V(vm.ArgVector(1)) });
    }

    // #408 void(vector mincorner, vector maxcorner, vector vel, float howmany, float color, float gravityflag, float randomveljitter) te_particlecube
    private void TeParticleCube(QcVm vm)
    {
        Parms(7, "VM_CL_te_particlecube");
        Te(new DpTempEntity
        {
            Type = TempEntityType.ParticleCube, Origin = V(vm.ArgVector(0)), Origin2 = V(vm.ArgVector(1)), Direction = V(vm.ArgVector(2)),
            Count = ArgInt(3), ColorStart = ArgInt(4), ColorLength = vm.ArgFloat(5) != 0 ? 1 : 0, Speed = vm.ArgFloat(6),
        });
    }

    // #409, #410 void(vector mincorner, vector maxcorner, vector vel, float howmany, float color) te_particlerain / te_particlesnow
    private void TeRain(QcVm vm, TempEntityType type, string name)
    {
        Parms(5, name);
        Te(new DpTempEntity { Type = type, Origin = V(vm.ArgVector(0)), Origin2 = V(vm.ArgVector(1)), Direction = V(vm.ArgVector(2)), Count = ArgInt(3), ColorStart = ArgInt(4) });
    }

    // #417 void(vector org, float radius, float lifetime, vector color) te_customflash
    private void TeCustomFlash(QcVm vm)
    {
        Parms(4, "VM_CL_te_customflash");
        Te(new DpTempEntity { Type = TempEntityType.CustomFlash, Origin = V(vm.ArgVector(0)), Radius = vm.ArgFloat(1), Speed = vm.ArgFloat(2), Color = V(vm.ArgVector(3)) });
    }

    // #427 void(vector org, float colorstart, float colorlength) te_explosion2
    private void TeExplosion2(QcVm vm)
    {
        Parms(3, "VM_CL_te_explosion2");
        Te(new DpTempEntity { Type = TempEntityType.Explosion2, Origin = V(vm.ArgVector(0)), ColorStart = ArgInt(1), ColorLength = ArgInt(2) });
    }

    // #428-#431 void(entity own, vector start, vector end) te_lightning1/2/3, te_beam
    private void TeBeam(QcVm vm, TempEntityType type, string name)
    {
        Parms(3, name);
        Te(new DpTempEntity { Type = type, Entity = vm.ArgEdict(0), Origin = V(vm.ArgVector(1)), Origin2 = V(vm.ArgVector(2)) });
    }

    // ---- tags and attachment -----------------------------------------------------------------------

    // #443 void(entity e, entity tagentity, string tagname) setattachment
    private void SetAttachment(QcVm vm)
    {
        Parms(3, "VM_CL_setattachment");
        int e = vm.ArgEdict(0);
        int tagEntity = vm.ArgEdict(1);
        string tagName = vm.ArgString(2);
        if (e == 0) { Warning("setattachment: can not modify world entity\n"); return; }
        if (vm.IsFree(e)) { Warning("setattachment: can not modify free entity\n"); return; }

        int tagIndex = 0;
        if (tagEntity != 0 && tagName.Length != 0)
        {
            // CL_GetModelByIndex, not CL_GetModelFromEdict: a free tag entity's stale model still counts.
            string? model = _state.ModelNameForIndex(Int(vm.FieldFloat(tagEntity, _f.ModelIndex)));
            if (model is not null) tagIndex = _presentation.Models.TagIndex(model, Int(vm.FieldFloat(tagEntity, _f.Skin)), tagName);
        }
        vm.FieldInt(e, _f.TagEntity) = tagEntity;
        vm.FieldFloat(e, _f.TagIndex) = tagIndex;
    }

    // #451 float(entity ent, string tagname) gettagindex
    private void GetTagIndex(QcVm vm)
    {
        Parms(2, "VM_CL_gettagindex");
        int e = vm.ArgEdict(0);
        string tagName = vm.ArgString(1);
        if (e == 0) { Warning($"VM_CL_gettagindex(entity #{e}): can't affect world entity\n"); return; }
        if (vm.IsFree(e)) { Warning($"VM_CL_gettagindex(entity #{e}): can't affect free entity\n"); return; }
        string? model = _host.ModelNameOf(e);
        vm.ReturnFloat(model is null ? 0 : _presentation.Models.TagIndex(model, Int(vm.FieldFloat(e, _f.Skin)), tagName));
    }

    // #452 vector(entity ent, float tagindex) gettaginfo: the tag's origin, with its axes in
    // v_forward/v_right/v_up and its parent, name and local transform in the gettaginfo_* globals.
    private void GetTagInfo(QcVm vm)
    {
        Parms(2, "VM_CL_gettaginfo");
        int e = vm.ArgEdict(0);
        int code = _presentation.Models.TagInfo(e, ArgInt(1), out LegacyTagInfo info);
        _host.SetVector(_g.VForward, info.Forward);
        _host.SetVector(_g.VRight, info.Right);
        _host.SetVector(_g.VUp, info.Up);
        vm.ReturnVector(info.Origin);
        _host.SetFloat(_g.GetTagInfoParent, info.Parent);
        _host.SetInt(_g.GetTagInfoName, info.Name is null ? 0 : vm.TempString(info.Name));
        _host.SetVector(_g.GetTagInfoForward, info.LocalForward);
        _host.SetVector(_g.GetTagInfoRight, info.LocalRight);
        _host.SetVector(_g.GetTagInfoUp, info.LocalUp);
        _host.SetVector(_g.GetTagInfoOffset, info.LocalOffset);
        if (code == 1) Warning("gettagindex: can't affect world entity\n");
        else if (code == 2) Warning("gettagindex: can't affect free entity\n");
    }

    // ---- surfaces ----------------------------------------------------------------------------------

    // The common opening of the getsurface builtins: the entity's model, or a zero / null result.
    private bool Surface(QcVm vm, int parms, string name, out int edict, out string model)
    {
        Parms(parms, name);
        vm.ReturnVector(default);
        edict = vm.ArgEdict(0);
        model = _host.ModelNameOf(edict) ?? "";
        return model.Length != 0;
    }

    // #438 float(entity e, vector p) getsurfacenearpoint: -1 if there is none.
    private void GetSurfaceNearPoint(QcVm vm)
    {
        Parms(2, "VM_getsurfacenearpoint");
        int e = vm.ArgEdict(0);
        string? model = _host.ModelNameOf(e);
        vm.ReturnFloat(model is null ? -1 : _presentation.Models.SurfaceNearPoint(e, model, vm.ArgVector(1)));
    }

    // ---- skeletons ---------------------------------------------------------------------------------

    // #263 float(float modlindex) skel_create
    private void SkelCreate(QcVm vm)
    {
        Parms(1, "VM_CL_skel_create");
        string? model = _state.ModelNameForIndex(ArgInt(0));
        vm.ReturnFloat(model is null ? 0 : _presentation.Models.SkelCreate(model));
    }

    // #264 float(float skel, entity ent, float modlindex, float retainfrac, float firstbone, float lastbone) skel_build
    private void SkelBuild(QcVm vm)
    {
        Parms(6, "VM_CL_skel_build");
        int e = vm.ArgEdict(1);
        string? model = _state.ModelNameForIndex(ArgInt(2));
        vm.ReturnFloat(model is null ? 0 : _presentation.Models.SkelBuild(ArgInt(0), e, model, vm.ArgFloat(3), ArgInt(4), ArgInt(5)));
    }

    // #269 skel_get_bonerel, #270 skel_get_boneabs: vector(float skel, float bonenum); the axes go
    // to v_forward, v_right, v_up.
    private void SkelGetBone(QcVm vm, bool absolute, string name)
    {
        Parms(2, name);
        vm.ReturnVector(default);
        // The C clears the three axis globals before it looks at its arguments.
        _host.SetVector(_g.VForward, default);
        _host.SetVector(_g.VRight, default);
        _host.SetVector(_g.VUp, default);
        if (!_presentation.Models.SkelGetBone(ArgInt(0), ArgInt(1), absolute, out LegacyBoneTransform bone)) return;
        _host.SetVector(_g.VForward, bone.Forward);
        _host.SetVector(_g.VRight, bone.Right);
        _host.SetVector(_g.VUp, bone.Up);
        vm.ReturnVector(bone.Origin);
    }

    private LegacyBoneTransform BoneArgument(QcVector origin) => new()
    {
        Origin = origin, Forward = _host.GetVector(_g.VForward), Right = _host.GetVector(_g.VRight), Up = _host.GetVector(_g.VUp),
    };

    // #271 skel_set_bone, #272 skel_mul_bone: void(float skel, float bonenum, vector org); the axes
    // come from v_forward, v_right, v_up.
    private void SkelSetBone(QcVm vm, bool multiply, string name)
    {
        Parms(3, name);
        _presentation.Models.SkelSetBone(ArgInt(0), ArgInt(1), BoneArgument(vm.ArgVector(2)), multiply);
    }

    // #273 void(float skel, float startbone, float endbone, vector org) skel_mul_bones
    private void SkelMulBones(QcVm vm)
    {
        Parms(4, "VM_CL_skel_mul_bones");
        _presentation.Models.SkelMulBones(ArgInt(0), ArgInt(1), ArgInt(2), BoneArgument(vm.ArgVector(3)));
    }

    // #276 float(float modlindex, string framename) frameforname
    private void FrameForName(QcVm vm)
    {
        Parms(2, "VM_CL_frameforname");
        string? model = _state.ModelNameForIndex(ArgInt(0));
        vm.ReturnFloat(model is null ? -1 : _presentation.Models.FrameForName(model, vm.ArgString(1)));
    }

    // #277 float(float modlindex, float framenum) frameduration
    private void FrameDuration(QcVm vm)
    {
        Parms(2, "VM_CL_frameduration");
        string? model = _state.ModelNameForIndex(ArgInt(0));
        vm.ReturnFloat(model is null ? 0 : _presentation.Models.FrameDuration(model, ArgInt(1)));
    }
}
