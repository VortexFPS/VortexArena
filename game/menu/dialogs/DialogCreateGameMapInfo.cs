using Godot;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Menu;

namespace VortexArena.Game.Menu;

/// <summary>
/// "Map Information" — <c>XonoticMapInfoDialog_fill</c> + <c>loadMapInfo</c>
/// (qcsrc/menu/xonotic/dialog_multiplayer_create_mapinfo.qc) on its 11 x 10 grid, the full menu width, titled with
/// the map's bsp name (<c>me.border.setText</c>): the 4:3 preview over rows 0..8 on the left (the
/// <c>/maps/&lt;bsp&gt;</c> → <c>/levelshots/&lt;bsp&gt;</c> → <c>nopreview_map</c> chain); from column 3.5 the title
/// and author (decolorized, cut to fit, in SKINCOLOR_MAPLIST_TITLE / _AUTHOR), "Gametypes:", and every menu
/// gametype (util.qc GAMETYPES then HIDDEN_GAMETYPES, 4 per row) — disabled when the map does not support it; the
/// description on row 9; "Close" and "MAP^Play" on the last row, Play disabled while <c>g_campaign</c> is on.
///
/// The metadata comes from the headless <see cref="MapInfoBackend"/> (the C# .mapinfo parser). Play gathers a
/// <see cref="MatchConfig"/> for the map (the create-game screen's gametype when the map supports it, else the
/// map's first) and fires <see cref="CreateGameScreen.RaiseStartGame"/> — the Start button's path (QC
/// <c>MapList_LoadMap</c>).
/// </summary>
public partial class DialogCreateGameMapInfo : MenuScreen, IQcDialog
{
    // util.qc GAMETYPES + HIDDEN_GAMETYPES: GameType_GetID(i) for i < GameType_GetTotalCount(), as (port NetName,
    // MapInfo_Type_ToText fallback when the registry lacks it).
    private static readonly (string Net, string Name)[] MenuGametypes =
    {
        ("dm", "Deathmatch"), ("tdm", "Team Deathmatch"), ("ctf", "Capture the Flag"), ("ca", "Clan Arena"),
        ("freezetag", "Freeze Tag"), ("mayhem", "Mayhem"), ("tmayhem", "Team Mayhem"), ("ka", "Keepaway"),
        ("tka", "Team Keepaway"), ("kh", "Key Hunt"), ("lms", "Last Man Standing"), ("dom", "Domination"),
        ("nb", "Nexball"), ("ons", "Onslaught"), ("as", "Assault"), ("surv", "Survival"),
        ("rc", "Race"), ("cts", "Race CTS"), ("inv", "Invasion"),
    };

    private readonly string _bspName;
    private readonly string? _preferredGametype;

    // dialog_multiplayer_create_mapinfo.qh
    public string QcTitle => _bspName.Length > 0 ? _bspName : Localization.Tr("Map Information");
    public float QcIntendedWidth => 1.0f;
    public float QcRows => 11;
    public Color QcBorderColor => MenuSkin.SkinColor("COLOR_DIALOG_MAPINFO", 1f, 1f, 1f);

    public DialogCreateGameMapInfo() : this("") { }

    /// <param name="bspName">The map file stem (e.g. "stormkeep") — QC MapInfo_Map_bspname.</param>
    /// <param name="preferredGametype">The create-game screen's currently selected gametype NetName, used for Play
    /// when the map supports it (else the map's first supported gametype is used).</param>
    public DialogCreateGameMapInfo(string bspName, string? preferredGametype = null)
    {
        _bspName = bspName ?? "";
        _preferredGametype = preferredGametype;
    }

    protected override void BuildUi()
    {
        Name = "DialogCreateGameMapInfo"; // QC XonoticMapInfoDialog

        var backend = new MapInfoBackend(MenuDataBridge.ReadText, MenuDataBridge.ImageExists)
        {
            // QC Duel.m_isForcedSupported: g_duel_not_dm_maps (default 0 → forced support active).
            ForceDuelOnDmMaps = MenuState.Cvars.GetFloat("g_duel_not_dm_maps") == 0f,
            // QC TeamDeathmatch.m_isForcedSupported: g_tdm_on_dm_maps (opposite polarity).
            ForceTdmOnDmMaps = MenuState.Cvars.GetFloat("g_tdm_on_dm_maps") != 0f,
        };
        MapInfo info = backend.Get(_bspName);                 // MapInfo_Get_ByID
        string previewBase = backend.PreviewImage(_bspName);  // the preview fallback chain

        var me = new QcGrid(QcRows, 10);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        float alpha = MenuSkin.Text.A;
        me.TR();
            me.TDempty(0.2f);
            me.TD(me.Rows - 2, 3, new QcImage(ResolvePreview(previewBase), 4f / 3f));
        me.GotoRC(0, 3.5f); me.SetFirstColumn(me.CurrentColumn);
        float w = me.Columns - me.CurrentColumn;
            me.TD(1, 1, Qc.Label("Title:"));
            me.TD(1, w - 1, new QcTextLabel
            {
                Text = MenuColorCodes.Strip(info.Title),
                AllowCut = true,
                Color = MenuSkin.TextLift(MenuSkin.Fade(MenuSkin.SkinColor("COLOR_MAPLIST_TITLE", 1f, 1f, 1f), alpha)),
            });
        me.TR();
            me.TD(1, 1, Qc.Label("Author:"));
            me.TD(1, w - 1, new QcTextLabel
            {
                Text = MenuColorCodes.Strip(info.Author),
                AllowCut = true,
                Color = MenuSkin.TextLift(MenuSkin.Fade(MenuSkin.SkinColor("COLOR_MAPLIST_AUTHOR", 0.4f, 0.4f, 0.7f), alpha)),
            });
        me.TR();
            me.TD(1, w, Qc.Label("Gametypes:"));

        int total = MenuGametypes.Length;
        int n = (int)System.MathF.Ceiling(total / (me.Rows - 6));
        float wgt = (w - 0.2f) / n;
        for (int i = 0; i < total; ++i)
        {
            if (i % n == 0)
            {
                me.TR();
                me.TDempty(0.2f);
            }
            (string net, string name) = MenuGametypes[i];
            me.TD(1, wgt, new QcLabel(GametypeName(net, name)) { Disabled = !info.Supports(net) });
        }

        me.GotoRC(me.Rows - 2, 0);
            me.TD(1, me.Columns, new QcTextLabel("", 0.5f) { Text = info.Description, AllowCut = true });

        me.GotoRC(me.Rows - 1, 0);
            me.TDempty(0.5f);
            me.TD(1, me.Columns - 5.5f, Qc.Button("Close", GoBack));
            me.TD(1, me.Columns - 5.5f, Qc.Button("Play", () => Play(info), "Load this map"))
                .Dep("g_campaign", 0, 0);
    }

    /// <summary>MapInfo_Type_ToText: the registry's name for the gametype, else the QC one.</summary>
    private static string GametypeName(string net, string fallback)
    {
        foreach (GameType gt in GameTypes.All)
            if (gt.NetName == net && !string.IsNullOrEmpty(gt.DisplayName))
                return gt.DisplayName;
        return fallback;
    }

    /// <summary>
    /// QC MapList_LoadMap: start the match on this map, with the gametype the create-game screen had selected when
    /// the map supports it, otherwise the map's first supported gametype, else "dm".
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
        GoBack();
    }

    private string ChooseGametype(MapInfo info)
    {
        if (!string.IsNullOrEmpty(_preferredGametype) && info.Supports(_preferredGametype!))
            return _preferredGametype!;
        foreach (string gt in info.SupportedGametypes)
            return gt; // first supported
        return string.IsNullOrEmpty(_preferredGametype) ? "dm" : _preferredGametype!;
    }

    /// <summary>The preview picture for the fallback chain's result ("nopreview_map" is a skin picture).</summary>
    private static Texture2D? ResolvePreview(string previewBase)
    {
        if (previewBase == "nopreview_map")
            return MenuSkin.SkinImage("nopreview_map");
        string baseName = previewBase.StartsWith('/') ? previewBase[1..] : previewBase;
        return MenuSkin.Image(baseName) ?? MenuSkin.SkinImage("nopreview_map");
    }
}
