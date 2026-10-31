using System;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The screenshot viewer — <c>XonoticScreenshotViewerDialog_fill</c> (qcsrc/menu/xonotic/dialog_media_screenshot_viewer.qc)
/// on its 25 x 4 grid, the whole menu wide: the picture (<see cref="QcScreenshotImage"/>) over every row but the
/// last, then "-", "+", "Reset", "Previous", "Next" and "Slide show" in twentieths of the width. The title is the
/// screenshot's name.
///
/// It walks the Screenshots tab's own list (goScreenshot moves its selection), changes picture every three seconds in
/// a slide show, zooms by 1.5 with the wheel or +/-, pans by dragging, and takes Left/Right for the previous/next
/// picture and Ctrl+Enter for the slide show. Unless <c>menu_screenshotviewer_enablemusic</c> is set, the music is
/// paused while it is open (loadScreenshot / close).
/// </summary>
public partial class DialogScreenshotViewer : MenuScreen, IQcDialog
{
    private const float ZoomFactor = 1.5f; // SCREENSHOTVIEWER_ZOOM_FACTOR

    private readonly QcNameList _list;
    private readonly QcScreenshotImage _image = new();
    private Button _slideShowButton = null!;
    private string _title = "";
    private string _current = "";
    private double _nextSlide; // the list's newSlideShowScreenshotTime: 0 = no slide show
    private static float _musicIndexBackup;

    public DialogScreenshotViewer(QcNameList list)
    {
        _list = list;
    }

    /// <summary>The viewer on the first screenshot the Screenshots tab would list (for a capture).</summary>
    public static DialogScreenshotViewer OnFirstScreenshot()
    {
        var list = new QcNameList(skinColor: false);
        list.SetNames(DialogMediaDemo.Scan("screenshots/", new[] { ".jpg", ".tga", ".png" }, null, "COLOR_SCREENSHOTLIST_SUBDIR"));
        return new DialogScreenshotViewer(list);
    }

    public string QcTitle => _title;
    public float QcIntendedWidth => 1f;
    public float QcRows => 25;

    private static double Now => Time.GetTicksMsec() / 1000.0;

    protected override void BuildUi()
    {
        Name = "DialogScreenshotViewer"; // QC XonoticScreenshotViewerDialog

        var me = new QcGrid(QcRows, 4);
        me.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(me);

        float c = me.Columns;
        me.TR();
            me.TD(me.Rows - 1, c, _image);
        me.GotoRC(me.Rows - 1, 0);
            me.TDempty(1f / 20 * c);
            me.TD(1, 1f / 20 * c, Qc.Button("-", () => _image.SetZoom(-1f / ZoomFactor, false)));
            me.TD(1, 1f / 20 * c, Qc.Button("+", () => _image.SetZoom(-ZoomFactor, false)));
            me.TD(1, 2f / 20 * c, Qc.Button("Reset", () => _image.SetZoom(0, false)));

            me.TDempty(2f / 20 * c);
            me.TD(1, 3f / 20 * c, Qc.Button("Previous", () => GoScreenshot(-1)));
            me.TD(1, 3f / 20 * c, Qc.Button("Next", () => GoScreenshot(+1)));

            me.TDempty(2f / 20 * c);
            _slideShowButton = me.TD(1, 4f / 20 * c,
                QcLook.Button(new Button { Text = Localization.Tr("Slide show") }, forcePressed: () => _nextSlide != 0));
            _slideShowButton.Pressed += ToggleSlideShow;

        GoScreenshot(0);
    }

    /// <summary>XonoticScreenshotList_goScreenshot: select the list's row <paramref name="d"/> away and show it.</summary>
    private void GoScreenshot(int d)
    {
        if (_list.Names.Count == 0) return;
        _list.SetSelected(Math.Clamp(_list.SelectedItem + d, 0, _list.Names.Count - 1));
        if (_nextSlide != 0) _nextSlide = Now + 3; // setSelected restarts a running slide show
        LoadScreenshot("screenshots/" + MenuColorCodes.Strip(_list.Names[_list.SelectedItem]).TrimStart('/'));
    }

    /// <summary>XonoticScreenshotViewerDialog_loadScreenshot.</summary>
    private void LoadScreenshot(string path)
    {
        // disable music as it can lag depending on image loading time
        if (MenuState.Cvars.GetFloat("menu_screenshotviewer_enablemusic") == 0
            && MenuState.Cvars.GetFloat("music_playlist_index") != 999)
        {
            float index = MenuState.Cvars.GetFloat("music_playlist_index");
            if (index != -1)
            {
                _musicIndexBackup = index;
                MenuState.Cvars.Set("music_playlist_sampleposition0", "0");
                MenuState.Cvars.Set("music_playlist_index", "999");
            }
            else
                MenuCommand.Run("cd pause");
        }
        if (_current == path) return;
        _current = path;
        _image.Load(MenuSkin.Image(path));
        _title = path["screenshots/".Length..]; // strip "/screenshots/"
    }

    private void ToggleSlideShow() => _nextSlide = _nextSlide != 0 ? 0 : Now + 3;

    public override void _Process(double delta)
    {
        // XonoticScreenshotList_draw: the slide show's next picture, back to the first after the last.
        if (_nextSlide != 0 && Now > _nextSlide && _list.Names.Count > 0)
        {
            if (_list.SelectedItem == _list.Names.Count - 1)
            {
                _list.SetSelected(0);
                GoScreenshot(0);
            }
            else
                GoScreenshot(+1);
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!IsVisibleInTree()) return;
        switch (e)
        {
            case InputEventKey { Pressed: true, Keycode: Key.Left or Key.Kp4 }:
                GoScreenshot(-1);
                break;
            case InputEventKey { Pressed: true, Keycode: Key.Right or Key.Kp6 }:
                GoScreenshot(+1);
                break;
            case InputEventKey { Pressed: true, Keycode: Key.Enter or Key.KpEnter or Key.Space, CtrlPressed: true }:
                ToggleSlideShow();
                break;
            case InputEventKey { Pressed: true, Unicode: '+' }:
                _image.SetZoom(-ZoomFactor, false);
                break;
            case InputEventKey { Pressed: true, Unicode: '-' }:
                _image.SetZoom(-1f / ZoomFactor, false);
                break;
            default:
                return;
        }
        AcceptEvent();
    }

    public override void _ExitTree()
    {
        // XonoticScreenshotViewerDialog_close: the music back, the slide show stopped.
        if (MenuState.Cvars.GetFloat("menu_screenshotviewer_enablemusic") == 0)
        {
            if (MenuState.Cvars.GetFloat("music_playlist_index") == 999)
                MenuState.Cvars.Set("music_playlist_index", _musicIndexBackup.ToString(System.Globalization.CultureInfo.InvariantCulture));
            else
                MenuCommand.Run("cd resume");
        }
        _nextSlide = 0;
        base._ExitTree();
    }
}

/// <summary>
/// xonotic/screenshotimage.qc on item/image.qc with forcedAspect -2: the picture at its real size (a picture pixel
/// is a menu pixel) zoomed by <c>zoomFactor</c>, starting at the zoom that fits it in the box (<c>zoomBox</c>), its
/// <c>zoomOffset</c> point at the box's centre and clipped to the box; dragged to pan, the wheel zooming at the
/// pointer. A zoom change shows the zoom as a percentage in the bottom-left corner for a second, fading out over the
/// next.
/// </summary>
public partial class QcScreenshotImage : Control
{
    private Texture2D? _texture;
    private float _zoomFactor = 1, _zoomBox = -1, _zoomMax;
    private Vector2 _zoomOffset = new(0.5f, 0.5f), _imgSize, _imgOrigin;
    private Vector2 _startZoomOffset, _startCoords;
    private double _zoomTime = -100;

    public QcScreenshotImage()
    {
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Stop;
        Resized += UpdateAspect;
    }

    private static double Now => Time.GetTicksMsec() / 1000.0;

    /// <summary>XonoticScreenshotImage_load: initZoom, then setZoom(0).</summary>
    public void Load(Texture2D? texture)
    {
        _texture = texture;
        _zoomOffset = new Vector2(0.5f, 0.5f);
        _zoomFactor = 1;
        _zoomBox = -1; // forcedAspect -2: worked out at the next updateAspect
        SetZoom(0, false);
    }

    /// <summary>Image_setZoom: a negative <paramref name="newZoom"/> multiplies (snapping to the real size or the box
    /// on the way), 0 resets to the box, a positive one is set.</summary>
    public void SetZoom(float newZoom, bool atMousePosition)
    {
        float prev = _zoomFactor;
        if (newZoom < 0)
        {
            _zoomFactor *= -newZoom;
            bool realSizeInTheMiddle = (prev - 1) * (_zoomFactor - 1) < 0;
            bool boxSizeInTheMiddle = _zoomBox > 0 && (prev - _zoomBox) * (_zoomFactor - _zoomBox) < 0;
            if (realSizeInTheMiddle && boxSizeInTheMiddle)
                _zoomFactor = prev < _zoomFactor ? Math.Min(1, _zoomBox) : Math.Max(1, _zoomBox);
            else if (realSizeInTheMiddle)
                _zoomFactor = 1;
            else if (boxSizeInTheMiddle)
                _zoomFactor = _zoomBox;
        }
        else if (newZoom == 0)
            _zoomFactor = _zoomBox > 0 ? _zoomBox : 1;
        else
            _zoomFactor = newZoom;
        _zoomFactor = Math.Clamp(_zoomFactor, 1f / 16, 16);
        if (_zoomMax > 0 && _zoomFactor > _zoomMax) _zoomFactor = _zoomMax;
        if (prev != _zoomFactor)
        {
            _zoomTime = Now;
            if (atMousePosition && _imgSize.X > 0 && _imgSize.Y > 0)
                _zoomOffset = _startZoomOffset + (_startCoords - new Vector2(0.5f, 0.5f)) / _imgSize;
        }
        UpdateAspect();
    }

    /// <summary>Image_updateAspect, in fractions of the box.</summary>
    private void UpdateAspect()
    {
        if (Size.X <= 0 || Size.Y <= 0) return;
        // draw_PictureSize: the picture's pixels as menu pixels.
        Vector2 sz = _texture is not null ? _texture.GetSize() * MenuMetrics.DesignUnit : Vector2.Zero;
        if (sz.X <= 0 || sz.Y <= 0) sz = Size;
        float asp = sz.X / sz.Y;
        _imgSize = sz / Size;
        if (_zoomBox < 0 && (_imgSize.X > 1 || _imgSize.Y > 1))
        {
            _zoomBox = Size.X > asp * Size.Y ? Size.Y * asp / Size.X / _imgSize.X : Size.X / (asp * Size.Y) / _imgSize.Y;
            _zoomFactor = _zoomBox;
        }
        if (_zoomMax > 0 && _zoomFactor > _zoomMax) _zoomFactor = _zoomMax;
        if (_zoomFactor != 0) _imgSize *= _zoomFactor;
        if (_imgSize.X > 1 || _imgSize.Y > 1)
            _zoomOffset = new Vector2(Math.Clamp(_zoomOffset.X, 0, 1), Math.Clamp(_zoomOffset.Y, 0, 1)); // zoomSnapToTheBox false
        else
            _zoomOffset = new Vector2(0.5f, 0.5f);
        _imgOrigin = new Vector2(0.5f, 0.5f) - _zoomOffset * _imgSize;
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp } wu:
                SetStart(wu.Position);
                SetZoom(-1.5f, true);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown } wd:
                SetStart(wd.Position);
                SetZoom(-1f / 1.5f, true);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb:
                SetStart(mb.Position); // mousePress: drag_setStartPos
                break;
            case InputEventMouseMotion mm when (mm.ButtonMask & MouseButtonMask.Left) != 0:
                // Image_drag
                if (_imgSize.X > 1 || _imgSize.Y > 1)
                {
                    _zoomOffset = _startZoomOffset + (_startCoords - mm.Position / Size) / _imgSize;
                    UpdateAspect();
                }
                break;
            case InputEventMouseMotion mm:
                SetStart(mm.Position); // mouseMove: drag_setStartPos
                return;
            default:
                return;
        }
        AcceptEvent();
    }

    private void SetStart(Vector2 position)
    {
        _startZoomOffset = _zoomOffset;
        _startCoords = position / Size;
    }

    public override void _Process(double delta)
    {
        if (Now < _zoomTime + 2) QueueRedraw(); // the zoom readout fading
    }

    public override void _Draw()
    {
        if (_texture is null) return;
        DrawTextureRect(_texture, new Rect2(_imgOrigin * Size, _imgSize * Size), false);
        double since = Now - _zoomTime;
        if (since < 2) // 1 seconds at full alpha, 1 second fading out
        {
            float z = _zoomFactor;
            string zoom = QcFormat.Decimals(z * 100f, z == MathF.Floor(z) ? 0 : 2) + "%"; // ftos_decimals_percentage
            float alpha = (float)Math.Min(1, 2 - since);
            QcDraw.Text(this, new Vector2(0.05f, 0.95f) * Size, zoom, MenuSkin.TextLift(new Color(1, 1, 1, alpha)));
        }
    }
}
