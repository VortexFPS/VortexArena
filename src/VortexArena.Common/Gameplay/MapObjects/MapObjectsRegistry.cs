// Registration of every map-object spawnfunc this module implements.
//
// The C# successor to QuakeC's spawnfunc_CLASSNAME registration: the BSP entity lump reads a "classname"
// key and looks it up in SpawnFuncs (EntityClasses.cs). The lead calls RegisterAll() once from GameInit so
// the lump can spawn doors / triggers / plats / jumppads / etc. Each registered delegate is the family's
// static setup method (Action<Entity>), matching one QC spawnfunc.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Installs the map-object spawnfuncs into <see cref="SpawnFuncs"/>. Call <see cref="RegisterAll"/> once at
/// boot (the lead wires it into GameInit). Idempotent: re-registering a classname just overwrites it.
/// </summary>
public static class MapObjectsRegistry
{
    /// <summary>Register every implemented map-object classname -> its setup spawnfunc.</summary>
    public static void RegisterAll()
    {
        // ---- world-item pickups (T35: common/items/item/*.qh + powerup .qh + server/items/spawning.qc) ----
        // One block funnels EVERY item_*/weapon_* classname into SpawnFuncs (the SPAWNFUNC_ITEM table + the
        // compat aliases + weapon_defaultspawnfunc). Must run AFTER GameRegistries.Bootstrap() (it enumerates the
        // [Item] pickups + the Weapon registry); GameInit calls RegisterAll after Bootstrap, so this is safe here.
        ItemSpawnFuncs.Register();

        // ---- Q3DF target_* compat entities (T52: server/compat/quake3.qc:119-295) ----
        // The Q3/QL/CPMA/Q1/Q2/WoP weapon/item/ammo classname remaps live in ItemSpawnFuncs.Register() above
        // (they share the item/weapon spawn paths); this installs the four DeFRaG target_* entities
        // (target_init / target_score / target_fragsFilter / target_print / target_smallprint). Must run after
        // ItemSpawnFuncs.Register so a map's target_* doesn't clobber a real item classname (it can't — disjoint
        // names — but the ordering matches the recon seam).
        CompatRemaps.Register();

        // ---- doors (func/door.qc, door_rotating.qc) ----
        SpawnFuncs.Register("func_door", Doors.DoorSetup);
        SpawnFuncs.Register("func_door_rotating", Doors.DoorRotatingSetup);

        // ---- platforms (func/plat.qc) ----
        SpawnFuncs.Register("func_plat", Platforms.PlatSetup);

        // ---- buttons (func/button.qc) ----
        SpawnFuncs.Register("func_button", Buttons.ButtonSetup);

        // ---- touch / relay triggers (trigger/*.qc) ----
        SpawnFuncs.Register("trigger_multiple", Triggers.MultipleSetup);
        SpawnFuncs.Register("trigger_once", Triggers.OnceSetup);
        SpawnFuncs.Register("trigger_hurt", Triggers.HurtSetup);
        SpawnFuncs.Register("trigger_heal", Triggers.HealSetup);
        SpawnFuncs.Register("target_heal", Triggers.TargetHealSetup);
        SpawnFuncs.Register("trigger_gravity", Triggers.GravitySetup);
        SpawnFuncs.Register("trigger_counter", Triggers.CounterSetup);
        SpawnFuncs.Register("trigger_relay", Triggers.RelaySetup);
        SpawnFuncs.Register("target_relay", Triggers.RelaySetup);
        SpawnFuncs.Register("target_delay", Triggers.TargetDelaySetup);
        SpawnFuncs.Register("trigger_delay", Triggers.DelaySetup);
        SpawnFuncs.Register("trigger_secret", Triggers.SecretSetup);
        SpawnFuncs.Register("trigger_swamp", Triggers.SwampSetup);
        SpawnFuncs.Register("trigger_impulse", Triggers.ImpulseSetup);
        SpawnFuncs.Register("trigger_keylock", Triggers.KeylockSetup);

        // ---- logic gates / activator relays (T22: trigger/{flipflop,monoflop,multivibrator,disablerelay,
        //      relay_if,relay_teamcheck,relay_activators,gamestart,magicear}.qc). trigger_relay/target_relay/
        //      target_delay/trigger_delay are already registered above (Triggers.cs) — do NOT re-register. ----
        SpawnFuncs.Register("trigger_flipflop", LogicGates.FlipflopSetup);
        SpawnFuncs.Register("trigger_monoflop", LogicGates.MonoflopSetup);
        SpawnFuncs.Register("trigger_multivibrator", LogicGates.MultivibratorSetup);
        SpawnFuncs.Register("trigger_disablerelay", LogicGates.DisableRelaySetup);
        SpawnFuncs.Register("trigger_relay_if", LogicGates.RelayIfSetup);
        SpawnFuncs.Register("trigger_relay_teamcheck", LogicGates.RelayTeamCheckSetup);
        SpawnFuncs.Register("relay_activate", LogicGates.RelayActivateSetup);
        SpawnFuncs.Register("relay_deactivate", LogicGates.RelayDeactivateSetup);
        SpawnFuncs.Register("relay_activatetoggle", LogicGates.RelayActivateToggleSetup);
        SpawnFuncs.Register("trigger_gamestart", LogicGates.GamestartSetup);
        SpawnFuncs.Register("trigger_magicear", LogicGates.MagicEarSetup);

        // ---- target_* utilities (T22: target/{kill,speed,spawnpoint,location,changelevel,levelwarp,give,
        //      items,spawn}.qc). target_heal/target_relay/target_delay/target_push are registered elsewhere. ----
        SpawnFuncs.Register("target_kill", TargetUtilities.KillSetup);
        SpawnFuncs.Register("target_speed", TargetUtilities.SpeedSetup);
        SpawnFuncs.Register("target_spawnpoint", TargetUtilities.SpawnPointSetup);
        SpawnFuncs.Register("target_location", TargetUtilities.LocationSetup);
        SpawnFuncs.Register("info_location", TargetUtilities.InfoLocationSetup);
        SpawnFuncs.Register("target_changelevel", TargetUtilities.ChangeLevelSetup);
        SpawnFuncs.Register("target_levelwarp", TargetUtilities.LevelWarpSetup);
        SpawnFuncs.Register("target_give", TargetUtilities.GiveSetup);
        SpawnFuncs.Register("target_items", TargetUtilities.ItemsSetup);
        SpawnFuncs.Register("target_spawn", TargetUtilities.SpawnSetup);

        // ---- info_autoscreenshot (server/cheats.qc) — observe/screenshot point; the TELEPORT cheat's preferred
        //      emergency destination. The findtarget aim is deferred to RunPostSpawn (INITPRIO_FINDTARGET). ----
        SpawnFuncs.Register("info_autoscreenshot", InfoAutoScreenshot.Setup);

        // ---- func_door_secret (T22: func/door_secret.qc) — slide-back-then-side secret door. (Uses its own
        //      "door_secret" classname, NOT "door", to stay out of the regular-door shared death/link dispatch
        //      the port introduced; see TargetUtilities.DoorSecretSetup.) ----
        SpawnFuncs.Register("func_door_secret", TargetUtilities.DoorSecretSetup);

        // ---- conveyor / ladder / water volumes (T22: func/{conveyor,ladder}.qc). The PlayerPhysics CONSUMER
        //      side (Entity.ConveyorEntity/ConveyorMoveDir, Entity.LadderEntity, func_water waterlevel) is
        //      already ported; these are the per-frame PRODUCER thinks + spawnfuncs. ----
        SpawnFuncs.Register("trigger_conveyor", MapVolumes.TriggerConveyorSetup);
        SpawnFuncs.Register("func_conveyor", MapVolumes.FuncConveyorSetup);
        SpawnFuncs.Register("func_ladder", MapVolumes.FuncLadderSetup);
        SpawnFuncs.Register("func_water", MapVolumes.FuncWaterSetup);

        // ---- warpzones (lib/warpzone/server.qc) — the brush auto-orients from its geometry (getsurface*); an
        //      optional trigger_warpzone_position gives an explicit orientation. Registered here, but the plane
        //      derivation + pair linking are deferred to GameWorld.Warpzones.InitMapZones() after all entities
        //      spawn (QC WarpZone_StartFrame). The stateless spawnfunc reaches the instance manager via the
        //      WarpzoneSpawns.Sink bridge the host wires in Boot (mirrors Porto.PortalSpawner). ----
        SpawnFuncs.Register("trigger_warpzone", WarpzoneSpawns.TriggerWarpzoneSetup);
        SpawnFuncs.Register("trigger_warpzone_position", WarpzoneSpawns.TriggerWarpzonePositionSetup);
        // QC's CANONICAL position spawnfunc (server.qc:642); trigger_warpzone_position just delegates to it. A map
        // authored with the bare misc_warpzone_position name must orient its zone identically.
        SpawnFuncs.Register("misc_warpzone_position", WarpzoneSpawns.MiscWarpzonePositionSetup);
        // runtime / moving-warpzone re-link (server.qc:807-815). Both names spawn the same triggerable use-handler.
        SpawnFuncs.Register("trigger_warpzone_reconnect", WarpzoneSpawns.TriggerWarpzoneReconnectSetup);
        SpawnFuncs.Register("target_warpzone_reconnect", WarpzoneSpawns.TriggerWarpzoneReconnectSetup);
        // map-placed security cameras (lib/warpzone/server.qc spawnfunc func_camera / func_warpzone_camera): the
        // dpcamera/func_camera entity resolves its target, stores the camera pose, and on touch/view stamps the
        // viewer's authoritative FixAngle/FixAngleAngles — the same view-orient channel the warpzone owner block
        // uses (no new wire fields). Routed through the same WarpzoneSpawns.Sink the other warpzone spawnfuncs use
        // (bridged in GameWorld.cs:453) so it realises on the normal match path. Client view-side presentation
        // (FixView / portal render) is the listen-host/demo ambient seam, same as LaserRenderer / PortalRenderer.
        SpawnFuncs.Register("func_camera", WarpzoneSpawns.FuncCameraSetup);
        SpawnFuncs.Register("func_warpzone_camera", WarpzoneSpawns.FuncCameraSetup);

        // ---- teleporters (trigger/teleport.qc, misc/teleport_dest.qc) ----
        SpawnFuncs.Register("trigger_teleport", Teleporters.TeleportSetup);
        SpawnFuncs.Register("target_teleporter", Teleporters.TargetTeleporterSetup);
        SpawnFuncs.Register("info_teleport_destination", Teleporters.TeleportDestSetup);
        SpawnFuncs.Register("misc_teleporter_dest", Teleporters.TeleportDestSetup);

        // ---- jumppads (trigger/jumppads.qc) ----
        SpawnFuncs.Register("trigger_push", Jumppads.PushSetup);
        SpawnFuncs.Register("trigger_push_velocity", Jumppads.PushVelocitySetup);
        SpawnFuncs.Register("target_push", Jumppads.TargetPushSetup);
        SpawnFuncs.Register("target_position", Jumppads.TargetPushSetup);
        SpawnFuncs.Register("info_notnull", Jumppads.TargetPushSetup);

        // ---- ambient/triggered speakers (target/speaker.qc) ----
        SpawnFuncs.Register("target_speaker", TargetSpeaker.SpeakerSetup);

        // ---- map music (target/music.qc) ----
        SpawnFuncs.Register("target_music", TargetMusic.TargetMusicSetup);
        SpawnFuncs.Register("trigger_music", TargetMusic.TriggerMusicSetup);

        // ---- hazard laser (T48: misc/laser.qc) — damage/detector think server-side; the beam renders
        //      client-side via game/client/LaserRenderer.cs (facade scan, listen-server/demo seam). ----
        SpawnFuncs.Register("misc_laser", Laser.LaserSetup);

        // ---- decoration props + static walls (T48: models.qc) — the misc_* model entities flip pitch and
        //      stay non-solid; func_wall/func_clientwall/func_static get Solid.Bsp + their inline brush via
        //      SetModel (real collision through ClipToEntities — previously walk-through bare edicts). ----
        SpawnFuncs.Register("misc_gamemodel", MapModels.GameModelSetup);
        SpawnFuncs.Register("misc_clientmodel", MapModels.ClientModelSetup);
        SpawnFuncs.Register("misc_models", MapModels.ModelsSetup);
        SpawnFuncs.Register("func_illusionary", MapModels.IllusionarySetup);
        SpawnFuncs.Register("func_clientillusionary", MapModels.ClientIllusionarySetup);
        SpawnFuncs.Register("func_wall", MapModels.WallSetup);
        SpawnFuncs.Register("func_clientwall", MapModels.ClientWallSetup);
        SpawnFuncs.Register("func_static", MapModels.StaticSetup);

        // ---- pickup keys (misc/keys.qc) — the key SOURCE for trigger_keylock / key-gated func_door. The
        //      keylock CONSUMER (item_keys_usekey) already lives in Triggers.cs/Doors.cs reading Entity.ItemKeys,
        //      but with no key spawnfunc a map's key entity spawned NOTHING — so a key-gated area was permanently
        //      locked. spawn_item_key + item_key_touch are ported inline below (KeySetup/KeyTouch); item_key1/2
        //      and the QL item_key_gold/silver/master aliases pre-stamp .itemkeys then chain to item_key. ----
        SpawnFuncs.Register("item_key", KeySetup);
        SpawnFuncs.Register("item_key1", e => { e.ItemKeys = 1 << 1; KeySetup(e); }); // BIT(1) SILVER (legacy swap)
        SpawnFuncs.Register("item_key2", e => { e.ItemKeys = 1 << 0; KeySetup(e); }); // BIT(0) GOLD (legacy swap)
        SpawnFuncs.Register("item_key_gold", e => { e.ItemKeys = 1 << 0; KeySetup(e); });   // QL gold
        SpawnFuncs.Register("item_key_silver", e => { e.ItemKeys = 1 << 1; KeySetup(e); }); // QL silver
        SpawnFuncs.Register("item_key_master", e => { e.ItemKeys = 0xffffff; KeySetup(e); }); // QL master (all 24 bits)

        // ---- continuous map particle emitters (T48: func/pointparticles.qc) — server state/toggling;
        //      emission is client-side (game/client/MapParticleEmitters.cs). ----
        SpawnFuncs.Register("func_pointparticles", PointParticles.PointParticlesSetup);
        SpawnFuncs.Register("func_sparks", PointParticles.SparksSetup);

        // ---- weather volumes (T48: func/rainsnow.qc) — drawn by game/client/WeatherSystem.cs. ----
        SpawnFuncs.Register("func_rain", RainSnow.RainSetup);
        SpawnFuncs.Register("func_snow", RainSnow.SnowSetup);

        // ---- continuously-moving brushes (func/rotating.qc, bobbing.qc, pendulum.qc, train.qc) ----
        SpawnFuncs.Register("func_rotating", MovingBrushes.RotatingSetup);
        SpawnFuncs.Register("func_bobbing", MovingBrushes.BobbingSetup);
        SpawnFuncs.Register("func_pendulum", MovingBrushes.PendulumSetup);
        SpawnFuncs.Register("func_train", MovingBrushes.TrainSetup);
        SpawnFuncs.Register("path_corner", MovingBrushes.PathCornerSetup); // func_train waypoints (misc/corner.qc)

        // ---- map-object long tail (T59): seven rare server-side entities ----
        //   common/mapobjects/func/stardust.qc        — func_stardust (EF_STARDUST sparkle prop, 0.25s heartbeat)
        //   common/mapobjects/misc/dynlight.qc        — dynlight (static / path / follow / tag-attach + toggle)
        //   common/mapobjects/trigger/viewloc.qc      — trigger_viewlocation + target_viewlocation_start/_end
        //   common/mapobjects/misc/follow.qc          — misc_follow (attach/follow at INITPRIO_FINDTARGET)
        //   common/mapobjects/func/fourier.qc         — func_fourier (sum-of-sines mover)
        //   common/mapobjects/func/vectormamamam.qc   — func_vectormamamam (4-reference projected mover)
        //   common/mapobjects/target/voicescript.qc   — target_voicescript (scripted voice-line sequence)
        // The follow/tag/path/reference lookups run at INITPRIO_FINDTARGET — drained in RunPostSpawn (below).
        SpawnFuncs.Register("func_stardust", Stardust.StardustSetup);
        SpawnFuncs.Register("dynlight", DynamicLight.DynlightSetup);
        SpawnFuncs.Register("trigger_viewlocation", ViewLocation.TriggerViewLocationSetup);
        SpawnFuncs.Register("target_viewlocation_start", ViewLocation.StartSetup);
        SpawnFuncs.Register("target_viewlocation_end", ViewLocation.EndSetup);
        SpawnFuncs.Register("target_viewlocation", ViewLocation.CompatSetup); // compat alias → _start
        SpawnFuncs.Register("misc_follow", Follow.FollowSetup);
        SpawnFuncs.Register("func_fourier", AdvancedMovers.FourierSetup);
        SpawnFuncs.Register("func_vectormamamam", AdvancedMovers.VectormamamamSetup);
        SpawnFuncs.Register("target_voicescript", VoiceScript.VoiceScriptSetup);

        // ---- breakables (func/breakable.qc) ----
        // Keep the map's classname so the Combat.Death break hook matches either spelling.
        SpawnFuncs.Register("func_breakable", e => { e.ClassName = "func_breakable"; Breakable.BreakableSetup(e); });
        SpawnFuncs.Register("misc_breakablemodel", e => { e.ClassName = "misc_breakablemodel"; Breakable.BreakableSetup(e); });

        // ---- NPCs: hand-placed monsters / turrets / vehicles (T14) ----
        // Each is the thin spawnfunc the per-type .qc defines, funnelling through the shared spawn driver so the
        // map-entity loader instantiates them exactly like any other map object. See MonsterSpawnFuncs /
        // TurretSpawnFuncs / VehicleSpawnFuncs for the QC source map (lib/spawnfunc.qh SPAWNFUNC + the per-NPC
        // spawnfuncs in common/monsters|turrets|vehicles).

        // monsters (common/monsters/monster/*.qc: spawnfunc(monster_X){ Monster_Spawn(this, true, MON_X); }).
        SpawnFuncs.Register("monster_zombie", MonsterSpawnFuncs.Zombie);
        SpawnFuncs.Register("monster_golem", MonsterSpawnFuncs.Golem);
        SpawnFuncs.Register("monster_shambler", MonsterSpawnFuncs.Shambler); // golem.qc compatibility alias
        SpawnFuncs.Register("monster_mage", MonsterSpawnFuncs.Mage);
        SpawnFuncs.Register("monster_spider", MonsterSpawnFuncs.Spider);
        SpawnFuncs.Register("monster_wyvern", MonsterSpawnFuncs.Wyvern);
        // monster_spawner (common/monsters/sv_spawner.qc): a triggered emitter of .spawnmob up to .count.
        SpawnFuncs.Register("monster_spawner", MonsterSpawnFuncs.MonsterSpawner);

        // turrets (common/turrets/turret/*.qc: spawnfunc(turret_X){ if(!turret_initialize(this,TUR_X)) delete; }).
        SpawnFuncs.Register("turret_machinegun", TurretSpawnFuncs.Machinegun);
        SpawnFuncs.Register("turret_plasma", TurretSpawnFuncs.Plasma);
        SpawnFuncs.Register("turret_plasma_dual", TurretSpawnFuncs.PlasmaDual);
        SpawnFuncs.Register("turret_mlrs", TurretSpawnFuncs.Mlrs);
        SpawnFuncs.Register("turret_flac", TurretSpawnFuncs.Flac);
        SpawnFuncs.Register("turret_hellion", TurretSpawnFuncs.Hellion);
        SpawnFuncs.Register("turret_hk", TurretSpawnFuncs.Hk);
        SpawnFuncs.Register("turret_phaser", TurretSpawnFuncs.Phaser);
        SpawnFuncs.Register("turret_tesla", TurretSpawnFuncs.Tesla);
        SpawnFuncs.Register("turret_walker", TurretSpawnFuncs.Walker);
        SpawnFuncs.Register("turret_ewheel", TurretSpawnFuncs.EWheel);
        SpawnFuncs.Register("turret_fusionreactor", TurretSpawnFuncs.FusionReactor);
        // turret_checkpoint (common/turrets/checkpoint.qc): the waypoint nodes ewheel/walker roam between; the
        // walker_checkpoint classname is a QC compat alias for the same spawnfunc.
        SpawnFuncs.Register("turret_checkpoint", TurretSpawnFuncs.Checkpoint);
        SpawnFuncs.Register("walker_checkpoint", TurretSpawnFuncs.Checkpoint);
        // turret_targettrigger (common/turrets/targettrigger.qc): a touch volume that designates the toucher as a
        // target to RECIEVETARGETS turrets (the HK) whose targetname matches the trigger's target (0.5s debounce).
        SpawnFuncs.Register("turret_targettrigger", TurretSpawnFuncs.TargetTrigger);

        // vehicles (common/vehicles/vehicle/*.qc: spawnfunc(vehicle_X){ ... vehicle_initialize(this,VEH_X,false); }).
        SpawnFuncs.Register("vehicle_racer", VehicleSpawnFuncs.Racer);
        SpawnFuncs.Register("vehicle_raptor", VehicleSpawnFuncs.Raptor);
        SpawnFuncs.Register("vehicle_spiderbot", VehicleSpawnFuncs.Spiderbot);
        SpawnFuncs.Register("vehicle_bumblebee", VehicleSpawnFuncs.Bumblebee);

        // ---- gametype objective entities (T18): the flag/control-point/checkpoint/generator/link/penalty
        //      spawnfuncs the BSP lump places for the objective modes. Each is a thin (stateless) spawnfunc that
        //      tags the classname and funnels the edict to GametypeObjectiveSpawns.Sink, which the host wires to
        //      the ACTIVE gametype (mirrors WarpzoneSpawns.Sink / Porto.PortalSpawner). With no sink wired (a
        //      bare unit test, or a non-objective gametype) the edict is simply ignored — the gametype's own
        //      SpawnFlag/SpawnControlPoint/SpawnCheckpoint API is the direct path the deterministic tests use. ----
        // CTF flags (common/gametypes/gametype/ctf/sv_ctf.qc spawnfunc item_flag_team*/item_flag_neutral).
        SpawnFuncs.Register("item_flag_team1", GametypeObjectiveSpawns.FlagTeam1);
        SpawnFuncs.Register("item_flag_team2", GametypeObjectiveSpawns.FlagTeam2);
        SpawnFuncs.Register("item_flag_team3", GametypeObjectiveSpawns.FlagTeam3);
        SpawnFuncs.Register("item_flag_team4", GametypeObjectiveSpawns.FlagTeam4);
        SpawnFuncs.Register("item_flag_neutral", GametypeObjectiveSpawns.FlagNeutral);
        // Domination control points (common/gametypes/gametype/domination/sv_domination.qc dom_controlpoint).
        SpawnFuncs.Register("dom_controlpoint", GametypeObjectiveSpawns.DomControlPoint);
        SpawnFuncs.Register("team_dom_point", GametypeObjectiveSpawns.DomControlPoint); // legacy classname alias
        // dom_team: team-definition data holder (carries a per-team control-point model). Route it through the sink
        // so it's RETIRED (never spawns/renders) — without this its map `model` key made it a stray dom_*.md3 model
        // wrongly visible on the map in the wrong gametype (playtest #20: Stormkeep DM showed a dom_* cluster).
        SpawnFuncs.Register("dom_team", GametypeObjectiveSpawns.DomTeam);
        // Race checkpoints + penalty zones (common/gametypes/gametype/race/sv_race.qc + server/race.qc).
        SpawnFuncs.Register("trigger_race_checkpoint", GametypeObjectiveSpawns.RaceCheckpoint);
        SpawnFuncs.Register("trigger_race_penalty", GametypeObjectiveSpawns.RacePenalty);
        // Onslaught generators / control points / links (sv_onslaught.qc spawnfunc onslaught_*).
        SpawnFuncs.Register("onslaught_generator", GametypeObjectiveSpawns.OnslaughtGenerator);
        SpawnFuncs.Register("onslaught_controlpoint", GametypeObjectiveSpawns.OnslaughtControlPoint);
        SpawnFuncs.Register("onslaught_link", GametypeObjectiveSpawns.OnslaughtLink);

        // ---- Assault objectives (T36: common/gametypes/gametype/assault/sv_assault.qc) ----
        // The objective-chain entities route through the Assault sink; the GameWorld.WireObjectiveSpawns Assault
        // arm reads each by classname and calls Assault.AddObjective/StageDecreaser/StageDestructible/AddRoundEnd.
        SpawnFuncs.Register("target_objective", GametypeObjectiveSpawns.TargetObjective);
        SpawnFuncs.Register("target_objective_decrease", GametypeObjectiveSpawns.TargetObjectiveDecrease);
        SpawnFuncs.Register("func_assault_destructible", GametypeObjectiveSpawns.FuncAssaultDestructible);
        SpawnFuncs.Register("func_assault_wall", GametypeObjectiveSpawns.FuncAssaultWall);
        SpawnFuncs.Register("target_assault_roundend", GametypeObjectiveSpawns.TargetAssaultRoundend);
        SpawnFuncs.Register("target_assault_roundstart", GametypeObjectiveSpawns.TargetAssaultRoundstart);
        // info_player_attacker/defender (sv_assault.qc:287/295) are SPAWN POINTS: QC sets this.team = NUM_TEAM_1/2
        // then chains to spawnfunc_info_player_deathmatch. The port keeps spawnpoints as passive findable edicts
        // (no spawnfunc) and SpawnSystem.SelectSpawnPoint finds them by classname — so we retag the edict to
        // info_player_deathmatch and stamp the team, exactly the QC chain's net effect (DetectTeamSpawns then
        // honors the explicit team). NOT routed through the objective sink (these aren't objectives).
        SpawnFuncs.Register("info_player_attacker", e => { e.Team = Teams.Red;  e.ClassName = "info_player_deathmatch"; });
        SpawnFuncs.Register("info_player_defender", e => { e.Team = Teams.Blue; e.ClassName = "info_player_deathmatch"; });

        // ---- Nexball goals + balls (T36: common/gametypes/gametype/nexball/sv_nexball.qc) ----
        SpawnFuncs.Register("nexball_redgoal", GametypeObjectiveSpawns.NexballRedGoal);
        SpawnFuncs.Register("nexball_bluegoal", GametypeObjectiveSpawns.NexballBlueGoal);
        SpawnFuncs.Register("nexball_yellowgoal", GametypeObjectiveSpawns.NexballYellowGoal);
        SpawnFuncs.Register("nexball_pinkgoal", GametypeObjectiveSpawns.NexballPinkGoal);
        SpawnFuncs.Register("nexball_fault", GametypeObjectiveSpawns.NexballFault);
        SpawnFuncs.Register("nexball_out", GametypeObjectiveSpawns.NexballOut);
        SpawnFuncs.Register("nexball_basketball", GametypeObjectiveSpawns.NexballBasketball);
        SpawnFuncs.Register("nexball_football", GametypeObjectiveSpawns.NexballFootball);
        // Compat aliases (sv_nexball.qc:684-712) — ball_redgoal/ball_bluegoal are INTENTIONALLY swapped.
        SpawnFuncs.Register("ball", GametypeObjectiveSpawns.BallFootball);
        SpawnFuncs.Register("ball_football", GametypeObjectiveSpawns.BallFootball);
        SpawnFuncs.Register("ball_basketball", GametypeObjectiveSpawns.BallBasketball);
        SpawnFuncs.Register("ball_redgoal", GametypeObjectiveSpawns.BallRedGoal);
        SpawnFuncs.Register("ball_bluegoal", GametypeObjectiveSpawns.BallBlueGoal);
        SpawnFuncs.Register("ball_fault", GametypeObjectiveSpawns.BallFault);
        SpawnFuncs.Register("ball_bound", GametypeObjectiveSpawns.BallBound);

        // ---- Invasion spawnpoints / waves / round-end (T36: common/gametypes/gametype/invasion/sv_invasion.qc) ----
        SpawnFuncs.Register("invasion_spawnpoint", GametypeObjectiveSpawns.InvasionSpawnpoint);
        SpawnFuncs.Register("invasion_wave", GametypeObjectiveSpawns.InvasionWave);
        SpawnFuncs.Register("target_invasion_roundend", GametypeObjectiveSpawns.TargetInvasionRoundend);

        // ---- CTS start/stop/intermediate timers (T36: server/race.qc target_checkpoint_setup) ----
        // NOTE: KeyHunt needs NO item_kh_key spawnfunc — keys are spawned at round start by KeyHunt.StartRound
        // (sv_keyhunt.qc kh_Key_Spawn), so registering one here would DOUBLE-spawn. (Intentionally absent.)
        SpawnFuncs.Register("target_startTimer", GametypeObjectiveSpawns.TargetStartTimer);
        SpawnFuncs.Register("target_stopTimer", GametypeObjectiveSpawns.TargetStopTimer);
        SpawnFuncs.Register("target_checkpoint", GametypeObjectiveSpawns.TargetCheckpoint);
    }

    /// <summary>
    /// Post-spawn pass the lead runs once after the whole BSP entity lump has been spawned (the headless
    /// analogue of QC's INITPRIO_LINKDOORS). It links double/quad doors into their owner/enemy groups now
    /// that every door's size is known. Safe to call more than once.
    /// </summary>
    public static void RunPostSpawn()
    {
        Doors.RunDeferredLinks();

        // T59 INITPRIO_FINDTARGET pass — run AFTER the door-link pass (mirrors QC's priority ordering): resolve
        // the follow/tag/path/reference lookups of the long-tail entities now that the whole BSP lump has
        // spawned (and door targetnames are settled). Each family drains its own pending-init queue.
        DynamicLight.RunDeferredInit();
        ViewLocation.RunDeferredInit();
        Follow.RunDeferredInit();
        AdvancedMovers.RunDeferredInit();
        Teleporters.RunDeferredInit(); // target_teleporter_checktarget disambiguation (dest/self-target/teleporter)
        InfoAutoScreenshot.RunDeferredInit(); // aim each info_autoscreenshot at its .target (QC findtarget pass)
    }

    // ====================================================================
    //  Pickup keys (common/mapobjects/misc/keys.qc) — ported inline so the
    //  registry stays the single owner of this family (no separate Keys.cs).
    // ====================================================================
    //
    //  spawnfunc(item_key)        -> KeySetup  (default netname/colormod/model per .itemkeys, then spawn_item_key)
    //  item_key_touch             -> KeyTouch  (grant itemkeys bits, pickup sound+centerprint, fire .target once)
    //
    //  Faithful to keys.qc:77-264. Divergences (mirrors the rest of this module's contracts):
    //   * EF_LOWPRECISION is a networking hint the port's Entity carries no field for; dropped (harmless).
    //   * MF_ROTATE is the DP model-spin flag (keys-only in all of Base). The port has no networked modelflags
    //     channel, so it rides Entity.ModelSpinRotate on the shared edict and EntityNode applies DP's
    //     '0 100 0'*fmod(time,3.6) yaw spin (csqcmodel_hooks.qc:617) on the listen-server/demo path.
    //   * QC's objerror on a multi-bit / nameless / modelless key is a headless no-op here: the edict is left
    //     inert (removed) rather than crashing the map, matching how the port treats other objerror sites.
    //   * play2(toucher, noise) (toucher-only 2D cue) -> MapMover.Play2 (CH_INFO / VOL_BASE / ATTEN_NONE).
    //   * The Q3COMPAT_COMMON QL +8z origin nudge is omitted (no Q3 compat global in the port; keys.qc:125-127).

    private const string KeyDefaultModel = "models/keys/key.md3";
    private const string KeyDefaultSound = "ITEMPICKUP"; // QC SND(ITEMPICKUP)

    /// <summary>QC <c>item_keys_names[ITEM_KEY_MAX]</c> (keys.qh:9): display name per key bit-index, recorded when an
    /// item_key spawns (keys.qc:240). Read by <see cref="ItemKeysKeylist"/> so door/keylock "you need …" centerprints
    /// can name the missing keys. Seeded with the stock bit names so a door requiring a key still names it even when
    /// the matching item_key pickup happens to be spawned later than the door's own setup.</summary>
    private static readonly System.Collections.Generic.Dictionary<int, string> ItemKeyNames = new()
    {
        [0] = "GOLD key",      // BIT(0)
        [1] = "SILVER key",    // BIT(1)
        [2] = "BRONZE key",    // BIT(2)
        [3] = "RED keycard",   // BIT(3)
        [4] = "BLUE keycard",  // BIT(4)
        [5] = "GREEN keycard", // BIT(5)
    };

    /// <summary>QC <c>lowestbit(v)</c> (lib/math.qc): bit-index of the least-significant set bit (0-based); -1 if 0.</summary>
    private static int LowestBit(int v)
    {
        if (v == 0)
            return -1;
        int b = 0;
        while ((v & 1) == 0) { v >>= 1; b++; }
        return b;
    }

    /// <summary>Port of <c>item_keys_keylist(float keylist)</c> (keys.qc:40-66): build the human-readable list of the
    /// keys named by the <paramref name="keylist"/> bitfield, e.g. "the SILVER key" or "the GOLD key, the SILVER key".
    /// Empty when no bits are set. Used by the func_door / trigger_keylock "You need %s" center notifications.</summary>
    public static string ItemKeysKeylist(int keylist)
    {
        // no keys
        if (keylist == 0)
            return "";

        // one key
        if ((keylist & (keylist - 1)) == 0)
            return "the " + KeyName(LowestBit(keylist));

        string n = "";
        int baseBit = 0;
        while (keylist != 0)
        {
            int l = LowestBit(keylist);
            n = string.IsNullOrEmpty(n)
                ? "the " + KeyName(baseBit + l)
                : n + ", the " + KeyName(baseBit + l);

            // QC bitshift(keylist, -(l+1)) is an unsigned right shift by (l+1).
            keylist = (int)((uint)keylist >> (l + 1));
            baseBit += l + 1;
        }
        return n;
    }

    private static string KeyName(int bitIndex)
        => ItemKeyNames.TryGetValue(bitIndex, out string? name) ? name : "";

    /// <summary>Port of <c>spawnfunc(item_key)</c> + <c>spawn_item_key</c> (keys.qc:100-264): resolve the default
    /// netname/colormod/model from <c>.itemkeys</c>, then place the key as a SOLID_TRIGGER pickup.</summary>
    private static void KeySetup(Entity this_)
    {
        this_.ClassName = "item_key";

        // Reject a key with more than one bit set (keys.qc:173-179) unless it's the master key. Headless: drop it.
        if (this_.ItemKeys > 0 && (this_.ItemKeys & (this_.ItemKeys - 1)) != 0 && this_.ItemKeys != 0xffffff)
        {
            MapMover.RemoveEntity(this_);
            return;
        }

        // Default netname + colormod by key id (keys.qc:182-238).
        string netName;
        Vector3 colorMod;
        string model = KeyDefaultModel;
        switch (this_.ItemKeys)
        {
            case 1 << 0: netName = "GOLD key";     colorMod = new Vector3(1f, .9f, 0f);    break;
            case 1 << 1: netName = "SILVER key";   colorMod = new Vector3(.9f, .9f, .9f);  break;
            case 1 << 2: netName = "BRONZE key";   colorMod = new Vector3(.6f, .25f, 0f);  break;
            case 1 << 3: netName = "RED keycard";  colorMod = new Vector3(.9f, 0f, 0f);    break; // FIXME(Base): keycard model
            case 1 << 4: netName = "BLUE keycard"; colorMod = new Vector3(0f, 0f, .9f);    break;
            case 1 << 5: netName = "GREEN keycard";colorMod = new Vector3(0f, .9f, 0f);    break;
            case 0xffffff: netName = "MASTER key"; colorMod = new Vector3(1f, .25f, .25f); break;
            default:
                // An unlisted/custom key: requires a custom netname AND model, else QC objerrors (keys.qc:221-237).
                netName = "FLUFFY PINK keycard";
                colorMod = new Vector3(1f, 1f, 1f);
                if (string.IsNullOrEmpty(this_.NetName) || string.IsNullOrEmpty(this_.Model))
                {
                    MapMover.RemoveEntity(this_);
                    return;
                }
                break;
        }

        if (string.IsNullOrEmpty(this_.NetName))
            this_.NetName = netName;
        if (this_.ColorModKey == Vector3.Zero)   // QC: if(!this.colormod) — null vector means "unset"
            this_.ColorModKey = colorMod;

        // QC keys.qc:240: item_keys_names[lowestbit(this.itemkeys)] = this.netname; — record the display name
        // so func_door/trigger_keylock's "You need <the SILVER key>" centerprints can name the missing keys.
        ItemKeyNames[LowestBit(this_.ItemKeys)] = this_.NetName;
        if (string.IsNullOrEmpty(this_.Model))
            this_.Model = model;
        if (string.IsNullOrEmpty(this_.Message))
            this_.Message = "You've picked up the " + this_.NetName + "!";
        if (string.IsNullOrEmpty(this_.Noise))
            this_.Noise = KeyDefaultSound;

        SpawnItemKey(this_);
    }

    /// <summary>Port of <c>spawn_item_key</c> (keys.qc:100-134): movetype/model/solid + bbox placement + touch.</summary>
    private static void SpawnItemKey(Entity this_)
    {
        // spawnflags&1 (FLOATING) => noalign (keys.qc:104-105).
        if ((this_.SpawnFlags & 1) != 0)
            this_.NoAlign = true;

        // noalign => MOVETYPE_NONE (suspended), else MOVETYPE_TOSS (drop to floor) (keys.qc:107-110).
        this_.MoveType = this_.NoAlign ? MoveType.None : MoveType.Toss;

        if (Api.Services is not null && !string.IsNullOrEmpty(this_.Model))
            Api.Entities.SetModel(this_, this_.Model);

        this_.Effects = AdvancedMovers.EfLowPrecision; // QC: this.effects = EF_LOWPRECISION
        // QC: this.modelflags |= MF_ROTATE (keys.qc:117). The port has no networked modelflags channel, so the
        // render-only spin rides this bool on the shared edict; EntityNode applies DP's '0 100 0'*fmod(time,3.6)
        // yaw spin (csqcmodel_hooks.qc:617) on the listen-server/demo path. MF_ROTATE is keys-only in all of Base.
        this_.ModelSpinRotate = true;
        this_.Solid = Solid.Trigger;

        // bbox placement (keys.qc:122-123): origin raised +32z within the bbox, size '-16 -16 -56'..'16 16 0'.
        MapMover.SetOrigin(this_, this_.Origin + new Vector3(0f, 0f, 32f));
        MapMover.SetSize(this_, new Vector3(-16f, -16f, -56f), new Vector3(16f, 16f, 0f));

        // NOTE: not FL_ITEM, so no DropToFloor_QC special-casing; the TOSS integrator settles it next frame
        // (keys.qc:129-131 DropToFloor_QC_DelayedInit). Matches StartItem.cs's TOSS drop contract.

        this_.Touch = KeyTouch;
        MapMover.IndexRegister(this_);
    }

    /// <summary>Port of <c>item_key_touch</c> (keys.qc:77-95): grant the key bits to a player, play the pickup
    /// sound + centerprint, then fire <c>.target</c> ONCE (the message is blanked across SUB_UseTargets so the
    /// fired targets don't re-print it, exactly as QC does).</summary>
    private static void KeyTouch(Entity this_, Entity toucher)
    {
        if ((toucher.Flags & EntFlags.Client) == 0) // IS_PLAYER
            return;

        // Already holds every bit this key grants -> ignore (keys.qc:83-84).
        if ((toucher.ItemKeys & this_.ItemKeys) != 0)
            return;

        toucher.ItemKeys |= this_.ItemKeys;
        MapMover.Play2(toucher, this_.Noise); // QC keys.qc:87 play2(toucher, this.noise) — 2D VOL_BASE/ATTEN_NONE
        MapMover.Centerprint(toucher, this_.Message);

        // Fire .target with the message blanked so SUB_UseTargets doesn't re-centerprint it (keys.qc:91-94).
        string oldMessage = this_.Message;
        this_.Message = "";
        MapMover.UseTargets(this_, toucher, toucher);
        this_.Message = oldMessage;
    }
}

/// <summary>
/// The (stateless) spawnfunc bridge for the gametype OBJECTIVE entities (CTF flags, Domination control points,
/// Race checkpoints/penalty zones, Onslaught generators/control-points/links). The BSP entity lump calls these
/// when it reads the matching classname; each tags the edict's classname and routes it to <see cref="Sink"/>,
/// which the host (<c>GameWorld</c>) wires to the ACTIVE gametype so the objective is registered on the right
/// mode (mirrors <see cref="WarpzoneSpawns.Sink"/> / <c>Porto.PortalSpawner</c>). When no sink is wired — a
/// bare unit test, or a non-objective gametype — the edict is ignored; the gametype's own
/// <c>SpawnFlag</c>/<c>SpawnControlPoint</c>/<c>SpawnCheckpoint</c> API is the direct path the deterministic
/// tests exercise. The edict already carries <c>Origin</c>/<c>Angles</c>/<c>Team</c>/<c>Fields</c> from the lump.
/// </summary>
public static class GametypeObjectiveSpawns
{
    /// <summary>Route a spawned objective edict to this match's active gametype. Null = no sink (unit tests).</summary>
    public static System.Action<Entity>? Sink;

    private static void Emit(Entity e, string className, int team)
    {
        e.ClassName = className;
        e.Team = team;
        Sink?.Invoke(e);
    }

    // CTF flags (item_flag_team1..4 map to NUM_TEAM_1..4; item_flag_neutral is the one-flag flag).
    public static void FlagTeam1(Entity e) => Emit(e, "item_flag_team1", Teams.Red);
    public static void FlagTeam2(Entity e) => Emit(e, "item_flag_team2", Teams.Blue);
    public static void FlagTeam3(Entity e) => Emit(e, "item_flag_team3", Teams.Yellow);
    public static void FlagTeam4(Entity e) => Emit(e, "item_flag_team4", Teams.Pink);
    public static void FlagNeutral(Entity e) => Emit(e, "item_flag_neutral", Teams.None);

    // Domination control point (the owning team starts neutral; the map may set an initial team via fields).
    public static void DomControlPoint(Entity e) => Emit(e, "dom_controlpoint", (int)e.Team);

    // Domination TEAM DEFINITION (dom_team): a data holder that in Base carries a team's control-point model
    // (dom_red/blue/…​md3). The port doesn't consume these — but the lump copies their `model` key onto the edict,
    // so WITHOUT routing them through the sink they survive as live, networked, RENDERED models (a cluster of
    // dom_*.md3 on a platform, wrongly visible in every mode incl. DM — playtest #20). Emit so the sink retires
    // them: removed in non-dom (default arm), and the Domination arm also retires them (never a placement).
    public static void DomTeam(Entity e) => Emit(e, "dom_team", (int)e.Team);

    // Race checkpoint + penalty zone (checkpoint index / penalty seconds come from the edict fields).
    public static void RaceCheckpoint(Entity e) => Emit(e, "trigger_race_checkpoint", Teams.None);
    public static void RacePenalty(Entity e) => Emit(e, "trigger_race_penalty", Teams.None);

    // Onslaught generator / control point / link (the generator's team comes from the edict).
    public static void OnslaughtGenerator(Entity e) => Emit(e, "onslaught_generator", (int)e.Team);
    public static void OnslaughtControlPoint(Entity e) => Emit(e, "onslaught_controlpoint", Teams.None);
    public static void OnslaughtLink(Entity e) => Emit(e, "onslaught_link", Teams.None);

    // ---- Assault objectives (common/gametypes/gametype/assault/sv_assault.qc) ----
    // The objective-chain edicts (target_objective / _decrease / func_assault_destructible / roundend/roundstart)
    // keep their classname so the Assault sink routes each by name; the chain's .target/.targetname/.health/.dmg
    // come off the edict (ApplyDictFields plumbs targetname/target/health/dmg). info_player_attacker/defender are
    // SPAWN POINTS, not objectives — handled below as classname-retagged passive spawnpoints (see RegisterAll).
    public static void TargetObjective(Entity e)         => Emit(e, "target_objective", Teams.None);
    public static void TargetObjectiveDecrease(Entity e) => Emit(e, "target_objective_decrease", Teams.None);
    public static void FuncAssaultDestructible(Entity e) => Emit(e, "func_assault_destructible", Teams.None);
    public static void FuncAssaultWall(Entity e)         => Emit(e, "func_assault_wall", Teams.None);
    public static void TargetAssaultRoundend(Entity e)   => Emit(e, "target_assault_roundend", Teams.None);
    public static void TargetAssaultRoundstart(Entity e) => Emit(e, "target_assault_roundstart", Teams.None);

    // ---- Nexball goals + balls (common/gametypes/gametype/nexball/sv_nexball.qc) ----
    // QC each goal spawnfunc sets this.team = NUM_TEAM_n (or GOAL_FAULT/GOAL_OUT) then SpawnGoal(this). We mirror
    // that team-stamp via Emit; the sink reads (int)e.Team and calls Nexball.SpawnGoal(team-or-sentinel, origin).
    // The PORT sentinels Nexball.GoalFault/GoalOut (-2/-3) are used — NOT QC's raw -1/-2 (see RISKS in recon).
    public static void NexballRedGoal(Entity e)    => Emit(e, "nexball_goal", Teams.Red);    // QC NUM_TEAM_1
    public static void NexballBlueGoal(Entity e)   => Emit(e, "nexball_goal", Teams.Blue);   // QC NUM_TEAM_2
    public static void NexballYellowGoal(Entity e) => Emit(e, "nexball_goal", Teams.Yellow); // QC NUM_TEAM_3
    public static void NexballPinkGoal(Entity e)   => Emit(e, "nexball_goal", Teams.Pink);   // QC NUM_TEAM_4
    public static void NexballFault(Entity e)      => Emit(e, "nexball_goal", Nexball.GoalFault);
    public static void NexballOut(Entity e)        => Emit(e, "nexball_goal", Nexball.GoalOut);
    public static void NexballBasketball(Entity e) => Emit(e, "nexball_basketball", Teams.None);
    public static void NexballFootball(Entity e)   => Emit(e, "nexball_football", Teams.None);
    // Compat aliases (QC spawnfuncs preserved for compatibility). ball/ball_football → football,
    // ball_basketball → basketball, ball_fault → fault, ball_bound → out, and the INTENTIONALLY SWAPPED goals
    // ball_redgoal → BLUE goal / ball_bluegoal → RED goal ("I blame Revenant" — sv_nexball.qc:697-704).
    public static void BallFootball(Entity e)   => NexballFootball(e);
    public static void BallBasketball(Entity e) => NexballBasketball(e);
    public static void BallRedGoal(Entity e)    => Emit(e, "nexball_goal", Teams.Blue); // QC ball_redgoal → bluegoal
    public static void BallBlueGoal(Entity e)   => Emit(e, "nexball_goal", Teams.Red);  // QC ball_bluegoal → redgoal
    public static void BallFault(Entity e)      => NexballFault(e);
    public static void BallBound(Entity e)      => NexballOut(e);

    // ---- Invasion spawnpoints / waves / round-end (common/gametypes/gametype/invasion/sv_invasion.qc) ----
    // invasion_wave reads .cnt (wave number) + .spawnmob (monster list); both are plumbed by ApplyDictFields.
    public static void InvasionSpawnpoint(Entity e)      => Emit(e, "invasion_spawnpoint", Teams.None);
    public static void InvasionWave(Entity e)            => Emit(e, "invasion_wave", Teams.None);
    public static void TargetInvasionRoundend(Entity e)  => Emit(e, "target_invasion_roundend", Teams.None);

    // ---- CTS start/stop/intermediate timers (server/race.qc target_checkpoint_setup, gated !g_race && !g_cts) ----
    // The port's CTS models a single start→stop course (no defrag intermediate checkpoints), so target_checkpoint
    // is consumed as a no-op; target_startTimer/target_stopTimer spawn the run timers.
    public static void TargetStartTimer(Entity e) => Emit(e, "target_startTimer", Teams.None);
    public static void TargetStopTimer(Entity e)  => Emit(e, "target_stopTimer", Teams.None);
    public static void TargetCheckpoint(Entity e) => Emit(e, "target_checkpoint", Teams.None);
}
