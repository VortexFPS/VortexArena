using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// Server welcome dialog — a faithful C# port of <c>XonoticWelcomeDialog_fill</c>
/// (qcsrc/menu/xonotic/dialog_welcome.qc). On connecting to a server the engine pops this dialog showing the
/// server's name (the dialog title) and its welcome MOTD in a scrollable text box, with Join / Spectate
/// buttons that join or spectate and close.
///
/// INERT (faithful UI, no backend): the title (<c>HOSTNAME</c>) and body (<c>WELCOME</c>/<c>CAMPAIGN</c> MOTD)
/// are supplied by the server at connect time (QC <c>readInputArgs</c> from an args buffer); VortexArena has no
/// server-connect backend, so the title falls back to QC's reset string ("Welcome") and the body shows a
/// short honest placeholder. The buttons are faithful and issue the SAME console commands QC does:
///   * Join -> <c>cmd join</c> (QC <c>makeXonoticCommandButton("Join", …, "cmd join", COMMANDBUTTON_CLOSE)</c>);
///   * Spectate -> <c>cmd spectate</c> (QC <c>"cmd spectate"</c>).
/// Both close the dialog after running, matching COMMANDBUTTON_CLOSE; Enter/Space defaulting to Join (QC
/// keyDown) maps to Join being the leftmost/preferred button here.
/// </summary>
public partial class DialogWelcome : MenuScreen
{
    protected override void BuildUi()
    {
        Name = "DialogWelcome";

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 32);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        // QC: the dialog border title is the server name (serverinfo_name), default "Welcome" (resetStrings).
        root.AddChild(MakeTitle("Welcome"));

        // QC me.serverinfo_MOTD_ent = makeXonoticTextBox() (centered, allowColors, escapedNewLines). INERT:
        // server-supplied MOTD; render a scrollable placeholder body.
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        var body = MakeLabel(
            "(server welcome message — connect backend pending)\n\n" +
            "When connected to a server, its welcome message (MOTD) would appear here. " +
            "Use Join to enter the game or Spectate to watch.");
        body.AutowrapMode = TextServer.AutowrapMode.Word;
        body.HorizontalAlignment = HorizontalAlignment.Center; // QC serverinfo_MOTD_ent.align = 0.5
        body.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(body);
        root.AddChild(scroll);

        // QC bottom row: Join ('0 1 0', "cmd join", CLOSE) + Spectate ('0 0 0', "cmd spectate", CLOSE).
        // Both close after issuing the command (COMMANDBUTTON_CLOSE) -> run then GoBack.
        root.AddChild(MakeButtonBar(
            MakeButton("Join", () => RunAndClose("cmd join")),
            MakeButton("Spectate", () => RunAndClose("cmd spectate"))));
    }

    // COMMANDBUTTON_CLOSE: issue the command, then close the dialog.
    private void RunAndClose(string command)
    {
        MenuCommand.Run(command);
        GoBack();
    }
}
