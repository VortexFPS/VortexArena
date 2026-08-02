using System.Collections.Generic;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The faithful Xonotic tab controller — C# successor to <c>makeXonoticTabController</c> +
/// <c>makeTabButton</c> (qcsrc/menu/xonotic/tabcontroller.qc). QC dialogs lay their tab buttons out as
/// full-width rows in the dialog grid (Settings uses TWO rows — Video/Effects/Audio over
/// Game/Input/User/Misc — Multiplayer one row of thirds), with the ACTIVE tab in the accent orange, and the
/// tab body sits directly on the dialog backing with NO inner frame. A Godot <see cref="TabContainer"/> gives
/// neither (compact chip tabs + a bordered content panel), so the dialogs that matter visually use this:
/// <code>
///   var tabs = new XonoticTabs();
///   tabs.AddRow();
///   tabs.AddTab("Video", new DialogSettingsVideo());
///   ...
///   tabs.Select(0);
/// </code>
/// </summary>
public partial class XonoticTabs : VBoxContainer
{
    private sealed class Entry
    {
        public Button Button = null!;
        public Control Content = null!;
        public string Title = "";
    }

    private readonly List<Entry> _entries = new();
    private readonly Control _host;
    private HBoxContainer? _currentRow;
    private int _current = -1;

    /// <summary>Index of the active tab (-1 until the first <see cref="Select"/>).</summary>
    public int Current => _current;

    /// <summary>Number of tabs added.</summary>
    public int Count => _entries.Count;

    public XonoticTabs()
    {
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 8);
        // The frameless content host: tab bodies are FullRect children toggled by visibility. QC tab content
        // draws straight onto the dialog backing — no panel stylebox here on purpose.
        _host = new Control { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(_host);
    }

    /// <summary>Start a new row of tab buttons (QC: each <c>me.TR(me)</c> of tab buttons).</summary>
    public void AddRow()
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);
        AddChild(row);
        MoveChild(_host, GetChildCount() - 1); // keep the content host below every button row
        _currentRow = row;
    }

    /// <summary>
    /// Add a tab: a button in the current row (QC <c>mc.makeTabButton(title, tab)</c>) + its content page.
    /// <paramref name="span"/> is the relative width within the row (QC column span; equal spans = equal widths).
    /// The first tab added is selected automatically.
    /// </summary>
    public void AddTab(string title, Control content, float span = 1f)
    {
        if (_currentRow is null)
            AddRow();

        title = Localization.Tr(title);
        var btn = new Button
        {
            Text = title,
            CustomMinimumSize = new Vector2(0, 34),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = span,
            FocusMode = FocusModeEnum.None,
        };

        _host.AddChild(content);
        content.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        content.Visible = false;

        var entry = new Entry { Button = btn, Content = content, Title = title };
        int index = _entries.Count;
        _entries.Add(entry);
        btn.Pressed += () => Select(index);
        _currentRow!.AddChild(btn);

        Restyle(entry, active: false);
        if (_entries.Count == 1)
            Select(0);
    }

    /// <summary>Activate tab <paramref name="index"/>: show its page, repaint the pills.</summary>
    public void Select(int index)
    {
        if (index < 0 || index >= _entries.Count)
            return;
        _current = index;
        for (int i = 0; i < _entries.Count; i++)
        {
            Entry e = _entries[i];
            e.Content.Visible = i == index;
            Restyle(e, active: i == index);
        }
    }

    /// <summary>Activate the tab titled <paramref name="title"/> (case-insensitive). False if not found.</summary>
    public bool SelectByTitle(string title)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (string.Equals(_entries[i].Title, title, System.StringComparison.OrdinalIgnoreCase))
            {
                Select(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>The title of tab <paramref name="index"/>.</summary>
    public string TitleOf(int index) => _entries[index].Title;

    /// <summary>
    /// Paint a tab pill. The showing tab is the one the QC ModalController holds in <c>forcePressed</c>
    /// (item/modalcontroller.qc:188), which in Button_draw means the CLICKED button graphic at
    /// COLOR_BUTTON_C — a white pressed pill, not a recolour. Its label stays SKINCOLOR_TEXT like every
    /// other button's: a QC tab button is a plain <c>makeXonoticButton(title, '0 0 0')</c>, and nothing in
    /// the toolkit varies a Label's colour by button state.
    /// </summary>
    private static void Restyle(Entry e, bool active)
    {
        StyleBox normal = MenuSkin.TabPill(active ? "active" : "normal");
        StyleBox hover = MenuSkin.TabPill(active ? "active" : "hover");
        e.Button.AddThemeStyleboxOverride("normal", normal);
        e.Button.AddThemeStyleboxOverride("hover", hover);
        e.Button.AddThemeStyleboxOverride("pressed", MenuSkin.TabPill("active"));
        e.Button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        foreach (string s in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
            e.Button.AddThemeColorOverride(s, MenuSkin.Text);
    }
}
