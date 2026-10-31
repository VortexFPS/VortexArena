using System;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// Shared menu construction helpers — the C# stand-ins for Xonotic's <c>makeXonoticHeaderLabel</c> /
/// <c>makeXonoticTextLabel</c> / the label+control row layout the dialogs are built from. Paired with the
/// cvar-bound <see cref="Widgets"/> factory, these are all a ported dialog needs: <see cref="Header"/> /
/// <see cref="Label"/> / <see cref="Row"/> for layout, <see cref="Widgets"/> for the bound controls.
/// </summary>
public static class Ui
{
    /// <summary>Xonotic skin (luma) palette: accent orange (brand/selection) and cool blue-white body text.</summary>
    public static Color Accent => MenuSkin.Accent;
    public static Color TextColor => MenuSkin.Text;

    /// <summary>A centered dialog title (bright blue-white, bold Xolonium).</summary>
    public static Label Title(string text)
    {
        text = Localization.Tr(text); // i18n seam: every menu string is translated here (QC got it from _())
        var l = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center };
        l.AddThemeFontSizeOverride("font_size", MenuSkin.TitleSize);
        l.AddThemeColorOverride("font_color", MenuSkin.Bright);
        if (MenuSkin.TitleFont is { } bold) l.AddThemeFontOverride("font", bold);
        return l;
    }

    /// <summary>A left-aligned section header — dim, bold blue-white (QC <c>makeXonoticHeaderLabel</c>).</summary>
    public static Label Header(string text)
    {
        text = Localization.Tr(text);
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", MenuSkin.HeaderSize);
        l.AddThemeColorOverride("font_color", MenuSkin.Header);
        if (MenuSkin.BoldFont is { } bold) l.AddThemeFontOverride("font", bold);
        return l;
    }

    /// <summary>A normal body label (QC <c>makeXonoticTextLabel</c>): cool blue-white.</summary>
    public static Label Label(string text)
    {
        text = Localization.Tr(text);
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", MenuSkin.Text);
        return l;
    }

    /// <summary>A "label : control" row — the workhorse settings layout (QC a TextLabel + the control beside it).</summary>
    public static HBoxContainer Row(string labelText, Control control, float labelMinWidth = 220f)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 10);
        var label = Label(labelText); // Label() already translates labelText
        label.CustomMinimumSize = new Vector2(labelMinWidth, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        // Expand the control to fill the row, unless it opted into a specific size (e.g. a compact colour swatch).
        if (control.SizeFlagsHorizontal == Control.SizeFlags.Fill)
            control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(label);
        row.AddChild(control);
        return row;
    }

    /// <summary>A standard menu button with a press handler (QC <c>makeXonoticButton</c>).</summary>
    public static Button Button(string text, Action onPressed)
    {
        text = Localization.Tr(text);
        var b = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(0, 36),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        b.Pressed += onPressed;
        return b;
    }

    /// <summary>A right-aligned bar of action buttons (Apply / Back / …) every dialog ends with.</summary>
    public static HBoxContainer ButtonBar(params Button[] buttons)
    {
        var bar = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        bar.AddThemeConstantOverride("separation", 12);
        foreach (var b in buttons)
        {
            b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            bar.AddChild(b);
        }
        return bar;
    }

    /// <summary>A fixed-height vertical spacer (the QC empty rows that separate groups).</summary>
    public static Control Spacer(float height = 12f) => new() { CustomMinimumSize = new Vector2(0, height) };
}
