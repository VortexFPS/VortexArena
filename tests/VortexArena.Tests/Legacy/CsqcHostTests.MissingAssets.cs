using VortexArena.Legacy.Csqc;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// A community server's client program names models and sounds this client may not have (its packages
/// did not arrive, or never held them). DarkPlaces warns and goes on; so must this client - a missing
/// file is never what ends a program.
/// </summary>
public partial class CsqcHostTests
{
    [Fact]
    public void MissingModelsAndSounds_AreWarnings_NeverFaults()
    {
        using Rig rig = new();
        Asm a = new();
        int e = a.E("e"), result = a.F("result"), org = a.V("org", 1, 2, 3);
        int model = a.Text("models/smbmod/not_shipped.md3"), sample = a.Text("smbmod/not_shipped.wav"), effect = a.Text("SMBMOD_NOT_AN_EFFECT");
        int one = a.Const(1), zero = a.Const(0);
        a.Begin("all");
        a.Get(14, e);                                    // spawn
        a.Call(20, model);                               // precache_model
        a.Call(3, e, model);                             // setmodel
        a.Call(19, sample);                              // precache_sound
        a.Call(8, e, one, sample, one, one);             // sound
        a.Call(483, Asm.Vec(org), sample, one, one);     // pointsound
        a.Call(74, Asm.Vec(org), sample, one, one);      // ambientsound
        a.Call(177, sample);                             // localsound
        a.Get(534, result, sample);                      // soundlength
        a.Call(404, Asm.Vec(org), model, zero, one, one); // effect: a sprite that is not there
        a.Get(335, result, effect);                      // particleeffectnum: an effect no effectinfo.txt names
        a.Call(337, result, Asm.Vec(org), Asm.Vec(org), one);   // pointparticles with whatever that answered
        a.End();
        CsqcHost host = rig.Load(a);
        rig.Presentation.SoundsMissing = true;

        host.Vm.Execute(host.Vm.FindFunction("all"));

        Assert.False(host.Faulted);
        Assert.Empty(host.UnimplementedBuiltins);
        Assert.Contains(rig.Warnings, w => w.Contains("VM_CL_precache_model: model \"models/smbmod/not_shipped.md3\" not found"));
        Assert.Contains(rig.Warnings, w => w.Contains("VM_precache_sound: Failed to load smbmod/not_shipped.wav"));
        Assert.Contains(rig.Warnings, w => w.Contains("VM_localsound: Failed to play smbmod/not_shipped.wav"));
        Assert.Contains("VM_CL_effect: Could not load model 'models/smbmod/not_shipped.md3'", rig.Printed.ToString());
        // The frame goes on afterwards.
        Assert.True(host.UpdateView(640, 480));
    }
}
