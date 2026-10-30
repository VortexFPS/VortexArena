using System.Collections.Generic;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// xonotic/tabcontroller.qc for a dialog laid out on a <see cref="QcGrid"/>: <c>makeTabButton</c> returns the
/// button for the dialog to TD wherever its tab row is, and the controller itself is TD'd over the tab body.
/// The showing tab's button is held in the clicked picture (the ModalController's <c>forcePressed</c>); the
/// tab bodies are frameless, drawn straight onto the dialog.
/// </summary>
public partial class QcTabController : Control
{
    private readonly List<(Button Button, Control Content, string Title)> _tabs = new();
    private int _current = -1;

    public QcTabController()
    {
        MouseFilter = MouseFilterEnum.Pass;
    }

    public int Current => _current;

    /// <summary>The button for a new tab showing <paramref name="content"/>; the first tab added is shown.</summary>
    public Button MakeTabButton(string title, Control content, string tooltip = "")
    {
        title = Localization.Tr(title);
        var button = new Button { Text = title, FocusMode = FocusModeEnum.None, TooltipText = Localization.Tr(tooltip) };
        int index = _tabs.Count;
        QcLook.Button(button, forcePressed: () => _current == index);
        button.Pressed += () => Select(index);
        content.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        content.Visible = false;
        AddChild(content);
        _tabs.Add((button, content, title));
        if (index == 0) Select(0);
        return button;
    }

    public void Select(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        _current = index;
        for (int i = 0; i < _tabs.Count; i++)
        {
            _tabs[i].Content.Visible = i == index;
            _tabs[i].Button.QueueRedraw();
        }
    }

    /// <summary>Show the tab titled <paramref name="title"/> (case-insensitive); false if there is none.</summary>
    public bool SelectByTitle(string title)
    {
        for (int i = 0; i < _tabs.Count; i++)
            if (string.Equals(_tabs[i].Title, title, System.StringComparison.OrdinalIgnoreCase))
            {
                Select(i);
                return true;
            }
        return false;
    }
}
