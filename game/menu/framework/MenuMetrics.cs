namespace VortexArena.Game.Menu;

/// <summary>
/// The Xonotic menu coordinate system + dialog metrics, ported from the QC so dialog sizes, title bars, rows and
/// margins have the same proportions as Base. Xonotic lays the menu out in a virtual canvas of fixed aspect
/// <c>MENU_ASPECT = 1280/1024 = 1.25</c> (menu.qc); every dialog declares an <see cref="DialogHeight">intendedWidth
/// + rows</see> and the engine computes its pixel size from the shared per-row / per-title / per-margin constants
/// (item/dialog.qc <c>configureDialog</c>, values from gfx/menu/luma/skinvalues.txt). All values here are in those
/// virtual "menu pixels" (a <see cref="ConHeight"/>-tall space); multiply by <see cref="FitScale"/> for screen px.
/// </summary>
public static class MenuMetrics
{
    /// <summary>Menu virtual canvas height (px). The whole menu scales to the viewport height from this.</summary>
    public const float ConHeight = 600f;
    /// <summary>MENU_ASPECT (1280/1024) — the menu's fixed width:height ratio.</summary>
    public const float MenuAspect = 1.25f;
    /// <summary>Menu virtual canvas width (px) = ConHeight * MenuAspect.</summary>
    public const float ConWidth = ConHeight * MenuAspect; // 750

    // skinvalues.txt: FONTSIZE_NORMAL 12, FONTSIZE_TITLE 16, HEIGHT_NORMAL 1.45, HEIGHT_TITLE 1.45.
    public const float FontNormal = 12f;
    public const float FontTitle = 16f;
    /// <summary>Row height = SKINFONTSIZE_NORMAL * SKINHEIGHT_NORMAL (item/xonotic/dialog.qh).</summary>
    public const float RowHeight = FontNormal * 1.45f;   // 17.4
    /// <summary>Title-bar height = SKINFONTSIZE_TITLE * SKINHEIGHT_TITLE; also the dialog's border thickness.</summary>
    public const float TitleHeight = FontTitle * 1.45f;  // 23.2

    // Dialog content margins / spacings (skinvalues MARGIN_*).
    public const float MarginTop = 8f;
    public const float MarginBottom = 12f;
    public const float MarginLeft = 16f;
    public const float MarginRight = 16f;
    public const float ColumnSpacing = 4f;
    public const float RowSpacing = 4f;
    /// <summary>HEIGHT_DIALOGBORDER — number of title-bar heights the border occupies.</summary>
    public const float BorderLines = 1f;

    /// <summary>
    /// A dialog's content height in menu px for <paramref name="rows"/> rows, exactly as item/dialog.qc:
    /// <c>borderLines*titleHeight + marginTop + rows*rowHeight + (rows-1)*rowSpacing + marginBottom</c>.
    /// </summary>
    public static float DialogHeight(float rows)
        => BorderLines * TitleHeight + MarginTop + rows * RowHeight + (rows - 1) * RowSpacing + MarginBottom;

    /// <summary>A dialog's width in menu px = intendedWidth (a fraction of the canvas) * ConWidth.</summary>
    public static float DialogWidth(float intendedWidth) => intendedWidth * ConWidth;

    /// <summary>Menu-px → screen-px scale for a viewport <paramref name="viewportHeight"/> px tall (fit to height).</summary>
    public static float FitScale(float viewportHeight) => viewportHeight / ConHeight;
}
