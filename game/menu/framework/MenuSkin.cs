using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using VortexArena.Formats.Vfs;
using VortexArena.Common.Localization;
using VortexArena.Game.Loaders;

namespace VortexArena.Game.Menu;

/// <summary>
/// The Xonotic menu skin, ported to a Godot <see cref="Theme"/>. Xonotic's menu has no widget look of its
/// own: the whole appearance comes from a <em>skin</em> — a folder of border-image <c>.tga</c> graphics plus a
/// <c>skinvalues.txt</c> palette under <c>gfx/menu/&lt;skin&gt;/</c> (default <c>menu_skin "luma"</c>, by sev) —
/// drawn by the engine's 3-/9-slice picture routines (<c>draw_ButtonPicture</c>/<c>draw_BorderPicture</c> in
/// menu/draw.qc). This class reproduces that skin as a single Godot Theme: it loads the same TGAs and the
/// Xolonium font from the mounted asset VFS, wraps the border-images in <see cref="StyleBoxTexture"/>es with the
/// 9-slice insets the QC uses (a 4:1 button = quarter-width end caps; a border image = quarter-size corners),
/// and stamps the <c>skinvalues.txt</c> colours onto the matching control classes.
///
/// <para>Because a Godot Theme cascades to every descendant <see cref="Control"/>, applying <see cref="Theme"/>
/// at the <see cref="MenuRoot"/> restyles the entire front-end — all ~100 ported dialogs and every cvar-bound
/// widget — at once, with no per-dialog edits. The class is resilient: if the VFS or an asset is missing it
/// falls back to flat styleboxes in the skin palette, so the menu still renders (and headless tests still
/// build) without the content repo mounted.</para>
/// </summary>
public static class MenuSkin
{
    // --- Image suffixes (skinvalues.txt): c(licked) d(isabled) f(ocused) n(ormal) s(eektrack), 0/1 = un/checked.

    private static Theme? _theme;
    private static bool _valuesLoaded;
    private static SkinValues? _skin;          // the full SKIN* table (schema defaults + skinvalues.txt overlay)
    private static string _skinFolder = "luma"; // the resolved skin folder (menu.qc draw_currentSkin tail)
    private static readonly Dictionary<string, Texture2D?> _texCache = new(StringComparer.OrdinalIgnoreCase);
    private static FontFile? _font, _fontBold;

    // ---------------------------------------------------------------------------------------------------------
    //  Font sizes (Godot px, tuned for ~1080p — the skin's FONTSIZE_* are in the menu's virtual canvas units).
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Body text / control labels.</summary>
    public const int BodySize = 18;
    /// <summary>Section header labels (bold, dimmed).</summary>
    public const int HeaderSize = 18;
    /// <summary>A dialog's title bar.</summary>
    public const int TitleSize = 30;
    /// <summary>The main-menu XONOTIC brand wordmark.</summary>
    public const int BrandSize = 72;

    // ---------------------------------------------------------------------------------------------------------
    //  Palette (resolved from skinvalues.txt with the luma values baked in as fallbacks)
    // ---------------------------------------------------------------------------------------------------------

    // These read the SKIN* table (the full skin-customizables.inc schema + the loaded skinvalues.txt overlay).
    // When the active skin file overrides a key, its value is used; otherwise the luma-tuned fallback below is
    // kept (so a headless run without the content repo still renders the signature blue-white look rather than
    // the bare Generic '1 1 1' schema default). See TableRgb/TableRgba/TableNum.

    /// <summary>Body text — cool blue-white (COLOR_TEXT @ ALPHA_TEXT).</summary>
    public static Color Text => TableRgba("COLOR_TEXT", "ALPHA_TEXT", 0.96f, 0.99f, 1f, 0.875f);
    /// <summary>Section headers — the same blue-white, dimmer (COLOR_HEADER @ ALPHA_HEADER).</summary>
    public static Color Header => TableRgba("COLOR_HEADER", "ALPHA_HEADER", 0.96f, 0.99f, 1f, 0.5f);
    /// <summary>A bright, fully-opaque blue-white for dialog titles / focused text.</summary>
    public static Color Bright => new(0.97f, 0.99f, 1f, 1f);
    /// <summary>The Xonotic accent orange — brand wordmark, active tab, list selection (COLOR_CREDITS_TITLE).</summary>
    public static Color Accent => TableRgb("COLOR_CREDITS_TITLE", 0.94f, 0.45f, 0.11f);
    /// <summary>The slightly warmer list-selection orange (COLOR_LISTBOX_SELECTED).</summary>
    public static Color Selection => TableRgb("COLOR_LISTBOX_SELECTED", 0.9f, 0.53f, 0.28f);
    /// <summary>List/box translucent backdrop (COLOR_LISTBOX_BACKGROUND @ ALPHA_LISTBOX_BACKGROUND).</summary>
    public static Color ListBackground => TableRgba("COLOR_LISTBOX_BACKGROUND", "ALPHA_LISTBOX_BACKGROUND", 0f, 0f, 0f, 0.25f);
    /// <summary>How far disabled widgets fade (ALPHA_DISABLED).</summary>
    public static float DisabledAlpha => TableNum("ALPHA_DISABLED", 0.25f);

    // ---------------------------------------------------------------------------------------------------------
    //  Backgrounds + the per-instance radio icons (CheckBox can't carry two icon sets in one theme).
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>The skinned mouse cursor (cursor.tga), down-scaled to its SKINSIZE_CURSOR draw size (32px).</summary>
    public static Texture2D? Cursor => LoadSkinTextureScaled("cursor", 32);

    /// <summary>Cursor hotspot in px = SKINOFFSET_CURSOR (0.25, 0.125) × SKINSIZE_CURSOR (32) = (8, 4).</summary>
    public static Vector2 CursorHotspot => new(8, 4);

    /// <summary>The main-menu space backdrop (background.tga).</summary>
    public static Texture2D? Background => LoadSkinTexture("background");
    /// <summary>The in-game (pause-menu) backdrop (background_ingame.tga).</summary>
    public static Texture2D? IngameBackground => LoadSkinTexture("background_ingame");
    /// <summary>Radio-button graphics, applied per-instance by <c>CvarRadioButton</c> (it is a Godot CheckBox).</summary>
    public static Texture2D? RadioChecked => LoadSkinTexture("radiobutton_n1");
    public static Texture2D? RadioUnchecked => LoadSkinTexture("radiobutton_n0");

    /// <summary>
    /// The Xonotic wordmark logo (XONOTIC + eagle reticle). It's baked into the top-left of the skin's second
    /// background layer (background_l2.tga, mostly transparent), so this crops that region out as a standalone
    /// logo texture for the nexposee. Aspect ≈ 2.93:1. Null without the asset.
    /// </summary>
    public static Texture2D? Logo => LoadLogo();

    private static Texture2D? LoadLogo()
    {
        const string key = "__logo";
        if (_texCache.TryGetValue(key, out Texture2D? cached))
            return cached;

        Texture2D? result = null;
        if (LoadSkinTexture("background_l2") is ImageTexture full)
        {
            Image? img = full.GetImage();
            if (img != null)
            {
                // The luma logo occupies this top-left region of the 2560×2048 layer (alpha bounding box).
                var box = new Rect2I(46, 0, 1367, 466);
                box = box.Intersection(new Rect2I(0, 0, img.GetWidth(), img.GetHeight()));
                if (box.Size.X > 0 && box.Size.Y > 0)
                {
                    try { result = ImageTexture.CreateFromImage(img.GetRegion(box)); }
                    catch (Exception ex) { GD.PrintErr($"[MenuSkin] logo crop failed: {ex.Message}"); }
                }
            }
        }
        _texCache[key] = result;
        return result;
    }

    /// <summary>The lazily-built, process-wide menu theme. Safe to read before any match (after MenuState.Boot).</summary>
    public static Theme Theme => _theme ??= Build();

    /// <summary>The Xolonium-bold font (titles/headers), or null if it could not be loaded.</summary>
    public static FontFile? BoldFont => Font(bold: true);

    // ---------------------------------------------------------------------------------------------------------
    //  Theme construction
    // ---------------------------------------------------------------------------------------------------------

    private static Theme Build()
    {
        var theme = new Theme();
        FontFile? font = Font(bold: false);
        if (font != null)
            theme.DefaultFont = font;
        theme.DefaultFontSize = BodySize;

        StyleText(theme);
        StyleButtons(theme);
        StyleChecks(theme);
        StyleSlider(theme);
        StyleLineEdit(theme);
        StyleDropdown(theme);
        StyleTabs(theme);
        StyleScrollbars(theme);
        StylePanels(theme);
        StyleLists(theme);

        return theme;
    }

    private static void StyleText(Theme t)
    {
        // Plain text labels are the cool blue-white body colour.
        t.SetColor("font_color", "Label", Text);
        t.SetFontSize("font_size", "Label", BodySize);
        // RichTextLabel (used by a few info panes) shares the body colour.
        t.SetColor("default_color", "RichTextLabel", Text);
    }

    private static void StyleButtons(Theme t)
    {
        // A Xonotic button is drawn by draw_ButtonPicture: height-square end caps + a stretched middle, so the
        // rounded ends stay undistorted at any button size (see ButtonPictureStyleBox). A fixed-margin 9-slice
        // would squash the caps vertically on non-64px buttons — the artefact this replaces.
        StyleBox Btn(string state, Color modulate)
            => PicStyle("button_" + state, modulate, contentH: 18, contentV: 7);

        t.SetStylebox("normal", "Button", Btn("n", Colors.White));
        t.SetStylebox("hover", "Button", Btn("f", Colors.White));
        t.SetStylebox("pressed", "Button", Btn("c", Colors.White));
        t.SetStylebox("disabled", "Button", Btn("d", new Color(1, 1, 1, 0.5f)));
        t.SetStylebox("focus", "Button", Btn("f", Colors.White));

        t.SetColor("font_color", "Button", Text);
        t.SetColor("font_hover_color", "Button", Bright);
        t.SetColor("font_pressed_color", "Button", Accent);
        t.SetColor("font_focus_color", "Button", Bright);
        t.SetColor("font_disabled_color", "Button", new Color(Text.R, Text.G, Text.B, 0.4f));
        t.SetFontSize("font_size", "Button", BodySize);
    }

    private static void StyleChecks(Theme t)
    {
        // CheckBox derives from Button, and Godot's theme lookup walks the class hierarchy — so without explicit
        // entries a checkbox would inherit the Button pill stylebox and render as a full-width bordered bar.
        // Xonotic checkboxes are just "[box] label", so blank out the backing styleboxes; the look is the icon.
        var empty = new StyleBoxEmpty();
        empty.ContentMarginTop = empty.ContentMarginBottom = 4;
        empty.ContentMarginLeft = empty.ContentMarginRight = 2;
        foreach (string s in new[] { "normal", "hover", "pressed", "disabled", "focus", "hover_pressed" })
            t.SetStylebox(s, "CheckBox", empty);

        // CheckBox draws the box graphic as an icon (checkbox_nX, X = checked). The 48px art is capped to a
        // text-row height so it sits beside a body label instead of dwarfing it.
        SetIcon(t, "CheckBox", "checked", "checkbox_n1");
        SetIcon(t, "CheckBox", "unchecked", "checkbox_n0");
        SetIcon(t, "CheckBox", "checked_disabled", "checkbox_d1");
        SetIcon(t, "CheckBox", "unchecked_disabled", "checkbox_d0");
        t.SetConstant("icon_max_width", "CheckBox", 24);
        t.SetConstant("h_separation", "CheckBox", 8);
        t.SetColor("font_color", "CheckBox", Text);
        t.SetColor("font_hover_color", "CheckBox", Bright);
        t.SetColor("font_pressed_color", "CheckBox", Bright);
        t.SetColor("font_disabled_color", "CheckBox", new Color(Text.R, Text.G, Text.B, 0.4f));
        t.SetFontSize("font_size", "CheckBox", BodySize);
    }

    private static void StyleSlider(Theme t)
    {
        // Track = the 4:1 seektrack groove (horizontal 3-slice). The grabber is the up-chevron knob; the source
        // art is 64² (far too big to draw 1:1) so it is down-scaled to a knob that sits on the ~22px track.
        const int knob = 22;
        // A clearly-visible inset groove. The skin's seektrack art is a very subtle trough that disappears
        // against the dark panel, so use a flat dark groove with a faint blue rim — reads as the Xonotic track
        // and gives the HSlider a real groove height for the chevron knob to ride on.
        var track = new StyleBoxFlat { BgColor = new Color(0f, 0f, 0f, 0.38f) };
        track.SetCornerRadiusAll(4);
        track.SetBorderWidthAll(1);
        track.BorderColor = new Color(0.40f, 0.55f, 0.72f, 0.55f);
        track.ContentMarginTop = track.ContentMarginBottom = 6; // ~12px groove thickness
        // The filled-to-the-left portion, tinted with the accent so the slider shows its value at a glance.
        var fill = new StyleBoxFlat { BgColor = new Color(Accent.R, Accent.G, Accent.B, 0.5f) };
        fill.SetCornerRadiusAll(4);
        t.SetStylebox("slider", "HSlider", track);
        t.SetStylebox("grabber_area", "HSlider", fill);
        t.SetStylebox("grabber_area_highlight", "HSlider", fill);
        SetIconTex(t, "HSlider", "grabber", LoadSkinTextureScaled("slider_n", knob));
        SetIconTex(t, "HSlider", "grabber_highlight", LoadSkinTextureScaled("slider_f", knob));
        SetIconTex(t, "HSlider", "grabber_disabled", LoadSkinTextureScaled("slider_d", knob));
        t.SetConstant("center_grabber", "HSlider", 1);
    }

    private static void StyleLineEdit(Theme t)
    {
        // Input boxes are a 9-slice frame (the 256×64 art carries a thin border all round).
        StyleBox Box(string state)
            => Slice9(LoadSkinTexture("inputbox_" + state), 64, 12, contentH: 12, contentV: 6)
               ?? Flat(new Color(0.03f, 0.05f, 0.08f, 0.85f), Colors.White);

        t.SetStylebox("normal", "LineEdit", Box("n"));
        t.SetStylebox("focus", "LineEdit", Box("f"));
        t.SetStylebox("read_only", "LineEdit", Box("n"));
        t.SetColor("font_color", "LineEdit", Text);
        t.SetColor("font_placeholder_color", "LineEdit", new Color(Text.R, Text.G, Text.B, 0.4f));
        t.SetColor("caret_color", "LineEdit", Accent);
        t.SetColor("selection_color", "LineEdit", new Color(Selection.R, Selection.G, Selection.B, 0.45f));
        t.SetFontSize("font_size", "LineEdit", BodySize);
    }

    private static void StyleDropdown(Theme t)
    {
        // The cvar TextSlider is a Godot OptionButton (a dropdown stand-in for the QC </> text slider): give it
        // the button skin so it reads as part of the set, and theme its popup so the open list matches too.
        StyleBox Btn(string state, Color mod) => PicStyle("button_" + state, mod, contentH: 14, contentV: 7);

        t.SetStylebox("normal", "OptionButton", Btn("n", Colors.White));
        t.SetStylebox("hover", "OptionButton", Btn("f", Colors.White));
        t.SetStylebox("pressed", "OptionButton", Btn("c", Colors.White));
        t.SetStylebox("disabled", "OptionButton", Btn("d", new Color(1, 1, 1, 0.5f)));
        t.SetStylebox("focus", "OptionButton", Btn("f", Colors.White));
        t.SetColor("font_color", "OptionButton", Text);
        t.SetColor("font_hover_color", "OptionButton", Bright);
        t.SetColor("font_pressed_color", "OptionButton", Bright);
        t.SetColor("font_focus_color", "OptionButton", Bright);
        t.SetFontSize("font_size", "OptionButton", BodySize);

        // The dropdown list (PopupMenu): a clean flat dark floating panel (NOT the chunky border.tga dialog frame,
        // which read as a thick rounded box around the open list). It's a list, so it follows the same flat
        // translucent-black listbox aesthetic — just more opaque, since it floats over the dialog and must stay
        // legible — with a faint blue rim to delimit it. Orange-tinted hover (the listbox-selection colour).
        var popup = new StyleBoxFlat { BgColor = new Color(0f, 0f, 0f, 0.88f) };
        popup.SetBorderWidthAll(1);
        popup.BorderColor = new Color(0.40f, 0.55f, 0.72f, 0.55f);
        popup.SetContentMarginAll(4);
        t.SetStylebox("panel", "PopupMenu", popup);
        var popupHover = new StyleBoxFlat { BgColor = new Color(Selection.R, Selection.G, Selection.B, 0.85f) };
        popupHover.SetCornerRadiusAll(2);
        t.SetStylebox("hover", "PopupMenu", popupHover);
        t.SetColor("font_color", "PopupMenu", Text);
        t.SetColor("font_hover_color", "PopupMenu", Bright);
        t.SetFontSize("font_size", "PopupMenu", BodySize);
    }

    private static void StyleTabs(Theme t)
    {
        // The settings/media tab strip: active tab in the accent orange, the rest dimmed; the body sits in a
        // bordered panel. Both TabContainer and the bare TabBar are themed (different screens use each). Tabs use
        // the same button-picture rendering (height-square caps) so short labels make compact pills.
        foreach (string type in new[] { "TabContainer", "TabBar" })
        {
            t.SetStylebox("tab_selected", type, PicStyle("button_n", Accent, 12, 9));
            t.SetStylebox("tab_hovered", type, PicStyle("button_f", Colors.White, 12, 9));
            t.SetStylebox("tab_unselected", type, PicStyle("button_n", new Color(1, 1, 1, 0.55f), 12, 9));
            t.SetColor("font_selected_color", type, Bright);
            t.SetColor("font_unselected_color", type, Header);
            t.SetColor("font_hovered_color", type, Bright);
            t.SetFontSize("font_size", type, BodySize);
        }
        // The tab BODY has no frame in Xonotic: QC tab content draws straight onto the dialog backing
        // (dialog.qc places the tab entity in the dialog grid; nothing draws a panel around it). The old
        // border.tga panel here put a chunky inner frame around every tab page ("weird border around the
        // controls") — keep just a little breathing room.
        var tabPanel = new StyleBoxEmpty();
        tabPanel.SetContentMarginAll(6);
        t.SetStylebox("panel", "TabContainer", tabPanel);
        t.SetStylebox("tabbar_background", "TabContainer", Flat(Colors.Transparent, Colors.White));
    }

    /// <summary>
    /// A tab pill stylebox for <see cref="XonoticTabs"/> (the QC tab-row buttons): <c>"active"</c> = the
    /// accent-orange button art (SKINCOLOR_TAB_ACTIVE), <c>"hover"</c> = the focused button art,
    /// anything else = the dimmed normal art.
    /// </summary>
    public static StyleBox TabPill(string state) => state switch
    {
        "active" => PicStyle("button_n", Accent, 12, 7),
        "hover" => PicStyle("button_f", Colors.White, 12, 7),
        _ => PicStyle("button_n", new Color(1, 1, 1, 0.55f), 12, 7),
    };

    private static void StyleScrollbars(Theme t)
    {
        // The Xonotic listbox scrollbar (item/listbox.qc): the track is the seektrack art (scrollbar_s) and the
        // grabber is scrollbar_n/_f/_c, both drawn with draw_VertButtonPicture (width-square caps, vertical
        // stretch) — reproduced by VertButtonPictureStyleBox so the scrollbar uses the real skin art at any
        // height. The ~16px bar width comes from each stylebox's left/right content margin.
        const float halfW = 8f; // → ~16px scrollbar (SKINWIDTH_SCROLLBAR)
        StyleBox VScroll(string state)
        {
            Texture2D? tex = LoadSkinTexture("scrollbar_" + state);
            if (tex == null)
                return Flat(state == "s" ? ListBackground : new Color(0.45f, 0.62f, 0.82f, 0.7f), Colors.White);
            var sb = new VertButtonPictureStyleBox { Texture = tex };
            sb.ContentMarginLeft = sb.ContentMarginRight = halfW;
            return sb;
        }
        t.SetStylebox("scroll", "VScrollBar", VScroll("s"));
        t.SetStylebox("grabber", "VScrollBar", VScroll("n"));
        t.SetStylebox("grabber_highlight", "VScrollBar", VScroll("f"));
        t.SetStylebox("grabber_pressed", "VScrollBar", VScroll("c"));

        // Horizontal scrollbars are rare in the menu; a flat translucent grabber is fine for them.
        StyleBox HFlat(Color c)
        {
            var s = new StyleBoxFlat { BgColor = c };
            s.SetCornerRadiusAll(3);
            s.ContentMarginTop = s.ContentMarginBottom = halfW;
            return s;
        }
        t.SetStylebox("scroll", "HScrollBar", HFlat(ListBackground));
        t.SetStylebox("grabber", "HScrollBar", HFlat(new Color(0.45f, 0.62f, 0.82f, 0.65f)));
        t.SetStylebox("grabber_highlight", "HScrollBar", HFlat(new Color(0.62f, 0.80f, 0.97f, 0.85f)));
        t.SetStylebox("grabber_pressed", "HScrollBar", HFlat(Selection));
    }

    private static void StyleLists(Theme t)
    {
        // ItemList / Tree: orange selection (SKINCOLOR_LISTBOX_SELECTED), translucent-black backdrop, blue-white
        // text — without this every list (campaign, server browser, media) falls back to Godot's grey default.
        StyleBox Selected()
        {
            var s = new StyleBoxFlat { BgColor = new Color(Selection.R, Selection.G, Selection.B, 0.9f) };
            s.SetCornerRadiusAll(2);
            return s;
        }
        // A Xonotic listbox has NO border frame: ListBox_draw (item/listbox.qc) simply fills its area with a flat
        // translucent black (COLOR_LISTBOX_BACKGROUND '0 0 0' @ ALPHA_LISTBOX_BACKGROUND 0.25) behind the items.
        // Wrapping the list in the chunky border.tga dialog frame (the old behaviour) was wrong — it drew a thick
        // rounded panel around every campaign/map/server list. Use the flat backdrop the skin specifies instead.
        var listPanel = new StyleBoxFlat { BgColor = ListBackground };
        listPanel.SetContentMarginAll(6);
        foreach (string type in new[] { "ItemList", "Tree" })
        {
            t.SetStylebox("panel", type, listPanel);
            t.SetStylebox("selected", type, Selected());
            t.SetStylebox("selected_focus", type, Selected());
            t.SetStylebox("cursor", type, new StyleBoxEmpty());
            t.SetStylebox("cursor_unfocused", type, new StyleBoxEmpty());
            t.SetColor("font_color", type, Text);
            t.SetColor("font_selected_color", type, Bright);
            t.SetColor("font_hovered_color", type, Bright);
            t.SetColor("font_outline_color", type, new Color(0, 0, 0, 0.6f));
            t.SetFontSize("font_size", type, BodySize);
        }
        // Tree-specific selection keys.
        t.SetStylebox("selected", "Tree", Selected());
        t.SetColor("font_selected_color", "Tree", Bright);
        t.SetColor("title_button_color", "Tree", Header);
    }

    private static void StylePanels(Theme t)
    {
        // PanelContainer/Panel become the bordered dialog frame (border.tga). Screens that wrap their content in
        // one get the authentic translucent-blue Xonotic dialog look for free.
        StyleBox panel = BorderPanel() ?? Flat(new Color(0.03f, 0.05f, 0.09f, 0.85f), Colors.White);
        t.SetStylebox("panel", "PanelContainer", panel);
        t.SetStylebox("panel", "Panel", panel);
    }

    /// <summary>The dialog-frame stylebox (border.tga, 9-slice with quarter-size corners), or null without assets.</summary>
    public static StyleBox? DialogPanelStyle() => BorderPanel();

    /// <summary>
    /// A dialog frame stylebox whose drawn border is exactly <paramref name="borderPx"/> thick (the Xonotic dialog
    /// border = the title-bar height, per draw_BorderPicture's theBorderSize). border.tga is square, so it's
    /// down-scaled to 4×borderPx and 9-sliced at the quarter — giving the right thin frame instead of the chunky
    /// 96px-native corners. Used by the nexposee panels; null without the asset.
    /// </summary>
    public static StyleBox? DialogFrame(int borderPx)
    {
        if (borderPx < 1)
            borderPx = 1;
        Texture2D? tex = LoadSkinTextureScaled("border", borderPx * 4);
        if (tex == null)
            return null;
        var sb = new StyleBoxTexture { Texture = tex };
        SetSliceMargins(sb, borderPx, borderPx, borderPx, borderPx);
        return sb;
    }

    /// <summary>The dialog close-button (X) graphic for the title bar.</summary>
    public static Texture2D? CloseButton => LoadSkinTexture("closebutton_n");

    /// <summary>
    /// Load an arbitrary game texture by its VFS base name (no extension), e.g. <c>"gfx/crosshair31"</c>,
    /// <c>"gfx/crosshairdot"</c>, a weapon icon, or <c>"gfx/menu/luma/colorpicker"</c>. The C# stand-in for the
    /// engine's <c>draw_PreloadPicture</c>/<c>draw_Picture</c> texture resolution the QC pickers use. Cached and
    /// resilient to a missing content repo (returns null, so a picker degrades to its fallback instead of
    /// crashing). Leading <c>/</c> is tolerated (the QC paths start with <c>"/gfx/..."</c>).
    /// </summary>
    public static Texture2D? Image(string baseNameNoExt)
    {
        if (string.IsNullOrEmpty(baseNameNoExt)) return null;
        string key = "img:" + baseNameNoExt;
        if (_texCache.TryGetValue(key, out Texture2D? cached))
            return cached;
        Texture2D? tex = LoadTexture(baseNameNoExt.TrimStart('/'));
        _texCache[key] = tex;
        return tex;
    }

    /// <summary>
    /// Load a skin graphic by its bare skin name (e.g. <c>"colorpicker"</c>, <c>"colorpicker_selected"</c>) from
    /// <c>gfx/menu/&lt;skin&gt;/</c> with the luma fallback — the public accessor for skin gfx the QC widgets
    /// reference via SKINGFX_* (here the HSL colorpicker image). Cached; null without the content repo.
    /// </summary>
    public static Texture2D? SkinImage(string baseName) => LoadSkinTexture(baseName);

    private static StyleBox? BorderPanel()
    {
        // The Xonotic dialog border is drawn at the title-bar thickness, NOT the texture's native quarter (96px),
        // which read as a chunky frame. Down-scale border.tga so its quarter corner is ~44px — a thin frame that
        // matches the nexposee panels and Base. (Used for pushed sub-dialogs + any menu PanelContainer.)
        const int corner = 44;
        Texture2D? tex = LoadSkinTextureScaled("border", corner * 4);
        if (tex == null)
            return null;
        var sb = new StyleBoxTexture { Texture = tex };
        SetSliceMargins(sb, corner, corner, corner, corner);
        sb.SetContentMarginAll(22);
        return sb;
    }

    // ---------------------------------------------------------------------------------------------------------
    //  StyleBox builders
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>A faithful Xonotic button picture (draw_ButtonPicture): height-square end caps, middle stretched.
    /// Use for buttons/dropdowns/tabs (the 4:1 button art) so the caps don't distort like a fixed-margin 9-slice.</summary>
    private static StyleBox PicStyle(string textureName, Color tint, float contentH, float contentV)
    {
        Texture2D? tex = LoadSkinTexture(textureName);
        if (tex == null)
            return Flat(new Color(0.06f, 0.09f, 0.13f, 0.82f), tint);
        var sb = new ButtonPictureStyleBox { Texture = tex, Tint = tint };
        sb.ContentMarginLeft = sb.ContentMarginRight = contentH;
        sb.ContentMarginTop = sb.ContentMarginBottom = contentV;
        return sb;
    }

    /// <summary>A horizontal 3-slice (4:1 button/track art): fixed end caps, stretched centre. <paramref name="cap"/>
    /// overrides the cap width (default = quarter width, the QC square end cap); a smaller cap keeps short tabs
    /// from being forced very wide by two full 64px caps.</summary>
    private static StyleBox? SliceH(Texture2D? tex, Color modulate, int contentH, int contentV, int cap = -1)
    {
        if (tex == null)
            return null;
        if (cap < 0)
            cap = System.Math.Max(1, tex.GetWidth() / 4); // quarter width = the square end cap
        var sb = new StyleBoxTexture { Texture = tex, ModulateColor = modulate };
        SetSliceMargins(sb, cap, 0, cap, 0);
        sb.AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Stretch;
        sb.AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Stretch;
        sb.ContentMarginLeft = sb.ContentMarginRight = contentH;
        sb.ContentMarginTop = sb.ContentMarginBottom = contentV;
        return sb;
    }

    /// <summary>A vertical 3-slice (1:4 scrollbar art): fixed quarter-height end caps, stretched centre.</summary>
    private static StyleBox? SliceV(Texture2D? tex, Color modulate)
    {
        if (tex == null)
            return null;
        int cap = System.Math.Max(1, tex.GetHeight() / 4);
        var sb = new StyleBoxTexture { Texture = tex, ModulateColor = modulate };
        SetSliceMargins(sb, 0, cap, 0, cap);
        return sb;
    }

    /// <summary>A full 9-slice with explicit corner insets (border/inputbox art).</summary>
    private static StyleBox? Slice9(Texture2D? tex, int hMargin, int vMargin, int contentH, int contentV)
    {
        if (tex == null)
            return null;
        var sb = new StyleBoxTexture { Texture = tex };
        SetSliceMargins(sb, hMargin, vMargin, hMargin, vMargin);
        sb.ContentMarginLeft = sb.ContentMarginRight = contentH;
        sb.ContentMarginTop = sb.ContentMarginBottom = contentV;
        return sb;
    }

    private static void SetSliceMargins(StyleBoxTexture sb, int left, int top, int right, int bottom)
    {
        sb.TextureMarginLeft = left;
        sb.TextureMarginTop = top;
        sb.TextureMarginRight = right;
        sb.TextureMarginBottom = bottom;
    }

    /// <summary>A flat fallback stylebox in the skin palette, used when an image asset is unavailable.</summary>
    private static StyleBoxFlat Flat(Color fill, Color border)
    {
        var sb = new StyleBoxFlat { BgColor = fill };
        sb.SetContentMarginAll(8);
        if (border.A > 0f && border != Colors.White)
        {
            sb.BorderColor = border;
            sb.SetBorderWidthAll(1);
        }
        return sb;
    }

    private static void SetIcon(Theme t, string type, string name, string asset)
        => SetIconTex(t, type, name, LoadSkinTexture(asset));

    private static void SetIconTex(Theme t, string type, string name, Texture2D? tex)
    {
        if (tex != null)
            t.SetIcon(name, type, tex);
    }

    // ---------------------------------------------------------------------------------------------------------
    //  Asset loading (VFS → Texture2D / FontFile), all resilient to a missing content repo
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The resolved skin folder name — a faithful port of the menu.qc fallback chain (menu.qc:174-191): try the
    /// <c>menu_skin</c> cvar; failing that its default (<c>cvar_defstring</c> → <see cref="CvarService.GetDefault"/>);
    /// failing that the hardcoded default skin name. (Base uses <c>wickedx</c> as the final hardcoded fallback, but
    /// only <c>luma</c> ships in the content repo, so luma is the de-facto floor here — see EnsureValues.) Cached
    /// in <see cref="_skinFolder"/> by <see cref="EnsureValues"/>.
    /// </summary>
    private static string SkinName
    {
        get
        {
            string s = MenuState.Cvars.GetString("menu_skin");
            if (!string.IsNullOrWhiteSpace(s))
                return s;
            string def = MenuState.Cvars.GetDefault("menu_skin");      // cvar_defstring("menu_skin")
            if (!string.IsNullOrWhiteSpace(def))
                return def;
            return "luma";                                            // Base: "wickedx"; only luma ships here
        }
    }

    /// <summary>
    /// Discard the cached theme, skin table, fonts, and textures so the next access rebuilds them from the
    /// CURRENT <c>menu_skin</c>/<c>prvm_language</c> — the C# successor to a <c>menu_restart</c> re-running
    /// <c>m_init_delayed</c> (re-applies the skin). Called by the menu host on a skin change so the live front-end
    /// restyles. (The Theme object is process-wide; callers must re-read <see cref="Theme"/> after a reload.)
    /// </summary>
    public static void Reload()
    {
        _theme = null;
        _skin = null;
        _valuesLoaded = false;
        _font = _fontBold = null;
        _texCache.Clear();
    }

    /// <summary>Load a skin image by base name (e.g. "button_n") from <c>gfx/menu/&lt;skin&gt;/</c>, cached.</summary>
    private static Texture2D? LoadSkinTexture(string baseName)
    {
        if (_texCache.TryGetValue(baseName, out Texture2D? cached))
            return cached;

        Texture2D? tex = LoadTexture($"gfx/menu/{SkinName}/{baseName}");
        // Fall back to luma if the active skin lacks the asset.
        if (tex == null && !string.Equals(SkinName, "luma", StringComparison.OrdinalIgnoreCase))
            tex = LoadTexture($"gfx/menu/luma/{baseName}");

        _texCache[baseName] = tex;
        return tex;
    }

    /// <summary>Load a skin image and down-scale it to <paramref name="size"/>² (for icons whose source art is
    /// far larger than it should draw — e.g. the 64px slider knob). Cached per (name,size).</summary>
    private static Texture2D? LoadSkinTextureScaled(string baseName, int size)
    {
        string key = baseName + "@" + size;
        if (_texCache.TryGetValue(key, out Texture2D? cached))
            return cached;

        Texture2D? result = LoadSkinTexture(baseName);
        if (result is ImageTexture src)
        {
            Godot.Image? img = src.GetImage();
            if (img != null)
            {
                var copy = (Godot.Image)img.Duplicate();
                copy.Resize(size, size, Godot.Image.Interpolation.Lanczos);
                result = ImageTexture.CreateFromImage(copy);
            }
        }
        _texCache[key] = result;
        return result;
    }

    /// <summary>Resolve a texture base name through the VFS and decode it (TGA via our decoder, else Godot).</summary>
    private static Texture2D? LoadTexture(string baseNameNoExt)
    {
        VirtualFileSystem? vfs = MenuState.Vfs;
        if (vfs == null)
            return null;
        try
        {
            string? vpath = vfs.ResolveImage(baseNameNoExt);
            if (vpath == null)
                return null;
            byte[] bytes = vfs.ReadBytes(vpath);
            string ext = System.IO.Path.GetExtension(vpath).TrimStart('.').ToLowerInvariant();
            Image? img = ext == "tga" ? TgaDecoder.Decode(bytes) : null;
            if (img == null)
            {
                img = new Image();
                Error err = ext switch
                {
                    "png" => img.LoadPngFromBuffer(bytes),
                    "jpg" or "jpeg" => img.LoadJpgFromBuffer(bytes),
                    _ => img.LoadTgaFromBuffer(bytes),
                };
                if (err != Error.Ok)
                    return null;
            }
            return ImageTexture.CreateFromImage(img);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[MenuSkin] failed to load '{baseNameNoExt}': {ex.Message}");
            return null;
        }
    }

    private static FontFile? Font(bool bold)
    {
        ref FontFile? slot = ref bold ? ref _fontBold : ref _font;
        if (slot != null)
            return slot;

        VirtualFileSystem? vfs = MenuState.Vfs;
        if (vfs == null)
            return null;

        foreach (string path in bold
                     ? new[] { "fonts/xolonium-bold.otf", "fonts/xolonium-regular.otf" }
                     : new[] { "fonts/xolonium-regular.otf", "fonts/xolonium-bold.otf" })
        {
            try
            {
                if (!vfs.Exists(path))
                    continue;
                var f = new FontFile { Data = vfs.ReadBytes(path) };
                slot = f;
                return slot;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[MenuSkin] failed to load font '{path}': {ex.Message}");
            }
        }
        return null;
    }

    // ---------------------------------------------------------------------------------------------------------
    //  The SKIN* table: the full skin-customizables.inc schema (typed defaults) + the skinvalues.txt overlay,
    //  loaded via the menu.qc fallback chain. Backed by the Godot-free VortexArena.Common.Localization.SkinValues so
    //  the schema + parse semantics (bare-key Skin_ApplySetting, value-substring, stof/stov) are unit-testable.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>The resolved skin table (schema defaults + the loaded skinvalues.txt). Lazily loaded.</summary>
    private static SkinValues Skin
    {
        get { EnsureValues(); return _skin!; }
    }

    private static void EnsureValues()
    {
        if (_valuesLoaded)
        {
            _skin ??= new SkinValues();
            return;
        }
        _valuesLoaded = true;

        var table = new SkinValues(); // seeded with the full Generic-default schema
        _skin = table;

        VirtualFileSystem? vfs = MenuState.Vfs;
        if (vfs == null)
        {
            _skinFolder = SkinName;
            return;
        }
        try
        {
            // menu.qc m_init_delayed fallback chain: menu_skin cvar → its defstring → the hardcoded default. The
            // SkinName getter already encodes the cvar→defstring→"luma" precedence; here we also fall back to
            // luma's file if the chosen skin lacks skinvalues.txt (only luma ships in the content repo).
            string folder = SkinName;
            string path = $"gfx/menu/{folder}/skinvalues.txt";
            if (!vfs.Exists(path))
            {
                folder = "luma";
                path = $"gfx/menu/{folder}/skinvalues.txt";
            }
            _skinFolder = folder;
            if (vfs.Exists(path))
                table.Load(vfs.ReadText(path)); // overlay the file on top of the schema (Base value-substring semantics)
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[MenuSkin] skinvalues parse failed: {ex.Message}");
        }
    }

    /// <summary>An RGB skin colour from the table; the (<paramref name="dr"/>,<paramref name="dg"/>,
    /// <paramref name="db"/>) luma-tuned fallback is used unless a loaded skin file overrides the key.</summary>
    private static Color TableRgb(string key, float dr, float dg, float db)
    {
        EnsureValues();
        if (_skin!.IsOverridden(key))
        {
            SkinVec v = _skin.Vector(key);
            return new Color(v.X, v.Y, v.Z, 1f);
        }
        return new Color(dr, dg, db, 1f);
    }

    /// <summary>An RGBA skin colour: RGB from <paramref name="colorKey"/>, alpha from <paramref name="alphaKey"/>,
    /// each falling back to the luma-tuned default unless a loaded skin file overrides it.</summary>
    private static Color TableRgba(string colorKey, string alphaKey, float dr, float dg, float db, float da)
    {
        Color c = TableRgb(colorKey, dr, dg, db);
        return new Color(c.R, c.G, c.B, TableNum(alphaKey, da));
    }

    /// <summary>A float skin value from the table; the <paramref name="fallback"/> is used unless a loaded skin
    /// file overrides the key.</summary>
    private static float TableNum(string key, float fallback)
    {
        EnsureValues();
        return _skin!.IsOverridden(key) ? _skin.Float(key, fallback) : fallback;
    }
}
