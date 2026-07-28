using System;
using Godot;
using XonoticGodot.Common.Menu;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// The Godot-side bridge from the (Godot-free) menu <see cref="DataSource"/> backends to the mounted asset
/// VFS and the shared cvar store. The data sources in <c>XonoticGodot.Common</c> deliberately don't
/// reference Godot (ADR-0008), so each list backend takes these seams; this is the single place that wires
/// them to <see cref="MenuState"/>. Reused by the screenshot / music / skin dialogs.
/// </summary>
public static class MenuDataBridge
{
    /// <summary>
    /// An <see cref="IFileEnumerator"/> over <see cref="MenuState.Vfs"/> — the C# stand-in for the engine
    /// <c>search_begin</c> the QC list backends use. Empty (no matches) when the VFS isn't mounted, so a list
    /// shown before <see cref="MenuState.Boot"/> just renders empty rather than crashing.
    /// </summary>
    public sealed class VfsFiles : IFileEnumerator
    {
        public System.Collections.Generic.IEnumerable<string> Find(string prefix, string? extension = null)
            => MenuState.Vfs?.Find(prefix, extension) ?? System.Array.Empty<string>();
    }

    /// <summary>The shared file enumerator (singleton — the VFS is process-wide).</summary>
    public static readonly VfsFiles Files = new();

    /// <summary>Read a cvar's current string value from the shared store (CvarStringSource seam).</summary>
    public static string CvarString(string name) => MenuState.Cvars.GetString(name);

    /// <summary>Read a UTF-8 text file from the VFS, or null if absent (skinvalues.txt / .mapinfo seam).</summary>
    public static string? ReadText(string vpath)
    {
        var vfs = MenuState.Vfs;
        if (vfs is null || !vfs.Exists(vpath))
            return null;
        try { return vfs.ReadText(vpath); }
        catch { return null; }
    }

    /// <summary>
    /// Does a preview image exist for the (extension-agnostic) base name? Mirrors the QC
    /// <c>draw_PictureExists</c> / <c>draw_PictureSize != '0 0 0'</c> probes (skin preview, map preview). Uses
    /// the VFS image-resolution search (the DP extension precedence). Tolerates a leading "/".
    /// </summary>
    public static bool ImageExists(string vpathBase)
    {
        var vfs = MenuState.Vfs;
        if (vfs is null)
            return false;
        string baseName = vpathBase.StartsWith('/') ? vpathBase[1..] : vpathBase;
        try { return vfs.ResolveImage(baseName) is not null; }
        catch { return false; }
    }
}

/// <summary>
/// The menu-skin picker — a faithful C# port of <c>XonoticSkinList</c> (qcsrc/menu/xonotic/skinlist.qc) and
/// its host section in the User Settings tab (dialog_settings_user.qc:16-31: the "Menu Skin" header, the
/// skin list, and the "Set skin" button). The QC list enumerates <c>gfx/menu/*/skinvalues.txt</c>, parses
/// each skin's title/author and resolves its preview image, pre-selects the row whose NAME matches
/// <c>menu_skin</c>, and on "Set skin" writes <c>menu_skin</c> and runs <c>menu_restart; menu_cmd
/// skinselect</c> (skinlist.qc:150-154).
///
/// This is a fully working file-scan backend: with the asset VFS mounted the list populates from the real
/// installed skins; with no VFS it renders empty with an honest note. The selection is live (it reads/writes
/// the same <c>menu_skin</c> cvar the engine uses); the apply routes through <see cref="MenuCommand"/>
/// (<c>menu_restart</c> is handled by the menu host — inert until then, logged).
/// </summary>
public partial class DialogMediaSkinList : VBoxContainer
{
    // A VBoxContainer (not a plain Control): a Control reports NO minimum size for anchor-drawn children, so a
    // parent VBox allocated this widget ~0 height and its content painted over the siblings laid out after it
    // (the Settings→User overlap glitch). As a container, the header/list/note/button minimums propagate.

    private SkinSource _source = null!;
    private ItemList _list = null!;
    private Label _note = null!;

    public override void _Ready()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 8);

        // skinlist.qc:getSkins / loadCvars — scan + parse + select. defstring("menu_skin") = "default".
        string defaultSkin = MenuState.Cvars.GetDefault("menu_skin");
        if (string.IsNullOrEmpty(defaultSkin))
            defaultSkin = "default";
        _source = new SkinSource(MenuDataBridge.Files, MenuDataBridge.ReadText, MenuDataBridge.ImageExists, defaultSkin);

        // dialog_settings_user.qc:22 — makeXonoticHeaderLabel(_("Menu Skin")).
        AddChild(Ui.Header("Menu Skin"));

        // skinlist.qc list — one row per skin showing "Title — Author".
        _list = new ItemList
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 200),
        };
        _list.ItemActivated += OnItemActivated; // QC doubleClickListBoxItem → setSkin
        AddChild(_list);

        _note = Ui.Label("");
        _note.AddThemeColorOverride("font_color", new Color(0.70f, 0.72f, 0.78f));
        AddChild(_note);

        // dialog_settings_user.qc:27-29 — makeXonoticButton(_("Set skin")) onClick=SetSkin_Click → setSkin.
        AddChild(Ui.Button("Set skin", SetSkin));

        Reload();
    }

    /// <summary>Rescan skins and repopulate the list, restoring the selection from <c>menu_skin</c> (QC loadCvars).</summary>
    private void Reload()
    {
        int n = _source.Reload(null);
        _list.Clear();
        foreach (SkinInfo skin in _source.Skins)
        {
            string author = string.IsNullOrEmpty(skin.Author) || skin.Author == "<AUTHOR>" ? "" : $"  —  {skin.Author}";
            _list.AddItem($"{skin.Title}{author}");
        }

        // skinlist.qc:loadCvars — select the row whose NAME == menu_skin.
        int sel = _source.IndexOf(MenuState.Cvars.GetString("menu_skin"));
        if (sel >= 0 && sel < _list.ItemCount)
            _list.Select(sel);

        _note.Text = n > 0
            ? $"({n} menu skin{(n == 1 ? "" : "s")} found in gfx/menu/)"
            : "(no menu skins found — asset VFS not mounted, or gfx/menu/ is empty)";
    }

    private void OnItemActivated(long index) => SetSkin();

    /// <summary>
    /// QC <c>XonoticSkinList_setSkin</c> (skinlist.qc:150-154): saveCvars writes <c>menu_skin</c> to the
    /// selected row's NAME, then <c>menu_restart; menu_cmd skinselect</c> rebuilds the menu with the new skin.
    /// </summary>
    private void SetSkin()
    {
        int[] selected = _list.GetSelectedItems();
        if (selected.Length == 0)
            return;
        int i = selected[0];
        if (i < 0 || i >= _source.Skins.Count)
            return;

        string name = _source.Skins[i].Name;
        MenuState.Cvars.Set("menu_skin", name);     // skinlist.qc:saveCvars
        MenuState.Cvars.MarkArchived("menu_skin");
        // QC: localcmd("\nmenu_restart\nmenu_cmd skinselect\n") — handled by the T50 menu_cmd dispatch.
        MenuCommand.Run("menu_restart");
        MenuCommand.Run("menu_cmd skinselect");
    }
}
