using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Exercises the music system map objects (target_music / trigger_music spawnfuncs) and the
/// cdtrack worldspawn extraction in GameWorld.
/// </summary>
[Collection("GlobalState")]
public class MusicSystemTests
{
    /// <summary>target_music and trigger_music are registered in SpawnFuncs after RegisterAll.</summary>
    [Fact]
    public void MusicSpawnfuncs_Are_Registered()
    {
        // Boot a minimal world so RegisterAll runs.
        var world = new CollisionWorld();
        var gw = new GameWorld(world, null);
        gw.Boot("dm");

        // Verify both classnames are registered.
        var e1 = Api.Entities.Spawn();
        e1.ClassName = "target_music";
        Assert.True(SpawnFuncs.TrySpawn("target_music", e1));
        Assert.Equal("target_music", e1.ClassName);

        var e2 = Api.Entities.Spawn();
        e2.ClassName = "trigger_music";
        Assert.True(SpawnFuncs.TrySpawn("trigger_music", e2));
        Assert.Equal("trigger_music", e2.ClassName);
    }

    /// <summary>target_music without a targetname is the map default — always active.</summary>
    [Fact]
    public void TargetMusic_NoTargetname_IsDefault_AlwaysActive()
    {
        var world = new CollisionWorld();
        var gw = new GameWorld(world, null);
        gw.Boot("dm");

        Entity e = Api.Entities.Spawn();
        e.Noise = "cdtracks/neon";
        e.Volume = 0.8f;
        TargetMusic.TargetMusicSetup(e);

        Assert.Equal("target_music", e.ClassName);
        Assert.Equal(MapMover.ActiveActive, e.Active);
        // No .Use delegate for an untargetable default.
        Assert.Null(e.Use);
    }

    /// <summary>
    /// A targetname'd target_music starts inactive and ACTIVATES on Use. QC <c>target_music_use</c>
    /// (music.qc:59-72) RE-SENDS the override to the activator on every trigger and never toggles it off — the
    /// override then lives for its <c>lifetime</c> window (client-side TargetMusic_Advance). So a repeat Use
    /// keeps it active (re-sent), it does not flip back off.
    /// </summary>
    [Fact]
    public void TargetMusic_WithTargetname_ActivatesOnUse()
    {
        var world = new CollisionWorld();
        var gw = new GameWorld(world, null);
        gw.Boot("dm");

        Entity e = Api.Entities.Spawn();
        e.TargetName = "music_override";
        e.Noise = "cdtracks/heavymetal";
        TargetMusic.TargetMusicSetup(e);

        Assert.Equal(MapMover.ActiveNot, e.Active);
        Assert.NotNull(e.Use);

        // QC target_music_use (music.qc:59-72) gates on IS_REAL_CLIENT(actor): only a real client
        // activator sends/re-sends the override. A bare (non-client) trigger source is a no-op, so the
        // activator must carry EntFlags.Client to exercise the activation path.
        Entity activator = Api.Entities.Spawn();
        activator.Flags |= EntFlags.Client;
        e.Use!(e, activator);
        Assert.Equal(MapMover.ActiveActive, e.Active);

        // Trigger again: QC re-sends the override (no toggle-off) — stays active.
        e.Use!(e, activator);
        Assert.Equal(MapMover.ActiveActive, e.Active);
    }

    /// <summary>trigger_music sets up as a trigger volume with a Touch handler.</summary>
    [Fact]
    public void TriggerMusic_SetsUpAsTriggerVolume()
    {
        var world = new CollisionWorld();
        var gw = new GameWorld(world, null);
        gw.Boot("dm");

        Entity e = Api.Entities.Spawn();
        e.Noise = "cdtracks/sensation3";
        e.Volume = 0.9f;
        TargetMusic.TriggerMusicSetup(e);

        Assert.Equal("trigger_music", e.ClassName);
        Assert.Equal(MapMover.ActiveActive, e.Active);
        Assert.NotNull(e.Touch);
        Assert.Equal(Solid.Trigger, e.Solid);
    }

    /// <summary>trigger_music with START_DISABLED flag starts inactive.</summary>
    [Fact]
    public void TriggerMusic_StartDisabled_InactiveBySpaawnflag()
    {
        var world = new CollisionWorld();
        var gw = new GameWorld(world, null);
        gw.Boot("dm");

        Entity e = Api.Entities.Spawn();
        e.Noise = "cdtracks/neon";
        e.SpawnFlags = 1; // START_DISABLED
        TargetMusic.TriggerMusicSetup(e);

        Assert.Equal(MapMover.ActiveNot, e.Active);
    }

    /// <summary>GameWorld.CdTrack is populated from worldspawn "music" key.</summary>
    [Fact]
    public void GameWorld_CdTrack_FromWorldspawn_Music()
    {
        var entities = new List<EntityDict>
        {
            new("worldspawn") { Fields = { ["music"] = "cdtracks/heavymetal.ogg" } }
        };
        var world = new CollisionWorld();
        var gw = new GameWorld(world, entities);
        gw.Boot("dm");

        Assert.Equal("cdtracks/heavymetal.ogg", gw.CdTrack);
    }

    /// <summary>GameWorld.CdTrack falls back to worldspawn "noise" if "music" is absent.</summary>
    [Fact]
    public void GameWorld_CdTrack_FromWorldspawn_Noise()
    {
        var entities = new List<EntityDict>
        {
            new("worldspawn") { Fields = { ["noise"] = "cdtracks/quiet.ogg" } }
        };
        var world = new CollisionWorld();
        var gw = new GameWorld(world, entities);
        gw.Boot("dm");

        Assert.Equal("cdtracks/quiet.ogg", gw.CdTrack);
    }

    /// <summary>Pre-set CdTrack is NOT overridden by worldspawn (mapinfo takes priority).</summary>
    [Fact]
    public void GameWorld_CdTrack_PreSet_NotOverriddenByWorldspawn()
    {
        var entities = new List<EntityDict>
        {
            new("worldspawn") { Fields = { ["music"] = "cdtracks/heavymetal.ogg" } }
        };
        var world = new CollisionWorld();
        var gw = new GameWorld(world, entities) { CdTrack = "cdtracks/neon" };
        gw.Boot("dm");

        // The pre-set value (from mapinfo) should win.
        Assert.Equal("cdtracks/neon", gw.CdTrack);
    }

    /// <summary>target_music volume defaults to 1 when unset (0).</summary>
    [Fact]
    public void TargetMusic_Volume_DefaultsToOne()
    {
        var world = new CollisionWorld();
        var gw = new GameWorld(world, null);
        gw.Boot("dm");

        Entity e = Api.Entities.Spawn();
        e.Noise = "cdtracks/test";
        // Volume is 0 (unset by the map).
        TargetMusic.TargetMusicSetup(e);

        Assert.Equal(1f, e.Volume);
    }

    // ========================================================================
    //  T48 — the "content tail" extra keys must reach the entity on the LIVE
    //  --host spawn path (GameWorld.SpawnMapEntities → ApplyDictFields), not
    //  only on the offline GameDemo path. Regression for the major bug where
    //  ApplyDictFields never called MapObjectFieldsExtra.Apply, so on --host
    //  every mdl/effect, scale, fade, music-fade, weather-direction key was
    //  silently dropped.
    // ========================================================================

    private static EntityDict Dict(string cls, Vector3 origin = default, params (string k, string v)[] fields)
    {
        var d = new EntityDict { ClassName = cls, Origin = origin };
        foreach (var (k, v) in fields) d.Fields[k] = v;
        return d;
    }

    private static GameWorld BootMap(IEnumerable<EntityDict> ents)
    {
        var gw = new GameWorld(new CollisionWorld(), ents.ToList());
        gw.Boot("dm");
        return gw;
    }

    /// <summary>
    /// func_pointparticles on the live path must receive its effectinfo effect NAME (the <c>mdl</c> key →
    /// Entity.Mdl). Without it the emitter has nothing to spawn — the headline T48 bug. Also verifies the
    /// emission-density (<c>impulse</c>) and per-emission count (<c>count</c>) keys arrive.
    /// </summary>
    [Fact]
    public void LivePath_FuncPointparticles_GetsMdlAndDensity()
    {
        BootMap(new[]
        {
            Dict("func_pointparticles", new Vector3(64, 0, 0),
                ("mdl", "smoke_large"), ("impulse", "8"), ("count", "3")),
        });

        Entity e = Assert.Single(Api.Entities.FindByClass("func_pointparticles"));
        Assert.Equal("smoke_large", e.Mdl);          // the effect NAME flowed through (was dropped before)
        Assert.Equal(8f, e.Impulse);                 // density key
        Assert.Equal(3f, e.ParticleCount);           // per-emission count multiplier
    }

    /// <summary>
    /// target_music on the live path must carry its fade window (<c>lifetime</c>/<c>fade_time</c>/<c>fade_rate</c>
    /// → MusicLifetime/MusicFadeIn/MusicFadeOut). Without them the client TargetMusic_Advance can't ramp.
    /// </summary>
    [Fact]
    public void LivePath_TargetMusic_CarriesLifetimeAndFades()
    {
        BootMap(new[]
        {
            Dict("target_music", default,
                ("targetname", "m1"), ("noise", "cdtracks/neon"),
                ("lifetime", "30"), ("fade_time", "2.5"), ("fade_rate", "4")),
        });

        Entity e = Assert.Single(Api.Entities.FindByClass("target_music"));
        Assert.Equal(30f, e.MusicLifetime);
        Assert.Equal(2.5f, e.MusicFadeIn);
        Assert.Equal(4f, e.MusicFadeOut);
    }

    /// <summary>
    /// func_rain on the live path must carry the fall velocity (the <c>velocity</c> key → moved to Entity.Dest
    /// by the spawnfunc). Without it the spawnfunc only ever sees the default direction.
    /// </summary>
    [Fact]
    public void LivePath_FuncRain_CarriesFallVelocityIntoDest()
    {
        BootMap(new[]
        {
            // wind: a non-default slanted fall direction the mapper set via the velocity key.
            Dict("func_rain", default, ("velocity", "40 0 -650"), ("count", "1500"), ("cnt", "14")),
        });

        Entity e = Assert.Single(Api.Entities.FindByClass("func_rain"));
        // rainsnow spawnfunc: dest = velocity (the wind/fall vector), then velocity zeroed.
        Assert.Equal(new Vector3(40, 0, -650), e.Dest);
        Assert.Equal(Vector3.Zero, e.Velocity);
        Assert.Equal(1500f, e.ParticleCount);  // density key (kept, > 1, < 65535)
        Assert.Equal(14, e.Cnt);               // palette colorbase override (default would be 12)
    }

    /// <summary>
    /// func_wall on the live path must carry the solid override + distance-fade keys (models.qc): a mapper
    /// <c>solid -1</c> forces non-solid, and fade_start/fade_end ride the entity for the client wall predraw.
    /// </summary>
    [Fact]
    public void LivePath_FuncWall_CarriesSolidOverrideAndFades()
    {
        BootMap(new[]
        {
            Dict("func_wall", default,
                ("solid", "-1"), ("fade_start", "512"), ("fade_end", "1024"), ("scale", "2")),
        });

        Entity e = Assert.Single(Api.Entities.FindByClass("func_wall"));
        // solid -1 -> SOLID_NOT (models.qc:178-179, applied by GModelInit from SolidOverride).
        Assert.Equal(Solid.Not, e.Solid);
        Assert.Equal(512f, e.FadeStartDist);
        Assert.Equal(1024f, e.FadeEndDist);
        Assert.Equal(2f, e.ScaleFactor);
    }
}
