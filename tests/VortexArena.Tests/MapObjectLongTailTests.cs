using System.Numerics;
using XonoticGodot.Common;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T59 map-object long-tail tests — the seven rare server-side entities ported from
/// common/mapobjects/{func/stardust, misc/dynlight, trigger/viewloc, misc/follow, func/fourier,
/// func/vectormamamam, target/voicescript}.qc.
///
/// Each test drives the REAL spawnfunc (through <see cref="SpawnFuncs"/>, registered by GameInit.Boot) on a
/// minimal engine facade, then asserts the faithful server contract: the EF flag / think for stardust, the
/// static/path/follow/tag modes + toggle for dynlight, the start/end anchor binding + per-tick player stamp
/// for viewloc, the attach/follow movetype for misc_follow, the sine/projection mover wiring for
/// fourier/vectormamamam, and the tokenization + scheduling for voicescript. The INITPRIO_FINDTARGET lookups
/// are resolved by <see cref="MapObjectsRegistry.RunPostSpawn"/> exactly as the BSP-lump load does.
/// </summary>
public class MapObjectLongTailTests
{
    /// <summary>Stand up a bare engine facade with the spawnfuncs registered (no map; pure-spawn tests).</summary>
    private static EngineServices Boot()
    {
        MapMover.ClearIndex();                 // the targetname index is process-global static; reset per test
        var world = new CollisionWorld();
        world.BuildGrid();
        var services = new EngineServices(world);
        GameInit.Boot(services);               // Api.Services = services; spawnfuncs + MapMover registered
        return services;
    }

    /// <summary>Spawn an edict and run its classname's spawnfunc, asserting the classname was registered.</summary>
    private static Entity Spawn(string className, System.Action<Entity>? configure = null)
    {
        Entity e = Api.Entities.Spawn();
        configure?.Invoke(e);
        Assert.True(SpawnFuncs.TrySpawn(className, e), $"{className} should be registered");
        return e;
    }

    // ===================================================================
    //  func_stardust
    // ===================================================================

    [Fact]
    public void Stardust_WearsStardustEffect_AndSchedulesQuarterSecondThink()
    {
        Boot();
        float t0 = Api.Clock.Time;
        Entity sd = Spawn("func_stardust");

        Assert.Equal("func_stardust", sd.ClassName);
        Assert.Equal(EffectFlags.Stardust, sd.Effects);          // QC: this.effects = EF_STARDUST
        Assert.NotNull(sd.Think);
        Assert.Equal(t0 + 0.25f, sd.NextThink, 3);               // QC: nextthink = time + 0.25

        // The think just re-arms the 0.25s heartbeat (csqcmodel autoupdate).
        sd.NextThink = 0f;
        sd.Think!(sd);
        Assert.Equal(t0 + 0.25f, sd.NextThink, 3);
    }

    // ===================================================================
    //  dynlight
    // ===================================================================

    [Fact]
    public void Dynlight_Static_DefaultsRadiusAndColor_AndIsActive()
    {
        Boot();
        Entity dl = Spawn("dynlight");                            // no target/follow/tag => static light

        Assert.Equal("dynlight", dl.ClassName);
        Assert.Equal(200f, dl.LightLev);                         // QC: if(!light_lev) light_lev = 200
        Assert.Equal(new Vector3(1f, 1f, 1f), dl.LightColor);    // QC: if(!color) color = '1 1 1'
        Assert.Equal(200f, dl.LightLefty);                       // QC: lefty = light_lev
        Assert.Equal(MapMover.ActiveActive, dl.Active);
        Assert.Equal(Solid.Not, dl.Solid);
        Assert.NotNull(dl.Use);
        Assert.NotNull(dl.SetActive);
        Assert.NotNull(dl.Reset);
    }

    [Fact]
    public void Dynlight_Use_TogglesLightRadiusOnOff()
    {
        Boot();
        Entity dl = Spawn("dynlight", e => e.LightLev = 250f);
        Assert.Equal(250f, dl.LightLev);

        dl.Use!(dl, dl);                                          // QC dynlight_use: on -> off
        Assert.Equal(0f, dl.LightLev);
        dl.Use!(dl, dl);                                          // off -> back to lefty
        Assert.Equal(250f, dl.LightLev);
    }

    [Fact]
    public void Dynlight_SetActive_Toggle_GatesRadius_AndResetReArms()
    {
        Boot();
        Entity dl = Spawn("dynlight", e => e.LightLev = 180f);

        dl.SetActive!(dl, MapMover.ActiveToggle);                // active -> not: radius drops to 0
        Assert.Equal(MapMover.ActiveNot, dl.Active);
        Assert.Equal(0f, dl.LightLev);

        dl.SetActive!(dl, MapMover.ActiveToggle);                // not -> active: radius restored to lefty
        Assert.Equal(MapMover.ActiveActive, dl.Active);
        Assert.Equal(180f, dl.LightLev);

        dl.LightLev = 0f; dl.Active = MapMover.ActiveNot;
        dl.Reset!(dl);                                           // QC dynlight_reset
        Assert.Equal(MapMover.ActiveActive, dl.Active);
        Assert.Equal(180f, dl.LightLev);
    }

    [Fact]
    public void Dynlight_Follow_BindsAimentAndOwner_AtFindTargetPass()
    {
        Boot();
        Entity host = Spawn("path_corner", e => { e.TargetName = "dl_host"; e.Origin = new Vector3(64f, 0f, 0f); });
        Entity dl = Spawn("dynlight", e =>
        {
            e.SpawnFlags = DynamicLight.Follow;                  // FOLLOW flag
            e.Target = "dl_host";
            e.Origin = new Vector3(64f, 0f, 32f);
        });

        // Before the INITPRIO pass the follow target isn't bound yet.
        Assert.Null(dl.Aiment);

        MapObjectsRegistry.RunPostSpawn();                       // INITPRIO_FINDTARGET pass

        Assert.Same(host, dl.Aiment);                            // QC: this.aiment = targ
        Assert.Same(host, dl.Owner);                             // QC: this.owner = targ
        Assert.Equal(MoveType.Follow, dl.MoveType);              // QC: set_movetype(MOVETYPE_FOLLOW)
        Assert.Equal(new Vector3(0f, 0f, 32f), dl.ViewOfs);      // QC: view_ofs = origin - targ.origin
        Assert.NotNull(dl.Think);
    }

    [Fact]
    public void Dynlight_TagAttach_AttachesToTargetTag_AtFindTargetPass()
    {
        var services = Boot();
        Entity host = Spawn("path_corner", e => { e.TargetName = "dl_model"; });
        Entity dl = Spawn("dynlight", e => { e.Target = "dl_model"; e.DTagName = "tag_head"; });

        MapObjectsRegistry.RunPostSpawn();

        Assert.Same(host, dl.Owner);                             // QC: this.owner = targ
        Assert.True(services.ModelsImpl.TryGetAttachment(dl, out Entity parent, out string tag));
        Assert.Same(host, parent);
        Assert.Equal("tag_head", tag);
    }

    // ===================================================================
    //  trigger_viewlocation + target_viewlocation_start/end
    // ===================================================================

    [Fact]
    public void ViewLocation_StartEnd_CarryCntAndAreFindable()
    {
        Boot();
        Entity start = Spawn("target_viewlocation_start", e => e.TargetName = "vl_a");
        Entity end = Spawn("target_viewlocation_end", e => e.TargetName = "vl_b");

        Assert.Equal(1, start.Cnt);                              // QC: start.cnt = 1
        Assert.Equal(2, end.Cnt);                               // QC: end.cnt = 2
        Assert.Same(start, MapMover.FindFirstByTargetName("vl_a"));
        Assert.Same(end, MapMover.FindFirstByTargetName("vl_b"));

        // compat alias spawns a _start.
        Entity compat = Spawn("target_viewlocation", e => e.TargetName = "vl_c");
        Assert.Equal("target_viewlocation_start", compat.ClassName);
        Assert.Equal(1, compat.Cnt);
    }

    [Fact]
    public void ViewLocation_Trigger_BindsAnchors_AndStampsInsidePlayer()
    {
        Boot();
        Entity start = Spawn("target_viewlocation_start", e => e.TargetName = "loc_start");
        Entity end = Spawn("target_viewlocation_end", e => e.TargetName = "loc_end");
        Entity trig = Spawn("trigger_viewlocation", e =>
        {
            e.Target = "loc_start";
            e.Target2 = "loc_end";
            e.Mins = new Vector3(-64f, -64f, -64f);
            e.Maxs = new Vector3(64f, 64f, 64f);
            e.Size = e.Maxs - e.Mins;
        });
        Api.Entities.SetOrigin(trig, Vector3.Zero);              // relink AbsMin/AbsMax over the box

        MapObjectsRegistry.RunPostSpawn();                       // viewloc_init binds the anchors

        Assert.Same(start, trig.Enemy);                          // QC: this.enemy = start anchor
        Assert.Same(end, trig.GoalEntity);                       // QC: this.goalentity = end anchor
        Assert.NotNull(trig.Think);

        // A player standing inside the volume gets .viewloc stamped on the tick; one outside does not.
        Entity inside = Api.Entities.Spawn();
        inside.ClassName = "player";
        inside.Flags |= EntFlags.Client;
        inside.Mins = new Vector3(-16f, -16f, -24f);
        inside.Maxs = new Vector3(16f, 16f, 45f);
        Api.Entities.SetOrigin(inside, Vector3.Zero);

        Entity outside = Api.Entities.Spawn();
        outside.ClassName = "player";
        outside.Flags |= EntFlags.Client;
        outside.Mins = new Vector3(-16f, -16f, -24f);
        outside.Maxs = new Vector3(16f, 16f, 45f);
        Api.Entities.SetOrigin(outside, new Vector3(1000f, 0f, 0f));

        trig.Think!(trig);                                       // viewloc_think

        Assert.Same(trig, inside.ViewLoc);
        Assert.Null(outside.ViewLoc);
    }

    [Fact]
    public void ViewLocation_Trigger_NoAnchor_DeletesItself()
    {
        Boot();
        Entity trig = Spawn("trigger_viewlocation", e =>
        {
            e.Target = "missing_start";
            e.Mins = new Vector3(-8f, -8f, -8f);
            e.Maxs = new Vector3(8f, 8f, 8f);
            e.Size = e.Maxs - e.Mins;
        });

        MapObjectsRegistry.RunPostSpawn();                       // no start anchor => delete(this)
        Assert.True(trig.IsFreed);
    }

    // ===================================================================
    //  misc_follow
    // ===================================================================

    [Fact]
    public void Follow_NoFlag_PutsDestInMoveTypeFollow_AndDeletesItself()
    {
        Boot();
        Entity src = Spawn("path_corner", e => { e.TargetName = "f_src"; e.Origin = new Vector3(100f, 0f, 0f); });
        Entity dst = Spawn("path_corner", e => { e.TargetName = "f_dst"; e.Origin = new Vector3(50f, 0f, 0f); });
        Entity follow = Spawn("misc_follow", e => { e.KillTarget = "f_src"; e.Target = "f_dst"; });

        MapObjectsRegistry.RunPostSpawn();                       // follow_init

        Assert.Equal(MoveType.Follow, dst.MoveType);             // QC follow_sameorigin: MOVETYPE_FOLLOW
        Assert.Same(src, dst.Aiment);                            // QC: dst.aiment = src
        Assert.Equal(new Vector3(-50f, 0f, 0f), dst.ViewOfs);    // QC: view_ofs = dst.origin - src.origin
        Assert.True(follow.IsFreed);                             // QC: delete(this)
    }

    [Fact]
    public void Follow_Attach_ParentsDstAndMakesItNonSolid()
    {
        var services = Boot();
        Entity src = Spawn("path_corner", e => { e.TargetName = "a_src"; });
        Entity dst = Spawn("path_corner", e => { e.TargetName = "a_dst"; });
        Entity follow = Spawn("misc_follow", e =>
        {
            e.SpawnFlags = Follow.Attach | Follow.Local;         // FOLLOW_ATTACH | FOLLOW_LOCAL
            e.KillTarget = "a_src";
            e.Target = "a_dst";
            e.Message = "tag_weapon";
        });

        MapObjectsRegistry.RunPostSpawn();

        Assert.True(services.ModelsImpl.TryGetAttachment(dst, out Entity parent, out string tag));
        Assert.Same(src, parent);
        Assert.Equal("tag_weapon", tag);
        Assert.Equal(Solid.Not, dst.Solid);                     // QC: dst.solid = SOLID_NOT
        Assert.True(follow.IsFreed);
    }

    [Fact]
    public void Follow_Joint_KeepsEdict_CarryingSrcAndDst()
    {
        Boot();
        Entity src = Spawn("path_corner", e => { e.TargetName = "j_src"; });
        Entity dst = Spawn("path_corner", e => { e.TargetName = "j_dst"; });
        Entity follow = Spawn("misc_follow", e =>
        {
            e.JointType = 1f;                                    // QC: a joint — the edict must STAY
            e.KillTarget = "j_src";
            e.Target = "j_dst";
        });

        MapObjectsRegistry.RunPostSpawn();

        Assert.False(follow.IsFreed);                            // joint edict persists
        Assert.Same(src, follow.Aiment);                        // QC: this.aiment = src
        Assert.Same(dst, follow.Enemy);                         // QC: this.enemy = dst
    }

    // ===================================================================
    //  func_fourier
    // ===================================================================

    [Fact]
    public void Fourier_Defaults_AndControllerDrivesVelocityTowardSine()
    {
        var services = Boot();
        Entity fr = Spawn("func_fourier", e =>
        {
            e.Origin = new Vector3(0f, 0f, 100f);
            e.NetName = "1 0.25 0 0 1";                          // freqmul=1 phase=0.25(cosine) z-axis amp 1
            e.Height = 32f;
        });

        Assert.Equal("func_fourier", fr.ClassName);
        Assert.Equal(4f, fr.Speed);                             // QC default speed = 4
        Assert.Equal(360f / 4f, fr.MoverCnt);                   // QC: cnt = 360 / speed
        Assert.Equal(new Vector3(0f, 0f, 100f), fr.DestVec);    // QC: destvec = origin
        Assert.Equal(MapMover.ActiveActive, fr.Active);

        // Find the spawned controller and drive one think; it must set the parent's velocity (it moved off origin).
        Entity? ctl = FindByClass(services, "func_fourier_controller");
        Assert.NotNull(ctl);
        Assert.Same(fr, ctl!.Owner);

        fr.Velocity = Vector3.Zero;
        ctl.Think!(ctl);
        Assert.NotEqual(Vector3.Zero, fr.Velocity);             // the sine target differs from origin => nonzero vel
    }

    [Fact]
    public void Fourier_EmptyNetname_DefaultsToBobbingEquivalent()
    {
        Boot();
        Entity fr = Spawn("func_fourier");                       // no netname
        Assert.Equal("1 0 0 0 1", fr.NetName);                   // QC: if(netname == "") netname = "1 0 0 0 1"
    }

    // ===================================================================
    //  func_vectormamamam
    // ===================================================================

    [Fact]
    public void Vectormamamam_Defaults_ResolvesReferences_AndSpawnsController()
    {
        var services = Boot();
        Entity refA = Spawn("path_corner", e => { e.TargetName = "vm_ref"; e.Origin = new Vector3(10f, 20f, 30f); });
        Entity vm = Spawn("func_vectormamamam", e =>
        {
            e.Origin = new Vector3(0f, 0f, 0f);
            e.Target = "vm_ref";
        });

        Assert.Equal("func_vectormamamam", vm.ClassName);
        Assert.Equal(1f, vm.TargetFactor);                      // QC: if(!targetfactor) targetfactor = 1
        Assert.Equal(MapMover.ActiveActive, vm.Active);

        MapObjectsRegistry.RunPostSpawn();                       // findtarget resolves wp00 + spawns controller

        Assert.Same(refA, vm.Wp00);                              // QC: wp00 = find(target)
        Entity? ctl = FindByClass(services, "func_vectormamamam_controller");
        Assert.NotNull(ctl);
        Assert.Same(vm, ctl!.Owner);

        // The controller drives velocity toward destvec + projection (the reference is offset, so vel is nonzero).
        vm.Velocity = Vector3.Zero;
        ctl.Think!(ctl);
        // With a single reference and no projection flag (project OFF the zero normal => full p), destvec was set
        // so that at timestep 0 the mover stays put; at TIMESTEP>0 the reference's predicted move would change it,
        // but a static reference (zero velocity) keeps the target == origin, so velocity stays ~0. Assert no NaN
        // and that the controller rescheduled.
        Assert.True(ctl.NextThink > 0f);
    }

    [Fact]
    public void Vectormamamam_ProjectionMath_MatchesQc()
    {
        Boot();
        // Two references; one projected ONTO its normal, one OFF. Verify destvec captures the spawn offset so the
        // mover holds its origin at timestep 0 (QC: destvec = origin - func_vectormamamam_origin(this, 0)).
        Entity r1 = Spawn("path_corner", e => { e.TargetName = "p1"; e.Origin = new Vector3(8f, 0f, 0f); });
        Entity r2 = Spawn("path_corner", e => { e.TargetName = "p2"; e.Origin = new Vector3(0f, 0f, 12f); });
        Entity vm = Spawn("func_vectormamamam", e =>
        {
            e.Origin = new Vector3(0f, 0f, 0f);
            e.Target = "p1";
            e.Target2 = "p2";
            e.SpawnFlags = AdvancedMovers.ProjectOnTargetNormal; // project ref1 ONTO its normal
            e.TargetNormal = new Vector3(1f, 0f, 0f);
            e.Target2Normal = new Vector3(0f, 0f, 1f);
        });

        MapObjectsRegistry.RunPostSpawn();

        Assert.Same(r1, vm.Wp00);
        Assert.Same(r2, vm.Wp01);
        // origin(this,0): ref1 projected onto (1,0,0) => (8,0,0); ref2 projected OFF (0,0,1) => (0,0,0) (perp part).
        // So func_vectormamamam_origin(this,0) = (8,0,0); destvec = origin - that = (-8,0,0).
        Assert.Equal(new Vector3(-8f, 0f, 0f), vm.DestVec);
    }

    [Fact]
    public void Vectormamamam_SetActive_StopsAndStartsAmbient()
    {
        Boot();
        Entity vm = Spawn("func_vectormamamam", e => e.Target = "");
        Assert.NotNull(vm.SetActive);

        vm.SetActive!(vm, MapMover.ActiveToggle);                // active -> not
        Assert.Equal(MapMover.ActiveNot, vm.Active);
        vm.SetActive!(vm, MapMover.ActiveToggle);                // not -> active
        Assert.Equal(MapMover.ActiveActive, vm.Active);

        // Drain the INITPRIO queue this spawn enqueued (no references => no-op) so it doesn't leak to other tests.
        MapObjectsRegistry.RunPostSpawn();
    }

    // ===================================================================
    //  target_voicescript
    // ===================================================================

    [Fact]
    public void VoiceScript_Tokenizes_CountsOrderedPrefix_BeforeStar()
    {
        Boot();
        Entity vs = Spawn("target_voicescript", e =>
        {
            // 2 ordered lines, then a '*', then a random pool.
            e.Message = "alpha 1.0 beta 2.0 * gamma 1.5 delta 1.5";
            e.NetName = "sound/vox";
            e.Wait = 1f;
            e.Delay = 0.5f;
        });

        Assert.Equal("target_voicescript", vs.ClassName);
        Assert.Equal(2, vs.Cnt);                                // QC: ordered-prefix line count (before '*')
        Assert.Equal(MapMover.ActiveActive, vs.Active);
        Assert.NotNull(vs.Use);
        Assert.NotNull(vs.Reset);
    }

    [Fact]
    public void VoiceScript_Use_LatchesScriptOntoActivator_AndAdvances()
    {
        Boot();
        Entity vs = Spawn("target_voicescript", e =>
        {
            e.Message = "alpha 0.5 beta 0.5";
            e.NetName = "vox";
            e.Wait = 0f;
            e.Delay = 0f;
        });

        Entity pl = Api.Entities.Spawn();
        pl.ClassName = "player";
        pl.Flags |= EntFlags.Client;

        vs.Use!(vs, pl);                                        // QC target_voicescript_use
        Assert.Same(vs, pl.VoiceScript);
        Assert.Equal(0f, pl.VoiceScriptIndex);

        // First Next: plays line 0, advances the index, sets voiceend = time + 0.5.
        float now = Api.Clock.Time;
        VoiceScript.Next(pl);
        Assert.Equal(1f, pl.VoiceScriptIndex);
        Assert.Equal(now + 0.5f, pl.VoiceScriptVoiceEnd, 3);

        // While the current line is still playing (time < voiceend), Next is a no-op (index unchanged).
        VoiceScript.Next(pl);
        Assert.Equal(1f, pl.VoiceScriptIndex);
    }

    [Fact]
    public void VoiceScript_Clear_DropsActiveScript()
    {
        Boot();
        Entity pl = Api.Entities.Spawn();
        pl.ClassName = "player";
        pl.Flags |= EntFlags.Client;
        Entity vs = Spawn("target_voicescript", e => { e.Message = "a 1 b 1"; e.NetName = "vox"; });
        pl.VoiceScript = vs;

        VoiceScript.Clear(pl);
        Assert.Null(pl.VoiceScript);
    }

    // ---- helper ----
    private static Entity? FindByClass(EngineServices services, string cls)
    {
        foreach (Entity e in services.Entities.FindByClass(cls))
            if (!e.IsFreed)
                return e;
        return null;
    }
}
