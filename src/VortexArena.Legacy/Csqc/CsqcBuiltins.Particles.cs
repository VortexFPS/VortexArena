// Port of Base/darkplaces/clvm_cmds.c "DP_CSQC_SPAWNPARTICLE, a QC hook to engine's CL_NewParticle":
// vmparticletheme_t, vmparticlespawner_t, VM_InitParticleSpawner, VM_ResetParticleTheme,
// VM_CL_ParticleThemeToGlobals, VM_CL_ParticleThemeFromGlobals, VM_CL_InitParticleSpawner,
// VM_CL_ResetParticle, VM_CL_ParticleTheme, VM_CL_ParticleThemeSave, VM_CL_ParticleThemeFree,
// VM_CL_SpawnParticle, VM_CL_SpawnParticleDelayed; and the argument checks at the head of
// cl_particles.c CL_NewParticle. The particle itself is the presentation's (ILegacyEffects.SpawnParticle).
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Csqc;

public sealed partial class CsqcBuiltins
{
    // vmparticletheme_t. A slot that was never saved is all zeros, as the C's zeroed allocation is.
    private struct ParticleTheme
    {
        public bool Initialized;
        public ushort TypeIndex;
        public int BlendMode, Orientation;
        public int Color1, Color2, Tex;
        public float Size, SizeIncrease, Alpha, AlphaFade, Gravity, Bounce, AirFriction, LiquidFriction, OriginJitter, VelocityJitter;
        public bool QualityReduction;
        public float Lifetime, Stretch;
        public int StainColor1, StainColor2, StainTex;
        public float StainAlpha, StainSize, DelaySpawn, DelayCollision, Angle, Spin;
    }

    // The program's particle_* globals (dpdefs/csprogsdefs.qc). A program that declares none of them
    // reads zeros and its writes go nowhere.
    private int _pgType = -1, _pgBlendMode = -1, _pgOrientation = -1, _pgColor1 = -1, _pgColor2 = -1, _pgTex = -1, _pgSize = -1,
        _pgSizeIncrease = -1, _pgAlpha = -1, _pgAlphaFade = -1, _pgTime = -1, _pgGravity = -1, _pgBounce = -1, _pgAirFriction = -1,
        _pgLiquidFriction = -1, _pgOriginJitter = -1, _pgVelocityJitter = -1, _pgQualityReduction = -1, _pgStretch = -1,
        _pgStainColor1 = -1, _pgStainColor2 = -1, _pgStainTex = -1, _pgStainAlpha = -1, _pgStainSize = -1, _pgDelaySpawn = -1,
        _pgDelayCollision = -1, _pgAngle = -1, _pgSpin = -1;

    // vmpartspawner. DarkPlaces keeps it in a C global that outlives the program (a second program finds
    // the first one's themes and need not call initparticlespawner); here it is the loaded program's own.
    private ParticleTheme[] _particleThemes = Array.Empty<ParticleTheme>();
    private bool _particleSpawnerVerified;

    /// <summary>vmpartspawner.max_themes: 0 until the program has called initparticlespawner.</summary>
    public int ParticleThemeCapacity => _particleThemes.Length;

    private void RegisterParticleSpawner()
    {
        // The offset is kept only if every cell of the global exists (as CsqcGlobalOffsets does).
        int S(string name) => _vm.FindGlobal(name) is { } d && d.Offset >= ProgsFile.ReservedOfs && d.Offset < _vm.NumGlobals ? d.Offset : -1;
        int V(string name) => _vm.FindGlobal(name) is { } d && d.Offset >= ProgsFile.ReservedOfs && d.Offset + 3 <= _vm.NumGlobals ? d.Offset : -1;
        _pgType = S("particle_type"); _pgBlendMode = S("particle_blendmode"); _pgOrientation = S("particle_orientation");
        _pgColor1 = V("particle_color1"); _pgColor2 = V("particle_color2"); _pgTex = S("particle_tex");
        _pgSize = S("particle_size"); _pgSizeIncrease = S("particle_sizeincrease"); _pgAlpha = S("particle_alpha");
        _pgAlphaFade = S("particle_alphafade"); _pgTime = S("particle_time"); _pgGravity = S("particle_gravity");
        _pgBounce = S("particle_bounce"); _pgAirFriction = S("particle_airfriction"); _pgLiquidFriction = S("particle_liquidfriction");
        _pgOriginJitter = S("particle_originjitter"); _pgVelocityJitter = S("particle_velocityjitter");
        _pgQualityReduction = S("particle_qualityreduction"); _pgStretch = S("particle_stretch");
        _pgStainColor1 = V("particle_staincolor1"); _pgStainColor2 = V("particle_staincolor2"); _pgStainTex = S("particle_staintex");
        _pgStainAlpha = S("particle_stainalpha"); _pgStainSize = S("particle_stainsize"); _pgDelaySpawn = S("particle_delayspawn");
        _pgDelayCollision = S("particle_delaycollision"); _pgAngle = S("particle_angle"); _pgSpin = S("particle_spin");

        Here(522, "initparticlespawner", InitParticleSpawner);
        Here(523, "resetparticle", ResetParticle);
        Here(524, "particletheme", SelectParticleTheme);
        Here(525, "particlethemesave", ParticleThemeSave);
        Here(526, "particlethemefree", ParticleThemeFree);
        Forward(527, "spawnparticle", SpawnParticle);
        Forward(528, "delayedparticle", SpawnParticleDelayed);
    }

    // "(int)vec[0] << 16) + ((int)vec[1] << 8) + (int)vec[2]", which is also what the stain colours'
    // "*65536 + *256 +" comes to in 32-bit arithmetic.
    private static int PackColor(QcVector c) => unchecked((Int(c.X) << 16) + (Int(c.Y) << 8) + Int(c.Z));

    private static QcVector UnpackColor(int c) => new((c >> 16) & 0xFF, (c >> 8) & 0xFF, c & 0xFF);

    // VM_ResetParticleTheme. The C leaves stainalpha and stainsize as they were.
    private static void ResetParticleTheme(ref ParticleTheme theme)
    {
        theme.Initialized = true;
        theme.TypeIndex = 2; // pt_static
        theme.BlendMode = 1; // PBLEND_ADD
        theme.Orientation = 0; // PARTICLE_BILLBOARD
        theme.Color1 = 0x808080;
        theme.Color2 = 0xFFFFFF;
        theme.Tex = 63;
        theme.Size = 2;
        theme.SizeIncrease = 0;
        theme.Alpha = 256;
        theme.AlphaFade = 512;
        theme.Gravity = 0;
        theme.Bounce = 0;
        theme.AirFriction = 1;
        theme.LiquidFriction = 4;
        theme.OriginJitter = 0;
        theme.VelocityJitter = 0;
        theme.QualityReduction = false;
        theme.Lifetime = 4;
        theme.Stretch = 1;
        theme.StainColor1 = -1;
        theme.StainColor2 = -1;
        theme.StainTex = -1;
        theme.DelaySpawn = 0;
        theme.DelayCollision = 0;
        theme.Angle = 0;
        theme.Spin = 0;
    }

    // VM_CL_ParticleThemeToGlobals.
    private void ParticleThemeToGlobals(in ParticleTheme theme)
    {
        _host.SetFloat(_pgType, theme.TypeIndex);
        _host.SetFloat(_pgBlendMode, theme.BlendMode);
        _host.SetFloat(_pgOrientation, theme.Orientation);
        _host.SetVector(_pgColor1, UnpackColor(theme.Color1));
        _host.SetVector(_pgColor2, UnpackColor(theme.Color2));
        _host.SetFloat(_pgTex, theme.Tex);
        _host.SetFloat(_pgSize, theme.Size);
        _host.SetFloat(_pgSizeIncrease, theme.SizeIncrease);
        _host.SetFloat(_pgAlpha, theme.Alpha / 256);
        _host.SetFloat(_pgAlphaFade, theme.AlphaFade / 256);
        _host.SetFloat(_pgTime, theme.Lifetime);
        _host.SetFloat(_pgGravity, theme.Gravity);
        _host.SetFloat(_pgBounce, theme.Bounce);
        _host.SetFloat(_pgAirFriction, theme.AirFriction);
        _host.SetFloat(_pgLiquidFriction, theme.LiquidFriction);
        _host.SetFloat(_pgOriginJitter, theme.OriginJitter);
        _host.SetFloat(_pgVelocityJitter, theme.VelocityJitter);
        _host.SetFloat(_pgQualityReduction, theme.QualityReduction ? 1 : 0);
        _host.SetFloat(_pgStretch, theme.Stretch);
        // -1 ("the particle's own colour") comes back as '255 255 255', as in the C.
        _host.SetVector(_pgStainColor1, UnpackColor(theme.StainColor1));
        _host.SetVector(_pgStainColor2, UnpackColor(theme.StainColor2));
        _host.SetFloat(_pgStainTex, theme.StainTex);
        _host.SetFloat(_pgStainAlpha, theme.StainAlpha / 256);
        _host.SetFloat(_pgStainSize, theme.StainSize);
        _host.SetFloat(_pgDelaySpawn, theme.DelaySpawn);
        _host.SetFloat(_pgDelayCollision, theme.DelayCollision);
        _host.SetFloat(_pgAngle, theme.Angle);
        _host.SetFloat(_pgSpin, theme.Spin);
    }

    // VM_CL_ParticleThemeFromGlobals.
    private void ParticleThemeFromGlobals(ref ParticleTheme theme)
    {
        theme.TypeIndex = unchecked((ushort)Int(_host.GetFloat(_pgType)));
        theme.BlendMode = Int(_host.GetFloat(_pgBlendMode));
        theme.Orientation = Int(_host.GetFloat(_pgOrientation));
        theme.Color1 = PackColor(_host.GetVector(_pgColor1));
        theme.Color2 = PackColor(_host.GetVector(_pgColor2));
        theme.Tex = Int(_host.GetFloat(_pgTex));
        theme.Size = _host.GetFloat(_pgSize);
        theme.SizeIncrease = _host.GetFloat(_pgSizeIncrease);
        theme.Alpha = _host.GetFloat(_pgAlpha) * 256;
        theme.AlphaFade = _host.GetFloat(_pgAlphaFade) * 256;
        theme.Lifetime = _host.GetFloat(_pgTime);
        theme.Gravity = _host.GetFloat(_pgGravity);
        theme.Bounce = _host.GetFloat(_pgBounce);
        theme.AirFriction = _host.GetFloat(_pgAirFriction);
        theme.LiquidFriction = _host.GetFloat(_pgLiquidFriction);
        theme.OriginJitter = _host.GetFloat(_pgOriginJitter);
        theme.VelocityJitter = _host.GetFloat(_pgVelocityJitter);
        theme.QualityReduction = _host.GetFloat(_pgQualityReduction) != 0;
        theme.Stretch = _host.GetFloat(_pgStretch);
        theme.StainColor1 = PackColor(_host.GetVector(_pgStainColor1));
        theme.StainColor2 = PackColor(_host.GetVector(_pgStainColor2));
        theme.StainTex = Int(_host.GetFloat(_pgStainTex));
        theme.StainAlpha = _host.GetFloat(_pgStainAlpha) * 256;
        theme.StainSize = _host.GetFloat(_pgStainSize);
        theme.DelaySpawn = _host.GetFloat(_pgDelaySpawn);
        theme.DelayCollision = _host.GetFloat(_pgDelayCollision);
        theme.Angle = _host.GetFloat(_pgAngle);
        theme.Spin = _host.GetFloat(_pgSpin);
    }

    // #522 float(float max_themes) initparticlespawner. "bound max themes to not be an insane value";
    // a second call throws every saved theme away ("reallocate"). Called with no argument the C reads
    // whatever the parameter cell holds, and so does this.
    private void InitParticleSpawner(QcVm vm)
    {
        Parms(0, 1, "VM_CL_InitParticleSpawner");
        int maxThemes = Math.Clamp(ArgInt(0), 4, 2048);
        _particleThemes = new ParticleTheme[maxThemes];
        _particleSpawnerVerified = true;
        ResetParticleTheme(ref _particleThemes[0]);
        vm.ReturnFloat(1);
    }

    // #523 void() resetparticle: the globals take theme 0, the defaults.
    private void ResetParticle(QcVm vm)
    {
        Parms(0, "VM_CL_ResetParticle");
        if (!_particleSpawnerVerified)
        {
            Warning("VM_CL_ResetParticle: particle spawner not initialized\n");
            return;
        }
        ParticleThemeToGlobals(_particleThemes[0]);
    }

    // #524 void(float theme) particletheme: the globals take a saved theme, or the defaults if there is none.
    private void SelectParticleTheme(QcVm vm)
    {
        Parms(1, "VM_CL_ParticleTheme");
        if (!_particleSpawnerVerified)
        {
            Warning("VM_CL_ParticleTheme: particle spawner not initialized\n");
            return;
        }
        int number = ArgInt(0);
        if (number < 0 || number >= _particleThemes.Length)
        {
            Warning($"VM_CL_ParticleTheme: bad theme number {number}\n");
            ParticleThemeToGlobals(_particleThemes[0]);
            return;
        }
        if (!_particleThemes[number].Initialized)
        {
            Warning($"VM_CL_ParticleTheme: theme #{number} not exists\n");
            ParticleThemeToGlobals(_particleThemes[0]);
            return;
        }
        ParticleThemeToGlobals(_particleThemes[number]);
    }

    // #525 float() particlethemesave: the globals go into the first free slot, whose number is returned
    // (-1 when there is none); void(float theme) particlethemeupdate: into that slot, which returns nothing.
    private void ParticleThemeSave(QcVm vm)
    {
        Parms(0, 1, "VM_CL_ParticleThemeSave");
        if (!_particleSpawnerVerified)
        {
            Warning("VM_CL_ParticleThemeSave: particle spawner not initialized\n");
            return;
        }
        int number;
        if (vm.ArgCount < 1)
        {
            for (number = 0; number < _particleThemes.Length; number++)
                if (!_particleThemes[number].Initialized) break;
            if (number >= _particleThemes.Length)
            {
                Warning(_particleThemes.Length == 2048
                    ? "VM_CL_ParticleThemeSave: no free theme slots\n"
                    : "VM_CL_ParticleThemeSave: no free theme slots, try initparticlespawner() with highter max_themes\n");
                vm.ReturnFloat(-1);
                return;
            }
            _particleThemes[number].Initialized = true;
            ParticleThemeFromGlobals(ref _particleThemes[number]);
            vm.ReturnFloat(number);
            return;
        }
        number = ArgInt(0);
        if (number < 0 || number >= _particleThemes.Length)
        {
            Warning($"VM_CL_ParticleThemeSave: bad theme number {number}\n");
            return;
        }
        _particleThemes[number].Initialized = true;
        ParticleThemeFromGlobals(ref _particleThemes[number]);
    }

    // #526 void(float theme) particlethemefree. Theme 0 cannot be freed.
    private void ParticleThemeFree(QcVm vm)
    {
        Parms(1, "VM_CL_ParticleThemeFree");
        if (!_particleSpawnerVerified)
        {
            Warning("VM_CL_ParticleThemeFree: particle spawner not initialized\n");
            return;
        }
        int number = ArgInt(0);
        if (number <= 0 || number >= _particleThemes.Length)
        {
            Warning($"VM_CL_ParticleThemeFree: bad theme number {number}\n");
            return;
        }
        if (!_particleThemes[number].Initialized)
        {
            Warning($"VM_CL_ParticleThemeFree: theme #{number} already freed\n");
            ParticleThemeToGlobals(_particleThemes[0]);
            return;
        }
        ResetParticleTheme(ref _particleThemes[number]);
        _particleThemes[number].Initialized = false;
    }

    // The CL_NewParticle call of the "global-set particle" branch: every argument read from the globals.
    private LegacySpawnParticle ParticleFromGlobals(QcVector origin, QcVector velocity) => new()
    {
        Origin = origin,
        Velocity = velocity,
        Type = unchecked((ushort)Int(_host.GetFloat(_pgType))),
        Color1 = PackColor(_host.GetVector(_pgColor1)),
        Color2 = PackColor(_host.GetVector(_pgColor2)),
        Texture = Int(_host.GetFloat(_pgTex)),
        Size = _host.GetFloat(_pgSize),
        SizeIncrease = _host.GetFloat(_pgSizeIncrease),
        Alpha = _host.GetFloat(_pgAlpha) * 256,
        AlphaFade = _host.GetFloat(_pgAlphaFade) * 256,
        Gravity = _host.GetFloat(_pgGravity),
        Bounce = _host.GetFloat(_pgBounce),
        AirFriction = _host.GetFloat(_pgAirFriction),
        LiquidFriction = _host.GetFloat(_pgLiquidFriction),
        OriginJitter = _host.GetFloat(_pgOriginJitter),
        VelocityJitter = _host.GetFloat(_pgVelocityJitter),
        QualityReduction = _host.GetFloat(_pgQualityReduction) != 0,
        Lifetime = _host.GetFloat(_pgTime),
        Stretch = _host.GetFloat(_pgStretch),
        Blend = Int(_host.GetFloat(_pgBlendMode)),
        Orientation = Int(_host.GetFloat(_pgOrientation)),
        StainColor1 = PackColor(_host.GetVector(_pgStainColor1)),
        StainColor2 = PackColor(_host.GetVector(_pgStainColor2)),
        StainTexture = Int(_host.GetFloat(_pgStainTex)),
        StainAlpha = _host.GetFloat(_pgStainAlpha) * 256,
        StainSize = _host.GetFloat(_pgStainSize),
        Angle = _host.GetFloat(_pgAngle),
        Spin = _host.GetFloat(_pgSpin),
    };

    // The CL_NewParticle call of the "quick themed particle" branch. The C does not ask whether the theme
    // was ever saved: a slot that was not is all zeros, which is a particle of type 0 (pt_dead).
    private static LegacySpawnParticle ParticleFromTheme(in ParticleTheme theme, QcVector origin, QcVector velocity) => new()
    {
        Origin = origin,
        Velocity = velocity,
        Type = theme.TypeIndex,
        Color1 = theme.Color1,
        Color2 = theme.Color2,
        Texture = theme.Tex,
        Size = theme.Size,
        SizeIncrease = theme.SizeIncrease,
        Alpha = theme.Alpha,
        AlphaFade = theme.AlphaFade,
        Gravity = theme.Gravity,
        Bounce = theme.Bounce,
        AirFriction = theme.AirFriction,
        LiquidFriction = theme.LiquidFriction,
        OriginJitter = theme.OriginJitter,
        VelocityJitter = theme.VelocityJitter,
        QualityReduction = theme.QualityReduction,
        Lifetime = theme.Lifetime,
        Stretch = theme.Stretch,
        Blend = theme.BlendMode,
        Orientation = theme.Orientation,
        StainColor1 = theme.StainColor1,
        StainColor2 = theme.StainColor2,
        StainTexture = theme.StainTex,
        StainAlpha = theme.StainAlpha,
        StainSize = theme.StainSize,
        Angle = theme.Angle,
        Spin = theme.Spin,
    };

    // The head of CL_NewParticle: "the type and texture numbers can come from effectinfo.txt or from CSQC
    // and index fixed size tables". What passes is the presentation's to make, or to refuse (cl_particles
    // 0, no free particle).
    private bool NewParticle(ref LegacySpawnParticle particle)
    {
        if (particle.Type >= LegacySpawnParticle.TypeCount || (uint)particle.Texture >= LegacySpawnParticle.MaxTextures) return false;
        if (particle.StainTexture >= LegacySpawnParticle.MaxTextures) particle.StainTexture = -1;
        return _presentation.Effects.SpawnParticle(particle);
    }

    // #527 float(vector org, vector vel) spawnparticle, float(vector org, vector vel, float theme) quickparticle:
    // 1 if a particle was made.
    private void SpawnParticle(QcVm vm)
    {
        Parms(2, 3, "VM_CL_SpawnParticle");
        vm.ReturnFloat(0);
        if (!_particleSpawnerVerified)
        {
            Warning("VM_CL_SpawnParticle: particle spawner not initialized\n");
            return;
        }
        LegacySpawnParticle particle;
        if (vm.ArgCount < 3)
        {
            particle = ParticleFromGlobals(vm.ArgVector(0), vm.ArgVector(1));
            // "if (particle_delayspawn) part->delayedspawn = cl.time + particle_delayspawn"
            particle.Delay = _host.GetFloat(_pgDelaySpawn);
        }
        else
        {
            int number = ArgInt(2);
            if (number <= 0 || number >= _particleThemes.Length)
            {
                Warning($"VM_CL_SpawnParticle: bad theme number {number}\n");
                return;
            }
            ref readonly ParticleTheme theme = ref _particleThemes[number];
            particle = ParticleFromTheme(theme, vm.ArgVector(0), vm.ArgVector(1));
            particle.Delay = theme.DelaySpawn;
        }
        vm.ReturnFloat(NewParticle(ref particle) ? 1 : 0);
    }

    // #528 float(vector org, vector vel, float delay, float collisiondelay) delayedparticle, and with a fifth
    // argument quickdelayedparticle. The collision delay is read by nothing in DarkPlaces either
    // ("//part->delayedcollisions = ...").
    private void SpawnParticleDelayed(QcVm vm)
    {
        Parms(4, 5, "VM_CL_SpawnParticleDelayed");
        vm.ReturnFloat(0);
        if (!_particleSpawnerVerified)
        {
            Warning("VM_CL_SpawnParticleDelayed: particle spawner not initialized\n");
            return;
        }
        LegacySpawnParticle particle;
        if (vm.ArgCount < 5) particle = ParticleFromGlobals(vm.ArgVector(0), vm.ArgVector(1));
        else
        {
            int number = ArgInt(4);
            if (number <= 0 || number >= _particleThemes.Length)
            {
                Warning($"VM_CL_SpawnParticleDelayed: bad theme number {number}\n");
                return;
            }
            particle = ParticleFromTheme(_particleThemes[number], vm.ArgVector(0), vm.ArgVector(1));
        }
        // "part->delayedspawn = cl.time + PRVM_G_FLOAT(OFS_PARM2)", whatever the theme or the globals say.
        particle.Delay = vm.ArgFloat(2);
        vm.ReturnFloat(NewParticle(ref particle) ? 1 : 0);
    }
}
