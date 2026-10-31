using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// "Winner" — <c>XonoticWinnerDialog_fill</c> (qcsrc/menu/xonotic/dialog_singleplayer_winner.qc) on its 12 x 3
/// grid, 0.32 of the menu wide: the <c>/gfx/winner</c> picture at its own aspect over rows 0..9, then a full-width
/// "OK" (<c>Dialog_Close</c>) on the last row. The QC also plays MENU_SOUND_WINNER on focus
/// (<c>XonoticWinnerDialog_focusEnter</c>); the port's menu has no focus-sound hook here, so that cue is omitted.
/// Shown after winning a single-player campaign level.
/// </summary>
public partial class DialogWinner : MenuScreen, IQcDialog
{
    // The "/gfx/winner" picture: a bare VFS name first (resolved through the content VFS, which probes
    // .tga/.png/.jpg), then a Godot resource as a fallback for art shipped that way. The content tree is not
    // imported by Godot (.gdignore), so only the VFS reaches it.
    private static readonly string[] WinnerImagePaths =
    {
        "gfx/winner",
        "res://gfx/winner.png",
    };

    // dialog_singleplayer_winner.qh
    public string QcTitle => Localization.Tr("Winner");
    public float QcIntendedWidth => 0.32f;
    public float QcRows => 12;
    public Color QcBorderColor => MenuSkin.SkinColor("COLOR_DIALOG_SINGLEPLAYER", 1f, 1f, 1f);

    protected override void BuildUi()
    {
        Name = "DialogWinner"; // QC XonoticWinnerDialog

        var me = new QcGrid(QcRows, 3);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        me.TR();
            me.TD(me.Rows - 2, me.Columns, new QcImage(LoadWinnerBanner(), -1f));

        me.GotoRC(me.Rows - 1, 0);
            me.TD(1, me.Columns, Qc.Button("OK", GoBack));
    }

    /// <summary>The "/gfx/winner" picture, or null when the content has none (then nothing is drawn, as the QC
    /// draws nothing for a missing picture in a menu image).</summary>
    private static Texture2D? LoadWinnerBanner()
        => VortexArena.Game.Hud.TextureCache.GetFirst(WinnerImagePaths);
}
