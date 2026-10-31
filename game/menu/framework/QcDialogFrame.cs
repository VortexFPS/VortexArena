using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// A dialog opened on top of another (a QC dialog shown by DialogOpenButton_Click) that knows its own QC size: its
/// title, <c>intendedWidth</c> and <c>rows</c> (xonotic/dialog.qh), and the colour of its border
/// (SKINCOLOR_DIALOG_*). <see cref="MenuRoot"/> frames such a dialog as Xonotic does (<see cref="QcDialogFrame"/>)
/// instead of in its generic large panel.
/// </summary>
public interface IQcDialog
{
    string QcTitle { get; }
    float QcIntendedWidth { get; }
    float QcRows { get; }
    Color QcBorderColor => Colors.White;
    /// <summary>Placed with <c>addItemRightCentered</c> (xonotic/mainwindow.qc: the HUD panel dialogs) — flush with
    /// the right edge of the menu canvas, centred vertically — instead of <c>addItemCentered</c>.</summary>
    bool QcRightCentered => false;
    /// <summary>QC <c>closable</c>: false for the first-run, terms-of-service and uid2name dialogs, which have no
    /// close button and must be answered.</summary>
    bool QcClosable => true;
}

/// <summary>A dialog with a QC <c>focusEnter</c>: told when it gets the focus, i.e. when the nexposee has finished
/// opening it.</summary>
public interface IQcFocusEnter
{
    void FocusEnter();
}

/// <summary>
/// The frame of a pushed QC dialog, drawn as an open main-menu panel is (item/dialog.qc configureDialog,
/// item/borderimage.qc): <c>intendedWidth</c> of the 750-wide menu canvas by the height its rows imply (title bar,
/// margins, rows and row spacing), centred on the screen; the skin's border picture tinted by the dialog's colour;
/// the title bold at SKINFONTSIZE_TITLE centred in the top border; the close button in the top-right corner; the
/// dialog's items inset by the margins below the title bar.
/// </summary>
public partial class QcDialogFrame : Control
{
    private readonly IQcDialog _dialog;
    private readonly Panel _border = new() { Name = "Border", MouseFilter = MouseFilterEnum.Stop };
    private readonly QcLabel _title;
    private readonly Button _close = new() { Name = "Close", Flat = true, FocusMode = FocusModeEnum.None };
    private readonly Control _content = new() { Name = "Content", MouseFilter = MouseFilterEnum.Pass };

    public QcDialogFrame(Control screen, IQcDialog dialog, System.Action close)
    {
        _dialog = dialog;
        Name = "QcDialogFrame";
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsPreset(LayoutPreset.FullRect);

        _border.SelfModulate = dialog.QcBorderColor;
        AddChild(_border);
        _title = new QcLabel(dialog.QcTitle, 0.5f) { Bold = true, FontSize = MenuMetrics.FontTitle, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_title);

        if (MenuSkin.CloseButton is { } icon)
        {
            _close.Icon = icon;
            _close.ExpandIcon = true;
            var empty = new StyleBoxEmpty();
            foreach (string s in new[] { "normal", "hover", "pressed", "focus" })
                _close.AddThemeStyleboxOverride(s, empty);
        }
        else
            _close.Text = "X";
        _close.Pressed += close;
        _close.Visible = dialog.QcClosable;
        AddChild(_close);

        if (screen is MenuScreen ms) ms.HostProvidesTitle = true;
        screen.SetAnchorsPreset(LayoutPreset.FullRect);
        _content.AddChild(screen);
        AddChild(_content);
        Resized += Layout;
    }

    public override void _Ready() => Layout();

    private void Layout()
    {
        float u = MenuMetrics.DesignUnit;
        var size = new Vector2(MenuMetrics.DialogWidth(_dialog.QcIntendedWidth), MenuMetrics.DialogHeight(_dialog.QcRows)) * u;
        Vector2 origin = (Size - size) * 0.5f;
        if (_dialog.QcRightCentered)
        {
            // Container_addItemRightCentered: origin.x = 1 - 0.5 * w, bounded to 1 - w, of the 1.25-aspect canvas
            // the menu draws in, centred in the window (menu.qc draw_reset_cropped).
            float canvasRight = 0.5f * (Size.X + MenuMetrics.ConWidth * u);
            origin.X = Mathf.Max(0f, canvasRight - size.X);
        }
        float th = MenuMetrics.TitleHeight * u;

        int borderPx = Mathf.Max(1, Mathf.RoundToInt(th));
        if (MenuSkin.DialogFrame(borderPx) is { } style)
            _border.AddThemeStyleboxOverride("panel", style);
        _border.Position = origin;
        _border.Size = size;

        _title.Position = origin;
        _title.Size = new Vector2(size.X, th);
        _close.Position = origin + new Vector2(size.X - th, 0f);
        _close.Size = new Vector2(th, th);

        float ml = MenuMetrics.MarginLeft * u, mr = MenuMetrics.MarginRight * u;
        float mt = MenuMetrics.MarginTop * u, mb = MenuMetrics.MarginBottom * u;
        _content.Position = origin + new Vector2(ml, th + mt);
        _content.Size = new Vector2(Mathf.Max(1f, size.X - ml - mr), Mathf.Max(1f, size.Y - th - mt - mb));
    }
}
