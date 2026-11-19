# Parity Index

_Generated 2026-11-19 from 164 units, 2402 features._

Status: `OK`=faithful `~`=partial `stub` `MISS`=missing `-`=n/a `?`=unknown; liveness `live`/`DEAD`/`~`/`?`.

## Dimension rollup

| dim | dead | faithful | live | missing | na | partial | stub | unknown |
|---|---|---|---|---|---|---|---|---|
| logic | 0 | 2047 | 0 | 54 | 39 | 250 | 6 | 6 |
| values | 0 | 1892 | 0 | 36 | 286 | 165 | 0 | 23 |
| timing | 0 | 1144 | 0 | 14 | 1150 | 70 | 0 | 24 |
| presentation | 0 | 679 | 0 | 75 | 1312 | 305 | 1 | 30 |
| audio | 0 | 318 | 0 | 20 | 2030 | 27 | 0 | 7 |
| liveness | 26 | 0 | 2194 | 0 | 61 | 114 | 0 | 7 |

## Features by unit

### `bot-ai` (bot) — 29 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `bot-ai.population.fixcount` | Population fill/trim (bot_number/minplayers/bot_vs_human, one add per frame) | OK | OK | OK | - | - | live | high |
| `bot-ai.population.strategytoken` | Strategy-token rotation (one goal search per frame) | OK | - | OK | - | - | live | high |
| `bot-ai.population.namemodel` | Bot name/model/skin from bots.txt (prefix/suffix, dedup, model path, skin, shirt/pants, forced team) | OK | OK | - | - | - | live | high |
| `bot-ai.think.throttle` | Per-bot think throttle + god mode + ping (bot_think) | OK | ~ | OK | - | - | live | high |
| `bot-ai.aim.aimdir` | Aim turn + skill error/smoothing (bot_aimdir filters) | OK | OK | OK | - | - | live | high |
| `bot-ai.aim.fire` | Fire decision (maxfiredeviation cone + fire timer + LOS + aggression) | OK | OK | OK | - | - | live | high |
| `bot-ai.aim.lead` | Projectile lead + ballistic arc (bot_shotlead / findtrajectorywithleading) | ~ | ~ | - | - | - | live | medium |
| `bot-ai.target.chooseenemy` | Enemy selection (nearest attackable with LOS, sticky tracking, SUPERBOT rating) | ~ | OK | OK | - | - | live | high |
| `bot-ai.target.shouldattack` | Target eligibility filter (bot_shouldattack) | OK | OK | - | - | - | live | high |
| `bot-ai.weapon.choose` | Weapon selection by range + combos (havocbot_chooseweapon) | OK | OK | OK | - | - | live | high |
| `bot-ai.weapon.reload` | Idle weapon reload (havocbot_ai not-attacking branch) | OK | OK | OK | - | - | live | high |
| `bot-ai.weapon.wr_aim` | Per-weapon fire driver wr_aim (secondary fire / detonate / combo / charge) | OK | - | - | - | - | live | high |
| `bot-ai.roles.goalrating` | Roles + goal rating (havocbot_role_generic / objective roles, routerating) | OK | OK | OK | - | - | live | high |
| `bot-ai.roles.freezetag` | Freeze Tag bot role (revive frozen teammates) | OK | OK | OK | - | - | live | high |
| `bot-ai.move.steer` | Goal-stack steering + obstacle/step jump (havocbot_movetogoal) | ~ | OK | OK | - | - | live | high |
| `bot-ai.move.bunnyhop` | Bunnyhopping toward far goals (havocbot_bunnyhop) | OK | OK | OK | - | - | live | high |
| `bot-ai.move.danger` | Danger look-ahead (lava/void/cliff/trigger_hurt) + evade | OK | OK | OK | - | - | live | high |
| `bot-ai.combat.dodge` | Combat movement (projectile dodge + retreat) | ~ | ~ | ~ | - | - | live | high |
| `bot-ai.special.jetpack_rocketjump` | Jetpack navigation + rocketjump/jetpack trigger_hurt escape | ~ | ~ | ~ | - | - | ~ | high |
| `bot-ai.move.keyboard` | Keyboard-movement emulation (havocbot_keyboard_movement) | OK | OK | OK | - | - | live | high |
| `bot-ai.scripting.commands` | Bot scripting command queue (bot_cmd / scripting.qc) | ~ | OK | OK | - | OK | live | high |
| `bot-ai.skill.modifiers` | Skill model: single knob vs 12 per-bot skill columns + autoskill *(intended)* | ~ | ~ | - | - | - | ~ | high |
| `bot-ai.state.aistatus` | AI status bitfield (.aistatus / AI_STATUS_*) | ~ | - | - | - | - | ~ | medium |
| `bot-ai.aim.latency` | Simulated bot input latency (skill-based ping + bot_aimlatency shot-lead delay) *(intended)* | MISS | MISS | MISS | - | - | - | high |
| `bot-ai.aim.reset` | Aim-state reset at discontinuities (bot_aim_reset on spawn / teleporter / warpzone) | ~ | - | - | - | - | ~ | high |
| `bot-ai.scripting.console_interface` | bot_cmd console surface (queue/parse, help/list, find bot by name/number, clearqueue) | OK | - | - | - | - | live | high |
| `bot-ai.scripting.moveto_personal_waypoint` | Scripted moveto via personal waypoint (havocbot_moveto / resetgoal, AI_STATUS_WAYPOINT_PERSONAL_*) *(intended)* | ~ | OK | ~ | - | - | live | high |
| `bot-ai.cvars.dead_knobs` | Bot cvar knobs declared but unread (bot_usemodelnames / bot_navigation_ignoreplayers / bot_wander_enable / bot_debug_*) | stub | ~ | - | - | - | DEAD | high |
| `bot-ai.roles.item_sharing` | Teamplay item-sharing: friends-aware pickup check + cross-bot per-item rating dedup | MISS | MISS | - | - | - | - | high |

### `bot-waypoints` (bot) — 17 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `bot-waypoints.graph.node_model` | Waypoint node + box/point model and flag bits | OK | OK | - | - | - | live | high |
| `bot-waypoints.graph.link_cost` | Link travel-cost model (linear + fall-time + water/crouch) | OK | OK | - | - | - | live | high |
| `bot-waypoints.graph.autolink` | Auto-relink (waypoint_think): PVS + distance + bidirectional tracewalk | OK | OK | - | - | - | ~ | high |
| `bot-waypoints.file.load_waypoints` | Load .waypoints node file (waypoint_loadall) with .race fallback | OK | OK | - | - | - | live | high |
| `bot-waypoints.file.load_links` | Load .waypoints.cache precompiled links (waypoint_load_links) | OK | OK | - | - | - | live | high |
| `bot-waypoints.file.load_hardwired` | Load .waypoints.hardwired map-maker links (waypoint_load_hardwiredlinks) | OK | OK | - | - | - | live | high |
| `bot-waypoints.file.save` | Save .waypoints/.cache/.hardwired writers (waypoint_saveall/_save_links/_save_hardwiredlinks) | OK | OK | - | - | - | DEAD | high |
| `bot-waypoints.auto.spawnforitem` | Auto item/spawn waypoints (waypoint_spawnforitem) | OK | OK | - | - | - | ~ | high |
| `bot-waypoints.auto.spawnforteleporter` | Auto teleporter/jumppad/warpzone waypoints (waypoint_spawnforteleporter/_wz) | ~ | ~ | - | - | - | ~ | medium |
| `bot-waypoints.path.findnearest` | Nearest-waypoint query (navigation_findnearestwaypoint) | OK | OK | - | - | - | live | high |
| `bot-waypoints.path.markroutes` | Shortest-path search over the graph (navigation_markroutes Dijkstra flood) + danger bias *(intended)* | OK | OK | - | - | - | live | high |
| `bot-waypoints.path.routetogoal` | Route building onto the goal stack (navigation_routetogoal + goalstack) | ~ | OK | - | - | - | live | high |
| `bot-waypoints.steer.movetogoal` | Steering / path follow (havocbot_movetogoal: jump/crouch/ladder/obstacle/brake/bunnyhop) | ~ | OK | OK | - | - | live | high |
| `bot-waypoints.steer.bunnyhop` | Bunnyhop maintenance (havocbot_bunnyhop) | ~ | OK | - | - | - | live | high |
| `bot-waypoints.steer.checkdanger` | Per-frame danger probe (havocbot_checkdanger: void/cliff/lava/trigger_hurt) | OK | OK | - | - | - | live | high |
| `bot-waypoints.steer.steerlib` | steerlib primitives (arrive/attract/repel/flock/swarm/traceavoid/beamsteer/wander) | MISS | MISS | - | - | - | - | high |
| `bot-waypoints.editor` | Waypoint editor (spawn/remove from editor, symmetry, hardwire, autowaypoints) *(intended)* | ~ | ~ | ~ | ~ | - | live | high |

### `cl-announcer` (client) — 9 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `cl-announcer.driver.frame` | Per-frame announcer driver (gametype gate, gamestart + time) *(intended)* | OK | - | OK | - | - | live | high |
| `cl-announcer.countdown.numbers` | Countdown number announcements (3-2-1) + center | OK | OK | OK | OK | OK | live | high |
| `cl-announcer.gamestart.prepare` | PREPARE one-shot on countdown arming (>=5s gate) | OK | OK | OK | OK | OK | live | high |
| `cl-announcer.time.remaining` | Remaining map-time cues (5/1 min) with hysteresis | OK | OK | OK | OK | OK | live | high |
| `cl-announcer.maptime.cvar` | cl_announcer_maptime mode source + menu mapping *(intended)* | OK | OK | - | - | - | live | high |
| `cl-announcer.voice.playback` | Announcer voice playback + antispam + queue | OK | OK | OK | OK | OK | live | medium |
| `cl-announcer.voice.cvars` | cl_announcer (voice pack) + cl_announcer_antispam wiring | OK | OK | - | OK | OK | live | high |
| `cl-announcer.title.gametype` | Gametype-name centerprint title above countdown | OK | OK | OK | OK | - | live | high |
| `cl-announcer.title.duel` | Duel title (A vs B) in 1v1 + ROUNDSTOP | OK | OK | OK | OK | - | live | high |

### `cl-centerprint` (client) — 13 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `cl-centerprint.ring.add_replace` | Message ring + add / replace-by-cpid | OK | OK | OK | OK | - | live | high |
| `cl-centerprint.feed.msg_center` | MSG_CENTER notification feed → panel *(intended)* | OK | OK | OK | OK | - | live | high |
| `cl-centerprint.countdown.count_token` | ^COUNT countdown decrement | OK | OK | OK | OK | - | live | high |
| `cl-centerprint.fade.in_out` | Per-message fade in / out | OK | OK | OK | OK | - | live | high |
| `cl-centerprint.fade.subsequent` | Subsequent-message two-pass progressive fade | OK | OK | OK | OK | - | live | high |
| `cl-centerprint.layout.draw` | Layout: flip / align / wrap / ^BOLD / color codes / font scales | OK | OK | - | OK | - | live | high |
| `cl-centerprint.kill.group` | centerprint_Kill — graceful group fade-out | OK | OK | OK | OK | - | live | high |
| `cl-centerprint.kill.all_remote` | MSG_CENTER_KILL — remote group / all kill from server | OK | OK | OK | OK | - | live | high |
| `cl-centerprint.title.gametype` | Gametype title line (centerprint_SetTitle + Announcer driver) | OK | OK | OK | OK | - | live | high |
| `cl-centerprint.title.duel` | Duel title 'left vs right' (centerprint_SetDuelTitle + Announcer_Duel) | OK | OK | OK | OK | - | live | high |
| `cl-centerprint.feed.engine_builtin` | Raw engine centerprint() builtin → panel (map/trigger/door/item/target_print text) | OK | - | - | OK | OK | live | high |
| `cl-centerprint.feed.chat_tell` | Server chat 'tell'/private-message centerprint (cmsg/sourcecmsg) | OK | OK | - | OK | - | live | high |
| `cl-centerprint.config.preview` | HUD-config live preview messages + title | OK | OK | OK | OK | - | live | high |

### `cl-crosshair` (client) — 18 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `cl-crosshair.core.master_gating` | Master gating + enable/number/size/alpha early-outs | ~ | OK | - | OK | - | live | high |
| `cl-crosshair.core.origin_chase` | Crosshair origin + third-person chase trace & body-alpha fade | ~ | OK | OK | OK | - | live | high |
| `cl-crosshair.trueaim.hit_classification` | True-aim forward trace + HITTEAM/HITENEMY/HITWORLD/HITOBSTRUCTION classification *(intended)* | ~ | OK | ~ | OK | - | live | medium |
| `cl-crosshair.trueaim.showimpact` | Debug: draw crosshair at the reprojected impact point (crosshair_hittest_showimpact) | MISS | OK | - | MISS | - | - | high |
| `cl-crosshair.color.special_modes` | Crosshair color: weapon / health+armor / fixed (crosshair_color_special) | OK | OK | - | OK | - | live | high |
| `cl-crosshair.anim.pickup_pulse` | Item-pickup crosshair pulse (crosshair_pickup) | OK | OK | OK | OK | - | ~ | high |
| `cl-crosshair.anim.hit_indication` | Hit-indication scale bump + color flash (crosshair_hitindication) | OK | OK | OK | OK | - | live | high |
| `cl-crosshair.anim.smooth_ease` | Goal-based smooth scale/alpha/color ease (crosshair_effect_time) | OK | OK | OK | ~ | - | live | high |
| `cl-crosshair.anim.switch_crossfade` | Weapon-switch crosshair image cross-fade | OK | OK | OK | OK | - | live | medium |
| `cl-crosshair.trueaim.teammate_blur_signal` | Teammate shrink + wall/teammate blur signalling | OK | OK | - | ~ | - | live | high |
| `cl-crosshair.ring.vortex_charge` | Vortex / Overkill-Nex charge ring (+ inner chargepool / moving-average ring) | ~ | OK | OK | ~ | - | live | high |
| `cl-crosshair.ring.reload_ammo` | Reload / ammo (clip) ring (+ Rifle ring art) | OK | OK | OK | OK | - | live | high |
| `cl-crosshair.ring.minelayer_hagar_arc` | Mine Layer / Hagar load / Arc heat rings | OK | OK | OK | OK | - | live | high |
| `cl-crosshair.ring.switch_fade` | Weapon-switch ring fade-out/in (wcross_ring_prev) | OK | OK | OK | OK | - | ~ | medium |
| `cl-crosshair.dot` | Center dot (crosshair_dot) | OK | OK | - | OK | - | live | high |
| `cl-crosshair.objring.nade_capture_revive` | Objective rings: NADE_TIMER > CAPTURE_PROGRESS > REVIVE_PROGRESS | OK | OK | OK | ~ | - | ~ | high |
| `cl-crosshair.per_weapon_2d_vehicle` | Per-weapon crosshair pic + 2D side-scroller + vehicle crosshair | ~ | ~ | - | ~ | - | ~ | high |
| `cl-crosshair.reticle.zoom_scope` | Zoom reticle (DrawReticle) — full-screen scope overlay | OK | OK | OK | OK | - | live | medium |

### `cl-csqcmodel` (client) — 18 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `cl-csqcmodel.predraw.dispatch_order` | Predraw dispatch + ordered pass (appearance->LOD->anim->tag->effects) | ~ | - | OK | ~ | - | live | high |
| `cl-csqcmodel.forcemodel.model_skin` | Force player model / skin (cl_forceplayermodels family) | OK | OK | - | OK | - | live | high |
| `cl-csqcmodel.forcecolors.cascade` | Force player colors + unique enemy colors *(intended)* | OK | OK | - | OK | - | ~ | high |
| `cl-csqcmodel.glowmod.deathfade_ghost` | Glowmod from colormap + death-fade + respawn-ghost *(intended)* | OK | OK | ~ | OK | - | live | high |
| `cl-csqcmodel.lod.distance_swap` | LOD model selection + swap *(intended)* | ~ | OK | - | ~ | - | ~ | high |
| `cl-csqcmodel.skeleton.upper_lower_aim` | Skeletal upper/lower split + aim bones + fixbone | OK | OK | OK | OK | - | live | high |
| `cl-csqcmodel.animdecide.state_machine` | animdecide locomotion + upper-body action state machine *(intended)* | OK | OK | OK | OK | - | live | high |
| `cl-csqcmodel.fallbackframe.remap` | Fallback frame remap (missing-frame substitution) | OK | OK | - | OK | - | ~ | medium |
| `cl-csqcmodel.tagindex.weapon_attach` | Auto tag-index (weapon/attachment bone resolution) | ~ | - | OK | ~ | - | ~ | medium |
| `cl-csqcmodel.effects.ef_mf_trail_lights` | EF_*/MF_* effects: dynamic lights, particles, render-flags, trails | ~ | ~ | OK | ~ | - | ~ | high |
| `cl-csqcmodel.effects.jetpack_loop` | Jetpack loop sound (MF_ROCKET) | OK | OK | OK | - | OK | live | high |
| `cl-csqcmodel.networking.csqcmodel_contract` | CSQCMODEL networked-property contract (effects/modelflags/skin/traileffect/colormap/colormod/glowmod/scale/alpha/v_angle/anim_*) | ~ | ~ | - | ~ | - | ~ | high |
| `cl-csqcmodel.anim.animstate_set_update` | Generic entity frame-animation machinery (anim_set / anim_update animstate) | ~ | ~ | ~ | ~ | - | live | medium |
| `cl-csqcmodel.animdecide.frame_registry` | Player animation framename registry + frame lookup (REGISTER_ANIMATION / ModelAnimation.getframe) | OK | ~ | - | ~ | - | live | medium |
| `cl-csqcmodel.animdecide.explicit_setstate` | Explicit networked anim states (animdecide_setstate: DEAD1/DEAD2/DUCK/FROZEN/FOLLOW) | ~ | ~ | - | ~ | - | live | medium |
| `cl-csqcmodel.hooks.header_contract` | csqcmodel_hooks.qh header contract (autocvar defaults + EF_/MF_ bit constants + csqcmodel_isdead consumer) | OK | OK | - | - | - | live | high |
| `cl-csqcmodel.skeleton.bone_class_contract` | Skeleton entity-class contract (bone_upperbody / bone_weapon / bone_aim+weights / fixbone) | OK | OK | - | OK | - | live | high |
| `cl-csqcmodel.prediction.csqcplayer_contract` | Client-side player prediction contract (csqcplayer / csqcplayer_status / cl_movement / stair-smoothing API) | ~ | ~ | ? | - | - | live | medium |

### `cl-hud` (client) — 60 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `cl-hud.engine.registry_and_order` | Panel registry, z-order, panel-order cvar | OK | OK | - | ~ | - | live | high |
| `cl-hud.engine.loadcvars` | Per-frame cvar-driven layout (HUD_Panel_LoadCvars) | OK | OK | OK | OK | - | live | high |
| `cl-hud.engine.skin_border` | 9-slice skin border (draw_BorderPicture) | OK | OK | - | OK | - | live | high |
| `cl-hud.engine.progressbar` | Progress-bar primitive (HUD_Panel_DrawProgressBar) | OK | OK | - | OK | - | live | high |
| `cl-hud.engine.num_color` | Value tint gradient (HUD_Get_Num_Color) | OK | OK | OK | OK | - | live | high |
| `cl-hud.engine.dynamic_shake` | Damage-keyed whole-HUD shake (Hud_Dynamic_Frame shake) | OK | OK | OK | OK | - | live | high |
| `cl-hud.engine.dynamic_follow` | HUD sway with viewmodel (hud_dynamic_follow) | OK | OK | OK | OK | - | live | high |
| `cl-hud.engine.dock` | HUD dock background (hud_dock) | OK | OK | - | OK | - | live | high |
| `cl-hud.engine.configure_mode` | HUD configure-mode editor (hud_config.qc) | OK | OK | ? | OK | - | live | medium |
| `cl-hud.panel.weapons` | Weapons panel (#0) | OK | OK | ? | OK | - | live | medium |
| `cl-hud.panel.ammo` | Ammo panel (#1) | OK | OK | - | OK | - | live | medium |
| `cl-hud.panel.powerups` | Powerups panel (#2) | OK | OK | ? | OK | - | live | medium |
| `cl-hud.panel.healtharmor` | Health/armor panel (#3) | OK | OK | OK | OK | - | live | high |
| `cl-hud.panel.notify` | Notify / kill-feed panel (#4) | OK | OK | OK | OK | - | live | high |
| `cl-hud.panel.timer` | Timer panel (#5) | OK | ? | OK | OK | - | live | medium |
| `cl-hud.panel.radar` | Radar / minimap panel (#6) | OK | OK | - | ~ | - | live | high |
| `cl-hud.panel.score` | Score overlay panel (#7) | OK | OK | - | OK | - | live | high |
| `cl-hud.panel.racetimer` | Race timer panel (#8) | OK | OK | OK | OK | - | live | high |
| `cl-hud.panel.vote` | Vote panel (#9) | OK | OK | OK | OK | - | live | high |
| `cl-hud.panel.modicons` | Mod icons panel (#10) | OK | ? | ? | OK | - | live | medium |
| `cl-hud.panel.pressedkeys` | Pressed-keys panel (#11) | OK | OK | - | ~ | - | live | medium |
| `cl-hud.panel.chat` | Chat panel (#12) *(intended)* | ~ | - | ~ | OK | - | live | high |
| `cl-hud.panel.engineinfo` | Engine-info (FPS) panel (#13) *(intended)* | OK | ? | ? | OK | - | live | medium |
| `cl-hud.panel.infomessages` | Info-messages panel (#14) | ~ | ? | ? | OK | - | live | medium |
| `cl-hud.panel.physics` | Physics / speedometer panel (#15) | OK | OK | ? | OK | - | live | medium |
| `cl-hud.panel.centerprint` | Center-print panel (#16) | OK | OK | OK | OK | - | live | high |
| `cl-hud.panel.itemstime` | Items-time panel (#17) | OK | ? | ? | ~ | - | live | medium |
| `cl-hud.panel.quickmenu` | Quick-menu panel | ~ | ? | - | ~ | - | live | high |
| `cl-hud.panel.scoreboard` | Scoreboard panel *(intended)* | OK | OK | ? | OK | - | live | medium |
| `cl-hud.panel.strafehud` | Strafe HUD panel | OK | ? | ? | ~ | - | live | low |
| `cl-hud.panel.pickup` | Pickup panel (#26) | OK | OK | OK | OK | - | live | medium |
| `cl-hud.panel.checkpoints` | Checkpoints panel (#27) | OK | OK | OK | OK | - | live | high |
| `cl-hud.panel.mapvote` | Map-vote panel | OK | OK | OK | OK | - | live | high |
| `cl-hud.panel.minigame` | Minigame HUD panels (BOARD/STATUS/HELP/MENU) | ~ | ? | ? | ~ | - | live | medium |
| `cl-hud.net.hybrid_hud` | Hybrid HUD (NetHud fallback + _fullHud), font + skin wiring *(intended)* | OK | - | - | OK | - | live | high |
| `cl-hud.draw.circle_clipped_pic` | Radial ring clipping primitive (DrawCircleClippedPic) | OK | OK | - | ~ | - | live | medium |
| `cl-hud.draw.borderlines` | Hollow rectangle frame primitive (drawborderlines) | OK | OK | - | ? | - | live | medium |
| `cl-hud.draw.pic_tiled` | Repeat-tiling pic primitive (drawpic_tiled) | MISS | - | - | ~ | - | ~ | high |
| `cl-hud.draw.string_aspect` | Fit-to-box text scaling (drawstring_aspect / DRAWSTRING_ASPECT_SCALE + expanding text variants) | ~ | - | - | ~ | - | live | medium |
| `cl-hud.draw.hud_shift_scale_macros` | Dynamic-HUD draw macro layer (HUD_Shift/HUD_Scale) + per-panel dynamichud gate + bold-font convention *(intended)* | ~ | ~ | - | ~ | - | ~ | high |
| `cl-hud.header.hud_config` | Configure-mode header contract (hud_config.qh cvars + HUD_Write export macros) | ? | OK | - | OK | - | live | medium |
| `cl-hud.header.mapvote` | Mapvote client header contract (highlight border, input event, picture net download) | ~ | OK | - | ~ | - | ~ | high |
| `cl-hud.header.physics` | Physics panel header defaults + mode constants (physics.qh) | OK | OK | - | OK | - | live | high |
| `cl-hud.header.weapons` | Weapons panel header defaults (weapons.qh) + accuracy color-level table | ~ | ~ | - | OK | - | ~ | high |
| `cl-hud.header.racetimer` | Race HUD client state block (racetimer.qh race_* globals) | ~ | ? | - | ~ | - | ~ | medium |
| `cl-hud.header.healtharmor` | Healtharmor panel header defaults (healtharmor.qh) | OK | OK | - | OK | - | live | high |
| `cl-hud.header.quickmenu` | Quickmenu header defaults + file-source cvars (quickmenu.qh) | ~ | ~ | - | OK | - | live | high |
| `cl-hud.header.ammo` | Ammo panel header defaults (ammo.qh) + nade progressbar hook | OK | OK | - | OK | - | live | high |
| `cl-hud.header.chat` | Chat panel engine-cvar layer (chat.qh con_chat*/con_notify*) *(intended)* | ~ | ~ | - | OK | - | live | medium |
| `cl-hud.header.timer` | Timer panel header defaults (timer.qh warning thresholds) | OK | OK | - | OK | - | live | high |
| `cl-hud.header.powerups` | Powerups header defaults + powerup progressbar color cvars (powerups.qh) | OK | ~ | - | ~ | - | live | high |
| `cl-hud.header.pickup` | Pickup panel header defaults (pickup.qh) | OK | OK | - | OK | - | live | high |
| `cl-hud.header.notify` | Notify panel header defaults (notify.qh) | OK | OK | - | OK | - | live | high |
| `cl-hud.header.engineinfo` | Engineinfo FPS-averaging header defaults (engineinfo.qh) | OK | OK | ? | OK | - | live | high |
| `cl-hud.header.vote` | Vote panel header contract (vote.qh alreadyvoted alpha + uid2name) *(design difference; open presentation)* | ~ | OK | ~ | ~ | - | live | medium |
| `cl-hud.header.infomessages` | Infomessages header defaults (infomessages.qh flip/dynamichud/cl_showspectators) | OK | ~ | - | ~ | - | live | high |
| `cl-hud.header.checkpoints` | Checkpoints panel header defaults (checkpoints.qh) | OK | OK | - | OK | - | live | high |
| `cl-hud.header.modicons` | Modicons header (modicons.qh dynamichud + mod_active gate) | OK | OK | - | OK | - | live | medium |
| `cl-hud.header.pressedkeys` | Pressedkeys header defaults (pressedkeys.qh) | OK | OK | - | OK | - | live | high |
| `cl-hud.header.score` | Score panel header defaults (score.qh) | OK | OK | - | OK | - | live | high |

### `cl-scoreboard` (client) — 21 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `cl-scoreboard.toggle.showscores` | Show while +showscores held (+ death/intermission auto-show) | ~ | OK | ~ | OK | - | live | high |
| `cl-scoreboard.fade.inout` | Cross-fade in/out | OK | OK | OK | OK | - | live | high |
| `cl-scoreboard.columns.default_layout` | Default column layout + scoreboard_columns spec | ~ | OK | - | ~ | - | live | high |
| `cl-scoreboard.columns.gametype_filter` | Per-gametype column filter (+/-pattern/field) | OK | - | - | OK | - | live | high |
| `cl-scoreboard.field.formatting` | Per-field value formatting (ScoreString/frags/kdr/sum/dmg) | ~ | ~ | - | ~ | - | live | high |
| `cl-scoreboard.field.ping` | Ping column value + color bands *(design difference; open timing, presentation)* | OK | OK | ~ | ~ | - | live | high |
| `cl-scoreboard.field.packetloss` | Packet-loss (pl) column | ~ | OK | - | ~ | - | live | high |
| `cl-scoreboard.field.name_icons` | Name-cell icons (player color / ready / handicap / ignored / wants-join) | ~ | ~ | ~ | ~ | - | live | high |
| `cl-scoreboard.sort.players` | Player row sort (primary/secondary/registry order) | OK | OK | - | OK | - | live | high |
| `cl-scoreboard.teams.grouping_totals` | Team sections + per-team totals | ~ | ~ | - | ~ | - | live | high |
| `cl-scoreboard.row.highlights` | Row highlights (stripe / self / eliminated) | OK | OK | - | ~ | - | live | high |
| `cl-scoreboard.layout.column_widths` | Content-measured column widths + title condensing | ~ | OK | - | ~ | - | live | high |
| `cl-scoreboard.header.gameinfo` | Game-info header (next map / gametype banner / limits / map+players) | OK | ~ | - | ~ | - | live | high |
| `cl-scoreboard.block.spectators` | Spectator list | OK | OK | - | ~ | - | live | high |
| `cl-scoreboard.block.accuracy` | Accuracy stats block | ~ | ~ | ~ | ~ | - | live | high |
| `cl-scoreboard.block.itemstats` | Item-stats block | OK | ~ | ~ | ~ | - | live | high |
| `cl-scoreboard.block.rankings` | Race, CTS and CTF capture rankings (+ race speed award) | ~ | ~ | ~ | ~ | - | live | high |
| `cl-scoreboard.block.mapstats` | Map stats (monsters / secrets) | OK | OK | - | OK | - | live | high |
| `cl-scoreboard.block.respawn` | Respawn-status line | OK | OK | OK | OK | - | live | high |
| `cl-scoreboard.ui.interactive` | Interactive scoreboard UI (navigation / team select / spectate / kick / tell / column cycling) | ~ | - | ? | ~ | - | live | medium |
| `cl-scoreboard.draw.export` | Scoreboard HUD skin cvar export (hud save) | OK | ~ | - | - | - | live | high |

### `cl-shownames` (client) — 13 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `cl-shownames.core.master_enable` | Master enable + per-client iteration | OK | OK | OK | OK | - | live | high |
| `cl-shownames.gate.team_enemy` | Team / enemy visibility gate | OK | OK | - | - | - | live | high |
| `cl-shownames.gate.self_spectatee` | Own-tag in chase + spectatee-switch window | OK | OK | OK | OK | - | live | medium |
| `cl-shownames.los.traceline` | Line-of-sight occlusion test | OK | OK | - | OK | - | live | high |
| `cl-shownames.fade.ramp` | Six-branch fade ramp (dead/blocked/offscreen/overlap/team/enemy) | OK | OK | OK | OK | - | live | high |
| `cl-shownames.crosshairdistance.gate` | Crosshair-distance proximity gate | OK | OK | OK | - | - | live | high |
| `cl-shownames.antioverlap.box` | Anti-overlap (farther of two overlapping tags fades) | OK | OK | OK | OK | - | live | high |
| `cl-shownames.distance.fade` | Distance fade + hard cull | OK | OK | - | OK | - | live | high |
| `cl-shownames.resize.distance` | Distance resize (shrink, floor 0.5) | OK | OK | - | OK | - | live | high |
| `cl-shownames.status.bar` | Teammate health/armor status bar | OK | OK | - | OK | - | live | high |
| `cl-shownames.name.decolorize` | Name draw + decolorize + width-truncation | OK | OK | - | OK | - | live | high |
| `cl-shownames.alpha.entcs_getalpha` | entcs_GetAlpha model-alpha factor + ShowNames_Draw mutator hook *(intended)* | OK | OK | - | OK | - | live | high |
| `cl-shownames.entcs.private_healtharmor` | entcs private-slice gating of health/armor (enemy zeroing) | OK | OK | - | OK | - | live | high |

### `cl-strafehud` (client) — 18 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `cl-strafehud.gate.show_mode` | Panel show gate (hud_panel_strafehud 0-4, observer hide, Race/CTS showoptional hook) + dynamichud/bg/padding | OK | OK | - | OK | - | live | high |
| `cl-strafehud.input.player_state` | Strafeplayer state acquisition: onground/jump-held/waterlevel/onslick + ground timeout for slick ramps | ~ | OK | OK | - | - | ~ | high |
| `cl-strafehud.input.wishangle_keys` | Wish-angle + forward/backward key derivation (local movement vector vs non-local pressed-keys fallback) | ~ | OK | - | - | - | ~ | high |
| `cl-strafehud.input.phys_cvars` | Movement-physics input surface (PHYS_* stat reads -> maxspeed/accel/friction/aircontrol/etc.) | ~ | ~ | - | - | - | live | high |
| `cl-strafehud.math.frametime` | Frame-time estimator (weighted-mean of input_timelength, 50ms DP frame split, fps_update window) | ~ | ~ | ~ | - | - | live | high |
| `cl-strafehud.math.friction_replica` | On-ground friction-speed replica (geometric per-tick friction, stopspeed cases) | OK | ~ | - | - | - | live | high |
| `cl-strafehud.math.strafe_angle` | Current strafe angle (velocity yaw vs view yaw, interior-angle wrap, fwd/backwards ±180 shift, v_flipped) | OK | OK | - | - | - | live | high |
| `cl-strafehud.math.zone_angles` | Best/prebest/overturn angle math (ground-friction, airborne, high-speed-landing onground_mode branches) | OK | ~ | - | - | - | live | high |
| `cl-strafehud.math.turn_sidestrafe` | Air side-strafe (A/D turn) detection: strafity, GeomLerp maxspeed/maxaccel blends, timeout_turn retention | OK | OK | OK | - | - | ~ | high |
| `cl-strafehud.math.wturn` | W-turn best-angle (aircontrol power==2 cubic, acos(sqrt(2/3)) asymptote, proper/unrestricted modes) | OK | OK | - | OK | - | ~ | high |
| `cl-strafehud.bar.zones_render` | Strafe-meter bar rendering: zone layout, 360° wrap/mirror, drawfill/progressbar/gradient/soft-gradient styles *(intended)* | OK | OK | - | ~ | - | live | high |
| `cl-strafehud.bar.projection_range` | Projection modes (linear/perspective/panoramic) + HUD angle range resolution (range / range_sidestrafe / FOV modes) | OK | ~ | - | OK | - | live | high |
| `cl-strafehud.indicators.angle_markers` | Angle indicators: current-angle (ratio-colored), best-angle ghost, switch/opposite-switch, dashes + 45° arrows, minspeed gate | OK | OK | - | OK | - | live | high |
| `cl-strafehud.indicators.direction` | Direction indicator (side cap + top/bottom rails showing strafe direction, backwards/opposite inversion) | OK | OK | - | OK | - | live | high |
| `cl-strafehud.extra.slick_detector` | Slick detector (hemispherical trace fan below the player, top/bottom warning bars) | stub | OK | - | ~ | - | ~ | high |
| `cl-strafehud.extra.text_indicators` | Fading text readouts: race start speed, jump height, vertical angle, strafe efficiency (+ unit conversion) | OK | ~ | OK | ~ | - | ~ | high |
| `cl-strafehud.audio.sonar` | Strafe sonar (audible strafe-quality beeps: interval/volume/pitch ramp by strafe ratio) | OK | OK | OK | - | OK | live | high |
| `cl-strafehud.coverage.infra` | Infra/coverage: _mod include shims, skin-export stub, demo sweep for the HUD editor *(intended)* | OK | OK | OK | OK | - | ~ | high |

### `cl-teamradar` (client) — 14 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `cl-teamradar.transform.coords` | World/texcoord/2D coordinate transforms | OK | OK | - | OK | - | live | high |
| `cl-teamradar.background.minimap` | Minimap background image (gfx/<map>_radar / _mini) | OK | OK | - | ~ | - | live | high |
| `cl-teamradar.blip.player` | Team-colored player facing-arrow blip | OK | OK | - | OK | - | live | high |
| `cl-teamradar.blip.enumeration` | entcs player enumeration (all players, local last) | OK | OK | - | OK | - | live | high |
| `cl-teamradar.icon.objective` | Objective radar icons (CTF flags / DOM / KH / waypoints) | OK | OK | - | OK | - | live | high |
| `cl-teamradar.icon.ping` | Ping pulse rings (gfx/teamradar_ping, teamradar_times) | OK | OK | OK | OK | - | live | high |
| `cl-teamradar.links.onslaught` | Onslaught radar links (control-point connection lines) | OK | ~ | OK | OK | - | live | high |
| `cl-teamradar.maximized.map` | Maximized fullscreen tactical radar (m bind) | OK | ~ | OK | ~ | - | live | high |
| `cl-teamradar.maximized.click` | Clickable radar for Onslaught spawn or teleport destinations | OK | ~ | - | ~ | - | live | high |
| `cl-teamradar.cvar.rotation` | Rotation modes (player-aligned vs cardinal lock) | OK | OK | - | OK | - | live | high |
| `cl-teamradar.cvar.zoomscale` | Zoom / scale blend (bigsize vs normalsize) | OK | OK | - | OK | - | live | high |
| `cl-teamradar.cvar.defaults` | cvar registration + defaults / panel enable + visibility | OK | OK | - | OK | - | ~ | high |
| `cl-teamradar.mutator.hook` | TeamRadar_Draw mutator hook (allow extra blips on radar) | MISS | - | - | MISS | - | - | medium |
| `cl-teamradar.cvar.dynamichud` | dynamichud scaling toggle + cvar export to skin | stub | OK | - | MISS | - | DEAD | medium |

### `cl-view` (client) — 28 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `cl-view.zoom.fov_state_machine` | Zoom / FOV state machine (current_viewzoom integration, 0.75 frustum, sensitivity) | OK | OK | OK | OK | - | live | high |
| `cl-view.zoom.spawn_zoom` | Spawn-zoom (zoom out from 1/factor on respawn) | OK | OK | OK | OK | - | live | high |
| `cl-view.zoom.weapon_zoom` | Weapon zoom fold-in (Vortex secondary zoom) | OK | OK | OK | OK | - | live | medium |
| `cl-view.zoom.velocity_zoom` | Velocity-based FOV zoom (cl_velocityzoom) | OK | OK | OK | OK | - | live | high |
| `cl-view.zoom.zoom_scroll` | Zoom-scroll (mousewheel adjusts zoom factor while zooming) | OK | OK | OK | OK | - | live | high |
| `cl-view.zoom.zoomscript_buttonstatus` | Zoomscript auto-zoom + zoom-button management (View_CheckButtonStatus) | OK | OK | - | - | - | live | high |
| `cl-view.chase.event_death_cam` | Event / death chase camera (cl_eventchase_death corpse-settle pull-back) | OK | OK | OK | OK | - | live | high |
| `cl-view.chase.classic_thirdperson` | Classic chase_active third-person + spectator camera (chase_back/up/front/overhead; View_SpectatorCamera) | ~ | OK | OK | OK | - | live | high |
| `cl-view.kick.punch_angle` | Punch-angle recoil kick (view angle, networked + decayed) | OK | OK | OK | OK | - | live | high |
| `cl-view.kick.punch_vector` | Punch-vector recoil kick (view origin) | OK | OK | OK | OK | - | live | high |
| `cl-view.bob.view_bob` | View bobbing (vertical cl_bob, horizontal cl_bob2, fall-bob cl_bobfall) | OK | OK | OK | OK | - | live | high |
| `cl-view.roll.view_roll` | View roll when strafing (cl_rollangle / CalcRoll) | OK | OK | OK | OK | - | live | high |
| `cl-view.tilt.death_idle` | Death tilt (v_deathtilt) + idle view-wave (v_idlescale) | OK | OK | OK | OK | - | live | high |
| `cl-view.smooth.stair_viewheight` | Stair + viewheight smoothing (stairsmoothz glide + viewheightavg blend) *(intended)* | OK | OK | OK | OK | - | ~ | high |
| `cl-view.screen.damage_flash` | Damage red screen flash (HUD_Damage) | OK | OK | OK | OK | - | live | high |
| `cl-view.screen.liquid_tint` | Liquid screen tint (HUD_Contents) | OK | OK | OK | OK | - | live | high |
| `cl-view.screen.frozen_overlay` | Freeze-Tag icy screen overlay (cl_ft HUD_Draw_overlay) | OK | OK | OK | OK | - | live | high |
| `cl-view.screen.postprocess` | GLSL blur/sharpen post-process and blur test *(intended)* | MISS | MISS | MISS | MISS | - | - | high |
| `cl-view.screen.nightvision` | Night-vision tint and noise | OK | OK | OK | OK | - | live | high |
| `cl-view.reticle.zoom_reticle` | Zoom reticle (generic +zoom + weapon scope) | OK | OK | OK | OK | - | live | high |
| `cl-view.viewmodel.sway` | Viewmodel sway (cl_followmodel / cl_leanmodel / cl_bobmodel) | OK | OK | OK | OK | - | live | high |
| `cl-view.demo.free_camera_lockview_ortho` | Demo free camera + lockview + orthoview + viewloc + FPS report | ~ | ~ | ~ | ~ | - | ~ | high |
| `cl-view.zoom.header_defaults` | view.qh bare autocvar defaults gating live zoom paths (spawnzoom / velocityzoom_type / zoomscroll) | - | OK | - | - | - | live | high |
| `cl-view.zoom.unpress_defaults` | Zoom/attack unpress-on-event defaults (cl_unpress_zoom_on_spawn/death/weapon_switch, cl_unpress_attack_on_weapon_switch) | ~ | ~ | - | - | - | ~ | medium |
| `cl-view.demo.camera_cvars` | Demo free-camera cvar family (camera_enable/free/chase_smoothly/speed_*/look_*/mouse_threshold/reset) | MISS | MISS | MISS | MISS | - | - | high |
| `cl-view.spawn.event_effects` | Spawn-event presentation (spawn flash particles + spawn sound + spawn-point idle glow, cl_spawn_* gating) | ~ | ~ | ~ | ~ | ~ | live | high |
| `cl-view.race.cptimes_defaults` | Race checkpoint-times display defaults (cl_race_cptimes_* AUTOCVAR_SAVE set + splits-console print) | ~ | ~ | - | ~ | - | live | medium |
| `cl-view.client.welcome_dialog` | Welcome dialog on connect (cl_welcome gate + Welcome_Message_Show_Try) | ~ | OK | - | ~ | - | live | high |

### `cl-viewmodel` (client) — 13 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `cl-viewmodel.model.select_and_rig` | View-model entity + model select (invisible-hand vs full-rig), rebuild on weapon change | OK | OK | OK | OK | - | live | high |
| `cl-viewmodel.anim.networked_frames` | Networked weapon fire/idle/reload animation (wframe temp entity) | OK | MISS | MISS | ~ | - | live | medium |
| `cl-viewmodel.model.skin_colormap_glow_effects` | Per-child skin / team colormap / glowmod / EF_NODEPTHTEST / cheap effects mask | OK | ~ | - | ~ | - | live | high |
| `cl-viewmodel.alpha.viewmodel_alpha` | View-model alpha (cl_viewmodel_alpha / _alpha_min, vehicle/dead/intermission/chase hide) | OK | OK | - | OK | - | live | high |
| `cl-viewmodel.align.gunalign_gunoffset` | Shot-origin alignment (cl_gunalign 1/2/3/4) + cl_gunoffset | OK | ~ | - | OK | - | live | high |
| `cl-viewmodel.switch.raise_lower_anim` | Weapon-switch raise/lower (barrel-tip -90*f*f keyed to switchdelay/weapon_nextthink) *(intended)* | ~ | ~ | ~ | ~ | - | live | high |
| `cl-viewmodel.sway.followmodel` | Follow sway (velocity lowpass -> acceleration offset, cl_followmodel) | OK | OK | OK | OK | - | live | high |
| `cl-viewmodel.sway.leanmodel` | Lean sway (view-angle highpass -> gun roll/pitch, cl_leanmodel) | OK | OK | OK | OK | - | live | high |
| `cl-viewmodel.sway.bobmodel` | Bob sway (ground-walk sinusoidal side/up sway, cl_bobmodel) | OK | OK | OK | OK | - | live | high |
| `cl-viewmodel.muzzle.flash_particle` | Muzzle flash particle (local firer + remote world-space, m_muzzleeffect) | OK | ~ | OK | OK | - | live | medium |
| `cl-viewmodel.muzzle.flash_model` | Muzzle flash MODEL (spinning flash md3 for the 7 m_muzzlemodel weapons: Devastator/MineLayer/OkRPC=flash.md3, Machinegun/Shotgun/OkHMG/OkMachinegun=uziflash.md3) | OK | OK | OK | OK | - | live | high |
| `cl-viewmodel.casings.brass_eject` | Brass / shell casings (eject, tumble, bounce, fade, bounce sounds) *(intended)* | OK | ~ | OK | ~ | OK | live | high |
| `cl-viewmodel.model.wr_viewmodel_override` | Per-weapon view-model override (wr_viewmodel, e.g. Tuba note-driven model swap) | OK | OK | OK | OK | - | live | high |

### `damage-pipeline` (damage) — 9 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `damage-pipeline.dispatch.front_gate` | Damage() front gate + hook/sound same-team rule + always-lethal kill/teamchange | OK | OK | - | - | - | live | high |
| `damage-pipeline.dispatch.teamplay_shaping` | Teamplay friendly-fire / mirror-damage shaping (teamplay_mode 1-4, virtual ff/mirror) | OK | OK | OK | - | OK | live | high |
| `damage-pipeline.dispatch.factors_selfdamage` | Global weapon damage/force factors + self-damage percent + Damage_Calculate hook | OK | OK | - | - | - | live | high |
| `damage-pipeline.resource.healtharmor_split` | Armor<->health damage split (healtharmor_applydamage, drown/armorpierce bypass) | OK | OK | - | - | - | live | high |
| `damage-pipeline.resource.player_damage` | PlayerDamage resource math: handicap, spawnshield, godmode tab, regen pause, pusher window, pain/death feedback | OK | OK | OK | OK | OK | live | high |
| `damage-pipeline.knockback.calcpush` | Knockback application + damage_explosion_calcpush momentum projection *(intended)* | OK | OK | - | - | - | live | high |
| `damage-pipeline.radius.splash` | RadiusDamage: core->edge falloff, falloff-scaled knockback, per-axis force shaping, through-floor LOS, warpzone propagation, HITTYPE_SPLASH tagging | OK | OK | - | - | - | live | high |
| `damage-pipeline.fire.burning` | Burning damage-over-time (Fire_AddDamage / Fire_ApplyDamage + fire transfer) | OK | OK | OK | - | - | live | high |
| `damage-pipeline.heal.dispatcher` | Central Heal() dispatcher (game_stopped/frozen/dead gate + event_heal route + limit cap) | OK | - | - | - | - | live | high |

### `cl-damageeffects` (effect) — 11 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `cl-damageeffects.net.sv_broadcast` | Server DamageInfo broadcast entity (ENT_CLIENT_DAMAGEINFO encode: deathtype/org/dmg/radius/edge/force/species/colormap/silent) *(intended)* | MISS | MISS | - | - | - | - | high |
| `cl-damageeffects.net.cl_radius_apply` | Client-side blast application: radius falloff, damageforcescale push + event_damage on client entities (gibs/casings/ragdolls) | MISS | MISS | - | MISS | - | - | high |
| `cl-damageeffects.impact.weapon_dispatch` | Per-weapon impact FX dispatch (wr_impacteffect at the blast point: backoff traceline, sky gate, hitscan-hit-player gate) *(intended)* | ~ | OK | - | ~ | ~ | live | high |
| `cl-damageeffects.impact.team_tint` | Team/player-color tinted impact particles (particles_colormin/max from the DamageInfo colormap byte) | MISS | MISS | - | MISS | - | - | medium |
| `cl-damageeffects.fx.attached_damage` | Attached damage effects on hit entities (cl_damageeffect and nearest eligible posed bone) *(design difference; open presentation)* | OK | OK | - | ~ | - | live | high |
| `cl-damageeffects.fx.attached_lifecycle` | Attached damage-effect lifecycle (cadence, distribution, corpse and respawn cleanup, first-person view gates) *(intended)* | OK | OK | OK | OK | - | live | high |
| `cl-damageeffects.fx.species_bleed` | Species blood-type prefix + WEP_FLAG_BLEED gate (alien_/robot_/animal_ blood; objects don't bleed) | OK | OK | - | ~ | - | live | high |
| `cl-damageeffects.impact.vehicle_fx` | Vehicle weapon impact FX switch (DEATH_VH_*: per-weapon impact sounds + particles, raptor shellfrag toss) | ~ | ~ | - | ~ | MISS | ~ | high |
| `cl-damageeffects.impact.turret_fx` | Turret weapon impact FX switch (DEATH_TURRET_*: per-turret impact sounds + particles) | MISS | MISS | - | MISS | MISS | - | high |
| `cl-damageeffects.fx.model_gib_limit` | Ordinary model-gib live limit and oldest-first eviction *(intended)* | OK | ~ | OK | OK | - | live | high |
| `cl-damageeffects.fx.corpse_splash` | Player and corpse hit effects, model launch and particle substitutions | ~ | ~ | ~ | ~ | MISS | live | high |

### `fx-effectinfo` (effect) — 18 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `fx-effectinfo.registry.named_effects` | Named EFFECT_* registry (all.inc) + ordering/content hash | OK | OK | - | - | - | live | high |
| `fx-effectinfo.emit.send_effect` | Server effect emission (Send_Effect / pointparticles / trailparticles) | OK | OK | - | - | - | live | high |
| `fx-effectinfo.emit.by_name_fallback` | Emit by effectinfo name (Send_Effect_) — registry scan + engine fallback | OK | OK | - | ~ | - | ~ | medium |
| `fx-effectinfo.net.wire_protocol` | EFF_NET_* wire encoding/decoding (Net_Write_Effect / net_effect) | OK | OK | - | - | - | live | high |
| `fx-effectinfo.catalog.file` | Original-mode effectinfo catalog and particle atlas | OK | OK | - | OK | - | live | high |
| `fx-effectinfo.parser.tokenize` | effectinfo.txt parser/tokeniser (CL_Particles_ParseEffectInfo) | OK | OK | - | OK | - | live | medium |
| `fx-effectinfo.model.baseline_defaults` | Parsed emitter data model + baseline defaults (particleeffectinfo_t) | OK | OK | - | OK | - | live | high |
| `fx-effectinfo.parser.keywords` | Keyword parsing (type/color/size/alpha/jitter/light/stain/water/...) *(intended)* | OK | OK | - | OK | - | live | high |
| `fx-effectinfo.consume.lifetime_fallback` | Heuristic fallback lifetime/burst (BuildFromInfo) approximation *(intended)* | OK | ~ | ~ | ~ | - | ~ | medium |
| `fx-effectinfo.emit.te_builtins` | te_* temp-effect builtins (te_explosion/te_spark/te_gunshot/te_blood/lightningarc/beams) | OK | ? | - | ~ | - | ~ | low |
| `fx-effectinfo.catalog.generator_sources` | QC generator sources (.inc DEF/SUB blocks) + dumpeffectinfo regen tool *(intended)* | MISS | - | - | - | - | - | high |
| `fx-effectinfo.catalog.vehicles` | Vehicle effect blocks (spiderbot/wakizashi/raptor muzzle+trail+impact+explode) | OK | OK | - | ~ | - | ~ | high |
| `fx-effectinfo.catalog.nades` | Nade effect blocks (nade_<color> trail / _burn / _explode) | OK | OK | - | ~ | - | DEAD | high |
| `fx-effectinfo.catalog.gentle_variants` | Gentle/particle-gib replacement blocks (damage_hit/damage_dissolve name families) | OK | OK | - | ~ | - | live | high |
| `fx-effectinfo.catalog.onslaught` | Onslaught generator gib blocks (gib_explode / gib_flame) | OK | OK | - | OK | - | DEAD | high |
| `fx-effectinfo.catalog.buffs` | Buff relic_* effect blocks (17 legacy relic visuals) | OK | OK | - | OK | - | - | high |
| `fx-effectinfo.catalog.instagib` | Instagib rocketminsta_laser_* trail blocks | OK | OK | - | OK | - | live | high |
| `fx-effectinfo.catalog.ctf` | CTF flag effect blocks (flag_touch / pass trail / cap burst) | OK | OK | - | ~ | - | ~ | high |

### `assault` (gametype) — 19 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `assault.mode.registration` | Assault gametype registration + scoring layout (always red vs blue) | OK | OK | - | - | - | live | high |
| `assault.objective.graph_spawn` | Objective-chain entities + deferred (spawn-order-independent) target resolution | OK | OK | - | - | - | live | high |
| `assault.objective.inactive_sentinel` | Objective inactive-health sentinel (ASSAULT_VALUE_INACTIVE) | OK | OK | - | - | - | live | high |
| `assault.objective.activate` | Activate an objective (set health, arm its decreasers + destructibles) | OK | OK | - | OK | - | live | high |
| `assault.objective.decrease_and_score` | Shoot destructible -> decreaser fires -> strip objective health + award score | OK | OK | OK | - | - | live | high |
| `assault.objective.advance_chain` | Destroyed objective fires its target -> activate next objective or roundend | OK | OK | - | - | - | live | high |
| `assault.win.attacker_round` | Attackers destroy the core -> win the round (666 team-score sentinel) | OK | OK | OK | - | - | live | high |
| `assault.round.second_round` | Two-round match: swap roles, re-clock round 2 to round 1 destruction time, 5s round delay | OK | OK | OK | - | - | live | high |
| `assault.win.defender_timelimit` | Timelimit elapses without core destruction -> defenders win | OK | OK | OK | - | - | live | high |
| `assault.spawns.attacker_defender` | info_player_attacker / info_player_defender team spawn points | OK | OK | - | - | - | live | high |
| `assault.notify.role_and_destroyed` | Per-spawn attacking/defending centerprint + objective-destroyed broadcast | OK | OK | - | OK | - | live | high |
| `assault.objective.waypoint_sprite` | Objective waypoint sprites (defend/push/destroy + health bars + radar icon) | OK | OK | OK | OK | - | live | high |
| `assault.mapobject.assault_wall` | func_assault_wall cosmetic wall toggles with its objective's health | OK | OK | ~ | OK | - | live | high |
| `assault.turret.roundstart_teamswap` | Roundstart turret team-swap + respawn (and as TurretSpawn team seeding) | OK | OK | - | - | - | live | high |
| `assault.bot.objective_role` | Bot offense/defense role rating the assault destructible objectives | OK | OK | OK | - | - | live | high |
| `assault.spawns.objective_evalfunc` | Objective-aware spawn-point eval (deprioritize spots near inactive/destroyed objectives) | OK | OK | - | - | - | live | high |
| `assault.objective.destructible_heal` | func_assault_destructible regen / event_heal (walls can be healed back up + sprite update) | OK | OK | - | ~ | - | live | high |
| `assault.config.warmup_incompatible` | Assault disables warmup + ready-restart-after-countdown (ReadLevelCvars) | OK | OK | - | - | - | live | high |
| `assault.notify.play_hitsound` | Force a hitsound when shelling a func_assault_destructible (PlayHitsound hook) | OK | OK | - | - | OK | live | high |

### `clanarena` (gametype) — 28 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `clanarena.round.lifecycle` | Round wait/countdown/live/end-delay state machine | OK | OK | OK | - | - | live | high |
| `clanarena.round.checkteams_gate` | Round-start gate (every active team has a live player) | OK | - | OK | - | - | live | high |
| `clanarena.round.resolution` | Round resolution: last team alive wins, banks a round point | OK | OK | OK | - | - | live | high |
| `clanarena.round.timelimit_stalemate` | Round time limit + stalemate prevention (survivors/health) | OK | OK | OK | - | - | live | high |
| `clanarena.damage.no_friendly_fire` | No friendly-fire / self / fall damage; no mirror damage | OK | OK | - | - | - | live | high |
| `clanarena.scoring.no_frags_for_kill` | Kills award no individual frags | OK | OK | - | - | - | live | medium |
| `clanarena.scoring.damage2score` | Damage dealt accrues to scoreboard SCORE (g_ca_damage2score) | OK | OK | - | - | - | live | high |
| `clanarena.loadout.start_items` | CA start loadout: 200 health / 200 armor / full ammo | OK | OK | - | - | - | live | high |
| `clanarena.loadout.weapon_arena` | Spawn with all weapons (g_ca_weaponarena 'most') | OK | OK | - | - | - | live | high |
| `clanarena.items.no_pickups` | No item pickups on the map (FilterItem) *(intended)* | OK | OK | - | - | - | live | high |
| `clanarena.scoring.match_limit` | Match win: round limit (10) / lead limit (6) | OK | OK | OK | - | - | live | high |
| `clanarena.hud.alive_counts` | Per-team alive counts (REDALIVE..PINKALIVE) + mod-icons panel | OK | OK | OK | OK | - | live | high |
| `clanarena.hud.eliminated_greyout` | Eliminated-player scoreboard grey-out | OK | - | OK | OK | - | live | high |
| `clanarena.spectate.enemies_rule` | Spectate-enemies anti-ghost rule (g_ca_spectate_enemies) | OK | OK | - | - | - | live | high |
| `clanarena.notify.round_outcome` | Round-win / tied / over notifications + 'You are now alone' | OK | - | OK | OK | - | live | high |
| `clanarena.round.grace_no_fire` | Round-start grace period: weapons cannot be fired | OK | OK | OK | - | - | live | high |
| `clanarena.scoring.per_round_award` | Per-player ROUNDS_PL award at round start | OK | OK | OK | - | - | live | high |
| `clanarena.spawn.no_regen_forced_spectate` | No health/armor regen; dead players forced to spectate (no respawn calc) | OK | - | OK | - | - | live | high |
| `clanarena.join.late_join_observer` | Late joiner forced to Observer until next round (+ CA join-late info) | OK | - | OK | OK | - | live | high |
| `clanarena.spawn.forbid_throw_weapon` | Cannot drop/throw the current weapon | OK | - | - | - | - | live | high |
| `clanarena.notify.alone_on_leave` | 'You are now alone!' on teammate disconnect / make-observer | OK | - | OK | OK | - | live | medium |
| `clanarena.matchend.restore_status` | Restore spectator/team status before final scores (MatchEnd_BeforeScores) | ~ | - | - | - | - | live | medium |
| `clanarena.spectate.force_spectate_cmd` | CA spectate-command force + 'leave the game' notice (ClientCommand_Spectate) | OK | - | - | OK | - | live | high |
| `clanarena.cmd.shuffleteams_reset` | shuffleteams reschedules to next round (SV_ParseServerCommand) | OK | - | OK | - | - | live | high |
| `clanarena.lifecycle.reset_map` | Round reset re-transmutes in-game players and re-allows spawn (reset_map_players/global) | OK | - | - | - | - | live | high |
| `clanarena.spawn.ingame_status_decimal_reset` | PlayerSpawn: set INGAME_STATUS_JOINED + reset damage2score decimal on game restart | OK | - | OK | - | - | live | high |
| `clanarena.hud.spectator_info_messages` | Suppress the join hint for Clan Arena scoreboard participants observing | OK | - | - | OK | - | live | high |
| `clanarena.mapinfo.always_supported` | Map-support heuristic (m_isAlwaysSupported: spawnpoints>=8 && diameter>3250) | OK | OK | - | - | - | live | high |

### `ctf` (gametype) — 28 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `ctf.flag.spawn_setup` | Flag entity spawn + model/skin/bbox setup | OK | OK | OK | ~ | - | live | high |
| `ctf.flag.touch_dispatch` | On-touch state dispatch (pickup/capture/return) | OK | - | OK | - | - | live | high |
| `ctf.flag.pickup` | Flag pickup (base + dropped) | OK | OK | OK | - | OK | live | high |
| `ctf.flag.capture` | Flag capture + caps/score/captime | OK | OK | OK | ~ | OK | live | high |
| `ctf.flag.return` | Flag return (player + auto/timeout) | OK | OK | OK | OK | OK | live | high |
| `ctf.flag.drop_on_death` | Carrier death drops the flag | OK | OK | OK | - | OK | live | high |
| `ctf.flag.fckill_score` | Flag-carrier-kill score + damage/force factors + auto-helpme | OK | OK | OK | OK | - | live | high |
| `ctf.flag.throw` | Throw the flag (+use, g_ctf_throw) | OK | OK | OK | - | OK | live | high |
| `ctf.flag.pass` | Pass the flag to a teammate (g_ctf_pass) *(intended)* | ~ | OK | OK | - | OK | live | high |
| `ctf.flag.remove_player` | Carrier disconnect/observe/portal/vehicle drops flag | OK | - | - | - | - | live | high |
| `ctf.flag.think_dropped` | Dropped-flag think (landtime, auto-return timer, float, capture-radius) | ~ | OK | OK | - | - | live | high |
| `ctf.capture_shield` | Capture shield (block worst players from the flag) | OK | OK | OK | OK | - | live | high |
| `ctf.stalemate` | Stalemate carrier reveal | OK | OK | OK | OK | - | live | high |
| `ctf.hud.modicons` | Flag-status mod-icon HUD (OBJECTIVE_STATUS) | OK | OK | OK | OK | - | live | high |
| `ctf.hud.waypoints` | Flag waypoint sprites (base / dropped / carrier) | OK | ~ | OK | ~ | - | live | high |
| `ctf.scoring_rules` | Scoreboard columns + team caps-primary ranking | OK | OK | - | OK | - | live | high |
| `ctf.win_condition` | Capture limit + lead limit win | OK | OK | OK | - | - | live | high |
| `ctf.oneflag` | One-flag CTF (neutral carriable flag) | OK | OK | - | - | - | ~ | medium |
| `ctf.bot_role` | Bot CTF role (grab/cap/return/escort) | OK | OK | OK | - | - | live | high |
| `ctf.flag.map_reset` | Flag reset on map/round reset (ctf_Reset) | OK | - | - | - | - | live | high |
| `ctf.match_end_freeze` | Freeze dropped/passing flags at match end (MatchEnd) | OK | - | - | - | - | live | high |
| `ctf.abort_speedrun` | Return carried flag on speedrun abort (AbortSpeedrun) | OK | - | - | - | - | live | high |
| `ctf.drop_special_items` | Throw flag on DropSpecialItems | OK | - | - | - | - | live | high |
| `ctf.logdeath_itemcode` | Flag-carrier death-log item code ('F') | OK | - | - | - | - | live | high |
| `ctf.ignore_frags` | Suppress frag score in CTF (g_ctf_ignore_frags) | OK | OK | - | - | - | live | high |
| `ctf.leaderboard` | Persistent cross-session capture leaderboard (g_ctf_leaderboard) | ~ | - | - | OK | - | live | medium |
| `ctf.followfc_superspec` | Superspectate 'follow flag carrier' command (followfc) | OK | - | - | - | - | live | high |
| `ctf.prefer_score_clear` | Prefer player-score on clear (PreferPlayerScore_Clear) | OK | - | - | - | - | live | high |

### `cts` (gametype) — 21 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `cts.mode.register_latch` | CTS gametype registration + qualifying/independent-players latch | OK | OK | - | - | - | live | high |
| `cts.score.rules` | Score rules: no SP_SCORE; rank by fastest time (qualifying) or laps/time/fastest | OK | OK | - | - | - | live | high |
| `cts.timing.start_finish` | Stage run timer: start-timer stamps, stop-timer folds fastest time | OK | OK | OK | - | - | live | high |
| `cts.timer.spawnfuncs` | target_startTimer / target_stopTimer trigger entities | OK | OK | - | - | - | live | high |
| `cts.checkpoints.intermediate` | Intermediate / defrag checkpoints (ordered progress + per-cp records) | ~ | ~ | OK | MISS | - | live | high |
| `cts.records.persistence` | Per-map top-99 CTS record ranking + UID gating | OK | OK | - | - | - | live | high |
| `cts.records.notifications` | Finish + record-set/improved/broken notifications (INFO_RACE_*) + medal status | OK | OK | OK | OK | - | live | high |
| `cts.finish.kill_delay_retract` | Finish kill-delay re-teleport to start (anti-speed-carry) | OK | OK | OK | - | - | live | high |
| `cts.physics.force_keyboard` | Forced keyboard movement quantization + race_movetime accumulator (PlayerPhysics) | OK | OK | OK | - | - | live | high |
| `cts.combat.selfdamage` | Self-damage + fall-damage suppression (g_cts_selfdamage) | OK | OK | - | - | - | live | high |
| `cts.weapon.shotgun_only` | Shotgun-only loadout (WantWeapon -> WEP_SHOTGUN) | OK | OK | - | - | - | live | high |
| `cts.weapon.no_drop_throw` | Forbid weapon throw + drop | OK | - | - | - | - | live | high |
| `cts.death.respawn_rules` | Death rules: force respawn, instant CTS respawn, abandon run, remove projectiles | OK | OK | OK | - | - | live | high |
| `cts.items.loot_filter` | Loot/monster-item filtering (g_cts_drop_monster_items) | OK | OK | - | - | - | live | high |
| `cts.speedaward` | Speed award (per-round + all-time best speed) + name DB update | OK | OK | OK | OK | - | live | high |
| `cts.hud.race_timer` | Race timer HUD: running clock, checkpoint splits, anticipation, medal, PB/server-best | OK | OK | OK | ~ | - | live | high |
| `cts.hud.cl_panel_gating` | CTS client panel gating (physics/strafe/race-timer shown, score/accuracy/item-stats hidden) | OK | - | - | ~ | - | live | high |
| `cts.map_reset` | Map reset: clear in-memory records, place event-log, qualifying==2 collapse | ~ | - | - | - | - | live | high |
| `cts.lifecycle.prepare_player` | Per-player race bookkeeping on connect/spawn/observe (race_PreparePlayer / race_RetractPlayer / race_checkpoint=-1 / out-of-game flag) | ~ | ~ | - | - | - | ~ | high |
| `cts.bot.role` | CTS bot role: route to the next race-checkpoint waypoint (havocbot_role_cts) | ~ | OK | - | - | - | live | medium |
| `cts.records.getrecords` | Paged map-record report of rank-one CTS times and holders | OK | OK | - | OK | - | live | high |

### `deathmatch` (gametype) — 9 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `deathmatch.gametype.identity` | Deathmatch gametype registration & identity (dm, FFA, USEPOINTS|PREFERRED) | OK | OK | - | - | - | live | high |
| `deathmatch.scoring.frag_matrix` | FFA frag scoring: enemy +1, suicide -1, world/accident death -1 | OK | OK | - | - | - | live | high |
| `deathmatch.scoring.teamkill_punishing` | Teamkill punishment escalation (g_teamkill_punishing) | ~ | ~ | - | - | - | - | medium |
| `deathmatch.winconditions.fraglimit` | Point/frag limit ends the match (topscore >= pointlimit) *(intended)* | OK | OK | OK | - | - | live | high |
| `deathmatch.winconditions.leadlimit` | Lead limit ends the match (topscore - secondscore >= leadlimit) + leadlimit_and_fraglimit | OK | OK | OK | - | - | live | high |
| `deathmatch.winconditions.timelimit_overtime` | Time limit + overtime/sudden-death (tie at limit keeps playing) | OK | OK | OK | OK | - | live | high |
| `deathmatch.announce.frags_remaining` | Remaining-frags announcer (1/2/3 frags left) | OK | OK | OK | OK | OK | live | high |
| `deathmatch.respawn.timing` | Respawn delay scheduling (calculate_player_respawn_time) | OK | OK | OK | OK | OK | live | high |
| `deathmatch.menu.describe` | Menu gametype-description prose (free-for-all help text shown in the gametype picker) | - | - | - | OK | - | ~ | high |

### `domination` (gametype) — 14 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `domination.init.mutator_limits` | Mutator register + score/lead limit + team setup | OK | OK | OK | - | - | live | high |
| `domination.controlpoint.spawn` | Control point entity spawn (dom_controlpoint / team_dom_point) | OK | OK | OK | OK | - | live | high |
| `domination.capture.touch` | Instant point capture on player touch | OK | OK | OK | - | - | live | high |
| `domination.scoring.tick` | Periodic per-point score tick to owning team + capturer | OK | OK | OK | - | - | live | high |
| `domination.scoring.rules` | Scoreboard rules (ticks/caps/takes columns + sort keys) | OK | OK | - | OK | - | live | high |
| `domination.win.pointlimit` | Point-limit / lead-limit team win (tick variant) | OK | OK | OK | - | - | live | high |
| `domination.roundbased.win` | Round-based variant: own-all-points round win + caps | OK | OK | OK | OK | - | live | high |
| `domination.capture.audio` | Capture sound (DOM_CLAIM) + narration (play2all) | OK | OK | - | - | OK | live | high |
| `domination.capture.notification` | Capture info notification (INFO_DOMINATION_CAPTURE_TIME) *(intended)* | OK | OK | - | OK | - | live | high |
| `domination.hud.modicons_pps` | Mod-icons HUD: per-team points-per-second bars | OK | OK | - | OK | - | live | high |
| `domination.waypoint.sprite` | Control point waypoint sprite + radar team color/ping | OK | OK | OK | OK | - | live | high |
| `domination.bot.role` | Bot role: rate unclaimed/contested/enemy control points | OK | OK | - | - | - | live | high |
| `domination.capture.eventlog` | Server gamelog echo on capture (dom_EventLog :dom:taken:) | OK | OK | - | - | - | live | high |
| `domination.roundbased.player_blocked` | Round-based player-blocking on round start / spawn *(intended)* | OK | - | OK | - | - | live | high |

### `duel` (gametype) — 9 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `duel.identity.registration` | Duel gametype identity + default match limits | OK | OK | - | - | - | live | high |
| `duel.rules.player_limit_2` | Hard 1v1 player limit (GetPlayerLimit -> 2) | OK | OK | - | - | - | live | high |
| `duel.items.powerup_filter` | Powerup item filter (block powerups unless g_duel_with_powerups) | OK | OK | - | - | - | live | high |
| `duel.scoring.frag_matrix` | Deathmatch frag/obituary scoring matrix (inherited) | OK | OK | OK | - | - | live | high |
| `duel.rules.match_end_latch` | End-of-match frag-limit latch + winner resolution + 'N frags left' announcer | OK | OK | OK | - | - | live | high |
| `duel.timing.respawn_delay` | Respawn delay (effective 2s via global fallback) | OK | OK | OK | - | - | live | high |
| `duel.presentation.duel_title` | Duel 'playerA vs playerB' centerprint title | OK | OK | OK | OK | - | live | high |
| `duel.presentation.forced_colors` | Forced enemy player colors in 1v1 (cl_forceplayercolors / suppress unique) | OK | OK | - | OK | - | live | high |
| `duel.mapsupport.gating` | Map-support gating (diameter < 3250; force duel on DM maps) | OK | OK | - | - | - | live | high |

### `freezetag` (gametype) — 30 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `freezetag.mode.registration` | Mode registration: teams, team-spawns, score/lead limits, FT scoring fields | OK | ~ | - | - | - | live | high |
| `freezetag.freeze.apply` | Freeze a fragged player (ice, HP=1, status effect, score matrix) | OK | OK | OK | OK | - | live | high |
| `freezetag.freeze.weaponlock` | Frozen player cannot fire weapons | OK | - | OK | - | - | live | high |
| `freezetag.freeze.movementlock` | Frozen player cannot move or jump | OK | OK | OK | - | - | live | high |
| `freezetag.unfreeze` | Unfreeze: clear ice state, restore health, re-enable | OK | OK | OK | OK | - | live | high |
| `freezetag.revive.manual` | Manual revive by nearby teammate (range geometry + progress) | OK | OK | OK | - | - | live | high |
| `freezetag.revive.score` | Revive scoring (reviver +1, FREEZETAG_REVIVALS +1; revived player return) | OK | ~ | OK | - | - | live | high |
| `freezetag.revive.time_to_score` | Time-to-score revive model (revive_time_to_score / speed_t2s) | OK | OK | OK | - | - | live | high |
| `freezetag.revive.auto_timeout` | Auto-thaw after g_freezetag_frozen_maxtime | OK | OK | OK | - | - | live | high |
| `freezetag.revive.auto_reducible` | Hitting a frozen enemy speeds their auto-thaw (revive_auto_reducible) | OK | OK | OK | - | - | live | high |
| `freezetag.damage.frozen_invuln` | Frozen players take 0 health damage + g_frozen_force knockback scaling | OK | OK | - | - | - | live | high |
| `freezetag.damage.softkill_void` | Frozen void/lava soft-kill teleport + g_frozen_damage_trigger; fall/nade revive | OK | OK | - | OK | OK | live | high |
| `freezetag.revive.spawnshield` | Post-revive spawn shield (g_freezetag_revive_spawnshield) | OK | OK | OK | - | - | live | high |
| `freezetag.round.flow` | Round handler flow (warmup/countdown/round/end-delay, ROUND_OVER tie) | OK | ~ | OK | - | - | live | high |
| `freezetag.win.roundlimit` | Match win on round/lead limit (ST_FT_ROUNDS) | OK | ~ | OK | - | - | live | high |
| `freezetag.startitems` | FT start loadout (g_ft_start_health/armor/ammo) | OK | OK | - | - | - | live | high |
| `freezetag.eliminated.net` | Networked alive counts + eliminated set (mod icons, scoreboard grey-out) | OK | OK | OK | OK | - | live | high |
| `freezetag.presentation.ice_model` | Ice entity model over frozen players (MDL_ICE, frames 0–20, team color) | OK | ~ | - | ~ | - | live | high |
| `freezetag.presentation.waypoints` | Frozen / Reviving waypoint sprites over frozen players | OK | OK | OK | OK | - | live | high |
| `freezetag.presentation.notifications` | Freeze/revive/self/auto-revive/spawn-late notifications (+ sounds) | OK | OK | OK | OK | OK | live | high |
| `freezetag.presentation.overlay_eventchase` | Full-screen frozen overlay tint + cl_eventchase_frozen cam + damage HUD | OK | OK | OK | ~ | - | live | high |
| `freezetag.bots` | Bot freeing / offense roles + frozen-target gating | OK | OK | OK | - | - | live | high |
| `freezetag.spawn.late` | Mid-round joiner spawns AS FROZEN (PlayerSpawn → SPAWN_LATE) | OK | OK | OK | OK | - | live | high |
| `freezetag.scoring.no_kill_frags` | No engine frags counted on a freeze-kill (GiveFragsForKill → 0) | OK | OK | - | - | - | live | high |
| `freezetag.weaponarena` | FT default weapon arena (g_freezetag_weaponarena 'most_available') | OK | OK | - | - | - | live | high |
| `freezetag.frozen.no_regen` | Frozen players don't regen health/armor (PlayerRegen) | OK | - | - | - | - | live | high |
| `freezetag.frozen.no_pickup` | Frozen players can't pick up items or buffs (ItemTouch/BuffTouch) | OK | - | - | - | - | live | high |
| `freezetag.frags.center_message` | Per-freeze choice centerprint ('You froze X' / 'You were frozen by Y') | OK | OK | OK | OK | - | live | high |
| `freezetag.presentation.frozen_anim` | Frozen-pose player animation (PlayerAnim ANIMSTATE_FROZEN) | OK | - | - | OK | - | live | high |
| `freezetag.notif.alone` | Last-survivor 'You are now alone!' centerprint (LastPlayerForTeam → CENTER_ALONE) | OK | OK | OK | OK | - | live | high |

### `invasion` (gametype) — 29 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `invasion.core.registration` | Invasion gametype registration + init (g_monsters forced, score limit) | OK | OK | - | - | - | live | high |
| `invasion.core.type_variants` | g_invasion_type ROUND/HUNT/STAGE variant selection | OK | OK | - | - | - | live | high |
| `invasion.score.rules` | Score rules: KILLS as sole primary-sort column, score disabled | OK | OK | - | OK | - | live | high |
| `invasion.score.monster_kill` | Killing a monster banks +1 KILLS to the player (MonsterDies) | OK | OK | OK | - | - | live | high |
| `invasion.wave.size_formula` | Per-round monster count: round(max(base, base*round*0.5)) | OK | OK | - | - | - | live | high |
| `invasion.wave.monster_skill` | Monster skill = round + max(1, players*0.3) | OK | OK | OK | - | - | live | high |
| `invasion.spawn.objective_entities` | Map spawnfuncs: invasion_spawnpoint / invasion_wave / target_invasion_roundend | OK | OK | - | - | - | live | high |
| `invasion.spawn.wave_entity_resolution` | Wave-entity lookup: exact .cnt else highest .cnt <= round | OK | - | - | - | - | live | high |
| `invasion.spawn.pick_monster` | Random monster pick with type filters + zombies-only | OK | OK | - | - | - | live | high |
| `invasion.spawn.pick_spawnpoint` | Spawn-point selection with recent-use de-weighting | OK | OK | OK | - | - | live | high |
| `invasion.spawn.chosen_monster` | Spawn the chosen monster (NORESPAWN, skill stamp, think wired) | OK | OK | OK | - | - | live | high |
| `invasion.round.fill_and_win_round_mode` | ROUND fill loop + round-cleared advance / win | OK | OK | OK | OK | - | live | high |
| `invasion.round.handler_wiring` | ROUND round_handler with Invasion-specific start/end/players callbacks + warmup + timelimit | OK | OK | OK | - | - | live | high |
| `invasion.round.timeout` | Round timeout: remove monsters, ROUND_OVER, restart round | OK | OK | OK | OK | - | live | high |
| `invasion.win.hunt` | HUNT: win when all placed monsters are cleared | OK | - | OK | - | - | live | high |
| `invasion.win.stage` | STAGE: win when >=70% of players reach the level-end trigger | OK | OK | OK | - | - | live | high |
| `invasion.win.point_limit` | Match end on banked-kill point limit (default 50) | OK | OK | OK | - | - | live | high |
| `invasion.rules.no_pvp_damage` | Player-vs-player damage cancelled (Damage_Calculate) | OK | OK | - | - | - | live | high |
| `invasion.rules.no_regen` | No health/armor regeneration (PlayerRegen) | OK | - | - | - | - | live | high |
| `invasion.rules.start_items` | ROUND start items: 200 health / 200 armor (SetStartItems) | OK | OK | - | - | - | live | high |
| `invasion.bots.targeting` | Bot/monster targeting: monsters won't target players, bots only attack monsters *(intended)* | OK | OK | - | - | - | live | high |
| `invasion.notify.supermonster` | Center-print when a supermonster arrives (CENTER_INVASION_SUPERMONSTER) | OK | - | - | OK | - | live | high |
| `invasion.notify.round_end` | Round-over / round-winner center+info prints | OK | - | - | OK | - | live | high |
| `invasion.hud.monster_count` | HUD monsters_total / monsters_killed publish (SV_StartFrame) | OK | OK | OK | OK | - | live | high |
| `invasion.client.hide_item_stats` | Scoreboard hides the item-stats panel in Invasion (cl_invasion) | OK | - | - | OK | - | live | high |
| `invasion.rules.accuracy_target_valid` | Monsters are valid weapon-accuracy targets (AccuracyTargetValid → MUT_ACCADD_INVALID) | OK | OK | - | - | - | live | high |
| `invasion.rules.mob_command_guards` | Block spawnmob/butcher console commands during an invasion (AllowMobSpawning / AllowMobButcher) | OK | OK | - | - | - | live | high |
| `invasion.spawn.monster_hitcontents` | MonsterSpawn stamps monster dphitcontentsmask = SOLID|BODY|BOTCLIP|MONSTERCLIP | OK | OK | - | - | - | live | high |
| `invasion.client.point_limit_menu` | Map-config point-limit menu slider (m_configuremenu 50..500, disabled) | - | OK | - | OK | - | live | high |

### `keepaway` (gametype) — 19 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `keepaway.ball.spawn` | Ball spawn at map start (single keepawayball) | OK | OK | OK | ~ | - | live | high |
| `keepaway.ball.respawn` | Loose-ball relocate timer + fall-off/NEEDKILL respawn | ~ | OK | OK | ~ | OK | live | high |
| `keepaway.ball.pickup` | Ball pickup (attach to carrier, mark VIP, pickups++) | OK | ~ | OK | ~ | OK | live | high |
| `keepaway.ball.drop` | Ball drop on death/use-key/disconnect/observe | ~ | OK | OK | MISS | OK | live | high |
| `keepaway.score.timepoints` | Per-second carry scoring (score_timepoints * frametime) | OK | OK | OK | - | - | live | high |
| `keepaway.score.bctime` | Ball-carry-time secondary column (KEEPAWAY_BCTIME += frametime) | OK | OK | OK | - | - | live | high |
| `keepaway.score.killbonuses` | Kill bonuses (bckill killer bonus + killac carrier bonus + carrierkills) | OK | OK | OK | - | - | live | high |
| `keepaway.win.pointlimit` | Point-limit win condition + leader | OK | OK | - | - | - | live | high |
| `keepaway.ffa.framing` | FFA framing (no teams) + tie -> overtime | OK | OK | - | - | - | live | high |
| `keepaway.damage.matrix` | Possession damage/force scaling matrix (Damage_Calculate) | OK | OK | - | - | - | live | high |
| `keepaway.carrier.highspeed` | Carrier speed multiplier (MOVEVARS_HIGHSPEED *= ballcarrier_highspeed) | OK | OK | OK | - | - | live | high |
| `keepaway.hud.modicon` | Keepaway HUD mod-icon (blinking ball-carrying icon) | OK | OK | OK | OK | - | live | high |
| `keepaway.waypoints.tracking` | Ball / carrier waypoint sprites + radar tracking (g_keepawayball_tracking) | ~ | OK | OK | ~ | - | live | high |
| `keepaway.bot.role` | Bot ka roles (carrier/collector + ball goal rating + ForbidAttack) | ~ | OK | - | - | - | live | high |
| `keepaway.warn.noncarrier` | Non-carrier frag warning (CENTER_KEEPAWAY_WARN) | OK | OK | - | OK | - | live | high |
| `keepaway.score.countfragsremaining` | Suppress 'frags remaining' announce when timed scoring is on (Scores_CountFragsRemaining) | OK | - | - | OK | OK | live | high |
| `keepaway.log.eventlog` | Gamelog :ka: pickup/dropped echoes (ka_EventLog) | OK | - | - | - | - | live | high |
| `keepaway.score.resetmapglobal` | reset_map_global: clear per-client float2int decimal accumulator | ~ | OK | OK | - | - | live | high |
| `keepaway.score.preferplayerscore` | PreferPlayerScore_Clear (suppress team-score preference in FFA ka) | OK | - | - | - | - | live | high |

### `keyhunt` (gametype) — 22 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `keyhunt.mapinfo.autodetection` | KeyHunt eligibility for native maps without a sidecar | OK | OK | - | - | - | live | high |
| `keyhunt.controller.round_loop` | Controller round loop: wait-for-players -> countdown -> start round | OK | OK | OK | OK | - | live | high |
| `keyhunt.round.spawn_keys` | Spawn one key per team onto a random live teammate at round start | OK | OK | OK | OK | - | live | high |
| `keyhunt.key.presentation_model` | Key model + team colormap + fullbright glow + per-team netname | OK | OK | - | OK | - | live | high |
| `keyhunt.key.carried_orbit` | Carried key orbits the carrier (XYSPEED circle); stops on drop | OK | OK | OK | OK | - | live | high |
| `keyhunt.key.touch_collect` | Touch by enemy collects a dropped key (+collect score, delay_collect re-pickup gate) | OK | OK | OK | OK | OK | live | high |
| `keyhunt.capture.all_owned_and_in_range` | Capture: all keys on one team AND carriers within maxdist | OK | OK | OK | - | - | live | high |
| `keyhunt.capture.instant_path_dead` | Instant capture-on-pickup path (no maxdist) — dead | OK | - | - | - | - | DEAD | high |
| `keyhunt.capture.scoring` | Capture scoring: team SCORE + KH_CAPS + per-carrier SP_SCORE/nade bonus | OK | OK | - | - | - | live | high |
| `keyhunt.round.finish_reset` | Finish round: remove keys, count down, restart | OK | OK | OK | OK | - | live | high |
| `keyhunt.loss.timeout_return` | Dropped key auto-returns/destroys after delay_return | OK | OK | OK | OK | OK | live | high |
| `keyhunt.loss.loser_team_push_destroy` | Loser team: push (score_push) vs destroyed (score_destroyed split) | OK | OK | OK | OK | OK | live | high |
| `keyhunt.drop.on_death` | Drop all keys on death/suicide/observe/disconnect | OK | OK | OK | OK | OK | live | high |
| `keyhunt.drop.voluntary_use` | Voluntary +use drop of one key (kh_Key_DropOne) | OK | OK | OK | OK | OK | live | high |
| `keyhunt.combat.carrier_damage_force` | Carrier/noncarrier damage + force multipliers | OK | OK | - | - | - | live | high |
| `keyhunt.combat.carrier_frag` | Carrier-frag bonus + team-kill penalty | OK | OK | - | - | - | live | high |
| `keyhunt.unreachable.damage_destroy` | Lava/slime/trigger destroy + damage-return (return_when_unreachable) | OK | ~ | OK | - | - | live | high |
| `keyhunt.alarm.siren` | Periodic alarm while one team holds all keys | OK | OK | OK | - | OK | live | high |
| `keyhunt.notify.center_info` | Center-print + info notification storm (START/SCAN/ROUNDSTART/INTERFERE/MEET/HELP + PICKUP/DROP/LOST/PUSHED/DESTROYED/CAPTURE) + capture VFX | OK | - | OK | OK | - | live | high |
| `keyhunt.hud.objective_status` | OBJECTIVE_STATUS key-state pack + HUD modicons *(intended)* | OK | OK | OK | OK | - | live | high |
| `keyhunt.waypoints.sprites` | Dropped-key + carrier waypoint sprites (Run here / Key Carrier) | OK | OK | - | OK | - | live | high |
| `keyhunt.bot.role` | Bot KeyHunt role (carrier/defense/offense/freelancer goal rating) | OK | OK | - | - | - | live | high |

### `lms` (gametype) — 26 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `lms.lives.lose_on_death` | Death costs the victim a life; kills score no points | OK | OK | - | - | - | live | high |
| `lms.lives.eliminate_at_zero` | At 0 lives the player is out of the game with a finishing rank | OK | OK | OK | - | - | live | high |
| `lms.win.last_standing` | Match ends when at most one player still has lives; survivor wins | OK | OK | OK | - | - | live | high |
| `lms.respawn.continuous` | Living players respawn throughout a single LMS match | OK | OK | OK | - | - | live | high |
| `lms.lives.starting_count` | Starting lives = mapinfo lives= (default 5, legacy 9) / override, capped at fraglimit | ~ | ~ | - | - | - | live | high |
| `lms.join.new_player_lives_gate` | Late-join lives gate (lowest-lives clamp + can't-join lockout) | OK | OK | - | - | - | live | high |
| `lms.respawn.dynamic_delay` | Dynamic respawn delay scaling with lives behind the leader | OK | OK | OK | - | - | live | high |
| `lms.loadout.start_items` | Fixed LMS start loadout (health/armor/ammo) + least-healthy late-join clone | OK | OK | - | - | - | live | high |
| `lms.loadout.weapon_arena` | LMS forces a weapon arena (most_available by default) | OK | OK | - | - | - | live | high |
| `lms.regen.disabled` | Health/armor regen and rot disabled in LMS | OK | OK | - | - | - | live | high |
| `lms.items.no_pickups` | Item pickups suppressed (HealthMega->ExtraLife when enabled) | OK | OK | - | - | - | live | high |
| `lms.leader.computation` | Leader detection (max-lives players with a large enough, small enough lead) | OK | OK | OK | - | - | live | high |
| `lms.leader.glow` | Leader glow effect (EF_ADDITIVE | EF_FULLBRIGHT) | OK | - | OK | OK | - | live | high |
| `lms.leader.waypoint` | Leader radar waypoint with periodic visibility window | OK | OK | OK | OK | - | live | high |
| `lms.leader.notifications` | Leader visibility centerprints (VISIBLE_LEADER / VISIBLE_OTHER) | OK | - | OK | OK | - | live | high |
| `lms.hud.mod_icon` | HUD mod-icon: leader count + colored +N lives lead | OK | OK | - | OK | - | live | high |
| `lms.hud.no_lives_message` | Info message: 'You have no more lives left' (eliminated local player) | OK | - | - | OK | - | live | high |
| `lms.damage.dynamic_vampire` | Dynamic vampire: under-dogs steal health when hitting leaders | OK | OK | - | - | - | live | high |
| `lms.item.extra_life` | Extra-life pickups grant lives | OK | OK | - | OK | - | live | high |
| `lms.scoreboard.columns` | LMS scoreboard columns (lives + rank, sorted rank-then-lives) | OK | OK | - | OK | - | live | high |
| `lms.forfeit.remove_player` | Disconnect/forfeit assigns a rank and reshuffles other ranks | OK | OK | - | - | - | live | high |
| `lms.rules.no_throw_and_spec_lockout` | Can't drop weapon; ranked-out players can't become spectators | OK | - | - | - | - | live | high |
| `lms.reset.map_players` | Map/round reset restores eliminated players and clears lives/rank | OK | - | - | - | - | live | high |
| `lms.bot.fixcount` | Bot_FixCount: only INGAME players count as active for bot balancing | OK | - | - | - | - | live | high |
| `lms.respawn.deadflag_reset` | PlayerPreThink: DEAD_DYING → DEAD_RESPAWNING (auto-respawn living players) | OK | - | OK | - | - | live | high |
| `lms.scoreboard.rank_intermission_write` | AddPlayerScore: allow LMS_RANK writes during intermission (late joiners) | OK | - | - | - | - | live | high |

### `mayhem` (gametype) — 13 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mayhem.identity.registration` | Gametype identity + registration (mayhem netname, FFA, USEPOINTS, limit defaults) | OK | OK | - | - | - | live | high |
| `mayhem.score.recompute` | MayhemCalculatePlayerScore — recompute SP_SCORE from damage+frags (methods 1/2/3) | OK | OK | - | - | - | live | high |
| `mayhem.score.damage_accrual` | PlayerDamage_SplitHealthArmor — accrue total_damage_dealt (enemy +, self/world -) | OK | OK | - | - | - | live | high |
| `mayhem.score.per_kill` | Per-kill score driver (QC GiveFragsForKill: zero direct frag + recompute attacker) *(intended)* | OK | OK | - | - | - | live | high |
| `mayhem.loadout.start_items` | SetStartItems — spawn health/armor/ammo + unlimited-ammo flag (200/200/60/320/160/180/0) | OK | OK | - | - | - | live | high |
| `mayhem.loadout.weapon_arena` | SetWeaponArena — grant the full arsenal (most_available) at spawn *(intended)* | OK | ~ | - | - | - | live | high |
| `mayhem.combat.damage_nullify` | Damage_Calculate — nullify self-damage (when selfdamage off) and always nullify fall damage | OK | OK | - | - | - | live | high |
| `mayhem.combat.regen_rot` | PlayerRegen — disable health/armor regen and rot *(intended)* | OK | OK | - | - | - | live | high |
| `mayhem.items.filter` | FilterItem — powerup + pickup-item spawning rules | OK | OK | - | - | - | live | medium |
| `mayhem.weapons.forbid_throw` | ForbidThrowCurrentWeapon — players cannot drop their weapon | OK | - | - | - | - | live | high |
| `mayhem.match.limits_overtime` | Point/lead limit end-of-match + FFA tie → overtime | OK | OK | OK | - | - | live | high |
| `mayhem.match.reset` | reset_map_players — zero total_damage_dealt on round/map reset | OK | - | - | - | - | live | high |
| `mayhem.menu.describe_support` | Guide and registration summaries, point-limit controls and map support | OK | OK | - | OK | - | live | high |

### `nexball` (gametype) — 21 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `nexball.init.initialize` | Mode init: teams, score/lead limit, meter-period rounding, radar | OK | OK | - | - | - | live | high |
| `nexball.score.scorerules` | Score rules: team ST_NEXBALL_GOALS, player goals + faults columns | OK | OK | - | - | - | live | high |
| `nexball.goal.touch_scoring` | GoalTouch: enemy goal +1, own-goal/fault -1 (credited to other team in 2-team), out returns | OK | OK | OK | MISS | OK | live | high |
| `nexball.goal.limit_win` | Goal limit + winning-team latch (GameRules_limit_score) + lead limit | OK | OK | - | - | - | live | high |
| `nexball.goal.tie_overtime` | Tied-goals reports a tie (timed-match overtime instead of draw) | OK | - | - | - | - | live | medium |
| `nexball.ball.spawn` | Ball spawn: world ball entity, home origin, model/scale/trail/effects | OK | OK | OK | ~ | - | live | high |
| `nexball.ball.lifecycle` | Ball lifecycle thinks: delay_start release, 4-step ResetBall glide, idle reset | OK | OK | OK | - | OK | live | high |
| `nexball.ball.football_kick` | Football kick physics (soccer-style velocity boost on touch) | OK | OK | OK | - | OK | live | high |
| `nexball.ball.basketball_pickup` | Basketball pickup gating (cnt, health, dropper delay_collect, carrier bump) | OK | OK | OK | - | OK | live | high |
| `nexball.ball.giveball` | GiveBall: ownership, carry effects, forteam lifetime, weapon-arena swap | OK | OK | OK | ~ | - | live | high |
| `nexball.ball.dropball` | DropBall / DropOwner / drop on death/disconnect/observe | OK | OK | OK | OK | - | live | high |
| `nexball.carry.perframe` | Carry per-frame: view-ball follow, safe-pass lock, carrier-slowdown, carrying status | OK | OK | OK | MISS | - | live | high |
| `nexball.weapon.ballstealer` | Ball-launcher weapon (BallStealer): primary launch, power meter, secondary tackle/safe-pass | OK | OK | OK | MISS | OK | live | high |
| `nexball.weapon.power_meter` | Basketball power meter (charge-and-release launch strength) | OK | OK | OK | OK | - | live | high |
| `nexball.sound.cues` | Sound cues: bounce / drop / steal / shoot / goal | - | - | - | - | OK | live | high |
| `nexball.hud.modicon` | HUD mod icon (carrying indicator + power-meter bar) + eventchase | OK | OK | OK | OK | - | live | high |
| `nexball.goal.sentinel_encoding` | Goal fault/out sentinel encoding + ball_redgoal/bluegoal swap *(intended)* | OK | OK | - | - | - | live | high |
| `nexball.weapon_arena.player_setup` | Weapon-arena player setup: PlayerSpawn grants WEP_NEXBALL, PlayerPreThink strips normal weapons | ~ | OK | - | - | - | live | high |
| `nexball.item.filter_block` | Item mutator hooks: FilterItem (no loot WEP_NEXBALL), ItemTouch (carriers get no weapons), ForbidThrow/Drop | ~ | - | - | - | - | live | high |
| `nexball.ball.bounce_jumppad` | Ball bounce tuning (bouncefactor/bouncestop) + jumppad pushability | OK | OK | - | - | - | live | high |
| `nexball.waypoint.sprites` | Waypoint sprites: WP_NbBall (carrier + loose ball) + WP_NbGoal (per-team, enemy-only) | OK | OK | - | OK | - | live | high |

### `onslaught` (gametype) — 18 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `onslaught.graph.power_propagation` | Power graph + shielding propagation (onslaught_updatelinks) | OK | - | - | - | - | live | high |
| `onslaught.links.resolution` | onslaught_link map entity → graph edges | OK | - | - | - | - | live | high |
| `onslaught.cp.touch_build` | Control-point capture-by-build (touch → icon → build → flip) | OK | OK | OK | MISS | OK | live | high |
| `onslaught.cp.icon_damage` | Capture-icon damage / destroy mid-build (revert to neutral) | OK | OK | OK | MISS | OK | live | high |
| `onslaught.cp.icon_think_regen` | Built-icon steady-state slow regen | OK | OK | OK | MISS | OK | live | high |
| `onslaught.cp.icon_heal` | Capture-icon heal (Healer mutator / heal beam) | OK | OK | - | - | - | live | high |
| `onslaught.gen.damage_destroy` | Generator shield-gated damage + destruction | OK | OK | OK | MISS | OK | live | high |
| `onslaught.round.handler` | Round handler (warmup → countdown → round → end-delay) | OK | OK | OK | - | - | live | high |
| `onslaught.win.check_winner` | Win check (last standing generator) + ST_ONS_GENS credit | OK | OK | OK | ~ | OK | live | high |
| `onslaught.overtime.decay` | Overtime generator self-decay (sudden death) | OK | OK | OK | - | OK | live | high |
| `onslaught.scoring.rules` | Scoreboard rules (generators / caps / takes) *(intended)* | OK | OK | - | ~ | - | live | high |
| `onslaught.captureshield` | CaptureShield (spinning shield model + push + blocked sound) | OK | OK | OK | ~ | OK | live | high |
| `onslaught.spawn.teleport_choose` | Spawn placement + teleport (spawn_choose / spawn_at_* / ons_Teleport / click-radar) | OK | OK | OK | - | - | live | high |
| `onslaught.cp.proximity_decap` | Proximity de-capture (stand near a point to flip it) | OK | OK | OK | MISS | - | live | high |
| `onslaught.presentation.csqc` | CSQC presentation (generator/icon models, animation, radar, death-cam, FX) | ~ | ~ | ~ | ~ | - | ~ | high |
| `onslaught.audio.cues` | Onslaught notifications + sounds emission | - | - | - | ~ | OK | live | high |
| `onslaught.gen.unshielded_alarm` | Generator un-shielded periodic alarm (ons_GeneratorThink) | OK | OK | OK | ~ | OK | live | high |
| `onslaught.bot.roles` | Bot Onslaught objective role (offense/defense/assistant goal rating) | OK | OK | ~ | - | - | live | high |

### `race` (gametype) — 19 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `race.mode.setup_limits` | Mode setup + limits (teams, qualifying, lap/frag/time limits) | ~ | ~ | OK | - | - | live | high |
| `race.score.rules` | Score rules (fastest/laps/time columns + sort keys, no SP_SCORE) | OK | OK | - | - | - | live | high |
| `race.checkpoint.entities` | Checkpoint trigger entities spawned from the map lump | OK | OK | - | - | - | live | high |
| `race.checkpoint.ordered_crossing` | Ordered checkpoint crossing detection + advance | OK | OK | - | - | - | live | high |
| `race.lap.timing_scoring` | Lap close: fastest-lap + laps + cumulative-time scoring | OK | OK | OK | - | - | live | high |
| `race.records.db` | Per-map top-99 record ranking DB (read/pos/write/setTime) | OK | OK | - | - | - | live | high |
| `race.records.notifications` | Record result + finish/abandon notifications (INFO_RACE_*) | OK | OK | - | OK | - | live | high |
| `race.penalty.zones` | Penalty zones (freeze in race / accumulate in qualifying) | OK | OK | OK | - | - | live | high |
| `race.win.condition` | Win condition: everyone-finished + lap-limit + sudden-death run-on | OK | OK | - | - | - | live | high |
| `race.team.laps` | Team race: members' laps add up into ST_RACE_LAPS | OK | OK | - | - | - | live | high |
| `race.qualifying.mode` | Qualifying time trial and ready or timed completion | ~ | OK | OK | - | - | live | high |
| `race.qualifying.transition` | Qualifying-then-race transition (g_race_qualifying==2 -> 0) | OK | OK | OK | - | - | live | high |
| `race.spawn.grid_respawn` | Spawn grid by race_place + respawn at last checkpoint | OK | OK | - | - | - | live | high |
| `race.movement.quantization` | Force-keyboard movement quantization + race_movetime + FixClientCvars | OK | OK | OK | - | - | live | high |
| `race.hud.split_timer` | Race split timer HUD panel (#8): running clock, splits, anticipation, medals | OK | OK | OK | OK | - | live | high |
| `race.hud.checkpoint_splits` | Checkpoints split list panel (#27) | OK | OK | - | OK | - | live | high |
| `race.bot.role` | Bot race role (navigate the checkpoint track) | OK | OK | - | - | - | live | high |
| `race.hud.mod_icon_rankings` | Race/CTS mod-icon (PB + server best + medals) and Rankings scoreboard column | OK | OK | OK | OK | - | ~ | high |
| `race.speed_awards` | Speed awards (round-best + all-time-best speed at checkpoint) | OK | OK | OK | OK | - | live | high |

### `survival` (gametype) — 16 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `survival.gametype.registration` | Gametype registration (surv, USEPOINTS, timelimit=20 pointlimit=12) | OK | OK | - | - | - | live | high |
| `survival.round.assign_roles` | Round start secretly assigns hunters (live-only count, bounded, random pick) | OK | OK | OK | OK | - | live | high |
| `survival.round.handler` | Round-handler driven warmup/countdown/round/end-delay cycle | OK | OK | OK | - | - | live | high |
| `survival.scoring.bank_validkills` | Kills bank into validkills during the round (no immediate score) | OK | OK | OK | - | - | live | high |
| `survival.scoring.round_end_award` | Round end awards banked kills + per-side bonuses (survivals/hunts, reward_survival) | OK | OK | OK | - | - | live | high |
| `survival.scoring.anonymize` | Anonymize kills/deaths/suicides/dmg on the scoreboard while a round runs | OK | OK | OK | OK | - | live | high |
| `survival.win.side_wipe` | Side-wipe win latch (hunters win if any hunter alive, else prey, else tie) | OK | OK | OK | OK | - | live | high |
| `survival.win.timeout` | Round timer / match timeout → survivors win | OK | OK | OK | - | - | live | high |
| `survival.death.eliminate` | Death eliminates the player for the round (no respawn) | OK | OK | OK | - | - | live | high |
| `survival.death.punish_teamkill` | Killing an ally auto-kills the killer + docks frags | OK | OK | OK | - | - | live | high |
| `survival.net.hidden_role_disclosure` | Hunter identities hidden from prey mid-round, disclosed at round end *(intended)* | OK | OK | OK | - | - | live | high |
| `survival.hud.own_role_tag` | Mod-icons own-role tag (Hunter red / Survivor green), hidden pre-round | OK | OK | OK | OK | - | live | high |
| `survival.presentation.player_colors_and_notifications` | Player green/red coloring + role/win center & info notifications | OK | OK | OK | OK | - | live | high |
| `survival.bot.forbid_attack_allies` | Bots never attack same-status (ally) players | OK | OK | - | - | - | live | high |
| `survival.notify.last_survivor_alone` | Last living member of a side gets the 'you are alone' center notify | OK | OK | OK | OK | - | live | high |
| `survival.join.late_join_observe` | Mid-round joiner is forced to observe (no spawn until next round) *(intended)* | OK | OK | OK | OK | - | live | high |

### `tdm` (gametype) — 13 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `tdm.identity.registration` | TDM gametype identity + default match limits (timelimit=15 pointlimit=50 teams=2 leadlimit=0) | OK | OK | - | - | - | live | high |
| `tdm.init.gamerules` | Server init: GameRules_teams + spawning_teams + limit_score/lead | OK | OK | - | - | - | live | high |
| `tdm.rules.team_count` | Team count (override>=2 ? g_tdm_teams_override : g_tdm_teams, clamp 2..4) | OK | OK | - | - | - | live | high |
| `tdm.rules.team_spawns` | Team spawnpoint gating (g_tdm_team_spawns, default 0) | OK | OK | - | - | - | live | high |
| `tdm.scoring.team_frag` | Team frag scoring (enemy +1 attacker team; suicide/world -1 victim team; teamkill -1 attacker team) -> ST_SCORE | OK | OK | OK | - | - | live | high |
| `tdm.scoring.player_frag` | Per-player SP_SCORE frag credit (each frag also lands on the killer's individual score) | OK | OK | OK | - | - | live | high |
| `tdm.rules.point_limit` | Team point-limit end-of-match (g_tdm_point_limit; -1 = use mapinfo 50) | OK | OK | OK | - | - | live | high |
| `tdm.rules.lead_limit` | Team lead-limit end-of-match (g_tdm_point_leadlimit; -1 = use mapinfo 0) | OK | OK | OK | - | - | live | high |
| `tdm.rules.tie_overtime` | Tied-team -> overtime/sudden-death (no draw at the limit) | OK | OK | OK | - | - | live | high |
| `tdm.team_balance.join` | Smallest-team assignment on join (TeamBalance_JoinBestTeam) | OK | OK | - | - | - | live | high |
| `tdm.presentation.remaining_frags` | Remaining-frags team announcer (ANNCE_REMAINING_FRAG_1/2/3) | OK | OK | OK | OK | OK | live | high |
| `tdm.rules.map_support` | Map-support gating (m_isAlwaysSupported >=8 spawns & diameter>3250; m_isForcedSupported g_tdm_on_dm_maps) | OK | OK | - | - | - | live | high |
| `tdm.rules.map_team_config` | Map team config (spawnfunc tdm_team custom names/colors; mapinfo teams= -> g_tdm_teams via m_parse_mapinfo/m_setTeams) | OK | OK | - | OK | - | live | high |

### `tka` (gametype) — 18 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `tka.ball.spawn` | Ball spawn at match start (g_tkaball_count balls) | OK | OK | OK | ~ | - | live | high |
| `tka.ball.respawn` | Loose-ball relocate timer + fall-off respawn | OK | OK | OK | MISS | ~ | live | high |
| `tka.ball.pickup` | Ball pickup (attach to carrier, VIP, pickups++, orbit anim) | OK | ~ | OK | ~ | OK | live | high |
| `tka.ball.drop` | Ball drop on death / use-key / disconnect / observe | OK | OK | OK | ~ | OK | live | high |
| `tka.score.teamkill` | Team kill scoring (killac to team while team holds ball) | OK | OK | OK | - | - | live | high |
| `tka.score.scoreteam` | g_tka_score_team: any teammate's kill scores while team holds ball | OK | OK | OK | - | - | live | high |
| `tka.score.bckill` | Ball-carrier-kill bonus (bckill to team) + carrierkills column | OK | OK | OK | - | - | live | high |
| `tka.score.bctime` | Ball-carry-time secondary column (TKA_BCTIME += frametime) | OK | OK | OK | - | - | live | high |
| `tka.score.timepoints` | Timed possession points to TEAM score (g_tka_score_timepoints) | OK | OK | OK | - | - | live | high |
| `tka.score.nofrags` | No DM frags (GiveFragsForKill zeroed) + reset_map_global | OK | OK | - | - | - | live | high |
| `tka.damage.matrix` | Possession damage/force scaling matrix (Damage_Calculate) | OK | OK | - | - | - | live | high |
| `tka.carrier.highspeed` | Carrier speed multiplier (MOVEVARS_HIGHSPEED *= g_tka_ballcarrier_highspeed) | OK | OK | OK | - | - | live | high |
| `tka.win.pointlimit` | Team point-limit + lead-limit win condition | OK | OK | - | - | - | live | high |
| `tka.team.framing` | Team framing (teams 2..4, team spawns) + tie -> overtime | OK | OK | - | - | - | live | high |
| `tka.hud.modicon` | TKA HUD mod-icon + TKA_BALLSTATUS stat (team ball-taken icons) | OK | OK | OK | OK | - | live | high |
| `tka.waypoints.tracking` | Team-colored carrier / ball waypoints + radar tracking | OK | OK | - | ~ | - | live | high |
| `tka.bot.role` | Bot TKA roles (carrier/collector, team-aware goalrating, ForbidAttack) | OK | OK | - | - | - | live | high |
| `tka.score.preferplayerscore` | PreferPlayerScore_Clear: keep TKA score on rejoin (g_score_resetonjoin -1) | OK | - | - | - | - | live | high |

### `tmayhem` (gametype) — 15 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `tmayhem.identity.registration` | Gametype identity + registration (tmayhem netname, teamplay, USEPOINTS, limit/team defaults) | OK | OK | - | - | - | live | high |
| `tmayhem.teams.count` | Team count 2..4 (g_tmayhem_teams_override >= 2 ? override : g_tmayhem_teams) | OK | OK | - | ? | - | live | medium |
| `tmayhem.teams.spawns` | Team spawnpoints gated on g_tmayhem_team_spawns (default off) | OK | OK | - | - | - | live | high |
| `tmayhem.score.recompute` | MayhemCalculatePlayerScore (teamplay branch) — recompute SP_SCORE + route to team ST_SCORE | OK | OK | - | - | - | live | high |
| `tmayhem.score.damage_accrual` | PlayerDamage_SplitHealthArmor — SAME_TEAM-aware total_damage_dealt accrual | OK | OK | - | - | - | live | high |
| `tmayhem.score.per_kill` | Per-kill score driver (QC GiveFragsForKill: zero direct frag + recompute attacker) *(intended)* | OK | OK | - | - | - | live | high |
| `tmayhem.combat.damage_nullify` | Damage_Calculate — self/fall-damage nullify (live target) + ALWAYS zero mirror damage | OK | OK | - | - | - | live | high |
| `tmayhem.combat.regen_rot` | PlayerRegen — disable health/armor regen and rot *(intended)* | ~ | OK | - | - | - | live | medium |
| `tmayhem.loadout.start_items` | SetStartItems — spawn health/armor/ammo + unlimited-ammo flag (200/200/60/320/160/180/0) | OK | OK | - | - | - | live | high |
| `tmayhem.loadout.weapon_arena` | SetWeaponArena — grant the full arsenal (most_available) at spawn | OK | OK | - | - | - | live | high |
| `tmayhem.items.filter` | FilterItem — powerup + pickup-item spawning rules | OK | OK | - | - | - | live | medium |
| `tmayhem.weapons.forbid_throw` | ForbidThrowCurrentWeapon — players cannot drop their weapon | OK | - | - | - | - | live | high |
| `tmayhem.match.limits_overtime` | Point/lead limit end-of-match (team ST_SCORE) + team tie → overtime | OK | OK | OK | - | - | live | high |
| `tmayhem.match.reset` | reset_map_players — zero total_damage_dealt on round/map reset | OK | - | - | - | - | live | high |
| `tmayhem.menu.describe_support` | Guide and registration summaries, point-limit controls and map support | OK | OK | - | OK | - | live | high |

### `items-pickups` (item) — 24 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `items-pickups.def.resource_items` | Health / Armor / Ammo item definitions, models, colors, sounds, bboxes | OK | OK | - | OK | OK | live | high |
| `items-pickups.def.amounts_and_caps` | Per-item give amounts and pickup caps (g_pickup_* cvars, seeded by m_iteminit) | OK | OK | - | - | - | live | high |
| `items-pickups.give.item_giveto` | Item_GiveTo / Item_GiveAmmoTo — the type-agnostic give toward a cap | OK | OK | - | - | - | live | high |
| `items-pickups.give.powerup_stack` | Powerup timer application (stack vs refresh, g_powerups_stack) | OK | OK | OK | - | - | live | medium |
| `items-pickups.touch.gate` | Item_Touch gate (FL_PICKUPITEMS / dead / SOLID_TRIGGER / owner / spawnshield) + ItemTouch/ItemTouched hooks *(intended)* | OK | OK | - | - | OK | live | high |
| `items-pickups.respawn.scheduling` | Respawn scheduling (ScheduleRespawn/In, countdown-vs-think split, player-count scaling, jitter, countdown waypoint+sound) *(intended)* | OK | OK | OK | OK | OK | live | high |
| `items-pickups.respawn.initial` | Initial respawn (powerups/superweapons don't spawn at start; shared-random initial timing) | OK | OK | OK | - | - | live | high |
| `items-pickups.show.visibility` | Item_Show — model/solid/effects toggle + weapon-stay translucent path | OK | OK | - | ~ | - | live | high |
| `items-pickups.loot.toss_despawn` | Loot toss lifecycle (MOVETYPE_TOSS, anti-instant-pick, Item_Think despawn window) | OK | OK | OK | - | - | live | high |
| `items-pickups.loot.item_damage` | Item_Damage — loot destroyed by NEEDKILL damage (lava/sky jets) | OK | OK | - | - | - | live | high |
| `items-pickups.target.use_setactive` | item_use / item_setactive — targeted-item trigger-to-spawn + active toggle | OK | OK | - | - | - | live | high |
| `items-pickups.team.finditem` | Item_FindTeam — team-item random one-at-a-time spawn/respawn group *(intended)* | OK | OK | - | - | - | live | high |
| `items-pickups.spawn.driver` | StartItem spawn driver + spawnfuncs + map-lump wiring (liveness) | OK | OK | - | - | - | live | high |
| `items-pickups.presentation.bob_ghost_despawn` | Client bob/spin, ghost-on-pickup fade, loot despawn fade + puffs, pickup/respawn particles, distance fade *(intended)* | OK | OK | OK | ~ | - | live | high |
| `items-pickups.itemstime.hud_feed` | itemstime mutator — timed-item respawn-countdown table for the HUD *(intended)* | OK | OK | OK | ~ | - | live | medium |
| `items-pickups.itemgroup.networking` | setItemGroup / setItemGroupCount — cluster nearby small items for the itemstime 'N items' HUD label | MISS | - | - | - | - | - | high |
| `items-pickups.menu.item_describe` | Menu Guide describe() text for Health/Armor/Ammo/Fuel items (MENUQC tooltips from g_pickup_* cvar defaults) | OK | OK | - | OK | - | live | high |
| `items-pickups.registry.items_contract` | Items registry contract — REGISTRY(Items,32), m_id assignment, sort order (CSQC default_order_items), dumpitems command *(intended)* | ~ | - | - | - | - | live | high |
| `items-pickups.registry.model_sound_resolution` | Item_Model / Item_Sound path resolution (models/items/ + misc/ prefixes, ItemModel/ItemSound mutator hooks) | OK | OK | - | OK | OK | live | high |
| `items-pickups.give.pickup_dispatch_inventory` | Pickup.giveTo dispatch + Inventory_pickupitem (per-item-id inventory count net + STAT(LAST_PICKUP) pickup-HUD/crosshair feed) *(intended)* | OK | - | - | ~ | - | live | high |
| `items-pickups.spawn.weapon_respawn_defaults` | weapon_defaultspawnfunc tail — weapon/superweapon respawn defaults, jitter, weapons_anyway, superweapons_finished + pickup-ammo seeding | OK | ~ | - | - | - | live | high |
| `items-pickups.give.give_grammar` | GiveItems op-aware give grammar (cheat give / target_items / target_give) + POSTGIVE GiveSound/GiveRot feedback | OK | OK | ~ | - | MISS | live | high |
| `items-pickups.presentation.client_render_defaults` | Client item-render autocvar bare defaults (client/items/items.qh) as live inputs to the ItemDraw port | OK | ~ | - | ~ | - | live | high |
| `items-pickups.spawn.item_initialise_api` | Item_Initialise generic init API (lifetime semantics, noalign keep-position, expiring flag) + Item_RandomFromList *(intended)* | OK | OK | - | - | - | live | medium |

### `resources` (item) — 9 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `resources.api.registry` | RES_* resource registry + field indirection (health/armor/5 ammo) | OK | OK | - | - | - | live | high |
| `resources.api.getlimit` | GetResourceLimit (per-resource caps + hook + hard clamp) | OK | OK | - | - | - | live | high |
| `resources.api.setresource` | SetResource / SetResourceExplicit (clamp-to-limit + waste/changed hooks) | OK | OK | - | - | - | live | high |
| `resources.api.give` | GiveResource / GiveResourceWithLimit (+ per-resource rot-pause push) | OK | OK | OK | - | - | live | high |
| `resources.api.take` | TakeResource / TakeResourceWithLimit | OK | OK | - | - | - | live | high |
| `resources.regen.loop` | player_regen + RotRegen + CalcRegen/CalcRot (health/armor/fuel regen and rot) | OK | OK | OK | - | - | live | high |
| `resources.regen.pause_timers` | Regen/rot pause timers (damage, spawn, pickup, jetpack-fuel) shared storage | OK | OK | OK | - | - | live | high |
| `resources.cl.readout` | Client resource readout for HUD (CSQC GetResource path) | OK | OK | - | OK | - | live | medium |
| `resources.api.ammoconsumption` | GetAmmoConsumption (ammo-per-shot, for Q3 ammo-box .count scaling) | OK | OK | - | - | - | live | high |

### `mapobject-func` (mapobject) — 23 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mapobject-func.driver.calcmove` | Shared mover driver: SUB_CalcMove / SUB_CalcAngleMove + cubic_speedfunc bezier easing | OK | OK | OK | - | - | live | high |
| `mapobject-func.driver.pushmove` | MOVETYPE_PUSH integrator: rider carry, block->revert->.blocked, local-clock think (SV_PushMove) | OK | OK | OK | - | - | live | high |
| `mapobject-func.reset` | Map-object .reset / .reset2 pass on round/match restart (reset_map_global entity-reset) | OK | OK | - | - | - | live | high |
| `mapobject-func.door.slide` | func_door — sliding door state machine, LinkDoors groups, field, touch/use/blocked | OK | OK | OK | OK | OK | live | high |
| `mapobject-func.door.rotating` | func_door_rotating — swinging door (angle-move between two angle positions) | OK | OK | OK | OK | OK | live | high |
| `mapobject-func.door.keys` | func_door key locks (gold/silver itemkeys, consume + locked/unlocked sounds) | OK | OK | OK | OK | OK | live | high |
| `mapobject-func.button` | func_button — press-to-fire (touch/use/damage), wait+return, setactive timer, alt texture | OK | OK | OK | ~ | OK | live | high |
| `mapobject-func.plat` | func_plat — riding platform (center trigger raises, 3s top dwell, crush/reverse) | OK | OK | OK | ~ | OK | live | high |
| `mapobject-func.train` | func_train — path_corner chain ride (TURN/CURVE/NEEDACTIVATION/random, per-corner speed/wait) | OK | OK | OK | ~ | ~ | live | high |
| `mapobject-func.rotating` | func_rotating — constant-axis spin, setactive toggle, STARTOFF, ambient noise *(intended)* | OK | OK | OK | - | ~ | live | high |
| `mapobject-func.bobbing` | func_bobbing — sine translation via 0.1s controller | OK | OK | OK | - | ~ | live | high |
| `mapobject-func.pendulum` | func_pendulum — sine roll via 0.1s controller (Q3A freq formula) | OK | OK | OK | - | ~ | live | high |
| `mapobject-func.fourier` | func_fourier — sum-of-sines mover (netname quintuple list) | OK | OK | OK | - | ~ | live | high |
| `mapobject-func.vectormamamam` | func_vectormamamam — 4-reference projected mover | OK | OK | OK | - | ~ | live | high |
| `mapobject-func.breakable` | func_breakable / misc_breakablemodel — destructible BSP (debris, blast, respawn) *(intended)* | OK | OK | OK | ~ | OK | live | high |
| `mapobject-func.door_secret` | func_door_secret — Quake-1 secret door (slide back/down, then sideways, return, re-arm) *(intended)* | OK | OK | OK | OK | OK | live | high |
| `mapobject-func.conveyor_ladder` | trigger_conveyor/func_conveyor + func_ladder/func_water — per-frame volume scanners (producer) *(intended)* | OK | OK | OK | - | - | live | medium |
| `mapobject-func.rainsnow.spawn` | func_rain / func_snow — weather volume spawnfuncs (dest/cnt/count defaults + clamps) | OK | OK | - | - | - | live | high |
| `mapobject-func.rainsnow.net_draw` | ENT_CLIENT_RAINSNOW wire + Draw_RainSnow client weather particle rendering *(intended)* | OK | OK | ~ | ~ | - | live | medium |
| `mapobject-func.stardust` | func_stardust — EF_STARDUST sparkle prop with 0.25s csqcmodel autoupdate heartbeat *(intended)* | OK | OK | OK | ? | - | live | medium |
| `mapobject-func.driver.subs_helpers` | subs.qh generic helpers — SUB_SetFade/SUB_SetFade_Think/SUB_VanishOrRemove (+ SUB_NullThink, SUB_Friction) | OK | OK | OK | - | - | live | high |
| `mapobject-func.driver.lodmodel` | LODmodel_attach / LOD_customize — server-side per-mover lodmodel1/lodmodel2 distance LOD swap | MISS | MISS | - | MISS | - | - | medium |
| `mapobject-func.contracts.headers` | func_* header contracts — spawnflag bits / state enums / IL decls (.qh files) | - | OK | - | - | - | live | high |

### `mapobject-misc` (mapobject) — 17 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mapobject-misc.laser.server_logic` | misc_laser damage + detector think (aim, trace, team gate, enter/exit latch) *(intended)* | OK | OK | OK | - | - | live | high |
| `mapobject-misc.laser.beam_render` | misc_laser beam + end effect + dynamic light (Draw_Laser) | OK | OK | OK | OK | - | live | high |
| `mapobject-misc.follow.attach_follow` | misc_follow attach / MOVETYPE_FOLLOW ride at spawn | OK | OK | OK | OK | - | live | high |
| `mapobject-misc.dynlight.server_state` | dynlight static/follow/tag/path modes + toggle + reset (server) *(intended)* | OK | OK | OK | - | - | live | high |
| `mapobject-misc.dynlight.render` | dynlight realtime light (radius/color/style render) *(intended)* | OK | OK | OK | OK | - | live | high |
| `mapobject-misc.keys.item_key` | item_key* pickup keys (spawn, touch, grant itemkeys, model rotate) | OK | OK | OK | OK | OK | live | high |
| `mapobject-misc.teleport_dest.marker` | info_teleport_destination / misc_teleporter_dest endpoint | OK | OK | - | - | - | live | high |
| `mapobject-misc.corner.path_corner` | path_corner waypoint + per-corner platmovetype override (set_platmovetype) | OK | OK | OK | - | - | live | high |
| `mapobject-misc.subs.calcmove` | SUB_CalcMove / SUB_CalcAngleMove mover driver (linear + bezier + cubic_speedfunc) | OK | OK | OK | - | - | live | high |
| `mapobject-misc.bgmscript.adsr` | bgmscript map-music ADSR animation (client models / point particles) *(design difference; open timing, presentation, liveness)* | OK | ~ | ~ | ~ | - | ~ | high |
| `mapobject-misc.models.props_walls` | models.qc decoration props + static walls (spawn/solid/drop/colormap) *(intended)* | OK | OK | OK | ~ | - | live | high |
| `mapobject-misc.models.clientwall_fade` | func_clientwall distance-fade + PVS alpha-cull + antiwall solid-toggle (Ent_Wall_PreDraw / g_clientmodel_use) | ~ | ~ | - | MISS | - | ~ | high |
| `mapobject-misc.pointparticles.server_logic` | func_pointparticles / func_sparks server state (spawn defaults, box anchor, toggle/use, round reset) *(intended)* | OK | OK | - | - | - | live | high |
| `mapobject-misc.pointparticles.client_emit` | func_pointparticles client emission (Draw_PointParticles continuous/burst particles and positional noise) *(design difference; open timing, presentation, liveness)* | ~ | ~ | ~ | ~ | ~ | ~ | high |
| `mapobject-misc.laser.wire_contract` | misc_laser net-link stream (SF_LASER_* delta sendflags, ENT_CLIENT_LASER) *(intended)* | ~ | - | - | - | - | ~ | high |
| `mapobject-misc.keys.usekey_keylist` | item_keys_usekey / item_keys_keylist door-key API + item_keys_names table | OK | OK | - | - | - | live | high |
| `mapobject-misc.models.wall_lod` | wall/prop LOD models (lodmodel1/lodmodel2 keys -> distance-switched modelindex) | MISS | MISS | - | MISS | - | - | high |

### `mapobject-movers-platforms` (mapobject) — 12 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mapobject-movers-platforms.plat.spawn_setup` | func_plat spawnfunc: defaults, pos1/pos2, brush init, reset | OK | OK | - | - | OK | live | high |
| `mapobject-movers-platforms.plat.q3compat_defaults` | Q3/Q3DF compat: speed 200, lip 8, dmg 2, medplat sounds, CPMA sound keys | OK | OK | - | - | ~ | live | high |
| `mapobject-movers-platforms.plat.center_trigger` | plat_spawn_inside_trigger: ride-detect center trigger volume | OK | OK | - | - | - | live | high |
| `mapobject-movers-platforms.plat.state_machine` | Up/down/top/bottom state machine + dwell timers | OK | OK | OK | - | OK | live | high |
| `mapobject-movers-platforms.plat.center_touch` | plat_center_touch: live creature raises / refreshes plat | OK | OK | OK | - | - | live | high |
| `mapobject-movers-platforms.plat.trigger_use` | plat_trigger_use / plat_use: external trigger send-down | OK | - | OK | - | - | live | high |
| `mapobject-movers-platforms.plat.crush` | plat_crush: CRUSH instakill / .dmg bite + reverse | OK | OK | - | - | - | live | high |
| `mapobject-movers-platforms.plat.reset` | plat_reset: targeted=start-raised, else start-bottom | OK | - | - | - | - | live | high |
| `mapobject-movers-platforms.plat.outside_target_use` | plat_outside_touch / plat_target_use (Q3 + mapper-wired) | OK | OK | OK | - | - | ~ | high |
| `mapobject-movers-platforms.subs.calcmove` | SUB_CalcMove mover driver (linear branch + bezier easing) | OK | OK | OK | - | - | live | high |
| `mapobject-movers-platforms.subs.platmovetype_parse` | set_platmovetype / cubic_speedfunc_is_sane (platmovetype key parse + sanity reject) | OK | OK | - | - | - | live | high |
| `mapobject-movers-platforms.engine.pusher_integration` | MOVETYPE_PUSH integration: think on ltime, rider carry, blocked revert | OK | - | OK | - | - | live | high |

### `mapobject-target` (mapobject) — 15 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mapobject-target.kill.use` | target_kill — deal 1000 void damage to the activator | OK | OK | - | - | - | live | high |
| `mapobject-target.speed.use` | target_speed — reproject the activator's velocity from speed + axis mask *(intended)* | OK | OK | - | - | - | live | high |
| `mapobject-target.spawnpoint.use` | target_spawnpoint — force the activator's next spawn point | OK | - | - | - | - | live | high |
| `mapobject-target.location.spawn` | target_location / info_location — HUD location-name volumes | OK | - | - | OK | - | live | high |
| `mapobject-target.changelevel.use` | target_changelevel — end / switch the level (multiplayer fraction) | OK | OK | - | - | - | live | high |
| `mapobject-target.levelwarp.use` | target_levelwarp — campaign level warp | OK | - | - | - | - | live | high |
| `mapobject-target.give.use` | target_give — give the activator the items pointed to | OK | OK | - | - | OK | live | medium |
| `mapobject-target.items.use` | target_items — set/add items + resources + powerups via a token string | ~ | OK | - | OK | - | live | high |
| `mapobject-target.spawn.use` | target_spawn — data-driven entity creator/editor ($-templating) *(intended)* | ~ | OK | - | - | - | live | medium |
| `mapobject-target.speaker.ambient_triggered` | target_speaker — ambient / looped / one-shot / activator sound emitter | ~ | OK | - | - | OK | live | high |
| `mapobject-target.music.target_music` | target_music — triggered background-music override / map default *(intended)* | ~ | ~ | ~ | OK | OK | ~ | medium |
| `mapobject-target.music.trigger_music` | trigger_music — brush-volume music override (highest priority) *(intended)* | ~ | OK | ~ | OK | OK | ~ | medium |
| `mapobject-target.voicescript.sequence` | target_voicescript — scripted per-player voice-line sequence | OK | OK | OK | - | OK | live | high |
| `mapobject-target.teleport_dest.spawn` | info_teleport_destination / misc_teleporter_dest — teleport endpoint | OK | OK | - | - | - | live | high |
| `mapobject-target.position.spawn` | target_position / info_notnull — passive jumppad/impulse destination marker | OK | - | - | - | - | live | medium |

### `mapobject-teleporters-portals` (mapobject) — 17 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `teleporters.core.teleport_player` | TeleportPlayer relocate/sound/telefrag/kill-credit core | OK | OK | OK | OK | OK | live | high |
| `teleporters.core.telefrag` | tdeath box telefrag with self-gib for dead/monster teleportees | OK | OK | OK | - | - | live | high |
| `teleporters.core.telefrag_teamplay` | Teammates spared from telefrag in team modes (g_telefrags_teamplay) | OK | OK | - | - | - | live | high |
| `teleporters.core.telefrag_cvars` | g_telefrags / g_telefrags_avoid cvars honored at runtime | OK | OK | - | - | - | live | high |
| `teleporters.dest.simple_teleport` | Simple_TeleportPlayer dest lookup + speed reproject + .speed cap | OK | OK | OK | - | - | live | high |
| `teleporters.dest.random_selection` | Multi-destination weighted-random pick with telefrag-avoid priority | OK | OK | ? | - | - | live | medium |
| `teleporters.trigger.active_gate` | Teleport_Active gating (active/dead/team/INVERT_TEAMS/observers-only/turret/vehicle) | OK | OK | - | - | - | live | high |
| `teleporters.trigger.touch_and_targets` | Teleport_Touch + SUB_UseTargets firing | OK | - | OK | - | - | live | high |
| `teleporters.trigger.team_claim` | trigger_teleport_use team claim | OK | - | - | - | - | ? | medium |
| `teleporters.spawn.entities` | spawnfuncs: trigger_teleport / target_teleporter / info_teleport_destination / misc_teleporter_dest | ~ | - | - | - | - | live | high |
| `teleporters.fx.flash` | Teleport flash effect at both ends | OK | OK | OK | OK | - | live | high |
| `teleporters.fx.sound` | Teleport sound (misc/teleport, debounced, optional noise word-list) | OK | OK | OK | - | OK | live | high |
| `portals.weapon.projectile` | Porto projectile launch/bounce/lifetime/one-portal latch + touch decision tree | OK | OK | OK | ~ | OK | live | high |
| `portals.entity.lifecycle` | Portal edict: model, skins, EF effects, health, lifetime/fade, damage, explode/expire sounds, Customize | ~ | ~ | ~ | MISS | ~ | ~ | high |
| `portals.teleport.transform_and_amazing` | Portal teleport transform, two-way semantics, Amazing announcer, portal-owner kill-credit | OK | ~ | ? | - | OK | live | medium |
| `portals.weapon.cleanup` | Portal pair lifecycle: one pair per owner, death/timeout clears both, ID-clear | OK | OK | OK | - | - | live | high |
| `portals.weapon.hook_and_predict` | Portal grappling-hook teleport + predict-ahead crossing (Portal_WillHitPlane) + flag exclusion | ~ | MISS | MISS | - | - | live | medium |

### `mapobject-triggers` (mapobject) — 24 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mapobject-triggers.common.sub_usetargets` | SUB_UseTargets — delay / killtarget / message / target_random fire primitive | OK | OK | OK | OK | OK | live | high |
| `mapobject-triggers.multi.trigger_multiple` | trigger_multiple / trigger_once — repeatable/one-shot volume trigger | OK | OK | OK | - | OK | live | high |
| `mapobject-triggers.hurt.trigger_hurt` | trigger_hurt — damage volume | OK | OK | OK | - | - | live | high |
| `mapobject-triggers.heal.trigger_heal` | trigger_heal / target_heal — heal volume / relay | OK | OK | OK | - | OK | live | high |
| `mapobject-triggers.gravity.trigger_gravity` | trigger_gravity — gravity zone + non-sticky exit checker | OK | OK | OK | - | OK | live | high |
| `mapobject-triggers.impulse.trigger_impulse` | trigger_impulse — radial / directional / accel force fields | OK | OK | OK | - | - | live | high |
| `mapobject-triggers.push.trigger_push` | trigger_push / target_push / trigger_push_velocity — jumppads | OK | OK | OK | ~ | OK | live | high |
| `mapobject-triggers.swamp.trigger_swamp` | trigger_swamp — slow + damage players inside | OK | OK | OK | - | - | live | high |
| `mapobject-triggers.secret.trigger_secret` | trigger_secret — secrets-found counter trigger | OK | OK | - | OK | OK | live | high |
| `mapobject-triggers.counter.trigger_counter` | trigger_counter — fire after N uses (+ per-player) | OK | OK | OK | OK | OK | live | high |
| `mapobject-triggers.relay.trigger_relay` | trigger_relay / target_relay / target_delay / trigger_delay | OK | OK | OK | - | - | live | high |
| `mapobject-triggers.monoflop.trigger_monoflop` | trigger_monoflop — one input -> one ON + one delayed OFF | OK | OK | OK | - | - | live | high |
| `mapobject-triggers.flipflop.trigger_flipflop` | trigger_flipflop — pass only every 2nd event | OK | OK | - | - | - | live | high |
| `mapobject-triggers.multivibrator.trigger_multivibrator` | trigger_multivibrator — free-running on/off oscillator | OK | OK | OK | - | - | live | high |
| `mapobject-triggers.disablerelay.trigger_disablerelay` | trigger_disablerelay — flip ACTIVE<->NOT on named targets | OK | - | - | - | - | live | high |
| `mapobject-triggers.relay_if.trigger_relay_if` | trigger_relay_if — cvar-compare gate | OK | - | - | - | - | live | high |
| `mapobject-triggers.relay_teamcheck.trigger_relay_teamcheck` | trigger_relay_teamcheck — team gate on the activator | OK | - | - | - | - | live | high |
| `mapobject-triggers.relay_activators.relay_activate` | relay_activate / relay_deactivate / relay_activatetoggle — set targets' active state | OK | - | - | - | - | live | high |
| `mapobject-triggers.gamestart.trigger_gamestart` | trigger_gamestart — fire targets at game start, then delete *(intended)* | OK | OK | OK | - | - | live | high |
| `mapobject-triggers.magicear.trigger_magicear` | trigger_magicear — chat pattern match -> SUB_UseTargets / text replace | OK | OK | - | - | - | live | high |
| `mapobject-triggers.keylock.trigger_keylock` | trigger_keylock — fire target when all required keys supplied | OK | OK | OK | OK | OK | live | high |
| `mapobject-triggers.teleport.trigger_teleport` | trigger_teleport / target_teleporter — player teleport source *(intended)* | OK | OK | OK | OK | OK | live | high |
| `mapobject-triggers.music.target_music` | target_music / trigger_music — map music overrides *(intended)* | OK | OK | OK | OK | OK | live | high |
| `mapobject-triggers.viewloc.trigger_viewlocation` | trigger_viewlocation and target anchors for 2.5D camera regions *(intended)* | ~ | OK | ~ | ~ | - | live | high |

### `warpzones` (mapobject) — 18 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `warpzones.transform.algebra` | Seamless-portal transform math (origin/velocity/angles rotated through the seam) *(intended)* | OK | OK | - | - | - | live | high |
| `warpzones.transform.chain` | Chained-transform accumulator (multi-portal composition) | OK | OK | - | - | - | live | high |
| `warpzones.spawn.trigger` | trigger_warpzone / trigger_warpzone_position map spawnfuncs + registration | OK | OK | - | - | - | live | high |
| `warpzones.spawn.brushplane` | Auto-orient IN plane from brush geometry (getsurface* area-weighted normal) | OK | OK | - | - | - | live | high |
| `warpzones.spawn.link` | Pair zones by target/targetname (+ sv_warpzone_allow_selftarget) and deferred init | OK | OK | OK | - | - | live | high |
| `warpzones.teleport.player` | Player/projectile crossing: warp origin/velocity/angles, momentum-preserved | OK | OK | OK | - | - | live | high |
| `warpzones.teleport.usetargets` | Fire warpzone targets on a crossing (SUB_UseTargets) | OK | OK | - | - | - | live | high |
| `warpzones.combat.traceline` | Hitscan/aim traces cross seamless portals (16-zone guard) *(intended)* | OK | OK | - | - | - | live | high |
| `warpzones.combat.findradius` | Splash radius damage reaches victims through portals (blast-frame falloff) *(intended)* | OK | ~ | - | - | - | live | high |
| `warpzones.porto` | Porto-weapon portals realised as warpzones (lifecycle + kill-credit + activate grace) | OK | OK | OK | MISS | - | live | high |
| `warpzones.client.render` | Portal surface drawn as a live camera view of the linked OUT plane | OK | ~ | OK | ~ | - | ~ | medium |
| `warpzones.client.fixview` | Seamless view across the seam (FixView / FixNearClip / View_Inside / FixPMove) | ~ | ~ | ~ | ~ | - | ~ | high |
| `warpzones.client.teleported_view` | Smooth view/input rotate on a server crossing (ENT_CLIENT_WARPZONE_TELEPORTED + CL_RotateMoves) | MISS | - | MISS | MISS | - | - | high |
| `warpzones.client.prediction` | Client-side prediction of a warpzone crossing | OK | - | OK | MISS | - | live | high |
| `warpzones.camera` | func_camera / func_warpzone_camera (fixed dpcamera security-camera view) | ~ | ~ | ~ | ~ | - | ~ | high |
| `warpzones.reconnect` | trigger_warpzone_reconnect + WarpZone_Think (runtime / moving-warpzone re-link) | OK | OK | OK | - | - | live | high |
| `warpzones.observer_warp` | Per-frame observer / SOLID_NOT client warp (WarpZone_StartFrame) | OK | OK | OK | - | - | live | high |
| `warpzones.combat.traceextras` | WarpZone_TraceToss (ballistic) + WarpZone_TrailParticles (trail through portal) | MISS | - | - | MISS | - | - | medium |

### `menu-core` (menu) — 39 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `menu-core.vm.lifecycle_toggle` | Menu VM lifecycle: init, draw loop, toggle/display/hide, disconnect reset, m_goto *(intended)* | ~ | ~ | - | ~ | MISS | live | high |
| `menu-core.vm.menu_music_sounds` | Menu music (cd loop $menu_cdtrack) + click/focus sounds (menu_sounds) | MISS | - | - | - | MISS | - | high |
| `menu-core.vm.tooltips` | Tooltip system incl. advanced mode (cvar name/value/default + dependency reasons) | ~ | ~ | - | ~ | - | live | high |
| `menu-core.vm.draw_primitives` | Menu draw primitives + transform stack (coverage: replaced by Godot 2D) *(intended)* | - | - | - | ~ | - | live | high |
| `menu-core.vm.menu_cmd` | menu_cmd command surface (directmenu/closemenu/nav verbs/sync/isdemo/dumptree) | ~ | - | - | - | MISS | live | high |
| `menu-core.toolkit.item_widgets` | QC widget toolkit (menu/item/*) + animation framework (coverage: Godot Controls replace it) *(intended)* | - | - | - | ~ | - | live | high |
| `menu-core.toolkit.xonotic_widgets` | Xonotic-skinned widget wrappers (menu/xonotic base widgets; coverage row) *(intended)* | - | - | - | ~ | - | live | medium |
| `menu-core.nav.mainwindow_nexposee` | Main menu fan (nexposee): 6 dialogs, scale calc, pulled Credits/Quit, open/collapse animation | OK | OK | ? | ~ | MISS | live | high |
| `menu-core.nav.tab_containers` | Multiplayer + Settings tab-container dialogs (tab sets/order) | OK | OK | - | ~ | - | live | high |
| `menu-core.nav.gamemenu` | In-game escape menu (Game Menu dialog) + leave-match button | ~ | ~ | - | ~ | MISS | live | high |
| `menu-core.nav.quit_dialog` | Quit confirmation dialog | OK | OK | - | OK | - | live | high |
| `menu-core.nav.welcome` | Server welcome/MOTD dialog (connect-time hostname + welcome text + Join/Spectate) | ~ | ~ | - | ~ | - | live | high |
| `menu-core.nav.teamselect` | Team selection dialog (auto/red/blue/yellow/pink/spectate, availability bitmask) | OK | OK | - | ~ | - | live | high |
| `menu-core.nav.firstrun_tos` | First-run welcome dialog + Terms-of-Service dialog (auto-shown on first launch) | ~ | ? | - | ~ | - | live | high |
| `menu-core.nav.uid2name` | uid2name stats-consent popup (allow nickname in player statistics?) *(intended)* | OK | OK | - | OK | - | live | high |
| `menu-core.browser.serverlist` | Server browser: refresh/LAN/master query, filters, sorting, favorites, join | ~ | ~ | ~ | ~ | MISS | live | high |
| `menu-core.create.maplist` | Map list widget: gametype filtering, previews, g_maplist toggle, add/remove, load map | ~ | ~ | - | ~ | MISS | live | high |
| `menu-core.create.gametypelist` | Create-game gametype list + weapons-list/weapon-arena widgets | ~ | ? | - | ~ | MISS | live | medium |
| `menu-core.keys.keybinder` | Keybinder: bind catalog, key capture, clear, reset-all, conflict/limit handling | ~ | ~ | - | ~ | MISS | live | high |
| `menu-core.sp.campaign_screen` | Singleplayer/campaign screen: level list + unlock frontier, campaign cycler, instant action | OK | ~ | - | ~ | MISS | live | high |
| `menu-core.sp.winner_dialog` | Campaign Winner dialog (auto-popped when g_campaign<name>_won flips) | stub | - | - | ~ | MISS | DEAD | high |
| `menu-core.media.demolist` | Demo browser (list/filter/play/timedemo + disconnect-confirm dialogs) | ~ | - | - | ~ | - | live | high |
| `menu-core.media.screenshots` | Screenshot browser (list/filter) + viewer (zoom/slideshow) | ~ | OK | - | ~ | - | live | high |
| `menu-core.media.musicplayer` | Music player (sound list, playlist cvar editing, menu-track set/reset, transport) | ~ | OK | - | ~ | MISS | live | high |
| `menu-core.media.guide` | Guide tab (topics/entries/descriptions) + Media tab container | ~ | - | - | ~ | - | live | medium |
| `menu-core.media.guide.description` | Guide description refresh and empty state *(intended)* | ~ | - | - | ~ | - | live | high |
| `menu-core.media.guide.entries` | Guide entry filters and keyboard selection *(intended)* | ~ | - | - | OK | ? | live | high |
| `menu-core.media.guide.pages` | Guide free-text pages and localized substitutions | OK | - | - | OK | - | live | high |
| `menu-core.media.guide.topics` | Guide topic routing, catalog order and icons *(intended)* | ~ | - | - | ~ | - | live | high |
| `menu-core.misc.credits` | Credits roll (auto-scroll + names list) | OK | ~ | ~ | OK | - | live | high |
| `menu-core.lists.language` | Language selection (languages.txt list, restart flow, in-match warning) | ~ | OK | - | ~ | MISS | live | high |
| `menu-core.lists.skins` | Menu-skin selection (skinlist) + HUD-skin list (hudskinlist) | ~ | OK | - | ~ | MISS | live | high |
| `menu-core.lists.stats` | Player statistics list (statslist) + server-info player list (playerlist) | MISS | - | - | MISS | - | - | high |
| `menu-core.profile.player_setup_widgets` | Player-setup widgets: model selector, name charmap + color pickers, crosshair picker/preview | OK | OK | - | ~ | - | live | medium |
| `menu-core.hudconfig.panel_dialogs` | HUD panel config dialogs (22 per-panel dialogs + hudsetup exit dialog) | OK | ? | - | ~ | - | live | medium |
| `menu-core.tools.sandbox_monster_dialogs` | Sandbox tools + monster tools dev dialogs | ~ | ? | - | ~ | - | ~ | high |
| `menu-core.util.updatecheck` | Update notification + ext-response system (version banner, banned/promoted/recommended, hotfix pk3) | MISS | MISS | - | MISS | - | - | high |
| `menu-core.util.helpers` | Menu helper framework: cvar save/load walk, setDependent visibility engine, sendCvars, gametype table | ~ | - | - | - | - | live | medium |
| `menu-core.misc.cvarlist` | Cvar list/editor (search, value edit, revert, description pane) | ~ | ~ | - | ~ | - | live | high |

### `menu-dialogs` (menu) — 21 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `menu-dialogs.settings.container` | Settings dialog: 7-tab container (Video/Effects/Audio over Game/Input/User/Misc) | OK | OK | - | OK | - | live | high |
| `menu-dialogs.settings.video` | Video tab: mode/resolution/vsync/fps + AA/aniso + color control | ~ | ~ | - | ~ | - | ~ | high |
| `menu-dialogs.settings.effects` | Effects tab: quality presets + geometry/texture/decals/lights/particles | ~ | ~ | - | ~ | - | ~ | high |
| `menu-dialogs.settings.audio` | Audio tab: channel volumes, output format, hitsound, menu sounds, announcer | ~ | ~ | - | ~ | ~ | ~ | high |
| `menu-dialogs.settings.game_host` | Game tab host: topic list + swappable panel (crosshair/HUD/messages/model/view/weapons + registered extras) *(intended)* | OK | - | - | ~ | - | live | high |
| `menu-dialogs.settings.game_crosshair` | Game>Crosshair tab: enable/per-weapon/custom, picker+preview, size/alpha/color, ring, dot, hittest/animations | OK | OK | - | ~ | - | live | high |
| `menu-dialogs.settings.game_hud` | Game>HUD tab: scoreboard opts, waypoints, player names, speed unit/damage overlay/dynamic HUD, HUD-editor entry (+hudconfirm) | ~ | OK | - | ~ | - | live | high |
| `menu-dialogs.settings.game_messages` | Game>Messages tab: spree/frag info, gametype notifications, chatbox/console, announcer toggles | ~ | ~ | - | ~ | ~ | ~ | high |
| `menu-dialogs.settings.game_model` | Game>Models tab: simple items, ghost items, force models/colors, body fading, gibs | ~ | OK | - | ~ | - | ~ | high |
| `menu-dialogs.settings.game_view` | Game>View tab: FOV, 1st/3rd person, bob/idle smoothing, zoom family, velocity zoom, reticle | OK | ~ | - | ~ | - | live | high |
| `menu-dialogs.settings.game_weapons` | Game>Weapons tab: priority list, cycling opts, autoswitch, viewmodel visuals, tracer teamcolor | OK | OK | - | ~ | - | ~ | high |
| `menu-dialogs.settings.input` | Input tab: keybinder list + mouse (sensitivity/filter/invert/accel) + other (console/jump/jetpack/joystick), userbind + bindings-reset dialogs | ~ | ~ | - | ~ | - | ~ | high |
| `menu-dialogs.settings.user` | User tab: menu-skin list, text-language list, gentle-mode trio (+ language warning dialog) | ~ | OK | - | ~ | - | ~ | high |
| `menu-dialogs.settings.misc` | Misc tab: network/HTTP groups, tooltips/animations/textshadow/showtime, Advanced settings + Factory reset (+ cvars dialog host + reset confirm) | ~ | ~ | - | ~ | - | ~ | high |
| `menu-dialogs.mp.container` | Multiplayer dialog: Servers/Create/Profile tab container | OK | OK | - | OK | - | live | high |
| `menu-dialogs.mp.join` | Servers tab: filter row (Categories/Empty/Full/Laggy/Pause), sort headers, address+bookmark+info row, Join | ~ | OK | - | ~ | - | live | high |
| `menu-dialogs.mp.serverinfo` | Server-info dialog: Status tab (hostname/address/gametype/map/mod/version/settings/players/bots/slots/encryption/ID) + ToS tab | ~ | ? | - | ~ | - | live | medium |
| `menu-dialogs.mp.create` | Create tab: gametype list + show-all, time/frag/teams/slots/bots/skill sliders, maplist column, Mutators..., Start | OK | ~ | - | ~ | - | live | high |
| `menu-dialogs.mp.create_mapinfo` | Map-info popup: preview image, title/author/description, supported-gametype tags, Close/Play | ~ | ? | - | ~ | - | live | medium |
| `menu-dialogs.mp.create_mutators` | Mutators dialog: gameplay mutators, weapon/item mutators, weapon-arena radios + per-weapon grid, special arenas (InstaGib/NIX), start-weapons | OK | ~ | - | ~ | - | live | medium |
| `menu-dialogs.mp.profile` | Profile tab: name box + color/charmap pickers, model selector + glow/detail palettes, stats opt-ins, language button, Apply | ~ | OK | - | ~ | - | ~ | high |

### `console-commands` (misc) — 26 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `console-commands.generic.dispatch_infra` | Generic-command dispatch + registry macros (GenericCommand, GENERIC_COMMANDS, Command class, per-program aliases) *(intended)* | OK | OK | - | - | - | live | high |
| `console-commands.generic.addtolist_removefromlist` | addtolist / removefromlist — dedup append / rebuild-remove on a space-separated list cvar | OK | OK | - | - | - | live | high |
| `console-commands.generic.maplist` | maplist add/remove/shuffle/cleanup — g_maplist editing | OK | OK | - | ~ | - | live | high |
| `console-commands.generic.settemp` | settemp / settemp_restore — temporary cvar override with capture-once restore | OK | OK | ? | - | - | live | medium |
| `console-commands.generic.rpn` | rpn — the RPN calculator command language (stack ops, arithmetic, cvar def/load, set ops) *(intended)* | ~ | ~ | - | - | - | live | high |
| `console-commands.generic.nextframe` | nextframe — queue a command for the next VM frame | OK | OK | OK | - | - | live | high |
| `console-commands.generic.dumpcommands` | dumpcommands — dump all command aliases to <program>_dump.txt | OK | ~ | - | - | - | live | high |
| `console-commands.generic.qc_curl` | qc_curl — HTTP GET/POST from the console (--cvar / --exec sinks) *(intended)* | MISS | MISS | - | - | - | - | high |
| `console-commands.generic.restartnotifs` | restartnotifs — destroy + re-register all notifications (with per-type counts) | MISS | MISS | - | - | - | - | high |
| `console-commands.generic.runtest` | runtest — run the in-VM unit tests (TEST_Run / RUN_ALL_TESTS) *(intended)* | MISS | - | - | - | - | - | high |
| `console-commands.generic.markup_easter_eggs` | Chat markup (&smiley table, `red` command) + the crc16-obfuscated chat easter eggs | MISS | MISS | - | MISS | - | - | high |
| `console-commands.client.dispatch` | CSQC command dispatch (cl_cmd GameCommand, CLIENT_COMMANDS registry, help) + registered console commands (+showaccuracy, demo camera +forward/…) *(intended)* | ~ | - | - | - | - | live | high |
| `console-commands.client.hud_group` | cl_cmd hud <configure|save|quickmenu|radar|clickradar|scoreboard_columns_set|scoreboard_columns_help> | ~ | ~ | - | ~ | - | live | high |
| `console-commands.client.localprint` | cl_cmd localprint — self-centerprint a message | OK | - | - | ~ | - | live | high |
| `console-commands.client.sendcvar_replication` | sendcvar + cmd sentcvar + GetCvars/REPLICATE plumbing (client cvar replication to the server) *(intended)* | OK | OK | OK | - | - | live | high |
| `console-commands.client.handlevote` | cl_cmd handlevote yes|no (+ vyes/vno binds, uid2name dialog interception) *(intended)* | OK | OK | - | ~ | - | live | high |
| `console-commands.client.mv_download` | cl_cmd mv_download — request a mapshot image during map vote | MISS | - | - | MISS | - | - | high |
| `console-commands.client.print_cptimes` | cl_cmd print_cptimes — dump stored race checkpoint splits to console | MISS | - | - | MISS | - | - | high |
| `console-commands.client.debug_tools` | cl_cmd blurtest / boxparticles / debugmodel / create_scrshot_ent (client debug commands) | MISS | - | - | MISS | - | - | high |
| `console-commands.replies.rankings` | getrankings — top-99 times for the current map | OK | OK | - | OK | - | live | high |
| `console-commands.replies.records_ladder` | getrecords pages and cross-map getladder *(intended)* | OK | OK | ~ | ~ | - | live | high |
| `console-commands.replies.maplists_monsters` | getmaplist (printmaplist) / getlsmaps (lsmaps) / getmonsterlist reply builders *(intended)* | ~ | OK | - | ~ | - | live | high |
| `console-commands.replies.infra` | Reply-cache infrastructure (init-time precompute, print_to delivery) — coverage *(intended)* | OK | - | - | - | - | live | high |
| `console-commands.server.common_qh_infra` | server/command/common.qh — COMMON_COMMANDS registry macro + timeout/adminnick autocvar + client-verification declarations (coverage) *(intended)* | OK | - | - | - | - | live | high |
| `console-commands.local.balance_diagnostics` | Local balance exports and entity and cvar diagnostics | ~ | OK | - | - | - | live | high |
| `console-commands.local.entity_debug_label` | Local aimed entity diagnostic (sv_show_entnum) *(intended)* | OK | OK | OK | OK | - | live | high |

### `minigames` (misc) — 31 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `minigames.framework.registry` | Minigame registry + descriptor lookup (REGISTER_MINIGAME, minigame_get_descriptor) *(intended)* | OK | ~ | - | - | - | live | high |
| `minigames.framework.tile_math` | Tile-name math (letter/number/pos/buildname/relative_tile/tile_name, next/prev team) | OK | OK | - | - | - | live | high |
| `minigames.framework.msle_networking` | Minigame entity networking (msle_spawn/MSLE table, minigame_SendEntity/CheckSend/resend, ENT_CLIENT_MINIGAME read) *(intended)* | ~ | - | OK | - | - | live | high |
| `minigames.framework.infra_files` | coverage: build-glue includes (_mod.inc/_mod.qh, per-game one-line .qh headers, panel decl header) *(intended)* | - | - | - | - | - | - | high |
| `minigames.sv.session_lifecycle` | Session lifecycle (start/join/part/end/end_minigames, addplayer/rmplayer, sessions list) | OK | OK | - | - | - | live | high |
| `minigames.sv.observer_forcing` | Minigame players forced to observer/spectator (sv_minigames_observer, player_clear_minigame movetype restore) | OK | OK | - | - | - | DEAD | high |
| `minigames.sv.client_command` | 'cmd minigame' dispatcher (create/join/list/list-sessions/end/part/invite + usage + playban gate) | OK | OK | - | - | - | live | high |
| `minigames.sv.invite` | Invite a player (validation chain + INFO_MINIGAME_INVITE notification to the target) | ~ | OK | - | MISS | - | live | high |
| `minigames.sv.impulse` | MinigameImpulse (impulses forwarded to the active game) | ~ | - | - | - | - | live | medium |
| `minigames.cl.session_activation` | Client session activate/deactivate (active_minigame, minigame_self, local-slot detection, autoclean) *(intended)* | OK | - | - | OK | - | live | high |
| `minigames.cl.cmd_forwarding` | minigame_cmd (client -> 'cmd minigame ...' command channel) + session list tracking | OK | - | - | - | - | live | high |
| `minigames.cl.turn_prompt` | 'It's your turn' prompt (minigame_prompt -> HUD notify push with the game icon) | MISS | - | - | MISS | - | - | high |
| `minigames.cl.hud_command_toggle` | HUD 'minigame' console command (menu toggle mutator hook, demo-mode gate) *(intended)* | ~ | - | - | ~ | - | live | high |
| `minigames.cl.draw_helpers` | Shared draw helpers (wrapped/truncated strings, centered pics, skin texture resolution, spectator list) | ~ | ~ | - | ~ | - | live | high |
| `minigames.hud.menu` | Minigame menu behavior (Create/Join/Current Game trees, per-game custom entries, Quit, Invite submenu) | ~ | OK | - | ~ | - | live | high |
| `minigames.hud.board_input` | Board input routing (mouse move/click into game events, menu keyboard navigation, key fallthrough rules) | ~ | - | - | ~ | - | live | high |
| `minigames.game.ttt.rules` | Tic Tac Toe rules (place/win/draw/next-match/singleplayer AI) *(intended)* | OK | OK | - | - | - | live | high |
| `minigames.game.ttt.presentation` | Tic Tac Toe board/status draw (piece pics, hover ghost, winglow, score column, turn messages) | ~ | - | - | ~ | - | live | high |
| `minigames.game.c4.rules` | Connect Four rules (gravity drop, 4-in-a-row win, draw at 42) | OK | OK | - | - | - | live | high |
| `minigames.game.c4.presentation` | Connect Four board draw (under/over board layers, column hover ghost, winglow/dim) | ~ | - | - | ~ | - | live | high |
| `minigames.game.pong.rules` | Pong simulation (ball physics, paddle drive, goals/own-goals, join-as-paddle, AI paddles, part->AI swap) | OK | ~ | ~ | - | - | live | high |
| `minigames.game.pong.presentation` | Pong board draw (ball + team-glow sprites, rotated paddle pics, score rows, wait message) | ~ | - | - | ~ | - | live | high |
| `minigames.game.nmm.rules` | Nine Men's Morris rules (place/move/fly/take phases, mills, win conditions) | ? | ? | - | - | - | live | medium |
| `minigames.game.nmm.presentation` | Nine Men's Morris client view (tile graph draw, selection/available highlights, home/dead piece rows) | MISS | - | - | MISS | - | - | high |
| `minigames.game.pp.rules` | Push-Pull rules (adjacent placement chain, captures, disabled pieces, surround end, score win, rematch) | ? | ? | - | - | - | live | medium |
| `minigames.game.pp.presentation` | Push-Pull client view (current-piece splat marker, valid-move ghost, team-tinted pieces, scores) | ~ | - | - | ~ | - | live | medium |
| `minigames.game.ps.rules` | Peg Solitaire rules (cross board, jump-capture, win-on-empty vs no-moves draw) | ? | OK | - | - | - | live | medium |
| `minigames.game.ps.presentation` | Peg Solitaire client view (selection/available highlights, ghost landing peg, remaining count) | ~ | - | - | ~ | - | live | high |
| `minigames.game.bd.rules` | Bulldozer rules (dozer push, boulder-on-target win, level load/save, editor, next-level chain) | ~ | ~ | - | - | - | ~ | high |
| `minigames.game.bd.presentation` | Bulldozer client view (tile pics, rotated dozer, boulder-on-target swap, editor cursor/palette, win banner) | MISS | - | - | MISS | - | - | high |
| `minigames.port.snake` | coverage: port-only Snake minigame (no Base counterpart) *(intended)* | - | - | - | - | - | live | high |

### `monster-framework` (monster) — 17 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `monster-framework.spawn.master_switch` | Master switch + validity + skill cull (Monster_Spawn front) | OK | OK | - | - | - | live | high |
| `monster-framework.spawn.setup` | Field stamping + defaults (Monster_Spawn_Setup / Setup) | OK | OK | OK | OK | OK | live | high |
| `monster-framework.spawn.miniboss` | Miniboss setup (Monster_Miniboss_Setup) | OK | OK | - | OK | - | live | high |
| `monster-framework.lifecycle.appear_use_touch` | Appear / Use / Touch acquisition (Monster_Appear/Use/Touch) | OK | OK | OK | - | - | live | high |
| `monster-framework.target.validtarget` | Target validity (Monster_ValidTarget) | OK | OK | - | - | - | live | high |
| `monster-framework.target.find_enemycheck` | Target acquisition + enemy retention (Monster_FindTarget / Monster_Enemy_Check) | OK | OK | OK | - | OK | live | high |
| `monster-framework.move.brain` | Movement brain (Monster_Move / Move_Target / WanderTarget) | ~ | OK | ~ | - | OK | live | high |
| `monster-framework.move.danger` | Edge/lava danger avoidance (Monster_CheckDanger) | OK | OK | - | - | - | live | high |
| `monster-framework.attack.dispatch` | Attack gating + dispatch (Monster_Attack_Check) | OK | OK | OK | - | OK | live | high |
| `monster-framework.attack.primitives` | Melee / leap / projectile primitives (Monster_Attack_Melee/Leap + projectile spawns) | OK | OK | OK | - | OK | live | high |
| `monster-framework.damage.pain_death` | Pain / death / corpse (Monster_Damage / Monster_Dead / Monster_Dead_Damage) *(intended)* | OK | OK | OK | - | ~ | live | high |
| `monster-framework.damage.loot_drop` | Loot drop on death (monster_dropitem) | OK | OK | OK | - | - | live | high |
| `monster-framework.move.straight_2d` | 2D straight-line mover (Monster_Move_2D) | OK | OK | OK | - | - | - | high |
| `monster-framework.lifecycle.respawn_reset` | Respawn + round-restart reset + scoring (Monster_Respawn / Monster_Reset / score) | OK | OK | OK | - | - | live | high |
| `monster-framework.spawn_drivers.stats` | Spawn drivers + monster_spawner + map-stats (spawnmonster / monster_spawner / monsters_setstatus) | OK | OK | - | - | - | live | high |
| `monster-framework.presentation.team_colors` | Team / skill colors + radar (monster_setupcolors / monster_changeteam) | OK | OK | - | OK | - | ~ | high |
| `monster-framework.presentation.healthbar_sounds_anim` | Presentation: healthbar sprite + monster sounds + CSQC anim *(intended)* | OK | OK | ~ | ~ | ~ | live | high |

### `monster-golem` (monster) — 12 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `monster-golem.spawn.setup` | Spawn + mr_setup (health/ranges/speeds/loot/spawn-shield/hitbox) | OK | OK | OK | - | - | live | high |
| `monster-golem.spawnfunc.alias` | monster_golem spawnfunc + monster_shambler compat alias (live map placement) | OK | OK | - | - | - | live | high |
| `monster-golem.brain.think` | Per-frame brain: enemy acquire, move, attack dispatch | OK | OK | OK | - | - | live | high |
| `monster-golem.attack.melee_combo` | Melee combo: 1-3 claw swings 0.5s apart | OK | OK | OK | OK | OK | live | high |
| `monster-golem.attack.smash` | Ranged ground-smash: leap + radius AoE in front | OK | OK | OK | OK | OK | live | high |
| `monster-golem.attack.lightning` | Ranged lightning chunk: bouncing shootable projectile, 5s fuse | OK | OK | OK | ~ | OK | live | high |
| `monster-golem.attack.lightning_explode` | Lightning detonation: small blast + chained zaps to wide radius | OK | OK | OK | OK | OK | live | high |
| `monster-golem.pain` | Pain reaction (mr_pain): pain window + pain animation | OK | OK | OK | OK | OK | live | high |
| `monster-golem.death` | Death (mr_death + Monster_Dead): corpse, loot, scoring, death anim | OK | OK | OK | OK | OK | live | high |
| `monster-golem.audio.voice_cues` | Monster_Sound voice cues (idle/sight/melee/spawn/pain/death) + antilag throttle | OK | OK | OK | - | ~ | live | high |
| `monster-golem.anim_frames` | Animation frame-group table (mr_anim) for .dpm playback | - | OK | ~ | ~ | - | live | high |
| `monster-golem.menu.describe` | Monsterpedia describe() flavor text (MENUQC) | - | OK | - | MISS | - | DEAD | high |

### `monster-mage` (monster) — 11 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `monster-mage.spawn.setup` | Spawn + mr_setup (health, speeds, loot, size, attackfunc) | OK | OK | OK | - | OK | live | high |
| `monster-mage.attack.select` | Attack selection (melee push vs ranged teleport/spike) *(intended)* | OK | OK | OK | - | - | live | high |
| `monster-mage.attack.spike` | Homing electric spike (one in flight, seeker guidance) | OK | OK | OK | OK | OK | live | high |
| `monster-mage.attack.push` | Explosive close-range push (AoE shove) | OK | OK | OK | OK | OK | live | high |
| `monster-mage.attack.teleport` | Teleport behind / random-relocate near the target | OK | OK | OK | OK | OK | live | high |
| `monster-mage.defend.shield` | Self damage-blocking shield | OK | OK | OK | - | OK | live | high |
| `monster-mage.defend.heal` | Radius heal of self/allies (skin variants: health/ammo/armor) | OK | OK | OK | OK | OK | live | high |
| `monster-mage.think.decide` | Per-think heal/shield decision (mr_think) | OK | OK | OK | - | - | live | high |
| `monster-mage.death.pain` | Pain reaction + death anim (mr_pain / mr_death) | OK | OK | OK | OK | OK | live | high |
| `monster-mage.anim.frames` | Animation frame groups (mr_anim) | OK | OK | ? | OK | - | live | high |
| `monster-mage.menu.describe` | MENUQC monster-tooltip description text | - | OK | - | ~ | - | DEAD | high |

### `monster-spider` (monster) — 17 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `monster-spider.identity.registration` | Spider monster type registration + identity (model, name, hitbox, spawnflags) | OK | ~ | - | OK | - | live | high |
| `monster-spider.spawn.map_placement` | monster_spider map spawnfunc (Monster_Spawn this true MON_SPIDER) | OK | OK | OK | - | - | live | high |
| `monster-spider.setup.mr_setup` | mr_setup: health / speeds / loot / damageforcescale defaults | OK | OK | - | - | - | live | high |
| `monster-spider.attack.bite` | Melee bite (Monster_Attack_Melee, DEATH_MONSTER_SPIDER) | OK | OK | OK | ? | OK | live | high |
| `monster-spider.attack.web_projectile` | Ranged web projectile (bouncing plasma, no direct damage) | OK | OK | OK | OK | OK | live | high |
| `monster-spider.web.applies_webbed` | Web explosion applies STATUSEFFECT_Webbed in radius (except spiders) | OK | OK | OK | - | - | live | high |
| `monster-spider.web.player_slow` | Webbed PLAYER move-speed slow (spiderweb PlayerPhysics_UpdateStats: HIGHSPEED *= 0.5) | OK | OK | OK | - | - | live | high |
| `monster-spider.web.monster_slow` | Webbed MONSTER move-speed slow (spiderweb MonsterMove hook: run/walk *= 0.5) *(intended)* | OK | OK | OK | - | - | live | high |
| `monster-spider.web.electro_impact_fx` | Web explosion EFFECT_ELECTRO_IMPACT particle | - | - | - | OK | - | live | high |
| `monster-spider.attack.web_sound` | Web fire sound (electro_fire2) | OK | OK | OK | - | OK | live | high |
| `monster-spider.attack.player_wielded` | Player-wielded SpiderAttack weapon (impulse 9, WEP_FLAG_HIDDEN|SPECIALATTACK) | MISS | MISS | - | - | - | - | high |
| `monster-spider.anim.mr_anim` | DPM frame-group animation map (mr_anim: melee/die/shoot/idle/pain/walk frames) | - | OK | OK | ? | - | live | medium |
| `monster-spider.pain.mr_pain` | Pain reaction + anim (mr_pain: random pain1/pain2, pain_finished) | OK | OK | OK | ? | OK | live | high |
| `monster-spider.death.mr_death` | Death reaction + anim (mr_death: random die1/die2) | OK | OK | OK | ? | OK | live | high |
| `monster-spider.think.brain` | Per-frame brain: enemy acquire, move, attack-check (Monster_Think) | OK | OK | ~ | - | - | live | high |
| `monster-spider.respawn.cycle` | Death fade / respawn cycle (Monster_Dead_Fade, Monster_Dead_Think) | OK | OK | OK | ? | - | live | medium |
| `monster-spider.menu.describe` | MENUQC monster-tooltip description text *(intended)* | - | OK | - | OK | - | live | high |

### `monster-wyvern` (monster) — 17 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `wyvern.identity.spawnflags_hitbox` | Identity, fly spawnflags, asymmetric hitbox | OK | OK | - | - | - | live | high |
| `wyvern.setup.stats` | mr_setup: health, speeds, force scale, loot | OK | OK | - | - | - | live | high |
| `wyvern.attack.dispatch` | Attack: 1s wind-up, ranged+melee both lob a fireball | OK | OK | OK | - | - | live | high |
| `wyvern.fireball.spawn` | Fireball projectile spawn (speed, size, spin, lifetime, muzzle) | OK | OK | OK | - | - | live | high |
| `wyvern.fireball.radiusdamage` | Fireball explosion: radius damage + knockback | OK | OK | - | - | - | live | high |
| `wyvern.fireball.burning` | Fireball ignites everything in radius (burning DoT) | OK | OK | OK | - | - | live | high |
| `wyvern.fireball.explode_effect` | Fireball blast particle (EFFECT_FIREBALL_EXPLODE) | OK | OK | - | ? | - | live | medium |
| `wyvern.fireball.firemine_visual` | In-flight fireball CSQC visual (PROJECTILE_FIREMINE) | OK | - | - | ? | - | live | medium |
| `wyvern.fireball.fire_sound` | Fireball launch sound (electro_fire) | OK | OK | OK | - | OK | live | high |
| `wyvern.brain.think_move` | Per-frame brain: flying chase + attack check | OK | OK | OK | - | - | live | high |
| `wyvern.pain` | Pain reaction (0.5s window, pain anim) | OK | OK | OK | ? | OK | live | medium |
| `wyvern.death` | Death: die anim + random corpse launch scatter | OK | OK | - | ? | - | live | medium |
| `wyvern.deadthink` | Dead-think: swap to anim_die2 once the corpse lands | OK | - | OK | ? | - | live | medium |
| `wyvern.anim.frame_table` | Animation frame groups (mr_anim) | OK | OK | OK | ? | - | live | medium |
| `wyvern.obituary` | Kill notification (was fireballed by a Wyvern) | OK | OK | - | OK | - | live | high |
| `wyvern.spawnfunc` | Map spawnfunc monster_wyvern (+ spawner/random/Invasion) | OK | OK | OK | - | - | live | high |
| `wyvern.menu.describe` | MENUQC monster-tooltip description text | OK | OK | - | OK | - | live | high |

### `monster-zombie` (monster) — 12 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `monster-zombie.identity.def` | Zombie identity / model / bbox / class flags | OK | OK | - | OK | - | live | high |
| `monster-zombie.setup.mr_setup` | Spawn setup: always-respawn at death point, loot, spawn shield, no-push spawn window | OK | OK | ~ | OK | OK | live | high |
| `monster-zombie.think.forcescale_restore` | Per-think: restore knockback scale after spawn animation | OK | OK | OK | - | - | live | high |
| `monster-zombie.attack.melee` | Melee attack: 3-way anim roll + forward traceline damage | OK | OK | OK | OK | OK | live | high |
| `monster-zombie.attack.block` | Defend block: raise armor + freeze when hurt vs healthy enemy | OK | OK | OK | OK | OK | live | high |
| `monster-zombie.attack.leap` | Leap attack: ballistic toss toward enemy when at range | OK | OK | OK | OK | - | live | medium |
| `monster-zombie.attack.leap_touch` | Leap contact: contact damage + knockback, then disarm to stop spam | OK | OK | OK | - | - | live | high |
| `monster-zombie.pain.mr_pain` | Pain reaction: brief pain window + pain anim + (now-silent) hurt sound | OK | OK | OK | OK | OK | live | high |
| `monster-zombie.death.mr_death` | Death: corpse + death anim + death sound + respawn machinery | OK | OK | OK | OK | OK | live | high |
| `monster-zombie.respawn.undead` | Always-respawn at death point (undead): corpse rises unless gibbed | OK | OK | OK | - | - | live | medium |
| `monster-zombie.presentation.animation` | Zombie model animation (mr_anim frame groups) | - | OK | ~ | OK | - | live | high |
| `monster-zombie.menu.describe` | MENUQC monster-tooltip description text | - | OK | - | OK | - | live | high |

### `mutator-bloodloss` (mutator) — 8 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-bloodloss.enable.registration` | Registration + enable cvar (g_bloodloss is both switch and threshold) | OK | OK | - | - | - | live | high |
| `mutator-bloodloss.rot.tick` | Periodic health-rot tick (PlayerPreThink) with randomized cadence | OK | OK | OK | - | - | live | medium |
| `mutator-bloodloss.rot.vehicle_eject` | Eject the player from any vehicle on each rot tick (VHEF_RELEASE) | OK | OK | OK | - | - | live | medium |
| `mutator-bloodloss.move.forced_crouch` | Forced crouch while at/below threshold (PlayerCanCrouch, SVQC) | OK | OK | OK | OK | - | live | high |
| `mutator-bloodloss.move.jump_block` | Cannot jump while at/below threshold (PlayerJump returns true, SVQC) | OK | OK | OK | OK | - | live | high |
| `mutator-bloodloss.threshold.live_reread` | Threshold value tracks the g_bloodloss cvar mid-match | OK | OK | - | - | - | live | high |
| `mutator-bloodloss.presentation.active_mutator_signalling` | Active-mutators serverinfo + scoreboard/HUD signalling (BuildMutatorsString, util MUT_BLOODLOSS row, CSQC mut_set_active) | ~ | OK | - | MISS | - | ~ | high |
| `mutator-bloodloss.menu.dialog` | Multiplayer-create mutators dialog (slider 10-50 + enable checkbox, g_instagib dependency, describe text) | OK | OK | - | ~ | - | live | high |

### `mutator-breakablehook` (mutator) — 5 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `breakablehook.activation.enable_cvar` | Mutator registration + enable via g_breakablehook | OK | OK | OK | - | - | live | high |
| `breakablehook.damage.gate` | Gate damage to the grapple chain (own-hook / disabled zeroing) | OK | OK | OK | - | - | live | high |
| `breakablehook.owner.punish_splash` | Punish hook owner with 5 splash + remove on cross-team break | OK | OK | OK | - | - | live | high |
| `breakablehook.remove.drop_chain` | Remove the hook chain when broken (RemoveHook proxy) *(intended)* | OK | - | OK | - | - | live | medium |
| `breakablehook.cvar.live_reread` | Live per-call cvar read (mid-match toggling of breaking rules) | OK | OK | OK | - | - | live | high |

### `mutator-buffs` (mutator) — 38 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `buffs.core.enable` | Buffs mutator enable (g_buffs) | OK | OK | - | - | - | live | high |
| `buffs.item.spawnfunc` | Map buff pickup item (item_buff_<type> spawnfunc + compat classnames) | OK | OK | OK | ~ | - | live | high |
| `buffs.item.auto_spawn` | Auto-spawn buffs when none exist (g_buffs_spawn_count) | OK | OK | OK | ~ | - | live | high |
| `buffs.item.touch_pickup` | Buff pickup + autoreplace on touch | OK | OK | OK | OK | OK | live | high |
| `buffs.item.needkill_respawn` | Buff item lava/needkill respawn on touch (ITEM_TOUCH_NEEDKILL) | OK | OK | OK | - | OK | live | high |
| `buffs.item.respawn_cooldown` | Buff item cooldown / respawn cycle (buff_Think + buff_SetCooldown) | ~ | OK | ~ | ~ | OK | live | medium |
| `buffs.item.random_location` | Random location / lifetime / randomize-on-reset (buff_Respawn / buff_Reset) | ~ | ~ | ~ | MISS | OK | live | high |
| `buffs.item.new_type_weighting` | Weighted random buff selection (buff_NewType / seencount) | OK | OK | - | - | - | live | high |
| `buffs.item.available` | buff_Available (per-buff enable + exclusions) | OK | OK | - | - | - | live | high |
| `buffs.resistance` | Resistance buff — damage reduction | OK | OK | OK | - | - | live | high |
| `buffs.medic.survive` | Medic buff — survive a fatal hit | OK | OK | OK | - | - | live | high |
| `buffs.medic.regen` | Medic buff — boosted regen / raised max / reduced rot | OK | OK | OK | - | - | live | high |
| `buffs.vampire` | Vampire buff — heal on damage dealt | OK | OK | OK | - | - | live | high |
| `buffs.jump.velocity` | Jump buff — increased jump height | OK | OK | OK | - | - | live | high |
| `buffs.jump.fall_immunity` | Jump buff — fall-damage immunity | OK | OK | OK | - | - | live | high |
| `buffs.bash` | Bash buff — knockback scaling + immunity | OK | OK | OK | - | - | live | high |
| `buffs.disability` | Disability buff — stun on hit (slow movement/attack/monsters) | OK | OK | OK | - | - | live | high |
| `buffs.vengeance` | Vengeance buff — reflect damage to attacker | OK | OK | OK | - | - | live | high |
| `buffs.luck` | Luck buff — chance of a critical hit | OK | OK | OK | - | - | live | high |
| `buffs.ammo` | Ammo buff — unlimited ammo + no reload | OK | OK | OK | - | - | live | high |
| `buffs.magnet` | Magnet buff — auto-collect nearby items | OK | OK | OK | - | - | live | medium |
| `buffs.flight` | Flight buff — crouch-in-air gravity flip | OK | OK | OK | - | - | live | high |
| `buffs.inferno` | Inferno buff — burn-on-hit + fire/lava resistance | OK | OK | OK | ~ | - | live | high |
| `buffs.swapper` | Swapper buff — swap places with nearest enemy | ~ | OK | OK | OK | OK | live | high |
| `buffs.carrier_model` | Carrier buff_model glow | ~ | ~ | OK | ~ | - | live | high |
| `buffs.buff_effect` | Unused buff_Effect helper | - | - | - | - | - | - | high |
| `buffs.waypoint` | Buff item waypoint sprite (WP_Buff radar/HUD) | OK | OK | OK | OK | - | live | high |
| `buffs.drop` | Drop held buff (PlayerUseKey / g_buffs_drop) | OK | OK | OK | - | OK | live | high |
| `buffs.replace_powerups` | Replace map powerups with buffs (FilterItem) | OK | OK | - | ~ | - | live | high |
| `buffs.mutator_string` | Report as active mutator (BuildMutatorsString / PrettyString) | OK | OK | - | - | - | live | high |
| `buffs.notifications` | Buff pickup/loss/drop notifications + sounds | OK | - | OK | OK | OK | live | high |
| `buffs.observer_cleanup` | Strip buff visuals on observer / disconnect | OK | - | - | OK | - | live | high |
| `buffs.port_extra_speed_invisible` | Port-only speed/invisible buff branches (not in QC buff set) *(intended)* | - | - | - | - | - | DEAD | high |
| `buffs.networking` | Buff held-state networking (ENT_CLIENT_STATUSEFFECTS) | OK | OK | OK | - | - | live | high |
| `buffs.defs.registration` | Per-buff class definitions (REGISTER_BUFF: netname + m_skin + m_color for all 13 buffs) | OK | OK | - | - | - | live | high |
| `buffs.defs.compat_mapping` | Q3/QL/WOP compat classname -> buff type mapping (BUFF_SPAWNFUNC_Q3COMPAT per .qh) | OK | OK | - | - | - | live | high |
| `buffs.defs.hud_identity` | Per-buff HUD identity (m_name label + m_icon + m_color on the held-buff powerups-panel entry) | OK | OK | - | OK | - | live | high |
| `buffs.defs.qh_autocvar_fallbacks` | Code-level autocvar fallback defaults (luck.qh =0.15/=3, magnet.qh range_buff =100) | - | OK | - | - | - | live | high |

### `mutator-bugrigs` (mutator) — 7 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-bugrigs.registration.cvars` | Mutator registration + 15 g_bugrigs_* cvar load (bugrigs_SetVars) | OK | OK | - | - | - | live | high |
| `mutator-bugrigs.activation.liveness` | Mutator discovered, applied at server boot, hooks subscribed | OK | - | - | - | - | live | high |
| `mutator-bugrigs.pm_physics.replace_move` | PM_Physics hook fully replaces the move with RaceCarPhysics | OK | OK | OK | - | - | live | high |
| `mutator-bugrigs.player_physics.prevangles` | PlayerPhysics hook stashes prevangles (+ disableclientprediction) *(intended)* | OK | OK | OK | - | - | live | high |
| `mutator-bugrigs.racecar.drive_model` | RaceCarPhysics drive model (steer/accel/friction, planar surface-align vs FLY, body pitch/roll, smoothing) + racecar_angle *(intended)* | OK | OK | OK | ? | - | live | medium |
| `mutator-bugrigs.clientconnect.chase_camera` | Force 3rd-person chase camera on player connect (chase_active 1) *(intended)* | OK | OK | OK | OK | - | live | high |
| `mutator-bugrigs.serverinfo.mutator_strings` | Advertise mutator in active-mutator strings (:bugrigs / , Bug rigs) | OK | OK | - | OK | - | live | high |

### `mutator-campcheck` (mutator) — 10 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `campcheck.mutator.registration` | Mutator enable via expr_evaluate(g_campcheck) + cvar load + activation | OK | OK | - | - | - | live | high |
| `campcheck.check.interval` | Per-frame 2D distance accumulation + interval camp check + bounded damage | OK | OK | OK | - | - | live | high |
| `campcheck.check.vehicle` | Camper in a vehicle takes double damage to the vehicle | OK | OK | - | - | - | ? | medium |
| `campcheck.fight.reset` | Damage_Calculate: fighters credited full distance (never punished mid-fight) | OK | OK | - | - | - | live | high |
| `campcheck.spawn.init` | PlayerSpawn: grace period (interval*2) + reset distance | OK | OK | OK | - | - | live | high |
| `campcheck.notify.centerprint` | 'Don't camp!' centerprint warning | OK | OK | OK | OK | - | live | high |
| `campcheck.death.obituary` | DEATH_CAMP obituary: 'Die camper!' / 'thought they found a nice camping ground' | OK | OK | - | OK | - | live | high |
| `campcheck.realclient.gate` | Real-clients-only / not-typing / not-weapon-locked guard chain | OK | OK | - | - | - | live | high |
| `campcheck.prematch.reset` | Pre-match / round-not-started reset of timer + distance *(intended)* | OK | OK | OK | - | - | live | high |
| `campcheck.lifecycle.misc` | PlayerDies centerprint clear, CopyBody clone origin, BuildMutatorsString advertise *(intended)* | OK | OK | - | OK | - | live | high |

### `mutator-cloaked` (mutator) — 6 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-cloaked.enable.predicate` | Enable predicate (g_cloaked cvar gate + registration/subscription) | OK | OK | - | - | - | live | high |
| `mutator-cloaked.alpha.set_default_alpha` | SetDefaultAlpha override — set player/weapon alpha from g_balance_cloaked_alpha | OK | OK | OK | OK | - | live | high |
| `mutator-cloaked.alpha.default_alpha_consumers` | default_player_alpha / default_weapon_alpha applied at spawn, death, weapon entities, vehicle-exit; networked + rendered | OK | OK | - | OK | - | live | high |
| `mutator-cloaked.report.mutators_pretty_string` | Active-mutators string contribution (', Cloaked' + MUT_CLOAKED bit) | OK | OK | - | OK | - | live | high |
| `mutator-cloaked.menu.metadata` | MENUQC metadata (describe text + create-game checkbox) | OK | OK | - | OK | - | live | high |
| `mutator-cloaked.compose.invisibility_powerup` | Composition with Invisibility powerup (expiry returns to default_player_alpha, i.e. cloaked alpha under cloaked) | OK | OK | - | OK | - | live | high |

### `mutator-damagetext` (mutator) — 9 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `damagetext.sv.player_damaged_producer` | Server PlayerDamaged producer (build + queue a damage-number event) | OK | OK | OK | - | - | live | high |
| `damagetext.sv.same_frame_accumulation` | Same-frame multi-hit accumulation (shotgun pellets coalesce onto one number) | OK | OK | OK | - | - | live | high |
| `damagetext.sv.flag_computation` | Wire flag computation (SAMETEAM / BIG_* / NO_ARMOR / NO_POTENTIAL) | OK | OK | - | - | - | live | high |
| `damagetext.sv.first_hit_bitset` | Per-victim first-hit-after-respawn set (dent_attackers -> STOP_ACCUMULATION; spawn/disconnect clears) | OK | OK | - | - | - | live | high |
| `damagetext.net.wire_and_visibility_tiers` | Wire encode/decode + sv_damagetext visibility tiers (spectators/attacker/all) *(intended)* | ~ | ~ | - | ~ | - | ~ | high |
| `damagetext.cl.format_and_size` | Label format tokens + font-size mapping (DamageText_update) | OK | OK | - | OK | - | live | high |
| `damagetext.cl.placement_2d_3d` | 2D-vs-3D placement heuristics (close-range / out-of-view / spectating, overlap stagger) | OK | OK | OK | OK | - | live | medium |
| `damagetext.cl.draw_fade_shrink_move` | Per-frame draw: fade, shrink, world/screen drift, centering, color codes, font | OK | OK | OK | OK | - | live | medium |
| `damagetext.ui.settings_tab` | In-game Damage Text settings menu tab | OK | OK | - | OK | - | live | high |

### `mutator-dodging` (mutator) — 10 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-dodging.core.enable` | Mutator registration + g_dodging enable gate | OK | OK | - | - | - | live | high |
| `mutator-dodging.input.double_tap` | Double-tap detection (PM_dodging_checkpressedkeys) | OK | OK | OK | - | - | live | high |
| `mutator-dodging.input.delay_state_gate` | Delay cooldown + ground/wall/air state gating | OK | OK | OK | - | - | live | high |
| `mutator-dodging.force.scaling` | Speed-to-force scaling (determine_force / map_bound_ranges) | OK | OK | - | - | - | live | high |
| `mutator-dodging.move.ramp_impulse` | Per-frame ramp + one-shot up-impulse (PM_dodging) | OK | OK | OK | - | - | live | high |
| `mutator-dodging.audio.jump_sound` | Dodge jump sound (sv_dodging_sound) | OK | OK | - | - | OK | live | medium |
| `mutator-dodging.anim.jump_action` | Dodge plays the jump animation (ANIMACTION_JUMP) | MISS | - | - | OK | - | live | high |
| `mutator-dodging.lifecycle.reset` | Dodge state reset on spawn + observer (dodging_ResetPlayer) | OK | OK | - | - | - | live | high |
| `mutator-dodging.clientselect.opt_in` | Client opt-in (sv_dodging_clientselect + cl_dodging + REPLICATE) | OK | OK | - | OK | - | live | high |
| `mutator-dodging.frozen.dodge` | Frozen dodging (sv_dodging_frozen / _frozen_doubletap / horiz_force_frozen) | ~ | OK | OK | - | - | live | medium |

### `mutator-doublejump` (mutator) — 4 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `doublejump.enable.cvar_gate` | Enable gate — sv_doublejump cvar gating mutator registration | OK | OK | OK | - | - | live | high |
| `doublejump.stat.per_player_doublejump` | Per-player DOUBLEJUMP stat via Physics_ClientOption | OK | OK | OK | - | - | live | high |
| `doublejump.jump.surface_trace_grant` | PlayerJump hook — near-surface tracebox grants the extra jump | OK | OK | OK | - | - | live | high |
| `doublejump.jump.velocity_clip` | Into-plane velocity clip on grant | OK | OK | OK | - | - | live | high |

### `mutator-dynamic_handicap` (mutator) — 8 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `dynamic_handicap.compute.update_handicap` | Per-player forced-handicap computation from mean SP_SCORE | OK | OK | - | - | - | live | high |
| `dynamic_handicap.compute.clamp` | Handicap clamp to [min, max] with Base's conditional bounds | OK | OK | - | - | - | live | high |
| `dynamic_handicap.apply.damage_scaling` | Forced handicap scales dealt/taken damage in the damage pipeline | OK | OK | OK | - | - | live | high |
| `dynamic_handicap.enable.predicate` | Mutator enable: g_dynamic_handicap AND not CTS/RACE | OK | OK | - | - | - | live | high |
| `dynamic_handicap.triggers.recompute` | Recompute on roster/score change (4 functional hooks in Base) | OK | - | OK | - | - | live | high |
| `dynamic_handicap.apply.invalid_value_guard` | Handicap_SetForcedHandicap value<=0 error guard | OK | - | - | - | - | live | high |
| `dynamic_handicap.present.handicap_level` | handicap_level int networked to client for scoreboard icon color | OK | OK | - | OK | - | live | high |
| `dynamic_handicap.present.mutator_string` | BuildMutatorsString / BuildMutatorsPrettyString entries | OK | OK | - | OK | - | live | high |

### `mutator-globalforces` (mutator) — 6 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-globalforces.register.enable` | Mutator registration + enable/scale cvar (g_globalforces doubles as force multiplier) | OK | OK | - | - | - | live | high |
| `mutator-globalforces.spread.hook` | Knockback spread on PlayerDamage_SplitHealthArmor: push every other in-range player by the damage force | OK | OK | OK | - | - | live | high |
| `mutator-globalforces.spread.noself` | Self-damage skip (g_globalforces_noself) | OK | OK | - | - | - | live | high |
| `mutator-globalforces.spread.range` | Range gate measured from the target (g_globalforces_range) | OK | OK | - | - | - | live | high |
| `mutator-globalforces.spread.selfscale_calcpush` | Per-player push: attacker self-scale + damageforcescale + momentum-clamped calcpush | OK | OK | OK | - | - | live | high |
| `mutator-globalforces.advertise.mutatorstring` | Mutator-list advertisement (':GlobalForces' / ', Global forces') | OK | OK | - | OK | - | live | high |

### `mutator-hook` (mutator) — 10 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-hook.enable.predicate` | Enable predicate (string g_grappling_hook via expr_evaluate) | OK | OK | - | - | - | live | high |
| `mutator-hook.onadd.free_reel` | MUTATOR_ONADD / ONROLLBACK_OR_REMOVE: free reeling when useammo off (WEP_HOOK.ammo_factor 0<->1) *(intended)* | OK | ~ | - | - | - | live | high |
| `mutator-hook.spawn.assign_offhand` | PlayerSpawn: assign the hook as the player's offhand weapon | OK | - | OK | - | - | live | high |
| `mutator-hook.offhand.think_driver` | Offhand think: drive the Hook grapple each frame while +hook is held | OK | - | OK | - | - | live | high |
| `mutator-hook.startitems.fuel` | SetStartItems: grant fuel + FuelRegen when useammo is on | OK | OK | - | - | - | live | high |
| `mutator-hook.filteritem.hide_pickup` | FilterItem: suppress the WEP_HOOK world pickup | OK | - | - | - | - | live | high |
| `mutator-hook.info.mutator_string` | BuildMutatorsString / Pretty: list the mutator in server info / votescreen | OK | OK | - | OK | - | live | high |
| `mutator-hook.client.gameplay_tip` | Client gameplay tip: 'grappling hook is enabled, press <key>' *(intended)* | OK | OK | - | OK | - | live | high |
| `mutator-hook.menu.toggle` | Menu: Grappling Hook mutator checkbox + describe page | OK | - | - | OK | - | live | high |
| `mutator-hook.precedence.blaster_override` | Precedence: offhand_blaster overrides grappling_hook when both enabled | OK | - | OK | - | - | live | high |

### `mutator-instagib` (mutator) — 22 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-instagib.enable.predicate` | Enable predicate + instagib item-list/flag setup (ONADD) | OK | OK | - | - | - | live | high |
| `mutator-instagib.loadout.start_items` | Start loadout (100hp/0armor/cells/Vaporizer-only/unlimited-superweapons) | OK | OK | - | - | - | live | high |
| `mutator-instagib.damage.vaporizer_lives` | Vaporizer hit subtracts an armor 'life', zero damage, lives-remaining centerprint | OK | OK | - | OK | OK | live | high |
| `mutator-instagib.damage.blaster` | Blaster does no damage/force (keepdamage/keepforce/mirror cvars honored) | OK | OK | - | - | - | live | high |
| `mutator-instagib.damage.mirror_as_lives` | Mirror damage costs the attacker an armor 'life' instead of killing | OK | OK | - | OK | OK | live | high |
| `mutator-instagib.damage.fall` | Fall damage never counted | OK | - | - | - | - | live | high |
| `mutator-instagib.damage.friendlyfire` | Friendly-fire (g_friendlyfire==0 + same team) damage nullification | OK | OK | - | - | - | live | high |
| `mutator-instagib.damage.contents` | Lava/slime/drown damage gated by g_instagib_damagedbycontents | OK | OK | - | - | - | live | high |
| `mutator-instagib.damage.friendlypush` | Vaporizer knockback on teammates gated by g_instagib_friendlypush | OK | OK | - | - | - | live | high |
| `mutator-instagib.damage.yoda` | Yoda easter-egg: hitting a partially-transparent player sets the announcer yoda flag | OK | OK | - | OK | OK | live | high |
| `mutator-instagib.death.gib` | Vaporizer death always gibs (damage = 1000) | OK | OK | - | OK | - | live | high |
| `mutator-instagib.regen.disabled` | No health/armor/ammo regeneration | OK | - | - | - | - | live | high |
| `mutator-instagib.regen.split_health_armor` | Armor never absorbs damage (PlayerDamage_SplitHealthArmor: take=damage, save=0) | OK | OK | - | - | - | live | high |
| `mutator-instagib.countdown.bleed` | No-ammo bleed-out countdown (1s cadence, 10/5 dmg, DEATH_NOAMMO, stop conditions) | OK | OK | OK | - | - | live | high |
| `mutator-instagib.countdown.announce` | Countdown announcer + find-ammo centerprints (number ladder, TERMINATED, MULTI variant, DOWNGRADE, clear) | OK | OK | - | OK | OK | live | high |
| `mutator-instagib.spawn.fullbright` | Players glow fullbright on spawn (EF_FULLBRIGHT) | OK | OK | - | ? | - | live | medium |
| `mutator-instagib.items.filter` | FilterItem: convert/replace/remove map weapons, powerups, ammo, jetpacks + random-powerup deck | OK | OK | - | OK | - | live | high |
| `mutator-instagib.items.touch` | ItemTouch: cells pickup full-heals to 100; ExtraLife grants armor lives | OK | OK | - | OK | OK | live | high |
| `mutator-instagib.items.defs` | VaporizerCells / ExtraLife item definitions + spawnfuncs | OK | OK | OK | OK | OK | live | high |
| `mutator-instagib.weapons.throw_arena_forbid` | Forbid Vaporizer throw; weapon-arena off; forbid random start weapons | OK | - | - | - | - | live | high |
| `mutator-instagib.lifecycle.stop_countdown` | Stop the no-ammo countdown on MatchEnd and MakePlayerObserver | OK | - | - | - | - | live | high |
| `mutator-instagib.misc.strings_monsters` | Mutator name/string tags; monster drop = vaporizer cells; Mage skin; random-items pick | OK | OK | - | OK | - | live | high |

### `mutator-invincibleproj` (mutator) — 5 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-invincibleproj.core.register` | Mutator registration + enable predicate (g_invincible_projectiles) | OK | OK | - | - | - | live | high |
| `mutator-invincibleproj.core.editprojectile_zero_health` | EditProjectile: zero a fired projectile's health so it can't be shot down | OK | OK | OK | - | - | live | high |
| `mutator-invincibleproj.string.machine` | BuildMutatorsString — append ':InvincibleProjectiles' to active-mutators token string | OK | OK | - | - | - | live | high |
| `mutator-invincibleproj.string.pretty` | BuildMutatorsPrettyString — append ', Invincible Projectiles' to display string | OK | OK | - | OK | - | live | high |
| `mutator-invincibleproj.client.active_mut_flag` | Client active-mutators detection flag (MUT_INVINCIBLE_PROJECTILES) | MISS | MISS | - | MISS | - | - | high |

### `mutator-itemstime` (mutator) — 10 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-itemstime.set.timed_items` | Tracked timed-item set (powerups + mega/big health+armor + superweapons aggregate) | OK | OK | - | - | - | live | high |
| `mutator-itemstime.producer.times_table` | Server it_times table: min scheduled respawn time per timed item *(intended)* | OK | OK | OK | - | - | live | high |
| `mutator-itemstime.producer.available_encoding` | Negative 'another copy available now' encoding | OK | OK | - | - | - | live | high |
| `mutator-itemstime.net.csqc_sync` | CSQC itemstime net message (per-client respawn-time sync) *(intended)* | OK | OK | OK | OK | - | live | high |
| `mutator-itemstime.producer.per_player_tiers` | Per-player send tiers + observer/spawn/connect sync hooks *(intended)* | OK | OK | OK | - | - | live | high |
| `mutator-itemstime.hud.enable_gate` | Panel enable gate (spectator/warmup/STAT(ITEMSTIME) visibility) | OK | OK | - | OK | - | live | high |
| `mutator-itemstime.net.itemstime_stat` | STAT(ITEMSTIME) client stat (= live sv_itemstime tier) *(intended)* | OK | OK | OK | - | - | live | high |
| `mutator-itemstime.hud.draw` | HUD draw: grid layout, countdown, color/blink, checkmark, progress bar, expanding flash | OK | OK | OK | OK | - | live | medium |
| `mutator-itemstime.hud.feed_host` | HUD feed (CurrentTimes / networked ItemTimes -> ItemsTimePanel each frame) *(intended)* | OK | OK | OK | OK | - | live | high |
| `mutator-itemstime.waypoints.spectator_sprites` | Spectator/warmup respawn waypoint sprites for timed items *(intended)* | OK | OK | OK | OK | OK | live | high |

### `mutator-kick_teamkiller` (mutator) — 7 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-kick_teamkiller.register.enable_gate` | Mutator registered/active only when g_kick_teamkiller_rate > 0 | OK | OK | - | - | - | live | high |
| `mutator-kick_teamkiller.detect.gates` | Entry gates: teamplay only, not warmup, real (non-bot) attacker | OK | - | - | - | - | live | high |
| `mutator-kick_teamkiller.detect.teamkill_score` | Read attacker's accumulated teamkill count (SP_TEAMKILLS) | OK | OK | - | - | - | live | high |
| `mutator-kick_teamkiller.detect.playtime` | Player playtime used as the rate denominator | OK | OK | OK | - | - | live | high |
| `mutator-kick_teamkiller.detect.threshold` | Rate threshold: teamkills >= lower_limit AND teamkills >= rate*playtime/60 | OK | OK | OK | - | - | live | high |
| `mutator-kick_teamkiller.action.punish` | Punishment switch: kick (1) / IP-ban (2) / play-ban+observe (default) | OK | OK | - | - | - | live | high |
| `mutator-kick_teamkiller.notify.broadcast` | Broadcast + center-print notifications on punishment (severity-dependent) | OK | OK | - | OK | - | live | high |

### `mutator-melee_only` (mutator) — 10 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `melee_only.enable.predicate` | Enable predicate (g_melee_only and not instagib/overkill/arena) | OK | OK | - | - | - | live | high |
| `melee_only.loadout.start_items` | Start loadout: Shotgun only, zero shells (SetStartItems) | OK | OK | OK | - | - | live | high |
| `melee_only.loadout.warmup` | Warmup loadout also forced to Shotgun/zero shells (warmup_start_weapons/warmup_start_ammo_shells) | OK | OK | OK | - | - | live | medium |
| `melee_only.loadout.player_spawn_reapply` | Per-spawn loadout re-apply (port-only PlayerSpawn handler) *(intended)* | OK | OK | OK | - | - | live | medium |
| `melee_only.arena.force_off` | Force weapon arena off (SetWeaponArena -> 'off') | OK | OK | - | - | - | live | high |
| `melee_only.weapons.forbid_random_start` | Forbid random start weapons (ForbidRandomStartWeapons -> true) | OK | OK | - | - | - | live | high |
| `melee_only.weapons.forbid_throw` | Forbid throwing/dropping current weapon (ForbidThrowCurrentWeapon -> true) | OK | OK | - | - | - | live | high |
| `melee_only.items.filter_small` | Filter out small health/armor pickups (FilterItemDefinition) | OK | OK | - | - | - | live | high |
| `melee_only.ui.mutator_string_machine` | Machine mutator tag ':MeleeOnly' (BuildMutatorsString) | OK | OK | - | OK | - | live | high |
| `melee_only.ui.mutator_string_pretty` | Pretty mutator label ', Melee only Arena' (BuildMutatorsPrettyString) | OK | OK | - | OK | - | live | high |

### `mutator-midair` (mutator) — 7 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-midair.enable.predicate` | g_midair master enable predicate | OK | OK | - | - | - | live | high |
| `mutator-midair.damage.shield_gate` | Airborne-only damage: zero damage during post-landing shield window | OK | OK | OK | - | - | live | high |
| `mutator-midair.damage.multiplier` | Airborne damage multiplier (g_midair_damagemultiplier) | OK | OK | OK | - | - | live | high |
| `mutator-midair.damage.forcescale` | Airborne knockback-force scale (g_midair_damageforcescale) | OK | OK | OK | - | - | live | high |
| `mutator-midair.ground.shield_and_glow` | On-ground shield arming + EF_ADDITIVE|EF_FULLBRIGHT glow | OK | OK | OK | OK | - | live | high |
| `mutator-midair.bot.disable_bunnyhop` | Disable bot bunnyhopping on spawn (bot_moveskill = 0) | OK | OK | OK | - | - | live | high |
| `mutator-midair.meta.mutator_strings` | BuildMutatorsString / BuildMutatorsPrettyString (":midair" / ", Midair") | OK | OK | - | - | - | live | high |

### `mutator-multijump` (mutator) — 7 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-multijump.enable.registration` | Registration + enable cvar (g_multijump is both switch and extra-jump count) | OK | OK | - | - | - | live | high |
| `mutator-multijump.counter.ground_reset` | Reset the per-air-time extra-jump counter on landing (PlayerPhysics hook) | OK | OK | OK | - | - | live | high |
| `mutator-multijump.jump.rejump_grant` | Midair re-jump grant: ready-gate debounce + count/speed/maxspeed gates + add-vs-set z (PlayerJump hook) | OK | OK | OK | - | - | live | high |
| `mutator-multijump.jump.client_gate` | Per-client cl_multijump opt-in/out/cap with g_multijump_client server default | OK | OK | - | OK | - | live | high |
| `mutator-multijump.jump.dodging_redirect` | Dodging horizontal-velocity redirect toward movement wish on a re-jump (g_multijump_dodging) | OK | OK | OK | - | - | live | medium |
| `mutator-multijump.counter.networking` | multijump_count networked as a stat for client prediction sync | ? | ? | ? | ? | - | ? | low |
| `mutator-multijump.presentation.mutator_list_string` | Active-mutators serverinfo/scoreboard string (:multijump / , Multi jump) | OK | OK | - | OK | - | live | high |

### `mutator-nades` (mutator) — 29 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-nades.enable.predicate` | Enable predicate + offhand assignment (g_nades) | OK | OK | - | - | - | live | high |
| `mutator-nades.throw.input_wire` | Throw input (offhand +hook release / weapon_drop double-press) | OK | OK | OK | - | - | live | high |
| `mutator-nades.prime.select` | nade_prime: type selection (strength / bonus / client-select / cvar) | OK | OK | OK | - | - | live | high |
| `mutator-nades.throw.charge_force` | Charge-throw force ramp + spread cone *(intended)* | OK | OK | OK | - | - | live | high |
| `mutator-nades.toss.projectile` | toss_nade: launch physics (size, newton-style, health, fuse) *(intended)* | OK | ~ | OK | - | - | live | high |
| `mutator-nades.touch.bounce_pickup_detonate` | nade_touch: owner pass-through, pickup, bounce, impact detonate | OK | OK | OK | - | OK | live | high |
| `mutator-nades.damage.launch_destroy` | nade_damage: shoot-to-launch / shoot-to-destroy interactions | OK | OK | OK | - | - | live | high |
| `mutator-nades.boom.dispatch` | nade_boom: type dispatch + destroyed->normal fallback | OK | - | OK | ? | ~ | live | high |
| `mutator-nades.boom.normal` | Normal nade explosion (RadiusDamage) | OK | OK | - | ? | - | live | high |
| `mutator-nades.boom.napalm` | Napalm: fireballs + fountain fire damage | OK | OK | OK | ? | ~ | live | high |
| `mutator-nades.boom.ice` | Ice: freeze field | OK | OK | OK | ? | OK | live | high |
| `mutator-nades.boom.translocate` | Translocate: teleport thrower to detonation | ~ | OK | - | ? | - | live | medium |
| `mutator-nades.boom.spawn` | Spawn: relocate respawn point | OK | OK | - | ? | - | live | high |
| `mutator-nades.boom.heal` | Heal: healing orb (friend heal / foe harm / armor) | OK | OK | OK | ? | - | live | high |
| `mutator-nades.boom.ammo` | Ammo: ammo orb (refill friend / drain foe) | OK | OK | OK | ? | - | live | high |
| `mutator-nades.boom.entrap_veil_darkness` | Entrap / Veil / Darkness orbs + fields | OK | OK | OK | ? | ? | live | high |
| `mutator-nades.boom.monster` | Pokenade: spawn monster | OK | OK | OK | - | - | live | high |
| `mutator-nades.bonus.economy` | Bonus-nade economy (accrual / award / wipe) | ~ | ~ | OK | OK | OK | live | high |
| `mutator-nades.lifecycle.cleanup` | Held-nade/bonus cleanup + spectate copy + vehicle/death-drop toss | OK | - | - | - | - | live | high |
| `mutator-nades.damage.freezetag_revive` | Damage_Calculate: freezetag revive-nade | ~ | OK | OK | - | - | live | high |
| `mutator-nades.presentation.client` | Client presentation: projectile/trail, orb model + 2D flash, darkness overlay, bonus ammo icon, charge ring | ? | ? | ? | ? | ? | live | medium |
| `mutator-nades.display.mutators_string` | Mutator-list display token (BuildMutatorsString / BuildMutatorsPrettyString) | OK | OK | - | OK | - | live | high |
| `mutator-nades.registry.type_catalog` | Nade type catalog (registry, class attribs, projectile ids, per-type enable cvars, small-nade flag) | OK | OK | - | - | - | live | high |
| `mutator-nades.presentation.projectile_trails_wire` | Nade projectile wire ids + per-type/per-team trails + burn-variant swap | ~ | - | - | ~ | - | live | medium |
| `mutator-nades.presentation.explode_effect` | Detonation particle effect name + thrower-color tint (nade_neutral_explode) | - | - | - | ~ | - | live | medium |
| `mutator-nades.select.client_type_replication` | Per-client nade type selection replication (cl_nade_type / cl_pokenade_type) | MISS | MISS | - | - | - | - | high |
| `mutator-nades.net.orb_wire` | Nade orb network entity (type/origin/lifetime/radius wire contract) | ~ | ~ | ~ | - | - | live | medium |
| `mutator-nades.menu.guide_describe` | Menu guide entry + describe text for the Nades mutator | - | - | - | MISS | - | - | high |
| `mutator-nades.bonus.cross_unit_awards` | Cross-gametype bonus-nade awards (g_nades_bonus_score_low / _high API consumers) | ~ | ~ | - | - | - | live | high |

### `mutator-new_toys` (mutator) — 9 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-new_toys.enable.predicate` | Enable predicate (g_new_toys gate + instagib/overkill exclusion + registration/subscription) | OK | OK | - | - | - | live | high |
| `mutator-new_toys.weapons.unblock` | ONADD/ONREMOVE — clear/set WEP_FLAG_MUTATORBLOCKED on the five new-toy weapons | OK | OK | - | - | - | live | high |
| `mutator-new_toys.replace.mapping` | Core->new-toy replacement mapping (nt_GetFullReplacement / nt_GetReplacement) | OK | OK | - | - | - | live | high |
| `mutator-new_toys.startitems.rearrange` | SetStartItems — rearrange the default start-weapon set through the replacement mapping | OK | OK | - | - | - | live | high |
| `mutator-new_toys.mapreplace.set_weaponreplace` | SetWeaponreplace — rewrite a map weapon_* entity's spawn (map "new_toys" key or auto mapping) | OK | OK | OK | OK | - | live | high |
| `mutator-new_toys.pickup.roflsound` | FilterItem — swap new-toy pickup sound to the 'New toys, new toys!' roflsound | OK | OK | - | OK | OK | live | high |
| `mutator-new_toys.menu.describe_page` | MENUQC describe page (mutator info text listing current new-toy weapons + instagib/ok exclusivity note) | OK | OK | - | ~ | - | live | high |
| `mutator-new_toys.menu.create_game_controls` | Create-game menu: New Toys checkbox + Never/Always/Randomly autoreplace radios | OK | OK | - | OK | - | live | high |
| `mutator-new_toys.menu.priority_list_suffix` | Weapon-priority list marks new-toy (mutator-blocked) weapons with a suffix *(intended)* | OK | OK | - | ~ | - | live | high |

### `mutator-nix` (mutator) — 16 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-nix.enable.gate` | Enable predicate (g_nix + arena-mutator exclusion) *(intended)* | OK | OK | - | - | - | live | high |
| `mutator-nix.add.wrinit` | MUTATOR_ONADD: reset rotation globals + wr_init warm of choosable weapons | OK | OK | - | - | - | live | high |
| `mutator-nix.remove.restore` | MUTATOR_ONREMOVE: restore start_ammo_* + start_weapons loadout | OK | OK | - | - | - | live | high |
| `mutator-nix.choose.canchoose` | NIX_CanChooseWeapon (choosable-weapon filter) | OK | OK | - | - | - | live | high |
| `mutator-nix.choose.next` | NIX_ChooseNextWeapon (weighted-random, avoid current) | OK | OK | - | - | - | live | high |
| `mutator-nix.rotation.engine` | NIX_GiveCurrentWeapon rotation clock + per-round resync | OK | OK | OK | - | - | live | high |
| `mutator-nix.ammo.refill_trickle` | Per-weapon ammo wipe/refill + trickle | OK | OK | OK | - | - | live | high |
| `mutator-nix.weaponset.force` | Force owned weapon set + switch each frame | OK | OK | OK | - | - | live | medium |
| `mutator-nix.notify.newweapon` | NIX_NEWWEAPON center notification | OK | OK | OK | OK | - | live | medium |
| `mutator-nix.notify.countdown` | NIX_COUNTDOWN center notification (last 5s) | OK | OK | OK | OK | - | live | medium |
| `mutator-nix.filter.items` | FilterItemDefinition (strip items; keep health/armor/powerups by cvar) *(intended)* | OK | OK | - | - | - | live | high |
| `mutator-nix.forbid.throw` | ForbidThrowCurrentWeapon (no weapon dropping) | OK | - | - | - | - | live | high |
| `mutator-nix.spawn.loadout` | PlayerSpawn loadout override + IT_UNLIMITED_SUPERWEAPONS | OK | OK | - | - | - | live | high |
| `mutator-nix.forbid.randomstart` | ForbidRandomStartWeapons (suppress random start weapons) | OK | - | - | - | - | live | high |
| `mutator-nix.entity.target_items` | OnEntityPreSpawn: delete target_items triggers | OK | - | - | - | - | live | high |
| `mutator-nix.modname.strings` | BuildMutatorsString / BuildMutatorsPrettyString / SetModname (NIX label) | OK | OK | - | - | - | live | high |

### `mutator-offhand_blaster` (mutator) — 8 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `offhand_blaster.enable.register` | Mutator enable via g_offhand_blaster cvar | OK | OK | - | - | - | live | high |
| `offhand_blaster.spawn.assign` | Assign Blaster to player offhand slot on spawn | OK | - | OK | - | - | live | high |
| `offhand_blaster.fire.offhand_think` | Offhand fire: hold +hook to fire a Blaster shot without switching weapons | OK | OK | OK | - | OK | live | high |
| `offhand_blaster.fire.blaster_attack` | Blaster primary projectile fired by the offhand (damage/force/speed) | OK | OK | OK | OK | OK | live | high |
| `offhand_blaster.precedence.over_hook` | Offhand blaster overrides the grappling hook offhand when both enabled | OK | - | - | - | - | live | high |
| `offhand_blaster.report.mutators_string` | Mutator appears in BuildMutatorsString / BuildMutatorsPrettyString | OK | OK | - | OK | - | live | high |
| `offhand_blaster.tips.gameplay_hint` | Client gameplay-tips line announcing the offhand blaster bind | OK | OK | - | OK | - | live | high |
| `offhand_blaster.menu.describe` | Menu mutator-info describe text | OK | OK | - | OK | - | live | high |

### `mutator-overkill` (mutator) — 18 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `overkill.enable.predicate` | Mutator enable predicate (g_overkill + guards) | OK | OK | - | - | - | live | high |
| `overkill.onadd.precache_and_itemblock` | MUTATOR_ONADD: precache ok_player models, set item mutator-block flags, build g_overkill_items list | ~ | OK | - | MISS | - | live | high |
| `overkill.weapons.unblock` | ok_weapons mutator: unblock the five OK weapons (WEP_FLAG_MUTATORBLOCKED) | ~ | ? | - | - | - | ? | low |
| `overkill.blaster.nullify_damage_force` | Damage_Calculate: zero secondary Blaster damage + force *(intended)* | OK | OK | - | - | - | live | high |
| `overkill.loot.drop_player` | ok_DropItem on player death (random loot, launched off corpse) | OK | OK | OK | OK | - | live | high |
| `overkill.loot.drop_monster` | ok_DropItem on monster death (+ suppress normal drop) | OK | OK | OK | OK | - | live | high |
| `overkill.respawn.remember_restore_weapon` | Remember held weapon at death, re-select on respawn (HMG->MG, RPC->Nex) | ~ | OK | OK | - | - | live | high |
| `overkill.loadout.start_items` | SetStartItems: OK weapon set + unlimited ammo | OK | OK | - | - | - | live | high |
| `overkill.countdown.blaster` | PlayerPreThink: allow secondary Blaster during round countdown | OK | - | OK | - | - | live | high |
| `overkill.filter.items_and_powerup_replace` | FilterItem: block normal health/armor + replace Strength/Shield with HMG/RPC superweapons | OK | OK | OK | OK | - | live | high |
| `overkill.randomitems.inject_ok_items` | RandomItems_GetRandomItemClassName: weighted OK item/weapon pool | ~ | OK | - | - | - | ~ | high |
| `overkill.itemwaypoints.respawn` | Item_RespawnCountdown/ScheduleRespawn: timed waypoints for surviving health/armor | OK | OK | OK | OK | - | live | high |
| `overkill.forbid.throw_weapon` | ForbidThrowCurrentWeapon -> true | OK | - | - | - | - | live | high |
| `overkill.forbid.random_start_weapons` | ForbidRandomStartWeapons -> true | OK | - | - | - | - | live | high |
| `overkill.set_weapon_arena_off` | SetWeaponArena -> 'off' | OK | - | - | - | - | live | high |
| `overkill.naming.mod_strings` | Mod-name strings: BuildMutatorsString ':OK', PrettyString ', Overkill', SetModname 'Overkill'; cl g_overkill cvar_settemp | OK | OK | - | OK | - | live | high |
| `overkill.okhmg.nade_selfdamage_scale` | okhmg_nadesupport Nade_Damage: scale OK-HMG nade self-damage to 10% max health *(intended)* | OK | OK | - | - | - | live | high |
| `overkill.oknex.charge_velocity` | oknex_charge GetPressedKeys: accumulate OK-Nex charge proportional to xy velocity *(intended)* | OK | OK | OK | - | - | live | high |

### `mutator-physical_items` (mutator) — 4 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-physical_items.registration.ode_gate` | Mutator registration + ODE-engine availability gate (MUTATOR_ONADD revert) *(intended)* | OK | - | - | - | - | live | high |
| `mutator-physical_items.registration.cvars` | g_physical_items* cvar load + defaults | OK | OK | - | - | - | ~ | high |
| `mutator-physical_items.item_spawn.ghost_entity` | Item_Spawn: spawn physics ghost entity + hide real item | OK | OK | OK | ~ | - | DEAD | high |
| `mutator-physical_items.item_callbacks.think_touch_damage` | physical_item think/touch/damage: reset-on-respawn, hazard/NODROP/SKY reset, alpha follow, delete-when-gone | OK | OK | OK | ~ | - | DEAD | high |

### `mutator-pinata` (mutator) — 8 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-pinata.enable.predicate` | Enable predicate (g_pinata gate + InstaGib/Overkill suppression + registration/activation) | OK | OK | - | - | - | live | high |
| `mutator-pinata.death.scatter_weapons` | PlayerDies burst — scatter every owned-but-not-held throwable weapon as loot | OK | OK | OK | - | - | live | high |
| `mutator-pinata.death.spawn_origin` | Burst spawn origin — CENTER_OR_VIEWOFS(frag_target) (eye height) | OK | OK | - | - | - | live | high |
| `mutator-pinata.death.throw_impulse` | Burst throw impulse — randomvec()*175 + '0 0 325' | OK | OK | - | - | - | live | high |
| `mutator-pinata.death.offhand_slots` | g_pinata_offhand — scatter off-hand (slot>0) weapons when dual-wielding *(intended)* | stub | OK | - | - | - | DEAD | high |
| `mutator-pinata.report.mutators_string` | Active-mutators machine string hook — BuildMutatorsString (':Pinata') | OK | OK | - | - | - | live | high |
| `mutator-pinata.report.mutators_pretty_string` | Active-mutators pretty string hook — BuildMutatorsPrettyString (', Piñata') | OK | OK | - | - | - | live | high |
| `mutator-pinata.menu.metadata` | MENUQC metadata (describe text + create-game checkbox) | OK | OK | - | OK | - | live | high |

### `mutator-powerups` (mutator) — 16 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `powerups.strength.damage_force` | Strength: outgoing damage + force multiplier (self vs others) | OK | OK | - | - | - | live | high |
| `powerups.shield.take_damage_force` | Shield (invincible): incoming damage + force reduction | OK | OK | - | - | - | live | high |
| `powerups.speed.highspeed` | Speed: movement highspeed multiplier | OK | OK | OK | - | - | live | high |
| `powerups.speed.attack_rate` | Speed: weapon attack-rate (refire) multiplier | OK | OK | OK | - | - | live | high |
| `powerups.invisibility.alpha` | Invisibility: player translucency while held | OK | OK | OK | OK | - | live | high |
| `powerups.invisibility.stealth` | Invisibility: radar/monster/bot stealth | OK | - | - | OK | - | live | high |
| `powerups.glow.effects` | Strength/Shield player glow (EF_BLUE / EF_RED + EF_ADDITIVE + EF_FULLBRIGHT) | OK | OK | OK | ~ | - | live | high |
| `powerups.strength.fire_sound` | Strength-fire sound (anti-spammed) on firing while Strength active | OK | OK | OK | - | OK | live | high |
| `powerups.notifications` | Pickup/powerdown notifications (center-print + broadcast) | ~ | - | OK | OK | - | live | high |
| `powerups.countdown_beep` | Powerdown countdown beep (play_countdown SND_POWEROFF) | OK | OK | OK | - | OK | live | high |
| `powerups.item.apply_stack` | Powerup pickup -> status-effect apply + stacking (max vs add) | OK | OK | OK | - | - | live | high |
| `powerups.item.defs_spawn_gate` | Powerup item defs + spawn gating (MUTATORBLOCKED) + jetpack fuel | OK | OK | OK | OK | - | live | high |
| `powerups.drop.ondeath_onuse` | Drop powerup on death / on +use (with countdown waypoint) | OK | OK | OK | OK | - | live | high |
| `powerups.obituary_mutator_strings` | Death-log item codes (S/I) + server-browser mutator strings | OK | - | - | OK | - | live | high |
| `powerups.hud.timer_bars` | HUD active-powerups panel (timer bars + icons) | OK | OK | OK | OK | - | live | medium |
| `powerups.jetpack.start_item_hookreplace` | Jetpack/FuelRegen map-spawn replaced with plain Fuel when already a start item | OK | - | - | - | - | live | high |

### `mutator-random_gravity` (mutator) — 6 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `random_gravity.enable.cvar_gate` | Enable gate — g_random_gravity cvar gating mutator registration + ONADD cvar_settemp | OK | OK | OK | - | - | live | high |
| `random_gravity.roll.startframe` | Per-frame gravity re-roll on SV_StartFrame (formula + clamp + delay schedule) | OK | OK | OK | - | - | live | high |
| `random_gravity.roll.gamestart_gate` | Pre-match suppression gate (time < game_starttime) | OK | - | OK | - | - | live | high |
| `random_gravity.roll.round_gate` | Pre-round-start suppression gate (round active && !round started) | OK | - | OK | - | - | ~ | high |
| `random_gravity.output.sv_gravity_consumption` | sv_gravity output consumed live by the physics integrator | OK | OK | OK | - | - | live | high |
| `random_gravity.advertise.mutator_string` | Mutator-list advertisement (BuildMutatorsString / BuildMutatorsPrettyString) | OK | OK | - | OK | - | live | high |

### `mutator-random_items` (mutator) — 11 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `random_items.gate.enable` | Enable gate + mutator registration (g_random_items || g_random_loot) | OK | OK | - | - | - | live | high |
| `random_items.engine.type_weighted_pick` | Type-weighted classname pick (RandomItems_GetRandomVanillaItemClassName) | OK | OK | - | - | - | live | high |
| `random_items.engine.weapon_prob_cvar` | Per-weapon probability lookup + weapon classname construction | OK | OK | - | - | - | live | high |
| `random_items.engine.item_prob_by_property` | Health/armor/resource/powerup classname pick (GetRandomItemClassNameWithProperty) | OK | OK | - | - | - | live | high |
| `random_items.engine.mod_injection_hook` | Mod-injection hook (RandomItems_GetRandomItemClassName MUTATOR_HOOKABLE; overkill/instagib override) | OK | OK | - | - | - | live | high |
| `random_items.map.filter_replace` | Map-item replacement on spawn (FilterItem -> RandomItems_ReplaceMapItem) | OK | OK | OK | - | - | live | high |
| `random_items.map.item_touched_rerandom` | Re-randomize map item on respawn (ItemTouched -> replace + ScheduleRespawn + delete) | OK | OK | OK | - | - | live | high |
| `random_items.loot.player_dies_count` | Loot count on death: floor(min + random()*max) items at corpse | OK | OK | OK | - | - | live | high |
| `random_items.loot.spawn_item` | Loot-item spawn (RandomItems_SpawnLootItem): classname + spread + lifetime | OK | OK | OK | - | - | live | high |
| `random_items.spawn.recursion_guard_ok_flag` | Spawn-mechanics correctness: random_items_is_spawning recursion guard, ITEM_IS_LOOT skip, ok_item flag | OK | OK | - | - | - | live | high |
| `random_items.string.mutator_labels` | Active-mutators string hooks (BuildMutatorsString / BuildMutatorsPrettyString) | OK | OK | - | OK | - | live | high |

### `mutator-rocketflying` (mutator) — 5 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-rocketflying.register.enable_gate` | Registration + g_rocket_flying enable gate | OK | OK | - | - | - | live | high |
| `mutator-rocketflying.editprojectile.disable_detonate_delay` | EditProjectile: kill rocket/mine remote-detonate delay (instant detonate) | OK | OK | OK | - | - | live | high |
| `mutator-rocketflying.allowrocketjumping.force_on` | AllowRocketJumping: force Devastator remote-jump self-boost on | OK | OK | - | - | - | live | high |
| `mutator-rocketflying.advertise.mutator_string` | BuildMutatorsString + BuildMutatorsPrettyString (advertise active mutator to clients) | OK | OK | - | OK | - | live | high |
| `mutator-rocketflying.menu.describe` | Menu checkbox + description (MutatorRocketFlying.describe) | OK | OK | - | OK | - | live | high |

### `mutator-rocketminsta` (mutator) — 9 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-rocketminsta.enable.registration` | Registration + enable (rides on g_instagib, hooks re-gate on g_rm live toggle) | OK | OK | - | - | - | live | high |
| `mutator-rocketminsta.damage.zero_self_nade` | Damage_Calculate: zero Devastator self/nade damage | OK | OK | - | - | - | live | high |
| `mutator-rocketminsta.damage.zero_laser_self_round` | Damage_Calculate: zero Electro laser self / pre-round damage (g_rm_laser) *(intended)* | ~ | OK | - | - | - | live | high |
| `mutator-rocketminsta.kill.force_gib` | PlayerDies: force gib on Devastator/Electro kills (corpse damage = 1000) | OK | OK | - | - | - | live | high |
| `mutator-rocketminsta.primary.explosion` | Primary rail beam detonates a Devastator-style explosion at the endpoint | OK | OK | OK | OK | OK | live | high |
| `mutator-rocketminsta.secondary.laser_barrage` | Secondary / out-of-cells primary: bouncing RM laser fan + hold-to-stream rapid ramp | OK | OK | OK | OK | OK | live | high |
| `mutator-rocketminsta.laser.projectile_render` | RM laser projectile networking, model, trail, and team color | - | - | - | OK | - | live | high |
| `mutator-rocketminsta.laser.electrobitch` | Electrobitch achievement on flying-enemy laser timeout-explode | OK | - | - | - | OK | live | high |
| `mutator-rocketminsta.instagib.downgrade` | Out-of-ammo downgrade (no instagib death countdown) when g_rm + g_rm_laser | OK | - | OK | OK | - | live | high |

### `mutator-running_guns` (mutator) — 2 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `running_guns.registration.enable` | Mutator registration + g_running_guns enable cvar | OK | OK | - | - | - | live | high |
| `running_guns.alpha.set_default_alpha` | SetDefaultAlpha override — player invisible (alpha -1), weapon visible (alpha +1) | OK | OK | - | OK | - | live | high |

### `mutator-sandbox` (mutator) — 15 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-sandbox.core.registration` | Mutator activation via g_sandbox + onadd autoload | OK | OK | OK | - | - | live | high |
| `mutator-sandbox.object.spawn` | object_spawn — create object entity from a model in front of player | OK | OK | OK | MISS | - | live | high |
| `mutator-sandbox.object.think_grab` | Per-object think: grab-class assignment + owner UID resync + CSQC update | OK | OK | OK | MISS | - | live | high |
| `mutator-sandbox.object.touch_material_fx` | Object touch: material impact particles + impact sounds (velocity-scaled) | OK | OK | OK | OK | OK | live | high |
| `mutator-sandbox.object.edit_get` | Edit-target trace + ownership permission check | OK | OK | - | - | - | live | high |
| `mutator-sandbox.object.edit_properties` | object_edit — skin/alpha/color_main/color_glow/frame/scale/solidity/physics/force/material | OK | OK | - | MISS | - | live | high |
| `mutator-sandbox.object.scale` | Object scale clamp + mins/maxs resize | OK | OK | - | MISS | - | live | high |
| `mutator-sandbox.object.attach` | object_attach get/set/remove — child object attachment to tag/bone | OK | OK | - | OK | - | live | high |
| `mutator-sandbox.object.remove` | object_remove — delete object + detach children + clear selections | OK | - | - | - | - | live | high |
| `mutator-sandbox.object.duplicate` | object_duplicate copy/paste via clipboard cvar | OK | OK | OK | - | - | ~ | high |
| `mutator-sandbox.object.claim_info` | object_claim + object_info object/mesh/attachments | OK | - | - | - | - | live | high |
| `mutator-sandbox.storage.persist` | Per-map object storage: save/load + 5s auto-save + autoload at map start | OK | OK | OK | - | - | live | high |
| `mutator-sandbox.drag.grab` | Drag/grab integration: carry objects with +button8 gated by .grab | ~ | OK | OK | MISS | - | live | high |
| `mutator-sandbox.hooks.gating` | readonly mode + Sandbox_DragAllowed/SaveAllowed/EditAllowed mutator hooks | OK | OK | - | - | - | live | high |
| `mutator-sandbox.menu.tools_dialog` | Sandbox Tools menu dialog (cvar inputs + command buttons) | OK | OK | - | OK | - | live | high |

### `mutator-spawn_near_teammate` (mutator) — 8 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-spawn_near_teammate.registration.cvar_gate` | Mutator registration + cvar gate (expr_evaluate g_spawn_near_teammate) | OK | OK | - | - | - | live | high |
| `mutator-spawn_near_teammate.bias.spawn_score` | Spawn_Score bias: +200 near a living teammate, +100 same-team fallback | OK | OK | - | - | - | live | high |
| `mutator-spawn_near_teammate.bias.lookat_facing` | Spawn facing: aim the spawned player at the chosen teammate | OK | OK | - | OK | - | live | high |
| `mutator-spawn_near_teammate.relocate.eligibility` | Relocate mode: per-teammate eligibility gates + 1-player-team guard | OK | OK | - | - | - | live | high |
| `mutator-spawn_near_teammate.relocate.spot_search` | Relocate mode: 6-offset trace search for a clear floor beside the teammate | OK | OK | - | - | - | live | high |
| `mutator-spawn_near_teammate.relocate.commit_closetodeath` | Relocate mode: pair-wise commit + closetodeath best-spot selection + cooldown | OK | OK | OK | OK | - | live | high |
| `mutator-spawn_near_teammate.teamplay_gate` | Teamplay-only activation (both hooks early-out in FFA) | OK | OK | - | - | - | live | high |
| `mutator-spawn_near_teammate.player_predicate` | Player predicate: IS_PLAYER (classname player) vs the port's FL_CLIENT flag | OK | - | - | - | - | live | medium |

### `mutator-spawn_unique` (mutator) — 5 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-spawn_unique.enable.cvar` | Enable predicate via g_spawn_unique (expr_evaluate) | OK | OK | - | - | - | live | high |
| `mutator-spawn_unique.state.su_last_point` | Per-player last-spawnpoint memory (su_last_point) *(intended)* | OK | - | - | - | - | live | high |
| `mutator-spawn_unique.spawn_score.demote_repeat_spot` | Spawn_Score hook: demote the player's last spawnpoint to priority 0.1 | OK | OK | - | - | - | live | high |
| `mutator-spawn_unique.player_spawn.record_spot` | PlayerSpawn hook: record the spawnpoint just used | OK | - | - | - | - | live | high |
| `mutator-spawn_unique.net.serverside_only` | Server-side only: su_last_point never networked, no CSQC/HUD presence | OK | - | - | - | - | - | high |

### `mutator-stale_move_negation` (mutator) — 5 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `smneg.registration.enable` | Mutator registration + g_smneg enable predicate | OK | OK | - | - | - | live | high |
| `smneg.tunables` | Tunable cvars + defaults (bonus, asymptote, cooldown_factor, start_health) | OK | OK | - | - | - | live | high |
| `smneg.multiplier_curve` | smneg_multiplier(weight) atan/tan damage-scale curve | OK | OK | - | - | - | live | high |
| `smneg.damage_calculate_hook` | Damage_Calculate hook: scale damage+force, accumulate weight, decay other weapons *(intended)* | OK | OK | OK | - | - | live | high |
| `smneg.mutator_string` | BuildMutatorsString / BuildMutatorsPrettyString advertisement | OK | OK | - | OK | - | live | high |

### `mutator-status_effects` (mutator) — 13 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `status_effects.framework.registry` | StatusEffects registry + StatusEffect base class (32 slots, m_id, attribs) | OK | OK | - | - | - | live | high |
| `status_effects.framework.flags_enums` | ACTIVE/PERSISTENT flags + REMOVE_NORMAL/TIMEOUT/CLEAR enums | OK | OK | - | - | - | live | high |
| `status_effects.framework.apply` | StatusEffects_apply (set timer+flags, auto ACTIVE, mark dirty, eff_time<=time guard, per-effect m_apply) | OK | OK | OK | - | - | live | high |
| `status_effects.framework.remove` | StatusEffects_remove (per-effect m_remove, zero timer+flags, removal sound if NORMAL & active & !persistent) | OK | - | - | - | OK | live | high |
| `status_effects.framework.tick` | StatusEffects_tick — per-frame: PERSISTENT recompute, timeout removal, per-effect m_tick (incl. .effects) | OK | OK | OK | OK | - | live | high |
| `status_effects.framework.gettime` | StatusEffects_gettime (timer read with still-active-this-frame clamp) | OK | OK | OK | - | - | live | high |
| `status_effects.framework.persistent` | m_persistent flag (passive grant: no timeout) + per-frame recompute | OK | OK | OK | - | - | live | high |
| `status_effects.framework.removeall_lifecycle` | removeall/clearall on death, disconnect, observer, map reset, PutClientInServer; SpectateCopy alias | OK | - | - | - | OK | live | high |
| `status_effects.net.entity` | ENT_CLIENT_STATUSEFFECTS networking (grouped bitmap + per-effect time+flags) *(intended)* | OK | OK | OK | OK | - | live | high |
| `status_effects.hud.powerups_feed` | Client HUD powerups feed (HUD_Powerups_add -> m_tick -> addPowerupItem) | OK | OK | OK | OK | - | live | high |
| `status_effects.burning` | Burning effect (EF_FLAME, Fire_AddDamage/ApplyDamage, water/frozen extinguish, lava persistence, removal sound) | OK | OK | OK | OK | OK | live | high |
| `status_effects.frozen` | Frozen effect (ice model block, RemoveGrapplingHooks, lava/STAT_FROZEN extinguish, blue overlay) *(intended)* | ~ | ~ | OK | ~ | - | live | medium |
| `status_effects.spawnshield_stunned_superweapon` | SpawnShield (EF_ADDITIVE|FULLBRIGHT), Stunned (EF_SHOCK), Superweapon (persistence + countdown sound) | OK | OK | OK | OK | OK | live | high |

### `mutator-superspec` (mutator) — 11 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `superspec.enable.predicate` | Enable predicate + registration (expr_evaluate(g_superspectate)) | OK | OK | - | - | - | live | high |
| `superspec.state.flag_bitfields` | Per-client option state (.autospec_flags, .superspec_flags, .superspec_itemfilter) *(intended)* | OK | OK | - | - | - | live | high |
| `superspec.spectate.transfer` | Spectate transfer helper (superspec_Spectate wrapping Spectate) *(intended)* | OK | - | - | - | - | live | high |
| `superspec.persistence.options_file` | Per-client options persistence (load on connect / save on disconnect, crypto_idfp-keyed) *(intended)* | OK | OK | - | - | - | live | high |
| `superspec.notify.missing_uid` | Missing-UID notification (delayed-hello think at time+5 -> INFO_SUPERSPEC_MISSING_UID) *(intended)* | OK | OK | OK | OK | - | live | high |
| `superspec.msg.helper` | Spectator message helper (superspec_msg: sprint + conditional centerprint by silent/verbose/spamlevel) | OK | OK | - | OK | - | live | high |
| `superspec.itemfilter.match` | Item-message classname filter (superspec_filteritem; empty = all) | OK | OK | - | - | - | live | high |
| `superspec.itemtouch.autospec_and_msg` | ItemTouch hook: pickup messages + auto-spectate on powerup/mega/flag/item-msg | OK | OK | - | OK | - | live | high |
| `superspec.playerdies.followkiller` | PlayerDies hook: followkiller auto-spectate the attacker | OK | OK | - | OK | - | live | high |
| `superspec.commands.parse` | Spectator console commands (superspec / autospec / superspec_itemfilter / followpowerup / followstrength / followshield) | OK | OK | - | OK | - | live | high |
| `superspec.ui.mutator_strings` | Mutator string tags (:SS machine tag + ', Super Spectators' pretty label) | OK | OK | - | OK | - | live | high |

### `mutator-touchexplode` (mutator) — 8 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `touchexplode.enable.cvar` | Enable predicate (g_touchexplode) | OK | OK | - | OK | - | live | high |
| `touchexplode.scan.prethink` | Per-frame pairwise overlap scan (PlayerPreThink) | OK | OK | OK | - | - | live | high |
| `touchexplode.scan.debounce` | 0.2s per-pair re-trigger debounce | OK | OK | OK | - | - | live | high |
| `touchexplode.blast.radiusdamage` | Midpoint explosion + RadiusDamage | OK | OK | - | - | - | live | high |
| `touchexplode.blast.deathtype` | DEATH_TOUCHEXPLODE death type / obituary | OK | - | - | OK | - | live | high |
| `touchexplode.blast.presentation` | Impact sound + explosion_small particle | OK | OK | - | OK | OK | live | high |
| `touchexplode.list.activemutators` | Active-mutators list entry ('Touch explode') | MISS | - | - | MISS | - | - | high |
| `touchexplode.menu.describe` | Menu mutator label + describe() info-page text | - | - | - | OK | - | live | high |

### `mutator-vampire` (mutator) — 5 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-vampire.enable.gate` | Enable predicate (g_vampire + instagib exclusion) | OK | OK | - | - | - | live | high |
| `mutator-vampire.heal.on_damage` | Heal attacker by damage dealt (PlayerDamage_SplitHealthArmor hook) | OK | OK | OK | - | - | live | high |
| `mutator-vampire.health.cap` | Heal clamped to health limit (GiveResource -> GetResourceLimit) | OK | OK | - | - | - | live | high |
| `mutator-vampire.buff.exclusion` | g_vampire suppresses the BUFF_VAMPIRE pickup | OK | OK | - | - | - | live | high |
| `mutator-vampire.report.mutator_strings` | Mutator-list strings + menu description (':Vampire' / ', Vampire' / MENUQC describe) | - | - | - | OK | - | live | high |

### `mutator-vampirehook` (mutator) — 5 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-vampirehook.enable.gate` | Enable predicate (expr_evaluate g_vampirehook) | OK | OK | - | - | - | live | high |
| `mutator-vampirehook.latch.aiment_follow` | Latched hook follows its toucher (sets .aiment) — the precondition for the drain | OK | OK | OK | - | - | live | high |
| `mutator-vampirehook.drain.engine` | GrappleHookThink drain/heal/self-drain on the hooked player | OK | OK | OK | - | - | live | high |
| `mutator-vampirehook.drain.heal_indirection` | Heal via event_heal/PlayerHeal vs direct GiveResourceWithLimit | OK | OK | - | - | - | live | high |
| `mutator-vampirehook.hitsound.accumulator` | hitsound_damage_dealt accumulator (hit-confirm ding) *(intended)* | MISS | - | - | MISS | - | - | high |

### `mutator-walljump` (mutator) — 9 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-walljump.activation.enable` | Mutator registration + g_walljump enable gate | OK | OK | - | - | - | live | high |
| `mutator-walljump.detect.touchwall` | Nearby-wall detection (4-way tracebox, steep non-NOIMPACT surface) | OK | OK | - | - | - | live | high |
| `mutator-walljump.gate.guards` | Wall-jump eligibility guards (delay, airborne, movetype, jump-tapped, unfrozen, alive) | OK | OK | OK | - | - | live | high |
| `mutator-walljump.impulse.velocity` | Off-wall velocity impulse (horizontal push /xy, z = jumpvel*z, crouch slam) + standard-jump composition | OK | OK | OK | - | - | live | high |
| `mutator-walljump.predict.shared_impulse` | Client-side prediction of the wall-jump impulse (Base runs it shared CSQC+SVQC) | OK | - | - | OK | - | live | high |
| `mutator-walljump.fx.smokering` | Smoke-ring particle at the wall contact point | OK | OK | - | OK | - | live | high |
| `mutator-walljump.fx.sound` | Wall-jump jump voice (PlayerSound playersound_jump) | OK | OK | OK | OK | OK | live | high |
| `mutator-walljump.fx.anim_and_oldvel` | Jump animation action + post-impulse oldvelocity stash | OK | OK | - | MISS | - | live | high |
| `mutator-walljump.menu.describe` | MENUQC mutator description text (Mutators dialog) | - | - | - | OK | - | live | high |

### `mutator-waypoints` (mutator) — 18 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `waypoints.registry.defs` | Waypoint-kind registry (WP_* defs: text/icon/color/blink) | OK | OK | - | OK | - | ~ | high |
| `waypoints.server.manager_api` | Server WaypointSprite_* spawn/update/kill API + manager | OK | OK | OK | - | - | ~ | high |
| `waypoints.gametype.collect` | Per-tick objective waypoint emit (per gametype) | OK | OK | OK | - | - | live | high |
| `waypoints.ctf.flag_markers` | CTF flag waypoint sprites (base/dropped/carrier state machine) | OK | OK | OK | OK | - | live | high |
| `waypoints.deploy.personal_here_danger` | Player-deployed pings (waypoint_personal/here/danger impulses) | OK | OK | OK | - | - | live | high |
| `waypoints.deploy.helpme` | Team HELP-ME attach + ping (waypoint_here_follow) | OK | OK | OK | OK | - | live | high |
| `waypoints.visibility.rules` | Per-peer visibility filter (SPRITERULE_DEFAULT/TEAMPLAY/SPECTATOR + CustomizeWaypoint) | OK | - | - | - | - | live | high |
| `waypoints.visibility.three_image_rule` | TEAMPLAY three-image per-audience swap (netname/2/3) *(intended)* | OK | - | - | OK | - | live | high |
| `waypoints.net.serialize` | Waypoint networking (S2C channel + per-peer filter) *(intended)* | ~ | OK | OK | - | - | live | high |
| `waypoints.client.draw` | 3D in-world sprite draw (icon/text + arrow + project) | OK | OK | OK | ~ | - | live | high |
| `waypoints.client.fades` | Distance / edge / crosshair / lifetime fades + blink | OK | OK | OK | ~ | - | live | high |
| `waypoints.client.cvars_inert` | Client tuning cvars (g_waypointsprite_*, cl_hidewaypoints) wiring | OK | OK | - | OK | - | live | high |
| `waypoints.radar.icons` | Team radar icons + ping ring from waypoint sprites | OK | OK | OK | OK | - | live | high |
| `waypoints.itemstime.respawn_countdown` | Item-respawn countdown waypoints (WP_Item/WP_Weapon + build bar + ping + sound) | OK | OK | OK | OK | ~ | live | high |
| `waypoints.keyhunt.key_markers` | Key Hunt key/carrier waypoint sprites (dropped + carrier state machine) | OK | OK | OK | OK | - | live | high |
| `waypoints.misc.lms_seeker_powerup` | Misc live producers: LMS leader, Seeker tag, powerup-drop countdown | OK | OK | OK | OK | - | live | high |
| `waypoints.weapon.whereis` | 'Where is weapon' spawn markers (g_showweaponspawns / Weapon_whereis) | OK | ~ | ~ | OK | - | live | high |
| `waypoints.monster.healthbar` | Monster floating WP_Monster healthbar marker (g_monsters_healthbars) | OK | OK | OK | ~ | - | live | high |

### `mutator-weaponarena_random` (mutator) — 6 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mutator-weaponarena_random.registration` | Mutator registration + enable predicate *(intended)* | OK | OK | OK | - | - | live | high |
| `mutator-weaponarena_random.setstartitems` | SetStartItems: latch random cvars (gated by g_weaponarena) | OK | OK | OK | - | - | live | high |
| `mutator-weaponarena_random.playerspawn` | PlayerSpawn: replace owned set with random N-subset (keep blaster) | OK | OK | OK | - | - | live | high |
| `mutator-weaponarena_random.givefragsforkill` | GiveFragsForKill: swap culprit weapon for a new random one | OK | OK | OK | - | - | live | high |
| `mutator-weaponarena_random.w_randomweapons` | W_RandomWeapons: uniform pick without replacement *(intended)* | OK | OK | - | - | - | live | high |
| `mutator-weaponarena_random.arena_weaponset_dep` | Base weapon-arena loadout (g_weaponarena -> start_weapons) — dependency *(intended)* | OK | OK | OK | - | - | live | high |

### `net-entity-state` (net) — 24 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `net-entity-state.csqcmodel.change_mask` | csqcmodel SendFlags change-mask delta channel *(intended)* | OK | ~ | OK | - | - | live | high |
| `net-entity-state.snapshot.delta_baseline` | Per-client delta baseline / ack history (DP EntityFrame) *(intended)* | OK | ? | OK | - | - | live | high |
| `net-entity-state.csqcmodel.interpolation` | Two-snapshot origin/angle interpolation + teleport/stall snap | OK | OK | OK | ~ | - | live | high |
| `net-entity-state.csqcmodel.teleport_bit` | Teleport bit cancels interpolation (EF_TELEPORT_BIT) *(intended)* | OK | ~ | OK | OK | - | live | medium |
| `net-entity-state.csqcmodel.force_updates` | Forced periodic origin keepalive (CSQCPLAYER_FORCE_UPDATES) *(intended)* | ~ | MISS | ~ | - | - | - | medium |
| `net-entity-state.entcs.radar_shownames_slice` | ent_cs GPS slice for radar + shownames teammate health/armor *(intended)* | OK | ~ | ~ | OK | - | live | high |
| `net-entity-state.entcs.privacy_mask` | ent_cs public/private mask (radar_showenemies / SAME_TEAM) *(intended)* | OK | ~ | - | - | - | live | high |
| `net-entity-state.wepent.active_weapon` | wepent active/held weapon (local viewmodel + remote third-person) *(intended)* | OK | OK | OK | OK | - | live | high |
| `net-entity-state.wepent.charges_clip_heat` | wepent charge/clip/heat HUD fields (vortex/oknex charge+pool, clip_load/size, hagar_load, minelayer_mines, arc_heat) *(intended)* | OK | OK | OK | OK | - | live | high |
| `net-entity-state.wepent.switch_alpha_misc` | wepent switch/alpha/gunalign/porto/tuba/skin viewmodel fields *(intended)* | ~ | ~ | ~ | ~ | - | ~ | high |
| `net-entity-state.csqcmodel.appearance_hook` | csqcmodel appearance hook (force model/colors, unique enemy colors, death-fade) | OK | OK | - | OK | - | live | high |
| `net-entity-state.replicate.client_cvars` | Client->server cvar replication (REPLICATE / cmd sentcvar) | OK | ~ | OK | - | - | live | high |
| `net-entity-state.csqcmodel.anim_state` | csqcmodel networked anim action state (upper/lower-body action split) *(intended)* | OK | ~ | OK | ~ | - | live | high |
| `net-entity-state.csqcmodel.alpha` | csqcmodel per-entity render alpha (m_alpha / .alpha transparency channel) *(intended)* | OK | OK | OK | OK | - | live | high |
| `net-entity-state.projectile.wire_channel` | CSQCProjectile server wire contract (SendFlags byte: origin/type/silent/no-trail/gravity/fade/onground/clientanimate) *(intended)* | OK | ~ | OK | - | ~ | live | high |
| `net-entity-state.projectile.clientanimate_prediction` | Client-animated projectile motion (velocity/gravity networking + client extrapolation vs two-snapshot interpolation) *(intended)* | OK | ~ | OK | OK | - | live | high |
| `net-entity-state.projectile.fade` | Crylink projectile visual fade *(intended)* | OK | OK | ~ | ~ | - | live | high |
| `net-entity-state.projectile.team_color` | Projectile owner team/clientcolors byte (team-tinted bolt body/trail) | OK | ~ | - | ~ | - | live | medium |
| `net-entity-state.projectile.teleport_resend` | Projectile teleport full-resend + no-trail bit (UpdateCSQCProjectileAfterTeleport, warpzone pass-through) | ~ | ~ | OK | ~ | - | live | high |
| `net-entity-state.wire.message_registry` | Network message-id registries (REGISTER_NET_TEMP/LINKED/C2S, renumbered id spaces, NET_HANDLE guard) *(intended)* | OK | ~ | - | - | - | ~ | high |
| `net-entity-state.wire.c2s_yenc` | Client->server message framing (cmd c2s string tunnel + yEnc byte encoding) *(intended)* | OK | - | OK | - | - | live | high |
| `net-entity-state.wire.primitives` | Wire serialization primitives (WriteInt24_t, WriteApproxPastTime, Writebits, WriteRegistered, ReadCSQCEntity) | OK | OK | - | - | - | ~ | high |
| `net-entity-state.wire.registry_checksum` | Registry-hash compatibility handshake (client/server content sync check at connect) *(intended)* | OK | ~ | OK | - | - | live | high |
| `net-entity-state.stats.stat_registry` | STAT registry server->client stat slots (magic MOVEVARS_* 220-255, FRAGLIMIT 235, TIMELIMIT 236, engine reserve 32) *(intended)* | OK | ~ | OK | - | - | live | high |

### `playerstats-xonstat` (net) — 11 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `playerstats-xonstat.gamereport.accumulator_db` | Game-report event DB (Init / AddPlayer / AddTeam / AddEvent / Event accumulator / Reset_All) | OK | OK | - | - | - | live | high |
| `playerstats-xonstat.gamereport.identity_consent` | Player identity + privacy consent (crypto UID iff cl_allow_uidtracking; _netname iff cl_allow_uid2name; _ranked from cl_allow_uidranking) | ~ | ~ | - | - | - | live | high |
| `playerstats-xonstat.gamereport.event_feeds` | Per-match stat feeds: kills-<victim>, achievements (sprees/firstblood/botlike), total-/scoreboard- score columns, team sections, per-weapon accuracy | ~ | ~ | - | - | - | ~ | high |
| `playerstats-xonstat.gamereport.alivetime` | Alivetime accounting (per-life accumulation + 30s AFK exclusion) | ~ | ~ | OK | - | - | live | high |
| `playerstats-xonstat.gamereport.avglatency` | Average-latency record (avglatency from the 10s latency sampler, reconnect averaging) | OK | MISS | - | - | - | DEAD | high |
| `playerstats-xonstat.report.payload_v9` | V9 report serializer — header/team/player line set (V R G O M I S C U D RP L Q P i n t r e) | ~ | ~ | - | - | - | live | high |
| `playerstats-xonstat.submission.http_upload` | XonStat report submission (HTTPS POST via url_multi_fopen, async DelayMapVote clear, SERVERFLAG_PLAYERSTATS) | MISS | MISS | MISS | - | - | - | high |
| `playerstats-xonstat.playerbasic.skill_fetch` | Server-side player skill fetch (PlayerStats_PlayerBasic: /skill?game_type_cd&hashkey JSON -> SP_SKILL) | MISS | MISS | - | stub | - | DEAD | high |
| `playerstats-xonstat.playerdetail.profile_fetch` | Menu player-profile / rankings fetch (PlayerStats_PlayerDetail -> Profile dialog stats list) | MISS | MISS | MISS | ~ | - | DEAD | high |
| `playerstats-xonstat.urllib.http_client` | urllib async HTTP client (url_single_fopen GET/POST, url_fclose POST-commit, slot pool, url_multi_fopen failover) | MISS | MISS | - | - | - | - | high |
| `playerstats-xonstat.json.parser` | QC JSON parser (json_parse -> flat key-path buffer, json_get) [coverage row] *(intended)* | MISS | - | - | - | - | - | high |

### `fx-deathtypes` (notification) — 11 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `fx-deathtypes.registry.packing` | Deathtype encoding: packed int (weaponmask + HITTYPE bits + DT_FIRST registry) vs string tag *(intended)* | OK | - | - | - | - | live | high |
| `fx-deathtypes.classify.weapon_vs_special` | Weapon-vs-special + armor-pierce/fire/lethal classifiers (DEATH_ISSPECIAL, DEATH_WEAPONOF, etc.) | OK | OK | - | - | - | live | high |
| `fx-deathtypes.classify.category` | Monster/turret/vehicle category (.message string compare; DEATH_ISMONSTER/ISTURRET/ISVEHICLE) | OK | OK | - | OK | - | live | high |
| `fx-deathtypes.message.weapon_select` | Per-weapon kill/suicide message selection (wr_killmessage / wr_suicidemessage, HITTYPE branches) *(intended)* | OK | OK | - | OK | - | live | high |
| `fx-deathtypes.message.special_basic` | Basic environment special-death message mapping (fall/drown/lava/slime/swamp/void/fire/telefrag/buff/noammo) | OK | OK | - | OK | - | live | high |
| `fx-deathtypes.message.special_nade_misc` | NADE-family + cheat/camp/rot/shooting_star/touchexplode special-death messages | OK | OK | - | OK | - | live | high |
| `fx-deathtypes.message.betrayal_suicide_text` | MIRRORDAMAGE -> DEATH_SELF_BETRAYAL and KILL -> DEATH_SELF_SUICIDE self lines (+ CTS /kill suppression) | OK | OK | - | OK | - | live | high |
| `fx-deathtypes.message.teamchange` | TEAMCHANGE / AUTOTEAMCHANGE self obituary + death_team arg + auto no-frag-negation | OK | OK | - | OK | - | live | high |
| `fx-deathtypes.message.mapper_custom` | HURTTRIGGER mapper message (msg_from_ent / DEATH_*_VOID_ENT) + DEATH_CUSTOM (deathmessage) | OK | OK | - | OK | - | ~ | high |
| `fx-deathtypes.special.suicide_special_routing` | Obituary SUICIDE/MURDER/ACCIDENT special-routing (TEAMCHANGE/MIRRORDAMAGE/HURTTRIGGER branch + BOTLIKE achievement) | OK | OK | - | OK | OK | live | high |
| `fx-deathtypes.name.lookup` | Deathtype_Name (int -> name) for logging/debug *(intended)* | - | - | - | - | - | - | high |

### `fx-notifications` (notification) — 16 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `fx-notifications.registry.types` | Notification registry + message types (MSG_ANNCE/INFO/CENTER/MULTI/CHOICE + MSG_CENTER_KILL + server-driven center variants) | OK | OK | - | - | - | live | high |
| `fx-notifications.send.dispatch` | Send_Notification (broadcast, arg-count validation, type switch) | OK | OK | - | - | - | live | high |
| `fx-notifications.net.protocol` | ENT_CLIENT_NOTIFICATION wire protocol (server encode -> client decode) *(intended)* | OK | OK | OK | OK | - | live | high |
| `fx-notifications.net.shouldsend` | Per-recipient broadcast routing (Notification_ShouldSend) | OK | - | - | - | - | live | high |
| `fx-notifications.args.tokens` | Arg-token expansion (NOTIF_ARGUMENT_LIST / Local_Notification_sprintf) | OK | OK | - | OK | - | live | high |
| `fx-notifications.multi.fanout` | MSG_MULTI fan-out to annce/info/center sub-notifications | OK | OK | - | OK | - | live | high |
| `fx-notifications.choice.resolve` | MSG_CHOICE option selection + per-client replication | OK | OK | - | OK | - | live | high |
| `fx-notifications.center.render` | MSG_CENTER -> centerprint (cpid replace/kill, ^COUNT countdown) | OK | OK | OK | OK | - | live | high |
| `fx-notifications.center.durcnt` | Centerprint durcnt (duration/count) + item_centerprinttime | OK | OK | OK | OK | - | live | high |
| `fx-notifications.info.killfeed` | MSG_INFO -> console line + HUD kill-notify (icon, attacker/victim) | OK | OK | - | ~ | - | live | high |
| `fx-notifications.annce.queue` | MSG_ANNCE -> announcer voice queue + anti-spam | OK | OK | OK | OK | OK | live | high |
| `fx-notifications.announcer.timecountdown` | Announcer time/countdown driver (REMAINING_MIN, 3-2-1-prepare, PickNumber) *(intended)* | OK | OK | OK | OK | OK | live | high |
| `fx-notifications.gentle.variants` | Gentle-mode message-variant selection (normal_or_gentle / cl_gentle) *(intended)* | OK | OK | - | OK | - | live | high |
| `fx-notifications.net.kill` | Kill_Notification / MSG_CENTER_KILL (server clears a centerprint group) | OK | OK | OK | OK | - | live | high |
| `fx-notifications.center.title` | Server-driven centerprint title / duel title (centerprint_SetTitle / _SetDuelTitle / _ClearTitle) *(intended)* | OK | OK | OK | OK | - | live | medium |
| `fx-notifications.center.raw` | Raw centerprint(client, text) builtin (private chat/tell, map .message, target_print, MOTD) | OK | OK | OK | OK | - | live | medium |

### `physics-movetypes` (physics) — 14 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `physics-movetypes.dispatch.frame` | Per-tick movetype dispatch + live server entity loop | OK | OK | OK | - | - | live | high |
| `physics-movetypes.flymove.slide` | FlyMove slide-and-step solver (clip planes, crease, gravity half-step) | OK | OK | OK | - | - | live | high |
| `physics-movetypes.clipvelocity` | ClipVelocity (slide off plane with overbounce + STOP_EPSILON snap) | OK | OK | - | - | - | live | high |
| `physics-movetypes.toss.ballistic` | Toss/Fly ballistic move + ground-rest, suspended-corpse rules | OK | OK | OK | - | - | live | high |
| `physics-movetypes.toss.bounce` | Bounce/BounceMissile restitution + bouncestop settle (grenades, casings, balls) | OK | OK | OK | - | - | live | high |
| `physics-movetypes.step.physics` | MOVETYPE_STEP integrator (monster bodies) | OK | OK | OK | - | - | live | high |
| `physics-movetypes.walk.stepmove` | WalkMove slide + explicit stair up/down stepping | OK | OK | OK | - | - | live | high |
| `physics-movetypes.push.pusher` | MOVETYPE_PUSH movers (local-time advance + think) | OK | OK | OK | - | - | live | high |
| `physics-movetypes.push.pushmove` | PushMove rider-carry, rotation, blocked-revert (door/plat crush) | OK | OK | OK | - | - | live | high |
| `physics-movetypes.follow` | MOVETYPE_FOLLOW rigid attach (held flag/key model) | OK | OK | OK | - | - | live | high |
| `physics-movetypes.matchticrate.interp` | MatchTicrate fixed state and residual client presentation *(intended)* | ~ | ~ | ~ | ~ | - | live | high |
| `physics-movetypes.checkwater` | CheckWater / CheckWaterTransition (waterlevel + transition cue) | OK | OK | OK | - | - | live | high |
| `physics-movetypes.pushentity.impact` | PushEntity sweep + Impact dual-dispatch + NudgeOutOfSolid *(intended)* | OK | OK | OK | - | - | live | high |
| `physics-movetypes.player.corpse_hull` | Shrink the authoritative player corpse hull at the end of the death tick | OK | OK | OK | - | - | live | high |

### `physics-player` (physics) — 19 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `physics-player.core.branch_selection` | PM_Main branch selection (waterjump/PM_Physics-hook/fly/swim/ladder/jetpack/ground/air) | OK | - | OK | - | - | live | high |
| `physics-player.ground.friction` | Ground/edge friction (PHYS_FRICTION_REPLICA_DT geometric form) | OK | OK | OK | - | - | live | high |
| `physics-player.ground.accelerate` | Ground accelerate (simple Quake accel, slick variant) | OK | OK | OK | - | - | live | high |
| `physics-player.air.qw_accelerate` | Air QW accelerate (PM_Accelerate: airaccel_qw clamp/stretch + sideways friction) | OK | OK | OK | - | - | live | high |
| `physics-player.air.strafe_blends` | Air strafe blends + airstop + airstrafeaccel_qw (GeomLerp/strafity) | OK | OK | OK | - | - | live | high |
| `physics-player.air.cpm_aircontrol` | CPM air control (CPM_PM_Aircontrol: curve momentum toward wishdir) | OK | OK | OK | - | - | live | high |
| `physics-player.air.warsowbunny` | Warsow-bunny air accel (PM_AirAccelerate) | OK | OK | OK | - | - | live | high |
| `physics-player.jump.playerjump` | PlayerJump (guards, jumpspeedcap, landing friction, jump velocity) | OK | OK | OK | - | OK | live | high |
| `physics-player.water.swim` | Swimming (water friction/accel, hold-jump rise, dive, frozen-resurface) | OK | OK | OK | - | - | ? | medium |
| `physics-player.ladder` | Ladder climb (gravity-free, func_water override) | OK | OK | OK | - | - | ? | medium |
| `physics-player.jetpack` | Jetpack thrust (PM_jetpack closed-form, fuel drain, pauseregen) | OK | OK | OK | - | - | ? | medium |
| `physics-player.crouch` | Crouch hull resize + view offset (PM_ClientMovement_UpdateStatus) | OK | OK | OK | OK | - | live | high |
| `physics-player.integrator.walkmove_flymove` | Slide-and-step collision integrator (SV_WalkMove / SV_FlyMove + stair step/down) *(intended)* | OK | OK | OK | - | - | live | high |
| `physics-player.movement_sounds` | Footsteps + landing sounds (PM_Footsteps / PM_check_hitground) *(intended)* | OK | OK | OK | - | OK | live | high |
| `physics-player.spectator_control` | Spectator free-flight speed ladder (sys_phys_spectator_control) *(intended)* | OK | OK | OK | - | - | live | high |
| `physics-player.specialcommand` | PM_check_specialcommand (xwxw... button cheat-code -> give-all) | OK | OK | OK | - | - | live | high |
| `physics-player.punch_decay` | View-punch decay (PM_check_punch: punchangle/punchvector bleed) | OK | OK | OK | - | - | live | high |
| `physics-player.pregame_hold` | Pre-match / game-stopped movement freeze (sys_phys_pregame_hold) *(intended)* | OK | - | OK | - | - | live | high |
| `physics-player.landing_event` | Landing event (sys_phys_land: jumppadcount reset + speedmeter/combo logs) *(intended)* | OK | - | OK | - | - | live | high |

### `scoring` (scoring) — 22 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `scoring.registry.fields_and_flags` | SP_* score-field registry + SFL_* sort/display flags | OK | OK | - | - | - | live | high |
| `scoring.rules.scorerules_basics` | Per-match column setup (ScoreRules_basics: blank then re-declare common columns) | OK | OK | - | - | - | live | high |
| `scoring.player.add_set` | Per-player score add/set (PlayerScore_Add/_Set + AddPlayerScore/AddedPlayerScore hooks + game_stopped clamp) | OK | OK | - | - | - | live | high |
| `scoring.team.two_slot_model` | Two-slot per-team score model (TeamScore_AddToTeam / teamscorekeepers / ST_SCORE + per-mode slot) | OK | OK | - | - | - | live | high |
| `scoring.compare.field_player_team` | Comparison + sort (ScoreField_Compare / PlayerScore_Compare / TeamScore_Compare / PlayerScore_Sort) | OK | OK | - | - | - | live | high |
| `scoring.winningcondition.scores` | Winning-condition reduction (WinningConditionHelper / WinningCondition_Scores / GetWinningCode) | OK | OK | OK | - | - | live | medium |
| `scoring.winningcondition.remaining_frags_announce` | Remaining-frags announcer (Scores_CountFragsRemaining -> REMAINING_FRAG_{1,2,3}) | OK | OK | ~ | - | OK | live | high |
| `scoring.lifecycle.clear` | Score clearing (PlayerScore_Clear via g_score_resetonjoin / Score_ClearAll) | OK | OK | - | - | - | live | high |
| `scoring.display.scorestring` | Scoreboard value formatting (ScoreString / count_ordinal / mmssth / TIME_ENCODE) | OK | OK | - | OK | - | live | high |
| `scoring.net.replication` | Score networking (ScoreInfo layout + Scoreboard values, change-gated, int24) *(intended)* | OK | OK | OK | - | - | live | high |
| `scoring.report.playerstats` | End-of-match XonStat game report (PlayerStats_GameReport pipeline, V9) *(intended)* | OK | OK | OK | - | - | live | high |
| `scoring.helper.float2int_decimal_carry` | Fractional-score accumulator (_GameRules_scoring_add_float2int decimal carry) | OK | OK | OK | - | - | live | high |
| `scoring.lifecycle.game_stopped` | game_stopped score clamp (drop score additions post-match/warmup-end) | OK | OK | OK | - | - | live | high |
| `scoring.display.niceprint` | Server `teamstatus` console standings dump (Score_NicePrint) | OK | OK | - | OK | - | live | high |
| `scoring.display.scorelog` | Server score-log / event-log standings dump (DumpStats / GetPlayerScoreString / GetTeamScoreString) *(intended)* | OK | OK | OK | OK | - | live | high |
| `scoring.accuracy.tracking` | Per-weapon accuracy tallies (accuracy_add: fired/hit/real damage + once-per-frame cnt counters + isgooddamage/canbegooddamage gates + per-weapon frag credit) | OK | OK | OK | - | - | live | high |
| `scoring.accuracy.networking` | Accuracy networking to the scoreboard/weapons HUD (ENT_CLIENT_ACCURACY per-weapon byte array, change-gated; data-share to spectators/others) | ~ | OK | OK | OK | - | live | high |
| `scoring.accuracy.lifecycle` | Accuracy lifecycle (accuracy_init/free on connect/disconnect, accuracy_reset, accuracy_resend force-resend) | OK | OK | - | - | - | live | high |
| `scoring.accuracy.playerstats_export` | Per-weapon accuracy export to the XonStat game report (acc-<weapon>-{fired,hit,real,cnt-hit,cnt-fired,frags} events) | OK | OK | - | - | - | DEAD | high |
| `scoring.player.attach_detach` | Scorekeeper attach/detach lifecycle (PlayerScore_Attach on connect / PlayerScore_Detach on disconnect) | OK | - | - | - | - | live | high |
| `scoring.status.getstatus_strings` | getstatus/rcon standings strings (worldstatus/clientstatus built by WinningConditionHelper, gated by g_full_getstatus_responses) | MISS | MISS | - | - | - | - | high |
| `scoring.rules.scorerules_basics_end` | Column-declaration finalize (ScoreRules_basics_end -> ScoreInfo_Init(teamplay_bitmask): broadcast the new layout + init teams via NumTeams) *(intended)* | OK | - | - | - | - | live | medium |

### `mapinfo` (server) — 21 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mapinfo.parse.file` | .mapinfo file parsing (title/description/author/gametype/type/size/hidden lines) | ~ | OK | - | - | - | live | high |
| `mapinfo.parse.type_string` | Gametype token resolution + deprecated-name remaps (MapInfo_Type_FromString) | ~ | OK | - | - | - | ~ | high |
| `mapinfo.gametype.registry` | Gametype registry metadata (netname/short name/pretty name/description/flags: teamplay, usepoints, preferred/priority, hidelimits, weaponarena, 1v1) | ~ | OK | - | OK | - | live | high |
| `mapinfo.gametype.defaults` | Per-gametype mapinfo defaults + per-map key=value overrides (_MapInfo_Map_ApplyGametypeEx: timelimit/leadlimit/fraglimit reset, pointlimit/lives/laplimit/caplimit/teampointlimit aliases, m_parse_mapinfo hooks) | ~ | ~ | - | - | - | live | high |
| `mapinfo.generate.entity_scan` | Native mapinfo autogeneration from the BSP entity lump or mounted .ent *(intended)* | ~ | ~ | - | OK | ~ | live | high |
| `mapinfo.generate.always_supported` | Generation-time support heuristics (m_isAlwaysSupported and m_isTwoBaseMode) | OK | OK | - | - | - | live | high |
| `mapinfo.forced_supported` | m_isForcedSupported dispatch (Duel, TDM, Mayhem, Team Mayhem, Team Keepaway, Survival) | ~ | OK | - | - | - | live | high |
| `mapinfo.arena.q3compat` | .arena/.defi Q3 compat parsing (_MapInfo_ParseArena, _MapInfo_FindArenaFile, g_mapinfo_q3compat modes) *(intended)* | OK | OK | - | OK | - | live | high |
| `mapinfo.enumerate.filter` | Map enumeration + gametype/string filtering for the menu list (MapInfo_Enumerate, _MapInfo_FilterGametype, MapInfo_FilterString, Get_ByID/BSPName_ByID) | ~ | - | - | OK | - | live | high |
| `mapinfo.flags.hidden_forbidden` | Map flags (hidden/forbidden/frustrating/donotwant, gameversion_min) + ForbiddenFlags/RequiredFlags cvar gates | ~ | OK | - | - | - | live | medium |
| `mapinfo.features.has` | Map features (has weapons/turrets/vehicles/monsters) + MapInfo_CurrentFeatures requirement mask | OK | OK | - | - | - | live | high |
| `mapinfo.check.checkmap` | MapInfo_CheckMap: can this map be played with the current gametype+features (rotation build, vote-map validation, suggestmap, lsmaps) | ~ | - | - | - | - | live | high |
| `mapinfo.load.map_settings` | MapInfo_LoadMapSettings at worldspawn: gametype-support check + fallback (random supported mode weighted by m_priority; g_mapinfo_allow_unsupported_modes_and_let_stuff_break; total-failure preferred-mode fallback) | ~ | MISS | - | - | - | live | high |
| `mapinfo.load.loadmap` | MapInfo_LoadMap: settemp restore + map/changelevel dispatch *(intended)* | ~ | - | - | - | - | live | high |
| `mapinfo.name.find_fixname` | MapInfo_FindName / MapInfo_FixName: unique-prefix map-name completion (binary search over the filtered list) | MISS | - | - | - | - | - | high |
| `mapinfo.list.allowed_maps` | MapInfo_ListAllowedMaps / ListAllAllowedMaps (lsmaps reply catalog, gametype-vote g_maplist regeneration) *(intended)* | OK | - | - | OK | - | live | high |
| `mapinfo.metadata.title_author` | Title/author/description derivation (title_sans_author separators, isRedundant titlestring, decolorize, SVQC worldspawn message/author fallback) | ~ | ~ | - | ~ | - | live | high |
| `mapinfo.metadata.cdtrack` | cdtrack → map music (clientstuff 'cd loop', worldspawn music/noise fallback) *(intended)* | OK | OK | - | OK | OK | live | high |
| `mapinfo.settemp.per_gametype` | Per-map settemps + client settemps + fog line (settemp_for_type / clientsettemp_for_type, _MapInfo_Parse_Settemp #include recursion, MAPINFO_SETTEMP_ACL, MapInfo_Map_clientstuff/fog application) *(intended)* | ~ | ~ | ~ | ~ | - | live | high |
| `mapinfo.sizes.minplayers` | MapReadSizes: maps/<map>.sizes → map_minplayers / map_maxplayers | ~ | ~ | - | - | - | ~ | high |
| `mapinfo.infra.cache_glob` | Coverage: cache/glob/temp infrastructure (MapInfo_Cache_Create/Store/Retrieve, _MapInfo_GlobItem, FilterList heapsort, MapInfo_ClearTemps, MapInfo_Shutdown, unquote) *(intended)* | OK | - | - | - | - | live | high |

### `sv-anticheat` (server) — 15 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `sv-anticheat.core.power_mean` | Power-mean accumulator (MEAN_DECLARE/accumulate/evaluate) *(intended)* | OK | OK | - | - | - | live | high |
| `sv-anticheat.detect.movement_oddity` | movement_oddity strafebot turn-reversal score | OK | OK | - | - | - | live | high |
| `sv-anticheat.detect.div0_evade` | div0_evade server-driven dodge-prediction probe | OK | OK | OK | - | - | live | high |
| `sv-anticheat.detect.strafebot_new_snapaim` | strafebot_new turn-angle + idle snap-aim signal/noise + snapback | OK | OK | OK | - | - | live | high |
| `sv-anticheat.detect.speedhack_old` | Generic speedhack (old movetime/servertime correlation) | OK | OK | OK | - | - | live | high |
| `sv-anticheat.detect.speedhack_new` | Generic speedhack (new decaying accumulator m1..m5) | OK | OK | OK | - | - | live | high |
| `sv-anticheat.frame.evasion_phase_walk` | Global div0_evade evasion-delta phase walk (start+end frame) *(intended)* | OK | OK | OK | - | - | live | high |
| `sv-anticheat.frame.fixangle_window` | Snap-aim suppression window after a forced view change *(intended)* | OK | ~ | OK | - | - | live | high |
| `sv-anticheat.lifecycle.init` | anticheat_init (jointime stamp + speedhack baseline clear) on connect | OK | OK | OK | - | - | live | high |
| `sv-anticheat.report.eventlog` | anticheat_report_to_eventlog (:anticheat: verdict lines on disconnect) | OK | OK | - | OK | - | live | high |
| `sv-anticheat.report.sv_cmd` | sv_cmd anticheat admin on-demand verdict report | OK | OK | - | OK | - | live | high |
| `sv-anticheat.report.playerstats` | anticheat_report_to_playerstats (XonStat end-of-match feed) | OK | OK | - | - | - | live | medium |
| `sv-anticheat.report.register_playerstats` | anticheat_register_to_playerstats (pre-register the anticheat event slots) | OK | OK | - | - | - | live | high |
| `sv-anticheat.report.display_verdict` | anticheat_display N/Y/- verdict formatting + ANTICHEATS table | OK | OK | - | OK | - | live | high |
| `sv-anticheat.spectate.evade_angle_copy` | anticheat_spectatecopy (spectator body follows spectatee evade angle) | OK | OK | - | OK | - | live | high |

### `sv-antilag` (server) — 16 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `sv-antilag.history.record` | Per-frame position-history ring (antilag_record) | OK | OK | OK | - | - | live | high |
| `sv-antilag.history.record_timestamp` | Record at altime = time + frametime*(1 + g_antilag_nudge) | OK | OK | OK | - | - | live | high |
| `sv-antilag.history.sample` | Sample a past origin by bracketing-pair lerp (antilag_find + antilag_takebackorigin) | OK | OK | OK | - | - | live | high |
| `sv-antilag.latency.cap` | ANTILAG_LATENCY = min(0.4, ping); takeback time = time - lag | OK | OK | OK | - | - | live | medium |
| `sv-antilag.takeback.bracket` | Takeback/restore bracket around the hitscan trace (antilag_takeback_all/restore_all) | OK | OK | OK | - | - | live | high |
| `sv-antilag.setupshot.trueaim` | W_SetupShot trueaim + shotorg traces are antilagged (g_antilag==2) | OK | OK | OK | - | - | live | high |
| `sv-antilag.solidmask.corpse` | Shooter dphitcontentsmask -> SOLID|BODY|CORPSE for the duration of the trace | OK | OK | - | - | - | live | high |
| `sv-antilag.gate.cvars` | Gating: g_antilag==0, cl_noantilag, lag<0.001, non-client shooter -> no rewind (antilag_getlag) | OK | OK | - | - | - | live | high |
| `sv-antilag.mode.server_hitscan` | g_antilag==2 server-side hitscan in the past (the takeback mode) | OK | OK | OK | - | - | live | high |
| `sv-antilag.mode.client_verified_and_cursor` | g_antilag==1 (verified client-side) and ==3 (prydon cursor) aim-correction | MISS | MISS | - | - | - | - | high |
| `sv-antilag.clear.on_spawn` | antilag_clear on (re)spawn / teleport / vehicle enter-exit | OK | OK | OK | - | - | live | high |
| `sv-antilag.arc_beam` | Arc beam lag-compensated trace + antilag_takebackavgvelocity | OK | OK | OK | - | - | live | high |
| `sv-antilag.melee.secondary` | Shotgun melee secondary trace is antilagged | OK | OK | OK | - | - | live | high |
| `sv-antilag.turret.imobeam` | Turret ImoBeam (phaser) trace uses WarpZone_tracebox_antilag | OK | OK | - | - | - | live | medium |
| `sv-antilag.hitplot.analysis` | Hitplot eye trace, rewind, and normalized coordinates | OK | OK | OK | - | - | live | high |
| `sv-antilag.hitplot.lifecycle` | Opt-in local hitplot lifetime per client and match *(intended)* | OK | OK | OK | - | - | live | high |

### `sv-campaign` (server) — 14 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `sv-campaign.file.parse` | Campaign .txt quoted-CSV parse (CampaignFile_Load) | OK | OK | - | - | - | live | high |
| `sv-campaign.preinit.config` | Per-level config: gametype + bot count/skill + mutator/permanent settemps (CampaignPreInit) | OK | OK | - | - | - | live | high |
| `sv-campaign.preinit.gametype_switch` | MapInfo_SwitchGameType + Campaign_Invalid revalidation | OK | - | - | - | - | live | high |
| `sv-campaign.preinit.abort` | Bailout paths: unknown map + cheats (CampaignBailout) | OK | OK | - | - | - | live | high |
| `sv-campaign.postinit.limits` | Per-level frag/time limits: default vs empty vs value (CampaignPostInit) | OK | OK | - | - | - | live | high |
| `sv-campaign.bots.may_start` | campaign_bots_may_start: hold bots/rounds/monsters until the human spawns | OK | OK | OK | - | - | live | high |
| `sv-campaign.winlose.decision` | Win/lose decision: sole-human-winner + beat-the-clock (CampaignPreIntermission) | OK | OK | OK | OK | - | live | high |
| `sv-campaign.progress.save` | Frontier progress persistence to campaign.cfg (CampaignSaveCvar + the save gate) | OK | OK | - | - | - | live | high |
| `sv-campaign.transition.next_level` | Advance / replay / last-level transition (CampaignPostIntermission + CampaignSetup) *(intended)* | OK | OK | - | OK | - | live | high |
| `sv-campaign.warp.command` | Level warp via sv_cmd warp (CampaignLevelWarp) | OK | OK | - | - | - | live | high |
| `sv-campaign.mapentity.levelwarp` | target_levelwarp / target_changelevel campaign win-credit (map-triggered) | OK | OK | OK | - | - | live | high |
| `sv-campaign.mapentity.gametype_switch` | target_changelevel next-map gametype switch (MapInfo_SwitchGameType on .gametype) *(intended)* | OK | OK | - | - | - | live | high |
| `sv-campaign.forceteam` | g_campaign_forceteam: force the player onto a given team in campaign | OK | OK | - | - | - | live | high |
| `sv-campaign.welcome.levelnum` | Campaign level number in the welcome/info message (Campaign_GetLevelNum) | OK | OK | - | ~ | - | live | high |

### `sv-chat` (server) — 15 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `sv-chat.gates.allowed_cvars` | Four g_chat_*_allowed entry gates (chat/private/spectator/team) | OK | OK | - | OK | - | live | high |
| `sv-chat.say.preprocessing` | Message preprocessing: leading-space trim, formatmessage call, colorstr/name build, magicear pass | OK | OK | - | OK | - | live | high |
| `sv-chat.say.display_strings` | Display + centerprint string construction (public/team/private, /me action form) | OK | OK | - | OK | - | live | high |
| `sv-chat.flood.per_type_throttle` | Per-say-type flood control (broadcast/team/tell) with persistent timestamps + line wrapping | OK | ~ | OK | OK | - | live | high |
| `sv-chat.flood.return_and_notify` | flood==2 trim+notify, flood==1 wait-sprint, 1/0/-1 return code, flood LOG_INFO note | OK | OK | OK | OK | - | live | high |
| `sv-chat.routing.recipients` | Recipient routing (public / team / spectator / private / minigame) with ignore + spectator-downgrade | OK | OK | - | OK | - | live | high |
| `sv-chat.routing.ignore_filter` | Mutual-ignore filtering on every recipient (and tell-to-ignorer returns -1) | OK | OK | - | - | - | live | high |
| `sv-chat.muted.fake_accept` | Muted sender = fake-accept (sender sees own line, no one else does) | OK | OK | - | OK | - | live | high |
| `sv-chat.ignore.crud` | Ignore-list CRUD: ignore / unignore / clear_ignores + list cap | ~ | OK | - | OK | - | live | high |
| `sv-chat.format.macros` | formatmessage %/\ macro expansion (%%/%h/%a/%o/%O/%w/%W/%s/%S/%t/%T + \n/\\, 7-budget) | OK | OK | - | OK | - | live | high |
| `sv-chat.format.crosshair_tokens` | Location/aim formatmessage tokens (%l/%y/%d/%x) + NearestLocation item fallback | OK | OK | - | OK | - | live | high |
| `sv-chat.voice.say_coupling` | VoiceMessage text routed through Say (flood-throttle + chat display + fake/real sound gating) | OK | OK | OK | OK | OK | live | high |
| `sv-chat.delivery.dedicated_print_gating` | dedicated_print gated on sv_dedicated (server-console echo) | OK | - | - | OK | - | live | high |
| `sv-chat.helpers.printtochat` | PrintToChat / PrintToChatAll / PrintToChatTeam (+ Debug* developer-gated variants) chat-line emitters | OK | - | - | OK | - | ~ | high |
| `sv-chat.mutator.hooks` | Chat mutator hooks (ChatMessage / ChatMessageTo / PreFormatMessage / FormatMessage) *(intended)* | MISS | - | - | - | - | - | high |

### `sv-cheats` (server) — 19 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `sv-cheats.gating.cheats_allowed` | CheatsAllowed — sv_cheats snapshot + dead/observer/maycheat gating + refusal logging | OK | OK | OK | OK | - | live | high |
| `sv-cheats.accounting.cheatcount` | Cheat counting (per-player + global) gating campaign progress | OK | OK | OK | - | - | live | high |
| `sv-cheats.cmd.god` | cmd god — toggle FL_GODMODE | OK | OK | OK | - | - | live | high |
| `sv-cheats.cmd.notarget` | cmd notarget — toggle FL_NOTARGET | OK | OK | OK | - | - | live | high |
| `sv-cheats.cmd.noclip` | cmd noclip — toggle MOVETYPE_NOCLIP | OK | OK | OK | - | - | live | high |
| `sv-cheats.cmd.fly` | cmd fly — toggle MOVETYPE_FLY | OK | OK | OK | - | - | live | high |
| `sv-cheats.cmd.give` | cmd give — grant items/weapons/resources | OK | OK | OK | - | - | live | high |
| `sv-cheats.cmd.usetarget` | cmd usetarget — fire a named target (SUB_UseTargets) | OK | OK | OK | - | - | live | high |
| `sv-cheats.cmd.killtarget` | cmd killtarget — remove a named target (SUB_UseTargets killtarget) | OK | OK | OK | - | - | live | high |
| `sv-cheats.impulse.give_all` | Cheat impulse GIVE_ALL (99) — give all | OK | OK | OK | - | - | live | high |
| `sv-cheats.impulse.clone` | Cheat impulses CLONE_MOVING (140) / CLONE_STANDING (142) — CopyBody clones | ~ | OK | ? | ~ | - | - | medium |
| `sv-cheats.impulse.speedrun` | Cheat impulses SPEEDRUN_INIT (30) / SPEEDRUN (141) — personal waypoint snapshot + restore | OK | OK | OK | - | - | live | high |
| `sv-cheats.impulse.teleport` | Cheat impulse TELEPORT (143) — emergency teleport to autoscreenshot or random location | ~ | ~ | - | - | - | live | high |
| `sv-cheats.impulse.r00t` | Cheat impulse R00T (148) — nuke a random enemy | OK | OK | - | OK | OK | live | high |
| `sv-cheats.cmd.teleporttotarget` | cmd teleporttotarget — teleport player to a named teleport target | OK | - | - | - | - | live | high |
| `sv-cheats.cmd.particles_make` | cmd pointparticles / trailparticles / make / penalty — debug/effect spawners | ~ | OK | - | OK | - | live | high |
| `sv-cheats.frame.drag` | CheatFrame + Drag — per-frame object dragging + dragbox/dragpoint map editor | MISS | MISS | MISS | MISS | - | - | high |
| `sv-cheats.mapent.info_autoscreenshot` | info_autoscreenshot map entity (observe/screenshot point, teleport target) | ~ | OK | - | - | - | live | high |
| `sv-cheats.campaign.start_block` | Campaign behavior when sv_cheats is enabled (load-time bailout) | OK | OK | OK | - | - | live | high |

### `sv-client-lifecycle` (server) — 23 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `sv-client-lifecycle.connect.client_connect` | ClientConnect: accept a client as an observer | ~ | OK | OK | - | - | live | high |
| `sv-client-lifecycle.connect.join_notice` | Configured timed join notice | OK | OK | ~ | ~ | - | live | medium |
| `sv-client-lifecycle.connect.fix_client_cvars_welcome` | FixClientCvars / SendWelcomeMessage / ClientInit_misc: server->client welcome + cvar push | ~ | ~ | ~ | ~ | - | ~ | high |
| `sv-client-lifecycle.join.join_player` | Join: observer -> live player | ~ | OK | OK | ~ | - | live | high |
| `sv-client-lifecycle.join.join_allowed` | joinAllowed gates | ~ | ~ | OK | MISS | - | live | high |
| `sv-client-lifecycle.spawn.put_player_in_server` | PutPlayerInServer: place + load out a spawning player | ~ | OK | OK | OK | - | live | high |
| `sv-client-lifecycle.spawn.spawn_shield` | Spawn shield + spawn-time regen/rot pause timers | OK | OK | OK | - | - | live | high |
| `sv-client-lifecycle.observer.put_observer_in_server` | PutObserverInServer: demote to free-fly observer | ~ | OK | OK | OK | - | live | high |
| `sv-client-lifecycle.observer.observer_think` | ObserverOrSpectatorThink: join-on-fire + spectate cycling | ~ | OK | OK | - | - | live | high |
| `sv-client-lifecycle.observer.autojoin_delayed` | Delayed autojoin for a real client (PlayerPreThink) | ~ | OK | OK | - | - | live | high |
| `sv-client-lifecycle.dead.respawn_state_machine` | Dead-player respawn state machine (DEAD_DYING..RESPAWNING) | OK | OK | OK | OK | - | live | high |
| `sv-client-lifecycle.dead.respawn_timing` | calculate_player_respawn_time (pcount-scaled delay) | OK | OK | OK | - | - | live | high |
| `sv-client-lifecycle.dead.respawn_countdown_announce` | ShowRespawnCountdown: 10-9-8 respawn announcer | OK | OK | OK | OK | OK | live | high |
| `sv-client-lifecycle.frame.player_regen` | player_regen: health/armor/fuel regen + rot | OK | OK | OK | - | - | live | high |
| `sv-client-lifecycle.frame.drown_player` | DrownPlayer: air timer + drown damage | OK | OK | OK | - | OK | live | high |
| `sv-client-lifecycle.frame.contents_fall_damage` | CreatureFrame liquids + fall damage (per-frame) | OK | OK | OK | - | - | live | medium |
| `sv-client-lifecycle.frame.player_powerups` | player_powerups: superweapon countdown + PlayerPowerups hook | OK | OK | OK | OK | OK | live | high |
| `sv-client-lifecycle.frame.pressed_keys` | GetPressedKeys: PRESSED_KEYS stat for the HUD | OK | OK | OK | ~ | - | live | high |
| `sv-client-lifecycle.frame.idle_kick` | sv_maxidle idle-kick / move-to-spectator + alivetime afk-gate | OK | OK | OK | OK | OK | live | high |
| `sv-client-lifecycle.frame.spectator_kick` | sv_spectate-disabled spectator kick + SPECTATE_WARNING | OK | OK | OK | OK | - | live | high |
| `sv-client-lifecycle.frame.name_and_version` | Version nag + nameless/too-long/invisible name enforcement + GOD MODE info | OK | OK | OK | OK | - | live | high |
| `sv-client-lifecycle.frame.prethink_misc` | PlayerPreThink misc: PlayerUseKey edge, SetZoomState, voice sounds, chat bubble | ~ | - | - | MISS | ~ | ~ | medium |
| `sv-client-lifecycle.disconnect.client_disconnect` | ClientDisconnect: teardown | ~ | OK | OK | OK | - | live | high |

### `sv-clientkill` (server) — 12 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `sv-clientkill.command.kill` | `kill` console command → suicide | OK | - | OK | - | - | live | high |
| `sv-clientkill.countdown.delay` | g_balance_kill_delay countdown timer before death | OK | OK | OK | - | - | live | high |
| `sv-clientkill.antispam.penalty` | g_balance_kill_antispam repeat-use penalty (clientkill_nexttime) | OK | OK | OK | - | - | live | high |
| `sv-clientkill.indicator.entity` | Kill-indicator floating digit entity above the head | OK | OK | ~ | ~ | - | live | medium |
| `sv-clientkill.announcer.number` | Spoken kill countdown announcer (ANNCE_NUM_KILL_n) | OK | - | OK | OK | OK | live | high |
| `sv-clientkill.centerprint.teamchange` | Center-print 'Suicide/Changing to TEAM/Spectating in N' | OK | - | OK | OK | - | live | high |
| `sv-clientkill.teamchange.deferred` | Deferred team change via killindicator_teamchange (selectteam/spectate) | OK | - | OK | OK | OK | live | high |
| `sv-clientkill.mutator.hooks` | ClientKill / ClientKill_Now mutator gating (freezetag block, cts/race killtime) | OK | - | OK | - | - | live | high |
| `sv-clientkill.silent.cts_finish` | Silent finish kill (ClientKill_Silent, g_cts_finish_kill_delay) | OK | OK | OK | - | - | live | high |
| `sv-clientkill.impulse.waypoint_kill` | Impulse-driven kill on personal-waypoint clear (race/cts checkpoints) | OK | - | OK | - | - | live | high |
| `sv-clientkill.clientkill_now.vehicle_exit` | Eject from vehicle on self-kill (ClientKill_Now vehicles_exit) | OK | OK | - | - | - | live | high |
| `sv-clientkill.teamchange.excess_removal` | TeamBalance_RemoveExcessPlayers after a deferred team change | OK | - | - | - | - | live | high |

### `sv-commands-votes` (server) — 17 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `sv-commands-votes.dispatch.client_parse` | Client command dispatch (cmd <verb>) + privilege split + flood/UTF-8/ban pre-gates | OK | OK | OK | - | - | live | high |
| `sv-commands-votes.dispatch.server_console` | Server-console dispatch (sv_cmd) + builtin command table | OK | OK | - | - | - | live | high |
| `sv-commands-votes.vote.call` | vote call — open a whitelisted call-vote (guard chain + caller auto-yes + cooldown) | OK | OK | OK | ~ | OK | live | high |
| `sv-commands-votes.vote.parse_whitelist` | VoteCommand_parse — whitelist match, nasty-char sanitize, command rewriting | OK | OK | - | - | - | live | high |
| `sv-commands-votes.vote.count` | VoteCount — tally, thresholds, spectator exclusion, accept/reject/timeout resolution | OK | OK | OK | - | - | live | high |
| `sv-commands-votes.vote.resolve` | VoteAccept / VoteReject / VoteTimeout / VoteStop + VoteThink | OK | OK | OK | OK | OK | live | high |
| `sv-commands-votes.vote.master` | vote master — login / do <cmd> / call master-vote | OK | OK | OK | - | - | live | high |
| `sv-commands-votes.timeout.state_machine` | timeout / timein — LEADTIME->ACTIVE->resume pause with per-player allowance | OK | OK | OK | OK | OK | live | high |
| `sv-commands-votes.ban.ip_id_masks` | Ban IP/idfp mask derivation + IsClientBanned + idmode | OK | OK | - | - | - | live | high |
| `sv-commands-votes.ban.insert_persist` | Ban_Insert / Ban_Delete / Ban_View + g_banned_list persistence + KickBan + enforce | OK | OK | - | - | - | live | high |
| `sv-commands-votes.ban.sync_online` | Online cross-server ban-list sync (uri_get / g_ban_sync_*) *(intended)* | MISS | MISS | MISS | - | - | - | high |
| `sv-commands-votes.ban.prefix_lists` | mute / playban / voteban prefix-list bans (g_chatban/playban/voteban_list) | OK | OK | - | - | - | live | high |
| `sv-commands-votes.common.info_commands` | who / time / records / rankings / ladder / lsmaps / printmaplist / teamstatus / info / cvar_changes | OK | OK | - | OK | - | live | high |
| `sv-commands-votes.common.cointoss` | cointoss — random coin flip broadcast | OK | OK | - | OK | - | live | high |
| `sv-commands-votes.admin.teamplay_match` | allready / resetmatch / lockteams / unlockteams / shuffleteams / moveplayer / allspec / nospectators / gametype / extendmatchtime / reducematchtime | OK | OK | - | - | - | live | high |
| `sv-commands-votes.admin.adminmsg_delrec_printstats` | adminmsg / delrec / printstats — admin message + race-record delete + score dump | OK | OK | - | OK | - | live | high |
| `sv-commands-votes.debug_tools.missing` | radarmap / gettaginfo / animbench / bbox / trace / stuffto / database / make_mapinfo | MISS | MISS | - | - | - | - | high |

### `sv-handicap` (server) — 8 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `sv-handicap.damage.give_take_scaling` | Damage scaling by give/take handicap in PlayerDamage | OK | OK | OK | - | - | live | high |
| `sv-handicap.total.forced_times_voluntary` | Total handicap = forced x voluntary, per direction | OK | OK | - | - | - | live | high |
| `sv-handicap.voluntary.cl_handicap_cvars` | Voluntary handicap from cl_handicap / cl_handicap_damage_given/taken | OK | OK | OK | - | - | live | high |
| `sv-handicap.init.defaults_to_one` | Handicap_Initialize: forced handicaps reset to 1 on (re)spawn | OK | OK | OK | - | - | live | high |
| `sv-handicap.level.compute_and_network` | handicap_level (0-16) computed + networked via ENTCS | OK | OK | OK | OK | - | live | high |
| `sv-handicap.scoreboard.handicap_icon` | Scoreboard player_handicap icon (white->red by level) *(intended)* | OK | OK | - | OK | - | live | high |
| `sv-handicap.xonstat.avg_sums` | Per-player damage-weighted handicap avg for the game report | OK | OK | OK | - | - | live | high |
| `sv-handicap.dynamic.mutator` | Dynamic Handicap mutator (auto-balance by score) | OK | OK | OK | - | - | live | high |

### `sv-impulse` (server) — 13 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `sv-impulse.dispatch.commands_gate` | ImpulseCommands dispatch + match-state gating | OK | OK | OK | - | - | live | high |
| `sv-impulse.weapon.group_cycle` | Weapon-group cycling impulses (1-9, 14) | OK | OK | OK | - | - | live | high |
| `sv-impulse.weapon.priority_cycle` | Custom-priority cycling impulses (prev/best/next, 200-229) | OK | OK | OK | - | - | ~ | high |
| `sv-impulse.weapon.byid` | Direct weapon-by-id impulses (230-253) | OK | OK | OK | - | - | live | high |
| `sv-impulse.weapon.nextprev_singletons` | next/prev/last/best singleton impulses (10/12, 18/19, 15/16, 11, 13) | OK | OK | OK | - | - | live | high |
| `sv-impulse.weapon.getcycle` | W_GetCycleWeapon traversal (the selection core) | ~ | OK | OK | - | - | live | high |
| `sv-impulse.weapon.switch` | W_SwitchWeapon / TryOthers / reload-on-reselect | OK | OK | OK | - | - | live | high |
| `sv-impulse.action.drop` | weapon_drop (impulse 17) — throw current weapon | OK | OK | OK | ~ | OK | live | high |
| `sv-impulse.action.reload` | weapon_reload (impulse 20) | OK | OK | OK | - | ? | live | high |
| `sv-impulse.use.usekey` | use (impulse 21) / +use key -> PlayerUseKey | OK | OK | OK | - | - | live | high |
| `sv-impulse.waypoints.sprites` | Waypoint-sprite impulses (personal/here/danger/helpme/clear, 30-39, 47-48) | OK | OK | OK | OK | - | live | high |
| `sv-impulse.cheat.impulses` | Cheat impulses (give-all 99, teleport 143, r00t 148) | OK | OK | - | - | - | live | high |
| `sv-impulse.cheat.command_impulse` | CheatImpulse 140-142 + SPEEDRUN_INIT 30 (clone-moving / speedrun / clone-standing / personal-checkpoint) | OK | OK | OK | - | - | ~ | high |

### `sv-intermission` (server) — 15 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `sv-intermission.matchend.nextlevel` | NextLevel match-end latch + once-per-match bookkeeping | OK | OK | OK | - | - | live | high |
| `sv-intermission.matchend.freeze` | World freeze during intermission (game_stopped/intermission_running) | OK | - | OK | - | - | live | high |
| `sv-intermission.timer.exittime` | Intermission exit timer (intermission_exittime + sv_mapchange_delay, -1 with no players) | OK | OK | OK | - | - | live | high |
| `sv-intermission.timer.input_early_exit` | Player input skips the scoreboard (press fire/jump after the hold) | OK | - | OK | OK | - | live | high |
| `sv-intermission.overtime.cascade` | Overtime / sudden-death cascade (CheckRules_World) | OK | OK | OK | OK | - | live | high |
| `sv-intermission.maplist.rotation` | Maplist init + next-map selection + recent-map exclusion | OK | OK | - | - | - | live | high |
| `sv-intermission.nextmap.override` | DoNextMapOverride priority (campaign / queued nextmap / samelevel / redirect / quit / lastlevel) | OK | OK | OK | - | - | live | high |
| `sv-intermission.nextmap.changelevel` | Apply chosen next map (Map_Goto → changelevel) + mark recent | OK | - | OK | - | - | live | high |
| `sv-intermission.mapvote.server_core` | Server map-vote core (MapVote_Start / tally / finish) | OK | OK | OK | - | - | live | high |
| `sv-intermission.mapvote.client_ui` | Client map-vote ballot UI (MapVote_Draw) | OK | OK | OK | ~ | - | live | high |
| `sv-intermission.matchend.nextmap_broadcast` | Next-map broadcast to clients (Set_NextMap / Send_NextMap_To_Player → scoreboard 'Next map:') | OK | - | OK | OK | - | live | high |
| `sv-intermission.client.view_freeze` | Intermission view freeze (SVC_INTERMISSION: camera lock + viewmodel EF_NODRAW + health sentinel) *(intended)* | OK | - | - | OK | - | live | high |
| `sv-intermission.client.scoreboard_autoshow` | Scoreboard auto-shows at intermission (no key held) | OK | - | OK | OK | - | live | high |
| `sv-intermission.client.autoscreenshot` | Server-forced end-of-match autoscreenshot | OK | OK | OK | OK | - | live | high |
| `sv-intermission.audio.cdtrack` | Intermission music switch (sv_intermission_cdtrack) + target_music_kill | OK | OK | - | - | OK | live | high |

### `sv-ipban` (server) — 13 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `sv-ipban.mask.derive` | Client IP/identity mask derivation (IPv4 /8../32, IPv6 /32../64, crypto idfp) | OK | OK | - | - | - | live | high |
| `sv-ipban.check.is_banned` | Ban check: 4 IP masks + crypto-id, idmode + crypto-always-wins | OK | OK | - | - | - | live | high |
| `sv-ipban.enforce.on_connect` | Enforce ban on connect: notify (g_ban_telluser) + drop | OK | OK | - | - | - | live | high |
| `sv-ipban.insert` | Ban_Insert: prolong-not-shorten, free/expired slot, evict-soonest, refuse-shorter | ~ | OK | - | - | - | live | high |
| `sv-ipban.kickban` | Ban_KickBanClient: masksize pick + ban IP and crypto id, fallback plain kick | OK | OK | - | - | - | live | high |
| `sv-ipban.enforce.roster` | Ban_Enforce: drop every connected client matching a slot (or all) | OK | - | - | OK | - | live | high |
| `sv-ipban.persist` | Save/Load via g_banned_list (version-1 token string, seconds remaining) | OK | OK | OK | - | - | live | high |
| `sv-ipban.view_delete` | Ban_View (banlist) + Ban_Delete (unban #N), index stability | OK | OK | - | OK | - | live | high |
| `sv-ipban.softban.chatban` | Mute (g_chatban_list): admin mute/unmute + fake-accept muted chat + connect re-apply | OK | OK | - | - | - | live | high |
| `sv-ipban.softban.playban` | Playban (g_playban_list): force spectate + minigame removal + connect re-apply | OK | OK | - | - | - | live | high |
| `sv-ipban.softban.playban.join_gate` | Playban join-attempt enforcement: a play-banned client cannot (re)join the game | OK | - | - | OK | - | live | high |
| `sv-ipban.softban.voteban` | Voteban (g_voteban_list): block calling and casting votes | OK | OK | - | OK | - | live | medium |
| `sv-ipban.sync.online` | Online cross-server ban-list sync (HTTP uri_get, bansyncer think, g_ban_sync_*) *(intended)* | MISS | MISS | MISS | - | - | - | high |

### `sv-mapvoting` (server) — 15 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `sv-mapvoting.lifecycle.think_loop` | Vote lifecycle / think loop (start, throttle, winner delay) | OK | OK | OK | - | - | live | high |
| `sv-mapvoting.ballot.build` | Build the ballot from rotation + suggestions + abstain | OK | OK | - | - | - | live | high |
| `sv-mapvoting.override_chain` | Pre-vote override chain (DoNextMapOverride: campaign / queued nextmap / samelevel) + silent GotoNextMap | OK | OK | - | - | - | live | high |
| `sv-mapvoting.vote_scoreboard_hide` | During-vote client state: 2342 sentinel health + SVC_FINALE (hide scoreboard, zero impulse each tick) *(intended)* | MISS | MISS | MISS | OK | - | ~ | high |
| `sv-mapvoting.cast.vote` | Cast a vote (impulse -> .mapvote) | OK | - | - | - | - | live | high |
| `sv-mapvoting.cast.abstain` | Abstain (Don't care) option | OK | OK | - | - | - | live | high |
| `sv-mapvoting.decide.tally_rank` | Tally votes + rank candidates | OK | - | - | - | - | live | high |
| `sv-mapvoting.decide.early_finish` | Early finish: timeout / leader unbeatable / all voted | OK | OK | OK | - | - | live | high |
| `sv-mapvoting.decide.reduce` | Ballot reduce / keep-two (mid-vote option pruning) | OK | OK | OK | OK | - | live | high |
| `sv-mapvoting.finish.winner` | Finish + pick winner + apply map change | OK | - | OK | OK | - | live | high |
| `sv-mapvoting.gametype_vote` | Gametype vote (pre-map gametype ballot + switch) | ~ | OK | OK | OK | - | live | high |
| `sv-mapvoting.suggestions` | Player map suggestions (suggestmap) | OK | OK | - | - | - | live | high |
| `sv-mapvoting.net.sync` | Vote state networking (ENT_CLIENT_MAPVOTE + screenshot transfer) *(intended)* | ~ | - | OK | ~ | - | live | high |
| `sv-mapvoting.client.draw` | Client vote-screen panel (grid, thumbnails, counts, winner reveal) | OK | OK | OK | OK | - | live | high |
| `sv-mapvoting.client.input` | Client vote input (keyboard/mouse selection + cast) | OK | - | - | OK | - | live | high |

### `sv-matrix` (server) — 1 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `sv-matrix.chat.bridge` | Optional native dedicated Matrix chat bridge *(intended)* | OK | OK | OK | - | - | live | high |

### `sv-spawnpoints` (server) — 15 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `sv-spawnpoints.select.select_spawn_point` | SelectSpawnPoint: gather, filter, weighted 50/50 pick | ~ | OK | OK | - | - | live | high |
| `sv-spawnpoints.select.spawn_score` | Spawn_Score: nearest-player distance scoring + good-distance prio | OK | OK | - | - | - | live | high |
| `sv-spawnpoints.select.weighted_point` | Spawn_WeightedPoint / RandomSelection reservoir pick | OK | OK | - | - | - | live | high |
| `sv-spawnpoints.select.teamcheck_ladder` | teamcheck branch ladder + have_team_spawns globals *(intended)* | OK | OK | - | - | - | live | high |
| `sv-spawnpoints.select.spot_filters` | Spot filters: wrong-team / inactive / restriction | OK | OK | - | - | - | live | high |
| `sv-spawnpoints.select.spawn_evalfunc` | race/assault spawn_evalfunc target chain + race_spawns requirement | OK | OK | - | - | - | live | high |
| `sv-spawnpoints.select.spawnpoint_targ` | target_spawnpoint forced-spawn redirection | OK | - | OK | - | - | live | high |
| `sv-spawnpoints.relocate.move_out_of_solid` | relocate_spawnpoint move-out-of-solid *(intended)* | OK | OK | OK | - | - | live | high |
| `sv-spawnpoints.put.put_player_in_server` | PutPlayerInServer: physics/loadout/placement reset on (re)spawn | OK | OK | OK | ~ | OK | live | high |
| `sv-spawnpoints.put.spawn_target_fire` | PutPlayerInServer fires the spawn spot's .target on spawn | OK | - | OK | - | - | live | high |
| `sv-spawnpoints.fx.spawn_event_particle` | SpawnEvent particle burst + sound on (re)spawn | OK | OK | OK | OK | OK | live | high |
| `sv-spawnpoints.fx.spawnpoint_idle_glow` | Idle spawn-point particle glow (Spawn_Draw) *(intended)* | OK | OK | OK | OK | - | live | high |
| `sv-spawnpoints.mutator.spawn_near_teammate` | spawn_near_teammate: bias/relocate spawn near a teammate | ~ | OK | OK | - | - | live | high |
| `sv-spawnpoints.mutator.spawn_unique` | spawn_unique: demote the player's last spawnpoint | OK | OK | - | - | - | live | high |
| `sv-spawnpoints.net.spawnpoint_link_send` | SpawnPoint_Send / link_spawnpoint / spawnpoint_think networking *(intended)* | ~ | MISS | MISS | ~ | - | live | medium |

### `sv-teamplay` (server) — 17 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `sv-teamplay.identity.team_codes` | Team color codes & active-team set | OK | OK | - | - | - | live | high |
| `sv-teamplay.join.find_best_team` | Assign joiner to the best (smallest) team | OK | OK | - | - | - | live | high |
| `sv-teamplay.join.skill_weighting` | Inverse-variance skill-weighted balance (g_balance_teams_skill) | OK | ~ | - | - | - | live | high |
| `sv-teamplay.balance.autobalance_bots` | Bot autobalance (move lowest-scoring bot largest->smallest) | ~ | ~ | ~ | - | - | live | high |
| `sv-teamplay.balance.prevent_imbalance` | Block switching to a stronger/larger team mid-match | OK | OK | - | OK | - | live | high |
| `sv-teamplay.forced.determine_team` | Forced teams (g_forced_team_*, otherwise, campaign forceteam) | OK | OK | - | - | - | live | high |
| `sv-teamplay.queue.join_queue` | Warmup/match join queue (g_balance_teams_queue) | ~ | OK | - | - | - | ~ | high |
| `sv-teamplay.remove.excess_players` | Remove excess players on leave (g_balance_teams_remove) | OK | OK | OK | OK | - | live | high |
| `sv-teamplay.nagger.team_nagger` | Unbalanced-teams nag + warmup hold (sv_teamnagger) | OK | OK | - | MISS | - | ~ | high |
| `sv-teamplay.bot_vs_human.team_split` | bot_vs_human team partitioning (bots vs humans on separate teams) | OK | OK | - | - | - | live | high |
| `sv-teamplay.change.kill_on_teamchange` | Kill + score clear on mid-match team change | OK | OK | - | - | - | live | high |
| `sv-teamplay.admin.lock_shuffle_move` | Admin team commands: lockteams/shuffleteams/moveplayer/team | ~ | - | - | - | - | live | medium |
| `sv-teamplay.color.set_player_colors` | color command / clientcolors encoding (SV_ChangeTeam, setcolor, SetPlayerColors) | OK | OK | - | OK | - | live | high |
| `sv-teamplay.global.team_entities` | Global team entities (score, alive count, owned items, winner queries) | ~ | OK | - | - | - | ~ | low |
| `sv-teamplay.change.team_change_hooks` | Player_ChangeTeam / Player_ChangedTeam mutator hooks | OK | - | - | - | - | live | high |
| `sv-teamplay.lock.lockonrestart` | Auto-lock teams on ready-restart (teamplay_lockonrestart) | OK | OK | - | - | - | live | high |
| `sv-teamplay.bot.bot_forced_team` | Bot forced team (bot_forced_team from bot config/connect) | OK | OK | - | - | - | live | high |

### `sv-weaponstats` (server) — 1 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `sv-weaponstats.local-report` | Optional local weapon matchup stats file | ~ | OK | OK | - | - | live | high |

### `sv-world-rules` (server) — 20 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `sv-world-rules.boot.worldspawn` | worldspawn map-init bookkeeping | OK | OK | - | - | - | live | high |
| `sv-world-rules.cvars.changes_log` | cvar_changes / cvar_purechanges server-list log | OK | OK | - | - | - | live | high |
| `sv-world-rules.warmup.stage` | Warmup stage entry + limit resolution | OK | OK | OK | - | - | live | high |
| `sv-world-rules.warmup.serverflags` | readlevelcvars serverflags (fullbright / pickuptimer) | OK | OK | - | ~ | - | live | high |
| `sv-world-rules.warmup.ready_count` | ReadyCount ready-up majority + countdown abort | OK | OK | OK | OK | OK | live | high |
| `sv-world-rules.warmup.ready_restart` | ReadyRestart / ReadyRestart_force match-restart flow | OK | OK | OK | - | - | live | high |
| `sv-world-rules.warmup.reset_map` | reset_map: full map/player reset on restart | ~ | OK | OK | - | - | live | high |
| `sv-world-rules.checkrules.cascade` | CheckRules_World per-frame win cascade | ~ | OK | OK | - | ~ | live | high |
| `sv-world-rules.checkrules.overtime` | Overtime / sudden-death (InitiateSuddenDeath/Overtime/GetWinningCode) | OK | OK | OK | OK | ~ | live | high |
| `sv-world-rules.nextlevel.intermission` | NextLevel + intermission entry / map-change timer | OK | OK | OK | ~ | - | live | high |
| `sv-world-rules.entity.randomseed` | RandomSeed shared-RNG broadcast entity *(intended)* | OK | ~ | OK | ~ | - | live | high |
| `sv-world-rules.entity.pingplreport` | PingPLReport ping/packet-loss client report | ~ | ~ | ~ | OK | - | ~ | high |
| `sv-world-rules.boot.lightstyles` | Animated lightstyle table install | OK | OK | OK | ~ | - | live | high |
| `sv-world-rules.misc.start_delay` | g_start_delay pre-match join window | OK | OK | OK | - | - | live | high |
| `sv-world-rules.misc.max_shot_distance` | max_shot_distance from world bounds | OK | OK | - | - | - | live | high |
| `sv-world-rules.misc.redirection` | RedirectionThink server redirect | ~ | ~ | ~ | - | - | live | high |
| `sv-world-rules.misc.shutdown` | Shutdown: persist DB/bans + slowmo/playerstats/bot teardown | ~ | - | - | - | - | live | high |
| `sv-world-rules.eventlog.dumpstats` | DumpStats final/periodic scores dump (:scores: / :status:) | OK | OK | OK | - | - | live | high |
| `sv-world-rules.boot.initialize_entity_queue` | InitializeEntity ordered deferred-spawn queue *(intended)* | ~ | - | OK | - | - | live | medium |
| `sv-world-rules.boot.default_alpha` | SetDefaultAlpha default player/weapon alpha resolution | OK | OK | - | - | - | live | high |

### `fx-sounds` (sound) — 15 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `fx-sounds.registry.sound_table` | Sound registry (SOUND(name,path) table) + path resolution | OK | OK | - | - | OK | live | high |
| `fx-sounds.registry.channels` | CH_* channels — auto(neg)=stack vs single(pos)=replace | OK | OK | - | OK | OK | live | high |
| `fx-sounds.registry.atten_vol` | ATTEN_* / VOL_* mix constants | - | OK | - | - | - | live | high |
| `fx-sounds.registry.random_variants` | SND_*_RANDOM pickers + counted GlobalSound variants | OK | OK | - | - | OK | live | high |
| `fx-sounds.emit.sound_play` | sound() / sound7 / SV_StartSound positional emit + networking *(intended)* | OK | OK | OK | OK | OK | live | high |
| `fx-sounds.emit.loop_stop` | loopsound / stopsound — persistent (entity,channel) loop + stop | OK | OK | OK | OK | OK | live | medium |
| `fx-sounds.emit.sound_allowed` | sound_allowed owner-walk gate + bot_sound_monopoly | OK | OK | - | - | OK | live | high |
| `fx-sounds.emit.play2_family` | play2 / play2team / play2all / spamsound | OK | OK | OK | - | OK | ~ | high |
| `fx-sounds.voice.playersound_registry` | PlayerSounds / GlobalSounds registries (body sound ids) | OK | OK | - | - | OK | live | high |
| `fx-sounds.voice.voicemessage_table` | REGISTER_VOICEMSG table (team radio + taunts) + VOICETYPE constants | OK | OK | - | - | OK | live | high |
| `fx-sounds.voice.globalsound_dispatch` | _GlobalSound VOICETYPE routing (recipients + taunt/gentle gates) | ~ | OK | - | OK | OK | live | high |
| `fx-sounds.voice.live_body_callers` | PlayerSound/GlobalSound call sites (footstep/fall/pain/death/jump) | OK | OK | OK | OK | OK | live | high |
| `fx-sounds.voice.persounds_manifest` | LoadPlayerSounds .sounds manifest parse + per-model/skin reload | OK | OK | - | - | OK | live | high |
| `fx-sounds.presentation.dp_attenuation` | Client spatialization — DP SND_Spatialize distance gain + emitter follow *(intended)* | OK | OK | OK | OK | OK | live | medium |
| `fx-sounds.presentation.hitsound_sample` | Hit-confirmation sound sample (HIT registry entry) | OK | OK | OK | OK | OK | live | high |

### `turret-ewheel` (turret) — 12 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `turret-ewheel.identity.def` | Identity, hitbox, models, spawnflags | OK | OK | - | ~ | - | live | high |
| `turret-ewheel.setup.tr_setup` | tr_setup: SlideBox/Step creature, select flags, ammo flags, home pose | OK | OK | - | - | - | live | high |
| `turret-ewheel.spawn.liveness` | Map spawnfunc wiring + per-frame think (turret_ewheel placed on map) | OK | OK | OK | - | - | live | high |
| `turret-ewheel.combat.brain` | Acquire/aim/fire combat brain (shared turret_think framework) | OK | OK | OK | - | - | live | high |
| `turret-ewheel.drive.enemy` | tr_think locomotion: chase/kite/hold toward enemy + body yaw | OK | OK | OK | - | - | live | high |
| `turret-ewheel.drive.path` | Waypoint path following (ewheel_move_path / ewheel_findtarget) | OK | OK | OK | - | - | live | high |
| `turret-ewheel.drive.idle` | ewheel_move_idle: brake to a stop + idle frame | OK | OK | OK | OK | - | live | high |
| `turret-ewheel.weapon.fire` | EWheelAttack: fast blaster bolt volley (2 shots, near-hitscan) | OK | OK | OK | ~ | OK | live | high |
| `turret-ewheel.damage.lifecycle` | Damage gating, head-shake, MOVE-shove, death + respawn | OK | OK | OK | - | ? | live | high |
| `turret-ewheel.head.track` | Head rotation: track type + aim clamps | OK | OK | OK | - | - | live | high |
| `turret-ewheel.client.draw` | CSQC rolling draw + low-health sparks (ewheel_draw) | OK | OK | OK | ~ | - | live | high |
| `turret-ewheel.anim.driveframes` | Locomotion drive-frame animation (frames 0..4) | OK | OK | OK | OK | - | live | high |

### `turret-flac` (turret) — 11 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `turret-flac.identity.spawn` | FLAC turret identity, hitbox, model, health, map spawn | OK | OK | - | ~ | - | live | high |
| `turret-flac.spawnfunc.liveness` | turret_flac map spawnfunc wired to a live per-frame think | OK | OK | OK | - | - | live | medium |
| `turret-flac.target.select_flags` | Target filtering: missiles-only, no turrets, team, range (anti-projectile role) | OK | OK | OK | - | - | live | high |
| `turret-flac.target.scoring_biases` | Target-selection scoring biases (range/angle/missile/player/same) | OK | OK | - | - | - | live | high |
| `turret-flac.aim.lead_compensate` | Lead aim + shot-traveltime compensation (intercept a fast missile) | OK | OK | OK | - | - | live | high |
| `turret-flac.aim.track_motor` | Head track motor (fluid-inertia) + per-axis aim limits | OK | OK | OK | - | - | live | high |
| `turret-flac.fire.flak_shell` | Flak shell fire: fast splash projectile + timed air-burst fuse | OK | OK | OK | ~ | OK | live | high |
| `turret-flac.lifecycle.respawn` | Death + respawn timer (and the port's extra death blast) | OK | OK | OK | - | - | live | high |
| `turret-flac.obituary.death_message` | FLAC kill obituary / death notification (DEATH_TURRET_FLAC) | OK | OK | - | OK | - | live | high |
| `turret-flac.weapon.player_form` | Hidden player FLAC weapon (FlacAttack, impulse 5) | OK | OK | OK | ~ | OK | live | high |
| `turret-flac.damage.headshake` | TFL_DMG_HEADSHAKE: head flinches off-aim when the turret is hit | OK | OK | - | - | - | live | high |

### `turret-framework` (turret) — 15 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `turret-framework.registry.descriptor` | Turret descriptor class + registry (REGISTER_TURRET / TR_PROPS_COMMON) | ~ | - | - | - | - | live | high |
| `turret-framework.spawn.init` | turret_initialize: field stamping, defaults, model/size, lifecycle hooks | OK | ~ | - | - | - | live | high |
| `turret-framework.spawn.liveness` | Map spawnfunc wiring + per-frame think arm (turret_link) | OK | OK | OK | - | - | live | high |
| `turret-framework.brain.think` | Per-frame brain: ammo regen + scan/aim/track/fire driver (turret_think) | OK | OK | OK | - | - | live | high |
| `turret-framework.acquire.target` | Target acquisition: radius scan + validate cascade + bias scoring | OK | OK | OK | - | - | live | high |
| `turret-framework.aim.predict` | Aim prediction: lead + shot-traveltime + z-gravity + splash (turret_aim_generic) | OK | OK | OK | - | - | live | high |
| `turret-framework.track.head` | Head tracking motors + per-axis clamps (turret_track) *(intended)* | OK | OK | OK | - | - | live | high |
| `turret-framework.fire.gate` | Fire gate + fire bookkeeping (turret_firecheck / turret_fire) | OK | OK | OK | - | - | live | high |
| `turret-framework.projectile.generic` | Generic turret projectile (turret_projectile) | OK | OK | OK | OK | ~ | live | high |
| `turret-framework.lifecycle.use_damage` | Activation + damage gate (turret_use / turret_damage / turret_heal) | OK | OK | OK | - | - | live | high |
| `turret-framework.lifecycle.death_respawn` | Death + respawn (turret_die / turret_hide / turret_respawn) | OK | OK | OK | - | - | live | high |
| `turret-framework.lifecycle.round_reset` | Round-restart reset hook (turret_reset -> turret_respawn via .reset) | OK | OK | OK | - | - | live | high |
| `turret-framework.net.sync` | Turret networking (turret_send / TNSF_* / ENT_CLIENT_TURRET) | ~ | ~ | ~ | ~ | - | live | high |
| `turret-framework.client.presentation` | Client presentation (cl_turrets.qc: construct/draw/draw2d/die/gibs/changeteam) *(intended)* | ~ | ~ | ~ | ~ | ~ | ~ | high |
| `turret-framework.mapents.path_trigger` | Waypoint path + receive-target entities (turret_checkpoint / targettrigger / manager) | OK | OK | OK | - | - | live | high |

### `turret-fusionreactor` (turret) — 9 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `turret-fusionreactor.identity.def` | Identity / model / hitbox / support class flags | OK | OK | - | ~ | - | live | high |
| `turret-fusionreactor.setup.tr_setup` | Setup flags: energy+recharge ammo, own-team range-limited targeting, HITALLVALID, no aim/track, head spin seed | OK | OK | - | MISS | - | live | high |
| `turret-fusionreactor.recharge.sweep` | HITALLVALID per-think recharge: top up ONE eligible same-team energy turret per shot_refire | OK | OK | OK | - | - | live | high |
| `turret-fusionreactor.firecheck` | Per-recipient firecheck (same team, alive, in range, recipient not full, own ammo, recipient uses energy) | OK | OK | OK | - | - | live | high |
| `turret-fusionreactor.ammo.regen` | Self ammo regeneration (recharge toward ammo_max each frame) | OK | OK | OK | - | - | live | high |
| `turret-fusionreactor.fx.attack` | te_smallflash at recharged recipient + ammo-scaled head spin | OK | ~ | OK | ~ | - | ~ | high |
| `turret-fusionreactor.spawn.lifecycle` | Spawnfunc + master switch + think wiring + damage/use/death/respawn | OK | OK | OK | - | - | live | high |
| `turret-fusionreactor.mutator.validtarget_hook` | FusionReactor_ValidTarget mutator override hook | OK | - | - | - | - | live | high |
| `turret-fusionreactor.menu.describe` | MENUQC turret-codex help text (describe) | OK | - | - | MISS | - | DEAD | high |

### `turret-hellion` (turret) — 10 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `turret-hellion.identity.def` | Identity, hitbox, models, spawnflags | OK | OK | - | ~ | - | live | high |
| `turret-hellion.setup.tr_setup` | tr_setup: aim/select/firecheck/ammo flags, fluid-inertia tracker | OK | OK | OK | - | - | live | high |
| `turret-hellion.spawn.liveness` | Map spawnfunc wiring + per-frame think (turret_hellion placed on map) | OK | OK | OK | - | - | live | high |
| `turret-hellion.combat.brain` | Acquire/aim/track/fire combat brain (AIM_SIMPLE, shared framework) | OK | OK | OK | - | - | live | high |
| `turret-hellion.weapon.launch` | HellionAttack: launch a heat-seeking missile (2-shot volley) | OK | OK | OK | ~ | OK | live | high |
| `turret-hellion.missile.guidance` | turret_hellion_missile_think: heat-seeking lead-predict + accelerate | OK | OK | OK | - | - | live | high |
| `turret-hellion.missile.detonate` | Missile detonation: touch / proximity / fuel-out / shot-down -> radius damage | OK | OK | OK | - | - | live | high |
| `turret-hellion.anim.headspin` | Launcher head-spin animation (tr_think frame 1..6 -> 0) | OK | OK | OK | MISS | - | live | high |
| `turret-hellion.damage.lifecycle` | Damage gating, retaliation, death + respawn | OK | OK | OK | - | - | live | high |
| `turret-hellion.weapon.aff` | Friendly-fire-avoidance fire gate (TFL_FIRECHECK_AFF) | OK | - | - | - | - | live | high |

### `turret-hk` (turret) — 12 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `turret-hk.identity.def` | Identity, hitbox, models, spawnflags | OK | OK | - | ~ | - | live | high |
| `turret-hk.setup.tr_setup` | tr_setup: aim/select/firecheck/shoot flags, ammo flags | OK | OK | - | - | - | live | high |
| `turret-hk.spawn.liveness` | Map spawnfunc wiring + per-frame think (turret_hk placed on map) | OK | OK | OK | - | - | live | high |
| `turret-hk.combat.brain` | Acquire/aim/track/fire combat brain (shared turret framework) | OK | OK | OK | - | - | live | high |
| `turret-hk.weapon.fire` | wr_think (turret branch): launch the guided rocket | OK | OK | OK | OK | OK | live | high |
| `turret-hk.weapon.guidance` | turret_hk_missile_think: obstacle-avoiding guided-rocket flight model | OK | OK | OK | - | - | live | high |
| `turret-hk.target.recieve` | External target reception (turret_hk_addtarget / TUR_FLAG_RECIEVETARGETS) | OK | OK | OK | - | - | live | high |
| `turret-hk.weapon.player` | WEP_HK player special-attack weapon (hidden, impulse 9) | OK | OK | OK | OK | OK | live | high |
| `turret-hk.damage.lifecycle` | Damage gating, retaliation, death + respawn | OK | OK | OK | - | ? | live | high |
| `turret-hk.anim.head` | tr_think head-frame animation (launcher cycle 0..5) | OK | OK | OK | ~ | - | live | high |
| `turret-hk.head.track` | Head rotation: track type + aim clamps | OK | OK | OK | - | - | live | high |
| `turret-hk.menu.describe` | MENUQC describe: turret info page text | MISS | - | - | ~ | - | DEAD | high |

### `turret-machinegun` (turret) — 14 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `turret-machinegun.spawn.spawnfunc` | Map spawnfunc + g_turrets master gate | OK | OK | - | - | - | live | high |
| `turret-machinegun.spawn.identity` | Identity: model, hitbox, health, team, solidity | OK | OK | - | ~ | - | live | high |
| `turret-machinegun.setup.flags` | tr_setup behaviour flags (target/aim/ammo/damage/turret) | OK | OK | - | - | - | live | high |
| `turret-machinegun.tunables.balance` | Per-unit balance tunables (g_turrets_unit_machinegun_*) | OK | OK | OK | - | - | live | high |
| `turret-machinegun.think.loop` | Per-frame think (ammo regen + acquire/aim/track/fire driver) | OK | OK | OK | - | - | live | high |
| `turret-machinegun.target.validate` | turret_validate_target reject cascade | OK | OK | - | - | - | live | high |
| `turret-machinegun.target.score` | turret_targetscore_generic bias-weighted scoring | OK | OK | - | - | - | live | high |
| `turret-machinegun.aim.lead` | turret_aim_generic lead + shot-traveltime compensation | OK | OK | OK | - | - | live | high |
| `turret-machinegun.track.fluidinertia` | turret_track FLUIDINERTIA head slew | OK | OK | OK | - | - | live | medium |
| `turret-machinegun.fire.gate_volley` | turret_firecheck + turret_fire refire/volley bookkeeping | OK | OK | OK | - | - | live | high |
| `turret-machinegun.weapon.firebullet` | wr_think fireBullet (hitscan bullet + spread + force) | OK | OK | - | OK | OK | live | high |
| `turret-machinegun.lifecycle.use_team` | turret_use: adopt activator team, set active | OK | OK | - | - | - | live | medium |
| `turret-machinegun.lifecycle.damage` | turret_damage gate: inactive-immunity, friendly-fire, headshake | OK | OK | - | ~ | - | live | high |
| `turret-machinegun.lifecycle.die_respawn` | turret_die / turret_respawn (+ death FX, gibs) | ~ | OK | OK | OK | OK | live | high |

### `turret-mlrs` (turret) — 8 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `turret-mlrs.identity.def` | Identity, hitbox, models, spawnflags | OK | OK | - | ~ | - | live | high |
| `turret-mlrs.setup.tr_setup` | tr_setup: ammo/aim/shoot flags, volley seed, select/track defaults | OK | OK | - | - | - | live | high |
| `turret-mlrs.spawn.liveness` | Map spawnfunc wiring + per-frame think (turret_mlrs placed on map) | OK | OK | OK | - | - | live | high |
| `turret-mlrs.combat.brain` | Acquire/aim/lead/track/fire combat brain incl. VOLLYALWAYS burst completion + target scoring | OK | OK | OK | - | - | live | high |
| `turret-mlrs.head.track` | Head rotation: FluidInertia track + aim clamps | OK | OK | OK | - | - | live | high |
| `turret-mlrs.anim.ammogauge` | tr_think head ammo-gauge frame (0 full .. 6 empty by remaining ammo) | OK | OK | OK | ~ | - | live | high |
| `turret-mlrs.weapon.fire` | MLRSTurretAttack: 6-rocket splash volley (unguided, shootable, travel-time fuse) | OK | OK | OK | OK | OK | live | high |
| `turret-mlrs.damage.lifecycle` | Damage gating, head-shake, death + respawn | OK | OK | OK | ~ | OK | live | high |

### `turret-phaser` (turret) — 14 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `turret-phaser.spawn.map_entity` | Map placement + per-frame think wiring (spawnfunc turret_phaser) | OK | OK | OK | - | - | live | high |
| `turret-phaser.identity.model_hitbox` | Identity, hitbox, health, body/head models | OK | OK | - | ~ | - | live | high |
| `turret-phaser.setup.flags` | tr_setup ammo + aim flags (energy/recharge/recieve, lead-only) | OK | OK | - | - | - | live | high |
| `turret-phaser.firecheck.fireflag_guard` | Custom firecheck: block fire while beam active/discharging (fireflag) | OK | OK | OK | - | - | live | high |
| `turret-phaser.fire.beam_spawn` | wr_think: spawn the sustained PhaserTurret_beam | OK | OK | OK | - | - | live | high |
| `turret-phaser.fire.beam_think_trace` | beam_think: per-frame trace, damage + slow, refire reset on end | OK | OK | OK | - | - | live | high |
| `turret-phaser.fire.imobeam_multihit` | FireImoBeam penetrating multi-target hit + slow | OK | OK | OK | - | - | live | high |
| `turret-phaser.fire.bot_dodge` | Beam registers as a dodgeable threat (bot_dodge / g_bot_dodge) | OK | OK | - | - | - | live | high |
| `turret-phaser.fire.target_select_bias` | Per-unit target-selection scoring biases (range/angle/same/missile/player) | OK | OK | - | - | - | live | high |
| `turret-phaser.fire.extra_slow_status` | Port-only layered slow/disability status effect *(intended)* | OK | - | OK | - | - | live | medium |
| `turret-phaser.audio.beam_sound` | Phaser beam sound (start + 2s re-trigger + silence-on-end) and impact cue | OK | OK | OK | - | OK | live | high |
| `turret-phaser.presentation.beam_visual_and_head_anim` | Beam model visual (MDL_TUR_PHASER_BEAM) + head charge/discharge animation (tr_think) | OK | OK | OK | ~ | - | live | medium |
| `turret-phaser.aim.head_track_tuning` | Per-unit head-tracking motor tuning (track_type / accel_pitch / accel_rot / blendrate) | OK | OK | OK | - | - | live | high |
| `turret-phaser.lifecycle.respawn_time` | Death + respawn timer | OK | OK | OK | - | - | live | high |

### `turret-plasma` (turret) — 13 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `turret-plasma.spawn.mapplacement` | Map placement spawnfunc + initialize (turret_plasma / turret_plasma_dual) | OK | OK | OK | - | - | live | high |
| `turret-plasma.identity.model_hitbox` | Identity, hitbox, model, health, weapon binding | OK | OK | - | ~ | - | live | high |
| `turret-plasma.balance.cvars` | Balance constants (damage/refire/range/ammo/aim) from g_turrets_unit_plasma_* | OK | OK | - | - | - | live | high |
| `turret-plasma.setup.flags` | tr_setup flag configuration (ammo/damage/firecheck/aim flags) | OK | OK | - | - | - | live | high |
| `turret-plasma.think.pipeline` | Per-frame think: ammo regen, acquire/scan delays, aim, track, fire gate | OK | OK | OK | - | - | live | high |
| `turret-plasma.target.select_score` | Target validation + bias-weighted scoring + selection | OK | OK | - | - | - | live | high |
| `turret-plasma.aim.lead_splash` | Aim prediction: lead + shot-traveltime compensate + ground splash | OK | OK | OK | - | - | live | high |
| `turret-plasma.track.fluid_inertia` | Head track motor (fluid-inertia) with per-axis pitch/rot clamps | OK | OK | OK | MISS | - | live | high |
| `turret-plasma.attack.plasma_ball` | tr_attack: plasma ball projectile (PlasmaAttack.wr_think) + radius damage | OK | OK | OK | OK | OK | live | high |
| `turret-plasma.attack.instagib_railgun` | tr_attack instagib override: instant railgun beam | OK | OK | OK | OK | OK | live | high |
| `turret-plasma.headspin.frame_anim` | Head spin frame animation after each shot (tr_think frame 1->5) | OK | OK | OK | MISS | - | live | high |
| `turret-plasma.damage.gate_retaliate` | turret_damage: inactive invuln, friendly-fire scale, headshake (no retaliate) | OK | OK | - | - | - | live | high |
| `turret-plasma.death.die_respawn` | Death + respawn (turret_die / turret_hide / turret_respawn) | OK | OK | OK | OK | OK | live | high |

### `turret-plasma_dual` (turret) — 11 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `turret-plasma_dual.spawn.registration` | Map spawnfunc + turret_initialize (registration, model, hitbox, lifecycle) | OK | OK | OK | - | - | live | high |
| `turret-plasma_dual.ai.think_loop` | Per-frame AI: ammo regen, target scan throttle, validate, aim, track, firecheck, fire | OK | OK | OK | - | - | live | high |
| `turret-plasma_dual.attack.plasma_ball` | tr_attack non-instagib: electro-style splash plasma projectile | OK | OK | OK | OK | OK | live | high |
| `turret-plasma_dual.attack.instagib_rail` | tr_attack instagib branch: instant instakill railgun beam | OK | OK | OK | OK | OK | live | high |
| `turret-plasma_dual.values.engagement_envelope` | Engagement envelope: refire, ranges, ammo, aim/track tunables | OK | OK | OK | - | - | live | high |
| `turret-plasma_dual.target.scoring_biases` | Target scoring biases (rangebias/samebias/anglebias/playerbias/missilebias) | OK | OK | - | - | - | live | high |
| `turret-plasma_dual.aim.lead_splash_predict` | Aiming: lead + shot-time compensation + z-predict + splash (aim at feet) | OK | OK | OK | - | - | live | high |
| `turret-plasma_dual.track.head_motor` | Head tracking: FLUIDINERTIA motor, pitch/rot clamps | OK | OK | OK | - | - | live | high |
| `turret-plasma_dual.damage.gating_die_respawn` | Damage handling: friendly-fire scale, MOVE-shove, die + timed respawn | OK | OK | OK | OK | OK | live | high |
| `turret-plasma_dual.damage.headshake` | TFL_DMG_HEADSHAKE: jolt head angles by random*damage on hit | OK | OK | OK | MISS | - | live | high |
| `turret-plasma_dual.presentation.head_frame_anim` | Head-frame wheel animation (tr_think 0..6 + ++frame on fire) | OK | OK | OK | MISS | - | live | high |

### `turret-tesla` (turret) — 8 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `turret-tesla.identity.def` | Identity, hitbox, models, spawnflags | OK | OK | - | ~ | - | live | high |
| `turret-tesla.setup.tr_setup` | tr_setup: select/validate flags, ammo flags, custom firecheck, no aim/track | OK | OK | - | - | - | live | high |
| `turret-tesla.damage.lifecycle` | Damage gating, ammo regen, death blast + respawn | OK | OK | OK | - | - | live | high |
| `turret-tesla.spawn.liveness` | Map spawnfunc wiring + per-frame think (turret_tesla placed on map) | OK | OK | OK | - | - | live | high |
| `turret-tesla.firecheck.custom` | Custom firecheck: rescan throttle + re-validate + cooldown + ammo + target gate | OK | OK | OK | - | - | live | high |
| `turret-tesla.weapon.chain` | Chain-lightning discharge (toast loop): nearest-LOS first hop, 10-hop decay | OK | OK | OK | - | - | live | high |
| `turret-tesla.weapon.arc_fx` | Per-hop lightning arc visual + discharge sound | OK | OK | OK | OK | OK | live | high |
| `turret-tesla.anim.tr_think` | tr_think head spin-up + idle random crackle arc | OK | OK | OK | ~ | - | live | high |

### `turret-walker` (turret) — 12 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `turret-walker.identity.def` | Identity, hitbox, models, spawnflags | OK | OK | - | ~ | - | live | high |
| `turret-walker.setup.tr_setup` | tr_setup: SlideBox/Step creature, select flags, ammo flags, home pose | OK | OK | - | - | - | live | high |
| `turret-walker.spawn.liveness` | Map spawnfunc wiring + per-frame think (turret_walker placed on map) | OK | OK | OK | - | - | live | high |
| `turret-walker.combat.brain` | Acquire/aim/fire combat brain (shared turret_think framework) | OK | OK | OK | - | - | live | high |
| `turret-walker.drive.chase` | tr_think locomotion: chase enemy (run/walk) + body yaw | OK | OK | OK | - | - | live | high |
| `turret-walker.drive.idle_roam` | Idle roam/wander + last-seen pursuit + waypoint path following | OK | OK | OK | - | - | live | high |
| `turret-walker.drive.gaits_extra` | Extra gaits: swim / jump / land / pain / strafe / turn | ~ | OK | OK | MISS | - | live | high |
| `turret-walker.weapon.minigun` | WalkerTurretAttack: near-hitscan minigun (fireBullet + force) | OK | OK | OK | OK | OK | live | high |
| `turret-walker.weapon.rocket` | Rocket volley: 4 homing rockets + reload (walker_fire_rocket / walker_rocket_think) | OK | OK | OK | OK | OK | live | high |
| `turret-walker.weapon.melee` | Melee bite (walker_melee_do_dmg): radius hit in front | OK | OK | OK | - | - | live | high |
| `turret-walker.head.track` | Head rotation: track type + aim clamps | OK | OK | OK | - | - | live | high |
| `turret-walker.client.draw` | CSQC walker_draw + locomotion anim frames | OK | ~ | OK | ~ | - | live | high |

### `vehicle-bumblebee` (vehicle) — 15 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `vehicle-bumblebee.spawn.spawnfunc` | Map spawnfunc + registration (vehicle_bumblebee) | OK | OK | - | - | - | live | high |
| `vehicle-bumblebee.spawn.vr_spawn` | vr_spawn: sub-entities (gun1/gun2/gun3), flags, health/shield/movetype | OK | OK | - | MISS | - | live | high |
| `vehicle-bumblebee.spawn.swim_liquidmask` | swim flag -> dphitcontentsmask liquid behavior | OK | OK | - | - | - | live | high |
| `vehicle-bumblebee.setup.vr_setup` | vr_setup: capability-flag derivation + respawntime | OK | OK | - | - | - | live | high |
| `vehicle-bumblebee.pilot.flight` | Pilot flight controller (avelocity yaw/pitch, thrust, roll, climb) | OK | OK | OK | - | - | live | high |
| `vehicle-bumblebee.pilot.raygun` | Center raygun: heal-beam (default) + damage-beam, target lock, energy gate | OK | OK | OK | ~ | - | live | high |
| `vehicle-bumblebee.gunner.frame` | Side-gunner turret: per-gun lock + lead + aim + plasma fire | OK | OK | OK | ~ | - | live | high |
| `vehicle-bumblebee.gunner.cannon` | Side-gunner plasma cannon projectile (bumblebee_fire_cannon) | OK | OK | - | ~ | OK | live | high |
| `vehicle-bumblebee.regen` | Regen: per-gun cannon ammo + body shield/energy/health | OK | OK | OK | - | - | live | high |
| `vehicle-bumblebee.multiseat.enter` | Multi-seat boarding: gunner-slot assignment + role ordering | OK | OK | OK | - | - | live | high |
| `vehicle-bumblebee.multiseat.exit` | Multi-seat exit: gunner eject + pilot exit + auto-land | OK | OK | OK | - | - | live | high |
| `vehicle-bumblebee.board.usekey` | +use board/exit dispatch (PlayerUseKey) | OK | OK | - | - | - | live | high |
| `vehicle-bumblebee.death` | Death: eject, gib toss, blowup, respawn | OK | OK | OK | ~ | OK | live | high |
| `vehicle-bumblebee.impact.bouncepain` | vr_impact bounce pain (vehicles_impact) | OK | OK | OK | - | - | live | high |
| `vehicle-bumblebee.client.presentation` | Client presentation: BRG beam, HUD, crosshairs, models, describe | ~ | ~ | ~ | ~ | - | ~ | high |

### `vehicle-framework` (vehicle) — 23 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `vehicle-framework.init.initialize` | vehicle_initialize — master gate, model/tag setup, placement, init hook | OK | OK | OK | - | - | live | high |
| `vehicle-framework.spawn.respawn` | vehicles_spawn — reset to idle/ownerless/shootable at spawn point | OK | OK | OK | ~ | - | live | high |
| `vehicle-framework.think.tick` | vehicles_think — per-tick think cadence + painframe + W2MODE mirror | OK | OK | OK | OK | - | live | high |
| `vehicle-framework.enter.handshake` | vehicles_enter — board guards, gunner branch, player freeze + link | OK | OK | OK | ~ | - | live | high |
| `vehicle-framework.enter.steal` | g_vehicles_steal — enemy-team boarding (shield zero + intruder waypoint) | OK | OK | - | MISS | - | live | high |
| `vehicle-framework.exit.handshake` | vehicles_exit — eject + restore player, return + recolor vehicle | OK | OK | OK | ~ | - | live | high |
| `vehicle-framework.exit.findgoodexit` | vehicles_findgoodexit — clear drop spot for the ejected pilot | OK | OK | - | - | - | live | high |
| `vehicle-framework.combat.damage` | vehicles_damage — per-weapon rate, shield-then-health, death eject | OK | OK | OK | ~ | OK | live | high |
| `vehicle-framework.combat.heal` | vehicles_heal — health restore with limit + owner percentage mirror | OK | OK | - | - | - | live | high |
| `vehicle-framework.combat.regen` | vehicles_regen / vehicles_regen_resource — shield/energy/health regen with pause | OK | OK | OK | - | - | live | high |
| `vehicle-framework.combat.crush` | vehicles_touch crush + vehicles_crushable — run over players/monsters | OK | OK | - | - | - | live | high |
| `vehicle-framework.combat.impact` | vehicles_impact — fall/collision self-damage | OK | OK | OK | - | - | live | high |
| `vehicle-framework.combat.painframe` | vehicles_painframe — low-health smoke + DMGSHAKE/DMGROLL jitter | OK | OK | OK | OK | - | live | high |
| `vehicle-framework.aim.locktarget` | vehicles_locktarget — homing lock-on build/decay + lock sounds | OK | OK | OK | ~ | OK | live | high |
| `vehicle-framework.aim.aimturret` | vehicle_aimturret — slew turret head toward a world target within limits | OK | OK | OK | - | - | live | high |
| `vehicle-framework.projectile.shared` | vehicles_projectile — generic vehicle bolt/rocket (splash, lifetime, shootable) | OK | OK | OK | ~ | OK | live | high |
| `vehicle-framework.physics.force_fromtag` | vehicles_force_fromtag_hover/maglev + vehicle_altitude — hover springs | OK | OK | - | - | - | live | high |
| `vehicle-framework.respawn.return_waypoint` | vehicles_setreturn / showwp / return — return waypoint + living-vehicle return | OK | OK | OK | MISS | - | live | high |
| `vehicle-framework.impulse.mode_switch` | vehicle_impulse — per-vehicle mode set/cycle before weapon impulses | OK | OK | - | - | - | live | high |
| `vehicle-framework.presentation.hud_camera_xhair` | Vehicles_drawHUD / drawCrosshair / AuxiliaryXhair / CSQCVehicleSetup + cockpit camera | OK | OK | - | ~ | OK | live | high |
| `vehicle-framework.hooks.init_touch` | MUTATOR_CALLHOOK(VehicleInit / VehicleTouch) dispatch | OK | - | - | - | - | live | high |
| `vehicle-framework.lifecycle.reset` | vehicles_reset — round/match-restart eject + clearreturn + respawn (.reset hook) | OK | - | - | - | - | live | high |
| `vehicle-framework.death.gibs` | vehicle_tossgib / vehicles_gib_* — flying debris gibs on vehicle death | OK | OK | OK | OK | OK | live | high |

### `vehicle-racer` (vehicle) — 13 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `vehicle-racer.identity.def` | Identity, hitbox, models, spawnflags | OK | OK | - | ~ | - | live | high |
| `vehicle-racer.spawn.lifecycle` | vr_spawn / vr_setup: movetype, solid, health/shield/energy, regen capability flags, bounce/mass | OK | OK | - | ~ | - | live | high |
| `vehicle-racer.spawnfunc.liveness` | Map spawnfunc wiring + master cvar gate + per-cvar tunable retune | OK | OK | OK | - | - | live | high |
| `vehicle-racer.board.usekey` | +use board / exit + guards (PlayerUseKey -> vehicles_enter/exit) | OK | OK | OK | - | - | live | high |
| `vehicle-racer.drive.hover4point` | racer_align4point: 4 engine springs + pitch/roll torque + stabilizer | OK | OK | OK | - | - | live | high |
| `vehicle-racer.drive.controller` | racer_frame: yaw/pitch/roll toward view, friction, wishmove, downforce | OK | OK | OK | - | - | live | high |
| `vehicle-racer.drive.afterburn` | Afterburn (jump): energy drain + boost thrust (water vs air) + booster/smoke FX + engine-sound state machine | OK | OK | OK | OK | OK | live | high |
| `vehicle-racer.weapon.cannon` | Primary energy laser cannon (rapid, energy-gated) | OK | OK | OK | ~ | OK | live | high |
| `vehicle-racer.weapon.rocket` | Secondary rocket pair (lock-on / homing / ground-hugging) | OK | OK | OK | ~ | OK | live | high |
| `vehicle-racer.weapon.rockethud` | Secondary HUD ammo/reload mirror (vehicle_ammo2 / vehicle_reload2) | OK | OK | OK | OK | - | live | high |
| `vehicle-racer.regen.resources` | Per-frame shield/energy/health regen + player %-stat mirror | OK | OK | OK | - | - | live | high |
| `vehicle-racer.death.blowup` | vr_death tumble -> deadtouch/timed blowup -> radius blast -> respawn | OK | OK | OK | OK | OK | live | high |
| `vehicle-racer.impact.bouncepain` | vr_impact: ram/collision fall-damage (vehicles_impact bouncepain) | OK | OK | OK | - | - | live | high |

### `vehicle-raptor` (vehicle) — 17 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `raptor.spawn.setup` | Spawn / setup (hitbox, resources, capability flags, sub-entities) | OK | OK | OK | ~ | - | live | high |
| `raptor.liveness.spawnfunc` | Map placement spawnfunc + live think wiring | OK | OK | OK | - | - | live | high |
| `raptor.enter` | Board / enter (vr_enter) | OK | ~ | OK | MISS | - | live | high |
| `raptor.takeoff` | Vertical takeoff sequence (raptor_takeoff) | OK | OK | OK | ~ | OK | live | high |
| `raptor.flight_controller` | Free-flight avelocity controller (raptor_frame physics) | OK | OK | OK | ~ | OK | live | high |
| `raptor.cannon_aim_lock` | Twin-cannon turret aim + target lock + lead predict | OK | OK | OK | OK | OK | live | medium |
| `raptor.cannon_fire` | Primary fire — twin laser cannon (1-1-2-2 cadence) | OK | OK | OK | ~ | OK | live | high |
| `raptor.bombs` | Secondary — cluster bombs burst into bomblets | OK | OK | OK | ~ | - | live | high |
| `raptor.flares` | Secondary — decoy flares (missile seduction) | OK | OK | OK | MISS | - | live | medium |
| `raptor.mode_switch` | Secondary mode switch (bomb/flare select + cycle) | OK | OK | - | OK | - | live | high |
| `raptor.exit_land` | Exit / auto-land (raptor_exit, raptor_land) | OK | OK | OK | ~ | - | live | high |
| `raptor.impact` | Bounce impact damage / occupant shake (vr_impact, bouncepain) | OK | OK | OK | - | MISS | live | high |
| `raptor.death` | Death tumble + blowup (vr_death, raptor_diethink, raptor_blowup) | OK | OK | OK | ~ | OK | live | high |
| `raptor.painframe` | Low-health pain frame (smoke + damage shake/roll jitter) | OK | OK | OK | OK | - | live | high |
| `raptor.hud_crosshair` | CSQC vehicle HUD + dual reticle / bomb dropmark crosshair | OK | OK | OK | OK | - | live | high |
| `raptor.cb_shellfrag_gib` | CSQC cluster-bomb shell-fragment gib effect *(intended)* | OK | ~ | OK | ~ | - | live | high |
| `raptor.missile_alarm` | Incoming guided-missile alarm (per-pilot warning sound) | OK | OK | OK | - | OK | live | high |

### `vehicle-spiderbot` (vehicle) — 16 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `vehicle-spiderbot.identity.registration` | Spiderbot vehicle type registration + identity (model, name, hitbox, view) | OK | OK | - | ~ | - | live | high |
| `vehicle-spiderbot.spawn.map_placement` | vehicle_spiderbot map spawnfunc (g_vehicle_spiderbot gate + vehicle_initialize) | OK | OK | - | - | - | live | high |
| `vehicle-spiderbot.spawn.setup` | vr_setup / vr_spawn — resources, flags, gun sub-entities, movetype | OK | OK | - | - | - | live | high |
| `vehicle-spiderbot.enter` | vr_enter — W2MODE=GUIDE, seat the pilot, reattach a carried CTF flag to the head | ~ | ~ | - | - | - | live | high |
| `vehicle-spiderbot.exit` | spiderbot_exit — unguide in-flight rockets, eject pilot with momentum/ahead-spot | OK | OK | - | - | - | live | high |
| `vehicle-spiderbot.frame.controller` | spiderbot_frame per-frame controller (drive dispatch + game_stopped + pilot glue) | OK | OK | OK | - | - | live | high |
| `vehicle-spiderbot.frame.head_aim` | Head turret aim toward the pilot crosshair (turn + pitch within limits) | OK | OK | OK | - | - | live | high |
| `vehicle-spiderbot.frame.groundalign` | 4-point leg ground alignment (movelib_groundalign4point) | OK | OK | OK | - | - | live | medium |
| `vehicle-spiderbot.frame.locomotion` | Walk/strafe/idle locomotion + body turn + gravity step | OK | OK | OK | - | - | live | high |
| `vehicle-spiderbot.frame.jump` | Directional jump (launch from wishmove) + jump/land latch | OK | OK | OK | OK | OK | live | high |
| `vehicle-spiderbot.weapon.minigun` | Twin alternating-barrel hitscan miniguns (heat/ammo belt, spread, solid penetration) | OK | OK | OK | ~ | OK | live | high |
| `vehicle-spiderbot.weapon.rockets` | 3-mode rocket launcher (VOLLY salvo / GUIDE homing / ARTILLERY lob) + belt reload | OK | OK | OK | ~ | OK | live | high |
| `vehicle-spiderbot.weapon.guide_release` | Guided-rocket steering think + guide-release on button-up | OK | OK | OK | - | - | live | high |
| `vehicle-spiderbot.weapon.modeswitch` | Rocket-mode set/cycle impulses (VOLLY/GUIDE/ARTILLERY) | OK | OK | - | - | - | live | high |
| `vehicle-spiderbot.death.blowup` | vr_death + spiderbot_blowup (burn, gib entities, 250-dmg blast, respawn) | OK | OK | OK | OK | OK | live | high |
| `vehicle-spiderbot.impact.bouncepain` | vr_impact — bounce/fall impact pain (g_vehicle_spiderbot_bouncepain) | OK | - | - | - | - | live | high |

### `overkill-weapons` (weapon) — 17 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `overkill-weapons.okmachinegun.primary_auto` | OK MachineGun primary — auto bullet with accumulating spread | OK | OK | OK | OK | OK | live | high |
| `overkill-weapons.okhmg.primary_auto` | OK Heavy MachineGun primary — superweapon auto bullet | OK | OK | OK | OK | OK | live | high |
| `overkill-weapons.okhmg.superweapon_gate` | OK HMG superweapon gate — fires only while Superweapon active | OK | - | OK | - | - | live | high |
| `overkill-weapons.oknex.primary_rail` | OK Nex primary — instant rail beam | OK | OK | OK | OK | OK | live | medium |
| `overkill-weapons.oknex.charge` | OK Nex charge / chargepool / velocity-charge / wr_glow *(intended)* | stub | ~ | ~ | MISS | MISS | DEAD | medium |
| `overkill-weapons.okshotgun.primary_pellets` | OK Shotgun primary — pellet fan | OK | OK | OK | OK | OK | live | high |
| `overkill-weapons.okrpc.primary_chainsaw_missile` | OK RPC primary — accelerating chainsaw missile (pass-through + explosion) | OK | OK | OK | ~ | OK | live | high |
| `overkill-weapons.shared.secondary_blaster_jump` | Shared secondary — Overkill blaster jump (own jump_interval timer) | OK | OK | OK | OK | OK | live | high |
| `overkill-weapons.shared.forced_reload` | Forced reload when clip below a primary shot | OK | OK | OK | - | OK | live | high |
| `overkill-weapons.shared.checkammo` | Per-weapon ammo checks (wr_checkammo1/2) dispatched to live gate | OK | OK | - | - | - | live | high |
| `overkill-weapons.okhmg.nadesupport` | OK HMG nade self-damage scaled to 10% (okhmg_nadesupport) | OK | OK | - | - | - | live | high |
| `overkill-weapons.identity.attributes` | Weapon identity (ammo type, impulse, flags, color, models, netname) | OK | OK | - | OK | - | live | high |
| `overkill-weapons.mode.enablement` | Overkill mode enablement (g_overkill → loadout + mutator + weapons live) | OK | OK | - | - | - | ~ | high |
| `overkill-weapons.presentation.fx` | Fire/impact presentation — muzzle flash, casings, recoil, impact effects | - | - | - | OK | OK | live | high |
| `overkill-weapons.shared.bot_ai` | Bot AI fire-button selection (wr_aim) for the five OK weapons | OK | OK | - | - | - | live | high |
| `overkill-weapons.oknex.zoom` | OK Nex sniper zoom (wr_zoom / wr_zoomdir) | MISS | - | - | MISS | - | - | medium |
| `overkill-weapons.mode.weapons_activation` | Standalone ok_weapons activation via g_overkill_weapons (independent of g_overkill) | OK | OK | - | - | - | live | high |

### `weapon-arc` (weapon) — 15 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `weapon-arc.identity.attributes` | Weapon identity / attributes (ammo, impulse, flags, color, models, crosshair, muzzle effect) | OK | OK | - | ~ | - | live | high |
| `weapon-arc.beam.dps_core` | Continuous beam DPS: per-tick trace + damage scaled by frametime/ammo coefficient | OK | OK | OK | - | - | live | high |
| `weapon-arc.beam.curve_direction` | Beam direction curving toward aim, limited by max angle + return speed | OK | OK | OK | - | - | live | high |
| `weapon-arc.beam.heal_teammates` | Beam heals teammates (health + armor) instead of damaging | OK | OK | OK | - | - | live | high |
| `weapon-arc.beam.falloff` | Exponential distance falloff on beam damage + force | OK | OK | - | - | - | live | high |
| `weapon-arc.beam.heat_overheat` | Barrel heat accumulation, overheat jam, and cooldown | ~ | OK | OK | OK | OK | live | high |
| `weapon-arc.beam.burst_variant` | Burst beam (secondary when bolt=0): higher damage/heat/ammo beam | OK | OK | OK | MISS | OK | live | high |
| `weapon-arc.bolt.secondary_burst` | Bolt secondary: fire a burst of bouncing explosive bolts | ~ | OK | ~ | OK | OK | live | high |
| `weapon-arc.bolt.explode_bounce` | Bolt impact: bounce-or-explode with radius damage + shoot-down | OK | OK | OK | OK | OK | live | high |
| `weapon-arc.checkammo` | Ammo checks (wr_checkammo1/2) + out-of-ammo auto-switch | OK | OK | - | - | - | live | high |
| `weapon-arc.beam.visual` | Visible beam rendering (Draw_ArcBeam / ENT_CLIENT_ARC_BEAM) | ~ | OK | ~ | ~ | - | live | high |
| `weapon-arc.heat_persistence` | Heat drop/pickup migration + reset on death (wr_drop/pickup/resetplayer/playerdeath) | ~ | - | - | - | - | live | high |
| `weapon-arc.smoke_overheat_fx` | Arc_Smoke: overheat smoke, overheat fire particles, overheat loop sound | OK | OK | OK | OK | OK | live | high |
| `weapon-arc.bot_aim` | Bot aiming for the beam (wr_aim — beam_botaimspeed / beam_botaimlifetime) | OK | OK | OK | - | - | live | medium |
| `weapon-arc.death_messages` | Kill/suicide death-message selection (wr_killmessage / wr_suicidemessage) | OK | OK | - | OK | - | live | high |

### `weapon-blaster` (weapon) — 12 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `weapon-blaster.identity.registration` | Blaster identity, flags, models, infinite ammo | OK | OK | - | ~ | - | live | high |
| `weapon-blaster.primary.attack` | Primary fire spawns the blaster bolt (W_Blaster_Attack) | OK | OK | OK | OK | OK | live | high |
| `weapon-blaster.primary.balance` | Primary balance constants (damage/force/speed/radius/refire/lifetime) | - | OK | OK | - | - | live | high |
| `weapon-blaster.shot.setup` | Shot setup (W_SetupShot_Dir: trueaim, muzzle origin, punchangle) | OK | OK | - | - | - | live | high |
| `weapon-blaster.shot.velocity` | Bolt launch velocity (W_SetupProjVelocity_Explicit) | OK | OK | - | - | - | live | high |
| `weapon-blaster.bolt.think_lifetime` | Bolt think: MOVETYPE_FLY + remove after lifetime (W_Blaster_Think) | OK | OK | OK | MISS | - | live | high |
| `weapon-blaster.bolt.touch_radiusdamage` | Bolt impact: radius damage + knockback with force_zscale + g_projectiles_interact (W_Blaster_Touch / RadiusDamageForSource) *(intended)* | OK | OK | - | ~ | OK | live | high |
| `weapon-blaster.refire.gate` | Refire/animtime gate (weapon_prepareattack + weapon_thinkf) | OK | OK | OK | - | - | live | high |
| `weapon-blaster.secondary.lastweapon` | Secondary = switch back to previous weapon (W_LastWeapon), not a fire mode | OK | - | - | - | - | live | medium |
| `weapon-blaster.offhand.fire` | Offhand blaster (g_offhand_blaster mutator) fires without switching weapons | OK | OK | OK | - | - | ~ | high |
| `weapon-blaster.bot.aim_dodge` | Bot weapon aim (wr_aim projectile lead) + bot_dodge of incoming bolts | OK | OK | - | - | - | live | high |
| `weapon-blaster.notification.kill_suicide` | Blaster kill/suicide death notifications (wr_killmessage / wr_suicidemessage) | OK | OK | - | OK | - | live | medium |

### `weapon-crylink` (weapon) — 13 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `crylink.identity` | Weapon identity, flags, models, color, balance defaults | OK | OK | OK | OK | - | live | high |
| `crylink.primary.attack` | Primary: 6-spike circular burst (W_Crylink_Attack) | OK | OK | OK | OK | OK | live | high |
| `crylink.secondary.attack` | Secondary: 5-spike tight group, strong pull (W_Crylink_Attack2) | OK | OK | OK | OK | OK | live | high |
| `crylink.projectile.touch_bounce_fade` | Spike touch: faded radius damage, limited bounces, bounce-damage factor | OK | OK | OK | OK | OK | live | high |
| `crylink.linkjoin.converge_on_release` | Link-join: held group converges on release (W_Crylink_LinkJoin) | OK | OK | OK | - | - | live | high |
| `crylink.linkexplode.chain_detonate` | Link-explode: chain-detonate the group on a damaging hit (W_Crylink_LinkExplode) | OK | OK | OK | OK | OK | live | high |
| `crylink.linkjoin.joinexplode_bonus` | Convergence join-explode bonus + EFFECT_CRYLINK_JOINEXPLODE (W_Crylink_LinkJoinEffect_Think) | OK | OK | OK | OK | - | live | high |
| `crylink.ammo.checkammo` | Per-mode ammo check + wait-release ammo guard (wr_checkammo1/2) | OK | OK | - | - | - | live | high |
| `crylink.reload` | Forced reload + clip system (wr_reload) | OK | OK | OK | - | OK | DEAD | high |
| `crylink.fx.trail_muzzle_impact` | Trail (purple plasma), muzzle flash, impact particle, projectile render *(intended)* | OK | OK | OK | OK | - | live | high |
| `crylink.notifications.kill_suicide_message` | Kill/suicide obituary messages (wr_killmessage / wr_suicidemessage) | OK | OK | - | OK | - | live | high |
| `crylink.bot.wr_aim` | Bot aim (wr_aim): bias toward primary/secondary, lead the target | OK | OK | - | - | - | live | high |
| `crylink.menu.describe` | MENUQC weapon-guide prose (describe) | - | - | - | MISS | - | - | high |

### `weapon-devastator` (weapon) — 13 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `weapon-devastator.fire.launch` | Primary fire: launch accelerating rocket (rl_release latch, refire gate, ammo) | OK | OK | OK | - | - | live | high |
| `weapon-devastator.flight.acceleration` | Rocket acceleration from speedstart toward speed | OK | OK | OK | - | - | live | high |
| `weapon-devastator.flight.laser_guide` | Laser guiding: steer rocket toward owner aim (guiderate-capped, goal-point lead) | OK | OK | OK | OK | OK | live | high |
| `weapon-devastator.explode.contact` | Contact / lifetime explosion: radius damage + force_xyscale knockback | OK | OK | OK | OK | OK | live | high |
| `weapon-devastator.explode.remote` | Secondary remote detonation (rl_detonate_later + spawnshield/proximity gate + rocket-jump variant) | OK | OK | OK | OK | OK | live | high |
| `weapon-devastator.rocket.shootdown` | Shootable rocket (event_damage destroys it -> explode) | OK | OK | OK | - | - | live | high |
| `weapon-devastator.firer.transparency` | Rocket transparent to firer (PROJECTILE_MAKETRIGGER SOLID_CORPSE) | OK | OK | - | - | - | live | high |
| `weapon-devastator.ammo.checkammo_reload` | Ammo check / reload / reset-on-death / kill-message | OK | OK | OK | - | - | live | high |
| `weapon-devastator.presentation.flight_fx` | Flying rocket model/trail/spin/light/fly-sound + muzzle flash | OK | OK | OK | ~ | OK | ~ | medium |
| `weapon-devastator.explode.airshot` | Airshot achievement on a mid-air direct enemy kill (ANNCE_ACHIEVEMENT_AIRSHOT) | OK | OK | - | OK | OK | live | high |
| `weapon-devastator.bot.aim` | Per-weapon bot aim + auto-detonation (wr_aim) | OK | OK | OK | - | - | live | high |
| `weapon-devastator.presentation.impact_fx` | Impact explosion effect + sound (wr_impacteffect: EFFECT_ROCKET_EXPLODE + SND_ROCKET_IMPACT) *(intended)* | OK | OK | - | OK | OK | live | high |
| `weapon-devastator.menu.describe` | Weapon-guide description text (MENUQC describe) | - | - | - | OK | - | live | medium |

### `weapon-electro` (weapon) — 19 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `electro.primary.fire_gate` | Primary bolt fire gate (refire + refire2 interlock) | OK | OK | OK | - | - | live | high |
| `electro.primary.bolt_projectile` | Primary bolt projectile (MOVETYPE_FLY, velocity, lifetime, in-flight visual) | OK | OK | OK | OK | OK | live | high |
| `electro.primary.explode_splash` | Bolt explosion splash damage + impact effect/sound | OK | OK | - | OK | OK | live | high |
| `electro.combo.trigger_chain` | Combo: bolt blast triggers nearby orbs into a chained explosion | OK | OK | OK | OK | OK | live | high |
| `electro.primary.midaircombo` | Primary bolt midair-combo (trigger orbs in flight) | OK | OK | OK | OK | OK | DEAD | high |
| `electro.secondary.orb_stream` | Secondary orb stream (count orbs, one per animtime while held) | OK | OK | OK | - | - | live | high |
| `electro.secondary.orb_projectile` | Secondary orb projectile (MOVETYPE_BOUNCE, gravity, shootable HP, in-flight visual) | OK | OK | OK | OK | OK | live | high |
| `electro.secondary.orb_touch_explode` | Orb touch: explode on player vs bounce (+ bounce sound, detonation sound) | OK | OK | - | OK | OK | live | high |
| `electro.secondary.orb_shotdown_combo` | Shoot-down orb converts to a combo blast | OK | OK | - | OK | OK | live | high |
| `electro.ammo_reload` | Ammo checks + reload (cells, combo_safeammocheck) | OK | OK | - | - | - | live | high |
| `electro.deathmessages` | Kill / suicide obituary lines (bolt/orb/combo variants) | OK | OK | - | OK | - | live | high |
| `electro.electrobitch_announce` | ELECTROBITCH airshot announcement (direct bolt kill on flying enemy) | OK | OK | - | OK | OK | live | high |
| `electro.bot_aim` | Bot aim heuristic (primary lead-aim, occasional secondary mortar lob) | OK | OK | - | - | - | live | high |
| `electro.orb.csqc_netlink_draw` | Orb CSQC net-link + scale-pulse / spin draw | OK | ~ | - | ~ | OK | live | medium |
| `electro.describe_guide` | Weapon-guide describe text (MENUQC) | ~ | - | - | ~ | - | live | medium |
| `electro.secondary.sticky_orb` | Sticky orb (stick to surface / follow corpse, MOVETYPE_FOLLOW) *(intended)* | OK | OK | OK | - | - | live | high |
| `electro.combo.explode_over_time` | Combo explode-over-time (combo_duration sustained damage field) *(intended)* | MISS | MISS | MISS | MISS | - | - | high |
| `electro.secondary.orb_limit` | Secondary orb count limit (oldest-orb rubble cap) | OK | OK | OK | - | - | live | high |
| `electro.secondary.orb_damagedbycontents` | Orb destroyed by lava/slime/drowning (damagedbycontents) | OK | OK | OK | - | - | live | high |

### `weapon-fireball` (weapon) — 13 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `weapon-fireball.identity` | Weapon identity/attributes (superweapon flags, color, models, impulse) | OK | OK | - | OK | - | live | high |
| `weapon-fireball.balance` | Balance constants (g_balance_fireball_* primary + secondary) | OK | OK | OK | - | - | live | high |
| `weapon-fireball.primary.chargeup` | Primary charge-up: 5-frame prefire windup (prefire sound + prefire-muzzleflash effects) | OK | OK | OK | OK | OK | live | high |
| `weapon-fireball.primary.launch` | Primary launch: spawn slow large fireball (MOVETYPE_FLY, ±16, fire2 sound, muzzleflash) | OK | OK | OK | ~ | OK | live | high |
| `weapon-fireball.primary.lifetime` | Fireball think: lifetime timeout explode + periodic laser scorch (0.1s) | OK | OK | OK | - | - | live | high |
| `weapon-fireball.primary.explode` | Fireball explode: heavy radius damage on impact/timeout | OK | OK | - | ~ | OK | live | high |
| `weapon-fireball.primary.bfg` | BFG secondary blast: expanding sphere and distance-scaled, LOS-gated damage | OK | OK | - | ~ | - | live | high |
| `weapon-fireball.primary.laserscorch` | Laser scorch: periodically set one nearby enemy alight (weighted nearest, prefer not-burning) | OK | OK | OK | ~ | - | live | high |
| `weapon-fireball.primary.shootable` | Shootable fireball (event_damage destroys it; default health 0) | OK | OK | ~ | - | - | live | high |
| `weapon-fireball.secondary.firemine` | Secondary: lob gravity bouncing firemine (MOVETYPE_BOUNCE, ±4, up-launch) | OK | OK | OK | ~ | OK | live | high |
| `weapon-fireball.secondary.firemine.lifecycle` | Firemine think/touch: scorch nearby, self-destruct at lifetime, ignite-on-touch-or-bounce | OK | OK | OK | OK | OK | live | high |
| `weapon-fireball.burn.model` | Fire damage-over-time (Fire_AddDamage / Fire_ApplyDamage) backing both projectiles' burns | OK | OK | OK | - | - | live | high |
| `weapon-fireball.bot.obituary` | Bot aim (wr_aim) + kill/suicide obituary lines (FIREMINE vs BLAST) | OK | OK | - | OK | - | live | high |

### `weapon-hagar` (weapon) — 12 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `hagar.primary.attack` | Primary rapid-fire straight rocket stream | OK | OK | OK | OK | OK | live | high |
| `hagar.secondary.bounce` | Non-loaded secondary: single bouncing rocket | OK | OK | OK | OK | OK | live | high |
| `hagar.secondary.load_release` | Loaded secondary salvo (release fan + per-shot bias spread) | OK | OK | OK | OK | OK | live | high |
| `hagar.secondary.load_charge_machine` | Hold-to-charge incremental load state machine | OK | OK | OK | OK | OK | live | high |
| `hagar.secondary.load_audio` | Load / beep / warning charge sounds | OK | OK | OK | - | OK | live | high |
| `hagar.damage.shootdown` | Shootable rocket -> burst when shot (W_Hagar_Damage) | OK | OK | OK | OK | OK | live | high |
| `hagar.explosion.radiusdamage` | Explosion: radius damage + knockback | OK | OK | OK | OK | OK | live | high |
| `hagar.fx.muzzle_explode_trail` | Muzzle flash, explosion particle, rocket trail, impact sound, bounce spark *(intended)* | OK | OK | OK | OK | OK | live | high |
| `hagar.hud.load_ring` | Crosshair Hagar load ring | OK | OK | - | OK | - | live | high |
| `hagar.identity` | Weapon identity, flags, models, pickup/switch balance | OK | OK | OK | OK | - | live | high |
| `hagar.notifications.killmessages` | Burst-vs-spray kill message + suicide message | OK | - | - | OK | - | live | high |
| `hagar.load_lifecycle` | Load-state lifecycle: release/give-back on switch-away, death, reset, equip | OK | OK | - | - | OK | live | high |

### `weapon-hlac` (weapon) — 9 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `weapon-hlac.primary.attack` | Primary rapid-fire single bolt with held-fire spread accumulation | OK | OK | OK | - | - | live | high |
| `weapon-hlac.secondary.burst` | Secondary one-shot burst of `shots` scattered bolts | OK | OK | OK | - | - | live | high |
| `weapon-hlac.spread.crouchmod` | Crouch+grounded spread tightening gate | OK | OK | - | - | - | live | high |
| `weapon-hlac.bolt.impact` | Bolt radius-damage burst on touch / lifetime | OK | OK | OK | - | - | live | high |
| `weapon-hlac.projectile.velocity` | Shared projectile-velocity setup (W_SetupProjVelocity_Basic) *(intended)* | OK | OK | - | - | - | live | medium |
| `weapon-hlac.recoil.punchangle` | Per-shot punchangle recoil kick | OK | OK | - | OK | - | live | high |
| `weapon-hlac.ammo.checkreload` | Ammo check + reload (wr_checkammo / wr_reload / forced reload) | OK | OK | OK | - | OK | live | high |
| `weapon-hlac.fx.muzzle_impact` | Muzzle flash, impact effect, fire/impact sounds | OK | OK | - | OK | OK | live | high |
| `weapon-hlac.identity.metadata` | Weapon identity, kill/suicide notifications, registration/liveness | OK | OK | - | OK | - | live | high |

### `weapon-hook` (weapon) — 12 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `weapon-hook.identity.attributes` | Weapon identity, models, color, flags, ammo type | OK | OK | - | OK | - | live | high |
| `weapon-hook.primary.state_machine` | Primary grapple lifecycle (fire/reel/remove hook_state machine) | OK | OK | OK | - | - | live | high |
| `weapon-hook.primary.hooked_fuel_drain` | Hooked-state fuel drain + free grace period | OK | OK | OK | - | - | live | high |
| `weapon-hook.primary.fire_grapplinghook` | FireGrapplingHook: launch the chain projectile | ~ | OK | OK | ~ | OK | live | high |
| `weapon-hook.primary.latch` | GrapplingHookTouch / GrapplingHook_Stop: latch onto what it hits | OK | - | OK | ~ | OK | live | high |
| `weapon-hook.primary.reel_pull` | GrapplingHookThink: reel the firer toward the latch | OK | OK | OK | - | - | live | high |
| `weapon-hook.primary.remove_reset` | RemoveHook / RemoveGrapplingHooks / reset + shoot-down | OK | - | OK | - | - | live | high |
| `weapon-hook.secondary.gravity_bomb` | Secondary: lob the gravity bomb (W_Hook_Attack2) *(intended)* | OK | OK | OK | OK | OK | live | high |
| `weapon-hook.secondary.blast_curve` | Secondary blast: duration-spread power-curve pull (W_Hook_ExplodeThink) | OK | OK | OK | OK | OK | live | high |
| `weapon-hook.offhand.mutator` | Grappling Hook offhand mutator (g_grappling_hook) | OK | OK | OK | - | - | live | high |
| `weapon-hook.mutator.vampirehook` | Vampire Hook (drain HP while hooked to an enemy) | OK | OK | OK | - | - | live | high |
| `weapon-hook.presentation.rope_line` | CSQC rope line rendering (Draw_GrapplingHook) | OK | OK | OK | ? | - | ~ | medium |

### `weapon-machinegun` (weapon) — 14 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `machinegun.identity` | MachineGun registration & identity (flags, color, ammo, models, impulse) | OK | OK | - | - | - | live | high |
| `machinegun.firemode.dispatch` | wr_think fire-mode dispatch (mode 0 vs mode 1, primary/secondary routing) | OK | OK | OK | - | - | live | high |
| `machinegun.fire.auto` | Sustained automatic primary fire (spread accumulation + per-shot damage/ammo) | OK | OK | OK | - | - | live | high |
| `machinegun.fire.burst` | Burst secondary (3-round no-spread burst, count-up scheduling, cooldown) | OK | OK | OK | - | - | live | high |
| `machinegun.fire.single_mode0` | Single 'first'/sustained shot (mode 0 primary + held-fire frame loop + secondary snipe) | OK | OK | OK | - | - | DEAD | high |
| `machinegun.spread.accumulation` | Spread accumulation: time-decay (default) + legacy counter branch | OK | OK | OK | - | - | live | high |
| `machinegun.heat` | Barrel-heat damage multiplier (cold/heat by spread accumulation) | OK | OK | - | - | - | live | high |
| `machinegun.fire.bullet_trace` | Shared bullet trace: spread, wall penetration, distance falloff, force, antilag | OK | OK | OK | OK | - | live | high |
| `machinegun.recoil` | Firing recoil (punchangle random kick unless g_norecoil) | OK | OK | - | OK | - | live | high |
| `machinegun.presentation.muzzle_impact` | Muzzle flash + bullet-impact particle effects + casing ejection | OK | OK | OK | OK | - | live | high |
| `machinegun.audio.impact_ricochet` | Bullet-impact random ricochet sound (SND_RIC_RANDOM) | OK | OK | OK | OK | OK | live | high |
| `machinegun.bot.wr_aim` | Bot fire logic (wr_aim distance-based primary/secondary selection) | OK | OK | - | - | - | live | high |
| `machinegun.notification.kill_suicide` | Kill/suicide obituary (MURDER_SNIPE vs MURDER_SPRAY, THINKING_WITH_PORTALS suicide) | OK | OK | - | OK | - | ~ | high |
| `machinegun.reload.checkammo` | Forced/manual reload (wr_reload) + zoom-suppressing ammo gate (wr_checkammo2) | OK | OK | OK | - | - | DEAD | high |

### `weapon-minelayer` (weapon) — 15 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `minelayer.fire.lay_mine` | Primary fire: lay a tossed mine | OK | OK | OK | OK | OK | ~ | high |
| `minelayer.fire.mine_limit` | Per-player mine limit gate | OK | OK | OK | OK | OK | ~ | high |
| `minelayer.mine.stick` | Mine sticks to BSP surface and locks in place | OK | OK | OK | OK | OK | ~ | high |
| `minelayer.mine.proximity` | Proximity detonation (closer enemy = sooner) | OK | OK | OK | - | OK | ~ | high |
| `minelayer.mine.lifetime_countdown` | Lifetime expiry + countdown warning | OK | OK | OK | - | OK | ~ | high |
| `minelayer.mine.owner_death` | Owner death/disconnect/frozen auto-detonate | OK | - | OK | - | - | ~ | high |
| `minelayer.mine.shootdown` | Mine shoot-down: knock loose / destroy when shot | OK | OK | - | - | - | live | high |
| `minelayer.fire.remote_detonate` | Secondary fire: remote-detonate all placed mines | OK | OK | OK | - | OK | ~ | high |
| `minelayer.mine.explode` | Explosion: radius damage + knockback + fx | OK | OK | - | OK | OK | ~ | high |
| `minelayer.mine.render` | Placed-mine model + networking (visible mine) | - | OK | - | ~ | - | ~ | high |
| `minelayer.ammo.checks` | Ammo checks + reload | OK | OK | OK | - | - | ~ | medium |
| `minelayer.obituary` | Suicide / murder obituary messages | OK | - | - | OK | - | live | high |
| `minelayer.bot.aim` | Bot AI: lay + remote-detonate decision (wr_aim) | OK | OK | OK | - | - | live | high |
| `minelayer.player.reset` | Per-player mine-count reset on respawn (wr_resetplayer) *(intended)* | OK | - | - | - | - | - | high |
| `minelayer.describe` | In-menu weapon-guide prose (describe) | - | - | - | MISS | - | - | high |

### `weapon-mortar` (weapon) — 11 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `mortar.identity` | Weapon identity, flags, models, color | OK | OK | OK | OK | - | live | high |
| `mortar.primary.attack` | Primary: impact grenade launch (type 0) | OK | OK | OK | OK | OK | live | high |
| `mortar.secondary.attack` | Secondary: bouncing grenade launch (type 1) | OK | OK | OK | OK | OK | live | high |
| `mortar.grenade.bounce` | Grenade bounce physics + contact handling (Touch1/Touch2) | OK | OK | OK | OK | OK | live | high |
| `mortar.grenade.lifetime_detonation` | Lifetime timeout + remote-detonate think (Think1) | OK | OK | OK | - | - | live | high |
| `mortar.grenade.shootdown` | Shootable grenade -> explode when destroyed (W_Mortar_Grenade_Damage) | OK | OK | OK | - | - | live | high |
| `mortar.explosion.radiusdamage` | Explosion: radius damage + knockback + remove | OK | OK | OK | OK | OK | live | high |
| `mortar.secondary.remote_detonate` | Secondary remote-detonate of own primaries (remote_detonateprimary) | OK | OK | OK | - | OK | live | high |
| `mortar.fx.projectile_visual` | In-flight grenade model + trail (CSQCProjectile) | OK | OK | OK | OK | - | live | high |
| `mortar.fx.airshot` | Airshot achievement announcement | OK | - | - | - | OK | live | high |
| `mortar.notifications.killmessages` | Bounce-vs-explode kill / suicide obituary | OK | - | - | OK | - | live | high |

### `weapon-porto` (weapon) — 13 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `weapon-porto.identity.attributes` | Weapon identity, models, color, flags, ammo type | OK | OK | - | OK | - | live | high |
| `weapon-porto.fire.launch` | Fire: eye-shot launch of the bouncing portal projectile | OK | OK | OK | - | OK | live | high |
| `weapon-porto.fire.refire_gate` | Fire gating: single-portal latch + porto_forbidden + refire | OK | OK | OK | - | - | live | high |
| `weapon-porto.fire.aim_hold` | Secondary aim-hold (porto_v_angle) in non-secondary mode | OK | OK | OK | - | - | DEAD | high |
| `weapon-porto.touch.placement_tree` | On-touch portal placement decision tree (in/out/combined red->blue) | ~ | OK | OK | - | OK | live | high |
| `weapon-porto.touch.portal_spawn` | Realise the placed portal (Portal_SpawnIn/OutPortalAtTrace) as a warpzone *(intended)* | OK | ? | ? | ? | - | live | medium |
| `weapon-porto.lifecycle.lifetime_self_destruct` | Lifetime self-destruct + success/soft-fail cleanup (Think/Fail/Success) | OK | OK | OK | - | OK | live | high |
| `weapon-porto.lifecycle.death_reset_cleanup` | Death / respawn cleanup (W_Porto_Remove, wr_resetplayer) | OK | OK | OK | - | - | live | high |
| `weapon-porto.presentation.trajectory_preview` | Portal-aim trajectory preview (Porto_Draw reflecting red/blue polyline) | ~ | OK | ? | ? | - | live | medium |
| `weapon-porto.presentation.projectile_render` | Projectile render (PORTO_RED / PORTO_BLUE, trail, scale) | OK | OK | - | OK | - | live | high |
| `weapon-porto.fire.strength_boost` | Strength powerup launch-speed boost | OK | OK | - | - | - | live | high |
| `weapon-porto.bot.aim` | Bot aim/fire decision (wr_aim) | OK | OK | - | - | - | live | high |
| `weapon-porto.bot.dodge_projectile` | Bot dodge rating on the launched porto projectile | OK | OK | - | - | - | live | high |

### `weapon-rifle` (weapon) — 17 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `rifle.identity` | Rifle registration & identity (flags, color, ammo, models, impulse) | OK | OK | - | OK | - | live | high |
| `rifle.think.gates` | wr_think gates — refire + burst-budget accumulator, primary/secondary routing, forced/secondary reload | OK | OK | OK | - | - | live | high |
| `rifle.bullethail` | Held-fire bullethail continuation (W_Rifle_BulletHail/_Continue) | OK | OK | OK | - | - | live | high |
| `rifle.fire.bullet` | W_Rifle_FireBullet — fire `shots` piercing hitscan bullets (damage/spread/penetration/falloff/force/headshot/accuracy) | OK | OK | - | - | - | live | high |
| `rifle.ammo.decrease` | W_DecreaseAmmo — clip-aware ammo consumption per shot | OK | OK | - | - | - | live | high |
| `rifle.reload` | Reload system (wr_reload, forced reload, secondary `reload` flag, wr_checkammo clip term) | OK | OK | OK | OK | OK | live | high |
| `rifle.deathtype.secondary` | HITTYPE_SECONDARY deathtype flag → secondary kill-message variant (hail) | OK | - | - | OK | - | live | high |
| `rifle.fx.muzzleflash` | Muzzle flash effect (EFFECT_RIFLE_MUZZLEFLASH) | OK | OK | - | OK | - | live | medium |
| `rifle.fx.impact` | Bullet impact effect (EFFECT_RIFLE_IMPACT == machinegun_impact) | OK | OK | - | ~ | - | live | medium |
| `rifle.fx.tracer` | Bullet tracer trail (EFFECT_RIFLE primary / EFFECT_RIFLE_WEAK secondary) | OK | OK | - | OK | - | live | high |
| `rifle.audio.fire` | Fire sound (SND_RIFLE_FIRE primary / SND_RIFLE_FIRE2 secondary) | OK | OK | - | - | OK | live | high |
| `rifle.audio.ric` | Bullet impact ricochet sound (SND_RIC_RANDOM) | OK | OK | - | - | OK | live | high |
| `rifle.casings` | Brass casing ejection (g_casings >= 2) | OK | OK | - | OK | - | live | high |
| `rifle.zoom_eye` | Zoom-from-eye shot re-aim (BUTTON_ZOOM/ZOOMSCRIPT) + wr_zoom/wr_zoomdir | OK | - | - | OK | - | live | high |
| `rifle.bot_aim` | Bot aim — riflemooth primary/secondary toggle (wr_aim) | OK | OK | - | - | - | live | high |
| `rifle.resetplayer` | wr_resetplayer — burst accumulator reset on spawn *(intended)* | OK | OK | OK | - | - | live | high |
| `rifle.suicidemessage` | wr_suicidemessage — weapon-suicide obituary (WEAPON_THINKING_WITH_PORTALS easter egg) | OK | OK | - | OK | - | live | high |

### `weapon-seeker` (weapon) — 11 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `weapon-seeker.identity` | Weapon identity / attributes (ammo, impulse, flags, models, color) | OK | OK | - | OK | - | live | high |
| `weapon-seeker.wr_think_dispatch` | wr_think fire dispatch (type 0/1 primary+secondary routing, refire/animtime gating) | OK | OK | OK | - | - | live | high |
| `weapon-seeker.missile_fire` | Homing missile launch (spawn, velocity, ammo, muzzle) | OK | OK | OK | OK | OK | live | high |
| `weapon-seeker.missile_homing` | Missile think: accel/decel speed clamp + turnrate homing + smart world-avoidance | OK | OK | OK | - | - | live | high |
| `weapon-seeker.missile_explode` | Missile explosion (radius damage + knockback + impact cue) | OK | OK | OK | OK | OK | live | high |
| `weapon-seeker.missile_shootdown` | Shootable missile (HP pool, self-damage x0.25, explode-on-death) | OK | OK | - | - | - | live | high |
| `weapon-seeker.flac_fire` | FLAC secondary spray (f_diff muzzle cycle, spread, short-lived explosive) | OK | OK | OK | OK | OK | live | high |
| `weapon-seeker.tag_fire` | Tag dart launch + on-touch tracker registration (with dedupe + impact cue + spark + waypoint) | OK | OK | OK | OK | OK | live | high |
| `weapon-seeker.tag_shootdown` | Shootable tag dart (HP pool, explode-on-death) | OK | OK | - | - | OK | live | high |
| `weapon-seeker.volley_controller` | Tag volley controller (type 0): auto-fire missile_count missiles at the tagged target | OK | OK | OK | - | - | live | high |
| `weapon-seeker.type1_attack` | Type-1 primary: fire missile at closest tagged target with line of sight | OK | - | OK | - | - | live | medium |

### `weapon-shotgun` (weapon) — 13 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `weapon-shotgun.primary.attack_fan` | Primary fire: 12-pellet hitscan fan | OK | OK | OK | - | - | live | high |
| `weapon-shotgun.primary.spread_style` | Pellet spread distribution (hitscan spread style) | OK | OK | - | - | - | live | high |
| `weapon-shotgun.primary.firebullet_falloff` | Per-pellet trace: falloff + solid penetration + force | OK | OK | - | - | - | live | high |
| `weapon-shotgun.primary.setupshot_recoil` | W_SetupShot: trueaim, muzzle offset, recoil punch | OK | OK | - | OK | - | live | high |
| `weapon-shotgun.primary.refire_timer` | Primary refire gating via private shotgun_primarytime | OK | OK | OK | - | - | live | high |
| `weapon-shotgun.secondary.melee_swing` | Secondary melee slap (swing-arc damage + multihit + multi-frame think) | OK | OK | OK | - | - | live | high |
| `weapon-shotgun.secondary.melee_routing` | Secondary routing: melee gate + out-of-ammo auto-melee + alt triple-shot | OK | OK | OK | - | - | live | high |
| `weapon-shotgun.ammo.checkammo_reload` | Ammo checks + reload | OK | OK | OK | - | - | live | high |
| `weapon-shotgun.bot.wr_aim` | Bot aim: melee vs ranged selection | OK | OK | - | - | - | live | high |
| `weapon-shotgun.fx.muzzle_impact` | Muzzleflash + bullet-impact particle *(intended)* | ~ | - | - | ~ | - | live | medium |
| `weapon-shotgun.fx.impact_ricochet_audio` | Impact ricochet sound + melee woosh + casing eject | OK | OK | OK | OK | OK | live | high |
| `weapon-shotgun.notify.killmessage` | Obituary: slap vs blast kill message + suicide line | OK | OK | - | - | - | live | high |
| `weapon-shotgun.menu.describe` | MENUQC weapon-guide prose (describe) | - | - | - | OK | - | live | high |

### `weapon-tuba` (weapon) — 11 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `weapon-tuba.fire.note_blast` | Held-fire note: refire-gated radius blast centered on player | OK | OK | OK | - | - | live | high |
| `weapon-tuba.deathtype.instrument_bits` | Death-type carries instrument/secondary bits for obituary selection | OK | OK | - | OK | - | live | high |
| `weapon-tuba.reload.instrument_cycle` | Reload cycles instrument Tuba -> Accordion -> Klein Bottle | OK | OK | OK | ~ | - | live | high |
| `weapon-tuba.pitch.getnote` | Note pitch selection from movement state *(intended)* | ~ | ~ | - | - | - | live | high |
| `weapon-tuba.note.sustain_lifetime` | Sustained note entity: spawn, refresh-on-hold, off-on-release, keep-alive | OK | ~ | ~ | - | - | live | high |
| `weapon-tuba.fx.smoke_ring` | Per-note smoke ring puff | OK | OK | OK | ~ | - | live | high |
| `weapon-tuba.audio.note_sound` | Sustained pitched note sound (loop sample, pitch step, fade, attenuation, networking) | OK | OK | ~ | ~ | ? | live | medium |
| `weapon-tuba.melody.recognition` | Melody recognition + magic-ear chat (W_Tuba_HasPlayed / lastnotes) | OK | OK | OK | OK | - | live | high |
| `weapon-tuba.setup.instrument_reset` | wr_setup resets instrument to Tuba on (re)equip | OK | OK | - | - | - | live | high |
| `weapon-tuba.identity.metadata` | Weapon identity, flags, models, ammo, bot aim, registration/liveness | OK | OK | - | ~ | - | live | high |
| `weapon-tuba.describe.guide` | Weapon-guide describe text (MENUQC) | - | - | - | OK | - | live | high |

### `weapon-vaporizer` (weapon) — 14 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `weapon-vaporizer.primary.rail_beam` | Primary rail beam (piercing hitscan + knockback + falloff) | OK | OK | OK | - | - | live | high |
| `weapon-vaporizer.primary.headshot` | Headshot head-AABB test + ANNCE_HEADSHOT | OK | OK | - | - | OK | live | high |
| `weapon-vaporizer.primary.fire_sound` | Primary fire sound (minstanexfire) | OK | OK | OK | - | OK | live | high |
| `weapon-vaporizer.primary.beam_particle` | Rail beam visual (cylindric beam) + muzzle flash | OK | ~ | OK | ~ | - | live | high |
| `weapon-vaporizer.primary.impact_effect` | Beam impact effect + sound (VORTEX_IMPACT / neximpact) | OK | OK | - | OK | OK | live | high |
| `weapon-vaporizer.primary.achievements` | Yoda (airshot) + Impressive (every-2nd headshot) announcer achievements | OK | OK | - | OK | OK | live | high |
| `weapon-vaporizer.secondary.blaster_laser` | Secondary Blaster knockback laser (plain instagib) | OK | OK | OK | - | - | ~ | high |
| `weapon-vaporizer.rm.primary_explosion` | Rocket-Minsta: explosion at the rail endpoint | OK | OK | - | OK | OK | live | high |
| `weapon-vaporizer.rm.laser_barrage` | Rocket-Minsta: secondary bouncing-laser fan + rapid-fire ladder | OK | OK | OK | OK | OK | live | high |
| `weapon-vaporizer.ammo.consumption` | Ammo consumption + checkammo + reload + bot aim | OK | OK | - | - | - | ~ | high |
| `weapon-vaporizer.instagib.integration` | InstaGib integration (start loadout, armor-as-lives, gib, blaster nullify, bleed-out) | OK | OK | OK | OK | OK | live | high |
| `weapon-vaporizer.messages.kill_suicide` | Kill / suicide death messages (WEAPON_VAPORIZER_MURDER / WEAPON_THINKING_WITH_PORTALS) | OK | OK | - | - | - | live | high |
| `weapon-vaporizer.client.reticle` | Client reticle / zoom (wr_init precache + wr_zoom button_zoom) | OK | OK | - | OK | - | live | high |
| `weapon-vaporizer.menu.describe` | MENUQC weapon-guide prose (describe) | - | - | - | OK | - | live | high |

### `weapon-vortex` (weapon) — 17 features

| id | name | L | V | T | P | A | live | conf |
|---|---|---|---|---|---|---|---|---|
| `weapon-vortex.charge.regen_passive` | Passive charge regen toward charge_limit | OK | OK | OK | - | - | live | high |
| `weapon-vortex.charge.regen_velocity` | Velocity charging (charge while moving fast) | OK | OK | OK | - | - | live | medium |
| `weapon-vortex.charge.secondary_ladder` | Zoom-charge / chargepool / sec-ammo charging ladder | OK | OK | OK | - | - | live | high |
| `weapon-vortex.charge.chargepool_regen` | Chargepool regen + health-regen pause | OK | OK | OK | - | - | live | medium |
| `weapon-vortex.attack.charge_damage` | Charge-scaled damage/force + charge consume on fire | OK | OK | OK | - | - | live | high |
| `weapon-vortex.attack.railgun_pierce` | Hitscan rail: pierce all targets, exponential falloff dmg+force, stop at world | OK | OK | OK | - | OK | live | high |
| `weapon-vortex.attack.overcharge_sound` | Overcharge zap sound when charge > charge_animlimit | OK | OK | OK | - | OK | live | high |
| `weapon-vortex.fx.beam` | Charged beam particle (EFFECT_VORTEX_BEAM) with charge-scale + team tint | OK | ~ | OK | ~ | - | live | high |
| `weapon-vortex.fx.charge_glow` | Player-model charge glow (wr_glow / vortex_glowcolor) | OK | OK | OK | OK | - | live | high |
| `weapon-vortex.fx.impact_muzzle` | Impact burst + muzzle flash | OK | OK | OK | OK | OK | live | medium |
| `weapon-vortex.hud.charge_ring` | Charge ring (+ chargepool inner ring) on the crosshair | OK | OK | OK | OK | - | live | high |
| `weapon-vortex.zoom.secondary_scope` | Secondary = zoom scope (reticle_nex) at stock balance | OK | OK | OK | OK | - | live | high |
| `weapon-vortex.ammo.reload_checkammo` | Cells ammo, forced reload, checkammo1/2, charge seed *(intended)* | OK | OK | OK | - | - | live | high |
| `weapon-vortex.achievements` | Yoda (mid-air rail kill) + Impressive (consecutive long-range hit) announcements | OK | - | OK | - | OK | live | high |
| `weapon-vortex.notify.death_messages` | Kill / suicide death messages (WEAPON_VORTEX_MURDER / WEAPON_THINKING_WITH_PORTALS) | OK | OK | - | OK | - | live | high |
| `weapon-vortex.bot.wr_aim` | Bot aim hook (bot_aim straight-shot at 1000000 speed) | OK | OK | OK | - | - | live | high |
| `weapon-vortex.charge.rot_decay` | Per-frame charge decay/rotation toward charge_limit (rot_rate / unequipped rot) | OK | OK | OK | - | - | live | high |
