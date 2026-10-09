using System;
using System.Linq;
using VortexArena.Legacy.Csqc;
using VortexArena.QuakeC;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// The DP_CSQC_SPAWNPARTICLE builtins (#522 to #528) as clvm_cmds.c has them: the theme table, the
/// particle_* globals, what reaches CL_NewParticle, and what a wrong number gets.
/// </summary>
public partial class CsqcHostTests
{
    private static readonly string[] ParticleFloats =
    {
        "particle_type", "particle_blendmode", "particle_orientation", "particle_tex", "particle_size", "particle_sizeincrease",
        "particle_alpha", "particle_alphafade", "particle_time", "particle_gravity", "particle_bounce", "particle_airfriction",
        "particle_liquidfriction", "particle_originjitter", "particle_velocityjitter", "particle_qualityreduction", "particle_stretch",
        "particle_staintex", "particle_stainalpha", "particle_stainsize", "particle_delayspawn", "particle_delaycollision",
        "particle_angle", "particle_spin",
    };

    private static readonly string[] ParticleVectors = { "particle_color1", "particle_color2", "particle_staincolor1", "particle_staincolor2" };

    /// <summary>A program with the particle globals and one function per way of calling the spawner. Its
    /// inputs are the globals "themes", "theme", "org", "vel", "delay"; a builtin's answer lands in "result".</summary>
    private static Asm ParticleProgram()
    {
        Asm a = new();
        foreach (string name in ParticleFloats) a.F(name);
        foreach (string name in ParticleVectors) a.V(name);
        int themes = a.F("themes", 8), theme = a.F("theme"), result = a.F("result", -99), org = a.V("org"), vel = a.V("vel"),
            delay = a.F("delay"), collisionDelay = a.F("collisiondelay");
        a.Begin("init"); a.Get(522, result, themes); a.End();
        a.Begin("init_noarg"); a.Get(522, result); a.End();
        a.Begin("reset"); a.Call(523); a.End();
        a.Begin("select"); a.Call(524, theme); a.End();
        a.Begin("save"); a.Get(525, result); a.End();
        a.Begin("update"); a.Call(525, theme); a.End();
        a.Begin("free"); a.Call(526, theme); a.End();
        a.Begin("spawn"); a.Get(527, result, Asm.Vec(org), Asm.Vec(vel)); a.End();
        a.Begin("quick"); a.Get(527, result, Asm.Vec(org), Asm.Vec(vel), theme); a.End();
        a.Begin("delayed"); a.Get(528, result, Asm.Vec(org), Asm.Vec(vel), delay, collisionDelay); a.End();
        a.Begin("quickdelayed"); a.Get(528, result, Asm.Vec(org), Asm.Vec(vel), delay, collisionDelay, theme); a.End();
        a.Begin("spawn_oneparm"); a.Call(527, Asm.Vec(org)); a.End();
        a.Begin("free_noparm"); a.Call(526); a.End();
        return a;
    }

    private static void SetF(Rig rig, string name, float value) => rig.Host!.Vm.GlobalFloat(rig.Host.Vm.FindGlobal(name)!.Offset) = value;
    private static void SetV(Rig rig, string name, float x, float y, float z) => rig.Host!.Vm.GlobalVector(rig.Host.Vm.FindGlobal(name)!.Offset) = new QcVector(x, y, z);

    private static float Run(Rig rig, string function)
    {
        SetF(rig, "result", -99);
        rig.Host!.Vm.Execute(rig.Host.Vm.FindFunction(function));
        return rig.Global("result");
    }

    // Distinct values in every global, so a field that lands in the wrong place shows.
    private static void FillParticleGlobals(Rig rig, float offset = 0)
    {
        SetF(rig, "particle_type", 3); SetF(rig, "particle_blendmode", 2); SetF(rig, "particle_orientation", 1);
        SetV(rig, "particle_color1", 16 + offset, 32, 48); SetV(rig, "particle_color2", 255, 128.9f, 1);
        SetF(rig, "particle_tex", 41 + offset); SetF(rig, "particle_size", 5.5f + offset); SetF(rig, "particle_sizeincrease", -1.25f);
        SetF(rig, "particle_alpha", 0.5f); SetF(rig, "particle_alphafade", 1.5f); SetF(rig, "particle_time", 7);
        SetF(rig, "particle_gravity", 0.75f); SetF(rig, "particle_bounce", 1.5f); SetF(rig, "particle_airfriction", 0.2f);
        SetF(rig, "particle_liquidfriction", 0.8f); SetF(rig, "particle_originjitter", 3); SetF(rig, "particle_velocityjitter", 9);
        SetF(rig, "particle_qualityreduction", 1); SetF(rig, "particle_stretch", 0.25f);
        SetV(rig, "particle_staincolor1", 1, 2, 3); SetV(rig, "particle_staincolor2", 4, 5, 6);
        SetF(rig, "particle_staintex", 17); SetF(rig, "particle_stainalpha", 0.25f); SetF(rig, "particle_stainsize", 2.5f);
        SetF(rig, "particle_delayspawn", 0); SetF(rig, "particle_delaycollision", 0.5f);
        SetF(rig, "particle_angle", 45); SetF(rig, "particle_spin", 90);
    }

    [Fact]
    public void ParticleSpawner_BeforeInit_EveryBuiltinWarnsAndDoesNothing()
    {
        using Rig rig = new();
        rig.Load(ParticleProgram());
        Assert.Equal(0, rig.Host!.Builtins.ParticleThemeCapacity);
        SetF(rig, "particle_size", 123);

        Run(rig, "reset");
        Run(rig, "select");
        Run(rig, "save");   // the C returns without writing an answer: the program reads a stale return cell
        Run(rig, "update");
        Run(rig, "free");
        Assert.Equal(0, Run(rig, "spawn"));
        Assert.Equal(0, Run(rig, "quick"));
        Assert.Equal(0, Run(rig, "delayed"));
        Assert.Equal(0, Run(rig, "quickdelayed"));

        Assert.Equal(123, rig.Global("particle_size"));
        Assert.False(rig.Presentation.Calls.ContainsKey("SpawnParticle"));
        Assert.Empty(rig.Presentation.SpawnedParticles);
        foreach (string name in new[] { "VM_CL_ResetParticle", "VM_CL_ParticleTheme", "VM_CL_ParticleThemeSave", "VM_CL_ParticleThemeFree", "VM_CL_SpawnParticle", "VM_CL_SpawnParticleDelayed" })
            Assert.Contains(rig.Warnings, w => w.Contains(name + ": particle spawner not initialized"));
        Assert.Equal(9, rig.Warnings.Count);
        Assert.False(rig.Host.Faulted);
    }

    [Theory]
    [InlineData(8f, 8)]
    [InlineData(0f, 4)]
    [InlineData(-5f, 4)]
    [InlineData(2048f, 2048)]
    [InlineData(1e9f, 2048)]
    [InlineData(float.NaN, 4)]
    public void ParticleSpawner_Init_BoundsTheThemeCount(float asked, int capacity)
    {
        using Rig rig = new();
        rig.Load(ParticleProgram());
        SetF(rig, "themes", asked);
        Assert.Equal(1, Run(rig, "init"));
        Assert.Equal(capacity, rig.Host!.Builtins.ParticleThemeCapacity);
        // Without an argument the C reads the parameter cell as it stands; whatever is there is bounded too.
        Run(rig, "init_noarg");
        Assert.InRange(rig.Host.Builtins.ParticleThemeCapacity, 4, 2048);
    }

    [Fact]
    public void ParticleSpawner_Reset_WritesTheDefaultThemeIntoTheGlobals()
    {
        using Rig rig = new();
        rig.Load(ParticleProgram());
        Run(rig, "init");
        FillParticleGlobals(rig);
        Run(rig, "reset");

        Assert.Equal(2, rig.Global("particle_type"));          // pt_static
        Assert.Equal(1, rig.Global("particle_blendmode"));     // PBLEND_ADD
        Assert.Equal(0, rig.Global("particle_orientation"));   // PARTICLE_BILLBOARD
        Assert.Equal(new QcVector(128, 128, 128), rig.GlobalVector("particle_color1"));
        Assert.Equal(new QcVector(255, 255, 255), rig.GlobalVector("particle_color2"));
        Assert.Equal(63, rig.Global("particle_tex"));
        Assert.Equal(2, rig.Global("particle_size"));
        Assert.Equal(0, rig.Global("particle_sizeincrease"));
        Assert.Equal(1, rig.Global("particle_alpha"));         // 256 / 256
        Assert.Equal(2, rig.Global("particle_alphafade"));     // 512 / 256
        Assert.Equal(4, rig.Global("particle_time"));
        Assert.Equal(0, rig.Global("particle_gravity"));
        Assert.Equal(0, rig.Global("particle_bounce"));
        Assert.Equal(1, rig.Global("particle_airfriction"));
        Assert.Equal(4, rig.Global("particle_liquidfriction"));
        Assert.Equal(0, rig.Global("particle_originjitter"));
        Assert.Equal(0, rig.Global("particle_velocityjitter"));
        Assert.Equal(0, rig.Global("particle_qualityreduction"));
        Assert.Equal(1, rig.Global("particle_stretch"));
        // staincolor -1 ("the particle's colour") reads back as 255 255 255, and the C never gives the
        // default theme a stain alpha or size.
        Assert.Equal(new QcVector(255, 255, 255), rig.GlobalVector("particle_staincolor1"));
        Assert.Equal(new QcVector(255, 255, 255), rig.GlobalVector("particle_staincolor2"));
        Assert.Equal(-1, rig.Global("particle_staintex"));
        Assert.Equal(0, rig.Global("particle_stainalpha"));
        Assert.Equal(0, rig.Global("particle_stainsize"));
        Assert.Equal(0, rig.Global("particle_delayspawn"));
        Assert.Equal(0, rig.Global("particle_delaycollision"));
        Assert.Equal(0, rig.Global("particle_angle"));
        Assert.Equal(0, rig.Global("particle_spin"));
        Assert.Empty(rig.Warnings);
    }

    [Fact]
    public void ParticleSpawner_Themes_AreSavedRestoredUpdatedAndFreed()
    {
        using Rig rig = new();
        rig.Load(ParticleProgram());
        SetF(rig, "themes", 4);
        Run(rig, "init");

        // Slot 0 is the default theme, so the first save lands in 1.
        FillParticleGlobals(rig);
        Assert.Equal(1, Run(rig, "save"));
        FillParticleGlobals(rig, offset: 100);
        Assert.Equal(2, Run(rig, "save"));

        // particletheme(1) brings the first set back, converted there and back as the C converts it.
        Run(rig, "reset");
        SetF(rig, "theme", 1);
        Run(rig, "select");
        Assert.Equal(3, rig.Global("particle_type"));
        Assert.Equal(2, rig.Global("particle_blendmode"));
        Assert.Equal(1, rig.Global("particle_orientation"));
        Assert.Equal(new QcVector(16, 32, 48), rig.GlobalVector("particle_color1"));
        Assert.Equal(new QcVector(255, 128, 1), rig.GlobalVector("particle_color2"));   // 128.9 is cut to 128
        Assert.Equal(41, rig.Global("particle_tex"));
        Assert.Equal(5.5f, rig.Global("particle_size"));
        Assert.Equal(-1.25f, rig.Global("particle_sizeincrease"));
        Assert.Equal(0.5f, rig.Global("particle_alpha"));
        Assert.Equal(1.5f, rig.Global("particle_alphafade"));
        Assert.Equal(7, rig.Global("particle_time"));
        Assert.Equal(0.75f, rig.Global("particle_gravity"));
        Assert.Equal(1.5f, rig.Global("particle_bounce"));
        Assert.Equal(0.2f, rig.Global("particle_airfriction"));
        Assert.Equal(0.8f, rig.Global("particle_liquidfriction"));
        Assert.Equal(3, rig.Global("particle_originjitter"));
        Assert.Equal(9, rig.Global("particle_velocityjitter"));
        Assert.Equal(1, rig.Global("particle_qualityreduction"));
        Assert.Equal(0.25f, rig.Global("particle_stretch"));
        Assert.Equal(new QcVector(1, 2, 3), rig.GlobalVector("particle_staincolor1"));
        Assert.Equal(new QcVector(4, 5, 6), rig.GlobalVector("particle_staincolor2"));
        Assert.Equal(17, rig.Global("particle_staintex"));
        Assert.Equal(0.25f, rig.Global("particle_stainalpha"));
        Assert.Equal(2.5f, rig.Global("particle_stainsize"));
        Assert.Equal(0.5f, rig.Global("particle_delaycollision"));
        Assert.Equal(45, rig.Global("particle_angle"));
        Assert.Equal(90, rig.Global("particle_spin"));

        SetF(rig, "theme", 2);
        Run(rig, "select");
        Assert.Equal(141, rig.Global("particle_tex"));
        Assert.Equal(105.5f, rig.Global("particle_size"));

        // particlethemeupdate(1) overwrites slot 1 with what the globals hold now (theme 2's values).
        SetF(rig, "theme", 1);
        Run(rig, "update");
        Run(rig, "reset");
        Run(rig, "select");
        Assert.Equal(141, rig.Global("particle_tex"));
        Assert.Empty(rig.Warnings);

        // Freed, the slot is "not exists": selecting it warns and gives the defaults; the next save reuses it.
        Run(rig, "free");
        Run(rig, "select");
        Assert.Contains(rig.Warnings, w => w.Contains("VM_CL_ParticleTheme: theme #1 not exists"));
        Assert.Equal(63, rig.Global("particle_tex"));
        Run(rig, "free");
        Assert.Contains(rig.Warnings, w => w.Contains("VM_CL_ParticleThemeFree: theme #1 already freed"));
        FillParticleGlobals(rig);
        Assert.Equal(1, Run(rig, "save"));
        Assert.Equal(3, Run(rig, "save"));

        // Four slots, all taken: -1, with the hint to ask for more.
        Assert.Equal(-1, Run(rig, "save"));
        Assert.Contains(rig.Warnings, w => w.Contains("VM_CL_ParticleThemeSave: no free theme slots, try initparticlespawner() with highter max_themes"));

        // initparticlespawner again throws every theme away.
        Run(rig, "init");
        Assert.Equal(1, Run(rig, "save"));
        Assert.False(rig.Host!.Faulted);
    }

    [Fact]
    public void ParticleSpawner_BadThemeNumbers_WarnAndChangeNothing()
    {
        using Rig rig = new();
        rig.Load(ParticleProgram());
        SetF(rig, "themes", 4);
        Run(rig, "init");
        FillParticleGlobals(rig);
        Assert.Equal(1, Run(rig, "save"));

        foreach (float bad in new[] { -1f, 4f, 5000f, float.NaN, float.PositiveInfinity })
        {
            rig.Warnings.Clear();
            SetF(rig, "theme", bad);
            SetF(rig, "particle_tex", 99);
            Run(rig, "select");   // warns, and loads the defaults
            Assert.Contains(rig.Warnings, w => w.Contains("VM_CL_ParticleTheme: bad theme number"));
            Assert.Equal(63, rig.Global("particle_tex"));
            Run(rig, "update");
            Assert.Contains(rig.Warnings, w => w.Contains("VM_CL_ParticleThemeSave: bad theme number"));
            Run(rig, "free");
            Assert.Contains(rig.Warnings, w => w.Contains("VM_CL_ParticleThemeFree: bad theme number"));
            Assert.Equal(0, Run(rig, "quick"));
            Assert.Contains(rig.Warnings, w => w.Contains("VM_CL_SpawnParticle: bad theme number"));
            Assert.Equal(0, Run(rig, "quickdelayed"));
            Assert.Contains(rig.Warnings, w => w.Contains("VM_CL_SpawnParticleDelayed: bad theme number"));
        }
        // Theme 0 is the default theme: it can be selected and overwritten, but not freed and not spawned from.
        rig.Warnings.Clear();
        SetF(rig, "theme", 0);
        Run(rig, "free");
        Assert.Contains(rig.Warnings, w => w.Contains("VM_CL_ParticleThemeFree: bad theme number 0"));
        Assert.Equal(0, Run(rig, "quick"));
        Assert.Contains(rig.Warnings, w => w.Contains("VM_CL_SpawnParticle: bad theme number 0"));
        Assert.Empty(rig.Presentation.SpawnedParticles);

        // Theme 1 is still what was saved.
        SetF(rig, "theme", 1);
        Run(rig, "select");
        Assert.Equal(41, rig.Global("particle_tex"));
        Assert.False(rig.Host!.Faulted);
    }

    [Fact]
    public void ParticleSpawner_Spawn_PassesTheGlobalsToNewParticleAsDarkPlacesDoes()
    {
        using Rig rig = new();
        rig.Load(ParticleProgram());
        Run(rig, "init");
        FillParticleGlobals(rig);
        SetV(rig, "org", 10, 20, 30);
        SetV(rig, "vel", -1, -2, -3);
        Assert.Equal(1, Run(rig, "spawn"));

        LegacySpawnParticle p = Assert.Single(rig.Presentation.SpawnedParticles);
        Assert.Equal(new QcVector(10, 20, 30), p.Origin);
        Assert.Equal(new QcVector(-1, -2, -3), p.Velocity);
        Assert.Equal(3, p.Type);
        Assert.Equal(2, p.Blend);
        Assert.Equal(1, p.Orientation);
        Assert.Equal(0x102030, p.Color1);
        Assert.Equal(0xFF8001, p.Color2);
        Assert.Equal(41, p.Texture);
        Assert.Equal(5.5f, p.Size);
        Assert.Equal(-1.25f, p.SizeIncrease);
        Assert.Equal(128f, p.Alpha);        // particle_alpha * 256
        Assert.Equal(384f, p.AlphaFade);    // particle_alphafade * 256
        Assert.Equal(0.75f, p.Gravity);
        Assert.Equal(1.5f, p.Bounce);
        Assert.Equal(0.2f, p.AirFriction);
        Assert.Equal(0.8f, p.LiquidFriction);
        Assert.Equal(3f, p.OriginJitter);
        Assert.Equal(9f, p.VelocityJitter);
        Assert.True(p.QualityReduction);
        Assert.Equal(7f, p.Lifetime);
        Assert.Equal(0.25f, p.Stretch);
        Assert.Equal(0x010203, p.StainColor1);
        Assert.Equal(0x040506, p.StainColor2);
        Assert.Equal(17, p.StainTexture);
        Assert.Equal(64f, p.StainAlpha);    // particle_stainalpha * 256
        Assert.Equal(2.5f, p.StainSize);
        Assert.Equal(45f, p.Angle);
        Assert.Equal(90f, p.Spin);
        Assert.Equal(0f, p.Delay);

        // particle_delayspawn delays a spawnparticle; delayedparticle's own argument replaces it.
        SetF(rig, "particle_delayspawn", 2.5f);
        Assert.Equal(1, Run(rig, "spawn"));
        Assert.Equal(2.5f, rig.Presentation.SpawnedParticles[1].Delay);
        SetF(rig, "delay", 0.75f);
        SetF(rig, "collisiondelay", 99);
        Assert.Equal(1, Run(rig, "delayed"));
        Assert.Equal(0.75f, rig.Presentation.SpawnedParticles[2].Delay);
        Assert.Equal(128f, rig.Presentation.SpawnedParticles[2].Alpha);
        Assert.Equal(3, rig.Presentation.Calls["SpawnParticle"]);
        Assert.Empty(rig.Warnings);
    }

    [Fact]
    public void ParticleSpawner_QuickParticle_UsesTheThemeAndNotTheGlobals()
    {
        using Rig rig = new();
        rig.Load(ParticleProgram());
        Run(rig, "init");
        FillParticleGlobals(rig);
        SetF(rig, "particle_delayspawn", 1.5f);
        Assert.Equal(1, Run(rig, "save"));
        Run(rig, "reset");   // the globals now hold the defaults
        SetF(rig, "theme", 1);
        SetV(rig, "org", 1, 2, 3);
        Assert.Equal(1, Run(rig, "quick"));

        LegacySpawnParticle p = Assert.Single(rig.Presentation.SpawnedParticles);
        Assert.Equal(new QcVector(1, 2, 3), p.Origin);
        Assert.Equal(3, p.Type);
        Assert.Equal(41, p.Texture);
        Assert.Equal(128f, p.Alpha);
        Assert.Equal(384f, p.AlphaFade);
        Assert.Equal(0x102030, p.Color1);
        Assert.Equal(64f, p.StainAlpha);
        Assert.Equal(1.5f, p.Delay);         // the theme's own delayspawn

        SetF(rig, "delay", 4);
        Assert.Equal(1, Run(rig, "quickdelayed"));
        Assert.Equal(4f, rig.Presentation.SpawnedParticles[1].Delay);
        Assert.Equal(41, rig.Presentation.SpawnedParticles[1].Texture);

        // A slot in range that was never saved is all zeros in the C: a particle of type 0 (pt_dead), which
        // CL_NewParticle accepts.
        SetF(rig, "theme", 5);
        Assert.Equal(1, Run(rig, "quick"));
        Assert.Equal(0, rig.Presentation.SpawnedParticles[2].Type);
        Assert.Empty(rig.Warnings);
    }

    [Fact]
    public void ParticleSpawner_NewParticle_RefusesNumbersOutsideItsTables()
    {
        using Rig rig = new();
        rig.Load(ParticleProgram());
        Run(rig, "init");
        Run(rig, "reset");

        // pt_total is 15; the particle font has 256 cells.
        foreach ((string global, float value) in new[] { ("particle_type", 15f), ("particle_type", -1f), ("particle_type", 70000f), ("particle_tex", 256f), ("particle_tex", -1f), ("particle_tex", float.NaN) })
        {
            Run(rig, "reset");
            SetF(rig, global, value);
            Assert.Equal(0, Run(rig, "spawn"));
            Assert.Equal(0, Run(rig, "delayed"));
        }
        Assert.Empty(rig.Presentation.SpawnedParticles);
        Assert.False(rig.Presentation.Calls.ContainsKey("SpawnParticle"));

        Run(rig, "reset");
        SetF(rig, "particle_type", 14);
        SetF(rig, "particle_tex", 255);
        SetF(rig, "particle_staintex", 300);   // "if (staintex >= MAX_PARTICLETEXTURES) staintex = -1"
        Assert.Equal(1, Run(rig, "spawn"));
        LegacySpawnParticle p = Assert.Single(rig.Presentation.SpawnedParticles);
        Assert.Equal(14, p.Type);
        Assert.Equal(255, p.Texture);
        Assert.Equal(-1, p.StainTexture);

        // No particle made (cl_particles 0, the pool full) is 0 to the program.
        rig.Presentation.RefuseParticles = true;
        Assert.Equal(0, Run(rig, "spawn"));
        Assert.Equal(0, Run(rig, "delayed"));
        Assert.Empty(rig.Warnings);
        Assert.False(rig.Host!.Faulted);
    }

    [Fact]
    public void ParticleSpawner_WrongArgumentCount_IsAProgramFault()
    {
        using Rig rig = new();
        rig.Load(ParticleProgram());
        Run(rig, "init");
        QcRuntimeException one = Assert.Throws<QcRuntimeException>(() => Run(rig, "spawn_oneparm"));
        Assert.Contains("VM_CL_SpawnParticle wrong parameter count", one.Message);
        QcRuntimeException none = Assert.Throws<QcRuntimeException>(() => Run(rig, "free_noparm"));
        Assert.Contains("VM_CL_ParticleThemeFree wrong parameter count", none.Message);
    }

    [Fact]
    public void ParticleSpawner_IsRegisteredUnderDarkPlacesNumbers_AndAProgramWithoutTheGlobalsIsSafe()
    {
        using Rig rig = new();
        Asm a = new();
        int result = a.F("result"), org = a.V("org");
        a.Begin("all");
        a.Call(522, a.Const(4));
        a.Call(523);
        a.Call(525);
        a.Call(524, a.Const(1));
        a.Get(527, result, Asm.Vec(org), Asm.Vec(org));
        a.Call(526, a.Const(1));
        a.End();
        CsqcHost host = rig.Load(a);
        foreach ((int number, string name) in new[] { (522, "initparticlespawner"), (523, "resetparticle"), (524, "particletheme"), (525, "particlethemesave"), (526, "particlethemefree"), (527, "spawnparticle"), (528, "delayedparticle") })
        {
            Assert.True(host.Vm.HasBuiltin(number), name);
            Assert.Contains(host.Builtins.Registered, r => r.Number == number && r.Name == name);
        }
        host.Vm.Execute(host.Vm.FindFunction("all"));
        // No particle_* globals: everything reads as zero, so the particle is type 0 with texture 0.
        Assert.Equal(1, rig.Global("result"));
        Assert.Equal(0, Assert.Single(rig.Presentation.SpawnedParticles).Type);
        Assert.Empty(host.UnimplementedBuiltins);
        Assert.False(host.Faulted);
    }
}
