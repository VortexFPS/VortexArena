using System;
using System.Collections.Generic;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// A XonoticTextLabel with item/label.qc's <c>allowCut</c> or <c>allowWrap</c> set, which <see cref="QcLabel"/>
/// does not do: a text too wide for the label is left at full size and cut with "..." (allowCut), or broken
/// into lines at the label's width and the block centred vertically (allowWrap) — instead of being condensed.
/// Both start at the left margin once the text no longer fits (the QC's "FIXME incompatible with align != 0").
/// </summary>
public partial class QcTextLabel : QcLabel
{
    public QcTextLabel(string text = "", float align = 0f) : base(text, align) { }

    /// <summary>QC <c>allowCut</c>: draw_TextShortenToWidth instead of condensing.</summary>
    public bool AllowCut { get; set; }

    /// <summary>QC <c>allowWrap</c>: getWrappedLine over the label's width, lines one font height apart.</summary>
    public bool AllowWrap { get; set; }

    public override void _Draw()
    {
        if (!AllowCut && !AllowWrap)
        {
            base._Draw();
            return;
        }
        Color color = Color ?? MenuSkin.Text;
        Color c = Disabled ? MenuSkin.Fade(color, MenuSkin.DisabledAlpha) : color;
        float ch = MenuMetrics.U(FontSize);
        float keepLeft = MarginLeftChars * ch, keepRight = MarginRightChars * ch;
        float avail = Size.X - keepLeft - keepRight;
        string t = Text;

        // Label_recalcPositionWithText: aligned while it fits, else from the left margin at full size.
        float used = QcDraw.TextWidth(t, FontSize, Bold);
        float x = used <= avail ? Align * (avail - used) + keepLeft : keepLeft;

        if (AllowCut)
        {
            float y = 0.5f * (Size.Y - ch);
            QcDraw.Text(this, new Vector2(x, y), QcDraw.ShortenToWidth(t, avail, FontSize, Bold), c, FontSize, Bold);
            return;
        }

        var lines = new List<string>();
        string? remaining = t;
        while (!string.IsNullOrEmpty(remaining))
            lines.Add(QcDraw.WrappedLine(ref remaining, avail, false, s => QcDraw.TextWidth(s, FontSize, Bold)));
        float top = 0.5f * (Size.Y - lines.Count * ch);
        foreach (string line in lines)
        {
            QcDraw.Text(this, new Vector2(x, top), line, c, FontSize, Bold);
            top += ch; // allowWrap_spacing 0
        }
    }
}

/// <summary>
/// makeXonoticImage(src, aspect) (item/image.qc without zoom): the picture fitted into the item at
/// <see cref="ForcedAspect"/> (0 = stretched over the whole item, negative = the picture's own aspect) and centred.
/// Nothing is drawn while there is no picture.
/// </summary>
public partial class QcImage : Control
{
    private Texture2D? _texture;

    public QcImage(Texture2D? texture = null, float forcedAspect = -1f)
    {
        _texture = texture;
        ForcedAspect = forcedAspect;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public Texture2D? Texture
    {
        get => _texture;
        set { _texture = value; QueueRedraw(); }
    }

    /// <summary>QC <c>forcedAspect</c>.</summary>
    public float ForcedAspect { get; set; }

    /// <summary>The picture's tint (Image <c>color</c>, '1 1 1').</summary>
    public Color Tint { get; set; } = Colors.White;

    public override void _Draw()
    {
        if (_texture is null || Size.X <= 0f || Size.Y <= 0f) return;
        Rect2 r = new(Vector2.Zero, Size);
        if (ForcedAspect != 0f)
        {
            float asp = ForcedAspect > 0f ? ForcedAspect : (float)_texture.GetWidth() / Math.Max(1, _texture.GetHeight());
            // Image_updateAspect: shrink the axis that is too large, centred along it.
            Vector2 s = Size.X > asp * Size.Y ? new Vector2(Size.Y * asp, Size.Y) : new Vector2(Size.X, Size.X / asp);
            r = new Rect2(0.5f * (Size - s), s);
        }
        DrawTextureRect(_texture, r, false, Tint);
    }

    public override Vector2 _GetMinimumSize() => Vector2.Zero;
}

/// <summary>
/// xonotic/textbox.qc, "slightly hacky multiline textbox with scrollbar": a list of one-row items, the text split
/// at its newlines and each paragraph wrapped (getWrappedLine) at the row width, plus one empty row at the end
/// (<c>nItems = line + 1</c>). With <see cref="AllowColors"/> the colour codes are drawn as colours from white at
/// full alpha; otherwise the text is SKINCOLOR_TEXT at SKINALPHA_TEXT. Nothing is ever highlighted
/// (selectionDoesntMatter).
/// </summary>
public partial class QcTextBox : MenuListBox
{
    private string _text = "";
    private string[] _lines = Array.Empty<string>();
    private float _wrappedFor = -1f;

    public QcTextBox()
    {
        ItemHeight = MenuMetrics.FontNormalU; // rowsPerItem 1
        SelectionDoesntMatter = true;
        Resized += Wrap;
    }

    /// <summary>QC <c>allowColors</c>.</summary>
    public bool AllowColors { get; set; }

    /// <summary>QC <c>align</c>: 0 = left, 0.5 = each line centred.</summary>
    public float Align { get; set; }

    protected override bool DrawsOwnHighlight => true;

    /// <summary>XonoticTextBox_setText.</summary>
    public string Text
    {
        get => _text;
        set
        {
            _text = value ?? "";
            _wrappedFor = -1f;
            Wrap();
        }
    }

    private void Wrap()
    {
        float width = ContentWidth; // 1 - controlWidth
        if (width <= 0f || _wrappedFor == width) return;
        _wrappedFor = width;
        var item = new Vector2(width, ItemHeight);
        var lines = new List<string>();
        // tokenizebyseparator: no token after the final separator, none at all for "".
        string text = _text.EndsWith('\n') ? _text[..^1] : _text;
        if (text.Length > 0)
            foreach (string paragraph in text.Split('\n'))
            {
                string? remaining = paragraph;
                while (remaining is not null)
                    lines.Add(QcDraw.WrappedLine(ref remaining, width, AllowColors, t => QcDraw.TextWidthAtResize(t, item)));
            }
        _lines = lines.ToArray();
        ItemCount = _lines.Length + 1;
        QueueRedraw();
    }

    protected override void DrawItem(int index, Rect2 rect, bool isSelected, bool isFocused)
    {
        if (index < 0 || index >= _lines.Length) return;
        string s = _lines[index];
        if (Align == 0.5f)
        {
            // draw_CenterText(0.5 * eX, s, realFontSize, color, 1, allowColors)
            float w = AllowColors ? QcDraw.TextWidthColors(s) : QcDraw.TextWidth(s);
            var at = rect.Position + new Vector2(0.5f * rect.Size.X - 0.5f * w, 0f);
            if (AllowColors) QcDraw.ColoredText(this, at, s, Colors.White);
            else QcDraw.Text(this, at, s, new Color(MenuSkin.Text, 1f));
        }
        else if (AllowColors)
            QcDraw.ColoredText(this, rect.Position, s, new Color(1f, 1f, 1f, MenuSkin.Text.A));
        else
            QcDraw.Text(this, rect.Position, s, MenuSkin.Text);
    }
}
