using System.Globalization;
using Godot;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// Video settings tab — a faithful C# port of <c>XonoticVideoSettingsTab_fill</c>
/// (qcsrc/menu/xonotic/dialog_settings_video.qc). Every control binds the same engine cvar the QC binds, with
/// the same dependencies and the same "Apply immediately" command button (<c>vid_restart</c>).
///
/// Faithfulness notes / approximations:
///   * The QC tab is built behind <c>cvar_type("vid_gl20") &amp; CVAR_TYPEFLAG_ENGINE</c> branches; we reproduce
///     the GL20-present branch (the modern path): antialiasing via the <c>vid_samples</c> mixedslider, the
///     <c>vid_gl20</c> shaders checkbox, and the GLSL color-control sliders that QC gates on <c>vid_gl20</c>.
///   * The QC resolution control is a <c>slider_resolution</c> (data-driven from <c>getresolution()</c>) that
///     stages the menu cvars <c>_menu_vid_width</c> / <c>_menu_vid_height</c> / <c>_menu_vid_pixelheight</c>,
///     which the apply button then copies into <c>vid_width</c> / <c>vid_height</c> / … before <c>vid_restart</c>.
///     We approximate with two <see cref="Widgets.TextSlider"/>s over a representative set of common
///     resolutions — one bound to <c>_menu_vid_width</c>, one to <c>_menu_vid_height</c> — so the SAME staging
///     cvars feed the SAME apply command. (APPROXIMATE: hard-coded list instead of enumerated display modes.)
///   * The apply button command string is reproduced verbatim from QC (it stages <c>_menu_vid_*</c> into the
///     real <c>vid_*</c> cvars and runs <c>vid_restart</c>). <c>menu_cmd update_conwidths_before_vid_restart</c>
///     and <c>menu_cmd sync</c> have no client backend yet (logged as inert by <see cref="MenuCommand"/>).
/// </summary>
public partial class DialogSettingsVideo : SettingsTab
{
    // Common 16:9 / 16:10 resolutions for the approximated resolution dropdowns (width list, height list).
    private static readonly (string Label, string Width, string Height)[] Resolutions =
    {
        ("1280x720",  "1280", "720"),
        ("1366x768",  "1366", "768"),
        ("1600x900",  "1600", "900"),
        ("1920x1080", "1920", "1080"),
        ("2560x1440", "2560", "1440"),
        ("3840x2160", "3840", "2160"),
    };

    protected override void Fill(VBoxContainer box)
    {
        // QC: videoApplyButton — makeXonoticCommandButton("Apply immediately", ... vid_restart ...).
        // Verbatim command string from the QC (stages the menu cvars into the real vid_* cvars, then restarts).
        var applyButton = Widgets.CommandButton("Apply immediately",
            "vid_width $_menu_vid_width; "
            + "vid_height $_menu_vid_height; "
            + "vid_pixelheight $_menu_vid_pixelheight; "
            + "vid_desktopfullscreen $_menu_vid_desktopfullscreen; "
            + "menu_cmd update_conwidths_before_vid_restart; "
            + "vid_restart; "
            + "menu_cmd sync");

        // -- Full screen / borderless ----------------------------------------------------------------------
        box.AddChild(Widgets.CheckBox("vid_fullscreen", "Full screen"));
        var borderless = Widgets.CheckBox("vid_borderless", "Borderless window");
        box.AddChild(borderless);
        Dependent.Bind(borderless, "vid_fullscreen", 0, 0); // QC setDependent(e,"vid_fullscreen",0,0)

        // -- Resolution (APPROXIMATE: two dropdowns staging _menu_vid_width / _menu_vid_height) -------------
        var resWidth = Widgets.TextSlider("_menu_vid_width", "Screen resolution (width)");
        var resHeight = Widgets.TextSlider("_menu_vid_height", "Screen resolution (height)");
        foreach (var (label, w, h) in Resolutions)
        {
            resWidth.Add(label, w);
            resHeight.Add(label, h);
        }
        box.AddChild(Ui.Row("Resolution:", resWidth));
        box.AddChild(Ui.Row("Resolution (height):", resHeight));

        // -- Font/UI size (QC mixedslider menu_vid_scale) --------------------------------------------------
        var uiScale = Widgets.TextSlider("menu_vid_scale")
            .Add("Unreadable", -1f).Add("Tiny", -0.75f).Add("Little", -0.5f).Add("Small", -0.25f)
            .Add("Medium", 0f).Add("Large", 0.25f).Add("Huge", 0.5f).Add("Gigantic", 0.75f)
            .Add("Colossal", 1f);
        box.AddChild(Ui.Row("Font/UI size:", uiScale));

        // -- Anaglyph 3D -----------------------------------------------------------------------------------
        box.AddChild(Widgets.CheckBox("r_stereo_redcyan", "Anaglyph 3D (red-cyan)"));

        // -- High-quality frame buffer (QC makeXonoticCheckBoxEx(2, 0, "r_viewfbo") — checked stores 2
        //    = RGBA16F fbo, unchecked 0; the 1 = RGBA8 middle mode has no menu row) -------------------------
        var viewfbo = Widgets.ValueCheckBox("r_viewfbo", 2f, 0f, "High-quality frame buffer");
        box.AddChild(viewfbo);
        Dependent.Bind(viewfbo, "vid_samples", 0, 1); // QC setDependent(e,"vid_samples",0,1) (GL20 branch)

        box.AddChild(Ui.Spacer());

        // -- Antialiasing (GL20 branch: QC mixedslider_T vid_samples, "%sx", depends on r_viewfbo==0) ------
        var aa = Widgets.TextSlider("vid_samples",
                "Enable antialiasing, which smooths the edges of 3D geometry. Note that it might decrease performance by quite a lot")
            .Add("Disabled", 1f).Add("2x", 2f).Add("4x", 4f);
        var aaRow = Ui.Row("Antialiasing:", aa);
        box.AddChild(aaRow);
        Dependent.Bind(aaRow, "r_viewfbo", 0, 0); // QC setDependent on both label and slider

        // -- Anisotropy (QC mixedslider_T gl_texture_anisotropy, "%sx") ------------------------------------
        var aniso = Widgets.TextSlider("gl_texture_anisotropy", "Anisotropic filtering quality")
            .Add("Disabled", 1f).Add("2x", 2f).Add("4x", 4f).Add("8x", 8f).Add("16x", 16f);
        box.AddChild(Ui.Row("Anisotropy:", aniso));

        box.AddChild(Ui.Spacer());

        // -- Depth first (QC radiobutton r_depthfirst 0/1/2) -----------------------------------------------
        const string dfTooltip = "Eliminate overdraw by rendering a depth-only version of the scene before the normal rendering starts";
        var dfGroup = new ButtonGroup();
        var dfRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        dfRow.AddThemeConstantOverride("separation", 8);
        dfRow.AddChild(Widgets.RadioButton("r_depthfirst", "0", "Disabled", dfGroup, dfTooltip));
        dfRow.AddChild(Widgets.RadioButton("r_depthfirst", "1", "World", dfGroup, dfTooltip));
        dfRow.AddChild(Widgets.RadioButton("r_depthfirst", "2", "All", dfGroup, dfTooltip));
        box.AddChild(Ui.Row("Depth first:", dfRow));

        box.AddChild(Ui.Spacer());

        // -- gl_finish -------------------------------------------------------------------------------------
        box.AddChild(Widgets.CheckBox("gl_finish", "Wait for GPU to finish each frame",
            "Make the CPU wait for the GPU to finish each frame, can help with some strange input or video lag on some machines"));

        // -- OpenGL 2.0 shaders (GL20 branch) --------------------------------------------------------------
        box.AddChild(Widgets.CheckBox("vid_gl20", "Use OpenGL 2.0 shaders (GLSL)"));

        box.AddChild(Ui.Spacer());

        // -- Color control (QC gotoRC second column; the GLSL sliders are gated on vid_gl20) ----------------
        box.AddChild(Ui.Row("Brightness:",
            Widgets.Slider("v_brightness", 0.0f, 0.5f, 0.02f, "Brightness of black")));
        box.AddChild(Ui.Row("Contrast:",
            Widgets.Slider("v_contrast", 1.0f, 3.0f, 0.05f, "Brightness of white")));

        // The next four QC sliders are gated on vid_gl20==1 (GLSL color control).
        var gamma = Widgets.Slider("v_gamma", 0.5f, 2.0f, 0.05f,
            "Inverse gamma correction value, a brightness effect that does not affect white or black");
        var gammaRow = Ui.Row("Gamma:", gamma);
        box.AddChild(gammaRow);
        Dependent.Bind(gammaRow, "vid_gl20", 1, 1); // QC setDependent(e,"vid_gl20",1,1)

        var contrastBoost = Widgets.Slider("v_contrastboost", 1.0f, 5.0f, 0.1f,
            "By how much to multiply the contrast in dark areas");
        var contrastBoostRow = Ui.Row("Contrast boost:", contrastBoost);
        box.AddChild(contrastBoostRow);
        Dependent.Bind(contrastBoostRow, "vid_gl20", 1, 1);

        var saturation = Widgets.Slider("r_glsl_saturation", 0.5f, 2.0f, 0.05f,
            "Saturation adjustment (0 = grayscale, 1 = normal, 2 = oversaturated), requires GLSL color control");
        var saturationRow = Ui.Row("Saturation:", saturation);
        box.AddChild(saturationRow);
        Dependent.Bind(saturationRow, "vid_gl20", 1, 1);

        box.AddChild(Ui.Row("Ambient:",
            Widgets.Slider("r_ambient", 0f, 20.0f, 0.25f,
                "Ambient lighting, if set too high it tends to make light on maps look dull and flat")));
        box.AddChild(Ui.Row("Intensity:",
            Widgets.Slider("r_hdr_scenebrightness", 0.5f, 2.0f, 0.05f, "Global rendering brightness")));

        box.AddChild(Ui.Spacer());

        // -- Framerate (QC makeXonoticHeaderLabel("Framerate")) --------------------------------------------
        box.AddChild(Ui.Header("Framerate"));

        // QC mixedslider cl_maxfps, "%s fps": addRange 128..256/128, 512..1024/512, 2048, + "Unlimited" = 0.
        var maxFps = Widgets.TextSlider("cl_maxfps")
            .Add(Fps(128), 128f).Add(Fps(256), 256f).Add(Fps(512), 512f).Add(Fps(1024), 1024f)
            .Add(Fps(2048), 2048f).Add("Unlimited", 0f);
        box.AddChild(Ui.Row("Maximum:", maxFps));

        // QC mixedslider cl_minfps, "%s fps": "Disabled" = 0, then 40..60/20, 100..150/25, 200..250/50, 400.
        var targetFps = Widgets.TextSlider("cl_minfps")
            .Add("Disabled", 0f)
            .Add(Fps(40), 40f).Add(Fps(60), 60f)
            .Add(Fps(100), 100f).Add(Fps(125), 125f).Add(Fps(150), 150f)
            .Add(Fps(200), 200f).Add(Fps(250), 250f)
            .Add(Fps(400), 400f);
        box.AddChild(Ui.Row("Target:", targetFps));

        // QC mixedslider cl_maxidlefps, "%s fps": 16..32/16, 64..128/64, + "Unlimited" = 0.
        var idleFps = Widgets.TextSlider("cl_maxidlefps")
            .Add(Fps(16), 16f).Add(Fps(32), 32f).Add(Fps(64), 64f).Add(Fps(128), 128f)
            .Add("Unlimited", 0f);
        box.AddChild(Ui.Row("Idle limit:", idleFps));

        // Vsync mode (vid_vsync): Mailbox is the recommended pacing fix on high-refresh displays — it renders
        // uncapped and presents the latest complete frame each refresh, so there's no tearing AND no fps
        // beat-doubling (the "drops to 60 from 160" judder). See ClientSettings.ApplyVideo / PERFORMANCE_REPORT B1.
        box.AddChild(Ui.Row("Vertical Sync:", Widgets.TextSlider("vid_vsync",
                "Off: no sync (tears, lowest latency). On: caps fps to the refresh rate, adds latency. "
                + "Mailbox (recommended): uncapped render, latest frame each refresh — no tearing, no fps beat. "
                + "Adaptive: vsync when fast enough, tears instead of stuttering when slow.")
            .Add("Disabled", 0f)
            .Add("On", 1f)
            .Add("Mailbox", 2f)
            .Add("Adaptive", 3f)));
        box.AddChild(Widgets.CheckBox("showfps", "Show frames per second",
            "Show your rendered frames per second"));
        box.AddChild(Widgets.CheckBox("showping", "Show ping",
            "Show your round-trip latency to the server, just above the FPS counter"));
        box.AddChild(Widgets.CheckBox("showposition", "Show position",
            "Show your map coordinates (x y z), stacked above the FPS/ping counters"));

        box.AddChild(Ui.Spacer());
        box.AddChild(applyButton); // "Apply immediately" — vid_restart
    }

    /// <summary>Render an fps value with the QC "%s fps" suffix.</summary>
    private static string Fps(int v) => $"{v.ToString(CultureInfo.InvariantCulture)} fps";
}
