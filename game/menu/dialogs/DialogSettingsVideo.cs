using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The Video settings tab — <c>XonoticVideoSettingsTab_fill</c> (qcsrc/menu/xonotic/dialog_settings_video.qc), TD for
/// TD on its 15.5 x 6.2 grid. Every control binds the cvar the QC binds, with the same dependencies and the same
/// "Apply immediately" command (<c>vid_restart</c>), live only while something it applies has changed
/// (<see cref="PendingApply"/>).
///
/// <para>The QC branches on <c>cvar_type("vid_gl20") &amp; CVAR_TYPEFLAG_ENGINE</c>; current DarkPlaces has no
/// <c>vid_gl20</c>, so this is the branch Xonotic shows today: "Resolution scaling" (<c>r_viewscale</c>) where the GL2
/// build had antialiasing, no GLSL checkbox, and no colour sliders gated on it.</para>
///
/// <para>Native additions, placed where the QC grid has room so nothing of Xonotic's moves: the vsync row is a mode
/// slider (Off / On / Mailbox / Adaptive, <c>vid_vsync</c> 0-3) instead of the QC's on/off checkbox, and "Show ping"
/// and "Show position" sit in the left column's empty row 13.</para>
/// </summary>
public partial class DialogSettingsVideo : QcTab
{
    public DialogSettingsVideo() : base(rows: 15.5f, columns: 6.2f, intendedWidth: 0.9f) { }

    protected override void Fill()
    {
        var apply = Qc.Command("Apply immediately",
            "vid_width $_menu_vid_width; "
            + "vid_height $_menu_vid_height; "
            + "vid_pixelheight $_menu_vid_pixelheight; "
            + "vid_desktopfullscreen $_menu_vid_desktopfullscreen; "
            + "menu_cmd update_conwidths_before_vid_restart; "
            + "vid_restart; "
            + "menu_cmd sync");
        // Live until resolution / fullscreen / borderless / process priority drift from what is running: those
        // recreate or resize the window and genuinely need the apply. Vsync, the fps cap and the jitter fix are
        // re-applied the moment they change (ClientSettings.InstallLiveVideoCvars), so they are not in the set.
        PendingApply.Bind(apply, ClientSettings.VideoApplyCvars, string.Empty);

        TR();
            TD(1, 1, Qc.Check(0, "vid_fullscreen", "Full screen"));
            TD(1, 2, Qc.Check(0, "vid_borderless", "Borderless window")).Dep("vid_fullscreen", 0, 0);
        TR();
            TD(1, 1, Qc.Label("Resolution:"));
            TD(1, 2, ResolutionSlider());
        TR();
            TD(1, 1, Qc.Label("Font/UI size:"));
            TD(1, 2, Qc.Mixed("menu_vid_scale")
                .Add("Unreadable", -1).Add("Tiny", -0.75f).Add("Little", -0.5f).Add("Small", -0.25f).Add("Medium", 0)
                .Add("Large", 0.25f).Add("Huge", 0.5f).Add("Gigantic", 0.75f).Add("Colossal", 1).Done());
        TR();
            TD(1, 3, Qc.Check(0, "r_stereo_redcyan", "Anaglyph 3D (red-cyan)"));
        TR();
            TD(1, 3, Qc.CheckEx(2, 0, "r_viewfbo", "High-quality frame buffer"));

        TR();
        TR();
            TD(1, 1.2f, Qc.Label("Resolution scaling:"));
            TD(1, 1.8f, Qc.Slider(0.5f, 2, 0.25f, "r_viewscale",
                "Screen or window size multiplier, above 1x does antialiasing, below 1x may help slow GPUs",
                "%sx", QcLook.DefaultValueSpace * 10f / 9f));
        TR();
            TD(1, 1.2f, Qc.Label("Anisotropy:"));
            TD(1, 1.8f, Qc.Mixed("gl_texture_anisotropy", "Anisotropic filtering quality", "%sx")
                .Add("Disabled", 1).AddRange(2, 4, 2).AddRange(8, 16, 8).Done(10f / 9f));

        const string dfTooltip = "Eliminate overdraw by rendering a depth-only version of the scene before the normal rendering starts";
        var depthFirst = new QcRadioGroup();
        TR();
        TR();
            TD(1, 1, Qc.Label("Depth first:"));
            TD(1, 2f / 3f, Qc.Radio(depthFirst, "r_depthfirst", "0", "Disabled", dfTooltip));
            TD(1, 2f / 3f, Qc.Radio(depthFirst, "r_depthfirst", "1", "World", dfTooltip));
            TD(1, 2f / 3f, Qc.Radio(depthFirst, "r_depthfirst", "2", "All", dfTooltip));

        TR();
        TR();
            TD(1, 3, Qc.Check(0, "gl_finish", "Wait for GPU to finish each frame",
                "Make the CPU wait for the GPU to finish each frame, can help with some strange input or video lag on some machines"));
        TR();
        // (the vid_gl20 checkbox: not in today's DarkPlaces)
        TR();
            TD(1, 1.5f, Qc.Check(0, "showping", "Show ping",
                "Show your round-trip latency to the server, just above the FPS counter"));
            TD(1, 1.5f, Qc.Check(0, "showposition", "Show position",
                "Show your map coordinates (x y z), stacked above the FPS/ping counters"));

        GotoRC(0, 3.2f); SetFirstColumn(CurrentColumn);
            TD(1, 1, Qc.Label("Brightness:"));
            TD(1, 2, Qc.Slider(0.0f, 0.5f, 0.02f, "v_brightness", "Brightness of black"));
        TR();
            TD(1, 1, Qc.Label("Contrast:"));
            TD(1, 2, Qc.Slider(1.0f, 3.0f, 0.05f, "v_contrast", "Brightness of white"));
        TR();
            TD(1, 1, Qc.Label("Gamma:"));
            TD(1, 2, Qc.Slider(0.5f, 2.0f, 0.05f, "v_gamma",
                "Inverse gamma correction value, a brightness effect that does not affect white or black"));
        TR();
            TD(1, 1, Qc.Label("Contrast boost:"));
            TD(1, 2, Qc.Slider(1.0f, 5.0f, 0.1f, "v_contrastboost", "By how much to multiply the contrast in dark areas"));
        TR();
            TD(1, 1, Qc.Label("Saturation:"));
            TD(1, 2, Qc.Slider(0.5f, 2.0f, 0.05f, "r_glsl_saturation",
                "Saturation adjustment (0 = grayscale, 1 = normal, 2 = oversaturated), requires GLSL color control"));
        TR();
            TD(1, 1, Qc.Label("Ambient:"));
            TD(1, 2, Qc.Slider(0, 20.0f, 0.25f, "r_ambient",
                "Ambient lighting, if set too high it tends to make light on maps look dull and flat"));
        TR();
            TD(1, 1, Qc.Label("Intensity:"));
            TD(1, 2, Qc.Slider(0.5f, 2.0f, 0.05f, "r_hdr_scenebrightness", "Global rendering brightness"));

        TR();
        TR();
            TD(1, 3, Qc.Header("Framerate"));
        TR();
            TD(1, 1, Qc.Label("Maximum:"));
            TD(1, 2, Qc.Mixed("cl_maxfps", "", "%s fps")
                .AddRange(128, 256, 128).AddRange(512, 1024, 512).AddRange(2048, 2048, 2048).Add("Unlimited", 0).Done());
        TR();
            TD(1, 1, Qc.Label("Target:"));
            TD(1, 2, Qc.Mixed("cl_minfps", "", "%s fps")
                .Add("Disabled", 0).AddRange(40, 60, 20).AddRange(100, 150, 25).AddRange(200, 250, 50).AddRange(400, 400, 100)
                .Done());
        TR();
            TD(1, 1, Qc.Label("Idle limit:"));
            TD(1, 2, Qc.Mixed("cl_maxidlefps", "", "%s fps")
                .AddRange(16, 32, 16).AddRange(64, 128, 64).Add("Unlimited", 0).Done());
        TR();
            // Vsync mode (vid_vsync): Mailbox is the recommended pacing fix on high-refresh displays - it renders
            // uncapped and presents the latest complete frame each refresh, so there's no tearing AND no fps
            // beat-doubling. See ClientSettings.ApplyVideo.
            TD(1, 1, Qc.Label("Vertical Sync:"));
            TD(1, 2, Qc.Mixed("vid_vsync",
                    "Off: no sync (tears, lowest latency). On: caps fps to the refresh rate, adds latency. "
                    + "Mailbox (recommended): uncapped render, latest frame each refresh - no tearing, no fps beat. "
                    + "Adaptive: vsync when fast enough, tears instead of stuttering when slow.")
                .Add("Disabled", 0).Add("On", 1).Add("Mailbox", 2).Add("Adaptive", 3).Done());
        TR();
            TD(1, 3, Qc.Check(0, "showfps", "Show frames per second", "Show your rendered frames per second"));

        GotoRC(Rows - 1, 0);
            TD(1, Columns, apply);
    }

    // -------------------------------------------------------------------------------------------------
    //  makeXonoticResolutionSlider (xonotic/slider_resolution.qc)
    // -------------------------------------------------------------------------------------------------

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // DarkPlaces menu.c video_resolutions_hardcoded[]: what getresolution lists for full screen when the video
    // system reports no modes of its own (Godot reports none).
    private static readonly (int W, int H, float Ph)[] FullscreenModes =
    {
        (320, 240, 1), (400, 300, 1), (512, 384, 1), (640, 480, 1), (800, 600, 1), (1024, 768, 1), (1152, 864, 1),
        (1280, 960, 1), (1400, 1050, 1), (1600, 1200, 1), (1792, 1344, 1), (1856, 1392, 1), (1920, 1440, 1),
        (2048, 1536, 1), (320, 256, 0.9375f), (640, 512, 0.9375f), (1280, 1024, 0.9375f), (320, 200, 1.2f),
        (640, 400, 1.2f), (840, 525, 1.2f), (960, 600, 1.2f), (1680, 1050, 1.2f), (1920, 1200, 1.2f),
        (320, 256, 1), (640, 512, 1), (1280, 1024, 1), (640, 384, 1), (1280, 768, 1), (320, 200, 1), (640, 400, 1),
        (720, 450, 1), (840, 525, 1), (960, 600, 1), (1280, 800, 1), (1440, 900, 1), (1680, 1050, 1), (1920, 1200, 1),
        (2560, 1600, 1), (3840, 2400, 1), (840, 540, 1), (1680, 1080, 1), (640, 360, 1), (683, 384, 1), (960, 540, 1),
        (1280, 720, 1), (1360, 768, 1), (1366, 768, 1), (1600, 900, 1), (1920, 1080, 1), (2560, 1440, 1), (3840, 2160, 1),
        (360, 240, 1.125f), (720, 480, 1.125f), (360, 283, 0.9545f), (720, 566, 0.9545f), (256, 224, 1.1667f),
        (512, 448, 1.1667f),
    };

    private static string Id(float w, float h, float ph)
        => QcFormat.MinDecimals(w) + " " + QcFormat.MinDecimals(h) + " " + QcFormat.MinDecimals(ph);

    private static Vector2I Desktop() => DisplayServer.GetName() == "headless" ? Vector2I.Zero : DisplayServer.ScreenGetSize();

    private static QcTextSlider ResolutionSlider()
    {
        var c = MenuState.Cvars;
        // The staging cvars hold the mode that is running until the slider picks another: menu.qc's updateConwidths
        // sets them from the live mode when the menu starts (and xonotic-client.cfg's `set _menu_vid_width
        // "$vid_width"` before that). They are plain `set` cvars, never saved.
        foreach ((string staged, string live) in new[] { ("_menu_vid_width", "vid_width"), ("_menu_vid_height", "vid_height"),
                     ("_menu_vid_pixelheight", "vid_pixelheight"), ("_menu_vid_desktopfullscreen", "vid_desktopfullscreen") })
        {
            string value = c.GetString(CvarUi.EnsureEngine(live));
            c.Set(staged, string.IsNullOrEmpty(value) ? (staged == "_menu_vid_pixelheight" ? "1" : "0") : value);
            c.UnmarkArchived(staged);
        }

        var slider = new QcTextSlider(
            () => c.GetString("_menu_vid_width") + " " + c.GetString("_menu_vid_height") + " " + c.GetString("_menu_vid_pixelheight"),
            id =>
            {
                string[] p = id.Split(' ');
                c.Set("_menu_vid_width", p[0]);
                c.Set("_menu_vid_height", p[1]);
                c.Set("_menu_vid_pixelheight", p[2]);
                Vector2I d = Desktop();
                bool desktop = float.Parse(p[0], Inv) == d.X && float.Parse(p[1], Inv) == d.Y && System.MathF.Abs(float.Parse(p[2], Inv) - 1f) < 0.01f;
                c.Set("_menu_vid_desktopfullscreen", desktop ? "1" : "0");
            }) { TooltipText = Localization.Tr("Screen resolution") };

        // loadResolutions: the modes sorted by width, height and pixel height, each once.
        var modes = new List<(float W, float H, float Ph)>();
        float maxW = 0, maxH = 0;
        void AddResolution(float w, float h, float ph)
        {
            if (maxW > 0 && w > maxW || maxH > 0 && h > maxH) return;
            int i = 0;
            for (; i < modes.Count; ++i)
            {
                (float mw, float mh, float mph) = modes[i];
                if (w > mw) continue;
                if (w < mw) break;
                if (h > mh) continue;
                if (h < mh) break;
                if (ph > mph + 0.01f) continue;
                if (ph < mph - 0.01f) break;
                return;
            }
            modes.Insert(i, (w, h, ph));
        }

        Vector2I desk = Desktop();
        if (c.GetFloat("vid_fullscreen") != 0)
        {
            foreach ((int w, int h, float ph) in FullscreenModes)
                if (w >= 640 && h >= 480 && (w <= 2 * h || c.GetFloat("menu_vid_allowdualscreenresolution") > 0))
                    AddResolution(w, h, ph);
            if (desk != Vector2I.Zero) AddResolution(desk.X, desk.Y, 1);
        }
        if (modes.Count == 0)
        {
            if (desk != Vector2I.Zero)
            {
                maxW = desk.X;
                maxH = desk.Y;
                AddResolution(desk.X, desk.Y, 1);
            }
            foreach ((int w, int h) in new[] { (640, 480), (800, 480), (800, 600), (1024, 600), (1024, 768), (1280, 720),
                         (1280, 960), (1280, 1024), (1920, 1080) })
                AddResolution(w, h, 1);
        }

        foreach ((float w, float h, float ph) in modes)
        {
            string text = $"{(int)w}x{(int)h}";
            if (ph != 1f)
            {
                // The aspect as the nearest small fraction, as the QC labels a non-square-pixel mode.
                float aspect = w / (h * ph), bestDenom = Mathf.Round(aspect), bestNum = 1;
                for (float denom = 2; denom < 10; ++denom)
                {
                    float num = Mathf.Round(aspect * denom);
                    if (System.MathF.Abs(num / denom - aspect) < System.MathF.Abs(bestNum / bestDenom - aspect))
                    {
                        bestNum = num;
                        bestDenom = denom;
                    }
                }
                text += $" ({(int)bestNum}:{(int)bestDenom})";
            }
            slider.Add(text, Id(w, h, ph));
        }
        return QcLook.Slider(slider);
    }
}
