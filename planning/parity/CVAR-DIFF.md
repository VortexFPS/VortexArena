# Cvar default diff — Base vs port

_Generated 2026-11-19 by `tools/parity-cvar-diff.py`. Simulated entries (Vortex layer on port only): `xonotic-client.cfg`, `xonotic-server.cfg`, `notifications.cfg`, `vortex-common.cfg`. This compares equivalent configuration inputs. It does not simulate the original engine's entire startup or a saved player profile._

Upstream (`unverified source tree`) — 6011 effective cvars from 25 files.
Port — 6029 effective cvars from 31 files.

Findings are LEADS for triage, not verdicts: confirm each on the live path, then either fix it
or record it in [cvar-diff-known.yaml](cvar-diff-known.yaml) (intended divergences) so it stops
re-flagging — the same discipline as `intended_divergence` in the registry.

## 1. Chain divergence (files exec'd on one side only, or with differing text)

- exec'd by port only: `vortex-bal-wep.cfg`
- exec'd by port only: `vortex-balance.cfg`
- exec'd by port only: `vortex-client.cfg`
- exec'd by port only: `vortex-common.cfg`
- exec'd by port only: `vortex-physics.cfg`
- exec'd by port only: `vortex-server.cfg`
- text differs: `binds-xonotic.cfg`
- text differs: `effects-normal.cfg`
- text differs: `gametypes-client.cfg`
- text differs: `notifications.cfg`

## 2. Effective value diffs (4)

| cvar | Base | port | set by |
|---|---|---|---|
| `gl_texturecompression` | `0` | `1` | vortex-client.cfg |
| `hostname` | `Xonotic  Server` | `Vortex  Server` | vortex-server.cfg |
| `maxplayers` | `16` | `32` | vortex-server.cfg |
| `menu_maxplayers` | `16` | `32` | vortex-client.cfg |

## 3. One-sided cvars (Base-only: 3, port-only: 16)

Base-only = set by Base's chain but not the port's (unconsumed defaults);
port-only = port additions. Grouped by prefix; full lists in `_cvar-diff.json`.

- **Base-only**: `notification_`×3
- **port-only**: `r_`×7, `notification_`×6, `cl_`×1, `gl_`×1, `sys_`×1

## 4. Code-default mismatches vs Base effective (101)

A C# literal default (Register table / Cvars.Defaults / numeric fallback read) that disagrees
with the Base effective value. Bites on any path that reads before/without the cfg chain
(tests, headless boots, unset cvars) — the `hud_panel_centerprint_fade_in` class.

| cvar | Base effective | code literal | where | kind |
|---|---|---|---|---|
| `r_coronas_occlusionquery` | `0` | `1` | game/client/CoronaRenderer.cs:75 | fallback |
| `r_drawdecals_drawdistance` | `300` | `0` | game/client/Decals.cs:74 | fallback |
| `r_shadow_shadowmapping` | `0` | `1` | game/client/FakeShadowRenderer.cs:89 | fallback |
| `cl_zoomfactor` | `5` | `2.5` | game/client/FirstPersonView.cs:282 | fallback |
| `cl_zoomfactor` | `5` | `2.5` | game/client/FirstPersonView.cs:307 | fallback |
| `cl_zoomspeed` | `8` | `3.5` | game/client/FirstPersonView.cs:309 | fallback |
| `v_idlescale` | `` | `0` | game/client/FirstPersonView.cs:628 | fallback |
| `r_shadow_shadowmapping` | `0` | `1` | game/client/LightBudget.cs:197 | fallback |
| `r_shadow_realtime_world_shadows` | `0` | `1` | game/client/LightBudget.cs:199 | fallback |
| `cl_deathglow` | `2` | `0` | game/client/ModelTint.cs:243 | fallback |
| `r_hdr_glowintensity` | `1` | `0` | game/client/ModelTint.cs:259 | fallback |
| `r_shadow_shadowmapping` | `0` | `1` | game/client/ShadowSettings.cs:132 | fallback |
| `r_water_resolutionmultiplier` | `0.25` | `0.5` | game/client/WaterRenderer.cs:133 | fallback |
| `r_shadow_realtime_world_importlightentitiesfrommap` | `0` | `1` | game/client/WorldLightRenderer.cs:173 | fallback |
| `r_shadow_realtime_world_lightmaps` | `1` | `0` | game/client/WorldLightRenderer.cs:367 | fallback |
| `r_drawdecals_drawdistance` | `300` | `0` | game/client/particles/DecalSplats.cs:226 | fallback |
| `con_textsize` | `10` | `8` | game/console/ConsoleOverlay.cs:1406 | fallback |
| `scr_conbrightness` | `0.35` | `1` | game/console/ConsoleOverlay.cs:1525 | fallback |
| `scr_conalphafactor` | `0.8` | `1` | game/console/ConsoleOverlay.cs:1528 | fallback |
| `scr_conscroll_x` | `-0.1` | `0` | game/console/ConsoleOverlay.cs:1533 | fallback |
| `scr_conscroll_y` | `-0.3` | `0` | game/console/ConsoleOverlay.cs:1533 | fallback |
| `scr_conalpha2factor` | `0.3` | `0` | game/console/ConsoleOverlay.cs:1538 | fallback |
| `scr_conscroll2_x` | `0.11` | `0` | game/console/ConsoleOverlay.cs:1540 | fallback |
| `scr_conscroll2_y` | `0.2` | `0` | game/console/ConsoleOverlay.cs:1540 | fallback |
| `scr_conalpha3factor` | `1` | `0` | game/console/ConsoleOverlay.cs:1542 | fallback |
| `con_textsize` | `10` | `8` | game/console/ConsoleOverlay.cs:853 | fallback |
| `hud_panel_centerprint_fade_in` | `0` | `0.15` | game/hud/CenterPrintPanel.cs:284 | Register |
| `con_chatsize` | `10` | `8` | game/hud/ChatPanel.cs:290 | Register |
| `hud_width` | `560` | `0` | game/hud/HudConfig.cs:35 | Register |
| `cl_race_cptimes_showself` | `1` | `0` | game/hud/RaceTimerPanel.cs:143 | Register |
| `scoreboard_columns` | `default` | `` | game/hud/ScoreboardPanel.cs:2761 | Register |
| `hud_panel_weapons_ammo_full_nails` | `320` | `200` | game/hud/WeaponsPanel.cs:643 | Register |
| `hud_panel_weapons_orderbyimpulse` | `1` | `0` | game/hud/WeaponsPanel.cs:659 | Register |
| `bgmvolume` | `0.75` | `1` | game/legacy/LegacyMenuSound.cs:254 | fallback |
| `volume` | `1` | `0.7` | game/menu/framework/ClientSettings.cs:1009 | Register |
| `bgmvolume` | `0.75` | `1` | game/menu/framework/ClientSettings.cs:1010 | Register |
| `scr_conalphafactor` | `0.8` | `1` | game/menu/framework/ClientSettings.cs:215 | Register |
| `scr_conalpha2factor` | `0.3` | `0` | game/menu/framework/ClientSettings.cs:217 | Register |
| `scr_conalpha3factor` | `1` | `0` | game/menu/framework/ClientSettings.cs:219 | Register |
| `scr_conbrightness` | `0.35` | `1` | game/menu/framework/ClientSettings.cs:221 | Register |
| `scr_conscroll_x` | `-0.1` | `0` | game/menu/framework/ClientSettings.cs:223 | Register |
| `scr_conscroll_y` | `-0.3` | `0` | game/menu/framework/ClientSettings.cs:224 | Register |
| `scr_conscroll2_x` | `0.11` | `0` | game/menu/framework/ClientSettings.cs:225 | Register |
| `scr_conscroll2_y` | `0.2` | `0` | game/menu/framework/ClientSettings.cs:226 | Register |
| `con_textsize` | `10` | `8` | game/menu/framework/ClientSettings.cs:229 | Register |
| `cl_movement_errorcompensation` | `1` | `0` | game/menu/framework/ClientSettings.cs:427 | Register |
| `r_coronas_occlusionquery` | `0` | `1` | game/menu/framework/ClientSettings.cs:637 | Register |
| `r_shadow_realtime_world_lightmaps` | `1` | `0` | game/menu/framework/ClientSettings.cs:656 | Register |
| `r_shadow_realtime_world_importlightentitiesfrommap` | `0` | `1` | game/menu/framework/ClientSettings.cs:658 | Register |
| `r_font_size_snapping` | `4` | `1` | game/text/DpText.cs:167 | fallback |
| `r_font_hinting` | `2` | `3` | game/text/DpText.cs:170 | fallback |
| `r_font_postprocess_blur` | `1` | `0` | game/text/DpText.cs:178 | fallback |
| `r_font_postprocess_outline` | `1` | `0` | game/text/DpText.cs:178 | fallback |
| `r_font_postprocess_blur` | `1` | `0` | game/text/DpText.cs:319 | fallback |
| `r_font_postprocess_outline` | `1` | `0` | game/text/DpText.cs:319 | fallback |
| `g_powerups` | `-1` | `0` | src/VortexArena.Common/Gameplay/GameTypes/ClanArena.cs:511 | fallback |
| `g_pickup_items` | `-1` | `0` | src/VortexArena.Common/Gameplay/GameTypes/ClanArena.cs:513 | fallback |
| `g_ctf_allow_vehicle_carry` | `1` | `0` | src/VortexArena.Common/Gameplay/GameTypes/Ctf.cs:2224 | fallback |
| `g_respawn_delay_large_count` | `8` | `0` | src/VortexArena.Common/Gameplay/GameTypes/EntityGametypeState.cs:230 | fallback |
| `g_nades_bonus_score_high` | `60` | `15` | src/VortexArena.Common/Gameplay/GameTypes/KeyHunt.cs:1285 | fallback |
| `g_lms_forfeit_min_match_time` | `30` | `0` | src/VortexArena.Common/Gameplay/GameTypes/LastManStanding.cs:671 | fallback |
| `g_balance_armor_regenstable` | `100` | `50` | src/VortexArena.Common/Gameplay/Monsters/Mage.cs:355 | fallback |
| `g_balance_armor_regenstable` | `100` | `50` | src/VortexArena.Common/Gameplay/Monsters/Mage.cs:447 | fallback |
| `g_monsters_miniboss_chance` | `5` | `0` | src/VortexArena.Common/Gameplay/Monsters/MonsterAI.cs:292 | fallback |
| `g_pickup_healthsmall_max` | `200` | `5` | src/VortexArena.Common/Gameplay/Mutators/VampireHookMutator.cs:122 | fallback |
| `g_throughfloor_damage` | `0.75` | `0.5` | src/VortexArena.Common/Gameplay/Nades/Booms/NadeNormalBoom.cs:69 | fallback |
| `g_throughfloor_force` | `0.75` | `0.7` | src/VortexArena.Common/Gameplay/Nades/Booms/NadeNormalBoom.cs:70 | fallback |
| `g_nades_override_dropweapon` | `1` | `0` | src/VortexArena.Common/Gameplay/Nades/NadesMutator.cs:110 | fallback |
| `g_freezetag_revive_nade` | `1` | `0` | src/VortexArena.Common/Gameplay/Nades/NadesMutator.cs:243 | fallback |
| `g_freezetag_revive_nade` | `1` | `0` | src/VortexArena.Common/Gameplay/Nades/NadesMutator.cs:368 | fallback |
| `g_warmup_start_ammo_cells` | `90` | `30` | src/VortexArena.Common/Gameplay/Player/SpawnSystem.cs:1458 | fallback |
| `g_vehicles_enter` | `0` | `1` | src/VortexArena.Common/Gameplay/Vehicles/Bumblebee.cs:184 | fallback |
| `g_instagib_extralives` | `1` | `0` | src/VortexArena.Common/Gameplay/Vehicles/Bumblebee.cs:806 | fallback |
| `g_vehicles_enter` | `0` | `1` | src/VortexArena.Common/Gameplay/Vehicles/VehicleCommon.cs:852 | fallback |
| `g_rm_laser_damage` | `80` | `150` | src/VortexArena.Common/Gameplay/Weapons/Vaporizer.cs:447 | fallback |
| `net_slist_queriespersecond` | `` | `128` | src/VortexArena.Legacy/Menu/MenuHostCache.cs:653 | fallback |
| `net_connecttimeout` | `30` | `15` | src/VortexArena.Legacy/Server/SvServer.Send.cs:138 | fallback |
| `sv_cullentities_trace` | `1` | `0` | src/VortexArena.Legacy/Server/SvServer.Send.cs:812 | fallback |
| `sv_cullentities_trace_eyejitter` | `0` | `16` | src/VortexArena.Legacy/Server/SvServer.Send.cs:819 | fallback |
| `sv_cullentities_trace_prediction_time` | `0.4` | `0.2` | src/VortexArena.Legacy/Server/SvServer.Send.cs:898 | fallback |
| `sv_cullentities_trace_eyejitter` | `0` | `16` | src/VortexArena.Legacy/Server/SvServer.Send.cs:947 | fallback |
| `sv_clmovement_inputtimeout` | `0.06249` | `0.1` | src/VortexArena.Legacy/Server/SvServer.User.cs:211 | fallback |
| `sv_allowdownloads` | `0` | `1` | src/VortexArena.Legacy/Server/SvServer.User.cs:355 | fallback |
| `sv_allowdownloads_inarchive` | `1` | `0` | src/VortexArena.Legacy/Server/SvServer.User.cs:356 | fallback |
| `net_messagetimeout` | `30` | `300` | src/VortexArena.Legacy/Server/SvServer.cs:330 | fallback |
| `net_connecttimeout` | `30` | `15` | src/VortexArena.Legacy/Server/SvServer.cs:330 | fallback |
| `g_sandbox` | `0` | `g_sandbox <subcommand> [args] — sandbox build mode (object_spawn/edit/attach/…)` | src/VortexArena.Server/Commands.cs:975 | Register |
| `g_warmup_start_ammo_cells` | `90` | `30` | src/VortexArena.Server/Cvars.cs:303 | table |
| `g_balance_contents_playerdamage_drowning` | `20` | `30` | src/VortexArena.Server/Cvars.cs:308 | table |
| `g_balance_contents_playerdamage_lava_burn_time` | `2.5` | `5` | src/VortexArena.Server/Cvars.cs:312 | table |
| `g_balance_contents_playerdamage_slime` | `30` | `40` | src/VortexArena.Server/Cvars.cs:313 | table |
| `g_balance_falldamage_factor` | `0.20` | `0.15` | src/VortexArena.Server/Cvars.cs:316 | table |
| `sv_maxidle_alsokickspectators` | `1` | `0` | src/VortexArena.Server/Cvars.cs:430 | table |
| `sv_maxidle_slots_countbots` | `1` | `0` | src/VortexArena.Server/Cvars.cs:432 | table |
| `hostname` | `Xonotic  Server` | `Vortex Arena Server` | src/VortexArena.Server/Cvars.cs:439 | table |
| `sv_eventlog_console` | `1` | `0` | src/VortexArena.Server/Cvars.cs:525 | table |
| `sv_eventlog_files_nameprefix` | `xonotic` | `ServerLog-` | src/VortexArena.Server/Cvars.cs:528 | table |
| `g_playerstats_gamereport_uri` | `https://stats.xonotic.org/stats/submit` | `` | src/VortexArena.Server/Cvars.cs:532 | table |
| `lastlevel` | `` | `0` | src/VortexArena.Server/Cvars.cs:604 | table |
| `g_pickup_weapons_anyway` | `1` | `0` | src/VortexArena.Server/Cvars.cs:622 | table |
| `g_powerups` | `-1` | `1` | src/VortexArena.Server/Cvars.cs:625 | table |

## 5. Base-effective cvars never referenced in port source (1293)

No string-literal occurrence anywhere under src/ or game/ — dead-setting candidates (the
graphics-stub class) or subsystems the port genuinely lacks. Interpolated reads
($"g_balance_{X}_damage") ARE pattern-matched; `+`-concatenated names are NOT (a lead here
may be read via string concat — check before filing). `g_physics_<set>_*` and
`notification_*` are excluded wholesale. Grouped by prefix; full list in `_cvar-diff.json`.

- `g_` ×913: `g_as_respawn_delay_large`, `g_as_respawn_delay_large_count`, `g_as_respawn_delay_max`, `g_as_respawn_delay_small`, `g_as_respawn_delay_small_count`, `g_as_respawn_waves`, `g_as_weapon_stay`, `g_ballistics_density_player` …
- `help_` ×72: `help_msg_0`, `help_msg_1`, `help_msg_10`, `help_msg_11`, `help_msg_12`, `help_msg_13`, `help_msg_14`, `help_msg_15` …
- `cl_` ×56: `cl_accuracy_data_receive`, `cl_accuracy_data_share`, `cl_arcbeam_simple`, `cl_autodemo_delete_keepmatches`, `cl_autodemo_delete_keeprecords`, `cl_db_saveasdump`, `cl_effects_lightningarc_branchfactor_add`, `cl_effects_lightningarc_branchfactor_start` …
- `sv_` ×42: `sv_accuracy_data_send`, `sv_accuracy_data_share`, `sv_autopause`, `sv_curl_serverpackages_auto`, `sv_db_saveasdump`, `sv_defaultcharacter`, `sv_defaultcharacterskin`, `sv_defaultplayercolors` …
- `scoreboard_` ×24: `scoreboard_accuracy`, `scoreboard_accuracy_border_thickness`, `scoreboard_accuracy_doublerows`, `scoreboard_accuracy_nocolors`, `scoreboard_alpha_bg`, `scoreboard_alpha_fg`, `scoreboard_alpha_name`, `scoreboard_alpha_name_self` …
- `_` ×16: `_backup_con_chatvars_set`, `_con_chat_maximized`, `_hud_panel_quickmenu_file_from_server`, `_hud_showbinds_reload`, `_menu_credits_export`, `_menu_initialized`, `_nextmap`, `_notarget` …
- `hud_` ×13: `hud_contents`, `hud_contents_blur`, `hud_contents_blur_alpha`, `hud_contents_factor`, `hud_contents_lava_color`, `hud_contents_slime_color`, `hud_contents_water_color`, `hud_damage_blur` …
- `camera_` ×12: `camera_chase_smoothly`, `camera_enable`, `camera_forward_follows`, `camera_free`, `camera_look_attenuation`, `camera_look_player`, `camera_mouse_threshold`, `camera_reset` …
- `(bare)` ×12: `debugdraw`, `debugtrace`, `joyadvanced`, `joyadvaxisr`, `joyadvaxisx`, `joyadvaxisy`, `joyadvaxisz`, `joypitchsensitivity` …
- `con_` ×9: `con_completion_chmap`, `con_completion_devmap`, `con_completion_gotomap`, `con_completion_playermodel`, `con_completion_ply`, `con_completion_tdem`, `con_completion_vdomap`, `con_completion_vmap` …
- `menu_` ×7: `menu_cl_gunalign`, `menu_gamemenu`, `menu_no_music_nor_welcome`, `menu_picmip_bypass`, `menu_showboxes`, `menu_slowmo`, `menu_use_default_hostname`
- `bot_` ×5: `bot_ai_dodgeupdateinterval`, `bot_ai_navigation_jetpack`, `bot_ai_navigation_jetpack_mindistance`, `bot_debug_goalstack`, `bot_debug_tracewalk`
- `debug_` ×3: `debug_text_3d_default_align`, `debug_text_3d_default_duration`, `debug_text_3d_default_velocity`
- `userbind10_` ×3: `userbind10_description`, `userbind10_press`, `userbind10_release`
- `userbind11_` ×3: `userbind11_description`, `userbind11_press`, `userbind11_release`
- `userbind12_` ×3: `userbind12_description`, `userbind12_press`, `userbind12_release`
- `userbind13_` ×3: `userbind13_description`, `userbind13_press`, `userbind13_release`
- `userbind14_` ×3: `userbind14_description`, `userbind14_press`, `userbind14_release`
- `userbind15_` ×3: `userbind15_description`, `userbind15_press`, `userbind15_release`
- `userbind16_` ×3: `userbind16_description`, `userbind16_press`, `userbind16_release`
- `userbind17_` ×3: `userbind17_description`, `userbind17_press`, `userbind17_release`
- `userbind18_` ×3: `userbind18_description`, `userbind18_press`, `userbind18_release`
- `userbind19_` ×3: `userbind19_description`, `userbind19_press`, `userbind19_release`
- `userbind1_` ×3: `userbind1_description`, `userbind1_press`, `userbind1_release`
- `userbind20_` ×3: `userbind20_description`, `userbind20_press`, `userbind20_release`
- `userbind21_` ×3: `userbind21_description`, `userbind21_press`, `userbind21_release`
- `userbind22_` ×3: `userbind22_description`, `userbind22_press`, `userbind22_release`
- `userbind23_` ×3: `userbind23_description`, `userbind23_press`, `userbind23_release`
- `userbind24_` ×3: `userbind24_description`, `userbind24_press`, `userbind24_release`
- `userbind25_` ×3: `userbind25_description`, `userbind25_press`, `userbind25_release`
- `userbind26_` ×3: `userbind26_description`, `userbind26_press`, `userbind26_release`
- `userbind27_` ×3: `userbind27_description`, `userbind27_press`, `userbind27_release`
- `userbind28_` ×3: `userbind28_description`, `userbind28_press`, `userbind28_release`
- `userbind29_` ×3: `userbind29_description`, `userbind29_press`, `userbind29_release`
- `userbind2_` ×3: `userbind2_description`, `userbind2_press`, `userbind2_release`
- `userbind30_` ×3: `userbind30_description`, `userbind30_press`, `userbind30_release`
- `userbind31_` ×3: `userbind31_description`, `userbind31_press`, `userbind31_release`
- `userbind32_` ×3: `userbind32_description`, `userbind32_press`, `userbind32_release`
- `userbind3_` ×3: `userbind3_description`, `userbind3_press`, `userbind3_release`
- `userbind4_` ×3: `userbind4_description`, `userbind4_press`, `userbind4_release`
- `userbind5_` ×3: `userbind5_description`, `userbind5_press`, `userbind5_release`
- `userbind6_` ×3: `userbind6_description`, `userbind6_press`, `userbind6_release`
- `userbind7_` ×3: `userbind7_description`, `userbind7_press`, `userbind7_release`
- `userbind8_` ×3: `userbind8_description`, `userbind8_press`, `userbind8_release`
- `userbind9_` ×3: `userbind9_description`, `userbind9_press`, `userbind9_release`
- `vid_` ×3: `vid_gl13`, `vid_netwmfullscreen`, `vid_x11_display`
- `debugdraw_` ×2: `debugdraw_filter`, `debugdraw_filterout`
- `rpn_` ×2: `rpn_linear_to_sRGB`, `rpn_sRGB_to_linear`
- `developer_` ×1: `developer_csqcentities`
- `posview_` ×1: `posview_verbose`
- `quit_` ×1: `quit_and_redirect_timer`
- `rescan_` ×1: `rescan_pending`
- `spawn_` ×1: `spawn_debug`
- `waypoint_` ×1: `waypoint_benchmark`