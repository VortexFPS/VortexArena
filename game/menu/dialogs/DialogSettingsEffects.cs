using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// Effects settings tab — a faithful C# port of <c>XonoticEffectsSettingsTab_fill</c>
/// (qcsrc/menu/xonotic/dialog_settings_effects.qc). Every control binds the same engine cvar the QC binds,
/// in the same order/grouping, with the same dependencies (setDependent → <see cref="Dependent.Bind"/>,
/// setDependentNOT → <see cref="Dependent.BindNot"/>) and the same "Apply immediately" command button
/// (<c>vid_restart</c>). The quality presets are the QC's five command buttons (<c>exec effects-*.cfg</c>).
///
/// Faithful-but-approximate spots (see the JSON notes too):
///   * QC <c>makeXonoticPicmipSlider</c> / the various <c>makeXonoticMixedSlider</c> become labeled
///     <see cref="Widgets.TextSlider"/>s on the same cvar (the picmip auto-clamp-to-VRAM is engine-side).
///   * QC <c>makeXonoticSliderCheckBox</c> (Motion blur) becomes a checkbox on <c>r_motionblur</c>
///     (on=0.4 default / off=0) plus the live slider; both write the same cvar.
///   * QC <c>makeMulti(e, "other")</c> checkboxes also poke a second cvar — we bind the primary cvar only.
///   * QC dependencies guarded by <c>cvar_type("vid_gl20") &amp; CVAR_TYPEFLAG_ENGINE</c> are skipped: VortexArena
///     has no such engine cvar, so applying them would permanently grey the widgets. Gameplay-cvar
///     dependencies and the compound <c>setDependentAND/OR/Weird</c> primary conditions are reproduced.
/// </summary>
public partial class DialogSettingsEffects : SettingsTab
{
    protected override void Fill(VBoxContainer box)
    {
        // The "Apply immediately" button (QC effectsApplyButton, command "vid_restart"); placed at the end.
        var applyButton = Widgets.CommandButton("Apply immediately", "vid_restart");

        // --- Quality preset: five command buttons exec'ing the effects-*.cfg presets -----------------------
        box.AddChild(Ui.Label("Quality preset:"));
        var presets = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        presets.AddThemeConstantOverride("separation", 8);
        presets.AddChild(Widgets.CommandButton("Low", "exec effects-low.cfg"));
        presets.AddChild(Widgets.CommandButton("Medium", "exec effects-med.cfg"));
        presets.AddChild(Widgets.CommandButton("Normal", "exec effects-normal.cfg"));
        presets.AddChild(Widgets.CommandButton("High", "exec effects-high.cfg"));
        presets.AddChild(Widgets.CommandButton("Ultra", "exec effects-ultra.cfg"));
        box.AddChild(presets);

        box.AddChild(Ui.Spacer());

        // --- Geometry / detail sliders (QC mixedsliders) ---------------------------------------------------
        var geometry = Widgets.TextSlider("r_subdivisions_tolerance", "Change the smoothness of the curves on the map")
            .Add("Lowest", 16).Add("Low", 8).Add("Normal", 4).Add("Good", 3).Add("Best", 2).Add("Insane", 1);
        box.AddChild(Ui.Row("Geometry detail:", geometry));

        var playerDetail = Widgets.TextSlider("cl_playerdetailreduction")
            .Add("Low", 4).Add("Medium", 3).Add("Normal", 2).Add("Good", 1).Add("Best", 0);
        box.AddChild(Ui.Row("Player detail:", playerDetail));

        // Texture resolution — QC picmip slider on gl_picmip (approx; engine VRAM auto-clamp not modeled).
        var texRes = Widgets.TextSlider("gl_picmip",
            "Change the sharpness of the textures. Lowering it will effectively reduce texture memory usage, but make the textures appear very blurry.")
            .Add("Lowest", 1337).Add("Very low", 2).Add("Low", 1).Add("Normal", 0).Add("Good", -1).Add("Best", -2);
        var texResRow = Ui.Row("Texture resolution:", texRes);
        box.AddChild(texResRow);
        Dependent.Bind(texResRow, "r_showsurfaces", 0, 0); // QC setDependent(e,"r_showsurfaces",0,0)

        // Texture compression — QC mixedslider gl_texturecompression.
        var texComp = Widgets.TextSlider("gl_texturecompression")
            .Add("Fast", 1).Add("Good", 2).Add("None", 0);
        var texCompRow = Ui.Row("Texture compression:", texComp);
        box.AddChild(texCompRow);
        Dependent.Bind(texCompRow, "r_showsurfaces", 0, 0); // QC setDependent(e,"r_showsurfaces",0,0) when can_dds

        box.AddChild(Ui.Spacer());

        // --- Sky / lightmaps / mapping ---------------------------------------------------------------------
        // QC makeXonoticCheckBoxEx(1, 0, "r_sky", ...): bit-0 of an int cvar → CheckBox on/off 1/0.
        box.AddChild(Widgets.CheckBox("r_sky", "Show sky", "Disable sky for performance and visibility"));

        box.AddChild(Widgets.CheckBox("mod_q3bsp_nolightmaps", "Use lightmaps",
            "Use high resolution lightmaps, which will look pretty but use up some extra video memory"));

        var deluxe = Widgets.CheckBox("r_glsl_deluxemapping", "Deluxe mapping", "Use per-pixel lighting effects");
        box.AddChild(deluxe);
        Dependent.Bind(deluxe, "mod_q3bsp_nolightmaps", 0, 0); // setDependent(e,"mod_q3bsp_nolightmaps",0,0)

        var gloss = Widgets.CheckBox("r_shadow_gloss", "Gloss",
            "Enable the use of glossmaps on textures supporting it");
        box.AddChild(gloss);
        Dependent.Bind(gloss, "mod_q3bsp_nolightmaps", 0, 0); // setDependent(e,"mod_q3bsp_nolightmaps",0,0)
        // (QC also setDependentAND on r_glsl_deluxemapping / vid_gl20 — primary dependency kept above.)

        box.AddChild(Widgets.CheckBox("r_glsl_offsetmapping", "Offset mapping",
            "Offset mapping effect that will make textures with bumpmaps appear like they \"pop out\" of the flat 2D surface"));

        var relief = Widgets.CheckBox("r_glsl_offsetmapping_reliefmapping", "Relief mapping",
            "Higher quality offset mapping, which also has a huge impact on performance");
        box.AddChild(relief);
        Dependent.Bind(relief, "r_glsl_offsetmapping", 1, 1); // setDependent(e,"r_glsl_offsetmapping",1,1)

        box.AddChild(Ui.Spacer());

        // --- Reflections ------------------------------------------------------------------------------------
        box.AddChild(Widgets.CheckBox("r_water", "Reflections",
            "Reflection and refraction quality, has a huge impact on performance on maps with reflecting surfaces"));

        var reflRes = Widgets.TextSlider("r_water_resolutionmultiplier", "Resolution of reflections/refractions")
            .Add("Blurred", 0.25f).Add("Good", 0.5f).Add("Sharp", 1);
        var reflResRow = Ui.Row("Resolution:", reflRes);
        box.AddChild(reflResRow);
        Dependent.Bind(reflResRow, "r_water", 1, 1); // setDependent(e,"r_water",1,1)

        box.AddChild(Ui.Spacer());

        // --- Decals -----------------------------------------------------------------------------------------
        box.AddChild(Widgets.CheckBox("cl_decals", "Decals", "Enable decals (bullet holes and blood)"));

        var decalsModels = Widgets.CheckBox("cl_decals_models", "Decals on models");
        box.AddChild(decalsModels);
        Dependent.Bind(decalsModels, "cl_decals", 1, 1); // setDependent(e,"cl_decals",1,1)

        var decalDist = Widgets.Slider("r_drawdecals_drawdistance", 200, 500, 20,
            "Decals further away than this will not be drawn", format: v => $"{CvarUi.Tidy(v)} qu");
        var decalDistRow = Ui.Row("Distance:", decalDist);
        box.AddChild(decalDistRow);
        Dependent.Bind(decalDistRow, "cl_decals", 1, 1); // setDependent + setDependentNOT(cl_decals_fadetime,0)

        var decalFade = Widgets.Slider("cl_decals_fadetime", 1, 20, 1,
            "Time in seconds before decals fade away", format: v => $"{CvarUi.Tidy(v)}s");
        var decalFadeRow = Ui.Row("Fade time:", decalFade);
        box.AddChild(decalFadeRow);
        Dependent.Bind(decalFadeRow, "cl_decals", 1, 1); // setDependent(e,"cl_decals",1,1)

        // Damage effects — QC mixedslider cl_damageeffect.
        var damageFx = Widgets.TextSlider("cl_damageeffect")
            .Add("Disabled", 0).Add("Skeletal", 1).Add("All", 2);
        box.AddChild(Ui.Row("Damage effects:", damageFx));

        box.AddChild(Ui.Spacer());

        // --- Lights & shadows (QC second column) -----------------------------------------------------------
        box.AddChild(Ui.Header("Lights & Shadows"));

        // QC makeMulti(e, "!gl_flashblend") also clears gl_flashblend — primary cvar bound here.
        box.AddChild(Widgets.CheckBox("r_shadow_realtime_dlight", "Realtime dynamic lights",
            "Temporary realtime light sources such as explosions, rockets and powerups"));

        var dlightShadows = Widgets.CheckBox("r_shadow_realtime_dlight_shadows", "Shadows",
            "Shadows cast by realtime dynamic lights");
        box.AddChild(dlightShadows);
        Dependent.Bind(dlightShadows, "r_shadow_realtime_dlight", 1, 1); // setDependent(...,1,1)

        box.AddChild(Widgets.CheckBox("r_shadow_realtime_world", "Realtime world lights",
            "Realtime light sources included in certain maps. May have a big impact on performance."));

        var worldShadows = Widgets.CheckBox("r_shadow_realtime_world_shadows", "Shadows",
            "Shadows cast by realtime world lights");
        box.AddChild(worldShadows);
        Dependent.Bind(worldShadows, "r_shadow_realtime_world", 1, 1); // setDependent(...,1,1)

        var normalMaps = Widgets.CheckBox("r_shadow_usenormalmap", "Use normal maps",
            "Directional shading of certain textures to simulate interaction of realtime light with a bumpy surface");
        box.AddChild(normalMaps);
        Dependent.Bind(normalMaps, "r_shadow_realtime_dlight", 1, 1); // setDependent + setDependentOR(world)

        // QC setDependentWeird(e, someShadowCvarIsEnabled): enabled when a dlight- or world-shadow combo is on.
        // No "weird" predicate dependency in the toolkit — left always enabled (note).
        box.AddChild(Widgets.CheckBox("r_shadow_shadowmapping", "Soft shadows"));

        var corona = Widgets.Slider("r_coronas", 0, 1.5f, 0.1f, "Flare effects around certain lights");
        box.AddChild(Ui.Row("Corona brightness:", corona));

        var coronaFade = Widgets.CheckBox("r_coronas_occlusionquery", "Fade coronas according to visibility",
            "Corona fading using occlusion queries");
        box.AddChild(coronaFade);
        Dependent.BindNot(coronaFade, "r_coronas", 0); // setDependentNOT(e,"r_coronas",0)

        // --- Vortex lighting (F1-B / F5 / N2 / N8) ---------------------------------------------------------
        // The port-only controls, kept together and BELOW the Base-parity ones so the tab still reads as
        // Xonotic. Every one of these has a live reader; nothing here is a stub.
        box.AddChild(Widgets.CheckBox("r_model_lightgrid", "Light models from the map",
            "Sample the map baked light grid per pixel, so players and pickups take the lighting of the room "
            + "they stand in (DarkPlaces does this by default). Off falls back to one global light."));

        var modelDlight = Widgets.CheckBox("r_model_dlight", "Dynamic lights on models",
            "Let explosions and muzzle flashes light players and items, not just the walls behind them.");
        box.AddChild(modelDlight);
        Dependent.Bind(modelDlight, "r_model_lightgrid", 1, 1);

        box.AddChild(Ui.Row("Ground shadows:", Widgets.TextSlider("r_fakeshadows",
            "Cheap projected shadows under players and pickups. Costs far less than real shadow maps and "
            + "stops models looking like they hover.")));

        var worldCasts = Widgets.CheckBox("r_shadow_world_casts", "World casts shadows",
            "Let map geometry cast into dynamic-light shadows. This is the expensive half of realtime "
            + "shadows - it makes every world cell a shadow caster.");
        box.AddChild(worldCasts);
        Dependent.Bind(worldCasts, "r_shadow_realtime_dlight_shadows", 1, 1);

        box.AddChild(Widgets.CheckBox("cl_rimlight", "Team rim light",
            "Outline teammates and powerup carriers with a coloured rim. This makes players easier to see, "
            + "so it is off by default and a server may prefer everyone to match."));

        box.AddChild(Ui.Spacer());

        // --- Postprocessing / motion blur ------------------------------------------------------------------
        box.AddChild(Widgets.CheckBox("r_bloom", "Bloom",
            "Enable bloom effect, which brightens the neighboring pixels of very bright pixels. Has a big impact on performance."));

        // QC makeXonoticCheckBoxEx(0.5,0,"hud_postprocessing_maxbluralpha",...) + makeMulti(hud_powerup).
        box.AddChild(Widgets.CheckBox("hud_postprocessing_maxbluralpha", "Extra postprocessing effects",
            "Enables special postprocessing effects for when damaged or under water or using a powerup",
            on: "0.5", off: "0"));

        // QC makeXonoticSliderCheckBox over r_motionblur (off=0, saved/default 0.4) + the slider beside it.
        // Approximated as a checkbox on the same cvar (on=0.4/off=0) plus the live slider.
        box.AddChild(Widgets.CheckBox("r_motionblur", "Motion blur", on: "0.4", off: "0"));
        var motionBlur = Widgets.Slider("r_motionblur", 0.1f, 1f, 0.1f, "Motion blur strength - 0.4 recommended");
        box.AddChild(Ui.Row("Motion blur:", motionBlur));

        box.AddChild(Ui.Spacer());

        // --- Particles --------------------------------------------------------------------------------------
        box.AddChild(Widgets.CheckBox("cl_particles", "Particles"));

        // Dual particle system (planning/particles-dual-system.md §D.3): the renderer-mode dropdown
        // (Original = faithful CPU parity backend, Mixed = per-effect, Modern = GPU custom-shader backend)
        // and the SDF collision-field generation toggle. Both bind the engine cvars the router reads.
        var particleMode = Widgets.TextSlider("cl_particles_modern",
                "Particle renderer: Original = faithful Darkplaces look; Modern = GPU shader with soft particles & collision; Mixed = per-effect")
            .Add("Original", 0).Add("Mixed", 1).Add("Modern", 2);
        var particleModeRow = Ui.Row("Particles renderer:", particleMode);
        box.AddChild(particleModeRow);
        Dependent.Bind(particleModeRow, "cl_particles", 1, 1);

        var sdfGen = Widgets.CheckBox("cl_particles_sdf_generate", "Generate collision fields",
            "Generate signed-distance collision fields at map load so modern particles bounce off and stain world geometry");
        box.AddChild(sdfGen);
        Dependent.Bind(sdfGen, "cl_particles", 1, 1);

        // QC makeMulti(e, "cl_spawn_event_particles"): also sets that cvar — primary bound here.
        var spawnFx = Widgets.CheckBox("cl_spawn_point_particles", "Spawnpoint effects",
            "Particle effects at all spawn points and whenever a player spawns");
        box.AddChild(spawnFx);
        Dependent.Bind(spawnFx, "cl_particles", 1, 1); // setDependent(e,"cl_particles",1,1)

        var partQuality = Widgets.Slider("cl_particles_quality", 0, 3.0f, 0.25f,
            "Multiplier for amount of particles. Less means less particles, which in turn gives for better performance",
            format: v => $"{CvarUi.Tidy(v)}x");
        var partQualityRow = Ui.Row("Quality:", partQuality);
        box.AddChild(partQualityRow);
        Dependent.Bind(partQualityRow, "cl_particles", 1, 1); // setDependent(e,"cl_particles",1,1)

        var partDist = Widgets.Slider("r_drawparticles_drawdistance", 200, 3000, 200,
            "Particles further away than this will not be drawn", format: v => $"{CvarUi.Tidy(v)} qu");
        var partDistRow = Ui.Row("Distance:", partDist);
        box.AddChild(partDistRow);
        Dependent.Bind(partDistRow, "cl_particles", 1, 1); // setDependent(e,"cl_particles",1,1)

        box.AddChild(Ui.Spacer());
        box.AddChild(applyButton); // "Apply immediately" — vid_restart
    }
}
