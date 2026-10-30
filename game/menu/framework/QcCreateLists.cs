using System;
using System.Collections.Generic;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// xonotic/gametypelist.qc: two text rows per gametype — the gametype's icon as a square the height of the row,
/// its name half a character after it, and "teamplay" / "free for all" right-aligned, both condensed together
/// when they do not fit the row.
/// </summary>
public partial class QcGametypeList : MenuListBox
{
    public readonly record struct Entry(string NetName, string Name, string Icon, bool TeamPlay);

    private readonly List<Entry> _entries = new();

    public QcGametypeList()
    {
        ItemHeight = 2f * MenuMetrics.FontNormalU; // rowsPerItem 2
    }

    public IReadOnlyList<Entry> Entries => _entries;

    public void SetEntries(IEnumerable<Entry> entries)
    {
        _entries.Clear();
        _entries.AddRange(entries);
        ItemCount = _entries.Count;
        QueueRedraw();
    }

    protected override void DrawItem(int index, Rect2 rect, bool isSelected, bool isFocused)
    {
        if (index < 0 || index >= _entries.Count) return;
        Entry e = _entries[index];
        float h = rect.Size.Y, ch = MenuMetrics.FontNormalU;
        // draw_Picture(columnIconOrigin, icon, columnIconSize x 1, '1 1 1', SKINALPHA_LISTBOX_SELECTED)
        if (MenuSkin.SkinImage(e.Icon) is { } icon)
            DrawTextureRect(icon, new Rect2(rect.Position, new Vector2(h, h)), false, MenuSkin.Fade(Colors.White, MenuSkin.SelectionAlpha));

        string s2 = Localization.Tr(e.TeamPlay ? "teamplay" : "free for all");
        float nameOrigin = h + 0.5f * ch;
        float nameSize = rect.Size.X - h - 1.5f * ch;
        // draw_CondensedFontFactor(s1 + " " + s2, ..., 1): squeeze both if the pair is wider than the row.
        float pair = QcDraw.TextWidth(e.Name + " " + s2);
        float f = pair > rect.Size.X ? rect.Size.X / pair : 1f;
        float y = rect.Position.Y + 0.5f * (h - ch);
        Color alpha = MenuSkin.Fade(Colors.White, MenuSkin.Text.A); // '1 1 1' @ SKINALPHA_TEXT
        QcDraw.Text(this, new Vector2(rect.Position.X + nameOrigin, y), e.Name, alpha, condense: f);
        float w2 = QcDraw.TextWidth(s2) * f;
        QcDraw.Text(this, new Vector2(rect.Position.X + nameOrigin + nameSize - w2, y), s2, MenuSkin.Text, condense: f);
    }
}

/// <summary>
/// xonotic/maplist.qc: four text rows per map — the 4:3 mapshot (with the checkmark in its lower-right quarter
/// when the map is in the selection), the title one character after it and the author right-aligned a line and a
/// half below, all faded when the map is not selected for the rotation. Clicking the picture toggles the map in
/// the selection (<c>clickListBoxItem</c>); double-clicking a row asks for its info.
/// </summary>
public partial class QcMapList : MenuListBox
{
    public sealed record Entry(string Map, string Title, string Author);

    private readonly List<Entry> _entries = new();
    private Func<string, bool> _included = _ => true;

    /// <summary>A click on a map's picture: add it to the selection or take it out.</summary>
    public event Action<string>? ToggleIncludeRequested;

    public QcMapList()
    {
        ItemHeight = 4f * MenuMetrics.FontNormalU; // rowsPerItem 4
    }

    public IReadOnlyList<Entry> Entries => _entries;

    public void SetEntries(IEnumerable<Entry> entries, Func<string, bool> included)
    {
        _entries.Clear();
        _entries.AddRange(entries);
        _included = included;
        ItemCount = _entries.Count;
        QueueRedraw();
    }

    private float PreviewWidth => ItemHeight * 4f / 3f;

    // ListBox_mouseRelease hands every single click to clickListBoxItem: one on the picture toggles the map.
    protected override void OnItemClicked(int index)
    {
        if (index >= 0 && index < _entries.Count && LastReleasePosition.X >= 0f && LastReleasePosition.X <= PreviewWidth)
            ToggleIncludeRequested?.Invoke(_entries[index].Map);
    }

    protected override void DrawItemBackground(int index, Rect2 rect, bool isSelected, bool isFocused)
    {
        if (isSelected || index < 0 || index >= _entries.Count || !_included(_entries[index].Map)) return;
        Color bg = MenuSkin.SkinColor("COLOR_MAPLIST_INCLUDEDBG", 0.03f, 0.25f, 0.49f);
        DrawRect(rect, MenuSkin.Fade(bg, MenuSkin.SkinNumber("ALPHA_MAPLIST_INCLUDEDBG", 0.3f)));
    }

    protected override void DrawItem(int index, Rect2 rect, bool isSelected, bool isFocused)
    {
        if (index < 0 || index >= _entries.Count) return;
        Entry e = _entries[index];
        bool included = _included(e.Map);
        float alpha = included || isSelected
            ? MenuSkin.SkinNumber("ALPHA_MAPLIST_INCLUDEDFG", 1f)
            : MenuSkin.SkinNumber("ALPHA_MAPLIST_NOTINCLUDEDFG", 0.25f);
        float h = rect.Size.Y, ch = MenuMetrics.FontNormalU, pw = PreviewWidth;

        Texture2D? shot = MenuSkin.Image("maps/" + e.Map) ?? MenuSkin.Image("levelshots/" + e.Map) ?? MenuSkin.SkinImage("nopreview_map");
        if (shot is not null)
            DrawTextureRect(shot, new Rect2(rect.Position, new Vector2(pw, h)), false, MenuSkin.Fade(Colors.White, alpha));
        if (included && MenuSkin.SkinImage("checkmark") is { } check)
            DrawTextureRect(check, new Rect2(rect.Position + new Vector2(pw - 0.5f * h, 0.5f * h), new Vector2(0.5f * h, 0.5f * h)), false, Colors.White);

        float nameOrigin = pw + ch, nameSize = rect.Size.X - pw - 2f * ch;
        float y1 = rect.Position.Y + 0.5f * (h - 2.5f * ch), y2 = y1 + 1.5f * ch;
        string title = ShortenToWidth(MenuColorCodes.Strip(e.Title), nameSize);
        string author = ShortenToWidth(MenuColorCodes.Strip(e.Author), nameSize);
        Color titleColor = MenuSkin.TextLift(MenuSkin.SkinColor("COLOR_MAPLIST_TITLE", 0.96f, 0.99f, 1f));
        Color authorColor = MenuSkin.TextLift(MenuSkin.SkinColor("COLOR_MAPLIST_AUTHOR", 0.56f, 0.72f, 0.86f));
        QcDraw.Text(this, new Vector2(rect.Position.X + nameOrigin, y1), title, MenuSkin.Fade(titleColor, alpha));
        QcDraw.Text(this, new Vector2(rect.Position.X + nameOrigin + nameSize - QcDraw.TextWidth(author), y2), author,
            MenuSkin.Fade(authorColor, alpha));
    }

    /// <summary>common/util.qc textShortenToWidth: as much as fits with "..." after it.</summary>
    private static string ShortenToWidth(string text, float width)
    {
        if (QcDraw.TextWidth(text) <= width) return text;
        float room = width - QcDraw.TextWidth("...");
        int n = text.Length;
        while (n > 0 && QcDraw.TextWidth(text[..n]) > room) n--;
        return text[..n] + "...";
    }
}
