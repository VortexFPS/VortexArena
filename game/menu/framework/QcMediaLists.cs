using System;
using System.Collections.Generic;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// xonotic/demolist.qc and screenshotlist.qc: one text row per file — its name a character in, shortened to the row
/// less a character each side, colour codes drawn (a subdirectory's name is coloured). The demo list draws in
/// SKINCOLOR_TEXT, the screenshot list in white; both at SKINALPHA_TEXT.
/// </summary>
public partial class QcNameList : MenuListBox
{
    private readonly List<string> _names = new();
    private readonly bool _skinColor;

    public QcNameList(bool skinColor)
    {
        _skinColor = skinColor;
        ItemHeight = MenuMetrics.FontNormalU; // rowsPerItem 1
    }

    public IReadOnlyList<string> Names => _names;

    /// <summary>The rows after a rescan; the first one selected, as getDemos / getScreenshots leave it.</summary>
    public void SetNames(IEnumerable<string> names)
    {
        _names.Clear();
        _names.AddRange(names);
        ItemCount = _names.Count;
        SetSelected(0);
        QueueRedraw();
    }

    protected override void DrawItem(int index, Rect2 rect, bool isSelected, bool isFocused)
    {
        if (index < 0 || index >= _names.Count) return;
        float ch = MenuMetrics.FontNormalU;
        string s = _names[index];
        if (s.StartsWith('/')) s = s[1..]; // "remove the first forward slash"
        s = QcDraw.ShortenToWidth(s, rect.Size.X - 2f * ch);
        // The colour DrawQ_String starts from (ColoredText lifts it): SKINCOLOR_TEXT or white, at SKINALPHA_TEXT.
        Color color = MenuSkin.Fade(_skinColor ? MenuSkin.SkinColor("COLOR_TEXT", 0.96f, 0.99f, 1f) : Colors.White, MenuSkin.Text.A);
        QcDraw.ColoredText(this, rect.Position + new Vector2(ch, 0.5f * (rect.Size.Y - ch)), s, color);
    }
}

/// <summary>
/// xonotic/soundlist.qc: one row per track in sound/cdtracks — "[C]" centred in a three-character column for the
/// current menu track (<c>menu_cdtrack</c>), "[D]" for its default, the track name after it.
/// </summary>
public partial class QcSoundList : MenuListBox
{
    private readonly List<string> _names = new();

    public QcSoundList() => ItemHeight = MenuMetrics.FontNormalU;

    public IReadOnlyList<string> Names => _names;

    public void SetNames(IEnumerable<string> names)
    {
        _names.Clear();
        _names.AddRange(names);
        ItemCount = _names.Count;
        SetSelected(0);
        QueueRedraw();
    }

    protected override void DrawItem(int index, Rect2 rect, bool isSelected, bool isFocused)
    {
        if (index < 0 || index >= _names.Count) return;
        float ch = MenuMetrics.FontNormalU, y = 0.5f * (rect.Size.Y - ch);
        Color white = MenuSkin.Fade(Colors.White, MenuSkin.Text.A);
        string s = _names[index];
        string mark = s == MenuState.Cvars.GetString("menu_cdtrack") ? "[C]"
            : s == MenuState.Cvars.GetDefault("menu_cdtrack") ? "[D]" : "";
        if (mark.Length > 0) // draw_CenterText in the number column
            QcDraw.Text(this, rect.Position + new Vector2(1.5f * ch - 0.5f * QcDraw.TextWidth(mark), y), mark, white);
        QcDraw.Text(this, rect.Position + new Vector2(3f * ch, y), QcDraw.ShortenToWidth(s, rect.Size.X - 4f * ch), white);
    }
}

/// <summary>
/// xonotic/playlist.qc: one row per track in <c>music_playlist_list0</c> — its 1-based number centred in a
/// three-character column, the play symbol after it on the track playing (blinking while it is paused), the name a
/// character further on.
/// </summary>
public partial class QcPlayList : MenuListBox
{
    private string[] _tracks = Array.Empty<string>();

    public QcPlayList() => ItemHeight = MenuMetrics.FontNormalU;

    public IReadOnlyList<string> Tracks => _tracks;

    // XonoticPlayList_draw: the list is the cvar, tokenized every frame.
    public override void _Process(double delta)
    {
        base._Process(delta);
        string list = MenuState.Cvars.GetString("music_playlist_list0");
        string[] tracks = list.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tracks.Length != _tracks.Length || string.Join(' ', tracks) != string.Join(' ', _tracks))
        {
            _tracks = tracks;
            ItemCount = tracks.Length;
            QueueRedraw();
        }
    }

    private int PlayingTrack
    {
        get
        {
            float index = MenuState.Cvars.GetFloat("music_playlist_index");
            return index is 0 or 999 ? (int)MenuState.Cvars.GetFloat("music_playlist_current0") : -1;
        }
    }

    protected override void DrawItem(int index, Rect2 rect, bool isSelected, bool isFocused)
    {
        if (index < 0 || index >= _tracks.Length) return;
        float ch = MenuMetrics.FontNormalU, y = 0.5f * (rect.Size.Y - ch);
        Color white = MenuSkin.Fade(Colors.White, MenuSkin.Text.A);
        if (index == PlayingTrack)
        {
            float f = MenuState.Cvars.GetFloat("music_playlist_sampleposition0");
            if (f <= 0 || (((int)(Time.GetTicksMsec() / 500)) & 1) != 0)
                QcDraw.Text(this, rect.Position + new Vector2(3f * ch, y), "▶", white);
        }
        string n = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        QcDraw.Text(this, rect.Position + new Vector2(1.5f * ch - 0.5f * QcDraw.TextWidth(n), y), n, white);
        QcDraw.Text(this, rect.Position + new Vector2(4f * ch, y), QcDraw.ShortenToWidth(_tracks[index], rect.Size.X - 5f * ch), white);
    }
}

/// <summary>One row of a guide list: what the topic's DataSource getEntry hands its display callback, and the
/// description (describe()).</summary>
public sealed record GuideEntry(string Name, Texture2D? Icon, Func<string> Describe);

/// <summary>
/// xonotic/guide/topics.qc: three text rows per topic — its icon as a square the height of the row, the name half a
/// character after it, condensed to fit and centred vertically.
/// </summary>
public partial class QcGuideTopicList : MenuListBox
{
    private readonly IReadOnlyList<(string Name, string Icon)> _topics;

    public QcGuideTopicList(IReadOnlyList<(string Name, string Icon)> topics)
    {
        _topics = topics;
        ItemHeight = 3f * MenuMetrics.FontNormalU;
        ItemCount = topics.Count;
    }

    protected override void DrawItem(int index, Rect2 rect, bool isSelected, bool isFocused)
    {
        if (index < 0 || index >= _topics.Count) return;
        (string name, string iconName) = _topics[index];
        float h = rect.Size.Y, ch = MenuMetrics.FontNormalU;
        if (MenuSkin.SkinImage(iconName) is { } icon)
            DrawTextureRect(icon, new Rect2(rect.Position, new Vector2(h, h)), false, MenuSkin.Fade(Colors.White, MenuSkin.SelectionAlpha));
        float nameOrigin = h + 0.5f * ch, nameSize = rect.Size.X - nameOrigin - ch;
        float w = QcDraw.TextWidth(name);
        float f = w > nameSize && w > 0f ? nameSize / w : 1f;
        QcDraw.Text(this, rect.Position + new Vector2(nameOrigin, 0.5f * (h - ch)), name, MenuSkin.Fade(Colors.White, MenuSkin.Text.A), condense: f);
    }
}

/// <summary>
/// xonotic/guide/entries.qc: three text rows per entry — its icon at the picture's own aspect the height of the
/// row (or, with none, a gap of one and a half characters), the name after it wrapped to two lines at most (the
/// second shortened), centred vertically.
/// </summary>
public partial class QcGuideEntryList : MenuListBox
{
    private IReadOnlyList<GuideEntry> _entries = Array.Empty<GuideEntry>();

    public QcGuideEntryList() => ItemHeight = 3f * MenuMetrics.FontNormalU;

    public IReadOnlyList<GuideEntry> Entries => _entries;

    /// <summary>refilter: the entries after a reload, keeping the selected one's name selected when still present,
    /// else the first. The caller shows the selected entry's description afterwards (the QC's onChange).</summary>
    public void SetEntries(IReadOnlyList<GuideEntry> entries, bool sourceChanged)
    {
        string? oldName = !sourceChanged && SelectedItem >= 0 && SelectedItem < _entries.Count ? _entries[SelectedItem].Name : null;
        _entries = entries;
        ItemCount = entries.Count;
        int select = 0;
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].Name == oldName) select = i;
        if (sourceChanged) SetScrollImmediate(0);
        SetSelected(select);
        QueueRedraw();
    }

    protected override void DrawItem(int index, Rect2 rect, bool isSelected, bool isFocused)
    {
        if (index < 0 || index >= _entries.Count) return;
        GuideEntry e = _entries[index];
        float h = rect.Size.Y, ch = MenuMetrics.FontNormalU;
        float x;
        if (e.Icon is { } icon)
        {
            Vector2 sz = icon.GetSize();
            float w = sz.Y > 0f ? h * sz.X / sz.Y : h;
            DrawTextureRect(icon, new Rect2(rect.Position, new Vector2(w, h)), false, MenuSkin.Fade(Colors.White, MenuSkin.SelectionAlpha));
            x = w + 0.5f * ch;
        }
        else
            x = 3f * 0.5f * ch; // rowsPerItem * 0.5 * realFontSize.x

        float maxWidth = rect.Size.X - x - ch;
        float y = 0.5f * (h - ch);
        Color white = MenuSkin.Fade(Colors.White, MenuSkin.Text.A);
        string? remaining = MenuColorCodes.Strip(e.Name);
        string s = QcDraw.WrappedLine(ref remaining, maxWidth);
        if (remaining is not null)
        {
            y -= 0.5f * ch;
            QcDraw.Text(this, rect.Position + new Vector2(x, y), s, white);
            s = QcDraw.ShortenToWidth(remaining, maxWidth);
            y += ch;
        }
        QcDraw.Text(this, rect.Position + new Vector2(x, y), s, white);
    }
}

/// <summary>
/// xonotic/guide/description.qc: the selected entry's describe() text as a list of single text rows, wrapped to the
/// row less a character each side (wrapGuideText: a paragraph line starting "* " becomes a dot point, its wrapped
/// lines and the lines after it indented under it), colour codes drawn, white at full alpha. Nothing is selectable;
/// a new text scrolls back to the top.
/// </summary>
public partial class QcGuideDescription : MenuListBox
{
    private string _description = "";
    private string[] _lines = Array.Empty<string>();
    private float _wrappedFor = -1f;

    public QcGuideDescription()
    {
        ItemHeight = MenuMetrics.FontNormalU;
        SelectionDoesntMatter = true;
        Resized += Wrap;
    }

    protected override bool DrawsOwnHighlight => true; // selectionDoesntMatter: no row is ever lit

    public void SetDescription(string description)
    {
        _description = description ?? "";
        _wrappedFor = -1f;
        Wrap();
    }

    private void Wrap()
    {
        float ch = MenuMetrics.FontNormalU, width = ContentWidth - 2f * ch;
        if (width <= 0f || _wrappedFor == width) return;
        _wrappedFor = width;
        var item = new Vector2(ContentWidth, ItemHeight);
        string wrapped = WrapGuideText(_description, width, t => QcDraw.TextWidthAtResize(t, item));
        // tokenizebyseparator: no token after the final separator.
        _lines = wrapped.EndsWith('\n') ? wrapped[..^1].Split('\n') : wrapped.Split('\n');
        ItemCount = _lines.Length;
        SetSelected(0); // reset scrollbar position
        SetScrollImmediate(0);
        QueueRedraw();
    }

    /// <summary>wrapGuideText: wraps text and makes dot point lists of lines starting with "* ".</summary>
    private static string WrapGuideText(string desc, float maxWidth, Func<string, float> measure)
    {
        var wrapped = new System.Text.StringBuilder();
        bool keepIndenting = false;
        string[] pars = desc.Split('\n');
        for (int i = 0; i < pars.Length; ++i)
        {
            var line = new System.Text.StringBuilder();
            string? remaining = pars[i];
            bool dotpoint = false, indent = false;
            if (remaining.StartsWith("* ", StringComparison.Ordinal))
            {
                dotpoint = true;
                // Middle Dot (U+00B7): as wide as a space, so wrapped lines line up with the text after it.
                remaining = "  · " + remaining[2..];
            }
            else if (keepIndenting)
            {
                dotpoint = true;
                indent = true;
            }
            do
            {
                if (indent) remaining = "    " + remaining; // indent lines inside dot point lists
                string s = QcDraw.WrappedLine(ref remaining, maxWidth, colors: true, measure: measure);
                indent = dotpoint;
                line.Append('\n').Append(s);
            }
            while (remaining is not null);
            wrapped.Append(line);
            // "if the current line is indented, the following non-empty lines must be indented too"
            keepIndenting = indent && i + 1 < pars.Length && pars[i + 1].Length > 0;
        }
        return wrapped.Append("\n\n").ToString();
    }

    protected override void DrawItem(int index, Rect2 rect, bool isSelected, bool isFocused)
    {
        if (index < 0 || index >= _lines.Length) return;
        QcDraw.ColoredText(this, rect.Position + new Vector2(MenuMetrics.FontNormalU, 0f), _lines[index], Colors.White);
    }
}
