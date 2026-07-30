using System.Collections.Generic;
using Godot;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Menu;

namespace VortexArena.Game.Menu;

/// <summary>
/// The Create-Game map-info popup — a faithful C# port of <c>XonoticMapInfoDialog</c>
/// (qcsrc/menu/xonotic/dialog_multiplayer_create_mapinfo.qc): the dialog that pops up when a map is
/// double-clicked in the create-game map list. The QC <c>loadMapInfo</c> (lines 9-39) runs
/// <c>MapInfo_Get_ByID</c>, then shows the bsp name, the (decolorized, author-stripped) title, the author,
/// the description, the preview image (with the <c>/maps/&lt;bsp&gt;</c> → <c>/levelshots/&lt;bsp&gt;</c> →
/// <c>nopreview_map</c> fallback chain), and a checklist of EVERY gametype with the ones the map doesn't
/// support disabled. The bottom row is "Close" and "MAP^Play" (which loads the map — QC
/// <c>MapList_LoadMap</c>, gated by <c>g_campaign 0</c>).
///
/// This is a working port over the headless <see cref="MapInfoBackend"/> (the C# .mapinfo parser): the
/// metadata + the gametype checklist come from the real <c>maps/&lt;bsp&gt;.mapinfo</c> when present, and the
/// preview falls through the same chain. "Play" gathers a <see cref="MatchConfig"/> for the chosen map (using
/// the map's first supported gametype, or the currently selected create-game gametype) and fires
/// <see cref="CreateGameScreen.RaiseStartGame"/> — the same path the create-game Start button uses.
/// </summary>
public partial class DialogCreateGameMapInfo : Control, IMenuScreen
{
    private readonly string _bspName;
    private readonly string? _preferredGametype;

    /// <summary>The host menu — assigned by <see cref="MenuRoot.Push"/> so Close/Play can pop this dialog cleanly
    /// (without it, Close would orphan the framing PanelContainer on the menu stack).</summary>
    public MenuRoot? Menu { get; set; }

    /// <param name="bspName">The map file stem (e.g. "stormkeep") — QC MapInfo_Map_bspname.</param>
    /// <param name="preferredGametype">The create-game screen's currently selected gametype NetName, used for Play
    /// when the map supports it (else the map's first supported gametype is used).</param>
    public DialogCreateGameMapInfo(string bspName, string? preferredGametype = null)
    {
        _bspName = bspName ?? "";
        _preferredGametype = preferredGametype;
    }

    public override void _Ready()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;

        var backend = new MapInfoBackend(MenuDataBridge.ReadText, MenuDataBridge.ImageExists)
        {
            // QC Duel.m_isForcedSupported: wire g_duel_not_dm_maps (default 0 → forced support active).
            ForceDuelOnDmMaps = MenuState.Cvars.GetFloat("g_duel_not_dm_maps") == 0f,
            // QC TeamDeathmatch.m_isForcedSupported: wire g_tdm_on_dm_maps (default 0 → forced support OFF;
            // set to 1 to auto-offer TDM on every DM map). Opposite polarity to the duel cvar above.
            ForceTdmOnDmMaps = MenuState.Cvars.GetFloat("g_tdm_on_dm_maps") != 0f,
        };
        MapInfo info = backend.Get(_bspName);          // QC MapInfo_Get_ByID
        string previewBase = backend.PreviewImage(_bspName); // QC preview fallback chain

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 24);
        AddChild(margin);

        var root = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddThemeConstantOverride("separation", 12);
        margin.AddChild(root);

        // QC: me.border.setText(currentMapBSPName) — the bsp name heads the dialog.
        root.AddChild(Ui.Title(_bspName));

        var body = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 18);
        root.AddChild(body);

        // ------------------------------------------------------------------- LEFT: 4:3 preview image
        var preview = new TextureRect
        {
            Texture = ResolvePreview(previewBase),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(240, 180), // QC makeXonoticImage(_, 4.0/3.0)
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        body.AddChild(preview);

        // ------------------------------------------------------------------- RIGHT: metadata + gametypes
        var details = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        details.AddThemeConstantOverride("separation", 6);
        body.AddChild(details);

        details.AddChild(Ui.Row("Title:", ColoredValue(info.Title, MenuSkin.Bright), 80f));
        details.AddChild(Ui.Row("Author:", ColoredValue(info.Author, MenuSkin.Text), 80f));

        details.AddChild(Ui.Spacer(4f));
        details.AddChild(Ui.Header("Gametypes:"));

        // QC: every gametype is shown; the ones the map doesn't support are disabled (greyed) — fill the grid.
        var gtGrid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        gtGrid.AddThemeConstantOverride("h_separation", 12);
        gtGrid.AddThemeConstantOverride("v_separation", 2);
        details.AddChild(gtGrid);

        foreach (GameType gt in GameTypes.All)
        {
            bool supported = info.Supports(gt.NetName);
            var lbl = Ui.Label(string.IsNullOrEmpty(gt.DisplayName) ? gt.NetName : gt.DisplayName);
            if (!supported)
                lbl.AddThemeColorOverride("font_color", new Color(0.45f, 0.47f, 0.52f)); // disabled (QC e.disabled = true)
            gtGrid.AddChild(lbl);
        }

        if (!info.HasMapInfoFile)
        {
            var noinfo = Ui.Label("(no .mapinfo for this map — gametype support unknown)");
            noinfo.AddThemeColorOverride("font_color", new Color(0.70f, 0.72f, 0.78f));
            details.AddChild(noinfo);
        }

        // QC: descriptionLabel spans the bottom (allowCut).
        if (!string.IsNullOrEmpty(info.Description))
        {
            details.AddChild(Ui.Spacer(4f));
            var desc = Ui.Label(info.Description);
            desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            desc.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            details.AddChild(desc);
        }

        // ------------------------------------------------------------------- BOTTOM: Close / Play
        var buttons = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        buttons.AddThemeConstantOverride("separation", 12);
        buttons.AddChild(Ui.Button("Close", Close));
        var play = Ui.Button("Play", () => Play(info)); // QC ZCTX(_("MAP^Play")) → MapList_LoadMap
        play.TooltipText = "Load this map (QC MapList_LoadMap; disabled in campaign)";
        buttons.AddChild(play);
        root.AddChild(buttons);
    }

    /// <summary>QC Dialog_Close — close the popup. Pops the menu stack when hosted by a <see cref="MenuRoot"/>
    /// (so the framing PanelContainer is removed and the screen beneath is re-shown); falls back to a direct
    /// self-removal when shown standalone (e.g. a test).</summary>
    private void Close()
    {
        if (Menu is not null)
        {
            Menu.Pop();
            return;
        }
        if (GetParent() is { } parent)
            parent.RemoveChild(this);
        QueueFree();
    }

    /// <summary>
    /// QC MapList_LoadMap: start the match on this map. Pick the gametype the create-game screen had selected
    /// when the map supports it; otherwise the map's first supported gametype; else fall back to "dm". Fires
    /// the same <see cref="CreateGameScreen.RaiseStartGame"/> path the Start button uses.
    /// </summary>
    private void Play(MapInfo info)
    {
        string gametype = ChooseGametype(info);

        float timeLimit = MenuState.Cvars.GetFloat("timelimit_override");
        float fragLimit = MenuState.Cvars.GetFloat("fraglimit_override");
        var config = new MatchConfig
        {
            Gametype = gametype,
            Map = _bspName,
            BotCount = (int)MenuState.Cvars.GetFloat("bot_number"),
            BotSkill = (int)MenuState.Cvars.GetFloat("skill"),
            TimeLimit = timeLimit > 0 ? (int)timeLimit : 0,
            FragLimit = fragLimit > 0 ? (int)fragLimit : 0,
        };
        CreateGameScreen.RaiseStartGame(config);
        Close();
    }

    private string ChooseGametype(MapInfo info)
    {
        if (!string.IsNullOrEmpty(_preferredGametype) && info.Supports(_preferredGametype!))
            return _preferredGametype!;
        foreach (string gt in info.SupportedGametypes)
            return gt; // first supported
        return string.IsNullOrEmpty(_preferredGametype) ? "dm" : _preferredGametype!;
    }

    /// <summary>Resolve the preview-image base name to a texture, falling back to the nopreview placeholder.</summary>
    private static Texture2D? ResolvePreview(string previewBase)
    {
        // "nopreview_map" is a skin image; a "/maps/<bsp>" or "/levelshots/<bsp>" is a content image.
        if (previewBase == "nopreview_map")
            return MenuSkin.SkinImage("nopreview_map");
        string baseName = previewBase.StartsWith('/') ? previewBase[1..] : previewBase;
        return MenuSkin.Image(baseName) ?? MenuSkin.SkinImage("nopreview_map");
    }

    private static Label ColoredValue(string text, Color color)
    {
        var l = Ui.Label(string.IsNullOrEmpty(text) ? "" : text);
        l.AddThemeColorOverride("font_color", color);
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return l;
    }
}
