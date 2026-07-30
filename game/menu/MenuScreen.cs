using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// Convenience base for the menu screens: implements <see cref="IMenuScreen"/> and bundles the little
/// UI-construction helpers every screen reuses (styled titles, header bars, the standard button look,
/// a labelled-row helper). The QC toolkit got this uniformity from its skin system + the
/// <c>makeXonotic*</c> factory functions; here the helpers play that role.
///
/// Screens override <see cref="BuildUi"/> instead of <c>_Ready</c> so the host has already assigned
/// <see cref="Menu"/> before the tree is built (navigation is available during construction).
/// </summary>
public abstract partial class MenuScreen : Control, IMenuScreen
{
    public MenuRoot? Menu { get; set; }

    /// <summary>
    /// True when this screen is hosted inside a frame that draws its own title bar (the nexposee), so the screen
    /// should NOT add its own title label (else it doubles up). Set by the host before the screen is built; false
    /// for a normally-pushed dialog, which still draws its own title.
    /// </summary>
    public bool HostProvidesTitle { get; set; }

    // Shared palette, sourced from the Xonotic skin (luma) so every screen matches the themed widgets.
    protected static Color Accent => MenuSkin.Accent;      // the brand/selection orange
    protected static Color TextColor => MenuSkin.Text;     // cool blue-white body text

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Pass;
        BuildUi();
    }

    /// <summary>Build this screen's widget tree. Called once, from <see cref="_Ready"/>.</summary>
    protected abstract void BuildUi();

    // -------------------------------------------------------------------------------------------------
    //  Construction helpers (the C# stand-ins for makeXonoticHeaderLabel / makeXonoticButton / …)
    // -------------------------------------------------------------------------------------------------

    /// <summary>A centered dialog title (bright blue-white, bold Xolonium — the Xonotic dialog title bar).</summary>
    protected static Label MakeTitle(string text)
    {
        text = Localization.Tr(text); // i18n seam (QC: the title literal was a _() string)
        var label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.AddThemeFontSizeOverride("font_size", MenuSkin.TitleSize);
        label.AddThemeColorOverride("font_color", MenuSkin.Bright);
        if (MenuSkin.BoldFont is { } bold) label.AddThemeFontOverride("font", bold);
        return label;
    }

    /// <summary>A section header label — dim, bold blue-white (QC <c>makeXonoticHeaderLabel</c>: SKINCOLOR_HEADER).</summary>
    protected static Label MakeHeader(string text)
    {
        text = Localization.Tr(text);
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", MenuSkin.HeaderSize);
        label.AddThemeColorOverride("font_color", MenuSkin.Header);
        if (MenuSkin.BoldFont is { } bold) label.AddThemeFontOverride("font", bold);
        return label;
    }

    /// <summary>A normal body label (QC <c>makeXonoticTextLabel</c>): cool blue-white.</summary>
    protected static Label MakeLabel(string text)
    {
        text = Localization.Tr(text);
        var label = new Label { Text = text };
        label.AddThemeColorOverride("font_color", MenuSkin.Text);
        return label;
    }

    /// <summary>A standard menu button with the given press handler (QC <c>makeXonoticButton</c>).</summary>
    protected static Button MakeButton(string text, System.Action onPressed)
    {
        text = Localization.Tr(text);
        var button = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(0, 36),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        button.Pressed += onPressed;
        return button;
    }

    /// <summary>
    /// A "label : control" row in an <see cref="HBoxContainer"/> — the workhorse layout in the QC
    /// settings tabs (a <c>TextLabel</c> in column 1, the control spanning the rest).
    /// </summary>
    protected static HBoxContainer MakeRow(string labelText, Control control, float labelMinWidth = 180f)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var label = MakeLabel(labelText); // MakeLabel() already translates labelText (i18n seam)
        label.CustomMinimumSize = new Vector2(labelMinWidth, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        // Expand the control to fill the row, unless it opted into a specific size (e.g. a compact colour swatch).
        if (control.SizeFlagsHorizontal == SizeFlags.Fill)
            control.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(label);
        row.AddChild(control);
        return row;
    }

    /// <summary>
    /// A bottom bar holding right-aligned action buttons (e.g. Apply / Back). Returns the bar so the
    /// caller can append it. Mirrors the bottom-row button strip every QC dialog ends with.
    /// </summary>
    protected static HBoxContainer MakeButtonBar(params Button[] buttons)
    {
        var bar = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        bar.AddThemeConstantOverride("separation", 12);
        foreach (var b in buttons)
        {
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            bar.AddChild(b);
        }
        return bar;
    }

    /// <summary>Pop this screen off the menu stack (the universal Back). Safe if there's no host.</summary>
    protected void GoBack() => Menu?.Pop();
}
