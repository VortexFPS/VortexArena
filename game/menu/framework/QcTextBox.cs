using System.Collections.Generic;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// xonotic/textbox.qc <c>XonoticTextBox</c>: a read-only text in a list box, one wrapped line per row. setText splits
/// it at newlines (the two characters <c>\n</c> with <see cref="EscapedNewLines"/>) and wraps each part to the row
/// (getWrappedLine, measured as resizeNotify measures, <see cref="QcDraw.TextWidthAtResize"/>), plus one empty row
/// at the end. A row is drawn centred with <see cref="Align"/> 0.5, else from the left; with <see cref="AllowColors"/>
/// in white, its colour codes drawn, else in SKINCOLOR_TEXT at SKINALPHA_TEXT. No selection, no hover fill.
/// </summary>
public partial class QcTextBox : MenuListBox
{
    private readonly List<string> _lines = new();
    private string _text = "";

    public QcTextBox()
    {
        ItemHeight = MenuMetrics.FontNormalU;
        SelectionDoesntMatter = true;
        Resized += Rewrap;
    }

    public float Align { get; set; }
    public bool AllowColors { get; set; }
    public bool EscapedNewLines { get; set; }

    protected override bool ShowSelection => false;
    protected override bool DrawsOwnHighlight => true;

    public string Text
    {
        get => _text;
        set { _text = value ?? ""; Rewrap(); }
    }

    private void Rewrap()
    {
        _lines.Clear();
        float width = ContentWidth;
        if (width > 0f)
        {
            var itemSize = new Vector2(width, ItemHeight);
            foreach (string part in _text.Split(EscapedNewLines ? @"\n" : "\n"))
            {
                string? remaining = part;
                while (remaining is not null)
                    _lines.Add(QcDraw.WrappedLine(ref remaining, width, AllowColors, t => QcDraw.TextWidthAtResize(t, itemSize)));
            }
        }
        ItemCount = _lines.Count + 1;
        QueueRedraw();
    }

    protected override void DrawItem(int index, Rect2 rect, bool isSelected, bool isFocused)
    {
        if (index < 0 || index >= _lines.Count) return;
        string s = _lines[index];
        float ch = MenuMetrics.FontNormalU, y = 0.5f * (rect.Size.Y - ch);
        if (AllowColors)
        {
            float x = Align == 0.5f ? 0.5f * (rect.Size.X - QcDraw.TextWidthColors(s)) : 0f;
            QcDraw.ColoredText(this, rect.Position + new Vector2(x, y), s, Colors.White);
        }
        else
        {
            float x = Align == 0.5f ? 0.5f * (rect.Size.X - QcDraw.TextWidth(s)) : 0f;
            QcDraw.Text(this, rect.Position + new Vector2(x, y), s, MenuSkin.Text);
        }
    }
}
