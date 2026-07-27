using Godot;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Particles;
using XonoticGodot.Engine.Simulation;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// Pushes the relevant cvars from the shared <see cref="MenuState.Cvars"/> store into the live Godot engine —
/// the C# successor to the engine reacting to <c>vid_restart</c> / <c>snd_restart</c> and honoring the
/// <c>bind</c> table. Xonotic's menu writes cvars (<c>vid_fullscreen</c>, <c>vid_width</c>, <c>mastervolume</c>,
/// …) and an "Apply immediately" command button issues the restart that makes them take effect; here those
/// command buttons route through <see cref="MenuCommand"/> to <see cref="ApplyVideo"/> / <see cref="ApplyAudio"/>.
///
/// <see cref="ApplyAll"/> runs once at boot (from <see cref="Shell"/>) so the saved preferences are live before
/// the menu is even shown. The cvar names match the ones the settings dialogs bind to, so a value set in the
/// menu and a value applied here are always the same cvar.
/// </summary>
public static class ClientSettings
{
    /// <summary>Apply every settings group to the live engine (video mode, audio buses).</summary>
    public static void ApplyAll()
    {
        ApplyVideo();
        ApplyAudio();

        // Register the client-effect cvar defaults (the vignette's cl_vignette_* set) into the shared menu/console
        // store at boot, so they're visible/bindable in the menu and console before a match's overlay registers
        // them. Idempotent — keeps any value the user's config already set.
        Client.VignetteOverlay.RegisterDefaults(MenuState.Cvars);
        Client.ReticleOverlay.RegisterDefaults(MenuState.Cvars); // zoom scope reticle (cl_reticle*)
        Client.FrameProfiler.RegisterDefaults(MenuState.Cvars);
        Client.ScreenshotService.RegisterDefaults(MenuState.Cvars); // scr_screenshot_* (the F12 screenshot command)

        // HUD skin + per-panel layout/behaviour cvar defaults (luma), so the menu HUD dialogs and the console
        // can see/drive them and panels read the luma defaults until a config overrides them.
        Hud.HudConfig.RegisterDefaults(MenuState.Cvars);

        RegisterTintDefaults(MenuState.Cvars);
        RegisterStairSmoothDefaults(MenuState.Cvars);
        RegisterEngineClientDefaults(MenuState.Cvars);
        RegisterParticleDefaults(MenuState.Cvars);
    }

    /// <summary>
    /// The dual particle system cvars (planning/particles-dual-system.md §overview): the renderer-mode control
    /// (<c>cl_particles_modern</c> 0/1/2, <c>cl_particles_sdf_*</c>) plus the stock Darkplaces particle/decal
    /// mirrors the faithful backend reads (<c>cl_particles</c>, <c>cl_particles_quality</c>, the per-type gates,
    /// <c>cl_decals_*</c>, <c>r_drawparticles_*</c>). Names + defaults are centralised in <see cref="ParticleCvars"/>;
    /// registering here makes them visible/bindable (the Effects dialog binds the renderer dropdown + SDF
    /// checkbox) and live before the first spawn. Idempotent — keeps any value a cfg already set.
    /// </summary>
    private static void RegisterParticleDefaults(CvarService c)
    {
        foreach ((string name, string def, bool archived) in ParticleCvars.Defaults)
            c.Register(name, def, archived ? CvarFlags.Save : CvarFlags.None);
    }

    /// <summary>
    /// The stock Darkplaces CLIENT engine-cvar defaults that no .cfg line assigns and no other subsystem
    /// registers — the same class as <see cref="RegisterEngineVideoDefaults"/> / <see cref="RegisterEngineAudioDefaults"/>.
    /// These are read across the client (HUD panels, input, net, console) but, being engine cvars registered in
    /// Darkplaces C (not the .cfg tree Xonotic ships), they never entered the cvar store — so they were invisible
    /// to <c>cvarlist</c>/<c>search</c>/Tab-completion and read as a silent 0/"" default. Every default below is
    /// stock-faithful AND preserves the port's current effective behaviour (so registering changes nothing but
    /// visibility): the two read with the <c>!= "0"</c> idiom (absent → ON) are registered "1" to match.
    /// </summary>
    private static void RegisterEngineClientDefaults(CvarService c)
    {
        const CvarFlags save = CvarFlags.Save;
        // FPS / ping HUD readouts — the showfps/showping checkboxes sit right under vid_vsync on the video dialog
        // and were invisible for the identical reason. cl_show* are the DP-native fallbacks FpsPanel/PingPanel read.
        c.Register("showfps", "0", save);
        c.Register("showping", "0", save);
        c.Register("showposition", "0", save);
        c.Register("cl_showfps", "0", save);
        c.Register("cl_showping", "0", save);
        c.Register("cl_showposition", "0", save);
        // PVS-cull escape hatch. WorldPvsCuller registers this in _Ready — which only runs inside a match — so a
        // menu-only session would leave it "allocated, default unknown"; register it eagerly at boot (idempotent
        // with WorldPvsCuller's own Register) so it's a declared cvar with a known default. The whole r_*/cl_*
        // debug/A-B family below is deliberately NOT CvarFlags.Save: Base has no counterpart for these, and a
        // perf-session pin (`--cvar`/console `set`) must never persist into config.cfg. A user who wants one
        // sticky can still `seta` it in the console (DP's allocated-cvar escape keeps it).
        c.Register("r_pvs_cull", "1");
        // (§12.8 A/B) Godot-native occlusion culling — orthogonal to r_pvs_cull, OFF by default (PVS is the
        // shipping path). Eager idempotent register (matches WorldOcclusion's own) so it's a declared cvar.
        c.Register("r_occlusion_cull", "0");
        // (§12.8) DP-faithful entity render culling: hide remote entities outside the camera's PVS — DP draws an
        // entity only when its cluster is in the view's PVS. ON by default (pairs with sv_cullentities_pvs so the
        // client also skips drawing anything that slips through); margin = the half-extent of the bounds box.
        c.Register("r_pvs_cull_entities", "1");
        c.Register("r_pvs_cull_entities_margin", "64");
        // (§12.5) Adaptive world-mesh cell size — scales the spatial split to map size to bound draw calls.
        // adaptive 0 = fixed r_world_cell_size (1024 = today). div ~= cells along the longest axis; min/max clamp.
        c.Register("r_world_cell_adaptive", "0");
        c.Register("r_world_cell_size", "1024");
        c.Register("r_world_cell_div", "8");
        c.Register("r_world_cell_min", "256");
        c.Register("r_world_cell_max", "4096");
        // Mouse pitch/yaw scale (DP cl_input.c defaults, 0.022 deg/count). m_pitch is SIGNED — negative inverts
        // the Y axis (the input dialog's "Invert aiming" writes ±|value|). Pre-DP-formula builds registered
        // m_pitch "1" and used only its sign — archived ±1 values are normalized at read (NetGame.MPitch) and
        // by the invert checkbox, so old configs don't get 45× pitch.
        c.Register("m_pitch", "0.022", save);
        c.Register("m_yaw", "0.022", save);
        // DP mouse filter + acceleration family (cl_input.c:401-412, all CF_ARCHIVE) — applied per render
        // frame by NetGame.FlushMouseLook (Common.Input.MouseAccel); the input dialog's "Smooth aiming"
        // checkbox and acceleration sliders bind m_filter / m_accelerate / minspeed / maxspeed. Stock values
        // make every branch a no-op (DP parity).
        c.Register("m_filter", "0", save);
        // (2026-07-26 wobble hunt) m_smoothdt: rescale the frame's mouse deltas onto the conditioned
        // display-interval timeline (the one cl_smoothdt gives translation), carry-ledgered so swipe
        // totals stay exactly 1:1. Kills the yaw channel's raw frame-time variance — the felt
        // "stutter when moving the mouse". 0 = legacy raw application (A/B).
        c.Register("m_smoothdt", "1", save);
        c.Register("m_accelerate", "1", save);
        c.Register("m_accelerate_minspeed", "5000", save);
        c.Register("m_accelerate_maxspeed", "10000", save);
        c.Register("m_accelerate_filter", "0", save);
        c.Register("m_accelerate_power_offset", "0", save);
        c.Register("m_accelerate_power", "2", save);
        c.Register("m_accelerate_power_senscap", "0", save);
        c.Register("m_accelerate_power_strength", "0", save);
        c.Register("m_accelerate_natural_strength", "0", save);
        c.Register("m_accelerate_natural_accelsenscap", "0", save);
        c.Register("m_accelerate_natural_offset", "0", save);
        // DP pitch clamp (CF_CLIENT, NOT archived): how far you can aim up/down (quake used -70/80).
        c.Register("in_pitch_min", "-90");
        c.Register("in_pitch_max", "90");
        // Server-browser auto-refresh pause toggle (DP default 0).
        c.Register("net_slist_pause", "0");
        // Local-player movement-prediction model (read live by NetGame). DEFAULT 1 = PATH A, the Base-faithful path:
        // predict the local player ONCE per RENDER frame at the real (clamped) frame dt — Xonotic Base's
        // Movetype_Physics_NoMatchTicrate — so the predicted origin lands at the exact render time and moves smoothly
        // at any fps (no fixed-tick→fps aliasing "lurch"). The physics is frametime-independent (half-step gravity →
        // dt-invariant apex; fps-independent strafe speed — see MovementTimingTests), so variable dt is safe; the
        // server stays a fixed 1/72 s authoritative tick. `set cl_movement_perframe 0` = the LEGACY fixed-tick path
        // (drain input in 1/72 s quanta + snap-to-latest render) — kept as an A/B fallback. See
        // [[camera-drift-render-smoothing]] / docs/NET-DEBUGGING.md.
        c.Register("cl_movement_perframe", "1");
        // Sub-tic eye extrapolation (DP partial-final-frame approximation, NetGame.UpdateCamera). Default ON. This
        // LINEAR extrapolation by the leftover input accumulator beats with any fps that isn't a multiple of 72,
        // shoving the eye a few units per hop — a candidate for "inconsistent bunnyhop timing". `set
        // cl_movement_subtic_extrapolate 0` renders the eye at the last simulated tic (no extrapolation) for A/B
        // isolation; the gravity-correct partial-tic sub-step fix supersedes it. NOT gated by cl_movement_smoothing_*.
        c.Register("cl_movement_subtic_extrapolate", "1");
        // Path A send model (only consulted in per-frame / cl_movement_perframe 1 mode). cl_netfps (DP-faithful,
        // default 72) = how many input datagrams/s the client sends; the client still PREDICTS every render frame.
        // cl_movement_send_all (default 0 = Base-faithful): 0 = gate sends to cl_netfps with bounded redundancy
        // (intermediate frames coalesce above ~cl_netfps×redundancy fps, exactly like Darkplaces); 1 = send every
        // predicted frame so the server replays the IDENTICAL command sequence and reconcile stays ~0 (more
        // bandwidth, ideal on a listen server). Read live by NetGame so it A/B-toggles in-session.
        c.Register("cl_netfps", "72", save);
        c.Register("cl_movement_send_all", "0");
        // cl_netimmediatebuttons (DP-faithful, default ON): send a command IMMEDIATELY (bypassing the cl_netfps rate
        // gate) when it carries an impulse or a button-state change (fire/jump/crouch press/release), so above 72 fps
        // those reach the server with minimal latency instead of waiting up to one ~13.9ms interval; steady movement
        // input stays rate-limited to cl_netfps. `set cl_netimmediatebuttons 0` rate-limits everything uniformly.
        c.Register("cl_netimmediatebuttons", "1", save);
        // Render-clock damping (Path A #2): 1 (default) = free-run the render clock and gently creep it toward server
        // time (Base cl_nettimesyncboundmode), 0 = hard-rebase to the latest server time every snapshot (the old
        // behaviour, which jolts the camera/decay timeline when snapshots arrive in lumps). For A/B isolation.
        c.Register("cl_netclock_smooth", "1");
        // (parity audit 2026-07-11) Render-clock cushion: 1 (default) = DP boundmode-5 parity — the slew/step
        // target sits a full MEASURED snapshot interval behind the newest state (Base targets cl.mtime[1], the
        // previous snapshot's time), so the cushion grows exactly when a loaded listen server produces snapshots
        // late and interpolation never rides the f=1 clamp edge (raw tick-lumps). 0 = the r16 half-tick bias.
        c.Register("cl_interp_cushion", "1");
        // (parity audit 2026-07-11) Correction-law A/B: 1 = Base's exact cl_nettimesyncboundmode 5 stepped law
        // (snap >0.5s / halve >0.1s / creep -2ms/+1ms per SNAPSHOT, free-run between snapshots — cl_parse.c:3354),
        // 0 (default) = the r16 continuous rate slew (±5% playback-rate band). Both respect cl_interp_cushion.
        c.Register("cl_netclock_dp5", "0");
        // (WS1, 2026-07-11) sv_threaded — the listen-host threading decision, READ FROM THIS SHARED STORE at
        // StartListenServer (prefix = authority, not reader: it decides the SERVER's drive). DEFAULT ON: the
        // sim worker owns tick+encode with per-tick gate holds; the render thread never blocks on the common
        // path. 0 = single-threaded drive + the unified cvar store. Headless hosts ignore it (always inline).
        c.Register("sv_threaded", "1");
        // Post-hitch stall-aware reconcile (default ON). After a frame HITCH (GC / heavy streaming stalls the shared
        // listen-server thread), the server is transiently behind; this HOLDS a moderate reconcile correction for a
        // few snapshots instead of snapping the camera back, then resumes. Defensive (NOT the cause of any observed
        // bug — the spawn-stutter was the ENet throttle), so it's toggleable: `set cl_movement_hitch_hold 0` reverts
        // to immediate snapping. Rationale + the masking risk it carries: docs/TROUBLESHOOTING.md.
        c.Register("cl_movement_hitch_hold", "1");
        // (r16) DP parity switches: cl_movement 0 = NO client-side movement prediction — the view rides the
        // server-authoritative owner state (velocity-extrapolated over the sub-snapshot remainder); commands
        // still go to the server. The settings-Misc dialog already Dependent-binds on cl_movement (QC parity)
        // — this makes the cvar real. cl_nolerp 1 = remote entities render raw newest snapshots (no
        // two-snapshot interpolation). Both live-toggle in-session; both DP-archived.
        c.Register("cl_movement", "1", save);
        c.Register("cl_nolerp", "0", save);
        // cl_smoothdt: condition the client MOTION path's per-frame dt (rolling-median predicted,
        // hitch-passthrough, drift-corrected to wall time) instead of the measured delta. DEFAULT 0 since
        // 2026-07-26: it shipped ON as the r16 "variance fix" on the strength of an A/B whose "raw" leg was
        // itself engine-mangled (MainTimerSync rewrote the delta — the real wobble, now fixed via
        // physics_jitter_fix 0). Re-A/B'd on the honest clock: no felt difference, so the filter's costs stand
        // unpaid — it is the last dt-side felt-band contributor (0.01% -> 0.46% RMS on a 311 s release
        // capture), it is AUTHORITATIVE (the conditioned dt ships in InputCommand.DeltaTime, so filter error
        // is real movement-speed error), the driftcap sheds real wall time (+0.078% slow), and Base has no dt
        // filter. 1 = the filter, kept as an A/B — see NetGame.ConditionDt for what a "1 feels better" result
        // would mean. Server tick timing is unaffected either way (always raw).
        c.Register("cl_smoothdt", "0");
        // (2026-07-26 wobble audit) cl_smoothdt_driftcap: bound cl_smoothdt's drift-repayment ledger so a
        // large excursion can't ride the ±4% clamp for hundreds of ms (a sustained, FELT speed error — the
        // measured worst case was 1.5s), and widen the hitch gate so a 144↔250fps cadence transition passes
        // raw instead of loading the ledger. Sheds wall-time debt past the cap (DP's own trade under
        // overload). 0 = legacy r16 unbounded ledger (A/B).
        c.Register("cl_smoothdt_driftcap", "1");
        // (2026-07-26 wobble ROOT CAUSE) cl_engine_jitterfix: Godot's physics_jitter_fix, exposed live. It is
        // NOT a physics knob for us — Godot's physics has no consumers here; it is the gate on whether
        // MainTimerSync rewrites the frame delta before _Process sees it. Default 0 = report the measured
        // delta (DP cl.realframetime). 0.5 = Godot stock = the rewriting behaviour, for A/B. Full mechanism +
        // the measured displayed-speed error it produces: ApplyEngineTiming.
        c.Register("cl_engine_jitterfix", "0");
        // (r16) cl_frame_governor: adaptive frame pacing — every second, cap the engine at ~0.98/p75 of the
        // recent frame times so delivery is metronomic while the rate still adapts to load (the property both
        // smooth-feeling controls — vsync ON and a hard-engaged cap — share). 0 = off. An explicit cl_maxfps
        // remains the upper bound; floor 60.
        c.Register("cl_frame_governor", "0");
        c.Register("cl_predictfire", "1");       // intentionally default ON (NetGame: unset → on)
        // Release +attack/+attack2 (until a physical re-press) when the server FORCE-switches a weapon that ran
        // DRY while the trigger is still down, so the switched-to weapon doesn't surprise-fire the moment it
        // raises. Port addition, default ON: Base's cl_unpress_attack_on_weapon_switch covers EVERY switch and
        // ships 0 (holding fire through an out-of-ammo switch opens fire with the new gun); this narrower gate
        // keeps manual-switch hold-fire and non-dry forced cycles (NIX) Base-faithful. 0 = raw Base behavior.
        c.Register("cl_unpress_attack_on_empty_switch", "1");
        // Client-side projectile prediction (CSQC Projectile_Draw): snap+extrapolate vs the old ease. Default
        // ON; `set cl_projectile_prediction 0` reverts for A/B feel-testing (ClientWorld polls it live).
        c.Register("cl_projectile_prediction", "1");
        // Master view-smoothing mode (default 1 = FAITHFUL): render the eye via the Base CSQCPlayer_ApplySmoothing
        // algorithm (stairsmoothz glide + viewheightavg eye-height blend, error compensation forced OFF so
        // corrections SNAP) so the camera matches stock Xonotic exactly and the only intentional divergence is the
        // stepheight processing. 0 = the port path (adaptive stair catch-up + error-comp/knockback glide). Read
        // live by NetGame.UpdateCamera; the Settings→Misc dialog can bind it.
        c.Register("cl_movement_smoothing_faithful", "1");
        // Prediction-error view smoothing strength (stock DP/Xonotic cvar; the Settings→Misc checkbox binds it).
        // Base default is 0 (snap to truth) — a correction the smoother would smear into a drifting camera lag
        // instead lands as a clean snap. Only consulted on the PORT path (faithful mode forces it 0). >0 re-enables
        // the port's decaying error glide. Read live by NetGame via ConfigureErrorSmoothing.
        c.Register("cl_movement_errorcompensation", "0", save);
        // Eye-height smoothing time, seconds (stock Xonotic cl_smoothviewheight, default 0.05): how fast the eye
        // blends to the new view offset on crouch/stand (faithful viewheightavg). 0 = snap. Read live by NetGame.
        c.Register("cl_smoothviewheight", "0.05", save);
        // Knockback view-smoothing window, seconds (PORT EXTENSION, read live by NetGame via ConfigureErrorSmoothing):
        // how long an explosion/blaster shove glides the view instead of popping it. Stock Xonotic discards the spike
        // (it predates predicted jumppads/teleporters); this port smooths it. 0 = pop like stock. See Reconciler.
        c.Register("cl_movement_errorcompensation_force_time", "0.12");
        // Precache ALL weapon view-models at map load (default ON) vs only this match's expected loadout.
        // Warming all (~24) costs a little extra load time, hidden by the loading screen, but removes the
        // 30–300 ms stall the first time the player switches to / picks up / sees an unanticipated weapon
        // (planning/PERFORMANCE_REPORT.md A3). `set cl_precache_all_weapons 0` restores the smart expected-only warm
        // for memory-constrained machines (NetGame.PrecacheWeaponModelsAsync reads it).
        c.Register("cl_precache_all_weapons", "1");
        // Persist the shared asset caches (parsed models, decoded sounds, compiled materials/textures, parsed
        // shaders) across map changes AND server switches (default ON). When ON, every match reuses MenuState's
        // process-lifetime AssetLoader, so the second map load finds the stock assets already hot instead of
        // rebuilding them — the whole per-map model/sound/material warm collapses to cache hits. The cost is
        // that the warmed set stays resident between matches (plus each visited map's external lightmaps
        // accumulate). `set cl_persist_asset_cache 0` restores per-match loaders for memory-constrained machines
        // (NetGame._Ready reads it once when choosing its AssetLoader).
        c.Register("cl_persist_asset_cache", "1");
        // Warm the map-independent eager asset set (weapons, stock player models, combat sounds) into the shared
        // cache at GAME LOAD — in the background while the player sits at the menu — instead of at the first map
        // load (default ON). With cl_persist_asset_cache, this makes the first match's precache a cache hit so the
        // map loads fast; the warm is fully budgeted (heavy IQM parse off-thread, small builds on a per-frame main
        // budget) so the menu never hitches (Shell.StartMenuAssetWarm → MenuAssetWarmer). No effect when
        // persistence is off (a per-match loader wouldn't see the warmed cache). `set cl_warm_at_boot 0` disables.
        c.Register("cl_warm_at_boot", "1");
        // Paint weapon PICKUPS with their weapon's registry color (a two-tone base/icon duotone) and dropped
        // loot with the DROPPER's player colors (default ON). Port-only cosmetic: `set cl_weapon_item_colors 0`
        // restores the stock untinted item look. Read per frame by the ClientWorld item pass.
        c.Register("cl_weapon_item_colors", "1");
        // Off-screen / distant pose-cull for skeletal player models (3.3): when ON, PlayerModel.PushBones is
        // skipped for a REMOTE player whose model is off-screen, and distant on-screen players refresh the
        // Skeleton3D at half rate. The CPU locomotion clock keeps running every frame, so a model going
        // on-screen mid-stride shows a fresh (not stale) pose. Default OFF preserves the port's current
        // behaviour byte-for-byte; it's a perf opt-in (the local player's own model is NEVER skipped).
        // (§13 flip, 2026-06-12) Default ON: off-screen remote players skip the ~50-60 bone-pose interop
        // calls/frame and distant on-screen ones refresh at half rate; the local player is never culled and
        // the locomotion clock always runs (fresh pose on re-entry). `cl_pose_cull 0` restores full-rate.
        c.Register("cl_pose_cull", "1");
        // Quake units: beyond this an ON-SCREEN remote player refreshes at half rate (a per-model phase stagger
        // spreads the work). 0 disables the distance half-rate, keeping only the off-screen skip.
        c.Register("cl_pose_cull_distance", "1500");
        // (§13 flip, 2026-06-12) Default ON: animated MD3s morph in a vertex shader (2 uniforms/frame)
        // instead of re-uploading lerped vertex+normal buffers every frame (the 3.3 Tier-3 item). Eligible
        // models only (StandardMaterial3D surfaces — others keep the CPU path automatically); visual parity
        // verified on the stormkeep item set. `cl_gpu_morph 0` restores the CPU path.
        c.Register("cl_gpu_morph", "1");
        // Spawn-point idle glow + player-spawn flash (Xonotic QC autocvars; the Effects dialog binds the
        // first, makeMulti pokes the second). Stock defaults ON — SpawnPointParticles / the SPAWN effect
        // read these (absent → 0 would silently disable both).
        c.Register("cl_spawn_point_particles", "1", save);
        c.Register("cl_spawn_event_particles", "1", save);
        // Base xonotic-client.cfg:77: the idle glow is culled beyond this range (0 = no distance cull).
        // MUST be registered: SpawnPointParticles reads it live, and an absent cvar reads 0 = "no cull",
        // which would render the glow at EVERY spawn point map-wide (the Base default culls at 1200qu).
        c.Register("cl_spawn_point_dist_max", "1200", save);
        // Console/diagnostics verbosity (DP CF_CLIENT, NOT archived — a debug toggle shouldn't persist).
        c.Register("developer", "0");
        // Net input→movement pipeline diagnostic (default-off; NOT archived — a debug toggle shouldn't persist). `set
        // net_input_trace 1` logs the [nettrace] line every ~0.25s: client push/send → server recv/enq/batch → ENet
        // throttle/loss/rtt → predicted-vs-authoritative origin → reconcile error. The end-to-end view for diagnosing
        // movement/networking issues (it found the ENet packet-throttle spawn-stutter). See docs/NET-DEBUGGING.md.
        c.Register("net_input_trace", "0");
    }

    /// <summary>
    /// The stair-step view-smoothing cvars so the console/menu can see and drive them. <c>cl_stairsmoothspeed</c>
    /// (the Base default, 200) already ships in xonotic-client.cfg; the two <c>cl_stairsmooth_*</c> knobs are PORT
    /// EXTENSIONS that tame the stair "jitter" (NetGame reads all three live each frame via ClientNet.ConfigureStairSmoothing):
    /// <list type="bullet">
    ///   <item><c>cl_stairsmooth_snapspeed</c> (30): airborne <c>|velocity.z|</c> above which the camera snaps to the
    ///         real Z (a jump/fall) instead of smoothing — below it the smoother survives the one-tick onground
    ///         flicker a stair step produces. Kept low so a descent snaps fast: with <c>sv_step_upspeed_max</c>
    ///         capping a step launch, the eye briefly falls after the cap, and a high threshold smoothed-then-jolted
    ///         that fall on every step (the "jittery with the cap set" report).</item>
    ///   <item><c>cl_stairsmooth_catchuptime</c> (0.1): close a step-sized lag within this time so a fast climb stays
    ///         inside the one-step clamp and never yanks the camera. 0 = fixed-speed (old) behaviour.</item>
    /// </list>
    /// </summary>
    private static void RegisterStairSmoothDefaults(CvarService c)
    {
        c.Register("cl_stairsmoothspeed", "200");
        c.Register("cl_stairsmooth_snapspeed", "30");
        c.Register("cl_stairsmooth_catchuptime", "0.1");
    }

    /// <summary>
    /// The dynamic colour-tint cvars (<see cref="Game.WorldTint"/>), so the console/menu can see and drive them.
    /// <c>r_map_tint</c>/<c>r_scene_tint</c> are <c>"r g b"</c> colours (0..1) and the matching <c>_strength</c>
    /// cvars are 0..1, where 0 = off (the default — no tint until you opt in). NOT archived: a tint set for a quick
    /// test shouldn't silently survive a restart and override every map. Maps set their own baseline via worldspawn
    /// keys; a strength cvar &gt; 0 overrides it live (e.g. <c>set r_map_tint "1 0 0"; set r_map_tint_strength 0.6</c>).
    /// </summary>
    private static void RegisterTintDefaults(CvarService c)
    {
        c.Register("r_map_tint", "1 1 1");
        c.Register("r_map_tint_strength", "0");
        c.Register("r_scene_tint", "1 1 1");
        c.Register("r_scene_tint_strength", "0");
        // Lightgrid model lighting (playtest r14). r_model_light_gamma: 1 = DP-faithful gamma-space light
        // response on grid-lit models (display scales linearly with grid light — the "punch"), 0 = plain
        // linear multiply through the tonemap. Default 0: Bryan preferred the linear look in the r15
        // playtest A/B (both keep the full grid shading structure — this only picks the response curve).
        // r_model_light_scale multiplies the grid sample (1 = DP's absolute 1/128 scale). NOT archived,
        // same rationale as the tints: live-tuning knobs.
        c.Register("r_model_light_gamma", "0");
        c.Register("r_model_light_scale", "1");
    }

    /// <summary>
    /// Apply <c>vid_vsync</c> onto the window and describe the resulting behavior in the console. DP applies
    /// vsync INSTANTLY on set — no vid_restart needed — so this is both the vid_restart slice and the live
    /// Changed-hook target (see <see cref="InstallLiveVideoCvars"/>; the 2026-07-11 "vid_restart didn't apply
    /// vsync mid-game" report: the apply worked but Mailbox is uncapped by design, so nothing VISIBLY changed —
    /// the mode's meaning is now spelled out).
    /// Vsync modes (vid_vsync, extended beyond DP's 0/1 — planning/PERFORMANCE_REPORT.md B1):
    ///   0 = off      — no sync; lowest latency, tears.
    ///   1 = on       — classic double-buffered vsync; fps LOCKS to refresh (a missed vblank halves it).
    ///   2 = mailbox  — triple-buffered: renders UNCAPPED, presents the newest frame at vblank. No tearing,
    ///                  no beat-doubling, and deliberately NO fps cap. Recommended for high-refresh displays.
    ///   3 = adaptive — vsync when fast enough, tears instead of stuttering when late.
    /// Drivers that don't support a mode fall back gracefully.
    /// </summary>
    public static void ApplyVsync(CvarService c)
    {
        int mode = (int)c.GetFloat("vid_vsync");
        DisplayServer.VSyncMode vsync = mode switch
        {
            <= 0 => DisplayServer.VSyncMode.Disabled,
            1 => DisplayServer.VSyncMode.Enabled,
            2 => DisplayServer.VSyncMode.Mailbox,
            _ => DisplayServer.VSyncMode.Adaptive,
        };
        DisplayServer.WindowSetVsyncMode(vsync);
        string meaning = vsync switch
        {
            DisplayServer.VSyncMode.Enabled => "classic vsync — fps locks to the refresh rate",
            DisplayServer.VSyncMode.Mailbox => "mailbox — no tearing, fps stays UNCAPPED by design",
            DisplayServer.VSyncMode.Adaptive => "adaptive — syncs when fast enough, tears when late",
            _ => "off — uncapped, may tear",
        };
        // Log the REQUESTED vs ACTUAL vsync mode — drivers can silently reject a mode (esp. Mailbox in windowed
        // mode), and the FrameProfiler env banner reads the mode BEFORE this runs, so this is the authoritative line.
        XonoticGodot.Common.Diagnostics.Log.Info(
            $"[video] vid_vsync {mode} → requested {vsync}, actual {DisplayServer.WindowGetVsyncMode()} ({meaning})");
    }

    /// <summary>Apply <c>cl_maxfps</c> onto <see cref="Godot.Engine.MaxFps"/> (DP applies this live too).</summary>
    public static void ApplyMaxFps(CvarService c)
    {
        // Framerate cap (DP cl_maxfps): 0 = unlimited. Guidance (B1): with vsync off, a cap slightly UNDER the
        // worst-case sustainable rate is smoother than uncapped, and it bounds the per-frame Godot-interop alloc
        // rate (godot#105750). With vid_vsync 2 (mailbox) leave it at 0 (uncapped).
        int maxFps = (int)c.GetFloat("cl_maxfps");
        // "Auto" = the DP-shipped default (256) ONLY: not an fps target anyone chose, and it lets the CPU
        // outrun the swapchain on a fast GPU -> present-jitter hitches, so it gets the engaging ceiling
        // max(144, refresh). Every EXPLICIT choice is the player's and is honored as-is — including the
        // menu's "Unlimited" (0 -> Engine.MaxFps 0, truly uncapped). (2026-07-06: 0 was previously folded
        // into the auto case, silently capping "Unlimited" at 144 — Bryan's uncap request; the perf harness
        // also captures uncapped now so peak frame time and its dips are measured, not masked by the cap.)
        int appliedFps = maxFps == 256
            ? System.Math.Max(144, (int)DisplayServer.ScreenGetRefreshRate())
            : maxFps;
        Godot.Engine.MaxFps = appliedFps;
        XonoticGodot.Common.Diagnostics.Log.Info(
            $"[video] cl_maxfps {maxFps} -> Engine.MaxFps {appliedFps} (refresh {DisplayServer.ScreenGetRefreshRate():0}Hz)");
    }

    /// <summary>
    /// Apply <c>cl_engine_jitterfix</c> onto <see cref="Godot.Engine.PhysicsJitterFix"/> — the knob that decides
    /// whether Godot reports the frame delta it MEASURED or a rewritten one.
    ///
    /// <para><b>Why this is a cvar at all</b> (2026-07-26 wobble root cause, planning/
    /// wobble-independent-audit-2026-07-26.md §3f). <c>MainTimerSync::advance_checked</c> runs three clamps over
    /// the wall-clock delta before it reaches <c>_Process</c>. Clamp 1 forces it into
    /// <c>[min_avg_physics_steps, max_avg_physics_steps] × physics_step</c>; clamp 2 then bounds it to
    /// <c>±physics_jitter_fix × physics_step</c> around the measured value and carries the difference in a
    /// <c>time_deficit</c> ledger repaid on later frames. None of this is gated by <c>run/delta_smooth</c> (that
    /// setting only disables <c>DeltaSmoother</c>, which additionally requires VSYNC_ENABLED and so was never
    /// running at our <c>vid_vsync 0</c> default).</para>
    ///
    /// <para>With this project's <c>physics_ticks_per_second = 10</c> the clamp band at ~144 fps is
    /// <c>[0, 100/12 ms] = [0, 8.333 ms]</c>: the ceiling sits ~1.4 ms above the median frame time and the floor
    /// collapses to zero, so it is a one-sided RECTIFIER — long frames are truncated, short frames are never
    /// extended. Measured in production traces: 23–40% of all frames report a time on the <c>100/N</c> ms grid,
    /// the deficit ledger rides its ±50 ms rails, and over 300 ms windows the time the game embodies differs from
    /// the time that elapsed by −20%/+28% (p1/p99), 23–38% of run time inside a >5% episode, worst episodes
    /// 460–550 ms. Motion integrates the reported delta while the display runs on wall time, so that IS a
    /// displayed-speed error — the felt wobble, generated below every port-side dt filter (which is why
    /// <c>cl_smoothdt</c>/<c>cl_smoothdt_driftcap</c> could not touch it: their input is already rewritten).</para>
    ///
    /// <para>0 (our default) makes clamp 2 degenerate to <c>clamp(measured, measured)</c>, which restores the exact
    /// measured delta and erases clamp 1's effect — DP-faithful <c>cl.realframetime</c> behaviour, and free for us
    /// because Godot's own physics has zero consumers here (see the project.godot rationale). 0.5 = Godot's stock
    /// value = the legacy behaviour, for A/B. Live-settable, so the A/B is a console toggle mid-match rather than
    /// a re-export: <c>cl_engine_jitterfix 0.5</c> / <c>cl_engine_jitterfix 0</c>. Score both legs with
    /// <c>tools/wobble-detect.py</c>.</para>
    /// </summary>
    public static void ApplyEngineTiming(CvarService c)
    {
        double jf = c.GetFloat("cl_engine_jitterfix");
        if (jf < 0d) jf = 0d;
        Godot.Engine.PhysicsJitterFix = jf;
        XonoticGodot.Common.Diagnostics.Log.Info(
            $"[video] cl_engine_jitterfix {jf:0.###} -> Engine.PhysicsJitterFix {Godot.Engine.PhysicsJitterFix:0.###} "
            + $"(physics {Godot.Engine.PhysicsTicksPerSecond} Hz, max steps/frame "
            + $"{Godot.Engine.MaxPhysicsStepsPerFrame}; 0 = report the measured delta)");
    }

    /// <summary>
    /// DP parity: <c>vid_vsync</c> and <c>cl_maxfps</c> take effect IMMEDIATELY when set (console `vid_vsync 1`,
    /// a cfg exec, a menu widget) — no <c>vid_restart</c> required. Resolution/fullscreen/borderless still go
    /// through vid_restart (they resize/recreate the window). Wire ONCE after boot-config load (Shell), so the
    /// bulk cfg exec doesn't re-apply per line.
    /// </summary>
    public static void InstallLiveVideoCvars(CvarService c)
    {
        c.Changed += name =>
        {
            if (string.Equals(name, "vid_vsync", StringComparison.Ordinal)) ApplyVsync(c);
            else if (string.Equals(name, "cl_maxfps", StringComparison.Ordinal)) ApplyMaxFps(c);
            // The wobble A/B has to be togglable mid-match to be feel-testable at all (see ApplyEngineTiming).
            else if (string.Equals(name, "cl_engine_jitterfix", StringComparison.Ordinal)) ApplyEngineTiming(c);
        };
    }

    /// <summary>
    /// Resolution + fullscreen + borderless + vsync from the <c>vid_*</c> cvars onto the window (QC vid_restart).
    /// </summary>
    public static void ApplyVideo()
    {
        CvarService c = MenuState.Cvars;
        RegisterEngineVideoDefaults(c);

        // vid_fullscreen is extended to a mode index (like vid_vsync): 0 windowed / 1 fullscreen (Godot's
        // composited borderless — the desktop compositor stays in the present path) / 2 EXCLUSIVE fullscreen.
        // (§12.7) Exclusive takes the compositor OUT of the present path on Windows, which removes the
        // biggest OS-stall class the forensics tag [external?] (rest-dominated ~100 ms frames with quiet
        // game-side numbers — DWM hiccups). Trade-offs are the classic ones: slower alt-tab, overlays may
        // flicker. 0/1 keep their stock DP meaning, so existing configs are unaffected.
        int fsMode = (int)c.GetFloat("vid_fullscreen");
        bool fullscreen = fsMode != 0;
        bool borderless = c.GetFloat("vid_borderless") != 0f;
        int w = (int)c.GetFloat("vid_width");
        int h = (int)c.GetFloat("vid_height");

        DisplayServer.WindowMode mode = fsMode switch
        {
            <= 0 => DisplayServer.WindowMode.Windowed,
            1 => DisplayServer.WindowMode.Fullscreen,
            _ => DisplayServer.WindowMode.ExclusiveFullscreen,
        };
        DisplayServer.WindowSetMode(mode);

        if (!fullscreen && w > 0 && h > 0)
            DisplayServer.WindowSetSize(new Vector2I(w, h));

        // Borderless only matters in windowed mode.
        if (!fullscreen)
            DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, borderless);

        ApplyVsync(c);

        // Framerate cap (DP cl_maxfps): 0 = unlimited. Previously read by the menu but never enforced, so the
        // game ran uncapped — variable frame times beat against the fixed 72 Hz input/prediction tick and showed
        // as micro-stutter. Honouring the cap lets the player pin a steady framerate (a multiple of 72 is ideal).
        // Guidance (B1): with vsync off, a cap slightly UNDER the worst-case sustainable rate is smoother than
        // uncapped, and it bounds the per-frame Godot-interop alloc rate (godot#105750) that an uncapped 300–600
        // fps would multiply into a Gen0 GC treadmill. With vid_vsync 2 (mailbox) leave it at 0 (uncapped).
        // (hitch-fix 2026-06-14) On a fast GPU the DP default (256) lets the CPU outrun the swapchain under mailbox
        // vsync -> constant present-jitter hitches. Controlled interleaved A/B (RTX 3080, catharsis + 6 bots):
        // capping the applied rate 256 -> 144 cut total hitches ~5x (277/164 -> 40/55) and raised 1%-low 60 -> 84.
        // A cap only helps when it ENGAGES (sits below the achievable fps), and 256 rarely does on a 3080. The
        // display refresh is the ideal ceiling, but Godot under-reports it in borderless mode (reads 60 on this
        // box), so for the "auto" case (the DP-shipped 256) we apply max(144, detected-refresh). Every OTHER
        // explicit choice -- the menu's 128 / 512 / 1024 / 2048 or "Unlimited" (0) -- is the player's, honored as-is.
        ApplyMaxFps(c);

        // Engine frame-delta honesty (the 2026-07-26 wobble root cause) — applied here so it is live from the
        // first rendered frame, and re-applied on every vid_restart alongside vsync/maxfps.
        ApplyEngineTiming(c);

        // (§12.7) OS-stall resistance: lift the process to ABOVE_NORMAL so background work (AV scans, the
        // indexer, browsers) can't preempt the game's main/render threads mid-frame — the CPU-side half of
        // the [external?] rest-stall class (the compositor half is vid_fullscreen 2). Deliberately NOT High:
        // High can starve audio/driver threads and feels worse, not better. sys_priority_boost 0 opts out.
        // Idempotent per apply; silently ignored where the OS denies it.
        try
        {
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            int boost = (int)c.GetFloat("sys_priority_boost");
            var want = boost != 0
                ? System.Diagnostics.ProcessPriorityClass.AboveNormal
                : System.Diagnostics.ProcessPriorityClass.Normal;
            if (proc.PriorityClass != want)
                proc.PriorityClass = want;
            // Always log the EFFECTIVE priority + the cvar that drove it. The old "only log on change" hid the
            // state in the common cases (already-AboveNormal, or sys_priority_boost 0 → Normal-and-unchanged),
            // so a config that pinned the boost off read as silence — indistinguishable from "boost on". This
            // line now makes "is the boost on?" answerable straight from the boot log.
            XonoticGodot.Common.Diagnostics.Log.Info(
                $"[video] process priority {proc.PriorityClass} (sys_priority_boost {boost})");
        }
        catch (Exception ex)
        {
            // Sandboxed / job-object-restricted environments can deny the change — run at stock priority,
            // but say so (an invisible no-op here would read as "the boost is on" when it isn't).
            XonoticGodot.Common.Diagnostics.Log.Info($"[video] process priority boost unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// The stock Darkplaces video-cvar defaults (vid_shared.c). Like the audio cvars these are ENGINE cvars —
    /// Darkplaces registers them in C, NOT in the .cfg tree Xonotic ships — so the port never picked up their
    /// defaults and they never entered the cvar store. The siblings (<c>vid_fullscreen</c>, <c>vid_width</c>,
    /// <c>vid_height</c>, …) only show up because xonotic-client.cfg happens to assign them as bare lines;
    /// <c>vid_vsync</c> and <c>vid_borderless</c> are assigned NOWHERE, so they were invisible to the console
    /// (cvarlist/apropos/Tab-completion all enumerate <see cref="CvarService.Names"/>) and <see cref="ApplyVideo"/>
    /// only ever read them with <see cref="ICvarService.GetFloat"/> (which returns 0 for an absent cvar without
    /// creating it — so vsync silently defaulted to OFF). Register them at boot (idempotent — keeps any value a
    /// cfg or config.cfg already set) so they're visible/bindable and reset correctly. Defaults + the archive
    /// flag (CF_ARCHIVE → Save) mirror vid_shared.c; "0" also preserves the current effective behaviour (vsync
    /// off, which matters for input latency).
    /// </summary>
    private static void RegisterEngineVideoDefaults(CvarService c)
    {
        const CvarFlags save = CvarFlags.Save;
        // vid_vsync is extended to a mode index (see ApplyVideo): 0 off / 1 on / 2 mailbox / 3 adaptive. The port
        // default is 0 (OFF — Bryan's 2026-07-06 call: −0.5 ms/frame AND better lows than the previous mailbox
        // default, uncapped decomposition in the perf-campaign doc); it's already set as a locked default in
        // MenuState.Boot, so this idempotent Register keeps that value and just carries the archive flag. A
        // player can still pick 1 / 2 (mailbox: no tearing, the pre-07-06 default) / 3 from console or menu.
        c.Register("vid_vsync", "0", save);
        c.Register("vid_borderless", "0", save);
        // (§12.7) AboveNormal process priority by default — see ApplyVideo's priority block. 0 = stock priority.
        c.Register("sys_priority_boost", "1", save);
    }

    /// <summary>
    /// The <c>mastervolume</c> / <c>bgmvolume</c> / channel volumes (DP linear 0..1) onto the audio buses
    /// (QC snd_restart + the per-channel volume cvars). Creates per-channel buses (Weapon/Voice/Player/Ambient)
    /// on first call if they don't exist in the project layout.
    /// </summary>
    public static void ApplyAudio()
    {
        EnsureChannelBuses();

        CvarService c = MenuState.Cvars;
        RegisterEngineAudioDefaults(c);
        // All three go through ChannelVol's "unset → full" guard too: these are DP ENGINE cvars (registered in
        // C, not the .cfg tree), so without RegisterEngineAudioDefaults they'd read 0 → SetBusVolume would MUTE
        // the Master/Music/SFX buses (the "all volume defaults are 0" bug). The guard is belt-and-suspenders.
        SetBusVolume("Master", ChannelVol(c, "mastervolume"));
        SetBusVolume("Music", ChannelVol(c, "bgmvolume"));
        // The "effects" bus stands in for the weapon/voice/item channels; use the loudest typical channel.
        SetBusVolume("SFX", ChannelVol(c, "snd_channel0volume"));

        // Per-channel buses (DP snd_channel<N>volume cvars → dedicated buses).
        // Default to 1.0 (full volume) when the cvar is unset (Xonotic's stock default.cfg sets these to 1).
        SetBusVolume("Weapon", ChannelVol(c, "snd_channel1volume"));
        SetBusVolume("Voice", ChannelVol(c, "snd_channel2volume"));
        SetBusVolume("Player", ChannelVol(c, "snd_channel7volume"));
        // Ambient inherits from the general effects channel (snd_channel0volume).
        SetBusVolume("Ambient", ChannelVol(c, "snd_channel0volume"));
    }

    /// <summary>
    /// The stock Darkplaces audio-cvar defaults (snd_main.c). These are ENGINE cvars — Darkplaces registers them
    /// in C, NOT in the .cfg tree Xonotic ships — so this port never picked up their defaults and every volume
    /// cvar read 0, leaving the menu sliders at 0% and (because <see cref="SetBusVolume"/> mutes at ≤0) the audio
    /// buses muted. Register them at boot (idempotent — <see cref="ICvarService.Register"/> keeps any value a cfg
    /// or the user's config.cfg already set, so overrides still win) so the defaults are authentic and the menu
    /// shows/resets them correctly. Values + flags (CF_ARCHIVE → Save) mirror snd_main.c verbatim.
    /// </summary>
    private static void RegisterEngineAudioDefaults(CvarService c)
    {
        const CvarFlags save = CvarFlags.Save;
        c.Register("mastervolume", "0.7", save);   // master volume
        c.Register("volume", "0.7", save);         // sound-effects volume
        c.Register("bgmvolume", "1", save);        // background-music volume
        c.Register("snd_staticvolume", "1", save); // ambient/static sounds
        // Per-entity-channel multipliers snd_channel0volume..snd_channel9volume (8/9 are QC music/ambient).
        for (int ch = 0; ch <= 9; ch++)
            c.Register($"snd_channel{ch}volume", "1", save);
        // Output cvars the audio dialog also displays (cosmetic here — Godot drives its own mixer).
        c.Register("snd_speed", "48000", save);
        c.Register("snd_channels", "2", save);
        c.Register("snd_swapstereo", "0", save);
        c.Register("snd_spatialization_control", "0", save);
        c.Register("snd_mutewhenidle", "1", save);

        // Distance-attenuation curve (ClientWorld reads these live to spatialize 3D sounds — see DpDistanceGain).
        // Defaults = Xonotic's shipped "new style" method 1 (binds-xonotic.cfg `snd_attenuation_method_1`:
        // menu_snd_attenuation_method 1 → radius 2400 / exponent 4 / decibel 0), NOT the Quake default
        // (1200/1/0, a too-flat linear ramp). Exponent 4 makes distant sounds fall off steeply so far-away
        // explosions go quiet. Tunable at runtime: e.g. `set snd_attenuation_exponent 2` (gentler) or the
        // decibel method `set snd_attenuation_exponent 0; set snd_attenuation_decibel 10` (radius 1200).
        c.Register("snd_soundradius", "2400");
        c.Register("snd_attenuation_exponent", "4", save);
        c.Register("snd_attenuation_decibel", "0", save);
        c.Register("menu_snd_attenuation_method", "1", save);
    }

    /// <summary>
    /// Ensure per-channel audio buses exist as children of Master. Safe to call multiple times (no-op if
    /// the buses already exist). These provide independent volume control for weapon/voice/player sounds.
    /// </summary>
    private static void EnsureChannelBuses()
    {
        // The Godot project ships no bus layout (.tres), so only the default "Master" bus exists — every other
        // bus this client routes to (MusicPlayer → "Music"; BusForChannel → "SFX"/"Weapon"/"Voice"/"Player"/
        // "Ambient") must be created here. Without "Music"/"SFX" their volume sliders silently no-op and those
        // players route to Master with a Godot "invalid bus" warning, so create the full set, all under Master.
        EnsureBus("Music", "Master");
        EnsureBus("SFX", "Master");
        EnsureBus("Weapon", "Master");
        EnsureBus("Voice", "Master");
        EnsureBus("Player", "Master");
        EnsureBus("Ambient", "Master");
    }

    /// <summary>Create a bus with the given name as a child of <paramref name="parentBus"/>, if it doesn't already exist.</summary>
    private static void EnsureBus(string busName, string parentBus)
    {
        if (AudioServer.GetBusIndex(busName) >= 0)
            return; // already exists
        int count = AudioServer.BusCount;
        AudioServer.AddBus(count);
        AudioServer.SetBusName(count, busName);
        AudioServer.SetBusSend(count, parentBus);
    }

    /// <summary>Read a per-channel volume cvar, defaulting to 1.0 (full) when unset (Xonotic stock default).</summary>
    private static float ChannelVol(CvarService c, string cvarName)
    {
        float v = c.GetFloat(cvarName);
        // If the cvar was never set (returns 0 from an empty store), treat as full volume (DP default = 1).
        // A user who explicitly set 0 gets mute via SetBusVolume's mute logic — but the cvar string would be
        // "0" not "", so we check for a truly absent cvar (GetString returns "").
        if (v <= 0f && string.IsNullOrEmpty(c.GetString(cvarName)))
            return 1f;
        return v;
    }

    /// <summary>Convert a linear 0..1 volume to dB and set it on the named bus, if that bus exists (0 = mute).</summary>
    private static void SetBusVolume(string busName, float linear)
    {
        int idx = AudioServer.GetBusIndex(busName);
        if (idx < 0)
            return;
        bool mute = linear <= 0.0001f;
        AudioServer.SetBusMute(idx, mute);
        if (!mute)
            AudioServer.SetBusVolumeDb(idx, Mathf.LinearToDb(Mathf.Clamp(linear, 0f, 1f)));
    }
}
