using System;
using System.Collections.Generic;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// "Panel HUD Setup" — <c>XonoticHUDExitDialog_fill</c> (qcsrc/menu/xonotic/dialog_hudsetup_exit.qc) on its
/// 18 x 8.2 grid, 0.8 of the menu wide, in SKINCOLOR_DIALOG_TEAMSELECT: on the left the HUD skin list (filter,
/// Refresh, "Set skin" through the <see cref="DialogHudSkinReset"/> confirm, "Save current skin" with its name
/// box); on the right the panel background defaults, the HUD dock and the editor grid; "Exit setup"
/// (<c>_hud_configure 0</c>, COMMANDBUTTON_CLOSE) across the bottom.
///
/// <para>The skin list is <c>XonoticHUDSkinList</c> drawn by the demo list's row widget (<see cref="QcNameList"/>:
/// one SKINCOLOR_TEXT row a character in, shortened to fit), filled the QC way: <c>hud_*.cfg</c> then
/// <c>data/hud_*.cfg</c> from the mounted files (or <see cref="HudPanelCommon.SkinListProvider"/> when set), sorted
/// by file name, each row the cfg's <c>//title</c> or its name, with <c>(//author)</c> after it.</para>
/// </summary>
public partial class DialogHudSetupExit : MenuScreen, IQcDialog
{
    public string QcTitle => Localization.Tr("Panel HUD Setup");
    public float QcIntendedWidth => 0.8f;
    public float QcRows => 18;
    public Color QcBorderColor => HudPanelCommon.DialogColor;

    private const float Columns = 8.2f;

    private readonly List<string> _paths = new();
    private QcNameList _list = null!;
    private string? _filter;
    private string _savedName = "";

    protected override void BuildUi()
    {
        Name = "DialogHudSetupExit"; // QC XonoticHUDExitDialog, "HUDExit"

        var me = new QcGrid(QcRows, Columns);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        _list = new QcNameList(skinColor: true);
        _list.ItemActivated += _ => SetHudSkin(); // doubleClickListBoxItem / Enter
        float vs = QcLook.DefaultValueSpace;

        me.TR();
            me.TD(1, 4, Qc.Header("HUD skins"));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 0.7f, Qc.Label("Filter:", 0.5f));
            var filter = me.TD(1, 2.9f, QcLook.InputBox(new LineEdit()));
                filter.TextChanged += FilterChange;
        me.TR();
            me.TD(12, 4, _list);
        me.GotoRC(QcRows - 4, 0);
            me.TDempty(0.2f);
            me.TD(1, 1.4f, Qc.Button("Refresh", RefreshClick));
            me.TD(1, 2.2f, Qc.Button("Set skin", () => Menu?.Push(new DialogHudSkinReset(SetHudSkin))));
        me.GotoRC(QcRows - 2.5f, 0);
            me.TD(1, 2, Qc.Button("Save current skin", SaveHudSkin,
                "Note: HUD skins are saved in data/data/ directory and can be manually renamed/deleted from there"));
            var saveName = me.TD(1, 2, QcLook.InputBox(new LineEdit()));
                saveName.TextChanged += t => _savedName = t;

        me.GotoRC(0, 4.2f); me.SetFirstColumn(me.CurrentColumn);
            me.TD(1, 4, Qc.Label("Panel background defaults:"));
        me.TR();
            me.TD(1, 1.4f, Qc.Label("Background:"));
                me.TD(1, 2.6f, HudPanelCommon.TextSlider("hud_panel_bg", 4f / 3f)
                    .Add(Localization.Tr("Disable"), "0")
                    .Add("border_default", "border_default")); // this is a file name!
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.2f, Qc.Label("Color:"));
            me.TD(2, 2.6f, HudPanelCommon.ColorPicker("hud_panel_bg_color"));
        me.TR();
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.2f, Qc.Label("Border size:"));
                me.TD(1, 2.6f, Qc.Mixed("hud_panel_bg_border")
                    .Add("Disable", 0)
                    .AddRange(2, 20, 2)
                    .Done());
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.2f, Qc.Label("Opacity:"));
                me.TD(1, 2.6f, Qc.Slider(0.05f, 1, 0.05f, "hud_panel_bg_alpha", "", "%"));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.2f, Qc.Label("Team color:"));
                me.TD(1, 2.6f, Qc.Mixed("hud_panel_bg_color_team", "", "%")
                    .Add("Disable", 0)
                    .AddRange(0.1f, 1, 0.1f)
                    .Done());
        me.TR();
            me.TDempty(0.4f);
            me.TD(1, 3.6f, Qc.Check(0, "hud_configure_teamcolorforced", "Test team color in configure mode"));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.2f, Qc.Label("Padding:"));
                me.TD(1, 2.6f, Qc.Slider(-5, 5, 1, "hud_panel_bg_padding"));

        me.TR();
            me.TD(1, 1.4f, Qc.Label("HUD Dock:"));
                me.TD(1, 2.6f, HudPanelCommon.TextSlider("hud_dock")
                    .Add(HudPanelCommon.Z("DOCK^Disabled"), "0")
                    .Add(HudPanelCommon.Z("DOCK^Small"), "dock_small")
                    .Add(HudPanelCommon.Z("DOCK^Medium"), "dock_medium")
                    .Add(HudPanelCommon.Z("DOCK^Large"), "dock_large"));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.2f, Qc.Label("Color:"));
            me.TD(2, 2.6f, HudPanelCommon.ColorPicker("hud_dock_color"));
        me.TR();
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.2f, Qc.Label("Opacity:"));
                me.TD(1, 2.6f, Qc.Slider(0.05f, 1, 0.05f, "hud_dock_alpha", "", "%"));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.2f, Qc.Label("Team color:"));
                me.TD(1, 2.6f, Qc.Mixed("hud_dock_color_team", "", "%")
                    .Add("Disable", 0)
                    .AddRange(0.1f, 1, 0.1f)
                    .Done());
        me.TR();
            me.TD(1, 1.4f, Qc.Label("Grid settings:"));
            me.TD(1, 2.6f, Qc.Check(0, "hud_configure_grid", "Snap panels to grid"));
        me.TR();
            me.TDempty(0.2f);
            me.TD(1, 1.2f, Qc.Label("Grid size:")).Dep("hud_configure_grid", 1, 1);
            me.TD(1, 0.2f, Qc.Label("X:")).Dep("hud_configure_grid", 1, 1);
                me.TD(1, 1.1f, Qc.Slider(0.005f, 0.07f, 0.005f, "hud_configure_grid_xsize", "", "%", vs))
                    .Dep("hud_configure_grid", 1, 1);
            me.TD(1, 0.2f, Qc.Label("Y:")).Dep("hud_configure_grid", 1, 1);
                me.TD(1, 1.1f, Qc.Slider(0.005f, 0.07f, 0.005f, "hud_configure_grid_ysize", "", "%", vs))
                    .Dep("hud_configure_grid", 1, 1);
        me.TR();
            me.TDempty(0.2f);
            const string verticalLines = "hud_configure_vertical_lines";
            me.TD(1, 3.6f, Qc.CheckEx(0.5f, 0, verticalLines, "Center line",
                Localization.Tr("Show a vertical centerline to help align panels. It's possible to show more vertical lines by editing %s in the console")
                    .Replace("%s", verticalLines)));

        me.GotoRC(QcRows - 1, 0);
            var exit = me.TD(1, Columns, Qc.Command("Exit setup", "_hud_configure 0"));
                exit.Pressed += GoBack; // COMMANDBUTTON_CLOSE

        GetHudSkins(); // showNotify
    }

    // -----------------------------------------------------------------------------------------------------
    //  XonoticHUDSkinList (xonotic/hudskinlist.qc)
    // -----------------------------------------------------------------------------------------------------

    /// <summary>XonoticHUDSkinList_getHUDSkins: <c>hud_*.cfg</c> and <c>data/hud_*.cfg</c> through the filter, sorted
    /// by file name, each shown as its title (or name) and author.</summary>
    private void GetHudSkins()
    {
        IEnumerable<string> files = HudPanelCommon.SkinListProvider?.Invoke() ?? ScanSkinFiles();
        var entries = new List<(string File, string Path)>();
        foreach (string f in files)
        {
            int slash = f.LastIndexOf('/');
            string file = slash >= 0 ? f[(slash + 1)..] : f;
            if (!file.StartsWith("hud_", StringComparison.OrdinalIgnoreCase)
                || !file.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)
                || !DialogMediaDemo.Matches(file[..^4], _filter is null ? null : "hud_" + _filter))
                continue;
            entries.Add((file, f));
        }
        entries.Sort((a, b) => string.CompareOrdinal(a.File, b.File)); // buf_sort by filename

        _paths.Clear();
        var rows = new List<string>();
        foreach ((string file, string path) in entries)
        {
            _paths.Add(path);
            (string title, string author) = ReadTitleAuthor(path);
            string s = title.Length > 0 ? title : file[4..^4]; // strip "hud_" and ".cfg"
            if (author.Length > 0) s += " (" + author + ")";
            rows.Add(s);
        }
        _list.SetNames(rows);
    }

    /// <summary>getHUDSkinFiles(me, sortbuf, "") and (…, "data/"): only files directly in each directory.</summary>
    private static IEnumerable<string> ScanSkinFiles()
    {
        foreach (string dir in new[] { "", "data/" })
            foreach (string f in MenuDataBridge.Files.Find(dir + "hud_", ".cfg"))
                if (f.IndexOf('/', dir.Length) < 0)
                    yield return f;
    }

    /// <summary>The cfg's leading <c>//title …</c> / <c>//author …</c> comment lines (tokenize_console, argv(1)).</summary>
    private static (string Title, string Author) ReadTitleAuthor(string path)
    {
        string title = "", author = "";
        string? text = MenuDataBridge.ReadText(path);
        if (text is null) return (title, author);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (!line.StartsWith("//", StringComparison.Ordinal)) break;
            List<string> argv = Tokenize(line[2..]);
            if (argv.Count < 2) continue;
            if (argv[0] == "title") title = argv[1];
            else if (argv[0] == "author") author = argv[1];
        }
        return (title, author);
    }

    /// <summary>tokenize_console: whitespace-separated words, a double-quoted run being one word.</summary>
    private static List<string> Tokenize(string s)
    {
        var argv = new List<string>();
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) break;
            int start;
            if (s[i] == '"')
            {
                start = ++i;
                while (i < s.Length && s[i] != '"') i++;
                argv.Add(s[start..i]);
                i++;
            }
            else
            {
                start = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
                argv.Add(s[start..i]);
            }
        }
        return argv;
    }

    /// <summary>HUDSkinList_Refresh_Click.</summary>
    private void RefreshClick()
    {
        GetHudSkins();
        _list.SetSelected(0); // always select the first element after a list update
    }

    /// <summary>HUDSkinList_Filter_Change: a plain word matches anywhere in the name.</summary>
    private void FilterChange(string text)
    {
        _filter = DialogMediaDemo.Glob(text);
        GetHudSkins();
    }

    /// <summary>SaveHUDSkin_Click: <c>hud save "name"</c> ("myconfig" when empty), the list refreshed a second later.</summary>
    private void SaveHudSkin()
    {
        string s = _savedName.Length > 0 ? _savedName : "myconfig";
        Game.Hud.HudConfigEditor.ExportCfg(s);
        GetTree()?.CreateTimer(1.0).Connect(SceneTreeTimer.SignalName.Timeout, Callable.From(() =>
        {
            if (IsInstanceValid(this) && IsInsideTree()) RefreshClick();
        }));
    }

    /// <summary>XonoticHUDSkinList_setHUDSkin: exec the selected skin's cfg.</summary>
    private void SetHudSkin()
    {
        int i = _list.SelectedItem;
        if (i < 0 || i >= _paths.Count) return;
        MenuCommand.Run("exec \"" + _paths[i] + "\"");
    }
}

/// <summary>
/// "Reset HUD skin" — <c>XonoticSkinResetDialog_fill</c> (qcsrc/menu/xonotic/dialog_hudsetup_exit.qc) on its 3 x 2
/// grid, 0.5 of the menu wide, in SKINCOLOR_DIALOG_QUIT: the question, then a red "Yes" that applies the selected
/// skin (<c>SetHUDSkin_Click</c>, which leaves the dialog open, as the QC does) and a green "No" that closes it.
/// </summary>
public partial class DialogHudSkinReset : MenuScreen, IQcDialog
{
    private readonly Action? _setHudSkin;

    public DialogHudSkinReset() { }

    public DialogHudSkinReset(Action setHudSkin) => _setHudSkin = setHudSkin;

    public string QcTitle => Localization.Tr("Reset HUD skin");
    public float QcIntendedWidth => 0.5f;
    public float QcRows => 3;
    public Color QcBorderColor => MenuSkin.SkinColor("COLOR_DIALOG_QUIT", 1f, 1f, 1f);

    protected override void BuildUi()
    {
        Name = "DialogHudSkinReset"; // QC XonoticSkinResetDialog

        var me = new QcGrid(QcRows, 2);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        me.TR();
            me.TD(1, 2, Qc.Label("Are you sure you want to reset your HUD to this skin?", 0.5f));
        me.TR();
        me.TR();
            me.TD(1, 1, Qc.Button("Yes", () => _setHudSkin?.Invoke(), tint: new Color(1, 0, 0)));
            me.TD(1, 1, Qc.Button("No", GoBack, tint: new Color(0, 1, 0)));
    }
}
