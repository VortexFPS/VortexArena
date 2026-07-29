using Godot;
using XonoticGodot.Common.Diagnostics;

namespace XonoticGodot.Game.Menu;

/// <summary>
/// Development-release disclaimer — the modal shown over the main menu on a plain launch, telling the player
/// this is a work-in-progress build and pointing them at the Discord servers for bug reports and community.
///
/// PORT-ONLY (no Base counterpart): Xonotic ships release builds and has no such dialog. Gated on
/// <c>cl_startup_disclaimer</c> (default 1, <see cref="XonoticGodot.Common.Services.CvarFlags.Save"/>): the
/// "Don't show this again" checkbox writes 0 and OK persists it, so the next launch skips straight to the menu.
/// Re-enable from the console with <c>set cl_startup_disclaimer 1</c>.
///
/// Pushed by <see cref="Shell"/> on the plain-menu boot path only — a <c>--map</c>/<c>--host</c>/<c>--connect</c>
/// boot, the model viewer and the dev <c>--menu-screen</c> route all bypass it, so automation and CI never
/// have to dismiss it. Reachable on demand for screenshots via <c>--menu-screen disclaimer</c>.
///
/// <see cref="ISelfFramedDialog"/>: it draws its own dim backdrop + centered panel (the QuitDialog recipe), so
/// MenuRoot skips the outer frame rather than nesting a panel in a panel.
/// </summary>
public partial class DialogDisclaimer : MenuScreen, ISelfFramedDialog
{
    // Bug reports / support, and the general community server. Shown as buttons (keyboard-navigable, themed)
    // with the raw URL beneath each so it can still be copied by hand when OS.ShellOpen has no browser to hand
    // (headless, or a Linux box without xdg-open).
    private const string SupportUrl = "https://discord.gg/HnK4KfuQ2q";
    private const string CommunityUrl = "https://discord.gg/BVHvsefBqp";

    protected override void BuildUi()
    {
        Name = "DialogDisclaimer";

        // --- Self-drawn modal frame: dim backdrop, then a centered panel (mirrors QuitDialog's non-embedded
        // branch, which is the port's established "modal over the menu" shape). ---
        var dim = new ColorRect
        {
            Color = new Color(0, 0, 0, 0.55f),
            MouseFilter = MouseFilterEnum.Stop,
        };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var panel = new PanelContainer();
        center.AddChild(panel);

        var margin = new MarginContainer();
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 28);
        panel.AddChild(margin);

        var column = new VBoxContainer { CustomMinimumSize = new Vector2(620, 0) };
        column.AddThemeConstantOverride("separation", 14);
        margin.AddChild(column);

        // --- Heading ---
        column.AddChild(MakeTitle("Development Release"));

        var intro = Wrap(MakeLabel("This is a development build of Vortex Arena, not a finished game."));
        column.AddChild(intro);

        column.AddChild(Ui.Spacer());

        // --- What to expect ---
        column.AddChild(Bullet("This is a work in progress — expect rough edges."));
        column.AddChild(Bullet("Some features are incomplete, and some may be broken outright."));
        column.AddChild(Bullet("Performance, balance, and content all still change between builds."));
        column.AddChild(Bullet("If you run into something broken, please report it on our Discord."));

        column.AddChild(Ui.Spacer());

        // --- Discord ---
        column.AddChild(MakeHeader("Join us on Discord"));
        column.AddChild(Wrap(MakeLabel(
            "Bug reports and support go to the first server; general chat, news and finding games happen on the second.")));

        column.AddChild(LinkRow("Support server — report a bug or get help", SupportUrl));
        column.AddChild(LinkRow("Community server — chat and find games", CommunityUrl));

        column.AddChild(Ui.Spacer());

        // --- Dismiss ---
        // INVERTED cvar binding: the checkbox is the negative of the cvar, so checking it ("don't show this
        // again") writes cl_startup_disclaimer 0 and unchecking restores 1. CvarCheckBox matches `on` by exact
        // string when it isn't "1", which is exactly the "0"/"1" pair below, and marks the cvar archived on
        // every write — OK then flushes it to config.cfg.
        column.AddChild(Widgets.CheckBox("cl_startup_disclaimer", "Don't show this again",
            "Skip this notice on future launches (console: set cl_startup_disclaimer 1 to restore it)",
            on: "0", off: "1"));

        var ok = MakeButton("OK", OnOk);
        column.AddChild(MakeButtonBar(ok));
        // Escape is inert at the main menu (Shell ignores it outside a match), so OK is the only way out —
        // focus it so Enter/gamepad-A dismisses without reaching for the mouse.
        ok.CallDeferred(Control.MethodName.GrabFocus);
    }

    /// <summary>A "• text" list item: an untranslated bullet glyph beside a wrapped, translated body label.</summary>
    private static HBoxContainer Bullet(string text)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);

        var glyph = new Label { Text = "•", VerticalAlignment = VerticalAlignment.Top };
        glyph.AddThemeColorOverride("font_color", Accent);
        row.AddChild(glyph);

        row.AddChild(Wrap(MakeLabel(text)));
        return row;
    }

    /// <summary>A Discord entry: a themed button that opens the invite, with the raw URL beneath it.</summary>
    private static VBoxContainer LinkRow(string label, string url)
    {
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 2);

        var button = MakeButton(label, () => OpenUrl(url));
        button.TooltipText = url;
        box.AddChild(button);

        // Copy-by-hand fallback (and an honest preview of where the button goes, so the destination isn't hidden
        // behind a label). Dim + smaller so it reads as a caption rather than a second line of body text.
        var caption = new Label { Text = url, HorizontalAlignment = HorizontalAlignment.Center };
        caption.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.72f));
        caption.AddThemeFontSizeOverride("font_size", MenuSkin.BodySize - 3);
        box.AddChild(caption);

        return box;
    }

    /// <summary>Hand the invite to the OS browser. Logged so a failure to launch one is visible in the console.</summary>
    private static void OpenUrl(string url)
    {
        Log.Info($"[menu] opening {url}");
        Error err = OS.ShellOpen(url);
        if (err != Error.Ok)
            Log.Warn($"[menu] could not open {url} ({err}) — copy the address from the dialog instead.");
    }

    /// <summary>Let a body label wrap inside the fixed-width modal column instead of stretching it.</summary>
    private static Label Wrap(Label label)
    {
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return label;
    }

    // The checkbox already wrote (and archived) the cvar; persist it so the choice survives the process.
    // SaveUserConfig no-ops under the automation guards (--no-save-config / --quit-after-seconds / …), so a
    // CI run that dismisses this dialog never leaves the flag behind.
    private void OnOk()
    {
        MenuState.SaveUserConfig();
        GoBack();
    }
}
