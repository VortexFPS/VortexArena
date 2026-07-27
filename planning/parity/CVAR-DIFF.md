# Cvar default diff — Base vs port

_Generated 2026-07-05 by `tools/parity-cvar-diff.py`. Entries simulated on both trees: `xonotic-client.cfg`, `xonotic-server.cfg`, `notifications.cfg` (the port's real boot chain — ConfigLoader/MenuState)._

Base tree: `C:\Users\Bryan\Projects\Xonotic\Base\data\xonotic-data.pk3dir` — 6011 effective cvars from 25 files.
Port tree: `C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data\xonotic-data.pk3dir` — 6050 effective cvars from 25 files.

Findings are LEADS for triage, not verdicts: confirm each on the live path, then either fix it
or record it in [cvar-diff-known.yaml](cvar-diff-known.yaml) (intended divergences) so it stops
re-flagging — the same discipline as `intended_divergence` in the registry.

## 1. Chain divergence (files exec'd on one side only, or with differing text)

- exec'd by Base only: `physicsx.cfg`
- exec'd by port only: `physicsbryan.cfg`
- text differs: `physics.cfg`
- text differs: `xonotic-server.cfg`

## 2. Effective value diffs (0)

none.

## 3. One-sided cvars (Base-only: 0, port-only: 0)

Base-only = set by Base's chain but not the port's (unconsumed defaults);
port-only = port additions. Grouped by prefix; full lists in `_cvar-diff.json`.

- **Base-only**: none
- **port-only**: none

## 4. Code-default mismatches vs Base effective (55)

A C# literal default (Register table / Cvars.Defaults / numeric fallback read) that disagrees
with the Base effective value. Bites on any path that reads before/without the cfg chain
(tests, headless boots, unset cvars) — the `hud_panel_centerprint_fade_in` class.

| cvar | Base effective | code literal | where | kind |
|---|---|---|---|---|
| `cl_zoomfactor` | `5` | `2.5` | game/client/FirstPersonView.cs:277 | fallback |
| `cl_zoomfactor` | `5` | `2.5` | game/client/FirstPersonView.cs:302 | fallback |
| `cl_zoomspeed` | `8` | `3.5` | game/client/FirstPersonView.cs:304 | fallback |
| `v_idlescale` | `` | `0` | game/client/FirstPersonView.cs:604 | fallback |
| `cl_deathglow` | `2` | `0` | game/client/ModelTint.cs:193 | fallback |
| `r_hdr_glowintensity` | `1` | `0` | game/client/ModelTint.cs:209 | fallback |
| `hud_panel_centerprint_fade_in` | `0` | `0.15` | game/hud/CenterPrintPanel.cs:283 | Register |
| `con_chatsize` | `10` | `8` | game/hud/ChatPanel.cs:285 | Register |
| `hud_width` | `560` | `0` | game/hud/HudConfig.cs:35 | Register |
| `cl_race_cptimes_showself` | `1` | `0` | game/hud/RaceTimerPanel.cs:143 | Register |
| `hud_panel_weapons_ammo_full_nails` | `320` | `200` | game/hud/WeaponsPanel.cs:642 | Register |
| `hud_panel_weapons_orderbyimpulse` | `1` | `0` | game/hud/WeaponsPanel.cs:658 | Register |
| `cl_netfps` | `64` | `72` | game/menu/framework/ClientSettings.cs:126 | Register |
| `cl_movement_errorcompensation` | `1` | `0` | game/menu/framework/ClientSettings.cs:157 | Register |
| `volume` | `1` | `0.7` | game/menu/framework/ClientSettings.cs:420 | Register |
| `bgmvolume` | `0.75` | `1` | game/menu/framework/ClientSettings.cs:421 | Register |
| `g_powerups` | `-1` | `0` | src/XonoticGodot.Common/Gameplay/GameTypes/ClanArena.cs:513 | fallback |
| `g_pickup_items` | `-1` | `0` | src/XonoticGodot.Common/Gameplay/GameTypes/ClanArena.cs:515 | fallback |
| `g_ctf_allow_vehicle_carry` | `1` | `0` | src/XonoticGodot.Common/Gameplay/GameTypes/Ctf.cs:2119 | fallback |
| `g_respawn_delay_large_count` | `8` | `0` | src/XonoticGodot.Common/Gameplay/GameTypes/EntityGametypeState.cs:230 | fallback |
| `g_nades_bonus_score_high` | `60` | `15` | src/XonoticGodot.Common/Gameplay/GameTypes/KeyHunt.cs:1212 | fallback |
| `g_lms_forfeit_min_match_time` | `30` | `0` | src/XonoticGodot.Common/Gameplay/GameTypes/LastManStanding.cs:671 | fallback |
| `g_balance_armor_regenstable` | `100` | `50` | src/XonoticGodot.Common/Gameplay/Monsters/Mage.cs:355 | fallback |
| `g_balance_armor_regenstable` | `100` | `50` | src/XonoticGodot.Common/Gameplay/Monsters/Mage.cs:447 | fallback |
| `g_monsters_miniboss_chance` | `5` | `0` | src/XonoticGodot.Common/Gameplay/Monsters/MonsterAI.cs:292 | fallback |
| `g_pickup_healthsmall_max` | `200` | `5` | src/XonoticGodot.Common/Gameplay/Mutators/VampireHookMutator.cs:116 | fallback |
| `g_throughfloor_damage` | `0.75` | `0.5` | src/XonoticGodot.Common/Gameplay/Nades/Booms/NadeNormalBoom.cs:69 | fallback |
| `g_throughfloor_force` | `0.75` | `0.7` | src/XonoticGodot.Common/Gameplay/Nades/Booms/NadeNormalBoom.cs:70 | fallback |
| `g_nades_override_dropweapon` | `1` | `0` | src/XonoticGodot.Common/Gameplay/Nades/NadesMutator.cs:110 | fallback |
| `g_freezetag_revive_nade` | `1` | `0` | src/XonoticGodot.Common/Gameplay/Nades/NadesMutator.cs:243 | fallback |
| `g_freezetag_revive_nade` | `1` | `0` | src/XonoticGodot.Common/Gameplay/Nades/NadesMutator.cs:368 | fallback |
| `g_warmup_start_ammo_cells` | `90` | `30` | src/XonoticGodot.Common/Gameplay/Player/SpawnSystem.cs:1213 | fallback |
| `g_vehicles_enter` | `0` | `1` | src/XonoticGodot.Common/Gameplay/Vehicles/Bumblebee.cs:184 | fallback |
| `g_instagib_extralives` | `1` | `0` | src/XonoticGodot.Common/Gameplay/Vehicles/Bumblebee.cs:806 | fallback |
| `g_vehicles_enter` | `0` | `1` | src/XonoticGodot.Common/Gameplay/Vehicles/VehicleCommon.cs:852 | fallback |
| `g_rm_laser_damage` | `80` | `150` | src/XonoticGodot.Common/Gameplay/Weapons/Vaporizer.cs:446 | fallback |
| `g_sandbox` | `0` | `g_sandbox <subcommand> [args] — sandbox build mode (object_spawn/edit/attach/…)` | src/XonoticGodot.Server/Commands.cs:874 | Register |
| `g_warmup_start_ammo_cells` | `90` | `30` | src/XonoticGodot.Server/Cvars.cs:265 | table |
| `g_balance_contents_playerdamage_drowning` | `20` | `30` | src/XonoticGodot.Server/Cvars.cs:270 | table |
| `g_balance_contents_playerdamage_lava_burn_time` | `2.5` | `5` | src/XonoticGodot.Server/Cvars.cs:274 | table |
| `g_balance_contents_playerdamage_slime` | `30` | `40` | src/XonoticGodot.Server/Cvars.cs:275 | table |
| `g_balance_falldamage_factor` | `0.20` | `0.15` | src/XonoticGodot.Server/Cvars.cs:278 | table |
| `bot_ai_custom_weapon_priority_far` | `vaporizer oknex vortex rifle electro devastator mortar hagar hlac crylink blaster okmachinegun machinegun fireball seeker okshotgun shotgun tuba minelayer` | `` | src/XonoticGodot.Server/Cvars.cs:342 | table |
| `bot_ai_custom_weapon_priority_mid` | `vaporizer devastator oknex vortex fireball seeker mortar electro okmachinegun machinegun arc crylink hlac hagar okshotgun shotgun blaster rifle tuba minelayer` | `` | src/XonoticGodot.Server/Cvars.cs:343 | table |
| `bot_ai_custom_weapon_priority_close` | `vaporizer oknex vortex okshotgun shotgun okmachinegun machinegun arc hlac tuba seeker hagar crylink mortar electro devastator blaster fireball rifle minelayer` | `` | src/XonoticGodot.Server/Cvars.cs:344 | table |
| `sv_maxidle_alsokickspectators` | `1` | `0` | src/XonoticGodot.Server/Cvars.cs:361 | table |
| `sv_maxidle_slots_countbots` | `1` | `0` | src/XonoticGodot.Server/Cvars.cs:363 | table |
| `hostname` | `Xonotic  Server` | `Xonotic XonoticGodot Server` | src/XonoticGodot.Server/Cvars.cs:370 | table |
| `sv_eventlog_console` | `1` | `0` | src/XonoticGodot.Server/Cvars.cs:424 | table |
| `sv_eventlog_files_nameprefix` | `xonotic` | `ServerLog-` | src/XonoticGodot.Server/Cvars.cs:427 | table |
| `g_playerstats_gamereport_uri` | `https://stats.xonotic.org/stats/submit` | `` | src/XonoticGodot.Server/Cvars.cs:431 | table |
| `lastlevel` | `` | `0` | src/XonoticGodot.Server/Cvars.cs:503 | table |
| `g_pickup_items` | `-1` | `1` | src/XonoticGodot.Server/Cvars.cs:520 | table |
| `g_pickup_weapons_anyway` | `1` | `0` | src/XonoticGodot.Server/Cvars.cs:523 | table |
| `g_powerups` | `-1` | `1` | src/XonoticGodot.Server/Cvars.cs:526 | table |

## 5. Base-effective cvars never referenced in port source (1555)

No string-literal occurrence anywhere under src/ or game/ — dead-setting candidates (the
graphics-stub class) or subsystems the port genuinely lacks. Interpolated reads
($"g_balance_{X}_damage") ARE pattern-matched; `+`-concatenated names are NOT (a lead here
may be read via string concat — check before filing). `g_physics_<set>_*` and
`notification_*` are excluded wholesale. Grouped by prefix; full list in `_cvar-diff.json`.

- `g_` ×952: `g_as_respawn_delay_large`, `g_as_respawn_delay_large_count`, `g_as_respawn_delay_max`, `g_as_respawn_delay_small`, `g_as_respawn_delay_small_count`, `g_as_respawn_waves`, `g_as_weapon_stay`, `g_assault` …
- `cl_` ×103: `cl_accuracy_data_receive`, `cl_accuracy_data_share`, `cl_arcbeam_simple`, `cl_areagrid_link_SOLID_NOT`, `cl_autodemo_delete_keepmatches`, `cl_autodemo_delete_keeprecords`, `cl_autodemo_nameformat`, `cl_bobmodel_side` …
- `help_` ×72: `help_msg_0`, `help_msg_1`, `help_msg_10`, `help_msg_11`, `help_msg_12`, `help_msg_13`, `help_msg_14`, `help_msg_15` …
- `sv_` ×71: `sv_accuracy_data_send`, `sv_accuracy_data_share`, `sv_allowdownloads`, `sv_allowdownloads_inarchive`, `sv_areagrid_link_SOLID_NOT`, `sv_autopause`, `sv_clmovement_inputtimeout`, `sv_cullentities_trace` …
- `r_` ×41: `r_bloom_blur`, `r_bloom_brighten`, `r_bloom_colorexponent`, `r_bloom_colorscale`, `r_bloom_colorsubtract`, `r_bloom_resolution`, `r_bloom_scenebrightness`, `r_cullentities_trace` …
- `menu_` ×28: `menu_cl_gunalign`, `menu_forced_saved_cvars`, `menu_gamemenu`, `menu_mouse_speed`, `menu_no_music_nor_welcome`, `menu_picmip_bypass`, `menu_reverted_nonsaved_cvars`, `menu_showboxes` …
- `scoreboard_` ×25: `scoreboard_accuracy`, `scoreboard_accuracy_border_thickness`, `scoreboard_accuracy_doublerows`, `scoreboard_accuracy_nocolors`, `scoreboard_alpha_bg`, `scoreboard_alpha_fg`, `scoreboard_alpha_name`, `scoreboard_alpha_name_self` …
- `scr_` ×22: `scr_conalpha`, `scr_conalpha2factor`, `scr_conalpha3factor`, `scr_conalphafactor`, `scr_conbrightness`, `scr_conforcewhiledisconnected`, `scr_conscroll2_x`, `scr_conscroll2_y` …
- `_` ×19: `_backup_con_chatvars_set`, `_cl_rate`, `_con_chat_maximized`, `_hud_panel_quickmenu_file_from_server`, `_hud_showbinds_reload`, `_menu_credits_export`, `_menu_initialized`, `_menu_vid_desktopfullscreen` …
- `con_` ×18: `con_chat`, `con_chatpos`, `con_chatwidth`, `con_completion_chmap`, `con_completion_devmap`, `con_completion_exec`, `con_completion_gotomap`, `con_completion_playdemo` …
- `hud_` ×18: `hud_contents`, `hud_contents_blur`, `hud_contents_blur_alpha`, `hud_contents_factor`, `hud_contents_lava_color`, `hud_contents_slime_color`, `hud_contents_water_color`, `hud_cursormode` …
- `(bare)` ×16: `debugdraw`, `debugtrace`, `edgefriction`, `freelook`, `joyadvanced`, `joyadvaxisr`, `joyadvaxisx`, `joyadvaxisy` …
- `camera_` ×12: `camera_chase_smoothly`, `camera_enable`, `camera_forward_follows`, `camera_free`, `camera_look_attenuation`, `camera_look_player`, `camera_mouse_threshold`, `camera_reset` …
- `gl_` ×12: `gl_flashblend`, `gl_picmip_other`, `gl_picmip_sprites`, `gl_picmip_world`, `gl_polyblend`, `gl_texturecompression_2d`, `gl_texturecompression_color`, `gl_texturecompression_gloss` …
- `vid_` ×8: `vid_conwidth`, `vid_desktopfullscreen`, `vid_gl13`, `vid_netwmfullscreen`, `vid_pixelheight`, `vid_sRGB`, `vid_sRGB_fallback`, `vid_x11_display`
- `joy_` ×7: `joy_deadzoneforward`, `joy_deadzonepitch`, `joy_deadzoneside`, `joy_deadzoneup`, `joy_deadzoneyaw`, `joy_sensitivitypitch`, `joy_sensitivityyaw`
- `bot_` ×5: `bot_ai_dodgeupdateinterval`, `bot_ai_navigation_jetpack`, `bot_ai_navigation_jetpack_mindistance`, `bot_debug_goalstack`, `bot_debug_tracewalk`
- `accuracy_` ×4: `accuracy_color0`, `accuracy_color1`, `accuracy_color2`, `accuracy_color_levels`
- `mod_` ×4: `mod_q3bsp_sRGBlightmaps`, `mod_q3shader_default_polygonfactor`, `mod_q3shader_default_polygonoffset`, `mod_q3shader_force_terrain_alphaflag`
- `debug_` ×3: `debug_text_3d_default_align`, `debug_text_3d_default_duration`, `debug_text_3d_default_velocity`
- `net_` ×3: `net_connecttimeout`, `net_messagetimeout`, `net_slist_queriespersecond`
- `snd_` ×3: `snd_cdautopause`, `snd_identicalsoundrandomization_tics`, `snd_identicalsoundrandomization_time`
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
- `debugdraw_` ×2: `debugdraw_filter`, `debugdraw_filterout`
- `rpn_` ×2: `rpn_linear_to_sRGB`, `rpn_sRGB_to_linear`
- `developer_` ×1: `developer_csqcentities`
- `locs_` ×1: `locs_enable`
- `posview_` ×1: `posview_verbose`
- `quit_` ×1: `quit_and_redirect_timer`
- `rescan_` ×1: `rescan_pending`
- `sbar_` ×1: `sbar_info_pos`
- `spawn_` ×1: `spawn_debug`
- `v_` ×1: `v_kicktime`
- `waypoint_` ×1: `waypoint_benchmark`