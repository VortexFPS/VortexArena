// Port of Base/darkplaces/clvm_cmds.c VM_drawline, VM_iscachedpic, VM_precache_pic, VM_freepic,
// VM_drawcharacter, VM_drawstring, VM_drawcolorcodedstring, VM_stringwidth, VM_findfont, VM_loadfont,
// VM_drawpic, VM_drawrotpic, VM_drawsubpic, VM_drawfill, VM_drawsetcliparea, VM_drawresetcliparea,
// VM_getimagesize, with getdrawfont / getdrawfontscale.
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Csqc;

/// <summary>
/// The 2D drawing builtins. In DarkPlaces these are one set of C functions that appears in two builtin
/// tables under different numbers: the client program's (vm_cl_builtins[], #315-#329, #356, #357) and
/// the menu program's (vm_m_builtins[], #451-#470, #356, #357). This is that one set; the client and
/// the menu host each register its members under their own table's numbers.
///
/// Each builtin checks its arguments as the C does and hands the call to an <see cref="ILegacyDraw"/>.
/// The font a text call uses comes from the program's <c>drawfont</c> and <c>drawfontscale</c> globals,
/// read at the moment of the call.
/// </summary>
public sealed class LegacyDrawBuiltins
{
    private const int DrawFlagCount = 5;      // DRAWFLAG_NUMFLAGS

    private readonly QcVm _vm;
    private readonly ILegacyDraw _draw;
    private readonly IQcHost _services;
    private readonly int _drawFont, _drawFontScale;

    /// <param name="drawFontOffset">Offset of the program's <c>drawfont</c> global, or -1 if it has none.</param>
    /// <param name="drawFontScaleOffset">Offset of its <c>drawfontscale</c> global, or -1.</param>
    public LegacyDrawBuiltins(QcVm vm, ILegacyDraw draw, IQcHost services, int drawFontOffset, int drawFontScaleOffset)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _draw = draw ?? throw new ArgumentNullException(nameof(draw));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _drawFont = drawFontOffset;
        _drawFontScale = drawFontScaleOffset;
    }

    /// <summary>prog->polygonbegin_guess2d = true: called by every builtin that draws in 2D, so a
    /// following R_BeginPolygon without its is-2D argument can guess. Null where nothing guesses.</summary>
    public Action? Drew2D { get; init; }

    // ---- helpers -----------------------------------------------------------------------------------

    private QcRuntimeException Fault(string message) => new($"{_vm.Name}: {message}");

    private void Warning(string message) => _services.Warning($"{_vm.Name} VM warning: {message}");

    private void Parms(int count, string name)
    {
        if (_vm.ArgCount != count) throw Fault($"{name} wrong parameter count {_vm.ArgCount} ({count} expected ) !");
    }

    private void Parms(int min, int max, string name)
    {
        if (_vm.ArgCount < min || _vm.ArgCount > max)
            throw Fault($"{name} wrong parameter count {_vm.ArgCount} ({min} to {max} expected ) !");
    }

    // VM_CheckEmptyString.
    private void CheckEmptyString(string s)
    {
        if (s.Length == 0 || s[0] is ' ' or '\t' or '\r' or '\n') throw Fault("Bad string");
    }

    private static int Int(float value) => QcVm.FloatToInt(value);

    private int ArgInt(int index) => QcVm.FloatToInt(_vm.ArgFloat(index));

    private int DrawFont
    {
        get
        {
            // getdrawfont: an out-of-range slot is FONT_DEFAULT.
            int font = _drawFont >= 0 ? Int(_vm.GlobalFloat(_drawFont)) : 0;
            return font is >= 0 and < 256 ? font : 0;
        }
    }

    private QcVector DrawFontScale
    {
        get
        {
            QcVector scale = _drawFontScale >= 0 ? _vm.GlobalVector(_drawFontScale) : default;
            return scale.X * scale.X + scale.Y * scale.Y + scale.Z * scale.Z > 0 ? new QcVector(scale.X, scale.Y, 0) : new QcVector(1, 1, 0);
        }
    }

    // Each drawing builtin validates its flag and scale the same way and returns the same codes.
    private bool CheckDraw(string name, int flag, QcVector? scale)
    {
        if (flag < 0 || flag >= DrawFlagCount)
        {
            _vm.ReturnFloat(-2);
            Warning($"{name}: wrong DRAWFLAG {flag} !\n");
            return false;
        }
        if (scale is { } s && (s.X == 0 || s.Y == 0))
        {
            _vm.ReturnFloat(-3);
            Warning($"{name}: scale {(s.X == 0 ? (s.Y == 0 ? "x and y" : "x") : "y")} is null !\n");
            return false;
        }
        return true;
    }

    // ---- the builtins ------------------------------------------------------------------------------

    // void(float width, vector pos1, vector pos2, vector rgb, float alpha, float flags) drawline
    public void DrawLine(QcVm vm)
    {
        Parms(6, "VM_drawline");
        Drew2D?.Invoke();
        _draw.Line(vm.ArgFloat(0), vm.ArgVector(1), vm.ArgVector(2), vm.ArgVector(3), vm.ArgFloat(4), ArgInt(5) & 0xFF);
    }

    // float(string name) iscachedpic: DarkPlaces always answers false.
    public void IsCachedPic(QcVm vm)
    {
        Parms(1, "VM_iscachedpic");
        vm.ReturnFloat(0);
    }

    // string(string name[, float flags]) precache_pic: its argument, or the null string if the
    // picture cannot be loaded.
    public void PrecachePic(QcVm vm)
    {
        Parms(1, 2, "VM_precache_pic");
        string name = vm.ArgString(0);
        vm.ReturnInt(vm.ArgInt(0));
        CheckEmptyString(name);
        if (!_draw.PictureExists(name)) vm.ReturnInt(0);
    }

    // vector(string picname) draw_getimagesize
    public void GetImageSize(QcVm vm)
    {
        Parms(1, "VM_getimagesize");
        string name = vm.ArgString(0);
        CheckEmptyString(name);
        QcVector size = _draw.ImageSize(name);
        vm.ReturnVector(new QcVector(size.X, size.Y, 0));
    }

    // void(string name) freepic
    public void FreePic(QcVm vm)
    {
        Parms(1, "VM_freepic");
        string name = vm.ArgString(0);
        CheckEmptyString(name);
        _draw.FreePicture(name);
    }

    private void DrawText(QcVector position, string text, QcVector scale, QcVector color, float alpha, int flag, bool ignoreColorCodes, bool returnColor)
    {
        LegacyText draw = new()
        {
            Position = position, Text = text, Scale = scale, Color = color, Alpha = alpha, Flags = flag,
            Font = DrawFont, FontScale = DrawFontScale, IgnoreColorCodes = ignoreColorCodes,
        };
        QcVector last = _draw.Text(draw);
        if (returnColor) _vm.ReturnVector(last);
        else _vm.ReturnFloat(1);
    }

    // float(vector position, float character, vector scale, vector rgb, float alpha, float flag) drawcharacter
    public void DrawCharacter(QcVm vm)
    {
        Parms(6, "VM_drawcharacter");
        Drew2D?.Invoke();
        // "(char) PRVM_G_FLOAT": the low byte.
        int character = ArgInt(1) & 0xFF;
        if (character == 0)
        {
            vm.ReturnFloat(-1);
            Warning("VM_drawcharacter: null character passed!\n");
            return;
        }
        QcVector scale = vm.ArgVector(2);
        int flag = ArgInt(5);
        if (!CheckDraw("VM_drawcharacter", flag, scale)) return;
        DrawText(vm.ArgVector(0), ((char)character).ToString(), scale, vm.ArgVector(3), vm.ArgFloat(4), flag, true, false);
    }

    // float(vector position, string text, vector scale, vector rgb, float alpha[, float flag]) drawstring
    public void DrawString(QcVm vm)
    {
        Parms(5, 6, "VM_drawstring");
        Drew2D?.Invoke();
        QcVector scale = vm.ArgVector(2);
        int flag = vm.ArgCount >= 6 ? ArgInt(5) : 0;
        if (!CheckDraw("VM_drawstring", flag, scale)) return;
        DrawText(vm.ArgVector(0), vm.ArgString(1), scale, vm.ArgVector(3), vm.ArgFloat(4), flag, true, false);
    }

    // drawcolorcodedstring(vector position, string text, vector scale, [vector rgb,] float alpha, float flag).
    // With the colour argument it returns the colour in effect at the end of the text.
    public void DrawColorCodedString(QcVm vm)
    {
        Parms(5, 6, "VM_drawcolorcodedstring");
        Drew2D?.Invoke();
        bool full = vm.ArgCount == 6;
        QcVector scale = vm.ArgVector(2);
        QcVector color = full ? vm.ArgVector(3) : new QcVector(1, 1, 1);
        float alpha = vm.ArgFloat(full ? 4 : 3);
        int flag = ArgInt(full ? 5 : 4);
        if (!CheckDraw("VM_drawcolorcodedstring", flag, scale)) return;
        DrawText(vm.ArgVector(0), vm.ArgString(1), scale, color, alpha, flag, false, full);
    }

    // float(string text, float allowColorCodes[, vector size]) stringwidth
    public void StringWidth(QcVm vm)
    {
        Parms(2, 3, "VM_stringwidth");
        QcVector fontScale = DrawFontScale;
        QcVector size;
        float multiplier;
        if (vm.ArgCount == 3)
        {
            size = vm.ArgVector(2);
            multiplier = 1;
        }
        else
        {
            // The two-argument form measures in character cells of an 8-pixel font.
            size = new QcVector(8, 8, 0);
            multiplier = 0.125f;
            if (fontScale.X >= 0.9f && fontScale.X <= 1.1f)
            {
                multiplier *= 2;
                fontScale.X /= 2;
                fontScale.Y /= 2;
            }
        }
        bool colors = ArgInt(1) != 0;
        vm.ReturnFloat(_draw.StringWidth(vm.ArgString(0), !colors, new QcVector(size.X, size.Y, 0), DrawFont, fontScale) * multiplier);
    }

    // float(string fontname) findfont
    public void FindFont(QcVm vm)
    {
        Parms(1, "VM_findfont");
        vm.ReturnFloat(_draw.FindFont(vm.ArgString(0)));
    }

    // float(string fontname, string fontmaps, string sizes[, float slot, float fix_scale, float fix_voffset]) loadfont
    public void LoadFont(QcVm vm)
    {
        Parms(3, 6, "VM_loadfont");
        string name = vm.ArgString(0), files = vm.ArgString(1), sizes = vm.ArgString(2);
        if (name.Length == 0) name = "default";
        if (files.Length == 0) files = "gfx/conchars";
        if (sizes.Length == 0) sizes = "10";
        int slot = -1;
        if (vm.ArgCount >= 4)
        {
            slot = ArgInt(3);
            if (slot < 0 || slot >= 256) slot = -1;
        }
        float scale = 1, verticalOffset = 0;
        if (vm.ArgCount >= 5)
        {
            scale = vm.ArgFloat(4);
            if (!(scale > 0)) scale = 1;
        }
        if (vm.ArgCount >= 6) verticalOffset = vm.ArgFloat(5);
        vm.ReturnFloat(_draw.LoadFont(name, files, sizes, slot, scale, verticalOffset));
    }

    // float(vector position, string pic, vector size, vector rgb, float alpha[, float flag]) drawpic
    public void DrawPic(QcVm vm)
    {
        Parms(5, 6, "VM_drawpic");
        Drew2D?.Invoke();
        string name = vm.ArgString(1);
        CheckEmptyString(name);
        int flag = vm.ArgCount >= 6 ? ArgInt(5) : 0;
        if (!CheckDraw("VM_drawpic", flag, null)) return;
        _draw.Picture(new LegacyPicture
        {
            Name = name, Position = vm.ArgVector(0), Size = vm.ArgVector(2), Color = vm.ArgVector(3), Alpha = vm.ArgFloat(4), Flags = flag,
            SourceSize = new QcVector(1, 1, 0),
        });
        vm.ReturnFloat(1);
    }

    // float(vector position, string pic, vector size, vector org, float angle, vector rgb, float alpha, float flag) drawrotpic
    public void DrawRotPic(QcVm vm)
    {
        Parms(8, "VM_drawrotpic");
        Drew2D?.Invoke();
        string name = vm.ArgString(1);
        CheckEmptyString(name);
        int flag = ArgInt(7);
        if (!CheckDraw("VM_drawrotpic", flag, null)) return;
        _draw.Picture(new LegacyPicture
        {
            Name = name, Position = vm.ArgVector(0), Size = vm.ArgVector(2), RotationOrigin = vm.ArgVector(3), Angle = vm.ArgFloat(4),
            Color = vm.ArgVector(5), Alpha = vm.ArgFloat(6), Flags = flag, SourceSize = new QcVector(1, 1, 0),
        });
        vm.ReturnFloat(1);
    }

    // float(vector position, vector size, string pic, vector srcPos, vector srcSize, vector rgb, float alpha, float flag) drawsubpic
    public void DrawSubPic(QcVm vm)
    {
        Parms(8, "VM_drawsubpic");
        Drew2D?.Invoke();
        string name = vm.ArgString(2);
        CheckEmptyString(name);
        int flag = ArgInt(7);
        if (!CheckDraw("VM_drawsubpic", flag, null)) return;
        _draw.Picture(new LegacyPicture
        {
            Name = name, Position = vm.ArgVector(0), Size = vm.ArgVector(1), SourcePosition = vm.ArgVector(3), SourceSize = vm.ArgVector(4),
            Color = vm.ArgVector(5), Alpha = vm.ArgFloat(6), Flags = flag,
        });
        vm.ReturnFloat(1);
    }

    // float(vector position, vector size, vector rgb, float alpha, float flag) drawfill
    public void DrawFill(QcVm vm)
    {
        Parms(5, "VM_drawfill");
        Drew2D?.Invoke();
        int flag = ArgInt(4);
        if (!CheckDraw("VM_drawfill", flag, null)) return;
        _draw.Fill(vm.ArgVector(0), vm.ArgVector(1), vm.ArgVector(2), vm.ArgFloat(3), flag);
        vm.ReturnFloat(1);
    }

    // void(float x, float y, float width, float height) drawsetcliparea, clamped to the virtual screen.
    public void DrawSetClipArea(QcVm vm)
    {
        Parms(4, "VM_drawsetcliparea");
        Drew2D?.Invoke();
        float conWidth = Int(_services.CvarExists("vid_conwidth") ? _services.CvarFloat("vid_conwidth") : 640);
        float conHeight = Int(_services.CvarExists("vid_conheight") ? _services.CvarFloat("vid_conheight") : 480);
        float x = Bound(0, vm.ArgFloat(0), conWidth);
        float y = Bound(0, vm.ArgFloat(1), conHeight);
        float w = Bound(0, vm.ArgFloat(2) + vm.ArgFloat(0) - x, conWidth - x);
        float h = Bound(0, vm.ArgFloat(3) + vm.ArgFloat(1) - y, conHeight - y);
        _draw.SetClipArea(x, y, w, h);

        static float Bound(float min, float value, float max) => value >= min ? (value < max ? value : max) : min;
    }

    // void() drawresetcliparea
    public void DrawResetClipArea(QcVm vm)
    {
        Parms(0, "VM_drawresetcliparea");
        Drew2D?.Invoke();
        _draw.ResetClipArea();
    }
}
