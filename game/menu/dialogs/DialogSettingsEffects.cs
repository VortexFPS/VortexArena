using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The Effects settings tab — <c>XonoticEffectsSettingsTab_fill</c> (qcsrc/menu/xonotic/dialog_settings_effects.qc),
/// TD for TD on its 15.5 x 6.2 grid, with the QC's cvars and dependencies (the <c>vid_gl20</c> branches as today's
/// DarkPlaces takes them: absent). The quality presets are the QC's five <c>exec effects-*.cfg</c> buttons.
///
/// <para>Where this renderer has nothing behind a control, the control stays (it keeps the setting a Xonotic config
/// carries) but is greyed with the reason (<see cref="Dependent.Unsupported"/>) instead of its QC dependency: offset
/// and relief mapping, decals on models, damage effects and the extra post-processing.</para>
///
/// <para>"Apply immediately" reloads the map (<c>r_restart</c>) rather than the QC's <c>vid_restart</c>, which here
/// re-applies only the window: what is not live on this tab is baked in when the map is built, and the button is
/// live only while one of those settings has changed (<see cref="ClientSettings.MapBuildApplyCvars"/>).</para>
/// </summary>
public partial class DialogSettingsEffects : QcTab
{
    public DialogSettingsEffects() : base(rows: 15.5f, columns: 6.2f, intendedWidth: 0.9f) { }

    // setDependentWeird(e, someShadowCvarIsEnabled)
    private static bool SomeShadowCvarIsEnabled()
    {
        var c = MenuState.Cvars;
        return c.GetFloat("r_shadow_realtime_dlight") != 0 && c.GetFloat("r_shadow_realtime_dlight_shadows") != 0
            || c.GetFloat("r_shadow_realtime_world") != 0 && c.GetFloat("r_shadow_realtime_world_shadows") != 0;
    }

    protected override void Fill()
    {
        var apply = Qc.Command("Apply immediately", "r_restart");
        PendingApply.Bind(apply, ClientSettings.MapBuildApplyCvars, string.Empty);

        TR();
            TD(1, 1, Qc.Label("Quality preset:"));
            const float n = 5;
            TD(1, 5 / n, Qc.Command("Low", "exec effects-low.cfg"));
            TD(1, 5 / n, Qc.Command("Medium", "exec effects-med.cfg"));
            TD(1, 5 / n, Qc.Command("Normal", "exec effects-normal.cfg"));
            TD(1, 5 / n, Qc.Command("High", "exec effects-high.cfg"));
            TD(1, 5 / n, Qc.Command("Ultra", "exec effects-ultra.cfg"));

        GotoRC(1.25f, 0);
            TD(1, 1, Qc.Label("Geometry detail:"));
            TD(1, 2, Qc.Mixed("r_subdivisions_tolerance", "Change the smoothness of the curves on the map")
                .Add("Lowest", 16).Add("Low", 8).Add("Normal", 4).Add("Good", 3).Add("Best", 2).Add("Insane", 1).Done());
        TR();
            TD(1, 1, Qc.Label("Player detail:"));
            TD(1, 2, Qc.Mixed("cl_playerdetailreduction")
                .Add("Low", 4).Add("Medium", 3).Add("Normal", 2).Add("Good", 1).Add("Best", 0).Done());
        TR();
            TD(1, 1, Qc.Label("Texture resolution:")).Dep("r_showsurfaces", 0, 0);
            // makeXonoticPicmipSlider: a mixed slider on gl_picmip (its texture-memory autofix needs
            // sys_memsize_virtual, which this client does not report, so it never clamps here either).
            TD(1, 2, Qc.Mixed("gl_picmip",
                    "Change the sharpness of the textures. Lowering it will effectively reduce texture memory usage, but make the textures appear very blurry.")
                .Add("Lowest", 1337).Add("Very low", 2).Add("Low", 1).Add("Normal", 0).Add("Good", -1).Add("Best", -2).Done())
                .Dep("r_showsurfaces", 0, 0);
        TR();
            TD(1, 1, Qc.Label("Texture compression:")).Dep("r_showsurfaces", 0, 0);
            TD(1, 2, Qc.Mixed("gl_texturecompression").Add("Fast", 1).Add("Good", 2).Add("None", 0).Done())
                .Dep("r_showsurfaces", 0, 0);
        TR();
            TDempty(0.2f);
            TD(1, 2.8f, Qc.CheckEx(1, 0, "r_sky", "Show sky", "Disable sky for performance and visibility"));
        TR();
            TD(1, 1, Qc.Check(1, "mod_q3bsp_nolightmaps", "Use lightmaps",
                "Use high resolution lightmaps, which will look pretty but use up some extra video memory"));
            TD(1, 1, Qc.Check(0, "r_glsl_deluxemapping", "Deluxe mapping", "Use per-pixel lighting effects"))
                .Dep("mod_q3bsp_nolightmaps", 0, 0);
            TD(1, 1, Qc.Check(0, "r_shadow_gloss", "Gloss", "Enable the use of glossmaps on textures supporting it"))
                .Dep("mod_q3bsp_nolightmaps", 0, 0).DepAnd("r_glsl_deluxemapping", 1, 1);
        TR();
            var offset = TD(1, 1, Qc.Check(0, "r_glsl_offsetmapping", "Offset mapping",
                "Offset mapping effect that will make textures with bumpmaps appear like they \"pop out\" of the flat 2D surface"));
            Dependent.Unsupported(offset, "this renderer has no offset/parallax mapping path.");
            var relief = TD(1, 2, Qc.Check(0, "r_glsl_offsetmapping_reliefmapping", "Relief mapping",
                "Higher quality offset mapping, which also has a huge impact on performance"));
            Dependent.Unsupported(relief, "this renderer has no offset/parallax mapping path.");
        TR();
            TD(1, 1, Qc.Check(0, "r_water", "Reflections:",
                "Reflection and refraction quality, has a huge impact on performance on maps with reflecting surfaces"));
            TD(1, 2, Qc.Mixed("r_water_resolutionmultiplier", "Resolution of reflections/refractions")
                .Add("Blurred", 0.25f).Add("Good", 0.5f).Add("Sharp", 1).Done())
                .Dep("r_water", 1, 1);
        TR();
            TD(1, 1, Qc.Check(0, "cl_decals", "Decals", "Enable decals (bullet holes and blood)"));
            var decalsModels = TD(1, 2, Qc.Check(0, "cl_decals_models", "Decals on models"));
            Dependent.Unsupported(decalsModels, "decals are projected onto world geometry only.");
        TR();
            TDempty(0.2f);
            TD(1, 0.8f, Qc.Label("Distance:")).DepNot("cl_decals_fadetime", 0).DepAnd("cl_decals", 1, 1);
            TD(1, 2, Qc.Slider(200, 500, 20, "r_drawdecals_drawdistance", "Decals further away than this will not be drawn", "%s qu"))
                .DepNot("cl_decals_fadetime", 0).DepAnd("cl_decals", 1, 1);
        TR();
            TDempty(0.2f);
            TD(1, 0.8f, Qc.Label("Fade time:")).Dep("cl_decals", 1, 1);
            TD(1, 2, Qc.Slider(1, 20, 1, "cl_decals_fadetime", "Time in seconds before decals fade away", "s"))
                .Dep("cl_decals", 1, 1);
        TR();
            TDempty(0.2f);
            var damageLabel = TD(1, 0.8f, Qc.Label("Damage effects:"));
            var damage = TD(1, 2, Qc.Mixed("cl_damageeffect").Add("Disabled", 0).Add("Skeletal", 1).Add("All", 2).Done());
            Dependent.Unsupported(damageLabel, "damage effects attached to players are not implemented yet.");
            Dependent.Unsupported(damage, "damage effects attached to players are not implemented yet.");

        GotoRC(1.25f, 3.2f); SetFirstColumn(CurrentColumn);
            TD(1, 2, Qc.Check(0, "r_shadow_realtime_dlight", "Realtime dynamic lights",
                "Temporary realtime light sources such as explosions, rockets and powerups"))
                .Multi("r_shadow_realtime_dlight", "!gl_flashblend");
            TD(1, 1, Qc.Check(0, "r_shadow_realtime_dlight_shadows", "Shadows", "Shadows cast by realtime dynamic lights"))
                .Dep("r_shadow_realtime_dlight", 1, 1);
        TR();
            TD(1, 2, Qc.Check(0, "r_shadow_realtime_world", "Realtime world lights",
                "Realtime light sources included in certain maps. May have a big impact on performance."));
            TD(1, 1, Qc.Check(0, "r_shadow_realtime_world_shadows", "Shadows", "Shadows cast by realtime world lights"))
                .Dep("r_shadow_realtime_world", 1, 1);
        TR();
            TDempty(0.2f);
            TD(1, 1.8f, Qc.Check(0, "r_shadow_usenormalmap", "Use normal maps",
                "Directional shading of certain textures to simulate interaction of realtime light with a bumpy surface"))
                .Dep("r_shadow_realtime_dlight", 1, 1).DepOr("r_shadow_realtime_world", 1, 1);
            TD(1, 1, Qc.Check(0, "r_shadow_shadowmapping", "Soft shadows")).DepFunc(SomeShadowCvarIsEnabled);
        TR();
            TD(1, 1, Qc.Label("Corona brightness:"));
            TD(1, 2, Qc.Slider(0, 1.5f, 0.1f, "r_coronas", "Flare effects around certain lights"));
        TR();
            TDempty(0.2f);
            TD(1, 2.8f, Qc.Check(0, "r_coronas_occlusionquery", "Fade coronas according to visibility",
                "Corona fading using occlusion queries")).DepNot("r_coronas", 0);
        TR();
        TR();
            TD(1, 1, Qc.Check(0, "r_bloom", "Bloom",
                "Enable bloom effect, which brightens the neighboring pixels of very bright pixels. Has a big impact on performance."));
            var extraPost = TD(1, 2, Qc.CheckEx(0.5f, 0, "hud_postprocessing_maxbluralpha", "Extra postprocessing effects",
                "Enables special postprocessing effects for when damaged or under water or using a powerup"))
                .Multi("hud_postprocessing_maxbluralpha", "hud_powerup");
            Dependent.Unsupported(extraPost, "the damage/underwater blur post-process is not implemented yet.");
        TR();
            var blur = Qc.Slider(0.1f, 1, 0.1f, "r_motionblur", "Motion blur strength - 0.4 recommended");
            TD(1, 1, new QcSliderCheckBox(0, true, blur, "Motion blur:", savedDefault: 0.4f));
            TD(1, 2, blur);
        TR();
        TR();
            TD(1, 1, Qc.Check(0, "cl_particles", "Particles"));
            TD(1, 2, Qc.Check(0, "cl_spawn_point_particles", "Spawnpoint effects",
                "Particle effects at all spawn points and whenever a player spawns"))
                .Multi("cl_spawn_point_particles", "cl_spawn_event_particles").Dep("cl_particles", 1, 1);
        TR();
            TDempty(0.2f);
            TD(1, 0.8f, Qc.Label("Quality:")).Dep("cl_particles", 1, 1);
            TD(1, 2, Qc.Slider(0, 3.0f, 0.25f, "cl_particles_quality",
                "Multiplier for amount of particles. Less means less particles, which in turn gives for better performance", "%sx"))
                .Dep("cl_particles", 1, 1);
        TR();
            TDempty(0.2f);
            TD(1, 0.8f, Qc.Label("Distance:")).Dep("cl_particles", 1, 1);
            TD(1, 2, Qc.Slider(200, 3000, 200, "r_drawparticles_drawdistance",
                "Particles further away than this will not be drawn", "%s qu"))
                .Dep("cl_particles", 1, 1);

        GotoRC(Rows - 1, 0);
            TD(1, Columns, apply);
    }
}
